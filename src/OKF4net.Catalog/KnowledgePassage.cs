// SPDX-License-Identifier: LGPL-3.0-or-later
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
public sealed record KnowledgePassage(
    string SourceId, string ConceptId, string? Title, string Excerpt, int Score, string BundleRelativePath);
