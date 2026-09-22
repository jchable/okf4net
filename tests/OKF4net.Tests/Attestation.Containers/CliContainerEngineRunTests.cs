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
    /// invocation to a marker file so this test can also assert no retry was
    /// attempted -- a kill that already hung once will not answer a second time
    /// either, and the caller is already past its own deadline.
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
        Assert.Equal(1, CountMarkerLines(marker));
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
    /// <see cref="CliContainerEngine.TeardownBudget"/> is shrunk here, like the sibling
    /// <see cref="A_hung_engine_kill_does_not_hang_the_timed_out_run"/>, rather than
    /// asserting against its 3 s production default -- that default left the assertion
    /// below only 0.5 s of headroom for process-startup and scheduling overhead, and it
    /// failed for real on a loaded windows-latest CI runner (3.59 s against a 3.5 s
    /// bound). <c>budget</c> below is a local variable, not the production constant,
    /// and the bound is computed FROM it (<c>budget</c> plus a fixed slack), so the two
    /// stay linked instead of drifting apart the way the literal 3.5 s did. 2 s of
    /// budget is comfortably above what the two ~750 ms attempts plus the 250 ms delay
    /// between them need (well over the 500 ms <c>RetryThreshold</c> remains after the
    /// first attempt, so the retry reliably fires) while still being far smaller than
    /// the production default; the 3 s of slack on top matches the sibling's headroom
    /// and was confirmed empirically (20 local runs, see the flaky-teardown-fix report)
    /// rather than picked by guesswork.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_slow_failing_kill_still_gets_retried_within_the_budget()
    {
        using var tmp = new TempDir();
        var marker = System.IO.Path.Combine(tmp.Path, "kill-calls.txt");
        var budget = TimeSpan.FromSeconds(2);
        var engine = new CliContainerEngine(DispatchingEngine(tmp, marker, runHangSeconds: 20, killMode: KillMode.SlowFail))
        {
            TeardownBudget = budget,
        };
        var clock = Stopwatch.StartNew();

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await engine.RunAsync(Spec(TimeSpan.FromMilliseconds(30))));

        var bound = budget + TimeSpan.FromSeconds(3);
        Assert.Contains("exceeded its timeout", ex.Message);
        Assert.True(clock.Elapsed < bound, $"the timed-out run took {clock.Elapsed} against a bound of {bound} (budget {budget})");
        Assert.Equal(2, CountMarkerLines(marker));
    }

    private static int CountMarkerLines(string markerFile) =>
        File.Exists(markerFile)
            ? File.ReadAllLines(markerFile).Count(static line => line.Length > 0)
            : 0;

    private enum KillMode
    {
        /// <summary>Hangs for 20 s and never exits on its own -- the unresponsive-daemon case.</summary>
        Hang,

        /// <summary>Targets roughly 750 ms then exits non-zero -- the slow-but-responsive, still-failing case.</summary>
        SlowFail,
    }

    /// <summary>
    /// An "engine" that dispatches on its first argument the way a real one does:
    /// <c>run</c> always hangs for <paramref name="runHangSeconds"/> (so
    /// <see cref="CliContainerEngine.RunAsync"/> genuinely times out and proceeds to
    /// teardown), while <c>kill</c> appends one line to <paramref name="markerFile"/>
    /// -- so a test can count how many kill attempts actually ran -- and then behaves
    /// per <paramref name="killMode"/>.
    /// </summary>
    private static string DispatchingEngine(TempDir tmp, string markerFile, int runHangSeconds, KillMode killMode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var killBody = killMode == KillMode.Hang
                ? "ping -n 21 127.0.0.1 >nul\r\nexit /b 1\r\n"
                : "ping -n 1 -w 750 192.0.2.1 >nul\r\nexit /b 1\r\n";
            return tmp.Write(
                "engine.cmd",
                "@echo off\r\n"
                + "if \"%1\"==\"kill\" goto kill\r\n"
                + $"ping -n {runHangSeconds + 1} 127.0.0.1 >nul\r\n"
                + "exit /b 0\r\n"
                + ":kill\r\n"
                + $"echo x >> \"{markerFile}\"\r\n"
                + killBody);
        }

        var killBodySh = killMode == KillMode.Hang
            ? "sleep 20\n"
            : "sleep 0.75\nexit 1\n";
        var path = tmp.Write(
            "engine.sh",
            "#!/bin/sh\n"
            + "if [ \"$1\" = \"kill\" ]; then\n"
            + $"  echo x >> '{markerFile}'\n"
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
