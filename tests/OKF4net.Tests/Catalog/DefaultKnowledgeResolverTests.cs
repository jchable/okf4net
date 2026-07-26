// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="DefaultKnowledgeResolver"/>: multi-source fan-out grouped by
/// descending source priority (no cross-source fusion), per-source failure
/// isolation, <see cref="KnowledgeDiagnosticCode.NoEnabledSources"/>/
/// <see cref="KnowledgeDiagnosticCode.NoMatches"/> as data, the blank-query
/// contract, and <see cref="KnowledgeContext.CatalogGeneration"/> stamping.
/// Exercised over two <see cref="TempDir"/> copies of the
/// <c>tests/fixtures/appendix_a</c> bundle registered as two catalog
/// sources, never touching <c>tests/fixtures</c> directly.
/// </summary>
public class DefaultKnowledgeResolverTests
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

    /// <summary>
    /// Sets up a catalog root with two fixture-copy sources ("hi", priority
    /// 10; "lo", priority 1), both enabled by default, and returns the
    /// constructed catalog plus the two sources' resolved directories.
    /// </summary>
    private static FileKnowledgeCatalog SetUpTwoSourceCatalog(TempDir root, bool hiEnabled = true, bool loEnabled = true)
    {
        CopyDirectory(BundlePath, Path.Combine(root.Path, "source-hi"));
        CopyDirectory(BundlePath, Path.Combine(root.Path, "source-lo"));

        var json = $$"""
            {
              "version": 1,
              "sources": [
                { "id": "lo", "path": "./source-lo", "priority": 1, "enabled": {{(loEnabled ? "true" : "false")}} },
                { "id": "hi", "path": "./source-hi", "priority": 10, "enabled": {{(hiEnabled ? "true" : "false")}} }
              ]
            }
            """;
        root.Write("catalog.json", json);

        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
    }

    // ---- (a) grouped by priority, tagged with source id ------------------

    [Fact]
    public async Task SearchAsync_concatenates_passages_grouped_by_descending_priority()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new DefaultKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        Assert.Empty(context.Diagnostics);
        Assert.NotEmpty(context.Passages);

        var hiBundle = Bundle.Load(Path.Combine(root.Path, "source-hi"));
        var expectedHiCount = ConceptSearch.Search(hiBundle.Concepts, "orders sales").Count;
        var loBundle = Bundle.Load(Path.Combine(root.Path, "source-lo"));
        var expectedLoCount = ConceptSearch.Search(loBundle.Concepts, "orders sales").Count;

        Assert.Equal(expectedHiCount + expectedLoCount, context.Passages.Count);

        // Grouped: every "hi" passage precedes every "lo" passage.
        var lastHiIndex = -1;
        var firstLoIndex = int.MaxValue;
        for (var i = 0; i < context.Passages.Count; i++)
        {
            if (context.Passages[i].SourceId == "hi")
            {
                lastHiIndex = i;
            }
            else if (context.Passages[i].SourceId == "lo" && i < firstLoIndex)
            {
                firstLoIndex = i;
            }
        }

        Assert.True(lastHiIndex < firstLoIndex, "higher-priority source's passages must all precede the lower-priority source's");
        Assert.Equal(expectedHiCount, context.Passages.Count(p => p.SourceId == "hi"));
        Assert.Equal(expectedLoCount, context.Passages.Count(p => p.SourceId == "lo"));
    }

    // ---- (b) failure isolation --------------------------------------------

    [Fact]
    public async Task SearchAsync_reports_SourceUnavailable_for_a_deleted_source_but_still_returns_the_other()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new DefaultKnowledgeResolver(catalog);

        // The catalog already validated both source paths at construction
        // (generation 1); delete one afterward so the resolver's own
        // re-resolution (not the catalog's load-time validation) is what
        // observes the failure.
        Directory.Delete(Path.Combine(root.Path, "source-lo"), recursive: true);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        var diagnostic = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, diagnostic.Code);
        Assert.Equal("lo", diagnostic.SourceId);

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("hi", p.SourceId));
    }

    // ---- (c) per-source order + score parity with the core scorer --------

    [Fact]
    public async Task SearchAsync_per_source_order_and_scores_match_ConceptSearch_directly()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new DefaultKnowledgeResolver(catalog);
        const string queryText = "orders sales";

        var context = await resolver.SearchAsync(new KnowledgeQuery(queryText));

        var hiBundle = Bundle.Load(Path.Combine(root.Path, "source-hi"));
        var expectedHi = ConceptSearch.Search(hiBundle.Concepts, queryText);
        var loBundle = Bundle.Load(Path.Combine(root.Path, "source-lo"));
        var expectedLo = ConceptSearch.Search(loBundle.Concepts, queryText);

        var actualHi = context.Passages.Where(p => p.SourceId == "hi").ToList();
        var actualLo = context.Passages.Where(p => p.SourceId == "lo").ToList();

        Assert.Equal(expectedHi.Count, actualHi.Count);
        for (var i = 0; i < expectedHi.Count; i++)
        {
            Assert.Equal(expectedHi[i].Concept.Id.ToString(), actualHi[i].ConceptId);
            Assert.Equal(expectedHi[i].Score, actualHi[i].Score);
        }

        Assert.Equal(expectedLo.Count, actualLo.Count);
        for (var i = 0; i < expectedLo.Count; i++)
        {
            Assert.Equal(expectedLo[i].Concept.Id.ToString(), actualLo[i].ConceptId);
            Assert.Equal(expectedLo[i].Score, actualLo[i].Score);
        }
    }

    // ---- (d) NoEnabledSources / NoMatches as data --------------------------

    [Fact]
    public async Task SearchAsync_with_no_enabled_sources_returns_NoEnabledSources_diagnostic()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root, hiEnabled: false, loEnabled: false);
        var resolver = new DefaultKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Empty(context.Passages);
        var diagnostic = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.NoEnabledSources, diagnostic.Code);
        Assert.Null(diagnostic.SourceId);
    }

    [Fact]
    public async Task SearchAsync_with_no_matches_across_all_sources_returns_NoMatches_diagnostic()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new DefaultKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("zzz-nonexistent-term"));

        Assert.Empty(context.Passages);
        var diagnostic = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.NoMatches, diagnostic.Code);
        Assert.Null(diagnostic.SourceId);
    }

    // ---- (e) blank query.Text is a caller error ----------------------------

    [Fact]
    public async Task SearchAsync_with_blank_query_text_throws_ArgumentException()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new DefaultKnowledgeResolver(catalog);

        await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.SearchAsync(new KnowledgeQuery("   ")));
        await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.SearchAsync(new KnowledgeQuery(string.Empty)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.SearchAsync(new KnowledgeQuery(null!)));
    }

    // ---- (f) CatalogGeneration stamping -------------------------------------

    [Fact]
    public async Task SearchAsync_stamps_CatalogGeneration_from_the_current_snapshot()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new DefaultKnowledgeResolver(catalog);

        var beforeReload = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(1, beforeReload.CatalogGeneration);

        await catalog.ReloadAsync();

        var afterReload = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(2, afterReload.CatalogGeneration);
    }

    // ---- Query is echoed back verbatim --------------------------------------

    [Fact]
    public async Task SearchAsync_echoes_the_query_back_on_the_context()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new DefaultKnowledgeResolver(catalog);
        var query = new KnowledgeQuery("orders", "sales");

        var context = await resolver.SearchAsync(query);

        Assert.Equal(query, context.Query);
    }
}
