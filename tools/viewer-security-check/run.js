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
 * @param {(window: object) => void} [instrument] Called with the jsdom window
 *   after marked.min.js is loaded and before viewer.js runs, so a case can
 *   wrap DOM APIs (see the unwrap-cost cases at the end of this file).
 */
function renderBody(markdown, links, instrument) {
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
  if (instrument) { instrument(window); }
  window.eval(viewerSource);

  return window.document.getElementById("okf-body");
}

let failures = 0;
let passed = 0;

// Every case name is unique: a copy-pasted name would let a passing case hide a
// failing one in the output (and in any grep of it).
const caseNames = new Set();
// Counts every registration, duplicates included (a slice file must add some).
let casesRegistered = 0;
function duplicateName(name) {
  casesRegistered++;
  if (!caseNames.has(name)) {
    caseNames.add(name);
    return false;
  }
  failures++;
  console.log(`FAIL  - duplicate case name: ${name}`);
  return true;
}

/**
 * Runs a synchronous case body. Returns null when it passed, else the error.
 * A body that returns a thenable is a failure: an async function would print
 * "ok" before its assertions ran, and a rejection after the run ended would
 * leave the summary green.
 * @param {() => void} fn
 */
function attemptSync(fn) {
  try {
    const returned = fn();
    if (returned && typeof returned.then === "function") {
      returned.then(undefined, () => {});
      return new Error("check() received an async function (it returned a promise): use checkAsync");
    }
    return null;
  } catch (err) {
    return err;
  }
}

/** @param {string} name @param {() => void} fn */
function check(name, fn) {
  if (duplicateName(name)) { return; }
  const err = attemptSync(fn);
  if (err === null) {
    passed++;
    console.log(`  ok  - ${name}`);
  } else {
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

// --- mutation-XSS: the unwrap changes tree shape, so re-parenting payloads
// that rely on browser parsing quirks (foster parenting, table/form/math
// scoping rules) get a fresh, explicit check rather than trusting that the
// earlier "flatten to text" behaviour happened to be safe for the same
// reason. -----------------------------------------------------------------

console.log("\nMutation XSS (re-parenting payloads must yield nothing executable):");

// Mirrors viewer.js's own ALLOWED_TAGS, kept in sync by hand: nothing checks
// the two lists against each other, so a drift here is a bug in this test,
// not in viewer.js. It exists so assertNothingExecutable can assert the
// *positive* property "every surviving element is an XHTML element on the
// allowlist", not just a curated negative list of a few tag names.
const ALLOWED_TAGS_MIRROR = new Set([
  "P", "H1", "H2", "H3", "H4", "H5", "H6",
  "UL", "OL", "LI", "A", "IMG", "CODE", "PRE", "BLOCKQUOTE",
  "TABLE", "THEAD", "TBODY", "TFOOT", "TR", "TH", "TD",
  "STRONG", "EM", "DEL", "HR", "BR", "INPUT",
]);
const XHTML_NS = "http://www.w3.org/1999/xhtml";

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
 * every surviving element is in the XHTML namespace and its tag (compared
 * uppercased) is on ALLOWED_TAGS_MIRROR; no on-star, style, srcdoc, formaction or action
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
    // Namespace first: the tag comparison below is uppercased, so without
    // this an SVG- or MathML-namespace <a> ("a") would pass as an allowed
    // XHTML "A". viewer.js admits no foreign-namespace element at all.
    assert(
      el.namespaceURI === XHTML_NS,
      `a foreign-namespace element survived: <${el.tagName}> (${el.namespaceURI})`
    );
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
  // The namespace check inside assertNothingExecutable is what fails here if
  // the SVG <a> is ever admitted as an allowed "A" (e.g. by uppercasing
  // tagName before the ALLOWED_TAGS lookup) even with its attributes stripped.
  assertNothingExecutable(body);
  const all = body.querySelectorAll("*");
  for (const el of all) {
    assert(!el.hasAttribute("href"), `an element carries a live href: <${el.tagName}>`);
    assert(!el.hasAttribute("xlink:href"), `an element carries a live xlink:href: <${el.tagName}>`);
  }
});

// --- unwrap: structural cost, counted rather than timed ----------------------
//
// Moving a node in the DOM is not O(1): it drags the node's whole subtree
// with it (the DOM Standard's remove and insert algorithms visit every
// descendant of the node they move). An unwrap that moves already-assembled
// subtrees again and again is therefore superlinear however few moves it
// makes: the outermost-first unwrap this replaced dragged 94.2 x N nodes for
// 50 wrappers around 200 children and 201.0 x N for a 200-deep chain, and
// the innermost-first one before it 44.5 x N and 26.0 x N on the first and
// third shapes below. Wall-clock bounds cannot guard this (machine- and
// jsdom-dependent, and loose enough to pass a quadratic algorithm at any
// size small enough to run quickly), so no case in this harness asserts a
// time. Instead these cases count, deterministically, the
// nodes dragged by every structural DOM mutation while sanitize() runs, and
// bound that work by a small multiple of N, the node count of the parsed body.

/** Inclusive node count of `node`'s subtree. */
function subtreeSize(node) {
  let count = 0;
  const stack = [node];
  while (stack.length) {
    const n = stack.pop();
    count++;
    for (let c = n.firstChild; c; c = c.nextSibling) { stack.push(c); }
  }
  return count;
}

const WORK_BOUND = 4;

/**
 * Renders `markdown` like renderBody, counting the structural work viewer.js
 * does between DOMParser.parseFromString returning and the pipeline's next
 * `document.getElementById` call (the lookup of #okf-body that follows
 * sanitize()) -- i.e. the work of sanitize() itself.
 *
 * Counted: appendChild, insertBefore, removeChild and replaceChild, each
 * charged the size of every subtree it moves or removes (a DocumentFragment
 * argument is charged for its children, which is what actually moves).
 * Every other *generic* node-moving API (the ChildNode/ParentNode mixin
 * methods, insertAdjacent*, normalize, adoptNode, the mutating Range methods,
 * and the textContent/innerHTML/outerHTML setters) throws while counting, so
 * a sanitizer cannot dodge the count by switching to one of them; each is
 * looked up as an own property of the prototype jsdom defines it on, and a
 * missing one is a harness setup error rather than a silently unguarded API.
 * Element-specific mutators are NOT intercepted -- e.g.
 * HTMLSelectElement.remove(index), the table deleteRow/deleteCell methods,
 * the tHead/tFoot/caption setters, HTMLAnchorElement's text setter -- and
 * deleteRow removes a row without going through removeChild. None of them
 * can implement a generic unwrap, which is what these cases guard.
 *
 * @returns {{ body: Element, nodes: number, work: number, parsed: boolean, violations: string[] }}
 */
function renderBodyCountingWork(markdown) {
  const state = { counting: false, parsed: false, nodes: 0, work: 0, violations: [] };
  const body = renderBody(markdown, {}, (window) => {
    const own = (proto, name, owner) => {
      const descriptor = Object.getOwnPropertyDescriptor(proto, name);
      if (!descriptor) {
        throw new Error(`harness setup: ${owner}.prototype.${name} not found -- update renderBodyCountingWork for this jsdom`);
      }
      return descriptor;
    };
    const charge = (node) => {
      if (node.nodeType !== 11) { return subtreeSize(node); }
      let sum = 0;
      for (let c = node.firstChild; c; c = c.nextSibling) { sum += subtreeSize(c); }
      return sum;
    };

    const parserProto = window.DOMParser.prototype;
    const parseFromString = own(parserProto, "parseFromString", "DOMParser").value;
    parserProto.parseFromString = function (...args) {
      const doc = parseFromString.apply(this, args);
      state.nodes = subtreeSize(doc.body);
      state.parsed = true;
      state.counting = true;
      return doc;
    };
    const getElementById = window.document.getElementById;
    window.document.getElementById = function (...args) {
      state.counting = false;
      return getElementById.apply(this, args);
    };

    const nodeProto = window.Node.prototype;
    for (const name of ["appendChild", "insertBefore", "removeChild"]) {
      const original = own(nodeProto, name, "Node").value;
      nodeProto[name] = function (node, ...rest) {
        if (state.counting) { state.work += charge(node); }
        return original.call(this, node, ...rest);
      };
    }
    const replaceChild = own(nodeProto, "replaceChild", "Node").value;
    nodeProto.replaceChild = function (node, child) {
      if (state.counting) { state.work += charge(node) + subtreeSize(child); }
      return replaceChild.call(this, node, child);
    };

    const forbiddenMethods = {
      Node: ["normalize"],
      Element: [
        "append", "prepend", "before", "after", "replaceWith", "remove", "replaceChildren",
        "insertAdjacentElement", "insertAdjacentHTML", "insertAdjacentText",
      ],
      CharacterData: ["before", "after", "replaceWith", "remove"],
      DocumentFragment: ["append", "prepend", "replaceChildren"],
      Document: ["append", "prepend", "replaceChildren", "adoptNode"],
      Range: ["insertNode", "surroundContents", "extractContents", "deleteContents"],
    };
    for (const [owner, names] of Object.entries(forbiddenMethods)) {
      const proto = window[owner].prototype;
      for (const name of names) {
        const original = own(proto, name, owner).value;
        proto[name] = function (...args) {
          if (state.counting) {
            state.violations.push(`${owner}.${name}`);
            throw new Error(`uncounted DOM mutation API used during sanitize(): ${owner}.${name}`);
          }
          return original.apply(this, args);
        };
      }
    }
    for (const [owner, name] of [["Node", "textContent"], ["Element", "innerHTML"], ["Element", "outerHTML"]]) {
      const proto = window[owner].prototype;
      const descriptor = own(proto, name, owner);
      Object.defineProperty(proto, name, {
        ...descriptor,
        set(value) {
          if (state.counting) {
            state.violations.push(`${owner}.${name} setter`);
            throw new Error(`uncounted DOM mutation API used during sanitize(): ${owner}.${name} setter`);
          }
          descriptor.set.call(this, value);
        },
      });
    }
  });
  return { body, ...state };
}

/**
 * One unwrap-cost case: the work bound, then nothing executable, then the
 * shape-specific output check.
 */
function checkUnwrapWork(label, markdown, verifyOutput) {
  check(`${label}: sanitize() work <= ${WORK_BOUND} x N, and the output is exactly the allowed content`, () => {
    const r = renderBodyCountingWork(markdown);
    assert(r.parsed, "DOMParser.parseFromString never ran -- the counter measured nothing");
    assert(r.violations.length === 0, `uncounted DOM mutation APIs used: ${r.violations.join(", ")}`);
    const multiple = (r.work / r.nodes).toFixed(1);
    console.log(`        work: ${r.work} nodes dragged for N = ${r.nodes} (${multiple} x N)`);
    assert(r.work > 0, "no structural DOM work counted for a shape that must unwrap -- the counter is not wired");
    assert(
      r.work <= WORK_BOUND * r.nodes,
      `sanitize() dragged ${r.work} nodes through DOM moves for N = ${r.nodes} (${multiple} x N, bound ${WORK_BOUND} x N)`
    );
    assertNothingExecutable(r.body);
    assert(!r.body.querySelector("div"), "a disallowed <div> wrapper survived");
    verifyOutput(r.body);
  });
}

console.log("\nUnwrap cost (deterministic work count, never wall-clock) and correctness:");

{
  const DEPTH = 50;
  const WIDTH = 200;
  const children = Array.from({ length: WIDTH }, (_, i) => `<em>${i}</em>`).join("");
  checkUnwrapWork(`${DEPTH} nested <div>s around ${WIDTH} <em>s`, "<div>".repeat(DEPTH) + children + "</div>".repeat(DEPTH), (body) => {
    const texts = Array.from(body.querySelectorAll("em"), (em) => em.textContent);
    assert(texts.length === WIDTH, `expected ${WIDTH} <em>s to survive, found ${texts.length}`);
    for (let i = 0; i < WIDTH; i++) {
      assert(texts[i] === String(i), `<em> #${i} out of order: found ${JSON.stringify(texts[i])}`);
    }
  });
}

{
  const DEPTH = 200;
  checkUnwrapWork(`a ${DEPTH}-deep chain of <div>s around one <em>`, "<div>".repeat(DEPTH) + "<em>x</em>" + "</div>".repeat(DEPTH), (body) => {
    const ems = body.querySelectorAll("em");
    assert(ems.length === 1 && ems[0].textContent === "x", `expected one <em>x</em>, found ${ems.length}: ${body.innerHTML.slice(0, 200)}`);
  });
}

{
  const DEPTH = 100;
  checkUnwrapWork(`${DEPTH} alternating <div><em> levels`, "<div><em>".repeat(DEPTH) + "x" + "</em></div>".repeat(DEPTH), (body) => {
    // Each surviving <em> must hold exactly the next one: the <div> between
    // every pair is gone, the nesting and the innermost text are not.
    let em = body.querySelector("em");
    for (let level = 1; level <= DEPTH; level++) {
      assert(em && em.tagName === "EM", `level ${level}: expected an <em>, found ${em ? `<${em.tagName}>` : "nothing"}`);
      if (level < DEPTH) {
        assert(em.childNodes.length === 1, `level ${level}: expected exactly one child, found ${em.childNodes.length}`);
        em = em.firstChild;
      }
    }
    assert(em.childNodes.length === 1 && em.firstChild.nodeType === 3 && em.firstChild.data === "x", "the innermost text was lost");
  });
}

{
  // Nested OPAQUE elements, not just nested wrappers: inside <svg>, <style>
  // is an ordinary SVG element whose children parse as elements, so this
  // parses (checked against marked + DOMParser) to <p><svg> holding a
  // DEPTH-deep chain of <style> elements, each also holding a <g>t</g>.
  // Phase 1's removals stay within N only because it walks back-to-front:
  // each <style> is removed before any enclosing <style>, so no removal
  // drags a subtree an earlier removal already took. Walking front-to-back
  // removes the outermost <style> first and then every nested one again
  // from its already-detached parent -- quadratic, and invisible to the
  // three wrapper-only shapes above.
  const DEPTH = 80;
  checkUnwrapWork(`a ${DEPTH}-deep chain of nested <style>s inside <svg>`, "<svg>" + "<style><g>t</g>".repeat(DEPTH) + "</style>".repeat(DEPTH) + "</svg>", (body) => {
    assert(!body.querySelector("svg, style, g"), `an <svg>/<style>/<g> survived: ${body.innerHTML.slice(0, 200)}`);
    assert(body.textContent.trim() === "", `opaque content leaked as text: ${JSON.stringify(body.textContent.slice(0, 80))}`);
  });
}

check("sanitize() fails closed on a root that is not connected to any document", () => {
  // viewer.js always hands sanitize() `parsed.body`, which is connected to
  // the DOMParser document. A sanitizer must not depend on that: an earlier
  // version skipped every collected disallowed element that was not
  // `isConnected`, so on a detached root it unwrapped nothing, and since
  // attributes are only sanitized on *admitted* elements, <div onclick> and
  // <svg onload> survived intact. This hands sanitize() a detached root
  // through the real pipeline (no test hook in viewer.js): the parsed body's
  // children are moved into a fresh, unattached <div> that stands in for
  // `parsed.body`.
  let detached = false;
  const body = renderBody('<div onclick="alert(1)"><svg onload="alert(1)"><em>kept</em></svg></div>', {}, (window) => {
    const parserProto = window.DOMParser.prototype;
    const parseFromString = parserProto.parseFromString;
    parserProto.parseFromString = function (...args) {
      const doc = parseFromString.apply(this, args);
      const holder = doc.createElement("div");
      while (doc.body.firstChild) { holder.appendChild(doc.body.firstChild); }
      detached = !holder.isConnected;
      Object.defineProperty(doc, "body", { value: holder });
      return doc;
    };
  });
  assert(detached, "the stand-in root was connected -- this case tested nothing");
  assertNothingExecutable(body);
  const em = body.querySelector("em");
  assert(em && em.textContent === "kept", `the allowed <em> inside the wrappers was lost: ${body.innerHTML}`);
});

check("sanitize() aborts when its phase-2 walk meets an element with no phase-1 classification", () => {
  // Fault injection, not a reachable input. Phase 2 matches the nodes its
  // walk meets against a second querySelectorAll("*") snapshot; here that
  // second call (on the parsed document, not the page) is made to omit every
  // <div>, so the walk meets a <div style> that is not the next snapshot
  // entry. A one-sided consistency check -- only "every snapshot element was
  // met" -- treats that <div> as kept and renders it, attributes uncleaned,
  // without throwing. sanitize() must throw instead, which aborts rendering
  // before #okf-body is touched.
  let win = null;
  let snapshots = 0;
  let injected = false;
  let thrown = null;
  try {
    renderBody('<div style="color:red">x</div>', {}, (window) => {
      win = window;
      const querySelectorAll = window.Element.prototype.querySelectorAll;
      window.Element.prototype.querySelectorAll = function (selector) {
        const result = querySelectorAll.call(this, selector);
        if (selector === "*" && this.ownerDocument !== window.document && ++snapshots === 2) {
          injected = true;
          return Array.from(result).filter((el) => el.tagName !== "DIV");
        }
        return result;
      };
    });
  } catch (err) {
    thrown = err;
  }
  assert(injected, "the fault was never injected (no second snapshot any more?) -- this case tests nothing; rewrite it");
  assert(thrown && /^sanitize:/.test(thrown.message), `expected sanitize() to throw, got: ${thrown ? thrown.message : "no error"}`);
  const target = win.document.getElementById("okf-body");
  assert(target.innerHTML === "", `rendering was not aborted: ${target.innerHTML}`);
});

check("a disallowed element nested inside an opaque one is never resurrected", () => {
  // In HTML, an <iframe>'s content is raw text, so `<iframe><div>` never
  // produces a DIV element at all. Foreign content is where a disallowed
  // element really does sit inside an opaque one: inside <svg>, <style> is an
  // ordinary SVG element whose children parse as elements, so the <a> and
  // <g> below are real (SVG-namespace, hence disallowed) elements. Phase 1
  // walks back-to-front, so it collects them for unwrapping before it
  // reaches the enclosing <style> and removes it; they must not come back,
  // and neither may their text. The <em> breaks out of foreign content at
  // parse time and lands after the <svg>, so it is legitimate and survives.
  const body = renderBody("para\n\n<svg><style><a><g>secret<em>x</em></g></a></style></svg>\n\nafter");
  assertNothingExecutable(body);
  assert(!body.textContent.includes("secret"), "text inside the removed <style> was resurrected");
  const em = body.querySelector("em");
  assert(em && em.textContent === "x", `the <em> that broke out of foreign content was lost: ${body.innerHTML}`);
});

check("viewer.js alone, with only { body, links }, adds no global and still renders", () => {
  // The VS Code extension reuses viewer.js as is (ROADMAP.md): the
  // interactive scripts must never become a dependency of it.
  let before = null;
  const body = renderBody("# Title\n\ntext", {}, (window) => {
    before = new Set(Object.getOwnPropertyNames(window));
  });
  const added = Object.getOwnPropertyNames(body.ownerDocument.defaultView).filter((n) => !before.has(n));
  assert(added.length === 0, `viewer.js defined new globals: ${added.join(", ")}`);
  assert(body.querySelector("h1") && body.textContent.includes("text"), "viewer.js did not render on its own");
});

// --- interactive viewer (spec 2026-10-06, P1) -------------------------------
//
// Helper cases load the real okf-site.js into a bare window. Page cases load
// pages of a site GENERATED by the real okf-render from
// fixtures/hostile-bundle/ (npm's pretest step writes it to
// .generated/hostile-site/), with every script the page references, through
// a resource loader serving that directory. Page cases are async: they are
// queued and awaited before the summary, so a late assertion still fails
// the run.

const { requestInterceptor, VirtualConsole } = require("jsdom");
const SITE = path.join(__dirname, ".generated", "hostile-site");
const BASE = "https://okf.test/";
const siteSource = fs.readFileSync(path.join(ASSETS, "okf-site.js"), "utf8");

function okfSite() {
  const dom = new JSDOM("<!doctype html><html><body></body></html>", { runScripts: "outside-only" });
  dom.window.eval(siteSource);
  return dom.window;
}

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

// jsdom 29 has no ResourceLoader any more: resources go through undici
// interceptors. This one serves SITE under BASE + mount (so a case can
// "move" the generated folder) and never lets a request reach the network.
// A path in `blocked` answers with an empty script, as if the file defined
// nothing; a path in `override` answers with the given source instead of
// the generated file.
function siteResources(opts) {
  const prefix = BASE + (opts.mount || "");
  return {
    interceptors: [
      requestInterceptor((request) => {
        if (!request.url.startsWith(prefix)) { return new Response("", { status: 404 }); }
        const rel = decodeURIComponent(request.url.slice(prefix.length).split(/[?#]/)[0]);
        const type = rel.endsWith(".js") ? "application/javascript" : rel.endsWith(".css") ? "text/css" : "text/html";
        const headers = { "Content-Type": type };
        if ((opts.blocked || []).includes(rel)) { return new Response("", { headers }); }
        if (opts.override && Object.prototype.hasOwnProperty.call(opts.override, rel)) {
          return new Response(opts.override[rel], { headers });
        }
        return new Response(fs.readFileSync(path.join(SITE, rel)), { headers });
      }),
    ],
  };
}

// Pages opened by the running async case. Script errors raised AFTER load
// (an exception in a click or key handler) are recorded here and checked
// when the case returns, so a handler that throws cannot leave the run green.
let openedPages = [];

async function openPage(rel, opts = {}) {
  const file = path.join(SITE, rel);
  if (!fs.existsSync(file)) {
    throw new Error(`${file} is missing: run "npm test", whose pretest step regenerates the site with okf-render`);
  }
  const errors = [];
  const page = { window: null, errors, navigations: 0 };
  const virtualConsole = new VirtualConsole();
  virtualConsole.on("jsdomError", (err) => {
    // jsdom does not implement navigation; location.assign() lands here
    // when a case does not veto the palette's okf:navigate event. Not an
    // error, but counted, so a case can tell a vetoed navigation from one
    // that happened (see navigations()).
    if (/Not implemented: navigation/.test(err.message)) {
      page.navigations++;
    } else {
      errors.push(err);
    }
  });
  const dom = new JSDOM(fs.readFileSync(file, "utf8"), {
    url: BASE + (opts.mount || "") + rel + (opts.hash || ""),
    runScripts: "dangerously",
    resources: siteResources(opts),
    pretendToBeVisual: true,
    virtualConsole,
    beforeParse(window) {
      if (opts.now) { window.Date.now = () => opts.now(); }
      if (opts.storage === "denied") {
        const deny = function () { throw new window.DOMException("denied", "SecurityError"); };
        window.Storage.prototype.getItem = deny;
        window.Storage.prototype.setItem = deny;
      }
      if (opts.storedTheme) { window.localStorage.setItem("okf-theme", opts.storedTheme); }
      if (opts.beforeParse) { opts.beforeParse(window); }
    },
  });
  page.window = dom.window;
  openedPages.push(page);
  await new Promise((resolve) => dom.window.addEventListener("load", resolve));
  if (errors.length > 0) { throw new Error(`page script error during load: ${errors[0].message}`); }
  return dom.window;
}

// How many cross-document navigations (location.assign and the like) a page
// opened by the running case has attempted. Fragment-only changes are not
// counted: jsdom implements those.
function navigations(window) {
  const page = openedPages.find((p) => p.window === window);
  if (!page) { throw new Error("navigations(): this window was not opened by openPage in the running case"); }
  return page.navigations;
}

function key(window, target, init) {
  const event = new window.KeyboardEvent("keydown", Object.assign({ bubbles: true, cancelable: true }, init));
  target.dispatchEvent(event);
  return event;
}

function type(window, input, value) {
  input.value = value;
  input.dispatchEvent(new window.Event("input", { bubbles: true }));
}

const asyncChecks = [];
// Set when the page cases start: a case queued later would never run.
let queueClosed = false;

// checkAsync only queues: every async result prints after ALL synchronous
// output, under the last header printed. So page cases share the one
// "Interactive viewer pages" header below; a console.log header placed
// between checkAsync calls would print at queue time, above none of them.
/** @param {string} name @param {() => Promise<void>} fn */
function checkAsync(name, fn) {
  if (queueClosed) {
    failures++;
    console.log(`FAIL  - ${name}`);
    console.log("        checkAsync was called after the page cases started: register(h) must register synchronously");
    process.exitCode = 1;
    return;
  }
  if (duplicateName(name)) { return; }
  asyncChecks.push({ name, fn });
}

console.log("\nInteractive viewer helpers (okf-site.js):");

check("normalize trims, collapses whitespace and lower-cases without folding accents", () => {
  const { OkfSite } = okfSite();
  assert(OkfSite.normalize("  Gross \t Margin ") === "gross margin", "whitespace not normalized");
  assert(OkfSite.normalize("Écart") === "écart", "accents must not be folded");
});

check("rank orders by fixed tiers, keeps a concept's best tier, breaks ties by index", () => {
  const { OkfSite } = okfSite();
  const index = { concepts: [
    { id: "metrics/margin", title: "Margin", tags: ["finance"] },
    { id: "margin", title: "Margin overview", tags: [] },
    { id: "glossary/gross-margin", title: "Gross margin", tags: ["margin"] },
    { id: "x", title: "Other", tags: ["Margin"] },
    { id: "y", title: "Unrelated", tags: ["margins"] },
  ] };
  const got = JSON.stringify(Array.from(OkfSite.rank(index, " MARGIN ")));
  assert(got === "[1,0,2,3]", `expected [1,0,2,3] (exact id, title prefix, id substring, exact tag), got ${got}`);
  assert(OkfSite.rank(index, "   ").length === 0, "a blank query must return nothing");
});

check("isStale compares whole milliseconds and never reports a missing deadline", () => {
  const { OkfSite } = okfSite();
  assert(!OkfSite.isStale(1000, 999), "stale before the deadline");
  assert(OkfSite.isStale(1000, 1000), "not stale at the deadline (§5.5 is now >= stale_after)");
  assert(!OkfSite.isStale(null, 1e15), "a null deadline reported stale");
});

check("uniqueSlugs never lets a generated suffix collide with a real heading", () => {
  const { OkfSite } = okfSite();
  const got = JSON.stringify(Array.from(OkfSite.uniqueSlugs(["Usage", "Usage", "Usage 1", "!!!"])));
  assert(got === JSON.stringify(["usage", "usage-1", "usage-1-1", "section"]), `got ${got}`);
  const same = JSON.stringify(Array.from(OkfSite.uniqueSlugs(["Usage", "Usage", "Usage"])));
  assert(same === JSON.stringify(["usage", "usage-1", "usage-2"]), `three identical headings: ${same}`);
  // The per-base counter starts past suffixes it handed out, but a real
  // heading may already hold the next ones: the collision check must keep looping.
  const taken = JSON.stringify(Array.from(OkfSite.uniqueSlugs(["Usage 1", "Usage 2", "Usage", "Usage"])));
  assert(taken === JSON.stringify(["usage-1", "usage-2", "usage", "usage-3"]), `suffixes already taken: ${taken}`);
});

check("uniqueSlugs does linear work on many identical headings (counted, not timed)", () => {
  const window = okfSite();
  const n = 2000;
  let lookups = 0;
  const has = window.Set.prototype.has;
  window.Set.prototype.has = function (value) { lookups++; return has.call(this, value); };
  try {
    const texts = new Array(n).fill("Usage");
    const slugs = window.OkfSite.uniqueSlugs(texts);
    assert(new Set(Array.from(slugs)).size === n, "the slugs are not unique");
  } finally {
    window.Set.prototype.has = has;
  }
  assert(lookups > 0, "the probe saw no lookup: it no longer measures uniqueSlugs");
  assert(lookups <= 3 * n, `${lookups} set lookups for ${n} identical headings (quadratic would be ~${n * n / 2})`);
});

check("fragmentCandidates keeps a prefixed fragment and slugifies an author one", () => {
  const { OkfSite } = okfSite();
  const json = (h) => JSON.stringify(Array.from(OkfSite.fragmentCandidates(h)));
  assert(json("#okf-h-usage") === JSON.stringify(["okf-h-usage"]), json("#okf-h-usage"));
  assert(json("#Usage") === JSON.stringify(["okf-h-Usage", "okf-h-usage"]), json("#Usage"));
  assert(json("#caf%C3%A9") === JSON.stringify(["okf-h-café"]), json("#caf%C3%A9"));
  assert(json("#") === "[]", json("#"));
  // An all-punctuation fragment slugifies to nothing: it must not fall back
  // to "section" and land on a heading titled "Section".
  assert(json("#!!!") === JSON.stringify(["okf-h-!!!"]), json("#!!!"));
  assert(json("#%") === JSON.stringify(["okf-h-%"]), json("#%"));
});

check("rootOf accepts only a chain of ../", () => {
  const window = okfSite();
  const html = window.document.documentElement;
  html.setAttribute("data-okf-root", "../../");
  assert(window.OkfSite.rootOf(window.document) === "../../", "a valid root was rejected");
  html.setAttribute("data-okf-root", "javascript:alert(1)//");
  assert(window.OkfSite.rootOf(window.document) === "", "a non-../ root was accepted");
});

check("readIndex rejects an element standing in for the index (DOM clobbering)", () => {
  const window = okfSite();
  const div = window.document.createElement("div");
  div.id = "OKF_INDEX";
  window.document.body.appendChild(div);
  window.OKF_INDEX = div;
  assert(window.OkfSite.readIndex(window) === null, "an element was accepted as the index");
});

console.log("\nInteractive viewer pages (generated from fixtures/hostile-bundle):");

checkAsync("the generated index executes with hostile ids as plain values", async () => {
  const window = await openPage("foo.html");
  const ids = Array.from(window.OKF_INDEX.concepts, (c) => c.id);
  for (const id of ["__proto__", "constructor", "toString"]) {
    assert(ids.includes(id), `${id} missing from the executed index`);
  }
  assert(window.OKF_INDEX.concepts.find((c) => c.id === "__proto__").title === "Proto", "__proto__ lost its record");
  assert(Object.getPrototypeOf(window.OKF_INDEX) === window.Object.prototype, "the index object's prototype was replaced");
});

// The explorer link of the concept `id`, found by the data-okf-id attribute
// (spec §11.2, E8: the label is the last segment, the title a tooltip). The id
// is compared as a value, never spliced into a selector.
function treeLink(window, id) {
  return Array.from(window.document.querySelectorAll("#okf-explorer a.okf-tree-link"))
    .find((a) => a.getAttribute("data-okf-id") === id) || null;
}

function isShown(el) {
  return el !== null && !el.closest("[hidden]");
}

checkAsync("explorer: a node is both a page and a folder, with separate open and expand commands", async () => {
  const window = await openPage("foo/bar.html");
  assert(!window.document.getElementById("okf-explorer").hidden, "the explorer stayed hidden");
  const foo = treeLink(window, "foo");
  assert(foo && foo.getAttribute("href") === "../foo.html", `foo href: ${foo && foo.getAttribute("href")} (must resolve from the site root)`);
  const toggle = foo.parentElement.querySelector("button.okf-tree-toggle");
  assert(toggle, "foo has no expand button although foo/bar exists");
  assert(toggle.getAttribute("aria-expanded") === "true", "the path to the current page is not expanded");
  const current = window.document.querySelector('#okf-explorer a[aria-current="page"]');
  assert(current && current.getAttribute("data-okf-id") === "foo/bar", "the current page is not marked");
  toggle.click();
  assert(toggle.getAttribute("aria-expanded") === "false" && !isShown(treeLink(window, "foo/bar")), "the toggle did not collapse");
});

// Recette R2. jsdom does no layout, so this probe gives #okf-explorer a
// geometry: 800 px tall at 100 px from the top, 3000 px of content, the
// current entry `entryOffset` (default 2700) px down it, and the given computed overflow-y. It records
// every scroll request: the explorer's own scrollTop, window scrolling and
// scrollIntoView. That the entry is visible on screen is the recette's (C2).
// `head` and `foot` (in px, mutable through probe.head / probe.foot) give the
// sticky head and foot of the explorer (E12, E13) a height: the head sits at
// the explorer's top, the foot at the bottom of its visible part. `fonts`
// installs a document.fonts.ready that probe.resolveFonts() settles; `navTop`
// is the explorer's initial scrollTop.
function explorerScrollProbe(overflowY, layout = true, entryOffset = 2700, bars = {}) {
  const probe = { requests: [], navTop: bars.navTop || 0, head: bars.head || 0, foot: bars.foot || 0, resolveFonts: null };
  probe.beforeParse = (w) => {
    if (bars.fonts) {
      const ready = new Promise((resolve) => { probe.resolveFonts = resolve; });
      Object.defineProperty(w.document, "fonts", { configurable: true, value: { ready } });
    }
    const isNav = (el) => el.id === "okf-explorer";
    const proto = w.Element.prototype;
    if (layout) {
      Object.defineProperty(proto, "scrollHeight", { configurable: true, get() { return isNav(this) ? 3000 : 0; } });
      Object.defineProperty(proto, "clientHeight", { configurable: true, get() { return isNav(this) ? 800 : 0; } });
      proto.getBoundingClientRect = function () {
        const current = this.getAttribute("aria-current") === "page";
        const visibleHeight = Math.min(800, w.innerHeight - 100);
        const isHead = this.classList.contains("okf-explorer-head"), isFoot = this.classList.contains("okf-explorer-foot");
        const top = isNav(this) ? 100 : isHead ? 100 : isFoot ? 100 + visibleHeight - probe.foot : current ? 100 + entryOffset - probe.navTop : 0;
        const height = isNav(this) ? 800 : isHead ? probe.head : isFoot ? probe.foot : current ? 20 : 0;
        return { top, bottom: top + height, left: 0, right: 0, width: 0, height, x: 0, y: top };
      };
      const computed = w.getComputedStyle.bind(w);
      w.getComputedStyle = (el, pseudo) => {
        const style = computed(el, pseudo);
        if (!isNav(el)) { return style; }
        return { overflowY, getPropertyValue: (name) => (name === "overflow-y" ? overflowY : style.getPropertyValue(name)) };
      };
    }
    Object.defineProperty(proto, "scrollTop", {
      configurable: true,
      get() { return isNav(this) ? probe.navTop : 0; },
      set(v) { probe.requests.push(`scrollTop of <${this.tagName.toLowerCase()} id="${this.id}"> = ${v}`); if (isNav(this)) { probe.navTop = v; } },
    });
    for (const name of ["scroll", "scrollTo", "scrollBy"]) {
      w[name] = () => probe.requests.push(`window.${name}`);
      proto[name] = function () { probe.requests.push(`${name} on <${this.tagName.toLowerCase()}>`); };
    }
    proto.scrollIntoView = function () { probe.requests.push(`scrollIntoView on <${this.tagName.toLowerCase()}>`); };
  };
  return probe;
}

checkAsync("explorer: on the desktop layout the current entry is scrolled into the explorer's own view", async () => {
  const probe = explorerScrollProbe("auto");
  const window = await openPage("foo/bar.html", { beforeParse: probe.beforeParse });
  assert(probe.requests.length === 1 && /^scrollTop of <nav id="okf-explorer">/.test(probe.requests[0]),
    `scroll requests: ${JSON.stringify(probe.requests)} (expected the explorer's own scrollTop, once)`);
  const nav = window.document.getElementById("okf-explorer");
  const current = nav.querySelector('a[aria-current="page"]').getBoundingClientRect();
  const view = nav.getBoundingClientRect();
  const visibleBottom = Math.min(view.bottom, window.innerHeight);
  assert(current.top >= view.top && current.bottom <= visibleBottom,
    `the current entry sits at ${current.top}-${current.bottom}, outside the explorer's visible ${view.top}-${visibleBottom}`);
});

// The explorer scrolls only when the entry is out of view: an entry already
// inside the visible part (here 300 px down an 800 px explorer that is itself
// cut by the 768 px window) is left where it is, so reloading a page near the
// top does not move the tree under the reader.
checkAsync("explorer: a current entry already inside the visible part causes no scroll request", async () => {
  const probe = explorerScrollProbe("auto", true, 300);
  await openPage("foo/bar.html", { beforeParse: probe.beforeParse });
  assert(probe.requests.length === 0, `scroll requests: ${JSON.stringify(probe.requests)} (the entry was already visible)`);
});

checkAsync("explorer: without its own scroll container (stacked layout, no layout at all) nothing scrolls", async () => {
  for (const [label, probe] of [["overflow visible", explorerScrollProbe("visible")], ["no layout", explorerScrollProbe("auto", false)]]) {
    await openPage("foo/bar.html", { beforeParse: probe.beforeParse });
    assert(probe.requests.length === 0, `${label}: scroll requests ${JSON.stringify(probe.requests)}`);
  }
});

// P1.1 (E12, E13): the head and the foot of the explorer are sticky, so the
// entry must land between them. The explorer is 800 px at 100 px from the top
// of a 768 px window: its visible part is 668 px; with a 243 px head and a
// 90 px foot the band is 243 to 578 from its top.
function entryInBand(window, probe) {
  const nav = window.document.getElementById("okf-explorer");
  const entry = nav.querySelector('a[aria-current="page"]').getBoundingClientRect();
  const bandTop = 100 + probe.head, bandBottom = 100 + Math.min(800, window.innerHeight - 100) - probe.foot;
  return { ok: entry.top >= bandTop && entry.bottom <= bandBottom, text: `the entry sits at ${entry.top}-${entry.bottom}, outside the band ${bandTop}-${bandBottom}` };
}

checkAsync("explorer: a far entry lands between the sticky head and the sticky foot, not at a third of the whole", async () => {
  const probe = explorerScrollProbe("auto", true, 2700, { head: 243, foot: 90 });
  const window = await openPage("foo/bar.html", { beforeParse: probe.beforeParse });
  const band = entryInBand(window, probe);
  assert(probe.requests.length === 1 && band.ok, `${band.text}; requests ${JSON.stringify(probe.requests)}`);
});

checkAsync("explorer: an entry hidden under the sticky head is brought out from under it", async () => {
  const probe = explorerScrollProbe("auto", true, 700, { head: 243, foot: 90, navTop: 500 });
  const window = await openPage("foo/bar.html", { beforeParse: probe.beforeParse });
  const band = entryInBand(window, probe);
  assert(probe.requests.length === 1 && band.ok, `${band.text}; requests ${JSON.stringify(probe.requests)}`);
});

checkAsync("explorer: an entry just above the sticky foot stays, one under it is brought out", async () => {
  const inside = explorerScrollProbe("auto", true, 556, { head: 243, foot: 90 });
  await openPage("foo/bar.html", { beforeParse: inside.beforeParse });
  assert(inside.requests.length === 0, `an entry fully above the foot (556-576 of 578) was moved: ${JSON.stringify(inside.requests)}`);
  const under = explorerScrollProbe("auto", true, 560, { head: 243, foot: 90 });
  const window = await openPage("foo/bar.html", { beforeParse: under.beforeParse });
  const band = entryInBand(window, under);
  assert(under.requests.length === 1 && band.ok, `${band.text}; requests ${JSON.stringify(under.requests)}`);
});

checkAsync("explorer: when the web fonts are ready the entry is placed again, unless the reader has scrolled", async () => {
  const probe = explorerScrollProbe("auto", true, 2700, { head: 100, foot: 90, fonts: true });
  const window = await openPage("foo/bar.html", { beforeParse: probe.beforeParse });
  assert(probe.requests.length === 1 && entryInBand(window, probe).ok, "the first reveal did not place the entry");
  probe.head = 400;
  assert(!entryInBand(window, probe).ok, "this case needs the taller head to cover the entry");
  probe.resolveFonts();
  await new Promise((resolve) => setTimeout(resolve, 0));
  const band = entryInBand(window, probe);
  assert(probe.requests.length === 2 && band.ok, `after the fonts: ${band.text}; requests ${JSON.stringify(probe.requests)}`);

  const scrolled = explorerScrollProbe("auto", true, 2700, { head: 100, foot: 90, fonts: true });
  await openPage("foo/bar.html", { beforeParse: scrolled.beforeParse });
  scrolled.navTop += 500;
  scrolled.head = 400;
  scrolled.resolveFonts();
  await new Promise((resolve) => setTimeout(resolve, 0));
  assert(scrolled.requests.length === 1, `the reveal ran again after the reader scrolled: ${JSON.stringify(scrolled.requests)}`);
});

checkAsync("explorer links resolve from the site root wherever the generated folder is moved", async () => {
  const window = await openPage("foo/bar.html", { mount: "moved/elsewhere/" });
  const foo = treeLink(window, "foo");
  assert(foo.href === `${BASE}moved/elsewhere/foo.html`, `foo resolves to ${foo.href}`);
  const proto = treeLink(window, "__proto__");
  assert(proto.href === `${BASE}moved/elsewhere/__proto__.html`, `__proto__ resolves to ${proto.href}`);
});

checkAsync("explorer: the filter keeps the ancestors of a match and hides the rest", async () => {
  const window = await openPage("index.html");
  const filter = window.document.getElementById("okf-tree-filter");
  type(window, filter, "bar");
  assert(isShown(treeLink(window, "foo/bar")), "the match is hidden");
  assert(isShown(treeLink(window, "foo")), "the match's ancestor is hidden");
  assert(!isShown(treeLink(window, "toString")), "a non-matching concept stayed visible");
  type(window, filter, "");
  assert(isShown(treeLink(window, "toString")), "clearing the filter did not restore the tree");
});

checkAsync("explorer: ids naming Object.prototype members are distinct entries", async () => {
  const window = await openPage("index.html");
  for (const id of ["__proto__", "constructor", "toString"]) {
    const link = treeLink(window, id);
    assert(link && link.getAttribute("href") === `${id}.html`, `${id}: ${link && link.getAttribute("href")}`);
  }
});

checkAsync("explorer: trust badges follow ConceptAudit's tiers", async () => {
  const window = await openPage("index.html");
  const row = (id) => treeLink(window, id).parentElement;
  assert(row("foo/bar").querySelector(".okf-trust-human"), "human-reviewed badge missing");
  assert(row("broken").querySelector(".okf-trust-machine"), "machine-confirmed badge missing");
  assert(!row("toString").querySelector(".okf-trust-human, .okf-trust-machine"), "an unverified concept got a trust badge");
});

checkAsync("staleness is evaluated at reading time and refreshed when the page becomes visible", async () => {
  let now = Date.UTC(2026, 9, 6);
  const window = await openPage("index.html", { now: () => now });
  const stale = (id) => treeLink(window, id).parentElement.querySelector(".okf-flag-stale");
  assert(!stale("foo/bar").hidden, "a 2000 deadline is not shown as stale in 2026");
  assert(stale("broken").hidden, "a 2999 deadline is shown as stale in 2026");
  assert(stale("toString") === null, "a concept without stale_after got a stale mark");
  now = Date.UTC(3000, 0, 1);
  window.document.dispatchEvent(new window.Event("visibilitychange"));
  assert(!stale("broken").hidden, "visibilitychange did not refresh staleness");
});

checkAsync("a sub-millisecond deadline is stale from the next whole millisecond, as in C#", async () => {
  const midnight = Date.UTC(2026, 9, 6);
  const window = await openPage("index.html", { now: () => midnight });
  const edge = window.OKF_INDEX.concepts.find((c) => c.id === "edge");
  assert(edge.staleAfterMs === midnight + 1, `staleAfterMs ${edge.staleAfterMs}, expected ${midnight + 1}`);
  assert(edge.staleAfterDate === "2026-10-06", `staleAfterDate ${edge.staleAfterDate}`);
  assert(treeLink(window, "edge").parentElement.querySelector(".okf-flag-stale").hidden, "stale at .000 although the deadline is .0001");
  assert(window.OkfSite.isStale(edge.staleAfterMs, midnight + 1), "not stale one millisecond later");
});

// What this case can and cannot show: jsdom parses the WHOLE document before
// it runs even a <head> script (a MutationObserver sees <link> and <body>
// inserted before okf-theme.js executes), so "applied before the first
// paint" is not observable here. It pins the two halves a browser needs:
// okf-theme.js sits in <head> before the stylesheet, and it applies the
// stored choice synchronously while it runs (not on DOMContentLoaded or
// later). That no other theme flashes on load is checked by hand
// (ACCEPTANCE.md, "Theme").
checkAsync("theme: a stored choice is applied while okf-theme.js runs, ahead of the stylesheet; denied storage degrades without errors", async () => {
  let atScriptLoad = "not recorded";
  const window = await openPage("index.html", {
    storedTheme: "dark",
    beforeParse(w) {
      // A classic script's load event fires right after it has executed.
      w.document.addEventListener("load", (e) => {
        const src = e.target && e.target.nodeType === 1 ? e.target.getAttribute("src") || "" : "";
        if (/okf-theme\.js$/.test(src)) { atScriptLoad = w.document.documentElement.getAttribute("data-theme"); }
      }, true);
    },
  });
  const html = window.document.documentElement;
  const head = window.document.head;
  const themeScript = head.querySelector('script[src$="okf-theme.js"]');
  const stylesheet = head.querySelector('link[rel="stylesheet"]');
  assert(themeScript && stylesheet && (themeScript.compareDocumentPosition(stylesheet) & window.Node.DOCUMENT_POSITION_FOLLOWING),
    "okf-theme.js is not in <head> ahead of the stylesheet");
  assert(atScriptLoad === "dark", `when okf-theme.js had run, data-theme was ${atScriptLoad} (the stored choice must apply synchronously)`);
  assert(html.getAttribute("data-theme") === "dark", "the stored theme was not applied");
  const toggle = window.document.getElementById("okf-theme-toggle");
  assert(toggle && toggle.getAttribute("aria-pressed") === "true", "the toggle does not announce the dark state");
  toggle.click();
  assert(html.getAttribute("data-theme") === "light", "the toggle did not switch");
  assert(window.localStorage.getItem("okf-theme") === "light", "the choice was not stored");
  const denied = await openPage("index.html", { storage: "denied" });
  const toggle2 = denied.document.getElementById("okf-theme-toggle");
  assert(toggle2, "the toggle is missing when storage is denied");
  toggle2.click();
  assert(denied.document.documentElement.getAttribute("data-theme") === "dark", "the toggle is broken when storage is denied");
});

// Recette R5: native scrollbars and controls follow the theme. jsdom
// cascades color-scheme but never matches (prefers-color-scheme: dark), so
// the system-dark rule is seen in the browsers only (recette C6).
checkAsync("theme: color-scheme follows the theme, so native scrollbars and controls do too", async () => {
  const scheme = (w) => w.getComputedStyle(w.document.documentElement).getPropertyValue("color-scheme");
  const system = await openPage("index.html");
  assert(scheme(system) === "light dark", `with no forced theme, color-scheme is "${scheme(system)}", expected "light dark"`);
  const dark = await openPage("index.html", { storedTheme: "dark" });
  assert(scheme(dark) === "dark", `forced dark: color-scheme is "${scheme(dark)}"`);
  dark.document.getElementById("okf-theme-toggle").click();
  assert(scheme(dark) === "light", `forced light by the toggle: color-scheme is "${scheme(dark)}"`);
});

checkAsync("theme: the announced state follows a system preference change while nothing is forced", async () => {
  let query = null;
  const window = await openPage("index.html", {
    beforeParse(w) {
      query = new w.EventTarget();
      query.matches = false;
      w.matchMedia = () => query;
    },
  });
  const toggle = window.document.getElementById("okf-theme-toggle");
  assert(toggle.getAttribute("aria-pressed") === "false", "a light system preference is announced as dark");
  query.matches = true;
  query.dispatchEvent(new window.Event("change"));
  assert(toggle.getAttribute("aria-pressed") === "true", "a change of the system preference was not re-announced");
});

function paletteOptions(window) {
  return Array.from(window.document.querySelectorAll("#okf-palette-list [role=option]"));
}

function optionId(option) {
  return option.querySelector(".okf-palette-id").textContent;
}

checkAsync("palette: shortcuts open it only outside editable fields and IME composition", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const backdrop = doc.querySelector(".okf-palette-backdrop");
  key(window, doc.body, { key: "k", ctrlKey: true, isComposing: true });
  assert(backdrop.hidden, "opened during IME composition");
  // The explorer's real filter field (an <input type="search">), not a
  // stand-in: "/" is an ordinary character there and must reach the field.
  const filter = doc.getElementById("okf-tree-filter");
  assert(filter, "this case needs the explorer's filter field");
  filter.focus();
  const slashInField = key(window, filter, { key: "/" });
  assert(backdrop.hidden, "'/' typed in the tree filter opened the palette");
  assert(!slashInField.defaultPrevented, "'/' typed in the tree filter was prevented, so the field never receives it");
  const ctrlKInField = key(window, filter, { key: "k", ctrlKey: true });
  assert(backdrop.hidden, "Ctrl+K in an editable field opened the palette");
  assert(!ctrlKInField.defaultPrevented, "Ctrl+K in the tree filter was prevented although the palette did not take it");
  filter.blur();
  // Any other text field too: a plain <input> (type text) and a <textarea>.
  for (const tag of ["input", "textarea"]) {
    const field = doc.createElement(tag);
    doc.body.appendChild(field);
    field.focus();
    const slash = key(window, field, { key: "/" });
    assert(backdrop.hidden, `'/' typed in a <${tag}> opened the palette`);
    assert(!slash.defaultPrevented, `'/' typed in a <${tag}> was prevented, so the field never receives it`);
    field.remove();
  }
  key(window, doc.body, { key: "k", ctrlKey: true, altKey: true });
  assert(backdrop.hidden, "Ctrl+Alt+K opened the palette");
  const event = key(window, doc.body, { key: "k", ctrlKey: true });
  assert(!backdrop.hidden && event.defaultPrevented, "Ctrl+K did not open the palette, or was not prevented");
  assert(doc.activeElement === doc.getElementById("okf-palette-input"), "focus did not move into the field");
});

checkAsync("palette: modal focus, arrows, Enter, Escape and focus return", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const opener = doc.querySelector(".okf-palette-open");
  opener.focus();
  opener.click();
  const input = doc.getElementById("okf-palette-input");
  const close = doc.querySelector(".okf-palette-close");
  assert(doc.activeElement === input, "focus is not in the field on open");
  key(window, input, { key: "Tab" });
  assert(doc.activeElement === close, "Tab did not move to Close");
  key(window, close, { key: "Tab" });
  assert(doc.activeElement === input, "Tab escaped the modal");
  type(window, input, "o");
  assert(paletteOptions(window).length > 1, "this case needs several results");
  assert(paletteOptions(window)[0].getAttribute("aria-selected") === "true", "the first option is not active");
  key(window, input, { key: "ArrowDown" });
  const second = paletteOptions(window)[1];
  assert(second.getAttribute("aria-selected") === "true", "ArrowDown did not move");
  assert(input.getAttribute("aria-activedescendant") === second.id, "aria-activedescendant not updated");
  assert(/matching concept/.test(doc.getElementById("okf-palette-status").textContent), "the result count is not announced");
  let navigated = null;
  doc.addEventListener("okf:navigate", (e) => { navigated = e.detail.href; e.preventDefault(); });
  key(window, input, { key: "Enter" });
  assert(navigated === `${optionId(second)}.html`, `navigated to ${navigated}`);
  key(window, input, { key: "Escape" });
  assert(doc.querySelector(".okf-palette-backdrop").hidden, "Escape did not close");
  assert(doc.activeElement === opener, "focus did not return to the opener");
});

checkAsync("palette: the active concept survives a narrower query, else the first option is active", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  const selected = () => paletteOptions(window).find((o) => o.getAttribute("aria-selected") === "true");
  type(window, input, "o");
  const target = paletteOptions(window).map(optionId).indexOf("foo/bar");
  assert(target > 0, "this case needs foo/bar after the first result");
  for (let k = 0; k < target; k++) { key(window, input, { key: "ArrowDown" }); }
  assert(optionId(selected()) === "foo/bar", `active before narrowing: ${optionId(selected())}`);
  type(window, input, "foo");
  const ids = paletteOptions(window).map(optionId);
  // The kept concept must NOT be first, or "keep it" and "reset to the first" look the same.
  assert(ids[0] === "foo" && ids[1] === "foo/bar", `order under "foo": ${ids.join(",")}`);
  assert(optionId(selected()) === "foo/bar", `active after narrowing: ${optionId(selected())}`);
  type(window, input, "bro");
  assert(selected() === paletteOptions(window)[0], "the first option is not active once the active one is filtered out");
});

// Recette R3: an option made active by an earlier keystroke, not by the
// arrows, must not survive the narrowing, or Enter opens a worse match.
checkAsync("palette: typing without the arrows keeps the first result active, and Enter opens it", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  const selected = () => paletteOptions(window).find((o) => o.getAttribute("aria-selected") === "true");
  type(window, input, "t");
  assert(optionId(selected()) === "toString", `active under "t": ${optionId(selected())} (this case needs toString first)`);
  type(window, input, "tr");
  const ids = paletteOptions(window).map(optionId);
  // toString still matches "tr" but no longer first: "keep it" and "reset to the first" differ here.
  assert(ids[0] === "constructor" && ids.includes("toString"), `order under "tr": ${ids.join(",")}`);
  assert(optionId(selected()) === "constructor", `active under "tr": ${optionId(selected())}, expected the first result`);
  let navigated = null;
  doc.addEventListener("okf:navigate", (e) => { navigated = e.detail.href; e.preventDefault(); });
  key(window, input, { key: "Enter" });
  assert(navigated === "constructor.html", `Enter navigated to ${navigated}`);
});

checkAsync("palette: ids naming Object.prototype members are found as concepts", async () => {
  const window = await openPage("index.html");
  window.document.querySelector(".okf-palette-open").click();
  type(window, window.document.getElementById("okf-palette-input"), "constructor");
  assert(optionId(paletteOptions(window)[0]) === "constructor", "constructor is not the first result");
});

checkAsync("palette: keys typed during IME composition never act on the open palette", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "o");
  let navigated = false;
  doc.addEventListener("okf:navigate", (e) => { navigated = true; e.preventDefault(); });
  key(window, input, { key: "ArrowDown", isComposing: true });
  assert(paletteOptions(window)[0].getAttribute("aria-selected") === "true", "ArrowDown moved during composition");
  key(window, input, { key: "Enter", isComposing: true });
  assert(!navigated, "Enter navigated during composition");
  key(window, input, { key: "Escape", isComposing: true });
  assert(!doc.querySelector(".okf-palette-backdrop").hidden, "Escape closed the palette during composition");
});

checkAsync("palette: the active option is scrolled into view", async () => {
  const window = await openPage("index.html");
  // jsdom has no scrollIntoView: a probe records the calls. The visual
  // result is checked in ACCEPTANCE.md.
  const calls = [];
  window.Element.prototype.scrollIntoView = function (options) {
    calls.push({ id: this.id, block: options && options.block });
  };
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "o");
  key(window, input, { key: "ArrowDown" });
  const last = calls[calls.length - 1];
  assert(last && last.id === "okf-palette-opt-1" && last.block === "nearest", `last scroll: ${JSON.stringify(last)}`);
});

checkAsync("palette: every match is listed and reachable, with no cap", async () => {
  const concepts = [];
  for (let k = 0; k < 60; k++) {
    const id = `item-${String(k).padStart(2, "0")}`;
    concepts.push({ id, title: `Item ${k}`, type: "Note", tags: [], path: `${id}.html`, trust: "unverified", staleAfterMs: null, staleAfterDate: null, typeIndex: 0, description: "" });
  }
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 2, concepts, ghosts: [], edges: [], tree: [], types: [{ name: "Note", count: 60, slot: 0 }] })};`;
  const window = await openPage("index.html", { override: { "assets/okf-index.js": source } });
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "item");
  assert(paletteOptions(window).length === 60, `listed ${paletteOptions(window).length} of 60`);
  const status = doc.getElementById("okf-palette-status").textContent;
  assert(status === "60 matching concepts", `status: ${status}`);
  key(window, input, { key: "ArrowUp" });
  const last = paletteOptions(window)[59];
  assert(optionId(last) === "item-59" && last.getAttribute("aria-selected") === "true", "the 60th match is not reachable");
});

checkAsync("palette: '/' outside an editable field opens it, and is prevented", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const event = key(window, doc.body, { key: "/" });
  assert(!doc.querySelector(".okf-palette-backdrop").hidden && event.defaultPrevented, "'/' did not open the palette, or was not prevented");
  assert(doc.activeElement === doc.getElementById("okf-palette-input"), "focus did not move into the field");
});

checkAsync("palette: keys keep working when focus has fallen to the page", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const backdrop = doc.querySelector(".okf-palette-backdrop");
  const opener = doc.querySelector(".okf-palette-open");
  const input = doc.getElementById("okf-palette-input");
  opener.focus();
  opener.click();
  // A click on the dialog's own text moves focus off the input (to the dialog,
  // or to <body> when nothing there is focusable).
  const dialog = doc.querySelector(".okf-palette");
  assert(dialog.getAttribute("tabindex") === "-1", "the dialog is not focusable, so a click inside it drops focus to <body>");
  dialog.focus();
  key(window, dialog, { key: "Tab" });
  assert(doc.activeElement === input, "Tab from the dialog did not land on the field");
  doc.activeElement.blur();
  assert(doc.activeElement === doc.body, "this case needs focus on <body>");
  key(window, doc.body, { key: "Tab" });
  assert(backdrop.contains(doc.activeElement), "Tab from <body> left the modal");
  doc.activeElement.blur();
  type(window, input, "o");
  key(window, doc.body, { key: "ArrowDown" });
  assert(doc.getElementById("okf-palette-opt-1").getAttribute("aria-selected") === "true", "ArrowDown from <body> did not move");
  let navigated = null;
  doc.addEventListener("okf:navigate", (e) => { navigated = e.detail.href; e.preventDefault(); });
  key(window, doc.body, { key: "Enter" });
  assert(navigated !== null, "Enter from <body> did not navigate");
  key(window, doc.body, { key: "Escape" });
  assert(backdrop.hidden, "Escape from <body> did not close");
  assert(doc.activeElement === opener, "focus did not return to the opener");
});

checkAsync("palette: focus returns to the element that had it when a shortcut opened the palette", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const link = doc.querySelector("a[href]");
  link.focus();
  assert(doc.activeElement === link, "this case needs a focusable link");
  key(window, link, { key: "k", ctrlKey: true });
  assert(!doc.querySelector(".okf-palette-backdrop").hidden, "Ctrl+K from a link did not open the palette");
  key(window, doc.getElementById("okf-palette-input"), { key: "Escape" });
  assert(doc.activeElement === link, "focus did not return to the link");
});

checkAsync("palette: arrow keys move the active option without rebuilding the list", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "o");
  const before = paletteOptions(window);
  key(window, input, { key: "ArrowDown" });
  const after = paletteOptions(window);
  assert(before.length === after.length && before.every((o, k) => o === after[k]), "the option elements were replaced");
  assert(after.filter((o) => o.getAttribute("aria-selected") === "true").length === 1, "not exactly one selected option");
  assert(after[1].getAttribute("aria-selected") === "true" && after[0].getAttribute("aria-selected") === "false", "selection did not move");
});

checkAsync("palette: aria-expanded follows whether there are results", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "o");
  assert(input.getAttribute("aria-expanded") === "true", "not expanded with results");
  type(window, input, "zzzz-no-such-concept");
  assert(input.getAttribute("aria-expanded") === "false", "still expanded with no results");
});

checkAsync("palette: from a nested page, the target resolves up to the site root", async () => {
  for (const opts of [{}, { mount: "moved/dir/" }]) {
    const window = await openPage("foo/bar.html", opts);
    const doc = window.document;
    doc.querySelector(".okf-palette-open").click();
    const input = doc.getElementById("okf-palette-input");
    type(window, input, "edge");
    let navigated = null;
    doc.addEventListener("okf:navigate", (e) => { navigated = e.detail.href; e.preventDefault(); });
    key(window, input, { key: "Enter" });
    assert(navigated === "../edge.html", `navigated to ${navigated} (${JSON.stringify(opts)})`);
  }
});

checkAsync("palette: a vetoed okf:navigate does not navigate, an unvetoed one does", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "edge");
  const veto = (e) => e.preventDefault();
  doc.addEventListener("okf:navigate", veto);
  key(window, input, { key: "Enter" });
  assert(navigations(window) === 0, `a vetoed okf:navigate still navigated (${navigations(window)} navigations)`);
  doc.removeEventListener("okf:navigate", veto);
  key(window, input, { key: "Enter" });
  assert(navigations(window) === 1, `an unvetoed okf:navigate made ${navigations(window)} navigations, expected 1`);
});

checkAsync("headings get generated ids only, the contents list them, no content id survives", async () => {
  const window = await openPage("foo.html");
  const doc = window.document;
  const ids = Array.from(doc.querySelectorAll("#okf-body h1, #okf-body h2, #okf-body h3"), (h) => h.id);
  assert(JSON.stringify(ids.slice(0, 3)) === JSON.stringify(["okf-h-usage", "okf-h-usage-1", "okf-h-usage-1-1"]), `ids: ${ids}`);
  assert(ids.every((id) => id.startsWith("okf-h-")), `an id escaped the prefix: ${ids}`);
  assert(doc.getElementById("OKF_INDEX") === null, "an id from bundle content survived");
  assert(doc.querySelectorAll("#okf-body [name]").length === 0, "a name attribute from bundle content survived");
  const toc = doc.getElementById("okf-toc");
  assert(!toc.hidden && !doc.getElementById("okf-context").hidden, "the contents list was not shown");
  assert(toc.querySelector("a").getAttribute("href") === "#okf-h-usage", "the first contents link is wrong");
});

checkAsync("an author fragment resolves to the generated heading, on load and on click, as a real navigation", async () => {
  const opened = await openPage("foo/bar.html", { hash: "#usage" });
  const focused = opened.document.activeElement;
  assert(focused && focused.id === "okf-h-usage", `focus on load: ${focused && focused.id}`);
  const window = await openPage("foo.html");
  const link = Array.from(window.document.querySelectorAll("#okf-body a")).find((a) => a.getAttribute("href") === "#usage");
  assert(link, "the fixture lost its #usage link");
  const before = window.history.length;
  const changed = new Promise((resolve) => window.addEventListener("hashchange", resolve, { once: true }));
  const event = new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
  link.dispatchEvent(event);
  assert(event.defaultPrevented, "the click was left to the browser, which finds no element 'usage'");
  await changed;
  assert(window.location.hash === "#okf-h-usage", `URL fragment after click: ${window.location.hash}`);
  assert(window.history.length === before + 1, `history length ${window.history.length}, expected ${before + 1}`);
  assert(window.document.activeElement.id === "okf-h-usage", `focus after click: ${window.document.activeElement.id}`);
  for (const modifier of ["ctrlKey", "shiftKey", "metaKey", "altKey"]) {
    const modified = new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0, [modifier]: true });
    link.dispatchEvent(modified);
    assert(!modified.defaultPrevented, `a click with ${modifier} was taken over instead of left to the browser (new tab, new window, download)`);
  }
});

checkAsync("clicking an accented fragment link a second time still moves the reading focus", async () => {
  const window = await openPage("foo.html");
  const doc = window.document;
  // marked percent-encodes the destination: the href is "#caf%C3%A9".
  const link = Array.from(doc.querySelectorAll("#okf-body a")).find((a) => decodeURIComponent(a.getAttribute("href")) === "#café");
  assert(link, "the fixture lost its #café link");
  const changed = new Promise((resolve) => window.addEventListener("hashchange", resolve, { once: true }));
  link.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
  await changed;
  assert(doc.activeElement.id === "okf-h-café", `focus after the first click: ${doc.activeElement.id}`);
  link.focus();
  link.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
  assert(doc.activeElement.id === "okf-h-café", `focus after the second click stayed on: ${doc.activeElement.tagName} ${doc.activeElement.id}`);
});

checkAsync("clicking a contents link a second time still moves the reading focus", async () => {
  const window = await openPage("foo.html");
  const doc = window.document;
  const link = doc.querySelector('#okf-toc a[href="#okf-h-usage-1"]');
  assert(link, "the contents lost their second Usage entry");
  const changed = new Promise((resolve) => window.addEventListener("hashchange", resolve, { once: true }));
  link.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
  await changed;
  assert(doc.activeElement.id === "okf-h-usage-1", `focus after the first click: ${doc.activeElement.id}`);
  // Same fragment again: no hashchange fires, so focus must be moved directly.
  link.focus();
  link.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
  assert(doc.activeElement.id === "okf-h-usage-1", `focus after the second click stayed on: ${doc.activeElement.tagName} ${doc.activeElement.id}`);
  const modified = new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0, ctrlKey: true });
  link.dispatchEvent(modified);
  assert(!modified.defaultPrevented, "a Ctrl+click on a contents link was taken over instead of left to the browser");
});

checkAsync("a page with no h2/h3 and no backlinks keeps the side panel hidden", async () => {
  // constructor.md: one paragraph, no heading, and no concept links to it.
  const window = await openPage("constructor.html");
  const doc = window.document;
  assert(doc.getElementById("okf-body").textContent.includes("Plain"), "the body did not render");
  assert(doc.querySelectorAll("#okf-body h2, #okf-body h3").length === 0, "this case needs a page without h2/h3");
  assert(doc.querySelector("#okf-context .okf-backlinks") === null, "this case needs a page without backlinks");
  assert(doc.getElementById("okf-toc").hidden, "an empty contents list was shown");
  assert(doc.getElementById("okf-context").hidden, "an empty side panel was shown");
});

checkAsync("without okf-index.js the page renders, the explorer stays hidden and nothing is clobbered", async () => {
  const window = await openPage("foo.html", { blocked: ["assets/okf-index.js"] });
  const doc = window.document;
  assert(window.OKF_INDEX === undefined, "this case needs okf-index.js blocked");
  assert(doc.getElementById("okf-explorer").hidden, "the explorer rendered without an index");
  assert(doc.getElementById("okf-tree-filter") === null, "the explorer built its filter without an index");
  assert(doc.querySelector(".okf-palette-open") === null, "the palette rendered without an index");
  // No palette, so its shortcuts are not taken: the browser keeps "/" and Ctrl+K.
  for (const init of [{ key: "/" }, { key: "k", ctrlKey: true }]) {
    const event = key(window, doc.body, init);
    assert(!event.defaultPrevented, `${JSON.stringify(init)} was prevented although there is no palette`);
  }
  assert(doc.querySelector(".okf-palette-backdrop") === null, "a shortcut built the palette without an index");
  assert(window.OkfSite.readIndex(window) === null, "readIndex accepted something that is not the index");
  assert(doc.getElementById("OKF_INDEX") === null, "bundle content created an element named OKF_INDEX");
  assert(doc.getElementById("okf-body").textContent.includes("first"), "the body did not render");
  assert(!doc.getElementById("okf-toc").hidden, "the contents list depends on the index");
});

checkAsync("explorer, palette and contents render hostile titles as inert text", async () => {
  const window = await openPage("foo.html");
  const doc = window.document;
  // Spec §7, control 3: the DOM is inspected AFTER opening, filtering and
  // selection, since each of those re-renders bundle text.
  type(window, doc.getElementById("okf-tree-filter"), "foo");
  assert(isShown(treeLink(window, "foo")), "the tree filter hid the hostile concept");
  assert(!isShown(treeLink(window, "toString")), "the tree filter did not filter");
  key(window, doc.body, { key: "/" });
  assert(!doc.querySelector(".okf-palette-backdrop").hidden, "'/' did not open the palette");
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "foo");
  assert(paletteOptions(window).map(optionId).includes("foo"), "the palette does not list the hostile concept");
  key(window, input, { key: "ArrowDown" });
  assert(input.getAttribute("aria-activedescendant") === "okf-palette-opt-1", "ArrowDown did not move the selection");
  for (const id of ["okf-explorer", "okf-palette-list", "okf-toc"]) {
    const root = doc.getElementById(id);
    // svg.okf-glyph is the chrome's own glyph (OkfShapes.icon, Tasks 13 and
    // 14 draw one per explorer row and palette option); any other svg would
    // still be markup that bundle text made live.
    assert(root.querySelectorAll("img, script, iframe, object, svg:not(.okf-glyph)").length === 0, `markup from bundle text became live in #${id}`);
  }
  assert(treeLink(window, "foo").getAttribute("title").includes("<img") && treeLink(window, "foo").textContent === "foo",
    "the explorer row of foo must show its segment and carry the hostile title as a tooltip");
  assert(doc.getElementById("okf-palette-list").textContent.includes("<img"), "the palette dropped the hostile title");
  assert(doc.getElementById("okf-toc").textContent.includes("<img"), "the contents dropped the hostile heading text");
  assert(doc.querySelectorAll("[onerror]").length === 0, "an onerror attribute reached the page");
  assert(window.__pwned === undefined, "a hostile title executed");
});

// The sanitizer keeps `class` on <code>, so body content can wear any chrome
// class. Every chrome rule is anchored to a chrome container: inside
// #okf-body such a class must change nothing (no overlay faking a dialog, no
// fake trust badge, no text hidden from sighted readers). Each slice lists its
// chrome classes in its own fixture page (spec §12.6): chrome-classes.md
// (P1), p11-chrome-classes.md (P1.1), p2-chrome-classes.md (P2),
// p3-chrome-classes.md (P3). The cases below run on every such page the
// generated site holds, so a slice's page is covered as soon as its fixture
// exists. Three layers, each with its own reach and none a proof:
//   1. a static scan of the selector text of the stylesheet (every selector
//      naming a class starts from a §12.6 anchor): a smoke check, but the only
//      layer that sees a rule whose class nothing wears;
//   2. a selector-matching differential: on a tree of elements wearing every
//      class the page lists AND every class any rule names, an element must
//      match exactly the class-naming selectors its unclassed twin matches,
//      pseudo-elements stripped (jsdom cannot compute their style), @media
//      rules included; independent of which properties a rule sets;
//   3. a computed-style comparison of the same pairs over every property the
//      stylesheet declares.
// Limits: all of it runs on what jsdom's CSSOM and selector engine understand
// (an at-rule or selector it drops is invisible), and the trees hold every tag
// the sanitizer admits, nothing else.
// The floor of properties compared even if no rule declares them:
const CHROME_PROPS = ["position", "display", "width", "height", "clip", "clip-path", "overflow", "z-index", "inset", "top",
  "background", "background-color", "border", "border-left", "border-left-color", "border-radius", "border-width",
  "padding", "margin", "white-space", "max-width", "min-height", "cursor", "flex", "list-style", "font-family",
  "font-size", "color", "text-transform", "outline", "overflow-wrap", "min-width", "order", "align-items",
  "flex-direction", "flex-basis", "flex-shrink", "flex-wrap", "margin-top", "overflow-x",
  // Properties the P1.1 chrome sets as well.
  "font-weight", "letter-spacing", "line-height", "text-decoration", "visibility", "text-overflow", "border-top",
  "border-bottom", "border-color", "box-shadow", "opacity", "gap", "justify-content", "vertical-align", "text-align",
  "max-height", "padding-left", "padding-top", "grid-template-columns", "flex-grow", "fill", "stroke", "left", "bottom",
  "transform", "background-image", "content", "filter", "pointer-events"];

// --- static selector analysis: begin (the plan-CSS proof extracts this block) ---

// Calls visit(rule) for every style rule of every stylesheet of a page, into
// @media, @supports and any other grouping rule jsdom's CSSOM keeps. CSS
// nesting (a rule inside a style rule, selectors with `&`) is walked too and
// stays flagged by the anchor scan, deliberately: `&` starts from no anchor.
function walkStyleRules(window, visit) {
  const walk = (rules) => {
    for (const rule of Array.from(rules)) {
      if (typeof rule.selectorText === "string" && rule.style) { visit(rule); }
      if (rule.cssRules) { walk(rule.cssRules); }
    }
  };
  for (const sheet of Array.from(window.document.styleSheets)) { walk(sheet.cssRules); }
}

// Splits at the top-level occurrences of any character of `seps` (outside
// (), [] and quotes), dropping empty pieces.
function splitTopLevel(text, seps) {
  const out = [];
  let depth = 0;
  let quote = "";
  let cur = "";
  for (const ch of text) {
    if (quote) {
      cur += ch;
      if (ch === quote) { quote = ""; }
    } else if (ch === '"' || ch === "'") {
      quote = ch;
      cur += ch;
    } else if (ch === "(" || ch === "[") {
      depth++;
      cur += ch;
    } else if (ch === ")" || ch === "]") {
      depth--;
      cur += ch;
    } else if (depth === 0 && seps.includes(ch)) {
      if (cur.trim()) { out.push(cur.trim()); }
      cur = "";
    } else {
      cur += ch;
    }
  }
  if (cur.trim()) { out.push(cur.trim()); }
  return out;
}

// Expands every :is(...) / :where(...) of a selector into its alternatives,
// recursively: ":is(A, B) .x" -> ["A .x", "B .x"].
function expandIs(selector) {
  const m = /:(?:is|where)\(/.exec(selector);
  if (!m) { return [selector]; }
  let depth = 1;
  let i = m.index + m[0].length;
  const start = i;
  while (i < selector.length && depth > 0) {
    if (selector[i] === "(") { depth++; }
    if (selector[i] === ")") { depth--; }
    i++;
  }
  const inner = selector.slice(start, i - 1);
  const before = selector.slice(0, m.index);
  const after = selector.slice(i);
  return splitTopLevel(inner, ",").flatMap((alt) => expandIs(before + alt + after));
}

// Parses a complex selector into compounds: [{ comb, text }], where comb is
// the combinator written BEFORE the compound ("" for the first, " ", ">",
// "+" or "~").
function parseComplex(selector) {
  const parts = [];
  let depth = 0;
  let quote = "";
  let cur = "";
  let curComb = "";
  let gap = "";
  for (const ch of selector.trim()) {
    const sep = !quote && depth === 0 && /[\s>+~]/.test(ch);
    if (sep) {
      if (cur) {
        parts.push({ comb: curComb, text: cur });
        cur = "";
        gap = "";
      }
      if (/\s/.test(ch)) { gap = gap || " "; } else { gap = ch; }
      continue;
    }
    if (!cur) {
      curComb = parts.length ? (gap || " ") : "";
      gap = "";
    }
    cur += ch;
    if (quote) {
      if (ch === quote) { quote = ""; }
    } else if (ch === '"' || ch === "'") {
      quote = ch;
    } else if (ch === "(" || ch === "[") {
      depth++;
    } else if (ch === ")" || ch === "]") {
      depth--;
    }
  }
  if (cur) { parts.push({ comb: curComb, text: cur }); }
  return parts;
}

// Does the text name a class: a .class (ASCII or not, escapes included), or a
// [class...] attribute test (any case, any namespace)?
function namesClass(text) {
  const bare = text.replace(/"[^"]*"|'[^']*'/g, "");
  return /\.(?:[A-Za-z_\\-]|[^ -~])/.test(bare.replace(/\[[^\]]*\]/g, ""))
    || /\[\s*(?:[\w*-]*\|)?class\b/i.test(bare);
}

// Every class token a selector names (.class, and the values of [class...=...]).
function classTokens(selectorText) {
  const tokens = new Set();
  const bare = selectorText.replace(/"[^"]*"|'[^']*'/g, "").replace(/\[[^\]]*\]/g, "");
  for (const m of bare.matchAll(/\.((?:\\.|[A-Za-z0-9_-]|[^ -~])+)/g)) { tokens.add(m[1].replace(/\\(.)/g, "$1")); }
  for (const m of selectorText.matchAll(/\[\s*(?:[\w*-]*\|)?class\s*[~|^$*]?=\s*(?:"([^"]*)"|'([^']*)'|([^\]\s]+))/gi)) {
    for (const token of (m[1] || m[2] || m[3] || "").split(/\s+/)) { if (token) { tokens.add(token); } }
  }
  return Array.from(tokens);
}

function tagOf(compound) {
  const m = /^[A-Za-z][\w-]*/.exec(compound);
  return m ? m[0].toLowerCase() : "";
}

// The §12.6 chrome anchors a class-naming selector may start from, as chains
// of compounds joined by ">"; `exact` anchors must be the whole selector (the
// layout shell). After an anchor the next combinator must be a descendant
// (space) or ">": "+" and "~" would reach siblings of the container, which
// hold #okf-body. Anything else naming a class could match a <code> in
// #okf-body that wears it.
const BODY = /^body$/;
const CHROME_ANCHORS = [
  { chain: [/^#okf-tools(?![\w-])/] },
  { chain: [/^#okf-explorer(?![\w-])/] },
  { chain: [/^#okf-context(?![\w-])/] },
  { chain: [BODY, /^\.okf-palette-backdrop(?![\w-])/] },
  { chain: [BODY, /^\.okf-layout$/, /^main$/, /^\.okf-page-head(?![\w-])/] },
  // The page head again, through main's id: only so a chip rule outweighs the
  // shared .okf-chip (an id in :is() counts as one). #okf-main is the layout's
  // one <main>, and no body code can be what a page-head descendant matches.
  { chain: [/^#okf-main$/, /^\.okf-page-head(?![\w-])/] },
  { chain: [BODY, /^\.okf-layout$/, /^main$/, /^\.(?:meta|errors)(?![\w-])/] },
  { chain: [BODY, /^header\.bar(?![\w-])/] },
  { chain: [BODY, /^\.okf-skip(?![\w-])/] },
  { chain: [BODY, /^\.okf-graph-layout(?![\w-])/] },
  { chain: [BODY, /^\.topline(?![\w-])/] },
  // The layout shell and the direct children of <main>: #okf-body is one,
  // but a class-less compound cannot be worn.
  { chain: [BODY, /^\.okf-layout$/], exact: true },
  { chain: [BODY, /^\.okf-layout$/, /^main$/], exact: true },
  { chain: [BODY, /^\.okf-layout$/, /^main$/, /^\*$/], exact: true },
  // The side-panel splitter (P4): okf-resize.js makes it a direct child of the
  // layout, and the layout's two state attributes select the rest. A direct
  // child of body > .okf-layout is never body content (that lives in main).
  { chain: [BODY, /^\.okf-layout(?:\[data-okf-resiz(?:ed|ing)\])?$/, /^\.okf-splitter(?![\w-])/] },
  { chain: [BODY, /^\.okf-layout\[data-okf-resized\]$/, /^#okf-context(?![\w-])/] },
  { chain: [BODY, /^\.okf-layout\[data-okf-resizing\]$/], exact: true },
];

// An optional leading root-state compound (attributes only, never a class).
const ROOT_STATE = /^(?:html|:root)(?:\[data-okf-[\w-]+(?:=(?:"[^"]*"|[^\]]*))?\])+$/;

function isAnchored(partsIn) {
  let parts = partsIn;
  if (parts.length > 1 && ROOT_STATE.test(parts[0].text) && parts[1].comb === " ") {
    parts = parts.slice(1).map((p, i) => (i === 0 ? { comb: "", text: p.text } : p));
  }
  for (const anchor of CHROME_ANCHORS) {
    const n = anchor.chain.length;
    if (parts.length < n) { continue; }
    if (!anchor.chain.every((re, i) => re.test(parts[i].text) && (i === 0 || parts[i].comb === ">"))) { continue; }
    if (anchor.exact) {
      if (parts.length === n) { return true; }
      continue;
    }
    if (parts.length === n || parts[n].comb === " " || parts[n].comb === ">") { return true; }
  }
  // An svg ancestor (the sanitizer never admits svg, so body content is never inside one).
  return parts.some((p, i) => i < parts.length - 1 && tagOf(p.text) === "svg" && (parts[i + 1].comb === " " || parts[i + 1].comb === ">"));
}

// The selectors of a rule that name a class and start from no anchor. The one
// exemption: every compound naming a class carries an explicit non-code tag
// (a.broken), so no <code> can be what it matches.
function unanchoredClassSelectors(selectorText) {
  const offenders = [];
  for (const complex of splitTopLevel(selectorText, ",")) {
    for (const alt of expandIs(complex)) {
      const parts = parseComplex(alt);
      if (!parts.some((p) => namesClass(p.text))) { continue; }
      if (isAnchored(parts)) { continue; }
      const tagged = parts.filter((p) => namesClass(p.text)).every((p) => {
        const tag = tagOf(p.text);
        return tag !== "" && tag !== "code";
      });
      if (tagged) { continue; }
      offenders.push(alt.trim());
    }
  }
  return offenders;
}

// A pseudo-element's rule styles the element it hangs on: matching is tested
// on the element, with the pseudo-element removed.
function withoutPseudoElements(selector) {
  return selector.replace(/::?(?:before|after|first-line|first-letter|marker|placeholder|selection)(?![\w-])/g, "");
}

// --- static selector analysis: end ---

// jsdom's selector engine (nwsapi) answers "no match" for a pseudo-class it
// does not know, and only sometimes throws: so the guard fails closed on its
// own, refusing any pseudo-class not in this list, each checked against jsdom 29
// (it evaluates them; none of them can match an element of a static page by
// itself except the structural ones). Add one only after checking that.
const KNOWN_PSEUDO_CLASSES = new Set(["not", "is", "where", "has", "hover", "focus", "focus-visible", "focus-within",
  "checked", "disabled", "enabled", "empty", "first-child", "last-child", "nth-child", "root", "link"]);

function unknownPseudoClasses(selector) {
  const bare = selector.replace(/"[^"]*"|'[^']*'/g, "").replace(/\[[^\]]*\]/g, "");
  return Array.from(bare.matchAll(/(?<!:):([\w-]+)/g), (m) => m[1]).filter((name) => !KNOWN_PSEUDO_CLASSES.has(name));
}

function declaredProperties(window) {
  const props = new Set(CHROME_PROPS);
  walkStyleRules(window, (rule) => {
    for (let i = 0; i < rule.style.length; i++) {
      if (!rule.style[i].startsWith("--")) { props.add(rule.style[i]); }
    }
  });
  return Array.from(props);
}

function chromeClassPages() {
  return fs.readdirSync(SITE).filter((name) => /(^|-)chrome-classes\.html$/.test(name)).sort();
}

// Smoke check, not proof: it reads selector text, so it cannot see a class
// applied by script or an anchor that is itself too wide. It sits beside the
// matching differential and the computed-style comparison below, which test
// what the engine does.
checkAsync("every selector naming a class starts from a §12.6 chrome anchor (static smoke check of viewer.css, @media included)", async () => {
  const window = await openPage("index.html");
  let rules = 0;
  const bad = [];
  walkStyleRules(window, (rule) => {
    rules++;
    for (const sel of unanchoredClassSelectors(rule.selectorText)) { bad.push(sel); }
  });
  assert(rules >= 50, `the walk saw only ${rules} style rules: the stylesheet did not load or the walk lost the @media rules`);
  assert(bad.length === 0, `class-naming selector(s) not anchored to a §12.6 chrome container (body code can wear the class): ${bad.join(" | ")}`);
});

// Every tag the sanitizer admits (viewer.js ALLOWED_TAGS), as descendants of
// a worn <code>: a rule `.chip strong` must be seen even if no fixture holds
// a strong. <li> appears directly under the code as well as in lists.
const PROBE_KIDS = "<p>p</p><h1>h1</h1><h2>h2</h2><h3>h3</h3><h4>h4</h4><h5>h5</h5><h6>h6</h6>"
  + "<ul><li>l</li></ul><ol><li>l</li></ol><li>direct</li><a href=\"#\">a</a><img alt=\"i\"><code>c</code>"
  + "<pre>p</pre><pre><code>pc</code></pre><blockquote>q</blockquote>"
  + "<table><thead><tr><th>h</th></tr></thead><tbody><tr><td>d</td></tr></tbody><tfoot><tr><td>f</td></tr></tfoot></table>"
  + "<strong>s</strong><em>e</em><del>d</del><hr><br><input type=\"checkbox\" disabled>";

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
    const listed = Array.from(body.querySelectorAll("code[class]"));
    assert(listed.length >= 1 && listed[0].classList.length >= 2,
      `${rel}: the first classed <code> carries ${listed.length ? listed[0].classList.length : 0} classes: the sanitizer dropped them, this case no longer tests anything`);
    // The classes worn: every class the fixture lists AND every class any rule
    // names, so a rule whose class no fixture lists is still exercised.
    const everyClass = new Set(listed.flatMap((el) => Array.from(el.classList)));
    walkStyleRules(window, (rule) => { for (const token of classTokens(rule.selectorText)) { everyClass.add(token); } });
    // Descendant, child and sibling pairs: whatever the fixture nests, a pair
    // `.a .b`, `.a > .b`, `.a + .b` is exercised by an element wearing EVERY
    // class, nested in and beside others like it, holding every admitted tag,
    // and followed by a <p>. Each element is paired with its twin in an
    // identical tree of unclassed <code>.
    const treeHtml = (mark) => `<code${mark}><code${mark}>n</code><p>x</p><code${mark}>m</code><p>y</p>${PROBE_KIDS}</code><p>after</p>`;
    const probe = doc.createElement("div");
    probe.innerHTML = `<div>${treeHtml(" data-probe-worn")}</div><div>${treeHtml("")}</div>`;
    for (const el of probe.querySelectorAll("[data-probe-worn]")) {
      el.removeAttribute("data-probe-worn");
      el.setAttribute("class", Array.from(everyClass).join(" "));
    }
    body.appendChild(probe);
    const wornTree = Array.from(probe.children[0].querySelectorAll("*"));
    const refTree = Array.from(probe.children[1].querySelectorAll("*"));
    assert(wornTree.length === refTree.length && wornTree.length > 40, `${rel}: the probe trees differ in shape or lost their elements (${wornTree.length}/${refTree.length})`);
    const pairs = [...listed.map((el) => [el, reference]), ...wornTree.map((el, i) => [el, refTree[i]])];
    const label = (el) => `<${el.tagName.toLowerCase()}${el.hasAttribute("class") ? ` class="${el.getAttribute("class").slice(0, 80)}..."` : ""}>`;
    const props = declaredProperties(window);
    const assertInert = (when) => {
      for (const el of body.querySelectorAll("*")) {
        const style = window.getComputedStyle(el);
        const where = `${when}: ${label(el)}`;
        assert(style.position !== "fixed" && style.position !== "absolute", `${where} inside #okf-body is position: ${style.position}`);
        assert(!/rect\(/.test(style.clip) && style.width !== "1px" && style.height !== "1px", `${where} inside #okf-body is clipped or 1px (screen-reader-only styling)`);
        assert(style.display !== "none" && style.visibility !== "hidden", `${where} inside #okf-body is hidden`);
      }
      // Layer 2. A selector the engine cannot evaluate fails the case, never
      // passes it (nested `&` rules land here too: conservative on purpose).
      const matches = (el, sel) => {
        const unknown = unknownPseudoClasses(sel);
        if (unknown.length) {
          throw new Error(`${when}: the selector "${sel}" uses :${unknown.join(", :")}, which the guard's engine is not known to evaluate (KNOWN_PSEUDO_CLASSES): the guard fails closed`);
        }
        try {
          return el.matches(sel);
        } catch (err) {
          throw new Error(`${when}: cannot evaluate the selector "${sel}" (${err.message}): the guard fails closed`);
        }
      };
      const inPairs = new Set(pairs.flat());
      walkStyleRules(window, (rule) => {
        for (const complex of splitTopLevel(rule.selectorText, ",")) {
          if (!namesClass(complex)) { continue; }
          const sel = withoutPseudoElements(complex);
          for (const [el, twin] of pairs) {
            assert(matches(el, sel) === matches(twin, sel),
              `${when}: the rule "${rule.selectorText}" reaches ${label(el)} inside #okf-body, which its unclassed twin does not match`);
          }
          // The rest of the body holds nothing the sanitizer let wear a class,
          // except a class a script put on a non-code element (a.broken).
          for (const el of body.querySelectorAll("*")) {
            if (inPairs.has(el) || probe.contains(el) || (el.hasAttribute("class") && el.tagName !== "CODE")) { continue; }
            assert(!matches(el, sel), `${when}: the rule "${rule.selectorText}" reaches ${label(el)} inside #okf-body`);
          }
        }
      });
      // Layer 3. jsdom neither expands shorthands nor resolves var() in them,
      // so both the shorthands and their longhands are compared, as declared.
      for (const [el, twin] of pairs) {
        const style = window.getComputedStyle(el);
        const expected = window.getComputedStyle(twin);
        for (const prop of props) {
          assert(style.getPropertyValue(prop) === expected.getPropertyValue(prop),
            `${when}: ${label(el)} ${prop}: ${style.getPropertyValue(prop)} (the unclassed counterpart has ${expected.getPropertyValue(prop)})`);
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
// cannot match would pass the cases above by styling nothing at all. One probe
// per P1 component; each P1.1 task adds its own probe case under its marker.
checkAsync("the real P1 chrome keeps its anchored styles", async () => {
  const window = await openPage("chrome-classes.html");
  const doc = window.document;
  const chrome = [
    [doc.querySelector("body > .okf-palette-backdrop"), "position", "fixed"],
    [doc.querySelector("#okf-explorer .okf-tree-toggle .okf-sr"), "position", "absolute"],
    [doc.querySelector("#okf-explorer .okf-flag-trust"), "width", "10px"],
    [doc.querySelector("#okf-tools .okf-tool"), "height", "34px"],
    // The old header and context-title rules, anchored by P1.1.
    [doc.querySelector("body > .topline"), "height", "6px"],
    [doc.querySelector("body > header.bar > .bar-in"), "display", "flex"],
    [doc.querySelector("body > header.bar .wordmark"), "font-size", "20px"],
    // The count line (C8) now lives on the index page only: a concept page's
    // meta line became the page head (Task 12).
    [(await openPage("index.html")).document.querySelector("body > .okf-layout > main > .meta"), "font-size", "13px"],
  ];
  for (const [el, prop, value] of chrome) {
    assert(el, `a chrome element this case needs is missing (${prop}: ${value})`);
    const got = (el.ownerDocument.defaultView).getComputedStyle(el).getPropertyValue(prop);
    assert(got === value, `the real <${el.tagName.toLowerCase()} class="${el.getAttribute("class")}"> lost its ${prop}: ${value} (got ${got})`);
  }
});

// Drops each "@media ... {" header and its closing brace, keeping the rules
// inside. Comments go first (they may hold braces or the word @media).
function unwrapMedia(css) {
  css = css.replace(/[/][*][^]*?[*][/]/g, "");
  let out = "";
  const stack = [];
  for (let i = 0; i < css.length; i++) {
    if (css.startsWith("@media", i)) {
      stack.push(true);
      i = css.indexOf("{", i);
    } else if (css[i] === "{") {
      stack.push(false);
      out += "{";
    } else if (css[i] === "}") {
      if (!stack.pop()) { out += "}"; }
    } else {
      out += css[i];
    }
  }
  return out;
}

checkAsync("palette: Shift+Tab also stays inside the modal", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  const close = doc.querySelector(".okf-palette-close");
  assert(doc.activeElement === input, "focus is not in the field on open");
  const back = key(window, input, { key: "Tab", shiftKey: true });
  assert(back.defaultPrevented && doc.activeElement === close, `Shift+Tab from the field left it to ${doc.activeElement.tagName} (prevented: ${back.defaultPrevented})`);
  const back2 = key(window, close, { key: "Tab", shiftKey: true });
  assert(back2.defaultPrevented && doc.activeElement === input, `Shift+Tab from Close left it to ${doc.activeElement.tagName} (prevented: ${back2.defaultPrevented})`);
});

checkAsync("palette: Ctrl+K or '/' with any other modifier stays the browser's", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const backdrop = doc.querySelector(".okf-palette-backdrop");
  const inits = [
    { key: "k", ctrlKey: true, metaKey: true },
    { key: "k", ctrlKey: true, shiftKey: true },
    { key: "K", ctrlKey: true, shiftKey: true },
    { key: "/", ctrlKey: true },
    { key: "/", altKey: true },
    { key: "/", metaKey: true },
  ];
  for (const init of inits) {
    const event = key(window, doc.body, init);
    assert(backdrop.hidden, `${JSON.stringify(init)} opened the palette`);
    assert(!event.defaultPrevented, `${JSON.stringify(init)} was prevented although the palette did not take it`);
  }
});

checkAsync("palette: Ctrl+K while it is open is swallowed and keeps it open, focus in the field", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const backdrop = doc.querySelector(".okf-palette-backdrop");
  key(window, doc.body, { key: "/" });
  const input = doc.getElementById("okf-palette-input");
  const close = doc.querySelector(".okf-palette-close");
  assert(!backdrop.hidden, "this case needs the palette open");
  for (const from of [input, close]) {
    from.focus();
    const event = key(window, from, { key: "k", ctrlKey: true });
    assert(event.defaultPrevented, `Ctrl+K on the open palette (focus on ${from.tagName}) fell through to the browser`);
    assert(!backdrop.hidden, "Ctrl+K closed the palette");
    assert(doc.activeElement === input, `after Ctrl+K focus is on ${doc.activeElement.tagName}, not the field`);
  }
});

checkAsync("palette: Enter on the Close button closes without navigating", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const backdrop = doc.querySelector(".okf-palette-backdrop");
  doc.querySelector(".okf-palette-open").click();
  type(window, doc.getElementById("okf-palette-input"), "o");
  assert(paletteOptions(window).length > 0, "this case needs an active option Enter could open");
  let navigated = 0;
  doc.addEventListener("okf:navigate", (e) => { navigated++; e.preventDefault(); });
  const close = doc.querySelector(".okf-palette-close");
  close.focus();
  const enter = key(window, close, { key: "Enter" });
  assert(navigated === 0, "Enter on Close opened the active option");
  assert(!enter.defaultPrevented, "Enter on Close was prevented, so the button never activates");
  // jsdom does not turn Enter on a button into a click; a browser does.
  close.click();
  assert(backdrop.hidden, "Close did not close the palette");
  assert(navigated === 0 && navigations(window) === 0, "closing navigated");
});

checkAsync("palette: a modal dialog, with its result count in a status region", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const dialog = doc.querySelector(".okf-palette-backdrop .okf-palette");
  assert(dialog.getAttribute("role") === "dialog", `role: ${dialog.getAttribute("role")}`);
  assert(dialog.getAttribute("aria-modal") === "true", `aria-modal: ${dialog.getAttribute("aria-modal")}`);
  const title = doc.getElementById(dialog.getAttribute("aria-labelledby"));
  assert(title && title.textContent.trim() !== "" && dialog.contains(title), "the dialog has no name");
  const status = doc.getElementById("okf-palette-status");
  assert(status && status.getAttribute("role") === "status" && dialog.contains(status), `status role: ${status && status.getAttribute("role")}`);
});

checkAsync("palette: reopening starts from an empty query", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const opener = doc.querySelector(".okf-palette-open");
  const input = doc.getElementById("okf-palette-input");
  opener.click();
  type(window, input, "foo");
  assert(paletteOptions(window).length > 0, "this case needs results before closing");
  key(window, input, { key: "Escape" });
  opener.click();
  assert(input.value === "", `the field reopened with "${input.value}"`);
  assert(paletteOptions(window).length === 0, `${paletteOptions(window).length} options listed for an empty query`);
  assert(doc.getElementById("okf-palette-status").textContent === "", "the status kept the previous count");
  assert(!input.hasAttribute("aria-activedescendant"), "an option is still active");
});

checkAsync("palette: a key another handler already prevented does not open it", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const backdrop = doc.querySelector(".okf-palette-backdrop");
  doc.body.addEventListener("keydown", (e) => e.preventDefault());
  key(window, doc.body, { key: "/" });
  assert(backdrop.hidden, "'/' opened the palette although a page handler had taken it");
  key(window, doc.body, { key: "k", ctrlKey: true });
  assert(backdrop.hidden, "Ctrl+K opened the palette although a page handler had taken it");
});

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

checkAsync("explorer: the stale badge shows staleAfterDate as written, never a date rebuilt from staleAfterMs", async () => {
  // The two fields disagree on purpose, so the case tells which one is shown.
  const concepts = [{ id: "x", title: "X", type: "Note", tags: [], path: "x.html", trust: "unverified", staleAfterMs: 0, staleAfterDate: "2026-10-06", typeIndex: 0, description: "" }];
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 2, concepts, ghosts: [], edges: [], tree: [{ name: "x", concept: 0, children: [] }], types: [{ name: "Note", count: 1, slot: 0 }] })};`;
  const window = await openPage("index.html", { override: { "assets/okf-index.js": source }, now: () => Date.UTC(2026, 9, 7) });
  const stale = treeLink(window, "x").parentElement.querySelector(".okf-flag-stale");
  assert(stale && !stale.hidden, "this case needs a shown stale badge");
  assert(stale.getAttribute("title") === "stale after 2026-10-06", `stale badge title: ${stale.getAttribute("title")}`);
});

checkAsync("an all-punctuation fragment focuses nothing, even beside a heading titled Section", async () => {
  // foo/bar.md has a "## Section" heading, whose generated id is okf-h-section.
  const control = await openPage("foo/bar.html", { hash: "#section" });
  assert(control.document.activeElement.id === "okf-h-section", `this case needs #section to land on the Section heading (focus: ${control.document.activeElement.id})`);
  for (const hash of ["#!!!", "#%", "#%20"]) {
    const window = await openPage("foo/bar.html", { hash });
    const focused = window.document.activeElement;
    assert(focused === window.document.body, `${hash} moved the focus to <${focused.tagName.toLowerCase()} id="${focused.id}">`);
  }
});

checkAsync("an h3 is listed in the contents as a sub-entry", async () => {
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const link = doc.querySelector('#okf-toc a[href="#okf-h-details"]');
  assert(link, "the ### Details heading is not in the contents");
  assert(link.parentElement.classList.contains("okf-toc-sub"), `the h3 entry has class "${link.parentElement.className}", expected okf-toc-sub`);
  const h2 = doc.querySelector('#okf-toc a[href="#okf-h-section"]');
  assert(h2 && !h2.parentElement.classList.contains("okf-toc-sub"), "an h2 entry is marked as a sub-entry");
  const ids = Array.from(doc.querySelectorAll("#okf-body h2, #okf-body h3"), (h) => h.id);
  assert(JSON.stringify(ids) === JSON.stringify(["okf-h-usage", "okf-h-section", "okf-h-details"]), `heading ids: ${ids}`);
});

checkAsync("long unbroken titles and ids may wrap in the palette and the contents", async () => {
  // jsdom does no layout: this checks the declarations only. That nothing
  // overflows at 390 px is checked by hand (ACCEPTANCE.md).
  const window = await openPage("foo.html");
  const doc = window.document;
  key(window, doc.body, { key: "/" });
  type(window, doc.getElementById("okf-palette-input"), "foo");
  const option = paletteOptions(window)[0];
  assert(option, "this case needs a palette option");
  // The title wraps only a word longer than its line (break-word): anywhere
  // would lower its min-content size and let the row break a short title
  // mid-word while the id kept its room (recette R4). The id may break
  // anywhere.
  const targets = [
    [option.querySelector(".okf-palette-title"), "break-word"],
    [option.querySelector(".okf-palette-id"), "anywhere"],
    [doc.querySelector("#okf-toc a"), "anywhere"],
  ];
  for (const [el, wrap] of targets) {
    assert(el, "an element this case needs is missing");
    const style = window.getComputedStyle(el);
    const where = `<${el.tagName.toLowerCase()} class="${el.getAttribute("class") || ""}">`;
    assert(style.getPropertyValue("overflow-wrap") === wrap, `${where} overflow-wrap: ${style.getPropertyValue("overflow-wrap")}, expected ${wrap}`);
    assert(style.getPropertyValue("min-width") === "0px" || style.getPropertyValue("min-width") === "0", `${where} min-width: ${style.getPropertyValue("min-width")}`);
  }
  const idStyle = window.getComputedStyle(option.querySelector(".okf-palette-id"));
  const id = idStyle.getPropertyValue("max-width");
  assert(id === "100%", `the palette id is not bounded by the row (max-width: ${id})`);
  // The option's text wraps: the id shares the title's line only when both fit
  // whole, else it goes under the title. No cap or shrink factor on the id can
  // promise that (a title just under its line still broke when the id's cap
  // bound it). P1.1 put the glyph beside that text, so the rule moved to it.
  const text = option.querySelector(".okf-palette-text");
  assert(text, "the palette option lost its text block");
  const wrap = window.getComputedStyle(text).getPropertyValue("flex-wrap");
  assert(wrap === "wrap", `the palette option's text does not wrap its id under a title that leaves it no room (flex-wrap: ${wrap})`);
});

checkAsync("nothing long and unbroken widens the page: panels and tree entries shrink, page text wraps", async () => {
  // Declarations only (jsdom does no layout); the recette measures the page
  // at 390 px (R1, C10). A flex item's automatic minimum is its min-content
  // width: without min-width: 0, a long nowrap tree entry widens the stacked
  // explorer (and the context panel), so those two panels never shrink to the
  // phone's width. The tree entry itself needs no min-width: it has
  // overflow: hidden, which already makes its automatic minimum size 0, so its
  // ellipsis applies; that overflow is what is asserted for it. In the page itself a
  // dotted member name or a path in inline code wraps (break-word), and a
  // frontmatter value may break anywhere, since a table cell is as wide as
  // its min-content.
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const chipDoc = (await openPage("p11-page.html")).document;
  const expected = [
    [doc.getElementById("okf-explorer"), "min-width", "0px"],
    [doc.getElementById("okf-context"), "min-width", "0px"],
    [doc.querySelector("#okf-explorer .okf-tree-link"), "overflow", "hidden"],
    [doc.querySelector("main"), "overflow-wrap", "break-word"],
    [doc.querySelector("#okf-body p"), "overflow-wrap", "break-word"],
    [doc.querySelector("body > .okf-layout > main > .okf-page-head .okf-fm-value"), "overflow-wrap", "anywhere"],
    // The page head's chips carry bundle text of any length (type, status,
    // verifier, a date as written): they wrap inside the column.
    ...["max-width:100%", "white-space:normal", "overflow-wrap:anywhere", "height:auto"].map((d) => {
      const [prop, value] = d.split(":");
      return [chipDoc.querySelector("body > .okf-layout > main > .okf-page-head .okf-chip-status"), prop, value];
    }),
  ];
  for (const [el, prop, value] of expected) {
    assert(el, `an element this case needs is missing (${prop}: ${value})`);
    const got = el.ownerDocument.defaultView.getComputedStyle(el).getPropertyValue(prop);
    assert(got === value || (value === "0px" && got === "0"), `<${el.tagName.toLowerCase()} id="${el.id}" class="${el.getAttribute("class") || ""}"> ${prop}: ${got}, expected ${value}`);
  }
});

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

// === P1.1 cases: one block per task; a task writes only under its own line ===
// --- Task 3: CSS foundation ---

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
    "--stale": "#b4540a", "--okf-type-0": "#1a3fd6", "--okf-type-1": "#1e7d32", "--okf-type-2": "#b4540a",
    "--okf-type-3": "#6a6a72", "--okf-type-4": "#0b6e69", "--okf-type-5": "#6a6a72",
    "--backdrop": "rgba(16, 16, 20, .34)", "--shadow": "0 18px 50px rgba(16, 16, 20, .28)",
  };
  const dark = {
    "--white": "#101014", "--ink": "#f2f2f5", "--blue": "#8fa5f5", "--blue-hover": "#b7c5f8", "--blue-soft": "#1a1a22",
    "--gray": "#9a9aa2", "--hair": "#2a2a33", "--red": "#ef6b5e", "--ghost": "#ef6b5e", "--edge": "#8a8a94",
    "--stale": "#e08a3e", "--okf-type-0": "#8fa5f5", "--okf-type-1": "#5dc26b", "--okf-type-2": "#e08a3e",
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
  style.textContent = mediaWhere(fs.readFileSync(path.join(SITE, "assets", "viewer.css"), "utf8"), (cond) => cond === "(min-width: 1100px)");
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
    return { chipHeight: css(chip, "height"), chipMinHeight: css(chip, "min-height"), chipSize: css(chip, "font-size"), titleSize: css(title, "font-size"), titleCase: css(title, "text-transform"), rowSize: css(row, "font-size") };
  };
  for (const container of containers) {
    assert(container, "a chrome container this case needs is missing");
    const got = probe(container);
    const where = container.id || container.className;
    // The page head's chips grow with their text (Task 12): auto height, 26 px minimum.
    const sized = where === "okf-page-head" ? got.chipHeight === "auto" && got.chipMinHeight === "26px" : got.chipHeight === "26px";
    assert(sized && got.chipSize === "12px", `${where}: .okf-chip height ${got.chipHeight}, min-height ${got.chipMinHeight}, font-size ${got.chipSize}`);
    assert(got.titleSize === "11px" && got.titleCase === "uppercase", `${where}: .okf-section-title font-size ${got.titleSize}, text-transform ${got.titleCase}`);
    assert(got.rowSize === "13.5px", `${where}: .okf-row font-size ${got.rowSize}`);
  }
  const inBody = probe(doc.getElementById("okf-body"));
  assert(inBody.chipHeight !== "26px" && inBody.titleSize !== "11px" && inBody.rowSize !== "13.5px",
    `the shared components styled body content: ${JSON.stringify(inBody)}`);
});

// --- Task 3 fix round: hover on a.broken, shape mapping, stacked order, content rules, [hidden] ---

checkAsync("a broken link in the body keeps its colour on hover: no :hover colour rule reaches a.broken", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const link = doc.createElement("a");
  link.className = "broken";
  doc.getElementById("okf-body").appendChild(link);
  // jsdom applies no :hover, so ask the stylesheet instead: every rule with a
  // :hover colour whose selector, once stripped of :hover, matches a.broken
  // must itself re-state --red. (Source order is no defence: the id in
  // `#okf-body a:hover` out-specifies `a.broken`.)
  const reaching = [];
  walkStyleRules(window, (rule) => {
    for (const sel of splitTopLevel(rule.selectorText, ",")) {
      if (!sel.includes(":hover") || !rule.style.getPropertyValue("color")) { continue; }
      let matches = false;
      try { matches = link.matches(sel.replace(/:hover/g, "")); } catch { matches = false; }
      if (matches && rule.style.getPropertyValue("color").replace(/\s/g, "") !== "var(--red)") {
        reaching.push(`${sel} { color: ${rule.style.getPropertyValue("color")} }`);
      }
    }
  });
  assert(reaching.length === 0, `a hover rule recolours a broken link: ${reaching.join(" | ")}`);
  // And the body link rule itself still exists (the fix must not delete it).
  const live = doc.createElement("a");
  live.setAttribute("href", "x.html");
  doc.getElementById("okf-body").appendChild(live);
  let liveHover = false;
  walkStyleRules(window, (rule) => {
    for (const sel of splitTopLevel(rule.selectorText, ",")) {
      if (sel.includes(":hover") && rule.style.getPropertyValue("color") && live.matches(sel.replace(/:hover/g, ""))) { liveHover = true; }
    }
  });
  assert(liveHover, "no :hover colour rule reaches an ordinary body link any more");
});

checkAsync("shape classes map to their §11.0 tokens (all six shapes and the marks)", async () => {
  const window = await openPage("index.html");
  const want = {
    "svg .okf-shape-0": ["fill", "var(--okf-type-0)"], "svg .okf-shape-1": ["fill", "var(--okf-type-1)"],
    "svg .okf-shape-2": ["fill", "var(--okf-type-2)"], "svg .okf-shape-3": ["fill", "var(--okf-type-3)"],
    "svg .okf-shape-4": ["stroke", "var(--okf-type-4)"], "svg .okf-shape-5": ["stroke", "var(--okf-type-5)"],
    "svg .okf-trust-human": ["fill", "var(--blue)"], "svg .okf-trust-machine": ["stroke", "var(--blue)"],
    "svg .okf-stale-mark": ["fill", "var(--stale)"], "svg .okf-ghost-mark": ["stroke", "var(--ghost)"],
  };
  const got = new Map();
  walkStyleRules(window, (rule) => { got.set(rule.selectorText.replace(/\s+/g, " ").trim(), rule.style); });
  for (const [selector, [prop, value]] of Object.entries(want)) {
    assert(got.has(selector), `viewer.css has no rule for ${selector}`);
    const actual = got.get(selector).getPropertyValue(prop).replace(/\s/g, "");
    assert(actual === value, `${selector}: ${prop} is ${actual || "unset"}, expected ${value}`);
  }
  for (const k of [4, 5]) {
    assert(got.get(`svg .okf-shape-${k}`).getPropertyValue("fill") === "none", `svg .okf-shape-${k} is not unfilled`);
  }
});

checkAsync("below 1100 px the explorer is ordered after the page and its context; the two queries leave no gap", async () => {
  const raw = fs.readFileSync(path.join(SITE, "assets", "viewer.css"), "utf8").replace(/[/][*][^]*?[*][/]/g, "");
  const conds = Array.from(raw.matchAll(/@media([^{]*)\{/g), (m) => m[1].trim());
  // (min-width: 1100px) and its exact complement: a fractional width such as
  // 1099.333 (Firefox, zoom) matches neither of a min 1100 / max 1099 pair.
  assert(conds.includes("(min-width: 1100px)"), `no (min-width: 1100px) block among ${JSON.stringify(conds)}`);
  assert(conds.includes("not all and (min-width: 1100px)"), `the stacked layout is not the complement of (min-width: 1100px): ${JSON.stringify(conds)}`);
  assert(!conds.some((c) => /max-width:\s*1099/.test(c)), `a (max-width: 1099px) block leaves a gap at 1099.5px: ${JSON.stringify(conds)}`);
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const style = doc.createElement("style");
  style.textContent = mediaWhere(fs.readFileSync(path.join(SITE, "assets", "viewer.css"), "utf8"), (cond) => cond === "not all and (min-width: 1100px)");
  doc.head.appendChild(style);
  const order = (el) => window.getComputedStyle(el).getPropertyValue("order");
  assert(order(doc.getElementById("okf-explorer")) === "1", `stacked #okf-explorer order: ${order(doc.getElementById("okf-explorer"))}`);
  assert(order(doc.querySelector("body > .okf-layout > main")) === "0" && order(doc.getElementById("okf-context")) === "0",
    `stacked main/context order: ${order(doc.querySelector("body > .okf-layout > main"))}/${order(doc.getElementById("okf-context"))}`);
});

checkAsync("#okf-body content rules (C7): paragraph margin 20, h2 keeps its own tight leading", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const body = doc.getElementById("okf-body");
  const p = doc.createElement("p");
  const h2 = doc.createElement("h2");
  body.appendChild(p);
  body.appendChild(h2);
  const css = (el, prop) => window.getComputedStyle(el).getPropertyValue(prop);
  assert(css(p, "margin-bottom") === "20px", `#okf-body p margin-bottom: ${css(p, "margin-bottom")}`);
  assert(css(h2, "line-height") === "1.25", `#okf-body h2 line-height: ${css(h2, "line-height")} (it would inherit the body line-height 1.65)`);
});

checkAsync("the hidden attribute hides every shared component, though each has an author display", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const main = doc.querySelector("body > .okf-layout > main");
  const head = doc.createElement("div"); head.className = "okf-page-head"; main.insertBefore(head, main.firstChild);
  const graph = doc.createElement("div"); graph.className = "okf-graph-layout"; doc.body.appendChild(graph);
  const containers = [doc.getElementById("okf-explorer"), doc.getElementById("okf-context"), head, graph, doc.querySelector("body > .okf-palette-backdrop")];
  const SVG = "http://www.w3.org/2000/svg";
  for (const container of containers) {
    assert(container, "a chrome container this case needs is missing");
    const where = container.id || container.className;
    const ul = doc.createElement("ul"); ul.className = "okf-legend";
    const li = doc.createElement("li"); ul.appendChild(li);
    const svg = doc.createElementNS(SVG, "svg"); svg.setAttribute("class", "okf-glyph");
    const plain = [["span", "okf-chip"], ["a", "okf-row"], ["span", "okf-glyph-blank"]].map(([tag, cls]) => {
      const el = doc.createElement(tag); el.className = cls; return el;
    });
    for (const el of [...plain, svg, ul]) { container.appendChild(el); }
    for (const el of [...plain, svg, li]) {
      el.setAttribute("hidden", "");
      const display = window.getComputedStyle(el).getPropertyValue("display");
      assert(display === "none", `${where}: hidden <${el.localName} class="${el.getAttribute("class")}"> is display: ${display}`);
    }
  }
});

// --- Task 4: index v2 ---
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

// --- Task 6: OkfShapes (control 10) ---

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
    circle: ["circle class=okf-shape-0 cx=100 cy=100 r=7"],
    square: ["rect class=okf-shape-1 height=16 width=16 x=92 y=92"],
    diamond: ["path class=okf-shape-2 d=M100 91 L109 100 L100 109 L91 100 Z"],
    triangle: ["path class=okf-shape-3 d=M100 92.25 L107.75 107.75 L92.25 107.75 Z"],
    ring: ["circle class=okf-shape-4 cx=100 cy=100 r=6 stroke-width=2"],
    other: ["rect class=okf-shape-5 height=12.5 stroke-width=1.5 width=12.5 x=93.75 y=93.75"],
    ghost: ["circle class=okf-ghost-mark cx=100 cy=100 r=7 stroke-dasharray=2 2 stroke-width=1.2"],
  },
  localCenter: {
    circle: ["rect class=okf-node-ring height=26 stroke-width=2 width=26 x=87 y=87", "circle class=okf-shape-0 cx=100 cy=100 r=9"],
    square: ["rect class=okf-node-ring height=27.5 stroke-width=2 width=27.5 x=86.25 y=86.25", "rect class=okf-shape-1 height=19.5 width=19.5 x=90.25 y=90.25"],
    diamond: ["rect class=okf-node-ring height=30 stroke-width=2 width=30 x=85 y=85", "path class=okf-shape-2 d=M100 89 L111 100 L100 111 L89 100 Z"],
    triangle: ["rect class=okf-node-ring height=25 stroke-width=2 width=25 x=87.5 y=87.5", "path class=okf-shape-3 d=M100 91.5 L108.5 108.5 L91.5 108.5 Z"],
    ring: ["rect class=okf-node-ring height=26 stroke-width=2 width=26 x=87 y=87", "circle class=okf-shape-4 cx=100 cy=100 r=8 stroke-width=2"],
    other: ["rect class=okf-node-ring height=26 stroke-width=2 width=26 x=87 y=87", "rect class=okf-shape-5 height=16.5 stroke-width=1.5 width=16.5 x=91.75 y=91.75"],
  },
  graph: {
    circle: ["rect class=okf-node-focus height=34 stroke-dasharray=3 3 stroke-width=2 width=34 x=83 y=83", "rect class=okf-node-ring height=26 stroke-width=2.4 width=26 x=87 y=87", "circle class=okf-shape-0 cx=100 cy=100 r=9"],
    square: ["rect class=okf-node-focus height=37 stroke-dasharray=3 3 stroke-width=2 width=37 x=81.5 y=81.5", "rect class=okf-node-ring height=29 stroke-width=2.4 width=29 x=85.5 y=85.5", "rect class=okf-shape-1 height=21 width=21 x=89.5 y=89.5"],
    diamond: ["rect class=okf-node-focus height=38 stroke-dasharray=3 3 stroke-width=2 width=38 x=81 y=81", "rect class=okf-node-ring height=30 stroke-width=2.4 width=30 x=85 y=85", "path class=okf-shape-2 d=M100 89 L111 100 L100 111 L89 100 Z"],
    triangle: ["rect class=okf-node-focus height=33 stroke-dasharray=3 3 stroke-width=2 width=33 x=83.5 y=83.5", "rect class=okf-node-ring height=25 stroke-width=2.4 width=25 x=87.5 y=87.5", "path class=okf-shape-3 d=M100 91.5 L108.5 108.5 L91.5 108.5 Z"],
    ring: ["rect class=okf-node-focus height=34 stroke-dasharray=3 3 stroke-width=2 width=34 x=83 y=83", "rect class=okf-node-ring height=26 stroke-width=2.4 width=26 x=87 y=87", "circle class=okf-shape-4 cx=100 cy=100 r=8 stroke-width=2"],
    other: ["rect class=okf-node-focus height=34 stroke-dasharray=3 3 stroke-width=2 width=34 x=83 y=83", "rect class=okf-node-ring height=26 stroke-width=2.4 width=26 x=87 y=87", "rect class=okf-shape-5 height=16.5 stroke-width=1.5 width=16.5 x=91.75 y=91.75"],
    ghost: ["rect class=okf-node-focus height=35.2 stroke-dasharray=3 3 stroke-width=2 width=35.2 x=82.4 y=82.4", "rect class=okf-node-ring height=27.2 stroke-width=2.4 width=27.2 x=86.4 y=86.4", "circle class=okf-ghost-mark cx=100 cy=100 r=9 stroke-dasharray=2 2 stroke-width=1.2"],
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
    OkfShapes.node("ghost", 10, 10, 19.2, OkfShapes.SIZES.graph.ghost),
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

check("OkfShapes: a finite input never writes Infinity, and a stroke never makes a negative or invisible shape", () => {
  const { OkfShapes } = okfShapes();
  // The result of the arithmetic is checked, not only the inputs (spec §12.2).
  throwsTypeError(() => OkfShapes.shape("circle", 0, 0, 1e308), "size 1e308 (r would be Infinity)");
  throwsTypeError(() => OkfShapes.shape("square", 0, 0, 1.79e308), "square of 1.79e308");
  throwsTypeError(() => OkfShapes.shape("diamond", 1.79e308, 0, 1.79e308), "diamond of 1.79e308");
  throwsTypeError(() => OkfShapes.node("square", 0, 0, 1.79e308, { ring: 1, focus: 1 }), "node of 1.79e308 (outline x would be -Infinity)");
  // A stroke wider than the shape would give a negative r or side.
  throwsTypeError(() => OkfShapes.shape("ring", 0, 0, 10, { stroke: 30 }), "ring stroke 30 > size 10");
  throwsTypeError(() => OkfShapes.shape("other", 0, 0, 10, { stroke: 30 }), "other stroke 30 > size 10");
  throwsTypeError(() => OkfShapes.shape("machine", 0, 0, 10, { stroke: 10.5 }), "machine stroke > size");
  throwsTypeError(() => OkfShapes.shape("ghost", 0, 0, 10, { stroke: 11, dash: [2, 2] }), "ghost stroke > size");
  assert(OkfShapes.shape("ring", 0, 0, 10, { stroke: 10 }).getAttribute("r") === "0", "a stroke equal to the size is the degenerate r=0, not an error");
  // A stroke-only shape with no stroke would be invisible.
  for (const kind of ["ring", "other", "machine", "ghost"]) {
    throwsTypeError(() => OkfShapes.shape(kind, 0, 0, 10), `${kind} without options`);
    throwsTypeError(() => OkfShapes.shape(kind, 0, 0, 10, { stroke: 0 }), `${kind} with stroke 0`);
    throwsTypeError(() => OkfShapes.node(kind, 0, 0, 10), `node ${kind} without options`);
  }
  // Fill shapes need no stroke.
  for (const kind of ["circle", "square", "diamond", "triangle", "human", "stale"]) {
    assert(OkfShapes.shape(kind, 0, 0, 10).localName.length > 0, `${kind} without options`);
  }
});

check("OkfShapes: the exported tables are frozen all the way down, and near-miss strings are not trust tiers", () => {
  const { OkfShapes } = okfShapes();
  const unfrozen = [];
  const walk = (value, where) => {
    if (value === null || typeof value !== "object") { return; }
    if (!Object.isFrozen(value)) { unfrozen.push(where); }
    for (const [k, v] of Object.entries(value)) { walk(v, `${where}.${k}`); }
  };
  walk(OkfShapes.KINDS, "KINDS");
  walk(OkfShapes.BOXES, "BOXES");
  walk(OkfShapes.SIZES, "SIZES");
  assert(unfrozen.length === 0, `not frozen: ${unfrozen.join(", ")}`);
  // Every dash array is among what was walked.
  assert(Object.isFrozen(OkfShapes.SIZES.graph.ghost.dash) && Object.isFrozen(OkfShapes.SIZES.icon.ghost.dash), "a ghost dash array is not frozen");
  for (const near of ["not-human-reviewed", "human-reviewed ", " machine-confirmed", "Human-Reviewed", "machine-confirmed2", "", "human", "machine", null, undefined, 3]) {
    assert(OkfShapes.trustKind(near) === null, `trustKind(${JSON.stringify(near)}) is not null`);
  }
  // Every type in the other slot still gives exactly one "Other types" entry (spec E4).
  const only = OkfShapes.typeLegendEntries({ types: [{ name: "", count: 2, slot: 5 }] });
  assert(JSON.stringify(only) === JSON.stringify([{ role: "type", slot: 5, label: "Other types", count: 2 }]), `all-other: ${JSON.stringify(only)}`);
  const two = OkfShapes.typeLegendEntries({ types: [{ name: "X", count: 2, slot: 5 }, { name: "Y", count: 1, slot: 5 }] });
  assert(two.length === 1 && two[0].count === 3 && two[0].slot === 5, `two other types: ${JSON.stringify(two)}`);
  assert(OkfShapes.typeLegendEntries({ types: [] }).length === 0 && OkfShapes.typeLegendEntries(null).length === 0, "no types still draws an entry");
});
// --- Task 7: theme button ---

checkAsync("theme: a click flips the pressed state and data-theme, and a system change while nothing is forced is followed by the next click", async () => {
  let query = null;
  const window = await openPage("index.html", {
    beforeParse(w) {
      query = new w.EventTarget();
      query.matches = false;
      w.matchMedia = () => query;
    },
  });
  const doc = window.document;
  const toggle = doc.getElementById("okf-theme-toggle");
  const state = () => `${toggle.getAttribute("aria-pressed")}/${doc.documentElement.getAttribute("data-theme")}`;
  assert(state() === "false/null", `initial state ${state()}`);
  toggle.click();
  assert(state() === "true/dark", `after one click: ${state()} (expected true/dark)`);
  toggle.click();
  assert(state() === "false/light", `after two clicks: ${state()} (expected false/light)`);
  // Nothing forced: the page follows the system. A forced theme ignores the system.
  doc.documentElement.removeAttribute("data-theme");
  query.matches = true;
  query.dispatchEvent(new window.Event("change"));
  assert(state() === "true/null", `after a system change to dark: ${state()}`);
  toggle.click();
  assert(state() === "false/light", `a click while the system is dark: ${state()} (expected false/light)`);
});

checkAsync("theme: the pressed toggle is drawn in blue (border-color and stroke are var(--blue)); the resting one is not", async () => {
  // jsdom resolves neither var() nor the border shorthand in getComputedStyle,
  // so the rules are read from the page's stylesheet (CSSOM) instead: the
  // declarations of the toggle's own rules (selectors anchored on
  // #okf-theme-toggle) that match the element in its current state, in source
  // order. No generic .okf-tool[aria-pressed] rule colours a pressed tool any
  // more (Task 11 dropped P1's), and the header's #okf-tools .okf-tool rule
  // loses to the toggle's id-anchored border shorthand, so only the toggle's
  // own rules decide. The tokens' colours are P1's :root.
  const window = await openPage("index.html");
  const doc = window.document;
  const toggle = doc.getElementById("okf-theme-toggle");
  const icon = toggle.querySelector(".okf-theme-icon");
  function declared(el, property) {
    let value = null;
    for (const sheet of Array.from(doc.styleSheets)) {
      for (const rule of Array.from(sheet.cssRules)) {
        if (rule.selectorText && rule.selectorText.includes("#okf-theme-toggle") && el.matches(rule.selectorText) && rule.style.getPropertyValue(property)) {
          value = rule.style.getPropertyValue(property);
        }
      }
    }
    return value;
  }
  assert(toggle.getAttribute("aria-pressed") === "false", "this case needs a light system preference at rest");
  assert(declared(toggle, "border-color") !== "var(--blue)" && declared(icon, "stroke") === "var(--ink)",
    `at rest: border-color ${declared(toggle, "border-color")}, stroke ${declared(icon, "stroke")} (expected not blue, ink)`);
  toggle.click();
  assert(toggle.getAttribute("aria-pressed") === "true", "the click did not press the toggle");
  assert(declared(toggle, "border-color") === "var(--blue)", `pressed border-color: ${declared(toggle, "border-color")}`);
  assert(declared(icon, "stroke") === "var(--blue)", `pressed stroke: ${declared(icon, "stroke")}`);
});

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

// --- Task 8: contents, current section ---

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
  // "Strictly above": a heading whose top is exactly on the line is not yet current.
  tops.set("Usage", -800);
  tops.set("Section", line - 1);
  tops.set("Details", line);
  window.dispatchEvent(new window.Event("resize"));
  await new Promise((resolve) => window.requestAnimationFrame(resolve));
  assert(JSON.stringify(current()) === JSON.stringify(["#okf-h-section"]), `a heading exactly on the line: ${JSON.stringify(current())} (expected the one above it)`);
});

// X4 end of page: a short tail can never lift the last heading to the top
// quarter, so at the bottom of a scrollable page the last entry is current.
// jsdom does no layout: the probe supplies the rects, scrollHeight and scrollY.
checkAsync("contents: at the bottom of a scrollable page the last entry is current, and a late load recomputes", async () => {
  const tops = new Map();
  const scroll = { y: 0, height: 0 };
  const window = await openPage("foo/bar.html", {
    beforeParse(w) {
      const real = w.Element.prototype.getBoundingClientRect;
      w.Element.prototype.getBoundingClientRect = function () {
        if (/^H[23]$/.test(this.tagName) && this.closest("#okf-body")) {
          const top = tops.has(this.textContent) ? tops.get(this.textContent) : 1000;
          return { top, bottom: top + 20, left: 0, right: 0, width: 0, height: 20, x: 0, y: top };
        }
        return real.call(this);
      };
      Object.defineProperty(w.Element.prototype, "scrollHeight", {
        configurable: true,
        get() { return this === this.ownerDocument.documentElement ? scroll.height : 0; },
      });
      Object.defineProperty(w, "scrollY", { configurable: true, get() { return scroll.y; } });
    },
  });
  const doc = window.document;
  const current = () => Array.from(doc.querySelectorAll('#okf-toc a[aria-current="location"]'), (a) => a.getAttribute("href"));
  const frame = () => new Promise((resolve) => window.requestAnimationFrame(resolve));
  assert(JSON.stringify(current()) === JSON.stringify(["#okf-h-usage"]), `on load: ${JSON.stringify(current())}`);
  // Section is the last heading above the line; Details (the last entry) is still below it.
  tops.set("Usage", -400);
  tops.set("Section", 10);
  tops.set("Details", window.innerHeight - 50);
  // A page that fits the window has no end to reach.
  scroll.height = window.innerHeight;
  window.dispatchEvent(new window.Event("scroll"));
  await frame();
  assert(JSON.stringify(current()) === JSON.stringify(["#okf-h-section"]), `a page that fits the window: ${JSON.stringify(current())}`);
  // Scrollable, one pixel short of the end: not the end yet.
  scroll.height = window.innerHeight + 500;
  scroll.y = 500 - 2;
  window.dispatchEvent(new window.Event("scroll"));
  await frame();
  assert(JSON.stringify(current()) === JSON.stringify(["#okf-h-section"]), `two pixels short of the end: ${JSON.stringify(current())}`);
  // At the end.
  scroll.y = 500;
  window.dispatchEvent(new window.Event("scroll"));
  await frame();
  assert(JSON.stringify(current()) === JSON.stringify(["#okf-h-details"]), `at the end of the page: ${JSON.stringify(current())}`);
  // Scrolling back up leaves the end.
  scroll.y = 0;
  window.dispatchEvent(new window.Event("scroll"));
  await frame();
  assert(JSON.stringify(current()) === JSON.stringify(["#okf-h-section"]), `back up: ${JSON.stringify(current())}`);
  // A late font or image moves a heading without a scroll or a resize.
  tops.set("Section", -100);
  tops.set("Details", 20);
  window.dispatchEvent(new window.Event("load"));
  await frame();
  assert(JSON.stringify(current()) === JSON.stringify(["#okf-h-details"]), `after a load event: ${JSON.stringify(current())}`);
});

checkAsync("contents: the scroll listener is passive, only a page with entries listens, and a hostile body cannot add a current mark", async () => {
  const listened = async (rel, hook) => {
    const seen = [];
    const window = await openPage(rel, {
      beforeParse(w) {
        const add = w.addEventListener;
        w.addEventListener = function (type, fn, options) {
          seen.push({ type, options });
          return add.apply(this, arguments);
        };
        if (hook) { hook(w); }
      },
    });
    return { window, scrolls: seen.filter((x) => x.type === "scroll") };
  };
  const tops = new Map();
  const probe = (w) => {
    const real = w.Element.prototype.getBoundingClientRect;
    w.Element.prototype.getBoundingClientRect = function () {
      if (/^H[23]$/.test(this.tagName) && this.closest("#okf-body")) {
        const top = tops.has(this.textContent) ? tops.get(this.textContent) : 1000;
        return { top, bottom: top + 20, left: 0, right: 0, width: 0, height: 20, x: 0, y: top };
      }
      return real.call(this);
    };
  };
  const bare = await listened("constructor.html");
  const { window, scrolls } = await listened("foo/bar.html", probe);
  assert(scrolls.length > bare.scrolls.length, `a page with contents entries registered ${scrolls.length} scroll listeners, a page without ${bare.scrolls.length}`);
  const passive = scrolls.filter((x) => x.options && typeof x.options === "object" && x.options.passive === true);
  assert(passive.length >= scrolls.length - bare.scrolls.length, "the contents' scroll listener is not passive");
  const doc = window.document;
  const code = doc.createElement("code");
  code.setAttribute("class", "okf-toc okf-toc-sub okf-toc-current");
  code.setAttribute("aria-current", "location");
  doc.getElementById("okf-body").appendChild(code);
  // The current entry moves while the hostile node is in the body: the old
  // mark must still be cleared, whatever else carries aria-current.
  tops.set("Usage", -400);
  tops.set("Section", 10);
  window.dispatchEvent(new window.Event("resize"));
  await new Promise((resolve) => window.requestAnimationFrame(resolve));
  tops.set("Section", -400);
  tops.set("Details", 10);
  window.dispatchEvent(new window.Event("resize"));
  await new Promise((resolve) => window.requestAnimationFrame(resolve));
  const marked = doc.querySelectorAll('#okf-toc [aria-current="location"]');
  assert(marked.length === 1 && marked[0].tagName === "A" && marked[0].getAttribute("href") === "#okf-h-details", `${marked.length} marked entries in the contents after a hostile body node`);
});

// --- Task 10: fonts ---
check("fonts: every @font-face of the written stylesheet points at a file written under assets/fonts", () => {
  const css = fs.readFileSync(path.join(SITE, "assets", "viewer.css"), "utf8");
  const urls = Array.from(css.matchAll(/@font-face\s*\{[^}]*url\("([^"]+)"\)/g), (m) => m[1]);
  assert(urls.length === 7, `${urls.length} @font-face urls, expected the 7 faces of spec §11.0`);
  for (const url of urls) {
    assert(/^fonts\/[a-z0-9-]+\.woff2$/.test(url), `${url} is not a relative fonts/ url`);
    assert(fs.existsSync(path.join(SITE, "assets", url)), `${url} was not written`);
  }
});

// --- Task 11: shell and header ---

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

// jsdom lays nothing out, so the geometry (a long bundle name must not push
// the tools past the window) is pinned by the declarations it rests on, read
// from the page's stylesheet: every rule matching #okf-tools, @media blocks
// included. min-width: 0 would let the tools box shrink below its
// unshrinkable buttons, which then overflow the bar (the page scrolls
// sideways); the bundle name gives way instead (H5, min-width: 0 there).
checkAsync("header: the tools box never shrinks below its buttons; the bundle name gives way", async () => {
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const declared = (el, prop) => {
    const values = [];
    const walk = (rules, media) => {
      for (const rule of Array.from(rules)) {
        if (rule.cssRules && rule.media) { walk(rule.cssRules, rule.media.mediaText); continue; }
        if (rule.selectorText && el.matches(rule.selectorText) && rule.style.getPropertyValue(prop) !== "") {
          values.push({ media, value: rule.style.getPropertyValue(prop) });
        }
      }
    };
    for (const sheet of Array.from(doc.styleSheets)) { walk(sheet.cssRules, ""); }
    return values;
  };
  // The one-line bar (rules outside @media; the last declaration wins): either
  // fix holds, no min-width: 0 (the box keeps its min-content width) or
  // flex-shrink: 0 (flex: none); min-width: 0 on a shrinkable box fails. Under
  // 900 px the tools wrap instead (the next case).
  const wide = (el, prop) => {
    const values = declared(el, prop).filter((d) => d.media === "");
    return values.length ? values[values.length - 1].value : "";
  };
  const tools = doc.getElementById("okf-tools");
  const toolsMin = wide(tools, "min-width");
  const toolsShrink = wide(tools, "flex-shrink");
  assert(toolsShrink === "0" || !/^0(px)?$/.test(toolsMin),
    `#okf-tools may shrink below its buttons: min-width ${toolsMin || "unset"}, flex-shrink ${toolsShrink || "unset"}`);
  const name = doc.getElementById("okf-bundle-name");
  assert(/^0(px)?$/.test(wide(name, "min-width")), `#okf-bundle-name min-width: ${wide(name, "min-width") || "unset"} (it must give way)`);
});

// The narrow header is the exact complement of (min-width: 900px): a
// viewport of 899.333 px (Firefox) matches neither of a min 900 / max 899
// pair. Applied through mediaWhere, since jsdom evaluates no @media.
checkAsync("header: under 900 px the bar wraps, through the complement of (min-width: 900px)", async () => {
  const raw = fs.readFileSync(path.join(SITE, "assets", "viewer.css"), "utf8").replace(/[/][*][^]*?[*][/]/g, "");
  const conds = Array.from(raw.matchAll(/@media([^{]*)\{/g), (m) => m[1].trim());
  assert(conds.includes("not all and (min-width: 900px)"), `no "not all and (min-width: 900px)" block among ${JSON.stringify(conds)}`);
  assert(!conds.some((c) => /max-width:\s*899/.test(c)), `a (max-width: 899px) block leaves a gap at 899.333px: ${JSON.stringify(conds)}`);
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const get = (el, prop) => window.getComputedStyle(el).getPropertyValue(prop);
  const barIn = doc.querySelector("body > header.bar .bar-in");
  const tools = doc.getElementById("okf-tools");
  assert(get(barIn, "flex-wrap") !== "wrap", "the wide bar already wraps");
  const style = doc.createElement("style");
  style.textContent = mediaWhere(fs.readFileSync(path.join(SITE, "assets", "viewer.css"), "utf8"), (cond) => cond === "not all and (min-width: 900px)");
  doc.head.appendChild(style);
  assert(get(barIn, "flex-wrap") === "wrap" && get(barIn, "height") === "auto", `narrow .bar-in: flex-wrap ${get(barIn, "flex-wrap")}, height ${get(barIn, "height")}`);
  assert(get(tools, "flex-wrap") === "wrap", `narrow #okf-tools flex-wrap: ${get(tools, "flex-wrap")}`);
  // A long name must not push the counts to a third line: the wrapped line
  // breaks on flex-basis, so the name's is 0 there.
  const name = doc.getElementById("okf-bundle-name");
  assert(/^0(px|%)?$/.test(get(name, "flex-basis")), `narrow #okf-bundle-name flex-basis: ${get(name, "flex-basis")}`);
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

// --- Task 12: page head ---

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
    [head.querySelector(".okf-chips .okf-chip"), "min-height", "26px"],
    [head.querySelector(".okf-fm-key"), "width", "92px"],
    [head.querySelector(".okf-fm-value"), "overflow-wrap", "anywhere"],
    [head.querySelector(".okf-fm-struct"), "white-space", "pre-wrap"],
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

checkAsync("page head: a structured frontmatter value keeps its line breaks, in the HTML and in the cell", async () => {
  const written = fs.readFileSync(path.join(SITE, "p11-page.html"), "utf8");
  const raw = /<span class="okf-fm-value okf-fm-struct">([^<]*resource: skills\/run\.md[^<]*)<\/span>/.exec(written);
  assert(raw && raw[1].includes("\n"), `the written text node has no line break: ${raw && JSON.stringify(raw[1])}`);
  const window = await openPage("p11-page.html");
  const cell = Array.from(window.document.querySelectorAll(".okf-fm-struct")).find((c) => c.textContent.includes("resource: skills/run.md"));
  assert(cell, "the executor cell is missing");
  assert(cell.textContent.includes("\n") && cell.textContent.split("\n").length >= 2, `the cell text is one line: ${JSON.stringify(cell.textContent)}`);
  const ws = window.getComputedStyle(cell).getPropertyValue("white-space");
  assert(ws === "pre-wrap", `white-space: ${ws}`);
});

checkAsync("page head: Referenced by counts its rows, each a .okf-row naming its source", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  const title = doc.getElementById("okf-backlinks-title");
  const rows = Array.from(doc.querySelectorAll("#okf-context .okf-backlinks a.okf-row[data-okf-target]"));
  assert(title && title.textContent === `Referenced by · ${rows.length}` && rows.length >= 1, `title: ${title && title.textContent}`);
  assert(rows.some((a) => a.getAttribute("data-okf-target") === "p11-page-ref" && a.getAttribute("href") === "p11-page-ref.html"), "the referrer row is missing");
});

// --- Task 13: explorer ---

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
    assert(chip.querySelector('svg.okf-glyph[width="12"]'), "a chip glyph is not the 12 px icon (E4)");
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
  // The accessible name: the aria-label when there is one (a visually hidden
  // span would be read "Filters , 1 active"), else the visible text without
  // the aria-hidden count.
  const spoken = () => button.getAttribute("aria-label") || Array.from(button.childNodes).filter((n) => !(n.nodeType === 1 && n.getAttribute("aria-hidden") === "true")).map((n) => n.textContent).join("");
  assert(badge.getAttribute("aria-hidden") === "true" && badge.hidden && spoken() === "Filters", `at rest: badge hidden ${badge.hidden}, name "${spoken()}"`);
  const chip = doc.querySelector("#okf-explorer .okf-type-chips button.okf-chip");
  chip.click();
  assert(!badge.hidden && badge.textContent === "1" && spoken() === "Filters, 1 active", `one chip: "${badge.textContent}", "${spoken()}"`);
  assert(button.querySelector(".okf-sr") === null, "the Filters button still carries a visually hidden span in its name");
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
    // 26 px is the chip's minimum: a long type name makes it grow (final review B).
    [doc.querySelector("#okf-explorer .okf-type-chips .okf-chip"), "min-height", "26px"],
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

checkAsync("explorer: a folder counts every destination below it, not only its direct children (E9)", async () => {
  const mk = (id, title, path) => ({ id, title, type: "Note", tags: [], path, trust: "unverified", typeIndex: 0, description: "" });
  const concepts = [mk("zeta/b", "B", "zeta/b.html"), mk("zeta/b/c", "C", "zeta/b/c.html"), mk("zeta/b/d/e", "E", "zeta/b/d/e.html")];
  const tree = [{ name: "zeta", concept: -1, children: [{ name: "b", concept: 0, children: [{ name: "c", concept: 1, children: [] }, { name: "d", concept: -1, children: [{ name: "e", concept: 2, children: [] }] }] }] }];
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 2, concepts, ghosts: [], edges: [], tree, types: [{ name: "Note", count: 3, slot: 0 }] })};`;
  const window = await openPage("index.html", { override: { "assets/okf-index.js": source } });
  const count = (id) => treeLink(window, id).parentElement.querySelector(".okf-tree-count").textContent;
  const zeta = Array.from(window.document.querySelectorAll("#okf-explorer .okf-tree-folder")).find((s) => s.textContent === "zeta");
  assert(zeta.parentElement.querySelector(".okf-tree-count").textContent === "3", `zeta counts ${zeta.parentElement.querySelector(".okf-tree-count").textContent}, expected 3 (b, c and e)`);
  assert(count("zeta/b") === "2", `b counts ${count("zeta/b")}, expected 2 (c and e)`);
});

checkAsync("explorer: a folder matches the name filter only while no chip is pressed", async () => {
  const concepts = [
    { id: "a", title: "A", type: "Alpha", tags: [], path: "a.html", trust: "unverified", typeIndex: 0, description: "" },
    { id: "zeta/b", title: "B", type: "Beta", tags: [], path: "zeta/b.html", trust: "unverified", typeIndex: 1, description: "" },
  ];
  const tree = [{ name: "a", concept: 0, children: [] }, { name: "zeta", concept: -1, children: [{ name: "b", concept: 1, children: [] }] }];
  const types = [{ name: "Alpha", count: 1, slot: 0 }, { name: "Beta", count: 1, slot: 1 }];
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 2, concepts, ghosts: [], edges: [], tree, types })};`;
  const window = await openPage("index.html", { override: { "assets/okf-index.js": source } });
  const doc = window.document;
  const folder = Array.from(doc.querySelectorAll("#okf-explorer .okf-tree-folder")).find((s) => s.textContent === "zeta");
  type(window, doc.getElementById("okf-tree-filter"), "zeta");
  assert(isShown(folder) && isShown(treeLink(window, "zeta/b")), "the name filter must show the folder zeta and its match");
  doc.querySelector('#okf-explorer .okf-type-chips button[data-okf-slot="0"]').click();
  assert(!isShown(folder) && !isShown(treeLink(window, "zeta/b")), "with the Alpha chip pressed, the folder zeta matched by its name alone");
});

checkAsync("explorer: a hidden flag and a hidden Filters count are not displayed", async () => {
  const window = await openPage("foo.html");
  const doc = window.document;
  const display = (el) => window.getComputedStyle(el).getPropertyValue("display");
  const stale = doc.querySelector("#okf-explorer .okf-flag-stale");
  const count = doc.querySelector("#okf-filters-toggle .okf-filters-count");
  stale.hidden = true;
  assert(count.hidden && display(count) === "none", `the zero count shows (${display(count)})`);
  assert(display(stale) === "none", `a hidden stale flag shows (${display(stale)})`);
});

// --- Task 14: palette ---

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
  // H7: 320 at most, giving way to 160. jsdom lays nothing out, so the shrink
  // is pinned by the declarations it rests on: a basis equal to the minimum
  // and a grow factor (the opener fills what its tools box leaves it, up to
  // the cap), never a definite width of 320, which keeps the opener at 320 at
  // every window width and crushes the bundle name instead.
  const get = (el, prop) => style.getPropertyValue(prop);
  assert(get(opener, "max-width") === "320px" && get(opener, "min-width") === "160px" && get(opener, "height") === "34px",
    `opener: max-width ${get(opener, "max-width")}, min-width ${get(opener, "min-width")}, height ${get(opener, "height")}`);
  assert(get(opener, "flex-grow") === "1" && get(opener, "flex-shrink") === "1" && get(opener, "flex-basis") === "160px" && get(opener, "width") === "auto",
    `opener flex: ${get(opener, "flex-grow")} ${get(opener, "flex-shrink")} ${get(opener, "flex-basis")}, width ${get(opener, "width")}`);
  // Its label has a 0 basis, so a long label never sizes the button.
  const labelStyle = window.getComputedStyle(label);
  assert(labelStyle.getPropertyValue("flex-basis") === "0px" && labelStyle.getPropertyValue("width") === "0px", `label flex-basis ${labelStyle.getPropertyValue("flex-basis")}, width ${labelStyle.getPropertyValue("width")}`);
});

checkAsync("palette: an option whose concept has no type field announces \"(no type)\", never \"undefined\"", async () => {
  const concepts = [{ id: "bare", title: "Bare", tags: [], path: "bare.html", trust: "unverified", staleAfterMs: null, staleAfterDate: null, typeIndex: 0, description: "" }];
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 2, concepts, ghosts: [], edges: [], tree: [], types: [{ name: "", count: 1, slot: 0 }] })};`;
  const window = await openPage("index.html", { override: { "assets/okf-index.js": source } });
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  type(window, doc.getElementById("okf-palette-input"), "bare");
  const options = paletteOptions(window);
  assert(options.length === 1, `this case needs one option (${options.length})`);
  const typeName = options[0].querySelector(".okf-palette-type");
  assert(typeName && typeName.textContent === "(no type)", `hidden type: "${typeName && typeName.textContent}"`);
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

// --- Task 15: okf-page.js (control 11) ---

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

checkAsync("page: Referenced by rows whose target names an Object.prototype member resolve by own id only", async () => {
  // The renderer never emits these rows for the fixture, so write a probe page:
  // the real p11-page.html with extra rows. Each id is looked up in a Map built
  // from the index, never as a property of a plain object.
  const probe = "p11-proto-rows.html";
  const rows = ["__proto__", "constructor", "toString", "valueOf", "__proto__/a.b/c-d", "hasOwnProperty"]
    .map((id) => `<li><a class="okf-row" href="x.html" data-okf-target="${id}">${id}</a></li>`).join("\n");
  const html = fs.readFileSync(path.join(SITE, "p11-page.html"), "utf8").replace("</ul>\n</section>\n</aside>", `${rows}\n</ul>\n</section>\n</aside>`);
  assert(html.includes('data-okf-target="valueOf"'), "the probe page was not built: the backlinks markup changed");
  fs.writeFileSync(path.join(SITE, probe), html);
  const window = await openPage(probe);
  const idx = window.OKF_INDEX;
  const byId = new Map(idx.concepts.map((c, i) => [c.id, i]));
  for (const a of window.document.querySelectorAll("#okf-context .okf-backlinks a.okf-row")) {
    const target = a.getAttribute("data-okf-target");
    const pos = byId.get(target);
    const glyph = a.firstElementChild;
    if (pos === undefined) {
      assert(glyph === null, `${target}: not a concept of the index, but the row got a glyph`);
    } else {
      const t = idx.types[idx.concepts[pos].typeIndex];
      assert(glyph && glyph.localName === "svg" && glyph.querySelector(`.okf-shape-${t.slot}`), `${target}: the row has no glyph of its type`);
    }
  }
  assert(byId.has("__proto__") && byId.has("constructor") && byId.has("toString") && !byId.has("valueOf"), "the fixture's prototype-named ids changed");
});
checkAsync("page: anchored queries ignore body code that copies the page head's nested chrome structures", async () => {
  // The sanitizer strips data-okf-*, so real body code cannot carry them; this
  // probe writes them into the page itself, the worst case a selector that
  // lost its anchor or its attribute could be fooled by. The nested structures
  // below are what the flat and mis-nested code of p11-chrome-classes.md is not.
  const payload = [
    '<div id="okf-probe-body">',
    '<code class="okf-chip okf-chip-type"><code class="okf-chip-glyph" data-okf-slot="0">t</code>TYPETEXT</code>',
    '<code class="okf-chip okf-chip-trust"><code class="okf-chip-glyph" data-okf-trust="human">h</code>TRUSTTEXT</code>',
    '<code class="okf-chip okf-chip-stale"><code class="okf-chip-glyph" data-okf-stale>g</code><code class="okf-chip-text">BODYTEXT</code></code>',
    '<code class="okf-fm"><code class="okf-fm-head">H</code><code class="okf-fm-grid"><code class="okf-fm-cell">a</code><code class="okf-fm-cell" data-okf-extra>b</code></code></code>',
    '<code class="okf-backlinks"><a class="okf-row" href="p11-page-ref.html" data-okf-target="p11-page-ref">ROWTEXT</a></code>',
    "</div>",
  ].join("");
  const staleChip = /<span class="okf-chip okf-chip-stale">[\s\S]*?<\/span><\/span>\n/;
  // p11-chrome-classes has an unverified head with no stale chip; p11-page's
  // head loses its stale chip here, while its index entry keeps the deadline.
  const probes = [["p11-chrome-classes.html", null], ["p11-page.html", staleChip]];
  for (const [rel, strip] of probes) {
    let html = fs.readFileSync(path.join(SITE, rel), "utf8");
    if (strip) {
      assert(strip.test(html), `${rel}: the stale chip to strip is missing`);
      html = html.replace(strip, "");
    }
    assert(html.includes("</main>"), `${rel}: no </main> to splice the probe before`);
    const probe = `p11-probe-${rel}`;
    fs.writeFileSync(path.join(SITE, probe), html.replace("</main>", `${payload}</main>`));
    const baseline = await openPage(probe, { blocked: ["assets/okf-page.js"] });
    const expected = baseline.document.getElementById("okf-probe-body").innerHTML;
    assert(expected.includes("BODYTEXT") && expected.includes("data-okf-stale"), `${rel}: the probe payload did not reach the page`);
    let now = Date.UTC(1999, 0, 1);
    const window = await openPage(probe, { now: () => now });
    const doc = window.document;
    const box = doc.getElementById("okf-probe-body");
    const unchanged = (when) => {
      assert(box.innerHTML === expected, `${rel} ${when}: the body code was changed: ${box.innerHTML}`);
      assert(box.querySelector("svg, button") === null, `${rel} ${when}: a glyph or button reached the body code`);
      for (const el of box.querySelectorAll("*")) {
        assert(window.getComputedStyle(el).getPropertyValue("display") !== "none", `${rel} ${when}: <${el.localName} class="${el.getAttribute("class")}"> was hidden`);
      }
    };
    unchanged("after load");
    now = Date.UTC(2001, 0, 1);
    doc.dispatchEvent(new window.Event("visibilitychange"));
    unchanged("after visibilitychange past the deadline");
    const toggle = doc.querySelector("body > .okf-layout > main > .okf-page-head .okf-fm-toggle");
    assert(toggle, `${rel}: this case needs the page head's Show all`);
    toggle.click();
    unchanged("after Show all");
    assert(doc.querySelectorAll("body > .okf-layout > main > .okf-page-head .okf-fm-toggle").length === 1, `${rel}: not exactly one Show all in the page head`);
  }
});

checkAsync("page: a damaged index entry (no type, no staleAfterDate, null) is skipped, never written as text", async () => {
  const source = fs.readFileSync(path.join(SITE, "assets", "okf-index.js"), "utf8");
  const damage = `${source}\n;(function () {
    var c = window.OKF_INDEX.concepts;
    for (var i = 0; i < c.length; i++) {
      if (c[i].id === "p11-page-ref") { delete c[i].type; }
      if (c[i].id === "p11-page") { c[i].staleAfterDate = null; }
    }
  })();`;
  const window = await openPage("p11-page.html", { override: { "assets/okf-index.js": damage }, now: () => Date.UTC(2026, 9, 7) });
  const doc = window.document;
  const row = Array.from(doc.querySelectorAll("#okf-context .okf-backlinks a.okf-row")).find((a) => a.getAttribute("data-okf-target") === "p11-page-ref");
  const glyph = row && row.firstElementChild;
  assert(glyph && glyph.localName === "svg" && glyph.querySelector("title") === null, `a concept without a type: ${glyph && glyph.outerHTML}`);
  const chip = doc.querySelector("body > .okf-layout > main > .okf-page-head .okf-chip-stale");
  assert(chip.querySelector(".okf-chip-text").textContent === "stale after 2000-01-01" && !chip.hasAttribute("data-okf-stale-now") && !chip.querySelector("svg"),
    `without staleAfterDate the chip must stay as written: "${chip.textContent}"`);
  // A null entry: the explorer and the palette read every entry and are out of
  // this task's reach, so only okf-page.js and the scripts that tolerate it load.
  const withNull = `${source}\n;window.OKF_INDEX.concepts.push(null);`;
  const other = await openPage("p11-page.html", {
    override: { "assets/okf-index.js": withNull },
    blocked: ["assets/okf-explorer.js", "assets/okf-palette.js"],
    now: () => Date.UTC(2026, 9, 7),
  });
  const head = other.document.querySelector("body > .okf-layout > main > .okf-page-head");
  assert(head.querySelector(".okf-chip-stale .okf-chip-text").textContent === "stale since 2000-01-01" && head.querySelector(".okf-fm-toggle"),
    "a null entry in the index stopped okf-page.js");
});

checkAsync("page: Show all needs no okf-site.js, and the stale text flips without OkfShapes", async () => {
  const noSite = await openPage("p11-page.html", { blocked: ["assets/okf-site.js"] });
  const toggle = noSite.document.querySelector("body > .okf-layout > main > .okf-page-head .okf-fm-toggle");
  assert(toggle && toggle.textContent === "Show all" && toggle.getAttribute("type") === "button", "Show all depends on okf-site.js");
  toggle.click();
  assert(noSite.document.getElementById("okf-fm").hasAttribute("data-okf-expanded") && toggle.textContent === "Show fewer", "Show all does not work without okf-site.js");
  let now = Date.UTC(1999, 0, 1);
  const noShapes = await openPage("p11-page.html", { blocked: ["assets/okf-shapes.js"], now: () => now });
  const chip = noShapes.document.querySelector("body > .okf-layout > main > .okf-page-head .okf-chip-stale");
  const text = chip.querySelector(".okf-chip-text");
  assert(text.textContent === "stale after 2000-01-01" && !chip.hasAttribute("data-okf-stale-now"), "before the deadline without OkfShapes");
  now = Date.UTC(2001, 0, 1);
  noShapes.document.dispatchEvent(new noShapes.Event("visibilitychange"));
  assert(text.textContent === "stale since 2000-01-01" && chip.hasAttribute("data-okf-stale-now") && chip.querySelector("svg") === null,
    `the text flip needs only the index: "${text.textContent}"`);
});

checkAsync("page: the stale chip is recalculated only when the page becomes visible", async () => {
  let now = Date.UTC(1999, 0, 1);
  const window = await openPage("p11-page.html", { now: () => now });
  const doc = window.document;
  const text = doc.querySelector("body > .okf-layout > main > .okf-page-head .okf-chip-stale .okf-chip-text");
  let state = "hidden";
  Object.defineProperty(doc, "visibilityState", { configurable: true, get: () => state });
  now = Date.UTC(2001, 0, 1);
  doc.dispatchEvent(new window.Event("visibilitychange"));
  assert(text.textContent === "stale after 2000-01-01", `a hidden page was recalculated: "${text.textContent}"`);
  state = "visible";
  doc.dispatchEvent(new window.Event("visibilitychange"));
  assert(text.textContent === "stale since 2000-01-01", `a visible page was not recalculated: "${text.textContent}"`);
});

// --- Task 16: fonts fallback (only if Task 10 found a browser blocking the fonts) ---

// --- P1.1 polish (recette findings E12-foot, C6-head, J3, the body h1, rules, line-heights) ---
// jsdom lays nothing out and its getComputedStyle ignores specificity, so these
// cases read the CSSOM: every rule whose selector matches the element, ranked
// by selector specificity then source order, which is what a browser does.
// @media rules count only where `media(cond)` says so.
function specificityOf(selector) {
  let a = 0, b = 0, c = 0;
  let rest = selector.replace(/\[[^\]]*\]/g, () => { b++; return ""; });
  const take = (re, fn) => { rest = rest.replace(re, (...m) => { fn(...m); return " "; }); };
  // Functional pseudo-classes first, innermost match each time.
  for (;;) {
    const m = /:(is|not|has|where|matches)\(([^()]*)\)/.exec(rest);
    if (!m) { break; }
    if (m[1] !== "where") {
      const inner = splitTopLevel(m[2], ",").map((s) => specificityOf(s.trim().replace(/^[>+~]\s*/, "")));
      const best = inner.reduce((x, y) => (y[0] - x[0] || y[1] - x[1] || y[2] - x[2]) > 0 ? y : x, [0, 0, 0]);
      a += best[0]; b += best[1]; c += best[2];
    }
    rest = rest.slice(0, m.index) + " " + rest.slice(m.index + m[0].length);
  }
  take(/::[\w-]+/g, () => { c++; });
  take(/#[\w-]+/g, () => { a++; });
  take(/\.[\w-]+/g, () => { b++; });
  take(/:[\w-]+/g, () => { b++; });
  for (const part of rest.split(/[\s>+~]+/)) {
    if (/^[A-Za-z][\w-]*$/.test(part)) { c++; }
  }
  return [a, b, c];
}

function beats(x, y) {
  for (let i = 0; i < 3; i++) {
    if (x[i] !== y[i]) { return x[i] > y[i]; }
  }
  return true;
}

function cascadeOf(doc, el, prop, media = () => false) {
  let best = null;
  let order = 0;
  const walk = (rules) => {
    for (const rule of Array.from(rules)) {
      if (rule.cssRules && rule.media) {
        if (media(rule.media.mediaText.trim())) { walk(rule.cssRules); }
        continue;
      }
      if (!rule.selectorText) { continue; }
      const value = rule.style.getPropertyValue(prop);
      order++;
      if (value === "") { continue; }
      for (const sel of splitTopLevel(rule.selectorText, ",")) {
        let hit = false;
        try { hit = el.matches(sel.trim()); } catch (e) { hit = false; }
        if (!hit) { continue; }
        const spec = specificityOf(sel.trim());
        // Later wins a tie: the walk is in source order.
        if (!best || beats(spec, best.spec)) {
          best = { value, spec, order, selector: sel.trim() };
        }
      }
    }
  };
  for (const sheet of Array.from(doc.styleSheets)) { walk(sheet.cssRules); }
  return best;
}

checkAsync("polish: the specificity ranking this harness uses knows :is(), :where(), :has(), ids, classes and elements", async () => {
  const cases = [
    [":is(#a, .b) .c", [1, 1, 0]],
    [":where(#a, .b) .c", [0, 1, 0]],
    ["body > .x > main > .y .z", [0, 3, 2]],
    ["#okf-main > .okf-page-head .okf-fm-head .okf-section-title", [1, 3, 0]],
    ["a.okf-row:hover", [0, 2, 1]],
    ["div:not(.a):has(> #b)", [1, 1, 1]],
    ["x::before", [0, 0, 2]],
    ["[data-a] [data-b=\"c d\"] p", [0, 2, 1]],
  ];
  for (const [sel, want] of cases) {
    const got = specificityOf(sel);
    assert(got.join(",") === want.join(","), `${sel}: ${got.join(",")}, expected ${want.join(",")}`);
  }
});

checkAsync("polish: a local margin on a section title in a chrome container beats the shared default (C6 head, J3), which a plain section title keeps", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  const bottom = (el) => { const w = cascadeOf(doc, el, "margin-bottom"); return w ? w.value + "  (" + w.selector + ")" : "none"; };
  const zero = (el) => /^0(px)?\b/.test(bottom(el));
  const fmTitle = doc.getElementById("okf-fm-title");
  assert(fmTitle && fmTitle.classList.contains("okf-section-title"), "this case needs the frontmatter title");
  assert(zero(fmTitle), `the frontmatter head's title margin-bottom is ${bottom(fmTitle)}: the head grows and the title rides above "Show all" (recette C6-head)`);
  doc.querySelector(".okf-palette-open").click();
  const matches = doc.querySelector("body > .okf-palette-backdrop .okf-palette-matches");
  assert(matches, "this case needs the palette's matches line");
  assert(zero(matches), `the palette's "Matches..." line margin-bottom is ${bottom(matches)}: the list starts 8 px under it (recette J3; the mockup has none)`);
  // The default survives where nothing overrides it, and the explorer keeps its own 6.
  const plain = doc.getElementById("okf-toc-title") || doc.getElementById("okf-backlinks-title");
  assert(plain && /^8px\b/.test(bottom(plain)), `a plain section title in the context panel has margin-bottom ${plain && bottom(plain)} (expected 8px)`);
  const explorerTitle = doc.querySelector("#okf-explorer .okf-explorer-title");
  assert(explorerTitle && /^6px\b/.test(bottom(explorerTitle)), `the explorer title's margin-bottom is ${explorerTitle && bottom(explorerTitle)}, not the 6 px of E2`);
});

checkAsync("polish: from 1100 px the explorer and the context panel fit the band under the 59 px header, so a page opens with the whole legend on screen (E12)", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  const wide = (cond) => cond === "(min-width: 1100px)";
  const tokenRule = cascadeOf(doc, doc.documentElement, "--okf-header-h", wide);
  assert(tokenRule && tokenRule.value.trim() === "59px", `--okf-header-h in the wide layout: ${tokenRule && tokenRule.value}`);
  // 59 = the 6 px band + the 52 px bar + its 1 px rule, read from the same stylesheet.
  const topline = cascadeOf(doc, doc.querySelector("body > .topline"), "height", wide);
  const bar = cascadeOf(doc, doc.querySelector("body > header.bar .bar-in"), "height", wide);
  const rule = cascadeOf(doc, doc.querySelector("body > header.bar"), "border-bottom", wide);
  assert(topline && bar && rule && parseFloat(topline.value) + parseFloat(bar.value) + 1 === 59 && /^1px/.test(rule.value),
    `the header is ${topline && topline.value} + ${bar && bar.value} + ${rule && rule.value}, not the 59 px of the token`);
  for (const id of ["okf-explorer", "okf-context"]) {
    const max = cascadeOf(doc, doc.getElementById(id), "max-height", wide);
    assert(max && /^calc\(100vh - var\(--okf-header-h\)\)$/.test(max.value.trim()), `#${id} max-height from 1100 px: ${max && max.value} (a panel 100vh tall ends 59 px below the fold)`);
    assert(cascadeOf(doc, doc.getElementById(id), "max-height", () => false) === null, `#${id} has a max-height in the stacked layout`);
  }
  const layout = cascadeOf(doc, doc.querySelector("body > .okf-layout"), "min-height", wide);
  assert(layout && /^calc\(100vh - var\(--okf-header-h\)\)$/.test(layout.value.trim()), `the layout's min-height from 1100 px: ${layout && layout.value}`);
});

checkAsync("polish: from 1100 px main stretches and carries the two side rules down as shadows on the panels' borders; the stacked layout has none", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  const main = doc.querySelector("body > .okf-layout > main");
  const wide = (cond) => cond === "(min-width: 1100px)";
  const shadow = cascadeOf(doc, main, "box-shadow", wide);
  assert(shadow && /^-1px 0 0 var\(--hair\), 1px 0 0 var\(--hair\)$/.test(shadow.value.trim()), `main box-shadow from 1100 px: ${shadow && shadow.value}`);
  const stretch = cascadeOf(doc, main, "align-self", wide);
  assert(stretch && stretch.value.trim() === "stretch", `main align-self from 1100 px: ${stretch && stretch.value} (the rules would stop at the end of the text)`);
  assert(cascadeOf(doc, main, "box-shadow", () => false) === null && cascadeOf(doc, main, "align-self", () => false) === null, "the stacked layout draws side rules");
  // Where the rules fall: the explorer's right border and the context panel's left one.
  const right = cascadeOf(doc, doc.getElementById("okf-explorer"), "border-right", wide);
  const left = cascadeOf(doc, doc.getElementById("okf-context"), "border-left", wide);
  assert(right && /1px solid var\(--hair\)/.test(right.value), "the explorer has no right border to line up with");
  assert(left && /1px solid var\(--hair\)/.test(left.value), "the context panel has no left border to line up with");
});

checkAsync("polish: the legend, the link rows and the palette foot use the mockups' line-height; link rows are links (blue, blue-hover on hover), and plain rows stay ink", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  const lh = (el) => { const w = cascadeOf(doc, el, "line-height"); return w ? w.value.trim() : "unset"; };
  const legend = doc.querySelector("#okf-explorer .okf-explorer-foot ul.okf-legend");
  assert(legend && lh(legend) === "normal", `legend line-height: ${legend && lh(legend)} (the mockup's is normal; the body's 1.6 spaces the rows 26 px apart)`);
  const row = doc.querySelector("#okf-context .okf-backlinks a.okf-row");
  assert(row && lh(row) === "normal", `Referenced by row line-height: ${row && lh(row)}`);
  const colour = cascadeOf(doc, row, "color");
  assert(colour && colour.value.trim() === "var(--blue)", `Referenced by row colour: ${colour && colour.value} (the mockup's link colour is blue)`);
  const hover = Array.from(doc.styleSheets).flatMap((s) => Array.from(s.cssRules)).filter((r) => r.selectorText && /a\.okf-row:hover/.test(r.selectorText)).map((r) => r.style.getPropertyValue("color"));
  assert(hover.length > 0 && hover.every((v) => v === "var(--blue-hover)"), `a.okf-row:hover colours: ${JSON.stringify(hover)}`);
  const plain = doc.createElement("span");
  plain.className = "okf-row";
  doc.querySelector("#okf-context").appendChild(plain);
  assert(cascadeOf(doc, plain, "color").value.trim() === "var(--ink)", `a row that is not a link is ${cascadeOf(doc, plain, "color").value}, not ink`);
  doc.querySelector(".okf-palette-open").click();
  const foot = doc.querySelector("body > .okf-palette-backdrop .okf-palette-foot");
  assert(foot && lh(foot) === "normal", `palette foot line-height: ${foot && lh(foot)} (40 px tall against the mockup's 35.5)`);
});

checkAsync("polish: a body h1 is subordinate to the page title and above the body h2 (Inter Tight 600, 26 px), anchored under #okf-body", async () => {
  const window = await openPage("p11-page.html");
  const doc = window.document;
  const body = doc.getElementById("okf-body");
  const h1 = doc.createElement("h1");
  h1.textContent = "Computation";
  body.appendChild(h1);
  const h2 = doc.createElement("h2");
  body.appendChild(h2);
  const px = (el, prop) => { const w = cascadeOf(doc, el, prop); return w ? { v: w.value.trim(), sel: w.selector } : { v: "unset", sel: "" }; };
  const size = px(h1, "font-size");
  const title = px(doc.querySelector("body > .okf-layout > main > .okf-page-head h1"), "font-size");
  assert(size.v === "26px" && /#okf-body/.test(size.sel), `a body h1 is ${size.v} (${size.sel}): the unanchored 32px rule is still what decides`);
  assert(parseFloat(title.v) > parseFloat(size.v) && parseFloat(size.v) > parseFloat(px(h2, "font-size").v), `the scale is not title ${title.v} > body h1 ${size.v} > body h2 ${px(h2, "font-size").v}`);
  assert(px(h1, "font-weight").v === "600" && px(h1, "font-family").v === "var(--display)", `body h1 weight ${px(h1, "font-weight").v}, family ${px(h1, "font-family").v}`);
});

// --- P1.1 final review, part B (browser JS, CSS, harness) ---------------------

console.log("\nP1.1 final review, part B:");

check("check() fails a body that returns a promise instead of printing ok before its assertions run", () => {
  // An async body used with check() would pass at once and, if it rejected
  // after the run ended, leave "N passed, 0 failed". checkAsync is the helper.
  const asyncBody = attemptSync(async () => { throw new Error("late"); });
  assert(asyncBody !== null && /use checkAsync/.test(asyncBody.message), `an async body was accepted by check(): ${asyncBody && asyncBody.message}`);
  const thenable = attemptSync(() => ({ then() {} }));
  assert(thenable !== null, "a thenable body was accepted by check()");
  assert(attemptSync(() => {}) === null && attemptSync(() => 3) === null, "a plain synchronous body was refused");
  const thrown = attemptSync(() => { throw new Error("plain"); });
  assert(thrown !== null && thrown.message === "plain", "a synchronous failure lost its message");
});

// A copy of a rendered page whose markdown body is replaced: the page keeps its
// real chrome and scripts, the body is whatever the case needs.
function withBody(rel, name, markdown) {
  const source = fs.readFileSync(path.join(SITE, rel), "utf8");
  const open = '<script type="application/json" id="okf-payload">';
  const start = source.indexOf(open);
  const end = source.indexOf("</script>", start);
  if (start < 0 || end < 0) { throw new Error(`${rel}: no payload to replace`); }
  const payload = JSON.parse(source.slice(start + open.length, end));
  payload.body = markdown;
  // HTML-safe JSON, as HtmlSafeJson writes it: < > & as JSON escapes.
  const backslash = String.fromCharCode(92);
  const json = JSON.stringify(payload)
    .replace(/</g, `${backslash}u003c`).replace(/>/g, `${backslash}u003e`).replace(/&/g, `${backslash}u0026`);
  fs.writeFileSync(path.join(SITE, name), source.slice(0, start + open.length) + json + source.slice(end));
  return name;
}

checkAsync("explorer: a long type name wraps whole inside its chip and the panel, and the count stays in the chip", async () => {
  // jsdom does no layout, so this reads the cascade (the recette measures: no
  // sideways scroll at 390 and 1440 px). The shared chip is nowrap and 26 px
  // high: left alone, a long name widens the page or is clipped (E4 shows it whole).
  // The long type is made in the executed index, not by a fixture: a bundle
  // fixture only reaches the chips while it ranks 0 to 4 among all the types,
  // and every fixture added later shifts that. Type rank 2 is renamed.
  const names = [
    "A deliberately very long type name written with spaces so that it must wrap inside its chip",
    "U".repeat(740),
  ];
  for (const name of names) {
    const rename = `window.OKF_INDEX.types[2].name = ${JSON.stringify(name)};`;
    const source = fs.readFileSync(path.join(SITE, "assets", "okf-index.js"), "utf8") + "\n;" + rename;
    const window = await openPage("foo/bar.html", { override: { "assets/okf-index.js": source } });
    assertWrappingChip(window, name);
  }
});

function assertWrappingChip(window, name) {
  const doc = window.document;
  const chip = Array.from(doc.querySelectorAll("#okf-explorer .okf-type-chips .okf-chip"))
    .find((c) => c.querySelector(".okf-chip-text").textContent === name);
  assert(chip, `the renamed type (${name.length} characters) is not among the type chips (rank 0 to 4)`);
  const count = chip.querySelector(".okf-chip-count");
  assert(count && /^\d+$/.test(count.textContent), "the chip lost its count");
  const want = { "max-width": "100%", height: "auto", "min-height": "26px", "white-space": "normal", "overflow-wrap": "anywhere" };
  for (const [prop, value] of Object.entries(want)) {
    const won = cascadeOf(doc, chip, prop);
    assert(won && won.value.trim() === value, `explorer type chip ${prop}: ${won && won.value} from ${won && won.selector}, expected ${value}`);
    assert(won.selector.startsWith("#okf-explorer"), `${prop} is decided by ${won.selector}, not by an explorer-anchored rule`);
  }
  const noShrink = cascadeOf(doc, count, "flex");
  assert(noShrink && /^(none|0 0 auto)$/.test(noShrink.value.trim()), `the count may shrink or wrap inside the chip (flex: ${noShrink && noShrink.value})`);
}

// Resolves with the window's next hashchange (the listener is registered last,
// so the page's own listeners have run by then), or rejects after `ms`: a case
// that depends on the event fails loudly instead of racing a fixed wait.
function nextHashChange(window, ms = 5000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`no hashchange within ${ms} ms (hash: ${window.location.hash})`)), ms);
    window.addEventListener("hashchange", () => { clearTimeout(timer); resolve(); }, { once: true });
  });
}

checkAsync("contents: a fragment naming an element outside the body (the skip link's #okf-main) is the browser's, even beside a heading titled alike", async () => {
  const probe = withBody("foo.html", "p11-fixb-skip.html", "## Okf: main\n\nfirst\n\n## OKF main\n\nsecond\n\n## Usage\n\nthird");
  const headings = (doc) => Array.from(doc.querySelectorAll("#okf-body h2"));
  // On load.
  const loaded = await openPage(probe, { hash: "#okf-main" });
  const focusedOnLoad = loaded.document.activeElement;
  assert(!focusedOnLoad || !focusedOnLoad.closest("#okf-body"), `#okf-main on load focused a body heading: ${focusedOnLoad && focusedOnLoad.textContent}`);
  // By activating the skip link, as a keyboard reader does.
  const window = await openPage(probe);
  const doc = window.document;
  const ids = headings(doc).map((h) => h.id);
  assert(ids[0] === "okf-h-okf-main", `this case needs a heading generated as okf-h-okf-main (got ${ids})`);
  const skip = doc.querySelector("a.okf-skip");
  assert(skip && skip.getAttribute("href") === "#okf-main", "the skip link is not a #okf-main link");
  let changed = nextHashChange(window);
  skip.click();
  await changed;
  assert(window.location.hash === "#okf-main", `the skip link left the fragment at ${window.location.hash}`);
  const focused = doc.activeElement;
  assert(!focused || !focused.closest("#okf-body"), `the skip link moved the focus to the body heading "${focused && focused.textContent}"`);
  // A prefixed fragment still reaches that very heading, and an author one the others.
  changed = nextHashChange(window);
  window.location.hash = "okf-h-okf-main";
  await changed;
  assert(doc.activeElement === headings(doc)[0], `#okf-h-okf-main focused ${doc.activeElement && doc.activeElement.tagName}`);
  changed = nextHashChange(window);
  window.location.hash = "usage";
  await changed;
  assert(doc.activeElement === headings(doc)[2], "an author fragment no longer reaches its heading");
});

checkAsync("explorer: only the path down to the current page is open by default", async () => {
  const folders = (doc) => Array.from(doc.querySelectorAll("#okf-explorer button.okf-tree-toggle"));
  const window = await openPage("foo/bar.html");
  const toggles = folders(window.document);
  assert(toggles.length >= 3, `this case needs several folders (${toggles.length})`);
  for (const toggle of toggles) {
    const item = toggle.closest("li");
    const label = item.querySelector(".okf-tree-link, .okf-tree-folder").textContent;
    const holds = item.querySelector('a[aria-current="page"]') !== null;
    assert(toggle.getAttribute("aria-expanded") === String(holds), `${label}: aria-expanded ${toggle.getAttribute("aria-expanded")}, holds the current page: ${holds}`);
    const children = item.querySelector(":scope > ul");
    assert(children.hidden === !holds, `${label}: children shown ${!children.hidden}, holds the current page: ${holds}`);
  }
  assert(toggles.filter((t) => t.getAttribute("aria-expanded") === "true").length === 1, "not exactly one folder is open");
  const home = await openPage("index.html");
  assert(folders(home.document).every((t) => t.getAttribute("aria-expanded") === "false"), "a folder is open on a page that is in none");
});

checkAsync("contents: only h2 and h3 are listed, an h4 or deeper is not", async () => {
  const probe = withBody("foo.html", "p11-fixb-levels.html", "## Two\n\na\n\n### Three\n\nb\n\n#### Four\n\nc\n\n##### Five\n\nd\n\n# One\n\ne");
  const window = await openPage(probe);
  const doc = window.document;
  const listed = Array.from(doc.querySelectorAll("#okf-toc a"), (a) => a.textContent);
  assert(JSON.stringify(listed) === JSON.stringify(["Two", "Three"]), `the contents list ${JSON.stringify(listed)}, expected only the h2 and the h3`);
  const deeper = Array.from(doc.querySelectorAll("#okf-body h1, #okf-body h4, #okf-body h5"));
  assert(deeper.length === 3 && deeper.every((h) => h.id.startsWith("okf-h-")), "this case needs the h1, h4 and h5 in the body, with ids");
});

checkAsync("palette: '/' typed with Shift (a layout where it needs Shift) opens it outside an editable field", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const backdrop = doc.querySelector(".okf-palette-backdrop");
  const event = key(window, doc.body, { key: "/", shiftKey: true });
  assert(!backdrop.hidden && event.defaultPrevented, `Shift+/ opened ${!backdrop.hidden}, prevented ${event.defaultPrevented}`);
  key(window, doc.getElementById("okf-palette-input"), { key: "Escape" });
  const filter = doc.getElementById("okf-tree-filter");
  filter.focus();
  const typed = key(window, filter, { key: "/", shiftKey: true });
  assert(backdrop.hidden && !typed.defaultPrevented, "Shift+/ in the tree filter was taken by the palette");
});

checkAsync("palette: Ctrl+K on a layout whose key is no ASCII letter (Cyrillic, Greek) is matched by the physical key; Dvorak and others by the letter", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const backdrop = doc.querySelector(".okf-palette-backdrop");
  const closeIt = () => { if (!backdrop.hidden) { key(window, doc.getElementById("okf-palette-input"), { key: "Escape" }); } };
  for (const init of [{ key: "л", code: "KeyK" }, { key: "κ", code: "KeyK" }, { key: "Dead", code: "KeyK" }]) {
    const event = key(window, doc.body, Object.assign({ ctrlKey: true }, init));
    assert(!backdrop.hidden && event.defaultPrevented, `Ctrl+${JSON.stringify(init)} did not open the palette`);
    closeIt();
  }
  // Dvorak: the physical K produces "t", and the physical V position produces "k".
  const dvorakT = key(window, doc.body, { key: "t", code: "KeyK", ctrlKey: true });
  assert(backdrop.hidden && !dvorakT.defaultPrevented, "Ctrl+T on the physical K key (Dvorak) opened the palette");
  const dvorakK = key(window, doc.body, { key: "k", code: "KeyV", ctrlKey: true });
  assert(!backdrop.hidden && dvorakK.defaultPrevented, "Ctrl+K produced by another physical key (Dvorak) did not open the palette");
  closeIt();
  // The fallback keeps the exact modifier rule and the editable-field rule.
  for (const extra of [{ shiftKey: true }, { altKey: true }, { metaKey: true }]) {
    const event = key(window, doc.body, Object.assign({ key: "л", code: "KeyK", ctrlKey: true }, extra));
    assert(backdrop.hidden && !event.defaultPrevented, `Ctrl+${JSON.stringify(extra)}+K (Cyrillic) was taken`);
  }
  const filter = doc.getElementById("okf-tree-filter");
  filter.focus();
  const inField = key(window, filter, { key: "л", code: "KeyK", ctrlKey: true });
  assert(backdrop.hidden && !inField.defaultPrevented, "Ctrl+K (Cyrillic) in an editable field was taken");
});

checkAsync("body headings: the focus ring of a heading reached through a fragment is the declared 2 px blue outline, offset 4 px", async () => {
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const h = doc.querySelector("#okf-body h2");
  assert(h && h.getAttribute("tabindex") === "-1", "this case needs a generated heading with tabindex -1");
  const rules = [];
  walkStyleRules(window, (rule) => {
    if (rule.selectorText === '#okf-body [tabindex="-1"]:focus') { rules.push(rule); }
  });
  assert(rules.length === 1, `${rules.length} rules style #okf-body [tabindex="-1"]:focus, expected one`);
  const style = rules[0].style;
  assert(style.getPropertyValue("outline").replace(/\s+/g, " ").trim() === "2px solid var(--blue)", `outline: ${style.getPropertyValue("outline")}`);
  assert(style.getPropertyValue("outline-offset").trim() === "4px", `outline-offset: ${style.getPropertyValue("outline-offset")}`);
  // The rule must reach a generated heading.
  assert(h.matches('#okf-body [tabindex="-1"]'), "a generated heading no longer matches the focus-ring rule");
});

checkAsync("explorer: the revealed entry sits a third of the band below the sticky head, not at the band's top", async () => {
  const probe = explorerScrollProbe("auto", true, 2700, { head: 243, foot: 90 });
  const window = await openPage("foo/bar.html", { beforeParse: probe.beforeParse });
  const entry = window.document.getElementById("okf-explorer").querySelector('a[aria-current="page"]').getBoundingClientRect();
  // Visible part 668 (800 high at 100, window 768); band from the head's 243
  // to the foot's top 578 = 335 high; a third rounds to 112.
  assert(entry.top === 100 + 243 + 112, `the entry sits at ${entry.top}, expected ${100 + 243 + 112} (a third of the band below its top)`);
});

checkAsync("body h3 is drawn in the 600 face at 17 px, below h2, never the heavy default", async () => {
  const window = await openPage("foo/bar.html");
  const doc = window.document;
  const h3 = doc.querySelector("#okf-body h3");
  const h2 = doc.querySelector("#okf-body h2");
  assert(h3 && h2, "this case needs a body h3 and h2");
  const value = (el, prop) => { const w = cascadeOf(doc, el, prop); return w ? { v: w.value.trim(), sel: w.selector } : { v: "unset", sel: "" }; };
  const weight = value(h3, "font-weight");
  assert(weight.v === "600" && /^#okf-body/.test(weight.sel), `body h3 weight ${weight.v} from ${weight.sel}`);
  assert(value(h3, "font-family").v === "var(--display)", `body h3 family ${value(h3, "font-family").v}`);
  assert(value(h3, "font-size").v === "17px" && parseFloat(value(h3, "font-size").v) < parseFloat(value(h2, "font-size").v), `body h3 size ${value(h3, "font-size").v} (h2 ${value(h2, "font-size").v})`);
  assert(weight.v === value(h2, "font-weight").v, "body h3 and h2 are not in the same face");
});

// A site index with damage, or one that throws on access, must leave the page
// working: header, body, contents and theme. The palette and the explorer
// simply go without what they cannot read.
function damageIndex(script) {
  const source = fs.readFileSync(path.join(SITE, "assets", "okf-index.js"), "utf8");
  return `${source}\n;(function () { var idx = window.OKF_INDEX; ${script} })();`;
}

function assertChromeWorks(window, what) {
  const doc = window.document;
  assert(doc.getElementById("okf-body").textContent.includes("first"), `${what}: the body did not render`);
  assert(!doc.getElementById("okf-toc").hidden, `${what}: the contents list is gone`);
  assert(doc.querySelector("body > header.bar"), `${what}: the header is gone`);
  const toggle = doc.getElementById("okf-theme-toggle");
  assert(toggle, `${what}: the theme toggle is gone`);
  const before = toggle.getAttribute("aria-pressed");
  toggle.click();
  assert(toggle.getAttribute("aria-pressed") !== before, `${what}: the theme toggle does not toggle`);
}

checkAsync("a damaged index (null entries, missing tags) degrades: no page error, the chrome works, the palette is never left half-updated", async () => {
  const override = { "assets/okf-index.js": damageIndex(
    "var n = idx.concepts.length; idx.concepts.push(null, 5, 'text');"
    + " idx.tree.push({ name: 'ghost-null', concept: n, children: [] }, { name: 'ghost-number', concept: n + 1, children: [] });"
    // A null BEFORE the current concept: the explorer's search for it walks past the null.
    + " idx.concepts[0] = null; delete idx.concepts[2].tags; idx.concepts[3].tags = 'oops';") };
  const window = await openPage("foo.html", { override });
  const doc = window.document;
  assert(!doc.getElementById("okf-explorer").hidden, "the explorer did not draw despite a readable index");
  for (const name of ["__proto__", "ghost-null", "ghost-number"]) {
    const ghost = Array.from(doc.querySelectorAll("#okf-explorer .okf-tree-folder")).find((s) => s.textContent === name);
    assert(ghost && ghost.closest("li").querySelector("a") === null, `${name}: a damaged entry was drawn as a link instead of a plain row`);
  }
  assert(doc.querySelector('#okf-explorer a[aria-current="page"]'), "the current entry was lost behind the damaged ones");
  // The palette: a query that only the tags could match walks every entry.
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "foo");
  const options = () => Array.from(doc.querySelectorAll(".okf-palette-option"));
  assert(options().length > 0, "no option for a query that matches");
  type(window, input, "zz-nothing-matches-this");
  assert(options().length === 0, `the previous options stayed after a query that matches nothing (${options().length}): Enter would open a stale result`);
  assert(doc.getElementById("okf-palette-status").textContent === "No matching concept", "the empty result is not announced");
  doc.querySelector(".okf-palette-close").click();
  assertChromeWorks(window, "damaged index");
});

checkAsync("an index whose access throws (a getter, a Proxy trap) is no index: no page error, nothing built from it, the chrome works", async () => {
  const getter = await openPage("foo.html", {
    blocked: ["assets/okf-index.js"],
    beforeParse(w) { Object.defineProperty(w, "OKF_INDEX", { configurable: true, get() { throw new w.Error("boom"); } }); },
  });
  assert(getter.document.getElementById("okf-explorer").hidden, "the explorer was built from a throwing getter");
  assert(getter.document.querySelector(".okf-palette-open") === null, "the palette was built from a throwing getter");
  assertChromeWorks(getter, "throwing getter");
  const trap = "var boom = function () { throw new Error('trap'); };"
    + "window.OKF_INDEX = new Proxy({ version: 2 }, { has: boom, get: boom, ownKeys: boom, getOwnPropertyDescriptor: boom });";
  const proxy = await openPage("foo.html", { override: { "assets/okf-index.js": trap } });
  assert(proxy.document.getElementById("okf-explorer").hidden, "the explorer was built from a Proxy that throws");
  assert(proxy.document.querySelector(".okf-palette-open") === null, "the palette was built from a Proxy that throws");
  assert(proxy.OkfSite.readIndex(proxy) === null, "readIndex accepted a Proxy that throws");
  assertChromeWorks(proxy, "throwing Proxy");
});

/// A damaged `tree` (final review B, item 3): the palette and P2 skip what they
// cannot read, and so does the explorer. A null or non-object entry is
// skipped, missing or non-array `children` are none, a node met twice (a
// cycle, or a shared subtree that would be built once per path) is drawn
// once, and nesting beyond TREE_DEPTH_LIMIT (100) is not drawn at all: the
// palette still reaches those concepts.
function explorerNames(doc) {
  return Array.from(doc.querySelectorAll("#okf-explorer .okf-tree-folder, #okf-explorer .okf-tree-link")).map((s) => s.textContent);
}

checkAsync("explorer: a damaged tree (null and non-object entries, missing or non-array children) degrades to what is readable", async () => {
  const override = { "assets/okf-index.js": damageIndex(
    "idx.tree.push(null, 5, 'text', { name: 'nochildren', concept: -1 }, { name: 'badchildren', concept: -1, children: 7 },"
    + " { name: 'strchildren', concept: -1, children: 'abc' },"
    + " { name: 'holey', concept: -1, children: [null, 3, { name: 'inner', concept: -1, children: [] }] });") };
  const window = await openPage("foo.html", { override });
  const doc = window.document;
  assert(!doc.getElementById("okf-explorer").hidden, "the explorer did not draw despite a readable index");
  const names = explorerNames(doc);
  for (const name of ["nochildren", "badchildren", "strchildren", "holey", "inner"]) {
    assert(names.includes(name), `${name}: not drawn (${names.length} names)`);
  }
  const holey = Array.from(doc.querySelectorAll("#okf-explorer .okf-tree-folder")).find((s) => s.textContent === "holey").closest("li");
  assert(holey.querySelectorAll(":scope > ul > li").length === 1, "the null and number children of holey were drawn");
  assert(doc.querySelector('#okf-explorer a[aria-current="page"]'), "the current entry was lost behind the damaged ones");
  assertChromeWorks(window, "damaged tree");
});

checkAsync("explorer: a cyclic tree (a node its own descendant) draws every node once and never overflows the stack", async () => {
  const override = { "assets/okf-index.js": damageIndex(
    "var a = { name: 'cyc-a', concept: -1, children: [] }, b = { name: 'cyc-b', concept: -1, children: [a] };"
    + " a.children.push(b, a); idx.tree.push(a);") };
  const window = await openPage("foo.html", { override });
  const doc = window.document;
  assert(!doc.getElementById("okf-explorer").hidden, "the explorer stayed hidden behind a cycle");
  const names = explorerNames(doc);
  assert(names.filter((n) => n === "cyc-a").length === 1 && names.filter((n) => n === "cyc-b").length === 1, `cycle drawn as: ${names.filter((n) => /^cyc-/.test(n))}`);
  assert(doc.querySelector('#okf-explorer a[aria-current="page"]'), "the current entry was lost");
  assertChromeWorks(window, "cyclic tree");
});

checkAsync("explorer: a 100 000-deep chain stops at the depth limit instead of overflowing the stack", async () => {
  const override = { "assets/okf-index.js": damageIndex(
    "var top = { name: 'deep-0', concept: -1, children: [] }, tip = top;"
    + " for (var d = 1; d < 100000; d++) { var next = { name: 'deep-' + d, concept: -1, children: [] }; tip.children.push(next); tip = next; }"
    + " idx.tree.push(top);") };
  const window = await openPage("foo.html", { override });
  const doc = window.document;
  assert(!doc.getElementById("okf-explorer").hidden, "the explorer stayed hidden behind a deep chain");
  const deep = explorerNames(doc).filter((n) => /^deep-\d+$/.test(n));
  assert(deep.length >= 50 && deep.length <= 101, `${deep.length} levels of the chain were drawn (expected a bounded number, at most 101)`);
  assert(doc.querySelector('#okf-explorer a[aria-current="page"]'), "the current entry was lost");
  assertChromeWorks(window, "deep chain");
});

checkAsync("explorer: a subtree shared by many parents is built once, not once per path", async () => {
  // 16 levels, each with two children that are the same next level: 2^16 paths.
  const override = { "assets/okf-index.js": damageIndex(
    "var next = { name: 'dag-leaf', concept: -1, children: [] };"
    + " for (var d = 0; d < 16; d++) { next = { name: 'dag-' + d, concept: -1, children: [next, next] }; }"
    + " idx.tree.push(next);") };
  const started = Date.now();
  const window = await openPage("foo.html", { override });
  const doc = window.document;
  assert(!doc.getElementById("okf-explorer").hidden, "the explorer stayed hidden behind a shared subtree");
  const names = explorerNames(doc).filter((n) => /^dag-/.test(n));
  assert(names.length === 17 && new Set(names).size === 17, `${names.length} dag rows drawn (expected the 17 distinct nodes once each)`);
  assert(Date.now() - started < 20000, "the shared subtree took long enough to be built per path");
});

// The indent grows with the depth up to INDENT_LEVELS (12) levels, then stops:
// 12 + 18 * 12 = 228 px. A deeper tree would otherwise widen the page at 390 px
// (final review B, item 4). The nesting itself (the <ul> inside <li> chain) keeps
// the true depth for assistive technology, and the stacked explorer may scroll
// sideways inside itself.
checkAsync("explorer: the row indent stops growing after 12 levels while the nesting keeps the true depth", async () => {
  const override = { "assets/okf-index.js": damageIndex(
    "var top = { name: 'ind-0', concept: -1, children: [] }, tip = top;"
    + " for (var d = 1; d < 40; d++) { var next = { name: 'ind-' + d, concept: -1, children: [] }; tip.children.push(next); tip = next; }"
    + " idx.tree.push(top);") };
  const window = await openPage("foo.html", { override });
  const doc = window.document;
  const rowOf = (n) => Array.from(doc.querySelectorAll("#okf-explorer .okf-tree-folder")).find((s) => s.textContent === "ind-" + n).closest(".okf-tree-row");
  const pad = (n) => parseFloat(rowOf(n).style.paddingLeft);
  assert(pad(0) === 12 && pad(1) === 30 && pad(12) === 228, `indent at depth 0, 1, 12: ${pad(0)}, ${pad(1)}, ${pad(12)}`);
  for (const n of [13, 20, 39]) { assert(pad(n) === 228, `indent at depth ${n}: ${pad(n)} (expected the 228 px cap)`); }
  const ancestors = (n) => {
    let count = 0;
    for (let e = rowOf(n).closest("li").parentElement; e; e = e.parentElement) { if (e.classList.contains("okf-tree-children")) { count++; } }
    return count;
  };
  assert(ancestors(39) === 39 && ancestors(13) === 13, `the nesting lost the true depth: ${ancestors(13)}, ${ancestors(39)}`);
});

// Smoke check of the stylesheet text, not proof: jsdom lays nothing out and
// applies no @media rule. The real check is the Chromium and Firefox run of
// the final review (item 4, a 40-level tree at 390 px: no document scroll).
checkAsync("explorer: the stacked layout lets the explorer scroll sideways inside itself (static smoke check)", async () => {
  const window = await openPage("index.html");
  let found = false;
  walkStyleRules(window, (rule) => {
    const media = rule.parentRule && rule.parentRule.media ? rule.parentRule.media.mediaText : "";
    if (rule.selectorText === "#okf-explorer" && rule.style.getPropertyValue("overflow-x") === "auto" && /not all and \(min-width:\s*1100px\)/.test(media)) { found = true; }
  });
  assert(found, "no `#okf-explorer { overflow-x: auto }` in the stacked-layout @media block");
});

// Forced colors (final review B, item 6): the type chip's ink background
// becomes Canvas, so a glyph filled white (var(--white)) disappears. Smoke
// check of the stylesheet text, not proof: jsdom applies no @media rule and
// has no forced-colors mode. The real check is Chromium and Firefox with
// forcedColors: 'active' (computed fill/stroke and a screenshot of the chip,
// page head and graph drawer, light and dark).
checkAsync("type chip glyph: a forced-colors rule gives the shapes CanvasText, anchored to the chip containers (static smoke check)", async () => {
  const window = await openPage("index.html");
  const seen = { fill: false, stroke: false };
  walkStyleRules(window, (rule) => {
    const media = rule.parentRule && rule.parentRule.media ? rule.parentRule.media.mediaText : "";
    if (!/forced-colors:\s*active/.test(media) || !/\.okf-chip-type\s+svg/.test(rule.selectorText)) { return; }
    if (rule.style.getPropertyValue("fill").toLowerCase() === "canvastext" && /okf-shape-0/.test(rule.selectorText)) { seen.fill = true; }
    if (rule.style.getPropertyValue("stroke").toLowerCase() === "canvastext" && /okf-shape-4/.test(rule.selectorText)) { seen.stroke = true; }
  });
  assert(seen.fill, "no forced-colors rule fills the chip's filled shapes (0 to 3) with CanvasText");
  assert(seen.stroke, "no forced-colors rule strokes the chip's outline shapes (4, 5) with CanvasText");
});

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
    const registeredBefore = casesRegistered;
    const returned = mod.register(SLICE_HELPERS);
    if (returned && typeof returned.then === "function") {
      // An async register can register (or skip) its cases after the run has
      // finished, or reject unseen: refuse it instead of losing its cases.
      returned.then(undefined, () => {});
      failures++;
      console.log(`FAIL  - cases/${name}: register(h) must be synchronous (it returned a promise)`);
    } else if (casesRegistered === registeredBefore) {
      // A register that defers its work (setTimeout, an event) and returns
      // undefined would otherwise register nothing and pass in silence.
      failures++;
      console.log(`FAIL  - cases/${name}: register(h) registered no case (cases must be registered before it returns)`);
    }
  } catch (err) {
    failures++;
    console.log(`FAIL  - cases/${name}: register(h) threw: ${err.message}`);
  }
}

// --- end of async checks ---

// A pending Promise does not keep Node alive: a case awaiting an event that
// never comes would end the process with code 0 and no summary. Every case
// is bounded by a timer (which does keep Node alive) that fails it instead.
const CASE_TIMEOUT_MS = 10000;
let summarized = false;
process.on("exit", () => {
  if (!summarized) {
    console.log("FAIL  - the harness exited before printing its summary");
    process.exitCode = 1;
  }
});

async function runAsyncChecks() {
  queueClosed = true;
  // Synchronous output (helpers, OkfShapes, slice files) has all been printed
  // by now; every queued page case reports under this header.
  console.log("\nPage cases (queued above, awaited now):");
  for (const { name, fn } of asyncChecks) {
    openedPages = [];
    let timer = null;
    try {
      await Promise.race([
        fn(),
        new Promise((_, reject) => {
          timer = setTimeout(
            () => reject(new Error(`timed out after ${CASE_TIMEOUT_MS} ms (an awaited event never came)`)),
            CASE_TIMEOUT_MS);
        }),
      ]);
      // Errors raised by handlers after load fail the case too.
      for (const page of openedPages) {
        if (page.errors.length > 0) { throw new Error(`page script error: ${page.errors[0].message}`); }
      }
      passed++;
      console.log(`  ok  - ${name}`);
    } catch (err) {
      failures++;
      console.log(`FAIL  - ${name}`);
      console.log(`        ${err.message}`);
    } finally {
      clearTimeout(timer);
      for (const page of openedPages) { page.window.close(); }
    }
  }
}

runAsyncChecks().then(
  () => {
    summarized = true;
    console.log(`\n${passed} passed, ${failures} failed`);
    process.exit(failures === 0 ? 0 : 1);
  },
  (err) => {
    console.log(`FAIL  - the async runner crashed: ${err && err.stack}`);
    process.exit(1);
  },
);
