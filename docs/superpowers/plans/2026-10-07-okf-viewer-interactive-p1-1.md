# Interactive viewer — P1.1 (mockup fidelity) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring every page `okf-render` generates to the validated mockups (A, the palette of C, and the header of B) — header of the three views, centre column (breadcrumb, de-duplicated H1, chips, collapsible frontmatter), explorer with shapes, type chips and legend, current section and glyphs in the context panel, the palette as drawn in C, embedded fonts — and lay the shared plumbing (index v2, `OkfShapes`, script table, merge markers, harness loader, tooled recette) that the P2 and P3 plans build on in parallel.

**Architecture:** The C# side stays a pure projection plus one writer: `SiteIndex`/`IndexScript` emit index v2 (`types`, `typeIndex`, `description`), `SiteModel` adds a page head model, `DisplayBody` (duplicate H1 removed) and the bundle name and graph page name, and `HtmlWriter` gets one document-start/header method for the three views, a script table with merge markers, and writes every embedded asset generically (fonts included). The browser side gains `okf-shapes.js` (the only module that draws a type, trust, staleness or ghost shape) and `okf-page.js` (glyphs, stale chip, "Show all"), and revises the explorer, palette, contents and theme scripts. `viewer.js` is not touched. Guards: xunit for the C#; the jsdom harness (extended with a slice-case loader and a generalized chrome-class guard) for the JS; a tooled Playwright recette, tracked in the repo and run by hand, for layout, fonts and contrast.

**Tech Stack:** C# 14 / .NET 10, xunit, BCL only (`OKF4net.Viewer`); plain ES2018 classic scripts; Node 22 + jsdom 29 (`tools/viewer-security-check/`); Playwright resolved at run time for the recette only (never a repo dependency).

**Spec:** `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`, revision 6 (commit `3f34890`), brought in line with revision 7 (commit `f84c990`) by this plan's r2. This plan implements slice **P1.1** (§9): everything §11 marks "P1.1", §12.0–§12.3 and §12.6–§12.8 as owned by P1.1, the P1.1 parts of §3 (§3.3 types, §3.4 collisions, §3.6 measurement), §4.1, §4.5, §6, §7 (controls 10, 11, 14 and the xunit list), §8 (explorer chips, Filters, theme) and arbitrations A15–A30. P2 (local graph) and P3 (global graph) are separate plans written in parallel from the same spec; this plan creates **no** P2 or P3 file and only lays their merge markers.

## Revision history

- **r1 (2026-10-07)** — first version, written in parallel with the P2 and P3 plans from spec revision 6.
- **Revision r2 (2026-10-07): applied pre-flight findings F1, F2, F3, X11, X22, X23 and the cosmetic ones; aligned with spec revision 7 (`f84c990`).**
  - F1: Task 1 is already implemented on `p11/t01-loader` (status note added to Task 1, including what the executor added beyond the plan: the static anchor scan and its `CHROME_ANCHORS` list, the injected probe tree, the async-`register` refusal, duplicate case names, the four new probe rows); Task 3 Step 4 and Task 11 Step 4 now start from the anchored rules commit `84588e4` left; Task 3 and Task 12 add the two anchors their new selectors need to `CHROME_ANCHORS`, and Task 3 anchors its `.okf-chip-type svg` rules.
  - F2: Task 11 changes the P1 hostile-title case's selector to `img, script, iframe, object, svg:not(.okf-glyph)`; Tasks 13 and 14 do not touch that line.
  - F3: Task 13 no longer calls `expectVisible(new Set(), "")`.
  - X11: the recette driver (Task 9) reports `pass: null` as "n/a", printed and not counted; Step 5 proves it.
  - X22: Task 17 updates the root `README.md` (fonts next to `marked`, local graph and graph page).
  - X23: Task 16 also updates Task 2's asset-order test.
  - X20: Task 0 gains the controller step that applies P2's documentation hand-off after P2 and P3 merge.
  - Cosmetic: test counts (Task 4: 12 `SiteIndexTypesTests`; Task 11: 27 `HtmlWriterHeaderTests`), Task 11's `GuardNoCaseCollisions` instruction ("the whole method"), `ViewerFontsTests`' order check now asserts something.

## Global Constraints

- Branch: `feat/viewer-interactive-p1` (P1, P1.1, P2 and P3 accumulate there, one PR #176 at the end, A20). Parallel tasks run in their own worktrees and branches and the controller merges them back (Task 0). Stage files by name; never `git add -A`.
- **No change to any public API of `src/OKF4net/`** (spec §2.1). If a task seems to need one, stop and ask: it is a spec change.
- **`viewer.js` is not modified** (spec §4.1).
- No new `PackageReference`; no new JS dependency (the harness keeps `jsdom` only; Playwright is resolved at run time by the recette, never added to `package.json`).
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later` (C# and JS). File-scoped namespaces, XML doc comments on public API, nullable enabled, `TreatWarningsAsErrors`. `dotnet format OKF4net.sln --verify-no-changes` must pass after every task.
- Classic scripts only, never `type="module"`. Each script is an IIFE that checks its globals (`OkfSite`, `OkfShapes`, index) and returns without error when one is missing (§12.6).
- Bundle text reaches the DOM through `textContent`, or `setAttribute` on a fixed attribute name; never `innerHTML`, never a markup string. Every attribute written in C# is double-quoted and passed through `HtmlEscape` (§12.3).
- No object keyed by a bundle string in the index or in JS: arrays, fixed-key records, `Map`, `Object.create(null)` (§3.4).
- Every SVG element is created by `createElementNS("http://www.w3.org/2000/svg", name)` from the fixed vocabulary of §12.2; colours are never attributes, only fixed classes read by `viewer.css` (§12.2).
- **Ownership (§12.0)**: a P1.1 task writes only the files its **Files** block lists; inside a shared file (`run.js`, `viewer.css`, `HtmlWriter.cs`) it writes only under its own marker line or in the line ranges its block names. Never edit a P2 or P3 marker except to create it (Task 1, Task 11).
- **Anchoring (§12.6)**: every new CSS selector of chrome starts from `#okf-tools`, `#okf-explorer`, `#okf-context`, `body > .okf-palette-backdrop`, `body > .okf-layout > main > .okf-page-head`, `body > .okf-layout > main > :is(.meta, .errors)`, `body > header.bar`, `body > .okf-skip`, `body > .topline`, `body > .okf-graph-layout`, or an `svg` ancestor. The harness checks this statically: Task 1's `CHROME_ANCHORS` list in `run.js` names every accepted anchor, and a class-naming selector that starts with none of them fails the run; a task whose new selector starts with an anchor kind not yet in that list (Task 3's `body > .okf-layout > main > *`, Task 12's `html[data-okf-js] body > .okf-layout > main > .okf-page-head …`) adds that exact anchor to the list in the same commit. Every `querySelector(All)` in `okf-page.js` starts from a container obtained by `getElementById` or a `body > …` selector, and a class selection also requires the expected `data-okf-*` attribute.
- **Fonts first (§11.0, A26)**: no task that adds a CSS rule to `viewer.css` starts before Task 10 (the `file://` font verdict) is merged. Task 1 only adds comment markers and anchors five existing P1 rules (no new rule; see its status note).
- Never type a `\u` escape sequence into a file through an editor tool: it is decoded on write. Build such characters at run time (`(char)0x2026` in C#, `String.fromCharCode(0x2026)` in JS, `char.ConvertFromUtf32(0x1F600)` for a surrogate pair).
- Never run `node run.js` alone: `npm test` (from `tools/viewer-security-check/`) regenerates the hostile site first (`pretest`).
- Every async harness case is bounded (the runner's 10 s timer) and every page it opens is checked for script errors when it returns (P1 runner, unchanged).
- Harness cases never hard-code a count, a type rank or a fixture size: they read `window.OKF_INDEX` (§12.7).
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Untrusted text in the breadcrumb, chips, frontmatter box, bundle name and index descriptions** (a `status`, a `verified.by`, a frontmatter key or value, a folder segment, a description holding `<img onerror>`, `"`, `</script>`, U+2028): it must stay inert text everywhere. Pinned by `PageHeadTests.Hostile_status_verifier_date_keys_and_values_are_escaped` and `HtmlWriterHeaderTests.A_hostile_bundle_name_is_escaped_in_text_and_title` (Tasks 12, 11), `IndexScriptTests.A_hostile_description_and_type_name_round_trip_inertly` (Task 4) and the harness case `page head: hostile status, verifier, date and frontmatter stay inert text after glyphs and Show all` (Task 15).
2. **Duplicate-H1 removal on real-world bodies**: CRLF files, leading blank lines made of spaces and tabs, a closing `##` sequence, `# C#`, setext headings, a heading inside a code fence, a title that differs only by spacing or by inline markup. A wrong strip deletes content; a missed strip shows two titles. Pinned by `PageModelTests.Duplicate_title_rule` (Task 5, 21 rows) and `PageHeadTests.The_payload_carries_the_display_body_and_Body_stays_raw` (Task 12).
3. **Fonts under `file://` at every page depth, in every browser**: a relative `@font-face` that Firefox's per-file origin blocks only on deep pages. Pinned by the recette control `fonts` (Task 9, run in Task 10 on each depth of both sites) and `ViewerFontsTests.Every_face_loads_an_embedded_woff2_relative_to_the_stylesheet` (Task 10); the fallback is Task 16.
4. **A concept page that collides with the graph page name** (`graph.md`, `Graph.md`, a folder `graph.html/`, an explicit host value): the site must still render, with the link pointing at the free name, and an explicit colliding value must be refused before anything is written. Pinned by `GraphPagePathTests` (Task 5) and `HtmlWriterHeaderTests.An_explicit_graph_page_that_collides_is_refused_before_writing` (Task 11).
5. **Body content wearing a chrome class** (the sanitizer keeps `class` on `<code>`): every new P1.1 selector must be anchored, and every P1.1 script must ignore body elements that wear its classes. Pinned by the generalized guard `chrome classes worn by body content change none of its styles, on every *chrome-classes page` over `p11-chrome-classes.md` (Task 1, re-run by every later task) and by `page glyphs and Show all leave body code wearing P1.1 chrome classes untouched` (Task 15).

## Task dependency graph and parallel groups

```
Task 1 (wave 0, alone: merge markers, loader, chrome guard)
  ├── chain F: Task 2 → Task 9 → Task 10 (one worktree, sequential: assets → recette tool → fonts + verdict)
  ├── Task 4  (index v2 + measurement)
  └── Task 5  (page model C#)
        ↓ all of wave 1 merged (the font verdict and the index size are now known)
  ├── Task 3  (CSS foundation)
  ├── Task 6  (OkfShapes)
  ├── Task 7  (theme button)
  └── Task 8  (contents: current section)
        ↓ wave 2 merged
  Task 11 (shell and header)
        ↓
  ├── Task 12 (page head markup, Referenced by)
  ├── Task 13 (explorer)
  ├── Task 14 (palette)
  └── Task 16 (okf-fonts.css fallback — ONLY if Task 10's verdict is "blocked")
        ↓ wave 4 merged
  Task 15 (okf-page.js)
        ↓
  ├── Task 17 (docs, CI, changelog)
  └── Task 18 (recette run, P1 port, fidelity checklist)
```

One line: `1 → {F: 2→9→10 ∥ 4 ∥ 5} → {3 ∥ 6 ∥ 7 ∥ 8} → 11 → {12 ∥ 13 ∥ 14 ∥ 16?} → 15 → {17 ∥ 18}`.

Why these edges (and not more): Task 10 must precede every task that adds a CSS rule (spec §11.0/A26: the font check comes before any other CSS task), so Tasks 3, 7, 8 wait for wave 1; Task 6 waits for Task 4's measurement (A23: measured before any JS reads the new index fields). Task 11 needs Task 2 (`WriteAssets`), Task 3 (tokens and shared components its CSS uses), Task 5 (`BundleName`, `GraphPagePath`), Task 6 (`okf-shapes.js` must exist before the script table loads it — "no page loads a script that does not exist yet", §12.0) and Task 7 (the theme button sits in the header). Tasks 12–14 and 16 touch disjoint regions (listed per task). Task 15 reads Task 12's markup. Tasks 17 and 18 touch disjoint files.

What P2 and P3 can start when (spec §9): P3's `okf-sim.js` and `cases/p3.js` can start as soon as **Task 1** is merged (the loader runs `cases/*.js`). P2's and P3's interfaces need §12.0–§12.3 delivered, i.e. **Tasks 1–6 and 11** merged; P2 and P3 then branch from the branch head once **all of P1.1** is merged, since they insert under P1.1's markers (`// P2: local graph`, `// P3: graph page (§12.5)`, `// P3: RenderGraph (§12.5)`, the `viewer.css` sections and the `ACCEPTANCE.md` sections).

## File ownership and merge zones

Shared files and who writes where inside them. A "marker" is a comment line created by Task 1 (or Task 11 for C#); the owning task inserts its lines directly **below** it. Two markers are always separated by a blank line nobody edits, so two parallel worktrees never touch the same hunk.

| File | Region | Task |
| --- | --- | --- |
| `tools/viewer-security-check/run.js` | `okfShapes()` helper after `okfSite()`; generalized chrome case; markers; loader; runner header | 1 |
| | under `// --- Task N: … ---` | N (3, 4, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16) |
| | the two P1 override indexes (`version: 1`) | 4 |
| | `CHROME_ANCHORS` (Task 1's static anchor scan): one added line each | 3 (wave 2), 12 (wave 4) |
| | `treeLink`, P1 explorer/stale/filter-label/hostile-title cases (except the hostile-title selector line), chrome probe line 3 | 13 |
| | P1 "long unbroken titles" case | 14 |
| | chrome probe line 4 (`.okf-tool`); the hostile-title case's `img, script, svg, iframe, object` selector line (F2) | 11 |
| | P1 "nothing long and unbroken" and "GFM table" cases (frontmatter lines) | 12 |
| `src/OKF4net.Viewer/Assets/viewer.css` | `@font-face` markers (top), a separator line after the P1 explorer rules and one after the P1 palette rules (so Tasks 12, 13, 14 delete non-adjacent blocks in parallel), per-task markers and the `P2`/`P3` sections (end) | 1 |
| | between the `@font-face` markers | 10 (16 empties it if needed) |
| | the five P1 rules `.topline`, `.bar-in`, `.wordmark`, `.wordmark sup`, `.meta`/`.errors` anchored (commit `84588e4`, done) | 1 |
| | `:root`, base, `main`, the comment above the anchored `.meta`/`.errors` rules, dark blocks, `.okf-sr`, layout block, under `--- Task 3` | 3 |
| | P1 header rules (as Task 1 anchored them) and `.okf-tool` rules (removed), under `--- Task 11` | 11 |
| | P1 `table.frontmatter` rules and the context `h2` rule (removed), backlinks rule, under `--- Task 12` | 12 |
| | P1 explorer rules (removed), under `--- Task 13` | 13 |
| | P1 palette rules (removed), under `--- Task 14` | 14 |
| | P1 contents rules, under `--- Task 8` | 8 |
| | under `--- Task 7` / `--- Task 15` | 7 / 15 |
| | `/* === P2: local graph === */` section | P2 only |
| | `/* === P3: graph page === */` section | P3 only |
| `src/OKF4net.Viewer/HtmlWriter.cs` | `Write` asset part, `WriteAssets`, `WriteBytes`, guard remarks | 2 |
| | `Write` (graph page, `// P3: graph page (§12.5)` marker), `ViewKind`, `RenderDocumentStart`, `RenderHeader`, `ScriptTag`, `PageScripts` (with `// P2: local graph`), `GraphPagePathOf`, `GuardNoCaseCollisions`, `RenderShell`, `RenderIndex`, `// P3: RenderGraph (§12.5)` marker, `HtmlEscape`/`RootPrefix` internal | 11 |
| | `RenderPage`, page head, frontmatter box, backlinks, `Payload` | 12 |
| | one line in `PageScripts` (`"okf-page.js"`) | 15 |
| | `RenderDocumentStart` font link, `WriteAssets` font inlining | 16 |
| `HtmlWriterHeaderTests` (new) | whole class | 11; Task 15 edits the script-order test; Task 16 the head test |
| `HtmlWriterAssetsTests` (new) | whole class | 2; Task 16 edits `Every_embedded_asset…` and `Assets_come_first…` |
| `tools/viewer-security-check/ACCEPTANCE.md` | `## P1.1`, `## P2`, `## P3` sections | 1 creates; 18 fills `## P1.1` |
| `fixtures/hostile-bundle/` | `p11-chrome-classes.md` | 1 |
| | `p11-types/` | 4 |
| | `p11-page.md` | 12 |
| `docs/superpowers/specs/2026-10-06-…-design.md` | §3.6 measurement paragraph | 4 |
| | §11.0 font result paragraph | 10 |

Lines of P2 and P3 inside P1.1-owned files (for their planners): `HtmlWriter.PageScripts` gains `"okf-local.js",` under `// P2: local graph`; `HtmlWriter.Write` gains, under `// P3: graph page (§12.5)`, exactly `WriteFile(outDir, root, verifiedDirs, graphPage, RenderGraph(site), written);` (`graphPage` is the local `Write` computes from `GraphPagePathOf(site)`); `RenderGraph` goes under `// P3: RenderGraph (§12.5)` and calls `RenderDocumentStart(site, ViewKind.Graph, "Global graph", "", null)` then `ScriptTag("", "okf-index.js")` and so on.

## Fidélité maquette

Every §11 element P1.1 delivers, the task that delivers it, and the check that proves it. "harness" = a jsdom case in `run.js` (name given); "xunit" = a test method; "recette" = a probe of `recette/p1-1.js` (Task 18), identified by the §11 id, run in Chrome, Edge and Firefox at 1 440 × 900 unless stated.

| §11 | Task | Proof |
| --- | --- | --- |
| §11.0 tokens (light, both dark blocks identical, `--red` dark) | 3 | harness `CSS tokens match spec §11.0 in light and in both dark blocks`; recette `tokens` (contrasts measured, both themes) |
| §11.0 typography | 3, 10 | recette `fonts`, `H3`, `C3`, `C7` |
| §11.0 fonts, budget, `file://` | 10 (16) | xunit `ViewerFontsTests`; recette `fonts` |
| H2 bar | 11 | recette `H2` (height 52, padding 20, full width) |
| H3 wordmark | 11 | xunit `The_page_header_is_the_spec_header`; recette `H3` |
| H4 separator | 11 | xunit same; recette `H4` |
| H5 bundle name, ellipsis, `title` | 5, 11 | xunit `PageModelTests.Bundle_name_*`, `HtmlWriterHeaderTests.A_hostile_bundle_name_is_escaped_in_text_and_title`; recette `H5` |
| H6 counts | 11 | xunit `Counts_*`; recette `H6` |
| H7 palette button | 14 | harness `palette: the opener is drawn as C draws it and keeps its shortcuts`; recette `H7` (320 → 160, hint hidden < 900) |
| H8 Filters | 13 | harness `explorer: Filters counts pressed chips and a non-empty filter, names the count, and focuses the field`; recette `H8` |
| H9 Global graph (both states) | 11 | xunit `The_three_views_link_the_graph_page_as_the_spec_says`; recette `H9` |
| H10 Reading view | 11 | xunit `The_graph_view_has_Reading_view_before_the_current_Global_graph` |
| H11 theme icon | 7 | harness `theme: the toggle is a 34 px icon button named Dark theme with the moon of the mockups`; recette `H11` |
| H12 Skip to content | 11 | harness `header: Skip to content is the first focusable element and reaches main`; recette `H12` |
| H13 narrow header | 11 | recette `H13` (899 px and 390 px: two lines, counts hidden at 390, no horizontal scroll) |
| E1 explorer 290 | 3 | harness `layout: three columns without wrapping from 1100 px (L6), side panels of 290 and 340`; recette `E1`, `L6` |
| E2 head, title text, aria-label | 13 | harness `explorer: the field is named Filter by name by aria-label, the title is text` ; recette `E2` |
| E3 field | 13 | recette `E3` |
| E4 type chips | 13 | harness `explorer: type chips filter by rank (OR), with the name filter (AND), keeping ancestors`; recette `E4` |
| E5 list | 13 | recette `E5` |
| E6 row | 13 | harness `explorer: rows show the segment, its type glyph, counts and flags in the spec order`; recette `E6` |
| E7 chevron | 13 | harness (same case: `aria-expanded`); recette `E7` |
| E8 label = segment, `title`, `data-okf-id` | 13 | harness same case + `explorer: a hostile title lives in the title attribute only`; recette `E8` |
| E9 folder count | 13 | harness same case (counts computed from the index) |
| E10 flags via OkfShapes | 13 | harness `explorer: trust badges follow ConceptAudit's tiers` (P1, updated), `staleness is evaluated at reading time…` (P1, updated) |
| E11 active row | 13 | harness `explorer: the current row is marked`; recette `E11` |
| E12 legend | 13 | harness `explorer: the legend lists human-reviewed, machine-confirmed and stale`; recette `E12` |
| E13 sticky head with chips | 3, 13 | P1 chrome case (sticky under unwrapped media); recette `E13` |
| C1 column | 3 | recette `C1` |
| C2 breadcrumb | 12 | xunit `PageHeadTests.Breadcrumb_*`; harness `page head: the breadcrumb links resolve from the site root, wherever the site is moved`; recette `C2` |
| C3 H1 | 12 | xunit `PageHeadTests.The_head_has_one_h1_with_the_display_title_and_replaces_the_meta_line`; recette `C3` |
| C4 double title | 5, 12 | xunit `PageModelTests.Duplicate_title_rule`, `PageHeadTests.The_payload_carries_the_display_body_and_Body_stays_raw`; harness `page head: the body does not repeat the title H1` |
| C5 chips | 12, 15 | xunit `PageHeadTests.Chips_*`; harness `page: chip glyphs come from OkfShapes and the stale chip flips at the deadline (control 11)`; recette `C5` |
| C6 frontmatter box | 5, 12, 15 | xunit `PageModelTests.Frontmatter_*`, `PageHeadTests.Frontmatter_box_*`; harness `page: Show all reveals the folded entries and is announced`; recette `C6` |
| C7 body | 3 | recette `C7` |
| C8 index | 3, 11 | xunit `The_index_view_has_the_header_and_no_page_head`; recette `C8` |
| X1 panel 340 | 3 | harness `layout: three columns without wrapping from 1100 px (L6), side panels of 290 and 340`; recette `X1` |
| X3 contents style | 8 | recette `X3` |
| X4 current section | 8 | harness `contents: the current section is the last h2/h3 above a quarter of the window, once per frame and after a fragment`; recette `X4` |
| X10 Referenced by | 12, 15 | xunit `PageHeadTests.Referenced_by_*`; harness `page: Referenced by rows get their type glyph from the index`; recette `X10` |
| X11 glyph titles | 13 | harness `explorer: type chips filter …` (each chip glyph has a `<title>`) |
| J1 dialog | 14 | recette `J1` (one control measures J1–J6) |
| J2 input row, Esc named Close | 14 | harness `palette: the Esc key is the Close button, second tab stop, named Close`; recette `J1` |
| J3 matches line | 14 | harness `palette: the matches line counts results and the status region is visually hidden` |
| J4 option glyph and type | 14 | harness `palette: each option has its type glyph and type name as hidden text` |
| J5 active title | 14 | recette `J1`; harness `palette: the real palette keeps its anchored styles` |
| J6 foot | 14 | harness (J3 case); recette `J1` |
| L6 no wrap ≥ 1100 | 3 | harness `layout: three columns without wrapping from 1100 px (L6), side panels of 290 and 340` (declarations); recette `L6` (1100, 1190, 1440) |
| G1 graph-view header | 11 | xunit `The_graph_view_has_Reading_view_before_the_current_Global_graph` (P3's recette checks it visually) |
| control 10 (OkfShapes) | 6 | harness `OkfShapes: …` (6 cases) |
| control 11 (page head) | 12, 15 | harness cases named above |
| control 14 (no request outside the site) | 18 | recette `requests` |

---

### Task 0: Execution protocol

This task has no code. It tells the controller how to run the plan.

- [ ] **Step 1: Start from the branch head**

```bash
cd E:/Sources/okf/.claude/worktrees/viewer-interactive-spec
git switch feat/viewer-interactive-p1
git log --oneline -1   # the commit that adds this plan
dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer|FullyQualifiedName~OKF4net.Tests.Render"
cd tools/viewer-security-check && npm ci && npm test && cd ../..
```

Expected: build without warnings, the viewer and render tests green, the harness ending `N passed, 0 failed`.

- [ ] **Step 2: Run each wave in dedicated worktrees**

For every task (or chain) of a wave, create a worktree and a branch from the current head of `feat/viewer-interactive-p1`, named after the task:

```bash
git worktree add ../p11-t04 -b p11/t04-index-v2 feat/viewer-interactive-p1
```

Chain F (Tasks 2, 9, 10) uses one worktree (`p11/chain-f`) and runs its three tasks in order. The implementer of a task works only in its worktree, follows the task's steps (each ends with a commit on the task branch) and never merges.

- [ ] **Step 3: Merge a wave**

When every task of a wave is committed and reviewed, merge each task branch into `feat/viewer-interactive-p1` with `git merge --no-ff p11/tNN-…`, in task-number order. The merge zones above make these merges conflict-free; a conflict means a task wrote outside its zone: stop and fix the task branch, never resolve by hand in favour of one side. After the last merge of the wave, on `feat/viewer-interactive-p1`:

```bash
dotnet build OKF4net.sln
dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer|FullyQualifiedName~OKF4net.Tests.Render"
dotnet format OKF4net.sln --verify-no-changes
cd tools/viewer-security-check && npm test && cd ../..
```

Expected: all green. Then remove the wave's worktrees (`git worktree remove ../p11-t04`) and start the next wave from the new head.

- [ ] **Step 4: Task 16 is conditional**

After wave 1, read the verdict Task 10 recorded in spec §11.0. "loaded everywhere" → skip Task 16. "blocked in at least one browser" → run Task 16 in wave 4.

- [ ] **Step 5: Wave 0 is already done**

Task 1 has been executed on `p11/t01-loader` (commits `acdb039`, `84588e4`, `15a31df`; see its status note). Review and merge that branch into `feat/viewer-interactive-p1` (Step 3's merge and checks) instead of re-running Task 1, then start wave 1 from the new head.

- [ ] **Step 6: After P2 and P3 merge — apply P2's documentation hand-off (controller, spec §12.0)**

P2 edits no documentation (§12.0, §12.8): its last task (P2 plan, Task 7 Step 5) hands the controller the exact wording of what `CLAUDE.md`, the viewer README and `CHANGELOG.md` lack about `okf-local.js`. Once **both** `feat/viewer-p2` and `feat/viewer-p3` are merged into `feat/viewer-interactive-p1` (P3's Task 11 edits `CLAUDE.md` and the viewer README after P1.1, so apply P2's text on top of P3's), the controller applies that wording in a worktree of its own branched from the branch head, outside the P2 and P3 worktrees, runs `dotnet build OKF4net.sln` and `dotnet format OKF4net.sln --verify-no-changes`, and commits it alone (`docs(viewer): local graph (P2) in CLAUDE.md, the viewer README and the changelog`, trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`) before PR #176 leaves draft.

---
### Task 1: Merge markers, slice-case loader and the generalized chrome-class guard

Wave 0, alone. Lays the markers every later task (and P2, P3) writes under, so parallel worktrees never edit the same hunk; delivers the loader §12.7 requires as P1.1's first task; generalizes the chrome-class guard (§12.6) so every later P1.1 selector is checked the moment it lands.

> **Status: already implemented — do not redo.** Its commits are on branch `p11/t01-loader`: `acdb039` (Steps 1–11 as written below), `84588e4` (anchoring of five P1 rules) and `15a31df` (a stronger guard). The steps below are kept as the record of what `acdb039` did; the controller reviews and merges the branch (Task 0, Step 5). Later tasks start from the state those three commits leave, which differs from the steps below in these ways:
>
> - **Five P1 rules are anchored (`84588e4`)**, because Step 1's fixture wears `topline bar-in wordmark meta errors` and the generalized guard failed on the still-unanchored P1 rules. In `viewer.css`, `.topline` became `body > .topline`; `.bar-in` became `body > header.bar > .bar-in` (still `max-width: 1440px; margin: 0 auto; padding: 16px clamp(16px, 3.5vw, 48px); display: flex; align-items: center; gap: 24px;`); `.wordmark` and `.wordmark sup` became `body > header.bar .wordmark` and `body > header.bar .wordmark sup`; `.meta`, `.errors` and `.errors h2` became `body > .okf-layout > main > .meta`, `body > .okf-layout > main > .errors` and `body > .okf-layout > main > .errors h2`. `header.bar { border-bottom: 1px solid var(--hair); }` is unchanged (its subject is a `header`, which body content cannot be). Task 3 Step 4 and Task 11 Step 4 start from these anchored forms.
> - **Three independent layers in the chrome-class guard (`15a31df`)**, each documented in `run.js` above `CHROME_PROPS`:
>   1. A **static anchor scan**, the case `every selector naming a class starts from a §12.6 chrome anchor (static smoke check of viewer.css, @media included)`: it walks every style rule of `document.styleSheets` (into `@media` and every grouping rule; it requires at least 50 rules), expands `:is()`/`:where()`, and fails on any class-naming selector that starts from none of the regular expressions of `CHROME_ANCHORS` — `#okf-tools`, `#okf-explorer`, `#okf-context`, `body > .okf-palette-backdrop`, `body > .okf-layout > main > .okf-page-head`, `body > .okf-layout > main > .meta|.errors`, `body > header.bar`, `body > .okf-skip`, `body > .okf-graph-layout`, `body > .topline`, `svg`, and the layout shell itself (`/^body > \.okf-layout(?: > main)?$/`) — unless its subject is an explicit tag other than `code`. **A later task (or P2, P3) whose selector starts with an anchor kind not in that list must add the exact anchor to `CHROME_ANCHORS` in the same commit**; Task 3 and Task 12 do (their steps give the line).
>   2. A **selector-matching differential**: no class-naming rule (pseudo-elements stripped) may match a classed `<code>` of `#okf-body`.
>   3. A **computed-style comparison over every property the stylesheet declares** (`declaredProperties`, on top of `CHROME_PROPS`, which gained `transform`, `background-image`, `content`, `filter`, `pointer-events`), done on every `*chrome-classes` page as loaded and with every `@media` unwrapped, against a **nested chrome-class tree injected at run time**: a `<code>` wearing every class the page lists, holding two such `<code>` children and `<sup>`, `<h2>`, `<ul><li>`, `<a>` descendants, compared element by element with the same tree unclassed.
> - **Loader hardening (`15a31df`)**: `register(h)` returning a promise is refused (`FAIL  - cases/<name>: register(h) must be synchronous (it returned a promise)`), `checkAsync` called after the page cases started fails the run, and a **duplicate case name** fails the run (`FAIL  - duplicate case name: …`): every case name of every slice must be unique.
> - **Four more probe rows** in `the real P1 chrome keeps its anchored styles`: `body > .topline` `height: 6px`, `body > header.bar > .bar-in` `display: flex`, `body > header.bar .wordmark` `font-size: 20px`, `body > .okf-layout > main > .meta` `font-size: 13px`. Task 11 keeps them true (its header markup keeps `.bar-in` a flex child of `header.bar`).

**Files:**
- Modify: `tools/viewer-security-check/run.js` (helper `okfShapes`, generalized chrome case, P1.1 markers, loader, runner header)
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` (comment markers only — no rule: this is not a CSS task in the sense of A26)
- Modify: `tools/viewer-security-check/ACCEPTANCE.md` (sections `## P1.1`, `## P2`, `## P3`)
- Modify: `tools/viewer-security-check/README.md` (section "Slice case files")
- Create: `tools/viewer-security-check/fixtures/hostile-bundle/p11-chrome-classes.md`

**Interfaces:**
- Consumes: P1's `run.js` helpers (`check`, `assert`, `checkAsync`, `okfSite`, `siteResources`, `openPage`, `navigations`, `key`, `type`, `unwrapMedia`, `isShown`, `paletteOptions`, `treeLink`).
- Produces:
  - `function okfShapes()` → a bare jsdom `window` with `okf-site.js` and `okf-shapes.js` evaluated (reads `okf-shapes.js` at call time).
  - The frozen helper object passed to `register(h)`: exactly `{ check, assert, checkAsync, okfSite, okfShapes, siteResources, openPage, navigations, key, type, unwrapMedia, isShown, paletteOptions, treeLink }`.
  - Loader: every `tools/viewer-security-check/cases/*.js`, in ordinal order of the file name, `require`d and `register(h)` called before the summary; absent folder or file is not an error.
  - `run.js` markers `// --- Task N: … ---` for N in 3, 4, 6, 7, 8, 10, 11, 12, 13, 14, 15, 16.
  - `viewer.css` markers: `/* === @font-face (P1.1 Task 10 replaces the lines between these two markers) === */` and `/* === end @font-face === */` at the top; one separator line after the P1 explorer rules and one after the P1 palette rules; `/* --- Task N: … --- */` for N in 3, 7, 8, 11, 12, 13, 14, 15; `/* === P2: local graph === */` + `/* (P2 rules) */`; `/* === P3: graph page === */` + `/* (P3 rules) */`.
  - `ACCEPTANCE.md` sections with placeholder lines `*(P1.1 checks)*`, `*(P2 checks)*`, `*(P3 checks)*`.
  - The P1.1 chrome-class list (fixture `p11-chrome-classes.md`), which fixes the class names later tasks use.

- [ ] **Step 1: Write the P1.1 chrome-class fixture**

Create `tools/viewer-security-check/fixtures/hostile-bundle/p11-chrome-classes.md`. The frontmatter has seven entries outside `type`/`title`, so the page gets folded entries and a "Show all" button (Task 15 uses it). The class list is the complete set of chrome classes P1.1 introduces; later tasks use exactly these names.

```markdown
---
type: Note
title: P1.1 chrome classes
description: Body code wearing every class the P1.1 chrome styles.
runtime: probe
owner: probe
area: probe
region: probe
level: probe
channel: probe
---
Flat: <code class="okf-skip topline bar bar-in wordmark bar-sep bar-bundle bar-counts okf-tool okf-tool-graph okf-theme-toggle okf-theme-icon okf-filters-count okf-palette-open okf-palette-label okf-palette-hint okf-section-title okf-chip okf-chip-type okf-chip-status okf-chip-trust okf-chip-unverified okf-chip-stale okf-chip-glyph okf-chip-text okf-chip-count okf-row okf-count okf-legend okf-legend-count okf-legend-mono okf-glyph okf-glyph-blank okf-glyph-slot okf-page-head okf-crumbs okf-crumb-sep okf-chips okf-fm okf-fm-head okf-fm-toggle okf-fm-grid okf-fm-cell okf-fm-key okf-fm-value okf-fm-struct okf-explorer-title okf-type-chips okf-tree-count okf-tree-current okf-flag okf-flag-trust okf-flag-stale okf-explorer-foot okf-palette-head okf-palette-search okf-palette-matches okf-palette-text okf-palette-foot okf-shape-0 okf-shape-5 okf-trust-human okf-trust-machine okf-stale-mark okf-ghost-mark okf-node okf-node-ring okf-node-focus okf-selected okf-focused meta errors">x</code>

Glyph slot: <code class="okf-chip-glyph okf-chip">no glyph</code> and a row: <code class="okf-row okf-count">row</code>

Folded: <code class="okf-fm okf-fm-grid"><code class="okf-fm-cell okf-fm-value">cell</code></code>

Reference: <code>x</code>
```

- [ ] **Step 2: Add the `okfShapes()` helper**

In `tools/viewer-security-check/run.js`, directly after the closing `}` of `function okfSite() { … }`, insert:

```js

// A bare window with okf-site.js and okf-shapes.js (spec §12.7), for the
// OkfShapes cases (control 10) and the slice case files. okf-shapes.js is read
// when the helper is CALLED, not when run.js loads: it is created by a later
// P1.1 task, and run.js must keep working before it exists.
function okfShapes() {
  const dom = new JSDOM("<!doctype html><html><body></body></html>", { runScripts: "outside-only" });
  dom.window.eval(siteSource);
  dom.window.eval(fs.readFileSync(path.join(ASSETS, "okf-shapes.js"), "utf8"));
  return dom.window;
}
```

- [ ] **Step 3: Generalize the chrome-class case**

In `run.js`, replace the whole block that starts with the comment `// The sanitizer keeps \`class\` on <code>, so body content can wear any chrome` and ends with the closing `});` of `checkAsync("chrome classes worn by body content change none of its styles", …)` (the `unwrapMedia` function that follows stays) with:

```js
// The sanitizer keeps `class` on <code>, so body content can wear any chrome
// class. Every chrome rule is anchored to a chrome container: inside
// #okf-body such a class must change nothing (no overlay faking a dialog, no
// fake trust badge, no text hidden from sighted readers). Each slice lists its
// chrome classes in its own fixture page (spec §12.6): chrome-classes.md
// (P1), p11-chrome-classes.md (P1.1), p2-chrome-classes.md (P2),
// p3-chrome-classes.md (P3). This case runs on every such page the generated
// site holds, so a slice's page is covered as soon as its fixture exists.
const CHROME_PROPS = ["position", "display", "width", "height", "clip", "clip-path", "overflow", "z-index", "inset", "top",
  "background", "background-color", "border", "border-left", "border-left-color", "border-radius", "border-width",
  "padding", "margin", "white-space", "max-width", "min-height", "cursor", "flex", "list-style", "font-family",
  "font-size", "color", "text-transform", "outline", "overflow-wrap", "min-width", "order", "align-items",
  "flex-direction", "flex-basis", "flex-shrink", "flex-wrap", "margin-top", "overflow-x",
  // Properties the P1.1 chrome sets as well.
  "font-weight", "letter-spacing", "line-height", "text-decoration", "visibility", "text-overflow", "border-top",
  "border-bottom", "border-color", "box-shadow", "opacity", "gap", "justify-content", "vertical-align", "text-align",
  "max-height", "padding-left", "padding-top", "grid-template-columns", "flex-grow", "fill", "stroke", "left", "bottom"];

function chromeClassPages() {
  return fs.readdirSync(SITE).filter((name) => /(^|-)chrome-classes\.html$/.test(name)).sort();
}

checkAsync("chrome classes worn by body content change none of its styles, on every *chrome-classes page", async () => {
  const pages = chromeClassPages();
  for (const required of ["chrome-classes.html", "p11-chrome-classes.html"]) {
    assert(pages.includes(required), `${required} is missing from the generated site (found: ${pages.join(", ")})`);
  }
  // jsdom applies no @media rule: a second pass loads a copy of the stylesheet
  // with every @media wrapper removed, as if every query matched.
  const unwrapped = unwrapMedia(fs.readFileSync(path.join(SITE, "assets", "viewer.css"), "utf8"));
  for (const rel of pages) {
    const window = await openPage(rel);
    const doc = window.document;
    const body = doc.getElementById("okf-body");
    const reference = body.querySelector("code:not([class])");
    assert(reference, `${rel}: the fixture lost its unclassed reference <code>`);
    const worn = Array.from(body.querySelectorAll("code[class]"));
    assert(worn.length >= 1 && worn[0].classList.length >= 2,
      `${rel}: the first classed <code> carries ${worn.length ? worn[0].classList.length : 0} classes: the sanitizer dropped them, this case no longer tests anything`);
    const assertInert = (when) => {
      for (const el of body.querySelectorAll("*")) {
        const style = window.getComputedStyle(el);
        const where = `${when}: <${el.tagName.toLowerCase()} class="${el.getAttribute("class") || ""}">`;
        assert(style.position !== "fixed" && style.position !== "absolute", `${where} inside #okf-body is position: ${style.position}`);
        assert(!/rect\(/.test(style.clip) && style.width !== "1px" && style.height !== "1px", `${where} inside #okf-body is clipped or 1px (screen-reader-only styling)`);
        assert(style.display !== "none" && style.visibility !== "hidden", `${where} inside #okf-body is hidden`);
      }
      // jsdom neither expands shorthands nor resolves var() in them, so both
      // the shorthands and their longhands are compared, as declared.
      const expected = window.getComputedStyle(reference);
      for (const el of worn) {
        const style = window.getComputedStyle(el);
        for (const prop of CHROME_PROPS) {
          assert(style.getPropertyValue(prop) === expected.getPropertyValue(prop),
            `${when}: <code class="${el.getAttribute("class")}"> ${prop}: ${style.getPropertyValue(prop)} (an unclassed <code> has ${expected.getPropertyValue(prop)})`);
        }
      }
    };
    assertInert(`${rel} as loaded`);
    const style = doc.createElement("style");
    style.textContent = unwrapped;
    doc.head.appendChild(style);
    const realHead = doc.querySelector("#okf-explorer .okf-explorer-head");
    assert(realHead && window.getComputedStyle(realHead).getPropertyValue("position") === "sticky",
      `${rel}: the unwrapped stylesheet no longer makes the real explorer head sticky: this pass tests nothing`);
    assertInert(`${rel} with every @media rule applied`);
  }
});

// The anchored rules still style the real chrome: a selector the engine
// cannot match would pass the case above by styling nothing at all. One probe
// per P1 component; each P1.1 task adds its own probe case under its marker.
checkAsync("the real P1 chrome keeps its anchored styles", async () => {
  const window = await openPage("chrome-classes.html");
  const doc = window.document;
  const chrome = [
    [doc.querySelector("body > .okf-palette-backdrop"), "position", "fixed"],
    [doc.querySelector("#okf-explorer .okf-tree-toggle .okf-sr"), "position", "absolute"],
    [doc.querySelector("#okf-explorer .okf-badge.okf-trust-human"), "width", "8px"],
    [doc.querySelector("#okf-tools .okf-tool"), "min-height", "36px"],
  ];
  for (const [el, prop, value] of chrome) {
    assert(el, `a chrome element this case needs is missing (${prop}: ${value})`);
    const got = window.getComputedStyle(el).getPropertyValue(prop);
    assert(got === value, `the real <${el.tagName.toLowerCase()} class="${el.getAttribute("class")}"> lost its ${prop}: ${value} (got ${got})`);
  }
});
```

- [ ] **Step 4: Add the P1.1 case markers and the slice-case loader**

In `run.js`, directly above the line `// --- end of async checks ---`, insert (the blank lines between markers are part of the contract: nobody edits them):

```js
// === P1.1 cases: one block per task; a task writes only under its own line ===
// --- Task 3: CSS foundation ---

// --- Task 4: index v2 ---

// --- Task 6: OkfShapes (control 10) ---

// --- Task 7: theme button ---

// --- Task 8: contents, current section ---

// --- Task 10: fonts ---

// --- Task 11: shell and header ---

// --- Task 12: page head ---

// --- Task 13: explorer ---

// --- Task 14: palette ---

// --- Task 15: okf-page.js (control 11) ---

// --- Task 16: fonts fallback (only if Task 10 found a browser blocking the fonts) ---

// === end of P1.1 cases ===

// --- slice case files (spec §12.7) ------------------------------------------
//
// P2 and P3 never edit this file: their cases live in cases/p2.js and
// cases/p3.js, each exporting register(h). Every cases/*.js present is loaded
// in ordinal order of its name and registered here, before the summary; an
// absent folder or file is not an error. h is frozen, so a slice cannot
// replace a helper another slice relies on. A slice registers synchronous
// checks with h.check (they run at once) and page cases with h.checkAsync
// (they are queued and awaited with every other page case).
const SLICE_HELPERS = Object.freeze({
  check, assert, checkAsync, okfSite, okfShapes, siteResources, openPage, navigations,
  key, type, unwrapMedia, isShown, paletteOptions, treeLink,
});
const CASES_DIR = path.join(__dirname, "cases");
let sliceCaseFiles = [];
try {
  sliceCaseFiles = fs.readdirSync(CASES_DIR).filter((name) => name.endsWith(".js")).sort();
} catch (err) {
  if (err.code !== "ENOENT") { throw err; }
}
for (const name of sliceCaseFiles) {
  console.log(`\nSlice cases (cases/${name}):`);
  let mod = null;
  try {
    mod = require(path.join(CASES_DIR, name));
  } catch (err) {
    failures++;
    console.log(`FAIL  - cases/${name} does not load: ${err.message}`);
    continue;
  }
  if (!mod || typeof mod.register !== "function") {
    failures++;
    console.log(`FAIL  - cases/${name} does not export register(h)`);
    continue;
  }
  try {
    mod.register(SLICE_HELPERS);
  } catch (err) {
    failures++;
    console.log(`FAIL  - cases/${name}: register(h) threw: ${err.message}`);
  }
}

```

- [ ] **Step 5: Give the awaited page cases their own header**

In `run.js`, in `async function runAsyncChecks() {`, insert as the first statement of the function body:

```js
  // Synchronous output (helpers, OkfShapes, slice files) has all been printed
  // by now; every queued page case reports under this header.
  console.log("\nPage cases (queued above, awaited now):");
```

- [ ] **Step 6: Add the `viewer.css` markers**

In `src/OKF4net.Viewer/Assets/viewer.css`, directly after the opening comment (the three lines ending `deliberately not coupled. */`) and before `:root {`, insert:

```css
/* === @font-face (P1.1 Task 10 replaces the lines between these two markers) === */
/* === end @font-face === */

```

Two separator lines keep the P1 blocks that wave 4 removes in parallel (explorer: Task 13; palette: Task 14; context titles: Task 12) from touching each other: directly after the line

```css
#okf-explorer .okf-stale { border-radius: 0; width: 9px; background: #b4540a; clip-path: polygon(50% 0, 100% 100%, 0 100%); }
```

insert

```css
/* (end of the P1 explorer rules: Task 13 removes the block above, Task 14 the one below; nobody edits this line) */
```

and directly after the line

```css
body > .okf-palette-backdrop .okf-palette:focus { outline: none; }
```

insert

```css
/* (end of the P1 palette rules: Task 14 removes the block above; nobody edits this line) */
```

At the very end of the file (after `#okf-body [tabindex="-1"]:focus { … }`), append:

```css

/* === P1.1 chrome: one block per task; a task writes only under its own line === */
/* --- Task 3: shared components (spec §12.6), shapes (§12.2), #okf-body content (C7) --- */

/* --- Task 7: theme button (H11) --- */

/* --- Task 8: contents (X3, X4) --- */

/* --- Task 11: header (H1-H13) --- */

/* --- Task 12: page head (C2-C6) and Referenced by (X10) --- */

/* --- Task 13: explorer (E1-E13) and Filters (H8) --- */

/* --- Task 14: palette (H7, J1-J6) --- */

/* --- Task 15: Show all (C6) --- */

/* === P2: local graph === */
/* (P2 rules) */

/* === P3: graph page === */
/* (P3 rules) */
```

- [ ] **Step 7: Add the slice sections to `ACCEPTANCE.md`**

At the end of `tools/viewer-security-check/ACCEPTANCE.md`, append:

```markdown

## P1.1

*(P1.1 checks)*

## P2

*(P2 checks)*

## P3

*(P3 checks)*
```

- [ ] **Step 8: Document the loader in the harness README**

In `tools/viewer-security-check/README.md`, append at the end:

```markdown

## Slice case files

`run.js` belongs to P1.1 (spec §12.0, §12.7). The later slices add their
cases without editing it: `cases/p2.js` and `cases/p3.js` each export
`register(h)`, where `h` is a frozen object holding exactly `check`, `assert`,
`checkAsync`, `okfSite`, `okfShapes`, `siteResources`, `openPage`,
`navigations`, `key`, `type`, `unwrapMedia`, `isShown`, `paletteOptions` and
`treeLink`. The loader calls `register(h)` for every `cases/*.js`, in ordinal
order of the file name, before the summary; a missing file is not an error.
Synchronous checks (`h.check`) run at once; page cases (`h.checkAsync`) are
queued and awaited with the others, each bounded by the runner's timer and
failed by any script error its pages raise. Fixtures follow the same rule:
`fixtures/hostile-bundle/p11-*`, `p2-*` and `p3-*` belong to their slice, and
each slice lists its chrome classes in its own `*chrome-classes.md`, which the
chrome-class case picks up by name.
```

- [ ] **Step 9: Run the harness**

```bash
cd tools/viewer-security-check && npm test
```

Expected: the existing cases pass, plus `ok  - chrome classes worn by body content change none of its styles, on every *chrome-classes page` and `ok  - the real P1 chrome keeps its anchored styles`; no `Slice cases` header (no `cases/` folder yet); last line `N passed, 0 failed`.

- [ ] **Step 10: Prove the loader runs, fails and isolates slice files (not committed)**

Create `tools/viewer-security-check/cases/zz-probe.js`:

```js
"use strict";
module.exports.register = (h) => {
  h.check("probe: a synchronous case from a slice file runs", () => {});
  h.check("probe: the helper object is frozen and complete", () => {
    h.assert(Object.isFrozen(h), "h is not frozen");
    const names = Object.keys(h).sort().join(",");
    h.assert(names === "assert,check,checkAsync,isShown,key,navigations,okfShapes,okfSite,openPage,paletteOptions,siteResources,treeLink,type,unwrapMedia", names);
  });
  h.checkAsync("probe: a failing page case from a slice file fails the run", async () => {
    const window = await h.openPage("index.html");
    h.assert(window.document.getElementById("okf-explorer") === null, "deliberate failure");
  });
};
```

and `tools/viewer-security-check/cases/zz-not-a-case.js` containing only `"use strict";`.

Run `npm test`. Expected: a `Slice cases (cases/zz-not-a-case.js):` header with `FAIL  - cases/zz-not-a-case.js does not export register(h)`, a `Slice cases (cases/zz-probe.js):` header with two `ok` lines, under the page cases `FAIL  - probe: a failing page case from a slice file fails the run` / `deliberate failure`, and exit code 1 (`echo $?` prints `1`). Then delete both files and the folder (`rm -r cases`) and run `npm test` again: green.

- [ ] **Step 11: Commit**

```bash
git add tools/viewer-security-check/run.js tools/viewer-security-check/ACCEPTANCE.md tools/viewer-security-check/README.md tools/viewer-security-check/fixtures/hostile-bundle/p11-chrome-classes.md src/OKF4net.Viewer/Assets/viewer.css
git commit -m "test(viewer): slice-case loader, P1.1 merge markers and a chrome-class guard over every slice's fixture

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 2: Generic embedded assets

Wave 1, chain F (first). §12.0: `ViewerAssets` gains a generic, internal read of an embedded resource by its path; the project keeps that path as the resource's `LogicalName`; `HtmlWriter.WriteAssets` writes every embedded resource, in ordinal order of its path, then `okf-index.js`. After this task, a file added under `Assets/` (P2's and P3's scripts, the fonts) is embedded and written with no other change.

**Files:**
- Modify: `src/OKF4net.Viewer/OKF4net.Viewer.csproj` (the `EmbeddedResource` item)
- Modify: `src/OKF4net.Viewer/ViewerAssets.cs` (whole file)
- Modify: `src/OKF4net.Viewer/HtmlWriter.cs` — `Write` (the nine `WriteAsset(...)` lines), `WriteAsset` (removed), `WriteFile`, new `WriteAssets`, `WriteBytes`, `Prepare`; the `GuardNoCaseCollisions` remarks sentence about asset extensions
- Test: `tests/OKF4net.Tests/Viewer/HtmlWriterAssetsTests.cs` (new)

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `internal static IReadOnlyList<string> ViewerAssets.Paths` — every embedded asset path relative to `Assets/`, `/`-separated, ordinal order (e.g. `fonts/inter-latin.woff2`, `okf-site.js`, `viewer.css`).
  - `internal static string ViewerAssets.Text(string path)`, `internal static byte[] ViewerAssets.Bytes(string path)` — `InvalidOperationException` naming the path when it is not embedded.
  - `private static void HtmlWriter.WriteAssets(ViewerSite site, string outDir, string root, HashSet<string> verifiedDirs, List<string> written)`; `WriteFile(...)` keeps its signature; `WriteBytes(string outDir, string root, HashSet<string> verifiedDirs, string relativePath, byte[] content, List<string> written)`.
  - The public P1 properties (`Css`, `MarkedJs`, `ViewerJs`, `ThemeJs`, `SiteJs`, `ExplorerJs`, `PaletteJs`, `TocJs`) are unchanged.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Viewer/HtmlWriterAssetsTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Writing every embedded asset generically (spec §12.0, §12.6).</summary>
public class HtmlWriterAssetsTests
{
    private static ViewerSite Site(TempDir src)
    {
        src.Write("a.md", "---\ntype: Note\ntitle: A\ndescription: d\n---\nBody.\n");
        return SiteModel.Build(Bundle.Load(src.Path));
    }

    [Fact]
    public void Every_embedded_asset_is_written_byte_for_byte_under_assets()
    {
        using var src = new TempDir();
        using var dest = new TempDir();

        HtmlWriter.Write(Site(src), dest.Path);

        Assert.NotEmpty(ViewerAssets.Paths);
        foreach (var path in ViewerAssets.Paths)
        {
            var file = Path.Combine(dest.Path, "assets", path.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), path);
            Assert.Equal(ViewerAssets.Bytes(path), File.ReadAllBytes(file));
        }
    }

    [Fact]
    public void Assets_come_first_in_ordinal_order_then_the_index_script_then_index_html_then_the_pages()
    {
        using var src = new TempDir();
        using var dest = new TempDir();

        var written = HtmlWriter.Write(Site(src), dest.Path);

        var assets = ViewerAssets.Paths.Select(p => "assets/" + p).ToList();
        Assert.Equal(assets, written.Take(assets.Count));
        Assert.Equal("assets/okf-index.js", written[assets.Count]);
        Assert.Equal("index.html", written[assets.Count + 1]);
        // P3 writes the graph page between index.html and the concept pages;
        // the concept pages still come last.
        Assert.Equal("a.html", written[^1]);
    }

    [Fact]
    public void Asset_paths_are_relative_slash_separated_ordinal_and_never_a_page_or_the_generated_index()
    {
        foreach (var path in ViewerAssets.Paths)
        {
            Assert.DoesNotContain('\\', path);
            Assert.False(path.StartsWith('/'), path);
            // GuardNoCaseCollisions leaves assets/ out of its set because no
            // asset path ends in .html: this pins that half of its argument.
            Assert.False(path.EndsWith(".html", StringComparison.OrdinalIgnoreCase), path);
            Assert.NotEqual("okf-index.js", path);
        }

        Assert.Equal(ViewerAssets.Paths.OrderBy(p => p, StringComparer.Ordinal), ViewerAssets.Paths);
    }

    [Fact]
    public void The_P1_assets_stay_embedded_and_their_properties_read_them()
    {
        foreach (var name in new[] { "viewer.css", "viewer.js", "marked.min.js", "okf-theme.js", "okf-site.js", "okf-explorer.js", "okf-palette.js", "okf-toc.js" })
        {
            Assert.Contains(name, ViewerAssets.Paths);
        }

        Assert.Equal(ViewerAssets.Text("viewer.css"), ViewerAssets.Css);
        Assert.Equal(ViewerAssets.Text("viewer.js"), ViewerAssets.ViewerJs);
        Assert.Equal(ViewerAssets.Text("okf-toc.js"), ViewerAssets.TocJs);
    }

    [Fact]
    public void The_font_provenance_readme_is_never_embedded()
        => Assert.DoesNotContain("fonts/README.md", ViewerAssets.Paths);

    [Fact]
    public void An_asset_that_is_not_embedded_is_a_clear_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ViewerAssets.Bytes("no-such-asset.js"));
        Assert.Contains("no-such-asset.js", ex.Message, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlWriterAssetsTests"`
Expected: build FAILS with `CS0117: 'ViewerAssets' does not contain a definition for 'Paths'` (and `Bytes`, `Text`).

- [ ] **Step 3: Keep the asset path as the resource name**

In `src/OKF4net.Viewer/OKF4net.Viewer.csproj`, replace

```xml
  <ItemGroup>
    <EmbeddedResource Include="Assets\**\*" />
  </ItemGroup>
```

with

```xml
  <ItemGroup>
    <!-- LogicalName keeps each asset's path below Assets/ ("Assets/fonts/x.woff2"),
         so ViewerAssets can list every asset and HtmlWriter writes each one at
         the same path under assets/ (spec §12.0). %(RecursiveDir) carries the OS
         separator; ViewerAssets turns "\" into "/" when it reads the names.
         fonts/README.md records the fonts' provenance and is not an asset. -->
    <EmbeddedResource Include="Assets\**\*" Exclude="Assets\fonts\README.md" LogicalName="Assets/%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>
```

- [ ] **Step 4: Rewrite `ViewerAssets`**

Replace the whole content of `src/OKF4net.Viewer/ViewerAssets.cs` with:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Viewer;

/// <summary>
/// The viewer's static assets, embedded in the assembly so the Native AOT
/// <c>okf-render</c> binary (<c>OKF4net.Render</c>) stays self-contained (no
/// files to ship alongside it). <c>okf</c> itself does not reference this
/// assembly at all -- static-site generation was split into its own binary
/// so the CI-facing validator does not carry this vendored viewer
/// JavaScript, which it never executes.
/// </summary>
/// <remarks>
/// Every file under <c>Assets/</c> (except <c>fonts/README.md</c>) is embedded
/// with its path below <c>Assets/</c> as its resource name, and
/// <c>HtmlWriter</c> writes every one of them (spec §12.0): a script, font or
/// licence text added there needs no change here.
/// </remarks>
public static class ViewerAssets
{
    private const string Prefix = "Assets/";

    /// <summary>The generated site's stylesheet.</summary>
    public static string Css { get; } = Text("viewer.css");

    /// <summary>The vendored marked bundle (MIT) used for client-side markdown rendering.</summary>
    public static string MarkedJs { get; } = Text("marked.min.js");

    /// <summary>The client bootstrap that renders a page's payload and rewires its links.</summary>
    public static string ViewerJs { get; } = Text("viewer.js");

    /// <summary>Applies the stored theme before first paint and adds the theme toggle (loaded in <c>&lt;head&gt;</c>).</summary>
    public static string ThemeJs { get; } = Text("okf-theme.js");

    /// <summary>Side-effect-free helpers shared by the interactive scripts (<c>window.OkfSite</c>).</summary>
    public static string SiteJs { get; } = Text("okf-site.js");

    /// <summary>The tree explorer.</summary>
    public static string ExplorerJs { get; } = Text("okf-explorer.js");

    /// <summary>The "Jump to" palette.</summary>
    public static string PaletteJs { get; } = Text("okf-palette.js");

    /// <summary>Heading anchors, the contents list and fragment resolution.</summary>
    public static string TocJs { get; } = Text("okf-toc.js");

    /// <summary>
    /// Every embedded asset's path relative to <c>Assets/</c>, <c>/</c>-separated,
    /// in ordinal order -- the order <c>HtmlWriter</c> writes them in.
    /// </summary>
    internal static IReadOnlyList<string> Paths { get; } = typeof(ViewerAssets).Assembly
        .GetManifestResourceNames()
        .Select(Normalize)
        .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(name => name[Prefix.Length..])
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToList();

    /// <summary>An embedded text asset, by its path below <c>Assets/</c>.</summary>
    /// <param name="path">The asset's <c>/</c>-separated path, e.g. <c>viewer.css</c>.</param>
    internal static string Text(string path)
    {
        using var stream = Open(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>An embedded asset's bytes, by its path below <c>Assets/</c>.</summary>
    /// <param name="path">The asset's <c>/</c>-separated path, e.g. <c>fonts/inter-latin.woff2</c>.</param>
    internal static byte[] Bytes(string path)
    {
        using var stream = Open(path);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static Stream Open(string path)
    {
        var assembly = typeof(ViewerAssets).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (string.Equals(Normalize(name), Prefix + path, StringComparison.Ordinal))
            {
                return assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"embedded asset not readable: {path}");
            }
        }

        throw new InvalidOperationException($"embedded asset not found: {path}");
    }

    private static string Normalize(string name) => name.Replace('\\', '/');
}
```

- [ ] **Step 5: Write every asset generically**

In `src/OKF4net.Viewer/HtmlWriter.cs`, in `Write`, replace the nine lines from `WriteAsset(outDir, root, verifiedDirs, "viewer.css", ViewerAssets.Css, written);` to `WriteAsset(outDir, root, verifiedDirs, "okf-index.js", IndexScript.Render(site.Index), written);` with:

```csharp
        WriteAssets(site, outDir, root, verifiedDirs, written);
```

Replace the method `WriteAsset(...)` and the method `WriteFile(...)` (the two methods between `InsideTheBundle` and the `GuardWithinOutputDirectory` doc comment) with:

```csharp
    /// <summary>
    /// Writes every embedded asset under <c>assets/</c>, at its path below
    /// <c>Assets/</c>, byte for byte and in ordinal order of that path, then
    /// the generated <c>okf-index.js</c>. A file added under <c>Assets/</c> is
    /// embedded by the project's wildcard and written here with no other
    /// change (spec §12.0); it is only LOADED by a page whose script table or
    /// writer names it.
    /// </summary>
    private static void WriteAssets(ViewerSite site, string outDir, string root, HashSet<string> verifiedDirs, List<string> written)
    {
        foreach (var path in ViewerAssets.Paths)
        {
            WriteBytes(outDir, root, verifiedDirs, "assets/" + path, ViewerAssets.Bytes(path), written);
        }

        WriteFile(outDir, root, verifiedDirs, "assets/okf-index.js", IndexScript.Render(site.Index), written);
    }

    private static void WriteFile(string outDir, string root, HashSet<string> verifiedDirs, string relativePath, string content, List<string> written)
    {
        var full = Prepare(outDir, root, verifiedDirs, relativePath);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        written.Add(relativePath);
    }

    private static void WriteBytes(string outDir, string root, HashSet<string> verifiedDirs, string relativePath, byte[] content, List<string> written)
    {
        var full = Prepare(outDir, root, verifiedDirs, relativePath);
        File.WriteAllBytes(full, content);
        written.Add(relativePath);
    }

    /// <summary>
    /// The checked destination of one file, its directory created: text and
    /// binary writes go through the same guard (spec §11.0).
    /// </summary>
    private static string Prepare(string outDir, string root, HashSet<string> verifiedDirs, string relativePath)
    {
        var full = Path.Combine(outDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        GuardWithinOutputDirectory(outDir, root, verifiedDirs, full, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }
```

In the remarks of `GuardNoCaseCollisions`, replace the sentence

```
    /// output volume. The asset files under <c>assets/</c> (the static scripts
    /// and stylesheet, and the generated <c>okf-index.js</c>) are left out
    /// of the set: every generated page path ends in <c>.html</c> and every
    /// asset path ends in <c>.js</c> or <c>.css</c>, so no page FILE can
    /// collide with an asset file under any string comparer -- adding entries
```

with

```
    /// output volume. The asset files under <c>assets/</c> (the embedded
    /// scripts, stylesheet, fonts and licence texts, and the generated
    /// <c>okf-index.js</c>) are left out of the set: every generated page path
    /// ends in <c>.html</c> and no asset path does (<c>HtmlWriterAssetsTests</c>
    /// pins the second half), so no page FILE can collide with an asset file
    /// under any string comparer -- adding entries
```

- [ ] **Step 6: Run the tests**

Run: `dotnet build OKF4net.sln` then `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer|FullyQualifiedName~OKF4net.Tests.Render"`
Expected: build without warnings; all pass, including the six `HtmlWriterAssetsTests` and the P1 `HtmlWriterTests` (`Write_emits_the_shared_assets_once`, `Write_emits_the_interactive_scripts`, `A_hand_built_site_without_an_index_writes_the_empty_one`).

- [ ] **Step 7: Check the harness still loads the regenerated site**

```bash
cd tools/viewer-security-check && npm test
```

Expected: `N passed, 0 failed` (same cases as after Task 1).

- [ ] **Step 8: Format and commit**

```bash
dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Viewer/OKF4net.Viewer.csproj src/OKF4net.Viewer/ViewerAssets.cs src/OKF4net.Viewer/HtmlWriter.cs tests/OKF4net.Tests/Viewer/HtmlWriterAssetsTests.cs
git commit -m "feat(viewer): embed and write every asset under Assets/ generically, bytes included

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: CSS foundation — tokens, typography base, layout (L6), shared components, shapes

Wave 2 (after Task 10's font verdict is merged). §11.0 tokens in light and in both dark blocks (with `--red` redefined in dark), the reading column (C1), content rules under `#okf-body` (C7), the three-column layout without wrapping from 1 100 px (L6, E1, X1), a comment on the legacy `.meta`/`.errors` rules (C8; Task 1 already anchored them), the shared components of §12.6 and the shape colours of §12.2 — everything P2 and P3 use without rewriting.

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` — `:root` block; the `main` rule and its comment; one comment line above the `.meta`/`.errors` rules (already anchored by Task 1); the two dark blocks and their comment; the "Interactive viewer" comment and the `.okf-sr` rule; the layout block from `body > .okf-layout {` to the end of the `@media (max-width: 1099px)` block; under `/* --- Task 3: … --- */`. Nothing else.
- Modify: `tools/viewer-security-check/run.js` (under `// --- Task 3: CSS foundation ---`; one line of `CHROME_ANCHORS`, Step 4)

**Interfaces:**
- Consumes: Task 1's markers.
- Produces (CSS contract later tasks and P2/P3 rely on):
  - tokens `--white --ink --blue --blue-hover --blue-soft --gray --hair --red --ghost --edge --stale --okf-type-0 … --okf-type-5 --backdrop --shadow` with the §11.0 values;
  - shared component classes, anchored to `#okf-explorer`, `#okf-context`, `body > .okf-layout > main > .okf-page-head`, `body > .okf-graph-layout`, `body > .okf-palette-backdrop`: `.okf-section-title`, `.okf-chip` (+ `.okf-chip-type`, `.okf-chip-status`, `.okf-chip-trust`, `.okf-chip-unverified`, `.okf-chip-stale[data-okf-stale-now]`, `button.okf-chip[aria-pressed]`, `.okf-chip-glyph:empty`), `.okf-row`, `.okf-legend` (+ `.okf-legend-count`, `.okf-legend-mono`), `.okf-glyph`, `.okf-glyph-blank`, `.okf-count`;
  - shape classes under an `svg` ancestor: `.okf-shape-0`…`.okf-shape-5`, `.okf-trust-human`, `.okf-trust-machine`, `.okf-stale-mark`, `.okf-ghost-mark`, `.okf-node-ring`, `.okf-node-focus`, `.okf-node.okf-selected`, `.okf-node.okf-focused`; and `.okf-chip-type svg …` under the chip containers;
  - `.okf-sr` anchored to every chrome container;
  - layout: `#okf-explorer` 290 and `#okf-context` 340 wide from 1 100 px, `main` `flex: 1 1 0`, `flex-wrap: nowrap`; `#okf-context` a flex column with `gap: 22px`; `[hidden]` honoured on both side panels; sticky explorer head and foot (`.okf-explorer-head`, `.okf-explorer-foot`) from 1 100 px.

- [ ] **Step 1: Write the failing harness cases**

In `run.js`, directly under `// --- Task 3: CSS foundation ---`, insert:

```js
console.log("\nP1.1 — CSS foundation:");

// The custom properties of one rule block of viewer.css, as declared. A
// smoke check on the SOURCE text, not on rendering: the recette measures the
// colours and contrasts in real browsers (spec §11.0, A19).
function declaredTokens(css, header) {
  const start = css.indexOf(header);
  assert(start !== -1, `viewer.css has no block starting with ${header}`);
  const open = css.indexOf("{", start);
  const close = css.indexOf("}", open);
  const tokens = new Map();
  for (const m of css.slice(open + 1, close).matchAll(/(--[a-z0-9-]+)\s*:\s*([^;]+);/g)) { tokens.set(m[1], m[2].trim()); }
  return tokens;
}

check("CSS tokens match spec §11.0 in light and in both dark blocks", () => {
  const css = fs.readFileSync(path.join(ASSETS, "viewer.css"), "utf8").replace(/[/][*][^]*?[*][/]/g, "");
  const light = {
    "--white": "#ffffff", "--ink": "#101014", "--blue": "#1a3fd6", "--blue-hover": "#102a96", "--blue-soft": "#eef1fd",
    "--gray": "#6a6a72", "--hair": "#e3e3e8", "--red": "#c0392b", "--ghost": "#c0392b", "--edge": "#8a8a94",
    "--stale": "#b4540a", "--okf-type-0": "#1a3fd6", "--okf-type-1": "#101014", "--okf-type-2": "#b4540a",
    "--okf-type-3": "#6a6a72", "--okf-type-4": "#0b6e69", "--okf-type-5": "#6a6a72",
    "--backdrop": "rgba(16, 16, 20, .34)", "--shadow": "0 18px 50px rgba(16, 16, 20, .28)",
  };
  const dark = {
    "--white": "#101014", "--ink": "#f2f2f5", "--blue": "#8fa5f5", "--blue-hover": "#b7c5f8", "--blue-soft": "#1a1a22",
    "--gray": "#9a9aa2", "--hair": "#2a2a33", "--red": "#ef6b5e", "--ghost": "#ef6b5e", "--edge": "#8a8a94",
    "--stale": "#e08a3e", "--okf-type-0": "#8fa5f5", "--okf-type-1": "#f2f2f5", "--okf-type-2": "#e08a3e",
    "--okf-type-3": "#9a9aa2", "--okf-type-4": "#2fb3a8", "--okf-type-5": "#9a9aa2",
    "--backdrop": "rgba(0, 0, 0, .55)", "--shadow": "none",
  };
  const blocks = [
    [":root {", light],
    [':root[data-theme="dark"] {', dark],
    [':root:not([data-theme="light"]) {', dark],
  ];
  for (const [header, want] of blocks) {
    const got = declaredTokens(css, header);
    for (const [name, value] of Object.entries(want)) {
      assert(got.get(name) === value, `${header} ${name}: ${got.get(name)}, expected ${value}`);
    }
  }
});

// Unwraps the @media blocks whose condition `keep` accepts and DROPS the
// others, so a case can apply the wide layout (min-width) without the narrow
// one. Same parser as unwrapMedia.
function mediaWhere(css, keep) {
  css = css.replace(/[/][*][^]*?[*][/]/g, "");
  let out = "";
  const stack = [];
  for (let i = 0; i < css.length; i++) {
    if (css.startsWith("@media", i)) {
      const open = css.indexOf("{", i);
      const kept = keep(css.slice(i + 6, open).trim());
      stack.push(kept ? "media-kept" : "media-dropped");
      i = open;
    } else if (css[i] === "{") {
      stack.push("block");
      if (!stack.includes("media-dropped")) { out += "{"; }
    } else if (css[i] === "}") {
      const top = stack.pop();
      if (top === "block" && !stack.includes("media-dropped")) { out += "}"; }
    } else if (!stack.includes("media-dropped")) {
      out += css[i];
    }
  }
  return out;
}

checkAsync("layout: three columns without wrapping from 1100 px (L6), side panels of 290 and 340", async () => {
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const style = doc.createElement("style");
  style.textContent = mediaWhere(fs.readFileSync(path.join(SITE, "assets", "viewer.css"), "utf8"), (cond) => /min-width/.test(cond) && !/max-width/.test(cond));
  doc.head.appendChild(style);
  const get = (el, prop) => window.getComputedStyle(el).getPropertyValue(prop);
  const layout = doc.querySelector("body > .okf-layout");
  const main = doc.querySelector("body > .okf-layout > main");
  const explorer = doc.getElementById("okf-explorer");
  const context = doc.getElementById("okf-context");
  assert(get(layout, "flex-wrap") === "nowrap", `.okf-layout flex-wrap: ${get(layout, "flex-wrap")}`);
  assert(get(explorer, "width") === "290px" && get(explorer, "flex-grow") === "0" && get(explorer, "flex-shrink") === "0",
    `explorer: width ${get(explorer, "width")}, grow ${get(explorer, "flex-grow")}, shrink ${get(explorer, "flex-shrink")}`);
  assert(get(context, "width") === "340px" && get(context, "flex-grow") === "0" && get(context, "flex-shrink") === "0",
    `context: width ${get(context, "width")}, grow ${get(context, "flex-grow")}, shrink ${get(context, "flex-shrink")}`);
  assert(get(main, "flex-grow") === "1" && get(main, "flex-shrink") === "1" && ["0", "0px", "0%"].includes(get(main, "flex-basis")) && ["0", "0px"].includes(get(main, "min-width")),
    `main: grow ${get(main, "flex-grow")}, shrink ${get(main, "flex-shrink")}, basis ${get(main, "flex-basis")}, min-width ${get(main, "min-width")}`);
});

checkAsync("layout: a hidden side panel stays hidden although the panels are flex containers", async () => {
  // constructor.html has no h2/h3 and no backlinks: its context panel is hidden.
  const window = await openPage("constructor.html");
  const context = window.document.getElementById("okf-context");
  assert(context.hidden, "this case needs the hidden context panel of constructor.html");
  assert(window.getComputedStyle(context).getPropertyValue("display") === "none", `a hidden #okf-context is display: ${window.getComputedStyle(context).getPropertyValue("display")}`);
});

checkAsync("shared components are styled under every chrome container, and not in the body", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const main = doc.querySelector("body > .okf-layout > main");
  const head = doc.createElement("div");
  head.className = "okf-page-head";
  main.insertBefore(head, main.firstChild);
  const graph = doc.createElement("div");
  graph.className = "okf-graph-layout";
  doc.body.appendChild(graph);
  const containers = [doc.getElementById("okf-explorer"), doc.getElementById("okf-context"), head, graph, doc.querySelector("body > .okf-palette-backdrop")];
  const probe = (container) => {
    const chip = doc.createElement("span"); chip.className = "okf-chip";
    const title = doc.createElement("h2"); title.className = "okf-section-title";
    const row = doc.createElement("a"); row.className = "okf-row";
    container.appendChild(chip); container.appendChild(title); container.appendChild(row);
    const css = (el, prop) => window.getComputedStyle(el).getPropertyValue(prop);
    return { chipHeight: css(chip, "height"), chipSize: css(chip, "font-size"), titleSize: css(title, "font-size"), titleCase: css(title, "text-transform"), rowSize: css(row, "font-size") };
  };
  for (const container of containers) {
    assert(container, "a chrome container this case needs is missing");
    const got = probe(container);
    const where = container.id || container.className;
    assert(got.chipHeight === "26px" && got.chipSize === "12px", `${where}: .okf-chip height ${got.chipHeight}, font-size ${got.chipSize}`);
    assert(got.titleSize === "11px" && got.titleCase === "uppercase", `${where}: .okf-section-title font-size ${got.titleSize}, text-transform ${got.titleCase}`);
    assert(got.rowSize === "13.5px", `${where}: .okf-row font-size ${got.rowSize}`);
  }
  const inBody = probe(doc.getElementById("okf-body"));
  assert(inBody.chipHeight !== "26px" && inBody.titleSize !== "11px" && inBody.rowSize !== "13.5px",
    `the shared components styled body content: ${JSON.stringify(inBody)}`);
});
```

- [ ] **Step 2: Run the harness to verify the new cases fail**

```bash
cd tools/viewer-security-check && npm test
```

Expected: `FAIL  - CSS tokens match spec §11.0 …` (`--blue-hover: undefined`), `FAIL  - layout: three columns …` (`flex-wrap: wrap`), `FAIL  - shared components …` (`.okf-chip height` empty); `layout: a hidden side panel stays hidden …` may already pass (jsdom's own `[hidden]` rule).

- [ ] **Step 3: Replace the tokens**

In `src/OKF4net.Viewer/Assets/viewer.css`, replace the whole `:root { … }` block (from `:root {` to its closing `}`, the one holding `--white: #ffffff;`) with:

```css
:root {
  /* Tokens (spec §11.0): the light values are the mockups'. */
  --white: #ffffff;
  --ink: #101014;
  --blue: #1a3fd6;
  --blue-hover: #102a96;
  --blue-soft: #eef1fd;
  --gray: #6a6a72;
  --hair: #e3e3e8;
  --red: #c0392b;
  --ghost: #c0392b;
  --edge: #8a8a94;
  --stale: #b4540a;
  --okf-type-0: #1a3fd6;
  --okf-type-1: #101014;
  --okf-type-2: #b4540a;
  --okf-type-3: #6a6a72;
  --okf-type-4: #0b6e69;
  --okf-type-5: #6a6a72;
  --backdrop: rgba(16, 16, 20, .34);
  --shadow: 0 18px 50px rgba(16, 16, 20, .28);
  --display: "Inter Tight", "Arial Narrow", sans-serif;
  --body: "Inter", "Helvetica Neue", sans-serif;
  --mono: "Space Mono", Consolas, monospace;
  /* Native scrollbars and form controls follow the theme too. */
  color-scheme: light dark;
}
```

- [ ] **Step 4: Replace the reading column; comment the anchored `.meta`/`.errors`**

Replace

```css
/* break-word: a word longer than the line (a dotted member name, a path in
   inline code) wraps instead of widening the page at phone width. */
main { max-width: 900px; margin: 0 auto; padding: 32px clamp(16px, 3.5vw, 48px) 96px; overflow-wrap: break-word; }
```

with

```css
/* The reading column (C1): padding 26 48 0 (16 on a phone), at most 720
   wide. break-word: a word longer than the line (a dotted member name, a
   path in inline code) wraps instead of widening the page at phone width.
   Anchored: the graph page's own <main> (P3) is not under .okf-layout. */
body > .okf-layout > main { padding: 26px clamp(16px, 3.5vw, 48px) 0; overflow-wrap: break-word; }
body > .okf-layout > main > * { max-width: 720px; }
```

`body > .okf-layout > main > *` names a class and its subject is `*`, so Task 1's static anchor scan would report it although it can never reach `#okf-body` content (whose `<code>` elements are never children of `main`). In `tools/viewer-security-check/run.js`, in `CHROME_ANCHORS`, replace the line

```js
  /^body > \.okf-layout(?: > main)?$/,
```

with

```js
  /^body > \.okf-layout(?: > main(?: > \*)?)?$/,
```

The `.meta`/`.errors` rules are already anchored (Task 1, commit `84588e4`); the file holds exactly:

```css
body > .okf-layout > main > .meta { font-family: var(--mono); font-size: 13px; color: var(--gray); }
body > .okf-layout > main > .errors { border-left: 3px solid var(--red); padding-left: 16px; margin-bottom: 32px; }
body > .okf-layout > main > .errors h2 { color: var(--red); font-size: 18px; }
```

Leave those three lines unchanged and insert directly above the first of them:

```css
/* The index page's count and parse errors (C8), anchored like all chrome. */
```

- [ ] **Step 5: Replace the two dark blocks**

Replace everything from the comment `/* Dark theme: follows the system unless the toggle forced a theme` to the closing `}` of the `@media (prefers-color-scheme: dark) { … }` block with:

```css
/* Dark theme: follows the system unless the toggle forced a theme
   (okf-theme.js sets data-theme on <html>). The two dark blocks hold the
   same values (the harness checks it). --red is redefined in dark: #c0392b
   on #101014 is 3.5:1, under the 4.5:1 text threshold of a.broken (spec
   §11.0). */
:root[data-theme="light"] { color-scheme: light; }
:root[data-theme="dark"] {
  --white: #101014; --ink: #f2f2f5; --blue: #8fa5f5; --blue-hover: #b7c5f8; --blue-soft: #1a1a22;
  --gray: #9a9aa2; --hair: #2a2a33; --red: #ef6b5e; --ghost: #ef6b5e; --edge: #8a8a94; --stale: #e08a3e;
  --okf-type-0: #8fa5f5; --okf-type-1: #f2f2f5; --okf-type-2: #e08a3e; --okf-type-3: #9a9aa2;
  --okf-type-4: #2fb3a8; --okf-type-5: #9a9aa2;
  --backdrop: rgba(0, 0, 0, .55); --shadow: none;
  color-scheme: dark;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --white: #101014; --ink: #f2f2f5; --blue: #8fa5f5; --blue-hover: #b7c5f8; --blue-soft: #1a1a22;
    --gray: #9a9aa2; --hair: #2a2a33; --red: #ef6b5e; --ghost: #ef6b5e; --edge: #8a8a94; --stale: #e08a3e;
    --okf-type-0: #8fa5f5; --okf-type-1: #f2f2f5; --okf-type-2: #e08a3e; --okf-type-3: #9a9aa2;
    --okf-type-4: #2fb3a8; --okf-type-5: #9a9aa2;
    --backdrop: rgba(0, 0, 0, .55); --shadow: none;
    color-scheme: dark;
  }
}
```

- [ ] **Step 6: Re-anchor `.okf-sr` and restate the anchoring rule**

Replace the comment that starts `/* Interactive viewer (spec 2026-10-06).` and the `.okf-sr` rule right after it with:

```css
/* Interactive viewer (spec 2026-10-06).
   Every chrome selector is anchored to a chrome container (spec §12.6):
   #okf-tools, #okf-explorer, #okf-context, body > .okf-palette-backdrop,
   body > .okf-layout > main > .okf-page-head, body > .okf-layout > main >
   :is(.meta, .errors), body > header.bar, body > .okf-skip, body > .topline,
   body > .okf-graph-layout, or an svg ancestor. The sanitizer keeps `class`
   on <code>, so body content can wear any of these class names: an
   unanchored rule would let it fake a dialog over the page, a trust badge,
   or hide text from sighted readers. #okf-body sits inside <main>, never
   directly under <body>, and holds no svg, so nothing here matches inside it
   (tools/viewer-security-check, chrome-class case). */
:is(#okf-tools, #okf-explorer, #okf-context, body > .okf-palette-backdrop, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout) .okf-sr {
  position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px;
  overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; border: 0;
}
```

- [ ] **Step 7: Replace the layout block**

Replace everything from `body > .okf-layout { display: flex; …` through the closing `}` of the `@media (max-width: 1099px) { … }` block (the block holding `#okf-explorer { order: 1; }`) with:

```css
body > .okf-layout { display: flex; flex-wrap: wrap; align-items: flex-start; }
body > .okf-layout > main { flex: 999 1 560px; min-width: 0; margin: 0; }
/* min-width: 0 on the side panels: a flex item's automatic minimum is its
   min-content width, which a long nowrap tree entry would push past a phone's
   width (the page then scrolls sideways, recette R1). */
#okf-explorer { flex: 1 1 100%; min-width: 0; padding: 0; border-bottom: 1px solid var(--hair); }
/* The context panel stacks its sections 22 apart (X1); P2's local graph
   slots in between them with no rule of its own. */
#okf-context {
  display: flex; flex-direction: column; gap: 22px;
  flex: 1 1 100%; min-width: 0; padding: 20px 20px 0; border-bottom: 1px solid var(--hair);
}
/* Both panels are flex containers or items with an author display: the
   hidden attribute must still win. */
#okf-explorer[hidden], #okf-context[hidden] { display: none; }
@media (min-width: 1100px) {
  /* Three columns that never wrap (L6): explorer 290 (E1), context 340 (X1),
     main takes the rest and may shrink below its content. */
  body > .okf-layout { flex-wrap: nowrap; }
  body > .okf-layout > main { flex-grow: 1; flex-shrink: 1; flex-basis: 0; min-width: 0; }
  #okf-explorer {
    flex-grow: 0; flex-shrink: 0; flex-basis: auto; width: 290px;
    border-bottom: 0; border-right: 1px solid var(--hair);
  }
  #okf-context {
    flex-grow: 0; flex-shrink: 0; flex-basis: auto; width: 340px;
    border-bottom: 0; border-left: 1px solid var(--hair);
  }
  #okf-explorer, #okf-context { position: sticky; top: 0; max-height: 100vh; overflow-y: auto; }
  /* The explorer's head (title, field, type chips: E13) stays at the top of
     its own scroll area and its legend (E12) at the bottom. Opaque, or tree
     rows would show through. */
  #okf-explorer .okf-explorer-head { position: sticky; top: 0; z-index: 1; background: var(--white); }
  #okf-explorer .okf-explorer-foot { position: sticky; bottom: 0; z-index: 1; background: var(--white); }
}
@media (max-width: 1099px) {
  /* Stacked: the page first, then its context, then the explorer, so a reader
     does not scroll through the tree before every page. Visual order only:
     the DOM (and so the reading and tab order) stays explorer, main, context;
     "Skip to content" (H12) jumps the explorer. */
  #okf-explorer { order: 1; }
}
```

- [ ] **Step 8: Write the shared components, the shape colours and the body content rules**

Directly under `/* --- Task 3: shared components (spec §12.6), shapes (§12.2), #okf-body content (C7) --- */`, insert:

```css
/* Shared components (spec §12.6), written once and anchored to every
   container that uses them, present and future: the explorer, the context
   panel (P2's local graph included), the page head, the graph page (P3) and
   the palette. P2 and P3 use these classes without restyling them. */
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-section-title {
  font-family: var(--mono); font-size: 11px; font-weight: 400; letter-spacing: .06em; line-height: 1.4;
  text-transform: uppercase; color: var(--gray); margin: 0 0 8px;
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-chip {
  display: inline-flex; align-items: center; gap: 7px; height: 26px; padding: 0 10px; box-sizing: border-box;
  font-family: var(--body); font-size: 12px; font-weight: 400; line-height: 1; white-space: nowrap;
  border: 1px solid var(--hair); background: var(--white); color: var(--gray); text-decoration: none;
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-chip-type {
  background: var(--ink); border-color: var(--ink); color: var(--white); font-weight: 600;
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-chip-status {
  font-family: var(--mono); color: var(--ink);
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-chip-trust {
  border-color: var(--blue); color: var(--blue);
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-chip-trust.okf-chip-unverified {
  border-color: var(--hair); color: var(--gray);
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-chip-stale[data-okf-stale-now] {
  border-color: var(--stale); color: var(--stale);
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) button.okf-chip {
  cursor: pointer; font-family: var(--body);
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) button.okf-chip[aria-pressed="true"] {
  border-color: var(--blue); background: var(--blue-soft); color: var(--blue); font-weight: 600;
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-chip-glyph:empty { display: none; }
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-chip-glyph {
  flex: none; display: inline-flex; align-items: center;
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-row {
  display: flex; align-items: center; gap: 9px; padding: 6px 0; min-width: 0;
  border-bottom: 1px solid var(--hair); font-size: 13.5px; color: var(--ink); text-decoration: none; overflow-wrap: anywhere;
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) a.okf-row:hover { color: var(--blue); }
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-legend {
  list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 7px; font-size: 12px; color: var(--gray);
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-legend li {
  display: flex; align-items: center; gap: 8px;
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) :is(.okf-legend-count, .okf-legend-mono) {
  font-family: var(--mono);
}
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-legend-count { font-size: 11px; color: var(--gray); }
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-glyph-blank {
  flex: none; display: inline-block; width: 10px; height: 10px;
}
:is(#okf-tools, #okf-explorer, #okf-context, body > .okf-palette-backdrop, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout) svg.okf-glyph {
  flex: none; display: block; overflow: visible;
}

/* Shapes (spec §12.2): colours come from the tokens through fixed classes,
   never from attributes, so a theme change redraws nothing. Anchored by an
   svg ancestor: the sanitizer never lets an svg into #okf-body (§4.5). */
svg .okf-shape-0 { fill: var(--okf-type-0); }
svg .okf-shape-1 { fill: var(--okf-type-1); }
svg .okf-shape-2 { fill: var(--okf-type-2); }
svg .okf-shape-3 { fill: var(--okf-type-3); }
svg .okf-shape-4 { fill: none; stroke: var(--okf-type-4); }
svg .okf-shape-5 { fill: none; stroke: var(--okf-type-5); }
svg .okf-trust-human { fill: var(--blue); }
svg .okf-trust-machine { fill: none; stroke: var(--blue); }
svg .okf-stale-mark { fill: var(--stale); }
svg .okf-ghost-mark { fill: var(--white); stroke: var(--ghost); }
svg .okf-node-ring, svg .okf-node-focus { fill: none; visibility: hidden; }
svg .okf-node-ring { stroke: var(--blue); }
svg .okf-node-focus { stroke: var(--ink); }
svg .okf-node.okf-selected > .okf-node-ring, svg .okf-node.okf-focused > .okf-node-focus { visibility: visible; }
/* In a type chip (white text on ink) the shape is white, filled or stroked.
   Anchored to the chip containers too: the static anchor scan (Task 1)
   accepts an svg ancestor only at the start of a selector. */
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-chip-type svg :is(.okf-shape-0, .okf-shape-1, .okf-shape-2, .okf-shape-3) { fill: var(--white); }
:is(#okf-explorer, #okf-context, body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout, body > .okf-palette-backdrop) .okf-chip-type svg :is(.okf-shape-4, .okf-shape-5) { stroke: var(--white); }

/* Body content (C7): rules for the markdown viewer.js renders, under the
   #okf-body id, never chrome. Same rules for every element of a kind, with or
   without a class. */
#okf-body { font-size: 15.5px; line-height: 1.65; }
#okf-body p { margin: 0 0 20px; }
#okf-body h2 { margin: 0 0 8px; font-family: var(--display); font-weight: 600; font-size: 21px; letter-spacing: -.02em; }
#okf-body table { font-size: 13.5px; margin: 0 0 20px; border-collapse: collapse; }
#okf-body th {
  text-align: left; font-family: var(--mono); font-weight: 400; font-size: 12px; color: var(--gray);
  padding: 6px 12px 6px 0; border-bottom: 1px solid var(--hair);
}
#okf-body td { padding: 6px 12px 6px 0; border-bottom: 1px solid var(--hair); }
#okf-body pre { background: var(--blue-soft); padding: 16px; margin: 0 0 20px; font-family: var(--mono); font-size: 12.5px; line-height: 1.6; }
#okf-body pre code { font-size: inherit; }
#okf-body a:hover { color: var(--blue-hover); }
```

- [ ] **Step 9: Run the harness**

```bash
cd tools/viewer-security-check && npm test
```

Expected: the four Task 3 cases `ok`; the P1 cases still `ok` — in particular `nothing long and unbroken widens the page…` (`main` `overflow-wrap: break-word`, panels `min-width: 0px`), `the real P1 chrome keeps its anchored styles`, `every selector naming a class starts from a §12.6 chrome anchor …` (Task 1's static scan, which would name `body > .okf-layout > main > *` without Step 4's `CHROME_ANCHORS` line) and the chrome-class case on both fixture pages; `N passed, 0 failed`.

- [ ] **Step 10: Run the .NET suites (the CSS is an embedded asset)**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer"`
Expected: all pass.

- [ ] **Step 11: Commit**

```bash
git add src/OKF4net.Viewer/Assets/viewer.css tools/viewer-security-check/run.js
git commit -m "feat(viewer): spec tokens in both themes, three columns that never wrap, shared components and shape colours

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 4: Index v2 (types, `typeIndex`, description) and its measurement

Wave 1. §12.1 schema v2 (A18, A19, A28), `OkfSite.readIndex` requiring it, `check-index.js` checking it, and the size measurement A23 requires **before** any JS reads the new fields.

**Files:**
- Modify: `src/OKF4net.Viewer/ViewerIndex.cs` (`IndexConcept` init properties, `IndexType`, `ViewerIndex.Types`)
- Modify: `src/OKF4net.Viewer/SiteIndex.cs` (types ranking, `typeIndex`, description)
- Modify: `src/OKF4net.Viewer/IndexScript.cs` (version 2, new fields)
- Modify: `src/OKF4net.Viewer/Assets/okf-site.js` (`readIndex` only)
- Modify: `tools/viewer-security-check/check-index.js` (whole file)
- Modify: `tools/viewer-security-check/run.js` (the two P1 override indexes; under `// --- Task 4: index v2 ---`)
- Create: `tools/viewer-security-check/fixtures/hostile-bundle/p11-types/` (11 files)
- Modify: `tests/OKF4net.Tests/Viewer/IndexScriptTests.cs` (`EmptyScript`, two new tests)
- Test: `tests/OKF4net.Tests/Viewer/SiteIndexTypesTests.cs` (new)
- Modify: `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md` (§3.6, one paragraph)

**Interfaces:**
- Consumes: P1's `SiteIndex.Build`, `IndexScript.Render`, `HtmlSafeJson.Quote`.
- Produces:
  - `IndexConcept.TypeIndex` (`int`, init) and `IndexConcept.Description` (`string`, init, default `""`).
  - `public sealed record IndexType(string Name, int Count, int Slot)`; `ViewerIndex.Types` (`IReadOnlyList<IndexType>`, init, default `[]`; `ViewerIndex.Empty` has none).
  - `internal const int SiteIndex.DescriptionLimit` (200, or 120 per Step 9), `internal const int SiteIndex.OtherSlot` (5), `internal static string SiteIndex.Describe(string? description, int limit)`, `internal static List<IndexType> SiteIndex.RankTypes(IEnumerable<string> types)`.
  - `okf-index.js` v2 exactly as §12.1: `{"version":2,"concepts":[{…,"staleAfterDate":…,"typeIndex":N,"description":"…"}],"ghosts":…,"edges":…,"tree":…,"types":[{"name":"…","count":N,"slot":N}]}`.
  - `OkfSite.readIndex(win)` returns `null` unless `version === 2` and `types` is an array.

- [ ] **Step 1: Write the failing xunit tests**

Create `tests/OKF4net.Tests/Viewer/SiteIndexTypesTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Index v2: the types table, each concept's type and its description (spec §12.1, A18, A19).</summary>
public class SiteIndexTypesTests
{
    private static string Concept(string type, string extra = "")
        => "---\n" + (type.Length > 0 ? $"type: {type}\n" : string.Empty) + $"title: T\n{extra}---\n";

    private static ViewerIndex Build(TempDir tmp) => SiteIndex.Build(Bundle.Load(tmp.Path));

    private static readonly string Ellipsis = ((char)0x2026).ToString();

    [Fact]
    public void Types_are_ranked_by_count_then_ordinal_name_and_only_non_empty_names_take_the_five_ranks()
    {
        using var tmp = new TempDir();
        foreach (var (file, type) in new[]
        {
            ("m1", "Metric"), ("m2", "Metric"), ("m3", "Metric"), ("c1", "Attested Computation"), ("c2", "Attested Computation"),
            ("p1", "Policy"), ("p2", "Policy"), ("none", ""), ("t1", "BigQuery Table"), ("l1", "Log"), ("s1", "Skill"),
        })
        {
            tmp.Write(file + ".md", Concept(type));
        }

        Assert.Equal(
            new[]
            {
                new IndexType("Metric", 3, 0), new IndexType("Attested Computation", 2, 1), new IndexType("Policy", 2, 2),
                new IndexType("", 1, 5), new IndexType("BigQuery Table", 1, 3), new IndexType("Log", 1, 4), new IndexType("Skill", 1, 5),
            },
            Build(tmp).Types);
    }

    [Fact]
    public void Each_concept_points_at_its_own_type()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Concept("Metric"));
        tmp.Write("b.md", Concept("Policy"));
        tmp.Write("c.md", Concept("Metric"));
        tmp.Write("d.md", Concept(""));
        var index = Build(tmp);

        foreach (var concept in index.Concepts)
        {
            Assert.Equal(concept.Type, index.Types[concept.TypeIndex].Name);
        }
    }

    [Fact]
    public void Acme_retail_ranks_its_types_as_the_spec_states()
    {
        // Spec §12.1 and A28: the rule applies even where the mockup, whose
        // data is illustrative, swaps BigQuery Table and Skill.
        var index = SiteIndex.Build(Bundle.Load(Path.Combine(TestPaths.RepoRoot(), "bundles", "acme_retail")));

        Assert.Equal(
            new[] { ("Metric", 0), ("Attested Computation", 1), ("Policy", 2), ("BigQuery Table", 3), ("Skill", 4) },
            index.Types.Select(t => (t.Name, t.Slot)));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  Two\t\tspaces\n and\r\nlines  ", "Two spaces and lines")]
    public void A_description_has_its_whitespace_runs_collapsed_and_its_ends_trimmed(string? raw, string expected)
        => Assert.Equal(expected, SiteIndex.Describe(raw, SiteIndex.DescriptionLimit));

    [Fact]
    public void A_description_at_the_limit_is_kept_whole()
    {
        var text = new string('a', SiteIndex.DescriptionLimit);

        Assert.Equal(text, SiteIndex.Describe(text, SiteIndex.DescriptionLimit));
    }

    [Fact]
    public void A_longer_description_keeps_limit_minus_one_code_points_then_an_ellipsis()
    {
        var limit = SiteIndex.DescriptionLimit;

        Assert.Equal(new string('a', limit - 1) + Ellipsis, SiteIndex.Describe(new string('a', limit + 1), limit));
    }

    [Fact]
    public void Spaces_left_at_the_cut_are_removed_before_the_ellipsis()
        => Assert.Equal("aaaaaaaa" + Ellipsis, SiteIndex.Describe("aaaaaaaa bbbbbbbb", 10));

    [Fact]
    public void A_surrogate_pair_counts_one_code_point_and_is_never_split()
    {
        var face = char.ConvertFromUtf32(0x1F600);

        Assert.Equal("aaaaaaaa" + face, SiteIndex.Describe("aaaaaaaa" + face, 10));
        Assert.Equal("aaaaaaaa" + face + Ellipsis, SiteIndex.Describe("aaaaaaaa" + face + "bb", 10));
        Assert.Equal("aaaaaaaaa" + Ellipsis, SiteIndex.Describe("aaaaaaaaa" + face + "b", 10));
    }

    [Fact]
    public void A_lone_surrogate_counts_one_code_point_and_is_kept_as_is()
    {
        var lone = ((char)0xD800).ToString();

        Assert.Equal("aaaa" + lone, SiteIndex.Describe("aaaa" + lone, 5));
        Assert.Equal("aaaa" + Ellipsis, SiteIndex.Describe("aaaa" + lone + "b", 5));
    }

    [Fact]
    public void The_index_carries_each_concepts_truncated_description()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Concept("Note", $"description: {new string('x', 250)}\n"));
        var concept = Assert.Single(Build(tmp).Concepts);

        Assert.Equal(SiteIndex.DescriptionLimit, concept.Description.Length);
        Assert.EndsWith(Ellipsis, concept.Description, StringComparison.Ordinal);
    }
}
```

In `tests/OKF4net.Tests/Viewer/IndexScriptTests.cs`, replace

```csharp
    private const string EmptyScript =
        "window.OKF_INDEX = {\"version\":1,\"concepts\":[],\"ghosts\":[],\"edges\":[],\"tree\":[]};\n";
```

with

```csharp
    private const string EmptyScript =
        "window.OKF_INDEX = {\"version\":2,\"concepts\":[],\"ghosts\":[],\"edges\":[],\"tree\":[],\"types\":[]};\n";
```

and add, before the closing `}` of the class:

```csharp
    [Fact]
    public void Version_2_appends_typeIndex_and_description_to_each_concept_and_the_types_table_last()
    {
        var concept = new IndexConcept(ConceptId.Parse("a"), "A", "Metric", [], "a.html", "unverified", null, null)
        {
            TypeIndex = 0,
            Description = "Gross margin.",
        };
        var index = new ViewerIndex([concept], [], [], []) { Types = [new IndexType("Metric", 1, 0), new IndexType("", 2, 5)] };

        var script = IndexScript.Render(index);

        Assert.StartsWith("window.OKF_INDEX = {\"version\":2,\"concepts\":[", script);
        Assert.Contains("\"staleAfterMs\":null,\"staleAfterDate\":null,\"typeIndex\":0,\"description\":\"Gross margin.\"}", script);
        Assert.EndsWith(",\"types\":[{\"name\":\"Metric\",\"count\":1,\"slot\":0},{\"name\":\"\",\"count\":2,\"slot\":5}]};\n", script);
    }

    [Fact]
    public void A_hostile_description_and_type_name_round_trip_inertly()
    {
        var lineSeparator = ((char)0x2028).ToString();
        var hostile = "</script><img src=x onerror=alert(1)>" + lineSeparator + "\"\\";
        var concept = new IndexConcept(ConceptId.Parse("a"), "A", hostile, [], "a.html", "unverified", null, null)
        {
            Description = hostile,
        };
        var index = new ViewerIndex([concept], [], [], []) { Types = [new IndexType(hostile, 1, 0)] };

        var script = IndexScript.Render(index);

        Assert.DoesNotContain("<", script);
        Assert.DoesNotContain(lineSeparator, script);
        var root = Parse(script);
        Assert.Equal(hostile, root.GetProperty("concepts")[0].GetProperty("description").GetString());
        Assert.Equal(hostile, root.GetProperty("types")[0].GetProperty("name").GetString());
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~SiteIndexTypesTests|FullyQualifiedName~IndexScriptTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'IndexType' could not be found` (and `TypeIndex`, `Description`, `Types`, `Describe`, `DescriptionLimit`).

- [ ] **Step 3: Extend the index model**

In `src/OKF4net.Viewer/ViewerIndex.cs`, replace

```csharp
public sealed record IndexConcept(
    ConceptId Id,
    string Title,
    string Type,
    IReadOnlyList<string> Tags,
    string Path,
    string Trust,
    long? StaleAfterMs,
    string? StaleAfterDate);
```

with

```csharp
public sealed record IndexConcept(
    ConceptId Id,
    string Title,
    string Type,
    IReadOnlyList<string> Tags,
    string Path,
    string Trust,
    long? StaleAfterMs,
    string? StaleAfterDate)
{
    /// <summary>Position of this concept's type in <see cref="ViewerIndex.Types"/> (spec §12.1).</summary>
    public int TypeIndex { get; init; }

    /// <summary>
    /// The frontmatter <c>description</c>, whitespace runs collapsed and
    /// truncated to <see cref="SiteIndex.DescriptionLimit"/> code points
    /// (spec §12.1, A18); empty when absent.
    /// </summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>One distinct frontmatter <c>type</c> of the bundle (spec §12.1, A19).</summary>
/// <param name="Name">The type, or the empty string for the concepts that have none.</param>
/// <param name="Count">How many concepts carry it.</param>
/// <param name="Slot">0 to 4 for the five most frequent non-empty types, in order; 5 for every other type and for the empty one.</param>
public sealed record IndexType(string Name, int Count, int Slot);
```

and in `ViewerIndex`, replace

```csharp
{
    /// <summary>An index with nothing in it, for a site built by hand.</summary>
    public static ViewerIndex Empty { get; } = new([], [], [], []);
}
```

with

```csharp
{
    /// <summary>An index with nothing in it, for a site built by hand.</summary>
    public static ViewerIndex Empty { get; } = new([], [], [], []);

    /// <summary>
    /// The distinct types, by count descending then ordinal name; each
    /// concept points into it by <see cref="IndexConcept.TypeIndex"/>
    /// (spec §12.1). The JS reads the ranks here and never recomputes them.
    /// </summary>
    public IReadOnlyList<IndexType> Types { get; init; } = [];
}
```

- [ ] **Step 4: Rank the types and describe the concepts**

In `src/OKF4net.Viewer/SiteIndex.cs`, add `using System.Text;` below `using System.Globalization;`. In `Build`, directly after the line `var position = new Dictionary<string, int>(StringComparer.Ordinal);`, insert:

```csharp
        var types = RankTypes(ordered.Select(c => c.Document.Frontmatter.Type ?? string.Empty));
        var typePosition = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < types.Count; i++)
        {
            typePosition[types[i].Name] = i;
        }

```

Replace

```csharp
                CeilingMilliseconds(finding.Lifecycle.StaleAfter),
                finding.Lifecycle.StaleAfterDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
```

with

```csharp
                CeilingMilliseconds(finding.Lifecycle.StaleAfter),
                finding.Lifecycle.StaleAfterDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            {
                TypeIndex = typePosition[frontmatter.Type ?? string.Empty],
                Description = Describe(frontmatter.Description, DescriptionLimit),
            });
```

Replace `return new ViewerIndex(concepts, ghosts, edges, BuildTree(concepts));` with:

```csharp
        return new ViewerIndex(concepts, ghosts, edges, BuildTree(concepts)) { Types = types };
```

Add these members to the class (before `CeilingMilliseconds`):

```csharp
    /// <summary>
    /// The longest description the index carries, in code points (spec §12.1,
    /// A18). A23: 120 instead if the measured index of the OKF4net bundle
    /// exceeds 600 000 bytes (spec §3.6 records the measurement).
    /// </summary>
    internal const int DescriptionLimit = 200;

    /// <summary>The slot every type past the fifth, and the empty type, share (the "other" shape).</summary>
    internal const int OtherSlot = 5;

    private const char Ellipsis = (char)0x2026;

    /// <summary>
    /// The types table (spec §12.1): one entry per distinct value, by count
    /// descending then <see cref="string.CompareOrdinal(string, string)"/> of
    /// the name; the first five NON-EMPTY names take slots 0 to 4 in that
    /// order, every other name and the empty one take <see cref="OtherSlot"/>.
    /// </summary>
    /// <param name="types">Each concept's type, the empty string when absent.</param>
    internal static List<IndexType> RankTypes(IEnumerable<string> types)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            counts[type] = counts.TryGetValue(type, out var n) ? n + 1 : 1;
        }

        var ranked = new List<IndexType>(counts.Count);
        var next = 0;
        foreach (var (name, count) in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var slot = name.Length > 0 && next < OtherSlot ? next++ : OtherSlot;
            ranked.Add(new IndexType(name, count, slot));
        }

        return ranked;
    }

    /// <summary>
    /// A description as the index carries it (spec §12.1): runs of
    /// <see cref="char.IsWhiteSpace(char)"/> collapsed to one space, ends
    /// trimmed; past <paramref name="limit"/> code points, the first
    /// <c>limit - 1</c> kept, trailing spaces removed and U+2026 appended. A
    /// valid surrogate pair counts one and is never split; a lone half counts
    /// one and is kept as is.
    /// </summary>
    /// <param name="description">The frontmatter description, or null.</param>
    /// <param name="limit">The longest result, in code points.</param>
    internal static string Describe(string? description, int limit)
    {
        if (string.IsNullOrEmpty(description))
        {
            return string.Empty;
        }

        var collapsed = new StringBuilder(description.Length);
        var pendingSpace = false;
        foreach (var ch in description)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = collapsed.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                collapsed.Append(' ');
                pendingSpace = false;
            }

            collapsed.Append(ch);
        }

        var text = collapsed.ToString();
        var cut = 0;
        var points = 0;
        for (var i = 0; i < text.Length; points++)
        {
            if (points == limit - 1)
            {
                cut = i;
            }

            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        }

        return points <= limit ? text : text[..cut].TrimEnd() + Ellipsis;
    }
```

- [ ] **Step 5: Serialize version 2**

In `src/OKF4net.Viewer/IndexScript.cs`, replace `sb.Append("{\"version\":1,\"concepts\":[");` with `sb.Append("{\"version\":2,\"concepts\":[");`. Replace

```csharp
              .Append(",\"staleAfterDate\":")
              .Append(c.StaleAfterDate is { } date ? HtmlSafeJson.Quote(date) : "null")
              .Append('}');
```

with

```csharp
              .Append(",\"staleAfterDate\":")
              .Append(c.StaleAfterDate is { } date ? HtmlSafeJson.Quote(date) : "null")
              .Append(",\"typeIndex\":").Append(c.TypeIndex.ToString(CultureInfo.InvariantCulture))
              .Append(",\"description\":").Append(HtmlSafeJson.Quote(c.Description))
              .Append('}');
```

Replace

```csharp
        sb.Append("],\"tree\":");
        AppendNodes(sb, index.Tree);
        sb.Append("};\n");
        return sb.ToString();
```

with

```csharp
        sb.Append("],\"tree\":");
        AppendNodes(sb, index.Tree);
        sb.Append(",\"types\":[");
        for (var i = 0; i < index.Types.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var t = index.Types[i];
            sb.Append("{\"name\":").Append(HtmlSafeJson.Quote(t.Name))
              .Append(",\"count\":").Append(t.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"slot\":").Append(t.Slot.ToString(CultureInfo.InvariantCulture))
              .Append('}');
        }

        sb.Append("]};\n");
        return sb.ToString();
```

- [ ] **Step 6: Run the xunit tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer"`
Expected: all pass (the 12 `SiteIndexTypesTests`, the updated `IndexScriptTests`, P1's `SiteIndexTests` and `HtmlWriterTests`).

- [ ] **Step 7: Require v2 in the browser and in `check-index.js`**

In `src/OKF4net.Viewer/Assets/okf-site.js`, replace

```js
  // The site index, or null when okf-index.js did not run or window.OKF_INDEX
  // is something else -- typically a DOM element reached through named
  // access (DOM clobbering).
  function readIndex(win) {
    var idx;
    try { idx = win.OKF_INDEX; } catch (e) { return null; }
    if (!idx || typeof idx !== "object" || "nodeType" in idx) { return null; }
    if (!Array.isArray(idx.concepts) || !Array.isArray(idx.ghosts)
        || !Array.isArray(idx.edges) || !Array.isArray(idx.tree)) { return null; }
    return idx;
  }
```

with

```js
  // The site index, or null when okf-index.js did not run, is not schema
  // version 2 (spec §12.1), or window.OKF_INDEX is something else --
  // typically a DOM element reached through named access (DOM clobbering).
  function readIndex(win) {
    var idx;
    try { idx = win.OKF_INDEX; } catch (e) { return null; }
    if (!idx || typeof idx !== "object" || "nodeType" in idx) { return null; }
    if (idx.version !== 2) { return null; }
    if (!Array.isArray(idx.concepts) || !Array.isArray(idx.ghosts)
        || !Array.isArray(idx.edges) || !Array.isArray(idx.tree)
        || !Array.isArray(idx.types)) { return null; }
    return idx;
  }
```

Replace the whole content of `tools/viewer-security-check/check-index.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Executes an okf-index.js written by okf-render and checks that it defines
// a well-formed site index, schema version 2 (the shape IndexScript.cs
// writes, spec §12.1). CI runs it on the file the NATIVE AOT binary writes:
// the jsdom harness (run.js) only exercises a site from a regular build.
//
// This is a smoke check on a NON-EMPTY bundle: it requires at least one
// concept and one tree node, so it is meant for a fixture that has them, not
// for an arbitrary (possibly empty) bundle.
"use strict";
const path = require("path");

const file = path.resolve(process.argv[2] || "");

function fail(why) {
  console.error(`${file}: not a usable site index (${why})`);
  process.exit(1);
}

const isStr = (v) => typeof v === "string";
const isInt = (v) => Number.isInteger(v);
// The longest description, in code points (spec §12.1: 200, or 120 under A23).
const DESCRIPTION_LIMIT = 200;

global.window = {};
try {
  require(file);
} catch (e) {
  fail(`does not execute: ${e && e.message}`);
}

const index = global.window.OKF_INDEX;
if (!index || typeof index !== "object") fail("window.OKF_INDEX is not defined");
if (index.version !== 2) fail(`version is ${JSON.stringify(index.version)}, expected 2`);
for (const key of ["concepts", "ghosts", "edges", "tree", "types"]) {
  if (!Array.isArray(index[key])) fail(`${key} is not an array`);
}
if (index.concepts.length === 0) fail("no concepts");
if (index.tree.length === 0) fail("empty tree");

const n = index.concepts.length;
const g = index.ghosts.length;
const t = index.types.length;

let rank = 0;
let total = 0;
index.types.forEach((type, i) => {
  if (!type || typeof type !== "object") fail(`types[${i}] is not an object`);
  if (!isStr(type.name)) fail(`types[${i}].name is not a string`);
  if (!isInt(type.count) || type.count <= 0) fail(`types[${i}].count is not a positive integer`);
  if (i > 0) {
    const prev = index.types[i - 1];
    if (prev.count < type.count || (prev.count === type.count && !(prev.name < type.name))) fail(`types[${i}] is out of order (count descending, then ordinal name)`);
  }
  // Ranks 0 to 4 go to the first five non-empty names, once each; 5 to the rest.
  const expected = type.name !== "" && rank < 5 ? rank++ : 5;
  if (type.slot !== expected) fail(`types[${i}].slot is ${type.slot}, expected ${expected}`);
  total += type.count;
});
if (total !== n) fail(`the type counts add up to ${total}, not to the ${n} concepts`);

index.concepts.forEach((c, i) => {
  if (!c || typeof c !== "object") fail(`concepts[${i}] is not an object`);
  for (const k of ["id", "title", "type", "path", "trust", "description"]) {
    if (!isStr(c[k])) fail(`concepts[${i}].${k} is not a string`);
  }
  if (!Array.isArray(c.tags) || !c.tags.every(isStr)) fail(`concepts[${i}].tags is not an array of strings`);
  if (c.path !== `${c.id}.html`) fail(`concepts[${i}].path is not id + ".html"`);
  // A deadline rounded up to a whole millisecond, and its date as written.
  if (c.staleAfterMs !== null && !isInt(c.staleAfterMs)) fail(`concepts[${i}].staleAfterMs is neither an integer nor null`);
  if (c.staleAfterDate !== null && !isStr(c.staleAfterDate)) fail(`concepts[${i}].staleAfterDate is neither a string nor null`);
  if (!isInt(c.typeIndex) || c.typeIndex < 0 || c.typeIndex >= t) fail(`concepts[${i}].typeIndex is out of range`);
  if (index.types[c.typeIndex].name !== c.type) fail(`concepts[${i}].typeIndex does not name its type`);
  if (Array.from(c.description).length > DESCRIPTION_LIMIT) fail(`concepts[${i}].description is longer than ${DESCRIPTION_LIMIT} code points`);
});

index.ghosts.forEach((x, i) => {
  if (!x || !isStr(x.id)) fail(`ghosts[${i}].id is not a string`);
});

index.edges.forEach((e, i) => {
  if (!Array.isArray(e) || e.length !== 4) fail(`edges[${i}] is not a 4-element array`);
  const [from, to, count, toGhost] = e;
  if (!isInt(from) || from < 0 || from >= n) fail(`edges[${i}] from is out of range`);
  if (toGhost !== 0 && toGhost !== 1) fail(`edges[${i}] ghost flag is not 0 or 1`);
  if (!isInt(to) || to < 0 || to >= (toGhost === 1 ? g : n)) fail(`edges[${i}] to is out of range`);
  if (!isInt(count) || count <= 0) fail(`edges[${i}] count is not a positive integer`);
});

function checkNodes(nodes, where) {
  if (!Array.isArray(nodes)) fail(`${where} is not an array`);
  nodes.forEach((node, i) => {
    const at = `${where}[${i}]`;
    if (!node || typeof node !== "object") fail(`${at} is not an object`);
    if (!isStr(node.name)) fail(`${at}.name is not a string`);
    if (!isInt(node.concept) || node.concept < -1 || node.concept >= n) fail(`${at}.concept is out of range`);
    checkNodes(node.children, `${at}.children`);
  });
}
checkNodes(index.tree, "tree");

console.log(`${file}: ${index.concepts.length} concepts, ${index.edges.length} edges, ${index.types.length} types`);
```

- [ ] **Step 8: Fixtures and harness cases (types, descriptions, the v1 overrides)**

Create the fixture folder `tools/viewer-security-check/fixtures/hostile-bundle/p11-types/`. No title or id here starts with `t`, or contains `foo`, `bar` or `edge`: P1's palette and explorer cases rely on those queries. Eleven files:

`p11-types/m1.md`:

```markdown
---
type: Metric
title: Metric one
description: Metric one has a deliberately long description,   with runs of   spaces the index collapses, so that the harness can check that the generated index truncates it to the limit in code points and ends it with an ellipsis instead of cutting it anywhere else; padding padding padding padding padding padding.
---
Plain.
```

`p11-types/m2.md` and `p11-types/m3.md`: the same with `title: Metric two` / `title: Metric three` and `description: A metric.`; `p11-types/c1.md`, `c2.md`: `type: Attested Computation`, titles `Computation one`, `Computation two`; `p11-types/p1.md`, `p2.md`: `type: Policy`, titles `Policy one`, `Policy two`; `p11-types/q1.md`: `type: BigQuery Table`, title `BigQuery table one`; `p11-types/l1.md`: `type: Log`, title `Log one`; `p11-types/s1.md`: `type: Skill`, title `Skill one`; each with `description: A concept of that type.` and body `Plain.`. `p11-types/none.md`, a concept with no type:

```markdown
---
title: Untyped one
description: A concept without a type.
---
Plain.
```

In `tools/viewer-security-check/run.js`, update P1's two hand-written indexes to version 2 (their cases are unchanged otherwise). In the case `palette: every match is listed and reachable, with no cap`, replace

```js
    concepts.push({ id, title: `Item ${k}`, type: "Note", tags: [], path: `${id}.html`, trust: "unverified", staleAfterMs: null, staleAfterDate: null });
  }
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 1, concepts, ghosts: [], edges: [], tree: [] })};`;
```

with

```js
    concepts.push({ id, title: `Item ${k}`, type: "Note", tags: [], path: `${id}.html`, trust: "unverified", staleAfterMs: null, staleAfterDate: null, typeIndex: 0, description: "" });
  }
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 2, concepts, ghosts: [], edges: [], tree: [], types: [{ name: "Note", count: 60, slot: 0 }] })};`;
```

and in the case `explorer: the stale badge shows staleAfterDate as written, never a date rebuilt from staleAfterMs`, replace

```js
  const concepts = [{ id: "x", title: "X", type: "Note", tags: [], path: "x.html", trust: "unverified", staleAfterMs: 0, staleAfterDate: "2026-10-06" }];
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 1, concepts, ghosts: [], edges: [], tree: [{ name: "x", concept: 0, children: [] }] })};`;
```

with

```js
  const concepts = [{ id: "x", title: "X", type: "Note", tags: [], path: "x.html", trust: "unverified", staleAfterMs: 0, staleAfterDate: "2026-10-06", typeIndex: 0, description: "" }];
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 2, concepts, ghosts: [], edges: [], tree: [{ name: "x", concept: 0, children: [] }], types: [{ name: "Note", count: 1, slot: 0 }] })};`;
```

Directly under `// --- Task 4: index v2 ---`, insert:

```js
console.log("\nP1.1 — index v2:");

check("readIndex accepts only a version 2 index with a types array", () => {
  const window = okfSite();
  const base = () => ({ concepts: [], ghosts: [], edges: [], tree: [] });
  window.OKF_INDEX = Object.assign(base(), { version: 2, types: [] });
  assert(window.OkfSite.readIndex(window) !== null, "a v2 index was rejected");
  window.OKF_INDEX = Object.assign(base(), { version: 1, types: [] });
  assert(window.OkfSite.readIndex(window) === null, "a v1 index was accepted");
  window.OKF_INDEX = Object.assign(base(), { version: 2 });
  assert(window.OkfSite.readIndex(window) === null, "an index without types was accepted");
});

checkAsync("the generated v2 index ranks the types, points each concept at its type and bounds descriptions", async () => {
  const window = await openPage("index.html");
  const idx = window.OKF_INDEX;
  assert(idx.version === 2, `version ${idx.version}`);
  const types = Array.from(idx.types);
  let rank = 0;
  let total = 0;
  types.forEach((t, k) => {
    if (k > 0) {
      const prev = types[k - 1];
      assert(prev.count > t.count || (prev.count === t.count && prev.name < t.name), `types out of order at ${k}: ${prev.name}/${prev.count}, ${t.name}/${t.count}`);
    }
    const want = t.name !== "" && rank < 5 ? rank++ : 5;
    assert(t.slot === want, `type "${t.name}" has slot ${t.slot}, expected ${want}`);
    total += t.count;
  });
  assert(total === idx.concepts.length, `type counts add up to ${total}, not ${idx.concepts.length}`);
  // The fixture exercises the empty type and the shared "other" slot.
  assert(types.some((t) => t.name === "" && t.slot === 5), "the fixture lost its untyped concept");
  assert(types.filter((t) => t.slot === 5).length >= 3, "the fixture no longer puts several types in the other slot");
  for (const c of idx.concepts) {
    assert(types[c.typeIndex] && types[c.typeIndex].name === c.type, `${c.id}: typeIndex ${c.typeIndex} does not name "${c.type}"`);
    assert(typeof c.description === "string" && Array.from(c.description).length <= 200, `${c.id}: description of ${Array.from(c.description).length} code points`);
  }
  const long = idx.concepts.find((c) => c.id === "p11-types/m1");
  assert(long && Array.from(long.description).length === 200 && long.description.endsWith(String.fromCharCode(0x2026)) && !/ {2}/.test(long.description),
    `p11-types/m1 description: ${long && JSON.stringify(long.description)}`);
});
```

- [ ] **Step 9: Run the harness**

```bash
cd tools/viewer-security-check && npm test
```

Expected: `ok  - readIndex accepts only a version 2 index with a types array`, `ok  - the generated v2 index ranks the types…`, every P1 case still `ok` (the explorer and palette read the v2 index), `N passed, 0 failed`.

- [ ] **Step 10: Measure the v2 index (A23) before any JS reads the new fields**

```bash
cd E:/Sources/okf/.claude/worktrees/viewer-interactive-spec   # or this task's worktree
M="$TEMP/okf-p11-measure"; rm -rf "$M"; mkdir -p "$M"
dotnet run --project producers/src/OkfProducer.Cli -c Release -- generate --repo . --out "$M/okf4net-bundle" --no-msbuild
dotnet run --project src/OKF4net.Render -c Release -- "$M/okf4net-bundle" --out "$M/okf4net-site"
dotnet run --project src/OKF4net.Render -c Release -- bundles/acme_retail --out "$M/acme-site"
for s in okf4net-site acme-site; do
  f="$M/$s/assets/okf-index.js"
  node -e "global.window = {}; require(process.argv[1]); const i = window.OKF_INDEX; console.log(process.argv[2] + ': ' + i.concepts.length + ' concepts, ' + i.ghosts.length + ' ghosts, ' + i.edges.length + ' edges, ' + i.types.length + ' types, ' + require('fs').statSync(process.argv[1]).size + ' bytes')" "$f" "$s"
  node tools/viewer-security-check/check-index.js "$f"
done
```

Expected: two measurement lines and two `check-index.js` lines ending `… types`. If the `okf4net-site` size is **above 600 000 bytes**: set `internal const int DescriptionLimit = 120;` in `SiteIndex.cs`, change the two `200` of the case `the generated v2 index ranks…` to `120` (the `DESCRIPTION_LIMIT` of `check-index.js` stays 200: it is the upper bound of §12.1), rerun Steps 6, 9 and 10, and record both measurements. Otherwise 200 stays.

- [ ] **Step 11: Record the measurement in the spec**

In `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`, §3.6, after the paragraph that ends `optimisation possible plus tard, non retenue (A23) : porter les descriptions, que seul le tiroir de \`graph.html\` lit, dans un script distinct chargé par cette seule page.`, append:

```markdown

Mesure du schéma v2 (P1.1, AAAA-MM-JJ, commande de l'étape 10 de la tâche 4
du plan P1.1) : bundle d'OKF4net — C concepts, G fantômes, E arêtes, T types,
`okf-index.js` de B octets ; `acme_retail` — c concepts, b octets.
Troncature retenue : L points de code.
```

writing the run date and the numbers of Step 10 in place of `AAAA-MM-JJ`, `C`, `G`, `E`, `T`, `B`, `c`, `b`, and `200` or `120` in place of `L`. Report the numbers when the task ends.

- [ ] **Step 12: Format and commit**

```bash
dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Viewer/ViewerIndex.cs src/OKF4net.Viewer/SiteIndex.cs src/OKF4net.Viewer/IndexScript.cs src/OKF4net.Viewer/Assets/okf-site.js tools/viewer-security-check/check-index.js tools/viewer-security-check/run.js tools/viewer-security-check/fixtures/hostile-bundle/p11-types tests/OKF4net.Tests/Viewer/SiteIndexTypesTests.cs tests/OKF4net.Tests/Viewer/IndexScriptTests.cs docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md
git commit -m "feat(viewer): site index v2 with ranked types, each concept's type and a bounded description; size measured

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Page model — display body (C4), page head (C5), frontmatter view (C6), bundle name, graph page name

Wave 1. Everything the centre column and the header need from the bundle, computed purely in `SiteModel` from the public `Frontmatter` API (§2.1) and the index (trust tier and stale date are read from it, never re-derived). No HTML here.

**Files:**
- Modify: `src/OKF4net.Viewer/ViewerModel.cs` (`ViewerFrontmatterEntry`, `ViewerPage`, `ViewerSite` init properties; new `ViewerPageHead`)
- Modify: `src/OKF4net.Viewer/SiteModel.cs` (`Build`, `BuildPage`, `DisplayValue`; new helpers)
- Test: `tests/OKF4net.Tests/Viewer/PageModelTests.cs` (new), `tests/OKF4net.Tests/Viewer/GraphPagePathTests.cs` (new)

**Interfaces:**
- Consumes: `SiteIndex.Build` (P1; `IndexConcept.Trust`, `StaleAfterDate`), `Frontmatter.Type/Get/Verified/AsMapping`, `Actor`, `Stamp`, `AuditVocabulary.Name`, `OKF4net.Internal.LfLines.SplitSpans`.
- Produces:
  - `ViewerFrontmatterEntry.Structured` (`bool`, init: the value is a compact YAML emission of a sequence or mapping) and `ViewerFrontmatterEntry.Extra` (`bool`, init: hidden while the box is folded, A27; a hand-built entry is never folded).
  - `public sealed record ViewerPageHead(string Type, string? Status, string Trust, string? Verifier, string? VerifiedDate, int MoreVerifications, string? StaleAfterDate)`.
  - `ViewerPage.DisplayBody` (`string?`, init; `null` = `Body`) and `ViewerPage.Head` (`ViewerPageHead?`, init; `null` for a hand-built page = no chips).
  - `ViewerSite.BundleName` (`string?`, init) and `ViewerSite.GraphPagePath` (`string?`, init).
  - `internal static string SiteModel.StripDuplicateTitle(string body, string title)`, `internal static string SiteModel.BundleNameOf(string root)`, `internal static string SiteModel.FreeGraphPagePath(IReadOnlyList<ViewerPage> pages)`, `internal const int SiteModel.FoldedEntries` (4).

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Viewer/PageModelTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>The centre column's model: display body (C4), page head (C5), frontmatter view (C6), bundle name.</summary>
public class PageModelTests
{
    private static ViewerPage Page(TempDir tmp, string frontmatter, string body = "")
    {
        tmp.Write("c.md", $"---\n{frontmatter}---\n{body}");
        return Assert.Single(SiteModel.Build(Bundle.Load(tmp.Path)).Pages);
    }

    // Spec §11.3, C4. Each row: the body, the display title, the display body.
    [Theory]
    [InlineData("# Gross margin\n\nText.", "Gross margin", "\nText.")]
    [InlineData("\n  \t\n# Gross margin\nText.", "Gross margin", "Text.")]
    [InlineData("# Gross margin\r\nText.", "Gross margin", "Text.")]
    [InlineData("\r\n# Gross margin\r\n\r\nText.", "Gross margin", "\r\nText.")]
    [InlineData("# Gross margin ##\nText.", "Gross margin", "Text.")]
    [InlineData("# Gross margin##\nText.", "Gross margin", "# Gross margin##\nText.")]
    [InlineData("# C#\nText.", "C#", "Text.")]
    [InlineData("# C#\nText.", "C", "# C#\nText.")]
    [InlineData("# ###\nText.", "###", "# ###\nText.")]
    [InlineData("#\tGross margin\nText.", "Gross margin", "Text.")]
    [InlineData("#   Gross    margin   \nText.", "Gross margin", "Text.")]
    [InlineData("# Gross margin\nText.", "  Gross   margin ", "Text.")]
    [InlineData(" # Gross margin\nText.", "Gross margin", " # Gross margin\nText.")]
    [InlineData("## Gross margin\nText.", "Gross margin", "## Gross margin\nText.")]
    [InlineData("#Gross margin\nText.", "Gross margin", "#Gross margin\nText.")]
    [InlineData("Gross margin\n============\nText.", "Gross margin", "Gross margin\n============\nText.")]
    [InlineData("```\n# Gross margin\n```", "Gross margin", "```\n# Gross margin\n```")]
    [InlineData("Intro.\n# Gross margin", "Gross margin", "Intro.\n# Gross margin")]
    [InlineData("# *Gross margin*\nText.", "Gross margin", "# *Gross margin*\nText.")]
    [InlineData("# Gross margin", "Gross margin", "")]
    [InlineData("", "Gross margin", "")]
    public void Duplicate_title_rule(string body, string title, string expected)
        => Assert.Equal(expected, SiteModel.StripDuplicateTitle(body, title));

    [Fact]
    public void A_page_keeps_its_raw_body_and_carries_the_display_body()
    {
        using var tmp = new TempDir();
        var page = Page(tmp, "type: Note\ntitle: Gross margin\n", "# Gross margin\n\nText.\n");

        Assert.Equal("# Gross margin\n\nText.", page.Body);
        Assert.Equal("\nText.", page.DisplayBody);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("status: \"\"\n", null)]
    [InlineData("status: [draft]\n", null)]
    [InlineData("status: draft\n", "draft")]
    [InlineData("status: \"<b>x</b>\"\n", "<b>x</b>")]
    public void Status_is_shown_only_as_a_non_empty_scalar_and_raw(string extra, string? expected)
    {
        using var tmp = new TempDir();

        Assert.Equal(expected, Page(tmp, $"type: Note\ntitle: T\n{extra}").Head!.Status);
    }

    [Fact]
    public void A_human_tier_shows_its_last_human_verifier_by_id_with_its_date_and_the_others_count()
    {
        using var tmp = new TempDir();
        var head = Page(tmp,
            "type: Note\ntitle: T\nverified:\n"
            + "  - { by: \"human:alice\", at: \"2026-06-01T08:00:00Z\" }\n"
            + "  - { by: \"tool:okf-ci\", at: \"2026-06-02T08:00:00Z\" }\n"
            + "  - { by: \"human:bob\", at: \"2026-07-01T09:00:00Z\" }\n").Head!;

        Assert.Equal("human-reviewed", head.Trust);
        Assert.Equal("bob", head.Verifier);
        Assert.Equal("2026-07-01", head.VerifiedDate);
        Assert.Equal(1, head.MoreVerifications);
    }

    [Fact]
    public void A_machine_tier_shows_its_last_verifier_raw_and_a_date_that_is_not_iso_as_written()
    {
        using var tmp = new TempDir();
        var head = Page(tmp,
            "type: Note\ntitle: T\nverified:\n"
            + "  - { by: \"tool:okf-ci\", at: \"2026-06-02T08:00:00Z\" }\n"
            + "  - { by: \"process:nightly\", at: \"yesterday\" }\n").Head!;

        Assert.Equal("machine-confirmed", head.Trust);
        Assert.Equal("process:nightly", head.Verifier);
        Assert.Equal("yesterday", head.VerifiedDate);
        Assert.Equal(1, head.MoreVerifications);
    }

    [Fact]
    public void A_malformed_human_actor_is_shown_raw()
    {
        using var tmp = new TempDir();
        var head = Page(tmp, "type: Note\ntitle: T\nverified:\n  - { by: \"human:\", at: \"2026-07-01\" }\n").Head!;

        Assert.Equal("human-reviewed", head.Trust);
        Assert.Equal("human:", head.Verifier);
        Assert.Equal("2026-07-01", head.VerifiedDate);
    }

    [Fact]
    public void A_verified_entry_without_by_names_no_verifier_and_no_date()
    {
        using var tmp = new TempDir();
        var head = Page(tmp, "type: Note\ntitle: T\nverified: { at: \"2026-07-01\" }\n").Head!;

        Assert.Equal("machine-confirmed", head.Trust);
        Assert.Null(head.Verifier);
        Assert.Null(head.VerifiedDate);
        Assert.Equal(0, head.MoreVerifications);
    }

    [Fact]
    public void An_unverified_concept_names_no_verifier_and_a_missing_date_is_omitted()
    {
        using var tmp = new TempDir();
        Assert.Null(Page(tmp, "type: Note\ntitle: T\n").Head!.Verifier);

        using var other = new TempDir();
        var head = Page(other, "type: Note\ntitle: T\nverified:\n  - { by: \"human:alice\" }\n").Head!;
        Assert.Equal("alice", head.Verifier);
        Assert.Null(head.VerifiedDate);
    }

    [Fact]
    public void The_head_carries_the_type_and_the_stale_date_of_the_index()
    {
        using var tmp = new TempDir();
        var head = Page(tmp, "type: Attested Computation\ntitle: T\nstale_after: \"2026-12-31\"\n").Head!;

        Assert.Equal("Attested Computation", head.Type);
        Assert.Equal("2026-12-31", head.StaleAfterDate);

        using var untyped = new TempDir();
        var bare = Page(untyped, "title: T\n").Head!;
        Assert.Equal(string.Empty, bare.Type);
        Assert.Null(bare.StaleAfterDate);
    }

    [Fact]
    public void Frontmatter_folds_all_but_the_first_four_entries_not_shown_above()
    {
        using var tmp = new TempDir();
        var page = Page(tmp,
            "type: Note\ntitle: T\nstatus: stable\na: 1\nverified: { by: \"human:x\", at: \"2026-01-01\" }\n"
            + "b: 2\nc: 3\nstale_after: \"2027-01-01\"\nd: 4\ne: 5\n");

        Assert.Equal(new[] { "type", "title", "status", "a", "verified", "b", "c", "stale_after", "d", "e" }, page.Frontmatter.Select(e => e.Key));
        Assert.Equal(new[] { "a", "b", "c", "d" }, page.Frontmatter.Where(e => !e.Extra).Select(e => e.Key));
    }

    [Fact]
    public void Frontmatter_joins_a_sequence_of_scalars_and_keeps_other_structures_as_compact_yaml()
    {
        using var tmp = new TempDir();
        var page = Page(tmp,
            "type: Note\ntitle: T\ntags: [finance, margin, attested]\nexecutor:\n  resource: skills/run.md\n"
            + "empty: []\nmixed:\n  - a\n  - { b: 1 }\nn: 3\n");
        var entries = page.Frontmatter.ToDictionary(e => e.Key);

        Assert.Equal("finance, margin, attested", entries["tags"].Value);
        Assert.False(entries["tags"].Structured);
        Assert.True(entries["executor"].Structured);
        Assert.Contains("skills/run.md", entries["executor"].Value, StringComparison.Ordinal);
        Assert.True(entries["empty"].Structured);
        Assert.NotEqual(string.Empty, entries["empty"].Value);
        Assert.True(entries["mixed"].Structured);
        Assert.Equal("3", entries["n"].Value);
        Assert.False(entries["n"].Structured);
    }

    [Fact]
    public void A_hand_built_page_and_entry_have_no_head_no_display_body_and_nothing_folded()
    {
        var entry = new ViewerFrontmatterEntry("k", "v");
        var page = new ViewerPage(ConceptId.Parse("x"), "X", "x.html", [entry], "# X", [], []);

        Assert.False(entry.Extra);
        Assert.False(entry.Structured);
        Assert.Null(page.Head);
        Assert.Null(page.DisplayBody);
    }

    [Fact]
    public void Bundle_name_is_the_folder_name_of_the_bundle_root()
    {
        using var tmp = new TempDir();
        tmp.Write("acme_retail/a.md", "---\ntype: Note\ntitle: A\n---\n");
        var root = Path.Combine(tmp.Path, "acme_retail");

        Assert.Equal("acme_retail", SiteModel.Build(Bundle.Load(root)).BundleName);
        Assert.Equal("acme_retail", SiteModel.BundleNameOf(root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Bundle_name_of_a_volume_root_is_bundle()
        => Assert.Equal("bundle", SiteModel.BundleNameOf(Path.GetPathRoot(Path.GetTempPath())!));
}
```

Create `tests/OKF4net.Tests/Viewer/GraphPagePathTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>The graph page's file name never collides with a page or a folder (spec §12.3, A25).</summary>
public class GraphPagePathTests
{
    private static ViewerPage Page(string path) => new(ConceptId.Parse("x"), "X", path, [], string.Empty, [], []);

    [Theory]
    [InlineData(new string[0], "graph.html")]
    [InlineData(new[] { "graph.html" }, "graph-1.html")]
    [InlineData(new[] { "Graph.html" }, "graph-1.html")]
    [InlineData(new[] { "graph.html/x.html" }, "graph-1.html")]
    [InlineData(new[] { "GRAPH.HTML/x/y.html" }, "graph-1.html")]
    [InlineData(new[] { "graph.html", "graph-1.html" }, "graph-2.html")]
    [InlineData(new[] { "graph-1.html" }, "graph.html")]
    [InlineData(new[] { "a/graph.html" }, "graph.html")]
    public void The_graph_page_takes_the_first_free_name(string[] pages, string expected)
        => Assert.Equal(expected, SiteModel.FreeGraphPagePath(pages.Select(Page).ToList()));

    [Fact]
    public void A_concept_named_graph_moves_the_graph_page_aside_and_the_site_still_renders()
    {
        using var tmp = new TempDir();
        tmp.Write("graph.md", "---\ntype: Note\ntitle: Graph\n---\n");

        Assert.Equal("graph-1.html", SiteModel.Build(Bundle.Load(tmp.Path)).GraphPagePath);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~PageModelTests|FullyQualifiedName~GraphPagePathTests"`
Expected: build FAILS with `CS0117: 'SiteModel' does not contain a definition for 'StripDuplicateTitle'` (and `Head`, `DisplayBody`, `Extra`, `Structured`, `BundleName`, `BundleNameOf`, `FreeGraphPagePath`, `GraphPagePath`).

- [ ] **Step 3: Extend the view model**

In `src/OKF4net.Viewer/ViewerModel.cs`, replace

```csharp
public sealed record ViewerFrontmatterEntry(string Key, string Value);
```

with

```csharp
public sealed record ViewerFrontmatterEntry(string Key, string Value)
{
    /// <summary>
    /// Whether <see cref="Value"/> is the compact YAML emission of a sequence
    /// or a mapping (rendered in the monospace face, spec §11.3, C6). A
    /// sequence of scalars is not: it is joined by <c>", "</c>.
    /// </summary>
    public bool Structured { get; init; }

    /// <summary>
    /// Whether the entry is hidden while the frontmatter box is folded (A27):
    /// every entry but the first four not already shown by the title and the
    /// chips. A hand-built entry is never folded.
    /// </summary>
    public bool Extra { get; init; }
}

/// <summary>What the chips of a concept page show (spec §11.3, C5).</summary>
/// <param name="Type">The frontmatter <c>type</c>, or the empty string when absent (shown as "(no type)").</param>
/// <param name="Status">The raw <c>status</c> when the key exists, is a scalar and is not empty; otherwise null.</param>
/// <param name="Trust">The trust tier, read from the site index (never re-derived).</param>
/// <param name="Verifier">The verifier shown after the tier, or null when the tier's set of verifications is empty.</param>
/// <param name="VerifiedDate">Its date: the first ten characters of <c>at</c> when they read <c>YYYY-MM-DD</c>, else <c>at</c> as written; null when absent.</param>
/// <param name="MoreVerifications">How many other verifications the tier's set holds ("+N", omitted at 0).</param>
/// <param name="StaleAfterDate">The §5.5 deadline's date from the site index, or null when there is none.</param>
public sealed record ViewerPageHead(
    string Type,
    string? Status,
    string Trust,
    string? Verifier,
    string? VerifiedDate,
    int MoreVerifications,
    string? StaleAfterDate);
```

Replace

```csharp
public sealed record ViewerPage(
    ConceptId Id,
    string Title,
    string RelativeHtmlPath,
    IReadOnlyList<ViewerFrontmatterEntry> Frontmatter,
    string Body,
    IReadOnlyList<ViewerLink> Links,
    IReadOnlyList<ViewerLink> Backlinks);
```

with

```csharp
public sealed record ViewerPage(
    ConceptId Id,
    string Title,
    string RelativeHtmlPath,
    IReadOnlyList<ViewerFrontmatterEntry> Frontmatter,
    string Body,
    IReadOnlyList<ViewerLink> Links,
    IReadOnlyList<ViewerLink> Backlinks)
{
    /// <summary>
    /// The markdown the page's payload carries: <see cref="Body"/> without a
    /// leading level-1 heading that repeats the title (spec §11.3, C4). Null
    /// (a page built by hand) means <see cref="Body"/>.
    /// </summary>
    public string? DisplayBody { get; init; }

    /// <summary>The chips' data (C5); null for a page built by hand, which then shows no chips.</summary>
    public ViewerPageHead? Head { get; init; }
}
```

In `ViewerSite`, below the `Index` property, add:

```csharp
    /// <summary>
    /// The bundle's name for the header and the breadcrumb: the bundle root's
    /// folder name (spec §12.3). Null for a site built by hand: the writer
    /// derives it from <see cref="BundleRoot"/> the same way.
    /// </summary>
    public string? BundleName { get; init; }

    /// <summary>
    /// The graph page's file name at the site root: <c>graph.html</c>, else
    /// <c>graph-1.html</c>, <c>graph-2.html</c>… (A25). Null for a site built
    /// by hand: the writer computes it the same way. An explicit value must be
    /// one <c>[A-Za-z0-9._-]+.html</c> segment that collides with nothing.
    /// </summary>
    public string? GraphPagePath { get; init; }
```

- [ ] **Step 4: Build the page model in `SiteModel`**

In `src/OKF4net.Viewer/SiteModel.cs`, replace the usings with:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;
using OKF4net.Yaml;
```

Replace the body of `Build` (from `var concepts = bundle.Concepts;` to the closing `};` of the `return new ViewerSite(…) { … };`) with:

```csharp
        // The index first: the page head reads the trust tier and the stale
        // date from it and never re-derives them (spec §11.3, C5).
        var index = SiteIndex.Build(bundle);
        var indexed = new Dictionary<string, IndexConcept>(StringComparer.Ordinal);
        foreach (var concept in index.Concepts)
        {
            indexed[concept.Id.ToString()] = concept;
        }

        var concepts = bundle.Concepts;
        var pages = new List<ViewerPage>(concepts.Count);
        var entries = new List<IndexEntry>(concepts.Count);

        foreach (var concept in concepts)
        {
            // BuildPage already read this concept's frontmatter; reuse it
            // here instead of a second bundle.Get(id) lookup for the index
            // entry's Type/Description. The `?? string.Empty` fallbacks are
            // load-bearing: IndexGenerator groups an empty `type` under
            // "Other", so they must match the old TypeOf/DescriptionOf
            // behaviour exactly.
            var page = BuildPage(bundle, concept, indexed[concept.Id.ToString()]);
            pages.Add(page);
            entries.Add(new IndexEntry(
                Type: concept.Document.Frontmatter.Type ?? string.Empty,
                Title: page.Title,
                Link: page.RelativeHtmlPath,
                Description: concept.Document.Frontmatter.Description ?? string.Empty));
        }

        return new ViewerSite(
            bundle.Root,
            pages,
            IndexGenerator.BuildIndexText(entries),
            bundle.ParseErrors.Select(e => new ViewerParseError(e.Path, e.Error)).ToList())
        {
            Index = index,
            BundleName = BundleNameOf(bundle.Root),
            GraphPagePath = FreeGraphPagePath(pages),
        };
```

Replace the method `BuildPage` with:

```csharp
    private static ViewerPage BuildPage(Bundle bundle, Concept concept, IndexConcept indexed)
    {
        var frontmatter = concept.Document.Frontmatter;

        var title = DisplayTitle(concept);

        var links = bundle.LinksFrom(concept.Id)
            .Select(l => new ViewerLink(l.Raw, RelativeHref(concept.Id, l.Target) + FragmentOf(l.Raw), l.Exists))
            .ToList();

        var backlinks = bundle.Backlinks(concept.Id)
            .Select(source => new ViewerLink(
                source.ToString(),
                RelativeHref(concept.Id, source),
                Exists: true))
            .ToList();

        return new ViewerPage(
            concept.Id,
            title,
            PagePath(concept.Id),
            FrontmatterEntries(frontmatter),
            concept.Document.Body,
            links,
            backlinks)
        {
            DisplayBody = StripDuplicateTitle(concept.Document.Body, title),
            Head = BuildHead(frontmatter, indexed),
        };
    }
```

Replace the method `DisplayValue` (and its doc comment) with:

```csharp
    /// <summary>
    /// A frontmatter value as one display string, and whether it is a
    /// structure (spec §11.3, C6). A scalar is shown as written; a non-empty
    /// sequence of scalars is joined by <c>", "</c> (the mockup's
    /// <c>finance, margin, attested</c>); anything else keeps P1's compact YAML
    /// emission -- dropping it would silently hide <c>sources</c> and every
    /// structured producer key -- and is never truncated.
    /// </summary>
    private static (string Text, bool Structured) DisplayValue(YamlValue value)
    {
        if (value.AsDisplayString() is { } scalar)
        {
            return (scalar, false);
        }

        if (value.AsSequence() is { Count: > 0 } items)
        {
            var parts = new List<string>(items.Count);
            foreach (var item in items)
            {
                if (item.AsDisplayString() is not { } part)
                {
                    parts = null;
                    break;
                }

                parts.Add(part);
            }

            if (parts is not null)
            {
                return (string.Join(", ", parts), false);
            }
        }

        return (value.ToYamlString().TrimEnd('\n').Replace("\n", " "), value is YamlSequence or YamlMapping);
    }

    /// <summary>The keys the title and the chips already show: never among the four unfolded entries (A27).</summary>
    private static readonly HashSet<string> ShownAbove = new(StringComparer.Ordinal) { "type", "title", "status", "verified", "stale_after" };

    /// <summary>How many entries the folded frontmatter box shows (A27).</summary>
    internal const int FoldedEntries = 4;

    private static List<ViewerFrontmatterEntry> FrontmatterEntries(Frontmatter frontmatter)
    {
        var entries = new List<ViewerFrontmatterEntry>();
        var unfolded = 0;
        foreach (var e in frontmatter.AsMapping().Entries)
        {
            var key = e.Key.AsDisplayString() ?? e.Key.ToYamlString().TrimEnd('\n');
            var (text, structured) = DisplayValue(e.Value);
            var extra = true;
            if (!ShownAbove.Contains(key) && unfolded < FoldedEntries)
            {
                unfolded++;
                extra = false;
            }

            entries.Add(new ViewerFrontmatterEntry(key, text) { Structured = structured, Extra = extra });
        }

        return entries;
    }

    /// <summary>
    /// The chips' data (spec §11.3, C5). The tier and the stale date come from
    /// the index. Retained verifications are those with a <c>by</c>; the
    /// tier's set is the retained human ones for the human tier, all retained
    /// ones for the machine tier, none otherwise; the last of the set, in
    /// document order, is shown.
    /// </summary>
    private static ViewerPageHead BuildHead(Frontmatter frontmatter, IndexConcept indexed)
    {
        var status = frontmatter.Get("status")?.AsDisplayString();
        if (status is { Length: 0 })
        {
            status = null;
        }

        var retained = frontmatter.Verified.Where(s => s.By is not null).ToList();
        var set = indexed.Trust == AuditVocabulary.Name(TrustTier.HumanReviewed)
            ? retained.Where(s => s.By!.Value.IsHuman).ToList()
            : indexed.Trust == AuditVocabulary.Name(TrustTier.MachineConfirmed)
                ? retained
                : new List<Stamp>();

        string? verifier = null;
        string? date = null;
        if (set.Count > 0)
        {
            var last = set[^1];
            var by = last.By!.Value;
            verifier = by.IsHuman && by.IsWellFormed ? by.Id! : by.Raw;
            date = DateOf(last.At);
        }

        return new ViewerPageHead(
            frontmatter.Type ?? string.Empty,
            status,
            indexed.Trust,
            verifier,
            date,
            Math.Max(0, set.Count - 1),
            indexed.StaleAfterDate);
    }

    /// <summary>A verification date for display: <c>YYYY-MM-DD</c> when <paramref name="at"/> starts so, else as written; null when absent.</summary>
    private static string? DateOf(string? at)
    {
        if (string.IsNullOrEmpty(at))
        {
            return null;
        }

        if (at.Length >= 10)
        {
            var d = at.AsSpan(0, 10);
            var iso = d[4] == '-' && d[7] == '-'
                && char.IsAsciiDigit(d[0]) && char.IsAsciiDigit(d[1]) && char.IsAsciiDigit(d[2]) && char.IsAsciiDigit(d[3])
                && char.IsAsciiDigit(d[5]) && char.IsAsciiDigit(d[6]) && char.IsAsciiDigit(d[8]) && char.IsAsciiDigit(d[9]);
            if (iso)
            {
                return at[..10];
            }
        }

        return at;
    }

    /// <summary>
    /// <paramref name="body"/> without its first non-blank line when that line
    /// is a level-1 ATX heading at column 0 whose text equals
    /// <paramref name="title"/> (ordinal, after both are normalized), and
    /// without the blank lines before it (spec §11.3, C4). Lines are split by
    /// <see cref="LfLines"/> (a final <c>\r</c> stripped); a blank line is
    /// empty or made of spaces and tabs. Anything else -- setext, inline
    /// markup, a heading inside a fence, another text -- is left alone: a kept
    /// duplicate costs nothing, a wrong removal would delete content.
    /// </summary>
    /// <param name="body">The raw markdown body.</param>
    /// <param name="title">The display title.</param>
    internal static string StripDuplicateTitle(string body, string title)
    {
        var wanted = NormalizeSpaces(title);
        foreach (var line in LfLines.SplitSpans(body))
        {
            var text = body.AsSpan(line.Start, line.ContentEnd - line.Start);
            if (IsBlank(text))
            {
                continue;
            }

            return AtxH1Text(text) is { } heading && string.Equals(NormalizeSpaces(heading), wanted, StringComparison.Ordinal)
                ? body[line.TerminatorEnd..]
                : body;
        }

        return body;
    }

    private static bool IsBlank(ReadOnlySpan<char> line)
    {
        foreach (var ch in line)
        {
            if (ch != ' ' && ch != '\t')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The text of a level-1 ATX heading at column 0 (<c>#</c> then a space or
    /// a tab, or <c>#</c> alone), or null. The closing sequence of <c>#</c> is
    /// removed only when a space or a tab precedes it or it is the whole
    /// content (CommonMark §4.2: <c># C#</c> keeps "C#").
    /// </summary>
    private static string? AtxH1Text(ReadOnlySpan<char> line)
    {
        if (line.Length == 0 || line[0] != '#')
        {
            return null;
        }

        if (line.Length > 1 && line[1] != ' ' && line[1] != '\t')
        {
            return null;
        }

        var content = line[1..].TrimEnd(" \t");
        var k = content.Length;
        while (k > 0 && content[k - 1] == '#')
        {
            k--;
        }

        if (k < content.Length && (k == 0 || content[k - 1] == ' ' || content[k - 1] == '\t'))
        {
            content = content[..k];
        }

        return content.ToString();
    }

    /// <summary>Spaces and tabs at the ends removed, inner runs of them collapsed to one space.</summary>
    private static string NormalizeSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        var pending = false;
        foreach (var ch in text)
        {
            if (ch == ' ' || ch == '\t')
            {
                pending = sb.Length > 0;
                continue;
            }

            if (pending)
            {
                sb.Append(' ');
                pending = false;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>The bundle's name: the root folder's name, <c>bundle</c> when it has none (spec §12.3).</summary>
    /// <param name="root">The bundle root, absolute or relative, with or without a trailing separator.</param>
    internal static string BundleNameOf(string root)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        return string.IsNullOrEmpty(name) ? "bundle" : name;
    }

    /// <summary>
    /// The graph page's file name (spec §12.3, A25): the first of
    /// <c>graph.html</c>, <c>graph-1.html</c>, <c>graph-2.html</c>… equal,
    /// ignoring case, to none of: a page's path, <c>index.html</c>, the first
    /// segment of a page's path (a folder). Never a refusal to render.
    /// </summary>
    /// <param name="pages">The site's pages.</param>
    internal static string FreeGraphPagePath(IReadOnlyList<ViewerPage> pages)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index.html" };
        foreach (var page in pages)
        {
            taken.Add(page.RelativeHtmlPath);
            var slash = page.RelativeHtmlPath.IndexOf('/');
            if (slash > 0)
            {
                taken.Add(page.RelativeHtmlPath[..slash]);
            }
        }

        for (var n = 0; ; n++)
        {
            var name = n == 0 ? "graph.html" : $"graph-{n}.html";
            if (!taken.Contains(name))
            {
                return name;
            }
        }
    }
```

- [ ] **Step 5: Run the tests**

Run: `dotnet build OKF4net.sln` then `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer|FullyQualifiedName~OKF4net.Tests.Render"`
Expected: no warning; all pass — the 21 `Duplicate_title_rule` rows and the other `PageModelTests`, the 9 `GraphPagePathTests`, and P1's `SiteModelTests` (`Build_renders_a_non_scalar_frontmatter_value_rather_than_dropping_it` still finds `core` in the joined `tags`).

- [ ] **Step 6: Format and commit**

```bash
dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Viewer/ViewerModel.cs src/OKF4net.Viewer/SiteModel.cs tests/OKF4net.Tests/Viewer/PageModelTests.cs tests/OKF4net.Tests/Viewer/GraphPagePathTests.cs
git commit -m "feat(viewer): page model for the centre column (display body without the duplicate H1, chips, folded frontmatter), bundle name, graph page name

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 6: `OkfShapes` — the single source of every shape (control 10)

Wave 2 (after Task 4's measurement is merged: A23 wants the v2 index measured before any JS reads its new fields, and `slotOf` reads `typeIndex` and `types`). Creates `assets/okf-shapes.js` exactly to §12.2: pixel-exact geometry in absolute coordinates, the `SIZES` table, `icon`, `node`, `legend`, `typeLegendEntries`, `slotOf`, `trustKind`, `typeLabel`. No page loads it yet (Task 11 adds it to the script table); the harness loads it in a bare window through `okfShapes()` (Task 1).

**Files:**
- Create: `src/OKF4net.Viewer/Assets/okf-shapes.js`
- Modify: `tools/viewer-security-check/run.js` (under `// --- Task 6: OkfShapes (control 10) ---` only)

**Interfaces:**
- Consumes: Task 1's `okfShapes()` helper; the index v2 shape of §12.1 (`concepts[i].typeIndex`, `types[k].slot/name/count`) as data only.
- Produces `window.OkfShapes` (frozen), exactly §12.2:
  - `KINDS` = `["circle","square","diamond","triangle","ring","other"]` (frozen), `OTHER_SLOT` = 5, `BOXES` = `{ icon: 12, chip: 10, flag: 10 }`, `SIZES[context][kind]` → frozen `{ size, stroke, dash, ring, focus }` for contexts `icon`, `chip`, `flag`, `local`, `localCenter`, `graph`.
  - `typeLabel(name)`, `slotOf(index, position)`, `kindOf(index, position)`, `trustKind(trust)`.
  - `shape(kind, cx, cy, size, options)` → one `<circle>`, `<rect>` or `<path>` with class `okf-shape-0…5`, `okf-trust-human`, `okf-trust-machine`, `okf-stale-mark` or `okf-ghost-mark`.
  - `icon(kind, context, title)` → `<svg class="okf-glyph" width="B" height="B" viewBox="0 0 B B" aria-hidden="true" focusable="false">` with an optional first `<title>`.
  - `node(kind, cx, cy, size, options)` → `<g class="okf-node">` with optional `rect.okf-node-focus` (side + 20, dashed 3 3), optional `rect.okf-node-ring` (side + 12), then the shape; never a `<title>` or `<text>`.
  - `typeLegendEntries(index)` → `[{ role: "type", slot, label, count }]`, ranks 0–4 in `types` order, then `{ slot: 5, label: "Other types", count: sum }` when any type has slot 5.
  - `legend(entries)` → `<ul class="okf-legend">`; entries `{ role: "type", slot, label, count? }`, `{ role: "trust", trust }`, `{ role: "stale" }`, `{ role: "ghost" }`.
  - Every invalid input (unknown kind, context or role; non-finite or negative number; `dash` not a pair) throws `TypeError`.

- [ ] **Step 1: Write the failing harness cases**

In `run.js`, directly under `// --- Task 6: OkfShapes (control 10) ---`, insert:

```js
console.log("\nP1.1 — OkfShapes (control 10):");

// Spec §12.2, written out by hand: every shape of every context, as one
// element's tag and sorted attributes. Never recomputed from the module.
const SHAPE_ICONS = {
  icon: {
    circle: "circle class=okf-shape-0 cx=6 cy=6 r=5",
    square: "rect class=okf-shape-1 height=9 width=9 x=1.5 y=1.5",
    diamond: "path class=okf-shape-2 d=M6 0.35 L11.65 6 L6 11.65 L0.35 6 Z",
    triangle: "path class=okf-shape-3 d=M6 0.5 L11.5 11.5 L0.5 11.5 Z",
    ring: "circle class=okf-shape-4 cx=6 cy=6 r=4 stroke-width=2",
    other: "rect class=okf-shape-5 height=7.5 stroke-width=1.5 width=7.5 x=2.25 y=2.25",
    ghost: "circle class=okf-ghost-mark cx=6 cy=6 r=4.4 stroke-dasharray=2 2 stroke-width=1.2",
  },
  chip: {
    circle: "circle class=okf-shape-0 cx=5 cy=5 r=4",
    square: "rect class=okf-shape-1 height=8 width=8 x=1 y=1",
    diamond: "path class=okf-shape-2 d=M5 0.5 L9.5 5 L5 9.5 L0.5 5 Z",
    triangle: "path class=okf-shape-3 d=M5 0.5 L9.5 9.5 L0.5 9.5 Z",
    ring: "circle class=okf-shape-4 cx=5 cy=5 r=3.25 stroke-width=1.5",
    other: "rect class=okf-shape-5 height=6.5 stroke-width=1.5 width=6.5 x=1.75 y=1.75",
  },
  flag: {
    human: "circle class=okf-trust-human cx=5 cy=5 r=4",
    machine: "circle class=okf-trust-machine cx=5 cy=5 r=3 stroke-width=2",
    stale: "path class=okf-stale-mark d=M0.5 0 L9.5 0 L5 5 L9.5 10 L0.5 10 L5 5 Z",
  },
};

// node(kind, 100, 100, s.size, s) for every cell of the graph contexts: the
// children of the <g>, in order.
const SHAPE_NODES = {
  local: {
    circle: ["circle class=okf-shape-0 cx=100 cy=100 r=10"],
    square: ["rect class=okf-shape-1 height=23 width=23 x=88.5 y=88.5"],
    diamond: ["path class=okf-shape-2 d=M100 87.25 L112.75 100 L100 112.75 L87.25 100 Z"],
    triangle: ["path class=okf-shape-3 d=M100 89 L111 111 L89 111 Z"],
    ring: ["circle class=okf-shape-4 cx=100 cy=100 r=8.75 stroke-width=2.5"],
    other: ["rect class=okf-shape-5 height=18.5 stroke-width=1.5 width=18.5 x=90.75 y=90.75"],
    ghost: ["circle class=okf-ghost-mark cx=100 cy=100 r=9.9 stroke-dasharray=3 3 stroke-width=1.4"],
  },
  localCenter: {
    circle: ["rect class=okf-node-ring height=38 stroke-width=2 width=38 x=81 y=81", "circle class=okf-shape-0 cx=100 cy=100 r=13"],
    square: ["rect class=okf-node-ring height=40 stroke-width=2 width=40 x=80 y=80", "rect class=okf-shape-1 height=28 width=28 x=86 y=86"],
    diamond: ["rect class=okf-node-ring height=43.1 stroke-width=2 width=43.1 x=78.45 y=78.45", "path class=okf-shape-2 d=M100 84.45 L115.55 100 L100 115.55 L84.45 100 Z"],
    triangle: ["rect class=okf-node-ring height=36 stroke-width=2 width=36 x=82 y=82", "path class=okf-shape-3 d=M100 88 L112 112 L88 112 Z"],
    ring: ["rect class=okf-node-ring height=38 stroke-width=2 width=38 x=81 y=81", "circle class=okf-shape-4 cx=100 cy=100 r=11.5 stroke-width=3"],
    other: ["rect class=okf-node-ring height=38 stroke-width=2 width=38 x=81 y=81", "rect class=okf-shape-5 height=24 stroke-width=2 width=24 x=88 y=88"],
  },
  graph: {
    circle: ["rect class=okf-node-focus height=46 stroke-dasharray=3 3 stroke-width=2 width=46 x=77 y=77", "rect class=okf-node-ring height=38 stroke-width=2.4 width=38 x=81 y=81", "circle class=okf-shape-0 cx=100 cy=100 r=13"],
    square: ["rect class=okf-node-focus height=50 stroke-dasharray=3 3 stroke-width=2 width=50 x=75 y=75", "rect class=okf-node-ring height=42 stroke-width=2.4 width=42 x=79 y=79", "rect class=okf-shape-1 height=30 width=30 x=85 y=85"],
    diamond: ["rect class=okf-node-focus height=51.1 stroke-dasharray=3 3 stroke-width=2 width=51.1 x=74.45 y=74.45", "rect class=okf-node-ring height=43.1 stroke-width=2.4 width=43.1 x=78.45 y=78.45", "path class=okf-shape-2 d=M100 84.45 L115.55 100 L100 115.55 L84.45 100 Z"],
    triangle: ["rect class=okf-node-focus height=44 stroke-dasharray=3 3 stroke-width=2 width=44 x=78 y=78", "rect class=okf-node-ring height=36 stroke-width=2.4 width=36 x=82 y=82", "path class=okf-shape-3 d=M100 88 L112 112 L88 112 Z"],
    ring: ["rect class=okf-node-focus height=46 stroke-dasharray=3 3 stroke-width=2 width=46 x=77 y=77", "rect class=okf-node-ring height=38 stroke-width=2.4 width=38 x=81 y=81", "circle class=okf-shape-4 cx=100 cy=100 r=11.5 stroke-width=3"],
    other: ["rect class=okf-node-focus height=46 stroke-dasharray=3 3 stroke-width=2 width=46 x=77 y=77", "rect class=okf-node-ring height=38 stroke-width=2.4 width=38 x=81 y=81", "rect class=okf-shape-5 height=24 stroke-width=2 width=24 x=88 y=88"],
    ghost: ["rect class=okf-node-focus height=47.6 stroke-dasharray=3 3 stroke-width=2 width=47.6 x=76.2 y=76.2", "rect class=okf-node-ring height=39.6 stroke-width=2.4 width=39.6 x=80.2 y=80.2", "circle class=okf-ghost-mark cx=100 cy=100 r=13 stroke-dasharray=3 3 stroke-width=1.6"],
  },
};

const SVG_NS = "http://www.w3.org/2000/svg";
const SVG_ELEMENTS = new Set(["svg", "g", "circle", "rect", "path", "line", "text", "title", "defs", "marker"]);
const SVG_ATTRIBUTES = new Set(["viewBox", "width", "height", "class", "aria-hidden", "focusable", "role", "aria-label", "tabindex", "id",
  "cx", "cy", "r", "x", "y", "x1", "y1", "x2", "y2", "d", "stroke-width", "stroke-dasharray", "text-anchor", "dominant-baseline",
  "marker-end", "refX", "refY", "markerWidth", "markerHeight", "orient", "transform"]);
const SHAPE_CLASSES = new Set(["okf-glyph", "okf-shape-0", "okf-shape-1", "okf-shape-2", "okf-shape-3", "okf-shape-4", "okf-shape-5",
  "okf-trust-human", "okf-trust-machine", "okf-stale-mark", "okf-ghost-mark", "okf-node", "okf-node-ring", "okf-node-focus",
  "okf-legend", "okf-legend-count", "okf-legend-mono", "okf-glyph-blank"]);

function shapeSig(el) {
  return el.localName + " " + Array.from(el.attributes).map((a) => `${a.name}=${a.value}`).sort().join(" ");
}

function eachElement(root, visit) {
  visit(root);
  for (const child of Array.from(root.children)) { eachElement(child, visit); }
}

// Only the fixed vocabulary of §12.2 and fixed class names, anywhere in `root`.
function assertShapeVocabulary(root, where) {
  eachElement(root, (el) => {
    if (el.namespaceURI === SVG_NS) {
      assert(SVG_ELEMENTS.has(el.localName), `${where}: SVG element <${el.localName}> is not in the fixed vocabulary`);
      for (const a of Array.from(el.attributes)) { assert(SVG_ATTRIBUTES.has(a.name), `${where}: SVG attribute ${a.name} is not in the fixed vocabulary`); }
    } else {
      assert(["ul", "li", "span"].includes(el.localName), `${where}: unexpected HTML element <${el.localName}>`);
    }
    for (const c of Array.from(el.classList)) { assert(SHAPE_CLASSES.has(c), `${where}: class "${c}" is not a fixed class`); }
  });
}

function throwsTypeError(fn, what) {
  let error = null;
  try { fn(); } catch (e) { error = e; }
  assert(error && error.name === "TypeError", `${what}: expected a TypeError, got ${error ? error.name + ": " + error.message : "no error"}`);
}

check("OkfShapes: every shape of every context has exactly the attributes of spec §12.2", () => {
  const { OkfShapes } = okfShapes();
  for (const [context, cells] of Object.entries(SHAPE_ICONS)) {
    const box = OkfShapes.BOXES[context];
    for (const [kind, want] of Object.entries(cells)) {
      const svg = OkfShapes.icon(kind, context);
      assert(shapeSig(svg) === `svg aria-hidden=true class=okf-glyph focusable=false height=${box} viewBox=0 0 ${box} ${box} width=${box}`, `${context}/${kind} <svg>: ${shapeSig(svg)}`);
      assert(svg.children.length === 1 && shapeSig(svg.firstElementChild) === want, `${context}/${kind}: ${svg.firstElementChild && shapeSig(svg.firstElementChild)}, expected ${want}`);
    }
  }
  for (const [context, cells] of Object.entries(SHAPE_NODES)) {
    for (const [kind, want] of Object.entries(cells)) {
      const s = OkfShapes.SIZES[context][kind];
      const g = OkfShapes.node(kind, 100, 100, s.size, s);
      assert(shapeSig(g) === "g class=okf-node", `${context}/${kind} <g>: ${shapeSig(g)}`);
      const got = Array.from(g.children, shapeSig);
      assert(JSON.stringify(got) === JSON.stringify(want), `${context}/${kind}: ${JSON.stringify(got)}, expected ${JSON.stringify(want)}`);
    }
  }
  const cells = Object.entries(OkfShapes.SIZES).map(([context, row]) => `${context}:${Object.keys(row).join(",")}`).join(" ");
  assert(cells === "icon:circle,square,diamond,triangle,ring,other,ghost chip:circle,square,diamond,triangle,ring,other flag:human,machine,stale local:circle,square,diamond,triangle,ring,other,ghost localCenter:circle,square,diamond,triangle,ring,other graph:circle,square,diamond,triangle,ring,other,ghost",
    `the SIZES table has other cells: ${cells}`);
  assert(JSON.stringify(Array.from(OkfShapes.KINDS)) === JSON.stringify(["circle", "square", "diamond", "triangle", "ring", "other"]) && OkfShapes.OTHER_SLOT === 5,
    `KINDS ${JSON.stringify(Array.from(OkfShapes.KINDS))}, OTHER_SLOT ${OkfShapes.OTHER_SLOT}`);
  assert(Object.isFrozen(OkfShapes) && Object.isFrozen(OkfShapes.KINDS) && Object.isFrozen(OkfShapes.SIZES) && Object.isFrozen(OkfShapes.SIZES.graph) && Object.isFrozen(OkfShapes.SIZES.graph.ghost),
    "OkfShapes or its tables are not frozen");
});

check("OkfShapes: the hourglass has the d of no type shape, and a node never carries a title or a text", () => {
  const { OkfShapes } = okfShapes();
  const stale = OkfShapes.shape("stale", 5, 5, 10).getAttribute("d");
  for (const kind of ["diamond", "triangle"]) {
    assert(OkfShapes.shape(kind, 5, 5, 10).getAttribute("d") !== stale, `the hourglass has the d of a ${kind}`);
  }
  assert(stale.split(" ").filter((p) => /^[ML]/.test(p)).length === 6, `the hourglass is not one path of six points: ${stale}`);
  for (const [context, cells] of Object.entries(SHAPE_NODES)) {
    for (const kind of Object.keys(cells)) {
      const s = OkfShapes.SIZES[context][kind];
      const g = OkfShapes.node(kind, 100, 100, s.size, s);
      assert(g.querySelector("title, text") === null, `node(${kind}) in ${context} carries a title or a text`);
    }
  }
});

check("OkfShapes: only the fixed SVG vocabulary and fixed classes; bundle text stays text", () => {
  const { OkfShapes, document } = okfShapes();
  const hostile = '<img src=x onerror="window.__pwned=1"> okf-shape-0" class="evil';
  const outputs = [
    OkfShapes.icon("circle", "icon", hostile),
    OkfShapes.icon("ghost", "icon"),
    OkfShapes.icon("stale", "flag"),
    OkfShapes.node("ghost", 10, 10, 27.6, OkfShapes.SIZES.graph.ghost),
    OkfShapes.legend([
      { role: "type", slot: 0, label: hostile, count: 3 },
      { role: "type", slot: 5, label: "Other types" },
      { role: "trust", trust: "human-reviewed" },
      { role: "trust", trust: "machine-confirmed" },
      { role: "trust", trust: "unverified" },
      { role: "stale" },
      { role: "ghost" },
    ]),
  ];
  for (const out of outputs) {
    assertShapeVocabulary(out, out.localName);
    assert(out.querySelectorAll("img, script").length === 0, "bundle text became markup");
  }
  assert(outputs[0].firstElementChild.localName === "title" && outputs[0].firstElementChild.textContent === hostile, "an icon title is not inert text in a first <title>");
  assert(outputs[4].textContent.includes(hostile), "a legend label was dropped instead of shown as text");
  assert(document.defaultView.__pwned === undefined, "bundle text executed");
});

check("OkfShapes: non-finite numbers, unknown kinds, contexts and roles throw a TypeError", () => {
  const { OkfShapes } = okfShapes();
  for (const kind of ["hexagon", "__proto__", "constructor", "toString", "", undefined]) {
    throwsTypeError(() => OkfShapes.shape(kind, 0, 0, 10), `shape(${String(kind)})`);
    throwsTypeError(() => OkfShapes.icon(kind, "icon"), `icon(${String(kind)})`);
    throwsTypeError(() => OkfShapes.node(kind, 0, 0, 10), `node(${String(kind)})`);
  }
  throwsTypeError(() => OkfShapes.shape("circle", NaN, 0, 10), "cx NaN");
  throwsTypeError(() => OkfShapes.shape("circle", 0, Infinity, 10), "cy Infinity");
  throwsTypeError(() => OkfShapes.shape("circle", 0, 0, NaN), "size NaN");
  throwsTypeError(() => OkfShapes.shape("circle", 0, 0, -1), "size negative");
  throwsTypeError(() => OkfShapes.shape("circle", 0, 0, 0), "size zero");
  throwsTypeError(() => OkfShapes.shape("circle", "0", 0, 10), "cx a string");
  throwsTypeError(() => OkfShapes.shape("ring", 0, 0, 10, { stroke: NaN }), "stroke NaN");
  throwsTypeError(() => OkfShapes.shape("ring", 0, 0, 10, { stroke: -1 }), "stroke negative");
  throwsTypeError(() => OkfShapes.shape("ghost", 0, 0, 10, { stroke: 1, dash: [1, NaN] }), "dash NaN");
  throwsTypeError(() => OkfShapes.shape("ghost", 0, 0, 10, { stroke: 1, dash: [1] }), "dash not a pair");
  throwsTypeError(() => OkfShapes.shape("ghost", 0, 0, 10, { stroke: 1, dash: "3 3" }), "dash a string");
  throwsTypeError(() => OkfShapes.node("square", 0, 0, 10, { focus: Infinity }), "focus Infinity");
  throwsTypeError(() => OkfShapes.node("square", 0, 0, 10, { ring: -2 }), "ring negative");
  for (const context of ["nope", "constructor", "__proto__", "local", "graph"]) {
    throwsTypeError(() => OkfShapes.icon("circle", context), `icon context ${context}`);
  }
  throwsTypeError(() => OkfShapes.icon("human", "icon"), "a trust shape in the icon context");
  throwsTypeError(() => OkfShapes.icon("stale", "chip"), "the hourglass in the chip context");
  throwsTypeError(() => OkfShapes.icon("ghost", "chip"), "a ghost in the chip context");
  throwsTypeError(() => OkfShapes.legend([{ role: "nope" }]), "legend role");
  throwsTypeError(() => OkfShapes.legend([{ role: "type", slot: 6, label: "x" }]), "legend slot 6");
  throwsTypeError(() => OkfShapes.legend([{ role: "trust", trust: "__proto__" }]), "legend trust");
});

check("OkfShapes: ranks are read from the index, never recomputed; anything out of bounds is the other slot", () => {
  const { OkfShapes } = okfShapes();
  // Deliberately NOT in count order: the module must read slot, not counts.
  const index = {
    concepts: [{ typeIndex: 0 }, { typeIndex: 1 }, { typeIndex: 9 }, { typeIndex: "0" }, {}, { typeIndex: 2 }],
    types: [{ name: "Rare", count: 1, slot: 3 }, { name: "Common", count: 9, slot: 0 }, { name: "Bad", count: 2, slot: 7 }],
  };
  const slots = [0, 1, 2, 3, 4, 5, -1, 1.5, "0", 99].map((p) => OkfShapes.slotOf(index, p));
  assert(JSON.stringify(slots) === JSON.stringify([3, 0, 5, 5, 5, 5, 5, 5, 5, 5]), `slots ${JSON.stringify(slots)}`);
  assert(OkfShapes.kindOf(index, 0) === "triangle" && OkfShapes.kindOf(index, 1) === "circle" && OkfShapes.kindOf(index, 2) === "other", "kindOf does not map slots to KINDS");
  assert(OkfShapes.slotOf(null, 0) === 5 && OkfShapes.slotOf({ concepts: index.concepts }, 0) === 5, "an index without types does not fall back to the other slot");
  const entries = OkfShapes.typeLegendEntries({ types: [
    { name: "A", count: 5, slot: 0 }, { name: "", count: 4, slot: 5 }, { name: "B", count: 3, slot: 1 }, { name: "C", count: 2, slot: 5 },
  ] });
  assert(JSON.stringify(entries) === JSON.stringify([
    { role: "type", slot: 0, label: "A", count: 5 }, { role: "type", slot: 1, label: "B", count: 3 }, { role: "type", slot: 5, label: "Other types", count: 6 },
  ]), `typeLegendEntries: ${JSON.stringify(entries)}`);
  const plain = OkfShapes.typeLegendEntries({ types: [{ name: "A", count: 1, slot: 0 }] });
  assert(plain.length === 1 && plain[0].label === "A", `no other type, yet: ${JSON.stringify(plain)}`);
  assert(OkfShapes.typeLabel("") === "(no type)" && OkfShapes.typeLabel("Metric") === "Metric", "typeLabel");
  assert(OkfShapes.trustKind("human-reviewed") === "human" && OkfShapes.trustKind("machine-confirmed") === "machine"
    && OkfShapes.trustKind("unverified") === null && OkfShapes.trustKind("constructor") === null, "trustKind");
});

check("OkfShapes: the legend draws each role as spec §12.2 says", () => {
  const { OkfShapes } = okfShapes();
  const ul = OkfShapes.legend([
    { role: "trust", trust: "human-reviewed" },
    { role: "trust", trust: "unverified" },
    { role: "stale" },
    { role: "ghost" },
    { role: "type", slot: 4, label: "Skill", count: 1 },
  ]);
  assert(ul.localName === "ul" && ul.className === "okf-legend" && ul.children.length === 5, `legend: <${ul.localName} class="${ul.className}"> with ${ul.children.length} items`);
  const [human, unverified, stale, ghost, type] = Array.from(ul.children);
  assert(human.firstElementChild.localName === "svg" && human.querySelector(".okf-trust-human") && human.textContent === "human-reviewed", "trust entry");
  assert(unverified.firstElementChild.className === "okf-glyph-blank" && unverified.textContent === "unverified", "unverified entry has no blank 10 px slot");
  assert(stale.querySelector(".okf-stale-mark") && stale.textContent === "stale (now " + String.fromCharCode(0x2265) + " stale_after)"
    && stale.querySelector(".okf-legend-mono").textContent === "stale_after", `stale entry: ${stale.textContent}`);
  assert(ghost.querySelector(".okf-ghost-mark") && ghost.textContent === "absent concept", "ghost entry");
  assert(type.querySelector(".okf-shape-4") && type.querySelector(".okf-legend-count").textContent === "1" && type.textContent === "Skill1", `type entry: ${type.textContent}`);
});
```

- [ ] **Step 2: Run the harness to verify the new cases fail**

```bash
cd tools/viewer-security-check && npm test
```

Expected: the six `OkfShapes: …` cases `FAIL` with `ENOENT: no such file or directory … okf-shapes.js`; every other case `ok`.

- [ ] **Step 3: Write `okf-shapes.js`**

Create `src/OKF4net.Viewer/Assets/okf-shapes.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The one source of every type, trust, staleness and ghost shape the viewer
// draws (spec §12.2): explorer, palette, chips, lists, legends and graphs all
// call this module; none draws such a shape itself. Pure geometry in absolute
// coordinates (no transform), a fixed SVG vocabulary, colours only through
// fixed classes that viewer.css maps to the §11.0 tokens, and every number
// checked finite. Bundle text never becomes a class, an id or an attribute
// here: titles and labels go through textContent. Loading this file only
// defines window.OkfShapes; elements are created in this window's document.
(function () {
  "use strict";
  var SVG_NS = "http://www.w3.org/2000/svg";
  var doc = window.document;

  var KINDS = Object.freeze(["circle", "square", "diamond", "triangle", "ring", "other"]);
  var OTHER_SLOT = 5;
  var BOXES = Object.freeze({ icon: 12, chip: 10, flag: 10 });

  // Class of each shape: the six type kinds by rank, then the marks.
  var SHAPE_CLASS = Object.freeze({
    circle: "okf-shape-0", square: "okf-shape-1", diamond: "okf-shape-2",
    triangle: "okf-shape-3", ring: "okf-shape-4", other: "okf-shape-5",
    human: "okf-trust-human", machine: "okf-trust-machine", stale: "okf-stale-mark", ghost: "okf-ghost-mark",
  });

  function cell(values, ring, focus) {
    return Object.freeze({
      size: values[0],
      stroke: values.length > 1 ? values[1] : 0,
      dash: values.length > 2 ? Object.freeze(values[2].slice()) : null,
      ring: ring,
      focus: focus,
    });
  }

  function row(ring, focus, cells) {
    var out = {};
    var kinds = Object.keys(cells);
    for (var i = 0; i < kinds.length; i++) { out[kinds[i]] = cell(cells[kinds[i]], ring, focus); }
    return Object.freeze(out);
  }

  // Spec §12.2: size (then stroke, then dash) per context and shape, read in
  // the mockups; the cells no mockup shows are drafting choices (§13).
  var SIZES = Object.freeze({
    icon: row(0, 0, { circle: [10], square: [9], diamond: [11.3], triangle: [11], ring: [10, 2], other: [9, 1.5], ghost: [10, 1.2, [2, 2]] }),
    chip: row(0, 0, { circle: [8], square: [8], diamond: [9], triangle: [9], ring: [8, 1.5], other: [8, 1.5] }),
    flag: row(0, 0, { human: [8], machine: [8, 2], stale: [10] }),
    local: row(0, 0, { circle: [20], square: [23], diamond: [25.5], triangle: [22], ring: [20, 2.5], other: [20, 1.5], ghost: [21.2, 1.4, [3, 3]] }),
    localCenter: row(2, 0, { circle: [26], square: [28], diamond: [31.1], triangle: [24], ring: [26, 3], other: [26, 2] }),
    graph: row(2.4, 2, { circle: [26], square: [30], diamond: [31.1], triangle: [24], ring: [26, 3], other: [26, 2], ghost: [27.6, 1.6, [3, 3]] }),
  });

  var TRUST_NAMES = Object.freeze(["human-reviewed", "machine-confirmed", "unverified"]);

  function own(table, key) {
    return typeof key === "string" && Object.prototype.hasOwnProperty.call(table, key);
  }

  function checkKind(kind) {
    if (!own(SHAPE_CLASS, kind)) { throw new TypeError("OkfShapes: unknown shape " + String(kind)); }
  }

  function finite(value, what) {
    if (typeof value !== "number" || !Number.isFinite(value)) { throw new TypeError("OkfShapes: " + what + " is not a finite number"); }
    return value;
  }

  function nonNegative(value, what) {
    if (finite(value, what) < 0) { throw new TypeError("OkfShapes: " + what + " is negative"); }
    return value;
  }

  function positive(value, what) {
    if (!(finite(value, what) > 0)) { throw new TypeError("OkfShapes: " + what + " is not positive"); }
    return value;
  }

  // Rounded to the hundredth and written by String (spec §12.2).
  function fmt(value) {
    return String(Math.round(value * 100) / 100);
  }

  function readOptions(options) {
    var o = options || {};
    var dash = o.dash === undefined ? null : o.dash;
    if (dash !== null) {
      if (!Array.isArray(dash) || dash.length !== 2) { throw new TypeError("OkfShapes: dash is neither a pair nor null"); }
      nonNegative(dash[0], "dash");
      nonNegative(dash[1], "dash");
    }
    return {
      stroke: o.stroke === undefined ? 0 : nonNegative(o.stroke, "stroke"),
      dash: dash,
      ring: o.ring === undefined ? 0 : nonNegative(o.ring, "ring"),
      focus: o.focus === undefined ? 0 : nonNegative(o.focus, "focus"),
    };
  }

  function svg(name) {
    return doc.createElementNS(SVG_NS, name);
  }

  // "M6 0.35 L11.65 6 … Z": a command is glued to its first number; numbers
  // and commands are separated by one space.
  function pathData(points) {
    var parts = [];
    for (var i = 0; i < points.length; i++) {
      parts.push(points[i][0] + fmt(points[i][1]), fmt(points[i][2]));
    }
    parts.push("Z");
    return parts.join(" ");
  }

  // One element whose bounding box fits the size x size square centred on
  // (cx, cy); for a stroked shape, the stroke's OUTER edge is on that square.
  function shape(kind, cx, cy, size, options) {
    checkKind(kind);
    finite(cx, "cx");
    finite(cy, "cy");
    positive(size, "size");
    var o = readOptions(options);
    var h = size / 2;
    var w = o.stroke;
    var el;
    if (kind === "circle" || kind === "human") {
      el = svg("circle");
      el.setAttribute("cx", fmt(cx));
      el.setAttribute("cy", fmt(cy));
      el.setAttribute("r", fmt(h));
    } else if (kind === "square") {
      el = svg("rect");
      el.setAttribute("x", fmt(cx - h));
      el.setAttribute("y", fmt(cy - h));
      el.setAttribute("width", fmt(size));
      el.setAttribute("height", fmt(size));
    } else if (kind === "diamond") {
      el = svg("path");
      el.setAttribute("d", pathData([["M", cx, cy - h], ["L", cx + h, cy], ["L", cx, cy + h], ["L", cx - h, cy]]));
    } else if (kind === "triangle") {
      el = svg("path");
      el.setAttribute("d", pathData([["M", cx, cy - h], ["L", cx + h, cy + h], ["L", cx - h, cy + h]]));
    } else if (kind === "ring" || kind === "machine" || kind === "ghost") {
      el = svg("circle");
      el.setAttribute("cx", fmt(cx));
      el.setAttribute("cy", fmt(cy));
      el.setAttribute("r", fmt((size - w) / 2));
      el.setAttribute("stroke-width", fmt(w));
      if (kind === "ghost" && o.dash) {
        el.setAttribute("stroke-dasharray", fmt(o.dash[0]) + " " + fmt(o.dash[1]));
      }
    } else if (kind === "other") {
      var side = size - w;
      el = svg("rect");
      el.setAttribute("x", fmt(cx - side / 2));
      el.setAttribute("y", fmt(cy - side / 2));
      el.setAttribute("width", fmt(side));
      el.setAttribute("height", fmt(side));
      el.setAttribute("stroke-width", fmt(w));
    } else {
      // stale: an hourglass, two triangles joined tip to tip, 0.9 x size wide.
      var a = 0.45 * size;
      el = svg("path");
      el.setAttribute("d", pathData([["M", cx - a, cy - h], ["L", cx + a, cy - h], ["L", cx, cy], ["L", cx + a, cy + h], ["L", cx - a, cy + h], ["L", cx, cy]]));
    }
    el.setAttribute("class", SHAPE_CLASS[kind]);
    return el;
  }

  function sizeOf(context, kind) {
    if (!own(SIZES, context) || !own(SIZES[context], kind)) {
      throw new TypeError("OkfShapes: no size for " + String(kind) + " in context " + String(context));
    }
    return SIZES[context][kind];
  }

  // A self-contained glyph for HTML: an svg of the context's box with the
  // shape centred in it; `title`, as text, in a first <title>.
  function icon(kind, context, title) {
    checkKind(kind);
    if (!own(BOXES, context)) { throw new TypeError("OkfShapes: unknown icon context " + String(context)); }
    var s = sizeOf(context, kind);
    var box = BOXES[context];
    var el = svg("svg");
    el.setAttribute("class", "okf-glyph");
    el.setAttribute("width", String(box));
    el.setAttribute("height", String(box));
    el.setAttribute("viewBox", "0 0 " + box + " " + box);
    el.setAttribute("aria-hidden", "true");
    el.setAttribute("focusable", "false");
    if (title !== undefined && title !== null) {
      var t = svg("title");
      t.textContent = String(title);
      el.appendChild(t);
    }
    el.appendChild(shape(kind, box / 2, box / 2, s.size, s));
    return el;
  }

  function outline(className, cx, cy, side, width, dashed) {
    var r = svg("rect");
    r.setAttribute("class", className);
    r.setAttribute("x", fmt(cx - side / 2));
    r.setAttribute("y", fmt(cy - side / 2));
    r.setAttribute("width", fmt(side));
    r.setAttribute("height", fmt(side));
    r.setAttribute("stroke-width", fmt(width));
    if (dashed) { r.setAttribute("stroke-dasharray", "3 3"); }
    return r;
  }

  // A graph node: focus outline (side + 20, dashed), selection outline
  // (side + 12), then the shape. The outlines show only under the fixed
  // classes okf-focused / okf-selected the caller toggles on the <g>. No
  // <title>, no <text>: the caller adds them (X7, G11, G13).
  function node(kind, cx, cy, size, options) {
    checkKind(kind);
    finite(cx, "cx");
    finite(cy, "cy");
    positive(size, "size");
    var o = readOptions(options);
    var g = svg("g");
    g.setAttribute("class", "okf-node");
    if (o.focus > 0) { g.appendChild(outline("okf-node-focus", cx, cy, size + 20, o.focus, true)); }
    if (o.ring > 0) { g.appendChild(outline("okf-node-ring", cx, cy, size + 12, o.ring, false)); }
    g.appendChild(shape(kind, cx, cy, size, options));
    return g;
  }

  function typeLabel(name) {
    return name === "" ? "(no type)" : String(name);
  }

  // The rank (0..5) of the concept at `position`, read from the index's
  // types table, never recomputed; OTHER_SLOT whenever anything is missing
  // or out of bounds.
  function slotOf(index, position) {
    if (!index || !Array.isArray(index.concepts) || !Array.isArray(index.types)) { return OTHER_SLOT; }
    if (!Number.isInteger(position) || position < 0 || position >= index.concepts.length) { return OTHER_SLOT; }
    var concept = index.concepts[position];
    var at = concept ? concept.typeIndex : undefined;
    if (!Number.isInteger(at) || at < 0 || at >= index.types.length) { return OTHER_SLOT; }
    var slot = index.types[at] ? index.types[at].slot : undefined;
    return Number.isInteger(slot) && slot >= 0 && slot <= OTHER_SLOT ? slot : OTHER_SLOT;
  }

  function kindOf(index, position) {
    return KINDS[slotOf(index, position)];
  }

  function trustKind(trust) {
    return trust === "human-reviewed" ? "human" : trust === "machine-confirmed" ? "machine" : null;
  }

  // Ranks 0-4 in the order of `types`, then one "Other types" entry summing
  // every type of the other slot, when there is one.
  function typeLegendEntries(index) {
    var out = [];
    var types = index && Array.isArray(index.types) ? index.types : [];
    var other = 0;
    var hasOther = false;
    for (var i = 0; i < types.length; i++) {
      var t = types[i];
      var count = t && Number.isInteger(t.count) ? t.count : 0;
      if (t && Number.isInteger(t.slot) && t.slot >= 0 && t.slot < OTHER_SLOT) {
        out.push({ role: "type", slot: t.slot, label: typeLabel(t.name), count: count });
      } else {
        hasOther = true;
        other += count;
      }
    }
    if (hasOther) { out.push({ role: "type", slot: OTHER_SLOT, label: "Other types", count: other }); }
    return out;
  }

  function html(tag, className, text) {
    var el = doc.createElement(tag);
    if (className) { el.className = className; }
    if (text !== undefined) { el.textContent = text; }
    return el;
  }

  function legendItem(entry) {
    var li = doc.createElement("li");
    var role = entry ? entry.role : undefined;
    if (role === "type") {
      var slot = entry.slot;
      if (!Number.isInteger(slot) || slot < 0 || slot > OTHER_SLOT) { throw new TypeError("OkfShapes: legend slot out of range"); }
      li.appendChild(icon(KINDS[slot], "icon"));
      li.appendChild(html("span", "", String(entry.label)));
      if (entry.count !== undefined) { li.appendChild(html("span", "okf-legend-count", String(entry.count))); }
    } else if (role === "trust") {
      if (TRUST_NAMES.indexOf(entry.trust) === -1) { throw new TypeError("OkfShapes: unknown trust tier"); }
      var kind = trustKind(entry.trust);
      li.appendChild(kind ? icon(kind, "flag") : html("span", "okf-glyph-blank"));
      li.appendChild(html("span", "", entry.trust));
    } else if (role === "stale") {
      li.appendChild(icon("stale", "flag"));
      var label = html("span", "", "stale (now " + String.fromCharCode(0x2265) + " ");
      label.appendChild(html("span", "okf-legend-mono", "stale_after"));
      label.appendChild(doc.createTextNode(")"));
      li.appendChild(label);
    } else if (role === "ghost") {
      li.appendChild(icon("ghost", "icon"));
      li.appendChild(html("span", "", "absent concept"));
    } else {
      throw new TypeError("OkfShapes: unknown legend role " + String(role));
    }
    return li;
  }

  function legend(entries) {
    var ul = html("ul", "okf-legend");
    for (var i = 0; i < entries.length; i++) { ul.appendChild(legendItem(entries[i])); }
    return ul;
  }

  window.OkfShapes = Object.freeze({
    KINDS: KINDS,
    OTHER_SLOT: OTHER_SLOT,
    BOXES: BOXES,
    SIZES: SIZES,
    typeLabel: typeLabel,
    slotOf: slotOf,
    kindOf: kindOf,
    trustKind: trustKind,
    shape: shape,
    icon: icon,
    node: node,
    typeLegendEntries: typeLegendEntries,
    legend: legend,
  });
})();
```

- [ ] **Step 4: Run the harness**

```bash
cd tools/viewer-security-check && npm test
```

Expected: the six `OkfShapes: …` cases `ok`; `N passed, 0 failed`.

- [ ] **Step 5: Prove the attribute table bites (not committed)**

In `okf-shapes.js`, change the `graph` row's `diamond: [31.1]` to `diamond: [31]`, run `npm test`: `FAIL  - OkfShapes: every shape of every context has exactly the attributes of spec §12.2` naming `graph/diamond`. Restore the line and rerun: green.

- [ ] **Step 6: Run the .NET suite (the new asset is embedded and written)**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlWriterAssetsTests"`
Expected: pass (`okf-shapes.js` now appears in `ViewerAssets.Paths` and in every written site).

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-shapes.js tools/viewer-security-check/run.js
git commit -m "feat(viewer): OkfShapes, the single pixel-exact source of type, trust, staleness and ghost shapes

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Theme button with the mockups' moon (H11), and the `data-okf-js` mark (C6)

Wave 2. The toggle keeps P1's name "Dark theme" and `aria-pressed` under the 34 × 34 icon of the mockups; `okf-theme.js`, which runs in `<head>`, also marks `<html>` with `data-okf-js` so the frontmatter box can fold without a layout jump (Task 12's CSS, §11.3 C6).

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/okf-theme.js` (whole file)
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` (under `/* --- Task 7: theme button (H11) --- */` only)
- Modify: `tools/viewer-security-check/run.js` (under `// --- Task 7: theme button ---` only)

**Interfaces:**
- Consumes: `#okf-tools` (P1 shell).
- Produces: `button#okf-theme-toggle.okf-tool.okf-theme-toggle[aria-label="Dark theme"][aria-pressed]` containing `svg.okf-theme-icon > path` (the moon), appended last to `#okf-tools`; `<html data-okf-js="">` set synchronously while `okf-theme.js` runs.

- [ ] **Step 1: Write the failing harness cases**

Under `// --- Task 7: theme button ---`, insert:

```js
checkAsync("theme: the toggle is a 34 px icon button named Dark theme with the moon of the mockups", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const toggle = doc.getElementById("okf-theme-toggle");
  assert(toggle && toggle.getAttribute("aria-label") === "Dark theme" && toggle.textContent.trim() === "",
    `toggle name "${toggle && toggle.getAttribute("aria-label")}", text "${toggle && toggle.textContent.trim()}"`);
  assert(toggle.hasAttribute("aria-pressed"), "the toggle lost its pressed state");
  assert(doc.getElementById("okf-tools").lastElementChild === toggle, "the toggle is not the last tool");
  const svg = toggle.querySelector("svg");
  assert(svg && svg.namespaceURI === "http://www.w3.org/2000/svg" && svg.getAttribute("aria-hidden") === "true" && svg.getAttribute("focusable") === "false",
    "the icon is not a decorative SVG");
  const paths = svg.querySelectorAll("path");
  assert(paths.length === 1 && paths[0].getAttribute("d") === "M13 9.5A5.5 5.5 0 0 1 6.5 3a5.5 5.5 0 1 0 6.5 6.5z" && paths[0].getAttribute("stroke-width") === "1.5",
    `moon path: ${paths.length ? paths[0].getAttribute("d") : "none"}`);
  for (const el of [svg, ...svg.querySelectorAll("*")]) {
    for (const a of Array.from(el.attributes)) {
      assert(["class", "width", "height", "viewBox", "aria-hidden", "focusable", "d", "stroke-width"].includes(a.name), `the icon carries a ${a.name} attribute (colours belong to viewer.css)`);
    }
  }
  const style = window.getComputedStyle(toggle);
  assert(style.getPropertyValue("width") === "34px" && style.getPropertyValue("height") === "34px",
    `toggle ${style.getPropertyValue("width")} x ${style.getPropertyValue("height")}`);
});

checkAsync("theme: data-okf-js marks <html> while okf-theme.js runs, with or without storage", async () => {
  for (const opts of [{}, { storage: "denied" }]) {
    let atScriptLoad = "not recorded";
    // openPage denies storage itself (opts.storage) before calling beforeParse.
    const window = await openPage("foo.html", Object.assign({}, opts, {
      beforeParse(w) {
        w.document.addEventListener("load", (e) => {
          const src = e.target && e.target.nodeType === 1 ? e.target.getAttribute("src") || "" : "";
          if (/okf-theme\.js$/.test(src)) { atScriptLoad = w.document.documentElement.hasAttribute("data-okf-js"); }
        }, true);
      },
    }));
    assert(atScriptLoad === true, `${JSON.stringify(opts)}: when okf-theme.js had run, data-okf-js was ${atScriptLoad}`);
    assert(window.document.documentElement.getAttribute("data-okf-js") === "", "data-okf-js is not an empty marker");
  }
});
```

- [ ] **Step 2: Run the harness to verify the cases fail**

```bash
cd tools/viewer-security-check && npm test
```

Expected: `FAIL  - theme: the toggle is a 34 px icon button …` (`toggle name "null", text "Dark theme"`), `FAIL  - theme: data-okf-js marks <html> …` (`data-okf-js was false`).

- [ ] **Step 3: Rewrite `okf-theme.js`**

Replace the whole content of `src/OKF4net.Viewer/Assets/okf-theme.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Theme (spec §4.7). Loaded in <head>, before the stylesheet, so a stored
// choice applies before the first paint on every page. Storage can be
// unavailable (file:// in some browsers, privacy modes) and, even when a
// write succeeds, local pages may not share it: every access is guarded and
// without it the page follows prefers-color-scheme.
//
// It also marks <html> with data-okf-js as soon as it runs: viewer.css folds
// the frontmatter box only under that mark (spec §11.3, C6), so a page
// without JavaScript shows every entry, and a page with it never shows them
// all for a frame before okf-page.js has run.
(function () {
  "use strict";
  var KEY = "okf-theme";
  var SVG_NS = "http://www.w3.org/2000/svg";
  // The moon of the mockups (H11), a constant of this module (spec §4.5).
  var MOON = "M13 9.5A5.5 5.5 0 0 1 6.5 3a5.5 5.5 0 1 0 6.5 6.5z";
  var root = document.documentElement;
  root.setAttribute("data-okf-js", "");

  function stored() {
    try {
      var value = window.localStorage.getItem(KEY);
      return value === "light" || value === "dark" ? value : null;
    } catch (e) {
      return null;
    }
  }

  function store(value) {
    try { window.localStorage.setItem(KEY, value); } catch (e) { /* not persisted */ }
  }

  var initial = stored();
  if (initial) { root.setAttribute("data-theme", initial); }

  function effective() {
    var forced = root.getAttribute("data-theme");
    if (forced === "light" || forced === "dark") { return forced; }
    var query = typeof window.matchMedia === "function" ? window.matchMedia("(prefers-color-scheme: dark)") : null;
    return query && query.matches ? "dark" : "light";
  }

  function moonIcon() {
    var svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("class", "okf-theme-icon");
    svg.setAttribute("width", "16");
    svg.setAttribute("height", "16");
    svg.setAttribute("viewBox", "0 0 16 16");
    svg.setAttribute("aria-hidden", "true");
    svg.setAttribute("focusable", "false");
    var path = document.createElementNS(SVG_NS, "path");
    path.setAttribute("d", MOON);
    path.setAttribute("stroke-width", "1.5");
    svg.appendChild(path);
    return svg;
  }

  function addToggle() {
    var tools = document.getElementById("okf-tools");
    if (!tools) { return; }
    var button = document.createElement("button");
    button.type = "button";
    button.id = "okf-theme-toggle";
    button.className = "okf-tool okf-theme-toggle";
    // An icon button: its name stays P1's "Dark theme", its state is aria-pressed.
    button.setAttribute("aria-label", "Dark theme");
    button.appendChild(moonIcon());
    function sync() {
      button.setAttribute("aria-pressed", effective() === "dark" ? "true" : "false");
    }
    button.addEventListener("click", function () {
      var next = effective() === "dark" ? "light" : "dark";
      root.setAttribute("data-theme", next);
      store(next);
      sync();
    });
    // While no theme is forced, the pressed state follows the system
    // preference: re-announce it when that preference changes.
    if (typeof window.matchMedia === "function") {
      var query = window.matchMedia("(prefers-color-scheme: dark)");
      if (query && typeof query.addEventListener === "function") {
        query.addEventListener("change", sync);
      } else if (query && typeof query.addListener === "function") {
        query.addListener(sync);
      }
    }
    sync();
    tools.appendChild(button);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", addToggle);
  } else {
    addToggle();
  }
})();
```

- [ ] **Step 4: Style the button**

In `viewer.css`, directly under `/* --- Task 7: theme button (H11) --- */`, insert:

```css
/* H11: a 34 x 34 square with the 16 px moon, stroked in ink; pressed (dark
   forced or followed): border and stroke blue. */
#okf-tools #okf-theme-toggle {
  display: inline-flex; align-items: center; justify-content: center; flex: none;
  width: 34px; height: 34px; min-height: 0; padding: 0;
  border: 1px solid var(--hair); background: var(--white); cursor: pointer;
}
#okf-tools #okf-theme-toggle .okf-theme-icon { display: block; fill: none; stroke: var(--ink); }
#okf-tools #okf-theme-toggle[aria-pressed="true"] { border-color: var(--blue); }
#okf-tools #okf-theme-toggle[aria-pressed="true"] .okf-theme-icon { stroke: var(--blue); }
```

- [ ] **Step 5: Run the harness**

```bash
cd tools/viewer-security-check && npm test
```

Expected: both Task 7 cases `ok`; P1's theme cases (`a stored choice is applied…`, `color-scheme follows the theme…`, `the announced state follows a system preference change…`) still `ok`; `N passed, 0 failed`.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-theme.js src/OKF4net.Viewer/Assets/viewer.css tools/viewer-security-check/run.js
git commit -m "feat(viewer): theme toggle drawn as the mockups' moon, still named Dark theme; data-okf-js mark

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Contents — current section (X4) and the mockup's list style (X3)

Wave 2. `okf-toc.js` marks the current section with `aria-current="location"`: the last listed H2 or H3 whose top is above a quarter of the window, else the first; recomputed at most once per frame on scroll and resize, and right after a fragment is resolved.

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/okf-toc.js` (whole file)
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` — the four P1 rules from `#okf-context :is(.okf-toc, .okf-backlinks) ul {` to `#okf-context .okf-toc a { min-width: 0; overflow-wrap: anywhere; }`, nothing else
- Modify: `tools/viewer-security-check/run.js` (under `// --- Task 8: contents, current section ---` only)

**Interfaces:**
- Consumes: P1's `okf-toc.js` behaviour (generated ids, contents list, fragments).
- Produces: `#okf-toc a[aria-current="location"]` on exactly one entry when the list is not empty; the threshold is `window.innerHeight * 0.25` (strictly above).

- [ ] **Step 1: Write the failing harness case**

Under `// --- Task 8: contents, current section ---`, insert:

```js
// jsdom does no layout: this probe gives every h2/h3 of #okf-body the top the
// case sets (by heading text), counts the reads, and lets the case move them.
checkAsync("contents: the current section is the last h2/h3 above a quarter of the window, once per frame and after a fragment", async () => {
  const tops = new Map();
  let reads = 0;
  const window = await openPage("foo/bar.html", {
    beforeParse(w) {
      const real = w.Element.prototype.getBoundingClientRect;
      w.Element.prototype.getBoundingClientRect = function () {
        if (/^H[23]$/.test(this.tagName) && this.closest("#okf-body")) {
          reads++;
          const top = tops.has(this.textContent) ? tops.get(this.textContent) : 1000;
          return { top, bottom: top + 20, left: 0, right: 0, width: 0, height: 20, x: 0, y: top };
        }
        return real.call(this);
      };
    },
  });
  const doc = window.document;
  const current = () => Array.from(doc.querySelectorAll('#okf-toc a[aria-current="location"]'), (a) => a.getAttribute("href"));
  assert(JSON.stringify(current()) === JSON.stringify(["#okf-h-usage"]), `on load, with every heading below the line: ${JSON.stringify(current())} (expected the first)`);
  const line = window.innerHeight * 0.25;
  tops.set("Usage", -400);
  tops.set("Section", line - 1);
  tops.set("Details", line + 1);
  reads = 0;
  for (let k = 0; k < 5; k++) { window.dispatchEvent(new window.Event("scroll")); }
  await new Promise((resolve) => window.requestAnimationFrame(resolve));
  assert(JSON.stringify(current()) === JSON.stringify(["#okf-h-section"]), `after scrolling: ${JSON.stringify(current())}`);
  assert(reads === 3, `five scroll events in one frame read the headings ${reads} times, expected 3 (one update)`);
  // A fragment moves no scroll event in jsdom: the update must follow the
  // fragment resolution itself.
  tops.set("Usage", -800);
  tops.set("Section", -400);
  tops.set("Details", 0);
  const changed = new Promise((resolve) => window.addEventListener("hashchange", resolve, { once: true }));
  window.location.hash = "okf-h-details";
  await changed;
  assert(JSON.stringify(current()) === JSON.stringify(["#okf-h-details"]), `after the fragment: ${JSON.stringify(current())}`);
  const style = window.getComputedStyle(doc.querySelector('#okf-toc a[aria-current="location"]'));
  assert(style.getPropertyValue("font-weight") === "600", `the current entry's font-weight: ${style.getPropertyValue("font-weight")}`);
});
```

- [ ] **Step 2: Run the harness to verify it fails**

```bash
cd tools/viewer-security-check && npm test
```

Expected: `FAIL  - contents: the current section …` (`on load …: []`).

- [ ] **Step 3: Rewrite `okf-toc.js`**

Replace the whole content of `src/OKF4net.Viewer/Assets/okf-toc.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Heading anchors, the "On this page" list, its current section and fragment
// resolution (spec §5, §11.4 X4). Runs after viewer.js has rendered and
// sanitized #okf-body, and does not need the site index. Ids are GENERATED
// here, after sanitization: always "okf-h-" + a slug of letters, digits and
// hyphens. No id or name from bundle content is ever admitted (the sanitizer
// strips them), so content cannot clobber a global such as window.OKF_INDEX.
(function () {
  "use strict";
  var site = window.OkfSite;
  var body = document.getElementById("okf-body");
  if (!site || !body) { return; }

  var headings = body.querySelectorAll("h1, h2, h3, h4, h5, h6");
  var texts = [];
  for (var i = 0; i < headings.length; i++) { texts.push(headings[i].textContent); }
  var slugs = site.uniqueSlugs(texts);
  for (var j = 0; j < headings.length; j++) {
    headings[j].setAttribute("id", site.HEADING_PREFIX + slugs[j]);
    headings[j].setAttribute("tabindex", "-1");
  }

  // The listed entries, in document order: { link, heading }.
  var entries = [];
  var toc = document.getElementById("okf-toc");
  var list = toc ? toc.querySelector("ul") : null;
  if (list) {
    for (var k = 0; k < headings.length; k++) {
      var level = headings[k].tagName;
      if (level !== "H2" && level !== "H3") { continue; }
      var item = site.element(document, "li", level === "H3" ? "okf-toc-sub" : "");
      var link = site.element(document, "a", "", headings[k].textContent);
      link.setAttribute("href", "#" + headings[k].id);
      item.appendChild(link);
      list.appendChild(item);
      entries.push({ link: link, heading: headings[k] });
    }
    if (entries.length > 0) {
      toc.hidden = false;
      var context = document.getElementById("okf-context");
      if (context) { context.hidden = false; }
    }
  }

  // X4: the current section is the last listed heading whose top is above a
  // quarter of the window, else the first one.
  function updateCurrent() {
    if (entries.length === 0) { return; }
    var line = window.innerHeight * 0.25;
    var current = entries[0];
    for (var e = 0; e < entries.length; e++) {
      if (entries[e].heading.getBoundingClientRect().top < line) { current = entries[e]; }
    }
    for (var f = 0; f < entries.length; f++) {
      if (entries[f] === current) {
        entries[f].link.setAttribute("aria-current", "location");
      } else {
        entries[f].link.removeAttribute("aria-current");
      }
    }
  }

  // At most one update per frame, however many scroll events arrive.
  var frame = typeof window.requestAnimationFrame === "function"
    ? function (callback) { window.requestAnimationFrame(callback); }
    : function (callback) { window.setTimeout(callback, 16); };
  var scheduled = false;
  function scheduleUpdate() {
    if (scheduled) { return; }
    scheduled = true;
    frame(function () {
      scheduled = false;
      updateCurrent();
    });
  }

  // The generated heading a fragment designates, or null. Only elements
  // inside #okf-body qualify.
  function targetOf(hash) {
    var candidates = site.fragmentCandidates(hash);
    for (var c = 0; c < candidates.length; c++) {
      var target = document.getElementById(candidates[c]);
      if (target && body.contains(target)) { return target; }
    }
    return null;
  }

  // Moves reading focus to that heading, then recomputes the current section.
  function go(hash) {
    var target = targetOf(hash);
    if (!target) { return false; }
    if (typeof target.scrollIntoView === "function") { target.scrollIntoView(); }
    target.focus();
    updateCurrent();
    return true;
  }

  // Author links written against the heading text ("#usage") predate the
  // generated ids. A plain click becomes a real fragment navigation to the
  // generated id -- URL, history entry and Back/Forward stay native -- and
  // hashchange then moves the focus. Modified clicks (new tab, new window)
  // are left to the browser: the new page resolves the fragment on load.
  // The contents list gets the same handling: its links already carry the
  // generated id, but a second click on the current one fires no hashchange
  // either, and must still move the focus to the heading.
  function followFragments(container) {
    container.addEventListener("click", function (e) {
      if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) { return; }
      var a = e.target && typeof e.target.closest === "function" ? e.target.closest("a") : null;
      if (!a || !container.contains(a)) { return; }
      var href = a.getAttribute("href");
      if (!href || href.charAt(0) !== "#") { return; }
      var target = targetOf(href);
      if (!target) { return; }
      e.preventDefault();
      // Compare the resolved elements, not strings: location.hash is
      // percent-encoded ("#okf-h-caf%C3%A9") while the id is not ("okf-h-café"),
      // and re-assigning the same fragment fires no hashchange.
      if (targetOf(window.location.hash) === target) {
        go(window.location.hash);
      } else {
        window.location.hash = target.id;
      }
    });
  }
  followFragments(body);
  if (toc) { followFragments(toc); }
  window.addEventListener("hashchange", function () { go(window.location.hash); });
  window.addEventListener("scroll", scheduleUpdate, { passive: true });
  window.addEventListener("resize", scheduleUpdate);
  updateCurrent();
  if (window.location.hash) { go(window.location.hash); }
})();
```

- [ ] **Step 4: Style the list as the mockup draws it**

In `viewer.css`, replace

```css
#okf-context :is(.okf-toc, .okf-backlinks) ul { list-style: none; margin: 0 0 24px; padding: 0; font-size: 14px; }
#okf-context .okf-toc li { padding: 4px 0 4px 10px; border-left: 2px solid var(--hair); }
#okf-context .okf-toc li.okf-toc-sub { padding-left: 22px; }
#okf-context .okf-toc a { min-width: 0; overflow-wrap: anywhere; }
```

with

```css
/* Sections are spaced by #okf-context's gap (X1). */
#okf-context :is(.okf-toc, .okf-backlinks) ul { list-style: none; margin: 0; padding: 0; font-size: 13.5px; }
/* X3: each entry is a link on a 2 px rule, an h3 indented; X4 marks the
   current section blue and semibold. */
#okf-context .okf-toc a {
  display: block; padding: 5px 0 5px 10px; border-left: 2px solid var(--hair);
  color: var(--ink); text-decoration: none; min-width: 0; overflow-wrap: anywhere;
}
#okf-context .okf-toc li.okf-toc-sub a { padding-left: 22px; }
#okf-context .okf-toc a:hover { color: var(--blue); }
#okf-context .okf-toc a[aria-current="location"] { border-left-color: var(--blue); color: var(--blue); font-weight: 600; }
```

- [ ] **Step 5: Run the harness**

```bash
cd tools/viewer-security-check && npm test
```

Expected: the Task 8 case `ok`; P1's contents and fragment cases (`headings get generated ids only…`, `an author fragment resolves…`, `clicking … a second time…`, `an h3 is listed in the contents as a sub-entry`, `long unbroken titles and ids may wrap…` for `#okf-toc a`) still `ok`; `N passed, 0 failed`.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-toc.js src/OKF4net.Viewer/Assets/viewer.css tools/viewer-security-check/run.js
git commit -m "feat(viewer): the contents mark the current section, once per frame and after a fragment

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 9: Tooled recette — driver, shared library, and the `fonts` control

Wave 1, chain F (second). §12.8: a recette tracked in the repo and run by hand, never by `npm test` or CI, resolving Playwright at run time. This task delivers the driver, its library and `p1-1.js` with the `fonts` control (§11.0's procedure), which Task 10 runs first. Task 18 adds every other control and the P1 port.

**Files:**
- Create: `tools/viewer-security-check/recette/recette.js`
- Create: `tools/viewer-security-check/recette/lib.js`
- Create: `tools/viewer-security-check/recette/p1-1.js`
- Modify: `tools/viewer-security-check/README.md` (section "Recette")

**Interfaces:**
- Consumes: nothing in the repo (Playwright through `OKF_PLAYWRIGHT` or `require("playwright-core")`).
- Produces:
  - CLI: `node tools/viewer-security-check/recette/recette.js --site <okf4net-site> --acme <acme-site> [--out <dir>] [--browsers chrome,edge,firefox,webkit] [--slices p1,p1.1,p2,p3] [--only id,id]`; default browsers `chrome,edge,firefox`, slices `p1,p1.1`, out `<tmp>/okf-recette-<timestamp>`; writes `<out>/results.json` and `<out>/shots/<browser>/<slice>/<id>.png`; exit 0 when no check failed, 1 otherwise, 2 on a crash. A slice file that does not exist yet (`p2.js`, `p3.js`) is reported as skipped.
  - A slice module exports `async function run(ctx)` returning `{ [id]: { pass: true | false | null, … } }`, ids from §11 (`H1`…`L6`), `RC1`…`RC11`, or a named control (`fonts`, `tokens`, `requests`). `pass: null` means "not applicable here" (with a `note` saying why, for instance P2's checks that need P3's page, or a check done by hand): the driver prints it `n/a`, counts it apart, and never counts it as a failure.
  - `ctx` (built by `lib.context`): `browserName`, `site` / `acme` (file URLs ending `/`), `siteDir` / `acmeDir`, `wanted(id)`, `newPage({ viewport, colorScheme })` (a Playwright page whose `okfTracked` holds `errors`, `outside` and `failed` requests), `shot(page, id)`, `close()`, `lib`.
  - `lib`: `loadPlaywright()`, `launch(pw, name)`, `siteUrl(dir)`, `readIndex(dir)`, `pagesByDepth(dir)`, `parseColor(css)`, `rgb(hex)`, `contrast(a, b)`, `context(...)`, `guard(fn)`.

- [ ] **Step 1: Write the shared library**

Create `tools/viewer-security-check/recette/lib.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Shared helpers of the tooled recette (spec §12.8): Playwright resolution,
// browsers, file:// URLs, pages by depth, colours and contrast, request and
// error tracking, captures. Playwright is NOT a dependency of this
// repository: it is resolved at run time (OKF_PLAYWRIGHT, else
// require("playwright-core")), and neither npm test nor CI ever runs this.
"use strict";
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const { pathToFileURL } = require("url");

function loadPlaywright() {
  const where = process.env.OKF_PLAYWRIGHT || "playwright-core";
  try {
    return require(where);
  } catch (e) {
    throw new Error(`Playwright not found (${where}). The recette needs playwright-core, which is deliberately not a dependency of this repository: set OKF_PLAYWRIGHT to the path of a playwright-core module (for instance the one an "npx playwright" run left in the npm cache), or install it outside the repository.`);
  }
}

const BROWSERS = {
  chrome: { type: "chromium", launch: { channel: "chrome" } },
  edge: { type: "chromium", launch: { channel: "msedge" } },
  firefox: { type: "firefox", launch: {} },
  webkit: { type: "webkit", launch: {} },
};

async function launch(pw, name) {
  const b = BROWSERS[name];
  if (!b) { throw new Error(`unknown browser ${name} (known: ${Object.keys(BROWSERS).join(", ")})`); }
  return pw[b.type].launch(b.launch);
}

function siteUrl(dir) {
  const url = pathToFileURL(path.resolve(dir)).href;
  return url.endsWith("/") ? url : url + "/";
}

// The OKF_INDEX a generated site's okf-index.js defines, executed in a sandbox.
function readIndex(dir) {
  const sandbox = { window: {} };
  vm.runInNewContext(fs.readFileSync(path.join(dir, "assets", "okf-index.js"), "utf8"), sandbox);
  return sandbox.window.OKF_INDEX;
}

// Spec §11.0 (A26): index.html for depth 0, then, for each depth present, the
// first page of that depth in index order. Depth = number of "/" in the path.
function pagesByDepth(dir) {
  const out = [{ rel: "index.html", depth: 0 }];
  const seen = new Set([0]);
  for (const concept of readIndex(dir).concepts) {
    const depth = concept.path.split("/").length - 1;
    if (!seen.has(depth)) {
      seen.add(depth);
      out.push({ rel: concept.path, depth });
    }
  }
  return out.sort((a, b) => a.depth - b.depth);
}

function parseColor(css) {
  const m = String(css).match(/rgba?\(([^)]+)\)/);
  if (!m) { return null; }
  const p = m[1].split(/[\s,/]+/).filter(Boolean).map(Number);
  return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
}

function hex(h) {
  const n = parseInt(h.slice(1), 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255, a: 1 };
}

// The computed-style form of a #rrggbb token: "rgb(r, g, b)".
function rgb(h) {
  const c = hex(h);
  return `rgb(${c.r}, ${c.g}, ${c.b})`;
}

function luminance(c) {
  const f = (v) => {
    const s = v / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
}

// WCAG contrast ratio of two colours given as "#rrggbb" or "rgb(…)".
function contrast(a, b) {
  const ca = String(a).startsWith("#") ? hex(a) : parseColor(a);
  const cb = String(b).startsWith("#") ? hex(b) : parseColor(b);
  const [x, y] = [luminance(ca), luminance(cb)].sort((p, q) => q - p);
  return Math.round(((x + 0.05) / (y + 0.05)) * 100) / 100;
}

function track(page, roots) {
  const tracked = { errors: [], outside: [], failed: [] };
  page.on("pageerror", (e) => tracked.errors.push(String(e)));
  page.on("console", (m) => { if (m.type() === "error") { tracked.errors.push("console: " + m.text()); } });
  page.on("request", (r) => {
    const url = r.url();
    if (!url.startsWith("data:") && !url.startsWith("about:") && !roots.some((root) => url.startsWith(root))) { tracked.outside.push(url); }
  });
  page.on("requestfailed", (r) => tracked.failed.push(r.url()));
  return tracked;
}

function context({ browser, name, opts, slice }) {
  const site = siteUrl(opts.site);
  const acme = siteUrl(opts.acme);
  const opened = [];
  const shots = path.join(opts.out, "shots", name, slice);
  fs.mkdirSync(shots, { recursive: true });
  return {
    browserName: name,
    site,
    acme,
    siteDir: path.resolve(opts.site),
    acmeDir: path.resolve(opts.acme),
    lib: module.exports,
    wanted: (id) => !opts.only || opts.only.has(id),
    async newPage(o = {}) {
      const ctx = await browser.newContext({ viewport: o.viewport || { width: 1440, height: 900 }, colorScheme: o.colorScheme || "light" });
      const page = await ctx.newPage();
      page.okfTracked = track(page, [site, acme]);
      opened.push(ctx);
      return page;
    },
    async shot(page, id) {
      await page.screenshot({ path: path.join(shots, `${id}.png`) });
    },
    async close() {
      for (const ctx of opened) { await ctx.close(); }
    },
  };
}

// Runs one control, turning a crash into a failed result.
async function guard(fn) {
  try {
    return await fn();
  } catch (e) {
    return { pass: false, error: String(e && e.stack ? e.stack : e).split("\n").slice(0, 2).join(" | ") };
  }
}

module.exports = { loadPlaywright, launch, siteUrl, readIndex, pagesByDepth, parseColor, rgb, contrast, context, guard, BROWSERS };
```

- [ ] **Step 2: Write the driver**

Create `tools/viewer-security-check/recette/recette.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Tooled recette of the interactive viewer (spec §12.8), run by hand, never
// by npm test or CI. Opens the generated sites in file://, as a reader who
// double-clicks a file, in real browsers, runs each slice's checks and
// writes results.json plus captures named by §11 id.
//
//   node tools/viewer-security-check/recette/recette.js --site <okf4net-site> --acme <acme-site>
//        [--out <dir>] [--browsers chrome,edge,firefox,webkit] [--slices p1,p1.1,p2,p3] [--only id,id]
//
// The two sites are built by the commands of ../ACCEPTANCE.md. A slice whose
// file does not exist yet is reported as skipped.
"use strict";
const fs = require("fs");
const os = require("os");
const path = require("path");
const lib = require("./lib");

const SLICE_FILES = { p1: "p1.js", "p1.1": "p1-1.js", p2: "p2.js", p3: "p3.js" };

function parseArgs(argv) {
  const out = { browsers: ["chrome", "edge", "firefox"], slices: ["p1", "p1.1"], only: null };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    const value = argv[i + 1];
    if (arg === "--site") { out.site = value; i++; }
    else if (arg === "--acme") { out.acme = value; i++; }
    else if (arg === "--out") { out.out = value; i++; }
    else if (arg === "--browsers") { out.browsers = value.split(","); i++; }
    else if (arg === "--slices") { out.slices = value.split(","); i++; }
    else if (arg === "--only") { out.only = new Set(value.split(",")); i++; }
    else { throw new Error(`unknown argument ${arg}`); }
  }
  if (!out.site || !out.acme) {
    throw new Error("usage: recette.js --site <okf4net-site> --acme <acme-site> [--out <dir>] [--browsers chrome,edge,firefox,webkit] [--slices p1,p1.1,p2,p3] [--only id,id]");
  }
  out.out = path.resolve(out.out || path.join(os.tmpdir(), `okf-recette-${new Date().toISOString().replace(/[:.]/g, "-")}`));
  return out;
}

(async () => {
  const opts = parseArgs(process.argv.slice(2));
  const pw = lib.loadPlaywright();
  fs.mkdirSync(opts.out, { recursive: true });
  const report = { date: new Date().toISOString(), site: path.resolve(opts.site), acme: path.resolve(opts.acme), browsers: [] };
  let failed = 0;
  // pass: null = not applicable (a check that needs a later slice, or one
  // done by hand): printed "n/a" with its note, never counted as a failure.
  let notApplicable = 0;
  for (const name of opts.browsers) {
    const entry = { browser: name, slices: {} };
    let browser;
    try {
      browser = await lib.launch(pw, name);
      entry.version = browser.version();
    } catch (e) {
      entry.fatal = String(e).split("\n")[0];
      failed++;
      report.browsers.push(entry);
      console.log(`${name}: cannot launch (${entry.fatal})`);
      continue;
    }
    for (const slice of opts.slices) {
      const file = path.join(__dirname, SLICE_FILES[slice] || `${slice}.js`);
      if (!fs.existsSync(file)) {
        entry.slices[slice] = { skipped: `${path.basename(file)} does not exist yet` };
        console.log(`${name} ${slice}: skipped (${path.basename(file)} does not exist yet)`);
        continue;
      }
      const ctx = lib.context({ browser, name, opts, slice });
      const results = await lib.guard(() => require(file).run(ctx)).then((r) => (r && r.pass === false && r.error ? { crash: r } : r));
      await ctx.close();
      entry.slices[slice] = results;
      for (const [id, r] of Object.entries(results)) {
        const verdict = r.pass === true ? "ok" : r.pass === null ? "n/a" : "FAIL";
        if (verdict === "FAIL") { failed++; }
        if (verdict === "n/a") { notApplicable++; }
        const detail = r.error ? " -- " + r.error : verdict === "n/a" && r.note ? " -- " + r.note : "";
        console.log(`${name} ${entry.version} ${slice} ${id}: ${verdict}${detail}`);
      }
    }
    await browser.close();
    report.browsers.push(entry);
  }
  const file = path.join(opts.out, "results.json");
  report.notApplicable = notApplicable;
  fs.writeFileSync(file, JSON.stringify(report, null, 2));
  console.log(`\n${failed === 0 ? "no check failed" : `${failed} check(s) failed`}, ${notApplicable} not applicable; report: ${file}; captures: ${path.join(opts.out, "shots")}`);
  process.exit(failed === 0 ? 0 : 1);
})().catch((e) => {
  console.error(e && e.message ? e.message : e);
  process.exit(2);
});
```

- [ ] **Step 3: Write `p1-1.js` with the `fonts` control**

Create `tools/viewer-security-check/recette/p1-1.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// P1.1 recette (spec §11, §12.8): results keyed by §11 id. This first
// version carries the `fonts` control (§11.0, A26); Task 18 of the P1.1 plan
// adds the other controls.
"use strict";

// The seven faces of spec §11.0, A16.
const FACES = [["Inter", 400], ["Inter", 500], ["Inter", 600], ["Inter Tight", 600], ["Inter Tight", 900], ["Space Mono", 400], ["Space Mono", 700]];

// §11.0: on index.html and on the first page of every depth of both sites,
// document.fonts.load must return a non-empty list whose every face is
// "loaded" (not document.fonts.check, which is true when no face matches),
// with no failed request under assets/fonts/.
async function fonts(ctx) {
  const results = [];
  for (const [label, dir, url] of [["okf4net", ctx.siteDir, ctx.site], ["acme", ctx.acmeDir, ctx.acme]]) {
    for (const { rel, depth } of ctx.lib.pagesByDepth(dir)) {
      const page = await ctx.newPage();
      await page.goto(url + rel, { waitUntil: "load" });
      const faces = await page.evaluate(async (wanted) => {
        const out = [];
        for (const [family, weight] of wanted) {
          try {
            const list = await document.fonts.load(`${weight} 16px "${family}"`);
            out.push({ family, weight, faces: list.length, loaded: list.length > 0 && list.every((f) => f.status === "loaded") });
          } catch (e) {
            out.push({ family, weight, faces: 0, loaded: false, error: String(e) });
          }
        }
        return out;
      }, FACES);
      const failed = page.okfTracked.failed.filter((u) => /\/assets\/fonts\//.test(u));
      results.push({ site: label, page: rel, depth, ok: failed.length === 0 && faces.every((f) => f.loaded), failed, faces: faces.filter((f) => !f.loaded) });
      await page.close();
    }
  }
  return { pass: results.every((r) => r.ok), pages: results.length, failures: results.filter((r) => !r.ok) };
}

async function run(ctx) {
  const out = {};
  if (ctx.wanted("fonts")) { out.fonts = await ctx.lib.guard(() => fonts(ctx)); }
  return out;
}

module.exports = { run };
```

- [ ] **Step 4: Document the recette**

Append to `tools/viewer-security-check/README.md`:

```markdown

## Recette (manual, outside CI)

`recette/` holds the tooled recette of the interactive viewer (spec §12.8):
what jsdom cannot see — layout, fonts really loaded, colours and contrasts in
both themes, widths at 1 100, 1 190 and 1 440 px, `file://` behaviour,
requests leaving the site — checked in real browsers on the two sites of
`ACCEPTANCE.md`, opened as files:

    OKF_PLAYWRIGHT=<path to a playwright-core module> \
      node tools/viewer-security-check/recette/recette.js \
        --site <okf4net-site> --acme <acme-site> \
        [--out <dir>] [--browsers chrome,edge,firefox,webkit] [--slices p1,p1.1,p2,p3] [--only id,id]

Playwright is **not** a dependency of this repository: `recette/lib.js`
resolves `OKF_PLAYWRIGHT`, else `require("playwright-core")`, and stops with
a clear message when neither exists. `npm test` and CI never run the
recette. Each slice has its own file (`p1.js`, `p1-1.js`, `p2.js`, `p3.js`)
exporting `async function run(ctx)`, whose results are keyed by the ids of
spec §11 (`H1`…`L6`), by the ported P1 checks `RC1`…`RC11`, or by a named
control (`fonts`, `tokens`, `requests`). A result is `pass: true`, `false`,
or `null` for "not applicable here" (with a `note`: a check that needs a
later slice, or one done by hand), which is printed `n/a` and never counted
as a failure; the exit code is 1 only when a check failed. `run(ctx)` gets
its pages from `ctx.newPage()` and writes its captures with
`ctx.shot(page, id)`; `ctx.lib` is `recette/lib.js` (contrast, colours,
pages by depth). Results and captures (1 440 × 900,
named by id) go to `--out`, by default a folder of the system's temporary
directory; a slice's report goes into the pull request, never into the
repository.
```

- [ ] **Step 5: Build two sites and prove the `fonts` control fails without fonts**

```bash
cd E:/Sources/okf/.claude/worktrees/viewer-interactive-spec   # or the chain F worktree
R="$TEMP/okf-p11-recette"; rm -rf "$R"; mkdir -p "$R"
dotnet run --project producers/src/OkfProducer.Cli -c Release -- generate --repo . --out "$R/okf4net-bundle" --no-msbuild
dotnet run --project src/OKF4net.Render -c Release -- "$R/okf4net-bundle" --out "$R/okf4net-site"
dotnet run --project src/OKF4net.Render -c Release -- bundles/acme_retail --out "$R/acme-site"
export OKF_PLAYWRIGHT="C:/Users/Julien/AppData/Local/npm-cache/_npx/6bcb61ec6d5aea22/node_modules/playwright-core"   # any playwright-core module
node tools/viewer-security-check/recette/recette.js --site "$R/okf4net-site" --acme "$R/acme-site" --browsers chrome --slices p1.1,p2 --only fonts --out "$R/out-nofonts"
```

Expected: `chrome … p1.1 fonts: FAIL` (no face is declared yet, so `document.fonts.load` returns an empty list), `chrome p2: skipped (p2.js does not exist yet)`, exit code 1, and `$R/out-nofonts/results.json` listing every page with `faces` of 0. Then run once with `OKF_PLAYWRIGHT=/nowhere`: expected exit code 2 and the message `Playwright not found (/nowhere). The recette needs playwright-core, …`.

Then prove that `pass: null` is "n/a", not a failure (not committed). Create `tools/viewer-security-check/recette/p3.js` containing:

```js
"use strict";
module.exports = { run: async () => ({ probe: { pass: null, note: "deliberately not applicable" } }) };
```

and run `node tools/viewer-security-check/recette/recette.js --site "$R/okf4net-site" --acme "$R/acme-site" --browsers chrome --slices p3 --out "$R/out-na"; echo $?`. Expected: the line `chrome … p3 probe: n/a -- deliberately not applicable`, the summary `no check failed, 1 not applicable; …`, and `0`. Change `pass: null` to `pass: false` and run again: `p3 probe: FAIL`, `1 check(s) failed, 0 not applicable; …`, and `1`. Delete `recette/p3.js` (it belongs to P3).

- [ ] **Step 6: Check the harness is untouched**

```bash
cd tools/viewer-security-check && npm test
```

Expected: `N passed, 0 failed`, no line from `recette/`.

- [ ] **Step 7: Commit**

```bash
git add tools/viewer-security-check/recette/recette.js tools/viewer-security-check/recette/lib.js tools/viewer-security-check/recette/p1-1.js tools/viewer-security-check/README.md
git commit -m "test(viewer): tooled recette driver (Playwright at run time, never a dependency) with the file:// fonts control

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Embedded fonts (A16) and their `file://` verdict (A26)

Wave 1, chain F (third). Vendors Inter (400, 500, 600), Inter Tight (600, 900) and Space Mono (400, 700), latin woff2 exactly as Google Fonts serves them, with their OFL texts and a provenance README; writes the relative `@font-face` block at the top of `viewer.css`; records sizes and the AOT growth; then runs the `fonts` control in Chrome, Edge and Firefox at every page depth of both sites and records the verdict that decides Task 16. Every later CSS task waits for this one (spec §11.0).

**Files:**
- Create: `tools/viewer-fonts/vendor-fonts.js`
- Create (generated by that script, committed): `src/OKF4net.Viewer/Assets/fonts/*.woff2`, `OFL-Inter.txt`, `OFL-InterTight.txt`, `OFL-SpaceMono.txt`, `README.md`
- Modify (by that script): `src/OKF4net.Viewer/Assets/viewer.css`, between the two `@font-face` markers only
- Test: `tests/OKF4net.Tests/Viewer/ViewerFontsTests.cs` (new)
- Modify: `tools/viewer-security-check/run.js` (under `// --- Task 10: fonts ---` only)
- Modify: `NOTICE`, `CLAUDE.md` (the first paragraph's sentence on third-party code), `src/OKF4net.Viewer/README.md` (Licensing)
- Modify: `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md` (§11.0, one paragraph)

**Interfaces:**
- Consumes: Task 2 (every file under `Assets/` embedded and written; `fonts/README.md` excluded), Task 9 (recette `fonts`), Task 1's markers.
- Produces: `assets/fonts/<family-slug>[-<weight>]-latin.woff2` (one file per distinct Google URL: a family Google serves as one variable file gets one file), `assets/fonts/OFL-*.txt`; seven `@font-face` rules (`font-family` in double quotes, `font-style: normal`, `font-weight`, `font-display: swap`, `src: url("fonts/<file>") format("woff2")`, `unicode-range` as Google serves it) in the order Inter 400, 500, 600, Inter Tight 600, 900, Space Mono 400, 700; the verdict line in spec §11.0.

- [ ] **Step 1: Record the AOT size before the fonts**

```bash
dotnet publish src/OKF4net.Render -c Release -o "$TEMP/okf-render-before"
ls -l "$TEMP/okf-render-before" | grep -E "okf-render(\.exe)?$"
```

Expected: one line with the size in bytes of the native `okf-render` binary. Keep the number for Step 11.

- [ ] **Step 2: Write the failing xunit tests**

Create `tests/OKF4net.Tests/Viewer/ViewerFontsTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>The embedded fonts and their <c>@font-face</c> rules (spec §11.0, A16).</summary>
public class ViewerFontsTests
{
    private static readonly (string Family, int Weight)[] Faces =
        [("Inter", 400), ("Inter", 500), ("Inter", 600), ("Inter Tight", 600), ("Inter Tight", 900), ("Space Mono", 400), ("Space Mono", 700)];

    private sealed record FontFace(string Family, int Weight, string Url, string Rule);

    private static List<FontFace> DeclaredFaces(string css)
    {
        var faces = new List<FontFace>();
        foreach (Match m in Regex.Matches(css, @"@font-face\s*\{([^}]*)\}"))
        {
            var rule = m.Groups[1].Value;
            faces.Add(new FontFace(
                Regex.Match(rule, "font-family:\\s*\"([^\"]+)\"").Groups[1].Value,
                int.Parse(Regex.Match(rule, @"font-weight:\s*(\d+)").Groups[1].Value, CultureInfo.InvariantCulture),
                Regex.Match(rule, "url\\(\"([^\"]+)\"\\)").Groups[1].Value,
                rule));
        }

        return faces;
    }

    [Fact]
    public void The_stylesheet_declares_exactly_the_seven_faces_in_order_before_anything_else()
    {
        var css = ViewerAssets.Css;

        Assert.Equal(Faces, DeclaredFaces(css).Select(f => (f.Family, f.Weight)));
        // "@font-face {" (with its brace), not "@font-face": the marker comment
        // above the block also holds "@font-face" and would make this vacuous.
        // The LAST rule must come before the :root tokens, so every face does.
        var lastFace = css.LastIndexOf("@font-face {", StringComparison.Ordinal);
        Assert.True(
            lastFace >= 0 && lastFace < css.IndexOf(":root {", StringComparison.Ordinal),
            "the @font-face rules must open the stylesheet, every one before the :root tokens (spec §12.6)");
    }

    [Fact]
    public void Every_face_loads_an_embedded_woff2_relative_to_the_stylesheet()
    {
        foreach (var face in DeclaredFaces(ViewerAssets.Css))
        {
            // Relative to assets/viewer.css, so valid at every page depth.
            Assert.Matches(@"^fonts/[a-z0-9-]+\.woff2$", face.Url);
            Assert.Contains(face.Url, ViewerAssets.Paths);
            Assert.Contains("format(\"woff2\")", face.Rule, StringComparison.Ordinal);
            Assert.Contains("font-display: swap", face.Rule, StringComparison.Ordinal);
            Assert.Matches(@"unicode-range:\s*U\+", face.Rule);
        }
    }

    [Fact]
    public void Every_woff2_is_one_and_together_they_stay_within_the_budget()
    {
        var fonts = ViewerAssets.Paths
            .Where(p => p.StartsWith("fonts/", StringComparison.Ordinal) && p.EndsWith(".woff2", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(fonts);

        long total = 0;
        foreach (var path in fonts)
        {
            var bytes = ViewerAssets.Bytes(path);
            Assert.Equal("wOF2", Encoding.ASCII.GetString(bytes, 0, 4));
            total += bytes.Length;
        }

        Assert.True(total <= 300_000, $"the woff2 files weigh {total} bytes, over the 300 000 of spec §11.0: back to the owner");
    }

    [Theory]
    [InlineData("fonts/OFL-Inter.txt")]
    [InlineData("fonts/OFL-InterTight.txt")]
    [InlineData("fonts/OFL-SpaceMono.txt")]
    public void Each_family_ships_its_SIL_OFL_text(string path)
        => Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", ViewerAssets.Text(path), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void The_written_site_holds_the_fonts_and_the_licences_but_not_the_provenance_readme()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a.md", "---\ntype: Note\ntitle: A\n---\n");

        HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);

        foreach (var face in DeclaredFaces(ViewerAssets.Css))
        {
            Assert.True(File.Exists(Path.Combine(dest.Path, "assets", face.Url.Replace('/', Path.DirectorySeparatorChar))), face.Url);
        }

        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "fonts", "OFL-Inter.txt")));
        Assert.False(File.Exists(Path.Combine(dest.Path, "assets", "fonts", "README.md")));
    }
}
```

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ViewerFontsTests"`
Expected: FAIL — `The_stylesheet_declares_exactly_the_seven_faces…` (no face declared), `Every_woff2_is_one…` (`Assert.NotEmpty`), `Each_family_ships_its_SIL_OFL_text` (`embedded asset not found: fonts/OFL-Inter.txt`).

- [ ] **Step 3: Write the vendoring script**

Create `tools/viewer-fonts/vendor-fonts.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Vendors the viewer's three font families (spec §11.0, A16): Inter (400,
// 500, 600), Inter Tight (600, 900) and Space Mono (400, 700), latin subset,
// woff2, exactly as Google Fonts distributes them -- never re-subset nor
// modified, so no SIL OFL "Modified Version" or Reserved Font Name question
// arises -- with their SIL OFL 1.1 texts. Writes
// src/OKF4net.Viewer/Assets/fonts/ (the fonts, the licences and README.md,
// the provenance table) and the @font-face block of viewer.css, between its
// two markers. Run by hand (Node 22, network) when the fonts are vendored or
// re-vendored; no build and no test runs it:
//   node tools/viewer-fonts/vendor-fonts.js
"use strict";
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");

const ROOT = path.join(__dirname, "..", "..");
const FONTS = path.join(ROOT, "src", "OKF4net.Viewer", "Assets", "fonts");
const CSS = path.join(ROOT, "src", "OKF4net.Viewer", "Assets", "viewer.css");
const BEGIN = "/* === @font-face (P1.1 Task 10 replaces the lines between these two markers) === */";
const END = "/* === end @font-face === */";
// A desktop Chrome user agent: Google Fonts answers it with woff2.
const UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
const CSS_URL = "https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&family=Inter+Tight:wght@600;900&family=Space+Mono:wght@400;700&display=swap";
// The faces, in the order viewer.css declares them.
const FACES = [["Inter", 400], ["Inter", 500], ["Inter", 600], ["Inter Tight", 600], ["Inter Tight", 900], ["Space Mono", 400], ["Space Mono", 700]];
const LICENCES = [
  ["Inter", "OFL-Inter.txt", "https://raw.githubusercontent.com/google/fonts/main/ofl/inter/OFL.txt"],
  ["Inter Tight", "OFL-InterTight.txt", "https://raw.githubusercontent.com/google/fonts/main/ofl/intertight/OFL.txt"],
  ["Space Mono", "OFL-SpaceMono.txt", "https://raw.githubusercontent.com/google/fonts/main/ofl/spacemono/OFL.txt"],
];
const BUDGET = 300000;

async function get(url, binary) {
  const response = await fetch(url, { headers: { "User-Agent": UA } });
  if (!response.ok) { throw new Error(`${url}: HTTP ${response.status}`); }
  return binary ? Buffer.from(await response.arrayBuffer()) : response.text();
}

const slug = (family) => family.toLowerCase().replace(/ /g, "-");
const sha256 = (buffer) => crypto.createHash("sha256").update(buffer).digest("hex");

(async () => {
  const css = await get(CSS_URL, false);
  const latin = [];
  for (const m of css.matchAll(/\/\*\s*([a-z-]+)\s*\*\/\s*@font-face\s*\{([^}]*)\}/g)) {
    if (m[1] !== "latin") { continue; }
    const rule = m[2];
    latin.push({
      family: /font-family:\s*'([^']+)'/.exec(rule)[1],
      weight: Number(/font-weight:\s*(\d+)/.exec(rule)[1]),
      url: /src:\s*url\((https:[^)]+\.woff2)\)\s*format\('woff2'\)/.exec(rule)[1],
      range: /unicode-range:\s*([^;]+);/.exec(rule)[1].trim(),
    });
  }
  const faces = FACES.map(([family, weight]) => {
    const face = latin.find((f) => f.family === family && f.weight === weight);
    if (!face) { throw new Error(`Google Fonts served no latin woff2 face for ${family} ${weight}`); }
    return face;
  });

  fs.mkdirSync(FONTS, { recursive: true });
  const files = new Map(); // url -> { name, bytes, sha, version, family, weights }
  for (const face of faces) {
    if (!files.has(face.url)) {
      const urls = new Set(faces.filter((f) => f.family === face.family).map((f) => f.url));
      const name = `${slug(face.family)}${urls.size > 1 ? "-" + face.weight : ""}-latin.woff2`;
      const bytes = await get(face.url, true);
      if (bytes.subarray(0, 4).toString("latin1") !== "wOF2") { throw new Error(`${face.url} is not a woff2 file`); }
      const version = (/\/s\/[^/]+\/(v\d+)\//.exec(face.url) || [null, "unknown"])[1];
      files.set(face.url, { name, bytes, sha: sha256(bytes), version, family: face.family, weights: [] });
      fs.writeFileSync(path.join(FONTS, name), bytes);
    }
    files.get(face.url).weights.push(face.weight);
  }

  const licences = [];
  for (const [family, name, url] of LICENCES) {
    const text = await get(url, false);
    if (!/SIL OPEN FONT LICENSE Version 1\.1/i.test(text)) { throw new Error(`${url} is not the SIL OFL 1.1`); }
    fs.writeFileSync(path.join(FONTS, name), text);
    licences.push({ family, name, url, sha: sha256(Buffer.from(text)), size: Buffer.byteLength(text) });
  }

  const block = [
    BEGIN,
    "/* Inter, Inter Tight and Space Mono (SIL OFL 1.1), latin subset, as Google Fonts serves",
    "   them (fonts/README.md). Relative to this stylesheet, so valid at every page depth. */",
  ];
  for (const face of faces) {
    block.push(`@font-face { font-family: "${face.family}"; font-style: normal; font-weight: ${face.weight}; font-display: swap; src: url("fonts/${files.get(face.url).name}") format("woff2"); unicode-range: ${face.range}; }`);
  }
  block.push(END);
  const source = fs.readFileSync(CSS, "utf8");
  const start = source.indexOf(BEGIN);
  const end = source.indexOf(END);
  if (start === -1 || end < start) { throw new Error("viewer.css lost its @font-face markers"); }
  fs.writeFileSync(CSS, source.slice(0, start) + block.join("\n") + source.slice(end + END.length));

  const date = new Date().toISOString().slice(0, 10);
  const total = [...files.values()].reduce((n, f) => n + f.bytes.length, 0);
  const lines = [
    "# Viewer fonts: provenance",
    "",
    `Vendored on ${date} by \`tools/viewer-fonts/vendor-fonts.js\` from Google Fonts`,
    `(\`${CSS_URL}\`), latin subset, woff2, **unmodified** (spec §11.0, A16).`,
    "Licence: SIL Open Font License 1.1, texts beside the fonts. This README is",
    "not embedded (`OKF4net.Viewer.csproj` excludes it); every other file here is",
    "embedded and written to `assets/fonts/` by `okf-render`.",
    "",
    "| File | Family | Weights | Source | Version | Bytes | sha256 |",
    "| --- | --- | --- | --- | --- | --- | --- |",
    ...[...files.entries()].map(([url, f]) => `| \`${f.name}\` | ${f.family} | ${f.weights.join(", ")} | ${url} | ${f.version} | ${f.bytes.length} | \`${f.sha}\` |`),
    "",
    "| Licence | Family | Source | Bytes | sha256 |",
    "| --- | --- | --- | --- | --- |",
    ...licences.map((l) => `| \`${l.name}\` | ${l.family} | ${l.url} | ${l.size} | \`${l.sha}\` |`),
    "",
    `Total woff2: ${total} bytes (budget ${BUDGET}, spec §11.0).`,
    "",
  ];
  fs.writeFileSync(path.join(FONTS, "README.md"), lines.join("\n"));
  console.log(`${files.size} woff2 files, ${total} bytes (budget ${BUDGET}); ${licences.length} licences; viewer.css @font-face rewritten.`);
  if (total > BUDGET) { console.log("OVER BUDGET: stop and report to the owner (spec §11.0)."); process.exitCode = 1; }
})().catch((e) => {
  console.error(e && e.message ? e.message : e);
  process.exit(1);
});
```

- [ ] **Step 4: Vendor the fonts**

```bash
node tools/viewer-fonts/vendor-fonts.js
git status --short src/OKF4net.Viewer/Assets
```

Expected: one line `N woff2 files, T bytes (budget 300000); 3 licences; viewer.css @font-face rewritten.` with T ≤ 300 000 (exit 0); `git status` shows the new `fonts/` files and `viewer.css` modified. `git diff src/OKF4net.Viewer/Assets/viewer.css` shows only the lines between the two markers changed, seven `@font-face` lines. If the script reports `OVER BUDGET`, stop: the owner decides (spec §11.0).

- [ ] **Step 5: Run the xunit tests**

Run: `dotnet build OKF4net.sln` then `dotnet test OKF4net.sln --filter "FullyQualifiedName~ViewerFontsTests|FullyQualifiedName~HtmlWriterAssetsTests"`
Expected: all pass.

- [ ] **Step 6: Pin the written stylesheet in the harness**

Under `// --- Task 10: fonts ---` in `run.js`, insert:

```js
check("fonts: every @font-face of the written stylesheet points at a file written under assets/fonts", () => {
  const css = fs.readFileSync(path.join(SITE, "assets", "viewer.css"), "utf8");
  const urls = Array.from(css.matchAll(/@font-face\s*\{[^}]*url\("([^"]+)"\)/g), (m) => m[1]);
  assert(urls.length === 7, `${urls.length} @font-face urls, expected the 7 faces of spec §11.0`);
  for (const url of urls) {
    assert(/^fonts\/[a-z0-9-]+\.woff2$/.test(url), `${url} is not a relative fonts/ url`);
    assert(fs.existsSync(path.join(SITE, "assets", url)), `${url} was not written`);
  }
});
```

Run `cd tools/viewer-security-check && npm test`. Expected: `ok  - fonts: every @font-face …`, `N passed, 0 failed`.

- [ ] **Step 7: Credit the fonts**

At the end of `NOTICE`, append:

```

------------------------------------------------------------------------------

The static site generated by `OKF4net.Viewer` (the `okf-render` binary) also
embeds three font families, latin subset, woff2, exactly as Google Fonts
distributes them (not modified, not re-subset):

    Inter, Inter Tight (https://github.com/rsms/inter)
    Space Mono (https://github.com/googlefonts/spacemono)

licensed under the SIL Open Font License, Version 1.1. Each family's licence
text, with its copyright notice, ships beside the fonts as
`src/OKF4net.Viewer/Assets/fonts/OFL-Inter.txt`, `OFL-InterTight.txt` and
`OFL-SpaceMono.txt`, and is written with them into every generated site
(`assets/fonts/`). Provenance (source URL, version, size, sha256) is recorded
in `src/OKF4net.Viewer/Assets/fonts/README.md`. The fonts are embedded in the
OKF4net.Viewer package and the `okf-render` binary; they are not part of any
other OKF4net library, nor of the `okf` binary.
```

In `CLAUDE.md`, in the first paragraph, replace

```
The one exception anywhere in the build is `OKF4net.Viewer`'s vendored copy of marked (MIT), embedded in that library and in the `okf-render` binary it backs — `okf` itself never references it.
```

with

```
The exceptions anywhere in the build are `OKF4net.Viewer`'s vendored copy of marked (MIT) and its three vendored font families — Inter, Inter Tight and Space Mono (SIL OFL 1.1, latin woff2 exactly as Google Fonts serves them, provenance in `src/OKF4net.Viewer/Assets/fonts/README.md`, re-vendored only by `tools/viewer-fonts/vendor-fonts.js`) — embedded in that library and in the `okf-render` binary it backs; `okf` itself never references them.
```

In `src/OKF4net.Viewer/README.md`, replace the Licensing paragraph with:

```markdown
LGPL-3.0-or-later. The generated site embeds a vendored copy of
[marked](https://github.com/markedjs/marked) (MIT) for client-side markdown
rendering, and three font families — Inter, Inter Tight and Space Mono (SIL
Open Font License 1.1), latin woff2 as Google Fonts distributes them — see
`NOTICE` and `Assets/fonts/README.md`.
```

- [ ] **Step 8: Run the `fonts` control (A26) in Chrome, Edge and Firefox**

Rebuild the two sites with this branch's `okf-render`, then run the control:

```bash
R="$TEMP/okf-p11-recette"; rm -rf "$R/okf4net-site" "$R/acme-site"
dotnet run --project producers/src/OkfProducer.Cli -c Release -- generate --repo . --out "$R/okf4net-bundle" --no-msbuild
dotnet run --project src/OKF4net.Render -c Release -- "$R/okf4net-bundle" --out "$R/okf4net-site"
dotnet run --project src/OKF4net.Render -c Release -- bundles/acme_retail --out "$R/acme-site"
node tools/viewer-security-check/recette/recette.js --site "$R/okf4net-site" --acme "$R/acme-site" --browsers chrome,edge,firefox --slices p1.1 --only fonts --out "$R/out-fonts"
```

Expected: three lines `chrome|edge|firefox <version> p1.1 fonts: ok|FAIL`. `results.json` lists, per browser, the pages checked (index.html and the first page of every depth of both sites) and, for any failure, the page, its depth, the faces not loaded and the failed requests.

- [ ] **Step 9: Check the installed Firefox by hand**

Open in the installed Firefox (not Playwright's) `file:///…/okf4net-site/index.html` and the deepest page the control listed for the OKF4net site; in the developer tools, Network tab, filter `woff2`, reload: every font request answers (no "blocked" or "CORS" line), and the computed font of a paragraph is "Inter". Note the Firefox version.

- [ ] **Step 10: Record the verdict**

The verdict is **"loaded everywhere"** when Steps 8 and 9 passed in every browser, else **"blocked in <browsers>"** — and Task 16 then runs in wave 4 (spec A26: one failure is enough).

- [ ] **Step 11: Measure the AOT growth and record everything in the spec**

```bash
dotnet publish src/OKF4net.Render -c Release -o "$TEMP/okf-render-after"
ls -l "$TEMP/okf-render-after" | grep -E "okf-render(\.exe)?$"
```

In `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`, §11.0, after the paragraph that ends `budget porté à ≤ 400 Ko pour \`okf-fonts.css\` ; la vérification est refaite.`, append:

```markdown

Mesure et vérification (P1.1, AAAA-MM-JJ) : woff2 — F fichiers, W octets au
total (budget 300 000), détail dans `src/OKF4net.Viewer/Assets/fonts/README.md` ;
binaire `okf-render` AOT (RID) — A octets avant, B octets après (+D).
Vérification `file://` (recette `--slices p1.1 --only fonts`) : Chrome VC,
Edge VE, Firefox de Playwright VF, Firefox installé VI (page racine et page
la plus profonde, onglet Réseau) ; profondeurs 0 à P du site d'OKF4net et 0 à
Q de celui d'`acme_retail`. Verdict : VERDICT.
```

writing the date, the numbers of Steps 1, 4 and 11 (`F`, `W`, the RID you published for, `A`, `B`, `D`), the browser versions of Steps 8–9, the deepest depths `P` and `Q` from `results.json`, and as `VERDICT` either `chargées partout ; viewer.css garde ses @font-face relatifs` or `bloquées dans <navigateurs> ; repli okf-fonts.css (tâche 16 du plan P1.1)`.

- [ ] **Step 12: Format and commit**

```bash
dotnet format OKF4net.sln --verify-no-changes
git add tools/viewer-fonts/vendor-fonts.js src/OKF4net.Viewer/Assets/fonts src/OKF4net.Viewer/Assets/viewer.css tests/OKF4net.Tests/Viewer/ViewerFontsTests.cs tools/viewer-security-check/run.js NOTICE CLAUDE.md src/OKF4net.Viewer/README.md docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md
git commit -m "feat(viewer): embed Inter, Inter Tight and Space Mono (SIL OFL 1.1) and record their file:// verdict

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 11: Shell and header of the three views (H1–H13, G1), script table, merge markers

Wave 3, alone. One method writes everything before `.okf-layout` for the page, index **and** graph views (§12.3); the script table `PageScripts` with P2's marker; the graph page name in the header and in the collision guard (A25); P3's two markers in `HtmlWriter`.

**Files:**
- Modify: `src/OKF4net.Viewer/HtmlWriter.cs` — `Write` (doc comment, graph page, guard call, P3 write marker, `RenderPage(site, page)`), `GuardNoCaseCollisions` (signature, graph page, remarks), `RenderPage`, `RenderIndex`, `RenderShell`, `HtmlEscape` and `RootPrefix` (made `internal`); new `ViewKind`, `PageScripts`, `GraphPagePathOf`, `IsGraphPageName`, `RenderDocumentStart`, `ViewName`, `RenderHeader`, `Counts`, `ScriptTag`; marker `// P3: RenderGraph (§12.5)` before `HtmlEscape`
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` — remove the P1 header rules as Task 1 anchored them (`body > .topline` … `body > header.bar .wordmark sup`) and the P1 tool rules (`#okf-tools { … }`, `:is(#okf-tools, body > .okf-palette-backdrop) .okf-tool { … }`, `#okf-tools .okf-tool[aria-pressed="true"] { … }`); under `/* --- Task 11: header (H1-H13) --- */`
- Modify: `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs` (`Pages_declare_their_site_root_and_concept`, `Write_keeps_a_script_closing_tag_in_a_body_inside_the_payload`)
- Test: `tests/OKF4net.Tests/Viewer/HtmlWriterHeaderTests.cs` (new)
- Modify: `tools/viewer-security-check/run.js` (under `// --- Task 11: shell and header ---`; line 4 of the P1 chrome probes; the `img, script, svg, iframe, object` selector line of the P1 case `explorer, palette and contents render hostile titles as inert text`, F2)

**Interfaces:**
- Consumes: Task 2 (`WriteAssets`), Task 5 (`ViewerSite.BundleName`, `ViewerSite.GraphPagePath`, `SiteModel.BundleNameOf`, `SiteModel.FreeGraphPagePath`), Task 6 (`okf-shapes.js` exists), Task 7 (theme button), Task 3 (tokens, `.okf-section-title`).
- Produces (P3 calls these unchanged):
  - `internal enum HtmlWriter.ViewKind { Page, Index, Graph }`
  - `internal static string HtmlWriter.RenderDocumentStart(ViewerSite site, ViewKind view, string title, string rootPrefix, string? conceptId)` — from `<!doctype html>` to `</div></header>\n`.
  - `internal static string HtmlWriter.RenderHeader(ViewerSite site, ViewKind view, string rootPrefix, string? conceptId)`
  - `internal static string HtmlWriter.ScriptTag(string rootPrefix, string name)` → `<script src="{prefix}assets/{name}"></script>\n`
  - `internal static string HtmlWriter.HtmlEscape(string value)`, `internal static string HtmlWriter.RootPrefix(string relativePath)`
  - `internal static string HtmlWriter.GraphPagePathOf(ViewerSite site)`; `internal static string? HtmlWriter.Counts(ViewerSite site)`
  - `internal static readonly string[] HtmlWriter.PageScripts` = `marked.min.js, viewer.js, okf-index.js, okf-site.js, okf-shapes.js, okf-explorer.js, okf-palette.js, okf-toc.js` then the line `// P2: local graph`.
  - In `Write`: a local `graphPage` and the line `// P3: graph page (§12.5)` between the `index.html` write and the page loop.
  - Markup: `<html lang="en" data-okf-root="…" data-okf-view="page|index|graph"[ data-okf-concept="…"]>`; `body > a.okf-skip[href="#okf-main"]`; `body > div.topline`; `header.bar > div.bar-in` with `a.wordmark`, `span.bar-sep`, `span#okf-bundle-name.bar-bundle[title]`, `span#okf-bundle-counts.bar-counts` (omitted per H6), `div#okf-tools.bar-tools` holding (graph view) `a#okf-reading-view.okf-tool[href="index.html"]` then `a#okf-global-graph.okf-tool.okf-tool-graph`; `main#okf-main`; `h2#okf-toc-title.okf-section-title`.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Viewer/HtmlWriterHeaderTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>The document start and header of the three views, the script table and the graph page name (spec §11.1, §12.3, §12.6, A25).</summary>
public class HtmlWriterHeaderTests
{
    private static ViewerSite Acme(TempDir src)
    {
        src.Write("acme_retail/tables/users.md", "---\ntype: Table\ntitle: Users\n---\nSee [orders](orders.md).\n");
        src.Write("acme_retail/tables/orders.md", "---\ntype: Table\ntitle: Orders\n---\nNo link.\n");
        return SiteModel.Build(Bundle.Load(Path.Combine(src.Path, "acme_retail")));
    }

    private static string Read(string dest, string rel)
        => File.ReadAllText(Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void The_page_header_is_the_spec_header()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        var page = Read(dest.Path, "tables/users.html");

        Assert.Contains("<header class=\"bar\"><div class=\"bar-in\">\n<a class=\"wordmark\" href=\"../index.html\">OKF4net<sup>§</sup></a>\n<span class=\"bar-sep\" aria-hidden=\"true\"></span>\n", page);
        Assert.Contains("<span class=\"bar-bundle\" id=\"okf-bundle-name\" title=\"acme_retail\">acme_retail</span>", page);
        Assert.Contains("<span class=\"bar-counts\" id=\"okf-bundle-counts\">2 concepts · 1 link</span>", page);
        Assert.Contains("<div class=\"bar-tools\" id=\"okf-tools\">\n<a class=\"okf-tool okf-tool-graph\" id=\"okf-global-graph\" href=\"../graph.html#tables/users\">Global graph</a>\n</div>\n</div></header>\n", page);
        Assert.DoesNotContain("okf-reading-view", page);
    }

    [Fact]
    public void The_html_element_declares_root_view_and_concept()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        Assert.StartsWith("<!doctype html>\n<html lang=\"en\" data-okf-root=\"../\" data-okf-view=\"page\" data-okf-concept=\"tables/users\">\n", Read(dest.Path, "tables/users.html"));
        Assert.StartsWith("<!doctype html>\n<html lang=\"en\" data-okf-root=\"\" data-okf-view=\"index\">\n", Read(dest.Path, "index.html"));
    }

    [Fact]
    public void The_three_views_link_the_graph_page_as_the_spec_says()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = Acme(src);
        HtmlWriter.Write(site, dest.Path);

        Assert.Contains("id=\"okf-global-graph\" href=\"../graph.html#tables/users\">Global graph</a>", Read(dest.Path, "tables/users.html"));
        Assert.Contains("id=\"okf-global-graph\" href=\"graph.html\">Global graph</a>", Read(dest.Path, "index.html"));
        Assert.Contains(
            "id=\"okf-global-graph\" href=\"graph.html\" aria-current=\"page\">Global graph</a>",
            HtmlWriter.RenderDocumentStart(site, HtmlWriter.ViewKind.Graph, "Global graph", string.Empty, null));
    }

    [Fact]
    public void The_graph_view_has_Reading_view_before_the_current_Global_graph()
    {
        using var src = new TempDir();
        var start = HtmlWriter.RenderDocumentStart(Acme(src), HtmlWriter.ViewKind.Graph, "Global graph", string.Empty, null);

        Assert.StartsWith("<!doctype html>\n<html lang=\"en\" data-okf-root=\"\" data-okf-view=\"graph\">\n", start);
        Assert.Contains("<title>Global graph</title>", start);
        var reading = start.IndexOf("<a class=\"okf-tool\" id=\"okf-reading-view\" href=\"index.html\">Reading view</a>\n", StringComparison.Ordinal);
        var graph = start.IndexOf("<a class=\"okf-tool okf-tool-graph\" id=\"okf-global-graph\" href=\"graph.html\" aria-current=\"page\">", StringComparison.Ordinal);
        Assert.True(reading >= 0 && graph > reading, "Reading view must come right before the current Global graph");
        Assert.EndsWith("</div></header>\n", start);
    }

    [Fact]
    public void Skip_to_content_is_the_first_element_of_the_body_and_main_is_its_target()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        var page = Read(dest.Path, "tables/users.html");

        Assert.Contains("<body>\n<a class=\"okf-skip\" href=\"#okf-main\">Skip to content</a>\n<div class=\"topline\"></div>\n<header class=\"bar\">", page);
        Assert.Contains("<main id=\"okf-main\">\n", page);
    }

    [Fact]
    public void The_head_loads_the_theme_script_then_the_stylesheet_and_nothing_else()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        var page = Read(dest.Path, "tables/users.html");
        var head = page[..page.IndexOf("</head>", StringComparison.Ordinal)];

        Assert.Single(Regex.Matches(head, "<script "));
        Assert.EndsWith("<script src=\"../assets/okf-theme.js\"></script>\n<link rel=\"stylesheet\" href=\"../assets/viewer.css\">\n", head);
    }

    [Fact]
    public void Pages_and_the_index_load_the_script_table_in_order_after_the_payload()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        Assert.Equal(
            new[] { "marked.min.js", "viewer.js", "okf-index.js", "okf-site.js", "okf-shapes.js", "okf-explorer.js", "okf-palette.js", "okf-toc.js" },
            HtmlWriter.PageScripts.Take(8));
        foreach (var (rel, prefix) in new[] { ("tables/users.html", "../"), ("index.html", string.Empty) })
        {
            var page = Read(dest.Path, rel);
            var tail = page[page.IndexOf("id=\"okf-payload\"", StringComparison.Ordinal)..];
            var loaded = Regex.Matches(tail, "<script src=\"" + Regex.Escape(prefix) + "assets/([^\"]+)\"></script>").Select(m => m.Groups[1].Value);
            Assert.Equal(HtmlWriter.PageScripts, loaded);
        }
    }

    [Fact]
    public void Every_script_and_stylesheet_a_page_loads_is_written()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        foreach (var html in Directory.EnumerateFiles(dest.Path, "*.html", SearchOption.AllDirectories))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(html), "(?:src|href)=\"([^\"#]*assets/[^\"#]+)\""))
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(html)!, m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar)));
                Assert.True(File.Exists(target), $"{html} loads {m.Groups[1].Value}, which was not written");
            }
        }
    }

    [Fact]
    public void The_index_view_has_the_header_and_no_page_head()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        var index = Read(dest.Path, "index.html");

        Assert.Contains("id=\"okf-bundle-name\"", index);
        Assert.Contains("<h1>Bundle index</h1>", index);
        Assert.Contains("<p class=\"meta\">2 concepts</p>", index);
        Assert.DoesNotContain("okf-page-head", index);
    }

    [Fact]
    public void The_contents_title_is_a_shared_section_title()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        HtmlWriter.Write(Acme(src), dest.Path);

        Assert.Contains("<h2 id=\"okf-toc-title\" class=\"okf-section-title\">On this page</h2>", Read(dest.Path, "tables/users.html"));
    }

    [Fact]
    public void A_hostile_bundle_name_is_escaped_in_text_and_title()
    {
        using var src = new TempDir();
        var site = new ViewerSite(src.Path, [], string.Empty, []) { BundleName = "<img src=x onerror=alert(1)>\"'&" };

        var header = HtmlWriter.RenderHeader(site, HtmlWriter.ViewKind.Index, string.Empty, null);

        Assert.DoesNotContain("<img", header);
        Assert.Contains("title=\"&lt;img src=x onerror=alert(1)&gt;&quot;'&amp;\">&lt;img src=x onerror=alert(1)&gt;&quot;'&amp;</span>", header);
    }

    [Fact]
    public void A_hand_built_site_takes_its_bundle_name_from_its_root()
    {
        using var src = new TempDir();
        var site = new ViewerSite(Path.Combine(src.Path, "my-bundle"), [], string.Empty, []);

        Assert.Contains(">my-bundle</span>", HtmlWriter.RenderHeader(site, HtmlWriter.ViewKind.Index, string.Empty, null));
    }

    [Fact]
    public void Counts_are_singular_for_one_and_count_links_to_absent_concepts()
    {
        using var src = new TempDir();
        src.Write("a.md", "---\ntype: Note\ntitle: A\n---\nSee [gone](gone.md).\n");

        Assert.Equal("1 concept · 1 link", HtmlWriter.Counts(SiteModel.Build(Bundle.Load(src.Path))));
    }

    [Fact]
    public void Counts_are_omitted_for_a_hand_built_site_with_pages_but_no_index()
    {
        using var src = new TempDir();
        var page = new ViewerPage(ConceptId.Parse("x"), "X", "x.html", [], string.Empty, [], []);

        Assert.Null(HtmlWriter.Counts(new ViewerSite(src.Path, [page], string.Empty, [])));
        Assert.Equal("0 concepts · 0 links", HtmlWriter.Counts(new ViewerSite(src.Path, [], string.Empty, [])));
        Assert.DoesNotContain("okf-bundle-counts", HtmlWriter.RenderHeader(new ViewerSite(src.Path, [page], string.Empty, []), HtmlWriter.ViewKind.Index, string.Empty, null));
    }

    [Fact]
    public void A_concept_named_graph_moves_the_link_to_graph_1_and_the_site_still_renders()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("graph.md", "---\ntype: Note\ntitle: Graph\n---\n");

        HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);

        Assert.Contains("id=\"okf-global-graph\" href=\"graph-1.html#graph\">", Read(dest.Path, "graph.html"));
    }

    [Theory]
    [InlineData("my graph.html")]
    [InlineData("sub/graph.html")]
    [InlineData("graph.htm")]
    [InlineData(".html")]
    [InlineData("Graph.HTML")]
    [InlineData("<x>.html")]
    public void An_explicit_graph_page_must_be_one_safe_html_segment(string name)
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = new ViewerSite(src.Path, [], string.Empty, []) { GraphPagePath = name };

        Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, dest.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dest.Path));
    }

    [Theory]
    [InlineData("map.html", "map.html")]
    [InlineData("map.html", "Map.html")]
    [InlineData("index.html", "x.html")]
    [InlineData("map.html", "map.html/x.html")]
    [InlineData("map.html", "MAP.html/x/y.html")]
    public void An_explicit_graph_page_that_collides_is_refused_before_writing(string graph, string pagePath)
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = new ViewerPage(ConceptId.Parse("x"), "X", pagePath, [], string.Empty, [], []);
        var site = new ViewerSite(src.Path, [page], string.Empty, []) { GraphPagePath = graph };

        Assert.Throws<ArgumentException>(() => HtmlWriter.Write(site, dest.Path));
        Assert.Empty(Directory.GetFileSystemEntries(dest.Path));
    }

    [Fact]
    public void A_valid_explicit_graph_page_is_the_one_linked()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = new ViewerPage(ConceptId.Parse("x"), "X", "x.html", [], string.Empty, [], []);

        HtmlWriter.Write(new ViewerSite(src.Path, [page], string.Empty, []) { GraphPagePath = "map.html" }, dest.Path);

        Assert.Contains("id=\"okf-global-graph\" href=\"map.html#x\">", Read(dest.Path, "x.html"));
    }
}
```

In `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs`, replace in `Pages_declare_their_site_root_and_concept`

```csharp
        Assert.Contains("<html lang=\"en\" data-okf-root=\"../\" data-okf-concept=\"tables/users\">", nested);
        var root = File.ReadAllText(Path.Combine(dest.Path, "index.html"));
        Assert.Contains("<html lang=\"en\" data-okf-root=\"\">", root);
```

with

```csharp
        Assert.Contains("<html lang=\"en\" data-okf-root=\"../\" data-okf-view=\"page\" data-okf-concept=\"tables/users\">", nested);
        var root = File.ReadAllText(Path.Combine(dest.Path, "index.html"));
        Assert.Contains("<html lang=\"en\" data-okf-root=\"\" data-okf-view=\"index\">", root);
```

and in `Write_keeps_a_script_closing_tag_in_a_body_inside_the_payload` replace

```csharp
        // RenderShell always emits exactly nine <script> elements: okf-theme.js
        // in <head>, then the JSON payload, marked, viewer.js, okf-index.js,
        // okf-site.js, okf-explorer.js, okf-palette.js and okf-toc.js. A tenth
        // </script> would mean the body's own literal "</script>" text broke out
        // of the payload container instead of staying HTML-safe-JSON-escaped
        // inside it.
        Assert.Equal(9, CountOccurrences(page, "</script>"));
```

with

```csharp
        // Every <script> the page opens is closed by its own </script>, however
        // many scripts the page loads (spec §7): one closing tag more than the
        // openings would mean the body's literal "</script>" broke out of the
        // payload container instead of staying HTML-safe-JSON-escaped inside it.
        Assert.Equal(CountOccurrences(page, "<script"), CountOccurrences(page, "</script>"));
```

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlWriterHeaderTests|FullyQualifiedName~HtmlWriterTests"`
Expected: build FAILS with `CS0426: The type name 'ViewKind' does not exist in the type 'HtmlWriter'` (and `RenderDocumentStart`, `RenderHeader`, `Counts`, `PageScripts`).

- [ ] **Step 2: Write the shell, the header and the script table**

In `src/OKF4net.Viewer/HtmlWriter.cs`, add `using System.Globalization;` above `using System.Text;`.

In the doc comment of `Write`, replace

```
    /// the same file on a case-insensitive output volume -- refused
    /// unconditionally, even on a case-sensitive volume where both writes
    /// would otherwise succeed, because a site that renders differently per
    /// filesystem is not a site).
    /// </exception>
```

with

```
    /// the same file on a case-insensitive output volume -- refused
    /// unconditionally, even on a case-sensitive volume where both writes
    /// would otherwise succeed, because a site that renders differently per
    /// filesystem is not a site); or an explicit
    /// <see cref="ViewerSite.GraphPagePath"/> is not one
    /// <c>[A-Za-z0-9._-]+.html</c> segment, or collides with a page, a page's
    /// folder or <c>index.html</c> (a computed one never does: spec §12.3).
    /// </exception>
```

Replace the start and the end of `Write`'s body — from `GuardNoCaseCollisions(site);` to `GuardOutputDirectory(site.BundleRoot, outDir);`, and from `WriteFile(outDir, root, verifiedDirs, "index.html", RenderIndex(site), written);` to the closing `}` of the `foreach` — so that the method reads:

```csharp
    public static IReadOnlyList<string> Write(ViewerSite site, string outDir)
    {
        var graphPage = GraphPagePathOf(site);
        GuardNoCaseCollisions(site, graphPage);
        GuardOutputDirectory(site.BundleRoot, outDir);

        var written = new List<string>();
        Directory.CreateDirectory(outDir);

        // Canonicalized once here rather than per file: every file written
        // below shares the same root, so GuardWithinOutputDirectory no
        // longer re-resolves it on every call. verifiedDirs is the companion
        // cache that lets the ancestor walk itself run once per directory
        // instead of once per file -- see GuardWithinOutputDirectory's
        // remarks for what that trades away.
        var root = ReparsePoints.CanonicalizeRoot(outDir);
        var verifiedDirs = new HashSet<string>(StringComparer.Ordinal);

        WriteAssets(site, outDir, root, verifiedDirs, written);

        WriteFile(outDir, root, verifiedDirs, "index.html", RenderIndex(site), written);

        // P3: graph page (§12.5)

        foreach (var page in site.Pages)
        {
            WriteFile(outDir, root, verifiedDirs, page.RelativeHtmlPath, RenderPage(site, page), written);
        }

        return written;
    }
```

Replace the whole `GuardNoCaseCollisions` method — from its signature line `private static void GuardNoCaseCollisions(ViewerSite site)` through the method's own closing `}` (the block below ends with that brace; its doc comment and `<remarks>` above the signature stay) — with:

```csharp
    private static void GuardNoCaseCollisions(ViewerSite site, string graphPage)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["index.html"] = "the generated index page",
        };

        if (!seen.TryAdd(graphPage, "the graph page"))
        {
            throw new ArgumentException(
                $"the graph page '{graphPage}' would render to the same file as {seen[graphPage]}",
                paramName: nameof(site));
        }

        foreach (var page in site.Pages)
        {
            var description = $"concept '{page.Id}'";
            if (!seen.TryAdd(page.RelativeHtmlPath, description))
            {
                throw new ArgumentException(
                    $"{seen[page.RelativeHtmlPath]} and {description} would render to the same file on a case-insensitive volume ('{page.RelativeHtmlPath}')",
                    paramName: nameof(site));
            }

            var slash = page.RelativeHtmlPath.IndexOf('/');
            if (slash > 0 && string.Equals(page.RelativeHtmlPath[..slash], graphPage, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"{description} lives in a folder named like the graph page ('{graphPage}')",
                    paramName: nameof(site));
            }
        }
    }
```

and append to the `<remarks>` of `GuardNoCaseCollisions`, before `/// </remarks>`:

```
    ///
    /// The graph page's name (<paramref name="graphPage"/>, spec §12.3) is
    /// seeded too, and a page whose first path segment is that name -- a
    /// folder -- is refused as well. <see cref="SiteModel.FreeGraphPagePath"/>
    /// never picks a name that trips either check, so only an explicit
    /// <see cref="ViewerSite.GraphPagePath"/> set by a host can: a host error,
    /// reported before anything is written.
```

Replace `RenderPage`, `RenderIndex` and `RenderShell` (the three methods, with `RenderShell`'s doc comment) with:

```csharp
    private static string RenderPage(ViewerSite site, ViewerPage page)
    {
        var prefix = RootPrefix(page.RelativeHtmlPath);
        var main = new StringBuilder();

        main.Append("<h1>").Append(HtmlEscape(page.Title)).Append("</h1>\n");
        main.Append("<p class=\"meta\">").Append(HtmlEscape(page.Id.ToString())).Append("</p>\n");
        main.Append(RenderFrontmatter(page.Frontmatter));
        main.Append("<div id=\"okf-body\"></div>\n");

        return RenderShell(site, ViewKind.Page, page.Title, prefix, page.Id.ToString(), main.ToString(), RenderBacklinks(page.Backlinks), Payload(page));
    }

    private static string RenderIndex(ViewerSite site)
    {
        var main = new StringBuilder();
        main.Append("<h1>Bundle index</h1>\n");
        main.Append("<p class=\"meta\">")
            .Append(site.Pages.Count)
            .Append(site.Pages.Count == 1 ? " concept" : " concepts")
            .Append("</p>\n");

        if (site.ParseErrors.Count > 0)
        {
            main.Append("<div class=\"errors\">\n<h2>Parse errors</h2>\n<ul>\n");
            foreach (var error in site.ParseErrors)
            {
                main.Append("<li><code>").Append(HtmlEscape(error.Path)).Append("</code> — ")
                    .Append(HtmlEscape(error.Error)).Append("</li>\n");
            }

            main.Append("</ul>\n</div>\n");
        }

        main.Append("<div id=\"okf-body\"></div>\n");

        // The index's links already point at generated .html paths, so its
        // rewiring table is deliberately empty.
        var payload = BuildPayload(site.IndexMarkdown, "{}");
        return RenderShell(site, ViewKind.Index, "Bundle index", string.Empty, conceptId: null, main.ToString(), aside: string.Empty, payload);
    }

    /// <summary>The three views a page of the site can be (spec §12.3), written as <c>data-okf-view</c>.</summary>
    internal enum ViewKind
    {
        /// <summary>A concept page.</summary>
        Page,

        /// <summary>The bundle index, <c>index.html</c>.</summary>
        Index,

        /// <summary>The global graph page (written by P3's <c>RenderGraph</c>).</summary>
        Graph,
    }

    /// <summary>
    /// The scripts at the end of every concept page and of the index, in load
    /// order (spec §12.6). A script is listed only once it exists under
    /// <c>Assets/</c>. P2 adds <c>"okf-local.js"</c> under its marker, last;
    /// <c>graph.html</c> does not use this table (P3's <c>RenderGraph</c>
    /// writes its own tags).
    /// </summary>
    internal static readonly string[] PageScripts =
    [
        "marked.min.js",
        "viewer.js",
        "okf-index.js",
        "okf-site.js",
        "okf-shapes.js",
        "okf-explorer.js",
        "okf-palette.js",
        "okf-toc.js",
        // P2: local graph
    ];

    /// <summary>
    /// The graph page's file name (spec §12.3, A25): the site's explicit
    /// <see cref="ViewerSite.GraphPagePath"/>, which must be one
    /// <c>[A-Za-z0-9._-]+.html</c> segment, else the first free name.
    /// </summary>
    /// <exception cref="ArgumentException">The explicit name is not one such segment.</exception>
    internal static string GraphPagePathOf(ViewerSite site)
    {
        if (site.GraphPagePath is not { } explicitPath)
        {
            return SiteModel.FreeGraphPagePath(site.Pages);
        }

        if (!IsGraphPageName(explicitPath))
        {
            throw new ArgumentException(
                $"the graph page '{explicitPath}' is not one [A-Za-z0-9._-]+.html segment",
                paramName: nameof(site));
        }

        return explicitPath;
    }

    private static bool IsGraphPageName(string name)
        => name.Length > ".html".Length
           && name.EndsWith(".html", StringComparison.Ordinal)
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// Everything from <c>&lt;!doctype html&gt;</c> to the end of the header, for
    /// the three views (spec §12.3): the root prefix, the view and the concept
    /// on <c>&lt;html&gt;</c>; <c>okf-theme.js</c> then the stylesheet in
    /// <c>&lt;head&gt;</c> (a stored theme applies before the first paint);
    /// "Skip to content", the top line and the header. P3 writes
    /// <c>graph.html</c> with <c>RenderDocumentStart(site, ViewKind.Graph,
    /// "Global graph", "", null)</c>.
    /// </summary>
    internal static string RenderDocumentStart(ViewerSite site, ViewKind view, string title, string rootPrefix, string? conceptId)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\" data-okf-root=\"").Append(HtmlEscape(rootPrefix))
          .Append("\" data-okf-view=\"").Append(ViewName(view)).Append('"');
        if (conceptId is not null)
        {
            sb.Append(" data-okf-concept=\"").Append(HtmlEscape(conceptId)).Append('"');
        }

        sb.Append(">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<title>").Append(HtmlEscape(title)).Append("</title>\n")
          .Append(ScriptTag(rootPrefix, "okf-theme.js"))
          .Append("<link rel=\"stylesheet\" href=\"").Append(HtmlEscape(rootPrefix)).Append("assets/viewer.css\">\n")
          .Append("</head>\n<body>\n")
          .Append("<a class=\"okf-skip\" href=\"#okf-main\">Skip to content</a>\n")
          .Append("<div class=\"topline\"></div>\n")
          .Append(RenderHeader(site, view, rootPrefix, conceptId));
        return sb.ToString();
    }

    private static string ViewName(ViewKind view) => view switch
    {
        ViewKind.Page => "page",
        ViewKind.Index => "index",
        _ => "graph",
    };

    /// <summary>
    /// The header (spec §11.1, §12.3): wordmark, bundle name, counts, and the
    /// tools: "Reading view" (graph view only), then "Global graph", the one
    /// source of the graph page's real name -- with the concept as fragment
    /// on a concept page, current on the graph view. The palette, "Filters"
    /// and the theme button are added by their scripts.
    /// </summary>
    internal static string RenderHeader(ViewerSite site, ViewKind view, string rootPrefix, string? conceptId)
    {
        var name = site.BundleName ?? SiteModel.BundleNameOf(site.BundleRoot);
        var graphPage = GraphPagePathOf(site);
        var sb = new StringBuilder("<header class=\"bar\"><div class=\"bar-in\">\n");
        sb.Append("<a class=\"wordmark\" href=\"").Append(HtmlEscape(rootPrefix)).Append("index.html\">OKF4net<sup>§</sup></a>\n");
        sb.Append("<span class=\"bar-sep\" aria-hidden=\"true\"></span>\n");
        sb.Append("<span class=\"bar-bundle\" id=\"okf-bundle-name\" title=\"").Append(HtmlEscape(name)).Append("\">")
          .Append(HtmlEscape(name)).Append("</span>\n");
        if (Counts(site) is { } counts)
        {
            sb.Append("<span class=\"bar-counts\" id=\"okf-bundle-counts\">").Append(counts).Append("</span>\n");
        }

        sb.Append("<div class=\"bar-tools\" id=\"okf-tools\">\n");
        if (view == ViewKind.Graph)
        {
            sb.Append("<a class=\"okf-tool\" id=\"okf-reading-view\" href=\"index.html\">Reading view</a>\n");
        }

        var href = view == ViewKind.Page && conceptId is not null
            ? rootPrefix + graphPage + "#" + conceptId
            : rootPrefix + graphPage;
        sb.Append("<a class=\"okf-tool okf-tool-graph\" id=\"okf-global-graph\" href=\"").Append(HtmlEscape(href)).Append('"');
        if (view == ViewKind.Graph)
        {
            sb.Append(" aria-current=\"page\"");
        }

        sb.Append(">Global graph</a>\n</div>\n</div></header>\n");
        return sb.ToString();
    }

    /// <summary>
    /// "N concepts · M links" (spec §11.1, H6): concepts and merged edges of the
    /// index, links to absent concepts included, singular for one. Null --
    /// the counts are omitted -- for a site built by hand whose index is empty
    /// although it has pages.
    /// </summary>
    internal static string? Counts(ViewerSite site)
    {
        var concepts = site.Index.Concepts.Count;
        if (concepts == 0 && site.Pages.Count > 0)
        {
            return null;
        }

        var links = site.Index.Edges.Count;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{concepts} {(concepts == 1 ? "concept" : "concepts")} · {links} {(links == 1 ? "link" : "links")}");
    }

    /// <summary>One <c>&lt;script src&gt;</c> line loading an asset, relative to the page's root prefix.</summary>
    internal static string ScriptTag(string rootPrefix, string name)
        => $"<script src=\"{HtmlEscape(rootPrefix)}assets/{HtmlEscape(name)}\"></script>\n";

    /// <summary>
    /// A concept page or the index (spec §12.3, §12.6): document start and
    /// header, the three zones, the payload and the script table. Its ids and
    /// attributes are the contract the interactive scripts rely on.
    /// </summary>
    private static string RenderShell(ViewerSite site, ViewKind view, string title, string rootPrefix, string? conceptId, string main, string aside, string payload)
    {
        var sb = new StringBuilder(RenderDocumentStart(site, view, title, rootPrefix, conceptId));
        sb.Append("<div class=\"okf-layout\">\n")
          .Append("<nav class=\"okf-explorer\" id=\"okf-explorer\" aria-label=\"Explorer\" hidden></nav>\n")
          .Append("<main id=\"okf-main\">\n").Append(main).Append("</main>\n")
          .Append("<aside class=\"okf-context\" id=\"okf-context\" aria-label=\"Page context\"").Append(aside.Length == 0 ? " hidden" : string.Empty).Append(">\n")
          .Append("<section class=\"okf-toc\" id=\"okf-toc\" aria-labelledby=\"okf-toc-title\" hidden>\n")
          .Append("<h2 id=\"okf-toc-title\" class=\"okf-section-title\">On this page</h2>\n<ul></ul>\n</section>\n")
          .Append(aside).Append("</aside>\n</div>\n")
          .Append("<script type=\"application/json\" id=\"okf-payload\">").Append(payload).Append("</script>\n");
        foreach (var script in PageScripts)
        {
            sb.Append(ScriptTag(rootPrefix, script));
        }

        return sb.Append("</body>\n</html>\n").ToString();
    }
```

Make the two helpers internal and place P3's marker: replace `private static string RootPrefix(string relativePath)` with `internal static string RootPrefix(string relativePath)`; and replace

```csharp
    /// <summary>
    /// Escapes text interpolated into the generated markup. Bundle content is
    /// semi-trusted -- a bundle may come from a third-party repository -- so
    /// every value reaching the page goes through this.
    /// </summary>
    private static string HtmlEscape(string value)
```

with

```csharp
    // P3: RenderGraph (§12.5)

    /// <summary>
    /// Escapes text interpolated into the generated markup, attribute values
    /// included (always written between double quotes): <c>&amp; &lt; &gt; "</c>.
    /// Bundle content is semi-trusted -- a bundle may come from a third-party
    /// repository -- so every value reaching the page goes through this.
    /// </summary>
    internal static string HtmlEscape(string value)
```

- [ ] **Step 3: Run the tests**

Run: `dotnet build OKF4net.sln` then `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer|FullyQualifiedName~OKF4net.Tests.Render"`
Expected: no warning; all pass, including the 27 `HtmlWriterHeaderTests` (as the test runner counts them) and every P1 `HtmlWriterTests`.

- [ ] **Step 4: Write the header CSS**

In `src/OKF4net.Viewer/Assets/viewer.css`, delete the P1 header rules **as Task 1 anchored them** (commit `84588e4`; the file holds exactly these lines, between the `body { … }` rule and the `/* break-word: …` comment):

```css
body > .topline { height: 6px; background: var(--blue); }
header.bar { border-bottom: 1px solid var(--hair); }
body > header.bar > .bar-in {
  max-width: 1440px; margin: 0 auto; padding: 16px clamp(16px, 3.5vw, 48px);
  display: flex; align-items: center; gap: 24px;
}
body > header.bar .wordmark {
  font-family: var(--display); font-weight: 900; font-size: 20px;
  letter-spacing: -.02em; color: var(--ink); text-decoration: none;
}
body > header.bar .wordmark sup { color: var(--blue); font-family: var(--mono); font-weight: 700; }
```

All five rules go, `body > header.bar > .bar-in { max-width: 1440px; margin: 0 auto; … }` included: the new `body > header.bar .bar-in` rule below has exactly the same specificity (0,2,2), so a kept `body > header.bar > .bar-in` would go on applying its `max-width: 1440px` and `margin: 0 auto` (H2 wants the bar full width) and its `padding`/`gap` wherever the new rule did not restate them. Deleting it is the change, not an override. The rules under the Task 11 marker restate `body > .topline`, `body > header.bar`, `.wordmark` and `.wordmark sup` with H1–H3's values.

Then delete

```css
#okf-tools { margin-left: auto; display: flex; flex-wrap: wrap; gap: 8px; }
:is(#okf-tools, body > .okf-palette-backdrop) .okf-tool {
  font: inherit; font-size: 14px; min-height: 36px; padding: 0 12px; cursor: pointer;
  border: 1px solid var(--hair); background: var(--white); color: var(--ink);
}
#okf-tools .okf-tool[aria-pressed="true"] { border-color: var(--blue); color: var(--blue); }
```

Under `/* --- Task 11: header (H1-H13) --- */`, insert:

```css
/* H12: the first focusable element, shown only while it has the focus. */
body > .okf-skip {
  position: absolute; left: 8px; top: -60px; z-index: 20; padding: 8px 12px;
  background: var(--blue); color: var(--white); font-size: 13.5px; font-weight: 600; text-decoration: none;
}
body > .okf-skip:focus { top: 8px; }
/* H1: the 6 px blue band. */
body > .topline { height: 6px; background: var(--blue); }
/* H2: a 52 px full-width bar, 20 px gaps and side padding. */
body > header.bar { border-bottom: 1px solid var(--hair); }
body > header.bar .bar-in { display: flex; align-items: center; gap: 20px; height: 52px; padding: 0 20px; min-width: 0; }
/* H3 */
body > header.bar .wordmark {
  flex: none; font-family: var(--display); font-weight: 900; font-size: 20px;
  letter-spacing: -.02em; color: var(--ink); text-decoration: none;
}
body > header.bar .wordmark sup { color: var(--blue); font-family: var(--mono); font-weight: 700; font-size: 12px; }
/* H4 */
body > header.bar .bar-sep { flex: none; width: 1px; height: 20px; background: var(--hair); }
/* H5: one line, cut with an ellipsis, so a long name never pushes the tools away. */
body > header.bar .bar-bundle {
  flex: 0 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  font-family: var(--mono); font-size: 13px; color: var(--ink);
}
/* H6 */
body > header.bar .bar-counts { flex: none; font-family: var(--mono); font-size: 13px; color: var(--gray); white-space: nowrap; }
/* The tools, after the flexible space: palette (H7), Filters (H8), Reading
   view (H10), Global graph (H9), theme (H11). */
#okf-tools { margin-left: auto; display: flex; align-items: center; gap: 20px; min-width: 0; flex: 0 1 auto; }
#okf-tools .okf-tool {
  display: inline-flex; align-items: center; gap: 8px; height: 34px; padding: 0 14px; box-sizing: border-box;
  font-family: var(--body); font-size: 13.5px; font-weight: 500; line-height: 1; white-space: nowrap;
  border: 1px solid var(--hair); background: var(--white); color: var(--ink); text-decoration: none; cursor: pointer;
}
/* H9: blue on the reading pages; filled and current on the graph page. */
#okf-tools .okf-tool-graph { border-color: var(--blue); background: var(--blue-soft); color: var(--blue); font-weight: 600; }
#okf-tools .okf-tool-graph[aria-current="page"] { background: var(--blue); color: var(--white); }
/* H13: under 900 px the bar takes two lines (brand, name, counts; then the
   tools), the counts go at phone width, and nothing scrolls sideways. */
@media (max-width: 899px) {
  body > header.bar .bar-in { flex-wrap: wrap; height: auto; padding: 8px 16px; row-gap: 8px; }
  #okf-tools { flex: 1 1 100%; flex-wrap: wrap; gap: 8px; margin-left: 0; }
}
@media (max-width: 520px) {
  body > header.bar .bar-counts { display: none; }
}
```

- [ ] **Step 5: Harness cases and the P1 tool probe**

In `run.js`, in `checkAsync("the real P1 chrome keeps its anchored styles", …)`, replace the probe line

```js
    [doc.querySelector("#okf-tools .okf-tool"), "min-height", "36px"],
```

with

```js
    [doc.querySelector("#okf-tools .okf-tool"), "height", "34px"],
```

In the P1 case `explorer, palette and contents render hostile titles as inert text` (F2), replace the line

```js
    assert(root.querySelectorAll("img, script, svg, iframe, object").length === 0, `markup from bundle text became live in #${id}`);
```

with

```js
    // svg.okf-glyph is the chrome's own glyph (OkfShapes.icon, Tasks 13 and
    // 14 draw one per explorer row and palette option); any other svg would
    // still be markup that bundle text made live.
    assert(root.querySelectorAll("img, script, iframe, object, svg:not(.okf-glyph)").length === 0, `markup from bundle text became live in #${id}`);
```

This line is Task 11's alone (wave 3): Tasks 13 and 14, which add the glyphs in parallel in wave 4, leave it as it is, so neither goes red for the other's glyphs and they never edit the same line.

Under `// --- Task 11: shell and header ---`, insert:

```js
checkAsync("header: Skip to content is the first focusable element and reaches main", async () => {
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const focusable = doc.querySelectorAll("a[href], button, input, select, textarea, [tabindex]:not([tabindex='-1'])");
  const first = focusable[0];
  assert(first && first.matches("body > a.okf-skip") && first.getAttribute("href") === "#okf-main", `first focusable: <${first && first.tagName.toLowerCase()} class="${first && first.className}">`);
  const main = doc.getElementById("okf-main");
  assert(main && main === doc.querySelector("body > .okf-layout > main"), "#okf-main is not the layout's <main>");
  assert(window.getComputedStyle(first).getPropertyValue("position") === "absolute", "the skip link is not taken out of the flow");
});

checkAsync("header: the tools keep their order, Global graph links this concept, and the bundle name and counts are text", async () => {
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const tools = Array.from(doc.getElementById("okf-tools").children);
  const graph = doc.getElementById("okf-global-graph");
  assert(tools[0].classList.contains("okf-palette-open"), `first tool: ${tools[0].className}`);
  assert(tools[tools.length - 1].id === "okf-theme-toggle", `last tool: ${tools[tools.length - 1].id}`);
  const at = tools.indexOf(graph);
  assert(at > 0 && at < tools.length - 1, `Global graph at ${at} of ${tools.length}`);
  assert(graph.getAttribute("href") === "../graph.html#foo/bar" && !graph.hasAttribute("aria-current"), `Global graph: ${graph.getAttribute("href")}`);
  const name = doc.getElementById("okf-bundle-name");
  assert(name.textContent === "hostile-bundle" && name.children.length === 0 && name.getAttribute("title") === "hostile-bundle", `bundle name: ${name.outerHTML}`);
  const idx = window.OKF_INDEX;
  const n = idx.concepts.length;
  const m = idx.edges.length;
  const expected = `${n} ${n === 1 ? "concept" : "concepts"} · ${m} ${m === 1 ? "link" : "links"}`;
  assert(doc.getElementById("okf-bundle-counts").textContent === expected, `counts: ${doc.getElementById("okf-bundle-counts").textContent}, expected ${expected}`);
  const index = await openPage("index.html");
  assert(index.document.getElementById("okf-global-graph").getAttribute("href") === "graph.html", "the index links the graph page with a fragment or a prefix");
  assert(index.document.documentElement.getAttribute("data-okf-view") === "index" && window.document.documentElement.getAttribute("data-okf-view") === "page", "data-okf-view");
});

checkAsync("header: the real header keeps its anchored styles", async () => {
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const chrome = [
    [doc.querySelector("body > .topline"), "height", "6px"],
    [doc.querySelector("body > header.bar .bar-in"), "height", "52px"],
    [doc.querySelector("body > header.bar .bar-bundle"), "text-overflow", "ellipsis"],
    [doc.querySelector("#okf-tools .okf-tool-graph"), "height", "34px"],
    [doc.querySelector("#okf-tools .okf-tool-graph"), "font-weight", "600"],
  ];
  for (const [el, prop, value] of chrome) {
    assert(el, `a header element this case needs is missing (${prop}: ${value})`);
    const got = window.getComputedStyle(el).getPropertyValue(prop);
    assert(got === value, `the real <${el.tagName.toLowerCase()} class="${el.getAttribute("class")}"> lost its ${prop}: ${value} (got ${got})`);
  }
});
```

- [ ] **Step 6: Run the harness**

```bash
cd tools/viewer-security-check && npm test
```

Expected: the three Task 11 cases `ok`; P1's cases still `ok` (the palette opener is still inserted first, the theme toggle appended last, `#okf-explorer` and `#okf-context` unchanged, the hostile-title case with its new selector); `the real P1 chrome keeps its anchored styles` `ok` (its `body > header.bar > .bar-in` row still reads `display: flex` from the new descendant rule); the static anchor scan and the chrome-class case `ok` on both fixture pages; `N passed, 0 failed`.

- [ ] **Step 7: Format and commit**

```bash
dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Viewer/HtmlWriter.cs src/OKF4net.Viewer/Assets/viewer.css tests/OKF4net.Tests/Viewer/HtmlWriterHeaderTests.cs tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs tools/viewer-security-check/run.js
git commit -m "feat(viewer): one header for the page, index and graph views, the script table, the graph page name, merge markers for P2 and P3

The P1 hostile-title case now rejects any svg but the chrome's own
svg.okf-glyph (img, script, iframe, object, svg:not(.okf-glyph)), so the
explorer and palette glyphs of the next wave do not trip it.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 12: Page head markup (C2–C6) and "Referenced by · N" (X10)

Wave 4. The centre column's head, written in C#: breadcrumb, one `<h1>`, the chips with empty glyph slots, the folding frontmatter box; the payload carries `DisplayBody`; "Referenced by" gets its count and `.okf-row` rows with `data-okf-target`. Glyphs and "Show all" come in Task 15.

**Files:**
- Modify: `src/OKF4net.Viewer/HtmlWriter.cs` — `Write` (one line: `var lookups = new PageLookups(site);` and the `RenderPage` call), `RenderPage`, `RenderFrontmatter` (replaced by `RenderFrontmatterBox`), `RenderBacklinks`, `Payload`; new `PageLookups`, `RenderPageHead`, `RenderCrumbs`, `RenderChips`, `TrustText`
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` — remove the P1 `table.frontmatter` rules, the P1 `#okf-context :is(.okf-toc, .okf-backlinks) h2 { … }` rule and the P1 `#okf-context .okf-backlinks li { … }` rule; under `/* --- Task 12: page head (C2-C6) and Referenced by (X10) --- */`
- Modify: `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs` (`Backlinks_live_in_the_page_context_aside_which_is_hidden_without_them`)
- Test: `tests/OKF4net.Tests/Viewer/PageHeadTests.cs` (new)
- Create: `tools/viewer-security-check/fixtures/hostile-bundle/p11-page.md`, `p11-page-ref.md`
- Modify: `tools/viewer-security-check/run.js` (under `// --- Task 12: page head ---`; the P1 cases `nothing long and unbroken widens the page…` and `a GFM table in the body scrolls…`; one line of `CHROME_ANCHORS`, Step 4)

**Interfaces:**
- Consumes: Task 5 (`ViewerPage.Head`, `DisplayBody`, `ViewerFrontmatterEntry.Extra/Structured`, `SiteModel.BundleNameOf`), Task 4 (`ViewerIndex.Types`, `IndexConcept.TypeIndex`, `SiteIndex.OtherSlot`), Task 11 (`RenderShell`, `ViewKind`, `HtmlEscape`, `RootPrefix`).
- Produces (read by Task 15, never by a bundle string):
  - `main > div.okf-page-head` (first child of `main`): `nav.okf-crumbs[aria-label="Breadcrumb"] > ol > li` (bundle name linking `index.html`; each folder a link when a concept of that id exists, else a `<span>`; separators `span.okf-crumb-sep[aria-hidden]`; the last segment `span[aria-current="page"]`), `h1`, `div.okf-chips`, `section#okf-fm.okf-fm`.
  - Chips: `span.okf-chip.okf-chip-type > span.okf-chip-glyph[data-okf-slot="0…5"]` + type label; `span.okf-chip.okf-chip-status` (when present); `span.okf-chip.okf-chip-trust[.okf-chip-unverified]` with `span.okf-chip-glyph[data-okf-trust="human|machine"]` for a verified tier; `span.okf-chip.okf-chip-stale > span.okf-chip-glyph[data-okf-stale] + span.okf-chip-text` "stale after YYYY-MM-DD" (when the index has a date).
  - Frontmatter: `section#okf-fm.okf-fm[aria-labelledby="okf-fm-title"] > div.okf-fm-head > h2#okf-fm-title.okf-section-title` "Frontmatter · N fields" (`1 field`), then `div#okf-fm-grid.okf-fm-grid > div.okf-fm-cell[data-okf-extra]? > span.okf-fm-key + span.okf-fm-value[.okf-fm-struct]`. No box when there is no entry.
  - Backlinks: `h2#okf-backlinks-title.okf-section-title` = `Referenced by <span class="okf-count">· N</span>`; rows `li > a.okf-row[href][data-okf-target="<id>"]`.

- [ ] **Step 1: Write the failing xunit tests**

Create `tests/OKF4net.Tests/Viewer/PageHeadTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>The centre column's head and "Referenced by", as written (spec §11.3 C2–C6, §11.4 X10, §12.3).</summary>
public class PageHeadTests
{
    private const string Period =
        "---\ntype: Attested Computation\ntitle: Gross margin for a period\nstatus: stable\n"
        + "verified:\n  - { by: \"human:jsmith\", at: \"2026-07-01T09:00:00Z\" }\n"
        + "stale_after: \"2026-12-31\"\ntags: [finance, margin]\nruntime: bigquery\na: 1\nb: 2\nc: 3\n---\n"
        + "# Gross margin for a period\n\nText with [m](../metrics/margin.md).\n";

    private static ViewerSite Acme(TempDir src)
    {
        src.Write("acme/computations/gross-margin-period.md", Period);
        src.Write("acme/computations.md", "---\ntype: Note\ntitle: Computations\n---\nThe folder's own page.\n");
        src.Write("acme/metrics/margin.md", "---\ntype: Metric\ntitle: Margin\n---\nSee [p](../computations/gross-margin-period.md).\n");
        src.Write("acme/untyped.md", "---\ntitle: Untyped\n---\nNothing.\n");
        return SiteModel.Build(Bundle.Load(Path.Combine(src.Path, "acme")));
    }

    private static string Write(ViewerSite site, TempDir dest, string rel)
    {
        HtmlWriter.Write(site, dest.Path);
        return File.ReadAllText(Path.Combine(dest.Path, rel.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static int SlotOf(ViewerSite site, string id)
    {
        var concept = site.Index.Concepts.Single(c => c.Id.ToString() == id);
        return site.Index.Types[concept.TypeIndex].Slot;
    }

    [Fact]
    public void Breadcrumb_links_the_bundle_index_and_a_folder_that_is_also_a_concept()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = Write(Acme(src), dest, "computations/gross-margin-period.html");

        Assert.Contains(
            "<div class=\"okf-page-head\">\n<nav class=\"okf-crumbs\" aria-label=\"Breadcrumb\"><ol>\n"
            + "<li><a href=\"../index.html\">acme</a></li>\n"
            + "<li><span class=\"okf-crumb-sep\" aria-hidden=\"true\">/</span><a href=\"../computations.html\">computations</a></li>\n"
            + "<li><span class=\"okf-crumb-sep\" aria-hidden=\"true\">/</span><span aria-current=\"page\">gross-margin-period</span></li>\n"
            + "</ol></nav>\n",
            page);
    }

    [Fact]
    public void Breadcrumb_shows_a_folder_without_its_own_concept_as_text()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = Write(Acme(src), dest, "metrics/margin.html");

        Assert.Contains("<li><span class=\"okf-crumb-sep\" aria-hidden=\"true\">/</span><span>metrics</span></li>\n", page);
    }

    [Fact]
    public void The_head_has_one_h1_with_the_display_title_and_replaces_the_meta_line()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = Write(Acme(src), dest, "computations/gross-margin-period.html");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "<h1"));
        Assert.Contains("</nav>\n<h1>Gross margin for a period</h1>\n<div class=\"okf-chips\">", page);
        Assert.DoesNotContain("class=\"meta\"", page);
        Assert.Contains("<main id=\"okf-main\">\n<div class=\"okf-page-head\">", page);
    }

    [Fact]
    public void The_payload_carries_the_display_body_and_Body_stays_raw()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = Acme(src);
        var page = Write(site, dest, "computations/gross-margin-period.html");

        var start = page.IndexOf("id=\"okf-payload\">", StringComparison.Ordinal) + "id=\"okf-payload\">".Length;
        var json = page[start..page.IndexOf("</script>", start, StringComparison.Ordinal)];
        var body = JsonDocument.Parse(json).RootElement.GetProperty("body").GetString()!;

        Assert.StartsWith("\nText with", body, StringComparison.Ordinal);
        Assert.StartsWith("# Gross margin for a period", site.Pages.Single(p => p.Id.ToString() == "computations/gross-margin-period").Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Chips_show_the_type_with_its_slot_the_status_the_trust_with_verifier_and_date_and_stale_after()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = Acme(src);
        var page = Write(site, dest, "computations/gross-margin-period.html");
        var slot = SlotOf(site, "computations/gross-margin-period");

        Assert.Contains(
            "<div class=\"okf-chips\">\n"
            + $"<span class=\"okf-chip okf-chip-type\"><span class=\"okf-chip-glyph\" data-okf-slot=\"{slot}\"></span>Attested Computation</span>\n"
            + "<span class=\"okf-chip okf-chip-status\">stable</span>\n"
            + "<span class=\"okf-chip okf-chip-trust\"><span class=\"okf-chip-glyph\" data-okf-trust=\"human\"></span>human-reviewed · jsmith · 2026-07-01</span>\n"
            + "<span class=\"okf-chip okf-chip-stale\"><span class=\"okf-chip-glyph\" data-okf-stale></span><span class=\"okf-chip-text\">stale after 2026-12-31</span></span>\n"
            + "</div>\n",
            page);
    }

    [Fact]
    public void An_untyped_unverified_page_shows_no_type_and_a_muted_trust_chip_without_glyph()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = Acme(src);
        var page = Write(site, dest, "untyped.html");

        Assert.Equal(5, SlotOf(site, "untyped"));
        Assert.Contains("<span class=\"okf-chip okf-chip-type\"><span class=\"okf-chip-glyph\" data-okf-slot=\"5\"></span>(no type)</span>\n", page);
        Assert.Contains("<span class=\"okf-chip okf-chip-trust okf-chip-unverified\">unverified</span>\n", page);
        Assert.DoesNotContain("okf-chip-status", page);
        Assert.DoesNotContain("okf-chip-stale", page);
    }

    [Fact]
    public void A_further_verification_is_counted_after_the_date()
    {
        var head = new ViewerPageHead("Note", null, "machine-confirmed", "tool:okf-ci", "2026-07-01", 2, null);

        Assert.Equal("machine-confirmed · tool:okf-ci · 2026-07-01 · +2", HtmlWriter.TrustText(head));
        Assert.Equal("machine-confirmed", HtmlWriter.TrustText(head with { Verifier = null, VerifiedDate = null, MoreVerifications = 0 }));
    }

    [Fact]
    public void Frontmatter_box_counts_every_field_and_marks_the_folded_ones()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = Write(Acme(src), dest, "computations/gross-margin-period.html");

        // Period declares ten entries: type, title, status, verified,
        // stale_after (shown above, folded), tags, runtime, a, b (the four
        // unfolded ones), c (folded).
        Assert.Contains("<section class=\"okf-fm\" id=\"okf-fm\" aria-labelledby=\"okf-fm-title\">\n<div class=\"okf-fm-head\"><h2 class=\"okf-section-title\" id=\"okf-fm-title\">Frontmatter · 10 fields</h2></div>\n<div class=\"okf-fm-grid\" id=\"okf-fm-grid\">\n", page);
        Assert.Contains("<div class=\"okf-fm-cell\" data-okf-extra><span class=\"okf-fm-key\">type</span><span class=\"okf-fm-value\">Attested Computation</span></div>\n", page);
        Assert.Contains("<div class=\"okf-fm-cell\"><span class=\"okf-fm-key\">tags</span><span class=\"okf-fm-value\">finance, margin</span></div>\n", page);
        Assert.Contains("<div class=\"okf-fm-cell\"><span class=\"okf-fm-key\">b</span><span class=\"okf-fm-value\">2</span></div>\n", page);
        Assert.Contains("<div class=\"okf-fm-cell\" data-okf-extra><span class=\"okf-fm-key\">c</span><span class=\"okf-fm-value\">3</span></div>\n", page);
        Assert.Contains("<div class=\"okf-fm-cell\" data-okf-extra><span class=\"okf-fm-key\">verified</span><span class=\"okf-fm-value okf-fm-struct\">", page);
    }

    [Fact]
    public void A_single_field_is_singular_and_a_page_without_fields_has_no_box()
    {
        using var src = new TempDir();
        using var one = new TempDir();
        using var none = new TempDir();
        var single = new ViewerPage(ConceptId.Parse("x"), "X", "x.html", [new ViewerFrontmatterEntry("k", "v")], string.Empty, [], []);
        var empty = new ViewerPage(ConceptId.Parse("x"), "X", "x.html", [], string.Empty, [], []);

        Assert.Contains("Frontmatter · 1 field</h2>", Write(new ViewerSite(src.Path, [single], string.Empty, []), one, "x.html"));
        Assert.DoesNotContain("okf-fm", Write(new ViewerSite(src.Path, [empty], string.Empty, []), none, "x.html"));
    }

    [Fact]
    public void Referenced_by_counts_its_rows_and_marks_each_with_its_target()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var page = Write(Acme(src), dest, "computations/gross-margin-period.html");

        Assert.Contains("<h2 id=\"okf-backlinks-title\" class=\"okf-section-title\">Referenced by <span class=\"okf-count\">· 1</span></h2>\n<ul>\n", page);
        Assert.Contains("<li><a class=\"okf-row\" href=\"../metrics/margin.html\" data-okf-target=\"metrics/margin\">metrics/margin</a></li>\n", page);
    }

    [Fact]
    public void Hostile_status_verifier_date_keys_and_values_are_escaped()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("evil.md",
            "---\ntype: \"<b>T</b>\"\ntitle: Evil\nstatus: \"<img src=x onerror=alert(1)>\"\n"
            + "verified:\n  - { by: \"human:<script>x</script>\", at: \"<i>2026</i>\" }\n"
            + "bad<key>: \"<v>&\\\"\"\n---\nBody.\n");
        var page = Write(SiteModel.Build(Bundle.Load(src.Path)), dest, "evil.html");
        var head = page[page.IndexOf("<div class=\"okf-page-head\">", StringComparison.Ordinal)..page.IndexOf("<div id=\"okf-body\">", StringComparison.Ordinal)];

        foreach (var raw in new[] { "<img src=x", "<script>x", "<i>2026", "<b>T</b>", "<v>", "bad<key>" })
        {
            Assert.DoesNotContain(raw, head);
        }

        Assert.Contains("&lt;b&gt;T&lt;/b&gt;</span>", head);
        Assert.Contains("<span class=\"okf-chip okf-chip-status\">&lt;img src=x onerror=alert(1)&gt;</span>", head);
        Assert.Contains("human-reviewed · &lt;script&gt;x&lt;/script&gt; · &lt;i&gt;2026&lt;/i&gt;</span>", head);
        Assert.Contains("<span class=\"okf-fm-key\">bad&lt;key&gt;</span><span class=\"okf-fm-value\">&lt;v&gt;&amp;&quot;</span>", head);
    }
}
```

In `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs`, in `Backlinks_live_in_the_page_context_aside_which_is_hidden_without_them`, replace

```csharp
        var backlinks = orders.IndexOf("<h2 id=\"okf-backlinks-title\">Referenced by</h2>", StringComparison.Ordinal);
```

with

```csharp
        var backlinks = orders.IndexOf("<h2 id=\"okf-backlinks-title\" class=\"okf-section-title\">Referenced by <span class=\"okf-count\">· 1</span></h2>", StringComparison.Ordinal);
```

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~PageHeadTests"`
Expected: build FAILS with `CS0117: 'HtmlWriter' does not contain a definition for 'TrustText'`.

- [ ] **Step 2: Write the page head**

In `src/OKF4net.Viewer/HtmlWriter.cs`, in `Write`, insert `var lookups = new PageLookups(site);` directly after `var graphPage = GraphPagePathOf(site);`, and replace `RenderPage(site, page)` with `RenderPage(site, page, lookups)`.

Replace the method `RenderPage` with:

```csharp
    private static string RenderPage(ViewerSite site, ViewerPage page, PageLookups lookups)
    {
        var prefix = RootPrefix(page.RelativeHtmlPath);
        var main = RenderPageHead(site, page, prefix, lookups) + "<div id=\"okf-body\"></div>\n";
        return RenderShell(site, ViewKind.Page, page.Title, prefix, page.Id.ToString(), main, RenderBacklinks(page.Backlinks), Payload(page));
    }

    /// <summary>Lookups every concept page shares, built once per <see cref="Write"/>.</summary>
    private sealed class PageLookups
    {
        public PageLookups(ViewerSite site)
        {
            foreach (var page in site.Pages)
            {
                PathById.TryAdd(page.Id.ToString(), page.RelativeHtmlPath);
            }

            var types = site.Index.Types;
            foreach (var concept in site.Index.Concepts)
            {
                var slot = concept.TypeIndex >= 0 && concept.TypeIndex < types.Count ? types[concept.TypeIndex].Slot : SiteIndex.OtherSlot;
                SlotById.TryAdd(concept.Id.ToString(), slot);
            }
        }

        /// <summary>Each page's path by concept id: the breadcrumb links a folder that is also a concept (C2).</summary>
        public Dictionary<string, string> PathById { get; } = new(StringComparer.Ordinal);

        /// <summary>Each concept's type slot by id, read from the index's types table, never recomputed (C5).</summary>
        public Dictionary<string, int> SlotById { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// The head of a concept page (spec §11.3, §12.3): breadcrumb (C2), the one
    /// <c>&lt;h1&gt;</c> (C3), the chips (C5) and the frontmatter box (C6). The
    /// glyph slots are empty: <c>okf-page.js</c> fills them from the fixed
    /// <c>data-okf-*</c> values written here, never from bundle text.
    /// </summary>
    private static string RenderPageHead(ViewerSite site, ViewerPage page, string prefix, PageLookups lookups)
    {
        var sb = new StringBuilder("<div class=\"okf-page-head\">\n");
        sb.Append(RenderCrumbs(site, page, prefix, lookups));
        sb.Append("<h1>").Append(HtmlEscape(page.Title)).Append("</h1>\n");
        sb.Append(RenderChips(page, lookups));
        sb.Append(RenderFrontmatterBox(page.Frontmatter));
        return sb.Append("</div>\n").ToString();
    }

    private static string RenderCrumbs(ViewerSite site, ViewerPage page, string prefix, PageLookups lookups)
    {
        const string Separator = "<span class=\"okf-crumb-sep\" aria-hidden=\"true\">/</span>";
        var name = site.BundleName ?? SiteModel.BundleNameOf(site.BundleRoot);
        var sb = new StringBuilder("<nav class=\"okf-crumbs\" aria-label=\"Breadcrumb\"><ol>\n");
        sb.Append("<li><a href=\"").Append(HtmlEscape(prefix)).Append("index.html\">").Append(HtmlEscape(name)).Append("</a></li>\n");
        var segments = page.Id.Segments;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var folder = string.Join('/', segments.Take(i + 1));
            sb.Append("<li>").Append(Separator);
            if (lookups.PathById.TryGetValue(folder, out var path))
            {
                sb.Append("<a href=\"").Append(HtmlEscape(prefix + path)).Append("\">").Append(HtmlEscape(segments[i])).Append("</a>");
            }
            else
            {
                sb.Append("<span>").Append(HtmlEscape(segments[i])).Append("</span>");
            }

            sb.Append("</li>\n");
        }

        sb.Append("<li>").Append(Separator).Append("<span aria-current=\"page\">").Append(HtmlEscape(segments[^1])).Append("</span></li>\n");
        return sb.Append("</ol></nav>\n").ToString();
    }

    private static string RenderChips(ViewerPage page, PageLookups lookups)
    {
        if (page.Head is not { } head)
        {
            return string.Empty;
        }

        var slot = lookups.SlotById.TryGetValue(page.Id.ToString(), out var s) ? s : SiteIndex.OtherSlot;
        var sb = new StringBuilder("<div class=\"okf-chips\">\n");
        sb.Append("<span class=\"okf-chip okf-chip-type\"><span class=\"okf-chip-glyph\" data-okf-slot=\"")
          .Append(slot.ToString(CultureInfo.InvariantCulture)).Append("\"></span>")
          .Append(HtmlEscape(head.Type.Length == 0 ? "(no type)" : head.Type)).Append("</span>\n");
        if (head.Status is { } status)
        {
            sb.Append("<span class=\"okf-chip okf-chip-status\">").Append(HtmlEscape(status)).Append("</span>\n");
        }

        var glyph = head.Trust == AuditVocabulary.Name(TrustTier.HumanReviewed) ? "human"
            : head.Trust == AuditVocabulary.Name(TrustTier.MachineConfirmed) ? "machine"
            : null;
        sb.Append("<span class=\"okf-chip okf-chip-trust").Append(glyph is null ? " okf-chip-unverified" : string.Empty).Append("\">");
        if (glyph is not null)
        {
            sb.Append("<span class=\"okf-chip-glyph\" data-okf-trust=\"").Append(glyph).Append("\"></span>");
        }

        sb.Append(HtmlEscape(TrustText(head))).Append("</span>\n");
        if (head.StaleAfterDate is { } date)
        {
            sb.Append("<span class=\"okf-chip okf-chip-stale\"><span class=\"okf-chip-glyph\" data-okf-stale></span><span class=\"okf-chip-text\">stale after ")
              .Append(HtmlEscape(date)).Append("</span></span>\n");
        }

        return sb.Append("</div>\n").ToString();
    }

    /// <summary>The trust chip's text (C5): the tier, then " · verifier · date · +N" when the tier names a verifier.</summary>
    internal static string TrustText(ViewerPageHead head)
    {
        var text = new StringBuilder(head.Trust);
        if (head.Verifier is { } verifier)
        {
            text.Append(" · ").Append(verifier);
            if (head.VerifiedDate is { } date)
            {
                text.Append(" · ").Append(date);
            }

            if (head.MoreVerifications > 0)
            {
                text.Append(" · +").Append(head.MoreVerifications.ToString(CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }
```

Replace the method `RenderFrontmatter` with:

```csharp
    /// <summary>
    /// The frontmatter box (spec §11.3, C6): every entry in document order in a
    /// two-column grid; the folded ones carry <c>data-okf-extra</c>, hidden by
    /// <c>viewer.css</c> only while JavaScript runs (<c>html[data-okf-js]</c>)
    /// and the box is not expanded. Without JavaScript everything shows.
    /// </summary>
    private static string RenderFrontmatterBox(IReadOnlyList<ViewerFrontmatterEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<section class=\"okf-fm\" id=\"okf-fm\" aria-labelledby=\"okf-fm-title\">\n");
        sb.Append("<div class=\"okf-fm-head\"><h2 class=\"okf-section-title\" id=\"okf-fm-title\">Frontmatter · ")
          .Append(entries.Count.ToString(CultureInfo.InvariantCulture))
          .Append(entries.Count == 1 ? " field" : " fields").Append("</h2></div>\n");
        sb.Append("<div class=\"okf-fm-grid\" id=\"okf-fm-grid\">\n");
        foreach (var entry in entries)
        {
            sb.Append("<div class=\"okf-fm-cell\"").Append(entry.Extra ? " data-okf-extra" : string.Empty).Append('>')
              .Append("<span class=\"okf-fm-key\">").Append(HtmlEscape(entry.Key)).Append("</span>")
              .Append("<span class=\"okf-fm-value").Append(entry.Structured ? " okf-fm-struct" : string.Empty).Append("\">")
              .Append(HtmlEscape(entry.Value)).Append("</span></div>\n");
        }

        return sb.Append("</div>\n</section>\n").ToString();
    }
```

Replace the method `RenderBacklinks` with:

```csharp
    /// <summary>
    /// "Referenced by · N" (spec §11.4, X10): <c>.okf-row</c> links, each
    /// carrying its source id in <c>data-okf-target</c> so <c>okf-page.js</c>
    /// finds its type in the index and adds its glyph.
    /// </summary>
    private static string RenderBacklinks(IReadOnlyList<ViewerLink> backlinks)
    {
        if (backlinks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<section class=\"okf-backlinks\" aria-labelledby=\"okf-backlinks-title\">\n")
            .Append("<h2 id=\"okf-backlinks-title\" class=\"okf-section-title\">Referenced by <span class=\"okf-count\">· ")
            .Append(backlinks.Count.ToString(CultureInfo.InvariantCulture)).Append("</span></h2>\n<ul>\n");
        foreach (var link in backlinks)
        {
            sb.Append("<li><a class=\"okf-row\" href=\"").Append(HtmlEscape(link.Href))
              .Append("\" data-okf-target=\"").Append(HtmlEscape(link.RawTarget)).Append("\">")
              .Append(HtmlEscape(link.RawTarget)).Append("</a></li>\n");
        }

        return sb.Append("</ul>\n</section>\n").ToString();
    }
```

In `Payload`, replace `return BuildPayload(page.Body, links.ToString());` with:

```csharp
        // The display body: the leading H1 that repeats the title is already
        // in the page head (spec §11.3, C4); viewer.js renders what it gets.
        return BuildPayload(page.DisplayBody ?? page.Body, links.ToString());
```

- [ ] **Step 3: Run the xunit tests**

Run: `dotnet build OKF4net.sln` then `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer|FullyQualifiedName~OKF4net.Tests.Render"`
Expected: all pass, including the 11 `PageHeadTests`, P1's `HtmlWriterTests` (`Write_renders_the_frontmatter_table` still finds "The users table") and `HtmlWriterHeaderTests`.

- [ ] **Step 4: Style the page head**

In `viewer.css`, delete the P1 rules

```css
table.frontmatter { border-collapse: collapse; width: 100%; font-size: 14px; }
table.frontmatter th, table.frontmatter td {
  text-align: left; vertical-align: top; padding: 6px 12px 6px 0;
  border-bottom: 1px solid var(--hair);
}
table.frontmatter th { font-family: var(--mono); font-weight: 400; color: var(--gray); width: 200px; }
/* anywhere, not break-word: a table cell is as wide as its min-content, so
   only anywhere lets a long unbroken value fit a phone's width. */
table.frontmatter td { overflow-wrap: anywhere; }
```

and

```css
#okf-context :is(.okf-toc, .okf-backlinks) h2 {
  font-family: var(--mono); font-size: 11px; font-weight: 400; letter-spacing: .06em;
  text-transform: uppercase; color: var(--gray); margin: 0 0 8px;
}
```

and

```css
#okf-context .okf-backlinks li { padding: 6px 0; border-bottom: 1px solid var(--hair); }
```

Under `/* --- Task 12: page head (C2-C6) and Referenced by (X10) --- */`, insert:

```css
/* C2: the breadcrumb, an ordered list with decorative separators. */
body > .okf-layout > main > .okf-page-head .okf-crumbs ol {
  list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; align-items: center; gap: 8px;
  font-family: var(--mono); font-size: 12.5px; color: var(--gray);
}
body > .okf-layout > main > .okf-page-head .okf-crumbs li { display: inline-flex; align-items: center; gap: 8px; min-width: 0; overflow-wrap: anywhere; }
body > .okf-layout > main > .okf-page-head .okf-crumbs a { color: var(--gray); text-decoration: none; }
body > .okf-layout > main > .okf-page-head .okf-crumbs a:hover { color: var(--blue); }
body > .okf-layout > main > .okf-page-head .okf-crumbs [aria-current="page"] { color: var(--ink); }
/* C3 */
body > .okf-layout > main > .okf-page-head h1 {
  margin: 14px 0 6px; font-family: var(--display); font-weight: 600; font-size: 34px;
  letter-spacing: -.02em; line-height: 1.15;
}
/* C5: the chips themselves are the shared .okf-chip (Task 3). */
body > .okf-layout > main > .okf-page-head .okf-chips { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin: 0 0 18px; }
/* C6: the frontmatter box. */
body > .okf-layout > main > .okf-page-head .okf-fm { border: 1px solid var(--hair); margin: 0 0 22px; }
body > .okf-layout > main > .okf-page-head .okf-fm-head {
  display: flex; align-items: center; justify-content: space-between; gap: 12px;
  padding: 9px 14px; border-bottom: 1px solid var(--hair); background: var(--blue-soft);
}
body > .okf-layout > main > .okf-page-head .okf-fm-head .okf-section-title { margin: 0; }
/* Two columns; every cell has a bottom rule and the grid sinks 1 px under the
   box's own border, so the last row shows no extra rule whatever is folded. */
body > .okf-layout > main > .okf-page-head .okf-fm-grid {
  display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); font-size: 13.5px; margin-bottom: -1px;
}
body > .okf-layout > main > .okf-page-head .okf-fm-cell {
  display: flex; gap: 12px; padding: 8px 14px; min-width: 0; border-bottom: 1px solid var(--hair);
}
body > .okf-layout > main > .okf-page-head .okf-fm-key {
  flex: none; width: 92px; font-family: var(--mono); font-size: 12px; color: var(--gray); overflow-wrap: anywhere;
}
/* anywhere: a long unbroken value fits a phone's width (recette R1). */
body > .okf-layout > main > .okf-page-head .okf-fm-value { min-width: 0; overflow-wrap: anywhere; }
body > .okf-layout > main > .okf-page-head .okf-fm-struct { font-family: var(--mono); font-size: 12.5px; }
/* Folded (A27) only while JavaScript runs and the box is not expanded: without
   JavaScript every entry shows, and the fold applies before the first paint
   (okf-theme.js marks <html> in <head>). */
html[data-okf-js] body > .okf-layout > main > .okf-page-head .okf-fm:not([data-okf-expanded]) .okf-fm-cell[data-okf-extra] { display: none; }
@media (max-width: 600px) {
  body > .okf-layout > main > .okf-page-head .okf-fm-grid { grid-template-columns: minmax(0, 1fr); }
}
/* X10: the rows are the shared .okf-row (Task 3). */
#okf-context .okf-backlinks li { list-style: none; }
```

The fold rule starts with `html[data-okf-js]`, an anchor kind Task 1's static scan does not know: it would be reported although its real anchor is the page head. In `tools/viewer-security-check/run.js`, in `CHROME_ANCHORS`, directly after the line

```js
  /^body > \.okf-layout > main > \.okf-page-head(?![\w-])/,
```

insert

```js
  // The fold of the frontmatter box (Task 12): the page head, under the mark
  // okf-theme.js puts on <html> while JavaScript runs.
  /^html\[data-okf-js\] body > \.okf-layout > main > \.okf-page-head(?![\w-])/,
```

- [ ] **Step 5: Fixtures and harness cases**

Create `tools/viewer-security-check/fixtures/hostile-bundle/p11-page.md`:

```markdown
---
type: Policy
title: Page head probe
description: The P1.1 page head, with hostile values.
status: "<img src=x onerror=window.__pwned=1>"
verified:
  - { by: "human:<img src=x onerror=window.__pwned=1>", at: "<b>2026-07-01</b>" }
stale_after: "2000-01-01T00:00:00Z"
probe<i>key</i>: "<img src=x onerror=window.__pwned=1>value"
runtime: probe
owner: probe
area: probe
region: probe
---
# Page head probe

## Section

Text.
```

and `tools/viewer-security-check/fixtures/hostile-bundle/p11-page-ref.md`:

```markdown
---
type: Metric
title: Page head referrer
description: Links to the page head probe.
---
See [the probe](p11-page.md).
```

In `run.js`, in the P1 case `nothing long and unbroken widens the page: panels and tree entries shrink, page text wraps`, replace the line

```js
    [doc.querySelector("main table.frontmatter td"), "overflow-wrap", "anywhere"],
```

with

```js
    [doc.querySelector("body > .okf-layout > main > .okf-page-head .okf-fm-value"), "overflow-wrap", "anywhere"],
```

and replace the whole P1 case `a GFM table in the body scrolls in its own box; the frontmatter table is left alone` (its comment line above included) with:

```js
// A wide GFM table in the page scrolls inside its own box (the page does not
// widen at 390 px); the frontmatter box, outside #okf-body, is not a table
// and is left alone. Declarations only: the recette measures the page's width.
checkAsync("a GFM table in the body scrolls in its own box; the frontmatter box is left alone", async () => {
  const window = await openPage("chrome-classes.html");
  const doc = window.document;
  const table = doc.querySelector("#okf-body table");
  assert(table, "the fixture lost its GFM table");
  const style = window.getComputedStyle(table);
  assert(style.getPropertyValue("display") === "block" && style.getPropertyValue("overflow-x") === "auto",
    `#okf-body table: display ${style.getPropertyValue("display")}, overflow-x ${style.getPropertyValue("overflow-x")}`);
  const box = doc.querySelector("body > .okf-layout > main > .okf-page-head .okf-fm");
  assert(box && box.querySelector("table") === null, "the frontmatter box is missing or is a table again");
  assert(window.getComputedStyle(box).getPropertyValue("overflow-x") !== "auto", "the frontmatter box scrolls like a body table");
});
```

Under `// --- Task 12: page head ---`, insert:

```js
checkAsync("page head: the breadcrumb links resolve from the site root, wherever the site is moved", async () => {
  for (const mount of ["", "moved/elsewhere/"]) {
    const window = await openPage("foo/bar.html", { mount });
    const crumbs = window.document.querySelector("body > .okf-layout > main > .okf-page-head nav.okf-crumbs");
    assert(crumbs && crumbs.getAttribute("aria-label") === "Breadcrumb", "the breadcrumb is not a labelled nav");
    const links = Array.from(crumbs.querySelectorAll("a"));
    assert(links.length === 2 && links[0].textContent === "hostile-bundle" && links[0].href === `${BASE}${mount}index.html`, `bundle crumb: ${links[0] && links[0].href}`);
    assert(links[1].textContent === "foo" && links[1].href === `${BASE}${mount}foo.html`, `folder crumb: ${links[1] && links[1].href} (foo is also a concept)`);
    const last = crumbs.querySelector('[aria-current="page"]');
    assert(last && last.textContent === "bar" && last.localName === "span", "the current segment is not the last crumb");
  }
  const typed = await openPage("p11-types/m1.html");
  const folder = typed.document.querySelector("body > .okf-layout > main > .okf-page-head nav.okf-crumbs li:nth-child(2)");
  assert(folder && folder.querySelector("a") === null && folder.textContent.endsWith("p11-types"), "a folder without its own concept is not plain text");
});

checkAsync("page head: the body does not repeat the title H1", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  assert(doc.querySelectorAll("#okf-body h1").length === 0, "the body still repeats the title");
  const h1 = doc.querySelectorAll("h1");
  assert(h1.length === 1 && h1[0].closest("body > .okf-layout > main > .okf-page-head") && h1[0].textContent === "Page head probe", `h1: ${h1.length}`);
  assert(doc.querySelector("#okf-toc a").getAttribute("href") === "#okf-h-section", "the contents do not start at the first remaining heading");
});

checkAsync("page head: hostile status, verifier, date and frontmatter are written as inert text", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  const head = doc.querySelector("body > .okf-layout > main > .okf-page-head");
  assert(head.querySelectorAll("img, script, b, i").length === 0, `markup from bundle text became live in the page head: ${head.innerHTML.slice(0, 200)}`);
  for (const text of ["<img src=x onerror=window.__pwned=1>", "<b>2026-07-01</b>", "probe<i>key</i>"]) {
    assert(head.textContent.includes(text), `the page head dropped ${text} instead of showing it as text`);
  }
  assert(window.__pwned === undefined, "a hostile value executed");
});

checkAsync("page head: the real page head keeps its anchored styles, and folds without JavaScript's help", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  const head = doc.querySelector("body > .okf-layout > main > .okf-page-head");
  const extra = head.querySelector(".okf-fm-cell[data-okf-extra]");
  const chrome = [
    [head.querySelector("h1"), "font-size", "34px"],
    [head.querySelector(".okf-chips .okf-chip"), "height", "26px"],
    [head.querySelector(".okf-fm-key"), "width", "92px"],
    [head.querySelector(".okf-fm-value"), "overflow-wrap", "anywhere"],
    [extra, "display", "none"],
  ];
  for (const [el, prop, value] of chrome) {
    assert(el, `a page-head element this case needs is missing (${prop}: ${value})`);
    const got = window.getComputedStyle(el).getPropertyValue(prop);
    assert(got === value, `the real <${el.tagName.toLowerCase()} class="${el.getAttribute("class")}"> lost its ${prop}: ${value} (got ${got})`);
  }
  doc.documentElement.removeAttribute("data-okf-js");
  assert(window.getComputedStyle(extra).getPropertyValue("display") !== "none", "a folded entry stays hidden without the JavaScript mark");
});

checkAsync("page head: Referenced by counts its rows, each a .okf-row naming its source", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  const title = doc.getElementById("okf-backlinks-title");
  const rows = Array.from(doc.querySelectorAll("#okf-context .okf-backlinks a.okf-row[data-okf-target]"));
  assert(title && title.textContent === `Referenced by · ${rows.length}` && rows.length >= 1, `title: ${title && title.textContent}`);
  assert(rows.some((a) => a.getAttribute("data-okf-target") === "p11-page-ref" && a.getAttribute("href") === "p11-page-ref.html"), "the referrer row is missing");
});
```

- [ ] **Step 6: Run the harness**

```bash
cd tools/viewer-security-check && npm test
```

Expected: the five Task 12 cases `ok`; P1's cases still `ok` (headings and contents of `foo.html` and `foo/bar.html` unchanged: their bodies do not start with their title); the chrome-class case `ok` (body code wearing `okf-fm`, `okf-fm-cell`, `okf-chip-glyph`… keeps its style); `N passed, 0 failed`.

- [ ] **Step 7: Format and commit**

```bash
dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Viewer/HtmlWriter.cs src/OKF4net.Viewer/Assets/viewer.css tests/OKF4net.Tests/Viewer/PageHeadTests.cs tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs tools/viewer-security-check/fixtures/hostile-bundle/p11-page.md tools/viewer-security-check/fixtures/hostile-bundle/p11-page-ref.md tools/viewer-security-check/run.js
git commit -m "feat(viewer): page head with breadcrumb, one H1, chips and a folding frontmatter box; Referenced by with its count

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 13: Explorer as mockup A draws it (E1–E13), type chips and "Filters" (H8)

Wave 4. Rows labelled by the last id segment (A22) with the title as tooltip, a type glyph from `OkfShapes`, folder counts, trust and staleness flags from `OkfShapes`, the active row marked; type chips that filter the tree (OR between chips, AND with the name field); the legend; the header's "Filters" button with its count. Updates the P1 cases and the helper `treeLink` this changes (§12.7).

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/okf-explorer.js` (whole file)
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` — remove the P1 explorer rules, from `#okf-explorer .okf-explorer-label {` to `#okf-explorer .okf-stale { … }`; under `/* --- Task 13: explorer (E1-E13) and Filters (H8) --- */`
- Modify: `tools/viewer-security-check/run.js` — `treeLink`; the P1 cases `explorer: a node is both a page and a folder…`, `staleness is evaluated at reading time…`, `a sub-millisecond deadline…`, `explorer: the filter field is labelled by what it does` (replaced), `explorer: the stale badge shows staleAfterDate…`, `explorer, palette and contents render hostile titles as inert text` (one line: the explorer's `<img` text line; its `img, script, iframe, object, svg:not(.okf-glyph)` selector line was set by Task 11 (F2) and is not edited here); line 3 of `the real P1 chrome keeps its anchored styles`; under `// --- Task 13: explorer ---`

**Interfaces:**
- Consumes: `OkfSite` (P1), `OkfShapes` (Task 6: `icon`, `slotOf`, `KINDS`, `typeLabel`, `trustKind`, `typeLegendEntries`, `legend`), index v2 (Task 4), `#okf-global-graph` in `#okf-tools` (Task 11), Task 3's sticky head/foot and shared chip CSS.
- Produces:
  - `nav#okf-explorer > div.okf-explorer-head` (`p.okf-section-title.okf-explorer-title[aria-hidden]` "Explorer", `input#okf-tree-filter[type=search][aria-label="Filter by name"]`, `div.okf-type-chips[role=group][aria-label="Filter by type"] > button.okf-chip[aria-pressed][data-okf-slot]`), `ul.okf-tree`, `div.okf-explorer-foot > ul.okf-legend`.
  - Rows: `li > div.okf-tree-row[.okf-tree-current]` with, in order, `button.okf-tree-toggle[aria-expanded]` (when children), `span.okf-glyph-slot > svg.okf-glyph` (when a concept), `a.okf-tree-link[title][data-okf-id]` or `span.okf-tree-folder`, `span.okf-tree-count` (when children), `span.okf-flag.okf-flag-trust`, `span.okf-flag.okf-flag-stale[title]`; inline `padding-left` = 12 + 18 × depth px.
  - Header: `button#okf-filters-toggle.okf-tool` ("Filters", `span.okf-sr` ", N active", `span.okf-filters-count[aria-hidden]` hidden at 0) inserted just before `#okf-global-graph`.
  - Harness: `treeLink(window, id)` finds `#okf-explorer a.okf-tree-link[data-okf-id="<id>"]` (by attribute value, never a selector built from the id).

- [ ] **Step 1: Update the P1 cases this task changes, and write the new cases**

In `run.js`, replace `treeLink` with:

```js
// The explorer link of the concept `id`, found by the data-okf-id attribute
// (spec §11.2, E8: the label is the last segment, the title a tooltip). The id
// is compared as a value, never spliced into a selector.
function treeLink(window, id) {
  return Array.from(window.document.querySelectorAll("#okf-explorer a.okf-tree-link"))
    .find((a) => a.getAttribute("data-okf-id") === id) || null;
}
```

In `explorer: a node is both a page and a folder, with separate open and expand commands`, replace

```js
  assert(current && current.getAttribute("title") === "foo/bar", "the current page is not marked");
```

with

```js
  assert(current && current.getAttribute("data-okf-id") === "foo/bar", "the current page is not marked");
```

In `staleness is evaluated at reading time and refreshed when the page becomes visible`, `a sub-millisecond deadline is stale from the next whole millisecond, as in C#` and `explorer: the stale badge shows staleAfterDate as written, never a date rebuilt from staleAfterMs`, replace every `.querySelector(".okf-stale")` with `.querySelector(".okf-flag-stale")`.

Replace the whole case `explorer: the filter field is labelled by what it does` with:

```js
checkAsync("explorer: the field is named Filter by name by aria-label, the title is text", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const filter = doc.getElementById("okf-tree-filter");
  assert(filter.getAttribute("aria-label") === "Filter by name", `aria-label: ${filter.getAttribute("aria-label")}`);
  assert(filter.getAttribute("placeholder") === "Filter by name" + String.fromCharCode(0x2026), `placeholder: ${filter.getAttribute("placeholder")}`);
  assert(doc.querySelector("#okf-explorer label") === null && filter.labels.length === 0, "the field still has a <label> (E2: the nav is already named Explorer)");
  const title = doc.querySelector("#okf-explorer .okf-explorer-head .okf-explorer-title");
  assert(title && title.textContent === "Explorer" && title.localName === "p" && title.classList.contains("okf-section-title"), "the explorer title is not section-title text");
});
```

In `explorer, palette and contents render hostile titles as inert text`, replace

```js
  assert(doc.getElementById("okf-explorer").textContent.includes("<img"), "the hostile title was dropped instead of shown as text");
```

with

```js
  assert(treeLink(window, "foo").getAttribute("title").includes("<img") && treeLink(window, "foo").textContent === "foo",
    "the explorer row of foo must show its segment and carry the hostile title as a tooltip");
```

In `the real P1 chrome keeps its anchored styles`, replace the probe line

```js
    [doc.querySelector("#okf-explorer .okf-badge.okf-trust-human"), "width", "8px"],
```

with

```js
    [doc.querySelector("#okf-explorer .okf-flag-trust"), "width", "10px"],
```

Under `// --- Task 13: explorer ---`, insert:

```js
// The type slot of the concept at `pos`, read from the executed index.
function indexSlot(idx, pos) {
  const t = idx.types[idx.concepts[pos].typeIndex];
  return t ? t.slot : 5;
}

checkAsync("explorer: rows show the segment, its type glyph, counts and flags in the spec order", async () => {
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const idx = window.OKF_INDEX;
  const parts = (row) => Array.from(row.children, (el) => el.classList.contains("okf-flag") ? el.classList[1] : el.classList[0]);
  const below = (node) => node.children.reduce((n, c) => n + (c.concept >= 0 ? 1 : 0) + below(c), 0);
  const fooNode = idx.tree.find((n) => n.name === "foo");
  const foo = treeLink(window, "foo");
  const fooPos = idx.concepts.findIndex((c) => c.id === "foo");
  assert(foo.textContent === "foo" && foo.getAttribute("title") === idx.concepts[fooPos].title, `foo label "${foo.textContent}", title "${foo.getAttribute("title")}"`);
  assert(JSON.stringify(parts(foo.parentElement)) === JSON.stringify(["okf-tree-toggle", "okf-glyph-slot", "okf-tree-link", "okf-tree-count"]),
    `foo row (a destination and a folder): ${JSON.stringify(parts(foo.parentElement))}`);
  assert(foo.parentElement.querySelector(".okf-tree-count").textContent === String(below(fooNode)), "foo's count is not its destinations below");
  assert(foo.parentElement.querySelector(".okf-tree-toggle").getAttribute("aria-expanded") === "true", "the path to the current page is not expanded");
  assert(foo.parentElement.querySelector(`.okf-glyph-slot svg .okf-shape-${indexSlot(idx, fooPos)}`), "foo's glyph is not the shape of its type's slot");
  assert(foo.parentElement.style.paddingLeft === "12px", `foo row padding-left ${foo.parentElement.style.paddingLeft}`);
  const bar = treeLink(window, "foo/bar");
  assert(bar.textContent === "bar" && bar.parentElement.style.paddingLeft === "30px", `bar: "${bar.textContent}", ${bar.parentElement.style.paddingLeft}`);
  assert(JSON.stringify(parts(bar.parentElement)) === JSON.stringify(["okf-glyph-slot", "okf-tree-link", "okf-flag-trust", "okf-flag-stale"]),
    `bar row (human-reviewed, stale): ${JSON.stringify(parts(bar.parentElement))}`);
  const leaf = treeLink(window, "toString");
  assert(JSON.stringify(parts(leaf.parentElement)) === JSON.stringify(["okf-glyph-slot", "okf-tree-link"]), `toString row: ${JSON.stringify(parts(leaf.parentElement))}`);
  const folder = Array.from(doc.querySelectorAll("#okf-explorer .okf-tree-folder")).find((s) => s.textContent === "p11-types");
  const folderNode = idx.tree.find((n) => n.name === "p11-types");
  assert(folder && folderNode && folderNode.concept === -1, "this case needs the p11-types folder without its own concept");
  assert(JSON.stringify(parts(folder.parentElement)) === JSON.stringify(["okf-tree-toggle", "okf-tree-folder", "okf-tree-count"]), `folder row: ${JSON.stringify(parts(folder.parentElement))}`);
  assert(folder.parentElement.querySelector(".okf-tree-count").textContent === String(below(folderNode)), "the folder count is not its destinations below");
});

checkAsync("explorer: the current row is marked", async () => {
  const window = await openPage("foo/bar.html");
  const rows = window.document.querySelectorAll("#okf-explorer .okf-tree-row.okf-tree-current");
  assert(rows.length === 1 && rows[0].querySelector('a.okf-tree-link[aria-current="page"]') === treeLink(window, "foo/bar"), `${rows.length} current rows`);
});

checkAsync("explorer: a hostile title lives in the title attribute only", async () => {
  const window = await openPage("index.html");
  const nav = window.document.getElementById("okf-explorer");
  const foo = treeLink(window, "foo");
  assert(foo.textContent === "foo" && foo.getAttribute("title").includes("<img"), "foo's row must show its segment and keep its title as a tooltip");
  assert(nav.querySelectorAll("img, script").length === 0 && window.__pwned === undefined, "a hostile title became live markup");
});

checkAsync("explorer: type chips filter by rank (OR), with the name filter (AND), keeping ancestors", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const idx = window.OKF_INDEX;
  const chips = Array.from(doc.querySelectorAll("#okf-explorer .okf-type-chips button.okf-chip"));
  const group = doc.querySelector("#okf-explorer .okf-type-chips");
  assert(group.getAttribute("role") === "group" && group.getAttribute("aria-label") === "Filter by type", "the chips are not a named group");
  const ranked = idx.types.filter((t) => t.slot < 5);
  const others = idx.types.filter((t) => t.slot === 5);
  const labels = ranked.map((t) => (t.name === "" ? "(no type)" : t.name)).concat(others.length ? ["Other types"] : []);
  const counts = ranked.map((t) => t.count).concat(others.length ? [others.reduce((n, t) => n + t.count, 0)] : []);
  assert(JSON.stringify(chips.map((c) => c.querySelector(".okf-chip-text").textContent)) === JSON.stringify(labels), `chips: ${chips.map((c) => c.textContent).join(" | ")}`);
  assert(JSON.stringify(chips.map((c) => Number(c.querySelector(".okf-chip-count").textContent))) === JSON.stringify(counts), "chip counts");
  for (const chip of chips) {
    const title = chip.querySelector("svg.okf-glyph > title");
    assert(title && title.textContent === chip.querySelector(".okf-chip-text").textContent, "a chip glyph lacks its <title> (X11)");
    assert(chip.getAttribute("aria-pressed") === "false", "a chip starts pressed");
  }
  const links = Array.from(doc.querySelectorAll("#okf-explorer a.okf-tree-link"));
  const filter = doc.getElementById("okf-tree-filter");
  const expectVisible = (pressed, q) => {
    const wants = new Map();
    for (const a of links) {
      const pos = idx.concepts.findIndex((c) => c.id === a.getAttribute("data-okf-id"));
      const c = idx.concepts[pos];
      const label = (c.title + " " + c.id).replace(/\s+/g, " ").trim().toLowerCase();
      wants.set(a, (pressed.size === 0 || pressed.has(indexSlot(idx, pos))) && (q === "" || label.includes(q)));
    }
    for (const a of links) {
      const below = a.closest("li").querySelector("ul");
      const descendantWanted = below ? Array.from(below.querySelectorAll("a.okf-tree-link")).some((d) => wants.get(d)) : false;
      if (wants.get(a)) { assert(isShown(a), `${a.getAttribute("data-okf-id")} matches but is hidden (${JSON.stringify([...pressed])}, "${q}")`); }
      if (!wants.get(a) && !descendantWanted) { assert(!isShown(a), `${a.getAttribute("data-okf-id")} matches nothing but is shown (${JSON.stringify([...pressed])}, "${q}")`); }
    }
  };
  const metric = chips.find((c) => Number(c.getAttribute("data-okf-slot")) === indexSlot(idx, idx.concepts.findIndex((c2) => c2.id === "p11-types/m1")));
  const other = chips.find((c) => c.getAttribute("data-okf-slot") === "5");
  assert(metric && other, "this case needs the Metric chip and the Other types chip");
  metric.click();
  assert(metric.getAttribute("aria-pressed") === "true", "a pressed chip is not announced");
  expectVisible(new Set([Number(metric.getAttribute("data-okf-slot"))]), "");
  other.click();
  expectVisible(new Set([Number(metric.getAttribute("data-okf-slot")), 5]), "");
  type(window, filter, "one");
  expectVisible(new Set([Number(metric.getAttribute("data-okf-slot")), 5]), "one");
  metric.click();
  other.click();
  type(window, filter, "");
  // No expectVisible(new Set(), "") here (F3): with every filter cleared the tree is collapsed by default, so "matches but is hidden" would fail on folded rows; the next assertion checks the cleared state instead.
  assert(links.every((a) => isShown(a) || a.closest("ul.okf-tree-children[hidden]")), "clearing every filter left a concept hidden");
});

checkAsync("explorer: Filters counts pressed chips and a non-empty filter, names the count, and focuses the field", async () => {
  const window = await openPage("foo.html");
  const doc = window.document;
  const button = doc.getElementById("okf-filters-toggle");
  assert(button && button.parentElement.id === "okf-tools" && button.nextElementSibling === doc.getElementById("okf-global-graph"), "Filters is not right before Global graph");
  const badge = button.querySelector(".okf-filters-count");
  const spoken = () => Array.from(button.childNodes).filter((n) => !(n.nodeType === 1 && n.getAttribute("aria-hidden") === "true")).map((n) => n.textContent).join("");
  assert(badge.getAttribute("aria-hidden") === "true" && badge.hidden && spoken() === "Filters", `at rest: badge hidden ${badge.hidden}, name "${spoken()}"`);
  const chip = doc.querySelector("#okf-explorer .okf-type-chips button.okf-chip");
  chip.click();
  assert(!badge.hidden && badge.textContent === "1" && spoken() === "Filters, 1 active", `one chip: "${badge.textContent}", "${spoken()}"`);
  type(window, doc.getElementById("okf-tree-filter"), "x");
  assert(badge.textContent === "2" && spoken() === "Filters, 2 active", `chip and field: "${badge.textContent}"`);
  type(window, doc.getElementById("okf-tree-filter"), "  ");
  assert(badge.textContent === "1", "a blank field counts as a filter");
  chip.click();
  assert(badge.hidden && spoken() === "Filters", "the count did not go back to zero");
  doc.body.focus();
  button.click();
  assert(doc.activeElement === doc.getElementById("okf-tree-filter"), "Filters does not move the focus to the field");
});

checkAsync("explorer: the legend lists human-reviewed, machine-confirmed and stale", async () => {
  const window = await openPage("index.html");
  const items = Array.from(window.document.querySelectorAll("#okf-explorer .okf-explorer-foot > ul.okf-legend > li"));
  assert(JSON.stringify(items.map((li) => li.textContent)) === JSON.stringify(["human-reviewed", "machine-confirmed", "stale (now " + String.fromCharCode(0x2265) + " stale_after)"]),
    `legend: ${JSON.stringify(items.map((li) => li.textContent))}`);
  assert(items[0].querySelector(".okf-trust-human") && items[1].querySelector(".okf-trust-machine") && items[2].querySelector(".okf-stale-mark"), "a legend entry lost its glyph");
});

checkAsync("explorer: the real explorer keeps its anchored styles", async () => {
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const chrome = [
    [doc.querySelector("#okf-explorer .okf-tree-row"), "height", "30px"],
    [doc.querySelector("#okf-explorer .okf-tree-count"), "font-size", "11px"],
    [doc.querySelector("#okf-explorer .okf-tree-folder"), "font-weight", "600"],
    [doc.querySelector("#okf-explorer .okf-type-chips .okf-chip"), "height", "26px"],
    [doc.getElementById("okf-tree-filter"), "height", "34px"],
    [doc.querySelector("#okf-explorer .okf-tree-toggle"), "width", "12px"],
    [doc.querySelector("#okf-tools #okf-filters-toggle"), "height", "34px"],
  ];
  for (const [el, prop, value] of chrome) {
    assert(el, `an explorer element this case needs is missing (${prop}: ${value})`);
    const got = window.getComputedStyle(el).getPropertyValue(prop);
    assert(got === value, `the real <${el.tagName.toLowerCase()} class="${el.getAttribute("class")}"> lost its ${prop}: ${value} (got ${got})`);
  }
});
```

- [ ] **Step 2: Run the harness to verify the new and updated cases fail**

```bash
cd tools/viewer-security-check && npm test
```

Expected: FAIL for the updated P1 explorer cases (`treeLink` finds nothing: no `data-okf-id` yet) and for the seven new Task 13 cases; the others `ok`.

- [ ] **Step 3: Rewrite `okf-explorer.js`**

Replace the whole content of `src/OKF4net.Viewer/Assets/okf-explorer.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The tree explorer (spec §3.2, §8, §11.2). Reads the site index through
// OkfSite.readIndex and draws every shape through OkfShapes; without either
// the explorer stays hidden and the page still works. Bundle text reaches
// the DOM through textContent and fixed-name attributes only (spec §3.4).
// Rows are labelled by the last id segment (A22), the title is a tooltip; the
// type chips filter the tree (OR between chips, AND with the name field), and
// the header's Filters button counts both (H8).
(function () {
  "use strict";
  var site = window.OkfSite;
  var shapes = window.OkfShapes;
  var nav = document.getElementById("okf-explorer");
  if (!site || !shapes || !nav) { return; }
  var index = site.readIndex(window);
  if (!index) { return; }

  var root = site.rootOf(document);
  var currentId = document.documentElement.getAttribute("data-okf-concept");
  var current = -1;
  for (var i = 0; i < index.concepts.length; i++) {
    if (index.concepts[i].id === currentId) { current = i; break; }
  }

  var staleMarks = [];
  var pressed = new Set();   // slots of the pressed type chips
  var filterCount = null;    // the Filters button's visible count
  var filterSpoken = null;   // its visually hidden ", N active"

  function el(tag, className, text) {
    return site.element(document, tag, className, text);
  }

  // E10: a trust flag (not for unverified) and a stale flag (shown only while
  // stale), both from OkfShapes, each with its visually hidden text.
  function appendFlags(row, concept) {
    var kind = shapes.trustKind(concept.trust);
    if (kind) {
      var trust = el("span", "okf-flag okf-flag-trust");
      trust.setAttribute("title", concept.trust);
      trust.appendChild(shapes.icon(kind, "flag"));
      trust.appendChild(el("span", "okf-sr", concept.trust));
      row.appendChild(trust);
    }
    if (typeof concept.staleAfterMs === "number") {
      var stale = el("span", "okf-flag okf-flag-stale");
      stale.setAttribute("title", "stale after " + (concept.staleAfterDate || ""));
      stale.appendChild(shapes.icon("stale", "flag"));
      stale.appendChild(el("span", "okf-sr", "stale"));
      staleMarks.push({ el: stale, ms: concept.staleAfterMs });
      row.appendChild(stale);
    }
  }

  function setExpanded(rec, open) {
    rec.ul.hidden = !open;
    rec.toggle.setAttribute("aria-expanded", open ? "true" : "false");
  }

  // E9: the concepts (destinations) below a node, the node itself excluded.
  function destinationsBelow(node) {
    var n = 0;
    for (var k = 0; k < node.children.length; k++) {
      if (node.children[k].concept >= 0) { n++; }
      n += destinationsBelow(node.children[k]);
    }
    return n;
  }

  // A node can be BOTH a destination (the concept at this path) and a folder
  // (children): foo.md and foo/bar.md coexist legally, so opening (the link)
  // and expanding (the chevron) are separate commands (spec §3.2); such a row
  // has two 12 px slots, chevron then glyph (E6).
  function build(node, depth) {
    var rec = { li: document.createElement("li"), ul: null, toggle: null, label: "", concept: node.concept, slot: -1, children: [], holdsCurrent: false, defaultOpen: false };
    var row = el("div", "okf-tree-row");
    row.style.paddingLeft = (12 + 18 * depth) + "px";

    if (node.children.length > 0) {
      var toggle = el("button", "okf-tree-toggle");
      toggle.type = "button";
      toggle.appendChild(el("span", "okf-sr", "Expand or collapse " + node.name));
      toggle.addEventListener("click", function () { setExpanded(rec, rec.ul.hidden); });
      rec.toggle = toggle;
      row.appendChild(toggle);
    }

    var concept = node.concept >= 0 ? index.concepts[node.concept] : null;
    if (concept) {
      rec.slot = shapes.slotOf(index, node.concept);
      var glyph = el("span", "okf-glyph-slot");
      glyph.appendChild(shapes.icon(shapes.KINDS[rec.slot], "icon", shapes.typeLabel(concept.type)));
      row.appendChild(glyph);
      var link = el("a", "okf-tree-link", node.name);
      link.setAttribute("href", site.resolve(root, concept.path));
      link.setAttribute("title", concept.title);
      link.setAttribute("data-okf-id", concept.id);
      if (node.concept === current) {
        link.setAttribute("aria-current", "page");
        row.classList.add("okf-tree-current");
        rec.holdsCurrent = true;
      }
      row.appendChild(link);
      // The name filter still searches the title and the id.
      rec.label = site.normalize(concept.title + " " + concept.id);
    } else {
      row.appendChild(el("span", "okf-tree-folder", node.name));
      rec.label = site.normalize(node.name);
    }
    if (node.children.length > 0) { row.appendChild(el("span", "okf-tree-count", String(destinationsBelow(node)))); }
    if (concept) { appendFlags(row, concept); }
    rec.li.appendChild(row);

    if (node.children.length > 0) {
      rec.ul = el("ul", "okf-tree-children");
      var below = false;
      for (var k = 0; k < node.children.length; k++) {
        var child = build(node.children[k], depth + 1);
        rec.children.push(child);
        rec.ul.appendChild(child.li);
        if (child.holdsCurrent) { below = true; }
      }
      rec.li.appendChild(rec.ul);
      // Only the path down to the current page starts open.
      rec.defaultOpen = below;
      if (below) { rec.holdsCurrent = true; }
      setExpanded(rec, rec.defaultOpen);
    }
    return rec;
  }

  // A concept matches when its title or id holds the query AND (no chip is
  // pressed OR its slot's chip is); a folder without a concept matches the
  // query by name only while no chip is pressed. Matches and their ancestors
  // stay visible, ancestors of a match open; no filter at all restores the
  // default state.
  function applyFilter(rec, q) {
    var filtering = q !== "" || pressed.size > 0;
    var childVisible = false;
    for (var k = 0; k < rec.children.length; k++) {
      if (applyFilter(rec.children[k], q)) { childVisible = true; }
    }
    var self;
    if (!filtering) {
      self = true;
    } else if (rec.concept >= 0) {
      self = (q === "" || rec.label.indexOf(q) !== -1) && (pressed.size === 0 || pressed.has(rec.slot));
    } else {
      self = pressed.size === 0 && rec.label.indexOf(q) !== -1;
    }
    var visible = self || childVisible;
    rec.li.hidden = !visible;
    if (rec.ul) { setExpanded(rec, filtering ? childVisible : rec.defaultOpen); }
    return visible;
  }

  // H8: pressed chips, plus one when the name field holds a query.
  function updateFiltersCount(q) {
    if (!filterCount) { return; }
    var n = pressed.size + (q !== "" ? 1 : 0);
    filterCount.textContent = String(n);
    filterCount.hidden = n === 0;
    filterSpoken.textContent = n === 0 ? "" : ", " + n + " active";
  }

  function refilter() {
    var q = site.normalize(filter.value);
    for (var k = 0; k < tops.length; k++) { applyFilter(tops[k], q); }
    updateFiltersCount(q);
  }

  // E4: one toggle chip per type of rank 0 to 4, plus "Other types"; the
  // glyph doubles as the page's legend of type shapes, titled (X11).
  function typeChip(entry) {
    var chip = el("button", "okf-chip");
    chip.type = "button";
    chip.setAttribute("aria-pressed", "false");
    chip.setAttribute("data-okf-slot", String(entry.slot));
    chip.appendChild(shapes.icon(shapes.KINDS[entry.slot], "icon", entry.label));
    chip.appendChild(el("span", "okf-chip-text", entry.label));
    chip.appendChild(el("span", "okf-chip-count", String(entry.count)));
    chip.addEventListener("click", function () {
      var on = !pressed.has(entry.slot);
      if (on) { pressed.add(entry.slot); } else { pressed.delete(entry.slot); }
      chip.setAttribute("aria-pressed", on ? "true" : "false");
      refilter();
    });
    return chip;
  }

  function refreshStale() {
    var now = Date.now();
    for (var k = 0; k < staleMarks.length; k++) {
      staleMarks[k].el.hidden = !site.isStale(staleMarks[k].ms, now);
    }
  }

  var head = el("div", "okf-explorer-head");
  // The <nav> is already named "Explorer": the visible title is text (E2).
  var title = el("p", "okf-section-title okf-explorer-title", "Explorer");
  title.setAttribute("aria-hidden", "true");
  var filter = document.createElement("input");
  filter.type = "search";
  filter.id = "okf-tree-filter";
  filter.setAttribute("aria-label", "Filter by name");
  filter.setAttribute("placeholder", "Filter by name" + String.fromCharCode(0x2026));
  filter.setAttribute("autocomplete", "off");
  head.appendChild(title);
  head.appendChild(filter);
  var entries = shapes.typeLegendEntries(index);
  if (entries.length > 0) {
    var chips = el("div", "okf-type-chips");
    chips.setAttribute("role", "group");
    chips.setAttribute("aria-label", "Filter by type");
    for (var e = 0; e < entries.length; e++) { chips.appendChild(typeChip(entries[e])); }
    head.appendChild(chips);
  }

  var list = el("ul", "okf-tree");
  var tops = [];
  for (var t = 0; t < index.tree.length; t++) {
    var rec = build(index.tree[t], 0);
    tops.push(rec);
    list.appendChild(rec.li);
  }

  // E12: the legend of the row flags.
  var foot = el("div", "okf-explorer-foot");
  foot.appendChild(shapes.legend([{ role: "trust", trust: "human-reviewed" }, { role: "trust", trust: "machine-confirmed" }, { role: "stale" }]));

  nav.appendChild(head);
  nav.appendChild(list);
  nav.appendChild(foot);
  nav.hidden = false;

  // H8: "Filters" in the header, right before "Global graph"; it moves the
  // focus to the name field (the browser scrolls it into view, stacked
  // layout included).
  var tools = document.getElementById("okf-tools");
  if (tools) {
    var filtersButton = el("button", "okf-tool");
    filtersButton.type = "button";
    filtersButton.id = "okf-filters-toggle";
    filtersButton.appendChild(document.createTextNode("Filters"));
    filterSpoken = el("span", "okf-sr", "");
    filtersButton.appendChild(filterSpoken);
    filterCount = el("span", "okf-filters-count", "0");
    filterCount.setAttribute("aria-hidden", "true");
    filterCount.hidden = true;
    filtersButton.appendChild(filterCount);
    filtersButton.addEventListener("click", function () { filter.focus(); });
    var graphLink = document.getElementById("okf-global-graph");
    tools.insertBefore(filtersButton, graphLink && graphLink.parentNode === tools ? graphLink : null);
  }

  // On the desktop layout the explorer is its own scroll container (sticky,
  // overflow-y: auto): bring the current entry into its view, about a third
  // from the top, by scrolling the explorer alone. Never the page: in the
  // stacked layout the explorer is not a scroll container and nothing moves.
  function revealCurrent() {
    var link = nav.querySelector('a.okf-tree-link[aria-current="page"]');
    if (!link || nav.scrollHeight <= nav.clientHeight) { return; }
    var overflow = window.getComputedStyle(nav).overflowY;
    if (overflow !== "auto" && overflow !== "scroll") { return; }
    var view = nav.getBoundingClientRect();
    var entry = link.getBoundingClientRect();
    // The part of the explorer on screen: it may run past the window bottom.
    var visible = Math.min(nav.clientHeight, window.innerHeight - Math.max(0, view.top));
    if (!(visible > 0)) { visible = nav.clientHeight; }
    var top = entry.top - view.top;
    if (top >= 0 && top + entry.height <= visible) { return; }
    nav.scrollTop = Math.max(0, nav.scrollTop + top - Math.round(visible / 3));
  }
  revealCurrent();

  filter.addEventListener("input", refilter);

  refreshStale();
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState === "visible") { refreshStale(); }
  });
})();
```

- [ ] **Step 4: Replace the explorer CSS**

In `viewer.css`, delete the P1 explorer rules — every rule from `#okf-explorer .okf-explorer-label {` through `#okf-explorer .okf-stale { border-radius: 0; width: 9px; background: #b4540a; clip-path: polygon(50% 0, 100% 100%, 0 100%); }` (the label, `#okf-tree-filter`, tree, row, toggle, spacer, link, folder, badge, trust and stale rules). Under `/* --- Task 13: explorer (E1-E13) and Filters (H8) --- */`, insert:

```css
/* E2: the head (sticky from 1100 px, Task 3) and its section title. */
#okf-explorer .okf-explorer-head { padding: 14px 16px 10px; }
#okf-explorer .okf-explorer-title { margin: 0 0 6px; }
/* E3 */
#okf-tree-filter {
  display: block; width: 100%; height: 34px; padding: 0 10px; box-sizing: border-box;
  font-family: var(--body); font-size: 13.5px; border: 1px solid var(--hair); border-radius: 0;
  background: var(--white); color: var(--ink);
}
/* E4: the type chips are the shared .okf-chip toggles (Task 3). */
#okf-explorer .okf-type-chips { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 10px; }
/* E5 */
#okf-explorer .okf-tree, #okf-explorer .okf-tree-children { list-style: none; margin: 0; padding: 0; }
#okf-explorer .okf-tree { border-top: 1px solid var(--hair); padding: 6px 0; }
/* E6: 30 px rows; okf-explorer.js sets the left padding, 12 + 18 per level,
   so the active row's band spans the whole panel. */
#okf-explorer .okf-tree-row {
  display: flex; align-items: center; gap: 9px; height: 30px; box-sizing: border-box;
  padding-right: 14px; border-left: 3px solid transparent; font-size: 13.5px;
}
/* E11 */
#okf-explorer .okf-tree-row.okf-tree-current { background: var(--blue-soft); border-left-color: var(--blue); }
/* E7: a 6 px CSS chevron in a 12 px wide, row-high button (WCAG 2.5.8 by
   the spacing exception: the row gap is 9). */
#okf-explorer .okf-tree-toggle { position: relative; flex: none; width: 12px; height: 30px; padding: 0; border: 0; background: none; cursor: pointer; }
#okf-explorer .okf-tree-toggle::before {
  content: ""; position: absolute; left: 3px; top: 12px; width: 6px; height: 6px; box-sizing: border-box;
  border-right: 1.5px solid var(--gray); border-bottom: 1.5px solid var(--gray); transform: rotate(-45deg);
}
#okf-explorer .okf-tree-toggle[aria-expanded="true"]::before { transform: rotate(45deg) translate(-1px, -1px); }
#okf-explorer .okf-glyph-slot { flex: none; display: flex; align-items: center; justify-content: center; width: 12px; height: 12px; }
/* E8: concept 400 ink, folder 600 ink, current 600 blue; one line, ellipsis. */
#okf-explorer :is(.okf-tree-link, .okf-tree-folder) {
  flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
  color: var(--ink); text-decoration: none;
}
#okf-explorer .okf-tree-folder { font-weight: 600; }
#okf-explorer .okf-tree-link:hover { text-decoration: underline; }
#okf-explorer .okf-tree-link[aria-current="page"] { color: var(--blue); font-weight: 600; }
/* E9 */
#okf-explorer .okf-tree-count { flex: none; font-family: var(--mono); font-size: 11px; color: var(--gray); }
/* E10: flags drawn by OkfShapes (flag context, 10 px box). */
#okf-explorer .okf-flag { flex: none; display: inline-flex; align-items: center; justify-content: center; width: 10px; height: 10px; }
#okf-explorer .okf-flag[hidden] { display: none; }
/* E12: the legend, under a rule (sticky at the bottom from 1100 px, Task 3). */
#okf-explorer .okf-explorer-foot { border-top: 1px solid var(--hair); padding: 12px 16px; }
/* H8: the count badge, hidden at zero. */
#okf-tools #okf-filters-toggle .okf-filters-count {
  font-family: var(--mono); font-size: 11px; line-height: 1.4; padding: 1px 6px; background: var(--blue); color: var(--white);
}
#okf-tools #okf-filters-toggle .okf-filters-count[hidden] { display: none; }
```

- [ ] **Step 5: Run the harness**

```bash
cd tools/viewer-security-check && npm test
```

Expected: the seven Task 13 cases `ok`; the updated P1 explorer, staleness, filter-label and hostile-title cases `ok`; `the real P1 chrome keeps its anchored styles` `ok` with the `.okf-flag-trust` probe; the scroll probes (`explorer: on the desktop layout…`, `…already inside the visible part…`, `…without its own scroll container…`) still `ok`; `N passed, 0 failed`.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-explorer.js src/OKF4net.Viewer/Assets/viewer.css tools/viewer-security-check/run.js
git commit -m "feat(viewer): explorer rows by segment with type glyphs, counts and OkfShapes flags; type chips, legend and Filters

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Palette as mockup C draws it (H7, J1–J6)

Wave 4. The opener becomes C's wide button with its shortcut hint; the dialog gets C's input row (search glyph, field, "Esc" key that is the Close button), the "Matches in title, id, tags · N" line, type glyphs and hidden type names in options, the active title in blue, and the footer. The ranking, keyboard, modal and shortcut behaviour of P1 are unchanged (J8).

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/okf-palette.js` (whole file)
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` — remove the P1 palette rules, from `body > .okf-palette-backdrop {` through `body > .okf-palette-backdrop .okf-palette:focus { outline: none; }`; under `/* --- Task 14: palette (H7, J1-J6) --- */`
- Modify: `tools/viewer-security-check/run.js` — the end of the P1 case `long unbroken titles and ids may wrap in the palette and the contents`; under `// --- Task 14: palette ---`. Not the P1 case `explorer, palette and contents render hostile titles as inert text`: Task 11 already made its selector accept the options' `svg.okf-glyph` (`img, script, iframe, object, svg:not(.okf-glyph)`, F2), so this task leaves that case untouched.

**Interfaces:**
- Consumes: `OkfSite` (`rank`, `normalize`, `resolve`, `rootOf`, `readIndex`, `element`), `OkfShapes` (`icon`, `kindOf`, `typeLabel`), index v2, `#okf-tools`.
- Produces: `button.okf-tool.okf-palette-open` (`span.okf-palette-label` "Jump to a concept…", `span.okf-palette-hint[aria-hidden]` "Ctrl K · /"); `body > div.okf-palette-backdrop > div.okf-palette[role=dialog]` holding `h2#okf-palette-title.okf-sr`, `div.okf-palette-head` (`svg.okf-palette-search`, `input#okf-palette-input`, `button.okf-palette-close[aria-label="Close"]` "Esc"), `p.okf-section-title.okf-palette-matches[aria-hidden]`, `ul#okf-palette-list`, `p#okf-palette-status.okf-palette-status.okf-sr[role=status]`, `div.okf-palette-foot`; options `li.okf-palette-option[role=option]` = `svg.okf-glyph` + `span.okf-sr.okf-palette-type` + `span.okf-palette-text` (`span.okf-palette-title`, `span.okf-palette-id`). The `okf:navigate` event is unchanged.

- [ ] **Step 1: Update the P1 wrapping case and write the new cases**

In `run.js`, in `long unbroken titles and ids may wrap in the palette and the contents`, replace

```js
  // The option wraps: the id shares the title's line only when both fit whole,
  // else it goes under the title. No cap or shrink factor on the id can promise
  // that (a title just under its line still broke when the id's cap bound it).
  const wrap = window.getComputedStyle(option).getPropertyValue("flex-wrap");
  assert(wrap === "wrap", `the palette option does not wrap its id under a title that leaves it no room (flex-wrap: ${wrap})`);
```

with

```js
  // The option's text wraps: the id shares the title's line only when both fit
  // whole, else it goes under the title. No cap or shrink factor on the id can
  // promise that (a title just under its line still broke when the id's cap
  // bound it). P1.1 put the glyph beside that text, so the rule moved to it.
  const text = option.querySelector(".okf-palette-text");
  assert(text, "the palette option lost its text block");
  const wrap = window.getComputedStyle(text).getPropertyValue("flex-wrap");
  assert(wrap === "wrap", `the palette option's text does not wrap its id under a title that leaves it no room (flex-wrap: ${wrap})`);
```

Under `// --- Task 14: palette ---`, insert:

```js
checkAsync("palette: the opener is drawn as C draws it and keeps its shortcuts", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const opener = doc.querySelector("#okf-tools .okf-palette-open");
  assert(opener && doc.getElementById("okf-tools").firstElementChild === opener, "the opener is not the first tool");
  const label = opener.querySelector(".okf-palette-label");
  const hint = opener.querySelector(".okf-palette-hint");
  assert(label && label.textContent === "Jump to a concept" + String.fromCharCode(0x2026), `label: ${label && label.textContent}`);
  assert(hint && hint.getAttribute("aria-hidden") === "true" && hint.textContent === "Ctrl K " + String.fromCharCode(0xB7) + " /", `hint: ${hint && hint.textContent}`);
  assert(opener.getAttribute("aria-keyshortcuts") === "Control+K /" && opener.getAttribute("aria-haspopup") === "dialog", "the opener lost its ARIA");
  const style = window.getComputedStyle(opener);
  assert(style.getPropertyValue("width") === "320px" && style.getPropertyValue("min-width") === "160px" && style.getPropertyValue("height") === "34px",
    `opener: width ${style.getPropertyValue("width")}, min-width ${style.getPropertyValue("min-width")}, height ${style.getPropertyValue("height")}`);
});

checkAsync("palette: the Esc key is the Close button, second tab stop, named Close", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const head = doc.querySelector("body > .okf-palette-backdrop .okf-palette-head");
  const input = doc.getElementById("okf-palette-input");
  const close = doc.querySelector(".okf-palette-close");
  assert(head && head.contains(input) && head.contains(close) && input.compareDocumentPosition(close) & window.Node.DOCUMENT_POSITION_FOLLOWING,
    "the input row is not search glyph, field, Esc");
  assert(close.textContent === "Esc" && close.getAttribute("aria-label") === "Close" && close.getAttribute("type") === "button", `close: "${close.textContent}" named "${close.getAttribute("aria-label")}"`);
  const search = head.querySelector("svg.okf-palette-search");
  assert(search && search.getAttribute("aria-hidden") === "true" && head.firstElementChild === search, "the search glyph is missing or not first");
  for (const el of [search, ...search.querySelectorAll("*")]) {
    assert(["svg", "circle", "path"].includes(el.localName), `the search glyph holds a <${el.localName}>`);
    for (const a of Array.from(el.attributes)) {
      assert(["class", "width", "height", "viewBox", "aria-hidden", "focusable", "cx", "cy", "r", "d", "stroke-width"].includes(a.name), `the search glyph carries ${a.name}`);
    }
  }
  key(window, input, { key: "Tab" });
  assert(doc.activeElement === close, "Tab from the field does not reach Esc");
  close.click();
  assert(doc.querySelector(".okf-palette-backdrop").hidden, "Esc does not close");
});

checkAsync("palette: the matches line counts results and the status region is visually hidden", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  const matches = doc.querySelector("body > .okf-palette-backdrop .okf-palette-matches");
  const status = doc.getElementById("okf-palette-status");
  assert(matches && matches.getAttribute("aria-hidden") === "true" && matches.textContent === "", `matches at rest: "${matches && matches.textContent}"`);
  assert(status.classList.contains("okf-sr") && status.getAttribute("role") === "status", "the status region is not visually hidden");
  type(window, input, "o");
  const n = paletteOptions(window).length;
  assert(matches.textContent === "Matches in title, id, tags " + String.fromCharCode(0xB7) + " " + n, `matches: "${matches.textContent}"`);
  assert(status.textContent === `${n} matching concepts`, `status: "${status.textContent}"`);
  type(window, input, "");
  assert(matches.textContent === "", "the matches line kept a count for an empty query");
  const foot = Array.from(doc.querySelectorAll("body > .okf-palette-backdrop .okf-palette-foot > span"), (s) => s.textContent);
  assert(JSON.stringify(foot) === JSON.stringify(["Up / Down to move", "Enter to open"]), `foot: ${JSON.stringify(foot)}`);
});

checkAsync("palette: each option has its type glyph and type name as hidden text", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const idx = window.OKF_INDEX;
  doc.querySelector(".okf-palette-open").click();
  type(window, doc.getElementById("okf-palette-input"), "p11-types");
  const options = paletteOptions(window);
  assert(options.length >= 5, `this case needs the p11-types concepts (${options.length} options)`);
  for (const option of options) {
    const pos = idx.concepts.findIndex((c) => c.id === optionId(option));
    const t = idx.types[idx.concepts[pos].typeIndex];
    const glyph = option.firstElementChild;
    assert(glyph.localName === "svg" && glyph.classList.contains("okf-glyph") && glyph.querySelector(`.okf-shape-${t.slot}`), `${optionId(option)}: glyph not of slot ${t.slot}`);
    const typeName = option.querySelector(".okf-palette-type");
    assert(typeName && typeName.classList.contains("okf-sr") && typeName.textContent === (t.name === "" ? "(no type)" : t.name), `${optionId(option)}: hidden type "${typeName && typeName.textContent}"`);
    assert(option.querySelector(".okf-palette-text > .okf-palette-title") && option.querySelector(".okf-palette-text > .okf-palette-id"), "the title and id are not in the text block");
  }
});

checkAsync("palette: the real palette keeps its anchored styles", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  type(window, doc.getElementById("okf-palette-input"), "o");
  const chrome = [
    [doc.querySelector("body > .okf-palette-backdrop"), "position", "fixed"],
    [doc.querySelector("body > .okf-palette-backdrop .okf-palette"), "max-width", "600px"],
    [doc.querySelector("body > .okf-palette-backdrop .okf-palette-head"), "height", "54px"],
    [doc.querySelector("body > .okf-palette-backdrop .okf-palette-input"), "font-size", "17px"],
    [doc.querySelector("body > .okf-palette-backdrop .okf-palette-close"), "font-size", "11px"],
    [doc.querySelector("body > .okf-palette-backdrop .okf-palette-option"), "min-height", "46px"],
    [doc.querySelector('body > .okf-palette-backdrop .okf-palette-option[aria-selected="true"] .okf-palette-title'), "font-weight", "600"],
    [doc.querySelector("body > .okf-palette-backdrop .okf-palette-matches"), "text-transform", "uppercase"],
  ];
  for (const [el, prop, value] of chrome) {
    assert(el, `a palette element this case needs is missing (${prop}: ${value})`);
    const got = window.getComputedStyle(el).getPropertyValue(prop);
    assert(got === value, `the real <${el.tagName.toLowerCase()} class="${el.getAttribute("class")}"> lost its ${prop}: ${value} (got ${got})`);
  }
});
```

- [ ] **Step 2: Run the harness to verify the cases fail**

```bash
cd tools/viewer-security-check && npm test
```

Expected: FAIL for the five Task 14 cases and for `long unbroken titles and ids may wrap…` (`the palette option lost its text block`); every P1 palette behaviour case still `ok`.

- [ ] **Step 3: Rewrite `okf-palette.js`**

Replace the whole content of `src/OKF4net.Viewer/Assets/okf-palette.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The "Jump to" palette (spec §4.6, §8, §11.6), drawn as mockup C draws it: a
// modal dialog opened by a visible button, Ctrl+K or "/". Results come from
// OkfSite.rank (fixed tiers, no weights -- not ConceptSearch, and never the
// body text); each option shows its type's glyph from OkfShapes with the type
// name as hidden text. Navigating dispatches a cancelable "okf:navigate"
// event first, so a host -- or the test harness -- can observe or veto it.
(function () {
  "use strict";
  var site = window.OkfSite;
  var shapes = window.OkfShapes;
  var tools = document.getElementById("okf-tools");
  if (!site || !shapes || !tools) { return; }
  var index = site.readIndex(window);
  if (!index) { return; }
  var root = site.rootOf(document);
  var SVG_NS = "http://www.w3.org/2000/svg";
  var ELLIPSIS = String.fromCharCode(0x2026);
  var MIDDLE_DOT = String.fromCharCode(0xB7);

  function el(tag, className, text) {
    return site.element(document, tag, className, text);
  }

  // J2: the search glyph of C, a constant of this module (spec §4.5).
  function searchIcon() {
    var svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("class", "okf-palette-search");
    svg.setAttribute("width", "18");
    svg.setAttribute("height", "18");
    svg.setAttribute("viewBox", "0 0 18 18");
    svg.setAttribute("aria-hidden", "true");
    svg.setAttribute("focusable", "false");
    var circle = document.createElementNS(SVG_NS, "circle");
    circle.setAttribute("cx", "8");
    circle.setAttribute("cy", "8");
    circle.setAttribute("r", "5");
    circle.setAttribute("stroke-width", "1.6");
    var handle = document.createElementNS(SVG_NS, "path");
    handle.setAttribute("d", "M12 12l4 4");
    handle.setAttribute("stroke-width", "1.6");
    svg.appendChild(circle);
    svg.appendChild(handle);
    return svg;
  }

  // H7: the label, then the shortcut hint, hidden from assistive technology
  // (aria-keyshortcuts announces the shortcuts).
  var opener = el("button", "okf-tool okf-palette-open");
  opener.type = "button";
  opener.setAttribute("aria-haspopup", "dialog");
  opener.setAttribute("aria-keyshortcuts", "Control+K /");
  opener.appendChild(el("span", "okf-palette-label", "Jump to a concept" + ELLIPSIS));
  var hint = el("span", "okf-palette-hint", "Ctrl K " + MIDDLE_DOT + " /");
  hint.setAttribute("aria-hidden", "true");
  opener.appendChild(hint);
  tools.insertBefore(opener, tools.firstChild);

  var backdrop = el("div", "okf-palette-backdrop");
  backdrop.hidden = true;
  var dialog = el("div", "okf-palette");
  dialog.setAttribute("role", "dialog");
  dialog.setAttribute("aria-modal", "true");
  dialog.setAttribute("aria-labelledby", "okf-palette-title");
  // Focusable but not a tab stop: a click on the dialog's own text keeps
  // focus inside it instead of dropping it to <body>.
  dialog.tabIndex = -1;
  var title = el("h2", "okf-sr", "Jump to a concept");
  title.id = "okf-palette-title";
  var head = el("div", "okf-palette-head");
  var input = document.createElement("input");
  input.type = "text";
  input.id = "okf-palette-input";
  input.className = "okf-palette-input";
  input.setAttribute("role", "combobox");
  input.setAttribute("aria-expanded", "true");
  input.setAttribute("aria-controls", "okf-palette-list");
  input.setAttribute("aria-autocomplete", "list");
  input.setAttribute("aria-labelledby", "okf-palette-title");
  input.setAttribute("autocomplete", "off");
  // J2: the close button is drawn as an "Esc" key and keeps the name Close.
  var close = el("button", "okf-palette-close", "Esc");
  close.type = "button";
  close.setAttribute("aria-label", "Close");
  head.appendChild(searchIcon());
  head.appendChild(input);
  head.appendChild(close);
  // J3: what sighted readers see; the status region below is what is announced.
  var matches = el("p", "okf-section-title okf-palette-matches");
  matches.setAttribute("aria-hidden", "true");
  var list = el("ul", "okf-palette-list");
  list.id = "okf-palette-list";
  list.setAttribute("role", "listbox");
  list.setAttribute("aria-label", "Matching concepts");
  var status = el("p", "okf-palette-status okf-sr");
  status.id = "okf-palette-status";
  status.setAttribute("role", "status");
  // J6
  var foot = el("div", "okf-palette-foot");
  foot.appendChild(el("span", "", "Up / Down to move"));
  foot.appendChild(el("span", "", "Enter to open"));
  dialog.appendChild(title);
  dialog.appendChild(head);
  dialog.appendChild(matches);
  dialog.appendChild(list);
  dialog.appendChild(status);
  dialog.appendChild(foot);
  backdrop.appendChild(dialog);
  document.body.appendChild(backdrop);

  var shown = [];
  var active = -1;
  // The concept the reader made active with the arrow keys since the palette
  // opened, or -1. Only that choice survives a query change; an option that
  // is active merely because it ranked first earlier does not (spec §8).
  var chosen = -1;
  var returnFocus = null;

  // Marks the active option and keeps it visible. Arrow keys call this alone:
  // the list is not rebuilt (it can hold every concept of a large bundle).
  function sync() {
    if (active >= 0 && list.children[active]) {
      var activeOption = list.children[active];
      activeOption.setAttribute("aria-selected", "true");
      input.setAttribute("aria-activedescendant", activeOption.id);
      // aria-activedescendant does not move DOM focus, so nothing scrolls
      // the list by itself: keep the active option visible.
      if (typeof activeOption.scrollIntoView === "function") {
        activeOption.scrollIntoView({ block: "nearest" });
      }
    } else {
      input.removeAttribute("aria-activedescendant");
    }
  }

  // J4: glyph of the type, the type name as hidden text (the explorer and its
  // legend are behind the modal), then the title and the id.
  function render() {
    while (list.firstChild) { list.removeChild(list.firstChild); }
    for (var k = 0; k < shown.length; k++) {
      var concept = index.concepts[shown[k]];
      var option = el("li", "okf-palette-option");
      option.id = "okf-palette-opt-" + k;
      option.setAttribute("role", "option");
      option.setAttribute("aria-selected", "false");
      option.appendChild(shapes.icon(shapes.kindOf(index, shown[k]), "icon"));
      option.appendChild(el("span", "okf-sr okf-palette-type", shapes.typeLabel(concept.type)));
      var text = el("span", "okf-palette-text");
      text.appendChild(el("span", "okf-palette-title", concept.title));
      text.appendChild(el("span", "okf-palette-id", concept.id));
      option.appendChild(text);
      option.addEventListener("click", activate.bind(null, k));
      list.appendChild(option);
    }
    input.setAttribute("aria-expanded", shown.length > 0 ? "true" : "false");
    sync();
  }

  function update() {
    // Every match is listed and reachable: no cap (owner decision, 2026-10-06).
    shown = site.rank(index, input.value);
    // Keep the concept the reader chose with the arrows when it survives the
    // new query; otherwise the first (best-ranked) option becomes active and
    // the choice is forgotten (spec §8).
    active = chosen === -1 ? -1 : shown.indexOf(chosen);
    if (active === -1) {
      chosen = -1;
      active = shown.length > 0 ? 0 : -1;
    }
    render();
    if (site.normalize(input.value) === "") {
      status.textContent = "";
      matches.textContent = "";
    } else {
      matches.textContent = "Matches in title, id, tags " + MIDDLE_DOT + " " + shown.length;
      if (shown.length === 0) {
        status.textContent = "No matching concept";
      } else {
        status.textContent = shown.length + (shown.length === 1 ? " matching concept" : " matching concepts");
      }
    }
  }

  function move(delta) {
    if (shown.length === 0) { return; }
    list.children[active].setAttribute("aria-selected", "false");
    active = (active + delta + shown.length) % shown.length;
    chosen = shown[active];
    sync();
  }

  function activate(k) {
    if (k < 0 || k >= shown.length) { return; }
    var href = site.resolve(root, index.concepts[shown[k]].path);
    var event = new CustomEvent("okf:navigate", { cancelable: true, detail: { href: href } });
    if (document.dispatchEvent(event)) { window.location.assign(href); }
  }

  function open() {
    if (!backdrop.hidden) { return; }
    returnFocus = document.activeElement;
    input.value = "";
    active = -1;
    chosen = -1;
    update();
    backdrop.hidden = false;
    input.focus();
  }

  function closePalette() {
    backdrop.hidden = true;
    var target = returnFocus && typeof returnFocus.focus === "function" && document.contains(returnFocus) && returnFocus !== document.body
      ? returnFocus
      : opener;
    returnFocus = null;
    target.focus();
  }

  opener.addEventListener("click", open);
  close.addEventListener("click", closePalette);
  backdrop.addEventListener("click", function (e) { if (e.target === backdrop) { closePalette(); } });
  input.addEventListener("input", update);

  function isEditable(target) {
    if (!target || target.nodeType !== 1) { return false; }
    var tag = target.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || target.isContentEditable === true;
  }

  // Ctrl+K and nothing else (spec §8, "sans autre modificateur").
  function isCtrlK(e) {
    return e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && (e.key === "k" || e.key === "K");
  }

  // One listener serves both states, on document, because a click inside the
  // dialog on something unfocusable can leave focus on <body>: the open-state
  // keys must not depend on where focus is.
  //
  // Closed: Ctrl+K and "/" are also browser shortcuts (Chrome, Firefox), so
  // they are taken only outside editable fields, without other modifiers and
  // outside IME composition, and prevented only when taken (spec §8).
  // Open: Ctrl+K again is swallowed (the palette stays open, focus returns to
  // its field) rather than handed to the browser's own Ctrl+K.
  document.addEventListener("keydown", function (e) {
    // Keys that confirm or cancel an IME composition belong to the IME, not
    // to the palette (Enter would navigate, Escape would close).
    if (e.isComposing || e.keyCode === 229) { return; }
    if (!backdrop.hidden) {
      if (e.key === "Escape") {
        e.preventDefault();
        closePalette();
      } else if (e.key === "ArrowDown") {
        e.preventDefault();
        move(1);
      } else if (e.key === "ArrowUp") {
        e.preventDefault();
        move(-1);
      } else if (e.key === "Enter" && e.target !== close) {
        // Enter on Close is that button's own click.
        e.preventDefault();
        activate(active);
      } else if (e.key === "Tab") {
        // Focus stays inside the modal: its only stops are the field and
        // Close (from <body> or the dialog itself, Tab lands on the field).
        e.preventDefault();
        (document.activeElement === input ? close : input).focus();
      } else if (isCtrlK(e)) {
        e.preventDefault();
        input.focus();
      }
      return;
    }
    if (e.defaultPrevented || isEditable(e.target)) { return; }
    var slash = e.key === "/" && !e.ctrlKey && !e.altKey && !e.metaKey;
    if (isCtrlK(e) || slash) {
      e.preventDefault();
      open();
    }
  });
})();
```

- [ ] **Step 4: Replace the palette CSS**

In `viewer.css`, delete the P1 palette rules — every rule from `body > .okf-palette-backdrop {` through `body > .okf-palette-backdrop .okf-palette:focus { outline: none; }`, the `@media (max-width: 480px)` block and the comments among them included. Under `/* --- Task 14: palette (H7, J1-J6) --- */`, insert:

```css
/* H7: 320 wide, shrinking to 160; the shortcut hint goes under 900 px. */
#okf-tools .okf-palette-open {
  flex: 0 1 320px; width: 320px; min-width: 160px; justify-content: space-between;
  padding: 0 12px; color: var(--gray); font-weight: 400;
}
#okf-tools .okf-palette-open .okf-palette-label { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
#okf-tools .okf-palette-open .okf-palette-hint { flex: none; font-family: var(--mono); font-size: 11px; border: 1px solid var(--hair); padding: 1px 6px; }
@media (max-width: 899px) {
  #okf-tools .okf-palette-open .okf-palette-hint { display: none; }
}
/* J1: backdrop, box 120 from the top, 600 wide (at most the window less 32). */
body > .okf-palette-backdrop {
  position: fixed; inset: 0; z-index: 10; display: flex; align-items: flex-start; justify-content: center;
  padding: 120px 16px 16px; background: var(--backdrop);
}
body > .okf-palette-backdrop[hidden] { display: none; }
body > .okf-palette-backdrop .okf-palette {
  width: 100%; max-width: 600px; background: var(--white); color: var(--ink);
  border: 1px solid var(--ink); box-shadow: var(--shadow);
}
body > .okf-palette-backdrop .okf-palette:focus { outline: none; }
/* J2: search glyph, field, Esc. */
body > .okf-palette-backdrop .okf-palette-head {
  display: flex; align-items: center; gap: 12px; height: 54px; padding: 0 16px; border-bottom: 1px solid var(--hair);
}
body > .okf-palette-backdrop .okf-palette-search { flex: none; fill: none; stroke: var(--gray); }
body > .okf-palette-backdrop .okf-palette-input {
  flex: 1; min-width: 0; height: 100%; padding: 0; border: 0; outline: 0; background: transparent;
  font-family: var(--body); font-size: 17px; color: var(--ink);
}
body > .okf-palette-backdrop .okf-palette-close {
  flex: none; padding: 1px 6px; border: 1px solid var(--hair); background: var(--white);
  font-family: var(--mono); font-size: 11px; color: var(--gray); cursor: pointer;
}
/* J3: the shared section title, padded. */
body > .okf-palette-backdrop .okf-palette-matches { margin: 0; padding: 10px 16px 6px; }
body > .okf-palette-backdrop .okf-palette-matches:empty { display: none; }
/* J4, J5 */
body > .okf-palette-backdrop .okf-palette-list { list-style: none; margin: 0; padding: 0 0 8px; max-height: 50vh; overflow-y: auto; }
body > .okf-palette-backdrop .okf-palette-option {
  display: flex; align-items: center; gap: 12px; min-height: 46px; padding: 0 16px;
  cursor: pointer; border-left: 3px solid transparent;
}
body > .okf-palette-backdrop .okf-palette-option[aria-selected="true"] { background: var(--blue-soft); border-left-color: var(--blue); }
body > .okf-palette-backdrop .okf-palette-option[aria-selected="true"] .okf-palette-title { color: var(--blue); font-weight: 600; }
/* R4 (P1 recette), kept on the text block: a long unbroken title or id wraps
   instead of widening the palette (390 px). The title breaks only a word
   longer than its line (break-word leaves its min-content size alone); the
   text wraps, so the id shares the title's line only when both fit whole,
   else it moves under the title; the id may break anywhere. */
body > .okf-palette-backdrop .okf-palette-text {
  flex: 1; min-width: 0; display: flex; flex-wrap: wrap; justify-content: space-between; align-items: baseline;
  gap: 2px 12px; padding: 6px 0;
}
body > .okf-palette-backdrop .okf-palette-title { flex: 1 1 auto; min-width: 0; overflow-wrap: break-word; font-size: 15px; }
body > .okf-palette-backdrop .okf-palette-id {
  flex: 0 1 auto; max-width: 100%; min-width: 0; overflow-wrap: anywhere;
  font-family: var(--mono); font-size: 12px; color: var(--gray);
}
@media (max-width: 480px) {
  body > .okf-palette-backdrop .okf-palette-text { flex-direction: column; flex-wrap: nowrap; align-items: stretch; gap: 2px; }
  body > .okf-palette-backdrop .okf-palette-id { max-width: none; }
}
/* J6 */
body > .okf-palette-backdrop .okf-palette-foot {
  display: flex; gap: 16px; padding: 10px 16px; border-top: 1px solid var(--hair); font-size: 12px; color: var(--gray);
}
```

- [ ] **Step 5: Run the harness**

```bash
cd tools/viewer-security-check && npm test
```

Expected: the five Task 14 cases and the updated `long unbroken titles and ids may wrap…` `ok`; every P1 palette case (ranking, modal focus, Tab and Shift+Tab, Escape, IME, no cap, `okf:navigate` veto, nested root resolution, `Enter on the Close button…`) still `ok`; `N passed, 0 failed`.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-palette.js src/OKF4net.Viewer/Assets/viewer.css tools/viewer-security-check/run.js
git commit -m "feat(viewer): palette drawn as mockup C, with type glyphs and a visible match count; behaviour unchanged

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---
### Task 15: `okf-page.js` — chip glyphs, the stale chip, "Show all", glyphs of "Referenced by" (control 11)

Wave 5. Creates `okf-page.js` and adds it to the script table. It reads only the fixed `data-okf-*` values Task 12 wrote and the index, starts every query from a chrome container, and does nothing outside a concept page.

**Files:**
- Create: `src/OKF4net.Viewer/Assets/okf-page.js`
- Modify: `src/OKF4net.Viewer/HtmlWriter.cs` (one line in `PageScripts`)
- Modify: `tests/OKF4net.Tests/Viewer/HtmlWriterHeaderTests.cs` (`Pages_and_the_index_load_the_script_table_in_order_after_the_payload`)
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` (under `/* --- Task 15: Show all (C6) --- */`)
- Modify: `tools/viewer-security-check/run.js` (under `// --- Task 15: okf-page.js (control 11) ---`)

**Interfaces:**
- Consumes: Task 12's markup (`.okf-page-head`, `.okf-chip-glyph[data-okf-slot|data-okf-trust|data-okf-stale]`, `.okf-chip-text`, `#okf-fm`, `#okf-fm-grid`, `.okf-fm-cell[data-okf-extra]`, `#okf-context .okf-backlinks a.okf-row[data-okf-target]`), `OkfShapes` (Task 6), `OkfSite.readIndex`, `OkfSite.isStale` (P1), index v2.
- Produces: glyphs inside the chip slots; `.okf-chip-stale[data-okf-stale-now]` with text "stale since YYYY-MM-DD" while stale (recomputed on load and on `visibilitychange` to visible); `button.okf-fm-toggle[aria-expanded][aria-controls="okf-fm-grid"]` "Show all" / "Show fewer" toggling `data-okf-expanded` on `#okf-fm`; a titled type glyph first in each "Referenced by" row. `HtmlWriter.PageScripts` = … `"okf-toc.js", "okf-page.js",` then `// P2: local graph`.

- [ ] **Step 1: Write the failing cases**

In `tests/OKF4net.Tests/Viewer/HtmlWriterHeaderTests.cs`, in `Pages_and_the_index_load_the_script_table_in_order_after_the_payload`, replace

```csharp
            new[] { "marked.min.js", "viewer.js", "okf-index.js", "okf-site.js", "okf-shapes.js", "okf-explorer.js", "okf-palette.js", "okf-toc.js" },
            HtmlWriter.PageScripts.Take(8));
```

with

```csharp
            new[] { "marked.min.js", "viewer.js", "okf-index.js", "okf-site.js", "okf-shapes.js", "okf-explorer.js", "okf-palette.js", "okf-toc.js", "okf-page.js" },
            HtmlWriter.PageScripts.Take(9));
```

Under `// --- Task 15: okf-page.js (control 11) ---` in `run.js`, insert:

```js
checkAsync("page: chip glyphs come from OkfShapes and the stale chip flips at the deadline (control 11)", async () => {
  // p11-page.md: stale_after 2000-01-01T00:00:00Z, human-reviewed.
  let now = Date.UTC(1999, 11, 31, 23, 59, 59, 999);
  const window = await openPage("p11-page.html", { now: () => now });
  const doc = window.document;
  const idx = window.OKF_INDEX;
  const head = doc.querySelector("body > .okf-layout > main > .okf-page-head");
  const pos = idx.concepts.findIndex((c) => c.id === "p11-page");
  const slot = idx.types[idx.concepts[pos].typeIndex].slot;
  const typeGlyph = head.querySelector(".okf-chip-type > .okf-chip-glyph > svg.okf-glyph");
  assert(typeGlyph && typeGlyph.getAttribute("width") === "10" && typeGlyph.querySelector(`.okf-shape-${slot}`), "the type chip has no chip-context glyph of its slot");
  assert(head.querySelector(".okf-chip-trust > .okf-chip-glyph > svg.okf-glyph .okf-trust-human"), "the trust chip has no human glyph");
  const chip = head.querySelector(".okf-chip-stale");
  const text = chip.querySelector(".okf-chip-text");
  assert(text.textContent === "stale after 2000-01-01" && !chip.hasAttribute("data-okf-stale-now") && !chip.querySelector("svg"),
    `a millisecond before the deadline: "${text.textContent}"`);
  now = Date.UTC(2000, 0, 1);
  doc.dispatchEvent(new window.Event("visibilitychange"));
  assert(text.textContent === "stale since 2000-01-01" && chip.hasAttribute("data-okf-stale-now") && chip.querySelector("svg .okf-stale-mark"),
    `at the deadline: "${text.textContent}"`);
  now = Date.UTC(1999, 0, 1);
  doc.dispatchEvent(new window.Event("visibilitychange"));
  assert(text.textContent === "stale after 2000-01-01" && !chip.hasAttribute("data-okf-stale-now") && !chip.querySelector("svg"), "the chip does not flip back");
});

checkAsync("page: Show all reveals the folded entries and is announced", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  const box = doc.getElementById("okf-fm");
  const toggle = box.querySelector(".okf-fm-head > button.okf-fm-toggle");
  assert(toggle && toggle.textContent === "Show all" && toggle.getAttribute("aria-expanded") === "false" && toggle.getAttribute("aria-controls") === "okf-fm-grid",
    `toggle: ${toggle && toggle.outerHTML}`);
  const shown = () => Array.from(box.querySelectorAll(".okf-fm-cell")).filter((c) => window.getComputedStyle(c).getPropertyValue("display") !== "none").length;
  const all = box.querySelectorAll(".okf-fm-cell").length;
  assert(shown() === 4 && all > 4, `folded: ${shown()} of ${all} shown, expected the first 4`);
  toggle.click();
  assert(shown() === all && box.hasAttribute("data-okf-expanded") && toggle.getAttribute("aria-expanded") === "true" && toggle.textContent === "Show fewer",
    `expanded: ${shown()} of ${all}, "${toggle.textContent}"`);
  toggle.click();
  assert(shown() === 4 && !box.hasAttribute("data-okf-expanded") && toggle.getAttribute("aria-expanded") === "false" && toggle.textContent === "Show all", "folded again");
  assert(window.getComputedStyle(toggle).getPropertyValue("font-size") === "12.5px", "the real Show all lost its anchored style");
});

checkAsync("page: Referenced by rows get their type glyph from the index", async () => {
  const window = await openPage("p11-page.html");
  const idx = window.OKF_INDEX;
  const row = Array.from(window.document.querySelectorAll("#okf-context .okf-backlinks a.okf-row")).find((a) => a.getAttribute("data-okf-target") === "p11-page-ref");
  const pos = idx.concepts.findIndex((c) => c.id === "p11-page-ref");
  const t = idx.types[idx.concepts[pos].typeIndex];
  const glyph = row && row.firstElementChild;
  assert(glyph && glyph.localName === "svg" && glyph.querySelector(`.okf-shape-${t.slot}`) && glyph.querySelector("title").textContent === t.name,
    `row glyph: ${glyph && glyph.outerHTML}`);
  assert(row.textContent.endsWith("p11-page-ref"), "the row lost its id text");
});

checkAsync("page head: hostile status, verifier, date and frontmatter stay inert text after glyphs and Show all", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  doc.getElementById("okf-fm").querySelector(".okf-fm-head > button.okf-fm-toggle").click();
  const head = doc.querySelector("body > .okf-layout > main > .okf-page-head");
  assert(head.querySelectorAll("img, script, b, i").length === 0, "markup from bundle text became live in the page head");
  for (const text of ["<img src=x onerror=window.__pwned=1>", "<b>2026-07-01</b>", "probe<i>key</i>"]) {
    assert(head.textContent.includes(text), `the page head dropped ${text}`);
  }
  assert(window.__pwned === undefined, "a hostile value executed");
});

checkAsync("page glyphs and Show all leave body code wearing P1.1 chrome classes untouched", async () => {
  const window = await openPage("p11-chrome-classes.html");
  const doc = window.document;
  const body = doc.getElementById("okf-body");
  const codes = Array.from(body.querySelectorAll("code"));
  const before = codes.map((c) => c.textContent);
  const toggle = doc.querySelector("body > .okf-layout > main > .okf-page-head .okf-fm-toggle");
  assert(toggle, "this case needs the fixture's folded entries and their Show all");
  toggle.click();
  assert(JSON.stringify(codes.map((c) => c.textContent)) === JSON.stringify(before), "body code wearing chrome classes was changed");
  for (const c of codes) {
    assert(window.getComputedStyle(c).getPropertyValue("display") !== "none", `<code class="${c.getAttribute("class")}"> was hidden`);
  }
  for (const rel of ["p11-chrome-classes.html", "p11-page.html", "foo.html"]) {
    const page = rel === "p11-chrome-classes.html" ? window : await openPage(rel);
    const b = page.document.getElementById("okf-body");
    assert(b.querySelectorAll("svg").length === 0, `${rel}: an svg reached #okf-body`);
    assert(Array.from(b.querySelectorAll("*")).every((el) => !Array.from(el.attributes).some((a) => a.name.startsWith("data-okf-"))), `${rel}: a data-okf-* attribute reached #okf-body`);
  }
});

checkAsync("page: okf-page.js does nothing on the index, and degrades without OkfShapes or the index", async () => {
  const index = await openPage("index.html");
  assert(index.document.querySelector(".okf-fm-toggle") === null, "okf-page.js acted on the index");
  const noShapes = await openPage("p11-page.html", { blocked: ["assets/okf-shapes.js"] });
  assert(noShapes.document.querySelector(".okf-fm-toggle"), "Show all needs no shapes");
  assert(noShapes.document.querySelector("body > .okf-layout > main > .okf-page-head svg") === null, "a glyph appeared without OkfShapes");
  const noIndex = await openPage("p11-page.html", { blocked: ["assets/okf-index.js"], now: () => Date.UTC(2026, 9, 7) });
  const head = noIndex.document.querySelector("body > .okf-layout > main > .okf-page-head");
  assert(head.querySelector(".okf-chip-type svg") && head.querySelector(".okf-chip-stale .okf-chip-text").textContent === "stale after 2000-01-01",
    "without the index the chip glyphs still come from the C# slots and the stale chip keeps its written text");
});
```

Run `cd tools/viewer-security-check && npm test` and `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlWriterHeaderTests"`.
Expected: the six Task 15 cases `FAIL` (no glyph, no toggle); the xunit test `FAIL` (`okf-page.js` missing from the table).

- [ ] **Step 2: Write `okf-page.js`**

Create `src/OKF4net.Viewer/Assets/okf-page.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The concept page's head (spec §11.3 C5, C6; §11.4 X10; §12.3): places the
// chip glyphs in the empty slots HtmlWriter wrote, flips the stale chip at
// the deadline (§4.4: on load and whenever the page becomes visible again),
// adds "Show all" to the frontmatter box, and puts its type's glyph before
// each "Referenced by" row. It reads only the fixed data-okf-* values the C#
// wrote and the index -- never bundle text; every query starts from a chrome
// container (getElementById or "body > …") and a class selection also
// requires its data-okf-* attribute, so body content wearing these class
// names is never touched (spec §12.6). Loaded on the index too, where it does
// nothing. Each part needs only what it uses: "Show all" works without
// OkfShapes or the index, the chip glyphs without the index.
(function () {
  "use strict";
  if (document.documentElement.getAttribute("data-okf-view") !== "page") { return; }
  var site = window.OkfSite;
  var shapes = window.OkfShapes;
  var head = document.querySelector("body > .okf-layout > main > .okf-page-head");
  if (!site || !head) { return; }

  // C6: "Show all" / "Show fewer", only when some entries are folded.
  var box = document.getElementById("okf-fm");
  var grid = document.getElementById("okf-fm-grid");
  if (box && grid && head.contains(box) && box.contains(grid) && grid.querySelector(".okf-fm-cell[data-okf-extra]")) {
    var toggle = site.element(document, "button", "okf-fm-toggle", "Show all");
    toggle.type = "button";
    toggle.setAttribute("aria-expanded", "false");
    toggle.setAttribute("aria-controls", "okf-fm-grid");
    toggle.addEventListener("click", function () {
      var expand = !box.hasAttribute("data-okf-expanded");
      if (expand) { box.setAttribute("data-okf-expanded", ""); } else { box.removeAttribute("data-okf-expanded"); }
      toggle.setAttribute("aria-expanded", expand ? "true" : "false");
      toggle.textContent = expand ? "Show fewer" : "Show all";
    });
    var boxHead = box.firstElementChild;
    (boxHead && boxHead.classList.contains("okf-fm-head") ? boxHead : box).appendChild(toggle);
  }

  if (!shapes) { return; }

  // C5: the type glyph (chip context, drawn white on the ink chip) and the
  // trust glyph (flag context), from the fixed values the C# wrote.
  var typeSlot = head.querySelector(".okf-chip-type > .okf-chip-glyph[data-okf-slot]");
  if (typeSlot) {
    var slot = typeSlot.getAttribute("data-okf-slot");
    if (/^[0-5]$/.test(slot)) { typeSlot.appendChild(shapes.icon(shapes.KINDS[Number(slot)], "chip")); }
  }
  var trustSlot = head.querySelector(".okf-chip-trust > .okf-chip-glyph[data-okf-trust]");
  if (trustSlot) {
    var trust = trustSlot.getAttribute("data-okf-trust");
    if (trust === "human" || trust === "machine") { trustSlot.appendChild(shapes.icon(trust, "flag")); }
  }

  var index = site.readIndex(window);
  if (!index) { return; }
  var positions = new Map();
  for (var i = 0; i < index.concepts.length; i++) { positions.set(index.concepts[i].id, i); }

  // C5: "stale after D" until the deadline, then "stale since D" with the
  // hourglass (A24); D is the index's staleAfterDate, never rebuilt.
  var staleSlot = head.querySelector(".okf-chip-stale > .okf-chip-glyph[data-okf-stale]");
  var current = positions.get(document.documentElement.getAttribute("data-okf-concept"));
  var staleText = staleSlot ? staleSlot.nextElementSibling : null;
  if (staleSlot && staleText && staleText.classList.contains("okf-chip-text") && current !== undefined) {
    var staleChip = staleSlot.parentElement;
    var concept = index.concepts[current];
    var date = typeof concept.staleAfterDate === "string" ? concept.staleAfterDate : "";
    var refreshStale = function () {
      if (site.isStale(concept.staleAfterMs, Date.now())) {
        if (!staleSlot.firstChild) { staleSlot.appendChild(shapes.icon("stale", "flag")); }
        staleText.textContent = "stale since " + date;
        staleChip.setAttribute("data-okf-stale-now", "");
      } else {
        while (staleSlot.firstChild) { staleSlot.removeChild(staleSlot.firstChild); }
        staleText.textContent = "stale after " + date;
        staleChip.removeAttribute("data-okf-stale-now");
      }
    };
    refreshStale();
    document.addEventListener("visibilitychange", function () {
      if (document.visibilityState === "visible") { refreshStale(); }
    });
  }

  // X10: each "Referenced by" row gets its source's type glyph, titled.
  var context = document.getElementById("okf-context");
  var rows = context ? context.querySelectorAll(".okf-backlinks a.okf-row[data-okf-target]") : [];
  for (var r = 0; r < rows.length; r++) {
    var pos = positions.get(rows[r].getAttribute("data-okf-target"));
    if (pos === undefined) { continue; }
    rows[r].insertBefore(shapes.icon(shapes.kindOf(index, pos), "icon", shapes.typeLabel(index.concepts[pos].type)), rows[r].firstChild);
  }
})();
```

- [ ] **Step 3: Load it on concept pages and the index**

In `src/OKF4net.Viewer/HtmlWriter.cs`, in `PageScripts`, replace

```csharp
        "okf-toc.js",
        // P2: local graph
```

with

```csharp
        "okf-toc.js",
        "okf-page.js",
        // P2: local graph
```

Under `/* --- Task 15: Show all (C6) --- */` in `viewer.css`, insert:

```css
/* C6: "Show all" / "Show fewer", a text button at the right of the box head. */
body > .okf-layout > main > .okf-page-head .okf-fm-toggle {
  flex: none; padding: 0; border: 0; background: none; cursor: pointer;
  font-family: var(--body); font-size: 12.5px; font-weight: 600; color: var(--blue);
}
```

- [ ] **Step 4: Run both suites**

```bash
dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer"
cd tools/viewer-security-check && npm test
```

Expected: all xunit tests pass (`Every_script_and_stylesheet_a_page_loads_is_written` now covers `okf-page.js`); the six Task 15 cases `ok`; every earlier case still `ok`; `N passed, 0 failed`.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-page.js src/OKF4net.Viewer/HtmlWriter.cs src/OKF4net.Viewer/Assets/viewer.css tests/OKF4net.Tests/Viewer/HtmlWriterHeaderTests.cs tools/viewer-security-check/run.js
git commit -m "feat(viewer): okf-page.js places chip and backlink glyphs, flips the stale chip, folds the frontmatter

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 16 (conditional): `okf-fonts.css` with `data:` fonts (A26 fallback)

**Run only if Task 10 recorded "blocked in <browsers>".** Wave 4, in parallel with Tasks 12–14 (disjoint regions: `RenderDocumentStart`'s `<head>` and `WriteAssets` in `HtmlWriter.cs`, the `@font-face` markers of `viewer.css`). Spec §11.0: `HtmlWriter` writes `assets/okf-fonts.css` whose `@font-face` rules carry the woff2 as `data:` URIs built at write time from the embedded fonts, linked right after `viewer.css` on every page; `viewer.css` loses its `@font-face`; the woff2 are no longer written apart (the licences still are); budget 400 KB; the check is run again.

**Files:**
- Create: `src/OKF4net.Viewer/Assets/okf-fonts.css` (the `@font-face` block, moved from `viewer.css`, relative URLs kept in the source)
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` (between the `@font-face` markers only)
- Modify: `tools/viewer-fonts/vendor-fonts.js` (target file)
- Modify: `src/OKF4net.Viewer/HtmlWriter.cs` — `WriteAssets`, `RenderDocumentStart`; new `InlineFonts`
- Modify: `tests/OKF4net.Tests/Viewer/ViewerFontsTests.cs`, `HtmlWriterAssetsTests.cs` (`Every_embedded_asset_is_written_byte_for_byte_under_assets` and `Assets_come_first_in_ordinal_order_then_the_index_script_then_index_html_then_the_pages`), `HtmlWriterHeaderTests.cs` (`The_head_loads_the_theme_script_then_the_stylesheet_and_nothing_else`)
- Modify: `tools/viewer-security-check/run.js` (Task 10's case; under `// --- Task 16: fonts fallback … ---`)
- Modify: `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md` (§11.0, one paragraph)

**Interfaces:**
- Consumes: Task 10's faces and woff2, Task 11's `RenderDocumentStart`, Task 2's `WriteAssets`.
- Produces: written `assets/okf-fonts.css` (seven `@font-face`, each `url("data:font/woff2;base64,…")`), linked by `<link rel="stylesheet" href="{prefix}assets/okf-fonts.css">` right after `viewer.css` on every page (and on P3's `graph.html` through `RenderDocumentStart`); no `assets/fonts/*.woff2` written; `internal static string HtmlWriter.InlineFonts(string css)`.

- [ ] **Step 1: Move the `@font-face` block**

Create `src/OKF4net.Viewer/Assets/okf-fonts.css` containing, verbatim, the lines currently between (and including) the two markers `/* === @font-face (P1.1 Task 10 replaces the lines between these two markers) === */` and `/* === end @font-face === */` of `viewer.css`, preceded by:

```css
/* SPDX-License-Identifier: LGPL-3.0-or-later (this file; the fonts are SIL OFL 1.1, see fonts/README.md)
   A26 fallback: at least one browser blocked the relative @font-face of
   viewer.css under file:// (spec §11.0). HtmlWriter writes this file with each
   url("fonts/….woff2") replaced by the embedded font as a data: URI, and links
   it right after viewer.css on every page. */
```

In `viewer.css`, replace everything strictly between the two markers with:

```css
/* The fonts are served by okf-fonts.css (A26 fallback, spec §11.0). */
```

In `tools/viewer-fonts/vendor-fonts.js`, replace `const CSS = path.join(ROOT, "src", "OKF4net.Viewer", "Assets", "viewer.css");` with `const CSS = path.join(ROOT, "src", "OKF4net.Viewer", "Assets", "okf-fonts.css");` and the comment line `// the provenance table) and the @font-face block of viewer.css, between its` with `// the provenance table) and the @font-face block of okf-fonts.css (A26 fallback), between its`.

- [ ] **Step 2: Update the tests to the fallback**

In `ViewerFontsTests`, replace every `ViewerAssets.Css` by `ViewerAssets.Text("okf-fonts.css")`, and replace `The_stylesheet_declares_exactly_the_seven_faces_in_order_before_anything_else` and `The_written_site_holds_the_fonts_and_the_licences_but_not_the_provenance_readme` with:

```csharp
    [Fact]
    public void Okf_fonts_css_declares_exactly_the_seven_faces_in_order_and_viewer_css_none()
    {
        Assert.Equal(Faces, DeclaredFaces(ViewerAssets.Text("okf-fonts.css")).Select(f => (f.Family, f.Weight)));
        Assert.DoesNotContain("@font-face", ViewerAssets.Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_written_okf_fonts_css_carries_every_font_as_data_and_no_woff2_is_written_apart()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a.md", "---\ntype: Note\ntitle: A\n---\n");

        HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);

        var css = File.ReadAllText(Path.Combine(dest.Path, "assets", "okf-fonts.css"));
        Assert.Equal(7, Regex.Matches(css, "url\\(\"data:font/woff2;base64,[A-Za-z0-9+/=]+\"\\)").Count);
        Assert.DoesNotContain("url(\"fonts/", css, StringComparison.Ordinal);
        Assert.True(css.Length <= 400_000, $"okf-fonts.css weighs {css.Length} bytes, over the 400 000 of spec §11.0");
        Assert.Empty(Directory.GetFiles(Path.Combine(dest.Path, "assets", "fonts"), "*.woff2"));
        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "fonts", "OFL-Inter.txt")));
        Assert.False(File.Exists(Path.Combine(dest.Path, "assets", "fonts", "README.md")));
    }
```

In `HtmlWriterAssetsTests.Every_embedded_asset_is_written_byte_for_byte_under_assets`, replace the `foreach` body with:

```csharp
            var file = Path.Combine(dest.Path, "assets", path.Replace('/', Path.DirectorySeparatorChar));
            if (path.StartsWith("fonts/", StringComparison.Ordinal) && path.EndsWith(".woff2", StringComparison.Ordinal))
            {
                // A26 fallback: inlined in okf-fonts.css, never written apart.
                Assert.False(File.Exists(file), path);
                continue;
            }

            Assert.True(File.Exists(file), path);
            if (path != "okf-fonts.css")
            {
                Assert.Equal(ViewerAssets.Bytes(path), File.ReadAllBytes(file));
            }
```

In `HtmlWriterAssetsTests.Assets_come_first_in_ordinal_order_then_the_index_script_then_index_html_then_the_pages` (Task 2), which compares the first written files with **every** embedded path and so fails once the woff2 are no longer written (X23), replace

```csharp
        var assets = ViewerAssets.Paths.Select(p => "assets/" + p).ToList();
```

with

```csharp
        // A26 fallback: the woff2 fonts travel inside okf-fonts.css and are
        // never written apart; every other embedded path still comes first,
        // in ordinal order (okf-fonts.css among them).
        var assets = ViewerAssets.Paths
            .Where(p => !(p.StartsWith("fonts/", StringComparison.Ordinal) && p.EndsWith(".woff2", StringComparison.Ordinal)))
            .Select(p => "assets/" + p)
            .ToList();
```

In `HtmlWriterHeaderTests.The_head_loads_the_theme_script_then_the_stylesheet_and_nothing_else`, replace

```csharp
        Assert.EndsWith("<script src=\"../assets/okf-theme.js\"></script>\n<link rel=\"stylesheet\" href=\"../assets/viewer.css\">\n", head);
```

with

```csharp
        Assert.EndsWith("<script src=\"../assets/okf-theme.js\"></script>\n<link rel=\"stylesheet\" href=\"../assets/viewer.css\">\n<link rel=\"stylesheet\" href=\"../assets/okf-fonts.css\">\n", head);
```

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ViewerFontsTests|FullyQualifiedName~HtmlWriterAssetsTests|FullyQualifiedName~HtmlWriterHeaderTests"`
Expected: FAIL (`okf-fonts.css` written with relative URLs, woff2 written apart — which also fails `Assets_come_first…` —, no `<link>`).

- [ ] **Step 3: Inline the fonts and link the file**

In `HtmlWriter.cs`, add `using System.Text.RegularExpressions;`. In `WriteAssets`, replace the `foreach` with:

```csharp
        foreach (var path in ViewerAssets.Paths)
        {
            if (path.StartsWith("fonts/", StringComparison.Ordinal) && path.EndsWith(".woff2", StringComparison.Ordinal))
            {
                // A26 fallback: the fonts travel inside okf-fonts.css.
                continue;
            }

            var bytes = path == "okf-fonts.css"
                ? new UTF8Encoding(false).GetBytes(InlineFonts(ViewerAssets.Text(path)))
                : ViewerAssets.Bytes(path);
            WriteBytes(outDir, root, verifiedDirs, "assets/" + path, bytes, written);
        }
```

and add, after `WriteAssets`:

```csharp
    /// <summary>
    /// <c>okf-fonts.css</c> as written (spec §11.0, A26 fallback): each
    /// <c>url("fonts/x.woff2")</c> replaced by that embedded font as a
    /// <c>data:font/woff2;base64,…</c> URI, so no font request is ever made.
    /// </summary>
    internal static string InlineFonts(string css)
        => Regex.Replace(
            css,
            "url\\(\"(fonts/[a-z0-9-]+\\.woff2)\"\\)",
            m => "url(\"data:font/woff2;base64," + Convert.ToBase64String(ViewerAssets.Bytes(m.Groups[1].Value)) + "\")");
```

In `RenderDocumentStart`, replace

```csharp
          .Append("<link rel=\"stylesheet\" href=\"").Append(HtmlEscape(rootPrefix)).Append("assets/viewer.css\">\n")
```

with

```csharp
          .Append("<link rel=\"stylesheet\" href=\"").Append(HtmlEscape(rootPrefix)).Append("assets/viewer.css\">\n")
          .Append("<link rel=\"stylesheet\" href=\"").Append(HtmlEscape(rootPrefix)).Append("assets/okf-fonts.css\">\n")
```

Run the three test classes again. Expected: pass.

- [ ] **Step 4: Point Task 10's harness case at the new file**

In `run.js`, replace the case `fonts: every @font-face of the written stylesheet points at a file written under assets/fonts` (under `// --- Task 10: fonts ---`) with:

```js
check("fonts: the written okf-fonts.css carries the seven faces as data URIs, and viewer.css none (A26 fallback)", () => {
  const css = fs.readFileSync(path.join(SITE, "assets", "okf-fonts.css"), "utf8");
  const urls = Array.from(css.matchAll(/@font-face\s*\{[^}]*url\("([^"]+)"\)/g), (m) => m[1]);
  assert(urls.length === 7 && urls.every((u) => u.startsWith("data:font/woff2;base64,")), `${urls.length} faces, not all data URIs`);
  assert(!/@font-face/.test(fs.readFileSync(path.join(SITE, "assets", "viewer.css"), "utf8")), "viewer.css still declares fonts");
});
```

Under `// --- Task 16: fonts fallback … ---`, insert:

```js
checkAsync("fonts: every page links okf-fonts.css right after viewer.css", async () => {
  for (const rel of ["index.html", "foo/bar.html"]) {
    const window = await openPage(rel);
    const links = Array.from(window.document.head.querySelectorAll('link[rel="stylesheet"]'), (l) => l.getAttribute("href"));
    const prefix = rel.includes("/") ? "../" : "";
    assert(JSON.stringify(links) === JSON.stringify([`${prefix}assets/viewer.css`, `${prefix}assets/okf-fonts.css`]), `${rel}: ${JSON.stringify(links)}`);
  }
});
```

Run `cd tools/viewer-security-check && npm test`. Expected: `N passed, 0 failed`.

- [ ] **Step 5: Run the `fonts` control again and record it**

Rebuild the two sites and rerun Task 10 Step 8's command (`--only fonts`, Chrome, Edge, Firefox) and Step 9 (installed Firefox). Expected: `fonts: ok` in the three browsers. In the spec §11.0 paragraph Task 10 added, append:

```markdown
Repli A26 appliqué (P1.1, AAAA-MM-JJ) : `assets/okf-fonts.css` de S octets
(budget 400 000), polices en URI `data:` ; vérification refaite : Chrome VC,
Edge VE, Firefox VF, Firefox installé VI — chargées partout.
```

with the date, the written size of `okf-fonts.css` for the OKF4net site, and the versions. If a browser still fails, stop and report to the owner.

- [ ] **Step 6: Format and commit**

```bash
dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Viewer/Assets/okf-fonts.css src/OKF4net.Viewer/Assets/viewer.css tools/viewer-fonts/vendor-fonts.js src/OKF4net.Viewer/HtmlWriter.cs tests/OKF4net.Tests/Viewer/ViewerFontsTests.cs tests/OKF4net.Tests/Viewer/HtmlWriterAssetsTests.cs tests/OKF4net.Tests/Viewer/HtmlWriterHeaderTests.cs tools/viewer-security-check/run.js docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md
git commit -m "fix(viewer): serve the fonts as data URIs in okf-fonts.css, which file:// cannot block (A26)

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 17: Documentation, CI smoke test and changelog

Wave 6, in parallel with Task 18. `CLAUDE.md` (the viewer paragraph), the viewer README, the root README (site description and licence paragraph, for the three slices), the harness README, the AOT smoke test (assets and fonts), and the whole `CHANGELOG` entry of the interactive viewer, one line per slice (§12.0, §12.8).

**Files:**
- Modify: `CLAUDE.md` (the `src/OKF4net.Viewer/` paragraph of Architecture)
- Modify: `src/OKF4net.Viewer/README.md`
- Modify: `tools/viewer-security-check/README.md` (section "Interactive viewer cases")
- Modify: `.github/workflows/ci.yml` (step "Run published okf-render")
- Modify: `CHANGELOG.md` (the "Interactive `okf-render` pages (P1)" entry)
- Modify: `README.md` (root: the site paragraph of the `okf-render` section, and the licence paragraph naming `marked`) — P1.1's for all three slices (spec r7 §12.0)

**Interfaces:**
- Consumes: everything above.
- Produces: documentation only; the CI step fails when the native `okf-render` does not write `okf-shapes.js`, `okf-page.js`, the three licence texts and either the woff2 fonts or `okf-fonts.css`.

- [ ] **Step 1: Extend the AOT smoke test**

In `.github/workflows/ci.yml`, in the step `Run published okf-render`, replace

```yaml
          node tools/viewer-security-check/check-index.js $indexScript
          if ($LASTEXITCODE -ne 0) { throw "$indexScript does not execute as a usable site index" }
          Write-Host "OK: okf-render wrote $outDir/index.html and an executable $indexScript"
```

with

```yaml
          node tools/viewer-security-check/check-index.js $indexScript
          if ($LASTEXITCODE -ne 0) { throw "$indexScript does not execute as a usable site index" }
          # Embedded assets come out of the NATIVE binary too (GetManifestResourceNames
          # under AOT): the P1.1 scripts, the font licences, and the fonts either as
          # woff2 files or inlined in okf-fonts.css (spec §11.0, A26).
          foreach ($asset in @("okf-shapes.js", "okf-page.js", "fonts/OFL-Inter.txt", "fonts/OFL-InterTight.txt", "fonts/OFL-SpaceMono.txt")) {
            if (-not (Test-Path "$outDir/assets/$asset")) { throw "okf-render did not write assets/$asset" }
          }
          $woff2 = @(Get-ChildItem -Path "$outDir/assets/fonts" -Filter *.woff2 -ErrorAction SilentlyContinue)
          if ($woff2.Count -eq 0 -and -not (Test-Path "$outDir/assets/okf-fonts.css")) { throw "okf-render wrote neither the woff2 fonts nor okf-fonts.css" }
          Write-Host "OK: okf-render wrote $outDir/index.html, an executable $indexScript, the P1.1 scripts and the fonts"
```

- [ ] **Step 2: Rewrite the viewer paragraph of `CLAUDE.md`**

In `CLAUDE.md`, in the `**`src/OKF4net.Viewer/`**` paragraph, replace the two sentences starting `Units: \`SiteModel\`` and `The interactive scripts (\`okf-theme.js\` in \`<head>\`;` (up to and including `(design: \`docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md\`).`) with:

```markdown
Units: `SiteModel` (pure `Bundle` → display-model projection, including the page head, `DisplayBody` without a leading H1 that repeats the title, the bundle name and the graph page's free name), `SiteIndex` + `IndexScript` (pure `Bundle` → site index v2 → `assets/okf-index.js`: concepts with their type rank and a description truncated at 200 code points, merged edges, ghosts, the id tree, the ranked `types` table), `HtmlWriter` (the only I/O; one `RenderDocumentStart`/`RenderHeader` for the page, index and graph views, the `PageScripts` table, the merge markers `// P2: local graph`, `// P3: graph page (§12.5)` and `// P3: RenderGraph (§12.5)`), `ViewerAssets` (every file under `Assets/` — CSS, JS, fonts, licence texts — embedded under its path and written by `HtmlWriter` with no per-file code). The interactive scripts (`okf-theme.js` in `<head>`; `okf-site.js`, `okf-shapes.js`, `okf-explorer.js`, `okf-palette.js`, `okf-toc.js`, `okf-page.js` after `viewer.js`) are classic scripts that never touch `viewer.js`'s `{ body, links }` contract and insert bundle text through `textContent`, or `setAttribute` on a fixed attribute name — never `innerHTML` or a markup string; every chrome CSS selector and every `okf-page.js` query is anchored to a chrome container, because the sanitizer keeps `class` on `<code>` (the harness checks every slice's `*chrome-classes.md` fixture). **`okf-shapes.js` (`OkfShapes`) is the single source of every type, trust, staleness and ghost shape** (explorer, palette, chips, lists, legends, graphs): pixel-exact geometry from one `SIZES` table, a fixed SVG vocabulary, colours only through classes reading the CSS tokens — never draw such a shape elsewhere. Their guard is the same jsdom harness, which loads a site generated from `tools/viewer-security-check/fixtures/hostile-bundle/`, and runs every slice's `cases/*.js` through a frozen helper object; what jsdom cannot see (layout, fonts, contrast) is checked by the tooled recette `tools/viewer-security-check/recette/`, run by hand, with Playwright resolved at run time and never a dependency (design: `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`).
```

- [ ] **Step 3: Update the viewer README**

Replace the first paragraph of `src/OKF4net.Viewer/README.md` (the line under `# OKF4net.Viewer`) with:

```markdown
Static HTML site generation for OKF knowledge bundles: one page per concept
(breadcrumb, title, type/status/trust/staleness chips, a folding frontmatter
box, the rendered body), a generated index, navigable cross-links, a tree
explorer with type shapes and filters, a "Jump to" palette, a contents list
that follows the reading position, "Referenced by" lists, light and dark
themes, and the Inter, Inter Tight and Space Mono fonts embedded — all of it
working from `file://`, with no server.
```

- [ ] **Step 4: Update the harness README's interactive section**

In `tools/viewer-security-check/README.md`, in `## Interactive viewer cases`, replace

```
The second half of `run.js` covers the interactive viewer scripts
(`okf-site.js`, `okf-theme.js`, `okf-explorer.js`, `okf-palette.js`,
`okf-toc.js`).
```

with

```
The second half of `run.js` covers the interactive viewer scripts
(`okf-site.js`, `okf-theme.js`, `okf-shapes.js`, `okf-explorer.js`,
`okf-palette.js`, `okf-toc.js`, `okf-page.js`); `OkfShapes` cases run in a
bare window (`okfShapes()`) against the attribute table of spec §12.2,
written out by hand.
```

and replace `well-formed index (version 1, and every concept` with `well-formed index (version 2, the ranked types table, and every concept`.

- [ ] **Step 5: Update the root `README.md` (spec r7 §12.0: P1.1 writes it for the three slices)**

In `README.md`, in the section whose heading reads "`okf-render` — generate a static HTML site", replace the paragraph

```markdown
The generated site is self-contained and opens straight off the filesystem —
no server needed. Every page carries a tree explorer of the bundle (concepts
show a badge when they are machine-confirmed or human-reviewed, and when they
are stale), a "Jump to" palette (Ctrl+K or `/`, matching titles, ids and
tags) and a light/dark toggle; a side panel with the page's contents and
backlinks appears where the page has contents (h2/h3 headings) or backlinks.
```

with

```markdown
The generated site is self-contained and opens straight off the filesystem —
no server needed. Every page carries one header (the bundle's name, its
concept and link counts, a "Jump to" palette — Ctrl+K or `/`, matching
titles, ids and tags —, "Global graph" and a light/dark toggle) and a tree
explorer of the bundle with type shapes, type filters and trust and
staleness flags. A concept page shows a breadcrumb, its title, chips for
type, `status`, trust and staleness, and a folding frontmatter box; its side
panel holds the page's contents, its local graph (the concept's neighbours at
one or two hops, with an equivalent list) and the pages that reference it.
A graph page (`graph.html`, or `graph-1.html`… when a concept already takes
that name) draws the whole bundle, with facets, a detail drawer, keyboard
navigation and an equivalent list. The Inter, Inter Tight and Space Mono
fonts are embedded.
```

Further down, in the licence section, replace

```markdown
The one exception anywhere in the build is `OKF4net.Viewer` (not itself
published to NuGet): it vendors a copy of
[marked](https://github.com/markedjs/marked) (MIT) for client-side markdown
rendering, embedded in that library and shipped inside the `okf-render`
binary it backs; `okf` itself never references it. See [`NOTICE`](NOTICE)
for the full accounting, including that vendored copy and the two kinds of
```

with

```markdown
The one exception anywhere in the build is `OKF4net.Viewer` (not itself
published to NuGet): it vendors a copy of
[marked](https://github.com/markedjs/marked) (MIT) for client-side markdown
rendering, and the Inter, Inter Tight and Space Mono fonts (SIL Open Font
License 1.1, their licence texts written beside them under `assets/fonts/`),
all embedded in that library and shipped inside the `okf-render` binary it
backs; `okf` itself never references them. See [`NOTICE`](NOTICE)
for the full accounting, including those vendored copies and the two kinds of
```

If Task 16 ran, the fonts travel inside `assets/okf-fonts.css`: write "their licence texts written under `assets/fonts/`" instead of "written beside them under `assets/fonts/`".

- [ ] **Step 6: Write the changelog entry, one line per slice**

In `CHANGELOG.md`, replace the whole entry that starts `- **Interactive \`okf-render\` pages (P1).**` (through `loads a site generated from a hostile fixture bundle and needs the .NET SDK.`) with:

```markdown
- **Interactive `okf-render` pages.** Delivered in slices on one branch; the
  OKF4net core API and `viewer.js` are unchanged throughout.
  - *P1 — navigation.* A tree explorer of the bundle (a concept that is also
    a folder opens and expands separately; trust tiers and staleness from
    `ConceptAudit`, staleness evaluated when the page is read), a "Jump to"
    palette (Ctrl+K or `/`, fixed-tier matching on titles, ids and tags — not
    full-text search), a light/dark toggle, and a side panel with contents and
    backlinks. Headings get generated `okf-h-` anchors, so author links such
    as `#usage` now land. The data comes from a generated
    `assets/okf-index.js` (`SiteIndex`, `IndexScript`). The jsdom harness
    loads a site generated from a hostile fixture bundle and needs the .NET SDK.
  - *P1.1 — the validated mockups.* One header on every page (bundle name,
    concept and link counts, "Global graph", "Skip to content"); a centre
    column with a breadcrumb, the title once (a leading H1 that repeats it is
    dropped), chips for type, `status`, trust with its verifier and date, and
    staleness, and a folding frontmatter box; types drawn as shapes and
    colours (`OkfShapes`, ranked by frequency in the index, schema v2, which
    also carries descriptions), explorer rows by id segment with type chips
    that filter, a legend, the current section marked in the contents, glyphs
    and a count on "Referenced by", the palette redrawn; Inter, Inter Tight
    and Space Mono (SIL OFL 1.1) embedded; a tooled, manual browser recette
    under `tools/viewer-security-check/recette/`.
  - *P2 — local graph.* The context panel shows the concept's neighbourhood
    (one or two hops, at most 40 nodes, links to absent concepts as ghosts)
    with an equivalent list.
  - *P3 — global graph.* `graph.html` draws the whole bundle with a
    deterministic layout, facets (type, trust, staleness, tags), a detail
    drawer and keyboard navigation, linked from every page's "Global graph".
```

- [ ] **Step 7: Check and commit**

```bash
dotnet build OKF4net.sln
dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer|FullyQualifiedName~OKF4net.Tests.Render"
dotnet format OKF4net.sln --verify-no-changes
git add CLAUDE.md README.md src/OKF4net.Viewer/README.md tools/viewer-security-check/README.md .github/workflows/ci.yml CHANGELOG.md
git commit -m "docs(viewer): P1.1 in CLAUDE.md, the READMEs and the changelog; AOT smoke test checks the new assets and fonts

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

Expected: build and tests green, format clean. The CI step itself is verified when the branch is pushed (the `aot-publish` job's `Run published okf-render` step prints `OK: … the P1.1 scripts and the fonts` on the three runners).

---
### Task 18: Recette — P1 port (RC1–RC11), every P1.1 control, the acceptance checklist, the final run

Wave 6, in parallel with Task 17. Completes the tooled recette (§12.8): `p1.js` ports P1's eleven checks to the P1.1 DOM, `p1-1.js` measures every §11 element this plan delivers (keyed by §11 id) plus `fonts`, `tokens` and `requests` (control 14); `ACCEPTANCE.md` gets the P1.1 hand checks. Then the whole slice is verified once, end to end.

**Files:**
- Modify: `tools/viewer-security-check/recette/lib.js` (one helper, `pageWithSections`, and its export)
- Create: `tools/viewer-security-check/recette/p1.js`
- Modify: `tools/viewer-security-check/recette/p1-1.js` (whole file)
- Modify: `tools/viewer-security-check/ACCEPTANCE.md` (the line `*(P1.1 checks)*` only)

**Interfaces:**
- Consumes: Task 9's driver and library; the P1.1 DOM and CSS of Tasks 3, 7, 8, 11–15.
- Produces: `p1.js` → results `RC1`…`RC11`; `p1-1.js` → results `fonts`, `H1`–`H13` (`H10` is P3's to check on `graph.html`), `E1`–`E13`, `C1`–`C8`, `X1`, `X3`, `X4`, `X10`, `X11`, `J1`–`J6`, `L6`, `tokens`, `requests`; captures named by id; `lib.pageWithSections(dir, n)` → the first page, in index order, whose body holds at least `n` `##`/`###` headings, or null.

- [ ] **Step 1: Add the page finder to the library**

In `tools/viewer-security-check/recette/lib.js`, add before `module.exports`:

```js
// The first page, in index order, whose markdown body holds at least `n`
// "## " or "### " headings: read from the payload of the generated HTML,
// where a newline is the two characters \n (HtmlSafeJson).
function pageWithSections(dir, n) {
  for (const concept of readIndex(dir).concepts) {
    const html = fs.readFileSync(path.join(dir, ...concept.path.split("/")), "utf8");
    if ((html.match(/\\n#{2,3} /g) || []).length >= n) { return concept.path; }
  }
  return null;
}
```

and add `pageWithSections` to the object of `module.exports`.

- [ ] **Step 2: Port P1's recette**

Create `tools/viewer-security-check/recette/p1.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// P1's recette (2026-10-07, run by hand outside the repository), ported and
// kept (spec §12.8): its checks C1–C11, renamed RC1–RC11 so they are not
// confused with C1–C8 of spec §11.3. Pages are chosen from the generated
// index instead of hard-coded paths, and selectors follow the P1.1 DOM.
"use strict";
const fs = require("fs");
const { fileURLToPath } = require("url");

function choose(ctx) {
  const idx = ctx.lib.readIndex(ctx.siteDir);
  const depth = (c) => c.path.split("/").length - 1;
  const max = Math.max(...idx.concepts.map(depth));
  const find = (nodes) => {
    for (const n of nodes) {
      if (n.concept >= 0 && n.children.length > 0) { return idx.concepts[n.concept]; }
      const hit = find(n.children);
      if (hit) { return hit; }
    }
    return null;
  };
  return {
    deep: idx.concepts.find((c) => depth(c) === max).path,
    far: idx.concepts[idx.concepts.length - 1].path,
    both: find(idx.tree),
    toc: ctx.lib.pageWithSections(ctx.siteDir, 3),
  };
}

const active = (page) => page.evaluate(() => { const a = document.activeElement; return a ? (a.id || a.className || a.tagName) : null; });
const paletteOpen = (page) => page.evaluate(() => { const b = document.querySelector("body > .okf-palette-backdrop"); return !!b && !b.hidden; });
const exists = (page) => { try { return fs.existsSync(fileURLToPath(page.url().split("#")[0])); } catch (e) { return false; } };
const clean = (page) => page.okfTracked.errors.length === 0;

async function rc1(ctx, p) {
  const page = await ctx.newPage();
  const times = {};
  for (const rel of ["index.html", p.deep, p.far]) {
    const t0 = Date.now();
    await page.goto(ctx.site + rel);
    times[rel] = Date.now() - t0;
  }
  await ctx.shot(page, "RC1");
  return { pass: Math.max(...Object.values(times)) < 2000 && clean(page), times };
}

async function rc2(ctx, p) {
  const page = await ctx.newPage();
  await page.goto(ctx.site + p.deep);
  const deep = await page.evaluate(() => {
    const nav = document.getElementById("okf-explorer");
    const current = nav.querySelector('a.okf-tree-link[aria-current="page"]');
    const toggles = [];
    let li = current ? current.closest("li").parentElement.closest("li") : null;
    while (li) {
      const t = li.querySelector(":scope > .okf-tree-row > .okf-tree-toggle");
      if (t) { toggles.push(t.getAttribute("aria-expanded")); }
      li = li.parentElement.closest("li");
    }
    return { hidden: nav.hidden, current: !!current, ancestorsOpen: toggles.every((x) => x === "true") };
  });
  await page.goto(ctx.site + p.far);
  const far = await page.evaluate(() => {
    const nav = document.getElementById("okf-explorer");
    const r = nav.getBoundingClientRect();
    const c = nav.querySelector('a.okf-tree-link[aria-current="page"]').getBoundingClientRect();
    return c.top >= Math.max(0, r.top) && c.bottom <= Math.min(innerHeight, r.bottom);
  });
  await page.goto(ctx.site + "index.html");
  const row = page.locator(`#okf-explorer a.okf-tree-link[data-okf-id="${p.both.id}"]`).locator("xpath=..");
  const toggle = row.locator(".okf-tree-toggle");
  const before = await toggle.getAttribute("aria-expanded");
  await toggle.click();
  const after = await toggle.getAttribute("aria-expanded");
  const stayed = page.url().endsWith("index.html");
  await page.fill("#okf-tree-filter", p.both.id.split("/").pop());
  const filtered = await page.evaluate(() => Array.from(document.querySelectorAll("#okf-explorer a.okf-tree-link")).filter((a) => !a.closest("[hidden]")).length);
  await page.fill("#okf-tree-filter", "");
  await Promise.all([page.waitForNavigation(), row.locator("a.okf-tree-link").click()]);
  const opened = page.url().endsWith(p.both.path);
  await ctx.shot(page, "RC2");
  return { pass: !deep.hidden && deep.current && deep.ancestorsOpen && far && before !== after && stayed && filtered >= 1 && opened && clean(page), deep, far, before, after, filtered, opened };
}

async function rc3(ctx, p) {
  const page = await ctx.newPage();
  await page.goto(ctx.site + p.deep);
  const res = {};
  for (const [name, selector] of [["explorer", "#okf-explorer a.okf-tree-link"], ["backlink", "#okf-context .okf-backlinks a.okf-row"], ["body", "#okf-body a[href$='.html']"]]) {
    const link = page.locator(selector).first();
    if (await link.count()) {
      await Promise.all([page.waitForNavigation(), link.click()]);
      res[name] = exists(page);
      await page.goBack();
    }
  }
  return { pass: res.explorer === true && res.backlink !== false && res.body !== false && clean(page), res };
}

async function rc4(ctx, p) {
  const page = await ctx.newPage();
  await page.goto(ctx.site + p.toc);
  const r = {};
  await page.click("#okf-tools .okf-palette-open");
  r.button = (await paletteOpen(page)) && (await active(page)) === "okf-palette-input";
  await page.keyboard.press("Escape");
  r.escapeReturnsToOpener = !(await paletteOpen(page)) && /okf-palette-open/.test(await active(page));
  await page.locator("#okf-body p").first().click();
  await page.keyboard.press("Control+k");
  r.ctrlK = await paletteOpen(page);
  for (let i = 0; i < 5; i++) { await page.keyboard.press("Tab"); }
  r.tabStays = await page.evaluate(() => !!document.activeElement.closest(".okf-palette"));
  for (let i = 0; i < 5; i++) { await page.keyboard.press("Shift+Tab"); }
  r.shiftTabStays = await page.evaluate(() => !!document.activeElement.closest(".okf-palette"));
  await page.keyboard.press("Escape");
  await page.locator("#okf-body p").first().click();
  await page.keyboard.press("/");
  r.slash = await paletteOpen(page);
  await page.keyboard.type(p.far.replace(/\.html$/, "").split("/").pop(), { delay: 10 });
  await page.keyboard.press("ArrowDown");
  const target = await page.evaluate(() => {
    const input = document.getElementById("okf-palette-input");
    const option = document.getElementById(input.getAttribute("aria-activedescendant"));
    return option && option.querySelector(".okf-palette-id").textContent;
  });
  await Promise.all([page.waitForNavigation(), page.keyboard.press("Enter")]);
  r.arrowEnter = page.url().endsWith(target + ".html");
  r.aria = await page.evaluate(() => {
    const d = document.querySelector(".okf-palette");
    return d.getAttribute("role") === "dialog" && d.getAttribute("aria-modal") === "true" && !!d.querySelector("[role=status]");
  });
  return { pass: Object.values(r).every(Boolean) && clean(page), ...r };
}

async function rc5(ctx) {
  const page = await ctx.newPage();
  await page.goto(ctx.site + "index.html");
  await page.keyboard.press("Control+k");
  await page.keyboard.type("o");
  const total = await page.evaluate(() => document.querySelectorAll("[role=option]").length);
  let hidden = 0;
  for (let i = 0; i < total; i++) {
    await page.keyboard.press("ArrowDown");
    if (i % 25 === 0 || i > total - 3) {
      const visible = await page.evaluate(() => {
        const input = document.getElementById("okf-palette-input");
        const o = document.getElementById(input.getAttribute("aria-activedescendant"));
        const r = o.getBoundingClientRect();
        const l = o.parentElement.getBoundingClientRect();
        return r.top >= l.top - 1 && r.bottom <= l.bottom + 1;
      });
      if (!visible) { hidden++; }
    }
  }
  await ctx.shot(page, "RC5");
  const broken = await page.evaluate(() => Array.from(document.querySelectorAll("[role=option]")).slice(0, 60).filter((o) => {
    const t = o.querySelector(".okf-palette-title");
    const lh = parseFloat(getComputedStyle(t).lineHeight) || 20;
    return !/\s/.test(t.textContent) && t.textContent.length < 30 && t.getBoundingClientRect().height > lh * 1.5;
  }).map((o) => o.querySelector(".okf-palette-title").textContent));
  return { pass: total > 50 && hidden === 0 && broken.length === 0 && clean(page), total, hidden, broken: broken.slice(0, 5) };
}

async function rc6(ctx, p) {
  const page = await ctx.newPage();
  await page.goto(ctx.site + p.far);
  await page.evaluate(() => { try { localStorage.removeItem("okf-theme"); } catch (e) { /* none */ } });
  await page.reload();
  const state = () => page.evaluate(() => ({
    attr: document.documentElement.getAttribute("data-theme"),
    pressed: document.getElementById("okf-theme-toggle").getAttribute("aria-pressed"),
    scheme: getComputedStyle(document.documentElement).colorScheme,
  }));
  await page.click("#okf-theme-toggle");
  const toggled = await state();
  await ctx.shot(page, "RC6");
  await page.goto(ctx.site + p.toc);
  const other = await state();
  await page.reload();
  const reload = await state();
  await page.click("#okf-theme-toggle");
  await page.evaluate(() => { try { localStorage.removeItem("okf-theme"); } catch (e) { /* none */ } });
  return { pass: toggled.attr === "dark" && toggled.pressed === "true" && /dark/.test(toggled.scheme) && other.attr === "dark" && reload.attr === "dark" && clean(page), toggled, other, reload };
}

async function rc7(ctx, p) {
  const out = {};
  for (const theme of ["light", "dark"]) {
    const page = await ctx.newPage();
    await page.goto(ctx.site + "index.html");
    await page.evaluate((t) => { try { localStorage.setItem("okf-theme", t); } catch (e) { /* none */ } }, theme);
    await page.goto(ctx.site + p.toc);
    const c = await page.evaluate(() => {
      const bg = getComputedStyle(document.body).backgroundColor;
      const pick = (sel) => { const e = document.querySelector(sel); return e ? getComputedStyle(e).color : null; };
      return { bg, text: pick("#okf-body p"), link: pick("#okf-body a, #okf-context a"), tree: pick("#okf-explorer .okf-tree-link"), current: pick('#okf-explorer [aria-current="page"]'), toc: pick("#okf-toc a") };
    });
    await page.keyboard.press("Control+k");
    await page.keyboard.type("o");
    const option = await page.evaluate(() => {
      const o = document.querySelector('[role=option][aria-selected="true"]');
      return { fg: getComputedStyle(o.querySelector(".okf-palette-title")).color, bg: getComputedStyle(o).backgroundColor };
    });
    const ratios = {};
    for (const k of ["text", "link", "tree", "current", "toc"]) { if (c[k]) { ratios[k] = ctx.lib.contrast(c[k], c.bg); } }
    ratios.activeOption = ctx.lib.contrast(option.fg, /rgba?\(0, 0, 0, 0\)/.test(option.bg) ? c.bg : option.bg);
    out[theme] = ratios;
    await page.evaluate(() => { try { localStorage.removeItem("okf-theme"); } catch (e) { /* none */ } });
  }
  return { pass: [...Object.values(out.light), ...Object.values(out.dark)].every((x) => x >= 4.5), ...out };
}

async function rc8(ctx, p) {
  const page = await ctx.newPage();
  await page.goto(ctx.site + p.toc);
  const items = page.locator("#okf-toc a");
  const n = await items.count();
  const ids = await page.evaluate(() => Array.from(document.querySelectorAll("#okf-toc a"), (a) => a.getAttribute("href").slice(1)));
  const r = { n };
  await items.nth(0).click();
  r.first = await active(page);
  await items.nth(n - 1).click();
  r.last = await active(page);
  await page.click("#okf-tools .okf-palette-open");
  await page.keyboard.press("Escape");
  await items.nth(n - 1).click();
  r.reclick = await active(page);
  await page.goBack();
  await page.waitForTimeout(200);
  r.back = await active(page);
  await page.goForward();
  await page.waitForTimeout(200);
  r.forward = await active(page);
  return { pass: r.first === ids[0] && r.last === ids[n - 1] && r.reclick === r.last && r.back === r.first && r.forward === r.last && clean(page), ...r };
}

async function rc9(ctx, p) {
  const page = await ctx.newPage();
  const id = await (async () => {
    await page.goto(ctx.site + p.toc);
    return page.evaluate(() => document.querySelectorAll("#okf-toc a")[1].getAttribute("href").slice(1));
  })();
  await page.goto("about:blank");
  await page.goto(ctx.site + p.toc + "#" + id.replace(/^okf-h-/, ""));
  await page.waitForTimeout(200);
  const focus = await active(page);
  return { pass: focus === id && clean(page), focus, expected: id };
}

async function rc10(ctx, p) {
  const page = await ctx.newPage({ viewport: { width: 390, height: 844 } });
  const r = {};
  for (const rel of [p.deep, p.far, "index.html"]) {
    await page.goto(ctx.site + rel);
    r[rel] = await page.evaluate(() => ({ scrollWidth: document.documentElement.scrollWidth, mainTop: Math.round(document.querySelector("main").getBoundingClientRect().top + scrollY) }));
  }
  await ctx.shot(page, "RC10-page");
  await page.keyboard.press("Control+k");
  await page.keyboard.type(p.deep.replace(/\.html$/, "").split("/").pop());
  r.palette = await page.evaluate(() => {
    const box = document.querySelector(".okf-palette").getBoundingClientRect();
    const options = Array.from(document.querySelectorAll("[role=option]"));
    return { left: Math.round(box.left), right: Math.round(box.right), overflowing: options.filter((o) => o.scrollWidth > o.clientWidth + 1).length };
  });
  await ctx.shot(page, "RC10-palette");
  const pages = [p.deep, p.far, "index.html"].map((rel) => r[rel]);
  return { pass: pages.every((v) => v.scrollWidth <= 390 && v.mainTop < 400) && r.palette.left >= 0 && r.palette.right <= 390 && r.palette.overflowing === 0 && clean(page), ...r };
}

async function rc11(ctx) {
  const page = await ctx.newPage();
  const idx = ctx.lib.readIndex(ctx.acmeDir);
  for (const c of idx.concepts) { await page.goto(ctx.acme + c.path); }
  await page.keyboard.press("Control+k");
  await page.keyboard.type("order");
  const results = await page.evaluate(() => document.querySelectorAll("[role=option]").length);
  await ctx.shot(page, "RC11");
  return { pass: idx.concepts.length === 9 && results > 0 && clean(page), concepts: idx.concepts.length, results };
}

async function run(ctx) {
  const p = choose(ctx);
  const checks = { RC1: rc1, RC2: rc2, RC3: rc3, RC4: rc4, RC5: rc5, RC6: rc6, RC7: rc7, RC8: rc8, RC9: rc9, RC10: rc10, RC11: rc11 };
  const out = {};
  for (const [id, fn] of Object.entries(checks)) {
    if (ctx.wanted(id)) { out[id] = await ctx.lib.guard(() => fn(ctx, p)); }
  }
  return out;
}

module.exports = { run };
```

- [ ] **Step 3: Complete the P1.1 recette**

Replace the whole content of `tools/viewer-security-check/recette/p1-1.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// P1.1 recette (spec §11, §12.8): what jsdom cannot see -- fonts really
// loaded, computed sizes and colours, widths, contrasts in both themes,
// requests -- measured in real browsers on the generated sites, opened as
// files. Results are keyed by §11 id (H, E, C, X, J, L) or named (fonts,
// tokens, requests); every id gets a capture at 1440 x 900 named after it, to
// set beside the mockups (A: Main.dc.html; the palette of C: Focus.dc.html).
"use strict";

// The seven faces of spec §11.0, A16.
const FACES = [["Inter", 400], ["Inter", 500], ["Inter", 600], ["Inter Tight", 600], ["Inter Tight", 900], ["Space Mono", 400], ["Space Mono", 700]];
// §11.0 light tokens the probes compare with.
const T = { white: "#ffffff", ink: "#101014", blue: "#1a3fd6", blueSoft: "#eef1fd", gray: "#6a6a72", hair: "#e3e3e8", stale: "#b4540a" };
// Mockup A's page, in acme_retail.
const PAGE = "computations/gross-margin-period.html";

// §11.0: on index.html and on the first page of every depth of both sites,
// document.fonts.load must return a non-empty list whose every face is
// "loaded" (not document.fonts.check, which is true when no face matches),
// with no failed request under assets/fonts/.
async function fonts(ctx) {
  const results = [];
  for (const [label, dir, url] of [["okf4net", ctx.siteDir, ctx.site], ["acme", ctx.acmeDir, ctx.acme]]) {
    for (const { rel, depth } of ctx.lib.pagesByDepth(dir)) {
      const page = await ctx.newPage();
      await page.goto(url + rel, { waitUntil: "load" });
      const faces = await page.evaluate(async (wanted) => {
        const out = [];
        for (const [family, weight] of wanted) {
          try {
            const list = await document.fonts.load(`${weight} 16px "${family}"`);
            out.push({ family, weight, faces: list.length, loaded: list.length > 0 && list.every((f) => f.status === "loaded") });
          } catch (e) {
            out.push({ family, weight, faces: 0, loaded: false, error: String(e) });
          }
        }
        return out;
      }, FACES);
      const failed = page.okfTracked.failed.filter((u) => /\/assets\/fonts\//.test(u));
      results.push({ site: label, page: rel, depth, ok: failed.length === 0 && faces.every((f) => f.loaded), failed, faces: faces.filter((f) => !f.loaded) });
      await page.close();
    }
  }
  return { pass: results.every((r) => r.ok), pages: results.length, failures: results.filter((r) => !r.ok) };
}

// Computed values of [selector, { property: expected }] pairs on `page`.
// expected: a string (exact), a RegExp, or a number (px, within 0.1, since
// Firefox rounds lengths to 1/60 px).
async function styles(page, checks) {
  const got = await page.evaluate((list) => list.map(([sel, props]) => {
    const el = document.querySelector(sel);
    if (!el) { return null; }
    const cs = getComputedStyle(el);
    const out = {};
    for (const p of props) { out[p] = cs.getPropertyValue(p).trim(); }
    return out;
  }), checks.map(([sel, props]) => [sel, Object.keys(props)]));
  const misses = [];
  checks.forEach(([sel, props], k) => {
    if (!got[k]) { misses.push(`${sel}: missing`); return; }
    for (const [p, want] of Object.entries(props)) {
      const value = got[k][p];
      const ok = want instanceof RegExp ? want.test(value)
        : typeof want === "number" ? Math.abs(parseFloat(value) - want) <= 0.1
        : value === want;
      if (!ok) { misses.push(`${sel} ${p}: ${value} (expected ${want})`); }
    }
  });
  return misses;
}

function probes(rgb, tocPage) {
  const head = "body > .okf-layout > main > .okf-page-head";
  return [
    ["H1", PAGE, [["body > .topline", { height: "6px", "background-color": rgb(T.blue) }]]],
    ["H2", PAGE, [["body > header.bar .bar-in", { height: "52px", "padding-left": "20px", "padding-right": "20px", "column-gap": "20px" }],
      ["body > header.bar", { "border-bottom-width": "1px", "border-bottom-color": rgb(T.hair) }]]],
    ["H3", PAGE, [["body > header.bar .wordmark", { "font-family": /Inter Tight/, "font-weight": "900", "font-size": "20px" }],
      ["body > header.bar .wordmark sup", { "font-family": /Space Mono/, "font-size": "12px", color: rgb(T.blue) }]]],
    ["H4", PAGE, [["body > header.bar .bar-sep", { width: "1px", height: "20px", "background-color": rgb(T.hair) }]]],
    ["H5", PAGE, [["#okf-bundle-name", { "font-family": /Space Mono/, "font-size": "13px", color: rgb(T.ink), "text-overflow": "ellipsis", "white-space": "nowrap" }]]],
    ["H6", PAGE, [["#okf-bundle-counts", { "font-family": /Space Mono/, "font-size": "13px", color: rgb(T.gray) }]]],
    ["H7", PAGE, [["#okf-tools .okf-palette-open", { width: "320px", height: "34px", color: rgb(T.gray), "font-size": 13.5 }],
      ["#okf-tools .okf-palette-hint", { "font-family": /Space Mono/, "font-size": "11px", "border-top-color": rgb(T.hair) }]]],
    ["H8", PAGE, [["#okf-filters-toggle", { height: "34px", "padding-left": "14px", "column-gap": "8px", "font-weight": "500", "font-size": 13.5 }]]],
    ["H9", PAGE, [["#okf-global-graph", { height: "34px", "border-top-color": rgb(T.blue), "background-color": rgb(T.blueSoft), color: rgb(T.blue), "font-weight": "600" }]]],
    ["H11", PAGE, [["#okf-theme-toggle", { width: "34px", height: "34px" }], ["#okf-theme-toggle path", { stroke: rgb(T.ink), fill: "none" }]]],
    ["E1", PAGE, [["#okf-explorer", { width: "290px", "border-right-color": rgb(T.hair) }]]],
    ["E2", PAGE, [["#okf-explorer .okf-explorer-head", { "padding-top": "14px", "padding-left": "16px", "padding-bottom": "10px" }],
      ["#okf-explorer .okf-explorer-title", { "font-family": /Space Mono/, "font-size": "11px", "text-transform": "uppercase", "margin-bottom": "6px", color: rgb(T.gray) }]]],
    ["E3", PAGE, [["#okf-tree-filter", { height: "34px", "font-size": 13.5, "padding-left": "10px", "border-top-color": rgb(T.hair) }]]],
    ["E4", PAGE, [["#okf-explorer .okf-type-chips", { "column-gap": "6px", "margin-top": "10px" }],
      ["#okf-explorer .okf-type-chips .okf-chip", { height: "26px", "padding-left": "10px", "font-size": "12px", color: rgb(T.gray), "border-top-color": rgb(T.hair) }]]],
    ["E5", PAGE, [["#okf-explorer .okf-tree", { "border-top-color": rgb(T.hair), "padding-top": "6px", "padding-bottom": "6px" }]]],
    ["E6", PAGE, [["#okf-explorer .okf-tree-row", { height: "30px", "column-gap": "9px", "padding-right": "14px", "border-left-width": "3px" }]]],
    ["E7", PAGE, [["#okf-explorer .okf-tree-toggle", { width: "12px", height: "30px" }]]],
    ["E8", PAGE, [["#okf-explorer .okf-tree-folder", { "font-size": 13.5, "font-weight": "600", color: rgb(T.ink) }],
      ["#okf-explorer .okf-tree-link:not([aria-current])", { "font-size": 13.5, "font-weight": "400", color: rgb(T.ink) }]]],
    ["E9", PAGE, [["#okf-explorer .okf-tree-count", { "font-family": /Space Mono/, "font-size": "11px", color: rgb(T.gray) }]]],
    ["E10", PAGE, [["#okf-explorer .okf-flag-trust", { width: "10px", height: "10px" }]]],
    ["E11", PAGE, [["#okf-explorer .okf-tree-row.okf-tree-current", { "background-color": rgb(T.blueSoft), "border-left-color": rgb(T.blue) }],
      ['#okf-explorer a.okf-tree-link[aria-current="page"]', { color: rgb(T.blue), "font-weight": "600" }]]],
    ["E12", PAGE, [["#okf-explorer .okf-explorer-foot", { "padding-top": "12px", "padding-left": "16px", "border-top-color": rgb(T.hair) }],
      ["#okf-explorer .okf-explorer-foot .okf-legend", { "font-size": "12px", color: rgb(T.gray), "row-gap": "7px" }]]],
    ["C1", PAGE, [["body > .okf-layout > main", { "padding-top": "26px", "padding-left": "48px" }], [head, { "max-width": "720px" }]]],
    ["C2", PAGE, [[`${head} .okf-crumbs ol`, { "font-family": /Space Mono/, "font-size": "12.5px", color: rgb(T.gray), "column-gap": "8px" }],
      [`${head} .okf-crumbs [aria-current="page"]`, { color: rgb(T.ink) }]]],
    ["C3", PAGE, [[`${head} h1`, { "font-family": /Inter Tight/, "font-weight": "600", "font-size": "34px", "line-height": 39.1, "margin-top": "14px", "margin-bottom": "6px" }]]],
    ["C5", PAGE, [[`${head} .okf-chips`, { "column-gap": "8px", "margin-bottom": "18px" }],
      [`${head} .okf-chip-type`, { height: "26px", "background-color": rgb(T.ink), color: rgb(T.white), "font-weight": "600", "font-size": "12px" }],
      [`${head} .okf-chip-status`, { "font-family": /Space Mono/, "border-top-color": rgb(T.hair) }],
      [`${head} .okf-chip-trust`, { "border-top-color": rgb(T.blue), color: rgb(T.blue) }]]],
    ["C6", PAGE, [["#okf-fm", { "border-top-color": rgb(T.hair), "margin-bottom": "22px" }],
      ["#okf-fm .okf-fm-head", { "background-color": rgb(T.blueSoft), "padding-top": "9px", "padding-left": "14px" }],
      ["#okf-fm .okf-fm-cell", { "column-gap": "12px", "padding-top": "8px", "padding-left": "14px" }],
      ["#okf-fm .okf-fm-key", { width: "92px", "font-family": /Space Mono/, "font-size": "12px", color: rgb(T.gray) }],
      ["#okf-fm .okf-fm-toggle", { "font-size": "12.5px", "font-weight": "600", color: rgb(T.blue) }]]],
    ["C7", PAGE, [["#okf-body p", { "font-size": "15.5px", "line-height": 25.575, "margin-bottom": "20px" }],
      ["#okf-body pre", { "background-color": rgb(T.blueSoft), "font-size": "12.5px", "padding-top": "16px" }]]],
    ["C7-h2", tocPage, [["#okf-body h2", { "font-family": /Inter Tight/, "font-size": "21px", "font-weight": "600", "margin-bottom": "8px" }]]],
    ["X1", PAGE, [["#okf-context", { width: "340px", "padding-top": "20px", "padding-left": "20px", "row-gap": "22px", "border-left-color": rgb(T.hair) }]]],
    ["X3", tocPage, [["#okf-toc a", { "font-size": 13.5, "border-left-width": "2px", "padding-left": "10px", "padding-top": "5px", "text-decoration-line": "none" }]]],
    ["X10", PAGE, [["#okf-context .okf-backlinks a.okf-row", { display: "flex", "column-gap": "9px", "font-size": 13.5, "border-bottom-color": rgb(T.hair) }]]],
  ];
}

async function probe(ctx, id, url, checks) {
  const page = await ctx.newPage();
  await page.goto(url);
  const misses = await styles(page, checks);
  await ctx.shot(page, id);
  await page.close();
  return { pass: misses.length === 0, misses };
}

// Behaviours and geometry the table above cannot express.
const specials = {
  async H2full(ctx) {
    const page = await ctx.newPage();
    await page.goto(ctx.acme + PAGE);
    const w = await page.evaluate(() => [document.querySelector("body > header.bar .bar-in").getBoundingClientRect().width, innerWidth]);
    return { pass: Math.abs(w[0] - w[1]) <= 1, widths: w };
  },
  async H7narrow(ctx) {
    const page = await ctx.newPage({ viewport: { width: 899, height: 900 } });
    await page.goto(ctx.acme + PAGE);
    const misses = await styles(page, [["#okf-tools .okf-palette-hint", { display: "none" }]]);
    return { pass: misses.length === 0, misses };
  },
  async H8count(ctx) {
    const page = await ctx.newPage();
    await page.goto(ctx.acme + PAGE);
    const atRest = await page.evaluate(() => document.querySelector("#okf-filters-toggle .okf-filters-count").hidden);
    await page.click("#okf-explorer .okf-type-chips button.okf-chip");
    const misses = await styles(page, [["#okf-filters-toggle .okf-filters-count", { display: /^(inline|block|inline-block)$/, "background-color": ctx.lib.rgb(T.blue), color: ctx.lib.rgb(T.white), "font-family": /Space Mono/ }]]);
    await ctx.shot(page, "H8");
    return { pass: atRest && misses.length === 0, atRest, misses };
  },
  async H12(ctx) {
    const page = await ctx.newPage();
    await page.goto(ctx.acme + PAGE);
    await page.keyboard.press("Tab");
    const r = await page.evaluate(() => { const a = document.activeElement; return { skip: a.matches("body > a.okf-skip"), top: a.getBoundingClientRect().top }; });
    await ctx.shot(page, "H12");
    await page.keyboard.press("Enter");
    await page.keyboard.press("Tab");
    const next = await page.evaluate(() => !!document.activeElement.closest("main"));
    return { pass: r.skip && r.top >= 0 && next, ...r, nextInMain: next };
  },
  async H13(ctx) {
    const out = {};
    for (const width of [899, 390]) {
      const page = await ctx.newPage({ viewport: { width, height: 844 } });
      await page.goto(ctx.acme + PAGE);
      out[width] = await page.evaluate(() => ({
        barHeight: document.querySelector("body > header.bar .bar-in").getBoundingClientRect().height,
        counts: getComputedStyle(document.getElementById("okf-bundle-counts")).display,
        scrollWidth: document.documentElement.scrollWidth,
      }));
      await ctx.shot(page, `H13-${width}`);
    }
    return { pass: out[899].barHeight > 52 && out[899].scrollWidth <= 899 && out[390].counts === "none" && out[390].scrollWidth <= 390, ...out };
  },
  async E13(ctx) {
    const deep = ctx.lib.pagesByDepth(ctx.siteDir).slice(-1)[0].rel;
    const page = await ctx.newPage();
    await page.goto(ctx.site + deep);
    const r = await page.evaluate(() => {
      const nav = document.getElementById("okf-explorer");
      nav.scrollTop = nav.scrollHeight;
      const n = nav.getBoundingClientRect();
      const h = nav.querySelector(".okf-explorer-head").getBoundingClientRect();
      const f = nav.querySelector(".okf-explorer-foot").getBoundingClientRect();
      return { headTop: h.top - Math.max(0, n.top), footBottom: Math.min(innerHeight, n.bottom) - f.bottom, chipsInHead: !!nav.querySelector(".okf-explorer-head .okf-type-chips") };
    });
    await ctx.shot(page, "E13");
    return { pass: Math.abs(r.headTop) <= 1 && Math.abs(r.footBottom) <= 1 && r.chipsInHead, ...r };
  },
  async C6fold(ctx) {
    const page = await ctx.newPage();
    await page.goto(ctx.acme + PAGE);
    const visible = () => page.evaluate(() => Array.from(document.querySelectorAll("#okf-fm .okf-fm-cell")).filter((c) => getComputedStyle(c).display !== "none").length);
    const folded = await visible();
    await page.click("#okf-fm .okf-fm-toggle");
    const all = await page.evaluate(() => document.querySelectorAll("#okf-fm .okf-fm-cell").length);
    const expanded = await visible();
    await ctx.shot(page, "C6-expanded");
    return { pass: folded === 4 && expanded === all && all > 4, folded, expanded, all };
  },
  async C8(ctx) {
    const page = await ctx.newPage();
    await page.goto(ctx.acme + "index.html");
    const misses = await styles(page, [["body > .okf-layout > main > .meta", { "font-family": /Space Mono/, "font-size": "13px" }], ["#okf-bundle-name", { "font-size": "13px" }]]);
    await ctx.shot(page, "C8");
    return { pass: misses.length === 0, misses };
  },
  async X4(ctx, tocPage) {
    const page = await ctx.newPage();
    await page.goto(ctx.site + tocPage);
    await page.evaluate(() => {
      const second = document.querySelectorAll("#okf-toc a")[1].getAttribute("href").slice(1);
      const h = document.getElementById(second);
      window.scrollTo(0, h.getBoundingClientRect().top + scrollY - innerHeight * 0.1);
    });
    await page.waitForTimeout(150);
    const r = await page.evaluate(() => {
      const links = Array.from(document.querySelectorAll("#okf-toc a"));
      const current = links.findIndex((a) => a.getAttribute("aria-current") === "location");
      return { current, color: current >= 0 ? getComputedStyle(links[current]).color : null };
    });
    await ctx.shot(page, "X4");
    return { pass: r.current === 1 && r.color === ctx.lib.rgb(T.blue), ...r };
  },
  async X11(ctx) {
    const page = await ctx.newPage();
    await page.goto(ctx.acme + PAGE);
    const ok = await page.evaluate(() => Array.from(document.querySelectorAll("#okf-explorer .okf-type-chips button.okf-chip"))
      .every((c) => c.querySelector("svg > title") && c.querySelector("svg > title").textContent === c.querySelector(".okf-chip-text").textContent));
    return { pass: ok };
  },
  async J(ctx) {
    const page = await ctx.newPage();
    await page.goto(ctx.acme + PAGE);
    await page.keyboard.press("Control+k");
    await page.keyboard.type("o");
    const box = await page.evaluate(() => { const r = document.querySelector(".okf-palette").getBoundingClientRect(); return { top: r.top, width: r.width }; });
    const misses = await styles(page, [
      [".okf-palette", { "box-shadow": /rgba?\(16, 16, 20/ }],
      ["body > .okf-palette-backdrop", { "background-color": /rgba\(16, 16, 20, 0\.34\)/ }],
      [".okf-palette-head", { height: "54px", "column-gap": "12px", "border-bottom-color": ctx.lib.rgb(T.hair) }],
      [".okf-palette-search", { stroke: ctx.lib.rgb(T.gray) }],
      [".okf-palette-input", { "font-size": "17px" }],
      [".okf-palette-close", { "font-family": /Space Mono/, "font-size": "11px", color: ctx.lib.rgb(T.gray) }],
      [".okf-palette-matches", { "text-transform": "uppercase", "font-size": "11px", "padding-top": "10px" }],
      ['.okf-palette-option[aria-selected="true"]', { "background-color": ctx.lib.rgb(T.blueSoft), "border-left-color": ctx.lib.rgb(T.blue) }],
      ['.okf-palette-option[aria-selected="true"] .okf-palette-title', { color: ctx.lib.rgb(T.blue), "font-weight": "600", "font-size": "15px" }],
      [".okf-palette-id", { "font-family": /Space Mono/, "font-size": "12px", color: ctx.lib.rgb(T.gray) }],
      [".okf-palette-foot", { "font-size": "12px", "column-gap": "16px", color: ctx.lib.rgb(T.gray) }],
    ]);
    const optionHeight = await page.evaluate(() => document.querySelector(".okf-palette-option").getBoundingClientRect().height);
    await ctx.shot(page, "J1-J6");
    return { pass: Math.abs(box.top - 120) <= 1 && Math.abs(box.width - 600) <= 1 && optionHeight >= 46 && misses.length === 0, box, optionHeight, misses };
  },
  async L6(ctx) {
    const out = {};
    for (const width of [1100, 1190, 1440]) {
      const page = await ctx.newPage({ viewport: { width, height: 900 } });
      await page.goto(ctx.acme + PAGE);
      out[width] = await page.evaluate(() => {
        const r = (el) => el.getBoundingClientRect();
        const e = r(document.getElementById("okf-explorer"));
        const m = r(document.querySelector("body > .okf-layout > main"));
        const c = r(document.getElementById("okf-context"));
        return { explorer: e.width, context: c.width, sideBySide: e.right <= m.left + 1 && m.right <= c.left + 1 && Math.abs(e.top - m.top) < 1 && Math.abs(c.top - m.top) < 1, scrollWidth: document.documentElement.scrollWidth };
      });
      await ctx.shot(page, `L6-${width}`);
    }
    return { pass: Object.entries(out).every(([w, v]) => v.sideBySide && Math.abs(v.explorer - 290) <= 1 && Math.abs(v.context - 340) <= 1 && v.scrollWidth <= Number(w)), ...out };
  },
  // §11.0 and A19: text tokens >= 4.5:1 on the page; shapes >= 3:1 on the
  // page and on the active row (--blue-soft); in both themes, measured.
  async tokens(ctx) {
    const page = await ctx.newPage();
    await page.goto(ctx.acme + PAGE);
    const out = {};
    let pass = true;
    for (const theme of ["light", "dark"]) {
      await page.evaluate((t) => document.documentElement.setAttribute("data-theme", t), theme);
      const c = await page.evaluate((names) => {
        const probeEl = document.createElement("span");
        document.body.appendChild(probeEl);
        const got = {};
        for (const n of names) { probeEl.style.color = `var(${n})`; got[n] = getComputedStyle(probeEl).color; }
        probeEl.remove();
        return got;
      }, ["--white", "--blue-soft", "--ink", "--gray", "--blue", "--blue-hover", "--red", "--stale", "--okf-type-0", "--okf-type-1", "--okf-type-2", "--okf-type-3", "--okf-type-4", "--okf-type-5"]);
      const ratios = {};
      for (const n of ["--ink", "--gray", "--blue", "--blue-hover", "--red", "--stale"]) {
        ratios[`${n} text`] = ctx.lib.contrast(c[n], c["--white"]);
        if (ratios[`${n} text`] < 4.5) { pass = false; }
      }
      for (const n of ["--okf-type-0", "--okf-type-1", "--okf-type-2", "--okf-type-3", "--okf-type-4", "--okf-type-5", "--blue", "--stale"]) {
        for (const bg of ["--white", "--blue-soft"]) {
          ratios[`${n} on ${bg}`] = ctx.lib.contrast(c[n], c[bg]);
          if (ratios[`${n} on ${bg}`] < 3) { pass = false; }
        }
      }
      out[theme] = ratios;
      await ctx.shot(page, `tokens-${theme}`);
    }
    return { pass, ...out };
  },
};

async function run(ctx) {
  const out = {};
  const want = (id) => ctx.wanted(id);
  const tocPage = ctx.lib.pageWithSections(ctx.siteDir, 3);
  const g = ctx.lib.guard;
  if (want("fonts")) { out.fonts = await g(() => fonts(ctx)); }
  for (const [id, rel, checks] of probes(ctx.lib.rgb, tocPage)) {
    if (!want(id)) { continue; }
    const base = rel === PAGE ? ctx.acme : ctx.site;
    out[id] = await g(() => probe(ctx, id, base + rel, checks));
  }
  const special = { H2: "H2full", H7: "H7narrow", H8: "H8count", H12: "H12", H13: "H13", E13: "E13", C6: "C6fold", C8: "C8", X4: "X4", X11: "X11", J1: "J", L6: "L6", tokens: "tokens" };
  for (const [id, name] of Object.entries(special)) {
    if (!want(id)) { continue; }
    const key = out[id] ? `${id}-${name}` : id;
    out[key] = await g(() => specials[name](ctx, tocPage));
  }
  // Control 14 and console errors, over every page this slice opened.
  if (want("requests")) {
    const pages = [];
    for (const rel of ["index.html", PAGE]) {
      const page = await ctx.newPage();
      await page.goto(ctx.acme + rel);
      pages.push(page.okfTracked);
    }
    const page = await ctx.newPage();
    await page.goto(ctx.site + tocPage);
    pages.push(page.okfTracked);
    const outside = pages.flatMap((t) => t.outside);
    const errors = pages.flatMap((t) => t.errors);
    out.requests = { pass: outside.length === 0 && errors.length === 0, outside: outside.slice(0, 10), errors: errors.slice(0, 10) };
  }
  return out;
}

module.exports = { run };
```

- [ ] **Step 4: Fill the P1.1 section of `ACCEPTANCE.md`**

In `tools/viewer-security-check/ACCEPTANCE.md`, replace the line `*(P1.1 checks)*` with:

```markdown
Tooled first (spec §12.8): build the two sites with the commands above, run
the recette, and paste its summary into the pull request:

    OKF_PLAYWRIGHT=<path to playwright-core> node tools/viewer-security-check/recette/recette.js \
      --site <tmp>/okf4net-site --acme <tmp>/acme-site --browsers chrome,edge,firefox --slices p1,p1.1

Every result must be `ok` (`RC1`–`RC11`, `fonts`, `H1`–`H13`, `E1`–`E13`,
`C1`–`C8`, `X1`, `X3`, `X4`, `X10`, `X11`, `J1`, `L6`, `tokens`,
`requests`). Then, by hand:

- [ ] Every capture of `<out>/shots/<browser>/p1.1/` set beside its mockup at 1 440 × 900 (A: `Main.dc.html`; the palette of C: `Focus.dc.html`): every visible difference is an "écart" of spec §11 or a defect.
- [ ] Installed Firefox (not Playwright's), on the OKF4net site's `index.html` and its deepest page: the Network tab shows every `assets/fonts/` request answered (or no font request at all if `okf-fonts.css` serves them), and a paragraph's computed font is Inter.
- [ ] Both themes: chips, explorer flags, type chips and the hourglass read clearly on the page and on the active row (the recette measures the ratios; this is the eye check).
- [ ] At 390 px: the header takes two lines without its counts, the frontmatter box one column, and nothing scrolls sideways.
- [ ] From the top of a page, Tab shows "Skip to content" first; Enter on it, then Tab, lands in the page, past the explorer.
- [ ] Safari (best effort): one page of each site with its fonts and shapes.
- IME and screen reader: deferred to issue #177 (A21), not blocking.
```

- [ ] **Step 5: Run the whole recette**

```bash
cd E:/Sources/okf/.claude/worktrees/viewer-interactive-spec   # or this task's worktree, rebased on the wave
R="$TEMP/okf-p11-recette"; rm -rf "$R"; mkdir -p "$R"
dotnet run --project producers/src/OkfProducer.Cli -c Release -- generate --repo . --out "$R/okf4net-bundle" --no-msbuild
dotnet run --project src/OKF4net.Render -c Release -- "$R/okf4net-bundle" --out "$R/okf4net-site"
dotnet run --project src/OKF4net.Render -c Release -- bundles/acme_retail --out "$R/acme-site"
node tools/viewer-security-check/recette/recette.js --site "$R/okf4net-site" --acme "$R/acme-site" --browsers chrome,edge,firefox,webkit --slices p1,p1.1,p2,p3 --out "$R/out"
```

Expected: for Chrome, Edge and Firefox every line `ok` (or `n/a` with its note) and the last line starting `no check failed` (WebKit is best effort: note its failures, do not block on them); `p2` and `p3` reported `skipped`. A failure is a defect of the task that owns the element (the Fidélité maquette table names it): fix it in that task's files, rerun `npm test` and this step, and commit the fix on its own with a message naming the §11 id (e.g. `fix(viewer): E6 rows are 30 px in Firefox`). Then do the hand checks of Step 4 and record the date, the browser versions and the result in the pull request description — never in the repository (§12.8).

- [ ] **Step 6: Final verification of the slice**

```bash
dotnet build OKF4net.sln
dotnet test OKF4net.sln
dotnet format OKF4net.sln --verify-no-changes
cd tools/viewer-security-check && npm test && cd ../..
```

Expected: build without warnings; the whole test suite green (the container integration tests are skipped as usual); format clean; harness `N passed, 0 failed`.

- [ ] **Step 7: Commit**

```bash
git add tools/viewer-security-check/recette/lib.js tools/viewer-security-check/recette/p1.js tools/viewer-security-check/recette/p1-1.js tools/viewer-security-check/ACCEPTANCE.md
git commit -m "test(viewer): recette ports P1's checks and measures every P1.1 mockup element, keyed by spec §11 id

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

## Self-review (done while writing; kept for the executors)

- **Spec coverage.** §9's P1.1 list: index v2 and measurement (Task 4); shapes and legends (Task 6); header of the three views and the graph page name (Tasks 5, 11); centre column (Tasks 5, 12, 15); explorer (Task 13); current section (Task 8); "Referenced by" glyphs and count (Tasks 12, 15); palette as C (Task 14); fonts, `file://`, `NOTICE`, `CLAUDE.md` (Tasks 9, 10, 16); tokens and typography (Task 3); shared components (Task 3); plumbing — generic assets (Task 2), script table and P2/P3 markers (Task 11), reserved `viewer.css` sections and `ACCEPTANCE.md` sections and the case loader (Task 1), recette (Tasks 9, 18). §12.7's P1 case updates: filter name (Task 13), v1 overrides (Task 4), `table.frontmatter` (Task 12), `treeLink` (Task 13), `.okf-stale` (Task 13), Close/Esc (Task 14 keeps `.okf-palette-close`, name "Close"). §3.4's comment on `GuardNoCaseCollisions` (Task 2). `ci.yml` (Task 17). `CHANGELOG` (Task 17). Controls 10, 11, 14 (Tasks 6, 12/15, 18).
- **Contracts kept exactly.** `OkfShapes` signature and table (§12.2); index v2 keys appended at the end of their objects (§12.1); `RenderDocumentStart(site, view, title, prefix, conceptId?)`, `RenderHeader(site, view, prefix, conceptId?)`, `ScriptTag(prefix, name)`, `HtmlEscape`, `RootPrefix` (§12.0, §12.3); header markup and ids of §12.3; `PageScripts` order of §12.6; markers `// P2: local graph`, `// P3: graph page (§12.5)`, `// P3: RenderGraph (§12.5)`; `run.js` helper object of §12.7 (the fourteen names, frozen).
- **Names used across tasks.** `SiteIndex.DescriptionLimit`, `SiteIndex.OtherSlot`, `SiteModel.StripDuplicateTitle`, `SiteModel.BundleNameOf`, `SiteModel.FreeGraphPagePath`, `HtmlWriter.ViewKind`, `HtmlWriter.GraphPagePathOf`, `HtmlWriter.Counts`, `HtmlWriter.TrustText`, `HtmlWriter.PageScripts`, `ViewerAssets.Paths/Text/Bytes`, `ViewerPageHead`, `ViewerFrontmatterEntry.Extra/Structured`, and the class list of `p11-chrome-classes.md` are each defined in exactly one task and only consumed elsewhere.
