# Catalog Explorer sample

A standalone console app walking through [`OKF4net.Catalog`](../../src/OKF4net.Catalog/README.md)
end to end: multi-source search, ranking-strategy comparison, per-caller
source visibility, and the `role: memory` tier — against
[`bundles/acme_retail`](../../bundles/README.md#acme-retail) and
[`bundles/ga4`](../../bundles/README.md#ga4). Uses `OKF4net.Catalog`
directly, with no dependency injection and no `OKF4net.Catalog.Hosting` —
see [the design spec](../../docs/superpowers/specs/2026-07-31-catalog-explorer-sample-design.md)
for the full rationale, including why these two bundles (not two unrelated
ones) and why DI is out of scope here.

Standalone: this project has its own `CatalogExplorer.sln`, is not part of
`OKF4net.sln`, and is not built or tested by this repo's CI.

## Run

```bash
dotnet run --project samples/catalog-explorer/src/CatalogExplorer
```

No environment variables, no API key, no network access — every scenario
runs against the two bundles already vendored in this repo.

## What it does

Five scenarios, printed in sequence:

1. **Load & inspect** — constructs `FileKnowledgeCatalog` over
   `config/catalog.json` and prints its five sources.
2. **Multi-source search** — one query (`"revenue purchase"`) through
   `KnowledgeResolverRouter`, default `GroupedBySource` strategy, showing
   real contributions from both `acme` (Acme's own proprietary metrics)
   and `ga4-reference` (Google's public GA4 reference docs).
3. **Ranking strategies compared** — the same query re-run under
   `Merged` and `PriorityWeighted`. `Merged` visibly interleaves passages
   from both sources by descending score. `PriorityWeighted` sorts by
   source priority first, score only within a tie — with `acme` and
   `ga4-reference` at two distinct priorities, that collapses to the same
   output as `GroupedBySource` here (the two strategies only diverge when
   two or more sources *share* a priority); still worth seeing side by
   side to understand why.
4. **Visibility** — the same query as an unscoped caller, an
   external-partner caller restricted to the public `ga4-reference`
   source via `PermittedSourceIds`, and an Acme-employee caller granted
   both sources via a fail-closed `SourceVisibilityPolicy`.
5. **Memory tier** — resolves the manifest's `role: memory` sources into
   a `FileMemoryStore` by hand (the same steps
   `OKF4net.Catalog.Hosting.AddMemory()` performs), writes one memory
   entry, reads it back, then deletes it so the run leaves no residue.
