// SPDX-License-Identifier: LGPL-3.0-or-later
// Client bootstrap for a generated OKF bundle page: read the embedded JSON
// payload, render its markdown, sanitize the result, then rewire
// inter-concept links.
(function () {
  "use strict";

  // Raw HTML in bundle markdown is neutralized by sanitizing the *parsed
  // DOM*, not by trying to suppress raw-HTML tokens at the markdown-renderer
  // level. That is a deliberate choice, not an oversight: marked (the
  // vendored renderer, v15.0.12) has no `sanitize` option any more, and an
  // earlier version of this file patched its renderer hooks instead
  // (`Renderer.html`, `TextRenderer.html`, both set to return `""`). That
  // approach was tried and dropped, because it cannot bound the actual
  // attack surface: marked's `Renderer.image()` interpolates the `alt`
  // attribute with NO escaping call at all, and `TextRenderer.text()`
  // returns raw, unescaped text. So a plain markdown image with no HTML in
  // it at all -- `![foo" onerror="alert(1)](x.png)` -- breaks out of the
  // alt attribute and adds a live `onerror` handler that fires on page load,
  // with no raw-HTML token anywhere a renderer-hook override could ever see.
  // No enumeration of renderer hooks closes this class of bug in general,
  // because the underlying defect is marked emitting an attribute value
  // with missing or incomplete escaping; there is no way to list every
  // place that might happen, now or in a future marked version.
  //
  // Measured against the vendored build plus the hostile-payload battery in
  // tools/viewer-security-check/: the renderer-hook approach neutralized
  // every one of those payloads, exactly as well as sanitizing the DOM
  // directly does, but it also silently destroyed benign content the
  // sanitizer below leaves intact -- e.g. `<details><summary>Resume</summary
  // >corps important</details>` rendered as `""` (all of it gone) instead of
  // `"Resumecorps important"`. A hook that returns `""` for any raw-HTML
  // token cannot distinguish "this token is dangerous" from "this token
  // merely wraps ordinary prose", so patching marked bought no security
  // property the sanitizer below lacks, while adding a real content-loss
  // bug. Sanitizing the parsed DOM does not have that failure mode: it drops
  // only the disallowed element itself and keeps its text, see the "keep
  // its text" comment in sanitize() below.
  //
  // So the DOM sanitizer below is the whole defense, not one layer of it:
  // allowlist which tags may exist, allowlist which attributes each
  // surviving tag may carry (an allowlist, not an on*/javascript:-style
  // blocklist, so a novel attribute or a differently-cased scheme is
  // dropped by default instead of trusted by default), constrain a handful
  // of attribute values where the name alone is not a strong enough gate
  // (e.g. `<input type=checkbox>` vs. any other input type), and validate
  // the URL scheme of anything that can navigate or fetch (href/src).
  var ALLOWED_TAGS = {
    P: 1, H1: 1, H2: 1, H3: 1, H4: 1, H5: 1, H6: 1,
    UL: 1, OL: 1, LI: 1, A: 1, IMG: 1, CODE: 1, PRE: 1, BLOCKQUOTE: 1,
    TABLE: 1, THEAD: 1, TBODY: 1, TFOOT: 1, TR: 1, TH: 1, TD: 1,
    STRONG: 1, EM: 1, DEL: 1, HR: 1, BR: 1,
    // INPUT is gated further by TAG_VALUE_CONSTRAINTS below: being on this
    // list is necessary but not sufficient for an <input> to survive.
    INPUT: 1,
  };

  // Some tags on ALLOWED_TAGS above are not safe to admit unconditionally --
  // INPUT is a live form control in general, and only a checkbox (rendered
  // read-only, see the forced `disabled` below) is inert enough for this
  // read-only viewer. Declared here as data (the attribute to read plus the
  // set of values that qualify), not as a predicate function or an
  // `if (tagName === "INPUT")` branch in sanitize(), so a future tag that
  // needs the same kind of constraint is a table entry, not new code. An
  // element whose tag is on ALLOWED_TAGS but fails the constraint here falls
  // through to the same generic "drop element, keep text" branch as any
  // other disallowed element -- it gets no special treatment for having
  // almost qualified.
  var TAG_VALUE_CONSTRAINTS = {
    INPUT: { attr: "type", values: { checkbox: 1 } },
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
    // `type` survives here unconstrained -- TAG_VALUE_CONSTRAINTS above has
    // already thrown away the whole element unless its value is "checkbox"
    // before this list is ever consulted, so by the time an INPUT reaches
    // sanitizeAttributes() the only value `type` can carry is the safe one.
    INPUT: { type: 1, disabled: 1, checked: 1 },
  };

  // Attributes in this set additionally have their value validated as a
  // URL once the tag/attribute allowlists above have already let them
  // through.
  var URL_ATTRS = { href: 1, src: 1 };
  var SAFE_SCHEMES = { "http:": 1, "https:": 1, "mailto:": 1 };

  // Tags whose element content is source, not prose: SCRIPT and STYLE are
  // "raw text" elements per the HTML parsing spec, so `.textContent` on one
  // returns its literal script/CSS source verbatim, unparsed. The generic
  // disallowed-element branch below keeps `.textContent` specifically so a
  // wrapper like `<div>` or `<details>` does not silently delete the prose
  // it wraps -- but there is no prose inside a SCRIPT or STYLE element to
  // preserve, only code, and dumping that code as visible page text (e.g. a
  // raw `<script>alert(1)</script>` turning into the literal words
  // "alert(1)" on the page) is never the right outcome. These tags are
  // dropped with no replacement at all instead of falling into the
  // keep-the-text branch.
  var OPAQUE_TAGS = { SCRIPT: 1, STYLE: 1 };

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
    var allowed = Object.prototype.hasOwnProperty.call(ALLOWED_ATTRS, el.tagName)
      ? ALLOWED_ATTRS[el.tagName]
      : {};
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

  // Applies TAG_VALUE_CONSTRAINTS to a tag that is otherwise on ALLOWED_TAGS.
  // Returns true when the element qualifies as its tag unconditionally (no
  // entry in the table) or its gating attribute's value is in the allowed
  // set for that tag; false means it must be treated exactly like a tag
  // that was never on ALLOWED_TAGS at all.
  function passesTagValueConstraint(node) {
    var constraint = TAG_VALUE_CONSTRAINTS[node.tagName];
    if (!constraint) { return true; }
    var actual = (node.getAttribute(constraint.attr) || "").toLowerCase();
    return !!constraint.values[actual];
  }

  function sanitize(root) {
    // Snapshot every descendant element up front: replacing a disallowed
    // element below mutates the tree, which would desync a live traversal.
    // Walk the snapshot back to front rather than in document order: for any
    // element, all of its descendants precede it in document order, so
    // processing the array in reverse guarantees every descendant has
    // already been resolved (dropped opaque, flattened to text, or
    // sanitized in place) by the time an ancestor reads `.textContent` for
    // itself below. That matters concretely for something like
    // `<math><mtext><script>alert(1)</script></mtext></math>`: without this
    // ordering, MATH's own `.textContent` read (while deciding what text to
    // keep) would still see SCRIPT's raw, un-dropped source, because
    // `.textContent` walks the live DOM directly and does not know this
    // function has plans to remove SCRIPT later in the same pass.
    var all = root.querySelectorAll("*");
    for (var i = all.length - 1; i >= 0; i--) {
      var node = all[i];
      if (!node.parentNode) { continue; } // already detached by a descendant/ancestor's removal
      if (Object.prototype.hasOwnProperty.call(OPAQUE_TAGS, node.tagName)) {
        node.parentNode.removeChild(node);
        continue;
      }
      var admitted = Object.prototype.hasOwnProperty.call(ALLOWED_TAGS, node.tagName)
        && passesTagValueConstraint(node);
      if (!admitted) {
        // Drop the element but keep its text so a disallowed wrapper does
        // not silently delete surrounding prose; nothing about its markup
        // (attributes, nested elements) survives the swap. This also covers
        // an INPUT that failed TAG_VALUE_CONSTRAINTS (any type other than
        // checkbox): it gets no special treatment for having almost
        // qualified as an allowed tag.
        var replacement = node.ownerDocument.createTextNode(node.textContent || "");
        node.parentNode.replaceChild(replacement, node);
        continue;
      }
      sanitizeAttributes(node);
      if (node.tagName === "INPUT") {
        // The only INPUT shape that reaches here is a checkbox (see
        // TAG_VALUE_CONSTRAINTS above). GFM task-list checkboxes
        // (`- [ ] foo` / `- [x] foo`) already render disabled from marked,
        // but a bundle can also emit raw `<input type="checkbox">` HTML
        // directly with no `disabled` attribute at all -- force it here
        // rather than trusting the source, since this is a read-only viewer
        // and a live, focusable checkbox has no legitimate use in it.
        node.setAttribute("disabled", "disabled");
      }
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
  //
  // Known limitation: a link written with angle-bracket syntax --
  // `[x](<../glossary/term.md>)` -- is never rewired. The C# side that
  // builds `payload.links` rejects the `<`-prefixed segment, so it never
  // enters this table, while marked still emits `href="../glossary/term.md"`
  // verbatim (without the angle brackets). The result is a link to a `.md`
  // file that does not exist in the generated site: it fails open (a dead
  // link, not a broken page), and the syntax is rare enough that this has
  // been left as a documented gap rather than fixed.
  //
  // Known limitation, same shape: a reference-style link -- `[x][ref]`
  // with a separate `[ref]: ../glossary/term.md` definition elsewhere in
  // the body -- is never rewired either. `LinkScanner` (src/OKF4net/Links.cs)
  // only extracts inline `[text](dest)` links, so a reference-style target
  // never reaches `payload.links` at all, while marked still resolves the
  // reference and emits `href="../glossary/term.md"` verbatim. Same failure
  // mode as the angle-bracket case: a dead link to a `.md` file that does
  // not exist in the generated site, and no backlink recorded on the
  // target. Latent rather than fixed here -- a full rendering of both
  // bundles/ga4 and bundles/acme_retail produced zero surviving `.md`
  // anchors, so this has not been observed to matter on a real bundle, but
  // the gap is real. A possible cheap general safety net for both of these
  // gaps, not implemented here: after this rewiring loop runs, any
  // remaining anchor whose href still ends in `.md` is by construction a
  // link the table missed (every href this loop actually rewires becomes
  // `.html`), so a follow-up pass could flag or neutralize those on sight
  // without needing to know *why* each one was missed.
  var map = payload.links || {};
  var anchors = target.getElementsByTagName("a");
  for (var i = 0; i < anchors.length; i++) {
    var raw = anchors[i].getAttribute("href");
    if (!raw) { continue; }
    var key = raw;
    if (!Object.prototype.hasOwnProperty.call(map, key)) {
      // marked's cleanUrl() step (see the big sanitizer comment above) runs
      // encodeURI() over every link destination before it reaches the DOM,
      // while the generation-time table below is keyed by the raw,
      // un-encoded destination. Given ConceptId's restricted segment
      // charset, the only place a target can carry a character encodeURI
      // would rewrite is its #fragment -- e.g. `a/b.md#café` reaches here
      // as `a/b.md#caf%C3%A9`, missing the raw key by construction even
      // though the target is perfectly valid. Retry once with the encoding
      // undone before giving up on the lookup.
      try {
        var decoded = decodeURI(key);
        if (Object.prototype.hasOwnProperty.call(map, decoded)) { key = decoded; }
      } catch (e) {
        // Malformed percent-encoding: nothing left to recover, fall through
        // to the miss below.
      }
    }
    if (!Object.prototype.hasOwnProperty.call(map, key)) { continue; }
    var entry = map[key];
    if (entry.exists) {
      anchors[i].setAttribute("href", entry.href);
    } else {
      anchors[i].removeAttribute("href");
      anchors[i].className = "broken";
      anchors[i].setAttribute("title", "broken link: " + raw);
    }
  }
})();
