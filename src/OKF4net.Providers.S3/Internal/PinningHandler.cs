// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// Refuses, before anything is sent, a request outside the current operation (spec D3, §4.4): another scheme,
/// host or port than the plan's one authority; a read whose RAW path (<see cref="Uri.AbsolutePath"/>, not decoded)
/// is not exactly the plan's prefix followed by the expected key's encoding (<see cref="S3KeyCodec.EncodePath"/>,
/// the encoding the SDK sends — gate G3); a listing whose raw path is not the plan's prefix alone, or whose query
/// has a repeated parameter or a <c>prefix</c> other than the expected one; any request made outside a provider
/// operation. A canonicalized path ('..' removed) or another encoding fails closed.
/// </summary>
internal sealed class PinningHandler(AddressingPlan plan) : DelegatingHandler
{
    /// <summary>At most this many sends per operation, retries and redirects included (spec Annexe B, consequence 4: a 307 answered forever made the SDK loop without bound).</summary>
    public const int MaxSendsPerOperation = 3;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var context = OperationContext.Current.Value;
        var uri = request.RequestUri;
        if (context is null || uri is null || !Admits(context, uri))
        {
            throw new GuardException(S3FailureKind.RefusedHost);
        }

        if (Interlocked.Increment(ref context.Sends) > MaxSendsPerOperation)
        {
            throw new GuardException(S3FailureKind.InvalidResponse);
        }

        StripAmbientUserAgentTokens(request);
        return base.SendAsync(request, ct);
    }

    /// <summary>
    /// Removes the exec-env/… token AWS_EXECUTION_ENV adds and any app/… token (spec Annexe B, G2): no client
    /// setting removes the first, and neither is signed (the User-Agent is not in SignedHeaders), so the signature
    /// is unchanged and nothing ambient reaches the store.
    /// </summary>
    private static void StripAmbientUserAgentTokens(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("User-Agent", out var values))
        {
            return;
        }

        var kept = string.Join(' ', string.Join(' ', values).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.StartsWith("exec-env/", StringComparison.Ordinal) && !t.StartsWith("app/", StringComparison.Ordinal)));
        request.Headers.Remove("User-Agent");
        request.Headers.TryAddWithoutValidation("User-Agent", kept);
    }

    private bool Admits(OperationContext context, Uri uri)
    {
        if (!string.Equals(uri.Scheme, plan.Scheme, StringComparison.Ordinal)
            || !string.Equals(uri.Host, plan.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != plan.Port)
        {
            return false;
        }

        return context.Operation switch
        {
            S3Operation.Read => context.ExpectedObjectS3Key is { } key
                && string.Equals(uri.AbsolutePath, plan.PathPrefix + S3KeyCodec.EncodePath(key), StringComparison.Ordinal),
            _ => string.Equals(uri.AbsolutePath, plan.PathPrefix, StringComparison.Ordinal)
                && context.ExpectedListPrefix is { } prefix
                && S3KeyCodec.TryGetUniqueQueryValue(uri.Query, "prefix", out var actual) && string.Equals(actual, prefix, StringComparison.Ordinal),
        };
    }
}
