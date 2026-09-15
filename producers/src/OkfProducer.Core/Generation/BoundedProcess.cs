// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace OkfProducer.Core.Generation;

/// <summary>How one <see cref="BoundedProcess.Run"/> call ended.</summary>
internal enum BoundedOutcome
{
    /// <summary>
    /// The process exited and both of its output streams were read to their end within the timeout.
    /// <see cref="BoundedResult.ExitCode"/> is the process's own exit code; a non-zero one is still
    /// <see cref="Completed"/> -- what a non-zero exit means is the caller's business.
    /// </summary>
    Completed,

    /// <summary>
    /// <see cref="Process.Start(ProcessStartInfo)"/> threw <see cref="Win32Exception"/> (no such
    /// executable, a missing working directory, a binary the OS refuses to launch) or returned
    /// <see langword="null"/>. Nothing ran. <see cref="BoundedResult.Exception"/> carries the
    /// <see cref="Win32Exception"/> when there was one.
    /// </summary>
    NotStarted,

    /// <summary>
    /// The exit, or the end of either stream, had not been reached when the timeout elapsed, and the
    /// call returned on time regardless. That includes a process that itself exited in time while
    /// something it started still held its output pipes open. A kill of the process tree was attempted,
    /// best effort -- see <see cref="BoundedProcess"/> for exactly which descendants that reaches on each
    /// platform, which is not all of them.
    /// </summary>
    TimedOut,

    /// <summary>
    /// Waiting for the exit or reading a stream threw <see cref="IOException"/>,
    /// <see cref="ObjectDisposedException"/> or <see cref="InvalidOperationException"/> -- a pipe torn
    /// down abnormally, or the process object not in the state the read required. The process tree was
    /// killed, best effort, and <see cref="BoundedResult.Exception"/> carries what was thrown.
    /// </summary>
    Faulted,
}

/// <summary>What one <see cref="BoundedProcess.Run"/> call produced.</summary>
/// <param name="Outcome">How the call ended; every other field is meaningful only as that outcome says.</param>
/// <param name="ExitCode">The process's exit code when <paramref name="Outcome"/> is <see cref="BoundedOutcome.Completed"/>; <c>-1</c> otherwise.</param>
/// <param name="Stdout">The stdout characters kept, up to the caller's cap; empty unless <see cref="BoundedOutcome.Completed"/>.</param>
/// <param name="StdoutOverflowed">Whether stdout held more than the cap and the rest was drained and discarded.</param>
/// <param name="Stderr">The stderr characters kept, up to the caller's cap; empty unless <see cref="BoundedOutcome.Completed"/>.</param>
/// <param name="StderrOverflowed">Whether stderr held more than the cap and the rest was drained and discarded.</param>
/// <param name="Exception">What was thrown, for <see cref="BoundedOutcome.NotStarted"/> (when there was an exception) and <see cref="BoundedOutcome.Faulted"/>; otherwise <see langword="null"/>.</param>
internal sealed record BoundedResult(
    BoundedOutcome Outcome,
    int ExitCode,
    string Stdout,
    bool StdoutOverflowed,
    string Stderr,
    bool StderrOverflowed,
    Exception? Exception);

/// <summary>
/// Runs one child process to completion under a hard deadline and a hard memory bound, and says how it
/// ended rather than throwing -- the one implementation behind <c>GitRevision.RunGit</c> and
/// <c>MsBuildProjectQuery.Run</c> (<c>OkfProducer.CodeGraph.Roslyn</c>), which used to carry a copy each
/// and had drifted apart.
///
/// <para><b>What it owns, and why each is here.</b></para>
/// <list type="bullet">
/// <item><description><b>Arguments go through <see cref="ProcessStartInfo.ArgumentList"/></b>, never a
/// concatenated string, so no argument can be re-split or re-quoted on the way to the child.</description></item>
/// <item><description><b>Stdin is redirected and closed immediately</b>, never written to. Without
/// redirecting it the child inherits whatever this process's own stdin is -- a live console under an
/// interactive terminal -- and a child that unexpectedly blocks reading it (a corrupted binary, or the
/// wrong executable entirely) would hang until the timeout rather than failing fast. Redirecting to a
/// pipe and closing our end hands it immediate EOF. (E2 put this in <c>RunGit</c>; the MSBuild copy
/// never had it.)</description></item>
/// <item><description><b>Both output streams are drained concurrently</b>, never one after the other: a
/// filled pipe buffer on the stream read second deadlocks a child blocked writing to it.</description></item>
/// <item><description><b>Both are capped, and drained past the cap.</b> What a child prints can be
/// controlled by the scanned repository (see <c>MsBuildProjectQuery</c>'s cap constants for the
/// measurement), so an unbounded read is an <see cref="OutOfMemoryException"/> it can ask for; simply
/// stopping at the cap would instead leave the child blocked on a full pipe until the timeout. Past
/// the cap, characters are read and discarded and the overflow is reported.</description></item>
/// <item><description><b>The deadline is enforced twice.</b> A <see cref="CancellationTokenSource"/>
/// cancels the exit wait and the reads, which is the fast path. The combined wait is also bounded by
/// <see cref="Task.WaitAsync(TimeSpan)"/>, which returns once the timeout elapses whether or not the
/// awaited tasks ever observe their own cancellation -- insurance for a stream whose read does not
/// honour its token, which .NET does not promise for every pipe on every platform. Before this type
/// only the <c>git</c> copy had that second bound; the MSBuild copy relied on the token alone.
/// MEASURED on Windows / .NET 10 (E11): the token alone was enough there -- with
/// <c>WaitAsync</c> removed, <c>BoundedProcessTests</c>' grandchild-holds-the-pipe case still returned
/// on time, and it hung for the grandchild's full lifetime only once the reads were ALSO given no
/// token. The E11 review measured the same on Linux (<c>mcr.microsoft.com/dotnet/sdk:10.0</c>). So the
/// second bound is belt and braces, not a fix for an observed hang. A read left stuck is abandoned: it
/// completes on its own once the pipe closes, and its eventual fault is observed so it cannot surface
/// as an unobserved task exception.</description></item>
/// <item><description><b>A timed-out or faulted child is killed with its process tree as it stands
/// when the call gives up</b> (<see cref="Process.Kill(bool)"/> with <c>entireProcessTree: true</c>),
/// and a failure to kill is swallowed: every caller is about to report its own, more useful failure,
/// and an exception escaping here would replace it. <b>What that reaches differs by platform</b>, and
/// the guarantee is only this. On both, a descendant of a child that is still running when the call
/// gives up is killed with it (<c>BoundedProcessTests</c> pins a grandchild of a live child). On POSIX,
/// a descendant whose own parent has ALREADY exited has been re-parented away from the tree and is not
/// reached: measured by the E11 review on Linux, a child that exits at once leaving
/// <c>(sleep 3; echo g &gt; g.txt) &amp;</c> holding its stdout makes the call return
/// <see cref="BoundedOutcome.TimedOut"/> on time, and the grandchild survives to write its file. On
/// Windows the same shape (<c>start /b</c>) was measured killed. So on POSIX the call is bounded but a
/// pipe-holding orphan may outlive it; this predates the runner, both former copies behaved the
/// same.</description></item>
/// </list>
///
/// <para><b>What it deliberately does not own.</b> It does not resolve the executable:
/// the name is handed to <see cref="ProcessStartInfo"/> as given, so a caller that must not let
/// Windows search the current directory passes an absolute path (as <c>GitRevision</c> does, via
/// <c>ResolveGitExecutable</c>). It does not check the working directory exists: a missing
/// one fails <see cref="Process.Start(ProcessStartInfo)"/> with a <see cref="Win32Exception"/>
/// indistinguishable from a missing executable, so callers that need to say which guard it
/// themselves, with their own message. It does not set the pipes' encoding: both callers decoded with
/// the platform default before this type existed, and changing that here would change how a
/// non-ASCII answer reads.</para>
///
/// <para><b>Safe for concurrent use.</b> It holds no shared mutable state -- no static buffer, no
/// cached process or token; every buffer, token and task belongs to a single call -- so any number of
/// calls may run at once on different threads.</para>
/// </summary>
internal static class BoundedProcess
{
    /// <summary>
    /// Starts <paramref name="executable"/> with <paramref name="arguments"/> in
    /// <paramref name="workingDirectory"/> and waits, at most <paramref name="timeout"/>, for it to exit
    /// and for both of its output streams to end.
    /// </summary>
    /// <param name="executable">The program to start, handed to <see cref="ProcessStartInfo"/> unresolved.</param>
    /// <param name="arguments">Each element becomes exactly one argument.</param>
    /// <param name="workingDirectory">The child's working directory; not checked for existence here.</param>
    /// <param name="timeout">The bound on the whole call from start to the end of both streams.</param>
    /// <param name="maxStdoutChars">How many stdout characters are kept; the rest is drained and discarded.</param>
    /// <param name="maxStderrChars">How many stderr characters are kept; the rest is drained and discarded.</param>
    /// <returns>The outcome; never <see langword="null"/>.</returns>
    internal static BoundedResult Run(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int maxStdoutChars,
        int maxStderrChars)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(maxStdoutChars);
        ArgumentOutOfRangeException.ThrowIfNegative(maxStderrChars);

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = workingDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? started;
        try
        {
            started = Process.Start(startInfo);
        }
        catch (Win32Exception e)
        {
            return Ended(BoundedOutcome.NotStarted, e);
        }

        if (started is null)
        {
            return Ended(BoundedOutcome.NotStarted, exception: null);
        }

        using var process = started;

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child already went away and took the pipe with it; there is no one left to hand EOF to.
        }

        using var deadline = new CancellationTokenSource(timeout);

        var stdoutTask = ReadCappedAsync(process.StandardOutput, maxStdoutChars, deadline.Token);
        var stderrTask = ReadCappedAsync(process.StandardError, maxStderrChars, deadline.Token);
        var exitTask = process.WaitForExitAsync(deadline.Token);

        try
        {
            Task.WhenAll(exitTask, stdoutTask, stderrTask).WaitAsync(timeout).GetAwaiter().GetResult();

            var stdout = stdoutTask.Result;
            var stderr = stderrTask.Result;
            return new BoundedResult(
                BoundedOutcome.Completed, process.ExitCode, stdout.Text, stdout.Overflowed, stderr.Text, stderr.Overflowed, Exception: null);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
            Abandon(process, exitTask, stdoutTask, stderrTask);
            return Ended(BoundedOutcome.TimedOut, exception: null);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Abandon(process, exitTask, stdoutTask, stderrTask);
            return Ended(BoundedOutcome.Faulted, e);
        }
    }

    private static BoundedResult Ended(BoundedOutcome outcome, Exception? exception) =>
        new(outcome, ExitCode: -1, string.Empty, StdoutOverflowed: false, string.Empty, StderrOverflowed: false, exception);

    /// <summary>Kills the tree and makes sure any task still running on it cannot fault unobserved later.</summary>
    private static void Abandon(Process process, params Task[] tasks)
    {
        TryKill(process);

        foreach (var task in tasks)
        {
            _ = task.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>What one capped stream read produced.</summary>
    /// <param name="Text">The characters kept, up to the cap.</param>
    /// <param name="Overflowed">Whether the stream held more than the cap and the rest was discarded.</param>
    private readonly record struct CappedRead(string Text, bool Overflowed);

    /// <summary>
    /// Reads <paramref name="reader"/> to the end, keeping at most <paramref name="maxChars"/>
    /// characters and discarding -- but still draining -- anything past that. Draining past the cap is
    /// the point, not a detail: stopping would leave the child blocked on a full pipe until the timeout
    /// killed it, where discarding keeps it moving to its own exit while memory stays bounded.
    /// </summary>
    private static async Task<CappedRead> ReadCappedAsync(StreamReader reader, int maxChars, CancellationToken token)
    {
        var buffer = new char[8192];
        var kept = new StringBuilder();
        var overflowed = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var room = maxChars - kept.Length;
            if (room >= read)
            {
                kept.Append(buffer, 0, read);
                continue;
            }

            if (room > 0)
            {
                kept.Append(buffer, 0, room);
            }

            overflowed = true;
        }

        return new CappedRead(kept.ToString(), overflowed);
    }

    /// <summary>
    /// Kills the process and its descendants, or gives up quietly.
    ///
    /// <para>
    /// Every caller is about to report its own failure, so anything escaping here would replace that
    /// with a raw exception the caller does not catch. <see cref="Process.Kill(bool)"/> with
    /// <c>entireProcessTree: true</c> is documented to throw <see cref="AggregateException"/> when part
    /// of the tree could not be killed -- which the <c>git</c> copy this replaced did not catch.
    /// </para>
    ///
    /// <para>
    /// NOT MEASURED: the <see cref="AggregateException"/> branch is read-verified against the documented
    /// contract only. Arranging a process tree whose partial kill fails is not something a test can do
    /// deterministically on this host.
    /// </para>
    /// </summary>
    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the timeout and here; nothing to kill.
        }
        catch (Win32Exception)
        {
            // Access denied killing the tree; left to the OS rather than failing the caller twice.
        }
        catch (AggregateException)
        {
            // Part of the tree survived. Same answer as access denied: the survivors are left to the OS.
        }
    }
}
