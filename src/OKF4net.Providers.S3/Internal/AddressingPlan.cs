// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>The one authority and path prefix a source's requests may use (spec §4.4 table), and its key mapping (D6).</summary>
internal sealed class AddressingPlan
{
    private AddressingPlan(string scheme, string host, int port, string pathPrefix, string keyPrefix)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
        PathPrefix = pathPrefix;
        KeyPrefix = keyPrefix;
    }

    public string Scheme { get; }
    public string Host { get; }
    public int Port { get; }

    /// <summary>"/&lt;bucket&gt;/" (path-style) or "/" (virtual-hosted): what the raw request path starts with.</summary>
    public string PathPrefix { get; }

    /// <summary>The bundle's S3 key prefix with its trailing '/', or "" for the whole bucket.</summary>
    public string KeyPrefix { get; }

    public static AddressingPlan For(NormalizedSource s)
    {
        var keyPrefix = s.Prefix.Length == 0 ? "" : s.Prefix + "/";
        if (s.ServiceUrl is { } url)
        {
            return s.PathStyle
                ? new AddressingPlan(url.Scheme, url.Host, url.Port, "/" + s.Bucket + "/", keyPrefix)
                : new AddressingPlan(url.Scheme, s.Bucket + "." + url.Host, url.Port, "/", keyPrefix);
        }

        var regional = "s3." + s.Region!.SystemName + "." + s.Region.PartitionDnsSuffix;
        return s.PathStyle
            ? new AddressingPlan(Uri.UriSchemeHttps, regional, 443, "/" + s.Bucket + "/", keyPrefix)
            : new AddressingPlan(Uri.UriSchemeHttps, s.Bucket + "." + regional, 443, "/", keyPrefix);
    }

    /// <summary>The S3 key of a bundle key.</summary>
    public string S3Key(BundleKey key) => KeyPrefix + key;

    /// <summary>The listing prefix for the whole bundle (<paramref name="under"/> null) or under a bundle key prefix.</summary>
    public string ListPrefix(BundleKey? under) => under is null ? KeyPrefix : KeyPrefix + under + "/";
}
