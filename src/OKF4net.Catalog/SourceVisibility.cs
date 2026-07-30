// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Filters an already priority/id-ordered enabled-source list down to the
/// subset visible to one query's caller, per the resolution order in
/// docs/design/specs/2026-07-29-okf4net-v2-source-visibility.md §5.
/// </summary>
/// <remarks>
/// Shared by <see cref="GroupedKnowledgeResolver"/> and
/// <see cref="FusedResolverEngine"/> -- the two places an enabled-source
/// list gets narrowed before searching -- so this algorithm cannot drift
/// between them, the same reasoning <see cref="ResolverGuards"/> already
/// applies to query validation.
/// </remarks>
internal static class SourceVisibility
{
    /// <summary>
    /// Returns the subset of <paramref name="sources"/> visible to
    /// <paramref name="query"/>'s caller.
    /// </summary>
    /// <param name="sources">The enabled, knowledge-role sources under consideration.</param>
    /// <param name="query">
    /// The query whose <see cref="KnowledgeQuery.Scope"/>/
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/>/
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> govern filtering.
    /// </param>
    /// <param name="defaultPolicy">
    /// The host's configured default policy, used when the query sets
    /// neither <see cref="KnowledgeQuery.PermittedSourceIds"/> nor
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/>.
    /// </param>
    /// <remarks>
    /// PRECONDITION: <paramref name="query"/> already passed
    /// <see cref="ResolverGuards.ValidateQuery"/> -- callers are guaranteed
    /// not to have both <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> set, so this
    /// method never needs to re-check that.
    /// </remarks>
    internal static List<KnowledgeCatalogSource> Filter(
        List<KnowledgeCatalogSource> sources,
        KnowledgeQuery query,
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultPolicy)
    {
        if (query.PermittedSourceIds is { } permitted)
        {
            return sources.Where(s => permitted.Contains(s.Id)).ToList();
        }

        var policy = query.SourceVisibilityPolicy ?? defaultPolicy;
        if (policy is null)
        {
            return sources;
        }

        return sources.Where(s => policy(query.Scope, s)).ToList();
    }
}
