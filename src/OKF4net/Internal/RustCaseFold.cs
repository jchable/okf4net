// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace OKF4net.Internal;

/// <summary>
/// A close port of two pieces of Rust's <c>&amp;str</c> surface that
/// <c>StringComparer.Ordinal</c> / <c>string.ToLowerInvariant()</c> do NOT
/// reproduce, needed so title sorting (<see cref="OKF4net.IndexGenerator"/>)
/// matches the Rust reference bit-for-bit on non-ASCII input:
///
/// <list type="bullet">
/// <item><description><see cref="ToLowercase"/> mirrors Rust's
/// <c>str::to_lowercase</c> (full Unicode default case folding, including
/// the one unconditional multi-character mapping and the language-
/// independent Final_Sigma rule) rather than .NET's
/// <c>string.ToLowerInvariant</c>, which differs on both points.</description></item>
/// <item><description><see cref="CompareCodePoints"/> mirrors Rust's
/// <c>String::cmp</c> (byte-wise UTF-8 comparison, equivalently Unicode
/// code-point order) rather than <c>StringComparer.Ordinal</c>, which
/// compares UTF-16 *code units* and so disagrees with code-point order
/// across the surrogate range.</description></item>
/// </list>
/// </summary>
internal static class RustCaseFold
{
    /// <summary>LATIN CAPITAL LETTER I WITH DOT ABOVE.</summary>
    private const int CapitalIWithDotAbove = 0x0130;

    /// <summary>GREEK CAPITAL LETTER SIGMA.</summary>
    private const int GreekCapitalSigma = 0x03A3;

    /// <summary>GREEK SMALL LETTER FINAL SIGMA (ς).</summary>
    private const string GreekFinalSigma = "ς";

    /// <summary>
    /// Lowercases <paramref name="s"/> the way Rust's <c>str::to_lowercase</c>
    /// does: Unicode default case folding (<c>Rune.ToLowerInvariant</c> per
    /// code point) plus the two documented departures from a simple
    /// per-character mapping that Rust's algorithm (and .NET's
    /// <c>string.ToLowerInvariant</c>) diverge on:
    ///
    /// <list type="bullet">
    /// <item><description>U+0130 (İ) LATIN CAPITAL LETTER I WITH DOT ABOVE
    /// is SpecialCasing.txt's only *unconditional* multi-character lowercase
    /// mapping: it becomes "i" + U+0307 COMBINING DOT ABOVE, two chars. .NET's
    /// <c>ToLowerInvariant</c> leaves U+0130 unchanged.</description></item>
    /// <item><description>The Final_Sigma rule: U+03A3 (Σ) becomes U+03C2
    /// (ς) instead of the default U+03C3 (σ) when it is in "final position"
    /// — preceded by a cased character (skipping any case-ignorable
    /// characters in between) and not followed by one (again skipping
    /// case-ignorable characters). This is a language-*independent* rule
    /// (unlike Turkish/Lithuanian-style locale casing, which Rust's
    /// <c>to_lowercase</c> also does not apply).</description></item>
    /// </list>
    ///
    /// <see cref="IsCased"/>/<see cref="IsCaseIgnorable"/> approximate the
    /// Unicode Cased / Case_Ignorable properties (Unicode §3.13, definition
    /// D136) via UnicodeCategory groupings rather than the full derived
    /// property tables — sufficient to place Final_Sigma correctly for
    /// title sorting, though it may not match the full Unicode algorithm on
    /// every exotic combination of combining marks.
    ///
    /// Caveat shared with the rest of this port: this delegates single
    /// code-point mapping to .NET's (ICU-backed) Unicode tables, which may
    /// be a different Unicode version than the Rust reference's
    /// <c>to_lowercase</c> (backed by Rust's own generated tables). For the
    /// overwhelming majority of code points — including everything above
    /// plus every ASCII and Latin-1 character — the two agree.
    /// </summary>
    internal static string ToLowercase(string s)
    {
        var runes = new List<Rune>(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            runes.Add(rune);
        }

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < runes.Count; i++)
        {
            var rune = runes[i];
            if (rune.Value == CapitalIWithDotAbove)
            {
                sb.Append('i').Append('̇');
            }
            else if (rune.Value == GreekCapitalSigma && IsFinalSigmaPosition(runes, i))
            {
                sb.Append(GreekFinalSigma);
            }
            else
            {
                sb.Append(Rune.ToLowerInvariant(rune).ToString());
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Compares two strings by successive Unicode code point (
    /// <see cref="Rune"/>) values, matching Rust's <c>String::cmp</c> /
    /// <c>Ord for str</c> (byte-wise comparison of the UTF-8 encoding, which
    /// is equivalent to code-point order since UTF-8 preserves code-point
    /// ordering). This differs from <c>StringComparer.Ordinal</c> /
    /// <c>string.CompareOrdinal</c>, which compare UTF-16 *code units*: a
    /// supplementary-plane character (code point &gt; U+FFFF) is encoded as a
    /// surrogate pair starting at or above U+D800, which sorts BELOW any
    /// BMP character in the U+E000-U+FFFF range under ordinal comparison but
    /// ABOVE it in code-point order.
    ///
    /// A shorter string that is a prefix of a longer one sorts first, as in
    /// Rust.
    /// </summary>
    internal static int CompareCodePoints(string a, string b)
    {
        using var runesA = a.EnumerateRunes().GetEnumerator();
        using var runesB = b.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasA = runesA.MoveNext();
            var hasB = runesB.MoveNext();
            if (!hasA || !hasB)
            {
                // Shorter-is-first, matching Rust's Ord for str/slices.
                return hasA.CompareTo(hasB);
            }

            var cmp = runesA.Current.Value.CompareTo(runesB.Current.Value);
            if (cmp != 0)
            {
                return cmp;
            }
        }
    }

    /// <summary>
    /// True when the Σ at <paramref name="runes"/>[<paramref name="index"/>]
    /// is in Unicode's Final_Sigma position: the nearest non-case-ignorable
    /// character before it is cased, and the nearest non-case-ignorable
    /// character after it (if any) is NOT cased.
    /// </summary>
    private static bool IsFinalSigmaPosition(IReadOnlyList<Rune> runes, int index)
    {
        var precededByCased = false;
        for (var j = index - 1; j >= 0; j--)
        {
            if (IsCaseIgnorable(runes[j]))
            {
                continue;
            }

            precededByCased = IsCased(runes[j]);
            break;
        }

        if (!precededByCased)
        {
            return false;
        }

        for (var j = index + 1; j < runes.Count; j++)
        {
            if (IsCaseIgnorable(runes[j]))
            {
                continue;
            }

            return !IsCased(runes[j]);
        }

        // Reached the end of the string without finding a non-ignorable
        // character: nothing follows, so this is final.
        return true;
    }

    /// <summary>
    /// Approximates the Unicode "Cased" property (D136): uppercase,
    /// lowercase, or titlecase letters.
    /// </summary>
    private static bool IsCased(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter;
    }

    /// <summary>
    /// Approximates the Unicode "Case_Ignorable" property (D136): combining
    /// marks, formatting characters, and modifier letters/symbols, plus the
    /// three punctuation characters (apostrophe, soft hyphen, right single
    /// quotation mark) the standard also names as case-ignorable. This is a
    /// deliberate simplification of the full derived property (which also
    /// includes a handful of General_Category=Cf/Lm/Sk/Mn/Me characters not
    /// covered by these category groupings, plus some by explicit
    /// Word_Break value) — sufficient for locating Final_Sigma in title
    /// text, not a full Unicode conformance claim.
    /// </summary>
    private static bool IsCaseIgnorable(Rune rune)
    {
        if (rune.Value is 0x0027 or 0x00AD or 0x2019)
        {
            return true;
        }

        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.ModifierSymbol;
    }
}
