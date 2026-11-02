// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;
using OKF4net.Internal;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// The catalog snapshot owns the handles (spec §5.2, §9): one per enabled knowledge source,
/// built by its provider under the catalog's options and the source's policy, confined to the
/// catalog root; reused across a reload only when the source's effective configuration is
/// unchanged; dropped and disposed when the source changes or leaves, while a request that
/// still holds the old snapshot keeps reading its image; disposed with the catalog.
/// </summary>
public class CatalogHandleTests
{
    private const string Doc = "---\ntype: Note\ntitle: A\ndescription: d\n---\nAlpha.\n";

    private static FileKnowledgeCatalog Catalog(
        TempDir root,
        string json,
        BundleRefreshPolicy? policy = null,
        Func<KnowledgeCatalogSource, BundleRefreshPolicy?>? policyFor = null,
        params IBundleProviderFactory[] factories)
    {
        root.Write("catalog.json", json);
        return new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = Path.Combine(root.Path, "catalog.json"),
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = factories,
            RefreshPolicy = policy ?? BundleRefreshPolicy.OnAccess,
            RefreshPolicyFor = policyFor,
        });
    }

    private const string TwoSources = """
        {
          "version": 1,
          "sources": [
            { "id": "a", "path": "./a" },
            { "id": "b", "path": "./b", "enabled": false },
            { "id": "m", "path": "./m", "role": "memory", "tier": "user" }
          ]
        }
        """;

    [Fact]
    public void Construction_builds_one_confined_OnAccess_handle_per_enabled_knowledge_source()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        root.Write("b/x.md", Doc);
        Directory.CreateDirectory(Path.Combine(root.Path, "m"));
        using var catalog = Catalog(root, TwoSources);

        var handles = catalog.Current.Handles;
        var handle = Assert.Single(handles).Value;
        Assert.True(handles.ContainsKey("a"));
        Assert.Equal(BundleRefreshMode.OnAccess, handle.RefreshPolicy.Mode);
        Assert.Same(BundleLoadOptions.Default, handle.Options);
        var provider = Assert.IsType<FileSystemBundleProvider>(handle.Provider);
        Assert.Equal(Path.Combine(root.Path, "a"), provider.Root);
        Assert.Equal(ReparsePoints.CanonicalizeRoot(root.Path), provider.ConfinementBoundary);
        Assert.Equal(1, handle.Current.Count);
    }

    [Fact]
    public async Task A_reload_with_an_unchanged_source_keeps_its_handle_and_its_image()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""");
        var before = catalog.Current.Handles["a"];
        var image = before.Current;

        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "a", "path": "./a", "priority": 5 } ] }""");
        var reloaded = await catalog.ReloadAsync();

        Assert.Equal(2, reloaded.Generation);
        Assert.Same(before, reloaded.Handles["a"]);
        Assert.Same(image, reloaded.Handles["a"].Current);
    }

    [Fact]
    public async Task A_reload_that_changes_a_sources_path_builds_a_new_handle_and_the_old_one_still_serves_its_image()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        root.Write("a2/x.md", Doc);
        root.Write("a2/y.md", Doc);
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""");
        var old = catalog.Current.Handles["a"];
        var oldImage = old.Current;

        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "a", "path": "./a2" } ] }""");
        var reloaded = await catalog.ReloadAsync();

        var fresh = reloaded.Handles["a"];
        Assert.NotSame(old, fresh);
        Assert.Equal(2, fresh.Current.Count);
        Assert.Equal(Path.Combine(root.Path, "a2"), fresh.Location);
        Assert.Same(oldImage, old.Current); // disposed by the catalog, still readable by a request that holds the old snapshot
    }

    [Theory]
    [InlineData("""{ this is not json""")]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "path": "./gone" } ] }""")]
    public async Task A_rejected_reload_keeps_every_handle(string broken)
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""");
        var before = catalog.Current;

        root.Write("catalog.json", broken);
        var after = await catalog.ReloadAsync();

        Assert.Same(before, after);
        Assert.NotEmpty(catalog.LastReloadDiagnostics);
        Assert.Equal(1, before.Handles["a"].Current.Count);
    }

    [Fact]
    public async Task A_factory_source_gets_a_handle_under_its_own_policy_and_leaves_the_catalog_disposed()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        var notifying = new NotifyingBundleProvider(new InMemoryBundleProvider(id: "mem").Add("n.md", Doc));
        var factory = new TestProviderFactory("memory", _ => notifying);
        const string withMemory = """{ "version": 1, "sources": [ { "id": "a", "path": "./a" }, { "id": "mem", "provider": "memory" } ] }""";
        using var catalog = Catalog(
            root,
            withMemory,
            policyFor: s => s.Id == "mem" ? BundleRefreshPolicy.Notify : null,
            factories: factory);

        var handle = catalog.Current.Handles["mem"];
        Assert.Same(notifying, handle.Provider);
        Assert.Equal(BundleRefreshMode.Notify, handle.RefreshPolicy.Mode);
        Assert.Equal(BundleRefreshMode.OnAccess, catalog.Current.Handles["a"].RefreshPolicy.Mode);
        Assert.Equal(1, notifying.Subscribers);
        Assert.Single(factory.Created);

        var reloaded = await catalog.ReloadAsync();
        Assert.Same(handle, reloaded.Handles["mem"]);
        Assert.Single(factory.Created); // reused, not created again

        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""");
        var without = await catalog.ReloadAsync();
        Assert.False(without.Handles.ContainsKey("mem"));
        Assert.Equal(0, notifying.Subscribers);
    }

    [Fact]
    public void A_factory_that_throws_or_a_Notify_policy_over_a_silent_provider_rejects_the_load_and_builds_nothing()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        var throwing = new TestProviderFactory("memory", _ => throw new InvalidOperationException("bucket s3://secret/path unreachable"));
        const string withMemory = """{ "version": 1, "sources": [ { "id": "mem", "provider": "memory" }, { "id": "a", "path": "./a" } ] }""";

        var error = Assert.Throws<CatalogException>(() => Catalog(root, withMemory, factories: throwing));
        Assert.Contains("[ProviderUnavailable]", error.Message);
        Assert.Contains("(InvalidOperationException)", error.Message);
        Assert.DoesNotContain("secret", error.Message);

        var silent = new TestProviderFactory("memory");
        var notify = Assert.Throws<CatalogException>(() => Catalog(root, withMemory, policy: BundleRefreshPolicy.Notify, factories: silent));
        Assert.Contains("[ProviderUnavailable]", notify.Message);
        Assert.Contains("(ArgumentException)", notify.Message);
    }

    [Fact]
    public void Disposing_the_catalog_disposes_its_handles()
    {
        using var root = new TempDir();
        var notifying = new NotifyingBundleProvider(new InMemoryBundleProvider(id: "mem").Add("n.md", Doc));
        var catalog = Catalog(
            root,
            """{ "version": 1, "sources": [ { "id": "mem", "provider": "memory" } ] }""",
            policy: BundleRefreshPolicy.Notify,
            factories: new TestProviderFactory("memory", _ => notifying));
        Assert.Equal(1, notifying.Subscribers);

        catalog.Dispose();
        catalog.Dispose();

        Assert.Equal(0, notifying.Subscribers);
    }

    [Fact]
    public void A_hand_built_snapshot_carries_no_handle()
    {
        Assert.True(CatalogManifestParser.TryParse("""{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""", "/m", out var snapshot, out _));

        Assert.Empty(snapshot!.Handles);
        Assert.Empty((snapshot with { Generation = 3 }).Handles);
    }
}
