// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// Parser tests: they call <see cref="YamlValue.Parse"/> directly and assert
/// on scalar values via accessors. Round-trip assertions (parse -> emit ->
/// parse) live in <see cref="YamlRoundtripTests"/>.
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
        // The block-mapping parser appends every entry unconditionally; it
        // does not dedup like Mapping.Insert does. Get() then returns the
        // FIRST match.
        var v = YamlValue.Parse("type: foo\ntype: bar\n");
        var m = v.AsMapping()!;
        Assert.Equal("foo", m.Get("type")!.AsString());
        Assert.Equal(2, m.Count);
        Assert.Equal("type: foo\ntype: bar\n", v.ToYamlString());
    }

    [Fact]
    public void Non_string_keys_are_invisible_to_get_and_keys_but_kept_in_entries()
    {
        // Mapping.Get/Keys both filter on the string variant, so a bool or
        // float key is invisible to them, but Entries still yields it (raw,
        // typed). The emitter runs every key through scalar emission, so a
        // float key "1.50" re-emits as "1.5" (trailing zero dropped), same
        // as any float scalar value.
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
    public void Flow_scalar_keeps_a_bare_colon_that_is_not_a_key_value_separator()
    {
        // A ':' inside a flow-style plain scalar only terminates the scalar
        // when it is a genuine key/value colon (followed by whitespace, a
        // flow indicator, or end-of-input) -- mirroring the block-style
        // SplitKeyValue rule. A bare ':' inside a value (e.g. "human:ada",
        // a URL, or an ISO timestamp) must be kept.
        var byHuman = YamlValue.Parse("x: {by: human:ada}\n");
        Assert.Equal("human:ada", byHuman.AsMapping()!.Get("x")!.AsMapping()!.Get("by")!.AsString());

        var resource = YamlValue.Parse("x: {resource: https://example.com/a}\n");
        Assert.Equal("https://example.com/a", resource.AsMapping()!.Get("x")!.AsMapping()!.Get("resource")!.AsString());

        var at = YamlValue.Parse("x: {at: 2026-07-03T00:00:00Z}\n");
        Assert.Equal("2026-07-03T00:00:00Z", at.AsMapping()!.Get("x")!.AsMapping()!.Get("at")!.AsString());

        var seq = YamlValue.Parse("v: [{by: human:ada}, {by: bot/1}]\n");
        var entries = seq.AsMapping()!.Get("v")!.AsSequence()!;
        Assert.Equal(2, entries.Count);
        Assert.Equal("human:ada", entries[0].AsMapping()!.Get("by")!.AsString());
        Assert.Equal("bot/1", entries[1].AsMapping()!.Get("by")!.AsString());

        // Regression guard: a genuine key/value colon still splits normally.
        var m = YamlValue.Parse("m: {a: b, c: d}\n").AsMapping()!.Get("m")!.AsMapping()!;
        Assert.Equal("b", m.Get("a")!.AsString());
        Assert.Equal("d", m.Get("c")!.AsString());
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
        // (byte-exact) comparison. The parser uses ordinal (byte-exact)
        // prefix checks, so a line beginning with a soft hyphen must NOT be
        // misread as a block-sequence item ("- ").
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
    public void Deeply_nested_flow_sequence_throws_instead_of_overflowing_the_stack()
    {
        // The parser adds an explicit recursion-depth limit as a deliberate
        // safety measure -- an uncatchable StackOverflowException would
        // otherwise take down the whole process. A flow sequence with 5000
        // nested '[' must throw a catchable YamlParseException, and the
        // parser (and process) must survive to run subsequent tests.
        var text = "tags: " + new string('[', 5000);
        var ex = Assert.Throws<YamlParseException>(() => YamlValue.Parse(text));
        Assert.Contains("nesting depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deeply_nested_block_sequence_throws_instead_of_overflowing_the_stack()
    {
        // The block parser's "indentation-relaxed" sequence style (list
        // items at the SAME column as their parent, e.g. "tags:\n- a\n- b")
        // is implemented by right-recursion: a bare "-" (empty item, i.e. a
        // nested-null item) recurses ParseSequence -> ParseNested ->
        // ParseSequence once per line. A long flat run of bare "-" lines
        // must throw rather than overflow the stack.
        var text = string.Concat(Enumerable.Repeat("-\n", 5000));
        var ex = Assert.Throws<YamlParseException>(() => YamlValue.Parse(text));
        Assert.Contains("nesting depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Lone_carriage_return_stays_embedded_in_the_line()
    {
        // LfLines splits only on '\n' (stripping one preceding '\r' when
        // present, i.e. it understands "\r\n"). A lone '\r' NOT followed by
        // '\n' is not a line terminator at all, so the whole input is a
        // single line and "title: foo" is part of the scalar value rather
        // than a second mapping entry.
        var v = YamlValue.Parse("type: doc\rtitle: foo");
        var m = v.AsMapping()!;
        Assert.Single(m.Keys);
        Assert.Equal("doc\rtitle: foo", m.Get("type")!.AsString());
    }

    [Fact]
    public void Multiline_plain_scalar_in_mapping_folds_with_a_space()
    {
        // Real-world case: upstream OKF bundles (the reference_agent
        // generator) write long `description:` values as a folded plain
        // scalar spanning two lines -- valid YAML, but the parser previously
        // only read a mapping entry's value from its own line, then threw
        // "unexpected indentation in mapping" on the deeper-indented
        // continuation line.
        var v = YamlValue.Parse(
            "description: Computes the count or list of users who have completed a purchase or\n  in-app purchase.\n");
        var m = v.AsMapping()!;
        Assert.Equal(
            "Computes the count or list of users who have completed a purchase or in-app purchase.",
            m.Get("description")!.AsString());
    }

    [Fact]
    public void Multiline_plain_scalar_blank_line_folds_to_a_newline_and_mapping_continues()
    {
        var v = YamlValue.Parse(
            "description: First line\n  continues here.\n\n  New paragraph starts.\nstatus: stable\n");
        var m = v.AsMapping()!;
        Assert.Equal(
            "First line continues here.\nNew paragraph starts.",
            m.Get("description")!.AsString());
        Assert.Equal("stable", m.Get("status")!.AsString());
    }

    [Fact]
    public void Multiline_plain_scalar_in_sequence_item_folds_and_sequence_continues()
    {
        var v = YamlValue.Parse("notes:\n- Some item text continues\n  onto a second line.\n- next item\n");
        var notes = v.AsMapping()!.Get("notes")!.AsSequence()!;
        Assert.Equal(2, notes.Count);
        Assert.Equal("Some item text continues onto a second line.", notes[0].AsString());
        Assert.Equal("next item", notes[1].AsString());
    }

    [Fact]
    public void Deeper_indented_sequence_item_after_a_plain_scalar_value_still_throws()
    {
        // A block-sequence indicator ("- ") is reserved at the start of a
        // line in block context -- it can never be folded into a plain
        // scalar as literal text, even at a deeper indent. This must keep
        // throwing (its pre-existing behavior) rather than silently
        // corrupting the nested sequence into scalar text.
        Assert.Throws<YamlParseException>(() =>
            YamlValue.Parse("notes: Some notes here\n  - looks like a sequence item\n"));
    }

    [Fact]
    public void Multiline_plain_scalar_comment_only_continuation_line_is_dropped()
    {
        // A comment-only continuation line is indentation-blind, matching
        // every other comment check in this parser -- it does not become
        // literal scalar text, and (here, as the value's last line before
        // dedent) leaves no trailing artifact once trailing blanks are
        // stripped.
        var v = YamlValue.Parse("description: some text\n  # a comment\nnext: value\n");
        var m = v.AsMapping()!;
        Assert.Equal("some text", m.Get("description")!.AsString());
        Assert.Equal("value", m.Get("next")!.AsString());
    }

    [Fact]
    public void Quoted_and_flow_values_are_unaffected_by_continuation_folding()
    {
        // Continuation folding is scoped to plain (unquoted, non-flow)
        // scalars only -- a deeper-indented line after a quoted or flow
        // value keeps throwing exactly as before: multi-line quoted/flow
        // values are a distinct, unimplemented feature, not this bug.
        Assert.Throws<YamlParseException>(() =>
            YamlValue.Parse("title: \"Some title\"\n  extra\n"));
        Assert.Throws<YamlParseException>(() =>
            YamlValue.Parse("tags: [a, b]\n  extra\n"));
    }
}
