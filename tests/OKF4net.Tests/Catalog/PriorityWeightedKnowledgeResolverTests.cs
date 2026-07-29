// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="PriorityWeightedKnowledgeResolver"/>: priority is the PRIMARY
/// sort key, so a higher-priority source's passage never falls behind a
/// lower-priority one however weak its match, with score ordering only
/// within a single priority tier. Uses hand-written bundles (rather than the
/// appendix_a fixture) so the score relationship between the two sources is
/// controlled by the test rather than incidental to the fixture.
/// </summary>
public class PriorityWeightedKnowledgeResolverTests
{
    private static FileKnowledgeCatalog BuildCatalog(TempDir root, string sourcesJson)
    {
        root.Write("catalog.json", $$"""
            {
              "version": 1,
              "sources": [{{sourcesJson}}]
            }
            """);

        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
    }

    /// <summary>
    /// A low-priority source whose concept matches STRONGLY (the term is in
    /// the title, worth x3) and a high-priority source whose concept matches
    /// WEAKLY (body only, worth x1) -- the exact case where the two fused
    /// strategies must disagree.
    /// </summary>
    private static FileKnowledgeCatalog SetUpInvertedScores(TempDir root)
    {
        root.Write(Path.Combine("weak-hi", "note.md"),
            "---\ntype: Note\ntitle: Unrelated heading\ndescription: d\n---\nA passing mention of orders.\n");
        root.Write(Path.Combine("strong-lo", "note.md"),
            "---\ntype: Note\ntitle: Orders orders orders\ndescription: orders\n---\nOrders everywhere orders.\n");

        return BuildCatalog(root, """
            { "id": "strong-lo", "path": "./strong-lo", "priority": 1, "enabled": true },
            { "id": "weak-hi", "path": "./weak-hi", "priority": 10, "enabled": true }
            """);
    }

    [Fact]
    public async Task A_higher_priority_source_outranks_a_stronger_lower_priority_match()
    {
        using var root = new TempDir();
        using var catalog = SetUpInvertedScores(root);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(2, context.Passages.Count);
        Assert.Equal("weak-hi", context.Passages[0].SourceId);
        Assert.Equal("strong-lo", context.Passages[1].SourceId);

        // ...and this is genuinely the priority ordering winning, not the
        // score ordering coinciding with it: the first passage scores LOWER.
        Assert.True(
            context.Passages[0].Score < context.Passages[1].Score,
            "the fixture must put the weaker match in the higher-priority source for this test to mean anything");
    }

    [Fact]
    public async Task Merged_ranks_the_same_catalog_the_other_way_round()
    {
        using var root = new TempDir();
        using var catalog = SetUpInvertedScores(root);
        var merged = new MergedKnowledgeResolver(catalog);

        var context = await merged.SearchAsync(new KnowledgeQuery("orders"));

        // The companion assertion to the test above: same catalog, same
        // query, opposite order -- proving the two strategies are actually
        // distinct rather than both quietly sorting by score.
        Assert.Equal("strong-lo", context.Passages[0].SourceId);
        Assert.Equal("weak-hi", context.Passages[1].SourceId);
    }

    [Fact]
    public async Task Score_still_orders_passages_within_one_priority_tier()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("tier", "strong.md"),
            "---\ntype: Note\ntitle: Orders orders\ndescription: orders\n---\nOrders orders.\n");
        root.Write(Path.Combine("tier", "weak.md"),
            "---\ntype: Note\ntitle: Unrelated\ndescription: d\n---\nOne mention of orders.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "tier", "path": "./tier", "priority": 5, "enabled": true }
            """);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(2, context.Passages.Count);
        Assert.Equal("strong", context.Passages[0].ConceptId);
        Assert.Equal("weak", context.Passages[1].ConceptId);
    }

    [Fact]
    public async Task Two_source_entries_resolving_to_the_same_directory_are_searched_once()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("shared", "note.md"), "---\ntype: Note\ntitle: Orders\ndescription: d\n---\nOrders.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "alias", "path": "./shared/../shared", "priority": 1, "enabled": true },
            { "id": "primary", "path": "./shared", "priority": 10, "enabled": true }
            """);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        var passage = Assert.Single(context.Passages);
        Assert.Equal("primary", passage.SourceId);
    }

    [Fact]
    public async Task A_blank_query_text_throws()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("src", "note.md"), "---\ntype: Note\ntitle: Orders\ndescription: d\n---\nOrders.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "src", "path": "./src", "priority": 1, "enabled": true }
            """);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.SearchAsync(new KnowledgeQuery("   ")));
    }
}
