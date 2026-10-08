// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The global graph page, graph.html (spec §6, §11.5, §12.5). This is its core:
// the model (nodes, edges, the compute of what is shown), the status line, the
// equivalent list with its "List" toggle, the drawer of the selected concept
// and the "Reading view" link. Later tasks extend it, each in its own section
// above the start-up block: facets, the drawing laid out by okf-sim.js, pan,
// zoom, draggable nodes, the keyboard contract. Bundle text reaches the DOM
// through textContent, or setAttribute on a fixed name (href built from an
// index path by the one resolver, OkfSite.resolve); every shape comes from
// OkfShapes (§12.2). Every DOM lookup starts from body > .okf-graph-layout or
// #okf-tools (§12.6).
(function () {
  "use strict";

  var site = window.OkfSite;
  var shapes = window.OkfShapes;
  var Sim = window.OkfSim;
  var layout = document.getElementById("okf-graph-layout");
  if (!site || !shapes || !Sim || !layout || layout.parentElement !== document.body) { return; }
  var index = site.readIndex(window);
  if (!index) { return; }

  function part(id) {
    var found = document.getElementById(id);
    return found && layout.contains(found) ? found : null;
  }
  var facetsBox = part("okf-facets");
  var statusLine = part("okf-graph-status");
  var zoomBox = part("okf-graph-zoom");
  var canvas = part("okf-graph-canvas");
  var listBox = part("okf-graph-list");
  var legendBox = part("okf-graph-legend");
  var detail = part("okf-graph-detail");
  if (!facetsBox || !statusLine || !zoomBox || !canvas || !listBox || !legendBox || !detail) { return; }
  var tools = document.getElementById("okf-tools");
  var readingView = document.getElementById("okf-reading-view");
  if (!tools || !readingView || !tools.contains(readingView)) { readingView = null; }

  var root = site.rootOf(document);
  var MIDDOT = String.fromCharCode(0xB7);
  var EMDASH = String.fromCharCode(0x2014);
  var SECTION = String.fromCharCode(0xA7);

  function el(tag, className, text) {
    return site.element(document, tag, className, text);
  }

  function clear(node) {
    while (node.firstChild) { node.removeChild(node.firstChild); }
  }

  function plural(n, one, many) {
    return n + " " + (n === 1 ? one : many);
  }

  function isIndex(v, length) {
    return typeof v === "number" && Number.isInteger(v) && v >= 0 && v < length;
  }

  // Later sections plug into these; each runs its functions in the order
  // they were added.
  var hooks = { beforeRefresh: [], refresh: [], select: [], node: [], drawing: [] };
  function run(list, arg) {
    for (var k = 0; k < list.length; k++) { list[k](arg); }
  }

  // === model ===
  // A node is a concept (key 0..N-1, its index position) or a ghost (key
  // N..N+G-1): index order, as the simulation numbers them (§12.5).
  var N = index.concepts.length;
  var G = index.ghosts.length;
  var total = N + G;
  var M = index.edges.length;
  var byId = new Map();
  for (var c0 = 0; c0 < N; c0++) { byId.set(index.concepts[c0].id, c0); }
  var edgeFrom = [];
  var edgeTo = [];
  var outOf = [];
  var into = [];
  for (var k0 = 0; k0 < total; k0++) { outOf.push([]); into.push([]); }
  for (var e0 = 0; e0 < M; e0++) {
    var edge = index.edges[e0];
    var ok = Array.isArray(edge) && isIndex(edge[0], N)
      && (edge[3] === 1 ? isIndex(edge[1], G) : isIndex(edge[1], N));
    var to = ok ? (edge[3] === 1 ? N + edge[1] : edge[1]) : -1;
    edgeFrom.push(ok ? edge[0] : -1);
    edgeTo.push(to);
    if (ok) {
      outOf[edge[0]].push(e0);
      into[to].push(e0);
    }
  }

  function idOf(key) {
    return key < N ? index.concepts[key].id : index.ghosts[key - N].id;
  }

  // What the list, the drawer and the node titles call a node.
  function nameOf(key) {
    return key < N ? idOf(key) : "absent: " + idOf(key);
  }

  function lastSegment(id) {
    return id.slice(id.lastIndexOf("/") + 1);
  }

  function kindOf(key) {
    return key < N ? shapes.kindOf(index, key) : "ghost";
  }

  function glyph(key) {
    return shapes.icon(kindOf(key), "icon");
  }

  var TRUST = ["human-reviewed", "machine-confirmed", "unverified"];
  function trustSlot(trust) {
    var t = TRUST.indexOf(trust);
    return t === -1 ? 2 : t;
  }

  var nowMs = Date.now();

  function defaultState() {
    return {
      types: index.types.map(function () { return true; }),
      trust: [true, true, true],
      staleOnly: false,
      tags: new Set(),
      labels: true,
      dim: false,
    };
  }
  var state = defaultState();

  // AND between facets, OR inside one (A10). A typeIndex out of range is
  // filtered by no type box.
  function matches(i) {
    var concept = index.concepts[i];
    if (isIndex(concept.typeIndex, state.types.length) && !state.types[concept.typeIndex]) { return false; }
    if (!state.trust[trustSlot(concept.trust)]) { return false; }
    if (state.staleOnly && !site.isStale(concept.staleAfterMs, nowMs)) { return false; }
    if (state.tags.size > 0) {
      for (var t = 0; t < concept.tags.length; t++) {
        if (state.tags.has(concept.tags[t])) { return true; }
      }
      return false;
    }
    return true;
  }

  // The visible graph: matched concepts (all concepts in dim mode), and the
  // ghosts at least one shown concept cites (A10, never filtered by facets).
  function compute() {
    var matched = new Uint8Array(N);
    var shown = new Uint8Array(total);
    var matchedCount = 0;
    for (var i = 0; i < N; i++) {
      if (matches(i)) {
        matched[i] = 1;
        matchedCount++;
      }
      if (matched[i] === 1 || state.dim) { shown[i] = 1; }
    }
    var ghostCount = 0;
    for (var g = N; g < total; g++) {
      for (var k = 0; k < into[g].length; k++) {
        if (shown[edgeFrom[into[g][k]]] === 1) {
          shown[g] = 1;
          ghostCount++;
          break;
        }
      }
    }
    var keys = [];
    for (var key = 0; key < total; key++) {
      if (shown[key] === 1) { keys.push(key); }
    }
    var links = 0;
    for (var e = 0; e < M; e++) {
      if (edgeFrom[e] >= 0 && shown[edgeFrom[e]] === 1 && shown[edgeTo[e]] === 1) { links++; }
    }
    return {
      matched: matched, shown: shown, matchedCount: matchedCount, keys: keys,
      ghostCount: ghostCount, links: links, over: keys.length > Sim.NODE_LIMIT,
    };
  }

  var view = null;

  // === status line, list, drawer ===
  function renderStatus() {
    if (view.over) {
      statusLine.textContent = view.keys.length + " concepts match " + EMDASH + " narrow the filters to draw the graph";
      return;
    }
    var text = "showing " + view.matchedCount + " of " + plural(N, "concept", "concepts");
    if (view.ghostCount > 0) { text += " + " + view.ghostCount + " absent"; }
    text += " " + MIDDOT + " " + view.links + " of " + plural(M, "link", "links");
    statusLine.textContent = text;
  }

  var listMode = false;
  var listButton = el("button", "okf-graph-tool okf-graph-list-toggle", "List");
  listButton.type = "button";
  listButton.setAttribute("aria-pressed", "false");
  listButton.setAttribute("aria-controls", "okf-graph-list");
  zoomBox.appendChild(listButton);
  listButton.addEventListener("click", function () {
    listMode = !listMode;
    updateMode();
  });

  // The drawing, or the equivalent list (§6): the list stands in for the
  // drawing when the reader asks for it, and always above NODE_LIMIT (A6).
  function updateMode() {
    var showList = listMode || view.over;
    listBox.hidden = !showList;
    canvas.hidden = showList;
    listButton.setAttribute("aria-pressed", showList ? "true" : "false");
    listButton.disabled = view.over;
  }

  var listButtons = new Map();

  function renderList() {
    clear(listBox);
    listButtons = new Map();
    listBox.appendChild(el("h2", "okf-section-title", "Concepts and links"));
    var items = el("ul", "okf-graph-list-items");
    view.keys.forEach(function (key) {
      var item = el("li", "okf-graph-list-item");
      var head = el("div", "okf-graph-list-head");
      head.appendChild(glyph(key));
      var button = el("button", "okf-graph-list-select", nameOf(key));
      button.type = "button";
      button.addEventListener("click", function () { select(key, "user"); });
      head.appendChild(button);
      item.appendChild(head);
      var relations = el("ul", "okf-graph-list-rel");
      outOf[key].forEach(function (k) {
        if (view.shown[edgeTo[k]] === 1) { relations.appendChild(el("li", null, "links to " + nameOf(edgeTo[k]))); }
      });
      into[key].forEach(function (k) {
        if (view.shown[edgeFrom[k]] === 1) { relations.appendChild(el("li", null, "referenced by " + nameOf(edgeFrom[k]))); }
      });
      if (relations.firstChild) { item.appendChild(relations); }
      items.appendChild(item);
      listButtons.set(key, button);
    });
    listBox.appendChild(items);
    markListSelection();
  }

  function markListSelection() {
    listButtons.forEach(function (button, key) {
      var current = key === selected;
      button.parentElement.parentElement.classList.toggle("okf-graph-list-current", current);
      if (current) {
        button.setAttribute("aria-current", "true");
        if (!listBox.hidden && typeof button.scrollIntoView === "function") { button.scrollIntoView({ block: "nearest" }); }
      } else {
        button.removeAttribute("aria-current");
      }
    });
  }

  var selected = -1;

  function chip(className, kind, context, text) {
    var span = el("span", className);
    if (kind) { span.appendChild(shapes.icon(kind, context)); }
    span.appendChild(document.createTextNode(text));
    return span;
  }

  function relationBlock(title, keys, first) {
    var block = el("section", first ? "okf-graph-rel okf-graph-rel-first" : "okf-graph-rel");
    block.appendChild(el("h3", "okf-section-title", title + " " + MIDDOT + " " + keys.length));
    var rows = el("ul", "okf-graph-rows");
    keys.forEach(function (key) {
      var item = el("li");
      var row;
      if (key < N) {
        row = el("a", "okf-row");
        row.setAttribute("href", site.resolve(root, index.concepts[key].path));
      } else {
        row = el("span", "okf-row okf-graph-absent");
      }
      row.appendChild(glyph(key));
      row.appendChild(document.createTextNode(nameOf(key)));
      item.appendChild(row);
      rows.appendChild(item);
    });
    block.appendChild(rows);
    return block;
  }

  // G17: the whole bundle's relations of the selection, whatever the facets.
  function renderDrawer() {
    clear(detail);
    detail.appendChild(el("p", "okf-section-title", "Selected"));
    if (selected < 0) {
      detail.appendChild(el("p", "okf-graph-empty", "Select a concept to see its links."));
      return;
    }
    var head = el("div", "okf-graph-detail-head");
    head.appendChild(el("p", "okf-graph-detail-id", idOf(selected)));
    if (selected >= N) {
      // A10: an absent concept is never navigable -- no link, no "Open page".
      head.appendChild(el("h2", "okf-graph-detail-title", "absent"));
      detail.appendChild(head);
      return;
    }
    var concept = index.concepts[selected];
    head.appendChild(el("h2", "okf-graph-detail-title", concept.title));
    detail.appendChild(head);
    var chips = el("div", "okf-chips");
    chips.appendChild(chip("okf-chip okf-chip-type", shapes.kindOf(index, selected), "chip", shapes.typeLabel(concept.type)));
    // C5's states, as P1.1 names and styles them (spec §12.6): the drawer
    // defines no chip class of its own.
    var trust = shapes.trustKind(concept.trust);
    chips.appendChild(chip(trust ? "okf-chip okf-chip-trust" : "okf-chip okf-chip-trust okf-chip-unverified",
      trust, "flag", concept.trust));
    if (typeof concept.staleAfterDate === "string") {
      var staleNow = site.isStale(concept.staleAfterMs, nowMs);
      var staleChip = staleNow
        ? chip("okf-chip okf-chip-stale", "stale", "flag", "stale since " + concept.staleAfterDate)
        : chip("okf-chip okf-chip-stale", null, null, "stale after " + concept.staleAfterDate);
      // "stale since": P1.1's CSS draws okf-chip-stale[data-okf-stale-now] in --stale.
      if (staleNow) { staleChip.setAttribute("data-okf-stale-now", ""); }
      chips.appendChild(staleChip);
    }
    detail.appendChild(chips);
    if (typeof concept.description === "string" && concept.description !== "") {
      detail.appendChild(el("p", "okf-graph-detail-desc", concept.description));
    }
    detail.appendChild(relationBlock("Links to", outOf[selected].map(function (k) { return edgeTo[k]; }), true));
    detail.appendChild(relationBlock("Referenced by", into[selected].map(function (k) { return edgeFrom[k]; }), false));
    var openPage = el("a", "okf-graph-open", "Open page");
    openPage.setAttribute("href", site.resolve(root, concept.path));
    detail.appendChild(openPage);
  }

  // H10, §12.5: "Reading view" leads to the selected concept's page.
  function updateReadingView() {
    if (!readingView) { return; }
    readingView.setAttribute("href", selected >= 0 && selected < N
      ? site.resolve(root, index.concepts[selected].path)
      : site.resolve(root, "index.html"));
  }

  // origin: "user" (click, keys, list) or "url" (a fragment).
  function select(key, origin) {
    selected = key;
    renderDrawer();
    markListSelection();
    updateReadingView();
    run(hooks.select, origin);
  }

  function refresh() {
    run(hooks.beforeRefresh);
    view = compute();
    renderStatus();
    renderList();
    run(hooks.refresh);
    updateMode();
  }

  [
    "Edge = body link (" + SECTION + "6), as in okf graph " + MIDDOT + " arrow points at the target",
    "Red dashed = broken link to an absent concept",
    "Blue = selection and its links",
    "Drag to pan " + MIDDOT + " scroll to zoom",
  ].forEach(function (text) { legendBox.appendChild(el("span", "okf-graph-legend-item", text)); });

  // === start-up ===
  refresh();
  renderDrawer();
  updateReadingView();
})();
