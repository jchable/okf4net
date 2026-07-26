# OKF4net V2 -- Scoped Memory: Design Notes

**Date:** 2026-07-24
**Status:** Design notes only. NOT approved for implementation. Refines the
"Deferred V2" section (§9) of the Local Catalog design spec
([2026-07-24-okf4net-local-catalog-design.md](2026-07-24-okf4net-local-catalog-design.md)).
These notes seed a future Lot 3 spec; the open decision at the end must be
resolved (via the normal brainstorming -> spec flow) before any code.

## Problem

`OkfContextProvider` today reads *and writes* the same bundle. Memory capture
is therefore bundle-global and unscoped: on a bundle shared by several
users/tenants, a scored recall can surface one session's captured exchange in
another. The V1 mitigation is already shipped -- `MemoryCaptureMode.Disabled`
by default, `SharedBundle` as an explicit, security-noted opt-in. V2 is about
letting capture be **enabled** on a multi-user deployment *without* leakage.

## Key reframing: read-only knowledge vs writable memory

The insight that unlocks the whole design is to stop treating "knowledge" and
"memory" as the same bundle:

| | **Knowledge bundle** (enterprise docs) | **Memory bundle** (captured sessions) |
|---|---|---|
| Runtime | read-only | writable |
| Sharing | shared by all (meant to be read) | must be scoped |
| Privacy | none (internal-public) | sensitive |

Read-only knowledge is shared *by design* -- there is no leak to prevent on the
read side. Scope therefore applies **only to the memory sink**, which shrinks
the problem dramatically and removes the temptation to scope the whole
knowledge surface.

This maps directly onto the catalog: a manifest source gains a role, e.g.
`"role": "knowledge"` (read-only, searched by the resolver) vs
`"role": "memory"` (written by capture, scoped).

## Three-tier layered memory

The chosen granularity is layered -- all three tiers, each with its own scope,
retention, and sharing:

| Tier | Scope key | Retention | Sharing |
|---|---|---|---|
| **Session** | session id | ephemeral (end of conversation) | private to the conversation |
| **User** | authenticated user id | durable | private to the user |
| **Tenant** | tenant id | durable | shared within the tenant, isolated between tenants |

- **Read = union of applicable tiers.** Context injection reads the read-only
  knowledge sources (via the resolver) **plus** the applicable memory tiers
  (session then user then tenant), most-specific first.
- **Write = one targeted tier**, per capture policy (default durable tier is
  configured; session memory is always-on ephemeral working memory; an
  explicit "remember for the team" promotion to the tenant tier is a later
  refinement).

Physical layout (scope becomes a path hierarchy; user memory nests under
tenant so cross-tenant collision is impossible by construction):

```
memory-tenant/<tenantId>/<date>.md            <- tenant tier
memory-user/<tenantId>/<userId>/<date>.md     <- user tier
session: in-memory or memory-session/<sid>/   <- session tier, ephemeral
```

## This resolves the a/b question

The earlier open fork was: (a) scope fixed at provider construction vs
(b) scope derived per-invocation from the `AgentSession`. Because session,
user, and tenant keys all come from the **authenticated request context per
invocation**, the layered model requires **(b)**: a session key cannot be
frozen into a constructor. **(a) becomes the degenerate single-scope case of
(b)** -- a single-user desktop/CLI where tenant = user = "local" and no session
stamping is needed. So there is one mechanism, not two competing strategies:

> The host injects an opaque, authenticated `KnowledgeAccessScope`
> (tenant, subject, ...) per invocation via `ProviderSessionState<T>`. The
> scope is **never** derived from an agent message (that would be
> user-influenceable and would enable cross-scope read/poison). A user in
> tenant A physically cannot address tenant B's path because the key is not
> theirs.

## Interaction with the E2 concurrent-capture race

Scoping largely dissolves E2 (same-day capture lost-update): session memory has
a single sequential writer (no race); user/tenant memory only contends for the
same user's *truly concurrent* turns, which the intra-process write lock (the
V1 E2 fix, Lot 1) already covers.

## Open decision (must be resolved before implementation)

**Scope-key storage form.** Readable path prefixes
(`memory-user/<tenant>/<user>/`) make RGPD deletion and audit trivial (delete a
subtree) but expose tenant/user ids on disk. Opaque hashed keys hide the ids
but require an index to enumerate/delete. For enterprise deployments with
ACL'd storage, readable prefixes are simpler and usually sufficient; the host
could choose. This is the main thing the Lot 3 spec must settle, together with:
retention/expiry of ephemeral session memory, deletion/audit APIs for durable
tiers, and whether the memory sink is one catalog source or one per tier.

## Staging recommendation (for the Lot 3 spec)

Design the contracts now (`KnowledgeAccessScope`, `role: memory` + `scope`
catalog source, per-invocation scope via `ProviderSessionState`) so nothing is
cornered, but stage the build:

1. **User tier (durable)** first -- highest value: the assistant that remembers
   a user across sessions.
2. **Session tier** -- nearly free (can be pure in-memory), add as an increment.
3. **Tenant tier** -- user tier plus a shared prefix; last.

## Relation to the convergence roadmap

This assumes the catalog is the substrate: the read-only knowledge sources are
the catalog's `knowledge` sources searched via `IKnowledgeResolver`; the
memory tiers are `memory` sources. A future
`IKnowledgeResolver -> AIContextProvider` adapter lets the agent-side provider
consume both, rather than the provider owning its own single-bundle path.
See the convergence note to be added to the catalog design spec.
