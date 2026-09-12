// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

public class ClockTests
{
    [Fact]
    public void FixedClock_returns_the_configured_date()
    {
        // Typed as IOkfClock: Today is a default interface member since the
        // Now/Today inversion, so it is reachable only through the interface,
        // not directly on the concrete FixedClock (which no longer declares it).
        IOkfClock clock = new FixedClock(new DateOnly(2026, 7, 27));
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
        IOkfClock clock = new FixedClock(new DateTimeOffset(2026, 7, 1, 14, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 7, 1, 14, 30, 0, TimeSpan.Zero), clock.Now);
        Assert.Equal(new DateOnly(2026, 7, 1), clock.Today);
    }

    [Fact]
    public void FixedClock_pinned_to_a_date_reports_midnight_UTC_as_Now()
    {
        IOkfClock clock = new FixedClock(new DateOnly(2026, 7, 1));

        Assert.Equal(new DateOnly(2026, 7, 1), clock.Today);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), clock.Now);
    }

    [Fact]
    public void FixedClock_normalizes_a_non_utc_instant()
    {
        // 2026-07-01T01:00+02:00 is 2026-06-30T23:00Z, so Today is June 30th.
        IOkfClock clock = new FixedClock(new DateTimeOffset(2026, 7, 1, 1, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(new DateTimeOffset(2026, 6, 30, 23, 0, 0, TimeSpan.Zero), clock.Now);
        Assert.Equal(new DateOnly(2026, 6, 30), clock.Today);
    }

    [Fact]
    public void Today_is_derived_from_Now()
    {
        IOkfClock clock = new NowOnlyClock(new DateTimeOffset(2026, 7, 1, 23, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 7, 1), clock.Today);
    }

    private sealed class NowOnlyClock(DateTimeOffset now) : IOkfClock
    {
        public DateTimeOffset Now { get; } = now;
    }
}
