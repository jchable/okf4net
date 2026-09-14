// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net.Viewer;

/// <summary>
/// Writes a <see cref="ViewerSite"/> out as a self-contained static site.
/// The only unit in the viewer that touches the filesystem.
/// </summary>
public static class HtmlWriter
{
    /// <summary>
    /// Writes <paramref name="site"/> into <paramref name="outDir"/>, creating
    /// it if needed, and returns the site-relative paths written in write
    /// order. Existing files with the same names are overwritten; nothing else
    /// in the directory is removed.
    /// </summary>
    /// <param name="site">The site model to write.</param>
    /// <param name="outDir">The output directory.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="outDir"/> resolves inside the rendered bundle, which
    /// would pollute the bundle being viewed; or a page in
    /// <paramref name="site"/> carries a <see cref="ViewerPage.RelativeHtmlPath"/>
    /// that resolves outside <paramref name="outDir"/> (e.g. a
    /// <c>../</c>-escaping path on a hand-constructed <see cref="ViewerPage"/>);
    /// or two pages carry ids whose <see cref="ViewerPage.RelativeHtmlPath"/>
    /// differ only by case, or a page's path differs only by case from this
    /// method's own generated <c>index.html</c> (§2's <see cref="ConceptId"/>
    /// segments are case-sensitive, but two such colliding names would write
    /// the same file on a case-insensitive output volume -- refused
    /// unconditionally, even on a case-sensitive volume where both writes
    /// would otherwise succeed, because a site that renders differently per
    /// filesystem is not a site).
    /// </exception>
    public static IReadOnlyList<string> Write(ViewerSite site, string outDir)
    {
        GuardNoCaseCollisions(site);
        GuardOutputDirectory(site.BundleRoot, outDir);

        var written = new List<string>();
        Directory.CreateDirectory(outDir);

        // Canonicalized once here rather than per file: every file written
        // below shares the same root, so GuardWithinOutputDirectory no
        // longer re-resolves it on every call. verifiedDirs is the companion
        // cache that lets the ancestor walk itself run once per directory
        // instead of once per file -- see GuardWithinOutputDirectory's
        // remarks for what that trades away.
        var root = ReparsePoints.CanonicalizeRoot(outDir);
        var verifiedDirs = new HashSet<string>(StringComparer.Ordinal);

        WriteAsset(outDir, root, verifiedDirs, "viewer.css", ViewerAssets.Css, written);
        WriteAsset(outDir, root, verifiedDirs, "viewer.js", ViewerAssets.ViewerJs, written);
        WriteAsset(outDir, root, verifiedDirs, "marked.min.js", ViewerAssets.MarkedJs, written);

        WriteFile(outDir, root, verifiedDirs, "index.html", RenderIndex(site), written);

        foreach (var page in site.Pages)
        {
            WriteFile(outDir, root, verifiedDirs, page.RelativeHtmlPath, RenderPage(page), written);
        }

        return written;
    }

    /// <summary>
    /// Rejects a site holding two pages whose <see cref="ViewerPage.RelativeHtmlPath"/>
    /// differ only by case, or a page that collides with one of <see cref="Write"/>'s
    /// own generated file names, before <see cref="Write"/> takes any other
    /// action -- run first, ahead of <see cref="GuardOutputDirectory"/> and
    /// <see cref="Directory.CreateDirectory(string)"/>, so a refused site
    /// creates nothing on disk.
    /// </summary>
    /// <remarks>
    /// Two ids that differ only by case are two concepts on a case-sensitive
    /// bundle volume and ONE file on a case-insensitive output volume, where
    /// the second write silently replaces the first and the index links both
    /// entries to the survivor. Refused up front, whatever the volume: a site
    /// that renders differently per filesystem is not a site.
    ///
    /// Seeded with <c>index.html</c> before any page is examined, because it
    /// is exactly this same collision one level up: <c>Bundle</c>'s reserved-
    /// filename check (<c>case IndexFilename:</c>) is an ordinal switch, so a
    /// root-level <c>Index.md</c> or <c>INDEX.md</c> loads as an ordinary
    /// concept named <c>Index</c> on a case-sensitive bundle volume, and its
    /// page (<c>Index.html</c>) would overwrite -- or be overwritten by --
    /// this method's own generated <c>index.html</c> on a case-insensitive
    /// output volume. The three asset files under <c>assets/</c> are left out
    /// of the set: every generated page path ends in <c>.html</c> and every
    /// asset path ends in <c>.js</c> or <c>.css</c>, so no page can ever
    /// collide with an asset under any string comparer -- adding entries that
    /// can never fire would only pad the set without making it any more
    /// honest.
    /// </remarks>
    private static void GuardNoCaseCollisions(ViewerSite site)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["index.html"] = "the generated index page",
        };

        foreach (var page in site.Pages)
        {
            var description = $"concept '{page.Id}'";
            if (!seen.TryAdd(page.RelativeHtmlPath, description))
            {
                throw new ArgumentException(
                    $"{seen[page.RelativeHtmlPath]} and {description} would render to the same file on a case-insensitive volume ('{page.RelativeHtmlPath}')",
                    paramName: nameof(site));
            }
        }
    }

    /// <summary>
    /// Rejects an output directory inside the bundle being rendered: writing
    /// there would add generated files to the very bundle the site describes.
    /// </summary>
    /// <remarks>
    /// Always compares <see cref="StringComparison.OrdinalIgnoreCase"/>, on
    /// every platform, with no <see cref="OperatingSystem"/> branch --
    /// deliberately the opposite choice from <c>Bundle.PathComparison</c>
    /// (Ordinal on every platform). Both are correct because they guard
    /// opposite polarities. Case-sensitivity is a property of the volume, not
    /// the OS (APFS/HFS+ can be case-insensitive on macOS, which is in this
    /// repo's CI matrix; Windows can have case-sensitive directories too), so
    /// an OS-based heuristic is bypassable either way -- what differs is
    /// which direction is safe to fail in. <c>Bundle.PathComparison</c>
    /// guards §6.2 containment: it decides whether to INCLUDE a resolved path
    /// as part of the bundle, so its safe failure mode is Ordinal's stricter
    /// "reject as a match" (excluding a legitimate case-variant path is merely
    /// inconvenient). This guard's polarity is the reverse -- it decides
    /// whether to REFUSE to write, so its safe failure mode is
    /// OrdinalIgnoreCase's broader "treat as a match": on a case-insensitive
    /// volume, an out-dir spelled with different case from the bundle root
    /// (e.g. bundle root <c>/Users/x/Bundle</c>, out dir <c>/Users/x/bundle/site</c>)
    /// is the SAME physical directory, and Ordinal would miss that, silently
    /// writing the generated site into the very bundle it renders. The cost of
    /// over-refusing under OrdinalIgnoreCase is bounded and cheap: on a
    /// genuinely case-sensitive volume, a case-variant out-dir is rejected
    /// with a clear error and the user picks another directory -- strictly
    /// better than the alternative of silently polluting the bundle.
    ///
    /// The same "prefer to over-refuse" reasoning extends to reparse points:
    /// <see cref="Path.GetFullPath(string)"/> never dereferences a symlink or
    /// Windows junction, so an <c>outDir</c> that IS one (or sits behind one)
    /// can lexically look nowhere near <paramref name="bundleRoot"/> while the
    /// OS silently redirects every write into it -- e.g. <c>mklink /J
    /// out-dir bundle\generated-site</c> followed by <c>okf-render bundle
    /// --out out-dir</c>. <see cref="ReparsePoints.ResolveThroughReparsePoints"/>
    /// follows that redirect and this method also checks the resolved location, so
    /// this only ever ADDS a refusal on top of the lexical check above --
    /// never removes one -- keeping the guard at least as strict as before.
    /// </remarks>
    private static void GuardOutputDirectory(string bundleRoot, string outDir)
    {
        var root = ReparsePoints.CanonicalizeRoot(bundleRoot);
        var target = ReparsePoints.CanonicalizeRoot(outDir);

        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;

        if (ReparsePoints.IsWithin(root, target, comparison)
            || ReparsePoints.IsWithin(root, ReparsePoints.ResolveThroughReparsePoints(target), comparison))
        {
            throw new ArgumentException(
                $"refusing to render into '{outDir}': it is inside the bundle being rendered ('{bundleRoot}')",
                nameof(outDir));
        }
    }

    private static void WriteAsset(string outDir, string root, HashSet<string> verifiedDirs, string name, string content, List<string> written)
        => WriteFile(outDir, root, verifiedDirs, "assets/" + name, content, written);

    private static void WriteFile(string outDir, string root, HashSet<string> verifiedDirs, string relativePath, string content, List<string> written)
    {
        var full = Path.Combine(outDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        GuardWithinOutputDirectory(outDir, root, verifiedDirs, full, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        written.Add(relativePath);
    }

    /// <summary>
    /// Rejects a computed file path that would land outside
    /// <paramref name="outDir"/> once resolved.
    /// </summary>
    /// <remarks>
    /// From the CLI, <c>relativePath</c> always derives from a
    /// <see cref="ConceptId"/> (which rejects <c>..</c>), so this can never
    /// trip there. But <see cref="ViewerPage"/> is a public record with a
    /// public constructor in a reusable library, so a third-party host can
    /// construct one with <c>RelativeHtmlPath</c> set to something like
    /// <c>../../../evil.html</c> and reach this method with no
    /// <see cref="ConceptId"/> validation in between. Compares
    /// <see cref="StringComparison.Ordinal"/> -- deliberately the OPPOSITE
    /// choice from <see cref="GuardOutputDirectory"/> just above, because the
    /// two guards have opposite polarities and the safe failure direction
    /// flips with the polarity. <see cref="GuardOutputDirectory"/> decides
    /// whether to REFUSE, so over-matching is safe there. This one decides
    /// whether to ALLOW a write, so over-matching means over-ALLOWING: on a
    /// case-SENSITIVE volume, <c>outDir</c> <c>/tmp/ViewerOut</c> and a page
    /// path resolving to <c>/tmp/viewerout/pwned.html</c> are two genuinely
    /// different directories, yet <c>OrdinalIgnoreCase</c> would call the
    /// second a prefix-match of the first and let the escaping write
    /// through. <c>Ordinal</c>'s failure mode is the harmless one: on a
    /// case-insensitive volume a legitimate case-variant path is refused and
    /// the caller passes a consistently-cased path instead. This is the
    /// direction <see cref="ReparsePoints.IsWithin"/>'s own remarks require
    /// of a containment check that gates permission rather than refusal.
    ///
    /// <c>relativePath</c> passing the lexical check above is not the whole
    /// story: mirrors <c>Bundle.TryResolveResource</c>'s §6.2 model (its
    /// <c>OrdinalIgnoreCase</c>-vs-<c>Ordinal</c> polarity aside) by also
    /// rejecting when <paramref name="fullPath"/> itself, or any directory
    /// strictly between it and <paramref name="outDir"/>, is a reparse point
    /// -- e.g. a "tables" subdirectory of <paramref name="outDir"/> planted
    /// as a junction to somewhere else before this write. A lexical match
    /// alone cannot see that: the OS follows the junction the moment
    /// <see cref="File.WriteAllText(string, string)"/> actually touches it,
    /// landing outside <paramref name="outDir"/> even though the computed
    /// string looked contained.
    ///
    /// <paramref name="root"/> is <paramref name="outDir"/> canonicalized
    /// ONCE by the caller (<see cref="Write"/>), not re-resolved on every
    /// call -- <see cref="ReparsePoints.CanonicalizeRoot"/> is pure string
    /// work over an <c>outDir</c> that does not change mid-<see cref="Write"/>,
    /// so re-deriving it per file bought nothing but cost. The per-file
    /// checks above (<see cref="ReparsePoints.IsWithin"/>,
    /// <see cref="ReparsePoints.IsReparsePoint(string)"/> on
    /// <paramref name="fullPath"/> itself) stay exactly that -- per file,
    /// same methods, same semantics -- because a symlink planted in place of
    /// the file being written this instant must always be caught.
    ///
    /// <see cref="ReparsePoints.HasReparsePointAncestor(string, string, StringComparison)"/>
    /// is the expensive part -- it stats every directory between
    /// <paramref name="root"/> and the file -- and is genuinely redundant
    /// across every file that lands in the same directory, so
    /// <paramref name="verifiedDirs"/> caches it per directory: the walk runs
    /// for a directory's first file (checked before
    /// <see cref="Directory.CreateDirectory(string)"/> creates that
    /// directory, same order as before) and is skipped for every later file
    /// in the same directory within this <see cref="Write"/> call. This is
    /// safe against a reparse point ALREADY sitting in <paramref name="outDir"/>
    /// before rendering starts: such a directory exists at the moment its
    /// first file is checked, so <see cref="ReparsePoints.IsReparsePoint(string)"/>
    /// (which reports an existing entry's own attributes, not a cached
    /// belief) sees it and this method throws before
    /// <paramref name="verifiedDirs"/> is ever updated -- a directory is
    /// added to the cache only after its walk returns cleanly. What the
    /// cache widens is a narrower, in-flight race: a directory verified
    /// reparse-free for its first file is not re-verified for later files in
    /// the same run, so an attacker able to replace a directory inside
    /// <paramref name="outDir"/> with a junction MID-RENDER could redirect a
    /// later write in that directory. That attacker already has write access
    /// to <paramref name="outDir"/> itself, which sits outside this guard's
    /// threat model -- the model here is untrusted bundle CONTENT choosing
    /// escaping paths, and reparse points already present in
    /// <paramref name="outDir"/> before this method ever runs, not a
    /// concurrent actor racing this method's own writes.
    /// </remarks>
    private static void GuardWithinOutputDirectory(string outDir, string root, HashSet<string> verifiedDirs, string fullPath, string relativePath)
    {
        var resolved = Path.GetFullPath(fullPath);
        var directory = Path.GetDirectoryName(resolved)!;

        const StringComparison comparison = StringComparison.Ordinal;

        var escapes = !ReparsePoints.IsWithin(root, resolved, comparison)
            || ReparsePoints.IsReparsePoint(resolved)
            || (!verifiedDirs.Contains(directory) && ReparsePoints.HasReparsePointAncestor(root, directory, comparison));

        if (escapes)
        {
            throw new ArgumentException(
                $"refusing to write '{relativePath}': it resolves outside the output directory ('{outDir}')",
                paramName: "site");
        }

        verifiedDirs.Add(directory);
    }

    /// <summary>The <c>../</c> prefix taking a page at <paramref name="relativePath"/> back to the site root.</summary>
    private static string RootPrefix(string relativePath)
    {
        var depth = relativePath.Count(c => c == '/');
        return string.Concat(Enumerable.Repeat("../", depth));
    }

    private static string RenderPage(ViewerPage page)
    {
        var prefix = RootPrefix(page.RelativeHtmlPath);
        var body = new StringBuilder();

        body.Append("<h1>").Append(HtmlEscape(page.Title)).Append("</h1>\n");
        body.Append("<p class=\"meta\">").Append(HtmlEscape(page.Id.ToString())).Append("</p>\n");
        body.Append(RenderFrontmatter(page.Frontmatter));
        body.Append("<div id=\"okf-body\"></div>\n");
        body.Append(RenderBacklinks(page.Backlinks));

        return RenderShell(page.Title, prefix, body.ToString(), Payload(page));
    }

    private static string RenderIndex(ViewerSite site)
    {
        var body = new StringBuilder();
        body.Append("<h1>Bundle index</h1>\n");
        body.Append("<p class=\"meta\">")
            .Append(site.Pages.Count)
            .Append(site.Pages.Count == 1 ? " concept" : " concepts")
            .Append("</p>\n");

        if (site.ParseErrors.Count > 0)
        {
            body.Append("<div class=\"errors\">\n<h2>Parse errors</h2>\n<ul>\n");
            foreach (var error in site.ParseErrors)
            {
                body.Append("<li><code>").Append(HtmlEscape(error.Path)).Append("</code> — ")
                    .Append(HtmlEscape(error.Error)).Append("</li>\n");
            }

            body.Append("</ul>\n</div>\n");
        }

        body.Append("<div id=\"okf-body\"></div>\n");

        // The index's links already point at generated .html paths, so its
        // rewiring table is deliberately empty.
        var payload = BuildPayload(site.IndexMarkdown, "{}");
        return RenderShell("Bundle index", string.Empty, body.ToString(), payload);
    }

    private static string RenderFrontmatter(IReadOnlyList<ViewerFrontmatterEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<table class=\"frontmatter\">\n");
        foreach (var entry in entries)
        {
            sb.Append("<tr><th>").Append(HtmlEscape(entry.Key)).Append("</th><td>")
              .Append(HtmlEscape(entry.Value)).Append("</td></tr>\n");
        }

        return sb.Append("</table>\n").ToString();
    }

    private static string RenderBacklinks(IReadOnlyList<ViewerLink> backlinks)
    {
        if (backlinks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<h2>Referenced by</h2>\n<ul>\n");
        foreach (var link in backlinks)
        {
            sb.Append("<li><a href=\"").Append(HtmlEscape(link.Href)).Append("\">")
              .Append(HtmlEscape(link.RawTarget)).Append("</a></li>\n");
        }

        return sb.Append("</ul>\n").ToString();
    }

    private static string Payload(ViewerPage page)
    {
        var links = new StringBuilder("{");
        for (var i = 0; i < page.Links.Count; i++)
        {
            var link = page.Links[i];
            if (i > 0)
            {
                links.Append(',');
            }

            links.Append(HtmlSafeJson.Quote(link.RawTarget))
                 .Append(":{\"href\":").Append(HtmlSafeJson.Quote(link.Href))
                 .Append(",\"exists\":").Append(link.Exists ? "true" : "false")
                 .Append('}');
        }

        links.Append('}');
        return BuildPayload(page.Body, links.ToString());
    }

    /// <summary>
    /// The page payload <c>viewer.js</c> reads: the raw markdown body plus the
    /// link-rewiring table. Both the concept pages and the index go through
    /// here, so the two cannot drift into different shapes -- <c>viewer.js</c>
    /// parses them with one code path.
    /// </summary>
    /// <param name="body">The raw markdown, quoted here (callers pass it unescaped).</param>
    /// <param name="linksJson">The already-built links object, including its braces.</param>
    private static string BuildPayload(string body, string linksJson)
        => $"{{\"body\":{HtmlSafeJson.Quote(body)},\"links\":{linksJson}}}";

    private static string RenderShell(string title, string rootPrefix, string body, string payload)
        => $"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{HtmlEscape(title)}</title>
        <link rel="stylesheet" href="{rootPrefix}assets/viewer.css">
        </head>
        <body>
        <div class="topline"></div>
        <header class="bar"><div class="bar-in">
        <a class="wordmark" href="{rootPrefix}index.html">OKF<sup>§</sup></a>
        </div></header>
        <main>
        {body}</main>
        <script type="application/json" id="okf-payload">{payload}</script>
        <script src="{rootPrefix}assets/marked.min.js"></script>
        <script src="{rootPrefix}assets/viewer.js"></script>
        </body>
        </html>

        """;

    /// <summary>
    /// Escapes text interpolated into the generated markup. Bundle content is
    /// semi-trusted -- a bundle may come from a third-party repository -- so
    /// every value reaching the page goes through this.
    /// </summary>
    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
}
