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
keeping their text) asserts the sanitizer isn't so aggressive it breaks
normal rendering.

## Why this exists

`viewer.js` defends against XSS in untrusted bundle content by sanitizing the
*parsed DOM* (see the comments at the top of `viewer.js` itself): an element
allowlist (a handful of tags gated further by an attribute-value constraint,
e.g. `<input>` survives only as `type="checkbox"`, forced `disabled`), a
per-tag attribute allowlist that drops every `on*` handler, URL-scheme
validation on `href`/`src`, and an opaque-tags table (`<script>`/`<style>`)
dropped with no text kept, since their content is source, not prose. That
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
npm test          # or: node run.js
```

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
