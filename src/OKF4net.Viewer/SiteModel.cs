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
        var concepts = bundle.Concepts;
        var pages = new List<ViewerPage>(concepts.Count);
        var entries = new List<IndexEntry>(concepts.Count);

        foreach (var concept in concepts)
        {
            // BuildPage already read this concept's frontmatter; reuse it
            // here instead of a second bundle.Get(id) lookup for the index
            // entry's Type/Description. The `?? string.Empty` fallbacks are
            // load-bearing: IndexGenerator groups an empty `type` under
            // "Other", so they must match the old TypeOf/DescriptionOf
            // behaviour exactly.
            var page = BuildPage(bundle, concept);
            pages.Add(page);
            entries.Add(new IndexEntry(
                Type: concept.Document.Frontmatter.Type ?? string.Empty,
                Title: page.Title,
                Link: page.RelativeHtmlPath,
                Description: concept.Document.Frontmatter.Description ?? string.Empty));
        }

        return new ViewerSite(
            bundle.Root,
            pages,
            IndexGenerator.BuildIndexText(entries),
            bundle.ParseErrors.Select(e => new ViewerParseError(e.Path, e.Error)).ToList());
    }

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
            .Select(l => new ViewerLink(l.Raw, RelativeHref(concept.Id, l.Target) + FragmentOf(l.Raw), l.Exists))
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
    /// The <c>#fragment</c> suffix of a raw link target, including the
    /// <c>#</c>, or the empty string when the target carries none.
    ///
    /// <see cref="ConceptLink.Resolve"/> strips the fragment before turning
    /// the target into a <see cref="ConceptId"/> (concept ids cannot contain
    /// <c>#</c>), so <see cref="RelativeHref"/> -- which operates on that
    /// resolved id -- never sees it. Re-attaching it here, onto the
    /// generated href, is what keeps a deep link such as
    /// <c>[usage](a/b.md#usage)</c> landing on <c>a/b.html#usage</c> instead
    /// of the top of the target page. Applied unconditionally, including for
    /// links to a missing concept: <c>viewer.js</c> only ever reads that
    /// entry's <c>href</c> when its <c>exists</c> flag is true, so a
    /// fragment tagging along on a broken link's (unused) href is inert, not
    /// "bogus" in any observable sense.
    /// </summary>
    private static string FragmentOf(string raw)
    {
        var idx = raw.IndexOf('#');
        return idx >= 0 ? raw[idx..] : string.Empty;
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
