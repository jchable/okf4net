// SPDX-License-Identifier: LGPL-3.0-or-later
//
// End-to-end smoke test of the second-brain sample, with no Claude Desktop:
// copies bundle/ to a temporary directory, starts okf-mcp over stdio on the
// copy (OKF_MCP_WRITABLE=1), drives it with raw JSON-RPC, then checks the disk
// and runs `okf validate` on the result.
//
//   dotnet run samples/second-brain/smoke.cs            # okf-mcp from this repo (dotnet run --no-build --project src/OKF4net.Mcp)
//   dotnet run samples/second-brain/smoke.cs -- --tool  # the installed `okf-mcp` dotnet tool
//
// Exit code 0 when every check passed. The copy is deleted at the end unless
// --keep is given. Every line okf-mcp writes on stdout must be JSON-RPC: a
// line that is not is reported and fails the run.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var useTool = args.Contains("--tool");
var keep = args.Contains("--keep");

var sampleDir = SampleDirectory();
var repoRoot = Path.GetFullPath(Path.Combine(sampleDir, "..", ".."));
var work = Path.Combine(Path.GetTempPath(), "okf-second-brain-" + Guid.NewGuid().ToString("N")[..8]);
CopyDirectory(Path.Combine(sampleDir, "bundle"), work);
Console.WriteLine($"bundle copy: <temp>/{Path.GetFileName(work)}");

// Whatever happens below (a failed check, an exception, a timeout), the
// server is stopped and the copy deleted unless --keep was given.
Process? serverProcess = null;
try
{
    var failures = 0;
    void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}");
        if (!ok)
        {
            failures++;
        }
    }

    var watched = new[] { "index.md", "notes/index.md", "log.md" };
    var before = watched.ToDictionary(f => f, f => Hash(Path.Combine(work, f)));

    // From the repo: build once, then start it exactly as the README's Claude
    // Desktop entry does (`dotnet run --no-build`), so startup is not a build.
    var mcpProject = Path.Combine(repoRoot, "src", "OKF4net.Mcp");
    if (!useTool)
    {
        using var build = Process.Start(new ProcessStartInfo("dotnet") { ArgumentList = { "build", mcpProject, "-v", "q", "-nologo" }, RedirectStandardOutput = true })!;
        await build.StandardOutput.ReadToEndAsync();
        await build.WaitForExitAsync();
        if (build.ExitCode != 0)
        {
            Console.WriteLine($"dotnet build {mcpProject} failed (exit {build.ExitCode})");
            return 1;
        }
    }

    var psi = useTool
        ? new ProcessStartInfo("okf-mcp")
        : new ProcessStartInfo("dotnet") { ArgumentList = { "run", "--no-build", "--project", mcpProject, "--" } };
    psi.ArgumentList.Add(work);
    psi.Environment["OKF_MCP_WRITABLE"] = "1";
    psi.Environment.Remove("OKF_MCP_READONLY"); // inherited, it would win over OKF_MCP_WRITABLE
    psi.RedirectStandardInput = true;
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;
    psi.StandardOutputEncoding = new UTF8Encoding(false);
    psi.StandardInputEncoding = new UTF8Encoding(false);
    Console.WriteLine($"server: {psi.FileName} {string.Join(' ', psi.ArgumentList)}".Replace(work, "<copy>", StringComparison.Ordinal).Replace(repoRoot, "<repo>", StringComparison.Ordinal));

    var server = Process.Start(psi) ?? throw new InvalidOperationException("could not start okf-mcp");
    serverProcess = server;
    var stderr = new StringBuilder();
    server.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stderr) { stderr.AppendLine(e.Data); } } };
    server.BeginErrorReadLine();

    var nonJsonLines = new List<string>();

    // Parses one stdout line; anything that is not a JSON-RPC 2.0 message
    // (request, notification or response) is recorded and yields null.
    JsonObject? ReadMessage(string line)
    {
        JsonNode? node = null;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
        }

        if (IsJsonRpcMessage(node))
        {
            return (JsonObject)node!;
        }

        nonJsonLines.Add(line);
        return null;
    }

    var display = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    var nextId = 0;

    async Task<JsonNode> Request(string method, JsonObject? @params = null)
    {
        var id = ++nextId;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null)
        {
            message["params"] = @params;
        }

        await server.StandardInput.WriteLineAsync(message.ToJsonString());
        await server.StandardInput.FlushAsync();

        // The first request waits for `dotnet run` to build; later ones are quick.
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (true)
        {
            var line = await server.StandardOutput.ReadLineAsync(timeout.Token)
                ?? throw new InvalidOperationException($"okf-mcp closed stdout. stderr:\n{stderr}");
            if (ReadMessage(line) is { } node && !node.ContainsKey("method")
                && node["id"] is JsonValue got && got.TryGetValue<int>(out var gotId) && gotId == id)
            {
                return node["error"] is { } error
                    ? throw new InvalidOperationException($"{method}: JSON-RPC error {error.ToJsonString()}")
                    : node["result"]!;
            }
        }
    }

    async Task<string> Call(string tool, JsonObject arguments)
    {
        var result = await Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments });
        var text = string.Concat(result["content"]!.AsArray().Select(c => c?["text"]?.GetValue<string>()));
        Console.WriteLine($"\n> {tool} {arguments.ToJsonString(display)}");
        Console.WriteLine(Indent(text));
        return text;
    }

    Console.WriteLine("\n== initialize");
    var init = await Request("initialize", new JsonObject
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "second-brain-smoke", ["version"] = "1.0" },
    });
    Console.WriteLine($"  server: {init["serverInfo"]?["name"]} {init["serverInfo"]?["version"]}, protocol {init["protocolVersion"]}");
    await server.StandardInput.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString());

    Console.WriteLine("\n== tools/list");
    var tools = (await Request("tools/list"))["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
    Console.WriteLine($"  {tools.Count} tools: {string.Join(", ", tools)}");
    Check(tools.Count == 12, "twelve tools with OKF_MCP_WRITABLE=1");
    Check(tools.Contains("okf_write_concept") && tools.Contains("okf_append_log") && tools.Contains("okf_regenerate_indexes"), "the write tools are registered");

    Console.WriteLine("\n== the observation gate");
    const string ideaId = "notes/standing-desk-idea";
    const string ideaFrontmatter = "type: Note\ntitle: Standing desk idea\ndescription: Try a standing desk for a month before buying one.\ntags: [home, health]";
    const string ideaBody = "# Idea\n\nBorrow a converter for a month, note how often I actually stand.\nRelated: [running](../areas/running.md).\n";
    var refused = await Call("okf_write_concept", new JsonObject { ["conceptId"] = ideaId, ["frontmatterYaml"] = ideaFrontmatter, ["body"] = ideaBody });
    Check(refused.StartsWith("Error:", StringComparison.Ordinal) && !File.Exists(Path.Combine(work, "notes", "standing-desk-idea.md")), "a write before any read is refused, nothing written");

    Console.WriteLine("\n== create a note (read first: the read reports it absent)");
    await Call("okf_read_concept", new JsonObject { ["conceptId"] = ideaId });
    var created = await Call("okf_write_concept", new JsonObject { ["conceptId"] = ideaId, ["frontmatterYaml"] = ideaFrontmatter, ["body"] = ideaBody });
    var ideaPath = Path.Combine(work, "notes", "standing-desk-idea.md");
    Check(!created.StartsWith("Error:", StringComparison.Ordinal) && File.Exists(ideaPath), "the new note exists on disk");
    Check(File.Exists(ideaPath) && File.ReadAllText(ideaPath).Contains("generated:", StringComparison.Ordinal), "the server stamped `generated` (none was given)");

    Console.WriteLine("\n== append to a note (read, add a line, write the whole document back)");
    var shown = await Call("okf_read_concept", new JsonObject { ["conceptId"] = "notes/reading-list" });

    // What the model does with what it was shown: the frontmatter block copied
    // back exactly as the read showed it (as the bundle's guide asks), the body
    // from disk plus one line.
    var frontmatter = FencedBlock(shown, "```yaml");
    var (_, body) = SplitDocument(File.ReadAllText(Path.Combine(work, "notes", "reading-list.md")));
    var appendedLine = "- *A Philosophy of Software Design* (John Ousterhout) — recommended by Sam.";
    body = body.Replace("\n\n# Read", "\n" + appendedLine + "\n\n# Read", StringComparison.Ordinal);
    var appended = await Call("okf_write_concept", new JsonObject { ["conceptId"] = "notes/reading-list", ["frontmatterYaml"] = frontmatter, ["body"] = body });
    var readingListText = File.ReadAllText(Path.Combine(work, "notes", "reading-list.md"));
    Check(!appended.StartsWith("Error:", StringComparison.Ordinal) && readingListText.Contains(appendedLine, StringComparison.Ordinal), "the appended line is on disk");
    Check(readingListText.Contains("by: human:me", StringComparison.Ordinal), "the frontmatter copied back as shown keeps `generated.by: human:me`");
    var again = await Call("okf_write_concept", new JsonObject { ["conceptId"] = "notes/reading-list", ["frontmatterYaml"] = frontmatter, ["body"] = body });
    Check(again.StartsWith("Error:", StringComparison.Ordinal), "a second write without a fresh read is refused");

    Console.WriteLine("\n== index.md and log.md after the writes alone");
    foreach (var f in watched)
    {
        Console.WriteLine($"  {f}: {(Hash(Path.Combine(work, f)) == before[f] ? "unchanged" : "CHANGED")}");
    }

    Check(watched.All(f => Hash(Path.Combine(work, f)) == before[f]), "okf_write_concept touches neither index.md nor log.md");

    Console.WriteLine("\n== the explicit follow-up tools");
    await Call("okf_regenerate_indexes", new JsonObject());
    await Call("okf_append_log", new JsonObject { ["kind"] = "Creation", ["text"] = "added the [standing desk idea](/notes/standing-desk-idea.md); one book on the [reading list](/notes/reading-list.md)." });
    Check(File.ReadAllText(Path.Combine(work, "notes", "index.md")).Contains("standing-desk-idea.md", StringComparison.Ordinal), "notes/index.md now lists the new note");
    Check(Hash(Path.Combine(work, "log.md")) != before["log.md"], "log.md has the new entry");
    await Call("okf_changes_since", new JsonObject { ["sinceDate"] = DateTime.UtcNow.ToString("yyyy-MM-dd") });

    // Closing stdin asks okf-mcp to shut down; read stdout to EOF so whatever it
    // writes while shutting down is checked too.
    server.StandardInput.Close();
    var drained = true;
    using (var drainTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
    {
        try
        {
            while (await server.StandardOutput.ReadLineAsync(drainTimeout.Token) is { } line)
            {
                ReadMessage(line);
            }
        }
        catch (OperationCanceledException)
        {
            drained = false;
        }
    }

    if (!server.WaitForExit(TimeSpan.FromSeconds(30)))
    {
        server.Kill(entireProcessTree: true);
    }

    Console.WriteLine("\n== stdio");
    Check(drained, "okf-mcp closed stdout within 30 s of stdin closing");
    foreach (var line in nonJsonLines)
    {
        Console.WriteLine($"  not JSON-RPC: {line}");
    }

    Check(nonJsonLines.Count == 0, "every line okf-mcp wrote on stdout was JSON-RPC");

    Console.WriteLine("\n== log.md on disk");
    Console.WriteLine(Indent(File.ReadAllText(Path.Combine(work, "log.md"))));

    Console.WriteLine("== okf validate");
    var validate = new ProcessStartInfo("dotnet") { ArgumentList = { "run", "--project", Path.Combine(repoRoot, "src", "OKF4net.Cli"), "--", "validate", work }, RedirectStandardOutput = true, RedirectStandardError = true };
    using (var okf = Process.Start(validate)!)
    {
        var output = await okf.StandardOutput.ReadToEndAsync() + await okf.StandardError.ReadToEndAsync();
        await okf.WaitForExitAsync();
        Console.WriteLine(Indent(output.Replace(work, "<copy>", StringComparison.Ordinal)));
        Check(okf.ExitCode == 0, $"okf validate exit code {okf.ExitCode}");
    }

    Console.WriteLine(failures == 0 ? "\nALL CHECKS PASSED" : $"\n{failures} CHECK(S) FAILED");
    return failures == 0 ? 0 : 1;
}
finally
{
    if (serverProcess is not null)
    {
        try
        {
            if (!serverProcess.HasExited)
            {
                serverProcess.Kill(entireProcessTree: true);
                serverProcess.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch (InvalidOperationException)
        {
            // It exited in between.
        }

        serverProcess.Dispose();
    }

    if (!keep && Directory.Exists(work))
    {
        Directory.Delete(work, recursive: true);
    }
}

static string SampleDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

static void CopyDirectory(string from, string to)
{
    foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
    {
        Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
    }

    Directory.CreateDirectory(to);
    foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
    {
        File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
}

// A JSON-RPC 2.0 envelope: `"jsonrpc": "2.0"`, then either a string `method`
// (request or notification) or an `id` with exactly one of `result` / `error`.
static bool IsJsonRpcMessage(JsonNode? node)
{
    if (node is not JsonObject o
        || o["jsonrpc"] is not JsonValue version || !version.TryGetValue<string>(out var v) || v != "2.0")
    {
        return false;
    }

    if (o.ContainsKey("method"))
    {
        return o["method"] is JsonValue method && method.TryGetValue<string>(out _)
            && !o.ContainsKey("result") && !o.ContainsKey("error");
    }

    return o.ContainsKey("id") && (o.ContainsKey("result") ^ o.ContainsKey("error"));
}

static string Hash(string path) => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "(absent)";

static (string Frontmatter, string Body) SplitDocument(string text)
{
    var lines = text.ReplaceLineEndings("\n").Split('\n');
    var close = Array.IndexOf(lines, "---", 1);
    return (string.Join('\n', lines[1..close]), string.Join('\n', lines[(close + 1)..]).TrimStart('\n'));
}

// The lines between the first `opening` fence line and the next ``` line.
static string FencedBlock(string text, string opening)
{
    var lines = text.ReplaceLineEndings("\n").Split('\n');
    var start = Array.IndexOf(lines, opening);
    if (start < 0)
    {
        throw new InvalidOperationException($"no {opening} block in:\n{text}");
    }

    var end = Array.IndexOf(lines, "```", start + 1);
    return string.Join('\n', lines[(start + 1)..end]);
}

static string Indent(string text) =>
    string.Join('\n', text.TrimEnd().ReplaceLineEndings("\n").Split('\n').Select(l => "    " + l)) + "\n";
