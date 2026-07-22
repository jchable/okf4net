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
}
