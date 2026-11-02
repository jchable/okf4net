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

    /// <summary>
    /// The <see cref="KnowledgeDiagnosticCode.SourceStale"/> diagnostic for a source that has
    /// just been searched, or <see langword="null"/> when it was served from a current image:
    /// once an image has loaded, the handle absorbs every later failure and keeps serving the
    /// last good one (spec §5.2), so the failure is read back from the handle here. The
    /// message names the failure by its exception type only (spec §7); a handle that is stale
    /// only because a reload is pending, with no failure recorded, reports nothing.
    /// </summary>
    internal static KnowledgeDiagnostic? StaleDiagnostic(OkfBundleKnowledgeSource source)
    {
        var handle = source.Handle;
        if (!handle.IsStale || handle.LastLoadError is not { } error)
        {
            return null;
        }

        return new KnowledgeDiagnostic(
            KnowledgeDiagnosticCode.SourceStale,
            source.Id,
            $"Source '{source.Id}' bundle at '{handle.Location}' is served from its last good image; the last reload failed ({error.GetType().Name}).");
    }
}
