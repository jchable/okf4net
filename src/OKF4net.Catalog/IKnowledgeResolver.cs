// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Searches across every enabled source of an <see cref="IKnowledgeCatalog"/>
/// and returns a single, grouped-by-source <see cref="KnowledgeContext"/>.
/// See <see cref="DefaultKnowledgeResolver"/> for the V1 implementation
/// (no cross-source fusion/dedup/merged ranking).
/// </summary>
public interface IKnowledgeResolver
{
    /// <summary>
    /// Runs <paramref name="query"/> against the catalog's currently enabled
    /// sources.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="query"/>'s <see cref="KnowledgeQuery.Text"/> is null, empty, or whitespace.</exception>
    ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default);
}
