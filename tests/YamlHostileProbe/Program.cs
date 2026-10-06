// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net;
using OKF4net.Yaml;

// Runs one hostile case, named by args[0], through the library's PUBLIC entry
// points and prints one line per step:
//
//   YamlValue.Parse:     "YamlParseException line=<n>: <message>" | "accepted"
//   OkfDocument.Parse:   "DocumentParseException: <message>"      | "accepted"
//   YamlValue.ToYamlString (emit cases only):
//                        "YamlEmitException: <message>"           | "emitted"
//
// then exits 0. Any other exception prints "unexpected <type>: <message>" and
// exits 2; an unknown case exits 3. A stack overflow cannot be caught at all:
// the runtime kills the process with a non-zero exit code, which is exactly
// what the harness (tests/OKF4net.Tests/Yaml/YamlHostileInputTests.cs) is
// there to see without dying itself.
//
// The "small-stack-*" cases run on a new thread with a 256 KB stack: smaller
// than a 1000-deep document (block or flow) needs, so they reach the parser's and the
// emitter's stack guard (RuntimeHelpers.TryEnsureSufficientExecutionStack)
// rather than the 1000 rule. They report a refusal, or success if the stack
// happens to suffice — never a crash.
const int SmallStack = 256 * 1024;

var name = args.Length == 1 ? args[0] : string.Empty;
List<string>? lines = name switch
{
    "small-stack-block-mappings" => OnSmallStack(() => ParseBoth(BlockThenFlow(1000, 0))),
    "small-stack-block-sequences" => OnSmallStack(() => ParseBoth(BlockSequences(1000))),
    "small-stack-flow" => OnSmallStack(() => ParseBoth("k: " + new string('[', 999) + "v" + new string(']', 999))),
    "small-stack-emit" => OnSmallStack(() => [Emit(Built(1000))]),
    _ => Build(name) is { } yaml ? ParseBoth(yaml) : null,
};

if (lines is null)
{
    Console.Error.WriteLine("usage: YamlHostileProbe <case>");
    return 3;
}

foreach (var line in lines)
{
    Console.WriteLine(line);
}

return lines.Exists(l => l.StartsWith("unexpected ", StringComparison.Ordinal)) ? 2 : 0;

static List<string> ParseBoth(string yaml) =>
[
    Describe(() => YamlValue.Parse(yaml)),
    // The same text as a concept's frontmatter: the realistic hostile path.
    Describe(() => OkfDocument.Parse("---\n" + yaml + "\n---\nbody\n")),
];

static string Describe(Action parse)
{
    try
    {
        parse();
        return "accepted";
    }
    catch (YamlParseException e)
    {
        return $"YamlParseException line={e.Line}: {e.Message}";
    }
    catch (DocumentParseException e)
    {
        return $"DocumentParseException: {e.Message}";
    }
    catch (Exception e)
    {
        return $"unexpected {e.GetType().Name}: {e.Message}";
    }
}

static string Emit(YamlValue value)
{
    try
    {
        value.ToYamlString();
        return "emitted";
    }
    catch (YamlEmitException e)
    {
        return $"YamlEmitException: {e.Message}";
    }
    catch (Exception e)
    {
        return $"unexpected {e.GetType().Name}: {e.Message}";
    }
}

static List<string> OnSmallStack(Func<List<string>> work)
{
    List<string> result = [];
    var thread = new Thread(() => result = work(), SmallStack);
    thread.Start();
    thread.Join();
    return result;
}

static string? Build(string name) => name switch
{
    // 100 000 unclosed flow sequences on one line, under a key.
    "flow-sequences" => "k: " + new string('[', 100_000),

    // 100 000 unclosed flow mappings on one line, under a key.
    "flow-mappings" => "k: " + string.Concat(Enumerable.Repeat("{a: ", 100_000)),

    // 100 000 unclosed flow sequences as the whole document (no block root).
    "flow-root" => new string('[', 100_000),

    // A flow value as a block sequence item: "- [[[…".
    "flow-in-sequence-item" => "- " + new string('[', 100_000),

    // A flow value under a compact "- key:" mapping: "- k: [[[…".
    "flow-under-compact-mapping" => "- k: " + new string('[', 100_000),

    // 50 000 empty items "-", each at column 0: an empty item followed by an
    // item at its own column opens a nested sequence, so this recurses
    // ParseSequence -> ParseNested -> ParseSequence once per line. ("- "
    // repeated on ONE line is a plain scalar, not a recursion test.)
    "dash-lines" => string.Concat(Enumerable.Repeat("-\n", 50_000)),

    // The same recursion entered as an indentation-relaxed sequence under a
    // key (ParseNested -> ParseSequence at the key's own column): "k:" then
    // 100 000 "-" lines at column 0.
    "relaxed-sequence-under-key" => "k:\n" + string.Concat(Enumerable.Repeat("-\n", 100_000)),

    // 50 000 levels mixing block and flow: 999 block mappings, then 49 001
    // flow mappings on the last key's line. Flow cannot contain block, so
    // this is the one way the two stack; the flow levels start counting
    // where the block levels stopped, and the 1001st collection overall is
    // the second flow mapping.
    "block-then-flow" => BlockThenFlow(999, 49_001),

    // At the limit (1000 block mappings, the deepest block shape per stack
    // frame), on this process's main thread: the guard must leave room for
    // everything it accepts.
    "limit-block-mappings" => BlockThenFlow(1000, 0),

    _ => null,
};

static string BlockThenFlow(int blockLevels, int flowLevels)
{
    // blockLevels "k:" lines, each opening a mapping one indent step deeper;
    // the last one carries flowLevels flow mappings and a scalar.
    var sb = new StringBuilder();
    for (var i = 0; i < blockLevels - 1; i++)
    {
        sb.Append(' ', 2 * i).Append("k:\n");
    }

    sb.Append(' ', 2 * (blockLevels - 1)).Append("k: ");
    for (var i = 0; i < flowLevels; i++)
    {
        sb.Append("{a: ");
    }

    return sb.Append('v').Append('}', flowLevels).Append('\n').ToString();
}

static string BlockSequences(int levels)
{
    // levels - 1 empty items "-", each one indent step deeper, opening the
    // next sequence; the last sequence holds a scalar.
    var sb = new StringBuilder();
    for (var i = 0; i < levels - 1; i++)
    {
        sb.Append(' ', 2 * i).Append("-\n");
    }

    return sb.Append(' ', 2 * (levels - 1)).Append("- v\n").ToString();
}

static YamlValue Built(int depth)
{
    // depth collections built without the parser, alternating mapping (odd
    // levels, the root included) and sequence, around a scalar.
    YamlValue inner = new YamlString("v");
    for (var level = depth; level >= 1; level--)
    {
        if (level % 2 == 1)
        {
            var map = new YamlMapping();
            map.Insert("k", inner);
            inner = map;
        }
        else
        {
            inner = new YamlSequence([inner]);
        }
    }

    return inner;
}
