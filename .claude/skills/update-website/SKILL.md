---
name: update-website
description: >
  Sync the OKF4net project website (web/, a Vite + React SSG app deployed to
  jchable.github.io/okf4net) with the current state of the project — new
  features, new src/ projects, CHANGELOG entries, roadmap movement, spec
  coverage. Use this whenever the user asks to update, refresh, sync, or
  "catch up" the website/site/web app with the project ("mets à jour le
  site", "le site n'est plus à jour", "ajoute ça sur le site", "sync web/
  avec la dernière release"), after a release ships and the site should
  reflect it, after a notable feature or new project lands under src/, or
  when the user just wants a staleness check on the site content — even if
  they only mention one page or one fact (e.g. "the CLI page still shows the
  old commands").
---

# Updating the OKF4net website

The site (`web/`) is a **friendlier restatement** of facts that already live
elsewhere in the repo — it must never say something the code, `README.md`,
`CHANGELOG.md`, or `ROADMAP.md` don't already support. Your job is to find
where the site has drifted from those sources and bring it back in sync, not
to invent new claims.

## 1. Gather ground truth

Read, in this order:

- `CHANGELOG.md` — the `[Unreleased]` section plus the most recent released
  version: this is the most reliable signal for "what's new."
- `README.md` — technical reference, including the spec-section → type
  mapping table; the site's `docs/spec` page should track it.
- `ROADMAP.md` — Now/Next/Later/Out of scope; anything the site claims is
  "coming soon" should agree with this, and anything that graduated from
  Next to Now (or shipped) should move too.
- `git log --oneline -- src/` since roughly the last time `web/` content was
  touched (`git log -1 --format=%H -- web/src/pages web/src/content` gives
  you that point) — this surfaces new projects under `src/` (new CLI verbs,
  new public API, a new `OKF4net.*` project) that may not have prose yet.
- `docs/design/` — historical specs/plans. Treat as context only, per this
  repo's rule that README/CHANGELOG are authoritative; don't cite a design
  doc as if it were shipped behaviour.

## 2. Where staleness actually hides

Check each of these against what you found in step 1 — don't assume "the
site looks fine" without checking the specific spot:

- **Version strings.** Any literal version number on the site
  (`web/src/pages/docs/Cli.tsx`'s `versionHtml` sample is the known one —
  search the whole tree for others: `git grep -rn "<old-version>" -- web/`)
  must match the latest tag, not a stale one. This is the same check the
  `release` skill runs from the other direction; if a release just shipped
  and the site wasn't part of that change, this is almost always the
  first thing that's wrong.
- **Project/feature roster.** `web/src/pages/Home.tsx` and `Library.tsx`
  describe `src/` projects and their capabilities in prose and tables. If a
  new project appeared under `src/` (check `git ls-tree -d main -- src`)
  or an existing one gained a documented capability, confirm it's
  represented — or explicitly decide it's too new/internal to surface yet.
- **CLI reference.** `Home.tsx`'s command table and `docs/Cli.tsx` must
  match the verbs `OkfCli.Run` actually implements
  (`src/OKF4net.Cli/OkfCli.cs`) — no missing verbs, no removed ones lingering.
- **Docs sidebar vs. docs bundle.** `web/src/content/docs.ts`'s `docsTree`
  must match what's under `docs/` (slugs, order, `soon` flags) — a page
  that shipped should lose its `soon` marker; a page that's gone shouldn't
  still be linked.
- **Spec coverage table.** `docs/Spec.tsx` (or wherever the § → type mapping
  lives) should track README's mapping table — if README gained a row for a
  new spec section, the site should too.
- **Roadmap-driven copy.** Any "coming soon" / "in progress" language on the
  site should agree with `ROADMAP.md`'s current Now/Next/Later buckets.

If you're unsure whether something counts as "notable enough" to add to the
site, ask the user rather than guessing — the site is public-facing and
represents the project's stated capabilities.

## 3. Edit

All of `web/src/pages/**`, `web/src/content/docs.ts`, and shared components
(`SiteBar`, `Colophon`, `web/src/components/doc/*.tsx`) are in scope, but
prefer the narrowest edit that fixes the drift — touch shared components
only when the content change genuinely requires a structural one (e.g. a
new docs page needs a new sidebar entry, not a new component). Match the
existing voice: technical, terse, spec-section-annotated (`§4, §6` style
refs next to headings), no marketing fluff — read a neighboring section
before writing prose so the new copy doesn't stand out.

## 4. Verify

The site is a real build, not just prose — prove it still builds and its
tests still pass before handing back:

```sh
cd web
npm run typecheck
npm run test
npm run build
```

`npm run test` runs `doc.test.tsx` and friends via vitest; `npm run build`
runs the full `vite-react-ssg build` (catches broken routes/imports that
typecheck alone can miss). Fix failures before considering the update done.

## 5. Hand back, don't ship

Show the user the diff (`git status` / `git diff -- web/`) and a short list
of what changed and why, tied back to the CHANGELOG/README/ROADMAP facts
that justified each edit. Don't commit or push — per this repo's normal
rules, that's the user's call, and `web/` changes only deploy on push to
`main` (`.github/workflows/pages.yml`) so there's no urgency to short-circuit
review. If the user wants it committed, a plain
`chore(web): sync site with vX.Y.Z` (or similarly scoped) message matching
the `release` skill's commit style is a reasonable default — ask if several
unrelated updates got bundled and a split makes more sense.
