# OKF4net Local Catalog -- Execution Plan

> **For agentic workers:** Execute each task in order. Do not start a later
> task until the tests and validation of the prior task pass. This plan adds a
> catalog of local OKF bundles only; it does not add remote connectors.

**Goal:** Deliver a hot-reloadable `catalog.json` of local OKF bundles, a
single-primary-source resolver, and an optional `IServiceCollection` API while
preserving the current format core and Agent Framework integrations.

**Baseline:** The source code, README, and existing test suite are authoritative.
Never modify `tests/fixtures/`. New source files have the LGPL SPDX header,
file-scoped namespaces, public XML documentation, nullable enabled, and no
warnings.

## Task 0 -- Confirm project and dependency boundaries

**Files:** `OKF4net.sln`, new project files, `CLAUDE.md`, test project file.

- [ ] Confirm that `OKF4net.Catalog` references only `OKF4net` and BCL APIs.
- [ ] Decide and document the explicit exception that permits
  `OKF4net.Catalog.Hosting` to reference
  `Microsoft.Extensions.DependencyInjection.Abstractions`. Do not add a DI
  package to `OKF4net`, CLI, or Catalog core.
- [ ] Add both projects to the solution and add catalog test coverage to the
  existing test project, or create a focused catalog test project only if test
  dependencies cannot remain centralized.
- [ ] Run `dotnet build OKF4net.sln` before and after the scaffold.

**Exit:** Project references are acyclic: Hosting -> Catalog -> OKF4net.

## Task 1 -- Define immutable catalog model and strict JSON parser

**Files:** Create model/parser files under `src/OKF4net.Catalog/`; create
`tests/OKF4net.Tests/Catalog/CatalogManifestTests.cs`.

- [ ] Add `KnowledgeCatalogSource`, `KnowledgeCatalogSnapshot`, diagnostics,
  and parser-specific exception/result types.
- [ ] Parse JSON with `System.Text.Json`; reject unknown properties,
  non-v1 versions, empty source arrays, duplicate source IDs, invalid IDs,
  malformed optional values, and embedded NUL characters.
- [ ] Establish defaults: `priority = 0`, `enabled = true`.
- [ ] Keep snapshots immutable and ordinally ordered where ordering is
  observable.
- [ ] Add red tests first for each invalid-manifest case and valid defaults.

**Exit:** Parser tests demonstrate a complete, validated, immutable manifest
model with no filesystem access yet.

## Task 2 -- Add catalog-root path safety

**Files:** Catalog path helper; possible narrow internal core seam; tests under
`tests/OKF4net.Tests/Catalog/`.

- [ ] Resolve each path relative to the manifest directory.
- [ ] Canonicalize the configured catalog root and require every source target
  to stay below it.
- [ ] Reject absolute paths, parent traversal that escapes the root, invalid
  target directories, reparse-point ancestors, and a target directory that is
  itself a reparse point.
- [ ] Reuse the repository's existing reparse-point logic through a narrowly
  scoped internal seam or an approved friend assembly; do not duplicate a
  second platform-specific implementation.
- [ ] Test normal nested paths, traversal, planted junction/symlink ancestors,
  and target nodes. Platform-dependent junction creation may retain the
  existing guarded-test pattern.

**Exit:** A catalog cannot expand its readable filesystem surface beyond its
configured root.

## Task 3 -- Implement file catalog snapshots and hot reload

**Files:** `FileKnowledgeCatalog`, options, diagnostics; catalog tests.

- [ ] Define `IKnowledgeCatalog` with `Current` and explicit `ReloadAsync`.
- [ ] Load, validate, and publish the first snapshot at construction/startup;
  report an invalid initial catalog deterministically rather than silently
  producing an empty catalog.
- [ ] Add `FileSystemWatcher` based reload with debounce, cancellation-safe
  disposal, and an atomically replaced immutable snapshot.
- [ ] On malformed or temporarily incomplete writes, retain the last
  known-good snapshot and expose a reload diagnostic.
- [ ] Test a valid replacement, a malformed replacement preserving the older
  snapshot, repeated events, and disposal.

**Exit:** Operations can atomically replace `catalog.json` without restarting
the application or causing a partial catalog to be served.

## Task 4 -- Implement local OKF source and primary resolver

**Files:** `IKnowledgeSource`, `OkfBundleKnowledgeSource`, query/result types,
`DefaultKnowledgeResolver`; resolver tests.

- [ ] Define `KnowledgeQuery`, `KnowledgePassage`, `KnowledgeContext`, and
  structured diagnostics with public XML documentation.
- [ ] Ensure an `OkfBundleKnowledgeSource` uses the same full-text scoring
  ordering and score calculation as `okf_search`; extract the smallest shared
  core or Agents seam necessary and add parity tests before refactoring.
- [ ] Select exactly one enabled source by descending priority then ordinal ID.
- [ ] Return `NoEnabledSources`, `SourceUnavailable`, and `NoMatches` as data,
  never as expected exceptions.
- [ ] Do not fall through to a lower-priority source if the selected one is
  unavailable.
- [ ] Include source ID, bundle-relative concept ID, score, and excerpt in all
  passages.

**Exit:** A resolver test over two fixture copies proves deterministic primary
selection and scoring parity with `OkfBundleTools.Search`.

## Task 5 -- Add the optional DI hosting facade

**Files:** `src/OKF4net.Catalog.Hosting/`; hosting tests.

- [ ] Add `AddKnowledge(this IServiceCollection, Action<KnowledgeOptions>)`.
- [ ] Provide `AddCatalogFile` and optionally `AddBundle` as development/test
  conveniences that produce the same snapshot model.
- [ ] Register immutable options, catalog, sources/resolver, watcher lifetime,
  and disposal behavior with appropriate service lifetimes.
- [ ] Validate options at startup: a catalog root is required, multiple catalog
  files are rejected in V1, and user input cannot alter catalog paths.
- [ ] Test service registration, resolution, invalid options, and disposal
  through a real `ServiceCollection`.

**Exit:** The documented service registration creates a working resolver with
no dependency leakage into the core format project.

## Task 6 -- Make memory policy explicit

**Files:** `src/OKF4net.Agents/OkfContextProviderOptions.cs`,
`OkfContextProvider.cs`, direct provider tests, README files.

- [ ] Add `MemoryCaptureMode.Disabled` and `MemoryCaptureMode.SharedBundle`.
- [ ] Make `Disabled` the default.
- [ ] Preserve `EnableMemoryCapture` temporarily as an obsolete compatibility
  property and reject configurations that set both properties inconsistently.
- [ ] Retain current `SharedBundle` write behavior and its security notes.
- [ ] Add regression tests for disabled default, explicit shared capture, and
  compatibility mapping.

**Exit:** Memory sharing cannot be enabled accidentally and the API states its
data-sharing implications directly.

## Task 7 -- Documentation, package metadata, and final validation

**Files:** Root README, Agents README, new Catalog README, package metadata,
and design docs only as needed.

- [ ] Document that OKF means Open Knowledge Format; describe `catalog.json`
  as an OKF4net manifest, not an OKF specification file.
- [ ] Show the DI registration and explicit `SharedBundle` opt-in.
- [ ] State V1 limits: local filesystem bundles, shared catalog, one selected
  source, no external connectors, no tenant-aware authorization.
- [ ] Add a V2 preview for application-filtered bundles and host-scoped memory
  without presenting it as implemented.
- [ ] Run `dotnet build OKF4net.sln -c Release`, `dotnet test OKF4net.sln`,
  `dotnet format OKF4net.sln --verify-no-changes`, and golden parity tests.

**Exit:** All validations pass and the NuGet-facing documentation makes no
promise beyond the shipped behavior.

## Deferred V2 -- Team-scoped sources and multiple-source fusion

Do not start this work as part of V1. Before implementation, produce a new
design specification answering all of the following:

- How the host passes authenticated, opaque access scope to a selector.
- Whether selection occurs before or after catalog snapshot construction.
- How host policy filters source IDs without requiring ASP.NET Core in Catalog
  core.
- How `HostScoped` memory keys are generated, retained, deleted, and audited.
- How multi-source scores are normalized and passages deduplicated.
- How source priority, partial failure, citations, token budgets, cancellation,
  and observability behave under parallel search.
