// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// The resolvers search through the handles the snapshot keeps (spec §9, §11 "Catalogue"):
/// two searches over an unchanged source reuse one image; a change in storage is seen at the
/// next search (<see cref="BundleRefreshPolicy.OnAccess"/>); two sources over one storage are
/// deduplicated by the provider's id; a snapshot without handles still resolves each source's
/// path at every request; and a source root replaced by a junction after the handle was
/// created is refused by the provider at the next access, the previous image served stale.
/// </summary>
public class CatalogResolverHandleTests
{
    private const string Doc = "---\ntype: Note\ntitle: Orders\ndescription: d\n---\nOrders and refunds.\n";

    private static int Loads(InMemoryBundleProvider storage) => storage.Calls.Count(c => c == "enumerate ");

    private static FileKnowledgeCatalog Catalog(TempDir root, string json, params IBundleProviderFactory[] factories)
    {
        root.Write("catalog.json", json);
        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = factories,
        });
    }

    private const string OneMemorySource = """{ "version": 1, "sources": [ { "id": "mem", "provider": "memory" } ] }""";

    [Theory]
    [InlineData(KnowledgeResolverStrategy.GroupedBySource)]
    [InlineData(KnowledgeResolverStrategy.Merged)]
    [InlineData(KnowledgeResolverStrategy.PriorityWeighted)]
    public async Task Two_searches_over_an_unchanged_source_load_it_once_and_a_change_is_seen_at_the_next_search(KnowledgeResolverStrategy strategy)
    {
        using var root = new TempDir();
        var storage = new InMemoryBundleProvider(id: "mem").Add("a.md", Doc);
        using var catalog = Catalog(root, OneMemorySource, new TestProviderFactory("memory", _ => storage));
        var resolver = new KnowledgeResolverRouter(catalog, strategy, null, null);

        var first = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        var second = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Single(first.Passages);
        Assert.Single(second.Passages);
        Assert.Equal(1, Loads(storage));

        storage.Add("b.md", Doc);
        var third = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(2, third.Passages.Count);
        Assert.Equal(2, Loads(storage));
    }

    [Theory]
    [InlineData(KnowledgeResolverStrategy.Merged)]
    [InlineData(KnowledgeResolverStrategy.PriorityWeighted)]
    public async Task Two_sources_over_one_storage_are_deduplicated_by_the_providers_id(KnowledgeResolverStrategy strategy)
    {
        using var root = new TempDir();
        var storage = new InMemoryBundleProvider(id: "shared").Add("a.md", Doc);
        const string two = """{ "version": 1, "sources": [ { "id": "x", "provider": "memory", "priority": 2 }, { "id": "y", "provider": "memory", "priority": 1 } ] }""";
        using var catalog = Catalog(root, two, new TestProviderFactory("memory", _ => storage));
        var resolver = new KnowledgeResolverRouter(catalog, strategy, null, null);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        var passage = Assert.Single(context.Passages);
        Assert.Equal("x", passage.SourceId);
        var duplicate = Assert.Single(context.Diagnostics, d => d.Code == KnowledgeDiagnosticCode.DuplicateDirectory);
        Assert.Equal("y", duplicate.SourceId);
        Assert.Contains("('memory://shared')", duplicate.Message);
        Assert.Contains("as source 'x'", duplicate.Message);
    }

    [Fact]
    public async Task Two_filesystem_sources_over_one_directory_are_still_reported_under_the_resolved_directory()
    {
        using var root = new TempDir();
        root.Write("docs/a.md", Doc);
        const string two = """{ "version": 1, "sources": [ { "id": "x", "path": "./docs", "priority": 2 }, { "id": "y", "path": "docs", "priority": 1 } ] }""";
        using var catalog = Catalog(root, two);
        var resolver = new MergedKnowledgeResolver(catalog);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Single(context.Passages);
        var duplicate = Assert.Single(context.Diagnostics, d => d.Code == KnowledgeDiagnosticCode.DuplicateDirectory);
        Assert.Contains($"('{Path.Combine(root.Path, "docs")}')", duplicate.Message);
    }

    /// <summary>A catalog whose snapshot carries no handle: the parser's own, as a host or a test may build one.</summary>
    private sealed class HandlelessCatalog(string catalogRoot, KnowledgeCatalogSnapshot snapshot) : IKnowledgeCatalog
    {
        public KnowledgeCatalogSnapshot Current { get; } = snapshot;

        public string CatalogRoot { get; } = catalogRoot;

        public IReadOnlyList<CatalogDiagnostic> LastReloadDiagnostics => [];

        public ValueTask<KnowledgeCatalogSnapshot> ReloadAsync(CancellationToken cancellationToken = default) => new(Current);
    }

    [Theory]
    [InlineData(KnowledgeResolverStrategy.GroupedBySource)]
    [InlineData(KnowledgeResolverStrategy.Merged)]
    public async Task A_snapshot_without_handles_resolves_each_sources_path_at_every_search(KnowledgeResolverStrategy strategy)
    {
        using var root = new TempDir();
        root.Write("docs/a.md", Doc);
        Assert.True(CatalogManifestParser.TryParse("""{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" }, { "id": "gone", "path": "./gone" } ] }""", root.Path, out var snapshot, out _));
        var resolver = new KnowledgeResolverRouter(new HandlelessCatalog(root.Path, snapshot!), strategy, null, null);

        Assert.Single((await resolver.SearchAsync(new KnowledgeQuery("orders"))).Passages);
        root.Write("docs/b.md", Doc);
        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(2, context.Passages.Count);
        var unavailable = Assert.Single(context.Diagnostics, d => d.Code == KnowledgeDiagnosticCode.SourceUnavailable);
        Assert.Equal("gone", unavailable.SourceId);
        Assert.Contains("could not be re-resolved", unavailable.Message);
    }

    [Fact]
    public void The_numeric_values_of_the_shipped_diagnostic_codes_are_unchanged_and_SourceStale_is_appended()
    {
        // The enum has shipped: a member is only ever appended (values of 9d08c87~1).
        Assert.Equal(0, (int)KnowledgeDiagnosticCode.NoEnabledSources);
        Assert.Equal(1, (int)KnowledgeDiagnosticCode.SourceUnavailable);
        Assert.Equal(2, (int)KnowledgeDiagnosticCode.NoMatches);
        Assert.Equal(3, (int)KnowledgeDiagnosticCode.DuplicateDirectory);
        Assert.Equal(4, (int)KnowledgeDiagnosticCode.SourceStale);
        Assert.Equal(5, Enum.GetValues<KnowledgeDiagnosticCode>().Length);
    }

    [Theory]
    [InlineData(KnowledgeResolverStrategy.GroupedBySource)]
    [InlineData(KnowledgeResolverStrategy.Merged)]
    [InlineData(KnowledgeResolverStrategy.PriorityWeighted)]
    public async Task A_source_deleted_after_its_first_search_is_served_from_its_last_image_with_one_SourceStale_diagnostic(KnowledgeResolverStrategy strategy)
    {
        using var root = new TempDir();
        root.Write("docs/a.md", Doc);
        root.Write("other/b.md", Doc);
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "docs", "path": "./docs", "priority": 2 }, { "id": "other", "path": "./other", "priority": 1 } ] }""");
        var resolver = new KnowledgeResolverRouter(catalog, strategy, null, null);
        var first = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(2, first.Passages.Count);
        Assert.Empty(first.Diagnostics);

        Directory.Delete(Path.Combine(root.Path, "docs"), recursive: true);

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Equal(2, context.Passages.Count);
        Assert.Contains(context.Passages, p => p.SourceId == "docs");
        var stale = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.SourceStale, stale.Code);
        Assert.Equal("docs", stale.SourceId);
        var handle = catalog.Current.Handles["docs"];
        Assert.True(handle.IsStale);
        Assert.Contains($"bundle at '{handle.Location}' is served from its last good image; the last reload failed ({handle.LastLoadError!.GetType().Name})", stale.Message);
        Assert.DoesNotContain(context.Diagnostics, d => d.Code == KnowledgeDiagnosticCode.SourceUnavailable);
    }

    [Theory]
    [InlineData(KnowledgeResolverStrategy.GroupedBySource)]
    [InlineData(KnowledgeResolverStrategy.Merged)]
    [InlineData(KnowledgeResolverStrategy.PriorityWeighted)]
    public async Task A_stale_diagnostic_names_the_failure_by_type_only_and_goes_when_the_source_recovers(KnowledgeResolverStrategy strategy)
    {
        using var root = new TempDir();
        var storage = new InMemoryBundleProvider(id: "mem").Add("a.md", Doc);
        using var catalog = Catalog(root, OneMemorySource, new TestProviderFactory("memory", _ => storage));
        var resolver = new KnowledgeResolverRouter(catalog, strategy, null, null);
        Assert.Empty((await resolver.SearchAsync(new KnowledgeQuery("orders"))).Diagnostics);

        storage.Add("b.md", Doc);
        storage.FailReadsWith = new BundleAccessRefusedException(null, "s3://secret-bucket denied");
        var failing = await resolver.SearchAsync(new KnowledgeQuery("orders"));

        Assert.Single(failing.Passages);
        var stale = Assert.Single(failing.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.SourceStale, stale.Code);
        Assert.DoesNotContain("secret", stale.Message);

        storage.FailReadsWith = null;
        var recovered = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(2, recovered.Passages.Count);
        Assert.Empty(recovered.Diagnostics);
    }

    [Fact]
    public async Task A_catalog_reload_during_a_search_leaves_the_search_on_its_snapshots_handles()
    {
        using var root = new TempDir();
        var old = new InMemoryBundleProvider(id: "old").Add("a.md", Doc);
        var replacement = new InMemoryBundleProvider(id: "new").Add("b.md", Doc).Add("c.md", Doc);
        var factory = new TestProviderFactory("memory", s => s.Settings.TryGetValue("bucket", out var bucket) && bucket == "two" ? replacement : old, "bucket");
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "m", "provider": "memory", "settings": { "bucket": "one" } } ] }""", factory);
        var resolver = new GroupedKnowledgeResolver(catalog);
        var reloaded = false;

        // The policy runs inside the search, after it captured the snapshot: the reload it
        // triggers replaces the catalog's snapshot (and disposes the old handle) under it.
        var query = new KnowledgeQuery("orders")
        {
            SourceVisibilityPolicy = (_, _) =>
            {
                if (!reloaded)
                {
                    reloaded = true;
                    root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "m", "provider": "memory", "settings": { "bucket": "two" } } ] }""");
                    Assert.Equal(2, catalog.ReloadAsync().AsTask().GetAwaiter().GetResult().Generation);
                }

                return true;
            },
        };

        var during = await resolver.SearchAsync(query);

        Assert.True(reloaded);
        Assert.Equal(1, during.CatalogGeneration);
        var passage = Assert.Single(during.Passages);
        Assert.Equal("a", passage.ConceptId);
        Assert.Empty(during.Diagnostics);

        var after = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(2, after.CatalogGeneration);
        Assert.Equal(2, after.Passages.Count);
    }

    [SkippableFact]
    public async Task A_source_root_replaced_by_a_junction_after_the_handle_was_created_is_refused_at_the_next_access()
    {
        using var root = new TempDir();
        using var external = new TempDir();
        root.Write("docs/a.md", Doc);
        external.Write("a.md", Doc);
        external.Write("b.md", Doc);
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }""");
        var resolver = new GroupedKnowledgeResolver(catalog);
        var handle = catalog.Current.Handles["docs"];
        Assert.Single((await resolver.SearchAsync(new KnowledgeQuery("orders"))).Passages);

        Directory.Delete(Path.Combine(root.Path, "docs"), recursive: true);
        Skip.IfNot(root.TryCreateJunctionToExternalDir("docs", external.Path), "needs a junction or a directory symlink");

        // The provider re-checks the chain from the catalog root before every access (spec §8):
        // the token read is refused, the reload fails and the previous image is served stale
        // (spec §5.2); nothing under the junction is ever read. The stale image is reported,
        // by exception type, not silently served.
        var context = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Single(context.Passages);
        var stale = Assert.Single(context.Diagnostics);
        Assert.Equal(KnowledgeDiagnosticCode.SourceStale, stale.Code);
        Assert.Equal("docs", stale.SourceId);
        Assert.Contains("(BundleLoadException)", stale.Message);
        Assert.True(handle.IsStale);
        var failure = Assert.IsType<BundleLoadException>(handle.LastLoadError);
        Assert.IsType<BundleAccessRefusedException>(failure.InnerException);
        Assert.Throws<BundleAccessRefusedException>(() => handle.Provider.GetVersion(BundleLimits.Default));
        Assert.Throws<BundleAccessRefusedException>(() => handle.Provider.Read(BundleKey.Parse("b.md")));
    }
}
