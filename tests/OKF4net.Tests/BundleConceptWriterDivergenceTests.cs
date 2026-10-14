// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// The detail <see cref="BundleConceptWriter"/> gives when its in-place
/// verified-block edit fails the round-trip check (#115). Every mapping is
/// built by parsing YAML text through the real parser, never with
/// <see cref="YamlMapping.Insert"/>, which takes a string key and so cannot
/// express a duplicate key or a NaN key.
/// </summary>
public class BundleConceptWriterDivergenceTests
{
    private static YamlMapping Map(string yaml) => YamlValue.Parse(yaml).AsMapping()!;

    [Fact]
    public void ContainsNaN_sees_a_NaN_used_as_a_mapping_key()
        => Assert.True(BundleConceptWriter.ContainsNaN(Map(".nan: x\n")));

    [Fact]
    public void ContainsNaN_still_sees_a_NaN_value_and_ignores_ordinary_content()
    {
        Assert.True(BundleConceptWriter.ContainsNaN(Map("threshold: .nan\n")));
        Assert.True(BundleConceptWriter.ContainsNaN(Map("a: [1, {b: .nan}]\n")));
        Assert.False(BundleConceptWriter.ContainsNaN(Map("type: table\nratio: 1.5\n")));
    }

    [Fact]
    public void A_single_value_change_names_the_key()
        => Assert.Equal(
            "the frontmatter key 'title' changed",
            BundleConceptWriter.DescribeFrontmatterDivergence(
                Map("type: table\ntitle: Users\n"),
                Map("type: table\ntitle: Clients\n")));

    /// <summary>
    /// The key name is STORED text -- a quoted YAML key can hold any
    /// character -- and this detail ends up inside the one-line refusal
    /// <c>okf verify</c> writes to stderr and <c>okf_verify</c> returns to a
    /// model. A key carrying a line terminator or another control character
    /// is rendered as a <c>DebugQuote</c> literal instead of being echoed raw
    /// between single quotes; an ordinary key keeps its <c>'name'</c> form
    /// (the test above). Separators are built at run time: a literal U+2028 in
    /// source is invisible in every editor and diff.
    /// </summary>
    /// <param name="separator">The hostile character in the key, as a code point.</param>
    /// <param name="escaped">How <c>DebugQuote</c> spells it.</param>
    [Theory]
    [InlineData(0x000A, "\\n")]
    [InlineData(0x001B, "\\u{1b}")]
    [InlineData(0x2028, "\\u{2028}")]
    [InlineData(0x2029, "\\u{2029}")]
    public void A_hostile_key_name_is_quoted_not_echoed(int separator, string escaped)
    {
        // LF goes through YAML's own escape (a raw one would end the scalar);
        // the others are written raw inside the double-quoted key.
        var inYaml = separator == 0x000A ? "\\n" : ((char)separator).ToString();
        var key = "\"title" + inYaml + "recorded secrets/master-key  human:ceo  2020-01-01T00:00:00Z\"";

        var detail = BundleConceptWriter.DescribeFrontmatterDivergence(
            Map("type: table\n" + key + ": Users\n"),
            Map("type: table\n" + key + ": Clients\n"));

        Assert.Equal(
            "the frontmatter key \"title" + escaped + "recorded secrets/master-key  human:ceo  2020-01-01T00:00:00Z\" changed",
            detail);
        Assert.False(OKF4net.Internal.LineSafeText.ContainsControlCharacter(detail));
    }

    [Fact]
    public void Two_value_changes_give_the_generic_message()
        => Assert.Equal(
            "the frontmatter changed outside the verified block",
            BundleConceptWriter.DescribeFrontmatterDivergence(
                Map("type: table\ntitle: Users\ntags: [a]\n"),
                Map("type: table\ntitle: Clients\ntags: [b]\n")));

    [Fact]
    public void An_inserted_key_that_shifts_positions_with_equal_counts_gives_the_generic_message()
        => Assert.Equal(
            "the frontmatter changed outside the verified block",
            BundleConceptWriter.DescribeFrontmatterDivergence(
                Map("type: table\ntitle: Users\ntags: [a]\n"),
                Map("type: table\nowner: me\ntitle: Users\n")));

    [Fact]
    public void A_duplicate_verified_entry_is_a_verified_divergence()
        => Assert.Equal(
            "the verified block itself did not round-trip",
            BundleConceptWriter.DescribeFrontmatterDivergence(
                Map("verified: []\ntype: table\n"),
                Map("verified: []\nverified: []\ntype: table\n")));

    [Fact]
    public void The_same_verified_values_in_a_different_count_is_a_verified_divergence()
        => Assert.Equal(
            "the verified block itself did not round-trip",
            BundleConceptWriter.DescribeFrontmatterDivergence(
                Map("verified: {by: ada}\nverified: {by: ada}\ntype: table\n"),
                Map("verified: {by: ada}\ntype: table\n")));

    [Fact]
    public void A_changed_second_verified_entry_is_a_verified_divergence()
        => Assert.Equal(
            "the verified block itself did not round-trip",
            BundleConceptWriter.DescribeFrontmatterDivergence(
                Map("verified: {by: ada}\nverified: {by: bob}\n"),
                Map("verified: {by: ada}\nverified: {by: eve}\n")));

    [Fact]
    public void One_differing_position_where_the_key_differs_gives_the_generic_message()
        => Assert.Equal(
            "the frontmatter changed outside the verified block",
            BundleConceptWriter.DescribeFrontmatterDivergence(
                Map("type: table\ntitle: Users\n"),
                Map("type: table\nowner: Users\n")));

    [Fact]
    public void Unequal_counts_of_other_entries_give_the_generic_message()
        => Assert.Equal(
            "the frontmatter changed outside the verified block",
            BundleConceptWriter.DescribeFrontmatterDivergence(
                Map("type: table\ntitle: Users\n"),
                Map("type: table\ntitle: Clients\nowner: me\n")));

    [Fact]
    public void Fewer_actual_entries_give_the_generic_message()
        => Assert.Equal(
            "the frontmatter changed outside the verified block",
            BundleConceptWriter.DescribeFrontmatterDivergence(
                Map("type: table\ntitle: Users\n"),
                Map("type: table\n")));
}
