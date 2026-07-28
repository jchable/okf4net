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
- `role` (optional, default `"knowledge"`) — `"knowledge"` (read-only, searched
  by the resolver) or `"memory"` (writable, scoped by tier — see below); any
  other string is rejected.
- `tier` — required when `role` is `"memory"`, one of `"session"`, `"user"`,
  or `"tenant"`; not allowed otherwise.

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

## Scoped memory (`role: "memory"`)

A `role: "memory"` source is written by capture (e.g.
`OkfContextProviderOptions.CaptureTier` in `OKF4net.Agents`), not searched by
`IKnowledgeResolver` — it feeds an `IMemoryStore` instead. Configure one
source per tier you need:

```json
{
  "version": 1,
  "sources": [
    { "id": "kb", "path": "./bundles/products", "role": "knowledge" },
    { "id": "mem-user", "path": "./memory/user", "role": "memory", "tier": "user" },
    { "id": "mem-tenant", "path": "./memory/tenant", "role": "memory", "tier": "tenant" },
    { "id": "mem-session", "path": "./memory/session", "role": "memory", "tier": "session" }
  ]
}
```

```csharp
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

services.AddKnowledge(o => o.AddCatalogFile("./config/catalog.json"));
services.AddMemory();

// Elsewhere:
IMemoryStore memory = provider.GetRequiredService<IMemoryStore>();
await memory.DeleteScopeAsync(scope, MemoryTier.Session); // e.g. when a conversation ends
```

**There is no code-level distinction between ephemeral and persistent
tiers** — every `role:"memory"` source's `path`, like every other source's,
must be relative to the manifest directory and resolve inside the catalog
root (`CatalogPathResolver.TryResolve` rejects absolute paths, paths that
escape the root, and reparse points anywhere along the way), so a source
cannot point directly at an OS temp directory or a symlink into one.
"Ephemeral" therefore isn't a per-source path trick; it's one of two real
choices:

- Run the **whole catalog root** on ephemeral storage (e.g. a container's
  tmpfs mount or ephemeral volume) — every source under it, including a
  session-tier one at a perfectly ordinary relative `path` like
  `mem-session` above, is then ephemeral by construction. The catalog root
  itself is exempt from the reparse-point walk, so this is the one place a
  mount point is fine.
- Treat any tier's subtree as revocable at will via
  `IMemoryStore.DeleteScopeAsync` — nothing purges automatically, but
  nothing stops a host from calling it the moment a conversation ends.

**V1 limitation:** `OKF4net.Catalog.Hosting`'s `AddMemory()` resolves the set
of `role:memory` sources once, at first `IMemoryStore` resolution from the
container, and does not pick up a source added/removed/edited afterward
(including via `IKnowledgeCatalog.ReloadAsync()`) — see `AddMemory`'s own XML
doc for the full explanation. Per-scope path resolution (the tenant/user/session
segments) stays fully live on every call; only the fixed set of configured
tiers is frozen.

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
