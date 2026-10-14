// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// The emitter's two output rules (#189). The sharing rule
/// (<c>YamlEmitter.MaxRepeatedOutputChars</c>): a value built in code may hold
/// one collection, or one long string, in several places; the OKF YAML subset
/// has no anchors or aliases, so each reuse is written in full, and the
/// characters written for reuses are capped. The output rule
/// (<c>YamlEmitter.MaxOutputChars</c>): no text is longer than a fixed bound.
/// Ordinary reuse stays writable and round-trips; both limits are exact, and a
/// refusal comes before the line that would pass a limit, header lines
/// included. Most limits are reached here through
/// <c>YamlEmitter.EmitWithLimits</c>, with small values. The hostile shapes run
/// in a child process: see
/// <see cref="YamlHostileInputTests.Emitting_a_value_that_would_write_too_much_is_refused_in_a_clean_exit"/>.
/// </summary>
public class YamlEmitterSharingTests
{
    private const string SharedRefusal = "YAML emit error: " + YamlEmitter.SharedOutputExceededMessage;
    private const string OutputRefusal = "YAML emit error: " + YamlEmitter.OutputSizeExceededMessage;
    private const int Large = int.MaxValue;

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
    /// With the production limit: a root sequence holding one shared sequence
    /// S three times. The first occurrence is not a reuse; each of the other two
    /// writes S's content, <c>"  - " + n + "\n"</c>, n + 5 characters. So the
    /// reuses write 2n + 10 characters, each one alone well under the limit:
    /// exactly the limit is written, one more pair of characters is refused.
    /// </summary>
    [Fact]
    public void The_limit_is_exact_and_adds_up_over_separate_reuses()
    {
        var atLimit = (YamlEmitter.MaxRepeatedOutputChars - 10) / 2;
        Assert.Equal(YamlEmitter.MaxRepeatedOutputChars, (2 * atLimit) + 10);

        var written = ThreeTimes(atLimit).ToYamlString();
        Assert.Equal((3 * (atLimit + 5)) + (3 * 2), written.Length);

        var e = Assert.Throws<YamlEmitException>(() => ThreeTimes(atLimit + 1).ToYamlString());
        Assert.Equal(SharedRefusal, e.Message);

        static YamlSequence ThreeTimes(int n)
        {
            var s = Strings(new string('x', n));
            return new YamlSequence([s, s, s]);
        }
    }

    /// <summary>
    /// Sharing is told by reference, not by value: structurally equal but
    /// distinct collections, holding distinct strings, are never counted as
    /// reuses, however large.
    /// </summary>
    [Fact]
    public void Equal_but_distinct_collections_are_not_reuses()
    {
        var n = YamlEmitter.MaxRepeatedOutputChars;
        var value = new YamlSequence([Strings(new string('x', n)), Strings(new string('x', n))]);

        Assert.Equal(2 * ("-\n  - ".Length + n + 1), value.ToYamlString().Length);
    }

    /// <summary>
    /// One long .NET string written twice, through two <see cref="YamlString"/>
    /// objects: the second line, <c>"- " + n + "\n"</c>, counts.
    /// </summary>
    [Fact]
    public void A_long_string_written_again_counts_exactly()
    {
        const int limit = 2_000;
        Assert.Equal((2 * (limit - 3)) + (2 * 3), YamlEmitter.EmitWithLimits(Twice(limit - 3), limit, Large).Length);

        var e = Assert.Throws<YamlEmitException>(() => YamlEmitter.EmitWithLimits(Twice(limit - 2), limit, Large));
        Assert.Equal(SharedRefusal, e.Message);

        static YamlSequence Twice(int n)
        {
            var big = new string('x', n);
            return new YamlSequence([new YamlString(big), new YamlString(big)]);
        }
    }

    /// <summary>
    /// The threshold: a string one character shorter than
    /// <c>YamlEmitter.LongStringChars</c> is reused freely (it costs no more
    /// than that per slot, and the output rule bounds the total); one of that
    /// length counts.
    /// </summary>
    [Fact]
    public void Only_strings_of_the_threshold_length_or_more_count()
    {
        const int limit = 2_000;
        var shortText = YamlEmitter.EmitWithLimits(TenTimes(YamlEmitter.LongStringChars - 1), limit, Large);
        Assert.Equal(10 * (YamlEmitter.LongStringChars + 2), shortText.Length);

        var e = Assert.Throws<YamlEmitException>(() => YamlEmitter.EmitWithLimits(TenTimes(YamlEmitter.LongStringChars), limit, Large));
        Assert.Equal(SharedRefusal, e.Message);

        static YamlSequence TenTimes(int n) =>
            new(Enumerable.Repeat<YamlValue>(new YamlString(new string('x', n)), 10).ToList());
    }

    /// <summary>
    /// A long string used again as a mapping key counts too: two distinct
    /// one-entry mappings whose keys share one .NET string. The second
    /// mapping's line, <c>"  " + key + ": v\n"</c>, is n + 6 characters.
    /// </summary>
    [Fact]
    public void A_long_key_written_again_counts()
    {
        const int n = 1_500;
        var key = new string('k', n);
        var value = new YamlSequence([OneEntry(key), OneEntry(key)]);

        Assert.Equal(2 * (2 + n + 6), YamlEmitter.EmitWithLimits(value, n + 6, Large).Length);
        var e = Assert.Throws<YamlEmitException>(() => YamlEmitter.EmitWithLimits(value, n + 5, Large));
        Assert.Equal(SharedRefusal, e.Message);

        static YamlMapping OneEntry(string key)
        {
            var map = new YamlMapping();
            map.Insert(key, new YamlString("v"));
            return map;
        }
    }

    /// <summary>
    /// A refusal comes before the line that would pass the sharing limit, and a
    /// header line (<c>key:</c>, <c>-</c>) is such a line. A root sequence
    /// holds a 3-level chain S twice; its first repeated line is a 54-character
    /// header, over a sharing limit of 10. The output limit leaves room for
    /// that one line and not the next: an emitter that wrote the chain's
    /// headers before checking would pass the output limit first and report it
    /// instead.
    /// </summary>
    [Fact]
    public void A_refusal_is_never_late_on_header_lines()
    {
        var key = new string('a', 50);
        YamlValue chain = new YamlString("v");
        for (var i = 0; i < 3; i++)
        {
            var map = new YamlMapping();
            map.Insert(key, chain);
            chain = map;
        }

        var once = YamlEmitter.EmitWithLimits(new YamlSequence([chain]), Large, Large).Length;
        var outputLimit = once + "-\n".Length + "  ".Length + key.Length + ":\n".Length;

        var e = Assert.Throws<YamlEmitException>(
            () => YamlEmitter.EmitWithLimits(new YamlSequence([chain, chain]), 10, outputLimit));
        Assert.Equal(SharedRefusal, e.Message);
    }

    [Fact]
    public void The_output_limit_is_exact()
    {
        var value = Strings("abc", "def");
        Assert.Equal("- abc\n- def\n", YamlEmitter.EmitWithLimits(value, Large, 12));
        var e = Assert.Throws<YamlEmitException>(() => YamlEmitter.EmitWithLimits(value, Large, 11));
        Assert.Equal(OutputRefusal, e.Message);

        Assert.Equal("abc\n", YamlEmitter.EmitWithLimits(new YamlString("abc"), Large, 4));
        e = Assert.Throws<YamlEmitException>(() => YamlEmitter.EmitWithLimits(new YamlString("abc"), Large, 3));
        Assert.Equal(OutputRefusal, e.Message);
    }

    [Fact]
    public void The_refusals_are_OkfExceptions_and_state_their_limits()
    {
        Assert.IsAssignableFrom<OkfException>(new YamlEmitException(YamlEmitter.SharedOutputExceededMessage));
        Assert.Contains(
            "more than " + YamlEmitter.MaxRepeatedOutputChars.ToString(CultureInfo.InvariantCulture) + " characters",
            YamlEmitter.SharedOutputExceededMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            "a string of " + YamlEmitter.LongStringChars.ToString(CultureInfo.InvariantCulture) + " characters or more",
            YamlEmitter.SharedOutputExceededMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            "longer than " + YamlEmitter.MaxOutputChars.ToString(CultureInfo.InvariantCulture) + " characters",
            YamlEmitter.OutputSizeExceededMessage,
            StringComparison.Ordinal);
    }
}
