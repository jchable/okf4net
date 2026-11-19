// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.Internal;
using Amazon.S3;
using Amazon.S3.Internal;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>
/// Spec Annexe B G1: <see cref="ProviderS3Client.Neutralize"/> applied to pipelines the test builds, so the cases a client
/// of this process never shows (CSM handlers, present only when CSM is enabled for the whole process) are executed.
/// </summary>
public class ProviderS3ClientTests
{
    private static AmazonS3Config Config() => new() { RegionEndpoint = RegionEndpoint.EUWest1 };

    [Fact]
    public void The_recorded_retry_policies_are_read_back_from_the_pipeline_not_from_the_intent()
    {
        var config = Config();

        // Two RetryHandlers: ReplaceHandler replaces one, and the other keeps its S3 policy, the one that reaches the
        // region detector. A record of what the customization MEANT to install would report the standard policy alone.
        var pipeline = new RuntimePipeline(new List<IPipelineHandler>
        {
            new RetryHandler(new AmazonS3StandardRetryPolicy(config)),
            new RetryHandler(new AmazonS3StandardRetryPolicy(config)),
        });

        var (policies, _) = ProviderS3Client.Neutralize(pipeline, config);

        Assert.Equal(2, policies.Count);
        Assert.Contains(typeof(StandardRetryPolicy), policies);
        Assert.Contains(typeof(AmazonS3StandardRetryPolicy), policies);
    }

    [Fact]
    public void The_CSM_handlers_and_the_S3_retry_policy_leave_a_pipeline_that_has_them()
    {
        var config = Config();
        var pipeline = new RuntimePipeline(new List<IPipelineHandler>
        {
            new RetryHandler(new AmazonS3StandardRetryPolicy(config)),
            new CSMCallAttemptHandler(),
            new CSMCallEventHandler(),
        });

        // Counter-proof: the pipeline under test does hold what the customization must remove.
        Assert.Contains(pipeline.EnumerateHandlers(), h => h is CSMCallAttemptHandler);
        Assert.Contains(pipeline.EnumerateHandlers(), h => h is CSMCallEventHandler);

        var (policies, handlers) = ProviderS3Client.Neutralize(pipeline, config);

        Assert.Equal([typeof(StandardRetryPolicy)], policies);
        Assert.DoesNotContain(typeof(CSMCallAttemptHandler), handlers);
        Assert.DoesNotContain(typeof(CSMCallEventHandler), handlers);
        Assert.Equal([typeof(StandardRetryPolicy)], pipeline.EnumerateHandlers().OfType<RetryHandler>().Select(h => h.RetryPolicy.GetType()));
    }
}
