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

    /// <summary>
    /// The READ side of the same boundary. <c>okf_append_log</c> now refuses a
    /// control character or a bidi control at the write, but a <c>log.md</c>
    /// written by an older build, by another producer or by hand still carries
    /// whatever it carries -- and <c>okf_changes_since</c> passed it straight
    /// through to the model and to whatever renders the tool result.
    ///
    /// There is nothing to refuse on a read (the file exists, and the entries
    /// are a human's words), so the rendering ESCAPES instead: each offending
    /// character is replaced by its own <c>&lt;U+XXXX&gt;</c> name. That keeps
    /// the line honest -- every word a human wrote survives, nothing is
    /// silently dropped -- while nothing left in it can reorder the display or
    /// drive a terminal. It is also the form a reader can act on: seeing
    /// <c>&lt;U+202E&gt;</c> in an audit trail tells them exactly what is in
    /// the file and that someone put it there.
    ///
    /// Numeric constants, not literals, for every payload.
    ///
    /// Post-audit re-review, Minor 4: also plants a KIND-LESS entry (no
    /// <c>**Kind**:</c>) so <c>AppendLogDay</c>'s other branch
    /// (<c>entry.Kind is null</c>) is exercised too -- previously only the
    /// <c>Kind</c>-bearing branch was pinned, so reverting the kind-less
    /// branch alone back to the un-escaping <c>OneLine</c> left this test
    /// green.
    /// </summary>
    [Fact]
    public void ChangesSince_escapes_control_and_bidi_characters_a_log_already_carries()
    {
        var esc = (char)0x001B;
        var rlo = (char)0x202E;
        var isolate = (char)0x2067;
        var vtab = (char)0x000B;
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        tmp.Write(
            "log.md",
            "# Directory Update Log\n\n## 2026-06-15\n"
            + "* **Upd" + esc + "[2Kate**: real" + rlo + "text" + isolate + " and" + vtab + "more.\n"
            + "* plain" + esc + "entry" + rlo + "text.\n");
        tools.InvalidateBundle();

        var result = tools.ChangesSince("2020-01-01");

        // Nothing that reorders the display or drives a terminal survives.
        Assert.DoesNotContain(esc.ToString(), result, StringComparison.Ordinal);
        Assert.DoesNotContain(rlo.ToString(), result, StringComparison.Ordinal);
        Assert.DoesNotContain(isolate.ToString(), result, StringComparison.Ordinal);
        Assert.DoesNotContain(vtab.ToString(), result, StringComparison.Ordinal);
        // Each one is NAMED rather than dropped: the whole entry, both fields,
        // reads back with every human-written word intact.
        Assert.Contains(
            "- **Upd<U+001B>[2Kate**: real<U+202E>text<U+2067> and<U+000B>more.",
            result,
            StringComparison.Ordinal);
        // The kind-less branch (no "**Kind**:") gets the same treatment.
        Assert.Contains(
            "- plain<U+001B>entry<U+202E>text.",
            result,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// And the escaping must not touch anything else: accents, CJK, a non-BMP
    /// emoji (a surrogate pair), right-to-left TEXT and a tab all read back
    /// verbatim. A tab is deliberately exempt on this side too, matching the
    /// write (see <c>OkfWriteToolsTests.AppendLog_accepts_a_tab_and_writes_it_verbatim</c>).
    /// </summary>
    [Fact]
    public void ChangesSince_leaves_ordinary_content_including_rtl_and_a_tab_untouched()
    {
        var tab = (char)0x0009;
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var text = "日本語 " + char.ConvertFromUtf32(0x1F680) + " تحديث" + tab + "עדכון (fusée).";
        tmp.Write("log.md", "# Directory Update Log\n\n## 2026-06-15\n* **Mise à jour**: " + text + "\n");
        tools.InvalidateBundle();

        var result = tools.ChangesSince("2020-01-01");

        Assert.Contains("- **Mise à jour**: " + text, result, StringComparison.Ordinal);
        Assert.DoesNotContain("<U+", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Post-audit re-review, Important 1: the per-log <c>## {path}</c> heading
    /// rendered the log file's RELATIVE PATH through <c>OneLine</c> alone —
    /// two lines away from the <c>OneLineLogText</c> escaper the bidi/tab/
    /// read-escape commit introduced for entry content — so a subdirectory
    /// name carrying a bidi control (accepted by NTFS with no privilege
    /// needed) reached the model, and a terminal or markdown viewer, raw. An
    /// unterminated override reverses the remainder of the rendered line, so
    /// the path a human is shown is not the path on disk: the same forgery
    /// <c>okf_append_log</c>'s write guard exists to prevent, reached through
    /// a sibling line instead of the guarded field.
    ///
    /// A path is not log entry text, but it is text this library did not
    /// write — a caller, another producer or the filesystem can name a
    /// directory however it likes — which is exactly the class
    /// <c>OneLineLogText</c> exists to make inert (fold soft separators,
    /// name every remaining control/bidi character visibly, touch nothing
    /// else). That is why the fix reuses it rather than adding a third
    /// escaper: this line needs precisely the same property the entry lines
    /// already have, no more and no less.
    /// </summary>
    [Fact]
    public void ChangesSince_escapes_a_bidi_control_in_the_logs_relative_path()
    {
        var rlo = (char)0x202E;
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        tmp.Write("sub" + rlo + "dir/log.md", "# Directory Update Log\n\n## 2026-06-15\n* **Update**: entry.\n");
        tools.InvalidateBundle();

        var result = tools.ChangesSince("2020-01-01");

        Assert.DoesNotContain(rlo.ToString(), result, StringComparison.Ordinal);
        Assert.Contains("## sub<U+202E>dir/log.md", result, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same Important-1 gap, the sibling sink: the <c>&gt; Skipped {path}</c>
    /// note printed when a log file fails to decode also rendered the
    /// relative path through <c>OneLine</c> alone.
    /// </summary>
    [Fact]
    public void ChangesSince_escapes_a_bidi_control_in_a_skipped_logs_relative_path()
    {
        var rlo = (char)0x202E;
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        var badDir = Path.Combine(tmp.Path, "sub" + rlo + "dir");
        Directory.CreateDirectory(badDir);
        File.WriteAllBytes(Path.Combine(badDir, "log.md"), [0x23, 0x20, 0xFF, 0xFE, 0x0A]);
        tools.InvalidateBundle();

        var result = tools.ChangesSince("2020-01-01");

        Assert.DoesNotContain(rlo.ToString(), result, StringComparison.Ordinal);
        Assert.Contains("Skipped sub<U+202E>dir/log.md", result, StringComparison.Ordinal);
    }
}
