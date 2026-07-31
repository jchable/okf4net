# Design: `/spec-gap-report` skill — OKF spec vs OKF4net conformance gap report

## Problem

OKF4net implements the OKF spec (currently v0.2) but there is no repeatable
way to check, at any point in time, exactly where the implementation
diverges from the *current* upstream spec text. The README carries a
hand-maintained §→type mapping table (high-level, "this section maps to
this type") and an OKF4net-version ↔ OKF-spec-version table, but neither is
generated from — or checked against — the actual normative statements in
the spec. Divergences that are intentional (documented choices) are mixed
in, conceptually, with real unaddressed gaps, with no way to tell them
apart at a glance.

## Goal

A Claude Code skill, triggered by the slash command `/spec-gap-report`,
that produces a Markdown report enumerating every normative statement in
the current upstream OKF spec, the OKF4net implementation status for each,
and a severity, distinguishing documented/intentional divergences from
undocumented gaps.

## Non-goals

- Not a CI check / not automatically run on a schedule or on push — this is
  an on-demand audit tool.
- Not a spec-conformance *test suite* — it doesn't emit code or executable
  assertions, just a report for a human to act on.
- Not responsible for fixing anything it finds, or for updating
  `ROADMAP.md` — that's a follow-up action for the user to take based on
  the report.
- Never implies touching `tests/fixtures/` to resolve a finding, even when
  a `Diverges` finding involves v0.1-covered behavior — those are
  byte-exact golden captures the project's hard rules forbid editing to
  make a test pass. The report describes gaps; how (or whether) to close
  one, without touching golden fixtures, remains a human decision.

## Trigger

Slash command: `.claude/skills/spec-gap-report/SKILL.md`, invoked only as
`/spec-gap-report`. No description-based auto-trigger (unlike
`okf:okf-validate`), since this is a deliberately heavyweight audit, not
something that should fire opportunistically.

## Spec acquisition

The upstream spec is external and not vendored in this repo. Source of
truth, confirmed during design research:
`GoogleCloudPlatform/knowledge-catalog`, file `okf/SPEC.md`
(<https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md>).

On every run:

1. Fetch the raw file text directly —
   `curl -fsSL https://raw.githubusercontent.com/GoogleCloudPlatform/knowledge-catalog/main/okf/SPEC.md`
   — and save it to the session scratchpad. This is preferred over `gh api`
   (verified equivalent output during design review, byte-identical to the
   `gh api ... | base64 -d` route) because the raw file is public: a plain
   HTTP fetch has no auth dependency (`gh api` requires an authenticated
   `gh` session even though the resource itself needs none) and no
   base64-decode step (whose flag differs between GNU and BSD `base64`,
   e.g. macOS wants `-D` not `-d` — a real portability trap for a command
   baked verbatim into a skill file used across the project's Linux/
   Windows/macOS CI matrix). Fall back to `gh api ... --jq '.content' |
   base64 -d` only if the raw fetch fails (e.g. network policy blocks raw
   GitHub content but allows the API). `WebFetch` is avoided for either
   path because it summarizes content through a small model, which risks
   losing or paraphrasing exact normative wording (`MUST`/`SHOULD`/`MAY`)
   — the report needs the literal text.
2. Extract the declared spec version from `## 12. Versioning` (pattern:
   `"This document specifies OKF version **X.Y**"`).
3. Compare that version to the latest row of the README's
   `### OKF4net version ↔ OKF spec version` table. If upstream has moved to
   a newer version than what OKF4net currently tracks, this is reported as
   **finding #1**, ahead of any section-level detail — it changes the
   framing of everything else in the report (e.g. gaps against a version
   OKF4net doesn't even target yet are lower priority than gaps against the
   version it claims to support).
4. If both the raw fetch and the `gh api` fallback fail (network, rename,
   rate limit, 404), stop and report the failure to the user rather than
   producing a report against an empty/partial spec.

## Comparison granularity

Section-level comparison (§1..§13) is too coarse — a section can be "mostly
implemented" while hiding an unaddressed `MUST NOT`. Instead, the skill
parses individual normative statements out of the spec text, each tagged
with its nearest `§` section/subsection anchor. A pure modal-keyword scan
(`MUST`/`MUST NOT`/`SHOULD`/`SHOULD NOT`/`MAY`) is **not sufficient on its
own** — verified against the real spec text during design review, it
silently drops §11's own three numbered conformance conditions ("A bundle
is **conformant** with OKF v0.2 if: 1. Every non-reserved `.md` file...
2. Every frontmatter block contains a non-empty `type` field. 3. ..."),
none of which use a modal keyword, plus §4.1's declaration that `type` is
the one always-required frontmatter key (marked `# REQUIRED` / `**Required:**`
in the spec text, again with no modal keyword). Those are two of the most
conformance-critical facts in the whole spec, so the extraction rule
combines **two** patterns:

1. Modal-keyword sentences/list items (`MUST`, `MUST NOT`, `SHOULD`,
   `SHOULD NOT`, `MAY`).
2. Declarative-requirement markers: numbered/bulleted items under a
   "conformant... if:" style lead-in (as in §11), and inline
   `REQUIRED`/`**Required:**` labels (as in §4.1's field table).

Both patterns are extracted from the actual spec body only — fenced code
blocks are excluded, since §9's own worked example embeds `##`-style
headings and prose inside a fenced `log.md` sample that must not be
mistaken for spec structure or spec requirements.

Given the same statement can appear twice — §11 restates rules already
stated in §5.1/§5.2/§5.3/§5.4/§5.5 verbatim or near-verbatim, sometimes
with an explicit forward citation (e.g. §11's "MUST treat a bare `verified`
mapping as a one-element list (§5.2)" restates §5.2's own MUST), a §11
bullet that cites another section in parentheses is treated as a **pointer
to the canonical statement**, not a second atomic unit: only the
section it points to owns the classification, annotated with a note that
it also gates §11 conformance (so its severity reflects that, without
creating a duplicate row at a different severity than its source).

Example: §11 Conformance alone yields roughly eight distinct atomic
statements this way (three declarative conformance conditions, one
`REQUIRED`-marker statement pulled in from §4.1, and the handful of
consumer `MUST`/`SHOULD` bullets not already pointers to §5), not one
"§11: done" line.

Each atomic statement (after the pointer-collapsing above) is the unit
that gets a status in the report.

## Implementation-side verification

For each normative statement:

1. Start from the README's existing §→type mapping table as an index into
   the codebase (e.g. §5 → `Frontmatter.Sources`/`Generated`/etc.).
2. Read the actual source (`src/OKF4net/*.cs` and relevant sibling
   projects) and the relevant tests under `tests/OKF4net.Tests/` to
   determine what the code actually does — not just that a type with a
   plausible name exists.
3. Given the volume (13 sections, ~15+ source files, corresponding test
   files), fan out with the `Agent` tool: one agent per spec section (or
   small group of adjacent subsections), each given the extracted
   normative statements for that section plus pointers to the relevant
   README table rows, tasked with reporting a status and file/line
   citation per statement. The main thread synthesizes all agent outputs
   into the final report. (This uses the plain `Agent` tool, not the
   `Workflow` orchestration tool — `Workflow` requires explicit user
   opt-in per session and this skill should work without that.)
4. Each agent must include, alongside every file/line citation, a short
   verbatim quote of the cited code — not just a `file:line` reference on
   its own. A `file:line` pointer is cheap for an agent to fabricate
   without actually reading the code; requiring the quoted snippet gives
   the synthesizing thread (and the human reading the final report) a
   trivial way to sanity-check that the citation is real, without turning
   this into a separate verification pipeline.

## Classification

Each normative statement gets:

- **Status**: `Implemented` / `Partial` / `Missing` / `Diverges` / `N/A`
- **If `Diverges`**: sub-classified as **Intentional** (a citation is found
  in README / CHANGELOG.md / `docs/design/*.md` / a code comment explaining
  the choice) or **Undocumented** (a real, unaddressed gap)
- **`N/A`**: for a statement that is normative in the spec but has no
  meaningful implementation surface for a *library* — e.g. §4.2's "Producers
  SHOULD favor structural markdown over freeform prose," which is authoring
  guidance for humans writing bundles, not something OKF4net's code can
  implement or fail to implement. Used sparingly, and only when the
  statement genuinely has no code-level counterpart (not as an escape
  hatch for statements that are merely hard to verify).
- **Partial vs. `Diverges`/Undocumented** (the two easiest to confuse):
  `Partial` means the implementation is on a trajectory toward the
  statement but doesn't fully satisfy it yet (e.g. the field is parsed but
  not validated, or validated but only with a `Warning` where the spec's
  `MUST` implies harder enforcement) — a plausible next PR closes it.
  `Diverges`/Undocumented means the implementation does something that
  actively conflicts with the statement, not merely less of it — a next PR
  would have to change existing behavior, not just add coverage. When a
  statement could read either way, default to `Partial` — `Diverges` is a
  stronger claim that needs unambiguous evidence of conflicting, not just
  incomplete, behavior.
- **Severity**:
  - **Critical** — breaks §11 conformance (the three numbered conditions,
    the `REQUIRED`-marker statement pulled in from §4.1, or the consumer
    `MUST`/`MUST NOT` bullets in §11 itself)
  - **Major** — a `MUST`/`SHOULD` outside §11 whose violation changes
    observable behavior
  - **Minor** — a `MAY`, an edge case, or a cosmetic/informative-only
    divergence
  - `N/A`-status statements carry no severity.

## Output

A Markdown file at
`docs/spec-conformance/YYYY-MM-DD-okf-spec-gap-report.md` (date = the day
the report is generated), containing:

1. A summary block: spec version compared against, OKF4net version/commit
   compared, and counts by status × severity.
2. Version-drift finding (if any), first.
3. Per-section detail: each normative statement, its status, severity,
   citation (spec §, code file/line, and — for intentional divergences —
   the doc/comment citing the reason).

The skill **does not auto-commit** the report — it writes the file and
tells the user it's ready, consistent with the project rule that commits
only happen on explicit request. The user decides whether to commit it,
same as any other generated artifact.

## Edge cases

- **Spec fetch failure** (raw fetch and the `gh api` fallback both fail —
  network, rename, rate limit, 404): abort with a clear error, no partial
  report.
- **Spec structure changes** (more/fewer top-level sections than the
  current 13, renumbering): the skill parses top-level section headings
  dynamically from whatever `SPEC.md` currently contains rather than
  assuming 13 fixed sections, so it stays correct as the spec evolves. The
  heading pattern is anchored specifically to `^## \d+\.\s` (a `##` heading
  starting with a number and a period, matching this spec's actual
  section-numbering convention) and excludes anything inside a fenced code
  block — verified necessary during design review, since §9's own worked
  `log.md` example embeds lines like `## 2026-05-22` inside a fenced block,
  which a bare `^##` scan would misparse as real spec sections.
- **A normative statement with no obvious code location**: reported as
  `Missing` rather than silently skipped — no silent gaps in the report.

## Testing

This is a documentation/report-generation skill, not application code —
there's no automated test suite for it. Verification is: run
`/spec-gap-report` once against the current repo state, inspect the
generated report for:

- (a) correct version-drift detection (none expected today, since both are
  v0.2);
- (b) plausible per-section statuses matching what's known from the README
  table;
- (c) that at least the two known intentional divergences already called
  out in the codebase — the `timestamp`→`generated.at` and
  `# Citations`→`sources` §13.1 fallbacks, both documented as
  equally-weighted `Warning`s in `BundleValidator` — show up correctly
  classified as **Intentional**, not as undocumented gaps;
- (d) §11's three numbered conformance conditions each appear as their own
  report row with a status — proof the extraction rule's declarative
  pattern (not just the modal-keyword pattern) actually ran, since a
  keyword-only implementation would silently produce zero rows for them;
- (e) no single spec rule appears twice in the report under two different
  section headings at two different severities — proof the §11-as-pointer
  dedup rule is applied, spot-checked specifically on the §5.2 "bare
  `verified` mapping" rule and its §11 restatement, which is the concrete
  case that motivated the rule.
