# OKF4net Lot 2 -- Local Catalog V1 (detailed execution plan)

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development.
> Execute tasks in order; each ends with an independently testable deliverable and
> a review gate. This plan implements the **revised** design spec
> ([2026-07-24-okf4net-local-catalog-design.md](../specs/2026-07-24-okf4net-local-catalog-design.md))
> -- read §4-§10 before Task 0. This is **Lot 2**; Lot 1 (memory policy) has
> already landed. Lot 3 (V2 team-scoped memory + multi-source fusion) is out of
> scope and separately specced.

**Goal:** A hot-reloadable `catalog.json` of local OKF bundles, a **multi-source**
resolver (every enabled source searched, results **grouped by source, no
fusion**), and an optional `IServiceCollection` hosting facade -- while keeping
the format core dependency-free and the Agent Framework layer untouched.

**Two decisions locked with the maintainer (do not re-litigate):**
1. **Shared scoring lives in the core** (`OKF4net`). `Catalog` cannot reference
   `Agents` (that would pull `Microsoft.Agents.AI` into a BCL-only project), so
   the concept scorer (`ScoreConceptsFor`) is promoted from `OKF4net.Agents`
   into `OKF4net` as a public BCL API; `Agents` and `Catalog` both call it
   (single source of truth, no duplication).
2. **The manifest carries `role` from V1** (`"role": "knowledge"`, default),
   with only `knowledge` legal in V1 (a `memory` role is a validation error);
   this is forward-compat for the V2 read-only/writable split without a later
   schema bump.

## Global Constraints

- CLAUDE.md governs: SPDX header on new files, file-scoped namespaces, XML docs
  on public API, nullable enabled, `TreatWarningsAsErrors`,
  `dotnet format --verify-no-changes` before every commit.
- Baseline: full `dotnet test` green (397+ on this branch), goldens 5/5,
  `tests/fixtures/` never modified. Keep the whole solution (including the merged
  `OKF4net.Mcp`) green at every task.
- **Dependency law (verify at Task 1 and never break):**
  `OKF4net` = BCL only; `OKF4net.Cli` = BCL only; `OKF4net.Agents` = only
  `Microsoft.Agents.AI`; **`OKF4net.Catalog` = only `OKF4net` + BCL** (NO
  `Microsoft.Extensions.*`, NO `Microsoft.Agents.AI`, NO connector SDK);
  **`OKF4net.Catalog.Hosting` = `OKF4net.Catalog` + `Microsoft.Extensions.DependencyInjection.Abstractions` only.**
  Project graph is acyclic: `Hosting -> Catalog -> OKF4net`.
- **Core changes this lot is allowed to make (only these):** the promoted
  scoring API (Task 0) and one `InternalsVisibleTo("OKF4net.Catalog")` grant so
  `Catalog` can reuse the existing `ReparsePoints` seam (Task 3). Both are pure
  BCL; the core's zero-runtime-dependency rule still holds. No other core edits.
- Errors are **data, never expected exceptions** (mirror the `RunTool`
  philosophy): unreadable catalog, no sources, unavailable source, no matches
  are all returned as diagnostics/results.
- All comparisons `StringComparison.Ordinal` (or `OrdinalIgnoreCase` only where an
  existing seam already does, e.g. path canonicalization, tag match). Generated
  text uses `"\n"`. UTF-8 no BOM.
- Immutable snapshots and results; ordinal-stable ordering where observable.
- Do not touch `.claude/`, the git stash, or `OKF4net.Mcp`.

---

### Task 0 -- Promote the concept scorer into the core

**Files:** Create `src/OKF4net/ConceptSearch.cs`; modify
`src/OKF4net.Agents/OkfBundleTools.cs` (delegate to it); create
`tests/OKF4net.Tests/ConceptSearchTests.cs`.

**Interfaces:**
- Consumes: `Bundle.Concepts` / `Concept` (`ConceptId Id, string Path, OkfDocument Document`), `Frontmatter`.
- Produces (core, public, BCL-only):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>A concept matched by <see cref="ConceptSearch"/>, with its score.</summary>
public sealed record ScoredConcept(Concept Concept, int Score);

/// <summary>
/// Full-text scoring of OKF concepts by query terms. Weights: title x3,
/// tags/description x2, body x1, summed over the query's whitespace-separated
/// terms (case-insensitive substring). This is the single shared scorer used by
/// <c>OkfBundleTools</c> (okf_search / context provider) and <c>OKF4net.Catalog</c>.
/// </summary>
public static class ConceptSearch
{
    /// <summary>
    /// Scores <paramref name="concepts"/> against <paramref name="query"/>,
    /// optionally pre-filtered to those carrying <paramref name="tag"/>
    /// (OrdinalIgnoreCase). Returns matches (Score &gt; 0) ordered by descending
    /// score then ascending <see cref="ConceptId"/>. An empty/whitespace query
    /// yields an empty list.
    /// </summary>
    public static IReadOnlyList<ScoredConcept> Search(
        IEnumerable<Concept> concepts, string query, string? tag = null);
}
```

- [ ] **Step 1:** Move the exact body of `OkfBundleTools.ScoreConceptsFor` +
  the private `ScoreConcept(Concept, terms)` weighting into `ConceptSearch`,
  operating on the passed `IEnumerable<Concept>` (not `GetBundle()`). Keep the
  term split (`query.Split((char[]?)null, RemoveEmptyEntries)`), the tag filter
  (`Frontmatter.Tags` OrdinalIgnoreCase), the `Score > 0` filter, and the
  `OrderByDescending(Score).ThenBy(Id)` ordering **byte-identical**.
- [ ] **Step 2:** Replace `OkfBundleTools.ScoreConceptsFor`'s body with a thin
  delegate: `ConceptSearch.Search(GetBundle().Concepts, query, tag)` (adapt the
  return shape to whatever `Search`/the tool currently expects; the tool
  `okf_search` and `OkfContextProvider` behavior must not change). Grep the
  solution: no other copy of the scoring weights remains.
- [ ] **Step 3:** Tests. `ConceptSearchTests` mirrors the scoring cases (title
  vs tag vs body weighting, multi-term additive/OR, ordering ties by id, empty
  query, tag filter). The **existing** `OkfSearchTests` (14) and provider tests
  MUST stay green unchanged -- they are the parity harness proving no behavior
  drift. Run the full suite + goldens.
- [ ] **Step 4:** `dotnet format`, build `-warnaserror`, full test, goldens.
  Commit `refactor: promote concept scorer to core OKF4net.ConceptSearch`.

**Exit:** One public core scorer; `Agents` delegates to it; `okf_search`
behavior unchanged (parity tests green); nothing else references a private copy.

---

### Task 1 -- Scaffold the two catalog projects + dependency boundaries

**Files:** Create `src/OKF4net.Catalog/OKF4net.Catalog.csproj`,
`src/OKF4net.Catalog.Hosting/OKF4net.Catalog.Hosting.csproj`; modify
`OKF4net.sln`, `tests/OKF4net.Tests/OKF4net.Tests.csproj`,
`src/OKF4net/OKF4net.csproj` (add `InternalsVisibleTo("OKF4net.Catalog")`),
`CLAUDE.md`.

**Interfaces:**
- `OKF4net.Catalog.csproj`: `net10.0`, one `ProjectReference` to `src/OKF4net`,
  **zero PackageReference**. Inherits Directory.Build.props.
- `OKF4net.Catalog.Hosting.csproj`: `net10.0`, `ProjectReference` to
  `OKF4net.Catalog`, one `PackageReference`
  `Microsoft.Extensions.DependencyInjection.Abstractions` (latest stable that
  supports net10.0 -- record the resolved version in the report).
- Tests: `OKF4net.Tests` gets `ProjectReference`s to both new projects
  (test-only DI dependency is allowed). Keep catalog tests centralized in
  `OKF4net.Tests` unless a hard blocker forces a separate project (report why).

- [ ] **Step 1:** `dotnet new classlib` both projects, remove `Class1.cs`, wire
  the sln + references. Add `<InternalsVisibleTo Include="OKF4net.Catalog" />`
  to `src/OKF4net/OKF4net.csproj`'s existing InternalsVisibleTo ItemGroup (so
  Task 3 can reuse `ReparsePoints` without duplicating a platform-specific
  implementation).
- [ ] **Step 2:** A trivial compile-smoke test (a placeholder public type in
  each project + a test that references it) to prove the reference graph builds.
- [ ] **Step 3:** Update `CLAUDE.md`: architecture now lists
  `src/OKF4net.Catalog/` (BCL + OKF4net) and `src/OKF4net.Catalog.Hosting/`
  (the only project allowed `Microsoft.Extensions.DependencyInjection.Abstractions`
  -- document this as the explicit dependency-policy exception; the core stays
  zero-dependency). Note the `Catalog` friend-assembly grant for `ReparsePoints`.
- [ ] **Step 4:** `dotnet format`, build `-warnaserror`, full test, goldens.
  Commit `feat: scaffold OKF4net.Catalog and OKF4net.Catalog.Hosting projects`.

**Exit:** Both projects build; the dependency law holds (verify no
`Microsoft.Extensions.*` in `Catalog`, no `Agents.AI` anywhere new); acyclic graph.

---

### Task 2 -- Immutable manifest model + strict JSON parser (with `role`)

**Files:** Create model/parser files under `src/OKF4net.Catalog/` (e.g.
`KnowledgeCatalogSource.cs`, `KnowledgeCatalogSnapshot.cs`,
`CatalogManifestParser.cs`, `CatalogDiagnostic.cs`); create
`tests/OKF4net.Tests/Catalog/CatalogManifestTests.cs`.

**Interfaces:**

```csharp
namespace OKF4net.Catalog;

public enum SourceRole { Knowledge } // V1: only Knowledge is legal; Memory is V2.

public sealed record KnowledgeCatalogSource(
    string Id, string Path, int Priority, bool Enabled, SourceRole Role);

/// <summary>An immutable, validated catalog manifest snapshot (no filesystem access yet).</summary>
public sealed record KnowledgeCatalogSnapshot(
    int Version, IReadOnlyList<KnowledgeCatalogSource> Sources, string ManifestDirectory);

public enum CatalogDiagnosticCode { /* ParseError, UnknownProperty, DuplicateSourceId, ... */ }
public sealed record CatalogDiagnostic(CatalogDiagnosticCode Code, string Message);

/// <summary>Strict System.Text.Json parser. Returns a snapshot OR diagnostics; never throws for malformed input.</summary>
public static class CatalogManifestParser
{
    public static bool TryParse(string json, string manifestDirectory,
        out KnowledgeCatalogSnapshot? snapshot, out IReadOnlyList<CatalogDiagnostic> diagnostics);
}
```

- [ ] **Step 1 (red tests first, one per rule):** reject -- unknown root/source
  property; `version != 1`; empty/missing `sources`; duplicate source `id`; `id`
  not a valid `ConceptId` segment (reuse `ConceptId.ValidateSegment`); empty
  `path`; embedded NUL anywhere; malformed optional (`priority` non-int,
  `enabled` non-bool); `role` other than `"knowledge"`. Accept -- defaults
  (`priority=0`, `enabled=true`, `role=Knowledge`), and preservation of source
  order. Use `System.Text.Json` with `JsonSerializerOptions` set to reject
  unknown members (`UnmappedMemberHandling.Disallow`).
- [ ] **Step 2:** Implement `TryParse`; snapshots immutable, source order
  preserved (ordinal-stable). No filesystem access in this task.
- [ ] **Step 3:** `dotnet format`, build, full test, goldens. Commit
  `feat: strict catalog.json manifest parser and immutable model`.

**Exit:** A complete, validated, immutable manifest model with no filesystem
access; every invalid case is a diagnostic, not an exception; `role` defaults to
knowledge and rejects other values.

---

### Task 3 -- Catalog-root path safety (reuse `ReparsePoints`)

**Files:** Create `src/OKF4net.Catalog/CatalogPathResolver.cs`; tests under
`tests/OKF4net.Tests/Catalog/CatalogPathSafetyTests.cs`.

**Interfaces:**

```csharp
namespace OKF4net.Catalog;

/// <summary>Resolves and safety-checks a source path against the canonical catalog root.</summary>
public static class CatalogPathResolver
{
    /// <summary>
    /// Resolves <paramref name="sourcePath"/> relative to the manifest directory,
    /// canonicalizes it, and confirms it stays at/below <paramref name="catalogRoot"/>
    /// with no reparse-point ancestor and no reparse-point target. Returns the
    /// resolved absolute directory, or a diagnostic (never throws).
    /// </summary>
    public static bool TryResolve(string catalogRoot, string manifestDirectory,
        string sourcePath, out string? resolvedDirectory, out CatalogDiagnostic? diagnostic);
}
```

- [ ] **Step 1 (red):** normal nested path resolves; reject -- absolute
  `sourcePath`; parent traversal escaping the root (`../../etc`); a target that
  isn't an existing directory; a reparse-point **ancestor** within the root; a
  target directory that **is** a reparse point. Reuse the core `ReparsePoints`
  seam (now friend-visible) -- do NOT write a second platform-specific reparse
  implementation. Canonicalize with `Path.GetFullPath` + `OrdinalIgnoreCase`
  containment, matching the convention `Bundle`/`OkfBundleTools` already use.
- [ ] **Step 2:** Implement. Platform-dependent junction/symlink tests may keep
  the repo's existing guarded-creation pattern (skip gracefully when the OS/user
  can't create one).
- [ ] **Step 3:** `dotnet format`, build, full test, goldens. Commit
  `feat: catalog path safety reusing the ReparsePoints seam`.

**Exit:** A catalog cannot expand its readable surface beyond its configured
root; reparse-point ancestors and targets are rejected; no duplicated
platform-specific reparse code.

---

### Task 4 -- File catalog snapshots + best-effort hot reload

**Files:** `src/OKF4net.Catalog/IKnowledgeCatalog.cs`,
`FileKnowledgeCatalog.cs`, `KnowledgeCatalogOptions.cs`; tests under
`tests/OKF4net.Tests/Catalog/`.

**Interfaces:**

```csharp
namespace OKF4net.Catalog;

public interface IKnowledgeCatalog
{
    KnowledgeCatalogSnapshot Current { get; }
    ValueTask<KnowledgeCatalogSnapshot> ReloadAsync(CancellationToken cancellationToken = default);
}

public sealed class KnowledgeCatalogOptions
{
    public required string CatalogFilePath { get; init; }   // the catalog.json
    public required string CatalogRoot { get; init; }        // canonicalized once at startup
    public TimeSpan ReloadDebounce { get; init; } = TimeSpan.FromMilliseconds(250);
    public bool WatchForChanges { get; init; } = true;
}

public sealed class FileKnowledgeCatalog : IKnowledgeCatalog, IDisposable
{
    public FileKnowledgeCatalog(KnowledgeCatalogOptions options); // loads+validates+publishes first snapshot
    // ... Current, ReloadAsync, Dispose ...
}
```

- [ ] **Step 1 (red):** construction loads, validates (parser + path safety),
  and publishes the first snapshot; an **invalid initial** catalog reports the
  failure deterministically (an aggregate exception at construction OR a clearly
  non-empty error snapshot -- pick and document; do NOT silently serve an empty
  catalog). A valid atomic replacement changes `Current`; a **malformed**
  replacement retains the last known-good `Current` and exposes a reload
  diagnostic. Repeated watcher events debounce to one reload. `Dispose` is
  cancellation-safe and idempotent.
- [ ] **Step 2:** Implement with `FileSystemWatcher` (debounced) swapping an
  **atomically replaced immutable snapshot**; parse+validate the whole new
  snapshot before swap; keep last-known-good on failure. **Document the watcher
  as best-effort** (misses/duplicates by OS/filesystem/container); `ReloadAsync`
  is the reliable, explicit source of truth. Only `catalog.json` is watched, not
  every markdown file (per spec §5.2).
- [ ] **Step 3:** `dotnet format`, build, full test, goldens. Commit
  `feat: file catalog with atomic snapshot swap and best-effort hot reload`.

**Exit:** `catalog.json` can be atomically replaced without a restart or serving
a partial catalog; a bad reload never drops the good snapshot; `ReloadAsync`
always works even if the watcher misses an event.

---

### Task 5 -- Local OKF source + multi-source resolver (grouped, no fusion)

**Files:** `src/OKF4net.Catalog/IKnowledgeSource.cs`,
`OkfBundleKnowledgeSource.cs`, `KnowledgeQuery.cs`, `KnowledgePassage.cs`,
`KnowledgeContext.cs`, `IKnowledgeResolver.cs`, `DefaultKnowledgeResolver.cs`,
`KnowledgeDiagnostic.cs`; tests under `tests/OKF4net.Tests/Catalog/`.

**Interfaces:**

```csharp
namespace OKF4net.Catalog;

public sealed record KnowledgeQuery(string Text, string? Tag = null); // Text required non-blank; no user/tenant/path in V1.

/// <summary>One search hit. Rich enough that a future IKnowledgeResolver -> AIContextProvider
/// adapter can render an &lt;okf-context&gt; block without a contract change (convergence, spec §4.3).</summary>
public sealed record KnowledgePassage(
    string SourceId, string ConceptId, string? Title, string Excerpt, int Score, string BundleRelativePath);

public enum KnowledgeDiagnosticCode { NoEnabledSources, SourceUnavailable, NoMatches }
public sealed record KnowledgeDiagnostic(KnowledgeDiagnosticCode Code, string? SourceId, string Message);

/// <summary>Structured result: passages grouped by source + provenance + diagnostics. Never a bare string.</summary>
public sealed record KnowledgeContext(
    KnowledgeQuery Query, long CatalogGeneration,
    IReadOnlyList<KnowledgePassage> Passages, IReadOnlyList<KnowledgeDiagnostic> Diagnostics);

public interface IKnowledgeSource
{
    string Id { get; }
    ValueTask<KnowledgeSearchResult> SearchAsync(KnowledgeQuery query, CancellationToken ct = default);
}
public sealed record KnowledgeSearchResult(IReadOnlyList<KnowledgePassage> Passages, KnowledgeDiagnostic? Diagnostic);

public interface IKnowledgeResolver
{
    ValueTask<KnowledgeContext> SearchAsync(KnowledgeQuery query, CancellationToken ct = default);
}
public sealed class DefaultKnowledgeResolver : IKnowledgeResolver { /* over IKnowledgeCatalog */ }
```

- [ ] **Step 1:** `OkfBundleKnowledgeSource` loads its bundle
  (`Bundle.Load(resolvedDir)`, permissive) and searches via the core
  **`ConceptSearch.Search(bundle.Concepts, query.Text, query.Tag)`** -- parity
  with `okf_search` is by construction (same core scorer). Each hit ->
  `KnowledgePassage` (SourceId, ConceptId `.ToString()`, `Frontmatter.Title`,
  a body excerpt = first body line containing a term, Score, bundle-relative
  path). Bundle load failure -> `SourceUnavailable` diagnostic, empty passages
  (never throw).
- [ ] **Step 2:** `DefaultKnowledgeResolver.SearchAsync`: take `catalog.Current`
  enabled sources, order by **descending priority then ascending ordinal id**,
  search **each** (construct/reuse an `OkfBundleKnowledgeSource` per enabled
  source), and **concatenate passages in that source order** (grouped by source;
  within a source, `ConceptSearch`'s own descending-score order). NO cross-source
  fusion/dedup/merged ranking. Aggregate per-source `SourceUnavailable`
  diagnostics; a failing source does not drop the others. `NoEnabledSources`
  (none enabled) and `NoMatches` (all sources returned nothing) as diagnostics.
  Stamp `CatalogGeneration` from the snapshot.
- [ ] **Step 3 (tests):** over **two TempDir copies** of `appendix_a` registered
  as two sources with different priorities -- (a) both are searched and passages
  come back grouped in priority order, each tagged with its source id; (b) a
  source pointed at a missing/unreadable dir yields `SourceUnavailable` while the
  other source's passages still return; (c) per-source passage order + scores
  match `ConceptSearch.Search` / `OkfBundleTools.Search` on the same bundle
  (parity); (d) `NoEnabledSources` and `NoMatches` are returned as data.
- [ ] **Step 4:** `dotnet format`, build, full test, goldens. Commit
  `feat: multi-source knowledge resolver grouped by source (no fusion)`.

**Exit:** A resolver test over two fixture copies proves multi-source
grouped-by-priority search, per-source failure isolation, and scoring parity
with the core scorer; `KnowledgeContext` carries enough provenance for the
future agent adapter.

---

### Task 6 -- Optional DI hosting facade

**Files:** `src/OKF4net.Catalog.Hosting/KnowledgeServiceCollectionExtensions.cs`,
`KnowledgeOptions.cs`; tests under `tests/OKF4net.Tests/Catalog/Hosting/`.

**Interfaces:**

```csharp
namespace OKF4net.Catalog.Hosting;

public sealed class KnowledgeOptions
{
    public void AddCatalogFile(string path); // resolves the catalog root from the file's directory
    // Optional dev/test convenience producing the same snapshot model:
    public void AddBundle(string id, string bundleDirectory);
}

public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledge(this IServiceCollection services, Action<KnowledgeOptions> configure);
}
```

- [ ] **Step 1 (tests via a real `ServiceCollection`):** `AddKnowledge` +
  `AddCatalogFile` registers a working `IKnowledgeResolver`/`IKnowledgeCatalog`
  (resolve and search end-to-end over fixture copies); options are validated at
  startup (a catalog root is **required**; **multiple catalog files rejected in
  V1**; user input cannot alter catalog paths); the catalog/watcher is a
  singleton with correct disposal (dispose the provider -> `FileKnowledgeCatalog`
  disposed). `AddBundle` produces the same snapshot model as a one-source file.
- [ ] **Step 2:** Implement with appropriate lifetimes (catalog singleton;
  resolver singleton or scoped -- justify). Register immutable options. No
  dependency leakage: `Catalog` core still has no `Microsoft.Extensions.*`
  reference (only `Hosting` does).
- [ ] **Step 3:** `dotnet format`, build, full test, goldens. Commit
  `feat: AddKnowledge DI hosting facade`.

**Exit:** The documented `services.AddKnowledge(...)` produces a working resolver
with no DI leakage into the format core.

---

### Task 7 -- Docs, package metadata, final validation

**Files:** Root `README.md`, new `src/OKF4net.Catalog/README.md`,
`src/OKF4net.Catalog.Hosting/README.md`, the two new `.csproj` (packaging),
`CLAUDE.md` as needed.

- [ ] **Package metadata** for `OKF4net.Catalog` and `OKF4net.Catalog.Hosting`:
  mirror the `OKF4net.Agents.csproj` packaging block (PackageId, Description,
  LGPL-3.0-or-later, PackageReadmeFile, repo URLs, symbols/snupkg,
  README/NOTICE/LICENSE.Apache-2.0 includes); **Version comes from
  Directory.Build.props** (do not hardcode). `dotnet pack` both; verify the
  Hosting nuspec depends on `OKF4net.Catalog` [same version] +
  `Microsoft.Extensions.DependencyInjection.Abstractions`, and `Catalog` depends
  only on `OKF4net`. Each package gets its own dedicated README (not the root
  one -- the root README's "zero-dependency" framing is wrong for these).
- [ ] **Docs:** README section: OKF = **Open Knowledge Format**; `catalog.json`
  is an **OKF4net manifest, not an OKF concept**; the `services.AddKnowledge`
  example; V1 limits stated exactly (local filesystem bundles, shared catalog,
  **all enabled sources searched but grouped by source -- no fusion/dedup/merged
  ranking**, no external connectors, no tenant-aware authorization); a V2 preview
  (application-filtered bundles, the read-only/writable `role` split, host-scoped
  memory tiers -- point to
  [the V2 notes](../specs/2026-07-24-okf4net-v2-scoped-memory-notes.md)) **without
  presenting any of it as implemented**.
- [ ] **Final gates:** `dotnet build OKF4net.sln -c Release -warnaserror`,
  `dotnet test OKF4net.sln`, `dotnet format OKF4net.sln --verify-no-changes`,
  golden parity tests -- all green. Commit
  `docs: catalog integration guide and package metadata`.

**Exit:** All validations pass; the NuGet-facing docs promise nothing beyond the
shipped behavior; two new packages pack with correct dependency graphs.

---

## Deferred (Lot 3 / V2) -- do not start here

Team-scoped sources, multi-source **fusion** (score normalization, dedup, merged
ranking, per-source token budgets), and host-scoped/layered memory
(session/user/tenant) require a **new** spec answering the questions in the
design spec's "V2" section and the layered model in
[2026-07-24-okf4net-v2-scoped-memory-notes.md](../specs/2026-07-24-okf4net-v2-scoped-memory-notes.md).
The `IKnowledgeResolver`/`KnowledgeContext`/`role` contracts here are shaped so
V2 can add a source selector and memory tiers without breaking V1 callers.
