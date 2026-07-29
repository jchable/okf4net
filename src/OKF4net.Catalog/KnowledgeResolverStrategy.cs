// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Which ranking algorithm an <see cref="IKnowledgeResolver"/> search uses.
/// Selected per host (see <c>KnowledgeOptions.DefaultResolverStrategy</c> in
/// <c>OKF4net.Catalog.Hosting</c>) or per call
/// (<see cref="KnowledgeQuery.ResolverStrategy"/>, which overrides the host
/// default); <see cref="KnowledgeResolverRouter"/> is what dispatches on it.
/// </summary>
public enum KnowledgeResolverStrategy
{
    /// <summary>
    /// Concatenate each enabled source's own descending-score results,
    /// source by source, in descending <see cref="KnowledgeCatalogSource.Priority"/>
    /// then ascending ordinal <see cref="KnowledgeCatalogSource.Id"/> order --
    /// no cross-source fusion, deduplication, or merged ranking. The
    /// original (and default) behaviour; see
    /// <see cref="GroupedKnowledgeResolver"/>.
    /// </summary>
    GroupedBySource,

    /// <summary>
    /// Merge every source's results into one list ranked by descending
    /// <see cref="KnowledgePassage.Score"/> across all sources, with
    /// <see cref="KnowledgeCatalogSource.Priority"/> as a tie-break only.
    /// See <see cref="MergedKnowledgeResolver"/>.
    /// </summary>
    Merged,

    /// <summary>
    /// Merge every source's results into one list ranked by descending
    /// <see cref="KnowledgeCatalogSource.Priority"/> FIRST, with
    /// <see cref="KnowledgePassage.Score"/> ordering only within a single
    /// priority tier -- so a higher-priority source's passage never falls
    /// behind a lower-priority one regardless of match strength. See
    /// <see cref="PriorityWeightedKnowledgeResolver"/>.
    /// </summary>
    PriorityWeighted,
}
