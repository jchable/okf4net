// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Catalog;

/// <summary>
/// Resolves and safety-checks a source's <c>path</c> against the canonical catalog root.
/// </summary>
/// <remarks>
/// A <c>catalog.json</c> manifest's <c>sources[].path</c> is a relative path (OKF spec
/// §5.1) resolved against the directory the manifest itself lives in. Because a manifest
/// is data -- potentially edited by a less-trusted party than whoever configured the
/// catalog root -- a source path must never be allowed to expand the catalog's readable
/// surface beyond that root, whether via an absolute path, <c>..</c> traversal, or a
/// reparse point (symlink/junction/mount point) planted somewhere along the way that would
/// make the OS silently follow the link out of the root the moment anything actually
/// touches disk. This mirrors the containment convention <c>OkfBundleTools.IsWithinBundleRoot</c>
/// uses for bundle roots, and reuses the same shared <see cref="ReparsePoints.IsWithin"/>
/// and <see cref="ReparsePoints.HasReparsePointAncestor"/> core helpers those use for
/// containment and reparse-point-ancestor detection (via <c>OKF4net</c>'s
/// <c>InternalsVisibleTo</c> grant to this assembly) rather than duplicating a second,
/// platform-specific implementation.
/// </remarks>
public static class CatalogPathResolver
{
    /// <summary>
    /// Resolves <paramref name="sourcePath"/> relative to <paramref name="manifestDirectory"/>,
    /// canonicalizes it, and confirms it stays at/below <paramref name="catalogRoot"/> with
    /// no reparse-point ancestor and no reparse-point target. Returns the resolved absolute
    /// directory, or a diagnostic explaining the rejection. Never throws.
    /// </summary>
    /// <param name="catalogRoot">The catalog's configured root directory.</param>
    /// <param name="manifestDirectory">
    /// The directory the <c>catalog.json</c> manifest lives in; <paramref name="sourcePath"/>
    /// is resolved relative to this directory.
    /// </param>
    /// <param name="sourcePath">
    /// The source's <c>path</c> value from the manifest, expected to be relative (OKF spec §5.1).
    /// </param>
    /// <param name="resolvedDirectory">
    /// On success, the resolved absolute directory path; <see langword="null"/> otherwise.
    /// </param>
    /// <param name="diagnostic">
    /// On failure, the specific reject reason; <see langword="null"/> otherwise.
    /// </param>
    /// <returns><see langword="true"/> if the source path is safe to use; otherwise <see langword="false"/>.</returns>
    public static bool TryResolve(
        string catalogRoot,
        string manifestDirectory,
        string sourcePath,
        out string? resolvedDirectory,
        out CatalogDiagnostic? diagnostic)
    {
        resolvedDirectory = null;

        if (string.IsNullOrEmpty(sourcePath))
        {
            diagnostic = new CatalogDiagnostic(CatalogDiagnosticCode.EmptyPath, "Source 'path' is empty.");
            return false;
        }

        if (Path.IsPathRooted(sourcePath))
        {
            diagnostic = new CatalogDiagnostic(
                CatalogDiagnosticCode.AbsolutePath,
                $"Source path '{sourcePath}' must be relative to the manifest directory, not absolute.");
            return false;
        }

        string fullRoot;
        string resolved;
        try
        {
            fullRoot = Path.GetFullPath(catalogRoot);
            resolved = Path.GetFullPath(Path.Combine(manifestDirectory, sourcePath));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostic = new CatalogDiagnostic(
                CatalogDiagnosticCode.InvalidPath,
                $"Source path '{sourcePath}' could not be resolved: {e.Message}");
            return false;
        }

        if (!IsWithinRoot(fullRoot, resolved))
        {
            diagnostic = new CatalogDiagnostic(
                CatalogDiagnosticCode.OutsideRoot,
                $"Source path '{sourcePath}' resolves to '{resolved}', which is outside the catalog root '{fullRoot}'.");
            return false;
        }

        if (!Directory.Exists(resolved))
        {
            diagnostic = new CatalogDiagnostic(
                CatalogDiagnosticCode.TargetNotFound,
                $"Source path '{sourcePath}' resolves to '{resolved}', which is not an existing directory.");
            return false;
        }

        if (HasReparsePointInPath(fullRoot, resolved))
        {
            diagnostic = new CatalogDiagnostic(
                CatalogDiagnosticCode.ReparsePointInPath,
                $"Source path '{sourcePath}' resolves through a reparse point (symlink/junction) at or above '{resolved}'.");
            return false;
        }

        diagnostic = null;
        resolvedDirectory = resolved;
        return true;
    }

    /// <summary>
    /// <c>true</c> if <paramref name="candidate"/> is <paramref name="root"/> itself or a
    /// descendant of it, comparing resolved absolute paths case-insensitively -- the same
    /// canonicalize-then-prefix-compare convention <c>OkfBundleTools.IsWithinBundleRoot</c>
    /// already uses for bundle-root containment. Both <paramref name="root"/> and
    /// <paramref name="candidate"/> are expected to already be the result of
    /// <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    private static bool IsWithinRoot(string root, string candidate) =>
        ReparsePoints.IsWithin(root, candidate, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> itself, or any directory strictly between it
    /// and <paramref name="root"/>, is a filesystem reparse point -- checked via
    /// <see cref="ReparsePoints.IsReparsePoint"/>, which reports the entry's own type
    /// (lstat-like) without following it.
    ///
    /// <paramref name="root"/> itself is deliberately exempt from this walk: a
    /// symlinked/mounted catalog root is a legitimate, explicit operator choice (symlinked
    /// project directories, container/WSL bind mounts, macOS's <c>/var</c>) -- exactly the
    /// same reasoning that keeps <c>IndexGenerator</c>'s and <c>OkfBundleTools</c>' own
    /// <c>HasReparsePointAncestor</c> helpers from ever inspecting the root they walk up to.
    /// Both parameters are expected to already be the result of
    /// <see cref="Path.GetFullPath(string)"/>, so the loop's exit test can use an ordinal
    /// string comparison the same way those helpers do.
    /// </summary>
    private static bool HasReparsePointInPath(string root, string path) =>
        ReparsePoints.HasReparsePointAncestor(root, path, StringComparison.OrdinalIgnoreCase);
}
