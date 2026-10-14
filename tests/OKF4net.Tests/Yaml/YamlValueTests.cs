// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

public class YamlValueTests
{
    [Fact]
    public void Mapping_preserves_insertion_order()
    {
        var m = new YamlMapping();
        m.Insert("zebra", new YamlInt(1));
        m.Insert("alpha", new YamlInt(2));
        m.Insert("mike", new YamlInt(3));
        Assert.Equal(new[] { "zebra", "alpha", "mike" }, m.Keys.ToArray());
    }

    [Fact]
    public void Mapping_insert_replaces_in_place_and_returns_previous()
    {
        var m = new YamlMapping();
        m.Insert("a", new YamlInt(1));
        m.Insert("b", new YamlInt(2));
        var previous = m.Insert("a", new YamlInt(99));
        Assert.Equal(1L, ((YamlInt)previous!).Value);
        Assert.Equal(new[] { "a", "b" }, m.Keys.ToArray()); // "a" garde sa position
        Assert.Equal(99L, m.Get("a")!.AsInt());
    }

    [Fact]
    public void Mapping_remove_returns_value_and_forgets_key()
    {
        var m = new YamlMapping();
        m.Insert("k", new YamlString("v"));
        Assert.Equal("v", ((YamlString)m.Remove("k")!).Value);
        Assert.False(m.ContainsKey("k"));
        Assert.Null(m.Remove("k"));
    }

    [Fact]
    public void Scalar_accessors_return_null_on_wrong_kind()
    {
        YamlValue v = new YamlString("hello");
        Assert.Equal("hello", v.AsString());
        Assert.Null(v.AsInt());
        Assert.Null(v.AsBool());
        Assert.Null(v.AsMapping());
    }

    [Fact]
    public void ToString_matches_ToYamlString()
    {
        // The ToString override must produce exactly the same text as
        // ToYamlString.
        var v = YamlValue.Parse("a: 1\n");
        Assert.Equal(v.ToYamlString(), v.ToString());
        Assert.Equal(v.ToYamlString(), $"{v}");
    }

    // --- F14: implicit conversions from string/bool/long/array to
    // YamlValue. There is intentionally no conversion from double. ---

    [Fact]
    public void Implicit_conversion_from_string_yields_YamlString()
    {
        YamlValue v = "x";
        var s = Assert.IsType<YamlString>(v);
        Assert.Equal("x", s.Value);
    }

    [Fact]
    public void Implicit_conversion_from_bool_yields_YamlBool()
    {
        YamlValue b = true;
        var bv = Assert.IsType<YamlBool>(b);
        Assert.True(bv.Value);
    }

    [Fact]
    public void Implicit_conversion_from_long_yields_YamlInt()
    {
        YamlValue i = 42L;
        var iv = Assert.IsType<YamlInt>(i);
        Assert.Equal(42L, iv.Value);
    }

    [Fact]
    public void Implicit_conversion_from_array_yields_YamlSequence()
    {
        YamlValue seq = new YamlValue[] { "a", 1L };
        var sv = Assert.IsType<YamlSequence>(seq);
        Assert.Equal(2, sv.Items.Count);
        Assert.Equal("a", ((YamlString)sv.Items[0]).Value);
        Assert.Equal(1L, ((YamlInt)sv.Items[1]).Value);
    }

    [Fact]
    public void Mapping_insert_compiles_via_implicit_conversion()
    {
        var m = new YamlMapping();
        m.Insert("k", "v");
        Assert.Equal("v", m.Get("k")!.AsString());
    }

    [Fact]
    public void Sequence_constructor_defensively_copies_the_backing_list()
    {
        // The constructor must not alias the caller's list: it defensively
        // copies its input, so post-construction mutation of the caller's
        // list is not observable through the sequence.
        var list = new List<YamlValue> { new YamlInt(1), new YamlInt(2) };
        var seq = new YamlSequence(list);

        list.Add(new YamlInt(3));
        list[0] = new YamlInt(99);

        Assert.Equal(2, seq.Items.Count);
        Assert.Equal(1L, seq.Items[0].AsInt());
        Assert.Equal(2L, seq.Items[1].AsInt());
        Assert.Equal("- 1\n- 2\n", seq.ToYamlString());
    }

    [Fact]
    public void Entries_is_not_downcast_mutable_to_the_backing_list()
    {
        // A consumer must not be able to `(List<...>)mapping.Entries).Add(...)`
        // and desync the mapping's internal `_index`/lookup state -- Entries
        // has to be a genuinely read-only view, not the internal List exposed
        // as IEnumerable.
        var m = new YamlMapping();
        m.Insert("a", new YamlInt(1));

        Assert.IsNotType<List<(YamlValue Key, YamlValue Value)>>(m.Entries, exactMatch: false);

        var view = m.Entries;
        Assert.Single(view);
        m.Insert("b", new YamlInt(2));
        Assert.Equal(2, m.Entries.Count);
    }

    [Fact]
    public void IsEmptyValue_treats_falsy_scalars_and_empty_collections_as_empty()
    {
        Assert.True(YamlNull.Instance.IsEmptyValue);
        Assert.True(new YamlString("").IsEmptyValue);
        Assert.True(new YamlSequence([]).IsEmptyValue);
        Assert.True(new YamlMapping().IsEmptyValue);
        Assert.True(new YamlInt(0).IsEmptyValue);
        Assert.True(new YamlBool(false).IsEmptyValue);
        Assert.False(new YamlBool(true).IsEmptyValue);
        Assert.False(new YamlFloat(0.0).IsEmptyValue);
        Assert.False(new YamlString("x").IsEmptyValue);
    }

    // Equality and hashing. Deep, cyclic and shared-everywhere values are
    // exercised in a child process (YamlHostileInputTests, #158), where a
    // regression to recursion would crash the probe instead of this host.

    [Fact]
    public void Equality_is_structural_and_order_sensitive()
    {
        var a = YamlValue.Parse("x: 1\ny: [a, b]\n");
        Assert.Equal(a, YamlValue.Parse("x: 1\ny: [a, b]\n"));
        Assert.Equal(a.GetHashCode(), YamlValue.Parse("x: 1\ny: [a, b]\n").GetHashCode());
        Assert.NotEqual(a, YamlValue.Parse("y: [a, b]\nx: 1\n"));
        Assert.NotEqual(a, YamlValue.Parse("x: 1\ny: [b, a]\n"));
        Assert.NotEqual(a, YamlValue.Parse("x: 1\ny: [a, b, c]\n"));
        Assert.NotEqual(a, YamlValue.Parse("x: '1'\ny: [a, b]\n"));
    }

    [Fact]
    public void Negative_zero_equals_zero_and_hashes_alike()
    {
        var zero = new YamlSequence([new YamlFloat(0.0)]);
        var negativeZero = new YamlSequence([new YamlFloat(-0.0)]);
        Assert.Equal(zero, negativeZero);
        Assert.Equal(zero.GetHashCode(), negativeZero.GetHashCode());
    }

    /// <summary>
    /// IEEE-754 all the way: a value holding NaN is unequal even to itself.
    /// Pins that the comparison takes no by-reference shortcut, which would
    /// make <c>v.Equals(v)</c> true here.
    /// </summary>
    [Fact]
    public void A_value_holding_NaN_is_unequal_even_to_itself()
    {
        var map = new YamlMapping();
        map.Insert("k", new YamlFloat(double.NaN));
        Assert.False(map.Equals(map));
    }

    [Fact]
    public void Sharing_a_subtree_does_not_change_equality_or_the_hash()
    {
        var shared = YamlValue.Parse("k: [1, 2]\n");
        var withSharing = new YamlMapping();
        withSharing.Insert("x", shared);
        withSharing.Insert("y", shared);

        var withCopies = YamlValue.Parse("x:\n  k: [1, 2]\ny:\n  k: [1, 2]\n");
        Assert.Equal(withCopies, withSharing);
        Assert.Equal(withSharing, withCopies);
        Assert.Equal(withCopies.GetHashCode(), withSharing.GetHashCode());
    }

    /// <summary>
    /// The comparison remembers pairs it has met, never one side alone: a
    /// node shared under two keys is compared with each of its two partners,
    /// whichever side holds the sharing. Remembering only the left node (or
    /// only the right) would skip the second partner and call these equal.
    /// </summary>
    /// <remarks>
    /// The shared subtree is 100 collections deep because the comparison only
    /// starts remembering pairs past its first 64: a shallower one would never
    /// reach the code this pins.
    /// </remarks>
    [Fact]
    public void A_shared_subtree_is_compared_with_each_of_its_partners()
    {
        static YamlValue Nested(string leaf)
        {
            var inner = YamlValue.Parse(leaf);
            for (var i = 0; i < 100; i++)
            {
                inner = new YamlSequence([inner]);
            }

            return inner;
        }

        var shared = Nested("[1, 2]");
        var withSharing = new YamlMapping();
        withSharing.Insert("x", shared);
        withSharing.Insert("y", shared);

        var differentSecond = new YamlMapping();
        differentSecond.Insert("x", Nested("[1, 2]"));
        differentSecond.Insert("y", Nested("[1, 3]"));

        Assert.NotEqual<YamlValue>(withSharing, differentSecond);
        Assert.NotEqual<YamlValue>(differentSecond, withSharing);
    }

    /// <summary>
    /// The hash reads a bounded number of nodes, so this value, larger than
    /// that, is only partly read. Equal copies still hash alike, and two
    /// values that differ only where the hash never reads still compare
    /// unequal. The bound itself is guarded where breaking it would hang, by
    /// the cyclic and diamond cases of <c>YamlHostileInputTests</c>.
    /// </summary>
    [Fact]
    public void Values_larger_than_the_hash_reads_stay_consistent_with_equality()
    {
        static YamlSequence Items(string last) =>
            new([.. Enumerable.Range(0, 10_000).Select(i => (YamlValue)new YamlInt(i)), new YamlString(last)]);

        Assert.Equal(Items("end").GetHashCode(), Items("end").GetHashCode());
        Assert.Equal(Items("end"), Items("end"));
        Assert.NotEqual(Items("end"), Items("END"));
    }
}
