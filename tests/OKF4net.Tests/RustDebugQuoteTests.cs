// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Tests;

/// <summary>
/// Tests for <see cref="RustDebugQuote"/>, verifying it reproduces Rust's
/// <c>{:?}</c> (<c>char::escape_debug</c>) formatting for <c>&amp;str</c>:
/// the long-standing escapes (quote, backslash, common control characters)
/// plus the fidelity gap fixed alongside this consolidation -- Rust also
/// numerically escapes Grapheme_Extend characters (approximated here as
/// Unicode categories NonSpacingMark/EnclosingMark) so a combining mark
/// never renders fused onto an adjacent quote or escape.
/// </summary>
public class RustDebugQuoteTests
{
    [Fact]
    public void Plain_ascii_is_unchanged_but_quoted()
    {
        Assert.Equal("\"hello\"", RustDebugQuote.Quote("hello"));
    }

    [Fact]
    public void Quote_and_backslash_are_escaped()
    {
        Assert.Equal("\"a\\\"b\\\\c\"", RustDebugQuote.Quote("a\"b\\c"));
    }

    [Fact]
    public void Common_control_characters_use_named_escapes()
    {
        Assert.Equal("\"a\\nb\\rc\\td\"", RustDebugQuote.Quote("a\nb\rc\td"));
    }

    [Fact]
    public void Other_control_characters_are_escaped_numerically()
    {
        // U+0001 (SOH) has no named escape_debug form; Rust renders it \u{1}.
        var input = "a" + "" + "b";
        Assert.Equal("\"a\\u{1}b\"", RustDebugQuote.Quote(input));
    }

    [Fact]
    public void Combining_mark_is_escaped_numerically_like_rust_escape_debug()
    {
        // Input is deliberately the DECOMPOSED form "e" + U+0301 COMBINING
        // ACUTE ACCENT (not the precomposed U+00E9, which is a plain
        // LowercaseLetter and must NOT be escaped). U+0301 is
        // Grapheme_Extend=Yes (General Category Mn); Rust's
        // char::escape_debug -- and therefore {:?} -- escapes it as \u{301}
        // rather than emitting it literally, so it never visually fuses
        // onto the preceding character. This is the fidelity gap the three
        // previous private DebugQuote copies missed.
        var input = "e" + "́";
        Assert.Equal("\"e\\u{301}\"", RustDebugQuote.Quote(input));
    }

    [Fact]
    public void Astral_character_is_kept_as_one_unit_when_not_escaped()
    {
        // U+1F600 GRINNING FACE (outside the Mn/Me categories and not a
        // control character) is emitted literally, as a single rune -- not
        // escaped, and not broken apart into its UTF-16 surrogate pair.
        var input = "\U0001F600";
        Assert.Equal("\"" + input + "\"", RustDebugQuote.Quote(input));
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
        Assert.Equal("\"a\\u{1e944}b\"", RustDebugQuote.Quote(input));
    }
}
