---
name: spec-gap-report
description: >
  Generate a detailed Markdown report of gaps between the current upstream
  OKF spec (GoogleCloudPlatform/knowledge-catalog, okf/SPEC.md) and the
  OKF4net implementation, with a severity per gap and documented/intentional
  divergences called out separately from real gaps. Explicit-invocation-only
  skill (`/spec-gap-report`); a deliberately heavyweight, on-demand audit.
disable-model-invocation: true
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
three declarative conformance conditions plus five consumer `MUST`/`SHOULD`
bullets that are not pointers to another section — not one "§11: done"
line. (§4.1's `REQUIRED`-marker statement is a separate atomic statement
filed under §4, its own section — see §4's Critical severity rule below for
why it still gates §11 conformance; §11 does not duplicate it into its own
count.)

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
is `src/OKF4net/Trust.cs:30-35`, quoting:

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
    `MUST`/`MUST NOT` bullet that is not itself a pointer). This also
    covers a statement in its own (non-§11) section that is the target of
    a §11 pointer per §2 above — it takes Critical severity because it
    gates §11 conformance, even though the section it's filed under is
    outside §11.
  - **Major** — a `MUST`/`SHOULD` outside §11 whose violation changes
    observable behavior
  - **Minor** — a `MAY`, an edge case, or a cosmetic/informative-only
    divergence
  - `N/A`-status statements carry no severity.

## 5. Write the report

Before writing the report, spot-check the citations: re-read 3-5 cited
`file:line` ranges spread across different sections and confirm the quoted
snippet in each matches the real file at that location. This is what makes
the §3 quoted-snippet requirement actually load-bearing — a snippet that's
never checked back against the source is no better than a bare citation.
Correct or drop any citation that doesn't check out before finalizing the
report.

Write a Markdown file at
`docs/spec-conformance/YYYY-MM-DD-okf-spec-gap-report.md` (date = the day
the report is generated), containing, in order:

1. A summary block: the upstream spec version compared against, the
   OKF4net version/commit compared, counts by status × severity, and every
   (sub)section that yielded zero atomic statements under §2's two-pattern
   rule — so a reader can tell "nothing normative here by design" apart
   from "not examined."
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
