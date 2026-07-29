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
}
