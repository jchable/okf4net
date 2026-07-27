// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace OKF4net.Internal;

/// <summary>
/// Renders a string the way Rust's <c>{:?}</c> (Debug) format does for
/// <c>&amp;str</c> -- which formats each <c>char</c> via
/// <c>char::escape_debug</c>: double-quoted, with <c>\</c>, <c>"</c>, and
/// the common control characters escaped by name (<c>\n</c>, <c>\r</c>,
/// <c>\t</c>), any other Unicode <c>Control</c> (Cc) character escaped
/// numerically as <c>\u{hex}</c> (lowercase, no leading zeros) -- and, the
/// detail the three previous private copies of this helper (formerly in
/// <c>ConceptId</c>, <c>BundleValidator</c>, and the CLI's <c>OkfCli</c>)
/// all missed, any character with the Unicode <c>Grapheme_Extend</c>
/// property is ALSO escaped numerically. Rust does this so a debug-printed
/// string never contains a combining mark sitting directly after the
/// opening <c>"</c> or another escape, which a terminal or editor could
/// otherwise render fused onto the adjacent quote/backslash glyph.
///
/// The single production here replaces those three byte-identical private
/// copies and additionally closes that fidelity gap: <see cref="Quote"/>
/// escapes not just control characters but also characters in Unicode
/// General Category NonSpacingMark (Mn) or EnclosingMark (Me) -- an
/// approximation of Grapheme_Extend=Yes (which is, precisely, Mn/Me plus a
/// short explicit list of SpacingMark (Mc) exceptions and a few
/// Other_Grapheme_Extend code points documented in Unicode's
/// DerivedCoreProperties.txt). Mn/Me covers the overwhelming majority of
/// combining marks in practice (e.g. U+0301 COMBINING ACUTE ACCENT is Mn)
/// and is the same category-based approximation strategy already used
/// elsewhere in this port (see <c>RustCaseFold.IsCaseIgnorable</c>) rather
/// than a full derived-property table.
///
/// Iterates by <see cref="Rune"/> (not <c>char</c>) so a supplementary-plane
/// code point is classified and escaped as one unit (e.g. <c>\u{1f600}</c>,
/// never a broken-apart UTF-16 surrogate pair), matching Rust's <c>char</c>
/// being a full Unicode scalar value rather than a UTF-16 code unit.
/// </summary>
internal static class RustDebugQuote
{
    /// <summary>
    /// Rust's <c>format!("{s:?}")</c> for <c>&amp;str</c>, applying
    /// <c>char::escape_debug</c> per character -- see the type doc comment
    /// for exactly which characters are escaped numerically.
    /// </summary>
    internal static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var rune in s.EnumerateRunes())
        {
            AppendEscaped(sb, rune);
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static void AppendEscaped(StringBuilder sb, Rune rune)
    {
        switch (rune.Value)
        {
            case '"':
                sb.Append("\\\"");
                return;
            case '\\':
                sb.Append("\\\\");
                return;
            case '\n':
                sb.Append("\\n");
                return;
            case '\r':
                sb.Append("\\r");
                return;
            case '\t':
                sb.Append("\\t");
                return;
        }

        if (IsControl(rune) || IsGraphemeExtendApprox(rune))
        {
            sb.Append("\\u{").Append(rune.Value.ToString("x")).Append('}');
        }
        else
        {
            sb.Append(rune.ToString());
        }
    }

    private static bool IsControl(Rune rune) => Rune.GetUnicodeCategory(rune) == UnicodeCategory.Control;

    /// <summary>
    /// Approximates Unicode's <c>Grapheme_Extend</c> derived property (used
    /// by Rust's <c>char::escape_debug</c>) as General Category
    /// NonSpacingMark (Mn) or EnclosingMark (Me) -- see the type doc comment
    /// for the scope of this approximation.
    /// </summary>
    private static bool IsGraphemeExtendApprox(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
    }
}
