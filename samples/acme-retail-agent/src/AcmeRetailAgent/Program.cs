// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Samples.AcmeRetailAgent;

var bundleRoot = ResolveBundleRoot(Environment.GetEnvironmentVariable("OKF_BUNDLE_ROOT"));
if (bundleRoot is null)
{
    Console.Error.WriteLine(
        "acme-retail-agent: could not locate bundles/acme_retail (no OKF4net.sln found "
        + "above " + AppContext.BaseDirectory + "). Set OKF_BUNDLE_ROOT to an absolute path instead.");
    return 2;
}

if (!Directory.Exists(bundleRoot))
{
    Console.Error.WriteLine($"acme-retail-agent: bundle root not found: {bundleRoot}. Set OKF_BUNDLE_ROOT to override.");
    return 2;
}

if (!ChatClientFactory.TryCreate(Environment.GetEnvironmentVariable, out var chatClient, out var chatError))
{
    Console.Error.WriteLine(ChatClientFactory.FormatStartupError(chatError));
    return 2;
}

const string SystemInstructions =
    "You are grounded in the Acme Retail OKF knowledge bundle (a fictional "
    + "retail company's metrics, policies, and attested computations). Use "
    + "the okf_* tools to answer questions -- do not guess at bundle "
    + "content. Attested Computations can be inspected with "
    + "okf_get_computation (their contract and sanctioned SQL) but this "
    + "sample cannot run them.";

// Kept in sync with OkfMcpToolset.WriteToolNames in src/OKF4net.Mcp: this
// sample is read-only by construction, not just by documentation, so the
// agent can never mutate the byte-exact, license-attributed upstream copy.
string[] writeToolNames = ["okf_write_concept", "okf_append_log", "okf_regenerate_indexes"];

var tools = new OkfBundleTools(bundleRoot);
var contextProvider = new OkfContextProvider(tools);
var agentOptions = new ChatClientAgentOptions
{
    ChatOptions = new ChatOptions
    {
        Instructions = SystemInstructions,
        Tools = [.. tools.GetTools().Where(t => !writeToolNames.Contains(t.Name))],
    },
    AIContextProviders = [contextProvider],
};
AIAgent agent = chatClient.AsAIAgent(agentOptions);

if (!TryReadOneShotPrompt(args, out var oneShotPrompt, out var promptError))
{
    Console.Error.WriteLine($"acme-retail-agent: {promptError}");
    return 2;
}

if (oneShotPrompt is not null)
{
    try
    {
        var response = await agent.RunAsync(oneShotPrompt);
        PrintToolCalls(response);
        Console.WriteLine(response.Text);
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"acme-retail-agent: chat request failed: {ex.Message}");
        return 1;
    }
}

Console.WriteLine("Acme Retail agent -- ask a question, or type 'exit'/'quit' to leave.");
var session = await agent.CreateSessionAsync();
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null || line.Trim() is "exit" or "quit")
    {
        break;
    }

    if (line.Trim().Length == 0)
    {
        continue;
    }

    try
    {
        var response = await agent.RunAsync(line, session);
        PrintToolCalls(response);
        Console.WriteLine(response.Text);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"acme-retail-agent: chat request failed: {ex.Message}");
        continue;
    }
}

return 0;

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

    return dir is null ? null : Path.Combine(dir.FullName, "bundles", "acme_retail");
}

// Returns false only when `--prompt` was given with no following value (a
// usage error, formatted into `error`); a `--prompt`-less invocation is not
// an error -- `prompt` is null and the caller falls through to stdin/REPL.
static bool TryReadOneShotPrompt(string[] args, out string? prompt, out string? error)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--prompt")
        {
            if (i + 1 >= args.Length)
            {
                prompt = null;
                error = "--prompt requires a value";
                return false;
            }

            prompt = args[i + 1];
            error = null;
            return true;
        }
    }

    error = null;
    if (Console.IsInputRedirected)
    {
        var piped = Console.In.ReadToEnd();
        prompt = string.IsNullOrWhiteSpace(piped) ? null : piped.Trim();
        return true;
    }

    prompt = null;
    return true;
}

static void PrintToolCalls(AgentResponse response)
{
    var calls = response.Messages
        .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
        .Select(c => c.Name)
        .ToList();
    if (calls.Count > 0)
    {
        Console.WriteLine($"[tools: {string.Join(", ", calls)}]");
    }
}
