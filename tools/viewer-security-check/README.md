# viewer-security-check

A small Node/jsdom harness that loads the **real** vendored files the `okf
render` command ships --
[`src/OKF4net.Viewer/Assets/marked.min.js`](../../src/OKF4net.Viewer/Assets/marked.min.js)
and
[`src/OKF4net.Viewer/Assets/viewer.js`](../../src/OKF4net.Viewer/Assets/viewer.js)
-- into a page and runs a battery of hostile markdown and raw-HTML payloads
through them, asserting the resulting DOM is inert (no live event handlers,
no dangerous URL schemes, no raw `<script>`/`<svg onload>`/`<iframe>`/
`<object>` survives, no non-checkbox `<input>` survives as an element). A
second battery of ordinary markdown (heading, list, bold, fenced code,
relative link, plain image, GFM task-list checkboxes rendering with correct
checked/unchecked state, disallowed wrapper tags like `<details>`/`<div>`
being unwrapped -- dropped themselves, but with their already-sanitized
children, links and tables included, kept in place) asserts the sanitizer
isn't so aggressive it breaks normal rendering.

## Why this exists

`viewer.js` defends against XSS in untrusted bundle content by sanitizing the
*parsed DOM* (see the comments at the top of `viewer.js` itself): an element
allowlist (a handful of tags gated further by an attribute-value constraint,
e.g. `<input>` survives only as `type="checkbox"`, forced `disabled`), a
per-tag attribute allowlist that drops every `on*` handler, URL-scheme
validation on `href`/`src`, and an opaque-tags table (`<script>`, `<style>`,
`<iframe>`, `<noembed>`, `<noframes>`, `<xmp>`, `<plaintext>`, `<template>`,
`<base>`) dropped with no content kept at all, since it is source, not prose.
A disallowed tag that is *not* on that opaque table (e.g. `<div>`,
`<details>`) is unwrapped instead: the element itself is dropped, but its
already-sanitized children move up in its place. The unwrap detaches every
node bottom-up and re-appends each kept node top-down, so every unwrap
mutation moves one childless node (removing an opaque element still drags its
subtree); the "Unwrap cost" cases in `run.js` count the nodes every mutation
drags while sanitizing and assert that stays within 4 × N (N = nodes in the
parsed body) on four shapes, one of them nested opaque elements -- a
deterministic count, not a timing, and no case in the harness asserts a
wall-clock bound. That
sanitizer is the whole defense, not one layer of it.

An earlier version of this file also patched marked's `renderer.html` hooks
(the main `Renderer` and its separate `TextRenderer`) to suppress raw-HTML
tokens before they ever reached the DOM sanitizer, since modern marked has no
`sanitize` option any more. It was removed: measured with this exact harness
against the vendored build (marked v15.0.12), the renderer-hook override
stopped nothing the DOM sanitizer alone didn't already stop, while it
silently deleted benign content the sanitizer preserves --
`<details><summary>Resume</summary>corps important</details>` rendered as
`""` (everything gone) with the renderer-hook override in place, versus
`"Resumecorps important"` without it. Renderer-hook patching also could never
have closed the gap the DOM sanitizer exists for in the first place: marked's
`Renderer.image()` builds the `alt` attribute with **no escaping call at
all**, so a plain markdown image with no raw-HTML token in it --
`![foo" onerror="alert(1)](x.png)` -- breaks out of the attribute and adds a
live `onerror` handler, a class of bug no renderer-hook override can see.

`tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs` can only smoke-check for
source-text markers (xunit runs on .NET and cannot execute JavaScript), so it
cannot prove any of this actually holds. This harness is the thing that can:
it runs the genuine marked + viewer.js pairing exactly as a generated page
does, in a real DOM (via jsdom), against the payloads that motivated the
sanitizer in the first place.

## Running it

```sh
cd tools/viewer-security-check
npm install
npm test          # regenerates the hostile site (pretest), then runs run.js
```

`npm test` is the command to use: its `pretest` step regenerates the site the
page cases load. `node run.js` on its own skips that step and reuses whatever
`.generated/` already holds, which may be stale.

Exits non-zero (and prints which check failed) if anything regresses.

## Run in CI

`ci.yml` runs this as the **`viewer sanitizer (JS)`** job (`npm ci && npm
test` on Node 22). It is a Node project sitting outside `OKF4net.sln` — it is
not published, and `OKF4net.sln` neither builds nor tests it — but unlike
`producers/`, it is *not* left out of CI: it is the only automated guard on a
security control, and the xunit tests beside it cannot execute JavaScript, so
they stay green even when the sanitizer is gutted.

## When to run this locally

Whoever bumps `src/OKF4net.Viewer/Assets/marked.min.js` to a newer marked
release should run this harness before pushing, rather than waiting on CI.
marked's HTML generation is exactly what the sanitizer defends against, and a
new marked version could change escaping behavior in ways this harness is the
only thing positioned to catch.

When you discover a new payload class, add a case here — this file is where
that knowledge has to live to survive.

## Interactive viewer cases

The second half of `run.js` covers the interactive viewer scripts
(`okf-site.js`, `okf-theme.js`, `okf-shapes.js`, `okf-explorer.js`,
`okf-palette.js`, `okf-toc.js`, `okf-page.js`); `OkfShapes` cases run in a
bare window (`okfShapes()`) against the attribute table of spec §12.2,
written out by hand. Helper cases load `okf-site.js` into a bare window. Page cases
load pages of a site generated by the real `okf-render` from
`fixtures/hostile-bundle/` — hostile titles, ids named `__proto__`,
`constructor` and `toString`, a concept that is also a folder, a broken link,
repeated headings, a clobbering attempt, body `<code>` wearing the chrome's
class names (checked through jsdom's computed styles: it cascades
`viewer.css` but does no layout) — so they execute the real `okf-index.js`
the generator writes. `npm test` runs `pretest` first, which deletes
`.generated/hostile-site/` and regenerates it with `dotnet run` (the
generator never deletes a file it no longer writes, so a removed asset would
otherwise survive), so the harness now needs the .NET SDK. (`node run.js`
alone skips `pretest` and reuses whatever `.generated/` holds.) Page cases are
async and are awaited before the summary.

What the harness cannot check (layout, browser shortcuts, focus rings,
contrast, absence of a theme flash) is in `ACCEPTANCE.md`.

`check-index.js` executes one `okf-index.js` and checks that it is a
well-formed index (version 2, the types table with its counts and slots, and
every concept — stale fields, `typeIndex` and the description bound included —
every edge and every tree node in the shape `IndexScript.cs` writes; a smoke
check on a non-empty bundle, not a validator for an empty one). CI's
`aot-publish` job runs it on the file the **native** `okf-render` writes,
which the harness above never sees.

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
pages by depth, `pageUrl` to open a page whose name holds `#`, `?` or `%`). `node tools/viewer-security-check/recette/lib.js --selftest`
(also `npm run recette:selftest`) checks the colour and url helpers against known
answers: a wrong contrast helper gives every later check a confident wrong
number. Results and captures (1 440 × 900,
named by id) go to `--out`, by default a folder of the system's temporary
directory; a slice's report goes into the pull request, never into the
repository.
