# External Review Briefing — Attestation Containers Design (pre-implementation)

You are an external reviewer with no prior context. This document is
self-contained: it tells you what to review, how to verify claims
independently, which decisions are deliberate (do not re-flag), and which
open questions are already known (do not re-report as new discoveries).
Everything else is fair game — the goal of this review is **fresh eyes**,
before any code gets written against this design.

## 1. What this project is

**OKF4net** is a zero-third-party-dependency .NET 10 implementation of the
[Open Knowledge Format (OKF) v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md).
It has a §10 "Attested Computation" subsystem (`src/OKF4net.Attestation/`):
host-plugged contracts (`IParameterBinder`, `IComputationExecutor`,
`IAttester`, grouped as `IAttestationRuntime`, resolved per bundle concept's
`runtime` field via `IAttestationRuntimeRegistry`) and an
`AttestationOrchestrator` that drives bind → execute → attest → gate. **No
concrete implementation of these contracts exists anywhere in the repo yet**
— it's a deliberate extension point.

The document under review proposes the missing piece: a new project,
`src/OKF4net.Attestation.Containers/` (**not yet written — this is a
pre-implementation design review, there is nothing to build or run**), that
implements these contracts by shelling out to a container engine
(Docker/Podman/nerdctl) to execute the *actual* sanctioned scripts/queries a
bundle references, instead of reimplementing their logic in C#. That
constraint — never port a sanctioned script's logic to C#, always invoke the
real thing — is a hard, previously-established rule for this subsystem: §10's
trust model rests on the receipt having been produced by the actual
referenced script, not somebody's C# reading of it.

## 2. What to review, and how to verify claims

The design document: `docs/superpowers/specs/2026-09-07-attestation-containers-design.md`.
Read it in full before anything else.

There is no code to build or test. Verify the design's claims against the
existing source it must fit into:

- `src/OKF4net.Attestation/Contracts.cs`, `Values.cs`,
  `AttestationOrchestrator.cs` — the contracts and orchestrator this design
  must implement/fit (already implemented and reviewed; out of scope for
  critique except where the new design would misuse them).
- `bundles/acme_retail/attesters/sql_equality.py` — a real attester script:
  a Python module exposing `attest(*, sanctioned_sql, receipt, claimed_value)`,
  meant to be imported, not run standalone.
- `bundles/acme_retail/computations/gross-margin-period.md` and
  `revenue-ytd.md` — real "Attested Computation" concepts (frontmatter shape:
  `runtime`, `parameters`, `executor.resource`/`executor.receipt`,
  `attester.resource`).
- `docs/superpowers/specs/2026-07-29-okf-attested-computation-design.md` —
  design of the existing §10 subsystem this work extends.
- `docs/spec/SPEC.md` §10 — the vendored OKF v0.2 spec text itself, to check
  whether the design invents behavior the spec should own, versus correctly
  treating execution mechanics as host-defined.
- `CLAUDE.md` (repo root) — project conventions: the zero-third-party-dependency
  rule (enforced per project), the "never port a sanctioned script" rule, and
  the precedent of `producers/` being deliberately excluded from CI.

## 3. Review criteria

1. **Feasibility** — do the described mechanisms (stdin-only I/O for
   everything, the Python attester bootstrap via `python -c`, the SQL client
   wrapper) actually work as claimed? Any concrete blocker (quoting/escaping
   across `Process.Start` on Windows vs. Linux, temp-file lifecycle inside a
   `--rm` container, `importlib` quirks)?
2. **Security** — this system executes bundle-authored code inside containers
   reached via a generic CLI shim. Scrutinize: injection risk in how
   stdin/args/env vars get shelled out (could a bundle-controlled string ever
   land in a place `Process.Start` treats as a shell command line rather than
   a genuinely separate argument/stream?); secrets-in-env-vars exposure;
   resource exhaustion (unbounded stdout, timeout/cancellation actually
   killing the remote container, not just the local client process); whether
   "the container is the security boundary" claim holds given what's
   described.
3. **Fit with existing contracts** — does the proposal actually satisfy
   `IParameterBinder`/`IComputationExecutor`/`IAttester`, including the
   orchestrator's errors-as-data model (exceptions from binder/executor/attester
   are caught upstream, never propagated)?
4. **Spec fidelity** — §10 intentionally leaves execution mechanics to the
   host (`executor.resource`/`attester.resource` are just §6.2-resolved
   paths, no invocation semantics defined). Verify the design doesn't
   overstep into spec territory, and correctly treats its own implementation
   choices (e.g., the attester kwarg-naming convention) as such rather than
   as spec requirements.
5. **Scope discipline (YAGNI)** — anything over-built for a v1? Anything
   pushed to "future work" that actually shouldn't be deferred?
6. **Internal consistency** — does any section's claim get undercut by
   another section (e.g., the "no volume mounts anywhere" principle — does it
   actually hold across all three execution protocols)?
7. **The SQL-text-must-not-be-rewritten invariant** — the SqlClient executor
   must run the bound SQL exactly as sanctioned (rewriting it, e.g. wrapping
   it in `json_agg(...)` to force JSON output, would break an attester like
   `sql_equality.py` that compares canonicalized `executed_sql` against the
   sanctioned text). Judge whether the design's chosen mechanism (off-the-shelf
   client images + a thin wrapper) can actually honor this invariant while
   still producing structured JSON output, or whether it's hand-waved.

## 4. Deliberate decisions — do not re-flag these as gaps

| # | Decision | Why |
|---|---|---|
| 1 | One `CliContainerEngine(binaryName)` class, not separate Docker/Podman/nerdctl classes | Their `run` CLI surface is compatible (Podman and nerdctl were both designed as Docker-CLI-compatible); one parameterized class covers all three without duplicating near-identical code. |
| 2 | SqlClient runtimes are clients connecting to an externally-configured database (connection string via host config) — never an auto-provisioned ephemeral database | Mirrors the existing `runtime: bigquery` example in `bundles/acme_retail`, which targets a real external service, not a simulated one. |
| 3 | No container volume mounts anywhere — script text, SQL text, and attester module source all travel over stdin | Deliberate cross-platform simplification: avoids Docker Desktop bind-mount path-translation issues on Windows. |
| 4 | SqlClient executor uses off-the-shelf official images (e.g. `postgres:16-alpine`) with a minimal wrapper, not custom OKF4net-built-and-published images | Avoids taking on a new image-publishing/maintenance channel (comparable in weight to the existing NuGet/winget distribution channels). The design explicitly flags the JSON-formatting robustness of this choice as unresolved, deferred to an implementation-time spike — don't re-flag that as a newly discovered gap, it's already called out in the doc. |
| 5 | The attester invocation convention (fixed kwarg names, e.g. `sanctioned_computation`/`receipt`/`values`) is a deliberate implementation choice of this new project — confirmed **not** an OKF v0.2 spec matter, since §10 leaves execution entirely to the host | `bundles/acme_retail/attesters/sql_equality.py`'s actual kwargs (`sanctioned_sql`, `receipt`, `claimed_value`) do not match this convention **on purpose**. The design keeps that bundle untouched (verbatim Apache-2.0 upstream copy) and defers wiring it up until after the convention is validated on a new, purpose-written example bundle. Don't flag the mismatch as an oversight — you may still critique whether the convention itself is well-chosen. |
| 6 | Container-integration tests (real shell to `docker`/`podman`) are deliberately excluded from CI | Mirrors the existing `producers/` precedent (`CLAUDE.md`): a documented local-only command is the guarantee, not a CI job. Explicit decision, not an omission. |
| 7 | Kubernetes/cloud execution engines, custom OKF4net-published images, non-Python attester bootstraps, and a stronger secrets mechanism than plain `-e` env vars are all out of scope for v1 | See the design's "Travaux futurs" section. Flag one of these only if you believe it's *not actually safe to defer*, not merely "missing." |

## 5. Known open questions the design already flags — do not report as new

- Whether official/off-the-shelf SQL client images can reliably format query
  results as JSON without rewriting the sanctioned SQL text (explicitly
  flagged as needing an implementation-time spike).
- The exact validation bundle/sample project names
  (`bundles/attestation_containers_demo/`, `samples/attestation-containers-demo/`)
  are fixed in the doc but nothing under those paths exists yet.

## 6. Requested output format

Findings ranked **Critical / Important / Minor**, each with: the design-doc
section it concerns, the concrete failure scenario or chain of reasoning, and
— where applicable — which existing file/line grounds the claim. Separate
explicitly: (a) problems that block writing an implementation plan as-is,
(b) problems addressable during implementation without revising this design,
(c) non-blocking suggestions. Do not re-report anything in §4 or §5 unless
you've found a genuinely new consequence not already described there. This is
a paper review — no code exists to run; reasoning grounded in the referenced
source files is the expected form of evidence, so cite what you checked.
