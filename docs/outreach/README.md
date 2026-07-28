# OKF4net Launch Kit

Ready-to-publish drafts for the OKF4net launch. Publication is a manual step — nothing
here posts itself. See the plan (`docs/superpowers/plans/2026-07-27-okf4net-outreach-onramp.md`)
and spec (`docs/superpowers/specs/2026-07-27-communication-plan-design.md`) for the strategy.

> **Note:** `devto-launch-article.md` and `short-form-posts.md` were removed — both were
> built around a "ported from Rust" launch narrative that no longer fits the project
> (OKF4net is now framed as an independent .NET implementation of the OKF spec, not a
> Rust port). The publication sequence below needs a new flagship-article draft with an
> updated angle before the J1–J5 sequence can proceed; `ecosystem-blurbs.md` (no Rust
> framing) is still valid as-is.

## Onramp gate (Phase 0) — status

Do not publish anything until the onramp is in place. Current state:

- [x] `ROADMAP.md` live (repo root)
- [x] README contributor callout + `## Contributing & roadmap` section merged
- [x] `CONTRIBUTING.md` "Where to start" pointer merged
- [x] GitHub Discussions enabled with a welcome post (discussions/4)
- [x] Onramp issues filed: **6 `good first issue` + 4 `help wanted`** (#5–#14)
- [ ] **Maintainer follow-up:** create the GitHub Projects board (Now/Next/Later) — the
  automation token lacked the `project` scope. Run `gh auth refresh -s project,read:project`
  then `gh project create --owner jchable --title "OKF4net Roadmap"`, or make it in the web UI.

## Publication sequence

| Day | Asset | Platform | Source file | Done |
|-----|-------|----------|-------------|------|
| Pre-J1 | awesome-dotnet PR + newsletter submissions | GitHub / newsletters | `ecosystem-blurbs.md` | [ ] |
| J1 | Flagship article | dev.to (canonical → personal site) | *(needs a new draft — independent-implementation angle)* | [ ] |
| J1 | Republish | Personal site (canonical), Medium (`rel=canonical`) | *(same, once drafted)* | [ ] |
| J2 | Show HN | news.ycombinator.com | *(needs a new draft)* | [ ] |
| J3 | Reddit | r/dotnet, r/csharp | *(needs a new draft)* | [ ] |
| J4 | Personal post | LinkedIn + Bluesky/Mastodon | *(needs a new draft)* | [ ] |
| J5 | Agents angle | dev.to / micro-blog | *(needs a new draft)* | [ ] |

## Before you publish — placeholders to replace

- `ecosystem-blurbs.md`: verify the "The week in .NET" submission path before submitting —
  the historical URL now redirects elsewhere; the file flags this.

## Canonical URL rule

The personal site is the canonical source. Set `rel=canonical` on the dev.to and Medium
copies to point at it, to avoid duplicate-content SEO penalties.

## After launch — steady state (Phase 2)

Per the spec: reactivity first (answer every issue/PR/discussion within 24–48h), one light
content piece per week, one ecosystem gesture per month, and a mini-launch post on every
release (via the `release` skill).
