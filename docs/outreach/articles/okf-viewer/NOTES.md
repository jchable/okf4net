# Viewer launch article: owner notes

Notes for publishing [`article.md`](article.md), the launch article of the
interactive `okf-render` viewer. This is **not** the J1 flagship article of
the launch sequence (`docs/outreach/README.md` keeps that row open): it is a
feature launch with its own row in that table.

Audience and tone follow
`docs/superpowers/specs/2026-07-27-communication-plan-design.md`: .NET
developers and people building agent systems, objective = contributors,
OKF4net framed as an independent implementation of the OKF spec (not a port),
English first, no hype words.

## State at publication (checked on 2026-10-14 against v0.7.0)

The article was written while the viewer was on `dev` and rewritten when
OKF4net 0.7.0 shipped it, so it now names the release. What was checked, and
what is left for the owner.

Checked:

- **The viewer is in a release.** `v0.7.0` carries `okf-render` archives for
  Windows, Linux and macOS on x64 and arm64, and `checksums.txt`
  (`gh release view v0.7.0`). The article says so and no longer says "dev
  branch" or "next release". `install.sh` accepts `--bin okf-render` (its
  header documents it); it could not be dry-run on the Windows machine the
  article was prepared on, so the article claims only what the script's own
  help says.
- **winget.** `Coderise.OKF4net.Render` was submitted for 0.6.0
  (microsoft/winget-pkgs PR 444993) and was still open on 2026-10-14; the
  0.7.0 submission for it is skipped by the workflow until
  `WINGET_RENDER_PUBLISHED` is set. The article says "submitted and waiting
  in the moderation queue". Re-check before publishing: if that PR has been
  merged, the sentence in *Getting it* must change.
- **Links.** The docs page <https://jchable.github.io/okf4net/docs/viewer/>
  returned 200 on 2026-10-14 (the site deploys from `main` on every release).
  #163 and #177 were open and still about the VS Code extension and the IME /
  screen-reader checks. The `good first issue` label listed six open issues.
- **The harness count.** `npm test` in `tools/viewer-security-check/` at the
  release commit `90adf19` printed "453 passed, 0 failed", as the article says.
- **The nuget.org side.** The seven 0.7.0 packages, `OKF4net.Cli` included,
  were pushed by the release workflow on 2026-10-14; confirm they are listed
  (`curl -s https://api.nuget.org/v3-flatcontainer/okf4net.cli/index.json`)
  before the article says `dotnet tool install OKF4net.Cli` works.

Left for the owner:

- [ ] **Images.** Run the `sed` line in the next section with `TAG=v0.7.0` and
  check every image in the dev.to preview. Choose a cover image (none is
  generated at dev.to's 1000 × 420 ratio; `graph-synthetic.png` or
  `concept-page.png` crop acceptably) and uncomment `cover_image`.
- [ ] **Front matter.** Set `canonical_url` to the personal-site copy (the
  canonical rule of `docs/outreach/README.md`), keep `published: false` until
  the final check, and confirm the four tags (`dotnet, opensource, showdev, ai`;
  `a11y` or `javascript` are reasonable swaps).
- [ ] **Delete the HTML comment** at the top of the body (owner notes).
- [ ] **Medium.** Medium does not render `<kbd>`: replace `<kbd>X</kbd>` with
  plain `X` in the Medium copy (`sed -E 's#</?kbd>##g'`), and import with
  `rel=canonical` pointing at the personal site.

## Image URLs for dev.to

The article uses relative paths (`img/...`) so it renders in the repository.
dev.to needs absolute URLs. Use the release tag: it holds the images and,
unlike a branch, never moves.

```sh
TAG=v0.7.0
sed "s#](img/#](https://raw.githubusercontent.com/jchable/okf4net/${TAG}/docs/outreach/articles/okf-viewer/img/#g; s#/<TAG>/#/${TAG}/#g" \
  docs/outreach/articles/okf-viewer/article.md > article.devto.md
```

The second expression fills the `<TAG>` placeholder in the commented
`cover_image` line. Do not commit `article.devto.md`. PowerShell equivalent:

```powershell
$t = 'v0.7.0'
(Get-Content docs/outreach/articles/okf-viewer/article.md -Raw) `
  -replace '\]\(img/', "](https://raw.githubusercontent.com/jchable/okf4net/$t/docs/outreach/articles/okf-viewer/img/" `
  -replace '/<TAG>/', "/$t/" | Set-Content article.devto.md -NoNewline
```

## Images

All 15 are PNG, made by `tools/viewer-security-check/recette/article-shots.js`
(documented in `tools/viewer-security-check/recette/README.md`) with Chrome at
a 1440 × 900 viewport (390 × 844 for the phone), the clock frozen at
2026-10-09. Two runs give byte-identical files. Content is only
`bundles/acme_retail` (the repository's sample bundle, fictional company), its
render by `okf-render` 0.6.0 for the "before" capture, and a synthetic bundle
the script generates from a fixed seed (112 concepts, fictional names, never
written into the repository). The OKF4net source bundle is **not** used: it is
not in the repository, so its captures could not be reproduced.

| File | Pixels | Size | Shows | Bundle |
|---|---|---|---|---|
| `before-static-site.png` | 1440 × 900 | 62 KB | a concept page as `okf-render` 0.6.0 wrote it | acme_retail |
| `concept-page.png` | 1440 × 900 | 158 KB | concept page, Neighbourhood at 2 hops | acme_retail |
| `explorer.png` | 578 × 1208 (2×) | 76 KB | explorer, two type chips pressed, a name filter | synthetic |
| `palette-sequence.png` | 1440 × 351 | 135 KB | 3 frames: Ctrl+K, a query, arrow keys | acme_retail |
| `palette.png` | 1276 × 720 (2×) | 62 KB | the palette with the query "refund" | synthetic |
| `neighbourhood-cap.png` | 668 × 712 (2×) | 152 KB | the side-panel Neighbourhood of a hub at the 40-node cap | synthetic |
| `neighbourhood-enlarged.png` | 1120 × 740 | 203 KB | the same neighbourhood in the enlarged dialog | synthetic |
| `graph-facets-drawer.png` | 1440 × 900 | 136 KB | global graph, tag facet, dimmed, selection and drawer | acme_retail |
| `graph-synthetic.png` | 800 × 732 | 231 KB | the global graph's drawing of 112 concepts | synthetic |
| `layout-sequence.png` | 1140 × 420 | 228 KB | 3 frames of the layout: start, mid, done (labels off) | synthetic |
| `keyboard-sequence.png` | 1440 × 486 | 89 KB | 3 frames: Tab + Home, ←, Space | acme_retail |
| `graph-list.png` | 820 × 734 | 82 KB | the List equivalent of the global graph | acme_retail |
| `splitter.png` | 1440 × 900 | 136 KB | graph page, drawer widened by the splitter, focus ring | acme_retail |
| `graph-dark.png` | 1440 × 900 | 136 KB | global graph, dark theme | acme_retail |
| `phone.png` | 780 × 1688 (2×) | 149 KB | a concept page at 390 px | acme_retail |
| **Total** | | **2,035 KB** | | |

To regenerate: `OKF_PLAYWRIGHT=<path to playwright-core> node
tools/viewer-security-check/recette/article-shots.js` (needs the .NET SDK and a
`v0.6.0` tag; `--no-before` skips the "before" capture). Look at every PNG
before committing.

## Claims and where they are proven

Every factual claim of the article, with the file that proves it. "Spec" is
`docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md` (r15);
"Viewer README" is `src/OKF4net.Viewer/README.md`; "docs page" is
`web/src/pages/docs/Viewer.tsx`.

| Claim | Proof |
|---|---|
| A bundle is a directory of markdown files with YAML frontmatter; `type` is the only required field; id = path without `.md` | `README.md` "What OKF is"; `docs/spec/SPEC.md` §2–§4, §11 |
| `verified` (§5.2), `stale_after` (§5.5); trust tiers are "advisory signals, not access control" | `docs/spec/SPEC.md` §5.2, §5.5, line "Trust tiers are advisory signals, not access control" |
| OKF4net: independent implementation built from the spec, BCL only, own YAML-subset parser and link scanner | `README.md` intro; `CLAUDE.md` "What this is", "Hard rules" |
| `okf-render` 0.6.0 wrote one page per concept with a frontmatter table and an index, no transversal navigation | spec §1; the `before-static-site.png` capture of the `v0.6.0` tag |
| Output: `index.html`, a page per concept at its path, `graph.html` (`graph-1.html` on collision), `assets/` with scripts, CSS, fonts, licences, `okf-index.js` | docs page "What you get"; spec §12.6, A25; CHANGELOG [0.7.0] P3 |
| Opens from `file://`, nothing fetched from the network | Viewer README; spec control 14; recette `C14-requests` |
| `okf-render` is a self-contained Native AOT binary, separate from `okf` so `okf` carries no marked or fonts | `README.md` "`okf-render` — generate a static HTML site"; `CLAUDE.md` `OKF4net.Render` |
| Released in 0.7.0; builds from the tag with `dotnet run --project src/OKF4net.Render` (.NET SDK 10+) | CHANGELOG [0.7.0]; `README.md` Building & testing |
| Release archives for Windows, Linux, macOS on x64 and arm64, with `checksums.txt`; `install.sh --bin okf-render`; winget package submitted (PR 444993), not merged | `gh release view v0.7.0` (assets); `packaging/install.sh` header; `gh api repos/microsoft/winget-pkgs/pulls/444993` (open on 2026-10-14) |
| Header: bundle name, counts, Jump to, Filters, Global graph, theme; *Reading view* on the graph page | docs page "anatomy"; spec §2, §11.1 |
| Three columns from 1,100 px; stacked below (page, context, explorer) | docs page; spec §10 R6 |
| Chips: type, status, trust with verifier and date, staleness; frontmatter folded to four entries not already shown | CHANGELOG P1.1; spec A15, A27 |
| Body rendered in the browser through the sanitizer; broken links flagged; `okf-h-` heading anchors make `#usage` land | CHANGELOG P1; spec §5; docs page "Reading a concept" |
| Concept that is also a folder: open and expand separately; rows labelled by last id segment, title as tooltip | spec §3.2, A22; docs page "The explorer" |
| Five most frequent types get circle, square, diamond, triangle, ring; others share a sixth shape; same shape everywhere | spec §2, A19; docs page; `okf-shapes.js` |
| human-reviewed = at least one `human:` stamp; machine-confirmed = stamps, none human; no `verified`, no mark; tiers from `ConceptAudit` | `src/OKF4net/Trust.cs` (`verified.Any(s => s.By is { IsHuman: true })`); spec §2.1, A1 |
| Staleness evaluated as of reading, on load and on `visibilitychange` | spec §4.4, A9; `okf-page.js`, `okf-explorer.js`, `okf-graph.js` (`visibilitychange`) |
| Palette: Ctrl+K or `/`; tiers exact id, prefix of title/id, substring, exact tag; ties by index order; no weights; never reads bodies | `okf-site.js` `rank` (comment and code); spec §4.6, §8 |
| One full-text scorer `ConceptSearch`, shared by Agents and Catalog; not forked into JS; body search via library and `okf-mcp`; VS Code extension #163; no `okf serve` | `CLAUDE.md` (`ConceptSearch`, Viewer paragraph); docs page "What it does not do"; `README.md` |
| Neighbourhood: 1 or 2 hops, rings, ≤ 40 nodes incl. centre, direct neighbours first, "+N omitted", ghosts never navigate, List of every neighbour with relation, Open in graph | Viewer README "Neighbourhood"; `okf-local.js` (`CAP = 40`); spec §4.3, A11 |
| Legend "solid = links to · dashed = referenced by" | the captures; docs page |
| Enlarge: dialog up to 1,100 × 760 px, list beside, labels cut at 32 instead of 20 from 560 px wide | docs page; spec r15 (X12), `OkfLocal.LARGE` |
| Hub of the synthetic bundle: 61 neighbours, 39 drawn, +22 omitted | `neighbourhood-cap.png`; `article-shots.js` `synthBundle()` |
| Global graph draws the body links = `okf graph`'s edges; arrows at the target | spec A3; Viewer README "Global graph" |
| Facets type, trust, freshness, tags; AND between, OR within; hide or dim; pan, zoom, drag, Fit; drawer with Open page | CHANGELOG P3; docs page "The global graph" |
| `graph.html#<id>` selects; selection written by `replaceState`, no history entry; concept pages link with their id | Viewer README; `okf-graph.js` (`replaceState`); spec control 13, A17 |
| Above 1,500 visible nodes (concepts and absent targets): list instead of drawing | `okf-sim.js` (`NODE_LIMIT = 1500`); Viewer README |
| Splitter: drag (mouse, touch, pen) or keys, 16 / 64 px, Enter resets; 240–720 px, middle column ≥ 360; `localStorage`; graph refits unless the view was moved | CHANGELOG "Resizable side panel"; spec r14 (L7); `okf-resize.js` |
| Follows the system theme; the moon button overrides and is remembered where storage allows | spec §4.7, A7; docs page "Theme and small screens" |
| Graph: one tab stop, Page Up/Down index order, Home/End, arrows to nearest neighbour in direction, Space selects, Enter opens, `aria-current` | Viewer README; spec §8; `okf-graph.js` (roving `tabindex`) |
| Text equivalents: Neighbourhood List, graph List (same facets) and drawer | spec §6, A30; docs page "Keyboard and assistive technology" |
| Palette is a modal dialog with focus kept and returned; enlarged dialog: Tab wraps, page inert, no scroll, Esc/Close/backdrop return focus | spec §8; docs page |
| Contrast targets 4.5:1 text, 3:1 shapes, both themes, measured by the recette | spec §6, §11.0, §12.8 |
| Reduced motion: layout runs, only start and end drawn | `okf-graph.js` (comment above `calm`); spec r12 |
| IME and screen-reader manual checks not done, tracked in #177; Safari best effort | spec §7, A14, A21 |
| `OKF4net.Viewer` references only `OKF4net`; vendored marked v15.0.12 (MIT) and Inter, Inter Tight, Space Mono (SIL OFL 1.1); licences in every site and beside the binary; `okf` never references them | `CLAUDE.md`; Viewer README "Licensing"; CHANGELOG [0.7.0] Changed |
| `okf-index.js` sets `window.OKF_INDEX`; a script because `fetch` of local JSON is blocked under `file://` | spec §3 |
| Classic scripts, not ES modules (blocked under `file://`); one global each, inert if a dependency is missing | spec §4.1, §12.6 |
| Every bundle string through one quoting function escaping `<`, `>`, `&`, U+2028, U+2029; no object keyed by id (`__proto__`, `constructor`, `toString`) | `src/OKF4net.Viewer/IndexScript.cs` (class remarks); `HtmlSafeJson.cs`; spec §3.4 |
| OKF4net's own bundle: 795 concepts, 912 edges, `okf-index.js` 407,325 bytes (during development) | spec §3.6 (measurement of 2026-10-07) |
| `okf-sim.js`: no DOM, clock, `Math.random`; only `+ - * / % >>>`, `Math.sqrt`, exact `floor/ceil/trunc/abs/min/max/imul`; fixed accumulation order; work counted not timed; suspendable mid-iteration | `okf-sim.js` header comment; spec §4.2; `CLAUDE.md` |
| Initial layout: grid in index order + LCG jitter seeded from node and edge counts (the code shown) | `okf-sim.js` `nextState`, `initialLayout` (lines 76–100) |
| Label boxes estimated, never measured, so the layout is the same in every browser | spec r11 |
| Same positions bit for bit in Chromium, Firefox, WebKit on acme_retail, harness fixture, OKF4net (806 nodes), synthetic 1,414 | spec r12 ("positions identiques au bit près") |
| DOM sanitizer: element and attribute allowlists, `on*` dropped, URL schemes, opaque tags, unwrap; the whole defence | `viewer.js`; `tools/viewer-security-check/README.md` "Why this exists"; `CLAUDE.md` |
| Renderer hooks removed: stopped nothing more, emptied a `<details>` block, could not see the unescaped `alt` of `Renderer.image()` | `tools/viewer-security-check/README.md` "Why this exists" |
| Sanitizer keeps `class` only on `<code>` | `viewer.js` `ALLOWED_ATTRS` (`CODE: { class: 1 }`) |
| Chrome selectors anchored to chrome containers; chrome lookups start from a fixed id or an anchored selector (`document.getElementById`, an anchored `document.querySelector`) and class queries stay inside those roots; the fragment-to-heading lookup (`okf-toc.js` `targetOf`) calls `document.getElementById` on fragment-derived ids, then checks the result is inside `#okf-body`; three-layer guard | spec §12.6 "Ancrage, propriété de sécurité"; `run.js` comment above `CHROME_PROPS` ("Three layers, each with its own reach and none a proof") |
| jsdom harness: real marked + scripts on a site rendered from a hostile fixture; injected scheduler; CI job `viewer sanitizer (JS)`; 453 cases | `tools/viewer-security-check/README.md`; `.github/workflows/ci.yml`; `npm test` output on `90adf19` (the v0.7.0 tag): "453 passed, 0 failed" |
| Mutants: splitter cases killed 50, enlarged-dialog cases 30 | spec r14 ("`cases/p4.js` (27 cas, 50 mutants tués)") and r15 ("`cases/p2-modal.js` (20 cas, 30 mutants tués)") |
| Recette: Chrome, Edge, Firefox, WebKit from `file://`; fonts, contrast, widths, no outside request; Playwright never a dependency | `tools/viewer-security-check/README.md` "Recette"; spec §12.8; `recette/lib.js` |
| AOT smoke in CI on Linux, macOS, Windows; runs the native binary; executes its `okf-index.js` | `.github/workflows/ci.yml` job `aot-publish` (`check-index.js`) |
| Cap crowded: labels far from their node, edges converging | spec r15 ("au plafond, quelques libellés déplacés loin de leur nœud et le faisceau d'arêtes vers le centre") |
| Palette scans every concept on each keystroke | `okf-site.js` `rank` (one loop over `index.concepts`) |
| Spec in 15 revisions, slices P1, P1.1, P2, P3, then r14 (splitter) and r15 (dialog); each with plan, cases, recette | spec header and §9 |
| Real-browser catches: overlapping labels (→ boxes, r11), Firefox `content-visibility` pushing the list entry below the fold, long tasks near the limit (r12) | spec r11, r12; `tools/viewer-security-check/ACCEPTANCE.md` P3-css-1 |
| `okf` is also the `OKF4net.Cli` dotnet tool: tool manifest, `dotnet tool restore`, `dotnet okf validate`, `dotnet tool install -g`, `dnx OKF4net.Cli --yes`; portable IL, .NET 10 runtime or later | `src/OKF4net.Cli/README.md`; CHANGELOG [0.7.0]; `.github/workflows/ci.yml` job `dotnet tool smoke test`; `dnx` and the manifest flow run locally on 2026-10-14 (`dnx` fails non-interactively without `--yes`) |
| `okf validate` exits 1 on a non-conformant bundle; warnings never fail it | `src/OKF4net.Cli/OkfCli.cs`; the `dotnet tool smoke test` job asserts exit 0 on `tests/fixtures/appendix_a` and exit 1 on `okf_v02_reserved` |
| `okf verify` printed a forged `recorded …` line from a stored timestamp, now line-safe in the renderer | CHANGELOG [0.7.0] (#123); `tests/OKF4net.Tests/AuditTextTests.cs`, `CliTests.cs` (reproduced with the real CLI before the fix) |
| A self-holding mapping, or a 60-level mapping sharing a sub-mapping, overflowed the stack, ran out of memory or never finished when hashed, compared or printed; now answers or refuses; parser output unaffected | CHANGELOG [0.7.0] (#158, #189); `tests/YamlHostileProbe` cases `value-*` and `emit-*`, run by `YamlHostileInputTests` in a child process |
| A concept cited by 100,000 others: ~170 s to build backlinks, under 1 s now, same order | CHANGELOG [0.7.0] (#148); PR 192 measured 169,550 ms before and 701 ms after for 100,000 citers |
| A junction or symlink to a bundle shares its write lock; in-process only | CHANGELOG [0.7.0] (#86, "Still in-process only") |
| The four upgrade changes: `RequiredKeys` / `RecommendedFields`, one YAML nesting rule, bare `okf` exits 0, emitter refusals | CHANGELOG [0.7.0] "Breaking changes at a glance" |

Removed while checking (they were in the first draft and the code says
otherwise or nothing): "the index is serialized by `System.Text.Json` with a
source-generated context" (it is hand-built through `HtmlSafeJson.Quote`;
`IndexScript.cs` says so), "every page has the explorer" (the graph page has
facets instead), "*Filters* is in every header" (the graph page has *Reading
view*), and the exact text of the `<details>` example (paraphrased instead).

## Social snippets

Plain text, no hashtag lists. Each is at most 280 characters with the link
counted as 23 characters, as X and Mastodon count any link; with a full
dev.to URL pasted they run to about 300, so post them on X and Mastodon with
the link as is (both shorten it).

**X**

> okf-render, the site generator of OKF4net, now turns an Open Knowledge Format bundle (markdown + YAML frontmatter) into an interactive viewer: explorer, Ctrl+K palette, local and global link graphs. Static files opened from disk, no server. <URL>

**LinkedIn**

> A folder of markdown is easy for agents and diffs, hard for people to browse. The new okf-render writes a static site with an explorer, trust and staleness marks, and link graphs with a keyboard path and a text equivalent. Write-up: <URL>

**Mastodon / Bluesky**

> How do you get a force-directed graph to give the same layout in Chromium, Firefox and WebKit? No Math.random, no clock, only exactly-rounded Math functions, and work counted instead of timed. Notes from building the okf-render viewer: <URL>

**Release post (X / Mastodon)**

> OKF4net 0.7.0: okf-render becomes an interactive viewer (explorer, Ctrl+K palette, local and global link graphs; static files, no server) and okf is now a dotnet tool you can pin in CI. Notes and the upgrade checklist: <URL>

**Hacker News** (link to the repository, not to the article, for a Show HN):

> Show HN: okf-render – browse a folder of markdown knowledge as an interactive static site

**Reddit** (r/dotnet; adapt for r/csharp, and say in the first comment that you are the author):

> okf-render: a Native AOT tool that turns a markdown knowledge bundle into an interactive static site (no server, no npm)

## A French version

Not written. The plan is English first; if you want a French version for the
personal site or LinkedIn, ask for a translation of `article.md` once the
English text is final, so the two do not drift. It would keep the same images
(their text is English UI) and the same claims table.
