# OKF4net V2 — Per-Caller Source Visibility (Lot B sub-project 3)

**Status:** approved design, not yet planned/implemented.

## 1. Goal

Let a host restrict *which* enabled `Knowledge`-role catalog sources a given
caller's search may see, based on the caller's authenticated identity
(`KnowledgeAccessScope`) — a filtering concern upstream of search, orthogonal
to the ranking/fusion concern the resolver-strategies lot (v0.3.0) already
shipped downstream of it.

## 2. Motivation

Today, `GroupedKnowledgeResolver` and `FusedResolverEngine` (the shared
engine behind `MergedKnowledgeResolver`/`PriorityWeightedKnowledgeResolver`)
both select eligible sources with exactly one filter:

```csharp
.Where(s => s.Enabled && s.Role == SourceRole.Knowledge)
```

(`src/OKF4net.Catalog/GroupedKnowledgeResolver.cs:80`,
`src/OKF4net.Catalog/FusedResolverEngine.cs:76`) — no notion of *who is
asking* enters into it. Every enabled knowledge source is visible to every
caller, unconditionally.

`KnowledgeAccessScope` (tenant/user/session identity, always host-supplied
via a delegate — `OkfContextProviderOptions.ScopeAccessor` — never derived
from a message) already exists and is already resolved on every scoped
invocation (`OkfContextProvider.cs:293`), but today it is used for exactly
one thing: scoping the memory read/write
(`_memoryStore!.ReadAsync(scope, ...)`, `OkfContextProvider.cs:336`). It is
never threaded into the knowledge search at all. This design closes that
gap — the identity plumbing already exists; only the wiring is missing.

The original, unimplemented sketch for this
(`docs/design/specs/2026-07-24-okf4net-local-catalog-design.md` §9) already
established the guiding principle this design follows: *"The hosting layer,
not `OKF4net.Catalog`, maps that scope to permitted source IDs. The resolver
receives an already filtered snapshot or a source-selector policy supplied
by the host."* No authorization language, role system, or ACL DSL belongs in
the zero-dependency catalog core; the core only ever asks a host-supplied
function or set "is this source visible," never decides visibility itself.

## 3. Terminology

- **Scope** — a `KnowledgeAccessScope` identifying the caller (tenant/user/
  session, or the all-null `Local` sentinel for the unscoped case).
- **Visibility policy** — a host-supplied function deciding, per source, per
  scope, whether that source is eligible for that caller's search.
- **Permitted set** — a host-precomputed, per-request set of source IDs a
  caller may see; the simpler of the two mechanisms this design offers.

## 4. Data model

```csharp
// KnowledgeQuery gains three members:
public KnowledgeAccessScope Scope { get; init; } = KnowledgeAccessScope.Local;
public IReadOnlySet<string>? PermittedSourceIds { get; init; }
public Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? SourceVisibilityPolicy { get; init; }

// KnowledgeOptions (Hosting) gains one member:
public Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>? DefaultSourceVisibilityPolicy { get; set; }
```

`Scope` is **non-nullable**, defaulting to `KnowledgeAccessScope.Local` — the
same all-null sentinel already used throughout the memory-scoping work,
rather than inventing a second nullability story for "no identity supplied."
A policy evaluated against `Local` decides for itself whether an unscoped
caller sees everything or nothing; that is a policy decision, not a plumbing
one.

`PermittedSourceIds` is the **default/recommended mechanism** — a host
precomputes (however it wants: tenant lookup, application/purpose lookup, or
both combined; `OKF4net.Catalog` never needs to know which) the exact set of
source IDs a caller may see, and hands it to the query. This mirrors the
original local-catalog design's §9 sketch (§2 above) — its "already filtered
snapshot" phrasing, almost verbatim. It has **no host-level default** — a static ID set cannot represent "differs
by tenant" at host-configuration time, so it exists only per-query.

`SourceVisibilityPolicy` is the **override mechanism** — a function
evaluated per source, for cases a flat ID list can't express conveniently.
Unlike `PermittedSourceIds`, it **does** have a host-level default
(`KnowledgeOptions.DefaultSourceVisibilityPolicy`), because a function (not a
static value) can still vary its answer per call by reading the
`KnowledgeAccessScope` argument it's given — the same reason
`OkfContextProviderOptions.ScopeAccessor` and
`KnowledgeOptions.DefaultResolverStrategy` can each be configured once and
still produce per-call-varying results.

**Documented departure from a stated V1 decision:** `KnowledgeQuery.cs`
currently carries the remark *"Deliberately V1-scoped: no user/tenant/path
fields. Those are identity and routing concerns the OKF spec (§8) keeps
orthogonal to a search query, and adding them here would be premature
surface before an actual multi-tenant consumer exists."* This design is
precisely that consumer materializing; the remark's own reasoning is the
justification for revisiting it, not something this design silently
overrides. The doc comment must be rewritten to reflect this, not deleted
without explanation.

## 5. Resolution algorithm

Applied once per search, before the fan-out, in both
`GroupedKnowledgeResolver` and `FusedResolverEngine` — centralized in one
new shared helper (mirroring how `ResolverGuards` already centralizes
cross-strategy validation, precisely to prevent this exact algorithm from
drifting between the two call sites): `internal static class
SourceVisibility`, with a method resolving the enabled+knowledge-role
source list down to the visible subset for one query.

In order:

1. If `query.PermittedSourceIds` and `query.SourceVisibilityPolicy` are
   **both** set on the same query, reject (§6) — a caller-created
   contradiction, not something to silently resolve.
2. Else if `query.PermittedSourceIds` is set, keep only sources whose `Id`
   is in that set. This always wins over any host-level default policy —
   being query-level, it is inherently more specific to this one call.
3. Else if `query.SourceVisibilityPolicy` is set, keep only sources for
   which `policy(query.Scope, source)` returns `true`. This overrides the
   host default for this one call.
4. Else if `KnowledgeOptions.DefaultSourceVisibilityPolicy` is configured
   (threaded down to the router/resolvers the same way
   `DefaultResolverStrategy`/`DefaultFairnessQuota` already are), apply it
   the same way as step 3.
5. Else, no restriction — every enabled `Knowledge`-role source remains
   eligible, identical to today's behavior. This is the case for every
   existing deployment that configures nothing new: **upgrading changes
   nothing until a host opts in**, the same guarantee the resolver-strategies
   lot already established for `ResolverStrategy`/`FairnessQuota`.

A host-level default policy is threaded through the same plumbing that
already carries `DefaultResolverStrategy`/`DefaultFairnessQuota` from
`KnowledgeOptions` into the concrete resolvers/router — no new dependency
direction, no new registration mechanism. Concretely, this touches all four
existing constructors the same way `defaultFairnessQuota` already touches
three of them:

- `GroupedKnowledgeResolver(IKnowledgeCatalog, IOkfClock?, ...)` — gains
  `Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>?
  defaultSourceVisibilityPolicy = null` (its first new optional parameter;
  today it has no default beyond `clock`).
- `MergedKnowledgeResolver`/`PriorityWeightedKnowledgeResolver` — each
  already takes `int? defaultFairnessQuota = null`; both gain
  `defaultSourceVisibilityPolicy` alongside it.
- `KnowledgeResolverRouter` — gains the same parameter, and passes it to
  all three internally-constructed resolvers, exactly as it already does
  for `defaultFairnessQuota`.
- `KnowledgeServiceCollectionExtensions.AddKnowledge` — reads
  `options.DefaultSourceVisibilityPolicy` into a local (same pattern as
  `defaultStrategy`/`defaultFairnessQuota` at
  `KnowledgeServiceCollectionExtensions.cs:87-89`) and passes it into the
  `KnowledgeResolverRouter` constructor call.

## 6. Validation

`ResolverGuards.ValidateQuery` gains the check for step 1 above: setting
both `PermittedSourceIds` and `SourceVisibilityPolicy` on the same query
throws `ArgumentException`, checked at the same shared boundary every
resolver already calls before doing any work — so this fails identically
regardless of which strategy handles the query, matching the reasoning
already established for the `FairnessQuota`/`ResolverStrategy` checks living
there.

An unknown ID in `PermittedSourceIds` (a typo, a source that was since
disabled or removed from the manifest) is not a validation error — it
simply matches nothing, exactly like any other query that admits zero
results. No special-casing needed; set intersection already handles it.

## 7. `OkfContextProvider` integration

Surgical change only. `scope` is already resolved once per invocation
(`OkfContextProvider.cs:293`, `_options.ScopeAccessor?.Invoke(context) ??
KnowledgeAccessScope.Local`) and already used for the memory read
(`OkfContextProvider.cs:336`). The knowledge query built at
`OkfContextProvider.cs:327` gains `Scope = scope` alongside its existing
`FairnessQuota = _options.KnowledgeQueryFairnessQuota` — the same identity
already resolved for memory now also reaches knowledge search. No new
option on `OkfContextProviderOptions`: the identity-resolution mechanism
already exists, only the wiring was missing.

## 8. Existing types touched

- `IKnowledgeResolver.cs:5-6` — "Searches across every enabled
  `SourceRole.Knowledge` source" becomes "every enabled, *visible*"; the
  `<exception>` on `SearchAsync` (`IKnowledgeResolver.cs:36-40`) gains the
  new `PermittedSourceIds`+`SourceVisibilityPolicy` case. While that block
  is being touched: it currently omits the `ResolverStrategy` validation
  case `ResolverGuards.ValidateQuery` already added in the resolver-strategies
  lot (a pre-existing gap, not introduced by this design) — worth closing in
  the same pass rather than adding a third omission next to it.
- `KnowledgeQuery.cs` — three new members (§4) plus the `<remarks>` rewrite
  documenting why the V1-scoped restriction no longer applies (§4).
- `KnowledgeOptions.cs` — one new member, doc-commented the same way
  `DefaultResolverStrategy`/`DefaultFairnessQuota` already are.
- `ResolverGuards.cs` — `ValidateQuery` gains the
  `PermittedSourceIds`+`SourceVisibilityPolicy` mutual-exclusion check (§6).
- Four constructors (§7): `GroupedKnowledgeResolver`, `MergedKnowledgeResolver`,
  `PriorityWeightedKnowledgeResolver`, `KnowledgeResolverRouter`.
- `KnowledgeServiceCollectionExtensions.AddKnowledge` — reads and threads
  the new host default (§7).
- `src/OKF4net.Catalog/README.md`'s "Choosing a ranking strategy" section
  (added by the resolver-strategies lot) gets a sibling "Choosing source
  visibility" section with a worked example, following the same pattern.

## 9. Explicitly out of scope

- **A manifest-declarative visibility syntax** (tags/rules embedded in
  `catalog.json`) — considered and rejected during brainstorming as
  reinventing an ACL DSL inside the zero-dependency catalog core, directly
  contradicting the original local-catalog design's §9 "no ASP.NET Core
  claims in the catalog core" principle (§2 above). A host that wants
  config-driven visibility can still build one by having
  `DefaultSourceVisibilityPolicy` read whatever config format it likes —
  that composition is available without adding syntax here.
- **Per-source async visibility checks** (e.g. a policy that needs to await
  a database call per source, per search) — the policy signature is
  synchronous (`Func<KnowledgeAccessScope, KnowledgeCatalogSource, bool>`).
  A host needing async work to determine visibility does it once, before
  constructing the query (precisely what `PermittedSourceIds` is for), not
  per source inside the resolver's fan-out loop.
- **Validating `PermittedSourceIds` entries against the current catalog
  snapshot** — an unknown ID is treated as "matches nothing," not an error
  (§6).
- **Extending visibility filtering to `SourceRole.Memory` sources** — memory
  sources are never searched by any `IKnowledgeResolver` strategy (they feed
  `IMemoryStore` instead, per spec §5.3) and already have their own,
  separate scoping mechanism (`KnowledgeAccessScope` passed directly to
  `IMemoryStore.ReadAsync`/`WriteAsync`). Out of scope by construction, not
  by omission.

## 10. Acceptance criteria

- A query with `PermittedSourceIds` set to a subset of the enabled sources
  returns matches only from that subset, from every one of the three
  resolver strategies.
- A query with `SourceVisibilityPolicy` set receives exactly
  `(query.Scope, source)` for each enabled knowledge source under
  consideration, and only sources it approves are searched.
- A host with `KnowledgeOptions.DefaultSourceVisibilityPolicy` configured,
  queried without either query-level field set, applies that default.
- A query with `PermittedSourceIds` set on a host that also has
  `DefaultSourceVisibilityPolicy` configured uses the `PermittedSourceIds`
  restriction, not the host default.
- A query with both `PermittedSourceIds` and `SourceVisibilityPolicy` set
  throws `ArgumentException`, identically across all three strategies.
- A host that configures neither field, and a query that sets neither, see
  byte-identical results to before this design — no behavioral change on
  upgrade.
- `OkfContextProvider`'s scoped (V2) mode passes the same
  `KnowledgeAccessScope` to both the knowledge query and the memory read for
  one invocation.
