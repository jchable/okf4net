// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

public class StalePolicyTests
{
    private static readonly Lifecycle FreshDoc = Lifecycle.From(null, "2026-08-01");   // stale on/after Aug 1
    private static readonly DateTimeOffset Today = Utc(2026, 7, 27);
    private static readonly DateTimeOffset WellPastStale = Utc(2026, 9, 1);

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0)
        => new(year, month, day, hour, minute, second, TimeSpan.Zero);

    [Fact]
    public void Default_policy_is_Use_and_admits_stale()
    {
        Assert.Equal(StaleMode.Use, default(StalePolicy).Mode);
        Assert.True(default(StalePolicy).Admits(FreshDoc, WellPastStale));
    }

    [Fact]
    public void Strict_excludes_stale_but_keeps_fresh()
    {
        Assert.True(StalePolicy.Strict.Admits(FreshDoc, Today));          // not yet stale
        Assert.False(StalePolicy.Strict.Admits(FreshDoc, WellPastStale)); // stale → excluded
    }

    [Fact]
    public void Tolerate_admits_within_grace_and_excludes_beyond()
    {
        var policy = StalePolicy.Tolerate(10); // stale_after 2026-08-01 + 10d = 2026-08-11
        Assert.True(policy.Admits(FreshDoc, Utc(2026, 8, 5)));   // within grace
        Assert.False(policy.Admits(FreshDoc, Utc(2026, 8, 20))); // beyond grace
    }

    [Fact]
    public void No_stale_after_is_always_admitted()
    {
        var noExpiry = Lifecycle.From(null, null);
        Assert.True(StalePolicy.Strict.Admits(noExpiry, WellPastStale));
    }

    [Fact]
    public void Tolerate_counts_grace_days_from_the_instant_not_the_date()
    {
        // stale_after 2026-08-01T18:00Z + 10 days of grace = 2026-08-11T18:00Z.
        var lc = Lifecycle.From(null, "2026-08-01T18:00:00Z");
        var policy = StalePolicy.Tolerate(10);

        Assert.True(policy.Admits(lc, Utc(2026, 8, 11, 17, 59, 0)));
        Assert.False(policy.Admits(lc, Utc(2026, 8, 11, 18, 0, 1)));
    }

    [Fact]
    public void Strict_excludes_a_concept_past_a_conformant_stale_after()
    {
        var lc = Lifecycle.From(null, "2026-08-01T00:00:00Z");

        Assert.False(StalePolicy.Strict.Admits(lc, Utc(2026, 8, 2)));
        Assert.True(StalePolicy.Strict.Admits(lc, Utc(2026, 7, 31)));
    }

    [Fact]
    public void Use_admits_everything_including_a_conformant_stale_concept()
    {
        var lc = Lifecycle.From(null, "2020-01-01T00:00:00Z");

        Assert.True(StalePolicy.Use.Admits(lc, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_malformed_stale_after_is_admitted_by_every_mode()
    {
        // The validator owns that diagnostic; a policy must not silently drop a
        // concept because its stamp was unreadable.
        var bad = Lifecycle.From(null, "not-a-date");

        Assert.True(StalePolicy.Strict.Admits(bad, WellPastStale));
        Assert.True(StalePolicy.Tolerate(0).Admits(bad, WellPastStale));
    }
}
