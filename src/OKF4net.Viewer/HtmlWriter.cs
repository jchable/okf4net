// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
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
    /// would pollute the bundle being viewed, or cannot be resolved because an
    /// entry on its path is a link that cannot be followed or could not be
    /// inspected; or a page in
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
    /// filesystem is not a site); or an explicit
    /// <see cref="ViewerSite.GraphPagePath"/> is not one
    /// <c>[A-Za-z0-9._-]+.html</c> segment, or collides with a page, a page's
    /// folder or <c>index.html</c> (a computed one never does: spec §12.3).
    /// </exception>
    public static IReadOnlyList<string> Write(ViewerSite site, string outDir)
    {
        var graphPage = GraphPagePathOf(site);

        // Pinned on the site from here on: a computed name walks every page,
        // so any later GraphPagePathOf(site) -- the internal RenderDocumentStart
        // and RenderHeader overloads included -- only validates this explicit
        // name and never walks the pages again. Once per Write, by construction.
        site = site with { GraphPagePath = graphPage };
        var lookups = new PageLookups(site);
        GuardNoCaseCollisions(site, graphPage);
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

        WriteAssets(site, outDir, root, verifiedDirs, written);

        WriteFile(outDir, root, verifiedDirs, "index.html", RenderIndex(site, graphPage), written);

        WriteFile(outDir, root, verifiedDirs, graphPage, RenderGraph(site, graphPage), written);

        foreach (var page in site.Pages)
        {
            WriteFile(outDir, root, verifiedDirs, page.RelativeHtmlPath, RenderPage(site, graphPage, page, lookups), written);
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
    /// output volume. The asset files under <c>assets/</c> (the embedded
    /// scripts, stylesheet, fonts and licence texts, and the generated
    /// <c>okf-index.js</c>) are left out of the set: every generated page path
    /// ends in <c>.html</c> and no asset path does (<c>HtmlWriterAssetsTests</c>
    /// pins the second half), so no page FILE can collide with an asset file
    /// under any string comparer -- adding entries
    /// that can never fire would only pad the set without making it any more
    /// honest. A page DIRECTORY can still be named like an asset (a concept
    /// <c>assets/okf-site.js/x</c> needs a directory where the asset file
    /// is): this guard does not catch that, and the write then fails with an
    /// <see cref="IOException"/> rather than overwriting anything.
    ///
    /// The graph page's name (<paramref name="graphPage"/>, spec §12.3) is
    /// seeded too, and a page whose first path segment is that name -- a
    /// folder -- is refused as well. <see cref="SiteModel.FreeGraphPagePath"/>
    /// never picks a name that trips either check, so only an explicit
    /// <see cref="ViewerSite.GraphPagePath"/> set by a host can: a host error,
    /// reported before anything is written.
    /// </remarks>
    private static void GuardNoCaseCollisions(ViewerSite site, string graphPage)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["index.html"] = "the generated index page",
        };

        if (!seen.TryAdd(graphPage, "the graph page"))
        {
            throw new ArgumentException(
                $"the graph page '{graphPage}' would render to the same file as {seen[graphPage]}",
                paramName: nameof(site));
        }

        foreach (var page in site.Pages)
        {
            var description = $"concept '{page.Id}'";
            if (!seen.TryAdd(page.RelativeHtmlPath, description))
            {
                throw new ArgumentException(
                    $"{seen[page.RelativeHtmlPath]} and {description} would render to the same file on a case-insensitive volume ('{page.RelativeHtmlPath}')",
                    paramName: nameof(site));
            }

            var slash = page.RelativeHtmlPath.IndexOf('/');
            if (slash > 0 && string.Equals(page.RelativeHtmlPath[..slash], graphPage, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"{description} lives in a folder named like the graph page ('{graphPage}')",
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
    /// --out out-dir</c>. <see cref="ReparsePoints.TryResolveThroughReparsePoints"/>
    /// follows that redirect and this method also checks the resolved location, so
    /// this only ever ADDS a refusal on top of the lexical check above --
    /// never removes one -- keeping the guard at least as strict as before.
    /// When that resolution cannot inspect an entry on <c>outDir</c>'s path,
    /// or cannot follow a link on it (a junction whose attributes the current
    /// user may not read is still traversed by the writes that follow), where
    /// <c>outDir</c> lands is unknown, and this guard refuses rather than
    /// assume the lexical path: a guard fails closed (see
    /// <see cref="ReparsePoints.IsReparsePointOrUninspectable"/>). That refusal
    /// has its own message, because "it is inside the bundle" would be a
    /// diagnosis the guard never made. A path that simply does not exist --
    /// missing directories, an empty drive, a share that is not there -- is
    /// not uninspectable and is not refused here: the write that follows
    /// reports its own, accurate I/O error.
    /// </remarks>
    private static void GuardOutputDirectory(string bundleRoot, string outDir)
    {
        var root = ReparsePoints.CanonicalizeRoot(bundleRoot);
        var target = ReparsePoints.CanonicalizeRoot(outDir);

        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;

        if (ReparsePoints.IsWithin(root, target, comparison))
        {
            throw InsideTheBundle(bundleRoot, outDir);
        }

        if (!ReparsePoints.TryResolveThroughReparsePoints(target, out var resolvedTarget))
        {
            throw new ArgumentException(
                $"refusing to render into '{outDir}': cannot determine where it resolves (an entry on its path is a link that cannot be followed, or could not be inspected)",
                nameof(outDir));
        }

        if (ReparsePoints.IsWithin(root, resolvedTarget, comparison))
        {
            throw InsideTheBundle(bundleRoot, outDir);
        }
    }

    private static ArgumentException InsideTheBundle(string bundleRoot, string outDir) =>
        new($"refusing to render into '{outDir}': it is inside the bundle being rendered ('{bundleRoot}')", nameof(outDir));

    /// <summary>
    /// Writes every embedded asset under <c>assets/</c>, at its path below
    /// <c>Assets/</c>, byte for byte and in ordinal order of that path, then
    /// the generated <c>okf-index.js</c>. A file added under <c>Assets/</c> is
    /// embedded by the project's wildcard and written here with no other
    /// change (spec §12.0); it is only LOADED by a page whose script table or
    /// writer names it.
    /// </summary>
    private static void WriteAssets(ViewerSite site, string outDir, string root, HashSet<string> verifiedDirs, List<string> written)
    {
        foreach (var path in ViewerAssets.Paths)
        {
            WriteBytes(outDir, root, verifiedDirs, "assets/" + path, ViewerAssets.Bytes(path), written);
        }

        WriteFile(outDir, root, verifiedDirs, "assets/okf-index.js", IndexScript.Render(site.Index), written);
    }

    private static void WriteFile(string outDir, string root, HashSet<string> verifiedDirs, string relativePath, string content, List<string> written)
    {
        var full = Prepare(outDir, root, verifiedDirs, relativePath);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        written.Add(relativePath);
    }

    private static void WriteBytes(string outDir, string root, HashSet<string> verifiedDirs, string relativePath, byte[] content, List<string> written)
    {
        var full = Prepare(outDir, root, verifiedDirs, relativePath);
        File.WriteAllBytes(full, content);
        written.Add(relativePath);
    }

    /// <summary>
    /// The checked destination of one file, its directory created: text and
    /// binary writes go through the same guard (spec §11.0).
    /// </summary>
    private static string Prepare(string outDir, string root, HashSet<string> verifiedDirs, string relativePath)
    {
        var full = Path.Combine(outDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        GuardWithinOutputDirectory(outDir, root, verifiedDirs, full, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
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
    /// <see cref="ReparsePoints.IsReparsePointOrUninspectable(string)"/> on
    /// <paramref name="fullPath"/> itself) stay exactly that -- per file,
    /// same methods, same semantics -- because a symlink planted in place of
    /// the file being written this instant must always be caught.
    ///
    /// <see cref="ReparsePoints.HasReparsePointOrUninspectableAncestor(string, string, StringComparison)"/>
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
    /// first file is checked, so the ancestor walk
    /// (which reads each existing entry's own attributes, not a cached
    /// belief) sees it and this method throws before
    /// <paramref name="verifiedDirs"/> is ever updated -- a directory is
    /// added to the cache only after its walk returns cleanly. Both checks
    /// use the STRICT predicates, so the same holds for a directory whose
    /// link status cannot be read: the walk refuses it like a link, and it is
    /// never cached as safe (a guard fails closed -- see
    /// <see cref="ReparsePoints.IsReparsePointOrUninspectable"/>). What the
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
            || ReparsePoints.IsReparsePointOrUninspectable(resolved)
            || (!verifiedDirs.Contains(directory) && ReparsePoints.HasReparsePointOrUninspectableAncestor(root, directory, comparison));

        if (escapes)
        {
            throw new ArgumentException(
                $"refusing to write '{relativePath}': it resolves outside the output directory ('{outDir}')",
                paramName: "site");
        }

        verifiedDirs.Add(directory);
    }

    /// <summary>The <c>../</c> prefix taking a page at <paramref name="relativePath"/> back to the site root.</summary>
    internal static string RootPrefix(string relativePath)
    {
        var depth = relativePath.Count(c => c == '/');
        return string.Concat(Enumerable.Repeat("../", depth));
    }

    private static string RenderPage(ViewerSite site, string graphPage, ViewerPage page, PageLookups lookups)
    {
        var prefix = RootPrefix(page.RelativeHtmlPath);
        var main = RenderPageHead(site, page, prefix, lookups) + "<div id=\"okf-body\"></div>\n";
        return RenderShell(site, graphPage, ViewKind.Page, page.Title, prefix, page.Id.ToString(), main, RenderBacklinks(page.Backlinks), Payload(page));
    }

    /// <summary>Lookups every concept page shares, built once per <see cref="Write"/>.</summary>
    private sealed class PageLookups
    {
        public PageLookups(ViewerSite site)
        {
            foreach (var page in site.Pages)
            {
                PathById.TryAdd(page.Id.ToString(), page.RelativeHtmlPath);
            }

            var types = site.Index.Types;
            foreach (var concept in site.Index.Concepts)
            {
                var slot = concept.TypeIndex >= 0 && concept.TypeIndex < types.Count ? types[concept.TypeIndex].Slot : SiteIndex.OtherSlot;
                SlotById.TryAdd(concept.Id.ToString(), slot);
            }
        }

        /// <summary>Each page's path by concept id: the breadcrumb links a folder that is also a concept (C2).</summary>
        public Dictionary<string, string> PathById { get; } = new(StringComparer.Ordinal);

        /// <summary>Each concept's type slot by id, read from the index's types table, never recomputed (C5).</summary>
        public Dictionary<string, int> SlotById { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The head of a concept page (spec §11.3, §12.3): breadcrumb (C2), the one
    /// <c>&lt;h1&gt;</c> (C3), the chips (C5) and the frontmatter box (C6). The
    /// glyph slots are empty: <c>okf-page.js</c> fills them from the fixed
    /// <c>data-okf-*</c> values written here, never from bundle text.
    /// </summary>
    private static string RenderPageHead(ViewerSite site, ViewerPage page, string prefix, PageLookups lookups)
    {
        var sb = new StringBuilder("<div class=\"okf-page-head\">\n");
        sb.Append(RenderCrumbs(site, page, prefix, lookups));
        sb.Append("<h1>").Append(HtmlEscape(page.Title)).Append("</h1>\n");
        sb.Append(RenderChips(page, lookups));
        sb.Append(RenderFrontmatterBox(page.Frontmatter));
        return sb.Append("</div>\n").ToString();
    }

    private static string RenderCrumbs(ViewerSite site, ViewerPage page, string prefix, PageLookups lookups)
    {
        const string Separator = "<span class=\"okf-crumb-sep\" aria-hidden=\"true\">/</span>";
        var name = site.BundleName ?? SiteModel.BundleNameOf(site.BundleRoot);
        var sb = new StringBuilder("<nav class=\"okf-crumbs\" aria-label=\"Breadcrumb\"><ol>\n");
        sb.Append("<li><a href=\"").Append(HtmlEscape(prefix)).Append("index.html\">").Append(HtmlEscape(name)).Append("</a></li>\n");
        var segments = page.Id.Segments;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var folder = string.Join('/', segments.Take(i + 1));
            sb.Append("<li>").Append(Separator);
            if (lookups.PathById.TryGetValue(folder, out var path))
            {
                sb.Append("<a href=\"").Append(HtmlEscape(prefix + path)).Append("\">").Append(HtmlEscape(segments[i])).Append("</a>");
            }
            else
            {
                sb.Append("<span>").Append(HtmlEscape(segments[i])).Append("</span>");
            }

            sb.Append("</li>\n");
        }

        sb.Append("<li>").Append(Separator).Append("<span aria-current=\"page\">").Append(HtmlEscape(segments[^1])).Append("</span></li>\n");
        return sb.Append("</ol></nav>\n").ToString();
    }

    private static string RenderChips(ViewerPage page, PageLookups lookups)
    {
        if (page.Head is not { } head)
        {
            return string.Empty;
        }

        var slot = lookups.SlotById.TryGetValue(page.Id.ToString(), out var s) ? s : SiteIndex.OtherSlot;
        var sb = new StringBuilder("<div class=\"okf-chips\">\n");
        sb.Append("<span class=\"okf-chip okf-chip-type\"><span class=\"okf-chip-glyph\" data-okf-slot=\"")
          .Append(slot.ToString(CultureInfo.InvariantCulture)).Append("\"></span>")
          .Append(HtmlEscape(head.Type.Length == 0 ? "(no type)" : head.Type)).Append("</span>\n");
        if (head.Status is { } status)
        {
            sb.Append("<span class=\"okf-chip okf-chip-status\">").Append(HtmlEscape(status)).Append("</span>\n");
        }

        var glyph = head.Trust == AuditVocabulary.Name(TrustTier.HumanReviewed) ? "human"
            : head.Trust == AuditVocabulary.Name(TrustTier.MachineConfirmed) ? "machine"
            : null;
        sb.Append("<span class=\"okf-chip okf-chip-trust").Append(glyph is null ? " okf-chip-unverified" : string.Empty).Append("\">");
        if (glyph is not null)
        {
            sb.Append("<span class=\"okf-chip-glyph\" data-okf-trust=\"").Append(glyph).Append("\"></span>");
        }

        sb.Append(HtmlEscape(TrustText(head))).Append("</span>\n");
        if (head.StaleAfterDate is { } date)
        {
            sb.Append("<span class=\"okf-chip okf-chip-stale\"><span class=\"okf-chip-glyph\" data-okf-stale></span><span class=\"okf-chip-text\">stale after ")
              .Append(HtmlEscape(date)).Append("</span></span>\n");
        }

        return sb.Append("</div>\n").ToString();
    }

    /// <summary>The trust chip's text (C5): the tier, then " · verifier · date · +N" when the tier names a verifier.</summary>
    internal static string TrustText(ViewerPageHead head)
    {
        var text = new StringBuilder(head.Trust);
        if (head.Verifier is { } verifier)
        {
            text.Append(" · ").Append(verifier);
            if (head.VerifiedDate is { } date)
            {
                text.Append(" · ").Append(date);
            }

            if (head.MoreVerifications > 0)
            {
                text.Append(" · +").Append(head.MoreVerifications.ToString(CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private static string RenderIndex(ViewerSite site, string graphPage)
    {
        var main = new StringBuilder();
        main.Append("<h1>Bundle index</h1>\n");
        main.Append("<p class=\"meta\">")
            .Append(site.Pages.Count)
            .Append(site.Pages.Count == 1 ? " concept" : " concepts")
            .Append("</p>\n");

        if (site.ParseErrors.Count > 0)
        {
            main.Append("<div class=\"errors\">\n<h2>Parse errors</h2>\n<ul>\n");
            foreach (var error in site.ParseErrors)
            {
                main.Append("<li><code>").Append(HtmlEscape(error.Path)).Append("</code> — ")
                    .Append(HtmlEscape(error.Error)).Append("</li>\n");
            }

            main.Append("</ul>\n</div>\n");
        }

        main.Append("<div id=\"okf-body\"></div>\n");

        // The index's links already point at generated .html paths, so its
        // rewiring table is deliberately empty.
        var payload = BuildPayload(site.IndexMarkdown, "{}");
        return RenderShell(site, graphPage, ViewKind.Index, "Bundle index", string.Empty, conceptId: null, main.ToString(), aside: string.Empty, payload);
    }

    /// <summary>The three views a page of the site can be (spec §12.3), written as <c>data-okf-view</c>.</summary>
    internal enum ViewKind
    {
        /// <summary>A concept page.</summary>
        Page,

        /// <summary>The bundle index, <c>index.html</c>.</summary>
        Index,

        /// <summary>The global graph page (written by <c>RenderGraph</c>).</summary>
        Graph,
    }

    /// <summary>
    /// The scripts at the end of every concept page and of the index, in load
    /// order (spec §12.6). Each script listed here exists under
    /// <c>Assets/</c>. <c>graph.html</c> does not use this table
    /// (<c>RenderGraph</c> writes its own tags).
    /// </summary>
    internal static readonly string[] PageScripts =
    [
        "marked.min.js",
        "viewer.js",
        "okf-index.js",
        "okf-site.js",
        "okf-shapes.js",
        "okf-explorer.js",
        "okf-palette.js",
        "okf-toc.js",
        "okf-page.js",
        "okf-local.js",
    ];

    /// <summary>
    /// The graph page's file name (spec §12.3, A25): the site's explicit
    /// <see cref="ViewerSite.GraphPagePath"/>, which must be one
    /// <c>[A-Za-z0-9._-]+.html</c> segment, else the first free name.
    /// </summary>
    /// <exception cref="ArgumentException">The explicit name is not one such segment.</exception>
    internal static string GraphPagePathOf(ViewerSite site)
    {
        if (site.GraphPagePath is not { } explicitPath)
        {
            return SiteModel.FreeGraphPagePath(site.Pages);
        }

        if (!IsGraphPageName(explicitPath))
        {
            throw new ArgumentException(
                $"the graph page '{explicitPath}' is not one [A-Za-z0-9._-]+.html segment",
                paramName: nameof(site));
        }

        return explicitPath;
    }

    private static bool IsGraphPageName(string name)
        => name.Length > ".html".Length
           && name.EndsWith(".html", StringComparison.Ordinal)
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// Everything from <c>&lt;!doctype html&gt;</c> to the end of the header, for
    /// the three views (spec §12.3): the root prefix, the view and the concept
    /// on <c>&lt;html&gt;</c>; <c>okf-theme.js</c> then the stylesheet in
    /// <c>&lt;head&gt;</c> (a stored theme applies before the first paint);
    /// "Skip to content", the top line and the header. This overload computes
    /// the graph page's name itself and is the tests' entry point;
    /// <see cref="Write"/> never calls it: it computes the name once and every
    /// page it writes, <see cref="RenderGraph"/>'s graph page included, goes
    /// through the private overload that takes that name (spec §12.0, §12.5).
    /// </summary>
    internal static string RenderDocumentStart(ViewerSite site, ViewKind view, string title, string rootPrefix, string? conceptId)
        => RenderDocumentStart(site, GraphPagePathOf(site), view, title, rootPrefix, conceptId);

    /// <summary>
    /// <see cref="RenderDocumentStart(ViewerSite, ViewKind, string, string, string?)"/>
    /// with the graph page's name already computed: <see cref="Write"/>
    /// computes it once for every page it writes.
    /// </summary>
    private static string RenderDocumentStart(ViewerSite site, string graphPage, ViewKind view, string title, string rootPrefix, string? conceptId)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\" data-okf-root=\"").Append(HtmlEscape(rootPrefix))
          .Append("\" data-okf-view=\"").Append(ViewName(view)).Append('"');
        if (conceptId is not null)
        {
            sb.Append(" data-okf-concept=\"").Append(HtmlEscape(conceptId)).Append('"');
        }

        sb.Append(">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<title>").Append(HtmlEscape(title)).Append("</title>\n")
          .Append(ScriptTag(rootPrefix, "okf-theme.js"))
          .Append("<link rel=\"stylesheet\" href=\"").Append(HtmlEscape(rootPrefix)).Append("assets/viewer.css\">\n")
          .Append("</head>\n<body>\n")
          .Append("<a class=\"okf-skip\" href=\"#okf-main\">Skip to content</a>\n")
          .Append("<div class=\"topline\"></div>\n")
          .Append(RenderHeader(site, graphPage, view, rootPrefix, conceptId));
        return sb.ToString();
    }

    private static string ViewName(ViewKind view) => view switch
    {
        ViewKind.Page => "page",
        ViewKind.Index => "index",
        _ => "graph",
    };

    /// <summary>
    /// The header (spec §11.1, §12.3): wordmark, bundle name, counts, and the
    /// tools: "Reading view" (graph view only), then "Global graph", the one
    /// source of the graph page's real name -- with the concept as fragment
    /// on a concept page, current on the graph view. The palette, "Filters"
    /// and the theme button are added by their scripts.
    /// </summary>
    internal static string RenderHeader(ViewerSite site, ViewKind view, string rootPrefix, string? conceptId)
        => RenderHeader(site, GraphPagePathOf(site), view, rootPrefix, conceptId);

    /// <summary>
    /// <see cref="RenderHeader(ViewerSite, ViewKind, string, string?)"/> with the
    /// graph page's name already computed.
    /// </summary>
    private static string RenderHeader(ViewerSite site, string graphPage, ViewKind view, string rootPrefix, string? conceptId)
    {
        var name = site.BundleName ?? SiteModel.BundleNameOf(site.BundleRoot);
        var sb = new StringBuilder("<header class=\"bar\"><div class=\"bar-in\">\n");
        sb.Append("<a class=\"wordmark\" href=\"").Append(HtmlEscape(rootPrefix)).Append("index.html\">OKF4net<sup>§</sup></a>\n");
        sb.Append("<span class=\"bar-sep\" aria-hidden=\"true\"></span>\n");
        sb.Append("<span class=\"bar-bundle\" id=\"okf-bundle-name\" title=\"").Append(HtmlEscape(name)).Append("\">")
          .Append(HtmlEscape(name)).Append("</span>\n");
        if (Counts(site) is { } counts)
        {
            sb.Append("<span class=\"bar-counts\" id=\"okf-bundle-counts\">").Append(counts).Append("</span>\n");
        }

        sb.Append("<div class=\"bar-tools\" id=\"okf-tools\">\n");
        if (view == ViewKind.Graph)
        {
            sb.Append("<a class=\"okf-tool\" id=\"okf-reading-view\" href=\"").Append(HtmlEscape(rootPrefix)).Append("index.html\">Reading view</a>\n");
        }

        var href = view == ViewKind.Page && conceptId is not null
            ? rootPrefix + graphPage + "#" + conceptId
            : rootPrefix + graphPage;
        sb.Append("<a class=\"okf-tool okf-tool-graph\" id=\"okf-global-graph\" href=\"").Append(HtmlEscape(href)).Append('"');
        if (view == ViewKind.Graph)
        {
            sb.Append(" aria-current=\"page\"");
        }

        sb.Append(">Global graph</a>\n</div>\n</div></header>\n");
        return sb.ToString();
    }

    /// <summary>
    /// "N concepts · M links" (spec §11.1, H6): concepts and merged edges of the
    /// index, links to absent concepts included, singular for one. Null --
    /// the counts are omitted -- for a site built by hand whose index is empty
    /// although it has pages.
    /// </summary>
    internal static string? Counts(ViewerSite site)
    {
        var concepts = site.Index.Concepts.Count;
        if (concepts == 0 && site.Pages.Count > 0)
        {
            return null;
        }

        var links = site.Index.Edges.Count;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{concepts} {(concepts == 1 ? "concept" : "concepts")} · {links} {(links == 1 ? "link" : "links")}");
    }

    /// <summary>One <c>&lt;script src&gt;</c> line loading an asset, relative to the page's root prefix.</summary>
    internal static string ScriptTag(string rootPrefix, string name)
        => $"<script src=\"{HtmlEscape(rootPrefix)}assets/{HtmlEscape(name)}\"></script>\n";

    /// <summary>
    /// A concept page or the index (spec §12.3, §12.6): document start and
    /// header, the three zones, the payload and the script table. Its ids and
    /// attributes are the contract the interactive scripts rely on.
    /// </summary>
    private static string RenderShell(ViewerSite site, string graphPage, ViewKind view, string title, string rootPrefix, string? conceptId, string main, string aside, string payload)
    {
        var sb = new StringBuilder(RenderDocumentStart(site, graphPage, view, title, rootPrefix, conceptId));
        sb.Append("<div class=\"okf-layout\">\n")
          .Append("<nav class=\"okf-explorer\" id=\"okf-explorer\" aria-label=\"Explorer\" hidden></nav>\n")
          .Append("<main id=\"okf-main\">\n").Append(main).Append("</main>\n")
          .Append("<aside class=\"okf-context\" id=\"okf-context\" aria-label=\"Page context\"").Append(aside.Length == 0 ? " hidden" : string.Empty).Append(">\n")
          .Append("<section class=\"okf-toc\" id=\"okf-toc\" aria-labelledby=\"okf-toc-title\" hidden>\n")
          .Append("<h2 id=\"okf-toc-title\" class=\"okf-section-title\">On this page</h2>\n<ul></ul>\n</section>\n")
          .Append(aside).Append("</aside>\n</div>\n")
          .Append("<script type=\"application/json\" id=\"okf-payload\">").Append(payload).Append("</script>\n");
        foreach (var script in PageScripts)
        {
            sb.Append(ScriptTag(rootPrefix, script));
        }

        return sb.Append("</body>\n</html>\n").ToString();
    }

    /// <summary>
    /// The frontmatter box (spec §11.3, C6): every entry in document order in a
    /// two-column grid; the folded ones carry <c>data-okf-extra</c>, hidden by
    /// <c>viewer.css</c> only while JavaScript runs (<c>html[data-okf-js]</c>)
    /// and the box is not expanded. Without JavaScript everything shows.
    /// </summary>
    private static string RenderFrontmatterBox(IReadOnlyList<ViewerFrontmatterEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<section class=\"okf-fm\" id=\"okf-fm\" aria-labelledby=\"okf-fm-title\">\n");
        sb.Append("<div class=\"okf-fm-head\"><h2 class=\"okf-section-title\" id=\"okf-fm-title\">Frontmatter · ")
          .Append(entries.Count.ToString(CultureInfo.InvariantCulture))
          .Append(entries.Count == 1 ? " field" : " fields").Append("</h2></div>\n");
        sb.Append("<div class=\"okf-fm-grid\" id=\"okf-fm-grid\">\n");
        foreach (var entry in entries)
        {
            sb.Append("<div class=\"okf-fm-cell\"").Append(entry.Extra ? " data-okf-extra" : string.Empty).Append('>')
              .Append("<span class=\"okf-fm-key\">").Append(HtmlEscape(entry.Key)).Append("</span>")
              .Append("<span class=\"okf-fm-value").Append(entry.Structured ? " okf-fm-struct" : string.Empty).Append("\">")
              .Append(HtmlEscape(entry.Value)).Append("</span></div>\n");
        }

        return sb.Append("</div>\n</section>\n").ToString();
    }

    /// <summary>
    /// "Referenced by · N" (spec §11.4, X10): <c>.okf-row</c> links, each
    /// carrying its source id in <c>data-okf-target</c> so <c>okf-page.js</c>
    /// finds its type in the index and adds its glyph.
    /// </summary>
    private static string RenderBacklinks(IReadOnlyList<ViewerLink> backlinks)
    {
        if (backlinks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<section class=\"okf-backlinks\" aria-labelledby=\"okf-backlinks-title\">\n")
            .Append("<h2 id=\"okf-backlinks-title\" class=\"okf-section-title\">Referenced by <span class=\"okf-count\">· ")
            .Append(backlinks.Count.ToString(CultureInfo.InvariantCulture)).Append("</span></h2>\n<ul>\n");
        foreach (var link in backlinks)
        {
            sb.Append("<li><a class=\"okf-row\" href=\"").Append(HtmlEscape(link.Href))
              .Append("\" data-okf-target=\"").Append(HtmlEscape(link.RawTarget)).Append("\">")
              .Append(HtmlEscape(link.RawTarget)).Append("</a></li>\n");
        }

        return sb.Append("</ul>\n</section>\n").ToString();
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
        // The display body: the leading H1 that repeats the title is already
        // in the page head (spec §11.3, C4); viewer.js renders what it gets.
        return BuildPayload(page.DisplayBody ?? page.Body, links.ToString());
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

    /// <summary>The scripts at the end of the graph page's body, in this order (spec §12.5, §12.6).</summary>
    private static readonly string[] GraphScripts =
        ["okf-index.js", "okf-site.js", "okf-shapes.js", "okf-palette.js", "okf-sim.js", "okf-graph.js"];

    /// <summary>
    /// The fixed skeleton <c>okf-graph.js</c> fills (spec §12.5). Written with
    /// explicit <c>\n</c> line endings, not a raw string literal, so the page
    /// is the same bytes on a CRLF checkout.
    /// </summary>
    private const string GraphSkeleton =
        "\n<div class=\"okf-graph-layout\" id=\"okf-graph-layout\">\n"
        + "<aside class=\"okf-facets\" id=\"okf-facets\" aria-label=\"Facets\"></aside>\n"
        + "<main id=\"okf-main\" class=\"okf-graph-main\" aria-label=\"Global graph\">\n"
        + "<p class=\"okf-graph-status\" id=\"okf-graph-status\" role=\"status\"></p>\n"
        + "<div class=\"okf-graph-zoom\" id=\"okf-graph-zoom\"></div>\n"
        + "<div class=\"okf-graph-canvas\" id=\"okf-graph-canvas\"></div>\n"
        + "<section class=\"okf-graph-list\" id=\"okf-graph-list\" aria-label=\"Concepts and links\" hidden></section>\n"
        + "<p class=\"okf-graph-legend\" id=\"okf-graph-legend\"></p>\n"
        + "</main>\n"
        + "<aside class=\"okf-graph-detail\" id=\"okf-graph-detail\" aria-label=\"Selected concept\"></aside>\n"
        + "</div>\n"
        + "<noscript><p>The graph needs JavaScript. <a href=\"index.html\">Bundle index</a></p></noscript>\n";

    /// <summary>
    /// The global graph page (spec §12.5): the shared document start and header
    /// in their <c>graph</c> view, the fixed skeleton <c>okf-graph.js</c> fills,
    /// and its six scripts. The only method that writes this page or loads
    /// <c>okf-sim.js</c> and <c>okf-graph.js</c> (§12.0). The page sits at the
    /// site root, so its root prefix is empty. It renders no markdown: no
    /// payload, no <c>marked</c>, no <c>viewer.js</c>. <paramref name="graphPage"/>
    /// is the name <see cref="Write"/> computed once for the whole site.
    /// </summary>
    private static string RenderGraph(ViewerSite site, string graphPage)
    {
        var page = new StringBuilder(RenderDocumentStart(site, graphPage, ViewKind.Graph, "Global graph", string.Empty, null));
        page.Append(GraphSkeleton);
        // ScriptTag returns a whole line, its "\n" included (spec §12.3).
        foreach (var script in GraphScripts)
        {
            page.Append(ScriptTag(string.Empty, script));
        }

        return page.Append("</body>\n</html>\n").ToString();
    }

    /// <summary>
    /// Escapes text interpolated into the generated markup, attribute values
    /// included (always written between double quotes): <c>&amp; &lt; &gt; "</c>.
    /// Bundle content is semi-trusted -- a bundle may come from a third-party
    /// repository -- so every value reaching the page goes through this.
    /// </summary>
    internal static string HtmlEscape(string value)
        => value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
}
