// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// How every resolver opens a knowledge source of a snapshot (spec §9, "Catalog : qui crée
/// et conserve les handles"): from the handle the catalog keeps for it, so that the image
/// survives from one request to the next and is refreshed under the source's policy; or,
/// for a snapshot that carries no handle for the source (one the parser or a test built by
/// hand), by resolving its path at this request and loading at each search, as every
/// resolver did before the seam.
/// </summary>
internal static class CatalogSourceOpener
{
    /// <summary>
    /// Opens <paramref name="source"/>. On success <paramref name="identity"/> is what two
    /// sources over one storage share — the provider's <see cref="Providers.IBundleProvider.Id"/>
    /// (the canonical root for the filesystem), or the resolved directory for a source
    /// without a handle — and <paramref name="location"/> is the displayable location; on
    /// failure <paramref name="diagnostic"/> says why the source is unavailable.
    /// </summary>
    internal static bool TryOpen(
        IKnowledgeCatalog catalog,
        KnowledgeCatalogSnapshot snapshot,
        KnowledgeCatalogSource source,
        out OkfBundleKnowledgeSource? bundleSource,
        out string? identity,
        out string? location,
        out KnowledgeDiagnostic? diagnostic)
    {
        if (snapshot.Handles.TryGetValue(source.Id, out var handle))
        {
            bundleSource = new OkfBundleKnowledgeSource(source.Id, handle);
            identity = handle.Provider.Id;
            location = handle.Location;
            diagnostic = null;
            return true;
        }

        if (!CatalogPathResolver.TryResolve(catalog.CatalogRoot, snapshot.ManifestDirectory, source.Path, out var directory, out var pathDiagnostic))
        {
            bundleSource = null;
            identity = null;
            location = null;
            diagnostic = new KnowledgeDiagnostic(
                KnowledgeDiagnosticCode.SourceUnavailable,
                source.Id,
                $"Source '{source.Id}' path could not be re-resolved: {pathDiagnostic!.Message}");
            return false;
        }

        bundleSource = new OkfBundleKnowledgeSource(source.Id, directory!);
        identity = directory;
        location = directory;
        diagnostic = null;
        return true;
    }
}
