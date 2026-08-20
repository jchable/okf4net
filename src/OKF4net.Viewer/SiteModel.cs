// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Viewer;

/// <summary>
/// Projects a loaded <see cref="Bundle"/> into the display model the viewer
/// renders. Pure: performs no I/O, so it is fully testable without touching
/// the filesystem.
/// </summary>
public static class SiteModel
{
    /// <summary>
    /// The href of <paramref name="to"/>'s generated page, relative to
    /// <paramref name="from"/>'s. Always <c>/</c>-separated and suffixed
    /// <c>.html</c>, so the generated site is navigable straight off the
    /// filesystem (<c>file://</c>) at any nesting depth.
    /// </summary>
    /// <param name="from">The concept whose page contains the link.</param>
    /// <param name="to">The concept being linked to.</param>
    public static string RelativeHref(ConceptId from, ConceptId to)
    {
        // Only the directory part of `from` matters: a page at a/b/c.html
        // sits in directory a/b, so it is 2 levels deep.
        var fromDir = from.Segments.Take(from.Segments.Count - 1).ToList();
        var toPath = to.Segments;

        var common = 0;
        while (common < fromDir.Count
               && common < toPath.Count - 1
               && string.Equals(fromDir[common], toPath[common], StringComparison.Ordinal))
        {
            common++;
        }

        var up = Enumerable.Repeat("..", fromDir.Count - common);
        var down = toPath.Skip(common);
        return string.Join('/', up.Concat(down)) + ".html";
    }

    /// <summary>
    /// Projects <paramref name="bundle"/> into the viewer's display model.
    /// Loading is permissive upstream, so a bundle carrying parse errors
    /// still yields a site -- the errors travel in
    /// <see cref="ViewerSite.ParseErrors"/> rather than aborting.
    /// </summary>
    /// <param name="bundle">The loaded bundle to project.</param>
    public static ViewerSite Build(Bundle bundle)
    {
        var pages = bundle.Concepts.Select(c => BuildPage(bundle, c)).ToList();

        var entries = pages
            .Select(p => new IndexEntry(
                Type: TypeOf(bundle, p.Id),
                Title: p.Title,
                Link: p.RelativeHtmlPath,
                Description: DescriptionOf(bundle, p.Id)))
            .ToList();

        return new ViewerSite(
            bundle.Root,
            pages,
            IndexGenerator.BuildIndexText(entries),
            bundle.ParseErrors.Select(e => new ViewerParseError(e.Path, e.Error)).ToList());
    }

    private static string TypeOf(Bundle bundle, ConceptId id)
        => bundle.Get(id)?.Document.Frontmatter.Type ?? string.Empty;

    private static string DescriptionOf(Bundle bundle, ConceptId id)
        => bundle.Get(id)?.Document.Frontmatter.Description ?? string.Empty;

    private static ViewerPage BuildPage(Bundle bundle, Concept concept)
    {
        var frontmatter = concept.Document.Frontmatter;

        var title = string.IsNullOrWhiteSpace(frontmatter.Title)
            ? concept.Id.ToString()
            : frontmatter.Title;

        var entries = frontmatter.AsMapping().Entries
            .Select(e => new ViewerFrontmatterEntry(
                e.Key.AsDisplayString() ?? e.Key.ToYamlString().TrimEnd('\n'),
                DisplayValue(e.Value)))
            .ToList();

        var links = bundle.LinksFrom(concept.Id)
            .Select(l => new ViewerLink(l.Raw, RelativeHref(concept.Id, l.Target), l.Exists))
            .ToList();

        var backlinks = bundle.Backlinks(concept.Id)
            .Select(source => new ViewerLink(
                source.ToString(),
                RelativeHref(concept.Id, source),
                Exists: true))
            .ToList();

        return new ViewerPage(
            concept.Id,
            title,
            concept.Id.ToString() + ".html",
            entries,
            concept.Document.Body,
            links,
            backlinks);
    }

    /// <summary>
    /// A frontmatter value as a single display string.
    /// <see cref="YamlValue.AsDisplayString"/> returns <c>null</c> for
    /// sequences and mappings, so those fall back to a compact YAML emit --
    /// dropping them would silently hide `tags`, `sources`, and every
    /// structured producer key.
    /// </summary>
    private static string DisplayValue(YamlValue value)
        => value.AsDisplayString()
           ?? value.ToYamlString().TrimEnd('\n').Replace("\n", " ");
}
