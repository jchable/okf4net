// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Internal;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>
/// Spec D3, Annexe B G1: the provider's client has neither the S3 retry policy (the only caller of the region
/// detector's own transport, BucketRegionDetector's out-of-band HEAD) nor any CSM handler. The gate found no scenario
/// that makes a plain client take that HEAD, so the guard is structural, with its counter-proof on a plain client.
/// </summary>
public class PipelineTests
{
    /// <summary>A plain client that records its own pipeline, as the provider's client does.</summary>
    private sealed class Probe(AWSCredentials c, AmazonS3Config cfg) : AmazonS3Client(c, cfg)
    {
        public Type? RetryPolicy { get; private set; }

        protected override void CustomizeRuntimePipeline(RuntimePipeline pipeline)
        {
            base.CustomizeRuntimePipeline(pipeline);
            RetryPolicy = pipeline.EnumerateHandlers().OfType<RetryHandler>().Single().RetryPolicy.GetType();
        }
    }

    /// <summary>
    /// The S3 retry policies, one per retry mode. Measured on AWSSDK.S3 4.1.0 (IL call inventory, 2026-11-18): the
    /// RetryForException[Async|Sync] of all three call AmazonS3RetryPolicy.SharedRetryForException[Async|Sync], whose
    /// async form calls BucketRegionDetector.DetectMismatchWithHeadBucketFallbackAsync — the out-of-band HEAD of G1.
    /// AmazonS3StandardRetryPolicy DERIVES from StandardRetryPolicy, hence the exact type comparisons below.
    /// (RequestRetryMode has no Legacy member in AWSSDK.Core 4.x; AmazonS3RetryPolicy stays listed as the family's root.)
    /// </summary>
    private static readonly Type[] S3Policies = [typeof(AmazonS3RetryPolicy), typeof(AmazonS3StandardRetryPolicy), typeof(AmazonS3AdaptiveRetryPolicy)];

    [Theory]
    [InlineData(RequestRetryMode.Standard, typeof(AmazonS3StandardRetryPolicy))] // the default mode (no RetryMode set: measured the same)
    [InlineData(RequestRetryMode.Adaptive, typeof(AmazonS3AdaptiveRetryPolicy))]
    public void Counter_proof_a_plain_client_carries_an_S3_retry_policy_in_every_mode(RequestRetryMode mode, Type expected)
    {
        using var plain = new Probe(new BasicAWSCredentials("A", "S"), new AmazonS3Config { RegionEndpoint = RegionEndpoint.EUWest1, RetryMode = mode });
        Assert.Equal(expected, plain.RetryPolicy);
    }

    [Fact]
    public void Counter_proof_a_plain_client_with_no_retry_mode_carries_the_S3_standard_policy()
    {
        using var plain = new Probe(new BasicAWSCredentials("A", "S"), new AmazonS3Config { RegionEndpoint = RegionEndpoint.EUWest1 });
        Assert.Equal(typeof(AmazonS3StandardRetryPolicy), plain.RetryPolicy);
    }

    [Fact]
    public void The_providers_client_carries_the_core_standard_policy_and_no_S3_one()
    {
        var source = SourceRules.Normalize(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 });
        using var client = (ProviderS3Client)ClientBuilder.Build(new SnapshotCredentials(), source, new FakeS3Handler(), out var http);
        using (http)
        {
            // Read from the pipeline's own RetryHandler after customization, not from what the client meant to install.
            Assert.Equal([typeof(StandardRetryPolicy)], client.RetryPolicyTypes);
            Assert.DoesNotContain(client.RetryPolicyTypes, S3Policies.Contains);
            Assert.Single(client.HandlerTypes, t => t == typeof(RetryHandler));
        }
    }

    /// <summary>
    /// SMOKE CHECK, NOT PROOF: the SDK adds the two CSM handlers only when CSM is enabled for the PROCESS (AWS_CSM_ENABLED,
    /// a profile, AWSConfigs), and it is not in the test host — enabling it here would reach every other test's clients
    /// — so a plain client of this process lacks them too and this assertion cannot fail by itself. The executed proof
    /// is HostileEnvironmentTests: under AWS_CSM_ENABLED (variables) and csm_enabled (profile), the plain witness sends
    /// datagrams and the provider none.
    /// </summary>
    [Fact]
    public void The_providers_client_has_no_CSM_handler()
    {
        var source = SourceRules.Normalize(new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 });
        using var client = (ProviderS3Client)ClientBuilder.Build(new SnapshotCredentials(), source, new FakeS3Handler(), out var http);
        using (http)
        {
            Assert.NotEmpty(client.HandlerTypes);
            Assert.DoesNotContain(client.HandlerTypes, t => t == typeof(CSMCallAttemptHandler));
            Assert.DoesNotContain(client.HandlerTypes, t => t == typeof(CSMCallEventHandler));
        }
    }
}
