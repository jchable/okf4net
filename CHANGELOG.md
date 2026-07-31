# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **`IndexGenerator.RegenerateIndexesWith` no longer erases the bundle-root
  `index.md`'s `okf_version` marker.** Regeneration rebuilt every `index.md`
  from scratch — entries only, no frontmatter block at all — so a bundle
  marked with `okf_version` (§12) lost that marker the moment any concept
  write triggered `okf_regenerate_indexes`. That silently broke `okf-mcp`'s
  bundle auto-discovery on the next server start (`no bundle root given and
  no marked bundle found`), even though the bundle had been correctly marked
  and previously discovered fine. The write path now preserves the root
  `index.md`'s existing frontmatter (read permissively — a file that fails
  to read or parse is left untouched rather than silently rewritten) and
  only regenerates the body; non-root `index.md` files are unaffected and
  still self-heal any stray frontmatter (§8) on the next regeneration, as
  before.

## [0.4.0] - 2026-07-30

### Added

- **`okf-mcp` bundle auto-discovery.** When neither a positional root nor
  `OKF_BUNDLE_ROOT` is given, `okf-mcp` now walks up from the current working
  directory looking for a *marked* bundle (a root `index.md` whose
  frontmatter declares `okf_version`, testing each level's directory then
  its `knowledge/` child). Discovery is deliberately strict — an unmarked
  directory is never mistaken for a bundle, so a writable server can't
  accidentally start against an arbitrary docs folder. The resolved bundle
  root is announced on startup. Does not apply to Claude Desktop, which
  spawns servers with an unrelated working directory — keep the positional
  argument or `OKF_BUNDLE_ROOT` there.
- **`OkfBundleTools.WriteToolNames`**, a new public property naming the
  three tools that mutate a bundle (`okf_write_concept`, `okf_append_log`,
  `okf_regenerate_indexes`) — the single source of truth for a host building
  a read-only tool set, instead of hand-maintaining its own copy of the list.

### Fixed

- **`ComputationExtractor`'s `# Computation` heading match no longer
  misfires inside an earlier, unrelated fenced code block.** The heading
  scan was blind to fence state: a heading-like line trimming to
  `# Computation` inside a prior Markdown fence was treated as the real
  heading, and that fence's own closing line was then mis-read as the
  sanctioned computation's opening fence — extracting arbitrary document
  text as if it were sanctioned §10 computation. The scan is now
  fence-aware. Separately, an indented `# Computation` heading (1-3 spaces,
  valid CommonMark ATX heading indentation) is now recognized, matching
  this method's own documented "trimmed text" contract.
- **Path-containment comparisons no longer guess case-sensitivity from the OS.**
  `ReparsePoints.IsWithinBundleRoot`, the 2-arg `ReparsePoints.HasReparsePointAncestor`,
  and `FileMemoryStore`'s reparse-escape check hardcoded `OrdinalIgnoreCase`
  (or picked it via an `IsWindows()||IsMacOS()` heuristic) instead of
  treating case-sensitivity as the runtime property of the volume it
  actually is — the same reasoning behind the earlier `Bundle.PathComparison`
  fix for `Bundle.TryResolveResource`. All three now use
  `StringComparison.Ordinal` unconditionally. Every current caller of these
  three sites already runs its own `Ordinal` containment check first, so no
  caller-reachable escape existed here before this change; what changes is
  that these helpers are now sound standing alone, independent of that
  caller discipline — real hardening at a security seam, at no cost to
  legitimate use, since every candidate path at these sites is built via
  `Path.Combine` from the same root it's compared against, so its prefix
  always keeps that root's exact casing. Separately,
  `MemoryServiceCollectionExtensions`'s memory/knowledge root overlap check
  — a misconfiguration-detection check whose safe direction is inverted
  from the escape-prevention sites above — now uses
  `StringComparison.OrdinalIgnoreCase` unconditionally instead of the same
  OS heuristic. This is the one site among the four with an actual
  observable behavior change: it now catches a case-variant overlap that
  the old heuristic could miss on Linux.

## [0.3.1-preview.1] - 2026-07-30

> Preview release: ships the §10 Attested Computation and per-caller source
> visibility work ahead of a full minor release.

### Added

- **Per-caller source visibility.** `IKnowledgeResolver` searches can now be
  restricted to a subset of enabled `Knowledge`-role sources, based on the
  caller's `KnowledgeAccessScope`. Two mutually-exclusive mechanisms on
  `KnowledgeQuery`: `PermittedSourceIds` (a host-precomputed set of source
  IDs — the recommended default, no host-level default since a static set
  can't represent "differs by tenant") and `SourceVisibilityPolicy` (a
  per-source function, with a `KnowledgeOptions.DefaultSourceVisibilityPolicy`
  host default a function can still vary per call by reading the scope it's
  given). `PermittedSourceIds` always wins over a configured default when
  set. `OkfContextProvider`'s scoped (V2) mode now passes the same
  `KnowledgeAccessScope` it already resolves for memory into the knowledge
  query too.
- **Attested Computation (§10).** Full v0.2 §10 support: `Frontmatter.ComputationContract`
  projects the runtime/parameters/computation/executor/attester contract; `OkfDocument.Computation()`
  returns the sanctioned computation (fenced `# Computation` or `computation:` file); `okf validate`
  emits §10 + §6.2 soft-guidance warnings (never Error). New zero-dep **`OKF4net.Attestation`**
  package: host-plugged `IParameterBinder`/`IComputationExecutor`/`IAttester` and an
  `AttestationOrchestrator` (load → bind → execute → receipt-shape check → attest → gate on
  verdict + `stale_after`), errors-as-data. `OKF4net.Agents` gains `okf_get_computation` and, when
  an orchestrator is wired, `okf_run_computation`.
- **§6.2 path-valued frontmatter resolution** — `OkfDocument.FrontmatterResources()` +
  `Bundle.TryResolveResource`/`ReadResourceText`, with broken/unsafe-path validator warnings.

### Changed

- **`KnowledgeQuery` is no longer V1-scoped.** It gains `Scope`
  (`KnowledgeAccessScope`, defaults to `KnowledgeAccessScope.Local`) — the
  "actual multi-tenant consumer" an earlier doc comment said would justify
  adding identity fields has materialized.
- **Breaking: `KnowledgeResolverRouter`'s constructor gained a new
  parameter, `defaultSourceVisibilityPolicy`, inserted between the
  pre-existing `defaultFairnessQuota` and `clock` parameters.** Any external
  caller invoking the constructor with positional arguments past
  `defaultFairnessQuota` fails to compile until the call site is updated —
  never silently, but source- and binary-breaking for that call shape.
  Callers using named arguments are unaffected.

## [0.3.0] - 2026-07-29

OKF4net now targets **OKF specification v0.2**. The core library and `okf` CLI
implement v0.2's provenance, trust, and lifecycle model, with the two
v0.2-sanctioned legacy fallbacks so v0.1 bundles keep loading unchanged.
`OKF4net.Catalog` gains fully-implemented session/tenant memory tiers and
three selectable knowledge-resolver ranking strategies.

### Added

- **Provenance / trust / lifecycle frontmatter (§5)** — typed, order-preserving
  accessors on `Frontmatter`, each projected lazily and never throwing on
  malformed input (permissive loading, §3):
  - `sources` with per-entry credibility signals (`author`, `usage_count`,
    `last_modified`) and the `usage_window` sibling (`Source`, `UsageWindow`).
  - `generated` / `verified` stamps and the derived trust tier (`Stamp`,
    `TrustTier`: unverified / machine-confirmed / human-reviewed).
  - `status` (draft|stable|deprecated) and `stale_after` (`Lifecycle`,
    `ConceptStatus`), with staleness computed against an injectable `IOkfClock`.
  - The §7 actor convention (`Actor`: `human:`/`process:`/`<producer>/<version>`).
  - `StalePolicy` (Use / Tolerate / Strict) for consumers.
- **`OkfDocument.Sources()`** — v0.2 provenance with the §13.1 legacy fallback:
  the frontmatter `sources` field, or the legacy `# Citations` body list when it
  is absent. `Frontmatter.LastChangedAt` falls back `generated.at ?? timestamp`.
- **v0.2 conformance fixture** (`tests/fixtures/okf_v02`) and its byte-exact golden.
- **Consumer-layer v0.2 wiring** — the provenance/trust/lifecycle model is now
  surfaced through `OKF4net.Agents` and `OKF4net.Catalog`:
  - `okf_write_concept` auto-stamps a `generated` block (§5.2) —
    `{by: okf4net/<version>, at: <UTC>}` — when the frontmatter has none (opt-in
    per tool; the scoped-memory write path is deliberately never auto-stamped).
  - `okf_read_concept` prints a `status | trust | stale` meta line, and
    `okf_search` marks hits `[deprecated]` / `[stale]`, when those differ from
    the defaults.
  - `OkfContextProvider`, `GroupedKnowledgeResolver`, and `KnowledgeQuery` honor
    a `StalePolicy` (default `Use` — surface everything, never silently drop)
    when admitting concepts, with staleness resolved against an injectable clock.
  - `KnowledgePassage` carries the matching concept's `TrustTier` and full
    `Lifecycle`, so a resolver or host can filter and render provenance without
    reparsing frontmatter.
- **Session and tenant memory tiers are now fully implemented and tested.**
  v0.2.0 shipped only the user tier working end-to-end (session/tenant were
  contract/parse-only). The underlying mechanism (`FileMemoryStore`, manifest
  parsing, DI wiring, `OkfContextProviderOptions.CaptureTier`) turned out to
  already be generic across all three tiers, so this release is primarily the
  test coverage, one path-nesting fix (see Fixed), and doc corrections that
  make the existing generic mechanism trustworthy for session/tenant, not new
  surface area.
- **Selectable resolver ranking strategies.** `IKnowledgeResolver` searches
  can now be ranked three ways: `GroupedBySource` (each source's results
  concatenated in priority order — the previous and still-default
  behaviour), `Merged` (one cross-source ranking by descending score, with
  source priority as a tie-break only), and `PriorityWeighted` (source
  priority first, score only within a priority tier). Choose one per host
  via `KnowledgeOptions.DefaultResolverStrategy`, or per call via
  `KnowledgeQuery.ResolverStrategy`. `AddKnowledge` now registers
  `KnowledgeResolverRouter` as the `IKnowledgeResolver`, so existing
  consumers gain per-query selection without any code change, and result
  ordering is unchanged until a host opts in.
- **Fairness interleaving for fused strategies.** An optional
  `FairnessQuota` (host-level `KnowledgeOptions.DefaultFairnessQuota` or
  per-query `KnowledgeQuery.FairnessQuota`) caps how many consecutive
  passages one source may contribute before another source's next-best
  passage is pulled ahead. It reorders only — no passage is ever dropped —
  so it affects consumers that truncate early, such as an agent context
  provider spending a token budget top-down.
- **Same-directory source dedup.** The merged strategies collapse two
  enabled manifest entries that resolve to the same directory, searching
  that bundle once instead of twice. Two *different* directories that
  happen to share a concept id are never merged: a concept id is relative
  to its own bundle root and is not a globally stable identity.
- **`OkfContextProviderOptions.KnowledgeQueryFairnessQuota`** — attaches a
  fairness quota to the knowledge query the context provider issues. The
  provider is the archetypal early-truncating consumer (it renders
  passages top-down until its token budget is spent), so this is what lets
  a budget-bounded agent see several sources instead of one prolific
  source's entire run.

### Changed

- **`OkfSpec.Version` is now `"0.2"`** — `okf validate` and `-V` report OKF v0.2.
  Conformance (§11) still requires only a non-empty `type`, a parseable
  frontmatter block, and well-formed reserved files; every new
  provenance/trust/lifecycle/actor check is a Warning or Info, never an Error.
- **`BundleValidator`** emits the v0.2 soft-guidance diagnostics and takes an
  optional `IOkfClock` for deterministic staleness.
- The producer-side `OkfDocument.Validate()` now requires `type`/`title`/
  `description` (no longer `timestamp`).
- **Breaking:** `DefaultKnowledgeResolver` is renamed `GroupedKnowledgeResolver`
  (behaviour identical). Code that resolves `IKnowledgeResolver` from DI is
  unaffected; only direct references to the concrete type name need
  updating.
- **A non-positive `FairnessQuota` is rejected** with an `ArgumentException`
  by every strategy — including `GroupedBySource`, which ignores the quota
  otherwise — so the same malformed query fails the same way whichever
  strategy runs it. A non-positive resolver constructor default throws
  `ArgumentOutOfRangeException` at construction. `null` remains the way to
  disable fairness reordering.

### Fixed

- **YAML flow-style plain scalars** now keep bare colons inside values, so v0.2
  frontmatter written in flow style (`generated: { by: human:ada, at: … }`,
  URLs, ISO timestamps) parses correctly.
- **`MemoryPath.For`'s session-tier path now nests under tenant and user**
  (`memory-session/<tenant>/<user>/<session>`, matching how the user tier
  already nests under tenant), closing an isolation gap where two different
  tenants sharing the same session id would have collided on
  `memory-session/<session>`. Any deployment that had already enabled the
  session tier (undocumented and untested before this release) will need to
  re-capture session memory under the new path -- existing session-tier
  content at the old path is orphaned, not migrated.

## [0.2.0] - 2026-07-27

This release grows OKF4net from a core library + CLI into an agent- and
tooling-ready stack: three new integration packages, a new MCP tool, and
host-scopeable long-term memory — all built on the same zero-dependency core.

### Added

- **Scoped memory (V2)** for `OkfContextProvider` — host-scoped long-term memory
  that can be enabled on a multi-user deployment without cross-scope leakage:
  - `KnowledgeAccessScope` (tenant / user / session; every segment path-safe by
    construction), supplied per invocation by a host `ScopeAccessor` delegate —
    never derived from a message.
  - `role:"memory"` catalog sources with a `MemoryTier` (`session`/`user`/
    `tenant`), a scoped `IMemoryStore` / `FileMemoryStore` (**user tier
    implemented**; session/tenant are contract/parse-only for now), and readable
    path prefixes via `MemoryPath.For` (`memory-user/<tenant>/<user>/…`), encoded
    case-insensitive-safe so distinct scopes never collide on Windows/macOS.
  - The provider's V2 mode reads knowledge (resolver) ∪ scoped memory under a
    **split token budget** (knowledge + memory floors with spillover) and
    captures each exchange deterministically to one tier; it never throws toward
    the invocation pipeline and injects only as message data.
  - RGPD/audit: `IMemoryStore.DeleteScopeAsync` / `EnumerateAsync`.
  - `AddMemory(this IServiceCollection)` DI facade wiring a store from the
    catalog's `role:memory` sources.
- **`OKF4net.BundleConceptWriter`** — the atomic, reparse-guarded, per-path-locked
  concept-write primitive, promoted to core so `OkfBundleTools` and the memory
  store share one write path.
- **`OKF4net.Agents`** — Microsoft Agent Framework integration (new package):
  - `OkfBundleTools` exposes nine OKF bundle operations as `AIFunction` tools
    (`okf_read_concept`, `okf_browse`, `okf_graph`, `okf_search`,
    `okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes`,
    `okf_validate_bundle`, `okf_changes_since`) for use via
    `chatClient.AsAIAgent(tools: …)`. Writes are producer-grade validated,
    serialized under a per-bundle-path lock, and guarded against directory
    traversal, embedded NUL, and symlink/junction (reparse-point) escapes.
  - `OkfContextProvider` (an `AIContextProvider`) auto-injects budget-bounded,
    progressive-disclosure bundle context into each invocation as reference
    data — never as instructions — and, opt-in, captures each exchange as
    deterministic per-day long-term memory (no LLM call) written back through
    the same validated, locked, reparse-guarded write path. Memory capture is
    off by default: `MemoryCaptureMode.Disabled` unless explicitly set to
    `Enabled`.
- **`OKF4net.Catalog`** and **`OKF4net.Catalog.Hosting`** — a local knowledge
  catalog (two new packages):
  - A hot-reloadable `catalog.json` manifest naming one or more local OKF
    bundles as *sources*, parsed by a strict, never-throw manifest parser
    (structured `CatalogDiagnostic`s, immutable results).
  - `FileKnowledgeCatalog`: fail-fast on an invalid initial manifest,
    errors-as-data on reload, atomic snapshot swap with a monotonic
    `Generation`, and a best-effort debounced file watcher (`ReloadAsync` is
    the source of truth). Source paths are validated to stay within the
    catalog root (OS-appropriate containment, reparse-point rejection).
  - A multi-source resolver that searches every enabled source and returns
    results **grouped by source — no cross-source fusion or dedup** (V1), plus
    an `AddKnowledge(…)` / `AddCatalogFile(…)` `IServiceCollection` facade.
    `OKF4net.Catalog.Hosting` is the only project taking a
    `Microsoft.Extensions.*` dependency; the catalog core stays zero-dependency.
- **`OKF4net.Mcp`** — a local Model Context Protocol server, shipped as the
  `okf-mcp` `dotnet tool`, exposing one OKF bundle to Claude Desktop / Claude
  Code over stdio (`dotnet tool install -g OKF4net.Mcp`). Bundle root via
  argument or `OKF_BUNDLE_ROOT`; read-only mode via `OKF_MCP_READONLY` drops
  the three write tools. stdout is reserved for JSON-RPC; all logs go to stderr.
- `OKF4net.ConceptSearch` — the shared full-text scorer (title ×3,
  tags/description ×2, body ×1) and excerpt helper, promoted into the core
  library so `OKF4net.Agents` (`okf_search` / context provider) and
  `OKF4net.Catalog` rank results identically by construction.

### Changed

- **Breaking:** `MemoryCaptureMode.SharedBundle` is renamed to
  `MemoryCaptureMode.Enabled` (reads correctly in both single-bundle and scoped
  modes).
- `role:"memory"` catalog sources are excluded from `IKnowledgeResolver` search
  (they feed `IMemoryStore`, never shared knowledge).
- `OkfContextProviderOptions.MemoryDirectory` is deprecated in favour of scoped
  `role:memory` catalog sources.
- The package version is now sourced solely from `Directory.Build.props` (one
  source of truth across every project).
- Public read-only surfaces hardened against downcast-and-mutate:
  `YamlMapping.Entries`, `ConceptId.Segments`, `KnowledgeCatalogSnapshot.Sources`,
  and all catalog diagnostic lists are now genuine read-only views.
- The project website was rebuilt as a Vite + React static site with expanded
  developer documentation (getting-started, library, CLI, MCP, and spec pages).

### Fixed

- `IndexGenerator` no longer walks into or lists a symlinked/junctioned
  subdirectory as if it were real: reparse-point detection now uses an
  lstat-correct `FileSystemInfo.LinkTarget` fallback on Unix (where
  `File.GetAttributes` resolves *through* a link): a reparse point is treated
  as neither a file nor a directory, so it is never traversed or listed.
- `Bundle.OkfVersion` is computed eagerly at `Bundle.Load` so it reflects a
  true snapshot of the bundle at load time (previously deferred, which could
  observe later mutation).

## [0.1.1] - 2026-07-24

### Added

- **winget distribution** for the `okf` CLI: `winget install Coderise.OKF4net`
  (portable package, command alias `okf`). Tagged releases now build Native AOT
  binaries for `win-x64` and `win-arm64`, publish a GitHub Release with the
  zipped binaries and `checksums.txt`, and generate the winget v1.6.0 manifests.
  See `packaging/winget/README.md` for the one-time submission to
  `microsoft/winget-pkgs`.
- Project website and developer documentation — getting-started guide, CLI and
  library API reference, and spec-section mapping — deployed to GitHub Pages.

### Changed

- CI/dependencies: bumped `actions/checkout` 4→7 and `actions/setup-dotnet` 4→6,
  and the test-dependencies group.

## [0.1.0] - 2026-07-22

### Added

- Initial C# implementation of OKF v0.1 (see [`NOTICE`](NOTICE) for the full
  derivation and attribution chain):
  - `OKF4net` library — YAML-subset parser/emitter, `OkfDocument`,
    `Frontmatter`, `ConceptId`, `LinkScanner`, `Bundle`, `IndexGenerator`,
    `ChangeLog`, `BundleValidator`.
  - `okf` CLI (`validate`, `info`, `index`, `graph`, `parse`, `fmt`),
    published as a Native AOT single-file binary.
- Test suite (unit, integration, and byte-exact golden CLI comparisons).

### Changed

- Relicensed from Apache-2.0 to LGPL-3.0-or-later; Apache-2.0 attribution for
  upstream ported portions is preserved in `NOTICE` and `LICENSE.Apache-2.0`.

[Unreleased]: https://github.com/jchable/okf4net/compare/v0.4.0...main
[0.4.0]: https://github.com/jchable/okf4net/compare/v0.3.1-preview.1...v0.4.0
[0.3.1-preview.1]: https://github.com/jchable/okf4net/compare/v0.3.0...v0.3.1-preview.1
[0.3.0]: https://github.com/jchable/okf4net/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/jchable/okf4net/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/jchable/okf4net/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/jchable/okf4net/releases/tag/v0.1.0
