// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// One search hit, with enough provenance that a future
/// <see cref="IKnowledgeResolver"/> -&gt; AI context-provider adapter can
/// render an <c>&lt;okf-context&gt;</c> block without a contract change
/// (convergence with the OKF spec §4.3 context-block shape).
/// </summary>
/// <param name="SourceId">
/// The <see cref="KnowledgeCatalogSource.Id"/> of the catalog source this
/// passage came from -- how passages from a multi-source
/// <see cref="DefaultKnowledgeResolver"/> search are attributed and grouped.
/// </param>
/// <param name="ConceptId">The matching concept's id (<c>ConceptId.ToString()</c>).</param>
/// <param name="Title">The matching concept's frontmatter title, if any.</param>
/// <param name="Excerpt">
/// The shared core excerpt (<c>ConceptSearch.Excerpt</c>): the first matching
/// body line, or the empty string if the core scorer found none.
/// </param>
/// <param name="Score">The core scorer's relevance score for this concept (higher is more relevant).</param>
/// <param name="BundleRelativePath">
/// The concept file's path relative to its bundle's root directory (the
/// absolute <c>Concept.Path</c> relativized against <c>Bundle.Root</c>).
/// </param>
/// <param name="TrustTier">
/// The matching concept's derived trust tier (<c>Frontmatter.TrustTier</c>,
/// §5.3) -- defaults to <see cref="OKF4net.TrustTier.Unverified"/> so
/// existing constructions predating this parameter keep their prior meaning.
/// </param>
/// <param name="Status">
/// The matching concept's lifecycle status (<c>Frontmatter.Lifecycle.Status</c>,
/// §5.4) -- defaults to <see cref="ConceptStatus.Stable"/>, the spec's own
/// default for an absent <c>status</c> field.
/// </param>
/// <param name="StaleAfter">
/// The matching concept's raw <c>stale_after</c> frontmatter value
/// (<c>Frontmatter.Lifecycle.StaleAfterRaw</c>), or <see langword="null"/> if
/// absent -- kept as the original string rather than a parsed date so a
/// malformed value can still be surfaced verbatim rather than silently lost.
/// </param>
public sealed record KnowledgePassage(
    string SourceId,
    string ConceptId,
    string? Title,
    string Excerpt,
    int Score,
    string BundleRelativePath,
    TrustTier TrustTier = TrustTier.Unverified,
    ConceptStatus Status = ConceptStatus.Stable,
    string? StaleAfter = null);
