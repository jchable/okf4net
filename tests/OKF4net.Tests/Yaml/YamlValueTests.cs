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
    public void IsEmptyValue_matches_rust_semantics()
    {
        Assert.True(YamlNull.Instance.IsEmptyValue);
        Assert.True(new YamlString("").IsEmptyValue);
        Assert.True(new YamlSequence([]).IsEmptyValue);
        Assert.True(new YamlMapping().IsEmptyValue);
        Assert.False(new YamlInt(0).IsEmptyValue);
        Assert.False(new YamlString("x").IsEmptyValue);
    }
}
