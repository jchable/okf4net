// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// The argument checks every <see cref="IKnowledgeResolver"/> applies before
/// searching, kept in one place so a malformed query fails IDENTICALLY
/// whichever strategy happens to run it.
/// </summary>
/// <remarks>
/// Strategy-dependent validation would be a genuine trap: the strategy is
/// often chosen by a host default the calling code never sees, so the same
/// typo would surface in one deployment and pass silently in another.
/// </remarks>
internal static class ResolverGuards
{
    /// <summary>
    /// Validates the strategy-independent parts of <paramref name="query"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="query"/>'s <see cref="KnowledgeQuery.Text"/> is null,
    /// empty, or whitespace; its <see cref="KnowledgeQuery.FairnessQuota"/>
    /// is set but not greater than zero; or its
    /// <see cref="KnowledgeQuery.ResolverStrategy"/> is set to a value that
    /// is not a defined <see cref="KnowledgeResolverStrategy"/> member.
    /// </exception>
    internal static void ValidateQuery(KnowledgeQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Text))
        {
            throw new ArgumentException("KnowledgeQuery.Text must be non-blank.", nameof(query));
        }

        // Checked even by strategies that ignore the quota: an out-of-range
        // value is a caller mistake regardless of who would have consumed it,
        // and null already means "disabled" -- so a non-positive number can
        // only be an error, never an intent.
        if (query.FairnessQuota is <= 0)
        {
            throw new ArgumentException(
                $"KnowledgeQuery.FairnessQuota must be greater than zero (got {query.FairnessQuota}); use null to disable fairness reordering.",
                nameof(query));
        }

        // Checked even by GroupedKnowledgeResolver, which never reads
        // ResolverStrategy itself: a caller can hand a KnowledgeQuery
        // straight to any concrete resolver, not only through
        // KnowledgeResolverRouter, so an undefined enum value (e.g. an
        // out-of-range int surviving a config bind) must fail the same way
        // everywhere rather than only where the router happens to notice it.
        if (query.ResolverStrategy is { } strategy)
        {
            ValidateStrategy(strategy, nameof(query));
        }
    }

    /// <summary>
    /// Validates that <paramref name="strategy"/> is one of the members
    /// <see cref="KnowledgeResolverStrategy"/> actually defines.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="strategy"/> is not a defined
    /// <see cref="KnowledgeResolverStrategy"/> member.
    /// </exception>
    internal static void ValidateStrategy(KnowledgeResolverStrategy strategy, string paramName)
    {
        if (!Enum.IsDefined(strategy))
        {
            throw new ArgumentException(
                $"'{strategy}' is not a defined KnowledgeResolverStrategy member.",
                paramName);
        }
    }

    /// <summary>
    /// Validates a resolver's constructor-supplied default fairness quota.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaultFairnessQuota"/> is set but not greater than
    /// zero.
    /// </exception>
    internal static void ValidateDefaultFairnessQuota(int? defaultFairnessQuota, string paramName)
    {
        if (defaultFairnessQuota is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                defaultFairnessQuota,
                "A default fairness quota must be greater than zero; use null to disable fairness reordering.");
        }
    }
}
