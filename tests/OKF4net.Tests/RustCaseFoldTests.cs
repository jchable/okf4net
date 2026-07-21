// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Tests;

/// <summary>
/// Tests for <see cref="RustCaseFold"/>, verifying it reproduces Rust's
/// <c>str::to_lowercase</c> (full Unicode case folding + the Final_Sigma
/// rule) and <c>String::cmp</c> (code-point / UTF-8 byte order) semantics
/// closely enough for title sorting, per the reviewed divergences in
/// IndexGenerator's <c>BuildIndexText</c>.
/// </summary>
public class RustCaseFoldTests
{
    [Fact]
    public void ToLowercase_maps_capital_i_with_dot_above_to_two_chars()
    {
        // U+0130 (İ) has the only unconditional multi-char lowercase mapping
        // in SpecialCasing.txt: "i" + U+0307 COMBINING DOT ABOVE. .NET's
        // ToLowerInvariant leaves U+0130 unchanged; Rust's to_lowercase does
        // not.
        Assert.Equal("i̇", RustCaseFold.ToLowercase("İ"));
    }

    [Fact]
    public void ToLowercase_applies_final_sigma_rule()
    {
        // "ΟΔΟΣ" (all-caps) -> "οδος": the trailing Σ is in final position
        // (preceded by a cased letter, not followed by one) so it becomes
        // ς (U+03C2), not the default σ (U+03C3).
        Assert.Equal("οδος", RustCaseFold.ToLowercase("ΟΔΟΣ"));

        // "ΣΟΣ" -> "σος": the leading Σ is NOT preceded by a cased letter
        // (start of string), so it takes the default mapping σ; the
        // trailing Σ is final, so it becomes ς.
        Assert.Equal("σος", RustCaseFold.ToLowercase("ΣΟΣ"));
    }

    [Fact]
    public void ToLowercase_skips_ascii_case_ignorable_punctuation_when_locating_final_sigma()
    {
        // "ΟΣ.ι": the Σ is followed by '.' (case-ignorable, per Rust's
        // char::is_case_ignorable ASCII fast path) and then by the cased ι
        // -- so, skipping the ignorable dot, Σ IS followed by a cased
        // character and is therefore NOT final. It takes the default
        // mapping σ, not ς.
        Assert.Equal("οσ.ι", RustCaseFold.ToLowercase("ΟΣ.ι"));

        // Same shape with ':' instead of '.' -- also in the ASCII
        // case-ignorable fast path.
        Assert.Equal("οσ:ι", RustCaseFold.ToLowercase("ΟΣ:ι"));

        // "ΟΣ.": nothing follows the ignorable trailing dot, so Σ IS
        // final and becomes ς.
        Assert.Equal("ος.", RustCaseFold.ToLowercase("ΟΣ."));
    }

    [Fact]
    public void ToLowercase_leaves_pure_ascii_unchanged_from_ToLowerInvariant()
    {
        Assert.Equal("hello world", RustCaseFold.ToLowercase("Hello World"));
        Assert.Equal("events_*", RustCaseFold.ToLowercase("events_*"));
    }

    [Fact]
    public void CompareCodePoints_orders_by_code_point_not_utf16_code_unit()
    {
        // U+E000 (private-use, in the BMP) vs U+1F600 (astral plane). In
        // UTF-16 *code-unit* (ordinal) order, U+E000 sorts as a single
        // 0xE000 unit while U+1F600 is encoded as the surrogate pair
        // 0xD83D 0xDE00 -- 0xD83D < 0xE000, so ordinal comparison would
        // (wrongly) say the astral character sorts first. Code-point order
        // (matching Rust's UTF-8 byte-wise String::cmp) says the opposite:
        // U+E000 < U+1F600.
        var bmpPrivateUse = "\uE000";
        var astral = "\U0001F600";

        Assert.True(RustCaseFold.CompareCodePoints(bmpPrivateUse, astral) < 0);
        // Sanity check on the claim above: ordinal comparison disagrees.
        Assert.True(string.CompareOrdinal(bmpPrivateUse, astral) > 0);
    }

    [Fact]
    public void CompareCodePoints_matches_ordinal_for_pure_ascii()
    {
        Assert.True(RustCaseFold.CompareCodePoints("apple", "banana") < 0);
        Assert.Equal(0, RustCaseFold.CompareCodePoints("same", "same"));
        Assert.True(RustCaseFold.CompareCodePoints("banana", "apple") > 0);
    }
}
