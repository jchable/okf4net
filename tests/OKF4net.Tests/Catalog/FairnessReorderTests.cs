// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// The fused strategies' opt-in fairness reordering: no source contributes
/// more than the quota's worth of CONSECUTIVE passages while another source
/// still has passages left, and nothing is ever dropped. Built on a catalog
/// where one source deliberately outnumbers the other, since that is the only
/// shape where the quota changes anything.
/// </summary>
public class FairnessReorderTests
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
    /// "big" holds 5 matching concepts scoring higher than "small"'s 2, so
    /// unfair (pure score) order drains all of "big" before "small" appears.
    /// </summary>
    private static FileKnowledgeCatalog SetUpLopsidedCatalog(TempDir root)
    {
        for (var i = 0; i < 5; i++)
        {
            root.Write(Path.Combine("big", $"b{i}.md"),
                $"---\ntype: Note\ntitle: Orders orders {i}\ndescription: orders\n---\nOrders orders.\n");
        }

        for (var i = 0; i < 2; i++)
        {
            root.Write(Path.Combine("small", $"s{i}.md"),
                $"---\ntype: Note\ntitle: Unrelated {i}\ndescription: d\n---\nOne mention of orders.\n");
        }

        return BuildCatalog(root, """
            { "id": "big", "path": "./big", "priority": 1, "enabled": true },
            { "id": "small", "path": "./small", "priority": 1, "enabled": true }
            """);
    }

    /// <summary>The length of the longest run of consecutive same-source passages.</summary>
    private static int LongestRun(IReadOnlyList<KnowledgePassage> passages)
    {
        var longest = 0;
        var current = 0;
        string? previous = null;

        foreach (var p in passages)
        {
            current = p.SourceId == previous ? current + 1 : 1;
            previous = p.SourceId;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    [Fact]
    public async Task Without_a_quota_one_source_can_monopolize_the_head_of_the_result()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        // The baseline this whole feature exists to fix: a caller truncating
        // after 5 passages would never see "small" at all.
        Assert.Equal(7, context.Passages.Count);
        Assert.All(context.Passages.Take(5), p => Assert.Equal("big", p.SourceId));
    }

    [Fact]
    public async Task A_quota_of_two_breaks_up_the_monopoly()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 2 });

        // "small" now appears within the first 3, so an early-truncating
        // caller sees both sources.
        Assert.Contains(context.Passages.Take(3), p => p.SourceId == "small");
    }

    [Fact]
    public async Task A_quota_never_drops_a_passage()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var unfair = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        var fair = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 1 });

        // Same multiset, different order -- reordering only, no filtering.
        Assert.Equal(
            unfair.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}").OrderBy(s => s, StringComparer.Ordinal),
            fair.Passages.Select(p => $"{p.SourceId}/{p.ConceptId}").OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_quota_is_honored_until_the_smaller_source_runs_out()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 1 });

        // With quota 1 and 5-vs-2 passages, the best possible interleave is
        // big, small, big, small, big, big, big -- so the only run longer
        // than 1 is the unavoidable tail after "small" is exhausted.
        var tail = context.Passages.Skip(4).ToList();
        Assert.All(tail, p => Assert.Equal("big", p.SourceId));
        Assert.Equal(3, LongestRun(tail));

        var head = context.Passages.Take(4).ToList();
        Assert.Equal(1, LongestRun(head));
    }

    [Fact]
    public async Task A_quota_applies_to_the_priority_weighted_strategy_too()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new PriorityWeightedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 1 });

        Assert.Equal(7, context.Passages.Count);
        Assert.Equal(1, LongestRun(context.Passages.Take(4).ToList()));
    }

    [Fact]
    public async Task A_constructor_default_quota_applies_when_the_query_sets_none()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog, clock: null, defaultFairnessQuota: 1);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(1, LongestRun(context.Passages.Take(4).ToList()));
    }

    [Fact]
    public async Task A_query_quota_overrides_the_constructor_default()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog, clock: null, defaultFairnessQuota: 1);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 5 });

        // Quota 5 is large enough that "big"'s whole run fits, so the result
        // is the unfair order again -- proving the query value won.
        Assert.All(context.Passages.Take(5), p => Assert.Equal("big", p.SourceId));
    }

    [Fact]
    public async Task A_non_positive_quota_is_rejected()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = -1 }));
    }

    [Fact]
    public async Task A_non_positive_quota_is_rejected_by_the_grouped_strategy_too()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);
        var resolver = new GroupedKnowledgeResolver(catalog);

        // GroupedBySource never uses a quota, but the SAME malformed query
        // must fail the SAME way whichever strategy happens to run it --
        // otherwise a caller's typo surfaces or hides depending on a host
        // default they may not even know about.
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 0 }));
    }

    [Fact]
    public void A_non_positive_constructor_default_quota_is_rejected_at_construction()
    {
        using var root = new TempDir();
        using var catalog = SetUpLopsidedCatalog(root);

        // Fail at construction, not on the first search: a misconfigured
        // default is a wiring mistake, and every later search would raise
        // the identical error anyway.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MergedKnowledgeResolver(catalog, clock: null, defaultFairnessQuota: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PriorityWeightedKnowledgeResolver(catalog, clock: null, defaultFairnessQuota: -1));
    }

    [Fact]
    public async Task A_single_source_result_is_unaffected_by_a_quota()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("only", "a.md"), "---\ntype: Note\ntitle: Orders a\ndescription: orders\n---\nOrders.\n");
        root.Write(Path.Combine("only", "b.md"), "---\ntype: Note\ntitle: Orders b\ndescription: orders\n---\nOrders.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "only", "path": "./only", "priority": 1, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var unfair = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        var fair = await resolver.SearchAsync(new KnowledgeQuery("orders") { FairnessQuota = 1 });

        // No alternative source exists, so the quota cannot be honored and
        // the algorithm simply drains the one source in ranked order.
        Assert.Equal(
            unfair.Passages.Select(p => p.ConceptId),
            fair.Passages.Select(p => p.ConceptId));
    }
}
