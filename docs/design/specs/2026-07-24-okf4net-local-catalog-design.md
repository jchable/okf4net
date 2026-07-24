# OKF4net Local Catalog -- Design Specification

**Date:** 2026-07-24  
**Status:** Approved for implementation planning  
**Scope:** V1 local, shared OKF catalog; V2 team-scoped catalog design only

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
- **Resolver:** The component that loads a catalog snapshot, chooses one
  source, searches it, and returns a structured result.
- **Knowledge context:** Search passages plus source provenance and diagnostics
  suitable for an application or an agent integration.

## 3. Non-goals

V1 does not implement any of the following:

- Runtime connectors for SharePoint, SQL Server, Azure AI Search, GitHub, or
  another remote system.
- Network I/O, credentials, embedding generation, vector search, chunking, or
  data synchronization.
- Multi-source query execution, score fusion, deduplication, or cross-bundle
  ranking.
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
- `OKF4net.Agents`: remains the Microsoft Agent Framework adapter. A future
  package can adapt `IKnowledgeResolver` into an `AIContextProvider` after the
  catalog contracts have proven stable.
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
- Each source has `id`, `path`, optional `priority` (default `0`), and optional
  `enabled` (default `true`); unknown source properties are errors.
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
  snapshot.
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
- The selected source ID and catalog generation.
- Ordered passages with concept ID, excerpt, score, and local bundle-relative
  provenance.
- Diagnostics such as `NoEnabledSources`, `SourceUnavailable`, or `NoMatches`.

The type is structured data. Formatting for a model prompt stays in an agent
adapter, rather than making search return an untraceable string.

## 7. Resolver behavior

V1 has exactly one source-selection algorithm:

1. Take enabled catalog sources.
2. Sort by descending `priority`, then ascending ordinal `id`.
3. Select the first source only.
4. Search it using the existing `OkfBundleTools.ScoreConceptsFor` scoring
   semantics, extracted into a reusable catalog-safe seam if necessary.
5. Return the ordered matching concepts and provenance as `KnowledgeContext`.

It never silently searches a lower-priority source after a primary-source
failure. The returned diagnostic makes the failure observable and preserves
the meaning of source priority. Multi-source fallback and fusion are V2 work.

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

The existing `EnableMemoryCapture` property remains temporarily for source
compatibility, is marked obsolete, and maps `true` to `SharedBundle` and
`false` to `Disabled`. Supplying both options is a configuration error.

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

- A valid manifest with two bundles selects the highest-priority enabled
  source deterministically.
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
