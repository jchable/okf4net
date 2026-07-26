// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// A structured knowledge-search result: passages grouped by source with
/// full provenance, plus diagnostics -- deliberately never a bare string, so
/// a caller can distinguish "no results" from "a source failed" from "no
/// source is enabled" without parsing text.
/// </summary>
/// <param name="Query">The query this result answers.</param>
/// <param name="CatalogGeneration">
/// The <see cref="KnowledgeCatalogSnapshot.Generation"/> the search ran
/// against, so a caller can tell whether a result reflects the latest
/// reload.
/// </param>
/// <param name="Passages">
/// The matching passages, concatenated **in source order** (descending
/// <see cref="KnowledgeCatalogSource.Priority"/> then ascending ordinal
/// <see cref="KnowledgeCatalogSource.Id"/>) and, within a source, in that
/// source's own descending-score order. There is deliberately no
/// cross-source fusion, deduplication, or merged ranking (V1 scope).
/// </param>
/// <param name="Diagnostics">
/// Any non-fatal conditions encountered while producing <see cref="Passages"/>
/// (see <see cref="KnowledgeDiagnosticCode"/>); empty when nothing noteworthy
/// occurred.
/// </param>
public sealed record KnowledgeContext(
    KnowledgeQuery Query, long CatalogGeneration,
    IReadOnlyList<KnowledgePassage> Passages, IReadOnlyList<KnowledgeDiagnostic> Diagnostics);
