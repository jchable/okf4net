// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Loads the REAL vendored src/OKF4net.Viewer/Assets/marked.min.js and
// src/OKF4net.Viewer/Assets/viewer.js into a jsdom page -- the same two
// scripts a generated OKF site ships -- and runs a battery of hostile and
// legitimate markdown payloads through them. See README.md in this
// directory for what this is and why it exists.
"use strict";

const fs = require("fs");
const path = require("path");
const { JSDOM } = require("jsdom");

const ASSETS = path.join(__dirname, "..", "..", "src", "OKF4net.Viewer", "Assets");
const markedSource = fs.readFileSync(path.join(ASSETS, "marked.min.js"), "utf8");
const viewerSource = fs.readFileSync(path.join(ASSETS, "viewer.js"), "utf8");

/**
 * Renders `markdown` through the real marked.min.js + viewer.js and returns
 * the resulting "#okf-body" element.
 *
 * The payload is attached via `textContent` (a DOM API call), not by
 * interpolating it into an HTML string that gets re-parsed -- exactly like
 * a real browser page, where the payload has already survived HTML parsing
 * once by the time the browser exposes `<script>.textContent`. Encoding the
 * payload so it survives that first HTML parse is `HtmlSafeJson`'s job and
 * is covered separately by `HtmlSafeJsonTests.cs`; this harness starts one
 * step downstream of that, exactly where marked.parse() and viewer.js's
 * sanitizer take over.
 *
 * @param {string} markdown
 * @param {object} [links] The generation-time link-rewiring table
 *   (`payload.links`), keyed exactly as `SiteModel`/`HtmlWriter` would emit
 *   it. Defaults to `{}` for tests that don't exercise rewiring.
 */
function renderBody(markdown, links) {
  const dom = new JSDOM(
    `<!doctype html><html><body>
      <div id="okf-body"></div>
      <script type="application/json" id="okf-payload"></script>
    </body></html>`,
    { runScripts: "outside-only" }
  );

  const { window } = dom;
  window.document.getElementById("okf-payload").textContent = JSON.stringify({
    body: markdown,
    links: links || {},
  });

  // Evaluated in write order, exactly as the generated page's
  // <script src="assets/marked.min.js"> then <script src="assets/viewer.js">
  // tags do.
  window.eval(markedSource);
  window.eval(viewerSource);

  return window.document.getElementById("okf-body");
}

let failures = 0;
let passed = 0;

/** @param {string} name @param {() => void} fn */
function check(name, fn) {
  try {
    fn();
    passed++;
    console.log(`  ok  - ${name}`);
  } catch (err) {
    failures++;
    console.log(`FAIL  - ${name}`);
    console.log(`        ${err.message}`);
  }
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

// --- hostile payloads: none of these may produce executable markup ---------

console.log("Hostile payloads (must render inert):");

check("alt-attribute breakout does not add a live onerror handler", () => {
  const body = renderBody('![foo" onerror="alert(1)](x.png)');
  const img = body.querySelector("img");
  assert(img, "expected an <img> to survive sanitization");
  assert(!img.hasAttribute("onerror"), "onerror attribute survived sanitization");
  assert(!body.innerHTML.toLowerCase().includes("onerror"), "onerror text leaked into output");
});

check("a javascript: href is stripped", () => {
  const body = renderBody("[click](javascript:alert(1))");
  const a = body.querySelector("a");
  assert(a, "expected an <a> to survive sanitization");
  assert(!a.hasAttribute("href"), "javascript: href survived sanitization");
});

check("a mixed-case JavaScript: href is stripped", () => {
  const body = renderBody("[click](JavaScript:alert(1))");
  const a = body.querySelector("a");
  assert(a, "expected an <a> to survive sanitization");
  assert(!a.hasAttribute("href"), "JavaScript: href survived sanitization (case-insensitivity gap)");
});

check("a data: href is stripped", () => {
  const body = renderBody("[click](data:text/html,alert(1))");
  const a = body.querySelector("a");
  assert(a, "expected an <a> to survive sanitization");
  assert(!a.hasAttribute("href"), "data: href survived sanitization");
});

check("a raw <script> block is not rendered", () => {
  const body = renderBody("Before\n\n<script>alert(1)</script>\n\nAfter");
  assert(body.querySelectorAll("script").length === 0, "a <script> element reached the page");
  assert(!body.innerHTML.includes("alert(1)"), "script payload text leaked into output");
});

check("a raw inline <img onerror=...> is not rendered live", () => {
  const body = renderBody("Look <img src=x onerror=alert(1)> here.");
  assert(!body.innerHTML.toLowerCase().includes("onerror"), "onerror text leaked into output");
});

check("nested HTML in alt text is not reparsed as markup", () => {
  // marked places this alt text into the `alt` attribute unescaped, but the
  // literal string has no `"` in it, so HTML attribute-value parsing never
  // terminates early: the whole `<img src=x onerror=alert(1)>` string stays
  // inside the outer <img>'s alt *attribute value*, never becoming a second,
  // live element with a real onerror attribute. Assert on that structurally
  // (one <img>, no element anywhere carries a live onerror attribute) rather
  // than a naive substring search over serialized innerHTML, which would
  // also flag this harmless case: the word "onerror" legitimately appears
  // as inert alt text, not as a live handler, and serialization is not
  // required to escape `<`/`>` inside an attribute value.
  const body = renderBody("![<img src=x onerror=alert(1)>](y.png)");
  const imgs = body.querySelectorAll("img");
  assert(imgs.length === 1, `expected exactly one <img>, found ${imgs.length}`);
  assert(!imgs[0].hasAttribute("onerror"), "onerror attribute survived sanitization");
  assert(body.querySelectorAll("[onerror]").length === 0, "some element carries a live onerror attribute");
});

check("a raw <svg onload=...> is not rendered", () => {
  const body = renderBody('<svg onload="alert(1)"></svg>');
  assert(body.querySelectorAll("svg").length === 0, "an <svg> element reached the page");
  assert(!body.innerHTML.toLowerCase().includes("onload"), "onload text leaked into output");
});

check("a raw <iframe> is not rendered", () => {
  const body = renderBody('<iframe src="javascript:alert(1)"></iframe>');
  assert(body.querySelectorAll("iframe").length === 0, "an <iframe> element reached the page");
});

check("a raw <form>/formaction is not rendered", () => {
  const body = renderBody('<form action="x"><button formaction="javascript:alert(1)">go</button></form>');
  assert(body.querySelectorAll("form").length === 0, "a <form> element reached the page");
  assert(!body.innerHTML.toLowerCase().includes("formaction"), "formaction text leaked into output");
});

check("a raw <object data=javascript:...> is not rendered", () => {
  const body = renderBody('<object data="javascript:alert(1)"></object>');
  assert(body.querySelectorAll("object").length === 0, "an <object> element reached the page");
});

check("a raw <math><mtext><script> is not rendered", () => {
  const body = renderBody("<math><mtext><script>alert(1)</script></mtext></math>");
  assert(body.querySelectorAll("script").length === 0, "a <script> element reached the page (via <math><mtext>)");
  assert(!body.innerHTML.includes("alert(1)"), "script payload text leaked into output");
});

check("a raw <div style=background:url(javascript:...)> is not rendered live", () => {
  const body = renderBody('<div style="background:url(javascript:alert(1))">hi</div>');
  assert(!body.innerHTML.toLowerCase().includes("style="), "style attribute survived sanitization");
  assert(body.textContent.includes("hi"), "wrapper text was lost even though the element was dropped");
});

check("a raw <a xlink:href=javascript:...> is not rendered live", () => {
  const body = renderBody('<a xlink:href="javascript:alert(1)">click</a>');
  const a = body.querySelector("a");
  assert(a, "expected an <a> to survive sanitization");
  assert(!a.hasAttribute("xlink:href"), "xlink:href attribute survived sanitization");
  assert(!body.innerHTML.toLowerCase().includes("javascript:"), "javascript: scheme text leaked into output");
});

check("a raw <img srcdoc=...> does not carry the srcdoc attribute", () => {
  const body = renderBody('<img src="x.png" srcdoc="<script>alert(1)</script>">');
  const img = body.querySelector("img");
  assert(img, "expected an <img> to survive sanitization");
  assert(!img.hasAttribute("srcdoc"), "srcdoc attribute survived sanitization");
});

check("a raw <input type=checkbox onfocus=... autofocus> survives only as an inert disabled checkbox", () => {
  const body = renderBody('<input type="checkbox" onfocus="alert(1)" autofocus>');
  const input = body.querySelector("input");
  assert(input, "expected the checkbox to survive as a real <input> element");
  assert(input.getAttribute("type") === "checkbox", "type attribute was altered");
  assert(input.hasAttribute("disabled"), "surviving checkbox was not forced disabled");
  assert(!input.hasAttribute("onfocus"), "onfocus attribute survived sanitization");
  assert(!input.hasAttribute("autofocus"), "autofocus attribute survived sanitization");
});

check("a raw <input type=text> does not survive as an element", () => {
  const body = renderBody('<input type="text" value="hi">');
  assert(body.querySelectorAll("input").length === 0, "a non-checkbox <input> survived sanitization");
});

check("a raw <input type=image src=...> does not survive as an element", () => {
  const body = renderBody('<input type="image" src="javascript:alert(1)">');
  assert(body.querySelectorAll("input").length === 0, "an <input type=image> survived sanitization");
});

check("a raw <input type=submit formaction=...> does not survive as an element", () => {
  const body = renderBody('<input type="submit" formaction="javascript:alert(1)">');
  assert(body.querySelectorAll("input").length === 0, "an <input type=submit> survived sanitization");
  assert(!body.innerHTML.toLowerCase().includes("formaction"), "formaction text leaked into output");
});

// --- behaviour change from removing the renderer-hook layer ---------------

console.log("\nWrapper text preserved (renderer-hook layer removed):");

check("a <details>/<summary> wrapper's text is preserved, not deleted", () => {
  const body = renderBody("<details><summary>Resume</summary>corps important</details>");
  const text = body.textContent.replace(/\s+/g, "").trim();
  assert(text === "Resumecorpsimportant", `expected wrapper text preserved, got: ${JSON.stringify(body.textContent)}`);
});

check("a nested <div><span><b> wrapper's text is preserved, not deleted", () => {
  const body = renderBody("<div><span>texte <b>gras</b></span></div>");
  const text = body.textContent.replace(/\s+/g, " ").trim();
  assert(text === "texte gras", `expected wrapper text preserved, got: ${JSON.stringify(body.textContent)}`);
});

// --- legitimate payloads: sanitization must not be over-aggressive --------

console.log("\nLegitimate payloads (must still render):");

check("a heading renders", () => {
  const body = renderBody("# Title");
  const h1 = body.querySelector("h1");
  assert(h1 && h1.textContent === "Title", "heading did not render as expected");
});

check("a list renders", () => {
  const body = renderBody("- one\n- two");
  const items = body.querySelectorAll("li");
  assert(items.length === 2, `expected 2 <li>, found ${items.length}`);
  assert(items[0].textContent === "one" && items[1].textContent === "two", "list items did not render as expected");
});

check("bold text renders", () => {
  const body = renderBody("**bold**");
  const strong = body.querySelector("strong");
  assert(strong && strong.textContent === "bold", "bold did not render as expected");
});

check("fenced code containing < and & renders as inert text", () => {
  const body = renderBody("```\n<div>&stuff</div>\n```");
  const code = body.querySelector("pre code");
  assert(code, "expected a <pre><code> block to survive");
  assert(code.textContent.trim() === "<div>&stuff</div>", `code text was mangled: ${JSON.stringify(code.textContent)}`);
  assert(code.querySelectorAll("div").length === 0, "code block content was parsed as live markup");
});

check("a relative link is left intact", () => {
  const body = renderBody("[term](../glossary/term.md)");
  const a = body.querySelector("a");
  assert(a && a.getAttribute("href") === "../glossary/term.md", "relative link href was altered or stripped");
});

check("a link with a fragment is rewired to the target page keeping the fragment (I1)", () => {
  const body = renderBody("[the usage section](a/b.md#usage)", {
    "a/b.md#usage": { href: "a/b.html#usage", exists: true },
  });
  const a = body.querySelector("a");
  assert(a, "expected an <a> to survive sanitization");
  assert(
    a.getAttribute("href") === "a/b.html#usage",
    `expected the fragment to survive rewiring, got: ${a.getAttribute("href")}`
  );
});

check("a broken link with a fragment is flagged broken and not given a live href (I1)", () => {
  const body = renderBody("[gone](a/missing.md#usage)", {
    "a/missing.md#usage": { href: "a/missing.html#usage", exists: false },
  });
  const a = body.querySelector("a");
  assert(a, "expected an <a> to survive sanitization");
  assert(!a.hasAttribute("href"), "broken link retained an href");
  assert(a.classList.contains("broken"), "broken link missing the 'broken' class");
});

check("a link whose fragment has non-ASCII chars is still rewired via a decodeURI fallback (I2)", () => {
  // marked's cleanUrl() step runs encodeURI() on every link destination, so
  // the DOM ends up with href="a/b.md#caf%C3%A9" even though the
  // generation-time table is keyed by the raw, un-encoded string. A direct
  // lookup on the DOM href therefore misses; viewer.js must retry with
  // decodeURI(href) before giving up, or this link silently dies (and the
  // C# side would have already counted it live and emitted a backlink).
  const body = renderBody("[x](a/b.md#café)", {
    "a/b.md#café": { href: "a/b.html#café", exists: true },
  });
  const a = body.querySelector("a");
  assert(a, "expected an <a> to survive sanitization");
  assert(
    a.getAttribute("href") === "a/b.html#café",
    `expected the encoded fragment to be rewired via the decodeURI fallback, got: ${a.getAttribute("href")}`
  );
});

check("a plain image with alt text renders", () => {
  const body = renderBody("![A nice diagram](diagram.png)");
  const img = body.querySelector("img");
  assert(img, "expected an <img> to survive");
  assert(img.getAttribute("src") === "diagram.png", "image src was altered or stripped");
  assert(img.getAttribute("alt") === "A nice diagram", "image alt text was altered or stripped");
});

check("an unchecked GFM task-list item renders a real disabled checkbox, unchecked", () => {
  const body = renderBody("- [ ] todo");
  const li = body.querySelector("li");
  assert(li, "expected a <li> to survive");
  const input = li.querySelector("input");
  assert(input, "expected a real <input> checkbox to survive sanitization");
  assert(input.getAttribute("type") === "checkbox", "surviving input was not type=checkbox");
  assert(input.hasAttribute("disabled"), "surviving checkbox was not disabled");
  assert(!input.hasAttribute("checked"), "unchecked item was rendered as checked");
});

check("a checked GFM task-list item renders a real disabled checkbox, checked", () => {
  const body = renderBody("- [x] done");
  const li = body.querySelector("li");
  assert(li, "expected a <li> to survive");
  const input = li.querySelector("input");
  assert(input, "expected a real <input> checkbox to survive sanitization");
  assert(input.getAttribute("type") === "checkbox", "surviving input was not type=checkbox");
  assert(input.hasAttribute("disabled"), "surviving checkbox was not disabled");
  assert(input.hasAttribute("checked"), "checked item was not rendered as checked");
});

check("a mixed task list keeps checked and unchecked items distinct via checkbox state", () => {
  const body = renderBody("- [ ] a faire\n- [x] fait\n");
  const items = body.querySelectorAll("li");
  assert(items.length === 2, `expected 2 <li>, found ${items.length}`);
  const firstInput = items[0].querySelector("input");
  const secondInput = items[1].querySelector("input");
  assert(firstInput && !firstInput.hasAttribute("checked"), "expected the first item's checkbox unchecked");
  assert(secondInput && secondInput.hasAttribute("checked"), "expected the second item's checkbox checked");
});

console.log("Content preservation and lookup hardening:");

check("a disallowed wrapper keeps its sanitized children, not just their text", () => {
  // The links table is keyed by the raw destination and valued by
  // {href, exists} (see viewer.js's rewiring loop and every other renderBody
  // call in this file that passes a links table) -- not a bare string.
  const body = renderBody("<details><summary>S</summary>\n\n**bold** [l](a.md)\n\n</details>", {
    "a.md": { href: "a.html", exists: true },
  });
  assert(body.querySelector("strong"), "the <strong> inside the wrapper was flattened away");
  const a = body.querySelector("a");
  assert(a && a.getAttribute("href") === "a.html", "the rewired link inside the wrapper was lost");
  assert(!body.querySelector("details"), "<details> itself must not survive");
});

check("a table inside a disallowed <div> survives", () => {
  const body = renderBody("<div>\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n</div>");
  assert(body.querySelector("table"), "the table collapsed to text");
});

check("an Object.prototype key does not satisfy the input type constraint", () => {
  const body = renderBody('<input type="constructor">');
  assert(!body.querySelector("input"), "<input type=constructor> survived as a live input");
});

check("Object.prototype keys are not allowed attributes", () => {
  const body = renderBody('<a href="x.md" constructor="1" __proto__="2">l</a>', {
    "x.md": { href: "x.html", exists: true },
  });
  const a = body.querySelector("a");
  assert(a && !a.hasAttribute("constructor") && !a.hasAttribute("__proto__"), "prototype-named attributes survived");
});

check("script inside svg is dropped with its source text", () => {
  const body = renderBody("<svg><script>alert(1)</script></svg>");
  assert(!body.textContent.includes("alert(1)"), "script source leaked as visible text");
});

check("raw-text elements (iframe, xmp, noembed) are dropped with their source", () => {
  for (const tag of ["iframe", "xmp", "noembed"]) {
    const body = renderBody(`<${tag}><script>alert(1)</script></${tag}>`);
    assert(!body.textContent.includes("alert(1)"), `${tag} source leaked as visible text`);
  }
});

check("noframes is dropped with its source", () => {
  // NOT `<noframes>...` as the very first content: per the HTML parsing
  // spec, base/link/meta/noframes/script/style/template/title are all
  // processed via the "in head" raw-text rules regardless of where they
  // appear in the markup -- if nothing has yet forced "in body" insertion
  // mode (i.e. this tag is literally the first content), the element lands
  // in <head> and never reaches <div id="okf-body"> at all, making an
  // assertion against body content pass vacuously, having tested nothing.
  // A leading paragraph forces "in body" mode first.
  const body = renderBody("para\n\n<noframes><script>alert(1)</script></noframes>\n\nafter");
  assert(!body.textContent.includes("alert(1)"), "noframes source leaked as visible text");
});

check("<style> in HTML context is dropped with its source", () => {
  // Same head-hoisting quirk as noframes above -- needs a leading paragraph,
  // or a bare `<style>` lands in <head> and this tests nothing.
  const body = renderBody("para\n\n<style>body{display:none}</style>\n\nafter");
  assert(!body.textContent.includes("display:none"), "style source leaked as visible text");
  assert(!body.querySelector("style"), "<style> element survived");
});

check("<style> inside <svg> is dropped with its source", () => {
  const body = renderBody("para\n\n<svg><style>x{color:red}</style></svg>\n\nafter");
  assert(!body.textContent.includes("color:red"), "svg-namespace style source leaked as visible text");
  assert(!body.querySelector("style"), "<style> element survived");
});

check("<plaintext> is dropped along with the rest of the body it swallows", () => {
  // <plaintext> is the most aggressive raw-text element in the HTML parsing
  // spec: once the tokenizer sees the start tag it never leaves the
  // PLAINTEXT state again for the rest of the document -- there is no
  // closing tag; "</plaintext>" is itself just more literal text. Everything
  // from that point to EOF becomes a single text node, so dropping the
  // <plaintext> element necessarily drops that whole trailing text with it.
  // Real behaviour change from the old flatten-to-text code, noted in
  // CHANGELOG: the rest of the body vanishes instead of surfacing as source
  // text.
  const body = renderBody("<plaintext><script>alert(1)</script></plaintext>after");
  assert(!body.textContent.includes("alert(1)"), "plaintext source leaked as visible text");
  assert(!body.textContent.includes("after"), "content after <plaintext> unexpectedly survived");
});

console.log("\nScheme obfuscation (each rule of isSafeUrl has a case):");
for (const [name, href] of [
  ["entity-encoded tab", "java&#9;script:alert(1)"],
  ["percent-encoded tab", "java%09script:alert(1)"],
  ["double-encoded tab", "java%2509script:alert(1)"],
  ["newline inside the scheme", "java\nscript:alert(1)"],
  ["leading control characters", "javascript:alert(1)"],
]) {
  check(`${name} does not produce a live javascript: href`, () => {
    const body = renderBody(`<a href="${href}">t</a>`);
    const a = body.querySelector("a");
    assert(a && !a.hasAttribute("href"), `href survived for ${name}`);
  });
}

check("<template> content is never rendered", () => {
  const body = renderBody("<template><img src=x onerror=alert(1)></template>");
  assert(!body.querySelector("img") && !body.innerHTML.includes("onerror"), "template content leaked");
});

check("<base> is dropped", () => {
  assert(!renderBody('<base href="javascript:alert(1)//">').querySelector("base"), "<base> survived");
});

check("srcset is not an allowed attribute", () => {
  const img = renderBody('<img src="a.png" srcset="javascript:alert(1)">').querySelector("img");
  assert(img && !img.hasAttribute("srcset"), "srcset survived");
});

console.log("\nUnwrap correctness and performance (two-phase design):");

check("~200 nested disallowed wrappers around ~2,000 allowed children unwrap correctly, well inside a bound that separates linear from quadratic", () => {
  // Proves the linearity the two-phase design exists for -- picking the
  // bound relative to two directly measured numbers, not a guess. A
  // one-phase back-to-front unwrap (unwrap immediately, innermost first)
  // moves the entire already-unwrapped subtree up one more level for every
  // surviving ancestor wrapper, which is quadratic in depth x width: on this
  // machine, in jsdom, this EXACT shape (200 nested wrappers around 2,000
  // children) measures ~2.6s with the two-phase design below vs ~15.9s with
  // a stand-in for the reverted one-phase code (both figures reproduced in
  // task-D1-report.md, which also has real-browser numbers via Playwright
  // for the reviewer's original 500-wrapper/20,000-child shape: ~565ms
  // two-phase vs ~3.0s one-phase vs ~75ms for the pre-unwrap
  // flatten-to-text code -- all three consistent with the reviewer's own
  // Chromium measurement). jsdom's own per-mutation cost does not scale
  // linearly with tree size the way a real browser's does (confirmed
  // separately, not a defect in the two-phase design: even a plain
  // `insertBefore` chain shows the same superlinear jsdom overhead), which
  // is why this case is sized down from the 500/20,000 shape used for the
  // real-browser numbers above -- large enough to separate the two
  // algorithms by roughly 6x on this machine, small enough to run in a few
  // seconds under `npm test`. The bound below sits between the two
  // measurements (well above the observed two-phase time, well below the
  // observed one-phase time), so a regression back to innermost-first
  // unwrapping fails this case long before anyone has to eyeball a
  // stopwatch.
  const DEPTH = 200;
  const WIDTH = 2000;
  const md = "<div>".repeat(DEPTH) + "<em>x</em>".repeat(WIDTH) + "</div>".repeat(DEPTH);
  const t0 = Date.now();
  const body = renderBody(md);
  const ms = Date.now() - t0;
  assert(!body.querySelector("div"), "a disallowed <div> wrapper survived");
  const ems = body.querySelectorAll("em");
  assert(ems.length === WIDTH, `expected ${WIDTH} <em> elements to survive, found ${ems.length}`);
  assert(
    ms < 8000,
    `unwrap took ${ms}ms for ${DEPTH} nested wrappers around ${WIDTH} children -- ` +
      "want it well under the quadratic cost of an innermost-first unwrap (~15.9s for this shape on the machine that set this bound)"
  );
});

check("a disallowed wrapper nested inside an opaque element leaves nothing behind, and does not crash phase 2", () => {
  // <div> here is disallowed-but-not-opaque, so phase 1 queues it for
  // phase-2 unwrapping same as any other disallowed element -- but <div> is
  // nested inside <iframe>, which IS opaque, so phase 1 removes the whole
  // <iframe> subtree (the queued <div> included) before phase 2 ever runs.
  // By the time phase 2 reaches the queued <div>, it is no longer connected
  // to the document; this proves that case is handled (skipped, not thrown,
  // not resurrected) rather than merely reasoned about in a comment.
  const body = renderBody("<iframe><div><strong>hidden</strong></div></iframe>");
  assert(!body.querySelector("iframe"), "<iframe> survived");
  assert(!body.querySelector("div"), "<div> survived");
  assert(!body.querySelector("strong"), "<strong> resurfaced outside the removed <iframe>");
  assert(!body.textContent.includes("hidden"), "opaque element's nested content leaked as text");
});

// --- mutation-XSS: the unwrap changes tree shape, so re-parenting payloads
// that rely on browser parsing quirks (foster parenting, table/form/math
// scoping rules) get a fresh, explicit check rather than trusting that the
// earlier "flatten to text" behaviour happened to be safe for the same
// reason. -----------------------------------------------------------------

console.log("\nMutation XSS (re-parenting payloads must yield nothing executable):");

// Mirrors viewer.js's own ALLOWED_TAGS exactly (kept in sync by hand -- a
// drift here is a bug in this test, not in viewer.js, but the mutation
// harness (mutate.js, not committed) plus the "svg-namespace <a>" case below
// are what would actually catch a real ALLOWED_TAGS regression; this list
// exists so assertNothingExecutable can assert the *positive* property "every
// surviving element is on the allowlist", not just a curated negative list of
// three tag names.
const ALLOWED_TAGS_MIRROR = new Set([
  "P", "H1", "H2", "H3", "H4", "H5", "H6",
  "UL", "OL", "LI", "A", "IMG", "CODE", "PRE", "BLOCKQUOTE",
  "TABLE", "THEAD", "TBODY", "TFOOT", "TR", "TH", "TD",
  "STRONG", "EM", "DEL", "HR", "BR", "INPUT",
]);

// Mirrors viewer.js's SAFE_SCHEMES plus its isSafeUrl() control-character
// strip and bounded percent-decode loop, so this assertion judges a scheme
// the same way the sanitizer itself does rather than a narrower javascript:
// /data: blocklist that a scheme like vbscript: or an obfuscated encoding
// could slip past unnoticed.
const SAFE_SCHEMES_MIRROR = new Set(["http:", "https:", "mailto:"]);
function stripControlCharactersMirror(value) {
  let stripped = "";
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code > 32 && code !== 127) { stripped += value.charAt(i); }
  }
  return stripped;
}
function hasUnsafeScheme(raw) {
  if (!raw) { return false; }
  let value = String(raw);
  for (let round = 0; round < 5; round++) {
    value = stripControlCharactersMirror(value);
    let decoded;
    try {
      decoded = decodeURIComponent(value);
    } catch (e) {
      break;
    }
    if (decoded === value) { break; }
    value = decoded;
  }
  const scheme = /^([a-zA-Z][a-zA-Z0-9+.-]*):/.exec(value);
  if (!scheme) { return false; }
  return !SAFE_SCHEMES_MIRROR.has(scheme[1].toLowerCase() + ":");
}

/**
 * Walks every element under `body` and asserts none of it is executable:
 * every surviving element's tag (compared uppercased, so a foreign-namespace
 * SCRIPT/STYLE reporting a lowercase tagName is still caught) is on
 * ALLOWED_TAGS_MIRROR; no on-star, style, srcdoc, formaction or action
 * attribute survives anywhere (by name, not by tag -- an attacker-controlled attribute
 * name is exactly what must never survive regardless of which element it
 * landed on after re-parenting); and href/src/xlink:href never carry a
 * scheme outside SAFE_SCHEMES, judged by the same decode/strip logic
 * isSafeUrl() itself uses (a bare javascript:/data: blocklist would miss
 * vbscript: and obfuscated encodings). A bare structural check, not a
 * one-off string search, because the unwrap can relocate nodes in ways a
 * substring match over serialized HTML would not reliably catch.
 * @param {Element} body
 */
function assertNothingExecutable(body) {
  const all = body.querySelectorAll("*");
  for (const el of all) {
    const tag = el.tagName.toUpperCase();
    assert(ALLOWED_TAGS_MIRROR.has(tag), `an element survived off the allowlist: <${el.tagName}>`);
    for (const attr of Array.from(el.attributes)) {
      const name = attr.name.toLowerCase();
      assert(!/^on/.test(name), `live event handler attribute survived: ${attr.name} on <${el.tagName}>`);
      assert(
        !["style", "srcdoc", "formaction", "action"].includes(name),
        `dangerous attribute survived: ${attr.name} on <${el.tagName}>`
      );
    }
    for (const attrName of ["href", "src", "xlink:href"]) {
      const value = el.getAttribute(attrName);
      if (value === null) { continue; }
      assert(!hasUnsafeScheme(value), `${attrName} carries an unsafe scheme on <${el.tagName}>: ${value}`);
    }
  }
}

check("noscript/title re-parenting payload yields nothing executable", () => {
  const body = renderBody('<noscript><p title="</noscript><img src=x onerror=alert(1)>">hi</noscript>');
  assertNothingExecutable(body);
});

check("math/mtext/table/mglyph/style re-parenting payload yields nothing executable", () => {
  const body = renderBody("<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>");
  assertNothingExecutable(body);
});

check("svg/style/id re-parenting payload yields nothing executable", () => {
  const body = renderBody('<svg></p><style><a id="</style><img src=1 onerror=alert(1)>"></svg>');
  assertNothingExecutable(body);
});

check("form/math/mglyph/style re-parenting payload yields nothing executable", () => {
  const body = renderBody(
    "<form><math><mtext></form><form><mglyph><style></math><img src onerror=alert(1)>"
  );
  assertNothingExecutable(body);
});

check("svg-namespace <a xlink:href> is unwrapped with no href/xlink:href carried anywhere", () => {
  const body = renderBody('<svg><a xlink:href="javascript:alert(1)">x</a></svg>');
  const all = body.querySelectorAll("*");
  for (const el of all) {
    assert(!el.hasAttribute("href"), `an element carries a live href: <${el.tagName}>`);
    assert(!el.hasAttribute("xlink:href"), `an element carries a live xlink:href: <${el.tagName}>`);
  }
});

console.log(`\n${passed} passed, ${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
