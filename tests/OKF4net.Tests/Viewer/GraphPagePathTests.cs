// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>The graph page's file name never collides with a page or a folder (spec §12.3, A25).</summary>
public class GraphPagePathTests
{
    private static ViewerPage Page(string path) => new(ConceptId.Parse("x"), "X", path, [], string.Empty, [], []);

    [Theory]
    [InlineData(new string[0], "graph.html")]
    [InlineData(new[] { "graph.html" }, "graph-1.html")]
    [InlineData(new[] { "Graph.html" }, "graph-1.html")]
    [InlineData(new[] { "graph.html/x.html" }, "graph-1.html")]
    [InlineData(new[] { "GRAPH.HTML/x/y.html" }, "graph-1.html")]
    [InlineData(new[] { "graph.html", "graph-1.html" }, "graph-2.html")]
    [InlineData(new[] { "graph-1.html" }, "graph.html")]
    [InlineData(new[] { "a/graph.html" }, "graph.html")]
    public void The_graph_page_takes_the_first_free_name(string[] pages, string expected)
        => Assert.Equal(expected, SiteModel.FreeGraphPagePath(pages.Select(Page).ToList()));

    [Fact]
    public void A_concept_named_graph_moves_the_graph_page_aside_and_the_site_still_renders()
    {
        using var tmp = new TempDir();
        tmp.Write("graph.md", "---\ntype: Note\ntitle: Graph\n---\n");

        Assert.Equal("graph-1.html", SiteModel.Build(Bundle.Load(tmp.Path)).GraphPagePath);
    }
}
