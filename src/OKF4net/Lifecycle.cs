// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

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
    /// Parses <c>stale_after</c> into an instant. Returns the §5 form as-is,
    /// lifts a bare date to midnight UTC and a zoneless datetime to UTC (both
    /// flagged legacy), and returns <c>(null, false)</c> for anything else.
    /// </summary>
    private static (DateTimeOffset? Instant, bool Legacy) ParseStaleAfter(string? raw)
    {
        if (raw is null)
        {
            return (null, false);
        }

        // The §5 form: an ISO 8601 datetime carrying an explicit offset.
        if (HasExplicitOffset(raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var withOffset))
        {
            return (withOffset.ToUniversalTime(), false);
        }

        // Legacy: a bare YYYY-MM-DD calendar date, read as midnight UTC.
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return (new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), true);
        }

        // Legacy: a datetime with no offset, assumed UTC.
        if (DateTime.TryParseExact(
                raw,
                ZonelessFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var naive))
        {
            return (new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Utc), TimeSpan.Zero), true);
        }

        return (null, false);
    }

    /// <summary>
    /// The zoneless datetime shapes read as a legacy fallback. Deliberately an
    /// exact-format list rather than <c>DateTime.TryParse</c>: the latter accepts
    /// culture-shaped junk (<c>"01/02/2026"</c>, a bare year) that the previous
    /// parser correctly reported as malformed, and widening "malformed" into
    /// "legacy, assumed UTC" would silently start honouring garbage.
    /// </summary>
    private static readonly string[] ZonelessFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd HH:mm",
    ];

    /// <summary>
    /// Whether the raw value ends in an explicit zone designator (<c>Z</c>, or
    /// <c>±hh:mm</c>). <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
    /// happily supplies the local offset for a zoneless value, so the raw text
    /// is the only reliable way to tell the two apart.
    /// </summary>
    private static bool HasExplicitOffset(string raw)
    {
        var s = raw.AsSpan().Trim();
        if (s.Length == 0)
        {
            return false;
        }

        if (s[^1] is 'Z' or 'z')
        {
            return true;
        }

        // ±hh:mm — look only past the date part, so the date's own hyphens
        // are never mistaken for a negative offset.
        for (var i = 10; i < s.Length; i++)
        {
            if (s[i] is '+' or '-')
            {
                return true;
            }
        }

        return false;
    }
}
