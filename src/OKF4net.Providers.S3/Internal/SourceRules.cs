// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Amazon;

namespace OKF4net.Providers.S3.Internal;

/// <summary>The validated, normalized form of an <see cref="S3BundleSource"/>, with its identity (spec D5, D6, §4.3).</summary>
internal sealed record NormalizedSource(
    string Bucket, string Prefix, string? DisplayName, RegionEndpoint? Region, Uri? ServiceUrl, bool PathStyle,
    TimeSpan RequestTimeout, int MaxConcurrentRequests, long MaxObjectBytes, S3ChecksumMode Checksums,
    string Id, string Location, string EndpointIdentity);

internal static partial class SourceRules
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // \A and \z, never ^ and $: '$' also matches before a final LF ("demo\n" would pass).
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]\z", RegexOptions.CultureInvariant)]
    private static partial Regex BucketShape();

    [GeneratedRegex(@"\A\d{1,3}(\.\d{1,3}){3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IpShape();

    private static readonly string[] RefusedSuffixes = ["--x-s3", "--ol-s3", "-s3alias", "--table-s3"];
    private static readonly string[] RefusedPrefixes = ["xn--", "sthree-", "amzn-s3-demo-"];

    public static NormalizedSource Normalize(S3BundleSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bucket = source.Bucket ?? throw new ArgumentException("A bucket is required.", nameof(source));
        if (!BucketShape().IsMatch(bucket) || bucket.Contains("..", StringComparison.Ordinal) || IpShape().IsMatch(bucket)
            || RefusedSuffixes.Any(s => bucket.EndsWith(s, StringComparison.Ordinal)) || RefusedPrefixes.Any(p => bucket.StartsWith(p, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The bucket is not a general-purpose S3 bucket name.", nameof(source));
        }

        var prefix = source.Prefix ?? "";
        if (prefix.Length > 0 && (!BundleKey.TryParse(prefix, out _, out _) || !IsWellFormed(prefix)))
        {
            throw new ArgumentException("The prefix must be empty or valid bundle key segments in well-formed Unicode.", nameof(source));
        }

        if (source.DisplayName is { } display && (display.Length == 0 || display.Contains('/', StringComparison.Ordinal)
            || display.Contains(':', StringComparison.Ordinal) || !BundleKey.TryParse(display, out _, out _) || !IsWellFormed(display)))
        {
            throw new ArgumentException("The display name must be one bundle key segment, without ':'.", nameof(source));
        }

        if ((source.Region is null) == (source.ServiceUrl is null))
        {
            throw new ArgumentException("Set exactly one of Region and ServiceUrl.", nameof(source));
        }

        if (source.RequestTimeout <= TimeSpan.Zero || source.MaxConcurrentRequests <= 0
            || source.MaxObjectBytes <= 0 || source.MaxObjectBytes > Array.MaxLength)
        {
            throw new ArgumentException("Timeouts and bounds must be positive (MaxObjectBytes at most Array.MaxLength).", nameof(source));
        }

        // Gate G3 (spec Annexe B, consequence 3): the SDK switches to path-style by itself for a bucket with '.'
        // (any endpoint) and for an IP-literal ServiceUrl; such configurations need ForcePathStyle, so the SDK
        // never changes the addressing of an admitted configuration.
        if (!source.ForcePathStyle && bucket.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException("A bucket name with '.' needs ForcePathStyle.", nameof(source));
        }

        string endpoint;
        Uri? serviceUrl = null;
        if (source.ServiceUrl is { } url)
        {
            if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
                || url.UserInfo.Length > 0 || url.AbsolutePath != "/" || url.Query.Length > 0 || url.Fragment.Length > 0)
            {
                throw new ArgumentException("ServiceUrl must be an http(s) origin only: no user info, path, query or fragment.", nameof(source));
            }

            if (url.Scheme == Uri.UriSchemeHttp && !source.AllowInsecureHttp)
            {
                throw new ArgumentException("An http:// ServiceUrl needs AllowInsecureHttp.", nameof(source));
            }

            if (!source.ForcePathStyle && url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            {
                throw new ArgumentException("An IP-address ServiceUrl needs ForcePathStyle.", nameof(source));
            }

            serviceUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"{url.Scheme}://{url.Host.ToLowerInvariant()}:{url.Port}/"));
            endpoint = string.Create(CultureInfo.InvariantCulture, $"{url.Scheme}://{url.Host.ToLowerInvariant()}:{url.Port}");
        }
        else
        {
            var region = source.Region!;
            if (region.PartitionDnsSuffix != "amazonaws.com")
            {
                throw new ArgumentException("Only regions of the amazonaws.com partition are supported.", nameof(source));
            }

            endpoint = "aws:" + region.SystemName;
        }

        endpoint += source.ForcePathStyle ? "|path" : "|vhost";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("s3v1\n" + endpoint + "\n" + bucket + "\n" + prefix)));
        var id = "s3:" + hash[..32];
        var location = "s3:" + (source.DisplayName ?? hash[..8]) + (prefix.Length == 0 ? "" : "/" + prefix);
        return new NormalizedSource(bucket, prefix, source.DisplayName, source.Region, serviceUrl, source.ForcePathStyle,
            source.RequestTimeout, source.MaxConcurrentRequests, source.MaxObjectBytes, source.Checksums, id, location, endpoint);
    }

    internal static bool IsWellFormed(string text)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
