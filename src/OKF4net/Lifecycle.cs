// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net;

/// <summary>The lifecycle state of a concept (§5.4). Absent <c>status</c> ⇒ <see cref="Stable"/>.</summary>
public enum ConceptStatus
{
    /// <summary>Not yet reviewed; possibly incomplete.</summary>
    Draft,

    /// <summary>Default; ready for consumption.</summary>
    Stable,

    /// <summary>Kept for links and history; no longer current.</summary>
    Deprecated,
}

/// <summary>
/// A concept's lifecycle fields (§5.4/§5.5): <c>status</c> and <c>stale_after</c>.
/// Parsing is lenient — an unknown status resolves to <see cref="ConceptStatus.Stable"/> with
/// <see cref="StatusIsKnown"/> false, and a malformed <c>stale_after</c> leaves
/// <see cref="StaleAfter"/> null with <see cref="StaleAfterMalformed"/> true. The validator warns on both.
/// </summary>
/// <remarks>
/// §5 requires every timestamp-valued key to be an ISO 8601 datetime with an
/// explicit UTC offset (<c>2026-06-30T14:00:00Z</c>). A bare <c>YYYY-MM-DD</c>,
/// or a datetime with no offset, is still read — normalized to midnight UTC and
/// to UTC respectively — but sets <see cref="StaleAfterIsLegacyDate"/> so the
/// validator can warn, in the same way the §13.1 legacy fields do.
/// </remarks>
public readonly record struct Lifecycle(ConceptStatus Status, bool StatusIsKnown, string? StaleAfterRaw, DateTimeOffset? StaleAfter)
{
    /// <summary>True when a <c>stale_after</c> value is present but could not be parsed at all.</summary>
    public bool StaleAfterMalformed => StaleAfterRaw is not null && StaleAfter is null;

    /// <summary>
    /// True when <c>stale_after</c> parsed but not in the §5 form — a bare
    /// <c>YYYY-MM-DD</c>, or a datetime carrying no explicit offset.
    /// </summary>
    public bool StaleAfterIsLegacyDate { get; private init; }

    /// <summary>The UTC calendar date of <see cref="StaleAfter"/>, for rendering. Null when it did not parse.</summary>
    public DateOnly? StaleAfterDate =>
        StaleAfter is { } d ? DateOnly.FromDateTime(d.UtcDateTime) : null;

    /// <summary>Whether the concept is stale as of <paramref name="now"/> (§5.5: <c>now &gt;= stale_after</c>).</summary>
    /// <param name="now">The instant to evaluate staleness at, typically <see cref="IOkfClock.Now"/>.</param>
    public bool IsStale(DateTimeOffset now) => StaleAfter is { } d && now >= d;

    /// <summary>Builds a <see cref="Lifecycle"/> from raw <c>status</c> and <c>stale_after</c> display strings.</summary>
    /// <param name="statusRaw">The raw <c>status</c> value, or null when absent.</param>
    /// <param name="staleAfterRaw">The raw <c>stale_after</c> value, or null when absent.</param>
    public static Lifecycle From(string? statusRaw, string? staleAfterRaw)
    {
        var (status, known) = statusRaw switch
        {
            null => (ConceptStatus.Stable, true),
            "draft" => (ConceptStatus.Draft, true),
            "stable" => (ConceptStatus.Stable, true),
            "deprecated" => (ConceptStatus.Deprecated, true),
            _ => (ConceptStatus.Stable, false),
        };

        var (instant, legacy) = ParseStaleAfter(staleAfterRaw);

        return new Lifecycle(status, known, staleAfterRaw, instant) { StaleAfterIsLegacyDate = legacy };
    }

    /// <summary>
    /// Parses <c>stale_after</c> into an instant, through the shared §5 seam.
    /// <c>stale_after</c> is one timestamp-valued key among several (§5.1's
    /// <c>last_modified</c> and <c>usage_window</c> bounds, §5.2's
    /// <c>generated.at</c> and <c>verified[].at</c>), so the parsing rule lives
    /// in <see cref="OkfTimestamp"/> rather than here.
    /// </summary>
    private static (DateTimeOffset? Instant, bool Legacy) ParseStaleAfter(string? raw)
    {
        if (raw is null)
        {
            return (null, false);
        }

        return OkfTimestamp.TryParse(raw, out var instant, out var legacy)
            ? (instant, legacy)
            : (null, false);
    }
}
