using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// Port of the round-trip invariant tests from tests/yaml.rs: the
/// <c>roundtrip</c> helper (lines 5-11: parse -> emit -> re-parse) and the
/// tests that use it (lines 41-73), plus the dedicated quoting/float
/// round-trip tests (lines 101-169). This is where <see cref="YamlValue.ToYamlString"/>
/// (backed by <see cref="YamlEmitter"/>) and structural <see cref="YamlValue"/>
/// equality get exercised together for the first time.
/// </summary>
public class YamlRoundtripTests
{
    /// <summary>
    /// Port of Rust's <c>roundtrip</c> helper (tests/yaml.rs:5-11): parse ->
    /// emit -> re-parse must produce a structurally equal value, mirroring
    /// <c>assert_eq!(v, reparsed)</c>. Also asserts emitter stability
    /// (re-emitting the reparsed value reproduces the same text), per the
    /// brief's docstring for this helper. Returns the original parsed value,
    /// like the Rust helper does.
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
        // quoted on emit so it re-parses as a string. Exact list from
        // tests/yaml.rs:105.
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
        // Port of tests/yaml.rs:151-169: .inf / -.inf / large & tiny finite
        // floats compare by bit pattern (NaN gets a separate, special-cased
        // assertion since NaN != NaN).
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
}
