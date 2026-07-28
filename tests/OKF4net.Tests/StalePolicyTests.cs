// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

public class StalePolicyTests
{
    private static readonly Lifecycle FreshDoc = Lifecycle.From(null, "2026-08-01");   // stale on/after Aug 1
    private static readonly DateOnly Today = new(2026, 7, 27);
    private static readonly DateOnly WellPastStale = new(2026, 9, 1);

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
        Assert.True(policy.Admits(FreshDoc, new DateOnly(2026, 8, 5)));   // within grace
        Assert.False(policy.Admits(FreshDoc, new DateOnly(2026, 8, 20))); // beyond grace
    }

    [Fact]
    public void No_stale_after_is_always_admitted()
    {
        var noExpiry = Lifecycle.From(null, null);
        Assert.True(StalePolicy.Strict.Admits(noExpiry, WellPastStale));
    }
}
