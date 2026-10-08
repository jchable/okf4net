// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// Tests for <c>assets/okf-index.js</c> serialization (spec §3.4). These
/// prove the text round-trips as JSON; they do not prove what the script
/// creates when a browser EXECUTES it -- the jsdom harness
/// (tools/viewer-security-check/) runs a generated index for that.
/// </summary>
public class IndexScriptTests
{
    private const string EmptyScript =
        "window.OKF_INDEX = {\"version\":2,\"concepts\":[],\"ghosts\":[],\"edges\":[],\"tree\":[],\"types\":[]};\n";

    private static JsonElement Parse(string script)
    {
        Assert.StartsWith(IndexScript.Prefix, script);
        Assert.EndsWith(";\n", script);
        var json = script[IndexScript.Prefix.Length..^2];
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void The_empty_index_is_a_classic_script_assigning_window_OKF_INDEX()
        => Assert.Equal(EmptyScript, IndexScript.Render(ViewerIndex.Empty));

    [Fact]
    public void An_empty_bundle_renders_the_empty_index()
    {
        using var tmp = new TempDir();

        Assert.Equal(EmptyScript, IndexScript.Render(SiteIndex.Build(Bundle.Load(tmp.Path))));
    }

    [Fact]
    public void Hostile_text_round_trips_and_cannot_close_a_script_or_break_a_line()
    {
        // Built at run time: a literal line-separator character typed into
        // this file would be a raw line break inside a C# string literal.
        var lineSeparator = ((char)0x2028).ToString();
        var paragraphSeparator = ((char)0x2029).ToString();
        var hostile = "</script><img src=x onerror=alert(1)>" + lineSeparator + paragraphSeparator + "\"\\";
        var concept = new IndexConcept(ConceptId.Parse("a"), hostile, hostile, [hostile], "a.html", "unverified", null, null);
        var index = new ViewerIndex([concept], [], [], [new IndexTreeNode(hostile, 0, [])]) { Types = [new IndexType(hostile, 1, 0)] };

        var script = IndexScript.Render(index);

        Assert.DoesNotContain("<", script);
        Assert.DoesNotContain(lineSeparator, script);
        Assert.DoesNotContain(paragraphSeparator, script);
        var root = Parse(script);
        var parsed = root.GetProperty("concepts")[0];
        Assert.Equal(hostile, parsed.GetProperty("title").GetString());
        Assert.Equal(hostile, parsed.GetProperty("type").GetString());
        Assert.Equal(hostile, parsed.GetProperty("tags")[0].GetString());
        Assert.Equal(hostile, root.GetProperty("tree")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Stale_fields_render_as_a_number_and_a_date_or_as_null()
    {
        var dated = new IndexConcept(ConceptId.Parse("a"), "A", "Note", [], "a.html", "unverified", 1791244800001L, "2026-10-06");
        var undated = new IndexConcept(ConceptId.Parse("b"), "B", "Note", [], "b.html", "unverified", null, null);

        var script = IndexScript.Render(new ViewerIndex([dated, undated], [], [], []) { Types = [new IndexType("Note", 2, 0)] });

        Assert.Contains("\"staleAfterMs\":1791244800001,\"staleAfterDate\":\"2026-10-06\"", script);
        Assert.Contains("\"staleAfterMs\":null,\"staleAfterDate\":null", script);
    }

    [Fact]
    public void Edges_render_as_quadruples_and_ghosts_as_ids()
    {
        var a = new IndexConcept(ConceptId.Parse("a"), "A", "Note", [], "a.html", "unverified", null, null);
        var b = new IndexConcept(ConceptId.Parse("b"), "B", "Note", [], "b.html", "unverified", null, null);
        var index = new ViewerIndex(
            [a, b],
            [new IndexGhost(ConceptId.Parse("gone"))],
            [new IndexEdge(0, 1, 2, false), new IndexEdge(0, 0, 1, true)],
            [])
        {
            Types = [new IndexType("Note", 2, 0)],
        };

        var script = IndexScript.Render(index);

        Assert.Contains("\"ghosts\":[{\"id\":\"gone\"}],\"edges\":[[0,1,2,0],[0,0,1,1]]", script);
    }

    [Fact]
    public void The_tree_renders_nested_nodes_with_fixed_keys()
    {
        // The tree points at positions 0 and 1, so the index must hold two concepts.
        var index = new ViewerIndex(
            [Plain("foo"), Plain("foo/bar")],
            [],
            [],
            [new IndexTreeNode("foo", 0, [new IndexTreeNode("bar", 1, [])])])
        { Types = [new IndexType("Metric", 2, 0)] };

        Assert.Contains(
            "\"tree\":[{\"name\":\"foo\",\"concept\":0,\"children\":[{\"name\":\"bar\",\"concept\":1,\"children\":[]}]}]",
            IndexScript.Render(index));
    }

    [Fact]
    public void Version_2_appends_typeIndex_and_description_to_each_concept_and_the_types_table_last()
    {
        var concept = new IndexConcept(ConceptId.Parse("a"), "A", "Metric", [], "a.html", "unverified", null, null)
        {
            TypeIndex = 0,
            Description = "Gross margin.",
        };
        var index = new ViewerIndex([concept], [], [], []) { Types = [new IndexType("Metric", 1, 0), new IndexType("", 2, 5)] };

        var script = IndexScript.Render(index);

        Assert.StartsWith("window.OKF_INDEX = {\"version\":2,\"concepts\":[", script);
        Assert.Contains("\"staleAfterMs\":null,\"staleAfterDate\":null,\"typeIndex\":0,\"description\":\"Gross margin.\"}", script);
        Assert.EndsWith(",\"types\":[{\"name\":\"Metric\",\"count\":1,\"slot\":0},{\"name\":\"\",\"count\":2,\"slot\":5}]};\n", script);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void A_concept_whose_TypeIndex_is_outside_the_types_table_is_refused(int typeIndex)
    {
        var concept = new IndexConcept(ConceptId.Parse("a"), "A", "Metric", [], "a.html", "unverified", null, null) { TypeIndex = typeIndex };
        var index = new ViewerIndex([concept], [], [], []) { Types = [new IndexType("Metric", 1, 0)] };

        Assert.Throws<ArgumentException>(() => IndexScript.Render(index));
        Assert.Throws<ArgumentException>(() => IndexScript.Render(new ViewerIndex([concept], [], [], [])));
    }

    private static IndexConcept Plain(string id)
        => new(ConceptId.Parse(id), id, "Metric", [], id + ".html", "unverified", null, null);

    private static ViewerIndex Indexed(IReadOnlyList<IndexGhost> ghosts, IReadOnlyList<IndexEdge> edges, IReadOnlyList<IndexTreeNode> tree)
        => new([Plain("a"), Plain("b")], ghosts, edges, tree) { Types = [new IndexType("Metric", 2, 0)] };

    [Theory]
    [InlineData(-1, 1, false)]
    [InlineData(2, 1, false)]
    [InlineData(0, -1, false)]
    [InlineData(0, 2, false)]
    [InlineData(0, 1, true)] // no ghost at position 1: the index has exactly one
    [InlineData(0, -1, true)]
    public void An_edge_pointing_outside_the_concepts_or_the_ghosts_is_refused(int from, int to, bool toGhost)
    {
        var index = Indexed([new IndexGhost(ConceptId.Parse("gone"))], [new IndexEdge(from, to, 1, toGhost)], []);

        var ex = Assert.Throws<ArgumentException>(() => IndexScript.Render(index));
        Assert.Equal("index", ex.ParamName);
    }

    [Fact]
    public void An_edge_to_a_ghost_is_checked_against_the_ghosts_not_the_concepts()
    {
        // Position 1 is a valid concept but not a ghost; position 0 is a ghost.
        var ok = Indexed([new IndexGhost(ConceptId.Parse("gone"))], [new IndexEdge(0, 0, 1, true)], []);
        Assert.Contains("[0,0,1,1]", IndexScript.Render(ok), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void A_top_level_tree_node_pointing_outside_the_concepts_is_refused(int concept)
    {
        var index = Indexed([], [], [new IndexTreeNode("x", concept, [])]);

        Assert.Throws<ArgumentException>(() => IndexScript.Render(index));
    }

    [Fact]
    public void A_nested_tree_node_pointing_outside_the_concepts_is_refused()
    {
        var nested = new IndexTreeNode("dir", -1, [new IndexTreeNode("leaf", 7, [])]);

        Assert.Throws<ArgumentException>(() => IndexScript.Render(Indexed([], [], [nested])));
    }

    [Fact]
    public void Valid_references_including_a_folder_with_no_concept_still_render()
    {
        var tree = new IndexTreeNode("dir", -1, [new IndexTreeNode("a", 0, []), new IndexTreeNode("b", 1, [])]);
        var index = Indexed([], [new IndexEdge(0, 1, 2, false)], [tree]);

        var script = IndexScript.Render(index);

        Assert.Contains("[0,1,2,0]", script, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"dir\",\"concept\":-1", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hostile_description_and_type_name_round_trip_inertly()
    {
        var lineSeparator = ((char)0x2028).ToString();
        var hostile = "</script><img src=x onerror=alert(1)>" + lineSeparator + "\"\\";
        var concept = new IndexConcept(ConceptId.Parse("a"), "A", hostile, [], "a.html", "unverified", null, null)
        {
            Description = hostile,
        };
        var index = new ViewerIndex([concept], [], [], []) { Types = [new IndexType(hostile, 1, 0)] };

        var script = IndexScript.Render(index);

        Assert.DoesNotContain("<", script);
        Assert.DoesNotContain(lineSeparator, script);
        var root = Parse(script);
        Assert.Equal(hostile, root.GetProperty("concepts")[0].GetProperty("description").GetString());
        Assert.Equal(hostile, root.GetProperty("types")[0].GetProperty("name").GetString());
    }
}
