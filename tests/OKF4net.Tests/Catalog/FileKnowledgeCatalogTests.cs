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

    /// <summary>Otherwise-valid JSON bytes with a trailing byte (0xFF) that is not valid UTF-8 on its own or as a continuation -- forces <c>OkfEncodings.Strict</c>'s decode to throw.</summary>
    private static readonly byte[] InvalidUtf8Bytes = [.. System.Text.Encoding.UTF8.GetBytes(OneSourceJson), 0xFF];

    private static string SetUpCatalogDirectory(TempDir temp)
    {
        Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));
        return temp.Write("catalog.json", OneSourceJson);
    }

    /// <summary>
    /// Atomically replaces the manifest content via write-to-temp then
    /// move-over (matches the brief's "temp file then File.Move/replace"
    /// recipe). Retries a bounded number of times on a transient Windows
    /// sharing violation: even with a reader opened with generous FileShare
    /// flags, a concurrent read of <paramref name="catalogPath"/> (this
    /// test's own watcher-triggered reload) can transiently overlap
    /// <see cref="File.Move(string, string, bool)"/>'s replace -- exactly
    /// what a real external editor/deploy-tool replacing the file underneath
    /// a live reader has to tolerate, so the test's stand-in for that
    /// external writer does too.
    /// </summary>
    private static void ReplaceCatalogAtomically(string catalogPath, string newJson)
    {
        const int maxAttempts = 20;
        var tempFile = catalogPath + ".tmp";
        File.WriteAllText(tempFile, newJson);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                File.Move(tempFile, catalogPath, overwrite: true);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < maxAttempts)
            {
                Thread.Sleep(5);
            }
        }
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

    /// <summary>
    /// Isolates the disabled-source skip in <c>TryLoadSnapshot</c> (<c>if
    /// (!source.Enabled) continue;</c> before <c>CatalogPathResolver.TryResolve</c>)
    /// from <see cref="CatalogPathResolver"/>'s own missing-directory
    /// rejection: this and <see cref="An_enabled_source_with_the_same_nonexistent_path_fails_construction"/>
    /// differ only in the "ghost" source's <c>enabled</c> flag, so together
    /// they prove it is specifically the disabled skip -- not some other
    /// reason a nonexistent path might be tolerated -- that lets construction
    /// succeed here.
    /// </summary>
    [Fact]
    public void Disabled_source_with_a_nonexistent_path_does_not_fail_construction()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));
        var catalogPath = temp.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "docs", "path": "./docs" },
                { "id": "ghost", "path": "./does-not-exist", "enabled": false }
              ]
            }
            """);

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        });

        Assert.Equal(2, catalog.Current.Sources.Count);
        var ghost = catalog.Current.Sources.Single(s => s.Id == "ghost");
        Assert.False(ghost.Enabled);
    }

    /// <summary>Companion to <see cref="Disabled_source_with_a_nonexistent_path_does_not_fail_construction"/> -- see that test's remarks.</summary>
    [Fact]
    public void An_enabled_source_with_the_same_nonexistent_path_fails_construction()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));
        var catalogPath = temp.Write("catalog.json", """
            {
              "version": 1,
              "sources": [
                { "id": "docs", "path": "./docs" },
                { "id": "ghost", "path": "./does-not-exist", "enabled": true }
              ]
            }
            """);

        var options = new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        };

        Assert.Throws<CatalogException>(() => new FileKnowledgeCatalog(options));
    }

    /// <summary>
    /// F9 regression: a strict-UTF8 <c>File.ReadAllBytes</c> + <c>GetString</c>
    /// decode (adopted to reject genuinely invalid UTF-8, unlike the old
    /// <c>File.ReadAllText</c>) does NOT strip a leading U+FEFF byte-order
    /// mark the way <c>File.ReadAllText</c> used to -- so a BOM-prefixed
    /// <c>catalog.json</c> (common from some editors/tools on Windows) would
    /// otherwise fail to parse as JSON (<c>JsonDocument.Parse</c> chokes on
    /// the leading U+FEFF) even though the manifest is perfectly valid.
    /// </summary>
    [Fact]
    public void Bom_prefixed_valid_catalog_loads_successfully()
    {
        using var temp = new TempDir();
        Directory.CreateDirectory(Path.Combine(temp.Path, "docs"));
        var catalogPath = Path.Combine(temp.Path, "catalog.json");
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var jsonBytes = System.Text.Encoding.UTF8.GetBytes(OneSourceJson);
        File.WriteAllBytes(catalogPath, [.. bom, .. jsonBytes]);

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
    public void Invalid_initial_catalog_utf8_throws_CatalogException()
    {
        using var temp = new TempDir();
        var catalogPath = Path.Combine(temp.Path, "catalog.json");
        File.WriteAllBytes(catalogPath, InvalidUtf8Bytes);

        var options = new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        };

        var ex = Assert.Throws<CatalogException>(() => new FileKnowledgeCatalog(options));
        Assert.Contains("Could not read catalog file", ex.Message);
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
    public async Task Invalid_utf8_replacement_keeps_last_good_current_and_populates_diagnostics()
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

        var tempFile = catalogPath + ".tmp";
        File.WriteAllBytes(tempFile, InvalidUtf8Bytes);
        File.Move(tempFile, catalogPath, overwrite: true);

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

    /// <summary>
    /// F4: <see cref="FileKnowledgeCatalog.LastReloadDiagnostics"/>'s path-escape
    /// rejection path (<c>pathDiagnostics</c> in <c>TryLoadSnapshot</c>) must be
    /// just as genuinely read-only as the read-failure path already is (that one
    /// uses <c>Array.AsReadOnly</c>) -- otherwise a caller could downcast and
    /// mutate published diagnostics out from under the catalog.
    /// </summary>
    [Fact]
    public async Task Reload_diagnostics_from_a_source_path_escape_cannot_be_downcast_to_a_mutable_list()
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

        await catalog.ReloadAsync();
        Assert.NotEmpty(catalog.LastReloadDiagnostics);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (List<CatalogDiagnostic>)catalog.LastReloadDiagnostics;
            mutable.Clear();
        });

        Assert.IsType<InvalidCastException>(castAttempt);
    }

    /// <summary>
    /// F4 (extended): a <em>syntactically invalid</em> reload publishes the
    /// parser's early-exit diagnostics verbatim through
    /// <see cref="FileKnowledgeCatalog.LastReloadDiagnostics"/>. Those early
    /// exits (malformed JSON / null / non-object root) previously returned the
    /// raw mutable <c>List&lt;CatalogDiagnostic&gt;</c>, so a caller could
    /// downcast and clear the published diagnostics after a bad reload. They
    /// now wrap identically to the validation and path-escape paths.
    /// </summary>
    [Fact]
    public async Task Reload_diagnostics_from_malformed_json_cannot_be_downcast_to_a_mutable_list()
    {
        using var temp = new TempDir();
        var catalogPath = SetUpCatalogDirectory(temp);

        using var catalog = new FileKnowledgeCatalog(new KnowledgeCatalogOptions
        {
            CatalogFilePath = catalogPath,
            CatalogRoot = temp.Path,
            WatchForChanges = false,
        });

        ReplaceCatalogAtomically(catalogPath, MalformedJson);
        await catalog.ReloadAsync();
        Assert.NotEmpty(catalog.LastReloadDiagnostics);

        var castAttempt = Record.Exception(() =>
        {
            var mutable = (List<CatalogDiagnostic>)catalog.LastReloadDiagnostics;
            mutable.Clear();
        });

        Assert.IsType<InvalidCastException>(castAttempt);
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
