// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Viewer;

/// <summary>One concept in the site index (<c>assets/okf-index.js</c>).</summary>
/// <param name="Id">The concept id (§2).</param>
/// <param name="Title">The display title: the frontmatter <c>title</c>, else the id.</param>
/// <param name="Type">The frontmatter <c>type</c>, or empty when absent.</param>
/// <param name="Tags">The frontmatter <c>tags</c>, in document order.</param>
/// <param name="Path">The page path relative to the site root, e.g. <c>tables/users.html</c>.</param>
/// <param name="Trust">The §5.3 trust tier, under its <see cref="AuditVocabulary"/> name.</param>
/// <param name="StaleAfterMs">
/// The §5.5 deadline in milliseconds since the Unix epoch, rounded UP to a
/// whole millisecond, so that <c>Date.now() &gt;= StaleAfterMs</c> in the
/// browser equals <see cref="Lifecycle.IsStale"/>; null when absent or malformed.
/// </param>
/// <param name="StaleAfterDate">
/// <see cref="Lifecycle.StaleAfterDate"/> as <c>yyyy-MM-dd</c>, for display only;
/// never derived from <paramref name="StaleAfterMs"/>, which can tip into the next day.
/// </param>
public sealed record IndexConcept(
    ConceptId Id,
    string Title,
    string Type,
    IReadOnlyList<string> Tags,
    string Path,
    string Trust,
    long? StaleAfterMs,
    string? StaleAfterDate)
{
    /// <summary>Position of this concept's type in <see cref="ViewerIndex.Types"/> (spec §12.1).</summary>
    /// <remarks>
    /// A hand-built index must fill this and <see cref="ViewerIndex.Types"/>
    /// consistently; <c>SiteIndex.Build</c> is the only in-tree producer, and
    /// <c>IndexScript.Render</c> rejects a position outside the table.
    /// </remarks>
    public int TypeIndex { get; init; }

    /// <summary>
    /// The frontmatter <c>description</c>, whitespace runs collapsed and
    /// truncated to 200 code points (spec §12.1, A18); empty when absent.
    /// </summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>One distinct frontmatter <c>type</c> of the bundle (spec §12.1, A19).</summary>
/// <param name="Name">The type, or the empty string for the concepts that have none.</param>
/// <param name="Count">How many concepts carry it.</param>
/// <param name="Slot">0 to 4 for the five most frequent non-empty types, in order; 5 for every other type and for the empty one.</param>
public sealed record IndexType(string Name, int Count, int Slot);

/// <summary>A link target that is not a concept of the bundle (a §6.1 broken link). Never navigable.</summary>
/// <param name="Id">The absent target's id.</param>
public sealed record IndexGhost(ConceptId Id);

/// <summary>All body links from one concept to one target, merged.</summary>
/// <param name="From">Position of the source in <see cref="ViewerIndex.Concepts"/>.</param>
/// <param name="To">Position of the target in <see cref="ViewerIndex.Concepts"/>, or in <see cref="ViewerIndex.Ghosts"/> when <paramref name="ToGhost"/>.</param>
/// <param name="Count">How many links in the source's body point at that target.</param>
/// <param name="ToGhost">Whether the target is absent from the bundle.</param>
public sealed record IndexEdge(int From, int To, int Count, bool ToGhost);

/// <summary>
/// One segment of the id tree. A node can be a destination (a concept whose
/// id is this path), a folder (children), or both: <c>foo.md</c> and
/// <c>foo/bar.md</c> coexist legally.
/// </summary>
/// <param name="Name">The segment.</param>
/// <param name="Concept">Position in <see cref="ViewerIndex.Concepts"/> of the concept at this path, or -1.</param>
/// <param name="Children">Child nodes, ordered by segment (ordinal).</param>
public sealed record IndexTreeNode(string Name, int Concept, IReadOnlyList<IndexTreeNode> Children);

/// <summary>The site index the interactive viewer scripts read.</summary>
/// <param name="Concepts">Every concept, ordered by <see cref="ConceptId.CompareTo(ConceptId)"/>.</param>
/// <param name="Ghosts">Every absent link target, ordered the same way.</param>
/// <param name="Edges">Merged body links, by source then target.</param>
/// <param name="Tree">Top-level tree nodes.</param>
public sealed record ViewerIndex(
    IReadOnlyList<IndexConcept> Concepts,
    IReadOnlyList<IndexGhost> Ghosts,
    IReadOnlyList<IndexEdge> Edges,
    IReadOnlyList<IndexTreeNode> Tree)
{
    /// <summary>An index with nothing in it, for a site built by hand.</summary>
    public static ViewerIndex Empty { get; } = new([], [], [], []);

    /// <summary>
    /// The distinct types, by count descending then ordinal name; each
    /// concept points into it by <see cref="IndexConcept.TypeIndex"/>
    /// (spec §12.1). The JS reads the ranks here and never recomputes them.
    /// </summary>
    /// <remarks>
    /// A hand-built index must fill this and each concept's
    /// <see cref="IndexConcept.TypeIndex"/> consistently
    /// (<c>SiteIndex.Build</c> is the only in-tree producer); the empty
    /// index has no concept and no type, which is valid.
    /// </remarks>
    public IReadOnlyList<IndexType> Types { get; init; } = [];
}
