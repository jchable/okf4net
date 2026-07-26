# OKF MCP Server — Design Spec

- **Date:** 2026-07-24
- **Status:** Approved (brainstorm), pending implementation plan
- **Topic:** Expose an OKF bundle to Claude (Desktop / Code) as a local MCP server, the way `mcp-obsidian` exposes an Obsidian vault.

## Motivation

An OKF bundle is structurally an Obsidian-style vault: a directory of markdown files with YAML frontmatter and cross-links. The way any tool (Obsidian included) plugs a vault into Claude is a **Model Context Protocol (MCP) server**: Claude Desktop / Claude Code launch it as a child process and call its tools in natural language ("note this", "what do I know about X", "link A to B"), persisting knowledge back to disk.

OKF4net already implements every operation such a server needs. [`OkfBundleTools`](../../../src/OKF4net.Agents/OkfBundleTools.cs) exposes nine hardened operations (`read_concept`, `browse`, `graph`, `search`, `write_concept`, `append_log`, `regenerate_indexes`, `validate_bundle`, `changes_since`) as `Microsoft.Extensions.AI` `AIFunction`s via `GetTools()`. What is missing is the MCP transport layer. This spec defines a thin façade that re-exposes those same `AIFunction`s over MCP stdio — no business logic is duplicated.

## Scope

**In scope**
- A new console executable project `src/OKF4net.Mcp/` that serves **one** OKF bundle over MCP stdio.
- Wiring the nine existing `OkfBundleTools` operations as MCP tools.
- Startup configuration (bundle root, read-only toggle) via CLI arg / environment variables.
- Distribution as a .NET global tool (`okf-mcp`).
- Integration tests for wiring + protocol round-trip.
- Documentation, including a ready-to-paste `claude_desktop_config.json` snippet.

**Out of scope (YAGNI for v1)**
- Remote / hosted MCP (HTTP/SSE, OAuth) for claude.ai — a possible later phase; the architecture must not preclude it, but nothing is built for it now.
- Multi-bundle serving from a single process (one bundle per server instance; multiple bundles = multiple server entries in the client config, the Obsidian model).
- Exposing concepts as MCP *resources* or *prompts* — tools only for v1.
- Native AOT for this project (see Non-Goals).
- Any change to `OKF4net`, `OKF4net.Cli`, or the existing `OkfBundleTools` logic. The library and CLI remain BCL-only; the CLI remains AOT.

## Non-Goals / Constraints

- **Zero-dependency rule is unchanged for library + CLI.** The rule is per-project. `src/OKF4net/` and `src/OKF4net.Cli/` stay BCL-only. `src/OKF4net.Mcp/` is a new project permitted a third-party dependency (the official MCP SDK), consistent with how `src/OKF4net.Agents/` already depends on `Microsoft.Agents.AI`.
- **No Native AOT for `.Mcp`.** The MCP SDK and `AIFunctionFactory` are reflection-based; AOT is not a goal here. The `okf` CLI's AOT guarantee is untouched because `.Mcp` is a separate project.
- **Fixtures untouched.** No golden fixtures are involved.
- **Spec fidelity preserved by reuse.** Because every tool is the existing `OkfBundleTools` method unchanged, all OKF v0.1 behaviour, producer validation, path-safety (reparse-point rejection, containment), the shared write lock, and the "tools never throw toward the LLM" guarantee carry over for free.

## Architecture

### Project layout
- New project `src/OKF4net.Mcp/`, `OutputType=Exe`, `net10.0`, added to `OKF4net.sln`.
- Dependencies:
  - `ModelContextProtocol` (official C# MCP SDK) — the tool/transport layer.
  - `Microsoft.Extensions.Hosting` — the generic host used by the SDK's stdio server.
  - `ProjectReference` → `OKF4net.Agents` (brings `OkfBundleTools`; `Microsoft.Agents.AI` comes transitively and is harmless/unused here).
- Packaged as a .NET global tool: `PackAsTool=true`, `ToolCommandName=okf-mcp`, `PackageId=OKF4net.Mcp`.

### Core wiring (`Program.cs`, ~100 lines)
1. Resolve the bundle root (see Configuration). On failure, write a clear message to **stderr** and exit non-zero.
2. Resolve read-only mode (see Configuration).
3. `var tools = new OkfBundleTools(root).GetTools();` — nine `AIFunction`s (via `AITool`).
4. If read-only, drop the three write tools (`okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes`) by name.
5. For each remaining function, register `McpServerTool.Create(fn)` on the server.
6. Build the host with `AddMcpServer().WithStdioServerTransport()` and the registered tools; `await host.RunAsync()`.

`McpServerTool.Create(AIFunction)` is the SDK's supported adapter from an `AIFunction` to an MCP tool; tool names and descriptions come from the functions themselves (which already carry the snake_case names and `[Description]` text), so there is a single source of truth and no drift.

### Data flow
```
Claude Desktop/Code  --stdio(JSON-RPC)-->  okf-mcp  -->  OkfBundleTools  -->  bundle on disk
                     <--tool results-----          <--markdown/report--
```

## Configuration

Resolved at startup, in this order:

- **Bundle root:** `args[0]` if present, else environment variable `OKF_BUNDLE_ROOT`. If neither is set, or the path does not exist, print a one-line usage/error to stderr and exit non-zero. (`OkfBundleTools`' constructor already throws `ArgumentException` when the root is missing; catch it and translate to the stderr message + exit code.)
- **Read-only mode:** environment variable `OKF_MCP_READONLY` — when set to a truthy value (`1`/`true`, case-insensitive), the three write tools are not registered, so the bundle is served for consultation only. Default: read-write.

## stdio discipline (critical)

On stdio transport, **stdout is reserved exclusively for the JSON-RPC stream.** All logging MUST go to stderr:
```csharp
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
```
Any stray `Console.WriteLine` to stdout corrupts the protocol. This is an invariant, not a preference.

## Tools exposed

All nine `OkfBundleTools` operations, read + write (read-only mode drops the three writers):

| Tool | Kind | Purpose |
|------|------|---------|
| `okf_read_concept` | read | Frontmatter, body, outgoing links, backlinks of one concept |
| `okf_browse` | read | Progressive-disclosure listing of a directory (index.md or generated) |
| `okf_graph` | read | Bundle-wide link stats, or one concept's links/backlinks/broken links |
| `okf_search` | read | Ranked full-text search over titles/descriptions/tags/bodies |
| `okf_write_concept` | write | Create/update a concept (producer validation before write) |
| `okf_append_log` | write | Append a dated entry to the bundle `log.md` |
| `okf_regenerate_indexes` | write | Regenerate every `index.md` |
| `okf_validate_bundle` | read | OKF v0.1 §9 conformance report |
| `okf_changes_since` | read | Changes since an ISO date, aggregated across logs |

## Server metadata

- Server name: `okf`.
- Version: read from the assembly informational version.

## Packaging & distribution

- **Primary:** .NET global tool. `dotnet tool install -g OKF4net.Mcp` installs the `okf-mcp` command. Published to nuget.org alongside the existing packages (fits the current NuGet Trusted-Publishing release flow).
- Claude Desktop config snippet (documented in the README):
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
  (Alternatively the bundle root can be supplied via `"env": { "OKF_BUNDLE_ROOT": "..." }`, and `"OKF_MCP_READONLY": "1"` for consultation-only.)

## Testing

- **Integration (wiring + protocol):** using the MCP SDK's in-process client/transport, start the server against a temporary bundle and assert:
  - `tools/list` returns the nine expected tool names (and only six when `OKF_MCP_READONLY` is set).
  - A round-trip: `okf_write_concept` a new concept → `okf_read_concept` returns it → `okf_validate_bundle` reports conformant.
- **Configuration errors:** missing/nonexistent bundle root exits non-zero with a stderr message and does not start serving.
- Tool *content* is already covered by the existing `OkfBundleTools` tests; these tests deliberately do not re-assert it — they cover the MCP layer only.
- Tests live in `tests/OKF4net.Tests/` (the existing project) unless the MCP SDK's test transport forces a separate host, in which case a dedicated test project is acceptable.

## Risks & mitigations

- **stdout pollution breaks the protocol** → enforce stderr-only logging; add a code comment marking it an invariant; the integration test exercises a real JSON-RPC round-trip which would fail on pollution.
- **SDK API surface drift** (`McpServerTool.Create`, `WithStdioServerTransport`) → pin the `ModelContextProtocol` package version; verified against current SDK docs at design time.
- **Transitive `Microsoft.Agents.AI` via `OKF4net.Agents`** → accepted (approach A); unused at runtime, no functional cost. A later refactor extracting `OkfBundleTools` into a dependency-lighter project remains mechanical if ever wanted.

## Future (explicitly deferred)

- Remote/hosted MCP for claude.ai (HTTP/SSE + OAuth).
- MCP resources/prompts exposing concepts directly.
- Multi-bundle serving.
