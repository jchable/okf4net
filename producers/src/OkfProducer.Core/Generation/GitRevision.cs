// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace OkfProducer.Core.Generation;

/// <summary>
/// Reads the HEAD commit's identity straight out of <c>git</c>, so <c>overview</c>'s
/// <c>generated.at</c> and <c>revision</c> (§6.1) stamp <b>which state of the code a bundle
/// reflects</b> rather than when the command happened to run.
///
/// <para><b>Why not the wall clock.</b> <c>--check</c> (a later task) regenerates and compares the
/// bundle byte for byte; a wall-clock <c>at</c> would make that comparison fail forever on this one
/// field, which is exactly the guard that stops a stale bundle from shipping silently. Reading it off
/// the HEAD commit instead means a source-identical run is byte-identical too, and the stamp becomes
/// <i>more</i> informative -- it stops saying "what time the command ran" (which git already records
/// at the bundle's own commit) and starts saying which commit this bundle was generated from.</para>
///
/// <para><b>Outside a git repository</b> -- <paramref name="repoRoot"/> is not one, or <c>git</c> could
/// not be run at all -- <see cref="HeadCommitInstant"/> falls back to the current wall-clock instant
/// and <see cref="HeadSha"/> returns <see langword="null"/>: there is no commit to report, so nothing
/// is fabricated. A later task's <c>--check</c> excludes both fields from its byte-for-byte comparison
/// in exactly that case, since a wall-clock value cannot be reproduced by regenerating.</para>
///
/// <para><b>A dirty working tree is read as HEAD, not as its own state.</b> Both values name the commit
/// currently checked out, regardless of any uncommitted change to the source this run actually scanned
/// -- so <c>revision</c> can name a commit the bundle was not, byte for byte, generated from. "Which
/// state of the code this bundle reflects" above is therefore a claim about the committed HEAD, not
/// about the working tree at generation time; a repository with real, uncommitted local edits is the
/// one case that claim overstates.</para>
/// </summary>
public static class GitRevision
{
    /// <summary>How long one <c>git</c> invocation may take before it is abandoned and the outside-git fallback applies.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The HEAD commit's <i>committer</i> date, normalized to UTC and formatted as the §5-conformant
    /// <c>yyyy-MM-ddTHH:mm:ssZ</c> -- an explicit UTC offset, never a bare date. Falls back to the
    /// current wall-clock instant, in the same format, when <paramref name="repoRoot"/> is not inside a
    /// git repository or <c>git</c> itself could not be run.
    /// </summary>
    /// <param name="repoRoot">The repository root to run <c>git</c> in.</param>
    public static string HeadCommitInstant(string repoRoot)
    {
        var raw = RunGit(repoRoot, "show", "-s", "--format=%cI", "HEAD");

        // `%cI` is git's own strict ISO-8601 committer date, e.g. `2026-08-31T14:32:10+02:00` -- always
        // carrying an explicit offset already, so nothing here can silently produce the non-conformant
        // bare-date form §5 forbids.
        if (raw is { Length: > 0 }
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var committed))
        {
            return FormatUtc(committed.ToUniversalTime());
        }

        return FormatUtc(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The HEAD commit's full sha, or <see langword="null"/> outside a git repository (or when
    /// <c>git</c> itself could not be run).
    ///
    /// <para><b>Not the same thing as a <c>--rev</c>.</b> A <c>--rev</c> (a later task's CLI flag) names
    /// a branch, and the permalink URL built from it is deliberately stable as the branch moves; this
    /// sha names the exact commit the bundle was generated from, and is deliberately precise. Conflating
    /// the two would make <c>revision</c> drift out of sync with the commit whenever the branch does.</para>
    /// </summary>
    /// <param name="repoRoot">The repository root to run <c>git</c> in.</param>
    public static string? HeadSha(string repoRoot) => RunGit(repoRoot, "rev-parse", "HEAD");

    /// <summary>
    /// The name of the branch currently checked out in <paramref name="repoRoot"/> (e.g. <c>main</c>,
    /// <c>feature/x</c>), or <see langword="null"/> when there is none to read: a <b>detached
    /// HEAD</b>, a path outside any git repository, or <c>git</c> itself not runnable.
    ///
    /// <para><b>What the null is for.</b> This is the default the CLI's <c>--rev</c> falls back to
    /// when building §4.3's <c>resource</c> permalinks, and a branch name is deliberately the only
    /// thing that can serve: it is stable as the branch moves, so a code concept's <c>resource</c>
    /// survives the next commit unchanged. On a detached HEAD there is no such name, and the tempting
    /// substitute -- <see cref="HeadSha"/> -- is exactly the wrong one: a sha would rewrite the
    /// <c>resource</c> of every code concept on every commit. So the null propagates, no
    /// <c>resource</c> is emitted at all, and <c>--rev</c> becomes required for permalinks there.</para>
    ///
    /// <para><c>symbolic-ref</c>, not <c>rev-parse --abbrev-ref HEAD</c>: the latter answers the
    /// literal string <c>HEAD</c> on a detached HEAD, which parses as a branch name and would silently
    /// build permalinks pointing at whatever <c>HEAD</c> means to the forge when the link is followed.
    /// <c>symbolic-ref</c> exits non-zero instead, which is the honest answer and the one that reaches
    /// the caller as a null.</para>
    /// </summary>
    /// <param name="repoRoot">The repository root to run <c>git</c> in.</param>
    public static string? CurrentBranch(string repoRoot) =>
        RunGit(repoRoot, "symbolic-ref", "--quiet", "--short", "HEAD") is { Length: > 0 } branch ? branch : null;

    /// <summary>Formats <paramref name="instant"/>, taken as UTC, as <c>yyyy-MM-ddTHH:mm:ssZ</c> -- invariant, second precision, a literal <c>Z</c>.</summary>
    private static string FormatUtc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// Runs <c>git &lt;arguments&gt;</c> in <paramref name="repoRoot"/> and returns its trimmed stdout,
    /// or <see langword="null"/> on any failure whatsoever -- no directory, no <c>git</c> binary, a
    /// non-zero exit (not a repository, no commits yet, a detached worktree with no HEAD), or a timeout.
    /// Every one of those collapses to the single "outside a git repository" fallback callers use; a
    /// caller that needs to tell them apart has no use for this type today.
    /// </summary>
    private static string? RunGit(string repoRoot, params string[] arguments)
    {
        // Guards Process.Start against a working directory that does not exist at all -- the common
        // case in this codebase's own tests, which pass a fixture path like `/repo` that names nothing
        // on disk. Without this check that throws a platform-specific exception instead of politely
        // reporting "not a git repository".
        if (!Directory.Exists(repoRoot))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = repoRoot,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new Win32Exception();
        }
        catch (Win32Exception)
        {
            // `git` is not on PATH at all -- folded into the same fallback as "not a repository" rather
            // than a distinct failure mode, since both leave this run with nothing to stamp.
            return null;
        }

        using (process)
        {
            using var cts = new CancellationTokenSource(Timeout);

            // Both streams drained concurrently, never one ReadToEnd() after the other: a filled pipe
            // buffer on either side would otherwise deadlock a process that is blocked writing to it.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            var exitTask = process.WaitForExitAsync(cts.Token);

            string stdout;
            try
            {
                // Bounded TWICE, deliberately. `cts` is the fast path and works in the ordinary case,
                // but cancelling a synchronous pipe read is not guaranteed to interrupt it -- the same
                // gap `MsBuildProjectQuery` (OkfProducer.CodeGraph.Roslyn) closed for the same reason.
                // `WaitAsync(Timeout)` throws once the timeout elapses regardless of whether the
                // awaited tasks ever observe their own cancellation, so this call returns on time
                // either way; a read left stuck is abandoned to complete on its own once TryKill below
                // closes the pipe, but nothing here blocks on it any longer.
                Task.WhenAll(exitTask, stdoutTask, stderrTask).WaitAsync(Timeout).GetAwaiter().GetResult();
                stdout = stdoutTask.Result;
            }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException)
            {
                TryKill(process);
                return null;
            }

            return process.ExitCode == 0 ? stdout.Trim() : null;
        }
    }

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
            // Access denied killing the tree; left to the OS rather than failing the run twice.
        }
    }
}
