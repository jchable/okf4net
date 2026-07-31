# OKF spec gap report skill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `.claude/skills/spec-gap-report/SKILL.md`, a Claude Code skill triggered by `/spec-gap-report` that produces a Markdown report enumerating every normative statement in the current upstream OKF spec, its OKF4net implementation status, and a severity — separating documented/intentional divergences from real undocumented gaps.

**Architecture:** A single self-contained `SKILL.md` runbook (matching the project's existing `release`/`update-website` skill format: YAML frontmatter + numbered procedural sections, no subdirectories). It instructs whoever runs it to: fetch the live upstream spec, extract atomic normative statements with a two-pattern rule, fan out via the `Agent` tool (one agent per spec section) to check each statement against `src/OKF4net/`, classify status/severity, and write a dated Markdown report — never auto-committing it.

**Tech Stack:** Markdown (the skill file itself), Bash/`curl`/`gh` (spec acquisition, run by whoever executes the skill), the `Agent` tool (implementation-side verification fan-out). No new C# code — this plan touches no `src/` or `tests/` project.

## Global Constraints

- Zero third-party runtime dependencies is **not applicable** — this plan adds no C# code, only a skill (Markdown) file.
- The skill is triggered **only** by the explicit slash command `/spec-gap-report` — no description-based auto-trigger.
- Spec acquisition prefers a raw HTTP fetch (`curl -fsSL https://raw.githubusercontent.com/GoogleCloudPlatform/knowledge-catalog/main/okf/SPEC.md`) over `gh api`, and never uses `WebFetch` for the spec text itself (risk of paraphrasing exact `MUST`/`SHOULD`/`MAY` wording).
- Implementation-side verification fans out with the plain `Agent` tool — never the `Workflow` orchestration tool (that requires explicit per-session user opt-in this skill must not assume).
- The generated report is written to `docs/spec-conformance/YYYY-MM-DD-okf-spec-gap-report.md` and is **never auto-committed** by the skill.
- The skill must never imply touching `tests/fixtures/` to resolve any finding it reports.
- Source design doc: `docs/superpowers/specs/2026-07-31-okf-spec-gap-report-design.md` — every task below implements a specific section of it; re-read the relevant section before starting a task if anything here is ambiguous.

---

### Task 1: Scaffold the skill file and its spec-acquisition section

**Files:**
- Create: `.claude/skills/spec-gap-report/SKILL.md`

**Interfaces:**
- Produces: the skill's YAML frontmatter (`name: spec-gap-report`, a `description` covering trigger phrases) and a `## 1. Fetch the current spec` section containing the exact fetch/fallback/version-extraction/version-drift commands. Later tasks append further `##` sections to this same file and must reuse the term "atomic statement" introduced in Task 2, not invent a synonym.

- [ ] **Step 1: Write the failing check for the fetch step**

Create a throwaway scratch check (not part of the skill file yet) that exercises exactly the primary command the skill will document:

```sh
mkdir -p /tmp/spec-gap-report-check
curl -fsSL https://raw.githubusercontent.com/GoogleCloudPlatform/knowledge-catalog/main/okf/SPEC.md -o /tmp/spec-gap-report-check/SPEC.md
grep -m1 -oE 'specifies OKF version \*\*[0-9]+\.[0-9]+\*\*' /tmp/spec-gap-report-check/SPEC.md
```

Expected: command fails right now only in the trivial sense that nothing has documented it yet — run it to establish the baseline output before it's enshrined in the skill file.

- [ ] **Step 2: Run the check and record the actual output**

Run the three commands above.
Expected output of the last line: `specifies OKF version **0.2**`
This confirms both that the fetch works and that the upstream spec is still v0.2 (matching OKF4net's latest tracked row in `README.md`'s `### OKF4net version ↔ OKF spec version` table, `[0.4.0] | v0.2`), so no version-drift finding is expected today.

- [ ] **Step 3: Write the skill frontmatter and intro**

Create `.claude/skills/spec-gap-report/SKILL.md`:

```markdown
---
name: spec-gap-report
description: >
  Generate a detailed Markdown report of gaps between the current upstream
  OKF spec (GoogleCloudPlatform/knowledge-catalog, okf/SPEC.md) and the
  OKF4net implementation, with a severity per gap and documented/intentional
  divergences called out separately from real gaps. Triggers ONLY on the
  literal `/spec-gap-report` slash command — never on natural-language
  requests for a conformance audit or spec comparison, even a close match;
  this is a deliberately heavyweight, on-demand audit, not something that
  should fire opportunistically from a description match.
---

# OKF spec gap report

Produces a dated report at `docs/spec-conformance/YYYY-MM-DD-okf-spec-gap-report.md`
enumerating every **atomic statement** in the current upstream OKF spec,
OKF4net's implementation status for each, and a severity — with
documented/intentional divergences called out separately from real,
undocumented gaps.

**Non-goals:** this is not a CI check, not a spec-conformance test suite (no
code or executable assertions, just a report for a human to act on), not
responsible for fixing anything it finds or for updating `ROADMAP.md`, and
it must never imply touching `tests/fixtures/` to resolve a finding — those
are byte-exact golden captures the project's `CLAUDE.md` forbids editing to
make a test pass. The report describes gaps; closing one (without touching
golden fixtures) is a separate, human-directed follow-up.

## 1. Fetch the current spec

```sh
curl -fsSL https://raw.githubusercontent.com/GoogleCloudPlatform/knowledge-catalog/main/okf/SPEC.md -o <scratchpad>/SPEC.md
```

If this fails (network policy, rename, rate limit), fall back to:

```sh
CONTENT=$(gh api repos/GoogleCloudPlatform/knowledge-catalog/contents/okf/SPEC.md --jq '.content')
echo "$CONTENT" | base64 -d > <scratchpad>/SPEC.md 2>/dev/null || echo "$CONTENT" | base64 -D > <scratchpad>/SPEC.md
```

(GNU `base64` wants `-d`, BSD/macOS `base64` wants `-D` — try both rather
than hardcoding one, since this fallback is most likely to be needed on
whichever platform it wasn't tested on.)

If *both* fail: stop and tell the user the fetch failed — do not produce a
report against an empty or partial spec.

Extract the declared version:

```sh
grep -m1 -oE 'specifies OKF version \*\*[0-9]+\.[0-9]+\*\*' <scratchpad>/SPEC.md
```

Compare it to the latest row of `README.md`'s
`### OKF4net version ↔ OKF spec version` table. If the upstream version is
newer than what that table's latest row lists, this is **finding #1** in
the eventual report, ahead of any section-level detail — it reframes
everything else (gaps against a version OKF4net doesn't even target yet
matter less than gaps against the version it claims to support).
```

- [ ] **Step 4: Verify the documented command matches the working command**

Copy the exact fetch + grep commands out of the freshly written `## 1. Fetch
the current spec` section and run them verbatim (pointing at
`/tmp/spec-gap-report-check/` instead of `<scratchpad>`):

```sh
curl -fsSL https://raw.githubusercontent.com/GoogleCloudPlatform/knowledge-catalog/main/okf/SPEC.md -o /tmp/spec-gap-report-check/SPEC.md
grep -m1 -oE 'specifies OKF version \*\*[0-9]+\.[0-9]+\*\*' /tmp/spec-gap-report-check/SPEC.md
```

Expected: same `specifies OKF version **0.2**` output as Step 2 — proves the
copy-pasted documentation is byte-correct, not just conceptually right.

- [ ] **Step 5: Commit**

```sh
git add .claude/skills/spec-gap-report/SKILL.md
git commit -m "feat(skills): scaffold spec-gap-report skill with spec-acquisition step"
```

---

### Task 2: Write the normative-statement extraction section

**Files:**
- Modify: `.claude/skills/spec-gap-report/SKILL.md` (append `## 2. Extract normative statements`)

**Interfaces:**
- Consumes: the `<scratchpad>/SPEC.md` file path convention from Task 1.
- Produces: the term **atomic statement** (an extracted normative unit) and the **pointer** concept (a §11 bullet that cites another section, collapsed into that section's statement rather than counted twice). Task 3 and Task 4 both refer to "atomic statement" and "pointer" — reuse these exact terms, do not rename them.

- [ ] **Step 1: Write the failing check proving keyword-only extraction is insufficient**

```sh
sed -n '733,741p' /tmp/spec-gap-report-check/SPEC.md
sed -n '733,741p' /tmp/spec-gap-report-check/SPEC.md | grep -nE 'MUST|SHOULD|MAY'
```

Expected: the first command prints §11's three numbered conformance
conditions (`1. Every non-reserved .md file...`, `2. Every frontmatter
block contains a non-empty type field.`, `3. Every reserved filename...`);
the second command (a pure modal-keyword grep over the same lines) prints
**nothing** — proving a keyword-only scan silently drops all three.

- [ ] **Step 2: Write the failing check proving the REQUIRED-marker gap**

```sh
sed -n '161,180p' /tmp/spec-gap-report-check/SPEC.md | grep -nE 'MUST|SHOULD|MAY'
grep -n 'REQUIRED' /tmp/spec-gap-report-check/SPEC.md | head -3
```

Expected: the first command prints nothing for the `type: <Type name> #
REQUIRED` line and the `**Required:**` heading in §4.1 (neither uses a
modal keyword); the second command finds them (`type: <Type name> #
REQUIRED` and the `**Required:**` bold label), confirming a
declarative-requirement pattern is needed as a second extraction rule.

- [ ] **Step 3: Write the failing check proving naive heading-scanning is unsafe**

```sh
grep -n '^## ' /tmp/spec-gap-report-check/SPEC.md | grep -E '^[0-9]+:## [0-9]{4}-[0-9]{2}-[0-9]{2}'
```

Expected: two matches, `## 2026-05-22` and `## 2026-05-15` — these are
lines *inside* §9's fenced `log.md` worked example, not real spec sections.
A bare `^## ` heading scan (without excluding fenced code blocks) would
misparse them as spec structure.

- [ ] **Step 4: Write the extraction section**

Append to `.claude/skills/spec-gap-report/SKILL.md`:

```markdown
## 2. Extract normative statements

Section-level comparison (§1..§13) is too coarse — a section can be
"mostly implemented" while hiding an unaddressed `MUST NOT`. Extract
**atomic statements** instead, each tagged with its nearest `§`
section/subsection anchor, using two patterns (a pure modal-keyword scan
misses real requirements — verified against the live spec: it silently
drops §11's own three numbered conformance conditions and §4.1's `type`
REQUIRED marker, neither of which uses a modal keyword):

1. **Modal-keyword** sentences/list items containing `MUST`, `MUST NOT`,
   `SHOULD`, `SHOULD NOT`, or `MAY`.
2. **Declarative-requirement** markers: numbered/bulleted items under a
   "conformant... if:" style lead-in (§11's three conditions), and inline
   `REQUIRED` / `**Required:**` labels (§4.1's `type` field).

Extract both patterns from the spec **body only** — exclude anything
inside a fenced code block. §9's own worked `log.md` example embeds lines
like `## 2026-05-22` inside a fenced block; a bare heading/keyword scan
that doesn't exclude fenced ranges will misparse example content as real
spec text. Likewise, when identifying top-level section boundaries, anchor
on `^## [0-9]+\.` (a numbered `##` heading, this spec's actual convention)
and still exclude fenced ranges, for the same reason.

**Collapse duplicates via pointers.** §11 restates rules already stated
elsewhere almost verbatim, sometimes with an explicit forward citation —
e.g. §11 has "MUST treat a bare `verified` mapping as a one-element list
(§5.2)", which restates §5.2's own "A single verifier MAY be written as
one `{ by, at }` mapping without the list dash. Consumers MUST treat a
bare mapping as a one-element list." When a §11 bullet cites another
section in parentheses like this, treat it as a **pointer** to that
section's atomic statement, not a second one — only the pointed-to section
owns the classification, annotated as also gating §11 conformance (so its
severity reflects that gate, instead of creating a duplicate report row at
a different severity than its source).

This only applies when the cited section actually yields an atomic
statement to inherit into. Some §11 bullets cite a section that is
explicitly informative rather than normative — e.g. §11's "SHOULD surface,
not silently drop, a failing attestation (§10.5)" cites §10.5, which the
spec itself labels "This subsection is informative, not normative." A
citation to an informative-only (sub)section is **never** a pointer: it
stays as its own §11 atomic statement, because collapsing it into a
section with nothing extracted from it would silently delete the
statement rather than merely re-file it — exactly the "no silent gaps"
rule this document states under Edge cases.

Worked example: §11 yields roughly eight atomic statements this way —
three declarative conformance conditions, the `REQUIRED`-marker statement
pulled in from §4.1, and the handful of consumer `MUST`/`SHOULD` bullets
that are not pointers to §5 — not one "§11: done" line.
```

- [ ] **Step 5: Re-run the three checks against the newly written prose**

Re-read the appended section and confirm each check from Steps 1–3 is
explicitly addressed by name (three conditions → declarative-requirement
pattern; `REQUIRED` marker → declarative-requirement pattern; fenced
`log.md` dates → fenced-range exclusion). This is a manual proofread, not
a new command — the point is to confirm nothing in Steps 1–3 was left
unaddressed in the prose.

- [ ] **Step 6: Commit**

```sh
git add .claude/skills/spec-gap-report/SKILL.md
git commit -m "feat(skills): add normative-statement extraction rules to spec-gap-report"
```

---

### Task 3: Write the implementation-side verification (Agent fan-out) section

**Files:**
- Modify: `.claude/skills/spec-gap-report/SKILL.md` (append `## 3. Verify against the implementation`)

**Interfaces:**
- Consumes: **atomic statement** and **pointer** from Task 2.
- Produces: the citation-grounding requirement (a quoted code snippet accompanying every `file:line`) that Task 5's report-writing section relies on when describing what a report row contains.

- [ ] **Step 1: Establish a real worked example to ground the section**

Find the actual OKF4net code backing §5.2's bare-`verified`-mapping rule
(the same rule used as the pointer example in Task 2):

```sh
grep -n "ParseVerified" src/OKF4net/Trust.cs
```

Expected: a match around line 30, `public static IReadOnlyList<Stamp>
ParseVerified(YamlValue? value) => value switch`, with a `YamlMapping m =>
[StampFrom(m)],` arm a couple of lines below it — this is the real
citation the section's worked example will use.

- [ ] **Step 2: Write the section**

Append to `.claude/skills/spec-gap-report/SKILL.md`:

```markdown
## 3. Verify against the implementation

For each atomic statement, start from `README.md`'s existing §→type
mapping table (the `## Mapping to the spec` section, headed "Spec section |
Implemented by") as an index into the codebase — e.g. §5 points at
`Frontmatter.Sources`/`Generated`/`Verified`/`TrustTier`/etc. Then actually
read the source (`src/OKF4net/*.cs` and sibling projects as relevant) and
the tests under `tests/OKF4net.Tests/` to see what the code really does —
not just that a plausibly-named type exists.

Given the volume (13 sections, 15+ source files and their tests), fan out
with the `Agent` tool: one agent per spec section (or small group of
adjacent subsections), each given that section's atomic statements plus
the relevant README table row(s), with a prompt shaped like:

> "For each of these OKF spec §N statements: [list], read
> src/OKF4net/<relevant files> and tests/OKF4net.Tests/<relevant files>
> and report, per statement: (1) a status — Implemented / Partial /
> Missing / Diverges / N/A, (2) a file:line citation **with a short
> verbatim quote of the cited code** — do not report a citation you have
> not actually read and quoted, (3) if the statement is a pointer to
> another section per the extraction rules, say so instead of
> re-classifying it."

The quoted-snippet requirement is not optional: a bare `file:line`
reference is cheap for an agent to fabricate without reading the code,
while a quoted snippet gives the synthesizing pass (and the human reading
the final report) a trivial way to sanity-check it's real. Worked example:
for §5.2's "MUST treat a bare mapping as a one-element list," the citation
is `src/OKF4net/Trust.cs:30-34`, quoting:

```csharp
public static IReadOnlyList<Stamp> ParseVerified(YamlValue? value) => value switch
{
    YamlMapping m => [StampFrom(m)],
    YamlSequence seq => seq.Items.OfType<YamlMapping>().Select(StampFrom).ToList(),
    _ => [],
};
```

— which is `Implemented`: a bare mapping (`YamlMapping m`) is normalized
to a one-element list (`[StampFrom(m)]`), matching the MUST.

The main thread (not a subagent) synthesizes all section agents' outputs
into the final report. Use the plain `Agent` tool for this fan-out, never
the `Workflow` orchestration tool — `Workflow` requires explicit per-session
user opt-in that this skill must not assume.
```

- [ ] **Step 3: Verify the worked example against the actual file**

```sh
sed -n '30,34p' src/OKF4net/Trust.cs
```

Expected: output matches the quoted snippet in the section verbatim
(modulo the file having moved — if it doesn't match, update the quoted
snippet and line numbers to the real current content before continuing).

- [ ] **Step 4: Commit**

```sh
git add .claude/skills/spec-gap-report/SKILL.md
git commit -m "feat(skills): add Agent fan-out and citation-grounding rules to spec-gap-report"
```

---

### Task 4: Write the classification section

**Files:**
- Modify: `.claude/skills/spec-gap-report/SKILL.md` (append `## 4. Classify each statement`)

**Interfaces:**
- Consumes: **atomic statement**, **pointer** (Task 2); the citation format from Task 3.
- Produces: the `Status` enum (`Implemented`/`Partial`/`Missing`/`Diverges`/`N/A`) and `Severity` enum (`Critical`/`Major`/`Minor`) that Task 5's report-writing section formats into the actual Markdown table.

- [ ] **Step 1: Establish the two worked examples the section will cite**

The two known intentional divergences already in the codebase:

```sh
grep -n "legacy" src/OKF4net/Validate.cs
```

Expected: matches around lines 274 and 279 —
`diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id,
"body \`# Citations\` is legacy; move provenance to the \`sources\`
frontmatter field"));` and `diagnostics.Add(new Diagnostic(Severity.Warning,
concept.Path, concept.Id, "\`timestamp\` is a legacy field; prefer
\`generated.at\`"));` — both §13.1 fallbacks, both already documented in
`README.md` (the "§13 Changes from v0.1" table row) as intentional,
equally-weighted `Warning`s.

- [ ] **Step 2: Write the section**

Append to `.claude/skills/spec-gap-report/SKILL.md`:

```markdown
## 4. Classify each statement

Each atomic statement (after pointer-collapsing per §2 above) gets:

- **Status**: `Implemented` / `Partial` / `Missing` / `Diverges` / `N/A`
- **If `Diverges`**: sub-classify as **Intentional** (a citation exists in
  `README.md` / `CHANGELOG.md` / `docs/design/*.md` / a code comment
  explaining the choice) or **Undocumented** (a real, unaddressed gap).
  Worked example of **Intentional**: the §13.1 `timestamp`→`generated.at`
  and `# Citations`→`sources` fallbacks, both flagged as equally-weighted
  `Warning`s at `src/OKF4net/Validate.cs:274` and `:279`, and both
  documented in `README.md`'s §13 table row — these are `Diverges` /
  `Intentional`, not `Missing` or undocumented gaps.
- **`N/A`**: for a statement with no meaningful implementation surface for
  a *library* — e.g. §4.2's "Producers SHOULD favor structural markdown
  over freeform prose" is authoring guidance for humans writing bundles,
  not something OKF4net's code can implement or fail to implement. Use
  sparingly — only when there is genuinely no code-level counterpart, not
  as an escape hatch for statements that are merely hard to verify.
- **`Partial` vs. `Diverges`/Undocumented** (the two easiest to confuse):
  `Partial` means the implementation is on a trajectory toward the
  statement but doesn't fully satisfy it yet (parsed but not validated, or
  validated with only a `Warning` where the spec's `MUST` implies harder
  enforcement) — a plausible next PR closes it without changing existing
  behavior. `Diverges`/Undocumented means the implementation actively
  conflicts with the statement, not merely does less of it — closing it
  would require *changing* existing behavior. When in doubt, default to
  `Partial`: `Diverges` is the stronger claim and needs unambiguous
  evidence of conflicting (not just incomplete) behavior.
- **Severity**:
  - **Critical** — breaks §11 conformance (the three declarative
    conditions, the §4.1 `REQUIRED` statement, or a §11 consumer
    `MUST`/`MUST NOT` bullet that is not itself a pointer)
  - **Major** — a `MUST`/`SHOULD` outside §11 whose violation changes
    observable behavior
  - **Minor** — a `MAY`, an edge case, or a cosmetic/informative-only
    divergence
  - `N/A`-status statements carry no severity.
```

- [ ] **Step 3: Sanity-check the worked example against the extraction rule from Task 2**

Confirm the two `Validate.cs` citations really do correspond to the §13.1
statements as classified in `README.md`'s existing §13 table row (read
`README.md`'s `| §13 Changes from v0.1 (legacy fallbacks) | ...` row) — the
worked example must describe real, current behavior, not a stale one.

- [ ] **Step 4: Commit**

```sh
git add .claude/skills/spec-gap-report/SKILL.md
git commit -m "feat(skills): add status/severity classification rules to spec-gap-report"
```

---

### Task 5: Write the report-output section, edge cases, and assemble the full skill

**Files:**
- Modify: `.claude/skills/spec-gap-report/SKILL.md` (append `## 5. Write the report` and `## Edge cases`)

**Interfaces:**
- Consumes: everything from Tasks 1–4 (fetch/version-drift, atomic statements, citation format, status/severity).
- Produces: the complete, final `SKILL.md` — no further tasks append to this file.

- [ ] **Step 1: Write the output and edge-cases sections**

Append to `.claude/skills/spec-gap-report/SKILL.md`:

```markdown
## 5. Write the report

Write a Markdown file at
`docs/spec-conformance/YYYY-MM-DD-okf-spec-gap-report.md` (date = the day
the report is generated), containing, in order:

1. A summary block: the upstream spec version compared against, the
   OKF4net version/commit compared, and counts by status × severity.
2. The version-drift finding from §1 above, if any — first, before any
   section-level detail.
3. Per-section detail: each atomic statement, its status, severity, and
   citation (spec §, `file:line` with the quoted snippet from §3 above,
   and — for `Diverges`/Intentional findings — the doc/comment citing the
   reason).

Do **not** commit the report. Write the file, then tell the user it's
ready and let them decide whether to commit it — consistent with this
project's rule that commits only happen on explicit request.

Never suggest editing `tests/fixtures/` to resolve a finding, even for a
`Diverges` finding that involves v0.1-covered behavior — `CLAUDE.md`
forbids touching those byte-exact golden captures to make a test pass.
Describing a gap is this skill's job; how (or whether) to close one is a
separate human decision.

## Edge cases

- **Spec fetch failure** (both the raw fetch and the `gh api` fallback
  fail): abort with a clear error, no partial report.
- **Spec structure changes** (more/fewer top-level sections than today's
  13, renumbering): §2 above already parses section boundaries dynamically
  (`^## [0-9]+\.`, excluding fenced ranges) rather than assuming exactly
  13 sections, so this stays correct as the spec evolves.
- **A statement with no obvious code location**: report it as `Missing`
  rather than silently skipping it — no silent gaps in the report.
```

- [ ] **Step 2: Proofread the full assembled file**

Read the complete `.claude/skills/spec-gap-report/SKILL.md` top to bottom.
Confirm: the frontmatter `description` still matches what the file
actually does; every `##` section from Tasks 1–5 is present in order (1
Fetch, 2 Extract, 3 Verify, 4 Classify, 5 Write the report, Edge cases);
terminology is consistent throughout (`atomic statement`, `pointer`,
`Status`/`Severity` enum spellings match exactly between §2/§3/§4/§5); no
placeholder text (`TBD`, `TODO`, "add appropriate...") anywhere.

- [ ] **Step 3: Commit**

```sh
git add .claude/skills/spec-gap-report/SKILL.md
git commit -m "feat(skills): add report-output and edge-case handling, complete spec-gap-report skill"
```

---

### Task 6: End-to-end dry run against the real repo

**Files:**
- Create: `docs/spec-conformance/YYYY-MM-DD-okf-spec-gap-report.md`, dated
  the day this task actually runs (the skill's actual output — kept as
  evidence the skill works, not deleted after the dry run). Written here
  as `2026-07-31-...` because that's today; if this task executes on a
  different day (e.g. resumed in a later session), use that day's date
  instead — both in the generated filename and in Step 8's `git add`.
- Modify: `.claude/skills/spec-gap-report/SKILL.md` (only if the dry run surfaces a real problem with the instructions)

**Interfaces:**
- Consumes: the complete skill file from Task 5.
- Produces: nothing further consumed by other tasks — this is the plan's final acceptance gate.

- [ ] **Step 1: Run the skill for real**

Follow `.claude/skills/spec-gap-report/SKILL.md` exactly as written, start
to finish, against the current state of this repo: fetch the live spec,
extract atomic statements, fan out `Agent` calls per section, classify,
and write `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md`.

- [ ] **Step 2: Check acceptance criterion (a) — version-drift detection**

Open the generated report's summary block. Expected: no version-drift
finding (both upstream and OKF4net's latest tracked row are v0.2, per
Task 1 Step 2's result — unless upstream has moved on since, in which case
the finding should be present and correctly framed as §1 describes).

- [ ] **Step 3: Check acceptance criterion (b) — plausible per-section statuses**

Skim the per-section detail. Expected: statuses broadly agree with
`README.md`'s existing §→type mapping table (e.g. §5, §8, §9, §11 should
show mostly `Implemented`, matching the table's claim that those sections
map to real OKF4net types).

- [ ] **Step 4: Check acceptance criterion (c) — known intentional divergences classified correctly**

Find the report rows for the §13.1 `timestamp`→`generated.at` and
`# Citations`→`sources` fallbacks. Expected: both `Diverges` / **Intentional**,
citing `src/OKF4net/Validate.cs:274`/`:279` (or their current line numbers)
and `README.md`'s §13 table row — not `Missing` or `Diverges`/Undocumented.

- [ ] **Step 5: Check acceptance criterion (d) — §11's declarative conditions present**

Find the report rows for §11. Expected: the three numbered conformance
conditions each appear as their own row with a status — proof the
declarative-requirement extraction pattern actually ran (a keyword-only
implementation would silently produce zero rows for them, per Task 2 Step 1).

- [ ] **Step 6: Check acceptance criterion (e) — no duplicate rule at two severities**

Find the report row(s) for the §5.2 bare-`verified`-mapping rule. Expected:
it appears **once**, under §5 (its canonical section), annotated as also
gating §11 conformance — not twice, once under §5 at one severity and
again under §11 at a different severity.

- [ ] **Step 7: Check acceptance criterion (f) — citations are genuine, not fabricated**

Pick 5 report rows spread across different sections and statuses. For
each, run the cited `file:line` (e.g. `sed -n '<line>,<line>p' <cited
file>`) and confirm the quoted snippet in the report matches the real
file content. Expected: all 5 match — this is the check that makes Task
3's quoted-snippet requirement worth anything; a report full of plausible
but unverified citations would pass every other criterion here while
still being untrustworthy.

- [ ] **Step 8: Check acceptance criterion (g) — no fenced example content leaked in as a spec section**

Search the report for any row attributed to `## 2026-05-22` or `##
2026-05-15` (the dates from §9's fenced `log.md` worked example) or any
other row whose "spec section" doesn't correspond to a real `## N.` spec
heading. Expected: none — confirms the fenced-range exclusion from Task 2
actually held when applied to the real spec text, not just in the Task 2
dry run against a hand-picked line range.

- [ ] **Step 9: Fix and re-run if any criterion fails**

If any of (a)–(g) fails, the failure is in the skill's instructions (not
in the report), since the report is only as good as the instructions it
followed. Do not hand-edit the generated report to make it match
expectations — that would validate the skill against a report it didn't
actually produce.

Scope the fix to what actually broke before deciding whether a full
re-run is warranted: a full re-run (repeating Step 1, which fans out
~13 `Agent` calls) is only needed when the fix touches `## 1. Fetch the
current spec` or `## 2. Extract normative statements` — those change
every section's input, so every section could be affected. A fix
localized to `## 3`, `## 4`, or `## 5` (verification, classification, or
report formatting) only needs the affected section(s) re-verified by
hand against the fix, not a full 13-agent re-run. Delete the bad report
only once a fix is in place and (re-)verified.

- [ ] **Step 10: Commit**

```sh
git add docs/spec-conformance/2026-07-31-okf-spec-gap-report.md
git add .claude/skills/spec-gap-report/SKILL.md
git commit -m "test(skills): dry-run spec-gap-report end to end, capture first real report"
```
