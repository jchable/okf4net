// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Viewer;

/// <summary>One frontmatter key/value pair, rendered for display.</summary>
/// <param name="Key">The frontmatter key, in document order.</param>
/// <param name="Value">The value, rendered as a display string.</param>
public sealed record ViewerFrontmatterEntry(string Key, string Value)
{
    /// <summary>
    /// Whether <see cref="Value"/> is the compact YAML emission of a sequence
    /// or a mapping (rendered in the monospace face, spec §11.3, C6). A
    /// sequence of scalars is not: it is joined by <c>", "</c>.
    /// </summary>
    public bool Structured { get; init; }

    /// <summary>
    /// Whether the entry is hidden while the frontmatter box is folded (A27):
    /// every entry but the first four not already shown by the title and the
    /// chips. A hand-built entry is never folded.
    /// </summary>
    public bool Extra { get; init; }
}

/// <summary>What the chips of a concept page show (spec §11.3, C5).</summary>
/// <param name="Type">The frontmatter <c>type</c>, or the empty string when absent (shown as "(no type)").</param>
/// <param name="Status">The raw <c>status</c> when the key exists, is a scalar and is neither empty nor whitespace-only; otherwise null.</param>
/// <param name="Trust">The trust tier, read from the site index (never re-derived).</param>
/// <param name="Verifier">The verifier shown after the tier, trimmed; null when the tier's set of verifications is empty or when the last one's verifier is empty or whitespace-only (the chip never renders an empty segment; the tier itself is unchanged).</param>
/// <param name="VerifiedDate">Its date: the first ten characters of <c>at</c> when they read <c>YYYY-MM-DD</c>, else <c>at</c> as written; null when absent.</param>
/// <param name="MoreVerifications">How many other verifications the tier's set holds ("+N", omitted at 0).</param>
/// <param name="StaleAfterDate">The §5.5 deadline's date from the site index, or null when there is none.</param>
public sealed record ViewerPageHead(
    string Type,
    string? Status,
    string Trust,
    string? Verifier,
    string? VerifiedDate,
    int MoreVerifications,
    string? StaleAfterDate);

/// <summary>
/// A link from one generated page to another, resolved at generation time.
/// </summary>
/// <param name="RawTarget">
/// The link destination as <c>Bundle.LinksFrom</c> reports it in
/// <c>ResolvedLink.Raw</c> -- the destination <c>LinkScanner</c> parsed out
/// of the markdown, not the literal source text: no title, no angle brackets,
/// backslash escapes resolved, and for a reference link its definition's
/// destination. For <c>[x](../a/b.md "Title")</c> this is <c>../a/b.md</c>,
/// never <c>../a/b.md "Title"</c>.
///
/// This is the right key to rewire on <em>whenever the target contains
/// nothing marked's client-side <c>cleanUrl</c> step would rewrite</em> --
/// which is the common case, since <see cref="ConceptId"/>'s restricted
/// segment charset keeps the path portion free of anything marked would
/// touch. That step runs <c>encodeURI()</c> (then undoes its own escaping
/// of literal <c>%</c>) over the whole destination before it reaches the
/// DOM, so a target whose <c>#fragment</c> carries a non-ASCII or otherwise
/// URI-unsafe character -- the only place one can appear in an internal
/// link target -- shows up in the rendered anchor's <c>href</c>
/// percent-encoded, even though this field never is. <c>viewer.js</c>
/// handles that: it looks the raw <c>href</c> up in the table first and,
/// on a miss, retries with <c>decodeURI(href)</c> before giving up.
/// </param>
/// <param name="Href">The generated page's path, relative to the linking page.</param>
/// <param name="Exists">Whether the target concept exists in the bundle.</param>
public sealed record ViewerLink(string RawTarget, string Href, bool Exists);

/// <summary>One unparseable file, surfaced on the generated index page.</summary>
/// <param name="Path">The offending file's path.</param>
/// <param name="Error">The parse error reported by <see cref="Bundle"/>.</param>
public sealed record ViewerParseError(string Path, string Error);

/// <summary>One generated concept page.</summary>
/// <param name="Id">The concept's id.</param>
/// <param name="Title">The display title (frontmatter title, else the concept id).</param>
/// <param name="RelativeHtmlPath">The page's path relative to the site root, e.g. <c>tables/users.html</c>.</param>
/// <param name="Frontmatter">The frontmatter entries, in document order.</param>
/// <param name="Body">The raw markdown body, kept as read; the writer renders <see cref="DisplayBody"/> (or this when that is null) client-side.</param>
/// <param name="Links">Outgoing internal links, for client-side href rewiring.</param>
/// <param name="Backlinks">Concepts linking to this one.</param>
public sealed record ViewerPage(
    ConceptId Id,
    string Title,
    string RelativeHtmlPath,
    IReadOnlyList<ViewerFrontmatterEntry> Frontmatter,
    string Body,
    IReadOnlyList<ViewerLink> Links,
    IReadOnlyList<ViewerLink> Backlinks)
{
    /// <summary>
    /// The markdown the page's payload carries: <see cref="Body"/> without a
    /// leading level-1 heading that repeats the title (spec §11.3, C4). Null
    /// (a page built by hand) means <see cref="Body"/>.
    /// </summary>
    public string? DisplayBody { get; init; }

    /// <summary>The chips' data (C5); null for a page built by hand, which then shows no chips.</summary>
    public ViewerPageHead? Head { get; init; }
}

/// <summary>The whole generated site, as a pure model.</summary>
/// <param name="BundleRoot">The source bundle's root directory.</param>
/// <param name="Pages">One entry per concept.</param>
/// <param name="IndexMarkdown">The index page's markdown, rendered by the same client-side path as concept bodies.</param>
/// <param name="ParseErrors">Files the bundle could not parse.</param>
public sealed record ViewerSite(
    string BundleRoot,
    IReadOnlyList<ViewerPage> Pages,
    string IndexMarkdown,
    IReadOnlyList<ViewerParseError> ParseErrors)
{
    /// <summary>
    /// The site index written to <c>assets/okf-index.js</c>.
    /// <see cref="ViewerIndex.Empty"/> for a site built by hand.
    /// </summary>
    public ViewerIndex Index { get; init; } = ViewerIndex.Empty;

    /// <summary>
    /// The bundle's name for the header and the breadcrumb: the bundle root's
    /// folder name (spec §12.3). Null for a site built by hand: the writer
    /// derives it from <see cref="BundleRoot"/> the same way.
    /// </summary>
    public string? BundleName { get; init; }

    /// <summary>
    /// The graph page's file name at the site root: <c>graph.html</c>, else
    /// <c>graph-1.html</c>, <c>graph-2.html</c>… (A25). Null for a site built
    /// by hand: the writer computes it the same way. An explicit value must be
    /// one <c>[A-Za-z0-9._-]+.html</c> segment that collides with nothing.
    /// </summary>
    public string? GraphPagePath { get; init; }
}
