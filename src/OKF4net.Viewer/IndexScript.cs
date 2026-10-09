// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace OKF4net.Viewer;

/// <summary>
/// Serializes a <see cref="ViewerIndex"/> as the classic script
/// <c>assets/okf-index.js</c>, which assigns <c>window.OKF_INDEX</c>.
/// </summary>
/// <remarks>
/// Hand-built so that every bundle-derived string goes through
/// <see cref="HtmlSafeJson.Quote"/>. Plain JSON string escaping is what keeps
/// bundle text inert here: <c>okf-index.js</c> is an external file loaded by
/// <c>&lt;script src&gt;</c>, so no HTML parser ever sees its text, and
/// U+2028 and U+2029 have been legal in string literals since ES2019. The
/// extra escapes (<c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, U+2028, U+2029)
/// are defence in depth: the text stays safe to inline in a page, and older
/// engines still parse it. (A reflection-based <c>System.Text.Json</c> would
/// also fail under Native AOT, but that is not the reason.) The object has
/// fixed keys only -- arrays and fixed-key records, never a dictionary keyed
/// by an id or a title -- because
/// <c>__proto__</c>, <c>constructor</c> and <c>toString</c> are valid concept
/// ids, and an object literal keyed by one of them does not mean what
/// <c>JSON.parse</c> of the same text means (spec §3.4).
/// </remarks>
public static class IndexScript
{
    /// <summary>The fixed text every index script starts with.</summary>
    public const string Prefix = "window.OKF_INDEX = ";

    /// <summary>Renders <paramref name="index"/> as a complete script, ending with <c>;\n</c>.</summary>
    /// <param name="index">The site index.</param>
    /// <exception cref="ArgumentException">
    /// A concept's <see cref="IndexConcept.TypeIndex"/> is outside <see cref="ViewerIndex.Types"/>,
    /// an <see cref="IndexEdge"/> points outside <see cref="ViewerIndex.Concepts"/> (or, for a ghost
    /// edge, outside <see cref="ViewerIndex.Ghosts"/>), or an <see cref="IndexTreeNode.Concept"/>
    /// is neither -1 nor a position in <see cref="ViewerIndex.Concepts"/>.
    /// </exception>
    public static string Render(ViewerIndex index)
    {
        Validate(index);

        var sb = new StringBuilder(Prefix);
        sb.Append("{\"version\":2,\"concepts\":[");
        for (var i = 0; i < index.Concepts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var c = index.Concepts[i];
            sb.Append("{\"id\":").Append(HtmlSafeJson.Quote(c.Id.ToString()))
              .Append(",\"title\":").Append(HtmlSafeJson.Quote(c.Title))
              .Append(",\"type\":").Append(HtmlSafeJson.Quote(c.Type))
              .Append(",\"tags\":[");
            for (var t = 0; t < c.Tags.Count; t++)
            {
                if (t > 0)
                {
                    sb.Append(',');
                }

                sb.Append(HtmlSafeJson.Quote(c.Tags[t]));
            }

            sb.Append("],\"path\":").Append(HtmlSafeJson.Quote(c.Path))
              .Append(",\"trust\":").Append(HtmlSafeJson.Quote(c.Trust))
              .Append(",\"staleAfterMs\":")
              .Append(c.StaleAfterMs is { } ms ? ms.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"staleAfterDate\":")
              .Append(c.StaleAfterDate is { } date ? HtmlSafeJson.Quote(date) : "null")
              .Append(",\"typeIndex\":").Append(c.TypeIndex.ToString(CultureInfo.InvariantCulture))
              .Append(",\"description\":").Append(HtmlSafeJson.Quote(c.Description))
              .Append('}');
        }

        sb.Append("],\"ghosts\":[");
        for (var i = 0; i < index.Ghosts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"id\":").Append(HtmlSafeJson.Quote(index.Ghosts[i].Id.ToString())).Append('}');
        }

        sb.Append("],\"edges\":[");
        for (var i = 0; i < index.Edges.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var e = index.Edges[i];
            sb.Append('[')
              .Append(e.From.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.To.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.ToGhost ? '1' : '0')
              .Append(']');
        }

        sb.Append("],\"tree\":");
        AppendNodes(sb, index.Tree);
        sb.Append(",\"types\":[");
        for (var i = 0; i < index.Types.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var t = index.Types[i];
            sb.Append("{\"name\":").Append(HtmlSafeJson.Quote(t.Name))
              .Append(",\"count\":").Append(t.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"slot\":").Append(t.Slot.ToString(CultureInfo.InvariantCulture))
              .Append('}');
        }

        sb.Append("]};\n");
        return sb.ToString();
    }

    /// <summary>
    /// Refuses an index whose positions point outside the lists they index: the
    /// scripts would otherwise read a missing concept, ghost or type and render
    /// a silently broken explorer or graph. <c>SiteIndex.Build</c> never
    /// produces one; only a hand-built <see cref="ViewerIndex"/> can.
    /// </summary>
    private static void Validate(ViewerIndex index)
    {
        foreach (var concept in index.Concepts)
        {
            if ((uint)concept.TypeIndex >= (uint)index.Types.Count)
            {
                throw new ArgumentException(
                    $"Concept '{concept.Id}' has TypeIndex {concept.TypeIndex}, outside the {index.Types.Count} types of the index.",
                    nameof(index));
            }
        }

        foreach (var edge in index.Edges)
        {
            if ((uint)edge.From >= (uint)index.Concepts.Count)
            {
                throw new ArgumentException(
                    $"An edge has From {edge.From}, outside the {index.Concepts.Count} concepts of the index.",
                    nameof(index));
            }

            var targets = edge.ToGhost ? index.Ghosts.Count : index.Concepts.Count;
            if ((uint)edge.To >= (uint)targets)
            {
                throw new ArgumentException(
                    $"An edge from concept {edge.From} has To {edge.To}, outside the {targets} {(edge.ToGhost ? "ghosts" : "concepts")} of the index.",
                    nameof(index));
            }
        }

        ValidateNodes(index.Tree, index.Concepts.Count, index);
    }

    private static void ValidateNodes(IReadOnlyList<IndexTreeNode> nodes, int conceptCount, ViewerIndex index)
    {
        foreach (var node in nodes)
        {
            // -1 is a folder with no concept of its own (spec §12.1).
            if (node.Concept != -1 && (uint)node.Concept >= (uint)conceptCount)
            {
                throw new ArgumentException(
                    $"Tree node '{node.Name}' has Concept {node.Concept}, neither -1 nor a position among the {conceptCount} concepts of the index.",
                    nameof(index));
            }

            ValidateNodes(node.Children, conceptCount, index);
        }
    }

    private static void AppendNodes(StringBuilder sb, IReadOnlyList<IndexTreeNode> nodes)
    {
        sb.Append('[');
        for (var i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"name\":").Append(HtmlSafeJson.Quote(nodes[i].Name))
              .Append(",\"concept\":").Append(nodes[i].Concept.ToString(CultureInfo.InvariantCulture))
              .Append(",\"children\":");
            AppendNodes(sb, nodes[i].Children);
            sb.Append('}');
        }

        sb.Append(']');
    }
}
