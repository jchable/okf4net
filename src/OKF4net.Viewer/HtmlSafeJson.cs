// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace OKF4net.Viewer;

/// <summary>
/// Hand-rolled JSON string escaping, safe to embed inside an HTML
/// <c>&lt;script&gt;</c> element. Hand-rolled rather than
/// <c>System.Text.Json</c> not for AOT reasons -- <c>System.Text.Json</c>
/// with a source-generated context is AOT-safe, and
/// <c>src/OKF4net.Cli/JsonOutput.cs</c> uses exactly that in this same
/// solution -- but because the extra escaping this type does beyond plain
/// JSON (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, U+2028, U+2029) is a
/// security requirement, not a formatting choice, and hand-rolling keeps
/// that rule in one small, auditable place rather than layered on top of a
/// general-purpose serializer.
/// </summary>
/// <remarks>
/// Beyond the JSON minimum this escapes <c>&lt;</c>, <c>&gt;</c> and
/// <c>&amp;</c> as <c>\uXXXX</c>. That is what makes a <c>&lt;/script&gt;</c>
/// sequence in untrusted bundle content unable to terminate the container
/// element early. U+2028/U+2029 are escaped too: they are valid JSON but
/// terminate a JavaScript string literal. Lone (unpaired) UTF-16 surrogates
/// are escaped as well, so the result is always well-formed UTF-16 and thus
/// representable by any encoder a later stage applies; a valid surrogate
/// pair (e.g. an emoji) is left untouched.
/// </remarks>
internal static class HtmlSafeJson
{
    /// <summary>
    /// <paramref name="value"/> as a complete JSON string literal, including
    /// its surrounding double quotes.
    /// </summary>
    /// <param name="value">The string to quote and escape.</param>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                // Escaped so untrusted content cannot close the surrounding
                // <script> element or inject markup into the page.
                case '<':
                case '>':
                case '&':
                case '\u2028':
                case '\u2029':
                    AppendUnicodeEscape(sb, c);
                    break;
                default:
                    if (c < ' ')
                    {
                        AppendUnicodeEscape(sb, c);
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        // A high surrogate followed by a low surrogate is a
                        // valid pair (e.g. an emoji) and must survive
                        // verbatim; anything else is a lone surrogate, which
                        // is not valid UTF-16 on its own and must be escaped
                        // so the returned string stays representable in any
                        // encoding a later stage might apply.
                        if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                        {
                            sb.Append(c).Append(value[i + 1]);
                            i++;
                        }
                        else
                        {
                            AppendUnicodeEscape(sb, c);
                        }
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        // A low surrogate not preceded by a high surrogate
                        // (the high-surrogate branch above already consumed
                        // valid pairs) is lone and must be escaped.
                        AppendUnicodeEscape(sb, c);
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static void AppendUnicodeEscape(StringBuilder sb, char c)
        => sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
}
