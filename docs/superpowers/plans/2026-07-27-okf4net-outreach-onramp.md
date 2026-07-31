# OKF4net Outreach — Phase 0 Onramp & Content Drafts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the OKF4net repo "launch-ready" for contributors (roadmap, contributor-first README, onramp issues, Discussions) and prepare all launch content drafts, so the maintainer only has to press "publish."

**Architecture:** Two workstreams. (A) **Repo onramp** — durable files committed to the repo (`ROADMAP.md`, README/CONTRIBUTING edits) plus GitHub-side setup (Discussions, labeled issues). (B) **Content drafts** — launch assets stored under `docs/outreach/` (dev.to article, short-form posts, awesome-dotnet/newsletter blurbs) that the maintainer publishes manually. Publication itself is out of scope — this plan only produces repo-ready state and ready-to-paste drafts.

**Tech Stack:** Markdown, GitHub CLI (`gh`), GitHub Projects/Discussions. No application code changes.

## Global Constraints

Copied verbatim from the spec (`docs/superpowers/specs/2026-07-27-communication-plan-design.md`); every task's requirements implicitly include these:

- **Language:** English-first for all public-facing content; French secondary/optional.
- **Editorial rule:** every content piece (1) leads with a concrete benefit, (2) shows 5–10 lines of code or `okf` in action, (3) ends with "open standard from Google + how to contribute." The format is context, never the hook.
- **Two audience angles:** .NET devs (zero-dependency, Native-AOT, BCL-only, independent spec-conformant implementation — NOT a "ported from Rust" hook, see `docs/outreach/README.md`'s note on why that framing was retired) and AI-agent builders (`OKF4net.Agents` + Microsoft Agent Framework, git-native agent memory).
- **No spam:** each post adapted per community; no copy-paste across Reddit/HN.
- **Honesty about AI assistance:** if AI-assisted, state it plainly; do not hide it.
- **License:** LGPL-3.0-or-later; new source files (none expected here) start with the SPDX header.
- **Never touch `tests/fixtures/`.** Do not modify application code in this plan.
- **Accuracy:** "zero third-party runtime dependency" claims must stay true (BCL-only library + CLI; `OKF4net.Agents` references only `Microsoft.Agents.AI`).

---

## File Structure

**Repo onramp (committed):**
- `ROADMAP.md` (create) — public Now/Next/Later roadmap.
- `README.md` (modify) — top-of-file contributor callout + new `## Contributing & roadmap` section before `## Building & testing`.
- `CONTRIBUTING.md` (modify) — add a friendly "Where to start / good first issues" pointer.

**Content drafts (committed under `docs/outreach/`, published manually later):**
- `docs/outreach/README.md` — index + publication checklist for the launch kit.
- `docs/outreach/devto-launch-article.md` — the flagship article (canonical source).
- `docs/outreach/short-form-posts.md` — Show HN, r/dotnet, r/csharp, LinkedIn, micro-blog, agents-angle.
- `docs/outreach/ecosystem-blurbs.md` — awesome-dotnet entry + newsletter submissions.
- `docs/outreach/issues/` — one markdown file per onramp issue draft, filed via `gh`.

**GitHub-side (not files):** Discussions enabled, Projects board, filed issues.

---

## Task 1: Public roadmap (`ROADMAP.md` + Projects board)

**Files:**
- Create: `ROADMAP.md`

**Interfaces:**
- Produces: a `ROADMAP.md` at repo root that README (Task 2) and CONTRIBUTING (Task 3) link to, and that onramp issues (Task 5) map onto.

- [ ] **Step 1: Write `ROADMAP.md`**

Use Now/Next/Later. Keep entries honest and derived from the actual codebase (CLI verbs `validate`/`info`/`index`/`graph`/`parse`/`fmt`, the YAML subset, `OKF4net.Agents`, `OKF4net.Catalog`). Content:

```markdown
# OKF4net Roadmap

OKF4net implements the [Open Knowledge Format (OKF) v0.1](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
on the .NET base class library with zero third-party runtime dependencies.
This roadmap shows where the project is heading. It is a living document —
issues labelled [`good first issue`](https://github.com/jchable/okf4net/labels/good%20first%20issue)
and [`help wanted`](https://github.com/jchable/okf4net/labels/help%20wanted)
are the concrete entry points.

## Now (in progress)

- Broaden test coverage and worked examples for the CLI verbs and the agents layer.
- Documentation: end-to-end tutorials for both audiences (library users, agent builders).

## Next

- More `OKF4net.Agents` samples with Microsoft Agent Framework.
- CLI ergonomics: richer diagnostics and machine-readable (`--json`) output where it aids tooling.
- Performance baselines for large bundle loads.

## Later

- Ecosystem integrations driven by user demand.
- Tracking upstream OKF spec evolution beyond v0.1.

## Out of scope

- Third-party runtime dependencies in the library or CLI (BCL-only is a hard rule).
- Divergence from the OKF v0.1 spec without a documented, cited reason.

## How to influence the roadmap

Open a [Discussion](https://github.com/jchable/okf4net/discussions) or comment on an
existing issue. Roadmap items graduate to labelled issues before work starts.
```

- [ ] **Step 2: Create the GitHub Projects board (manual/CLI)**

Run:

```bash
gh project create --owner jchable --title "OKF4net Roadmap" 2>&1 || echo "Create via web UI: repo → Projects → New project → Board; columns: Now / Next / Later"
```

Expected: a project is created, or the fallback instruction is printed. This step is optional-but-recommended; the board mirrors `ROADMAP.md` columns. Do not block the plan if `gh project` scopes are missing — note it for the maintainer.

- [ ] **Step 3: Verify**

Checklist:
- Every roadmap bullet is truthful against the codebase (no invented features).
- Links to labels and Discussions use the `jchable/okf4net` repo path.
- No third-party-dependency item contradicts the zero-dep rule.

- [ ] **Step 4: Commit**

```bash
git add ROADMAP.md
git commit -m "docs: add public roadmap (Now/Next/Later)"
```

---

## Task 2: Contributor-first README

**Files:**
- Modify: `README.md` — add a callout after the intro blockquote (currently ends near line 23, before `## What OKF is`) and a new `## Contributing & roadmap` section immediately before `## Building & testing`.

**Interfaces:**
- Consumes: `ROADMAP.md` (Task 1), the `good first issue` / `help wanted` labels, Discussions (Task 4).

- [ ] **Step 1: Add the top-of-README contributor callout**

Insert immediately after the intro blockquote (before the `## What OKF is` heading):

```markdown
> **Want to contribute?** OKF4net is a young, welcoming project with a clear
> [roadmap](ROADMAP.md) and issues labelled
> [`good first issue`](https://github.com/jchable/okf4net/labels/good%20first%20issue).
> No prior OKF knowledge required — see [Contributing & roadmap](#contributing--roadmap).
```

- [ ] **Step 2: Add the `## Contributing & roadmap` section**

Insert immediately before `## Building & testing`:

```markdown
## Contributing & roadmap

Contributions are welcome and the barrier to entry is deliberately low — the
library is pure BCL C# with no third-party runtime dependencies, so there is
no framework to learn before you can help.

- **Where the project is going:** [`ROADMAP.md`](ROADMAP.md).
- **Good first issues:** [browse the label](https://github.com/jchable/okf4net/labels/good%20first%20issue)
  — each names the files to touch and the test to make pass.
- **Bigger pieces:** [`help wanted`](https://github.com/jchable/okf4net/labels/help%20wanted).
- **Questions before you code:** open a [Discussion](https://github.com/jchable/okf4net/discussions).
- **How to build, test, and submit:** [`CONTRIBUTING.md`](CONTRIBUTING.md).
```

- [ ] **Step 3: Verify**

```bash
grep -n "Contributing & roadmap" README.md
```
Expected: two matches (the callout anchor link resolves to the section, and the section heading itself). Confirm the anchor `#contributing--roadmap` matches the GitHub-slugged heading. Confirm no claim in the new text overstates the project (it is "young").

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: make README contributor-first (callout + Contributing section)"
```

---

## Task 3: CONTRIBUTING "where to start" pointer

**Files:**
- Modify: `CONTRIBUTING.md` — add a short "Where to start" block after the opening `# Contributing to OKF4net` intro and before `## Prerequisites`.

**Interfaces:**
- Consumes: labels and `ROADMAP.md`. The existing build/test/style/spec-fidelity/PR sections stay as-is (already solid).

- [ ] **Step 1: Add the pointer block**

Insert after the first paragraph, before `## Prerequisites`:

```markdown
## Where to start

New here? Pick an issue labelled
[`good first issue`](https://github.com/jchable/okf4net/labels/good%20first%20issue) —
each is scoped, names the files involved, and states how to verify it. For larger
work see [`help wanted`](https://github.com/jchable/okf4net/labels/help%20wanted)
and the [roadmap](ROADMAP.md). Unsure? Open a
[Discussion](https://github.com/jchable/okf4net/discussions) and we'll help you find
something. Three commands get you building and testing (see below): `dotnet build`,
`dotnet test`, `dotnet format`.
```

- [ ] **Step 2: Verify**

```bash
grep -n "Where to start" CONTRIBUTING.md
```
Expected: 1 match. Confirm the three commands match those documented in the `## Building and testing` section already present.

- [ ] **Step 3: Commit**

```bash
git add CONTRIBUTING.md
git commit -m "docs: add 'Where to start' pointer to CONTRIBUTING"
```

---

## Task 4: Enable GitHub Discussions

**Files:** none (GitHub setting).

**Interfaces:**
- Produces: a Discussions space that Tasks 1–3 already link to. Do this before those links go live to avoid 404s (or accept a short window).

- [ ] **Step 1: Enable Discussions**

Run:

```bash
gh api -X PATCH repos/jchable/okf4net -f has_discussions=true 2>&1 || echo "Fallback: repo → Settings → General → Features → check Discussions"
```
Expected: JSON with `"has_discussions": true`, or the fallback instruction.

- [ ] **Step 2: Seed one welcome discussion**

In the web UI (or `gh`), create an "Announcements" or "General" post titled "Welcome — introduce yourself & ask anything" with 2–3 sentences inviting questions and pointing to `good first issue`. This gives arrivals a non-empty space.

- [ ] **Step 3: Verify**

```bash
gh api repos/jchable/okf4net --jq .has_discussions
```
Expected: `true`.

*(No commit — GitHub-side setting.)*

---

## Task 5: Draft and file 8–12 onramp issues

**Files:**
- Create: `docs/outreach/issues/*.md` (one file per issue draft).

**Interfaces:**
- Consumes: `ROADMAP.md` themes.
- Produces: filed GitHub issues carrying `good first issue` or `help wanted` labels, linked from README/CONTRIBUTING/ROADMAP.

**Issue draft template** (every file uses this shape):

```markdown
### Title: <imperative, specific>
**Labels:** good first issue | help wanted ; documentation | enhancement | test as appropriate
**Difficulty / est. effort:** <e.g. ~1h, small>

**Context:** <1–2 sentences: why this matters, spec § if relevant>
**Files to touch:** `exact/path`, `exact/path`
**What to do:** <numbered, concrete steps>
**How to verify:** <exact command, e.g. `dotnet test --filter ...`, and expected result>
**Good to know:** <link to CONTRIBUTING, relevant README section>
```

- [ ] **Step 1: Write the candidate backlog**

Create one file per issue under `docs/outreach/issues/`. Aim for 8–12: roughly 6 `good first issue` (small, self-contained: doc additions, an example bundle, a focused test, a clearer error message, an XML-doc gap, a README usage snippet) and 3–4 `help wanted` (a `--json` output option for a CLI verb, an added `OKF4net.Agents` sample, a performance baseline/benchmark, an added golden-parity test case). Each MUST:
- Name real files (verify the path exists before writing the issue).
- State a verification command that actually runs (`dotnet test --filter ...`, `dotnet build`, `okf <verb> ...`).
- Respect hard rules: no new runtime dependency; never modify `tests/fixtures/`; cite the spec § for behavioural items.

Do NOT invent features that violate spec fidelity or the zero-dependency rule. If unsure a candidate is valid, drop it — fewer, correct issues beat padding.

- [ ] **Step 2: Self-check each draft**

Checklist per file:
- File paths exist (`ls`/Glob confirmed).
- Verification command is copy-pasteable and real.
- Scope is genuinely one sitting for a newcomer (good-first) or clearly bounded (help-wanted).
- No overlap/duplication between issues.

- [ ] **Step 3: Commit the drafts**

```bash
git add docs/outreach/issues/
git commit -m "docs: draft onramp issues (good first issue / help wanted)"
```

- [ ] **Step 4: File the issues on GitHub**

For each draft, run (example shown; repeat per file):

```bash
gh issue create --repo jchable/okf4net \
  --title "<title>" \
  --body-file docs/outreach/issues/<file>.md \
  --label "good first issue" 2>&1
```
Expected: each prints a new issue URL. Add `--label "help wanted"`, `--label "documentation"`, etc. as the draft specifies. After filing, confirm:

```bash
gh issue list --repo jchable/okf4net --state open --label "good first issue"
```
Expected: the good-first issues listed. This is the go/no-go gate for launch — the spec requires the onramp exist before any post.

---

## Task 6: Ecosystem blurbs (awesome-dotnet + newsletters)

**Files:**
- Create: `docs/outreach/ecosystem-blurbs.md`.

**Interfaces:**
- Consumes: the positioning from Global Constraints. Produces ready-to-submit text; submission is manual.

- [ ] **Step 1: Write the awesome-dotnet entry**

In `docs/outreach/ecosystem-blurbs.md`, include the exact one-line entry in awesome-dotnet's format, and note the target category (likely "Miscellaneous" or a docs/knowledge category) plus the fork→edit→PR steps and the list's contribution guidelines URL to check first:

```markdown
## awesome-dotnet

**Target:** https://github.com/quozd/awesome-dotnet (verify contribution guide first)
**Category:** Miscellaneous (or best-fit)
**Entry line:**
* [OKF4net](https://github.com/jchable/okf4net) - Zero-dependency .NET implementation of Google's Open Knowledge Format (OKF): knowledge as a directory of markdown files with YAML frontmatter; library + AOT CLI + Microsoft Agent Framework tools.
**Steps:** fork → add line alphabetically in the chosen section → PR referencing the guideline checklist.
```

- [ ] **Step 2: Write newsletter submission blurbs**

Add short (2–3 sentence) blurbs and submission targets for `.NET Weekly` (dotnetweekly.com) and `The week in .NET` (via a blog/issue submission or the .NET blog community links), each leading with a benefit and linking the repo. Note: re-submit on each release as a fresh item.

- [ ] **Step 3: Verify & commit**

Checklist: entry matches awesome-dotnet's exact bullet format; links resolve; claims accurate. Then:

```bash
git add docs/outreach/ecosystem-blurbs.md
git commit -m "docs: add ecosystem submission blurbs (awesome-dotnet, newsletters)"
```

---

## Task 7: Flagship dev.to launch article (draft)

**Files:**
- Create: `docs/outreach/devto-launch-article.md`.

**Interfaces:**
- Consumes: Global Constraints (editorial rule, .NET angle lead). Produces the canonical article body reused by Task 8 short posts and republished (canonical) to the personal site and, secondarily, Medium.

- [ ] **Step 1: Draft the article to this spec**

Write a complete ~1,200–1,800-word article in `docs/outreach/devto-launch-article.md`. It is a real draft, not an outline — write the prose. Required structure and content:

- **Front matter** (dev.to format): `title`, `published: false`, `tags: dotnet, csharp, opensource, ai`, `canonical_url` placeholder pointing to the personal site.
- **Title:** benefit-led, e.g. *"OKF4net: a zero-dependency .NET toolkit for knowledge bundles you can `cat` and `git clone`."* **Not** a "ported from Rust" hook — `docs/outreach/README.md` already retired that framing (the two drafts built around it were deleted); OKF4net is presented as an independent implementation of the OKF spec.
- **Hook (1–2 paragraphs):** the concrete benefit — knowledge bundles you can `cat` and `git clone`, zero dependencies — before naming OKF.
- **What OKF is (short):** 3–4 sentences, link the spec (Google's `GoogleCloudPlatform/knowledge-catalog`). Format is context, not the pitch.
- **Engineering rigor as the credibility signal:** zero third-party runtime dependencies (a hand-rolled YAML-subset parser and CLI arg parsing, not a shortcut), Native AOT, an extensive test suite with byte-exact golden CLI fixtures locking in behavior. Pull the current test count from the repo when drafting — do not reuse an old number. If AI-assisted, say so plainly here.
- **Show, don't tell:** one 5–10 line library snippet (load a bundle, validate) and one `okf` CLI example, taken from the real README `## Usage` section so they compile/run.
- **The agents angle (short section):** `OKF4net.Agents` — git-native, human-readable agent memory vs opaque vector store; one small snippet.
- **Design choices worth a paragraph:** zero-dependency, Native AOT, BCL-only YAML subset.
- **Call to contribute (closing):** link `good first issue`, `ROADMAP.md`, Discussions; explicitly say newcomers welcome, no OKF background needed. LGPL-3.0-or-later noted.

- [ ] **Step 2: Verify against the editorial rule**

Checklist:
- Leads with benefit, not with "OKF is a format…".
- Contains at least one real, correct code snippet copied from README usage.
- Ends with a concrete contribute CTA (labels + roadmap + discussions).
- All technical claims verifiable (dependency counts, test counts) against README/repo.
- English. AI-assistance disclosed if applicable.

- [ ] **Step 3: Commit**

```bash
git add docs/outreach/devto-launch-article.md
git commit -m "docs: draft dev.to launch article"
```

---

## Task 8: Short-form launch posts (draft)

**Files:**
- Create: `docs/outreach/short-form-posts.md`.

**Interfaces:**
- Consumes: the flagship article (Task 7) for links and framing. Each sub-post is adapted per community (no copy-paste — Global Constraints).

- [ ] **Step 1: Draft every launch post in one file, clearly sectioned**

Write complete, ready-to-paste drafts (not outlines) for each, with a one-line note on timing (J1–J5 per spec) and the target URL/subreddit:

- **Show HN (J2):** a title (`Show HN: OKF4net – zero-dependency .NET impl of Google's Open Knowledge Format`) + a first-comment body (3–5 sentences: what it is, why you built it, what's interesting technically, invitation for feedback). Links the repo, not the article. Note: stay available 2–3h to reply.
- **r/dotnet + r/csharp (J3):** a .NET-technical framing (zero-dep/AOT), title + body, links the article; a line reminding to engage honestly and follow each subreddit's self-promo rules.
- **LinkedIn (J4):** a personal/narrative post ("why I built this"), benefit-led, 120–200 words, English, with 3–5 hashtags.
- **Micro-blog thread — Bluesky/Mastodon (J4):** a 3–5 post thread, each ≤300 chars, first post is the hook, last post is the contribute CTA.
- **Agents-angle post (J5):** short dev.to note or micro-blog post targeting AI-agent builders, leading with git-native agent memory, linking `OKF4net.Agents` docs.

Each section MUST respect the editorial rule and be genuinely distinct in wording per platform.

- [ ] **Step 2: Verify**

Checklist:
- No two posts are copy-paste identical.
- Each leads with a benefit and has a clear single CTA.
- Character limits respected (Bluesky ~300, Mastodon default 500 — target ≤300 to be safe).
- Links correct (HN → repo; Reddit/LinkedIn → article).

- [ ] **Step 3: Commit**

```bash
git add docs/outreach/short-form-posts.md
git commit -m "docs: draft short-form launch posts (HN/Reddit/LinkedIn/micro-blog/agents)"
```

---

## Task 9: Launch-kit index & publication checklist

**Files:**
- Create: `docs/outreach/README.md`.

**Interfaces:**
- Consumes: all prior drafts. Produces the maintainer's single "press publish" runbook.

- [ ] **Step 1: Write the index + checklist**

`docs/outreach/README.md` lists each asset with its file link, target platform, scheduled day (J1–J5), and a checkbox. Include the go/no-go gate ("Phase 0 onramp complete: ROADMAP live, ≥6 good-first issues filed, Discussions on") at the top, and the canonical-URL reminder (personal site is canonical; dev.to and Medium set `rel=canonical` to it).

```markdown
# OKF4net Launch Kit

**Do not publish anything until the onramp gate passes:**
- [ ] `ROADMAP.md` live
- [ ] ≥ 6 `good first issue` + ≥ 3 `help wanted` filed
- [ ] GitHub Discussions enabled with a welcome post
- [ ] README contributor callout + section merged

## Publication sequence
| Day | Asset | Platform | File | Done |
|-----|-------|----------|------|------|
| J1 | Flagship article | dev.to (canonical → personal site) | devto-launch-article.md | [ ] |
| J1 | Republish | Personal site (canonical), Medium (rel=canonical) | devto-launch-article.md | [ ] |
| J2 | Show HN | news.ycombinator.com | short-form-posts.md | [ ] |
| J3 | Reddit | r/dotnet, r/csharp | short-form-posts.md | [ ] |
| J4 | Personal post | LinkedIn + Bluesky/Mastodon | short-form-posts.md | [ ] |
| J5 | Agents angle | dev.to / micro-blog | short-form-posts.md | [ ] |
| Pre-J1 | awesome-dotnet PR + newsletters | GitHub / newsletters | ecosystem-blurbs.md | [ ] |

## Canonical URL rule
Personal site is the canonical source. Set `rel=canonical` on dev.to and Medium
copies to it to avoid duplicate-content SEO penalties.
```

- [ ] **Step 2: Verify & commit**

Checklist: every asset file referenced exists; the gate matches Task 5's go/no-go. Then:

```bash
git add docs/outreach/README.md
git commit -m "docs: add launch-kit index and publication checklist"
```

---

## Self-Review (completed by plan author)

**1. Spec coverage:**
- §5 Positioning → embedded in Global Constraints + Tasks 7/8. ✓
- §6 Phase 0 onramp (README, ROADMAP, 8–12 issues, CONTRIBUTING, Discussions, awesome-dotnet) → Tasks 1,2,3,4,5,6. ✓
- §7 Phase 1 launch drafts (dev.to, HN, Reddit, LinkedIn, micro-blog, agents, Medium canonical) → Tasks 7,8,9. ✓
- §7 newsletters filet → Task 6. ✓
- §8/§9/§10 (maintenance cadence, calendar, guardrails) → intentionally out of this plan; they are recurring manual actions, not repo deliverables. Publication itself is out of scope by user decision.

**2. Placeholder scan:** No "TBD/TODO". Content tasks (5,7,8) specify concrete structure, required inclusions, and acceptance checklists rather than shipping final prose inside the plan — appropriate for creative drafts; the deliverable is the drafted file, verified by checklist.

**3. Consistency:** Repo path `jchable/okf4net` used throughout; label names match the repo (`good first issue`, `help wanted`); `docs/outreach/` paths consistent across Tasks 5–9; canonical-URL rule stated identically in Tasks 7 and 9.

**Note on granularity:** Content drafts (Tasks 7,8) do not follow a TDD red/green cycle — none applies to prose. Their "test" is the editorial-rule acceptance checklist plus factual verification against the repo.
