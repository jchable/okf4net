// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// Refuses, before anything is sent, a request outside the current operation (spec D3, §4.4):
/// <list type="bullet">
/// <item>any request made outside a provider operation;</item>
/// <item>any method but GET — every provider operation is a GET: a read is GetObject, a listing and an existence
/// check are ListObjectsV2 (no HEAD is ever sent);</item>
/// <item>another scheme, host or port than the plan's one authority, in the URI or in an explicit Host header;</item>
/// <item>a read whose RAW path (<see cref="Uri.AbsolutePath"/>, not decoded) is not exactly the plan's prefix followed
/// by the expected key's encoding (<see cref="S3KeyCodec.EncodePath"/>, the encoding the SDK sends — gate G3), or
/// which carries any query at all;</item>
/// <item>a listing whose raw path is not the plan's prefix alone, or whose query is not drawn from
/// <see cref="ListParameters"/> with each parameter at most once, <c>list-type=2</c> and <c>encoding-type=url</c>
/// present, and a <c>prefix</c> (absent counting as empty) equal to the expected one once strictly decoded.</item>
/// </list>
/// A canonicalized path ('..' removed), another encoding, an encoded parameter name, an undecodable value or a raw <c>+</c> in a
/// query value (a store reads it as a space, the guard as a <c>+</c>) fails closed.
/// </summary>
internal sealed class PinningHandler(AddressingPlan plan) : DelegatingHandler
{
    /// <summary>At most this many sends per operation, retries and redirects included (spec Annexe B, consequence 4: a 307 answered forever made the SDK loop without bound).</summary>
    public const int MaxSendsPerOperation = 3;

    /// <summary>
    /// The query parameters a listing page may carry: exactly what plan Task 7's <c>ListObjectsV2Request</c> (BucketName,
    /// Prefix, MaxKeys, ContinuationToken, Encoding) makes the SDK send, measured by the guard tests on the real client.
    /// </summary>
    internal static readonly IReadOnlySet<string> ListParameters = new HashSet<string>(StringComparer.Ordinal)
    {
        "list-type", "prefix", "encoding-type", "continuation-token", "max-keys",
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var context = OperationContext.Current.Value;
        var uri = request.RequestUri;
        if (context is null || uri is null || request.Method != HttpMethod.Get || !HostHeaderAdmitted(request.Headers) || !Admits(context, uri))
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

    /// <summary>
    /// An explicit Host header (none: the URI's authority is sent) must be one value naming the plan's host and port;
    /// the port may be omitted only when it is the scheme's default. The raw values are read (TryGetValues), never the
    /// parsed <see cref="System.Net.Http.Headers.HttpRequestHeaders.Host"/>, which is null for a value added without
    /// validation that would still be sent.
    /// </summary>
    private bool HostHeaderAdmitted(System.Net.Http.Headers.HttpRequestHeaders headers)
    {
        if (!headers.TryGetValues("Host", out var values))
        {
            return true;
        }

        var all = values.ToArray();
        if (all.Length != 1)
        {
            return false;
        }

        var host = all[0];
        string name;
        int port;
        var close = host.StartsWith('[') ? host.IndexOf(']', StringComparison.Ordinal) : -1;
        var colon = host.IndexOf(':', close < 0 ? 0 : close);
        if (colon < 0)
        {
            name = host;
            port = plan.Scheme == Uri.UriSchemeHttps ? 443 : 80;
        }
        else
        {
            name = host[..colon];
            var digits = host[(colon + 1)..];
            if (digits.Length is 0 or > 5 || !digits.All(char.IsAsciiDigit))
            {
                return false;
            }

            port = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return string.Equals(name, plan.Host, StringComparison.OrdinalIgnoreCase) && port == plan.Port;
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
                && uri.Query.Length == 0 && !uri.PathAndQuery.Contains('?', StringComparison.Ordinal)
                && string.Equals(uri.AbsolutePath, plan.PathPrefix + S3KeyCodec.EncodePath(key), StringComparison.Ordinal),
            _ => string.Equals(uri.AbsolutePath, plan.PathPrefix, StringComparison.Ordinal)
                && context.ExpectedListPrefix is { } prefix
                && ListQueryAdmitted(uri.Query, prefix),
        };
    }

    private static bool ListQueryAdmitted(string query, string expectedPrefix)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var name = eq < 0 ? pair : pair[..eq];
            var raw = eq < 0 ? "" : pair[(eq + 1)..];
            // A raw '+' is refused in every value: a store reads it as a space, a strict decoding as a '+' — the guard and
            // the store would disagree on what is asked. The SDK sends a '+' as %2B and a space as %20.
            if (!ListParameters.Contains(name) || !seen.TryAdd(name, raw) || raw.Contains('+', StringComparison.Ordinal) || !S3KeyCodec.TryDecode(raw, out _))
            {
                return false;
            }
        }

        return seen.GetValueOrDefault("list-type") == "2"
            && seen.GetValueOrDefault("encoding-type") == "url"
            && S3KeyCodec.TryDecode(seen.GetValueOrDefault("prefix") ?? "", out var actual)
            && string.Equals(actual, expectedPrefix, StringComparison.Ordinal);
    }
}
