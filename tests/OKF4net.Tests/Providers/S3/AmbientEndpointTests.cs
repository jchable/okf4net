// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;
using Xunit.Abstractions;

namespace OKF4net.Tests.Providers.S3;

/// <summary>
/// Spec D2, T1 "Environnement hostile" ("mêmes URI"): on a Region source — the one source an ambient endpoint redirects,
/// since an explicit ServiceUrl wins over it (HostileEnvironmentTests) — AWS_ENDPOINT_URL_S3 / AWS_ENDPOINT_URL never
/// change where the provider's credential-bearing requests go. In-process, over the scripted fake (no request leaves the
/// machine), with the process environment set and restored around each test; the collection runs alone, so no other test
/// sees the variables.
/// </summary>
[Collection(AmbientEnvironmentCollection.Name)]
public class AmbientEndpointTests(ITestOutputHelper output)
{
    private const string Evil = "evil.invalid";

    private static readonly string[] Variables = ["AWS_ENDPOINT_URL_S3", "AWS_ENDPOINT_URL"];

    private static S3BundleSource Source() =>
        new() { Bucket = "okf", Prefix = "kb", Region = RegionEndpoint.EUWest1, ForcePathStyle = true, RequestTimeout = TimeSpan.FromMinutes(5) };

    private static FakeS3Handler Fake() => S3Test.Serving(S3Test.Objects(("kb/a.md", "---\ntype: Note\ntitle: T\ndescription: D.\n---\n\nBody.\n")));

    [Fact]
    public async Task An_ambient_endpoint_redirects_a_plain_client_but_never_the_providers_on_a_Region_source()
    {
        var saved = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in Variables)
            {
                Environment.SetEnvironmentVariable(v, "https://" + Evil);
            }

            // POSITIVE WITNESS, first: a plain client on the same region, over the same kind of fake, IS sent to the ambient
            // endpoint (measured: 1 request, to evil.invalid). It also proves the SDK reads the variables when this client is
            // configured, not once per process before they were set: otherwise nothing below could be believed.
            var witness = Fake();
            using (var http = new HttpClient(witness, disposeHandler: false))
            using (var plain = new AmazonS3Client(S3Test.Creds, new AmazonS3Config { RegionEndpoint = RegionEndpoint.EUWest1, ForcePathStyle = true, HttpClientFactory = new ProviderHttpClientFactory(http) }))
            {
                using var _ = await plain.GetObjectAsync(new GetObjectRequest { BucketName = "okf", Key = "kb/a.md" });
            }

            output.WriteLine("witness hosts: " + Hosts(witness));
            Assert.True(witness.Count(Evil) >= 1, "witness: the plain client was not redirected; seen hosts: " + Hosts(witness));

            // The provider, on the same environment: every request goes to the plan's regional host, none to the ambient one.
            var fake = Fake();
            var expected = AddressingPlan.For(SourceRules.Normalize(Source())).Host;
            using (var provider = S3Test.Provider(fake, Source()))
            {
                var bundle = await Bundle.LoadAsync(provider, null, CancellationToken.None);
                Assert.Single(bundle.Concepts);
            }

            output.WriteLine("provider hosts: " + Hosts(fake));
            Assert.Equal(0, fake.Count(Evil));
            Assert.NotEmpty(fake.Requests); // a request was sent: the host assertion below is not vacuous
            Assert.All(fake.Requests, r => Assert.Equal(expected, r.RequestUri!.Host));
            Assert.Equal("s3.eu-west-1.amazonaws.com", expected);
        }
        finally
        {
            foreach (var (k, v) in saved)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    private static string Hosts(FakeS3Handler fake) => string.Join(", ", fake.Requests.Select(r => r.RequestUri!.Authority));
}

/// <summary>
/// Runs the tests that set AWS_* variables of the test process with no other test alongside them: xunit executes a
/// collection that disables parallelization on its own, after all the parallel ones.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AmbientEnvironmentCollection
{
    /// <summary>The collection's name, shared by the definition and the test classes.</summary>
    public const string Name = "Ambient AWS environment";
}
