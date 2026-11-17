// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec §4.5: the s3v1 encoding, octet by octet.</summary>
public class BundleTokenTests
{
    private static string H(params (string, string, long)[] e) => BundleTokenHasher.Hash(e);

    private static string Expected(params byte[] stream) => "s3v1:" + Convert.ToHexStringLower(SHA256.HashData(stream));

    [Fact]
    public void The_encoding_is_fixed_by_a_vector()
    {
        // stream = len("a.md")=4 | "a.md" | len("\"e\"")=3 | "\"e\"" | len("1")=1 | "1", lengths as 4-byte big-endian
        var expected = Expected([0, 0, 0, 4, (byte)'a', (byte)'.', (byte)'m', (byte)'d', 0, 0, 0, 3, (byte)'"', (byte)'e', (byte)'"', 0, 0, 0, 1, (byte)'1']);
        Assert.Equal(expected, H(("a.md", "\"e\"", 1)));
    }

    [Fact]
    public void The_encoding_of_a_supplementary_name_after_a_private_use_name_is_fixed_by_a_vector()
    {
        // U+E000 (EE 80 80) then U+10000 (F0 90 80 80): the UTF-8 order the walker requires; sizes 0 and 1234567.
        var expected = Expected(
            [
                0, 0, 0, 3, 0xEE, 0x80, 0x80, 0, 0, 0, 3, (byte)'"', (byte)'x', (byte)'"', 0, 0, 0, 1, (byte)'0',
                0, 0, 0, 4, 0xF0, 0x90, 0x80, 0x80, 0, 0, 0, 3, (byte)'"', (byte)'y', (byte)'"', 0, 0, 0, 7, (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7',
            ]);
        Assert.Equal(expected, H((char.ConvertFromUtf32(0xE000), "\"x\"", 0), (char.ConvertFromUtf32(0x10000), "\"y\"", 1234567)));
    }

    [Fact]
    public void The_empty_listing_has_a_token()
    {
        Assert.Equal(Expected(), H());
        Assert.StartsWith("s3v1:", H(), StringComparison.Ordinal);
        Assert.Equal("s3v1:".Length + 64, H().Length);
    }

    [Fact]
    public void Every_change_of_the_triplet_changes_the_token()
    {
        var baseline = H(("a.md", "\"e\"", 1), ("b.md", "\"f\"", 2));
        Assert.NotEqual(baseline, H(("a.md", "\"e\"", 1)));
        Assert.NotEqual(baseline, H(("a.md", "\"e\"", 1), ("b.md", "\"f\"", 2), ("c.md", "\"g\"", 3)));
        Assert.NotEqual(baseline, H(("a.md", "\"e\"", 1), ("c.md", "\"f\"", 2)));
        Assert.NotEqual(baseline, H(("a.md", "\"e\"", 1), ("b.md", "\"f\"", 3)));
        Assert.NotEqual(baseline, H(("a.md", "\"e\"", 1), ("b.md", "\"x\"", 2)));
        Assert.NotEqual(baseline, H(("b.md", "\"f\"", 2), ("a.md", "\"e\"", 1))); // the listing's order is hashed
        Assert.NotEqual(H(("ab", "\"c\"", 1)), H(("a", "b\"c\"", 1))); // field framing: no concatenation alias
        Assert.NotEqual(H(("a", "\"e\"", 11)), H(("a", "\"e\"1", 1)));
        Assert.NotEqual(H(("a", "\"e\"", 1), ("b", "\"e\"", 1)), H(("a", "\"e\"", 1), ("", "b\"e\"", 1)));
    }

    [Fact]
    public void The_incremental_builder_and_the_one_shot_hash_agree()
    {
        using var builder = new BundleTokenHasher();
        builder.Add("a.md", "\"e\"", 1);
        builder.Add("b.md", "\"f\"", 2);
        Assert.Equal(H(("a.md", "\"e\"", 1), ("b.md", "\"f\"", 2)), builder.Finish());
    }

    [Fact]
    public void Culture_does_not_change_the_token()
    {
        var before = H(("a.md", "\"e\"", 1234567));
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
        try
        {
            Assert.Equal(before, H(("a.md", "\"e\"", 1234567)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
