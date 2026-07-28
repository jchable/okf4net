# OKF4net V2 — Scoped Memory (Lot 3): Design Spec

**Date:** 2026-07-27
**Status:** Approved design (brainstorming complete). Feeds the Lot 3
implementation plan (via the writing-plans flow).
**Refines:** the design notes in
[2026-07-24-okf4net-v2-scoped-memory-notes.md](2026-07-24-okf4net-v2-scoped-memory-notes.md)
and the "Deferred V2" section (§9) of
[2026-07-24-okf4net-local-catalog-design.md](2026-07-24-okf4net-local-catalog-design.md).

## 1. Problem & goal

`OkfContextProvider` (V1) reads *and* writes the same bundle, so memory capture
is bundle-global and unscoped: on a bundle shared by several users/tenants, a
scored recall can surface one session's captured exchange in another. V1 ships
a mitigation only — `MemoryCaptureMode.Disabled` by default. **V2 lets capture
be enabled on a multi-user deployment without cross-scope leakage.**

## 2. Framing: two write paths, not "read-only vs writable"

An earlier framing called knowledge "read-only". That is imprecise: OKF bundles
are writable today through `OkfBundleTools` (`okf_write_concept`,
`okf_append_log`, `okf_regenerate_indexes`). The real distinction is **two
different write paths**:

| Write path | Nature | Scope |
|---|---|---|
| **Knowledge authoring** — `okf_write_concept` & co. | Deliberate, tool-gated curation (like editing a shared wiki) | shared, intentional — **unchanged by V2** |
| **Memory capture** — `StoreAIContextAsync` (automatic) | Implicit capture of the conversation exchange | **the leak surface → scoped by V2** |

What *is* read-only is the **resolver operation**:
`IKnowledgeResolver.SearchAsync` searches, never mutates. V2 adds a **scoped
memory-capture sink** for the automatic path; it does not restrict deliberate
authoring.

## 3. Scope of this lot

- **Three-tier contracts, user-tier-first.** Design the session/user/tenant
  contracts so nothing is cornered, but **implement only the user tier**
  (durable — highest value: the assistant that remembers a user across
  sessions). Session and tenant tiers are recognized by the manifest parser;
  their storage implementation is staged for later lots.
- **Catalog convergence.** Memory is modeled as catalog `role: memory` sources;
  the agent-side provider consumes the catalog via an
  `IKnowledgeResolver → AIContextProvider` adapter rather than owning a single
  bundle path.
- **Out of scope (separate specs):** multi-source *fusion* of the resolver
  (V1 groups by source, no fusion); hashed scope keys; session/tenant tier
  storage implementations.

## 4. Architecture & components

Guiding principle: **knowledge search (read-only, shared)** and **memory
capture (writable, scoped)** are distinct surfaces; scope applies only to
memory.

| Unit | Responsibility | Project |
|---|---|---|
| `KnowledgeAccessScope` | Immutable authenticated value `{ TenantId?, UserId?, SessionId? }`. All-null ⇒ "local" (desktop/CLI — the degenerate single-scope case). **Never derived from a message.** | `OKF4net.Catalog` |
| `SourceRole.Memory` + `tier` | New enum value; a `catalog.json` source with `role:"memory"` declares its `tier` (`session`/`user`/`tenant`). Parser extended + validated. | `OKF4net.Catalog` |
| `IMemoryStore` | **Scoped read+write** contract: `ReadAsync`, `WriteAsync`, `DeleteScopeAsync`, `EnumerateAsync` (see §6, §8). `IKnowledgeResolver` stays **read-only, unchanged**. | `OKF4net.Catalog` |
| **Core write primitive** (§4.1) | Atomic read-modify-write append-to-concept + a process-wide per-path lock registry, **promoted from `OkfBundleTools` to core** so both `OkfBundleTools` and `FileMemoryStore` reuse it (dependency-legal; no duplicate lock registry). | `OKF4net` |
| `FileMemoryStore` | Filesystem implementation. Path derivation isolated in one function (`MemoryPath.For`). Writes reuse the **core write primitive** (§4.1) — producer validation + per-path lock + reparse guards — under the scoped root. **User tier implemented**; session/tenant staged. | `OKF4net.Catalog` |
| `IKnowledgeResolver → AIContextProvider` adapter | Per invocation: resolve scope via the delegate accessor; READ = knowledge (resolver) ∪ memory (store), budget-bounded, injected as message data; WRITE = capture → memory store, one tier. The V1 `OkfContextProvider` **evolves into** this adapter. | `OKF4net.Agents` |

**Dependency edge (decision A):** `OKF4net.Agents` gains a reference to
`OKF4net.Catalog`, so the adapter lives in `OKF4net.Agents` and consumes both
`IKnowledgeResolver`/`IMemoryStore` (Catalog) and `AIContextProvider`
(Microsoft.Agents.AI). The graph stays acyclic:
`Agents → Catalog → OKF4net`, and `Agents → Microsoft.Agents.AI`. `Catalog`
remains BCL + core only.

## 5. Data model

### 5.1 `KnowledgeAccessScope`

Immutable record with `TenantId?`, `UserId?`, `SessionId?` — opaque strings,
each **validated via `ConceptId.ValidateSegment`** (rejects `/`, `..`, embedded
NUL), so a scope is a path-safe key *by construction*. All-null ⇒ "local".

### 5.2 Path derivation — one isolated function

`MemoryPath.For(tier, scope)` maps a tier + scope to a **readable-prefix**
subpath, relative to a memory source's root:

```
tenant   → memory-tenant/<tenant>/
user     → memory-user/<tenant>/<user>/     ← implemented this lot
session  → memory-session/<session>/        ← superseded: nests under tenant/user too, see 2026-07-28-okf4net-v2-session-tenant-tiers.md
```

- A null `<tenant>` renders as the sentinel segment `_local` (desktop/CLI).
- User memory nests under tenant, so cross-tenant collision is impossible by
  construction.
- Switching to hashed keys later changes **only this function**.

### 5.3 Manifest: `role: "memory"` + `tier`

```json
{ "id": "user-mem", "path": "./mem/user", "role": "memory", "tier": "user" }
```

- Add `SourceRole.Memory` (today reserved, undefined). A `role:memory` source
  **requires** a valid `tier` (`session`|`user`|`tenant`); otherwise a new
  `CatalogDiagnosticCode` is reported (errors-as-data — the parser never
  throws, consistent with V1).
- The source `path` is the **root**; `MemoryPath.For` templates the scoped
  subpath beneath it at runtime.
- `role:memory` sources are **not** searched by `IKnowledgeResolver`; they feed
  `IMemoryStore`. Clean surface separation.
- **One memory source per tier** (resolves the notes' open "one source vs one
  per tier"). A manifest may carry up to three. `IMemoryStore` is configured
  with these sources: `ReadAsync` unions the tiers applicable to the scope;
  `WriteAsync(scope, tier)` routes to that tier's source. This lot
  registers + implements the **user-tier** source; session/tenant sources parse
  but their stores are staged.

## 6. Data flow (per invocation)

### 6.1 READ — `ProvideAIContextAsync` (progressive disclosure, budget-bounded)

1. Resolve `scope` via the delegate accessor (host-authenticated). All-null ⇒
   "local".
2. Derive the query from the last user message (as V1).
3. **Knowledge:** `resolver.SearchAsync(query)` → shared passages.
4. **Memory:** `memoryStore.ReadAsync(scope, query)` → scoped passages, **union
   of applicable tiers, most-specific first** (session → user → tenant). Scored
   via the shared core `ConceptSearch` (ranking parity).
5. **Assemble under a split budget** (§6.3) and inject as **message data, never
   `AIContext.Instructions`** (V1 invariant). `<okf-context>` fences are
   readability markers, not a security boundary.

### 6.2 WRITE — two distinct paths

- **Memory capture (automatic):** `StoreAIContextAsync` → deterministic capture
  (last user message + final response, **no LLM**, blockquote neutralization as
  V1) → `memoryStore.WriteAsync(scope, entry, tier)`. Reuses producer
  validation + per-path lock + reparse guards (Lot 1 machinery). **Exactly one
  targeted tier** (default = user; see §9).
- **Deliberate authoring:** `okf_write_concept` & co. remain `OkfBundleTools`
  targeting a **configured bundle root** (shared, tool-gated) — unchanged. That
  root is typically also registered as a `role:knowledge` source, so authored
  concepts become searchable via the resolver. **The two write paths never
  merge**: capture ⟶ `IMemoryStore` (scoped); authoring ⟶ bundle (shared).

**Invariant:** `scope` flows through READ *and* WRITE, always from the delegate
(host), never from a message — a tenant-A user physically cannot address
tenant B's path.

### 6.3 Split budget

The single `TokenBudget` is partitioned into a **knowledge share** and a
**memory share**, each a configurable floor with sensible defaults. Unused
capacity in an under-filled surface **spills over** to the other (guarantees no
starvation *and* wastes no budget). Within the memory share, tiers fill
most-specific-first (session → user → tenant). The estimate remains the crude
chars/4 soft budget of V1.

## 7. RGPD / deletion / audit

`IMemoryStore` exposes, implemented for the user tier this lot:

- `DeleteScopeAsync(scope, tier?)` — delete a scope's memory subtree (`tier`
  null ⇒ all applicable tiers). A subtree removal, trivial thanks to readable
  prefixes; guarded by the same reparse checks as writes.
- `EnumerateAsync(scope)` — list what is stored for a scope (audit / data
  subject access request).

## 8. Security invariants & error handling

- **Scope only from the delegate** (host-authenticated), never from a message.
  Tenant isolation is *structural* (path nesting).
- READ = union of the **scope's applicable tiers** only; WRITE = a **single
  tier**, via producer validation + per-path lock + reparse guards.
- Scope segments validated (`ConceptId.ValidateSegment`) ⇒ no path traversal.
- **The provider never throws toward the invocation pipeline** (V1 invariant):
  a memory read/write failure degrades gracefully (errors-as-data / logged);
  context is still injected best-effort. Injection is always message data,
  never `Instructions`.

## 9. Options evolution (`OkfContextProviderOptions`)

- `ScopeAccessor` — `Func<InvokingContext, KnowledgeAccessScope>` (the host
  supplies the authenticated scope per invocation). Absent ⇒ "local".
- `CaptureTier` — which tier capture writes to; **default = user**.
- Knowledge/memory **budget shares** (with defaults) for §6.3.
- `MemoryCapture` (enum `MemoryCaptureMode { Disabled, Enabled }`) stays
  `Disabled` by default; when `Enabled` it writes to the scoped `IMemoryStore`.
  (The pre-1.0 V1 value `SharedBundle` is **renamed to `Enabled`** — it read
  wrong in scoped mode; the single unscoped bundle becomes the degenerate
  "local"-scope case of one user-tier memory source.) When a `ScopeAccessor`
  **is** configured but the capture's scope cannot be correlated (no session, or
  no prior context-provide), the capture is **skipped** (recorded in
  `LastMemoryError`) rather than misfiled into the `_local` subtree; with no
  `ScopeAccessor`, capture is local.
- `MemoryDirectory` (single-bundle) is **deprecated** in favour of `role:memory`
  catalog sources.

## 10. Testing strategy

- **Scope isolation (the crux):** a tenant-A scope can **neither read, write,
  nor delete** tenant B's memory.
- **Path derivation:** readable prefixes, `_local` sentinel, and segment
  validation rejecting `..` / `/` / NUL.
- **`FileMemoryStore` user tier:** read / write / delete / enumerate round-trip.
- **Manifest parser:** `role:memory` + `tier` accepted; missing/invalid `tier`
  rejected with the new diagnostic code (never throws).
- **Adapter / provider E2E:** scripted zero-network `ChatClientAgent` (extends
  the V1 test) — scope via delegate → split-budget READ → user-tier WRITE →
  never-throw → injection-as-message-not-instructions.

## 11. Staging (build order)

1. **Core write primitive (§4.1):** promote the atomic append-to-concept +
   per-path lock registry from `OkfBundleTools` (Agents) into core `OKF4net`;
   refactor `OkfBundleTools` to consume it (behaviour unchanged, tests still
   green). This unblocks `FileMemoryStore` reusing it from `Catalog`.
2. **Contracts:** `KnowledgeAccessScope`; `SourceRole.Memory` + `tier` parsing;
   `IMemoryStore` (incl. `DeleteScopeAsync`/`EnumerateAsync`); `MemoryPath.For`.
3. **`FileMemoryStore`** — user tier (read/write/delete/enumerate), on the core
   write primitive.
4. **Adapter:** `OkfContextProvider` evolves to consume resolver + memory store
   + scope delegate; split-budget READ; scoped capture WRITE.
5. **Wiring / DI:** `Agents` references `Catalog`; options gain `ScopeAccessor`
   + `CaptureTier` + budget shares.

## 12. Deferred / non-goals

- **Session & tenant tier storage** — contracts and parsing only this lot.
- **Multi-source fusion** of the resolver — separate spec.
- **Hashed/opaque scope keys** — readable prefixes only; isolated in
  `MemoryPath.For` so a later switch is a one-point change.
- **"Remember for the team" promotion** (explicit user→tenant promotion) — a
  later refinement.

## 13. How this resolves the notes' open decisions

- **Scope-key storage form** → readable path prefixes, derivation isolated
  (§5.2).
- **Scope injection (a vs b)** → per-invocation delegate accessor; (a) is the
  degenerate "local" case of (b) (§4, §9).
- **Memory sink: one source vs per tier** → one source per tier (§5.3).
- **Deletion/audit APIs** → in the `IMemoryStore` contract, user-tier
  implemented (§7).
- **Retention of ephemeral session memory** → deferred with the session tier
  (§12).
