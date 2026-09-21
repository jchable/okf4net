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

    /// <summary>
    /// U+2028 LINE SEPARATOR, written as a numeric constant on purpose: a
    /// literal one in source is invisible in every editor and diff that would
    /// have to review the payload, exactly as
    /// <c>Internal/LineSafeText.cs</c> says of its own two. Same convention
    /// (and same reason) as <see cref="OkfComputationToolsTests"/>'s copy.
    /// </summary>
    private const char LineSeparator = (char)0x2028;

    /// <summary>
    /// Every terminator <c>OkfBundleTools.OneLine</c> folds, beyond <c>\r</c>.
    /// Splitting an assertion's input on all of them is what makes "one line"
    /// mean what it says: an LF-only split cannot see a forged line that a
    /// markdown or JavaScript splitter downstream would. U+000B (VT) is
    /// deliberately absent -- <c>ReplaceLineEndings</c> does not fold it, and
    /// no such splitter treats it as a break.
    /// </summary>
    private static readonly char[] EveryLineTerminator =
        ['\n', '\r', LineSeparator, (char)0x2029, (char)0x0085, (char)0x000C];

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

    // A junction/symlink placed INSIDE the bundle (e.g. bundleRoot/linked)
    // can point at an arbitrary external directory. The lexical containment
    // check (IsWithinBundleRoot) alone would accept "linked/x" -- it still
    // resolves to a path string under bundleRoot -- but the OS follows the
    // reparse point the moment WriteConcept actually touches disk
    // (Directory.CreateDirectory/File.WriteAllText), escaping the bundle.
    // See OkfBundleToolsTests for the Browse counterpart. Requires
    // reparse-point-creation privilege (a Windows junction via mklink /J
    // needs none; the Directory.CreateSymbolicLink fallback does) and, when
    // neither mechanism is available, SKIPS via Xunit.SkippableFact so the run
    // summary shows it. It used to return early on
    // TryCreateJunctionToExternalDir's bool instead, which counted as PASSED
    // while verifying nothing.
    [SkippableFact]
    public void WriteConcept_refuses_to_write_through_a_junction_and_leaves_the_external_dir_empty()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        using var external = new TempDir();

        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("linked", external.Path), "no junction/symlink privilege on this machine");

        var result = tools.WriteConcept("linked/x", ValidFrontmatter, "Body.\n");

        Assert.Contains("Error", result);
        Assert.Empty(Directory.GetFiles(external.Path));
    }

    // The junction test above covers a reparse-point ANCESTOR directory;
    // this covers the target FILE itself being a reparse point (e.g. an
    // existing "concept" that is actually a planted symlink to an external
    // file). HasReparsePointAncestor only walks directory ancestors of the
    // target path -- it never inspects the leaf path itself -- so
    // WriteConcept has a separate ReparsePoints.IsReparsePoint(targetPath)
    // check for this case; without it, File.WriteAllText would follow the
    // link and silently overwrite the external file. Requires
    // symlink-creation privilege and skips itself (Skip.IfNot on
    // TryCreateFileSymlinkToExternalFile's bool return) when unavailable.
    [SkippableFact]
    public void WriteConcept_refuses_to_overwrite_a_target_that_is_itself_a_symlink()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        using var external = new TempDir();
        var externalFile = external.Write("secret.md", "do not touch\n");

        Skip.IfNot(tmp.TryCreateFileSymlinkToExternalFile("tables/x.md", externalFile), "no symlink privilege on this machine");

        var result = tools.WriteConcept("tables/x", ValidFrontmatter, "Body.\n");

        Assert.Contains("Error", result);
        Assert.Equal("do not touch\n", File.ReadAllText(externalFile));
    }

    // Finding 2 (P1 review): ValidateConceptTarget's reparse-point checks
    // run BEFORE the write, so a concurrent local actor could in principle
    // substitute a path component with a symlink/junction in the gap
    // between that check and the later File.WriteAllText -- a classic
    // check-then-write (TOCTOU). This is a documented, residual limitation
    // that is NOT fully closed (see ValidateConceptTarget's and
    // WriteValidatedContentLocked's XML doc remarks) -- but
    // WriteValidatedContentLocked re-runs the same reparse-point checks
    // again immediately before the write, narrowing the window. This test
    // proves that late re-check is load-bearing: using the internal
    // BeforeLateReparseCheckForTest hook, it deterministically substitutes
    // the target's freshly-created parent directory with a junction to an
    // external directory at exactly the point such a race would need to
    // land -- a substitution the EARLIER check could never have caught,
    // since "linked" did not exist yet when ValidateConceptTarget ran.
    // Requires junction/symlink privilege; probes for it up front (without
    // mutating anything the real assertion depends on) and skips cleanly if
    // unavailable, per the other reparse-point tests' pattern.
    [SkippableFact]
    public void WriteConcept_late_reparse_recheck_catches_a_substitution_planted_after_the_early_check()
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);
        using var external = new TempDir();

        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("privilege-probe", external.Path), "no junction/symlink privilege on this machine");

        Directory.Delete(Path.Combine(tmp.Path, "privilege-probe"));

        tools.BeforeLateReparseCheckForTest = () =>
        {
            // "linked" was just created as a REAL, empty directory by
            // WriteValidatedContentLocked's Directory.CreateDirectory call
            // (ValidateConceptTarget ran earlier, when "linked" did not
            // exist at all, so it had nothing to reject). Swap it out for a
            // junction to an external directory right before the late
            // re-check runs, simulating a concurrent local substitution
            // landing in that narrow window.
            Directory.Delete(Path.Combine(tmp.Path, "linked"));
            Assert.True(tmp.TryCreateJunctionToExternalDir("linked", external.Path));
        };

        var result = tools.WriteConcept("linked/x", ValidFrontmatter, "Body.\n");

        Assert.Contains("Error", result);
        Assert.Contains("reparse point", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(external.Path));
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

    [Fact]
    public void WriteConcept_auto_stamps_generated_when_absent()
    {
        using var tmp = new TempDir();
        var tools = new OkfBundleTools(tmp.Path) { UtcNow = () => new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc) };

        var result = tools.WriteConcept("metrics/dau", "type: Metric\ntitle: DAU\ndescription: Daily active users.", "Body.");
        Assert.StartsWith("Written", result);

        var doc = OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "metrics", "dau.md")));
        var gen = doc.Frontmatter.Generated;
        Assert.NotNull(gen);
        Assert.Equal("okf4net/0.2", gen!.Value.By!.Value.Raw);
        Assert.Equal("2026-07-27T10:00:00Z", gen.Value.At);
    }

    [Fact]
    public void WriteConcept_keeps_caller_supplied_generated()
    {
        using var tmp = new TempDir();
        var tools = new OkfBundleTools(tmp.Path) { UtcNow = () => new DateTime(2026, 7, 27, 10, 0, 0, DateTimeKind.Utc) };

        tools.WriteConcept("metrics/dau",
            "type: Metric\ntitle: DAU\ndescription: D\ngenerated: {by: human:ada, at: 2020-01-01T00:00:00Z}", "Body.");

        var doc = OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "metrics", "dau.md")));
        Assert.Equal("human:ada", doc.Frontmatter.Generated!.Value.By!.Value.Raw);
        Assert.Equal("2020-01-01T00:00:00Z", doc.Frontmatter.Generated.Value.At);
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

    /// <summary>
    /// The rejection must name the field that was actually wrong. The theory
    /// above only asserts "Error", so it would pass just as happily with the
    /// `kind` and `text` messages swapped — which is exactly the failure a
    /// shared, field-name-parameterised validator can introduce.
    /// </summary>
    [Theory]
    [InlineData("", "text", "kind")]
    [InlineData("Update", "", "text")]
    [InlineData("Update\nx", "text", "kind")]
    [InlineData("Update", "text\nx", "text")]
    public void AppendLog_rejection_names_the_offending_field(string kind, string text, string expectedField)
    {
        using var tmp = new TempDir();
        var tools = NewToolsOverFixtureCopy(tmp);

        var result = tools.AppendLog(kind, text);

        Assert.StartsWith($"Error: invalid {expectedField} ", result, StringComparison.Ordinal);
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

    /// <summary>
    /// The four separators <c>GuardLogField</c> deliberately does NOT reject
    /// are folded to a space before they reach <c>log.md</c>, and the echoed
    /// success message names what was written rather than the raw argument.
    ///
    /// U+000C, U+0085, U+2028 and U+2029 cannot forge history on re-read --
    /// <c>ChangeLog.Parse</c> splits on LF only -- but they DO split a
    /// downstream markdown or JavaScript renderer of the same file, and until
    /// this fix they were persisted verbatim into the user's repository, where
    /// every other consumer inherits them. The assertion is on the FILE, not
    /// on a rendering of it: this is the one sink in this area where the
    /// defect was persistent rather than per-render.
    /// </summary>
    [Theory]
    [InlineData(0x000C)]
    [InlineData(0x0085)]
    [InlineData(0x2028)]
    [InlineData(0x2029)]
    public void AppendLog_folds_a_soft_separator_instead_of_persisting_it(int separator)
    {
        var sep = (char)separator;
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        var tools = new OkfBundleTools(tmp.Path);

        var result = tools.AppendLog("Upd" + sep + "- **Forged**: kind", "real" + sep + "- **Update**: FORGED");

        Assert.StartsWith("Appended", result);
        var onDisk = File.ReadAllText(Path.Combine(tmp.Path, "log.md"));
        // Gone from the FILE, not merely from a rendering of it.
        Assert.DoesNotContain(sep.ToString(), onDisk, StringComparison.Ordinal);
        Assert.Contains("* **Upd - **Forged**: kind**: real - **Update**: FORGED", onDisk, StringComparison.Ordinal);
        // One bullet, under a split that honours the separator as well as LF.
        Assert.Single(onDisk.Split(EveryLineTerminator), l => l.StartsWith("* ", StringComparison.Ordinal));
        // And the echo names what was written, not the raw argument.
        Assert.DoesNotContain(sep.ToString(), result, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the asymmetry that fold creates, pinned so it cannot
    /// be "simplified" into folding all six: <c>\n</c> and <c>\r</c> stay
    /// REFUSED, and no <c>log.md</c> is created at all. Those two are what
    /// <c>ChangeLog.Parse</c> splits on, so a caller who sent one would
    /// otherwise believe a forged-looking entry was recorded as written; it
    /// must learn the write did not happen.
    /// </summary>
    [Theory]
    [InlineData('\n')]
    [InlineData('\r')]
    public void AppendLog_still_refuses_a_hard_line_break_rather_than_folding_it(char separator)
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        var tools = new OkfBundleTools(tmp.Path);

        var result = tools.AppendLog("Upd" + separator + "## 2099-01-01", "real" + separator + "* **Forged**: not real.");

        Assert.StartsWith("Error", result);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "log.md")), "log.md must not be created by a refused append");
    }

    // log.md always lives directly at BundleRoot, so HasReparsePointAncestor
    // gives no protection here (its walk starts at BundleRoot itself and
    // stops immediately). The real risk is log.md ITSELF being a planted
    // file symlink pointing at an external file: File.Exists/ReadAllBytes/
    // WriteAllText would all follow it and silently overwrite that external
    // file. Requires symlink-creation privilege and skips itself (Skip.IfNot on
    // TryCreateFileSymlinkToExternalFile's bool return) when unavailable.
    [SkippableFact]
    public void AppendLog_refuses_to_write_through_a_planted_log_md_symlink()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path);
        var tools = new OkfBundleTools(tmp.Path);
        using var external = new TempDir();
        var externalFile = external.Write("secret.txt", "do not touch\n");

        Skip.IfNot(tmp.TryCreateFileSymlinkToExternalFile("log.md", externalFile), "no symlink privilege on this machine");

        var result = tools.AppendLog("Update", "Should not be written.");

        Assert.Contains("Error", result);
        Assert.Equal("do not touch\n", File.ReadAllText(externalFile));
    }

    // Mirrors WriteConcept_late_reparse_recheck_catches_a_substitution_planted_after_the_early_check
    // for AppendLog's own late re-check (added for TOCTOU-guard parity with
    // WriteConcept). AppendLog's early check runs before _bundleLock is
    // acquired, and in this test log.md does not exist yet at that point, so
    // the early check has nothing to reject. The BeforeLateReparseCheckForTest
    // hook then plants a file symlink at log.md, pointing at an external
    // file, right before AppendLog's late re-check runs inside the lock --
    // exactly the narrow window such a race would need to land in, which the
    // earlier check could never have caught. Requires symlink-creation
    // privilege; probes for it up front (without mutating anything the real
    // assertion depends on) and skips cleanly if unavailable, per the other
    // reparse-point tests' pattern.
    [SkippableFact]
    public void AppendLog_late_reparse_recheck_catches_a_substitution()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path);
        var tools = new OkfBundleTools(tmp.Path) { UtcNow = () => new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc) };
        using var external = new TempDir();
        var externalFile = external.Write("secret.txt", "do not touch\n");

        Skip.IfNot(tmp.TryCreateFileSymlinkToExternalFile("privilege-probe.txt", externalFile), "no symlink privilege on this machine");

        File.Delete(Path.Combine(tmp.Path, "privilege-probe.txt"));

        tools.BeforeLateReparseCheckForTest = () =>
        {
            // log.md did not exist when the early check (before _bundleLock)
            // ran, so it had nothing to reject. Plant a file symlink at
            // log.md right before the late re-check runs, simulating a
            // concurrent local substitution landing in that narrow window.
            Assert.True(tmp.TryCreateFileSymlinkToExternalFile("log.md", externalFile));
        };

        var result = tools.AppendLog("Update", "Should not be written.");

        Assert.Contains("Error", result);
        Assert.Contains("reparse point", result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("do not touch\n", File.ReadAllText(externalFile));
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

    /// <summary>
    /// The verb's own bullet list is a line-structured sink of exactly the
    /// same shape as <c>AppendLogFileChanges</c>'s <c>## {rel}</c> and
    /// <c>&gt; Skipped {rel}</c>: <c>rel</c> is a <c>Path.GetRelativePath</c>
    /// over a bundle path, and a directory name may carry a soft line
    /// terminator (NTFS and POSIX both accept U+2028 in a name). One index
    /// file must stay one <c>- </c> line under a split that honours those
    /// terminators, not just under an LF split.
    /// </summary>
    [Fact]
    public void RegenerateIndexes_bullet_cannot_be_forged_by_a_directory_name()
    {
        using var tmp = new TempDir();
        tmp.Write("sub" + LineSeparator + "- index.md" + LineSeparator + "dir/rev.md", "---\ntype: Metric\n---\n\nbody\n");
        tmp.Write("top.md", "---\ntype: Metric\n---\n\nbody\n");
        var tools = new OkfBundleTools(tmp.Path);

        var result = tools.RegenerateIndexes();

        Assert.Contains("Regenerated 2 index file(s):", result, StringComparison.Ordinal);
        Assert.Equal(2, result.Split(EveryLineTerminator).Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
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
