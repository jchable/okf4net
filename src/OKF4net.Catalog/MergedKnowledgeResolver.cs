// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// The <see cref="KnowledgeResolverStrategy.Merged"/> strategy: every enabled
/// <see cref="SourceRole.Knowledge"/> source's matches merged into ONE list
/// ranked by descending <see cref="KnowledgePassage.Score"/> across all
/// sources, with <see cref="KnowledgeCatalogSource.Priority"/> as a tie-break
/// only -- never a score multiplier -- and then, if a
/// <see cref="KnowledgeQuery.FairnessQuota"/> applies, reordered for fairness.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why raw scores are comparable across bundles.</b> The core
/// <c>ConceptSearch</c> scorer is a deterministic weighted term-count (title
/// x3, tags/description x2, body x1) with NO per-corpus statistics -- no IDF,
/// no document-frequency or bundle-size normalization. Two passages scoring
/// equally in different bundles matched an equal weight of terms, so ranking
/// them against each other directly is sound rather than merely approximate.
/// </para>
/// <para>
/// <b>Source dedup.</b> Two enabled manifest entries resolving to the same
/// directory are the same bundle mounted twice; only the survivor (higher
/// <see cref="KnowledgeCatalogSource.Priority"/>, then lower ordinal
/// <see cref="KnowledgeCatalogSource.Id"/>) is searched. The eliminated entry
/// therefore contributes neither passages NOR diagnostics -- it is never
/// searched at all. Two DIFFERENT directories that happen to produce the same
/// concept id are never merged: a concept id is derived from a path relative
/// to its own bundle root and is not a globally stable identity, so
/// collapsing them would silently conflate unrelated concepts.
/// </para>
/// </remarks>
public sealed class MergedKnowledgeResolver : IKnowledgeResolver
{
    /// <summary>
    /// Descending score, then descending source priority, then ordinal source
    /// id, then ordinal concept id. The last two exist purely to make the
    /// order TOTAL: <see cref="List{T}.Sort(IComparer{T})"/> is unstable, so
    /// any remaining tie would let equally-ranked passages shuffle between
    /// otherwise identical searches.
    /// </summary>
    private sealed class ScoreFirstComparer : IComparer<RankedPassage>
    {
        public int Compare(RankedPassage x, RankedPassage y)
        {
            var byScore = y.Passage.Score.CompareTo(x.Passage.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byPriority = y.Priority.CompareTo(x.Priority);
            if (byPriority != 0)
            {
                return byPriority;
            }

            var bySource = string.CompareOrdinal(x.Passage.SourceId, y.Passage.SourceId);
            return bySource != 0 ? bySource : string.CompareOrdinal(x.Passage.ConceptId, y.Passage.ConceptId);
        }
    }

    private static readonly ScoreFirstComparer Comparer = new();

    private readonly IKnowledgeCatalog _catalog;
    private readonly IOkfClock _clock;
    private readonly int? _defaultFairnessQuota;
    private readonly Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? _defaultSourceVisibilityPolicy;

    /// <summary>
    /// Creates a resolver over <paramref name="catalog"/>.
    /// </summary>
    /// <param name="catalog">The catalog whose enabled knowledge sources are searched.</param>
    /// <param name="clock">Supplies "today" for stale-policy filtering; defaults to the system clock.</param>
    /// <param name="defaultFairnessQuota">
    /// The fairness quota applied when a query does not set its own
    /// <see cref="KnowledgeQuery.FairnessQuota"/>. <see langword="null"/>
    /// (the default) disables fairness reordering entirely.
    /// </param>
    /// <param name="defaultSourceVisibilityPolicy">
    /// The visibility policy applied when a query leaves both
    /// <see cref="KnowledgeQuery.PermittedSourceIds"/> and
    /// <see cref="KnowledgeQuery.SourceVisibilityPolicy"/> unset;
    /// <see langword="null"/> (the default) applies no restriction.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaultFairnessQuota"/> is set but not greater than zero.
    /// </exception>
    public MergedKnowledgeResolver(
        IKnowledgeCatalog catalog,
        IOkfClock? clock = null,
        int? defaultFairnessQuota = null,
        Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? defaultSourceVisibilityPolicy = null)
    {
        ResolverGuards.ValidateDefaultFairnessQuota(defaultFairnessQuota, nameof(defaultFairnessQuota));

        _catalog = catalog;
        _clock = clock ?? new SystemClock();
        _defaultFairnessQuota = defaultFairnessQuota;
        _defaultSourceVisibilityPolicy = defaultSourceVisibilityPolicy;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A blank <see cref="KnowledgeQuery.Text"/> throws
    /// <see cref="ArgumentException"/> rather than being reported as a
    /// diagnostic: unlike <see cref="KnowledgeDiagnosticCode.NoMatches"/> (a
    /// legitimate zero-result outcome) or
    /// <see cref="KnowledgeDiagnosticCode.NoEnabledSources"/> (a legitimate
    /// catalog state), a blank query is a caller error -- there is no
    /// sensible search to attempt. A non-positive
    /// <see cref="KnowledgeQuery.FairnessQuota"/> or an undefined
    /// <see cref="KnowledgeQuery.ResolverStrategy"/> throw the same way; see
    /// <see cref="ResolverGuards.ValidateQuery"/>. Every check runs
    /// SYNCHRONOUSLY -- this method is deliberately not <see langword="async"/>
    /// itself, so the throw happens at the call site rather than inside the
    /// returned <see cref="ValueTask{TResult}"/>.
    /// </remarks>
    public ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ResolverGuards.ValidateQuery(query);
        return FusedResolverEngine.SearchAsync(
            _catalog, _clock, query, Comparer, query.FairnessQuota ?? _defaultFairnessQuota,
            _defaultSourceVisibilityPolicy, ct);
    }
}
