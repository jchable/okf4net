// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// The resizable side panel: <c>okf-resize.js</c> is written under
/// <c>assets/</c> and is the first script of every concept page, of the index
/// and of the graph page, so a stored width is in place before the other
/// scripts fill the page. What the script does is checked by
/// <c>tools/viewer-security-check/cases/p4.js</c>: xunit cannot execute it,
/// so these are presence and order checks, not proof of behaviour.
/// </summary>
public class HtmlWriterResizeTests
{
    private static string WriteSite(TempDir src, TempDir dest)
    {
        src.Write("a.md", "---\ntype: Note\ntitle: A\ndescription: d\n---\nSee [b](/tables/b.md).\n");
        src.Write("tables/b.md", "---\ntype: Note\ntitle: B\ndescription: d\n---\nBody.\n");
        HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);
        return dest.Path;
    }

    private static List<string> ScriptSources(string html)
        => Regex.Matches(html, "<script[^>]*[ ]src=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void Write_writes_okf_resize_js_under_assets_with_its_header()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var outDir = WriteSite(src, dest);

        var script = File.ReadAllText(Path.Combine(outDir, "assets", "okf-resize.js"));

        Assert.StartsWith("// SPDX-License-Identifier: LGPL-3.0-or-later", script);
        Assert.Contains("assets/okf-resize.js", ScriptSources(File.ReadAllText(Path.Combine(outDir, "a.html"))).Select(s => s.Replace("../", string.Empty)));
        Assert.Contains("okf-resize.js", ViewerAssets.Paths);
    }

    [Theory]
    [InlineData("a.html", "")]
    [InlineData("tables/b.html", "../")]
    [InlineData("index.html", "")]
    [InlineData("graph.html", "")]
    public void Every_page_loads_okf_resize_js_once_and_first_of_its_scripts(string page, string prefix)
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var outDir = WriteSite(src, dest);

        var html = File.ReadAllText(Path.Combine(outDir, page.Replace('/', Path.DirectorySeparatorChar)));
        // The theme script in <head> is a different matter: this is the body's table.
        var sources = ScriptSources(html[html.IndexOf("<body>", StringComparison.Ordinal)..]);

        Assert.Single(sources, s => s.EndsWith("okf-resize.js", StringComparison.Ordinal));
        Assert.Equal(prefix + "assets/okf-resize.js", sources[0]);
    }

    [Fact]
    public void The_script_tables_start_with_okf_resize_js()
    {
        Assert.Equal("okf-resize.js", HtmlWriter.PageScripts[0]);
        Assert.Equal("marked.min.js", HtmlWriter.PageScripts[1]);
    }
}
