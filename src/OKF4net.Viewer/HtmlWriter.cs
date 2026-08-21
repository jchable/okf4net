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
    /// <c>../</c>-escaping path on a hand-constructed <see cref="ViewerPage"/>).
    /// </exception>
    public static IReadOnlyList<string> Write(ViewerSite site, string outDir)
    {
        GuardOutputDirectory(site.BundleRoot, outDir);

        var written = new List<string>();
        Directory.CreateDirectory(outDir);

        WriteAsset(outDir, "viewer.css", ViewerAssets.Css, written);
        WriteAsset(outDir, "viewer.js", ViewerAssets.ViewerJs, written);
        WriteAsset(outDir, "marked.min.js", ViewerAssets.MarkedJs, written);

        WriteFile(outDir, "index.html", RenderIndex(site), written);

        foreach (var page in site.Pages)
        {
            WriteFile(outDir, page.RelativeHtmlPath, RenderPage(page), written);
        }

        return written;
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
    /// out-dir bundle\generated-site</c> followed by <c>okf render bundle
    /// --out out-dir</c>. <see cref="ResolveThroughReparsePoints"/> follows
    /// that redirect and this method also checks the resolved location, so
    /// this only ever ADDS a refusal on top of the lexical check above --
    /// never removes one -- keeping the guard at least as strict as before.
    /// </remarks>
    private static void GuardOutputDirectory(string bundleRoot, string outDir)
    {
        var root = ReparsePoints.CanonicalizeRoot(bundleRoot);
        var target = ReparsePoints.CanonicalizeRoot(outDir);

        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;

        if (ReparsePoints.IsWithin(root, target, comparison)
            || ReparsePoints.IsWithin(root, ResolveThroughReparsePoints(target), comparison))
        {
            throw new ArgumentException(
                $"refusing to render into '{outDir}': it is inside the bundle being rendered ('{bundleRoot}')",
                nameof(outDir));
        }
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to the real location the OS would
    /// land on once it actually touches disk, by walking upward from
    /// <paramref name="path"/> (inclusive) for the nearest ancestor that is
    /// itself a filesystem reparse point (symlink, junction, mount point),
    /// resolving that ancestor to its final target via
    /// <see cref="Directory.ResolveLinkTarget(string, bool)"/>, and
    /// re-attaching whatever trailing path segments do not exist yet.
    /// Returns <paramref name="path"/> unchanged if no ancestor up to the
    /// filesystem root is a reparse point -- the common case, and the only
    /// one <see cref="Path.GetFullPath(string)"/> alone can see.
    /// </summary>
    /// <remarks>
    /// Bounded by <paramref name="path"/>'s own ancestor depth, not by
    /// <c>bundleRoot</c>: unlike <see cref="ReparsePoints.HasReparsePointAncestor(string, string)"/>
    /// (which walks a candidate KNOWN to be lexically nested under a root,
    /// and can safely stop there), an <c>outDir</c> under attack here is NOT
    /// lexically nested under the bundle root at all -- that is the whole
    /// point of the bypass -- so there is no shorter bound to walk to than
    /// "however deep outDir's own path is". Deliberately does not chase a
    /// SECOND reparse point that might appear further up past the first one
    /// resolved: any escape reachable only through a reparse point nested
    /// INSIDE the resolved location is <see cref="GuardWithinOutputDirectory"/>'s
    /// concern (it walks every intermediate directory between outDir and each
    /// file actually written), not this one-shot outDir resolution's.
    /// </remarks>
    private static string ResolveThroughReparsePoints(string path)
    {
        var current = path;
        var tail = new List<string>();

        while (true)
        {
            if (ReparsePoints.IsReparsePoint(current))
            {
                var resolvedTarget = Directory.ResolveLinkTarget(current, returnFinalTarget: true);
                if (resolvedTarget is null)
                {
                    // IsReparsePoint(current) just returned true, so this
                    // should not happen -- but resolution is not this
                    // method's only line of defense (see remarks above), so
                    // fail safe by falling back to the lexical path rather
                    // than throwing.
                    return path;
                }

                var resolved = resolvedTarget.FullName;
                for (var i = tail.Count - 1; i >= 0; i--)
                {
                    resolved = Path.Combine(resolved, tail[i]);
                }

                return resolved;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                return path; // Reached the filesystem root without finding a reparse point.
            }

            tail.Add(Path.GetFileName(current));
            current = parent;
        }
    }

    private static void WriteAsset(string outDir, string name, string content, List<string> written)
        => WriteFile(outDir, "assets/" + name, content, written);

    private static void WriteFile(string outDir, string relativePath, string content, List<string> written)
    {
        var full = Path.Combine(outDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        GuardWithinOutputDirectory(outDir, full, relativePath);
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
    /// <see cref="ConceptId"/> validation in between. Uses the same
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> comparison as
    /// <see cref="GuardOutputDirectory"/>, for the same polarity reason
    /// documented on its remarks: this check also decides whether to ALLOW a
    /// write (into <paramref name="outDir"/>), so its safe failure mode is
    /// the broader "treat as a case-insensitive match" -- on a
    /// case-insensitive volume, failing to recognize a legitimate
    /// case-variant path as being inside <paramref name="outDir"/> would
    /// wrongly refuse a write that was actually safe, whereas the reverse
    /// mistake (a genuinely different, case-sensitive path being let through)
    /// cannot happen: an OrdinalIgnoreCase-only match against the resolved
    /// root still requires the resolved path to be a text prefix of the
    /// resolved root, which a traversal outside it is not.
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
    /// </remarks>
    private static void GuardWithinOutputDirectory(string outDir, string fullPath, string relativePath)
    {
        var root = ReparsePoints.CanonicalizeRoot(outDir);
        var resolved = Path.GetFullPath(fullPath);

        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;

        if (!ReparsePoints.IsWithin(root, resolved, comparison)
            || ReparsePoints.IsReparsePoint(resolved)
            || ReparsePoints.HasReparsePointAncestor(root, resolved, comparison))
        {
            throw new ArgumentException(
                $"refusing to write '{relativePath}': it resolves outside the output directory ('{outDir}')",
                paramName: "site");
        }
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
