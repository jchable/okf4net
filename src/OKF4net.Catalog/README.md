# OKF4net.Catalog

A catalog of local [Open Knowledge Format (OKF) v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
knowledge bundles for .NET: a hot-reloadable `catalog.json` manifest naming
one or more bundles as *sources*, and a resolver that searches every enabled
source and returns results grouped by source.

**OKF** is the Open Knowledge Format — a directory tree of markdown files with
YAML frontmatter (see [OKF4net](https://www.nuget.org/packages/OKF4net) for
the format core). `catalog.json` is **not** an OKF concept and is not part of
the OKF spec: it is an **OKF4net manifest**, a small piece of catalog
configuration that points at OKF bundles from the outside. Nothing about the
manifest format is portable to another OKF implementation.

This package references only `OKF4net` (the format core) — no third-party
runtime dependencies.

## What it does

- **Loads and validates `catalog.json`.** `FileKnowledgeCatalog` parses the
  manifest, validates every enabled source's `path` against the catalog root
  (rejecting absolute paths, paths that escape the root, missing directories,
  and reparse points in between), and publishes the result as an immutable,
  versioned `KnowledgeCatalogSnapshot`. An invalid *initial* manifest throws
  `CatalogException` (fail-fast at startup).
- **Hot-reloads, best-effort.** A debounced `FileSystemWatcher` on the
  manifest file triggers automatic reloads. This is best-effort only — OS/
  filesystem/container layers can miss or duplicate watcher events. Call
  `IKnowledgeCatalog.ReloadAsync()` directly whenever you need a reliable,
  synchronous guarantee that an edit has been picked up. A reload is
  errors-as-data: a malformed or invalid replacement manifest leaves the
  current snapshot untouched and records the reject reasons in
  `LastReloadDiagnostics` instead of throwing.
- **Searches every enabled source, grouped by source.** `IKnowledgeResolver`
  fans a query out across every enabled `KnowledgeCatalogSource` (using the
  same `ConceptSearch` scorer the `OKF4net.Agents` tools use) and
  concatenates the results in source-priority order. **There is no
  cross-source fusion, deduplication, or merged ranking** — passages are
  grouped by their originating source, not blended into one ranked list. See
  [the design notes](https://github.com/jchable/okf4net/blob/main/docs/design/specs/2026-07-24-okf4net-v2-scoped-memory-notes.md)
  for where that's headed in a future version.

## Minimal `catalog.json`

```json
{
  "version": 1,
  "sources": [
    { "id": "products", "path": "./bundles/products", "priority": 10, "enabled": true },
    { "id": "support", "path": "./bundles/support", "priority": 0, "enabled": true }
  ]
}
```

- `id` — unique within the manifest, a single OKF concept-id segment.
- `path` — relative to the manifest's own directory; must resolve inside the
  catalog root.
- `priority` (optional, default `0`) — higher priority sources are searched
  first and their passages appear first in a grouped result.
- `enabled` (optional, default `true`).
- `role` (optional, default `"knowledge"`) — the only legal value in V1; any
  other string is rejected.

## Quick start

```csharp
using OKF4net.Catalog;

var options = new KnowledgeCatalogOptions
{
    CatalogFilePath = "./config/catalog.json",
    CatalogRoot = "./config",
};

using var catalog = new FileKnowledgeCatalog(options);
IKnowledgeResolver resolver = new DefaultKnowledgeResolver(catalog);

KnowledgeContext result = await resolver.SearchAsync(new KnowledgeQuery("refund policy"));

foreach (var passage in result.Passages)
{
    Console.WriteLine($"[{passage.SourceId}] {passage.ConceptId} ({passage.Score}): {passage.Excerpt}");
}

foreach (var diagnostic in result.Diagnostics)
{
    Console.WriteLine($"[{diagnostic.Code}] {diagnostic.Message}");
}
```

`KnowledgeContext` is deliberately never a bare string: `Passages` (grouped by
source, in source-priority then per-source descending-score order) and
`Diagnostics` (e.g. `NoEnabledSources`, `SourceUnavailable`, `NoMatches`) let a
caller distinguish "no results" from "a source failed" from "no source is
enabled" without parsing text.

## V1 limits

- Local filesystem bundles only — no remote/HTTP sources, no external
  connectors.
- One shared catalog per `FileKnowledgeCatalog` instance — no per-caller or
  per-tenant filtering of which sources are visible.
- All enabled sources are searched and results are grouped by source; there is
  no fusion, deduplication, or merged cross-source ranking.
- No tenant-aware authorization of any kind.

See [the project README](https://github.com/jchable/okf4net) for the full
documentation, and NOTICE/LICENSE.Apache-2.0 for the attribution chain of the
underlying OKF implementation.

Licensed LGPL-3.0-or-later.
