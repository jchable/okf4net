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
