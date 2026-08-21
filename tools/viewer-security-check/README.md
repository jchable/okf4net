# viewer-security-check

A small Node/jsdom harness that loads the **real** vendored files the `okf
render` command ships --
[`src/OKF4net.Viewer/Assets/marked.min.js`](../../src/OKF4net.Viewer/Assets/marked.min.js)
and
[`src/OKF4net.Viewer/Assets/viewer.js`](../../src/OKF4net.Viewer/Assets/viewer.js)
-- into a page and runs a battery of hostile markdown payloads through them,
asserting the resulting DOM is inert (no live event handlers, no dangerous
URL schemes, no raw `<script>`/`<svg onload>` survives). A second battery of
ordinary markdown (heading, list, bold, fenced code, relative link, plain
image, GFM task-list checked/unchecked markers) asserts the sanitizer isn't
so aggressive it breaks normal rendering.

## Why this exists

`viewer.js` defends against XSS in untrusted bundle content with two layers
(see the comments at the top of `viewer.js` itself):

1. **Layer 1** (defense in depth, *not* the control that holds): overrides
   marked's `renderer.html` hooks (and `TextRenderer.html`) to suppress raw
   HTML tokens, since modern marked has no `sanitize` option any more.
2. **Layer 2** (the control that actually holds): sanitizes the *parsed DOM*
   -- an element allowlist, a per-tag attribute allowlist that drops every
   `on*` handler, and URL-scheme validation on `href`/`src` -- before the
   result ever touches the live page.

Layer 2 exists because layer 1 alone is not enough: marked's
`Renderer.image()` builds the `alt` attribute with **no escaping call at
all**, so a plain markdown image with no raw-HTML token in it --
`![foo" onerror="alert(1)](x.png)` -- breaks out of the attribute and adds a
live `onerror` handler, a class of bug no renderer-hook override can see.

`tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs` can only smoke-check for
source-text markers (xunit runs on .NET and cannot execute JavaScript), so it
cannot prove any of this actually holds. This harness is the thing that can:
it runs the genuine marked + viewer.js pairing exactly as a generated page
does, in a real DOM (via jsdom), against the payloads that motivated layer 2
in the first place.

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
marked's HTML generation is exactly what layer 2 defends against, and a new
marked version could change escaping behavior in ways layer 1 was never going
to catch regardless.

When you discover a new payload class, add a case here — this file is where
that knowledge has to live to survive.
