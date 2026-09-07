# External Review Briefing — Attestation Containers Design (pre-implementation, round 2)

You are an external reviewer with no prior context. This document is
self-contained: it tells you what to review, how to verify claims
independently, which decisions are deliberate (do not re-flag), which fixes
were already made in a prior review round (verify them, don't just restate
the problem they solved), and which open questions are already known (do not
re-report as new discoveries). Everything else is fair game — the goal is
**fresh eyes**, before any code gets written against this design.

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

**This is round 2.** A first adversarial review already ran against an
earlier version of this same document and found a critical defect: a single
generic parameter binder was substituting values directly into the
computation's text (SQL or script source). That's fixed now — see §4 for
what changed and why. This round is a fresh pass over the *current* document,
with extra scrutiny on whether that fix (and the smaller fixes bundled with
it) actually holds up, plus anything round 1 didn't cover.

## 2. What to review, and how to verify claims

The design document: `docs/superpowers/specs/2026-09-07-attestation-containers-design.md`.
Read it in full before anything else — including its "Révision" note at the
top, which summarizes what round 1 changed.

There is no code to build or test. Verify the design's claims against the
existing source it must fit into:

- `src/OKF4net.Attestation/Contracts.cs`, `Values.cs`,
  `AttestationOrchestrator.cs` — the contracts and orchestrator this design
  must implement/fit (already implemented and reviewed; out of scope for
  critique except where the new design would misuse them).
- `bundles/acme_retail/attesters/sql_equality.py` — a real attester script:
  a Python module exposing `attest(*, sanctioned_sql, receipt, claimed_value)`,
  meant to be imported, not run standalone. Its docstring states named bind
  variables (`@name`) are compared **symbolically** — the executor is
  "trusted to bind," meaning `receipt.executed_sql` is expected to still
  contain the placeholder, never a substituted literal value.
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

1. **Does the binder fix actually work?** The design now says
   `ScriptParameterBinder`/`SqlClientParameterBinder` never touch
   `BoundComputation.BoundText` — it stays byte-identical to the sanctioned
   computation text, placeholders included — and values travel only via
   `BoundComputation.Values`, to be relayed by the executor through each
   target's *native* parameter-binding mechanism (a JSON env var for scripts;
   the SQL client's own bind-parameter flag for `SqlClient`). Verify this
   actually closes the injection/provenance-comparison problem it's meant to
   close, for both kinds — and that nothing else in the document
   (particularly the SqlClient wrapper description) quietly reintroduces
   textual substitution.
2. **Is the SqlClient wrapper's job actually achievable?** It must now do two
   things at once without rewriting the sanctioned SQL text: (a) bind
   parameter values through the target client's native mechanism, and (b)
   format the result as JSON. The design flags both as needing an
   implementation-time spike rather than claiming they're solved — judge
   whether that's an honest deferral or whether it's actually a blocker that
   should change the design now (e.g., if no realistic combination of
   client/image/version can do both without a rewrite).
3. **Security, beyond what round 1 already fixed.** Round 1 added: an
   explicit `ProcessStartInfo.ArgumentList`-only / no-shell-string
   constraint, `--memory`/`--cpus`/`--pids-limit` resource ceilings, and a
   caller-independent timeout. Check these are coherent and sufficient (e.g.,
   do the stated resource-limit knobs have sane default *values*, not just
   named flags? does the timeout mechanism interact correctly with the
   already-specified `--rm` + explicit `kill` cancellation path?), and look
   for anything security-relevant round 1 didn't touch.
4. **Fit with existing contracts** — does the proposal actually satisfy
   `IParameterBinder`/`IComputationExecutor`/`IAttester`, including the
   orchestrator's errors-as-data model (exceptions from binder/executor/attester
   are caught upstream, never propagated)?
5. **Spec fidelity** — §10 intentionally leaves execution mechanics to the
   host (`executor.resource`/`attester.resource` are just §6.2-resolved
   paths, no invocation semantics defined). Verify the design doesn't
   overstep into spec territory, and correctly treats its own implementation
   choices (e.g., the attester kwarg-naming convention, the choice to ignore
   `executor.resource`) as such rather than as spec requirements.
6. **Scope discipline (YAGNI)** — anything over-built for a v1? Anything
   pushed to "future work" that actually shouldn't be deferred?
7. **Internal consistency** — does any section's claim get undercut by
   another section (e.g., the "no volume mounts anywhere" principle, or the
   "container is the security boundary" claim now that `SqlClient` is
   explicitly exempted from `--network=none`)?

## 4. What round 1 already fixed — verify, don't re-litigate from scratch

| # | Fix | Why it was needed |
|---|---|---|
| 1 | Two parameter binders (`ScriptParameterBinder`, `SqlClientParameterBinder`), neither of which ever edits `BoundText` | The original single generic "text substitution" binder would inline literal values into SQL text, making `receipt.executed_sql` permanently mismatch the sanctioned SQL's canonicalized form in `sql_equality.py`-style comparisons (which expect `@name` placeholders to survive verbatim) — i.e. attestation would fail on every real run of the bundle this design is motivated by. It was also a textbook injection vector (SQL and Python have incompatible escaping rules; a naive value like an unquoted date isn't even valid Python source). |
| 2 | Explicit constraint: `CliContainerEngine` builds commands via `ProcessStartInfo.ArgumentList` only, never a concatenated shell string or `UseShellExecute = true`; the in-container `SqlClient` wrapper must pipe stdin straight into the client's native query-reading mode, never capture it into a shell variable for reinterpolation | This was previously implicit — the whole "no bundle string reaches a shell" property depends on it, and it's an easy thing to violate later while writing the wrapper. |
| 3 | `ContainerRuntimeProfile` now specifies `--memory`/`--cpus`/`--pids-limit` defaults, applied by `CliContainerEngine` | `--rm` alone caps nothing; a buggy or hostile script/query could exhaust host CPU/RAM/process table from inside a container the design otherwise calls "the security boundary." |
| 4 | A default wall-clock timeout on `ContainerRuntimeProfile`, enforced by `CliContainerEngine` independently of the caller's `CancellationToken` | A caller that never cancels (fire-and-forget agent call, abandoned UI) previously had no mechanism stopping a hung container. |
| 5 | Explicit note that this host does not read or execute `executor.resource` (§10.2) — it runs the `computation` text directly | Previously unstated; `executor.resource` in the reference `bigquery` example points to an agent-facing skill doc, not code — a bundle author relying on that field being followed would be silently ignored otherwise. |
| 6 | Explicit correction to the acme_retail-wiring plan: `sql_equality.py`'s `claimed_value` parameter has no counterpart anywhere in `AttestationContext` (`Contract`/`Computation`/`Bound`/`Values`/`Receipt`) — nothing has been "claimed" yet at the point `IAttester` is invoked (`Displayable` is computed *after*). The script's own docstring ("safe to run consumer-side") suggests it's meant to be called by a downstream consumer, not plugged into `IAttester` directly | The design previously implied wiring `acme_retail` onto this host was mostly a kwarg-renaming exercise. It isn't — the validation bundle's attester must be written against `AttestationContext`'s actual shape, and real `acme_retail` wiring needs a decision about where the "displayed value matches receipt" check lives before it's just a renaming exercise. |

Items 1–4 above are also still open as an *implementation-time* concern in
one sense: the SqlClient spike (see §5) must land on a mechanism per
SQL client that actually satisfies fix #1's invariant (no rewriting) while
still binding parameters and producing JSON. Flag it only if you see a
reason it's unachievable in principle, not merely unresolved in this
document.

The following decisions from round 1 are unchanged and still apply — don't
re-flag them either:

| # | Decision | Why |
|---|---|---|
| 7 | One `CliContainerEngine(binaryName)` class, not separate Docker/Podman/nerdctl classes | Their `run` CLI surface is compatible (Podman and nerdctl were both designed as Docker-CLI-compatible); one parameterized class covers all three without duplicating near-identical code. |
| 8 | SqlClient runtimes are clients connecting to an externally-configured database (connection string via host config) — never an auto-provisioned ephemeral database | Mirrors the existing `runtime: bigquery` example in `bundles/acme_retail`, which targets a real external service, not a simulated one. |
| 9 | No container volume mounts anywhere — script text, SQL text, and attester module source all travel over stdin | Deliberate cross-platform simplification: avoids Docker Desktop bind-mount path-translation issues on Windows. |
| 10 | SqlClient executor uses off-the-shelf official images (e.g. `postgres:16-alpine`) with a minimal wrapper, not custom OKF4net-built-and-published images | Avoids taking on a new image-publishing/maintenance channel. |
| 11 | The attester invocation convention (fixed kwarg names, e.g. `sanctioned_computation`/`receipt`/`values`) is a deliberate implementation choice, confirmed **not** an OKF v0.2 spec matter | §10 leaves execution entirely to the host. `bundles/acme_retail` is kept untouched/verbatim on purpose; the convention is validated on a new, purpose-written example bundle instead — see fix #6 above for why that bundle can't just copy `sql_equality.py`'s signature. |
| 12 | Container-integration tests (real shell to `docker`/`podman`) are deliberately excluded from CI | Mirrors the existing `producers/` precedent (`CLAUDE.md`): a documented local-only command is the guarantee, not a CI job. |
| 13 | Kubernetes/cloud execution engines, custom OKF4net-published images, non-Python attester bootstraps, and a stronger secrets mechanism than plain `-e` env vars are all out of scope for v1 | See the design's "Travaux futurs" section. Flag one only if you believe it's *not actually safe to defer*. |

## 5. Known open questions the design already flags — do not report as new

- Whether official/off-the-shelf SQL client images can, per client and
  version, both (a) bind parameter values through a native mechanism and
  (b) format query results as JSON — without rewriting the sanctioned SQL
  text. Explicitly flagged as needing an implementation-time spike; the
  design also recommends pinning images by digest rather than floating tag.
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
you've found a genuinely new consequence not already described there — in
particular, do not re-report the original text-substitution binder defect as
if it were still present; verify the fix instead. This is a paper review —
no code exists to run; reasoning grounded in the referenced source files is
the expected form of evidence, so cite what you checked.
