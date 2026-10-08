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
  var hooks = { beforeRefresh: [], refresh: [], settled: [], select: [], node: [], drawing: [], show: [] };
  function run(list, arg) {
    for (var k = 0; k < list.length; k++) { list[k](arg); }
  }

  // === model ===
  // A node is a concept (key 0..N-1, its index position) or a ghost (key
  // N..N+G-1): index order, as the simulation numbers them (§12.5).
  //
  // A damaged entry (not an object, or an id that is not a string) keeps its
  // key but is no node: never matched, drawn, listed or counted, and every
  // edge that names it is skipped, as okf-local.js and the palette skip one.
  // Every field is read once, here; one of the wrong type is taken as absent
  // ("" for a text, no tags, no page). Nothing below reads OKF_INDEX's
  // entries again, except OkfShapes.kindOf, which has its own guards.
  var N = index.concepts.length;
  var G = index.ghosts.length;
  var total = N + G;

  function textOf(v) {
    return typeof v === "string" ? v : "";
  }

  function idIn(entry) {
    return entry !== null && typeof entry === "object" && typeof entry.id === "string" ? entry.id : null;
  }

  var concepts = [];
  var conceptCount = 0;
  var byId = new Map();
  for (var c0 = 0; c0 < N; c0++) {
    var raw = index.concepts[c0];
    var rawId = idIn(raw);
    if (rawId === null) {
      concepts.push(null);
      continue;
    }
    var rawTags = raw.tags;
    concepts.push({
      id: rawId,
      title: textOf(raw.title),
      type: textOf(raw.type),
      path: textOf(raw.path),
      trust: typeof raw.trust === "string" ? raw.trust : "unverified",
      typeIndex: raw.typeIndex,
      tags: Array.isArray(rawTags) ? rawTags.filter(function (tag) { return typeof tag === "string"; }) : [],
      staleAfterMs: raw.staleAfterMs,
      staleAfterDate: typeof raw.staleAfterDate === "string" ? raw.staleAfterDate : null,
      description: textOf(raw.description),
    });
    conceptCount++;
    byId.set(rawId, c0);
  }
  var ghosts = [];
  for (var g0 = 0; g0 < G; g0++) {
    var ghostId = idIn(index.ghosts[g0]);
    ghosts.push(ghostId === null ? null : { id: ghostId });
  }

  // Only the edges between two entries: edge k of the model is not edge k of
  // the index once one is skipped.
  var edgeFrom = [];
  var edgeTo = [];
  var outOf = [];
  var into = [];
  for (var k0 = 0; k0 < total; k0++) { outOf.push([]); into.push([]); }
  for (var e0 = 0; e0 < index.edges.length; e0++) {
    var edge = index.edges[e0];
    if (!Array.isArray(edge)) { continue; }
    var from = edge[0];
    var target = edge[1];
    var toGhost = edge[3] === 1;
    if (!isIndex(from, N) || concepts[from] === null) { continue; }
    if (toGhost ? !isIndex(target, G) || ghosts[target] === null : !isIndex(target, N) || concepts[target] === null) { continue; }
    var to = toGhost ? N + target : target;
    outOf[from].push(edgeFrom.length);
    into[to].push(edgeFrom.length);
    edgeFrom.push(from);
    edgeTo.push(to);
  }
  var M = edgeFrom.length;

  // The types' table, each row read once: name, count and slot, or null.
  var types = [];
  for (var t0 = 0; t0 < index.types.length; t0++) {
    var rawType = index.types[t0];
    types.push(rawType !== null && typeof rawType === "object" ? {
      name: textOf(rawType.name),
      count: isIndex(rawType.count, Infinity) ? rawType.count : 0,
      slot: isIndex(rawType.slot, shapes.KINDS.length) ? rawType.slot : shapes.OTHER_SLOT,
    } : null);
  }

  // Only ever asked for an entry's key.
  function idOf(key) {
    return key < N ? concepts[key].id : ghosts[key - N].id;
  }

  // A concept's page, resolved, or null (a ghost, or a concept with no path).
  function pageOf(key) {
    return key >= 0 && key < N && concepts[key] !== null && concepts[key].path !== ""
      ? site.resolve(root, concepts[key].path)
      : null;
  }

  // What the list, the drawer and the node titles call a node.
  function nameOf(key) {
    return key < N ? idOf(key) : "absent: " + idOf(key);
  }

  function lastSegment(id) {
    return id.slice(id.lastIndexOf("/") + 1);
  }

  // G11/G13 (revision 11): a node label longer than LABEL_MAX code points
  // keeps the first LABEL_MAX - 1 and an ellipsis; the whole name stays in the
  // node's <title> and accessible name. By code points, so a cut never leaves
  // half a surrogate pair; reads at most LABEL_MAX + 1 of them, whatever the
  // length of the text.
  var LABEL_MAX = 24;
  var ELLIPSIS = String.fromCharCode(0x2026);

  function isPair(s, at) {
    var high = s.charCodeAt(at);
    return high >= 0xd800 && high <= 0xdbff && at + 1 < s.length && (s.charCodeAt(at + 1) & 0xfc00) === 0xdc00;
  }

  function cutLabel(text) {
    var units = 0;
    var points = 0;
    var keep = 0;
    while (units < text.length && points <= LABEL_MAX) {
      if (points === LABEL_MAX - 1) { keep = units; }
      units += isPair(text, units) ? 2 : 1;
      points++;
    }
    return points > LABEL_MAX ? text.slice(0, keep) + ELLIPSIS : text;
  }

  // Code points of a (cut) label: its width is counted in them.
  function labelChars(text) {
    var points = 0;
    for (var at = 0; at < text.length; at += isPair(text, at) ? 2 : 1) { points++; }
    return points;
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
      types: types.map(function () { return true; }),
      trust: [true, true, true],
      staleOnly: false,
      tags: new Set(),
      labels: true,
      dim: false,
    };
  }
  var state = defaultState();

  // AND between facets, OR inside one (A10). A typeIndex out of range is
  // filtered by no type box. A damaged entry matches nothing.
  function matches(i) {
    var concept = concepts[i];
    if (concept === null) { return false; }
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
      if (matched[i] === 1 || (state.dim && concepts[i] !== null)) { shown[i] = 1; }
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
      if (shown[edgeFrom[e]] === 1 && shown[edgeTo[e]] === 1) { links++; }
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
    var line = "showing " + view.matchedCount + " of " + plural(conceptCount, "concept", "concepts");
    if (view.ghostCount > 0) { line += " + " + view.ghostCount + " absent"; }
    line += " " + MIDDOT + " " + view.links + " of " + plural(M, "link", "links");
    statusLine.textContent = line;
  }

  var listMode = false;
  var listButton = el("button", "okf-graph-tool okf-graph-list-toggle", "List");
  // Zoom in, Zoom out and Fit: they act on the drawing, so they rest while
  // the list stands in for it.
  var toolButtons = [];
  listButton.type = "button";
  listButton.setAttribute("aria-pressed", "false");
  listButton.setAttribute("aria-controls", "okf-graph-list");
  zoomBox.appendChild(listButton);
  listButton.addEventListener("click", function () {
    listMode = !listMode;
    updateMode();
  });

  // The list is built only while it is shown, and only when the shown set has
  // changed since it was last built: its entries and relations depend on
  // nothing else (about ten elements per node, so ~16k at NODE_LIMIT, and
  // the whole bundle above it).
  var listStale = true;

  function sameKeys(a, b) {
    if (a === null || a.length !== b.length) { return false; }
    for (var i = 0; i < a.length; i++) {
      if (a[i] !== b[i]) { return false; }
    }
    return true;
  }

  // The drawing, or the equivalent list (§6): the list stands in for the
  // drawing when the reader asks for it, and always above NODE_LIMIT (A6).
  function updateMode() {
    var showList = listMode || view.over;
    var wasHidden = canvas.hidden;
    listBox.hidden = !showList;
    canvas.hidden = showList;
    if (wasHidden && !showList) { run(hooks.show); }
    listButton.setAttribute("aria-pressed", showList ? "true" : "false");
    listButton.disabled = view.over;
    toolButtons.forEach(function (button) { button.disabled = showList; });
    if (showList && listStale) {
      renderList();
      listStale = false;
    }
  }

  // Entries one task builds. A long list (above NODE_LIMIT, the whole
  // bundle) is built LIST_CHUNK entries a frame, in index order, so that no
  // click waits for all of it; the first chunk is built at once.
  var LIST_CHUNK = 200;
  // Bumped by every build, and by a change of the shown set: a chunk of an
  // older build does nothing.
  var listJob = 0;
  // The built entries' buttons, by key, and the one marked as the selection.
  var listButtons = new Map();
  var listMarked = null;

  function listEntry(key, shown) {
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
      if (shown[edgeTo[k]] === 1) { relations.appendChild(el("li", null, "links to " + nameOf(edgeTo[k]))); }
    });
    into[key].forEach(function (k) {
      if (shown[edgeFrom[k]] === 1) { relations.appendChild(el("li", null, "referenced by " + nameOf(edgeFrom[k]))); }
    });
    if (relations.firstChild) { item.appendChild(relations); }
    listButtons.set(key, button);
    return item;
  }

  function renderList() {
    var job = ++listJob;
    clear(listBox);
    listButtons = new Map();
    listMarked = null;
    listBox.appendChild(el("h2", "okf-section-title", "Concepts and links"));
    var items = el("ul", "okf-graph-list-items");
    listBox.appendChild(items);
    var keys = view.keys;
    var shown = view.shown;
    var at = 0;
    function chunk() {
      if (job !== listJob) { return; }
      var end = Math.min(keys.length, at + LIST_CHUNK);
      var part = document.createDocumentFragment();
      var built = false;
      for (; at < end; at++) {
        part.appendChild(listEntry(keys[at], shown));
        if (keys[at] === selected) { built = true; }
      }
      items.appendChild(part);
      // The selection is marked as soon as its entry exists.
      if (built) { markListSelection(); }
      if (at < keys.length) { schedule(chunk); }
    }
    chunk();
  }

  // One entry marked (class and aria-current) at most: the selection's, once
  // built; brought into the list's view.
  function markListSelection() {
    var button = selected >= 0 && listButtons.has(selected) ? listButtons.get(selected) : null;
    if (listMarked && listMarked !== button) {
      listMarked.parentElement.parentElement.classList.remove("okf-graph-list-current");
      listMarked.removeAttribute("aria-current");
    }
    listMarked = button;
    if (!button) { return; }
    var item = button.parentElement.parentElement;
    item.classList.add("okf-graph-list-current");
    button.setAttribute("aria-current", "true");
    revealEntry(item);
  }

  // The entry, its relation lines included, in the middle of the list (its
  // top at the list's top when it is taller), unless it is wholly in view
  // already. Only the list scrolls: scrollIntoView would scroll the page too.
  function revealEntry(item) {
    if (listBox.hidden) { return; }
    var box = listBox.getBoundingClientRect();
    var r = item.getBoundingClientRect();
    var height = listBox.clientHeight;
    var into = r.top - box.top - listBox.clientTop;
    if (into >= 0 && into + r.height <= height) { return; }
    var top = listBox.scrollTop + into - (r.height < height ? (height - r.height) / 2 : 0);
    listBox.scrollTop = Math.max(0, top);
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
      var href = pageOf(key);
      if (href !== null) {
        row = el("a", "okf-row");
        row.setAttribute("href", href);
      } else if (key < N) {
        row = el("span", "okf-row");
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
    var concept = concepts[selected];
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
    if (concept.description !== "") {
      detail.appendChild(el("p", "okf-graph-detail-desc", concept.description));
    }
    detail.appendChild(relationBlock("Links to", outOf[selected].map(function (k) { return edgeTo[k]; }), true));
    detail.appendChild(relationBlock("Referenced by", into[selected].map(function (k) { return edgeFrom[k]; }), false));
    var page = pageOf(selected);
    if (page !== null) {
      var openPage = el("a", "okf-graph-open", "Open page");
      openPage.setAttribute("href", page);
      detail.appendChild(openPage);
    }
  }

  // H10, §12.5: "Reading view" leads to the selected concept's page.
  function updateReadingView() {
    if (!readingView) { return; }
    var page = pageOf(selected);
    readingView.setAttribute("href", page !== null ? page : site.resolve(root, "index.html"));
  }

  // origin: "user" (click, keys, list) or "url" (a fragment).
  function select(key, origin) {
    selected = key;
    // Centring belongs to the fragment that asked for it; after a click, a
    // rebuild fits the view instead of recentring the node the page opened on.
    if (origin === "user") { intent = -1; }
    renderDrawer();
    markListSelection();
    updateReadingView();
    run(hooks.select, origin);
  }

  function refresh() {
    run(hooks.beforeRefresh);
    var previousKeys = view ? view.keys : null;
    view = compute();
    if (!sameKeys(previousKeys, view.keys)) {
      listStale = true;
      listJob++;
    }
    renderStatus();
    run(hooks.refresh);
    updateMode();
    run(hooks.settled);
  }

  [
    "Edge = body link (" + SECTION + "6), as in okf graph " + MIDDOT + " arrow points at the target",
    "Red dashed = broken link to an absent concept",
    "Blue = selection and its links",
    "Drag to pan " + MIDDOT + " scroll to zoom",
  ].forEach(function (text) { legendBox.appendChild(el("span", "okf-graph-legend-item", text)); });

  // === facets ===
  var facetInputs = { types: [], trust: [], stale: null, labels: null, dim: null };
  var tagButtons = new Map();
  var staleCount = null;

  function facetSection(title, id) {
    var section = el("section", "okf-facet");
    var heading = el("h2", "okf-section-title", title);
    heading.id = "okf-facet-" + id;
    section.setAttribute("role", "group");
    section.setAttribute("aria-labelledby", heading.id);
    section.appendChild(heading);
    return section;
  }

  function checkboxRow(checked, mark, name, count, onChange) {
    var row = el("label", "okf-facet-row");
    var input = document.createElement("input");
    input.type = "checkbox";
    input.checked = checked;
    input.addEventListener("change", function () { onChange(input.checked); });
    row.appendChild(input);
    if (mark) { row.appendChild(mark); }
    row.appendChild(el("span", "okf-facet-name", name));
    var countSpan = null;
    if (count !== null) {
      // The space keeps the accessible name "Note 9", not "Note9".
      row.appendChild(document.createTextNode(" "));
      countSpan = el("span", "okf-facet-count", String(count));
      row.appendChild(countSpan);
    }
    return { row: row, input: input, count: countSpan };
  }

  function emptyMark() {
    // P1.1's shared empty glyph slot (10 x 10, spec §12.6), not a class of P3's.
    var span = el("span", "okf-glyph-blank");
    span.setAttribute("aria-hidden", "true");
    return span;
  }

  function staleNowCount() {
    var n = 0;
    for (var i = 0; i < N; i++) {
      if (concepts[i] !== null && site.isStale(concepts[i].staleAfterMs, nowMs)) { n++; }
    }
    return n;
  }

  function buildFacets() {
    var details = el("details", "okf-facets-details");
    details.appendChild(el("summary", "okf-facets-summary", "Filters"));
    var body = el("div", "okf-facets-body");
    details.appendChild(body);

    // G3: one row per entry of `types`, index order; the page's type legend.
    // A damaged entry has no row (its concepts are filtered by no type box).
    var typeSection = facetSection("Type", "type");
    var typeList = el("div", "okf-facet-list");
    types.forEach(function (type, t) {
      if (type === null) { return; }
      var r = checkboxRow(true, shapes.icon(shapes.KINDS[type.slot], "icon"), shapes.typeLabel(type.name), type.count,
        function (on) { state.types[t] = on; refresh(); });
      facetInputs.types.push(r.input);
      typeList.appendChild(r.row);
    });
    typeSection.appendChild(typeList);
    body.appendChild(typeSection);

    // G4: the three tiers, bundle totals.
    var trustSection = facetSection("Trust", "trust");
    var trustList = el("div", "okf-facet-list");
    var trustCounts = [0, 0, 0];
    concepts.forEach(function (concept) { if (concept !== null) { trustCounts[trustSlot(concept.trust)]++; } });
    TRUST.forEach(function (tier, t) {
      var kind = shapes.trustKind(tier);
      var r = checkboxRow(true, kind ? shapes.icon(kind, "flag") : emptyMark(), tier, trustCounts[t],
        function (on) { state.trust[t] = on; refresh(); });
      facetInputs.trust.push(r.input);
      trustList.appendChild(r.row);
    });
    trustSection.appendChild(trustList);
    body.appendChild(trustSection);

    // G5: stale as of now, re-evaluated with nowMs (§4.4).
    var freshSection = facetSection("Freshness", "freshness");
    var fresh = checkboxRow(false, shapes.icon("stale", "flag"), "Stale only (as of now)", staleNowCount(),
      function (on) { state.staleOnly = on; refresh(); });
    facetInputs.stale = fresh.input;
    staleCount = fresh.count;
    freshSection.appendChild(fresh.row);
    body.appendChild(freshSection);

    // G6: tags by count (descending), then ordinal; the first 12, then all.
    var tagCounts = new Map();
    concepts.forEach(function (concept) {
      if (concept === null) { return; }
      new Set(concept.tags).forEach(function (tag) { tagCounts.set(tag, (tagCounts.get(tag) || 0) + 1); });
    });
    var tags = Array.from(tagCounts.keys()).sort(function (a, b) {
      var d = tagCounts.get(b) - tagCounts.get(a);
      return d !== 0 ? d : (a < b ? -1 : a > b ? 1 : 0);
    });
    if (tags.length > 0) {
      var tagSection = facetSection("Tags", "tags");
      var tagRow = el("div", "okf-facet-tags");
      tags.forEach(function (tag, k) {
        // Name and count in their own spans (the name may wrap, the count
        // stays); textContent is still "<tag> <count>".
        var button = el("button", "okf-chip");
        button.appendChild(el("span", "okf-facet-tag-name", tag));
        button.appendChild(document.createTextNode(" "));
        button.appendChild(el("span", "okf-facet-tag-count", String(tagCounts.get(tag))));
        button.type = "button";
        button.setAttribute("aria-pressed", "false");
        button.hidden = k >= 12;
        button.addEventListener("click", function () {
          if (state.tags.has(tag)) { state.tags.delete(tag); } else { state.tags.add(tag); }
          button.setAttribute("aria-pressed", state.tags.has(tag) ? "true" : "false");
          refresh();
        });
        tagButtons.set(tag, button);
        tagRow.appendChild(button);
      });
      tagSection.appendChild(tagRow);
      if (tags.length > 12) {
        var more = el("button", "okf-facet-more", "Show all tags (" + tags.length + ")");
        more.type = "button";
        more.addEventListener("click", function () {
          tagButtons.forEach(function (button) { button.hidden = false; });
          more.remove();
          tagButtons.get(tags[12]).focus();
        });
        tagSection.appendChild(more);
      }
      body.appendChild(tagSection);
    }

    // G7.
    var displaySection = facetSection("Display", "display");
    displaySection.classList.add("okf-facet-display");
    var labels = checkboxRow(true, null, "Node labels", null, function (on) { state.labels = on; refresh(); });
    var dim = checkboxRow(false, null, "Dim unmatched instead of hiding", null, function (on) { state.dim = on; refresh(); });
    facetInputs.labels = labels.input;
    facetInputs.dim = dim.input;
    displaySection.appendChild(labels.row);
    displaySection.appendChild(dim.row);
    body.appendChild(displaySection);

    facetsBox.appendChild(details);

    // G19: below 1100 px the facets fold into "Filters"; above, always open.
    var wide = typeof window.matchMedia === "function" ? window.matchMedia("(min-width: 1100px)") : null;
    details.open = wide ? wide.matches : true;
    if (wide) {
      var onWide = function () { if (wide.matches) { details.open = true; } };
      if (typeof wide.addEventListener === "function") { wide.addEventListener("change", onWide); } else { wide.addListener(onWide); }
    }
  }

  // Back to the defaults of G3-G7. The caller refreshes.
  function resetFacets() {
    state = defaultState();
    facetInputs.types.forEach(function (input) { input.checked = true; });
    facetInputs.trust.forEach(function (input) { input.checked = true; });
    if (facetInputs.stale) { facetInputs.stale.checked = false; }
    if (facetInputs.labels) { facetInputs.labels.checked = true; }
    if (facetInputs.dim) { facetInputs.dim.checked = false; }
    tagButtons.forEach(function (button) { button.setAttribute("aria-pressed", "false"); });
  }

  // §4.4, A9: staleness is re-read when the page comes back to the front.
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState !== "visible") { return; }
    nowMs = Date.now();
    if (staleCount) { staleCount.textContent = String(staleNowCount()); }
    refresh();
    renderDrawer();
  });

  // === drawing ===
  var SVG_NS = "http://www.w3.org/2000/svg";
  // Spec §12.5: read once. A clobbered value (an element named
  // OKF_SCHEDULER) is not a function and is ignored.
  var schedule = typeof window.OKF_SCHEDULER === "function"
    ? window.OKF_SCHEDULER
    : function (callback) { window.requestAnimationFrame(callback); };
  var REDRAW_EVERY = 25;
  // Under prefers-reduced-motion the layout runs in the same slices but is
  // drawn at its start and at its end only: no animation (read every frame,
  // so a change of the preference applies to the layout running).
  var calm = typeof window.matchMedia === "function" ? window.matchMedia("(prefers-reduced-motion: reduce)") : null;
  // The drawing of the shown set: its keys, slots, nodes and edges.
  var drawing = null;
  // The <svg> and its layers, made once. A node (by key) and a link (by
  // model edge) are made once too, the first time they are drawn, and every
  // later drawing that shows them reuses them: a filter change does not
  // rebuild what it keeps, and the layout moves nodes, never remakes them.
  var stage = null;
  var nodeCache = [];
  var lineCache = [];
  var simulation = null;
  var generation = 0;
  var transform = { a: 0, b: 0, s: 1 };
  var userMovedView = false;
  var intent = -1;
  // When the pointerup that ended a drag was sent (its timeStamp), or null:
  // the click that follows that release, within a moment, is the drag's.
  var suppressedAt = null;
  var SUPPRESS_WINDOW = 250;
  // Every node ever made, by its <g>.
  var nodeOfElement = new Map();

  function svgEl(name, className) {
    var created = document.createElementNS(SVG_NS, name);
    if (className) { created.setAttribute("class", className); }
    return created;
  }

  function num(v) {
    return String(Math.round(v * 100) / 100);
  }

  function marker(id, size, className) {
    var m = svgEl("marker");
    m.setAttribute("id", id);
    m.setAttribute("viewBox", "0 0 10 10");
    m.setAttribute("refX", "9");
    m.setAttribute("refY", "5");
    m.setAttribute("markerWidth", String(size));
    m.setAttribute("markerHeight", String(size));
    m.setAttribute("orient", "auto-start-reverse");
    var head = svgEl("path", className);
    head.setAttribute("d", "M0 0 L10 5 L0 10 Z");
    m.appendChild(head);
    return m;
  }

  function open(key) {
    var href = pageOf(key);
    if (href === null) { return; }
    var event = new CustomEvent("okf:navigate", { cancelable: true, detail: { href: href } });
    if (document.dispatchEvent(event)) { window.location.assign(href); }
  }

  // A node's shapes are drawn once by OkfShapes around (0, 0), its label
  // under them (G11); moveNode places the whole <g>.
  function makeNode(key) {
    var kind = kindOf(key);
    var size = shapes.SIZES.graph[kind];
    var g = shapes.node(kind, 0, 0, size.size, size);
    g.setAttribute("tabindex", "-1");
    g.setAttribute("role", "button");
    g.setAttribute("aria-label", key < N
      ? concepts[key].title + ", " + shapes.typeLabel(concepts[key].type)
      : nameOf(key));
    if (key >= N) { g.classList.add("okf-graph-ghost"); }
    var title = svgEl("title");
    title.textContent = nameOf(key);
    g.insertBefore(title, g.firstChild);
    var label = svgEl("text", key < N ? "okf-graph-label" : "okf-graph-label okf-graph-ghost-label");
    label.setAttribute("text-anchor", "middle");
    label.setAttribute("x", "0");
    label.setAttribute("y", num(size.size / 2 + 16));
    label.textContent = cutLabel(key < N ? lastSegment(idOf(key)) : nameOf(key));
    g.appendChild(label);
    var node = {
      key: key, kind: kind, size: size, g: g, title: title, label: label,
      chars: labelChars(label.textContent), x: 0, y: 0,
    };
    g.addEventListener("click", function (e) {
      var swallow = suppressedAt !== null && Math.abs(e.timeStamp - suppressedAt) <= SUPPRESS_WINDOW;
      suppressedAt = null;
      if (swallow) { return; }
      select(key, "user");
    });
    g.addEventListener("dblclick", function () { open(key); });
    nodeOfElement.set(g, node);
    run(hooks.node, node);
    return node;
  }

  // One attribute: the <g>'s translate (spec §12.2, revision 12). Nothing is
  // remade: focus and listeners stay, and the browser moves what it drew.
  function moveNode(node, x, y) {
    if (!Number.isFinite(x) || !Number.isFinite(y)) { return; }
    node.g.setAttribute("transform", "translate(" + num(x) + " " + num(y) + ")");
    node.x = x;
    node.y = y;
  }

  function nodeFor(key) {
    if (!nodeCache[key]) { nodeCache[key] = makeNode(key); }
    return nodeCache[key];
  }

  // G12: from the source's edge to the target's, arrow at the target; two
  // opposite edges are shifted 3 apart, each to its own left.
  function placeEdge(e) {
    var a = drawing.nodes[e.from];
    var b = drawing.nodes[e.to];
    var dx = b.x - a.x;
    var dy = b.y - a.y;
    var d = Math.sqrt(dx * dx + dy * dy);
    if (!(d > 0) || !Number.isFinite(d)) { return; }
    var ux = dx / d;
    var uy = dy / d;
    var ox = e.reverse ? -uy * 3 : 0;
    var oy = e.reverse ? ux * 3 : 0;
    var ra = a.size.size / 2;
    var rb = b.size.size / 2 + 2;
    e.line.setAttribute("x1", num(a.x + ux * ra + ox));
    e.line.setAttribute("y1", num(a.y + uy * ra + oy));
    e.line.setAttribute("x2", num(b.x - ux * rb + ox));
    e.line.setAttribute("y2", num(b.y - uy * rb + oy));
  }

  function placeEdges() {
    drawing.edges.forEach(placeEdge);
  }

  // The links of the node in `slot` (in either direction), and only them.
  function placeEdgesOf(slot) {
    drawing.edgesOf[slot].forEach(placeEdge);
  }

  // Classes only: selection ring and blue edges (G14), dimmed nodes (G8),
  // hidden labels (G7). A theme change redraws nothing.
  function styleDrawing() {
    if (!drawing) { return; }
    drawing.svg.classList.toggle("okf-graph-nolabels", !state.labels);
    drawing.nodes.forEach(function (node) {
      node.g.classList.toggle("okf-selected", node.key === selected);
      // AT: the selection is announced, as it is in the list.
      if (node.key === selected) { node.g.setAttribute("aria-current", "true"); } else { node.g.removeAttribute("aria-current"); }
      node.g.classList.toggle("okf-graph-dim", node.key < N && view.matched[node.key] !== 1);
    });
    drawing.edges.forEach(function (e) {
      var out = edgeFrom[e.k] === selected;
      var inward = !out && edgeTo[e.k] === selected;
      e.line.classList.toggle("okf-graph-edge-out", out);
      e.line.classList.toggle("okf-graph-edge-in", inward);
      // Set only when it changes: a set attribute is a change for the
      // browser, even to the same value, and every link would be redrawn.
      var arrow = out || inward ? "url(#okf-graph-arrow-sel)" : "url(#okf-graph-arrow)";
      if (e.line.getAttribute("marker-end") !== arrow) { e.line.setAttribute("marker-end", arrow); }
    });
  }

  function canvasSize() {
    return { w: canvas.clientWidth || 800, h: canvas.clientHeight || 600 };
  }

  // No coordinate of the view or of a dragged node goes past this: far
  // enough for any layout, near enough that num() never prints an exponent.
  var COORD_LIMIT = 1e6;

  function limited(v) {
    return Math.max(-COORD_LIMIT, Math.min(COORD_LIMIT, v));
  }

  function setTransform(a, b, s) {
    if (!Number.isFinite(a) || !Number.isFinite(b) || !Number.isFinite(s) || s <= 0) { return; }
    a = limited(a);
    b = limited(b);
    transform = { a: a, b: b, s: s };
    if (drawing) {
      drawing.viewport.setAttribute("transform",
        "translate(" + num(a) + " " + num(b) + ") scale(" + String(Math.round(s * 1000) / 1000) + ")");
    }
  }

  var MIN_SCALE = 0.05;
  var MAX_SCALE = 4;

  // Space Mono 11.5 px: about 7 px an advance, to count a label's width. An
  // estimate, never a measure: the layout uses it, and the layout must be the
  // same in every browser whatever its fonts.
  var LABEL_ADVANCE = 7;

  // A node's shape and its label (estimated width, ending 20 below the
  // shape), in graph coordinates; with `outline`, also its focus contour (G14:
  // a square of side + 20, centred, its stroke half outside it). The one
  // measure of what "the node" covers, for the layout's boxes, the fit and
  // bringing a focused node into view.
  function nodeBox(node, outline) {
    var h = node.size.size / 2;
    var reach = outline ? h + 10 + node.size.focus / 2 : h;
    var wide = Math.max(reach, node.chars * LABEL_ADVANCE / 2);
    return { l: node.x - wide, r: node.x + wide, t: node.y - reach, b: node.y + h + 20 };
  }

  // The boxes okf-sim keeps apart: per node [halfWidth, above, below] around
  // its centre (§12.5). Labels count whether shown or not (G7 hides them
  // without moving anything).
  function simBoxes() {
    return drawing.nodes.map(function (node) {
      var box = nodeBox({ x: 0, y: 0, size: node.size, chars: node.chars });
      return [box.r, -box.t, box.b];
    });
  }

  // "Fit": every drawn node and its label inside the canvas, never above
  // FIT_CAP, so labels stay near their 11.5 design size: FIT_SIDE beside the
  // drawing, FIT_TOP above and below it, where the status line, the zoom box
  // and the legend sit.
  var FIT_CAP = 1.25;
  var FIT_SIDE = 24;
  var FIT_TOP = 56;

  // While the layout of a graph of more than PAINT_SLICE nodes runs, its view
  // keeps its scale as long as the drawing fits in it and is no more than
  // ZOOM_SLACK times too small for it; one that outgrew it gets ZOOM_ROOM of
  // the fitting scale, room to grow. A new scale lays every label out again
  // (about 50 ms of rendering at 1 400 nodes in Chromium), a move does not.
  // The layout's last paint, and every other fit, is exact.
  var ZOOM_SLACK = 1.5;
  var ZOOM_ROOM = 0.8;

  // `size`, when given, is the canvas measured by the caller (see paint);
  // `running`: a large graph's layout is under way (see ZOOM_SLACK).
  function fit(size, running) {
    if (!drawing || drawing.nodes.length === 0) { return; }
    var minX = Infinity;
    var minY = Infinity;
    var maxX = -Infinity;
    var maxY = -Infinity;
    drawing.nodes.forEach(function (node) {
      var box = nodeBox(node);
      minX = Math.min(minX, box.l);
      maxX = Math.max(maxX, box.r);
      minY = Math.min(minY, box.t);
      maxY = Math.max(maxY, box.b);
    });
    if (!size) { size = canvasSize(); }
    var s = Math.min((size.w - 2 * FIT_SIDE) / Math.max(maxX - minX, 1), (size.h - 2 * FIT_TOP) / Math.max(maxY - minY, 1));
    s = Math.max(MIN_SCALE, Math.min(FIT_CAP, s));
    if (running) {
      var kept = transform.s;
      if (s >= kept && s <= kept * ZOOM_SLACK) {
        s = kept;
      } else if (s < kept) {
        s = Math.max(MIN_SCALE, s * ZOOM_ROOM);
      }
    }
    setTransform(size.w / 2 - (minX + maxX) / 2 * s, size.h / 2 - (minY + maxY) / 2 * s, s);
  }

  function centerOn(key, size) {
    var slot = drawing ? drawing.slotOf[key] : -1;
    if (slot < 0) { return; }
    var node = drawing.nodes[slot];
    if (!size) { size = canvasSize(); }
    setTransform(size.w / 2 - node.x * transform.s, size.h / 2 - node.y * transform.s, transform.s);
  }

  // Until the reader pans or zooms, the view follows the layout: fitted, and
  // centred on the node a fragment selected.
  function applyIntent(size, running) {
    if (!drawing || userMovedView) { return; }
    fit(size, running === true);
    if (intent >= 0) { centerOn(intent, size); }
  }

  // The whole drawing at once (a drag that stops the layout; the layout's
  // own paints go through paintSlice). Geometry only: no class changes here,
  // buildDrawing styles once and styleDrawing runs on every change of state.
  // The canvas is measured first: measured after the nodes moved, it would
  // make the browser lay the whole drawing out at once, inside this task.
  function paint(positions) {
    var size = canvasSize();
    drawing.nodes.forEach(function (node, slot) { moveNode(node, positions[2 * slot], positions[2 * slot + 1]); });
    placeEdges();
    applyIntent(size);
  }

  function stopLayout() {
    generation++;
    if (simulation) {
      simulation.cancel();
      simulation = null;
    }
  }

  // A layout's paint moves at most PAINT_SLICE nodes a frame (and their
  // links): the browser restyles, lays out and repaints only what moved, so
  // a frame of a large graph stays short. The view follows (Fit, a centred
  // fragment) once every node is placed: in the same frame when one slice
  // was enough, else in a frame of its own, since a new view repaints all.
  var PAINT_SLICE = 500;

  // Draws `positions` from slot `from` on, one slice; returns where the
  // next slice starts (the node count once all are placed).
  function paintSlice(positions, from) {
    var end = Math.min(drawing.nodes.length, from + PAINT_SLICE);
    for (var slot = from; slot < end; slot++) {
      moveNode(drawing.nodes[slot], positions[2 * slot], positions[2 * slot + 1]);
    }
    for (var moved = from; moved < end; moved++) { placeEdgesOf(moved); }
    return end;
  }

  // The layout runs a slice of work a frame (okf-sim's budget). Every
  // REDRAW_EVERY iterations it is drawn, slice by slice, from a copy of its
  // positions while it goes on; its end is always drawn, and only then is it
  // "done". Under reduced motion, only its start and its end are drawn.
  // `size`: the canvas, measured before the drawing was changed.
  function startLayout(size) {
    stopLayout();
    var mine = generation;
    var current = Sim.create({ nodeCount: drawing.keys.length, edges: drawing.simEdges, boxes: simBoxes() }, null);
    if (current === null) { return; }
    simulation = current;
    canvas.setAttribute("data-okf-layout", "running");
    var count = drawing.nodes.length;
    var paintedAt = 0;
    var finished = false;
    var shown = current.positions();
    var next = 0;
    var fitDue = false;
    // One slice of the paint under way, or its fit.
    function paintStep() {
      if (next < count) {
        next = paintSlice(shown, next);
        if (next < count) { return; }
        if (count <= PAINT_SLICE) {
          applyIntent(size);
        } else {
          fitDue = true;
        }
      } else if (fitDue) {
        fitDue = false;
        applyIntent(size, !finished);
      }
    }
    paintStep();
    function frame() {
      if (mine !== generation) { return; }
      // Measured before anything moves (see paint).
      size = canvasSize();
      if (!finished) {
        var r = current.step();
        finished = r.done;
        var still = calm !== null && calm.matches === true;
        // The end replaces a paint still under way: it is the one to show.
        if (r.done || (next === count && !fitDue && !still && r.iterations - paintedAt >= REDRAW_EVERY)) {
          paintedAt = r.iterations;
          shown = current.positions();
          next = 0;
          fitDue = false;
        }
      }
      paintStep();
      if (finished && next === count && !fitDue) {
        simulation = null;
        canvas.setAttribute("data-okf-layout", "done");
        return;
      }
      schedule(frame);
    }
    schedule(frame);
  }

  function makeStage() {
    var svg = svgEl("svg", "okf-graph-svg");
    svg.setAttribute("role", "group");
    var defs = svgEl("defs");
    defs.appendChild(marker("okf-graph-arrow", 7, "okf-graph-arrowhead"));
    defs.appendChild(marker("okf-graph-arrow-sel", 8, "okf-graph-arrowhead-sel"));
    svg.appendChild(defs);
    var viewport = svgEl("g", "okf-graph-viewport");
    var edgeLayer = svgEl("g", "okf-graph-edges");
    var nodeLayer = svgEl("g", "okf-graph-nodes");
    viewport.appendChild(edgeLayer);
    viewport.appendChild(nodeLayer);
    svg.appendChild(viewport);
    stage = { svg: svg, viewport: viewport, edgeLayer: edgeLayer, nodeLayer: nodeLayer };
    run(hooks.drawing, stage);
  }

  // Makes `layer`'s children exactly `wanted`, in that order, given that the
  // ones already there are in that order too (both follow index order): the
  // others leave (`left` is told), the kept ones are not moved -- a moved
  // element is restyled -- and the new ones go in at their place.
  function syncLayer(layer, wanted, left) {
    var keep = new Set(wanted);
    for (var child = layer.firstChild; child !== null;) {
      var next = child.nextSibling;
      if (!keep.has(child)) {
        layer.removeChild(child);
        if (left) { left(child); }
      }
      child = next;
    }
    var cursor = layer.firstChild;
    for (var i = 0; i < wanted.length; i++) {
      if (wanted[i] === cursor) {
        cursor = cursor.nextSibling;
      } else {
        layer.insertBefore(wanted[i], cursor);
      }
    }
  }

  function buildDrawing() {
    stopLayout();
    var size = canvasSize();
    if (!stage) { makeStage(); }
    var keys = view.keys.slice();
    var slotOf = new Int32Array(total).fill(-1);
    keys.forEach(function (key, slot) { slotOf[key] = slot; });
    stage.svg.setAttribute("aria-label", "Graph of " + plural(keys.length, "node", "nodes"));
    var edges = [];
    var simEdges = [];
    var pairs = new Set();
    for (var k = 0; k < M; k++) {
      var f = edgeFrom[k];
      var t = edgeTo[k];
      // A self-link is listed (drawer, list) but not drawn; the simulation
      // takes no loop (§12.5).
      if (f === t || slotOf[f] < 0 || slotOf[t] < 0) { continue; }
      if (!lineCache[k]) { lineCache[k] = svgEl("line", t >= N ? "okf-graph-edge okf-graph-edge-ghost" : "okf-graph-edge"); }
      edges.push({ k: k, line: lineCache[k], from: slotOf[f], to: slotOf[t], reverse: false });
      simEdges.push([slotOf[f], slotOf[t]]);
      pairs.add(slotOf[f] * keys.length + slotOf[t]);
    }
    var edgesOf = keys.map(function () { return []; });
    edges.forEach(function (e) {
      e.reverse = pairs.has(e.to * keys.length + e.from);
      edgesOf[e.from].push(e);
      edgesOf[e.to].push(e);
    });
    var nodes = keys.map(nodeFor);
    syncLayer(stage.edgeLayer, edges.map(function (e) { return e.line; }), null);
    // A node that leaves while focused keeps no contour for its return (a
    // removed element is not always sent a blur).
    syncLayer(stage.nodeLayer, nodes.map(function (node) { return node.g; }), function (g) { g.classList.remove("okf-focused"); });
    if (stage.svg.parentNode !== canvas) {
      clear(canvas);
      canvas.appendChild(stage.svg);
    }
    drawing = { keys: keys, slotOf: slotOf, svg: stage.svg, viewport: stage.viewport, nodes: nodes, edges: edges, edgesOf: edgesOf, simEdges: simEdges };
    userMovedView = false;
    setTransform(transform.a, transform.b, transform.s);
    styleDrawing();
    startLayout(size);
  }

  hooks.refresh.push(function () {
    if (view.over) {
      stopLayout();
      clear(canvas);
      canvas.removeAttribute("data-okf-layout");
      drawing = null;
      return;
    }
    if (drawing && sameKeys(drawing.keys, view.keys)) {
      styleDrawing();
      return;
    }
    buildDrawing();
  });
  hooks.select.push(styleDrawing);
  // The canvas was hidden (list mode, or above the limit) and is drawn again:
  // its size was unknown, so fit again unless the reader moved the view.
  hooks.show.push(applyIntent);

  // === fragment and history ===

  function decodeFragment(hash) {
    var h = String(hash || "");
    if (h.charAt(0) === "#") { h = h.slice(1); }
    try { return decodeURIComponent(h); } catch (e) { return h; }
  }

  // §12.5: replaceState, or location.replace when replaceState throws (a
  // file:// document's null origin); never a new history entry. A ghost or
  // no selection leaves no fragment.
  function writeUrl(origin) {
    if (origin !== "user") { return; }
    var id = selected >= 0 && selected < N ? concepts[selected].id : "";
    if (decodeFragment(window.location.hash) === id) { return; }
    try {
      window.history.replaceState(null, "", id === "" ? window.location.pathname + window.location.search : "#" + id);
    } catch (err) {
      window.location.replace(id === "" ? "#" : "#" + id);
    }
  }
  hooks.select.push(writeUrl);

  // A concept's id (exact, concepts only): facets back to their defaults,
  // node selected and centred. Anything else: no selection, Fit.
  function followFragment(hash) {
    var id = decodeFragment(hash);
    var key = id === "" ? undefined : byId.get(id);
    userMovedView = false;
    if (key === undefined) {
      intent = -1;
      select(-1, "url");
      applyIntent();
      return;
    }
    resetFacets();
    intent = key;
    refresh();
    // The refresh may have restored the focus to a node, and revealing it is
    // the reader's keyboard talking, not a view they moved: a fragment is
    // explicit navigation, it fits and centres whatever happened meanwhile.
    userMovedView = false;
    select(key, "url");
    applyIntent();
  }

  // The page's own location.replace fires one hashchange per call, so two
  // selections in one task fire two. A fragment that names what is already
  // selected is that echo (the URL always states the selection), never a
  // reader's move: no flag to run out of, however many fire.
  window.addEventListener("hashchange", function () {
    var id = decodeFragment(window.location.hash);
    if (id === (selected >= 0 && selected < N ? concepts[selected].id : "")) { return; }
    followFragment(window.location.hash);
  });

  // === keyboard ===
  // One tab stop for the whole graph (roving tabindex), §8.
  var active = -1;
  var focusWasOnNode = false;
  var focusWasInList = false;
  var previousActive = -1;
  canvas.tabIndex = -1;
  listBox.tabIndex = -1;

  function nodeOfKey(key) {
    if (!drawing || key < 0) { return null; }
    var slot = drawing.slotOf[key];
    return slot >= 0 ? drawing.nodes[slot] : null;
  }

  function setActive(key) {
    var old = nodeOfKey(active);
    if (old) { old.g.setAttribute("tabindex", "-1"); }
    active = key;
    var now = nodeOfKey(key);
    if (now) { now.g.setAttribute("tabindex", "0"); }
  }

  function focusNode(key) {
    var node = nodeOfKey(key);
    if (!node) { return; }
    setActive(key);
    reveal(node);
    node.g.focus();
  }

  // The next drawn node after `key` in index order, else the previous one.
  function nearestDrawn(key) {
    for (var k = key + 1; k < total; k++) { if (nodeOfKey(k)) { return k; } }
    for (var j = key - 1; j >= 0; j--) { if (nodeOfKey(j)) { return j; } }
    return -1;
  }

  function stepInOrder(key, delta) {
    var slot = drawing.slotOf[key] + delta;
    return slot >= 0 && slot < drawing.keys.length ? drawing.keys[slot] : -1;
  }

  // The four cones partition the plane: a neighbour belongs to the arrow of
  // the larger of |dx| and |dy| (the horizontal one on a tie, and for a
  // neighbour at the very same position, whose angle is 0). So every
  // neighbour is reachable by exactly one arrow.
  function coneOf(dx, dy) {
    if (Math.abs(dx) >= Math.abs(dy)) { return dx >= 0 ? "ArrowRight" : "ArrowLeft"; }
    return dy > 0 ? "ArrowDown" : "ArrowUp";
  }

  // The drawn neighbours of `key` (link in either direction) in the cone of
  // `arrow`: nearest first, then index order.
  function neighboursToward(key, arrow) {
    var from = nodeOfKey(key);
    if (!from) { return []; }
    var found = [];
    var seen = new Set();
    outOf[key].map(function (k) { return edgeTo[k]; })
      .concat(into[key].map(function (k) { return edgeFrom[k]; }))
      .forEach(function (other) {
        if (other === key || seen.has(other)) { return; }
        seen.add(other);
        var node = nodeOfKey(other);
        if (!node) { return; }
        var dx = node.x - from.x;
        var dy = node.y - from.y;
        if (coneOf(dx, dy) === arrow) { found.push({ key: other, d: dx * dx + dy * dy }); }
      });
    found.sort(function (a, b) { return a.d - b.d || a.key - b.key; });
    return found.map(function (f) { return f.key; });
  }

  // An arrow reaches the nearest neighbour in its cone; the same arrow pressed
  // again from there goes on through the origin's other neighbours in that
  // cone, wrapping to the nearest after the farthest (the cycle is endless, so
  // the reader is never stuck and no neighbour is out of reach). A cone with a
  // single neighbour is no cycle: the same arrow then goes on from it.
  // `cycle` remembers the origin; any other key ends it (a refresh needs not:
  // the cone is recomputed from the origin on every press).
  var cycle = null;

  function neighbourToward(key, arrow, prior) {
    if (prior && prior.arrow === arrow && prior.landed === key) {
      var around = neighboursToward(prior.origin, arrow);
      var at = around.indexOf(key);
      if (around.length > 1 && at >= 0) {
        var next = around[(at + 1) % around.length];
        cycle = { origin: prior.origin, arrow: arrow, landed: next };
        return next;
      }
    }
    var list = neighboursToward(key, arrow);
    if (list.length === 0) { return -1; }
    if (list.length > 1) { cycle = { origin: key, arrow: arrow, landed: list[0] }; }
    return list[0];
  }

  function onKey(e) {
    if (e.altKey || e.ctrlKey || e.metaKey) { return; }
    var node = nodeOfElement.get(e.target);
    if (!node) { return; }
    var key = node.key;
    var target = -1;
    var prior = cycle;
    cycle = null;
    switch (e.key) {
      case "ArrowRight":
      case "ArrowLeft":
      case "ArrowUp":
      case "ArrowDown":
        target = neighbourToward(key, e.key, prior);
        break;
      case "PageDown":
        target = stepInOrder(key, 1);
        break;
      case "PageUp":
        target = stepInOrder(key, -1);
        break;
      case "Home":
        target = drawing.keys[0];
        break;
      case "End":
        target = drawing.keys[drawing.keys.length - 1];
        break;
      case "Enter":
        e.preventDefault();
        open(key);
        return;
      case " ":
      case "Spacebar":
        e.preventDefault();
        select(key, "user");
        return;
      default:
        return;
    }
    e.preventDefault();
    if (target >= 0) { focusNode(target); }
  }

  // Set by any press, cleared by the next key: the focus a node takes in
  // between came from the pointer.
  var pointerFocus = false;
  document.addEventListener("pointerdown", function () { pointerFocus = true; }, true);
  document.addEventListener("keydown", function () { pointerFocus = false; }, true);

  hooks.node.push(function (node) {
    node.g.addEventListener("focus", function () {
      setActive(node.key);
      node.g.classList.add("okf-focused");
      // Tab arrives here without focusNode(): the keyboard reveals. A press
      // (left, right, a tap's compatibility mousedown) focuses the node under
      // the pointer: that is not navigation, and the reader sees it.
      if (!pointerFocus) { reveal(node); }
    });
    node.g.addEventListener("blur", function () { node.g.classList.remove("okf-focused"); });
  });
  hooks.drawing.push(function (made) { made.svg.addEventListener("keydown", onKey); });

  // The reader who selects (a click, the list, a fragment) lands on the
  // selection when tabbing back to the graph.
  hooks.select.push(function () { if (nodeOfKey(selected)) { setActive(selected); } });

  hooks.beforeRefresh.push(function () {
    var focused = document.activeElement;
    focusWasOnNode = !!(focused && nodeOfElement.has(focused));
    focusWasInList = !!(focused && listBox.contains(focused));
    previousActive = active;
  });

  // §8: if a filter hides the active node, the stop (and the focus, if a
  // node had it) goes to the next drawn node in index order, else the
  // previous one, else the graph's container.
  hooks.refresh.push(function () {
    var target = -1;
    if (drawing) {
      drawing.nodes.forEach(function (node) { node.g.setAttribute("tabindex", "-1"); });
      if (previousActive >= 0) {
        target = nodeOfKey(previousActive) ? previousActive : nearestDrawn(previousActive);
      } else if (nodeOfKey(selected)) {
        target = selected;
      } else if (drawing.keys.length > 0) {
        target = drawing.keys[0];
      }
    }
    active = -1;
    if (target >= 0) { setActive(target); }
  });

  // The focus moves once updateMode() has shown and hidden the two views: a
  // hidden element takes no focus. A node that had it keeps it (on the stop);
  // the list's focus, when the list goes away, goes to the stop, else to the
  // List toggle; an emptied graph hands the focus to its container.
  hooks.settled.push(function () {
    var showList = listMode || view.over;
    if (focusWasOnNode) {
      if (active >= 0) {
        focusNode(active);
      } else {
        (showList ? listBox : canvas).focus();
      }
    } else if (focusWasInList) {
      if (showList) {
        if (!listBox.contains(document.activeElement)) { listBox.focus(); }
      } else if (active >= 0) {
        focusNode(active);
      } else {
        listButton.focus();
      }
    }
  });

  // The container takes the focus only when the graph is empty: it needs a
  // name for that.
  canvas.setAttribute("role", "group");
  canvas.setAttribute("aria-label", "Graph");

  // === pan, zoom, drag ===
  var MINUS = String.fromCharCode(0x2212);
  // A press that moves under this many pixels is a click.
  var DRAG_THRESHOLD = 3;
  // A focused node is kept this far (px) inside the canvas, so its focus
  // contour is too.
  var REVEAL_MARGIN = 8;

  // Zoom around (px, py), in canvas pixels. A zoom the clamps refuse, or on a
  // hidden or absent drawing, moves nothing and does not count as the reader
  // moving the view.
  function zoomBy(factor, px, py) {
    if (!drawing || canvas.hidden || !Number.isFinite(factor) || !Number.isFinite(px) || !Number.isFinite(py)) { return; }
    var s = Math.max(MIN_SCALE, Math.min(MAX_SCALE, transform.s * factor));
    if (s === transform.s) { return; }
    setTransform(px - (px - transform.a) * (s / transform.s), py - (py - transform.b) * (s / transform.s), s);
    userMovedView = true;
  }

  function toolButton(text, label, className, onClick) {
    var button = el("button", className, text);
    button.type = "button";
    if (label) { button.setAttribute("aria-label", label); }
    button.addEventListener("click", function () {
      if (drawing && !canvas.hidden) { onClick(); }
    });
    zoomBox.insertBefore(button, listButton);
    toolButtons.push(button);
    return button;
  }

  toolButton("+", "Zoom in", "okf-graph-tool okf-graph-zoom-step", function () {
    var size = canvasSize();
    zoomBy(1.25, size.w / 2, size.h / 2);
  });
  toolButton(MINUS, "Zoom out", "okf-graph-tool okf-graph-zoom-step", function () {
    var size = canvasSize();
    zoomBy(1 / 1.25, size.w / 2, size.h / 2);
  });
  toolButton("Fit", null, "okf-graph-tool", function () {
    userMovedView = false;
    intent = -1;
    applyIntent();
  });

  // One notch of a wheel (100 px, or 3 lines) is 1.1x, in proportion to the
  // delta and never more than a notch an event: a trackpad sends many small
  // ones. A delta of zero or not a number is not a zoom (and not taken: the
  // page may scroll sideways); every other is, even where the clamps leave
  // the view as it is, so that the page does not scroll under a reader who
  // reached the limit.
  canvas.addEventListener("wheel", function (e) {
    if (!drawing) { return; }
    if (!Number.isFinite(e.deltaY)) { return; }
    var d = Math.max(-1000, Math.min(1000, e.deltaY));
    if (e.deltaMode === 1) { d *= 33; } else if (e.deltaMode === 2) { d *= 400; }
    if (!Number.isFinite(d) || d === 0) { return; }
    e.preventDefault();
    d = Math.max(-100, Math.min(100, d));
    var r = canvas.getBoundingClientRect();
    zoomBy(Math.pow(1.1, -d / 100), e.clientX - r.left, e.clientY - r.top);
  }, { passive: false });

  // A press that moves DRAG_THRESHOLD px or more drags: the background pans
  // the view, a node moves alone (after the layout, which the drag stops:
  // §4.2's determinism covers the initial layout only, and a dragged position
  // feeds nothing back into it). Both are followed by the increment since the
  // last event, so a wheel zoom in the middle of a drag cannot make the view
  // jump back.
  var gesture = null;

  canvas.addEventListener("pointerdown", function (e) {
    if (!drawing || e.button !== 0) { return; }
    // A second pointer (a second finger) is not this drag; the same pointer
    // pressing again is a new one.
    if (gesture && gesture.id !== e.pointerId) { return; }
    suppressedAt = null;
    var node = null;
    for (var t = e.target; t && t !== canvas; t = t.parentNode) {
      if (nodeOfElement.has(t)) {
        node = nodeOfElement.get(t);
        break;
      }
    }
    gesture = { id: e.pointerId, node: node, drawing: drawing, x0: e.clientX, y0: e.clientY, x: e.clientX, y: e.clientY, moved: false };
  });

  // Moves and releases are followed on window, not by pointer capture: a
  // capture taken on press retargets a plain click (and dblclick) to the
  // canvas, so the node would never be selected or opened.
  window.addEventListener("pointermove", function (e) {
    if (!gesture || e.pointerId !== gesture.id) { return; }
    if (!drawing || (gesture.node && gesture.drawing !== drawing)) {
      // The drawing was rebuilt (or taken away) under the node drag: the
      // node may still be drawn, but where the new layout puts it.
      gesture = null;
      return;
    }
    // A mouse released outside the window sends no pointerup: its next move
    // says no button is down.
    if (e.pointerType === "mouse" && e.buttons === 0) {
      endGesture(e);
      return;
    }
    if (!Number.isFinite(e.clientX) || !Number.isFinite(e.clientY)) { return; }
    if (!gesture.moved) {
      var dx = e.clientX - gesture.x0;
      var dy = e.clientY - gesture.y0;
      if (dx * dx + dy * dy < DRAG_THRESHOLD * DRAG_THRESHOLD) { return; }
      gesture.moved = true;
      // The view is the reader's from here on: nothing refits it.
      userMovedView = true;
      if (gesture.node && simulation) {
        // The layout moved the node since the last paint: drag it from where
        // the layout leaves it.
        paint(simulation.positions());
        stopLayout();
        canvas.setAttribute("data-okf-layout", "done");
      }
    }
    var mx = e.clientX - gesture.x;
    var my = e.clientY - gesture.y;
    gesture.x = e.clientX;
    gesture.y = e.clientY;
    if (gesture.node) {
      moveNode(gesture.node, limited(gesture.node.x + mx / transform.s), limited(gesture.node.y + my / transform.s));
      placeEdgesOf(drawing.slotOf[gesture.node.key]);
    } else {
      setTransform(transform.a + mx, transform.b + my, transform.s);
    }
  });

  // A drag that ends at pointerup swallows the click that follows that
  // release (a moment after it, see SUPPRESS_WINDOW); a release the page never
  // saw (outside the window) has no click to swallow.
  function endGesture(e) {
    if (!gesture || e.pointerId !== gesture.id) { return; }
    if (gesture.moved && e.type === "pointerup") { suppressedAt = e.timeStamp; }
    gesture = null;
  }
  // A cancelled pointer is followed by no click.
  function dropGesture(e) {
    if (gesture && e.pointerId === gesture.id) { gesture = null; }
  }
  // A touch captures the element it lands on, and a browser may report that
  // capture lost while the node under the finger moves (it used to be
  // redrawn; since revision 12 it is translated), in the middle of a drag
  // that goes on, and the pointerup or pointercancel still comes. So a node
  // drag does not end there. A pan moves nothing under the pointer: a
  // capture lost during one is not ours, and ends it.
  function captureLost(e) {
    if (gesture && !gesture.node) { dropGesture(e); }
  }
  // A click after a release over another element lands on their common
  // ancestor, not on the node: it is the drag's too, once it came.
  window.addEventListener("click", function () { suppressedAt = null; });
  window.addEventListener("pointerup", endGesture);
  window.addEventListener("pointercancel", dropGesture);
  window.addEventListener("lostpointercapture", captureLost);
  window.addEventListener("blur", function () { gesture = null; });

  // 2.4.11: a focused node is not left outside the canvas, or under its
  // edge. The view pans by the least that brings the node's shape, its label
  // and its focus contour (G14, side + 20, stroke included) inside
  // REVEAL_MARGIN (a box wider than the canvas is centred), and a view that
  // moved is the reader's: it counts as one (the layout, the list's return, a
  // rebuild's fit would otherwise take it back). It is keyboard navigation
  // (arrows, paging, Tab: see pointerFocus), so a pointer press and a drag
  // are left alone.
  function revealShift(lo, hi, extent) {
    if (hi - lo > extent - 2 * REVEAL_MARGIN) { return extent / 2 - (lo + hi) / 2; }
    if (lo < REVEAL_MARGIN) { return REVEAL_MARGIN - lo; }
    if (hi > extent - REVEAL_MARGIN) { return extent - REVEAL_MARGIN - hi; }
    return 0;
  }

  function reveal(node) {
    if (!drawing || canvas.hidden || gesture) { return; }
    var size = canvasSize();
    var box = nodeBox(node, true);
    var s = transform.s;
    var dx = revealShift(transform.a + box.l * s, transform.a + box.r * s, size.w);
    var dy = revealShift(transform.b + box.t * s, transform.b + box.b * s, size.h);
    if (dx === 0 && dy === 0) { return; }
    setTransform(transform.a + dx, transform.b + dy, s);
    userMovedView = true;
  }

  // === start-up ===
  buildFacets();
  refresh();
  renderDrawer();
  updateReadingView();
  // A fragment naming a concept selects and centres it. Any other leaves the
  // page as refresh() drew it -- nothing selected, the layout's own fit --
  // without measuring the canvas again (a layout of the whole new drawing).
  var startId = decodeFragment(window.location.hash);
  if (startId !== "" && byId.has(startId)) { followFragment(window.location.hash); }
})();
