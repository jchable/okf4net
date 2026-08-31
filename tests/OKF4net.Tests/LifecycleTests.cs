// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

public class LifecycleTests
{
    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, TimeSpan.Zero);

    [Theory]
    [InlineData(null, ConceptStatus.Stable, true)]
    [InlineData("draft", ConceptStatus.Draft, true)]
    [InlineData("stable", ConceptStatus.Stable, true)]
    [InlineData("deprecated", ConceptStatus.Deprecated, true)]
    [InlineData("archived", ConceptStatus.Stable, false)] // unknown ⇒ Stable, not known
    public void Status_parses_with_default_and_unknown(string? raw, ConceptStatus expected, bool known)
    {
        var lc = Lifecycle.From(raw, null);
        Assert.Equal(expected, lc.Status);
        Assert.Equal(known, lc.StatusIsKnown);
    }

    [Fact]
    public void IsStale_true_when_now_at_or_after_stale_after()
    {
        var lc = Lifecycle.From(null, "2026-07-01");
        Assert.True(lc.IsStale(Utc(2026, 7, 1)));   // boundary: now == stale_after
        Assert.True(lc.IsStale(Utc(2026, 7, 27)));
        Assert.False(lc.IsStale(Utc(2026, 6, 30)));
    }

    [Fact]
    public void IsStale_false_when_stale_after_absent()
        => Assert.False(Lifecycle.From(null, null).IsStale(Utc(2030, 1, 1)));

    [Fact]
    public void Malformed_stale_after_is_flagged_and_never_stale()
    {
        var lc = Lifecycle.From(null, "not-a-date");
        Assert.True(lc.StaleAfterMalformed);
        Assert.Null(lc.StaleAfter);
        Assert.False(lc.IsStale(Utc(2030, 1, 1)));
    }

    [Fact]
    public void Conformant_instant_stale_after_is_parsed_and_goes_stale()
    {
        // §5: "Every timestamp-valued key in OKF is an ISO 8601 datetime with
        // an explicit UTC offset". Before this fix the value below failed to
        // parse, so IsStale silently returned false forever.
        var lc = Lifecycle.From(null, "2026-06-30T14:00:00Z");

        Assert.False(lc.StaleAfterMalformed);
        Assert.False(lc.StaleAfterIsLegacyDate);
        Assert.Equal(Utc(2026, 6, 30, 14, 0, 0), lc.StaleAfter);
        Assert.True(lc.IsStale(Utc(2026, 6, 30, 14, 0, 0)));   // §5.5 boundary is inclusive
        Assert.True(lc.IsStale(Utc(2026, 7, 1)));
        Assert.False(lc.IsStale(Utc(2026, 6, 30, 13, 59, 59)));
    }

    [Fact]
    public void Non_utc_offset_is_honoured_not_ignored()
    {
        // 2026-06-30T14:00:00+02:00 is 12:00Z, so 13:00Z is already past it.
        var lc = Lifecycle.From(null, "2026-06-30T14:00:00+02:00");

        Assert.False(lc.StaleAfterIsLegacyDate);
        Assert.True(lc.IsStale(Utc(2026, 6, 30, 13, 0, 0)));
        Assert.False(lc.IsStale(Utc(2026, 6, 30, 11, 0, 0)));
    }

    [Fact]
    public void Legacy_date_only_stale_after_still_parses_and_is_flagged()
    {
        var lc = Lifecycle.From(null, "2026-07-01");

        Assert.False(lc.StaleAfterMalformed);
        Assert.True(lc.StaleAfterIsLegacyDate);
        Assert.Equal(Utc(2026, 7, 1), lc.StaleAfter);
        Assert.Equal(new DateOnly(2026, 7, 1), lc.StaleAfterDate);
    }

    [Fact]
    public void Legacy_date_only_keeps_the_inclusive_midnight_boundary()
    {
        // A date-only stale_after normalizes to midnight UTC, so the day it
        // names is stale from its first instant — the boundary CliTests,
        // AuditTests and OkfAuditToolTests all lock.
        var lc = Lifecycle.From(null, "2026-07-01");

        Assert.True(lc.IsStale(Utc(2026, 7, 1)));
        Assert.False(lc.IsStale(Utc(2026, 6, 30, 23, 59, 59)));
    }

    [Fact]
    public void An_instant_stale_after_is_not_stale_earlier_that_same_day()
    {
        // The whole point of the fix: within-day precision. A concept expiring
        // at 23:00Z is fresh at 09:00Z on the same date, and stale at 23:00Z.
        var lc = Lifecycle.From(null, "2026-07-01T23:00:00Z");

        Assert.False(lc.IsStale(Utc(2026, 7, 1, 9, 0, 0)));
        Assert.True(lc.IsStale(Utc(2026, 7, 1, 23, 0, 0)));
        Assert.Equal(new DateOnly(2026, 7, 1), lc.StaleAfterDate);
    }

    [Theory]
    [InlineData("not-a-date")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-07-01T25:00:00Z")]
    [InlineData("")]
    public void Genuinely_malformed_stale_after_is_still_rejected(string raw)
    {
        var lc = Lifecycle.From(null, raw);

        Assert.True(lc.StaleAfterMalformed);
        Assert.False(lc.StaleAfterIsLegacyDate);
        Assert.Null(lc.StaleAfter);
        Assert.Null(lc.StaleAfterDate);
        Assert.False(lc.IsStale(Utc(2030, 1, 1)));
    }

    [Fact]
    public void A_datetime_without_an_offset_is_treated_as_UTC_and_flagged_legacy()
    {
        // §5 requires an explicit offset. We still read it (permissive, §11),
        // assume UTC, and flag it so the validator can warn.
        var lc = Lifecycle.From(null, "2026-07-01T12:00:00");

        Assert.False(lc.StaleAfterMalformed);
        Assert.True(lc.StaleAfterIsLegacyDate);
        Assert.Equal(Utc(2026, 7, 1, 12, 0, 0), lc.StaleAfter);
    }

    [Fact]
    public void An_absent_stale_after_is_neither_malformed_nor_legacy()
    {
        var lc = Lifecycle.From(null, null);

        Assert.False(lc.StaleAfterMalformed);
        Assert.False(lc.StaleAfterIsLegacyDate);
        Assert.Null(lc.StaleAfterDate);
    }
}
