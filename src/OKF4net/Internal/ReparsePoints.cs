// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// Detects filesystem reparse points (symlinks, junctions, mount points),
/// mirroring the lstat-based semantics of Rust's <c>DirEntry::file_type()</c>
/// (bundle.rs:207-222, index.rs:223-234).
///
/// Rust's directory walks call <c>entry.file_type()</c>, which reports the
/// type of the directory entry ITSELF without following a symlink -- unlike
/// <c>Path::is_dir()</c>/<c>Path::is_file()</c> (and .NET's
/// <see cref="Directory.Exists(string)"/>/<see cref="File.Exists(string)"/>),
/// which resolve through any symlink to the type of its target. For a
/// symlink entry, <c>file_type().is_dir()</c> and <c>file_type().is_file()</c>
/// are BOTH <c>false</c> -- it matches neither match arm in either Rust
/// <c>collect_markdown</c>, so the entry is skipped (bundle.rs) or excluded
/// from directory recursion (index.rs).
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
            // real (index.rs/bundle.rs skip it via lstat's is_dir()==false).
            // FileSystemInfo.LinkTarget reads the entry itself and is non-null
            // exactly when the entry is a link, on every platform.
            return new DirectoryInfo(path).LinkTarget is not null
                || new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

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
    /// <paramref name="root"/>. Callers differ on this (ordinal vs.
    /// ordinal-ignore-case) depending on their own root-equality convention;
    /// this parameter preserves each call site's original behavior rather
    /// than silently unifying it.
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
    /// silently defeating this method's entire purpose. Callers whose input
    /// is already validated/trusted, or who only ever run where the
    /// filesystem itself is case-insensitive, may still choose
    /// <c>OrdinalIgnoreCase</c> to match that filesystem's own equality
    /// semantics.
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
}
