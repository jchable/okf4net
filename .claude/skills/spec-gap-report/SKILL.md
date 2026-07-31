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
