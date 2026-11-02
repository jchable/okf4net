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

    private static NotifyingBundleProvider NewNotifying(string id = "mem") =>
        new(new InMemoryBundleProvider(id: id).Add("n.md", Doc));

    [Fact]
    public async Task A_rejected_reload_disposes_the_handles_it_built_before_the_failure()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        var notifying = NewNotifying();
        var good = new TestProviderFactory("memory", _ => notifying);
        var broken = new TestProviderFactory("broken", _ => throw new InvalidOperationException("s3://secret"));
        using var catalog = Catalog(
            root,
            """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""",
            policyFor: s => s.Id == "n" ? BundleRefreshPolicy.Notify : null,
            factories: [good, broken]);
        var before = catalog.Current;

        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "a", "path": "./a" }, { "id": "n", "provider": "memory" }, { "id": "bad", "provider": "broken" } ] }""");
        var after = await catalog.ReloadAsync();

        Assert.Same(before, after);
        var diagnostic = Assert.Single(catalog.LastReloadDiagnostics);
        Assert.Equal(CatalogDiagnosticCode.ProviderUnavailable, diagnostic.Code);
        Assert.DoesNotContain("secret", diagnostic.Message);
        Assert.Single(good.Created); // the handle was built, then disposed by the rejection
        Assert.Equal(0, notifying.Subscribers);
        Assert.Same(before.Handles["a"], catalog.Current.Handles["a"]);
        Assert.Equal(1, catalog.Current.Handles["a"].Current.Count);
    }

    [Fact]
    public void A_throwing_RefreshPolicyFor_rejects_the_load_by_type_name_and_disposes_the_handles_built_before_it()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        var notifying = NewNotifying();
        var factory = new TestProviderFactory("memory", _ => notifying);

        var error = Assert.Throws<CatalogException>(() => Catalog(
            root,
            """{ "version": 1, "sources": [ { "id": "n", "provider": "memory" }, { "id": "a", "path": "./a" } ] }""",
            policyFor: s => s.Id == "n"
                ? BundleRefreshPolicy.Notify
                : throw new InvalidOperationException("tenant secret-42 unknown"),
            factories: factory));

        Assert.Contains("[ProviderUnavailable]", error.Message);
        Assert.Contains("(InvalidOperationException)", error.Message);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Equal(0, notifying.Subscribers);
    }

    [Fact]
    public async Task A_throwing_RefreshPolicyFor_on_a_reload_keeps_the_current_snapshot()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        var throwNow = false;
        using var catalog = Catalog(
            root,
            """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""",
            policyFor: _ => throwNow ? throw new InvalidOperationException("boom") : null);
        var before = catalog.Current;

        throwNow = true;
        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "a", "path": "./a", "priority": 9 } ] }""");
        var after = await catalog.ReloadAsync();

        Assert.Same(before, after);
        Assert.Equal(CatalogDiagnosticCode.ProviderUnavailable, Assert.Single(catalog.LastReloadDiagnostics).Code);
    }

    [Fact]
    public async Task A_handle_whose_dispose_throws_when_it_leaves_does_not_fail_the_published_reload()
    {
        using var root = new TempDir();
        var first = NewNotifying("first");
        var second = NewNotifying("second");
        var factory = new TestProviderFactory("memory", s => s.Id == "one" ? new ThrowingDisposeProvider(first) : second);
        var catalog = Catalog(
            root,
            """{ "version": 1, "sources": [ { "id": "one", "provider": "memory" }, { "id": "two", "provider": "memory" } ] }""",
            policy: BundleRefreshPolicy.Notify,
            factories: factory);
        Assert.Equal(1, second.Subscribers);

        // "one" leaves: its dispose throws, the reload is still published.
        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "two", "provider": "memory" } ] }""");
        var reloaded = await catalog.ReloadAsync();
        Assert.Equal(2, reloaded.Generation);
        Assert.Empty(catalog.LastReloadDiagnostics);

        catalog.Dispose();
        Assert.Equal(0, second.Subscribers);
    }

    [Fact]
    public async Task A_dispose_that_throws_in_a_rejected_load_loses_neither_the_diagnostic_nor_the_other_handles()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        var first = NewNotifying("first");
        var second = NewNotifying("second");
        var factory = new TestProviderFactory("memory", s => s.Id == "one" ? new ThrowingDisposeProvider(first) : second);
        var broken = new TestProviderFactory("broken", _ => throw new InvalidOperationException("s3://secret"));
        using var catalog = Catalog(
            root,
            """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""",
            policyFor: s => s.IsFileSystem ? null : BundleRefreshPolicy.Notify,
            factories: [factory, broken]);
        var before = catalog.Current;

        // "one" (throwing dispose) is built first, then "two", then "bad" fails: the load is rejected
        // and the two built handles are disposed, the throwing one first.
        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "a", "path": "./a" }, { "id": "one", "provider": "memory" }, { "id": "two", "provider": "memory" }, { "id": "bad", "provider": "broken" } ] }""");
        var after = await catalog.ReloadAsync();

        Assert.Same(before, after);
        var diagnostic = Assert.Single(catalog.LastReloadDiagnostics);
        Assert.Equal(CatalogDiagnosticCode.ProviderUnavailable, diagnostic.Code);
        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(0, second.Subscribers);
    }

    [Fact]
    public void A_dispose_that_throws_does_not_strand_the_other_handles_when_the_catalog_is_disposed()
    {
        using var root = new TempDir();
        var first = NewNotifying("first");
        var second = NewNotifying("second");
        var factory = new TestProviderFactory("memory", s => s.Id == "one" ? new ThrowingDisposeProvider(first) : second);
        var catalog = Catalog(
            root,
            """{ "version": 1, "sources": [ { "id": "one", "provider": "memory" }, { "id": "two", "provider": "memory" } ] }""",
            policy: BundleRefreshPolicy.Notify,
            factories: factory);
        Assert.Equal(1, second.Subscribers);

        // The throwing handle is the first one the snapshot iterates (manifest order).
        Assert.Equal("one", catalog.Current.Handles.Keys.First());
        catalog.Dispose();

        Assert.Equal(0, second.Subscribers);
    }

    [Fact]
    public async Task A_dispose_that_throws_does_not_strand_the_other_handles_a_reload_drops()
    {
        using var root = new TempDir();
        root.Write("a/x.md", Doc);
        var first = NewNotifying("first");
        var second = NewNotifying("second");
        var factory = new TestProviderFactory("memory", s => s.Id == "one" ? new ThrowingDisposeProvider(first) : second);
        using var catalog = Catalog(
            root,
            """{ "version": 1, "sources": [ { "id": "one", "provider": "memory" }, { "id": "two", "provider": "memory" }, { "id": "a", "path": "./a" } ] }""",
            policyFor: s => s.IsFileSystem ? null : BundleRefreshPolicy.Notify,
            factories: factory);
        Assert.Equal("one", catalog.Current.Handles.Keys.First());

        // Both leave the catalog, the throwing one iterated first by the disposal.
        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""");
        var reloaded = await catalog.ReloadAsync();

        Assert.Equal(2, reloaded.Generation);
        Assert.Empty(catalog.LastReloadDiagnostics);
        Assert.Equal(0, second.Subscribers);
    }

    [Fact]
    public async Task A_settings_change_builds_a_second_provider_and_drops_the_replaced_handles_subscription()
    {
        using var root = new TempDir();
        var providers = new List<NotifyingBundleProvider>();
        var factory = new TestProviderFactory(
            "memory",
            _ =>
            {
                var created = NewNotifying();
                providers.Add(created);
                return created;
            },
            "bucket");
        using var catalog = Catalog(
            root,
            """{ "version": 1, "sources": [ { "id": "m", "provider": "memory", "settings": { "bucket": "one" } } ] }""",
            policy: BundleRefreshPolicy.Notify,
            factories: factory);
        var old = catalog.Current.Handles["m"];
        Assert.Equal(1, providers[0].Subscribers);

        root.Write("catalog.json", """{ "version": 1, "sources": [ { "id": "m", "provider": "memory", "settings": { "bucket": "two" } } ] }""");
        var reloaded = await catalog.ReloadAsync();

        Assert.Equal(2, factory.Created.Count);
        Assert.NotSame(old, reloaded.Handles["m"]);
        Assert.Equal(0, providers[0].Subscribers);
        Assert.Equal(1, providers[1].Subscribers);
    }

    [SkippableFact]
    public void A_failure_after_the_handles_are_built_disposes_them_when_the_constructor_throws()
    {
        // The only way to make CreateWatcher fail after a successful load without a seam:
        // deny the right to list the manifest's directory (a watcher needs it, reading the
        // known file does not), verified below by probing a watcher on that directory.
        using var root = new TempDir();
        var notifying = NewNotifying();
        var manifestDirectory = Path.Combine(root.Path, "cfg");
        Directory.CreateDirectory(manifestDirectory);
        File.WriteAllText(Path.Combine(manifestDirectory, "catalog.json"), """{ "version": 1, "sources": [ { "id": "mem", "provider": "memory" } ] }""");

        var restore = DenyListing(manifestDirectory);
        try
        {
            Skip.If(restore is null, "cannot deny listing of a directory on this machine");
            Skip.If(WatcherCanBeCreatedOn(manifestDirectory), "a watcher can still be created on a directory whose listing is denied (running as a privileged user?)");

            var options = new KnowledgeCatalogOptions
            {
                CatalogFilePath = Path.Combine(manifestDirectory, "catalog.json"),
                CatalogRoot = root.Path,
                WatchForChanges = true,
                ProviderFactories = [new TestProviderFactory("memory", _ => notifying)],
                RefreshPolicy = BundleRefreshPolicy.Notify,
            };

            Assert.ThrowsAny<Exception>(() => new FileKnowledgeCatalog(options));
            Assert.Equal(0, notifying.Subscribers);
        }
        finally
        {
            restore?.Invoke();
        }
    }

    private static bool WatcherCanBeCreatedOn(string directory)
    {
        try
        {
            using var probe = new FileSystemWatcher(directory, "x") { EnableRaisingEvents = true };
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Denies listing <paramref name="directory"/> (not its children); returns the undo, or <see langword="null"/> when this machine cannot.</summary>
    private static Action? DenyListing(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            var sid = "*" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            if (!Icacls(directory, "/deny", sid + ":(RD)"))
            {
                return null;
            }

            return () => Icacls(directory, "/remove:d", sid);
        }

        try
        {
            var original = File.GetUnixFileMode(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return () =>
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(directory, original);
                }
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static bool Icacls(params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("icacls.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    [SkippableFact]
    public void A_source_directory_replaced_by_a_junction_outside_the_root_never_serves_outside_content()
    {
        using var root = new TempDir();
        using var outside = new TempDir();
        root.Write("a/x.md", Doc);
        outside.Write("leak.md", "---\ntype: Note\ntitle: Leak\ndescription: d\n---\nLeaked.\n");
        using var catalog = Catalog(root, """{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""");
        var handle = catalog.Current.Handles["a"];
        Assert.Equal(1, handle.Current.Count);

        Directory.Delete(Path.Combine(root.Path, "a"), recursive: true);
        Skip.IfNot(root.TryCreateJunctionToExternalDir("a", outside.Path), "no junction/symlink privilege on this machine");

        // A no-leak oracle, not a pin of one outcome: the OnAccess handle may fail closed
        // (BundleLoadException), or keep serving its previous image (stale), or whatever the
        // guards decide on this platform; the only thing asserted is that nothing from outside
        // the catalog root is ever served.
        try
        {
            var served = handle.Current;
            Assert.DoesNotContain(served.Concepts, c => c.Path.EndsWith("leak.md", StringComparison.Ordinal));
        }
        catch (BundleLoadException)
        {
            // failed closed
        }
    }
}

/// <summary>A notifying provider whose change subscription throws when it is disposed.</summary>
file sealed class ThrowingDisposeProvider(NotifyingBundleProvider inner) : IBundleProvider, IBundleChangeNotifier
{
    public IDisposable Subscribe(Action onChange)
    {
        var inside = inner.Subscribe(onChange);
        return new Throwing(inside);
    }

    public string Id => inner.Id;
    public string Location => inner.Location;
    public BundleProviderCapabilities Capabilities => inner.Capabilities;
    public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;
    public BundleVersion GetVersion(BundleLimits limits) => inner.GetVersion(limits);
    public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits) => inner.Enumerate(prefix, limits);
    public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) => inner.Read(key, expected);
    public bool Exists(BundleKey key) => inner.Exists(key);
    public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => inner.Write(key, content, condition);
    public void Delete(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);

    private sealed class Throwing(IDisposable inside) : IDisposable
    {
        public void Dispose()
        {
            inside.Dispose();
            throw new InvalidOperationException("dispose failed");
        }
    }
}
