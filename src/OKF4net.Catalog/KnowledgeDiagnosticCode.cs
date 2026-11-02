// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Enumerates every non-fatal condition a knowledge search
/// (<see cref="IKnowledgeSource.SearchAsync"/> or
/// <see cref="IKnowledgeResolver.SearchAsync"/>) can report as data rather
/// than by throwing.
/// </summary>
public enum KnowledgeDiagnosticCode
{
    /// <summary>No source in the catalog snapshot is enabled, so no search could run at all.</summary>
    NoEnabledSources,

    /// <summary>
    /// A specific source could not be searched and was skipped: its first load
    /// failed (a source whose bundle never loaded has no image to serve), or,
    /// for a snapshot that carries no handle for it, its path could not be
    /// re-resolved at this request; other sources are still searched. A source
    /// that loaded once and whose later reload fails is not unavailable: it is
    /// served from its last good image and reported as <see cref="SourceStale"/>.
    /// </summary>
    SourceUnavailable,

    /// <summary>Every source that was actually searched returned zero passages for the query.</summary>
    NoMatches,

    /// <summary>
    /// A source's storage is the same as another, already-opened source's (a
    /// fusing resolver's dedup-before-fan-out step, keyed on the provider's
    /// <see cref="Providers.IBundleProvider.Id"/>: the canonical root for the
    /// filesystem provider, the resolved directory for a source without a
    /// handle): this source was skipped so the shared bundle is not searched
    /// and scored twice. The surviving source is named in the message.
    /// </summary>
    DuplicateDirectory,

    /// <summary>
    /// A source was searched, and its passages are returned, but from its last
    /// good image: the reload its handle attempted since failed (spec §5.2 keeps
    /// the previous image served rather than losing the source). The message
    /// names the failure by its exception type only; the failure itself stays on
    /// <see cref="Providers.BundleHandle.LastLoadError"/>. Appended after the
    /// shipped members, so their numeric values are unchanged.
    /// </summary>
    SourceStale,
}
