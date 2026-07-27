# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Scoped memory (V2)** for `OkfContextProvider` — host-scoped long-term memory
  that can be enabled on a multi-user deployment without cross-scope leakage:
  - `KnowledgeAccessScope` (tenant / user / session; every segment path-safe by
    construction), supplied per invocation by a host `ScopeAccessor` delegate —
    never derived from a message.
  - `role:"memory"` catalog sources with a `MemoryTier` (`session`/`user`/
    `tenant`), a scoped `IMemoryStore` / `FileMemoryStore` (**user tier
    implemented**; session/tenant are contract/parse-only for now), and readable
    path prefixes via `MemoryPath.For` (`memory-user/<tenant>/<user>/…`).
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

### Changed

- **Breaking:** `MemoryCaptureMode.SharedBundle` is renamed to
  `MemoryCaptureMode.Enabled` (reads correctly in both single-bundle and scoped
  modes).
- `role:"memory"` catalog sources are excluded from `IKnowledgeResolver` search
  (they feed `IMemoryStore`, never shared knowledge).
- `OkfContextProviderOptions.MemoryDirectory` is deprecated in favour of scoped
  `role:memory` catalog sources.

## [0.2.0] - 2026-07-26

This release grows OKF4net from a core library + CLI into an agent- and
tooling-ready stack: three new integration packages and a new MCP tool, all
built on the same zero-dependency core.

### Added

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
    `SharedBundle`.
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

- The package version is now sourced solely from `Directory.Build.props` (one
  source of truth across every project).
- Public read-only surfaces hardened against downcast-and-mutate:
  `YamlMapping.Entries`, `ConceptId.Segments`, `KnowledgeCatalogSnapshot.Sources`,
  and all catalog diagnostic lists are now genuine read-only views.
- The project website was rebuilt as a Vite + React static site with expanded
  developer documentation (getting-started, library, CLI, MCP, and spec pages).

### Fixed

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

- Initial C# implementation of OKF v0.1, ported from this repository's former
  Rust `okf` implementation (byte-exact parity proven before removal):
  - `OKF4net` library — YAML-subset parser/emitter, `OkfDocument`,
    `Frontmatter`, `ConceptId`, `LinkScanner`, `Bundle`, `IndexGenerator`,
    `ChangeLog`, `BundleValidator`.
  - `okf` CLI (`validate`, `info`, `index`, `graph`, `parse`, `fmt`),
    published as a Native AOT single-file binary.
- Test suite (unit, integration, and byte-exact golden CLI comparisons).

### Changed

- Relicensed from Apache-2.0 to LGPL-3.0-or-later; Apache-2.0 attribution for
  upstream ported portions is preserved in `NOTICE` and `LICENSE.Apache-2.0`.

[Unreleased]: https://github.com/jchable/okf4net/compare/v0.2.0...main
[0.2.0]: https://github.com/jchable/okf4net/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/jchable/okf4net/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/jchable/okf4net/releases/tag/v0.1.0
