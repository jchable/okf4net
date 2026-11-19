// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using Amazon;
using OKF4net.Providers;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>
/// #291: reading a public bucket without credentials is an explicit option (<see cref="S3BundleSource.Anonymous"/>), never
/// an accident: no host credentials are involved at all, and no request carries an <c>Authorization</c> header.
/// </summary>
public class AnonymousTests
{
    private static readonly Dictionary<string, byte[]> Objects = new() { ["kb/a.md"] = "---\ntype: Note\n---\n"u8.ToArray() };

    private static S3BundleSource Anon() => S3Test.Local() with { Anonymous = true };

    private static async Task<List<string>> List(S3BundleProvider p)
    {
        var names = new List<string>();
        await foreach (var l in p.EnumerateAsync(null, BundleLimits.Default, default))
        {
            names.Add(l.RawName);
        }

        return names;
    }

    private static bool Signed(HttpRequestMessage r) => r.Headers.Contains("Authorization");

    [Fact]
    public async Task An_anonymous_source_lists_and_reads_without_signing_anything()
    {
        var fake = S3Test.Serving(Objects);
        using var p = new S3BundleProvider(Anon(), fake);

        Assert.Equal(["a.md"], await List(p));
        using var read = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        Assert.NotNull(read);

        Assert.NotEmpty(fake.Requests);
        Assert.All(fake.Requests, r => Assert.False(Signed(r), "an anonymous request must carry no Authorization header"));
    }

    [Fact]
    public async Task Positive_control_a_source_with_credentials_does_sign()
    {
        // The same fake and the same operations: with credentials the Authorization header IS there, so the test above
        // can only pass because the anonymous path really sends none.
        var fake = S3Test.Serving(Objects);
        using var p = S3Test.Provider(fake);
        Assert.Equal(["a.md"], await List(p));
        Assert.All(fake.Requests, r => Assert.True(r.Headers.Contains("Authorization")));
    }

    [Fact]
    public async Task A_store_that_requires_no_authorization_header_is_read()
    {
        // A scripted public store: it refuses (403 AccessDenied) any request that carries a signature.
        var inner = S3Test.Serving(Objects);
        var store = new FakeS3Handler
        {
            Respond = (req, ct) => req.Headers.Contains("Authorization")
                ? Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.Forbidden, FakeS3Handler.Error("AccessDenied")))
                : inner.Respond(req, ct),
        };
        using var anonymous = new S3BundleProvider(Anon(), store);
        Assert.Equal(["a.md"], await List(anonymous));

        using var signed = S3Test.Provider(store);
        var refused = await Assert.ThrowsAsync<S3RequestException>(() => List(signed));
        Assert.Equal(403, refused.StatusCode);
    }

    [Fact]
    public void The_two_constructors_never_cross()
    {
        var fake = new FakeS3Handler();
        var withCredentials = Assert.Throws<ArgumentException>(() => new S3BundleProvider(S3Test.Creds, Anon(), fake));
        Assert.Equal("An anonymous source takes no credentials: use the constructor without them.", withCredentials.Message.Split(" (Parameter")[0]);

        var without = Assert.Throws<ArgumentException>(() => new S3BundleProvider(S3Test.Local(), fake));
        Assert.Equal("A source without Anonymous needs credentials: use the constructor that takes them.", without.Message.Split(" (Parameter")[0]);

        Assert.Throws<ArgumentNullException>(() => new S3BundleProvider(null!, S3Test.Local(), fake));
        Assert.Throws<ArgumentNullException>(() => new S3BundleProvider(null!, fake));
    }

    // Known answers computed OUTSIDE the code under test: the framing of the spec (section 4.3) plus the segment "|anon" appended
    // to the endpoint identity only when the option is set (so every existing Id is unchanged):
    //   printf 's3v1\nhttps://store.example:443|path|anon\ndemo\nkb' | sha256sum   (first 32 hex digits)
    //   printf 's3v1\naws:eu-west-1|vhost|anon\ndemo\n' | sha256sum
    [Fact]
    public void Anonymous_is_part_of_the_id_only_when_set()
    {
        var url = new S3BundleSource { Bucket = "demo", Prefix = "kb", ServiceUrl = new Uri("https://store.example"), ForcePathStyle = true };
        Assert.Equal("s3:9c0dfc848f8fd95cc9489be35146063e", SourceRules.Normalize(url).Id);
        Assert.Equal("s3:13166085d3625258c3e59fcb3d30c65a", SourceRules.Normalize(url with { Anonymous = true }).Id);
        Assert.Equal("s3:e8eb40e61670ee597cdc96704970a523", SourceRules.Normalize(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1, Anonymous = true }).Id);
        Assert.True(SourceRules.Normalize(url with { Anonymous = true }).Anonymous);
        Assert.False(SourceRules.Normalize(url).Anonymous);
    }
}
