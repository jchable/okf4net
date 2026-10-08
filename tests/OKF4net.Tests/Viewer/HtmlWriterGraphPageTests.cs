// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// Tests for the global graph page (spec §12.5), written by
/// <c>HtmlWriter.RenderGraph</c>. Its scripts are guarded by the jsdom harness
/// (<c>tools/viewer-security-check/cases/p3.js</c>), not here.
/// </summary>
public class HtmlWriterGraphPageTests
{
    private static readonly string[] GraphScripts =
    [
        "assets/okf-index.js", "assets/okf-site.js", "assets/okf-shapes.js",
        "assets/okf-palette.js", "assets/okf-sim.js", "assets/okf-graph.js",
    ];

    private static string Concept(string title, string body = "")
        => $"---\ntype: Note\ntitle: {title}\ndescription: d\n---\n{body}";

    private static IReadOnlyList<string> Render(TempDir src, TempDir dest)
        => HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);

    private static string Read(TempDir dest, string relative)
        => File.ReadAllText(Path.Combine(dest.Path, relative));

    private static string GlobalGraphHref(string html)
    {
        var link = Regex.Match(html, "<a [^>]*id=\"okf-global-graph\"[^>]*>");
        Assert.True(link.Success, "no #okf-global-graph link");
        return Regex.Match(link.Value, "href=\"([^\"]*)\"").Groups[1].Value;
    }

    [Fact]
    public void Write_writes_the_graph_page_at_the_site_root_in_the_graph_view()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a.md", Concept("A"));

        Render(src, dest);

        var html = Read(dest, "graph.html");
        Assert.Contains("data-okf-view=\"graph\"", html);
        Assert.Contains("data-okf-root=\"\"", html);
        Assert.Contains("id=\"okf-reading-view\"", html);
    }

    [Fact]
    public void The_graph_page_loads_exactly_its_six_scripts_in_order()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a.md", Concept("A"));

        Render(src, dest);

        var html = Read(dest, "graph.html");
        var body = html[html.IndexOf("id=\"okf-graph-layout\"", StringComparison.Ordinal)..];
        var scripts = Regex.Matches(body, "<script[^>]*\\ssrc=\"([^\"]+)\"").Select(m => m.Groups[1].Value);
        Assert.Equal(GraphScripts, scripts);
    }

    [Fact]
    public void The_graph_page_has_no_payload_and_none_of_the_reading_scripts()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a.md", Concept("A", "Some **body**."));

        Render(src, dest);

        var html = Read(dest, "graph.html");
        Assert.DoesNotContain("okf-payload", html);
        Assert.DoesNotContain("id=\"okf-body\"", html);
        foreach (var script in new[] { "marked.min.js", "assets/viewer.js", "okf-toc.js", "okf-explorer.js", "okf-page.js", "okf-local.js" })
        {
            Assert.DoesNotContain(script, html);
        }
    }

    [Fact]
    public void The_graph_page_holds_the_skeleton_okf_graph_js_fills_in_order()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a.md", Concept("A"));

        Render(src, dest);

        var html = Read(dest, "graph.html");
        string[] pieces =
        [
            "<div class=\"okf-graph-layout\" id=\"okf-graph-layout\">",
            "<aside class=\"okf-facets\" id=\"okf-facets\" aria-label=\"Facets\"></aside>",
            "<main id=\"okf-main\" class=\"okf-graph-main\" aria-label=\"Global graph\">",
            "<p class=\"okf-graph-status\" id=\"okf-graph-status\" role=\"status\"></p>",
            "<div class=\"okf-graph-zoom\" id=\"okf-graph-zoom\"></div>",
            "<div class=\"okf-graph-canvas\" id=\"okf-graph-canvas\"></div>",
            "<section class=\"okf-graph-list\" id=\"okf-graph-list\" aria-label=\"Concepts and links\" hidden></section>",
            "<p class=\"okf-graph-legend\" id=\"okf-graph-legend\"></p>",
            "</main>",
            "<aside class=\"okf-graph-detail\" id=\"okf-graph-detail\" aria-label=\"Selected concept\"></aside>",
            "</div>",
            "<noscript><p>The graph needs JavaScript. <a href=\"index.html\">Bundle index</a></p></noscript>",
        ];
        var at = 0;
        foreach (var piece in pieces)
        {
            var next = html.IndexOf(piece, at, StringComparison.Ordinal);
            Assert.True(next >= 0, $"missing, or out of order: {piece}");
            at = next + piece.Length;
        }

        Assert.Single(Regex.Matches(html, "id=\"okf-main\""));
    }

    [Fact]
    public void A_concept_named_graph_moves_the_graph_page_to_the_first_free_name()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("graph.md", Concept("A concept called graph"));

        var written = Render(src, dest);

        Assert.Contains("graph-1.html", written);
        Assert.Contains("id=\"okf-graph-layout\"", Read(dest, "graph-1.html"));
        Assert.Contains("id=\"okf-payload\"", Read(dest, "graph.html"));
        Assert.Equal("graph-1.html", GlobalGraphHref(Read(dest, "index.html")));
    }

    [Fact]
    public void The_graph_page_is_written_after_the_index_and_before_the_pages()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a.md", Concept("A"));
        src.Write("tables/users.md", Concept("Users"));

        var written = Render(src, dest).ToList();

        var graph = written.IndexOf("graph.html");
        Assert.Equal(written.IndexOf("index.html") + 1, graph);
        Assert.True(written.IndexOf("a.html") > graph && written.IndexOf("tables/users.html") > graph);
    }

    [Fact]
    public void A_site_built_by_hand_still_gets_its_graph_page()
    {
        using var bundle = new TempDir();
        using var dest = new TempDir();
        var site = new ViewerSite(bundle.Path, [], "# Hand-built", []);

        HtmlWriter.Write(site, dest.Path);

        Assert.Contains("id=\"okf-graph-layout\"", Read(dest, "graph.html"));
    }

    [Fact]
    public void The_simulation_and_graph_scripts_are_written_as_assets()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a.md", Concept("A"));

        Render(src, dest);

        foreach (var script in new[] { "okf-sim.js", "okf-graph.js" })
        {
            Assert.StartsWith("// SPDX-License-Identifier: LGPL-3.0-or-later", Read(dest, Path.Combine("assets", script)));
        }
    }
}
