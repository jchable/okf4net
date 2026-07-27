// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// A scoped read+write memory sink. READ unions the scope's applicable tiers
/// (most-specific first: session → user → tenant), scored via the shared core
/// <c>ConceptSearch</c>. WRITE targets exactly one tier. Deletion/enumeration
/// support RGPD/audit. <see cref="IKnowledgeResolver"/> stays read-only and
/// unchanged. Every operation is errors-as-data — none throws for a data
/// condition (unresolvable path, unreadable bundle, reparse-point subtree);
/// those are reported via result fields/diagnostics.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Reads the scope's applicable-tier memory, scored against <paramref name="query"/>, most-specific first.</summary>
    ValueTask<MemoryReadResult> ReadAsync(KnowledgeAccessScope scope, KnowledgeQuery query, CancellationToken ct = default);

    /// <summary>Writes <paramref name="entry"/> into the scope's <paramref name="tier"/> memory (create-or-append, atomic).</summary>
    ValueTask<MemoryWriteResult> WriteAsync(KnowledgeAccessScope scope, MemoryEntry entry, MemoryTier tier, CancellationToken ct = default);

    /// <summary>Deletes a scope's memory subtree for one tier, or (when <paramref name="tier"/> is null) every applicable configured tier.</summary>
    ValueTask<MemoryDeleteResult> DeleteScopeAsync(KnowledgeAccessScope scope, MemoryTier? tier = null, CancellationToken ct = default);

    /// <summary>Lists the concepts stored for a scope across its applicable configured tiers (audit / DSAR).</summary>
    ValueTask<IReadOnlyList<MemoryConcept>> EnumerateAsync(KnowledgeAccessScope scope, CancellationToken ct = default);
}
