// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using System.Text;
using Amazon.Runtime;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Providers over the scripted fake (spec T1).</summary>
internal static class S3Test
{
    public static readonly BasicAWSCredentials Creds = new("AKIDEXAMPLE", "secret");

    /// <summary>
    /// A local path-style source. Its deadline (5 minutes) is one no test meets by accident: the credentials are
    /// resolved on the thread pool (spec §4.4), which a fully loaded test run can starve for seconds. A test ABOUT the
    /// deadline sets <c>RequestTimeout</c> itself and drives it with a <see cref="ManualClock"/>.
    /// </summary>
    public static S3BundleSource Local(string prefix = "kb") =>
        new() { Bucket = "okf", Prefix = prefix, ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true, RequestTimeout = TimeSpan.FromMinutes(5) };

    public static S3BundleProvider Provider(FakeS3Handler fake, S3BundleSource? source = null, AWSCredentials? creds = null) =>
        new(creds ?? Creds, source ?? Local(), fake);

    /// <summary>The ETag the fake serves for <paramref name="bytes"/>: the quoted MD5 hex, as S3 does for a simple upload.</summary>
    public static string ETag(byte[] bytes) => "\"" + Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes)) + "\"";

    /// <summary>
    /// A fake serving <paramref name="objects"/> (S3 key → bytes) as a real store would: listings (pages of <paramref name="pageSize"/>), GETs.
    /// With <paramref name="awsStyle"/>, listed keys are encoded as Amazon S3 and MinIO do (a space as a raw <c>+</c>, a <c>+</c> as <c>%2B</c>);
    /// otherwise as RustFS does (a space as <c>%20</c>).
    /// </summary>
    public static FakeS3Handler Serving(IReadOnlyDictionary<string, byte[]> objects, int pageSize = 1000, bool awsStyle = false)
    {
        var sorted = objects.Keys.Order(Comparer<string>.Create(OKF4net.Providers.S3.Internal.S3KeyCodec.CompareByCodePoint)).ToArray();
        return new FakeS3Handler
        {
            Respond = (req, _) =>
            {
                var uri = req.RequestUri!;
                if (uri.Query.Contains("list-type=2", StringComparison.Ordinal))
                {
                    OKF4net.Providers.S3.Internal.S3KeyCodec.TryGetUniqueQueryValue(uri.Query, "prefix", out var prefix);
                    OKF4net.Providers.S3.Internal.S3KeyCodec.TryGetUniqueQueryValue(uri.Query, "continuation-token", out var token);
                    OKF4net.Providers.S3.Internal.S3KeyCodec.TryGetUniqueQueryValue(uri.Query, "max-keys", out var mk);
                    var max = Math.Min(pageSize, mk is null ? 1000 : int.Parse(mk, System.Globalization.CultureInfo.InvariantCulture));
                    var start = token is null ? 0 : int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
                    var matching = sorted.Where(k => k.StartsWith(prefix ?? "", StringComparison.Ordinal)).ToArray();
                    var page = matching.Skip(start).Take(max).ToArray();
                    var more = start + page.Length < matching.Length;
                    return Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, ListPageParserTests.Page(
                        page.Select(k => (Encode(k, awsStyle), ETag(objects[k]), (long)objects[k].Length)),
                        more, more ? (start + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null)));
                }

                OKF4net.Providers.S3.Internal.S3KeyCodec.TryDecode(uri.AbsolutePath, out var path);
                var key = path!["/okf/".Length..];
                return Task.FromResult(objects.TryGetValue(key, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : FakeS3Handler.Xml(HttpStatusCode.NotFound, FakeS3Handler.Error("NoSuchKey")));
            },
        };
    }

    public static string Encode(string key, bool awsStyle = false)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            var c = (char)b;
            if (awsStyle && c == ' ')
            {
                sb.Append('+');
            }
            else if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '/')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    public static Dictionary<string, byte[]> Objects(params (string Key, string Text)[] items) =>
        items.ToDictionary(i => i.Key, i => Encoding.UTF8.GetBytes(i.Text), StringComparer.Ordinal);
}
