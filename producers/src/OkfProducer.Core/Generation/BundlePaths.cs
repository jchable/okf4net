// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Generation;

/// <summary>
/// The one containment question this producer asks of the filesystem, and the helpers that answer
/// it: where a bundle root really is, where a path under it really lands once every symbolic link
/// and junction has been followed, and whether that is still inside.
///
/// <para><b>Why it is a type of its own rather than private members of <see cref="BundleWriter"/>,
/// where all of this started.</b> <see cref="GenerationManifest.WriteTo"/> writes into the bundle
/// root too, and it lives in another file; gating it meant either a second component walk or this
/// move. A second walk is the failure mode this codebase avoids by policy -- two resolutions that
/// drift apart give two different answers to "is this inside the bundle", and the one that answers
/// "yes" is the one that writes.</para>
///
/// <para><b>What this type does not claim.</b> Nothing here makes an untrusted bundle safe to
/// generate into in general, and nothing here knows which callers ask. It answers one question
/// about one path at the moment it is asked; each caller is responsible for asking, and for what it
/// does with a refusal. Which calls are gated is documented at those calls, and deliberately not
/// totalled anywhere -- two rounds of review have now been wrong about the total, in both
/// directions.</para>
/// </summary>
internal static class BundlePaths
{
    /// <summary>
    /// How two paths are compared for equality and containment: case-insensitively on Windows, where
    /// the filesystem is, and ordinally elsewhere.
    /// </summary>
    internal static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// <paramref name="bundleRoot"/> as an absolute path with its own reparse point followed, or
    /// <see langword="null"/> when it is a link this process cannot follow.
    ///
    /// <para>Resolved rather than taken literally so that a bundle which <i>is</i> a junction -- or
    /// sits behind one the operator created deliberately -- is not treated as an escape by every
    /// containment check below. Only the root's own link is followed; a link on one of its ancestors
    /// is irrelevant, because every path compared against it is built from this same value.</para>
    /// </summary>
    internal static string? ResolveRoot(string bundleRoot)
    {
        try
        {
            // Trimmed, not just full-pathed: `Path.GetFullPath` PRESERVES a trailing separator
            // (`"dir/"` -> `"dir/"`, `"dir"` -> `"dir"`), and `IsInside` below compares against
            // `root + DirectorySeparatorChar` -- so an untrimmed root with `--out dir/` compares as
            // `"dir/" + "/"`, which no real path under it can ever start with. Every candidate then
            // reads as outside the root and is refused with the reparse-point message below, even
            // though nothing was linked at all. `TrimEndingDirectorySeparator` leaves a bare drive or
            // filesystem root (`C:\`, `/`) alone by design -- trimming those would change what they
            // mean -- so this is safe to apply unconditionally.
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
            var info = new DirectoryInfo(full);
            if (info.LinkTarget is null)
            {
                return full;
            }

            return info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName))
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where <paramref name="candidate"/> really lands once every symbolic link and junction between
    /// <paramref name="resolvedRoot"/> and it has been followed, or <see langword="null"/> when that
    /// lands outside the root -- or when the answer cannot be established at all.
    ///
    /// <para>Walked component by component because the BCL cannot answer it in one call:
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> resolves the path it is given only if <i>that</i>
    /// path is itself a link, and the dangerous shape is a link several components up with an ordinary
    /// file name hanging off it. <c>returnFinalTarget: true</c> at each hop, since a chain of links
    /// that passes back through the bundle proves nothing about where the last one lands.</para>
    ///
    /// <para>Every failure is <see langword="null"/>, which the callers read as "refuse". A broken
    /// link, a permission error, a path the platform rejects: none of them is evidence that deleting
    /// is safe, and this is the code path that ends in <see cref="File.Delete(string)"/>. The broken
    /// link is the one this used to get wrong -- see <see cref="LinkAt"/>.</para>
    /// </summary>
    internal static string? ResolveInsideRoot(string resolvedRoot, string candidate)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(resolvedRoot, candidate);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var current = resolvedRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            try
            {
                current = Path.Combine(current, segment);

                FileSystemInfo? info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current) ? new FileInfo(current) : LinkAt(current);

                if (info?.LinkTarget is null)
                {
                    continue;
                }

                if (info.ResolveLinkTarget(returnFinalTarget: true) is not { } target)
                {
                    return null;
                }

                current = Path.GetFullPath(target.FullName);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return IsInside(resolvedRoot, current) ? current : null;
    }

    /// <summary>
    /// The reparse point at <paramref name="path"/> when neither <see cref="Directory.Exists"/> nor
    /// <see cref="File.Exists"/> could see one, or <see langword="null"/> when there is none.
    ///
    /// <para><b>Why the Exists probes are not enough, which the doc above claimed they were.</b> Both
    /// of them FOLLOW a symbolic link, so a link whose target has been removed answers false to both
    /// -- and the component walk above then treated it as an ordinary path component and carried on,
    /// while <see cref="ResolveInsideRoot"/> promised in writing that a broken link is refused. The
    /// link's own target string is still on disk, so asking for it directly finds it.</para>
    ///
    /// <para><b>Measured, and it is not uniform.</b> On Windows a dangling <i>junction</i> is already
    /// caught: a junction is a real directory entry, so <c>Directory.Exists</c> answers true even with
    /// its target gone, and the walk resolves it. The gap is a dangling <i>symbolic</i> link, which
    /// needs SeCreateSymbolicLinkPrivilege to create on Windows -- so no test in this suite can reach
    /// this method on an ordinary Windows run, and it is left untested rather than covered by an
    /// assertion that would pass whatever this code did. On Unix, where a symbolic link needs no
    /// privilege, it is the ordinary shape.</para>
    ///
    /// <para>Reached only after both probes have failed, deliberately: probing first would change
    /// which of <see cref="DirectoryInfo"/> and <see cref="FileInfo"/> is handed to
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> for links that resolve perfectly well today,
    /// and that argument is not inert -- it tells the BCL which kind of object to expect at the far
    /// end. This adds the missing case without moving any case that already worked.</para>
    /// </summary>
    internal static FileSystemInfo? LinkAt(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null)
        {
            return directory;
        }

        var file = new FileInfo(path);
        return file.LinkTarget is not null ? file : null;
    }

    /// <summary>
    /// Whether <paramref name="path"/> starts with <paramref name="root"/> followed by a directory
    /// separator, compared under <see cref="PathComparison"/> -- a plain string test, and deliberately
    /// nothing more.
    ///
    /// <para><b>What it answers, precisely.</b> "Strictly under": the root itself is not inside, and
    /// neither is any spelling of it with a trailing separator. It does not normalise either argument:
    /// <c>root/../other/x</c> starts with <c>root/</c> and IS inside by this test, so it is only
    /// meaningful for a <paramref name="path"/> that is already a full, normalised path (<see
    /// cref="Path.GetFullPath(string)"/>) and a <paramref name="root"/> that is already resolved and
    /// trimmed (<see cref="ResolveRoot"/>). A filesystem root (<c>C:\</c>, <c>/</c>) as
    /// <paramref name="root"/> matches nothing, since its own separator is doubled by the test.</para>
    ///
    /// <para><b>Why it is not <see cref="TryGetPathUnderRoot"/>.</b> Its callers are
    /// <see cref="BundleWriter"/>'s containment checks on paths it has itself built from
    /// <see cref="ResolveRoot"/>'s answer: where a concept may be written, whether an owned prefix lies
    /// in the bundle before anything under it is pruned, and how a refusal message names a path. There
    /// the root itself must NOT count as inside -- a concept or a prune target that IS the bundle root
    /// is exactly what must be refused -- and the comparison has to be the same <see
    /// cref="PathComparison"/> <see cref="ResolveInsideRoot"/> uses, so the two cannot disagree about a
    /// path they both check. <see cref="TryGetPathUnderRoot"/> answers the repository question instead
    /// ("at or under, normalising both sides"), and is what the scanner and the code stage use; E11 fix
    /// round 1 moved <c>RepositoryScanner</c> off this method, where it had been asking that other
    /// question with the wrong tool.</para>
    /// </summary>
    internal static bool IsInside(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>
    /// Whether <paramref name="path"/> is itself a symbolic link or a junction -- a broken one
    /// included, since a link whose target has been removed is still a link.
    ///
    /// <para>Expressed through <see cref="LinkAt"/> rather than through a fresh pair of probes: the
    /// probe order is the part that is easy to get wrong, and one implementation of it is enough.
    /// Unlike <see cref="ResolveInsideRoot"/> this does not follow the link, so it answers for a link
    /// pointing anywhere at all, inside the bundle or out of it.</para>
    ///
    /// <para><b>This does not, on its own, fail closed.</b> The catch below reports a link for a path
    /// the platform rejects or a genuine I/O error, but it is NOT reached by a permission denial: both
    /// <c>DirectoryInfo.LinkTarget</c> and <c>FileInfo.LinkTarget</c> answer <see langword="null"/>
    /// without throwing there (measured in E11 fix round 1, on Linux with <c>chmod 000</c> and on
    /// Windows with a deny-read ACE -- the Unix readlink wrapper maps EACCES to "not a link"), so an
    /// uninspectable path comes back "not a link" from HERE. Reporting it as a link is
    /// <see cref="IsLinkOrUninspectable"/>'s job, which classifies the level with
    /// <see cref="File.GetAttributes(string)"/> first; that is the predicate
    /// <see cref="HasLinkAncestor"/> -- and so every guard that decides whether to READ something --
    /// actually walks over.
    ///
    /// <para><b>That is tolerable at all three of this method's own callers, but not for one reason.</b>
    /// Two of them (<c>RepositoryScanner</c>'s subdirectory descent and <c>BundleDrift</c>'s walk)
    /// use it to decide whether to walk INTO a directory, and there a wrong "not a link" costs
    /// coverage, not containment. The third, <c>BundleWriter.FirstLinkUnder</c>, is the opposite
    /// shape: its caller <c>ResetBundle</c> uses a non-null answer to REFUSE a recursive delete, so a
    /// wrong "not a link" lets the delete proceed. What keeps that safe is not this predicate but
    /// <see cref="Directory.Delete(string, bool)"/> itself, which unlinks a reparse point rather than
    /// descending through it -- the scan is there to turn a half-finished delete into a clean refusal,
    /// belt and braces over a containment guarantee the BCL already makes.</para>
    /// </summary>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            return LinkAt(path) is not null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// <paramref name="path"/> relative to <paramref name="root"/>, in the platform's own separators
    /// (<c>"."</c> for the root itself), or <see langword="false"/> when <paramref name="path"/> is not
    /// at or under <paramref name="root"/> at all.
    ///
    /// <para><b>The one repository-containment question this producer asks</b>, answered here so the
    /// scanner, the Roslyn engine, the tree-sitter engine and <see cref="SourceOwnershipMap"/> cannot drift
    /// apart on it (E11: <c>CompilationFactory</c>, <c>RoslynResolver</c> and <c>SourceOwnershipMap</c>
    /// each carried a copy, and <c>RoslynResolver</c>'s had drifted -- it tested the leading <c>..</c> as a
    /// string PREFIX, so a directory literally named <c>..foo</c> read as outside the repository; E11 fix
    /// round 1 moved <c>RepositoryScanner</c>'s solution-project filter off <see cref="IsInside"/> onto
    /// this too).</para>
    ///
    /// <para>Not under the root means: a different drive or share (the relative answer comes back
    /// rooted), or a first SEGMENT of exactly <c>..</c>. Any other first segment is a name, not a climb:
    /// <c>..foo</c>, <c>...</c>, <c>.. </c> (trailing space), and -- on POSIX, where a backslash is an
    /// ordinary filename character -- <c>..\x</c>. Separators are the platform's own.
    /// <see cref="Path.GetRelativePath(string, string)"/> settles casing on the platform's own terms
    /// rather than by a <see cref="StringComparison"/> picked here (measured on Windows:
    /// <c>GetRelativePath(@"C:\REPO", @"C:\repo\a\x.cs")</c> is <c>a\x.cs</c>), and makes both arguments
    /// absolute first -- so a relative argument is resolved against the current directory, and an
    /// unnormalised <c>root/../other/x</c> is correctly outside.</para>
    ///
    /// <para><b>A path the platform rejects is not under the root, and that is a contract, not an
    /// accident.</b> A path holding a NUL makes <see cref="Path.GetRelativePath(string, string)"/> throw
    /// <see cref="ArgumentException"/>; this answers <see langword="false"/> instead. The copies this
    /// replaced let that exception escape (<c>RoslynResolver.RelativeToRepository</c>,
    /// <c>SourceOwnershipMap.Relativize</c>), so for them a NUL-bearing path now yields
    /// <see langword="null"/> (not owned) rather than a throw -- the direction every other refusal in this
    /// producer already takes for a path MSBuild printed.</para>
    ///
    /// <para><b>Lexical.</b> Nothing here follows a link; see <see cref="HasLinkAncestor"/> for that, and
    /// <see cref="IsInside"/> for how its question differs.</para>
    /// </summary>
    internal static bool TryGetPathUnderRoot(string root, string path, out string relative)
    {
        try
        {
            relative = Path.GetRelativePath(root, path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            relative = string.Empty;
            return false;
        }

        if (Path.IsPathRooted(relative) || FirstSegment(relative) == "..")
        {
            relative = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> itself or lies under it; see <see cref="TryGetPathUnderRoot"/>.</summary>
    internal static bool IsAtOrUnderRoot(string root, string path) => TryGetPathUnderRoot(root, path, out _);

    /// <summary>
    /// Whether <paramref name="path"/> is itself a link, or any of its first <paramref name="levels"/>
    /// ancestor directories is -- counting up from its containing directory, and never further.
    ///
    /// <para><b>The bound is a count, never a string to stop at, and that is the whole point.</b> A walk
    /// that stops only on meeting a root string runs to the filesystem root for any path that never
    /// meets it -- a <c>Compile</c> item from outside the repository
    /// (<c>&lt;Compile Include="..\..\Shared\X.cs"/&gt;</c>, ordinary in real solutions), or a root spelled
    /// in a different case -- and on macOS or Linux, where a shared tree commonly sits under a symlinked
    /// ancestor (<c>/tmp</c> -&gt; <c>/private/tmp</c>), that dropped every such item. That bug shipped
    /// once in <c>CompilationFactory</c>; counting makes over-walking structurally impossible. A link at
    /// or above the repository root is the operator's own checkout layout, not something the scanned
    /// repository chose, so the caller's count stops below it.</para>
    ///
    /// <para><b>Each level fails closed, which <see cref="IsReparsePoint"/> alone does not.</b> Its catch
    /// suggests otherwise, but it is never reached by a permission denial -- measured in E11 fix round 1:
    /// on Linux (uid 1000) with <c>chmod 000</c> on an ancestor, and on Windows with an inheritable
    /// deny-read ACE on one, <c>DirectoryInfo.LinkTarget</c> and <c>FileInfo.LinkTarget</c> both answer
    /// <see langword="null"/> without throwing (the Unix readlink wrapper maps EACCES to "not a link").
    /// So each level is first classified by <see cref="File.GetAttributes(string)"/>, which on both
    /// platforms does throw <see cref="UnauthorizedAccessException"/> on exactly those levels (measured).
    /// The rule mirrors the core library's strict guard: not found is not a link, and an access or any
    /// other I/O error is treated as one. See <see cref="IsLinkOrUninspectable"/> for the per-level
    /// probe.</para>
    ///
    /// <para><b>That refuses some files that ARE readable, deliberately -- and one such shape was a
    /// containment escape.</b> On Windows, a level whose own read-attributes right is denied, under a
    /// parent that denies listing, cannot have its attributes read even though a file beneath it still
    /// opens by path (traverse bypass). Measured (E11 fix round 2): a junction <c>repo\p\jra</c> pointing
    /// OUTSIDE the repository, carrying a deny <c>(RA)</c> ACE, under <c>repo\p</c> denying <c>(RD)</c>.
    /// <c>LinkTarget</c> on it answers <see langword="null"/>, so before this probe the walk saw no link:
    /// at <c>da6225d</c> and at E11 round 1 (<c>f4f8250</c>), <c>TreeSitterExtractor</c> extracted the
    /// outside file, <c>CompilationFactory</c> parsed it into the compilation, and a key file there was
    /// handed to the compilation as its strong-name key. That shape and a harmless one (a plain directory
    /// with the same two ACEs, whose readable file the E11 re-review measured going from
    /// <c>Extracted</c> to <c>SkippedSymlink</c>) look identical to every probe available here, so both
    /// are refused. What is
    /// NOT refused: a Windows deny-list ACE alone, or a POSIX directory with search but no read
    /// permission, which leave the attributes readable -- measured. POSIX has no traverse bypass, so no
    /// POSIX shape reads a file whose ancestor cannot be stat'ed. Pinned by
    /// <c>BundlePathsTests.A_junction_to_outside_whose_attributes_are_denied_under_an_unlistable_parent_is_never_read_windows</c>
    /// and the <c>HasLinkAncestor_does_not_refuse_*</c> twins.</para>
    ///
    /// <para>The walk only ever inspects the path string it was given, never a link's target, so a link
    /// loop cannot make it run longer than <paramref name="levels"/> + 1 levels.</para>
    /// </summary>
    /// <param name="path">The file (or directory) whose own link status and ancestry are tested.</param>
    /// <param name="levels">How many ancestor directories to test; <c>0</c> tests only <paramref name="path"/> itself.</param>
    internal static bool HasLinkAncestor(string path, int levels)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(levels);

        if (IsLinkOrUninspectable(path))
        {
            return true;
        }

        var directory = Path.GetDirectoryName(path);
        for (var i = 0; i < levels && !string.IsNullOrEmpty(directory); i++)
        {
            if (IsLinkOrUninspectable(directory))
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    /// <summary>
    /// One level of <see cref="HasLinkAncestor"/>: <see langword="true"/> when <paramref name="path"/> is
    /// a link by <see cref="IsReparsePoint"/>'s definition, or when its own metadata cannot be read (an
    /// access denial, any other I/O error, a path the platform rejects).
    ///
    /// <para><b>One metadata probe classifies the level; <see cref="IsReparsePoint"/> still decides what
    /// a link is.</b> <see cref="File.GetAttributes(string)"/> reports a path's OWN attributes without
    /// following it, and every link carries <see cref="FileAttributes.ReparsePoint"/> there -- measured in
    /// E11 fix round 1: on Windows a junction and a dangling junction; on Linux a directory symlink, a
    /// dangling one, a file symlink, a dangling file symlink and a self-referencing loop, all reported
    /// <c>ReparsePoint</c> without throwing. So a level whose attributes lack it is not a link and needs
    /// no further probe, and a level that has it (which also covers a reparse point that is not a link)
    /// is handed to <see cref="IsReparsePoint"/>. That is also what keeps the cost down. Measured
    /// (a 12-level link-free path, 5000 calls x 6 rounds, Release), per file against the tree-sitter walk
    /// this replaced: probing every level with <see cref="IsReparsePoint"/> alone, as E11 round 1 did,
    /// cost x1.92 on Windows and x1.88 on Linux; adding a separate attribute read to that cost x3.00 and
    /// x3.13; this form costs x1.02 and x1.63. A level the probe reports as not found is still asked of
    /// <see cref="IsReparsePoint"/> rather than assumed plain.</para>
    /// </summary>
    private static bool IsLinkOrUninspectable(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return IsReparsePoint(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException)
        {
            return true;
        }

        return (attributes & FileAttributes.ReparsePoint) != 0 && IsReparsePoint(path);
    }

    /// <summary>
    /// <see cref="HasLinkAncestor"/> bounded by <paramref name="root"/>: tests <paramref name="path"/>
    /// itself and every directory between it and <paramref name="root"/>, excluding the root. The count is
    /// the number of directory segments in <paramref name="path"/>'s <see cref="TryGetPathUnderRoot"/>
    /// answer; a path not under <paramref name="root"/> at all gets a count of <c>0</c>, so only the path
    /// itself is tested -- there is no bound to walk within, and walking anyway is the bug
    /// <see cref="HasLinkAncestor"/> describes. A <see langword="null"/> <paramref name="root"/> means the
    /// caller has no root to bound by (<c>SourceFileGate.Unbounded</c>), and likewise tests only the path
    /// itself.
    /// </summary>
    internal static bool HasLinkAncestorUnderRoot(string? root, string path) =>
        HasLinkAncestor(path, root is not null && TryGetPathUnderRoot(root, path, out var relative) ? DirectorySegments(relative) : 0);

    /// <summary>How many directory segments precede the last segment of a relative path (<c>a/b/x.cs</c> is 2, <c>x.cs</c> and <c>.</c> are 0).</summary>
    private static int DirectorySegments(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length - 1;

    private static string FirstSegment(string relative)
    {
        var end = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return end < 0 ? relative : relative[..end];
    }
}
