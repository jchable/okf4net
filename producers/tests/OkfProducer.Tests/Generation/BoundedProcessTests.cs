// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using OkfProducer.Core.Generation;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// <see cref="BoundedProcess"/>, the one child-process runner behind <c>GitRevision.RunGit</c> and
/// <c>MsBuildProjectQuery.Run</c> (E11), exercised against real children rather than described.
///
/// <para><b>The children are the host's own shell</b> (<c>cmd.exe</c> on Windows, <c>/bin/sh</c>
/// elsewhere), each given a fixed command with no path or quote inside it: every file a child touches
/// is named relative to the working directory the test hands it, so no argument needs quoting beyond
/// what <see cref="ProcessStartInfo.ArgumentList"/> already does. Nothing here mutates process-wide
/// state, so the class runs in parallel with the rest of the suite; the timing bounds below are
/// therefore loose on purpose -- each asserts "returned well before the child would have finished",
/// never a tight wall-clock figure.</para>
/// </summary>
public class BoundedProcessTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    [Fact]
    public void A_child_that_prints_and_exits_is_Completed_with_its_stdout_stderr_and_exit_code()
    {
        using var dir = new TempDir();

        var result = Shell(dir.Path, "echo hello& echo oops 1>&2& exit 3", "echo hello; echo oops 1>&2; exit 3", Generous);

        Assert.Equal(BoundedOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("hello", result.Stdout.Trim());
        Assert.Equal("oops", result.Stderr.Trim());
        Assert.False(result.StdoutOverflowed);
        Assert.False(result.StderrOverflowed);
        Assert.Null(result.Exception);
    }

    [Fact]
    public void A_nonexistent_executable_is_NotStarted_and_carries_the_start_failure()
    {
        using var dir = new TempDir();

        var result = BoundedProcess.Run(
            "okfgen-no-such-executable-" + Guid.NewGuid().ToString("N"), [], dir.Path, Generous, 1024, 1024);

        Assert.Equal(BoundedOutcome.NotStarted, result.Outcome);
        Assert.IsType<System.ComponentModel.Win32Exception>(result.Exception);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public void A_child_that_outlives_the_timeout_is_TimedOut_returns_early_and_is_killed()
    {
        // The child waits ~5 s and only then writes its marker. Returning well before 5 s proves the
        // runner did not wait for the exit; the marker still being absent after the child's own
        // deadline has passed proves it was killed rather than merely abandoned.
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "marker.txt");

        var clock = Stopwatch.StartNew();
        var result = Shell(
            dir.Path,
            "ping -n 6 127.0.0.1 >nul& echo done> marker.txt",
            "sleep 5; echo done > marker.txt",
            TimeSpan.FromMilliseconds(300));
        var elapsed = clock.Elapsed;

        Assert.Equal(BoundedOutcome.TimedOut, result.Outcome);
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"returned after {elapsed}, not near the 300 ms timeout.");

        Thread.Sleep(TimeSpan.FromSeconds(7) - elapsed);
        Assert.False(File.Exists(marker), "the timed-out child ran to completion: it was not killed.");
    }

    [Fact]
    public void A_child_that_exits_while_a_grandchild_holds_its_pipes_is_still_bounded_by_the_timeout()
    {
        // The child exits at once, but a background grandchild inherits its stdout and keeps it open for
        // ~5 s -- the shape an inherited MSBuild worker node takes. The exit wait alone is satisfied
        // immediately; only a bound on the READS stops the call from blocking until the grandchild lets
        // go. This pins that bound, not the mechanism providing it. Measured on Windows / .NET 10 by
        // mutation: removing BoundedProcess's WaitAsync left this green (the reads' cancellation token
        // sufficed there); removing the token from the reads as well made it fail after ~5 s.
        using var dir = new TempDir();

        var clock = Stopwatch.StartNew();
        var result = Shell(
            dir.Path,
            "start /b ping -n 6 127.0.0.1",
            "sleep 5 &",
            TimeSpan.FromMilliseconds(500));
        var elapsed = clock.Elapsed;

        Assert.Equal(BoundedOutcome.TimedOut, result.Outcome);
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"returned after {elapsed}: the reads were not bounded by the timeout.");
    }

    [Fact]
    public void Stdout_past_its_cap_is_drained_and_reported_as_overflowed_without_hanging()
    {
        // 2 MiB through a 64 KiB cap. Stopping at the cap would leave the child blocked on a full pipe
        // until the timeout; draining past it lets the child exit normally.
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "big.txt"), new string('x', 2 * 1024 * 1024));

        var result = Shell(dir.Path, "type big.txt", "cat big.txt", Generous, maxStdoutChars: 64 * 1024);

        Assert.Equal(BoundedOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StdoutOverflowed);
        Assert.Equal(64 * 1024, result.Stdout.Length);
        Assert.False(result.StderrOverflowed);
    }

    [Fact]
    public void Stderr_past_its_cap_is_drained_and_reported_as_overflowed_independently()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "big.txt"), new string('x', 2 * 1024 * 1024));

        var result = Shell(dir.Path, "type big.txt 1>&2", "cat big.txt 1>&2", Generous, maxStderrChars: 64 * 1024);

        Assert.Equal(BoundedOutcome.Completed, result.Outcome);
        Assert.True(result.StderrOverflowed);
        Assert.Equal(64 * 1024, result.Stderr.Length);
        Assert.False(result.StdoutOverflowed);
        Assert.Equal(string.Empty, result.Stdout);
    }

    [Fact]
    public void A_child_that_reads_stdin_gets_EOF_immediately()
    {
        // `sort` / `cat` with no file argument read stdin to its end. With stdin redirected and closed
        // they see EOF at once and exit 0 having printed nothing; left inheriting a live stdin they
        // would block until the (generous) timeout instead.
        using var dir = new TempDir();
        var (executable, arguments) = OperatingSystem.IsWindows()
            ? (Path.Combine(Environment.SystemDirectory, "sort.exe"), Array.Empty<string>())
            : ("/bin/cat", Array.Empty<string>());

        var clock = Stopwatch.StartNew();
        var result = BoundedProcess.Run(executable, arguments, dir.Path, TimeSpan.FromSeconds(30), 1024, 1024);

        Assert.Equal(BoundedOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout.Trim());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}: stdin was not at EOF.");
    }

    [Fact]
    public void Concurrent_runs_share_no_state()
    {
        // E10b will call MsBuildProjectQuery.Query -- and so this runner -- from several threads at once.
        // Eight simultaneous children, each echoing its own index: every result must carry exactly its
        // own child's output, which a shared buffer, token or process field would cross.
        using var dir = new TempDir();

        var results = new BoundedResult[8];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = results.Length }, i =>
        {
            results[i] = Shell(dir.Path, $"echo run-{i}", $"echo run-{i}", Generous);
        });

        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(BoundedOutcome.Completed, results[i].Outcome);
            Assert.Equal(0, results[i].ExitCode);
            Assert.Equal($"run-{i}", results[i].Stdout.Trim());
        }
    }

    private static BoundedResult Shell(
        string workingDirectory,
        string windowsCommand,
        string posixCommand,
        TimeSpan timeout,
        int maxStdoutChars = 1024 * 1024,
        int maxStderrChars = 1024 * 1024) =>
        OperatingSystem.IsWindows()
            ? BoundedProcess.Run(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                ["/d", "/s", "/c", windowsCommand],
                workingDirectory,
                timeout,
                maxStdoutChars,
                maxStderrChars)
            : BoundedProcess.Run("/bin/sh", ["-c", posixCommand], workingDirectory, timeout, maxStdoutChars, maxStderrChars);

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "okfproducer-boundedprocess-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
