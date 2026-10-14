// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// The emitter's sharing rule (#189, <c>YamlEmitter.MaxRepeatedOutputChars</c>):
/// a value built in code may hold one collection object in several places; the
/// OKF YAML subset has no anchors or aliases, so each reuse is written in full,
/// and the characters written for reuses are capped. Ordinary reuse stays
/// writable and round-trips; the cap is exact and adds up over separate
/// reuses. The hostile shapes (a 60-level diamond chain, one collection held
/// 100 000 times) run in a child process: see
/// <see cref="YamlHostileInputTests.Emitting_a_value_that_reuses_collections_is_refused_in_a_clean_exit"/>.
/// </summary>
public class YamlEmitterSharingTests
{
    private static YamlSequence Strings(params string[] items) => new([.. items.Select(i => (YamlValue)new YamlString(i))]);

    [Fact]
    public void A_sequence_and_a_mapping_reused_under_two_keys_are_written_like_two_copies()
    {
        var tags = Strings("a", "b");
        var window = new YamlMapping();
        window.Insert("from", new YamlString("2026-01-01"));
        var shared = new YamlMapping();
        shared.Insert("tags", tags);
        shared.Insert("keywords", tags);
        shared.Insert("usage_window", window);
        shared.Insert("previous_window", window);

        var copies = new YamlMapping();
        copies.Insert("tags", Strings("a", "b"));
        copies.Insert("keywords", Strings("a", "b"));
        var w1 = new YamlMapping();
        w1.Insert("from", new YamlString("2026-01-01"));
        var w2 = new YamlMapping();
        w2.Insert("from", new YamlString("2026-01-01"));
        copies.Insert("usage_window", w1);
        copies.Insert("previous_window", w2);

        var text = shared.ToYamlString();
        Assert.Equal(copies.ToYamlString(), text);
        Assert.Equal(
            "tags:\n  - a\n  - b\nkeywords:\n  - a\n  - b\n"
            + "usage_window:\n  from: 2026-01-01\nprevious_window:\n  from: 2026-01-01\n",
            text);
        Assert.Equal(shared, YamlValue.Parse(text));
    }

    [Fact]
    public void A_small_diamond_chain_is_written_in_full_and_round_trips()
    {
        // 3 levels, each holding the same next level under l and r: 8 leaves.
        YamlValue inner = new YamlString("v");
        for (var i = 0; i < 3; i++)
        {
            var map = new YamlMapping();
            map.Insert("l", inner);
            map.Insert("r", inner);
            inner = map;
        }

        var text = inner.ToYamlString();
        Assert.Equal(8, text.Split('\n').Count(l => l.EndsWith(": v", StringComparison.Ordinal)));
        Assert.Equal(inner, YamlValue.Parse(text));
    }

    /// <summary>
    /// A root sequence holding one shared sequence S three times. The first
    /// occurrence is not a reuse; each of the other two writes S's content,
    /// <c>"  - " + n + "\n"</c>, n + 5 characters. So the reuses write
    /// 2n + 10 characters, each one alone well under the limit: exactly the
    /// limit is written, one more pair of characters is refused.
    /// </summary>
    [Fact]
    public void The_limit_is_exact_and_adds_up_over_separate_reuses()
    {
        var atLimit = (YamlEmitter.MaxRepeatedOutputChars - 10) / 2;
        Assert.Equal(YamlEmitter.MaxRepeatedOutputChars, (2 * atLimit) + 10);

        var written = ThreeTimes(atLimit).ToYamlString();
        Assert.Equal((3 * (atLimit + 5)) + (3 * 2), written.Length);

        var e = Assert.Throws<YamlEmitException>(() => ThreeTimes(atLimit + 1).ToYamlString());
        Assert.Equal("YAML emit error: " + YamlEmitter.SharedOutputExceededMessage, e.Message);

        static YamlSequence ThreeTimes(int n)
        {
            var s = Strings(new string('x', n));
            return new YamlSequence([s, s, s]);
        }
    }

    [Fact]
    public void The_refusal_is_an_OkfException_and_states_the_limit()
    {
        Assert.IsAssignableFrom<OkfException>(new YamlEmitException(YamlEmitter.SharedOutputExceededMessage));
        Assert.Contains(
            "more than " + YamlEmitter.MaxRepeatedOutputChars.ToString(CultureInfo.InvariantCulture) + " characters",
            YamlEmitter.SharedOutputExceededMessage,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Sharing is told by reference, not by value: structurally equal but
    /// distinct collections are never counted as reuses, however large.
    /// </summary>
    [Fact]
    public void Equal_but_distinct_collections_are_not_reuses()
    {
        var n = YamlEmitter.MaxRepeatedOutputChars;
        var big = new string('x', n);
        var value = new YamlSequence([Strings(big), Strings(big)]);

        Assert.Equal(2 * ("-\n  - ".Length + n + 1), value.ToYamlString().Length);
    }
}
