// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The local graph of a concept page (spec §4.3, §12.4): the concept and its
// neighbours at one or two hops, in rings. This file holds window.OkfLocal,
// pure (no DOM, no clock, no randomness): from the site index alone it
// computes the drawn nodes and their ring positions, every index edge between
// them and the full neighbour list. tools/viewer-security-check calls it
// directly (cases/p2.js, cases/p2-modal.js). The page part draws the result
// into #okf-context on a concept page, and again, larger, in the modal dialog
// its "Enlarge the neighbourhood" button opens (spec X12).
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
  // cap: a shape (up to 9 from its centre) and its label (baseline 14 under
  // a shape, 3 of descender) stay inside the 298 x 248 view; no shape touches
  // the centre's selection square (up to +- 15) or its label, centred under
  // it at baseline cy + 26.5 to cy + 29 (a band 120 wide, y 132 to 151); and
  // each ring sits clear of the other. The inner ring of 2 hops is the large
  // one it is because any ellipse a node can reach under that label would
  // collide with it, whatever the number of nodes. The radii were set for the
  // earlier, larger shapes (13, +- 20, cy + 34) and kept when the shapes
  // shrank to 0.7 of them: smaller shapes only widen every clearance.
  var ONE_RING = Object.freeze([Object.freeze({ rx: 127, ry: 95 })]);
  var TWO_RINGS = Object.freeze([Object.freeze({ rx: 106, ry: 72 }), Object.freeze({ rx: 134, ry: 98 })]);
  // The enlarged neighbourhood (spec X12): the same layout in a larger view,
  // `viewport` { width, height } given to build() and labels(), from the
  // panel's 298 x 248 up to `max` on each side. The centre keeps its place
  // relative to the middle (6 above it), the ring radii grow with the view on
  // each axis (rx with the width, ry with the height), so every clearance the
  // panel's sweep pins grows with them; shapes and fonts keep their sizes. In a
  // view at least `wideFrom` wide a label is cut at `wideChars` code points
  // instead of LABEL.maxChars (cases/p2-modal.js sweeps it).
  var LARGE = Object.freeze({ max: 4096, wideFrom: 560, wideChars: 32 });

  // The view a viewport asks for: VIEW itself when there is none, so the
  // panel's layout is computed exactly as before (cases/p2-modal.js pins it).
  function viewOf(viewport) {
    if (viewport === undefined) { return VIEW; }
    if (viewport === null || typeof viewport !== "object") { throw new TypeError("okf-local: viewport is not an object"); }
    var w = viewport.width;
    var h = viewport.height;
    if (typeof w !== "number" || typeof h !== "number" || !(w >= VIEW.width && w <= LARGE.max) || !(h >= VIEW.height && h <= LARGE.max)) {
      throw new TypeError("okf-local: viewport is not a finite size between the panel's and " + LARGE.max);
    }
    return { width: w, height: h, cx: w / 2, cy: h / 2 - (VIEW.height / 2 - VIEW.cy) };
  }

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
  // (spec §7: counted, never timed). `viewport`, optional: the enlarged
  // view (LARGE above); without it, the panel's.
  function build(index, centre, hops, viewport) {
    var view = viewOf(viewport);
    var sx = view.width / VIEW.width;
    var sy = view.height / VIEW.height;
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
    var nodes = [{ key: centre, dist: 0, x: view.cx, y: view.cy }];
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
          // sx and sy are exactly 1 in the panel's view: its positions are unchanged.
          x: round(view.cx + radii[r].rx * sx * Math.cos(angle)),
          y: round(view.cy + radii[r].ry * sy * Math.sin(angle)),
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
  //  1. a label stays 4 inside the view: its box is clamped, and a text of more
  //     than 20 code points is cut to 19 plus an ellipsis (never inside a
  //     surrogate pair; the node's <title> and the list carry the whole id) --
  //     in a view LARGE.wideFrom wide or more, 32 and 31 (LARGE.wideChars);
  //  2. a label that would cover another label, or a shape, takes the first free
  //     place of a fixed, bounded search: for each of 21 rows (the default,
  //     then 12 further under and above, alternately), centred, then starting
  //     or ending at its node's x, then beside the shape (to its right, then
  //     to its left, 3 clear of it, rows counted from the node's own height), then
  //     against either margin; failing that, resting just under or over an obstacle,
  //     the nearest first;
  //  3. none free: first the places that avoid labels only, then the default
  //     (clamped) -- the cap's 39 nodes cannot all be readable (A11).
  // Boxes keep 0.5 clear of each other and 1 of a shape. At most 40 labels x (294
  // row places + 7 x 158 places resting on an obstacle) x 80 boxes, twice: bounded
  // whatever the index.
  var LABEL = Object.freeze({
    margin: 4, char: 6.2, centreChar: 6.5, ascent: 8, descent: 3, under: 14, above: 4, beside: 3, level: 4, step: 12, rows: 20, maxChars: 20,
  });
  var ELLIPSIS = String.fromCharCode(0x2026);

  // By code points, not UTF-16 units: a cut never leaves half a surrogate pair.
  // Reads at most max + 1 code points, whatever the length of the text.
  function cut(text, max) {
    var s = String(text);
    var units = 0;
    var points = 0;
    var keep = 0; // the length, in units, of the first maxChars - 1 code points
    while (units < s.length && points <= max) {
      if (points === max - 1) { keep = units; }
      var high = s.charCodeAt(units);
      var pair = high >= 0xd800 && high <= 0xdbff && units + 1 < s.length && (s.charCodeAt(units + 1) & 0xfc00) === 0xdc00;
      units += pair ? 2 : 1;
      points++;
    }
    return points > max ? s.slice(0, keep) + ELLIPSIS : s;
  }

  function overlaps(a, b) { return a.x1 < b.x2 && b.x1 < a.x2 && a.y1 < b.y2 && b.y1 < a.y2; }

  // nodes: build()'s nodes (the centre first); texts: each node's label text;
  // halves: each node's half-extent (the centre's counts its selection square).
  // viewport: optional, build()'s (the enlarged view). Returns, per node,
  // { text, x, y, anchor, truncated, box }.
  function labels(nodes, texts, halves, viewport) {
    if (!Array.isArray(nodes) || !Array.isArray(texts) || !Array.isArray(halves) || texts.length !== nodes.length || halves.length !== nodes.length) {
      throw new TypeError("okf-local: labels needs one text and one half-extent per node");
    }
    var view = viewOf(viewport);
    var w = view.width;
    var h = view.height;
    var maxChars = w >= LARGE.wideFrom ? LARGE.wideChars : LABEL.maxChars;
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

    // Modes: 0 centred on the node, 1 starting at its x, 2 ending at its x,
    // 3 starting right of the shape, 4 ending left of it, 5 and 6 against the left
    // and the right margin.
    function place(j, width, baseline, mode) {
      var x1 = mode === 0 ? nodes[j].x - width / 2
        : mode === 1 ? nodes[j].x
        : mode === 2 ? nodes[j].x - width
        : mode === 3 ? nodes[j].x + halves[j] + LABEL.beside
        : mode === 4 ? nodes[j].x - halves[j] - LABEL.beside - width
        : mode === 5 ? m
        : w - m - width;
      x1 = Math.min(Math.max(x1, m), w - m - width);
      var box = { x1: x1, y1: baseline - LABEL.ascent, x2: x1 + width, y2: baseline + LABEL.descent };
      return { box: box, baseline: baseline, mode: mode };
    }

    // The place (j, baseline, mode) if it is inside the view and free, else null.
    function fit(j, width, baseline, mode, avoidShapes) {
      var candidate = place(j, width, baseline, mode);
      var b = candidate.box;
      if (b.y1 < m || b.y2 > h - m) { return null; }
      for (var p = 0; p < placed.length; p++) { if (overlaps(b, placed[p])) { return null; } }
      for (var s = 0; avoidShapes && s < shapes.length; s++) { if (overlaps(b, shapes[s])) { return null; } }
      return candidate;
    }

    // The first free place for node j, or null. Stage 1: the rows around the
    // node. Stage 2, for a crowded drawing whose free bands fall between rows: the
    // baselines that rest against an obstacle (just under or over a label or,
    // when shapes count, a shape), nearest to the default first; every mode each.
    function search(j, width, avoidShapes) {
      var below = nodes[j].y + halves[j] + LABEL.under;
      var above = nodes[j].y - halves[j] - LABEL.above;
      var row;
      var mode;
      var side;
      var found;
      for (row = 0; row <= LABEL.rows; row++) {
        var baselines = [below + row * LABEL.step, above - row * LABEL.step];
        var level = [nodes[j].y + LABEL.level + row * LABEL.step, nodes[j].y + LABEL.level - row * LABEL.step];
        for (mode = 0; mode < 7; mode++) {
          for (side = 0; side < 2; side++) {
            // Within a row: centred below, centred above, starting below and above, ending below
            // and above, then right of the shape and left of it, level with the node, lower, higher,
            // then against the left and the right margin, under and over the node.
            found = fit(j, width, mode < 3 || mode > 4 ? baselines[side] : level[side], mode, avoidShapes);
            if (found) { return found; }
          }
        }
      }
      var rests = [];
      var q;
      for (q = 0; q < placed.length; q++) { rests.push(placed[q].y2 + LABEL.ascent, placed[q].y1 - LABEL.descent); }
      for (q = 0; avoidShapes && q < shapes.length; q++) { rests.push(shapes[q].y2 + LABEL.ascent, shapes[q].y1 - LABEL.descent); }
      rests.sort(function (a, b) { return Math.abs(a - below) - Math.abs(b - below) || a - b; });
      for (q = 0; q < rests.length; q++) {
        for (mode = 0; mode < 7; mode++) {
          found = fit(j, width, rests[q], mode, avoidShapes);
          if (found) { return found; }
        }
      }
      return null;
    }

    for (var j = 0; j < nodes.length; j++) {
      var text = cut(texts[j], maxChars);
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
        x: round(chosen.mode === 0 ? (box.x1 + box.x2) / 2 : chosen.mode === 1 || chosen.mode === 3 || chosen.mode === 5 ? box.x1 : box.x2),
        y: round(chosen.baseline),
        anchor: chosen.mode === 0 ? "middle" : chosen.mode === 1 || chosen.mode === 3 || chosen.mode === 5 ? "start" : "end",
        truncated: text !== String(texts[j]),
        box: { x1: round(box.x1), y1: round(box.y1), x2: round(box.x2), y2: round(box.y2) },
      });
    }
    return out;
  }

  window.OkfLocal = Object.freeze({
    CAP: CAP, VIEW: VIEW, LABEL: LABEL, LARGE: LARGE, build: build, segment: segment, lastSegment: lastSegment, labels: labels,
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
  // The enlarge glyph (spec X12): four corners pointing out, a constant of
  // this module in the fixed vocabulary of §12.2 (one path, M and L only).
  var ENLARGE = "M2 6 L2 2 L6 2 M10 2 L14 2 L14 6 M14 10 L14 14 L10 14 M6 14 L2 14 L2 10";

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

  // The drawing of `result`. `large`: absent for the panel (298 x 248, its
  // arrow markers okf-local-arrow*); for the modal, { viewport } -- the view
  // build() laid the result out in, which is also the svg's own size and
  // viewBox -- with markers okf-local-modal-arrow*, so the two drawings never
  // share an id.
  function drawing(result, large) {
    var width = large ? large.viewport.width : VIEW.width;
    var height = large ? large.viewport.height : VIEW.height;
    var arrow = large ? "okf-local-modal-arrow" : "okf-local-arrow";
    var picture = svg("svg");
    picture.setAttribute("viewBox", "0 0 " + num(width) + " " + num(height));
    picture.setAttribute("width", large ? num(width) : "100%");
    picture.setAttribute("height", num(height));
    picture.setAttribute("role", "img");
    picture.setAttribute("focusable", "false");
    picture.setAttribute("aria-label", "Local graph of " + idOf(centre) + ", " + hopsText(result.hops) + ": "
      + (result.nodes.length - 1) + " of " + neighboursText(result.total) + " drawn; the list " + (large ? "beside" : "below") + " names them all");
    var defs = svg("defs");
    defs.appendChild(marker(arrow, "okf-local-arrowhead"));
    defs.appendChild(marker(arrow + "-in", "okf-local-arrowhead okf-local-arrowhead-in"));
    picture.appendChild(defs);

    var sizes = [];
    var reach = [];
    for (var n = 0; n < result.nodes.length; n++) {
      var s = shapes.SIZES[n === 0 ? "localCenter" : "local"][kindOf(result.nodes[n].key)];
      sizes.push(s);
      // The arrow stops just outside the target; the centre's selection
      // square (side + 8, stroke 2) counts as part of it.
      reach.push(s.size / 2 + (n === 0 ? 5 : 1));
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
      line.setAttribute("marker-end", edge.dashed ? "url(#" + arrow + "-in)" : "url(#" + arrow + ")");
      lines.appendChild(line);
    }
    picture.appendChild(lines);

    // Where each label goes: OkfLocal.labels keeps them inside the view and
    // apart from each other (the node <title> keeps a cut id whole).
    var texts = [];
    var halves = [];
    for (var t = 0; t < result.nodes.length; t++) {
      texts.push(lastSegment(idOf(result.nodes[t].key)));
      halves.push(sizes[t].size / 2 + (t === 0 ? 4 : 0));
    }
    var places = labels(result.nodes, texts, halves, large ? large.viewport : undefined);

    // Each drawing keeps its own map, so the panel's and the modal's clicks
    // never read each other's nodes.
    var nodeKeys = new Map(); // drawn <g> -> node key
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
      for (var target = e.target; target && target !== picture; target = target.parentNode) {
        if (nodeKeys.has(target)) {
          var key = nodeKeys.get(target);
          var entry = key < C ? index.concepts[key] : null;
          if (entry !== null && typeof entry === "object" && typeof entry.path === "string") { navigate(site.resolve(root, entry.path)); }
          return; // a ghost is never navigable
        }
      }
    });
    return picture;
  }

  // The depth shown, shared by the panel and the modal: pressing a depth in
  // either redraws both (spec X12). Not remembered across pages (X5).
  var current = first;

  function setHops(hops) {
    current = build(index, centre, hops);
    render();
  }

  function hopGroup(prefix) {
    var group = el("div", "okf-hops");
    group.setAttribute("role", "group");
    group.setAttribute("aria-label", "Neighbourhood depth");
    var buttons = [];
    for (var hops = 1; hops <= 2; hops++) {
      var button = el("button", "", hopsText(hops));
      button.type = "button";
      button.id = prefix + hops;
      button.setAttribute("aria-pressed", hops === 1 ? "true" : "false");
      button.addEventListener("click", setHops.bind(null, hops));
      group.appendChild(button);
      buttons.push(button);
    }
    return { group: group, buttons: buttons };
  }

  function pressHops(buttons, hops) {
    for (var b = 0; b < buttons.length; b++) {
      buttons[b].setAttribute("aria-pressed", b + 1 === hops ? "true" : "false");
    }
  }

  function enlargeIcon() {
    var glyph = svg("svg", "okf-local-enlarge-icon");
    glyph.setAttribute("width", "16");
    glyph.setAttribute("height", "16");
    glyph.setAttribute("viewBox", "0 0 16 16");
    glyph.setAttribute("aria-hidden", "true");
    glyph.setAttribute("focusable", "false");
    var path = svg("path");
    path.setAttribute("d", ENLARGE);
    path.setAttribute("stroke-width", "1.5");
    glyph.appendChild(path);
    return glyph;
  }

  // "Open in graph" repeats the header's own link, fragment included: the
  // header is the only source of the graph page's name (spec §12.3).
  var tools = document.getElementById("okf-tools");
  var globalLink = tools ? tools.querySelector("#okf-global-graph") : null;
  var graphHref = globalLink ? globalLink.getAttribute("href") : null;

  // The foot of X8: the legend of the edges, then "Open in graph" if the
  // header has a graph link.
  function foot(className, linkId) {
    var p = el("p", className);
    p.appendChild(el("span", "", "solid = links to" + DOT + "dashed = referenced by"));
    if (graphHref) {
      var openLink = el("a", "", "Open in graph");
      openLink.id = linkId;
      openLink.setAttribute("href", graphHref);
      p.appendChild(openLink);
    }
    return p;
  }

  var section = el("section", "okf-local");
  section.id = "okf-local-graph";
  section.setAttribute("aria-labelledby", "okf-local-title");
  var head = el("div", "okf-local-head");
  var heading = el("h2", "okf-section-title", "Neighbourhood");
  heading.id = "okf-local-title";
  var panelHops = hopGroup("okf-local-hops-");
  // X12: the opener of the enlarged view, an icon button after the depth.
  var enlarge = el("button", "okf-local-enlarge");
  enlarge.type = "button";
  enlarge.id = "okf-local-enlarge";
  enlarge.setAttribute("aria-label", "Enlarge the neighbourhood");
  enlarge.setAttribute("title", "Enlarge the neighbourhood");
  enlarge.setAttribute("aria-haspopup", "dialog");
  enlarge.setAttribute("aria-expanded", "false");
  enlarge.appendChild(enlargeIcon());
  head.appendChild(heading);
  head.appendChild(panelHops.group);
  head.appendChild(enlarge);
  var canvas = el("div", "okf-local-canvas");
  section.appendChild(head);
  section.appendChild(canvas);
  section.appendChild(foot("okf-local-foot", "okf-local-open"));
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

  function fillRows(list, result) {
    list.textContent = "";
    for (var k = 0; k < result.list.length; k++) { list.appendChild(row(result.list[k])); }
  }

  function renderPanel(result) {
    pressHops(panelHops.buttons, result.hops);
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
    fillRows(rows, result);
  }

  // --- the enlarged neighbourhood: a modal dialog (spec X12) ---------------
  // Built on first open, appended to <body> as #okf-local-modal (the sanitizer
  // strips id from body content, so no body element can take that id), then
  // shown and hidden. While it is open: the rest of <body> is inert and
  // aria-hidden, the page does not scroll (data-okf-modal-open on <html>),
  // Tab and Shift+Tab cycle inside it, Escape or a click on the backdrop
  // closes it and focus goes back to the opener. Its own code, not the
  // palette's: the palette's trap knows its two stops only.
  var modal = null;

  function conceptTitle() {
    var entry = index.concepts[centre];
    return entry !== null && typeof entry === "object" && typeof entry.title === "string" && entry.title !== "" ? entry.title : idOf(centre);
  }

  function buildModal() {
    var backdrop = el("div", "okf-local-backdrop");
    backdrop.id = "okf-local-modal";
    backdrop.hidden = true;
    var dialog = el("div", "okf-local-dialog");
    dialog.setAttribute("role", "dialog");
    dialog.setAttribute("aria-modal", "true");
    dialog.setAttribute("aria-labelledby", "okf-local-modal-title");
    // Focusable but not a tab stop: focus lands here on open, and a click on
    // the dialog's own text keeps it inside.
    dialog.tabIndex = -1;
    var top = el("div", "okf-local-modal-head");
    var title = el("h2", "okf-local-modal-title", "Neighbourhood of " + conceptTitle());
    title.id = "okf-local-modal-title";
    var hops = hopGroup("okf-local-modal-hops-");
    var close = el("button", "okf-local-modal-close", "Close");
    close.type = "button";
    top.appendChild(title);
    top.appendChild(hops.group);
    top.appendChild(close);
    var body = el("div", "okf-local-modal-body");
    var area = el("div", "okf-local-modal-canvas");
    var side = el("div", "okf-local-modal-side");
    var count = el("h3", "okf-section-title");
    count.id = "okf-local-modal-count";
    var list = el("ul", "okf-local-modal-rows");
    list.setAttribute("role", "list");
    list.setAttribute("aria-labelledby", "okf-local-modal-count");
    side.appendChild(count);
    side.appendChild(list);
    body.appendChild(area);
    body.appendChild(side);
    dialog.appendChild(top);
    dialog.appendChild(body);
    dialog.appendChild(foot("okf-local-modal-foot", "okf-local-modal-open"));
    backdrop.appendChild(dialog);
    close.addEventListener("click", closeModal);
    backdrop.addEventListener("click", function (e) { if (e.target === backdrop) { closeModal(); } });
    document.body.appendChild(backdrop);
    return { backdrop: backdrop, dialog: dialog, hops: hops.buttons, area: area, count: count, list: list, viewport: null, frame: 0, inerted: [] };
  }

  // The drawing area's size, in the bounds build() accepts: never smaller
  // than the panel's view (a smaller area shows the drawing scaled down by
  // its viewBox), never larger than LARGE.max.
  function measure() {
    var w = Math.floor(modal.area.clientWidth);
    var h = Math.floor(modal.area.clientHeight);
    return {
      width: Math.min(Math.max(Number.isFinite(w) ? w : 0, VIEW.width), LARGE.max),
      height: Math.min(Math.max(Number.isFinite(h) ? h : 0, VIEW.height), LARGE.max),
    };
  }

  // The modal's drawing of the current depth, laid out for `viewport`, by
  // default the area as it is now.
  function drawModal(viewport) {
    viewport = viewport || measure();
    modal.viewport = viewport;
    var result = build(index, centre, current.hops, viewport);
    modal.area.textContent = "";
    modal.area.appendChild(drawing(result, { viewport: viewport }));
    if (result.omitted > 0) {
      // A note, not a button: every neighbour is in the list beside.
      modal.area.appendChild(el("p", "okf-local-modal-omitted", "+" + result.omitted + " omitted"));
    }
  }

  function renderModal() {
    pressHops(modal.hops, current.hops);
    drawModal();
    modal.count.textContent = "List" + DOT + neighboursText(current.total);
    fillRows(modal.list, current);
  }

  function render() {
    renderPanel(current);
    if (modal && !modal.backdrop.hidden) { renderModal(); }
  }

  // A window resize lays the drawing out again for the new area, at most
  // once a frame and only when the area changed; meanwhile the viewBox scales
  // the previous drawing into the area.
  function onResize() {
    if (modal.frame) { return; }
    var later = typeof window.requestAnimationFrame === "function" ? window.requestAnimationFrame.bind(window) : function (fn) { fn(); return 0; };
    modal.frame = later(function () {
      modal.frame = 0;
      if (modal.backdrop.hidden) { return; }
      var size = measure();
      if (size.width !== modal.viewport.width || size.height !== modal.viewport.height) { drawModal(size); }
    }) || 0;
  }

  // The tab stops of the dialog, in document order.
  function stops() {
    var found = modal.dialog.querySelectorAll("button, a[href], [tabindex]");
    var out = [];
    for (var k = 0; k < found.length; k++) {
      var stop = found[k];
      if (stop.disabled || stop.tabIndex < 0) { continue; }
      out.push(stop);
    }
    return out;
  }

  function isCtrlK(e) {
    return e.ctrlKey && !e.altKey && !e.metaKey && (e.key === "k" || e.key === "K" || e.code === "KeyK");
  }

  // Captured on document, before the palette's own listener: while the
  // modal is open, Escape closes it, Tab stays inside it, and the palette's
  // shortcuts ("/" and Ctrl+K) are refused, prevented so the palette (which
  // ignores a prevented key) does not open over it.
  function onKey(e) {
    if (modal.backdrop.hidden || e.isComposing || e.keyCode === 229) { return; }
    if (e.key === "Escape") {
      e.preventDefault();
      closeModal();
    } else if (e.key === "Tab") {
      // Every Tab is taken and moved along the dialog's own stops, rebouncing
      // at either end: left to the browser, a Tab from the last button would
      // skip the list's links where Tab does not reach links (Safari) and
      // leave the dialog.
      e.preventDefault();
      var list = stops();
      if (list.length === 0) {
        modal.dialog.focus();
        return;
      }
      var at = list.indexOf(document.activeElement);
      var next = at === -1 ? (e.shiftKey ? list.length - 1 : 0) : (at + (e.shiftKey ? list.length - 1 : 1)) % list.length;
      list[next].focus();
    } else if ((e.key === "/" && !e.ctrlKey && !e.altKey && !e.metaKey) || isCtrlK(e)) {
      e.preventDefault();
    }
  }

  function openModal() {
    if (modal && !modal.backdrop.hidden) { return; } // already open: nothing twice
    if (!modal) { modal = buildModal(); }
    modal.backdrop.hidden = false;
    // The rest of the page: inert and hidden from assistive technology,
    // remembering what this changed so closing restores exactly that.
    modal.inerted = [];
    for (var child = document.body.firstElementChild; child; child = child.nextElementSibling) {
      if (child === modal.backdrop || child.localName === "script") { continue; }
      var change = { node: child, inert: !child.hasAttribute("inert"), hidden: !child.hasAttribute("aria-hidden") };
      if (change.inert) { child.setAttribute("inert", ""); }
      if (change.hidden) { child.setAttribute("aria-hidden", "true"); }
      modal.inerted.push(change);
    }
    html.setAttribute("data-okf-modal-open", "");
    enlarge.setAttribute("aria-expanded", "true");
    renderModal();
    document.addEventListener("keydown", onKey, true);
    window.addEventListener("resize", onResize);
    modal.dialog.focus();
  }

  function closeModal() {
    if (!modal || modal.backdrop.hidden) { return; }
    modal.backdrop.hidden = true;
    document.removeEventListener("keydown", onKey, true);
    window.removeEventListener("resize", onResize);
    if (modal.frame && typeof window.cancelAnimationFrame === "function") { window.cancelAnimationFrame(modal.frame); }
    modal.frame = 0;
    for (var k = 0; k < modal.inerted.length; k++) {
      var change = modal.inerted[k];
      if (change.inert) { change.node.removeAttribute("inert"); }
      if (change.hidden) { change.node.removeAttribute("aria-hidden"); }
    }
    modal.inerted = [];
    // The drawing and the list are rebuilt on the next open.
    modal.area.textContent = "";
    modal.list.textContent = "";
    html.removeAttribute("data-okf-modal-open");
    enlarge.setAttribute("aria-expanded", "false");
    enlarge.focus();
  }

  enlarge.addEventListener("click", openModal);

  render();
  var toc = document.getElementById("okf-toc");
  context.insertBefore(section, toc && toc.parentNode === context ? toc.nextSibling : context.firstChild);
  context.hidden = false;
})();
