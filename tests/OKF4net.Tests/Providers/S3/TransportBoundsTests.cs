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

    [Fact]
    public async Task A_successful_object_body_is_passed_through_untouched()
    {
        var (r, e, w) = await Send(HttpStatusCode.OK, 1_000_000, list: false, withLength: true);
        Assert.Null(e);
        Assert.Same(w, r!.Content);
        Assert.Equal(0, w.BytesRead);
    }
}
