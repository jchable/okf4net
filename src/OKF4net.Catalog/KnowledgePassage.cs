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
/// <see cref="GroupedKnowledgeResolver"/> search are attributed and grouped.
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
/// <param name="Lifecycle">
/// The matching concept's full lifecycle projection (<c>Frontmatter.Lifecycle</c>,
/// §5.4/§5.5): status, raw and parsed <c>stale_after</c>, and derived staleness.
/// Carried whole -- rather than split into flattened status/stale fields a
/// consumer would have to reassemble -- so <see cref="IKnowledgeResolver"/>
/// stale filtering (<see cref="StalePolicy.Admits"/>) reads it directly, and a
/// malformed <c>stale_after</c> is still surfaced verbatim via
/// <c>Lifecycle.StaleAfterRaw</c> rather than silently lost. Defaults to
/// <c>default</c> (no <c>stale_after</c>, so admitted under every policy).
/// </param>
public sealed record KnowledgePassage(
    string SourceId,
    string ConceptId,
    string? Title,
    string Excerpt,
    int Score,
    string BundleRelativePath,
    TrustTier TrustTier = TrustTier.Unverified,
    Lifecycle Lifecycle = default);
