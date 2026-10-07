// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Index v2: the types table, each concept's type and its description (spec §12.1, A18, A19).</summary>
public class SiteIndexTypesTests
{
    private static string Concept(string type, string extra = "")
        => "---\n" + (type.Length > 0 ? $"type: {type}\n" : string.Empty) + $"title: T\n{extra}---\n";

    private static ViewerIndex Build(TempDir tmp) => SiteIndex.Build(Bundle.Load(tmp.Path));

    private static readonly string Ellipsis = ((char)0x2026).ToString();

    [Fact]
    public void Types_are_ranked_by_count_then_ordinal_name_and_only_non_empty_names_take_the_five_ranks()
    {
        using var tmp = new TempDir();
        foreach (var (file, type) in new[]
        {
            ("m1", "Metric"), ("m2", "Metric"), ("m3", "Metric"), ("c1", "Attested Computation"), ("c2", "Attested Computation"),
            ("p1", "Policy"), ("p2", "Policy"), ("none", ""), ("t1", "BigQuery Table"), ("l1", "Log"), ("s1", "Skill"),
        })
        {
            tmp.Write(file + ".md", Concept(type));
        }

        Assert.Equal(
            new[]
            {
                new IndexType("Metric", 3, 0), new IndexType("Attested Computation", 2, 1), new IndexType("Policy", 2, 2),
                new IndexType("", 1, 5), new IndexType("BigQuery Table", 1, 3), new IndexType("Log", 1, 4), new IndexType("Skill", 1, 5),
            },
            Build(tmp).Types);
    }

    [Fact]
    public void Each_concept_points_at_its_own_type()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Concept("Metric"));
        tmp.Write("b.md", Concept("Policy"));
        tmp.Write("c.md", Concept("Metric"));
        tmp.Write("d.md", Concept(""));
        var index = Build(tmp);

        foreach (var concept in index.Concepts)
        {
            Assert.Equal(concept.Type, index.Types[concept.TypeIndex].Name);
        }
    }

    [Fact]
    public void Acme_retail_ranks_its_types_as_the_spec_states()
    {
        // Spec §12.1 and A28: the rule applies even where the mockup, whose
        // data is illustrative, swaps BigQuery Table and Skill.
        var index = SiteIndex.Build(Bundle.Load(Path.Combine(TestPaths.RepoRoot(), "bundles", "acme_retail")));

        Assert.Equal(
            new[] { ("Metric", 0), ("Attested Computation", 1), ("Policy", 2), ("BigQuery Table", 3), ("Skill", 4) },
            index.Types.Select(t => (t.Name, t.Slot)));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  Two\t\tspaces\n and\r\nlines  ", "Two spaces and lines")]
    public void A_description_has_its_whitespace_runs_collapsed_and_its_ends_trimmed(string? raw, string expected)
        => Assert.Equal(expected, SiteIndex.Describe(raw, SiteIndex.DescriptionLimit));

    [Fact]
    public void A_description_at_the_limit_is_kept_whole()
    {
        var text = new string('a', SiteIndex.DescriptionLimit);

        Assert.Equal(text, SiteIndex.Describe(text, SiteIndex.DescriptionLimit));
    }

    [Fact]
    public void A_longer_description_keeps_limit_minus_one_code_points_then_an_ellipsis()
    {
        var limit = SiteIndex.DescriptionLimit;

        Assert.Equal(new string('a', limit - 1) + Ellipsis, SiteIndex.Describe(new string('a', limit + 1), limit));
    }

    [Fact]
    public void Spaces_left_at_the_cut_are_removed_before_the_ellipsis()
        => Assert.Equal("aaaaaaaa" + Ellipsis, SiteIndex.Describe("aaaaaaaa bbbbbbbb", 10));

    [Fact]
    public void A_surrogate_pair_counts_one_code_point_and_is_never_split()
    {
        var face = char.ConvertFromUtf32(0x1F600);

        Assert.Equal("aaaaaaaa" + face, SiteIndex.Describe("aaaaaaaa" + face, 10));
        Assert.Equal("aaaaaaaa" + face + Ellipsis, SiteIndex.Describe("aaaaaaaa" + face + "bb", 10));
        Assert.Equal("aaaaaaaaa" + Ellipsis, SiteIndex.Describe("aaaaaaaaa" + face + "b", 10));
    }

    [Fact]
    public void A_lone_surrogate_counts_one_code_point_and_is_kept_as_is()
    {
        var lone = ((char)0xD800).ToString();

        Assert.Equal("aaaa" + lone, SiteIndex.Describe("aaaa" + lone, 5));
        Assert.Equal("aaaa" + Ellipsis, SiteIndex.Describe("aaaa" + lone + "b", 5));
    }

    [Fact]
    public void The_index_carries_each_concepts_truncated_description()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Concept("Note", $"description: {new string('x', 250)}\n"));
        var concept = Assert.Single(Build(tmp).Concepts);

        Assert.Equal(SiteIndex.DescriptionLimit, concept.Description.Length);
        Assert.EndsWith(Ellipsis, concept.Description, StringComparison.Ordinal);
    }
}
