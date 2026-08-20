// SPDX-License-Identifier: LGPL-3.0-or-later
// Client bootstrap for a generated OKF bundle page: read the embedded JSON
// payload, render its markdown, sanitize the result, then rewire
// inter-concept links.
(function () {
  "use strict";

  // --- Layer 1 (defence in depth, NOT the control that holds): neuter
  // marked's own raw-HTML passthrough. marked renders raw HTML by default
  // and no longer exposes a `sanitize` option, so this suppresses it at the
  // renderer level. Verified against the vendored build (marked v15.0.12):
  // both block-level raw HTML (`<script>...</script>` as its own block) and
  // inline raw HTML (`<img onerror=...>` inside a paragraph) are dispatched
  // through this same `renderer.html` hook, so one override covers both.
  //
  // This layer is NOT sufficient by itself: marked also builds some
  // attributes (image alt/title) with no escaping call at all, so a
  // markdown-native construct containing no raw HTML token at all can
  // still break out of an attribute. See the sanitizer below, which is
  // the control that actually closes that gap.
  marked.use({
    renderer: {
      html: function () { return ""; },
    },
  });

  // marked v15 renders image/title alt-text through a second, separate
  // TextRenderer instance (marked.TextRenderer) that the override above
  // does not touch: it is constructed fresh internally and never reads
  // back from options. This closes the html-token half of that path
  // (for example: an image alt-text containing an anchor tag with an
  // onclick attribute). It does NOT close plain-text alt-text attribute
  // breakout; see the sanitizer below.
  if (marked.TextRenderer) {
    marked.TextRenderer.prototype.html = function () { return ""; };
  }

  // --- Layer 2, the control that actually holds: sanitize the parsed DOM
  // before it goes anywhere near the live page.
  //
  // marked's Renderer.image() interpolates the alt attribute with NO
  // escaping call at all, and TextRenderer.text() returns raw, unescaped
  // text. So a plain markdown image with no HTML in it at all can break
  // out of the alt attribute and add a live onerror handler that fires on
  // page load (the image fails to resolve), with no click and nothing an
  // html-token renderer override could ever see. No amount of patching
  // marked's renderer hooks closes this class of bug in general, because
  // the underlying defect is marked emitting an attribute value with
  // missing or incomplete escaping; there is no way to enumerate every
  // place that might happen, now or in a future marked version.
  //
  // The fix that generalizes is sanitizing the parsed markup itself:
  // allowlist which tags may exist, allowlist which attributes each
  // surviving tag may carry (an allowlist, not an on*/javascript:-style
  // blocklist, so a novel attribute or a differently-cased scheme is
  // dropped by default instead of trusted by default), and validate the
  // URL scheme of anything that can navigate or fetch (href/src).
  var ALLOWED_TAGS = {
    P: 1, H1: 1, H2: 1, H3: 1, H4: 1, H5: 1, H6: 1,
    UL: 1, OL: 1, LI: 1, A: 1, IMG: 1, CODE: 1, PRE: 1, BLOCKQUOTE: 1,
    TABLE: 1, THEAD: 1, TBODY: 1, TFOOT: 1, TR: 1, TH: 1, TD: 1,
    STRONG: 1, EM: 1, DEL: 1, HR: 1, BR: 1,
  };

  // Per-tag attribute allowlist. Anything not listed here for a given tag
  // is stripped regardless of its name: deliberately an allowlist, so an
  // event-handler name or trick this list's author never thought of is
  // dropped by default rather than let through by default.
  var ALLOWED_ATTRS = {
    A: { href: 1, title: 1 },
    IMG: { src: 1, alt: 1, title: 1 },
    CODE: { class: 1 },
    TH: { align: 1 },
    TD: { align: 1 },
  };

  // Attributes in this set additionally have their value validated as a
  // URL once the tag/attribute allowlists above have already let them
  // through.
  var URL_ATTRS = { href: 1, src: 1 };
  var SAFE_SCHEMES = { "http:": 1, "https:": 1, "mailto:": 1 };

  // Strips every ASCII control character and space (code points 0 to 32
  // inclusive, plus DEL, 127) wherever it appears. Built with charCodeAt
  // rather than a regex escape class, so this source file carries no raw
  // control bytes of its own. Browsers ignore these characters inside a
  // URL, which is exactly how a stray tab or newline hidden inside a
  // javascript scheme tries to slip past a naive prefix check: stripped,
  // it collapses right back down to the plain scheme name and is
  // correctly rejected below. A genuine http(s) or mailto URL never needs
  // raw whitespace or control characters to be well formed, so this
  // cannot reject a legitimate link.
  function stripControlCharacters(value) {
    var stripped = "";
    for (var i = 0; i < value.length; i++) {
      var code = value.charCodeAt(i);
      if (code > 32 && code !== 127) { stripped += value.charAt(i); }
    }
    return stripped;
  }

  function isSafeUrl(raw) {
    // A missing or empty value is inert: no navigation, no fetch.
    if (!raw) { return true; }
    var value = String(raw);
    // Percent-encoding can hide a control character behind plain, printable
    // ASCII: "java%09script:alert(1)" has no literal tab for the stripper
    // above to see, only the three printable characters "%09" -- confirmed
    // against the vendored build, marked's own angle-bracket link syntax
    // (`[x](<...>)`) happily percent-encodes a raw tab exactly like this.
    // Decode repeatedly (bounded, so doubly percent-encoded input such as
    // "%2509" cannot hide behind a single decode pass either), stripping
    // control characters after every round, until decoding stops changing
    // the value or the round limit is hit, before testing the scheme.
    for (var round = 0; round < 5; round++) {
      value = stripControlCharacters(value);
      var decoded;
      try {
        decoded = decodeURIComponent(value);
      } catch (e) {
        // Malformed percent-encoding: nothing further to decode, judge the
        // string as it stands.
        break;
      }
      if (decoded === value) { break; }
      value = decoded;
    }
    var scheme = /^([a-zA-Z][a-zA-Z0-9+.-]*):/.exec(value);
    // No scheme at all: a relative path, a fragment, or a query string,
    // safe, and exactly what the link-rewiring step below expects to see
    // for in-bundle links.
    if (!scheme) { return true; }
    return !!SAFE_SCHEMES[scheme[1].toLowerCase() + ":"];
  }

  function sanitizeAttributes(el) {
    var allowed = ALLOWED_ATTRS[el.tagName] || {};
    // Snapshot names first: removing attributes while iterating the live
    // attributes NamedNodeMap skips entries.
    var names = [];
    for (var i = 0; i < el.attributes.length; i++) { names.push(el.attributes[i].name); }
    for (var j = 0; j < names.length; j++) {
      var name = names[j].toLowerCase();
      if (!allowed[name]) { el.removeAttribute(names[j]); continue; }
      if (URL_ATTRS[name] && !isSafeUrl(el.getAttribute(names[j]))) {
        el.removeAttribute(names[j]);
      }
    }
  }

  function sanitize(root) {
    // Snapshot every descendant element up front: replacing a disallowed
    // element below mutates the tree, which would desync a live traversal.
    var all = root.querySelectorAll("*");
    for (var i = 0; i < all.length; i++) {
      var node = all[i];
      if (!node.parentNode) { continue; } // already detached by an ancestor's removal
      if (!ALLOWED_TAGS[node.tagName]) {
        // Drop the element but keep its text so a disallowed wrapper does
        // not silently delete surrounding prose; nothing about its markup
        // (attributes, nested elements) survives the swap.
        var text = node.ownerDocument.createTextNode(node.textContent || "");
        node.parentNode.replaceChild(text, node);
        continue;
      }
      sanitizeAttributes(node);
    }
    return root;
  }

  var payloadEl = document.getElementById("okf-payload");
  if (!payloadEl) { return; }
  var payload = JSON.parse(payloadEl.textContent);

  // Parse into a document with no browsing context of its own: DOMParser
  // output never executes script elements and never fetches image or
  // similar resources, so sanitizing here, before anything touches the
  // live page, cannot be raced by a resource load or a handler firing
  // first.
  var parsed = new DOMParser().parseFromString(marked.parse(payload.body || ""), "text/html");
  var clean = sanitize(parsed.body);

  var target = document.getElementById("okf-body");
  target.innerHTML = "";
  while (clean.firstChild) { target.appendChild(clean.firstChild); }

  // Rewire internal links from the generation-time table. Anything absent
  // from the table (external URLs, anchors) is left exactly as authored.
  var map = payload.links || {};
  var anchors = target.getElementsByTagName("a");
  for (var i = 0; i < anchors.length; i++) {
    var raw = anchors[i].getAttribute("href");
    if (!raw || !Object.prototype.hasOwnProperty.call(map, raw)) { continue; }
    var entry = map[raw];
    if (entry.exists) {
      anchors[i].setAttribute("href", entry.href);
    } else {
      anchors[i].removeAttribute("href");
      anchors[i].className = "broken";
      anchors[i].setAttribute("title", "broken link: " + raw);
    }
  }
})();
