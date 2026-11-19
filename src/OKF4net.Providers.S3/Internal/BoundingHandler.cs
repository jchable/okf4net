// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// Bounds what reaches the SDK's deserializers (spec §4.6). An error response (status &gt;= 300) or, in a
/// <see cref="S3Operation.List"/> or <see cref="S3Operation.Exists"/> operation (the current
/// <see cref="OperationContext"/> decides, never the URL), a successful listing page is read from the network to
/// B + 1 bytes at most (one byte to tell the end from an excess, with or without Content-Length); within B it is
/// handed to the SDK on a NEW in-memory content — the SDK never sees the original stream, so it cannot start
/// deserializing before the check — and a listing page's bytes are also left in the operation's
/// <see cref="ListPageCapture"/> for the provider's strict parse. Past B nothing reaches the SDK. A successful
/// object body passes through untouched (the provider bounds it by MaxObjectBytes). The buffer grows with what
/// actually arrives, so a small page never costs the whole bound.
/// </summary>
internal sealed class BoundingHandler : DelegatingHandler
{
    public const int ListBodyBytes = 8 * 1024 * 1024;
    public const int ErrorBodyBytes = 64 * 1024;
    public const int TokenBytes = 1024;

    private const int InitialBuffer = 16 * 1024;

    /// <summary>The most a body buffer is allocated up front, whatever length the store declares: the buffer grows with what actually arrives.</summary>
    private const int FirstBufferCap = 256 * 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        var operation = OperationContext.Current.Value;
        var isList = operation?.Operation is S3Operation.List or S3Operation.Exists;
        if (isList)
        {
            operation!.Capture?.Body = null; // the capture is the LAST response's page: an earlier attempt of the same SDK call (a retry) must not outlive a later one that failed (#290)
        }

        var status = (int)response.StatusCode;
        int? bound = status >= 300 ? ErrorBodyBytes : isList ? ListBodyBytes : null;
        if (bound is null)
        {
            if (operation?.Operation == S3Operation.Read)
            {
                DropEtagTheSdkCannotCheck(response);
            }

            return response;
        }

        // A HEAD answer, a 204 and a 304 carry no body: their Content-Length describes a representation not sent.
        var bodiless = request.Method == HttpMethod.Head || status is 204 or 304;
        List<KeyValuePair<string, IEnumerable<string>>> headers;
        List<KeyValuePair<string, IEnumerable<string>>> contentHeaders;
        byte[]? body;
        var statusCode = response.StatusCode;
        var version = response.Version;
        var reasonPhrase = response.ReasonPhrase;
        var requestMessage = response.RequestMessage;
        try
        {
            // The metadata is snapshotted while the original is intact; the original never reaches the SDK. The copy is
            // built only AFTER the read, from the snapshot: a read that fails (or overruns) leaves no half-built copy
            // behind (minors A5), only the original, released below whatever the outcome.
            headers = response.Headers.Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.ToArray())).ToList();
            contentHeaders = response.Content.Headers
                .Where(h => !string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(h => new KeyValuePair<string, IEnumerable<string>>(h.Key, h.Value.ToArray()))
                .ToList();
            body = await ReadBoundedAsync(response.Content, bound.Value, bodiless, ct).ConfigureAwait(false);
        }
        finally
        {
            DisposeQuietly(response); // its disposal cannot mask the outcome
        }

        if (body is null)
        {
            throw new GuardException(S3FailureKind.InvalidResponse);
        }

        if (isList && status < 300 && operation?.Capture is { } capture)
        {
            capture.Body = body;
        }

        var copy = new HttpResponseMessage(statusCode)
        {
            Version = version,
            ReasonPhrase = reasonPhrase,
            RequestMessage = requestMessage,
        };
        foreach (var h in headers)
        {
            copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        copy.Content = new ByteArrayContent(body);
        foreach (var h in contentHeaders)
        {
            copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        copy.Content.Headers.ContentLength = body.Length;
        return copy;
    }

    /// <summary>
    /// The SDK checks the body of a successful GetObject against its ETag, as an MD5 digest, whenever the ETag is a single
    /// part (no '-'): it hex-decodes the ETag and compares. An ETag that is not 32 hexadecimal digits is not an MD5, so the
    /// check can only fail, wrongly: a non-hex value throws a FormatException, a hex digest of another length (SHA-1, SHA-256)
    /// fails as an integrity error (measured on AWSSDK.S3 4.1.0, #286). The header is dropped from such a response, so the SDK
    /// has nothing to check; the provider's own integrity is the version, the SHA-256 of the bytes it read, whatever the ETag
    /// says. A genuine MD5 ETag (32 hex digits) and a multipart-shaped one (<c>&lt;hex&gt;-&lt;parts&gt;</c>, which the SDK skips)
    /// are left alone, so a body that does not match a real MD5 is still refused as <see cref="S3FailureKind.Integrity"/>.
    /// </summary>
    private static void DropEtagTheSdkCannotCheck(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("ETag", out var values))
        {
            return;
        }

        var etag = string.Concat(values).Trim().Trim('"');
        if (etag.Contains('-', StringComparison.Ordinal) || (etag.Length == 32 && etag.All(char.IsAsciiHexDigit)))
        {
            return;
        }

        response.Headers.Remove("ETag");
    }

    /// <returns>The body, or null when it is longer than <paramref name="bound"/>. Never reads more than B + 1 bytes.</returns>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int bound, bool bodiless, CancellationToken ct)
    {
        var declared = bodiless ? null : content.Headers.ContentLength;
        if (declared > bound)
        {
            return null;
        }

        var limit = bound + 1;
        var buffer = new byte[(int)Math.Min(FirstBufferCap, Math.Min(limit, declared is { } d ? d + 1 : InitialBuffer))]; // a declared length is the store's claim: it never pre-allocates more than FirstBufferCap (#290)
        var total = 0;
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            while (true)
            {
                if (total == buffer.Length)
                {
                    if (buffer.Length == limit)
                    {
                        return null; // B + 1 bytes: an excess
                    }

                    Array.Resize(ref buffer, (int)Math.Min(limit, 2L * buffer.Length));
                }

                var n = await stream.ReadAsync(buffer.AsMemory(total, Math.Min(81920, buffer.Length - total)), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    return total == buffer.Length ? buffer : buffer.AsSpan(0, total).ToArray();
                }

                total += n;
            }
        }
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
