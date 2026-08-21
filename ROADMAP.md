# OKF4net Roadmap

OKF4net implements the [Open Knowledge Format (OKF) v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
on the .NET base class library with zero third-party runtime dependencies.
This roadmap shows where the project is heading. It is a living document —
issues labelled [`good first issue`](https://github.com/jchable/okf4net/labels/good%20first%20issue)
and [`help wanted`](https://github.com/jchable/okf4net/labels/help%20wanted)
are the concrete entry points.

## Now (in progress)

- Broaden test coverage and worked examples for the CLI verbs and the agents layer.
- Documentation: end-to-end tutorials for both audiences (library users, agent builders).

## Next

- More `OKF4net.Agents` samples with Microsoft Agent Framework — the first,
  `samples/acme-retail-agent`, shipped in 0.4.0; more welcome.
- `OKF4net.Catalog` samples: `samples/catalog-explorer` (multi-source
  search, ranking strategies, per-caller visibility, the `role: memory`
  tier) shipped. A natural next one: a read-write "second brain"
  personal-notes sample over `OKF4net.Mcp` in Claude Desktop — the current
  MCP story is read-only-focused; this would exercise write/append and
  `IndexGenerator`/`ChangeLog` (§8/§9) updating live as notes are added.
- Performance baselines for large bundle loads.
- Bundle viewer: **static render shipped** as `okf render` (`OKF4net.Viewer`).
  The live-server half of [#40](https://github.com/jchable/okf4net/issues/40)
  remains open — it is what unlocks full-text search in the viewer, since a
  server can run `ConceptSearch` directly instead of mirroring its weights in
  JavaScript. Its implementation approach (zero-dep `HttpListener`, ASP.NET
  Core, or a standalone web tool) is still open.
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
- **Zero-dependency bundle linter for CI.** A small AOT tool built on just
  `BundleValidator`/`LinkScanner` (no `OKF4net.Cli` dependencies beyond
  what's already zero-dep), packaged for GitHub Actions/pre-commit — a
  docs/DevOps-facing entry point distinct from the agent-builder-facing
  samples, showcasing the zero-dependency story to a different audience.
- **Interactive cross-link graph explorer.** A small web front-end over
  `okf graph`/`IndexGenerator` output, visualizing a bundle's concept
  cross-links — outreach-oriented (contributor/adoption funnel), likely
  outside pure C#/.NET so scoped as its own project rather than a
  `samples/` entry.
- **Dogfooding on a real third-party OSS project's docs.** Convert an
  existing open-source project's markdown docs into an OKF bundle via
  `okf fmt`/`index`/`validate`, as a concrete "here's how you'd actually
  adopt this" walkthrough rather than a synthetic sample bundle.
- **Attested Computation (§10), executed for real.** `samples/acme-retail-agent`
  is deliberately read-only for `Attested Computation` concepts — it
  inspects (`okf_get_computation`) but never runs
  (`okf_run_computation`) `bundles/acme_retail`'s sanctioned SQL, because
  trusting a C# reimplementation of `attesters/sql_equality.py` would
  undermine the whole point of attestation. Actually running one end to
  end needs a sandboxed container-based `IComputationExecutor`/`IAttester`
  runtime, scoped in
  [that sample's design spec](docs/superpowers/specs/2026-07-30-acme-retail-bundle-and-agent-sample-design.md#future-work-a-container-based-execution-runtime)
  — its own design pass before implementation.
- **Open question upstream: concept id character set.** The spec (§2) does not
  restrict which characters a concept id may contain; `ConceptId.ValidateSegment`
  currently restricts to ASCII regardless. Whether to allow full Unicode (any
  alphabet, no transliteration) is an open, deliberately deferred decision —
  raised upstream, see
  [docs/outreach/upstream-issues/2026-07-31-concept-id-character-set-clarification.md](docs/outreach/upstream-issues/2026-07-31-concept-id-character-set-clarification.md).
  Blocks nothing today (the new `ConceptId.Slugify` helper folds non-ASCII to
  `'-'` in the meantime), but revisit once upstream responds — a decision to
  broaden `ValidateSegment` needs its own design pass (cross-platform Unicode
  normalization, golden-fixture impact).

- **`producers/OkfProducer` walking skeleton shipped** (repo scanner → OKF v0.2 bundle generator,
  `generate`/`validate` commands, npm/NuGet/README detection only — see
  [its design spec](docs/superpowers/specs/2026-07-31-okf-producer-design.md) and
  [core plan](docs/superpowers/plans/2026-07-31-okf-producer-core.md)). Two follow-ups noted at
  merge time, not yet acted on:
  - **No CI coverage.** `producers/` is deliberately outside `OKF4net.sln`/`ci.yml`, so nothing
    verifies it still builds after an `src/OKF4net` API change — it can rot silently. Either add a
    lightweight build+test job for `producers/OkfProducer.sln`, or treat "does `producers/` still
    build" as an explicit step whenever a public `OKF4net` API changes.
  - **Undocumented.** Not mentioned in `README.md`/`CLAUDE.md`/`CONTRIBUTING.md`. Add pointers once
    the producer grows past this first walking-skeleton slice (more ecosystems, LLM enrichment).
- **Known limitation: generated `sources[].resource` paths don't resolve against the bundle.**
  `producers/OkfProducer`'s `ConceptGenerator` records `sources[].resource` relative to the
  *scanned repository* (e.g. `package.json`), which is the semantically correct provenance
  reference — but `BundleValidator` resolves `sources[].resource` relative to the *bundle root*,
  so every generated package/doc concept gets a "path not found" warning by construction. Decided
  at merge time: accept the warning rather than embed copies of referenced files in the bundle
  (which would be a larger, unplanned scope change). Revisit only if this becomes a real friction
  point once the producer has actual users.

## Out of scope

- Third-party runtime dependencies in the library or CLI (BCL-only is a hard rule).
- Divergence from the OKF v0.2 spec without a documented, cited reason.

## How to influence the roadmap

Open a [Discussion](https://github.com/jchable/okf4net/discussions) or comment on an
existing issue. Roadmap items graduate to labelled issues before work starts.
