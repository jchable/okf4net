# Ecosystem submission blurbs

Draft copy for submitting OKF4net to community lists and newsletters. Facts
below are checked against `README.md` as of this writing:

- Zero third-party runtime dependencies in `src/OKF4net/` and
  `src/OKF4net.Cli/` (BCL only — own YAML-subset parser, link scanner, CLI
  arg parsing).
- Library + a `okf` CLI (`validate`/`audit`/`verify`/`info`/`index`/`graph`/
  `parse`/`fmt`/`render`), published Native AOT, self-contained, single-file.
- `src/OKF4net.Agents/` is a separate package exposing bundle operations as
  Microsoft Agent Framework tools (`OkfBundleTools`, twelve `AITool`s) plus
  `OkfContextProvider`; it is the only project depending on
  `Microsoft.Agents.AI`.
- Implements Google's Open Knowledge Format (OKF) v0.1: a bundle is a
  directory tree of markdown files with YAML frontmatter.

---

## awesome-dotnet

**Target:** https://github.com/quozd/awesome-dotnet (verify contribution
guide before submitting — checked 2026-07-27, summarized below; guidelines
can change)

**Category:** `Misc` — best fit. The list has no dedicated "knowledge
management," "data formats," or "documentation" category that OKF4net cleanly
belongs to; adjacent sections (`Documentation`, `Markdown Processors`,
`Serialization`, `Configuration`) are all narrower than what the library
does. `Misc` is where comparably general-purpose libraries/tools land.

**Entry line** (alphabetical under `O` in the `Misc` section):

```markdown
* [OKF4net](https://github.com/jchable/okf4net) - Zero-dependency .NET implementation of Google's Open Knowledge Format (OKF): knowledge as a directory of markdown files with YAML frontmatter; library + Native AOT CLI + Microsoft Agent Framework tools + local catalog + MCP server.
```

This keeps the task-provided wording as-is — it checks out against the
README (zero-dependency claim, directory-of-markdown-with-YAML-frontmatter
description, library + AOT CLI + Agent Framework tools layer are all
verified above).

**Contribution rules** (from `CONTRIBUTING.md` in the target repo, fetched
2026-07-27 — re-verify before opening a PR in case they've changed):

- Entry format: `[LIBRARY](LINK) - DESCRIPTION`, concise description.
- **Submit one link per pull request**, unless multiple entries belong to
  the same category.
- Include a link to the added project in the PR description.
- Quality bar the maintainers apply: "generally useful to the community,"
  "actively maintained," "stable," "documented," and has tests — OKF4net
  qualifies (a comprehensive test suite with byte-exact golden CLI parity
  per README, CI on three OSes, `dotnet format --verify-no-changes` gate).
- Tags like `**[Research]**`, `**[$]**`, `**[Proprietary]**`,
  `**[Free for OSS]**` exist for special cases; none apply here (OKF4net is
  LGPL-3.0-or-later, free, not a research project).

**Steps:**

1. Fork `quozd/awesome-dotnet`.
2. Add the entry line alphabetically (by project name, "OKF4net" → under
   `O`) inside the `Misc` section.
3. Open a PR with the entry link in the PR description, following the
   one-link-per-PR rule above.
4. Re-read `CONTRIBUTING.md` in the fork at PR time — list contribution
   guides do get updated; the summary above may drift.

---

## Newsletter submission blurbs

Blurb text is deliberately similar between targets (same facts, light
rewording) — reuse whichever fits the venue's tone.

### .NET Weekly (dotnetweekly.com)

> OKF4net brings Google's Open Knowledge Format to .NET as a
> zero-dependency library: parse, validate, cross-link, and index a bundle
> of markdown + YAML-frontmatter files with nothing but the BCL. It ships a
> Native AOT `okf` CLI (`validate` exits non-zero on non-conformance, so it
> drops straight into CI) and a Microsoft Agent Framework tools layer that
> lets an AI agent read, search, and write to a bundle as its own
> file-based memory. https://github.com/jchable/okf4net

**Submission mechanism (verified 2026-07-27):** dotnetweekly.com has an
"Add a link" control on the homepage that routes to `/login` — the site's
own text says "Once subscribed you can login, submit a link and receive the
weekly newsletter." So the path is: subscribe → log in → use "Add a link"
to submit the repo URL yourself. No separate email or form was found; this
in-site submission flow is the only mechanism observed.

### The week in .NET

**Submission mechanism — could not fully verify, treat as unreliable:**

- The most recent explainer post found
  (`devblogs.microsoft.com/dotnet/the-week-in-net-links/`) points
  contributors to a submission form at `weekindotnet.azurewebsites.net`
  ("add your posts, it takes only a second"). As of 2026-07-27 that URL
  **301-redirects to an unrelated site** (`aspnetcore.news`), so this
  submission form is dead.
- The `week-in-net` tag archive on the .NET Blog
  (`devblogs.microsoft.com/dotnet/tag/week-in-net/`) shows its most recent
  entries dated 2017; no evidence of the series continuing past that was
  found. It may be discontinued, or simply not indexed the way I searched
  for it.
- **Verify submission path** before relying on this target: check
  `devblogs.microsoft.com/dotnet` directly for whether "Week in .NET" (or a
  successor community-roundup post) is still being published, and if so,
  what current post says about submitting links (comments, a live form, or
  a named contact). Do not use the `weekindotnet.azurewebsites.net` link
  above — it no longer goes anywhere relevant.

> Draft blurb, ready once a live submission path is confirmed:
>
> OKF4net is a from-scratch, zero-dependency .NET port of Google's Open
> Knowledge Format (OKF v0.1) — treat a directory of markdown + YAML files
> as a queryable, cross-linked knowledge bundle. It ships a Native AOT
> `okf` CLI (`validate`/`audit`/`verify`/`info`/`index`/`graph`/`parse`/`fmt`/
> `render`) and a Microsoft Agent Framework tools layer for agent-native
> read/write access to the bundle. https://github.com/jchable/okf4net

---

## Re-submission note

Both newsletters are weekly, item-of-the-week formats — a submission isn't
a standing listing. **Re-submit the relevant blurb as a fresh item on every
OKF4net release** (update the one-line hook if the release adds a
newsworthy capability, e.g. a new CLI verb or package), rather than
expecting one submission to carry forward.
