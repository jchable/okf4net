// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

public class LifecycleTests
{
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
    public void IsStale_true_when_today_at_or_after_stale_after()
    {
        var lc = Lifecycle.From(null, "2026-07-01");
        Assert.True(lc.IsStale(new DateOnly(2026, 7, 1)));  // boundary: today == stale_after
        Assert.True(lc.IsStale(new DateOnly(2026, 7, 27)));
        Assert.False(lc.IsStale(new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public void IsStale_false_when_stale_after_absent()
        => Assert.False(Lifecycle.From(null, null).IsStale(new DateOnly(2030, 1, 1)));

    [Fact]
    public void Malformed_stale_after_is_flagged_and_never_stale()
    {
        var lc = Lifecycle.From(null, "not-a-date");
        Assert.True(lc.StaleAfterMalformed);
        Assert.Null(lc.StaleAfter);
        Assert.False(lc.IsStale(new DateOnly(2030, 1, 1)));
    }
}
