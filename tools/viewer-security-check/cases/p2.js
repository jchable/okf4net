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

  checkAsync("local graph: shapes are OkfShapes nodes at the local and localCenter sizes, labels under them", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    doc.getElementById("okf-local-hops-2").click();
    const S = window.OkfShapes;
    const index = window.OKF_INDEX;
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
      assert(label.getAttribute("text-anchor") === "middle", `node ${k}: the label is not centred`);
      // X7: baseline at cy + size / 2 + 14 (centre: + 6 more, under its square).
      const x = Number(label.getAttribute("x"));
      const y = Math.round((Number(label.getAttribute("y")) - size.size / 2 - (centre ? 6 : 0) - 14) * 100) / 100;
      const reference = S.node(kind, x, y, size.size, size);
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
      assert(labels.includes(evil), `no label reads the literal markup: ${JSON.stringify(labels)}`);
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

function register(h) {
  registerPure(h);
  registerPage(h);
}

module.exports = { register };
