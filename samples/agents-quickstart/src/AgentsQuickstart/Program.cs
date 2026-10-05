// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Samples.AgentsQuickstart;

// Tool output and descriptions contain non-ASCII text (section signs, dashes).
Console.OutputEncoding = System.Text.Encoding.UTF8;

const string SearchQuery = "purchaser";
const string ConceptId = "references/metrics/purchasers";

var bundleRoot = ResolveBundleRoot(Environment.GetEnvironmentVariable("OKF_BUNDLE_ROOT"));
if (bundleRoot is null)
{
    Console.Error.WriteLine(
        "agents-quickstart: could not locate bundles/ga4 (no OKF4net.sln found above "
        + AppContext.BaseDirectory.ReplaceLineEndings(" ") + "). Set OKF_BUNDLE_ROOT to override.");
    return 2;
}

if (!Directory.Exists(bundleRoot))
{
    Console.Error.WriteLine($"agents-quickstart: bundle root not found: {bundleRoot.ReplaceLineEndings(" ")}. Set OKF_BUNDLE_ROOT to override.");
    return 2;
}

Console.WriteLine("OKF4net.Agents quickstart");
Console.WriteLine("No LLM endpoint, no API key, no network: the \"model\" in step 3 is a scripted stand-in.");
Console.WriteLine($"Bundle: {bundleRoot}");

// ---------------------------------------------------------------------------
PrintHeader("1. Wrap the bundle and list its tools");
Narrate(
    "OkfBundleTools wraps one bundle root. GetTools(OkfToolMode.ReadOnly) returns every tool",
    "except the ones that write to disk, so nothing this sample does can modify the bundle.");

var tools = new OkfBundleTools(bundleRoot);
IList<AITool> readOnlyTools = tools.GetTools(OkfToolMode.ReadOnly);

Console.WriteLine();
foreach (var tool in readOnlyTools)
{
    Console.WriteLine($"  {tool.Name,-22} {FirstSentence(tool.Description)}");
}

Console.WriteLine();
Narrate(
    $"{readOnlyTools.Count} tools. Left out in ReadOnly mode: {string.Join(", ", OkfBundleTools.WriteToolNames.Order(StringComparer.Ordinal))}.",
    "(GetTools() with no argument would include them, ungated -- see the main README's security note.)");

// ---------------------------------------------------------------------------
PrintHeader("2. Call two tools directly from C#");
Narrate(
    "Each tool is also a plain public method returning agent-friendly text, so you can try",
    "one without an agent at all. Expected errors (an unknown id, an empty query) come back",
    "as a message, never as an exception.");

Console.WriteLine();
Console.WriteLine($"tools.Search(\"{SearchQuery}\")   // what okf_search runs");
PrintIndented(tools.Search(SearchQuery), maxLines: 20);

Console.WriteLine();
Console.WriteLine($"tools.ReadConcept(\"{ConceptId}\")   // what okf_read_concept runs");
PrintIndented(tools.ReadConcept(ConceptId), maxLines: 16);

// ---------------------------------------------------------------------------
PrintHeader("3. The same tools behind a Microsoft Agent Framework agent");
Narrate(
    "This is the AsAIAgent wiring from the main README, with the ReadOnly tool list from step 1.",
    "The IChatClient is a ScriptedChatClient: it replays three fixed turns (search, read, answer)",
    "instead of calling a model. Everything else is real -- the Agent Framework's function-invoking",
    "pipeline executes each requested okf_* tool and hands the result back to the \"model\" on its",
    "next turn.");
Console.WriteLine();

IChatClient chatClient = new ScriptedChatClient(
    [
        ScriptStep.Call("okf_search", new Dictionary<string, object?> { ["query"] = SearchQuery }),
        ScriptStep.Call("okf_read_concept", new Dictionary<string, object?> { ["conceptId"] = ConceptId }),
        ScriptStep.Answer(
            "In GA4, a purchaser is a user who logged either an `in_app_purchase` or a `purchase` event "
            + $"(source: {ConceptId})."),
    ],
    narrate: line => Console.WriteLine($"  {line}"));

AIAgent agent = chatClient.AsAIAgent(
    instructions: "Answer questions using the okf_* tools over the connected OKF bundle.",
    tools: readOnlyTools);

const string Question = "Who counts as a purchaser in GA4?";
Console.WriteLine($"  user: {Question}");
var response = await agent.RunAsync(Question);

var called = response.Messages
    .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
    .Select(c => c.Name);
Console.WriteLine($"  [tools called: {string.Join(", ", called)}]");
Console.WriteLine($"  assistant: {response.Text}");
Console.WriteLine();
Narrate(
    "The final answer is part of the script; a real model would write it from the tool results.",
    "To use one, assign any real IChatClient to `chatClient` instead of the ScriptedChatClient:",
    "the AsAIAgent call, the tools and RunAsync stay exactly as they are. samples/acme-retail-agent",
    "builds the same kind of agent over an OpenAI-compatible endpoint.");

return 0;

// OKF_BUNDLE_ROOT, when set, is resolved via Path.GetFullPath (a relative
// value resolves against the current directory). Otherwise the bundle is
// bundles/ga4 at the repo root, found by walking up from the running
// assembly to OKF4net.sln.
static string? ResolveBundleRoot(string? overridePath)
{
    if (!string.IsNullOrWhiteSpace(overridePath))
    {
        return Path.GetFullPath(overridePath.Trim());
    }

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
    {
        dir = dir.Parent;
    }

    return dir is null ? null : Path.Combine(dir.FullName, "bundles", "ga4");
}

static void PrintHeader(string title)
{
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
}

static void Narrate(params string[] lines)
{
    foreach (var line in lines)
    {
        Console.WriteLine(line);
    }
}

// Indents tool output so it reads as quoted text, cutting it after maxLines.
static void PrintIndented(string text, int maxLines)
{
    var lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
    foreach (var line in lines.Take(maxLines))
    {
        Console.WriteLine($"  | {line}");
    }

    if (lines.Length > maxLines)
    {
        Console.WriteLine($"  | ... ({lines.Length - maxLines} more lines)");
    }
}

// The first sentence of a tool description, for a one-line listing.
static string FirstSentence(string description)
{
    var end = description.IndexOf(". ", StringComparison.Ordinal);
    return end < 0 ? description : description[..(end + 1)];
}
