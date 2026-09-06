// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Tests.CodeGraph;

public class Utf8OffsetsTests
{
    [Fact]
    public void Ascii_only_text_has_identical_utf8_and_utf16_offsets()
    {
        const string text = "var x = Foo();";
        var utf16Index = text.IndexOf("Foo", StringComparison.Ordinal);

        Assert.Equal(utf16Index, Utf8Offsets.ToUtf8(text, utf16Index));
        Assert.Equal(utf16Index, Utf8Offsets.ToUtf16(text, utf16Index));
    }

    [Fact]
    public void An_accented_bmp_character_before_the_call_shifts_the_utf8_offset_by_one_byte()
    {
        // café: 'é' (U+00E9) is one UTF-16 code unit but two UTF-8 bytes.
        const string text = "var café = Foo();";
        var utf16Index = text.IndexOf("Foo", StringComparison.Ordinal);

        var utf8Index = Utf8Offsets.ToUtf8(text, utf16Index);

        Assert.NotEqual(utf16Index, utf8Index);
        Assert.Equal(utf16Index + 1, utf8Index);
        Assert.Equal(utf16Index, Utf8Offsets.ToUtf16(text, utf8Index));
    }

    [Fact]
    public void A_surrogate_pair_before_the_call_shifts_the_utf8_offset_by_two_bytes()
    {
        // \U0001F3AF (a dart-throwing target emoji) is a surrogate pair: two UTF-16 code units,
        // four UTF-8 bytes -- a naive per-char loop would miscount each half as a lone BMP
        // character instead of recognizing the pair as one astral-plane codepoint.
        const string text = "var x = \"\U0001F3AF\"; Foo();";
        var utf16Index = text.IndexOf("Foo", StringComparison.Ordinal);

        var utf8Index = Utf8Offsets.ToUtf8(text, utf16Index);

        Assert.NotEqual(utf16Index, utf8Index);
        Assert.Equal(utf16Index + 2, utf8Index);
        Assert.Equal(utf16Index, Utf8Offsets.ToUtf16(text, utf8Index));
    }

    [Fact]
    public void Crlf_line_endings_do_not_disturb_the_offset_conversion()
    {
        // naïve: 'ï' (U+00EF) is one UTF-16 code unit but two UTF-8 bytes; the CRLF pair that
        // follows is plain ASCII and should not add any further skew.
        const string text = "var naïve = 1;\r\nFoo();";
        var utf16Index = text.IndexOf("Foo", StringComparison.Ordinal);

        var utf8Index = Utf8Offsets.ToUtf8(text, utf16Index);

        Assert.Equal(utf16Index + 1, utf8Index);
        Assert.Equal(utf16Index, Utf8Offsets.ToUtf16(text, utf8Index));
    }

    [Fact]
    public void Round_trips_at_every_codepoint_boundary_across_ascii_latin_and_an_astral_character()
    {
        const string text = "café \U0001F3AF naïve";

        var boundaries = new List<int>();
        var utf16Index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            boundaries.Add(utf16Index);
            utf16Index += rune.Utf16SequenceLength;
        }

        boundaries.Add(text.Length);

        foreach (var boundary in boundaries)
        {
            var utf8Offset = Utf8Offsets.ToUtf8(text, boundary);
            Assert.Equal(boundary, Utf8Offsets.ToUtf16(text, utf8Offset));
        }
    }

    [Fact]
    public void ToUtf8_at_the_very_start_and_end_matches_zero_and_the_full_byte_count()
    {
        const string text = "café \U0001F3AF naïve";

        Assert.Equal(0, Utf8Offsets.ToUtf8(text, 0));
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(text), Utf8Offsets.ToUtf8(text, text.Length));
    }

    [Theory]
    [InlineData(-1)]
    public void ToUtf8_rejects_a_negative_offset(int utf16Offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Utf8Offsets.ToUtf8("abc", utf16Offset));
    }

    [Fact]
    public void ToUtf8_rejects_an_offset_past_the_end_of_the_text()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Utf8Offsets.ToUtf8("abc", 4));
    }

    [Theory]
    [InlineData(-1)]
    public void ToUtf16_rejects_a_negative_offset(int utf8Offset)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Utf8Offsets.ToUtf16("abc", utf8Offset));
    }

    [Fact]
    public void ToUtf16_rejects_an_offset_past_the_end_of_the_text()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Utf8Offsets.ToUtf16("abc", 4));
    }

    [Fact]
    public void An_offset_that_splits_a_surrogate_pair_is_refused_rather_than_converted()
    {
        // The failure mode this type exists to prevent is a PLAUSIBLE WRONG NUMBER, not a missing one:
        // it is the join key between two engines, so a bad conversion credits a call to whatever sits a
        // few bytes away rather than dropping it (§2.1).
        //
        // Measured before the guard: `GetByteCount` over a span ending on a lone high surrogate runs it
        // through the replacement fallback and scores 3 bytes -- neither the 0 of the codepoint's start
        // nor the 4 of its end. Exactly the shape of answer that looks usable and is not.
        const string Text = "A\U0001D11EB";   // A, a four-byte musical symbol (two UTF-16 units), B

        Assert.Equal(1, Utf8Offsets.ToUtf8(Text, 1));   // the boundary before the pair
        Assert.Equal(5, Utf8Offsets.ToUtf8(Text, 3));   // the boundary after it

        Assert.Throws<ArgumentOutOfRangeException>(() => Utf8Offsets.ToUtf8(Text, 2));
    }

    [Fact]
    public void A_utf8_offset_inside_a_sequence_is_refused_rather_than_rounded_up()
    {
        // The other direction, and the one that was silently wrong rather than merely undefined: the
        // walk tested `utf8Count >= utf8Offset`, so an offset one byte into the four-byte codepoint
        // returned the index AFTER it -- a real UTF-16 index, for a different position than the caller
        // asked about.
        const string Text = "A\U0001D11EB";

        Assert.Equal(1, Utf8Offsets.ToUtf16(Text, 1));   // the boundary before the codepoint
        Assert.Equal(3, Utf8Offsets.ToUtf16(Text, 5));   // the boundary after it

        foreach (var inside in new[] { 2, 3, 4 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Utf8Offsets.ToUtf16(Text, inside));
        }

        // And the two mistakes are still told apart, so the guard did not swallow the pre-existing one.
        Assert.Throws<ArgumentOutOfRangeException>(() => Utf8Offsets.ToUtf16(Text, 99));
    }
}
