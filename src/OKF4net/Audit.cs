// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

namespace OKF4net;

/// <summary>
/// The selection predicates of an audit (§5.3–§5.5). Predicates combine with
/// AND; <c>default</c> selects every concept.
/// </summary>
/// <remarks>
/// The generated equality compares <see cref="Trust"/> by reference (the
/// behaviour of <c>EqualityComparer&lt;IReadOnlySet&lt;T&gt;&gt;.Default</c>), so two
/// logically identical queries may compare unequal. Do not rely on it, and do
/// not use an <see cref="AuditQuery"/> as a dictionary key: the record struct is
/// for <c>with</c> and <c>ToString</c>, not for its equality.
/// </remarks>
/// <param name="StaleOnly">Keep only concepts past their <c>stale_after</c> date.</param>
/// <param name="Trust">Keep only concepts whose derived tier is in this set; null keeps every tier.</param>
/// <param name="Status">Keep only concepts with this lifecycle status; null keeps every status.</param>
/// <param name="Type">
/// Keep only concepts whose frontmatter <c>type</c> matches exactly (ordinal).
/// Null — or blank, which §11 forbids as a concept's type and which callers
/// emit for "unset" — keeps every type.
/// </param>
public readonly record struct AuditQuery(
    bool StaleOnly = false,
    IReadOnlySet<TrustTier>? Trust = null,
    ConceptStatus? Status = null,
    string? Type = null)
{
    /// <summary>The query that keeps every concept.</summary>
    public static AuditQuery All => default;

    /// <summary>
    /// Whether this query constrains the selection below "every concept" --
    /// true whenever any predicate is non-default. This is not "did the
    /// caller type a filter flag": the CLI's report mode builds
    /// <c>new AuditQuery(StaleOnly: true)</c> itself, with no flag typed, and
    /// this is still <see langword="true"/> for it.
    /// </summary>
    public bool IsFiltered => StaleOnly || Trust is not null || Status is not null || Type is not null;
}

/// <summary>One concept selected by an audit, with its signals already derived.</summary>
/// <param name="Id">The concept id (§2).</param>
/// <param name="Path">The concept's file path, as built from the bundle root.</param>
/// <param name="Type">The frontmatter <c>type</c>, or null when absent.</param>
/// <param name="Title">The frontmatter <c>title</c>, or null when absent.</param>
/// <param name="Trust">The derived trust tier (§5.3).</param>
/// <param name="Lifecycle">The lifecycle fields (§5.4/§5.5).</param>
/// <param name="IsStale">Whether the concept is stale as of the report's <see cref="AuditReport.AsOf"/>.</param>
public readonly record struct AuditFinding(
    ConceptId Id,
    string Path,
    string? Type,
    string? Title,
    TrustTier Trust,
    Lifecycle Lifecycle,
    bool IsStale);

/// <summary>
/// The result of an audit: counts over the whole bundle, plus the concepts the
/// query selected. The counts never narrow with the query -- the denominator
/// stays stable while <see cref="Findings"/> moves.
/// </summary>
public sealed class AuditReport
{
    internal AuditReport(
        DateOnly asOf,
        int conceptCount,
        IReadOnlyDictionary<TrustTier, int> trustCounts,
        IReadOnlyDictionary<ConceptStatus, int> statusCounts,
        int staleCount,
        IReadOnlyList<AuditFinding> findings)
    {
        AsOf = asOf;
        ConceptCount = conceptCount;
        TrustCounts = trustCounts;
        StatusCounts = statusCounts;
        StaleCount = staleCount;
        Findings = findings;
    }

    /// <summary>The observation date staleness was evaluated against.</summary>
    public DateOnly AsOf { get; }

    /// <summary>The number of concepts in the bundle (not in the selection).</summary>
    public int ConceptCount { get; }

    /// <summary>Concept counts per trust tier over the whole bundle; all three keys are always present.</summary>
    public IReadOnlyDictionary<TrustTier, int> TrustCounts { get; }

    /// <summary>Concept counts per lifecycle status over the whole bundle; all three keys are always present.</summary>
    public IReadOnlyDictionary<ConceptStatus, int> StatusCounts { get; }

    /// <summary>The number of stale concepts in the whole bundle.</summary>
    public int StaleCount { get; }

    /// <summary>
    /// The selected concepts, sorted by concept id — component-wise, via
    /// <see cref="ConceptId.CompareTo"/>, the same ordering <c>Bundle.Load</c>
    /// and <c>IndexGenerator</c> use. Not a flat ordinal compare of the joined
    /// id: the two disagree wherever a separator meets a segment character
    /// (<c>orders/extra</c> sorts before <c>orders-extra</c>).
    /// </summary>
    public IReadOnlyList<AuditFinding> Findings { get; }
}

/// <summary>
/// The single spelling of the audit vocabularies, shared by every surface (CLI
/// input, CLI text, JSON, agent tool) so no two layers can drift apart.
/// </summary>
public static class AuditVocabulary
{
    /// <summary>
    /// The trust tiers, weakest to strongest -- the canonical order. JSON
    /// serializes in this order; the text report walks it in reverse, showing
    /// the strongest tier first.
    /// </summary>
    public static IReadOnlyList<TrustTier> TrustTiersInOrder { get; } =
        [TrustTier.Unverified, TrustTier.MachineConfirmed, TrustTier.HumanReviewed];

    /// <summary>The lifecycle statuses in §5.4 order -- the order every surface displays them in.</summary>
    public static IReadOnlyList<ConceptStatus> StatusesInOrder { get; } =
        [ConceptStatus.Draft, ConceptStatus.Stable, ConceptStatus.Deprecated];

    /// <summary>The wire/display name of a trust tier.</summary>
    public static string Name(TrustTier tier) => tier switch
    {
        TrustTier.HumanReviewed => "human-reviewed",
        TrustTier.MachineConfirmed => "machine-confirmed",
        _ => "unverified",
    };

    /// <summary>The wire/display name of a lifecycle status.</summary>
    public static string Name(ConceptStatus status) => status switch
    {
        ConceptStatus.Draft => "draft",
        ConceptStatus.Deprecated => "deprecated",
        _ => "stable",
    };

    /// <summary>Parses a trust tier name (exact, ordinal). Unlike frontmatter parsing, an unknown name fails rather than defaulting.</summary>
    public static bool TryParseTrustTier(string text, out TrustTier tier)
    {
        switch (text)
        {
            case "unverified": tier = TrustTier.Unverified; return true;
            case "machine-confirmed": tier = TrustTier.MachineConfirmed; return true;
            case "human-reviewed": tier = TrustTier.HumanReviewed; return true;
            default: tier = TrustTier.Unverified; return false;
        }
    }

    /// <summary>Parses a lifecycle status name (exact, ordinal). Unlike <see cref="Lifecycle.From"/>, an unknown name fails rather than resolving to stable.</summary>
    public static bool TryParseStatus(string text, out ConceptStatus status)
    {
        switch (text)
        {
            case "draft": status = ConceptStatus.Draft; return true;
            case "stable": status = ConceptStatus.Stable; return true;
            case "deprecated": status = ConceptStatus.Deprecated; return true;
            default: status = ConceptStatus.Stable; return false;
        }
    }

    /// <summary>
    /// Parses a comma-separated list of trust tier names (e.g.
    /// <c>"unverified,human-reviewed"</c>). Each entry is trimmed and parsed
    /// with <see cref="TryParseTrustTier"/>; a duplicate entry is absorbed
    /// silently since <paramref name="tiers"/> is a set. The single grammar
    /// shared by the CLI's <c>--trust</c> flag and the <c>okf_audit</c> tool's
    /// <c>trust</c> parameter -- callers differ only in what they do on
    /// failure (the CLI raises a <c>CliOperationException</c> naming
    /// <paramref name="badEntry"/>; the tool returns a usage message).
    /// </summary>
    /// <param name="raw">The comma-separated list, as typed by the caller.</param>
    /// <param name="tiers">On success, the parsed tiers; empty on failure.</param>
    /// <param name="badEntry">On failure, the first entry (trimmed) that failed to parse; <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> when every entry parsed.</returns>
    public static bool TryParseTrustTiers(string raw, out HashSet<TrustTier> tiers, out string? badEntry)
    {
        tiers = [];
        foreach (var entry in raw.Split(','))
        {
            var trimmed = entry.Trim();
            if (!TryParseTrustTier(trimmed, out var tier))
            {
                tiers = [];
                badEntry = trimmed;
                return false;
            }

            tiers.Add(tier);
        }

        badEntry = null;
        return true;
    }

    /// <summary>
    /// The freshness token for one concept line (§5.5): <c>"stale &lt;date&gt;"</c>
    /// or <c>"fresh &lt;date&gt;"</c> when <c>stale_after</c> parsed, or
    /// <c>"no-stale-after"</c> when it is absent or malformed. The single
    /// spelling shared by both renderers -- do not spell these tokens as
    /// literals outside this method.
    /// </summary>
    /// <param name="lifecycle">The concept's lifecycle fields.</param>
    /// <param name="isStale">Whether the concept is stale as of the report's <c>AsOf</c> date.</param>
    public static string Freshness(Lifecycle lifecycle, bool isStale) =>
        lifecycle.StaleAfter is { } date
            ? (isStale ? "stale " : "fresh ") + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "no-stale-after";
}

/// <summary>
/// Queries a bundle's §5.3–§5.5 signals. Reads nothing from disk, writes
/// nothing, and never throws on data: an unparseable document is already absent
/// from <see cref="Bundle.Concepts"/>, and a malformed <c>stale_after</c> simply
/// reads as "not stale" (the validator owns that diagnostic).
/// </summary>
public static class ConceptAudit
{
    /// <summary>Runs <paramref name="query"/> over <paramref name="bundle"/>.</summary>
    /// <param name="bundle">The loaded bundle.</param>
    /// <param name="query">The selection predicates; <c>default</c> selects everything.</param>
    /// <param name="clock">Supplies "today" for staleness (§5.5); defaults to <see cref="SystemClock"/>.</param>
    public static AuditReport Run(Bundle bundle, AuditQuery query = default, IOkfClock? clock = null)
    {
        var asOf = (clock ?? new SystemClock()).Today;

        // Seeded from the vocabulary rather than from a hand-written list of
        // members: a tier or status added to the enum without a matching line
        // here would make the increments below throw KeyNotFoundException on the
        // first concept carrying it, breaking this type's "never throws on data"
        // contract exactly where the vocabulary exists to prevent drift.
        var trustCounts = AuditVocabulary.TrustTiersInOrder.ToDictionary(tier => tier, _ => 0);
        var statusCounts = AuditVocabulary.StatusesInOrder.ToDictionary(status => status, _ => 0);

        var staleCount = 0;
        var findings = new List<AuditFinding>();

        foreach (var concept in bundle.Concepts)
        {
            var frontmatter = concept.Document.Frontmatter;
            var tier = frontmatter.TrustTier;
            var lifecycle = frontmatter.Lifecycle;
            var isStale = lifecycle.IsStale(asOf);

            trustCounts[tier]++;
            statusCounts[lifecycle.Status]++;
            if (isStale)
            {
                staleCount++;
            }

            if (query.StaleOnly && !isStale)
            {
                continue;
            }

            if (query.Trust is { } tiers && !tiers.Contains(tier))
            {
                continue;
            }

            if (query.Status is { } status && lifecycle.Status != status)
            {
                continue;
            }

            // Blank is treated as "no type filter", not as a filter for the
            // empty string: §11 requires a non-empty `type`, so no conformant
            // concept can carry one, and a blank filter could only ever select
            // nothing. Callers whose filters come from a model or a form pass
            // "" for "unset" far more often than they mean it literally.
            if (!string.IsNullOrWhiteSpace(query.Type)
                && !string.Equals(frontmatter.Type, query.Type, StringComparison.Ordinal))
            {
                continue;
            }

            findings.Add(new AuditFinding(
                concept.Id,
                concept.Path,
                frontmatter.Type,
                frontmatter.Title,
                tier,
                lifecycle,
                isStale));
        }

        findings.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        return new AuditReport(asOf, bundle.Count, trustCounts, statusCounts, staleCount, findings);
    }
}
