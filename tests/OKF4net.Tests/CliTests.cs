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

        Assert.Equal(1, root.GetProperty("trust").GetProperty("humanReviewed").GetInt32());
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
}
