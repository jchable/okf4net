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
    // Readable, offset-bearing, wrong spelling.
    [InlineData("2026-6-3T14:00:00Z", nameof(TimestampForm.NonIso8601))]
    [InlineData("2026-06-3T14:00:00Z", nameof(TimestampForm.NonIso8601))]
    [InlineData("2026-06-30T14:00:00z", nameof(TimestampForm.NonIso8601))]
    [InlineData("2026-06-30T14:00:00+0200", nameof(TimestampForm.NonIso8601))]
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
}
