# OKF4net V2 — Resolver Strategies (Lot B sub-project 2)

**Status:** approved design, not yet planned/implemented.

## 1. Goal

Replace the single, fixed grouped-by-source search behavior of
`DefaultKnowledgeResolver` with a choice of **named resolver strategies**,
selectable per host (DI configuration) or per query (explicit override),
without breaking any existing consumer of `IKnowledgeResolver`.

## 2. Motivation

The concrete driver is agent/MCP context injection under a shared token
budget (`OkfContextProvider`, `okf_search`). Tracing
`OkfContextProvider.AppendPassages` (`src/OKF4net.Agents/OkfContextProvider.cs:342-348`)
shows it walks `KnowledgeContext.Passages` **in the order the resolver
returns them** and stops once its token budget is exhausted — there is no
"top-N" concept anywhere, the resolver returns everything and the caller
truncates by walking.

With today's V1 `DefaultKnowledgeResolver`, that order is **grouped by
source**: every passage from the highest-priority source, then every
passage from the next, etc. Under a tight budget, a weak match from a
high-priority source is rendered before a strong match from a
lower-priority source ever gets a chance. A single merged, score-ranked
list fixes this directly.

Rather than pick one "correct" ranking algorithm and force every caller
onto it, this design offers **multiple resolver strategies** side by side,
selectable at the host or per query — including keeping today's grouped
behavior exactly as it is, for callers that already depend on it.

## 3. Terminology

- **Strategy** — one concrete ranking/fusion algorithm, implemented as a
  standalone `IKnowledgeResolver`.
- **Fusion** — merging passages from multiple sources into a single
  ordered list (as opposed to V1's grouped concatenation).
- **Dedup** — collapsing two passages that are provably the same
  underlying concept, found through more than one catalog source entry.
- **Fairness quota** — the maximum number of consecutive passages from one
  source before the output makes room for a different source, in a fused
  strategy's output.

## 4. Resolver strategies

Three concrete `IKnowledgeResolver` implementations:

### 4.1 `GroupedKnowledgeResolver`

A pure rename of today's `DefaultKnowledgeResolver` — **zero behavior
change**. Every enabled source searched, results concatenated grouped by
source (descending `Priority`, then ordinal `Id`), no dedup, no fusion.
Renamed because "Default" stops meaning "the only resolver" once three
strategies exist side by side; `GroupedBySource` names what it actually
does.

### 4.2 `MergedKnowledgeResolver`

Fuses all sources into one list:

1. Fan out to every enabled `Knowledge`-role source, as `GroupedKnowledgeResolver`
   does today (unchanged: `CatalogPathResolver.TryResolve` per source,
   `SourceUnavailable` diagnostic for a source that fails re-resolution).
2. **Dedup** (§5).
3. Apply `KnowledgeQuery.StalePolicy` admission filtering (unchanged
   semantics, same place in the pipeline as today).
4. Sort the admitted, deduped passages by `Score` desc, then `Priority`
   desc, then `SourceId` ordinal, then `ConceptId` ordinal (full
   determinism; `Priority` here is a **tie-break only**, exactly its
   existing meaning — never a score multiplier).
5. Optional **fairness reordering** (§6), governed by a resolved
   `FairnessQuota` (query override, else the resolver's own default, else
   disabled/`null`).

No score normalization: `ConceptSearch`'s scorer (title ×3, tags/description
×2, body ×1) has no per-corpus statistics (no IDF, no document-frequency
adjustment) — it is the same deterministic weighted term-count formula
regardless of source, so raw scores from different bundles are directly
comparable without adjustment.

### 4.3 `PriorityWeightedKnowledgeResolver`

Identical pipeline to `MergedKnowledgeResolver` (fan-out → dedup → stale
filter → optional fairness reorder), differing only in the sort key order
at step 4: **`Priority` desc first, `Score` desc only within a priority
tier**, then `SourceId`/`ConceptId` ordinal. This gives an operator an
honest "this source's results always outrank that one, regardless of
match strength" guarantee — a lexicographic sort-key swap, not a numeric
blend. A numeric blend (e.g. `effectiveScore = rawScore + priority * K`)
was considered and rejected: it would require inventing a scale/unit for
`Priority` relative to `Score` with no principled default, adding
complexity and surprise for a benefit no concrete use case has
demonstrated. The lexicographic approach needs no such mapping.

`MergedKnowledgeResolver` and `PriorityWeightedKnowledgeResolver` share one
internal engine (fan-out, dedup, stale filtering, fairness reorder) and
differ only in the final comparator, to avoid duplicating that pipeline
twice.

## 5. Dedup

`ConceptId` is derived purely from a concept file's path **relative to its
own bundle root** (`ConceptId.FromPath`, `src/OKF4net/ConceptId.cs:115`) —
it is not a globally stable identity. Two unrelated sources can both have
a concept at, say, `policies/refund.md` and produce the identical
`ConceptId` string by pure coincidence, with completely unrelated content.
There is no content hash or explicit cross-bundle correlation anywhere in
the codebase today (`Provenance.Source`, OKF spec §5.1, is a concept's own
citation list, not an identity signal).

Given no reliable general-purpose signal, dedup in this design is
deliberately narrow: `MergedKnowledgeResolver`/`PriorityWeightedKnowledgeResolver`
already resolve each enabled source's absolute bundle directory during
fan-out (`CatalogPathResolver.TryResolve`). If two enabled source entries
resolve to the **literal same directory** (e.g. the same bundle
accidentally mounted twice under two `catalog.json` entries), a passage
sharing both that resolved directory and its `ConceptId` is the same
content found twice — keep one, discard the other: whichever of the two
source entries has the higher `Priority` (then, if still tied, the lower
ordinal `Id`) is the survivor, matching the same source-ordering
convention §4.2/§4.3 already use. Two different resolved
directories that happen to produce the same `ConceptId` string are
**never** merged — doing so would silently conflate unrelated concepts,
which is worse than a visible duplicate.

Fuzzy/semantic duplicate detection (similar content, different id or
directory) is out of scope — a materially different, higher-risk feature
(similarity thresholds, possibly embeddings) with no concrete need
demonstrated yet.

`GroupedKnowledgeResolver` gets no dedup — its behavior is explicitly
frozen as-is (§4.1).

## 6. Fairness reordering

Optional, and only meaningful for the two fused strategies. Given a
`FairnessQuota` `K` (a positive int; `null`/unset disables it entirely —
pure score order), reordering runs **after** the primary sort (§4.2/4.3)
and **never drops a passage** — it only reorders, so a caller that
consumes the entire list (not budget-truncated) sees the same set of
results either way.

Algorithm: walk the sorted list left to right, tracking the current
"run" (the source of the last emitted passage and how many consecutive
passages have come from it). When the next passage in line would extend a
run past `K`, and at least one passage from a *different* source remains
further down the list, pull that passage forward instead (preserving its
relative position among same-source passages) and resume; the displaced
passage remains in the queue and is emitted at its next opportunity. If
every remaining passage is from the same source (no alternative to pull
forward), the quota cannot be honored and the algorithm simply continues
draining that source — the quota is a fairness goal for
interleaved-source result sets, not a hard mathematical guarantee when one
source vastly outnumbers the others.

This is a bounded, best-effort reorder over what is expected to be a
small result set (dozens to low hundreds of passages per query) — no
particular time-complexity target is set by this design; the
implementation plan should note whatever bound the simplest correct
approach yields.

## 7. Selecting a strategy

```csharp
namespace OKF4net.Catalog;

public enum KnowledgeResolverStrategy
{
    GroupedBySource,
    Merged,
    PriorityWeighted,
}
```

- **`KnowledgeQuery`** gains:
  - `KnowledgeResolverStrategy? ResolverStrategy { get; init; }` — `null`
    means "use the host default."
  - `int? FairnessQuota { get; init; }` — `null` means "use the host
    default"; meaningful only for `Merged`/`PriorityWeighted`, silently
    unused by `GroupedBySource`.
- **`KnowledgeOptions`** (`src/OKF4net.Catalog.Hosting/KnowledgeOptions.cs`,
  the `AddKnowledge` configure callback) gains:
  - `KnowledgeResolverStrategy DefaultResolverStrategy { get; set; } = KnowledgeResolverStrategy.GroupedBySource;`
    — defaults to today's exact behavior, so an existing host that
    configures nothing new sees no change.
  - `int? DefaultFairnessQuota { get; set; }` — `null` by default
    (disabled).
- **`KnowledgeResolverRouter : IKnowledgeResolver`** (new, in
  `OKF4net.Catalog`) owns one instance of each of the three concrete
  resolvers plus the host's default strategy/quota. On `SearchAsync`, it
  resolves `query.ResolverStrategy ?? hostDefaultStrategy` and
  `query.FairnessQuota ?? hostDefaultFairnessQuota`, and delegates to the
  matching concrete resolver. `KnowledgeServiceCollectionExtensions.AddKnowledge`
  registers the router as the singleton `IKnowledgeResolver` (replacing
  today's direct `new DefaultKnowledgeResolver(catalog)` registration) —
  every existing consumer that resolves `IKnowledgeResolver` via DI keeps
  working unchanged, with the new per-query override now reachable through
  that same injected instance.

## 8. Existing types touched

- `KnowledgeContext.Passages`'s XML doc (`src/OKF4net.Catalog/KnowledgeContext.cs:16-22`)
  currently states a fixed grouped-by-source ordering contract "(V1
  scope)". That contract becomes resolver-specific: the doc comment must
  say the ordering depends on which `KnowledgeResolverStrategy` produced
  the result, and point to each resolver's own ordering guarantee instead
  of asserting one fixed contract.
- `DefaultKnowledgeResolver` is renamed to `GroupedKnowledgeResolver` (§4.1)
  — a breaking rename for any direct (non-DI) consumer of the concrete
  type name. Acceptable pre-1.0, no compatibility shim, consistent with
  how `MemoryCaptureMode` replaced `EnableMemoryCapture` outright.

## 9. Documentation

`src/OKF4net.Catalog/README.md`'s "V1 limits" bullet ("results are grouped
by source — no fusion, deduplication, or merged cross-source ranking") and
the root `README.md`'s "V2 preview" paragraph (cross-source result fusion)
both need rewriting from "not implemented" to shipped, describing the
three strategies and how to select one — mirroring how the scoped-memory
work updated the same two files from preview language to shipped
documentation.

## 10. Explicitly out of scope

- **Application-filtered bundles** (per-caller/per-tenant visibility of
  which sources are searched at all) — a filtering concern upstream of
  search, orthogonal to fusion/ranking downstream of search. Originally
  bundled with fusion under one "V2 preview" README paragraph; split out
  here as its own future sub-project.
- **Fuzzy/semantic dedup** (§5) — no reliable signal exists today.
- **Numeric priority-weighted score blending** — superseded by the
  lexicographic `PriorityWeighted` strategy (§4.3), which needs no
  scale/unit decision.
- Everything §9 of the original local-catalog design already deferred
  that this design doesn't pick up: token-budget allocation *within* the
  resolver (the caller still owns budget truncation, as it does today),
  citations, and audit diagnostics beyond the existing
  `SourceUnavailable`/`NoMatches`/`NoEnabledSources` set.

## 11. Acceptance criteria

- A query against `GroupedKnowledgeResolver` produces byte-identical
  ordering behavior to today's `DefaultKnowledgeResolver`, source-renamed
  only.
- A query against `MergedKnowledgeResolver` with two sources returns a
  single list ordered by descending score across both sources, with a
  passage found via two source entries that resolve to the same directory
  appearing exactly once.
- A query with the same `ConceptId` present in two *different* resolved
  directories returns **both** passages (never falsely deduped).
- A query against `PriorityWeightedKnowledgeResolver` with two sources of
  different `Priority` never places a lower-priority source's passage
  ahead of a higher-priority source's passage, regardless of score.
- A `Merged`/`PriorityWeighted` query with a `FairnessQuota` set produces
  an output containing the exact same passages as with no quota set (no
  data loss), reordered so no source exceeds the quota in a row unless no
  alternative source has passages remaining.
- `KnowledgeQuery.ResolverStrategy`/`FairnessQuota` left `null` falls back
  to `KnowledgeOptions.DefaultResolverStrategy`/`DefaultFairnessQuota`;
  a host that configures neither sees `GroupedBySource` behavior,
  unchanged from today.
