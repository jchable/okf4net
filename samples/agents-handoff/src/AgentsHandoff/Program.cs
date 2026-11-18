// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Samples.AgentsHandoff;

// Tool output and descriptions contain non-ASCII text (section signs, dashes).
Console.OutputEncoding = System.Text.Encoding.UTF8;

const string NoteId = "notes/purchasers-on-the-sample-dataset";
const string NoteFrontmatter =
    "type: Note\n"
    + "title: Counting purchasers on the GA4 sample dataset\n"
    + "description: The purchasers query's date range is a template; on the sample dataset, narrow it to the shards the dataset holds.\n"
    + "tags:\n"
    + "- purchasers\n"
    + "- sample-data\n"
    + "- handoff\n";
const string NoteBody =
    "The [purchasers query](../references/metrics/purchasers.md) filters "
    + "`_TABLE_SUFFIX BETWEEN '20180501' AND '20240131'`: a template range. The "
    + "[GA4 sample dataset](../datasets/ga4_obfuscated_sample_ecommerce.md) only holds daily "
    + "shards from November 2020 to January 2021, so when counting purchasers on it, narrow "
    + "the range to those months: the same count, over fewer shards.\n";

var keepCopy = args.Contains("--keep");
var sourceRoot = ResolveBundleRoot(Environment.GetEnvironmentVariable("OKF_BUNDLE_ROOT"));
if (sourceRoot is null)
{
    Console.Error.WriteLine(
        "agents-handoff: could not locate bundles/ga4 (no OKF4net.sln found above "
        + AppContext.BaseDirectory.ReplaceLineEndings(" ") + "). Set OKF_BUNDLE_ROOT to override.");
    return 2;
}

if (!Directory.Exists(sourceRoot))
{
    Console.Error.WriteLine($"agents-handoff: bundle root not found: {sourceRoot.ReplaceLineEndings(" ")}. Set OKF_BUNDLE_ROOT to override.");
    return 2;
}

// The curator writes, so it never writes into the repository: everything below works on a
// throwaway copy of the bundle, deleted at exit unless --keep is given.
var workRoot = Path.Combine(Path.GetTempPath(), "okf-agents-handoff-" + Guid.NewGuid().ToString("N")[..8]);
CopyDirectory(sourceRoot, workRoot);

try
{
    return await RunAsync(workRoot);
}
finally
{
    if (keepCopy)
    {
        Console.WriteLine();
        Console.WriteLine($"Working copy kept (--keep): {workRoot}");
    }
    else
    {
        Directory.Delete(workRoot, recursive: true);
    }
}

static async Task<int> RunAsync(string workRoot)
{
    Console.WriteLine("OKF4net.Agents hand-off sample");
    Console.WriteLine("No LLM endpoint, no API key, no network: both \"models\" below are scripted stand-ins.");
    Console.WriteLine("Bundle: a temporary working copy of bundles/ga4 (the repository is never written).");

    // -----------------------------------------------------------------------
    PrintHeader("1. One BundleHandle, two agents' tool sets");
    Narrate(
        "The host builds ONE BundleHandle over the working copy and gives it to two OkfBundleTools:",
        "a curator's, offered every tool (OkfToolMode.ReadWrite), and a reader's, offered the",
        "read-only ones (OkfToolMode.ReadOnly) plus an OkfContextProvider built over them. One tool",
        "set per agent, because what okf_read_concept showed a session is recorded per tool set;",
        "one handle for both, because the image of the bundle is the handle's.",
        "Its refresh policy is Manual -- nothing reloads it on a timer or on access -- yet a write by",
        "a tool set built over it invalidates it whatever the policy (spec §5.1, §5.2).");

    using var shared = new BundleHandle(new FileSystemBundleProvider(workRoot), refreshPolicy: BundleRefreshPolicy.Manual);
    var curatorTools = new OkfBundleTools(shared);
    var readerTools = new OkfBundleTools(shared);
    var contextProvider = new OkfContextProvider(readerTools);

    IList<AITool> curatorToolList = curatorTools.GetTools(OkfToolMode.ReadWrite);
    IList<AITool> readerToolList = readerTools.GetTools(OkfToolMode.ReadOnly);
    Console.WriteLine();
    Console.WriteLine($"  curator: {curatorToolList.Count} tools, including {string.Join(", ", curatorToolList.Select(t => t.Name).Where(OkfBundleTools.WriteToolNames.Contains))}");
    Console.WriteLine($"  reader:  {readerToolList.Count} tools, none of them a write tool, plus the OkfContextProvider");

    Console.WriteLine();
    Narrate(
        "Two more consumers of the same directory, for comparison. Neither is given the shared",
        "handle: `new OkfBundleTools(root)` builds its own (Manual), and the second is built over",
        "a handle of its own with the OnAccess policy, which compares the provider's version",
        "token before every use. Both load their image now, before anything is written:");
    var outsider = new OkfBundleTools(workRoot);
    using var watcherHandle = new BundleHandle(new FileSystemBundleProvider(workRoot), refreshPolicy: BundleRefreshPolicy.OnAccess);
    var watcher = new OkfBundleTools(watcherHandle);
    Console.WriteLine();
    Console.WriteLine($"  outsider (own handle, Manual):   {FirstLine(await outsider.ReadConceptAsync(NoteId))}");
    Console.WriteLine($"  watcher  (own handle, OnAccess): {FirstLine(await watcher.ReadConceptAsync(NoteId))}");

    // -----------------------------------------------------------------------
    PrintHeader("2. The curator agent writes a note");
    Narrate(
        "The curator's scripted model tries to write the note straight away, which okf_write_concept",
        "refuses: a session may only write a concept it has read with okf_read_concept (or seen",
        "absent there), so that a write is conditioned on the version it saw (spec §5.4). It reads,",
        "is told the concept does not exist, writes it, and regenerates the indexes so the new",
        "notes/ directory is listed.");
    Console.WriteLine();

    var writeArguments = new Dictionary<string, object?>
    {
        ["conceptId"] = NoteId,
        ["frontmatterYaml"] = NoteFrontmatter,
        ["body"] = NoteBody,
    };
    IChatClient curatorModel = new ScriptedChatClient(
        "curator",
        [
            ScriptStep.Call("okf_write_concept", writeArguments),
            ScriptStep.Call("okf_read_concept", new Dictionary<string, object?> { ["conceptId"] = NoteId }),
            ScriptStep.Call("okf_write_concept", writeArguments),
            ScriptStep.Call("okf_regenerate_indexes", new Dictionary<string, object?>()),
            ScriptStep.Answer($"Left the note for the analysts at {NoteId}, and regenerated the indexes."),
        ],
        narrate: line => Console.WriteLine($"  {line}"));
    AIAgent curator = curatorModel.AsAIAgent(
        instructions: "You curate an OKF bundle. Record what analysts should know as concepts, using the okf_* tools.",
        tools: curatorToolList);

    const string CuratorTask = "Leave a note for the analysts: the purchasers query's date range needs narrowing on the sample dataset.";
    Console.WriteLine($"  user: {CuratorTask}");
    var curatorResponse = await curator.RunAsync(CuratorTask);
    PrintRunSummary(curatorResponse);

    // -----------------------------------------------------------------------
    PrintHeader("3. The reader agent picks the note up");
    Narrate(
        "A different agent, a different conversation, read-only tools. Its OkfContextProvider runs",
        "before the model's first turn and injects the bundle's root listing plus the concepts that",
        "score best against the question -- read from the shared handle, which the curator's write",
        "invalidated, so the note is among them without anyone calling Refresh. The scripted model",
        "then opens the note with okf_read_concept, also served from the shared image.");
    Console.WriteLine();

    IChatClient readerModel = new ScriptedChatClient(
        "reader",
        [
            ScriptStep.Call("okf_read_concept", new Dictionary<string, object?> { ["conceptId"] = NoteId }),
            ScriptStep.Answer(
                "Run the purchasers query (references/metrics/purchasers), but on the sample dataset narrow "
                + $"_TABLE_SUFFIX to the November 2020 - January 2021 shards (source: {NoteId})."),
        ],
        narrate: line => Console.WriteLine($"  {line}"));
    AIAgent reader = readerModel.AsAIAgent(new ChatClientAgentOptions
    {
        ChatOptions = new ChatOptions
        {
            Instructions = "Answer analysts' questions from the OKF bundle, using the okf_* tools and the context provided.",
            Tools = readerToolList,
        },
        AIContextProviders = [contextProvider],
    });

    const string ReaderQuestion = "How do I count purchasers on the GA4 sample dataset?";
    Console.WriteLine($"  user: {ReaderQuestion}");
    var readerResponse = await reader.RunAsync(ReaderQuestion);
    PrintRunSummary(readerResponse);

    // -----------------------------------------------------------------------
    PrintHeader("4. What the consumers outside the shared handle see");
    Narrate(
        "The write invalidated the shared handle only. The watcher's OnAccess policy notices the",
        "directory changed at its next use; the outsider keeps serving the image it loaded in",
        "step 1 until its host refreshes it.");
    Console.WriteLine();
    Console.WriteLine($"  watcher  (OnAccess):                {FirstLine(await watcher.ReadConceptAsync(NoteId))}");
    Console.WriteLine($"  outsider (Manual):                  {FirstLine(await outsider.ReadConceptAsync(NoteId))}");
    await outsider.Handle.RefreshAsync();
    Console.WriteLine($"  outsider after Handle.RefreshAsync: {FirstLine(await outsider.ReadConceptAsync(NoteId))}");
    Console.WriteLine();
    Narrate(
        "Share one handle between every consumer that must agree on the bundle; pick a refresh policy",
        "for the changes that come from elsewhere (another process, a git pull, a human editor).");

    return 0;
}

// OKF_BUNDLE_ROOT, when set, is resolved via Path.GetFullPath (a relative
// value resolves against the current directory). Otherwise the bundle is
// bundles/ga4 at the repo root, found by walking up from the running
// assembly to OKF4net.sln. Either way it is only read: the sample works on a copy.
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

// A plain recursive copy: the sample bundles hold ordinary files and directories only.
static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    foreach (var directory in Directory.EnumerateDirectories(source))
    {
        CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
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

static void PrintRunSummary(AgentResponse response)
{
    var called = response.Messages
        .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
        .Select(c => c.Name);
    Console.WriteLine($"  [tools called: {string.Join(", ", called)}]");
    Console.WriteLine($"  assistant: {response.Text}");
}

// The first non-empty line of a tool's text, enough to tell "found" from "not found".
static string FirstLine(string text) =>
    text.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
