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
