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
        Local(bucket: "demo\n"), Local(bucket: "demo\r\n"), Local(bucket: "de\u0001mo"), Local(prefix: "kb\n"),
        Local() with { ServiceUrl = new Uri("http://user:pw@127.0.0.1:9000") },
        Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/gateway") },
        Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/?x=1") },
        Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/#f") },
        Local() with { AllowInsecureHttp = false },
        Local() with { Region = RegionEndpoint.EUWest1 },
        Local() with { ServiceUrl = null },
        Local() with { RequestTimeout = TimeSpan.Zero }, Local() with { MaxConcurrentRequests = 0 },
        Local() with { MaxObjectBytes = 0 }, Local() with { MaxObjectBytes = (long)Array.MaxLength + 1 },
        new S3BundleSource { Bucket = "my.dotted.bucket", Region = RegionEndpoint.EUWest1 }, // virtual-hosted + dots: refused (spec §4.4)
        new S3BundleSource { Bucket = "my.dotted.bucket", ServiceUrl = new Uri("https://store.example") },      // dots without ForcePathStyle, any endpoint (gate G3)
        new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), AllowInsecureHttp = true }, // an IP ServiceUrl without ForcePathStyle (gate G3)
        new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.CNNorth1 }, // partition outside amazonaws.com: refused in v1 (gate G3)
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_source_is_refused(S3BundleSource source) =>
        Assert.Throws<ArgumentException>(() => SourceRules.Normalize(source));

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
