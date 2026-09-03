// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

public class ClockTests
{
    [Fact]
    public void FixedClock_returns_the_configured_date()
    {
        var clock = new FixedClock(new DateOnly(2026, 7, 27));
        Assert.Equal(new DateOnly(2026, 7, 27), clock.Today);
    }

    [Fact]
    public void SystemClock_returns_today_utc()
    {
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.Date), new SystemClock().Today);
    }

    [Fact]
    public void FixedClock_pinned_to_an_instant_exposes_both_Now_and_Today()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 1, 14, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 14, 30, 0, TimeSpan.Zero), clock.Now);
        Assert.Equal(new DateOnly(2026, 7, 1), clock.Today);
    }

    [Fact]
    public void FixedClock_pinned_to_a_date_reports_midnight_UTC_as_Now()
    {
        var clock = new FixedClock(new DateOnly(2026, 7, 1));

        Assert.Equal(new DateOnly(2026, 7, 1), clock.Today);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), clock.Now);
    }

    [Fact]
    public void FixedClock_normalizes_a_non_utc_instant()
    {
        // 2026-07-01T01:00+02:00 is 2026-06-30T23:00Z, so Today is June 30th.
        var clock = new FixedClock(new DateTimeOffset(2026, 7, 1, 1, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(new DateTimeOffset(2026, 6, 30, 23, 0, 0, TimeSpan.Zero), clock.Now);
        Assert.Equal(new DateOnly(2026, 6, 30), clock.Today);
    }

    [Fact]
    public void A_clock_implementing_only_Today_still_satisfies_the_interface()
    {
        // Guards the default interface member: an external implementer written
        // against the pre-fix interface must keep compiling and working.
        IOkfClock clock = new TodayOnlyClock(new DateOnly(2026, 7, 1));

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), clock.Now);
    }

    private sealed class TodayOnlyClock(DateOnly today) : IOkfClock
    {
        public DateOnly Today { get; } = today;
    }
}
