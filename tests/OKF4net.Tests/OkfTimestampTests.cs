// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;
using OKF4net.Internal;

namespace OKF4net.Tests;

/// <summary>
/// The §5 timestamp seam: <see cref="OkfTimestamp"/> is the one place this
/// library both writes and reads the form §5 mandates ("Every timestamp-valued
/// key in OKF is an ISO 8601 datetime with an explicit UTC offset"). Every
/// consumer — <c>stale_after</c>, <c>generated.at</c>, <c>verified[].at</c>,
/// <c>sources[].last_modified</c>, <c>usage_window.from</c>/<c>.to</c> — goes
/// through it, so the rule is spelled once.
/// </summary>
public class OkfTimestampTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void The_section_5_form_parses_and_is_not_legacy()
    {
        Assert.True(OkfTimestamp.TryParse("2026-06-30T14:00:00Z", out var instant, out var legacy));
        Assert.Equal(Utc(2026, 6, 30, 14, 0, 0), instant);
        Assert.False(legacy);
    }

    [Fact]
    public void A_non_utc_offset_is_normalized_to_utc_and_is_not_legacy()
    {
        Assert.True(OkfTimestamp.TryParse("2026-06-30T14:00:00+02:00", out var instant, out var legacy));
        Assert.Equal(Utc(2026, 6, 30, 12, 0, 0), instant);
        Assert.False(legacy);
    }

    [Fact]
    public void A_bare_date_is_read_as_midnight_utc_and_flagged_legacy()
    {
        Assert.True(OkfTimestamp.TryParse("2026-07-01", out var instant, out var legacy));
        Assert.Equal(Utc(2026, 7, 1), instant);
        Assert.True(legacy);
    }

    [Fact]
    public void A_zoneless_datetime_is_assumed_utc_and_flagged_legacy()
    {
        Assert.True(OkfTimestamp.TryParse("2026-07-01T12:00:00", out var instant, out var legacy));
        Assert.Equal(Utc(2026, 7, 1, 12, 0, 0), instant);
        Assert.True(legacy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-07-01T25:00:00Z")]
    public void Malformed_values_are_rejected(string raw)
    {
        Assert.False(OkfTimestamp.TryParse(raw, out _, out var legacy));
        Assert.False(legacy);
    }

    [Theory]
    [InlineData("01/02/2026")]
    [InlineData("2026")]
    [InlineData("July 1, 2026")]
    public void Culture_shaped_values_are_rejected_not_silently_accepted_as_legacy(string raw)
    {
        // The legacy fallback reads two shapes on purpose: a bare ISO date and
        // a zoneless ISO datetime. Widening it to DateTime.TryParse would turn
        // "malformed" into "legacy, assumed UTC" for values no OKF producer
        // ever writes, and the validator would stop reporting them.
        Assert.False(OkfTimestamp.TryParse(raw, out _, out _));
    }

    [Fact]
    public void Round_trips_with_FormatUtc()
    {
        var written = OkfTimestamp.FormatUtc(new DateTime(2026, 6, 30, 14, 0, 0, DateTimeKind.Utc));

        Assert.Equal("2026-06-30T14:00:00Z", written);
        Assert.True(OkfTimestamp.TryParse(written, out var instant, out var legacy));
        Assert.Equal(Utc(2026, 6, 30, 14, 0, 0), instant);
        Assert.False(legacy);
    }

    [Theory]
    [InlineData("2026-07-01T00:00:00Z", true)]
    [InlineData("2026-07-01T00:00:00+00:00", false)]
    [InlineData("2026-07-01", false)]
    [InlineData("2026-07-01T00:00:00.000Z", false)]
    public void IsEmittedUtcForm_accepts_exactly_what_FormatUtc_writes(string raw, bool expected)
    {
        Assert.Equal(expected, OkfTimestamp.IsEmittedUtcForm(raw));
        Assert.True(OkfTimestamp.IsEmittedUtcForm(OkfTimestamp.FormatUtc(new DateTime(2026, 7, 1, 12, 30, 0, DateTimeKind.Utc))));
    }

    /// <summary>
    /// The oracle the §5 grammar answers to: every timestamp literal the spec
    /// itself writes must classify <see cref="TimestampForm.Conformant"/>. A
    /// grammar derived purely by reasoning is exactly what produced the two
    /// defects this seam already had (a permissive parser silently accepting
    /// non-ISO-8601 spellings) — checking it against evidence the author does
    /// not control is the point. If this test ever fails, the grammar is wrong,
    /// not the spec: see <c>docs/superpowers/specs/2026-08-31-okf-timestamp-spelling-design.md</c>
    /// §4.
    /// </summary>
    [Fact]
    public void Every_timestamp_the_spec_itself_writes_is_conformant()
    {
        var specPath = Path.Combine(TestPaths.RepoRoot(), "docs", "spec", "SPEC.md");
        var spec = File.ReadAllText(specPath);

        var literals = Regex.Matches(spec, @"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+([Z]|[+-][0-9:]+)")
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        Assert.Equal(18, literals.Count);

        foreach (var literal in literals)
        {
            var form = OkfTimestamp.Classify(literal, out _);
            Assert.True(form is TimestampForm.Conformant, $"{literal} classified {form}, expected Conformant");
        }

        // Completeness guard. The extraction above is modelled on the §5 grammar
        // it validates, so it structurally cannot see a wholly-basic
        // (20260630T140000Z), week-date, ordinal-date, lowercase-z or
        // comma-fraction literal -- all of which ISO 8601 permits. Without this,
        // a future vendored spec could add one, keep 18 matching values, and the
        // new form would go unvalidated while the test stayed green: the oracle
        // would silently stop being an oracle.
        //
        // This scan is deliberately shape-agnostic -- any token carrying a time
        // and a zone designator, in any ISO 8601 form. Everything it finds must
        // already be covered above. The spec contains none of these today, which
        // is why the assertion is "empty" rather than a second expected list.
        var everyShape = Regex.Matches(
                spec,
                @"\b[0-9][0-9A-Za-z:,.\-]*[Tt][0-9][0-9:,.]*(?:[Zz]|[+-][0-9]{2}(?::?[0-9]{2})?)")
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        var unseen = everyShape.Except(literals).ToList();
        Assert.True(
            unseen.Count == 0,
            $"The spec now writes {unseen.Count} timestamp literal(s) the §5 extraction cannot see, so they are "
            + $"not being validated: {string.Join(", ", unseen)}. Widen the extraction, or state plainly in the "
            + "design doc that the oracle no longer covers every literal the spec writes.");
    }

    // xunit [Theory] data must be public, but TimestampForm is internal (see
    // the design doc §5.1: it deliberately stays internal rather than joining
    // the public surface for this one need) — so InlineData below carries the
    // expected form's name as a string and Classify_matches_the_expected_form
    // parses it back with Enum.Parse<TimestampForm> inside the method body,
    // where the internal type never has to appear in a public signature.
    [Theory]
    // The §5 form: fixed-width components, uppercase "Z" or an extended offset,
    // seconds optional, fraction optional.
    [InlineData("2026-06-30T14:00:00Z", nameof(TimestampForm.Conformant))]
    [InlineData("2026-06-30T14:00:00+02:00", nameof(TimestampForm.Conformant))]
    [InlineData("2026-05-28T22:53:05+00:00", nameof(TimestampForm.Conformant))]
    [InlineData("2026-06-30T14:00:00.123Z", nameof(TimestampForm.Conformant))]
    [InlineData("2026-06-30T14:00Z", nameof(TimestampForm.Conformant))]
    // Discovered while writing the battery: a negative offset and a
    // many-digit fraction are both grammar-legal and not covered above.
    [InlineData("2026-06-30T14:00:00-05:00", nameof(TimestampForm.Conformant))]
    [InlineData("2026-06-30T14:00:00.123456789Z", nameof(TimestampForm.Conformant))]
    // Fix round 1, finding 1: the comma decimal sign (ISO 8601 §4.2.2.4 names
    // it the *preferred* one) and the reduced-precision ±hh offset (no
    // minutes at all, so nothing to separate — not basic/extended mixing,
    // which is why +0200 below stays rejected) are both grammar-legal. The
    // original regex rejected both, which was stricter than ISO 8601 itself
    // — the same defect class ("false positive on conformant data") this
    // branch exists to fix.
    [InlineData("2026-06-30T14:00:00,123Z", nameof(TimestampForm.Conformant))]
    [InlineData("2026-06-30T14:00:00+02", nameof(TimestampForm.Conformant))]
    [InlineData("2026-06-30T14:00:00-05", nameof(TimestampForm.Conformant))]
    // Review of PR #108: the earliest representable instants. Both are
    // ordinary four-digit §5 timestamps that the readability gate accepts,
    // and the date-presence guard used to call them date-less because its
    // probe landed in the same year 1 that NoCurrentDateDefault substitutes
    // for a value with no date part at all.
    [InlineData("0001-01-01T00:00:00Z", nameof(TimestampForm.Conformant))]
    [InlineData("0001-01-02T01:00:00Z", nameof(TimestampForm.Conformant))]
    // Readable, offset-bearing, wrong spelling.
    // Fix round 2: the negative zero offset. ISO 8601 forbids it (2004
    // §4.2.5.2 / 2019 §4.3.13) — a zero difference from UTC takes a plus sign,
    // so "Z" and "+00:00" spell it and "-00:00" does not. Only RFC 3339 §4.3
    // permits it, and SPEC.md cites no RFC. Both precisions are covered
    // because the grammar accepts both. "+00:00" sits above as Conformant on
    // purpose: it is the spelling of one of the spec's own 18 literals
    // (2026-05-28T22:53:05+00:00), so the sign is what decides here, not the
    // zero. A negative *non*-zero offset stays Conformant ("-05:00" / "-05"
    // above): the rule is about the sign of zero, not about minus signs.
    [InlineData("2026-06-30T14:00:00-00:00", nameof(TimestampForm.NonIso8601))]
    [InlineData("2026-06-30T14:00:00-00", nameof(TimestampForm.NonIso8601))]
    [InlineData("2026-6-3T14:00:00Z", nameof(TimestampForm.NonIso8601))]
    [InlineData("2026-06-3T14:00:00Z", nameof(TimestampForm.NonIso8601))]
    [InlineData("2026-06-30T14:00:00z", nameof(TimestampForm.NonIso8601))]
    [InlineData("2026-06-30T14:00:00+0200", nameof(TimestampForm.NonIso8601))]
    // Pinned by the review of PR #108, where it is the row that keeps the
    // date-presence guard honest: its date part is real but is not written in
    // any ISO 8601 shape, so it is readable-but-misspelled and must stay so.
    // A guard that decided date-presence from the text alone would have to
    // call this date-less and demote it to Unreadable, silently moving a
    // value from one diagnostic to another; keeping the parse probe as the
    // primary test is what avoids that. IsConformantSpelling stays the sole
    // authority on spelling, here as everywhere.
    [InlineData("06/30/2026 14:00:00Z", nameof(TimestampForm.NonIso8601))]
    // "2026-06-30T4:00:00Z" (task-1-brief.md's table) is NOT here: settled by
    // execution, not by reading, per the task brief. DateTimeOffset.TryParse
    // accepts an unpadded month/day ("2026-6-3T…") but rejects an unpadded
    // *hour* outright — an asymmetry in the BCL's permissive parser, not in
    // this grammar. Classify's readability gate is deliberately the
    // pre-existing lenient DateTimeOffset.TryParse seam (unchanged by this
    // task), so an hour the BCL itself cannot parse is Unreadable, not
    // NonIso8601. See the task-1 report for the full verification.
    [InlineData("2026-06-30T4:00:00Z", nameof(TimestampForm.Unreadable))]
    // Discovered while writing the battery: the date/time separator's case
    // matters too (ISO 8601 fixes it as literal "T"), and a space in its
    // place is a different spelling that §5 does not accept as an alternative.
    [InlineData("2026-06-30t14:00:00Z", nameof(TimestampForm.NonIso8601))]
    [InlineData("2026-06-30 14:00:00Z", nameof(TimestampForm.NonIso8601))]
    // Legacy: readable, but no offset at all.
    [InlineData("2026-07-01", nameof(TimestampForm.LegacyDateOnly))]
    [InlineData("2026-07-01T12:00:00", nameof(TimestampForm.LegacyDateOnly))]
    [InlineData("2026-07-01 12:00:00", nameof(TimestampForm.LegacyDateOnly))]
    // Not a timestamp at all.
    [InlineData("", nameof(TimestampForm.Unreadable))]
    [InlineData("not-a-date", nameof(TimestampForm.Unreadable))]
    [InlineData("2026-13-01T00:00:00Z", nameof(TimestampForm.Unreadable))]
    [InlineData("2026-01-01T25:00:00Z", nameof(TimestampForm.Unreadable))]
    [InlineData("01/02/2026", nameof(TimestampForm.Unreadable))]
    [InlineData("2026", nameof(TimestampForm.Unreadable))]
    [InlineData("July 1, 2026", nameof(TimestampForm.Unreadable))]
    // Review of PR #108: a year-1 date is readable, a *malformed* year-1 date
    // is not. The date-presence guard settles the year-1 case from the raw
    // text, so this row pins that it never promotes an out-of-range component
    // into a readable value: the readability gate turns this away first,
    // exactly as it does for 2026-13-01 above.
    [InlineData("0001-13-45T00:00:00Z", nameof(TimestampForm.Unreadable))]
    public void Classify_matches_the_expected_form(string raw, string expected)
    {
        Assert.Equal(Enum.Parse<TimestampForm>(expected), OkfTimestamp.Classify(raw, out _));
    }

    [Fact]
    public void A_non_iso8601_spelling_still_yields_its_instant()
    {
        // §11 forbids dropping a readable value: an unpadded month/day is not
        // ISO 8601, but it is unambiguous, so the instant is still read.
        var form = OkfTimestamp.Classify("2026-6-3T14:00:00Z", out var instant);

        Assert.Equal(TimestampForm.NonIso8601, form);
        Assert.Equal(Utc(2026, 6, 3, 14, 0, 0), instant);
    }

    [Fact]
    public void A_comma_decimal_sign_is_conformant_and_yields_the_right_instant()
    {
        // Fix round 1, finding 1: settled by execution, not assumption —
        // DateTimeOffset.TryParse already accepts "," as a fraction separator
        // under InvariantCulture/RoundtripKind (verified with a throwaway
        // probe before this test was written), so no normalization step is
        // needed to make the value both Conformant and readable; the raw
        // string with its comma is exactly what both the readability parse
        // and IsConformantSpelling see.
        var form = OkfTimestamp.Classify("2026-06-30T14:00:00,123Z", out var instant);

        Assert.Equal(TimestampForm.Conformant, form);
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 14, 0, 0, 123, TimeSpan.Zero), instant);
    }

    [Fact]
    public void A_negative_zero_offset_is_flagged_but_still_yields_its_instant()
    {
        // §11 forbids dropping a readable value: -00:00 is unambiguous (the BCL
        // reads it as +00:00), so the instant is still read — only the spelling
        // is flagged.
        var form = OkfTimestamp.Classify("2026-06-30T14:00:00-00:00", out var instant);

        Assert.Equal(TimestampForm.NonIso8601, form);
        Assert.Equal(Utc(2026, 6, 30, 14, 0, 0), instant);
    }

    /// <summary>
    /// The known, deliberate limit of the readability gate, pinned so it cannot
    /// drift into a silent surprise: these are all genuine ISO 8601 datetimes
    /// with an explicit UTC offset, and <see cref="DateTimeOffset.TryParse(string, IFormatProvider, System.Globalization.DateTimeStyles, out DateTimeOffset)"/>
    /// reads none of them (verified by execution, not by reading the BCL docs).
    /// They therefore classify <see cref="TimestampForm.Unreadable"/> and yield
    /// no instant, so they are never evaluated for staleness. Adding them would
    /// be a parser rewrite, and no literal in <c>docs/spec/SPEC.md</c> uses any
    /// of these forms — see the design doc's "Out of scope" section. The
    /// validator's message for this bucket says only that the value could not be
    /// read, deliberately never that it is not ISO 8601, because of these rows.
    /// </summary>
    [Theory]
    [InlineData("20200630T140000Z")]          // wholly basic format
    [InlineData("2026-06-30T23:59:60Z")]      // leap second: ISO 8601 admits [60]
    [InlineData("2026-W27-1T14:00:00Z")]      // week date
    [InlineData("2026-181T14:00:00Z")]        // ordinal date
    public void Iso8601_forms_the_bcl_parser_cannot_read_are_Unreadable(string raw)
    {
        Assert.Equal(TimestampForm.Unreadable, OkfTimestamp.Classify(raw, out var instant));
        Assert.Equal(default, instant);
    }

    [Fact]
    public void End_of_day_24_00_is_Unreadable_and_that_is_correct_not_a_gap()
    {
        // Deliberately NOT a row in the theory above: end-of-day 24:00 is not an
        // ISO 8601 form this parser fails to read, it is a form ISO 8601 does not
        // permit here at all. ISO 8601 admits [24] for the hour "only to indicate
        // the end of a calendar day within a time interval", and forbids it "for
        // a single time point" -- which is exactly what §5.5 makes stale_after
        // ("An absolute instant"). So rejecting it is right, and it must not be
        // listed among the spellings we regret dropping.
        Assert.Equal(TimestampForm.Unreadable, OkfTimestamp.Classify("2020-06-30T24:00:00Z", out var instant));
        Assert.Equal(default, instant);
    }

    /// <summary>
    /// <see cref="DateTimeOffset.TryParse(string, IFormatProvider, System.Globalization.DateTimeStyles, out DateTimeOffset)"/>
    /// fills a missing date part with the machine's wall-clock date, so a
    /// time-only value would read as "today at that time" — staleness that
    /// flips within the day, differs per machine, and ignores
    /// <c>--as-of</c>. §5's grammar is a full <c>YYYY-MM-DDThh:mm[...]</c>
    /// datetime, so a value with no date part at all is not a §5 timestamp
    /// under any spelling and must classify Unreadable rather than being
    /// evaluated.
    /// </summary>
    [Theory]
    [InlineData("10:00Z")]
    [InlineData("10:00+02:00")]
    [InlineData("T10:00:00Z")]
    [InlineData("T10:00Z")]
    // The row that decides how the date-presence guard must be written, and
    // the one time-only spelling here that actually reaches it (the offset
    // sign sits past the tenth character, so the cheaper zone check ahead of
    // the guard routes it to the offset-bearing branch instead of turning it
    // away first — verified by execution, the shorter 23:00-02:00 does not
    // get that far). Its substituted year-1 date rolls over to 0001-01-02
    // under the negative offset, so "the probe stayed inside year 1" is the
    // sound negative test: "the probe landed exactly on the substituted
    // 0001-01-01" would let this one through as a readable year-1 instant.
    [InlineData("23:00:00.000-02:00")]
    public void A_value_without_a_date_is_unreadable_not_todays_date(string raw)
    {
        Assert.Equal(TimestampForm.Unreadable, OkfTimestamp.Classify(raw, out _));
    }

    /// <summary>
    /// Review of PR #108: <c>0001-01-01T00:00:00Z</c> is a valid four-digit §5
    /// timestamp, and one the readability gate parses, so it must keep its
    /// year-1 instant rather than be discarded because year 1 is also what
    /// <c>NoCurrentDateDefault</c> substitutes for a value with no date part.
    /// </summary>
    [Fact]
    public void The_earliest_representable_instant_is_conformant_and_keeps_its_instant()
    {
        var form = OkfTimestamp.Classify("0001-01-01T00:00:00Z", out var instant);

        Assert.Equal(TimestampForm.Conformant, form);
        Assert.Equal(Utc(1, 1, 1), instant);
        Assert.Equal(1, instant.Year);
    }

    /// <summary>
    /// The date-presence guard must not catch a value that genuinely has a
    /// date but is spelled non-ISO-8601 (a space instead of <c>T</c>) —
    /// that still classifies <see cref="TimestampForm.NonIso8601"/> and keeps
    /// its instant, exactly as before.
    /// </summary>
    [Fact]
    public void A_readable_non_iso_spelling_that_has_a_date_is_still_evaluated()
    {
        Assert.Equal(TimestampForm.NonIso8601, OkfTimestamp.Classify("2026-06-30 10:00Z", out var instant));
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 10, 0, 0, TimeSpan.Zero), instant);
    }
}
