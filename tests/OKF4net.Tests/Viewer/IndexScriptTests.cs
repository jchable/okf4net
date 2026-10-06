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
        "window.OKF_INDEX = {\"version\":1,\"concepts\":[],\"ghosts\":[],\"edges\":[],\"tree\":[]};\n";

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
        var index = new ViewerIndex([concept], [], [], [new IndexTreeNode(hostile, 0, [])]);

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

        var script = IndexScript.Render(new ViewerIndex([dated, undated], [], [], []));

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
            []);

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
}
