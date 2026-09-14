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

console.log(`\n${passed} passed, ${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
