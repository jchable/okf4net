// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Tests for the site index projection behind <c>assets/okf-index.js</c> (spec §3).</summary>
public class SiteIndexTests
{
    private static string Concept(string title, string extra = "", string body = "")
        => $"---\ntype: Note\ntitle: {title}\ndescription: d\n{extra}---\n{body}";

    private static ViewerIndex Build(TempDir tmp) => SiteIndex.Build(Bundle.Load(tmp.Path));

    [Fact]
    public void Concepts_are_ordered_by_ConceptId_CompareTo()
    {
        using var tmp = new TempDir();
        tmp.Write("a-b.md", Concept("AB"));
        tmp.Write("a/b.md", Concept("A slash B"));
        tmp.Write("a.md", Concept("A"));

        // Segment-wise ordinal: "a/b" sorts before "a-b", which a comparison
        // of the whole strings would reverse ('/' 0x2F > '-' 0x2D).
        Assert.Equal(new[] { "a", "a/b", "a-b" }, Build(tmp).Concepts.Select(c => c.Id.ToString()));
    }

    [Fact]
    public void A_concept_that_is_also_a_folder_is_one_node_with_a_destination_and_children()
    {
        using var tmp = new TempDir();
        tmp.Write("foo.md", Concept("Foo"));
        tmp.Write("foo/bar.md", Concept("Bar"));
        var index = Build(tmp);

        var foo = Assert.Single(index.Tree);
        Assert.Equal("foo", foo.Name);
        Assert.Equal("foo", index.Concepts[foo.Concept].Id.ToString());
        var bar = Assert.Single(foo.Children);
        Assert.Equal("bar", bar.Name);
        Assert.Equal("foo/bar", index.Concepts[bar.Concept].Id.ToString());
        Assert.Empty(bar.Children);
    }

    [Fact]
    public void A_folder_without_its_own_concept_has_no_destination()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/users.md", Concept("Users"));

        var tables = Assert.Single(Build(tmp).Tree);
        Assert.Equal(-1, tables.Concept);
        Assert.Single(tables.Children);
    }

    [Theory]
    [InlineData("", "unverified")]
    [InlineData("verified:\n  - { by: \"human:jsmith\", at: \"2026-07-01T09:00:00Z\" }\n", "human-reviewed")]
    [InlineData("verified:\n  - { by: \"tool:okf-ci\", at: \"2026-07-01T09:00:00Z\" }\n", "machine-confirmed")]
    public void Trust_is_the_ConceptAudit_tier_under_its_vocabulary_name(string extra, string expected)
    {
        using var tmp = new TempDir();
        tmp.Write("c.md", Concept("C", extra));

        Assert.Equal(expected, Assert.Single(Build(tmp).Concepts).Trust);
    }

    [Theory]
    [InlineData("2026-10-06T00:00:00Z", 0L, "2026-10-06")]
    [InlineData("2026-10-06T00:00:00.0001000Z", 1L, "2026-10-06")]          // rounded UP to the next whole ms
    [InlineData("2026-10-06T02:00:00+02:00", 0L, "2026-10-06")]             // offset normalized to UTC
    [InlineData("2026-10-06", 0L, "2026-10-06")]                            // legacy date-only: midnight UTC
    [InlineData("2026-10-06T23:59:59.9999Z", 86_400_000L, "2026-10-06")]    // ms tips into the next day, the date does not
    public void StaleAfter_is_exported_as_ceiling_milliseconds_and_the_Lifecycle_date(string raw, long offsetMs, string date)
    {
        using var tmp = new TempDir();
        tmp.Write("c.md", Concept("C", $"stale_after: \"{raw}\"\n"));
        var concept = Assert.Single(Build(tmp).Concepts);

        var midnight = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal(midnight + offsetMs, concept.StaleAfterMs);
        Assert.Equal(date, concept.StaleAfterDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("soon")]
    public void An_absent_or_malformed_stale_after_exports_nulls(string? raw)
    {
        using var tmp = new TempDir();
        tmp.Write("c.md", Concept("C", raw is null ? "" : $"stale_after: \"{raw}\"\n"));
        var concept = Assert.Single(Build(tmp).Concepts);

        Assert.Null(concept.StaleAfterMs);
        Assert.Null(concept.StaleAfterDate);
    }

    [Fact]
    public void Repeated_links_merge_into_one_edge_and_absent_targets_become_sorted_ghosts()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Concept("A", body: "[x](b.md) [y](b.md) [z](zz/gone.md) [w](missing.md)\n"));
        tmp.Write("b.md", Concept("B"));
        var index = Build(tmp);

        Assert.Equal(new[] { "missing", "zz/gone" }, index.Ghosts.Select(g => g.Id.ToString()));
        Assert.Equal(
            new[] { new IndexEdge(0, 1, 2, false), new IndexEdge(0, 0, 1, true), new IndexEdge(0, 1, 1, true) },
            index.Edges);
    }

    [Fact]
    public void Ids_that_name_Object_prototype_members_are_ordinary_concepts()
    {
        using var tmp = new TempDir();
        tmp.Write("__proto__.md", Concept("Proto"));
        tmp.Write("constructor.md", Concept("Constructor"));
        tmp.Write("toString.md", Concept("ToString"));

        Assert.Equal(
            new[] { "__proto__", "constructor", "toString" },
            Build(tmp).Concepts.Select(c => c.Id.ToString()));
    }

    [Fact]
    public void Title_falls_back_to_the_id_and_path_is_the_page_path()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/users.md", "---\ntype: Note\ndescription: d\ntags: [core, users]\n---\n");
        var concept = Assert.Single(Build(tmp).Concepts);

        Assert.Equal("tables/users", concept.Title);
        Assert.Equal("tables/users.html", concept.Path);
        Assert.Equal("Note", concept.Type);
        Assert.Equal(new[] { "core", "users" }, concept.Tags);
    }

    [Fact]
    public void SiteModel_Build_populates_the_index()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Concept("A"));

        var site = SiteModel.Build(Bundle.Load(tmp.Path));

        Assert.Equal("a", Assert.Single(site.Index.Concepts).Id.ToString());
    }
}
