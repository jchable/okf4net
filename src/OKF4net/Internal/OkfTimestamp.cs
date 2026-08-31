// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

namespace OKF4net.Internal;

/// <summary>
/// The single §5 timestamp seam: both the UTC format this library emits and
/// the parser every timestamp-valued key is read through.
/// </summary>
/// <remarks>
/// <para>
/// §5: "Every timestamp-valued key in OKF is an ISO 8601 datetime with an
/// explicit UTC offset, for example <c>2026-06-30T14:00:00Z</c>." That covers
/// <c>stale_after</c> (§5.5), <c>generated.at</c> and <c>verified[].at</c>
/// (§5.2), and <c>sources[].last_modified</c> / <c>usage_window.from</c> /
/// <c>usage_window.to</c> (§5.1). All of them come through
/// <see cref="TryParse"/>, so the rule is spelled once rather than re-derived
/// per field.
/// </para>
/// <para>
/// It deliberately does <b>not</b> cover §9 <c>log.md</c> date headings, which
/// §9 pins to bare <c>YYYY-MM-DD</c> ("Date headings MUST use ISO 8601
/// <c>YYYY-MM-DD</c> form"). Those stay on <see cref="OKF4net.ChangeLog.IsIsoDate"/>.
/// </para>
/// <para>
/// <see cref="FormatUtc"/> is what <see cref="OKF4net.BundleConceptWriter"/> and
/// the agent-memory capture stamps in <c>OkfContextProvider</c> write: second
/// precision, no fractional part, a literal <c>Z</c> suffix, invariant culture.
/// <c>OKF4net.Agents</c> sees this internal type via the core project's
/// <c>InternalsVisibleTo</c>.
/// </para>
/// </remarks>
internal static class OkfTimestamp
{
    /// <summary>
    /// The zoneless datetime shapes read as a legacy fallback. Deliberately an
    /// exact-format list rather than <see cref="DateTime.TryParse(string, IFormatProvider, DateTimeStyles, out DateTime)"/>,
    /// which accepts culture-shaped values (<c>01/02/2026</c>, a bare year) that
    /// no OKF producer writes: widening "malformed" into "legacy, assumed UTC"
    /// would silently start honouring garbage the validator should report.
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
    /// Formats <paramref name="utc"/> as <c>yyyy-MM-ddTHH:mm:ssZ</c> under the
    /// invariant culture. The caller is responsible for passing a UTC instant;
    /// the trailing <c>Z</c> is a literal designator, not a computed offset.
    /// </summary>
    internal static string FormatUtc(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// Parses a §5 timestamp. Reads the conformant form as-is, and — permissively,
    /// per §11 — also a bare <c>YYYY-MM-DD</c> (as midnight UTC) and a zoneless
    /// ISO datetime (as UTC), setting <paramref name="isLegacyForm"/> for both so
    /// callers can warn without rejecting.
    /// </summary>
    /// <param name="raw">The raw frontmatter value.</param>
    /// <param name="instant">The parsed instant, normalized to UTC.</param>
    /// <param name="isLegacyForm">True when the value parsed but not in the §5 form.</param>
    /// <returns>False when <paramref name="raw"/> is not a timestamp at all.</returns>
    internal static bool TryParse(string raw, out DateTimeOffset instant, out bool isLegacyForm)
    {
        // The §5 form: an ISO 8601 datetime carrying an explicit offset.
        if (HasExplicitOffset(raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var withOffset))
        {
            instant = withOffset.ToUniversalTime();
            isLegacyForm = false;
            return true;
        }

        // Legacy: a bare YYYY-MM-DD calendar date, read as midnight UTC.
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            instant = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            isLegacyForm = true;
            return true;
        }

        // Legacy: a datetime with no offset, assumed UTC.
        if (DateTime.TryParseExact(raw, ZonelessFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
        {
            instant = new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Utc), TimeSpan.Zero);
            isLegacyForm = true;
            return true;
        }

        instant = default;
        isLegacyForm = false;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="raw"/> is a §5-conformant timestamp — it parses
    /// <em>and</em> carries an explicit offset.
    /// </summary>
    /// <param name="raw">The raw frontmatter value.</param>
    internal static bool IsConformant(string raw) =>
        TryParse(raw, out _, out var legacy) && !legacy;

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
