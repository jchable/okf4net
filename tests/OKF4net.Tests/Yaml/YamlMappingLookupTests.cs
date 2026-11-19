// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// Pins the lookup semantics of <see cref="YamlMapping"/> with duplicate keys
/// and across mutations (#198), so replacing its linear key search by a lazily
/// built index cannot change what any accessor returns.
/// </summary>
public class YamlMappingLookupTests
{
    private static YamlMapping Parsed(params (YamlValue Key, YamlValue Value)[] pairs)
    {
        var m = new YamlMapping();
        foreach (var (k, v) in pairs)
        {
            m.PushRaw(k, v);
        }

        return m;
    }

    private static YamlString S(string s) => new(s);

    private static long? I(YamlValue? v) => v?.AsInt();

    [Fact]
    public void Get_returns_the_first_of_duplicate_keys()
    {
        var m = Parsed((S("a"), new YamlInt(1)), (S("b"), new YamlInt(2)), (S("a"), new YamlInt(3)));
        Assert.Equal(1, I(m.Get("a")));
        Assert.Equal(2, I(m.Get("b")));
        Assert.Null(m.Get("c"));
        Assert.True(m.ContainsKey("a"));
        Assert.False(m.ContainsKey("c"));
    }

    [Fact]
    public void Non_string_keys_are_never_matched_by_a_string()
    {
        var m = Parsed((new YamlInt(1), new YamlInt(10)), (S("1"), new YamlInt(11)));
        Assert.Equal(11, I(m.Get("1")));
        Assert.Equal(2, m.Count);
        Assert.Equal(["1"], m.Keys.ToArray());
    }

    [Fact]
    public void Insert_replaces_the_first_duplicate_in_place_and_returns_the_previous_value()
    {
        var m = Parsed((S("a"), new YamlInt(1)), (S("b"), new YamlInt(2)), (S("a"), new YamlInt(3)));
        var previous = m.Insert("a", new YamlInt(9));
        Assert.Equal(1, I(previous));
        Assert.Equal(["a", "b", "a"], m.Keys.ToArray());
        Assert.Equal([9L, 2L, 3L], m.Entries.Select(e => e.Value.AsInt()!.Value).ToArray());
        Assert.Equal(9, I(m.Get("a")));
    }

    [Fact]
    public void Insert_of_a_new_key_appends_and_returns_null()
    {
        var m = new YamlMapping();
        Assert.Null(m.Insert("a", new YamlInt(1)));
        Assert.Null(m.Insert("b", new YamlInt(2)));
        Assert.Equal(["a", "b"], m.Keys.ToArray());
        Assert.Equal(2, I(m.Get("b")));
    }

    [Fact]
    public void Remove_takes_the_first_duplicate_and_the_later_one_becomes_visible()
    {
        var m = Parsed((S("a"), new YamlInt(1)), (S("b"), new YamlInt(2)), (S("a"), new YamlInt(3)));
        Assert.Equal(1, I(m.Get("a"))); // builds any lazy index before the mutation
        Assert.Equal(1, I(m.Remove("a")));
        Assert.Equal(3, I(m.Get("a")));
        Assert.Equal(["b", "a"], m.Keys.ToArray());
        Assert.Equal(3, I(m.Remove("a")));
        Assert.Null(m.Get("a"));
        Assert.False(m.ContainsKey("a"));
        Assert.Null(m.Remove("a"));
        Assert.Equal(2, I(m.Get("b")));
    }

    [Fact]
    public void Remove_shifts_the_positions_of_the_keys_after_it()
    {
        var m = new YamlMapping();
        m.Insert("a", new YamlInt(1));
        m.Insert("b", new YamlInt(2));
        m.Insert("c", new YamlInt(3));
        Assert.Equal(3, I(m.Get("c")));
        m.Remove("a");
        Assert.Equal(3, I(m.Get("c")));
        Assert.Equal(2, I(m.Insert("b", new YamlInt(20))));
        Assert.Equal(["b", "c"], m.Keys.ToArray());
        Assert.Equal([20L, 3L], m.Entries.Select(e => e.Value.AsInt()!.Value).ToArray());
    }

    [Fact]
    public void Remove_then_Insert_appends_at_the_end()
    {
        var m = new YamlMapping();
        m.Insert("a", new YamlInt(1));
        m.Insert("b", new YamlInt(2));
        Assert.Equal(1, I(m.Get("a")));
        m.Remove("a");
        Assert.Null(m.Insert("a", new YamlInt(5)));
        Assert.Equal(["b", "a"], m.Keys.ToArray());
        Assert.Equal(5, I(m.Get("a")));
    }

    [Fact]
    public void PushRaw_after_a_lookup_is_seen_and_a_duplicate_does_not_shadow_the_first()
    {
        var m = Parsed((S("a"), new YamlInt(1)));
        Assert.Equal(1, I(m.Get("a")));
        m.PushRaw(S("b"), new YamlInt(2));
        m.PushRaw(S("a"), new YamlInt(3));
        m.PushRaw(new YamlInt(7), new YamlInt(4));
        Assert.Equal(2, I(m.Get("b")));
        Assert.Equal(1, I(m.Get("a")));
        Assert.Equal(4, m.Count);
        Assert.Equal(1, I(m.Insert("a", new YamlInt(8))));
        Assert.Equal(8, m.Entries[0].Value.AsInt());
        Assert.Equal(3, m.Entries[2].Value.AsInt());
    }

    [Fact]
    public void Replacing_in_place_keeps_later_lookups_correct()
    {
        var m = new YamlMapping();
        m.Insert("a", new YamlInt(1));
        m.Insert("b", new YamlInt(2));
        Assert.Equal(2, I(m.Get("b")));
        m.Insert("a", new YamlInt(10));
        Assert.Equal(10, I(m.Get("a")));
        Assert.Equal(2, I(m.Get("b")));
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public void Key_comparison_is_ordinal()
    {
        var m = new YamlMapping();
        m.Insert("a", new YamlInt(1));
        m.Insert("A", new YamlInt(2));
        m.Insert("e\u0301", new YamlInt(3));
        m.Insert("\u00e9", new YamlInt(4));
        Assert.Equal(4, m.Count);
        Assert.Equal(2, I(m.Get("A")));
        Assert.Equal(3, I(m.Get("e\u0301")));
        Assert.Equal(4, I(m.Get("\u00e9")));
    }

    [Fact]
    public void An_empty_key_is_a_key()
    {
        var m = new YamlMapping();
        m.Insert("", new YamlInt(1));
        Assert.Equal(1, I(m.Get("")));
        Assert.Equal(1, I(m.Remove("")));
        Assert.True(m.IsEmpty);
    }

    [Fact]
    public void Building_and_reading_a_large_mapping_does_a_linear_amount_of_key_work()
    {
        // Counted, never timed: a quadratic search does n(n-1)/2 probes here.
        const int n = 50_000;
        var m = new YamlMapping();
        for (var i = 0; i < n; i++)
        {
            Assert.Null(m.Insert("k" + i, new YamlInt(i)));
        }

        for (var i = 0; i < n; i++)
        {
            Assert.Equal(i, m.Get("k" + i)!.AsInt());
        }

        Assert.True(m.ContainsKey("k0"));
        Assert.Equal(n, m.Count);
        Assert.True(m.KeyProbes <= 8L * n, $"{m.KeyProbes} key probes for {n} keys");
    }

    [Fact]
    public void Concurrent_readers_of_an_unmutated_mapping_all_see_the_first_duplicate()
    {
        var m = new YamlMapping();
        for (var i = 0; i < 2000; i++)
        {
            m.PushRaw(S("k" + (i % 1000)), new YamlInt(i));
        }

        var bad = 0;
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 1000; i++)
            {
                if (m.Get("k" + i)?.AsInt() != i)
                {
                    Interlocked.Increment(ref bad);
                }
            }
        });
        Assert.Equal(0, bad);
    }
}
