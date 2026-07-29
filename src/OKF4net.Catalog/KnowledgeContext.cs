// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// A structured knowledge-search result: passages with full provenance, plus
/// diagnostics -- deliberately never a bare string, so a caller can
/// distinguish "no results" from "a source failed" from "no source is
/// enabled" without parsing text.
/// </summary>
/// <param name="Query">The query this result answers.</param>
/// <param name="CatalogGeneration">
/// The <see cref="KnowledgeCatalogSnapshot.Generation"/> the search ran
/// against, so a caller can tell whether a result reflects the latest
/// reload.
/// </param>
/// <param name="Passages">
/// The matching passages, each carrying its originating
/// <see cref="KnowledgePassage.SourceId"/>. Their ORDER is defined by the
/// <see cref="IKnowledgeResolver"/> that produced this result, not by this
/// type: see <see cref="KnowledgeResolverStrategy"/> for the available
/// orderings and each resolver's own documentation for its exact guarantee.
/// </param>
/// <param name="Diagnostics">
/// Any non-fatal conditions encountered while producing <see cref="Passages"/>
/// (see <see cref="KnowledgeDiagnosticCode"/>); empty when nothing noteworthy
/// occurred.
/// </param>
public sealed record KnowledgeContext(
    KnowledgeQuery Query, long CatalogGeneration,
    IReadOnlyList<KnowledgePassage> Passages, IReadOnlyList<KnowledgeDiagnostic> Diagnostics);
