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
        // Rust `impl fmt::Display for Value` (yaml/mod.rs:213-217) writes
        // to_yaml_string(); the C# override must match exactly.
        var v = YamlValue.Parse("a: 1\n");
        Assert.Equal(v.ToYamlString(), v.ToString());
        Assert.Equal(v.ToYamlString(), $"{v}");
    }

    // --- F14: implicit conversions, porting Rust's From<&str>/From<bool>/
    // From<i64>/From<Vec<T>> for Value (yaml/mod.rs:219-247). There is
    // intentionally no From<f64> port -- Rust has none. ---

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
        // Rust's `From<Vec<Value>>` consumes (moves) the Vec, so mutating it
        // after construction is impossible there. The C# constructor must
        // not alias the caller's list, so post-construction mutation of the
        // caller's list must not be observable through the sequence.
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
    public void IsEmptyValue_matches_rust_semantics()
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
}
