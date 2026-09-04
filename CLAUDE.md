# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

OKF4net — a zero-dependency .NET (C# / net10.0) implementation of the Open Knowledge Format (OKF) v0.2: knowledge bundles as directories of markdown files with YAML frontmatter. It is an independent implementation built from the OKF spec, backed by an extensive test suite including byte-exact golden CLI captures (see `tests/fixtures/`). Licensed LGPL-3.0-or-later; portions derived from upstream Apache-2.0 work remain Apache-2.0 (see NOTICE).

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

- **Zero third-party runtime dependencies, per project.** `src/OKF4net/`, `src/OKF4net.Cli/`, `src/OKF4net.Catalog/`, and `src/OKF4net.Attestation/`: BCL only — the library has its own YAML-subset parser, link scanner, and CLI arg parsing; do not add packages there. `OKF4net.Attestation` references only `OKF4net` (host-plugged contracts + `AttestationOrchestrator`); it is in turn referenced by `OKF4net.Agents`. `src/OKF4net.Agents/` references exclusively `Microsoft.Agents.AI` (plus `OKF4net.Attestation`). `src/OKF4net.Catalog.Hosting/` is the one explicit dependency-policy exception: it references exclusively `Microsoft.Extensions.DependencyInjection.Abstractions`, so catalog sources can be registered with a host's `IServiceCollection` — the core (`OKF4net.Catalog` and below) stays zero-dependency. `src/OKF4net.Mcp/` is a leaf executable (the `okf-mcp` `dotnet tool`), not a published library API: it composes `OKF4net.Agents`' tools over stdio and is the only project referencing `ModelContextProtocol` and `Microsoft.Extensions.Hosting`; those deps are allowed there and nothing else may depend on it. Test-only packages (xunit, etc.) are fine everywhere.
- **Never touch `tests/fixtures/` to make a failing test pass.** These are byte-exact golden captures of the reference CLI output (LF endings, significant trailing whitespace; protected by `.gitattributes -text`) — the source of truth for OKF **v0.1** conformance (provenance is in `tests/fixtures/README.md`). If C# output differs from a golden file for v0.1-covered behaviour, treat it as a regression to investigate on the C# side — never hand-edit the fixture. **Exception: behaviour not yet covered by any existing golden capture** may add new fixtures when hand-verified against the applicable spec text, not a reference binary, and named/documented as such — and may revise an existing v0.1 fixture only where a deliberate spec version bump intentionally changes the exact output it captures, citing the spec section (§) that changed.
- **Spec fidelity.** Behaviour must conform to the OKF v0.2 spec; behavioural changes should cite the spec section (§) and intentional divergences from the reference implementation need a documented reason.
- **A green xunit run says nothing about code xunit cannot execute.** The suite runs on .NET, so it cannot execute the JavaScript in `src/OKF4net.Viewer/Assets/`. A test that greps a `.js` file for a marker string passes just as happily when the code behind that marker has been gutted — this is not hypothetical: two "raw HTML disabled" tests were green while the sanitizer had an exploitable hole. Any non-.NET code carrying a real guarantee needs its own executable guard (for the viewer: `tools/viewer-security-check/`, run in CI as the `viewer sanitizer (JS)` job), and a source-text assertion must say in its own comment that it is a smoke check, not proof.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- File-scoped namespaces, XML doc comments on public API, nullable enabled (all enforced via Directory.Build.props: `TreatWarningsAsErrors`, LangVersion 14).

## Architecture

- **`src/OKF4net/`** — the library. One file per spec concern, following the OKF reference implementation's structure: `ConceptId` (§2), `Bundle` (§3, permissive loading — parse failures go into `Bundle.ParseErrors`, never abort), `OkfDocument`/`Frontmatter` (§4), `Links.cs`/`LinkScanner` (§6, legacy citations §13.1), `IndexGenerator` (§8), `ChangeLog` (§9), `Validate.cs`/`BundleValidator` (§11). The README has the full spec-section → type mapping table.
  - `ConceptSearch` — the single shared full-text scorer (title x3, tags/description x2, body x1) used by both `OKF4net.Agents` (`okf_search`/context provider) and `OKF4net.Catalog` (`OkfBundleKnowledgeSource`, `FileMemoryStore`); do not fork a second scorer in either consumer.
  - `Audit.cs` — `ConceptAudit`, the single shared corpus-level query behind both `okf audit` and the `okf_audit` tool; the two renderers are deliberately separate (the CLI's bytes are golden-locked), but the computation and the `AuditVocabulary` labels must not be forked.
  - `Yaml/` — the documented YAML *subset* (scalars, lists, shallow maps, block/flow, `|`/`>`); it deliberately rejects anchors/tags/multi-docs with clear errors. `Frontmatter` wraps an order-preserving `YamlMapping` with typed getters rather than a fixed DTO, so unknown producer keys survive round-trips.
  - `Internal/LfLines.cs` — the single shared line splitter (splits on `\n` only, stripping a preceding `\r`). Use it anywhere `\n`-based line splitting matters; do not reintroduce private copies.
  - `Internal/ReparsePoints.cs` — internal symlink/junction detection; `OKF4net.Catalog` is granted `InternalsVisibleTo` so it can reuse this seam rather than duplicating a platform-specific implementation.
- **`src/OKF4net.Cli/`** — the `okf` binary (`validate`/`audit`/`info`/`index`/`graph`/`parse`/`fmt`/`render`), published Native AOT (`PublishAot`, `InvariantGlobalization`). All logic lives in `OkfCli.Run(args, out, err)` so tests invoke it in-process without spawning a process.
- **`src/OKF4net.Attestation/`** — zero-dep §10 attested-computation orchestration, referencing only `OKF4net`. Defines the host-plugged contracts (`IParameterBinder`, `IComputationExecutor`, `IAttester`, resolved per concept's `runtime` field through `IAttestationRuntimeRegistry`) and the value types that flow between them (`BoundComputation`, `Receipt`, `AttestationVerdict`, `AttestationContext`, `AttestationOutcome`); `AttestationOrchestrator.RunAsync` drives one run end to end (resolve → bind → execute → receipt-shape check → attest → gate on verdict + `stale_after`), errors-as-data, never writing a verdict back to the bundle (§10.6). Referenced by `OKF4net.Agents` to back `okf_run_computation`.
- **`src/OKF4net.Agents/`** — Microsoft Agent Framework layer exposing OKF bundle operations as function tools (e.g. `OkfBundleTools`) plus `OkfContextProvider`, an `AIContextProvider` that auto-injects budget-bounded bundle context and captures deterministic per-day memory concepts; the only project depending on `Microsoft.Agents.AI`.
- **`src/OKF4net.Catalog/`** — knowledge-catalog model and logic, referencing only `OKF4net` (BCL otherwise; zero `PackageReference`). Depended on by `OKF4net.Catalog.Hosting`. Each manifest source carries a `role` (`SourceRole`): `Knowledge` (read-only, searched by `IKnowledgeResolver`) or `Memory` (writable, scoped by a required `tier` — `session`/`user`/`tenant`, all three backed by `FileMemoryStore`, fed by `IMemoryStore`, never searched by the resolver); any other `role` string in `catalog.json` is rejected (`CatalogDiagnosticCode.IllegalRole`).
- **`src/OKF4net.Catalog.Hosting/`** — host-integration layer for the catalog, referencing only `OKF4net.Catalog`. This is the sole project allowed a `Microsoft.Extensions.*` package (`Microsoft.Extensions.DependencyInjection.Abstractions`) — an explicit, narrowly-scoped exception to the zero-dependency rule so catalog sources can register with a host's `IServiceCollection`; the core dependency graph (`OKF4net.Catalog` → `OKF4net`) stays zero-dependency and acyclic.
- **`src/OKF4net.Viewer/`** — static HTML site generation for a bundle, referencing only `OKF4net` (BCL otherwise; zero `PackageReference`). Backs the `okf render` verb. Three units: `SiteModel` (pure `Bundle` → display-model projection), `HtmlWriter` (the only I/O), `ViewerAssets` (embedded CSS/JS). Markdown is rendered **client-side** by a vendored copy of marked (MIT, v15.0.12, credited in `NOTICE`) — the generated page carries its raw markdown plus a link-rewiring table as an HTML-safe JSON payload, escaped by `HtmlSafeJson` so untrusted bundle content cannot break out of the `<script>` container. Raw HTML is neutralized by sanitizing the **parsed DOM** in `viewer.js`, not by marked itself (it has no `sanitize` option any more) and not by patching marked's renderer hooks (tried and dropped — see below): an element allowlist (gated for a handful of tags by an attribute-value constraint table, e.g. `<input>` survives only as `type="checkbox"`, forced `disabled`, since a screen reader announces a real checkbox with its state where a decorative glyph would lose it), a per-tag attribute allowlist that drops every `on*` handler, URL-scheme validation on `href`/`src`, and an opaque-tags table (`<script>`/`<style>`) dropped with no text kept, since their content is source, not prose. This sanitizer is the whole defense, not one layer of it — renderer-hook patching (suppressing marked's `html` renderer output) was tried and measured against the vendored build plus the hostile-payload battery in `tools/viewer-security-check/`: it stopped nothing the sanitizer alone didn't already stop, while it silently deleted benign wrapped content (e.g. `<details><summary>...</summary>body</details>` rendered as `""` instead of keeping "body"), because marked's `Renderer.image()` interpolates the `alt` attribute with no escaping at all — `![foo" onerror="alert(1)](x.png)` breaks out of the attribute with no raw-HTML token involved, a class of bug no renderer-hook override can see, let alone stop; no amount of patching marked's hooks bounds that class in general. **Do not reintroduce renderer-hook patching as "extra defense in depth"** — it buys no security property the sanitizer lacks and reintroduces the content-loss bug. xunit runs on .NET and cannot execute JavaScript, so `tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs` only smoke-checks for source-text markers (allowlist names, rejected schemes) — those tests stay green even if the sanitizer is gutted, and are **not** proof it works. The real guard is `tools/viewer-security-check/`, a Node/jsdom harness that runs the actual vendored `marked.min.js` and `viewer.js` against hostile payloads; CI runs it as the `viewer sanitizer (JS)` job. **Whenever you re-vendor `marked.min.js` or edit `viewer.js`, that harness is what tells you whether the defense still holds** — add a case to it for any new payload class you discover. No full-text search by design: a static site has no server to run `ConceptSearch`, and mirroring its weights in JS would fork the scorer — search lands with the planned `okf serve`.
- **`src/OKF4net.Mcp/`** — a local [Model Context Protocol](https://modelcontextprotocol.io) server exposing one OKF bundle over stdio, published as the `okf-mcp` `dotnet tool` (`PackAsTool`, no Native AOT). Thin entry point: `Program.cs` resolves the bundle root + read-only flag via `OkfMcpConfig` (testable static; on misconfig it prints a one-line usage/error to stderr and exits non-zero) and starts a stdio host serving `OKF4net.Agents`' `OkfBundleTools`. **stdio invariant: stdout is reserved for the JSON-RPC stream, every log line goes to stderr.** Tests live in `tests/OKF4net.Tests/Mcp/`.
- **`tests/OKF4net.Tests/`** — xunit. `GoldenParityTests` diffs CLI output byte-for-byte against `tests/fixtures/golden/`; tests locate the repo root by walking up from the test assembly to `OKF4net.sln`. Some parity tests temporarily set the CWD to the repo root because goldens embed the relative bundle path as given on the command line. Catalog and Catalog.Hosting tests live here too (`Catalog/`) rather than in separate test projects.

Two validation levels exist by design: `OkfDocument.ValidateConformance()` enforces only what §11 requires (non-empty `type`); `OkfDocument.Validate()` is the stricter producer-side check (`type`, `title`, `description` — `Frontmatter.RequiredKeys`). `timestamp` is a *legacy* §13.1 field since the v0.2 bump: provenance is the `generated` stamp, which `BundleConceptWriter` auto-stamps on writes that omit it *when its opt-in `AutoStampGenerated` flag is set* — it is not unconditional for every write. The two §13.1 renames (`timestamp`→`generated.at`, body `# Citations`→frontmatter `sources`) are both v0.2-conformant fallbacks (a v0.2 consumer reads the new form, falling back to the legacy one; a v0.1 bundle loads unchanged) and both surface as a `Warning` in `BundleValidator.Validate` — kept equally weighted rather than treating `timestamp` as a quieter "simple rename" (see `docs/superpowers/specs/2026-07-27-okf-v0.2-upgrade-design.md`, item 10).

**`docs/spec/SPEC.md` is the OKF v0.2 specification itself**, vendored verbatim
(Copyright Google LLC, Apache-2.0, upstream commit `62432a0`) so every `§`
citation in this repo resolves locally instead of against whatever upstream
`main` says today. **Read it before answering any conformance question, and
never edit it** — it is not ours. To adopt a newer spec version, re-download it
and update the whole provenance table (commit, date, `sha256`, size) in
`docs/spec/README.md`; `.gitattributes` marks it `-text` so the checksum stays
verifiable on every platform. Deliberate divergences are recorded in
`docs/spec-conformance/`, never by editing the spec. Note that upstream's own
sample bundles can drift from it: `bundles/acme_retail/` (also a verbatim copy)
violates §5's timestamp rule, and `okf validate` correctly warns about it —
that is upstream drift to report upstream, not a local fix.

`docs/design/` holds historical migration specs/plans — context only; the code and README are authoritative.

`bundles/` holds sample OKF bundles for manual testing/demos (e.g.
`bundles/acme_retail/`, `bundles/ga4/`) — distinct from `tests/fixtures/`,
which stays byte-exact golden captures. `samples/` holds standalone example
projects that consume those bundles (each with its own solution/build, not
part of `OKF4net.sln` or CI).

`producers/OkfProducer` is a standalone native OKF producer CLI (`okfgen`;
`OkfProducer.sln`: `OkfProducer.Core` + `OkfProducer.CodeGraph.TreeSitter` +
`OkfProducer.CodeGraph.Roslyn` + `OkfProducer.Cli`, `generate`/`validate`
commands via System.CommandLine + Generic Host) that scans a repository
(`RepositoryScanner`) and generates an OKF v0.2 bundle from it
(`ConceptGenerator`, `BundleWriter`, built on `OkfDocumentBuilder`), including a
C# code-graph stage — one concept per namespace/type/member with resolved
`## Calls` links. `OkfProducer.Cli` is the composition root: the only project
that references everything, and therefore the only place the pipeline can be
assembled. Same status as `samples/`: its own solution, references
`src/OKF4net`, not part of `OKF4net.sln`, not published to NuGet, and exempt
from the zero-dependency rule above (`Microsoft.Extensions.Hosting`,
`System.CommandLine`, `Microsoft.CodeAnalysis.CSharp`, `TreeSitter.DotNet` —
`OkfProducer.Core` itself still references only `OKF4net`). **`producers/` is
outside CI by decision (2026-08-01), not by omission**, so the guarantee is one
local command — `dotnet test producers/OkfProducer.sln` — stated at the top of
`producers/README.md`; run it before touching the producer and after any public
`OKF4net` API change. Remaining follow-ups (more ecosystems, per-RID package
weight) are in `ROADMAP.md`.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).
