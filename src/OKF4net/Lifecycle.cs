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
public readonly record struct Lifecycle(ConceptStatus Status, bool StatusIsKnown, string? StaleAfterRaw, DateOnly? StaleAfter)
{
    /// <summary>True when a <c>stale_after</c> value is present but is not a valid <c>YYYY-MM-DD</c> calendar date.</summary>
    public bool StaleAfterMalformed => StaleAfterRaw is not null && StaleAfter is null;

    /// <summary>Whether the concept is stale as of <paramref name="asOf"/> (§5.5: <c>today &gt;= stale_after</c>).</summary>
    public bool IsStale(DateOnly asOf) => StaleAfter is { } d && asOf >= d;

    /// <summary>Builds a <see cref="Lifecycle"/> from raw <c>status</c> and <c>stale_after</c> display strings.</summary>
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

        DateOnly? parsed = staleAfterRaw is not null
            && DateOnly.TryParseExact(staleAfterRaw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : null;

        return new Lifecycle(status, known, staleAfterRaw, parsed);
    }
}
