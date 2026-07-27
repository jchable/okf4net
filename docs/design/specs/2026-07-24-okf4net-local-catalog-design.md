# OKF4net Local Catalog -- Design Specification

**Date:** 2026-07-24  
**Status:** Approved for implementation planning  
**Scope:** V1 local, shared OKF catalog; V2 team-scoped catalog design only  
**Revised 2026-07-24:** V1 resolver searches all enabled sources grouped by
source (no cross-source fusion); `EnableMemoryCapture` is replaced outright by
`MemoryCaptureMode` (pre-release, no compatibility shim); the file watcher is
documented best-effort with `ReloadAsync` as the reliable path; the catalog is
stated as the intended substrate beneath `OkfContextProvider`; and the
memory-policy change ships as an independent lot ahead of the catalog. These
revisions are a reviewable diff over baseline commit `7771fa7`.

## 1. Goal

Make OKF4net the natural .NET way to consume multiple local Open Knowledge
Format (OKF) bundles, without changing the meaning of OKF or turning the
runtime library into a federation layer for SharePoint, SQL Server, or Azure
AI Search.

The V1 developer experience is configuration-first and follows the
Microsoft.Extensions idiom:

```csharp
services.AddKnowledge(options =>
{
    options.AddCatalogFile("./config/catalog.json");
});
```

The application resolves a query through one selected local bundle:

```csharp
KnowledgeContext context = await knowledge.SearchAsync(question, cancellationToken);
```

The existing `Bundle`, `OkfBundleTools`, and `OkfContextProvider` APIs remain
supported. The catalog is an additive higher-level API, not a replacement for
the single-bundle API.

## 2. Terminology

- **OKF / Open Knowledge Format:** The open format represented by a directory
  of Markdown documents with YAML frontmatter. It is never called "Open
  Knowledge Framework".
- **Bundle:** One local filesystem directory conforming to OKF conventions.
- **Catalog:** An OKF4net-specific manifest that registers several local
  bundles. It is not an extension of the upstream OKF specification and must
  not be presented as an OKF document.
- **Source:** One named catalog entry backed by exactly one local OKF bundle.
- **Resolver:** The component that loads a catalog snapshot, searches the
  enabled sources, and returns a structured, source-grouped result.
- **Knowledge context:** Search passages plus source provenance and diagnostics
  suitable for an application or an agent integration.

## 3. Non-goals

V1 does not implement any of the following:

- Runtime connectors for SharePoint, SQL Server, Azure AI Search, GitHub, or
  another remote system.
- Network I/O, credentials, embedding generation, vector search, chunking, or
  data synchronization.
- Score fusion, passage deduplication, a single cross-bundle ranking, or
  per-source token-budget allocation. (V1 *does* search several sources, but
  returns their matches grouped by source rather than merged -- see §7.)
- Authorization decisions based on ASP.NET Core claims, users, or tenants.
- A new `agent.AskAsync` abstraction. Agent invocation remains owned by
  Microsoft Agent Framework; a later adapter can consume `KnowledgeContext`.

External data may later be imported by a separate tool into local OKF bundles.
At runtime, this catalog continues to consume local OKF files only.

## 4. Package boundaries

### 4.1 `OKF4net.Catalog`

This new project is the BCL-only catalog and resolver core. It references
`OKF4net` and contains:

- Catalog JSON parsing and validation.
- Path validation and immutable catalog snapshots.
- `IKnowledgeCatalog`, `IKnowledgeSource`, and `IKnowledgeResolver`.
- `OkfBundleKnowledgeSource`, implemented using the existing core bundle API.
- `DefaultKnowledgeResolver`.
- Result, diagnostics, and options types.

It must not reference `Microsoft.Extensions.*`, `Microsoft.Agents.AI`, or any
remote connector SDK.

### 4.2 `OKF4net.Catalog.Hosting`

This optional integration project provides the ASP.NET Core-style registration
surface:

```csharp
services.AddKnowledge(options => options.AddCatalogFile("./config/catalog.json"));
```

It may reference only `Microsoft.Extensions.DependencyInjection.Abstractions`
in addition to `OKF4net.Catalog`. Adding that integration dependency requires
an explicit update to the repository dependency policy before implementation.
The core format library remains zero-dependency.

### 4.3 Existing projects

- `OKF4net`: unchanged public format and bundle behavior.
- `OKF4net.Agents`: remains the Microsoft Agent Framework adapter. The catalog
  is intended to become the **substrate** beneath the agent layer, not a
  permanent parallel API: once the catalog contracts prove stable, an
  `IKnowledgeResolver` -> `AIContextProvider` adapter will let
  `OkfContextProvider` inject from the resolver (one or many bundles) instead
  of owning its own single-bundle path. The shared scoring seam
  (`ScoreConceptsFor`) is the first convergence point, and `KnowledgeContext`
  provenance is intentionally rich enough (source ID, concept ID, excerpt,
  score) for that adapter to render `<okf-context>` blocks without a contract
  change.
- `OKF4net.Cli`: unchanged in V1.

## 5. V1 catalog manifest

`catalog.json` is a deployment artifact managed by operations. It is a JSON
manifest, not an OKF concept.

```json
{
  "version": 1,
  "sources": [
    {
      "id": "product",
      "path": "../bundles/product",
      "priority": 100,
      "enabled": true
    },
    {
      "id": "support",
      "path": "../bundles/support",
      "priority": 50
    }
  ]
}
```

### 5.1 Schema rules

- Root object has exactly `version` and `sources`; unknown properties are a
  validation error in V1.
- `version` must be the integer `1`.
- `sources` must be a non-empty array.
- Each source has `id`, `path`, optional `priority` (default `0`), optional
  `enabled` (default `true`), and optional `role` (default `"knowledge"`);
  unknown source properties are errors. In V1 only `"knowledge"` is legal --
  a `"memory"` role is a validation error, reserved for the V2 read-only/writable
  split (see the V2 scoped-memory notes). Carrying `role` from V1 is deliberate
  forward-compat so V2 needs no manifest-schema bump.
- `id` is a unique, ordinal, valid `ConceptId` segment.
- `path` is a non-empty relative filesystem path, resolved relative to the
  directory containing `catalog.json`.
- Absolute paths, embedded NUL characters, paths that resolve outside the
  configured catalog root, and paths traversing a reparse point are rejected.
- An enabled source path must be an accessible directory. It need not already
  contain a fully conformant bundle: normal bundle parse errors stay available
  through source diagnostics, matching `Bundle.Load` permissiveness.

### 5.2 Operations and hot reload

- The catalog root is configured by the application and is canonicalized once
  at startup.
- `catalog.json` is replaced atomically by operations: write and validate a
  sibling temporary file, then replace the manifest.
- A file watcher requests a debounced reload. The implementation parses and
  validates a complete new snapshot before atomically replacing the active
  snapshot. The watcher is **best-effort**: `FileSystemWatcher` misses or
  duplicates events depending on the OS, filesystem, network shares, and
  containers. `ReloadAsync` is the reliable, explicit source of truth; the
  watcher is an optimization, and an application that needs guaranteed
  freshness calls `ReloadAsync`.
- A failed reload never removes the last known-good snapshot. It records a
  diagnostic observable through the catalog API and logging integration.
- Changes to an individual bundle are visible through the existing bundle
  reload/cache semantics; V1 does not watch every Markdown file itself.

## 6. Public contracts

The exact namespace is `OKF4net.Catalog`. The initial contracts are intentionally
small and async so that V2 may add a source selector without breaking callers.

```csharp
public interface IKnowledgeCatalog
{
    KnowledgeCatalogSnapshot Current { get; }
    ValueTask<KnowledgeCatalogSnapshot> ReloadAsync(CancellationToken cancellationToken = default);
}

public interface IKnowledgeSource
{
    string Id { get; }
    ValueTask<KnowledgeSearchResult> SearchAsync(
        KnowledgeQuery query,
        CancellationToken cancellationToken = default);
}

public interface IKnowledgeResolver
{
    ValueTask<KnowledgeContext> SearchAsync(
        KnowledgeQuery query,
        CancellationToken cancellationToken = default);
}
```

`KnowledgeQuery` initially has a required non-blank query text. It carries no
user, tenant, claims, or arbitrary path in V1.

`KnowledgeContext` contains:

- The original query.
- The catalog generation, and per-passage source provenance.
- Passages grouped by source (source order = descending `priority`, then
  ascending ordinal `id`; within a source, descending score), each with its
  source ID, concept ID, excerpt, score, and local bundle-relative provenance.
- Diagnostics such as `NoEnabledSources`, `SourceUnavailable` (per failing
  source), or `NoMatches`.

The type is structured data. Formatting for a model prompt stays in an agent
adapter, rather than making search return an untraceable string.

## 7. Resolver behavior

V1 searches every enabled source and returns their matches **grouped by
source**. It does not fuse or cross-rank: raw scores from different bundles are
not comparable, so a single merged ranking would be misleading.

1. Take the enabled catalog sources.
2. Order them by descending `priority`, then ascending ordinal `id`.
3. Search each one using the existing `OkfBundleTools.ScoreConceptsFor` scoring
   semantics, extracted into a reusable catalog-safe seam if necessary. Within
   a source, passages are ordered by that source's own descending score.
4. Concatenate the per-source results in the source order from step 2, each
   passage tagged with its originating source ID. The result is grouped by
   source, never merged into one cross-source ranking.

A source that cannot be searched (missing directory, load failure) yields a
`SourceUnavailable` diagnostic for that source; the other sources' results are
still returned. `NoEnabledSources` and `NoMatches` are returned as data, never
as expected exceptions.

Cross-source score normalization, passage deduplication, a single merged
ranking, and per-source token-budget allocation are deliberately deferred to V2
(§9). V1 delivers real multi-bundle search with honest per-source provenance,
without taking on the hard fusion problem.

## 8. Security and memory policy

The V1 catalog is shared. It has no user or tenant identity, so it cannot make
per-user authorization decisions. The application owns access to the catalog
file and the catalog root through deployment configuration and filesystem ACLs.

The `OkfContextProvider` memory policy is made explicit:

```csharp
public enum MemoryCaptureMode
{
    Disabled,
    SharedBundle,
}
```

- `Disabled` is the default and writes no conversational data.
- `SharedBundle` is explicit opt-in and writes the current deterministic daily
  memory representation. Any session that can read that bundle may later
  retrieve the captured exchange.

`EnableMemoryCapture` is replaced outright by `MemoryCaptureMode`. The library
is pre-release and unpublished, so no compatibility shim is warranted: the
boolean is removed, not deprecated. `Disabled` is the default, and enabling
capture on a shared bundle is the explicit, security-noted `SharedBundle`
choice. Per-tenant/user/session isolation of memory is a V2 concern; its design
is captured in
[2026-07-24-okf4net-v2-scoped-memory-notes.md](2026-07-24-okf4net-v2-scoped-memory-notes.md)
and refines §9 below. This memory-policy change has no dependency on the
catalog and ships as an independent lot ahead of it.

Bundle content continues to be untrusted and is injected into agent messages,
never agent instructions.

## 9. V2 design: team-scoped bundles

V2 adds multiple visible sources and authorization without putting ASP.NET Core
claims into the catalog core.

The application authenticates the request and supplies only an opaque scope:

```csharp
public sealed record KnowledgeAccessScope(string TenantId, string SubjectId, string Purpose);
```

The hosting layer, not `OKF4net.Catalog`, maps that scope to permitted source
IDs. The resolver receives an already filtered snapshot or a source-selector
policy supplied by the host. This keeps the library usable in web apps,
workers, CLIs, and desktop applications.

V2 can add `HostScoped` memory only after that host contract exists. Its scope
must be derived from authenticated application data, never from an agent
message. It writes under an opaque, validated scope key and reads only the
same scope.

V2 may query several permitted bundles in parallel and merge results. It must
define score normalization, source priority, passage deduplication, token
budget allocation, citations, cancellation, partial failures, and audit
diagnostics before implementation.

## 10. Acceptance criteria

- A valid manifest with two bundles searches both enabled sources and returns
  their matches grouped by source in priority order (highest-priority source
  first), each passage carrying its source provenance.
- Invalid schema, duplicate IDs, outside-root paths, missing directories, and
  reparse-point traversal are rejected without replacing the last good
  snapshot.
- A successful atomic manifest replacement changes the active snapshot; a
  malformed replacement retains the prior snapshot and records a diagnostic.
- Results preserve source and concept provenance and share the existing full
  text scoring semantics.
- The catalog core remains BCL-only; hosting is optional.
- The full solution builds with warnings as errors, all tests pass, format is
  clean, and existing golden fixtures are untouched.
