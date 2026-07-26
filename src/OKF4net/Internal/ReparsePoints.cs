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
/// from directory recursion (index.rs). Checking
/// <see cref="FileAttributes.ReparsePoint"/> via
/// <see cref="File.GetAttributes(string)"/> reproduces that: like Win32's
/// <c>GetFileAttributes</c> and POSIX's <c>lstat</c>, it reports the entry's
/// own attributes rather than following the link.
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
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
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
