// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;
using Amazon.S3;

namespace OKF4net.Providers.S3.Internal;

/// <summary>Builds the provider's client with a hermetic configuration (spec D2, §4.4; every setting below is a row of gate G2).</summary>
internal static class ClientBuilder
{
    public static AmazonS3Client Build(AWSCredentials credentials, NormalizedSource source, HttpMessageHandler network, out HttpClient httpClient)
    {
        var plan = AddressingPlan.For(source);
        var chain = new PinningHandler(plan) { InnerHandler = new BoundingHandler { InnerHandler = network } };
        httpClient = new HttpClient(chain, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        return new ProviderS3Client(credentials, Configure(source, new ProviderHttpClientFactory(httpClient)));
    }

    /// <summary>The same client WITHOUT the guard, for the counter-proof tests only (never reachable from the public API).</summary>
    internal static AmazonS3Client BuildUnguardedForCounterProof(AWSCredentials credentials, NormalizedSource source, HttpMessageHandler network)
    {
        var httpClient = new HttpClient(network, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        return new AmazonS3Client(credentials, Configure(source, new ProviderHttpClientFactory(httpClient)));
    }

    /// <summary>The network handler of a real provider: no redirect following, no ambient proxy (spec §4.4).</summary>
    public static SocketsHttpHandler Network() => new() { AllowAutoRedirect = false, UseProxy = false };

    private static AmazonS3Config Configure(NormalizedSource source, HttpClientFactory factory)
    {
        var config = new AmazonS3Config
        {
            IgnoreConfiguredEndpointUrls = true,
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = 2,
            UseFIPSEndpoint = false,
            UseDualstackEndpoint = false,
            UseAccelerateEndpoint = false,
            UseArnRegion = false,
            DisableMultiregionAccessPoints = true,
            DefaultConfigurationMode = DefaultConfigurationMode.Standard,
            DisableClockSkewCorrection = false,
            ForcePathStyle = source.PathStyle,
            AllowAutoRedirect = false,
            CacheHttpClient = false,
            Timeout = Timeout.InfiniteTimeSpan,
            HttpClientFactory = factory,
            RequestChecksumCalculation = source.Checksums == S3ChecksumMode.WhenRequired ? RequestChecksumCalculation.WHEN_REQUIRED : RequestChecksumCalculation.WHEN_SUPPORTED,
            ResponseChecksumValidation = source.Checksums == S3ChecksumMode.WhenRequired ? ResponseChecksumValidation.WHEN_REQUIRED : ResponseChecksumValidation.WHEN_SUPPORTED,
        };
        if (source.ServiceUrl is { } url)
        {
            config.ServiceURL = url.ToString();
            config.AuthenticationRegion = "us-east-1";
        }
        else
        {
            config.RegionEndpoint = source.Region;
        }

        ApplyGateRows(config);
        return config;
    }

    /// <summary>
    /// The configuration rows of gate G2/G4 (spec Annexe B): every other ambient setting is already overridden
    /// explicitly in <see cref="Configure"/> (measured: identical requests under a hostile environment and profile).
    /// The per-client neutralization of CSM and region detection (G1) is <see cref="ProviderS3Client"/>; the
    /// User-Agent token AWS_EXECUTION_ENV adds, which no setting removes, is stripped by <see cref="PinningHandler"/>.
    /// </summary>
    private static void ApplyGateRows(AmazonS3Config config)
    {
        config.ClientAppId = ""; // G4: an empty string neutralizes AWS_SDK_UA_APP_ID / sdk_ua_app_id (null falls back, " " leaves "app/")
    }
}

/// <summary>
/// The provider's client (spec Annexe B, G1): its own pipeline loses the two CSM handlers (UDP telemetry, decided
/// by a process-wide singleton) and the S3 retry policy, whose region correction is the ONLY caller of
/// BucketRegionDetector's out-of-band HEAD transport; the standard policy keeps retrying 5xx and throttling.
/// Per client: other AmazonS3Client instances of the host are untouched.
/// </summary>
internal sealed class ProviderS3Client(AWSCredentials credentials, AmazonS3Config config) : AmazonS3Client(credentials, config)
{
    /// <summary>The pipeline's handler types after customization, for the structural test (Task 10).</summary>
    internal IReadOnlyList<Type> HandlerTypes { get; private set; } = [];

    /// <summary>The retry policy type in effect, for the structural test (Task 10).</summary>
    internal Type? RetryPolicyType { get; private set; }

    protected override void CustomizeRuntimePipeline(Amazon.Runtime.Internal.RuntimePipeline pipeline)
    {
        base.CustomizeRuntimePipeline(pipeline);
        TryRemove<Amazon.Runtime.Internal.CSMCallAttemptHandler>(pipeline);
        TryRemove<Amazon.Runtime.Internal.CSMCallEventHandler>(pipeline);
        var policy = new Amazon.Runtime.Internal.StandardRetryPolicy(Config);
        pipeline.ReplaceHandler<Amazon.Runtime.Internal.RetryHandler>(new Amazon.Runtime.Internal.RetryHandler(policy));
        RetryPolicyType = policy.GetType();
        HandlerTypes = pipeline.EnumerateHandlers().Select(h => h.GetType()).ToArray();
    }

    private static void TryRemove<T>(Amazon.Runtime.Internal.RuntimePipeline pipeline)
        where T : Amazon.Runtime.IPipelineHandler
    {
        if (pipeline.EnumerateHandlers().Any(h => h is T))
        {
            pipeline.RemoveHandler<T>(); // present only when CSM is enabled for the process (AWS_CSM_ENABLED, profile, AWSConfigs)
        }
    }
}
