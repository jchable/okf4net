// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

/// <summary>
/// The YAML subset's one nesting rule, held by the parser and the emitter
/// alike: the depth of a node is the number of collections (mapping or
/// sequence, block or flow, empty or not) on the path from the root down to
/// and including it; a scalar adds nothing; the root collection has depth 1;
/// a document or value is accepted when every collection in it has depth
/// at most 1000.
///
/// Three properties, for every shape below:
/// <list type="bullet">
/// <item>R→W: text the parser accepts, the emitter accepts once parsed;</item>
/// <item>W→R: a value the emitter accepts re-parses from the emitted text,
/// <c>Equal</c> to the original;</item>
/// <item>refusal: at depth 1001 the parser rejects the text AND the emitter
/// rejects the value.</item>
/// </list>
/// The W→R side is ALSO tested on values built in code, independently of the
/// parser: a parser-first test can never hand the emitter a value the parser
/// already refuses, so it cannot see an emitter that accepts too much.
/// </summary>
public class YamlDepthSymmetryTests
{
    private const int Limit = 1000;
    private const string Message = "nesting depth limit exceeded";

    // ---------------------------------------------------------------- text generators
    //
    // Each generator takes `c`, the number of collections it builds itself,
    // and a `leaf` that becomes the innermost value. A scalar leaf ("v") adds
    // nothing; an empty-collection leaf ("[]" / "{}") is one more collection.
    // So the document's depth is c + LeafDepth(leaf).

    private static int LeafDepth(string leaf) => leaf is "[]" or "{}" ? 1 : 0;

    private static string Pad(int n) => new(' ', n);

    /// <summary>
    /// <c>k:\n  k:\n    …\n      k: leaf</c>: one block mapping per line, each
    /// line opening the next one two columns deeper. c lines, c mappings.
    /// </summary>
    private static string BlockMappings(int c, string leaf)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < c - 1; i++)
        {
            sb.Append(Pad(2 * i)).Append("k:\n");
        }

        return sb.Append(Pad(2 * (c - 1))).Append("k: ").Append(leaf).Append('\n').ToString();
    }

    /// <summary>
    /// <c>-\n  -\n    - leaf</c>: one block sequence per line, the empty
    /// item <c>-</c> opening the next one two columns deeper. c lines, c
    /// sequences.
    /// </summary>
    private static string BlockSequences(int c, string leaf)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < c - 1; i++)
        {
            sb.Append(Pad(2 * i)).Append("-\n");
        }

        return sb.Append(Pad(2 * (c - 1))).Append("- ").Append(leaf).Append('\n').ToString();
    }

    /// <summary>
    /// <c>- k:\n    - k:\n        - k: leaf</c>: each line is a sequence
    /// whose single item is a compact <c>- key:</c> mapping, so each line
    /// opens TWO collections (sequence, then mapping), four columns deeper
    /// than the last. For an odd c a root mapping <c>k:</c> comes first (one
    /// collection) and the lines start at column 2. c collections in all.
    /// </summary>
    private static string CompactSequenceMappings(int c, string leaf)
    {
        var sb = new StringBuilder();
        var baseIndent = 0;
        var pairs = c / 2;
        if (c % 2 == 1)
        {
            // The root mapping: depth 1.
            if (pairs == 0)
            {
                return sb.Append("k: ").Append(leaf).Append('\n').ToString();
            }

            sb.Append("k:\n");
            baseIndent = 2;
        }

        for (var j = 0; j < pairs; j++)
        {
            sb.Append(Pad(baseIndent + (4 * j))).Append("- k:");
            sb.Append(j == pairs - 1 ? " " + leaf : string.Empty).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// The indentation-relaxed form <c>BlockParser.ParseNested</c> accepts: a
    /// block sequence at the SAME column as the key that owns it
    /// (<c>k:\n- a</c>, what PyYAML's <c>safe_dump</c> writes for lists).
    /// <c>k:\n- k:\n  - k:\n    - leaf</c>: the root mapping <c>k:</c> (one
    /// collection), then each <c>- k:</c> line opens a sequence at its parent
    /// key's own column plus the compact mapping it holds (two collections),
    /// that mapping's key sitting two columns in. When one collection
    /// remains, a final <c>- leaf</c> line opens a relaxed sequence alone. c
    /// collections in all.
    /// </summary>
    private static string RelaxedSequences(int c, string leaf)
    {
        var lines = new List<string> { "k:" };
        var depth = 1;
        var indent = 0;
        while (c - depth >= 2)
        {
            lines.Add(Pad(indent) + "- k:");
            depth += 2;
            indent += 2;
        }

        if (c - depth == 1)
        {
            lines.Add(Pad(indent) + "- " + leaf);
        }
        else
        {
            lines[^1] += " " + leaf;
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// <c>k: [[[leaf]]]</c>: the top-level block mapping (one collection)
    /// plus c - 1 flow sequences.
    /// </summary>
    private static string FlowSequences(int c, string leaf) =>
        "k: " + new string('[', c - 1) + leaf + new string(']', c - 1) + "\n";

    /// <summary>
    /// <c>k: {a: {a: leaf}}</c>: the top-level block mapping (one collection)
    /// plus c - 1 flow mappings.
    /// </summary>
    private static string FlowMappings(int c, string leaf) =>
        "k: " + string.Concat(Enumerable.Repeat("{a: ", c - 1)) + leaf + new string('}', c - 1) + "\n";

    /// <summary>
    /// <c>[[[leaf]]]</c> as the whole document: c flow sequences, the
    /// outermost one being the root (depth 1) — no block collection at all.
    /// </summary>
    private static string FlowRoot(int c, string leaf) =>
        new string('[', c) + leaf + new string(']', c) + "\n";

    /// <summary>
    /// <paramref name="b"/> block mappings (<see cref="BlockMappings"/>'s
    /// shape) whose innermost value is <paramref name="f"/> flow sequences
    /// on the last key's own line: b + f collections.
    /// </summary>
    private static string Mixed(int b, int f, string leaf) =>
        BlockMappings(b, new string('[', f) + leaf + new string(']', f));

    /// <summary>
    /// The text of <paramref name="shape"/> nested exactly
    /// <paramref name="depth"/> collections deep, the leaf included.
    /// "Mixed:b" puts b block levels first and the rest in flow.
    /// </summary>
    private static string Text(string shape, int depth, string leaf)
    {
        var c = depth - LeafDepth(leaf);
        if (shape.StartsWith("Mixed:", StringComparison.Ordinal))
        {
            var b = int.Parse(shape["Mixed:".Length..], System.Globalization.CultureInfo.InvariantCulture);
            return Mixed(b, c - b, leaf);
        }

        return shape switch
        {
            "BlockMappings" => BlockMappings(c, leaf),
            "BlockSequences" => BlockSequences(c, leaf),
            "CompactSequenceMappings" => CompactSequenceMappings(c, leaf),
            "RelaxedSequences" => RelaxedSequences(c, leaf),
            "FlowSequences" => FlowSequences(c, leaf),
            "FlowMappings" => FlowMappings(c, leaf),
            "FlowRoot" => FlowRoot(c, leaf),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };
    }

    private static readonly string[] Shapes =
    [
        "BlockMappings", "BlockSequences", "CompactSequenceMappings", "RelaxedSequences",
        "FlowSequences", "FlowMappings", "FlowRoot",
        // b block levels, the rest flow: the flow parser must start counting
        // where the block parser stopped.
        "Mixed:1", "Mixed:450", "Mixed:500", "Mixed:900",
    ];

    private static readonly string[] Leaves = ["v", "[]", "{}"];

    public static TheoryData<string, string> ShapesAndLeaves()
    {
        var data = new TheoryData<string, string>();
        foreach (var shape in Shapes)
        {
            foreach (var leaf in Leaves)
            {
                data.Add(shape, leaf);
            }
        }

        return data;
    }

    /// <summary>
    /// The depth of <paramref name="value"/> under the rule: 0 for a scalar,
    /// otherwise 1 + the deepest child (an empty collection is 1).
    /// </summary>
    private static int Depth(YamlValue value) => value switch
    {
        YamlMapping m => 1 + m.Entries.Select(e => Math.Max(Depth(e.Key), Depth(e.Value))).DefaultIfEmpty(0).Max(),
        YamlSequence s => 1 + s.Items.Select(Depth).DefaultIfEmpty(0).Max(),
        _ => 0,
    };

    // ---------------------------------------------------------------- text, parser first

    /// <summary>
    /// At the limit: the parser accepts the text, the generator built exactly
    /// the depth it claims, the emitter accepts the value (R→W), and the
    /// emitted text parses back to an equal value (W→R).
    /// </summary>
    [Theory]
    [MemberData(nameof(ShapesAndLeaves))]
    public void At_the_limit_the_parser_accepts_and_the_value_round_trips(string shape, string leaf)
    {
        var value = YamlValue.Parse(Text(shape, Limit, leaf));
        Assert.Equal(Limit, Depth(value));

        var emitted = value.ToYamlString();
        Assert.Equal(value, YamlValue.Parse(emitted));
    }

    /// <summary>One collection deeper: the parser refuses the text, naming the limit.</summary>
    [Theory]
    [MemberData(nameof(ShapesAndLeaves))]
    public void One_over_the_limit_the_parser_refuses(string shape, string leaf)
    {
        var ex = Assert.Throws<YamlParseException>(() => YamlValue.Parse(Text(shape, Limit + 1, leaf)));
        Assert.Contains(Message, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The error names the line that opens the 1001st collection — for the
    /// block shapes the 1001st line, for an inline flow value the line
    /// holding it.
    /// </summary>
    [Theory]
    [InlineData("BlockMappings", 1001)]
    [InlineData("BlockSequences", 1001)]
    [InlineData("FlowSequences", 1)]
    [InlineData("Mixed:500", 500)]
    public void The_refusal_reports_the_line_where_the_limit_was_crossed(string shape, int line)
    {
        var ex = Assert.Throws<YamlParseException>(() => YamlValue.Parse(Text(shape, Limit + 1, "v")));
        Assert.Equal(line, ex.Line);
    }

    // ---------------------------------------------------------------- values built in code

    private static YamlValue Wrap(YamlValue inner, string kind)
    {
        if (kind == "seq")
        {
            return new YamlSequence([inner]);
        }

        var map = new YamlMapping();
        map.Insert("k", inner);
        return map;
    }

    /// <summary>
    /// A value <paramref name="depth"/> collections deep, built without the
    /// parser: <paramref name="kinds"/> picks the collection at each level
    /// ("map", "seq", or "alt" alternating, root first), and the innermost
    /// one holds <paramref name="leaf"/> — a scalar, or an EMPTY collection
    /// that is itself the deepest one.
    /// </summary>
    private static YamlValue Built(int depth, string kinds, string leaf)
    {
        // An empty leaf IS the innermost collection; a scalar leaf sits inside it.
        YamlValue inner;
        int remaining;
        switch (leaf)
        {
            case "[]":
                inner = new YamlSequence([]);
                remaining = depth - 1;
                break;
            case "{}":
                inner = new YamlMapping();
                remaining = depth - 1;
                break;
            default:
                inner = new YamlString(leaf);
                remaining = depth;
                break;
        }

        // Levels are wrapped innermost first; level i (1-based, root = 1) is
        // wrapped when `remaining` reaches i.
        for (var level = remaining; level >= 1; level--)
        {
            var kind = kinds switch
            {
                "alt" => level % 2 == 1 ? "map" : "seq",
                _ => kinds,
            };
            inner = Wrap(inner, kind);
        }

        return inner;
    }

    public static TheoryData<string, string> KindsAndLeaves()
    {
        var data = new TheoryData<string, string>();
        foreach (var kinds in new[] { "map", "seq", "alt" })
        {
            foreach (var leaf in Leaves)
            {
                data.Add(kinds, leaf);
            }
        }

        return data;
    }

    /// <summary>
    /// W→R on a value the parser never saw: at depth 1000 the emitter accepts
    /// it and its text parses back equal.
    /// </summary>
    [Theory]
    [MemberData(nameof(KindsAndLeaves))]
    public void A_built_value_at_the_limit_is_emitted_and_parses_back_equal(string kinds, string leaf)
    {
        var value = Built(Limit, kinds, leaf);
        Assert.Equal(Limit, Depth(value));

        var emitted = value.ToYamlString();
        Assert.Equal(value, YamlValue.Parse(emitted));
    }

    /// <summary>
    /// Refusal on the writer's side: a value 1001 collections deep — the
    /// 1001st possibly an EMPTY collection, which the emitter writes inline
    /// without recursing — is refused.
    /// </summary>
    [Theory]
    [MemberData(nameof(KindsAndLeaves))]
    public void A_built_value_one_over_the_limit_is_refused_by_the_emitter(string kinds, string leaf)
    {
        var value = Built(Limit + 1, kinds, leaf);
        Assert.Equal(Limit + 1, Depth(value));

        var ex = Assert.Throws<YamlEmitException>(() => value.ToYamlString());
        Assert.Contains(Message, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the real-world corpus

    /// <summary>
    /// Every bundle this repository holds — the samples under <c>bundles/</c>
    /// (upstream copies and our own) and the conformance fixtures under
    /// <c>tests/fixtures/</c> — loads without a nesting refusal, and so does
    /// every markdown file in them parsed on its own (a file a bundle walk
    /// skips is still a file a host may parse). Real frontmatter is a handful
    /// of levels deep; this pins that the rule change refuses none of it.
    /// </summary>
    [Fact]
    public void No_bundle_in_the_repository_reaches_the_limit()
    {
        var root = TestPaths.RepoRoot();
        var roots = new[] { "bundles", Path.Combine("tests", "fixtures") }
            .SelectMany(d => Directory.GetDirectories(Path.Combine(root, d)))
            .ToList();
        Assert.True(roots.Count >= 8, $"expected the repository's bundles, found {roots.Count}");

        var files = 0;
        foreach (var bundleRoot in roots)
        {
            var bundle = Bundle.Load(bundleRoot);
            Assert.DoesNotContain(bundle.ParseErrors, e => e.Error.Contains("nesting depth", StringComparison.Ordinal));

            foreach (var path in Directory.EnumerateFiles(bundleRoot, "*.md", SearchOption.AllDirectories))
            {
                files++;
                if (!OkfDocument.TryParse(File.ReadAllText(path), out _, out var error))
                {
                    Assert.DoesNotContain("nesting depth", error, StringComparison.Ordinal);
                }
            }
        }

        Assert.True(files >= 50, $"expected the repository's concept files, found {files}");
    }
}
