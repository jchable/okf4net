// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D3, §4.4: one authority, one path prefix, refused before sending; counter-proofs without the guard.</summary>
public sealed class GuardTests : IDisposable
{
    private static readonly ImmutableCredentials Creds = new("AKIDEXAMPLE", "secret", "TOKEN-CANARY");

    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in _owned)
        {
            d.Dispose();
        }
    }

    private (AmazonS3Client Client, FakeS3Handler Fake) Build(S3BundleSource source, bool guarded = true)
    {
        var fake = new FakeS3Handler();
        var normalized = SourceRules.Normalize(source);
        HttpClient httpClient;
        var client = guarded
            ? ClientBuilder.Build(new SnapshotCredentials(), normalized, fake, out httpClient)
            : ClientBuilder.BuildUnguardedForCounterProof(new SnapshotCredentials(), normalized, fake, out httpClient);
        _owned.Add(client);
        _owned.Add(httpClient);
        return (client, fake);
    }

    private static async Task<Exception?> Get(AmazonS3Client client, string bucket, string s3Key)
    {
        OperationContext.Current.Value = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = s3Key, Credentials = Creds };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // a scripted loop of redirects cannot hang the test
        try
        {
            using var r = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = s3Key }, deadline.Token);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    // (source, Location the store answers, URL the SDK really sends next). Measured on AWSSDK.S3 4.1.0 (gate G3 log,
    // and again here): the SDK takes the Location's AUTHORITY only and keeps the request's resource path, the key
    // ("/kb/x"); the Location's own path and query are ignored. Under path-style the bucket segment belongs to the
    // endpoint, so it is lost with it. From virtual-hosted, "/kb/x" goes to the regional host: bucket kb, key x —
    // another object. From path-style, "/kb/x" goes to the virtual-hosted host: bucket demo, key kb/x — the same
    // object, through an authority this configuration does not admit. The guard refuses both (one authority).
    public static TheoryData<S3BundleSource, string, string> TwoAuthorities => new()
    {
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 }, "https://s3.eu-west-1.amazonaws.com/kb/x", "https://s3.eu-west-1.amazonaws.com/kb/x" },
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1, ForcePathStyle = true }, "https://demo.s3.eu-west-1.amazonaws.com/kb/x", "https://demo.s3.eu-west-1.amazonaws.com/kb/x" },
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 }, "https://s3.eu-west-1.amazonaws.com/zzz/q?w=1", "https://s3.eu-west-1.amazonaws.com/kb/x" }, // the Location's path is ignored
    };

    private static void RedirectOnceThenServe(FakeS3Handler fake, string location)
    {
        var first = 0;
        fake.Respond = (req, _) => Task.FromResult(Interlocked.Exchange(ref first, 1) == 0
            ? FakeS3Handler.Redirect(HttpStatusCode.TemporaryRedirect, location)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("FROM-TARGET") });
    }

    [Theory]
    [MemberData(nameof(TwoAuthorities))]
    public async Task A_307_between_the_two_authorities_of_one_bucket_is_refused_both_ways(S3BundleSource source, string location, string _)
    {
        var (client, fake) = Build(source);
        RedirectOnceThenServe(fake, location);

        var e = await Get(client, "demo", "kb/x");

        var guard = Assert.IsType<GuardException>(e is GuardException ? e : e?.InnerException ?? e);
        Assert.Equal(S3FailureKind.RefusedHost, guard.Kind);
        Assert.Single(fake.Requests); // the original only; the redirected request never reached the network handler
    }

    [Theory]
    [MemberData(nameof(TwoAuthorities))]
    public async Task Counter_proof_without_the_guard_the_same_307_leaves_the_admitted_authority(S3BundleSource source, string location, string sentNext)
    {
        var (client, fake) = Build(source, guarded: false);
        RedirectOnceThenServe(fake, location);

        Assert.Null(await Get(client, "demo", "kb/x"));
        Assert.Equal(2, fake.Requests.Count); // bounded: the fake redirects once
        Assert.Equal(sentNext, fake.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task A_307_to_another_bucket_host_is_refused_and_its_counter_proof_follows()
    {
        var source = new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 };
        const string target = "https://evil.s3.eu-west-1.amazonaws.com/kb/x";
        foreach (var guarded in new[] { true, false })
        {
            var (client, fake) = Build(source, guarded);
            fake.Respond = (req, _) => Task.FromResult(req.RequestUri!.Host.StartsWith("evil.", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("X") }
                : FakeS3Handler.Redirect(HttpStatusCode.TemporaryRedirect, target));
            var e = await Get(client, "demo", "kb/x");
            Assert.Equal(guarded ? 0 : 1, fake.Count("evil.s3.eu-west-1.amazonaws.com"));
            Assert.Equal(guarded, e is not null);
        }
    }

    [Fact]
    public async Task An_altered_key_on_the_right_authority_is_refused()
    {
        var (client, fake) = Build(new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true });
        OperationContext.Current.Value = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = "kb/a.md", Credentials = Creds };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => client.GetObjectAsync(new GetObjectRequest { BucketName = "okf", Key = "kb/b.md" }));
        }
        finally
        {
            OperationContext.Current.Value = null;
        }

        Assert.Empty(fake.Requests);
    }

    private static (HttpMessageInvoker Invoker, FakeS3Handler Fake) Guard()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)) };
        var plan = AddressingPlan.For(SourceRules.Normalize(new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true }));
        return (new HttpMessageInvoker(new PinningHandler(plan) { InnerHandler = fake }), fake);
    }

    private static async Task<bool> Admitted(OperationContext? context, string url, HttpMethod? method = null, string? hostHeader = null)
    {
        var (invoker, fake) = Guard();
        using var __ = invoker;
        OperationContext.Current.Value = context;
        try
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
            if (hostHeader is not null)
            {
                Assert.True(request.Headers.TryAddWithoutValidation("Host", hostHeader)); // unvalidated: the guard must read what is sent, not the parsed view
            }

            using var _ = await invoker.SendAsync(request, default);
            return fake.Requests.Count == 1;
        }
        catch (GuardException g) when (g.Kind == S3FailureKind.RefusedHost)
        {
            Assert.Empty(fake.Requests);
            return false;
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Fact]
    public async Task The_guard_alone_admits_exactly_the_expected_requests()
    {
        // Directly on the handler, with a context that has usable credentials: nothing else can refuse first.
        var read = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = "kb/a b.md", Credentials = Creds };
        var list = new OperationContext(S3Operation.List) { ExpectedListPrefix = "kb/", Credentials = Creds };
        Assert.True(await Admitted(read, "http://127.0.0.1:9000/okf/kb/a%20b.md"));
        Assert.False(await Admitted(null, "http://127.0.0.1:9000/okf/kb/a%20b.md"));                 // outside any operation
        Assert.False(await Admitted(read, "http://127.0.0.1:9000/okf/kb/a+b.md"));                   // another key
        Assert.False(await Admitted(read, "http://127.0.0.1:9000/okf/kb/a%2520b.md"));               // another encoding of another key
        Assert.False(await Admitted(read, "http://127.0.0.1:9001/okf/kb/a%20b.md"));                 // another port
        Assert.True(await Admitted(list, "http://127.0.0.1:9000/okf/?list-type=2&prefix=kb%2F&encoding-type=url"));
        Assert.False(await Admitted(list, "http://127.0.0.1:9000/okf/?list-type=2&prefix=kb2%2F&encoding-type=url")); // an altered prefix
        Assert.False(await Admitted(list, "http://127.0.0.1:9000/okf/?list-type=2&prefix=kb%2F&prefix=x&encoding-type=url")); // a repeated prefix
        Assert.False(await Admitted(list, "http://127.0.0.1:9000/other/?list-type=2&prefix=kb%2F"));  // another bucket path
    }

    [Fact]
    public async Task The_guard_admits_only_GET()
    {
        // Every provider operation is a GET: Read is GetObject, List and Exists are ListObjectsV2 (plan Tasks 7-8). No HEAD.
        var read = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = "kb/a.md", Credentials = Creds };
        var list = new OperationContext(S3Operation.List) { ExpectedListPrefix = "kb/", Credentials = Creds };
        const string listUrl = "http://127.0.0.1:9000/okf/?list-type=2&prefix=kb%2F&encoding-type=url";
        Assert.True(await Admitted(read, "http://127.0.0.1:9000/okf/kb/a.md", HttpMethod.Get));
        Assert.False(await Admitted(read, "http://127.0.0.1:9000/okf/kb/a.md", HttpMethod.Put));
        Assert.False(await Admitted(read, "http://127.0.0.1:9000/okf/kb/a.md", HttpMethod.Delete));
        Assert.False(await Admitted(read, "http://127.0.0.1:9000/okf/kb/a.md", HttpMethod.Post));
        Assert.False(await Admitted(read, "http://127.0.0.1:9000/okf/kb/a.md", HttpMethod.Head));
        Assert.False(await Admitted(list, listUrl, HttpMethod.Delete));
        Assert.False(await Admitted(list, listUrl, HttpMethod.Head));
    }

    [Fact]
    public async Task A_host_header_must_name_the_admitted_authority()
    {
        var read = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = "kb/a.md", Credentials = Creds };
        const string url = "http://127.0.0.1:9000/okf/kb/a.md";
        Assert.True(await Admitted(read, url, hostHeader: "127.0.0.1:9000"));
        Assert.False(await Admitted(read, url, hostHeader: "evil.127.0.0.1:9000"));      // another bucket's virtual host
        Assert.False(await Admitted(read, url, hostHeader: "127.0.0.1:9001"));           // another port
        Assert.False(await Admitted(read, url, hostHeader: "127.0.0.1"));                // the default port, not the plan's
        Assert.False(await Admitted(read, url, hostHeader: "127.0.0.1:9000:9000"));      // not an authority
        Assert.False(await Admitted(read, url, hostHeader: "evil.example/x"));           // not an authority either
    }

    [Fact]
    public async Task A_host_header_on_a_default_port_may_omit_it()
    {
        // Through the real client: whatever Host the SDK sets on a regional source is admitted; then the guard alone on that authority.
        var (client, fake) = Build(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 });
        fake.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("X") });
        Assert.Null(await Get(client, "demo", "kb/x"));

        var plan = AddressingPlan.For(SourceRules.Normalize(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 }));
        foreach (var (host, admitted) in new[] { ("demo.s3.eu-west-1.amazonaws.com", true), ("DEMO.s3.eu-west-1.amazonaws.com:443", true), ("evil.s3.eu-west-1.amazonaws.com", false), ("demo.s3.eu-west-1.amazonaws.com:80", false) })
        {
            var inner = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)) };
            using var invoker = new HttpMessageInvoker(new PinningHandler(plan) { InnerHandler = inner });
            OperationContext.Current.Value = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = "kb/x", Credentials = Creds };
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "https://demo.s3.eu-west-1.amazonaws.com/kb/x");
                request.Headers.Host = host;
                using var _ = await invoker.SendAsync(request, default);
                Assert.True(admitted, host);
            }
            catch (GuardException g) when (g.Kind == S3FailureKind.RefusedHost)
            {
                Assert.False(admitted, host);
            }
            finally
            {
                OperationContext.Current.Value = null;
            }
        }
    }

    [Fact]
    public async Task The_query_is_an_allowlist()
    {
        var read = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = "kb/a.md", Credentials = Creds };
        var list = new OperationContext(S3Operation.List) { ExpectedListPrefix = "kb/", Credentials = Creds };
        var whole = new OperationContext(S3Operation.List) { ExpectedListPrefix = "", Credentials = Creds };
        const string root = "http://127.0.0.1:9000/okf/";
        Assert.False(await Admitted(read, "http://127.0.0.1:9000/okf/kb/a.md?versionId=1"));        // a read has no query
        Assert.False(await Admitted(read, "http://127.0.0.1:9000/okf/kb/a.md?"));                    // not even an empty one
        Assert.True(await Admitted(list, root + "?list-type=2&prefix=kb%2F&encoding-type=url&max-keys=1000&continuation-token=abc%3D"));
        Assert.False(await Admitted(list, root + "?list-type=2&list-type=2&prefix=kb%2F&encoding-type=url"));   // a repeated list-type
        Assert.False(await Admitted(list, root + "?list-type=2&prefix=kb%2F&encoding-type=url&encoding-type=url")); // a doubled encoding-type
        Assert.False(await Admitted(list, root + "?list-type=2&prefix=kb%2F&encoding-type=url&max-keys=1&max-keys=2"));
        Assert.False(await Admitted(list, root + "?list-type=2&prefix=kb%2F&encoding-type=url&start-after=x"));  // a parameter outside the set
        Assert.False(await Admitted(list, root + "?list-type=2&prefix=kb%2F&encoding-type=url&delimiter=%2F"));
        Assert.False(await Admitted(list, root + "?list-type=2&prefix%20=kb%2F&prefix=kb%2F&encoding-type=url")); // an encoded name is not a known name (Uri itself decodes %66 to f before sending, so the guard sees what is sent)
        Assert.False(await Admitted(list, root + "?prefix=kb%2F&encoding-type=url"));                  // list-type required
        Assert.False(await Admitted(list, root + "?list-type=1&prefix=kb%2F&encoding-type=url"));      // list-type is exactly 2
        Assert.False(await Admitted(list, root + "?list-type=2&prefix=kb%2F"));                         // encoding-type required
        Assert.False(await Admitted(list, root + "?list-type=2&prefix=kb%2F&encoding-type=URL"));      // encoding-type is exactly url
        Assert.False(await Admitted(list, root + "?list-type=2&encoding-type=url"));                    // the expected prefix is missing
        Assert.False(await Admitted(list, root + "?list-type=2&prefix=kb%2F&encoding-type=url&continuation-token=%C3%28")); // not UTF-8 once decoded (a lone "%ZZ" would be re-escaped by Uri to %25ZZ)
        // A raw '+' in a query value: S3 reads it as a space while a strict decoding reads a '+', so the guard and the
        // store would disagree on what is asked. Refused in every value; the SDK sends a '+' as %2B and a space as %20.
        var plus = new OperationContext(S3Operation.List) { ExpectedListPrefix = "k+b/", Credentials = Creds };
        Assert.False(await Admitted(plus, root + "?list-type=2&prefix=k+b%2F&encoding-type=url"));
        Assert.True(await Admitted(plus, root + "?list-type=2&prefix=k%2Bb%2F&encoding-type=url"));
        Assert.False(await Admitted(list, root + "?list-type=2&prefix=kb%2F&encoding-type=url&continuation-token=a+b"));
        Assert.True(await Admitted(list, root + "?list-type=2&prefix=kb%2F&encoding-type=url&continuation-token=a%2Bb"));
        Assert.False(await Admitted(list, root + "?list-type=2&prefix=kb%2F&encoding-type=url&max-keys=+1000")); Assert.True(await Admitted(whole, root + "?list-type=2&encoding-type=url"));                    // the whole bucket: no prefix or an empty one
        Assert.True(await Admitted(whole, root + "?list-type=2&prefix=&encoding-type=url"));
        Assert.False(await Admitted(whole, root + "?list-type=2&prefix=x&encoding-type=url"));
    }

    private static string ListingXml(string? nextToken = null) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult><Name>demo</Name><EncodingType>url</EncodingType>"
        + (nextToken is null ? "<IsTruncated>false</IsTruncated>" : $"<IsTruncated>true</IsTruncated><NextContinuationToken>{nextToken}</NextContinuationToken>")
        + "<MaxKeys>1000</MaxKeys><Contents><Key>kb/a.md</Key><ETag>\"e\"</ETag><Size>1</Size></Contents></ListBucketResult>";

    public static TheoryData<S3BundleSource, string> ListSources => new()
    {
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 }, "kb/" },
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1, ForcePathStyle = true }, "kb/" },
        { new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true }, "kb/" },
        { new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true }, "" },
    };

    [Theory]
    [MemberData(nameof(ListSources))]
    public async Task What_the_real_client_sends_for_a_listing_page_and_a_read_is_admitted(S3BundleSource source, string listPrefix)
    {
        // The allowlist is measured against the requests ListObjectsV2Async (as plan Task 7 builds it) and GetObjectAsync really send.
        var (client, fake) = Build(source);
        fake.Respond = (req, _) => Task.FromResult(req.RequestUri!.Query.Length > 0
            ? FakeS3Handler.Xml(HttpStatusCode.OK, ListingXml("tok"))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("X") });
        foreach (var token in new[] { null, "1/ab+c=" })
        {
            OperationContext.Current.Value = new OperationContext(S3Operation.List) { ExpectedListPrefix = listPrefix, Capture = new ListPageCapture(), Credentials = Creds };
            try
            {
                await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = source.Bucket, Prefix = listPrefix, MaxKeys = 1000, ContinuationToken = token, Encoding = EncodingType.Url });
            }
            finally
            {
                OperationContext.Current.Value = null;
            }
        }

        Assert.Null(await Get(client, source.Bucket, "kb/a b+c.md"));
        Assert.Equal(3, fake.Requests.Count);
        Assert.Equal("", fake.Requests[2].RequestUri!.Query);
    }

    [Fact]
    public async Task A_307_answered_forever_stops_at_three_sends()
    {
        // Gate finding (spec Annexe B, consequence 4): without a cap the SDK followed a same-URL 307 without bound.
        var (client, fake) = Build(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 });
        fake.Respond = (req, _) => Task.FromResult(FakeS3Handler.Redirect(HttpStatusCode.TemporaryRedirect, req.RequestUri!.ToString()));
        var e = await Get(client, "demo", "kb/x");
        var guard = Assert.IsType<GuardException>(e is GuardException ? e : e?.InnerException ?? e);
        Assert.Equal(S3FailureKind.InvalidResponse, guard.Kind);
        Assert.Equal(PinningHandler.MaxSendsPerOperation, fake.Requests.Count);
    }

    [Fact]
    public async Task Ambient_user_agent_tokens_never_leave_and_the_signature_does_not_cover_them()
    {
        var (invoker, fake) = Guard();
        var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9000/okf/kb/a.md");
        request.Headers.TryAddWithoutValidation("User-Agent", "aws-sdk-dotnet-coreclr/4.1.0.0 lang/.NET_Core#10.0 exec-env/AMBIENTEXEC api/S3#4.1.0.0 app/AMBIENTMARK md/ClientAsync");
        OperationContext.Current.Value = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = "kb/a.md", Credentials = Creds };
        try
        {
            using var _ = await invoker.SendAsync(request, default);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }

        var ua = string.Join(' ', fake.Requests.Single().Headers.GetValues("User-Agent"));
        Assert.DoesNotContain("exec-env/", ua, StringComparison.Ordinal);
        Assert.DoesNotContain("app/", ua, StringComparison.Ordinal);
        Assert.Contains("api/S3#4.1.0.0", ua, StringComparison.Ordinal);

        // The removal cannot break a signature: on a request the real client signs, the User-Agent is not a signed header (gate G2).
        var (client, signed) = Build(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 });
        signed.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("X") });
        Assert.Null(await Get(client, "demo", "kb/x"));
        var authorization = string.Join(",", signed.Requests.Single().Headers.GetValues("Authorization"));
        var signedHeaders = authorization[(authorization.IndexOf("SignedHeaders=", StringComparison.Ordinal) + "SignedHeaders=".Length)..].Split(',')[0];
        Assert.Contains("host", signedHeaders.Split(';'));
        Assert.DoesNotContain("user-agent", signedHeaders.Split(';'));
    }

    /// <summary>Counts the SDK's credential requests: it asks again for every attempt it signs, retries included.</summary>
    private sealed class CountingCredentials : AWSCredentials
    {
        public int Calls;

        public override ImmutableCredentials GetCredentials()
        {
            Interlocked.Increment(ref Calls);
            return Creds;
        }

        public override Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromResult(GetCredentials());
    }

    [Fact]
    public async Task A_guard_refusal_is_not_retried()
    {
        // Gate G6. A refused request never reaches the network handler, so a retry cannot be seen there; it is seen in
        // the signer, which asks for credentials once per attempt. A refusal must cost what one successful send costs.
        var source = SourceRules.Normalize(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 });
        var credentials = new CountingCredentials();
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("X") }) };
        var client = ClientBuilder.Build(credentials, source, fake, out var httpClient);
        using (httpClient)
        {
            async Task<Exception?> Read(string expected, string asked)
            {
                OperationContext.Current.Value = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = expected };
                try
                {
                    using var r = await client.GetObjectAsync(new GetObjectRequest { BucketName = "demo", Key = asked });
                    return null;
                }
                catch (Exception e)
                {
                    return e;
                }
                finally
                {
                    OperationContext.Current.Value = null;
                }
            }

            Assert.Null(await Read("kb/a.md", "kb/a.md"));
            var perSend = Interlocked.Exchange(ref credentials.Calls, 0);
            Assert.True(perSend > 0);

            var e = await Read("kb/a.md", "kb/b.md");
            Assert.Equal(S3FailureKind.RefusedHost, Assert.IsType<GuardException>(e is GuardException ? e : e?.InnerException ?? e).Kind);
            Assert.Equal(perSend, credentials.Calls);
            Assert.Single(fake.Requests); // the successful read only
        }
    }

    public static TheoryData<S3BundleSource, string, string> Table => new()
    {
        { new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true }, "127.0.0.1:9000", "/okf/" },
        { new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://store.example:9000"), AllowInsecureHttp = true }, "okf.store.example:9000", "/" }, // a named host: an IP one needs ForcePathStyle (gate G3)
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1, ForcePathStyle = true }, "s3.eu-west-1.amazonaws.com:443", "/demo/" },
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 }, "demo.s3.eu-west-1.amazonaws.com:443", "/" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void The_plan_admits_exactly_one_authority_and_one_path_prefix(S3BundleSource source, string authority, string pathPrefix)
    {
        var plan = AddressingPlan.For(SourceRules.Normalize(source));
        Assert.Equal(authority, $"{plan.Host}:{plan.Port}");
        Assert.Equal(pathPrefix, plan.PathPrefix);
    }

}
