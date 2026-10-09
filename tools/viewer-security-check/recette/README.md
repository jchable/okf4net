# recette/

Manual browser checks of the interactive viewer, run by hand, never by `npm
test` or CI. Playwright is **not** a dependency of this repository: every
script here resolves it at run time through `lib.js` (`OKF_PLAYWRIGHT`, the
path of a `playwright-core` module, else `require("playwright-core")`). The
tooled recette itself (`recette.js`, slices `p1`…`p3`) is described in the
[Recette section of the parent README](../README.md#recette-manual-outside-ci).

## `docs-shots.js` — the screenshots of the online docs

Regenerates the PNGs that `web/src/pages/docs/Viewer.tsx` shows (the page
`/docs/viewer/` of the public website), in one command:

    OKF_PLAYWRIGHT=<path to a playwright-core module> \
      node tools/viewer-security-check/recette/docs-shots.js

It renders `bundles/acme_retail` with the `okf-render` built from this tree
(`dotnet run -c Release --project src/OKF4net.Render`, so the .NET SDK must be
installed), opens the pages from `file://` in Chrome at a fixed 1 440 × 900
viewport (390 × 844 for the narrow one) with the clock frozen at 2026-10-09 so
the staleness marks do not depend on the day it runs, draws the numbered
callouts that the docs page lists, and writes one PNG per shot into
`web/public/viewer/`. Two runs give byte-identical files.

| Option | Default | Meaning |
|---|---|---|
| `--out <dir>` | `web/public/viewer` | where the PNGs go |
| `--site <dir>` | (rendered into a temporary folder) | reuse an already rendered site |
| `--browser <name>` | `chrome` | `chrome`, `edge`, `firefox` or `webkit` (names of `lib.js`) |
| `--only a,b` | all | only these shots, names without `.png` |

After a re-capture, the sizes in `web/src/content/viewerShots.ts` must match
the files (`npm test` in `web/` checks it: `viewerShots.test.tsx` reads each
PNG header), and the numbered lists next to `viewer-concept.png` and
`viewer-graph.png` in `Viewer.tsx` must still match the callouts. Look at every
PNG before committing it; the whole set stays under 1.5 MB.

## `article-shots.js` — the screenshots of the viewer launch article

Regenerates the PNGs of `docs/outreach/articles/okf-viewer/article.md` into
`docs/outreach/articles/okf-viewer/img/`, in one command:

    OKF_PLAYWRIGHT=<path to a playwright-core module> \
      node tools/viewer-security-check/recette/article-shots.js

Same frozen clock (2026-10-09), browser and viewports as `docs-shots.js`, but
clean captures (no numbered callouts), from three renders made into a
temporary folder that is deleted at the end:

- `bundles/acme_retail`, with the `okf-render` built from this tree;
- a **synthetic bundle** the script writes itself from a fixed seed
  (`synthBundle()`): 112 concepts of seven types, fictional names only, a hub
  concept (`glossary/customer`) with 61 neighbours so the Neighbourhood
  reaches its 40-node cap, and two absent link targets drawn as ghosts. It is
  never written into the repository;
- `bundles/acme_retail` with the `okf-render` of the **`v0.6.0` tag**, for the
  one "before" capture: the tag is extracted with `git archive` (no worktree,
  no checkout) and run with `dotnet run`, which adds a build of that tree.

Two captures are **still sequences** composed in the browser (frames laid out
in a blank page, then captured): no image library is needed. The layout
sequence stays reproducible because the script installs
`window.OKF_SCHEDULER` (the hook `okf-graph.js` reads instead of
`requestAnimationFrame`, spec §7) and runs a counted number of layout slices
between frames. Two runs give byte-identical files.

| Option | Default | Meaning |
|---|---|---|
| `--out <dir>` | `docs/outreach/articles/okf-viewer/img` | where the PNGs go |
| `--browser <name>` | `chrome` | `chrome`, `edge`, `firefox` or `webkit` (names of `lib.js`) |
| `--only a,b` | all | only these shots, names without `.png` |
| `--before-site <dir>` | (rendered from `v0.6.0`) | reuse a site already rendered by `okf-render` 0.6.0 |
| `--no-before` | | skip the "before" capture and the `v0.6.0` build |

Look at every PNG before committing it: each stays under about 250 KB and the
whole set under 2.5 MB (the script prints the sizes).
