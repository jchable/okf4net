// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Render;

namespace OKF4net.Tests.Render;

/// <summary>
/// Smoke tests for the <c>okf-render</c> binary, exercising
/// <see cref="OkfRenderCli.Run"/> in-process (no subprocess spawn). This is
/// the coverage that used to live on <c>okf render</c> in <c>CliTests</c>,
/// moved here after <c>render</c> was split out of <c>okf</c> into its own
/// Native AOT binary (<c>OKF4net.Render</c>) so the CI-facing <c>okf</c>
/// validator does not carry the vendored viewer JavaScript it never executes.
/// </summary>
public class OkfRenderCliTests
{
    // Same fixture used by CliTests, resolved the same way: `dotnet test`
    // runs from the test assembly's output folder, not the repo root.
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (OkfRenderCli.Run(args, o, e), o.ToString(), e.ToString());
    }

    [Fact]
    public void Renders_a_site_and_reports_success()
    {
        using var dest = new TempDir();
        var outDir = Path.Combine(dest.Path, "site");

        var r = Run(BundlePath, "--out", outDir);

        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Err);
        Assert.True(File.Exists(Path.Combine(outDir, "index.html")));
    }

    [Fact]
    public void Without_out_fails()
    {
        var r = Run(BundlePath);
        Assert.Equal(1, r.Code);

        // Regression guard: this message used to read "render requires --out
        // <dir>", naming a verb this binary never exposes (it was ported
        // verbatim from the old `okf render` verb). It must name the flag or
        // the tool instead.
        Assert.Contains("--out", r.Err);
        Assert.DoesNotContain("render requires", r.Err);
    }

    [Fact]
    public void No_args_prints_usage_and_fails()
    {
        // A bare invocation is the discovery gesture for a one-command tool:
        // OkfCli.Run does exactly this for zero arguments (full Usage block
        // on stderr, no "error: " prefix), and this tool should behave the
        // same way rather than surfacing "error: missing <bundle>".
        var r = Run();
        Assert.Equal(1, r.Code);
        Assert.Contains("USAGE:", r.Err);
        Assert.Equal("", r.Out);
    }

    [Fact]
    public void Into_the_bundle_itself_fails()
    {
        // Regression guard: if this check ever weakens, `dotnet test` would
        // write generated HTML straight into whatever bundle path is passed
        // here. Use a throwaway bundle in a TempDir -- never BundlePath,
        // which is the byte-exact golden fixture tests/fixtures/appendix_a --
        // so a regression can never corrupt the real goldens the
        // golden-parity tests depend on.
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");

        var r = Run(tmp.Path, "--out", Path.Combine(tmp.Path, "site"));

        Assert.Equal(1, r.Code);
        Assert.Contains("error:", r.Err);

        // Regression guard for the .NET ArgumentException(paramName) leaking
        // its " (Parameter 'outDir')" framework-noise suffix into CLI output
        // meant for humans -- HtmlWriter.Write is a library API and correctly
        // keeps throwing with paramName set; this tool must strip it before
        // printing.
        Assert.DoesNotContain("Parameter", r.Err);
    }

    [Fact]
    public void Out_flag_missing_its_value_after_the_bundle_fails_with_out_message()
    {
        var r = Run(BundlePath, "--out");

        Assert.Equal(1, r.Code);
        Assert.Contains("--out requires a value", r.Err);
    }

    [Fact]
    public void Bare_out_flag_and_no_bundle_fails_with_out_message_not_missing_bundle()
    {
        // Same "--out present but unvalued" failure as the test above, just
        // with the bundle positional also absent. The two spellings must
        // agree: "--out"'s own bounds check always wins over the missing
        // positional, regardless of what else is absent.
        var r = Run("--out");

        Assert.Equal(1, r.Code);
        Assert.Contains("--out requires a value", r.Err);
        Assert.DoesNotContain("missing <bundle>", r.Err);
    }

    [Fact]
    public void Only_out_and_no_bundle_fails_rather_than_treating_the_out_dir_as_the_bundle()
    {
        // --out is this tool's only valued flag, so a naive "first arg not
        // starting with '-'" scan would read the value of --out as the
        // bundle path when the bundle itself is omitted. Guard against
        // silently rendering the output directory as if it were the bundle.
        using var dest = new TempDir();
        var outDir = Path.Combine(dest.Path, "site");

        var r = Run("--out", outDir);

        Assert.Equal(1, r.Code);
        Assert.Contains("error:", r.Err);
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void Unknown_option_is_rejected()
    {
        using var tmp = new TempDir();
        tmp.Write("notes/thing.md", "---\ntype: Note\ntitle: Thing\ndescription: A thing.\n---\n\nbody\n");

        var r = Run(tmp.Path, "--bogus");

        Assert.Equal(1, r.Code);
        Assert.Contains("unknown option: --bogus", r.Err);
        Assert.Equal("", r.Out);
    }

    [Fact]
    public void Extra_positional_is_rejected()
    {
        using var tmp = new TempDir();
        tmp.Write("notes/thing.md", "---\ntype: Note\ntitle: Thing\ndescription: A thing.\n---\n\nbody\n");

        var r = Run(tmp.Path, "extra", "--out", Path.Combine(tmp.Path, "site"));

        Assert.Equal(1, r.Code);
        Assert.Contains("unexpected argument: extra", r.Err);
    }

    [Fact]
    public void Help_prints_usage_and_succeeds()
    {
        var r = Run("--help");
        Assert.Equal(0, r.Code);
        Assert.Contains("USAGE:", r.Out);
        Assert.Contains("okf-render", r.Out);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public void Short_help_flag_also_works()
    {
        var r = Run("-h");
        Assert.Equal(0, r.Code);
        Assert.Contains("USAGE:", r.Out);
    }

    [Fact]
    public void Version_prints_and_succeeds()
    {
        var r = Run("--version");
        Assert.Equal(0, r.Code);
        Assert.Contains("okf-render ", r.Out);
        Assert.Contains("OKF spec v0.2", r.Out);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public void Short_version_flag_also_works()
    {
        var r = Run("-V");
        Assert.Equal(0, r.Code);
        Assert.Contains("okf-render ", r.Out);
    }
}
