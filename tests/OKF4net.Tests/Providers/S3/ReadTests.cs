// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using OKF4net.Providers;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D8, §4.3 ReadAsync.</summary>
public class ReadTests
{
    private static async Task<byte[]> Bytes(BundleObject o)
    {
        using var ms = new MemoryStream();
        await o.Content.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task The_version_is_the_filesystem_providers_format_for_the_same_bytes()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "a.md"), "hello\n");
        var fs = await new FileSystemBundleProvider(tmp.Path).ReadAsync(BundleKey.Parse("a.md"), null, default);
        fs.Dispose();
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/a.md", "hello\n"))));
        using var o = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        Assert.Equal(fs.Version, o.Version);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("hello\n"u8)), o.Version.Token);
        Assert.Equal("hello\n"u8.ToArray(), await Bytes(o));
        Assert.False(o.Content.CanWrite);
        Assert.Equal(6, o.Length);
    }

    [Fact]
    public async Task An_expected_version_is_checked_on_the_bytes_not_the_etag()
    {
        // The ETag the ORIGINAL object had, served over other bytes: a store whose ETag does not follow the content.
        // A multipart-shaped ETag ("<hex>-<parts>"), which the SDK does not check against the body. (Measured: an ETag
        // that is not hex, "\"same\"", fails inside the SDK's own response handling with FormatException — Transport;
        // a plain MD5 ETag is checked by the SDK itself, next test.)
        var etag = "\"" + Convert.ToHexStringLower(MD5.HashData("original"u8)) + "-2\"";
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("other", null, "text/plain"), Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag) } }) };
        using var p = S3Test.Provider(fake);
        var expected = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData("original"u8)));
        await Assert.ThrowsAsync<BundleVersionMismatchException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), expected, default));
        Assert.DoesNotContain(fake.Requests, r => r.Headers.IfMatch.Count > 0); // the store's If-Match is not trusted, nor sent
        using var unpinned = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("other"u8)), unpinned.Version.Token); // the bytes', never the ETag's
    }

    [Fact]
    public async Task A_plain_MD5_ETag_over_other_bytes_is_refused_by_the_SDK_as_Integrity()
    {
        // Measured on AWSSDK.S3 4.1.0: the SDK compares a single-part (hex MD5) ETag with the MD5 of the body it reads.
        var etag = S3Test.ETag("original"u8.ToArray());
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("other", null, "text/plain"), Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag) } }) };
        using var p = S3Test.Provider(fake);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.Equal(S3FailureKind.Integrity, e.Kind);
    }

    [Fact]
    public async Task An_expected_version_equal_to_the_bytes_is_served()
    {
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/a.md", "original"))));
        var expected = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData("original"u8)));
        using var o = await p.ReadAsync(BundleKey.Parse("a.md"), expected, default);
        Assert.Equal(expected, o.Version);
        Assert.Equal("original"u8.ToArray(), await Bytes(o));
    }

    [Fact]
    public async Task A_declared_length_past_the_bound_is_refused_without_reading_the_body()
    {
        var read = false;
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ObservingStream(() => read = true)) };
                r.Content.Headers.ContentLength = 11;
                return Task.FromResult(r);
            },
        };
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxObjectBytes = 10 });
        await Assert.ThrowsAsync<BundleObjectTooLargeException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.False(read);
    }

    /// <summary>A body far larger than any buffer the SDK or HttpClient could fill ahead of the provider's reads.</summary>
    private const int HugeBody = 4 * 1024 * 1024;

    /// <summary>
    /// The read-ahead the read-bound tests allow past the provider's own B + 1 bytes: one 64 KiB chunk, for a later SDK
    /// or HttpClient that buffers. Measured on AWSSDK.S3 4.1.0 through the real pipeline: exactly 11 bytes (B + 1, B = 10)
    /// read from a 4 MiB body, with and without Content-Length — no read-ahead at all. Far below <see cref="HugeBody"/>,
    /// so a reader that drains the body before bounding it fails the assertion (fix round 1: mutation shown RED).
    /// </summary>
    private const int ReadAheadAllowance = 64 * 1024;

    [Fact]
    public async Task A_body_longer_than_its_bound_without_length_is_refused_at_the_bound()
    {
        // A NON-seekable stream: StreamContent cannot compute a length, so no Content-Length reaches the SDK.
        var stream = new NonSeekable(new byte[HugeBody]);
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }) };
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxObjectBytes = 10 });
        await Assert.ThrowsAsync<BundleObjectTooLargeException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.True(stream.BytesRead <= 11 + ReadAheadAllowance, $"read {stream.BytesRead} of {HugeBody} bytes"); // never the whole body
    }

    [Fact]
    public async Task A_body_exactly_at_the_bound_without_length_is_accepted()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekable(new byte[10])) }) };
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxObjectBytes = 10 });
        using var o = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        Assert.Equal(10, o.Length);
    }

    [Theory]
    [InlineData(10, 10)] // at the bound, with its length
    [InlineData(1, 1)]   // the smallest
    [InlineData(0, 0)]   // empty
    public async Task A_body_within_the_bound_with_its_length_is_read_whole(int length, int expected)
    {
        var bytes = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }) };
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxObjectBytes = 10 });
        using var o = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        Assert.Equal(expected, o.Length);
        Assert.Equal(bytes, await Bytes(o));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), o.Version.Token);
    }

    [Fact]
    public async Task A_body_longer_than_its_declared_length_is_refused_at_the_bound()
    {
        // HTTP/1.1 framing makes this impossible on the wire; under the guard it still never passes MaxObjectBytes.
        var stream = new NonSeekable(new byte[HugeBody]);
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
                r.Content.Headers.ContentLength = 5;
                return Task.FromResult(r);
            },
        };
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxObjectBytes = 10 });
        await Assert.ThrowsAsync<BundleObjectTooLargeException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.True(stream.BytesRead <= 11 + ReadAheadAllowance, $"read {stream.BytesRead} of {HugeBody} bytes");
    }

    [Fact]
    public async Task A_body_shorter_than_its_declared_length_is_never_served()
    {
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekable(new byte[5])) };
                r.Content.Headers.ContentLength = 8;
                return Task.FromResult(r);
            },
        };
        using var p = S3Test.Provider(fake);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.Equal(S3FailureKind.Transport, e.Kind); // a truncated body, as HttpClient reports one over the wire
    }

    [Fact]
    public async Task The_original_body_is_released_after_a_read_and_after_a_refusal()
    {
        var served = new List<TrackedStream>();
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var s = new TrackedStream(new byte[20]);
                lock (served)
                {
                    served.Add(s);
                }

                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(s) };
                r.Content.Headers.ContentLength = 20;
                return Task.FromResult(r);
            },
        };
        using (var p = S3Test.Provider(fake))
        {
            using var o = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        }

        using (var p = S3Test.Provider(fake, S3Test.Local() with { MaxObjectBytes = 10 }))
        {
            await Assert.ThrowsAsync<BundleObjectTooLargeException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        }

        Assert.Equal(2, served.Count);
        Assert.All(served, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task A_stream_that_ignores_the_token_and_only_yields_to_disposal_is_a_Timeout()
    {
        var deaf = new DeafUntilDisposed();
        var clock = new ManualClock();
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(deaf) }) };
        using var p = new S3BundleProvider(S3Test.Creds, S3Test.Local() with { RequestTimeout = TimeSpan.FromSeconds(30) }, fake, clock);
        var call = p.ReadAsync(BundleKey.Parse("a.md"), null, default).AsTask();
        await Task.WhenAny(deaf.Reading.Task, call).WaitAsync(TimeSpan.FromSeconds(30)); // the body read has started
        Assert.True(deaf.Reading.Task.IsCompleted, call.Exception?.InnerException?.GetType().Name ?? "the read ended before its body");
        clock.Advance(TimeSpan.FromSeconds(30)); // the deadline fires inside the body read
        // Bounded: without the disposal on the deadline, the read would never end (a red test, never a hung run).
        var e = await Assert.ThrowsAsync<S3RequestException>(() => call.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(S3FailureKind.Timeout, e.Kind); // not Transport, though the read ended with ObjectDisposedException
    }

    [Fact]
    public async Task A_failed_read_then_a_failed_disposal_reports_the_read()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailsReadAndDispose(new IOException("reset"), new Amazon.Runtime.AmazonClientException("Expected hash not equal to calculated hash"))) }) };
        using var p = S3Test.Provider(fake);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.Equal(S3FailureKind.Transport, e.Kind); // the read's failure, first; the disposal's never masks it
    }

    [Fact]
    public async Task A_bound_then_a_failed_disposal_reports_the_bound()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailsReadAndDispose(null, new IOException("dispose"), length: 100)) }) };
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxObjectBytes = 10 });
        await Assert.ThrowsAsync<BundleObjectTooLargeException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
    }

    [Fact]
    public async Task A_failed_release_still_returns_the_permit()
    {
        var first = 1;
        var fake = new FakeS3Handler
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Interlocked.Exchange(ref first, 0) == 1
                    ? new StreamContent(new FailsReadAndDispose(new IOException("reset"), new IOException("dispose")))
                    : new ByteArrayContent("x"u8.ToArray()),
            }),
        };
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxConcurrentRequests = 1 });
        await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        using var o = await p.ReadAsync(BundleKey.Parse("a.md"), null, default).AsTask().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("x"u8.ToArray(), await Bytes(o));
    }

    [Fact]
    public async Task A_wrong_response_checksum_is_Integrity_through_the_real_pipeline()
    {
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("hello"u8.ToArray()) };
                r.Headers.TryAddWithoutValidation("x-amz-checksum-crc32", "AAAAAA=="); // not the CRC32 of "hello"
                return Task.FromResult(r);
            },
        };
        using var p = S3Test.Provider(fake);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.Equal(S3FailureKind.Integrity, e.Kind);
    }

    [Fact]
    public async Task A_right_response_checksum_is_served()
    {
        var crc = Convert.ToBase64String(BitConverter.IsLittleEndian ? BitConverter.GetBytes(System.IO.Hashing.Crc32.HashToUInt32("hello"u8)).Reverse().ToArray() : BitConverter.GetBytes(System.IO.Hashing.Crc32.HashToUInt32("hello"u8)));
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("hello"u8.ToArray()) };
                r.Headers.TryAddWithoutValidation("x-amz-checksum-crc32", crc);
                return Task.FromResult(r);
            },
        };
        using var p = S3Test.Provider(fake);
        using var o = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        Assert.Equal("hello"u8.ToArray(), await Bytes(o));
    }

    [Fact]
    public async Task A_credentials_provider_that_blocks_fails_a_read_as_Credentials()
    {
        using var never = new ManualResetEventSlim();
        var called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new ManualClock();
        var fake = S3Test.Serving(S3Test.Objects(("kb/a.md", "x")));
        using var p = new S3BundleProvider(
            new CountingCredentials(() => { called.TrySetResult(); never.Wait(); return null!; }), S3Test.Local() with { RequestTimeout = TimeSpan.FromSeconds(30) }, fake, clock);
        try
        {
            var call = p.ReadAsync(BundleKey.Parse("a.md"), null, default).AsTask();
            await called.Task.WaitAsync(TimeSpan.FromSeconds(30)); // the host's provider is blocking now
            clock.Advance(TimeSpan.FromSeconds(30));
            var e = await Assert.ThrowsAsync<S3RequestException>(() => call.WaitAsync(TimeSpan.FromSeconds(60)));
            Assert.Equal(S3FailureKind.Credentials, e.Kind); // through the public member: not Timeout
            Assert.Empty(fake.Requests); // nothing sent without a snapshot
        }
        finally
        {
            never.Set();
        }
    }

    [Fact]
    public async Task A_307_on_the_same_authority_re_signs_with_the_snapshot_never_the_host_provider()
    {
        var calls = 0;
        var host = new CountingCredentials(() => Interlocked.Increment(ref calls) == 1 ? new ImmutableCredentials("A", "S", "T") : throw new InvalidOperationException("called from the pipeline"));
        var sent = 0;
        var fake = new FakeS3Handler
        {
            Respond = (req, _) => Task.FromResult(Interlocked.Increment(ref sent) == 1
                ? FakeS3Handler.Redirect(HttpStatusCode.TemporaryRedirect, req.RequestUri!.ToString()) // the same URL: admitted by the guard
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("x"u8.ToArray()) }),
        };
        // A Region source: on an IP ServiceUrl the SDK refuses any redirect with ArgumentException (gate G4).
        using var p = S3Test.Provider(fake, new S3BundleSource { Bucket = "demo", Prefix = "kb", Region = Amazon.RegionEndpoint.EUWest1, RequestTimeout = TimeSpan.FromSeconds(60) }, host);
        using var o = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.All(fake.Requests, r => Assert.Contains("Credential=A/", string.Join(",", r.Headers.GetValues("Authorization")) /* gate: the SDK adds it without validation; Headers.Authorization stays null */, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Never_more_than_MaxConcurrentRequests_mixed_operations_bodies_included()
    {
        var inFlight = 0;
        var peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serving = S3Test.Serving(S3Test.Objects(("kb/a.md", "x"), ("kb/b.md", "y")));
        var fake = new FakeS3Handler
        {
            Respond = async (req, ct) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                int seen;
                while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref inFlight);
                return await serving.Respond(req, ct);
            },
        };
        // A long deadline: the queued operations' deadlines start at the call (spec §4.5), and a loaded machine is slow.
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxConcurrentRequests = 2, RequestTimeout = TimeSpan.FromSeconds(60) });
        var ops = new List<Task>
        {
            p.ReadAsync(BundleKey.Parse("a.md"), null, default).AsTask(),
            p.ReadAsync(BundleKey.Parse("b.md"), null, default).AsTask(),
            p.ExistsAsync(BundleKey.Parse("a.md"), default).AsTask(),
            p.GetVersionAsync(BundleLimits.Default, default).AsTask(),
            p.ReadAsync(BundleKey.Parse("a.md"), null, default).AsTask(),
        };
        await WaitUntil(() => Volatile.Read(ref peak) == 2); // two in flight, however slowly they got there
        await Task.Delay(300);                               // and no third joins them while they are held
        Assert.Equal(2, Volatile.Read(ref peak));
        release.SetResult();
        await Task.WhenAll(ops).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, Volatile.Read(ref peak));
    }

    [Fact]
    public async Task A_suspended_body_holds_its_permit()
    {
        // The permit is held to the end of the body (spec D16): a read whose body stalls keeps the second one waiting.
        var stalled = new StallingStream(1);
        var first = true;
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                Stream body = first ? stalled : new MemoryStream("y"u8.ToArray());
                first = false;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
            },
        };
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxConcurrentRequests = 1 });
        using var cts = new CancellationTokenSource();
        var stuck = p.ReadAsync(BundleKey.Parse("a.md"), null, cts.Token).AsTask();
        await WaitUntil(() => fake.Requests.Count == 1);
        var second = p.ReadAsync(BundleKey.Parse("b.md"), null, default).AsTask();
        await Task.Delay(200);
        Assert.False(second.IsCompleted);
        Assert.Single(fake.Requests);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stuck.WaitAsync(TimeSpan.FromSeconds(60)));
        using var o = await second.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("y"u8.ToArray(), await Bytes(o));
    }

    [Fact]
    public async Task Each_provider_uses_and_disposes_only_its_own_HttpClient()
    {
        var a = new CountingHandler(S3Test.Serving(S3Test.Objects(("kb/a.md", "x"))));
        var b = new CountingHandler(S3Test.Serving(S3Test.Objects(("kb/a.md", "y"))));
        var pa = new S3BundleProvider(S3Test.Creds, S3Test.Local(), a);
        var pb = new S3BundleProvider(S3Test.Creds, S3Test.Local(), b);
        using (var o = await pa.ReadAsync(BundleKey.Parse("a.md"), null, default)) { }
        await Assert.ThrowsAnyAsync<Exception>(async () => await pa.ReadAsync(BundleKey.Parse("missing.md"), null, default)); // an error
        using (var o = await pa.ReadAsync(BundleKey.Parse("a.md"), null, default)) { }        // the same client still serves
        Assert.Equal(3, a.Sends);
        Assert.Equal(0, b.Sends);
        Assert.Equal(0, a.Disposals);
        pa.Dispose();
        pa.Dispose();
        Assert.Equal(1, a.Disposals);
        Assert.Equal(0, b.Disposals);
        pb.Dispose();
    }

    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int Sends;
        public int Disposals;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Sends);
            return base.SendAsync(request, ct);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Interlocked.Increment(ref Disposals);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public long BytesRead;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = _inner.Read(buffer, offset, count); Interlocked.Add(ref BytesRead, n); return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed;

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Ignores every token; a read blocks until the stream is disposed, then throws ObjectDisposedException.</summary>
    private sealed class DeafUntilDisposed : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when a read has started waiting.</summary>
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Reading.TrySetResult();
            await _disposed.Task; // deliberately not WaitAsync(ct)
            throw new ObjectDisposedException(nameof(DeafUntilDisposed));
        }
        protected override void Dispose(bool disposing) { _disposed.TrySetResult(); base.Dispose(disposing); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Gives <paramref name="length"/> bytes then fails with <paramref name="onRead"/> (or ends when null); its disposal throws <paramref name="onDispose"/>.</summary>
    private sealed class FailsReadAndDispose(Exception? onRead, Exception onDispose, int length = 0) : Stream
    {
        private int _left = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left > 0) { var n = Math.Min(_left, count); _left -= n; return n; }
            return onRead is null ? 0 : throw onRead;
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) throw onDispose; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_stalled_body_is_a_Timeout_within_the_deadline()
    {
        var stalling = new StallingStream(10);
        var clock = new ManualClock();
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stalling) }) };
        using var p = new S3BundleProvider(S3Test.Creds, S3Test.Local() with { RequestTimeout = TimeSpan.FromSeconds(30) }, fake, clock);
        var call = p.ReadAsync(BundleKey.Parse("a.md"), null, default).AsTask();
        await Task.WhenAny(stalling.Stalled.Task, call).WaitAsync(TimeSpan.FromSeconds(30)); // 10 bytes in, then nothing
        Assert.True(stalling.Stalled.Task.IsCompleted, call.Exception?.InnerException?.GetType().Name ?? "the read ended before its body stalled");
        clock.Advance(TimeSpan.FromSeconds(30));
        var e = await Assert.ThrowsAsync<S3RequestException>(() => call.WaitAsync(TimeSpan.FromSeconds(60))); // within the deadline, never a hang
        Assert.Equal(S3FailureKind.Timeout, e.Kind);
    }

    [Fact]
    public async Task A_caller_cancellation_stays_a_cancellation()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(10)) }) };
        using var p = S3Test.Provider(fake);
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.ReadAsync(BundleKey.Parse("a.md"), null, cts.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(60)));
    }

    /// <summary>
    /// A host's <c>catch (OperationCanceledException e) when (e.CancellationToken == ct)</c> must see its own token, whatever
    /// exit the cancellation took: waiting for a permit, resolving credentials, waiting for the headers, reading the body.
    /// </summary>
    [Theory]
    [InlineData("permit")]
    [InlineData("credentials")]
    [InlineData("headers")]
    [InlineData("body")]
    [InlineData("exists")]
    [InlineData("list")]
    public async Task A_caller_cancellation_carries_the_callers_own_token(string stage)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ImmutableCredentials>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeS3Handler
        {
            Respond = async (_, ct) =>
            {
                arrived.TrySetResult();
                if (stage == "body")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(1)) };
                }

                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        AWSCredentials creds = stage == "credentials" ? new PendingCredentials(pending.Task) : S3Test.Creds;
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxConcurrentRequests = 1, RequestTimeout = TimeSpan.FromMinutes(5) }, creds);
        Task? holder = null;
        if (stage == "permit")
        {
            holder = p.ReadAsync(BundleKey.Parse("held.md"), null, default).AsTask(); // holds the only permit
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        using var cts = new CancellationTokenSource();
        Task call = stage switch
        {
            "exists" => p.ExistsAsync(BundleKey.Parse("a.md"), cts.Token).AsTask(),
            "list" => Drain(p, cts.Token),
            _ => p.ReadAsync(BundleKey.Parse("a.md"), null, cts.Token).AsTask(),
        };
        if (stage is "headers" or "body" or "exists" or "list")
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await Task.Delay(50); // past the headers, into the body, for "body"
        }
        else
        {
            await Task.Delay(100);
        }

        Assert.False(call.IsCompleted);
        await cts.CancelAsync();
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(cts.Token, e.CancellationToken);
        pending.TrySetResult(new ImmutableCredentials("A", "S", null));
        p.Dispose();
        if (holder is not null)
        {
            await Assert.ThrowsAsync<S3RequestException>(() => holder.WaitAsync(TimeSpan.FromSeconds(60)));
        }
    }

    private static async Task Drain(S3BundleProvider p, CancellationToken ct)
    {
        await foreach (var _ in p.EnumerateAsync(null, BundleLimits.Default, ct))
        {
        }
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("body")]
    [InlineData("credentials")]
    [InlineData("permit")]
    public async Task A_provider_disposed_during_a_read_fails_it_as_Disposed(string stage)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<ImmutableCredentials>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeS3Handler
        {
            Respond = async (_, ct) =>
            {
                arrived.TrySetResult();
                if (stage == "body")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DeafUntilDisposed()) }; // only the disposal unblocks it
                }

                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            },
        };
        AWSCredentials creds = stage == "credentials" ? new PendingCredentials(pending.Task) : S3Test.Creds;
        var p = S3Test.Provider(fake, S3Test.Local() with { MaxConcurrentRequests = 1, RequestTimeout = TimeSpan.FromMinutes(5) }, creds);
        Task? holder = null;
        if (stage == "permit")
        {
            holder = p.ReadAsync(BundleKey.Parse("held.md"), null, default).AsTask();
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        var call = p.ReadAsync(BundleKey.Parse("a.md"), null, default).AsTask();
        if (stage is "headers" or "body")
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(60));
            await Task.Delay(50);
        }
        else
        {
            await Task.Delay(100);
        }

        Assert.False(call.IsCompleted);
        p.Dispose();
        var e = await Assert.ThrowsAsync<S3RequestException>(() => call.WaitAsync(TimeSpan.FromSeconds(60)));
        Assert.Equal(S3FailureKind.Disposed, e.Kind);
        Assert.Equal("S3 read failed: Disposed for 'a.md'", e.Message);
        if (holder is not null)
        {
            Assert.Equal(S3FailureKind.Disposed, (await Assert.ThrowsAsync<S3RequestException>(() => holder.WaitAsync(TimeSpan.FromSeconds(60)))).Kind);
        }

        pending.TrySetResult(new ImmutableCredentials("A", "S", null));
    }

    [Fact]
    public async Task A_provider_disposed_during_a_walk_fails_it_as_Disposed()
    {
        var objects = S3Test.Objects(("kb/a.md", "x"), ("kb/b.md", "y"));
        var p = S3Test.Provider(S3Test.Serving(objects, pageSize: 1));
        var e = await Assert.ThrowsAsync<S3RequestException>(async () =>
        {
            await foreach (var _ in p.EnumerateAsync(null, BundleLimits.Default, default))
            {
                p.Dispose(); // between two pages
            }
        });
        Assert.Equal(S3FailureKind.Disposed, e.Kind);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Retries_and_redirects_sign_with_the_operation_snapshot()
    {
        var calls = 0;
        var host = new CountingCredentials(() => Interlocked.Increment(ref calls) == 1 ? new ImmutableCredentials("A", "S", "T") : throw new InvalidOperationException("called from the pipeline"));
        var attempts = 0;
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(Interlocked.Increment(ref attempts) < 3 ? FakeS3Handler.Xml(HttpStatusCode.ServiceUnavailable, FakeS3Handler.Error("SlowDown")) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("x"u8.ToArray()) }) };
        using var p = S3Test.Provider(fake, creds: host);
        using var o = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        Assert.Equal(3, attempts);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.All(fake.Requests, r => Assert.Contains("Credential=A/", string.Join(",", r.Headers.GetValues("Authorization")) /* gate: the SDK adds it without validation; Headers.Authorization stays null */, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Credentials_are_resolved_once_per_read_outside_any_operation_context()
    {
        var seen = new List<OKF4net.Providers.S3.Internal.OperationContext?>();
        var creds = new CountingCredentials(() =>
        {
            lock (seen)
            {
                seen.Add(OKF4net.Providers.S3.Internal.OperationContext.Current.Value);
            }

            return new ImmutableCredentials("AKIDEXAMPLE", "secret", null);
        });
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/a.md", "x"))), creds: creds);
        using (await p.ReadAsync(BundleKey.Parse("a.md"), null, default)) { }
        using (await p.ReadAsync(BundleKey.Parse("a.md"), null, default)) { }
        Assert.Equal(2, seen.Count);
        Assert.All(seen, Assert.Null); // the host's code never runs inside the guard's context
        Assert.Null(OKF4net.Providers.S3.Internal.OperationContext.Current.Value);
    }

    [Fact]
    public async Task A_persistent_change_with_a_constant_triplet_is_not_reloaded_but_pinned_code_is_refused()
    {
        // Spec §4.3 limit 2, T2 "Fraîcheur": a durable replacement at the same size whose listing keeps the same ETag
        // (a store RustFS cannot be made to emulate) leaves the s3v1 token unchanged, so OnAccess does NOT reload; the
        // pinned re-read of the changed code is refused all the same, since the version is the SHA-256 of the bytes read.
        const string concept = "---\ntype: Attested Computation\ntitle: C\ndescription: D.\nruntime: bigquery\ncomputation: q.sql\n---\n";
        var original = S3Test.Objects(("kb/c.md", concept), ("kb/q.sql", "SELECT 1\n"));
        var served = new Dictionary<string, byte[]>(original, StringComparer.Ordinal);
        var listing = S3Test.Serving(original); // its listing triplets (key, ETag, size) never change
        var fake = new FakeS3Handler
        {
            Respond = (req, ct) =>
            {
                if (req.RequestUri!.Query.Contains("list-type=2", StringComparison.Ordinal))
                {
                    return listing.Respond(req, ct);
                }

                OKF4net.Providers.S3.Internal.S3KeyCodec.TryDecode(req.RequestUri.AbsolutePath, out var path);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served[path!["/okf/".Length..]]) });
            },
        };
        using var handle = new BundleHandle(S3Test.Provider(fake), refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = await handle.GetCurrentAsync();
        var file = first.ExecutableResources[ConceptId.Parse("c")].Computation;
        Assert.Equal(ExecutableResourceState.Pinned, file.State);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("SELECT 1\n"u8)), file.Version!.Value.Token);

        served["kb/q.sql"] = "SELECT 2\n"u8.ToArray(); // same size; the listing still reports the original ETag
        var second = await handle.GetCurrentAsync();

        Assert.Same(first, second); // the token did not change: not reloaded (limit 2 of §4.3)
        Assert.False(handle.IsStale);
        await Assert.ThrowsAsync<BundleVersionMismatchException>(async () => await second.ReadResourceTextAsync(file.Key!, file.Version));
        var (text, version) = await second.ReadResourceTextAsync(file.Key!); // unpinned, the new bytes are what is served
        Assert.Equal("SELECT 2\n", text);
        Assert.NotEqual(file.Version, version);
    }

    [Fact]
    public async Task A_disposed_provider_fails_as_Disposed()
    {
        var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/a.md", "x"))));
        p.Dispose();
        p.Dispose(); // once
        Assert.Equal(S3FailureKind.Disposed, (await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default))).Kind);
    }

    private sealed class CountingCredentials(Func<ImmutableCredentials> get) : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials() => get();
    }

    private sealed class PendingCredentials(Task<ImmutableCredentials> result) : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials() => result.GetAwaiter().GetResult();

        public override Task<ImmutableCredentials> GetCredentialsAsync() => result;
    }

    private sealed class ObservingStream(Action onRead) : MemoryStream(new byte[11])
    {
        public override int Read(byte[] buffer, int offset, int count) { onRead(); return base.Read(buffer, offset, count); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { onRead(); return base.ReadAsync(buffer, ct); }
    }

    /// <summary>Gives <paramref name="first"/> bytes, then blocks until cancelled.</summary>
    private sealed class StallingStream(int first) : Stream
    {
        private int _left = first;

        /// <summary>Completes when the stream has given its bytes and starts waiting.</summary>
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_left > 0) { var n = Math.Min(_left, buffer.Length); _left -= n; return n; }
            Stalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
