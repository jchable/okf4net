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

console.log(`\n${passed} passed, ${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
