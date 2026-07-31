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

## Trigger

Slash command: `.claude/skills/spec-gap-report/SKILL.md`, invoked only as
`/spec-gap-report`. No description-based auto-trigger (unlike
`okf:okf-validate`), since this is a deliberately heavyweight audit, not
something that should fire opportunistically.

## Spec acquisition

The upstream spec is external and not vendored in this repo. Source of
truth, confirmed during design research:
`GoogleCloudPlatform/knowledge-catalog`, file `okf/SPEC.md`
(https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md).

On every run:

1. `gh api repos/GoogleCloudPlatform/knowledge-catalog/contents/okf/SPEC.md
   --jq '.content' | base64 -d` → saved to the session scratchpad. `gh api`
   is used instead of `WebFetch` because `WebFetch` summarizes content
   through a small model, which risks losing or paraphrasing exact
   normative wording (`MUST`/`SHOULD`/`MAY`) — the report needs the literal
   text.
2. Extract the declared spec version from `## 12. Versioning` (pattern:
   `"This document specifies OKF version **X.Y**"`).
3. Compare that version to the latest row of the README's
   `### OKF4net version ↔ OKF spec version` table. If upstream has moved to
   a newer version than what OKF4net currently tracks, this is reported as
   **finding #1**, ahead of any section-level detail — it changes the
   framing of everything else in the report (e.g. gaps against a version
   OKF4net doesn't even target yet are lower priority than gaps against the
   version it claims to support).
4. If `gh api` fails (network, rename, rate limit, 404), stop and report
   the failure to the user rather than producing a report against an
   empty/partial spec.

## Comparison granularity

Section-level comparison (§1..§13) is too coarse — a section can be "mostly
implemented" while hiding an unaddressed `MUST NOT`. Instead, the skill
parses individual normative statements out of the spec text: sentences or
list items containing `MUST`, `MUST NOT`, `SHOULD`, `SHOULD NOT`, or `MAY`,
each tagged with its nearest `§` section/subsection anchor. Example: §11
Conformance alone yields roughly eight distinct normative statements (three
numbered conformance conditions, plus several `MUST`/`SHOULD` bullets about
optional families), not one "§11: done" line.

Each such statement is the atomic unit that gets a status in the report.

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

## Classification

Each normative statement gets:

- **Status**: `Implemented` / `Partial` / `Missing` / `Diverges`
- **If `Diverges`**: sub-classified as **Intentional** (a citation is found
  in README / CHANGELOG.md / `docs/design/*.md` / a code comment explaining
  the choice) or **Undocumented** (a real, unaddressed gap)
- **Severity**:
  - **Critical** — breaks §11 conformance (the three numbered conditions,
    or the consumer `MUST`/`MUST NOT` bullets)
  - **Major** — a `MUST`/`SHOULD` outside §11 whose violation changes
    observable behavior
  - **Minor** — a `MAY`, an edge case, or a cosmetic/informative-only
    divergence

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

- **`gh api` failure**: abort with a clear error, no partial report.
- **Spec structure changes** (more/fewer top-level sections than the
  current 13, renumbering): the skill parses `## N. Title` headings
  dynamically from whatever `SPEC.md` currently contains rather than
  assuming 13 fixed sections, so it stays correct as the spec evolves.
- **A normative statement with no obvious code location**: reported as
  `Missing` rather than silently skipped — no silent gaps in the report.

## Testing

This is a documentation/report-generation skill, not application code —
there's no automated test suite for it. Verification is: run
`/spec-gap-report` once against the current repo state, inspect the
generated report for (a) correct version-drift detection (none expected
today, since both are v0.2), (b) plausible per-section statuses matching
what's known from the README table, and (c) that at least the two known
intentional divergences already called out in the codebase — the
`timestamp`→`generated.at` and `# Citations`→`sources` §13.1 fallbacks,
both documented as equally-weighted `Warning`s in `BundleValidator` — show
up correctly classified as **Intentional**, not as undocumented gaps.
