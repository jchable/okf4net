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
    /// A specific source could not be searched (its resolved directory could
    /// not be re-validated, or loading its bundle failed) and was skipped;
    /// other sources are still searched.
    /// </summary>
    SourceUnavailable,

    /// <summary>Every source that was actually searched returned zero passages for the query.</summary>
    NoMatches,
}
