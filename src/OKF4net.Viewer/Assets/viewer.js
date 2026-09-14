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
  // tags are removed with their whole subtree in sanitize()'s first phase,
  // before anything is unwrapped, so their content can never reach an
  // unwrap -- including a *non-opaque* disallowed element nested inside one
  // of them. In HTML that nesting cannot come from `<iframe><div>` (an
  // iframe's content is raw text, so no DIV element is ever created), but
  // foreign content does produce it: in
  // `<svg><style><a><g>x</g></a></style></svg>` the SVG <style>'s children
  // parse as real elements. The first phase marks <a> and <g> for unwrapping
  // before it reaches and removes <style>; the unwrap phase walks only what
  // is still under the root, so it never meets them. Compared via
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
    // Phase 1 decides and cleans; phase 2 only restructures.
    //
    // Phase 1 walks a static snapshot of every descendant element
    // (querySelectorAll lists them in document order) back-to-front, so
    // every element is visited after all of its descendants. Each element is
    // either removed outright with its whole subtree (OPAQUE_TAGS), or
    // admitted and cleaned in place (attribute allowlist, URL check, forced
    // `disabled`), or marked for unwrapping (every other element). The
    // back-to-front order is load-bearing for cost: it removes every opaque
    // subtree once, before any enclosing opaque element is removed, so no
    // removal drags nodes an earlier removal already took and opaque
    // removals stay within N nodes dragged in total. A front-to-back walk
    // removes the outermost opaque element first and then each nested one
    // again from its already-detached parent -- quadratic for nested opaque
    // elements: run.js's unwrap-cost case with 80 nested <style>s inside
    // <svg> measures 39.9 x N front-to-back against 1.0 x N back-to-front.
    var all = root.querySelectorAll("*");
    var unwrapAt = [];
    var unwrapCount = 0;
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
        // Mark for phase 2 rather than unwrapping here: unwrapping one
        // element at a time is what made the unwrap superlinear (see phase
        // 2). This also covers an INPUT that failed TAG_VALUE_CONSTRAINTS
        // (any type other than checkbox): it gets no special treatment for
        // having almost qualified as an allowed tag. Its attributes are never
        // cleaned, because the element itself never reaches the page. Opaque
        // tags (SCRIPT/STYLE/IFRAME/etc.) never reach this branch at all --
        // they were removed above, so their raw source text can never be
        // exposed by phase 2's unwrap.
        unwrapAt[i] = true;
        unwrapCount++;
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
    if (unwrapCount === 0) { return root; }

    // Phase 2 unwraps every marked element: the element goes, its children
    // take its place, in order. Moving a node in the DOM drags its whole
    // subtree with it (the DOM Standard's remove and insert algorithms each
    // visit every descendant of the node they move), so what matters is not
    // how many moves are made but how many nodes they drag. Unwrapping
    // marked elements one at a time drags the same nodes again and again,
    // whatever the order: innermost-first re-moves the content below a
    // wrapper once for every wrapper above it (D wrappers around M nodes of
    // content: about D x M), and outermost-first moves each wrapper's
    // children as whole subtrees, the next wrapper included (a chain of D
    // wrappers: about D x N). Both were
    // committed in intermediate, unreleased versions of this change; the
    // unwrap-cost cases in tools/viewer-security-check/run.js measure the
    // innermost-first one at 44.5 x N for 50 wrappers around 200 children,
    // and the outermost-first one at 94.2 x N for that shape and 201.0 x N
    // for a 200-deep chain.
    //
    // So phase 2 instead records, in one walk over every node still under
    // `root`, each node's current parent and its parent-to-be (the nearest
    // ancestor that is not being unwrapped, or `root`); then detaches every
    // node bottom-up (last in document order first, so each node has no
    // children left when it is removed) and re-appends every kept node
    // top-down to its parent-to-be (first in document order first, so each
    // node has no children yet when it is appended). Every unwrap mutation
    // thus moves a single childless node: one removal per node and at most
    // one insertion, so phase 2 drags at most 2 x N nodes (N = nodes under
    // `root`). Phase 1's opaque removals do drag whole subtrees, at most N
    // nodes more in total thanks to its back-to-front order (see above). The
    // run.js unwrap-cost cases count exactly this -- every node dragged by
    // every structural mutation during sanitize(), with the other generic
    // node-moving APIs made to throw -- and assert it stays within 4 x N on
    // four shapes (measured: 1.9, 1.0, 1.5 and 1.0 x N). The walk
    // itself does constant work per node.
    //
    // The walk uses a TreeWalker rooted at `root`, so it meets exactly the
    // nodes under `root` and nothing else. That is also what keeps this
    // fail-closed: an element marked in phase 1 and later removed with an
    // opaque ancestor (in HTML, `<iframe><div>` cannot produce one, an
    // iframe's content being raw text, but foreign content can:
    // `<svg><style><a><g>x</g></a></style></svg>`, where the SVG <style>'s
    // children parse as elements) is simply never met, and every marked
    // element that IS under `root` is always unwrapped, whether or not
    // `root` itself is attached to a document. (An earlier version skipped
    // marked elements that were not `isConnected`, which on a detached root
    // kept every one of them, attributes uncleaned; run.js has a case.)
    //
    // Marks are matched to the walk by identity, merge-style, rather than
    // with a Set or Map (this file sticks to ES5): take a second snapshot of
    // the elements still under `root` -- a subsequence of `all`, in the same
    // order, since phase 1 only removed subtrees -- and carry each mark over
    // with one forward pointer into `all`; the walk then meets those same
    // elements in that same order, so a second forward pointer tells it
    // which node is the next element and whether it is marked. Both
    // pointers only move forward, so the matching is linear too.
    var live = root.querySelectorAll("*");
    var liveUnwrap = [];
    var fromAll = 0;
    for (var l = 0; l < live.length; l++) {
      while (fromAll < all.length && all[fromAll] !== live[l]) { fromAll++; }
      if (fromAll === all.length) {
        // Unreachable (every element under root was in the phase 1
        // snapshot), but an element phase 1 never examined must never be
        // rendered: abort instead.
        throw new Error("sanitize: an element escaped phase 1");
      }
      liveUnwrap[l] = unwrapAt[fromAll] === true;
      fromAll++;
    }

    var nodes = [];
    var oldParents = [];
    var newParents = [];
    var keep = [];
    // One entry per open level of the walk: the parent the nodes at that
    // level have now, and the one they will have after unwrapping.
    var oldParentStack = [root];
    var newParentStack = [root];
    var nextElement = 0;
    var walker = root.ownerDocument.createTreeWalker(root, NodeFilter.SHOW_ALL);
    var current = walker.firstChild();
    while (current) {
      var unwrap = false;
      if (nextElement < live.length && current === live[nextElement]) {
        unwrap = liveUnwrap[nextElement];
        nextElement++;
      } else if (current.nodeType === 1) {
        // Unreachable (the walk and `live` list the same elements in the
        // same order), but an element that is not the next `live` entry has
        // no classification: keeping it would render it uncleaned, so abort.
        throw new Error("sanitize: the phase 2 walk met an unclassified element");
      }
      var newParent = newParentStack[newParentStack.length - 1];
      nodes.push(current);
      oldParents.push(oldParentStack[oldParentStack.length - 1]);
      newParents.push(newParent);
      keep.push(!unwrap);
      var firstChild = walker.firstChild();
      if (firstChild) {
        oldParentStack.push(current);
        newParentStack.push(unwrap ? newParent : current);
        current = firstChild;
        continue;
      }
      current = null;
      for (;;) {
        var sibling = walker.nextSibling();
        if (sibling) { current = sibling; break; }
        var up = walker.parentNode();
        if (up === null || up === root) { break; }
        oldParentStack.pop();
        newParentStack.pop();
      }
    }
    if (nextElement !== live.length) {
      // Unreachable for the same reason. Together with the check inside the
      // walk, this verifies both directions: every element the walk met was
      // the next `live` entry, and every `live` entry was met.
      throw new Error("sanitize: the phase 2 walk missed an element");
    }

    // Parents come from the walk rather than from reading `parentNode` back
    // off each node: when a node is detached, only nodes after it in
    // document order have moved, so its recorded parent is still its parent.
    for (var d = nodes.length - 1; d >= 0; d--) {
      oldParents[d].removeChild(nodes[d]);
    }
    for (var n = 0; n < nodes.length; n++) {
      if (keep[n]) { newParents[n].appendChild(nodes[n]); }
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
