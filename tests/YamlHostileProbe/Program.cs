// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net;
using OKF4net.Yaml;

// Parses one hostile YAML input, named by args[0], through the library's two
// PUBLIC parse entry points, and prints one line per entry point:
//
//   YamlValue.Parse:   "YamlParseException line=<n>: <message>" | "accepted"
//   OkfDocument.Parse: "DocumentParseException: <message>"      | "accepted"
//
// then exits 0. Any other exception prints "unexpected <type>: <message>" and
// exits 2; an unknown case exits 3. A stack overflow cannot be caught at all:
// the runtime kills the process with a non-zero exit code, which is exactly
// what the harness (tests/OKF4net.Tests/Yaml/YamlHostileInputTests.cs) is
// there to see without dying itself.
if (args.Length != 1 || Build(args[0]) is not { } yaml)
{
    Console.Error.WriteLine("usage: YamlHostileProbe <case>");
    return 3;
}

try
{
    Console.WriteLine(Describe(() => YamlValue.Parse(yaml)));

    // The same text as a concept's frontmatter: the realistic hostile path.
    Console.WriteLine(Describe(() => OkfDocument.Parse("---\n" + yaml + "\n---\nbody\n")));
    return 0;
}
catch (Exception e)
{
    Console.WriteLine($"unexpected {e.GetType().Name}: {e.Message}");
    return 2;
}

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
}

static string? Build(string name) => name switch
{
    // 100 000 unclosed flow sequences on one line.
    "flow-sequences" => "k: " + new string('[', 100_000),

    // 100 000 unclosed flow mappings on one line.
    "flow-mappings" => "k: " + string.Concat(Enumerable.Repeat("{a: ", 100_000)),

    // 50 000 empty items "-", each at column 0: an empty item followed by an
    // item at its own column opens a nested sequence, so this recurses
    // ParseSequence -> ParseNested -> ParseSequence once per line. ("- "
    // repeated on ONE line is a plain scalar, not a recursion test.)
    "dash-lines" => string.Concat(Enumerable.Repeat("-\n", 50_000)),

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
