// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Linq;
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
        var r = Run("render", BundlePath, "--out", Path.Combine(BundlePath, "site"));
        Assert.Equal(1, r.Code);
        Assert.Contains("error:", r.Err);
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
}
