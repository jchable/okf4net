// SPDX-License-Identifier: LGPL-3.0-or-later
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
    public void Duplicate_string_keys_are_all_preserved_first_match_wins_on_get()
    {
        // Port of Rust's push_raw (parser.rs:132): the block-mapping parser
        // appends every entry unconditionally, it does not dedup like
        // Mapping::insert does. get() then returns the FIRST match
        // (mod.rs:65-70).
        var v = YamlValue.Parse("type: foo\ntype: bar\n");
        var m = v.AsMapping()!;
        Assert.Equal("foo", m.Get("type")!.AsString());
        Assert.Equal(2, m.Count);
        Assert.Equal("type: foo\ntype: bar\n", v.ToYamlString());
    }

    [Fact]
    public void Non_string_keys_are_invisible_to_get_and_keys_but_kept_in_entries()
    {
        // Port of Mapping::get/keys (mod.rs:64-75): both filter on the
        // String variant via as_str(), so a bool or float key is invisible
        // to them, but iter()/Entries still yields it (raw, typed). The
        // emitter's emit_mapping (emitter.rs:26-42) runs every key through
        // emit_scalar, so a float key "1.50" re-emits via format_float as
        // "1.5" (trailing zero dropped), same as any float scalar value.
        var v = YamlValue.Parse("true: x\n1.50: y\n");
        var m = v.AsMapping()!;
        Assert.Null(m.Get("true"));
        Assert.Empty(m.Keys);
        var entries = m.Entries.ToList();
        Assert.Equal(2, entries.Count);
        Assert.IsType<YamlBool>(entries[0].Key);
        Assert.IsType<YamlFloat>(entries[1].Key);
        Assert.Equal("true: x\n1.5: y\n", v.ToYamlString());
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
    public void Culture_sensitive_StartsWith_does_not_misparse_a_soft_hyphen_line()
    {
        // string.StartsWith(string) without an explicit StringComparison
        // uses CurrentCulture, whose linguistic comparison treats certain
        // zero-width "format" characters -- e.g. U+00AD SOFT HYPHEN -- as
        // ignorable: "­- x".StartsWith("- ") is empirically true under
        // CurrentCulture/InvariantCulture, but false under ordinal
        // (byte-exact) comparison. Rust's str::starts_with (parser.rs:84,
        // 113, 153, 217) is always byte-exact, so a line beginning with a
        // soft hyphen must NOT be misread as a block-sequence item ("- ").
        var v = YamlValue.Parse("outer:\n  ­- x\n");
        var value = v.AsMapping()!.Get("outer")!;
        Assert.Equal("­- x", value.AsString());
    }

    [Fact]
    public void Tab_indentation_is_error()
    {
        Assert.Throws<YamlParseException>(() => YamlValue.Parse("a:\n\tb: 1"));
    }

    [Fact]
    public void Lone_carriage_return_stays_embedded_in_the_line()
    {
        // Rust's str::lines() only splits on '\n' (stripping one preceding
        // '\r' when present, i.e. it understands "\r\n"). A lone '\r' NOT
        // followed by '\n' is not a line terminator at all, so the whole
        // input is a single line and "title: foo" is part of the scalar
        // value rather than a second mapping entry.
        var v = YamlValue.Parse("type: doc\rtitle: foo");
        var m = v.AsMapping()!;
        Assert.Single(m.Keys);
        Assert.Equal("doc\rtitle: foo", m.Get("type")!.AsString());
    }
}
