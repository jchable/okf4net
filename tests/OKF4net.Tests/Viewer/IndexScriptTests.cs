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
        var index = new ViewerIndex([], [], [], [new IndexTreeNode("foo", 0, [new IndexTreeNode("bar", 1, [])])]);

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
