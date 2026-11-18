// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using OKF4net.Providers;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec §4.6: the closed table, by the real SDK pipeline; closed-vocabulary messages.</summary>
public class ErrorTests
{
    private const string Sentinel = "SENTINEL-bucket.s3.example-REQ123";

    /// <summary>A deadline no row meets by accident (the credentials are resolved on the thread pool, which a loaded run can starve).</summary>
    private static S3BundleSource Patient => S3Test.Local() with { RequestTimeout = TimeSpan.FromSeconds(60) };

    private static S3BundleProvider Provider(FakeS3Handler fake) => S3Test.Provider(fake, Patient);

    public static TheoryData<HttpStatusCode, string, Type, S3FailureKind?> Table => new()
    {
        { HttpStatusCode.NotFound, "NoSuchKey", typeof(BundleObjectNotFoundException), null },
        { HttpStatusCode.Forbidden, "AccessDenied", typeof(BundleAccessRefusedException), null },
        { HttpStatusCode.Forbidden, "InvalidAccessKeyId", typeof(S3RequestException), S3FailureKind.Credentials },
        { HttpStatusCode.Forbidden, "SignatureDoesNotMatch", typeof(S3RequestException), S3FailureKind.Credentials },
        { HttpStatusCode.Forbidden, "ExpiredToken", typeof(S3RequestException), S3FailureKind.Credentials },
        { HttpStatusCode.BadRequest, "ExpiredToken", typeof(S3RequestException), S3FailureKind.Credentials },
        { HttpStatusCode.BadRequest, "InvalidToken", typeof(S3RequestException), S3FailureKind.Credentials },
        { HttpStatusCode.BadRequest, "TokenRefreshRequired", typeof(S3RequestException), S3FailureKind.Credentials },
        { HttpStatusCode.NotFound, "NoSuchBucket", typeof(S3RequestException), S3FailureKind.BucketUnavailable },
        { HttpStatusCode.MovedPermanently, "PermanentRedirect", typeof(S3RequestException), S3FailureKind.BucketUnavailable },
        { HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed", typeof(S3RequestException), S3FailureKind.BucketUnavailable },
        { HttpStatusCode.BadRequest, "IllegalLocationConstraintException", typeof(S3RequestException), S3FailureKind.BucketUnavailable },
        { HttpStatusCode.ServiceUnavailable, "SlowDown", typeof(S3RequestException), S3FailureKind.Throttled },
        { (HttpStatusCode)429, "TooManyRequests", typeof(S3RequestException), S3FailureKind.Throttled },
        { HttpStatusCode.InternalServerError, "InternalError", typeof(S3RequestException), S3FailureKind.ServerError },
        { HttpStatusCode.ServiceUnavailable, "ServiceUnavailable", typeof(S3RequestException), S3FailureKind.ServerError }, // 503 with another code: its status range
        { HttpStatusCode.Conflict, "Whatever", typeof(S3RequestException), S3FailureKind.ClientError },
        { HttpStatusCode.Forbidden, "Unknown" + Sentinel, typeof(S3RequestException), S3FailureKind.ClientError },
        { HttpStatusCode.NotFound, "NoSuchUpload", typeof(S3RequestException), S3FailureKind.ClientError }, // a 404 with an unrecognized code
        { HttpStatusCode.Found, "Moved", typeof(S3RequestException), S3FailureKind.InvalidResponse },
        { HttpStatusCode.PermanentRedirect, "Moved", typeof(S3RequestException), S3FailureKind.InvalidResponse },
        { (HttpStatusCode)599, "Odd", typeof(S3RequestException), S3FailureKind.ServerError },
        { HttpStatusCode.NotFound, "SignatureDoesNotMatch", typeof(S3RequestException), S3FailureKind.ClientError }, // an incoherent pair: its status range, not its code
        { HttpStatusCode.Forbidden, "NoSuchKey", typeof(S3RequestException), S3FailureKind.ClientError },            // nor a NoSuchKey under 403
        { HttpStatusCode.NotFound, "AccessDenied", typeof(S3RequestException), S3FailureKind.ClientError },          // nor an AccessDenied under 404
        { HttpStatusCode.ServiceUnavailable, "NoSuchBucket", typeof(S3RequestException), S3FailureKind.ServerError },
        { (HttpStatusCode)600, "Odd", typeof(S3RequestException), S3FailureKind.InvalidResponse },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public async Task Each_row_of_the_table(HttpStatusCode status, string code, Type expected, S3FailureKind? kind)
    {
        var fake = new FakeS3Handler
        {
            Respond = (_, _) => Task.FromResult(FakeS3Handler.Xml(status,
                $"<?xml version=\"1.0\"?><Error><Code>{code}</Code><Message>{Sentinel}\n https://{Sentinel}/x\u2028</Message><RequestId>{Sentinel}</RequestId><HostId>{Sentinel}</HostId><Endpoint>{Sentinel}</Endpoint><Region>{Sentinel}</Region></Error>",
                r =>
                {
                    r.Headers.TryAddWithoutValidation("x-amz-request-id", Sentinel);
                    r.Headers.TryAddWithoutValidation("x-amz-id-2", Sentinel);
                    r.Headers.TryAddWithoutValidation("x-amz-bucket-region", Sentinel);
                })),
        };
        using var p = Provider(fake);
        var e = await Record.ExceptionAsync(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.IsType(expected, e);
        if (kind is { } k)
        {
            var s3 = (S3RequestException)e!;
            Assert.Equal(k, s3.Kind);
            Assert.Equal(S3Operation.Read, s3.Operation);
            Assert.Equal((int)status, s3.StatusCode);
            Assert.Equal($"S3 read failed: {k} (HTTP {(int)status}) for 'a.md'", s3.Message); // the closed vocabulary, exactly
        }

        Assert.DoesNotContain(Sentinel, e!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("okf", e.Message, StringComparison.Ordinal); // the bucket
        Assert.DoesNotContain("127.0.0.1", e.Message, StringComparison.Ordinal); // the endpoint
        Assert.DoesNotContain(code, e.Message, StringComparison.Ordinal);
        Assert.InRange(fake.Requests.Count, 1, 3); // retries included, never past the guard's cap
    }

    /// <summary>
    /// A response without a body: the SDK names the error after the status (measured on AWSSDK.S3 4.1.0:
    /// <c>MovedPermanently</c>, <c>Forbidden</c>, <c>NotFound</c>, <c>RedirectKeepVerb</c>), never a code S3 sent.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently, S3FailureKind.BucketUnavailable)] // S3's region answer without a body
    [InlineData(HttpStatusCode.Forbidden, S3FailureKind.ClientError)]
    [InlineData(HttpStatusCode.NotFound, S3FailureKind.ClientError)]
    [InlineData(HttpStatusCode.TemporaryRedirect, S3FailureKind.InvalidResponse)] // a 307 without Location
    [InlineData(HttpStatusCode.Found, S3FailureKind.InvalidResponse)]
    public async Task A_bodiless_error_is_classified_by_its_status(HttpStatusCode status, S3FailureKind kind)
    {
        using var p = Provider(new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status)) });
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.Equal(kind, e.Kind);
        Assert.Equal((int)status, e.StatusCode);
    }

    [Fact]
    public async Task A_307_without_Location_is_InvalidResponse() =>
        Assert.Equal(S3FailureKind.InvalidResponse, (await Assert.ThrowsAsync<S3RequestException>(async () =>
            await Provider(new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)) })
                .ReadAsync(BundleKey.Parse("a.md"), null, default))).Kind);

    [Fact]
    public async Task A_transport_failure_after_the_headers_is_Transport()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream()) }) };
        Assert.Equal(S3FailureKind.Transport, (await Assert.ThrowsAsync<S3RequestException>(async () => await Provider(fake).ReadAsync(BundleKey.Parse("a.md"), null, default))).Kind);
    }

    [Fact]
    public async Task A_transport_failure_before_the_headers_is_Transport()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => throw new HttpRequestException("connection refused " + Sentinel) };
        using var p = Provider(fake);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.Equal(S3FailureKind.Transport, e.Kind);
        Assert.Null(e.StatusCode);
        Assert.Equal("S3 read failed: Transport for 'a.md'", e.Message);
    }

    [Fact]
    public async Task A_failure_at_the_release_alone_is_classified_like_any_other()
    {
        // The body reads whole, then its release fails: that failure, alone, is the outcome (spec §4.5).
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailsAtDispose(new IOException("reset " + Sentinel))) }) };
        using var p = Provider(fake);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.Equal(S3FailureKind.Transport, e.Kind);
        Assert.DoesNotContain(Sentinel, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(64 * 1024, typeof(BundleObjectNotFoundException))]
    [InlineData(64 * 1024 + 1, typeof(S3RequestException))]
    public async Task An_error_body_is_bounded_before_the_sdk_reads_it(int size, Type expected)
    {
        var head = "<?xml version=\"1.0\"?><Error><Code>NoSuchKey</Code><Message>";
        var tail = "</Message></Error>";
        var xml = head + new string('m', size - head.Length - tail.Length) + tail;
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.NotFound, xml)) };
        using var p = Provider(fake);
        var e = await Record.ExceptionAsync(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.IsType(expected, e);
        if (e is S3RequestException s3)
        {
            Assert.Equal(S3FailureKind.InvalidResponse, s3.Kind);
        }
    }

    [Fact]
    public async Task A_credentials_failure_during_the_survey_fails_the_load()
    {
        const string computation = "---\ntype: Attested Computation\ntitle: C\ndescription: D.\nruntime: python\ncomputation: ./c.py\nattester:\n  resource: ./att.py\n---\n\nBody\n";
        var forbidden = false;
        var objects = S3Test.Objects(("kb/c.md", computation), ("kb/c.py", "print(1)\n"), ("kb/att.py", "def attest(r): return True\n"));
        var serving = S3Test.Serving(objects);
        var fake = new FakeS3Handler
        {
            Respond = (req, ct) =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("c.md", StringComparison.Ordinal))
                {
                    forbidden = true; // credentials expire right after the concepts are read
                }
                else if (forbidden && !req.RequestUri.Query.Contains("list-type", StringComparison.Ordinal))
                {
                    return Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.Forbidden, FakeS3Handler.Error("ExpiredToken")));
                }

                return serving.Respond(req, ct);
            },
        };
        using var p = Provider(fake);
        var e = await Assert.ThrowsAsync<BundleLoadException>(async () => await Bundle.LoadAsync(p));
        Assert.IsType<S3RequestException>(e.InnerException);
        Assert.Equal(S3FailureKind.Credentials, ((S3RequestException)e.InnerException!).Kind);
    }

    [Fact]
    public async Task The_same_bundle_loads_when_the_survey_reads_succeed()
    {
        // Counter-proof of the test above: the concept shape is one the core surveys, and both resources are Pinned.
        const string computation = "---\ntype: Attested Computation\ntitle: C\ndescription: D.\nruntime: python\ncomputation: ./c.py\nattester:\n  resource: ./att.py\n---\n\nBody\n";
        var objects = S3Test.Objects(("kb/c.md", computation), ("kb/c.py", "print(1)\n"), ("kb/att.py", "def attest(r): return True\n"));
        using var p = Provider(S3Test.Serving(objects));
        var bundle = await Bundle.LoadAsync(p);
        var survey = Assert.Single(bundle.ExecutableResources).Value;
        Assert.Equal(ExecutableResourceState.Pinned, survey.Computation.State);
        Assert.Equal(ExecutableResourceState.Pinned, survey.Attester.State);
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromException<int>(new IOException("reset " + Sentinel));
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("reset " + Sentinel);
    }

    private sealed class FailsAtDispose(Exception onDispose) : MemoryStream("abc"u8.ToArray())
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                throw onDispose;
            }
        }
    }
}
