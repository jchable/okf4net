// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace OKF4net.Internal;

/// <summary>
/// Renders a string as a debug-style quoted literal: double-quoted, with
/// <c>\</c>, <c>"</c>, and the common control characters escaped by name
/// (<c>\n</c>, <c>\r</c>, <c>\t</c>), any other Unicode <c>Control</c> (Cc)
/// character escaped numerically as <c>\u{hex}</c> (lowercase, no leading
/// zeros), and, importantly, any character with the Unicode
/// <c>Grapheme_Extend</c> property ALSO escaped numerically. Escaping
/// combining marks matters so a quoted string never contains a combining
/// mark sitting directly after the opening <c>"</c> or another escape, which
/// a terminal or editor could otherwise render fused onto the adjacent
/// quote/backslash glyph.
///
/// <see cref="Quote"/> escapes not just control characters but also
/// characters in Unicode General Category NonSpacingMark (Mn) or
/// EnclosingMark (Me) -- an approximation of Grapheme_Extend=Yes (which is,
/// precisely, Mn/Me plus a short explicit list of SpacingMark (Mc) exceptions
/// and a few Other_Grapheme_Extend code points documented in Unicode's
/// DerivedCoreProperties.txt). Mn/Me covers the overwhelming majority of
/// combining marks in practice (e.g. U+0301 COMBINING ACUTE ACCENT is Mn) and
/// is the same category-based approximation strategy used elsewhere here (see
/// <c>UnicodeCaseFold.IsCaseIgnorable</c>) rather than a full derived-property
/// table.
///
/// It ALSO escapes U+2028 LINE SEPARATOR and U+2029 PARAGRAPH SEPARATOR
/// (General Categories Zl and Zp, whose only members these two are). They are
/// not <c>Cc</c>, so the control-character arm never saw them, and every
/// message built here is a LINE in someone's output: an <c>okf verify</c>
/// refusal, a validator diagnostic, a tool result an agent reads. A markdown
/// or JavaScript line splitter downstream treats both as terminators, so a
/// concept id of <c>a</c> + U+2028 + <c>recorded x  human:ceo  …</c> quoted
/// itself into a second, forged <c>recorded</c> line in a refusal that wrote
/// nothing (executed). Quoting a value for a human to READ and leaving in the
/// two characters that end the line it is read on are not compatible: this is
/// the same rule <c>OkfBundleTools.OneLine</c> states, applied at the helper
/// every such message already goes through, rather than folded one sink at a
/// time. <c>LineSafeText.ContainsControlCharacter</c> has always counted these
/// two as line-breaking; this is what closes the disagreement between the two
/// helpers.
///
/// Iterates by <see cref="Rune"/> (not <c>char</c>) so a supplementary-plane
/// code point is classified and escaped as one unit (e.g. <c>\u{1f600}</c>,
/// never a broken-apart UTF-16 surrogate pair): each unit is a full Unicode
/// scalar value rather than a UTF-16 code unit.
/// </summary>
internal static class DebugQuote
{
    /// <summary>
    /// Returns <paramref name="s"/> as a debug-style quoted literal -- see the
    /// type doc comment for exactly which characters are escaped numerically.
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

        if (IsControl(rune) || IsLineBreaking(rune) || IsGraphemeExtendApprox(rune))
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
    /// U+2028 LINE SEPARATOR and U+2029 PARAGRAPH SEPARATOR, the only members
    /// of General Categories Zl and Zp -- named by category rather than by
    /// their two code points so no literal, invisible separator has to appear
    /// in this source file to express the rule. They are NOT
    /// <see cref="UnicodeCategory.Control"/>, which is exactly why they used to
    /// pass through a helper whose whole job is to make a value safe to read on
    /// one line; see the type doc comment.
    ///
    /// <para>Deliberately NOT widened to every <c>Zs</c> space separator: an
    /// ordinary space, or a non-breaking one, does not end a line, and escaping
    /// it would disfigure every quoted multi-word value for nothing.</para>
    /// </summary>
    private static bool IsLineBreaking(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>
    /// Approximates Unicode's <c>Grapheme_Extend</c> derived property as
    /// General Category NonSpacingMark (Mn) or EnclosingMark (Me) -- see the
    /// type doc comment for the scope of this approximation.
    /// </summary>
    private static bool IsGraphemeExtendApprox(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
    }
}
