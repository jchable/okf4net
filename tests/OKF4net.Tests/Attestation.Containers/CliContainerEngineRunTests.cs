// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using OKF4net.Attestation.Containers;

namespace OKF4net.Tests.Attestation.Containers;

/// <summary>
/// The half of <see cref="CliContainerEngine"/> that spawns a process, exercised
/// with no container engine at all: the "binary" is either absent or a throwaway
/// script that hangs, or writes fixed bytes, whatever it is asked. That is enough to
/// pin the two teardown properties a real engine cannot be made to demonstrate on
/// demand, which decoder each output stream is wired to, and the output-cap and
/// UTF-8 accounting the real-Docker integration tests then confirm end to end.
/// </summary>
public class CliContainerEngineRunTests
{
    private static ContainerRunSpec Spec(TimeSpan? timeout) => new(
        Image: "python:3.12-slim",
        Command: ["python3", "-"],
        Stdin: null,
        Environment: new Dictionary<string, string>(),
        NetworkMode: "none",
        MemoryBytes: 64L * 1024 * 1024,
        Cpus: 0.5,
        PidsLimit: 16,
        Timeout: timeout);

    public static TheoryData<TimeSpan> UnenforceableTimeouts => new()
    {
        System.Threading.Timeout.InfiniteTimeSpan,
        TimeSpan.Zero,
        TimeSpan.FromSeconds(-5),
        TimeSpan.MaxValue,
    };

    /// <summary>
    /// <see cref="ContainerRunSpec"/> is public, so a host can hand the engine a
    /// Timeout the profile would have refused. <c>Timeout.InfiniteTimeSpan</c> is the
    /// dangerous one: a <see cref="CancellationTokenSource"/> accepts it and never
    /// fires, so the wall-clock ceiling is gone while looking set. The rejection must
    /// also come BEFORE the engine process starts — the binary here does not exist, so
    /// an engine that validated after <c>Start()</c> would surface "could not be
    /// started" instead, and a real one would have left a container running.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnenforceableTimeouts))]
    public async Task An_unenforceable_timeout_is_rejected_before_any_process_is_started(TimeSpan timeout)
    {
        var engine = new CliContainerEngine("okf-no-such-engine-binary");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await engine.RunAsync(Spec(timeout)));
    }

    /// <summary>
    /// The 8 Mi-character cap is documented as a stage failure, and the first version
    /// truncated silently instead: a container could flood stderr without limit and
    /// be accepted, and a cut-off stdout prefix went on to receipt parsing as if it
    /// were complete. The reader has to say when it dropped something, or
    /// <see cref="CliContainerEngine.RunAsync"/> cannot fail the stage.
    /// </summary>
    [Fact]
    public async Task The_bounded_reader_reports_when_it_dropped_output()
    {
        var kept = await CliContainerEngine.ReadBoundedAsync(StreamOver(new string('x', 10)), CliContainerEngine.StrictUtf8, maxChars: 10);
        Assert.Equal(10, kept.Text.Length);
        Assert.False(kept.Truncated);
        Assert.False(kept.InvalidBytes);

        var dropped = await CliContainerEngine.ReadBoundedAsync(StreamOver(new string('x', 11)), CliContainerEngine.StrictUtf8, maxChars: 10);
        Assert.Equal(10, dropped.Text.Length);
        Assert.True(dropped.Truncated);
        Assert.False(dropped.InvalidBytes);
    }

    /// <summary>
    /// stdout is the receipt an attester authenticates, and the first version decoded
    /// it with a replacement fallback, so a stray <c>0xFF</c> became U+FFFD in a receipt
    /// no script produced. The strict reader has to say the bytes were invalid — and
    /// has to keep reading after saying so. A reader that stopped at the bad byte would
    /// leave the child blocked on a full pipe (64 KiB on Linux), turning this failure
    /// into a timeout; hence well over 64 KiB after the bad byte, and the check that
    /// the stream was consumed to its end.
    /// </summary>
    [Fact]
    public async Task Invalid_utf8_is_reported_and_the_rest_of_the_stream_is_still_drained()
    {
        // One whole read of valid text first, so the invalid byte lands in the second
        // read: what the first read decoded is kept, and must come back exactly — no
        // replacement character in it — while the second read, bad byte and all, is lost.
        var completedRead = new string('b', CliContainerEngine.ReadBufferBytes);
        var bytes = new List<byte>(Encoding.UTF8.GetBytes(completedRead + "{\"x\":\""));
        bytes.Add(0xFF);
        bytes.AddRange(Encoding.UTF8.GetBytes(new string('a', 100 * 1024)));
        using var stream = new MemoryStream(bytes.ToArray());

        var read = await CliContainerEngine.ReadBoundedAsync(stream, CliContainerEngine.StrictUtf8, maxChars: 8 * 1024 * 1024);

        Assert.True(read.InvalidBytes);
        Assert.False(read.Truncated);
        Assert.Equal(stream.Length, stream.Position);
        Assert.Equal(completedRead, read.Text);
    }

    /// <summary>
    /// A multi-byte sequence cut off at the end of the stream is invalid too. The
    /// decoder holds such bytes back waiting for the rest, so a reader that never
    /// flushed it at end of stream would silently drop them and call the output valid.
    /// </summary>
    [Fact]
    public async Task A_multibyte_sequence_cut_off_at_end_of_stream_is_invalid()
    {
        using var stream = new MemoryStream([(byte)'a', (byte)'b', 0xE2, 0x82]);

        var read = await CliContainerEngine.ReadBoundedAsync(stream, CliContainerEngine.StrictUtf8, maxChars: 1024);

        Assert.True(read.InvalidBytes);
    }

    public static TheoryData<int> BytesBeforeTheBoundary => new() { 1, 2, 3 };

    /// <summary>
    /// The strict decoder must not mistake a valid sequence that straddles two reads
    /// for an invalid one: a reader that decoded each buffer on its own
    /// (<c>Encoding.GetString</c> per read) would reject every character the buffer
    /// boundary cuts. A 4-byte character is placed so the boundary cuts it after 1, 2
    /// and 3 bytes, with a 3-byte character right behind it.
    /// </summary>
    [Theory]
    [MemberData(nameof(BytesBeforeTheBoundary))]
    public async Task Valid_multibyte_utf8_split_across_the_read_buffer_boundary_decodes_exactly(int bytesBeforeBoundary)
    {
        var expected = new string('a', CliContainerEngine.ReadBufferBytes - bytesBeforeBoundary) + "\U0001F600\u20AC tail";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(expected));

        var read = await CliContainerEngine.ReadBoundedAsync(stream, CliContainerEngine.StrictUtf8, maxChars: 1024 * 1024);

        Assert.False(read.InvalidBytes);
        Assert.Equal(expected, read.Text);
    }

    /// <summary>
    /// Reading raw bytes instead of through the <see cref="StreamReader"/> the process
    /// hands out must not change what a valid receipt parses as: that reader skipped a
    /// leading UTF-8 byte-order mark, and <c>JsonDocument.Parse</c> rejects a leading
    /// U+FEFF, so a script whose receipt starts with one would start failing. Only the
    /// leading one is skipped; a U+FEFF anywhere else is content.
    /// </summary>
    [Fact]
    public async Task A_leading_utf8_byte_order_mark_is_skipped_as_before()
    {
        using var stream = new MemoryStream([0xEF, 0xBB, 0xBF, (byte)'{', 0xEF, 0xBB, 0xBF, (byte)'}']);

        var read = await CliContainerEngine.ReadBoundedAsync(stream, CliContainerEngine.StrictUtf8, maxChars: 2);

        Assert.False(read.InvalidBytes);
        Assert.Equal("{\uFEFF", read.Text);
        Assert.True(read.Truncated);
    }

    private static readonly string Bom = ((char)0xFEFF).ToString();

    public static TheoryData<string, byte[], bool, string> ByteOrderMarkPlacements => new()
    {
        // Kills "skip every leading BOM": only the first is a byte-order mark.
        { "two leading BOMs in one read", [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, (byte)'{'], false, Bom + "{" },
        // Kills "skip a U+FEFF at the start of every read": here the mid-stream one is
        // the first character its own read decodes.
        { "a mid-stream BOM, one byte per read", [(byte)'a', 0xEF, 0xBB, 0xBF, (byte)'b'], true, "a" + Bom + "b" },
        // Kills "the first read ends the start of the stream even when it decoded
        // nothing": the reads holding EF and BB decode no character at all.
        { "a leading BOM split across one-byte reads", [0xEF, 0xBB, 0xBF, (byte)'a'], true, "a" },
    };

    /// <summary>
    /// "One leading UTF-8 BOM, only at stream start" pinned against how the pipe
    /// chunks its reads: a read boundary must neither make a later U+FEFF look leading
    /// nor make a split leading BOM look like content.
    /// </summary>
    [Theory]
    [MemberData(nameof(ByteOrderMarkPlacements))]
    public async Task Exactly_one_byte_order_mark_is_skipped_and_only_at_the_start_of_the_stream(
        string placement, byte[] bytes, bool oneBytePerRead, string expected)
    {
        _ = placement;
        using Stream stream = oneBytePerRead ? new OneBytePerReadStream(bytes) : new MemoryStream(bytes);

        var read = await CliContainerEngine.ReadBoundedAsync(stream, CliContainerEngine.StrictUtf8, maxChars: 1024);

        Assert.False(read.InvalidBytes);
        Assert.Equal(expected, read.Text);
    }

    /// <summary>
    /// The engine, not only the reader: stderr must reach the host leniently decoded.
    /// Wiring stderr to the strict decoder would not fail any run — nothing consults
    /// stderr's invalid-bytes flag — it would silently empty the traceback instead,
    /// which is exactly what a host must never lose. The "engine" is a script that
    /// copies a file holding a lone <c>0xFF</c> to stderr and exits 0.
    /// </summary>
    [Fact]
    public async Task Invalid_bytes_on_stderr_do_not_fail_a_run_and_reach_the_host_replaced()
    {
        using var tmp = new TempDir();
        var engine = new CliContainerEngine(ByteWritingEngine(tmp, [(byte)'a', 0xFF, (byte)'b'], toStderr: true, exitCode: 0));

        var result = await engine.RunAsync(Spec(TimeSpan.FromSeconds(30)));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("a" + (char)0xFFFD + "b", result.Stderr);
    }

    /// <summary>
    /// The CI-visible half of the real-Docker
    /// <c>ContainerIntegrationTests.A_container_whose_stdout_is_not_valid_utf8_fails_the_stage</c>:
    /// stdout wired to the strict decoder, and the failure carrying the exit code like
    /// the output-ceiling message does.
    /// </summary>
    [Fact]
    public async Task Invalid_bytes_on_stdout_fail_the_run_with_the_exit_code()
    {
        using var tmp = new TempDir();
        var engine = new CliContainerEngine(ByteWritingEngine(tmp, [(byte)'{', 0xFF, (byte)'}'], toStderr: false, exitCode: 3));

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await engine.RunAsync(Spec(TimeSpan.FromSeconds(30))));

        Assert.Equal("container stdout was not valid UTF-8 (exit code 3)", ex.Message);
    }

    /// <summary>
    /// stderr is read with the lenient decoder on purpose: it is host-side diagnostics,
    /// never authenticated, and failing on it would hide the traceback a host needs.
    /// Invalid bytes there are replaced, not reported.
    /// </summary>
    [Fact]
    public async Task The_lenient_reader_replaces_invalid_bytes_instead_of_reporting_them()
    {
        using var stream = new MemoryStream([(byte)'a', 0xFF, (byte)'b']);

        var read = await CliContainerEngine.ReadBoundedAsync(stream, CliContainerEngine.LenientUtf8, maxChars: 1024);

        Assert.False(read.InvalidBytes);
        Assert.Equal("a\uFFFDb", read.Text);
    }

    /// <summary>
    /// Teardown after a timeout runs <c>engine kill</c>, and the first version waited
    /// for that child with no bound at all: an engine whose daemon has gone away hangs
    /// on <c>kill</c>, which turned the timeout <see cref="CliContainerEngine.RunAsync"/>
    /// promised into a hang. The "engine" here always hangs on <c>run</c> (so the
    /// outer run genuinely times out) and hangs 20 s on <c>kill</c> too, standing in
    /// for exactly that unresponsive daemon, and records one line per <c>kill</c>
    /// invocation to a marker file so this test can also assert nothing else was
    /// attempted -- no removal, no check -- since the hung kill used up the whole
    /// budget, an engine that did not answer once will not answer a second time, and
    /// the caller is already past its own deadline.
    /// <para>
    /// <see cref="CliContainerEngine.TeardownBudget"/> is shrunk to 1 s here instead of
    /// asserting against its 3 s production default: the old, unbounded-per-attempt
    /// behaviour this replaces takes at least 5 s regardless of that budget (its bound
    /// was a hardcoded per-attempt 5 s, never a shared deadline), so a 1 s budget still
    /// gives a wide, CI-safe margin either side of the 4 s assertion below, wider than
    /// a straight 3 s-budget-vs-old-5 s comparison would on a loaded runner.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_hung_engine_kill_does_not_hang_the_timed_out_run()
    {
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "kill-calls.txt");
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, killMode: KillMode.Hang))
        {
            TeardownBudget = TimeSpan.FromSeconds(1),
        };
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await engine.RunAsync(Spec(TimeSpan.FromMilliseconds(300))));

        Assert.Contains("exceeded its timeout", ex.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"the timed-out run took {clock.Elapsed}");
        Assert.Equal(1, CountInvocations(marker, "kill"));
        // The hung kill used up the whole budget, so no removal was started either.
        Assert.Equal(0, CountInvocations(marker, "rm"));
        Assert.Equal(0, CountInvocations(marker, "inspect"));
    }

    /// <summary>
    /// The other half of the same budget: a <c>kill</c> that answers -- slowly, and
    /// with failure -- must not stop the removal that follows it, and the removal rounds
    /// that follow must still get to run, because they are what verifies the container
    /// is gone. The "engine" here targets roughly 750 ms then exits non-zero on every
    /// <c>kill</c> call (a real <c>sleep 0.75</c> on POSIX; an approximate <c>ping</c>-based
    /// wait on Windows that in practice can run notably shorter, measured 275-485 ms),
    /// answers <c>rm -f</c> with 0 and <c>inspect</c> with 1 (absent), and records one
    /// marker line per call. Expected: one <c>kill</c> (teardown no longer retries it --
    /// once the local client is dead the removal subsumes it), then at least two rounds
    /// of <c>rm -f</c> + <c>inspect</c>: absence has to be seen twice, and, since this
    /// engine never confirmed the container existed, to hold for a further 1.5 s.
    /// <para>
    /// <see cref="CliContainerEngine.TeardownBudget"/> is widened here rather than
    /// shrunk, unlike the sibling <see cref="A_hung_engine_kill_does_not_hang_the_timed_out_run"/>:
    /// that sibling shrinks its budget purely for test speed (a hung <c>kill</c>
    /// consumes its whole slice of the budget every time, so a smaller budget makes a
    /// deterministically slow test faster without changing what it proves). This test
    /// is different -- its engine answers on its own, so the second round only runs if
    /// the budget still has the internal <c>RetryThreshold</c> (500 ms) left after the
    /// first one, and what the first round costs is <em>not</em> just the kill's
    /// ~750 ms (POSIX <c>sleep</c>; the Windows <c>ping</c> measured 275-485 ms): the
    /// teardown launches 1 kill, then rounds of rm + inspect (each a cmd or sh spawn
    /// plus pipe drain, tens of ms on an idle box and several hundred on a loaded
    /// runner) separated by 250 ms delays, for at least the 1.5 s the absence has to
    /// hold. An earlier version of this test with a 2 s budget lost its retry on a loaded
    /// windows-latest runner (3.59 s against a 3.5 s bound) and on a loaded
    /// ubuntu-latest one (1 of the 2 expected markers written). So <c>budget</c> stays
    /// at 5 s: the 0.75 s kill + the 1.5 s window + a last round is about 2.5-3 s on an
    /// idle box, and a round only starts while at least the 500 ms RetryThreshold of
    /// the budget remains, so a slow runner ends the teardown early rather than running
    /// past the budget. The elapsed bound is derived from the budget plus a fixed slack
    /// rather than a bare literal, so the two cannot drift apart.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_slow_failing_kill_does_not_stop_the_removal_rounds_within_the_budget()
    {
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var budget = TimeSpan.FromSeconds(5);
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, killMode: KillMode.SlowFail, rmExitCode: 0, inspectExitCode: 1))
        {
            TeardownBudget = budget,
        };
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await engine.RunAsync(Spec(TimeSpan.FromMilliseconds(30))));

        var bound = budget + TimeSpan.FromSeconds(3);
        Assert.Contains("exceeded its timeout", ex.Message);
        Assert.True(clock.Elapsed < bound, $"the timed-out run took {clock.Elapsed} against a bound of {bound} (budget {budget})");
        Assert.Equal(1, CountInvocations(marker, "kill"));
        Assert.True(CountInvocations(marker, "rm") >= 2);
        Assert.True(CountInvocations(marker, "inspect") >= 2);
    }

    private static readonly System.Text.RegularExpressions.Regex EngineContainerName =
        new("^okf-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// #110: a run cancelled or timed out while its container is created but not
    /// started leaves one <c>kill</c> cannot touch (it is not running) and <c>--rm</c>
    /// never fires for (it never ran). The teardown therefore issues <c>rm -f</c> for the
    /// run's container -- its exact name, not a pattern -- and then <c>inspect</c> of the
    /// same name to check it is gone. Absence has to be seen in two rounds, and -- as
    /// <c>kill</c> never confirmed the container existed, so its <c>create</c> may still
    /// be in flight -- to hold for a further 1.5 s (the lower bound asserted on the
    /// elapsed time below; the code's own <c>AbsentWindow</c>). The CI-visible half only:
    /// whether the engine really has no such container is <c>ContainerIntegrationTests</c>'
    /// to say.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_teardown_removes_the_container_by_name_and_checks_it_is_gone(bool callerCancels)
    {
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var budget = TimeSpan.FromSeconds(5);
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 0, inspectExitCode: 1))
        {
            TeardownBudget = budget,
        };
        var clock = Stopwatch.StartNew();

        await AssertRunEndsAsync(engine, callerCancels);

        var kill = Assert.Single(Invocations(marker, "kill"));
        var name = kill.Single();
        Assert.Matches(EngineContainerName, name);
        // `rm -f <name>` and `inspect <name>`, the surface Docker, Podman and nerdctl
        // share, both aimed at the very container `kill` was just refused for.
        Assert.True(CountInvocations(marker, "rm") >= 2);
        Assert.All(Invocations(marker, "rm"), removal => Assert.Equal(["-f", name], removal));
        Assert.True(CountInvocations(marker, "inspect") >= 2);
        Assert.All(Invocations(marker, "inspect"), inspection => Assert.Equal([name], inspection));
        // The run alone is ~0.3 s (the timeout / the cancel delay); the 1.5 s hold on
        // top of it is what keeps teardown watching for a late create.
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(1500), $"teardown gave up after {clock.Elapsed}");
    }

    /// <summary>
    /// A <c>kill</c> that succeeded does not end the teardown: <c>--rm</c> may well have
    /// removed the container, but nothing here has checked, and that is the whole point.
    /// The removal rounds run exactly as when <c>kill</c> failed.
    /// </summary>
    [Fact]
    public async Task A_successful_kill_does_not_end_the_teardown_before_the_container_is_checked()
    {
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Succeed, rmExitCode: 1, inspectExitCode: 1))
        {
            TeardownBudget = TimeSpan.FromSeconds(5),
        };

        await AssertRunEndsAsync(engine, callerCancels: false);

        Assert.Equal(1, CountInvocations(marker, "kill"));
        Assert.Equal(2, CountInvocations(marker, "rm"));
        Assert.Equal(2, CountInvocations(marker, "inspect"));
    }

    /// <summary>
    /// <c>rm -f</c> of a name the engine does not know exits 0 on Docker (29.4.1), so its
    /// exit code is no evidence that the container is gone, and teardown must not stop on
    /// it. Here <c>rm -f</c> exits 0 every time but <c>inspect</c> keeps finding the
    /// container: the rounds repeat until the budget is spent -- more than the two a clean
    /// check needs -- and the outcome is still the one the run was going to report.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_removal_that_exits_zero_is_not_taken_as_proof_the_container_is_gone(bool callerCancels)
    {
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var budget = TimeSpan.FromSeconds(2);
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 0, inspectExitCode: 0))
        {
            TeardownBudget = budget,
        };
        var clock = Stopwatch.StartNew();

        await AssertRunEndsAsync(engine, callerCancels);

        var bound = budget + TimeSpan.FromSeconds(3);
        Assert.True(clock.Elapsed < bound, $"the run took {clock.Elapsed} against a bound of {bound} (budget {budget})");
        Assert.True(CountInvocations(marker, "rm") >= 2, "teardown stopped after one removal although the container was still there");
        Assert.True(CountInvocations(marker, "inspect") >= 2);
    }

    /// <summary>
    /// A <c>rm -f</c> or <c>inspect</c> that fails -- as one does on an engine that is
    /// down or odd -- must neither replace the outcome the run was already going to
    /// report (the caller's <see cref="OperationCanceledException"/>, or the timeout's
    /// <see cref="ContainerExecutionException"/>) nor escape as an exception of its own;
    /// teardown never throws. A non-zero <c>inspect</c> is read as "absent".
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failing_removal_does_not_change_the_outcome(bool callerCancels)
    {
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var budget = TimeSpan.FromSeconds(5);
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 1, inspectExitCode: 1))
        {
            TeardownBudget = budget,
        };
        var clock = Stopwatch.StartNew();

        await AssertRunEndsAsync(engine, callerCancels);

        var bound = budget + TimeSpan.FromSeconds(3);
        Assert.True(clock.Elapsed < bound, $"the run took {clock.Elapsed} against a bound of {bound} (budget {budget})");
        Assert.Equal(1, CountInvocations(marker, "kill"));
        Assert.True(CountInvocations(marker, "rm") >= 2);
        Assert.True(CountInvocations(marker, "inspect") >= 2);
    }

    /// <summary>
    /// Runs <paramref name="engine"/> (whose "run" hangs) until it is cancelled by the
    /// caller's token or timed out by the spec's own deadline, and asserts the exception
    /// that tells which -- the shape the teardown must never disturb.
    /// </summary>
    private static async Task AssertRunEndsAsync(CliContainerEngine engine, bool callerCancels)
    {
        if (callerCancels)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await engine.RunAsync(Spec(TimeSpan.FromSeconds(60)), cts.Token));
        }
        else
        {
            var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
                async () => await engine.RunAsync(Spec(TimeSpan.FromMilliseconds(300))));
            Assert.Contains("exceeded its timeout", ex.Message);
        }
    }

    /// <summary>
    /// The arguments (after the subcommand) of every invocation of <paramref name="subcommand"/>
    /// the fake engine recorded, in order.
    /// </summary>
    private static List<string[]> Invocations(string markerFile, string subcommand) =>
        (File.Exists(markerFile) ? File.ReadAllLines(markerFile) : [])
            .Select(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 0 && parts[0] == subcommand)
            .Select(static parts => parts[1..])
            .ToList();

    private static int CountInvocations(string markerFile, string subcommand) => Invocations(markerFile, subcommand).Count;

    private enum KillMode
    {
        /// <summary>Hangs for 20 s and never exits on its own -- the unresponsive-daemon case.</summary>
        Hang,

        /// <summary>Targets roughly 750 ms then exits non-zero -- the slow-but-responsive, still-failing case.</summary>
        SlowFail,

        /// <summary>Exits 1 at once -- what <c>kill</c> does for a container that was created but never started, or does not exist.</summary>
        Fail,

        /// <summary>Exits 0 at once -- <c>kill</c> of a running container.</summary>
        Succeed,
    }

    /// <summary>
    /// An "engine" that dispatches on its first argument the way a real one does:
    /// <c>run</c> always hangs for <paramref name="runHangSeconds"/> (so
    /// <see cref="CliContainerEngine.RunAsync"/> genuinely times out and proceeds to
    /// teardown), <c>kill</c> behaves per <paramref name="killMode"/>, and <c>rm</c> and
    /// <c>inspect</c> exit <paramref name="rmExitCode"/> and <paramref name="inspectExitCode"/>
    /// at once. Every invocation appends its own arguments as one line to
    /// <paramref name="markerFile"/>, so a test can count how many of each ran and with
    /// what. The defaults model an engine that answers every teardown command with
    /// failure; the real Docker's answers (verified on 29.4.1) are <c>kill</c> 1 and
    /// <c>inspect</c> 1 for a name it does not know, <c>rm -f</c> 0.
    /// </summary>
    private static string DispatchingEngine(TempDir tmp, string markerFile, int runHangSeconds, KillMode killMode, int rmExitCode = 1, int inspectExitCode = 1)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var killBody = killMode switch
            {
                KillMode.Hang => "ping -n 21 127.0.0.1 >nul\r\nexit /b 1\r\n",
                KillMode.SlowFail => "ping -n 1 -w 750 192.0.2.1 >nul\r\nexit /b 1\r\n",
                KillMode.Succeed => "exit /b 0\r\n",
                _ => "exit /b 1\r\n",
            };
            return tmp.Write(
                "engine.cmd",
                "@echo off\r\n"
                + $"echo %* >> \"{markerFile}\"\r\n"
                + "if \"%1\"==\"kill\" goto kill\r\n"
                + "if \"%1\"==\"rm\" goto rm\r\n"
                + "if \"%1\"==\"inspect\" goto inspect\r\n"
                + $"ping -n {runHangSeconds + 1} 127.0.0.1 >nul\r\n"
                + "exit /b 0\r\n"
                + ":rm\r\n"
                + $"exit /b {rmExitCode}\r\n"
                + ":inspect\r\n"
                + $"exit /b {inspectExitCode}\r\n"
                + ":kill\r\n"
                + killBody);
        }

        var killBodySh = killMode switch
        {
            KillMode.Hang => "sleep 20",
            KillMode.SlowFail => "sleep 0.75; exit 1",
            KillMode.Succeed => "exit 0",
            _ => "exit 1",
        };
        var path = tmp.Write(
            "engine.sh",
            "#!/bin/sh\n"
            + $"echo \"$*\" >> '{markerFile}'\n"
            + "case \"$1\" in\n"
            + $"  kill) {killBodySh} ;;\n"
            + $"  rm) exit {rmExitCode} ;;\n"
            + $"  inspect) exit {inspectExitCode} ;;\n"
            + $"  *) sleep {runHangSeconds} ;;\n"
            + "esac\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static MemoryStream StreamOver(string text) =>
        new(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// An "engine" that ignores its arguments, copies <paramref name="bytes"/> verbatim
    /// to stdout or stderr, and exits with <paramref name="exitCode"/>. The bytes live
    /// in a file the script copies (<c>type</c>/<c>cat</c>) rather than in the script
    /// text, so no shell quoting stands between them and the pipe.
    /// </summary>
    private static string ByteWritingEngine(TempDir tmp, byte[] bytes, bool toStderr, int exitCode)
    {
        var payload = Path.Combine(tmp.Path, "payload.bin");
        File.WriteAllBytes(payload, bytes);
        var redirect = toStderr ? " 1>&2" : "";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return tmp.Write("engine.cmd", $"@echo off\r\ntype \"{payload}\"{redirect}\r\nexit /b {exitCode}\r\n");
        }

        var path = tmp.Write("engine.sh", $"#!/bin/sh\ncat '{payload}'{redirect}\nexit {exitCode}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>A stream that hands out at most one byte per read, so every sequence is split.</summary>
    private sealed class OneBytePerReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 1));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_inner.Read(buffer.Span[..Math.Min(buffer.Length, 1)]));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

}
