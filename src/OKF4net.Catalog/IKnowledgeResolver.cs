// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Searches across every enabled, *visible* <see cref="SourceRole.Knowledge"/>
/// source of an <see cref="IKnowledgeCatalog"/> and returns a single
/// <see cref="KnowledgeContext"/>. Visibility -- which sources a given
/// caller may see at all -- is governed by
/// <see cref="KnowledgeQuery.PermittedSourceIds"/>/
/// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> and any host-level
/// default; see <see cref="SearchAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering is the implementation's contract, not this interface's.</b>
/// Each strategy documents its own, and they genuinely differ:
/// <see cref="GroupedKnowledgeResolver"/> concatenates each source's results
/// grouped by source; <see cref="MergedKnowledgeResolver"/> merges them into
/// one ranking by descending score; <see cref="PriorityWeightedKnowledgeResolver"/>
/// merges them ranked by source priority first. Callers that need a
/// particular ordering must select it -- see
/// <see cref="KnowledgeResolverStrategy"/> and
/// <see cref="KnowledgeResolverRouter"/> -- rather than relying on whatever
/// the injected implementation happens to be.
/// </para>
/// <para>
/// Common to every implementation: <see cref="SourceRole.Memory"/> sources
/// are never searched (they feed <c>IMemoryStore</c> instead), non-fatal
/// conditions come back as <see cref="KnowledgeContext.Diagnostics"/> rather
/// than exceptions, and a failing source never prevents the others from
/// being searched.
/// </para>
/// </remarks>
public interface IKnowledgeResolver
{
    /// <summary>
    /// Runs <paramref name="query"/> against the catalog's currently
    /// enabled, visible sources.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="query"/>'s <see cref="KnowledgeQuery.Text"/> is null,
    /// empty, or whitespace; its <see cref="KnowledgeQuery.FairnessQuota"/>
    /// is set but not greater than zero; its
    /// <see cref="KnowledgeQuery.ResolverStrategy"/> is set to a value that
    /// is not a defined <see cref="KnowledgeResolverStrategy"/> member; or
    /// both <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> are set.
    /// </exception>
    ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default);
}
