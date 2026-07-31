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
        // (This particular fixture is two byte-identical bundle copies, so
        // every score ties and the assertion would hold even under grouped
        // order -- see Scores_genuinely_interleave_across_sources_not_just_within_one
        // below for a fixture where it wouldn't.)
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
    public async Task Scores_genuinely_interleave_across_sources_not_just_within_one()
    {
        using var root = new TempDir();

        // Four distinct, hand-controlled scores (verified against
        // ConceptSearch.ScoreConcept's presence-based title x3/description
        // x2/body x1 weighting) spread across two equal-priority sources so
        // that neither GROUPED order (a's two passages, then b's two, in
        // either priority direction) nor any accidental non-sort would
        // satisfy "scores never increase" -- unlike the byte-identical
        // fixture above, where every tie makes that assertion trivially true
        // regardless of whether ranking is actually implemented.
        root.Write(Path.Combine("a", "a1.md"),
            "---\ntype: Note\ntitle: Orders update\ndescription: orders processed\n---\nGeneral update, no further mention.\n");
        root.Write(Path.Combine("a", "a2.md"),
            "---\ntype: Note\ntitle: Unrelated\ndescription: nothing here\n---\nA quick note about orders today.\n");
        root.Write(Path.Combine("b", "b1.md"),
            "---\ntype: Note\ntitle: Orders orders orders\ndescription: orders orders\n---\nOrders mentioned here too.\n");
        root.Write(Path.Combine("b", "b2.md"),
            "---\ntype: Note\ntitle: Nothing special\ndescription: orders noted\n---\nNo further mention.\n");

        using var catalog = BuildCatalog(root, """
            { "id": "a", "path": "./a", "priority": 1, "enabled": true },
            { "id": "b", "path": "./b", "priority": 1, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        // b1=6 (title+description+body), a1=5 (title+description), b2=2
        // (description only), a2=1 (body only) -- four distinct scores that
        // interleave b, a, b, a. A grouped-by-source implementation could
        // never produce this order (it would emit all of one source before
        // the other), so this genuinely distinguishes "ranked by score" from
        // "concatenated by source," rather than relying on ties to pass.
        Assert.Equal(
            new[] { "b1", "a1", "b2", "a2" },
            context.Passages.Select(p => p.ConceptId).ToArray());
        Assert.Equal(
            new[] { 6, 5, 2, 1 },
            context.Passages.Select(p => p.Score).ToArray());
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
        // eliminated entry contributes nothing at all -- but is reported,
        // not silently dropped.
        Assert.All(context.Passages, p => Assert.Equal("primary", p.SourceId));
        var duplicate = Assert.Single(context.Diagnostics, d => d.SourceId == "alias");
        Assert.Equal(KnowledgeDiagnosticCode.DuplicateDirectory, duplicate.Code);
        Assert.Contains("primary", duplicate.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Pins the deliberate trade-off documented on
    /// <see cref="CatalogPathResolver.PathComparison"/>: an unconditional
    /// <c>Ordinal</c> comparison means two source paths that differ only in
    /// case are never treated as a false duplicate, even on a
    /// case-insensitive host where they resolve to the very same physical
    /// directory (the opposite failure from the OS-heuristic this replaced,
    /// which could wrongly collapse two genuinely different directories on a
    /// case-sensitive volume). This test passes identically regardless of
    /// the actual host's case-sensitivity -- it asserts the DuplicateDirectory
    /// diagnostic is never falsely raised for a case-variant pair, not
    /// anything about how many passages come back (that part is host- and
    /// case-sensitivity-dependent, and not the property under test here).
    /// </summary>
    [Fact]
    public async Task Case_variant_source_paths_are_never_reported_as_a_false_duplicate()
    {
        using var root = new TempDir();
        CopyDirectory(BundlePath, Path.Combine(root.Path, "Shared"));

        // On a case-sensitive filesystem, "Shared" and "shared" are two
        // distinct directories that must both physically exist for the
        // catalog to resolve either source path at all -- on a
        // case-insensitive host, Directory.Exists already reports the
        // lowercase spelling as present (it's the same physical directory),
        // so this is skipped there rather than double-copying into it.
        if (!Directory.Exists(Path.Combine(root.Path, "shared")))
        {
            CopyDirectory(BundlePath, Path.Combine(root.Path, "shared"));
        }

        using var catalog = BuildCatalog(root, """
            { "id": "upper", "path": "./Shared", "priority": 10, "enabled": true },
            { "id": "lower", "path": "./shared", "priority": 1, "enabled": true }
            """);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        Assert.DoesNotContain(context.Diagnostics, d => d.Code == KnowledgeDiagnosticCode.DuplicateDirectory);
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
    public async Task PermittedSourceIds_that_excludes_every_enabled_source_still_returns_NoEnabledSources_but_a_visibility_message()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders")
        {
            PermittedSourceIds = new HashSet<string> { "does-not-exist" },
        });

        Assert.Empty(context.Passages);
        var diagnostic = Assert.Single(context.Diagnostics);

        // Same diagnostic code as "genuinely nothing configured" -- the plan
        // deliberately reuses NoEnabledSources rather than minting a new
        // code for a visibility-narrowed-to-zero result -- but the message
        // must point at visibility filtering, not catalog.json, since two
        // sources genuinely are enabled here.
        Assert.Equal(KnowledgeDiagnosticCode.NoEnabledSources, diagnostic.Code);
        Assert.Contains("visib", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("configured", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void SearchAsync_throws_synchronously_for_a_null_query()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        // Assert.Throws (never ThrowsAsync) only passes if the exception is
        // thrown while this delegate is RUNNING, not deferred into a faulted
        // ValueTask -- proves this resolver's validation is synchronous.
        Assert.Throws<ArgumentNullException>(() => resolver.SearchAsync(null!));
    }

    [Fact]
    public async Task SearchAsync_rejects_an_undefined_ResolverStrategy()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            async () => await resolver.SearchAsync(new KnowledgeQuery("orders") { ResolverStrategy = (KnowledgeResolverStrategy)99 }));

        Assert.Contains("KnowledgeResolverStrategy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_rejects_both_PermittedSourceIds_and_SourceVisibilityPolicy_set_together()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var ex = await Assert.ThrowsAsync<ArgumentException>(async () => await resolver.SearchAsync(new KnowledgeQuery("orders")
        {
            PermittedSourceIds = new HashSet<string> { "hi" },
            SourceVisibilityPolicy = (_, _) => true,
        }));

        Assert.Contains("PermittedSourceIds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_with_PermittedSourceIds_only_searches_the_named_source()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales")
        {
            PermittedSourceIds = new HashSet<string> { "lo" },
        });

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("lo", p.SourceId));
    }

    [Fact]
    public async Task SearchAsync_with_a_constructor_default_policy_applies_it_when_the_query_sets_neither_field()
    {
        using var root = new TempDir();
        using var catalog = SetUpTwoSourceCatalog(root);
        var resolver = new MergedKnowledgeResolver(catalog, defaultSourceVisibilityPolicy: (_, source) => source.Id == "hi");

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        Assert.NotEmpty(context.Passages);
        Assert.All(context.Passages, p => Assert.Equal("hi", p.SourceId));
    }
}
