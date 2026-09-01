// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace OKF4net.Internal;

/// <summary>
/// The four ways a raw §5 timestamp value can classify. Reading stays
/// permissive (§11: a readable value is never dropped) — this is a
/// classification of a value already known to be readable, split from
/// <see cref="TimestampForm.Unreadable"/> which covers the rest.
/// </summary>
internal enum TimestampForm
{
    /// <summary>Not a timestamp at all — <see cref="OkfTimestamp.Classify"/> could not read it.</summary>
    Unreadable,

    /// <summary>Matches the §5 grammar: ISO 8601 extended format with an explicit UTC offset.</summary>
    Conformant,

    /// <summary>A bare <c>YYYY-MM-DD</c> calendar date, or a datetime with no offset at all.</summary>
    LegacyDateOnly,

    /// <summary>Carries an explicit offset and parses, but the spelling is not ISO 8601.</summary>
    NonIso8601,
}

/// <summary>
/// The single §5 timestamp seam: both the UTC format this library emits and
/// the parser every timestamp-valued key is read through.
/// </summary>
/// <remarks>
/// <para>
/// §5: "Every timestamp-valued key in OKF is an ISO 8601 datetime with an
/// explicit UTC offset, for example <c>2026-06-30T14:00:00Z</c>." That covers
/// <c>stale_after</c> (§5.5), <c>generated.at</c> and <c>verified[].at</c>
/// (§5.2), and <c>sources[].last_modified</c> plus both <c>usage_window</c>
/// bounds in either position — the shared, top-level sibling
/// (<c>usage_window.from</c> / <c>usage_window.to</c>) and a per-entry
/// <c>sources[].usage_window</c> override (§5.1). All of them come through
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
    /// The exact §5 grammar: <c>YYYY-MM-DDThh:mm[:ss[(.|,)s+]]offset</c>, where
    /// <c>offset</c> is <c>Z</c>, a reduced-precision <c>±hh</c>, or an extended
    /// <c>±hh:mm</c>. Every component is fixed-width, the designator is a
    /// literal uppercase <c>Z</c>, and a minutes-bearing offset must be
    /// colon-separated — ISO 8601 forbids mixing basic and extended forms, so
    /// <c>+0200</c> (minutes with no separator) is rejected while <c>+02</c>
    /// (no minutes at all, so nothing to separate) and <c>+02:00</c> are both
    /// accepted. The fraction's decimal sign may be <c>.</c> or <c>,</c> — ISO
    /// 8601 §4.2.2.4 names the comma the <em>preferred</em> sign, so rejecting
    /// it would make this grammar stricter than the spec it exists to enforce,
    /// exactly the defect class this seam exists to catch. The negative zero
    /// offset (<c>-00:00</c> / <c>-00</c>) is <b>not</b> excluded by this
    /// pattern — <see cref="IsNegativeZeroOffset"/> turns it away separately,
    /// see there for why. <c>[0-9]</c> rather
    /// than <c>\d</c> throughout: <c>\d</c> is Unicode-aware in .NET and would
    /// otherwise match non-ASCII decimal digits, which this method — the
    /// strict authority on spelling — should reject on its own terms rather
    /// than relying on the readability gate ahead of it to have already done
    /// so. Deliberately checked against the raw text rather than derived from
    /// a parsed value: <see cref="DateTimeOffset"/> has no memory of how its
    /// source string was spelled, so the spelling check has to run before
    /// parsing throws that information away.
    /// </summary>
    private static readonly Regex ConformantPattern = new(
        @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}([.,][0-9]+)?)?(Z|[+-][0-9]{2}(:[0-9]{2})?)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Formats <paramref name="utc"/> as <c>yyyy-MM-ddTHH:mm:ssZ</c> under the
    /// invariant culture. The caller is responsible for passing a UTC instant;
    /// the trailing <c>Z</c> is a literal designator, not a computed offset.
    /// </summary>
    internal static string FormatUtc(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// Classifies a §5 timestamp and, whenever it is readable at all, yields its
    /// instant. Reading stays permissive per §11 (a readable value is never
    /// dropped, whatever <see cref="TimestampForm"/> it lands in) — only the
    /// classification is strict about the §5 grammar.
    /// </summary>
    /// <param name="raw">The raw frontmatter value.</param>
    /// <param name="instant">
    /// The parsed instant, normalized to UTC, for every form except
    /// <see cref="TimestampForm.Unreadable"/> (where it is <c>default</c>).
    /// </param>
    internal static TimestampForm Classify(string raw, out DateTimeOffset instant)
    {
        // Carries an explicit offset and is readable at all: either the §5 form,
        // or a readable value that is not spelled ISO 8601. The spelling check
        // runs against the raw text, not the parsed value — DateTimeOffset does
        // not remember whether its source used "Z" or "z", "+02:00" or "+0200".
        if (HasExplicitOffset(raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var withOffset))
        {
            instant = withOffset.ToUniversalTime();
            return IsConformantSpelling(raw) ? TimestampForm.Conformant : TimestampForm.NonIso8601;
        }

        // Legacy: a bare YYYY-MM-DD calendar date, read as midnight UTC.
        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            instant = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return TimestampForm.LegacyDateOnly;
        }

        // Legacy: a datetime with no offset, assumed UTC.
        if (DateTime.TryParseExact(raw, ZonelessFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var naive))
        {
            instant = new DateTimeOffset(DateTime.SpecifyKind(naive, DateTimeKind.Utc), TimeSpan.Zero);
            return TimestampForm.LegacyDateOnly;
        }

        instant = default;
        return TimestampForm.Unreadable;
    }

    /// <summary>
    /// Parses a §5 timestamp. Reads the conformant form as-is, and — permissively,
    /// per §11 — also a bare <c>YYYY-MM-DD</c> (as midnight UTC), a zoneless
    /// ISO datetime (as UTC), and an offset-bearing value that is not spelled
    /// ISO 8601, setting <paramref name="isLegacyForm"/> for the first two so
    /// callers can warn without rejecting. A thin wrapper over
    /// <see cref="Classify"/>, kept for callers that only need the legacy/not
    /// distinction rather than the full four-way form.
    /// </summary>
    /// <param name="raw">The raw frontmatter value.</param>
    /// <param name="instant">The parsed instant, normalized to UTC.</param>
    /// <param name="isLegacyForm">True when the value parsed but not in the §5 form.</param>
    /// <returns>False when <paramref name="raw"/> is not a timestamp at all.</returns>
    internal static bool TryParse(string raw, out DateTimeOffset instant, out bool isLegacyForm)
    {
        var form = Classify(raw, out instant);
        isLegacyForm = form is TimestampForm.LegacyDateOnly;
        return form is not TimestampForm.Unreadable;
    }

    /// <summary>
    /// Whether <paramref name="raw"/> is a §5-conformant timestamp: an ISO 8601
    /// datetime, wholly extended format, carrying an explicit UTC offset.
    /// </summary>
    /// <param name="raw">The raw frontmatter value.</param>
    internal static bool IsConformant(string raw) => Classify(raw, out _) is TimestampForm.Conformant;

    /// <summary>
    /// Whether <paramref name="raw"/> matches the exact §5 grammar:
    /// <c>YYYY-MM-DDThh:mm[:ss[(.|,)s+]]offset</c>, <c>offset</c> being <c>Z</c>,
    /// a reduced-precision <c>±hh</c>, or an extended <c>±hh:mm</c> — the
    /// negative zero offset excepted (see <see cref="IsNegativeZeroOffset"/>).
    /// Every component is fixed-width and the designator's case is significant.
    /// Leading/trailing whitespace is trimmed before the check, deliberately:
    /// it matches <see cref="HasExplicitOffset"/>'s own trim, and is harmless
    /// in practice since a plain YAML scalar is already trimmed by the parser
    /// by the time it reaches here. Called only once the value is already
    /// known to parse, so an out-of-range component (month 13, hour 25, …) has
    /// already been turned away as <see cref="TimestampForm.Unreadable"/> by the
    /// time this runs — this method's only job is rejecting a spelling that
    /// parses but is not ISO 8601 (<c>2026-6-3T14:00:00Z</c>, a lowercase
    /// designator, or a basic-format offset like <c>+0200</c>).
    /// </summary>
    private static bool IsConformantSpelling(string raw)
    {
        var s = raw.AsSpan().Trim();
        return ConformantPattern.IsMatch(s) && !IsNegativeZeroOffset(s);
    }

    /// <summary>
    /// Whether the value's offset is a negative zero (<c>-00:00</c> or its
    /// reduced-precision <c>-00</c>). ISO 8601 forbids it — a zero difference
    /// from UTC carries a plus sign (ISO 8601:2004 §4.2.5.2, 2019 §4.3.13), so
    /// <c>Z</c> and <c>+00:00</c> are the conformant spellings and <c>-00:00</c>
    /// is not. RFC 3339 §4.3 does permit it, with its own "offset unknown"
    /// meaning — but <c>docs/spec/SPEC.md</c> cites no RFC (<c>grep -c RFC</c> →
    /// 0) and delegates to ISO 8601 itself, so the RFC's licence does not reach
    /// this grammar. Kept out of <see cref="ConformantPattern"/> rather than
    /// folded into it as a lookahead: the pattern's job is component shape, and
    /// a sign/zero interaction expressed as a negated group is far easier to
    /// misread than to state. Safe to test by suffix because it runs only on a
    /// value that already matched the pattern, whose last characters are
    /// therefore the offset.
    /// </summary>
    private static bool IsNegativeZeroOffset(ReadOnlySpan<char> s) =>
        s.EndsWith("-00:00", StringComparison.Ordinal) || s.EndsWith("-00", StringComparison.Ordinal);

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
