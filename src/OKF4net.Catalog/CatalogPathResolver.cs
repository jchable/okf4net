// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Catalog;

/// <summary>
/// Resolves and safety-checks a source's <c>path</c> against the canonical catalog root.
/// </summary>
/// <remarks>
/// A <c>catalog.json</c> manifest's <c>sources[].path</c> is a relative path (an
/// OKF4net-specific manifest field, not part of the OKF spec) resolved against the
/// directory the manifest itself lives in. Because a manifest
/// is data -- potentially edited by a less-trusted party than whoever configured the
/// catalog root -- a source path must never be allowed to expand the catalog's readable
/// surface beyond that root, whether via an absolute path, <c>..</c> traversal, or a
/// reparse point (symlink/junction/mount point) planted somewhere along the way that would
/// make the OS silently follow the link out of the root the moment anything actually
/// touches disk. This mirrors the containment convention <see cref="ReparsePoints.IsWithinBundleRoot"/>
/// uses for bundle roots, and reuses the same shared <see cref="ReparsePoints.IsWithin"/>
/// and <see cref="ReparsePoints.HasReparsePointAncestor(string, string, System.StringComparison)"/>
/// core helpers those use for
/// containment and reparse-point-ancestor detection (via <c>OKF4net</c>'s
/// <c>InternalsVisibleTo</c> grant to this assembly) rather than duplicating a second,
/// platform-specific implementation.
/// </remarks>
public static class CatalogPathResolver
{
    /// <summary>
    /// The comparison used for containment (<see cref="IsWithinRoot"/>) and
    /// the reparse-point-ancestor walk's root-stop test
    /// (<see cref="HasReparsePointInPath"/>).
    /// </summary>
    /// <remarks>
    /// A <c>catalog.json</c> source path is LESS-TRUSTED input (see this
    /// type's own <see cref="CatalogPathResolver"/> remarks), and <c>..</c>
    /// is legitimately allowed in it -- containment is therefore the primary
    /// defense against it escaping the catalog root, not a secondary check.
    /// The configured root's exact path spelling is the authority on every
    /// platform: filesystem case sensitivity is a volume property, so an OS
    /// check cannot safely choose a looser comparison. An ordinal comparison
    /// rejects a path that leaves the configured spelling and re-enters it
    /// through a case variant, which could otherwise escape on a
    /// case-sensitive APFS volume. It also prevents the reparse-point walk
    /// from stopping early at such a case-variant directory.
    /// <para>
    /// This security comparison is deliberately private and distinct from
    /// <see cref="PathComparison"/>, which remains the resolver's existing
    /// directory-deduplication convention rather than a containment boundary.
    /// </para>
    /// </remarks>
    private const StringComparison ContainmentComparison = StringComparison.Ordinal;

    /// <summary>
    /// The existing comparison used by <see cref="FusedResolverEngine"/> to
    /// deduplicate resolved source-directory strings. It is intentionally not
    /// used for containment or reparse-point checks.
    /// </summary>
    internal static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
    /// The source's <c>path</c> value from the manifest, expected to be relative (an
    /// OKF4net-specific manifest field, not part of the OKF spec).
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
            // CanonicalizeRoot matters here: an operator-configured catalogRoot
            // with a trailing separator would otherwise defeat the
            // HasReparsePointInPath ancestor walk below -- see its remarks.
            fullRoot = ReparsePoints.CanonicalizeRoot(catalogRoot);
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
    /// descendant of it, comparing resolved absolute paths with
    /// <see cref="ContainmentComparison"/>. A strict ordinal comparison is
    /// required because the source path is less-trusted input.
    /// <paramref name="root"/> is expected to already be the result of
    /// <see cref="ReparsePoints.CanonicalizeRoot"/> (see its remarks for why a bare
    /// <see cref="Path.GetFullPath(string)"/> is not enough here); <paramref name="candidate"/>
    /// only needs <see cref="Path.GetFullPath(string)"/>.
    /// </summary>
    private static bool IsWithinRoot(string root, string candidate) =>
        ReparsePoints.IsWithin(root, candidate, ContainmentComparison);

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> itself, or any directory strictly between it
    /// and <paramref name="root"/>, is a filesystem reparse point -- checked via
    /// <see cref="ReparsePoints.IsReparsePoint"/>, which reports the entry's own type
    /// (lstat-like) without following it.
    ///
    /// <paramref name="root"/> itself is deliberately exempt from this walk: a
    /// symlinked/mounted catalog root is a legitimate, explicit operator choice (symlinked
    /// project directories, container/WSL bind mounts, macOS's <c>/var</c>) -- exactly the
    /// same reasoning that keeps <c>IndexGenerator</c>'s own <c>HasReparsePointAncestor</c>
    /// wrapper, and the shared <see cref="ReparsePoints.HasReparsePointAncestor(string, string)"/>
    /// convenience overload other callers use, from ever inspecting the root they walk up to.
    /// <paramref name="root"/> is expected to already be the result of
    /// <see cref="ReparsePoints.CanonicalizeRoot"/> (see its remarks for why a bare
    /// <see cref="Path.GetFullPath(string)"/> is not enough here); <paramref name="path"/>
    /// only needs <see cref="Path.GetFullPath(string)"/>. The walk's root-stop test uses
    /// <see cref="ContainmentComparison"/> so a case-variant of
    /// <paramref name="root"/> cannot stop the walk early and skip inspection
    /// of a planted reparse point.
    /// </summary>
    private static bool HasReparsePointInPath(string root, string path) =>
        ReparsePoints.HasReparsePointAncestor(root, path, ContainmentComparison);
}
