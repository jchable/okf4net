// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace OKF4net.Providers.S3.Internal;

/// <summary>Strict percent-decoding to bytes, then UTF-8 without replacement (spec §4.3). Never Uri.UnescapeDataString.</summary>
internal static class S3KeyCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryDecode(string encoded, [NotNullWhen(true)] out string? decoded)
    {
        decoded = null;
        var bytes = new List<byte>(encoded.Length);
        Span<byte> one = stackalloc byte[4];
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c == '%')
            {
                if (i + 2 >= encoded.Length || !IsHex(encoded[i + 1]) || !IsHex(encoded[i + 2]))
                {
                    return false;
                }

                bytes.Add((byte)((Hex(encoded[i + 1]) << 4) | Hex(encoded[i + 2])));
                i += 2;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                if (!char.IsHighSurrogate(c) || i + 1 >= encoded.Length || !char.IsLowSurrogate(encoded[i + 1]))
                {
                    return false;
                }

                var n = StrictUtf8.GetBytes(encoded.AsSpan(i, 2), one);
                bytes.AddRange(one[..n]);
                i++;
                continue;
            }

            var m = StrictUtf8.GetBytes(encoded.AsSpan(i, 1), one);
            bytes.AddRange(one[..m]);
        }

        try
        {
            decoded = StrictUtf8.GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// The path encoding of an S3 key as the SDK sends it (gate G3 confirms it on the hostile battery): RFC 3986
    /// unreserved characters and '/' kept, every other UTF-8 byte as %XX with uppercase hex.
    /// </summary>
    public static string EncodePath(string s3Key)
    {
        var sb = new StringBuilder(s3Key.Length);
        foreach (var b in StrictUtf8.GetBytes(s3Key))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '/')
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

    /// <summary>The strictly decoded value of <paramref name="name"/>; false when absent, repeated, or not decodable.</summary>
    public static bool TryGetUniqueQueryValue(string query, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        var found = false;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (!string.Equals(eq < 0 ? pair : pair[..eq], name, StringComparison.Ordinal))
            {
                continue;
            }

            if (found || !TryDecode(eq < 0 ? "" : pair[(eq + 1)..], out value))
            {
                value = null;
                return false;
            }

            found = true;
        }

        return found;
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static int Hex(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
