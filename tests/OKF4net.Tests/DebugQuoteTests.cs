// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Tests;

/// <summary>
/// Tests for <see cref="DebugQuote"/>, verifying debug-style string quoting:
/// the escapes (quote, backslash, common control characters) plus numeric
/// escaping of Grapheme_Extend characters (approximated here as Unicode
/// categories NonSpacingMark/EnclosingMark) so a combining mark never renders
/// fused onto an adjacent quote or escape.
/// </summary>
public class DebugQuoteTests
{
    [Fact]
    public void Plain_ascii_is_unchanged_but_quoted()
    {
        Assert.Equal("\"hello\"", DebugQuote.Quote("hello"));
    }

    [Fact]
    public void Quote_and_backslash_are_escaped()
    {
        Assert.Equal("\"a\\\"b\\\\c\"", DebugQuote.Quote("a\"b\\c"));
    }

    [Fact]
    public void Common_control_characters_use_named_escapes()
    {
        Assert.Equal("\"a\\nb\\rc\\td\"", DebugQuote.Quote("a\nb\rc\td"));
    }

    [Fact]
    public void Other_control_characters_are_escaped_numerically()
    {
        // U+0001 (SOH) has no named escape form; it renders as \u{1}.
        //
        // Written as a numeric constant. It was a LITERAL U+0001 byte in this
        // source file until now -- invisible in every editor and diff that
        // would have to review it, which is exactly the hygiene rule the rest
        // of this area follows (see Internal/LineSafeText.cs on its own two).
        // Behaviour is identical; only the spelling changed.
        var input = "a" + (char)0x0001 + "b";
        Assert.Equal("\"a\\u{1}b\"", DebugQuote.Quote(input));
    }

    [Fact]
    public void Combining_mark_is_escaped_numerically()
    {
        // Input is deliberately the DECOMPOSED form "e" + U+0301 COMBINING
        // ACUTE ACCENT (not the precomposed U+00E9, which is a plain
        // LowercaseLetter and must NOT be escaped). U+0301 is
        // Grapheme_Extend=Yes (General Category Mn), so it is escaped as
        // \u{301} rather than emitted literally, so it never visually fuses
        // onto the preceding character. This is the fidelity gap the three
        // previous private DebugQuote copies missed.
        var input = "e" + "́";
        Assert.Equal("\"e\\u{301}\"", DebugQuote.Quote(input));
    }

    /// <summary>
    /// U+2028 LINE SEPARATOR and U+2029 PARAGRAPH SEPARATOR are General
    /// Categories Zl and Zp, not <c>Cc</c>, so the control arm never saw them
    /// and they were emitted literally. Every message built through this helper
    /// is a LINE in someone's output, and a markdown or JavaScript splitter
    /// downstream ends a line on either — so quoting a value for a human to
    /// read while leaving in the character that ends the line they read it on
    /// was not really quoting it.
    ///
    /// <para>Written as numeric constants: a literal separator in source is
    /// invisible in every editor and diff that would have to review it, the
    /// same reason <c>Internal/LineSafeText.cs</c> spells its two that way.</para>
    /// </summary>
    [Fact]
    public void Line_and_paragraph_separators_are_escaped_numerically()
    {
        Assert.Equal("\"a\\u{2028}b\"", DebugQuote.Quote("a" + (char)0x2028 + "b"));
        Assert.Equal("\"a\\u{2029}b\"", DebugQuote.Quote("a" + (char)0x2029 + "b"));
    }

    /// <summary>
    /// The deliberate limit of the arm above: Zl/Zp, not every Zs. An ordinary
    /// space and a NO-BREAK SPACE (U+00A0, Zs) do not end a line, and escaping
    /// them would disfigure every quoted multi-word value for nothing.
    /// </summary>
    [Fact]
    public void Space_separators_are_not_escaped()
    {
        Assert.Equal("\"a b\"", DebugQuote.Quote("a b"));
        Assert.Equal("\"a" + (char)0x00A0 + "b\"", DebugQuote.Quote("a" + (char)0x00A0 + "b"));
    }

    [Fact]
    public void Astral_character_is_kept_as_one_unit_when_not_escaped()
    {
        // U+1F600 GRINNING FACE (outside the Mn/Me categories and not a
        // control character) is emitted literally, as a single rune -- not
        // escaped, and not broken apart into its UTF-16 surrogate pair.
        var input = "\U0001F600";
        Assert.Equal("\"" + input + "\"", DebugQuote.Quote(input));
    }

    [Fact]
    public void Astral_character_is_escaped_as_one_unit_when_control()
    {
        // Sanity check that Rune-based iteration classifies and escapes an
        // out-of-BMP code point as a single \u{...} unit rather than a
        // broken-apart surrogate pair. There is no astral control
        // character in practice, so this instead exercises the mechanism
        // via a combining mark outside the BMP: U+1E944 (Mn, Adlam nasal
        // suffix combining diacritic).
        var input = "a" + "\U0001E944" + "b";
        Assert.Equal("\"a\\u{1e944}b\"", DebugQuote.Quote(input));
    }
}
