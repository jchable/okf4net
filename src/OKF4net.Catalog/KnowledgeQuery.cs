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
/// Carries the caller's identity (<see cref="Scope"/>) and, optionally, which
/// sources that caller may see (<see cref="PermittedSourceIds"/> or
/// <see cref="SourceVisibilityPolicy"/>) -- the "actual multi-tenant consumer"
/// an earlier version of this remark said would justify adding identity
/// fields here has now materialized; see
/// docs/design/specs/2026-07-29-okf4net-v2-source-visibility.md.
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

    /// <summary>
    /// The caller's identity, for source-visibility filtering
    /// (<see cref="PermittedSourceIds"/>/<see cref="SourceVisibilityPolicy"/>)
    /// and for any <see cref="SourceVisibilityPolicy"/> a host or this query
    /// supplies. Defaults to <see cref="KnowledgeAccessScope.Local"/> -- the
    /// same all-null sentinel already used throughout the memory-scoping
    /// work -- rather than a second nullability story for "no identity
    /// supplied." A policy evaluated against <c>Local</c> decides for itself
    /// whether an unscoped caller sees everything or nothing.
    /// </summary>
    public KnowledgeAccessScope Scope { get; init; } = KnowledgeAccessScope.Local;

    /// <summary>
    /// When set, only sources whose <see cref="KnowledgeCatalogSource.Id"/>
    /// is in this set are searched -- the default/recommended visibility
    /// mechanism: a host precomputes the exact set of source IDs a caller
    /// may see (however it wants -- tenant lookup, application/purpose
    /// lookup, or both combined) and hands it to the query. Always wins over
    /// any host-level <c>DefaultSourceVisibilityPolicy</c>, being more
    /// specific to this one call. Has no host-level default: a static ID set
    /// cannot represent "differs by tenant" at host-configuration time.
    /// <see langword="null"/> (the default) applies no restriction from this
    /// field. Mutually exclusive with <see cref="SourceVisibilityPolicy"/> on
    /// the same query -- setting both throws (see
    /// <c>ResolverGuards.ValidateQuery</c>). Matching (<c>Contains</c>) uses
    /// whichever equality comparer the host constructed this
    /// <see cref="IReadOnlySet{T}"/> with (e.g. ordinal vs.
    /// <see cref="System.StringComparison.OrdinalIgnoreCase"/>) -- it is not
    /// forced to ordinal, unlike the catalog's own internal source-id
    /// ordering (<see cref="System.StringComparer.Ordinal"/>) elsewhere.
    /// </summary>
    public IReadOnlySet<string>? PermittedSourceIds { get; init; }

    /// <summary>
    /// When set, only sources for which this function returns
    /// <see langword="true"/> (given <see cref="Scope"/> and the source
    /// under consideration) are searched -- the override mechanism, for
    /// visibility rules a flat ID list can't express conveniently. Overrides
    /// any host-level default policy for this one call.
    /// <see langword="null"/> (the default) defers to that host default.
    /// Mutually exclusive with <see cref="PermittedSourceIds"/> on the same
    /// query -- setting both throws (see <c>ResolverGuards.ValidateQuery</c>).
    /// Synchronous by design: a host needing asynchronous work (e.g. a
    /// database call) to determine visibility does it once, before
    /// constructing the query, via <see cref="PermittedSourceIds"/> instead --
    /// not per source inside a resolver's fan-out loop.
    /// <see cref="KnowledgeAccessScope"/> has no value-equality override
    /// (reference equality only): a policy function should compare
    /// <see cref="KnowledgeAccessScope.TenantId"/>/<see cref="KnowledgeAccessScope.UserId"/>/
    /// <see cref="KnowledgeAccessScope.SessionId"/> individually rather than
    /// comparing two <see cref="KnowledgeAccessScope"/> instances with
    /// <c>==</c>/<c>Equals</c>.
    /// </summary>
    public Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? SourceVisibilityPolicy { get; init; }
}
