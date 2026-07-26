// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// <see cref="FileKnowledgeCatalog"/>: construction fail-fast vs. runtime
/// errors-as-data reloads, atomic snapshot swap, debounced best-effort
/// watcher, and disposal. Reload assertions mostly drive
/// <see cref="IKnowledgeCatalog.ReloadAsync"/> explicitly for determinism;
/// the watcher itself is covered separately with a tolerant, polling
/// assertion (it is documented as best-effort -- see
/// <see cref="FileKnowledgeCatalog"/>'s remarks).
/// </summary>
public class FileKnowledgeCatalogTests
{
    private const string OneSourceJson = """
        { "version": 1, "sources": [ { "id": "docs", "path": "./docs" } ] }
        """;

    private const string TwoSourceJson = """
        {
          "version": 1,
          "sources": [
            { "id": "docs", "path": "./docs" },
            { "id": "more", "path": "./more" }
          ]
        }
        """;

    private const string MalformedJson = "{ this is not valid json";

    private const string InvalidVersionJson = """{ "version": 2, "sources": [ { "id": "docs", "path": "./docs" } ] }""";

    private static string SetUpCatalogDirectory(TempDir temp)
    {
        Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));
        return temp.Write("catalog.json", OneSourceJson);
    }

    /// <summary>Atomically replaces the manifest content via write-to-temp then move-over (matches the brief's "temp file then File.Move/replace" recipe).</summary>
    private static void ReplaceCatalogAtomically(string catalogPath, string newJson)
    {
        var tempFile = catalogPath + ".tmp";
        File.WriteAllText(tempFile, newJson);
        File.Move(tempFile, catalogPath, overwrite: true);
    }

    // ---- Construction ------------------------------------------------

    [Fact]
    public void Valid_construction_publishes_sources_at_generation_1()
    {
        using var temp = new TempDir();
        var catalogPath = SetUpCatalogDirectory(temp);

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        });

        Assert.Equal(1, catalog.Current.Generation);
        var source = Assert.Single(catalog.Current.Sources);
        Assert.Equal("docs", source.Id);
        Assert.Empty(catalog.LastReloadDiagnostics);
    }

    [Fact]
    public void Invalid_initial_catalog_throws_CatalogException()
    {
        using var temp = new TempDir();
        var catalogPath = temp.Write("catalog.json", InvalidVersionJson);

        var options = new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        };

        var ex = Assert.Throws<CatalogException>(() => new FileKnowledgeCatalog(options));
        Assert.Contains("WrongVersion", ex.Message);
    }

    [Fact]
    public void Initial_catalog_with_source_path_escaping_root_throws_CatalogException()
    {
        using var temp = new TempDir();
        using var outsideRoot = new TempDir();
        Directory.CreateDirectory(Path.Combine(outsideRoot.Path, "elsewhere"));

        var json = $$"""
            { "version": 1, "sources": [ { "id": "outside", "path": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(outsideRoot.Path, "elsewhere"))}} } ] }
            """;
        var catalogPath = temp.Write("catalog.json", json);

        var options = new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        };

        var ex = Assert.Throws<CatalogException>(() => new FileKnowledgeCatalog(options));
        Assert.Contains("AbsolutePath", ex.Message);
    }

    // ---- Explicit ReloadAsync: valid replacement ----------------------

    [Fact]
    public async Task Valid_atomic_replacement_swaps_current_and_increments_generation()
    {
        using var temp = new TempDir();
        var catalogPath = SetUpCatalogDirectory(temp);
        Directory.CreateDirectory(Path.Combine(temp.Path, "more"));

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        });
        Assert.Equal(1, catalog.Current.Generation);

        ReplaceCatalogAtomically(catalogPath, TwoSourceJson);

        var result = await catalog.ReloadAsync();

        Assert.Equal(2, result.Generation);
        Assert.Equal(2, catalog.Current.Generation);
        Assert.Equal(2, catalog.Current.Sources.Count);
        Assert.Equal(["docs", "more"], catalog.Current.Sources.Select(s => s.Id));
        Assert.Empty(catalog.LastReloadDiagnostics);
        Assert.Same(result, catalog.Current);
    }

    [Fact]
    public async Task Repeated_valid_reloads_increment_generation_each_time()
    {
        using var temp = new TempDir();
        var catalogPath = SetUpCatalogDirectory(temp);
        Directory.CreateDirectory(Path.Combine(temp.Path, "more"));

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        });

        ReplaceCatalogAtomically(catalogPath, TwoSourceJson);
        await catalog.ReloadAsync();
        Assert.Equal(2, catalog.Current.Generation);

        ReplaceCatalogAtomically(catalogPath, OneSourceJson);
        await catalog.ReloadAsync();
        Assert.Equal(3, catalog.Current.Generation);
        Assert.Single(catalog.Current.Sources);
    }

    // ---- Explicit ReloadAsync: malformed replacement (errors-as-data) --

    [Fact]
    public async Task Malformed_replacement_keeps_last_good_current_and_populates_diagnostics()
    {
        using var temp = new TempDir();
        var catalogPath = SetUpCatalogDirectory(temp);

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        });
        var goodSnapshot = catalog.Current;

        ReplaceCatalogAtomically(catalogPath, MalformedJson);

        var result = await catalog.ReloadAsync();

        Assert.Same(goodSnapshot, result);
        Assert.Same(goodSnapshot, catalog.Current);
        Assert.Equal(1, catalog.Current.Generation);
        Assert.NotEmpty(catalog.LastReloadDiagnostics);
    }

    [Fact]
    public async Task Reload_with_source_path_escaping_root_is_rejected_atomically()
    {
        using var temp = new TempDir();
        using var outsideRoot = new TempDir();
        Directory.CreateDirectory(Path.Combine(outsideRoot.Path, "elsewhere"));
        var catalogPath = SetUpCatalogDirectory(temp);

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        });

        var badJson = $$"""
            {
              "version": 1,
              "sources": [
                { "id": "docs", "path": "./docs" },
                { "id": "outside", "path": {{System.Text.Json.JsonSerializer.Serialize(Path.Combine(outsideRoot.Path, "elsewhere"))}} }
              ]
            }
            """;
        ReplaceCatalogAtomically(catalogPath, badJson);

        var result = await catalog.ReloadAsync();

        Assert.Equal(1, result.Generation);
        Assert.Single(catalog.Current.Sources);
        Assert.Contains(catalog.LastReloadDiagnostics, d => d.Code == CatalogDiagnosticCode.AbsolutePath);
    }

    [Fact]
    public async Task Successful_reload_after_a_failed_one_clears_diagnostics()
    {
        using var temp = new TempDir();
        var catalogPath = SetUpCatalogDirectory(temp);
        Directory.CreateDirectory(Path.Combine(temp.Path, "more"));

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        });

        ReplaceCatalogAtomically(catalogPath, MalformedJson);
        await catalog.ReloadAsync();
        Assert.NotEmpty(catalog.LastReloadDiagnostics);

        ReplaceCatalogAtomically(catalogPath, TwoSourceJson);
        var result = await catalog.ReloadAsync();

        Assert.Equal(2, result.Generation);
        Assert.Empty(catalog.LastReloadDiagnostics);
    }

    // ---- Watcher: debounced, best-effort ------------------------------

    [Fact]
    public async Task Watcher_fires_debounced_reload_on_atomic_replacement()
    {
        using var temp = new TempDir();
        var catalogPath = SetUpCatalogDirectory(temp);
        Directory.CreateDirectory(Path.Combine(temp.Path, "more"));

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = true,
            ReloadDebounce = TimeSpan.FromMilliseconds(50),
        });
        Assert.Equal(1, catalog.Current.Generation);

        ReplaceCatalogAtomically(catalogPath, TwoSourceJson);

        // Best-effort: poll with a generous timeout rather than asserting an
        // exact event count or timing. If this ever flakes in CI, that is
        // itself evidence of the watcher's documented best-effort nature --
        // ReloadAsync (exercised deterministically by the tests above)
        // remains the reliable path.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (catalog.Current.Generation < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.True(catalog.Current.Generation >= 2, "Watcher-triggered reload did not observe the change within the timeout.");
        Assert.Equal(2, catalog.Current.Sources.Count);
        Assert.Empty(catalog.LastReloadDiagnostics);
    }

    [Fact]
    public async Task Rapid_burst_of_replacements_does_not_corrupt_state()
    {
        using var temp = new TempDir();
        var catalogPath = SetUpCatalogDirectory(temp);
        Directory.CreateDirectory(Path.Combine(temp.Path, "more"));

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = true,
            ReloadDebounce = TimeSpan.FromMilliseconds(100),
        });

        // Fire a burst of alternating valid replacements well within one
        // debounce window each; whatever the watcher's OS-level event count
        // turns out to be, the published Current must always be one of the
        // two well-formed variants written below -- never a torn mix of the
        // two, and the diagnostics must always be consistent with whichever
        // snapshot ended up published.
        for (var i = 0; i < 5; i++)
        {
            ReplaceCatalogAtomically(catalogPath, i % 2 == 0 ? TwoSourceJson : OneSourceJson);
            await Task.Delay(10);
        }

        // Drive a final, deterministic reload explicitly so the assertions
        // below do not themselves race the watcher (per guidance: prefer
        // ReloadAsync for deterministic end-state checks).
        var final = await catalog.ReloadAsync();

        Assert.True(final.Generation >= 1);
        Assert.Empty(catalog.LastReloadDiagnostics);
        Assert.True(final.Sources.Count is 1 or 2);
        Assert.Same(final, catalog.Current);
    }

    // ---- Dispose -------------------------------------------------------

    [Fact]
    public void Dispose_is_idempotent()
    {
        using var temp = new TempDir();
        var catalogPath = SetUpCatalogDirectory(temp);

        var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = true,
        });

        catalog.Dispose();
        var exception = Record.Exception(() => catalog.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public async Task No_reload_watcher_or_explicit_applies_after_dispose()
    {
        using var temp = new TempDir();
        var catalogPath = SetUpCatalogDirectory(temp);
        Directory.CreateDirectory(Path.Combine(temp.Path, "more"));

        var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = true,
            ReloadDebounce = TimeSpan.FromMilliseconds(20),
        });
        Assert.Equal(1, catalog.Current.Generation);

        catalog.Dispose();

        ReplaceCatalogAtomically(catalogPath, TwoSourceJson);
        await Task.Delay(300); // give a stray watcher callback every chance to misbehave

        Assert.Equal(1, catalog.Current.Generation);

        var result = await catalog.ReloadAsync();
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, catalog.Current.Generation);
        Assert.Single(catalog.Current.Sources);
    }
}
