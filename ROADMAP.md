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
  - **Next, highest-value follow-up: a time-aware audit.** A `verified`
    stamp today attests a moment, not a version — `Trust.DeriveTier` derives
    `human-reviewed` from an actor's presence alone, so a five-year-old human
    stamp counts the same as one from this morning, and nothing currently
    flags that the concept's content moved after the review. Exposing the
    stamps' timestamps on `AuditFinding` would let `okf audit` ask "reviewed,
    but as of when, and has the file changed since?" — answered outside the
    library, by comparing `max(verified[].at)` against
    `git log -1 --format=%cI -- <path>` (the folder is canonical; its
    history is git's, not the frontmatter's). Deliberately out of `okf
    verify`'s scope: it needs no new write path, only turns an existing
    field from a permanent alibi into a signal that decays. No schema
    extension (`digest`, `scope`, `note` on the stamp) is planned to
    recreate this information inside the bundle instead — that question is
    answered by git, on purpose.
  - **Atomic write-then-rename in `BundleConceptWriter`.** Every write path
    in the class ends at `File.WriteAllText`, which truncates the target and
    writes in place, so a failure mid-write (full disk, device error) can
    leave a concept truncated or half-written. `RecordVerifications` reports
    the concepts whose write returned, and that file is not among them — so
    the report is not wrong, but "exactly what landed" is a stronger claim
    than the primitive supports, and the docs now say so. Closing it means
    writing to a temporary file in the same directory and `File.Replace`-ing
    it over the target. Deliberately its own pass rather than a footnote to
    `okf verify`: the call sits immediately after the late reparse-point
    re-check and inside the per-bundle lock, so a replacement needs tests for
    `File.Replace` semantics (cross-volume, existing-file, permissions,
    what happens to the backup), for the path-safety guard still holding
    against the *temporary* name, and for the lock — a security-sensitive
    seam that must not be swapped in passing. Pre-existing and shared by
    every write path; not introduced by verification.
- **Per-verb `--help` for the CLI.** `okf audit --help` today prints
  `error: missing <bundle>`, and so do `okf validate --help` and every other
  verb: the CLI has one global usage block and no per-verb help, so a verb's
  own flags are only discoverable by reading OPTIONS or this repo. `audit`
  makes it visible (six optional flags, none of which fit on its COMMANDS
  line), but the gap is CLI-wide and should be closed for all nine verbs at
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
