// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// Slice P2 (local graph): <c>okf-local.js</c> is written under <c>assets/</c>
/// and loaded last by every concept page and by the index (spec §4.1, §12.0,
/// §12.6). What the script does is checked by
/// <c>tools/viewer-security-check/cases/p2.js</c>: xunit cannot execute it.
/// </summary>
public class HtmlWriterLocalGraphTests
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
    public void Write_writes_okf_local_js_under_assets()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var outDir = WriteSite(src, dest);

        var script = File.ReadAllText(Path.Combine(outDir, "assets", "okf-local.js"));

        Assert.Contains("window.OkfLocal = Object.freeze(", script);
    }

    [Theory]
    [InlineData("a.html", "")]
    [InlineData("tables/b.html", "../")]
    [InlineData("index.html", "")]
    public void Concept_pages_and_the_index_load_okf_local_js_once_and_last(string page, string prefix)
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var outDir = WriteSite(src, dest);

        var html = File.ReadAllText(Path.Combine(outDir, page.Replace('/', Path.DirectorySeparatorChar)));
        var sources = ScriptSources(html);

        Assert.Single(sources, s => s.EndsWith("okf-local.js", StringComparison.Ordinal));
        Assert.Equal(prefix + "assets/okf-local.js", sources[^1]);

        // It reads the index, OkfSite and OkfShapes, and comes after the page
        // head's script (spec §12.6), so all four load before it.
        foreach (var dependency in new[] { "okf-index.js", "okf-site.js", "okf-shapes.js", "okf-page.js" })
        {
            Assert.InRange(sources.IndexOf(prefix + "assets/" + dependency), 0, sources.Count - 2);
        }
    }
}
