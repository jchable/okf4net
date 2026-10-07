// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The local graph of a concept page (spec §4.3, §12.4): the concept and its
// neighbours at one or two hops, in rings. Two parts in one classic script:
//
// - window.OkfLocal, pure (no DOM, no clock, no randomness): from the site
//   index alone it computes the drawn nodes and their ring positions, every
//   index edge between them and the full neighbour list.
//   tools/viewer-security-check calls it directly (cases/p2.js).
// - the page part, which runs only on a concept page (<html
//   data-okf-view="page">) and draws that result into #okf-context: an SVG
//   built by code (fixed element and attribute names, shapes from OkfShapes,
//   labels by textContent, URLs from index paths through OkfSite.resolve),
//   never inside #okf-body.
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
  // One ring at 1 hop (or when the second hop draws nothing), two at 2 hops;
  // measured on the mockup's 298 x 248 drawing, labels 14 under a shape.
  var ONE_RING = Object.freeze([Object.freeze({ rx: 94, ry: 70 })]);
  var TWO_RINGS = Object.freeze([Object.freeze({ rx: 52, ry: 40 }), Object.freeze({ rx: 118, ry: 88 })]);

  function round(value) { return Math.round(value * 100) / 100; }

  // [from, to, count, toGhost] as IndexScript writes it, turned into node
  // keys; null for anything else, so a damaged index draws less instead of
  // throwing.
  function ends(edge, conceptCount, ghostCount) {
    if (!Array.isArray(edge)) { return null; }
    var from = edge[0];
    var to = edge[1];
    var ghost = edge[3] === 1;
    if (!Number.isInteger(from) || from < 0 || from >= conceptCount) { return null; }
    if (!Number.isInteger(to) || to < 0 || to >= (ghost ? ghostCount : conceptCount)) { return null; }
    return { from: from, to: ghost ? conceptCount + to : to, count: Number.isInteger(edge[2]) && edge[2] > 0 ? edge[2] : 1 };
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
      var pair = e.from * N + e.to;
      if (byPair.has(pair)) {
        drawn[byPair.get(pair)].count += e.count;
        continue;
      }
      byPair.set(pair, drawn.length);
      // Dashed when the target is closer to the centre than the source
      // ("referenced by"), solid otherwise ("links to"), spec X7.
      drawn.push({ from: slot[e.from], to: slot[e.to], count: e.count, dashed: dist[e.to] < dist[e.from], paired: false });
    }
    for (k = 0; k < drawn.length; k++) {
      work++;
      drawn[k].paired = byPair.has(nodes[drawn[k].to].key * N + nodes[drawn[k].from].key);
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
  // pair sit 2 x shift apart (spec §4.3: ± 3). null when the ends coincide.
  function segment(x1, y1, r1, x2, y2, r2, shift) {
    var args = [x1, y1, r1, x2, y2, r2, shift];
    for (var k = 0; k < args.length; k++) {
      if (!Number.isFinite(args[k])) { throw new TypeError("okf-local: segment needs finite numbers"); }
    }
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
    return {
      x1: round(x1 + ux * from + nx),
      y1: round(y1 + uy * from + ny),
      x2: round(x2 - ux * to + nx),
      y2: round(y2 - uy * to + ny),
    };
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
})();
