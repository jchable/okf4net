// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// Detects filesystem reparse points (symlinks, junctions, mount points)
/// using lstat-based semantics: it reports the type of the directory entry
/// ITSELF without following a symlink.
///
/// This is the semantics the bundle and index directory walks need: unlike
/// <c>Path.is_dir()</c>/<c>is_file()</c> (and .NET's
/// <see cref="Directory.Exists(string)"/>/<see cref="File.Exists(string)"/>),
/// which resolve through any symlink to the type of its target, a
/// reparse-point entry is treated as neither a plain file nor a plain
/// directory, so it is skipped rather than traversed or collected.
///
/// On Windows, <see cref="File.GetAttributes(string)"/> reproduces this
/// (Win32 <c>GetFileAttributes</c> reports the entry's own
/// <see cref="FileAttributes.ReparsePoint"/> without following it). On Unix,
/// however, <c>File.GetAttributes</c> resolves THROUGH a symlink (stat, not
/// lstat), so a symlink to a directory reports as a plain directory with no
/// reparse flag; <see cref="IsReparsePoint"/> therefore falls back to
/// <see cref="FileSystemInfo.LinkTarget"/>, which reads the entry itself and
/// is non-null exactly for a link, on every platform.
/// </summary>
internal static class ReparsePoints
{
    /// <summary>
    /// <c>true</c> if <paramref name="path"/> is itself a reparse point
    /// (symlink, junction, ...), without following it. Returns <c>false</c>
    /// (rather than throwing) if the attributes cannot be read -- e.g. a
    /// dangling symlink whose attributes are nonetheless still queryable via
    /// <c>GetFileAttributes</c>/<c>lstat</c> on the link itself, or a path
    /// that vanished between enumeration and this check -- since such races
    /// mean there's no entry left to skip.
    /// </summary>
    internal static bool IsReparsePoint(string path)
    {
        try
        {
            // Fast path: on Windows a symlink/junction/mount point sets the
            // ReparsePoint attribute, and File.GetAttributes reports the entry
            // itself (Win32 GetFileAttributes semantics).
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            // lstat-correct cross-platform fallback. On Unix, File.GetAttributes
            // resolves THROUGH a symlink (stat, not lstat), so a symlink to a
            // directory reports as a plain Directory with no ReparsePoint flag --
            // which would let a symlinked subdirectory be walked/indexed as if
            // it were a real directory. FileSystemInfo.LinkTarget reads the
            // entry itself and is non-null exactly when the entry is a link,
            // on every platform.
            return new DirectoryInfo(path).LinkTarget is not null
                || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves <paramref name="root"/> to a full path with any trailing
    /// directory separator trimmed. <see cref="Path.GetFullPath(string)"/>
    /// alone preserves a trailing separator if the input has one -- e.g.
    /// <c>"/foo"</c> and <c>"/foo/"</c> survive it as distinct strings -- but
    /// every walk in this class stops via EXACT STRING EQUALITY against an
    /// ancestor produced by <see cref="Path.GetDirectoryName(string)"/>,
    /// which never carries a trailing separator. An untrimmed root therefore
    /// never matches, and the walk overshoots past the intended root into
    /// the real filesystem above it -- on macOS, for example, reaching the
    /// genuine <c>/var</c> symlink and rejecting an entirely valid write.
    /// Every caller that computes its own root for a reparse-point or
    /// containment check against this class must resolve it through here,
    /// not through a bare <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    internal static string CanonicalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> itself, or any directory strictly
    /// between it and <paramref name="root"/>, is a filesystem reparse point --
    /// checked via <see cref="IsReparsePoint"/>, which reports each entry's own
    /// type without following it. Both <paramref name="root"/> and
    /// <paramref name="path"/> are expected to already be resolved (typically
    /// via <see cref="Path.GetFullPath(string)"/>) by the caller; this method
    /// performs no canonicalization of its own.
    /// </summary>
    /// <param name="root">
    /// The walk's upper bound. Deliberately never inspected by this walk --
    /// the loop's exit test (<paramref name="rootComparison"/>) stops before
    /// <see cref="IsReparsePoint"/> is ever called on <paramref name="root"/>
    /// itself -- callers that need <paramref name="path"/> itself checked
    /// (e.g. an existing concept file that may itself be a planted symlink)
    /// must call <see cref="IsReparsePoint"/> on it separately.
    /// </param>
    /// <param name="path">The starting point of the upward walk.</param>
    /// <param name="rootComparison">
    /// The comparison used to detect that the walk has reached
    /// <paramref name="root"/>. Every current caller passes
    /// <see cref="StringComparison.Ordinal"/> uniformly -- the 2-arg
    /// <see cref="HasReparsePointAncestor(string, string)"/> overload,
    /// <c>IndexGenerator</c>'s private wrapper, <c>FileMemoryStore.PathComparison</c>,
    /// and <c>CatalogPathResolver</c>'s <c>ContainmentComparison</c> path all
    /// do. The parameter stays explicit rather than hardcoding
    /// <c>Ordinal</c> internally, keeping this comparison a visible,
    /// independently testable seam instead of an implicit assumption.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a reparse point was found; otherwise
    /// <see langword="false"/> (including if the walk runs past the
    /// filesystem root without ever reaching <paramref name="root"/>, which
    /// stops the walk rather than looping forever).
    /// </returns>
    internal static bool HasReparsePointAncestor(string root, string path, StringComparison rootComparison)
    {
        var current = path;

        while (!string.Equals(current, root, rootComparison))
        {
            if (IsReparsePoint(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                // Walked past the filesystem root without ever reaching
                // root -- callers already guard containment separately, but
                // stop here rather than loop forever.
                break;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> itself, or any directory
    /// strictly between it and <paramref name="bundleRoot"/>, is a
    /// filesystem reparse point (symlink, junction, mount point) -- resolves
    /// <paramref name="bundleRoot"/> via <see cref="CanonicalizeRoot"/> (see
    /// its remarks for why a bare <see cref="Path.GetFullPath(string)"/>
    /// is not enough here), then delegates to
    /// <see cref="HasReparsePointAncestor(string, string, StringComparison)"/>
    /// with <see cref="StringComparison.Ordinal"/>. Complements
    /// <see cref="IsWithinBundleRoot"/>: that check only compares resolved
    /// path STRINGS, so a junction that lexically resolves under
    /// <paramref name="bundleRoot"/> still passes it even though the OS
    /// follows the junction the moment something actually touches disk
    /// (<see cref="Directory.Exists(string)"/>,
    /// <see cref="File.ReadAllText(string)"/>, <see cref="File.WriteAllText(string, string)"/>)
    /// -- silently reading or writing outside the bundle. Walking every
    /// intermediate directory and rejecting on the first reparse point closes
    /// that gap. Never inspects <paramref name="path"/> itself -- a caller
    /// whose target could itself be a planted file symlink (not just an
    /// ancestor directory) must separately check <see cref="IsReparsePoint"/>
    /// on it.
    /// </summary>
    /// <remarks>
    /// <see cref="StringComparison.Ordinal"/> on every platform, not an
    /// OS-conditional choice -- case-sensitivity is a runtime property of the
    /// specific volume, not of the OS: APFS/HFS+ can be configured
    /// case-sensitive, and a volume mounted on Linux can be case-insensitive
    /// (FAT/exFAT, a case-folding network share). Every legitimate caller's
    /// <paramref name="path"/> is built via <see cref="Path.Combine(string, string)"/>
    /// from the same <paramref name="bundleRoot"/> passed to this method, so
    /// its prefix always keeps <paramref name="bundleRoot"/>'s exact casing --
    /// <c>Ordinal</c> costs nothing for legitimate input, and closes the same
    /// case-variant escape <see cref="IsWithinBundleRoot"/> closes.
    /// </remarks>
    internal static bool HasReparsePointAncestor(string bundleRoot, string path)
    {
        var fullRoot = CanonicalizeRoot(bundleRoot);
        var current = Path.GetFullPath(path);
        return HasReparsePointAncestor(fullRoot, current, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> is <paramref name="root"/>
    /// itself or a descendant of it, comparing the two path strings with
    /// <paramref name="comparison"/>. Both parameters are expected to already
    /// be resolved (typically via <see cref="Path.GetFullPath(string)"/>) by
    /// the caller; this method performs no canonicalization of its own.
    /// </summary>
    /// <remarks>
    /// <paramref name="comparison"/> is entirely the caller's choice, and that
    /// choice matters: callers whose <paramref name="path"/> can come from
    /// UNTRUSTED input (e.g. a manifest's relative source path, which may
    /// legitimately contain <c>..</c>) and who run on a case-SENSITIVE
    /// filesystem (Linux, the CI/container target) must pass
    /// <see cref="StringComparison.Ordinal"/> -- <c>OrdinalIgnoreCase</c>
    /// would treat a case-variant of <paramref name="root"/> (a genuinely
    /// different directory on such a filesystem) as contained within it,
    /// silently defeating this method's entire purpose. Every current caller
    /// of this method passes <see cref="StringComparison.Ordinal"/> --
    /// <see cref="IsWithinBundleRoot"/>, <c>Bundle.cs</c>, and
    /// <c>CatalogPathResolver</c>'s <c>ContainmentComparison</c> path all do.
    /// A future caller that instead chooses <c>OrdinalIgnoreCase</c> owes a
    /// documented safe-direction argument for why an over-approximation is
    /// the safer failure mode at that call site, the way
    /// <c>MemoryServiceCollectionExtensions.ThrowIfMemoryOverlapsKnowledge</c>
    /// documents its own deliberate choice of <c>OrdinalIgnoreCase</c>.
    /// </remarks>
    internal static bool IsWithin(string root, string path, StringComparison comparison)
    {
        if (string.Equals(root, path, comparison))
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, comparison);
    }

    /// <summary>
    /// <c>true</c> if <paramref name="candidate"/> is <paramref name="root"/>
    /// itself or a descendant of it, resolving <paramref name="root"/> via
    /// <see cref="CanonicalizeRoot"/> (so <paramref name="candidate"/> equal
    /// to <paramref name="root"/> itself still matches <see cref="IsWithin"/>'s
    /// exact-equality check even when <paramref name="root"/> has a trailing
    /// separator -- see <see cref="CanonicalizeRoot"/>'s remarks) and
    /// comparing with <see cref="StringComparison.Ordinal"/> on every
    /// platform. A lexical check alone: a junction/symlink among
    /// <paramref name="candidate"/>'s ancestors can still resolve here even
    /// though the OS would follow it to somewhere else entirely once actual
    /// I/O touches disk -- pair with <see cref="HasReparsePointAncestor(string, string)"/>
    /// for that.
    /// </summary>
    /// <remarks>
    /// Case-sensitivity is a runtime property of the specific volume, not of
    /// the OS: APFS/HFS+ can be configured case-sensitive, and a volume
    /// mounted on Linux can be case-insensitive (FAT/exFAT, a case-folding
    /// network share) -- an OS-conditional comparison leaves an escape open
    /// on exactly the combination it assumes cannot occur.
    /// <see cref="StringComparison.Ordinal"/> has no cost for legitimate
    /// input: every legitimate <paramref name="candidate"/> is built via
    /// <see cref="Path.Combine(string, string)"/> from the same
    /// <paramref name="root"/> passed to this method (a purely lexical
    /// operation that never re-cases from disk), so its prefix always keeps
    /// <paramref name="root"/>'s exact casing. Only a <c>..</c> climb
    /// re-entering a case-variant sibling of <paramref name="root"/> itself
    /// produces a mismatched prefix -- precisely the escape this method must
    /// reject.
    /// </remarks>
    internal static bool IsWithinBundleRoot(string root, string candidate)
    {
        var fullRoot = CanonicalizeRoot(root);
        var fullCandidate = Path.GetFullPath(candidate);
        return IsWithin(fullRoot, fullCandidate, StringComparison.Ordinal);
    }
}
