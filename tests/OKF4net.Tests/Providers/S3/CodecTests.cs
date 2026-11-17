// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec §4.3, strict decoding.</summary>
public class CodecTests
{
    /// <summary>The string of one code point (escapes kept out of the source on purpose: they read alike).</summary>
    private static string U(int codePoint) => char.ConvertFromUtf32(codePoint);

    [Theory]
    [InlineData("%FF")]
    [InlineData("%C0%AF")]
    [InlineData("%ED%A0%80")]
    [InlineData("%")]
    [InlineData("%4")]
    [InlineData("%G1")]
    public void Invalid_encodings_are_refused(string encoded) => Assert.False(S3KeyCodec.TryDecode(encoded, out _));

    [Theory]
    [InlineData("%25FF", "%FF")]
    [InlineData("%25C0%25AF", "%C0%AF")]
    [InlineData("a+b.md", "a+b.md")]
    [InlineData("%F0%90%80%80", "\U00010000")]
    public void Valid_encodings_decode_once_exactly(string encoded, string expected)
    {
        Assert.True(S3KeyCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void Valid_encodings_decode_once_exactly_without_normalization()
    {
        Assert.True(S3KeyCodec.TryDecode("caf%C3%A9.md", out var nfc));
        Assert.Equal("caf" + U(0xE9) + ".md", nfc);
        Assert.True(S3KeyCodec.TryDecode("cafe%CC%81.md", out var nfd));
        Assert.Equal("cafe" + U(0x301) + ".md", nfd); // NFD stays NFD
        Assert.True(S3KeyCodec.TryDecode("bad%01.md", out var control));
        Assert.Equal("bad" + U(1) + ".md", control);
    }

    [Theory]
    [InlineData("a+b.md", "a b.md")]       // a listed value is form-encoded: Amazon S3 and MinIO send a space as '+'
    [InlineData("a%2Bb.md", "a+b.md")]     // ... and a literal '+' as %2B
    [InlineData("a%20b.md", "a b.md")]     // RustFS sends a space as %20
    [InlineData("++", "  ")]
    [InlineData("%2B%2B", "++")]
    [InlineData("%2B+%20", "+  ")]
    [InlineData("%25FF", "%FF")]
    [InlineData("%2525", "%25")]
    [InlineData("%F0%90%80%80", "\U00010000")]
    public void A_listed_value_decodes_a_raw_plus_as_a_space(string encoded, string expected)
    {
        Assert.True(S3KeyCodec.TryDecodeListed(encoded, out var decoded));
        Assert.Equal(expected, decoded);
    }

    [Theory]
    [InlineData("%FF")]
    [InlineData("%C0%AF")]
    [InlineData("%ED%A0%80")]
    [InlineData("%")]
    [InlineData("%4")]
    [InlineData("%G1")]
    [InlineData("a+%FF")]
    [InlineData("+%")]
    public void A_listed_value_is_as_strict_as_any_other(string encoded) => Assert.False(S3KeyCodec.TryDecodeListed(encoded, out _));

    [Fact]
    public void Only_listed_values_read_a_plus_as_a_space()
    {
        // The guard and the request paths compare literally: what the SDK sends never carries a raw '+' (it sends %2B).
        Assert.True(S3KeyCodec.TryDecode("a+b.md", out var literal));
        Assert.Equal("a+b.md", literal);
        Assert.True(S3KeyCodec.TryDecodeListed("a+b.md", out var listed));
        Assert.Equal("a b.md", listed);
    }

    [Fact]
    public void Lax_decoding_aliases_are_impossible()
    {
        Assert.False(S3KeyCodec.TryDecode("%FF.md", out _));
        Assert.True(S3KeyCodec.TryDecode("%25FF.md", out var literal));
        Assert.Equal("%FF.md", literal);
    }

    [Fact]
    public void Code_point_order_is_utf8_order_not_utf16_order()
    {
        // U+E000 < U+10000 in UTF-8 (EE 80 80 < F0 90 80 80); UTF-16 ordinal says the opposite (E000 > D800).
        Assert.True(S3KeyCodec.CompareByCodePoint(U(0xE000), U(0x10000)) < 0);
        Assert.True(string.CompareOrdinal(U(0xE000), U(0x10000)) > 0);
    }

    [Theory]
    [InlineData("a", "b", -1)]
    [InlineData("a", "a", 0)]
    [InlineData("a", "ab", -1)]
    [InlineData("ab", "a", 1)]
    [InlineData("", "", 0)]
    [InlineData("", "a", -1)]
    [InlineData("kb/a.md", "kb/a.md.bak", -1)]
    public void Code_point_order_is_a_total_order_with_prefixes_first(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(S3KeyCodec.CompareByCodePoint(a, b)));
        Assert.Equal(-sign, Math.Sign(S3KeyCodec.CompareByCodePoint(b, a)));
    }

    [Fact]
    public void Code_point_order_agrees_with_the_utf8_byte_order()
    {
        // The two orders S3 and the provider use must coincide on every well-formed pair (spec §4.5).
        int[] points = [0x61, 0x62, 0x7E, 0x7F, 0x80, 0x7FF, 0x800, 0xE9, 0x301, 0xD7FF, 0xE000, 0xFFFD, 0xFFFF, 0x10000, 0x1F600, 0x10FFFF];
        var samples = new List<string> { "", "a/b", "a.md", "a/", "e" + U(0x301) };
        samples.AddRange(points.Select(U));
        samples.AddRange(points.Select(p => "a" + U(p) + "z"));
        foreach (var a in samples)
        {
            foreach (var b in samples)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(System.Text.Encoding.UTF8.GetBytes(b));
                Assert.Equal(Math.Sign(bytes), Math.Sign(S3KeyCodec.CompareByCodePoint(a, b)));
            }
        }
    }
}
