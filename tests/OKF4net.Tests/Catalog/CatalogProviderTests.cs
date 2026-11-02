// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// A <c>catalog.json</c> source names its provider (spec §9): the <c>provider</c> and
/// <c>settings</c> fields of the manifest, their parse rules, the factories a host registers
/// (<see cref="KnowledgeCatalogOptions.ProviderFactories"/>, <see cref="KnowledgeOptions.AddProviderFactory"/>)
/// and what the catalog refuses — an unknown type, a setting the factory does not accept, a
/// memory source off the filesystem — before any provider is created.
/// </summary>
public class CatalogProviderTests
{
    private static KnowledgeCatalogSnapshot Parse(string json)
    {
        Assert.True(CatalogManifestParser.TryParse(json, "/m", out var snapshot, out var diagnostics), string.Join("; ", diagnostics.Select(d => d.Message)));
        return snapshot!;
    }

    private static IReadOnlyList<CatalogDiagnostic> Reject(string json)
    {
        Assert.False(CatalogManifestParser.TryParse(json, "/m", out _, out var diagnostics));
        return diagnostics;
    }

    // ---- The parser --------------------------------------------------------

    [Fact]
    public void A_source_without_provider_is_a_filesystem_source_with_no_setting()
    {
        var source = Assert.Single(Parse("""{ "version": 1, "sources": [ { "id": "a", "path": "./a" } ] }""").Sources);

        Assert.Equal(KnowledgeCatalogSource.FileSystemProviderType, source.ProviderType);
        Assert.True(source.IsFileSystem);
        Assert.Same(ProviderSettings.Empty, source.Settings);
        Assert.Equal("./a", source.Path);
    }

    [Fact]
    public void A_source_may_name_a_provider_and_its_settings_and_then_carries_no_path()
    {
        var source = Assert.Single(Parse("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "settings": { "bucket": "b", "prefix": "p/" } } ] }""").Sources);

        Assert.Equal("s3", source.ProviderType);
        Assert.False(source.IsFileSystem);
        Assert.Equal(string.Empty, source.Path);
        Assert.Equal(ProviderSettings.From([new("prefix", "p/"), new("bucket", "b")]), source.Settings);
        Assert.Equal(["bucket", "prefix"], source.Settings.Keys.ToList());
    }

    [Theory]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "" , "path": "./a" } ] }""", CatalogDiagnosticCode.MalformedProvider)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": 3, "path": "./a" } ] }""", CatalogDiagnosticCode.MalformedProvider)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "settings": [ "bucket" ] } ] }""", CatalogDiagnosticCode.MalformedSettings)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "settings": { "bucket": 1 } } ] }""", CatalogDiagnosticCode.MalformedSettings)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "settings": { "bucket": "b", "bucket": "c" } } ] }""", CatalogDiagnosticCode.MalformedSettings)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "path": "./a", "settings": { "bucket": "b" } } ] }""", CatalogDiagnosticCode.IllegalSetting)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "path": "./a" } ] }""", CatalogDiagnosticCode.IllegalPath)]
    [InlineData("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "role": "memory", "tier": "user" } ] }""", CatalogDiagnosticCode.IllegalMemoryProvider)]
    public void The_parser_rejects_a_malformed_or_illegal_provider_declaration(string json, CatalogDiagnosticCode expected)
    {
        var diagnostics = Reject(json);

        Assert.Contains(diagnostics, d => d.Code == expected);
    }

    [Fact]
    public void A_filesystem_source_still_needs_a_path_and_a_memory_source_may_say_filesystem()
    {
        Assert.Contains(Reject("""{ "version": 1, "sources": [ { "id": "a", "provider": "filesystem" } ] }"""), d => d.Code == CatalogDiagnosticCode.EmptyPath);

        var memory = Assert.Single(Parse("""{ "version": 1, "sources": [ { "id": "m", "path": "./m", "role": "memory", "tier": "user", "provider": "filesystem" } ] }""").Sources);
        Assert.True(memory.IsFileSystem);
        Assert.Equal(SourceRole.Memory, memory.Role);
    }

    [Fact]
    public void ProviderSettings_compare_by_value_and_refuse_a_duplicate_name()
    {
        var a = ProviderSettings.From([new("x", "1"), new("y", "2")]);
        var b = ProviderSettings.From([new("y", "2"), new("x", "1")]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, ProviderSettings.From([new("x", "1")]));
        Assert.Equal("1", a["x"]);
        Assert.False(a.TryGetValue("z", out _));
        Assert.Same(ProviderSettings.Empty, ProviderSettings.From([]));
        Assert.Throws<ArgumentException>(() => ProviderSettings.From([new("x", "1"), new("x", "2")]));
        Assert.Throws<KeyNotFoundException>(() => a["z"]);
    }

    [Fact]
    public void Two_sources_whose_settings_come_in_different_orders_are_equal_records_with_equal_hashes()
    {
        var first = Assert.Single(Parse("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "settings": { "bucket": "b", "prefix": "p/" } } ] }""").Sources);
        var second = Assert.Single(Parse("""{ "version": 1, "sources": [ { "id": "a", "provider": "s3", "settings": { "prefix": "p/", "bucket": "b" } } ] }""").Sources);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.NotEqual(first, second with { ProviderType = "blob" });
    }

    [Fact]
    public void ToString_never_prints_a_setting_value_on_the_settings_or_on_the_source()
    {
        var settings = ProviderSettings.From([new("y", "secret-two"), new("x", "secret-one")]);
        var source = new KnowledgeCatalogSource("a", string.Empty, 0, true, SourceRole.Knowledge) { ProviderType = "s3", Settings = settings };

        Assert.Equal("x=***;y=***", settings.ToString());
        Assert.DoesNotContain("secret", source.ToString());
        Assert.Contains("x=***", source.ToString());
    }

    // ---- Diagnostic order --------------------------------------------------
    // A manifest without provider/settings reports its problems in the order it always did:
    // id, path, priority, enabled, role, tier.

    [Theory]
    [InlineData("""{ "id": "a", "path": "", "role": "bogus" }""", new[] { CatalogDiagnosticCode.EmptyPath, CatalogDiagnosticCode.IllegalRole })]
    [InlineData("""{ "id": "a", "path": "./a", "priority": "x", "enabled": "y", "role": "bogus" }""", new[] { CatalogDiagnosticCode.MalformedPriority, CatalogDiagnosticCode.MalformedEnabled, CatalogDiagnosticCode.IllegalRole })]
    [InlineData("""{ "id": "a", "path": "", "priority": "x", "role": "memory" }""", new[] { CatalogDiagnosticCode.EmptyPath, CatalogDiagnosticCode.MalformedPriority, CatalogDiagnosticCode.IllegalTier })]
    [InlineData("""{ "id": "a", "path": "./a", "role": "bogus", "tier": "user" }""", new[] { CatalogDiagnosticCode.IllegalRole })]
    [InlineData("""{ "id": "a", "path": "./a", "tier": "user" }""", new[] { CatalogDiagnosticCode.IllegalTier })]
    public void A_manifest_without_provider_reports_its_diagnostics_in_the_shipped_order(string source, CatalogDiagnosticCode[] expected)
    {
        var diagnostics = Reject($$"""{ "version": 1, "sources": [ {{source}} ] }""");

        Assert.Equal(expected, diagnostics.Select(d => d.Code).ToArray());
    }

    // The numeric value of every member that shipped in 0.7.0 is part of the binary contract.
    [Theory]
    [InlineData(CatalogDiagnosticCode.ParseError, 0)]
    [InlineData(CatalogDiagnosticCode.UnknownRootProperty, 1)]
    [InlineData(CatalogDiagnosticCode.UnknownSourceProperty, 2)]
    [InlineData(CatalogDiagnosticCode.WrongVersion, 3)]
    [InlineData(CatalogDiagnosticCode.EmptySources, 4)]
    [InlineData(CatalogDiagnosticCode.DuplicateSourceId, 5)]
    [InlineData(CatalogDiagnosticCode.InvalidSourceId, 6)]
    [InlineData(CatalogDiagnosticCode.EmptyPath, 7)]
    [InlineData(CatalogDiagnosticCode.EmbeddedNul, 8)]
    [InlineData(CatalogDiagnosticCode.MalformedPriority, 9)]
    [InlineData(CatalogDiagnosticCode.MalformedEnabled, 10)]
    [InlineData(CatalogDiagnosticCode.IllegalRole, 11)]
    [InlineData(CatalogDiagnosticCode.IllegalTier, 12)]
    [InlineData(CatalogDiagnosticCode.DuplicateMemoryTier, 13)]
    [InlineData(CatalogDiagnosticCode.InvalidPath, 14)]
    [InlineData(CatalogDiagnosticCode.AbsolutePath, 15)]
    [InlineData(CatalogDiagnosticCode.OutsideRoot, 16)]
    [InlineData(CatalogDiagnosticCode.TargetNotFound, 17)]
    [InlineData(CatalogDiagnosticCode.ReparsePointInPath, 18)]
    public void A_diagnostic_code_that_shipped_keeps_its_numeric_value(CatalogDiagnosticCode code, int shipped)
    {
        Assert.Equal(shipped, (int)code);
    }

    [Fact]
    public void The_provider_codes_are_appended_after_the_shipped_ones()
    {
        CatalogDiagnosticCode[] added =
        [
            CatalogDiagnosticCode.MalformedProvider,
            CatalogDiagnosticCode.MalformedSettings,
            CatalogDiagnosticCode.IllegalSetting,
            CatalogDiagnosticCode.IllegalPath,
            CatalogDiagnosticCode.IllegalMemoryProvider,
            CatalogDiagnosticCode.UnknownProvider,
            CatalogDiagnosticCode.ProviderUnavailable,
        ];

        Assert.Equal(Enumerable.Range(19, 7), added.Select(c => (int)c));
    }

    // ---- The catalog -------------------------------------------------------

    private static (TempDir Root, string Manifest) CatalogWith(string json)
    {
        var root = new TempDir();
        Directory.CreateDirectory(Path.Combine(root.Path, "docs"));
        return (root, root.Write("catalog.json", json));
    }

    private const string UnknownProviderJson = """{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" }, { "id": "remote", "provider": "s3", "settings": { "bucket": "b" } } ] }""";

    [Fact]
    public void A_source_naming_a_provider_with_no_factory_is_refused_at_construction()
    {
        var (root, manifest) = CatalogWith(UnknownProviderJson);
        using var disposable = root;

        var error = Assert.Throws<CatalogException>(() => new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
        }));

        Assert.Contains("[UnknownProvider]", error.Message);
        Assert.Contains("'s3'", error.Message);
    }

    [Fact]
    public void A_registered_factory_accepts_the_source_and_a_setting_it_does_not_list_is_refused()
    {
        var (root, manifest) = CatalogWith(UnknownProviderJson);
        using var disposable = root;
        var accepting = new TestProviderFactory("s3", acceptedSettings: ["bucket", "prefix"]);
        var options = new KnowledgeCatalogOptions
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = [accepting],
        };

        using var catalog = new FileKnowledgeCatalog(options);
        Assert.Equal(2, catalog.Current.Sources.Count);

        var refusing = new TestProviderFactory("s3", acceptedSettings: ["prefix"]);
        var error = Assert.Throws<CatalogException>(() => new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = [refusing],
        }));
        Assert.Contains("[IllegalSetting]", error.Message);
        Assert.Contains("'bucket'", error.Message);
    }

    [Fact]
    public async Task A_reload_naming_an_unknown_provider_is_rejected_and_keeps_the_snapshot()
    {
        var (root, manifest) = CatalogWith("""{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }""");
        using var disposable = root;
        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
        });
        var before = catalog.Current;

        File.WriteAllText(manifest, UnknownProviderJson);
        var after = await catalog.ReloadAsync();

        Assert.Same(before, after);
        Assert.Contains(catalog.LastReloadDiagnostics, d => d.Code == CatalogDiagnosticCode.UnknownProvider);
    }

    [Fact]
    public void A_factory_for_the_built_in_type_or_a_duplicate_type_is_a_configuration_error()
    {
        var (root, manifest) = CatalogWith("""{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }""");
        using var disposable = root;
        KnowledgeCatalogOptions Options(params IBundleProviderFactory[] factories) => new()
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = factories,
        };

        Assert.Throws<ArgumentException>(() => new FileKnowledgeCatalog(Options(new TestProviderFactory("filesystem"))));
        Assert.Throws<ArgumentException>(() => new FileKnowledgeCatalog(Options(new TestProviderFactory("s3"), new TestProviderFactory("s3"))));
        Assert.Throws<ArgumentException>(() => new FileKnowledgeCatalog(Options(new TestProviderFactory(string.Empty))));
        using var fine = new FileKnowledgeCatalog(Options(new TestProviderFactory("s3"), new TestProviderFactory("blob")));
    }

    private const string DisabledRemoteJson = """{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" }, { "id": "remote", "enabled": false, "provider": "s3", "settings": { "bucket": "b" } } ] }""";

    [Fact]
    public void A_disabled_source_is_checked_against_a_factory_that_exists_but_not_against_a_missing_one()
    {
        var (root, manifest) = CatalogWith(DisabledRemoteJson);
        using var disposable = root;
        KnowledgeCatalogOptions Options(params IBundleProviderFactory[] factories) => new()
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = factories,
        };

        // No factory for the type: a host may register it, or enable the source, later.
        using (new FileKnowledgeCatalog(Options()))
        {
        }

        // A factory exists and accepts the setting.
        using (new FileKnowledgeCatalog(Options(new TestProviderFactory("s3", acceptedSettings: ["bucket"]))))
        {
        }

        // A factory exists and does not list the setting: refused although the source is disabled.
        var error = Assert.Throws<CatalogException>(() => new FileKnowledgeCatalog(Options(new TestProviderFactory("s3", acceptedSettings: ["prefix"]))));
        Assert.Contains("[IllegalSetting]", error.Message);
        Assert.Contains("'bucket'", error.Message);
    }

    [Fact]
    public void A_null_factory_or_a_null_factory_list_is_refused()
    {
        var (root, manifest) = CatalogWith("""{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }""");
        using var disposable = root;

        Assert.Throws<ArgumentNullException>(() => new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = [null!],
        }));
        Assert.Throws<ArgumentNullException>(() => new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = manifest,
            CatalogRoot = root.Path,
            WatchForChanges = false,
            ProviderFactories = null!,
        }));
    }

    // ---- Hosting -----------------------------------------------------------

    [Fact]
    public void AddKnowledge_refuses_a_misconfigured_factory_at_registration_time()
    {
        var (root, manifest) = CatalogWith("""{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }""");
        using var disposable = root;
        void Register(params IBundleProviderFactory[] factories) => new ServiceCollection().AddKnowledge(o =>
        {
            o.AddCatalogFile(manifest);
            foreach (var factory in factories)
            {
                o.AddProviderFactory(factory);
            }
        });

        Assert.Throws<ArgumentException>(() => Register(new TestProviderFactory("filesystem")));
        Assert.Throws<ArgumentException>(() => Register(new TestProviderFactory("s3"), new TestProviderFactory("s3")));
        Assert.Throws<ArgumentException>(() => Register(new TestProviderFactory(string.Empty)));
        Register(new TestProviderFactory("s3"), new TestProviderFactory("blob"));
    }

    [Fact]
    public void AddProviderFactory_hands_the_factories_to_the_catalog_options()
    {
        var (root, manifest) = CatalogWith("""{ "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }""");
        using var disposable = root;
        var factory = new TestProviderFactory("s3");
        var services = new ServiceCollection();

        services.AddKnowledge(o =>
        {
            o.AddCatalogFile(manifest);
            o.AddProviderFactory(factory);
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<KnowledgeCatalogOptions>();
        Assert.Same(factory, Assert.Single(options.ProviderFactories));
        Assert.Throws<ArgumentNullException>(() => new KnowledgeOptions().AddProviderFactory(null!));
    }
}
