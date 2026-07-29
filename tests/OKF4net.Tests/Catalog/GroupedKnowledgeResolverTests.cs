// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="GroupedKnowledgeResolver"/>: multi-source fan-out grouped by
/// descending source priority (no cross-source fusion), per-source failure
/// isolation, <see cref="KnowledgeDiagnosticCode.NoEnabledSources"/>/
/// <see cref="KnowledgeDiagnosticCode.NoMatches"/> as data, the blank-query
/// contract, and <see cref="KnowledgeContext.CatalogGeneration"/> stamping.
/// Exercised over two <see cref="TempDir"/> copies of the
/// <c>tests/fixtures/appendix_a</c> bundle registered as two catalog
/// sources, never touching <c>tests/fixtures</c> directly.
/// </summary>
public class GroupedKnowledgeResolverTests
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

    /// <summary>
    /// Sets up a single-source catalog root whose one source directory
    /// contains a single concept file named <paramref name="fileName"/> with
    /// raw <paramref name="content"/> (frontmatter + body) -- for tests that
    /// only need one concept's lifecycle behaviour rather than the full
    /// <c>appendix_a</c> fixture. Returns the constructed catalog plus its
    /// backing <see cref="TempDir"/> (kept alive for the test's <c>using</c>
    /// scope; the catalog reads from it lazily on every search).
    /// </summary>
    private static (FileKnowledgeCatalog Catalog, TempDir Root) BuildCatalogWithConcept(string fileName, string content)
    {
        var root = new TempDir();
        root.Write(Path.Combine("source", fileName), content);

        var json = """
            {
              "version": 1,
              "sources": [
                { "id": "s1", "path": "./source", "priority": 1, "enabled": true }
              ]
            }
            """;
        root.Write("catalog.json", json);

        var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });

        return (catalog, root);
    }

    // ---- (g) StalePolicy filters passages by lifecycle ---------------------

    [Fact]
    public async Task Strict_policy_filters_out_stale_passages()
    {
        // Arrange: a catalog with one bundle containing one stale concept.
        var (catalog, root) = BuildCatalogWithConcept(
            "old.md",
            "---\ntype: Metric\ntitle: Churn cohort\ndescription: d\nstale_after: 2026-01-01\n---\nChurn cohort.\n");
        using var disposableRoot = root;
        using var disposableCatalog = catalog;
        var resolver = new GroupedKnowledgeResolver(catalog, new FixedClock(new DateOnly(2026, 7, 27)));

        var strict = await resolver.SearchAsync(new KnowledgeQuery("churn") { StalePolicy = StalePolicy.Strict });
        Assert.Empty(strict.Passages);

        var used = await resolver.SearchAsync(new KnowledgeQuery("churn")); // default Use
        Assert.Single(used.Passages);
    }

    // ---- (a) grouped by priority, tagged with source id ------------------

    [Fact]
    public async Task SearchAsync_concatenates_passages_grouped_by_descending_priority()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new GroupedKnowledgeResolver(catalog);

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

    // ---- (a2) role:memory sources are excluded from knowledge search ------

    [Fact]
    public async Task Memory_role_sources_are_not_searched()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "source-knowledge"));
        // The memory source also carries fixture content that matches the
        // query below -- if the resolver ever stopped filtering by role, its
        // passages would show up under SourceId "mem" too, making this a
        // genuine red/green test rather than one that passes trivially
        // because an empty directory yields zero passages either way.
        CopyDirectory(BundlePath, Path.Combine(root.Path, "source-memory"));

        var json = """
            {
              "version": 1,
              "sources": [
                { "id": "kb", "path": "./source-knowledge", "priority": 1, "enabled": true, "role": "knowledge" },
                { "id": "mem", "path": "./source-memory", "priority": 10, "enabled": true, "role": "memory", "tier": "user" }
              ]
            }
            """;
        root.Write("catalog.json", json);

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
        var resolver = new GroupedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("kb", p.SourceId));
        Assert.DoesNotContain(context.Passages, p => p.SourceId == "mem");
    }

    // ---- (b) failure isolation --------------------------------------------

    [Fact]
    public async Task SearchAsync_reports_SourceUnavailable_for_a_deleted_source_but_still_returns_the_other()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new GroupedKnowledgeResolver(catalog);

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
        var resolver = new GroupedKnowledgeResolver(catalog);
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
        var resolver = new GroupedKnowledgeResolver(catalog);

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
        var resolver = new GroupedKnowledgeResolver(catalog);

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
        var resolver = new GroupedKnowledgeResolver(catalog);

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
        var resolver = new GroupedKnowledgeResolver(catalog);

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
        var resolver = new GroupedKnowledgeResolver(catalog);
        var query = new KnowledgeQuery("orders", "sales");

        var context = await resolver.SearchAsync(query);

        Assert.Equal(query, context.Query);
    }

    // ---- Passages / Diagnostics are genuine read-only views (not just a List<T> hidden behind an interface) --

    [Fact]
    public async Task SearchAsync_Passages_cannot_be_downcast_to_a_mutable_list_and_mutated()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new GroupedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.NotEmpty(context.Passages);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (List<KnowledgePassage>)context.Passages;
            mutable.Clear();
        });

        Assert.IsType<InvalidCastException>(castAttempt);
    }

    [Fact]
    public async Task SearchAsync_Diagnostics_cannot_be_downcast_to_a_mutable_list_and_mutated()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        Directory.Delete(Path.Combine(root.Path, "source-lo"), recursive: true);
        var resolver = new GroupedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.NotEmpty(context.Diagnostics);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (List<KnowledgeDiagnostic>)context.Diagnostics;
            mutable.Clear();
        });

        Assert.IsType<InvalidCastException>(castAttempt);
    }

    [Fact]
    public async Task SearchAsync_NoEnabledSources_Diagnostics_cannot_be_downcast_to_a_mutable_array_and_mutated()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root, hiEnabled: false, loEnabled: false);
        var resolver = new GroupedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        var diagnostic = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.NoEnabledSources, diagnostic.Code);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (KnowledgeDiagnostic[])context.Diagnostics;
            mutable[0] = diagnostic;
        });

        Assert.IsType<InvalidCastException>(castAttempt);
    }
}
