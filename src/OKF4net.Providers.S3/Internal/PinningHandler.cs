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

        // The method TOKEN is compared ordinally: HttpMethod equality ignores case, and "get" would be sent as "get".
        if (context is null || uri is null || !string.Equals(request.Method.Method, HttpMethod.Get.Method, StringComparison.Ordinal) || !HostHeaderAdmitted(request.Headers) || !Admits(context, uri))
        {
            throw new GuardException(S3FailureKind.RefusedHost);
        }

        if (Interlocked.Increment(ref context.Sends) > MaxSendsPerOperation)
        {
            throw new GuardException(S3FailureKind.InvalidResponse);
        }

        DropAmbientHeaderContent(request);
        return base.SendAsync(request, ct);
    }

    /// <summary>The header the SDK adds inside AWS Lambda (AWS_LAMBDA_FUNCTION_NAME and _X_AMZN_TRACE_ID set), carrying the ambient trace id; not signed (measured).</summary>
    internal const string TraceIdHeader = "x-amzn-trace-id";

    // Review of 2026-11-19: a value is a NUMBER or an element of a CLOSED SET, never free text. Checking only the form let
    // an ambient text imitating a token ride in a slot the SDK had left empty (a newer SDK that renamed the token, a value
    // outside the form), where no second value contradicts it. A set too narrow for a platform or a newer SDK only removes
    // information; the request is sent all the same. The values were measured on Windows only; the other names come from
    // the runtime's own enumerations and are unmeasured. (Declared before UserAgentSlots, whose lambdas read them.)
    private static readonly HashSet<string> Architectures = new(StringComparer.Ordinal)
    {
        "X86", "X64", "Arm", "Arm64", "Armv6", "Wasm", "S390x", "LoongArch64", "Ppc64le", "RiscV64",
    };

    private static readonly HashSet<string> OsNames = new(StringComparer.Ordinal)
    {
        "windows", "linux", "macos", "osx", "freebsd", "android", "ios", "tvos", "browser", "wasi",
    };

    private static readonly HashSet<string> LanguageNames = new(StringComparer.Ordinal)
    {
        ".NET_Core", ".NET_Framework", ".NET_Native", ".NET", "Mono",
    };

    /// <summary>The SDK's business-metric codes seen so far (b, e, E, F, N, P, R); an unknown code makes the token unknown.</summary>
    private static readonly HashSet<char> MetricCodes = ['b', 'e', 'E', 'F', 'N', 'P', 'R'];

    /// <summary>
    /// The User-Agent's slots, in the order the guard writes them back: a token prefix (ordinal, case-sensitive) and the
    /// form of the value after it. Derived from what AWSSDK.S3/Core 4.1.0 send in a clean environment, measured
    /// 2026-11-19 (<c>aws-sdk-dotnet-coreclr/4.1.0.0 ua/2.1 os/windows#10.0.26200.0 md/ARCH#X64 lang/.NET_Core#10.0.15
    /// md/aws-sdk-dotnet-core#4.1.0.0 api/S3#4.1.0.0 md/ClientAsync cfg/init-coll#0 m/b,P,N,E</c>), and from the SDK's
    /// own format strings for the two variants this provider can produce: <c>ft/aot</c> under Native AOT and
    /// <c>md/ClientSync</c>. Nothing ambient has a slot: <c>exec-env/</c> (AWS_EXECUTION_ENV), <c>app/</c>
    /// (AWS_SDK_UA_APP_ID) and the raw text of AWS_INTERNAL_ENV are all dropped as unknown tokens.
    /// </summary>
    private static readonly (string Prefix, Func<string, bool> Value)[] UserAgentSlots =
    [
        ("aws-sdk-dotnet-coreclr/", IsVersion),
        ("ua/", IsVersion),
        ("os/", v => IsNameAndVersion(v, OsNames)),
        ("md/ARCH#", v => Architectures.Contains(v)),
        ("lang/", v => IsNameAndVersion(v, LanguageNames)),
        ("md/aws-sdk-dotnet-core#", IsVersion),
        ("ft/", v => v == "aot"),
        ("api/S3#", IsVersion),
        ("md/Client", v => v is "Async" or "Sync"),
        ("cfg/init-coll#", IsVersion),
        ("m/", IsMetrics),
    ];

    /// <summary>
    /// Removes what an ambient setting puts in a request's headers (spec Annexe B, G2; Annexe C12, maintainer's decision
    /// 2026-11-19). Neither header is signed (measured: SignedHeaders is host, x-amz-content-sha256, x-amz-date and, on a
    /// read, x-amz-checksum-mode), so the signature is unchanged.
    /// <list type="bullet">
    /// <item>The <c>x-amzn-trace-id</c> header is dropped, whatever its case.</item>
    /// <item>The User-Agent is REBUILT from <see cref="UserAgentSlots"/>, never filtered by a list of known bad tokens: a
    /// token is kept only when it matches a slot's prefix and form; a slot that comes twice with different values (an
    /// ambient text imitating a known token, e.g. AWS_INTERNAL_ENV="md/ARCH#x") is dropped whole, an exact repeat counts
    /// once; the kept tokens are written in the slots' order, whatever the order received. Fail-closed: anything unknown
    /// is dropped, and when nothing is left no User-Agent is sent. An allowlist too narrow for a newer SDK only removes
    /// information; the request is sent all the same.</item>
    /// </list>
    /// </summary>
    private static void DropAmbientHeaderContent(HttpRequestMessage request)
    {
        request.Headers.Remove(TraceIdHeader); // HttpHeaders names are case-insensitive
        request.Content?.Headers.Remove(TraceIdHeader);
        foreach (var name in UserAgentHeaders)
        {
            if (!request.Headers.TryGetValues(name, out var values))
            {
                continue;
            }

            var rebuilt = RebuildUserAgent(string.Join(' ', values));
            request.Headers.Remove(name);
            if (rebuilt.Length > 0)
            {
                request.Headers.TryAddWithoutValidation(name, rebuilt);
            }
        }
    }

    /// <summary>
    /// The two headers the SDK puts its agent text in: <c>User-Agent</c>, or <c>x-amz-user-agent</c> when the host sets the
    /// process-wide <c>AWSConfigs.UseAlternateUserAgentHeader</c>. Measured 2026-11-19: neither is signed.
    /// </summary>
    private static readonly string[] UserAgentHeaders = ["User-Agent", "x-amz-user-agent"];

    /// <summary>The User-Agent rebuilt from <paramref name="received"/> by <see cref="UserAgentSlots"/> (see <see cref="DropAmbientHeaderContent"/>).</summary>
    internal static string RebuildUserAgent(string received)
    {
        var kept = new string?[UserAgentSlots.Length];
        var ambiguous = new bool[UserAgentSlots.Length];
        foreach (var token in received.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            for (var i = 0; i < UserAgentSlots.Length; i++)
            {
                var (prefix, value) = UserAgentSlots[i];
                if (token.StartsWith(prefix, StringComparison.Ordinal) && value(token[prefix.Length..]))
                {
                    if (kept[i] is { } first && !string.Equals(first, token, StringComparison.Ordinal))
                    {
                        ambiguous[i] = true;
                    }

                    kept[i] = token;
                    break;
                }
            }
        }

        return string.Join(' ', kept.Where((t, i) => t is not null && !ambiguous[i]));
    }

    /// <summary>One to four dot-separated numbers of one to five digits (4.1.0.0, 10.0.26200.0, 2.1, 0).</summary>
    private static bool IsVersion(string v)
    {
        var parts = v.Split('.');
        return parts.Length is >= 1 and <= 4 && parts.All(p => p.Length is >= 1 and <= 5 && p.All(char.IsAsciiDigit));
    }

    /// <summary>A name of <paramref name="names"/> and an <see cref="IsVersion"/>, joined by one '#' (os/windows#10.0.26200.0).</summary>
    private static bool IsNameAndVersion(string v, HashSet<string> names)
    {
        var hash = v.IndexOf('#', StringComparison.Ordinal);
        return hash > 0 && names.Contains(v[..hash]) && IsVersion(v[(hash + 1)..]);
    }

    /// <summary>The business-metric codes: comma-separated single codes of <see cref="MetricCodes"/>, at most 16 of them.</summary>
    private static bool IsMetrics(string v)
    {
        var codes = v.Split(',');
        return codes.Length is >= 1 and <= 16 && codes.All(c => c.Length == 1 && MetricCodes.Contains(c[0]));
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
