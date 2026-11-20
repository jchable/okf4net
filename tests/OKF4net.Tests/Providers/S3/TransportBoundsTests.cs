// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec §4.6, bounds applied before deserialization.</summary>
public class TransportBoundsTests
{
    /// <summary>A content whose stream counts the bytes read from it and who reads it.</summary>
    private sealed class WitnessContent(byte[] body) : HttpContent
    {
        public long BytesRead;
        public int Readers;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await stream.WriteAsync(body);

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            Interlocked.Increment(ref Readers);
            return Task.FromResult<Stream>(new CountingStream(new MemoryStream(body), n => Interlocked.Add(ref BytesRead, n)));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return false; // no Content-Length unless the test sets it
        }
    }

    private sealed class CountingStream(Stream inner, Action<int> onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); onRead(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = await inner.ReadAsync(buffer, ct); onRead(n); return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(HttpResponseMessage? Response, Exception? Error, WitnessContent Witness)> Send(HttpStatusCode status, int size, bool list, bool withLength)
    {
        var witness = new WitnessContent(new byte[size]);
        var inner = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var r = new HttpResponseMessage(status) { Content = witness };
                if (withLength) r.Content.Headers.ContentLength = size;
                return Task.FromResult(r);
            },
        };
        var bounding = new BoundingHandler { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(bounding);
        var uri = list ? "http://127.0.0.1:9000/okf/?list-type=2&prefix=kb%2F" : "http://127.0.0.1:9000/okf/kb/a.md";
        OperationContext.Current.Value = new OperationContext(list ? S3Operation.List : S3Operation.Read) { Capture = list ? new ListPageCapture() : null };
        try
        {
            return (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), default), null, witness);
        }
        catch (Exception e)
        {
            return (null, e, witness);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_listing_at_the_bound_passes_on_a_new_content(bool withLength)
    {
        var (r, e, w) = await Send(HttpStatusCode.OK, BoundingHandler.ListBodyBytes, list: true, withLength);
        Assert.Null(e);
        Assert.IsNotType<WitnessContent>(r!.Content);
        Assert.Equal(BoundingHandler.ListBodyBytes, (await r.Content.ReadAsByteArrayAsync()).Length);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, BoundingHandler.ListBodyBytes * 4, true)]
    [InlineData(HttpStatusCode.NotFound, BoundingHandler.ErrorBodyBytes * 64, false)]
    [InlineData(HttpStatusCode.Forbidden, BoundingHandler.ErrorBodyBytes * 64, true)]
    public async Task Past_the_bound_nothing_reaches_the_sdk_and_at_most_one_byte_more_was_read(HttpStatusCode status, int size, bool list)
    {
        // Bodies far larger than the bound: an implementation reading everything before checking would read `size`.
        var bound = list && status == HttpStatusCode.OK ? BoundingHandler.ListBodyBytes : BoundingHandler.ErrorBodyBytes;
        foreach (var withLength in new[] { true, false })
        {
            var (r, e, w) = await Send(status, size, list, withLength);
            Assert.Null(r);
            Assert.Equal(S3FailureKind.InvalidResponse, Assert.IsType<GuardException>(e).Kind);
            Assert.True(w.BytesRead <= bound + 1, $"the handler read {w.BytesRead} bytes, past B + 1");
        }
    }

    [Fact]
    public async Task An_endless_body_without_length_stops_at_B_plus_one()
    {
        long read = 0;
        var endless = new StreamContent(new CountingStream(new EndlessStream(), n => Interlocked.Add(ref read, n)));
        var inner = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = endless }) };
        using var invoker = new HttpMessageInvoker(new BoundingHandler { InnerHandler = inner });
        OperationContext.Current.Value = new OperationContext(S3Operation.List) { Capture = new ListPageCapture() };
        try
        {
            var e = await Assert.ThrowsAsync<GuardException>(() => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/?list-type=2&prefix=kb%2F"), default));
            Assert.Equal(S3FailureKind.InvalidResponse, e.Kind);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }

        Assert.True(Interlocked.Read(ref read) <= BoundingHandler.ListBodyBytes + 1);
    }

    /// <summary>A content that records its own disposal and its stream's, and whose body may fail after some bytes (minors A5).</summary>
    private sealed class TrackedContent(long bodyLength, bool failAfterFirstRead) : HttpContent
    {
        public bool ContentDisposed { get; private set; }
        public bool StreamDisposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new TrackedStream(this, bodyLength, failAfterFirstRead));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            ContentDisposed = true;
            base.Dispose(disposing);
        }

        private sealed class TrackedStream(TrackedContent owner, long length, bool failAfterFirstRead) : Stream
        {
            private long _sent;
            private bool _failed;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (failAfterFirstRead && _sent > 0 && !_failed)
                {
                    _failed = true;
                    throw new IOException("the connection broke in the middle of the body");
                }

                var n = (int)Math.Min(buffer.Length, Math.Min(length - _sent, 4096));
                buffer.Span[..n].Clear();
                _sent += n;
                return ValueTask.FromResult(n);
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                owner.StreamDisposed = true;
                base.Dispose(disposing);
            }
        }
    }

    [Theory]
    [InlineData(false)] // a body that overruns the bound
    [InlineData(true)]  // a body that breaks on the wire
    public async Task When_reading_the_body_fails_the_original_response_and_its_stream_are_released(bool breaks)
    {
        // Minors A5. Whatever ends the bounded read, nothing of the original response is left to the garbage collector:
        // its content and the stream read from it are disposed. (The copy handed to the SDK is not built until the body
        // was read within the bound, so a failed read leaves none: nothing for the SDK nor a test to observe.)
        var content = new TrackedContent(breaks ? 1_000_000 : BoundingHandler.ErrorBodyBytes + 100, failAfterFirstRead: breaks);
        var (r, e) = await SendIn(new OperationContext(S3Operation.Read), HttpMethod.Get, "http://127.0.0.1:9000/okf/kb/a.md", () => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = content });
        Assert.Null(r);
        if (breaks)
        {
            Assert.IsType<IOException>(e);
        }
        else
        {
            Assert.Equal(S3FailureKind.InvalidResponse, Assert.IsType<GuardException>(e).Kind);
        }

        Assert.True(content.StreamDisposed, "the stream read from the original content");
        Assert.True(content.ContentDisposed, "the original response's content");
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => count; // zeros forever
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(HttpResponseMessage? Response, Exception? Error)> SendIn(OperationContext context, HttpMethod method, string url, Func<HttpResponseMessage> respond)
    {
        var inner = new FakeS3Handler { Respond = (_, _) => Task.FromResult(respond()) };
        using var invoker = new HttpMessageInvoker(new BoundingHandler { InnerHandler = inner });
        OperationContext.Current.Value = context;
        try
        {
            return (await invoker.SendAsync(new HttpRequestMessage(method, url), default), null);
        }
        catch (Exception e)
        {
            return (null, e);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Theory]
    [InlineData(S3Operation.List)]
    [InlineData(S3Operation.Exists)]
    public async Task The_listing_bound_follows_the_operation_not_the_query(S3Operation operation)
    {
        // No "list-type=2" in the URL: the operation alone decides that a 2xx body is a listing page.
        var witness = new WitnessContent(new byte[BoundingHandler.ListBodyBytes + 10]);
        var (r, e) = await SendIn(new OperationContext(operation) { Capture = new ListPageCapture() }, HttpMethod.Get, "http://127.0.0.1:9000/okf/", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = witness });
        Assert.Null(r);
        Assert.Equal(S3FailureKind.InvalidResponse, Assert.IsType<GuardException>(e).Kind);
        Assert.True(witness.BytesRead <= BoundingHandler.ListBodyBytes + 1);
    }

    [Fact]
    public async Task A_read_whose_url_looks_like_a_listing_is_still_a_read()
    {
        var witness = new WitnessContent(new byte[BoundingHandler.ListBodyBytes + 10]);
        var (r, e) = await SendIn(new OperationContext(S3Operation.Read), HttpMethod.Get, "http://127.0.0.1:9000/okf/kb/a.md?list-type=2", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = witness });
        Assert.Null(e);
        Assert.Same(witness, r!.Content);
        Assert.Equal(0, witness.BytesRead);
    }

    [Theory]
    [InlineData("HEAD", HttpStatusCode.NotFound)]
    [InlineData("HEAD", HttpStatusCode.Forbidden)]
    [InlineData("GET", HttpStatusCode.NotModified)]
    [InlineData("GET", HttpStatusCode.NoContent)]
    public async Task A_response_without_a_body_is_not_judged_by_its_declared_length(string method, HttpStatusCode status)
    {
        // Content-Length on a HEAD answer (and on 204/304) describes a representation that is not sent.
        var context = new OperationContext(status == HttpStatusCode.NoContent ? S3Operation.List : S3Operation.Read) { Capture = new ListPageCapture() };
        var (r, e) = await SendIn(context, new HttpMethod(method), "http://127.0.0.1:9000/okf/kb/a.md", () =>
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentLength = 10L * BoundingHandler.ListBodyBytes;
            return response;
        });
        Assert.Null(e);
        Assert.Equal(status, r!.StatusCode);
        Assert.Empty(await r.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_body_that_comes_although_none_is_expected_is_still_bounded()
    {
        var witness = new WitnessContent(new byte[BoundingHandler.ErrorBodyBytes * 4]);
        var (r, e) = await SendIn(new OperationContext(S3Operation.Read), HttpMethod.Head, "http://127.0.0.1:9000/okf/kb/a.md", () => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = witness });
        Assert.Null(r);
        Assert.Equal(S3FailureKind.InvalidResponse, Assert.IsType<GuardException>(e).Kind);
        Assert.True(witness.BytesRead <= BoundingHandler.ErrorBodyBytes + 1);
    }

    [Fact]
    public async Task A_small_listing_page_does_not_allocate_the_whole_bound()
    {
        // Everything below completes synchronously on this thread (in-memory content), so the thread's counter sees it all.
        var body = new byte[4096];
        var context = new OperationContext(S3Operation.List) { Capture = new ListPageCapture() };
        var inner = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new WitnessContent(body) }) };
        using var invoker = new HttpMessageInvoker(new BoundingHandler { InnerHandler = inner });
        OperationContext.Current.Value = context;
        try
        {
            (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/"), default)).Dispose(); // warm-up
            var before = GC.GetAllocatedBytesForCurrentThread();
            var task = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/"), default);
            var after = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(task.IsCompleted, "the measured send must complete on this thread");
            using var response = await task;
            Assert.True(after - before < 1024 * 1024, $"a 4 KiB page allocated {after - before} bytes");
            Assert.Equal(body.Length, context.Capture!.Body!.Length);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Fact]
    public async Task A_declared_length_equal_to_the_bound_does_not_allocate_the_whole_bound_for_a_small_body()
    {
        // #290: the buffer was sized from the DECLARED length, so a store declaring the bound and sending a few bytes cost
        // 8 MiB + 1 per page. The first buffer is small now and grows with what arrives. Same measuring technique as above
        // (everything completes synchronously on this thread).
        var body = new byte[10];
        var content = new WitnessContent(body);
        content.Headers.ContentLength = BoundingHandler.ListBodyBytes;
        var context = new OperationContext(S3Operation.List) { Capture = new ListPageCapture() };
        var inner = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }) };
        using var invoker = new HttpMessageInvoker(new BoundingHandler { InnerHandler = inner });
        OperationContext.Current.Value = context;
        try
        {
            var warm = new WitnessContent(body);
            warm.Headers.ContentLength = BoundingHandler.ListBodyBytes;
            inner.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = warm });
            (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/"), default)).Dispose(); // warm-up

            inner.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            var before = GC.GetAllocatedBytesForCurrentThread();
            var task = invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/"), default);
            var after = GC.GetAllocatedBytesForCurrentThread();
            Assert.True(task.IsCompleted, "the measured send must complete on this thread");
            using var response = await task;
            Assert.True(after - before < 1024 * 1024, $"a 10-byte page declaring the bound allocated {after - before} bytes");
            Assert.Equal(body.Length, context.Capture!.Body!.Length);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Fact]
    public async Task A_failed_attempt_does_not_leave_the_body_of_an_earlier_one_in_the_capture()
    {
        // #290: one SDK call may send several requests (retries) under ONE operation context. A page captured by an
        // earlier attempt must not outlive a later attempt that failed: the provider re-reads the capture to judge a
        // deserializer failure, and it must judge the response that failed, never a stale one.
        var context = new OperationContext(S3Operation.List) { Capture = new ListPageCapture() };
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable]);
        var inner = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(statuses.Dequeue()) { Content = new ByteArrayContent("<x/>"u8.ToArray()) }) };
        using var invoker = new HttpMessageInvoker(new BoundingHandler { InnerHandler = inner });
        OperationContext.Current.Value = context;
        try
        {
            (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/"), default)).Dispose();
            Assert.NotNull(context.Capture!.Body); // positive control: the first attempt did capture its page

            (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/"), default)).Dispose();
            Assert.Null(context.Capture.Body);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Fact]
    public async Task An_attempt_that_throws_before_any_response_does_not_leave_the_body_of_an_earlier_one_in_the_capture()
    {
        // #290 (Copilot review of #304): the capture is cleared before the send, so a retry that fails with a transport
        // error (no response at all) cannot leave the previous attempt's page for the provider to judge.
        var context = new OperationContext(S3Operation.List) { Capture = new ListPageCapture() };
        var sends = 0;
        var inner = new FakeS3Handler
        {
            Respond = (_, _) => ++sends == 1
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("<x/>"u8.ToArray()) })
                : throw new HttpRequestException("connection reset"),
        };
        using var invoker = new HttpMessageInvoker(new BoundingHandler { InnerHandler = inner });
        OperationContext.Current.Value = context;
        try
        {
            (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/"), default)).Dispose();
            Assert.NotNull(context.Capture!.Body); // positive control: the first attempt did capture its page

            await Assert.ThrowsAsync<HttpRequestException>(() => invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/"), default));
            Assert.Null(context.Capture.Body);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    /// <summary>A response that forgets its metadata when disposed: a copy made after disposal would lose it.</summary>
    private sealed class ForgetfulResponse : HttpResponseMessage
    {
        public ForgetfulResponse(HttpStatusCode status)
            : base(status)
        {
        }

        protected override void Dispose(bool disposing)
        {
            Headers.Clear();
            Content?.Headers.Clear();
            ReasonPhrase = null;
            RequestMessage = null;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task The_copy_keeps_the_original_metadata()
    {
        HttpRequestMessage? seen = null;
        var inner = new FakeS3Handler
        {
            Respond = (req, _) =>
            {
                seen = req;
                var r = new ForgetfulResponse(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found", Content = new ByteArrayContent("<Error/>"u8.ToArray()) };
                r.Headers.TryAddWithoutValidation("x-amz-request-id", "r1");
                r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
                return Task.FromResult<HttpResponseMessage>(r);
            },
        };
        using var invoker = new HttpMessageInvoker(new BoundingHandler { InnerHandler = inner });
        OperationContext.Current.Value = new OperationContext(S3Operation.Read);
        try
        {
            using var copy = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/kb/a.md"), default);
            Assert.IsNotType<ForgetfulResponse>(copy);
            Assert.Equal("r1", Assert.Single(copy.Headers.GetValues("x-amz-request-id")));
            Assert.Equal("application/xml", copy.Content.Headers.ContentType!.MediaType);
            Assert.Equal("Not Found", copy.ReasonPhrase);
            Assert.Same(seen, copy.RequestMessage);
            Assert.Equal(8, copy.Content.Headers.ContentLength);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Fact]
    public async Task A_successful_object_body_is_passed_through_untouched()
    {
        var (r, e, w) = await Send(HttpStatusCode.OK, 1_000_000, list: false, withLength: true);
        Assert.Null(e);
        Assert.Same(w, r!.Content);
        Assert.Equal(0, w.BytesRead);
    }
}
