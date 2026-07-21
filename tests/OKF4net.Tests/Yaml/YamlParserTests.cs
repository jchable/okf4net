using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// Port of the "parse" half of tests/yaml.rs (lines 14-180). Round-trip
/// assertions (the <c>roundtrip</c> helper: parse -> emit -> parse) are
/// deferred to Task 3; here we call <see cref="YamlValue.Parse"/> directly,
/// and assert on scalar values via accessors since <see cref="YamlValue"/>
/// structural equality is not implemented until Task 3.
/// </summary>
public class YamlParserTests
{
    [Fact]
    public void Scalars()
    {
        Assert.Equal("hello", YamlValue.Parse("hello").AsString());
        Assert.Equal(42L, YamlValue.Parse("42").AsInt());
        Assert.Equal(-7L, YamlValue.Parse("-7").AsInt());
        Assert.Equal(2.5, Assert.IsType<YamlFloat>(YamlValue.Parse("2.5")).Value);
        Assert.True(YamlValue.Parse("true").AsBool());
        Assert.False(YamlValue.Parse("false").AsBool());
        Assert.Same(YamlNull.Instance, YamlValue.Parse("null"));
        Assert.Same(YamlNull.Instance, YamlValue.Parse("~"));
        Assert.Same(YamlNull.Instance, YamlValue.Parse(""));
    }

    [Fact]
    public void Quoted_scalars()
    {
        Assert.Equal("42", YamlValue.Parse("\"42\"").AsString());
        Assert.Equal("true", YamlValue.Parse("'true'").AsString());
        Assert.Equal("line1\nline2", YamlValue.Parse("\"line1\\nline2\"").AsString());
        Assert.Equal("it's here", YamlValue.Parse("'it''s here'").AsString());
    }

    [Fact]
    public void Block_mapping()
    {
        var v = YamlValue.Parse("type: BigQuery Table\ntitle: Orders\ncount: 3\n");
        var m = v.AsMapping()!;
        Assert.Equal("BigQuery Table", m.Get("type")!.AsString());
        Assert.Equal(3L, m.Get("count")!.AsInt());
        // Key order is preserved.
        Assert.Equal(new[] { "type", "title", "count" }, m.Keys.ToArray());
    }

    [Fact]
    public void Flow_and_block_sequences()
    {
        var flow = YamlValue.Parse("tags: [sales, orders, revenue]\n");
        Assert.Equal(3, flow.AsMapping()!.Get("tags")!.AsSequence()!.Count);
        var block = YamlValue.Parse("tags:\n  - sales\n  - orders\n");
        var tags = block.AsMapping()!.Get("tags")!;
        Assert.Equal("sales", tags.AsSequence()![0].AsString());
    }

    [Fact]
    public void Nested_mappings()
    {
        var v = YamlValue.Parse("a:\n  b:\n    c: deep\n  d: 2\ne: top\n");
        var m = v.AsMapping()!;
        var a = m.Get("a")!.AsMapping()!;
        var b = a.Get("b")!.AsMapping()!;
        Assert.Equal("deep", b.Get("c")!.AsString());
        Assert.Equal(2L, a.Get("d")!.AsInt());
        Assert.Equal("top", m.Get("e")!.AsString());
    }

    [Fact]
    public void Flow_mapping()
    {
        var v = YamlValue.Parse("obj: {x: 1, y: two}\n");
        var obj = v.AsMapping()!.Get("obj")!.AsMapping()!;
        Assert.Equal(1L, obj.Get("x")!.AsInt());
        Assert.Equal("two", obj.Get("y")!.AsString());
    }

    [Fact]
    public void Comments_are_ignored()
    {
        var v = YamlValue.Parse("# leading comment\ntype: X  # trailing\ntitle: Y\n");
        var m = v.AsMapping()!;
        Assert.Equal("X", m.Get("type")!.AsString());
        Assert.Equal("Y", m.Get("title")!.AsString());
    }

    [Fact]
    public void Literal_block_scalar()
    {
        var v = YamlValue.Parse("body: |\n  line one\n  line two\n");
        Assert.Equal("line one\nline two\n", v.AsMapping()!.Get("body")!.AsString());
    }

    [Fact]
    public void Folded_block_scalar()
    {
        var v = YamlValue.Parse("body: >\n  line one\n  line two\n");
        Assert.Equal("line one line two\n", v.AsMapping()!.Get("body")!.AsString());
    }

    [Fact]
    public void Block_sequence_at_parent_indent()
    {
        // This is exactly what PyYAML's safe_dump (the reference serializer) emits
        // for list values: dashes at the same column as the key.
        var v = YamlValue.Parse("type: X\ntags:\n- sales\n- orders\ntitle: Y\n");
        var m = v.AsMapping()!;
        var tags = m.Get("tags")!.AsSequence()!;
        Assert.Equal(2, tags.Count);
        Assert.Equal("sales", tags[0].AsString());
        Assert.Equal("Y", m.Get("title")!.AsString());
        // And nested under a deeper mapping.
        var nested = YamlValue.Parse("outer:\n  tags:\n  - a\n  - b\n");
        var inner = nested.AsMapping()!.Get("outer")!.AsMapping()!;
        Assert.Equal(2, inner.Get("tags")!.AsSequence()!.Count);
    }

    [Fact]
    public void Conservative_number_resolution()
    {
        // Zero-padded codes stay strings (not coerced to ints).
        Assert.Equal("007", YamlValue.Parse("007").AsString());
        Assert.Equal("08", YamlValue.Parse("08").AsString());
        // Bare-exponent forms stay strings; only point-bearing floats are floats.
        Assert.Equal("1e3", YamlValue.Parse("1e3").AsString());
        Assert.Equal(1500.0, Assert.IsType<YamlFloat>(YamlValue.Parse("1.5e3")).Value);
        Assert.Equal(0L, YamlValue.Parse("0").AsInt());
        Assert.Equal(-42L, YamlValue.Parse("-42").AsInt());
    }

    [Fact]
    public void Unterminated_flow_is_error()
    {
        Assert.Throws<YamlParseException>(() => YamlValue.Parse("tags: [a, b"));
    }

    [Fact]
    public void Tab_indentation_is_error()
    {
        Assert.Throws<YamlParseException>(() => YamlValue.Parse("a:\n\tb: 1"));
    }
}
