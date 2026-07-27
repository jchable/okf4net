# OKF4net Launch Kit

Ready-to-publish drafts for the OKF4net launch. Publication is a manual step — nothing
here posts itself. See the plan (`docs/superpowers/plans/2026-07-27-okf4net-outreach-onramp.md`)
and spec (`docs/superpowers/specs/2026-07-27-communication-plan-design.md`) for the strategy.

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
| J1 | Flagship article | dev.to (canonical → personal site) | `devto-launch-article.md` | [ ] |
| J1 | Republish | Personal site (canonical), Medium (`rel=canonical`) | `devto-launch-article.md` | [ ] |
| J2 | Show HN | news.ycombinator.com | `short-form-posts.md` | [ ] |
| J3 | Reddit | r/dotnet, r/csharp | `short-form-posts.md` | [ ] |
| J4 | Personal post | LinkedIn + Bluesky/Mastodon | `short-form-posts.md` | [ ] |
| J5 | Agents angle | dev.to / micro-blog | `short-form-posts.md` | [ ] |

## Before you publish — placeholders to replace

- `devto-launch-article.md`: set `canonical_url` to the real personal-site URL
  (placeholder `https://REPLACE-WITH-PERSONAL-SITE/okf4net-launch`).
- `short-form-posts.md`: replace `https://dev.to/REPLACE-WITH-ARTICLE-URL` in the Reddit
  and LinkedIn posts with the live dev.to article URL (do this after J1).
- `ecosystem-blurbs.md`: verify the "The week in .NET" submission path before submitting —
  the historical URL now redirects elsewhere; the file flags this.

## Canonical URL rule

The personal site is the canonical source. Set `rel=canonical` on the dev.to and Medium
copies to point at it, to avoid duplicate-content SEO penalties.

## After launch — steady state (Phase 2)

Per the spec: reactivity first (answer every issue/PR/discussion within 24–48h), one light
content piece per week, one ecosystem gesture per month, and a mini-launch post on every
release (via the `release` skill).
