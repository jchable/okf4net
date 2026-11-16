// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net.Http.Headers;

namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// Bounds what reaches the SDK's deserializers (spec §4.6). An error response (status &gt;= 300) or a listing page is
/// read from the network to B + 1 bytes at most (one byte to tell the end from an excess, with or without
/// Content-Length); within B it is handed to the SDK on a NEW in-memory content — the SDK never sees the original
/// stream, so it cannot start deserializing before the check — and a listing page's bytes are also left in the
/// operation's <see cref="ListPageCapture"/> for the provider's strict parse. Past B nothing reaches the SDK. A
/// successful object body passes through untouched (the provider bounds it by MaxObjectBytes).
/// </summary>
internal sealed class BoundingHandler : DelegatingHandler
{
    public const int ListBodyBytes = 8 * 1024 * 1024;
    public const int ErrorBodyBytes = 64 * 1024;
    public const int TokenBytes = 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        var isList = request.RequestUri?.Query.Contains("list-type=2", StringComparison.Ordinal) == true;
        var status = (int)response.StatusCode;
        int? bound = status >= 300 ? ErrorBodyBytes : isList ? ListBodyBytes : null;
        if (bound is null)
        {
            return response;
        }

        byte[]? body;
        try
        {
            body = await ReadBoundedAsync(response.Content, bound.Value, ct).ConfigureAwait(false);
        }
        finally
        {
            DisposeQuietly(response); // the original never reaches the SDK; its disposal cannot mask the outcome
        }

        if (body is null)
        {
            throw new GuardException(S3FailureKind.InvalidResponse);
        }

        if (isList && status < 300 && OperationContext.Current.Value?.Capture is { } capture)
        {
            capture.Body = body;
        }

        var copy = new HttpResponseMessage(response.StatusCode)
        {
            Version = response.Version,
            ReasonPhrase = response.ReasonPhrase,
            RequestMessage = response.RequestMessage,
            Content = new ByteArrayContent(body),
        };
        foreach (var h in response.Headers)
        {
            copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        foreach (var h in response.Content.Headers)
        {
            if (!string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
        }

        copy.Content.Headers.ContentLength = body.Length;
        return copy;
    }

    /// <returns>The body, or null when it is longer than <paramref name="bound"/>. Never reads more than B + 1 bytes.</returns>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int bound, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } declared && declared > bound)
        {
            return null;
        }

        var buffer = new byte[bound + 1];
        var total = 0;
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            while (total < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(total, Math.Min(81920, buffer.Length - total)), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    return buffer.AsSpan(0, total).ToArray();
                }

                total += n;
            }
        }

        return null; // B + 1 bytes: an excess
    }

    private static void DisposeQuietly(IDisposable d)
    {
        try
        {
            d.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Ignored on purpose: the response is discarded either way, and the outcome is already decided.
        }
    }
}
