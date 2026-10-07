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

  window.OkfLocal = Object.freeze({
    CAP: CAP, VIEW: VIEW, build: build, segment: segment, lastSegment: lastSegment,
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
    if (index.concepts[i].id === currentId) { centre = i; break; }
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

  function idOf(key) { return key < C ? index.concepts[key].id : index.ghosts[key - C].id; }
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
      label.setAttribute("x", num(node.x));
      label.setAttribute("y", num(node.y + size.size / 2 + (j === 0 ? 6 : 0) + 14));
      label.setAttribute("text-anchor", "middle");
      label.textContent = lastSegment(idOf(node.key));
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
          if (key < C) { navigate(site.resolve(root, index.concepts[key].path)); }
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

  function render(result) {
    for (var b = 0; b < hopButtons.length; b++) {
      hopButtons[b].setAttribute("aria-pressed", b + 1 === result.hops ? "true" : "false");
    }
    canvas.textContent = "";
    canvas.appendChild(drawing(result));
  }

  render(first);
  var toc = document.getElementById("okf-toc");
  context.insertBefore(section, toc && toc.parentNode === context ? toc.nextSibling : context.firstChild);
  context.hidden = false;
})();
