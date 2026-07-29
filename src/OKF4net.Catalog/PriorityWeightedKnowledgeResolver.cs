// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// The <see cref="KnowledgeResolverStrategy.PriorityWeighted"/> strategy:
/// the same fusion pipeline as <see cref="MergedKnowledgeResolver"/>, but
/// ranked by descending <see cref="KnowledgeCatalogSource.Priority"/> FIRST,
/// with <see cref="KnowledgePassage.Score"/> ordering only WITHIN a single
/// priority tier. A higher-priority source's passage therefore never falls
/// behind a lower-priority source's, however much stronger the latter's
/// match -- except for the reordering a <see cref="KnowledgeQuery.FairnessQuota"/>,
/// if one applies, can still introduce afterward.
/// </summary>
/// <remarks>
/// <para>
/// This is a lexicographic sort-key swap, NOT a numeric blend of priority
/// into the score. A blend (say <c>score + priority * K</c>) would require
/// inventing a scale relating two quantities that have no common unit, with
/// no principled default and surprising behaviour at the boundaries. Sorting
/// on priority first delivers the guarantee an operator actually asks for --
/// "this source is authoritative" -- exactly, and needs no such mapping.
/// </para>
/// <para>
/// Source dedup, stale filtering, diagnostics, and the never-throw contract
/// are all inherited unchanged from <see cref="FusedResolverEngine"/>; see
/// <see cref="MergedKnowledgeResolver"/>'s remarks for their semantics.
/// </para>
/// </remarks>
public sealed class PriorityWeightedKnowledgeResolver : IKnowledgeResolver
{
    /// <summary>
    /// Descending source priority, then descending score, then ordinal source
    /// id, then ordinal concept id. The last two exist purely to make the
    /// order TOTAL: <see cref="List{T}.Sort(IComparer{T})"/> is unstable, so
    /// any remaining tie would let equally-ranked passages shuffle between
    /// otherwise identical searches.
    /// </summary>
    private sealed class PriorityFirstComparer : IComparer<RankedPassage>
    {
        public int Compare(RankedPassage x, RankedPassage y)
        {
            var byPriority = y.Priority.CompareTo(x.Priority);
            if (byPriority != 0)
            {
                return byPriority;
            }

            var byScore = y.Passage.Score.CompareTo(x.Passage.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var bySource = string.CompareOrdinal(x.Passage.SourceId, y.Passage.SourceId);
            return bySource != 0 ? bySource : string.CompareOrdinal(x.Passage.ConceptId, y.Passage.ConceptId);
        }
    }

    private static readonly PriorityFirstComparer Comparer = new();

    private readonly IKnowledgeCatalog _catalog;
    private readonly IOkfClock _clock;
    private readonly int? _defaultFairnessQuota;

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
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaultFairnessQuota"/> is set but not greater than zero.
    /// </exception>
    public PriorityWeightedKnowledgeResolver(IKnowledgeCatalog catalog, IOkfClock? clock = null, int? defaultFairnessQuota = null)
    {
        ResolverGuards.ValidateDefaultFairnessQuota(defaultFairnessQuota, nameof(defaultFairnessQuota));

        _catalog = catalog;
        _clock = clock ?? new SystemClock();
        _defaultFairnessQuota = defaultFairnessQuota;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A blank <see cref="KnowledgeQuery.Text"/>, or a non-positive
    /// <see cref="KnowledgeQuery.FairnessQuota"/>, throws
    /// <see cref="ArgumentException"/>, exactly as in
    /// <see cref="MergedKnowledgeResolver.SearchAsync"/>.
    /// </remarks>
    public ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return FusedResolverEngine.SearchAsync(
            _catalog, _clock, query, Comparer, query.FairnessQuota ?? _defaultFairnessQuota, ct);
    }
}
