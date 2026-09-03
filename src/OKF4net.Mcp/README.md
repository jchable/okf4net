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
      "env": { "OKF_BUNDLE_ROOT": "/path/to/my-bundle" }
    }
  }
}
```

The server is **read-only by default**: the write tools `okf_write_concept`,
`okf_append_log` and `okf_regenerate_indexes` are not registered unless you set
`OKF_MCP_WRITABLE=1`.

Bundle content is untrusted — it comes from files another agent or a human
contributor may have written — so an injection carried in a concept body can
only cause a persistent change if a write tool is reachable. Serving a bundle
for consultation is both the common case and the safe one, so it is what you
get for free.

`OKF_MCP_READONLY=1` is still accepted and still forces read-only. It wins over
`OKF_MCP_WRITABLE` when both are set, so an existing configuration keeps
working and keeps meaning the same thing.

> **Changed default.** Earlier versions registered the write tools unless
> `OKF_MCP_READONLY=1` was set. If you relied on that, add
> `OKF_MCP_WRITABLE=1`.

## Bundle resolution order

`okf-mcp` resolves its bundle root in this order:

1. The first positional argument.
2. The `OKF_BUNDLE_ROOT` environment variable.
3. **Convention discovery**: starting from the current working directory and
   walking up, the first directory that is a *marked* bundle — testing at
   each level the directory itself, then its `knowledge/` child. A marked
   bundle has a root `index.md` whose frontmatter declares `okf_version`
   (§12).

Discovery is deliberately strict — an unmarked bundle (no `okf_version` in
its root `index.md`) is **not** discovered, so a writable server can never
mistake an arbitrary docs directory for a bundle. Mark the bundle (add
`okf_version` to the root `index.md` frontmatter, e.g. via the OKF Claude
Code plugin's `/okf-init`) or use an explicit root.

Note for Claude Desktop: Desktop spawns servers with an unrelated working
directory, so discovery does not apply there — keep the positional argument
or `OKF_BUNDLE_ROOT` in `claude_desktop_config.json`.

## Tools

`okf_read_concept`, `okf_browse`, `okf_graph`, `okf_search`, `okf_audit`,
`okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes`, `okf_validate_bundle`,
`okf_changes_since`, `okf_get_computation`.

Each is the corresponding `OkfBundleTools` operation, so all OKF v0.2 behaviour,
producer-grade validation, path-safety, and locking apply unchanged.

That's eight tools by default (the read-only ones above), or eleven when
`OKF_MCP_WRITABLE=1` adds the three write tools.
`okf_get_computation` reads a §10 attested-computation concept's contract and
sanctioned computation source — read-only, no attestation runtime needed.
`okf_audit` reads the bundle's trust/freshness/lifecycle signals — also
read-only. The twelfth `OkfBundleTools` tool, `okf_run_computation`, is
**not** exposed by this server: it only appears in `GetTools()` when the tool
set is constructed with an `OKF4net.Attestation` `AttestationOrchestrator`
wired in, and this server starts `OkfBundleTools` with no orchestrator (it
wires no host-specific binder/executor/attester runtime). Embed
`OKF4net.Agents` directly if you need `okf_run_computation`.
