// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// The <see cref="IKnowledgeResolver"/> a host actually injects: it owns one
/// instance of each concrete strategy and dispatches every search to
/// <see cref="KnowledgeQuery.ResolverStrategy"/>, or to the configured
/// default when the query names none.
/// </summary>
/// <remarks>
/// <para>
/// Registering this as the single <see cref="IKnowledgeResolver"/> is what
/// makes per-query strategy selection reachable without any existing consumer
/// changing: they keep resolving one <see cref="IKnowledgeResolver"/> from the
/// container and simply gain the ability to set
/// <see cref="KnowledgeQuery.ResolverStrategy"/> on a query.
/// </para>
/// <para>
/// <b>Why the defaults are plain constructor parameters.</b> They come from
/// <c>KnowledgeOptions</c>, which lives in <c>OKF4net.Catalog.Hosting</c> --
/// a package that depends on THIS one. Referencing it here would invert that
/// dependency and make the graph cyclic, so the hosting layer reads its own
/// options and passes the two values in.
/// </para>
/// </remarks>
public sealed class KnowledgeResolverRouter : IKnowledgeResolver
{
    private readonly GroupedKnowledgeResolver _grouped;
    private readonly MergedKnowledgeResolver _merged;
    private readonly PriorityWeightedKnowledgeResolver _priorityWeighted;
    private readonly KnowledgeResolverStrategy _defaultStrategy;

    /// <summary>
    /// Creates a router over <paramref name="catalog"/>, constructing all
    /// three strategies eagerly (each is a stateless wrapper over the shared
    /// catalog, so this is cheap).
    /// </summary>
    /// <param name="catalog">The catalog every strategy searches.</param>
    /// <param name="defaultStrategy">
    /// The strategy used when a query leaves
    /// <see cref="KnowledgeQuery.ResolverStrategy"/> unset. Defaults to
    /// <see cref="KnowledgeResolverStrategy.GroupedBySource"/> -- the
    /// behaviour every pre-existing deployment already has, so an upgrade
    /// never silently reorders anyone's results.
    /// </param>
    /// <param name="defaultFairnessQuota">
    /// The fairness quota the fused strategies use when a query leaves
    /// <see cref="KnowledgeQuery.FairnessQuota"/> unset;
    /// <see langword="null"/> (the default) disables reordering.
    /// </param>
    /// <param name="defaultSourceVisibilityPolicy">
    /// The visibility policy every strategy uses when a query leaves both
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> unset;
    /// <see langword="null"/> (the default) applies no restriction.
    /// </param>
    /// <param name="clock">Supplies "today" for stale-policy filtering; defaults to the system clock.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="defaultStrategy"/> is not a defined
    /// <see cref="KnowledgeResolverStrategy"/> member.
    /// </exception>
    public KnowledgeResolverRouter(
        IKnowledgeCatalog catalog,
        KnowledgeResolverStrategy defaultStrategy = KnowledgeResolverStrategy.GroupedBySource,
        int? defaultFairnessQuota = null,
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultSourceVisibilityPolicy = null,
        IOkfClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ResolverGuards.ValidateStrategy(defaultStrategy, nameof(defaultStrategy));

        var effectiveClock = clock ?? new SystemClock();
        _grouped = new GroupedKnowledgeResolver(catalog, effectiveClock, defaultSourceVisibilityPolicy);
        _merged = new MergedKnowledgeResolver(catalog, effectiveClock, defaultFairnessQuota, defaultSourceVisibilityPolicy);
        _priorityWeighted = new PriorityWeightedKnowledgeResolver(catalog, effectiveClock, defaultFairnessQuota, defaultSourceVisibilityPolicy);
        _defaultStrategy = defaultStrategy;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Delegates to the selected strategy; every contract the strategies
    /// document (the blank-query <see cref="ArgumentException"/>,
    /// errors-as-data diagnostics, generation stamping) is theirs unchanged.
    /// <paramref name="query"/> is validated -- including
    /// <see cref="KnowledgeQuery.ResolverStrategy"/>, if set -- before any
    /// strategy is chosen, so an undefined enum value never silently falls
    /// back to <see cref="KnowledgeResolverStrategy.GroupedBySource"/>; it is
    /// rejected instead.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="query"/> fails <see cref="ResolverGuards.ValidateQuery"/>
    /// -- including a <see cref="KnowledgeQuery.ResolverStrategy"/> that is
    /// not a defined <see cref="KnowledgeResolverStrategy"/> member.
    /// </exception>
    public ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ResolverGuards.ValidateQuery(query);

        // The switch is exhaustive by construction, not by convention: every
        // reachable value of (query.ResolverStrategy ?? _defaultStrategy) was
        // already validated above (query.ResolverStrategy, if set) or at
        // construction (_defaultStrategy, in the constructor) -- so the
        // fallback arm below can never actually run for a value this method
        // itself produced.
        return (query.ResolverStrategy ?? _defaultStrategy) switch
        {
            KnowledgeResolverStrategy.GroupedBySource => _grouped.SearchAsync(query, ct),
            KnowledgeResolverStrategy.Merged => _merged.SearchAsync(query, ct),
            KnowledgeResolverStrategy.PriorityWeighted => _priorityWeighted.SearchAsync(query, ct),
            var strategy => throw new InvalidOperationException($"unreachable KnowledgeResolverStrategy: {strategy}"),
        };
    }
}
