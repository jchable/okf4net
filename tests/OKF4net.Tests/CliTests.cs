// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using OKF4net.Cli;

namespace OKF4net.Tests;

/// <summary>
/// Smoke tests for the <c>okf</c> CLI, exercising <see cref="OkfCli.Run"/>
/// in-process (no subprocess spawn). One test per subcommand plus the
/// no-args/usage path. Exact exit codes and output text follow the CLI's
/// documented behaviour.
/// </summary>
public class CliTests
{
    // `dotnet test` runs with the current directory set to the test
    // assembly's output folder (bin/Debug/net10.0), not the repo root, so
    // the fixture path is resolved relative to the repo root (located by
    // TestPaths.RepoRoot, walking up from the test assembly to the .sln)
    // rather than assumed relative to the process's current directory.
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private static readonly string V02BundlePath =
        Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02");

    private static (int Code, string Out, string Err) Run(params string[] args) => TestPaths.Run(args);

    [Fact]
    public void No_args_prints_usage_and_fails()
    {
        var r = Run();
        Assert.Equal(1, r.Code);
        Assert.Contains("USAGE:", r.Err);
        Assert.Equal("", r.Out);
    }

    [Fact]
    public void Unknown_subcommand_fails()
    {
        var r = Run("frobnicate");
        Assert.Equal(1, r.Code);
        Assert.Contains("unknown subcommand: frobnicate", r.Err);
    }

    [Fact]
    public void Help_prints_usage_and_succeeds()
    {
        var r = Run("--help");
        Assert.Equal(0, r.Code);
        Assert.Contains("USAGE:", r.Out);
    }

    [Fact]
    public void Version_prints_and_succeeds()
    {
        var r = Run("--version");
        Assert.Equal(0, r.Code);
        Assert.Contains("okf ", r.Out);
        Assert.Contains("OKF spec v0.2", r.Out);
    }

    [Fact]
    public void Version_matches_the_build_version()
    {
        // OkfCli.CliVersion is hand-maintained, separate from <Version> in
        // Directory.Build.props. The two drifted once and the winget package
        // for 0.2.0 shipped a binary whose `--version` printed 0.1.0-alpha.1
        // (caught by a Microsoft moderator, not by us). Fail the build here
        // instead: Version_prints_and_succeeds only checks the "okf " prefix.
        var props = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "Directory.Build.props"));
        var declared = Regex.Match(props, @"<Version>\s*([^<\s]+)\s*</Version>");
        Assert.True(declared.Success, "no <Version> element in Directory.Build.props");

        var r = Run("--version");
        Assert.Equal(0, r.Code);
        Assert.StartsWith($"okf {declared.Groups[1].Value} ", r.Out);
    }

    [Fact]
    public void Validate_conformant_bundle_exits_zero()
    {
        var r = Run("validate", BundlePath);
        Assert.Equal(0, r.Code);
        Assert.Contains("conformant with OKF v0.2", r.Out);
    }

    /// <summary>
    /// §5.5's staleness warning depends on what "today" is, so without a way
    /// to pin the date `okf validate`'s output was not reproducible: the same
    /// bundle validated clean before a concept's stale_after and warned after
    /// it, with no way to assert either in CI. `okf audit` gained `--as-of`
    /// first; this closes the asymmetry on the verb that reports the warning.
    /// </summary>
    [Fact]
    public void Validate_as_of_pins_the_staleness_warning()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            "---\ntype: Metric\ntitle: Daily Active Users\ndescription: Count.\nstale_after: 2026-06-01\n---\n");

        var before = Run("validate", tmp.Path, "--as-of", "2026-05-31");
        var onTheDay = Run("validate", tmp.Path, "--as-of", "2026-06-01");

        // §5.5 is `today >= stale_after`, so the boundary date is already stale.
        Assert.DoesNotContain("concept is stale", before.Out);
        Assert.Contains("concept is stale (stale_after 2026-06-01)", onTheDay.Out);

        // Staleness is a warning, not a conformance error: both still exit 0.
        Assert.Equal(0, before.Code);
        Assert.Equal(0, onTheDay.Code);
    }

    /// <summary>
    /// `--as-of` exists so a CI job's verdict is reproducible; an archived
    /// report that does not say which date it was evaluated against cannot be
    /// told apart from an unpinned run, so the date belongs in the document.
    /// </summary>
    [Fact]
    public void Validate_json_records_the_date_it_was_evaluated_against()
    {
        var pinned = Run("validate", BundlePath, "--as-of", "2026-06-01", "--json");
        using var pinnedDoc = JsonDocument.Parse(pinned.Out);
        Assert.Equal("2026-06-01", pinnedDoc.RootElement.GetProperty("asOf").GetString());

        // Unpinned runs report the date they actually used, rather than omitting it.
        var unpinned = Run("validate", BundlePath, "--json");
        using var unpinnedDoc = JsonDocument.Parse(unpinned.Out);
        Assert.False(string.IsNullOrEmpty(unpinnedDoc.RootElement.GetProperty("asOf").GetString()));
    }

    /// <summary>
    /// One document, one spelling: a consumer grouping findings by trust tier
    /// must be able to look that tier straight up in the counts object.
    /// </summary>
    [Fact]
    public void Audit_json_spells_trust_tiers_the_same_way_in_counts_and_findings()
    {
        var r = Run("audit", V02BundlePath, "--as-of", "2099-06-01", "--json");

        using var doc = JsonDocument.Parse(r.Out);
        var counts = doc.RootElement.GetProperty("trust");
        var finding = doc.RootElement.GetProperty("findings").EnumerateArray().Single();

        Assert.Equal(["unverified", "machine-confirmed", "human-reviewed"], counts.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, counts.GetProperty(finding.GetProperty("trust").GetString()!).GetInt32());
    }

    /// <summary>
    /// A blank `--type` is "no type filter", not a filter for the empty string:
    /// §11 forbids an empty frontmatter type, so filtering for one could only
    /// ever select nothing.
    /// </summary>
    [Fact]
    public void Audit_a_blank_type_filter_selects_every_concept()
    {
        var r = Run("audit", V02BundlePath, "--type", "");

        Assert.Equal(0, r.Code);
        Assert.Equal(2, r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Validate_rejects_an_invalid_as_of_date()
    {
        var r = Run("validate", BundlePath, "--as-of", "2026-13-01");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: --as-of is not a valid YYYY-MM-DD date: \"2026-13-01\"\n", r.Err);
    }

    /// <summary>
    /// Regression guard: `--as-of` must be declared as a valued flag on
    /// `validate` too, or its value is mistaken for the bundle path.
    /// </summary>
    [Fact]
    public void Validate_as_of_before_the_positional_resolves_the_bundle()
    {
        var r = Run("validate", "--as-of", "2026-06-01", BundlePath);

        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public void Validate_nonconformant_bundle_exits_nonzero()
    {
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntitle: no type\n---\n\nx\n");
        var r = Run("validate", tmp.Path);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("not conformant", r.Out);
    }

    [Fact]
    public void Validate_bundle_with_malformed_reserved_file_exits_nonzero()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
        tmp.Write("sub/index.md", "---\ntitle: nope\n---\n\n# Listing\n");
        var r = Run("validate", tmp.Path);
        Assert.NotEqual(0, r.Code);
        Assert.Contains("not conformant", r.Out);
    }

    [Fact]
    public void Info_prints_summary()
    {
        var r = Run("info", BundlePath);
        Assert.Equal(0, r.Code);
        Assert.Contains("concepts:   4", r.Out);
    }

    [Fact]
    public void Validate_json_reports_bundle_conformance_and_diagnostics()
    {
        var r = Run("validate", "--json", BundlePath);
        Assert.Equal(0, r.Code);

        using var doc = System.Text.Json.JsonDocument.Parse(r.Out);
        var root = doc.RootElement;
        Assert.Equal(BundlePath, root.GetProperty("bundle").GetString());
        Assert.True(root.GetProperty("conformant").GetBoolean());
        Assert.Equal(4, root.GetProperty("conceptCount").GetInt32());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());
        var diagnostics = root.GetProperty("diagnostics");
        Assert.True(diagnostics.GetArrayLength() > 0);
        var first = diagnostics[0];
        Assert.True(first.TryGetProperty("severity", out _));
        Assert.True(first.TryGetProperty("code", out _));
        Assert.True(first.TryGetProperty("message", out _));
    }

    [Fact]
    public void Validate_json_diagnostic_field_is_populated_when_applicable()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
        var r = Run("validate", "--json", tmp.Path);

        using var doc = System.Text.Json.JsonDocument.Parse(r.Out);
        var diagnostics = doc.RootElement.GetProperty("diagnostics");
        var timestampDiag = diagnostics.EnumerateArray().Single(d => d.GetProperty("code").GetString() == "LegacyTimestamp");
        Assert.Equal("timestamp", timestampDiag.GetProperty("field").GetString());
    }

    [Fact]
    public void Info_json_reports_bundle_summary()
    {
        var r = Run("info", "--json", BundlePath);
        Assert.Equal(0, r.Code);

        using var doc = System.Text.Json.JsonDocument.Parse(r.Out);
        var root = doc.RootElement;
        Assert.Equal(4, root.GetProperty("conceptCount").GetInt32());
        Assert.True(root.TryGetProperty("types", out var types));
        Assert.True(types.EnumerateObject().Any());
        Assert.True(root.TryGetProperty("linkCount", out _));
        Assert.True(root.TryGetProperty("brokenLinkCount", out _));
    }

    [Fact]
    public void Info_json_types_is_present_and_empty_for_a_bundle_with_no_concepts()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n");
        var r = Run("info", "--json", tmp.Path);

        using var doc = System.Text.Json.JsonDocument.Parse(r.Out);
        Assert.True(doc.RootElement.TryGetProperty("types", out var types));
        Assert.Empty(types.EnumerateObject());
    }

    [Fact]
    public void Validate_text_output_is_unchanged_by_the_json_feature()
    {
        var r = Run("validate", BundlePath);
        Assert.Equal(0, r.Code);
        Assert.Contains("conformant with OKF v0.2", r.Out);
        Assert.DoesNotContain("{", r.Out);
    }

    [Fact]
    public void Info_text_output_is_unchanged_by_the_json_feature()
    {
        var r = Run("info", BundlePath);
        Assert.Equal(0, r.Code);
        Assert.Contains("concepts:   4", r.Out);
        Assert.DoesNotContain("{", r.Out);
    }

    [Fact]
    public void Index_regenerates_indexes()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Thing\ntitle: A\n---\n\nbody\n");
        var r = Run("index", tmp.Path);
        Assert.Equal(0, r.Code);
        Assert.Contains("index file(s) regenerated", r.Out);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "index.md")));
    }

    [Fact]
    public void Graph_dot_prints_digraph()
    {
        var r = Run("graph", BundlePath, "--dot");
        Assert.Equal(0, r.Code);
        Assert.StartsWith("digraph okf {", r.Out);
    }

    [Fact]
    public void Graph_dot_styles_broken_links_dashed_and_red()
    {
        // CmdGraph: an edge to a non-existent concept gets
        // ` [style=dashed, color=red]` appended before the trailing `;`.
        // A resolvable edge gets no style suffix at all. Build a small bundle
        // with one concept linking to a target that does not exist in the
        // bundle.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nSee [missing](/does/not/exist.md).\n");
        var r = Run("graph", tmp.Path, "--dot");

        Assert.Equal(0, r.Code);
        Assert.Contains("\"a\" -> \"does/not/exist\" [style=dashed, color=red];\n", r.Out);
    }

    [Fact]
    public void Graph_dot_does_not_style_resolvable_links()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nSee [b](/b.md).\n");
        tmp.Write("b.md", "---\ntype: Note\n---\nbody\n");
        var r = Run("graph", tmp.Path, "--dot");

        Assert.Equal(0, r.Code);
        Assert.Contains("\"a\" -> \"b\";\n", r.Out);
        Assert.DoesNotContain("style=dashed", r.Out);
    }

    [Fact]
    public void Parse_prints_document_structure()
    {
        var r = Run("parse", Path.Combine(BundlePath, "tables", "users.md"));
        Assert.Equal(0, r.Code);
        Assert.Contains("frontmatter (", r.Out);
        Assert.Contains("has non-empty `type`: true", r.Out);
    }

    [Fact]
    public void Fmt_write_normalizes_file_in_place()
    {
        using var tmp = new TempDir();
        var path = tmp.Write("doc.md", "---\ntype: Thing\n---\nbody\n");
        var r = Run("fmt", path, "-w");
        Assert.Equal(0, r.Code);
        Assert.Contains("formatted", r.Out);
        Assert.Contains("body\n", File.ReadAllText(path));
    }

    // ----------------------------------------------------------------
    // A3: invalid-path arguments must exit 1 with a uniform "error: ..."
    // message on stderr, never an unhandled-exception stack trace -- every
    // I/O failure is funneled to a single `error: {msg}` on stderr.
    // An embedded NUL byte is a convenient garbage path on every platform:
    // .NET's filesystem APIs reject it with ArgumentException ("Null
    // character in path"), which previously escaped uncaught from
    // ReadFileStrict/WriteFileStrict instead of being funneled like every
    // other I/O failure.
    // ----------------------------------------------------------------

    [Fact]
    public void Parse_with_embedded_nul_path_exits_one_with_error_prefix()
    {
        var r = Run("parse", "a\0b.md");
        Assert.Equal(1, r.Code);
        Assert.StartsWith("error:", r.Err);
        Assert.DoesNotContain("at OKF4net", r.Err);
        Assert.DoesNotContain("at OKF4net", r.Out);
    }

    [Fact]
    public void Validate_with_embedded_nul_path_exits_one_with_error_prefix()
    {
        var r = Run("validate", "x\0y");
        Assert.Equal(1, r.Code);
        Assert.StartsWith("error:", r.Err);
        Assert.DoesNotContain("at OKF4net", r.Err);
        Assert.DoesNotContain("at OKF4net", r.Out);
    }

    [Fact]
    public void Fmt_with_embedded_nul_path_exits_one_with_error_prefix()
    {
        var r = Run("fmt", "a\0b.md");
        Assert.Equal(1, r.Code);
        Assert.StartsWith("error:", r.Err);
        Assert.DoesNotContain("at OKF4net", r.Err);
    }

    [Fact]
    public void Info_with_embedded_nul_path_exits_one_with_error_prefix()
    {
        var r = Run("info", "x\0y");
        Assert.Equal(1, r.Code);
        Assert.StartsWith("error:", r.Err);
        Assert.DoesNotContain("at OKF4net", r.Err);
    }

    [Fact]
    public void Graph_with_embedded_nul_path_exits_one_with_error_prefix()
    {
        var r = Run("graph", "x\0y");
        Assert.Equal(1, r.Code);
        Assert.StartsWith("error:", r.Err);
        Assert.DoesNotContain("at OKF4net", r.Err);
    }

    [Fact]
    public void Index_with_embedded_nul_path_reports_no_files_written_not_an_error()
    {
        // Deliberately NOT an "error: ..." exit-1 case: RegenerateIndexes
        // checks whether the bundle root exists first, and Directory.Exists
        // swallows the underlying failure and simply returns false for a
        // garbage path, so the function returns an empty result rather than
        // an I/O error. CmdIndex then reports "no index files written" and
        // exits 0. Audited (A3) to confirm this command needed no change,
        // unlike parse/fmt/validate/info/graph.
        var r = Run("index", "x\0y");
        Assert.Equal(0, r.Code);
        Assert.Contains("no index files written", r.Out);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public void Render_writes_a_site_and_reports_success()
    {
        using var dest = new TempDir();
        var outDir = Path.Combine(dest.Path, "site");

        var r = Run("render", BundlePath, "--out", outDir);

        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Err);
        Assert.True(File.Exists(Path.Combine(outDir, "index.html")));
    }

    [Fact]
    public void Render_without_out_fails()
    {
        var r = Run("render", BundlePath);
        Assert.Equal(1, r.Code);
        Assert.Contains("--out", r.Err);
    }

    [Fact]
    public void Render_without_a_bundle_fails()
    {
        var r = Run("render");
        Assert.Equal(1, r.Code);
        Assert.Contains("error:", r.Err);
    }

    [Fact]
    public void Render_into_the_bundle_itself_fails()
    {
        // Regression guard: if this check ever weakens, `dotnet test` would
        // write generated HTML straight into whatever bundle path is passed
        // here. Use a throwaway bundle in a TempDir -- never BundlePath,
        // which is the byte-exact golden fixture tests/fixtures/appendix_a --
        // so a regression can never corrupt the real goldens the
        // golden-parity tests depend on.
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");

        var r = Run("render", tmp.Path, "--out", Path.Combine(tmp.Path, "site"));

        Assert.Equal(1, r.Code);
        Assert.Contains("error:", r.Err);

        // Regression guard for the .NET ArgumentException(paramName) leaking
        // its " (Parameter 'outDir')" framework-noise suffix into CLI output
        // meant for humans -- HtmlWriter.Write is a library API and correctly
        // keeps throwing with paramName set; the CLI must strip it before
        // printing.
        Assert.DoesNotContain("Parameter", r.Err);
    }

    [Fact]
    public void Render_with_out_flag_missing_its_value_after_the_bundle_fails_with_out_message()
    {
        var r = Run("render", BundlePath, "--out");

        Assert.Equal(1, r.Code);
        Assert.Contains("--out requires a value", r.Err);
    }

    [Fact]
    public void Render_with_bare_out_flag_and_no_bundle_fails_with_out_message_not_missing_bundle()
    {
        // Same "--out present but unvalued" failure as the test above, just
        // with the bundle positional also absent. Before the fix this order
        // dependency made the message flip to "missing <bundle>" (Positional
        // ran first and hit the empty slot before FlagValue's bounds check
        // ever fired) -- deterministic now: FlagValue's check always wins.
        var r = Run("render", "--out");

        Assert.Equal(1, r.Code);
        Assert.Contains("--out requires a value", r.Err);
        Assert.DoesNotContain("missing <bundle>", r.Err);
    }

    [Fact]
    public void Usage_mentions_the_render_verb()
    {
        var r = Run("--help");
        Assert.Equal(0, r.Code);
        Assert.Contains("render", r.Out);
    }

    [Fact]
    public void Render_with_only_out_and_no_bundle_fails_rather_than_treating_the_out_dir_as_the_bundle()
    {
        // --out is the CLI's first VALUED option -- every other verb's flags
        // are valueless (--dot, --json, -w) -- so the naive Positional()
        // scan (first arg not starting with '-') would previously return the
        // *value* of --out as the bundle path when the bundle itself is
        // omitted. Guard against silently rendering the output directory as
        // if it were the bundle.
        using var dest = new TempDir();
        var outDir = Path.Combine(dest.Path, "site");

        var r = Run("render", "--out", outDir);

        Assert.Equal(1, r.Code);
        Assert.Contains("error:", r.Err);
        Assert.False(Directory.Exists(outDir));
    }

    // ----------------------------------------------------------------
    // audit
    // ----------------------------------------------------------------

    [Fact]
    public void Audit_report_mode_prints_summary_and_worklist()
    {
        var r = Run("audit", V02BundlePath, "--as-of", "2099-06-01");

        Assert.Equal(0, r.Code);
        Assert.Contains("as of:      2099-06-01\n", r.Out);
        Assert.Contains("concepts:   2\n", r.Out);
        Assert.Contains("     1  human-reviewed\n", r.Out);
        Assert.Contains("     1  unverified\n", r.Out);
        Assert.Contains("     2  stable\n", r.Out);
        Assert.Contains("stale:      1 of 2 past stale_after\n", r.Out);
        Assert.Contains("needs attention (1):\n", r.Out);
        Assert.Contains("  metrics/dau  stale 2099-01-01  human-reviewed  stable\n", r.Out);
    }

    [Fact]
    public void Audit_query_mode_prints_bare_lines_only()
    {
        var r = Run("audit", V02BundlePath, "--stale", "--as-of", "2099-06-01");

        Assert.Equal(0, r.Code);
        Assert.Equal("metrics/dau  stale 2099-01-01  human-reviewed  stable\n", r.Out);
    }

    [Fact]
    public void Audit_without_flags_selects_the_same_set_as_stale()
    {
        var report = Run("audit", V02BundlePath, "--as-of", "2099-06-01");
        var query = Run("audit", V02BundlePath, "--stale", "--as-of", "2099-06-01");

        var reportIds = report.Out
            .Split('\n')
            .Where(l => l.StartsWith("  metrics/", StringComparison.Ordinal))
            .Select(l => l.Trim().Split("  ")[0])
            .ToList();
        var queryIds = query.Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split("  ")[0])
            .ToList();

        Assert.Equal(queryIds, reportIds);
    }

    [Fact]
    public void Audit_empty_selection_prints_nothing()
    {
        var r = Run("audit", V02BundlePath, "--status", "deprecated");

        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Out);
    }

    /// <summary>
    /// Positive-selection coverage for <c>--type</c>: both fixture concepts
    /// are <c>type: Metric</c>, so the exact-case value must select both, and
    /// the lowercase variant must select none -- pinning the documented
    /// ordinal, case-sensitive rule (§ Audit.cs <c>AuditQuery.Type</c>) at the
    /// CLI boundary. Without this, transposing <c>Type</c>/<c>Status</c> in
    /// <c>ParseAuditQuery</c> would leave the suite green.
    /// </summary>
    [Fact]
    public void Audit_type_filter_selects_matching_concepts_case_sensitively()
    {
        var match = Run("audit", V02BundlePath, "--type", "Metric");
        Assert.Equal(0, match.Code);
        Assert.Equal(2, match.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

        var noMatch = Run("audit", V02BundlePath, "--type", "metric");
        Assert.Equal(0, noMatch.Code);
        Assert.Equal("", noMatch.Out);
    }

    /// <summary>
    /// Positive-selection coverage for <c>--status</c>: both fixture concepts
    /// resolve to <c>status: stable</c> (one explicitly, one via
    /// <c>Lifecycle.From</c>'s fallback for the unknown "retired" value), so
    /// this must select both.
    /// </summary>
    [Fact]
    public void Audit_status_filter_selects_matching_concepts()
    {
        var r = Run("audit", V02BundlePath, "--status", "stable");

        Assert.Equal(0, r.Code);
        Assert.Equal(2, r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Audit_three_tier_idiom_returns_every_concept()
    {
        var r = Run("audit", V02BundlePath, "--trust", "unverified,machine-confirmed,human-reviewed");

        Assert.Equal(0, r.Code);
        Assert.Equal(2, r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Audit_rejects_an_invalid_as_of_date()
    {
        var r = Run("audit", V02BundlePath, "--as-of", "2026-13-01");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: --as-of is not a valid YYYY-MM-DD date: \"2026-13-01\"\n", r.Err);
    }

    [Fact]
    public void Audit_rejects_an_unknown_trust_tier()
    {
        var r = Run("audit", V02BundlePath, "--trust", "foo");

        Assert.Equal(1, r.Code);
        Assert.Equal(
            "error: unknown trust tier \"foo\"; expected unverified, machine-confirmed or human-reviewed\n",
            r.Err);
    }

    [Fact]
    public void Audit_rejects_an_unknown_status()
    {
        var r = Run("audit", V02BundlePath, "--status", "retired");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: unknown status \"retired\"; expected draft, stable or deprecated\n", r.Err);
    }

    [Fact]
    public void Audit_rejects_an_empty_trust_entry_but_absorbs_duplicates()
    {
        var empty = Run("audit", V02BundlePath, "--trust", "unverified,,human-reviewed");
        Assert.Equal(1, empty.Code);
        Assert.Contains("unknown trust tier", empty.Err);

        var duplicated = Run("audit", V02BundlePath, "--trust", "unverified,unverified");
        var single = Run("audit", V02BundlePath, "--trust", "unverified");
        Assert.Equal(0, duplicated.Code);
        Assert.Equal(single.Out, duplicated.Out);
    }

    /// <summary>
    /// Regression guard: valued flags must be declared to <c>Positional</c>, or
    /// their value is mistaken for the bundle path when they precede it.
    /// </summary>
    [Theory]
    [InlineData("--as-of", "2099-06-01")]
    [InlineData("--trust", "unverified")]
    [InlineData("--status", "stable")]
    [InlineData("--type", "Metric")]
    public void Audit_valued_flags_before_the_positional_resolve_the_bundle(string flag, string value)
    {
        var r = Run("audit", flag, value, V02BundlePath);

        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Err);
    }

    /// <summary>
    /// A valued flag left without a value must name itself, even when it is the
    /// only argument -- otherwise the user is told the bundle is missing and the
    /// real mistake is hidden. This is why CmdAudit validates flag values before
    /// resolving the positional.
    /// </summary>
    [Theory]
    [InlineData("--as-of")]
    [InlineData("--trust")]
    [InlineData("--status")]
    [InlineData("--type")]
    public void Audit_reports_a_valued_flag_left_without_a_value(string flag)
    {
        var r = Run("audit", flag);

        Assert.Equal(1, r.Code);
        Assert.Equal($"error: {flag} requires a value\n", r.Err);
    }

    /// <summary>
    /// A token consumed as a valued flag's value is that value and nothing
    /// else — it must not also register as a flag. Before the arguments were
    /// scanned once, presence and value were independent scans of the raw
    /// array, so `--type --stale` set the stale filter as well as the type.
    /// </summary>
    [Fact]
    public void Audit_a_flag_name_used_as_a_value_is_not_also_a_flag()
    {
        var r = Run("audit", V02BundlePath, "--type", "--stale", "--json");

        Assert.Equal(0, r.Code);

        using var doc = JsonDocument.Parse(r.Out);
        var query = doc.RootElement.GetProperty("query");

        Assert.Equal("--stale", query.GetProperty("type").GetString());
        Assert.False(query.GetProperty("stale").GetBoolean());
    }

    /// <summary>
    /// Everything after `--` is positional, never a flag — that is what the
    /// separator is for. Only the positional scan used to honour it, so a
    /// `--json` sitting after the separator still switched the output format.
    /// </summary>
    [Fact]
    public void Audit_tokens_after_the_separator_are_never_flags()
    {
        var r = Run("audit", "--", V02BundlePath, "--json");

        Assert.Equal(0, r.Code);
        Assert.StartsWith("bundle:     ", r.Out);
        Assert.DoesNotContain("\"conceptCount\"", r.Out);
    }

    /// <summary>
    /// The `--` rule is the CLI's, not audit's: on every verb, a token after the
    /// separator is positional and never a flag. This pins it on `fmt`, whose
    /// `-w` is the one flag with a side effect on disk — before the arguments
    /// were scanned once, only the positional lookup honoured the separator, so
    /// `-w` sitting after it still rewrote the file.
    /// </summary>
    [Fact]
    public void Fmt_a_write_flag_after_the_separator_is_not_a_flag()
    {
        using var tmp = new TempDir();
        const string unformatted = "---\ntype: Note\ntitle:   Spaced\n---\n\nbody\n";
        var file = tmp.Write("note.md", unformatted);

        var r = Run("fmt", "--", file, "-w");

        Assert.Equal(0, r.Code);
        Assert.Equal(unformatted, File.ReadAllText(file));
        Assert.Contains("title: Spaced", r.Out);
    }

    /// <summary>
    /// A `--` with nothing after it still ends the option scan, but it does not
    /// discard a positional that came before: `okf audit b --` resolves `b`.
    /// </summary>
    [Fact]
    public void Audit_a_trailing_separator_keeps_the_earlier_positional()
    {
        var r = Run("audit", V02BundlePath, "--");

        Assert.Equal(0, r.Code);
        Assert.StartsWith($"bundle:     {V02BundlePath}", r.Out);
    }

    /// <summary>
    /// A repeated flag resolves to its FIRST occurrence, the rule every verb
    /// inherited from the original `Array.IndexOf` lookup and which the design
    /// spec (§4.1) documents. The later occurrence still consumes its own
    /// value, so that value never lands in the positional slot.
    /// </summary>
    [Fact]
    public void Audit_a_repeated_flag_resolves_to_its_first_occurrence()
    {
        var r = Run("audit", V02BundlePath, "--trust", "human-reviewed", "--trust", "unverified", "--json");

        Assert.Equal(0, r.Code);

        using var doc = JsonDocument.Parse(r.Out);
        var trust = doc.RootElement.GetProperty("query").GetProperty("trust")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(["human-reviewed"], trust);
    }

    [Fact]
    public void Audit_as_of_alone_stays_in_report_mode()
    {
        var r = Run("audit", V02BundlePath, "--as-of", "2099-06-01");

        Assert.Contains("needs attention", r.Out);
    }

    /// <summary>
    /// Report mode's empty-worklist branch: <c>--as-of</c> pinned before
    /// <c>metrics/dau</c>'s <c>stale_after</c> (2099-01-01) leaves nothing
    /// stale, so <c>WriteAuditReport</c> takes its early-return branch and
    /// prints the "none" line instead of a "needs attention (N):" worklist.
    /// </summary>
    [Fact]
    public void Audit_report_mode_prints_none_when_nothing_is_stale()
    {
        var r = Run("audit", V02BundlePath, "--as-of", "2026-01-01");

        Assert.Equal(0, r.Code);
        Assert.Contains("stale:      0 of 2 past stale_after\n", r.Out);
        Assert.Contains("needs attention: none\n", r.Out);
        Assert.DoesNotContain("needs attention (", r.Out);
    }

    /// <summary>
    /// <c>metrics/legacy</c> has no <c>stale_after</c> at all, so
    /// <c>FormatAuditFinding</c> takes its "no-stale-after" branch rather than
    /// "stale "/"fresh " + a date. <c>--trust unverified</c> selects exactly
    /// this concept (it has no <c>verified</c> entries), independent of
    /// <c>--as-of</c>/the system clock.
    /// </summary>
    [Fact]
    public void Audit_finding_line_reports_no_stale_after_when_the_field_is_absent()
    {
        var r = Run("audit", V02BundlePath, "--trust", "unverified");

        Assert.Equal(0, r.Code);
        Assert.Equal("metrics/legacy  no-stale-after  unverified  stable\n", r.Out);
    }

    [Fact]
    public void Help_lists_audit_right_after_validate()
    {
        var r = Run("--help");

        Assert.Equal(0, r.Code);
        var lines = r.Out.Split('\n').Select(l => l.TrimStart()).ToList();
        var validateIndex = lines.FindIndex(l => l.StartsWith("validate ", StringComparison.Ordinal));
        var auditIndex = lines.FindIndex(l => l.StartsWith("audit ", StringComparison.Ordinal));

        Assert.True(validateIndex >= 0 && auditIndex == validateIndex + 1);
    }

    [Fact]
    public void Audit_json_carries_counts_query_and_findings()
    {
        var r = Run("audit", V02BundlePath, "--as-of", "2099-06-01", "--json");

        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out);

        using var doc = JsonDocument.Parse(r.Out);
        var root = doc.RootElement;

        Assert.Equal("2099-06-01", root.GetProperty("asOf").GetString());
        Assert.Equal(2, root.GetProperty("conceptCount").GetInt32());
        Assert.Equal(1, root.GetProperty("staleCount").GetInt32());

        // Report mode selects what --stale selects, so the replayed query says so.
        Assert.True(root.GetProperty("query").GetProperty("stale").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("query").GetProperty("trust").ValueKind);

        Assert.Equal(1, root.GetProperty("trust").GetProperty("human-reviewed").GetInt32());
        Assert.Equal(1, root.GetProperty("trust").GetProperty("unverified").GetInt32());
        Assert.Equal(2, root.GetProperty("status").GetProperty("stable").GetInt32());

        var finding = root.GetProperty("findings").EnumerateArray().Single();
        Assert.Equal("metrics/dau", finding.GetProperty("conceptId").GetString());
        Assert.Equal("Metric", finding.GetProperty("type").GetString());
        Assert.Equal("Daily Active Users", finding.GetProperty("title").GetString());
        Assert.Equal("human-reviewed", finding.GetProperty("trust").GetString());
        Assert.Equal("2099-01-01", finding.GetProperty("staleAfter").GetString());
        Assert.True(finding.GetProperty("stale").GetBoolean());
    }

    [Fact]
    public void Audit_json_serializes_trust_query_in_ladder_order()
    {
        var r = Run("audit", V02BundlePath, "--trust", "human-reviewed,unverified", "--json");

        using var doc = JsonDocument.Parse(r.Out);
        var trust = doc.RootElement.GetProperty("query").GetProperty("trust")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(["unverified", "human-reviewed"], trust);
    }

    [Fact]
    public void Audit_json_keeps_a_malformed_stale_after_raw_and_not_stale()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\nstale_after: not-a-date\n---\n");

        var r = Run("audit", tmp.Path, "--trust", "unverified", "--json");

        using var doc = JsonDocument.Parse(r.Out);
        var finding = doc.RootElement.GetProperty("findings").EnumerateArray().Single();

        Assert.Equal("not-a-date", finding.GetProperty("staleAfter").GetString());
        Assert.False(finding.GetProperty("stale").GetBoolean());
    }

    /// <summary>
    /// `--` ends option parsing; it does not discard the positionals that came
    /// before it. With a single positional slot the old rule ("the token after
    /// the separator wins") was indistinguishable from this one; with a verb
    /// that takes several, it would silently drop the bundle.
    /// </summary>
    [Fact]
    public void Separator_keeps_positionals_from_both_sides()
    {
        var r = Run("audit", V02BundlePath, "--", "--json");

        // The bundle before `--` is still the positional; `--json` after it is
        // an argument, not a flag, so the output is the text report.
        Assert.Equal(0, r.Code);
        Assert.StartsWith($"bundle:     {V02BundlePath}", r.Out);
        Assert.DoesNotContain("\"conceptCount\"", r.Out);
    }

    private static string NewBundleWithTwoConcepts(TempDir tmp)
    {
        tmp.Write("metrics/dau.md", "---\ntype: Metric\ntitle: DAU\n---\n\nbody\n");
        tmp.Write("metrics/rev.md", "---\ntype: Metric\ntitle: Revenue\n---\n\nbody\n");
        return tmp.Path;
    }

    [Fact]
    public void Verify_records_a_stamp_on_each_named_concept()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);

        var r = Run("verify", bundle, "metrics/dau", "metrics/rev", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z");

        Assert.Equal(0, r.Code);
        Assert.Equal(
            "recorded metrics/dau  human:ada  2026-08-28T09:14:00Z\n"
            + "recorded metrics/rev  human:ada  2026-08-28T09:14:00Z\n",
            r.Out);
        Assert.Contains("by: human:ada", File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    [Fact]
    public void Verify_reports_the_timestamp_it_superseded()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            "---\ntype: Metric\nverified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n---\n\nbody\n");

        var r = Run("verify", tmp.Path, "metrics/dau", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z");

        Assert.Equal(0, r.Code);
        Assert.Equal(
            "recorded metrics/dau  human:ada  2026-08-28T09:14:00Z  (replaces 2026-01-01T00:00:00Z)\n",
            r.Out);
    }

    /// <summary>The line that closes the loop: audit's ids piped into verify.</summary>
    [Fact]
    public void Verify_reads_ids_from_stdin_when_the_id_is_a_dash()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);

        var r = TestPaths.RunWithStdin(
            "metrics/dau\n\nmetrics/rev\n",
            "verify", bundle, "-", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z");

        Assert.Equal(0, r.Code);
        // The blank line is ignored, both concepts are stamped, order preserved.
        Assert.Equal(
            "recorded metrics/dau  human:ada  2026-08-28T09:14:00Z\n"
            + "recorded metrics/rev  human:ada  2026-08-28T09:14:00Z\n",
            r.Out);
    }

    /// <summary>
    /// Fully validated first: every id is resolved before anything is written, so one
    /// unknown id leaves the whole bundle untouched.
    /// </summary>
    [Fact]
    public void Verify_writes_nothing_when_one_id_is_unknown()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var before = File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md"));

        var r = Run("verify", bundle, "metrics/dau", "metrics/nope", "--by", "human:ada");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: unknown concept \"metrics/nope\"\n", r.Err);
        Assert.Equal(before, File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    /// <summary>
    /// A document with no `type` loads into the bundle but is refused at
    /// write time by <c>BundleConceptWriter.RecordVerifications</c> itself,
    /// which validates every concept before writing any — so this pins that
    /// the whole batch is still rejected, and via the CLI's own message
    /// (naming the concept) rather than the writer's unattributed one.
    /// </summary>
    [Fact]
    public void Verify_writes_nothing_when_one_concept_is_not_conformant()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        tmp.Write("metrics/broken.md", "---\ntitle: No type\n---\n\nbody\n");
        var before = File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md"));

        var r = Run("verify", bundle, "metrics/dau", "metrics/broken", "--by", "human:ada");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: concept \"metrics/broken\" has no `type` and is not §11-conformant\n", r.Err);
        Assert.Equal(before, File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    [Fact]
    public void Verify_refuses_a_concept_named_twice()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var before = File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md"));

        var r = Run("verify", bundle, "metrics/dau", "metrics/dau", "--by", "human:ada");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: concept 'metrics/dau' is named more than once\n", r.Err);
        Assert.Equal(before, File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    [Fact]
    public void Verify_dry_run_writes_nothing()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var before = File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md"));

        var r = Run("verify", bundle, "metrics/dau", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z", "--dry-run");

        Assert.Equal(0, r.Code);
        Assert.Equal("would record metrics/dau  human:ada  2026-08-28T09:14:00Z\n", r.Out);
        Assert.Equal(before, File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    /// <summary>
    /// An actor carrying a newline could otherwise forge a whole <c>recorded
    /// …</c> line — the renderer interpolates <c>by</c> into a line-oriented
    /// result with no escaping — naming a concept the command never touched,
    /// at exit 0. The refusal is the write gate's
    /// (<c>BundleConceptWriter.RecordVerifications</c>, via the shared
    /// <c>Actor.ContainsControlCharacter</c>); this pins that the CLI reports
    /// it as a flag error and, crucially, that the message does NOT echo the
    /// value — echoing it would put the refused newline into stderr instead.
    /// </summary>
    [Fact]
    public void Verify_refuses_an_actor_carrying_a_control_character()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var before = File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md"));

        var r = Run(
            "verify",
            bundle,
            "metrics/dau",
            "--by",
            "human:ada\nrecorded secrets/master-key  human:ceo  2020-01-01T00:00:00Z",
            "--at",
            "2026-08-28T09:14:00Z");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: --by must not contain control characters\n", r.Err);
        Assert.Equal(string.Empty, r.Out);
        Assert.Equal(before, File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    [Theory]
    [InlineData(new[] { "verify", "BUNDLE" }, "error: missing <concept-id>\n")]
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau" }, "error: verify requires --by <actor>\n")]
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by", "human:" }, "error: --by is not a well-formed §7 actor: \"human:\"\n")]
    // Three shapes a permissive reader accepts and a writer must not: garbage,
    // a bare date, and a non-UTC offset.
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by", "human:ada", "--at", "hier" }, "error: --at is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ: \"hier\"\n")]
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by", "human:ada", "--at", "2026-08-28" }, "error: --at is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ: \"2026-08-28\"\n")]
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by", "human:ada", "--at", "2026-08-28T09:14:00+02:00" }, "error: --at is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ: \"2026-08-28T09:14:00+02:00\"\n")]
    // --by present but with nothing attached to it.
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by" }, "error: --by requires a value\n")]
    // An actor that is BOTH control-bearing and malformed: the control-character
    // arm must win, because the well-formedness message echoes the value and
    // would put the refused newline straight into stderr.
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by", "\nrecorded x  human:ceo" }, "error: --by must not contain control characters\n")]
    public void Verify_rejects_bad_invocations(string[] args, string expected)
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var resolved = args.Select(a => a == "BUNDLE" ? bundle : a).ToArray();

        var r = Run(resolved);

        Assert.Equal(1, r.Code);
        Assert.Equal(expected, r.Err);
    }

    /// <summary>
    /// The documented pipeline (<c>okf audit … --trust unverified | cut … |
    /// okf verify … -</c>) must be idempotent. <c>okf audit --trust
    /// unverified</c> deliberately exits 0 with no output when nothing needs
    /// attention, so <c>verify</c> on that empty stream is "nothing to do" —
    /// exiting 1 there made the headline pipeline fail under <c>set -e</c>
    /// exactly when the bundle was healthy, and the obvious operator
    /// workaround (<c>|| true</c>) would also have swallowed a real
    /// partial-write failure.
    /// </summary>
    [Fact]
    public void Verify_exits_zero_when_standard_input_is_empty()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var before = File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md"));

        var r = TestPaths.RunWithStdin(string.Empty, "verify", bundle, "-", "--by", "human:ada");

        Assert.Equal(0, r.Code);
        Assert.Equal(string.Empty, r.Out);
        Assert.Equal(string.Empty, r.Err);
        Assert.Equal(before, File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    /// <summary>
    /// An invocation already doomed by its own arguments must not drain the
    /// pipe first: behind a slow producer that is a pointless wait, and on a
    /// terminal it hangs until the user finds Ctrl-D. The reader here throws
    /// if anything reads it, so this fails rather than merely being slow. The
    /// message ordering the <c>[Theory]</c> above pins is unaffected — every
    /// one of those errors is decided from the argument list alone.
    /// </summary>
    [Fact]
    public void Verify_validates_the_flags_before_reading_standard_input()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);

        var r = TestPaths.RunWithReader(new ThrowingReader(), "verify", bundle, "-");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: verify requires --by <actor>\n", r.Err);
    }

    [Fact]
    public void Verify_refuses_to_mix_stdin_with_explicit_ids()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);

        var r = Run("verify", bundle, "-", "metrics/dau", "--by", "human:ada");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: \"-\" (stdin) cannot be combined with explicit concept ids\n", r.Err);
    }

    /// <summary>
    /// The write phase cannot be atomic across several files: RecordVerifications
    /// writes "metrics/dau" first, THEN fails writing "metrics/rev" (made
    /// unwritable below), so "dau" already landed on disk by the time the
    /// batch fails. This pins the exact contract fixed twice already —  once
    /// in the core (b25553b, moving <c>records.Add</c> out of the prepare
    /// loop so <c>Records</c> means "written", not "prepared") and once here,
    /// in the verb itself, which must print every landed record BEFORE
    /// throwing on <c>!outcome.Recorded</c> rather than swallow it. A version
    /// of <c>CmdVerify</c> that swapped that print loop and the throw would
    /// print nothing and still exit 1 -- indistinguishable from this test's
    /// perspective if it only checked the exit code, which is why stdout is
    /// asserted here, not just <c>r.Code</c>.
    ///
    /// Deliberately does NOT use the internal
    /// <see cref="BundleConceptWriter.BeforeLateReparseCheckForTest"/> hook
    /// <see cref="RecordVerificationTests"/> uses for the same kind of
    /// injected write-time failure: <see cref="CmdVerify"/> constructs its
    /// own private <see cref="BundleConceptWriter"/> instance that a test has
    /// no handle to, so that seam cannot be reached from here. Instead this
    /// makes the SECOND file genuinely unwritable (read-only) before
    /// invoking the verb at all -- a black-box failure any process,
    /// including a real filesystem permission error, could produce.
    /// </summary>
    [Fact]
    public void Verify_prints_the_records_that_landed_before_a_later_write_failure()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var revPath = Path.Combine(bundle, "metrics", "rev.md");
        var originalRev = File.ReadAllText(revPath);
        File.SetAttributes(revPath, File.GetAttributes(revPath) | FileAttributes.ReadOnly);

        try
        {
            // Probe before asserting anything real depends on it: some
            // environments (e.g. a CI job running as root on Linux) do not
            // enforce the read-only bit at all, which would silently turn
            // this into a false pass/fail rather than a skip. Restoring the
            // original content afterward keeps the probe write itself inert.
            try
            {
                File.WriteAllText(revPath, originalRev);
                return; // read-only wasn't enforced on this platform/user -- skip.
            }
            catch (UnauthorizedAccessException)
            {
                // Expected: read-only is enforced here, continue.
            }

            var r = Run("verify", bundle, "metrics/dau", "metrics/rev", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z");

            Assert.Equal(1, r.Code);
            // The concept written BEFORE the failure must be reported, not
            // swallowed -- this is the assertion a swapped print/throw order
            // would fail.
            Assert.Equal("recorded metrics/dau  human:ada  2026-08-28T09:14:00Z\n", r.Out);
            Assert.StartsWith("error: ", r.Err);
            Assert.DoesNotContain("recorded metrics/rev", r.Out);
            // The write really landed on disk, not just in memory.
            Assert.Contains("by: human:ada", File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
        }
        finally
        {
            File.SetAttributes(revPath, File.GetAttributes(revPath) & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// A library failure no verb anticipated must still exit like every other
    /// failure. A concept whose frontmatter parses and cannot be re-emitted
    /// (see <see cref="DeepYamlDocument"/>) reached <c>YamlEmitter</c>'s
    /// nesting guard, which threw a bare <c>InvalidOperationException</c>:
    /// <c>OkfCli.Run</c> caught only <c>CliOperationException</c>, so the
    /// process died with a stack trace. The emitter now raises an
    /// <c>OkfException</c> and <c>Run</c> catches that base type, which is a
    /// strict improvement for all nine verbs — no golden pinned a crash.
    /// </summary>
    [Fact]
    public void A_document_that_cannot_be_re_emitted_exits_cleanly_rather_than_crashing()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/deep.md", DeepYamlDocument.Text());

        var r = Run("verify", tmp.Path, "metrics/deep", "--by", "human:ada");

        Assert.Equal(1, r.Code);
        Assert.StartsWith("error: ", r.Err);
        Assert.Contains("nesting depth limit exceeded", r.Err);
        Assert.DoesNotContain("   at ", r.Err);
    }

    /// <summary>The loop, end to end: audit lists it, verify clears it.</summary>
    [Fact]
    public void Audit_then_verify_removes_the_concept_from_the_unverified_worklist()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");

        var before = Run("audit", tmp.Path, "--trust", "unverified");
        Assert.Contains("metrics/dau", before.Out);

        Run("verify", tmp.Path, "metrics/dau", "--by", "human:ada");

        var after = Run("audit", tmp.Path, "--trust", "unverified");
        Assert.Equal("", after.Out);
    }

    [Fact]
    public void Help_lists_verify_after_audit()
    {
        var r = Run("--help");

        var lines = r.Out.Split('\n').Select(l => l.TrimStart()).ToList();
        var auditIndex = lines.FindIndex(l => l.StartsWith("audit ", StringComparison.Ordinal));
        var verifyIndex = lines.FindIndex(l => l.StartsWith("verify ", StringComparison.Ordinal));

        Assert.True(auditIndex >= 0 && verifyIndex == auditIndex + 1);
    }

    /// <summary>
    /// A verb that does not document reading standard input must never touch
    /// it — otherwise `okf fmt file` inside a pipeline would block on a reader
    /// nobody is feeding. A StringReader could not prove this (it records
    /// nothing), so the reader here throws if anything reads it.
    /// </summary>
    [Fact]
    public void A_verb_that_does_not_read_stdin_never_touches_it()
    {
        var r = TestPaths.RunWithReader(
            new ThrowingReader(),
            "fmt",
            Path.Combine(BundlePath, "tables", "users.md"));

        Assert.Equal(0, r.Code);
        Assert.Contains("title: Users", r.Out);
    }

    /// <summary>A reader that fails the test if the CLI reads from it at all.</summary>
    private sealed class ThrowingReader : TextReader
    {
        public override int Peek() => throw new InvalidOperationException("stdin was read");

        public override int Read() => throw new InvalidOperationException("stdin was read");

        public override string? ReadLine() => throw new InvalidOperationException("stdin was read");
    }
}
