// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The tree explorer (spec §3.2, §8). Reads the site index through
// OkfSite.readIndex and renders into #okf-explorer; without an index the
// explorer stays hidden and the page still works. Bundle text reaches the
// DOM through textContent and fixed-name attributes only (spec §3.4).
(function () {
  "use strict";
  var site = window.OkfSite;
  var nav = document.getElementById("okf-explorer");
  if (!site || !nav) { return; }
  var index = site.readIndex(window);
  if (!index) { return; }

  var root = site.rootOf(document);
  var currentId = document.documentElement.getAttribute("data-okf-concept");
  var current = -1;
  for (var i = 0; i < index.concepts.length; i++) {
    if (index.concepts[i].id === currentId) { current = i; break; }
  }

  // The trust value only selects a key here; it never becomes a class name.
  var TRUST_CLASS = new Map([
    ["human-reviewed", "okf-trust-human"],
    ["machine-confirmed", "okf-trust-machine"],
  ]);

  var staleMarks = [];

  function el(tag, className, text) {
    return site.element(document, tag, className, text);
  }

  function appendBadges(row, concept) {
    var trustClass = TRUST_CLASS.get(concept.trust);
    if (trustClass) {
      var badge = el("span", "okf-badge " + trustClass);
      badge.setAttribute("title", concept.trust);
      badge.appendChild(el("span", "okf-sr", concept.trust));
      row.appendChild(badge);
    }
    if (typeof concept.staleAfterMs === "number") {
      var stale = el("span", "okf-badge okf-stale");
      stale.setAttribute("title", "stale after " + (concept.staleAfterDate || ""));
      stale.appendChild(el("span", "okf-sr", "stale"));
      staleMarks.push({ el: stale, ms: concept.staleAfterMs });
      row.appendChild(stale);
    }
  }

  function setExpanded(rec, open) {
    rec.ul.hidden = !open;
    rec.toggle.setAttribute("aria-expanded", open ? "true" : "false");
  }

  // A node can be BOTH a destination (the concept at this path) and a folder
  // (children): foo.md and foo/bar.md coexist legally, so opening (the link)
  // and expanding (the button) are separate commands (spec §3.2).
  function build(node) {
    var rec = { li: document.createElement("li"), ul: null, toggle: null, label: "", children: [], holdsCurrent: false, defaultOpen: false };
    var row = el("div", "okf-tree-row");

    if (node.children.length > 0) {
      var toggle = el("button", "okf-tree-toggle");
      toggle.type = "button";
      toggle.appendChild(el("span", "okf-sr", "Expand or collapse " + node.name));
      toggle.addEventListener("click", function () { setExpanded(rec, rec.ul.hidden); });
      rec.toggle = toggle;
      row.appendChild(toggle);
    } else {
      row.appendChild(el("span", "okf-tree-spacer"));
    }

    var concept = node.concept >= 0 ? index.concepts[node.concept] : null;
    if (concept) {
      var link = el("a", "okf-tree-link", concept.title);
      link.setAttribute("href", site.resolve(root, concept.path));
      link.setAttribute("title", concept.id);
      if (node.concept === current) {
        link.setAttribute("aria-current", "page");
        rec.holdsCurrent = true;
      }
      row.appendChild(link);
      appendBadges(row, concept);
      rec.label = site.normalize(concept.title + " " + concept.id);
    } else {
      row.appendChild(el("span", "okf-tree-folder", node.name));
      rec.label = site.normalize(node.name);
    }
    rec.li.appendChild(row);

    if (node.children.length > 0) {
      rec.ul = el("ul", "okf-tree-children");
      var below = false;
      for (var k = 0; k < node.children.length; k++) {
        var child = build(node.children[k]);
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

  // Filter by name: matches and their ancestors stay visible; ancestors of a
  // match open. An empty query restores the default state.
  function applyFilter(rec, q) {
    var childVisible = false;
    for (var k = 0; k < rec.children.length; k++) {
      if (applyFilter(rec.children[k], q)) { childVisible = true; }
    }
    var visible = q === "" || rec.label.indexOf(q) !== -1 || childVisible;
    rec.li.hidden = !visible;
    if (rec.ul) { setExpanded(rec, q === "" ? rec.defaultOpen : childVisible); }
    return visible;
  }

  function refreshStale() {
    var now = Date.now();
    for (var k = 0; k < staleMarks.length; k++) {
      staleMarks[k].el.hidden = !site.isStale(staleMarks[k].ms, now);
    }
  }

  var head = el("div", "okf-explorer-head");
  var label = el("label", "okf-explorer-label", "Explorer");
  label.setAttribute("for", "okf-tree-filter");
  var filter = document.createElement("input");
  filter.type = "search";
  filter.id = "okf-tree-filter";
  filter.setAttribute("placeholder", "Filter by name");
  filter.setAttribute("autocomplete", "off");
  head.appendChild(label);
  head.appendChild(filter);

  var list = el("ul", "okf-tree");
  var tops = [];
  for (var t = 0; t < index.tree.length; t++) {
    var rec = build(index.tree[t]);
    tops.push(rec);
    list.appendChild(rec.li);
  }

  nav.appendChild(head);
  nav.appendChild(list);
  nav.hidden = false;

  filter.addEventListener("input", function () {
    var q = site.normalize(filter.value);
    for (var k = 0; k < tops.length; k++) { applyFilter(tops[k], q); }
  });

  refreshStale();
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState === "visible") { refreshStale(); }
  });
})();
