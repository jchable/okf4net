// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace OKF4net.Viewer;

/// <summary>
/// Hand-rolled JSON string escaping, safe to embed inside an HTML
/// <c>&lt;script&gt;</c> element. Hand-rolled rather than
/// <c>System.Text.Json</c> because the CLI consuming this is published
/// Native AOT and must stay free of reflection-based serialization.
/// </summary>
/// <remarks>
/// Beyond the JSON minimum this escapes <c>&lt;</c>, <c>&gt;</c> and
/// <c>&amp;</c> as <c>\uXXXX</c>. That is what makes a <c>&lt;/script&gt;</c>
/// sequence in untrusted bundle content unable to terminate the container
/// element early. U+2028/U+2029 are escaped too: they are valid JSON but
/// terminate a JavaScript string literal.
/// </remarks>
public static class HtmlSafeJson
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

        foreach (var c in value)
        {
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
