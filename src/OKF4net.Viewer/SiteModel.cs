// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;
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
        // The index first: the page head reads the trust tier and the stale
        // date from it and never re-derives them (spec §11.3, C5).
        var index = SiteIndex.Build(bundle);
        var indexed = new Dictionary<string, IndexConcept>(StringComparer.Ordinal);
        foreach (var concept in index.Concepts)
        {
            indexed[concept.Id.ToString()] = concept;
        }

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
            var page = BuildPage(bundle, concept, indexed[concept.Id.ToString()]);
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
            bundle.ParseErrors.Select(e => new ViewerParseError(e.Path, e.Error)).ToList())
        {
            Index = index,
            BundleName = BundleNameOf(bundle.Root),
            GraphPagePath = FreeGraphPagePath(pages),
        };
    }

    private static ViewerPage BuildPage(Bundle bundle, Concept concept, IndexConcept indexed)
    {
        var frontmatter = concept.Document.Frontmatter;

        var title = DisplayTitle(concept);

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
            PagePath(concept.Id),
            FrontmatterEntries(frontmatter),
            concept.Document.Body,
            links,
            backlinks)
        {
            DisplayBody = StripDuplicateTitle(concept.Document.Body, title),
            Head = BuildHead(frontmatter, indexed),
        };
    }

    /// <summary>The display title: the frontmatter <c>title</c>, else the concept id.</summary>
    internal static string DisplayTitle(Concept concept)
    {
        var title = concept.Document.Frontmatter.Title;
        return string.IsNullOrWhiteSpace(title) ? concept.Id.ToString() : title;
    }

    /// <summary>The generated page's path relative to the site root.</summary>
    internal static string PagePath(ConceptId id) => id + ".html";

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
    /// A frontmatter value as one display string, and whether it is a
    /// structure (spec §11.3, C6). A scalar is shown as written; a non-empty
    /// sequence of scalars is joined by <c>", "</c> (the mockup's
    /// <c>finance, margin, attested</c>); anything else keeps P1's compact YAML
    /// emission -- dropping it would silently hide <c>sources</c> and every
    /// structured producer key -- and is never truncated.
    /// </summary>
    private static (string Text, bool Structured) DisplayValue(YamlValue value)
    {
        if (value.AsDisplayString() is { } scalar)
        {
            return (scalar, false);
        }

        if (value.AsSequence() is { Count: > 0 } items)
        {
            var parts = new List<string>(items.Count);
            foreach (var item in items)
            {
                if (item.AsDisplayString() is not { } part)
                {
                    parts = null;
                    break;
                }

                parts.Add(part);
            }

            if (parts is not null)
            {
                return (string.Join(", ", parts), false);
            }
        }

        return (value.ToYamlString().TrimEnd('\n').Replace("\n", " "), value is YamlSequence or YamlMapping);
    }

    /// <summary>The keys the title and the chips already show: never among the four unfolded entries (A27).</summary>
    private static readonly HashSet<string> ShownAbove = new(StringComparer.Ordinal) { "type", "title", "status", "verified", "stale_after" };

    /// <summary>How many entries the folded frontmatter box shows (A27).</summary>
    internal const int FoldedEntries = 4;

    private static List<ViewerFrontmatterEntry> FrontmatterEntries(Frontmatter frontmatter)
    {
        var entries = new List<ViewerFrontmatterEntry>();
        var unfolded = 0;
        foreach (var e in frontmatter.AsMapping().Entries)
        {
            var key = e.Key.AsDisplayString() ?? e.Key.ToYamlString().TrimEnd('\n');
            var (text, structured) = DisplayValue(e.Value);
            var extra = true;
            if (!ShownAbove.Contains(key) && unfolded < FoldedEntries)
            {
                unfolded++;
                extra = false;
            }

            entries.Add(new ViewerFrontmatterEntry(key, text) { Structured = structured, Extra = extra });
        }

        return entries;
    }

    /// <summary>
    /// The chips' data (spec §11.3, C5). The tier and the stale date come from
    /// the index. Retained verifications are those with a <c>by</c>; the
    /// tier's set is the retained human ones for the human tier, all retained
    /// ones for the machine tier, none otherwise; the last of the set, in
    /// document order, is shown.
    /// </summary>
    private static ViewerPageHead BuildHead(Frontmatter frontmatter, IndexConcept indexed)
    {
        var status = frontmatter.Get("status")?.AsDisplayString();
        if (string.IsNullOrWhiteSpace(status))
        {
            status = null;
        }

        var retained = frontmatter.Verified.Where(s => s.By is not null).ToList();
        var set = indexed.Trust == AuditVocabulary.Name(TrustTier.HumanReviewed)
            ? retained.Where(s => s.By!.Value.IsHuman).ToList()
            : indexed.Trust == AuditVocabulary.Name(TrustTier.MachineConfirmed)
                ? retained
                : new List<Stamp>();

        string? verifier = null;
        string? date = null;
        if (set.Count > 0)
        {
            var last = set[^1];
            var by = last.By!.Value;
            // Trimmed; an empty or whitespace-only verifier is absent, so the
            // chip never renders an empty segment. The tier (above) is untouched.
            verifier = (by.IsHuman && by.IsWellFormed ? by.Id! : by.Raw).Trim();
            if (verifier.Length == 0)
            {
                verifier = null;
            }

            date = DateOf(last.At);
        }

        return new ViewerPageHead(
            frontmatter.Type ?? string.Empty,
            status,
            indexed.Trust,
            verifier,
            date,
            Math.Max(0, set.Count - 1),
            indexed.StaleAfterDate);
    }

    /// <summary>A verification date for display: <c>YYYY-MM-DD</c> when <paramref name="at"/> starts so, else as written; null when absent.</summary>
    private static string? DateOf(string? at)
    {
        if (string.IsNullOrEmpty(at))
        {
            return null;
        }

        if (at.Length >= 10)
        {
            var d = at.AsSpan(0, 10);
            var iso = d[4] == '-' && d[7] == '-'
                && char.IsAsciiDigit(d[0]) && char.IsAsciiDigit(d[1]) && char.IsAsciiDigit(d[2]) && char.IsAsciiDigit(d[3])
                && char.IsAsciiDigit(d[5]) && char.IsAsciiDigit(d[6]) && char.IsAsciiDigit(d[8]) && char.IsAsciiDigit(d[9]);
            if (iso)
            {
                return at[..10];
            }
        }

        return at;
    }

    /// <summary>
    /// <paramref name="body"/> without its first non-blank line when that line
    /// is a level-1 ATX heading at column 0 whose text equals
    /// <paramref name="title"/> (ordinal, after both are normalized), and
    /// without the blank lines before it (spec §11.3, C4). Lines are split by
    /// <see cref="LfLines"/> (a final <c>\r</c> stripped); a blank line is
    /// empty or made of spaces and tabs. The comparison is on raw text, never
    /// on rendered markup: a heading with inline markup is stripped only when
    /// the title is written with the same markup (<c>**T**</c> against
    /// <c># **T**</c>). Anything else -- setext, a heading inside a fence,
    /// another text -- is left alone: a kept duplicate costs nothing, a wrong
    /// removal would delete content.
    /// </summary>
    /// <param name="body">The raw markdown body.</param>
    /// <param name="title">The display title.</param>
    internal static string StripDuplicateTitle(string body, string title)
    {
        var wanted = NormalizeSpaces(title);
        foreach (var line in LfLines.SplitSpans(body))
        {
            var text = body.AsSpan(line.Start, line.ContentEnd - line.Start);
            if (IsBlank(text))
            {
                continue;
            }

            return AtxH1Text(text) is { } heading && string.Equals(NormalizeSpaces(heading), wanted, StringComparison.Ordinal)
                ? body[line.TerminatorEnd..]
                : body;
        }

        return body;
    }

    private static bool IsBlank(ReadOnlySpan<char> line)
    {
        foreach (var ch in line)
        {
            if (ch != ' ' && ch != '\t')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The text of a level-1 ATX heading at column 0 (<c>#</c> then a space or
    /// a tab, or <c>#</c> alone), or null. The closing sequence of <c>#</c> is
    /// removed only when a space or a tab precedes it or it is the whole
    /// content (CommonMark §4.2: <c># C#</c> keeps "C#").
    /// </summary>
    private static string? AtxH1Text(ReadOnlySpan<char> line)
    {
        if (line.Length == 0 || line[0] != '#')
        {
            return null;
        }

        if (line.Length > 1 && line[1] != ' ' && line[1] != '\t')
        {
            return null;
        }

        var content = line[1..].TrimEnd(" \t");
        var k = content.Length;
        while (k > 0 && content[k - 1] == '#')
        {
            k--;
        }

        if (k < content.Length && (k == 0 || content[k - 1] == ' ' || content[k - 1] == '\t'))
        {
            content = content[..k];
        }

        return content.ToString();
    }

    /// <summary>Spaces and tabs at the ends removed, inner runs of them collapsed to one space.</summary>
    private static string NormalizeSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pending = false;
        foreach (var ch in text)
        {
            if (ch == ' ' || ch == '\t')
            {
                pending = sb.Length > 0;
                continue;
            }

            if (pending)
            {
                sb.Append(' ');
                pending = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>The bundle's name: the root folder's name, <c>bundle</c> when it has none (spec §12.3).</summary>
    /// <param name="root">The bundle root, absolute or relative, with or without a trailing separator.</param>
    internal static string BundleNameOf(string root)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        return string.IsNullOrEmpty(name) ? "bundle" : name;
    }

    /// <summary>
    /// The graph page's file name (spec §12.3, A25): the first of
    /// <c>graph.html</c>, <c>graph-1.html</c>, <c>graph-2.html</c>… equal,
    /// ignoring case, to none of: a page's path, <c>index.html</c>, the first
    /// segment of a page's path (a folder). Never a refusal to render.
    /// </summary>
    /// <param name="pages">The site's pages.</param>
    internal static string FreeGraphPagePath(IReadOnlyList<ViewerPage> pages)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index.html" };
        foreach (var page in pages)
        {
            taken.Add(page.RelativeHtmlPath);
            var slash = page.RelativeHtmlPath.IndexOf('/');
            if (slash > 0)
            {
                taken.Add(page.RelativeHtmlPath[..slash]);
            }
        }

        for (var n = 0; ; n++)
        {
            var name = n == 0 ? "graph.html" : $"graph-{n}.html";
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }
}
