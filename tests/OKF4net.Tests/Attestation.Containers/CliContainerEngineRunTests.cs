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
/// <para><b>Runs alone.</b> The class sits in a collection with parallelization
/// disabled (<see cref="CliContainerEngineTeardownTimingCollection"/>), which xunit
/// runs by itself after every parallel collection has finished. The two teardown
/// tests bound wall-clock time, and <see cref="CliContainerEngine"/> does its teardown
/// on thread-pool continuations (timer callbacks, process-exit notifications), so
/// in-process thread-pool starvation from the rest of the suite stretches them. Measured
/// during the parallel phase, under four concurrent full suites (#149): pool callbacks
/// 2.0-2.7 s late while a dedicated thread was at most 0.26 s late, the first
/// <c>kill</c> starting 3.6 s into a run whose timeout was 30 ms, and the slow-kill test
/// taking 8.2-9.5 s against its 8 s bound. Running alone removes that source.
/// Other processes on the machine still compete for the CPU, which is why each bound
/// below also states its margin.</para>
/// </summary>
[Collection(CliContainerEngineTeardownTimingCollection.Name)]
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
    /// promised into a hang. The "engine" here always hangs on <c>start</c> (so the
    /// outer run genuinely times out) and hangs on <c>kill</c> too, standing in for
    /// exactly that unresponsive daemon. Its <c>kill</c> writes a <c>kill</c> line to a
    /// marker file as it starts, sleeps <see cref="HungKillSeconds"/>, then writes a
    /// <c>done</c> line -- a line only a <c>kill</c> child that was left to run out its
    /// hang can write. The <c>kill</c> lines let this test assert no retry was attempted
    /// -- a kill that already hung once will not answer a second time either, and the
    /// caller is already past its own deadline.
    /// <para>
    /// The run's timeout fires through a manual clock as soon as <c>start</c> is running
    /// (<see cref="EndRunOnceStartedAsync"/>), not after 300 ms of wall clock, which a
    /// loaded machine could spend before <c>create</c> had exited -- no <c>kill</c> is
    /// issued then, and "exactly one <c>kill</c>" below failed on correct code.
    /// </para>
    /// <para>
    /// The clock starts at the hang (#149). This test used to time the whole
    /// <see cref="CliContainerEngine.RunAsync"/> call against a fixed 4 s, so that one
    /// number had to absorb spawning the <c>start</c> process, the 300 ms timeout,
    /// spawning <c>kill</c>, the 1 s budget and two process-tree kills; a GitHub Windows
    /// runner needed 4.25-4.3 s for all of that, against ~1.3 s normally. Its main
    /// bound is now measured from the moment the <c>kill</c> child writes its
    /// <c>kill</c> line -- immediately before its sleep -- as seen by a 5 ms poll of the
    /// marker file (<see cref="WhenKillLineAppearsAsync"/>, whose reading can only be
    /// late, which shortens the window and can only hide a regression on one run).
    /// </para>
    /// <para>
    /// The mechanism the bounds rest on (<c>KillContainerAsync</c>, <c>TryKillOnceAsync</c>):
    /// the teardown stopwatch starts, and the first attempt's slice of the budget is
    /// computed -- in practice the whole 1 s, since nothing has run yet -- BEFORE
    /// <c>kill</c> is spawned; that slice's cancellation is armed only once
    /// <c>Start()</c> has returned. The time spent spawning is therefore charged to the
    /// budget only for the retry decision, not to the first attempt. The <c>kill</c>
    /// line is written after <c>Start()</c> returned (once the child's shell is up), so
    /// the cut-off lands at most 1 s after that line, and what follows it -- killing the
    /// <c>kill</c> tree, killing the <c>start</c> tree, draining output -- is latency.
    /// </para>
    /// <para>
    /// What each assertion catches, and what it cannot:
    /// <list type="bullet">
    /// <item><b>Window (kill line to return) under 1 s budget + 2 s slack.</b> Measured
    /// with the class running alone and <see cref="EnsureThreadPoolHeadroom"/>: at most
    /// 1.11 s on the three CI runners (48 runs). Without that headroom: at most 1.37 s
    /// locally under four concurrent full suites (64 runs), and 1.83 s once on a Windows
    /// CI runner. Catches an unbounded wait on <c>kill</c> (about 4 s, since
    /// the child exits after its sleep); the fixed 5 s per-attempt bound the shared
    /// budget replaced (about 5 s, minus the child's shell start-up -- so it is missed
    /// only on a run where that start-up takes over 2 s; the CI measurements put the
    /// whole spawn-plus-start-up at tens of ms); and any extra wait of 2 s or more after
    /// the hang begins, e.g. a bounded drain after a hung <c>kill</c>. Cannot see a
    /// delay before <c>kill</c> starts.</item>
    /// <item><b>End to end under 8 s</b>, from before <see cref="CliContainerEngine.RunAsync"/>
    /// is called. Catches a delay anywhere, including before teardown starts, and a
    /// teardown that waits out the 20 s <c>start</c> hang (one that stopped killing the
    /// <c>start</c> tree). Correct runs take 1.3-1.8 s; 8 s is close to twice the
    /// slowest CI run ever seen (4.3 s, #149, under parallel load). Cannot catch an
    /// added delay of up to ~6 s before <c>kill</c> starts.</item>
    /// <item><b>No <c>done</c> line, checked after outliving the hang</b> (until the
    /// kill line + <see cref="HungKillSeconds"/> + 2 s). Catches a <c>kill</c> child
    /// that was abandoned rather than killed with its process tree: the window
    /// assertion alone passes then, because the wait itself was still cut off. Correct
    /// code fails it only if the kill tree is killed more than 4 - 1 = 3 s after the
    /// cut-off. The extra 2 s covers a <c>ping</c>-based sleep running long; an
    /// abandoned child slower than that is missed on that run.</item>
    /// <item><b>Exactly one <c>kill</c> line.</b> Catches a retry after a hung
    /// <c>kill</c>, including a fresh budget per attempt. The budget stays at 1 s
    /// rather than smaller so this means something: a <c>kill</c> that failed fast would
    /// leave more than <c>RetryThreshold</c> (500 ms) of 1 s and be retried, so only the
    /// hang consuming the budget explains a single attempt. Its one timing dependency:
    /// the child must write its line within the 1 s slice, i.e. its shell must start in
    /// under 1 s.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_hung_engine_kill_does_not_hang_the_timed_out_run()
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "kill-calls.txt");
        var timeouts = new ManualTimeProvider();
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, killMode: KillMode.Hang))
        {
            TeardownBudget = HungKillBudget,
            TimeProvider = timeouts,
        };
        var clock = Stopwatch.StartNew();
        using var stopWatching = new CancellationTokenSource();
        var killLineSeen = WhenKillLineAppearsAsync(marker, clock, stopWatching.Token);

        var (outcome, returnedAt) = await EndRunOnceStartedAsync(engine, marker, timeouts, clock);
        await stopWatching.CancelAsync();
        var killStartedAt = await killLineSeen;

        Assert.Contains("exceeded its timeout", Assert.IsType<ContainerExecutionException>(outcome).Message);
        Assert.True(
            returnedAt < HungKillEndToEndBound,
            $"the timed-out run took {returnedAt} against an end-to-end bound of {HungKillEndToEndBound}");
        Assert.True(killStartedAt.HasValue, "the fake engine's kill never wrote its kill line");
        var window = returnedAt - killStartedAt.Value;
        var windowBound = HungKillBudget + HungKillWindowSlack;
        Assert.True(
            window < windowBound,
            $"the timed-out run returned {window} after the hung kill started its sleep, against a bound of {windowBound} (teardown budget {HungKillBudget})");

        // Then outlive the hang: a kill child that was abandoned rather than killed
        // writes its `done` line before this wait ends.
        var outlive = killStartedAt.Value + TimeSpan.FromSeconds(HungKillSeconds) + HungKillOrphanGrace - clock.Elapsed;
        if (outlive > TimeSpan.Zero)
        {
            await Task.Delay(outlive);
        }

        var (kills, dones) = ReadMarker(marker);
        Assert.True(
            dones == 0,
            $"the hung kill wrote its done line: it ran out its {HungKillSeconds} s sleep instead of being killed with its process tree");
        Assert.Equal(1, kills);
        // The hung kill used up the whole budget: no removal and no check was started.
        Assert.Empty(Invocations(marker, "rm"));
        Assert.Empty(Invocations(marker, "inspect"));
    }

    /// <summary>
    /// The other half of the same budget: a <c>kill</c> that answers -- slowly, and
    /// with failure -- must not stop the removal rounds that follow it (#110): they are
    /// what verifies the container is gone, and they exist for a container whose
    /// creation was still in flight, not only for a dead engine. The "engine" here
    /// targets roughly 750 ms then exits non-zero on <c>kill</c> (a real
    /// <c>sleep 0.75</c> on POSIX; an approximate <c>ping</c>-based wait on Windows that
    /// in practice can run notably shorter, measured 275-485 ms), answers <c>rm -f</c>
    /// with 0 and <c>inspect</c> with 1 (absent), and records one marker line per call.
    /// Expected: one <c>kill</c> (not retried: once the local client is dead the removal
    /// subsumes it), then at least two rounds of <c>rm -f</c> + <c>inspect</c>.
    /// <para>
    /// The run's timeout fires through a manual clock once <c>start</c> is running
    /// (<see cref="EndRunOnceStartedAsync"/>). It used to be 30 ms of wall clock, which
    /// could expire before the fake engine had finished <c>create</c>: teardown then
    /// rightly issued no <c>kill</c> (the container was not started) and the "one
    /// <c>kill</c>" assertion failed on correct code.
    /// </para>
    /// <para>
    /// <see cref="CliContainerEngine.TeardownBudget"/> is widened here rather than
    /// shrunk, unlike the sibling <see cref="A_hung_engine_kill_does_not_hang_the_timed_out_run"/>:
    /// that sibling shrinks its budget purely for test speed (a hung <c>kill</c>
    /// consumes its whole slice of the budget every time, so a smaller budget makes a
    /// deterministically slow test faster without changing what it proves). This test
    /// is different -- its <c>kill</c> answers on its own, so the retry firing depends
    /// on the budget still having the internal <c>RetryThreshold</c> (500 ms) left
    /// over after the first attempt, and that first attempt's real cost is <em>not</em>
    /// just the ~750 ms POSIX <c>sleep</c> (or the Windows <c>ping</c>, measured
    /// 275-485 ms): it also carries process-spawn and pipe-drain overhead that a
    /// previous 2 s budget gave only ~750 ms of headroom to absorb, which was enough on
    /// an unloaded box but not on a loaded windows-latest runner (3.59 s against a
    /// 3.5 s bound) and, per the CI run this comment now documents, not on a loaded
    /// ubuntu-latest runner either (only 1 of the 2 expected `kill` markers was
    /// written -- the retry was skipped because too little budget was left). A 3.5 s
    /// (windows) or 2 s (POSIX) budget both left too little of that margin. Rather than
    /// re-tuning the same knob a second time, <c>budget</c> here is set to comfortably
    /// exceed 2x the slower attempt plus the retry delay (2 x 0.75 s + 0.25 s = 1.75 s
    /// on POSIX; smaller on Windows) by roughly 3 s of headroom -- enough to absorb
    /// even a heavily loaded shared CI runner's scheduling noise without the
    /// RetryThreshold check ever coming close to the edge.
    /// </para>
    /// <para>
    /// What each assertion catches, and what it cannot:
    /// <list type="bullet">
    /// <item><b>One <c>kill</c> line, at least two <c>rm</c> and two <c>inspect</c>
    /// lines</b> -- the guarantee itself. Catches removal rounds that were dropped or
    /// made to need more budget than a slow <c>kill</c> leaves, and a retried
    /// <c>kill</c>. It fails correct code only if the first round cannot start within
    /// the 5 s budget less <c>RetryThreshold</c>.</item>
    /// <item><b>Teardown (first <c>kill</c> line to return) under the budget + 3 s.</b>
    /// This used to be timed from before <see cref="CliContainerEngine.RunAsync"/> was
    /// called, and that is what failed
    /// under load (8.2 s and 9.5 s against 8 s). In those runs the first <c>kill</c>
    /// started 3.6 s into the run, so most of the time was spent before teardown began.
    /// It is now timed from the first <c>kill</c> line, seen by the same poll as the
    /// sibling's. With the class running alone it took 1.0-2.0 s on the CI runners
    /// (the POSIX <c>kill</c> sleeps 2 x 0.75 s). Catches a teardown
    /// that runs on for 8 s or more after its first <c>kill</c>, e.g. a retry delay of
    /// several seconds, or an attempt that does not end when its <c>kill</c> exits. It
    /// cannot show that the budget itself is honoured: neither 0.3-0.75 s attempt comes
    /// near it. That is the sibling's job, whose 1 s budget a hung <c>kill</c> actually
    /// exhausts.</item>
    /// </list>
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_slow_failing_kill_does_not_stop_the_removal_rounds_within_the_budget()
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "kill-calls.txt");
        var budget = TimeSpan.FromSeconds(5);
        var timeouts = new ManualTimeProvider();
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, killMode: KillMode.SlowFail, rmExitCode: 0, inspectExitCode: 1))
        {
            TeardownBudget = budget,
            TimeProvider = timeouts,
        };
        var clock = Stopwatch.StartNew();
        using var stopWatching = new CancellationTokenSource();
        var killLineSeen = WhenKillLineAppearsAsync(marker, clock, stopWatching.Token);

        var (outcome, returnedAt) = await EndRunOnceStartedAsync(engine, marker, timeouts, clock);
        await stopWatching.CancelAsync();
        var firstKill = await killLineSeen;

        Assert.Contains("exceeded its timeout", Assert.IsType<ContainerExecutionException>(outcome).Message);
        Assert.Equal(1, ReadMarker(marker).Kills);
        Assert.True(Invocations(marker, "rm").Count >= 2);
        Assert.True(Invocations(marker, "inspect").Count >= 2);
        Assert.True(firstKill.HasValue, "the fake engine's kill never wrote its kill line");
        var teardown = returnedAt - firstKill.Value;
        var bound = budget + TimeSpan.FromSeconds(3);
        Assert.True(
            teardown < bound,
            $"the timed-out run returned {teardown} after its first kill started, against a bound of {bound} (budget {budget})");
    }

    /// <summary>
    /// How long the <see cref="KillMode.Hang"/> fake <c>kill</c> sleeps before writing its
    /// <c>done</c> line; see <see cref="A_hung_engine_kill_does_not_hang_the_timed_out_run"/>
    /// for why 4 s. On Windows the sleep is <c>ping -n 5 127.0.0.1</c>, whose four
    /// one-second intervals only ever run long, never short.
    /// </summary>
    private const int HungKillSeconds = 4;

    /// <summary>The teardown budget <see cref="A_hung_engine_kill_does_not_hang_the_timed_out_run"/> runs with.</summary>
    private static readonly TimeSpan HungKillBudget = TimeSpan.FromSeconds(1);

    /// <summary>Latency allowed after the 1 s budget in the hung-kill window; see that test for the measurements behind it.</summary>
    private static readonly TimeSpan HungKillWindowSlack = TimeSpan.FromSeconds(2);

    /// <summary>The hung-kill test's end-to-end backstop, timed from before <see cref="CliContainerEngine.RunAsync"/> is called; see that test.</summary>
    private static readonly TimeSpan HungKillEndToEndBound = TimeSpan.FromSeconds(8);

    /// <summary>How long past the end of its sleep an abandoned hung <c>kill</c> child is given to write its <c>done</c> line.</summary>
    private static readonly TimeSpan HungKillOrphanGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Raises -- never lowers -- the thread pool's minimum worker count before a teardown
    /// test. Running alone (see the class) keeps other tests from competing, but not from
    /// leaving work behind: a test that abandons a blocking stage leaves a pool thread
    /// blocked after it ends, and once the minimum is used up the pool adds threads
    /// slowly, so a queued teardown continuation waits for one. Measured on a Windows CI
    /// runner with the class already running alone: one hung-kill window of 1.83 s against
    /// ~1.0 s, with pool callbacks 1.0 s late while a dedicated thread was at most 16 ms
    /// late. The setting is process-wide, but only ever raised, and a production host's
    /// pool is its own concern: this removes test-host noise, not anything
    /// <see cref="CliContainerEngine"/> does.
    /// </summary>
    private static void EnsureThreadPoolHeadroom()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        if (workers < 64)
        {
            ThreadPool.SetMinThreads(64, io);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex EngineContainerName =
        new("^okf-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// #110: a run cancelled or timed out while its container is created but not
    /// started leaves one <c>kill</c> cannot touch (it is not running) and <c>--rm</c>
    /// never fires for (it never ran). The teardown therefore issues <c>rm -f</c> for the
    /// run's container -- its exact name, not a pattern -- and then <c>inspect</c> of the
    /// same name to check it is gone. The run's <c>create</c> exited 0 before <c>start</c>
    /// ever ran, so its outcome is settled and absence needs only two rounds -- exactly two
    /// <c>inspect</c> calls, not the 1.5 s hold an unsettled create gets (see the test
    /// further down). The CI-visible half only: whether the engine really has no such
    /// container is <c>ContainerIntegrationTests</c>' to say.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_teardown_removes_the_container_by_name_and_checks_it_is_gone(bool callerCancels)
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var timeouts = new ManualTimeProvider();
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 0, inspectExitCode: 1))
        {
            TeardownBudget = TimeSpan.FromSeconds(5),
            TimeProvider = timeouts,
        };

        await AssertRunEndsAsync(engine, timeouts, marker, callerCancels);

        var kill = Assert.Single(Invocations(marker, "kill"));
        var name = kill.Single();
        Assert.Matches(EngineContainerName, name);
        // `rm -f <name>` and `inspect <name>`, the surface Docker, Podman and nerdctl
        // share, both aimed at the very container `kill` was just refused for.
        Assert.True(Invocations(marker, "rm").Count >= 2);
        Assert.All(Invocations(marker, "rm"), removal => Assert.Equal(["-f", name], removal));
        Assert.Equal(2, Invocations(marker, "inspect").Count);
        Assert.All(Invocations(marker, "inspect"), inspection => Assert.Equal([name], inspection));
    }

    /// <summary>
    /// The other side of the settled create: one that has to be killed because it outlasts
    /// two thirds of the budget (30 s here, 4 s of a 6 s budget) is unsettled -- the
    /// daemon may yet commit it -- so absence is held for the 1.5 s window on top of the
    /// two rounds, as it was for every cancellation before the create became its own
    /// command. The lower bound is that wait plus the 4 s one.
    /// </summary>
    [Fact]
    public async Task An_unsettled_create_gets_the_absence_window()
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 0, inspectExitCode: 1, createSeconds: 30))
        {
            TeardownBudget = TimeSpan.FromSeconds(6),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.RunAsync(Spec(TimeSpan.FromSeconds(60)), cts.Token));

        Assert.Empty(Invocations(marker, "kill"));
        Assert.True(Invocations(marker, "inspect").Count >= 3, "teardown stopped at two absent rounds instead of holding the window");
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(5300), $"teardown gave up after {clock.Elapsed}");
    }

    /// <summary>
    /// A <c>kill</c> that succeeded does not end the teardown: <c>--rm</c> may well have
    /// removed the container, but nothing here has checked, and that is the whole point.
    /// The removal rounds run exactly as when <c>kill</c> failed (and, the container
    /// having been confirmed to exist, two rounds are enough).
    /// </summary>
    [Fact]
    public async Task A_successful_kill_does_not_end_the_teardown_before_the_container_is_checked()
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var timeouts = new ManualTimeProvider();
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Succeed, rmExitCode: 1, inspectExitCode: 1))
        {
            TeardownBudget = TimeSpan.FromSeconds(5),
            TimeProvider = timeouts,
        };

        await AssertRunEndsAsync(engine, timeouts, marker, callerCancels: false);

        Assert.Single(Invocations(marker, "kill"));
        Assert.Equal(2, Invocations(marker, "rm").Count);
        Assert.Equal(2, Invocations(marker, "inspect").Count);
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
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var timeouts = new ManualTimeProvider();
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 0, inspectExitCode: 0))
        {
            // Wide enough that a loaded machine still gets two rounds in: each round is
            // two process spawns plus a 250 ms delay, and a round only starts while at
            // least RetryThreshold of the budget remains.
            TeardownBudget = TimeSpan.FromSeconds(4),
            TimeProvider = timeouts,
        };

        await AssertRunEndsAsync(engine, timeouts, marker, callerCancels);

        Assert.True(Invocations(marker, "rm").Count >= 2, "teardown stopped after one removal although the container was still there");
        Assert.True(Invocations(marker, "inspect").Count >= 2);
    }

    /// <summary>
    /// A <c>rm -f</c> or <c>inspect</c> that fails -- as one does on an engine that is
    /// down or odd -- must neither replace the outcome the run was already going to
    /// report (the caller's <see cref="OperationCanceledException"/>, or the timeout's
    /// <see cref="ContainerExecutionException"/>) nor escape as an exception of its own;
    /// teardown never throws. A non-zero <c>inspect</c> that says "No such object" is read
    /// as "absent" (the fake engine's default); see the test below for one that does not.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failing_removal_does_not_change_the_outcome(bool callerCancels)
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var timeouts = new ManualTimeProvider();
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 1, inspectExitCode: 1))
        {
            TeardownBudget = TimeSpan.FromSeconds(5),
            TimeProvider = timeouts,
        };

        await AssertRunEndsAsync(engine, timeouts, marker, callerCancels);

        Assert.Single(Invocations(marker, "kill"));
        Assert.True(Invocations(marker, "rm").Count >= 2);
        Assert.True(Invocations(marker, "inspect").Count >= 2);
    }

    /// <summary>
    /// A non-zero <c>inspect</c> that does NOT say the name is unknown -- a daemon that is
    /// down, say -- is no evidence of absence. Teardown must keep retrying until its budget
    /// runs out: with a 3 s budget, the absence verdict (two rounds, 1.5 s apart) would end
    /// it at about 1.5 s, so a teardown that returns before 2.5 s took the failure for absence.
    /// The second case is a client that cannot reach the daemon's socket: its "no such file
    /// or directory" says "no such", which a match on those two words alone took for
    /// absence; only "no such container" or "no such object" is.
    /// </summary>
    [Theory]
    [InlineData("Cannot connect to the Docker daemon")]
    [InlineData("dial unix /var/run/docker.sock: connect: no such file or directory")]
    public async Task An_inspect_failure_that_is_not_not_found_is_not_taken_as_absence(string inspectStderr)
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 1, inspectExitCode: 1, inspectStderr: inspectStderr))
        {
            TeardownBudget = TimeSpan.FromSeconds(3),
        };

        // Cancelled once `start` runs, never on a wall-clock delay: a cancellation that lands
        // during `create` spends two thirds of the budget waiting for it, which leaves too
        // little for the four rounds asserted below (the flake this test had).
        var stopwatch = Stopwatch.StartNew();
        var (outcome, returnedAt) = await EndRunOnceStartedAsync(engine, marker, null, stopwatch);

        Assert.IsAssignableFrom<OperationCanceledException>(outcome);
        Assert.True(returnedAt >= TimeSpan.FromSeconds(2.5), $"teardown returned after {returnedAt.TotalSeconds:F1} s with a 3 s budget: an inspect the daemon could not answer was taken as absence");
        Assert.True(Invocations(marker, "inspect").Count >= 4);
    }

    /// <summary>
    /// #110: a run cancelled while its <c>create</c> is still running does not kill that
    /// client -- a <c>create</c> killed mid-flight can be committed by the daemon later,
    /// unseen -- it waits for it to exit (2 s here, inside a 5 s budget), and only then
    /// removes by name. Once the create has exited its outcome is settled, so absence needs
    /// the two rounds and not the 1.5 s hold an unsettled create does: exactly two
    /// <c>inspect</c> calls, where the hold would take six or so.
    /// </summary>
    [Fact]
    public async Task A_create_still_running_at_cancellation_is_awaited_then_removed_on_settled_state()
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 0, inspectExitCode: 1, createSeconds: 2))
        {
            TeardownBudget = TimeSpan.FromSeconds(5),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.RunAsync(Spec(TimeSpan.FromSeconds(60)), cts.Token));

        var lines = File.ReadAllLines(marker).Select(static line => line.Trim()).ToList();
        var createDone = lines.IndexOf("create-done");
        Assert.True(createDone >= 0, "the create client was killed before it finished");
        var firstRemoval = lines.FindIndex(static line => line.StartsWith("rm ", StringComparison.Ordinal));
        Assert.True(firstRemoval > createDone, "the removal ran before the create had finished");
        Assert.Empty(Invocations(marker, "kill"));
        Assert.Equal(2, Invocations(marker, "inspect").Count);
    }

    /// <summary>
    /// A <c>create</c> that outlasts the whole teardown budget (a cold pull, a daemon that
    /// has stopped answering) is killed when the budget is spent, and the run still ends at
    /// the budget with the caller's cancellation: waiting for it is bounded.
    /// </summary>
    [Fact]
    public async Task A_create_that_outlasts_the_budget_is_killed_and_the_run_still_ends()
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 0, inspectExitCode: 1, createSeconds: 15))
        {
            TeardownBudget = TimeSpan.FromSeconds(1),
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await engine.RunAsync(Spec(TimeSpan.FromSeconds(60)), cts.Token));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(6), $"the run took {stopwatch.Elapsed.TotalSeconds:F1} s: the create client was waited for past the 1 s teardown budget");
        Assert.DoesNotContain("create-done", File.ReadAllLines(marker).Select(static line => line.Trim()));
    }

    /// <summary>How long the descendant <see cref="A_descendant_holding_the_output_pipes_does_not_hold_the_timed_out_run"/>'s <c>create</c> leaves behind keeps running.</summary>
    private const int PipeHoldingDescendantSeconds = 30;

    /// <summary>
    /// Once the run has timed out and teardown is done, the timeout's exception carries
    /// the output of the client whose wait was cancelled, and the first version awaited
    /// those drains with no bound. A drain only ends when the last writer of its pipe
    /// closes it, and a client's descendant inherits the pipe: here the <c>create</c>
    /// leaves a background process behind holding both pipes for
    /// <see cref="PipeHoldingDescendantSeconds"/>, then exits 0 after about a second. The
    /// 300 ms timeout lands during that second, so teardown waits for the <c>create</c>
    /// (it exits on its own) and removes the container; what is left is the drain.
    /// Unbounded, the run returned when the descendant ended, ~30 s; bounded, teardown
    /// (~1.5-2 s) plus the drain bound (1 s). The 12 s bound sits between the two with a
    /// wide margin each side.
    /// </summary>
    [Fact]
    public async Task A_descendant_holding_the_output_pipes_does_not_hold_the_timed_out_run()
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, KillMode.Fail, rmExitCode: 0, inspectExitCode: 1, createSeconds: 1, createDescendantSeconds: PipeHoldingDescendantSeconds))
        {
            TeardownBudget = TimeSpan.FromSeconds(5),
        };

        var clock = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await engine.RunAsync(Spec(TimeSpan.FromMilliseconds(300))));
        clock.Stop();

        Assert.Contains("exceeded its timeout", ex.Message);
        // The timeout landed during `create`, which was waited for, not killed: the drain
        // is the only thing still held open when teardown ends.
        Assert.Contains("create-done", File.ReadAllLines(marker).Select(static line => line.Trim()));
        Assert.Empty(Invocations(marker, "kill"));
        var bound = TimeSpan.FromSeconds(12);
        Assert.True(
            clock.Elapsed < bound,
            $"the timed-out run took {clock.Elapsed} against a bound of {bound}: it waited for a descendant holding the output pipes ({PipeHoldingDescendantSeconds} s)");
    }

    /// <summary>
    /// An engine that refuses the <c>create</c> (image not found, a bad flag, a daemon that
    /// is down) leaves no container: the run reports the engine's exit code and message as
    /// the failed result a single <c>run</c> would have, and never starts anything.
    /// </summary>
    [Fact]
    public async Task A_create_the_engine_refuses_is_reported_as_a_failed_run()
    {
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        string engineScript;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            engineScript = tmp.Write("engine.cmd", $"@echo off\r\necho %1 >> \"{marker}\"\r\necho Unable to find image 1>&2\r\nexit /b 125\r\n");
        }
        else
        {
            engineScript = tmp.Write("engine.sh", $"#!/bin/sh\necho \"$1\" >> '{marker}'\necho 'Unable to find image' >&2\nexit 125\n");
            File.SetUnixFileMode(engineScript, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var result = await new CliContainerEngine(engineScript).RunAsync(Spec(TimeSpan.FromSeconds(30)));

        Assert.Equal(125, result.ExitCode);
        Assert.Contains("Unable to find image", result.Stderr);
        Assert.Equal(["create"], File.ReadAllLines(marker).Select(static line => line.Trim()).ToArray());
    }

    /// <summary>
    /// A <c>start</c> that exits non-zero may have failed before the container ever ran,
    /// leaving it in Created state, which <c>--rm</c> never removes -- and nothing here can
    /// tell that from the container's own non-zero exit. So a non-zero <c>start</c> is
    /// followed by the same teardown as a cancelled one: one <c>kill</c>, then removal
    /// rounds until <c>inspect</c> finds the name unknown twice (the create is settled, so
    /// exactly two). The result is the one <c>start</c> gave -- its exit code, stdout and
    /// stderr -- untouched by the teardown. A <c>start</c> that exits 0 triggers no
    /// teardown at all: <c>--rm</c> removes a container that ran.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    public async Task A_start_that_exits_non_zero_is_followed_by_teardown_and_its_result_kept(int startExitCode)
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "calls.txt");
        var engine = new CliContainerEngine(ExitingStartEngine(tmp, marker, startExitCode))
        {
            TeardownBudget = TimeSpan.FromSeconds(5),
        };

        var result = await engine.RunAsync(Spec(TimeSpan.FromSeconds(30)));

        Assert.Equal(startExitCode, result.ExitCode);
        Assert.Equal("start-out", result.Stdout.Trim());
        Assert.Equal("start-err", result.Stderr.Trim());
        if (startExitCode == 0)
        {
            Assert.Empty(Invocations(marker, "kill"));
            Assert.Empty(Invocations(marker, "rm"));
            Assert.Empty(Invocations(marker, "inspect"));
            return;
        }

        var kill = Assert.Single(Invocations(marker, "kill"));
        var name = kill.Single();
        Assert.Matches(EngineContainerName, name);
        Assert.NotEmpty(Invocations(marker, "rm"));
        Assert.All(Invocations(marker, "rm"), removal => Assert.Equal(["-f", name], removal));
        Assert.Equal(2, Invocations(marker, "inspect").Count);
    }

    /// <summary>
    /// An "engine" whose <c>create</c> exits 0 and whose <c>start</c> prints
    /// <c>start-out</c> on stdout and <c>start-err</c> on stderr, then exits with
    /// <paramref name="startExitCode"/>. <c>kill</c> (exit 1), <c>rm</c> (exit 0) and
    /// <c>inspect</c> (exit 1, "No such object") each record one line in
    /// <paramref name="markerFile"/>, the way <see cref="DispatchingEngine"/>'s do.
    /// </summary>
    private static string ExitingStartEngine(TempDir tmp, string markerFile, int startExitCode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return tmp.Write(
                "engine.cmd",
                "@echo off\r\n"
                + "if \"%1\"==\"create\" exit /b 0\r\n"
                + "if \"%1\"==\"kill\" goto kill\r\n"
                + "if \"%1\"==\"rm\" goto rm\r\n"
                + "if \"%1\"==\"inspect\" goto inspect\r\n"
                + "echo start-out\r\n"
                + "echo start-err 1>&2\r\n"
                + $"exit /b {startExitCode}\r\n"
                + ":kill\r\n"
                + $"echo kill %2 >> \"{markerFile}\"\r\n"
                + "exit /b 1\r\n"
                + ":rm\r\n"
                + $"echo %* >> \"{markerFile}\"\r\n"
                + "exit /b 0\r\n"
                + ":inspect\r\n"
                + $"echo %* >> \"{markerFile}\"\r\n"
                + "echo Error: No such object: %2 1>&2\r\n"
                + "exit /b 1\r\n");
        }

        var path = tmp.Write(
            "engine.sh",
            "#!/bin/sh\n"
            + "case \"$1\" in\n"
            + "  create) exit 0 ;;\n"
            + $"  kill) echo \"kill $2\" >> '{markerFile}'; exit 1 ;;\n"
            + $"  rm) echo \"$*\" >> '{markerFile}'; exit 0 ;;\n"
            + $"  inspect) echo \"$*\" >> '{markerFile}'; echo \"Error: No such object: $2\" >&2; exit 1 ;;\n"
            + "esac\n"
            + "echo start-out\n"
            + "echo start-err >&2\n"
            + $"exit {startExitCode}\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>
    /// Runs <paramref name="engine"/> (whose <c>start</c> hangs) until it is cancelled by
    /// the caller's token or timed out by the spec's own deadline -- either way once
    /// <c>start</c> is running (<see cref="EndRunOnceStartedAsync"/>) -- and asserts the
    /// exception that tells which: the shape the teardown must never disturb.
    /// </summary>
    private static async Task AssertRunEndsAsync(CliContainerEngine engine, ManualTimeProvider timeouts, string markerFile, bool callerCancels)
    {
        var (outcome, _) = await EndRunOnceStartedAsync(engine, markerFile, callerCancels ? null : timeouts, Stopwatch.StartNew());
        if (callerCancels)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(outcome);
        }
        else
        {
            Assert.Contains("exceeded its timeout", Assert.IsType<ContainerExecutionException>(outcome).Message);
        }
    }

    /// <summary>
    /// Runs <paramref name="engine"/> -- a <see cref="DispatchingEngine"/> whose
    /// <see cref="CliContainerEngine.TimeProvider"/> is <paramref name="timeouts"/> -- and
    /// ends the run as soon as the fake engine's <c>start</c> line appears: by firing the
    /// spec's timeout through <paramref name="timeouts"/>, or, when that is null, by
    /// cancelling the caller's token. Returns what <see cref="CliContainerEngine.RunAsync"/>
    /// threw (null if nothing) and the <paramref name="clock"/> reading the moment it
    /// returned.
    /// <para>
    /// Why not a short real timeout: these tests assert what teardown does for a run
    /// cancelled during <c>start</c> (one <c>kill</c>, for one), and a timeout of 30 or
    /// 300 ms of wall clock can expire before a loaded machine has even finished
    /// <c>create</c> -- teardown then rightly issues no <c>kill</c>, and the "exactly one
    /// kill" assertion failed on correct code. The <c>start</c> line is written once the
    /// <c>start</c> client's shell is running, so the run is past <c>create</c> by
    /// construction, whatever the machine's load.
    /// </para>
    /// </summary>
    private static async Task<(Exception? Outcome, TimeSpan ReturnedAt)> EndRunOnceStartedAsync(
        CliContainerEngine engine, string markerFile, ManualTimeProvider? timeouts, Stopwatch clock)
    {
        using var caller = new CancellationTokenSource();
        using var stopWatching = new CancellationTokenSource();
        Action end = timeouts is null ? caller.Cancel : timeouts.Fire;
        var ended = WhenStartLineAppearsAsync(markerFile, end, stopWatching.Token);

        // The spec's timeout only fires through `timeouts`: 300 ms is never waited for.
        var spec = Spec(timeouts is null ? TimeSpan.FromSeconds(60) : TimeSpan.FromMilliseconds(300));
        var outcome = await Record.ExceptionAsync(async () => await engine.RunAsync(spec, caller.Token));
        var returnedAt = clock.Elapsed;
        await stopWatching.CancelAsync();
        Assert.True(await ended, "the fake engine's start never wrote its start line");
        return (outcome, returnedAt);
    }

    /// <summary>
    /// Polls <paramref name="markerFile"/> on a dedicated thread every 5 ms and calls
    /// <paramref name="end"/> once, as soon as a <c>start</c> line is there; true if it
    /// did, false if <paramref name="stop"/> was cancelled first. A dedicated thread, like
    /// <see cref="WhenKillLineAppearsAsync"/>'s, so thread-pool starvation cannot delay it.
    /// </summary>
    private static Task<bool> WhenStartLineAppearsAsync(string markerFile, Action end, CancellationToken stop) =>
        Task.Factory.StartNew(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (ReadMarkerLines(markerFile).Any(static line => FirstToken(line) == "start"))
                    {
                        end();
                        return true;
                    }

                    Thread.Sleep(5);
                }

                return false;
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>
    /// A <see cref="TimeProvider"/> whose timers never fire on their own, only when
    /// <see cref="Fire"/> is called: the run's timeout then expires exactly when a test
    /// says so. Hand-written rather than <c>Microsoft.Extensions.TimeProvider.Testing</c>'s
    /// <c>FakeTimeProvider</c>, which this project does not reference: only
    /// <see cref="CreateTimer"/> is needed.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        /// <summary>Fires, once, every timer created so far that is armed and not disposed.</summary>
        public void Fire()
        {
            ManualTimer[] timers;
            lock (_gate)
            {
                timers = [.. _timers];
            }

            foreach (var timer in timers)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private readonly Lock _gate = new();
            private TimeSpan _dueTime = dueTime;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _dueTime = dueTime;
                    return true;
                }
            }

            public void Fire()
            {
                lock (_gate)
                {
                    if (_disposed || _dueTime == global::System.Threading.Timeout.InfiniteTimeSpan)
                    {
                        return;
                    }

                    _dueTime = global::System.Threading.Timeout.InfiniteTimeSpan;
                }

                callback(state);
            }

            public void Dispose()
            {
                lock (_gate)
                {
                    _disposed = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>
    /// The arguments (after the subcommand) of every invocation of <paramref name="subcommand"/>
    /// the fake engine recorded, in order.
    /// </summary>
    private static List<string[]> Invocations(string markerFile, string subcommand) =>
        ReadMarkerLines(markerFile)
            .Select(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 0 && parts[0] == subcommand)
            .Select(static parts => parts[1..])
            .ToList();

    private static string FirstToken(string line) =>
        line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

    /// <summary>
    /// Counts the <c>kill</c> and <c>done</c> lines of one snapshot of the marker file.
    /// Each line is trimmed before it is compared: cmd's <c>echo kill &gt;&gt; file</c>
    /// keeps the space before the redirection as part of the line.
    /// </summary>
    private static (int Kills, int Dones) ReadMarker(string markerFile)
    {
        var lines = ReadMarkerLines(markerFile);
        return (
            lines.Count(static line => FirstToken(line) == "kill"),
            lines.Count(static line => FirstToken(line) == "done"));
    }

    /// <summary>
    /// One snapshot of the marker file's lines; none while it does not exist. It is
    /// opened sharing read, write and delete, so a fake-engine child that appends while
    /// it is read (<c>&gt;&gt;</c> in cmd or sh) can still open it -- with
    /// <see cref="File.ReadAllLines(string)"/>'s read-only sharing, cmd's append could
    /// fail and its line be lost. A read that still collides is retried rather than
    /// failing the test.
    /// </summary>
    private static string[] ReadMarkerLines(string markerFile)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(markerFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var lines = new List<string>();
                while (reader.ReadLine() is { } line)
                {
                    lines.Add(line);
                }

                return [.. lines];
            }
            catch (FileNotFoundException)
            {
                return [];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>
    /// Polls <paramref name="markerFile"/> on a dedicated thread every 5 ms and returns the
    /// <paramref name="clock"/> reading at which the first <c>kill</c> line was seen, or
    /// <c>null</c> if none had appeared by the time <paramref name="stop"/> was cancelled
    /// (one last read is made after that). The reading can only be late, never early: the
    /// line is written immediately before the child starts its sleep and is seen some
    /// milliseconds later, so a window measured from it is never longer than the real one
    /// -- poll lag can hide a regression on one run, never fail correct code.
    /// </summary>
    private static Task<TimeSpan?> WhenKillLineAppearsAsync(string markerFile, Stopwatch clock, CancellationToken stop) =>
        Task.Factory.StartNew(
            () =>
            {
                while (true)
                {
                    var stopping = stop.IsCancellationRequested;
                    if (ReadMarker(markerFile).Kills > 0)
                    {
                        return (TimeSpan?)clock.Elapsed;
                    }

                    if (stopping)
                    {
                        return null;
                    }

                    Thread.Sleep(5);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private enum KillMode
    {
        /// <summary>
        /// Sleeps <see cref="HungKillSeconds"/>, then writes a <c>done</c> marker line and
        /// exits non-zero -- the unresponsive-daemon case, which teardown must cut off
        /// before that line is written.
        /// </summary>
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
    /// <c>start</c> (anything not named below) appends a <c>start</c> line to
    /// <paramref name="markerFile"/> (see <see cref="EndRunOnceStartedAsync"/>), then
    /// always hangs for <paramref name="runHangSeconds"/> (so
    /// <see cref="CliContainerEngine.RunAsync"/> genuinely times out and proceeds to
    /// teardown), while <c>kill</c> appends one <c>kill</c> line to
    /// <paramref name="markerFile"/> -- so a test can count how many kill attempts
    /// actually ran -- and then behaves per <paramref name="killMode"/>. <c>create</c>
    /// takes <paramref name="createSeconds"/> (none by default) and exits 0, writing a
    /// <c>create-begin</c> and a <c>create-done</c> line around the wait so a test can
    /// order it against the teardown commands. With <paramref name="createDescendantSeconds"/>,
    /// <c>create</c> first starts a background process that lives that long and inherits
    /// its stdout and stderr, so both pipes stay open after the <c>create</c> client exits.
    /// </summary>
    private static string DispatchingEngine(TempDir tmp, string markerFile, int runHangSeconds, KillMode killMode, int rmExitCode = 1, int inspectExitCode = 1, string inspectStderr = "Error: No such object", int createSeconds = 0, int createDescendantSeconds = 0)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var killBody = killMode switch
            {
                KillMode.Hang => $"ping -n {HungKillSeconds + 1} 127.0.0.1 >nul\r\necho done >> \"{markerFile}\"\r\nexit /b 1\r\n",
                KillMode.SlowFail => "ping -n 1 -w 750 192.0.2.1 >nul\r\nexit /b 1\r\n",
                KillMode.Succeed => "exit /b 0\r\n",
                _ => "exit /b 1\r\n",
            };
            return tmp.Write(
                "engine.cmd",
                "@echo off\r\n"
                + "if \"%1\"==\"kill\" goto kill\r\n"
                + "if \"%1\"==\"rm\" goto rm\r\n"
                + "if \"%1\"==\"inspect\" goto inspect\r\n"
                + "if \"%1\"==\"create\" goto create\r\n"
                + $"echo start >> \"{markerFile}\"\r\n"
                + $"ping -n {runHangSeconds + 1} 127.0.0.1 >nul\r\n"
                + "exit /b 0\r\n"
                + ":create\r\n"
                + $"echo create-begin >> \"{markerFile}\"\r\n"
                // No redirection: the background ping inherits the script's stdout and
                // stderr, i.e. the create client's pipes.
                + (createDescendantSeconds > 0 ? $"start \"\" /b ping -n {createDescendantSeconds + 1} 127.0.0.1\r\n" : "")
                + (createSeconds > 0 ? $"ping -n {createSeconds + 1} 127.0.0.1 >nul\r\n" : "")
                + $"echo create-done >> \"{markerFile}\"\r\n"
                + "exit /b 0\r\n"
                + ":rm\r\n"
                + $"echo %* >> \"{markerFile}\"\r\n"
                + $"exit /b {rmExitCode}\r\n"
                + ":inspect\r\n"
                + $"echo %* >> \"{markerFile}\"\r\n"
                + $"echo {inspectStderr}: %2 1>&2\r\n"
                + $"exit /b {inspectExitCode}\r\n"
                + ":kill\r\n"
                + $"echo kill %2 >> \"{markerFile}\"\r\n"
                + killBody);
        }

        var killBodySh = killMode switch
        {
            KillMode.Hang => $"sleep {HungKillSeconds}\n  echo done >> '{markerFile}'\n  exit 1\n",
            KillMode.SlowFail => "sleep 0.75\nexit 1\n",
            KillMode.Succeed => "exit 0\n",
            _ => "exit 1\n",
        };
        var path = tmp.Write(
            "engine.sh",
            "#!/bin/sh\n"
            + "if [ \"$1\" = \"create\" ]; then\n"
            + $"  echo create-begin >> '{markerFile}'\n"
            + (createDescendantSeconds > 0 ? $"  sleep {createDescendantSeconds} &\n" : "")
            + (createSeconds > 0 ? $"  sleep {createSeconds}\n" : "")
            + $"  echo create-done >> '{markerFile}'\n"
            + "  exit 0\n"
            + "elif [ \"$1\" = \"rm\" ]; then\n"
            + $"  echo \"$*\" >> '{markerFile}'\n"
            + $"  exit {rmExitCode}\n"
            + "elif [ \"$1\" = \"inspect\" ]; then\n"
            + $"  echo \"$*\" >> '{markerFile}'\n"
            + $"  echo \"{inspectStderr}: $2\" >&2\n"
            + $"  exit {inspectExitCode}\n"
            + "elif [ \"$1\" = \"kill\" ]; then\n"
            + $"  echo \"kill $2\" >> '{markerFile}'\n"
            + $"  {killBodySh}"
            + "else\n"
            + $"  echo start >> '{markerFile}'\n"
            + $"  sleep {runHangSeconds}\n"
            + "fi\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static MemoryStream StreamOver(string text) =>
        new(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// An "engine" whose <c>start</c> copies <paramref name="bytes"/> verbatim to stdout or
    /// stderr and exits with <paramref name="exitCode"/>. The bytes live in a file the
    /// script copies (<c>type</c>/<c>cat</c>) rather than in the script text, so no shell
    /// quoting stands between them and the pipe. <c>create</c> exits 0, and the teardown a
    /// non-zero <c>start</c> triggers is answered at once as a container already gone
    /// would be (<c>kill</c> 1, <c>rm</c> 0, <c>inspect</c> 1 with "No such object"), so
    /// it costs two rounds rather than the whole budget.
    /// </summary>
    private static string ByteWritingEngine(TempDir tmp, byte[] bytes, bool toStderr, int exitCode)
    {
        var payload = Path.Combine(tmp.Path, "payload.bin");
        File.WriteAllBytes(payload, bytes);
        var redirect = toStderr ? " 1>&2" : "";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return tmp.Write(
                "engine.cmd",
                "@echo off\r\n"
                + "if \"%1\"==\"create\" exit /b 0\r\n"
                + "if \"%1\"==\"kill\" exit /b 1\r\n"
                + "if \"%1\"==\"rm\" exit /b 0\r\n"
                + "if \"%1\"==\"inspect\" goto inspect\r\n"
                + $"type \"{payload}\"{redirect}\r\n"
                + $"exit /b {exitCode}\r\n"
                + ":inspect\r\n"
                + "echo Error: No such object: %2 1>&2\r\n"
                + "exit /b 1\r\n");
        }

        var path = tmp.Write(
            "engine.sh",
            "#!/bin/sh\n"
            + "[ \"$1\" = \"create\" ] && exit 0\n"
            + "[ \"$1\" = \"kill\" ] && exit 1\n"
            + "[ \"$1\" = \"rm\" ] && exit 0\n"
            + "if [ \"$1\" = \"inspect\" ]; then echo \"Error: No such object: $2\" >&2; exit 1; fi\n"
            + $"cat '{payload}'{redirect}\n"
            + $"exit {exitCode}\n");
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

/// <summary>
/// Runs <see cref="CliContainerEngineRunTests"/> with no other test alongside it: xunit
/// executes a collection that disables parallelization on its own, after all the
/// parallel ones. See that class for why.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliContainerEngineTeardownTimingCollection
{
    /// <summary>The collection's name, shared by the definition and the test class.</summary>
    public const string Name = "CliContainerEngine teardown timing";
}
