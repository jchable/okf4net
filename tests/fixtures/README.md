# Test fixtures — what this directory is, and what has authority

This directory holds the input bundles the test suite exercises and, under
`golden/`, **snapshots of this project's own CLI output**. The snapshots are
regenerable, under conditions stated below. They are not a reference
implementation's output, and they carry no conformance authority of their own.

**The OKF specification is the only conformance authority**: `docs/spec/SPEC.md`,
vendored at a fixed version. What the spec requires is verified by tests that
cite a `§` (`ValidateTests`, `IndexTests`, `AttestedComputationTests`, …).
What the four machine outputs (`validate --json`, `info --json`,
`audit --json`, `graph --dot`) project is verified by the full-projection
tests in `MachineOutputTests`, against hand-derived expectations. What the
snapshots here verify is that the CLI's **rendering** has not changed — no
more, and no less.

Design and rationale: `docs/superpowers/specs/2026-09-22-golden-fixtures-authority-design.md`.

## Layout

- `appendix_a/` — the example bundle: `datasets/sales.md`, `tables/orders.md`,
  `tables/customers.md`, plus two additions to exercise more of the CLI:
  - `log.md` — a root-level reserved log file with one valid ISO-8601 dated
    entry, so `info`/`index` see a non-empty log and `validate` reports no
    log-related warnings.
  - `tables/users.md` — a deliberately **non-strict** concept document: it
    has `type` and `title` but is missing `description` and `timestamp`, so
    `validate` emits the "missing recommended frontmatter field" warnings
    (§11 soft guidance — the bundle stays conformant, exit code 0).
- `okf_v02/`, `okf_v02_computation/`, `okf_v02_reserved/` — hand-authored
  v0.2 input bundles; see the revision log below for what each isolates.
- `golden/` — one group of snapshot files per test in `GoldenParityTests`.
  Exit codes are asserted in the tests, not stored as files. The `validate`
  invocations run from the repository root with the relative path shown,
  because the output embeds the path as given.

| Test | Snapshot files | Invocation | Guarded by the snapshot alone (presentation) | Also pinned directly (semantic) |
|---|---|---|---|---|
| `Validate_output_and_exitcode_match_golden` | `validate.out` | `okf validate tests/fixtures/appendix_a --as-of 2026-09-25` | line wording and order | the verdict, every diagnostic, the counts (`MachineOutputTests`, `ValidateTests`), exit code 0 |
| `Validate_v02_fixture_matches_golden` | `validate-v02.out` | `okf validate tests/fixtures/okf_v02 --as-of 2026-09-25` | line wording | the rules behind these diagnostics, on their own inputs (`ValidateTests`); exit code 0 — this fixture's own diagnostic set and counts are guarded by the snapshot and the semantic regime |
| `Validate_computation_fixture_matches_golden` | `validate-computation.out` | `okf validate tests/fixtures/okf_v02_computation --as-of 2026-09-25` | line wording | the §10/§6.2 rules, on their own inputs (`AttestedComputationTests`, `ValidateTests`); exit code 0 — this fixture's own diagnostic set and counts are guarded by the snapshot and the semantic regime |
| `Validate_reserved_fixture_matches_golden` | `validate-reserved.out` | `okf validate tests/fixtures/okf_v02_reserved --as-of 2026-09-25` | line wording | the four §11 errors and the non-conformant verdict (`MachineOutputTests`), exit code 1 |
| `Info_output_matches_golden` | `info.out` | `okf info tests/fixtures/appendix_a` | column alignment, the `types:` block | every count (`MachineOutputTests.Info_json_*`), both numbers of the `links:` line |
| `Audit_report_matches_golden` | `audit-v02.out` | `okf audit tests/fixtures/okf_v02 --as-of 2099-06-01` | column layout | the selection, tiers, statuses, staleness (`MachineOutputTests.Audit_json_*`, `AuditTests`) |
| `Audit_json_matches_golden` | `audit-v02.json` | `okf audit tests/fixtures/okf_v02 --as-of 2099-06-01 --json` | property order | every value (`MachineOutputTests.Audit_json_projects_a_stale_finding_completely`) |
| `Graph_dot_matches_golden` | `graph.dot` | `okf graph <appendix_a> --dot` | edge order | the grammar — header line, the `rankdir` line, two-space edge indentation, closing brace — the edge set and its size, determinism (`MachineOutputTests.Graph_dot_*`) |
| `Fmt_output_matches_golden` | `fmt/users.md` | `okf fmt <appendix_a>/tables/users.md` | nothing beyond the envelope | the envelope, idempotence, the stdout branch (`DocumentTests`, `CliTests`) |
| `Index_generation_matches_golden` | `index-input/index.md`, `index-input/datasets/index.md`, `index-input/tables/index.md` | `IndexGenerator.RegenerateIndexes` on a copy of `appendix_a` | the synthesizer wording | §8 structure and no frontmatter, `# Other`, no description suffix, self-listing (`IndexTests`) |
| `Verify_output_matches_golden` | `verify.out`, `verify-dau.md` | `okf verify <copy of okf_v02> metrics/dau metrics/legacy --by human:ada --at 2026-08-28T09:14:00Z` | the two stdout lines' wording | the written `verified` block (§5.2) and that nothing else moved (`RecordVerificationTests`) |

## Two regimes for a change to a snapshot

A snapshot changes when the CLI's output changes on purpose. **Which procedure
applies depends on what the diff changes, not on which file it lands in.** A
diff is **presentation** only if it preserves all five of:

1. the facts exposed, their values and their associations (a count stays on
   the same type, a diagnostic on the same severity);
2. the elements present, absent, and their multiplicity;
3. link targets and relations;
4. the syntactic validity and interpreted structure of the format (a DOT
   document stays parseable; an `index.md` stays sections and lists in the
   sense of §8);
5. written effects and exit codes.

A diff that changes any of these is **semantic**. A mixed diff is semantic.
Doubt resolves to semantic.

- **Presentation diff** — regenerate with the update mode (below), read the
  diff, re-run without the variable. Nothing else.
- **Semantic diff, and every edit to an input bundle** — explicit user
  arbitration first, then a dated entry in the revision log below saying what
  changed and why, then the same two runs. The update mode is a capture tool;
  having the command is not permission to use it on these.

Examples, so the rule is applied the same way twice:

| Change | Regime | Why |
|---|---|---|
| Realign the columns of `okf info` | presentation | same facts, same values |
| Swap two DOT edge lines without changing the edges | presentation | same edge set (order is not a contract; determinism is, and `MachineOutputTests` pins it) |
| Reorder the properties of a JSON object | presentation | same interpreted object |
| `5 internal (0 broken)` → `5 internal` | **semantic** | a fact disappeared (criterion 1) |
| Indent a whole `index.md` by four spaces | **semantic** | sections become a code block (criterion 4) |
| `[Users](users.md)` → `[Comptes](users.md)` in an index | **semantic** | the visible label is a fact exposed to the reader (criterion 1) |

## Regenerating a snapshot

The update mode is **scoped**: it rewrites only the tests you name, and refuses
the whole list before writing anything if a name is unknown, empty or
duplicated. Name the tests (exact `GoldenParityTests` method names,
comma-separated, no wildcard) in `OKF_UPDATE_GOLDEN`, and pass the same names
to `--filter` so the run executes them:

```sh
OKF_UPDATE_GOLDEN=Info_output_matches_golden,Graph_dot_matches_golden \
  dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj \
  --filter "FullyQualifiedName~GoldenParityTests.Info_output_matches_golden|FullyQualifiedName~GoldenParityTests.Graph_dot_matches_golden"
```

(PowerShell: `$env:OKF_UPDATE_GOLDEN = "Info_output_matches_golden,Graph_dot_matches_golden"`
first, and `Remove-Item Env:OKF_UPDATE_GOLDEN` afterwards.)

**That command exits RED, by design, and a green run would be the bug.** Each
named test rewrites its group of files — a group is written whole, and put back
whole if a file cannot be replaced — from the value it would have compared, and
then *refuses to assert*: the expected side was just produced by the same
harness as the actual side, so the comparison would be a tautology, and a
tautology reported green is exactly how a stale variable in someone's shell
disarms a snapshot without anyone noticing. Two failures are possible and say
which they are: **capture failure** (a guard such as the exit code did not
hold, or a file could not be written — the message says whether the group was
left untouched or restored) and **refuses to assert** (the group was rewritten;
read the diff). If a file could not be put back, the message names it and the
directory where the backups were kept. If a named test did not run because
`--filter` excluded it, the collection fails at teardown
(`Test Collection Cleanup Failure (GoldenParity)`, exit code 1); the console
prints only the exception type, and the names of the tests that did not capture
are in the detailed log (`--logger "console;verbosity=detailed"`).

One limit, stated rather than hidden: if the filter excludes **every**
`GoldenParityTests` test, the fixture that enforces the scope is never
constructed, the variable has no effect, and the run is green. Nothing is
written and there is no diff — the absence is the signal. Keep the filter and
the variable naming the same tests.

So the procedure is two runs, not one:

1. Run with the variable. Read `git diff tests/fixtures/golden`. Decide the
   regime (above): a presentation diff needs nothing more; a semantic diff
   needed arbitration **before** this step and needs its dated entry below.
2. Re-run **without** the variable to actually check, and commit the diff with
   the change that caused it.

## Comparison contract

Snapshots are committed with LF endings, UTF-8 without BOM, and compared after
being read as text: the four `validate` outputs and `audit-v02.json` are
compared **after** the OUTPUT's native path separators are normalised to `/`
(the snapshots were captured on Linux and embed paths); every other file is
compared as read. The update mode writes the normalised text, UTF-8 without
BOM, LF, nothing added. `.gitattributes` marks `tests/fixtures/** -text` and
`.editorconfig` excludes this directory, so no tool normalises them. Never let
an editor touch them: trailing whitespace, final newlines and line endings are
significant.

## Provenance (historical)

Four snapshot groups — `info.out`, `graph.dot`, `fmt/users.md` and the three
`index-input/*.md` (six files) — still hold the bytes first captured on
2026-07-21 from the Rust `okf` crate that then lived in this repository (source
at commit `d20343c`), built in Docker:

```
docker image: rust:1  (pulled digest sha256:9a2cd304a852f05d3352f75bc2775242371c0169a72dbb40d5d881379d571989)
rustc 1.97.1 (8bab26f4f 2026-07-14)
cargo 1.97.1 (c980f4866 2026-06-30)
```

Build: `cargo build --release` with `CARGO_TARGET_DIR=/tmp/target` (kept
outside the mounted worktree so no build artifacts landed in git).

That origin is history, not authority: the project implements the published
spec, and treating a deleted reimplementation as its norm was an authority that
existed and was abandoned by decision on 2026-09-30. These four groups follow
the same two regimes as every other snapshot.

## Revision log

## v0.1 → v0.2 bump (2026-07-28)

The OKF spec bump from v0.1 to v0.2 (provenance §5.1, trust §5.2/§5.3,
lifecycle §5.4/§5.5, actor §7, `okf_version` §11/§12) intentionally changes
some captured output. Per the exception documented in the repo's `CLAUDE.md`,
two kinds of change were made, neither of them a hand-edit and neither of
them a re-capture from the (removed) Rust binary:

- `golden/validate.out` was **revised**: regenerated by running the C#
  `okf validate tests/fixtures/appendix_a` from the repo root (the
  `appendix_a` fixture itself is unchanged). The new content reflects the
  v0.2 validator's additional legacy-field diagnostics — `appendix_a`'s
  concepts all still use the v0.1 `timestamp` field with no `generated`
  block, so each now gets a `... 'timestamp' is a legacy field; prefer
  'generated.at'` line (3 of the 4 concepts have `timestamp`) — and
  the CLI's version banner now reads `✓ conformant with OKF v0.2`. The exit
  code golden (`validate.exitcode`, `0`) is unchanged: `appendix_a` remains
  conformant.
  - **2026-07-29 follow-up:** the legacy-`timestamp` diagnostic was
    `[warning]`-not-`[info]` from the start, matching the legacy `#
    Citations` diagnostic. Both §13.1 renames (`timestamp`→`generated.at`,
    `# Citations`→`sources`) are equally-weighted producer nudges — a v0.2
    consumer falls back to the legacy form for both, so the bundle stays
    conformant either way, but neither rename is a mere formality the
    validator should stay quiet about. `golden/validate.out` was
    regenerated the same way as above; the 3 `timestamp` lines moved from
    `[info]` to `[warning]` and the summary line's counts shifted
    accordingly (`5 warning(s), 3 info` → `8 warning(s), 0 info`).
- `okf_v02/` and `golden/validate-v02.out` / `golden/validate-v02.exitcode`
  are **new** v0.2 fixtures, hand-authored against the v0.2 spec text (not
  byte-exact-vs-Rust — v0.2 postdates the Rust reference implementation).
  `okf_v02/metrics/dau.md` exercises a fully-populated, clean v0.2 document
  (`generated`/`verified` stamps, `sources`, `usage_window`, `status`, a
  future `stale_after`); `okf_v02/metrics/legacy.md` exercises three v0.2
  validator warnings at once: a malformed `generated.by` actor (`bob`, no
  `human:`/`process:` prefix), an unknown `status` value (`retired`), and
  the legacy `# Citations` heading (superseded by the `sources` frontmatter
  field). Both fixtures were verified by reading each emitted diagnostic
  against the Task 9 validator rules before saving the golden, not assumed.

## §10 Attested Computation bump (2026-07-29)

- `okf_v02_computation/` and `golden/validate-computation.out` /
  `golden/validate-computation.exitcode` are **new** v0.2 fixtures for §10
  (Attested Computation) and its §6.2 (path-valued frontmatter fields)
  dependency — hand-authored and hand-verified against the v0.2 spec text,
  like `okf_v02/` above: no reference binary implements §10 either.
  - `computations/revenue.md` — a fully well-formed **inline** Attested
    Computation (`runtime: bigquery`, a `parameters` entry, `executor`/
    `attester` resources resolving to real files under the bundle root's
    `references/`, `generated`/`verified`/`sources`, and a
    future `stale_after`), plus its `# Computation` fenced SQL block.
    Contributes exactly two diagnostics, both deliberate: its `stale_after`
    and its `sources[].last_modified` keep the legacy date-only form on
    purpose, so each raises a `LegacyDateOnlyTimestamp` warning — see
    "Temporal form (§5)" below, which is where that choice is explained.
    It contributed none before the §5 work of 2026-08-31.
  - `computations/revenue-file.md` — the **file-based** variant
    (`computation: references/computations/revenue.sql`, no fence).
    Contributes zero diagnostics.
  - `references/skills/run-on-bq.md` and
    `references/attesters/revenue.py` /
    `references/computations/revenue.sql` are the path-valued
    targets the two concepts above point at; the `.md` one is itself a
    plain, conformant `Skill` concept (every `.md` file under a bundle root
    is loaded as a concept, §3), the other two are non-`.md` plain-text
    targets.
  - `metrics/revenue.md` — a `Metric` linking `computations/revenue.md` by
    a normal markdown body link (§10.4), to show an Attested Computation
    being referenced like any other concept.
  - `malformed/both.md`, `malformed/broken-exec.md`, `malformed/no-runtime.md`
    each isolate exactly one §10/§6.2 warning: a `computation:` path
    declared *together with* an inline `# Computation` fence, an
    `executor.resource` pointing at a file that does not exist, and an
    Attested Computation missing the required `runtime` field,
    respectively. Every field not under test (recommended fields, the
    other frontmatter path-valued fields) is kept well-formed so each file
    contributes exactly the one diagnostic it is named for.
  - Every diagnostic in `validate-computation.out` was verified by reading
    it against the exact message text and trigger condition in
    `BundleValidator.Validate` (§10 §7 of the design) before saving the
    golden, not assumed — including that `runtime` absence, the
    inline-vs-path ambiguity, and a broken `executor.resource` are all
    `[warning]`, never `[error]`: §10 sits outside the §11 conformance
    floor, so a malformed Attested Computation concept stays conformant
    (exit code `0`).

## §11 conformance fix for malformed reserved files (2026-07-31)

- `okf_v02_reserved/` and `golden/validate-reserved.out` /
  `golden/validate-reserved.exitcode` are **new** v0.2 fixtures for the
  fix that makes `BundleValidator.ValidateReserved` correctly enforce §11
  condition 3 (reserved files must follow their §8/§9 structure) —
  hand-authored and hand-verified against the actual `BundleValidator`
  behavior after the fix, like the fixtures above: no reference binary
  implements this either, and every prior golden fixture predates the
  fix (all were re-verified during design and confirmed unaffected).
  - `index.md` (root) declares an extra key beside `okf_version` →
    `RootIndexExtraFrontmatter`, now `[error]`.
  - `log.md` (root) has a non-ISO-8601 date heading → `LogDateInvalid`,
    now `[error]`.
  - `sub/index.md` (non-root) declares frontmatter → `IndexHasFrontmatter`,
    now `[error]`.
  - `broken/index.md` has unparseable YAML frontmatter →
    `UnparseableIndex`, a brand-new diagnostic for a case that previously
    produced no diagnostic at all.
  - `concepts/note.md` is a fully clean concept, contributing zero
    diagnostics, so every diagnostic in the golden output is attributable
    to exactly one of the four cases above.
  - The bundle is **not conformant** (exit code `1`) — this is the point
    of the fix: all four cases were previously `[warning]` or silent, and
    the bundle incorrectly validated as conformant (exit code `0`).
  - `DiagnosticCode.UnparseableIndex`/`UnparseableLog`'s decoder-failure
    branch (invalid UTF-8 bytes in a reserved file) is exercised by unit
    tests instead of this fixture — `ValidateTests.Unreadable_index_bytes_are_an_error`
    and `ValidateTests.Unreadable_log_bytes_are_an_error` write raw invalid
    UTF-8 bytes directly to `index.md`/`log.md`, the same technique already
    used elsewhere in this repo for a non-UTF-8 `log.md`
    (`OkfValidateChangesTests.ChangesSince_skips_a_non_utf8_log_file_with_a_note_instead_of_throwing`).
    Since `ChangeLog.Parse` never throws, that decoder-failure branch is in
    fact the *only* way `UnparseableLog` can fire in practice.
  - Not covered by any test (documented gap, not an oversight): a reserved
    file that fails to *read* for I/O/permission reasons specifically (as
    opposed to failing to *decode* or *parse*) — the `IOException`/
    `UnauthorizedAccessException` catch clauses in `ValidateReserved`. No
    reliable, non-flaky, cross-platform way to construct a genuinely
    unreadable-for-permission-reasons file was found for this repo's
    Linux/Windows/macOS CI matrix; the code path is identical in shape to
    the decoder-failure and parse-failure branches that *are* covered
    (same diagnostic construction, different caught exception type), so
    the risk of it being wrong is low, but it remains unexercised by an
    automated test.

## `okf audit` goldens (2026-08-21)

- `golden/audit-v02.out`, `golden/audit-v02.json` — output of
  `okf audit tests/fixtures/okf_v02 --as-of 2099-06-01` (and its `--json`
  form). **Hand-authored**, verified against the spec text (§5.3 trust tiers,
  §5.4 statuses, §5.5 staleness) rather than captured from the reference CLI:
  `audit` is an OKF4net verb with no upstream counterpart. The `--as-of` date
  is pinned so the output cannot drift with the calendar.
  - 2026-09-12: gained `evaluatedAt` (the §5.5 evaluation instant; `asOf` is
    its date) — hand-verified against §5.5, this fixture is a v0.2
    hand-authored capture, not a reference-binary capture.

## `okf verify` goldens (2026-08-28)

- `golden/verify.out` — output of `okf verify <copy of okf_v02> metrics/dau
  metrics/legacy --by human:ada --at 2026-08-28T09:14:00Z`. **Hand-authored**,
  verified against the design spec's stated output format rather than captured
  from a reference CLI: `verify` is an OKF4net verb with no upstream
  counterpart. The two lines were written into the plan before that run, then
  confirmed byte-for-byte against its actual stdout — the same run that
  produced `verify-dau.md` below. The bundle is a throwaway copy because the
  verb writes. The first line carries a `(replaces …)` suffix because
  `okf_v02/metrics/dau.md` already holds a `human:ada` stamp, so that run
  exercises the replace path while the second line exercises the append path.
- `golden/verify-dau.md` — `metrics/dau.md` as it stands **after** that same
  run. Pins what stdout cannot: that the stamp replaced the existing `human:ada`
  entry **in place** (still the second entry, after `process:nightly`, which is
  untouched), that `generated` was neither rewritten nor refreshed, and that no
  key was added, dropped or reordered. The 2026-08-28 capture re-emitted the
  *whole* frontmatter through the YAML emitter's canonical block style, so the
  source fixture's flow mappings, inline list, and compact entries
  (`tags: [engagement]`, `generated: { … }`, `usage_window: { … }`, and the
  `sources` entry) appeared here expanded. Every scalar value other than the
  replaced `at` was unchanged, as was the body. Produced by running
  the command once on a copy, then **read line by line and justified by hand**
  before being frozen — the inspection is the provenance, not the capture.
  Revised once, when the §5 temporal-form pass gave the source fixture explicit
  UTC offsets: this golden is *derived* from `okf_v02/metrics/dau.md`, so a
  deliberate change to its input has to reach it. Four values moved with the
  source (`sources[].last_modified`, both `usage_window` bounds, `stale_after`)
  and nothing else did — re-checked by hand, not re-captured blindly: the
  `human:ada` stamp is still replaced in place as the second `verified` entry
  after an untouched `process:nightly`, `generated` is still neither rewritten
  nor refreshed, and no key was added, dropped or reordered.
  - **2026-09-13 revision (Task C7, finding #11, §5.2):** `RecordVerifications`
    used to build its output by fully re-serializing the parsed frontmatter
    through `YamlEmitter`, exactly like every other write path in this
    library — which is what the paragraph above described and pinned as
    deliberate. That turned out to be a bug specific to `verify`: its own doc
    comment already promised "preserving every other frontmatter key and the
    body", which a full re-emit does not do (it also normalizes CRLF to LF and
    drops YAML comments, neither of which this fixture exercises, but both of
    which a CRLF or commented bundle would hit). `RecordVerifications` now
    edits the `verified:` block **in place** in the raw text
    (`FrontmatterBlockEdit`) and never touches any other key. This golden is
    revised to match: `tags: [engagement]`, `generated: { by: …, at: … }`,
    the `sources` entry (`- id: ga4` on one line), and
    `usage_window: { from: …, to: … }` now appear exactly as
    `okf_v02/metrics/dau.md` itself writes them, in their original flow/inline
    spelling — nothing outside `verified` moved. The clause above calling the
    reflow "pre-existing behaviour of every bundle write, not something
    `verify` does" is no longer true of `verify`: it is still true of
    `WriteConcept`/`AppendToConceptAtomic`, which still fully re-serialize, but
    `verify` is now the one write path that does not. Re-checked by hand
    against the same four properties as the prior revision: the `verified`
    block is still the emitter's canonical block style, with `human:ada`
    replaced in place as the second entry after an untouched `process:nightly`;
    `generated` is still neither rewritten nor refreshed; no key was added,
    dropped or reordered; the body is still byte-identical. Confirmed by
    running the command on a fresh copy and diffing the result against both
    the source fixture (the only difference is the `verified:` block) and the
    prior golden (the only difference is `tags`/`generated`/`sources`/
    `usage_window` reverting to the source's own spelling) before freezing the
    new bytes.
## Temporal form (§5) (2026-08-31)

OKF v0.2 §5 requires every timestamp-valued key to be an ISO 8601 datetime with
an explicit UTC offset (`2026-06-30T14:00:00Z`). OKF4net reads the legacy
date-only form as a fallback and warns (`LegacyDateOnlyTimestamp`), in the same
way it handles the §13.1 legacy fields.

These two fixtures deliberately cover both paths and **must not be made
uniform** — making either match the other silently drops a covered path:

- `okf_v02/metrics/dau.md` carries the **conformant** form on every
  timestamp-valued key it has — `stale_after`, `sources[].last_modified` and
  both `usage_window` bounds. Revised on 2026-08-31 from the previous date-only
  values, under the CLAUDE.md exception for a deliberate spec change, citing §5.
- `okf_v02_computation/computations/revenue.md` keeps the **legacy** date-only
  form on purpose, on both its `stale_after` and its `sources[].last_modified`,
  so `validate-computation.out` captures the fallback warning reaching two
  different keys rather than only the one everybody thinks of (`stale_after`).
  The golden pins the rendered text, which is all `Diagnostic.ToString()` emits
  — severity, path and message. The typed `Field` that tells the two apart is
  pinned by `ValidateTests.A_legacy_date_only_usage_window_warns_once_per_bound`
  (and its `last_modified` / `stale_after` neighbours), which assert on `.Field`
  and `.Code` directly; no golden can, since neither reaches the rendered line.

§5 reaches every timestamp-valued key, not just `stale_after`: §5.1 makes
`usage_window` a "`{ from, to }` datetime range" and `last_modified` a recency
timestamp. §9 is the deliberate exception — `log.md` date headings MUST stay
bare `YYYY-MM-DD`, and no fixture here should ever give one a time.

Two goldens moved with that revision, both re-derived and inspected line by line
rather than blanket-regenerated:

- `golden/validate-computation.out` now carries **two** `[warning]` lines for
  `computations/revenue.md` — one for its date-only `stale_after` and one for
  its date-only `sources[].last_modified` — and its summary reads
  `5 warning(s)`, up from the pre-§5 `3 warning(s)`. Two lines rather than one
  is deliberate: it shows the §5 check reaching both keys on one concept, and
  each line naming the key it is about. It does **not** show the diagnostic's
  `Field`: `Diagnostic.ToString()` renders severity, path and message only, so
  neither `Field` nor `Code` ever reaches a golden. Those are asserted directly
  in `tests/OKF4net.Tests/ValidateTests.cs` (the `A_legacy_date_only_*` tests).
  `validate-computation.exitcode` stays
  `0`: the diagnostic is a `Warning`, and §5 form sits outside the §11
  conformance floor.
- `golden/audit-v02.json`'s `findings[0].staleAfter` goes `"2099-01-01"` →
  `"2099-01-01T00:00:00Z"`. That field is the **verbatim raw frontmatter
  value** (`Lifecycle.StaleAfterRaw`), so it echoes the fixture edit directly.
  Nothing else in the JSON moved — `staleCount`, `stale` and `asOf` are
  unchanged, i.e. `okf audit` reaches the same verdict.

`golden/audit-v02.out` (the text form) is deliberately **unchanged**:
`AuditVocabulary.Freshness` renders the *parsed* date as `yyyy-MM-dd`, so it
still reads `stale 2099-01-01` for the now-conformant value. That is the
invariant to preserve if this rendering is ever revisited.

## §6.2 path-valued field bases (2026-09-11)

`okf_v02_computation/` was **relaid out**, and one of its concepts edited, to
match the structure the spec itself uses. No golden capture was touched: the
byte-exact `golden/validate-computation.out` is unchanged, and
`Validate_computation_fixture_matches_golden` passes against it as-is. That is
the point of recording this here — the fixture moved, the captured output did
not.

Why it was allowed at all: this fixture is not a capture of a reference
binary. As the §10 section above states, no reference implementation
implements §10, so `okf_v02_computation/` was **hand-authored and
hand-verified against the v0.2 spec text**. That hand-verification was wrong
on one point, and the fixture had been shaped around the error rather than
around the spec:

- `computations/revenue.md` declares `executor.resource:
  references/skills/run-on-bq.md`, copied from §10.2. Appendix A gives that
  example's layout, with the concept in `computations/` and `references/` at
  the **bundle root**. The fixture instead placed `references/` *under*
  `computations/`, which is where OKF4net's then-current concept-relative
  resolution looked — so the fixture illustrated the implementation rather
  than the spec, and could never have caught the defect. See **S6.2-1** in
  `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md`.

What changed:

- `computations/references/` → `references/` (a `git mv`; the three target
  files are byte-identical). The declarations in `computations/revenue.md`
  and `computations/revenue-file.md` are untouched — they were already right;
  it was the tree around them that was wrong.
- `malformed/both.md`'s `computation: refs/query.sql` → `./refs/query.sql`.
  That file's `refs/` directory is genuinely a sibling of the concept, so
  under the corrected rule it needs the explicit document-relative prefix.
  This also gives the fixture coverage of the `./` form alongside the bare
  one. The file's single intended diagnostic (a `computation:` path declared
  together with an inline fence) is unaffected, which is why the golden does
  not move.

  State the alternative plainly, since this one is a judgement call: left
  alone, that bare `refs/query.sql` would have resolved from the root, missed,
  and added a sixth `… not found` warning — so the fixture INPUT was changed
  to hold the captured OUTPUT still. The justification is that `./` is the
  spelling that expresses what that file actually means under the adopted
  reading of §6.2 (S6.2-1), not that the
  golden was inconvenient; had the concept genuinely intended a root-relative
  path, the right move would have been to re-bless the capture and say so.

`malformed/broken-exec.md` keeps its bare `does-not-exist.md`: it names a file
that exists at neither base, so it still contributes exactly the one
`… not found` warning it is there for, with the same message text.

## Two deletions under the fixtures-authority design (2026-10-01)

Arbitration: `docs/superpowers/specs/2026-09-22-golden-fixtures-authority-design.md`, §4 and §5.6. Both are element removals (criterion 2 of that design's diff rule), hence recorded here.

- **`golden/index-input/`'s five bundle copies** (`log.md`, `datasets/sales.md`, `tables/{customers,orders,users}.md`) are deleted. `Index_generation_matches_golden` copies `appendix_a/` into a temporary directory and reads only the three generated `index.md` files, so no assertion changes. What is lost is a standalone historical archive of the input bundle as it was captured on 2026-07-21; what is kept is the three `index.md` outputs and the file-count assertion (which catches an extra file or a net deletion, not a modified original).
- **The four `*.exitcode` files** are deleted. Each held one ASCII digit; the values now live in `GoldenParityTests` as assertions with their reason: `0` for `appendix_a`, `okf_v02` and `okf_v02_computation` (warnings only, still conformant), `1` for `okf_v02_reserved` (§11 condition 3 fails; the integer is this CLI's contract, not the spec's).
