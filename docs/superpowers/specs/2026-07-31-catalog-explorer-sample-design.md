# `catalog-explorer` sample: OKF4net.Catalog end-to-end

Date: 2026-07-31

## Motivation

No sample in this repo exercises `OKF4net.Catalog` at all — the existing
`samples/acme-retail-agent` uses the core library and `OKF4net.Agents`
against a single bundle, but nothing shows the catalog's actual reason to
exist: searching across *multiple* knowledge sources, comparing ranking
strategies, scoping visibility per caller, or the `role: memory` tier. This
spec adds a standalone console sample, `samples/catalog-explorer`, that
walks through those in sequence, using `OKF4net.Catalog` directly (no DI) —
`OKF4net.Catalog.Hosting`'s `IServiceCollection` wiring is a separate,
narrower concern, deliberately out of scope here (see "Out of scope").

### Why two bundles, and why these two

An early version of this design paired `acme_retail` with the upstream
`crypto_bitcoin` bundle purely because both exist upstream. That pairing was
rejected: a retail company's internal docs and a public Bitcoin blockchain
dataset share no vocabulary, so no query would plausibly return results from
both, and "two unrelated domains in one catalog" isn't a scenario anyone
actually has. A multi-source catalog needs sources that a real caller would
plausibly search *together*.

This spec instead pairs `bundles/acme_retail` (unchanged) with a new
`bundles/ga4` — the upstream `okf/bundles/ga4` bundle, Google's public
reference docs for Google Analytics 4 ecommerce metrics (`purchasers`,
`revenue`-adjacent audience metrics, event tables). The framing: **an
internal analyst copilot at Acme Retail**, which searches both Acme's own
proprietary knowledge (`metrics/revenue.md`, `metrics/gross-margin.md`,
`tables/orders.md`, ...) *and* the public GA4 reference material its
analysts consult to compute standard ecommerce metrics. This gives every
scenario below a real reason to exist:

- a query like `"revenue"` or `"purchase"` genuinely returns relevant
  passages from **both** sources, so grouping/ranking differences are
  visible, not simulated;
- the visibility scenario becomes **proprietary vs. public reference**
  (an external-partner-scoped caller sees only the public `ga4-reference`
  source; an Acme-employee-scoped caller sees both) — a distinct, equally
  realistic pattern from the tenant-prefix example already in
  `OKF4net.Catalog`'s own README, not a redundant restatement of it.

## Part A — `bundles/ga4`

Copy verbatim (byte-for-byte) from `okf/bundles/ga4` at
`GoogleCloudPlatform/knowledge-catalog` commit
`3fcbb9f828c2f23d109c855ee403c3a4c81f3a96` — the same commit `acme_retail`
was pinned to — Apache-2.0:

```text
bundles/ga4/
  index.md
  datasets/
    index.md
    ga4_obfuscated_sample_ecommerce.md
  references/
    index.md
    metrics/
      index.md
      acquired_users.md
      frequently_active_users.md
      google_acquired_cohorts.md
      highly_active_users.md
      n_day_active_users.md
      n_day_inactive_users.md
      purchasers.md
  tables/
    index.md
    events_.md
  README.md              # new — provenance & license, mirrors acme_retail/README.md
```

`viz.html` is **not** carried over, same rationale as `acme_retail`: a
generated artifact of the upstream Python visualizer, not OKF bundle
content.

### `bundles/ga4/README.md`

Mirrors `bundles/acme_retail/README.md`'s shape: what the bundle is
(Google's public GA4 ecommerce reference docs — `Reference` concepts only,
no Attested Computations/Metrics/Policies, a deliberately different concept
mix from `acme_retail`), provenance (source repo, path, commit SHA,
Apache-2.0), the `viz.html` omission, and a "Validating" section with the
expected `okf validate` result.

### Other doc updates

- One `NOTICE` entry for `bundles/ga4/`, alongside the existing
  `acme_retail` entry, same format.
- `bundles/README.md` gets a `## GA4` section (mirroring its existing
  `## Acme Retail` section) pointing at `samples/catalog-explorer/`.

## Part B — `samples/catalog-explorer`

Kebab-case directory; standard .NET naming inside (project
`CatalogExplorer`, namespace `OKF4net.Samples.CatalogExplorer`). Own
`CatalogExplorer.sln` — **not** added to `OKF4net.sln`, **not** wired into
`ci.yml`, same convention as `acme-retail-agent`. Project references:
`OKF4net.Catalog` only (no `OKF4net.Catalog.Hosting`, no
`Microsoft.Extensions.*`, no chat client — this sample needs no LLM and no
API key, unlike `acme-retail-agent`).

```text
samples/catalog-explorer/
  CatalogExplorer.sln
  README.md
  config/
    catalog.json
  src/CatalogExplorer/
    CatalogExplorer.csproj
    Program.cs
```

### `config/catalog.json`

`CatalogPathResolver.TryResolve` resolves every source's `path` relative to
the **manifest file's own directory** (`samples/catalog-explorer/config/`),
never relative to `CatalogRoot` — `CatalogRoot` is only the containment
boundary the resolved path is checked against, not a resolution base (see
`FileKnowledgeCatalog`'s `_manifestDirectory`, always derived from
`CatalogFilePath`). So paths climb out of `config/` with `..`:

```json
{
  "version": 1,
  "sources": [
    { "id": "acme", "path": "../../../bundles/acme_retail", "role": "knowledge", "priority": 10 },
    { "id": "ga4-reference", "path": "../../../bundles/ga4", "role": "knowledge", "priority": 0 },
    { "id": "mem-session", "path": "../memory/session", "role": "memory", "tier": "session" },
    { "id": "mem-user", "path": "../memory/user", "role": "memory", "tier": "user" },
    { "id": "mem-tenant", "path": "../memory/tenant", "role": "memory", "tier": "tenant" }
  ]
}
```

`../../../bundles/...` climbs `config/` → `catalog-explorer/` → `samples/`
→ repo root, then into `bundles/`; `../memory/...` climbs one level to
`catalog-explorer/`, then into a sibling `memory/` directory. Both stay
inside `CatalogRoot` (see below), which is why `..` is accepted here at all
— `CatalogPathResolver` explicitly allows it, rejecting only a path whose
*resolved* result lands outside the root.

### `Program.cs`

`KnowledgeCatalogOptions` is constructed directly (not via
`AddCatalogFile`, which would derive `CatalogRoot` from `catalog.json`'s own
directory and reject every path above): `CatalogFilePath` points at
`config/catalog.json`, `CatalogRoot` is set explicitly to the repo root,
located by the same "walk up from `AppContext.BaseDirectory` to
`OKF4net.sln`" helper `acme-retail-agent/Program.cs` already uses.

Five scenarios, run in sequence, each printed under its own console header —
no CLI args, no interactivity, `dotnet run` alone produces the full
walkthrough top to bottom:

1. **Load & inspect.** Construct `new FileKnowledgeCatalog(options)` — it
   loads and validates synchronously in the constructor (fail-fast: an
   invalid initial manifest throws `CatalogException`, there is no separate
   `LoadAsync`); print `catalog.Current`'s enabled sources and any load
   diagnostics.
2. **Multi-source search.** Build one `IKnowledgeResolver` for the whole
   walkthrough — `new KnowledgeResolverRouter(catalog)` — and run one query
   (e.g. `"purchase revenue"`) with `ResolverStrategy` left unset (default
   `GroupedBySource`); print `KnowledgeContext.Passages` (grouped, source by
   source) and `.Diagnostics`, showing real contributions from both `acme`
   and `ga4-reference`.
3. **Ranking strategies compared.** The same query run three times through
   the same router, only `KnowledgeQuery.ResolverStrategy` changing —
   `GroupedBySource`, `Merged`, `PriorityWeighted` — passage order printed
   side by side so the strategy's effect on ordering is visible. (No need
   for three separate resolver instances: the router dispatches per query.)
4. **Visibility.** The same query, still through the router, run as three
   callers by varying only the `KnowledgeQuery`: an unscoped caller (neither
   field set — sees everything, today's default); a caller with
   `PermittedSourceIds = { "ga4-reference" }` (public/external-partner —
   sees only the public reference); and a caller with a
   `SourceVisibilityPolicy` closure that grants the `acme` source only when
   `scope.UserId` starts with `"acme-employee-"`, and fails closed (grants
   nothing) for any other `UserId`, including `null` — mirroring the
   fail-closed pattern already documented in `OKF4net.Catalog`'s README.
   (`PermittedSourceIds`/`SourceVisibilityPolicy` are plain `KnowledgeQuery`
   fields in `OKF4net.Catalog` itself — nothing here needs
   `OKF4net.Catalog.Hosting`'s host-wide default.)
5. **Memory tier.** Read `catalog.Current.Sources`, filter to
   `Role == SourceRole.Memory`, and resolve each via
   `CatalogPathResolver.TryResolve(catalog.CatalogRoot, catalog.Current.ManifestDirectory, source.Path, ...)`
   into a `Dictionary<MemoryTier, string>` — by hand, the same handful of
   steps `OKF4net.Catalog.Hosting.AddMemory()` performs, so the manifest's
   `mem-session`/`mem-user`/`mem-tenant` sources are what actually back the
   store rather than a hardcoded path dictionary. Construct
   `new FileMemoryStore(tierRoots)`, write one `MemoryEntry` into the `user`
   tier for a demo scope, read it back via `ReadAsync`, then
   `DeleteScopeAsync` at the end of the run so `dotnet run` leaves no
   residue and stays repeatable. (The memory roots under
   `samples/catalog-explorer/memory/` are disjoint from both knowledge
   roots, `bundles/acme_retail` and `bundles/ga4` — `AddMemory()`'s own
   disjointness check, mirrored or not, would pass either way; noted so a
   future path edit doesn't accidentally nest one inside the other.)

### Error handling

None beyond what the library already provides: `SearchAsync`/`ReadAsync`
are errors-as-data (`KnowledgeContext.Diagnostics`,
`MemoryReadResult`/`MemoryWriteResult`), and the walkthrough prints them as
part of the narrative rather than branching on them — same style as
`acme-retail-agent`.

### Testing

No dedicated test project, same reasoning as `acme-retail-agent`: no bespoke
logic here beyond wiring already covered by `OKF4net.Catalog`'s own test
suite. Verification is manual: `dotnet run` for the walkthrough, plus
`okf validate bundles/ga4` documented in that bundle's README, mirroring
`bundles/acme_retail`'s "Validating" section.

## Out of scope

- `OKF4net.Catalog.Hosting` / `IServiceCollection` wiring — a separate
  sample if/when it's worth building; this one stays DI-free by design so it
  reads as "how the catalog itself works," not "how to host it."
- Hot-reload (`FileSystemWatcher` / `ReloadAsync()`) — no scenario here
  edits `catalog.json` at runtime.
- Adding `samples/catalog-explorer` to `OKF4net.sln` or `ci.yml`.
- Any LLM/agent wiring (`OKF4net.Agents`) — this sample is catalog-only;
  `acme-retail-agent` already covers the agent-facing story.

## Open risks

- None identified — this sample has no external dependency beyond the two
  vendored bundles and the already-shipped `OKF4net.Catalog` API surface.
