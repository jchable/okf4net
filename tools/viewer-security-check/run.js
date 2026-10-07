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
function duplicateName(name) {
  if (!caseNames.has(name)) {
    caseNames.add(name);
    return false;
  }
  failures++;
  console.log(`FAIL  - duplicate case name: ${name}`);
  return true;
}

/** @param {string} name @param {() => void} fn */
function check(name, fn) {
  if (duplicateName(name)) { return; }
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
    throw new Error("checkAsync called after the page cases started");
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

function treeLink(window, id) {
  return Array.from(window.document.querySelectorAll("#okf-explorer a.okf-tree-link"))
    .find((a) => a.getAttribute("title") === id) || null;
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
  assert(current && current.getAttribute("title") === "foo/bar", "the current page is not marked");
  toggle.click();
  assert(toggle.getAttribute("aria-expanded") === "false" && !isShown(treeLink(window, "foo/bar")), "the toggle did not collapse");
});

// Recette R2. jsdom does no layout, so this probe gives #okf-explorer a
// geometry: 800 px tall at 100 px from the top, 3000 px of content, the
// current entry `entryOffset` (default 2700) px down it, and the given computed overflow-y. It records
// every scroll request: the explorer's own scrollTop, window scrolling and
// scrollIntoView. That the entry is visible on screen is the recette's (C2).
function explorerScrollProbe(overflowY, layout = true, entryOffset = 2700) {
  const probe = { requests: [], navTop: 0 };
  probe.beforeParse = (w) => {
    const isNav = (el) => el.id === "okf-explorer";
    const proto = w.Element.prototype;
    if (layout) {
      Object.defineProperty(proto, "scrollHeight", { configurable: true, get() { return isNav(this) ? 3000 : 0; } });
      Object.defineProperty(proto, "clientHeight", { configurable: true, get() { return isNav(this) ? 800 : 0; } });
      proto.getBoundingClientRect = function () {
        const current = this.getAttribute("aria-current") === "page";
        const top = isNav(this) ? 100 : current ? 100 + entryOffset - probe.navTop : 0;
        const height = isNav(this) ? 800 : current ? 20 : 0;
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
  const stale = (id) => treeLink(window, id).parentElement.querySelector(".okf-stale");
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
  assert(treeLink(window, "edge").parentElement.querySelector(".okf-stale").hidden, "stale at .000 although the deadline is .0001");
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
    concepts.push({ id, title: `Item ${k}`, type: "Note", tags: [], path: `${id}.html`, trust: "unverified", staleAfterMs: null, staleAfterDate: null });
  }
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 1, concepts, ghosts: [], edges: [], tree: [] })};`;
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
    assert(root.querySelectorAll("img, script, svg, iframe, object").length === 0, `markup from bundle text became live in #${id}`);
  }
  assert(doc.getElementById("okf-explorer").textContent.includes("<img"), "the hostile title was dropped instead of shown as text");
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
// exists. Three independent layers, none of which relies on the others:
//   1. a static scan of the stylesheet (every selector naming a class starts
//      from a §12.6 anchor): the only layer that sees a class no fixture
//      lists;
//   2. a selector-matching differential: no body element wearing chrome
//      classes may match a class-naming selector (pseudo-elements stripped,
//      @media rules included): independent of which properties a rule sets;
//   3. a computed-style comparison against an unclassed <code>, over every
//      property the stylesheet declares.
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

// Calls visit(rule) for every style rule of every stylesheet of a page, into
// @media, @supports and any other grouping rule.
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
// (), [] and quotes), dropping empty pieces; `keep` keeps the separators.
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

// Expands the first :is(...) / :where(...) of a selector into its
// alternatives, recursively: ":is(A, B) .x" -> ["A .x", "B .x"].
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

function normalizeSelector(selector) {
  return selector.replace(/\s+/g, " ").replace(/ ?> ?/g, " > ").trim();
}

// Does the selector name a class (a .class, or a [class...] attribute test)?
function namesClass(selector) {
  const bare = selector.replace(/"[^"]*"|'[^']*'/g, "");
  return /\.[A-Za-z_\\-]/.test(bare.replace(/\[[^\]]*\]/g, "")) || /\[\s*class\b/.test(bare);
}

// The tag of the last compound (the element the selector matches), or "".
function subjectTag(selector) {
  const compounds = splitTopLevel(selector, " >+~");
  const last = compounds.length ? compounds[compounds.length - 1] : "";
  const m = /^[A-Za-z][\w-]*/.exec(last);
  return m ? m[0].toLowerCase() : "";
}

// The §12.6 chrome anchors a class-naming selector may start from. Anything
// else naming a class could match a <code> in #okf-body that wears it. A
// selector whose subject is an explicit non-code tag (a.broken,
// table.frontmatter th) can never match a <code>, so it needs no anchor.
const CHROME_ANCHORS = [
  /^#okf-tools(?![\w-])/, /^#okf-explorer(?![\w-])/, /^#okf-context(?![\w-])/,
  /^body > \.okf-palette-backdrop(?![\w-])/,
  /^body > \.okf-layout > main > \.okf-page-head(?![\w-])/,
  /^body > \.okf-layout > main > \.(?:meta|errors)(?![\w-])/,
  /^body > header\.bar(?![\w-])/, /^body > \.okf-skip(?![\w-])/, /^body > \.okf-graph-layout(?![\w-])/,
  /^body > \.topline(?![\w-])/, /^svg(?![\w-])/,
  // The layout shell itself: never an ancestor-or-self test of #okf-body content.
  /^body > \.okf-layout(?: > main)?$/,
];

function unanchoredClassSelectors(selectorText) {
  const offenders = [];
  for (const complex of splitTopLevel(selectorText, ",")) {
    for (const alt of expandIs(complex).map(normalizeSelector)) {
      if (!namesClass(alt)) { continue; }
      if (CHROME_ANCHORS.some((re) => re.test(alt))) { continue; }
      const tag = subjectTag(alt);
      if (tag && tag !== "code") { continue; }
      offenders.push(alt);
    }
  }
  return offenders;
}

// A pseudo-element's rule styles the element it hangs on: matching is tested
// on the element, with the pseudo-element removed.
function withoutPseudoElements(selector) {
  return selector.replace(/::?(?:before|after|first-line|first-letter|marker|placeholder|selection)(?![\w-])/g, "");
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
// computed-style comparison below, which tests what the browser engine does.
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
    // Descendant, child and sibling pairs: whatever the fixture nests, a pair
    // `.a .b`, `.a > .b` or `.a + .b` of listed classes is exercised too by an
    // element wearing EVERY class the page lists, nested in and beside others
    // like it, and holding descendants (sup, h2, li, a) that are compared
    // against the same descendants of an unclassed <code>.
    const everyClass = Array.from(new Set(listed.flatMap((el) => Array.from(el.classList)))).join(" ");
    const kids = "<sup>s</sup><h2>h</h2><ul><li>l</li></ul><a href=\"#\">a</a>";
    const probe = doc.createElement("div");
    probe.innerHTML = `<code class="${everyClass}"><code class="${everyClass}">n</code><code class="${everyClass}">m</code>${kids}</code>`
      + `<code><code>n</code><code>m</code>${kids}</code>`;
    body.appendChild(probe);
    const wornRoot = probe.children[0];
    const refRoot = probe.children[1];
    const worn = Array.from(body.querySelectorAll("code[class]"));
    const props = declaredProperties(window);
    const sameStyle = (when, el, expectedEl, label) => {
      const style = window.getComputedStyle(el);
      const expected = window.getComputedStyle(expectedEl);
      for (const prop of props) {
        assert(style.getPropertyValue(prop) === expected.getPropertyValue(prop),
          `${when}: ${label} ${prop}: ${style.getPropertyValue(prop)} (the unclassed counterpart has ${expected.getPropertyValue(prop)})`);
      }
    };
    const assertInert = (when) => {
      for (const el of body.querySelectorAll("*")) {
        const style = window.getComputedStyle(el);
        const where = `${when}: <${el.tagName.toLowerCase()} class="${el.getAttribute("class") || ""}">`;
        assert(style.position !== "fixed" && style.position !== "absolute", `${where} inside #okf-body is position: ${style.position}`);
        assert(!/rect\(/.test(style.clip) && style.width !== "1px" && style.height !== "1px", `${where} inside #okf-body is clipped or 1px (screen-reader-only styling)`);
        assert(style.display !== "none" && style.visibility !== "hidden", `${where} inside #okf-body is hidden`);
      }
      // Layer 2: no class-naming rule (pseudo-elements included: jsdom cannot
      // compute their style, so matching is the only way to see a ::before
      // content such as a fake badge) matches an element of the body.
      walkStyleRules(window, (rule) => {
        for (const complex of splitTopLevel(rule.selectorText, ",")) {
          if (!namesClass(complex)) { continue; }
          const sel = withoutPseudoElements(complex);
          for (const el of worn) {
            let hit = false;
            try { hit = el.matches(sel); } catch (err) { hit = false; }
            assert(!hit, `${when}: the rule "${rule.selectorText}" reaches <code class="${el.getAttribute("class")}"> inside #okf-body`);
          }
        }
      });
      // Layer 3. jsdom neither expands shorthands nor resolves var() in them,
      // so both the shorthands and their longhands are compared, as declared.
      for (const el of worn) { sameStyle(when, el, reference, `<code class="${el.getAttribute("class")}">`); }
      const wornTree = [wornRoot, ...wornRoot.querySelectorAll("*")];
      const refTree = [refRoot, ...refRoot.querySelectorAll("*")];
      assert(wornTree.length === refTree.length, `${when}: the probe trees differ in shape`);
      wornTree.forEach((el, i) => sameStyle(when, el, refTree[i], `<${el.tagName.toLowerCase()}> inside a classed <code>`));
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
    [doc.querySelector("#okf-explorer .okf-badge.okf-trust-human"), "width", "8px"],
    [doc.querySelector("#okf-tools .okf-tool"), "min-height", "36px"],
    // The old header and context-title rules, anchored by P1.1.
    [doc.querySelector("body > .topline"), "height", "6px"],
    [doc.querySelector("body > header.bar > .bar-in"), "display", "flex"],
    [doc.querySelector("body > header.bar .wordmark"), "font-size", "20px"],
    [doc.querySelector("body > .okf-layout > main > .meta"), "font-size", "13px"],
  ];
  for (const [el, prop, value] of chrome) {
    assert(el, `a chrome element this case needs is missing (${prop}: ${value})`);
    const got = window.getComputedStyle(el).getPropertyValue(prop);
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

checkAsync("explorer: the filter field is labelled by what it does", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const filter = doc.getElementById("okf-tree-filter");
  const label = doc.querySelector('#okf-explorer label[for="okf-tree-filter"]');
  assert(label, "the filter has no <label for>");
  assert(filter.labels && filter.labels.length === 1 && filter.labels[0] === label, "the <label> is not associated with the filter");
  assert(label.textContent.trim() === "Filter the explorer", `the filter's accessible name is "${label.textContent.trim()}"`);
});

checkAsync("explorer: the stale badge shows staleAfterDate as written, never a date rebuilt from staleAfterMs", async () => {
  // The two fields disagree on purpose, so the case tells which one is shown.
  const concepts = [{ id: "x", title: "X", type: "Note", tags: [], path: "x.html", trust: "unverified", staleAfterMs: 0, staleAfterDate: "2026-10-06" }];
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 1, concepts, ghosts: [], edges: [], tree: [{ name: "x", concept: 0, children: [] }] })};`;
  const window = await openPage("index.html", { override: { "assets/okf-index.js": source }, now: () => Date.UTC(2026, 9, 7) });
  const stale = treeLink(window, "x").parentElement.querySelector(".okf-stale");
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
  // The option wraps: the id shares the title's line only when both fit whole,
  // else it goes under the title. No cap or shrink factor on the id can promise
  // that (a title just under its line still broke when the id's cap bound it).
  const wrap = window.getComputedStyle(option).getPropertyValue("flex-wrap");
  assert(wrap === "wrap", `the palette option does not wrap its id under a title that leaves it no room (flex-wrap: ${wrap})`);
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
  const expected = [
    [doc.getElementById("okf-explorer"), "min-width", "0px"],
    [doc.getElementById("okf-context"), "min-width", "0px"],
    [doc.querySelector("#okf-explorer .okf-tree-link"), "overflow", "hidden"],
    [doc.querySelector("main"), "overflow-wrap", "break-word"],
    [doc.querySelector("#okf-body p"), "overflow-wrap", "break-word"],
    [doc.querySelector("main table.frontmatter td"), "overflow-wrap", "anywhere"],
  ];
  for (const [el, prop, value] of expected) {
    assert(el, `an element this case needs is missing (${prop}: ${value})`);
    const got = window.getComputedStyle(el).getPropertyValue(prop);
    assert(got === value || (value === "0px" && got === "0"), `<${el.tagName.toLowerCase()} id="${el.id}" class="${el.getAttribute("class") || ""}"> ${prop}: ${got}, expected ${value}`);
  }
});

// A wide GFM table in the page scrolls inside its own box (the page does not
// widen at 390 px); the frontmatter table, outside #okf-body, stays a table.
// Declarations only: the recette measures the page's width.
checkAsync("a GFM table in the body scrolls in its own box; the frontmatter table is left alone", async () => {
  const window = await openPage("chrome-classes.html");
  const doc = window.document;
  const table = doc.querySelector("#okf-body table");
  assert(table, "the fixture lost its GFM table");
  const style = window.getComputedStyle(table);
  assert(style.getPropertyValue("display") === "block" && style.getPropertyValue("overflow-x") === "auto",
    `#okf-body table: display ${style.getPropertyValue("display")}, overflow-x ${style.getPropertyValue("overflow-x")}`);
  const front = window.getComputedStyle(doc.querySelector("main table.frontmatter"));
  assert(front.getPropertyValue("display") === "table" && front.getPropertyValue("overflow-x") !== "auto",
    `the frontmatter table changed (display ${front.getPropertyValue("display")}, overflow-x ${front.getPropertyValue("overflow-x")})`);
});

// === P1.1 cases: one block per task; a task writes only under its own line ===
// --- Task 3: CSS foundation ---

// --- Task 4: index v2 ---

// --- Task 6: OkfShapes (control 10) ---

// --- Task 7: theme button ---

// --- Task 8: contents, current section ---

// --- Task 10: fonts ---

// --- Task 11: shell and header ---

// --- Task 12: page head ---

// --- Task 13: explorer ---

// --- Task 14: palette ---

// --- Task 15: okf-page.js (control 11) ---

// --- Task 16: fonts fallback (only if Task 10 found a browser blocking the fonts) ---

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
    const returned = mod.register(SLICE_HELPERS);
    if (returned && typeof returned.then === "function") {
      // An async register can register (or skip) its cases after the run has
      // finished, or reject unseen: refuse it instead of losing its cases.
      returned.then(undefined, () => {});
      failures++;
      console.log(`FAIL  - cases/${name}: register(h) must be synchronous (it returned a promise)`);
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
