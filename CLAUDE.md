# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OKF4net — a zero-dependency .NET (C# / net10.0) implementation of the Open Knowledge Format (OKF) v0.1: knowledge bundles as directories of markdown files with YAML frontmatter. It is an independent implementation built from the OKF spec, backed by an extensive test suite including byte-exact golden CLI captures (see `tests/fixtures/`). Licensed LGPL-3.0-or-later; portions derived from upstream Apache-2.0 work remain Apache-2.0 (see NOTICE).

## Commands

```sh
dotnet build OKF4net.sln                    # build everything (warnings are errors)
dotnet test OKF4net.sln                     # full test suite incl. golden CLI comparisons
dotnet test OKF4net.sln --filter "FullyQualifiedName~ConceptIdTests"        # one test class
dotnet test OKF4net.sln --filter "FullyQualifiedName~ConceptIdTests.Parse"  # one test method
dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Agents"  # OKF4net.Agents tests only
dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Mcp"     # OKF4net.Mcp tests only
dotnet format OKF4net.sln                   # format; CI runs --verify-no-changes
dotnet publish src/OKF4net.Cli -c Release   # Native AOT self-contained `okf` binary
```

Requires .NET SDK 10.0+. CI (ci.yml) runs build+test on Linux/Windows/macOS, `dotnet format --verify-no-changes`, and an AOT publish smoke test — all three must pass.

## Hard rules

- **Zero third-party runtime dependencies, per project.** `src/OKF4net/`, `src/OKF4net.Cli/`, and `src/OKF4net.Catalog/`: BCL only — the library has its own YAML-subset parser, link scanner, and CLI arg parsing; do not add packages there. `src/OKF4net.Agents/` references exclusively `Microsoft.Agents.AI`. `src/OKF4net.Catalog.Hosting/` is the one explicit dependency-policy exception: it references exclusively `Microsoft.Extensions.DependencyInjection.Abstractions`, so catalog sources can be registered with a host's `IServiceCollection` — the core (`OKF4net.Catalog` and below) stays zero-dependency. `src/OKF4net.Mcp/` is a leaf executable (the `okf-mcp` `dotnet tool`), not a published library API: it composes `OKF4net.Agents`' tools over stdio and is the only project referencing `ModelContextProtocol` and `Microsoft.Extensions.Hosting`; those deps are allowed there and nothing else may depend on it. Test-only packages (xunit, etc.) are fine everywhere.
- **Never touch `tests/fixtures/`.** These are byte-exact golden captures of the reference CLI output (LF endings, significant trailing whitespace; protected by `.gitattributes -text`). If C# output differs from a golden file, treat it as a regression to investigate on the C# side — never hand-edit the fixture. (Provenance is in `tests/fixtures/README.md`.)
- **Spec fidelity.** Behaviour must conform to the OKF v0.1 spec; behavioural changes should cite the spec section (§) and intentional divergences from the reference implementation need a documented reason.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- File-scoped namespaces, XML doc comments on public API, nullable enabled (all enforced via Directory.Build.props: `TreatWarningsAsErrors`, LangVersion 14).

## Architecture

- **`src/OKF4net/`** — the library. One file per spec concern, following the OKF reference implementation's structure: `ConceptId` (§2), `Bundle` (§3, permissive loading — parse failures go into `Bundle.ParseErrors`, never abort), `OkfDocument`/`Frontmatter` (§4), `Links.cs`/`LinkScanner` (§5/§8), `IndexGenerator` (§6), `ChangeLog` (§7), `Validate.cs`/`BundleValidator` (§9). The README has the full spec-section → type mapping table.
  - `ConceptSearch` — the single shared full-text scorer (title x3, tags/description x2, body x1) used by both `OKF4net.Agents` (`okf_search`/context provider) and `OKF4net.Catalog` (`DefaultKnowledgeResolver`); do not fork a second scorer in either consumer.
  - `Yaml/` — the documented YAML *subset* (scalars, lists, shallow maps, block/flow, `|`/`>`); it deliberately rejects anchors/tags/multi-docs with clear errors. `Frontmatter` wraps an order-preserving `YamlMapping` with typed getters rather than a fixed DTO, so unknown producer keys survive round-trips.
  - `Internal/LfLines.cs` — the single shared line splitter (splits on `\n` only, stripping a preceding `\r`). Use it anywhere `\n`-based line splitting matters; do not reintroduce private copies.
  - `Internal/ReparsePoints.cs` — internal symlink/junction detection; `OKF4net.Catalog` is granted `InternalsVisibleTo` so it can reuse this seam rather than duplicating a platform-specific implementation.
- **`src/OKF4net.Cli/`** — the `okf` binary (`validate`/`info`/`index`/`graph`/`parse`/`fmt`), published Native AOT (`PublishAot`, `InvariantGlobalization`). All logic lives in `OkfCli.Run(args, out, err)` so tests invoke it in-process without spawning a process.
- **`src/OKF4net.Agents/`** — Microsoft Agent Framework layer exposing OKF bundle operations as function tools (e.g. `OkfBundleTools`) plus `OkfContextProvider`, an `AIContextProvider` that auto-injects budget-bounded bundle context and captures deterministic per-day memory concepts; the only project depending on `Microsoft.Agents.AI`.
- **`src/OKF4net.Catalog/`** — knowledge-catalog model and logic, referencing only `OKF4net` (BCL otherwise; zero `PackageReference`). Depended on by `OKF4net.Catalog.Hosting`. Each manifest source carries a `role` (`SourceRole`); V1 recognizes only `Knowledge` (read-only, searched by the resolver) — a reserved `Memory` role for V2 scoped memory is deliberately not defined yet, and any other `role` string in `catalog.json` is rejected (`CatalogDiagnosticCode.IllegalRole`).
- **`src/OKF4net.Catalog.Hosting/`** — host-integration layer for the catalog, referencing only `OKF4net.Catalog`. This is the sole project allowed a `Microsoft.Extensions.*` package (`Microsoft.Extensions.DependencyInjection.Abstractions`) — an explicit, narrowly-scoped exception to the zero-dependency rule so catalog sources can register with a host's `IServiceCollection`; the core dependency graph (`OKF4net.Catalog` → `OKF4net`) stays zero-dependency and acyclic.
- **`src/OKF4net.Mcp/`** — a local [Model Context Protocol](https://modelcontextprotocol.io) server exposing one OKF bundle over stdio, published as the `okf-mcp` `dotnet tool` (`PackAsTool`, no Native AOT). Thin entry point: `Program.cs` resolves the bundle root + read-only flag via `OkfMcpConfig` (testable static; on misconfig it prints a one-line usage/error to stderr and exits non-zero) and starts a stdio host serving `OKF4net.Agents`' `OkfBundleTools`. **stdio invariant: stdout is reserved for the JSON-RPC stream, every log line goes to stderr.** Tests live in `tests/OKF4net.Tests/Mcp/`.
- **`tests/OKF4net.Tests/`** — xunit. `GoldenParityTests` diffs CLI output byte-for-byte against `tests/fixtures/golden/`; tests locate the repo root by walking up from the test assembly to `OKF4net.sln`. Some parity tests temporarily set the CWD to the repo root because goldens embed the relative bundle path as given on the command line. Catalog and Catalog.Hosting tests live here too (`Catalog/`) rather than in separate test projects.

Two validation levels exist by design: `OkfDocument.ValidateConformance()` enforces only what §9 requires (non-empty `type`); `OkfDocument.Validate()` is the stricter producer-side check (`type`, `title`, `description`, `timestamp`).

`docs/design/` holds historical migration specs/plans — context only; the code and README are authoritative.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
