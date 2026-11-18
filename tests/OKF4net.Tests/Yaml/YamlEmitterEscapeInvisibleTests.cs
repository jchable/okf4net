// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// <see cref="YamlEmitter.EmitEscapingInvisible"/> (#256): the reader-facing
/// emit behind <c>okf_read_concept</c>'s frontmatter block. A key or value
/// holding an invisible, bidi, separator or control character is quoted, never
/// plain, with that character escaped; the round trip stays exact; and
/// <see cref="YamlEmitter.Emit"/>, what is written to disk, is unchanged.
/// </summary>
public class YamlEmitterEscapeInvisibleTests
{
    public static TheoryData<string, char> Categories() => new()
    {
        { "C0 VT", '\u000B' },
        { "C0 FF", '\u000C' },
        { "C0 SOH", '\u0001' },
        { "C1 first", '\u0080' },
        { "C1 NEL", '\u0085' },
        { "C1 last", '\u009F' },
        { "soft hyphen", (char)0x00AD },
        { "bidi ALM", (char)0x061C },
        { "bidi LRM", (char)0x200E },
        { "bidi RLM", (char)0x200F },
        { "bidi LRE", (char)0x202A },
        { "bidi RLO", (char)0x202E },
        { "bidi LRI", (char)0x2066 },
        { "bidi PDI", (char)0x2069 },
        { "zero-width space", (char)0x200B },
        { "ZWNJ", (char)0x200C },
        { "ZWJ", (char)0x200D },
        { "word joiner", (char)0x2060 },
        { "invisible plus", (char)0x2064 },
        { "BOM", (char)0xFEFF },
        { "Mongolian vowel separator", (char)0x180E },
        { "line separator", (char)0x2028 },
        { "paragraph separator", (char)0x2029 },
    };

    [Theory]
    [MemberData(nameof(Categories))]
    public void A_key_or_value_holding_the_character_is_quoted_with_it_escaped(string category, char c)
    {
        Assert.True(YamlEmitter.IsInvisibleOrBreaking(c), category);
        var map = new YamlMapping();
        map.Insert("plain", new YamlString("a" + c + "b"));
        map.Insert("k" + c, new YamlString("v"));
        map.Insert("list", new YamlSequence([new YamlString(c + "lead"), new YamlString("trail" + c)]));

        var shown = YamlEmitter.EmitEscapingInvisible(map);

        Assert.DoesNotContain(c, shown);
        Assert.DoesNotContain(shown, ch => ch != '\n' && YamlEmitter.IsInvisibleOrBreaking(ch));
        Assert.Contains("plain: \"a", shown, StringComparison.Ordinal);
        Assert.Contains("\"k", shown, StringComparison.Ordinal);
        Assert.True(map.Equals(YamlParser.Parse(shown)), category + ":\n" + shown);
    }

    [Fact]
    public void Every_character_the_predicate_accepts_round_trips_escaped()
    {
        for (var code = 0; code <= 0xFFFF; code++)
        {
            var c = (char)code;
            if (char.IsSurrogate(c) || !YamlEmitter.IsInvisibleOrBreaking(c))
            {
                continue;
            }

            var map = new YamlMapping();
            map.Insert("v", new YamlString("x" + c + "y"));
            var shown = YamlEmitter.EmitEscapingInvisible(map);
            Assert.DoesNotContain(shown, ch => ch != '\n' && YamlEmitter.IsInvisibleOrBreaking(ch));
            Assert.True(map.Equals(YamlParser.Parse(shown)), "U+" + code.ToString("X4", CultureInfo.InvariantCulture));
        }
    }

    [Theory]
    [InlineData((char)0x0020)]
    [InlineData((char)0x007F)]
    [InlineData((char)0x00A0)]
    [InlineData((char)0x200A)]
    [InlineData((char)0x2065)]
    [InlineData((char)0x206A)]
    [InlineData((char)0x00E9)]
    public void Neighbouring_characters_are_not_escaped(char c)
    {
        Assert.False(YamlEmitter.IsInvisibleOrBreaking(c));
    }

    [Theory]
    [MemberData(nameof(Categories))]
    public void What_is_written_to_disk_is_unchanged(string category, char c)
    {
        var map = new YamlMapping();
        map.Insert("v", new YamlString("a" + c + "b"));

        // Outside C0 (whose handling on disk is whatever it always was), each of
        // these characters stays a raw plain scalar on disk: the option is for
        // shown text only.
        var disk = YamlEmitter.Emit(map);
        if (c >= ' ')
        {
            Assert.Equal("v: a" + c + "b\n", disk);
        }

        Assert.True(map.Equals(YamlParser.Parse(disk)), category);
    }

    [Fact]
    public void An_astral_tag_character_cannot_be_escaped_and_stays_raw()
    {
        var tag = char.ConvertFromUtf32(0xE0041); // TAG LATIN CAPITAL LETTER A
        var map = new YamlMapping();
        map.Insert("v", new YamlString("a" + tag + "b"));

        var shown = YamlEmitter.EmitEscapingInvisible(map);

        Assert.Contains(tag, shown, StringComparison.Ordinal);
        Assert.True(map.Equals(YamlParser.Parse(shown)));
    }
}
