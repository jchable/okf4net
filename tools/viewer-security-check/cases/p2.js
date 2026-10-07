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
    const r = OkfLocal.build(index, 0, 2);
    assert(r.total === 1 && r.edges.length === 1 && r.edges[0].count === 1, JSON.stringify(r));
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
    const r = OkfLocal.build(index, 0, 2);
    const bound = 4 * index.edges.length + 2 * n;
    assert(r.work > 0 && r.work <= bound,
      `${r.work} units of work for ${index.edges.length} edges and ${n} nodes (bound ${bound}; one scan of the edges per first-hop node would be ~${600 * index.edges.length})`);
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
}

function register(h) {
  registerPure(h);
}

module.exports = { register };
