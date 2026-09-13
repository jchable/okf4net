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
  // only the disallowed element itself and unwraps its already-sanitized
  // children in its place, see the "unwrap" comment in sanitize() below.
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
  // through to the same generic "drop element, unwrap its children" branch
  // as any other disallowed element -- it gets no special treatment for
  // having almost qualified.
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

  // Tags whose element content is source, not prose, plus tags whose content
  // must never be exposed as page text at all. SCRIPT, STYLE, IFRAME,
  // NOEMBED, NOFRAMES, XMP and PLAINTEXT are all "raw text" elements per the
  // HTML parsing spec (PLAINTEXT more so: once the tokenizer sees a
  // `<plaintext>` start tag it never leaves the PLAINTEXT state again for the
  // rest of the document, so a `<plaintext>` mid-body drops everything after
  // it, not just its own tag -- see the CHANGELOG for this task), so
  // `.textContent` on one of these returns its literal source verbatim,
  // unparsed. The generic disallowed-element branch below unwraps and keeps
  // an element's *sanitized descendant elements and text* so a wrapper like
  // `<div>` or `<details>` does not silently delete the prose (or a link, or
  // a table) it wraps -- but there is no prose inside any of the tags below
  // to preserve, only source text, and exposing that source as visible page
  // content (e.g. a raw `<script>alert(1)</script>` turning into the literal
  // words "alert(1)" on the page, or an <iframe>'s markup leaking the same
  // way) is never the right outcome. Killing cases for every entry above
  // this line live in tools/viewer-security-check/run.js (deleting an entry
  // from this table and re-running the harness must turn it red); TEMPLATE
  // and BASE below are deliberately not accompanied by a killing case --
  // removing either from this table does NOT turn the harness red, and that
  // is expected, not a gap: TEMPLATE's content lives in a separate inert
  // document fragment that `querySelectorAll` never walks into in the first
  // place (nothing to unwrap, killed or not), and BASE has no children to
  // unwrap either (it is a redirect vector via its own `href`, not a
  // container). Both stay in this table as defence in depth against that
  // reasoning changing under a future browser/jsdom behaviour, not because
  // today's harness can distinguish "present" from "absent" for them. These
  // tags are removed entirely, before the disallowed-element branch below
  // ever runs, so their content can never reach an unwrap -- including when
  // a *non-opaque* disallowed wrapper is itself nested inside one of these
  // (e.g. `<iframe><div>x</div></iframe>`): the DIV gets queued for the
  // unwrap phase like any other disallowed element, but by the time that
  // phase runs, IFRAME's removal has already detached the whole subtree
  // (DIV included) from the document, which the unwrap phase checks for --
  // see the `isConnected` comment in sanitize() below. Compared via
  // `.toUpperCase()` on `tagName`, not a bare equality/lookup on `tagName`
  // itself: a foreign-namespace element (SVG, MathML) reports a lowercase
  // `tagName` (e.g. `"script"`, not `"SCRIPT"`), and a `<script>` nested
  // inside an `<svg>` is still a script.
  var OPAQUE_TAGS = {
    SCRIPT: 1, STYLE: 1, IFRAME: 1, NOEMBED: 1, NOFRAMES: 1,
    XMP: 1, PLAINTEXT: 1, TEMPLATE: 1, BASE: 1,
  };

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
    // Own-property lookup, for the same reason as every other allowlist
    // table in this file: a URL literally beginning "constructor:" or
    // "__proto__:" must not resolve an inherited Object.prototype member.
    return Object.prototype.hasOwnProperty.call(SAFE_SCHEMES, scheme[1].toLowerCase() + ":");
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
      // Own-property lookups, not bare `allowed[name]`/`URL_ATTRS[name]`:
      // a bracket lookup on a plain object also resolves inherited
      // Object.prototype members, so an attribute literally named
      // "constructor", "__proto__", "hasOwnProperty", etc. would otherwise
      // read back a function/object instead of undefined and pass a truthy
      // check it never earned a table entry for.
      if (!Object.prototype.hasOwnProperty.call(allowed, name)) {
        el.removeAttribute(names[j]);
        continue;
      }
      if (
        Object.prototype.hasOwnProperty.call(URL_ATTRS, name) &&
        !isSafeUrl(el.getAttribute(names[j]))
      ) {
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
    // Own-property lookups for the same reason as sanitizeAttributes above:
    // a tag or attribute value named "constructor" (etc.) must not resolve
    // an inherited Object.prototype member instead of "no entry here".
    var constraint = Object.prototype.hasOwnProperty.call(TAG_VALUE_CONSTRAINTS, node.tagName)
      ? TAG_VALUE_CONSTRAINTS[node.tagName]
      : null;
    if (!constraint) { return true; }
    var actual = (node.getAttribute(constraint.attr) || "").toLowerCase();
    return Object.prototype.hasOwnProperty.call(constraint.values, actual);
  }

  function sanitize(root) {
    // Snapshot every descendant element up front: removing/unwrapping a
    // disallowed element below mutates the tree, which would desync a live
    // traversal (and querySelectorAll always returns elements in document
    // order -- parents before their descendants -- which phase 2 below
    // relies on).
    //
    // Two phases, not one, and each walks the snapshot in a different
    // direction, for a performance reason with no correctness stake in
    // it -- an earlier one-phase version of this function unwrapped
    // disallowed elements immediately, back-to-front (innermost first), and
    // that ordering used to matter because an ancestor read `.textContent`
    // to decide what to keep. That read is gone (unwrapping moves live
    // nodes, it never re-derives text), so back-to-front is no longer
    // required for correctness -- but it is actively harmful for
    // performance if kept for the unwrap step itself: unwrapping innermost
    // first means every surviving disallowed ancestor re-moves the *entire*
    // already-unwrapped subtree of everything below it up one more level,
    // which is quadratic in nested-wrapper depth times child width. Measured
    // against 500 nested disallowed `<div>`s wrapping 20,000 allowed
    // `<em>`s: back-to-front unwrap ~3s in Chromium (~55ms for the
    // equivalent old flatten-to-text code, ~40s in jsdom for a smaller but
    // still illustrative 1000x1 case); unwrapping outermost-first instead
    // brings that down to real-browser sub-second time for the same shape
    // (measured via Playwright against a real Chromium, not jsdom -- see
    // task-D1-report.md), because each element then moves its *own* direct
    // children exactly once, never a subtree assembled by earlier unwraps.
    // See the nested-wrapper harness case below for the linearity proof kept
    // under CI (sized down from the shape above so it runs in seconds under
    // jsdom, which has its own, unrelated superlinear cost for large DOM
    // mutations that a real browser does not -- see that case's own comment).
    //
    // Phase 1 (back-to-front, as before): remove opaque elements, sanitize
    // attributes and force `disabled` on admitted elements, and COLLECT
    // (do not yet unwrap) every disallowed element, into `toUnwrap`. Two
    // separate reasons phase 1 still has to walk back-to-front, both load
    // bearing:
    //   1. It guarantees an opaque removal always happens after everything
    //      nested inside it has already been classified (see the
    //      `isConnected` comment in phase 2 for why that ordering is exactly
    //      what phase 2 needs to detect).
    //   2. Walking back-to-front is what makes `toUnwrap` come out in
    //      *descending* document order (deepest queued first), which phase 2
    //      below relies on to iterate it in reverse and get outermost-first
    //      -- walking phase 1 ascending instead silently reverses that
    //      assumption and phase 2 goes back to unwrapping innermost-first:
    //      output still correct, but the quadratic cost is back. Confirmed
    //      by mutation-testing this exact change against the harness; the
    //      nested-wrapper case below is what catches it.
    var all = root.querySelectorAll("*");
    var toUnwrap = [];
    for (var i = all.length - 1; i >= 0; i--) {
      var node = all[i];
      // node.tagName.toUpperCase(), not a bare comparison: a foreign-namespace
      // element (SVG, MathML) reports a lowercase tagName, so an SVG-nested
      // <script> ("script") must still match the OPAQUE_TAGS entry ("SCRIPT").
      if (Object.prototype.hasOwnProperty.call(OPAQUE_TAGS, node.tagName.toUpperCase())) {
        node.parentNode.removeChild(node);
        continue;
      }
      var admitted = Object.prototype.hasOwnProperty.call(ALLOWED_TAGS, node.tagName)
        && passesTagValueConstraint(node);
      if (!admitted) {
        // Queue for phase 2 rather than unwrapping here -- see the block
        // comment above this loop for why phase 2 needs its own pass, in
        // the opposite direction, to stay linear. This also covers an INPUT
        // that failed TAG_VALUE_CONSTRAINTS (any type other than checkbox):
        // it gets no special treatment for having almost qualified as an
        // allowed tag. Opaque tags (SCRIPT/STYLE/IFRAME/etc.) never reach
        // this branch at all -- they were removed above, so their raw
        // source text can never be exposed by phase 2's unwrap.
        toUnwrap.push(node);
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
    // Phase 2: unwrap the collected elements in DOCUMENT order (outermost
    // first), so each element's direct children move exactly once -- linear
    // in total node count rather than quadratic in depth times width. Every
    // node was already sanitized in phase 1, so phase 2 only re-parents
    // already-clean nodes; it never re-examines a tag, attribute, or URL.
    // `toUnwrap` was built by walking `all` back-to-front, so it holds
    // disallowed elements in *descending* document order (deepest queued
    // first) -- iterate it in reverse to get outermost first.
    for (var k = toUnwrap.length - 1; k >= 0; k--) {
      var disallowed = toUnwrap[k];
      // A queued element can be detached by the time phase 2 reaches it:
      // if a disallowed, non-opaque wrapper is itself nested inside an
      // OPAQUE ancestor -- e.g. `<iframe><div>x</div></iframe>` -- phase 1
      // classifies the inner DIV (a descendant, visited first in the
      // back-to-front walk) and queues it here *before* it later reaches
      // the outer IFRAME and removes it, which detaches DIV's whole subtree
      // from the document along with it. Unwrapping a detached node would
      // be harmless (it only rearranges an already-invisible subtree that
      // nothing under `root` can ever reference again) but it is still
      // pointless work on every such subtree, so skip it explicitly rather
      // than silently rely on that harmlessness. `isConnected` (not a bare
      // `.parentNode` truthiness check) is what actually detects this: a
      // detached node's `.parentNode` is still non-null -- it points at
      // whatever removed ancestor it was nested under -- so only a real
      // "is this reachable from a Document" check catches the case.
      if (!disallowed.isConnected) { continue; }
      var parent = disallowed.parentNode;
      // Move every child in one structural operation via a detached
      // DocumentFragment, rather than one insertBefore call per child
      // directly against the live, attached tree: an element with many
      // direct children (concretely, the innermost of many nested
      // disallowed wrappers, which by the time phase 2 reaches it holds
      // everything every wrapper above it ever contained) costs one
      // attached-tree mutation per child with the naive loop -- measured
      // far slower, on both jsdom and a real engine, than moving the same
      // children into a fragment (cheap: the fragment has no document to
      // notify) and swapping the whole fragment in with a single
      // replaceChild call.
      var fragment = disallowed.ownerDocument.createDocumentFragment();
      while (disallowed.firstChild) { fragment.appendChild(disallowed.firstChild); }
      parent.replaceChild(fragment, disallowed);
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

  // Load-bearing: the sanitized tree is moved into the live page with
  // appendChild (live DOM nodes) and is NEVER serialized back to an HTML
  // string and re-parsed anywhere in this file. That matters because
  // unwrapping in sanitize() above can legally build tag shapes the HTML
  // parser itself would never produce in one pass -- e.g. a <table> ending
  // up as a direct child of a <p> (a parser would close the <p> first), or
  // an <a> nested inside another <a> (the parser auto-closes an open <a>
  // before opening a new one). Those shapes are harmless exactly because
  // nothing downstream re-parses them: `target.innerHTML = clean.innerHTML`
  // here, or any other round trip through a markup string, would hand such
  // a shape back to an HTML parser, which "fixes" it by re-nesting/moving
  // nodes according to its own tree-construction rules (the same class of
  // mutation-XSS behaviour -- parser normalization changing the tree after
  // an already-sanitized pass -- that motivates the mutation-XSS harness
  // cases below in run.js) instead of preserving it as authored here.
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
