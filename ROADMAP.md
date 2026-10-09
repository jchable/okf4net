# OKF4net Roadmap

OKF4net implements the [Open Knowledge Format (OKF) v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
on the .NET base class library with zero third-party runtime dependencies.
This file keeps the context behind shipped work and settled decisions. Planned
work — features, bugs, design lots and open questions — is tracked as GitHub
issues, grouped by milestone: [`v0.7.0`](https://github.com/jchable/okf4net/milestone/1)
for the next release and [`v-next`](https://github.com/jchable/okf4net/milestone/2)
for everything not yet scheduled. Issues labelled
[`good first issue`](https://github.com/jchable/okf4net/labels/good%20first%20issue)
and [`help wanted`](https://github.com/jchable/okf4net/labels/help%20wanted)
are the concrete entry points.

## Shipped, with notes

- **`okf audit` shipped** — a corpus-level query over a bundle's trust (§5.3),
  lifecycle (§5.4) and staleness (§5.5) signals: counts plus a filterable
  worklist, across the CLI verb and the read-only `okf_audit` agent tool,
  backed by the shared `ConceptAudit`/`AuditVocabulary` model in `OKF4net`.
  Motivated by ["OKF v0.2 Quietly Admits the Folder Has a Ceiling"](https://medium.com/@davidroliver/okf-v0-2-quietly-admits-the-folder-has-a-ceiling-the-way-up-is-a-library-25fa54e872f9)
  — see [its design spec](docs/superpowers/specs/2026-08-21-okf-audit-design.md).
- **`okf verify` shipped** — the verb that answers what `okf audit` asks
  about trust: it records a review by adding, or from the same actor
  replacing, a `{by, at}` entry in a named concept's `verified` list (§5.2),
  so — for a `human:` actor — the concept clears audit's trust-filtered
  selection at the next pass. A `process:` or `<producer>/<version>` actor is accepted
  symmetrically (§7) but only moves the concept from `unverified` to
  `machine-confirmed`, which `--trust unverified,machine-confirmed` still
  selects. Verification only moves the trust dimension (§5.3) — `stale_after` is
  untouched, so a just-reviewed concept can still appear in `okf audit`'s
  *default* (staleness-only) worklist. `<id>…` accepts `-` to
  read ids from standard input, so `okf audit … --trust unverified | cut
  -d' ' -f1 | okf verify … --by human:ada -` closes the loop in one line.
  Backed by the new `BundleConceptWriter.RecordVerifications` — the single
  governed writer of `verified` — and exposed to agents as `okf_verify`. See
  [its design spec](docs/superpowers/specs/2026-08-28-okf-verify-design.md).
- Performance baselines for large bundle loads: **a `Bundle.Load` baseline
  exists** (`BundleLoadPerformanceTests`, a 2,001-concept synthetic bundle,
  [#6](https://github.com/jchable/okf4net/issues/6)) — it prints its timings
  and gates only on a pathology ceiling, not on a target.
- Bundle viewer: **interactive viewer shipped** (on `dev`, in the next
  `okf-render` release) as the standalone `okf-render` binary
  (`OKF4net.Render`, over `OKF4net.Viewer`) — split out of `okf` itself so
  the CI-facing validator does not carry the viewer's JavaScript. A generated
  site opens straight from disk and has a tree explorer, a "Jump to" palette, a
  theme toggle, a contents panel, a local graph for each concept (1–2 hops,
  enlargeable, with a resizable side column) and a global graph page
  (`graph.html`: deterministic layout computed in the browser, facets, a
  detail drawer, keyboard and text equivalents) — see the
  [interactive viewer design](docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md)
  (the global graph is [#162](https://github.com/jchable/okf4net/issues/162),
  re-scoped into `okf-render` by design decision A4).
  The live-server half of [#40](https://github.com/jchable/okf4net/issues/40)
  was **dropped, and the issue closed**. What a static site cannot do is
  always-fresh viewing and full-text search: both are pursued as a VS Code
  extension ([#163](https://github.com/jchable/okf4net/issues/163)), which
  reaches them from inside the editor without a local HTTP server (an
  extension host is a process, so it can have the .NET side run
  `ConceptSearch` rather than mirroring its weights in JavaScript — the
  viewer's "Jump to" palette is deliberately not search: it matches titles,
  ids and tags only).
  - **The client-side XSS defense is guarded by a JS harness, not by xunit.**
    xunit runs on .NET and cannot execute JavaScript, so
    `tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs` only smoke-checks for
    source-text markers — it stays green even if the sanitizer is gutted.
    `tools/viewer-security-check/` (Node/jsdom, run against the real vendored
    marked) is the actual guard, and CI runs it as the `viewer sanitizer (JS)`
    job. Re-vendoring `marked.min.js` is the change most likely to regress the
    defense, and that job is what catches it.

## Later

- Ecosystem integrations driven by user demand.
- Tracking upstream OKF spec evolution beyond v0.2.

## `producers/OkfProducer`

- **`producers/OkfProducer` shipped** (repo scanner → OKF v0.2 bundle generator, `generate`/
  `validate` commands, npm/NuGet/README detection, and a C# code-graph stage: one concept per
  namespace, type and member, with resolved `## Calls` links — see
  [its design spec](docs/superpowers/specs/2026-07-31-okf-producer-design.md), the
  [core plan](docs/superpowers/plans/2026-07-31-okf-producer-core.md) and the
  [code-graph design](docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md)).
  Two things a reader should not mistake for open questions:
  - **No CI coverage — decided, not pending.** On 2026-08-01 it was settled that `producers/` does
    **not** go into CI: it stays outside `OKF4net.sln`/`ci.yml`. Two consequences, both accepted.
    The guarantee is local and it is one command, stated at the top of
    [`producers/README.md`](producers/README.md): `dotnet test producers/OkfProducer.sln`, run
    before touching the producer and after any public `OKF4net` API change. And the per-RID
    packaging smoke test cannot be a guarantee without CI, so it is a **documented manual step at
    release time**, described as such rather than implied to be covered.
  - **Documented.** [`producers/README.md`](producers/README.md) carries the flag surface, the
    verification command, the packaging step and the project layout.

  Fixed:
  - **`LiftedMarkdown`'s escaping rules are a copy of a scanner that has since been rewritten.**
    Fixed (#111): `LiftedMarkdown` still *prefers* its own rendering of lifted text, but it now
    asks `LinkScanner.ExtractLinks` on what it is about to emit and falls back to an encoded form
    when the scanner disagrees, so the rules can drift without the output becoming unsafe.
    `LiftedMarkdownAgreementTests` is the executable guard — against the real scanner, over a fixed
    payload list and a seeded sweep, and over the whole generated bundle. Measured before the fix,
    on 40,000 random strings built from the characters the scanner turns on (an adversarial
    alphabet, not typical text): about 7% either manufactured a link or hid the producer's own (an
    unclosed `<!--` swallowed `## Contains`), the ticket's payload ``` `` a ` b `` [x](y) ```
    among them. This repository's own bundle (763 concepts) is byte-identical before and after.
    What remains is a limit of the method, not a gap in it: the check runs in a frame shaped like
    the generated body, so a context the generator adds later has to be added to the frame too.
- **Known limitation: without `--repo-url`, `packages/` and `docs/` `resource` paths don't resolve
  against the bundle.** `producers/OkfProducer` records those families' `resource` relative to the
  *scanned repository* (e.g. `src/OKF4net/OKF4net.csproj`), which is the semantically correct
  provenance reference — but that path names a file in the *repository*, not in the *bundle*, so
  `BundleValidator` looks for `<bundle>/src/OKF4net/…` and it misses by construction: one "path
  not found" warning apiece, 10 on this repository. (Until the §6.2 fix of 2026-09-11 this entry
  blamed a different mechanism — a bare relative `resource` resolving against the concept's own
  directory, giving `<bundle>/packages/src/OKF4net/…`. That resolution rule was itself the bug and
  is gone; the base moved, the miss did not.)
  **`--repo-url` removes all 10**: those families build the same forge URL the `code/` family does,
  and a URL short-circuits the validator's path classifier. The original entry here recorded 20
  warnings and framed the only alternative as embedding copies of referenced files in the bundle;
  both were wrong. Half the 20 came from a one-entry `sources` block repeating `resource` verbatim
  (deleted — §4.5 already forbade it by name), and the other half needed no scope change at all,
  only passing `GenerateOptions` to two builders that had never taken it. What remains is the
  no-`--repo-url` fallback, kept deliberately: omitting the field costs the same 10 warnings
  (measured, 2026-09-03) and the path is the only pointer those concepts have to their own subject.
  See `producers/README.md`, "What `--repo-url` changes".
- **Fixed (#112): `dotnet exec <dll>` queried a different `dotnet` than every other way of
  launching the producer.** Reported 2026-09-15 as silent degradation of an SDK-8-pinned
  sub-project (exit `0`, only a `note: … not compiled (MsBuildQueryFailed)`), with every
  `DOTNET_*`/`MSBuild*` variable and `PATH` identical between the clean and the degraded run. They
  were identical because the difference is not in the environment: `Process.Start("dotnet")`
  searches the *running application's* directory before `PATH`, and under `dotnet exec` the
  application is `dotnet` itself, so the child was the hosting install while the apphost,
  `dotnet run` and an MSBuild `<Exec>` of the apphost read `PATH`. A repository whose
  `global.json` pins an SDK only the `PATH` install has was queried by one that lacks it.
  `MsBuildProjectQuery` now resolves `dotnet` against `PATH` (the same resolver `git` already
  had) and falls back to the hosting install only when `PATH` has none. Reproduced and verified
  2026-10-05 on Windows with a runtime-only second install as the host (SDK 8 itself was not
  installed); see `producers/README.md`.

## Out of scope

- Third-party runtime dependencies in the library or CLI (BCL-only is a hard rule).
- Divergence from the OKF v0.2 spec without a documented, cited reason.

## How to influence the roadmap

Open a [Discussion](https://github.com/jchable/okf4net/discussions) or comment on an
existing issue. Roadmap items graduate to labelled issues before work starts.
