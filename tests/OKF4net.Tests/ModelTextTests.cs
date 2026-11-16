// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Tests;

/// <summary>
/// <see cref="ModelText.Fold"/>, class by class (#122, design section 2.1):
/// every line terminator becomes one space, every other C0/C1 control U+FFFD,
/// every bidi control is removed, and everything else is left as is. Every
/// invisible character is written as an escape sequence, never literally.
/// </summary>
public class ModelTextTests
{
    [Theory]
    [InlineData("a\u000Db", "a b")]           // CR
    [InlineData("a\u000Ab", "a b")]           // LF
    [InlineData("a\u000D\u000Ab", "a b")]    // CRLF: one line ending, one space
    [InlineData("a\u000A\u000Db", "a  b")]   // LF then CR: two line endings
    [InlineData("a\u0085b", "a b")]           // NEL
    [InlineData("a\u000Bb", "a b")]           // VT
    [InlineData("a\u000Cb", "a b")]           // FF
    [InlineData("a\u2028b", "a b")]           // LINE SEPARATOR
    [InlineData("a\u2029b", "a b")]           // PARAGRAPH SEPARATOR
    [InlineData("\u000D\u000A", " ")]
    [InlineData("x\u000A## FORGED\u2028- FORGED", "x ## FORGED - FORGED")]
    public void A_line_terminator_becomes_one_space(string input, string expected)
    {
        Assert.Equal(expected, ModelText.Fold(input));
        Assert.Contains(input, c => ModelText.Classify(c) == ModelTextCharClass.LineTerminator);
    }

    [Theory]
    [InlineData((char)0x0D)]
    [InlineData((char)0x0A)]
    [InlineData((char)0x0B)]
    [InlineData((char)0x0C)]
    [InlineData((char)0x85)]
    [InlineData((char)0x2028)]
    [InlineData((char)0x2029)]
    public void Each_line_terminator_is_classified_as_one(char c) =>
        Assert.Equal(ModelTextCharClass.LineTerminator, ModelText.Classify(c));

    [Theory]
    [InlineData("a\u001Bb")]  // ESC
    [InlineData("a\u0000b")]  // NUL
    [InlineData("a\u0007b")]  // BEL
    [InlineData("a\u0008b")]  // BS
    [InlineData("a\u007Fb")]  // DEL
    [InlineData("a\u0080b")]  // C1: PAD
    [InlineData("a\u009Bb")]  // C1: CSI
    [InlineData("a\u009Fb")]  // C1: APC, the last C1
    [InlineData("a\u001Fb")]  // the last C0
    public void Any_other_control_becomes_the_replacement_character(string input)
    {
        Assert.Equal("a\uFFFDb", ModelText.Fold(input));
        Assert.Equal(ModelTextCharClass.Control, ModelText.Classify(input[1]));
    }

    [Theory]
    [InlineData((char)0x061C)]
    [InlineData((char)0x200E)]
    [InlineData((char)0x200F)]
    [InlineData((char)0x202A)]
    [InlineData((char)0x202B)]
    [InlineData((char)0x202C)]
    [InlineData((char)0x202D)]
    [InlineData((char)0x202E)]
    [InlineData((char)0x2066)]
    [InlineData((char)0x2067)]
    [InlineData((char)0x2068)]
    [InlineData((char)0x2069)]
    public void Each_bidi_control_is_removed(char bidi)
    {
        Assert.Equal("ab", ModelText.Fold("a" + bidi + "b"));
        Assert.Equal(ModelTextCharClass.Bidi, ModelText.Classify(bidi));
        Assert.True(ModelText.IsBidiControl(bidi));
        Assert.True(BundleConceptWriter.IsBidiControl(bidi));
    }

    [Fact]
    public void There_are_exactly_twelve_bidi_controls()
    {
        var count = 0;
        for (var i = 0; i <= 0xFFFF; i++)
        {
            if (ModelText.IsBidiControl((char)i))
            {
                count++;
            }
        }

        Assert.Equal(12, count);
    }

    [Theory]
    [InlineData("plain ascii text")]
    [InlineData("## heading-looking - text: with markers")]
    [InlineData("caf\u00E9 \u65E5\u672C\u8A9E \u062A\u062D\u062F\u064A\u062B \u05E2\u05D3\u05DB\u05D5\u05DF")] // accents, CJK, Arabic and Hebrew letters
    [InlineData("tab\u0009inside")]                 // TAB is ordinary whitespace inside a line
    [InlineData("zero\u200Bwidth \u00A0nbsp")]     // other format and space characters are out of scope
    [InlineData("")]
    public void Ordinary_text_is_unchanged(string input)
    {
        Assert.Same(input, ModelText.Fold(input));
        Assert.False(ModelText.NeedsFold(input));
    }

    [Fact]
    public void A_surrogate_pair_stays_intact_beside_folded_characters()
    {
        var rocket = char.ConvertFromUtf32(0x1F680);
        var folded = ModelText.Fold(rocket + "\u000A" + rocket + "\u202E" + rocket + "\u001B");

        Assert.Equal(rocket + " " + rocket + rocket + "\uFFFD", folded);
        Assert.Equal(3, folded.EnumerateRunes().Count(r => r.Value == 0x1F680));
    }

    [Fact]
    public void Fold_is_idempotent()
    {
        var hostile = "a\u000D\u000A\u2028b\u001B\u202Ec\u0085d\u000B\u000C\u0009e";
        var once = ModelText.Fold(hostile);

        Assert.Equal("a  b\uFFFDc d  \u0009e", once);
        Assert.Equal(once, ModelText.Fold(once));
        Assert.False(ModelText.NeedsFold(once));
    }

    /// <summary>
    /// The refusal predicate and the fold share one classification: whatever
    /// the fold would change, the predicate refuses (so a value it accepts
    /// prints unchanged), and the only character it refuses that the fold
    /// keeps is TAB. Every BMP code unit is checked, not a sample.
    /// </summary>
    [Fact]
    public void The_line_safe_predicate_refuses_every_character_the_fold_changes()
    {
        for (var i = 0; i <= 0xFFFF; i++)
        {
            var s = ((char)i).ToString();
            var folds = ModelText.NeedsFold(s);
            var refused = LineSafeText.ContainsControlCharacter(s);

            Assert.True(!folds || refused, $"U+{i:X4} is folded but not refused");
            Assert.True(folds || !refused || i == 0x09, $"U+{i:X4} is refused but not folded");
        }
    }

    /// <summary>
    /// What the predicate refused before #122 it still refuses (every C0/C1
    /// control, TAB included, and the two separators); the bidi controls are
    /// the only addition.
    /// </summary>
    [Fact]
    public void The_line_safe_predicate_only_grew()
    {
        for (var i = 0; i <= 0xFFFF; i++)
        {
            var c = (char)i;
            var before = char.IsControl(c) || i is 0x2028 or 0x2029;
            if (before)
            {
                Assert.True(LineSafeText.ContainsControlCharacter(c.ToString()), $"U+{i:X4} was refused and no longer is");
            }
            else
            {
                Assert.Equal(ModelText.IsBidiControl(c), LineSafeText.ContainsControlCharacter(c.ToString()));
            }
        }
    }
}
