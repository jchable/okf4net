# OKF4net Roadmap

OKF4net implements the [Open Knowledge Format (OKF) v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
on the .NET base class library with zero third-party runtime dependencies.
This file keeps the context behind shipped work and settled decisions. Planned
work — features, bugs, design lots and open questions — is tracked as GitHub
issues, grouped by milestone: [`v0.9.0`](https://github.com/jchable/okf4net/milestone/4)
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
- **End-to-end tutorials shipped** (on `dev`,
  [#174](https://github.com/jchable/okf4net/issues/174), formerly the "Now" item
  "tutorials for both audiences"): [library users](docs/tutorials/library-users.md)
  write, index, load and validate a bundle through the asynchronous provider
  API and check it with `okf`; [agent builders](docs/tutorials/agent-builders.md)
  expose a bundle through `OKF4net.Agents` (a shared `BundleHandle`,
  `OkfToolMode`, `OkfContextProvider`, an approved write) and through
  `okf-mcp`. Every snippet and command was run to produce the outputs they
  show; the agent steps use a scripted `IChatClient`, and the two steps that
  need a model (a real `IChatClient`, an MCP client configuration) say so.
  They link to `samples/` rather than duplicate them.
- More `OKF4net.Agents` samples ([#159](https://github.com/jchable/okf4net/issues/159),
  open: further agent patterns are welcome). Shipped so far:
  [`samples/acme-retail-agent`](samples/acme-retail-agent/README.md) (0.4.0),
  [`samples/agents-quickstart`](samples/agents-quickstart/README.md) and
  **[`samples/agents-handoff`](samples/agents-handoff/README.md)** (on `dev`) —
  a curator agent (`OkfToolMode.ReadWrite`) writes a note that a reader agent
  (`OkfToolMode.ReadOnly` plus `OkfContextProvider`) finds in its injected
  context, because both tool sets share one `BundleHandle` (§5.2); it also
  shows `okf_write_concept`'s read-before-write gate (§5.4) and how a consumer
  with its own `Manual` or `OnAccess` handle sees the change. The last two need
  no model or API key.
- Performance baselines for large bundle loads: **a `Bundle.Load` baseline
  exists** (`BundleLoadPerformanceTests`, a 2,001-concept synthetic bundle,
  [#6](https://github.com/jchable/okf4net/issues/6)) — it prints its timings
  and gates only on a pathology ceiling, not on a target.
- **Read-write MCP sample shipped** ([#160](https://github.com/jchable/okf4net/issues/160)):
  [`samples/second-brain/`](samples/second-brain/README.md) is a starter
  personal-notes bundle served writable by `okf-mcp` in Claude Desktop. It
  includes the Desktop configuration for Windows and macOS, example prompts,
  and `smoke.cs`, which drives `okf-mcp` over stdio on a copy of the bundle
  and checks the disk. The sample states plainly what the MCP write path does
  not do. `okf_write_concept` changes the concept file only, so the §8
  indexes and the §9 log are updated only when the model calls
  `okf_regenerate_indexes` and `okf_append_log`. Nothing makes those calls
  automatically, and the bundle's own guide note asks the model to make them.
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
- **Bundle provider seam shipped (0.8.0)** — a bundle is read from, and
  written to, a key space of byte objects behind `IBundleProvider`
  ([#139](https://github.com/jchable/okf4net/issues/139)), with an asynchronous
  contract ([#152](https://github.com/jchable/okf4net/issues/152)), the
  filesystem as the writable reference implementation
  ([#140](https://github.com/jchable/okf4net/issues/140)) and a bundle shipped
  inside an assembly, `OKF4net.Providers.EmbeddedResources`
  ([#141](https://github.com/jchable/okf4net/issues/141)). Both new packages
  went out on nuget.org with 0.8.0 through Trusted Publishing; the S3 provider
  below is the third backend.
- **S3 bundle provider shipped (0.8.0)** — `OKF4net.Providers.S3`
  ([#142](https://github.com/jchable/okf4net/issues/142), see its
  [design spec](docs/superpowers/specs/2026-11-16-s3-bundle-provider-design.md)):
  a bundle in Amazon S3 or an S3-compatible store, read-only and truly
  asynchronous, in its own package. It is the one backend with a third-party
  dependency (`AWSSDK.S3`, declared as a minimum, `>= 4.1.0`); the core and the
  other providers stay zero-dependency. Three decisions worth knowing: the
  provider builds **its own client and pins every request** (one authority, one
  path prefix, `GET` only), because a 307 between two S3 hostnames would
  otherwise silently change the bucket, and no ambient AWS setting is allowed to
  change what it sends; **credentials are the host's** (the provider never reads
  the environment or a config file, and resolves them once per operation, in
  isolation); and **objects are versioned by the SHA-256 of their bytes**, so
  sanctioned code (§5.1, §5.4) can only run at the version the load surveyed.
  Two core changes ride with it, for every provider: the load reads the bundle
  token *before* listing, and `ResolveResourceKeyAsync` throws instead of
  reporting `Unsafe` for a provider failure other than a refusal (the same
  confusion while validation reads `index.md`/`log.md` is
  [#247](https://github.com/jchable/okf4net/issues/247)).
  - **The guarantee against a real store is local, not CI — decided, not
    pending.** The `Category=S3Integration` tests start RustFS 1.0.3 themselves
    (Podman or Docker) and are excluded from CI like `ContainerIntegration`: run
    `dotnet test tests/OKF4net.Tests --filter Category=S3Integration` and check
    the run reports **0 skipped**, after any change to the package or any bump
    of `AWSSDK.S3`/`AWSSDK.Core`. CI's own guard is the `S3 provider AOT smoke
    test` job (the real packages, Native AOT, at the minimum and the latest SDK).
  - **What was and was not measured.** Tested against RustFS 1.0.3 only (Podman,
    Docker, Docker in WSL) and, for Native AOT, on Windows. MinIO, real AWS S3
    and the Linux/macOS legs of the CI job had not run when this shipped
    ([#292](https://github.com/jchable/okf4net/issues/292),
    [#293](https://github.com/jchable/okf4net/issues/293)). The limits the final
    review found are tickets; the maintainer's decisions of 2026-11-19 are
    implemented and recorded in the spec's Annexe C (an empty truncated listing
    page is followed within a page budget, the guard drops the ambient headers
    and rebuilds the User-Agent from numbers and closed sets, a non-hex
    single-part ETag is an `InvalidResponse`), with residuals in
    [#283](https://github.com/jchable/okf4net/issues/283),
    [#285](https://github.com/jchable/okf4net/issues/285) and
    [#286](https://github.com/jchable/okf4net/issues/286). Still open: a
    `ServiceUrl` source always signs for `us-east-1`
    ([#284](https://github.com/jchable/okf4net/issues/284)), anonymous access is
    unsupported ([#291](https://github.com/jchable/okf4net/issues/291)).

## Later

- Ecosystem integrations driven by user demand.
- Tracking upstream OKF spec evolution beyond v0.2.
- **Bundle providers beyond the filesystem — follow-ups.** The seam, its
  asynchronous contract and the filesystem, embedded-resource and S3 providers
  shipped in 0.8.0 (see "Shipped, with notes"); what follows is tracked as
  issues: a built-in catalog factory for the embedded provider
  (`"provider": "embedded"` in `catalog.json`,
  [#236](https://github.com/jchable/okf4net/issues/236)) and for the S3 one
  ([#239](https://github.com/jchable/okf4net/issues/239)), `okf-mcp` over S3
  ([#238](https://github.com/jchable/okf4net/issues/238)), the S3 follow-ups
  [#283](https://github.com/jchable/okf4net/issues/283) to
  [#292](https://github.com/jchable/okf4net/issues/292), and a per-user OS lock
  closing the cross-process window of the filesystem provider's conditional
  writes, deferred ([#178](https://github.com/jchable/okf4net/issues/178)).

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
