// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Slice P2 (local graph) cases. run.js's case loader calls register(h) with
// the frozen helpers (spec §12.7). Pure cases load the real okf-local.js into
// a bare window and call window.OkfLocal directly. Page cases open pages of
// the site okf-render generates from fixtures/hostile-bundle/ (its p2-local/
// folder and the p2- fixtures), sometimes under a synthetic index served in
// place of assets/okf-index.js. Expected counts come from the index a case
// reads or builds, never from a fixture's size; work is counted, never timed.
"use strict";
const fs = require("fs");
const path = require("path");
const { JSDOM } = require("jsdom");

const LOCAL_JS = path.join(__dirname, "..", "..", "..", "src", "OKF4net.Viewer", "Assets", "okf-local.js");

// A bare window that loaded okf-local.js alone: no OkfSite, no OkfShapes and
// no data-okf-view, so its page part returns at once and only OkfLocal exists.
function okfLocal() {
  const dom = new JSDOM("<!doctype html><html><body></body></html>", { runScripts: "outside-only" });
  dom.window.eval(fs.readFileSync(LOCAL_JS, "utf8"));
  return dom.window;
}

// The tree a v2 index carries (destination + children, spec §3.2), so the
// P1 and P1.1 scripts of a page served a synthetic index get a sound one.
function treeOf(ids) {
  const root = { children: new Map() };
  ids.forEach((id, position) => {
    let node = root;
    for (const segment of id.split("/")) {
      if (!node.children.has(segment)) { node.children.set(segment, { name: segment, concept: -1, children: new Map() }); }
      node = node.children.get(segment);
    }
    node.concept = position;
  });
  const freeze = (node) => Array.from(node.children.values())
    .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
    .map((child) => ({ name: child.name, concept: child.concept, children: freeze(child) }));
  return freeze(root);
}

// A version-2 site index (spec §12.1) over `ids`, in the order given: that
// order IS index order for okf-local.js, which never compares ids. `links`
// are [from, to] concept positions, or [from, to, "ghost"] with `to` a
// position in `ghostIds`. `titles[k]` replaces the title of concept k.
function siteIndex(ids, links, ghostIds = [], titles = []) {
  const concepts = ids.map((id, k) => ({
    id, title: titles[k] === undefined ? id : titles[k], type: "Note", tags: [], path: `${id}.html`,
    trust: "unverified", staleAfterMs: null, staleAfterDate: null, typeIndex: 0, description: "",
  }));
  return {
    version: 2,
    concepts,
    ghosts: ghostIds.map((id) => ({ id })),
    edges: links.map(([from, to, ghost]) => [from, to, 1, ghost === "ghost" ? 1 : 0]),
    tree: treeOf(ids),
    types: [{ name: "Note", count: ids.length, slot: 0 }],
  };
}

function registerPure(h) {
  const { check, assert } = h;

  check("local graph: build is deterministic, finite, and inside the view", () => {
    const index = siteIndex(["c", "a", "b", "d", "e"], [[0, 1], [2, 0], [1, 3], [3, 4], [0, 0, "ghost"]], ["gone"]);
    const one = okfLocal().OkfLocal;
    const other = okfLocal().OkfLocal;
    for (const hops of [1, 2]) {
      const first = JSON.stringify(one.build(index, 0, hops));
      assert(first === JSON.stringify(one.build(index, 0, hops)), `hops ${hops}: two builds in one window differ`);
      assert(first === JSON.stringify(other.build(index, 0, hops)), `hops ${hops}: two windows disagree`);
      for (const node of one.build(index, 0, hops).nodes) {
        assert(Number.isFinite(node.x) && Number.isFinite(node.y), `hops ${hops}: node ${node.key} at (${node.x}, ${node.y})`);
        assert(node.x >= 0 && node.x <= one.VIEW.width && node.y >= 0 && node.y <= one.VIEW.height,
          `hops ${hops}: node ${node.key} at (${node.x}, ${node.y}) is outside the ${one.VIEW.width} x ${one.VIEW.height} view`);
      }
    }
  });

  check("local graph: membership is undirected, edges keep their direction, links back to the centre are dashed", () => {
    const { OkfLocal } = okfLocal();
    // c -> a, b -> c, a -> d: a and b are direct neighbours (one linked to,
    // one linking in); d is two hops away, through a.
    const index = siteIndex(["c", "a", "b", "d"], [[0, 1], [2, 0], [1, 3]]);
    const one = OkfLocal.build(index, 0, 1);
    const list1 = JSON.stringify(one.list.map((e) => [e.key, e.dist, e.rel]));
    assert(list1 === "[[1,1,1],[2,1,2]]", `1 hop list (key, dist, rel): ${list1}`);
    const edges1 = JSON.stringify(one.edges.map((e) => [one.nodes[e.from].key, one.nodes[e.to].key, e.dashed]));
    assert(edges1 === "[[0,1,false],[2,0,true]]", `1 hop edges (from, to, dashed): ${edges1}`);
    const two = OkfLocal.build(index, 0, 2);
    const list2 = JSON.stringify(two.list.map((e) => [e.key, e.dist, e.via]));
    assert(list2 === "[[1,1,-1],[2,1,-1],[3,2,1]]", `2 hops list (key, dist, via): ${list2}`);
    const edges2 = JSON.stringify(two.edges.map((e) => [two.nodes[e.from].key, two.nodes[e.to].key, e.dashed]));
    assert(edges2 === "[[0,1,false],[2,0,true],[1,3,false]]", `2 hops edges: ${edges2}`);
  });

  check("local graph: every index edge between drawn nodes is drawn, including between two neighbours", () => {
    const { OkfLocal } = okfLocal();
    // c -> a, c -> b, a -> b: a -> b joins two nodes of the same ring.
    const r = OkfLocal.build(siteIndex(["c", "a", "b"], [[0, 1], [0, 2], [1, 2]]), 0, 1);
    const edges = JSON.stringify(r.edges.map((e) => [r.nodes[e.from].key, r.nodes[e.to].key, e.dashed]));
    assert(edges === "[[0,1,false],[0,2,false],[1,2,false]]", `edges: ${edges}`);
  });

  check("local graph: a self-link is no neighbour, opposite edges are paired and offset, repeated records add up", () => {
    const { OkfLocal } = okfLocal();
    const index = siteIndex(["a", "b"], [[0, 0], [0, 1], [1, 0]]);
    index.edges.push([0, 1, 2, 0]); // a damaged index repeating a pair: its count adds up
    const r = OkfLocal.build(index, 0, 1);
    assert(r.total === 1 && r.list[0].key === 1 && r.list[0].rel === 3, `list: ${JSON.stringify(r.list)}`);
    const edges = JSON.stringify(r.edges.map((e) => [r.nodes[e.from].key, r.nodes[e.to].key, e.count, e.paired]));
    assert(edges === "[[0,1,3,true],[1,0,1,true]]", `edges (from, to, count, paired): ${edges}`);
    const self = OkfLocal.build(siteIndex(["s"], [[0, 0]]), 0, 2);
    assert(self.total === 0 && self.nodes.length === 1 && self.edges.length === 0, `a self-link only: ${JSON.stringify(self)}`);
    // Each direction moves 3 along its own normal: the two lines are 6 apart.
    const there = OkfLocal.segment(0, 0, 0, 10, 0, 0, 3);
    const back = OkfLocal.segment(10, 0, 0, 0, 0, 0, 3);
    assert(there.y1 === 3 && there.y2 === 3 && back.y1 === -3 && back.y2 === -3, `offsets: ${JSON.stringify([there, back])}`);
  });

  check("local graph: no neighbour gives the centre alone; one neighbour gives one finite node in the view", () => {
    const { OkfLocal } = okfLocal();
    const r = OkfLocal.build(siteIndex(["only"], []), 0, 2);
    assert(r.total === 0 && r.omitted === 0 && r.nodes.length === 1 && r.edges.length === 0 && r.list.length === 0, JSON.stringify(r));
    assert(r.nodes[0].x === OkfLocal.VIEW.cx && r.nodes[0].y === OkfLocal.VIEW.cy, "the centre is not at the middle of the view");
    // A ring of one (m = 1): the angle formula must still give a finite point.
    const one = OkfLocal.build(siteIndex(["c", "n"], [[1, 0]]), 0, 2);
    const n = one.nodes[1];
    assert(one.nodes.length === 2 && one.edges.length === 1 && one.edges[0].dashed, `one neighbour: ${JSON.stringify(one)}`);
    assert(Number.isFinite(n.x) && Number.isFinite(n.y) && n.x >= 0 && n.x <= OkfLocal.VIEW.width && n.y >= 0 && n.y <= OkfLocal.VIEW.height,
      `the lone neighbour sits at (${n.x}, ${n.y})`);
  });

  check("local graph: the cap draws 40 nodes, direct neighbours first, then index order, ghosts after concepts", () => {
    const { OkfLocal } = okfLocal();
    assert(OkfLocal.CAP === 40, `CAP is ${OkfLocal.CAP}`);
    // A hub with 300 direct neighbours (odd ones linked to, even ones linking
    // in), five absent targets, and 50 concepts two hops away.
    const ids = ["hub"];
    const links = [];
    for (let k = 1; k <= 300; k++) { ids.push(`s${k}`); links.push(k % 2 ? [0, k] : [k, 0]); }
    for (let k = 0; k < 50; k++) { ids.push(`far${k}`); links.push([1 + k, 301 + k]); }
    for (let g = 0; g < 5; g++) { links.push([0, g, "ghost"]); }
    const index = siteIndex(ids, links, ["g0", "g1", "g2", "g3", "g4"]);
    const firstGhost = index.concepts.length;
    for (const hops of [1, 2]) {
      const r = OkfLocal.build(index, 0, hops);
      const drawn = r.nodes.slice(1).map((n) => n.key);
      assert(r.nodes.length === 40, `hops ${hops}: ${r.nodes.length} nodes drawn`);
      assert(JSON.stringify(drawn) === JSON.stringify(Array.from({ length: 39 }, (_, k) => k + 1)), `hops ${hops}: drew ${JSON.stringify(drawn)}`);
      assert(r.nodes.slice(1).every((n) => n.dist === 1), `hops ${hops}: a second-hop node was drawn before every direct neighbour`);
      const total = hops === 1 ? 305 : 355;
      assert(r.total === total && r.omitted === total - 39, `hops ${hops}: total ${r.total}, omitted ${r.omitted}`);
      const ghostsAt = r.list.findIndex((e) => e.key >= firstGhost);
      assert(ghostsAt === 300 && r.list[300].dist === 1, `hops ${hops}: the ghosts are not listed right after the 300 direct concepts (at ${ghostsAt})`);
    }
    // Absent targets count in the cap too.
    const absent = [];
    for (let g = 0; g < 45; g++) { absent.push([0, g, "ghost"]); }
    const r = OkfLocal.build(siteIndex(["hub"], absent, Array.from({ length: 45 }, (_, g) => `gone${g}`)), 0, 1);
    assert(r.nodes.length === 40 && r.omitted === 6, `45 absent targets: ${r.nodes.length} drawn, ${r.omitted} omitted`);
  });

  check("local graph: the second hop never starts from an absent concept", () => {
    const { OkfLocal } = okfLocal();
    // c -> gone and x -> gone: sharing an absent target is no path to x.
    const r = OkfLocal.build(siteIndex(["c", "x"], [[0, 0, "ghost"], [1, 0, "ghost"]], ["gone"]), 0, 2);
    assert(JSON.stringify(r.list.map((e) => e.key)) === "[2]", `list: ${JSON.stringify(r.list)}`);
  });

  check("local graph: ids named after Object.prototype members are ordinary concepts", () => {
    const window = okfLocal();
    const before = window.Object.getOwnPropertyNames(window.Object.prototype).join();
    const ids = ["__proto__", "constructor", "toString", "hasOwnProperty", "valueOf"];
    const r = window.OkfLocal.build(siteIndex(ids, [[0, 1], [0, 2], [3, 0], [4, 0]]), 0, 2);
    assert(JSON.stringify(r.list.map((e) => e.key)) === "[1,2,3,4]", `list: ${JSON.stringify(r.list)}`);
    assert(window.OkfLocal.lastSegment("a/__proto__") === "__proto__" && window.OkfLocal.lastSegment("constructor") === "constructor", "lastSegment");
    assert(window.Object.getOwnPropertyNames(window.Object.prototype).join() === before, "Object.prototype changed");
  });

  check("local graph: a damaged edge record is skipped, never thrown on", () => {
    const { OkfLocal } = okfLocal();
    const index = siteIndex(["a", "b"], [[0, 1]]);
    index.edges.push([0, 99, 1, 0], [0, 0, 1, 1], ["x", 1, 1, 0], null, [0, 1.5, 1, 0], [-1, 1, 1, 0], "0,1");
    // A ghost flag that is not 0 or 1 is damage, never read as "a concept edge".
    index.edges.push([1, 0, 1, true], [1, 0, 1, 2], [1, 0, 1, "1"], [1, 0, 1, -1], [1, 0, 1], [1, 0, 1, null]);
    const r = OkfLocal.build(index, 0, 2);
    assert(r.total === 1 && r.edges.length === 1 && r.edges[0].count === 1, JSON.stringify(r));
    // Two records of an enormous count add up to a count, never to Infinity.
    const huge = siteIndex(["a", "b"], []);
    huge.edges.push([0, 1, 1e308, 0], [0, 1, 1e308, 0]);
    const h2 = OkfLocal.build(huge, 0, 1);
    assert(h2.edges.length === 1 && Number.isFinite(h2.edges[0].count) && h2.edges[0].count > 0, `count: ${h2.edges[0].count}`);
  });

  check("local graph: two hops around a hub cost linear work in the edges (counted, not timed)", () => {
    const { OkfLocal } = okfLocal();
    const n = 2000;
    const ids = [];
    const links = [];
    for (let k = 0; k < n; k++) { ids.push(`n${String(k).padStart(4, "0")}`); }
    for (let k = 1; k <= 600; k++) {
      links.push([0, k]);
      for (let j = 1; j <= 3; j++) { links.push([k, 601 + ((k * 7 + j * 131) % 1399)]); }
    }
    const index = siteIndex(ids, links);
    const edgeCount = index.edges.length;
    // The module's own `work` is a claim about itself; this counts, on the
    // harness side, every element of index.edges it actually reads.
    let reads = 0;
    index.edges = new Proxy(index.edges, {
      get(target, prop, receiver) {
        if (typeof prop === "string" && /^\d+$/.test(prop)) { reads++; }
        return Reflect.get(target, prop, receiver);
      },
    });
    const r = OkfLocal.build(index, 0, 2);
    const bound = 4 * edgeCount + 2 * n;
    assert(r.work > 0 && r.work <= bound,
      `${r.work} units of work for ${edgeCount} edges and ${n} nodes (bound ${bound}; one scan of the edges per first-hop node would be ~${600 * edgeCount})`);
    assert(reads >= edgeCount && reads <= 3 * edgeCount && r.work >= reads,
      `${reads} edge records read for ${edgeCount} edges (at most 3 passes: ${3 * edgeCount}), and the reported work (${r.work}) must cover them`);
    assert(r.nodes.length === OkfLocal.CAP, `${r.nodes.length} nodes drawn`);
  });

  check("local graph: segment never returns a non-finite coordinate", () => {
    const window = okfLocal();
    const { OkfLocal } = window;
    for (const bad of [[NaN, 0, 1, 10, 0, 1, 0], [0, 0, 1, Infinity, 0, 1, 0], [0, 0, 1, 10, 0, 1, NaN]]) {
      let threw = null;
      try { OkfLocal.segment(...bad); } catch (e) { threw = e; }
      assert(threw instanceof window.TypeError, `segment(${bad.join(", ")}) did not throw a TypeError`);
    }
    assert(OkfLocal.segment(5, 5, 3, 5, 5, 3, 0) === null, "coincident ends gave a segment");
    const close = OkfLocal.segment(0, 0, 10, 5, 0, 10, 0);
    assert(close && close.x1 === 0 && close.x2 === 5 && [close.y1, close.y2].every(Number.isFinite), `overlapping reaches: ${JSON.stringify(close)}`);
    const trimmed = OkfLocal.segment(0, 0, 10, 100, 0, 11, 0);
    assert(trimmed.x1 === 10 && trimmed.x2 === 89, `trimmed: ${JSON.stringify(trimmed)}`);
    // Finite input can still overflow: a null segment, never an Infinity coordinate;
    // and a huge but representable one is still drawn.
    assert(OkfLocal.segment(1e308, 0, 0, 1e308, 10, 0, -1e308) === null, "an overflowing offset gave a segment");
    for (const args of [[0, 0, 0, 10, 0, 0, 1e307], [1e15, 0, 0, 0, 0, 0, 0]]) {
      const out = OkfLocal.segment(...args);
      assert(out !== null && [out.x1, out.y1, out.x2, out.y2].every(Number.isFinite), `segment(${args.join(", ")}) should give a finite segment, gave ${JSON.stringify(out)}`);
    }
    // A length that overflows is no segment either (never an Infinity or NaN coordinate).
    const overflow = OkfLocal.segment(0, 0, 1e300, 1e300, 1e300, 0, 3);
    assert(overflow === null || [overflow.x1, overflow.y1, overflow.x2, overflow.y2].every(Number.isFinite), `overflowing length: ${JSON.stringify(overflow)}`);
  });

  check("local graph: segment refuses a negative reach", () => {
    const window = okfLocal();
    for (const bad of [[0, 0, -1, 10, 0, 1, 0], [0, 0, 1, 10, 0, -1, 0]]) {
      let threw = null;
      try { window.OkfLocal.segment(...bad); } catch (e) { threw = e; }
      assert(threw instanceof window.TypeError, `segment(${bad.join(", ")}) did not throw a TypeError`);
    }
  });

  check("local graph: build rejects a centre that is not a concept, and depths other than 1 and 2", () => {
    const window = okfLocal();
    const index = siteIndex(["a"], []);
    for (const [centre, hops] of [[1, 1], [-1, 1], [0.5, 1], [0, 0], [0, 3], [0, "2"]]) {
      let threw = null;
      try { window.OkfLocal.build(index, centre, hops); } catch (e) { threw = e; }
      assert(threw instanceof window.TypeError, `build(index, ${centre}, ${JSON.stringify(hops)}) did not throw a TypeError`);
    }
  });

  check("local graph: OkfLocal is pure -- no DOM, no clock, no randomness (executed, not grepped)", () => {
    const window = okfLocal();
    const trap = (what) => () => { throw new Error(`OkfLocal used ${what}`); };
    window.Math.random = trap("Math.random");
    window.Date.now = trap("Date.now");
    window.document.createElement = trap("document.createElement");
    window.document.createElementNS = trap("document.createElementNS");
    window.document.getElementById = trap("document.getElementById");
    window.document.querySelector = trap("document.querySelector");
    window.OkfLocal.build(siteIndex(["c", "a", "b"], [[0, 1], [2, 1], [0, 0, "ghost"]], ["gone"]), 0, 2);
    window.OkfLocal.segment(0, 0, 1, 10, 10, 1, 3);
  });

  check("local graph: a second-hop node reached from two direct neighbours takes the smallest key as its via", () => {
    const { OkfLocal } = okfLocal();
    // c -> a, c -> b, a -> d, b -> d: d is reached through a (key 1) and b (key 2).
    const edgesInOrder = [[0, 1], [0, 2], [1, 3], [2, 3]];
    // ... and the same with d linking to a and b (the other direction of the walk).
    const linkingIn = [[0, 1], [0, 2], [3, 1], [3, 2]];
    for (const links of [edgesInOrder, edgesInOrder.slice().reverse(), linkingIn, linkingIn.slice().reverse()]) {
      const r = OkfLocal.build(siteIndex(["c", "a", "b", "d"], links), 0, 2);
      const list = JSON.stringify(r.list.map((e) => [e.key, e.dist, e.via]));
      assert(list === "[[1,1,-1],[2,1,-1],[3,2,1]]", `list (key, dist, via): ${list}`);
    }
  });

  check("local graph: a self-link on the centre or on a neighbour adds no neighbour and no hop", () => {
    const { OkfLocal } = okfLocal();
    // c -> c, c -> a, a -> a: only a is a neighbour; a's self-link makes nothing two hops away.
    const r = OkfLocal.build(siteIndex(["c", "a"], [[0, 0], [0, 1], [1, 1]]), 0, 2);
    assert(JSON.stringify(r.list.map((e) => [e.key, e.dist, e.rel, e.via])) === "[[1,1,1,-1]]", `list: ${JSON.stringify(r.list)}`);
    assert(r.nodes.length === 2 && r.edges.length === 1 && r.omitted === 0, `nodes/edges: ${JSON.stringify(r)}`);
  });

  check("local graph: repeated edges between one pair never repeat a neighbour", () => {
    const { OkfLocal } = okfLocal();
    const links = [[0, 1], [0, 1], [0, 1], [1, 2], [1, 2]];
    for (const hops of [1, 2]) {
      const r = OkfLocal.build(siteIndex(["c", "a", "d"], links), 0, hops);
      const keys = JSON.stringify(r.list.map((e) => e.key));
      assert(keys === (hops === 1 ? "[1]" : "[1,2]"), `hops ${hops}: list keys ${keys}`);
      assert(r.nodes.length === r.total + 1 && new Set(r.nodes.map((n) => n.key)).size === r.nodes.length, `hops ${hops}: a node was drawn twice`);
      assert(r.edges[0].count === 3, `hops ${hops}: the repeated c -> a records add up to ${r.edges[0].count}`);
    }
  });

  check("local graph: the layout does not depend on edge order; neighbours sit on the diagonals, hop 2 outside hop 1, coordinates in hundredths", () => {
    const { OkfLocal } = okfLocal();
    // c with four direct neighbours and six second-hop concepts, edges in a
    // fixed pseudo-random order (no Math.random: the harness is deterministic too).
    const ids = ["c", "a", "b", "d", "e", "f1", "f2", "f3", "f4", "f5", "f6"];
    const links = [[0, 1], [2, 0], [0, 3], [4, 0], [1, 5], [2, 6], [3, 7], [4, 8], [1, 9], [3, 10], [5, 6], [3, 6]];
    const shuffled = [];
    let seed = 12345;
    const pool = links.slice();
    while (pool.length) { seed = (seed * 1103515245 + 12345) % 2147483648; shuffled.push(pool.splice(seed % pool.length, 1)[0]); }
    for (const hops of [1, 2]) {
      const reference = OkfLocal.build(siteIndex(ids, links), 0, hops);
      for (const other of [links.slice().reverse(), shuffled]) {
        const r = OkfLocal.build(siteIndex(ids, other), 0, hops);
        assert(JSON.stringify(r.nodes) === JSON.stringify(reference.nodes), `hops ${hops}: the nodes depend on the edge order`);
        assert(JSON.stringify(r.list) === JSON.stringify(reference.list), `hops ${hops}: the list depends on the edge order`);
      }
      for (const node of reference.nodes) {
        assert(Math.abs(node.x * 100 - Math.round(node.x * 100)) < 1e-9 && Math.abs(node.y * 100 - Math.round(node.y * 100)) < 1e-9,
          `hops ${hops}: node ${node.key} at (${node.x}, ${node.y}) is not in hundredths`);
      }
    }
    // Four neighbours: clockwise from the top, one in each quadrant (half a step off the axes).
    const four = OkfLocal.build(siteIndex(ids, links), 0, 1);
    const { cx, cy } = OkfLocal.VIEW;
    const quadrants = four.nodes.slice(1).map((n) => [Math.sign(n.x - cx), Math.sign(n.y - cy)]);
    assert(JSON.stringify(quadrants) === "[[1,-1],[1,1],[-1,1],[-1,-1]]", `quadrants of the 4 neighbours (dx, dy signs): ${JSON.stringify(quadrants)}`);
    // Second-hop nodes are farther from the centre than first-hop ones.
    const two = OkfLocal.build(siteIndex(ids, links), 0, 2);
    const far = (n) => Math.hypot(n.x - cx, n.y - cy);
    const inner = two.nodes.filter((n) => n.dist === 1).map(far);
    const outer = two.nodes.filter((n) => n.dist === 2).map(far);
    assert(inner.length === 4 && outer.length === 6 && Math.min(...outer) > Math.max(...inner),
      `ring distances: first hop ${inner.map((v) => v.toFixed(1))}, second hop ${outer.map((v) => v.toFixed(1))}`);
  });

  check("local graph: no drawn shape touches the centre, its selection square or its label, or leaves the view, for every node count up to the cap", () => {
    const { OkfLocal } = okfLocal();
    const { width, height, cx, cy } = OkfLocal.VIEW;
    // A shape reaches 13 from its centre (the diamond: 25.5 / 2); a label sits
    // at baseline y + 14 under it, 3 of descender. The centre's selection
    // square is +- 20 and its label (X7: baseline cy + 14 + 6 + 14, Space Mono
    // 10.5, up to about 120 wide) fills x 89..209, y 140..156.
    const half = 13;
    const band = { x1: 89, x2: 209, y1: 140, y2: 156 };
    const problem = (n) => {
      if (n.x - half < 0 || n.x + half > width || n.y - half < 0 || n.y + half + 14 + 3 > height) { return "leaves the view"; }
      if (n.x + half > band.x1 && n.x - half < band.x2 && n.y + half > band.y1 && n.y - half < band.y2) { return "touches the centre's label"; }
      if (n.x + half > cx - 20 && n.x - half < cx + 20 && n.y + half > cy - 20 && n.y - half < cy + 20) { return "touches the centre"; }
      return null;
    };
    const star = (direct, second) => {
      // c -> a1..aN; a1 -> d1..dM: N direct and M second-hop neighbours.
      const ids = ["c"];
      const links = [];
      for (let k = 1; k <= direct; k++) { ids.push(`a${k}`); links.push([0, k]); }
      for (let k = 1; k <= second; k++) { ids.push(`d${k}`); links.push([1, direct + k]); }
      return siteIndex(ids, links);
    };
    let builds = 0;
    for (let m = 1; m <= OkfLocal.CAP - 1; m++) {
      for (const n of OkfLocal.build(star(m, 0), 0, 1).nodes.slice(1)) {
        const why = problem(n);
        assert(!why, `1 hop, ${m} neighbours: node ${n.key} at (${n.x}, ${n.y}) ${why}`);
      }
      builds++;
    }
    for (let m = 1; m <= OkfLocal.CAP - 2; m++) {
      for (let second = 1; m + second <= OkfLocal.CAP - 1; second++) {
        const r = OkfLocal.build(star(m, second), 0, 2);
        assert(r.nodes.length === m + second + 1, `2 hops, ${m} + ${second}: ${r.nodes.length} nodes drawn`);
        for (const n of r.nodes.slice(1)) {
          const why = problem(n);
          assert(!why, `2 hops, ${m} + ${second}: node ${n.key} at (${n.x}, ${n.y}) ${why}`);
        }
        builds++;
      }
    }
    assert(builds === 39 + 741, `${builds} layouts checked`);
  });
}

const SVG_NS = "http://www.w3.org/2000/svg";
// The fixed vocabulary of spec §12.2.
const SVG_ELEMENTS = new Set(["svg", "g", "circle", "rect", "path", "line", "text", "title", "defs", "marker"]);
const SVG_ATTRIBUTES = new Set(["viewBox", "width", "height", "class", "aria-hidden", "focusable", "role", "aria-label",
  "tabindex", "id", "cx", "cy", "r", "x", "y", "x1", "y1", "x2", "y2", "d", "stroke-width", "stroke-dasharray",
  "text-anchor", "dominant-baseline", "marker-end", "refX", "refY", "markerWidth", "markerHeight", "orient", "transform"]);
const NUMERIC = new Set(["cx", "cy", "r", "x", "y", "x1", "y1", "x2", "y2", "width", "height", "stroke-width",
  "refX", "refY", "markerWidth", "markerHeight"]);

// Spec §4.5, §12.2: the drawing holds the fixed vocabulary only, every
// number in it is finite, every id is a fixed okf- value.
function assertFixedSvg(assert, svg) {
  for (const node of [svg, ...svg.querySelectorAll("*")]) {
    assert(node.namespaceURI === SVG_NS, `<${node.localName}> is outside the SVG namespace`);
    assert(SVG_ELEMENTS.has(node.localName), `<${node.localName}> is not in the fixed vocabulary`);
    for (const name of node.getAttributeNames()) {
      const value = node.getAttribute(name);
      assert(SVG_ATTRIBUTES.has(name), `<${node.localName}> carries ${name}="${value}", outside the fixed vocabulary`);
      if (name === "id") { assert(value.startsWith("okf-"), `id="${value}" is not a fixed okf- value`); }
      if (NUMERIC.has(name) && !(node === svg && name === "width" && value === "100%")) {
        assert(Number.isFinite(Number(value)), `<${node.localName}> ${name}="${value}" is not a finite number`);
      }
      if (name === "d" || name === "viewBox" || name === "stroke-dasharray") {
        const numbers = value.replace(/[MLZ]/g, " ").trim().split(/\s+/).filter((t) => t !== "");
        assert(numbers.every((t) => Number.isFinite(Number(t))), `<${node.localName}> ${name}="${value}" holds a non-finite number`);
      }
    }
  }
}

// The test's own reading of spec §4.3 over an executed index, written apart
// from okf-local.js: keys are concept positions, then concepts.length + a
// ghost position. Only for pages under the cap (no ordering is checked).
function expectedHood(index, centre, hops) {
  const C = index.concepts.length;
  const ends = index.edges.map(([from, to, , ghost]) => [from, ghost ? C + to : to]).filter(([from, to]) => from !== to);
  const dist = new Map([[centre, 0]]);
  for (const [from, to] of ends) {
    if (from === centre) { dist.set(to, 1); }
    if (to === centre) { dist.set(from, 1); }
  }
  if (hops === 2) {
    const first = new Set(Array.from(dist).filter(([key, d]) => d === 1 && key < C).map(([key]) => key));
    for (const [from, to] of ends) {
      if (first.has(from) && !dist.has(to)) { dist.set(to, 2); }
      if (first.has(to) && !dist.has(from)) { dist.set(from, 2); }
    }
  }
  const drawn = ends.filter(([from, to]) => dist.has(from) && dist.has(to))
    .map(([from, to]) => ({ from, to, dashed: dist.get(to) < dist.get(from) }));
  return { dist, total: dist.size - 1, drawn };
}

function positionOf(window, id) {
  const position = window.OKF_INDEX.concepts.findIndex((c) => c.id === id);
  if (position === -1) { throw new Error(`${id} is not in the executed index`); }
  return position;
}

function click(window, target) {
  target.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 }));
}

function registerPage(h) {
  const { checkAsync, assert, openPage, navigations } = h;

  checkAsync("local graph: a concept page draws its neighbourhood in #okf-context, after the contents, before Referenced by", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const section = doc.getElementById("okf-local-graph");
    assert(section, "no #okf-local-graph on a page with neighbours");
    const context = doc.getElementById("okf-context");
    assert(section.parentElement === context && !context.hidden, "the section is not shown in #okf-context");
    assert(section.previousElementSibling === doc.getElementById("okf-toc"),
      `the section follows <${section.previousElementSibling && section.previousElementSibling.localName}>, not #okf-toc`);
    assert(section.nextElementSibling && section.nextElementSibling.classList.contains("okf-backlinks"), "the section is not right before Referenced by");
    const title = doc.getElementById("okf-local-title");
    assert(section.getAttribute("aria-labelledby") === "okf-local-title" && title.textContent === "Neighbourhood", "the section is not named Neighbourhood");
    assert(title.localName === "h2" && title.classList.contains("okf-section-title"), "the title is not the shared section title");
    const svg = section.querySelector(".okf-local-canvas > svg");
    assert(svg && svg.getAttribute("role") === "img" && /^Local graph of p2-local\/c, 1 hop: /.test(svg.getAttribute("aria-label")),
      `svg role / name: ${svg && svg.getAttribute("role")} / ${svg && svg.getAttribute("aria-label")}`);
    assert(svg.getAttribute("viewBox") === "0 0 298 248", `viewBox ${svg.getAttribute("viewBox")}`);
    assert(doc.getElementById("okf-body").querySelector("svg") === null, "an svg reached #okf-body");
    const expected = expectedHood(window.OKF_INDEX, positionOf(window, "p2-local/c"), 1);
    const drawn = svg.querySelectorAll("g.okf-node").length;
    assert(drawn === 1 + expected.total, `${drawn} nodes for ${expected.total} neighbours`);
    assertFixedSvg(assert, svg);
  });

  checkAsync("local graph: 1 hop by default, 2 hops adds the second ring, both announced by aria-pressed", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const one = doc.getElementById("okf-local-hops-1");
    const two = doc.getElementById("okf-local-hops-2");
    const group = one.parentElement;
    assert(group.getAttribute("role") === "group" && group.getAttribute("aria-label") === "Neighbourhood depth", "the toggle is not a named group");
    assert(one.localName === "button" && one.type === "button" && two.type === "button", "the toggle is not two buttons");
    assert(one.textContent === "1 hop" && two.textContent === "2 hops", `labels: ${one.textContent} / ${two.textContent}`);
    assert(one.getAttribute("aria-pressed") === "true" && two.getAttribute("aria-pressed") === "false", "1 hop is not the default");
    const centre = positionOf(window, "p2-local/c");
    const nodes = () => doc.querySelectorAll("#okf-local-graph svg g.okf-node").length;
    const shallow = expectedHood(window.OKF_INDEX, centre, 1);
    const deep = expectedHood(window.OKF_INDEX, centre, 2);
    assert(deep.total > shallow.total, "this case needs a page with a second hop");
    two.click();
    assert(one.getAttribute("aria-pressed") === "false" && two.getAttribute("aria-pressed") === "true", "2 hops is not announced as pressed");
    assert(nodes() === 1 + deep.total, `2 hops: ${nodes()} nodes for ${deep.total} neighbours`);
    // Only the drawing's own <svg>: T4's list rows add svg.okf-glyph icons
    // to the section, which are not drawings.
    assert(doc.querySelectorAll("#okf-local-graph .okf-local-canvas > svg").length === 1, "the old drawing was kept beside the new one");
    assert(doc.querySelectorAll("#okf-local-arrow, #okf-local-arrow-in").length === 2, "the arrow markers are duplicated");
    assertFixedSvg(assert, doc.querySelector("#okf-local-graph .okf-local-canvas > svg"));
    one.click();
    assert(nodes() === 1 + shallow.total, "back to 1 hop did not redraw the first ring only");
  });

  checkAsync("local graph: every index edge between drawn nodes is drawn, dashed when it points back toward the centre", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    doc.getElementById("okf-local-hops-2").click();
    const expected = expectedHood(window.OKF_INDEX, positionOf(window, "p2-local/c"), 2);
    const lines = Array.from(doc.querySelectorAll("#okf-local-graph svg line.okf-local-edge"));
    const dashed = lines.filter((l) => l.classList.contains("okf-local-edge-in"));
    const wantDashed = expected.drawn.filter((e) => e.dashed).length;
    assert(lines.length === expected.drawn.length, `${lines.length} lines for ${expected.drawn.length} index edges between drawn nodes`);
    assert(dashed.length === wantDashed && wantDashed > 0 && wantDashed < lines.length, `${dashed.length} dashed lines, expected ${wantDashed} of ${lines.length}`);
    for (const line of lines) {
      const back = line.classList.contains("okf-local-edge-in");
      assert(line.getAttribute("stroke-width") === "1.4", `stroke-width ${line.getAttribute("stroke-width")}`);
      assert(back ? line.getAttribute("stroke-dasharray") === "4 3" : !line.hasAttribute("stroke-dasharray"), "a line is dashed against its direction");
      assert(line.getAttribute("marker-end") === (back ? "url(#okf-local-arrow-in)" : "url(#okf-local-arrow)"), `marker-end ${line.getAttribute("marker-end")}`);
    }
    // hub <-> a are both drawn at 2 hops from c: the two lines must not
    // retrace each other (offset ± 3).
    const coords = (l) => ["x1", "y1", "x2", "y2"].map((name) => Number(l.getAttribute(name)));
    for (const one of lines) {
      for (const other of lines) {
        const [a1, b1, c1, d1] = coords(one);
        const [a2, b2, c2, d2] = coords(other);
        assert(!(one !== other && a1 === c2 && b1 === d2 && c1 === a2 && d1 === b2), "two opposite edges are drawn on top of each other");
      }
    }
    const marker = doc.getElementById("okf-local-arrow");
    assert(marker && marker.getAttribute("markerWidth") === "7" && marker.getAttribute("markerHeight") === "7", "the arrow is not 7");
  });

  checkAsync("local graph: shapes are OkfShapes nodes at the local and localCenter sizes, labels under or over them", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    doc.getElementById("okf-local-hops-2").click();
    const S = window.OkfShapes;
    const index = window.OKF_INDEX;
    const hood = window.OkfLocal.build(index, positionOf(window, "p2-local/c"), 2);
    const groups = Array.from(doc.querySelectorAll("#okf-local-graph svg g.okf-node"));
    assert(groups.length > 2, "this case needs drawn neighbours");
    const kinds = new Set();
    groups.forEach((g, k) => {
      const centre = k === 0;
      assert(g.classList.contains("okf-selected") === centre, `node ${k}: okf-selected is ${centre ? "missing" : "set"}`);
      const title = g.firstElementChild;
      assert(title.localName === "title", `node ${k}: its first child is <${title.localName}>, not <title>`);
      const ghost = title.textContent.startsWith("absent: ");
      const id = ghost ? title.textContent.slice("absent: ".length) : title.textContent;
      const kind = ghost ? "ghost" : S.kindOf(index, positionOf(window, id));
      kinds.add(kind);
      const size = S.SIZES[centre ? "localCenter" : "local"][kind];
      const label = g.lastElementChild;
      assert(label.localName === "text" && label.textContent === window.OkfLocal.lastSegment(id), `node ${k}: label "${label.textContent}" for ${id}`);
      // X7: baseline at cy + size / 2 + 14 (centre: + 6 more, under its square) unless
      // another label or shape is there: then a whole number of rows (12) further under
      // or over it (OkfLocal.labels; the cases below pin the rule itself).
      const anchor = label.getAttribute("text-anchor");
      assert(anchor === "middle" || anchor === "start" || anchor === "end", `node ${k}: label anchor ${anchor}`);
      const node = hood.nodes[k];
      const half = size.size / 2 + (centre ? 6 : 0);
      const fromUnder = Number(label.getAttribute("y")) - (node.y + half + 14);
      const fromOver = node.y - half - 4 - Number(label.getAttribute("y"));
      const rows = (d) => Math.abs(Math.round(d / 12) * 12 - d) < 0.02 && Math.round(d / 12) >= 0 && Math.round(d / 12) <= 10;
      assert(rows(fromUnder) || rows(fromOver), `node ${k}: label baseline ${label.getAttribute("y")} is on no row under or over its node at y=${node.y}`);
      if (centre) { assert(fromUnder === 0 && anchor === "middle", "the centre label left its place under the square"); }
      const reference = S.node(kind, node.x, node.y, size.size, size);
      assert(label.previousElementSibling.outerHTML === reference.lastElementChild.outerHTML,
        `node ${k} (${id}, ${kind}): ${label.previousElementSibling.outerHTML} is not OkfShapes' ${reference.lastElementChild.outerHTML}`);
      assert(centre === (g.querySelector(".okf-node-ring") !== null), `node ${k}: the selection square is ${centre ? "missing" : "present"}`);
    });
    assert(kinds.size >= 3, `only ${kinds.size} distinct shapes drawn: the fixture no longer exercises the size table`);
  });

  checkAsync("local graph: absent concepts are drawn but never navigable", async () => {
    const window = await openPage("p2-local/hub.html");
    const doc = window.document;
    const index = window.OKF_INDEX;
    const ghostIds = new Set(index.ghosts.map((g) => g.id));
    const hood = expectedHood(index, positionOf(window, "p2-local/hub"), 1);
    const expected = Array.from(hood.dist.keys()).filter((key) => key >= index.concepts.length).length;
    const ghosts = Array.from(doc.querySelectorAll("#okf-local-graph svg g.okf-local-ghost"));
    assert(ghosts.length === expected && expected > 0, `${ghosts.length} absent concepts drawn, expected ${expected}`);
    let attempts = 0;
    doc.addEventListener("okf:navigate", (e) => { attempts++; e.preventDefault(); });
    for (const g of ghosts) {
      const title = g.firstElementChild.textContent;
      assert(title.startsWith("absent: ") && ghostIds.has(title.slice("absent: ".length)), `ghost title "${title}"`);
      assert(g.lastElementChild.classList.contains("okf-local-label-ghost"), "a ghost label is not marked as such");
      assert(!g.classList.contains("okf-local-node"), "a ghost is marked as a navigable node");
      click(window, g.lastElementChild);
      click(window, g.lastElementChild.previousElementSibling);
    }
    assert(attempts === 0 && navigations(window) === 0, "clicking an absent concept tried to navigate");
  });

  checkAsync("local graph: a click on a concept node opens its page through the resolver", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const node = Array.from(doc.querySelectorAll("#okf-local-graph svg g.okf-local-node"))
      .find((g) => g.firstElementChild.textContent === "p2-local/a");
    assert(node, "node p2-local/a is not drawn on c's page");
    let href = null;
    const veto = (e) => { href = e.detail.href; e.preventDefault(); };
    doc.addEventListener("okf:navigate", veto);
    click(window, node.lastElementChild);
    const path = window.OKF_INDEX.concepts[positionOf(window, "p2-local/a")].path;
    assert(href === "../" + path, `navigated to ${href}, expected ../${path} (the page's root prefix + the index path)`);
    assert(navigations(window) === 0, "a vetoed okf:navigate still navigated");
    doc.removeEventListener("okf:navigate", veto);
    click(window, node.lastElementChild.previousElementSibling);
    assert(navigations(window) === 1, `${navigations(window)} navigations after an unvetoed click on the shape`);
  });

  checkAsync("local graph: Open in graph repeats the header's Global graph link, fragment included", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const header = doc.getElementById("okf-global-graph");
    const open = doc.getElementById("okf-local-open");
    assert(header && open, "the header link or Open in graph is missing");
    assert(open.getAttribute("href") === header.getAttribute("href") && open.getAttribute("href").endsWith("#p2-local/c"),
      `Open in graph: ${open.getAttribute("href")}, header: ${header.getAttribute("href")}`);
    assert(open.textContent === "Open in graph" && open.parentElement.classList.contains("okf-local-foot"), "Open in graph is not in the foot");
    const dot = String.fromCharCode(0xb7);
    assert(open.previousElementSibling.textContent === `solid = links to ${dot} dashed = referenced by`, `legend: ${open.previousElementSibling.textContent}`);
  });

  checkAsync("local graph: without the header's graph link, Open in graph is left out", async () => {
    // Simulates a header without #okf-global-graph: the one lookup
    // okf-local.js makes for it (from #okf-tools) answers nothing.
    const beforeParse = (w) => {
      const query = w.Element.prototype.querySelector;
      w.Element.prototype.querySelector = function (selector) {
        return this.id === "okf-tools" && selector === "#okf-global-graph" ? null : query.call(this, selector);
      };
    };
    const window = await openPage("p2-local/c.html", { beforeParse });
    const doc = window.document;
    assert(doc.getElementById("okf-local-graph"), "the section is missing");
    assert(doc.getElementById("okf-local-open") === null, "Open in graph was written without a link to repeat");
  });

  checkAsync("local graph: Open in graph repeats whatever href the header's link carries (the header alone names the graph page)", async () => {
    // Spec §12.3: the header is the only source of the graph page's file name.
    // Make its link answer a name nothing in okf-local.js could guess.
    const wanted = "../graph-7.html#p2-local/c";
    const beforeParse = (w) => {
      const get = w.Element.prototype.getAttribute;
      w.Element.prototype.getAttribute = function (name) {
        return this.id === "okf-global-graph" && name === "href" ? wanted : get.call(this, name);
      };
    };
    const window = await openPage("p2-local/c.html", { beforeParse });
    const open = window.document.getElementById("okf-local-open");
    assert(open, "Open in graph is missing");
    assert(open.getAttribute("href") === wanted, `Open in graph is ${open.getAttribute("href")}, the header's link says ${wanted}`);
  });

  checkAsync("local graph: arrows stop outside their target; only opposite edges are offset, by 3", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    doc.getElementById("okf-local-hops-2").click();
    const S = window.OkfShapes;
    const index = window.OKF_INDEX;
    const C = index.concepts.length;
    const result = window.OkfLocal.build(index, positionOf(window, "p2-local/c"), 2);
    const lines = Array.from(doc.querySelectorAll("#okf-local-graph svg line.okf-local-edge"));
    assert(lines.length === result.edges.length, `${lines.length} lines for ${result.edges.length} laid-out edges`);
    let paired = 0;
    result.edges.forEach((e, k) => {
      const a = result.nodes[e.from];
      const b = result.nodes[e.to];
      const [x1, y1, x2, y2] = ["x1", "y1", "x2", "y2"].map((name) => Number(lines[k].getAttribute(name)));
      // Signed distance of the line's start from the axis through both centres.
      const offset = ((b.x - a.x) * (y1 - a.y) - (b.y - a.y) * (x1 - a.x)) / Math.hypot(b.x - a.x, b.y - a.y);
      const opposite = result.edges.some((o) => o.from === e.to && o.to === e.from);
      assert(opposite === Boolean(e.paired), `edge ${k}: paired is ${e.paired}, an opposite edge ${opposite ? "exists" : "does not exist"}`);
      if (opposite) { paired++; }
      assert(Math.abs(Math.abs(offset) - (opposite ? 3 : 0)) <= 0.05, `edge ${k}: offset ${offset.toFixed(2)} from the axis, expected ${opposite ? "+-3" : "0"}`);
      const centre = e.to === 0;
      const kind = b.key < C ? S.kindOf(index, b.key) : "ghost";
      const size = S.SIZES[centre ? "localCenter" : "local"][kind].size;
      const gap = Math.hypot(x2 - b.x, y2 - b.y);
      assert(gap >= size / 2 + (centre ? 6 : 0) - 0.05, `edge ${k}: its arrow ends ${gap.toFixed(2)} from the centre of a ${size} px shape, under the shape`);
    });
    assert(paired >= 2, `${paired} opposite edges: the fixture no longer exercises the offset`);
  });

  checkAsync("local graph: ids reach the drawing as text, never as markup", async () => {
    const evil = "<img src=x onerror=window.__pwned=1>";
    const source = fs.readFileSync(path.join(__dirname, "..", ".generated", "hostile-site", "assets", "okf-index.js"), "utf8");
    const damage = (code) => ({ "assets/okf-index.js": `${source}\n;(function () { var i = window.OKF_INDEX; ${code} })();` });
    const concept = await openPage("p2-local/c.html", {
      override: damage(`i.concepts.forEach(function (c) { if (c.id === "p2-local/a") { c.id = "p2-local/${evil}"; } });`),
    });
    const ghost = await openPage("p2-local/hub.html", {
      override: damage(`i.ghosts.forEach(function (g) { if (g.id === "p2-local/gone-1") { g.id = "p2-local/${evil}"; } });`),
    });
    for (const [window, id] of [[concept, `p2-local/${evil}`], [ghost, `p2-local/${evil}`]]) {
      const section = window.document.getElementById("okf-local-graph");
      const svg = section.querySelector(".okf-local-canvas > svg");
      assertFixedSvg(assert, svg);
      const titles = Array.from(svg.querySelectorAll("title")).map((t) => t.textContent);
      assert(titles.includes(id) || titles.includes(`absent: ${id}`), `no node titled with the literal id: ${JSON.stringify(titles)}`);
      const labels = Array.from(svg.querySelectorAll("text.okf-local-label")).map((t) => t.textContent);
      // The label is cut to 24 characters with an ellipsis; the node title keeps the whole id.
      const ellipsis = String.fromCharCode(0x2026);
      assert(labels.some((l) => l.endsWith(ellipsis) && l.length === 24 && evil.startsWith(l.slice(0, -1))), `no label reads the literal markup, cut: ${JSON.stringify(labels)}`);
      assert(section.querySelector("img") === null && window.__pwned === undefined, "an id became an element");
    }
  });

  checkAsync("local graph: a damaged entry (null concept, null ghost, no path) draws less, never throws", async () => {
    const source = fs.readFileSync(path.join(__dirname, "..", ".generated", "hostile-site", "assets", "okf-index.js"), "utf8");
    // The explorer and the palette read every entry and are out of this
    // task's reach (as in run.js's own damaged-index case): only the scripts
    // that tolerate a null entry load.
    const blocked = ["assets/okf-explorer.js", "assets/okf-palette.js"];
    const damaged = (code) => ({ "assets/okf-index.js": `${source}\n;(function () { var i = window.OKF_INDEX; ${code} })();` });
    // A null in front shifts every position: it is met by the search for the
    // centre and, through the shifted edges, by the neighbours' ids.
    const nullFirst = await openPage("p2-local/c.html", { override: damaged("i.concepts.unshift(null);"), blocked });
    assert(nullFirst.document.getElementById("okf-body").textContent.length > 0, "a null concept emptied the page");
    // The hub's own absent concept and c's own neighbour are the null ones.
    const nullGhost = await openPage("p2-local/hub.html", {
      override: damaged('i.ghosts.forEach(function (g, k) { if (g && g.id === "p2-local/gone-1") { i.ghosts[k] = null; } });'),
      blocked,
    });
    assert(nullGhost.document.getElementById("okf-local-graph"), "a null ghost removed the whole neighbourhood");
    const nullNeighbour = await openPage("p2-local/c.html", {
      override: damaged('i.concepts.forEach(function (c, k) { if (c && c.id === "p2-local/a") { i.concepts[k] = null; } });'),
      blocked,
    });
    assert(nullNeighbour.document.getElementById("okf-local-graph"), "a null neighbour removed the whole neighbourhood");
    // The list keeps the row (the drawing keeps the node), as text, no link.
    const nullRows = Array.from(nullNeighbour.document.querySelectorAll("#okf-local-list li"));
    assert(nullRows.length === 3 && nullRows.every((li) => li.querySelector(".okf-local-rel").textContent !== ""),
      `a null neighbour changed the list: ${nullRows.length} rows`);
    assert(nullRows.some((li) => li.firstElementChild.localName === "span" && !li.firstElementChild.classList.contains("okf-local-absent")),
      "a null neighbour is not listed as plain text");
    // A concept without a path is drawn but cannot be opened (no "../undefined").
    const noPath = await openPage("p2-local/c.html", {
      override: damaged(`i.concepts.forEach(function (c) { if (c.id === "p2-local/a") { delete c.path; } });`),
    });
    let vetoed = 0;
    noPath.document.addEventListener("okf:navigate", (e) => { vetoed++; e.preventDefault(); });
    const node = Array.from(noPath.document.querySelectorAll("#okf-local-graph svg g.okf-local-node"))
      .find((g) => g.firstElementChild.textContent === "p2-local/a");
    assert(node, "the concept without a path is not drawn");
    click(noPath, node.lastElementChild);
    assert(vetoed === 0 && navigations(noPath) === 0, "a concept without a path was navigated to");
    const pathless = Array.from(noPath.document.querySelectorAll("#okf-local-list li"))
      .find((li) => li.querySelector(".okf-local-id").textContent === "p2-local/a");
    assert(pathless && pathless.firstElementChild.localName === "span" && !pathless.firstElementChild.hasAttribute("href"),
      "a concept without a path is listed with a link");
    assert(noPath.document.getElementById("okf-local-list").textContent.indexOf("undefined") === -1, "the list printed undefined");
  });

  checkAsync("local graph: a page whose only content is its neighbourhood un-hides the side panel", async () => {
    // outbound.md links out, has no heading and nothing links to it: no
    // contents, no Referenced by, so the panel is hidden until the section opens it.
    const window = await openPage("p2-local/outbound.html");
    const doc = window.document;
    const context = doc.getElementById("okf-context");
    assert(doc.getElementById("okf-toc") === null || doc.getElementById("okf-toc").hidden, "outbound.md was expected to have no contents");
    assert(doc.querySelector("#okf-context .okf-backlinks") === null, "outbound.md was expected to have no backlinks");
    assert(doc.getElementById("okf-local-graph") && doc.getElementById("okf-local-graph").parentElement === context, "no neighbourhood on a page with one outgoing link");
    assert(!context.hidden, "the side panel stayed hidden around a neighbourhood");
    assert(doc.querySelectorAll("#okf-local-graph svg g.okf-node").length === 2, "one outgoing link did not draw two nodes");
  });

  checkAsync("local graph: nothing on the index, without an index or OkfShapes, or without a neighbour", async () => {
    const pages = [
      ["index.html", {}],
      ["p2-local/c.html", { blocked: ["assets/okf-index.js"] }],
      ["p2-local/c.html", { blocked: ["assets/okf-shapes.js"] }],
      ["p2-local/lonely.html", {}],
      ["p2-local/self.html", {}],
    ];
    for (const [page, opts] of pages) {
      const window = await openPage(page, opts);
      assert(window.document.getElementById("okf-local-graph") === null, `${page} ${JSON.stringify(opts)}: a local graph was drawn`);
    }
    const lonely = await openPage("p2-local/lonely.html");
    assert(lonely.document.getElementById("okf-context").hidden, "a page with no contents, no backlinks and no neighbour showed an empty side panel");
  });
}

function registerList(h) {
  const { checkAsync, assert, openPage } = h;
  const dot = String.fromCharCode(0xb7);

  checkAsync("local graph: over the cap, +N omitted opens the full list, in index order", async () => {
    const ids = ["p2-local/hub"];
    const links = [];
    for (let k = 0; k < 120; k++) {
      ids.push(`p2-local/n${String(k).padStart(3, "0")}`);
      links.push(k % 2 ? [k + 1, 0] : [0, k + 1]);
    }
    const ghostIds = ["p2-local/zz-0", "p2-local/zz-1", "p2-local/zz-2"];
    ghostIds.forEach((_, g) => links.push([0, g, "ghost"]));
    const source = `window.OKF_INDEX = ${JSON.stringify(siteIndex(ids, links, ghostIds))};`;
    const window = await openPage("p2-local/hub.html", { override: { "assets/okf-index.js": source } });
    const doc = window.document;
    const section = doc.getElementById("okf-local-graph");
    const total = 123;
    const cap = window.OkfLocal.CAP;
    const drawn = section.querySelectorAll("svg g.okf-node").length;
    assert(drawn === cap, `${drawn} nodes drawn`);
    const more = section.querySelector(".okf-local-canvas > button.okf-local-omitted");
    assert(more && more.type === "button" && more.textContent === `+${total - (cap - 1)} omitted`, `omitted: ${more && more.textContent}`);
    assert(more.getAttribute("aria-controls") === "okf-local-list", "+N omitted does not control the list");
    const details = doc.getElementById("okf-local-list");
    assert(details && details.localName === "details" && details.classList.contains("okf-local-list") && !details.open, "the list is not a closed <details>");
    const summary = details.querySelector("summary");
    assert(summary.textContent === `List ${dot} ${total} neighbours`, `summary: ${summary.textContent}`);
    const rows = Array.from(details.querySelectorAll("li"));
    assert(rows.length === total, `${rows.length} rows for ${total} neighbours`);
    assert(rows[0].querySelector(".okf-local-id").textContent === "p2-local/n000", `first row: ${rows[0].textContent}`);
    assert(rows[total - 1].querySelector(".okf-local-id").textContent === "absent: p2-local/zz-2", `last row: ${rows[total - 1].textContent}`);
    more.click();
    assert(details.open, "+N omitted did not open the list");
    // jsdom 29 focuses a details' first <summary>; recette X9 checks real browsers.
    assert(doc.activeElement === summary, `focus after +N omitted: <${doc.activeElement.localName}>`);
    doc.getElementById("okf-local-hops-2").click();
    assert(details.open, "redrawing at 2 hops closed the list");
    assert(details.querySelectorAll("li").length === total, "2 hops changed a list that has no second hop");
  });

  checkAsync("local graph: hostile titles and ids named after Object.prototype members stay inert and distinct", async () => {
    const ids = ["__proto__", "a/__proto__", "constructor", "hasOwnProperty", "toString"];
    const titles = ['<img src=x onerror="window.__pwned=1">Proto', "</title><script>window.__pwned=2</script>", "constructor",
      '"><svg onload="window.__pwned=3">', "toString"];
    const links = [[0, 1], [0, 2], [3, 0], [4, 0], [1, 2]];
    const source = `window.OKF_INDEX = ${JSON.stringify(siteIndex(ids, links, [], titles))};`;
    const window = await openPage("__proto__.html", { override: { "assets/okf-index.js": source } });
    const doc = window.document;
    const section = doc.getElementById("okf-local-graph");
    assert(section, "no local graph for the concept __proto__");
    // Spec §7 control 3: inspected after opening AND after redrawing.
    doc.getElementById("okf-local-hops-2").click();
    doc.getElementById("okf-local-hops-1").click();
    const svg = section.querySelector("svg");
    assertFixedSvg(assert, svg);
    const drawn = Array.from(svg.querySelectorAll("g.okf-node"), (g) => g.firstElementChild.textContent);
    assert(JSON.stringify(drawn) === JSON.stringify(ids), `drawn: ${JSON.stringify(drawn)}`);
    const listed = Array.from(section.querySelectorAll(".okf-local-list .okf-local-id"), (s) => s.textContent);
    assert(JSON.stringify(listed) === JSON.stringify(ids.slice(1)), `listed: ${JSON.stringify(listed)}`);
    const rows = Array.from(section.querySelectorAll(".okf-local-list a.okf-row"));
    rows.forEach((a, k) => assert(a.getAttribute("title") === titles[k + 1], `row ${k}: title attribute ${a.getAttribute("title")}`));
    const live = doc.getElementById("okf-context").querySelectorAll("img, script, iframe, object, [onerror], [onload]");
    assert(live.length === 0, `markup from a title became live in #okf-context (${live.length})`);
    assert(svg.textContent.indexOf("<") === -1, "a title reached the drawing");
    assert(window.__pwned === undefined, "a hostile title executed");
  });

  checkAsync("local graph: the drawing is an image with no tab stop; the toggle and the list are the keyboard path", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const svg = doc.querySelector("#okf-local-graph svg");
    assert(svg.getAttribute("role") === "img" && svg.getAttribute("focusable") === "false", "the drawing is not a non-focusable image");
    assert(!svg.hasAttribute("tabindex") && svg.querySelectorAll("[tabindex]").length === 0, "the drawing has a tab stop");
    const expected = expectedHood(window.OKF_INDEX, positionOf(window, "p2-local/c"), 1);
    const rows = Array.from(doc.querySelectorAll("#okf-local-list li"));
    assert(rows.length === expected.total, `${rows.length} rows for ${expected.total} neighbours`);
    for (const row of rows) {
      const line = row.firstElementChild;
      const ghost = line.querySelector(".okf-local-id").textContent.startsWith("absent: ");
      assert(ghost ? line.localName === "span" : line.localName === "a" && line.hasAttribute("href"), `row "${row.textContent}": <${line.localName}>`);
      assert(line.classList.contains("okf-row"), "a row is not a shared .okf-row");
      const glyph = line.firstElementChild;
      assert(glyph.localName === "svg" && glyph.classList.contains("okf-glyph") && glyph.getAttribute("aria-hidden") === "true",
        "a row's glyph is not an OkfShapes icon");
    }
    const rels = rows.map((r) => r.querySelector(".okf-local-rel").textContent);
    assert(rels.includes("links to") && rels.includes("referenced by"), `relations: ${JSON.stringify(rels)}`);
    doc.getElementById("okf-local-hops-2").click();
    const deep = Array.from(doc.querySelectorAll("#okf-local-list .okf-local-rel"), (s) => s.textContent);
    assert(deep.some((t) => /^2 hops via p2-local\//.test(t)), `2 hops relations: ${JSON.stringify(deep)}`);
    assert(doc.querySelector("#okf-local-graph .okf-local-omitted") === null, "+N omitted shown under the cap");
  });

  checkAsync("local graph: the list names both directions of a two-way link, and absent concepts without a link", async () => {
    const window = await openPage("p2-local/hub.html");
    const doc = window.document;
    const rows = Array.from(doc.querySelectorAll("#okf-local-list li"), (li) => ({
      id: li.querySelector(".okf-local-id").textContent,
      rel: li.querySelector(".okf-local-rel").textContent,
      tag: li.firstElementChild.localName,
    }));
    const a = rows.find((r) => r.id === "p2-local/a");
    assert(a && a.rel === `links to ${dot} referenced by`, `p2-local/a: ${JSON.stringify(a)}`);
    const absent = rows.filter((r) => r.id.startsWith("absent: "));
    assert(absent.length > 0 && absent.every((r) => r.tag === "span" && r.rel === "links to"), `absent rows: ${JSON.stringify(absent)}`);
    assert(!rows.some((r) => r.id === "p2-local/hub"), "the self-link listed the hub as its own neighbour");
  });
}

// The list's wording, boundaries and links, pinned exactly (review of P2 Task 4).
function registerListPins(h) {
  const { checkAsync, assert, openPage } = h;
  const dot = String.fromCharCode(0xb7);
  const served = (index) => ({ override: { "assets/okf-index.js": `window.OKF_INDEX = ${JSON.stringify(index)};` } });
  const pairs = (doc) => Array.from(doc.querySelectorAll("#okf-local-list li"),
    (li) => [li.querySelector(".okf-local-id").textContent, li.querySelector(".okf-local-rel").textContent]);

  // A hub with `n` direct neighbours (alternating directions) and nothing else.
  async function hub(n) {
    const ids = ["p2-local/hub"];
    const links = [];
    for (let k = 0; k < n; k++) {
      ids.push(`p2-local/n${String(k).padStart(3, "0")}`);
      links.push(k % 2 ? [k + 1, 0] : [0, k + 1]);
    }
    return openPage("p2-local/hub.html", served(siteIndex(ids, links)));
  }

  checkAsync("local graph: the cap boundary - 39 neighbours show no mention, 40 say +1 omitted, 41 say +2", async () => {
    for (const [n, omitted] of [[38, 0], [39, 0], [40, 1], [41, 2]]) {
      const window = await hub(n);
      const section = window.document.getElementById("okf-local-graph");
      const drawn = section.querySelectorAll("svg g.okf-node").length;
      const more = section.querySelector("button.okf-local-omitted");
      assert(drawn === Math.min(n + 1, 40), `${n} neighbours: ${drawn} nodes drawn`);
      assert(omitted === 0 ? more === null : more !== null && more.textContent === `+${omitted} omitted`,
        `${n} neighbours: ${more ? more.textContent : "no mention"}`);
      assert(window.document.querySelectorAll("#okf-local-list li").length === n, `${n} neighbours: the list has every one`);
      assert(window.document.querySelector("#okf-local-list summary").textContent === `List ${dot} ${n} neighbours`, `${n} neighbours: summary`);
    }
  });

  checkAsync("local graph: each row names exactly its relation - 1 hop and 2 hops, the smallest direct neighbour wins, ghosts included", async () => {
    // hub -> a, b -> hub, hub <-> d; e is reached from b AND a (edge of b
    // first), f and g only through d; ghost zz-0 is linked by hub, zz-1 by a.
    const ids = ["hub", "a", "b", "d", "e", "f", "g"].map((s) => `p2-local/${s}`);
    const links = [[0, 1], [2, 0], [0, 3], [3, 0], [2, 4], [1, 4], [3, 5], [6, 3], [0, 0, "ghost"], [1, 1, "ghost"]];
    const window = await openPage("p2-local/hub.html", served(siteIndex(ids, links, ["p2-local/zz-0", "p2-local/zz-1"])));
    const doc = window.document;
    const one = JSON.stringify(pairs(doc));
    const wantOne = JSON.stringify([
      ["p2-local/a", "links to"],
      ["p2-local/b", "referenced by"],
      ["p2-local/d", `links to ${dot} referenced by`],
      ["absent: p2-local/zz-0", "links to"],
    ]);
    assert(one === wantOne, `1 hop: ${one}`);
    doc.getElementById("okf-local-hops-2").click();
    const two = JSON.stringify(pairs(doc));
    const wantTwo = JSON.stringify([
      ["p2-local/a", "links to"],
      ["p2-local/b", "referenced by"],
      ["p2-local/d", `links to ${dot} referenced by`],
      ["absent: p2-local/zz-0", "links to"],
      ["p2-local/e", "2 hops via p2-local/a"],
      ["p2-local/f", "2 hops via p2-local/d"],
      ["p2-local/g", "2 hops via p2-local/d"],
      ["absent: p2-local/zz-1", "2 hops via p2-local/a"],
    ]);
    assert(two === wantTwo, `2 hops: ${two}`);
    doc.getElementById("okf-local-hops-1").click();
    assert(JSON.stringify(pairs(doc)) === wantOne, "going back to 1 hop did not restore the list");
  });

  checkAsync("local graph: a 2-hop row whose direct neighbour is unreadable says '2 hops' with no dangling via", async () => {
    const index = siteIndex(["p2-local/hub", "p2-local/a", "p2-local/e"], [[0, 1], [1, 2]]);
    index.concepts[1] = null;
    const window = await openPage("p2-local/hub.html", Object.assign(served(index), { blocked: ["assets/okf-explorer.js", "assets/okf-palette.js"] }));
    const doc = window.document;
    doc.getElementById("okf-local-hops-2").click();
    const rels = pairs(doc).map(([, rel]) => rel);
    assert(JSON.stringify(rels) === JSON.stringify(["links to", "2 hops"]), `relations: ${JSON.stringify(rels)}`);
  });

  checkAsync("local graph: the list's links are the drawing's links, through the resolver, from a nested page and under a mount", async () => {
    for (const mount of ["", "moved/dir/"]) {
      const window = await openPage("p2-local/c.html", { mount });
      const doc = window.document;
      const base = window.location.href.slice(0, -"p2-local/c.html".length);
      const hrefs = new Map(); // concept id -> where the drawing's click goes
      let href = null;
      doc.addEventListener("okf:navigate", (e) => { href = e.detail.href; e.preventDefault(); });
      for (const g of doc.querySelectorAll("#okf-local-graph svg g.okf-local-node:not(.okf-selected)")) {
        href = null;
        click(window, g.lastElementChild);
        hrefs.set(g.firstElementChild.textContent, href);
      }
      const links = Array.from(doc.querySelectorAll("#okf-local-list a.okf-row"));
      assert(links.length === 3 && hrefs.size === 3, `${mount}: ${links.length} links, ${hrefs.size} drawn concepts`);
      for (const a of links) {
        const id = a.querySelector(".okf-local-id").textContent;
        const path = window.OKF_INDEX.concepts[positionOf(window, id)].path;
        assert(a.getAttribute("href") === hrefs.get(id) && a.getAttribute("href") === "../" + path,
          `${mount}${id}: list href ${a.getAttribute("href")}, drawing href ${hrefs.get(id)}`);
        assert(a.href === base + path, `${mount}${id}: resolves to ${a.href}, expected ${base + path}`);
      }
    }
  });

  checkAsync("local graph: the summary is singular for one neighbour, rows carry their type label or 'absent concept', the list keeps its role", async () => {
    const index = siteIndex(["p2-local/hub", "p2-local/x", "p2-local/y"], [[0, 1], [0, 2], [0, 0, "ghost"]], ["p2-local/zz"]);
    index.concepts[2].type = "";
    const window = await openPage("p2-local/hub.html", served(index));
    const doc = window.document;
    const titles = Array.from(doc.querySelectorAll("#okf-local-list li"), (li) => li.querySelector("svg.okf-glyph > title").textContent);
    assert(JSON.stringify(titles) === JSON.stringify(["Note", "(no type)", "absent concept"]), `glyph titles: ${JSON.stringify(titles)}`);
    assert(doc.querySelector("#okf-local-list > ul").getAttribute("role") === "list", "the list lost its explicit role");
    const one = await openPage("p2-local/hub.html", served(siteIndex(["p2-local/hub", "p2-local/x"], [[0, 1]])));
    assert(one.document.querySelector("#okf-local-list summary").textContent === `List ${dot} 1 neighbour`,
      `summary: ${one.document.querySelector("#okf-local-list summary").textContent}`);
  });
}

// P2's rules are anchored to #okf-context: the real section keeps its style, body <code> wearing the same classes stays plain (spec §12.6).
function registerChrome(h) {
  const { checkAsync, assert, openPage, unwrapMedia } = h;

  checkAsync("local graph: its rules are anchored to #okf-context and still style the real section", async () => {
    // The real section, over the cap so +N omitted exists.
    const ids = ["p2-local/hub"];
    const links = [];
    for (let k = 0; k < 50; k++) { ids.push(`p2-local/m${String(k).padStart(2, "0")}`); links.push([0, k + 1]); }
    const source = `window.OKF_INDEX = ${JSON.stringify(siteIndex(ids, links))};`;
    const window = await openPage("p2-local/hub.html", { override: { "assets/okf-index.js": source } });
    const doc = window.document;
    const probes = [
      ["#okf-local-graph .okf-local-canvas", "position", "relative"],
      ["#okf-local-graph .okf-local-canvas", "height", "250px"],
      ["#okf-local-graph .okf-hops", "display", "flex"],
      ["#okf-local-hops-1", "height", "24px"],
      ["#okf-local-hops-1", "font-size", "12px"],
      ["#okf-local-graph .okf-local-omitted", "position", "absolute"],
      ["#okf-local-graph .okf-local-foot", "display", "flex"],
      ["#okf-local-graph .okf-local-foot", "font-size", "12px"],
      ["#okf-local-open", "font-weight", "600"],
      ["#okf-local-list summary", "cursor", "pointer"],
    ];
    for (const [selector, prop, value] of probes) {
      const el = doc.querySelector(selector);
      assert(el, `${selector} is missing`);
      const got = window.getComputedStyle(el).getPropertyValue(prop);
      assert(got === value, `the real ${selector} lost its ${prop}: ${value} (got ${got})`);
    }

    // The drawing's colours, fonts and cursors, on a small neighbourhood with an absent
    // neighbour (the cap above leaves no room for one). jsdom keeps var() unresolved, so
    // this pins that each paint comes from its token: --edge, --ink, --ghost (§11.0).
    const small = siteIndex(["p2-local/hub", "p2-local/out", "p2-local/in"], [[0, 1], [2, 0], [0, 0, "ghost"]], ["p2-local/gone"]);
    const drawnWindow = await openPage("p2-local/hub.html", { override: { "assets/okf-index.js": `window.OKF_INDEX = ${JSON.stringify(small)};` } });
    const drawn = drawnWindow.document;
    const paints = [
      [".okf-local-edge:not(.okf-local-edge-in)", "stroke", "var(--edge)"],
      [".okf-local-edge-in", "stroke", "var(--edge)"],
      [".okf-local-arrowhead:not(.okf-local-arrowhead-in)", "fill", "var(--edge)"],
      [".okf-local-arrowhead-in", "fill", "var(--edge)"],
      ["text.okf-local-label:not(.okf-local-label-center):not(.okf-local-label-ghost)", "fill", "var(--ink)"],
      ["text.okf-local-label:not(.okf-local-label-center):not(.okf-local-label-ghost)", "font-size", "10px"],
      [".okf-local-label-center", "font-size", "10.5px"],
      [".okf-local-label-center", "font-weight", "700"],
      [".okf-local-label-ghost", "fill", "var(--ghost)"],
      [".okf-local-node", "cursor", "pointer"],
      [".okf-local-ghost", "cursor", "auto"],
      [".okf-local-absent", "color", "var(--ghost)"],
    ];
    for (const [selector, prop, value] of paints) {
      const el = drawn.querySelector(`#okf-local-graph ${selector}`) || drawn.querySelector(`#okf-local-list ${selector}`);
      assert(el, `${selector} is missing from the drawing`);
      const got = drawnWindow.getComputedStyle(el).getPropertyValue(prop);
      assert(got === value, `the drawing's ${selector} lost its ${prop}: ${value} (got ${got})`);
    }

    // Body <code> wearing the same classes stays a plain <code>.
    const page = await openPage("p2-chrome-classes.html");
    const body = page.document.getElementById("okf-body");
    const reference = body.querySelector("code:not([class])");
    const worn = Array.from(body.querySelectorAll("code[class]"));
    assert(reference && worn.length >= 4 && worn[0].classList.contains("okf-local-canvas"),
      "the fixture lost its classed <code> elements, or the sanitizer dropped class: this case tests nothing");
    const props = ["position", "display", "height", "width", "border", "border-left", "background", "background-color",
      "color", "cursor", "font-family", "font-size", "font-weight", "padding", "margin", "margin-left", "right", "bottom",
      "flex", "list-style", "fill", "stroke", "overflow-wrap", "justify-content", "gap", "text-decoration"];
    const compare = (when) => {
      const expected = page.getComputedStyle(reference);
      for (const el of worn) {
        const style = page.getComputedStyle(el);
        for (const prop of props) {
          assert(style.getPropertyValue(prop) === expected.getPropertyValue(prop),
            `${when}: <code class="${el.getAttribute("class")}"> ${prop}: ${style.getPropertyValue(prop)} (an unclassed <code> has ${expected.getPropertyValue(prop)})`);
        }
      }
    };
    compare("as loaded");
    // jsdom applies no @media rule: compare again with every one unwrapped.
    const unwrapped = page.document.createElement("style");
    unwrapped.textContent = unwrapMedia(fs.readFileSync(path.join(__dirname, "..", ".generated", "hostile-site", "assets", "viewer.css"), "utf8"));
    page.document.head.appendChild(unwrapped);
    compare("with every @media rule applied");
  });
}

// Node labels (review of P2 Task 5, D1 and D2): a label never leaves the view
// and, for an ordinary neighbourhood, never covers another label. The width is
// the module's stated estimate (6.2 per character, the centre's 6.5; a label is
// 8 above its baseline and 3 below it), restated here so a change of the
// constants is a change of this case.
function registerLabels(h) {
  const { check, checkAsync, assert, openPage } = h;
  const CHAR = 6.2;
  const CENTRE_CHAR = 6.5;
  const MARGIN = 4;
  const view = { width: 298, height: 248 };
  const boxOf = (text, x, y, anchor, centre) => {
    const width = text.length * (centre ? CENTRE_CHAR : CHAR);
    const x1 = anchor === "middle" ? x - width / 2 : anchor === "start" ? x : x - width;
    return { x1, x2: x1 + width, y1: y - 8, y2: y + 3 };
  };
  const meet = (a, b) => a.x1 < b.x2 && b.x1 < a.x2 && a.y1 < b.y2 && b.y1 < a.y2;
  // Problems of one set of boxes [name, box]: outside the 4 px margin, or touching another.
  const problems = (named) => {
    const found = [];
    named.forEach(([name, box], i) => {
      if (box.x1 < MARGIN - 0.1 || box.x2 > view.width - MARGIN + 0.1 || box.y1 < MARGIN - 0.1 || box.y2 > view.height - MARGIN + 0.1) {
        found.push(`${name} [${box.x1.toFixed(1)}..${box.x2.toFixed(1)} x ${box.y1.toFixed(1)}..${box.y2.toFixed(1)}] leaves the view`);
      }
      for (let j = 0; j < i; j++) {
        if (meet(box, named[j][1])) { found.push(`${name} covers ${named[j][0]}`); }
      }
    });
    return found;
  };
  // A hub with `first` direct neighbours (alternating directions) and `second`
  // concepts two hops away (all through the first ones), `ghosts` absent
  // targets, the ids' last segments `length` characters long (the cut is at 24).
  const star = (first, second, ghosts, length) => {
    const name = (prefix, k) => `p2-local/${prefix}${String(k).padStart(2, "0")}${"s".repeat(Math.max(0, length - 3))}`;
    const ids = ["p2-local/hub"];
    const links = [];
    for (let k = 0; k < first; k++) { ids.push(name("n", k)); links.push(k % 2 ? [k + 1, 0] : [0, k + 1]); }
    for (let k = 0; k < second; k++) { ids.push(name("f", k)); links.push([1 + (k % first), first + 1 + k]); }
    const absent = [];
    for (let g = 0; g < ghosts; g++) { absent.push(name("g", g)); links.push([0, g, "ghost"]); }
    return siteIndex(ids, links, absent);
  };
  const served = (index) => ({ override: { "assets/okf-index.js": `window.OKF_INDEX = ${JSON.stringify(index)};` } });
  const drawnLabels = (doc) => Array.from(doc.querySelectorAll("#okf-local-graph svg text.okf-local-label"), (t, k) => [
    `label ${k} "${t.textContent}"`,
    boxOf(t.textContent, Number(t.getAttribute("x")), Number(t.getAttribute("y")), t.getAttribute("text-anchor"), t.classList.contains("okf-local-label-center")),
  ]);

  checkAsync("local graph: no label leaves the drawing or covers another, 1 and 2 hops, ghosts and long ids included", async () => {
    const pages = [
      ["the fixture concept, 2 hops", "p2-local/c.html", null, 2],
      ["the fixture hub with its absent concepts", "p2-local/hub.html", null, 1],
      ["8 direct neighbours, 2 absent, 2 hops", "p2-local/hub.html", star(8, 0, 2, 12), 2],
      ["5 direct and 5 second-hop, 1 absent, 2 hops", "p2-local/hub.html", star(5, 5, 1, 19), 2],
      ["10 direct neighbours with 40-character ids", "p2-local/hub.html", star(10, 0, 0, 40), 1],
      ["4 direct and 6 second-hop with 40-character ids", "p2-local/hub.html", star(4, 6, 0, 40), 2],
      ["6 direct, 1 absent with a 40-character id, 1 hop", "p2-local/hub.html", star(6, 0, 1, 40), 1],
    ];
    for (const [what, page, index, hops] of pages) {
      const window = await openPage(page, index ? served(index) : {});
      if (hops === 2) { window.document.getElementById("okf-local-hops-2").click(); }
      const labels = drawnLabels(window.document);
      assert(labels.length >= 3, `${what}: only ${labels.length} labels drawn`);
      const found = problems(labels);
      assert(found.length === 0, `${what}: ${found.slice(0, 3).join("; ")}`);
    }
  });

  check("local graph: labels() keeps every label in the view and apart, for every neighbourhood of up to 10 neighbours", () => {
    const { OkfLocal } = okfLocal();
    let layouts = 0;
    const sweep = (first, second, ghosts, length) => {
      const index = star(first, second, ghosts, length);
      const r = OkfLocal.build(index, 0, second > 0 ? 2 : 1);
      const C = index.concepts.length;
      const texts = r.nodes.map((n) => OkfLocal.lastSegment(n.key < C ? index.concepts[n.key].id : index.ghosts[n.key - C].id));
      const halves = r.nodes.map((n) => (n.dist === 0 ? 20 : n.key >= C ? 10.6 : 12.75));
      const placed = OkfLocal.labels(r.nodes, texts, halves);
      const found = problems(placed.map((p, k) => [`label ${k} "${p.text}"`, boxOf(p.text, p.x, p.y, p.anchor, k === 0)]));
      assert(found.length === 0, `${first} + ${second} neighbours, ${ghosts} absent, ids of ${length}: ${found.slice(0, 3).join("; ")}`);
      layouts++;
    };
    for (const length of [4, 12, 19, 24, 60]) {
      for (let first = 1; first <= 10; first++) {
        for (let ghosts = 0; ghosts <= 2 && first + ghosts <= 10; ghosts++) { sweep(first, 0, ghosts, length); }
        for (let second = 1; first + second <= 10; second++) { sweep(first, second, 0, length); }
      }
    }
    assert(layouts > 300, `${layouts} layouts swept`);
  });

  check("local graph: labels() is the mockup place when nothing collides: centred, baseline 14 under the shape", () => {
    const { OkfLocal } = okfLocal();
    const r = OkfLocal.build(siteIndex(["c", "a", "b", "d", "e"], [[0, 1], [0, 2], [3, 0], [4, 0]]), 0, 1);
    const placed = OkfLocal.labels(r.nodes, ["c", "a", "b", "d", "e"], r.nodes.map((n) => (n.dist === 0 ? 20 : 12)));
    placed.forEach((p, k) => {
      assert(p.x === r.nodes[k].x && p.anchor === "middle" && p.y === r.nodes[k].y + (k === 0 ? 20 : 12) + 14 && !p.truncated,
        `label ${k}: (${p.x}, ${p.y}) ${p.anchor} for a node at (${r.nodes[k].x}, ${r.nodes[k].y})`);
    });
  });

  check("local graph: labels() cuts a text over 24 characters to 23 and an ellipsis, and keeps a shorter one whole", () => {
    const { OkfLocal } = okfLocal();
    const r = OkfLocal.build(siteIndex(["c", "a", "b"], [[0, 1], [0, 2]]), 0, 1);
    const long = "x".repeat(60);
    const exact = "y".repeat(24);
    const placed = OkfLocal.labels(r.nodes, ["c", long, exact], [20, 12, 12]);
    const ellipsis = String.fromCharCode(0x2026);
    assert(placed[1].text === "x".repeat(23) + ellipsis && placed[1].truncated === true, `cut: ${placed[1].text}`);
    assert(placed[2].text === exact && placed[2].truncated === false, `24 characters are kept: ${placed[2].text}`);
    assert(placed[0].text === "c" && placed[0].truncated === false, "a short text changed");
  });

  check("local graph: labels() at the cap is deterministic, never throws, and still keeps every label in the view", () => {
    const { OkfLocal } = okfLocal();
    const other = okfLocal().OkfLocal;
    const ids = ["hub"];
    const links = [];
    for (let k = 1; k <= 300; k++) { ids.push(`s${k}-${"q".repeat(k % 30)}`); links.push(k % 2 ? [0, k] : [k, 0]); }
    // Mixed lengths, then 39 labels of 24 characters: more than 20 rows can hold, so some
    // fall back to their own place (clamped), and may overlap (A11) but never leave the view.
    for (const [hops, every] of [[1, null], [2, null], [1, "z".repeat(40)], [2, "z".repeat(40)]]) {
      const r = OkfLocal.build(siteIndex(ids, links), 0, hops);
      const texts = r.nodes.map((n) => every || ids[n.key]);
      const halves = r.nodes.map((n) => (n.dist === 0 ? 20 : 12));
      const placed = OkfLocal.labels(r.nodes, texts, halves);
      assert(r.nodes.length === OkfLocal.CAP && placed.length === OkfLocal.CAP, `hops ${hops}: ${placed.length} labels`);
      assert(JSON.stringify(placed) === JSON.stringify(OkfLocal.labels(r.nodes, texts, halves)), `hops ${hops}: two layouts in one window differ`);
      assert(JSON.stringify(placed) === JSON.stringify(other.labels(r.nodes, texts, halves)), `hops ${hops}: two windows disagree`);
      placed.forEach((p, k) => {
        const b = boxOf(p.text, p.x, p.y, p.anchor, k === 0);
        assert(b.x1 >= MARGIN - 0.1 && b.x2 <= view.width - MARGIN + 0.1 && b.y1 >= MARGIN - 0.1 && b.y2 <= view.height - MARGIN + 0.1,
          `hops ${hops}: label "${p.text}" at (${p.x}, ${p.y}) ${p.anchor} leaves the view`);
      });
    }
  });

  check("local graph: labels() at the cap with two rings (20 + 19) keeps every label in the view, the bottom of the outer ring included", () => {
    const { OkfLocal } = okfLocal();
    const index = star(20, 19, 0, 40);
    const r = OkfLocal.build(index, 0, 2);
    assert(r.nodes.length === OkfLocal.CAP && r.nodes.some((n) => n.dist === 2), "this case needs both rings at the cap");
    const texts = r.nodes.map(() => "z".repeat(40));
    const placed = OkfLocal.labels(r.nodes, texts, r.nodes.map((n) => (n.dist === 0 ? 20 : 12)));
    placed.forEach((p, k) => {
      const b = boxOf(p.text, p.x, p.y, p.anchor, k === 0);
      assert(b.x1 >= MARGIN - 0.1 && b.x2 <= view.width - MARGIN + 0.1 && b.y1 >= MARGIN - 0.1 && b.y2 <= view.height - MARGIN + 0.1,
        `label ${k} at (${p.x}, ${p.y}) ${p.anchor} leaves the view`);
    });
  });

  check("local graph: labels() refuses mismatched or non-finite input with a TypeError", () => {
    const window = okfLocal();
    const nodes = [{ x: 149, y: 118, dist: 0, key: 0 }];
    for (const args of [[nodes, [], [1]], [nodes, ["a"], []], [nodes, ["a"], [NaN]], [[{ x: Infinity, y: 0 }], ["a"], [1]], [[{ x: 1, y: 1 }], ["a"], [-1]], [null, [], []]]) {
      let threw = null;
      try { window.OkfLocal.labels(...args); } catch (e) { threw = e; }
      assert(threw instanceof window.TypeError, `labels(${JSON.stringify(args)}) did not throw a TypeError`);
    }
  });
}

function register(h) {
  registerPure(h);
  registerPage(h);
  registerList(h);
  registerListPins(h);
  registerChrome(h);
  registerLabels(h);
}

module.exports = { register };
