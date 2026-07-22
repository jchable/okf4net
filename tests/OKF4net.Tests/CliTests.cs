// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Cli;

namespace OKF4net.Tests;

/// <summary>
/// Smoke tests for the <c>okf</c> CLI, exercising <see cref="OkfCli.Run"/>
/// in-process (no subprocess spawn). One test per subcommand plus the
/// no-args/usage path, mirroring the shape of <c>src/bin/okf.rs</c>'s
/// dispatch table. Exact exit codes and output text are read from
/// <c>okf.rs</c> (the port's source of truth) rather than invented.
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
        Assert.Contains("OKF spec v0.1", r.Out);
    }

    [Fact]
    public void Validate_conformant_bundle_exits_zero()
    {
        var r = Run("validate", BundlePath);
        Assert.Equal(0, r.Code);
        Assert.Contains("conformant with OKF v0.1", r.Out);
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
    public void Info_prints_summary()
    {
        var r = Run("info", BundlePath);
        Assert.Equal(0, r.Code);
        Assert.Contains("concepts:   4", r.Out);
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
    // message on stderr, never an unhandled-exception stack trace --
    // mirroring Rust's io::Error -> `.to_string()` -> `error: {msg}` funnel
    // (okf.rs's `Err(msg) => { eprintln!("error: {msg}"); ... }`, main.rs:50-53).
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
        // Deliberately NOT an "error: ..." exit-1 case: Rust's regenerate_indexes
        // (index.rs:93-97) checks `bundle_root.exists()` first, which -- like
        // .NET's Directory.Exists -- swallows the underlying failure and
        // simply returns false for a garbage path, so the function returns
        // Ok(empty) rather than an io::Error. cmd_index then reports "no
        // index files written" and exits 0. Audited (A3) to confirm this
        // command needed no change, unlike parse/fmt/validate/info/graph.
        var r = Run("index", "x\0y");
        Assert.Equal(0, r.Code);
        Assert.Contains("no index files written", r.Out);
        Assert.Equal("", r.Err);
    }
}
