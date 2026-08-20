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
 */
function renderBody(markdown) {
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
    links: {},
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
  const body = renderBody("![<img src=x onerror=alert(1)>](y.png)");
  const imgs = body.querySelectorAll("img");
  assert(imgs.length === 1, `expected exactly one <img>, found ${imgs.length}`);
  assert(!imgs[0].hasAttribute("onerror"), "onerror attribute survived sanitization");
  assert(!body.innerHTML.toLowerCase().includes("onerror"), "onerror text leaked into output");
});

check("a raw <svg onload=...> is not rendered", () => {
  const body = renderBody('<svg onload="alert(1)"></svg>');
  assert(body.querySelectorAll("svg").length === 0, "an <svg> element reached the page");
  assert(!body.innerHTML.toLowerCase().includes("onload"), "onload text leaked into output");
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

check("a plain image with alt text renders", () => {
  const body = renderBody("![A nice diagram](diagram.png)");
  const img = body.querySelector("img");
  assert(img, "expected an <img> to survive");
  assert(img.getAttribute("src") === "diagram.png", "image src was altered or stripped");
  assert(img.getAttribute("alt") === "A nice diagram", "image alt text was altered or stripped");
});

console.log(`\n${passed} passed, ${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
