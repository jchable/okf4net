// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="KnowledgeResolverRouter"/>: dispatches each search to the
/// strategy named by the query, falling back to the configured default, so
/// the single injected <see cref="IKnowledgeResolver"/> every consumer
/// already depends on gains per-call strategy selection without any of them
/// changing.
/// </summary>
public class KnowledgeResolverRouterTests
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
    /// A low-priority source matching strongly and a high-priority source
    /// matching weakly -- the three strategies order this catalog
    /// differently, which is how each test tells them apart.
    /// </summary>
    private static FileKnowledgeCatalog SetUpDistinguishingCatalog(TempDir root)
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
    public async Task The_default_strategy_is_grouped_by_source()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog);

        var viaRouter = await router.SearchAsync(new KnowledgeQuery("orders"));
        var viaGrouped = await new GroupedKnowledgeResolver(catalog).SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(
            viaGrouped.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}"),
            viaRouter.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}"));
    }

    [Fact]
    public async Task A_query_strategy_overrides_the_default()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog); // default: GroupedBySource

        var merged = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.Merged });

        // Merged ranks by raw score, so the strong-but-low-priority source wins.
        Assert.Equal("strong-lo", merged.Passages[0].SourceId);
    }

    [Fact]
    public async Task The_configured_default_applies_when_the_query_names_none()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog, KnowledgeResolverStrategy.Merged);

        var context = await router.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal("strong-lo", context.Passages[0].SourceId);
    }

    [Fact]
    public async Task Each_strategy_is_reachable_by_name()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog);

        var merged = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.Merged });
        var weighted = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.PriorityWeighted });
        var grouped = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.GroupedBySource });

        Assert.Equal("strong-lo", merged.Passages[0].SourceId);
        Assert.Equal("weak-hi", weighted.Passages[0].SourceId);
        Assert.Equal("weak-hi", grouped.Passages[0].SourceId); // grouped leads with the highest-priority source
    }

    /// <summary>
    /// Two sources ("a" and "b") sharing priority 1 alongside a third ("hi")
    /// at priority 10 -- chosen so <see cref="GroupedKnowledgeResolver"/> and
    /// <see cref="PriorityWeightedKnowledgeResolver"/> only genuinely diverge
    /// when two enabled sources share a priority tier: Grouped still emits
    /// one source's whole block before the other's within that tier, while
    /// PriorityWeighted interleaves the tier by score. Source "a" carries two
    /// passages (scores 6 and 1) and source "b" one (score 3), so within the
    /// priority-1 tier the score order is a(6), b(3), a(1) -- genuinely
    /// interleaved across sources, not just two single-passage blocks in a
    /// different order. "hi" (score 4) sits strictly between "a"'s two scores,
    /// so it also separates <see cref="MergedKnowledgeResolver"/> (pure score
    /// order, ignores the tier boundary) from the other two (which both place
    /// the sole priority-10 passage first).
    /// </summary>
    private static FileKnowledgeCatalog SetUpEqualPriorityTierCatalog(TempDir root)
    {
        // Score 4: title + body match "orders", description does not.
        root.Write(Path.Combine("hi", "note.md"),
            "---\ntype: Note\ntitle: Customer orders dashboard\ndescription: Internal dashboard notes\n---\nThis page discusses orders processing.\n");

        // Score 6: title + description + body all match.
        root.Write(Path.Combine("a", "strong.md"),
            "---\ntype: Note\ntitle: Orders orders orders\ndescription: orders\n---\nOrders everywhere orders.\n");

        // Score 1: body only.
        root.Write(Path.Combine("a", "weak.md"),
            "---\ntype: Note\ntitle: Unrelated heading\ndescription: d\n---\nA passing mention of orders.\n");

        // Score 3: description + body match, title does not.
        root.Write(Path.Combine("b", "medium.md"),
            "---\ntype: Note\ntitle: Unrelated title\ndescription: orders backlog\n---\nTrack orders in the backlog.\n");

        return BuildCatalog(root, """
            { "id": "hi", "path": "./hi", "priority": 10, "enabled": true },
            { "id": "a", "path": "./a", "priority": 1, "enabled": true },
            { "id": "b", "path": "./b", "priority": 1, "enabled": true }
            """);
    }

    [Fact]
    public async Task Grouped_and_PriorityWeighted_diverge_within_a_shared_priority_tier()
    {
        using var root = new TempDir();
        using var catalog = SetUpEqualPriorityTierCatalog(root);
        var router = new KnowledgeResolverRouter(catalog);

        var grouped = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.GroupedBySource });
        var weighted = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.PriorityWeighted });
        var merged = await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = KnowledgeResolverStrategy.Merged });

        // Grouped: "hi" block (priority 10), then "a"'s whole block (its own
        // two passages score-ordered: 6, 1), then "b"'s block (score 3) --
        // the tier is NOT interleaved by score.
        Assert.Equal(
            new[] { "hi/note", "a/strong", "a/weak", "b/medium" },
            grouped.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}"));

        // PriorityWeighted: "hi" still leads (higher priority tier), but
        // within the priority-1 tier "a" and "b" interleave by score:
        // a(6), b(3), a(1) -- "b/medium" now falls between "a"'s two passages.
        Assert.Equal(
            new[] { "hi/note", "a/strong", "b/medium", "a/weak" },
            weighted.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}"));

        // Merged: pure score order regardless of priority tier, so "a/strong"
        // (score 6) leads even over "hi/note" (score 4, but priority 10).
        Assert.Equal(
            new[] { "a/strong", "hi/note", "b/medium", "a/weak" },
            merged.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}"));
    }

    [Fact]
    public async Task The_default_fairness_quota_reaches_the_fused_strategies()
    {
        using var root = new TempDir();
        for (var i = 0; i < 4; i++)
        {
            root.Write(Path.Combine("big", $"b{i}.md"),
                $"---\ntype: Note\ntitle: Orders orders {i}\ndescription: orders\n---\nOrders orders.\n");
        }

        root.Write(Path.Combine("small", "s0.md"),
            "---\ntype: Note\ntitle: Unrelated\ndescription: d\n---\nOne mention of orders.\n");

        using var catalog = BuildCatalog(root, """
            { "id": "big", "path": "./big", "priority": 1, "enabled": true },
            { "id": "small", "path": "./small", "priority": 1, "enabled": true }
            """);
        var router = new KnowledgeResolverRouter(catalog, KnowledgeResolverStrategy.Merged, defaultFairnessQuota: 1);

        var context = await router.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal("small", context.Passages[1].SourceId);
    }

    [Fact]
    public async Task A_blank_query_text_throws_whichever_strategy_is_selected()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await router.SearchAsync(new KnowledgeQuery("  ") { ResolverStrategy = KnowledgeResolverStrategy.Merged }));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await router.SearchAsync(new KnowledgeQuery("  ") { ResolverStrategy = KnowledgeResolverStrategy.GroupedBySource }));
    }

    [Fact]
    public void An_undefined_default_strategy_is_rejected_at_construction()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);

        var ex = Assert.Throws<ArgumentException>(
            () => new KnowledgeResolverRouter(catalog, (KnowledgeResolverStrategy)99));

        Assert.Equal("defaultStrategy", ex.ParamName);
    }

    [Fact]
    public async Task An_undefined_query_strategy_is_rejected_rather_than_silently_falling_back_to_grouped()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog);

        // The defect this closes: before it, an undefined ResolverStrategy
        // value fell through the switch's default arm straight to
        // GroupedBySource, with no error at all.
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            async () => await router.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = (KnowledgeResolverStrategy)99 }));

        Assert.Contains("KnowledgeResolverStrategy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchAsync_throws_synchronously_for_a_null_query()
    {
        using var root = new TempDir();
        using var catalog = SetUpDistinguishingCatalog(root);
        var router = new KnowledgeResolverRouter(catalog);

        Assert.Throws<ArgumentNullException>(() => router.SearchAsync(null!));
    }
}
