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
}
