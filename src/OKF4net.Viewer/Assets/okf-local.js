// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The local graph of a concept page (spec §4.3, §12.4): the concept and its
// neighbours at one or two hops, in rings. This file holds window.OkfLocal,
// pure (no DOM, no clock, no randomness): from the site index alone it
// computes the drawn nodes and their ring positions, every index edge between
// them and the full neighbour list. tools/viewer-security-check calls it
// directly (cases/p2.js). The page part, which draws the result into
// #okf-context on a concept page, is added to this file by a later task of
// the slice.
//
// Node keys: a concept is its position in index.concepts; a ghost (an absent
// link target, never navigable) is concepts.length + its position in
// index.ghosts. Ascending key order is index order, concepts first, then
// ghosts: the order of the cap and of the angles. The JS never compares ids
// (spec §3.1).
(function () {
  "use strict";

  var CAP = 40; // nodes drawn, the centre included (spec A11)
  var VIEW = Object.freeze({ width: 298, height: 248, cx: 149, cy: 118 });
  // One ring at 1 hop (or when the second hop draws nothing), two at 2 hops.
  // Chosen, not measured off the mockup (its half-extents are not radii), on
  // three constraints that cases/p2.js pins for every node count up to the
  // cap: a shape (up to 13 from its centre) and its label (baseline 14 under
  // a shape, 3 of descender) stay inside the 298 x 248 view; no shape touches
  // the centre's selection square (+- 20) or its label, centred under it at
  // baseline cy + 34 (a band 120 wide, y 140 to 156); and each ring sits
  // clear of the other. The inner ring of 2 hops is the large one it is
  // because any ellipse a node can reach under that label would collide with
  // it, whatever the number of nodes.
  var ONE_RING = Object.freeze([Object.freeze({ rx: 127, ry: 95 })]);
  var TWO_RINGS = Object.freeze([Object.freeze({ rx: 106, ry: 72 }), Object.freeze({ rx: 134, ry: 98 })]);

  // To hundredths; a magnitude too large to hold hundredths (and to multiply
  // by 100 without overflowing) is returned as it is.
  function round(value) { return Math.abs(value) < 1e15 ? Math.round(value * 100) / 100 : value; }

  // A count never becomes Infinity, which would be no text and no JSON.
  function saturate(count) { return count > Number.MAX_SAFE_INTEGER ? Number.MAX_SAFE_INTEGER : count; }

  // [from, to, count, toGhost] as IndexScript writes it, turned into node
  // keys; null for anything else, so a damaged index draws less instead of
  // throwing.
  function ends(edge, conceptCount, ghostCount) {
    if (!Array.isArray(edge)) { return null; }
    var from = edge[0];
    var to = edge[1];
    if (edge[3] !== 0 && edge[3] !== 1) { return null; }
    var ghost = edge[3] === 1;
    if (!Number.isInteger(from) || from < 0 || from >= conceptCount) { return null; }
    if (!Number.isInteger(to) || to < 0 || to >= (ghost ? ghostCount : conceptCount)) { return null; }
    return { from: from, to: ghost ? conceptCount + to : to, count: Number.isInteger(edge[2]) && edge[2] > 0 ? saturate(edge[2]) : 1 };
  }

  // The neighbourhood of concept `centre` at `hops` (1 or 2). Membership is
  // undirected (links out and in); edges keep their direction. The second hop
  // never starts from a ghost; a self-link is no neighbour. At most four
  // passes over the edges and one over the nodes per hop: `work` counts them
  // (spec §7: counted, never timed).
  function build(index, centre, hops) {
    var C = index.concepts.length;
    var G = index.ghosts.length;
    var N = C + G;
    if (!Number.isInteger(centre) || centre < 0 || centre >= C) { throw new TypeError("okf-local: centre is not a concept position"); }
    if (hops !== 1 && hops !== 2) { throw new TypeError("okf-local: hops is not 1 or 2"); }
    var edges = index.edges;
    var work = 0;
    var k;
    var e;
    var dist = new Int8Array(N).fill(-1);
    var rel = new Int8Array(N);           // hop 1: 1 = the centre links to it, 2 = it links to the centre
    var via = new Int32Array(N).fill(-1); // hop 2: the smallest hop-1 concept key it is reached from
    dist[centre] = 0;

    for (k = 0; k < edges.length; k++) {
      work++;
      e = ends(edges[k], C, G);
      if (!e || e.from === e.to) { continue; }
      if (e.from === centre) {
        dist[e.to] = 1;
        rel[e.to] |= 1;
      } else if (e.to === centre) {
        dist[e.from] = 1;
        rel[e.from] |= 2;
      }
    }

    if (hops === 2) {
      for (k = 0; k < edges.length; k++) {
        work++;
        e = ends(edges[k], C, G);
        if (!e || e.from === e.to) { continue; }
        // e.from is always a concept; e.to may be a ghost, which never expands.
        if (dist[e.from] === 1 && dist[e.to] !== 0 && dist[e.to] !== 1) {
          dist[e.to] = 2;
          if (via[e.to] === -1 || e.from < via[e.to]) { via[e.to] = e.from; }
        }
        if (dist[e.to] === 1 && e.to < C && dist[e.from] !== 0 && dist[e.from] !== 1) {
          dist[e.from] = 2;
          if (via[e.from] === -1 || e.to < via[e.from]) { via[e.from] = e.to; }
        }
      }
    }

    var list = [];
    for (var d = 1; d <= hops; d++) {
      for (k = 0; k < N; k++) {
        work++;
        if (dist[k] === d) {
          list.push(Object.freeze({ key: k, dist: d, rel: d === 1 ? rel[k] : 0, via: d === 2 ? via[k] : -1 }));
        }
      }
    }

    // Drawn: the centre, then the list up to the cap (direct neighbours come
    // first in it). Their order on each ring is key order.
    var shown = list.slice(0, CAP - 1);
    var slot = new Int32Array(N).fill(-1);
    var nodes = [{ key: centre, dist: 0, x: VIEW.cx, y: VIEW.cy }];
    slot[centre] = 0;
    var rings = [[], []];
    for (k = 0; k < shown.length; k++) { rings[shown[k].dist - 1].push(shown[k].key); }
    var radii = rings[1].length > 0 ? TWO_RINGS : ONE_RING;
    for (var r = 0; r < radii.length; r++) {
      var m = rings[r].length;
      for (var j = 0; j < m; j++) {
        // Clockwise from the top, half a step off it: four neighbours sit on
        // the diagonals, as in the mockup. Math.cos and Math.sin are allowed
        // here (spec §4.3): the cross-engine rule binds the global simulation.
        var angle = -Math.PI / 2 + Math.PI / m + 2 * Math.PI * j / m;
        slot[rings[r][j]] = nodes.length;
        nodes.push({
          key: rings[r][j],
          dist: r + 1,
          x: round(VIEW.cx + radii[r].rx * Math.cos(angle)),
          y: round(VIEW.cy + radii[r].ry * Math.sin(angle)),
        });
      }
    }

    // Every index edge whose two ends are drawn, not only the ones walked.
    // A repeated record of one pair (IndexScript already merges them) adds
    // its count to the first.
    var drawn = [];
    var byPair = new Map();
    for (k = 0; k < edges.length; k++) {
      work++;
      e = ends(edges[k], C, G);
      if (!e || e.from === e.to || slot[e.from] === -1 || slot[e.to] === -1) { continue; }
      // Keyed by drawn slot (at most CAP of them): exact whatever N is.
      var pair = slot[e.from] * nodes.length + slot[e.to];
      if (byPair.has(pair)) {
        drawn[byPair.get(pair)].count = saturate(drawn[byPair.get(pair)].count + e.count);
        continue;
      }
      byPair.set(pair, drawn.length);
      // Dashed when the target is closer to the centre than the source
      // ("referenced by"), solid otherwise ("links to"), spec X7.
      drawn.push({ from: slot[e.from], to: slot[e.to], count: e.count, dashed: dist[e.to] < dist[e.from], paired: false });
    }
    for (k = 0; k < drawn.length; k++) {
      work++;
      drawn[k].paired = byPair.has(drawn[k].to * nodes.length + drawn[k].from);
    }

    return {
      centre: centre,
      hops: hops,
      nodes: nodes,
      edges: drawn,
      list: list,
      total: list.length,
      omitted: list.length - shown.length,
      work: work,
    };
  }

  // The drawn part of an edge between nodes centred at (x1, y1) and
  // (x2, y2): trimmed by each node's reach (r1, r2) so the arrow tip stops at
  // the target's edge -- untrimmed when the two reaches overlap -- and moved
  // by `shift` along the edge's normal, so the two lines of an A -> B, B -> A
  // pair sit 2 x shift apart (spec §4.3: ± 3). null when the ends coincide or the result overflows.
  function segment(x1, y1, r1, x2, y2, r2, shift) {
    var args = [x1, y1, r1, x2, y2, r2, shift];
    for (var k = 0; k < args.length; k++) {
      if (!Number.isFinite(args[k])) { throw new TypeError("okf-local: segment needs finite numbers"); }
    }
    if (r1 < 0 || r2 < 0) { throw new TypeError("okf-local: segment needs reaches of 0 or more"); }
    var dx = x2 - x1;
    var dy = y2 - y1;
    var length = Math.sqrt(dx * dx + dy * dy);
    if (!(length > 0) || !Number.isFinite(length)) { return null; }
    var ux = dx / length;
    var uy = dy / length;
    var nx = -uy * shift;
    var ny = ux * shift;
    var from = length > r1 + r2 ? r1 : 0;
    var to = length > r1 + r2 ? r2 : 0;
    var out = {
      x1: round(x1 + ux * from + nx),
      y1: round(y1 + uy * from + ny),
      x2: round(x2 - ux * to + nx),
      y2: round(y2 - uy * to + ny),
    };
    // Finite input can still overflow (a huge shift): no segment, not Infinity.
    return Number.isFinite(out.x1) && Number.isFinite(out.y1) && Number.isFinite(out.x2) && Number.isFinite(out.y2) ? out : null;
  }

  // The last segment of an id: a node's label (spec X7).
  function lastSegment(id) {
    var text = String(id);
    var slash = text.lastIndexOf("/");
    return slash === -1 ? text : text.slice(slash + 1);
  }

  // --- node labels (spec X7, review D1/D2) ----------------------------------
  // A label is the last segment of its node's id in Space Mono 10 (the centre's
  // 10.5 bold). Its width is ESTIMATED from the text length, never measured (this
  // module is pure): 6.2 per character, 6.5 for the centre's (Space Mono's advance
  // is 0.612 em; the constants round up so the estimate is never short). A label
  // is 8 above its baseline and 3 below it. The default place is the mockup's:
  // centred, baseline 14 under the shape. Three rules keep it readable, in this
  // order, all deterministic (node order, no clock, no randomness):
  //  1. a label stays 4 inside the view: its centre is clamped, and a text over
  //     24 characters is cut to 23 plus an ellipsis (the node's <title> and the
  //     list carry the whole id);
  //  2. a label that would cover another label, or a shape, takes the first free
  //     place of a fixed, bounded search: for each of 21 rows (the default,
  //     then 12 further under and above, alternately), centred, then starting
  //     or ending at its node's x;
  //  3. none free: first the places that avoid labels only, then the default
  //     (clamped) -- the cap's 39 nodes cannot all be readable (A11).
  // At most 40 labels x 126 places x 80 boxes, twice: bounded whatever the index.
  var LABEL = Object.freeze({
    margin: 4, char: 6.2, centreChar: 6.5, ascent: 8, descent: 3, under: 14, above: 4, step: 12, rows: 20, maxChars: 24,
  });
  var ELLIPSIS = String.fromCharCode(0x2026);

  function cut(text) {
    var s = String(text);
    return s.length > LABEL.maxChars ? s.slice(0, LABEL.maxChars - 1) + ELLIPSIS : s;
  }

  function overlaps(a, b) { return a.x1 < b.x2 && b.x1 < a.x2 && a.y1 < b.y2 && b.y1 < a.y2; }

  // nodes: build()'s nodes (the centre first); texts: each node's label text;
  // halves: each node's half-extent (the centre's counts its selection square).
  // Returns, per node, { text, x, y, anchor, truncated, box }.
  function labels(nodes, texts, halves) {
    if (!Array.isArray(nodes) || !Array.isArray(texts) || !Array.isArray(halves) || texts.length !== nodes.length || halves.length !== nodes.length) {
      throw new TypeError("okf-local: labels needs one text and one half-extent per node");
    }
    var w = VIEW.width;
    var h = VIEW.height;
    var m = LABEL.margin;
    var shapes = [];
    var k;
    for (k = 0; k < nodes.length; k++) {
      if (!Number.isFinite(nodes[k].x) || !Number.isFinite(nodes[k].y) || !Number.isFinite(halves[k]) || halves[k] < 0) {
        throw new TypeError("okf-local: labels needs finite positions and half-extents");
      }
      shapes.push({ x1: nodes[k].x - halves[k] - 1, y1: nodes[k].y - halves[k] - 1, x2: nodes[k].x + halves[k] + 1, y2: nodes[k].y + halves[k] + 1 });
    }
    var placed = [];
    var out = [];

    function place(j, width, baseline, mode) {
      var x1 = mode === 0 ? nodes[j].x - width / 2 : mode === 1 ? nodes[j].x : nodes[j].x - width;
      x1 = Math.min(Math.max(x1, m), w - m - width);
      var box = { x1: x1, y1: baseline - LABEL.ascent, x2: x1 + width, y2: baseline + LABEL.descent };
      return { box: box, baseline: baseline, mode: mode };
    }

    // Tries the places of node j against `blocking`; the first that is inside
    // the view and free, or null.
    function search(j, width, avoidShapes) {
      var below = nodes[j].y + halves[j] + LABEL.under;
      var above = nodes[j].y - halves[j] - LABEL.above;
      for (var row = 0; row <= LABEL.rows; row++) {
        var baselines = [below + row * LABEL.step, above - row * LABEL.step];
        for (var mode = 0; mode < 3; mode++) {
          for (var side = 0; side < 2; side++) {
            // Within a row: centred below, centred above, starting below and above, ending below and above.
            var candidate = place(j, width, baselines[side], mode);
            var b = candidate.box;
            if (b.y1 < m || b.y2 > h - m) { continue; }
            var free = true;
            for (var p = 0; p < placed.length && free; p++) { free = !overlaps(b, placed[p]); }
            for (var s = 0; avoidShapes && s < shapes.length && free; s++) { free = !overlaps(b, shapes[s]); }
            if (free) { return candidate; }
          }
        }
      }
      return null;
    }

    for (var j = 0; j < nodes.length; j++) {
      var text = cut(texts[j]);
      var width = text.length * (j === 0 ? LABEL.centreChar : LABEL.char);
      var chosen = search(j, width, true) || search(j, width, false);
      if (!chosen) {
        var base = Math.min(Math.max(nodes[j].y + halves[j] + LABEL.under, m + LABEL.ascent), h - m - LABEL.descent);
        chosen = place(j, width, base, 0);
      }
      var box = chosen.box;
      placed.push({ x1: box.x1 - 0.5, y1: box.y1 - 0.5, x2: box.x2 + 0.5, y2: box.y2 + 0.5 });
      out.push({
        text: text,
        x: round(chosen.mode === 0 ? (box.x1 + box.x2) / 2 : chosen.mode === 1 ? box.x1 : box.x2),
        y: round(chosen.baseline),
        anchor: chosen.mode === 0 ? "middle" : chosen.mode === 1 ? "start" : "end",
        truncated: text !== String(texts[j]),
        box: { x1: round(box.x1), y1: round(box.y1), x2: round(box.x2), y2: round(box.y2) },
      });
    }
    return out;
  }

  window.OkfLocal = Object.freeze({
    CAP: CAP, VIEW: VIEW, LABEL: LABEL, build: build, segment: segment, lastSegment: lastSegment, labels: labels,
  });

  // --- page part: a concept page only (spec §4.1, §12.4) -------------------
  var site = window.OkfSite;
  var shapes = window.OkfShapes;
  var html = document.documentElement;
  if (!site || !shapes || html.getAttribute("data-okf-view") !== "page") { return; }
  var context = document.getElementById("okf-context");
  var index = site.readIndex(window);
  if (!context || !index) { return; }
  var currentId = html.getAttribute("data-okf-concept");
  var centre = -1;
  for (var i = 0; i < index.concepts.length; i++) {
    // A damaged entry (null, not an object) is skipped, as okf-page.js does.
    var entry = index.concepts[i];
    if (entry !== null && typeof entry === "object" && entry.id === currentId) { centre = i; break; }
  }
  if (centre < 0) { return; }
  // The first ring is computed on load, by scanning the edges (no adjacency
  // structure is kept); the second hop when "2 hops" is pressed (spec §3.6).
  // No neighbour, no section (§12.4).
  var first = build(index, centre, 1);
  if (first.total === 0) { return; }

  var SVG_NS = "http://www.w3.org/2000/svg";
  var DOT = " " + String.fromCharCode(0xb7) + " "; // " · ", built at run time
  var C = index.concepts.length;
  var root = site.rootOf(document);
  var nodeKeys = new Map(); // drawn <g> -> node key, replaced with each drawing

  function el(tag, className, text) { return site.element(document, tag, className, text); }

  function svg(name, className) {
    var node = document.createElementNS(SVG_NS, name);
    if (className) { node.setAttribute("class", className); }
    return node;
  }

  // Every number is checked finite before it becomes an attribute (§4.5).
  function num(value) {
    if (!Number.isFinite(value)) { throw new TypeError("okf-local: non-finite coordinate"); }
    return String(round(value));
  }

  // A damaged entry reads as an empty id: it draws a blank, never throws.
  function idOf(key) {
    var entry = key < C ? index.concepts[key] : index.ghosts[key - C];
    return entry !== null && typeof entry === "object" && typeof entry.id === "string" ? entry.id : "";
  }
  function kindOf(key) { return key < C ? shapes.kindOf(index, key) : "ghost"; }
  function hopsText(hops) { return hops === 1 ? "1 hop" : "2 hops"; }
  function neighboursText(n) { return n + (n === 1 ? " neighbour" : " neighbours"); }

  // The palette's hook: a host may veto the navigation with preventDefault().
  function navigate(href) {
    var event = new CustomEvent("okf:navigate", { cancelable: true, detail: { href: href } });
    if (document.dispatchEvent(event)) { window.location.assign(href); }
  }

  // The mockup's arrow (spec X7: 7, to the target), filled by CSS (--edge).
  function marker(id, className) {
    var m = svg("marker");
    m.setAttribute("id", id);
    m.setAttribute("viewBox", "0 0 10 10");
    m.setAttribute("refX", "9");
    m.setAttribute("refY", "5");
    m.setAttribute("markerWidth", "7");
    m.setAttribute("markerHeight", "7");
    m.setAttribute("orient", "auto");
    var head = svg("path", className);
    head.setAttribute("d", "M0 0 L10 5 L0 10 Z");
    m.appendChild(head);
    return m;
  }

  function drawing(result) {
    var picture = svg("svg");
    picture.setAttribute("viewBox", "0 0 " + VIEW.width + " " + VIEW.height);
    picture.setAttribute("width", "100%");
    picture.setAttribute("height", String(VIEW.height));
    picture.setAttribute("role", "img");
    picture.setAttribute("focusable", "false");
    picture.setAttribute("aria-label", "Local graph of " + idOf(centre) + ", " + hopsText(result.hops) + ": "
      + (result.nodes.length - 1) + " of " + neighboursText(result.total) + " drawn; the list below names them all");
    var defs = svg("defs");
    defs.appendChild(marker("okf-local-arrow", "okf-local-arrowhead"));
    defs.appendChild(marker("okf-local-arrow-in", "okf-local-arrowhead okf-local-arrowhead-in"));
    picture.appendChild(defs);

    var sizes = [];
    var reach = [];
    for (var n = 0; n < result.nodes.length; n++) {
      var s = shapes.SIZES[n === 0 ? "localCenter" : "local"][kindOf(result.nodes[n].key)];
      sizes.push(s);
      // The arrow stops just outside the target; the centre's selection
      // square (side + 12, stroke 2) counts as part of it.
      reach.push(s.size / 2 + (n === 0 ? 7 : 1));
    }

    var lines = svg("g", "okf-local-edges");
    for (var k = 0; k < result.edges.length; k++) {
      var edge = result.edges[k];
      var a = result.nodes[edge.from];
      var b = result.nodes[edge.to];
      var seg = segment(a.x, a.y, reach[edge.from], b.x, b.y, reach[edge.to], edge.paired ? 3 : 0);
      if (!seg) { continue; }
      var line = svg("line", edge.dashed ? "okf-local-edge okf-local-edge-in" : "okf-local-edge");
      line.setAttribute("x1", num(seg.x1));
      line.setAttribute("y1", num(seg.y1));
      line.setAttribute("x2", num(seg.x2));
      line.setAttribute("y2", num(seg.y2));
      line.setAttribute("stroke-width", "1.4");
      if (edge.dashed) { line.setAttribute("stroke-dasharray", "4 3"); }
      line.setAttribute("marker-end", edge.dashed ? "url(#okf-local-arrow-in)" : "url(#okf-local-arrow)");
      lines.appendChild(line);
    }
    picture.appendChild(lines);

    // Where each label goes: OkfLocal.labels keeps them inside the view and
    // apart from each other (the node <title> keeps a cut id whole).
    var texts = [];
    var halves = [];
    for (var t = 0; t < result.nodes.length; t++) {
      texts.push(lastSegment(idOf(result.nodes[t].key)));
      halves.push(sizes[t].size / 2 + (t === 0 ? 6 : 0));
    }
    var places = labels(result.nodes, texts, halves);

    nodeKeys = new Map();
    for (var j = 0; j < result.nodes.length; j++) {
      var node = result.nodes[j];
      var ghost = node.key >= C;
      var size = sizes[j];
      var g = shapes.node(kindOf(node.key), node.x, node.y, size.size, size);
      g.classList.add(ghost ? "okf-local-ghost" : "okf-local-node");
      if (j === 0) { g.classList.add("okf-selected"); }
      var title = svg("title");
      title.textContent = ghost ? "absent: " + idOf(node.key) : idOf(node.key);
      g.insertBefore(title, g.firstChild);
      var label = svg("text", "okf-local-label" + (j === 0 ? " okf-local-label-center" : "") + (ghost ? " okf-local-label-ghost" : ""));
      label.setAttribute("x", num(places[j].x));
      label.setAttribute("y", num(places[j].y));
      label.setAttribute("text-anchor", places[j].anchor);
      label.textContent = places[j].text;
      g.appendChild(label);
      nodeKeys.set(g, node.key);
      picture.appendChild(g);
    }

    // Mouse only: the drawing is an image with no tab stop; the keyboard
    // path is the list and Referenced by (spec §8).
    picture.addEventListener("click", function (e) {
      for (var t = e.target; t && t !== picture; t = t.parentNode) {
        if (nodeKeys.has(t)) {
          var key = nodeKeys.get(t);
          var entry = key < C ? index.concepts[key] : null;
          if (entry !== null && typeof entry === "object" && typeof entry.path === "string") { navigate(site.resolve(root, entry.path)); }
          return; // a ghost is never navigable
        }
      }
    });
    return picture;
  }

  function hopButton(hops) {
    var button = el("button", "", hopsText(hops));
    button.type = "button";
    button.id = "okf-local-hops-" + hops;
    button.setAttribute("aria-pressed", hops === 1 ? "true" : "false");
    button.addEventListener("click", function () { render(build(index, centre, hops)); });
    return button;
  }

  var section = el("section", "okf-local");
  section.id = "okf-local-graph";
  section.setAttribute("aria-labelledby", "okf-local-title");
  var head = el("div", "okf-local-head");
  var heading = el("h2", "okf-section-title", "Neighbourhood");
  heading.id = "okf-local-title";
  var group = el("div", "okf-hops");
  group.setAttribute("role", "group");
  group.setAttribute("aria-label", "Neighbourhood depth");
  var hopButtons = [hopButton(1), hopButton(2)];
  group.appendChild(hopButtons[0]);
  group.appendChild(hopButtons[1]);
  head.appendChild(heading);
  head.appendChild(group);
  var canvas = el("div", "okf-local-canvas");
  var foot = el("p", "okf-local-foot");
  foot.appendChild(el("span", "", "solid = links to" + DOT + "dashed = referenced by"));
  // "Open in graph" repeats the header's own link, fragment included: the
  // header is the only source of the graph page's name (spec §12.3).
  var tools = document.getElementById("okf-tools");
  var globalLink = tools ? tools.querySelector("#okf-global-graph") : null;
  var graphHref = globalLink ? globalLink.getAttribute("href") : null;
  if (graphHref) {
    var openLink = el("a", "", "Open in graph");
    openLink.id = "okf-local-open";
    openLink.setAttribute("href", graphHref);
    foot.appendChild(openLink);
  }
  section.appendChild(head);
  section.appendChild(canvas);
  section.appendChild(foot);
  // The equivalent list (spec §6, X9): every neighbour, drawn or not, with
  // its relation to this concept -- the keyboard path through the
  // neighbourhood (§8), since the drawing has no tab stop.
  var details = el("details", "okf-local-list");
  details.id = "okf-local-list";
  var summary = el("summary", "");
  var rows = el("ul", "");
  // The shared list reset (list-style: none) makes Safari/VoiceOver drop the
  // implicit list role; the explicit one keeps "list, N items" announced.
  rows.setAttribute("role", "list");
  details.appendChild(summary);
  details.appendChild(rows);
  section.appendChild(details);

  // The relation a row names, from this concept's point of view (X8's
  // words); a second-hop row names the first direct neighbour, in index
  // order, it is reached through. A damaged `via` (no readable id) names no
  // one rather than leaving a dangling "via".
  function relationText(entry) {
    if (entry.dist === 2) {
      var through = idOf(entry.via);
      return through === "" ? "2 hops" : "2 hops via " + through;
    }
    if (entry.rel === 3) { return "links to" + DOT + "referenced by"; }
    return entry.rel === 1 ? "links to" : "referenced by";
  }

  // A damaged entry reads as no type, never as the text "undefined".
  function typeOf(concept) {
    return concept !== null && typeof concept === "object" && typeof concept.type === "string" ? concept.type : "";
  }

  // One row of the list. Its link goes through the resolver, like the
  // drawing's click, but does not dispatch okf:navigate: like the explorer
  // and Referenced by, a plain link is left to the browser; only the palette
  // and the drawing (which has no href to follow) dispatch the event.
  function row(entry) {
    var item = el("li", "");
    var line;
    var concept = entry.key < C ? index.concepts[entry.key] : null;
    if (concept !== null && typeof concept === "object" && typeof concept.path === "string") {
      line = el("a", "okf-row");
      line.setAttribute("href", site.resolve(root, concept.path));
      line.setAttribute("title", typeof concept.title === "string" ? concept.title : "");
      line.appendChild(shapes.icon(kindOf(entry.key), "icon", shapes.typeLabel(typeOf(concept))));
      line.appendChild(el("span", "okf-local-id", idOf(entry.key)));
    } else {
      // A ghost is never navigable (spec A10): text, no link. A damaged
      // concept entry (no path) is listed the same way, as text.
      line = el("span", entry.key < C ? "okf-row" : "okf-row okf-local-absent");
      line.appendChild(shapes.icon(kindOf(entry.key), "icon", entry.key < C ? shapes.typeLabel(typeOf(concept)) : "absent concept"));
      line.appendChild(el("span", "okf-local-id", entry.key < C ? idOf(entry.key) : "absent: " + idOf(entry.key)));
    }
    line.appendChild(el("span", "okf-local-rel", relationText(entry)));
    item.appendChild(line);
    return item;
  }

  function render(result) {
    for (var b = 0; b < hopButtons.length; b++) {
      hopButtons[b].setAttribute("aria-pressed", b + 1 === result.hops ? "true" : "false");
    }
    canvas.textContent = "";
    canvas.appendChild(drawing(result));
    if (result.omitted > 0) {
      // Over the cap (spec A11): the rest is named in the list, which this
      // opens.
      var more = el("button", "okf-local-omitted", "+" + result.omitted + " omitted");
      more.type = "button";
      more.setAttribute("aria-controls", "okf-local-list");
      more.addEventListener("click", function () {
        details.open = true;
        summary.focus();
      });
      canvas.appendChild(more);
    }
    summary.textContent = "List" + DOT + neighboursText(result.total);
    rows.textContent = "";
    for (var k = 0; k < result.list.length; k++) { rows.appendChild(row(result.list[k])); }
  }

  render(first);
  var toc = document.getElementById("okf-toc");
  context.insertBefore(section, toc && toc.parentNode === context ? toc.nextSibling : context.firstChild);
  context.hidden = false;
})();
