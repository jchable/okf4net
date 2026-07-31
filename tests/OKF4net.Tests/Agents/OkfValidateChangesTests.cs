// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Tests for <see cref="OkfBundleTools.ValidateBundle"/> and
/// <see cref="OkfBundleTools.ChangesSince"/>: the diagnostics-report
/// rendering (mirroring the CLI's <c>validate</c> wording), the date-grouped
/// change summary aggregated from every <c>log.md</c>, and the never-throws
/// guard behaviour for adversarial input. Mirrors the <c>TempDir</c>
/// fixture-copy pattern used by <see cref="OkfSearchTests"/> so these tests
/// never touch <c>tests/fixtures/</c> directly.
/// </summary>
public class OkfValidateChangesTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

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

    // ----- ValidateBundle -----------------------------------------------

    [Fact]
    public void ValidateBundle_reports_warning_for_users_md_missing_recommended_fields()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ValidateBundle();

        Assert.Contains("[warning]", result);
        Assert.Contains("users.md", result);
    }

    [Fact]
    public void ValidateBundle_reports_conformant_for_fixture_with_no_errors()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ValidateBundle();

        Assert.Contains("conformant", result);
        Assert.Contains("0 error(s)", result);
        Assert.DoesNotContain("[error]", result);
    }

    [Fact]
    public void ValidateBundle_reports_error_and_nonconformant_when_type_is_missing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        tmp.Write("tables/broken.md", "---\ntitle: Broken\n---\n\nNo type here.\n");
        tools.InvalidateBundle();

        var result = tools.ValidateBundle();

        Assert.Contains("[error]", result);
        Assert.Contains("not conformant", result);
        Assert.DoesNotContain("0 error(s)", result);
    }

    [Fact]
    public void ValidateBundle_reports_error_and_nonconformant_for_malformed_reserved_file()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        tmp.Write("tables/index.md", "---\ntitle: nope\n---\n\n# Listing\n");
        tools.InvalidateBundle();

        var result = tools.ValidateBundle();

        Assert.Contains("[error]", result);
        Assert.Contains("not conformant", result);
        Assert.DoesNotContain("0 error(s)", result);
    }

    [Fact]
    public void ValidateBundle_never_throws_when_bundle_root_disappears()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        tools.GetBundle(); // force initial load/cache
        Directory.Delete(tmp.Path, recursive: true);
        tools.InvalidateBundle();

        var result = tools.ValidateBundle();

        Assert.Contains("Error", result);
    }

    // ----- ChangesSince ---------------------------------------------------

    [Fact]
    public void ChangesSince_lists_fixture_log_entries_when_date_is_far_in_the_past()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ChangesSince("2020-01-01");

        Assert.Contains("log.md", result);
        Assert.Contains("2026-05-28", result);
        Assert.Contains("Creation", result);
        Assert.Contains("Established the sales dataset and its orders/customers tables.", result);
    }

    [Fact]
    public void ChangesSince_uses_inclusive_boundary_on_the_exact_day()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ChangesSince("2026-05-28");

        Assert.Contains("2026-05-28", result);
    }

    [Fact]
    public void ChangesSince_reports_no_changes_for_a_future_date()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ChangesSince("2999-01-01");

        Assert.Equal("No changes since 2999-01-01.", result);
    }

    [Fact]
    public void ChangesSince_excludes_entries_strictly_before_the_given_date()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ChangesSince("2026-05-29");

        Assert.Equal("No changes since 2026-05-29.", result);
    }

    [Fact]
    public void ChangesSince_reports_usage_message_for_an_invalid_date_string()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ChangesSince("pas-une-date");

        Assert.Contains("Usage", result);
    }

    [Fact]
    public void ChangesSince_reports_usage_message_for_a_null_date_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ChangesSince(null!);

        Assert.Contains("Usage", result);
    }

    [Fact]
    public void ChangesSince_reports_usage_message_for_a_blank_date_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ChangesSince("   ");

        Assert.Contains("Usage", result);
    }

    [Fact]
    public void ChangesSince_rejects_embedded_null_character_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ChangesSince("2020-01-0\0");

        Assert.Contains("Error", result);
    }

    [Fact]
    public void ChangesSince_reports_usage_message_for_garbage_date_without_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.ChangesSince("!!!not-a-date!!!");

        Assert.Contains("Usage", result);
    }

    [Fact]
    public void ChangesSince_groups_entries_under_a_relative_log_path_with_forward_slashes()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "datasets", "nested"));
        tmp.Write(
            "datasets/nested/log.md",
            "# Directory Update Log\n\n## 2026-06-01\n* **Update**: Nested change.\n");
        tools.InvalidateBundle();

        var result = tools.ChangesSince("2020-01-01");

        Assert.Contains("datasets/nested/log.md", result);
        Assert.DoesNotContain("datasets\\nested\\log.md", result);
    }

    [Fact]
    public void ChangesSince_skips_a_non_utf8_log_file_with_a_note_instead_of_throwing()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var badLogPath = Path.Combine(tmp.Path, "log.md");
        File.WriteAllBytes(badLogPath, [0x23, 0x20, 0xFF, 0xFE, 0x0A]);
        tools.InvalidateBundle();

        var result = tools.ChangesSince("2020-01-01");

        Assert.DoesNotContain("Established the sales dataset", result);
        // Permissive: the malformed file is skipped, not fatal — no exception
        // — but the skip note must survive even though nothing else matched
        // (it is the only content source in this bundle).
        Assert.Contains("Skipped", result);
        Assert.Contains("log.md", result);
        Assert.Contains("No changes since 2020-01-01.", result);
    }

    [Fact]
    public void ChangesSince_preserves_skip_note_alongside_matching_days_from_another_log()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        File.WriteAllBytes(Path.Combine(tmp.Path, "log.md"), [0x23, 0x20, 0xFF, 0xFE, 0x0A]);
        Directory.CreateDirectory(Path.Combine(tmp.Path, "datasets"));
        tmp.Write(
            "datasets/log.md",
            "# Directory Update Log\n\n## 2026-06-01\n* **Update**: Nested change.\n");
        tools.InvalidateBundle();

        var result = tools.ChangesSince("2020-01-01");

        Assert.Contains("Skipped", result);
        Assert.Contains("datasets/log.md", result);
        Assert.Contains("Nested change.", result);
    }

    [Fact]
    public void ChangesSince_excludes_non_iso_day_headings_regardless_of_since_date()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        tmp.Write(
            "log.md",
            "# Directory Update Log\n\n## Notes\n* **Update**: Not a real date heading.\n\n## 2026-05-28\n* **Creation**: Established the sales dataset and its orders/customers tables.\n");
        tools.InvalidateBundle();

        var farPast = tools.ChangesSince("2000-01-01");
        var farFuture = tools.ChangesSince("2999-01-01");

        Assert.DoesNotContain("Not a real date heading", farPast);
        Assert.DoesNotContain("## Notes", farPast);
        Assert.DoesNotContain("Not a real date heading", farFuture);
        Assert.DoesNotContain("Notes", farFuture);
    }

    [Fact]
    public void ChangesSince_renders_multiple_matching_days_in_one_log_newest_first()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        tmp.Write(
            "log.md",
            "# Directory Update Log\n\n"
            + "## 2026-06-15\n* **Update**: Third entry.\n\n"
            + "## 2026-05-28\n* **Creation**: Established the sales dataset and its orders/customers tables.\n\n"
            + "## 2026-06-01\n* **Update**: Second entry.\n");
        tools.InvalidateBundle();

        var result = tools.ChangesSince("2020-01-01");

        var i0615 = result.IndexOf("2026-06-15", StringComparison.Ordinal);
        var i0601 = result.IndexOf("2026-06-01", StringComparison.Ordinal);
        var i0528 = result.IndexOf("2026-05-28", StringComparison.Ordinal);

        Assert.True(i0615 >= 0 && i0601 >= 0 && i0528 >= 0);
        Assert.True(i0615 < i0601, "2026-06-15 must render before 2026-06-01 (descending order).");
        Assert.True(i0601 < i0528, "2026-06-01 must render before 2026-05-28 (descending order).");
    }
}
