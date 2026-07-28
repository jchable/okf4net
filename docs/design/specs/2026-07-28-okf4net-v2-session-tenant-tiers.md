# OKF4net V2 Scoped Memory — Session/Tenant Tiers: Design Spec

**Date:** 2026-07-28
**Status:** Approved for implementation.
**Relation to prior work:** Refines the staging plan in
[2026-07-24-okf4net-v2-scoped-memory-notes.md](2026-07-24-okf4net-v2-scoped-memory-notes.md)
("1. User tier first... 2. Session tier... 3. Tenant tier") and the contracts
implemented by [2026-07-27-okf4net-v2-scoped-memory.md](2026-07-27-okf4net-v2-scoped-memory.md)
(Lot 3, merged to `main`). This is the first of two independent sub-projects
under the "Lot B" umbrella; the second (cross-source resolver fusion) is a
separate, much larger design problem and is deliberately out of scope here —
see [§9 of the local catalog design](2026-07-24-okf4net-local-catalog-design.md#9-v2-design-team-scoped-bundles)
for its own open questions.

## Problem

Lot 3 shipped the full three-tier contract (`KnowledgeAccessScope` with
`TenantId`/`UserId`/`SessionId`, `SourceRole.Memory` + `MemoryTier`,
`MemoryPath.For`, `IMemoryStore`/`FileMemoryStore`) but only ever exercised
the **user** tier end-to-end. Session and tenant tiers exist in every layer of
the mechanism — manifest parsing, DI wiring, `FileMemoryStore`'s read/write/
delete/enumerate logic, `OkfContextProvider`'s configurable `CaptureTier` — but
have **zero test coverage** proving they actually round-trip correctly, and
one piece of documentation (`AddMemory`'s XML doc) is now stale, still saying
"this lot wires the user tier."

## Discovery: the mechanism is already tier-agnostic

Verified directly in the code (not assumed) before any design decision below:

- `FileMemoryStore.IsApplicable` already switches on all three
  `MemoryTier` values (`Session => scope.SessionId is not null`,
  `User => scope.UserId is not null`, `Tenant => scope.TenantId is not null`)
  — no tier is special-cased.
- `ReadAsync`/`WriteAsync`/`DeleteScopeAsync`/`EnumerateAsync` all key off the
  `_tierRoots` dictionary and the `MemoryTier` parameter generically; nothing
  in their bodies assumes `User`.
- `CatalogManifestParser.ParseTier` already accepts `"session"`, `"user"`, and
  `"tenant"`.
- `MemoryServiceCollectionExtensions.AddMemory`'s `ResolveRoots` already reads
  `source.Tier` from *any* `role:memory` source and populates `tierRoots[tier]`
  for whichever tier the manifest declares — not hardcoded to `User`. (Its own
  XML doc comment undersells this: "This lot wires the user tier" is stale.)
- `OkfContextProviderOptions.CaptureTier` (default `MemoryTier.User`) is
  already a public, host-settable property, and `OkfContextProvider` already
  calls `_memoryStore.WriteAsync(scope, entry, _options.CaptureTier, ct)`
  generically.

**Consequence:** this is not a "design and build session/tenant tiers"
project. It is "prove, through real test coverage, that the tier-agnostic
mechanism Lot 3 already built actually works for session and tenant" — plus
one documentation fix and one deployment-pattern write-up. No new storage
abstraction, no new API surface.

## Decision: ephemeral vs. persistent session memory is a deployment choice, not a code branch

The original design notes flagged retention as unresolved: "Session — ephemeral
(end of conversation)... nearly free (can be pure in-memory)," implying session
tier might need a different storage implementation than user/tenant's
file-backed `FileMemoryStore`.

**Resolved: session tier reuses `FileMemoryStore` unchanged.** No new
in-memory-only `IMemoryStore` implementation, no TTL/expiry mechanism inside
the library.

Reasoning: `role:memory` sources are always explicitly declared in
`catalog.json` — there is no "tier not present in the manifest gets an
implicit default" behavior anywhere in the current design (`AddMemory`'s own
doc comment: "a tier not present in the manifest is simply absent from the
store"), and adding one would be new, unrequested magic. Given that, "ephemeral
by default, persistent if you choose" is entirely a property of **which path**
the host points the `tier:session` source at:

- Point it at a temp directory (e.g. under the OS temp path, or a
  container's ephemeral volume) → data does not outlive the temp location's
  own lifecycle → ephemeral in practice.
- Point it at a durable directory (same as user/tenant would use) → the exact
  same code path persists it indefinitely → durable in practice.

Either way, `IMemoryStore.DeleteScopeAsync(scope, MemoryTier.Session)`
(already implemented, already tested for the user tier) is the explicit
cleanup call a host makes when a conversation ends, regardless of which path
choice it made. This lot's job is to document this pattern clearly (with a
`catalog.json` example showing both), not to build a mechanism that picks
between them.

## Explicitly out of scope

- **Cross-source resolver fusion** — separate sub-project, separate spec.
- **"Remember for the team" promotion** (copying/moving a captured entry from
  user tier to tenant tier) — the original design notes call this out as "a
  later refinement"; stays deferred.
- **Automatic TTL/expiry inside the store** — superseded by the deployment-path
  decision above; `DeleteScopeAsync` remains the only cleanup mechanism.
- **The in-progress core spec v0.1 → v0.2 work** (branch
  `okf4net-okf0.2-support`, unmerged) — this sub-project targets `dev`'s
  current shape of `ConceptId`/`Frontmatter`/`BundleConceptWriter`; it does
  not anticipate or depend on the new Lifecycle/Provenance/Trust/Actor
  concepts that branch is adding to core.

## Work

1. **Test coverage for `FileMemoryStore`, mirrored per tier.** For both
   `MemoryTier.Session` and `MemoryTier.Tenant`, add the same shape of test
   `FileMemoryStoreTests.cs` already has for `User`:
   - Write-then-read round-trip.
   - Cross-scope isolation (two distinct sessions/tenants cannot read each
     other's entries) — the existing `A_tenant_A_scope_cannot_read_tenant_B_memory`-style
     test, parameterized or duplicated per tier.
   - `EnumerateAsync` isolation — Lot A's
     `Enumerate_does_not_list_a_different_scopes_concepts` proved cross-scope
     isolation but only ever wrote/read `MemoryTier.User`; add the same
     shape of test with `MemoryTier.Session` and `MemoryTier.Tenant`.
   - `DeleteScopeAsync` removes only the target scope's subtree.
   - A `Local` scope round-trip for session tier specifically, since
     `KnowledgeAccessScope.IsLocal` is the single-user desktop/CLI degenerate
     case every tier must still support.
2. **`OkfContextProvider` capture-tier coverage.** Extend
   `OkfContextProviderMemoryTests.cs` (or equivalent) with a scripted E2E case
   setting `CaptureTier = MemoryTier.Session` (today only `User` is exercised
   end-to-end through the provider), confirming capture writes to the
   configured session root and a subsequent read recalls it within the same
   scope.
3. **Fix `AddMemory`'s stale doc comment** — remove "this lot wires the user
   tier," describe the tier-agnostic wiring accurately.
4. **Deployment pattern documentation** — a `catalog.json` example (README or
   a doc under `docs/`) showing all three `role:memory` sources configured
   together, with the session source pointed at a temp path and an inline
   note on the ephemeral-vs-persistent path choice and `DeleteScopeAsync`'s
   role.

## Testing

Existing `FileMemoryStoreTests.cs`/`MemoryServiceCollectionExtensionsTests.cs`/
`OkfContextProviderMemoryTests.cs` patterns are the template — no new test
infrastructure or helpers needed beyond what `User`-tier tests already use
(`TempDir`, the `MemPath` derivation helper, `KnowledgeAccessScope`
constructors). Full suite (`dotnet test OKF4net.sln`) plus format check must
stay green throughout, per repo convention.

## Acceptance criteria

- Session and tenant tiers have the same depth of test coverage the user tier
  already has: round-trip, cross-scope isolation, enumerate, delete — no gaps
  relative to `User`.
- `AddMemory`'s doc comment accurately describes its already-generic behavior.
- A documented `catalog.json` example demonstrates configuring all three
  tiers, including the ephemeral-session-via-temp-path pattern.
- No new public API surface, no new storage implementation, no TTL/expiry
  mechanism added.
