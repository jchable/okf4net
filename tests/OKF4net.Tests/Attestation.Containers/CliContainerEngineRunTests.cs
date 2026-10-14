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
    /// promised into a hang. The "engine" here always hangs on <c>run</c> (so the
    /// outer run genuinely times out) and hangs on <c>kill</c> too, standing in for
    /// exactly that unresponsive daemon. Its <c>kill</c> writes a <c>kill</c> line to a
    /// marker file as it starts, sleeps <see cref="HungKillSeconds"/>, then writes a
    /// <c>done</c> line -- a line only a <c>kill</c> child that was left to run out its
    /// hang can write. The <c>kill</c> lines let this test assert no retry was attempted
    /// -- a kill that already hung once will not answer a second time either, and the
    /// caller is already past its own deadline.
    /// <para>
    /// The clock starts at the hang (#149). This test used to time the whole
    /// <see cref="CliContainerEngine.RunAsync"/> call against a fixed 4 s, so that one
    /// number had to absorb spawning the <c>run</c> process, the 300 ms timeout,
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
    /// <c>kill</c> tree, killing the <c>run</c> tree, draining output -- is latency.
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
    /// teardown that waits out the 20 s <c>run</c> hang (one that stopped killing the
    /// <c>run</c> tree). Correct runs take 1.3-1.8 s; 8 s is close to twice the
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
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, killMode: KillMode.Hang))
        {
            TeardownBudget = HungKillBudget,
        };
        var clock = Stopwatch.StartNew();
        using var stopWatching = new CancellationTokenSource();
        var killLineSeen = WhenKillLineAppearsAsync(marker, clock, stopWatching.Token);

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await engine.RunAsync(Spec(TimeSpan.FromMilliseconds(300))));
        var returnedAt = clock.Elapsed;
        await stopWatching.CancelAsync();
        var killStartedAt = await killLineSeen;

        Assert.Contains("exceeded its timeout", ex.Message);
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
    }

    /// <summary>
    /// The other half of the same budget: a <c>kill</c> that answers -- slowly, and
    /// with failure -- must still get its retry, because the retry exists for a
    /// container whose creation was still in flight, not only for a dead engine. The
    /// "engine" here targets roughly 750 ms then exits non-zero on every <c>kill</c>
    /// call (a real <c>sleep 0.75</c> on POSIX; an approximate <c>ping</c>-based wait
    /// on Windows that in practice can run notably shorter, measured 275-485 ms) and
    /// records one marker line per call, so this test can assert the retry actually
    /// happened (two lines).
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
    /// <item><b>Exactly two <c>kill</c> lines</b> -- the guarantee itself. Catches a
    /// retry that was dropped or made to need more budget than a slow <c>kill</c>
    /// leaves, and a third attempt. It fails correct code only if the first attempt
    /// takes over 4.5 s (the 5 s budget less <c>RetryThreshold</c>). Even in the
    /// starved runs described on the class, where the whole run took up to 9.5 s,
    /// every run made both attempts.</item>
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
    public async Task A_slow_failing_kill_still_gets_retried_within_the_budget()
    {
        EnsureThreadPoolHeadroom();
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "kill-calls.txt");
        var budget = TimeSpan.FromSeconds(5);
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, killMode: KillMode.SlowFail))
        {
            TeardownBudget = budget,
        };
        var clock = Stopwatch.StartNew();
        using var stopWatching = new CancellationTokenSource();
        var killLineSeen = WhenKillLineAppearsAsync(marker, clock, stopWatching.Token);

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await engine.RunAsync(Spec(TimeSpan.FromMilliseconds(30))));
        var returnedAt = clock.Elapsed;
        await stopWatching.CancelAsync();
        var firstKill = await killLineSeen;

        Assert.Contains("exceeded its timeout", ex.Message);
        Assert.Equal(2, ReadMarker(marker).Kills);
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

    /// <summary>
    /// Counts the <c>kill</c> and <c>done</c> lines of one snapshot of the marker file.
    /// Each line is trimmed before it is compared: cmd's <c>echo kill &gt;&gt; file</c>
    /// keeps the space before the redirection as part of the line. A read that collides
    /// with a child still appending is retried rather than failing the test.
    /// </summary>
    private static (int Kills, int Dones) ReadMarker(string markerFile)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(markerFile))
                {
                    return (0, 0);
                }

                var lines = File.ReadAllLines(markerFile);
                return (
                    lines.Count(static line => line.Trim() == "kill"),
                    lines.Count(static line => line.Trim() == "done"));
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
    }

    /// <summary>
    /// An "engine" that dispatches on its first argument the way a real one does:
    /// <c>run</c> always hangs for <paramref name="runHangSeconds"/> (so
    /// <see cref="CliContainerEngine.RunAsync"/> genuinely times out and proceeds to
    /// teardown), while <c>kill</c> appends one <c>kill</c> line to
    /// <paramref name="markerFile"/> -- so a test can count how many kill attempts
    /// actually ran -- and then behaves per <paramref name="killMode"/>.
    /// </summary>
    private static string DispatchingEngine(TempDir tmp, string markerFile, int runHangSeconds, KillMode killMode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var killBody = killMode == KillMode.Hang
                ? $"ping -n {HungKillSeconds + 1} 127.0.0.1 >nul\r\necho done >> \"{markerFile}\"\r\nexit /b 1\r\n"
                : "ping -n 1 -w 750 192.0.2.1 >nul\r\nexit /b 1\r\n";
            return tmp.Write(
                "engine.cmd",
                "@echo off\r\n"
                + "if \"%1\"==\"kill\" goto kill\r\n"
                + $"ping -n {runHangSeconds + 1} 127.0.0.1 >nul\r\n"
                + "exit /b 0\r\n"
                + ":kill\r\n"
                + $"echo kill >> \"{markerFile}\"\r\n"
                + killBody);
        }

        var killBodySh = killMode == KillMode.Hang
            ? $"sleep {HungKillSeconds}\n  echo done >> '{markerFile}'\n  exit 1\n"
            : "sleep 0.75\nexit 1\n";
        var path = tmp.Write(
            "engine.sh",
            "#!/bin/sh\n"
            + "if [ \"$1\" = \"kill\" ]; then\n"
            + $"  echo kill >> '{markerFile}'\n"
            + $"  {killBodySh}"
            + "else\n"
            + $"  sleep {runHangSeconds}\n"
            + "fi\n");
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
