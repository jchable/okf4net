# Interactive viewer — P2 (local graph) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every concept page of an `okf-render` site a "Neighbourhood" section in its context panel: the concept and its neighbours at 1 or 2 hops drawn as rings in an SVG built by code, capped at 40 nodes with "+N omitted", an equivalent accessible list, the legend line and "Open in graph".

**Architecture:** One new classic script, `assets/okf-local.js`, in two parts. `window.OkfLocal` is pure (no DOM, no clock, no randomness): from the site index alone it computes the drawn nodes and their ring positions, every index edge between them (solid or dashed, paired opposites offset ± 3) and the full neighbour list, in at most four counted passes over the edges. The page part runs only under `<html data-okf-view="page">` and draws that result into `#okf-context` right after `#okf-toc`, with every shape from P1.1's `OkfShapes` at the `local` / `localCenter` sizes of spec §12.2. `HtmlWriter` gains one line (P1.1's `PageScripts` table, under its `// P2: local graph` marker), `viewer.css` gains P2's reserved section, the jsdom harness gains `cases/p2.js` and `p2-` fixtures, the manual-run recette gains `recette/p2.js`.

**Tech Stack:** C# 14 / .NET 10, xunit, BCL only (`OKF4net.Viewer`); plain ES2018 classic scripts; Node 22 + jsdom 29 (existing `tools/viewer-security-check/`); Playwright (`playwright-core`, resolved at run time by P1.1's `recette.js`, never a repo dependency) for the recette only.

**Spec:** `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md` (revision 6; brought in line with revision 7, `f84c990`, by this plan's r2). This plan implements slice **P2** (§9): §4.3 (local graph), §4.5 (application SVG), §6 (equivalent list), §7 control 12 plus controls 3 and 7 for the local graph, §8 (local graph keyboard contract), §11.4 X5–X9, §12.4, and P2's rows of §12.0. It **consumes** P1.1's contracts (§12.0–§12.3, §12.6–§12.8) without changing them; P1.1 is not implemented when this plan is written, so every task names the exact §12 item it codes against.

## Revision history

- **r1 (2026-10-07)** — first version, written in parallel with the P1.1 and P3 plans from spec revision 6.
- **Revision r2 (2026-10-07): applied pre-flight findings X10, X11, X14, X16, X17, X20; aligned with spec revision 7 (`f84c990`).**
  - X16 (T3): the "old drawing" check counts only `#okf-local-graph .okf-local-canvas > svg`, so T4's list glyphs (`svg.okf-glyph`) no longer turn it red.
  - X17 (T5): the hop buttons use `font-family: inherit` (plus `font-weight`/`line-height: inherit`), not the font shorthand set to inherit, after which jsdom drops `font-size: 12px` (the probe read 16px); the probe's expected 12px is unchanged.
  - X10, X11 (T6, rulings 11): `recette/p2.js` uses P1.1's `ctx` as spec §12.8 r7 fixes it — `ctx.newPage()`, `ctx.browserName`, `ctx.shot(page, id)`, `ctx.wanted(id)`, `ctx.lib.rgb` and `ctx.lib.contrast` (no local copies) — and its `pass: null` results are reported "n/a" by P1.1's driver, so T6 Step 4's expected outcome holds.
  - X20 (T7, ruling 10): spec r7 §12.0 settles it — P2 edits no documentation; T7 Step 5 hands the controller the exact wording for `CLAUDE.md`, the viewer README and `CHANGELOG.md`, which the controller applies after P2 and P3 are merged (P1.1 plan, Task 0 Step 6).
  - X14: the fixture-naming rule P1.1 Task 4 states is now a Global Constraint here too (P2's fixtures already follow it).

## Task dependency graph

```text
T1 (OkfLocal, pure + its cases) ............ can start NOW, in parallel with P1.1
        |
P1.1 merged on feat/viewer-interactive-p1 (§12.0-§12.3, §12.6-§12.8 delivered)
        |
T2 (one line in PageScripts + xunit) --> T3 (fixtures, section, SVG, toggle, foot)
  --> T4 (equivalent list, +N omitted, keyboard, hostile pages) --> T5 (CSS section, chrome probe)
  --> T6 (recette p2.js, ACCEPTANCE ## P2) --> T7 (full verification, hand-off)
```

- **Before P1.1 lands:** only **T1**. It creates two files no other slice touches (`Assets/okf-local.js`, `cases/p2.js`) and needs nothing but the index shape of §12.1, which its cases build themselves. Its cases run under P1.1's case loader (the first task of P1.1, §12.7); until that loader is on the branch, T1 Step 4 runs them through a five-line stub driver.
- **After P1.1 is merged:** T2 to T7, in order. They are sequential **inside** P2 because `okf-local.js` and `cases/p2.js` are shared by T1, T3, T4 and T5. T6 only reads the DOM contract this plan fixes (T3/T4 **Produces**), so it may be *written* in parallel with T4–T5 in the same worktree, but it is *run* after T5.
- **P2 and P3 in parallel:** both branch from P1.1's result into their own worktrees. P2 writes only the files and markers §12.0 gives it (table below); P3 writes none of them. No merge conflict is expected when both return to `feat/viewer-interactive-p1`.

## File ownership and merge zones (spec §12.0)

P2 writes **only** these. Anything else is read, never edited; a task that seems to need another file is a spec change — stop and ask.

| File | P2's zone | Task |
| --- | --- | --- |
| `src/OKF4net.Viewer/Assets/okf-local.js` | whole file (P2 creates it) | T1, T3, T4 |
| `src/OKF4net.Viewer/HtmlWriter.cs` | **one line**, `"okf-local.js",`, directly under P1.1's `// P2: local graph` marker in `PageScripts` | T2 |
| `src/OKF4net.Viewer/Assets/viewer.css` | the line `/* (P2 rules) */` under P1.1's `/* === P2: local graph === */`, replaced by P2's rules | T5 |
| `tests/OKF4net.Tests/Viewer/HtmlWriterLocalGraphTests.cs` | whole file (P2's own xunit class, §7) | T2 |
| `tools/viewer-security-check/cases/p2.js` | whole file | T1, T3, T4, T5 |
| `tools/viewer-security-check/fixtures/hostile-bundle/p2-local/` and `p2-chrome-classes.md` | P2's fixtures (`p2-` prefix, §12.7) | T3, T5 |
| `tools/viewer-security-check/ACCEPTANCE.md` | the line `*(P2 checks)*` under P1.1's `## P2`, replaced | T6 |
| `tools/viewer-security-check/recette/p2.js` | whole file | T6 |

Not P2's (spec §12.0): `run.js`, `check-index.js`, the harness README, `recette/recette.js`, `lib.js`, `ViewerAssets.cs`, the csproj, `ci.yml`, `CLAUDE.md`, the viewer README, `NOTICE`, `CHANGELOG.md` (P1.1 writes the whole P2 line). T7 checks that what those owners wrote matches what P2 delivers and reports any gap; it does not edit them.

## Contracts consumed from P1.1

| § | What P2 relies on |
| --- | --- |
| §12.1 | `window.OKF_INDEX` version 2: `concepts[]` (`id`, `title`, `type`, `path`, `typeIndex`, …), `ghosts[]` (`id`), `edges[]` = `[from, to, count, toGhost]` (merged, concept targets then ghosts), `tree`, `types`; `OkfSite.readIndex` accepts exactly that. |
| §12.2 | `OkfShapes.SIZES.local[kind]`, `SIZES.localCenter[kind]` (`{ size, stroke, dash, ring, focus }`), `kindOf(index, position)`, `typeLabel(name)`, `node(kind, cx, cy, size, options)` → `<g class="okf-node">` (focus rect, ring rect, then the shape; no `<title>`, no `<text>`), `icon(kind, context, title)` → `<svg class="okf-glyph" aria-hidden="true">`; P1.1's CSS shows `.okf-node-ring` only under `.okf-selected` and colours `okf-shape-*`, `okf-ghost-mark` from the tokens. |
| §12.3 | `<html data-okf-view="page" data-okf-concept="<id>" data-okf-root="../…">`; `#okf-tools` holding `a#okf-global-graph` whose `href` is the graph page + `#<id>` (the only source of the graph page's name). |
| §12.0, §12.6 | `HtmlWriter.PageScripts` with the marker `// P2: local graph` at its end; `HtmlWriter.WriteAssets` writing every embedded `Assets/` resource (so `okf-local.js` needs no other C# change); `viewer.css` section `/* === P2: local graph === */` with `/* (P2 rules) */`; shared components `.okf-section-title` and `.okf-row` already styled under `#okf-context`; `#okf-context` laid out as sections 22 apart (X1); tokens `--edge`, `--ghost`, `--blue-hover` (§11.0) — P2 adds no token. |
| §12.7 | The case loader in `run.js` calls `require("cases/p2.js").register(h)`; `h` carries `check`, `assert`, `checkAsync`, `openPage` (options `blocked`, `override`, `beforeParse`, `hash`, `now`), `navigations`, `unwrapMedia`, …; `ACCEPTANCE.md` has `## P2` with `*(P2 checks)*`. |
| §12.8 | `recette/recette.js --slices p2` loads `recette/p2.js` and awaits `run(ctx)`; `ctx` (P1.1's `lib.context`, spec r7): `browserName`, `site`, `acme` (`file://` URLs ending `/`), `siteDir`, `acmeDir`, `wanted(id)`, `newPage({ viewport, colorScheme })` (1 440 × 900, light by default; `page.okfTracked` holds `errors`, `outside`, `failed`), `shot(page, id)`, `close()` (called by the driver), `lib` (`recette/lib.js`: `rgb(hex)`, `contrast(a, b)`, `parseColor`, `readIndex`, `pagesByDepth`, `siteUrl`, `guard`, …). A result `pass: null` is printed "n/a" and never counted as a failure. |

## Global Constraints

- Work in the dedicated worktree `.claude/worktrees/viewer-p2`, branch `feat/viewer-p2` (T1 Step 0). Stage files by name; never `git add -A`. Never `git stash` bare.
- **No change to any public API of `src/OKF4net/`** (spec §2.1), no new `PackageReference`, no new JS dependency (the harness keeps `jsdom` only; Playwright is resolved at run time by P1.1's recette, never added to `package.json`).
- **`viewer.js` is not modified** (spec §4.1): T7 proves it with `git diff --exit-code`.
- **Classic scripts only**, never `type="module"`; `okf-local.js` is one IIFE that checks its globals (`OkfSite`, `OkfShapes`, the index) and returns without error when one is missing (§12.6).
- **Application SVG** (§4.5, §12.2): every SVG element by `document.createElementNS("http://www.w3.org/2000/svg", name)` with `name` in `svg g circle rect path line text title defs marker`; attributes only from `viewBox width height class aria-hidden focusable role aria-label tabindex id cx cy r x y x1 y1 x2 y2 d stroke-width stroke-dasharray text-anchor dominant-baseline marker-end refX refY markerWidth markerHeight orient transform`; `id` values fixed `okf-…`; every coordinate checked `Number.isFinite` (else `TypeError`); colours never in attributes, only fixed classes (`okf-local-…` for P2's own, §12.2); bundle text only through `textContent` (`<title>`, `<text>`) or `aria-label`; URLs only from index paths through `OkfSite.resolve`. Shapes of type and ghost come from `OkfShapes` only.
- **No object keyed by a bundle string**: arrays, typed arrays and `Map`; node keys are integers (concept position, then `concepts.length` + ghost position).
- **One comparator**: the JS never compares ids; order = index position (§3.1).
- **Anchoring** (§12.6): every CSS selector of P2 starts with `#okf-context`; every DOM lookup of `okf-local.js` goes through `document.getElementById` (`okf-context`, `okf-toc`, `okf-tools`) or `#okf-tools`'s own `querySelector`, never a class query on `document`.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`; C#: file-scoped namespace, nullable, `TreatWarningsAsErrors`; `dotnet format OKF4net.sln --verify-no-changes` must pass.
- **Never type a `\uXXXX` escape** into a file through an editor tool: it is decoded on write. Build such characters at run time: the middle dot of "List · N" and of the legend is `String.fromCharCode(0xb7)` in every JS file of this plan.
- **Harness**: run it with `npm test` (its `pretest` regenerates `.generated/hostile-site/`); `node run.js` alone skips `pretest` and reuses a stale site. Async cases are bounded by `run.js`'s 10 s timer and fail on any page script error raised after load: never await an event that may not come; jsdom provides `requestAnimationFrame` under `pretendToBeVisual`, but `okf-local.js` draws synchronously and needs none. A case never hard-codes a count, a type rank or a fixture size: it reads `window.OKF_INDEX` or the synthetic index it builds (§12.7).
- **Fixture names** (pre-flight X14, the rule P1.1 Task 4 states for its own fixtures): no fixture title or id starts with `t`, or contains `foo`, `bar` or `edge`, and no id sorts between `foo` and `foo/bar` — P1's palette and explorer cases rely on those queries (a P3 fixture titled "Twin A" took the palette's first tier for "t" and turned a P1 case red). Every case name is unique across all slices (P1.1's loader fails the run on a duplicate).
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Rulings taken (spec ambiguities, most conservative reading — reported to the controller)

1. **The cap of 40 counts the centre** (§4.3, A11 say "40 nœuds"): at most 39 neighbours are drawn; "+N omitted" counts the neighbours not drawn.
2. **Order of the cap and of the angles between a concept and a ghost.** §4.3 says "ordre `ConceptId.CompareTo`", but §3.1 forbids the JS to reimplement the comparator and the index sorts concepts and ghosts in two separate arrays: no position compares a concept id with a ghost id. Ruling: inside each hop, concepts in index order, then ghosts in index order — the node order §12.5 already fixes for `OkfSim`. **Spec fix requested** (§4.3 should say "position dans l'index, concepts puis fantômes").
3. **Relations named by the equivalent list** (§6 "mêmes relations", X9 "relation"): a direct neighbour reads "links to", "referenced by" or "links to · referenced by" (the centre's point of view, X8's words); a second-hop neighbour reads "2 hops via `<id>`", `<id>` being the first direct neighbour (index order) it is reached from. Edges between two neighbours are drawn but not spelled out in the list (they are on each neighbour's own page). **Spec precision requested.**
4. **A self-link** is not a neighbour, not drawn and not listed (it has no direction to draw; §12.5 already excludes loops from `OkfSim`).
5. **No neighbour**: the section is not created at all (§12.4 "caché"); `#okf-context` is left as P1/P1.1 left it.
6. **Ghost label and list row**: label = last segment of the id in `--ghost`; `<title>` "absent: `<id>`"; list row "absent: `<id>`", a `<span>`, no link (wording of G13/G17). All local edges are `--edge` (X7), including those to a ghost (G12's red dashes are B's).
7. **"+N omitted"** (§4.3 "« +N omis » et un lien vers la liste complète") is one `<button>` in the frame, bottom right, that opens the list and focuses its summary; it is HTML, not SVG text (the SVG is an image, §8).
8. **Geometry not fixed by the spec**: the mockup's `viewBox="0 0 298 248"`, centre (149, 118), one ring rx 127 / ry 95 at 1 hop (and at 2 hops when the second hop draws nothing), two rings rx 106 / ry 72 and rx 134 / ry 98 at 2 hops (the mockup's half-extents are not radii; these constants are chosen, and `cases/p2.js` pins, for every node count up to the cap, that no shape (reach 13) or its label (14 under, 3 of descender) leaves the view, nor touches the centre's selection square (± 20) or its label band x 89..209, y 140..156 — any inner ellipse smaller than about rx 100 / ry 72 puts a node under that label for some node count); angle of the j-th of m nodes `−π/2 + π/m + 2πj/m` (four neighbours land on one diagonal each, clockwise from the top); an edge is trimmed by `size / 2 + 1` at each end (`+ 7` for the centre, to clear its selection square), untrimmed when the two reaches overlap. Labels are always under the shape (X7), where A puts the top row's labels above — écart, X7 wins.
9. **Arrow marker** = the mockup's (`viewBox 0 0 10 10`, `refX 9`, `refY 5`, `markerWidth 7`, `markerHeight 7`); "flèche 7" read as `markerWidth 7`. `markerUnits` is outside the fixed vocabulary, so it renders at 7 × 1.4. `orient="auto"` (A's `auto-start-reverse` is identical on `marker-end`). `okf-local-arrow` and `okf-local-arrow-in` are the same arrow; the second exists for dashed edges (§12.4).
10. **Documentation**: the dispatch brief asks P2 for `CLAUDE.md` / README / `CHANGELOG` touch-ups, but §12.0 gives `CLAUDE.md`, the viewer README and `NOTICE` to P1.1 then P3, and the whole `CHANGELOG` entry to P1.1 (one line per slice). Ruling: §12.0 wins; P2 edits none of them; T7 checks what the owners wrote against P2's delivery and hands the exact wording of any missing line to the controller. **Settled by spec r7 §12.0 and §12.8**: P2 hands its text over in its final report (T7 Step 5); the controller applies it after P2 and P3 are merged, outside their worktrees (P1.1 plan, Task 0 Step 6).
11. **Recette `ctx`** — **settled by spec r7 §12.8**, which adopts P1.1's `lib.context`: `recette/p2.js` opens its pages with `ctx.newPage({ viewport, colorScheme })`, names the browser by `ctx.browserName`, writes captures with `ctx.shot(page, id)` (under `--out/shots/<browser>/p2/<id>.png`, 1 440 × 900), honours `ctx.wanted(id)`, and takes `rgb` and `contrast` from `ctx.lib` (no local copies). There is no shared page and no `--out` directory in `ctx`. A `pass: null` result (not applicable, with a `note`) is printed "n/a" by P1.1's driver and never counted as a failure.
12. **Load-time cost**: §3.6 builds graph structures on demand; the local graph is visible on load, so the first ring is computed on load by scanning the edges (no adjacency structure is kept); the second hop is computed when "2 hops" is pressed.

## Fidélité maquette (§11 elements owned by P2)

| §11 | Element (values from §11.4) | Task | Proved by |
| --- | --- | --- | --- |
| X5 | "Neighbourhood" title + "1 hop" / "2 hops" adjacent buttons, h 24, padding 0 10, 12; pressed `--blue` / `--white` 600; unpressed `--white`, border `--hair`, `--gray`; 1 hop by default, not remembered | T3 (DOM, `aria-pressed`), T5 (CSS) | jsdom: "1 hop by default, 2 hops adds the second ring…" (T3), "its rules are anchored…" (T5, height 24); recette `X5` (sizes, colours, adjacency, reload) |
| X6 | Frame: border `--hair`, height 250, SVG full width | T3, T5 | jsdom: T5 probe (`height: 250px`); recette `X6` |
| X7 | Centre = its type's shape at `localCenter` (square 28), selection square side + 12, stroke 2, `--blue`, label Space Mono 10.5 700 `--ink`; neighbours at `local` (circle 20, diamond 25.5, triangle 22), label = last id segment, Space Mono 10 `--ink`, full id in `<title>`; ghost at `local` / `ghost`; label baseline `cy + size/2 + 14` (centre `+ 6 + 14`); edges `--edge` 1.4, arrow 7 to the target, **solid** unless the target is closer to the centre than the source, then **dashed 4 3** | T1 (geometry, dashed rule), T3 (drawing), T5 (label fonts, `--edge`) | jsdom: T1 "membership is undirected…", T3 "shapes are OkfShapes nodes at the local and localCenter sizes…", "every index edge between drawn nodes is drawn, dashed when…"; recette `X7` (`getBBox`, computed fonts and colours, both themes) |
| X8 | Foot 12 `--gray`: "solid = links to · dashed = referenced by"; right: "Open in graph" 600 `--blue`, no underline, `href` = `#okf-global-graph`'s, fragment included | T3, T5 | jsdom: T3 "Open in graph repeats the header's…", "…is left out"; recette `X8`, `P2-6` |
| X9 | "+N omitted" in the frame above 40 nodes; "List · N neighbours" collapsible; every neighbour as `.okf-row` with icon glyph, id and relation | T1 (cap, order), T4 (list, button), T5 (CSS) | jsdom: T1 "the cap draws 40 nodes…", T4 "over the cap, +N omitted opens the full list…", "the drawing is an image with no tab stop…"; recette `X9`, `P2-7` |

Consumed, not delivered by P2: X1 (panel width and section spacing, P1.1), X2 and `.okf-section-title` (P1/P1.1), X10 and `.okf-row` (P1.1), H9's `href` (P1.1), the `local` / `localCenter` rows of `SIZES` (P1.1). Écarts recorded: labels always under the shape (A puts the top row above; X7 wins); edges trimmed at the shapes' edges (A ends some lines at node centres); "+N omitted" and the list exist outside the mockup (A30).

## Review Focus

1. **Bundle text in the drawing and the list** (a title `<img src=x onerror=…>`, `</title><script>`, `"><svg onload=…>`): nothing becomes live; the SVG holds only the fixed vocabulary — pinned by T4 "hostile titles and ids named after Object.prototype members stay inert and distinct" (with `assertFixedSvg`).
2. **Ids that are valid `ConceptId`s and dangerous keys** (`__proto__`, `constructor`, `toString`, `hasOwnProperty`, `valueOf`, also as centre): distinct nodes and rows, `Object.prototype` untouched — pinned by T1 "ids named after Object.prototype members are ordinary concepts" and T4's hostile page case.
3. **Degenerate graphs** — no neighbour, a self-link only, one neighbour: no section, no `NaN`, centre at the middle — pinned by T1 "no neighbour gives the centre alone; one neighbour gives one finite node…", "a self-link is no neighbour…", and T3 "nothing on the index, without an index or OkfShapes, or without a neighbour".
4. **A hub with hundreds of neighbours, and the 2-hop explosion**: exactly 40 nodes, direct neighbours first, index order, ghosts after concepts; work linear in the edges, **counted, never timed** — pinned by T1 "the cap draws 40 nodes…", "two hops around a hub cost linear work…", T4 "over the cap, +N omitted…".
5. **Self-links, repeated and opposite edges, ghosts**: a loop is no neighbour, a repeated pair is one line, A → B and B → A are two lines 6 apart, the second hop never starts from a ghost, a ghost never navigates — pinned by T1 "a self-link is no neighbour, opposite edges are paired and offset…", "the second hop never starts from an absent concept", T3 "absent concepts are drawn and listed but never navigable", "every index edge between drawn nodes is drawn…" (no two lines retrace each other).
6. **Non-finite coordinates and damaged index records**: every number in the SVG is finite, a damaged edge is skipped — pinned by T1 "segment never returns a non-finite coordinate", "a damaged edge record is skipped…", and `assertFixedSvg` in T3/T4.
7. **Chrome classes worn by body content** (`<code class="okf-local-canvas">`): unstyled, while the real section keeps its style; no SVG in `#okf-body` — pinned by T5 "its rules are anchored to #okf-context…" and T3's structure case.
8. **`viewer.js` unchanged** — pinned by T7 Step 4 (`git diff --exit-code`) and P1's existing "viewer.js alone" case.

---

### Task 1: `OkfLocal`, the pure local-graph module, and its cases

**Files:**
- Create: `src/OKF4net.Viewer/Assets/okf-local.js`
- Create: `tools/viewer-security-check/cases/p2.js`

**Interfaces:**
- Consumes: the index shape of §12.1 only (`concepts[]`, `ghosts[]`, `edges[]` as `[from, to, count, toGhost]`); the cases build their own indexes. §12.7 `register(h)` with `h.check`, `h.assert`.
- Produces (global `window.OkfLocal`, frozen):
  - `CAP` — `40`, nodes drawn, the centre included.
  - `VIEW` — `{ width: 298, height: 248, cx: 149, cy: 118 }`.
  - `build(index, centre, hops)` → `{ centre, hops, nodes, edges, list, total, omitted, work }` where `centre` is a concept position, `hops` is `1` or `2` (else `TypeError`); `nodes[0]` is the centre; each node `{ key, dist, x, y }` (`key` = concept position, or `concepts.length` + ghost position; `dist` 0, 1 or 2; `x`, `y` rounded to hundredths); each edge `{ from, to, count, dashed, paired }` (`from`/`to` index `nodes`); each list entry, frozen, `{ key, dist, rel, via }` (`rel`: 1 the centre links to it, 2 it links to the centre, 3 both, 0 at hop 2; `via`: at hop 2 the smallest direct-neighbour concept key it is reached from, else −1); `total` = `list.length`; `omitted` = neighbours not drawn; `work` = counted units, ≤ `4 × edges + 2 × nodes`.
  - `segment(x1, y1, r1, x2, y2, r2, shift)` → `{ x1, y1, x2, y2 }` or `null` (coincident ends); `TypeError` on a non-finite argument.
  - `lastSegment(id)` → the text after the last `/`.
- Produces (harness): `cases/p2.js` exporting `register(h)`; helpers `okfLocal()` (bare window with `okf-local.js`), `siteIndex(ids, links, ghostIds, titles)` (a v2 index), `treeOf(ids)`.

- [ ] **Step 0: Create the P2 worktree**

```bash
cd E:/Sources/okf
git worktree add .claude/worktrees/viewer-p2 -b feat/viewer-p2 feat/viewer-interactive-p1
cd E:/Sources/okf/.claude/worktrees/viewer-p2/tools/viewer-security-check
npm ci
git -C E:/Sources/okf/.claude/worktrees/viewer-p2 rev-parse HEAD
```

Expected: `Preparing worktree (new branch 'feat/viewer-p2')`, `npm ci` adds `jsdom`, and a commit SHA. **Write that SHA down as `P2_BASE`**: T7 diffs against it. Every later command of this plan runs in `E:/Sources/okf/.claude/worktrees/viewer-p2`.

- [ ] **Step 1: Write the failing cases**

Create `tools/viewer-security-check/cases/p2.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Slice P2 (local graph) cases. run.js's case loader calls register(h) with
// the frozen helpers (spec §12.7). Pure cases load the real okf-local.js into
// a bare window and call window.OkfLocal directly. Page cases open pages of
// the site okf-render generates from fixtures/hostile-bundle/ (its p2-local/
// folder and the p2- fixtures), sometimes under a synthetic index served in
// place of assets/okf-index.js. Expected counts come from the index a case
// reads or builds, never from a fixture's size; work is counted, never timed.
"use strict";
const fs = require("fs");
const path = require("path");
const { JSDOM } = require("jsdom");

const LOCAL_JS = path.join(__dirname, "..", "..", "..", "src", "OKF4net.Viewer", "Assets", "okf-local.js");

// A bare window that loaded okf-local.js alone: no OkfSite, no OkfShapes and
// no data-okf-view, so its page part returns at once and only OkfLocal exists.
function okfLocal() {
  const dom = new JSDOM("<!doctype html><html><body></body></html>", { runScripts: "outside-only" });
  dom.window.eval(fs.readFileSync(LOCAL_JS, "utf8"));
  return dom.window;
}

// The tree a v2 index carries (destination + children, spec §3.2), so the
// P1 and P1.1 scripts of a page served a synthetic index get a sound one.
function treeOf(ids) {
  const root = { children: new Map() };
  ids.forEach((id, position) => {
    let node = root;
    for (const segment of id.split("/")) {
      if (!node.children.has(segment)) { node.children.set(segment, { name: segment, concept: -1, children: new Map() }); }
      node = node.children.get(segment);
    }
    node.concept = position;
  });
  const freeze = (node) => Array.from(node.children.values())
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
    .map((child) => ({ name: child.name, concept: child.concept, children: freeze(child) }));
  return freeze(root);
}

// A version-2 site index (spec §12.1) over `ids`, in the order given: that
// order IS index order for okf-local.js, which never compares ids. `links`
// are [from, to] concept positions, or [from, to, "ghost"] with `to` a
// position in `ghostIds`. `titles[k]` replaces the title of concept k.
function siteIndex(ids, links, ghostIds = [], titles = []) {
  const concepts = ids.map((id, k) => ({
    id, title: titles[k] === undefined ? id : titles[k], type: "Note", tags: [], path: `${id}.html`,
    trust: "unverified", staleAfterMs: null, staleAfterDate: null, typeIndex: 0, description: "",
  }));
  return {
    version: 2,
    concepts,
    ghosts: ghostIds.map((id) => ({ id })),
    edges: links.map(([from, to, ghost]) => [from, to, 1, ghost === "ghost" ? 1 : 0]),
    tree: treeOf(ids),
    types: [{ name: "Note", count: ids.length, slot: 0 }],
  };
}

function registerPure(h) {
  const { check, assert } = h;

  check("local graph: build is deterministic, finite, and inside the view", () => {
    const index = siteIndex(["c", "a", "b", "d", "e"], [[0, 1], [2, 0], [1, 3], [3, 4], [0, 0, "ghost"]], ["gone"]);
    const one = okfLocal().OkfLocal;
    const other = okfLocal().OkfLocal;
    for (const hops of [1, 2]) {
      const first = JSON.stringify(one.build(index, 0, hops));
      assert(first === JSON.stringify(one.build(index, 0, hops)), `hops ${hops}: two builds in one window differ`);
      assert(first === JSON.stringify(other.build(index, 0, hops)), `hops ${hops}: two windows disagree`);
      for (const node of one.build(index, 0, hops).nodes) {
        assert(Number.isFinite(node.x) && Number.isFinite(node.y), `hops ${hops}: node ${node.key} at (${node.x}, ${node.y})`);
        assert(node.x >= 0 && node.x <= one.VIEW.width && node.y >= 0 && node.y <= one.VIEW.height,
          `hops ${hops}: node ${node.key} at (${node.x}, ${node.y}) is outside the ${one.VIEW.width} x ${one.VIEW.height} view`);
      }
    }
  });

  check("local graph: membership is undirected, edges keep their direction, links back to the centre are dashed", () => {
    const { OkfLocal } = okfLocal();
    // c -> a, b -> c, a -> d: a and b are direct neighbours (one linked to,
    // one linking in); d is two hops away, through a.
    const index = siteIndex(["c", "a", "b", "d"], [[0, 1], [2, 0], [1, 3]]);
    const one = OkfLocal.build(index, 0, 1);
    const list1 = JSON.stringify(one.list.map((e) => [e.key, e.dist, e.rel]));
    assert(list1 === "[[1,1,1],[2,1,2]]", `1 hop list (key, dist, rel): ${list1}`);
    const edges1 = JSON.stringify(one.edges.map((e) => [one.nodes[e.from].key, one.nodes[e.to].key, e.dashed]));
    assert(edges1 === "[[0,1,false],[2,0,true]]", `1 hop edges (from, to, dashed): ${edges1}`);
    const two = OkfLocal.build(index, 0, 2);
    const list2 = JSON.stringify(two.list.map((e) => [e.key, e.dist, e.via]));
    assert(list2 === "[[1,1,-1],[2,1,-1],[3,2,1]]", `2 hops list (key, dist, via): ${list2}`);
    const edges2 = JSON.stringify(two.edges.map((e) => [two.nodes[e.from].key, two.nodes[e.to].key, e.dashed]));
    assert(edges2 === "[[0,1,false],[2,0,true],[1,3,false]]", `2 hops edges: ${edges2}`);
  });

  check("local graph: every index edge between drawn nodes is drawn, including between two neighbours", () => {
    const { OkfLocal } = okfLocal();
    // c -> a, c -> b, a -> b: a -> b joins two nodes of the same ring.
    const r = OkfLocal.build(siteIndex(["c", "a", "b"], [[0, 1], [0, 2], [1, 2]]), 0, 1);
    const edges = JSON.stringify(r.edges.map((e) => [r.nodes[e.from].key, r.nodes[e.to].key, e.dashed]));
    assert(edges === "[[0,1,false],[0,2,false],[1,2,false]]", `edges: ${edges}`);
  });

  check("local graph: a self-link is no neighbour, opposite edges are paired and offset, repeated records add up", () => {
    const { OkfLocal } = okfLocal();
    const index = siteIndex(["a", "b"], [[0, 0], [0, 1], [1, 0]]);
    index.edges.push([0, 1, 2, 0]); // a damaged index repeating a pair: its count adds up
    const r = OkfLocal.build(index, 0, 1);
    assert(r.total === 1 && r.list[0].key === 1 && r.list[0].rel === 3, `list: ${JSON.stringify(r.list)}`);
    const edges = JSON.stringify(r.edges.map((e) => [r.nodes[e.from].key, r.nodes[e.to].key, e.count, e.paired]));
    assert(edges === "[[0,1,3,true],[1,0,1,true]]", `edges (from, to, count, paired): ${edges}`);
    const self = OkfLocal.build(siteIndex(["s"], [[0, 0]]), 0, 2);
    assert(self.total === 0 && self.nodes.length === 1 && self.edges.length === 0, `a self-link only: ${JSON.stringify(self)}`);
    // Each direction moves 3 along its own normal: the two lines are 6 apart.
    const there = OkfLocal.segment(0, 0, 0, 10, 0, 0, 3);
    const back = OkfLocal.segment(10, 0, 0, 0, 0, 0, 3);
    assert(there.y1 === 3 && there.y2 === 3 && back.y1 === -3 && back.y2 === -3, `offsets: ${JSON.stringify([there, back])}`);
  });

  check("local graph: no neighbour gives the centre alone; one neighbour gives one finite node in the view", () => {
    const { OkfLocal } = okfLocal();
    const r = OkfLocal.build(siteIndex(["only"], []), 0, 2);
    assert(r.total === 0 && r.omitted === 0 && r.nodes.length === 1 && r.edges.length === 0 && r.list.length === 0, JSON.stringify(r));
    assert(r.nodes[0].x === OkfLocal.VIEW.cx && r.nodes[0].y === OkfLocal.VIEW.cy, "the centre is not at the middle of the view");
    // A ring of one (m = 1): the angle formula must still give a finite point.
    const one = OkfLocal.build(siteIndex(["c", "n"], [[1, 0]]), 0, 2);
    const n = one.nodes[1];
    assert(one.nodes.length === 2 && one.edges.length === 1 && one.edges[0].dashed, `one neighbour: ${JSON.stringify(one)}`);
    assert(Number.isFinite(n.x) && Number.isFinite(n.y) && n.x >= 0 && n.x <= OkfLocal.VIEW.width && n.y >= 0 && n.y <= OkfLocal.VIEW.height,
      `the lone neighbour sits at (${n.x}, ${n.y})`);
  });

  check("local graph: the cap draws 40 nodes, direct neighbours first, then index order, ghosts after concepts", () => {
    const { OkfLocal } = okfLocal();
    assert(OkfLocal.CAP === 40, `CAP is ${OkfLocal.CAP}`);
    // A hub with 300 direct neighbours (odd ones linked to, even ones linking
    // in), five absent targets, and 50 concepts two hops away.
    const ids = ["hub"];
    const links = [];
    for (let k = 1; k <= 300; k++) { ids.push(`s${k}`); links.push(k % 2 ? [0, k] : [k, 0]); }
    for (let k = 0; k < 50; k++) { ids.push(`far${k}`); links.push([1 + k, 301 + k]); }
    for (let g = 0; g < 5; g++) { links.push([0, g, "ghost"]); }
    const index = siteIndex(ids, links, ["g0", "g1", "g2", "g3", "g4"]);
    const firstGhost = index.concepts.length;
    for (const hops of [1, 2]) {
      const r = OkfLocal.build(index, 0, hops);
      const drawn = r.nodes.slice(1).map((n) => n.key);
      assert(r.nodes.length === 40, `hops ${hops}: ${r.nodes.length} nodes drawn`);
      assert(JSON.stringify(drawn) === JSON.stringify(Array.from({ length: 39 }, (_, k) => k + 1)), `hops ${hops}: drew ${JSON.stringify(drawn)}`);
      assert(r.nodes.slice(1).every((n) => n.dist === 1), `hops ${hops}: a second-hop node was drawn before every direct neighbour`);
      const total = hops === 1 ? 305 : 355;
      assert(r.total === total && r.omitted === total - 39, `hops ${hops}: total ${r.total}, omitted ${r.omitted}`);
      const ghostsAt = r.list.findIndex((e) => e.key >= firstGhost);
      assert(ghostsAt === 300 && r.list[300].dist === 1, `hops ${hops}: the ghosts are not listed right after the 300 direct concepts (at ${ghostsAt})`);
    }
    // Absent targets count in the cap too.
    const absent = [];
    for (let g = 0; g < 45; g++) { absent.push([0, g, "ghost"]); }
    const r = OkfLocal.build(siteIndex(["hub"], absent, Array.from({ length: 45 }, (_, g) => `gone${g}`)), 0, 1);
    assert(r.nodes.length === 40 && r.omitted === 6, `45 absent targets: ${r.nodes.length} drawn, ${r.omitted} omitted`);
  });

  check("local graph: the second hop never starts from an absent concept", () => {
    const { OkfLocal } = okfLocal();
    // c -> gone and x -> gone: sharing an absent target is no path to x.
    const r = OkfLocal.build(siteIndex(["c", "x"], [[0, 0, "ghost"], [1, 0, "ghost"]], ["gone"]), 0, 2);
    assert(JSON.stringify(r.list.map((e) => e.key)) === "[2]", `list: ${JSON.stringify(r.list)}`);
  });

  check("local graph: ids named after Object.prototype members are ordinary concepts", () => {
    const window = okfLocal();
    const before = window.Object.getOwnPropertyNames(window.Object.prototype).join();
    const ids = ["__proto__", "constructor", "toString", "hasOwnProperty", "valueOf"];
    const r = window.OkfLocal.build(siteIndex(ids, [[0, 1], [0, 2], [3, 0], [4, 0]]), 0, 2);
    assert(JSON.stringify(r.list.map((e) => e.key)) === "[1,2,3,4]", `list: ${JSON.stringify(r.list)}`);
    assert(window.OkfLocal.lastSegment("a/__proto__") === "__proto__" && window.OkfLocal.lastSegment("constructor") === "constructor", "lastSegment");
    assert(window.Object.getOwnPropertyNames(window.Object.prototype).join() === before, "Object.prototype changed");
  });

  check("local graph: a damaged edge record is skipped, never thrown on", () => {
    const { OkfLocal } = okfLocal();
    const index = siteIndex(["a", "b"], [[0, 1]]);
    index.edges.push([0, 99, 1, 0], [0, 0, 1, 1], ["x", 1, 1, 0], null, [0, 1.5, 1, 0], [-1, 1, 1, 0], "0,1");
    const r = OkfLocal.build(index, 0, 2);
    assert(r.total === 1 && r.edges.length === 1 && r.edges[0].count === 1, JSON.stringify(r));
  });

  check("local graph: two hops around a hub cost linear work in the edges (counted, not timed)", () => {
    const { OkfLocal } = okfLocal();
    const n = 2000;
    const ids = [];
    const links = [];
    for (let k = 0; k < n; k++) { ids.push(`n${String(k).padStart(4, "0")}`); }
    for (let k = 1; k <= 600; k++) {
      links.push([0, k]);
      for (let j = 1; j <= 3; j++) { links.push([k, 601 + ((k * 7 + j * 131) % 1399)]); }
    }
    const index = siteIndex(ids, links);
    const r = OkfLocal.build(index, 0, 2);
    const bound = 4 * index.edges.length + 2 * n;
    assert(r.work > 0 && r.work <= bound,
      `${r.work} units of work for ${index.edges.length} edges and ${n} nodes (bound ${bound}; one scan of the edges per first-hop node would be ~${600 * index.edges.length})`);
    assert(r.nodes.length === OkfLocal.CAP, `${r.nodes.length} nodes drawn`);
  });

  check("local graph: segment never returns a non-finite coordinate", () => {
    const window = okfLocal();
    const { OkfLocal } = window;
    for (const bad of [[NaN, 0, 1, 10, 0, 1, 0], [0, 0, 1, Infinity, 0, 1, 0], [0, 0, 1, 10, 0, 1, NaN]]) {
      let threw = null;
      try { OkfLocal.segment(...bad); } catch (e) { threw = e; }
      assert(threw instanceof window.TypeError, `segment(${bad.join(", ")}) did not throw a TypeError`);
    }
    assert(OkfLocal.segment(5, 5, 3, 5, 5, 3, 0) === null, "coincident ends gave a segment");
    const close = OkfLocal.segment(0, 0, 10, 5, 0, 10, 0);
    assert(close && close.x1 === 0 && close.x2 === 5 && [close.y1, close.y2].every(Number.isFinite), `overlapping reaches: ${JSON.stringify(close)}`);
    const trimmed = OkfLocal.segment(0, 0, 10, 100, 0, 11, 0);
    assert(trimmed.x1 === 10 && trimmed.x2 === 89, `trimmed: ${JSON.stringify(trimmed)}`);
  });

  check("local graph: build rejects a centre that is not a concept, and depths other than 1 and 2", () => {
    const window = okfLocal();
    const index = siteIndex(["a"], []);
    for (const [centre, hops] of [[1, 1], [-1, 1], [0.5, 1], [0, 0], [0, 3], [0, "2"]]) {
      let threw = null;
      try { window.OkfLocal.build(index, centre, hops); } catch (e) { threw = e; }
      assert(threw instanceof window.TypeError, `build(index, ${centre}, ${JSON.stringify(hops)}) did not throw a TypeError`);
    }
  });

  check("local graph: OkfLocal is pure -- no DOM, no clock, no randomness (executed, not grepped)", () => {
    const window = okfLocal();
    const trap = (what) => () => { throw new Error(`OkfLocal used ${what}`); };
    window.Math.random = trap("Math.random");
    window.Date.now = trap("Date.now");
    window.document.createElement = trap("document.createElement");
    window.document.createElementNS = trap("document.createElementNS");
    window.document.getElementById = trap("document.getElementById");
    window.document.querySelector = trap("document.querySelector");
    window.OkfLocal.build(siteIndex(["c", "a", "b"], [[0, 1], [2, 1], [0, 0, "ghost"]], ["gone"]), 0, 2);
    window.OkfLocal.segment(0, 0, 1, 10, 10, 1, 3);
  });
}

function register(h) {
  registerPure(h);
}

module.exports = { register };
```

- [ ] **Step 2: Run the cases and watch them fail**

If P1.1's case loader is already on the branch (`grep -n "cases" tools/viewer-security-check/run.js` prints the loader), run:

```bash
cd tools/viewer-security-check && npm test
```

Otherwise run the stub driver (it executes the synchronous cases only, exactly as `h.check` prints them):

```bash
cd tools/viewer-security-check && node -e 'let f=0;const h=Object.freeze({check(n,fn){try{fn();console.log("  ok  - "+n)}catch(e){f++;console.log("FAIL  - "+n+"\n        "+e.message)}},assert(c,m){if(!c)throw new Error(m)},checkAsync(){}});require("./cases/p2.js").register(h);console.log(f+" failed");process.exit(f?1:0)'
```

Expected: every `local graph:` case FAILS with `ENOENT: no such file or directory, open '…okf-local.js'`; exit code 1.

- [ ] **Step 3: Write the module**

Create `src/OKF4net.Viewer/Assets/okf-local.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The local graph of a concept page (spec §4.3, §12.4): the concept and its
// neighbours at one or two hops, in rings. Two parts in one classic script:
//
// - window.OkfLocal, pure (no DOM, no clock, no randomness): from the site
//   index alone it computes the drawn nodes and their ring positions, every
//   index edge between them and the full neighbour list.
//   tools/viewer-security-check calls it directly (cases/p2.js).
// - the page part, which runs only on a concept page (<html
//   data-okf-view="page">) and draws that result into #okf-context: an SVG
//   built by code (fixed element and attribute names, shapes from OkfShapes,
//   labels by textContent, URLs from index paths through OkfSite.resolve),
//   never inside #okf-body.
//
// Node keys: a concept is its position in index.concepts; a ghost (an absent
// link target, never navigable) is concepts.length + its position in
// index.ghosts. Ascending key order is index order, concepts first, then
// ghosts: the order of the cap and of the angles. The JS never compares ids
// (spec §3.1).
(function () {
  "use strict";

  var CAP = 40; // nodes drawn, the centre included (spec A11)
  var VIEW = Object.freeze({ width: 298, height: 248, cx: 149, cy: 118 });
  // One ring at 1 hop (or when the second hop draws nothing), two at 2 hops;
  // measured on the mockup's 298 x 248 drawing, labels 14 under a shape.
  var ONE_RING = Object.freeze([Object.freeze({ rx: 127, ry: 95 })]);
  var TWO_RINGS = Object.freeze([Object.freeze({ rx: 106, ry: 72 }), Object.freeze({ rx: 134, ry: 98 })]);

  function round(value) { return Math.round(value * 100) / 100; }

  // [from, to, count, toGhost] as IndexScript writes it, turned into node
  // keys; null for anything else, so a damaged index draws less instead of
  // throwing.
  function ends(edge, conceptCount, ghostCount) {
    if (!Array.isArray(edge)) { return null; }
    var from = edge[0];
    var to = edge[1];
    var ghost = edge[3] === 1;
    if (!Number.isInteger(from) || from < 0 || from >= conceptCount) { return null; }
    if (!Number.isInteger(to) || to < 0 || to >= (ghost ? ghostCount : conceptCount)) { return null; }
    return { from: from, to: ghost ? conceptCount + to : to, count: Number.isInteger(edge[2]) && edge[2] > 0 ? edge[2] : 1 };
  }

  // The neighbourhood of concept `centre` at `hops` (1 or 2). Membership is
  // undirected (links out and in); edges keep their direction. The second hop
  // never starts from a ghost; a self-link is no neighbour. At most four
  // passes over the edges and one over the nodes per hop: `work` counts them
  // (spec §7: counted, never timed).
  function build(index, centre, hops) {
    var C = index.concepts.length;
    var G = index.ghosts.length;
    var N = C + G;
    if (!Number.isInteger(centre) || centre < 0 || centre >= C) { throw new TypeError("okf-local: centre is not a concept position"); }
    if (hops !== 1 && hops !== 2) { throw new TypeError("okf-local: hops is not 1 or 2"); }
    var edges = index.edges;
    var work = 0;
    var k;
    var e;
    var dist = new Int8Array(N).fill(-1);
    var rel = new Int8Array(N);           // hop 1: 1 = the centre links to it, 2 = it links to the centre
    var via = new Int32Array(N).fill(-1); // hop 2: the smallest hop-1 concept key it is reached from
    dist[centre] = 0;

    for (k = 0; k < edges.length; k++) {
      work++;
      e = ends(edges[k], C, G);
      if (!e || e.from === e.to) { continue; }
      if (e.from === centre) {
        dist[e.to] = 1;
        rel[e.to] |= 1;
      } else if (e.to === centre) {
        dist[e.from] = 1;
        rel[e.from] |= 2;
      }
    }

    if (hops === 2) {
      for (k = 0; k < edges.length; k++) {
        work++;
        e = ends(edges[k], C, G);
        if (!e || e.from === e.to) { continue; }
        // e.from is always a concept; e.to may be a ghost, which never expands.
        if (dist[e.from] === 1 && dist[e.to] !== 0 && dist[e.to] !== 1) {
          dist[e.to] = 2;
          if (via[e.to] === -1 || e.from < via[e.to]) { via[e.to] = e.from; }
        }
        if (dist[e.to] === 1 && e.to < C && dist[e.from] !== 0 && dist[e.from] !== 1) {
          dist[e.from] = 2;
          if (via[e.from] === -1 || e.to < via[e.from]) { via[e.from] = e.to; }
        }
      }
    }

    var list = [];
    for (var d = 1; d <= hops; d++) {
      for (k = 0; k < N; k++) {
        work++;
        if (dist[k] === d) {
          list.push(Object.freeze({ key: k, dist: d, rel: d === 1 ? rel[k] : 0, via: d === 2 ? via[k] : -1 }));
        }
      }
    }

    // Drawn: the centre, then the list up to the cap (direct neighbours come
    // first in it). Their order on each ring is key order.
    var shown = list.slice(0, CAP - 1);
    var slot = new Int32Array(N).fill(-1);
    var nodes = [{ key: centre, dist: 0, x: VIEW.cx, y: VIEW.cy }];
    slot[centre] = 0;
    var rings = [[], []];
    for (k = 0; k < shown.length; k++) { rings[shown[k].dist - 1].push(shown[k].key); }
    var radii = rings[1].length > 0 ? TWO_RINGS : ONE_RING;
    for (var r = 0; r < radii.length; r++) {
      var m = rings[r].length;
      for (var j = 0; j < m; j++) {
        // Clockwise from the top, half a step off it: four neighbours sit on
        // the diagonals, as in the mockup. Math.cos and Math.sin are allowed
        // here (spec §4.3): the cross-engine rule binds the global simulation.
        var angle = -Math.PI / 2 + Math.PI / m + 2 * Math.PI * j / m;
        slot[rings[r][j]] = nodes.length;
        nodes.push({
          key: rings[r][j],
          dist: r + 1,
          x: round(VIEW.cx + radii[r].rx * Math.cos(angle)),
          y: round(VIEW.cy + radii[r].ry * Math.sin(angle)),
        });
      }
    }

    // Every index edge whose two ends are drawn, not only the ones walked.
    // A repeated record of one pair (IndexScript already merges them) adds
    // its count to the first.
    var drawn = [];
    var byPair = new Map();
    for (k = 0; k < edges.length; k++) {
      work++;
      e = ends(edges[k], C, G);
      if (!e || e.from === e.to || slot[e.from] === -1 || slot[e.to] === -1) { continue; }
      var pair = e.from * N + e.to;
      if (byPair.has(pair)) {
        drawn[byPair.get(pair)].count += e.count;
        continue;
      }
      byPair.set(pair, drawn.length);
      // Dashed when the target is closer to the centre than the source
      // ("referenced by"), solid otherwise ("links to"), spec X7.
      drawn.push({ from: slot[e.from], to: slot[e.to], count: e.count, dashed: dist[e.to] < dist[e.from], paired: false });
    }
    for (k = 0; k < drawn.length; k++) {
      work++;
      drawn[k].paired = byPair.has(nodes[drawn[k].to].key * N + nodes[drawn[k].from].key);
    }

    return {
      centre: centre,
      hops: hops,
      nodes: nodes,
      edges: drawn,
      list: list,
      total: list.length,
      omitted: list.length - shown.length,
      work: work,
    };
  }

  // The drawn part of an edge between nodes centred at (x1, y1) and
  // (x2, y2): trimmed by each node's reach (r1, r2) so the arrow tip stops at
  // the target's edge -- untrimmed when the two reaches overlap -- and moved
  // by `shift` along the edge's normal, so the two lines of an A -> B, B -> A
  // pair sit 2 x shift apart (spec §4.3: ± 3). null when the ends coincide.
  function segment(x1, y1, r1, x2, y2, r2, shift) {
    var args = [x1, y1, r1, x2, y2, r2, shift];
    for (var k = 0; k < args.length; k++) {
      if (!Number.isFinite(args[k])) { throw new TypeError("okf-local: segment needs finite numbers"); }
    }
    var dx = x2 - x1;
    var dy = y2 - y1;
    var length = Math.sqrt(dx * dx + dy * dy);
    if (!(length > 0) || !Number.isFinite(length)) { return null; }
    var ux = dx / length;
    var uy = dy / length;
    var nx = -uy * shift;
    var ny = ux * shift;
    var from = length > r1 + r2 ? r1 : 0;
    var to = length > r1 + r2 ? r2 : 0;
    return {
      x1: round(x1 + ux * from + nx),
      y1: round(y1 + uy * from + ny),
      x2: round(x2 - ux * to + nx),
      y2: round(y2 - uy * to + ny),
    };
  }

  // The last segment of an id: a node's label (spec X7).
  function lastSegment(id) {
    var text = String(id);
    var slash = text.lastIndexOf("/");
    return slash === -1 ? text : text.slice(slash + 1);
  }

  window.OkfLocal = Object.freeze({
    CAP: CAP, VIEW: VIEW, build: build, segment: segment, lastSegment: lastSegment,
  });
})();
```

- [ ] **Step 4: Run the cases and watch them pass**

Run the same command as Step 2 (`npm test` with the loader, else the stub driver).

Expected: the 13 `local graph:` cases print `  ok  - local graph: …`; with the stub, the last line is `0 failed` and the exit code 0; with `npm test`, the summary reads `… passed, 0 failed`.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-local.js tools/viewer-security-check/cases/p2.js
git commit -m "$(cat <<'EOF'
feat(viewer): OkfLocal, the pure local-graph layout (P2)

okf-local.js computes, from the site index alone, the neighbourhood of a
concept at one or two hops (undirected membership, directed edges), its ring
layout, the 40-node cap (direct neighbours first, index order, ghosts after
concepts) and the full neighbour list, in work counted linear in the edges.
cases/p2.js pins determinism, bounds, the cap, ghosts, self-links, opposite
and repeated edges, hostile keys, damaged records and purity.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Load `okf-local.js` on concept pages and the index

**Precondition:** P1.1 is merged on `feat/viewer-interactive-p1`.

**Files:**
- Modify: `src/OKF4net.Viewer/HtmlWriter.cs` (one line under `// P2: local graph` in `PageScripts`)
- Create: `tests/OKF4net.Tests/Viewer/HtmlWriterLocalGraphTests.cs`

**Interfaces:**
- Consumes: §12.0 rows `HtmlWriter.PageScripts` (marker `// P2: local graph`, end of table) and `HtmlWriter.WriteAssets` (every embedded `Assets/` resource written under `assets/`); §12.6 load order (`okf-index.js`, `okf-site.js`, `okf-shapes.js`, …, `okf-page.js`, then P2's line).
- Produces: every concept page and `index.html` end with `<script src="<prefix>assets/okf-local.js">`, after `okf-page.js`; `assets/okf-local.js` is written. `graph.html` (P3) does not load it — P3's `RenderGraph` owns that page.

- [ ] **Step 0: Bring P1.1 in and check its markers**

```bash
git merge --no-edit feat/viewer-interactive-p1
grep -n "// P2: local graph" src/OKF4net.Viewer/HtmlWriter.cs
grep -n "(P2 rules)" src/OKF4net.Viewer/Assets/viewer.css
grep -n "(P2 checks)" tools/viewer-security-check/ACCEPTANCE.md
ls src/OKF4net.Viewer/Assets/okf-shapes.js src/OKF4net.Viewer/Assets/okf-page.js tools/viewer-security-check/recette/recette.js
grep -n "cases" tools/viewer-security-check/run.js
```

Expected: the merge succeeds without conflict (T1 only added new files); each `grep` prints at least one line and `ls` lists the three files. If any is missing, P1.1 is not merged: stop.

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Viewer/HtmlWriterLocalGraphTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// Slice P2 (local graph): <c>okf-local.js</c> is written under <c>assets/</c>
/// and loaded last by every concept page and by the index (spec §4.1, §12.0,
/// §12.6). What the script does is checked by
/// <c>tools/viewer-security-check/cases/p2.js</c>: xunit cannot execute it.
/// </summary>
public class HtmlWriterLocalGraphTests
{
    private static string WriteSite(TempDir src, TempDir dest)
    {
        src.Write("a.md", "---\ntype: Note\ntitle: A\ndescription: d\n---\nSee [b](/tables/b.md).\n");
        src.Write("tables/b.md", "---\ntype: Note\ntitle: B\ndescription: d\n---\nBody.\n");
        HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);
        return dest.Path;
    }

    private static List<string> ScriptSources(string html)
        => Regex.Matches(html, "<script[^>]*\\ssrc=\"([^\"]*)\"").Select(m => m.Groups[1].Value).ToList();

    [Fact]
    public void Write_writes_okf_local_js_under_assets()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var outDir = WriteSite(src, dest);

        var script = File.ReadAllText(Path.Combine(outDir, "assets", "okf-local.js"));

        Assert.Contains("window.OkfLocal = Object.freeze(", script);
    }

    [Theory]
    [InlineData("a.html", "")]
    [InlineData("tables/b.html", "../")]
    [InlineData("index.html", "")]
    public void Concept_pages_and_the_index_load_okf_local_js_once_and_last(string page, string prefix)
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var outDir = WriteSite(src, dest);

        var html = File.ReadAllText(Path.Combine(outDir, page.Replace('/', Path.DirectorySeparatorChar)));
        var sources = ScriptSources(html);

        Assert.Single(sources, s => s.EndsWith("okf-local.js", StringComparison.Ordinal));
        Assert.Equal(prefix + "assets/okf-local.js", sources[^1]);

        // It reads the index, OkfSite and OkfShapes, and comes after the page
        // head's script (spec §12.6), so all four load before it.
        foreach (var dependency in new[] { "okf-index.js", "okf-site.js", "okf-shapes.js", "okf-page.js" })
        {
            Assert.InRange(sources.IndexOf(prefix + "assets/" + dependency), 0, sources.Count - 2);
        }
    }
}
```

- [ ] **Step 2: Run the tests and watch the page test fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlWriterLocalGraphTests"`
Expected: `Write_writes_okf_local_js_under_assets` PASSES (P1.1's generic `WriteAssets` already writes T1's file); the three `Concept_pages_and_the_index_load_okf_local_js_once_and_last` cases FAIL with `Assert.Single() Failure: The collection was empty`.

- [ ] **Step 3: Add the one line**

In `src/OKF4net.Viewer/HtmlWriter.cs`, directly under the marker line `// P2: local graph` of `PageScripts`, insert the entry `"okf-local.js",` with the marker's own indentation, so the block reads (indentation as P1.1 wrote the marker):

```csharp
        // P2: local graph
        "okf-local.js",
```

Change nothing else in the file.

- [ ] **Step 4: Run the tests and watch them pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlWriterLocalGraphTests"`
Expected: `Passed!  - Failed: 0, Passed: 4`.

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer"`
Expected: `Failed: 0` (P1 and P1.1 classes unaffected: P1.1 made the script count of `HtmlWriterTests` independent of the number of scripts, §7).

Run: `dotnet format OKF4net.sln --verify-no-changes`
Expected: exit code 0, no output.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Viewer/HtmlWriter.cs tests/OKF4net.Tests/Viewer/HtmlWriterLocalGraphTests.cs
git commit -m "$(cat <<'EOF'
feat(viewer): load okf-local.js on concept pages and the index (P2)

One entry in PageScripts, under P1.1's "P2: local graph" marker (spec
§12.0); the generic asset writer already writes the file. HtmlWriterLocalGraphTests
pins that it loads once, last, after the index, OkfSite, OkfShapes and
okf-page.js.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Fixtures, the Neighbourhood section, its drawing, toggle and foot

**Files:**
- Create: `tools/viewer-security-check/fixtures/hostile-bundle/p2-local/a.md`, `b.md`, `c.md`, `d.md`, `far.md`, `hub.md`, `lonely.md`, `self.md`
- Modify: `src/OKF4net.Viewer/Assets/okf-local.js` (the page part, after the `OkfLocal` export)
- Modify: `tools/viewer-security-check/cases/p2.js` (`registerPage`, `assertFixedSvg`, `expectedHood`, `positionOf`)

**Interfaces:**
- Consumes: T1's `OkfLocal.build/segment/lastSegment/VIEW`; §12.1 index v2 via `OkfSite.readIndex`; §12.2 `OkfShapes.SIZES.local`, `SIZES.localCenter`, `kindOf`, `node`; §12.3 `data-okf-view`, `data-okf-concept`, `data-okf-root` (`OkfSite.rootOf`), `#okf-tools a#okf-global-graph`; `OkfSite.element`, `OkfSite.resolve`; §12.7 `h.checkAsync`, `h.openPage` (`blocked`, `beforeParse`), `h.navigations`.
- Produces (DOM contract, §12.4), inserted into `#okf-context` right after `#okf-toc`, `#okf-context` un-hidden:
  - `section.okf-local#okf-local-graph[aria-labelledby=okf-local-title]` containing, in order: `div.okf-local-head` (`h2#okf-local-title.okf-section-title` "Neighbourhood"; `div.okf-hops[role=group][aria-label="Neighbourhood depth"]` with `button#okf-local-hops-1` "1 hop" and `button#okf-local-hops-2` "2 hops", `type=button`, `aria-pressed`); `div.okf-local-canvas` holding one `svg[role=img][focusable=false][viewBox="0 0 298 248"][width=100%][height=248][aria-label]`; `p.okf-local-foot` (`span` legend, then `a#okf-local-open` "Open in graph" when the header link exists).
  - In the SVG: `defs > marker#okf-local-arrow` and `marker#okf-local-arrow-in`, each with `path.okf-local-arrowhead`; `g.okf-local-edges > line.okf-local-edge` (`stroke-width="1.4"`; dashed ones add class `okf-local-edge-in`, `stroke-dasharray="4 3"`, `marker-end="url(#okf-local-arrow-in)"`, else `url(#okf-local-arrow)`); one `g.okf-node` per node from `OkfShapes.node`, with class `okf-local-node` (concept) or `okf-local-ghost`, the centre also `okf-selected`, children `title` (id, or "absent: id") first and `text.okf-local-label` (+ `okf-local-label-center`, `okf-local-label-ghost`) last.
  - Behaviour: click on a concept node dispatches the cancelable `okf:navigate` event (`detail.href` = `OkfSite.resolve(root, path)`), then `location.assign`; a ghost does nothing. The hop buttons redraw the canvas.
  - In `okf-local.js`, the function `render(result)` and the line `section.appendChild(foot);` are the anchors T4 edits.

- [ ] **Step 1: Write the fixtures**

All links use the bundle-absolute form (§6.1 of the OKF spec). Ids sort `p2-local/a`, `b`, `c`, `d`, `far`, `hub`, `lonely`, `self`; ghosts `p2-local/gone-1`, `gone-2`. Neighbourhoods this gives: `hub` — a (both ways, the hub → a link written twice), b, gone-1, gone-2, and a self-link; `c` — a, b (both link in), far (linked to), then hub and d at two hops.

Create `tools/viewer-security-check/fixtures/hostile-bundle/p2-local/hub.md`:

```markdown
---
type: Metric
title: '<img src=x onerror="window.__pwned=1">Hub'
description: P2 fixture - links to a twice, to b, to itself and to two absent concepts.
---
See [a](/p2-local/a.md), [a again](/p2-local/a.md), [b](/p2-local/b.md), [myself](/p2-local/hub.md), [gone one](/p2-local/gone-1.md) and [gone two](/p2-local/gone-2.md).
```

Create `tools/viewer-security-check/fixtures/hostile-bundle/p2-local/a.md`:

```markdown
---
type: Policy
title: A
description: P2 fixture - links back to the hub and on to c.
---
Back to the [hub](/p2-local/hub.md), on to [c](/p2-local/c.md).
```

Create `tools/viewer-security-check/fixtures/hostile-bundle/p2-local/b.md`:

```markdown
---
type: Skill
title: B
description: P2 fixture - links to c and d.
---
See [c](/p2-local/c.md) and [d](/p2-local/d.md).
```

Create `tools/viewer-security-check/fixtures/hostile-bundle/p2-local/c.md`:

```markdown
---
type: Attested Computation
title: C
description: P2 fixture - linked from a and b, links to far; hub and d are two hops away.
---
On to [far](/p2-local/far.md).
```

Create `tools/viewer-security-check/fixtures/hostile-bundle/p2-local/d.md`:

```markdown
---
type: BigQuery Table
title: D
description: P2 fixture - linked from b only.
---
Plain.
```

Create `tools/viewer-security-check/fixtures/hostile-bundle/p2-local/far.md`:

```markdown
---
type: Note
title: Far
description: P2 fixture - linked from c only.
---
Plain.
```

Create `tools/viewer-security-check/fixtures/hostile-bundle/p2-local/lonely.md`:

```markdown
---
type: Note
title: Lonely
description: P2 fixture - no link in or out, no heading.
---
Plain.
```

Create `tools/viewer-security-check/fixtures/hostile-bundle/p2-local/self.md`:

```markdown
---
type: Note
title: Self
description: P2 fixture - links to itself only.
---
See [myself](/p2-local/self.md).
```

- [ ] **Step 2: Write the failing page cases**

In `tools/viewer-security-check/cases/p2.js`, insert above `function register(h) {`:

```js
const SVG_NS = "http://www.w3.org/2000/svg";
// The fixed vocabulary of spec §12.2.
const SVG_ELEMENTS = new Set(["svg", "g", "circle", "rect", "path", "line", "text", "title", "defs", "marker"]);
const SVG_ATTRIBUTES = new Set(["viewBox", "width", "height", "class", "aria-hidden", "focusable", "role", "aria-label",
  "tabindex", "id", "cx", "cy", "r", "x", "y", "x1", "y1", "x2", "y2", "d", "stroke-width", "stroke-dasharray",
  "text-anchor", "dominant-baseline", "marker-end", "refX", "refY", "markerWidth", "markerHeight", "orient", "transform"]);
const NUMERIC = new Set(["cx", "cy", "r", "x", "y", "x1", "y1", "x2", "y2", "width", "height", "stroke-width",
  "refX", "refY", "markerWidth", "markerHeight"]);

// Spec §4.5, §12.2: the drawing holds the fixed vocabulary only, every
// number in it is finite, every id is a fixed okf- value.
function assertFixedSvg(assert, svg) {
  for (const node of [svg, ...svg.querySelectorAll("*")]) {
    assert(node.namespaceURI === SVG_NS, `<${node.localName}> is outside the SVG namespace`);
    assert(SVG_ELEMENTS.has(node.localName), `<${node.localName}> is not in the fixed vocabulary`);
    for (const name of node.getAttributeNames()) {
      const value = node.getAttribute(name);
      assert(SVG_ATTRIBUTES.has(name), `<${node.localName}> carries ${name}="${value}", outside the fixed vocabulary`);
      if (name === "id") { assert(value.startsWith("okf-"), `id="${value}" is not a fixed okf- value`); }
      if (NUMERIC.has(name) && !(node === svg && name === "width" && value === "100%")) {
        assert(Number.isFinite(Number(value)), `<${node.localName}> ${name}="${value}" is not a finite number`);
      }
      if (name === "d" || name === "viewBox" || name === "stroke-dasharray") {
        const numbers = value.replace(/[MLZ]/g, " ").trim().split(/\s+/).filter((t) => t !== "");
        assert(numbers.every((t) => Number.isFinite(Number(t))), `<${node.localName}> ${name}="${value}" holds a non-finite number`);
      }
    }
  }
}

// The test's own reading of spec §4.3 over an executed index, written apart
// from okf-local.js: keys are concept positions, then concepts.length + a
// ghost position. Only for pages under the cap (no ordering is checked).
function expectedHood(index, centre, hops) {
  const C = index.concepts.length;
  const ends = index.edges.map(([from, to, , ghost]) => [from, ghost ? C + to : to]).filter(([from, to]) => from !== to);
  const dist = new Map([[centre, 0]]);
  for (const [from, to] of ends) {
    if (from === centre) { dist.set(to, 1); }
    if (to === centre) { dist.set(from, 1); }
  }
  if (hops === 2) {
    const first = new Set(Array.from(dist).filter(([key, d]) => d === 1 && key < C).map(([key]) => key));
    for (const [from, to] of ends) {
      if (first.has(from) && !dist.has(to)) { dist.set(to, 2); }
      if (first.has(to) && !dist.has(from)) { dist.set(from, 2); }
    }
  }
  const drawn = ends.filter(([from, to]) => dist.has(from) && dist.has(to))
    .map(([from, to]) => ({ from, to, dashed: dist.get(to) < dist.get(from) }));
  return { dist, total: dist.size - 1, drawn };
}

function positionOf(window, id) {
  const position = window.OKF_INDEX.concepts.findIndex((c) => c.id === id);
  if (position === -1) { throw new Error(`${id} is not in the executed index`); }
  return position;
}

function click(window, target) {
  target.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
}

function registerPage(h) {
  const { checkAsync, assert, openPage, navigations } = h;

  checkAsync("local graph: a concept page draws its neighbourhood in #okf-context, after the contents, before Referenced by", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const section = doc.getElementById("okf-local-graph");
    assert(section, "no #okf-local-graph on a page with neighbours");
    const context = doc.getElementById("okf-context");
    assert(section.parentElement === context && !context.hidden, "the section is not shown in #okf-context");
    assert(section.previousElementSibling === doc.getElementById("okf-toc"),
      `the section follows <${section.previousElementSibling && section.previousElementSibling.localName}>, not #okf-toc`);
    assert(section.nextElementSibling && section.nextElementSibling.classList.contains("okf-backlinks"), "the section is not right before Referenced by");
    const title = doc.getElementById("okf-local-title");
    assert(section.getAttribute("aria-labelledby") === "okf-local-title" && title.textContent === "Neighbourhood", "the section is not named Neighbourhood");
    assert(title.localName === "h2" && title.classList.contains("okf-section-title"), "the title is not the shared section title");
    const svg = section.querySelector(".okf-local-canvas > svg");
    assert(svg && svg.getAttribute("role") === "img" && /^Local graph of p2-local\/c, 1 hop: /.test(svg.getAttribute("aria-label")),
      `svg role / name: ${svg && svg.getAttribute("role")} / ${svg && svg.getAttribute("aria-label")}`);
    assert(svg.getAttribute("viewBox") === "0 0 298 248", `viewBox ${svg.getAttribute("viewBox")}`);
    assert(doc.getElementById("okf-body").querySelector("svg") === null, "an svg reached #okf-body");
    const expected = expectedHood(window.OKF_INDEX, positionOf(window, "p2-local/c"), 1);
    const drawn = svg.querySelectorAll("g.okf-node").length;
    assert(drawn === 1 + expected.total, `${drawn} nodes for ${expected.total} neighbours`);
    assertFixedSvg(assert, svg);
  });

  checkAsync("local graph: 1 hop by default, 2 hops adds the second ring, both announced by aria-pressed", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const one = doc.getElementById("okf-local-hops-1");
    const two = doc.getElementById("okf-local-hops-2");
    const group = one.parentElement;
    assert(group.getAttribute("role") === "group" && group.getAttribute("aria-label") === "Neighbourhood depth", "the toggle is not a named group");
    assert(one.localName === "button" && one.type === "button" && two.type === "button", "the toggle is not two buttons");
    assert(one.textContent === "1 hop" && two.textContent === "2 hops", `labels: ${one.textContent} / ${two.textContent}`);
    assert(one.getAttribute("aria-pressed") === "true" && two.getAttribute("aria-pressed") === "false", "1 hop is not the default");
    const centre = positionOf(window, "p2-local/c");
    const nodes = () => doc.querySelectorAll("#okf-local-graph svg g.okf-node").length;
    const shallow = expectedHood(window.OKF_INDEX, centre, 1);
    const deep = expectedHood(window.OKF_INDEX, centre, 2);
    assert(deep.total > shallow.total, "this case needs a page with a second hop");
    two.click();
    assert(one.getAttribute("aria-pressed") === "false" && two.getAttribute("aria-pressed") === "true", "2 hops is not announced as pressed");
    assert(nodes() === 1 + deep.total, `2 hops: ${nodes()} nodes for ${deep.total} neighbours`);
    // Only the drawing's own <svg>: T4's list rows add svg.okf-glyph icons
    // to the section, which are not drawings.
    assert(doc.querySelectorAll("#okf-local-graph .okf-local-canvas > svg").length === 1, "the old drawing was kept beside the new one");
    assert(doc.querySelectorAll("#okf-local-arrow, #okf-local-arrow-in").length === 2, "the arrow markers are duplicated");
    assertFixedSvg(assert, doc.querySelector("#okf-local-graph .okf-local-canvas > svg"));
    one.click();
    assert(nodes() === 1 + shallow.total, "back to 1 hop did not redraw the first ring only");
  });

  checkAsync("local graph: every index edge between drawn nodes is drawn, dashed when it points back toward the centre", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    doc.getElementById("okf-local-hops-2").click();
    const expected = expectedHood(window.OKF_INDEX, positionOf(window, "p2-local/c"), 2);
    const lines = Array.from(doc.querySelectorAll("#okf-local-graph svg line.okf-local-edge"));
    const dashed = lines.filter((l) => l.classList.contains("okf-local-edge-in"));
    const wantDashed = expected.drawn.filter((e) => e.dashed).length;
    assert(lines.length === expected.drawn.length, `${lines.length} lines for ${expected.drawn.length} index edges between drawn nodes`);
    assert(dashed.length === wantDashed && wantDashed > 0 && wantDashed < lines.length, `${dashed.length} dashed lines, expected ${wantDashed} of ${lines.length}`);
    for (const line of lines) {
      const back = line.classList.contains("okf-local-edge-in");
      assert(line.getAttribute("stroke-width") === "1.4", `stroke-width ${line.getAttribute("stroke-width")}`);
      assert(back ? line.getAttribute("stroke-dasharray") === "4 3" : !line.hasAttribute("stroke-dasharray"), "a line is dashed against its direction");
      assert(line.getAttribute("marker-end") === (back ? "url(#okf-local-arrow-in)" : "url(#okf-local-arrow)"), `marker-end ${line.getAttribute("marker-end")}`);
    }
    // hub <-> a are both drawn at 2 hops from c: the two lines must not
    // retrace each other (offset ± 3).
    const coords = (l) => ["x1", "y1", "x2", "y2"].map((name) => Number(l.getAttribute(name)));
    for (const one of lines) {
      for (const other of lines) {
        const [a1, b1, c1, d1] = coords(one);
        const [a2, b2, c2, d2] = coords(other);
        assert(!(one !== other && a1 === c2 && b1 === d2 && c1 === a2 && d1 === b2), "two opposite edges are drawn on top of each other");
      }
    }
    const marker = doc.getElementById("okf-local-arrow");
    assert(marker && marker.getAttribute("markerWidth") === "7" && marker.getAttribute("markerHeight") === "7", "the arrow is not 7");
  });

  checkAsync("local graph: shapes are OkfShapes nodes at the local and localCenter sizes, labels under them", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    doc.getElementById("okf-local-hops-2").click();
    const S = window.OkfShapes;
    const index = window.OKF_INDEX;
    const groups = Array.from(doc.querySelectorAll("#okf-local-graph svg g.okf-node"));
    assert(groups.length > 2, "this case needs drawn neighbours");
    const kinds = new Set();
    groups.forEach((g, k) => {
      const centre = k === 0;
      assert(g.classList.contains("okf-selected") === centre, `node ${k}: okf-selected is ${centre ? "missing" : "set"}`);
      const title = g.firstElementChild;
      assert(title.localName === "title", `node ${k}: its first child is <${title.localName}>, not <title>`);
      const ghost = title.textContent.startsWith("absent: ");
      const id = ghost ? title.textContent.slice("absent: ".length) : title.textContent;
      const kind = ghost ? "ghost" : S.kindOf(index, positionOf(window, id));
      kinds.add(kind);
      const size = S.SIZES[centre ? "localCenter" : "local"][kind];
      const label = g.lastElementChild;
      assert(label.localName === "text" && label.textContent === window.OkfLocal.lastSegment(id), `node ${k}: label "${label.textContent}" for ${id}`);
      assert(label.getAttribute("text-anchor") === "middle", `node ${k}: the label is not centred`);
      // X7: baseline at cy + size / 2 + 14 (centre: + 6 more, under its square).
      const x = Number(label.getAttribute("x"));
      const y = Math.round((Number(label.getAttribute("y")) - size.size / 2 - (centre ? 6 : 0) - 14) * 100) / 100;
      const reference = S.node(kind, x, y, size.size, size);
      assert(label.previousElementSibling.outerHTML === reference.lastElementChild.outerHTML,
        `node ${k} (${id}, ${kind}): ${label.previousElementSibling.outerHTML} is not OkfShapes' ${reference.lastElementChild.outerHTML}`);
      assert(centre === (g.querySelector(".okf-node-ring") !== null), `node ${k}: the selection square is ${centre ? "missing" : "present"}`);
    });
    assert(kinds.size >= 3, `only ${kinds.size} distinct shapes drawn: the fixture no longer exercises the size table`);
  });

  checkAsync("local graph: absent concepts are drawn but never navigable", async () => {
    const window = await openPage("p2-local/hub.html");
    const doc = window.document;
    const index = window.OKF_INDEX;
    const ghostIds = new Set(index.ghosts.map((g) => g.id));
    const hood = expectedHood(index, positionOf(window, "p2-local/hub"), 1);
    const expected = Array.from(hood.dist.keys()).filter((key) => key >= index.concepts.length).length;
    const ghosts = Array.from(doc.querySelectorAll("#okf-local-graph svg g.okf-local-ghost"));
    assert(ghosts.length === expected && expected > 0, `${ghosts.length} absent concepts drawn, expected ${expected}`);
    let attempts = 0;
    doc.addEventListener("okf:navigate", (e) => { attempts++; e.preventDefault(); });
    for (const g of ghosts) {
      const title = g.firstElementChild.textContent;
      assert(title.startsWith("absent: ") && ghostIds.has(title.slice("absent: ".length)), `ghost title "${title}"`);
      assert(g.lastElementChild.classList.contains("okf-local-label-ghost"), "a ghost label is not marked as such");
      assert(!g.classList.contains("okf-local-node"), "a ghost is marked as a navigable node");
      click(window, g.lastElementChild);
      click(window, g.lastElementChild.previousElementSibling);
    }
    assert(attempts === 0 && navigations(window) === 0, "clicking an absent concept tried to navigate");
  });

  checkAsync("local graph: a click on a concept node opens its page through the resolver", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const node = Array.from(doc.querySelectorAll("#okf-local-graph svg g.okf-local-node"))
      .find((g) => g.firstElementChild.textContent === "p2-local/a");
    assert(node, "node p2-local/a is not drawn on c's page");
    let href = null;
    const veto = (e) => { href = e.detail.href; e.preventDefault(); };
    doc.addEventListener("okf:navigate", veto);
    click(window, node.lastElementChild);
    const path = window.OKF_INDEX.concepts[positionOf(window, "p2-local/a")].path;
    assert(href === "../" + path, `navigated to ${href}, expected ../${path} (the page's root prefix + the index path)`);
    assert(navigations(window) === 0, "a vetoed okf:navigate still navigated");
    doc.removeEventListener("okf:navigate", veto);
    click(window, node.lastElementChild.previousElementSibling);
    assert(navigations(window) === 1, `${navigations(window)} navigations after an unvetoed click on the shape`);
  });

  checkAsync("local graph: Open in graph repeats the header's Global graph link, fragment included", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const header = doc.getElementById("okf-global-graph");
    const open = doc.getElementById("okf-local-open");
    assert(header && open, "the header link or Open in graph is missing");
    assert(open.getAttribute("href") === header.getAttribute("href") && open.getAttribute("href").endsWith("#p2-local/c"),
      `Open in graph: ${open.getAttribute("href")}, header: ${header.getAttribute("href")}`);
    assert(open.textContent === "Open in graph" && open.parentElement.classList.contains("okf-local-foot"), "Open in graph is not in the foot");
    const dot = String.fromCharCode(0xb7);
    assert(open.previousElementSibling.textContent === `solid = links to ${dot} dashed = referenced by`, `legend: ${open.previousElementSibling.textContent}`);
  });

  checkAsync("local graph: without the header's graph link, Open in graph is left out", async () => {
    // Simulates a header without #okf-global-graph: the one lookup
    // okf-local.js makes for it (from #okf-tools) answers nothing.
    const beforeParse = (w) => {
      const query = w.Element.prototype.querySelector;
      w.Element.prototype.querySelector = function (selector) {
        return this.id === "okf-tools" && selector === "#okf-global-graph" ? null : query.call(this, selector);
      };
    };
    const window = await openPage("p2-local/c.html", { beforeParse });
    const doc = window.document;
    assert(doc.getElementById("okf-local-graph"), "the section is missing");
    assert(doc.getElementById("okf-local-open") === null, "Open in graph was written without a link to repeat");
  });

  checkAsync("local graph: nothing on the index, without an index or OkfShapes, or without a neighbour", async () => {
    const pages = [
      ["index.html", {}],
      ["p2-local/c.html", { blocked: ["assets/okf-index.js"] }],
      ["p2-local/c.html", { blocked: ["assets/okf-shapes.js"] }],
      ["p2-local/lonely.html", {}],
      ["p2-local/self.html", {}],
    ];
    for (const [page, opts] of pages) {
      const window = await openPage(page, opts);
      assert(window.document.getElementById("okf-local-graph") === null, `${page} ${JSON.stringify(opts)}: a local graph was drawn`);
    }
    const lonely = await openPage("p2-local/lonely.html");
    assert(lonely.document.getElementById("okf-context").hidden, "a page with no contents, no backlinks and no neighbour showed an empty side panel");
  });
}
```

Then replace

```js
function register(h) {
  registerPure(h);
}
```

with

```js
function register(h) {
  registerPure(h);
  registerPage(h);
}
```

- [ ] **Step 3: Run the harness and watch the page cases fail**

Run: `cd tools/viewer-security-check && npm test`
Expected: `pretest` regenerates the site (now with `p2-local/`); T1's cases still pass; every new page case except "nothing on the index…" FAILS (first one: `no #okf-local-graph on a page with neighbours`); P1 and P1.1 cases still pass; exit code 1.

- [ ] **Step 4: Write the page part**

In `src/OKF4net.Viewer/Assets/okf-local.js`, replace

```js
  window.OkfLocal = Object.freeze({
    CAP: CAP, VIEW: VIEW, build: build, segment: segment, lastSegment: lastSegment,
  });
})();
```

with

```js
  window.OkfLocal = Object.freeze({
    CAP: CAP, VIEW: VIEW, build: build, segment: segment, lastSegment: lastSegment,
  });

  // --- page part: a concept page only (spec §4.1, §12.4) -------------------
  var site = window.OkfSite;
  var shapes = window.OkfShapes;
  var html = document.documentElement;
  if (!site || !shapes || html.getAttribute("data-okf-view") !== "page") { return; }
  var context = document.getElementById("okf-context");
  var index = site.readIndex(window);
  if (!context || !index) { return; }
  var currentId = html.getAttribute("data-okf-concept");
  var centre = -1;
  for (var i = 0; i < index.concepts.length; i++) {
    if (index.concepts[i].id === currentId) { centre = i; break; }
  }
  if (centre < 0) { return; }
  // The first ring is computed on load, by scanning the edges (no adjacency
  // structure is kept); the second hop when "2 hops" is pressed (spec §3.6).
  // No neighbour, no section (§12.4).
  var first = build(index, centre, 1);
  if (first.total === 0) { return; }

  var SVG_NS = "http://www.w3.org/2000/svg";
  var DOT = " " + String.fromCharCode(0xb7) + " "; // " · ", built at run time
  var C = index.concepts.length;
  var root = site.rootOf(document);
  var nodeKeys = new Map(); // drawn <g> -> node key, replaced with each drawing

  function el(tag, className, text) { return site.element(document, tag, className, text); }

  function svg(name, className) {
    var node = document.createElementNS(SVG_NS, name);
    if (className) { node.setAttribute("class", className); }
    return node;
  }

  // Every number is checked finite before it becomes an attribute (§4.5).
  function num(value) {
    if (!Number.isFinite(value)) { throw new TypeError("okf-local: non-finite coordinate"); }
    return String(round(value));
  }

  function idOf(key) { return key < C ? index.concepts[key].id : index.ghosts[key - C].id; }
  function kindOf(key) { return key < C ? shapes.kindOf(index, key) : "ghost"; }
  function hopsText(hops) { return hops === 1 ? "1 hop" : "2 hops"; }
  function neighboursText(n) { return n + (n === 1 ? " neighbour" : " neighbours"); }

  // The palette's hook: a host may veto the navigation with preventDefault().
  function navigate(href) {
    var event = new CustomEvent("okf:navigate", { cancelable: true, detail: { href: href } });
    if (document.dispatchEvent(event)) { window.location.assign(href); }
  }

  // The mockup's arrow (spec X7: 7, to the target), filled by CSS (--edge).
  function marker(id, className) {
    var m = svg("marker");
    m.setAttribute("id", id);
    m.setAttribute("viewBox", "0 0 10 10");
    m.setAttribute("refX", "9");
    m.setAttribute("refY", "5");
    m.setAttribute("markerWidth", "7");
    m.setAttribute("markerHeight", "7");
    m.setAttribute("orient", "auto");
    var head = svg("path", className);
    head.setAttribute("d", "M0 0 L10 5 L0 10 Z");
    m.appendChild(head);
    return m;
  }

  function drawing(result) {
    var picture = svg("svg");
    picture.setAttribute("viewBox", "0 0 " + VIEW.width + " " + VIEW.height);
    picture.setAttribute("width", "100%");
    picture.setAttribute("height", String(VIEW.height));
    picture.setAttribute("role", "img");
    picture.setAttribute("focusable", "false");
    picture.setAttribute("aria-label", "Local graph of " + idOf(centre) + ", " + hopsText(result.hops) + ": "
      + (result.nodes.length - 1) + " of " + neighboursText(result.total) + " drawn; the list below names them all");
    var defs = svg("defs");
    defs.appendChild(marker("okf-local-arrow", "okf-local-arrowhead"));
    defs.appendChild(marker("okf-local-arrow-in", "okf-local-arrowhead okf-local-arrowhead-in"));
    picture.appendChild(defs);

    var sizes = [];
    var reach = [];
    for (var n = 0; n < result.nodes.length; n++) {
      var s = shapes.SIZES[n === 0 ? "localCenter" : "local"][kindOf(result.nodes[n].key)];
      sizes.push(s);
      // The arrow stops just outside the target; the centre's selection
      // square (side + 12, stroke 2) counts as part of it.
      reach.push(s.size / 2 + (n === 0 ? 7 : 1));
    }

    var lines = svg("g", "okf-local-edges");
    for (var k = 0; k < result.edges.length; k++) {
      var edge = result.edges[k];
      var a = result.nodes[edge.from];
      var b = result.nodes[edge.to];
      var seg = segment(a.x, a.y, reach[edge.from], b.x, b.y, reach[edge.to], edge.paired ? 3 : 0);
      if (!seg) { continue; }
      var line = svg("line", edge.dashed ? "okf-local-edge okf-local-edge-in" : "okf-local-edge");
      line.setAttribute("x1", num(seg.x1));
      line.setAttribute("y1", num(seg.y1));
      line.setAttribute("x2", num(seg.x2));
      line.setAttribute("y2", num(seg.y2));
      line.setAttribute("stroke-width", "1.4");
      if (edge.dashed) { line.setAttribute("stroke-dasharray", "4 3"); }
      line.setAttribute("marker-end", edge.dashed ? "url(#okf-local-arrow-in)" : "url(#okf-local-arrow)");
      lines.appendChild(line);
    }
    picture.appendChild(lines);

    nodeKeys = new Map();
    for (var j = 0; j < result.nodes.length; j++) {
      var node = result.nodes[j];
      var ghost = node.key >= C;
      var size = sizes[j];
      var g = shapes.node(kindOf(node.key), node.x, node.y, size.size, size);
      g.classList.add(ghost ? "okf-local-ghost" : "okf-local-node");
      if (j === 0) { g.classList.add("okf-selected"); }
      var title = svg("title");
      title.textContent = ghost ? "absent: " + idOf(node.key) : idOf(node.key);
      g.insertBefore(title, g.firstChild);
      var label = svg("text", "okf-local-label" + (j === 0 ? " okf-local-label-center" : "") + (ghost ? " okf-local-label-ghost" : ""));
      label.setAttribute("x", num(node.x));
      label.setAttribute("y", num(node.y + size.size / 2 + (j === 0 ? 6 : 0) + 14));
      label.setAttribute("text-anchor", "middle");
      label.textContent = lastSegment(idOf(node.key));
      g.appendChild(label);
      nodeKeys.set(g, node.key);
      picture.appendChild(g);
    }

    // Mouse only: the drawing is an image with no tab stop; the keyboard
    // path is the list and Referenced by (spec §8).
    picture.addEventListener("click", function (e) {
      for (var t = e.target; t && t !== picture; t = t.parentNode) {
        if (nodeKeys.has(t)) {
          var key = nodeKeys.get(t);
          if (key < C) { navigate(site.resolve(root, index.concepts[key].path)); }
          return; // a ghost is never navigable
        }
      }
    });
    return picture;
  }

  function hopButton(hops) {
    var button = el("button", "", hopsText(hops));
    button.type = "button";
    button.id = "okf-local-hops-" + hops;
    button.setAttribute("aria-pressed", hops === 1 ? "true" : "false");
    button.addEventListener("click", function () { render(build(index, centre, hops)); });
    return button;
  }

  var section = el("section", "okf-local");
  section.id = "okf-local-graph";
  section.setAttribute("aria-labelledby", "okf-local-title");
  var head = el("div", "okf-local-head");
  var heading = el("h2", "okf-section-title", "Neighbourhood");
  heading.id = "okf-local-title";
  var group = el("div", "okf-hops");
  group.setAttribute("role", "group");
  group.setAttribute("aria-label", "Neighbourhood depth");
  var hopButtons = [hopButton(1), hopButton(2)];
  group.appendChild(hopButtons[0]);
  group.appendChild(hopButtons[1]);
  head.appendChild(heading);
  head.appendChild(group);
  var canvas = el("div", "okf-local-canvas");
  var foot = el("p", "okf-local-foot");
  foot.appendChild(el("span", "", "solid = links to" + DOT + "dashed = referenced by"));
  // "Open in graph" repeats the header's own link, fragment included: the
  // header is the only source of the graph page's name (spec §12.3).
  var tools = document.getElementById("okf-tools");
  var globalLink = tools ? tools.querySelector("#okf-global-graph") : null;
  var graphHref = globalLink ? globalLink.getAttribute("href") : null;
  if (graphHref) {
    var openLink = el("a", "", "Open in graph");
    openLink.id = "okf-local-open";
    openLink.setAttribute("href", graphHref);
    foot.appendChild(openLink);
  }
  section.appendChild(head);
  section.appendChild(canvas);
  section.appendChild(foot);

  function render(result) {
    for (var b = 0; b < hopButtons.length; b++) {
      hopButtons[b].setAttribute("aria-pressed", b + 1 === result.hops ? "true" : "false");
    }
    canvas.textContent = "";
    canvas.appendChild(drawing(result));
  }

  render(first);
  var toc = document.getElementById("okf-toc");
  context.insertBefore(section, toc && toc.parentNode === context ? toc.nextSibling : context.firstChild);
  context.hidden = false;
})();
```

- [ ] **Step 5: Run the harness and watch it pass**

Run: `cd tools/viewer-security-check && npm test`
Expected: every `local graph:` case prints `  ok  -`; the P1 and P1.1 cases still pass (notably "a page with no h2/h3 and no backlinks keeps the side panel hidden", whose `constructor.html` has no neighbour, and the generalised chrome-classes case); the summary ends `0 failed`; exit code 0.

If "shapes are OkfShapes nodes…" fails only on the comparison of `outerHTML` because P1.1's `node` writes attributes in another order than a fresh call would (it cannot: both calls run the same function), re-read the failure: it means the drawn node was not built by `OkfShapes.node` at the label's position — a real defect.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-local.js tools/viewer-security-check/cases/p2.js tools/viewer-security-check/fixtures/hostile-bundle/p2-local/a.md tools/viewer-security-check/fixtures/hostile-bundle/p2-local/b.md tools/viewer-security-check/fixtures/hostile-bundle/p2-local/c.md tools/viewer-security-check/fixtures/hostile-bundle/p2-local/d.md tools/viewer-security-check/fixtures/hostile-bundle/p2-local/far.md tools/viewer-security-check/fixtures/hostile-bundle/p2-local/hub.md tools/viewer-security-check/fixtures/hostile-bundle/p2-local/lonely.md tools/viewer-security-check/fixtures/hostile-bundle/p2-local/self.md
git commit -m "$(cat <<'EOF'
feat(viewer): the Neighbourhood section draws the local graph (P2)

On a concept page, okf-local.js inserts a Neighbourhood section after the
contents list: a 1 hop / 2 hops toggle (aria-pressed), an SVG built by code
from the fixed vocabulary, with OkfShapes nodes at the local and localCenter
sizes, labels by textContent, directed edges dashed when they point back
toward the centre and offset when opposite, and the legend with "Open in
graph" repeating the header's link. Concept nodes navigate through the
resolver; ghosts never do. p2-local fixtures and page cases pin it.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: The equivalent list, "+N omitted", and the hostile and keyboard cases

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/okf-local.js` (the list skeleton after `section.appendChild(foot);`, `render`, new `row`, `relationText`)
- Modify: `tools/viewer-security-check/cases/p2.js` (`registerList`)

**Interfaces:**
- Consumes: T3's section, `render(result)`, `el`, `idOf`, `kindOf`, `neighboursText`, `DOT`; T1's `build(...).list` entries `{ key, dist, rel, via }`; §12.2 `OkfShapes.icon(kind, "icon", title)`, `OkfShapes.typeLabel(name)`; §12.6 shared `.okf-row` (P1.1 styles it under `#okf-context`); §12.7 `h.openPage` with `override`.
- Produces (DOM, §12.4 + X9): after the foot, `details.okf-local-list#okf-local-list` > `summary` "List · N neighbour(s)" + `ul` with one `li` per neighbour (drawn or not), in list order: a concept is `a.okf-row[href][title=<concept title>]`, a ghost `span.okf-row.okf-local-absent`; each holds `svg.okf-glyph` (OkfShapes icon, `<title>` = type label or "absent concept"), `span.okf-local-id` (id, or "absent: id"), `span.okf-local-rel` ("links to", "referenced by", "links to · referenced by", or "2 hops via `<id>`"). Above the cap, `button.okf-local-omitted[aria-controls=okf-local-list]` "+N omitted" inside `.okf-local-canvas`, after the SVG: it opens the list and focuses its summary. Redrawing keeps the list's open state.

- [ ] **Step 1: Write the failing cases**

In `tools/viewer-security-check/cases/p2.js`, insert above `function register(h) {`:

```js
function registerList(h) {
  const { checkAsync, assert, openPage } = h;
  const dot = String.fromCharCode(0xb7);

  checkAsync("local graph: over the cap, +N omitted opens the full list, in index order", async () => {
    const ids = ["p2-local/hub"];
    const links = [];
    for (let k = 0; k < 120; k++) {
      ids.push(`p2-local/n${String(k).padStart(3, "0")}`);
      links.push(k % 2 ? [k + 1, 0] : [0, k + 1]);
    }
    const ghostIds = ["p2-local/zz-0", "p2-local/zz-1", "p2-local/zz-2"];
    ghostIds.forEach((_, g) => links.push([0, g, "ghost"]));
    const source = `window.OKF_INDEX = ${JSON.stringify(siteIndex(ids, links, ghostIds))};`;
    const window = await openPage("p2-local/hub.html", { override: { "assets/okf-index.js": source } });
    const doc = window.document;
    const section = doc.getElementById("okf-local-graph");
    const total = 123;
    const cap = window.OkfLocal.CAP;
    const drawn = section.querySelectorAll("svg g.okf-node").length;
    assert(drawn === cap, `${drawn} nodes drawn`);
    const more = section.querySelector(".okf-local-canvas > button.okf-local-omitted");
    assert(more && more.type === "button" && more.textContent === `+${total - (cap - 1)} omitted`, `omitted: ${more && more.textContent}`);
    assert(more.getAttribute("aria-controls") === "okf-local-list", "+N omitted does not control the list");
    const details = doc.getElementById("okf-local-list");
    assert(details && details.localName === "details" && details.classList.contains("okf-local-list") && !details.open, "the list is not a closed <details>");
    const summary = details.querySelector("summary");
    assert(summary.textContent === `List ${dot} ${total} neighbours`, `summary: ${summary.textContent}`);
    const rows = Array.from(details.querySelectorAll("li"));
    assert(rows.length === total, `${rows.length} rows for ${total} neighbours`);
    assert(rows[0].querySelector(".okf-local-id").textContent === "p2-local/n000", `first row: ${rows[0].textContent}`);
    assert(rows[total - 1].querySelector(".okf-local-id").textContent === "absent: p2-local/zz-2", `last row: ${rows[total - 1].textContent}`);
    more.click();
    assert(details.open, "+N omitted did not open the list");
    // jsdom 29 focuses a details' first <summary>; recette X9 checks real browsers.
    assert(doc.activeElement === summary, `focus after +N omitted: <${doc.activeElement.localName}>`);
    doc.getElementById("okf-local-hops-2").click();
    assert(details.open, "redrawing at 2 hops closed the list");
    assert(details.querySelectorAll("li").length === total, "2 hops changed a list that has no second hop");
  });

  checkAsync("local graph: hostile titles and ids named after Object.prototype members stay inert and distinct", async () => {
    const ids = ["__proto__", "a/__proto__", "constructor", "hasOwnProperty", "toString"];
    const titles = ['<img src=x onerror="window.__pwned=1">Proto', "</title><script>window.__pwned=2</script>", "constructor",
      '"><svg onload="window.__pwned=3">', "toString"];
    const links = [[0, 1], [0, 2], [3, 0], [4, 0], [1, 2]];
    const source = `window.OKF_INDEX = ${JSON.stringify(siteIndex(ids, links, [], titles))};`;
    const window = await openPage("__proto__.html", { override: { "assets/okf-index.js": source } });
    const doc = window.document;
    const section = doc.getElementById("okf-local-graph");
    assert(section, "no local graph for the concept __proto__");
    // Spec §7 control 3: inspected after opening AND after redrawing.
    doc.getElementById("okf-local-hops-2").click();
    doc.getElementById("okf-local-hops-1").click();
    const svg = section.querySelector("svg");
    assertFixedSvg(assert, svg);
    const drawn = Array.from(svg.querySelectorAll("g.okf-node"), (g) => g.firstElementChild.textContent);
    assert(JSON.stringify(drawn) === JSON.stringify(ids), `drawn: ${JSON.stringify(drawn)}`);
    const listed = Array.from(section.querySelectorAll(".okf-local-list .okf-local-id"), (s) => s.textContent);
    assert(JSON.stringify(listed) === JSON.stringify(ids.slice(1)), `listed: ${JSON.stringify(listed)}`);
    const rows = Array.from(section.querySelectorAll(".okf-local-list a.okf-row"));
    rows.forEach((a, k) => assert(a.getAttribute("title") === titles[k + 1], `row ${k}: title attribute ${a.getAttribute("title")}`));
    const live = doc.getElementById("okf-context").querySelectorAll("img, script, iframe, object, [onerror], [onload]");
    assert(live.length === 0, `markup from a title became live in #okf-context (${live.length})`);
    assert(svg.textContent.indexOf("<") === -1, "a title reached the drawing");
    assert(window.__pwned === undefined, "a hostile title executed");
  });

  checkAsync("local graph: the drawing is an image with no tab stop; the toggle and the list are the keyboard path", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const svg = doc.querySelector("#okf-local-graph svg");
    assert(svg.getAttribute("role") === "img" && svg.getAttribute("focusable") === "false", "the drawing is not a non-focusable image");
    assert(!svg.hasAttribute("tabindex") && svg.querySelectorAll("[tabindex]").length === 0, "the drawing has a tab stop");
    const expected = expectedHood(window.OKF_INDEX, positionOf(window, "p2-local/c"), 1);
    const rows = Array.from(doc.querySelectorAll("#okf-local-list li"));
    assert(rows.length === expected.total, `${rows.length} rows for ${expected.total} neighbours`);
    for (const row of rows) {
      const line = row.firstElementChild;
      const ghost = line.querySelector(".okf-local-id").textContent.startsWith("absent: ");
      assert(ghost ? line.localName === "span" : line.localName === "a" && line.hasAttribute("href"), `row "${row.textContent}": <${line.localName}>`);
      assert(line.classList.contains("okf-row"), "a row is not a shared .okf-row");
      const glyph = line.firstElementChild;
      assert(glyph.localName === "svg" && glyph.classList.contains("okf-glyph") && glyph.getAttribute("aria-hidden") === "true",
        "a row's glyph is not an OkfShapes icon");
    }
    const rels = rows.map((r) => r.querySelector(".okf-local-rel").textContent);
    assert(rels.includes("links to") && rels.includes("referenced by"), `relations: ${JSON.stringify(rels)}`);
    doc.getElementById("okf-local-hops-2").click();
    const deep = Array.from(doc.querySelectorAll("#okf-local-list .okf-local-rel"), (s) => s.textContent);
    assert(deep.some((t) => /^2 hops via p2-local\//.test(t)), `2 hops relations: ${JSON.stringify(deep)}`);
    assert(doc.querySelector("#okf-local-graph .okf-local-omitted") === null, "+N omitted shown under the cap");
  });

  checkAsync("local graph: the list names both directions of a two-way link, and absent concepts without a link", async () => {
    const window = await openPage("p2-local/hub.html");
    const doc = window.document;
    const rows = Array.from(doc.querySelectorAll("#okf-local-list li"), (li) => ({
      id: li.querySelector(".okf-local-id").textContent,
      rel: li.querySelector(".okf-local-rel").textContent,
      tag: li.firstElementChild.localName,
    }));
    const a = rows.find((r) => r.id === "p2-local/a");
    assert(a && a.rel === `links to ${dot} referenced by`, `p2-local/a: ${JSON.stringify(a)}`);
    const absent = rows.filter((r) => r.id.startsWith("absent: "));
    assert(absent.length > 0 && absent.every((r) => r.tag === "span" && r.rel === "links to"), `absent rows: ${JSON.stringify(absent)}`);
    assert(!rows.some((r) => r.id === "p2-local/hub"), "the self-link listed the hub as its own neighbour");
  });
}
```

Then replace

```js
function register(h) {
  registerPure(h);
  registerPage(h);
}
```

with

```js
function register(h) {
  registerPure(h);
  registerPage(h);
  registerList(h);
}
```

- [ ] **Step 2: Run the harness and watch the new cases fail**

Run: `cd tools/viewer-security-check && npm test`
Expected: the four new cases FAIL (first: `+N omitted does not control the list`… or `omitted: null`); T1 and T3 cases pass; exit code 1.

- [ ] **Step 3: Add the list skeleton**

In `src/OKF4net.Viewer/Assets/okf-local.js`, replace

```js
  section.appendChild(head);
  section.appendChild(canvas);
  section.appendChild(foot);
```

with

```js
  section.appendChild(head);
  section.appendChild(canvas);
  section.appendChild(foot);
  // The equivalent list (spec §6, X9): every neighbour, drawn or not, with
  // its relation to this concept -- the keyboard path through the
  // neighbourhood (§8), since the drawing has no tab stop.
  var details = el("details", "okf-local-list");
  details.id = "okf-local-list";
  var summary = el("summary", "");
  var rows = el("ul", "");
  details.appendChild(summary);
  details.appendChild(rows);
  section.appendChild(details);
```

- [ ] **Step 4: Fill the list and add "+N omitted"**

In the same file, replace

```js
  function render(result) {
    for (var b = 0; b < hopButtons.length; b++) {
      hopButtons[b].setAttribute("aria-pressed", b + 1 === result.hops ? "true" : "false");
    }
    canvas.textContent = "";
    canvas.appendChild(drawing(result));
  }
```

with

```js
  // The relation a row names, from this concept's point of view (X8's
  // words); a second-hop row names the first direct neighbour, in index
  // order, it is reached through.
  function relationText(entry) {
    if (entry.dist === 2) { return "2 hops via " + idOf(entry.via); }
    if (entry.rel === 3) { return "links to" + DOT + "referenced by"; }
    return entry.rel === 1 ? "links to" : "referenced by";
  }

  function row(entry) {
    var item = el("li", "");
    var line;
    if (entry.key < C) {
      var concept = index.concepts[entry.key];
      line = el("a", "okf-row");
      line.setAttribute("href", site.resolve(root, concept.path));
      line.setAttribute("title", concept.title);
      line.appendChild(shapes.icon(kindOf(entry.key), "icon", shapes.typeLabel(concept.type)));
      line.appendChild(el("span", "okf-local-id", concept.id));
    } else {
      // A ghost is never navigable (spec A10): text, no link.
      line = el("span", "okf-row okf-local-absent");
      line.appendChild(shapes.icon("ghost", "icon", "absent concept"));
      line.appendChild(el("span", "okf-local-id", "absent: " + idOf(entry.key)));
    }
    line.appendChild(el("span", "okf-local-rel", relationText(entry)));
    item.appendChild(line);
    return item;
  }

  function render(result) {
    for (var b = 0; b < hopButtons.length; b++) {
      hopButtons[b].setAttribute("aria-pressed", b + 1 === result.hops ? "true" : "false");
    }
    canvas.textContent = "";
    canvas.appendChild(drawing(result));
    if (result.omitted > 0) {
      // Over the cap (spec A11): the rest is named in the list, which this
      // opens.
      var more = el("button", "okf-local-omitted", "+" + result.omitted + " omitted");
      more.type = "button";
      more.setAttribute("aria-controls", "okf-local-list");
      more.addEventListener("click", function () {
        details.open = true;
        summary.focus();
      });
      canvas.appendChild(more);
    }
    summary.textContent = "List" + DOT + neighboursText(result.total);
    rows.textContent = "";
    for (var k = 0; k < result.list.length; k++) { rows.appendChild(row(result.list[k])); }
  }
```

- [ ] **Step 5: Run the harness and watch it pass**

Run: `cd tools/viewer-security-check && npm test`
Expected: every `local graph:` case prints `  ok  -`; the summary ends `0 failed`; exit code 0.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-local.js tools/viewer-security-check/cases/p2.js
git commit -m "$(cat <<'EOF'
feat(viewer): the local graph's equivalent list and +N omitted (P2)

Under the drawing, "List · N neighbours" names every neighbour, drawn or
not, as a shared .okf-row with its OkfShapes glyph, its id and its relation
("links to", "referenced by", "2 hops via ..."); a ghost is text, never a
link. Over the 40-node cap, "+N omitted" in the frame opens that list and
focuses it. Cases pin the cap on a 123-neighbour hub, hostile titles and
Object.prototype ids, and the keyboard path.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: P2's CSS section, anchored, and its chrome probe

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` (replace `/* (P2 rules) */` under `/* === P2: local graph === */`)
- Create: `tools/viewer-security-check/fixtures/hostile-bundle/p2-chrome-classes.md`
- Modify: `tools/viewer-security-check/cases/p2.js` (`registerChrome`)

**Interfaces:**
- Consumes: §12.6 section `/* === P2: local graph === */` and its line `/* (P2 rules) */`; §11.0 tokens `--white`, `--ink`, `--blue`, `--blue-hover`, `--gray`, `--hair`, `--edge`, `--ghost`, `--mono` (P2 adds none); P1.1's shared `.okf-section-title`, `.okf-row`, shape colours (`okf-shape-*`, `okf-ghost-mark`, `.okf-node-ring` under `.okf-selected`); P1.1's generalised chrome-classes case, which walks every `*chrome-classes.html` page; `h.unwrapMedia`.
- Produces: X5, X6, X8, X9 dimensions and colours, label fonts (X7), edge and arrow colours; every selector starts with `#okf-context`. Fixture `p2-chrome-classes.md` listing P2's classes (§12.6).

- [ ] **Step 1: Write the fixture and the failing probe**

Create `tools/viewer-security-check/fixtures/hostile-bundle/p2-chrome-classes.md`:

```markdown
---
type: Note
title: P2 chrome classes
description: Body code wearing every class the local graph styles (spec 12.6).
---
Flat: <code class="okf-local-canvas okf-local okf-local-head okf-hops okf-local-omitted okf-local-foot okf-local-list okf-local-id okf-local-rel okf-local-absent okf-local-edges okf-local-edge okf-local-edge-in okf-local-arrowhead okf-local-arrowhead-in okf-local-label okf-local-label-center okf-local-label-ghost okf-local-node okf-local-ghost okf-node okf-selected okf-section-title okf-row">x</code>

Nested: <code class="okf-local-canvas"><code class="okf-local-omitted">+9 omitted</code></code>

Foot: <code class="okf-local-foot">solid = links to</code>

Reference: <code>x</code>
```

In `tools/viewer-security-check/cases/p2.js`, insert above `function register(h) {`:

```js
function registerChrome(h) {
  const { checkAsync, assert, openPage, unwrapMedia } = h;

  checkAsync("local graph: its rules are anchored to #okf-context and still style the real section", async () => {
    // The real section, over the cap so +N omitted exists.
    const ids = ["p2-local/hub"];
    const links = [];
    for (let k = 0; k < 50; k++) { ids.push(`p2-local/m${String(k).padStart(2, "0")}`); links.push([0, k + 1]); }
    const source = `window.OKF_INDEX = ${JSON.stringify(siteIndex(ids, links))};`;
    const window = await openPage("p2-local/hub.html", { override: { "assets/okf-index.js": source } });
    const doc = window.document;
    const probes = [
      ["#okf-local-graph .okf-local-canvas", "position", "relative"],
      ["#okf-local-graph .okf-local-canvas", "height", "250px"],
      ["#okf-local-graph .okf-hops", "display", "flex"],
      ["#okf-local-hops-1", "height", "24px"],
      ["#okf-local-hops-1", "font-size", "12px"],
      ["#okf-local-graph .okf-local-omitted", "position", "absolute"],
      ["#okf-local-graph .okf-local-foot", "display", "flex"],
      ["#okf-local-graph .okf-local-foot", "font-size", "12px"],
      ["#okf-local-open", "font-weight", "600"],
      ["#okf-local-list summary", "cursor", "pointer"],
    ];
    for (const [selector, prop, value] of probes) {
      const el = doc.querySelector(selector);
      assert(el, `${selector} is missing`);
      const got = window.getComputedStyle(el).getPropertyValue(prop);
      assert(got === value, `the real ${selector} lost its ${prop}: ${value} (got ${got})`);
    }

    // Body <code> wearing the same classes stays a plain <code>.
    const page = await openPage("p2-chrome-classes.html");
    const body = page.document.getElementById("okf-body");
    const reference = body.querySelector("code:not([class])");
    const worn = Array.from(body.querySelectorAll("code[class]"));
    assert(reference && worn.length >= 4 && worn[0].classList.contains("okf-local-canvas"),
      "the fixture lost its classed <code> elements, or the sanitizer dropped class: this case tests nothing");
    const props = ["position", "display", "height", "width", "border", "border-left", "background", "background-color",
      "color", "cursor", "font-family", "font-size", "font-weight", "padding", "margin", "margin-left", "right", "bottom",
      "flex", "list-style", "fill", "stroke", "overflow-wrap", "justify-content", "gap", "text-decoration"];
    const compare = (when) => {
      const expected = page.getComputedStyle(reference);
      for (const el of worn) {
        const style = page.getComputedStyle(el);
        for (const prop of props) {
          assert(style.getPropertyValue(prop) === expected.getPropertyValue(prop),
            `${when}: <code class="${el.getAttribute("class")}"> ${prop}: ${style.getPropertyValue(prop)} (an unclassed <code> has ${expected.getPropertyValue(prop)})`);
        }
      }
    };
    compare("as loaded");
    // jsdom applies no @media rule: compare again with every one unwrapped.
    const unwrapped = page.document.createElement("style");
    unwrapped.textContent = unwrapMedia(fs.readFileSync(path.join(__dirname, "..", ".generated", "hostile-site", "assets", "viewer.css"), "utf8"));
    page.document.head.appendChild(unwrapped);
    compare("with every @media rule applied");
  });
}
```

Then replace

```js
function register(h) {
  registerPure(h);
  registerPage(h);
  registerList(h);
}
```

with

```js
function register(h) {
  registerPure(h);
  registerPage(h);
  registerList(h);
  registerChrome(h);
}
```

- [ ] **Step 2: Run the harness and watch the probe fail**

Run: `cd tools/viewer-security-check && npm test`
Expected: the new case FAILS with `the real #okf-local-graph .okf-local-canvas lost its position: relative (got …)`; P1.1's generalised chrome-classes case passes on `p2-chrome-classes.html` (nothing styles those classes yet); exit code 1.

- [ ] **Step 3: Write the rules**

In `src/OKF4net.Viewer/Assets/viewer.css`, replace the line

```css
/* (P2 rules) */
```

with

```css
/* The Neighbourhood section okf-local.js inserts into #okf-context (spec
   §11.4 X5-X9, §12.4). Every rule is anchored to #okf-context: body <code>
   wearing these classes changes nothing (p2-chrome-classes.md). Colours come
   from the §11.0 tokens, so a theme switch redraws nothing. */
#okf-context .okf-local-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 8px; }
#okf-context .okf-local-head .okf-section-title { margin: 0; }
#okf-context .okf-hops { display: flex; flex: none; }
/* Longhands, not the font shorthand: jsdom drops every declaration that
   follows the shorthand set to inherit, so the harness would read 16px. */
#okf-context .okf-hops button {
  height: 24px; padding: 0 10px; font-family: inherit; font-weight: inherit; line-height: inherit; font-size: 12px; cursor: pointer;
  border: 1px solid var(--hair); background: var(--white); color: var(--gray);
}
#okf-context .okf-hops button + button { border-left-width: 0; }
#okf-context .okf-hops button[aria-pressed="true"] { border-color: var(--blue); background: var(--blue); color: var(--white); font-weight: 600; }
#okf-context .okf-local-canvas { position: relative; height: 250px; border: 1px solid var(--hair); }
#okf-context .okf-local-canvas svg { display: block; width: 100%; height: 248px; }
#okf-context .okf-local-omitted {
  position: absolute; right: 6px; bottom: 6px; padding: 1px 6px; cursor: pointer;
  font-family: var(--mono); font-size: 11px; color: var(--gray); background: var(--white); border: 1px solid var(--hair);
}
#okf-context .okf-local-foot { display: flex; justify-content: space-between; align-items: baseline; gap: 12px; margin: 8px 0 0; font-size: 12px; color: var(--gray); }
#okf-context .okf-local-foot a { flex: none; font-weight: 600; color: var(--blue); text-decoration: none; }
#okf-context .okf-local-foot a:hover { color: var(--blue-hover); }
#okf-context .okf-local-list { margin-top: 10px; font-size: 12px; }
#okf-context .okf-local-list summary { cursor: pointer; color: var(--gray); }
#okf-context .okf-local-list ul { list-style: none; margin: 4px 0 0; padding: 0; }
#okf-context .okf-local-id { min-width: 0; overflow-wrap: anywhere; }
#okf-context .okf-local-rel { flex: none; margin-left: auto; font-size: 12px; color: var(--gray); }
#okf-context .okf-local-absent { color: var(--ghost); }
#okf-context svg .okf-local-edge { fill: none; stroke: var(--edge); }
#okf-context svg .okf-local-arrowhead { fill: var(--edge); }
#okf-context svg .okf-local-label { font-family: var(--mono); font-size: 10px; fill: var(--ink); }
#okf-context svg .okf-local-label-center { font-size: 10.5px; font-weight: 700; }
#okf-context svg .okf-local-label-ghost { fill: var(--ghost); }
#okf-context svg .okf-local-node { cursor: pointer; }
```

- [ ] **Step 4: Run the harness and watch it pass**

Run: `cd tools/viewer-security-check && npm test`
Expected: every case passes, P1.1's generalised chrome-classes case included; summary `0 failed`; exit code 0.

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer"`
Expected: `Failed: 0` (the stylesheet is an embedded asset; the viewer tests still pass).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Viewer/Assets/viewer.css tools/viewer-security-check/fixtures/hostile-bundle/p2-chrome-classes.md tools/viewer-security-check/cases/p2.js
git commit -m "$(cat <<'EOF'
feat(viewer): style the local graph, anchored to #okf-context (P2)

P2's section of viewer.css: the 1 hop / 2 hops toggle, the 250 px frame,
+N omitted, the legend and Open in graph, the list, label fonts and --edge
lines and arrows. Every rule starts with #okf-context; a probe checks the
real section keeps its style while body <code> wearing the same classes
(p2-chrome-classes.md) stays plain, @media unwrapped included.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Recette `p2.js` and the `## P2` acceptance lines

**Files:**
- Create: `tools/viewer-security-check/recette/p2.js`
- Modify: `tools/viewer-security-check/ACCEPTANCE.md` (replace `*(P2 checks)*`)

**Interfaces:**
- Consumes: §12.8 (spec r7) `recette/recette.js --slices p2` calling `run(ctx)` with P1.1's `ctx` (ruling 11): `ctx.newPage({ viewport, colorScheme })`, `ctx.browserName`, `ctx.site`, `ctx.acme`, `ctx.wanted(id)`, `ctx.shot(page, id)`, `ctx.lib.rgb`, `ctx.lib.contrast`, `page.okfTracked.outside`; the driver's "n/a" for `pass: null`; the DOM contract of T3/T4 (**Produces**); `window.OkfShapes.SIZES`, `kindOf`; the acme_retail page `computations/gross-margin-period.html` (type Attested Computation, rank 1 → square, §12.1).
- Produces: `module.exports = { run }`, `run(ctx)` → `{ X5, X6, X7, X8, X9, "P2-1" … "P2-8" }` (only the ids `ctx.wanted` accepts), each `{ pass: true | false | null, … }` (`null` = not applicable, with a `note`); captures `<id>.png` under `--out/shots/<browser>/p2/` (written by `ctx.shot`); `ACCEPTANCE.md` lines `P2-1` … `P2-8`.

- [ ] **Step 1: Check P1.1's recette `ctx`**

```bash
grep -n "browserName\|newPage\|async shot\|wanted:\|lib: module.exports" tools/viewer-security-check/recette/lib.js
grep -n "n/a" tools/viewer-security-check/recette/recette.js
```

Expected: `lib.context` defines `browserName`, `wanted`, `newPage`, `shot` and `lib` (spec §12.8 r7), and `recette.js` prints `n/a` for a `pass: null` result without counting it as a failure. If either is missing, stop: P1.1 is not merged as planned (do not adapt `p2.js` to another shape).

- [ ] **Step 2: Write the recette**

Create `tools/viewer-security-check/recette/p2.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Recette of slice P2 (local graph): spec §11.4 X5-X9 and ACCEPTANCE.md
// "## P2" lines P2-1 to P2-8. Run by recette.js (--slices p2), in file://,
// never by npm test or CI (spec §12.8). It measures what jsdom cannot:
// geometry (getBBox), computed fonts and colours, contrasts in both themes,
// real clicks and navigation, 390 px, requests.
//
// ctx is P1.1's lib.context (spec §12.8): pages come from ctx.newPage(),
// captures go through ctx.shot(page, id), colours and contrasts through
// ctx.lib. A result { pass: null, note } means "not applicable": the driver
// prints it n/a and never counts it as a failure.
"use strict";
const fs = require("fs");
const { fileURLToPath } = require("url");

// acme_retail page whose type ranks second (Attested Computation -> square,
// spec §12.1): the mockup's centre, a 28 square in a 40 selection square.
const ACME_PAGE = "computations/gross-margin-period.html";
const CAP = 40;
const TOKENS = {
  light: { white: "#ffffff", blue: "#1a3fd6", gray: "#6a6a72", hair: "#e3e3e8", edge: "#8a8a94" },
  dark: { white: "#101014", blue: "#8fa5f5", gray: "#9a9aa2", hair: "#2a2a33", edge: "#8a8a94" },
};

async function run(ctx) {
  const { site, acme } = ctx;
  const { rgb, contrast } = ctx.lib;
  const results = {};
  const dot = String.fromCharCode(0xb7);
  // One page at ctx.newPage's defaults (1 440 x 900, light); P2-4 and P2-5
  // open their own.
  const page = await ctx.newPage();
  const check = async (id, fn) => {
    if (!ctx.wanted(id)) { return; }
    try { results[id] = await fn(); } catch (e) { results[id] = { pass: false, error: String(e).split("\n")[0] }; }
  };
  const shot = (id, p = page) => ctx.shot(p, id);
  // A theme is forced through data-theme, which both dark blocks honour.
  const open = async (url, theme = "light", p = page) => {
    await p.goto(url);
    await p.waitForSelector("#okf-local-graph .okf-local-canvas > svg");
    await p.evaluate((t) => document.documentElement.setAttribute("data-theme", t), theme);
  };

  await check("X5", async () => {
    await open(acme + ACME_PAGE);
    const r = await page.evaluate(() => {
      const one = document.getElementById("okf-local-hops-1");
      const two = document.getElementById("okf-local-hops-2");
      const s1 = getComputedStyle(one);
      const s2 = getComputedStyle(two);
      const b1 = one.getBoundingClientRect();
      const b2 = two.getBoundingClientRect();
      return {
        pressed: [one.getAttribute("aria-pressed"), two.getAttribute("aria-pressed")],
        height: [b1.height, b2.height], padding: [s1.paddingLeft, s1.paddingRight], fontSize: s1.fontSize,
        on: { bg: s1.backgroundColor, color: s1.color, weight: s1.fontWeight },
        off: { bg: s2.backgroundColor, color: s2.color, border: s2.borderTopColor },
        adjacent: Math.abs(b2.left - b1.right) < 0.5,
      };
    });
    await shot("X5");
    await page.click("#okf-local-hops-2");
    const after = await page.evaluate(() => ["okf-local-hops-1", "okf-local-hops-2"].map((id) => document.getElementById(id).getAttribute("aria-pressed")));
    await open(acme + ACME_PAGE);
    const reloaded = await page.evaluate(() => document.getElementById("okf-local-hops-1").getAttribute("aria-pressed"));
    const t = TOKENS.light;
    const pass = r.pressed.join() === "true,false" && r.height.every((v) => v === 24) && r.padding.join() === "10px,10px"
      && r.fontSize === "12px" && r.on.bg === rgb(t.blue) && r.on.color === rgb(t.white) && r.on.weight === "600"
      && r.off.bg === rgb(t.white) && r.off.color === rgb(t.gray) && r.off.border === rgb(t.hair) && r.adjacent
      && after.join() === "false,true" && reloaded === "true";
    return { pass, ...r, after, reloaded };
  });

  await check("X6", async () => {
    await open(acme + ACME_PAGE);
    const r = await page.evaluate(() => {
      const frame = document.querySelector("#okf-local-graph .okf-local-canvas");
      const s = getComputedStyle(frame);
      return {
        height: frame.getBoundingClientRect().height,
        border: [s.borderTopWidth, s.borderTopStyle, s.borderTopColor].join(),
        svgWidth: frame.querySelector("svg").getBoundingClientRect().width,
        inner: frame.clientWidth,
      };
    });
    await shot("X6");
    const pass = r.height === 250 && r.border === `1px,solid,${rgb(TOKENS.light.hair)}` && Math.abs(r.svgWidth - r.inner) < 0.5;
    return { pass, ...r };
  });

  await check("X7", async () => {
    await open(acme + ACME_PAGE);
    await page.click("#okf-local-hops-2");
    const r = await page.evaluate(() => {
      const idx = window.OKF_INDEX;
      const S = window.OkfShapes;
      const svg = document.querySelector("#okf-local-graph .okf-local-canvas > svg");
      const nodes = Array.from(svg.querySelectorAll("g.okf-node")).map((g) => {
        const title = g.querySelector("title").textContent;
        const ghost = title.startsWith("absent: ");
        const centre = g.classList.contains("okf-selected");
        const kind = ghost ? "ghost" : S.kindOf(idx, idx.concepts.findIndex((c) => c.id === title));
        const s = S.SIZES[centre ? "localCenter" : "local"][kind];
        const text = g.querySelector("text");
        const box = text.previousElementSibling.getBBox();
        const ts = getComputedStyle(text);
        const ring = g.querySelector(".okf-node-ring");
        const rs = ring && getComputedStyle(ring);
        return {
          title, kind, centre, expected: s.size - s.stroke, measured: Math.max(box.width, box.height),
          label: { size: ts.fontSize, weight: ts.fontWeight, family: ts.fontFamily },
          ring: ring ? { side: ring.getBBox().width, expected: s.size + 12, width: rs.strokeWidth, color: rs.stroke } : null,
        };
      });
      const lines = Array.from(svg.querySelectorAll("line.okf-local-edge")).map((l) => {
        const ls = getComputedStyle(l);
        return { dashed: l.classList.contains("okf-local-edge-in"), dash: l.getAttribute("stroke-dasharray"), stroke: ls.stroke, width: ls.strokeWidth, marker: l.getAttribute("marker-end") };
      });
      const marker = svg.querySelector("#okf-local-arrow");
      return { nodes, lines, marker: [marker.getAttribute("markerWidth"), marker.getAttribute("markerHeight")].join() };
    });
    await shot("X7-light");
    await page.evaluate(() => document.documentElement.setAttribute("data-theme", "dark"));
    await shot("X7-dark");
    const t = TOKENS.light;
    const nodesOk = r.nodes.every((n) => Math.abs(n.measured - n.expected) <= 0.6
      && n.label.size === (n.centre ? "10.5px" : "10px") && n.label.weight === (n.centre ? "700" : "400") && /Space Mono/.test(n.label.family)
      && (n.centre ? n.ring && n.ring.side === n.ring.expected && n.ring.width === "2px" && n.ring.color === rgb(t.blue) : n.ring === null));
    const linesOk = r.lines.length > 0 && r.lines.every((l) => l.stroke === rgb(t.edge) && l.width === "1.4px"
      && (l.dashed ? l.dash === "4 3" && l.marker === "url(#okf-local-arrow-in)" : l.dash === null && l.marker === "url(#okf-local-arrow)"));
    return { pass: nodesOk && linesOk && r.marker === "7,7", ...r };
  });

  await check("X8", async () => {
    await open(acme + ACME_PAGE);
    const r = await page.evaluate(() => {
      const foot = document.querySelector("#okf-local-graph .okf-local-foot");
      const a = document.getElementById("okf-local-open");
      const fs = getComputedStyle(foot);
      const as = a && getComputedStyle(a);
      const g = document.getElementById("okf-global-graph");
      return {
        text: foot.querySelector("span").textContent, size: fs.fontSize, color: fs.color,
        link: a && {
          text: a.textContent, weight: as.fontWeight, color: as.color, underline: as.textDecorationLine,
          href: a.getAttribute("href"), atRight: Math.abs(a.getBoundingClientRect().right - foot.getBoundingClientRect().right) < 1,
        },
        global: g && g.getAttribute("href"),
      };
    });
    await shot("X8");
    const t = TOKENS.light;
    const pass = r.text === `solid = links to ${dot} dashed = referenced by` && r.size === "12px" && r.color === rgb(t.gray)
      && Boolean(r.link) && r.link.text === "Open in graph" && r.link.weight === "600" && r.link.color === rgb(t.blue)
      && r.link.underline === "none" && r.link.atRight && r.link.href === r.global;
    return { pass, ...r };
  });

  await check("X9", async () => {
    await page.goto(site + "index.html");
    const hub = await page.evaluate((cap) => {
      const idx = window.OKF_INDEX;
      const C = idx.concepts.length;
      const sets = idx.concepts.map(() => new Set());
      for (const [from, to, , ghost] of idx.edges) {
        const key = ghost ? C + to : to;
        if (from === key) { continue; }
        sets[from].add(key);
        if (!ghost) { sets[to].add(from); }
      }
      const k = sets.findIndex((s) => s.size > cap - 1);
      return k === -1 ? null : { path: idx.concepts[k].path, total: sets[k].size };
    }, CAP);
    if (!hub) { return { pass: null, note: "no concept of this site has more than 39 neighbours; the cap is covered by the jsdom cases" }; }
    await open(site + hub.path);
    const r = await page.evaluate(() => {
      const section = document.getElementById("okf-local-graph");
      const more = section.querySelector(".okf-local-omitted");
      return {
        nodes: section.querySelectorAll(".okf-local-canvas > svg g.okf-node").length, more: more && more.textContent,
        summary: section.querySelector(".okf-local-list summary").textContent, rows: section.querySelectorAll(".okf-local-list li").length,
      };
    });
    await shot("X9");
    await page.click("#okf-local-graph .okf-local-omitted");
    const opened = await page.evaluate(() => ({ open: document.getElementById("okf-local-list").open, focus: document.activeElement.tagName }));
    const pass = r.nodes === CAP && r.more === `+${hub.total - (CAP - 1)} omitted` && r.summary === `List ${dot} ${hub.total} neighbours`
      && r.rows === hub.total && opened.open && opened.focus === "SUMMARY";
    return { pass, hub, ...r, opened };
  });

  await check("P2-1", async () => {
    await open(acme + ACME_PAGE);
    const target = await page.evaluate(() => {
      const g = document.querySelector("#okf-local-graph .okf-local-canvas > svg g.okf-local-node:not(.okf-selected)");
      const id = g.querySelector("title").textContent;
      return { id, path: window.OKF_INDEX.concepts.find((c) => c.id === id).path };
    });
    await page.locator("#okf-local-graph .okf-local-canvas > svg g.okf-local-node:not(.okf-selected)").first().click();
    await page.waitForURL((url) => url.href.endsWith(target.path));
    const landed = page.url();
    await page.goBack();
    return { pass: landed === acme + target.path, target, landed };
  });

  await check("P2-2", async () => {
    await open(acme + ACME_PAGE);
    const one = await page.locator("#okf-local-graph .okf-local-canvas > svg g.okf-node").count();
    await page.click("#okf-local-hops-2");
    const two = await page.locator("#okf-local-graph .okf-local-canvas > svg g.okf-node").count();
    await shot("P2-2-two-hops");
    return { pass: two > one, one, two };
  });

  await check("P2-3", async () => {
    const measured = {};
    let pass = true;
    for (const theme of ["light", "dark"]) {
      await open(acme + ACME_PAGE, theme);
      const m = await page.evaluate(() => {
        const svg = document.querySelector("#okf-local-graph .okf-local-canvas > svg");
        const shapes = Array.from(svg.querySelectorAll("g.okf-node > :is(circle, rect, path):not(.okf-node-ring):not(.okf-node-focus)")).map((s) => {
          const cs = getComputedStyle(s);
          return cs.fill === "none" || s.classList.contains("okf-ghost-mark") ? cs.stroke : cs.fill;
        });
        return {
          bg: getComputedStyle(document.body).backgroundColor,
          shapes,
          edges: Array.from(svg.querySelectorAll("line.okf-local-edge"), (l) => getComputedStyle(l).stroke),
          labels: Array.from(svg.querySelectorAll("text.okf-local-label:not(.okf-local-label-ghost)"), (t) => getComputedStyle(t).fill),
          foot: getComputedStyle(document.querySelector("#okf-local-graph .okf-local-foot")).color,
        };
      });
      const min = (list) => (list.length ? Math.min(...list.map((c) => contrast(c, m.bg))) : null);
      const r = { shapes: min(m.shapes), edges: min(m.edges), labels: min(m.labels), foot: contrast(m.foot, m.bg) };
      measured[theme] = r;
      pass = pass && r.shapes !== null && r.shapes >= 3 && r.edges !== null && r.edges >= 3 && r.labels !== null && r.labels >= 4.5 && r.foot >= 4.5;
      await shot(`P2-3-${theme}`);
    }
    return { pass, ...measured };
  });

  await check("P2-4", async () => {
    const narrow = await ctx.newPage({ viewport: { width: 390, height: 844 } });
    await open(acme + ACME_PAGE, "light", narrow);
    const r = await narrow.evaluate(() => ({
      scroll: document.documentElement.scrollWidth, inner: window.innerWidth,
      svg: document.querySelector("#okf-local-graph .okf-local-canvas > svg").getBoundingClientRect().width,
    }));
    await shot("P2-4-390", narrow);
    return { pass: r.scroll <= r.inner && r.svg > 0, ...r };
  });

  await check("P2-5", async () => {
    // A fresh page, so okfTracked holds this check's requests only; it lists
    // every request outside both sites (data: and about: excepted).
    const fresh = await ctx.newPage();
    await open(acme + ACME_PAGE, "light", fresh);
    await fresh.click("#okf-local-hops-2");
    await fresh.click("#okf-local-hops-1");
    const outside = fresh.okfTracked.outside.slice();
    return { pass: outside.length === 0, outside };
  });

  await check("P2-6", async () => {
    await open(acme + ACME_PAGE);
    const href = await page.getAttribute("#okf-local-open", "href");
    const target = new URL(href, page.url());
    if (!fs.existsSync(fileURLToPath(target.href.split("#")[0]))) {
      return { pass: null, note: "the graph page is not written yet (P3): not a P2 defect (spec §9)", href };
    }
    await page.click("#okf-local-open");
    await page.waitForURL((url) => url.href.split("#")[0] === target.href.split("#")[0]);
    return { pass: new URL(page.url()).hash === target.hash, href, landed: page.url() };
  });

  await check("P2-7", async () => {
    await open(acme + ACME_PAGE);
    await page.focus("#okf-local-hops-2");
    const stops = [];
    for (let k = 0; k < 4; k++) {
      await page.keyboard.press("Tab");
      const stop = await page.evaluate(() => {
        const a = document.activeElement;
        return { tag: a.tagName, id: a.id, inSvg: Boolean(a.closest("svg")) };
      });
      stops.push(stop);
      if (stop.tag === "SUMMARY") { break; }
    }
    await page.keyboard.press("Enter");
    await page.keyboard.press("Tab");
    const row = await page.evaluate(() => {
      const a = document.activeElement;
      return { tag: a.tagName, row: a.classList.contains("okf-row"), open: document.getElementById("okf-local-list").open };
    });
    const pass = stops.every((s) => !s.inSvg) && stops.some((s) => s.tag === "SUMMARY") && row.open && row.tag === "A" && row.row;
    return { pass, stops, row };
  });

  if (ctx.wanted("P2-8")) {
    results["P2-8"] = { pass: null, note: "compare the captures shots/<browser>/p2/X5.png to X9.png with mockup A's Neighbourhood section by hand (ACCEPTANCE.md P2-8)" };
  }
  return results;
}

module.exports = { run };
```

- [ ] **Step 3: Write the acceptance lines**

In `tools/viewer-security-check/ACCEPTANCE.md`, replace the line

```markdown
*(P2 checks)*
```

with

```markdown
`recette/recette.js --slices p2` measures each line below in Chrome, Edge and Firefox (ids are its result keys, beside §11's X5–X9); what it reports `n/a` (`pass: null`, with its note) is checked by hand or waits for P3.

- [ ] **P2-1** On a concept page with neighbours (acme `computations/gross-margin-period.html`), "Neighbourhood" sits between "On this page" and "Referenced by"; clicking a neighbour opens its page in `file://`; clicking an absent concept (red dashed circle) does nothing.
- [ ] **P2-2** "1 hop" is pressed on load and after a reload; "2 hops" adds the second ring, arrows point at their targets, links into the page are dashed as the legend says.
- [ ] **P2-3** Shapes and lines contrast at least 3:1 and labels and the legend 4.5:1 with the page, in the light and the dark theme; switching the theme redraws nothing and keeps the graph readable.
- [ ] **P2-4** At 390 px wide, the frame fits the panel without horizontal scrolling (a label at the frame's edge may be clipped; the list names it in full).
- [ ] **P2-5** No request leaves the site while the local graph loads or redraws.
- [ ] **P2-6** "Open in graph" opens the graph page selected on this concept (from P3 on; before P3 the missing page is expected, spec §9).
- [ ] **P2-7** Keyboard: Tab from "2 hops" never stops inside the drawing; it reaches "+N omitted" (when shown), "Open in graph", then "List · N neighbours", which Enter opens, and each row.
- [ ] **P2-8** Side by side at 1440 × 900 with mockup A's Neighbourhood section (captures `shots/<browser>/p2/X5.png` … `X9.png` under the recette's `--out`): toggle, frame, shapes, labels, edges and foot match, or the difference is one of the écarts recorded in the P2 plan's "Fidélité maquette".
```

- [ ] **Step 4: Run the recette once**

```bash
cd tools/viewer-security-check
dotnet run --project ../../src/OKF4net.Render -c Release -- ../../bundles/acme_retail --out "$TEMP/p2-recette/acme-site"
dotnet run --project ../../producers/src/OkfProducer.Cli -c Release -- generate --repo ../.. --out "$TEMP/p2-recette/okf4net-bundle" --no-msbuild
dotnet run --project ../../src/OKF4net.Render -c Release -- "$TEMP/p2-recette/okf4net-bundle" --out "$TEMP/p2-recette/okf4net-site"
node recette/recette.js --site "$TEMP/p2-recette/okf4net-site" --acme "$TEMP/p2-recette/acme-site" --out "$TEMP/p2-recette/out" --browsers chrome,edge,firefox --slices p2
```

Expected: the recette prints, per browser, `X5` to `X9` and `P2-1` to `P2-7` as `ok` — except `P2-6`, printed `n/a -- the graph page is not written yet (P3)…` until P3 writes the graph page, and `P2-8`, `n/a` by design (a hand comparison) —, ends with `no check failed, N not applicable; …` and exit code 0 (P1.1's driver never counts `pass: null` as a failure), and writes the captures under `$TEMP/p2-recette/out/shots/<browser>/p2/`. A `false` is a defect to fix in T3–T5 (never by loosening the recette); the report goes in the PR description, not in the repository (§12.8).

- [ ] **Step 5: Commit**

```bash
git add tools/viewer-security-check/recette/p2.js tools/viewer-security-check/ACCEPTANCE.md
git commit -m "$(cat <<'EOF'
test(viewer): recette and acceptance lines of the local graph (P2)

recette/p2.js measures X5-X9 in real browsers (sizes by getBBox, computed
fonts and colours, contrasts in both themes, 390 px, real navigation, the
cap on the OKF4net site, the keyboard path, requests) and ACCEPTANCE.md gets
the P2-1 to P2-8 lines it reports against.

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 7: Full verification and hand-off

**Files:**
- None created or modified (verification only; any defect found goes back to the task that owns the code).

**Interfaces:**
- Consumes: everything above; `P2_BASE` from T1 Step 0.
- Produces: the evidence the slice is done, and the hand-off to the controller (docs wording, merge).

- [ ] **Step 1: Build and test everything**

```bash
dotnet build OKF4net.sln
dotnet test OKF4net.sln
dotnet format OKF4net.sln --verify-no-changes
cd tools/viewer-security-check && npm test
```

Expected: `Build succeeded.` with `0 Warning(s)` and `0 Error(s)`; `dotnet test` reports `Failed: 0` for every test assembly (the `ContainerIntegration` tests skip without Docker, which is expected here); `dotnet format` exits 0 with no output; `npm test` ends `… passed, 0 failed` with exit code 0.

- [ ] **Step 2: Check the native binary writes and serves the script**

```bash
dotnet publish src/OKF4net.Render -c Release -o "$TEMP/p2-aot"
"$TEMP/p2-aot/okf-render" bundles/acme_retail --out "$TEMP/p2-aot-site"
ls "$TEMP/p2-aot-site/assets/okf-local.js"
grep -c "assets/okf-local.js" "$TEMP/p2-aot-site/computations/gross-margin-period.html"
```

Expected: the publish succeeds; `ls` lists the file; `grep -c` prints `1`. (On Windows the binary is `okf-render.exe`; Git Bash resolves `okf-render` to it.)

- [ ] **Step 3: Check P2 wrote only its own files**

```bash
git diff --name-only "$P2_BASE"..HEAD -- . ':!src/OKF4net.Viewer/Assets' ':!tools/viewer-security-check' ':!tests/OKF4net.Tests/Viewer/HtmlWriterLocalGraphTests.cs'
git diff "$P2_BASE"..HEAD --stat -- src/OKF4net.Viewer/Assets tools/viewer-security-check
```

Replace `$P2_BASE` with the SHA written down in T1 Step 0. Expected: the first command lists only `src/OKF4net.Viewer/HtmlWriter.cs` plus the files brought in by the P1.1 merge of T2 Step 0 (compare with `git diff --name-only "$P2_BASE"..feat/viewer-interactive-p1`: anything else is a P2 edit outside its zone, and goes back); in `HtmlWriter.cs`, `git diff feat/viewer-interactive-p1..HEAD -- src/OKF4net.Viewer/HtmlWriter.cs` shows exactly one added line, `"okf-local.js",`. The `--stat` lists only `okf-local.js`, `viewer.css` (P2 section), `cases/p2.js`, `fixtures/hostile-bundle/p2-*`, `ACCEPTANCE.md`, `recette/p2.js` beyond P1.1's own changes.

- [ ] **Step 4: Check `viewer.js` is unchanged**

```bash
git diff --exit-code "$P2_BASE"..HEAD -- src/OKF4net.Viewer/Assets/viewer.js && echo "viewer.js unchanged"
git diff --exit-code feat/viewer-interactive-p1..HEAD -- src/OKF4net.Viewer/Assets/viewer.js && echo "viewer.js unchanged by P2"
```

Expected: `viewer.js unchanged by P2` (the first line may differ only if P1.1 itself changed `viewer.js`, which §4.1 forbids — report it if so). P1's "viewer.js alone" harness case already passed in Step 1.

- [ ] **Step 5: Hand the documentation text to the controller (no edit, ruling 10, spec r7 §12.0)**

```bash
grep -n "okf-local" CLAUDE.md src/OKF4net.Viewer/README.md CHANGELOG.md tools/viewer-security-check/README.md
```

P2 edits none of these files. **Controller hand-off:** the controller applies the text below after P2 **and** P3 are merged into `feat/viewer-interactive-p1`, in its own worktree, outside P2's and P3's (P1.1 plan, Task 0 Step 6) — P3 edits `CLAUDE.md` and the viewer README after P1.1, so this text goes on top of P3's. Put this whole step's wording, unchanged, into the final report (Step 7), marking each edit "already present" when the grep above shows it is:

- `CLAUDE.md`, `src/OKF4net.Viewer/` paragraph, edit 1 — replace `` `okf-toc.js`, `okf-page.js` after `viewer.js`) `` with `` `okf-toc.js`, `okf-page.js`, `okf-local.js` after `viewer.js`) ``.
- `CLAUDE.md`, same paragraph, edit 2 — insert directly after the sentence ending `` (design: `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`). `` (before the global-graph sentence P3 adds there):

  ```markdown
  `okf-local.js` (P2) draws a concept page's local graph — 1 or 2 hops, rings, 40 nodes at most centre included, then "+N omitted" and the equivalent list — from the site index alone, into `#okf-context`; its pure part `OkfLocal` is exercised by `tools/viewer-security-check/cases/p2.js`, its shapes come from `okf-shapes.js` only.
  ```

- `src/OKF4net.Viewer/README.md` — insert directly above P3's `## Global graph` heading (or append at the end if that heading is absent):

  ```markdown
  ## Local graph

  Every concept page with at least one neighbour shows a "Neighbourhood"
  section in its side panel, drawn by `okf-local.js` from the site index
  alone: the concept and its neighbours at one hop, or two with the "2 hops"
  button; solid lines for the links the page makes, dashed ones for the links
  it receives; absent concepts as dashed circles that are never links; at most
  40 nodes, then "+N omitted"; an equivalent list naming every neighbour and
  its relation; and "Open in graph", which opens the global graph on this
  concept.
  ```

- `CHANGELOG.md` — only if P1.1's P2 line claims something P2 did not deliver or omits one of the behaviours below; then replace the entry's P2 item (from `- *P2 — local graph.*` through `with an equivalent list.`, three lines) with:

  ```markdown
    - *P2 — local graph.* Every concept page with a neighbour shows its
      neighbourhood in the context panel: one or two hops, solid and dashed
      directed edges, absent concepts as ghosts, at most 40 nodes with
      "+N omitted", a full neighbour list, and "Open in graph".
  ```

- [ ] **Step 6: Refresh the knowledge graph**

```bash
graphify update .
```

Expected: the command completes (AST-only; `graphify-out/` is git-ignored, nothing to stage).

- [ ] **Step 7: Hand back**

Report to the controller: the branch `feat/viewer-p2` and its head SHA, the Step 1 summaries, the recette results of T6 Step 4 per browser, and the **controller hand-off** of Step 5 — its four edits verbatim, each marked "to apply" or "already present" — for the controller to apply after P2 and P3 are merged (P1.1 plan, Task 0 Step 6). The controller merges it into `feat/viewer-interactive-p1` (`git merge --no-ff feat/viewer-p2` in that branch's worktree); no conflict is expected, P2's edits being confined to its markers and files (§12.0). Do not merge or push from this worktree.
