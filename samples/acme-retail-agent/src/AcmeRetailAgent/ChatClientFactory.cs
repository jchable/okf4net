// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using OpenAI;

namespace OKF4net.Samples.AcmeRetailAgent;

/// <summary>
/// Resolves an <see cref="IChatClient"/> from environment variables naming an
/// OpenAI-compatible endpoint (OpenAI, Ollama, Azure OpenAI, or any
/// Claude/Copilot-fronting OpenAI-compatible gateway) -- one code path for
/// every provider, no per-provider branching.
/// </summary>
public static class ChatClientFactory
{
    /// <summary>Environment variable naming the OpenAI-compatible base URL (required).</summary>
    public const string BaseUrlEnv = "OKF_CHAT_BASE_URL";

    /// <summary>Environment variable naming the bearer API key (optional -- e.g. not required for local Ollama).</summary>
    public const string ApiKeyEnv = "OKF_CHAT_API_KEY";

    /// <summary>Environment variable naming the model id understood by the endpoint (required).</summary>
    public const string ModelEnv = "OKF_CHAT_MODEL";

    /// <summary>
    /// Resolves an <see cref="IChatClient"/> from <paramref name="getEnv"/>.
    /// Returns <see langword="false"/> with a human-readable
    /// <paramref name="error"/> when <see cref="BaseUrlEnv"/> is missing or
    /// not a valid absolute URI, or <see cref="ModelEnv"/> is missing. Makes
    /// no network calls -- the returned client only talks to the endpoint on
    /// its first real chat request.
    /// </summary>
    /// <param name="getEnv">Environment-variable accessor (e.g. <see cref="Environment.GetEnvironmentVariable(string)"/>).</param>
    /// <param name="client">The resolved chat client, or <see langword="null"/> on failure.</param>
    /// <param name="error">The failure reason, or <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryCreate(
        Func<string, string?> getEnv,
        [NotNullWhen(true)] out IChatClient? client,
        [NotNullWhen(false)] out string? error)
    {
        client = null;

        var baseUrl = getEnv(BaseUrlEnv)?.Trim();
        if (string.IsNullOrEmpty(baseUrl))
        {
            error = $"{BaseUrlEnv} is not set";
            return false;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint))
        {
            error = $"{BaseUrlEnv} is not a valid absolute URI: '{baseUrl}'";
            return false;
        }

        var model = getEnv(ModelEnv)?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            error = $"{ModelEnv} is not set";
            return false;
        }

        // Ollama and other local gateways ignore the API key, but the OpenAI
        // SDK's ApiKeyCredential rejects an empty string -- fall back to a
        // placeholder when none is configured.
        var apiKey = getEnv(ApiKeyEnv)?.Trim();
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey);
        var options = new OpenAIClientOptions { Endpoint = endpoint };

        client = new OpenAIClient(credential, options).GetChatClient(model).AsIChatClient();
        error = null;
        return true;
    }

    /// <summary>
    /// Formats a single-line startup usage/error for stderr, mirroring
    /// <c>OkfMcpConfig.FormatStartupError</c>'s convention in
    /// <c>OKF4net.Mcp</c>.
    /// </summary>
    /// <param name="error">The failure reason from <see cref="TryCreate"/> (may be <see langword="null"/>).</param>
    public static string FormatStartupError(string? error)
    {
        var message = string.IsNullOrWhiteSpace(error) ? "startup configuration error" : error.Trim();
        return $"acme-retail-agent: {message}. Set {BaseUrlEnv} and {ModelEnv} "
            + $"(and optionally {ApiKeyEnv}) to an OpenAI-compatible endpoint.";
    }
}
