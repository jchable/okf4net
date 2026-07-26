// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

namespace OKF4net.Tests.Catalog.Hosting;

/// <summary>
/// <see cref="KnowledgeServiceCollectionExtensions.AddKnowledge"/> exercised
/// over a real <see cref="ServiceCollection"/>/<see cref="ServiceProvider"/>:
/// end-to-end wiring (resolve, search, get grouped passages back), options
/// validation at registration time, singleton lifetimes, fail-fast
/// <see cref="CatalogException"/> surfacing on first resolve, and provider
/// disposal reaching the underlying <see cref="FileKnowledgeCatalog"/>. Uses
/// <see cref="TempDir"/> copies of <c>tests/fixtures/appendix_a</c>, never
/// touching <c>tests/fixtures</c> directly.
/// </summary>
public class KnowledgeServiceCollectionExtensionsTests
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
    /// Sets up a temp catalog root with two fixture-copy sources ("hi",
    /// priority 10; "lo", priority 1) and returns the written catalog.json
    /// path.
    /// </summary>
    private static string SetUpTwoSourceCatalogFile(TempDir root)
    {
        CopyDirectory(BundlePath, Path.Combine(root.Path, "source-hi"));
        CopyDirectory(BundlePath, Path.Combine(root.Path, "source-lo"));

        var json = """
            {
              "version": 1,
              "sources": [
                { "id": "lo", "path": "./source-lo", "priority": 1 },
                { "id": "hi", "path": "./source-hi", "priority": 10 }
              ]
            }
            """;
        return root.Write("catalog.json", json);
    }

    // ---- End-to-end wiring -------------------------------------------------

    [Fact]
    public async Task AddKnowledge_wires_a_working_resolver_end_to_end()
    {
        using var root = new TempDir();
        var catalogPath = SetUpTwoSourceCatalogFile(root);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(catalogPath));

        using var provider = services.BuildServiceProvider();
        var resolver = provider.GetRequiredService<IKnowledgeResolver>();

        var context = await resolver.SearchAsync(new KnowledgeQuery("orders sales"));

        Assert.Empty(context.Diagnostics);
        Assert.NotEmpty(context.Passages);

        var hiBundle = Bundle.Load(Path.Combine(root.Path, "source-hi"));
        var expectedHiCount = ConceptSearch.Search(hiBundle.Concepts, "orders sales").Count;
        var loBundle = Bundle.Load(Path.Combine(root.Path, "source-lo"));
        var expectedLoCount = ConceptSearch.Search(loBundle.Concepts, "orders sales").Count;

        Assert.Equal(expectedHiCount + expectedLoCount, context.Passages.Count);
        Assert.Equal(expectedHiCount, context.Passages.Count(p => p.SourceId == "hi"));
        Assert.Equal(expectedLoCount, context.Passages.Count(p => p.SourceId == "lo"));
    }

    // ---- Catalog root is derived from the file's directory -----------------

    [Fact]
    public void AddCatalogFile_derives_the_catalog_root_from_the_files_directory()
    {
        using var root = new TempDir();
        var catalogPath = SetUpTwoSourceCatalogFile(root);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(catalogPath));

        using var provider = services.BuildServiceProvider();
        var catalogOptions = provider.GetRequiredService<KnowledgeCatalogOptions>();

        Assert.Equal(Path.GetFullPath(catalogPath), catalogOptions.CatalogFilePath);
        Assert.Equal(Path.GetFullPath(root.Path), Path.GetFullPath(catalogOptions.CatalogRoot));
    }

    // ---- Singleton lifetimes ------------------------------------------------

    [Fact]
    public void IKnowledgeCatalog_is_the_same_singleton_instance_across_resolves()
    {
        using var root = new TempDir();
        var catalogPath = SetUpTwoSourceCatalogFile(root);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(catalogPath));

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IKnowledgeCatalog>();
        var second = provider.GetRequiredService<IKnowledgeCatalog>();

        Assert.Same(first, second);
    }

    [Fact]
    public void IKnowledgeResolver_is_the_same_singleton_instance_across_resolves()
    {
        using var root = new TempDir();
        var catalogPath = SetUpTwoSourceCatalogFile(root);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(catalogPath));

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IKnowledgeResolver>();
        var second = provider.GetRequiredService<IKnowledgeResolver>();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task IKnowledgeResolver_operates_over_the_same_singleton_catalog_instance()
    {
        using var root = new TempDir();
        var catalogPath = SetUpTwoSourceCatalogFile(root);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(catalogPath));

        using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<IKnowledgeCatalog>();
        var resolver = provider.GetRequiredService<IKnowledgeResolver>();

        var before = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(1, before.CatalogGeneration);

        await catalog.ReloadAsync();

        var after = await resolver.SearchAsync(new KnowledgeQuery("orders"));
        Assert.Equal(2, after.CatalogGeneration);
    }

    // ---- Options validation at registration time ----------------------------

    [Fact]
    public void AddKnowledge_with_no_catalog_file_throws_immediately()
    {
        var services = new ServiceCollection();

        var ex = Assert.Throws<ArgumentException>(() => services.AddKnowledge(_ => { }));
        Assert.Contains("AddCatalogFile", ex.Message);
    }

    [Fact]
    public void AddKnowledge_with_multiple_AddCatalogFile_calls_throws_immediately()
    {
        using var root = new TempDir();
        var catalogPath = SetUpTwoSourceCatalogFile(root);
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddKnowledge(o =>
        {
            o.AddCatalogFile(catalogPath);
            o.AddCatalogFile(catalogPath);
        }));
        Assert.Contains("AddCatalogFile", ex.Message);
    }

    // ---- Fail-fast: invalid initial catalog surfaces CatalogException on resolve --

    [Fact]
    public void AddKnowledge_does_not_throw_for_an_invalid_catalog_file()
    {
        using var root = new TempDir();
        var catalogPath = root.Write("catalog.json", """{ "version": 2, "sources": [] }""");

        var services = new ServiceCollection();
        var exception = Record.Exception(() => services.AddKnowledge(o => o.AddCatalogFile(catalogPath)));

        Assert.Null(exception);
    }

    [Fact]
    public void Resolving_IKnowledgeCatalog_for_an_invalid_catalog_file_throws_CatalogException()
    {
        using var root = new TempDir();
        var catalogPath = root.Write("catalog.json", """{ "version": 2, "sources": [] }""");

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(catalogPath));

        using var provider = services.BuildServiceProvider();

        Assert.Throws<CatalogException>(() => provider.GetRequiredService<IKnowledgeCatalog>());
    }

    [Fact]
    public void Resolving_IKnowledgeResolver_for_an_invalid_catalog_file_throws_CatalogException()
    {
        using var root = new TempDir();
        var catalogPath = root.Write("catalog.json", """{ "version": 2, "sources": [] }""");

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(catalogPath));

        using var provider = services.BuildServiceProvider();

        Assert.Throws<CatalogException>(() => provider.GetRequiredService<IKnowledgeResolver>());
    }

    // ---- Disposal: disposing the provider disposes the FileKnowledgeCatalog --

    [Fact]
    public async Task Disposing_the_provider_disposes_the_FileKnowledgeCatalog_so_it_stops_honoring_reloads()
    {
        using var root = new TempDir();
        var catalogPath = SetUpTwoSourceCatalogFile(root);

        var services = new ServiceCollection();
        services.AddKnowledge(o => o.AddCatalogFile(catalogPath));

        var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IKnowledgeCatalog>();
        Assert.Equal(1, catalog.Current.Generation);

        provider.Dispose();

        // A disposed FileKnowledgeCatalog stops honoring reloads (see
        // FileKnowledgeCatalogTests.No_reload_watcher_or_explicit_applies_after_dispose):
        // ReloadAsync becomes a no-op returning the last-known-good snapshot
        // unchanged. Replace the manifest with a valid, different snapshot
        // and confirm the disposed catalog never picks it up -- the
        // observable proof that the provider's Dispose() reached the
        // singleton FileKnowledgeCatalog's own Dispose().
        var tempFile = catalogPath + ".tmp";
        File.WriteAllText(tempFile, """
            {
              "version": 1,
              "sources": [ { "id": "hi", "path": "./source-hi", "priority": 10 } ]
            }
            """);
        File.Move(tempFile, catalogPath, overwrite: true);

        var result = await catalog.ReloadAsync();

        Assert.Equal(1, result.Generation);
        Assert.Equal(1, catalog.Current.Generation);
        Assert.Equal(2, catalog.Current.Sources.Count);
    }
}
