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

/// <summary>
/// The result of a scoped memory deletion (spec §1, §9): one <see cref="MemoryTierDeletion"/>
/// per tier whose scope held something or was refused, in read order, and the error text
/// when any tier failed. A tier whose scope held no key is absent from <see cref="Tiers"/>.
/// </summary>
/// <param name="Tiers">What each tier's deletion did, with the keys deleted and those that failed.</param>
/// <param name="Error">The failures of every tier joined by <c>; </c>, or <see langword="null"/> when nothing failed.</param>
public sealed record MemoryDeleteResult(IReadOnlyList<MemoryTierDeletion> Tiers, string? Error);

/// <summary>
/// What deleting one tier's scope did: every object under the scope's prefix is deleted on
/// its own, so the <see cref="OKF4net.Providers.BundleWriteReport"/> says exactly which keys
/// were deleted and which failed, with why; a scope the provider refused (a reparse point
/// on its path) reports one failure under the scope's prefix and nothing deleted.
/// </summary>
/// <param name="Tier">The tier.</param>
/// <param name="Report">The keys deleted, in order, and the failures.</param>
public sealed record MemoryTierDeletion(MemoryTier Tier, OKF4net.Providers.BundleWriteReport Report);

/// <summary>One stored memory concept, for enumeration/audit.</summary>
public sealed record MemoryConcept(MemoryTier Tier, string ConceptId, string? Title);
