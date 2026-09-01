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

- **`okf audit` shipped** — a corpus-level query over a bundle's trust (§5.3),
  lifecycle (§5.4) and staleness (§5.5) signals: counts plus a filterable
  worklist, across the CLI verb and the read-only `okf_audit` agent tool,
  backed by the shared `ConceptAudit`/`AuditVocabulary` model in `OKF4net`.
  Motivated by ["OKF v0.2 Quietly Admits the Folder Has a Ceiling"](https://medium.com/@davidroliver/okf-v0-2-quietly-admits-the-folder-has-a-ceiling-the-way-up-is-a-library-25fa54e872f9)
  — see [its design spec](docs/superpowers/specs/2026-08-21-okf-audit-design.md).
- **Per-verb `--help` for the CLI.** `okf audit --help` today prints
  `error: missing <bundle>`, and so do `okf validate --help` and every other
  verb: the CLI has one global usage block and no per-verb help, so a verb's
  own flags are only discoverable by reading OPTIONS or this repo. `audit`
  makes it visible (six optional flags, none of which fit on its COMMANDS
  line), but the gap is CLI-wide and should be closed for all eight verbs at
  once — intercepting `--help` inside each command before its positional is
  resolved, which also changes those invocations from exit 1 to exit 0.
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

  Open follow-ups, still open:
  - **More ecosystems.** Package detection is npm and NuGet only, and the code stage is C# only.
    The architecture is multi-language by construction (one `LanguageProfile` per language, one
    `ISymbolResolver` per precision level); a second profile would test the generality of that
    seam rather than chase coverage.
  - **Per-RID package weight.** A RID-specific `dotnet tool` package measures 80.7–87.6 MB
    installed (11.5–13.3 MB to download). Most of it is tree-sitter grammars this producer never
    loads — `verilog` 17.3 MB, `razor` 10.5 MB, `cpp` 5.1 MB — which cannot be removed one file
    at a time, because `deps.json` is what feeds `NATIVE_DLL_SEARCH_DIRECTORIES`. Getting the
    installed size below ~40 MB is a follow-up, not a v1 promise.
  - **No test covers `--rev`'s branch auto-detection happy path.** Every CLI fixture repository is
    deliberately outside git (so the suite stays at ~16 s and spawns no MSBuild), and the detached
    -HEAD case is covered by the one test that does build a git repository. The auto-detected
    branch name is verified by manual run only.
- **Known limitation: `packages/` and `docs/` `resource` paths don't resolve against the bundle.**
  `producers/OkfProducer` records those families' `resource`/`sources[].resource` relative to the
  *scanned repository* (e.g. `src/OKF4net/OKF4net.csproj`), which is the semantically correct
  provenance reference — but `BundleValidator` resolves a bare relative `resource` against the
  **concept's own directory**, not the bundle root (`Bundle.TryResolveResource`), so
  `packages/okf4net.md` sends the validator looking under `<bundle>/packages/src/OKF4net/…` and it
  misses by construction: one "path not found" warning apiece (20 on this repository). Decided at
  merge time: accept the warning rather than embed copies of referenced files in the bundle (a
  larger, unplanned scope change). The `code/` family is not affected — it omits `resource`
  entirely unless `--repo-url` and a ref make it an absolute permalink, which is exactly this
  mechanism's reason. Revisit only if this becomes real friction once the producer has users.

## Out of scope

- Third-party runtime dependencies in the library or CLI (BCL-only is a hard rule).
- Divergence from the OKF v0.2 spec without a documented, cited reason.

## How to influence the roadmap

Open a [Discussion](https://github.com/jchable/okf4net/discussions) or comment on an
existing issue. Roadmap items graduate to labelled issues before work starts.
