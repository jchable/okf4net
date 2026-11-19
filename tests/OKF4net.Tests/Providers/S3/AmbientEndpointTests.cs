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

    /// <summary>The variables a row of <see cref="Rows"/> may set, each saved and restored around the test.</summary>
    private static readonly string[] RowVariables = ["AWS_S3_US_EAST_1_REGIONAL_ENDPOINT"];

    /// <summary>
    /// Each row: an ambient setting that moves a Region-configured plain client to another host (the measured host is the
    /// row's witness), a region and an addressing mode. Measured on AWSSDK.S3 4.1.0 / AWSSDK.Core 4.1.0, 2026-11-19: before
    /// <c>USEast1RegionalEndpointValue = Regional</c> was set, the provider's every us-east-1 operation failed
    /// <c>S3 list failed: RefusedHost</c> with no request sent under the legacy setting, in both addressing modes. The SDK
    /// reads the variable each time a client is configured, so it can be a row here.
    /// Not rows, because the SDK reads them once per process (measured: in the full suite, a plain client set after them
    /// does not move): the profile key <c>s3_us_east_1_regional_endpoint = legacy</c> (measured in fresh processes: the
    /// plain client goes to s3.amazonaws.com, the provider failed RefusedHost before the fix and goes to the regional
    /// host since; the same configuration line neutralizes both sources), and AWS_USE_FIPS_ENDPOINT /
    /// AWS_USE_DUALSTACK_ENDPOINT, witnessed in a child process by <see cref="HostileEnvironmentTests"/>.
    /// </summary>
    public static TheoryData<string, string, bool, string> Rows() => new()
    {
        { "us-east-1-legacy-variable", "us-east-1", true, "s3.amazonaws.com" },
        { "us-east-1-legacy-variable", "us-east-1", false, "okf.s3.amazonaws.com" },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task An_ambient_setting_moves_a_plain_client_but_never_the_providers_Region_source(string row, string region, bool pathStyle, string witnessHost)
    {
        var saved = RowVariables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in RowVariables)
            {
                Environment.SetEnvironmentVariable(v, null);
            }

            switch (row)
            {
                case "us-east-1-legacy-variable":
                    Environment.SetEnvironmentVariable("AWS_S3_US_EAST_1_REGIONAL_ENDPOINT", "legacy");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(row), row, null);
            }

            var endpoint = RegionEndpoint.GetBySystemName(region);

            // POSITIVE WITNESS, first: a plain client on the same region and addressing mode, over the same kind of fake,
            // IS sent to the row's host (measured: exactly that host), so the setting is really read in this process.
            var witness = Fake(pathStyle);
            using (var http = new HttpClient(witness, disposeHandler: false))
            using (var plain = new AmazonS3Client(S3Test.Creds, new AmazonS3Config { RegionEndpoint = endpoint, ForcePathStyle = pathStyle, HttpClientFactory = new ProviderHttpClientFactory(http) }))
            {
                using var _ = await plain.GetObjectAsync(new GetObjectRequest { BucketName = "okf", Key = "kb/a.md" });
            }

            output.WriteLine("witness hosts: " + Hosts(witness));
            Assert.NotEmpty(witness.Requests);
            Assert.All(witness.Requests, r => Assert.Equal(witnessHost, r.RequestUri!.Host));

            // The provider, on the same environment: it loads, and every request goes to the plan's regional host.
            var source = new S3BundleSource { Bucket = "okf", Prefix = "kb", Region = endpoint, ForcePathStyle = pathStyle, RequestTimeout = TimeSpan.FromMinutes(5) };
            var expected = AddressingPlan.For(SourceRules.Normalize(source)).Host;
            Assert.Equal((pathStyle ? "" : "okf.") + $"s3.{region}.amazonaws.com", expected);
            var fake = Fake(pathStyle);
            using (var provider = S3Test.Provider(fake, source))
            {
                var bundle = await Bundle.LoadAsync(provider, null, CancellationToken.None);
                Assert.Single(bundle.Concepts);
            }

            output.WriteLine("provider hosts: " + Hosts(fake));
            Assert.NotEmpty(fake.Requests); // a request was sent: the host assertion below is not vacuous
            Assert.All(fake.Requests, r => Assert.Equal(expected, r.RequestUri!.Host));
        }
        finally
        {
            foreach (var (k, v) in saved)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    /// <summary>The scripted store, path-style or virtual-hosted (the bucket then named by the host, not the path).</summary>
    private static FakeS3Handler Fake(bool pathStyle)
    {
        var fake = Fake();
        if (!pathStyle)
        {
            var served = fake.Respond;
            fake.Respond = (req, ct) =>
            {
                var uri = req.RequestUri!;
                using var asPath = new HttpRequestMessage(req.Method, new Uri(uri.GetLeftPart(UriPartial.Authority) + "/okf" + uri.PathAndQuery));
                return served(asPath, ct);
            };
        }

        return fake;
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
