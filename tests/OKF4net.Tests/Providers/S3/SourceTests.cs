// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D5, D6, §4.3 (Id, Location).</summary>
public class SourceTests
{
    private static S3BundleSource Local(string bucket = "okf", string prefix = "kb") =>
        new() { Bucket = bucket, Prefix = prefix, ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true };

    public static TheoryData<S3BundleSource> Invalid => new()
    {
        Local(prefix: "/kb"), Local(prefix: "kb/"), Local(prefix: "a//b"), Local(prefix: ".."), Local(prefix: "a/./b"),
        Local(prefix: "kb\uD800"),
        Local() with { DisplayName = "a/b" }, Local() with { DisplayName = "a:b" }, Local() with { DisplayName = "" },
        Local(bucket: "arn:aws:s3:eu-west-1:123:accesspoint/x"), Local(bucket: "Upper"), Local(bucket: "ab"),
        Local(bucket: "my-bucket--x-s3"), Local(bucket: "my-bucket--ol-s3"), Local(bucket: "my-bucket-s3alias"), Local(bucket: "a..b"), Local(bucket: "192.168.0.1"),
        // Bucket forms the spec's D6 does not list but S3 reserves (xn-- punycode, sthree-, the sample namespace, table buckets):
        // the spec is silent on them, the code refuses them, and these rows characterize that (no spec text requires or forbids it).
        Local(bucket: "xn--demo"), Local(bucket: "sthree-demo"), Local(bucket: "amzn-s3-demo-bucket"), Local(bucket: "my-bucket--table-s3"),
        Local(bucket: "demo\n"), Local(bucket: "demo\r\n"), Local(bucket: "de\u0001mo"), Local(prefix: "kb\n"),
        Local() with { ServiceUrl = new Uri("http://user:pw@127.0.0.1:9000") },
        Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/gateway") },
        Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/?x=1") },
        Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/#f") },
        Local() with { AllowInsecureHttp = false },
        Local() with { Region = RegionEndpoint.EUWest1 },
        Local() with { ServiceUrl = null },
        Local() with { RequestTimeout = TimeSpan.Zero }, Local() with { RequestTimeout = TimeSpan.FromSeconds(-1) }, Local() with { MaxConcurrentRequests = 0 },
        Local() with { MaxConcurrentRequests = -1 },
        Local() with { ServiceUrl = new Uri("ftp://127.0.0.1:21") },           // a scheme that is neither http nor https
        Local() with { ServiceUrl = new Uri("file:///tmp/bucket") },
        new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://[::1]:9000"), AllowInsecureHttp = true }, // an IPv6 ServiceUrl without ForcePathStyle (gate G3)
        Local() with { MaxObjectBytes = 0 }, Local() with { MaxObjectBytes = (long)Array.MaxLength + 1 },
        new S3BundleSource { Bucket = "my.dotted.bucket", Region = RegionEndpoint.EUWest1 }, // virtual-hosted + dots: refused (spec §4.4)
        new S3BundleSource { Bucket = "my.dotted.bucket", ServiceUrl = new Uri("https://store.example") },      // dots without ForcePathStyle, any endpoint (gate G3)
        new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), AllowInsecureHttp = true }, // an IP ServiceUrl without ForcePathStyle (gate G3)
        new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.CNNorth1 }, // partition outside amazonaws.com: refused in v1 (gate G3)
    };

    /// <summary>Every message <see cref="SourceRules.Normalize"/> may throw: a closed vocabulary, naming no configured value.</summary>
    private static readonly string[] RefusalMessages =
    [
        "The bucket is not a general-purpose S3 bucket name.",
        "The prefix must be empty or valid bundle key segments in well-formed Unicode.",
        "The display name must be one bundle key segment, without ':'.",
        "Set exactly one of Region and ServiceUrl.",
        "Timeouts and bounds must be positive (MaxObjectBytes at most Array.MaxLength).",
        "A bucket name with '.' needs ForcePathStyle.",
        "ServiceUrl must be an http(s) origin only: no user info, path, query or fragment.",
        "An http:// ServiceUrl needs AllowInsecureHttp.",
        "An IP-address ServiceUrl needs ForcePathStyle.",
        "Only regions of the amazonaws.com partition are supported.",
    ];

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_source_is_refused(S3BundleSource source)
    {
        // Exactly ArgumentException (Assert.Throws demands the exact type), about the 'source' parameter, with a message
        // from the closed vocabulary above: never the bucket, prefix, display name or URL the host configured.
        var ex = Assert.Throws<ArgumentException>(() => SourceRules.Normalize(source));
        Assert.Equal("source", ex.ParamName);
        Assert.Contains(RefusalMessages, m => ex.Message.StartsWith(m, StringComparison.Ordinal));
        foreach (var value in new[] { source.Bucket, source.Prefix, source.DisplayName, source.ServiceUrl?.Host }.Where(v => !string.IsNullOrEmpty(v)))
        {
            Assert.DoesNotContain(value!, ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("xn--demo")]
    [InlineData("sthree-demo")]
    [InlineData("amzn-s3-demo-bucket")]
    [InlineData("my-bucket--table-s3")]
    public void Each_reserved_bucket_form_is_refused_as_a_bucket_name(string bucket)
    {
        // Pins the reason, not only the type: refused by the bucket rule, not by some other rule of Local().
        var ex = Assert.Throws<ArgumentException>(() => SourceRules.Normalize(Local(bucket: bucket)));
        Assert.StartsWith("The bucket is not a general-purpose S3 bucket name.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("source", ex.ParamName);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:21")]
    [InlineData("file:///tmp/bucket")]
    public void A_non_http_ServiceUrl_scheme_is_refused_as_an_origin_problem(string url)
    {
        var ex = Assert.Throws<ArgumentException>(() => SourceRules.Normalize(Local() with { ServiceUrl = new Uri(url) }));
        Assert.StartsWith("ServiceUrl must be an http(s) origin", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_IPv6_ServiceUrl_needs_ForcePathStyle_and_is_admitted_with_it()
    {
        var v6 = new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://[::1]:9000"), AllowInsecureHttp = true };
        var ex = Assert.Throws<ArgumentException>(() => SourceRules.Normalize(v6));
        Assert.StartsWith("An IP-address ServiceUrl needs ForcePathStyle.", ex.Message, StringComparison.Ordinal);
        // Positive control: the same source with path style is valid, so the refusal above is about addressing only.
        var admitted = SourceRules.Normalize(v6 with { ForcePathStyle = true });
        Assert.Equal("http://[::1]:9000|path", admitted.EndpointIdentity);
    }

    [Fact]
    public void A_negative_RequestTimeout_is_refused_for_the_bounds_reason()
    {
        var ex = Assert.Throws<ArgumentException>(() => SourceRules.Normalize(Local() with { RequestTimeout = TimeSpan.FromSeconds(-1) }));
        Assert.StartsWith("Timeouts and bounds must be positive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Region_only_source_is_valid_and_its_endpoint_identity_is_the_region()
    {
        var n = SourceRules.Normalize(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 });
        Assert.Equal("aws:eu-west-1|vhost", n.EndpointIdentity);
        Assert.Null(n.ServiceUrl);
        Assert.False(n.PathStyle);
        Assert.Equal("s3:" + n.Id[3..11], n.Location);
        Assert.Equal("aws:eu-west-1|path", SourceRules.Normalize(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1, ForcePathStyle = true }).EndpointIdentity);
    }

    // Expected values come from the framing the spec documents (design spec section 4.3, row Id):
    //   "s3:" + lowercase hex(SHA-256(UTF-8("s3v1\n" + endpoint + "\n" + bucket + "\n" + prefix)))[..32]
    // and were computed OUTSIDE the code under test with coreutils:  printf 's3v1\n<endpoint>\n<bucket>\n<prefix>' | sha256sum
    // (first 32 hex digits). The Id is a cross-version contract (catalog deduplication): changing a literal here is a break.
    [Theory]
    [InlineData("http://127.0.0.1:9000", "okf", "kb", true, "s3:372717ae1b07702d2f0a0e51975024c2")]
    [InlineData("https://store.example", "demo", "kb/sub", false, "s3:72fc94c7a9ab1acffccba52a0bea0f2f")]
    public void The_id_framing_is_a_known_answer_for_a_service_url(string url, string bucket, string prefix, bool pathStyle, string expected)
    {
        var source = new S3BundleSource
        {
            Bucket = bucket,
            Prefix = prefix,
            ServiceUrl = new Uri(url),
            ForcePathStyle = pathStyle,
            AllowInsecureHttp = url.StartsWith("http:", StringComparison.Ordinal),
        };
        Assert.Equal(expected, SourceRules.Normalize(source).Id);
    }

    [Fact]
    public void The_id_framing_is_a_known_answer_for_a_region()
    {
        // printf 's3v1\naws:eu-west-1|vhost\ndemo\n' | sha256sum, first 32 hex digits (empty prefix: the trailing LF stays).
        Assert.Equal("s3:63d18a068ce5a930b78e4a652ad39cd2", SourceRules.Normalize(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 }).Id);
    }

    /// <summary>Counts its own disposal: the network object a provider builds and, when its source is refused, must not leave alive.</summary>
    private sealed class TrackedNetwork : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void A_refused_source_leaves_no_network_object_alive(S3BundleSource source)
    {
        // Minors A2: the public constructor built its SocketsHttpHandler BEFORE the source was validated, and nothing
        // disposed it when validation threw. The network seam is a factory now: whatever it built must be disposed
        // (and, validation coming first, it is not even called).
        var built = new List<TrackedNetwork>();
        Assert.Throws<ArgumentException>(() => new S3BundleProvider(S3Test.Creds, source, () =>
        {
            var network = new TrackedNetwork();
            built.Add(network);
            return network;
        }));
        Assert.All(built, n => Assert.True(n.Disposed));
    }

    [Fact]
    public void A_valid_source_calls_the_network_factory_once_and_the_provider_disposes_what_it_built()
    {
        // The positive witness of the test above: the factory IS called for a valid source, so an empty list there means
        // "validated first", not "the seam is never reached"; and Dispose releases the handler through the HttpClient.
        var built = new List<TrackedNetwork>();
        var p = new S3BundleProvider(S3Test.Creds, Local(), () =>
        {
            var network = new TrackedNetwork();
            built.Add(network);
            return network;
        });
        Assert.Single(built);
        Assert.False(built[0].Disposed);
        p.Dispose();
        Assert.True(built[0].Disposed);
    }

    [Fact]
    public void The_id_is_opaque_stable_and_follows_the_effective_configuration()
    {
        var a = SourceRules.Normalize(Local());
        Assert.Matches("^s3:[0-9a-f]{32}$", a.Id);
        Assert.Equal(a.Id, SourceRules.Normalize(Local()).Id);
        Assert.Equal(a.Id, SourceRules.Normalize(Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/") }).Id);
        Assert.NotEqual(a.Id, SourceRules.Normalize(Local(prefix: "kb2")).Id);
        Assert.NotEqual(a.Id, SourceRules.Normalize(Local(bucket: "okf2")).Id);
        Assert.NotEqual(a.Id, SourceRules.Normalize(Local() with { ServiceUrl = new Uri("http://127.0.0.1:9001") }).Id);
        // Addressing is part of the identity. A host name, not the IP of Local(): an IP ServiceUrl without ForcePathStyle is refused (gate G3).
        var named = Local() with { ServiceUrl = new Uri("http://store.local:9000") };
        Assert.NotEqual(SourceRules.Normalize(named).Id, SourceRules.Normalize(named with { ForcePathStyle = false }).Id);
        Assert.DoesNotContain("okf", a.Id[3..], StringComparison.Ordinal);
    }

    [Fact]
    public void The_location_never_names_the_bucket()
    {
        var plain = SourceRules.Normalize(Local(bucket: "secret-bucket"));
        Assert.Equal("s3:" + plain.Id[3..11] + "/kb", plain.Location);
        Assert.Equal("s3:docs/kb", SourceRules.Normalize(Local(bucket: "secret-bucket") with { DisplayName = "docs" }).Location);
        Assert.Equal("s3:docs", SourceRules.Normalize(Local(bucket: "secret-bucket", prefix: "") with { DisplayName = "docs" }).Location);
        Assert.DoesNotContain("secret-bucket", plain.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prefix_holding_U_FFFD_is_valid_and_distinct_from_any_other() =>
        Assert.NotEqual(SourceRules.Normalize(Local(prefix: "kb�")).Id, SourceRules.Normalize(Local(prefix: "kb")).Id);
}
