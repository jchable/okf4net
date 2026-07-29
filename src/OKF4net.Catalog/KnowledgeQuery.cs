// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// A knowledge-search request against an <see cref="IKnowledgeResolver"/> or
/// <see cref="IKnowledgeSource"/>.
/// </summary>
/// <param name="Text">
/// The search text, forwarded verbatim to the core <c>ConceptSearch.Search</c>
/// scorer (whitespace-separated, case-insensitive substring terms). Required
/// to be non-blank -- <see cref="GroupedKnowledgeResolver.SearchAsync"/>
/// throws <see cref="ArgumentException"/> for a blank <see cref="Text"/>,
/// since that is a caller/programming error rather than a data condition
/// (contrast <see cref="KnowledgeDiagnosticCode.NoMatches"/>, which is a
/// legitimate zero-result outcome for a well-formed query).
/// </param>
/// <param name="Tag">
/// Optional tag filter (<see cref="System.StringComparison.OrdinalIgnoreCase"/>),
/// reusing <c>ConceptSearch</c>'s own tag-filter semantics.
/// </param>
/// <remarks>
/// Deliberately V1-scoped: no user/tenant/path fields. Those are identity and
/// routing concerns the OKF spec (§8) keeps orthogonal to a search query, and
/// adding them here would be premature surface before an actual multi-tenant
/// consumer exists.
/// </remarks>
public sealed record KnowledgeQuery(string Text, string? Tag = null)
{
    /// <summary>How stale concepts (§5.5) are treated. Default <see cref="StalePolicy.Use"/>: surface everything.</summary>
    public StalePolicy StalePolicy { get; init; }

    /// <summary>
    /// Which ranking strategy to use for this one search, overriding the
    /// host's configured default. <see langword="null"/> (the default) defers
    /// to that host default -- it does NOT mean
    /// <see cref="KnowledgeResolverStrategy.GroupedBySource"/>. Only
    /// <see cref="KnowledgeResolverRouter"/> reads this; a concrete resolver
    /// used directly implements exactly one strategy and ignores it.
    /// </summary>
    public KnowledgeResolverStrategy? ResolverStrategy { get; init; }

    /// <summary>
    /// The maximum number of CONSECUTIVE passages one source may contribute
    /// to a fused result before a different source's next-best passage is
    /// pulled ahead of it. <see langword="null"/> (the default) defers to the
    /// host's configured default, which is itself <see langword="null"/>
    /// (disabled -- pure ranked order) unless configured otherwise.
    /// <para>
    /// Reordering only: no passage is ever dropped, so a caller that consumes
    /// the whole result gets the same set either way. It exists for callers
    /// that truncate early -- an agent context provider spending a token
    /// budget top-down, for instance, which would otherwise let one prolific
    /// source crowd out every other source's best material.
    /// </para>
    /// <para>
    /// Meaningful only for <see cref="KnowledgeResolverStrategy.Merged"/> and
    /// <see cref="KnowledgeResolverStrategy.PriorityWeighted"/>;
    /// <see cref="KnowledgeResolverStrategy.GroupedBySource"/> ignores it
    /// entirely (its output is grouped by source by definition). Must be
    /// greater than zero when set.
    /// </para>
    /// </summary>
    public int? FairnessQuota { get; init; }
}
