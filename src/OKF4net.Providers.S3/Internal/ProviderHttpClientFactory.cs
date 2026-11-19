// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;

namespace OKF4net.Providers.S3.Internal;

/// <summary>Always the provider's own HttpClient: never cached across clients, never disposed by the SDK (spec §4.4, gate G5).</summary>
internal sealed class ProviderHttpClientFactory(HttpClient client) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => client;

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

    public override string? GetConfigUniqueString(IClientConfig clientConfig) => null;
}
