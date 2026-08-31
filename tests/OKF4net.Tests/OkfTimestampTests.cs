// SPDX-License-Identifier: LGPL-3.0-or-later
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
}
