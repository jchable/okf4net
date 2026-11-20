// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using System.Text.RegularExpressions;
using OKF4net.Providers;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>
/// #284: a store that validates the region of the SigV4 credential scope (Wasabi regional endpoints, Scaleway, Garage...
/// are believed to; none measured) answers 400 AuthorizationHeaderMalformed to the default scope, <c>us-east-1</c>. The
/// scripted store below enforces the scope the way such a store does, reading the region out of the request's own
/// <c>Authorization</c> header, so the test fails without the option and passes with it.
/// </summary>
public class SigningRegionTests
{
    private static readonly Regex Scope = new(@"Credential=[^/]+/\d{8}/(?<region>[^/]+)/s3/aws4_request", RegexOptions.CultureInvariant);

    private static FakeS3Handler StoreEnforcing(string region)
    {
        var inner = S3Test.Serving(new Dictionary<string, byte[]> { ["kb/a.md"] = "---\ntype: Note\n---\n"u8.ToArray() });
        var store = new FakeS3Handler();
        store.Respond = (req, ct) =>
        {
            var auth = req.Headers.TryGetValues("Authorization", out var v) ? string.Concat(v) : "";
            var signed = Scope.Match(auth).Groups["region"].Value;
            return signed == region
                ? inner.Respond(req, ct)
                : Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.BadRequest, FakeS3Handler.Error("AuthorizationHeaderMalformed")));
        };
        return store;
    }

    private static async Task<List<string>> List(S3BundleProvider p)
    {
        var names = new List<string>();
        await foreach (var l in p.EnumerateAsync(null, BundleLimits.Default, default))
        {
            names.Add(l.RawName);
        }

        return names;
    }

    [Fact]
    public async Task A_store_that_checks_the_scope_region_refuses_the_default_and_accepts_the_declared_one()
    {
        // Without the option: signed with us-east-1, refused 400. The failure is the one the ticket describes.
        using var plain = S3Test.Provider(StoreEnforcing("eu-west-1"));
        var refused = await Assert.ThrowsAsync<S3RequestException>(() => List(plain));
        Assert.Equal(S3FailureKind.BucketUnavailable, refused.Kind);
        Assert.Equal(400, refused.StatusCode);

        // With it: the scope carries the declared region and the listing succeeds.
        using var declared = S3Test.Provider(StoreEnforcing("eu-west-1"), S3Test.Local() with { SigningRegion = "eu-west-1" });
        Assert.Equal(["a.md"], await List(declared));
    }

    [Fact]
    public async Task The_default_scope_is_still_us_east_1()
    {
        // Positive control for the helper itself: a store enforcing us-east-1 accepts a source with no SigningRegion.
        using var p = S3Test.Provider(StoreEnforcing("us-east-1"));
        Assert.Equal(["a.md"], await List(p));
    }
}
