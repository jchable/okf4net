// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace OKF4net.Viewer;

/// <summary>
/// Projects a loaded <see cref="Bundle"/> into the <see cref="ViewerIndex"/>
/// behind <c>assets/okf-index.js</c>. Pure: no I/O, and no system clock is read.
/// </summary>
public static class SiteIndex
{
    /// <summary>Builds the site index of <paramref name="bundle"/>.</summary>
    /// <param name="bundle">The loaded bundle.</param>
    public static ViewerIndex Build(Bundle bundle)
    {
        // One comparator for everything ordered in the index (spec §3.1).
        var ordered = bundle.Concepts.OrderBy(c => c.Id, Comparer<ConceptId>.Default).ToList();

        // Only the trust tier and the parsed stale_after are read from the
        // audit. Staleness itself is evaluated in the browser (spec §4.4), so
        // the clock is pinned: the projection stays pure.
        var findings = ConceptAudit.Run(bundle, default, new FixedClock(DateTimeOffset.UnixEpoch))
            .Findings
            .ToDictionary(f => f.Id.ToString(), StringComparer.Ordinal);

        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        var types = RankTypes(ordered.Select(c => c.Document.Frontmatter.Type ?? string.Empty));
        var typePosition = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < types.Count; i++)
        {
            typePosition[types[i].Name] = i;
        }

        var concepts = new List<IndexConcept>(ordered.Count);
        foreach (var concept in ordered)
        {
            var id = concept.Id.ToString();
            var finding = findings[id];
            var frontmatter = concept.Document.Frontmatter;
            position[id] = concepts.Count;
            concepts.Add(new IndexConcept(
                concept.Id,
                SiteModel.DisplayTitle(concept),
                frontmatter.Type ?? string.Empty,
                frontmatter.Tags,
                SiteModel.PagePath(concept.Id),
                AuditVocabulary.Name(finding.Trust),
                CeilingMilliseconds(finding.Lifecycle.StaleAfter),
                finding.Lifecycle.StaleAfterDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            {
                TypeIndex = typePosition[frontmatter.Type ?? string.Empty],
                Description = Describe(frontmatter.Description, DescriptionLimit),
            });
        }

        var absent = new SortedSet<ConceptId>(Comparer<ConceptId>.Default);
        foreach (var concept in ordered)
        {
            foreach (var link in bundle.LinksFrom(concept.Id))
            {
                if (!link.Exists)
                {
                    absent.Add(link.Target);
                }
            }
        }

        var ghosts = absent.Select(id => new IndexGhost(id)).ToList();
        var ghostPosition = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ghosts.Count; i++)
        {
            ghostPosition[ghosts[i].Id.ToString()] = i;
        }

        var edges = new List<IndexEdge>();
        for (var from = 0; from < ordered.Count; from++)
        {
            // Bundle.BuildGraph keeps every occurrence; repeated (source,
            // target) pairs merge into one edge with a count (spec §3.3).
            // Concept targets sort before ghosts, each by position.
            var counts = new SortedDictionary<(bool ToGhost, int To), int>();
            foreach (var link in bundle.LinksFrom(ordered[from].Id))
            {
                var target = link.Target.ToString();
                var key = link.Exists ? (false, position[target]) : (true, ghostPosition[target]);
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            foreach (var (key, count) in counts)
            {
                edges.Add(new IndexEdge(from, key.To, count, key.ToGhost));
            }
        }

        return new ViewerIndex(concepts, ghosts, edges, BuildTree(concepts)) { Types = types };
    }

    /// <summary>
    /// The longest description the index carries, in code points (spec §12.1,
    /// A18). A23: 120 instead if the measured index of the OKF4net bundle
    /// exceeds 600 000 bytes (spec §3.6 records the measurement).
    /// </summary>
    internal const int DescriptionLimit = 200;

    /// <summary>The slot every type past the fifth, and the empty type, share (the "other" shape).</summary>
    internal const int OtherSlot = 5;

    private const char Ellipsis = (char)0x2026;

    /// <summary>
    /// The types table (spec §12.1): one entry per distinct value, by count
    /// descending then <see cref="string.CompareOrdinal(string, string)"/> of
    /// the name; the first five NON-EMPTY names take slots 0 to 4 in that
    /// order, every other name and the empty one take <see cref="OtherSlot"/>.
    /// </summary>
    /// <param name="types">Each concept's type, the empty string when absent.</param>
    internal static List<IndexType> RankTypes(IEnumerable<string> types)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            counts[type] = counts.TryGetValue(type, out var n) ? n + 1 : 1;
        }

        var ranked = new List<IndexType>(counts.Count);
        var next = 0;
        foreach (var (name, count) in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var slot = name.Length > 0 && next < OtherSlot ? next++ : OtherSlot;
            ranked.Add(new IndexType(name, count, slot));
        }

        return ranked;
    }

    /// <summary>
    /// A description as the index carries it (spec §12.1): runs of
    /// <see cref="char.IsWhiteSpace(char)"/> collapsed to one space, ends
    /// trimmed; past <paramref name="limit"/> code points, the first
    /// <c>limit - 1</c> kept, trailing spaces removed and U+2026 appended. A
    /// valid surrogate pair counts one and is never split; a lone half counts
    /// one and is kept as is.
    /// </summary>
    /// <param name="description">The frontmatter description, or null.</param>
    /// <param name="limit">The longest result, in code points.</param>
    internal static string Describe(string? description, int limit)
    {
        if (string.IsNullOrEmpty(description))
        {
            return string.Empty;
        }

        var collapsed = new StringBuilder(description.Length);
        var pendingSpace = false;
        foreach (var ch in description)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = collapsed.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                collapsed.Append(' ');
                pendingSpace = false;
            }

            collapsed.Append(ch);
        }

        var text = collapsed.ToString();
        var cut = 0;
        var points = 0;
        for (var i = 0; i < text.Length; points++)
        {
            if (points == limit - 1)
            {
                cut = i;
            }

            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        }

        return points <= limit ? text : text[..cut].TrimEnd() + Ellipsis;
    }

    /// <summary>
    /// <paramref name="instant"/> as milliseconds since the Unix epoch,
    /// rounded UP: <c>Date.now()</c> is a whole number of milliseconds, so
    /// <c>now &gt;= ceiling(deadline)</c> holds exactly when
    /// <c>now &gt;= deadline</c> (spec §4.4).
    /// </summary>
    private static long? CeilingMilliseconds(DateTimeOffset? instant)
    {
        if (instant is not { } value)
        {
            return null;
        }

        var ticks = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var milliseconds = ticks / TimeSpan.TicksPerMillisecond;
        if (ticks % TimeSpan.TicksPerMillisecond > 0)
        {
            milliseconds++;
        }

        return milliseconds;
    }

    private static List<IndexTreeNode> BuildTree(List<IndexConcept> concepts)
    {
        var root = new TreeBuilder(string.Empty);
        for (var i = 0; i < concepts.Count; i++)
        {
            var node = root;
            foreach (var segment in concepts[i].Id.Segments)
            {
                node = node.Child(segment);
            }

            node.Concept = i;
        }

        return root.Freeze();
    }

    private sealed class TreeBuilder(string name)
    {
        private readonly SortedDictionary<string, TreeBuilder> _children = new(StringComparer.Ordinal);

        public string Name { get; } = name;

        public int Concept { get; set; } = -1;

        public TreeBuilder Child(string segment)
        {
            if (!_children.TryGetValue(segment, out var child))
            {
                child = new TreeBuilder(segment);
                _children.Add(segment, child);
            }

            return child;
        }

        public List<IndexTreeNode> Freeze()
            => _children.Values.Select(c => new IndexTreeNode(c.Name, c.Concept, c.Freeze())).ToList();
    }
}
