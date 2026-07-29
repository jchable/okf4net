// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="MergedKnowledgeResolver"/>: one cross-source ranking by
/// descending score (priority as tie-break only), source-level dedup of two
/// manifest entries resolving to the same directory, and the shared
/// never-throw/errors-as-data contract inherited from the fused engine.
/// Exercised over <see cref="TempDir"/> copies of the
/// <c>tests/fixtures/appendix_a</c> bundle, never touching
/// <c>tests/fixtures</c> directly.
/// </summary>
public class MergedKnowledgeResolverTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

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

    /// <summary>Two fixture copies as two distinct sources: "hi" (priority 10) and "lo" (priority 1).</summary>
    private static FileKnowledgeCatalog SetUpTwoSourceCatalog(TempDir root)
    {
        CopyDirectory(BundlePath, Path.Combine(root.Path, "source-hi"));
        CopyDirectory(BundlePath, Path.Combine(root.Path, "source-lo"));

        return BuildCatalog(root, """
            { "id": "lo", "path": "./source-lo", "priority": 1, "enabled": true },
            { "id": "hi", "path": "./source-hi", "priority": 10, "enabled": true }
            """);
    }

    [Fact]
    public async Task Passages_are_ranked_by_descending_score_across_all_sources()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        Assert.Empty(context.Diagnostics);
        Assert.NotEmpty(context.Passages);

        // The defining property of a merged ranking: scores never increase.
        var scores = context.Passages.Select(p => p.Score).ToList();
        Assert.Equal(scores.OrderByDescending(s => s).ToList(), scores);

        // Both sources contribute. (They are identical fixture copies, so
        // every score ties and priority orders each tie -- the ordering that
        // actually distinguishes merged from grouped is asserted by
        // PriorityWeightedKnowledgeResolverTests, which uses a catalog whose
        // score and priority orders genuinely disagree.)
        Assert.Contains(context.Passages, p => p.SourceId == "hi");
        Assert.Contains(context.Passages, p => p.SourceId == "lo");
    }

    [Fact]
    public async Task Priority_breaks_ties_between_equal_scores()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        // The two sources are byte-identical fixture copies, so for every
        // score the higher-priority source's passage must come first.
        foreach (var group in context.Passages.GroupBy(p => p.Score))
        {
            var ids = group.Select(p => p.SourceId).ToList();
            var firstLo = ids.IndexOf("lo");
            var lastHi = ids.LastIndexOf("hi");
            if (firstLo >= 0 && lastHi >= 0)
            {
                Assert.True(lastHi < firstLo, $"score {group.Key}: 'hi' must precede 'lo'");
            }
        }
    }

    [Fact]
    public async Task Two_source_entries_resolving_to_the_same_directory_are_searched_once()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "shared"));

        // Two ids, two different relative spellings, ONE resolved directory.
        using var catalog = BuildCatalog(root, """
            { "id": "alias", "path": "./shared/../shared", "priority": 1, "enabled": true },
            { "id": "primary", "path": "./shared", "priority": 10, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        // Every concept appears exactly once...
        var ids = context.Passages.Select(p => p.ConceptId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        // ...attributed to the surviving (higher-priority) source, and the
        // eliminated entry contributes nothing at all.
        Assert.All(context.Passages, p => Assert.Equal("primary", p.SourceId));
        Assert.DoesNotContain(context.Diagnostics, d => d.SourceId == "alias");
    }

    [Fact]
    public async Task The_same_ConceptId_in_two_different_directories_is_never_deduped()
    {
        using var root = new TempDir();

        // Two genuinely distinct bundles that happen to share a concept id.
        root.Write(Path.Combine("a", "shared.md"), "---\ntype: Note\ntitle: Alpha\ndescription: d\n---\nOrders alpha.\n");
        root.Write(Path.Combine("b", "shared.md"), "---\ntype: Note\ntitle: Beta\ndescription: d\n---\nOrders beta.\n");

        using var catalog = BuildCatalog(root, """
            { "id": "a", "path": "./a", "priority": 1, "enabled": true },
            { "id": "b", "path": "./b", "priority": 2, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        // Same ConceptId, unrelated content: BOTH must survive. Collapsing
        // these would silently hide one bundle's concept behind another's.
        Assert.Equal(2, context.Passages.Count);
        Assert.All(context.Passages, p => Assert.Equal("shared", p.ConceptId));
        Assert.Equal(
            new[] { "a", "b" },
            context.Passages.Select(p => p.SourceId).OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Stale_passages_are_filtered_by_the_query_policy()
    {
        using var root = new TempDir();
        root.Write(Path.Combine("source", "old.md"),
            "---\ntype: Metric\ntitle: Churn cohort\ndescription: d\nstale_after: 2026-01-01\n---\nChurn cohort.\n");
        using var catalog = BuildCatalog(root, """
            { "id": "s1", "path": "./source", "priority": 1, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog, new FixedClock(new DateOnly(2026, 7, 27)));

        var strict = await resolver.SearchAsync(new KnowledgeQuery("churn") { StalePolicy = StalePolicy.Strict });
        Assert.Empty(strict.Passages);

        var used = await resolver.SearchAsync(new KnowledgeQuery("churn"));
        Assert.Single(used.Passages);
    }

    [Fact]
    public async Task An_unresolvable_source_yields_a_diagnostic_and_the_others_still_search()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "good"));
        CopyDirectory(BundlePath, Path.Combine(root.Path, "gone"));
        using var catalog = BuildCatalog(root, """
            { "id": "good", "path": "./good", "priority": 1, "enabled": true },
            { "id": "gone", "path": "./gone", "priority": 2, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        // The catalog already validated both source paths at construction
        // (generation 1); delete one afterward so the engine's own
        // re-resolution (not the catalog's load-time validation) is what
        // observes the failure -- mirroring
        // GroupedKnowledgeResolverTests.SearchAsync_reports_SourceUnavailable_for_a_deleted_source_but_still_returns_the_other.
        Directory.Delete(Path.Combine(root.Path, "gone"), recursive: true);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("good", p.SourceId));
        var diagnostic = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, diagnostic.Code);
        Assert.Equal("gone", diagnostic.SourceId);
    }

    [Fact]
    public async Task No_enabled_sources_is_reported_as_data()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "off"));
        using var catalog = BuildCatalog(root, """
            { "id": "off", "path": "./off", "priority": 1, "enabled": false }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Empty(context.Passages);
        var diagnostic = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.NoEnabledSources, diagnostic.Code);
    }

    [Fact]
    public async Task No_matches_is_reported_as_data()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "src"));
        using var catalog = BuildCatalog(root, """
            { "id": "src", "path": "./src", "priority": 1, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("zzzznotpresentanywhere"));

        Assert.Empty(context.Passages);
        var diagnostic = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.NoMatches, diagnostic.Code);
    }

    [Fact]
    public async Task A_blank_query_text_throws()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "src"));
        using var catalog = BuildCatalog(root, """
            { "id": "src", "path": "./src", "priority": 1, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.SearchAsync(new KnowledgeQuery("   ")));
    }

    [Fact]
    public async Task The_catalog_generation_is_stamped_on_the_result()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(catalog.Current.Generation, context.CatalogGeneration);
    }
}
