// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// One deterministic memory-capture entry to persist: the per-day concept leaf
/// name, the frontmatter used only if the concept must be created, and the
/// already-formatted (neutralized) markdown section to append.
/// </summary>
/// <param name="ConceptName">The concept's leaf name (a single concept-id segment), e.g. <c>2026-07-27</c>.</param>
/// <param name="FrontmatterYamlIfCreating">Producer frontmatter applied only when the concept does not yet exist.</param>
/// <param name="SectionMarkdown">The formatted section body appended on every capture.</param>
public sealed record MemoryEntry(string ConceptName, string FrontmatterYamlIfCreating, string SectionMarkdown);

/// <summary>The result of a scoped memory read: matching passages plus errors-as-data diagnostics.</summary>
public sealed record MemoryReadResult(IReadOnlyList<KnowledgePassage> Passages, IReadOnlyList<KnowledgeDiagnostic> Diagnostics);

/// <summary>The result of a scoped memory write: whether it was written, and the error text if not.</summary>
public sealed record MemoryWriteResult(bool Written, string? Error);

/// <summary>The result of a scoped memory deletion: how many tier subtrees were removed, and the error text if any.</summary>
public sealed record MemoryDeleteResult(int TiersDeleted, string? Error);

/// <summary>One stored memory concept, for enumeration/audit.</summary>
public sealed record MemoryConcept(MemoryTier Tier, string ConceptId, string? Title);
