# OKF MCP Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship an `okf-mcp` executable that serves one OKF bundle to Claude Desktop / Claude Code over MCP stdio, the way `mcp-obsidian` serves an Obsidian vault.

**Architecture:** A new console project `src/OKF4net.Mcp/` is a thin façade: it takes the nine `AIFunction`s already produced by `OkfBundleTools.GetTools()` and re-exposes each as an MCP tool via `McpServerTool.Create(AIFunction)`, served over stdio by the official MCP SDK's generic host. No OKF business logic is written or duplicated — all path-safety, locking, producer validation, and "never throw toward the LLM" behaviour is inherited from `OkfBundleTools`.

**Tech Stack:** C# / net10.0, `ModelContextProtocol` 2.0.0-preview.3 (official MCP SDK), `Microsoft.Extensions.Hosting`, `OKF4net.Agents` (project reference, brings `OkfBundleTools`). Tests: xunit + the SDK's in-memory stream transport.

## Global Constraints

- Target framework: `net10.0` (inherited via `Directory.Build.props`).
- `Nullable`, `ImplicitUsings`, `LangVersion 14`, `TreatWarningsAsErrors=true` are all inherited from `Directory.Build.props` — **warnings are errors**, so every public member of `src/OKF4net.Mcp/` needs an XML doc comment (the project sets `GenerateDocumentationFile=true`).
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- File-scoped namespaces; XML doc comments on public API.
- The zero-third-party-dependency rule is **per-project**: `src/OKF4net/` and `src/OKF4net.Cli/` stay BCL-only and are NOT touched by this plan. `src/OKF4net.Mcp/` is a new project permitted the MCP SDK dependency (consistent with `src/OKF4net.Agents/` depending on `Microsoft.Agents.AI`).
- **No Native AOT** for `src/OKF4net.Mcp/` (the SDK is reflection-based). Do not add `PublishAot`.
- **Never touch `tests/fixtures/`.** No fixtures are involved here; tests build their own temp bundles.
- The three **write** tool names are exactly: `okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes`. The full set of nine tool names is: `okf_read_concept`, `okf_browse`, `okf_graph`, `okf_search`, `okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes`, `okf_validate_bundle`, `okf_changes_since`.
- **stdio invariant:** on the stdio transport, stdout is reserved for JSON-RPC. All logging MUST go to stderr. Never write to stdout from this project.

**Reference spec:** `docs/design/specs/2026-07-24-okf-mcp-server-design.md`.

---

### Task 1: Scaffold `OKF4net.Mcp` project and the tool builder

Creates the project, wires it into the solution and the test project, and implements `OkfMcpToolset.Build` — the single reusable seam that turns a bundle root into a list of MCP tools. Verified end-to-end by an in-memory MCP client that lists the nine tools.

**Files:**
- Create: `src/OKF4net.Mcp/OKF4net.Mcp.csproj`
- Create: `src/OKF4net.Mcp/OkfMcpToolset.cs`
- Modify: `OKF4net.sln` (add the project)
- Modify: `tests/OKF4net.Tests/OKF4net.Tests.csproj` (add project reference to `OKF4net.Mcp`)
- Create: `tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs`

**Interfaces:**
- Produces:
  - `public static class OkfMcpToolset` with
    `public static IReadOnlyList<ModelContextProtocol.Server.McpServerTool> Build(string bundleRoot, bool readOnly)`
  - `internal static readonly IReadOnlySet<string> OkfMcpToolset.WriteToolNames` = `{ "okf_write_concept", "okf_append_log", "okf_regenerate_indexes" }`

- [ ] **Step 1: Create the project file**

Create `src/OKF4net.Mcp/OKF4net.Mcp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <RootNamespace>OKF4net.Mcp</RootNamespace>
    <AssemblyName>okf-mcp</AssemblyName>
  </PropertyGroup>

  <PropertyGroup Label="Packaging">
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>okf-mcp</ToolCommandName>
    <PackageId>OKF4net.Mcp</PackageId>
    <Authors>Julien CHABLE</Authors>
    <Description>Local MCP (Model Context Protocol) server that exposes an OKF knowledge bundle to Claude Desktop / Claude Code as read/write tools.</Description>
    <Copyright>Copyright 2026 Julien CHABLE</Copyright>
    <PackageLicenseExpression>LGPL-3.0-or-later</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageProjectUrl>https://github.com/jchable/okf4net</PackageProjectUrl>
    <RepositoryUrl>https://github.com/jchable/okf4net</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageTags>okf;mcp;model-context-protocol;claude;knowledge;llm-tools</PackageTags>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
  </PropertyGroup>

  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
    <None Include="..\..\NOTICE" Pack="true" PackagePath="\" />
    <None Include="..\..\LICENSE.Apache-2.0" Pack="true" PackagePath="\" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="2.0.0-preview.3" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\OKF4net.Agents\OKF4net.Agents.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="OKF4net.Tests" />
  </ItemGroup>

</Project>
```

Note: `README.md` is referenced as the package readme; it is created in Task 5. Until then, `dotnet build` succeeds (readme is only needed by `dotnet pack`), but create an empty placeholder now so the `<None Include>` glob resolves:

Create `src/OKF4net.Mcp/README.md` with a single line (fully written in Task 5):

```markdown
# OKF4net.Mcp
```

- [ ] **Step 2: Add the project to the solution**

Run:
```bash
dotnet sln OKF4net.sln add src/OKF4net.Mcp/OKF4net.Mcp.csproj
```
Expected: `Project ... added to the solution.`

- [ ] **Step 3: Reference the new project from the test project**

In `tests/OKF4net.Tests/OKF4net.Tests.csproj`, add to the existing `<ItemGroup>` of `ProjectReference`s (next to the `OKF4net.Agents` reference):

```xml
    <ProjectReference Include="..\..\src\OKF4net.Mcp\OKF4net.Mcp.csproj" />
```

- [ ] **Step 4: Write the failing test (lists nine tools)**

Create `tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OKF4net.Mcp;

namespace OKF4net.Tests.Mcp;

public sealed class OkfMcpServerTests
{
    private static string NewBundleDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "okf-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Spins up an in-memory MCP server over the given tools and returns a connected client.</summary>
    private static async Task<(McpServer Server, McpClient Client)> ConnectAsync(IReadOnlyList<McpServerTool> tools)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [.. tools] });
        _ = server.RunAsync();

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));

        return (server, client);
    }

    [Fact]
    public async Task Build_exposes_all_nine_tools()
    {
        var bundle = NewBundleDir();
        try
        {
            var tools = OkfMcpToolset.Build(bundle, readOnly: false);
            var (server, client) = await ConnectAsync(tools);
            await using var _ = server;
            await using var __ = client;

            var listed = await client.ListToolsAsync();
            var names = listed.Select(t => t.Name).OrderBy(n => n).ToArray();

            Assert.Equal(
                new[]
                {
                    "okf_append_log", "okf_browse", "okf_changes_since", "okf_graph",
                    "okf_read_concept", "okf_regenerate_indexes", "okf_search",
                    "okf_validate_bundle", "okf_write_concept",
                },
                names);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }
}
```

- [ ] **Step 5: Run the test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfMcpServerTests.Build_exposes_all_nine_tools"`
Expected: FAIL to compile — `OkfMcpToolset` does not exist.

- [ ] **Step 6: Implement `OkfMcpToolset`**

Create `src/OKF4net.Mcp/OkfMcpToolset.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using OKF4net.Agents;

namespace OKF4net.Mcp;

/// <summary>
/// Builds the MCP tool set for one OKF bundle by wrapping the
/// <see cref="AIFunction"/>s produced by <see cref="OkfBundleTools.GetTools"/>.
/// This is the single seam shared by the stdio host (<c>Program</c>) and the
/// tests, so both expose exactly the same tools.
/// </summary>
public static class OkfMcpToolset
{
    /// <summary>
    /// The three write tools, dropped when the server is started read-only.
    /// </summary>
    internal static readonly IReadOnlySet<string> WriteToolNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "okf_write_concept",
            "okf_append_log",
            "okf_regenerate_indexes",
        };

    /// <summary>
    /// Creates the MCP tools rooted at <paramref name="bundleRoot"/>. When
    /// <paramref name="readOnly"/> is <see langword="true"/>, the three write
    /// tools (<see cref="WriteToolNames"/>) are omitted so the bundle is served
    /// for consultation only.
    /// </summary>
    /// <param name="bundleRoot">Path to the OKF bundle's root directory.</param>
    /// <param name="readOnly">When true, omit the write tools.</param>
    /// <returns>The MCP tools to register on the server.</returns>
    /// <exception cref="ArgumentException"><paramref name="bundleRoot"/> does not exist.</exception>
    public static IReadOnlyList<McpServerTool> Build(string bundleRoot, bool readOnly)
    {
        var okf = new OkfBundleTools(bundleRoot);

        var result = new List<McpServerTool>();
        foreach (var tool in okf.GetTools())
        {
            if (readOnly && WriteToolNames.Contains(tool.Name))
            {
                continue;
            }

            result.Add(McpServerTool.Create((AIFunction)tool));
        }

        return result;
    }
}
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfMcpServerTests.Build_exposes_all_nine_tools"`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/OKF4net.Mcp/ OKF4net.sln tests/OKF4net.Tests/OKF4net.Tests.csproj tests/OKF4net.Tests/Mcp/
git commit -m "feat(mcp): scaffold OKF4net.Mcp project and OkfMcpToolset builder"
```

---

### Task 2: Read-only mode filters out the write tools

**Files:**
- Modify: `tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs` (add one test)

**Interfaces:**
- Consumes: `OkfMcpToolset.Build(string, bool)` from Task 1.

- [ ] **Step 1: Write the failing test**

Add this method to `OkfMcpServerTests`:

```csharp
    [Fact]
    public async Task Build_readOnly_omits_the_three_write_tools()
    {
        var bundle = NewBundleDir();
        try
        {
            var tools = OkfMcpToolset.Build(bundle, readOnly: true);
            var (server, client) = await ConnectAsync(tools);
            await using var _ = server;
            await using var __ = client;

            var names = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();

            Assert.Equal(6, names.Count);
            Assert.DoesNotContain("okf_write_concept", names);
            Assert.DoesNotContain("okf_append_log", names);
            Assert.DoesNotContain("okf_regenerate_indexes", names);
            Assert.Contains("okf_read_concept", names);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfMcpServerTests.Build_readOnly_omits_the_three_write_tools"`
Expected: PASS (the filtering logic already exists from Task 1; this test locks it in).

- [ ] **Step 3: Commit**

```bash
git add tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs
git commit -m "test(mcp): assert read-only mode drops the write tools"
```

---

### Task 3: End-to-end write → read round-trip through MCP

Proves that a real MCP `tools/call` reaches `OkfBundleTools` and persists to disk, then reads back.

**Files:**
- Modify: `tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs` (add a helper + one test)

**Interfaces:**
- Consumes: `OkfMcpToolset.Build`, the in-memory `ConnectAsync` helper.

- [ ] **Step 1: Write the failing test**

Add this helper and test to `OkfMcpServerTests`:

```csharp
    private static string ResultText(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task Write_then_read_round_trips_through_mcp()
    {
        var bundle = NewBundleDir();
        try
        {
            var tools = OkfMcpToolset.Build(bundle, readOnly: false);
            var (server, client) = await ConnectAsync(tools);
            await using var _ = server;
            await using var __ = client;

            var write = await client.CallToolAsync(
                "okf_write_concept",
                new Dictionary<string, object?>
                {
                    ["conceptId"] = "notes/test",
                    ["frontmatterYaml"] =
                        "type: note\ntitle: Test Note\ndescription: A test note.\ntimestamp: 2026-07-24T00:00:00Z",
                    ["body"] = "Hello from MCP.",
                });
            Assert.Contains("Written notes/test", ResultText(write));

            var read = await client.CallToolAsync(
                "okf_read_concept",
                new Dictionary<string, object?> { ["conceptId"] = "notes/test" });
            var text = ResultText(read);

            Assert.Contains("Test Note", text);
            Assert.Contains("Hello from MCP.", text);
            Assert.True(File.Exists(Path.Combine(bundle, "notes", "test.md")));
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfMcpServerTests.Write_then_read_round_trips_through_mcp"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs
git commit -m "test(mcp): end-to-end write/read round-trip through the MCP transport"
```

---

### Task 4: Startup config resolution + stdio host (`Program`)

Adds the executable's entry point: resolve the bundle root and read-only flag from args/env, fail cleanly to stderr with a non-zero exit code when misconfigured, otherwise start the stdio server. Config resolution is factored into a testable static so the failure paths are covered without spawning a process (the SDK warns against `WithStdioServerTransport` in unit tests, so the host itself is not unit-tested).

**Files:**
- Create: `src/OKF4net.Mcp/OkfMcpConfig.cs`
- Create: `src/OKF4net.Mcp/Program.cs`
- Create: `tests/OKF4net.Tests/Mcp/OkfMcpConfigTests.cs`

**Interfaces:**
- Produces:
  - `public static class OkfMcpConfig` with
    `public static bool TryResolve(IReadOnlyList<string> args, Func<string, string?> getEnv, out string bundleRoot, out bool readOnly, out string? error)`
  - Environment variables: `OKF_BUNDLE_ROOT` (path), `OKF_MCP_READONLY` (truthy = `1`/`true`/`yes`/`on`, case-insensitive).

- [ ] **Step 1: Write the failing config tests**

Create `tests/OKF4net.Tests/Mcp/OkfMcpConfigTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Mcp;

namespace OKF4net.Tests.Mcp;

public sealed class OkfMcpConfigTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out var v) ? v : null;
    }

    [Fact]
    public void Missing_root_fails_and_names_the_env_var()
    {
        var ok = OkfMcpConfig.TryResolve([], Env(), out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("OKF_BUNDLE_ROOT", error);
    }

    [Fact]
    public void Nonexistent_root_fails_with_not_found()
    {
        var missing = Path.Combine(Path.GetTempPath(), "okf-does-not-exist-" + Guid.NewGuid().ToString("N"));

        var ok = OkfMcpConfig.TryResolve([missing], Env(), out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("not found", error);
    }

    [Fact]
    public void Arg_takes_precedence_and_defaults_to_read_write()
    {
        var dir = Directory.CreateTempSubdirectory("okf-cfg-").FullName;
        try
        {
            var ok = OkfMcpConfig.TryResolve([dir], Env(("OKF_BUNDLE_ROOT", "ignored")), out var root, out var readOnly, out var error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(dir, root);
            Assert.False(readOnly);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Env_root_used_when_no_arg_and_readonly_flag_parsed()
    {
        var dir = Directory.CreateTempSubdirectory("okf-cfg-").FullName;
        try
        {
            var ok = OkfMcpConfig.TryResolve(
                [],
                Env(("OKF_BUNDLE_ROOT", dir), ("OKF_MCP_READONLY", "1")),
                out var root,
                out var readOnly,
                out _);

            Assert.True(ok);
            Assert.Equal(dir, root);
            Assert.True(readOnly);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfMcpConfigTests"`
Expected: FAIL to compile — `OkfMcpConfig` does not exist.

- [ ] **Step 3: Implement `OkfMcpConfig`**

Create `src/OKF4net.Mcp/OkfMcpConfig.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Mcp;

/// <summary>
/// Resolves the server's startup configuration (bundle root + read-only flag)
/// from command-line arguments and environment variables. Pure and injectable
/// (<paramref name="getEnv"/>) so both success and failure paths are unit-testable.
/// </summary>
public static class OkfMcpConfig
{
    /// <summary>Environment variable naming the bundle root when no positional argument is given.</summary>
    public const string BundleRootEnv = "OKF_BUNDLE_ROOT";

    /// <summary>Environment variable enabling read-only mode when truthy.</summary>
    public const string ReadOnlyEnv = "OKF_MCP_READONLY";

    private static readonly IReadOnlySet<string> TruthyValues =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1", "true", "yes", "on" };

    /// <summary>
    /// Resolves configuration. The bundle root is the first non-blank argument,
    /// else the <c>OKF_BUNDLE_ROOT</c> environment variable. Returns
    /// <see langword="false"/> with a human-readable <paramref name="error"/>
    /// when no root is given or the root does not exist.
    /// </summary>
    /// <param name="args">Process arguments (positional bundle root at index 0).</param>
    /// <param name="getEnv">Environment-variable accessor (e.g. <see cref="Environment.GetEnvironmentVariable(string)"/>).</param>
    /// <param name="bundleRoot">The resolved bundle root (empty on failure).</param>
    /// <param name="readOnly">Whether read-only mode is requested.</param>
    /// <param name="error">The failure reason, or <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryResolve(
        IReadOnlyList<string> args,
        Func<string, string?> getEnv,
        out string bundleRoot,
        out bool readOnly,
        out string? error)
    {
        bundleRoot = string.Empty;
        readOnly = TruthyValues.Contains(getEnv(ReadOnlyEnv)?.Trim() ?? string.Empty);

        var root = args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : getEnv(BundleRootEnv);

        if (string.IsNullOrWhiteSpace(root))
        {
            error = $"no bundle root given. Pass it as the first argument or set {BundleRootEnv}.";
            return false;
        }

        root = root.Trim();
        if (!Directory.Exists(root))
        {
            error = $"bundle root not found: {root}";
            return false;
        }

        bundleRoot = root;
        error = null;
        return true;
    }
}
```

- [ ] **Step 4: Run the config tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfMcpConfigTests"`
Expected: PASS (all four).

- [ ] **Step 5: Implement `Program`**

Create `src/OKF4net.Mcp/Program.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using OKF4net.Mcp;

// Resolve configuration up front; a misconfigured launch must fail loudly on
// stderr (stdout is reserved for the JSON-RPC stream) with a non-zero code.
if (!OkfMcpConfig.TryResolve(args, Environment.GetEnvironmentVariable, out var bundleRoot, out var readOnly, out var error))
{
    Console.Error.WriteLine($"okf-mcp: {error}");
    Console.Error.WriteLine("Usage: okf-mcp <bundle-root>   (or set OKF_BUNDLE_ROOT; OKF_MCP_READONLY=1 for read-only)");
    return 2;
}

var builder = Host.CreateApplicationBuilder(args);

// stdio invariant: every log line goes to stderr so it can never corrupt the
// JSON-RPC protocol carried on stdout.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

builder.Services
    .AddMcpServer(options => options.ServerInfo = new Implementation { Name = "okf", Version = version })
    .WithStdioServerTransport();

// Register the OKF tools as singletons; the SDK collects every registered
// McpServerTool into the server's tool collection.
foreach (var tool in OkfMcpToolset.Build(bundleRoot, readOnly))
{
    builder.Services.AddSingleton(tool);
}

await builder.Build().RunAsync();
return 0;
```

- [ ] **Step 6: Build to verify the host compiles**

Run: `dotnet build src/OKF4net.Mcp/OKF4net.Mcp.csproj`
Expected: `Build succeeded` with 0 warnings (warnings are errors).

If `options.ServerInfo` / `Implementation` do not resolve against the pinned SDK version, remove the `AddMcpServer(...)` configuration lambda and call `AddMcpServer()` with no argument (the SDK then derives the server name from the assembly — `okf-mcp`). This is the only line whose exact SDK shape is version-sensitive; the tests do not depend on it.

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net.Mcp/OkfMcpConfig.cs src/OKF4net.Mcp/Program.cs tests/OKF4net.Tests/Mcp/OkfMcpConfigTests.cs
git commit -m "feat(mcp): stdio host with arg/env config resolution and read-only flag"
```

---

### Task 5: Documentation (project README + root README section)

**Files:**
- Modify: `src/OKF4net.Mcp/README.md` (replace the placeholder from Task 1)
- Modify: `README.md` (root — add a "Use OKF in Claude" section)

- [ ] **Step 1: Write the project README**

Replace the contents of `src/OKF4net.Mcp/README.md`:

````markdown
# OKF4net.Mcp

A local [Model Context Protocol](https://modelcontextprotocol.io) server that
exposes one OKF knowledge bundle to Claude Desktop / Claude Code as read/write
tools — the same way an Obsidian MCP server exposes a vault.

## Install

```sh
dotnet tool install -g OKF4net.Mcp
```

This installs the `okf-mcp` command.

## Use with Claude Desktop

Add an entry to `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "okf": {
      "command": "okf-mcp",
      "args": ["C:\\path\\to\\my-bundle"]
    }
  }
}
```

The bundle root may instead be supplied via the environment:

```json
{
  "mcpServers": {
    "okf": {
      "command": "okf-mcp",
      "env": { "OKF_BUNDLE_ROOT": "/path/to/my-bundle", "OKF_MCP_READONLY": "1" }
    }
  }
}
```

Set `OKF_MCP_READONLY=1` to serve the bundle for consultation only (the write
tools `okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes` are not
registered).

## Tools

`okf_read_concept`, `okf_browse`, `okf_graph`, `okf_search`, `okf_write_concept`,
`okf_append_log`, `okf_regenerate_indexes`, `okf_validate_bundle`,
`okf_changes_since`.

Each is the corresponding `OkfBundleTools` operation, so all OKF v0.1 behaviour,
producer-grade validation, path-safety, and locking apply unchanged.
````

- [ ] **Step 2: Add a section to the root README**

In `README.md` at the repo root, add a new section (place it after the existing project/usage overview — search for the section listing the projects or the CLI and insert after it):

```markdown
## Use OKF in Claude (MCP)

`OKF4net.Mcp` is a local MCP server that plugs an OKF bundle straight into
Claude Desktop / Claude Code, so you can read, search, and persist knowledge in
your bundle from a chat — the way an Obsidian MCP server exposes a vault.

```sh
dotnet tool install -g OKF4net.Mcp
```

Then point Claude Desktop at a bundle in `claude_desktop_config.json`:

```json
{ "mcpServers": { "okf": { "command": "okf-mcp", "args": ["/path/to/bundle"] } } }
```

See [`src/OKF4net.Mcp/README.md`](src/OKF4net.Mcp/README.md) for read-only mode
and the full tool list.
```

- [ ] **Step 3: Commit**

```bash
git add src/OKF4net.Mcp/README.md README.md
git commit -m "docs(mcp): document okf-mcp install and Claude Desktop setup"
```

---

### Task 6: Full verification pass

**Files:** none (verification only).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build OKF4net.sln`
Expected: `Build succeeded`, 0 warnings, 0 errors.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test OKF4net.sln`
Expected: all tests pass, including the new `OkfMcpServerTests` (3) and `OkfMcpConfigTests` (4), and every pre-existing test (no regressions).

- [ ] **Step 3: Verify formatting**

Run: `dotnet format OKF4net.sln --verify-no-changes`
Expected: no changes reported (exit 0). If it fails, run `dotnet format OKF4net.sln` and commit the formatting fixes.

- [ ] **Step 4: Smoke-test the tool package builds**

Run: `dotnet pack src/OKF4net.Mcp/OKF4net.Mcp.csproj -c Release`
Expected: `Successfully created package ...OKF4net.Mcp.<version>.nupkg` (confirms `PackAsTool` + readme packaging are valid).

- [ ] **Step 5: Commit any formatting fixes (if Step 3 required them)**

```bash
git add -A
git commit -m "style(mcp): apply dotnet format"
```

---

## Self-Review

**Spec coverage:**
- Project `src/OKF4net.Mcp/` (console exe, added to sln) → Task 1. ✅
- Thin façade over `OkfBundleTools.GetTools()` via `McpServerTool.Create(AIFunction)` → Task 1 (`OkfMcpToolset`). ✅
- One bundle per server, root via `args[0]` / `OKF_BUNDLE_ROOT` → Task 4 (`OkfMcpConfig`). ✅
- Read-only mode via `OKF_MCP_READONLY` dropping the three write tools → Task 2 + Task 4. ✅
- stdio discipline (logging to stderr) → Task 4 (`Program`). ✅
- Server metadata name `okf`, version from assembly → Task 4. ✅
- All nine tools, read+write → Task 1 (asserted). ✅
- Distribution as `.NET` global tool `okf-mcp` → Task 1 csproj (`PackAsTool`), Task 6 pack smoke test. ✅
- Integration tests: nine-tool list, read-only six-tool list, write→read round-trip, config errors → Tasks 1–4. ✅
- Docs incl. `claude_desktop_config.json` snippet → Task 5. ✅
- Library + CLI untouched, BCL-only, AOT preserved → no task modifies them. ✅
- No Native AOT for `.Mcp` → csproj omits `PublishAot`. ✅

**Placeholder scan:** The `src/OKF4net.Mcp/README.md` one-line file in Task 1 is a deliberate, explained placeholder replaced in full in Task 5 (needed so the csproj `<None Include="README.md">` glob resolves before the real content exists). No other placeholders. The only version-sensitive API line (`ServerInfo`/`Implementation` in `Program`) has an explicit fallback in Task 4 Step 6.

**Type consistency:** `OkfMcpToolset.Build(string, bool)` and `OkfMcpConfig.TryResolve(...)` signatures are identical everywhere they appear (definition, tests, `Program`). Tool name strings are consistent between `WriteToolNames`, the nine-tool assertion, and the read-only assertion. `ConnectAsync` and `ResultText` helpers are defined once in `OkfMcpServerTests` and reused.
