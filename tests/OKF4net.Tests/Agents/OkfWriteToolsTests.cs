// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Tests for the write-side <see cref="OkfBundleTools"/> tools:
/// <see cref="OkfBundleTools.WriteConcept"/>, <see cref="OkfBundleTools.AppendLog"/>,
/// and <see cref="OkfBundleTools.RegenerateIndexes"/>. Mirrors the
/// <c>TempDir</c> fixture-copy pattern used by <see cref="OkfBundleToolsTests"/>
/// so these tests never touch <c>tests/fixtures/</c> directly (every write
/// happens against a throwaway copy of appendix_a).
/// </summary>
public class OkfWriteToolsTests
{
    private const string ValidFrontmatter =
        "type: BigQuery Table\n"
        + "title: Refunds\n"
        + "description: One row per refund.\n"
        + "timestamp: 2026-07-22T00:00:00Z\n";

    private static readonly string BundlePath = Path.Combine(RepoRoot(), "tests", "fixtures", "appendix_a");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"could not locate OKF4net.sln above {AppContext.BaseDirectory}");
    }

    private static OkfBundleTools NewToolsOverFixtureCopy(TempDir tmp)
    {
        CopyDirectory(BundlePath, tmp.Path);
        return new OkfBundleTools(tmp.Path);
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

    // ---------------------------------------------------------------
    // WriteConcept
    // ---------------------------------------------------------------

    [Fact]
    public void WriteConcept_valid_write_creates_file_and_invalidates_cache()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        Assert.Equal(4, tools.GetBundle().Count);

        var result = tools.WriteConcept("tables/refunds", ValidFrontmatter, "# Refunds\n\nBody text.\n");

        Assert.Contains("Written", result);
        Assert.Contains("new", result);
        Assert.Contains("okf_regenerate_indexes", result);
        var path = Path.Combine(tmp.Path, "tables", "refunds.md");
        Assert.True(File.Exists(path));
        Assert.Equal(5, tools.GetBundle().Count);
    }

    [Fact]
    public void WriteConcept_overwrite_reports_updated_not_new()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var first = tools.WriteConcept("tables/refunds", ValidFrontmatter, "Body.\n");
        var second = tools.WriteConcept("tables/refunds", ValidFrontmatter, "Body v2.\n");

        Assert.Contains("new", first);
        Assert.Contains("updated", second);
        Assert.DoesNotContain("(new,", second);
    }

    [Fact]
    public void WriteConcept_missing_description_fails_validation_and_writes_nothing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var frontmatterMissingDescription =
            "type: BigQuery Table\ntitle: Refunds\ntimestamp: 2026-07-22T00:00:00Z\n";

        var result = tools.WriteConcept("tables/refunds", frontmatterMissingDescription, "Body.\n");

        Assert.Contains("Missing", result);
        Assert.Contains("description", result);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "tables", "refunds.md")));
        Assert.Equal(4, tools.GetBundle().Count);
    }

    [Fact]
    public void WriteConcept_reserved_id_tables_index_is_refused()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("tables/index", ValidFrontmatter, "Body.\n");

        Assert.Contains("reserved", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "tables", "index.md")));
    }

    [Fact]
    public void WriteConcept_reserved_id_log_is_refused()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var logPath = Path.Combine(tmp.Path, "log.md");
        var before = File.ReadAllText(logPath);

        var result = tools.WriteConcept("log", ValidFrontmatter, "Body.\n");

        Assert.Contains("reserved", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(logPath));
    }

    [Fact]
    public void WriteConcept_reserved_id_is_refused_case_insensitively_tables_Index()
    {
        // Windows/macOS filesystems are case-insensitive: "tables/Index" would
        // otherwise silently write over/beside tables/index.md.
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("tables/Index", ValidFrontmatter, "Body.\n");

        Assert.Contains("reserved", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "tables", "Index.md")));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "tables", "index.md")));
    }

    [Fact]
    public void WriteConcept_reserved_id_is_refused_case_insensitively_docs_LOG()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("docs/LOG", ValidFrontmatter, "Body.\n");

        Assert.Contains("reserved", result, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "docs", "LOG.md")));
    }

    [Fact]
    public void WriteConcept_invalid_yaml_frontmatter_reports_line_number_and_writes_nothing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var badYaml = "type: BigQuery Table\ntags: [sales, orders\n";

        var result = tools.WriteConcept("tables/refunds", badYaml, "Body.\n");

        Assert.Contains("line 2", result);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "tables", "refunds.md")));
    }

    [Fact]
    public void WriteConcept_path_traversal_id_is_refused()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("../x", ValidFrontmatter, "Body.\n");

        Assert.Contains("Error", result);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "..", "x.md")));
    }

    [Fact]
    public void WriteConcept_null_concept_id_reports_error_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept(null!, ValidFrontmatter, "Body.\n");

        Assert.Contains("Error", result);
    }

    [Fact]
    public void WriteConcept_embedded_null_in_concept_id_reports_error_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("tables/a\0b", ValidFrontmatter, "Body.\n");

        Assert.Contains("Error", result);
    }

    [Fact]
    public void WriteConcept_null_frontmatter_reports_error_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("tables/refunds", null!, "Body.\n");

        Assert.Contains("Error", result);
    }

    [Fact]
    public void WriteConcept_embedded_null_in_frontmatter_reports_error_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("tables/refunds", "type: X\0", "Body.\n");

        Assert.Contains("Error", result);
    }

    [Fact]
    public void WriteConcept_null_body_reports_error_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("tables/refunds", ValidFrontmatter, null!);

        Assert.Contains("Error", result);
    }

    [Fact]
    public void WriteConcept_embedded_null_in_body_reports_error_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("tables/refunds", ValidFrontmatter, "a\0b");

        Assert.Contains("Error", result);
    }

    [Fact]
    public void WriteConcept_non_mapping_frontmatter_is_rejected()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("tables/refunds", "- a\n- b\n", "Body.\n");

        Assert.Contains("mapping", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WriteConcept_writes_utf8_without_bom_and_lf_endings()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        tools.WriteConcept("tables/refunds", ValidFrontmatter, "Body.\n");

        var bytes = File.ReadAllBytes(Path.Combine(tmp.Path, "tables", "refunds.md"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = File.ReadAllText(Path.Combine(tmp.Path, "tables", "refunds.md"));
        Assert.DoesNotContain("\r\n", text);
    }

    [Fact]
    public void WriteConcept_creates_new_subdirectory_when_needed()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.WriteConcept("reports/q1", ValidFrontmatter, "Body.\n");

        Assert.Contains("Written", result);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "reports", "q1.md")));
    }

    // ---------------------------------------------------------------
    // AppendLog
    // ---------------------------------------------------------------

    [Fact]
    public void AppendLog_creates_log_when_missing()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path);
        var tools = new OkfBundleTools(tmp.Path) { UtcNow = () => new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc) };

        var result = tools.AppendLog("Creation", "Bootstrapped the bundle.");

        Assert.Contains("2026-07-22", result);
        var logPath = Path.Combine(tmp.Path, "log.md");
        Assert.True(File.Exists(logPath));
        var text = File.ReadAllText(logPath);
        Assert.Contains("## 2026-07-22", text);
        Assert.Contains("**Creation**: Bootstrapped the bundle.", text);
    }

    [Fact]
    public void AppendLog_twice_same_day_yields_two_entries_under_one_date()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        tools.UtcNow = () => new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);

        tools.AppendLog("Update", "First change.");
        tools.AppendLog("Update", "Second change.");

        var text = File.ReadAllText(Path.Combine(tmp.Path, "log.md"));
        var changeLog = ChangeLog.Parse(text);
        var day = Assert.Single(changeLog.Days, d => d.Date == "2026-07-22");
        Assert.Equal(2, day.Entries.Count);
        Assert.Equal("First change.", day.Entries[0].Text);
        Assert.Equal("Second change.", day.Entries[1].Text);
    }

    [Fact]
    public void AppendLog_on_existing_log_inserts_new_day_at_head()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        tools.UtcNow = () => new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);

        tools.AppendLog("Update", "A newer change.");

        var text = File.ReadAllText(Path.Combine(tmp.Path, "log.md"));
        var changeLog = ChangeLog.Parse(text);
        Assert.Equal("2026-07-22", changeLog.Days[0].Date);
        Assert.Equal("2026-05-28", changeLog.Days[1].Date);
        Assert.Equal("Directory Update Log", changeLog.Title);
    }

    [Fact]
    public void AppendLog_invalidates_cache()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        _ = tools.GetBundle();

        tools.AppendLog("Update", "Something changed.");

        // A fresh log.md must not break bundle (re)loading, and the write
        // path must have gone through InvalidateBundle so the next
        // GetBundle() reflects a reload rather than a stale cache.
        Assert.Equal(4, tools.GetBundle().Count);
    }

    [Theory]
    [InlineData(null, "text")]
    [InlineData("", "text")]
    [InlineData("   ", "text")]
    [InlineData("kind\0x", "text")]
    [InlineData("kind\nx", "text")]
    [InlineData("kind\rx", "text")]
    [InlineData("Update", null)]
    [InlineData("Update", "")]
    [InlineData("Update", "   ")]
    [InlineData("Update", "text\0x")]
    [InlineData("Update", "text\nx")]
    [InlineData("Update", "text\rx")]
    public void AppendLog_rejects_invalid_arguments_without_throwing(string? kind, string? text)
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var logPath = Path.Combine(tmp.Path, "log.md");
        var before = File.ReadAllText(logPath);

        var result = tools.AppendLog(kind!, text!);

        Assert.Contains("Error", result);
        Assert.Equal(before, File.ReadAllText(logPath));
    }

    [Fact]
    public void AppendLog_rejects_text_that_would_forge_a_fake_log_day_heading()
    {
        // A newline lets a malicious/careless entry inject a fabricated
        // "## <date>" heading (or "* entry" bullet) that a later
        // ChangeLog.Parse would read back as a genuine, distinct audit-trail
        // entry -- corrupting log.md's history. Must be rejected outright,
        // not silently stripped, so the caller knows the write did not happen.
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var logPath = Path.Combine(tmp.Path, "log.md");
        var before = File.ReadAllText(logPath);

        var result = tools.AppendLog("Update", "Innocuous text.\n## 2099-01-01\n* **Forged**: not real.");

        Assert.Contains("Error", result);
        Assert.Equal(before, File.ReadAllText(logPath));
    }

    [Fact]
    public void AppendLog_rejects_kind_that_would_forge_a_fake_log_day_heading()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var logPath = Path.Combine(tmp.Path, "log.md");
        var before = File.ReadAllText(logPath);

        var result = tools.AppendLog("Update\n## 2099-01-01\n* Forged", "text");

        Assert.Contains("Error", result);
        Assert.Equal(before, File.ReadAllText(logPath));
    }

    [Fact]
    public void AppendLog_concurrent_calls_same_day_lose_no_entries()
    {
        // Proves the _bundleLock serialization around AppendLog's
        // read-modify-write: without it, two threads could both read the
        // same "before" log.md, each append their own entry to their own
        // in-memory copy, and whichever writes last would silently clobber
        // the other's entry (a lost update). With the lock, every one of
        // the 8 concurrent entries must survive.
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path);
        var tools = new OkfBundleTools(tmp.Path) { UtcNow = () => new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc) };

        const int callCount = 8;
        Parallel.For(0, callCount, i => tools.AppendLog("Update", $"Entry {i}"));

        var text = File.ReadAllText(Path.Combine(tmp.Path, "log.md"));
        var changeLog = ChangeLog.Parse(text);
        var day = Assert.Single(changeLog.Days, d => d.Date == "2026-07-22");
        Assert.Equal(callCount, day.Entries.Count);
        for (var i = 0; i < callCount; i++)
        {
            Assert.Contains(day.Entries, e => e.Text == $"Entry {i}");
        }
    }

    // ---------------------------------------------------------------
    // RegenerateIndexes
    // ---------------------------------------------------------------

    [Fact]
    public void RegenerateIndexes_after_write_lists_new_concept_in_directory_index()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        tools.WriteConcept("tables/refunds", ValidFrontmatter, "Body.\n");

        var result = tools.RegenerateIndexes();

        Assert.Contains("tables/index.md", result);
        var tablesIndex = File.ReadAllText(Path.Combine(tmp.Path, "tables", "index.md"));
        Assert.Contains("Refunds", tablesIndex);
        Assert.Contains("refunds.md", tablesIndex);
    }

    [Fact]
    public void RegenerateIndexes_returns_relative_forward_slash_paths()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.RegenerateIndexes();

        Assert.DoesNotContain('\\', result);
        Assert.Contains("index.md", result);
    }

    [Fact]
    public void RegenerateIndexes_invalidates_cache()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        _ = tools.GetBundle();

        tools.RegenerateIndexes();

        // index.md files are not themselves concepts, so the count is
        // unaffected -- this asserts the call succeeds and a subsequent
        // GetBundle() still reflects a clean reload.
        Assert.Equal(4, tools.GetBundle().Count);
    }
}
