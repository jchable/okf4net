// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>The document start and header of the three views, the script table and the graph page name (spec §11.1, §12.3, §12.6, A25).</summary>
public class HtmlWriterHeaderTests
{
    private static ViewerSite Acme(TempDir src)
    {
        src.Write("acme_retail/tables/users.md", "---\ntype: Table\ntitle: Users\n---\nSee [orders](orders.md).\n");
        src.Write("acme_retail/tables/orders.md", "---\ntype: Table\ntitle: Orders\n---\nNo link.\n");
        return SiteModel.Build(Bundle.Load(Path.Combine(src.Path, "acme_retail")));
    }

    private static string Read(string dest, string rel)
        => File.ReadAllText(Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void The_page_header_is_the_spec_header()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        var page = Read(dest.Path, "tables/users.html");

        Assert.Contains("<header class=\"bar\"><div class=\"bar-in\">\n<a class=\"wordmark\" href=\"../index.html\">OKF4net<sup>§</sup></a>\n<span class=\"bar-sep\" aria-hidden=\"true\"></span>\n", page);
        Assert.Contains("<span class=\"bar-bundle\" id=\"okf-bundle-name\" title=\"acme_retail\">acme_retail</span>", page);
        Assert.Contains("<span class=\"bar-counts\" id=\"okf-bundle-counts\">2 concepts · 1 link</span>", page);
        Assert.Contains("<div class=\"bar-tools\" id=\"okf-tools\">\n<a class=\"okf-tool okf-tool-graph\" id=\"okf-global-graph\" href=\"../graph.html#tables/users\">Global graph</a>\n</div>\n</div></header>\n", page);
        Assert.DoesNotContain("okf-reading-view", page);
    }

    [Fact]
    public void The_html_element_declares_root_view_and_concept()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        Assert.StartsWith("<!doctype html>\n<html lang=\"en\" data-okf-root=\"../\" data-okf-view=\"page\" data-okf-concept=\"tables/users\">\n", Read(dest.Path, "tables/users.html"));
        Assert.StartsWith("<!doctype html>\n<html lang=\"en\" data-okf-root=\"\" data-okf-view=\"index\">\n", Read(dest.Path, "index.html"));
    }

    [Fact]
    public void The_three_views_link_the_graph_page_as_the_spec_says()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = Acme(src);
        HtmlWriter.Write(site, dest.Path);

        Assert.Contains("id=\"okf-global-graph\" href=\"../graph.html#tables/users\">Global graph</a>", Read(dest.Path, "tables/users.html"));
        Assert.Contains("id=\"okf-global-graph\" href=\"graph.html\">Global graph</a>", Read(dest.Path, "index.html"));
        Assert.Contains(
            "id=\"okf-global-graph\" href=\"graph.html\" aria-current=\"page\">Global graph</a>",
            HtmlWriter.RenderDocumentStart(site, HtmlWriter.ViewKind.Graph, "Global graph", string.Empty, null));
    }

    [Fact]
    public void The_graph_view_has_Reading_view_before_the_current_Global_graph()
    {
        using var src = new TempDir();
        var start = HtmlWriter.RenderDocumentStart(Acme(src), HtmlWriter.ViewKind.Graph, "Global graph", string.Empty, null);

        Assert.StartsWith("<!doctype html>\n<html lang=\"en\" data-okf-root=\"\" data-okf-view=\"graph\">\n", start);
        Assert.Contains("<title>Global graph</title>", start);
        var reading = start.IndexOf("<a class=\"okf-tool\" id=\"okf-reading-view\" href=\"index.html\">Reading view</a>\n", StringComparison.Ordinal);
        var graph = start.IndexOf("<a class=\"okf-tool okf-tool-graph\" id=\"okf-global-graph\" href=\"graph.html\" aria-current=\"page\">", StringComparison.Ordinal);
        Assert.True(reading >= 0 && graph > reading, "Reading view must come right before the current Global graph");
        Assert.EndsWith("</div></header>\n", start);
    }

    [Fact]
    public void Skip_to_content_is_the_first_element_of_the_body_and_main_is_its_target()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        var page = Read(dest.Path, "tables/users.html");

        Assert.Contains("<body>\n<a class=\"okf-skip\" href=\"#okf-main\">Skip to content</a>\n<div class=\"topline\"></div>\n<header class=\"bar\">", page);
        Assert.Contains("<main id=\"okf-main\">\n", page);
    }

    [Fact]
    public void The_head_loads_the_theme_script_then_the_stylesheet_and_nothing_else()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        var page = Read(dest.Path, "tables/users.html");
        var head = page[..page.IndexOf("</head>", StringComparison.Ordinal)];

        Assert.Single(Regex.Matches(head, "<script "));
        Assert.EndsWith("<script src=\"../assets/okf-theme.js\"></script>\n<link rel=\"stylesheet\" href=\"../assets/viewer.css\">\n", head);
    }

    [Fact]
    public void Pages_and_the_index_load_the_script_table_in_order_after_the_payload()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        Assert.Equal(
            new[] { "okf-resize.js", "marked.min.js", "viewer.js", "okf-index.js", "okf-site.js", "okf-shapes.js", "okf-explorer.js", "okf-palette.js", "okf-toc.js", "okf-page.js" },
            HtmlWriter.PageScripts.Take(10));
        foreach (var (rel, prefix) in new[] { ("tables/users.html", "../"), ("index.html", string.Empty) })
        {
            var page = Read(dest.Path, rel);
            var tail = page[page.IndexOf("id=\"okf-payload\"", StringComparison.Ordinal)..];
            var loaded = Regex.Matches(tail, "<script src=\"" + Regex.Escape(prefix) + "assets/([^\"]+)\"></script>").Select(m => m.Groups[1].Value);
            Assert.Equal(HtmlWriter.PageScripts, loaded);
        }
    }

    [Fact]
    public void Every_script_and_stylesheet_a_page_loads_is_written()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        foreach (var html in Directory.EnumerateFiles(dest.Path, "*.html", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(html), "(?:src|href)=\"([^\"#]*assets/[^\"#]+)\""))
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(html)!, m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
                Assert.True(File.Exists(target), $"{html} loads {m.Groups[1].Value}, which was not written");
            }
        }
    }

    [Fact]
    public void The_index_view_has_the_header_and_no_page_head()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        var index = Read(dest.Path, "index.html");

        Assert.Contains("id=\"okf-bundle-name\"", index);
        Assert.Contains("<h1>Bundle index</h1>", index);
        Assert.Contains("<p class=\"meta\">2 concepts</p>", index);
        Assert.DoesNotContain("okf-page-head", index);
    }

    [Fact]
    public void The_contents_title_is_a_shared_section_title()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        Assert.Contains("<h2 id=\"okf-toc-title\" class=\"okf-section-title\">On this page</h2>", Read(dest.Path, "tables/users.html"));
    }

    [Fact]
    public void A_hostile_bundle_name_is_escaped_in_text_and_title()
    {
        using var src = new TempDir();
        var site = new ViewerSite(src.Path, [], string.Empty, []) { BundleName = "<img src=x onerror=alert(1)>\"'&" };

        var header = HtmlWriter.RenderHeader(site, HtmlWriter.ViewKind.Index, string.Empty, null);

        Assert.DoesNotContain("<img", header);
        Assert.Contains("title=\"&lt;img src=x onerror=alert(1)&gt;&quot;'&amp;\">&lt;img src=x onerror=alert(1)&gt;&quot;'&amp;</span>", header);
    }

    [Fact]
    public void A_hand_built_site_takes_its_bundle_name_from_its_root()
    {
        using var src = new TempDir();
        var site = new ViewerSite(Path.Combine(src.Path, "my-bundle"), [], string.Empty, []);

        Assert.Contains(">my-bundle</span>", HtmlWriter.RenderHeader(site, HtmlWriter.ViewKind.Index, string.Empty, null));
    }

    [Fact]
    public void Counts_are_singular_for_one_and_count_links_to_absent_concepts()
    {
        using var src = new TempDir();
        src.Write("a.md", "---\ntype: Note\ntitle: A\n---\nSee [gone](gone.md).\n");

        Assert.Equal("1 concept · 1 link", HtmlWriter.Counts(SiteModel.Build(Bundle.Load(src.Path))));
    }

    [Fact]
    public void Counts_are_omitted_for_a_hand_built_site_with_pages_but_no_index()
    {
        using var src = new TempDir();
        var page = new ViewerPage(ConceptId.Parse("x"), "X", "x.html", [], string.Empty, [], []);

        Assert.Null(HtmlWriter.Counts(new ViewerSite(src.Path, [page], string.Empty, [])));
        Assert.Equal("0 concepts · 0 links", HtmlWriter.Counts(new ViewerSite(src.Path, [], string.Empty, [])));
        Assert.DoesNotContain("okf-bundle-counts", HtmlWriter.RenderHeader(new ViewerSite(src.Path, [page], string.Empty, []), HtmlWriter.ViewKind.Index, string.Empty, null));
    }

    [Fact]
    public void A_concept_named_graph_moves_the_link_to_graph_1_and_the_site_still_renders()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("graph.md", "---\ntype: Note\ntitle: Graph\n---\n");

        HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);

        Assert.Contains("id=\"okf-global-graph\" href=\"graph-1.html#graph\">", Read(dest.Path, "graph.html"));
    }

    [Theory]
    [InlineData("my graph.html")]
    [InlineData("sub/graph.html")]
    [InlineData("graph.htm")]
    [InlineData(".html")]
    [InlineData("Graph.HTML")]
    [InlineData("<x>.html")]
    public void An_explicit_graph_page_must_be_one_safe_html_segment(string name)
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = new ViewerSite(src.Path, [], string.Empty, []) { GraphPagePath = name };

        Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, dest.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dest.Path));
    }

    [Theory]
    [InlineData("map.html", "map.html")]
    [InlineData("map.html", "Map.html")]
    [InlineData("index.html", "x.html")]
    [InlineData("map.html", "map.html/x.html")]
    [InlineData("map.html", "MAP.html/x/y.html")]
    public void An_explicit_graph_page_that_collides_is_refused_before_writing(string graph, string pagePath)
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = new ViewerPage(ConceptId.Parse("x"), "X", pagePath, [], string.Empty, [], []);
        var site = new ViewerSite(src.Path, [page], string.Empty, []) { GraphPagePath = graph };

        Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, dest.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dest.Path));
    }

    [Fact]
    public void Write_links_the_computed_graph_page_from_every_page_of_a_hand_built_site()
    {
        // A computed name walks every page; Write computes it once and pins it
        // on the site (site with { GraphPagePath = ... }), so the name is not
        // recomputed per page by construction. This test pins the behaviour on
        // a large hand-built site (no GraphPagePath set): never timed.
        using var src = new TempDir();
        using var dest = new TempDir();
        var pages = Enumerable.Range(0, 3000)
            .Select(i => new ViewerPage(ConceptId.Parse($"p{i}"), $"P{i}", $"p{i}.html", [], string.Empty, [], []))
            .ToList();
        var site = new ViewerSite(src.Path, pages, string.Empty, []);

        HtmlWriter.Write(site, dest.Path);

        Assert.Contains("id=\"okf-global-graph\" href=\"graph.html#p0\">", Read(dest.Path, "p0.html"));
        Assert.Contains("id=\"okf-global-graph\" href=\"graph.html#p2999\">", Read(dest.Path, "p2999.html"));
    }

    [Fact]
    public void Reading_view_and_the_wordmark_share_the_root_prefix()
    {
        using var src = new TempDir();
        var start = HtmlWriter.RenderDocumentStart(Acme(src), HtmlWriter.ViewKind.Graph, "Global graph", "../", null);

        Assert.Contains("<a class=\"wordmark\" href=\"../index.html\">", start);
        Assert.Contains("id=\"okf-reading-view\" href=\"../index.html\">Reading view</a>", start);
    }

    [Fact]
    public void A_valid_explicit_graph_page_is_the_one_linked()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = new ViewerPage(ConceptId.Parse("x"), "X", "x.html", [], string.Empty, [], []);

        HtmlWriter.Write(new ViewerSite(src.Path, [page], string.Empty, []) { GraphPagePath = "map.html" }, dest.Path);

        Assert.Contains("id=\"okf-global-graph\" href=\"map.html#x\">", Read(dest.Path, "x.html"));
    }
}
