// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// A live view over a <c>catalog.json</c> manifest: the current validated,
/// immutable snapshot, plus an explicit, errors-as-data way to pick up
/// changes without ever serving a partial or corrupted catalog.
/// </summary>
public interface IKnowledgeCatalog
{
    /// <summary>
    /// The current validated snapshot. Never <see langword="null"/> and
    /// never partially applied -- a snapshot only ever changes as a whole,
    /// atomic replacement.
    /// </summary>
    KnowledgeCatalogSnapshot Current { get; }

    /// <summary>
    /// Diagnostics from the most recent reload attempt; empty on success. A
    /// failed reload keeps <see cref="Current"/> as the last-known-good
    /// snapshot (its <see cref="KnowledgeCatalogSnapshot.Generation"/>
    /// unchanged) and records why here -- the errors-as-data surface for
    /// reload failures. (Construction-time failures throw instead; see
    /// <see cref="FileKnowledgeCatalog"/>.)
    /// </summary>
    IReadOnlyList<CatalogDiagnostic> LastReloadDiagnostics { get; }

    /// <summary>
    /// Re-reads and re-validates the manifest and, only on success,
    /// atomically publishes the result as the new <see cref="Current"/> with
    /// <see cref="KnowledgeCatalogSnapshot.Generation"/> incremented by one.
    /// Never throws: a malformed/invalid replacement leaves
    /// <see cref="Current"/> and its generation unchanged and populates
    /// <see cref="LastReloadDiagnostics"/> instead. Returns
    /// <see cref="Current"/> either way -- the new snapshot on success, the
    /// unchanged one on failure.
    /// </summary>
    ValueTask<KnowledgeCatalogSnapshot> ReloadAsync(CancellationToken cancellationToken = default);
}
