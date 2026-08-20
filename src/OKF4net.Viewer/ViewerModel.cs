// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Viewer;

/// <summary>One frontmatter key/value pair, rendered for display.</summary>
/// <param name="Key">The frontmatter key, in document order.</param>
/// <param name="Value">The value, rendered as a display string.</param>
public sealed record ViewerFrontmatterEntry(string Key, string Value);

/// <summary>
/// A link from one generated page to another, resolved at generation time.
/// </summary>
/// <param name="RawTarget">The link target exactly as written in the markdown source.</param>
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
/// <param name="Body">The raw markdown body, rendered client-side.</param>
/// <param name="Links">Outgoing internal links, for client-side href rewiring.</param>
/// <param name="Backlinks">Concepts linking to this one.</param>
public sealed record ViewerPage(
    ConceptId Id,
    string Title,
    string RelativeHtmlPath,
    IReadOnlyList<ViewerFrontmatterEntry> Frontmatter,
    string Body,
    IReadOnlyList<ViewerLink> Links,
    IReadOnlyList<ViewerLink> Backlinks);

/// <summary>The whole generated site, as a pure model.</summary>
/// <param name="BundleRoot">The source bundle's root directory.</param>
/// <param name="Pages">One entry per concept.</param>
/// <param name="IndexMarkdown">The index page's markdown, rendered by the same client-side path as concept bodies.</param>
/// <param name="ParseErrors">Files the bundle could not parse.</param>
public sealed record ViewerSite(
    string BundleRoot,
    IReadOnlyList<ViewerPage> Pages,
    string IndexMarkdown,
    IReadOnlyList<ViewerParseError> ParseErrors);
