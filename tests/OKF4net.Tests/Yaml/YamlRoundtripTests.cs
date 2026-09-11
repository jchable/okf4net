// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// Round-trip invariant tests: the <c>Roundtrip</c> helper (parse -> emit ->
/// re-parse) and the tests that use it, plus the dedicated quoting/float
/// round-trip tests. This is where <see cref="YamlValue.ToYamlString"/>
/// (backed by <see cref="YamlEmitter"/>) and structural <see cref="YamlValue"/>
/// equality get exercised together.
/// </summary>
public class YamlRoundtripTests
{
    /// <summary>
    /// Round-trip helper: parse -> emit -> re-parse must produce a
    /// structurally equal value. Also asserts emitter stability (re-emitting
    /// the reparsed value reproduces the same text). Returns the original
    /// parsed value.
    /// </summary>
    private static YamlValue Roundtrip(string src)
    {
        var v = YamlValue.Parse(src);
        var emitted = v.ToYamlString();
        var reparsed = YamlValue.Parse(emitted);
        Assert.Equal(v, reparsed);
        Assert.Equal(emitted, reparsed.ToYamlString());
        return v;
    }

    [Fact]
    public void Block_mapping()
    {
        var v = Roundtrip("type: BigQuery Table\ntitle: Orders\ncount: 3\n");
        var m = v.AsMapping()!;
        Assert.Equal("BigQuery Table", m.Get("type")!.AsString());
        Assert.Equal(3L, m.Get("count")!.AsInt());
        // Key order is preserved.
        Assert.Equal(new[] { "type", "title", "count" }, m.Keys.ToArray());
    }

    [Fact]
    public void Flow_and_block_sequences()
    {
        var flow = Roundtrip("tags: [sales, orders, revenue]\n");
        Assert.Equal(3, flow.AsMapping()!.Get("tags")!.AsSequence()!.Count);
        var block = Roundtrip("tags:\n  - sales\n  - orders\n");
        var tags = block.AsMapping()!.Get("tags")!;
        Assert.Equal("sales", tags.AsSequence()![0].AsString());
    }

    [Fact]
    public void Nested_mappings()
    {
        Roundtrip("a:\n  b:\n    c: deep\n  d: 2\ne: top\n");
    }

    [Fact]
    public void Flow_mapping()
    {
        var v = Roundtrip("obj: {x: 1, y: two}\n");
        var obj = v.AsMapping()!.Get("obj")!.AsMapping()!;
        Assert.Equal(1L, obj.Get("x")!.AsInt());
        Assert.Equal("two", obj.Get("y")!.AsString());
    }

    [Fact]
    public void Strings_needing_quotes_roundtrip()
    {
        // A string that looks like a number / bool / has special chars must be
        // quoted on emit so it re-parses as a string.
        foreach (var s in new[] { "42", "true", "null", "a: b", "value # x", "", "  spaced  " })
        {
            YamlValue v = new YamlString(s);
            var m = new YamlMapping();
            m.Insert("k", v);
            var emitted = m.ToYamlString();
            var reparsed = YamlValue.Parse(emitted);
            Assert.Equal(v, reparsed.AsMapping()!.Get("k"));
        }
    }

    [Fact]
    public void Non_finite_and_large_floats_roundtrip()
    {
        // .inf / -.inf / large & tiny finite floats compare by bit pattern
        // (NaN gets a separate, special-cased assertion since NaN != NaN).
        foreach (var f in new[] { double.PositiveInfinity, double.NegativeInfinity, 1e30, -2.5e-12, 1.0 })
        {
            var m = new YamlMapping();
            m.Insert("k", new YamlFloat(f));
            var emitted = m.ToYamlString();
            var reparsed = YamlValue.Parse(emitted);
            var got = reparsed.AsMapping()!.Get("k");
            var gotFloat = Assert.IsType<YamlFloat>(got);
            Assert.Equal(BitConverter.DoubleToInt64Bits(f), BitConverter.DoubleToInt64Bits(gotFloat.Value));
        }

        // NaN is a float on the way back (compared specially).
        var nanMap = new YamlMapping();
        nanMap.Insert("k", new YamlFloat(double.NaN));
        var nanReparsed = YamlValue.Parse(nanMap.ToYamlString());
        var nanValue = Assert.IsType<YamlFloat>(nanReparsed.AsMapping()!.Get("k"));
        Assert.True(double.IsNaN(nanValue.Value));
    }

    [Fact]
    public void Emitting_a_pathologically_deep_value_throws_instead_of_overflowing_the_stack()
    {
        // Construct the deep YamlSequence directly (not via the parser,
        // which has its own independent depth guard) so this exercises
        // YamlEmitter's guard specifically. The emitter adds a depth guard so
        // a pathologically deep value throws instead of overflowing the stack.
        YamlValue v = new YamlSequence([]);
        for (var i = 0; i < 5000; i++)
        {
            v = new YamlSequence([v]);
        }

        // YamlEmitException, NOT InvalidOperationException: the type is what
        // makes this errors-as-data everywhere. Every layer that converts
        // library failures into data catches OkfException (the writer's
        // RunTool, the CLI's top-level handler); a bare
        // InvalidOperationException matched neither filter and escaped both.
        Assert.Throws<YamlEmitException>(() => v.ToYamlString());
    }

    /// <summary>
    /// The reachable version of the guard above: not a tree assembled in
    /// memory, but a document a bundle can hold. The parser counts block and
    /// flow nesting on two independent counters while the emitter counts both
    /// on one, so 600 + 600 levels parse and then fail to emit — proving the
    /// throw is reachable from ordinary input, which is what makes its type
    /// matter.
    /// </summary>
    [Fact]
    public void A_document_can_parse_and_still_exceed_the_emitters_depth()
    {
        var document = OkfDocument.Parse(DeepYamlDocument.Text());

        Assert.Throws<YamlEmitException>(() => document.Frontmatter.AsMapping().ToYamlString());
    }
}
