// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Side-effect-free helpers shared by the interactive viewer scripts
// (okf-explorer.js, okf-palette.js, okf-toc.js): reading the site index, the
// palette ranking, staleness, the one path resolver, heading slugs. Loading
// this file only defines window.OkfSite, so tools/viewer-security-check/
// calls every function directly. viewer.js does not use it: viewer.js keeps
// its { body, links } contract, which the VS Code extension reuses.
(function () {
  "use strict";

  var HEADING_PREFIX = "okf-h-";

  // The site index, or null when okf-index.js did not run or window.OKF_INDEX
  // is something else -- typically a DOM element reached through named
  // access (DOM clobbering).
  function readIndex(win) {
    var idx;
    try { idx = win.OKF_INDEX; } catch (e) { return null; }
    if (!idx || typeof idx !== "object" || "nodeType" in idx) { return null; }
    if (!Array.isArray(idx.concepts) || !Array.isArray(idx.ghosts)
        || !Array.isArray(idx.edges) || !Array.isArray(idx.tree)) { return null; }
    return idx;
  }

  // Spec §4.6: trim, collapse internal whitespace, locale-independent lower
  // case. Accents are NOT folded.
  function normalize(text) {
    return String(text).replace(/\s+/g, " ").trim().toLowerCase();
  }

  // Palette ranking (spec §4.6). Fixed tiers, no weights -- deliberately not
  // ConceptSearch: 0 exact id, 1 prefix of the title or id, 2 substring of
  // the title or id, 3 exact tag. Tiers are tried best first, so a concept
  // keeps its best one. Ties keep index order, which C# sorted with
  // ConceptId.CompareTo (spec §3.1). Returns concept positions.
  function rank(index, query) {
    var q = normalize(query);
    if (q === "") { return []; }
    var hits = [];
    for (var i = 0; i < index.concepts.length; i++) {
      var c = index.concepts[i];
      var id = normalize(c.id);
      var title = normalize(c.title);
      var tier = -1;
      if (id === q) {
        tier = 0;
      } else if (id.indexOf(q) === 0 || title.indexOf(q) === 0) {
        tier = 1;
      } else if (id.indexOf(q) !== -1 || title.indexOf(q) !== -1) {
        tier = 2;
      } else {
        for (var t = 0; t < c.tags.length; t++) {
          if (normalize(c.tags[t]) === q) { tier = 3; break; }
        }
      }
      if (tier !== -1) { hits.push({ i: i, tier: tier }); }
    }
    hits.sort(function (a, b) { return a.tier - b.tier || a.i - b.i; });
    return hits.map(function (h) { return h.i; });
  }

  // Spec §4.4: staleAfterMs is the deadline rounded UP to a whole millisecond
  // in C#, so this comparison equals Lifecycle.IsStale. It is the only part
  // of §5.5 redone in JavaScript (an exception recorded in CLAUDE.md).
  function isStale(staleAfterMs, nowMs) {
    return typeof staleAfterMs === "number" && nowMs >= staleAfterMs;
  }

  // This page's way back to the site root, as HtmlWriter wrote it. Anything
  // but a chain of "../" is refused: the resolver below concatenates it.
  function rootOf(doc) {
    var root = doc.documentElement.getAttribute("data-okf-root") || "";
    return /^(\.\.\/)*$/.test(root) ? root : "";
  }

  // The only resolver the interactive scripts use (spec §3.5): index paths
  // are relative to the site root, never to the page or to the script.
  function resolve(root, path) {
    return root + path;
  }

  // Heading slug (spec §5): letters, digits and hyphens only.
  function slugify(text) {
    var slug = normalize(text).replace(/[^\p{L}\p{N} -]/gu, "").replace(/ /g, "-");
    return slug === "" ? "section" : slug;
  }

  // Unique slugs in document order. A taken candidate keeps counting, so a
  // generated suffix never collides with a real heading: "Usage", "Usage",
  // "Usage 1" -> usage, usage-1, usage-1-1.
  function uniqueSlugs(texts) {
    var used = new Set();
    var out = [];
    for (var k = 0; k < texts.length; k++) {
      var base = slugify(texts[k]);
      var slug = base;
      var n = 0;
      while (used.has(slug)) { n++; slug = base + "-" + n; }
      used.add(slug);
      out.push(slug);
    }
    return out;
  }

  // Generated ids a URL fragment may designate, best first: an already
  // prefixed fragment as is; an author fragment ("#usage", "#Usage") as
  // written, then slugified.
  function fragmentCandidates(hash) {
    var h = String(hash || "");
    if (h.charAt(0) === "#") { h = h.slice(1); }
    try { h = decodeURIComponent(h); } catch (e) { /* keep it raw */ }
    if (h === "") { return []; }
    if (h.indexOf(HEADING_PREFIX) === 0) { return [h]; }
    var candidates = [HEADING_PREFIX + h];
    var slugged = HEADING_PREFIX + slugify(h);
    if (slugged !== candidates[0]) { candidates.push(slugged); }
    return candidates;
  }

  // Creates an element; text goes through textContent, never markup.
  function element(doc, tag, className, text) {
    var el = doc.createElement(tag);
    if (className) { el.className = className; }
    if (text !== undefined) { el.textContent = text; }
    return el;
  }

  window.OkfSite = Object.freeze({
    HEADING_PREFIX: HEADING_PREFIX,
    readIndex: readIndex,
    normalize: normalize,
    rank: rank,
    isStale: isStale,
    rootOf: rootOf,
    resolve: resolve,
    slugify: slugify,
    uniqueSlugs: uniqueSlugs,
    fragmentCandidates: fragmentCandidates,
    element: element,
  });
})();
