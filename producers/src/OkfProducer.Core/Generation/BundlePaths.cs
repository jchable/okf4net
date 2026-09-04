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
            var full = Path.GetFullPath(bundleRoot);
            var info = new DirectoryInfo(full);
            if (info.LinkTarget is null)
            {
                return full;
            }

            return info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? Path.GetFullPath(target.FullName)
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

    /// <summary>Whether <paramref name="path"/> lies strictly under <paramref name="root"/>, comparing whole path components.</summary>
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
    /// <para>An unanswerable path is reported as a link. Every caller uses this to decide whether to
    /// walk INTO something: declining to walk costs coverage, and walking costs containment.</para>
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
}
