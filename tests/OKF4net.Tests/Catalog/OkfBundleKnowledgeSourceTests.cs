// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Catalog;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="OkfBundleKnowledgeSource"/>: one image served from a <see cref="BundleHandle"/>
/// under its refresh policy (spec §5.2, §9), mapping of <see cref="ScoredConcept"/> to
/// <see cref="KnowledgePassage"/>, scoring/ordering parity with the core
/// <see cref="ConceptSearch"/>, and never-throws behaviour on a bad bundle directory.
/// </summary>
public class OkfBundleKnowledgeSourceTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private const string Doc = "---\ntype: Note\ntitle: Orders\ndescription: d\n---\n\nOrders and refunds.\n";

    private static int Loads(InMemoryBundleProvider storage) => storage.Calls.Count(c => c == "enumerate ");

    // ---- The image comes from the handle (lot D) ---------------------------

    [Fact]
    public async Task Two_searches_over_an_unchanged_bundle_load_it_once_under_OnAccess()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("a.md", Doc);
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var source = new OkfBundleKnowledgeSource("k", handle);

        var first = await source.SearchAsync(new KnowledgeQuery("orders"));
        var second = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Single(first.Passages);
        Assert.Single(second.Passages);
        Assert.Equal(1, Loads(storage));
        Assert.Same(handle, source.Handle);
    }

    [Fact]
    public async Task A_change_in_storage_is_seen_at_the_next_search_under_OnAccess()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("a.md", Doc);
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var source = new OkfBundleKnowledgeSource("k", handle);

        Assert.Single((await source.SearchAsync(new KnowledgeQuery("orders"))).Passages);
        storage.Add("b.md", Doc);

        var after = await source.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(2, after.Passages.Count);
        Assert.Equal(2, Loads(storage));
    }

    [Fact]
    public async Task Under_Manual_the_image_is_served_until_the_host_refreshes()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("a.md", Doc);
        using var handle = new BundleHandle(storage);
        var source = new OkfBundleKnowledgeSource("k", handle);

        Assert.Single((await source.SearchAsync(new KnowledgeQuery("orders"))).Passages);
        storage.Add("b.md", Doc);
        Assert.Single((await source.SearchAsync(new KnowledgeQuery("orders"))).Passages);

        handle.Refresh();
        Assert.Equal(2, (await source.SearchAsync(new KnowledgeQuery("orders"))).Passages.Count);
    }

    [Fact]
    public async Task A_failed_reload_keeps_the_previous_image_served_and_a_failed_first_load_is_SourceUnavailable()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("a.md", Doc);
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var source = new OkfBundleKnowledgeSource("k", handle);
        Assert.Single((await source.SearchAsync(new KnowledgeQuery("orders"))).Passages);

        storage.Add("b.md", Doc);
        storage.FailEnumerateWith = new IOException("storage down");
        var stale = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Null(stale.Diagnostic);
        Assert.Single(stale.Passages);
        Assert.True(handle.IsStale);

        var broken = new InMemoryBundleProvider(id: "never") { FailEnumerateWith = new IOException("storage down") };
        using var neverLoaded = new BundleHandle(broken);
        var result = await new OkfBundleKnowledgeSource("never", neverLoaded).SearchAsync(new KnowledgeQuery("orders"));
        Assert.Empty(result.Passages);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, result.Diagnostic!.Code);
        Assert.Equal("never", result.Diagnostic.SourceId);
        Assert.Contains("bundle at 'memory://never' could not be loaded", result.Diagnostic.Message);
    }

    [Fact]
    public async Task A_failed_token_read_keeps_the_previous_image_served()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("a.md", Doc);
        using var handle = new BundleHandle(storage, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var source = new OkfBundleKnowledgeSource("k", handle);
        Assert.Single((await source.SearchAsync(new KnowledgeQuery("orders"))).Passages);

        storage.FailVersionWith = new IOException("token unreadable");
        var result = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Null(result.Diagnostic);
        Assert.Single(result.Passages);
        Assert.True(handle.IsStale);
    }

    [Fact]
    public void The_directory_constructor_builds_its_own_OnAccess_handle_over_the_filesystem()
    {
        using var tmp = new TempDir();
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        Assert.Equal(BundleRefreshMode.OnAccess, source.Handle.RefreshPolicy.Mode);
        var provider = Assert.IsType<FileSystemBundleProvider>(source.Handle.Provider);
        Assert.Equal(tmp.Path, provider.Root);
        Assert.Null(provider.ConfinementBoundary);
        Assert.NotSame(source.Handle, new OkfBundleKnowledgeSource("other", tmp.Path).Handle);
        Assert.Throws<ArgumentNullException>(() => new OkfBundleKnowledgeSource("x", (BundleHandle)null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a\0b")]
    public void The_directory_constructor_refuses_a_directory_that_is_null_empty_or_not_a_path(string? directory)
    {
        // A null or empty one is the provider's own guard; a string Path.GetFullPath rejects (an embedded NUL)
        // is the canonicalisation of the root: both surface as an ArgumentException at construction.
        Assert.ThrowsAny<ArgumentException>(() => new OkfBundleKnowledgeSource("x", directory!));
    }

    [Fact]
    public void BundleRelativePath_is_the_key_or_the_relativised_path_of_a_hand_built_concept()
    {
        var storage = new InMemoryBundleProvider(id: "k").Add("tables/orders.md", Doc);
        var bundle = Bundle.Load(storage);
        var loaded = Assert.Single(bundle.Concepts);
        Assert.Equal("tables/orders.md", OkfBundleKnowledgeSource.RelativePathOf(bundle, loaded));

        var handBuilt = new Concept(loaded.Id, Path.Combine(bundle.Location, "tables", "orders.md"), loaded.Document);
        Assert.Null(handBuilt.Key);
        Assert.Equal("tables/orders.md", OkfBundleKnowledgeSource.RelativePathOf(bundle, handBuilt));
    }

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

    [Fact]
    public async Task SearchAsync_maps_every_scored_concept_to_a_passage_carrying_the_source_id()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Null(result.Diagnostic);
        Assert.NotEmpty(result.Passages);
        Assert.All(result.Passages, p => Assert.Equal("mine", p.SourceId));
    }

    [Fact]
    public async Task SearchAsync_passage_fields_match_the_core_scorer_exactly()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);
        var query = new KnowledgeQuery("orders sales");

        var result = await source.SearchAsync(query);

        var bundle = Bundle.Load(tmp.Path);
        var expected = ConceptSearch.Search(bundle.Concepts, query.Text, query.Tag);

        Assert.Equal(expected.Count, result.Passages.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var p = result.Passages[i];
            Assert.Equal(e.Concept.Id.ToString(), p.ConceptId);
            Assert.Equal(e.Concept.Document.Frontmatter.Title, p.Title);
            Assert.Equal(e.Score, p.Score);
            Assert.Equal(ConceptSearch.Excerpt(e.Concept.Document.Body, query.Text) ?? string.Empty, p.Excerpt);
            Assert.Equal(e.Concept.Key!.ToString(), p.BundleRelativePath);
        }

        // The scorer's own contract: descending score, ties broken by ascending concept id --
        // assert the passage order itself is non-increasing in score as an extra sanity check.
        for (var i = 1; i < result.Passages.Count; i++)
        {
            Assert.True(result.Passages[i - 1].Score >= result.Passages[i].Score);
        }
    }

    [Fact]
    public async Task SearchAsync_relativizes_the_absolute_concept_path_against_the_bundle_root()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.All(result.Passages, p => Assert.False(Path.IsPathRooted(p.BundleRelativePath)));
    }

    [Fact]
    public async Task SearchAsync_over_missing_directory_returns_SourceUnavailable_without_throwing()
    {
        using var tmp = new TempDir();
        var missing = Path.Combine(tmp.Path, "does-not-exist");
        var source = new OkfBundleKnowledgeSource("gone", missing);

        var result = await source.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Empty(result.Passages);
        Assert.NotNull(result.Diagnostic);
        Assert.Equal(KnowledgeDiagnosticCode.SourceUnavailable, result.Diagnostic!.Code);
        Assert.Equal("gone", result.Diagnostic.SourceId);
    }

    [Fact]
    public async Task SearchAsync_with_blank_query_returns_no_passages_without_throwing()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("   "));

        Assert.Null(result.Diagnostic);
        Assert.Empty(result.Passages);
    }

    [Fact]
    public async Task SearchAsync_honors_the_tag_filter()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var unfiltered = await source.SearchAsync(new KnowledgeQuery("sales"));
        var filtered = await source.SearchAsync(new KnowledgeQuery("sales", "sales"));

        Assert.Equal(3, unfiltered.Passages.Count);
        Assert.Equal(2, filtered.Passages.Count);
        Assert.DoesNotContain(filtered.Passages, p => p.ConceptId == "tables/users");
    }

    // ---- Defensive null-Text guard (NRT is a compile-time hint only) -------

    [Fact]
    public async Task SearchAsync_with_null_query_Text_returns_empty_without_throwing()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        // KnowledgeQuery.Text is annotated non-null, but nothing at runtime
        // stops a null from arriving here (deserialization, reflection, an
        // explicit `!`) -- this must never throw a NullReferenceException,
        // per this type's documented never-throw contract.
        var result = await source.SearchAsync(new KnowledgeQuery(null!));

        Assert.Null(result.Diagnostic);
        Assert.Empty(result.Passages);
    }

    // ---- BundleRelativePath is normalized to '/' regardless of OS ---------

    [Fact]
    public async Task SearchAsync_normalizes_nested_BundleRelativePath_to_forward_slashes()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("orders"));

        // appendix_a's concepts live under nested directories (e.g. tables/orders.md),
        // so this exercises an actual multi-segment relative path, not just a bare filename.
        Assert.Contains(result.Passages, p => p.BundleRelativePath.Contains('/'));
        Assert.All(result.Passages, p => Assert.DoesNotContain('\\', p.BundleRelativePath));
    }

    // ---- Trust/status/stale_after are carried through from frontmatter -----

    [Fact]
    public async Task Passage_carries_trust_status_and_stale_after()
    {
        using var tmp = new TempDir();
        tmp.Write("dau.md",
            "---\ntype: Metric\ntitle: DAU active\ndescription: d\nstatus: deprecated\nverified: {by: human:ada}\nstale_after: 2026-01-01\n---\nActive users.\n");
        var source = new OkfBundleKnowledgeSource("s1", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("active"));
        var p = Assert.Single(result.Passages);
        Assert.Equal(TrustTier.HumanReviewed, p.TrustTier);
        Assert.Equal(ConceptStatus.Deprecated, p.Lifecycle.Status);
        Assert.Equal("2026-01-01", p.Lifecycle.StaleAfterRaw);
    }

    // ---- Passages is a genuine read-only view (not just a List<T> hidden behind an interface) --

    [Fact]
    public async Task SearchAsync_Passages_cannot_be_downcast_to_a_mutable_list_and_mutated()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);
        var source = new OkfBundleKnowledgeSource("mine", tmp.Path);

        var result = await source.SearchAsync(new KnowledgeQuery("orders"));
        Assert.NotEmpty(result.Passages);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (List<KnowledgePassage>)result.Passages;
            mutable.Clear();
        });

        Assert.IsType<InvalidCastException>(castAttempt);
    }
}
