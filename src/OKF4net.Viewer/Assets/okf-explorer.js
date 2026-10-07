// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The tree explorer (spec §3.2, §8, §11.2). Reads the site index through
// OkfSite.readIndex and draws every shape through OkfShapes; without either
// the explorer stays hidden and the page still works. Bundle text reaches
// the DOM through textContent and fixed-name attributes only (spec §3.4).
// Rows are labelled by the last id segment (A22), the title is a tooltip; the
// type chips filter the tree (OR between chips, AND with the name field), and
// the header's Filters button counts both (H8).
(function () {
  "use strict";
  var site = window.OkfSite;
  var shapes = window.OkfShapes;
  var nav = document.getElementById("okf-explorer");
  if (!site || !shapes || !nav) { return; }
  var index = site.readIndex(window);
  if (!index) { return; }

  var root = site.rootOf(document);
  var currentId = document.documentElement.getAttribute("data-okf-concept");
  var current = -1;
  for (var i = 0; i < index.concepts.length; i++) {
    if (index.concepts[i].id === currentId) { current = i; break; }
  }

  var staleMarks = [];
  var pressed = new Set();   // slots of the pressed type chips
  var filterCount = null;    // the Filters button's visible count
  var filtersButton = null;  // the header's Filters button

  function el(tag, className, text) {
    return site.element(document, tag, className, text);
  }

  // E10: a trust flag (not for unverified) and a stale flag (shown only while
  // stale), both from OkfShapes, each with its visually hidden text.
  function appendFlags(row, concept) {
    var kind = shapes.trustKind(concept.trust);
    if (kind) {
      var trust = el("span", "okf-flag okf-flag-trust");
      trust.setAttribute("title", concept.trust);
      trust.appendChild(shapes.icon(kind, "flag"));
      trust.appendChild(el("span", "okf-sr", concept.trust));
      row.appendChild(trust);
    }
    if (typeof concept.staleAfterMs === "number") {
      var stale = el("span", "okf-flag okf-flag-stale");
      stale.setAttribute("title", "stale after " + (concept.staleAfterDate || ""));
      stale.appendChild(shapes.icon("stale", "flag"));
      stale.appendChild(el("span", "okf-sr", "stale"));
      staleMarks.push({ el: stale, ms: concept.staleAfterMs });
      row.appendChild(stale);
    }
  }

  function setExpanded(rec, open) {
    rec.ul.hidden = !open;
    rec.toggle.setAttribute("aria-expanded", open ? "true" : "false");
  }

  // E9: the concepts (destinations) below a node, the node itself excluded.
  function destinationsBelow(node) {
    var n = 0;
    for (var k = 0; k < node.children.length; k++) {
      if (node.children[k].concept >= 0) { n++; }
      n += destinationsBelow(node.children[k]);
    }
    return n;
  }

  // A node can be BOTH a destination (the concept at this path) and a folder
  // (children): foo.md and foo/bar.md coexist legally, so opening (the link)
  // and expanding (the chevron) are separate commands (spec §3.2); such a row
  // has two 12 px slots, chevron then glyph (E6).
  function build(node, depth) {
    var rec = { li: document.createElement("li"), ul: null, toggle: null, label: "", concept: node.concept, slot: -1, children: [], holdsCurrent: false, defaultOpen: false };
    var row = el("div", "okf-tree-row");
    row.style.paddingLeft = (12 + 18 * depth) + "px";

    if (node.children.length > 0) {
      var toggle = el("button", "okf-tree-toggle");
      toggle.type = "button";
      toggle.appendChild(el("span", "okf-sr", "Expand or collapse " + node.name));
      toggle.addEventListener("click", function () { setExpanded(rec, rec.ul.hidden); });
      rec.toggle = toggle;
      row.appendChild(toggle);
    }

    var concept = node.concept >= 0 ? index.concepts[node.concept] : null;
    if (concept) {
      rec.slot = shapes.slotOf(index, node.concept);
      var glyph = el("span", "okf-glyph-slot");
      glyph.appendChild(shapes.icon(shapes.KINDS[rec.slot], "icon", shapes.typeLabel(concept.type)));
      row.appendChild(glyph);
      var link = el("a", "okf-tree-link", node.name);
      link.setAttribute("href", site.resolve(root, concept.path));
      link.setAttribute("title", concept.title);
      link.setAttribute("data-okf-id", concept.id);
      if (node.concept === current) {
        link.setAttribute("aria-current", "page");
        row.classList.add("okf-tree-current");
        rec.holdsCurrent = true;
      }
      row.appendChild(link);
      // The name filter still searches the title and the id.
      rec.label = site.normalize(concept.title + " " + concept.id);
    } else {
      row.appendChild(el("span", "okf-tree-folder", node.name));
      rec.label = site.normalize(node.name);
    }
    if (node.children.length > 0) { row.appendChild(el("span", "okf-tree-count", String(destinationsBelow(node)))); }
    if (concept) { appendFlags(row, concept); }
    rec.li.appendChild(row);

    if (node.children.length > 0) {
      rec.ul = el("ul", "okf-tree-children");
      var below = false;
      for (var k = 0; k < node.children.length; k++) {
        var child = build(node.children[k], depth + 1);
        rec.children.push(child);
        rec.ul.appendChild(child.li);
        if (child.holdsCurrent) { below = true; }
      }
      rec.li.appendChild(rec.ul);
      // Only the path down to the current page starts open.
      rec.defaultOpen = below;
      if (below) { rec.holdsCurrent = true; }
      setExpanded(rec, rec.defaultOpen);
    }
    return rec;
  }

  // A concept matches when its title or id holds the query AND (no chip is
  // pressed OR its slot's chip is); a folder without a concept matches the
  // query by name only while no chip is pressed. Matches and their ancestors
  // stay visible, ancestors of a match open; no filter at all restores the
  // default state.
  function applyFilter(rec, q) {
    var filtering = q !== "" || pressed.size > 0;
    var childVisible = false;
    for (var k = 0; k < rec.children.length; k++) {
      if (applyFilter(rec.children[k], q)) { childVisible = true; }
    }
    var self;
    if (!filtering) {
      self = true;
    } else if (rec.concept >= 0) {
      self = (q === "" || rec.label.indexOf(q) !== -1) && (pressed.size === 0 || pressed.has(rec.slot));
    } else {
      self = pressed.size === 0 && rec.label.indexOf(q) !== -1;
    }
    var visible = self || childVisible;
    rec.li.hidden = !visible;
    if (rec.ul) { setExpanded(rec, filtering ? childVisible : rec.defaultOpen); }
    return visible;
  }

  // H8: pressed chips, plus one when the name field holds a query.
  function updateFiltersCount(q) {
    if (!filterCount) { return; }
    var n = pressed.size + (q !== "" ? 1 : 0);
    filterCount.textContent = String(n);
    filterCount.hidden = n === 0;
    // The accessible name: an aria-label, since an absolutely positioned
    // visually hidden span makes a reader say "Filters , 1 active".
    if (n === 0) { filtersButton.removeAttribute("aria-label"); } else { filtersButton.setAttribute("aria-label", "Filters, " + n + " active"); }
  }

  function refilter() {
    var q = site.normalize(filter.value);
    for (var k = 0; k < tops.length; k++) { applyFilter(tops[k], q); }
    updateFiltersCount(q);
  }

  // E4: one toggle chip per type of rank 0 to 4, plus "Other types"; the
  // glyph doubles as the page's legend of type shapes, titled (X11).
  function typeChip(entry) {
    var chip = el("button", "okf-chip");
    chip.type = "button";
    chip.setAttribute("aria-pressed", "false");
    chip.setAttribute("data-okf-slot", String(entry.slot));
    chip.appendChild(shapes.icon(shapes.KINDS[entry.slot], "icon", entry.label));
    chip.appendChild(el("span", "okf-chip-text", entry.label));
    chip.appendChild(el("span", "okf-chip-count", String(entry.count)));
    chip.addEventListener("click", function () {
      var on = !pressed.has(entry.slot);
      if (on) { pressed.add(entry.slot); } else { pressed.delete(entry.slot); }
      chip.setAttribute("aria-pressed", on ? "true" : "false");
      refilter();
    });
    return chip;
  }

  function refreshStale() {
    var now = Date.now();
    for (var k = 0; k < staleMarks.length; k++) {
      staleMarks[k].el.hidden = !site.isStale(staleMarks[k].ms, now);
    }
  }

  var head = el("div", "okf-explorer-head");
  // The <nav> is already named "Explorer": the visible title is text (E2).
  var title = el("p", "okf-section-title okf-explorer-title", "Explorer");
  title.setAttribute("aria-hidden", "true");
  var filter = document.createElement("input");
  filter.type = "search";
  filter.id = "okf-tree-filter";
  filter.setAttribute("aria-label", "Filter by name");
  filter.setAttribute("placeholder", "Filter by name" + String.fromCharCode(0x2026));
  filter.setAttribute("autocomplete", "off");
  head.appendChild(title);
  head.appendChild(filter);
  var entries = shapes.typeLegendEntries(index);
  if (entries.length > 0) {
    var chips = el("div", "okf-type-chips");
    chips.setAttribute("role", "group");
    chips.setAttribute("aria-label", "Filter by type");
    for (var e = 0; e < entries.length; e++) { chips.appendChild(typeChip(entries[e])); }
    head.appendChild(chips);
  }

  var list = el("ul", "okf-tree");
  var tops = [];
  for (var t = 0; t < index.tree.length; t++) {
    var rec = build(index.tree[t], 0);
    tops.push(rec);
    list.appendChild(rec.li);
  }

  // E12: the legend of the row flags.
  var foot = el("div", "okf-explorer-foot");
  foot.appendChild(shapes.legend([{ role: "trust", trust: "human-reviewed" }, { role: "trust", trust: "machine-confirmed" }, { role: "stale" }]));

  nav.appendChild(head);
  nav.appendChild(list);
  nav.appendChild(foot);
  nav.hidden = false;

  // H8: "Filters" in the header, right before "Global graph"; it moves the
  // focus to the name field (the browser scrolls it into view, stacked
  // layout included).
  var tools = document.getElementById("okf-tools");
  if (tools) {
    filtersButton = el("button", "okf-tool");
    filtersButton.type = "button";
    filtersButton.id = "okf-filters-toggle";
    filtersButton.appendChild(document.createTextNode("Filters"));
    filterCount = el("span", "okf-filters-count", "0");
    filterCount.setAttribute("aria-hidden", "true");
    filterCount.hidden = true;
    filtersButton.appendChild(filterCount);
    filtersButton.addEventListener("click", function () { filter.focus(); });
    var graphLink = document.getElementById("okf-global-graph");
    tools.insertBefore(filtersButton, graphLink && graphLink.parentNode === tools ? graphLink : null);
  }

  // On the desktop layout the explorer is its own scroll container (sticky,
  // overflow-y: auto) with a sticky head and a sticky foot (E12, E13): bring
  // the current entry into the band between them, about a third from its top,
  // by scrolling the explorer alone. Never the page: in the stacked layout
  // the explorer is not a scroll container and nothing moves.
  function revealCurrent() {
    var link = nav.querySelector('a.okf-tree-link[aria-current="page"]');
    if (!link || nav.scrollHeight <= nav.clientHeight) { return; }
    var overflow = window.getComputedStyle(nav).overflowY;
    if (overflow !== "auto" && overflow !== "scroll") { return; }
    var view = nav.getBoundingClientRect();
    var entry = link.getBoundingClientRect();
    // The part of the explorer on screen: it may run past the window bottom.
    var visible = Math.min(nav.clientHeight, window.innerHeight - Math.max(0, view.top));
    if (!(visible > 0)) { visible = nav.clientHeight; }
    // The usable band: under the sticky head, above the sticky foot.
    var bandTop = Math.max(0, head.getBoundingClientRect().bottom - view.top);
    var bandBottom = visible;
    var footTop = foot.getBoundingClientRect().top - view.top;
    if (footTop > bandTop) { bandBottom = Math.min(bandBottom, footTop); }
    var band = bandBottom - bandTop;
    if (!(band > 0)) { return; }
    var top = entry.top - view.top;
    if (top >= bandTop && top + entry.height <= bandBottom) { return; }
    nav.scrollTop = Math.max(0, nav.scrollTop + top - bandTop - Math.round(band / 3));
  }
  revealCurrent();
  // The web fonts change the head's height (the chips wrap differently): once
  // they are ready, place the entry again, unless the reader has scrolled.
  var revealedAt = nav.scrollTop;
  if (document.fonts && document.fonts.ready && typeof document.fonts.ready.then === "function") {
    document.fonts.ready.then(function () {
      if (nav.scrollTop === revealedAt) { revealCurrent(); }
    });
  }

  filter.addEventListener("input", refilter);

  refreshStale();
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState === "visible") { refreshStale(); }
  });
})();
