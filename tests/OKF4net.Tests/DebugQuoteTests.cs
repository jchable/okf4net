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
        var input = "a" + "" + "b";
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
