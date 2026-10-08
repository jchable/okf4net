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
  var hooks = { beforeRefresh: [], refresh: [], select: [], node: [], drawing: [], show: [] };
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

  // The list is built only while it is shown, and only when the shown set has
  // changed since it was last built: its entries and relations depend on
  // nothing else (about ten elements per node, so ~16k at NODE_LIMIT).
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
    if (showList && listStale) {
      renderList();
      listStale = false;
    }
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
    var previousKeys = view ? view.keys : null;
    view = compute();
    if (!sameKeys(previousKeys, view.keys)) { listStale = true; }
    renderStatus();
    run(hooks.refresh);
    updateMode();
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
      if (site.isStale(index.concepts[i].staleAfterMs, nowMs)) { n++; }
    }
    return n;
  }

  function buildFacets() {
    var details = el("details", "okf-facets-details");
    details.appendChild(el("summary", "okf-facets-summary", "Filters"));
    var body = el("div", "okf-facets-body");
    details.appendChild(body);

    // G3: one row per entry of `types`, index order; the page's type legend.
    var typeSection = facetSection("Type", "type");
    var typeList = el("div", "okf-facet-list");
    index.types.forEach(function (type, t) {
      var slot = isIndex(type.slot, shapes.KINDS.length) ? type.slot : shapes.OTHER_SLOT;
      var r = checkboxRow(true, shapes.icon(shapes.KINDS[slot], "icon"), shapes.typeLabel(type.name), type.count,
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
    index.concepts.forEach(function (concept) { trustCounts[trustSlot(concept.trust)]++; });
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
    index.concepts.forEach(function (concept) {
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
  var drawing = null;
  var simulation = null;
  var generation = 0;
  var transform = { a: 0, b: 0, s: 1 };
  var userMovedView = false;
  var intent = -1;
  var suppressClick = false;
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
    if (key < 0 || key >= N) { return; }
    var href = site.resolve(root, index.concepts[key].path);
    var event = new CustomEvent("okf:navigate", { cancelable: true, detail: { href: href } });
    if (document.dispatchEvent(event)) { window.location.assign(href); }
  }

  function makeNode(key) {
    var kind = kindOf(key);
    var size = shapes.SIZES.graph[kind];
    var g = shapes.node(kind, 0, 0, size.size, size);
    g.setAttribute("tabindex", "-1");
    g.setAttribute("role", "button");
    g.setAttribute("aria-label", key < N
      ? index.concepts[key].title + ", " + shapes.typeLabel(index.concepts[key].type)
      : nameOf(key));
    if (key >= N) { g.classList.add("okf-graph-ghost"); }
    var title = svgEl("title");
    title.textContent = nameOf(key);
    g.insertBefore(title, g.firstChild);
    var label = svgEl("text", key < N ? "okf-graph-label" : "okf-graph-label okf-graph-ghost-label");
    label.setAttribute("text-anchor", "middle");
    label.textContent = key < N ? lastSegment(idOf(key)) : nameOf(key);
    g.appendChild(label);
    var node = { key: key, kind: kind, size: size, g: g, title: title, label: label, x: 0, y: 0 };
    g.addEventListener("click", function () {
      if (suppressClick) {
        suppressClick = false;
        return;
      }
      select(key, "user");
    });
    g.addEventListener("dblclick", function () { open(key); });
    nodeOfElement.set(g, node);
    run(hooks.node, node);
    return node;
  }

  // The node's shapes are redrawn by OkfShapes at the new centre (absolute
  // coordinates, no transform); its <g>, <title> and label are kept, so focus
  // and listeners survive the move.
  function moveNode(node, x, y) {
    if (!Number.isFinite(x) || !Number.isFinite(y)) { return; }
    var fresh = shapes.node(node.kind, x, y, node.size.size, node.size);
    var child = node.title.nextSibling;
    while (child && child !== node.label) {
      var next = child.nextSibling;
      node.g.removeChild(child);
      child = next;
    }
    while (fresh.firstChild) { node.g.insertBefore(fresh.firstChild, node.label); }
    node.label.setAttribute("x", num(x));
    node.label.setAttribute("y", num(y + node.size.size / 2 + 16));
    node.x = x;
    node.y = y;
  }

  // G12: from the source's edge to the target's, arrow at the target; two
  // opposite edges are shifted 3 apart, each to its own left.
  function placeEdges() {
    drawing.edges.forEach(function (e) {
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
    });
  }

  // Classes only: selection ring and blue edges (G14), dimmed nodes (G8),
  // hidden labels (G7). A theme change redraws nothing.
  function styleDrawing() {
    if (!drawing) { return; }
    drawing.svg.classList.toggle("okf-graph-nolabels", !state.labels);
    drawing.nodes.forEach(function (node) {
      node.g.classList.toggle("okf-selected", node.key === selected);
      node.g.classList.toggle("okf-graph-dim", node.key < N && view.matched[node.key] !== 1);
    });
    drawing.edges.forEach(function (e) {
      var out = edgeFrom[e.k] === selected;
      var inward = !out && edgeTo[e.k] === selected;
      e.line.classList.toggle("okf-graph-edge-out", out);
      e.line.classList.toggle("okf-graph-edge-in", inward);
      e.line.setAttribute("marker-end", out || inward ? "url(#okf-graph-arrow-sel)" : "url(#okf-graph-arrow)");
    });
  }

  function canvasSize() {
    return { w: canvas.clientWidth || 800, h: canvas.clientHeight || 600 };
  }

  function setTransform(a, b, s) {
    if (!Number.isFinite(a) || !Number.isFinite(b) || !Number.isFinite(s) || s <= 0) { return; }
    transform = { a: a, b: b, s: s };
    if (drawing) {
      drawing.viewport.setAttribute("transform",
        "translate(" + num(a) + " " + num(b) + ") scale(" + String(Math.round(s * 1000) / 1000) + ")");
    }
  }

  var MIN_SCALE = 0.05;
  var MAX_SCALE = 4;

  // Space Mono 11.5 px: about 7 px an advance, to count a label's width.
  var LABEL_ADVANCE = 7;

  // "Fit": every drawn node, its label (estimated width, 20 below the shape)
  // inside the canvas with a margin, never above 1.5x.
  function fit() {
    if (!drawing || drawing.nodes.length === 0) { return; }
    var minX = Infinity;
    var minY = Infinity;
    var maxX = -Infinity;
    var maxY = -Infinity;
    drawing.nodes.forEach(function (node) {
      var h = node.size.size / 2;
      var wide = Math.max(h, node.label.textContent.length * LABEL_ADVANCE / 2);
      minX = Math.min(minX, node.x - wide);
      maxX = Math.max(maxX, node.x + wide);
      minY = Math.min(minY, node.y - h);
      maxY = Math.max(maxY, node.y + h + 20);
    });
    var size = canvasSize();
    var margin = 56;
    var s = Math.min((size.w - 2 * margin) / Math.max(maxX - minX, 1), (size.h - 2 * margin) / Math.max(maxY - minY, 1));
    s = Math.max(MIN_SCALE, Math.min(1.5, s));
    setTransform(size.w / 2 - (minX + maxX) / 2 * s, size.h / 2 - (minY + maxY) / 2 * s, s);
  }

  function centerOn(key) {
    var slot = drawing ? drawing.slotOf[key] : -1;
    if (slot < 0) { return; }
    var node = drawing.nodes[slot];
    var size = canvasSize();
    setTransform(size.w / 2 - node.x * transform.s, size.h / 2 - node.y * transform.s, transform.s);
  }

  // Until the reader pans or zooms, the view follows the layout: fitted, and
  // centred on the node a fragment selected.
  function applyIntent() {
    if (!drawing || userMovedView) { return; }
    fit();
    if (intent >= 0) { centerOn(intent); }
  }

  function paint(positions) {
    drawing.nodes.forEach(function (node, slot) { moveNode(node, positions[2 * slot], positions[2 * slot + 1]); });
    placeEdges();
    styleDrawing();
    applyIntent();
  }

  function stopLayout() {
    generation++;
    if (simulation) {
      simulation.cancel();
      simulation = null;
    }
  }

  function startLayout() {
    stopLayout();
    var mine = generation;
    var current = Sim.create({ nodeCount: drawing.keys.length, edges: drawing.simEdges }, null);
    if (current === null) { return; }
    simulation = current;
    canvas.setAttribute("data-okf-layout", "running");
    var paintedAt = 0;
    paint(current.positions());
    function frame() {
      if (mine !== generation) { return; }
      var r = current.step();
      if (r.done || r.iterations - paintedAt >= REDRAW_EVERY) {
        paintedAt = r.iterations;
        paint(current.positions());
      }
      if (r.done) {
        simulation = null;
        canvas.setAttribute("data-okf-layout", "done");
        return;
      }
      schedule(frame);
    }
    schedule(frame);
  }

  function buildDrawing() {
    stopLayout();
    clear(canvas);
    nodeOfElement = new Map();
    var keys = view.keys.slice();
    var slotOf = new Int32Array(total).fill(-1);
    keys.forEach(function (key, slot) { slotOf[key] = slot; });
    var svg = svgEl("svg", "okf-graph-svg");
    svg.setAttribute("role", "group");
    svg.setAttribute("aria-label", "Graph of " + plural(keys.length, "node", "nodes"));
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
    var edges = [];
    var simEdges = [];
    var pairs = new Set();
    for (var k = 0; k < M; k++) {
      var f = edgeFrom[k];
      var t = edgeTo[k];
      // A self-link is listed (drawer, list) but not drawn; the simulation
      // takes no loop (§12.5).
      if (f < 0 || f === t || slotOf[f] < 0 || slotOf[t] < 0) { continue; }
      var line = svgEl("line", t >= N ? "okf-graph-edge okf-graph-edge-ghost" : "okf-graph-edge");
      line.setAttribute("marker-end", "url(#okf-graph-arrow)");
      edgeLayer.appendChild(line);
      edges.push({ k: k, line: line, from: slotOf[f], to: slotOf[t], reverse: false });
      simEdges.push([slotOf[f], slotOf[t]]);
      pairs.add(slotOf[f] * keys.length + slotOf[t]);
    }
    edges.forEach(function (e) { e.reverse = pairs.has(e.to * keys.length + e.from); });
    drawing = { keys: keys, slotOf: slotOf, svg: svg, viewport: viewport, nodes: [], edges: edges, simEdges: simEdges };
    drawing.nodes = keys.map(function (key) {
      var node = makeNode(key);
      nodeLayer.appendChild(node.g);
      return node;
    });
    canvas.appendChild(svg);
    userMovedView = false;
    setTransform(transform.a, transform.b, transform.s);
    run(hooks.drawing);
    startLayout();
  }

  hooks.refresh.push(function () {
    if (view.over) {
      stopLayout();
      clear(canvas);
      canvas.removeAttribute("data-okf-layout");
      drawing = null;
      nodeOfElement = new Map();
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
  var expectedHash = null;

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
    var id = selected >= 0 && selected < N ? index.concepts[selected].id : "";
    if (decodeFragment(window.location.hash) === id) { return; }
    try {
      window.history.replaceState(null, "", id === "" ? window.location.pathname + window.location.search : "#" + id);
    } catch (err) {
      expectedHash = id;
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
    select(key, "url");
    applyIntent();
  }

  window.addEventListener("hashchange", function () {
    if (expectedHash !== null) {
      var mine = decodeFragment(window.location.hash) === expectedHash;
      expectedHash = null;
      if (mine) { return; }
    }
    followFragment(window.location.hash);
  });

  // === start-up ===
  buildFacets();
  refresh();
  renderDrawer();
  updateReadingView();
  followFragment(window.location.hash);
})();
