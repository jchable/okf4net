// SPDX-License-Identifier: LGPL-3.0-or-later
//
// P3 (global graph) cases for tools/viewer-security-check (spec
// docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md, §7
// controls 1, 2, 3, 7, 9, 13, §12.5, §12.7). run.js calls register(h) with its
// frozen helpers. `node cases/p3.js` runs the simulation cases alone, without
// run.js, so the simulation can be developed before the page exists.
"use strict";
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const ASSETS = path.join(__dirname, "..", "..", "..", "src", "OKF4net.Viewer", "Assets");
const SIM_FILE = path.join(ASSETS, "okf-sim.js");

// Never type a backslash-u escape into this file through an editor tool (it is
// decoded on write): the cases that need one build it at run time.
const BS = String.fromCharCode(92);

// --- simulation (okf-sim.js) ------------------------------------------------

// The exact Math functions §4.2 allows; everything else is approximated per
// engine and would break cross-engine determinism.
const ALLOWED_MATH = ["sqrt", "floor", "ceil", "trunc", "abs", "min", "max", "imul"];

// Globals deleted from the sandbox's context: clocks, random or GC-dependent
// sources, locale/date formatting, shared-memory timers, dynamic code.
const REMOVED_GLOBALS = ["Date", "eval", "Function", "WeakRef", "FinalizationRegistry", "Intl", "SharedArrayBuffer",
  "Atomics", "crypto", "queueMicrotask", "performance", "setTimeout", "setInterval", "setImmediate",
  "requestAnimationFrame"];

// Runs INSIDE the sandbox context, before the module: builds the restricted
// Math from the context's OWN Math (a Math copied from the host realm would
// hand the module the host's Function constructor through
// Math.sqrt.constructor), makes it the global Math, poisons every function
// constructor reachable from a function value (Function, async, generator
// and async generator), then deletes the clock/random/dynamic-code globals.
// It returns the objects the module is given, and `adopt`: the module's
// public object re-exported so that every argument of create() is first
// copied into THIS realm. Without it the host's graph, edge arrays and options
// would reach the module, and graph['con'+'structor']['con'+'structor'] is the
// host's own, unpoisoned Function constructor. A value already built in the
// context (a plain Array or Object of this realm, e.g. from inCtx) passes
// through untouched, so a case can still hand over a getter or a huge sparse
// array that must not be cloned.
const PRELUDE = "(function () {\n"
  + "  var allowed = " + JSON.stringify(ALLOWED_MATH) + ";\n"
  + "  var removed = " + JSON.stringify(REMOVED_GLOBALS) + ";\n"
  + "  var realMath = Math;\n"
  + "  var restricted = {};\n"
  + "  for (var i = 0; i < allowed.length; i++) { restricted[allowed[i]] = realMath[allowed[i]]; }\n"
  + "  Object.freeze(restricted);\n"
  + "  function blocked() { throw new TypeError('sandbox: dynamic code construction is blocked'); }\n"
  + "  var protos = [Object.getPrototypeOf(function () {}), Object.getPrototypeOf(async function () {}),\n"
  + "    Object.getPrototypeOf(function* () {}), Object.getPrototypeOf(async function* () {})];\n"
  + "  for (var p = 0; p < protos.length; p++) {\n"
  + "    Object.defineProperty(protos[p], 'constructor', { value: blocked, writable: false, configurable: false });\n"
  + "  }\n"
  + "  Object.defineProperty(globalThis, 'Math', { value: restricted, writable: false, configurable: false });\n"
  + "  for (var r = 0; r < removed.length; r++) { try { delete globalThis[removed[r]]; } catch (e) { /* none */ } }\n"
  + "  function cloneIn(v) {\n"
  + "    if (v === null || (typeof v !== 'object' && typeof v !== 'function')) { return v; }\n"
  + "    if (typeof v === 'function') { throw new TypeError('sandbox: a function cannot cross into the sandbox'); }\n"
  + "    var proto = Object.getPrototypeOf(v);\n"
  + "    if (Array.isArray(v)) {\n"
  + "      if (proto === Array.prototype) { return v; }\n"
  + "      var a = [];\n"
  + "      for (var i2 = 0; i2 < v.length; i2++) { a.push(cloneIn(v[i2])); }\n"
  + "      return a;\n"
  + "    }\n"
  + "    if (proto === Object.prototype) { return v; }\n"
  + "    var o = {};\n"
  + "    var keys = Object.keys(v);\n"
  + "    for (var j2 = 0; j2 < keys.length; j2++) { o[keys[j2]] = cloneIn(v[keys[j2]]); }\n"
  + "    return o;\n"
  + "  }\n"
  + "  function adopt(real) {\n"
  + "    if (real === null || typeof real !== 'object' || typeof real.create !== 'function') { return real; }\n"
  + "    var create = function (graph, options) { return real.create(cloneIn(graph), cloneIn(options)); };\n"
  + "    return Object.freeze({ NODE_LIMIT: real.NODE_LIMIT, create: create });\n"
  + "  }\n"
  + "  return { win: {}, math: restricted, adopt: adopt };\n"
  + "})()";

// The ONE way this file runs okf-sim.js (or a variant of it): a fresh V8
// context prepared by PRELUDE, then the module as the body of a STRICT
// function whose parameters shadow every ambient name it must not use --
// Math holds only the allowed functions (any other call throws), and
// document, Date, performance, requestAnimationFrame, setTimeout, self and
// globalThis are undefined; `this` is undefined at the module's top level.
// The module still sees its own `window`, created inside the context.
// Returns the window the module wrote to (window.OkfSim -- adopted, see
// PRELUDE -- and whatever a fault-injection variant records on it). Every
// OkfSim returned is remembered with its context so inCtx() can build inputs,
// and run probes, INSIDE that same realm.
const CONTEXT_OF = new WeakMap();

function sandboxLoad(source) {
  const context = vm.createContext({});
  const env = vm.runInContext(PRELUDE, context, { filename: "okf-sim-sandbox-prelude.js" });
  const wrapper = vm.runInContext(
    "(function (window, Math, document, Date, performance, requestAnimationFrame, setTimeout, self, globalThis) {\"use strict\";\n"
      + source + "\n})",
    context,
    { filename: SIM_FILE });
  wrapper(env.win, env.math);
  if (env.win.OkfSim !== undefined) {
    env.win.OkfSim = env.adopt(env.win.OkfSim);
    if (env.win.OkfSim !== null && typeof env.win.OkfSim === "object") { CONTEXT_OF.set(env.win.OkfSim, context); }
  }
  return env.win;
}

// Evaluates `source` inside the realm an OkfSim (from sandboxLoad/loadSim)
// lives in, and returns the result. It refuses an object that did not come out
// of sandboxLoad, so a loader that bypassed the sandbox cannot use it.
function inCtx(OkfSim, source) {
  const context = CONTEXT_OF.get(OkfSim);
  if (context === undefined) { throw new Error("inCtx: this OkfSim was not loaded through sandboxLoad()"); }
  return vm.runInContext(source, context, { filename: "okf-sim-case-input.js" });
}

function loadSim() {
  return sandboxLoad(fs.readFileSync(SIM_FILE, "utf8")).OkfSim;
}

// Fault injection: okf-sim.js with each [from, to] replaced exactly once, run
// in the same sandbox. A pattern that no longer matches exactly once fails
// loudly (the case must be updated with the module, never silently skipped).
function patchedWindow(patches) {
  let source = fs.readFileSync(SIM_FILE, "utf8");
  for (const [from, to] of patches) {
    if (source.split(from).length !== 2) { throw new Error(`fault-injection pattern does not match exactly once: ${from}`); }
    source = source.replace(from, () => to);
  }
  return sandboxLoad(source);
}

// Deterministic edge lists for the shapes control 1 names (the harness's own
// generator, independent of the module's).
function pseudoRandomEdges(n, count, seed) {
  const edges = [];
  const seen = new Set();
  let state = seed >>> 0;
  const next = () => { state = (Math.imul(state, 1103515245) + 12345) >>> 0; return state; };
  while (edges.length < count) {
    const s = next() % n;
    const t = next() % n;
    const key = s * n + t;
    if (s !== t && !seen.has(key)) { seen.add(key); edges.push([s, t]); }
  }
  return edges;
}

function path_(n) { const e = []; for (let i = 1; i < n; i++) { e.push([i - 1, i]); } return e; }
function complete(n) { const e = []; for (let i = 0; i < n; i++) { for (let j = 0; j < n; j++) { if (i !== j) { e.push([i, j]); } } } return e; }
function completeOnce(n) { const e = []; for (let i = 0; i < n; i++) { for (let j = i + 1; j < n; j++) { e.push([i, j]); } } return e; }
function star(leaves) { const e = []; for (let i = 1; i <= leaves; i++) { e.push([0, i]); } return { nodeCount: leaves + 1, edges: e }; }

// Shapes of control 1 (spec §7): empty, singleton, edgeless, clumped,
// repeated edges, two components, and a large graph at the node limit; plus a
// star (a hub with a crowd around it) and a dense graph that pile nodes into
// crowded cells.
function shapes(OkfSim) {
  return {
    empty: { nodeCount: 0, edges: [] },
    singleton: { nodeCount: 1, edges: [] },
    edgeless: { nodeCount: 50, edges: [] },
    clumped: { nodeCount: 30, edges: complete(30) },
    repeated: { nodeCount: 3, edges: Array.from({ length: 50 }, () => [0, 1]).concat([[1, 0], [1, 2]]) },
    components: { nodeCount: 9, edges: [[0, 1], [1, 2], [2, 0], [3, 4], [4, 5]] },
    path: { nodeCount: 40, edges: path_(40) },
    star: star(1000),
    dense: { nodeCount: 200, edges: completeOnce(200) },
    large: { nodeCount: OkfSim.NODE_LIMIT, edges: pseudoRandomEdges(OkfSim.NODE_LIMIT, 3 * OkfSim.NODE_LIMIT, 7) },
  };
}

// The clamp okf-sim.js applies: SPACING (60) x (ceil(sqrt(n)) + 2) x 4,
// written out here as the expected bound, not read from the module.
function bound(n) { return 60 * (Math.ceil(Math.sqrt(n)) + 2) * 4; }

// Steps a simulation to its end, checking every slice's work against the
// budget; a step that is not the last must do some work (no stall).
function runToEnd(h, simulation, sliceWork, label) {
  const steps = [];
  for (let k = 0; k < 50000000; k++) {
    const r = simulation.step();
    h.assert(r.work >= 0 && r.work <= sliceWork, `${label}: a slice did ${r.work} units for a budget of ${sliceWork}`);
    steps.push(r);
    if (r.done) { return steps; }
    h.assert(r.work > 0, `${label}: a step that is not the last did no work (stall)`);
  }
  throw new Error(`${label}: never finished`);
}

// The same checks without keeping the steps (for millions of tiny slices).
function drain(h, simulation, sliceWork, label) {
  for (let k = 0; k < 50000000; k++) {
    const r = simulation.step();
    h.assert(r.work >= 0 && r.work <= sliceWork, `${label}: a slice did ${r.work} units for a budget of ${sliceWork}`);
    if (r.done) { return k + 1; }
    h.assert(r.work > 0, `${label}: a step that is not the last did no work (stall)`);
  }
  throw new Error(`${label}: never finished`);
}

function assertFiniteAndBounded(h, positions, n, label) {
  h.assert(positions.length === 2 * n, `${label}: ${positions.length} coordinates for ${n} nodes`);
  for (let k = 0; k < positions.length; k++) {
    h.assert(Number.isFinite(positions[k]), `${label}: coordinate ${k} is ${positions[k]}`);
    h.assert(Math.abs(positions[k]) <= bound(n), `${label}: coordinate ${k} = ${positions[k]} is outside +-${bound(n)}`);
  }
}

function sameBits(a, b) {
  if (a.length !== b.length) { return false; }
  for (let k = 0; k < a.length; k++) { if (!Object.is(a[k], b[k])) { return false; } }
  return true;
}

// FNV-1a over the bytes of the coordinates: a compact snapshot of a layout.
function layoutHash(positions) {
  const bytes = new Uint8Array(Float64Array.from(positions).buffer);
  let hash = 2166136261;
  for (let k = 0; k < bytes.length; k++) { hash = Math.imul(hash ^ bytes[k], 16777619) >>> 0; }
  return hash.toString(16);
}

function minPairDistance(p, n) {
  let best = Infinity;
  for (let i = 0; i < n; i++) {
    for (let j = i + 1; j < n; j++) {
      const d = Math.sqrt((p[2 * i] - p[2 * j]) ** 2 + (p[2 * i + 1] - p[2 * j + 1]) ** 2);
      if (d < best) { best = d; }
    }
  }
  return best;
}

// An independent model of the simulation for graphs so small that every pair
// of nodes within the repulsion cut-off shares a 3 x 3 cell neighbourhood and
// the per-cell cap never binds: all pairs, no grid. It restates the force law
// of spec §4.2 (repulsion K2/d2 inside 2 x SPACING, springs d^2/SPACING,
// gravity 0.02, step capped by a linearly cooling temperature, clamp), and
// counts the units the module must count: the candidate pairs of a 3 x 3
// neighbourhood in a grid of 180 (revision 11 widened it from 2 x SPACING, so
// that two overlapping boxes always share one). Without boxes, as here, there
// is no collision and no closing fifth. Summation order differs from the
// module's, so positions are compared to a tolerance, work exactly.
function referenceRun(initial, n, edges, maxIterations) {
  const SPACING = 60;
  const CELL = 180;
  const limit = bound(n);
  let p = Float64Array.from(initial);
  let work = 0;
  for (let k = 0; k < maxIterations; k++) {
    const temperature = SPACING * (1 - k / maxIterations);
    const fx = new Float64Array(n);
    const fy = new Float64Array(n);
    for (let i = 0; i < n; i++) {
      for (let j = 0; j < n; j++) {
        const near = Math.abs(Math.floor(p[2 * i] / CELL) - Math.floor(p[2 * j] / CELL)) <= 1
          && Math.abs(Math.floor(p[2 * i + 1] / CELL) - Math.floor(p[2 * j + 1] / CELL)) <= 1;
        if (!near) { continue; }
        work++;
        if (i === j) { continue; }
        const dx = p[2 * i] - p[2 * j];
        const dy = p[2 * i + 1] - p[2 * j + 1];
        const d2 = dx * dx + dy * dy;
        if (d2 < 4 * SPACING * SPACING) { fx[i] += dx * SPACING * SPACING / d2; fy[i] += dy * SPACING * SPACING / d2; }
      }
    }
    for (const [s, t] of edges) {
      work++;
      const ex = p[2 * t] - p[2 * s];
      const ey = p[2 * t + 1] - p[2 * s + 1];
      const pull = Math.sqrt(ex * ex + ey * ey) / SPACING;
      fx[s] += ex * pull; fy[s] += ey * pull;
      fx[t] -= ex * pull; fy[t] -= ey * pull;
    }
    const next = new Float64Array(2 * n);
    for (let i = 0; i < n; i++) {
      work++;
      const x = p[2 * i];
      const y = p[2 * i + 1];
      const vx = fx[i] - 0.02 * x;
      const vy = fy[i] - 0.02 * y;
      const m = Math.sqrt(vx * vx + vy * vy);
      let nx = x;
      let ny = y;
      if (m > 0) { const move = Math.min(m, temperature); nx = x + vx / m * move; ny = y + vy / m * move; }
      next[2 * i] = Math.max(-limit, Math.min(limit, nx));
      next[2 * i + 1] = Math.max(-limit, Math.min(limit, ny));
    }
    p = next;
  }
  return { positions: p, work };
}

function registerSim(h) {
  console.log("\nGlobal graph simulation (okf-sim.js, P3):");

  h.check("sim: create returns null above NODE_LIMIT and a simulation at it", () => {
    const OkfSim = loadSim();
    h.assert(OkfSim.NODE_LIMIT === 1500, `NODE_LIMIT is ${OkfSim.NODE_LIMIT}, spec §12.5 records 1500`);
    h.assert(OkfSim.create({ nodeCount: OkfSim.NODE_LIMIT + 1, edges: [] }) === null, "NODE_LIMIT + 1 nodes were accepted");
    h.assert(OkfSim.create({ nodeCount: 5000, edges: [] }) === null, "5000 nodes were accepted");
    const at = OkfSim.create({ nodeCount: OkfSim.NODE_LIMIT, edges: [] }, { maxIterations: 1 });
    h.assert(at && typeof at.step === "function" && typeof at.positions === "function"
      && typeof at.stats === "function" && typeof at.cancel === "function", "no simulation at NODE_LIMIT");
    h.assert(Object.isFrozen(OkfSim) && Object.isFrozen(at), "OkfSim or a simulation is not frozen");
  });

  h.check("sim: malformed input is a TypeError, never a silent layout", () => {
    const OkfSim = loadSim();
    // Built INSIDE the sandbox's realm (a host copy would be cloned element by element).
    const sparseHuge = inCtx(OkfSim, "new Array(4294967295)");
    const sparseBig = inCtx(OkfSim, "new Array(1000000000)");
    const bad = [
      [null, undefined], [{ nodeCount: -1, edges: [] }, undefined], [{ nodeCount: 1.5, edges: [] }, undefined],
      [{ nodeCount: "3", edges: [] }, undefined], [{ nodeCount: 3 }, undefined],
      [{ nodeCount: 3, edges: [[0, 3]] }, undefined], [{ nodeCount: 3, edges: [[1, 1]] }, undefined],
      [{ nodeCount: 3, edges: [[0]] }, undefined], [{ nodeCount: 3, edges: [["0", 1]] }, undefined],
      [{ nodeCount: 3, edges: [[0, 1, 2]] }, undefined], [{ nodeCount: 3, edges: [[0, NaN]] }, undefined],
      [{ nodeCount: 3, edges: [[0, 1.5]] }, undefined], [{ nodeCount: 3, edges: [[-1, 0]] }, undefined],
      [{ nodeCount: 3, edges: [null] }, undefined], [{ nodeCount: 3, edges: "01" }, undefined],
      [{ nodeCount: 3, edges: [] }, { sliceWork: 0 }], [{ nodeCount: 3, edges: [] }, { maxIterations: 1.5 }],
      [{ nodeCount: 3, edges: [] }, { cellCap: -2 }], [{ nodeCount: 3, edges: [] }, "fast"],
      // An unknown option key is a mistake (a typo would silently keep the default).
      [{ nodeCount: 3, edges: [] }, { maxIteration: 5 }], [{ nodeCount: 3, edges: [] }, { sliceWork: 10, cellcap: 4 }],
      // A huge sparse edge list must be refused by its first hole, not allocated for.
      [{ nodeCount: 3, edges: sparseHuge }, undefined],
      [{ nodeCount: 3, edges: sparseBig }, undefined],
    ];
    for (const [graph, options] of bad) {
      let threw = null;
      try { OkfSim.create(graph, options); } catch (e) { threw = e; }
      let shown = "";
      if (!threw || threw.name !== "TypeError") {
        try { shown = `${JSON.stringify(graph)}, ${JSON.stringify(options)}`; } catch (e) { shown = "(unprintable)"; }
      }
      h.assert(threw && threw.name === "TypeError", `create(${shown}) did not throw a TypeError (${threw && threw.name})`);
    }
    // Documented order: graph, nodeCount, edges array and options are checked
    // first; above NODE_LIMIT the call then returns null without examining
    // the edge entries.
    h.assert(OkfSim.create({ nodeCount: 5000, edges: [[0, 9999], null] }) === null, "above NODE_LIMIT the edge entries were examined");
    h.assert(OkfSim.create({ nodeCount: 5000, edges: sparseHuge }) === null, "a 5000-node graph with a sparse edge list was not refused with null");
    let threw = null;
    try { OkfSim.create({ nodeCount: 5000, edges: [] }, { sliceWork: 0 }); } catch (e) { threw = e; }
    h.assert(threw && threw.name === "TypeError", "bad options were not rejected above NODE_LIMIT");
  });

  h.check("sim: an edge is read once -- a getter that changes its answer cannot smuggle in an out-of-range node", () => {
    const OkfSim = loadSim();
    // The edge (and its counting getter) is built inside the sandbox's realm.
    const probe = inCtx(OkfSim, "(function () { var e = []; var reads = 0;"
      + " Object.defineProperty(e, '0', { get: function () { reads++; return reads === 1 ? 0 : 7; }, enumerable: true });"
      + " e[1] = 1; return { edge: e, reads: function () { return reads; } }; })()");
    const s = OkfSim.create({ nodeCount: 3, edges: [probe.edge] }, { maxIterations: 4 });
    h.assert(s !== null, "a valid edge (as first read) was refused");
    runToEnd(h, s, 100000, "toctou");
    assertFiniteAndBounded(h, s.positions(), 3, "toctou");
    h.assert(probe.reads() === 1, `the edge's first field was read ${probe.reads()} times`);
  });

  h.check("sim: empty and single-node graphs are done without iterating", () => {
    const OkfSim = loadSim();
    for (const n of [0, 1]) {
      const s = OkfSim.create({ nodeCount: n, edges: [] });
      const r = s.step();
      h.assert(r.done === true && r.iterations === 0 && r.work === 0, `${n} node(s): ${JSON.stringify(r)}`);
      assertFiniteAndBounded(h, s.positions(), n, `${n} node(s)`);
    }
  });

  h.check("sim: the initial layout is a jittered grid in index order; the seed depends on node and edge counts", () => {
    const OkfSim = loadSim();
    const n = 10;
    const p = OkfSim.create({ nodeCount: n, edges: [] }).positions();
    const side = Math.ceil(Math.sqrt(n));
    const half = (side - 1) / 2;
    for (let i = 0; i < n; i++) {
      const gx = (i % side - half) * 60;
      const gy = (Math.floor(i / side) - half) * 60;
      h.assert(Math.abs(p[2 * i] - gx) <= 15 && Math.abs(p[2 * i + 1] - gy) <= 15,
        `node ${i} starts at (${p[2 * i]}, ${p[2 * i + 1]}), not within the jitter of grid cell (${gx}, ${gy})`);
      // The jitter is real: a layout that sits exactly on the grid has none.
      h.assert(p[2 * i] !== gx && p[2 * i + 1] !== gy, `node ${i} sits exactly on its grid cell (no jitter)`);
    }
    const withEdge = OkfSim.create({ nodeCount: n, edges: [[0, 1]] }).positions();
    h.assert(!sameBits(p, withEdge), "the jitter does not depend on the edge count");
    // Ten and twelve nodes share a 4 x 4 grid: only the seed (node count) can
    // make the first ten nodes start differently.
    const twelve = OkfSim.create({ nodeCount: 12, edges: [] }).positions();
    h.assert(!sameBits(p, twelve.slice(0, 20)), "the jitter does not depend on the node count");
    h.assert(sameBits(p, OkfSim.create({ nodeCount: n, edges: [] }).positions()), "the initial layout is not repeatable");
  });

  h.check("sim: every shape stays finite and bounded, iterations within the bound, slices within budget", () => {
    const OkfSim = loadSim();
    const all = shapes(OkfSim);
    for (const name of Object.keys(all)) {
      const graph = all[name];
      const maxIterations = (name === "large" || name === "star" || name === "dense") ? 3 : 60;
      const sliceWork = 5000;
      const s = OkfSim.create(graph, { maxIterations, sliceWork, cellCap: 24 });
      const steps = runToEnd(h, s, sliceWork, name);
      const stats = s.stats();
      const expected = graph.nodeCount <= 1 ? 0 : maxIterations;
      h.assert(stats.iterations === expected, `${name}: ${stats.iterations} iterations, expected ${expected}`);
      h.assert(stats.maxSliceWork <= sliceWork, `${name}: maxSliceWork ${stats.maxSliceWork} > ${sliceWork}`);
      const summed = steps.reduce((a, r) => a + r.work, 0);
      h.assert(summed === stats.totalWork, `${name}: steps did ${summed} units, stats say ${stats.totalWork}`);
      assertFiniteAndBounded(h, s.positions(), graph.nodeCount, name);
    }
  });

  // The per-iteration work of a graph, averaged over `iterations` iterations.
  function perIteration(OkfSim, graph, cellCap, iterations) {
    const s = OkfSim.create(graph, { maxIterations: iterations, sliceWork: 1000000000, cellCap });
    s.step();
    return s.stats().totalWork / iterations;
  }

  h.check("sim: work is counted per candidate pair visited, and the per-cell cap really caps it", () => {
    const OkfSim = loadSim();
    const all = shapes(OkfSim);
    // Two crowds: 30 nodes pulled into one clump by a complete graph, and a
    // 1000-leaf star. For each, a ceiling that holds only WITH the cap
    // (n * 9 cells * cellCap pairs + one unit per edge + one per node) and a
    // check that the same graph WITHOUT it (cellCap far above any cell) goes
    // over that ceiling -- so the case still tests the cap, not just a bound.
    for (const [name, cellCap] of [["clumped", 1], ["clumped", 2], ["star", 1], ["star", 4]]) {
      const graph = all[name];
      const n = graph.nodeCount;
      const ceiling = n * 9 * cellCap + graph.edges.length + n;
      const capped = perIteration(OkfSim, graph, cellCap, 40);
      const uncapped = perIteration(OkfSim, graph, 1000000000, 40);
      h.assert(capped <= ceiling, `${name}/cap ${cellCap}: ${capped} units per iteration exceeds n*9*cap + edges + n = ${ceiling}`);
      h.assert(uncapped > ceiling, `${name}/cap ${cellCap}: uncapped work ${uncapped} does not exceed ${ceiling}; the case no longer proves the cap`);
      // Every node visits at least itself in its own cell, every spring and
      // every integration counts: a floor that a counter skipping pairs breaks.
      h.assert(capped >= n + graph.edges.length + n, `${name}/cap ${cellCap}: ${capped} units per iteration: pairs or springs are not counted`);
    }
  });

  h.check("sim: exact work for a hand-derived graph (pairs, springs and integrations are each counted)", () => {
    const OkfSim = loadSim();
    // Two nodes start at about (-30,-30) and (30,-30), jittered by at most 15:
    // always in the adjacent cells (-1,-1) and (0,-1) of the 180-wide grid, so
    // each node visits itself and the other = 4 pair units per iteration,
    // plus one unit per edge, plus 2 integrations.
    const cases = [
      { edges: [], work: 4 + 0 + 2 },
      { edges: [[0, 1]], work: 4 + 1 + 2 },
      { edges: [[0, 1], [1, 0], [0, 1]], work: 4 + 3 + 2 },
    ];
    for (const c of cases) {
      const s = OkfSim.create({ nodeCount: 2, edges: c.edges }, { maxIterations: 1, sliceWork: 1000 });
      const r = s.step();
      h.assert(r.done && r.work === c.work && s.stats().totalWork === c.work,
        `${c.edges.length} edge(s): ${JSON.stringify(r)}, expected ${c.work} units`);
    }
  });

  h.check("sim: an iteration costlier than a slice suspends inside it and resumes within budget", () => {
    const OkfSim = loadSim();
    const graph = shapes(OkfSim).large;
    const sliceWork = 5000;
    const s = OkfSim.create(graph, { maxIterations: 2, sliceWork });
    const steps = runToEnd(h, s, sliceWork, "large");
    h.assert(s.stats().totalWork / 2 > sliceWork, "one iteration fits a slice: this case no longer tests suspension");
    let midIteration = 0;
    for (let k = 1; k < steps.length; k++) {
      if (steps[k].iterations === steps[k - 1].iterations && steps[k].work > 0) { midIteration++; }
    }
    h.assert(midIteration > 0, "no slice ended inside an iteration");
  });

  h.check("sim: a slice budget of one unit still finishes, with identical positions", () => {
    const OkfSim = loadSim();
    const graph = shapes(OkfSim).components;
    const fine = OkfSim.create(graph, { maxIterations: 20, sliceWork: 1 });
    const steps = runToEnd(h, fine, 1, "sliceWork 1");
    h.assert(steps.length >= fine.stats().totalWork, `${steps.length} steps for ${fine.stats().totalWork} units of one`);
    const whole = OkfSim.create(graph, { maxIterations: 20, sliceWork: 1000000000 });
    runToEnd(h, whole, 1000000000, "one slice");
    h.assert(sameBits(fine.positions(), whole.positions()), "slicing by one unit changed the layout");
    h.assert(fine.stats().totalWork === whole.stats().totalWork, "slicing changed the amount of work");
  });

  h.check("sim: positions() is a copy, and it changes only when an iteration completes (every step of one whole iteration at sliceWork 1)", () => {
    const OkfSim = loadSim();
    const s = OkfSim.create(shapes(OkfSim).path, { maxIterations: 5, sliceWork: 10 });
    const before = s.positions();
    const r = s.step();
    h.assert(r.iterations === 0 && r.work === 10, `the first slice of 10 units completed an iteration: ${JSON.stringify(r)}`);
    h.assert(sameBits(s.positions(), before), "positions moved before an iteration completed");
    const copy = s.positions();
    copy[0] = 123456;
    h.assert(s.positions()[0] !== 123456, "positions() returned the live buffer");
    // One unit at a time through the whole first iteration: not one
    // coordinate may move until the step that completes it.
    const fine = OkfSim.create(shapes(OkfSim).components, { maxIterations: 3, sliceWork: 1 });
    const initial = fine.positions();
    let steps = 0;
    for (;;) {
      const step = fine.step();
      steps++;
      if (step.iterations === 1) { break; }
      h.assert(sameBits(fine.positions(), initial), `positions moved after ${steps} unit(s), before the iteration completed`);
    }
    h.assert(steps > 20, `the first iteration took only ${steps} one-unit steps`);
    h.assert(!sameBits(fine.positions(), initial), "completing an iteration did not move any node");
  });

  h.check("sim: cancel() ends the work; a later simulation does not inherit anything", () => {
    const OkfSim = loadSim();
    const graph = shapes(OkfSim).components;
    // Rapid filter changes: ten simulations started and cancelled mid-way.
    for (let k = 0; k < 10; k++) {
      const s = OkfSim.create(graph, { maxIterations: 50, sliceWork: 3 });
      s.step();
      s.step();
      const worked = s.stats().totalWork;
      s.cancel();
      const r = s.step();
      h.assert(r.done === true && r.work === 0, `a cancelled simulation stepped: ${JSON.stringify(r)}`);
      h.assert(s.stats().totalWork === worked, "a cancelled simulation kept counting work");
    }
    const after = OkfSim.create(graph, { maxIterations: 50, sliceWork: 3 });
    runToEnd(h, after, 3, "after cancellations");
    const fresh = loadSim().create(graph, { maxIterations: 50, sliceWork: 3 });
    runToEnd(h, fresh, 3, "fresh module");
    h.assert(sameBits(after.positions(), fresh.positions()), "cancelled simulations changed a later layout");
  });

  // Snapshots (FNV-1a of the final coordinates). The layout is arithmetic on
  // IEEE doubles with + - * / and correctly rounded sqrt only, so these values
  // are the same on every conforming engine; a change to the force law, the
  // constants, the order of operations or the cell scan changes them, and
  // must be a deliberate, reviewed change (re-derive, then update here).
  // Revision 11 (grid cell 120 -> 180) kept components/40, whose cells never
  // fill up, and changed the two dense ones (were 5278e311 and de107342):
  // with larger cells a crowded cell lists other members, so the cap keeps
  // other pairs.
  const SNAPSHOT_COMPONENTS_40 = "54cbd2f8";
  const SNAPSHOT_DENSE_24 = "b95e30a5";
  const SNAPSHOT_DENSE_1 = "971155cc";

  h.check("sim: same input, same layout -- two loads and five slicings, mid-iteration cuts included", () => {
    const reference = {};
    for (const load of [0, 1]) {
      const OkfSim = loadSim();
      const all = shapes(OkfSim);
      for (const name of ["clumped", "components", "repeated", "path"]) {
        for (const sliceWork of [1, 7, 97, 1000, 1000000000]) {
          const s = OkfSim.create(all[name], { maxIterations: 40, sliceWork });
          runToEnd(h, s, sliceWork, `${name}/${sliceWork}`);
          const key = name;
          const got = { positions: s.positions(), work: s.stats().totalWork };
          if (!reference[key]) { reference[key] = got; continue; }
          h.assert(sameBits(got.positions, reference[key].positions), `${name}: load ${load}, sliceWork ${sliceWork} gave another layout`);
          h.assert(got.work === reference[key].work, `${name}: load ${load}, sliceWork ${sliceWork} counted ${got.work} units, not ${reference[key].work}`);
        }
      }
    }
    h.assert(layoutHash(reference.components.positions) === SNAPSHOT_COMPONENTS_40,
      `components/40: layout hash ${layoutHash(reference.components.positions)}, snapshot ${SNAPSHOT_COMPONENTS_40}`);
  });

  h.check("sim: the coincident-node branch is reached, and is deterministic across loads and slicings (cellCap 24 and 1)", () => {
    // A dense graph piles nodes so closely that some pairs fall under the
    // coincidence threshold. The first part proves, by counting through a
    // fault-injected copy of the module, that the branch executes; the second
    // runs the unmodified module through two loads and mid-iteration cuts.
    const hit = "var sign = node < j ? -1 : 1;";
    const counted = patchedWindow([[hit, "window.coincidentHits = (window.coincidentHits || 0) + 1; " + hit]]);
    const graph = { nodeCount: 200, edges: completeOnce(200) };
    for (const cellCap of [24, 1]) {
      const probe = counted.OkfSim.create(graph, { maxIterations: 200, sliceWork: 1000000000, cellCap });
      const before = counted.coincidentHits || 0;
      probe.step();
      const hits = (counted.coincidentHits || 0) - before;
      h.assert(hits >= 1, `cellCap ${cellCap}: the coincident-node branch was never reached (${hits} hits)`);
      const expected = layoutHash(probe.positions());
      for (const load of [0, 1]) {
        const OkfSim = loadSim();
        for (const sliceWork of [7, 97, 1000000000]) {
          const s = OkfSim.create(graph, { maxIterations: 200, sliceWork, cellCap });
          drain(h, s, sliceWork, `dense/${cellCap}/${sliceWork}`);
          h.assert(layoutHash(s.positions()) === expected, `dense/cellCap ${cellCap}: load ${load}, sliceWork ${sliceWork} gave another layout`);
        }
      }
      const pinned = cellCap === 24 ? SNAPSHOT_DENSE_24 : SNAPSHOT_DENSE_1;
      h.assert(expected === pinned, `dense/cellCap ${cellCap}: layout hash ${expected}, snapshot ${pinned}`);
    }
  });

  h.check("sim: a non-finite coordinate keeps the node where it was (fault injection: infinite gravity)", () => {
    // The module cannot produce a non-finite position from a valid graph; this
    // forces one (gravity of 1e308 overflows the force to infinity) and
    // counts, through the injected copy, that the guard ran.
    const guard = "if (!Number.isFinite(nx) || !Number.isFinite(ny)) {";
    const w = patchedWindow([
      ["var GRAVITY = 0.02;", "var GRAVITY = 1e308;"],
      [guard, guard + " window.guarded = (window.guarded || 0) + 1;"],
    ]);
    const graph = shapes(w.OkfSim).components;
    const s = w.OkfSim.create(graph, { maxIterations: 10, sliceWork: 1000 });
    runToEnd(h, s, 1000, "infinite gravity");
    h.assert((w.guarded || 0) > 0, "the non-finite guard never ran: this case no longer tests it");
    assertFiniteAndBounded(h, s.positions(), graph.nodeCount, "infinite gravity");
  });

  h.check("sim: the clamp holds when the layout is pushed outward without limit (fault injection: anti-gravity)", () => {
    // With gravity pointing away from the origin the nodes run to the edge of
    // the allowed square; the clamp is the only thing that stops them.
    const w = patchedWindow([["var GRAVITY = 0.02;", "var GRAVITY = -1;"]]);
    const graph = { nodeCount: 50, edges: [] };
    const s = w.OkfSim.create(graph, { maxIterations: 200, sliceWork: 1000000000 });
    s.step();
    const p = s.positions();
    assertFiniteAndBounded(h, p, 50, "anti-gravity");
    let atEdge = 0;
    for (let k = 0; k < p.length; k++) { if (Math.abs(p[k]) === bound(50)) { atEdge++; } }
    h.assert(atEdge > 0, "no coordinate reached the clamp: this case no longer tests it");
  });

  h.check("sim: layout invariants -- cooling, spreading, spring length, and agreement with an independent model", () => {
    const OkfSim = loadSim();
    const all = shapes(OkfSim);
    // Cooling: in iteration k no node moves further than SPACING * (1 - k/N).
    for (const name of ["path", "clumped", "edgeless", "components"]) {
      const iterations = 20;
      const s = OkfSim.create(all[name], { maxIterations: iterations, sliceWork: 7 });
      let previous = s.positions();
      let completed = 0;
      for (;;) {
        const r = s.step();
        if (r.iterations !== completed) {
          completed = r.iterations;
          const p = s.positions();
          let farthest = 0;
          for (let i = 0; i < p.length; i += 2) { farthest = Math.max(farthest, Math.sqrt((p[i] - previous[i]) ** 2 + (p[i + 1] - previous[i + 1]) ** 2)); }
          const limit = 60 * (1 - (completed - 1) / iterations);
          h.assert(farthest <= limit + 1e-9, `${name}: iteration ${completed - 1} moved a node ${farthest}, over its temperature ${limit}`);
          previous = p;
        }
        if (r.done) { break; }
      }
      h.assert(completed === iterations, `${name}: ${completed} iterations`);
    }
    // Spreading: 50 unconnected nodes repel each other, so after 100
    // iterations none is close to another (attraction would clump them).
    const spread = OkfSim.create(all.edgeless, { maxIterations: 100 });
    spread.step();
    const apart = minPairDistance(spread.positions(), 50);
    h.assert(apart >= 80, `edgeless: two nodes ${apart} apart after 100 iterations (repulsion should keep them > 80)`);
    // Springs: along a path of 40 nodes the median edge ends up near SPACING.
    const line = OkfSim.create(all.path, { maxIterations: 200 });
    line.step();
    const p = line.positions();
    const lengths = [];
    for (let i = 1; i < 40; i++) { lengths.push(Math.sqrt((p[2 * i] - p[2 * i - 2]) ** 2 + (p[2 * i + 1] - p[2 * i - 1]) ** 2)); }
    lengths.sort((a, b) => a - b);
    const median = lengths[19];
    h.assert(median >= 51 && median <= 75, `path: median edge length ${median}, expected within [51, 75] around SPACING 60`);
    // An independent model (all pairs, no grid) of tiny graphs: positions to
    // 1e-6, work exactly (pins repulsion, springs, gravity, cooling, counting).
    const tiny = [
      { nodeCount: 2, edges: [[0, 1]] },
      { nodeCount: 5, edges: [[0, 1], [1, 2], [2, 0], [2, 3]] },
      { nodeCount: 4, edges: [] },
    ];
    for (const graph of tiny) {
      const s = OkfSim.create(graph, { maxIterations: 8, sliceWork: 3 });
      const initial = s.positions();
      runToEnd(h, s, 3, "tiny");
      const model = referenceRun(initial, graph.nodeCount, graph.edges, 8);
      const got = s.positions();
      for (let k = 0; k < got.length; k++) {
        h.assert(Math.abs(got[k] - model.positions[k]) <= 1e-6, `${graph.nodeCount} nodes: coordinate ${k} is ${got[k]}, the model says ${model.positions[k]}`);
      }
      h.assert(s.stats().totalWork === model.work, `${graph.nodeCount} nodes: ${s.stats().totalWork} units, the model counts ${model.work}`);
    }
  });

  registerSimBoxes(h);
}

// --- boxes: what each node's drawing covers (spec §12.5, revision 11) --------

// acme_retail's graph as the page hands it to okf-sim: nodes in index order,
// links merged, and per node [halfWidth, above, below] -- the shape (graph
// context: square 30, circle 26, diamond 31.1, ring 26, triangle 24) and its
// label (the last id segment, 7 an advance, ending 20 under the shape).
const ACME = {
  nodeCount: 9,
  edges: [[0, 1], [2, 0], [2, 3], [2, 4], [3, 2], [4, 1], [5, 0], [5, 2], [5, 3], [6, 0], [6, 1], [6, 2], [6, 4], [6, 8]],
  boxes: [[66.5, 15, 35], [38.5, 15, 35], [42, 13, 33], [66.5, 13, 33], [24.5, 13, 33], [52.5, 15.55, 35.55],
    [66.5, 15.55, 35.55], [31.5, 13, 33], [21, 12, 32]],
};

// A graph whose every node has the same box.
function boxed(graph, box) {
  return { nodeCount: graph.nodeCount, edges: graph.edges, boxes: Array.from({ length: graph.nodeCount }, () => box.slice()) };
}

// The pairs whose boxes overlap by more than `slack` on both axes; node i
// covers x +- boxes[i][0], from y - boxes[i][1] to y + boxes[i][2].
function boxOverlaps(p, boxes, slack) {
  const out = [];
  for (let i = 0; i < boxes.length; i++) {
    for (let j = i + 1; j < boxes.length; j++) {
      const ox = boxes[i][0] + boxes[j][0] - Math.abs(p[2 * i] - p[2 * j]);
      const oy = Math.min(p[2 * i + 1] + boxes[i][2], p[2 * j + 1] + boxes[j][2])
        - Math.max(p[2 * i + 1] - boxes[i][1], p[2 * j + 1] - boxes[j][1]);
      if (ox > slack && oy > slack) { out.push(`${i}~${j} (${ox.toFixed(1)} x ${oy.toFixed(1)})`); }
    }
  }
  return out;
}

function registerSimBoxes(h) {
  h.check("sim: boxes keep drawings apart -- acme_retail's nine nodes, two stars and a clique end with no box over another; a link rests across the clearance", () => {
    const OkfSim = loadSim();
    const cases = [
      ["acme_retail", ACME],
      ["star of 16, wide labels", boxed(star(16), [80, 15, 35])],
      ["star of 30", boxed(star(30), [70, 15, 35])],
      ["clique of 12", boxed({ nodeCount: 12, edges: completeOnce(12) }, [60, 15, 35])],
    ];
    for (const [name, graph] of cases) {
      const s = OkfSim.create(graph);
      runToEnd(h, s, 100000, name);
      const p = s.positions();
      assertFiniteAndBounded(h, p, graph.nodeCount, name);
      const over = boxOverlaps(p, graph.boxes, 0);
      h.assert(over.length === 0, `${name}: ${over.length} pair(s) of boxes overlap: ${over.slice(0, 5).join(", ")}`);
      const apart = minPairDistance(p, graph.nodeCount);
      h.assert(apart >= 45, `${name}: two centres ${apart.toFixed(1)} apart (expected at least 45)`);
    }
    // A spring pulls across the clearance between its boxes, not across the
    // centre distance: two linked nodes with wide boxes rest 28 apart box to
    // box (a centre-distance spring would hold them 14 apart).
    const pair = OkfSim.create(boxed({ nodeCount: 2, edges: [[0, 1]] }, [80, 15, 35]));
    runToEnd(h, pair, 100000, "linked pair");
    const q = pair.positions();
    const clearance = Math.max(Math.abs(q[0] - q[2]) - 160, Math.abs(q[1] - q[3]) - 50);
    h.assert(clearance >= 20, `linked pair: ${clearance.toFixed(1)} between the two boxes (expected at least 20)`);
  });

  h.check("sim: the last fifth of the iterations only separates overlapping boxes -- a layout without overlap stays put", () => {
    const OkfSim = loadSim();
    // Two linked nodes with small boxes never overlap: once the last fifth
    // starts (iteration 160 of 200), no spring, repulsion or gravity moves them.
    const graph = { nodeCount: 2, edges: [[0, 1]], boxes: [[10, 5, 5], [10, 5, 5]] };
    const s = OkfSim.create(graph, { maxIterations: 200, sliceWork: 1 });
    const at = {};
    for (;;) {
      const r = s.step();
      if (at[r.iterations] === undefined) { at[r.iterations] = s.positions(); }
      if (r.done) { break; }
    }
    h.assert(!sameBits(at[159], at[160]), "iteration 159 moved nothing: this case no longer tells the two regimes apart");
    h.assert(sameBits(at[160], at[200]), "a node moved in the closing fifth although no boxes overlapped");
    // Without boxes there is no closing fifth: the same graph keeps moving.
    const plain = OkfSim.create({ nodeCount: 2, edges: [[0, 1]] }, { maxIterations: 200, sliceWork: 1 });
    const seen = {};
    for (;;) {
      const r = plain.step();
      if (seen[r.iterations] === undefined) { seen[r.iterations] = plain.positions(); }
      if (r.done) { break; }
    }
    h.assert(!sameBits(seen[190], seen[200]), "without boxes the last iterations stopped moving");
    // Settling, no node moves further than SPACING in one iteration (before,
    // the cooling temperature bounds it as without boxes).
    const crowd = OkfSim.create(boxed(star(30), [70, 15, 35]), { maxIterations: 200, sliceWork: 7 });
    let previous = crowd.positions();
    let completed = 0;
    for (;;) {
      const r = crowd.step();
      if (r.iterations !== completed) {
        completed = r.iterations;
        const p = crowd.positions();
        let farthest = 0;
        for (let i = 0; i < p.length; i += 2) { farthest = Math.max(farthest, Math.sqrt((p[i] - previous[i]) ** 2 + (p[i + 1] - previous[i + 1]) ** 2)); }
        const limit = completed > 160 ? 60 : 60 * (1 - (completed - 1) / 200);
        h.assert(farthest <= limit + 1e-9, `star of 30: iteration ${completed - 1} moved a node ${farthest}, over ${limit}`);
        previous = p;
      }
      if (r.done) { break; }
    }
  });

  h.check("sim: malformed boxes are a TypeError; above NODE_LIMIT they are not examined; an entry is read once; a huge box is clamped", () => {
    const OkfSim = loadSim();
    const bad = [
      "x", {}, [[1, 1, 1]], [[1, 1, 1], [1, 1, 1], [1, 1, 1], [1, 1, 1]], [null, [1, 1, 1], [1, 1, 1]],
      [[1, 1], [1, 1, 1], [1, 1, 1]], [[1, 1, 1, 1], [1, 1, 1], [1, 1, 1]], [[-1, 1, 1], [1, 1, 1], [1, 1, 1]],
      [[1, NaN, 1], [1, 1, 1], [1, 1, 1]], [[1, 1, Infinity], [1, 1, 1], [1, 1, 1]], [["1", 1, 1], [1, 1, 1], [1, 1, 1]],
    ];
    for (const boxes of bad) {
      let threw = null;
      try { OkfSim.create({ nodeCount: 3, edges: [[0, 1]], boxes }); } catch (e) { threw = e; }
      h.assert(threw && threw.name === "TypeError", `boxes ${JSON.stringify(boxes)} did not throw a TypeError (${threw && threw.name})`);
    }
    // Not an array: refused before the limit, like edges. Entries: never
    // examined above it.
    let threw = null;
    try { OkfSim.create({ nodeCount: 5000, edges: [], boxes: "x" }); } catch (e) { threw = e; }
    h.assert(threw && threw.name === "TypeError", "boxes that are not an array were not refused above NODE_LIMIT");
    h.assert(OkfSim.create({ nodeCount: 5000, edges: [], boxes: [null] }) === null, "above NODE_LIMIT the box entries were examined");
    const sparse = inCtx(OkfSim, "new Array(4294967295)");
    h.assert(OkfSim.create({ nodeCount: 5000, edges: [], boxes: sparse }) === null, "a sparse box list above NODE_LIMIT was not refused with null");
    // Read once: a getter that changes its answer cannot smuggle in a bad box.
    const probe = inCtx(OkfSim, "(function () { var b = []; var reads = 0;"
      + " Object.defineProperty(b, '0', { get: function () { reads++; return reads === 1 ? [5, 5, 5] : [-1, NaN, 1]; }, enumerable: true });"
      + " b[1] = [5, 5, 5]; return { boxes: b, reads: function () { return reads; } }; })()");
    const s = OkfSim.create({ nodeCount: 2, edges: [[0, 1]], boxes: probe.boxes }, { maxIterations: 20 });
    runToEnd(h, s, 100000, "read once");
    assertFiniteAndBounded(h, s.positions(), 2, "read once");
    h.assert(probe.reads() === 1, `the box entry was read ${probe.reads()} times`);
    // A box larger than the grid can see is clamped to (180 - 8) / 2 = 86, the
    // most two boxes may span and still share a 3 x 3 neighbourhood: the
    // layout is the one of boxes at that limit, finite and bounded.
    const huge = OkfSim.create({ nodeCount: 3, edges: [[0, 1]], boxes: [[1e300, 1e300, 1e300], [0, 0, 0], [1e9, 0, 1e9]] });
    runToEnd(h, huge, 100000, "huge boxes");
    assertFiniteAndBounded(h, huge.positions(), 3, "huge boxes");
    const atLimit = OkfSim.create({ nodeCount: 3, edges: [[0, 1]], boxes: [[86, 86, 86], [0, 0, 0], [86, 0, 86]] });
    runToEnd(h, atLimit, 100000, "boxes at the limit");
    h.assert(sameBits(huge.positions(), atLimit.positions()), "boxes above the limit are not clamped to 86");
  });

  // FNV-1a of acme_retail's layout with its boxes, default options (see the
  // other snapshots: a change here is a reviewed change of the layout).
  const SNAPSHOT_ACME_BOXED = "3842fc78";

  h.check("sim: with boxes, same input, same layout -- two loads and four slicings; work stays within the per-cell ceiling", () => {
    const reference = {};
    const graphs = { acme: ACME, star: boxed(star(16), [80, 15, 35]) };
    for (const load of [0, 1]) {
      const OkfSim = loadSim();
      for (const name of Object.keys(graphs)) {
        for (const sliceWork of [1, 7, 997, 1000000000]) {
          const s = OkfSim.create(graphs[name], { sliceWork });
          drain(h, s, sliceWork, `${name}/${sliceWork}`);
          const got = { positions: s.positions(), work: s.stats().totalWork };
          const g = graphs[name];
          const ceiling = 200 * (g.nodeCount * 9 * 24 + g.edges.length + g.nodeCount);
          h.assert(got.work <= ceiling, `${name}: ${got.work} units over 200 iterations, above ${ceiling}`);
          if (!reference[name]) { reference[name] = got; continue; }
          h.assert(sameBits(got.positions, reference[name].positions), `${name}: load ${load}, sliceWork ${sliceWork} gave another layout`);
          h.assert(got.work === reference[name].work, `${name}: load ${load}, sliceWork ${sliceWork} counted ${got.work} units, not ${reference[name].work}`);
        }
      }
    }
    h.assert(layoutHash(reference.acme.positions) === SNAPSHOT_ACME_BOXED,
      `acme/boxes: layout hash ${layoutHash(reference.acme.positions)}, snapshot ${SNAPSHOT_ACME_BOXED}`);
  });
}

// --- purity guards for okf-sim.js (spec §4.2, §7 control 2) -----------------

// Walks the source and returns { code, findings }. `code` is the source with
// comments removed and the CONTENTS of string literals blanked (delimiters
// kept), so text inside a string or a comment can neither hide code (a
// string holding two slashes does not start a comment) nor look like code
// (a string reading Math.sin is not a call). `findings` lists constructs the
// static check refuses outright, because it cannot see through them: template
// literals; HTML-like comments (the characters <!-- and -->, which V8 reads as
// single-line comments and which would hide a block-comment opener from this
// scanner); any backslash-u or backslash-x escape (inside a string or an
// identifier); and a slash that opens a regular-expression literal, which is
// recognised only (a) right after one of the characters ( , = : [ ! & | ? { } ;
// + - * % < > ~ ^ or at the start, and (b) right after one of the keywords in
// REGEX_KEYWORDS. A slash after any other operand (an identifier, a number, a
// closing parenthesis or bracket) is read as division, so a regular-expression
// literal after the closing parenthesis of an if/while/for head is NOT
// recognised: okf-sim.js has none, and what such a literal holds is still
// scanned as code (so a forbidden name inside it is still found).
const REGEX_KEYWORDS = ["return", "typeof", "void", "case", "in", "of", "delete", "new", "else", "do", "yield",
  "await", "instanceof", "throw"];

function scanSource(source) {
  let code = "";
  const findings = [];
  const n = source.length;
  let previous = "";
  let i = 0;
  while (i < n) {
    const c = source[i];
    const d = source[i + 1];
    if (c === "/" && d === "/") {
      while (i < n && source[i] !== "\n") { i++; }
      continue;
    }
    if (c === "/" && d === "*") {
      const end = source.indexOf("*/", i + 2);
      if (end < 0) { findings.push("unterminated block comment"); break; }
      code += " ";
      i = end + 2;
      continue;
    }
    if (c === "\"" || c === "'") {
      code += c;
      i++;
      while (i < n && source[i] !== c && source[i] !== "\n") {
        if (source[i] === BS) {
          const e = source[i + 1];
          if (e === "u" || e === "x") { findings.push(`a ${BS}${e} escape in a string literal`); }
          code += "  ";
          i += 2;
          continue;
        }
        code += " ";
        i++;
      }
      if (i >= n || source[i] !== c) { findings.push("unterminated string literal"); } else { code += c; i++; }
      previous = c;
      continue;
    }
    if (c === "`") {
      findings.push("a template literal (backtick)");
      code += c;
      i++;
      previous = c;
      continue;
    }
    if ((c === "<" && source.startsWith("<!--", i)) || (c === "-" && source.startsWith("-->", i))) {
      findings.push(`an HTML-like comment (${source.slice(i, i + (c === "<" ? 4 : 3))})`);
    }
    if (c === "/") {
      // After an operand it is division; anywhere else it opens a regular
      // expression literal, which this check does not parse.
      const word = /([A-Za-z_$][\w$]*)$/.exec(code.trimEnd());
      if (previous === "" || "(,=:[!&|?{};+-*%<>~^".includes(previous) || (word !== null && REGEX_KEYWORDS.includes(word[1]))) {
        findings.push("a regular-expression literal");
      }
      code += c;
      i++;
      previous = c;
      continue;
    }
    if (c === BS) {
      findings.push(`a backslash escape outside a string (${BS}${d} in an identifier)`);
      code += c;
      i++;
      continue;
    }
    code += c;
    if (c !== " " && c !== "\t" && c !== "\n" && c !== "\r") { previous = c; }
    i++;
  }
  return { code, findings };
}

// Source text with comments removed and string contents blanked.
function codeOf(source) {
  return scanSource(source).code;
}

// The names okf-sim.js must not reach, beyond Math: anything that reads a
// clock, a random source, the DOM or another global, or that can build code
// or reach the global object (`this`, constructors, prototypes).
const FORBIDDEN_NAMES = ["Date", "performance", "random", "requestAnimationFrame", "setTimeout", "setInterval",
  "setImmediate", "queueMicrotask", "document", "globalThis", "self", "eval", "Function", "constructor", "__proto__",
  "this", "WeakRef", "FinalizationRegistry", "crypto", "Intl", "Atomics", "SharedArrayBuffer", "Reflect", "Proxy",
  "location", "navigator", "import", "require", "process", "OKF_SCHEDULER", "OKF_INDEX"];

// Findings of the static check, as readable strings (empty = clean).
function purityFindings(source) {
  const { code, findings } = scanSource(source);
  const found = findings.slice();
  for (const m of code.matchAll(/\bMath\s*\.\s*([A-Za-z_$][\w$]*)/g)) {
    if (!ALLOWED_MATH.includes(m[1])) { found.push(`Math.${m[1]}`); }
  }
  if (/\bMath\s*\[/.test(code)) { found.push("Math[...] (computed access)"); }
  if (/\bMath\b(?!\s*\.)/.test(code)) { found.push("Math used other than as Math.<name>"); }
  if (code.includes("**")) { found.push("the ** operator"); }
  for (const name of FORBIDDEN_NAMES) {
    if (new RegExp(`\\b${name}\\b`).test(code)) { found.push(name); }
  }
  const windows = code.match(/\bwindow\b/g) || [];
  if (windows.length !== 1 || !/\bwindow\.OkfSim\s*=/.test(code)) {
    found.push(`window used ${windows.length} time(s); only "window.OkfSim =" is allowed`);
  }
  return found;
}

function registerPurity(h) {
  // SMOKE CHECK, not proof: it reads okf-sim.js's source text. It catches a
  // forbidden Math function, the ** operator, a clock, the DOM, `this`, a
  // constructor chain or another global written anywhere in the file,
  // executed or not, and refuses what it cannot see through: template
  // literals, HTML-like comments, backslash-u and backslash-x escapes, and a
  // regular-expression literal opened after an operator, an opening bracket or
  // a listed keyword (a regular-expression literal after the closing
  // parenthesis of an if/while/for head is not recognised; see scanSource). It
  // does NOT prove the module is pure: an alias assembled at run time by a
  // construct it does not parse would pass. The sandbox is the executable
  // half, and proves only what the cases actually EXECUTE: sandboxLoad()
  // gives the module a Math rebuilt inside a fresh context from that
  // context's own Math (allowed functions only), poisons the Function
  // constructors, deletes the clock/random/dynamic-code globals, copies every
  // argument of create() into that context so no host-realm object reaches the
  // module, and runs it strict with `this` undefined, so a forbidden call on an
  // executed path throws.
  h.check("sim purity: okf-sim.js calls no Math function outside §4.2 and no clock, DOM or other global (smoke check)", () => {
    const findings = purityFindings(fs.readFileSync(SIM_FILE, "utf8"));
    h.assert(findings.length === 0, `okf-sim.js uses: ${findings.join(", ")}`);
  });

  h.check("sim purity: the static check itself flags each forbidden form", () => {
    const clean = "(function () { window.OkfSim = Object.freeze({ a: Math.sqrt(2) + Math.imul(3, 4) }); })();";
    h.assert(purityFindings(clean).length === 0, `a clean module was flagged: ${purityFindings(clean).join(", ")}`);
    // [snippet, a substring the findings must contain]
    const dirty = [
      ["var a = Math.sin(1);", "Math.sin"], ["var a = Math.pow(2, 3);", "Math.pow"], ["var a = Math.round(2.5);", "Math.round"],
      ["var a = 2 ** 3;", "the ** operator"], ["var a = Math['cos'](1);", "Math[...]"],
      ["var m = Math; var a = m.exp(1);", "Math used other than"], ["var a = Math?.sin(1);", "Math used other than"],
      ["var a = Date.now();", "Date"], ["var a = Math.random();", "Math.random"],
      ["var a = performance.now();", "performance"], ["requestAnimationFrame(f);", "requestAnimationFrame"],
      ["var d = document.body;", "document"], ["var g = globalThis;", "globalThis"], ["var w = window.innerWidth;", "window used"],
      // Strings holding comment openers must not hide what follows.
      ["var u = '//'; var a = 2 ** 3;", "the ** operator"], ["var u = \"http://x\"; var a = Math.sin(0);", "Math.sin"],
      ["var u = '/*'; var a = Math.sin(0); var v = '*/';", "Math.sin"],
      // Constructor chains, `this`, and the globals a clock or GC can hide in.
      ["var a = Math.sqrt.constructor('return 1');", "constructor"], ["var a = [].constructor.constructor('x');", "constructor"],
      ["var a = this['Ma' + 'th'];", "this"], ["var a = typeof WeakRef;", "WeakRef"], ["var a = crypto;", "crypto"],
      ["var a = queueMicrotask;", "queueMicrotask"], ["var a = location;", "location"], ["var a = navigator;", "navigator"],
      ["var a = eval('1');", "eval"], ["var a = Function('1');", "Function"], ["var a = new Intl.DateTimeFormat();", "Intl"],
      // Forms the check cannot read are refused outright.
      ["var a = `x`;", "template literal"], ["var a = /x/.test('x');", "regular-expression literal"],
      ["var a = Math." + BS + "u0073in(0);", "escape"], ["var a = M" + BS + "u0061th.sin(0);", "escape"],
      ["var a = 'Ma" + BS + "x74h';", "escape"],
      // HTML-like comments are single-line comments to V8 and hide a `/*` from
      // a scanner that does not know them; a regular-expression literal after
      // a keyword is not division, and its quote would start a string.
      ["var q = 0; <!-- /*\nvar a = Math.sin(0) + 2 ** 3;\n--> */", "HTML-like comment"],
      ["var q = 0;\n--> var a = Math.sin(0);", "HTML-like comment"],
      ["var a = [void /'/, Math.sin(0), 2 ** 3, void /'/][1];", "regular-expression literal"],
      ["function g() { return /'/.source + Math.sin(0) + /'/.source; }", "regular-expression literal"],
      ["var a = typeof /x/;", "regular-expression literal"],
      ["switch (1) { case /x/.test('x'): break; }", "regular-expression literal"],
      ["var a = 'x' in /y/;", "regular-expression literal"],
      ["var a = new /x/.constructor();", "regular-expression literal"],
    ];
    for (const [line, expected] of dirty) {
      const source = clean.replace("window.OkfSim", `${line} window.OkfSim`);
      const findings = purityFindings(source);
      h.assert(findings.length > 0, `not flagged: ${line}`);
      h.assert(findings.join(" | ").includes(expected), `${line}: flagged as ${findings.join(" | ")}, expected a finding with "${expected}"`);
    }
    // Comments, and text inside strings, are not code.
    h.assert(purityFindings(clean + "\n// Math.sin and ** in a comment are not code\n").length === 0, "a comment was flagged");
    h.assert(purityFindings(clean + "\n/* Math.sin ** `x` */\n").length === 0, "a block comment was flagged");
    const quiet = "(function () { var u = 'http://x/*y*/ Math.sin ** this <!-- -->'; var d = (4 + 2) / 3 / 1; var total = d / 2; window.OkfSim = u + total; })();";
    h.assert(purityFindings(quiet).length === 0, `strings or divisions were flagged: ${purityFindings(quiet).join(", ")}`);
    h.assert(codeOf("var s = 'a//b'; // c").includes("var s ="), "codeOf dropped code after a string holding //");
  });

  h.check("sim purity: the sandbox refuses every route to a real clock, random or forbidden Math function, and no host-realm object reaches the module", () => {
    // Positive control: the sandbox runs a module that only uses what it may.
    const ok = sandboxLoad("window.r = Math.sqrt(16) + Math.max(1, 2) + Math.imul(2, 3);");
    h.assert(ok.r === 12, `the sandbox failed to run an allowed module: ${ok.r}`);
    const blocked = "sandbox: dynamic code construction is blocked";
    // [what is attempted, module source, expected error name, expected message fragment]. Each must
    // throw THAT error: an unrelated failure (a typo in the source, say) cannot pass.
    const hostile = [
      ["Math.sin", "window.r = Math.sin(1);", "TypeError", "Math.sin is not a function"],
      ["Math.random", "window.r = Math.random();", "TypeError", "Math.random is not a function"],
      ["assigning into Math", "Math.sin = function () { return 0; };", "TypeError", "object is not extensible"],
      ["Math.sqrt.constructor (the Function constructor via an allowed function)", "window.r = Math.sqrt.constructor('return Ma' + 'th.sin(0)')();", "TypeError", blocked],
      ["[].constructor.constructor", "window.r = [].constructor.constructor('return Ma' + 'th.sin(0)')();", "TypeError", blocked],
      ["new Map().constructor.constructor reaching Date.now", "window.r = new Map().constructor.constructor('return Da' + 'te.now()')();", "TypeError", blocked],
      ["a function value's constructor", "window.r = (function () {}).constructor('return 1')();", "TypeError", blocked],
      ["the async function constructor", "window.r = (async function () {}).constructor('return 1');", "TypeError", blocked],
      ["the generator function constructor", "window.r = (function* () {}).constructor('return 1');", "TypeError", blocked],
      ["the async generator function constructor", "window.r = (async function* () {}).constructor('return 1');", "TypeError", blocked],
      ["this['Ma'+'th'].sin at the top level", "window.r = this['Ma' + 'th'].sin(0);", "TypeError", "Cannot read properties of undefined (reading 'Math')"],
      ["Date.now()", "window.r = Date.now();", "TypeError", "Cannot read properties of undefined (reading 'now')"],
      ["new Date()", "window.r = new Date();", "TypeError", "Date is not a constructor"],
      ["eval", "window.r = eval('1');", "ReferenceError", "eval is not defined"],
      ["the Function global", "window.r = Function('return 1')();", "ReferenceError", "Function is not defined"],
      ["Intl", "window.r = new Intl.DateTimeFormat().format();", "ReferenceError", "Intl is not defined"],
      ["WeakRef", "window.r = new WeakRef({});", "ReferenceError", "WeakRef is not defined"],
      ["FinalizationRegistry", "window.r = new FinalizationRegistry(function () {});", "ReferenceError", "FinalizationRegistry is not defined"],
      ["performance.now()", "window.r = performance.now();", "TypeError", "Cannot read properties of undefined (reading 'now')"],
      ["setTimeout", "window.r = setTimeout(function () {}, 0);", "TypeError", "setTimeout is not a function"],
      ["document", "window.r = document.body;", "TypeError", "Cannot read properties of undefined (reading 'body')"],
    ];
    for (const [name, code, errorName, fragment] of hostile) {
      let threw = null;
      try { sandboxLoad(code); } catch (e) { threw = e; }
      h.assert(threw !== null, `the sandbox let this through: ${name}`);
      h.assert(threw.name === errorName && String(threw.message).includes(fragment),
        `${name}: threw ${threw.name}: ${threw.message}, expected ${errorName} containing "${fragment}"`);
    }
    // Host-realm objects: create() receives the host's graph, edge arrays and
    // options. A module that climbs from one of them to a Function constructor
    // would get the HOST's, unpoisoned. The sandbox copies every argument into
    // its own realm first, so the climb ends at the poisoned one.
    const climb = "['con' + 'structor']['con' + 'structor']";
    const viaArgs = [
      ["the graph", `graph${climb}('return Ma' + 'th.sin(1)')()`, { nodeCount: 2, edges: [[0, 1]] }, undefined],
      ["the graph, reaching Date.now", `graph${climb}('return Da' + 'te.now()')()`, { nodeCount: 2, edges: [[0, 1]] }, undefined],
      ["the graph, reaching process", `graph${climb}('return pro' + 'cess')()`, { nodeCount: 2, edges: [[0, 1]] }, undefined],
      ["an edge entry, reaching Math.random", `graph.edges[0]${climb}('return Ma' + 'th.random()')()`, { nodeCount: 2, edges: [[0, 1]] }, undefined],
      ["the edge list", `graph.edges${climb}('return Ma' + 'th.sin(1)')()`, { nodeCount: 2, edges: [[0, 1]] }, undefined],
      ["the options", `options${climb}('return Ma' + 'th.sin(1)')()`, { nodeCount: 2, edges: [] }, { maxIterations: 3 }],
    ];
    for (const [name, expression, graph, options] of viaArgs) {
      const win = sandboxLoad(`window.OkfSim = Object.freeze({ NODE_LIMIT: 1500, create: function (graph, options) { return ${expression}; } });`);
      let threw = null;
      try { win.OkfSim.create(graph, options); } catch (e) { threw = e; }
      h.assert(threw !== null, `a host-realm object reached the module through ${name}`);
      h.assert(threw.name === "TypeError" && String(threw.message).includes(blocked),
        `${name}: threw ${threw.name}: ${threw.message}, expected the sandbox's blocked constructor`);
    }
    // The module's own loader runs through the same function: a function
    // evaluated in the OkfSim's realm sees the poisoned constructors, the
    // restricted Math and the deleted globals (inCtx refuses an OkfSim that
    // sandboxLoad did not produce).
    const realm = inCtx(loadSim(), "(function () { var r = []; try { (function () {}).constructor('return 1'); r.push('ctor ran'); } catch (e) { r.push('ctor blocked'); }"
      + " r.push(typeof Date, typeof eval, typeof Function, typeof Math.sin, typeof Math.sqrt, Object.isFrozen(Math), typeof WeakRef, typeof Intl);"
      + " return r.join(); })()");
    h.assert(realm === "ctor blocked,undefined,undefined,undefined,undefined,function,true,undefined,undefined",
      `loadSim()'s realm is not the sandbox: ${realm}`);
  });
}

// --- graph page (graph.html) -----------------------------------------------

// U+00B7, built at run time (an escape typed into a file is decoded on write).
const MIDDOT = String.fromCharCode(0xb7);
const EMDASH = String.fromCharCode(0x2014);
const GRAPH_SCRIPTS = ["okf-index.js", "okf-site.js", "okf-shapes.js", "okf-palette.js", "okf-sim.js", "okf-graph.js"];

// The page's path, from the "Global graph" link of index.html with its
// fragment removed: never a hard-coded name (§12.7, A25).
async function graphPath(h) {
  const index = await h.openPage("index.html");
  const link = index.document.getElementById("okf-global-graph");
  h.assert(link, "index.html has no #okf-global-graph link");
  return link.getAttribute("href").split("#")[0];
}

// The deterministic scheduler of §7: frame callbacks queue here and run only
// when a case drains them.
function makeScheduler() {
  const queue = [];
  let ran = 0;
  return {
    install(w) { w.OKF_SCHEDULER = (callback) => { queue.push(callback); }; },
    pending() { return queue.length; },
    ran() { return ran; },
    // Runs `count` queued callbacks (fewer if the queue empties).
    frames(count) {
      for (let k = 0; k < count && queue.length > 0; k++) { queue.shift()(); ran++; }
    },
    // Drains the queue, callbacks queued meanwhile included; bounded.
    flush(limit = 100000) {
      let k = 0;
      while (queue.length > 0) {
        if (k++ >= limit) { throw new Error(`the layout did not end within ${limit} frames`); }
        queue.shift()();
        ran++;
      }
    },
  };
}

// Opens the graph page with the scheduler injected before any script runs.
async function openGraph(h, opts = {}) {
  const rel = await graphPath(h);
  const scheduler = makeScheduler();
  const before = opts.beforeParse;
  const window = await h.openPage(rel, Object.assign({}, opts, {
    beforeParse(w) {
      scheduler.install(w);
      if (before) { before(w); }
    },
  }));
  return { window, doc: window.document, scheduler, rel };
}

// The drawn nodes, by the id their <title> carries ("absent: <id>" for a ghost).
function drawnNodes(doc) {
  return Array.from(doc.querySelectorAll("#okf-graph-canvas g.okf-node")).map((g) => ({
    g, name: g.querySelector("title").textContent,
  }));
}

function nodeNamed(doc, name) {
  const found = drawnNodes(doc).find((n) => n.name === name);
  return found ? found.g : null;
}

// Where a node's <g> is moved to: its translate(x y), or (0, 0) without one
// (revision 12: the shapes are drawn once around (0, 0) and the node moves).
function nodeOffset(g) {
  const m = /^translate\((-?[0-9.]+) (-?[0-9.]+)\)$/.exec(g.getAttribute("transform") || "translate(0 0)");
  if (!m) { throw new Error(`node transform "${g.getAttribute("transform")}"`); }
  return { x: Number(m[1]), y: Number(m[2]) };
}

// The centre of a drawn node, from its label (x = cx, y = cy + size/2 + 16),
// in graph units.
function labelPoint(g) {
  const text = g.querySelector("text");
  const at = nodeOffset(g);
  return { x: at.x + Number(text.getAttribute("x")), y: at.y + Number(text.getAttribute("y")) };
}

function facetInputs(doc, id) {
  const heading = doc.getElementById(`okf-facet-${id}`);
  return heading ? Array.from(heading.parentElement.querySelectorAll("input[type=checkbox]")) : [];
}

function click(window, target) {
  target.dispatchEvent(new window.MouseEvent("click", { bubbles: true, cancelable: true }));
}

function setChecked(window, input, value) {
  input.checked = value;
  input.dispatchEvent(new window.Event("change", { bubbles: true }));
}

// Expected status line, computed from the executed index (never a count
// written in a case, §12.7).
function expectedStatus(index, shownConcepts, matchedCount) {
  const N = index.concepts.length;
  const shown = new Set(shownConcepts);
  const ghosts = new Set();
  let links = 0;
  for (const [from, to, , toGhost] of index.edges) {
    if (!shown.has(from)) { continue; }
    if (toGhost === 1) { ghosts.add(to); links++; } else if (shown.has(to)) { links++; }
  }
  const M = index.edges.length;
  let text = `showing ${matchedCount} of ${N} ${N === 1 ? "concept" : "concepts"}`;
  if (ghosts.size > 0) { text += ` + ${ghosts.size} absent`; }
  return `${text} ${MIDDOT} ${links} of ${M} ${M === 1 ? "link" : "links"}`;
}

// The list is built only while it is shown: a case that reads it opens it first.
function showList(window, doc) {
  const toggle = doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
  if (doc.getElementById("okf-graph-list").hidden) { click(window, toggle); }
}

function conceptPos(index, id) {
  const i = index.concepts.findIndex((c) => c.id === id);
  if (i === -1) { throw new Error(`the fixture lost concept ${id}`); }
  return i;
}

function registerGraphPage(h) {
  h.checkAsync("graph page: reached by the Global graph link, it loads exactly its six scripts", async () => {
    const { doc, rel } = await openGraph(h);
    h.assert(doc.documentElement.getAttribute("data-okf-view") === "graph", `${rel} is not the graph view`);
    const scripts = Array.from(doc.querySelectorAll("body script[src]"), (s) => s.getAttribute("src"));
    h.assert(JSON.stringify(scripts) === JSON.stringify(GRAPH_SCRIPTS.map((s) => `assets/${s}`)), `scripts: ${scripts.join(", ")}`);
    h.assert(doc.getElementById("okf-body") === null && doc.getElementById("okf-payload") === null, "the graph page carries a body or a payload");
  });

  h.checkAsync("graph page: the status line counts the whole bundle by default", async () => {
    const { window, doc } = await openGraph(h);
    const index = window.OKF_INDEX;
    const all = index.concepts.map((_, i) => i);
    const want = expectedStatus(index, all, all.length);
    const got = doc.getElementById("okf-graph-status").textContent;
    h.assert(got === want, `status "${got}", expected "${want}"`);
  });

  h.checkAsync("graph page: List shows every concept and ghost with its relations, in index order", async () => {
    const { window, doc } = await openGraph(h);
    const index = window.OKF_INDEX;
    const toggle = doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
    h.assert(toggle && toggle.getAttribute("aria-pressed") === "false", "no List toggle, or pressed by default");
    click(window, toggle);
    const list = doc.getElementById("okf-graph-list");
    h.assert(!list.hidden && doc.getElementById("okf-graph-canvas").hidden && toggle.getAttribute("aria-pressed") === "true", "List did not replace the drawing");
    const names = Array.from(list.querySelectorAll(".okf-graph-list-select"), (b) => b.textContent);
    const want = index.concepts.map((c) => c.id).concat(index.ghosts.map((g) => `absent: ${g.id}`));
    h.assert(JSON.stringify(names) === JSON.stringify(want), `list entries: ${names.join(" | ")}`);
    const a = Array.from(list.querySelectorAll(".okf-graph-list-item")).find((li) => li.querySelector(".okf-graph-list-select").textContent === "p3-graph/b");
    const relations = Array.from(a.querySelectorAll(".okf-graph-list-rel li"), (li) => li.textContent);
    for (const want2 of ["links to p3-graph/c", "referenced by p3-graph/a", "referenced by p3-graph/hostile"]) {
      h.assert(relations.includes(want2), `p3-graph/b lacks "${want2}": ${relations.join(" | ")}`);
    }
    click(window, toggle);
    h.assert(list.hidden && !doc.getElementById("okf-graph-canvas").hidden, "List did not give the drawing back");
  });

  h.checkAsync("graph page: selecting from the list fills the drawer and points Reading view at the page", async () => {
    const { window, doc } = await openGraph(h);
    showList(window, doc);
    const index = window.OKF_INDEX;
    const detail = doc.getElementById("okf-graph-detail");
    h.assert(detail.textContent.includes("Select a concept to see its links."), "the empty drawer lacks its prompt");
    const reading = doc.getElementById("okf-reading-view");
    h.assert(reading.getAttribute("href") === "index.html", `Reading view starts at ${reading.getAttribute("href")}`);
    const b = index.concepts[conceptPos(index, "p3-graph/b")];
    const button = Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).find((x) => x.textContent === "p3-graph/b");
    click(window, button);
    h.assert(detail.querySelector(".okf-section-title").textContent === "Selected", "the drawer lacks its \"Selected\" title");
    h.assert(detail.querySelector(".okf-chips .okf-chip.okf-chip-type"), "the drawer lacks the okf-chip-type chip");
    h.assert(detail.querySelector(".okf-graph-detail-id").textContent === "p3-graph/b", "the drawer does not show the id");
    h.assert(detail.querySelector("h2").textContent === b.title, "the drawer does not show the title");
    h.assert(detail.querySelector(".okf-graph-detail-desc").textContent === b.description, "the drawer lacks the description");
    const heads = Array.from(detail.querySelectorAll(".okf-graph-rel h3"), (x) => x.textContent);
    h.assert(heads[0] === `Links to ${MIDDOT} 1` && heads[1] === `Referenced by ${MIDDOT} 2`, `relation heads: ${heads.join(" | ")}`);
    const open = detail.querySelector("a.okf-graph-open");
    h.assert(open && open.getAttribute("href") === b.path && open.textContent === "Open page", "Open page is missing or wrong");
    h.assert(reading.getAttribute("href") === b.path, `Reading view points at ${reading.getAttribute("href")}`);
    h.assert(button.getAttribute("aria-current") === "true", "the list does not mark the selection");
  });

  h.checkAsync("graph page: the status line uses the singular forms (1 concept, 1 link) and counts a cited ghost", async () => {
    const mk = (id) => ({ id, title: "T " + id, type: "Note", tags: [], path: id + ".html", trust: "unverified", typeIndex: 0, description: "" });
    const types = [{ name: "Note", count: 1, slot: 0 }];
    const source = (ghosts, edges) => "window.OKF_INDEX = " + JSON.stringify({
      version: 2, concepts: [mk("solo")], ghosts, edges, tree: [{ name: "solo", concept: 0, children: [] }], types,
    }) + ";";
    // One concept citing itself: listed, counted as a link, never drawn.
    const self = await openGraph(h, { override: { "assets/okf-index.js": source([], [[0, 0, 1, 0]]) } });
    let got = self.doc.getElementById("okf-graph-status").textContent;
    h.assert(got === `showing 1 of 1 concept ${MIDDOT} 1 of 1 link`, `self-link status "${got}"`);
    h.assert(got === expectedStatus(self.window.OKF_INDEX, [0], 1), "the literal and expectedStatus disagree");
    // One concept citing one absent concept: the ghost is counted apart.
    const ghost = await openGraph(h, { override: { "assets/okf-index.js": source([{ id: "nowhere" }], [[0, 0, 1, 1]]) } });
    got = ghost.doc.getElementById("okf-graph-status").textContent;
    h.assert(got === `showing 1 of 1 concept + 1 absent ${MIDDOT} 1 of 1 link`, `ghost status "${got}"`);
    h.assert(got === expectedStatus(ghost.window.OKF_INDEX, [0], 1), "the literal and expectedStatus disagree (ghost)");
  });

  h.checkAsync("graph page: a cited ghost is a row without a link; a selected ghost shows only 'absent' and its id", async () => {
    const { window, doc } = await openGraph(h);
    showList(window, doc);
    const detail = doc.getElementById("okf-graph-detail");
    const pick = (name) => click(window, Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).find((x) => x.textContent === name));
    pick("p3-graph/hostile");
    const absent = Array.from(detail.querySelectorAll(".okf-row")).find((r) => r.textContent === "absent: p3-graph/absent-target");
    h.assert(absent && absent.tagName === "SPAN" && !absent.closest("a"), "the cited ghost is not a plain row");
    pick("absent: p3-graph/absent-target");
    h.assert(detail.querySelector("h2").textContent === "absent" && detail.querySelector(".okf-graph-detail-id").textContent === "p3-graph/absent-target", "the ghost drawer is wrong");
    h.assert(detail.querySelectorAll("a").length === 0, "the ghost drawer holds a link");
    h.assert(doc.getElementById("okf-reading-view").getAttribute("href") === "index.html", "Reading view points at a ghost");
  });

  h.checkAsync("graph page: hostile title, type and description stay text in the drawer", async () => {
    const { window, doc } = await openGraph(h);
    showList(window, doc);
    click(window, Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).find((x) => x.textContent === "p3-graph/hostile"));
    const detail = doc.getElementById("okf-graph-detail");
    for (const n of ["31", "32", "33"]) {
      h.assert(detail.textContent.includes(`<img src=x onerror="window.__pwned=${n}">`), `the drawer dropped hostile text ${n} instead of showing it`);
    }
    h.assert(doc.getElementById("okf-graph-layout").querySelectorAll("img, [onerror]").length === 0, "an element or handler from bundle text reached the page");
    h.assert(window.__pwned === undefined, "bundle text executed");
  });

  h.checkAsync("graph page: the drawer's trust and staleness chips wear P1.1's chip states (§12.6)", async () => {
    const { window, doc } = await openGraph(h);
    showList(window, doc);
    const index = window.OKF_INDEX;
    const detail = doc.getElementById("okf-graph-detail");
    const pick = (i) => click(window, Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).find((x) => x.textContent === index.concepts[i].id));
    const chipOf = (cls) => detail.querySelector(`.okf-chips .okf-chip.${cls}`);
    // Every concept is chosen from the executed index (§12.7): the hostile
    // bundle has verified and unverified concepts, one stale long ago and one
    // stale only in 2999.
    const now = Date.now();
    const verified = index.concepts.findIndex((c) => window.OkfShapes.trustKind(c.trust) !== null);
    const unverified = index.concepts.findIndex((c) => window.OkfShapes.trustKind(c.trust) === null);
    const since = index.concepts.findIndex((c) => typeof c.staleAfterDate === "string" && window.OkfSite.isStale(c.staleAfterMs, now));
    const after = index.concepts.findIndex((c) => typeof c.staleAfterDate === "string" && !window.OkfSite.isStale(c.staleAfterMs, now));
    h.assert(verified >= 0 && unverified >= 0 && since >= 0 && after >= 0, "this case needs a verified, an unverified, a stale and a not-yet-stale concept");
    pick(verified);
    h.assert(chipOf("okf-chip-trust") && !chipOf("okf-chip-trust").classList.contains("okf-chip-unverified"), "a verified tier is drawn as unverified");
    pick(unverified);
    h.assert(chipOf("okf-chip-trust") && chipOf("okf-chip-trust").classList.contains("okf-chip-unverified"), "the unverified tier lacks okf-chip-unverified");
    pick(since);
    const sinceChip = chipOf("okf-chip-stale");
    h.assert(sinceChip && sinceChip.hasAttribute("data-okf-stale-now") && sinceChip.querySelector("svg.okf-glyph")
      && sinceChip.textContent === `stale since ${index.concepts[since].staleAfterDate}`, "stale since is not okf-chip-stale[data-okf-stale-now] with its sandglass");
    pick(after);
    const afterChip = chipOf("okf-chip-stale");
    h.assert(afterChip && !afterChip.hasAttribute("data-okf-stale-now")
      && afterChip.textContent === `stale after ${index.concepts[after].staleAfterDate}`, "stale after is not a plain okf-chip-stale");
    h.assert(detail.querySelectorAll("[class*='okf-graph-chip']").length === 0, "the drawer wears a P3-only chip class (spec §12.6 names the states)");
  });
}

// Changes OKF_INDEX as the page assigns it, so a case can hand the page a
// bundle shape the fixture does not have (a repeated tag, many tags).
function patchIndex(patch) {
  return (w) => {
    let value;
    Object.defineProperty(w, "OKF_INDEX", {
      configurable: true,
      get() { return value; },
      set(v) { patch(v); value = v; },
    });
  };
}

function registerFacets(h) {
  h.checkAsync("facets: type, trust, freshness, tags and display, with the bundle's fixed totals", async () => {
    const DEADLINE = Date.UTC(2026, 9, 6); // edge.md: 2026-10-06T00:00:00.0001Z, rounded up to +1 ms
    const { window, doc } = await openGraph(h, { now: () => DEADLINE + 1 });
    const index = window.OKF_INDEX;
    const types = facetInputs(doc, "type");
    h.assert(types.length === index.types.length && types.every((i) => i.checked), "one checked box per entry of types");
    const typeRows = types.map((i) => i.closest("label"));
    index.types.forEach((t, k) => {
      const name = typeRows[k].querySelector(".okf-facet-name").textContent;
      const count = typeRows[k].querySelector(".okf-facet-count").textContent;
      h.assert(name === (t.name === "" ? "(no type)" : t.name) && count === String(t.count), `type row ${k}: ${name} ${count}`);
      h.assert(typeRows[k].querySelector("svg.okf-glyph"), `type row ${k} has no glyph (it is the type legend, G3)`);
    });
    const trust = facetInputs(doc, "trust");
    const tiers = ["human-reviewed", "machine-confirmed", "unverified"];
    trust.forEach((input, k) => {
      const row = input.closest("label");
      const want = index.concepts.filter((c) => c.trust === tiers[k]).length;
      h.assert(row.querySelector(".okf-facet-name").textContent === tiers[k] && row.querySelector(".okf-facet-count").textContent === String(want), `trust row ${k}`);
    });
    const [stale] = facetInputs(doc, "freshness");
    const staleWant = index.concepts.filter((c) => c.staleAfterMs !== null && DEADLINE + 1 >= c.staleAfterMs).length;
    h.assert(!stale.checked && stale.closest("label").querySelector(".okf-facet-count").textContent === String(staleWant), "freshness row");
    const chips = Array.from(doc.querySelectorAll("#okf-facets button.okf-chip"));
    const counts = new Map();
    for (const c of index.concepts) { for (const t of new Set(c.tags)) { counts.set(t, (counts.get(t) || 0) + 1); } }
    const order = Array.from(counts.keys()).sort((a, b) => counts.get(b) - counts.get(a) || (a < b ? -1 : a > b ? 1 : 0));
    h.assert(JSON.stringify(chips.map((b) => b.textContent)) === JSON.stringify(order.map((t) => `${t} ${counts.get(t)}`)), `tag chips: ${chips.map((b) => b.textContent).join(" | ")}`);
    h.assert(chips.every((b) => b.getAttribute("aria-pressed") === "false"), "a tag starts pressed");
    for (const name of ["__proto__", "constructor"]) {
      h.assert(chips.some((b) => b.textContent === `${name} 1`), `the tag ${name} is missing or merged with an inherited property`);
    }
    const display = facetInputs(doc, "display");
    h.assert(display.length === 2 && display[0].checked && !display[1].checked, "Display: labels on, dim off");
  });

  h.checkAsync("facets: AND between facets, OR inside one; a ghost stays while one shown concept cites it", async () => {
    const { window, doc } = await openGraph(h);
    showList(window, doc);
    const index = window.OKF_INDEX;
    const status = () => doc.getElementById("okf-graph-status").textContent;
    const chip = (tag) => Array.from(doc.querySelectorAll("#okf-facets button.okf-chip")).find((b) => b.textContent.startsWith(`${tag} `));
    click(window, chip("graph-core"));
    click(window, chip("constructor"));
    let shown = index.concepts.map((c, i) => (c.tags.includes("graph-core") || c.tags.includes("constructor") ? i : -1)).filter((i) => i >= 0);
    h.assert(status() === expectedStatus(index, shown, shown.length), `two tags (OR): ${status()}`);
    // AND with the type facet: drop the type of p3-graph/c.
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    setChecked(window, facetInputs(doc, "type")[cType], false);
    shown = shown.filter((i) => index.concepts[i].typeIndex !== cType);
    h.assert(status() === expectedStatus(index, shown, shown.length), `tags AND type: ${status()}`);
    // The hostile concept is the only source of p3-graph/absent-target.
    const hostile = conceptPos(index, "p3-graph/hostile");
    setChecked(window, facetInputs(doc, "type")[index.concepts[hostile].typeIndex], false);
    shown = shown.filter((i) => i !== hostile);
    h.assert(status() === expectedStatus(index, shown, shown.length), `ghost without a shown source: ${status()}`);
    const ghostRow = Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).find((b) => b.textContent === "absent: p3-graph/absent-target");
    h.assert(!ghostRow, "a ghost no shown concept cites is still listed");
    // The list restricts its relations to shown nodes (X4/R23): hostile is hidden, so b is no longer "referenced by" it.
    const bItem = Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-item")).find((li) => li.querySelector(".okf-graph-list-select").textContent === "p3-graph/b");
    h.assert(bItem, "p3-graph/b left the list although its type is shown");
    const bRelations = Array.from(bItem.querySelectorAll(".okf-graph-list-rel li"), (li) => li.textContent);
    h.assert(!bRelations.includes("referenced by p3-graph/hostile"), `a hidden concept still appears in a relation: ${bRelations.join(" | ")}`);
    h.assert(!Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).some((b) => b.textContent === "p3-graph/hostile"), "a concept of an unchecked type is listed");
  });

  h.checkAsync("facets: the trust facet filters, and dimming keeps every concept shown while the status counts only the matches", async () => {
    const { window, doc } = await openGraph(h);
    showList(window, doc);
    const index = window.OKF_INDEX;
    const status = () => doc.getElementById("okf-graph-status").textContent;
    const tiers = ["human-reviewed", "machine-confirmed", "unverified"];
    const trustBoxes = facetInputs(doc, "trust");
    setChecked(window, trustBoxes[2], false);
    const all = index.concepts.map((c, i) => i);
    const kept = all.filter((i) => index.concepts[i].trust !== tiers[2]);
    h.assert(kept.length > 0 && kept.length < all.length, "the fixture must hold both unverified and verified concepts");
    h.assert(status() === expectedStatus(index, kept, kept.length), `trust off: ${status()}`);
    const listed = () => Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select"), (b) => b.textContent);
    h.assert(listed().length < index.concepts.length + index.ghosts.length, "an unchecked tier still lists everything");
    const dim = facetInputs(doc, "display")[1];
    setChecked(window, dim, true);
    h.assert(status() === expectedStatus(index, all, kept.length), `dimming: ${status()}`);
    h.assert(all.every((i) => listed().includes(index.concepts[i].id)), "dimming dropped a concept from the list");
    setChecked(window, dim, false);
    h.assert(status() === expectedStatus(index, kept, kept.length), `dimming off again: ${status()}`);
  });

  h.checkAsync("facets: a hostile type name and tag stay text, before and after filtering by them", async () => {
    const { window, doc } = await openGraph(h);
    showList(window, doc);
    const facets = doc.getElementById("okf-facets");
    h.assert(facets.textContent.includes('<img src=x onerror="window.__pwned=31">Kind'), "the hostile type name is not shown as text");
    const tag = Array.from(facets.querySelectorAll("button.okf-chip")).find((b) => b.textContent.startsWith("<img"));
    h.assert(tag && tag.textContent.includes("window.__pwned=34"), "the hostile tag is not a chip");
    click(window, tag);
    h.assert(tag.getAttribute("aria-pressed") === "true", "the hostile tag did not press");
    const listed = Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select"), (b) => b.textContent);
    h.assert(listed.includes("p3-graph/hostile"), "filtering by the hostile tag lost its concept");
    h.assert(doc.getElementById("okf-graph-layout").querySelectorAll("img, [onerror]").length === 0, "an element or handler from bundle text reached the page");
    h.assert(window.__pwned === undefined, "bundle text executed");
  });

  h.checkAsync("facets: freshness is re-read on visibilitychange (A9)", async () => {
    const DEADLINE = Date.UTC(2026, 9, 6) + 1;
    let now = DEADLINE - 1;
    const { window, doc } = await openGraph(h, { now: () => now });
    showList(window, doc);
    const [stale] = facetInputs(doc, "freshness");
    const count = () => Number(stale.closest("label").querySelector(".okf-facet-count").textContent);
    const before = count();
    setChecked(window, stale, true);
    const listed = () => Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select"), (b) => b.textContent);
    h.assert(!listed().includes("edge"), "edge is stale before its deadline");
    now = DEADLINE;
    doc.dispatchEvent(new window.Event("visibilitychange"));
    h.assert(count() === before + 1, `stale count ${count()} after the deadline, ${before} before`);
    h.assert(listed().includes("edge"), "edge did not become stale at its deadline");
  });
  h.checkAsync("facets: a tag a concept lists twice counts that concept once (G6)", async () => {
    const { window, doc } = await openGraph(h, {
      beforeParse: patchIndex((idx) => { idx.concepts.find((c) => c.tags.includes("graph-core")).tags.push("graph-core"); }),
    });
    const index = window.OKF_INDEX;
    h.assert(index.concepts.some((c) => c.tags.filter((t) => t === "graph-core").length === 2), "the patch did not repeat the tag");
    const want = index.concepts.filter((c) => c.tags.includes("graph-core")).length;
    const chip = Array.from(doc.querySelectorAll("#okf-facets button.okf-chip")).find((b) => b.textContent.startsWith("graph-core "));
    h.assert(chip && chip.textContent === `graph-core ${want}`, `chip: ${chip && chip.textContent}, expected graph-core ${want}`);
    h.assert(chip.querySelector(".okf-facet-tag-name").textContent === "graph-core" && chip.querySelector(".okf-facet-tag-count").textContent === String(want), "the chip does not keep its name and its count in their own spans");
  });

  h.checkAsync("facets: only the first 12 tags show, then 'Show all tags (n)' reveals the rest and keeps the focus (G6)", async () => {
    const plain = await openGraph(h);
    h.assert(plain.doc.querySelectorAll("#okf-facets button.okf-chip").length <= 12 && !plain.doc.querySelector("#okf-facets .okf-facet-more"),
      "a bundle with at most 12 tags offers 'Show all tags'");
    const sections = Array.from(plain.doc.querySelectorAll("#okf-facets .okf-facet"));
    h.assert(sections.length >= 4 && sections.every((x) => x.getAttribute("role") === "group" && plain.doc.getElementById(x.getAttribute("aria-labelledby")) && x.contains(plain.doc.getElementById(x.getAttribute("aria-labelledby")))), "a facet section is not a labelled group");
    const row0 = plain.doc.querySelector("#okf-facets .okf-facet-row");
    const t0 = plain.window.OKF_INDEX.types[0];
    h.assert(row0.textContent === `${t0.name} ${t0.count}`, `the first type row reads "${row0.textContent}" (its accessible name needs a space before the count)`);
    const { window, doc } = await openGraph(h, {
      beforeParse: patchIndex((idx) => { for (let k = 0; k < 9; k++) { idx.concepts[0].tags.push(`zz-extra-${k}`); } }),
    });
    const chips = Array.from(doc.querySelectorAll("#okf-facets button.okf-chip"));
    h.assert(chips.length > 12, "the patch did not give the bundle more than 12 tags");
    h.assert(chips.filter((b) => !b.hidden).length === 12 && chips.slice(0, 12).every((b) => !b.hidden), "not exactly the first 12 chips are visible");
    const more = doc.querySelector("#okf-facets .okf-facet-more");
    h.assert(more && more.textContent === `Show all tags (${chips.length})`, `more button: ${more && more.textContent}`);
    click(window, more);
    h.assert(chips.every((b) => !b.hidden), "a chip is still hidden after 'Show all tags'");
    h.assert(!doc.querySelector("#okf-facets .okf-facet-more"), "'Show all tags' is still there");
    h.assert(doc.activeElement === chips[12], "the focus did not move to the first revealed tag");
  });

  h.checkAsync("facets: staleness is not re-read while the page is hidden (A9)", async () => {
    const DEADLINE = Date.UTC(2026, 9, 6) + 1;
    let now = DEADLINE - 1;
    let visibility = "visible";
    const { window, doc } = await openGraph(h, {
      now: () => now,
      beforeParse(w) { Object.defineProperty(w.document, "visibilityState", { configurable: true, get: () => visibility }); },
    });
    const [stale] = facetInputs(doc, "freshness");
    const count = () => Number(stale.closest("label").querySelector(".okf-facet-count").textContent);
    const before = count();
    now = DEADLINE;
    visibility = "hidden";
    doc.dispatchEvent(new window.Event("visibilitychange"));
    h.assert(count() === before, `a hidden page re-read the clock: ${count()}, was ${before}`);
    visibility = "visible";
    doc.dispatchEvent(new window.Event("visibilitychange"));
    h.assert(count() === before + 1, `coming back to the front: ${count()}, expected ${before + 1}`);
  });

  h.checkAsync("facets: below 1100px the facets fold into 'Filters' and open again when the viewport widens (G19)", async () => {
    // One stub per media query: the theme script asks for others too.
    const queries = new Map();
    const stub = (matches) => (w) => {
      w.matchMedia = (text) => {
        if (!queries.has(text)) {
          const q = { matches: text === WIDE ? matches : false, listeners: [] };
          q.addEventListener = (type, fn) => { if (type === "change") { q.listeners.push(fn); } };
          q.removeEventListener = () => {};
          q.addListener = (fn) => { q.listeners.push(fn); };
          queries.set(text, q);
        }
        return queries.get(text);
      };
    };
    const WIDE = "(min-width: 1100px)";
    const { doc } = await openGraph(h, { beforeParse: stub(false) });
    const details = doc.querySelector("#okf-facets details");
    h.assert(details && details.querySelector("summary").textContent === "Filters", "no 'Filters' disclosure");
    h.assert(!details.open, "the facets start open on a narrow viewport");
    const query = queries.get(WIDE);
    h.assert(query && query.listeners.length > 0, "the page does not follow the media query");
    query.matches = true;
    query.listeners.forEach((fn) => fn());
    h.assert(details.open, "the facets did not open when the viewport widened");
    queries.clear();
    const wide = await openGraph(h, { beforeParse: stub(true) });
    h.assert(wide.doc.querySelector("#okf-facets details").open, "the facets start closed on a wide viewport");
  });

  h.checkAsync("facets: the list is built only when shown, and only when the shown set changed", async () => {
    const { window, doc } = await openGraph(h);
    const index = window.OKF_INDEX;
    const list = doc.getElementById("okf-graph-list");
    const items = () => list.querySelectorAll(".okf-graph-list-item");
    h.assert(items().length === 0, "the list is built while hidden");
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    setChecked(window, facetInputs(doc, "type")[cType], false);
    h.assert(items().length === 0, "a filter change built the hidden list");
    showList(window, doc);
    const names = () => Array.from(list.querySelectorAll(".okf-graph-list-select"), (b) => b.textContent);
    const wantAfter = (type) => index.concepts.filter((c) => c.typeIndex !== type).map((c) => c.id);
    h.assert(wantAfter(cType).every((id) => names().includes(id)) && !index.concepts.some((c) => c.typeIndex === cType && names().includes(c.id)),
      "the list built on show ignores the filters set while it was hidden");
    const first = items()[0];
    setChecked(window, facetInputs(doc, "display")[0], false);
    h.assert(items()[0] === first, "Node labels rebuilt a list whose entries did not change");
    setChecked(window, facetInputs(doc, "type")[cType], true);
    h.assert(items()[0] !== first && names().length > index.concepts.filter((c) => c.typeIndex !== cType).length, "a changed shown set did not rebuild the visible list");
    click(window, doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle"));
    setChecked(window, facetInputs(doc, "type")[cType], false);
    showList(window, doc);
    h.assert(!index.concepts.some((c) => c.typeIndex === cType && names().includes(c.id)), "re-opening the list showed a stale one");
  });
}

// The vocabulary of §12.2, for P3's SVG: element names, attribute names, and
// the only values `id` and `transform` may take.
const SVG_NS = "http://www.w3.org/2000/svg";
const SVG_ELEMENTS = new Set(["svg", "g", "circle", "rect", "path", "line", "text", "title", "defs", "marker"]);
const SVG_ATTRIBUTES = new Set(["viewBox", "width", "height", "class", "aria-hidden", "focusable", "role", "aria-label", "aria-current",
  "tabindex", "id", "cx", "cy", "r", "x", "y", "x1", "y1", "x2", "y2", "d", "stroke-width", "stroke-dasharray",
  "text-anchor", "dominant-baseline", "marker-end", "refX", "refY", "markerWidth", "markerHeight", "orient", "transform"]);

function assertFixedSvg(h, doc) {
  const layout = doc.getElementById("okf-graph-layout");
  for (const svg of doc.querySelectorAll("svg")) {
    h.assert(layout.contains(svg) || svg.closest("#okf-tools") || svg.closest("body > .okf-palette-backdrop"),
      "an <svg> sits outside the chrome containers");
  }
  for (const node of layout.querySelectorAll("*")) {
    if (node.namespaceURI !== SVG_NS) { continue; }
    h.assert(SVG_ELEMENTS.has(node.localName), `SVG element <${node.localName}> is outside §12.2`);
    for (const attr of Array.from(node.attributes)) {
      h.assert(SVG_ATTRIBUTES.has(attr.name), `attribute ${attr.name} on <${node.localName}> is outside §12.2`);
      // Added by Task 7 (selection for assistive technology); the one value is "true".
      if (attr.name === "aria-current") { h.assert(attr.value === "true" && node.localName === "g", `aria-current="${attr.value}" on <${node.localName}>`); }
      if (attr.name === "id") { h.assert(/^okf-[a-z0-9-]+$/.test(attr.value), `id "${attr.value}" is not a fixed okf- value`); }
      if (attr.name === "class") {
        h.assert(attr.value.split(/\s+/).every((c) => /^okf-[a-z0-9-]+$/.test(c)), `class "${attr.value}" is not made of fixed okf- names`);
      }
      if (attr.name === "transform") {
        // The viewport's pan/zoom, or (revision 12) a node's position.
        const viewport = node.localName === "g" && node.classList.contains("okf-graph-viewport");
        const placed = node.localName === "g" && node.classList.contains("okf-node");
        h.assert((viewport && /^translate\(-?[0-9.]+ -?[0-9.]+\) scale\([0-9.]+\)$/.test(attr.value))
          || (placed && /^translate\(-?[0-9.]+ -?[0-9.]+\)$/.test(attr.value)), `transform "${attr.value}" on <${node.localName} class="${node.getAttribute("class")}">`);
      }
    }
  }
}

function viewportTransform(doc) {
  const m = /^translate\((-?[0-9.]+) (-?[0-9.]+)\) scale\(([0-9.]+)\)$/.exec(
    doc.querySelector("#okf-graph-canvas g.okf-graph-viewport").getAttribute("transform"));
  return { a: Number(m[1]), b: Number(m[2]), s: Number(m[3]) };
}

// The geometry of the drawing: view transform, node shapes and label
// positions, edge ends. Not the roving tabindex, which legitimately depends
// on where the reader went before.
function drawingText(doc) {
  const parts = [doc.querySelector("#okf-graph-canvas g.okf-graph-viewport").getAttribute("transform")];
  for (const { g, name } of drawnNodes(doc)) {
    const p = labelPoint(g);
    parts.push(`${name}@${p.x},${p.y}`);
  }
  for (const l of doc.querySelectorAll("#okf-graph-canvas line")) {
    parts.push(["x1", "y1", "x2", "y2"].map((a) => l.getAttribute(a)).join(","));
  }
  return parts.join("|");
}

// okf-sim.js with NODE_LIMIT lowered, to reach the threshold with the
// fixture; refuses to run if the line it patches has changed.
function lowLimitSim(limit) {
  const source = fs.readFileSync(SIM_FILE, "utf8");
  const line = "var NODE_LIMIT = 1500;";
  if (!source.includes(line)) { throw new Error(`okf-sim.js no longer holds "${line}": update lowLimitSim`); }
  return source.replace(line, `var NODE_LIMIT = ${limit};`);
}

// okf-sim.js with its default slice of work lowered, so a layout needs many
// frames; refuses to run if the line it patches has changed.
function slicedSim(work, source = fs.readFileSync(SIM_FILE, "utf8")) {
  const line = "sliceWork: 100000,";
  if (!source.includes(line)) { throw new Error(`okf-sim.js no longer holds "${line}": update slicedSim`); }
  return source.replace(line, `sliceWork: ${work},`);
}

// okf-sim.js followed by a wrapper that records each simulation the page
// creates and whether it was cancelled (window.OKF_TEST_SIMS).
function recordingSim(source) {
  return `${source}\n;(function () {
  var real = window.OkfSim;
  var made = [];
  window.OKF_TEST_SIMS = made;
  window.OkfSim = Object.freeze({
    NODE_LIMIT: real.NODE_LIMIT,
    create: function (graph, options) {
      var sim = real.create(graph, options);
      if (sim === null) { return null; }
      var record = { cancelled: false };
      made.push(record);
      return Object.freeze({
        step: function () { return sim.step(); },
        positions: function () { return sim.positions(); },
        stats: function () { return sim.stats(); },
        cancel: function () { record.cancelled = true; return sim.cancel(); },
      });
    },
  });
})();`;
}

// okf-sim.js followed by a wrapper that keeps a JSON copy of every graph the
// page hands to create().
function graphRecordingSim(source) {
  return `${source}
;(function () {
  var real = window.OkfSim;
  var seen = [];
  window.OKF_TEST_GRAPHS = seen;
  window.OkfSim = Object.freeze({
    NODE_LIMIT: real.NODE_LIMIT,
    create: function (graph, options) {
      seen.push(JSON.parse(JSON.stringify(graph)));
      return real.create(graph, options);
    },
  });
})();`;
}

// jsdom has no layout: the graph canvas gets a client size while it is shown;
// a hidden element measures 0, as in a browser.
function sizedCanvas(width, height) {
  return (w) => {
    for (const [name, value] of [["clientWidth", width], ["clientHeight", height]]) {
      Object.defineProperty(w.HTMLElement.prototype, name, {
        configurable: true,
        get() { return this.id === "okf-graph-canvas" && !this.hidden ? value : 0; },
      });
    }
  };
}

// The drawn nodes in canvas pixels: each shape (centre and half size) and its
// label (about 6.9 px an advance in Space Mono 11.5, ending 20 under the shape).
function fittedBox(window, doc) {
  const t = viewportTransform(doc);
  const index = window.OKF_INDEX;
  const box = { minX: Infinity, minY: Infinity, maxX: -Infinity, maxY: -Infinity, scale: t.s };
  for (const { g, name } of drawnNodes(doc)) {
    const ghost = name.startsWith("absent: ");
    const kind = ghost ? "ghost" : window.OkfShapes.kindOf(index, conceptPos(index, name));
    const half = window.OkfShapes.SIZES.graph[kind].size / 2;
    const p = labelPoint(g);
    const cy = p.y - half - 16;
    const wide = Math.max(half, g.querySelector("text").textContent.length * 6.9 / 2);
    box.minX = Math.min(box.minX, t.a + (p.x - wide) * t.s);
    box.maxX = Math.max(box.maxX, t.a + (p.x + wide) * t.s);
    box.minY = Math.min(box.minY, t.b + (cy - half) * t.s);
    box.maxY = Math.max(box.maxY, t.b + (cy + half + 20) * t.s);
  }
  return box;
}

// The fit leaves 24 px beside the drawing and 56 above and below it (the
// status line and zoom box sit above, the legend below, nothing beside), and
// touches them on the tighter axis.
const FIT_SIDE = 24;
const FIT_TOP = 56;
const FIT_CAP = 1.25;

function assertFitted(h, window, doc, width, height, when) {
  const b = fittedBox(window, doc);
  const eps = 2;
  h.assert(b.scale < FIT_CAP, `${when}: the fit hit its ${FIT_CAP}x cap (${b.scale}): the canvas is too large for this check`);
  h.assert(b.minX >= FIT_SIDE - eps && b.maxX <= width - FIT_SIDE + eps && b.minY >= FIT_TOP - eps && b.maxY <= height - FIT_TOP + eps,
    `${when}: the drawing spans x ${b.minX.toFixed(1)}..${b.maxX.toFixed(1)}, y ${b.minY.toFixed(1)}..${b.maxY.toFixed(1)} in a ${width} x ${height} canvas`);
  h.assert(b.maxX - b.minX >= width - 2 * FIT_SIDE - eps || b.maxY - b.minY >= height - 2 * FIT_TOP - eps,
    `${when}: the drawing fills neither axis of a ${width} x ${height} canvas: it was fitted to another size`);
}

// G11/G13 as revision 11 cuts them: a label longer than 24 code points keeps
// its first 23 and an ellipsis (never half a surrogate pair).
const LABEL_MAX = 24;
const ELLIPSIS = String.fromCharCode(0x2026);

function cutLabel(text) {
  const points = Array.from(text);
  return points.length > LABEL_MAX ? points.slice(0, LABEL_MAX - 1).join("") + ELLIPSIS : text;
}

// The label a node must carry, from the index alone.
function expectedLabel(index, slot) {
  const N = index.concepts.length;
  if (slot >= N) { return cutLabel(`absent: ${index.ghosts[slot - N].id}`); }
  const id = index.concepts[slot].id;
  return cutLabel(id.slice(id.lastIndexOf("/") + 1));
}

// The boxes the page must hand okf-sim for the whole bundle drawn: per node
// [halfWidth, above, below], the shape (graph context) and its label (7 an
// advance per code point, ending 20 under the shape).
function expectedBoxes(window) {
  const index = window.OKF_INDEX;
  const N = index.concepts.length;
  const out = [];
  for (let slot = 0; slot < N + index.ghosts.length; slot++) {
    const kind = slot < N ? window.OkfShapes.kindOf(index, slot) : "ghost";
    const half = window.OkfShapes.SIZES.graph[kind].size / 2;
    const chars = Array.from(expectedLabel(index, slot)).length;
    out.push([Math.max(half, chars * 7 / 2), half, half + 20]);
  }
  return out;
}

// What the drawn nodes cover in graph units, as a browser draws them: each
// shape (centre, half size) and its label (Space Mono 11.5: 0.612 em = 7.04
// an advance; from 12.7 above the baseline to 4.4 below it, Chromium's box).
// Returns the label/label, label/shape and shape/shape pairs that overlap.
function drawnOverlaps(window, doc) {
  const index = window.OKF_INDEX;
  const items = drawnNodes(doc).map(({ g, name }) => {
    const kind = name.startsWith("absent: ") ? "ghost" : window.OkfShapes.kindOf(index, conceptPos(index, name));
    const half = window.OkfShapes.SIZES.graph[kind].size / 2;
    const p = labelPoint(g);
    const cy = p.y - half - 16;
    const w = Array.from(g.querySelector("text").textContent).length * 7.04 / 2;
    return {
      name,
      shape: { l: p.x - half, r: p.x + half, t: cy - half, b: cy + half },
      label: { l: p.x - w, r: p.x + w, t: p.y - 12.7, b: p.y + 4.4 },
    };
  });
  const hit = (a, b) => Math.min(a.r, b.r) - Math.max(a.l, b.l) > 0 && Math.min(a.b, b.b) - Math.max(a.t, b.t) > 0;
  const found = [];
  for (let i = 0; i < items.length; i++) {
    for (let j = 0; j < items.length; j++) {
      if (i === j) { continue; }
      const a = items[i];
      const b = items[j];
      if (i < j && hit(a.label, b.label)) { found.push(`label ${a.name} / label ${b.name}`); }
      if (hit(a.label, b.shape)) { found.push(`label ${a.name} / shape ${b.name}`); }
      if (i < j && hit(a.shape, b.shape)) { found.push(`shape ${a.name} / shape ${b.name}`); }
    }
  }
  return found;
}

// The node count of "only p3-graph/d's type" and that type's index: the
// limit at which that narrowing draws and the whole bundle does not.
async function narrowedLimit(h) {
  const probe = (await h.openPage("index.html")).OKF_INDEX;
  const keepType = probe.concepts[conceptPos(probe, "p3-graph/d")].typeIndex;
  const kept = probe.concepts.map((c, i) => (c.typeIndex === keepType ? i : -1)).filter((i) => i >= 0);
  const ghostsKept = new Set(probe.edges.filter((e) => e[3] === 1 && kept.includes(e[0])).map((e) => e[1]));
  return { keepType, count: kept.length + ghostsKept.size };
}

// The node count of "only p3-graph/d's type" and that type's index: the
// limit at which that narrowing draws and the whole bundle does not.
async function narrowedLimit(h) {
  const probe = (await h.openPage("index.html")).OKF_INDEX;
  const keepType = probe.concepts[conceptPos(probe, "p3-graph/d")].typeIndex;
  const kept = probe.concepts.map((c, i) => (c.typeIndex === keepType ? i : -1)).filter((i) => i >= 0);
  const ghostsKept = new Set(probe.edges.filter((e) => e[3] === 1 && kept.includes(e[0])).map((e) => e[1]));
  return { keepType, count: kept.length + ghostsKept.size };
}

function registerDrawing(h) {
  h.checkAsync("drawing: the injected scheduler drives the layout to its end; nothing is timed", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    const canvas = doc.getElementById("okf-graph-canvas");
    h.assert(scheduler.pending() > 0 && canvas.getAttribute("data-okf-layout") === "running", "the layout did not wait for the scheduler");
    scheduler.flush();
    h.assert(canvas.getAttribute("data-okf-layout") === "done", "the layout did not end");
    const index = window.OKF_INDEX;
    const names = drawnNodes(doc).map((n) => n.name);
    const want = index.concepts.map((c) => c.id).concat(index.ghosts.map((g) => `absent: ${g.id}`));
    h.assert(JSON.stringify(names) === JSON.stringify(want), `drawn nodes: ${names.join(" | ")}`);
    for (const { g, name } of drawnNodes(doc)) {
      const p = labelPoint(g);
      h.assert(Number.isFinite(p.x) && Number.isFinite(p.y), `${name} is at a non-finite position`);
    }
    assertFixedSvg(h, doc);
  });

  h.checkAsync("drawing: the page draws okf-sim's layout of the visible graph, nodes, links and boxes in index order", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    const index = window.OKF_INDEX;
    const N = index.concepts.length;
    const edges = index.edges.filter(([f, t, , g]) => !(g === 0 && f === t)).map(([f, t, , g]) => [f, g === 1 ? N + t : t]);
    const boxes = expectedBoxes(window);
    const expected = loadSim().create({ nodeCount: N + index.ghosts.length, edges, boxes });
    runToEnd(h, expected, 100000, "expected layout");
    const positions = expected.positions();
    const initial = drawnNodes(doc).map((n) => labelPoint(n.g).x);
    scheduler.flush();
    const nodes = drawnNodes(doc);
    const round = (v) => Math.round(v * 100) / 100;
    nodes.forEach(({ g, name }, slot) => {
      const p = labelPoint(g);
      h.assert(Math.abs(p.x - round(positions[2 * slot])) < 0.006, `${name} is drawn at x ${p.x}, the layout says ${positions[2 * slot]}`);
      // The label sits at cy + size / 2 + 16 (G11): y checks the layout and the baseline.
      const size = window.OkfShapes.SIZES.graph[slot < N ? window.OkfShapes.kindOf(index, slot) : "ghost"].size;
      h.assert(Math.abs(p.y - (round(positions[2 * slot + 1]) + size / 2 + 16)) < 0.02, `${name} is drawn at y ${p.y}, the layout says ${positions[2 * slot + 1]} (+ ${size / 2 + 16})`);
    });
    h.assert(nodes.some((n, slot) => n.g && Math.abs(labelPoint(n.g).x - initial[slot]) > 1), "the drawing never left the initial layout");
  });

  h.checkAsync("drawing: hostile titles stay inert in node names; the SVG keeps its fixed vocabulary", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const g = nodeNamed(doc, "p3-graph/hostile");
    h.assert(g && g.getAttribute("aria-label").startsWith('<img src=x onerror="window.__pwned=32">Hostile'), "the hostile title is not the node's name");
    click(window, g);
    h.assert(doc.getElementById("okf-graph-layout").querySelectorAll("img, [onerror]").length === 0, "an element or handler from bundle text reached the page");
    h.assert(window.__pwned === undefined, "bundle text executed");
    assertFixedSvg(h, doc);
  });

  h.checkAsync("drawing: a layout cut into many frames ends the same as one cut into few, and stays running until its last frame", async () => {
    const whole = await openGraph(h);
    whole.scheduler.flush();
    const wholeFrames = whole.scheduler.ran();
    const sliced = await openGraph(h, { override: { "assets/okf-sim.js": slicedSim(300) } });
    const canvas = sliced.doc.getElementById("okf-graph-canvas");
    let frames = 0;
    while (sliced.scheduler.pending() > 0) {
      h.assert(canvas.getAttribute("data-okf-layout") === "running", `the layout read "${canvas.getAttribute("data-okf-layout")}" before its last frame (frame ${frames})`);
      sliced.scheduler.frames(1);
      frames++;
      h.assert(frames < 100000, "the sliced layout did not end");
    }
    h.assert(frames >= wholeFrames + 10, `the patched simulation ran ${frames} frames, the default ${wholeFrames}: slicing is not exercised`);
    h.assert(canvas.getAttribute("data-okf-layout") === "done", "the sliced layout never read done");
    h.assert(drawingText(whole.doc) === drawingText(sliced.doc), "two slicings of one graph drew two different layouts");
  });

  h.checkAsync("drawing: rapid filter changes cancel the running layout; the last one wins", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.frames(1);
    const types = facetInputs(doc, "type");
    for (let k = 0; k < 6; k++) {
      setChecked(window, types[k % types.length], false);
      setChecked(window, types[k % types.length], true);
    }
    setChecked(window, types[0], false);
    const before = scheduler.ran();
    // A stale frame must not touch the page: the layout reads "done" only
    // once the last layout's own last frame has run.
    const canvas = doc.getElementById("okf-graph-canvas");
    while (scheduler.pending() > 0) {
      scheduler.frames(1);
      if (canvas.getAttribute("data-okf-layout") === "done") {
        h.assert(scheduler.pending() === 0, "a cancelled layout's frame marked the layout done");
      }
    }
    const reference = await openGraph(h);
    setChecked(reference.window, facetInputs(reference.doc, "type")[0], false);
    const referenceFrames = reference.scheduler.pending();
    const refBefore = reference.scheduler.ran();
    reference.scheduler.flush();
    h.assert(drawingText(doc) === drawingText(reference.doc), "the interrupted page drew another graph than a direct one");
    // Each cancelled layout returns at its next frame without stepping or
    // rescheduling: at most one extra callback per cancelled generation.
    const extra = (scheduler.ran() - before) - (reference.scheduler.ran() - refBefore);
    h.assert(extra <= 13 && referenceFrames >= 1, `${extra} extra frames ran after twelve cancelled layouts`);
  });

  h.checkAsync("drawing: edges -- one per merged link, opposite pair offset, ghost links dashed, no self-loop line", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const index = window.OKF_INDEX;
    const lines = Array.from(doc.querySelectorAll("#okf-graph-canvas line.okf-graph-edge"));
    const selfLoops = index.edges.filter(([f, t, , g]) => g === 0 && f === t).length;
    h.assert(lines.length === index.edges.length - selfLoops, `${lines.length} lines for ${index.edges.length} merged links (${selfLoops} self-loop)`);
    const ghostLines = lines.filter((l) => l.classList.contains("okf-graph-edge-ghost"));
    h.assert(ghostLines.length === index.edges.filter((e) => e[3] === 1).length, "ghost links are not all marked");
    for (const l of lines) {
      h.assert(["x1", "y1", "x2", "y2"].every((a) => Number.isFinite(Number(l.getAttribute(a)))), "an edge has a non-finite end");
      h.assert(l.getAttribute("marker-end") === "url(#okf-graph-arrow)", "an unselected edge lacks its arrow");
    }
    // twin-a <-> twin-b: lines are drawn in index edge order, so the two
    // opposite links are found by position; they are parallel, 6 apart
    // (each shifted 3 to its own left).
    const ta = conceptPos(index, "p3-graph/twin-a");
    const tb = conceptPos(index, "p3-graph/twin-b");
    const drawn = index.edges.filter(([f, t, , g]) => !(g === 0 && f === t));
    const ab = lines[drawn.findIndex(([f, t, , g]) => g === 0 && f === ta && t === tb)];
    const ba = lines[drawn.findIndex(([f, t, , g]) => g === 0 && f === tb && t === ta)];
    h.assert(ab && ba, "the twin links are not drawn");
    const end = (l, n) => Number(l.getAttribute(n));
    const dx = end(ab, "x2") - end(ab, "x1");
    const dy = end(ab, "y2") - end(ab, "y1");
    const length = Math.sqrt(dx * dx + dy * dy);
    const px = (end(ba, "x1") + end(ba, "x2")) / 2 - end(ab, "x1");
    const py = (end(ba, "y1") + end(ba, "y2")) / 2 - end(ab, "y1");
    const gap = Math.abs(px * dy - py * dx) / length;
    h.assert(Math.abs(gap - 6) < 0.05, `the opposite links are ${gap} apart, not 6`);
  });

  h.checkAsync("drawing: dim keeps unmatched nodes drawn, dimmed and counted; labels can be hidden", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const index = window.OKF_INDEX;
    const total = drawnNodes(doc).length;
    setChecked(window, facetInputs(doc, "display")[1], true);
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    setChecked(window, facetInputs(doc, "type")[cType], false);
    scheduler.flush();
    h.assert(drawnNodes(doc).length === total, "dim mode removed nodes");
    const dimmed = drawnNodes(doc).filter((n) => n.g.classList.contains("okf-graph-dim")).map((n) => n.name);
    const want = index.concepts.filter((c) => c.typeIndex === cType).map((c) => c.id);
    h.assert(JSON.stringify(dimmed) === JSON.stringify(want), `dimmed: ${dimmed.join(", ")}`);
    const matched = index.concepts.length - want.length;
    h.assert(doc.getElementById("okf-graph-status").textContent.startsWith(`showing ${matched} of `), "the status does not count matches in dim mode");
    setChecked(window, facetInputs(doc, "display")[0], false);
    h.assert(doc.querySelector("#okf-graph-canvas svg").classList.contains("okf-graph-nolabels"), "labels were not hidden");
  });

  h.checkAsync("drawing: above NODE_LIMIT nothing is drawn, the list stands in, and narrowing draws again", async () => {
    // The limit is the node count of "only p3-graph/d's type": that
    // narrowing must draw, the whole bundle must not.
    const probe = (await h.openPage("index.html")).OKF_INDEX;
    const keepType = probe.concepts[conceptPos(probe, "p3-graph/d")].typeIndex;
    const kept = probe.concepts.map((c, i) => (c.typeIndex === keepType ? i : -1)).filter((i) => i >= 0);
    const ghostsKept = new Set(probe.edges.filter((e) => e[3] === 1 && kept.includes(e[0])).map((e) => e[1]));
    const limit = kept.length + ghostsKept.size;
    const all = probe.concepts.length + probe.ghosts.length;
    h.assert(limit >= 2 && all > limit, `the fixture no longer separates the narrowed graph (${limit}) from the whole (${all})`);
    const { window, doc, scheduler } = await openGraph(h, { override: { "assets/okf-sim.js": lowLimitSim(limit) } });
    const index = window.OKF_INDEX;
    const status = doc.getElementById("okf-graph-status");
    h.assert(status.textContent === `${all} concepts match ${EMDASH} narrow the filters to draw the graph`, `status: ${status.textContent}`);
    h.assert(doc.querySelector("#okf-graph-canvas svg") === null && scheduler.pending() === 0, "something was drawn or scheduled above the limit");
    const list = doc.getElementById("okf-graph-list");
    const toggle = doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
    h.assert(!list.hidden && toggle.disabled && toggle.getAttribute("aria-pressed") === "true", "the list does not stand in for the drawing");
    facetInputs(doc, "type").forEach((input, k) => { if (k !== keepType) { setChecked(window, input, false); } });
    scheduler.flush();
    h.assert(drawnNodes(doc).length === limit && list.hidden && !toggle.disabled, "narrowing to the limit did not draw");
  });

  h.checkAsync("drawing: the view is fitted to the canvas's real size, labels included, and again when the canvas comes back from the list", async () => {
    const W = 340;
    const H = 260;
    const { window, doc, scheduler } = await openGraph(h, { beforeParse: sizedCanvas(W, H) });
    scheduler.flush();
    assertFitted(h, window, doc, W, H, "after the first layout");
    // A hidden canvas measures 0: whatever is laid out meanwhile is fitted to
    // the 800 x 600 fallback, so showing the canvas must fit again.
    const toggle = doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
    click(window, toggle);
    h.assert(doc.getElementById("okf-graph-canvas").hidden, "List did not hide the canvas");
    const index = window.OKF_INDEX;
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    setChecked(window, facetInputs(doc, "type")[cType], false);
    scheduler.flush();
    click(window, toggle);
    h.assert(!doc.getElementById("okf-graph-canvas").hidden, "List did not show the canvas again");
    assertFitted(h, window, doc, W, H, "after coming back from the list");
    // A tall, narrow canvas: the width is the tighter axis, so the drawing
    // runs from side margin to side margin.
    const tall = await openGraph(h, { beforeParse: sizedCanvas(300, 900) });
    tall.scheduler.flush();
    assertFitted(h, tall.window, tall.doc, 300, 900, "on a tall canvas");
    const b = fittedBox(tall.window, tall.doc);
    h.assert(b.maxX - b.minX >= 300 - 2 * FIT_SIDE - 2, `on a tall canvas the drawing spans ${(b.maxX - b.minX).toFixed(1)} px of 300, not side margin to side margin`);
  });

  h.checkAsync("drawing: the first fit after the node limit is the canvas's real size, not the fallback", async () => {
    const limit = await narrowedLimit(h);
    // Low enough that even the first, compact layout is fitted under the cap.
    const W = 340;
    const H = 180;
    const { window, doc, scheduler } = await openGraph(h, { override: { "assets/okf-sim.js": lowLimitSim(limit.count) }, beforeParse: sizedCanvas(W, H) });
    facetInputs(doc, "type").forEach((input, k) => { if (k !== limit.keepType) { setChecked(window, input, false); } });
    h.assert(drawnNodes(doc).length === limit.count, "narrowing to the limit did not draw");
    // before any frame: the first fit, made while the canvas was still hidden
    assertFitted(h, window, doc, W, H, "at the first draw after the limit");
    scheduler.flush();
    assertFitted(h, window, doc, W, H, "after the layout");
  });

  h.checkAsync("drawing: a label wider than its shape counts in the fit", async () => {
    const W = 340;
    const H = 260;
    // Two linked concepts whose labels are far wider than their shapes: the
    // labels, not the shapes, set the horizontal extent.
    const long = patchIndex((idx) => {
      idx.concepts = idx.concepts.slice(0, 2);
      idx.concepts[0].id = `p3-graph/${"a".repeat(34)}`;
      idx.concepts[1].id = `p3-graph/${"b".repeat(34)}`;
      idx.ghosts = [];
      idx.edges = [[0, 1, 1, 0]];
    });
    const { window, doc, scheduler } = await openGraph(h, { beforeParse: (w) => { sizedCanvas(W, H)(w); long(w); } });
    scheduler.flush();
    h.assert(drawnNodes(doc).length === 2, "the patched bundle does not draw two nodes");
    const b = fittedBox(window, doc);
    h.assert(b.minX >= FIT_SIDE - 2 && b.maxX <= W - FIT_SIDE + 2, `a label sticks out: the drawing spans x ${b.minX.toFixed(1)}..${b.maxX.toFixed(1)}`);
  });

  h.checkAsync("drawing: Fit never draws above 1.25x, so a small graph's labels stay near their 11.5 design size", async () => {
    const W = 1400;
    const H = 900;
    const { doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": namedIndexSource(["n/a", "n/b"], [[0, 1, 1, 0]]) },
      beforeParse: sizedCanvas(W, H),
    });
    scheduler.flush();
    h.assert(drawnNodes(doc).length === 2, "setup: two nodes are not drawn");
    const t = viewportTransform(doc);
    h.assert(t.s === FIT_CAP, `two nodes on a ${W} x ${H} canvas are drawn at ${t.s}x, not at the ${FIT_CAP}x cap`);
  });

  h.checkAsync("drawing: the fixture's opening view has no label over another label or a shape, and no shape over another", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    h.assert(drawnNodes(doc).length === window.OKF_INDEX.concepts.length + window.OKF_INDEX.ghosts.length, "setup: the whole fixture is not drawn");
    const found = drawnOverlaps(window, doc);
    h.assert(found.length === 0, `${found.length} overlap(s): ${found.slice(0, 6).join("; ")}`);
  });

  h.checkAsync("drawing: a label longer than 24 code points keeps 23 and an ellipsis; the <title>, the name and the box follow", async () => {
    const smile = String.fromCodePoint(0x1F600);
    const ids = [
      `n/${"a".repeat(30)}`,
      `n/${"b".repeat(24)}`,
      `n/${"c".repeat(22)}${smile}x`,
      `n/${"d".repeat(22)}${smile}xy`,
      "n/short",
    ];
    const { window, doc, scheduler } = await openGraph(h, {
      override: {
        "assets/okf-index.js": namedIndexSource(ids, [[0, 1, 1, 0], [1, 2, 1, 0], [2, 3, 1, 0], [3, 4, 1, 0]]),
        "assets/okf-sim.js": graphRecordingSim(fs.readFileSync(SIM_FILE, "utf8")),
      },
    });
    scheduler.flush();
    const want = [
      `${"a".repeat(23)}${ELLIPSIS}`,
      "b".repeat(24),
      `${"c".repeat(22)}${smile}x`,
      `${"d".repeat(22)}${smile}${ELLIPSIS}`,
      "short",
    ];
    drawnNodes(doc).forEach(({ g, name }, slot) => {
      const label = g.querySelector("text").textContent;
      h.assert(label === want[slot], `${ids[slot]}: label ${JSON.stringify(label)}, expected ${JSON.stringify(want[slot])}`);
      h.assert(name === ids[slot] && g.getAttribute("aria-label").startsWith(`T ${ids[slot]}`), `${ids[slot]}: the title or the name lost the whole id`);
    });
    const graphs = window.OKF_TEST_GRAPHS;
    h.assert(graphs.length === 1, `${graphs.length} simulations started`);
    const boxes = graphs[0].boxes;
    const half = window.OkfShapes.SIZES.graph[window.OkfShapes.kindOf(window.OKF_INDEX, 0)].size / 2;
    const expected = want.map((text) => [Math.max(half, Array.from(text).length * 7 / 2), half, half + 20]);
    h.assert(JSON.stringify(boxes) === JSON.stringify(expected), `boxes handed to okf-sim: ${JSON.stringify(boxes)}, expected ${JSON.stringify(expected)}`);
  });

  h.checkAsync("drawing: going over the limit mid-layout cancels the simulation and leaves nothing behind; a rebuild cancels the old one", async () => {
    const limit = await narrowedLimit(h);
    const { window, doc, scheduler } = await openGraph(h, { override: { "assets/okf-sim.js": recordingSim(slicedSim(40, lowLimitSim(limit.count))) } });
    const types = facetInputs(doc, "type");
    types.forEach((input, k) => { if (k !== limit.keepType) { setChecked(window, input, false); } });
    const sims = window.OKF_TEST_SIMS;
    h.assert(sims.length === 1 && !sims[0].cancelled, "narrowing to the limit did not start one simulation");
    scheduler.frames(1);
    // Another type, then dropped again: new layouts, each old one cancelled.
    const other = (limit.keepType + 1) % types.length;
    setChecked(window, types[other], true);
    setChecked(window, types[other], false);
    h.assert(sims.length >= 2 && sims.slice(0, -1).every((s) => s.cancelled) && !sims[sims.length - 1].cancelled,
      `rebuilds left ${sims.filter((s) => !s.cancelled).length} of ${sims.length} simulations running`);
    scheduler.frames(1);
    // Over the limit, mid-layout.
    setChecked(window, types[other], true);
    setChecked(window, types[(other + 1) % types.length], true);
    const canvas = doc.getElementById("okf-graph-canvas");
    h.assert(canvas.querySelector("svg") === null && !canvas.hasAttribute("data-okf-layout"), "the drawing or its layout mark survived the limit");
    h.assert(sims.every((s) => s.cancelled), "a simulation was left running over the limit");
    // Stale frames must not touch the page.
    scheduler.flush();
    h.assert(canvas.querySelector("svg") === null && !canvas.hasAttribute("data-okf-layout"), "a stale frame drew over the limit");
  });

  h.checkAsync("drawing: a click selects the node; its out links are blue and end at the target, its in links are blue too, from their own source (G14)", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const index = window.OKF_INDEX;
    const N = index.concepts.length;
    const ta = conceptPos(index, "p3-graph/twin-a");
    click(window, nodeNamed(doc, "p3-graph/twin-a"));
    const selected = drawnNodes(doc).filter((n) => n.g.classList.contains("okf-selected")).map((n) => n.name);
    h.assert(JSON.stringify(selected) === JSON.stringify(["p3-graph/twin-a"]), `selected: ${selected.join(", ")}`);
    const lines = Array.from(doc.querySelectorAll("#okf-graph-canvas line.okf-graph-edge"));
    const drawn = index.edges.filter(([f, t, , g]) => !(g === 0 && f === t));
    h.assert(lines.length === drawn.length, "edge count changed");
    const nodes = drawnNodes(doc);
    const radius = (key) => window.OkfShapes.SIZES.graph[key < N ? window.OkfShapes.kindOf(index, key) : "ghost"].size / 2;
    // the shape's centre: the label is 16 under its bottom edge
    const at = (key) => {
      const p = labelPoint(nodes[key].g);
      return { x: p.x, y: p.y - radius(key) - 16 };
    };
    let outs = 0;
    let ins = 0;
    lines.forEach((l, i) => {
      const [f, t, , g] = drawn[i];
      const to = g === 1 ? N + t : t;
      const out = f === ta;
      const inward = !out && to === ta;
      if (out) { outs++; }
      if (inward) { ins++; }
      h.assert(l.classList.contains("okf-graph-edge-out") === out, `edge ${i}: out class`);
      h.assert(l.classList.contains("okf-graph-edge-in") === inward, `edge ${i}: in class`);
      h.assert(l.getAttribute("marker-end") === (out || inward ? "url(#okf-graph-arrow-sel)" : "url(#okf-graph-arrow)"), `edge ${i}: marker`);
      const d = (x, y, key) => Math.hypot(x - at(key).x, y - at(key).y);
      const x1 = Number(l.getAttribute("x1"));
      const y1 = Number(l.getAttribute("y1"));
      const x2 = Number(l.getAttribute("x2"));
      const y2 = Number(l.getAttribute("y2"));
      // The tail leaves the source's edge, the arrow ends 2 before the target's
      // (3 aside at most, for an opposite pair).
      h.assert(d(x2, y2, to) <= radius(to) + 2 + 3.05, `edge ${i}: the arrow end is not at the target`);
      h.assert(d(x1, y1, f) <= radius(f) + 3.05, `edge ${i}: the tail is not at the source`);
      h.assert((x2 - x1) * (at(to).x - at(f).x) + (y2 - y1) * (at(to).y - at(f).y) > 0, `edge ${i}: the arrow points away from the target`);
    });
    h.assert(outs > 0 && ins > 0, `the fixture gives twin-a ${outs} out and ${ins} in links`);
    // The list selects the same way; the drawing follows.
    showList(window, doc);
    const button = Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).find((b) => b.textContent === "p3-graph/twin-b");
    click(window, button);
    const after = drawnNodes(doc).filter((n) => n.g.classList.contains("okf-selected")).map((n) => n.name);
    h.assert(JSON.stringify(after) === JSON.stringify(["p3-graph/twin-b"]), `selected from the list: ${after.join(", ")}`);
  });

  h.checkAsync("drawing: ghost nodes wear the ghost classes, concept nodes do not", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const nodes = drawnNodes(doc);
    const ghosts = nodes.filter((n) => n.name.startsWith("absent: "));
    h.assert(ghosts.length === window.OKF_INDEX.ghosts.length && ghosts.length > 0, "the ghosts are not all drawn");
    for (const { g, name } of nodes) {
      const isGhost = name.startsWith("absent: ");
      h.assert(g.classList.contains("okf-graph-ghost") === isGhost, `${name}: ghost class`);
      h.assert(g.querySelector("text").classList.contains("okf-graph-ghost-label") === isGhost, `${name}: ghost label class`);
    }
  });

  h.checkAsync("drawing: a double click offers the page through a cancelable okf:navigate; a ghost has none", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const events = [];
    doc.addEventListener("okf:navigate", (e) => { events.push({ href: e.detail.href, cancelable: e.cancelable }); e.preventDefault(); });
    const dbl = (target) => target.dispatchEvent(new window.MouseEvent("dblclick", { bubbles: true, cancelable: true }));
    const g = nodeNamed(doc, "p3-graph/twin-a");
    click(window, g);
    const open = doc.querySelector("#okf-graph-detail a.okf-graph-open").getAttribute("href");
    dbl(g);
    h.assert(events.length === 1 && events[0].cancelable && events[0].href === open, `events: ${JSON.stringify(events)} for ${open}`);
    const ghost = drawnNodes(doc).find((n) => n.name.startsWith("absent: ")).g;
    dbl(ghost);
    h.assert(events.length === 1, "a ghost offered a page");
  });

  h.checkAsync("drawing: styling changes (labels, selection) restyle the drawing without laying it out again (R14)", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const canvas = doc.getElementById("okf-graph-canvas");
    const before = drawnNodes(doc).map((n) => n.g);
    const text = drawingText(doc);
    const ran = scheduler.ran();
    setChecked(window, facetInputs(doc, "display")[0], false);
    click(window, nodeNamed(doc, "p3-graph/twin-a"));
    setChecked(window, facetInputs(doc, "display")[0], true);
    h.assert(scheduler.pending() === 0 && scheduler.ran() === ran, "a style change scheduled a layout");
    h.assert(canvas.getAttribute("data-okf-layout") === "done", "the layout mark left done");
    const after = drawnNodes(doc).map((n) => n.g);
    h.assert(after.length === before.length && after.every((g, i) => g === before[i]), "the nodes were rebuilt");
    h.assert(drawingText(doc) === text, "the geometry changed with a style");
  });
}

function selectedName(doc) {
  const g = doc.querySelector("#okf-graph-canvas g.okf-node.okf-selected");
  return g ? g.querySelector("title").textContent : null;
}

function nextHashChange(window) {
  return new Promise((resolve) => window.addEventListener("hashchange", resolve, { once: true }));
}

function registerFragment(h) {
  h.checkAsync("fragment: a concept's id selects it and centres it in the fitted view (control 13)", async () => {
    const { window, doc, scheduler } = await openGraph(h, { hash: "#p3-graph/b" });
    scheduler.flush();
    const index = window.OKF_INDEX;
    h.assert(selectedName(doc) === "p3-graph/b", `selected: ${selectedName(doc)}`);
    h.assert(doc.querySelector("#okf-graph-detail .okf-graph-detail-id").textContent === "p3-graph/b", "the drawer is not filled");
    h.assert(doc.getElementById("okf-reading-view").getAttribute("href") === index.concepts[conceptPos(index, "p3-graph/b")].path, "Reading view");
    // jsdom has no layout: the canvas is taken as 800 x 600.
    const t = viewportTransform(doc);
    const p = labelPoint(nodeNamed(doc, "p3-graph/b"));
    const size = 30; // a square in the graph context (§12.2): label baseline at cy + 15 + 16
    const cx = t.a + p.x * t.s;
    const cy = t.b + (p.y - size / 2 - 16) * t.s;
    h.assert(Math.abs(cx - 400) < 1 && Math.abs(cy - 300) < 1, `the selection is drawn at (${cx}, ${cy}), not centred`);
  });

  h.checkAsync("fragment: hostile, unknown, ghost or malformed fragments select nothing and link nowhere", async () => {
    for (const hash of ["#%3Cimg%20src%3Dx%20onerror%3D%22window.__pwned%3D9%22%3E", "#no-such-concept", "#nowhere",
      "#p3-graph/absent-target", "#%E0%A4%A", "#", "#hasOwnProperty"]) {
      const { window, doc, scheduler } = await openGraph(h, { hash });
      scheduler.flush();
      h.assert(selectedName(doc) === null, `${hash} selected ${selectedName(doc)}`);
      h.assert(doc.getElementById("okf-graph-detail").textContent.includes("Select a concept to see its links."), `${hash}: the drawer is not empty`);
      h.assert(doc.getElementById("okf-reading-view").getAttribute("href") === "index.html", `${hash}: Reading view moved`);
      h.assert(window.__pwned === undefined, `${hash} executed`);
    }
  });

  h.checkAsync("fragment: ids named like Object.prototype members are ordinary concepts (control 7)", async () => {
    for (const id of ["__proto__", "constructor", "toString"]) {
      const { doc, scheduler } = await openGraph(h, { hash: `#${id}` });
      scheduler.flush();
      h.assert(selectedName(doc) === id, `#${id} selected ${selectedName(doc)}`);
      h.assert(drawnNodes(doc).filter((n) => n.name === id).length === 1, `${id} is not exactly one node`);
    }
  });

  h.checkAsync("fragment: a hashchange to a concept hidden by the facets resets them, then selects", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const index = window.OKF_INDEX;
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    const status = doc.getElementById("okf-graph-status");
    const initialStatus = status.textContent;
    setChecked(window, facetInputs(doc, "type")[cType], false);
    click(window, Array.from(doc.querySelectorAll("#okf-facets button.okf-chip")).find((b) => b.textContent.startsWith("graph-core ")));
    scheduler.flush();
    h.assert(nodeNamed(doc, "p3-graph/c") === null, "the facets did not hide p3-graph/c");
    // Every other facet away from its default as well: trust, freshness, display.
    setChecked(window, facetInputs(doc, "trust")[0], false);
    setChecked(window, facetInputs(doc, "freshness")[0], true);
    setChecked(window, facetInputs(doc, "display")[0], false);
    setChecked(window, facetInputs(doc, "display")[1], true);
    scheduler.flush();
    const changed = nextHashChange(window);
    window.location.hash = "#p3-graph/c";
    await changed;
    scheduler.flush();
    h.assert(facetInputs(doc, "type").every((i) => i.checked), "the type facet was not reset");
    h.assert(Array.from(doc.querySelectorAll("#okf-facets button.okf-chip")).every((b) => b.getAttribute("aria-pressed") === "false"), "the tags were not reset");
    h.assert(selectedName(doc) === "p3-graph/c", `selected after the hashchange: ${selectedName(doc)}`);
    h.assert(facetInputs(doc, "trust").every((i) => i.checked), "the trust facet was not reset");
    h.assert(!facetInputs(doc, "freshness")[0].checked, "'Stale only' was not reset");
    h.assert(facetInputs(doc, "display")[0].checked && !facetInputs(doc, "display")[1].checked, "the display facets were not reset");
    h.assert(!doc.querySelector("#okf-graph-canvas svg").classList.contains("okf-graph-nolabels"), "the labels stayed hidden");
    h.assert(status.textContent === initialStatus, `the status line after the reset: ${status.textContent} (was ${initialStatus})`);
    h.assert(drawnNodes(doc).every((n) => !n.g.classList.contains("okf-graph-dim")), "a node stayed dimmed");
  });

  h.checkAsync("fragment: above NODE_LIMIT it fills the drawer and marks the list entry, drawing nothing", async () => {
    const { doc } = await openGraph(h, { hash: "#p3-graph/d", override: { "assets/okf-sim.js": lowLimitSim(3) } });
    h.assert(doc.querySelector("#okf-graph-canvas svg") === null, "something was drawn");
    h.assert(doc.querySelector("#okf-graph-detail .okf-graph-detail-id").textContent === "p3-graph/d", "the drawer is not filled");
    const current = doc.querySelector('#okf-graph-list .okf-graph-list-select[aria-current="true"]');
    h.assert(current && current.textContent === "p3-graph/d", "the list does not mark the entry");
  });

  h.checkAsync("history: a selection rewrites the URL by replaceState, never adding an entry; a ghost clears it", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const length = window.history.length;
    click(window, nodeNamed(doc, "p3-graph/a"));
    h.assert(window.location.hash === "#p3-graph/a", `hash after a click: ${window.location.hash}`);
    click(window, nodeNamed(doc, "p3-graph/e"));
    h.assert(window.location.hash === "#p3-graph/e", `hash after a second click: ${window.location.hash}`);
    click(window, nodeNamed(doc, "absent: p3-graph/absent-target"));
    h.assert(window.location.hash === "" && !window.location.href.includes("#"), `a ghost left the fragment: ${window.location.href}`);
    h.assert(doc.getElementById("okf-reading-view").getAttribute("href") === "index.html", "Reading view after selecting a ghost");
    h.assert(window.history.length === length, `history.length went from ${length} to ${window.history.length}`);
  });

  h.checkAsync("history: when replaceState throws, location.replace keeps history.length and its hashchange is ignored", async () => {
    const { window, doc, scheduler } = await openGraph(h, {
      beforeParse(w) {
        w.History.prototype.replaceState = function () { throw new w.DOMException("file:// origin", "SecurityError"); };
      },
    });
    scheduler.flush();
    const length = window.history.length;
    // A facet the ignored hashchange must not reset.
    setChecked(window, facetInputs(doc, "display")[0], false);
    const changed = nextHashChange(window);
    click(window, nodeNamed(doc, "p3-graph/a"));
    await changed;
    h.assert(window.location.hash === "#p3-graph/a", `hash: ${window.location.hash}`);
    h.assert(window.history.length === length, `history.length went from ${length} to ${window.history.length}`);
    h.assert(!facetInputs(doc, "display")[0].checked, "the page's own hashchange was followed (the facets were reset)");
    h.assert(selectedName(doc) === "p3-graph/a", "the selection changed");
    const cleared = nextHashChange(window);
    click(window, nodeNamed(doc, "absent: nowhere"));
    await cleared;
    h.assert(window.location.hash === "" && window.history.length === length, `deselecting: ${window.location.href}, length ${window.history.length}`);
  });

  h.checkAsync("fragment: a selection made while the list is hidden is marked when the list is built, and after a facet reset", async () => {
    const { window, doc, scheduler } = await openGraph(h, { hash: "#p3-graph/b" });
    scheduler.flush();
    h.assert(doc.getElementById("okf-graph-list").hidden, "the list was built for nothing");
    showList(window, doc);
    const marked = () => Array.from(doc.querySelectorAll('#okf-graph-list .okf-graph-list-select[aria-current="true"]')).map((b) => b.textContent);
    h.assert(marked().length === 1 && marked()[0] === "p3-graph/b", `list marks: ${marked()}`);
    // The list is built and shown; a facet hides c, then a fragment brings it back.
    const index = window.OKF_INDEX;
    setChecked(window, facetInputs(doc, "type")[index.concepts[conceptPos(index, "p3-graph/c")].typeIndex], false);
    scheduler.flush();
    const changed = nextHashChange(window);
    window.location.hash = "#p3-graph/c";
    await changed;
    scheduler.flush();
    h.assert(marked().length === 1 && marked()[0] === "p3-graph/c", `list marks after the reset: ${marked()}`);
    // The list stale and hidden (canvas mode), a fragment selects: the rebuild marks it.
    const toggle = doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
    click(window, toggle);
    setChecked(window, facetInputs(doc, "display")[1], true);
    const again = nextHashChange(window);
    window.location.hash = "#p3-graph/d";
    await again;
    scheduler.flush();
    showList(window, doc);
    h.assert(marked().length === 1 && marked()[0] === "p3-graph/d", `list marks after a hidden rebuild: ${marked()}`);
  });

  h.checkAsync("history: ids named like Object.prototype members round-trip through the URL and a hashchange", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    for (const id of ["__proto__", "constructor", "toString"]) {
      click(window, nodeNamed(doc, id));
      h.assert(window.location.hash === `#${id}`, `hash after clicking ${id}: ${window.location.hash}`);
      h.assert(selectedName(doc) === id, `selected: ${selectedName(doc)}`);
    }
    click(window, nodeNamed(doc, "p3-graph/a"));
    const changed = nextHashChange(window);
    window.location.hash = "#__proto__";
    await changed;
    h.assert(selectedName(doc) === "__proto__", `selected after a hashchange to #__proto__: ${selectedName(doc)}`);
    h.assert(doc.querySelector("#okf-graph-detail .okf-graph-detail-id").textContent === "__proto__", "the drawer is not filled");
  });

  h.checkAsync("fragment: a hashchange to a hostile fragment deselects, link-free and without throwing", async () => {
    const { window, doc, scheduler } = await openGraph(h, { hash: "#p3-graph/b" });
    scheduler.flush();
    h.assert(selectedName(doc) === "p3-graph/b", "setup: not selected");
    const hostile = ["#%3Cimg%20src%3Dx%20onerror%3Dwindow.__pwned%3D9%3E", "#%E0%A4%A", "#p3-graph/a&id=p3-graph/b", `#${"a".repeat(200000)}`, "#%00", "#p3-graph/b%20", "#P3-GRAPH/B"];
    for (const hash of hostile) {
      click(window, nodeNamed(doc, "p3-graph/b"));
      h.assert(selectedName(doc) === "p3-graph/b", "setup: not reselected");
      const changed = nextHashChange(window);
      window.location.hash = hash;
      await changed;
      scheduler.flush();
      h.assert(selectedName(doc) === null, `${hash.slice(0, 40)} selected ${selectedName(doc)}`);
      h.assert(doc.getElementById("okf-reading-view").getAttribute("href") === "index.html", "Reading view moved");
      h.assert(window.__pwned === undefined, "a fragment executed");
      h.assert(doc.querySelectorAll("#okf-graph-detail img, #okf-graph-detail script").length === 0, "the drawer carries markup");
    }
  });

  h.checkAsync("fragment: centring belongs to the fragment that asked for it; a rebuild after a click fits the view", async () => {
    // Opened on #b, then a click selects a: a later rebuild must not recentre b.
    const hiding = async (opts) => {
      const opened = await openGraph(h, opts);
      opened.scheduler.flush();
      return opened;
    };
    const { window, doc, scheduler } = await hiding({ hash: "#p3-graph/b" });
    const index = window.OKF_INDEX;
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    click(window, nodeNamed(doc, "p3-graph/a"));
    setChecked(window, facetInputs(doc, "type")[cType], false);
    scheduler.flush();
    h.assert(window.location.hash === "#p3-graph/a" && selectedName(doc) === "p3-graph/a", "setup: a is not selected");
    h.assert(nodeNamed(doc, "p3-graph/c") === null, "setup: the facet did not hide p3-graph/c");
    // The reference: the same facets on a page opened without a fragment (Fit).
    const fresh = await hiding({});
    setChecked(fresh.window, facetInputs(fresh.doc, "type")[cType], false);
    fresh.scheduler.flush();
    const got = viewportTransform(doc);
    const want = viewportTransform(fresh.doc);
    h.assert(Math.abs(got.a - want.a) < 0.01 && Math.abs(got.b - want.b) < 0.01 && Math.abs(got.s - want.s) < 0.001,
      `the rebuilt view is ${JSON.stringify(got)}, Fit is ${JSON.stringify(want)}`);
  });

  h.checkAsync("history: with replaceState throwing, two selections in one task leave the page's own hashchanges ignored", async () => {
    const { window, doc, scheduler } = await openGraph(h, {
      beforeParse(w) {
        w.History.prototype.replaceState = function () { throw new w.DOMException("file:// origin", "SecurityError"); };
      },
    });
    scheduler.flush();
    setChecked(window, facetInputs(doc, "display")[0], false);
    const before = viewportTransform(doc);
    let events = 0;
    window.addEventListener("hashchange", () => { events++; });
    click(window, nodeNamed(doc, "p3-graph/a"));
    click(window, nodeNamed(doc, "p3-graph/e"));
    // Both navigations fire their own hashchange; wait for them.
    for (let k = 0; k < 50 && events < 2; k++) { await new Promise((resolve) => window.setTimeout(resolve, 0)); }
    scheduler.flush();
    h.assert(events === 2, `${events} hashchange events fired, expected 2`);
    h.assert(window.location.hash === "#p3-graph/e" && selectedName(doc) === "p3-graph/e", `hash ${window.location.hash}, selected ${selectedName(doc)}`);
    h.assert(!facetInputs(doc, "display")[0].checked, "a hashchange of the page's own reset the facets");
    const after = viewportTransform(doc);
    h.assert(after.a === before.a && after.b === before.b && after.s === before.s, `the view moved: ${JSON.stringify(before)} -> ${JSON.stringify(after)}`);
  });

  h.checkAsync("history: a fragment-driven selection never rewrites the URL, and an unchanged one does not either", async () => {
    let calls = 0;
    const count = {
      beforeParse(w) {
        const real = w.History.prototype.replaceState;
        w.History.prototype.replaceState = function (...args) { calls++; return real.apply(this, args); };
      },
    };
    // A fragment that selects nothing stays in the URL: only the reader's selections write it.
    let opened = await openGraph(h, Object.assign({ hash: "#nowhere" }, count));
    opened.scheduler.flush();
    h.assert(opened.window.location.hash === "#nowhere", `a followed fragment was rewritten: ${opened.window.location.hash}`);
    h.assert(calls === 0, `replaceState was called ${calls} time(s) while following a fragment`);
    // A selection that leaves the URL as it is writes nothing.
    calls = 0;
    opened = await openGraph(h, Object.assign({ hash: "#p3-graph/b" }, count));
    opened.scheduler.flush();
    click(opened.window, nodeNamed(opened.doc, "p3-graph/b"));
    h.assert(calls === 0, `replaceState was called ${calls} time(s) for a selection the URL already states`);
    click(opened.window, nodeNamed(opened.doc, "p3-graph/a"));
    h.assert(calls === 1, `a new selection called replaceState ${calls} time(s)`);
  });

  h.checkAsync("fragment: the Global graph link of a concept page leads to that concept, selected", async () => {
    const page = await h.openPage("p3-graph/a.html");
    const href = page.document.getElementById("okf-global-graph").getAttribute("href");
    const hash = href.slice(href.indexOf("#"));
    h.assert(href.indexOf("#") > 0 && hash === "#p3-graph/a", `Global graph link: ${href}`);
    const { doc, scheduler } = await openGraph(h, { hash });
    scheduler.flush();
    h.assert(selectedName(doc) === "p3-graph/a", "the graph page did not select the linking concept");
  });
}


function focusedName(doc) {
  const el = doc.activeElement;
  return el && el.matches && el.matches("#okf-graph-canvas g.okf-node") ? el.querySelector("title").textContent : null;
}

function tabStops(doc) {
  return Array.from(doc.querySelectorAll('#okf-graph-canvas [tabindex="0"]'));
}

// An index of one hub linking `count` leaves (leaf1..leafN, all of one type, so
// that their shapes have one size and label points compare exactly). With
// `chain`, one more leaf hangs off leaf2 only. With `awkward`, the hub's edges
// are listed last-leaf first, leaf1 also links back to the hub and the hub
// lists leaf8 twice: ties must not depend on edge order, and a neighbour is
// one neighbour however many edges say so.
function hubIndexSource(count, chain = false, awkward = false) {
  const ids = ["hub"];
  for (let k = 1; k <= count + (chain ? 1 : 0); k++) { ids.push(`leaf${k}`); }
  const concepts = ids.map((id) => ({ id, title: `T ${id}`, type: "Note", tags: [], path: `${id}.html`, trust: "unverified", staleAfterMs: null, staleAfterDate: null, typeIndex: 0, description: "" }));
  const edges = [];
  for (let k = 1; k <= count; k++) { edges.push([0, awkward ? count + 1 - k : k, 1, 0]); }
  if (awkward) { edges.push([1, 0, 1, 0], [0, count, 1, 0]); }
  if (chain) { edges.push([2, count + 1, 1, 0]); }
  const tree = concepts.map((c, i) => ({ name: c.id, concept: i, children: [] }));
  return "window.OKF_INDEX = " + JSON.stringify({
    version: 2, concepts, ghosts: [], edges, tree, types: [{ name: "Note", count: concepts.length, slot: 0 }],
  }) + ";";
}

// okf-sim.js followed by a stand-in that lays the nodes out at fixed points
// (index order) and finishes at once: a layout a case can reason about.
function fixedSim(points) {
  return `${fs.readFileSync(SIM_FILE, "utf8")}\n;(function () {
  var real = window.OkfSim;
  var points = ${JSON.stringify(points)};
  window.OkfSim = Object.freeze({
    NODE_LIMIT: real.NODE_LIMIT,
    create: function (graph) {
      var flat = new Float64Array(2 * graph.nodeCount);
      for (var i = 0; i < graph.nodeCount; i++) { flat[2 * i] = points[i][0]; flat[2 * i + 1] = points[i][1]; }
      return Object.freeze({
        step: function () { return { done: true, iterations: 1 }; },
        positions: function () { return flat; },
        stats: function () { return {}; },
        cancel: function () {},
      });
    },
  });
})();`;
}

function registerKeyboard(h) {
  h.checkAsync("keyboard: the graph is one tab stop (roving tabindex)", async () => {
    const { doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const stops = tabStops(doc);
    h.assert(stops.length === 1, `${stops.length} tab stops in the drawing`);
    h.assert(doc.querySelectorAll('#okf-graph-canvas g.okf-node[tabindex="-1"]').length === drawnNodes(doc).length - 1, "the other nodes are not tabindex=-1");
    stops[0].focus();
    h.assert(stops[0].classList.contains("okf-focused") && !stops[0].classList.contains("okf-selected"), "focus is not marked apart from selection (G14)");
  });

  h.checkAsync("keyboard: Page Down/Up and Home/End reach both components and the isolated node in index order (control 9)", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const order = drawnNodes(doc).map((n) => n.name);
    for (const must of ["p3-graph/a", "p3-graph/d", "p3-graph/isolated"]) { h.assert(order.includes(must), `${must} is not drawn`); }
    tabStops(doc)[0].focus();
    h.key(window, doc.activeElement, { key: "Home" });
    const visited = [focusedName(doc)];
    for (let k = 1; k < order.length; k++) {
      const event = h.key(window, doc.activeElement, { key: "PageDown" });
      h.assert(event.defaultPrevented, "Page Down was not taken");
      visited.push(focusedName(doc));
    }
    h.assert(JSON.stringify(visited) === JSON.stringify(order), `Page Down visited ${visited.join(" > ")}`);
    h.key(window, doc.activeElement, { key: "PageDown" });
    h.assert(focusedName(doc) === order[order.length - 1], "Page Down past the last node moved");
    h.key(window, doc.activeElement, { key: "PageUp" });
    h.assert(focusedName(doc) === order[order.length - 2], "Page Up did not step back");
    h.key(window, doc.activeElement, { key: "Home" });
    h.assert(focusedName(doc) === order[0] && tabStops(doc).length === 1 && tabStops(doc)[0] === doc.activeElement, "Home, or the roving stop");
    h.key(window, doc.activeElement, { key: "End" });
    h.assert(focusedName(doc) === order[order.length - 1], "End");
  });

  h.checkAsync("keyboard: arrows move to a linked neighbour, Space selects, Enter opens the page", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const index = window.OKF_INDEX;
    const b = nodeNamed(doc, "p3-graph/b");
    const reached = new Set();
    for (const arrow of ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"]) {
      b.focus();
      h.key(window, b, { key: arrow });
      reached.add(focusedName(doc));
    }
    reached.delete("p3-graph/b");
    h.assert(reached.size > 0, "no arrow left p3-graph/b");
    for (const name of reached) {
      h.assert(["p3-graph/a", "p3-graph/c", "p3-graph/hostile"].includes(name), `an arrow reached ${name}, which is not linked to p3-graph/b`);
    }
    const isolated = nodeNamed(doc, "p3-graph/isolated");
    isolated.focus();
    for (const arrow of ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"]) { h.key(window, isolated, { key: arrow }); }
    h.assert(focusedName(doc) === "p3-graph/isolated", "an arrow left a node that has no link");
    b.focus();
    h.key(window, b, { key: " " });
    h.assert(selectedName(doc) === "p3-graph/b" && window.location.hash === "#p3-graph/b", "Space did not select");
    const opened = [];
    doc.addEventListener("okf:navigate", (e) => { opened.push(e.detail.href); e.preventDefault(); });
    h.key(window, b, { key: "Enter" });
    h.assert(opened.length === 1 && opened[0] === index.concepts[conceptPos(index, "p3-graph/b")].path, `Enter opened ${opened.join(", ")}`);
    const ghost = nodeNamed(doc, "absent: nowhere");
    ghost.focus();
    h.key(window, ghost, { key: "Enter" });
    h.assert(opened.length === 1, "Enter on a ghost navigated");
  });

  h.checkAsync("keyboard: a filter hiding the focused node hands focus to the next drawn node, else the previous, else the container (§8)", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const index = window.OKF_INDEX;
    const order = () => drawnNodes(doc).map((n) => n.name);
    // p3-graph/c is followed in index order by other concepts: its successor gets focus.
    const before = order();
    nodeNamed(doc, "p3-graph/c").focus();
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    setChecked(window, facetInputs(doc, "type")[cType], false);
    scheduler.flush();
    const next = before.slice(before.indexOf("p3-graph/c") + 1).find((name) => order().includes(name));
    h.assert(focusedName(doc) === next, `focus went to ${focusedName(doc)}, expected ${next}`);
    h.assert(tabStops(doc).length === 1 && tabStops(doc)[0] === doc.activeElement, "the roving stop did not follow the focus");
    // Ghosts come last in index order: hide every type that cites one, so
    // that a concept is last, then hide that concept -- no successor, so the
    // focus goes to the previous drawn node.
    const citing = new Set(index.edges.filter((e) => e[3] === 1).map((e) => index.concepts[e[0]].typeIndex));
    for (const t of citing) {
      const input = facetInputs(doc, "type")[t];
      if (input.checked) { setChecked(window, input, false); }
    }
    scheduler.flush();
    const remaining = order();
    const last = remaining[remaining.length - 1];
    h.assert(last !== undefined && !last.startsWith("absent: "), `no concept is last (${last}): this case no longer tests "previous"`);
    nodeNamed(doc, last).focus();
    setChecked(window, facetInputs(doc, "type")[index.concepts[conceptPos(index, last)].typeIndex], false);
    scheduler.flush();
    const prev = remaining.slice(0, remaining.indexOf(last)).reverse().find((name) => order().includes(name));
    h.assert(prev !== undefined, "nothing is left before the last node: this case no longer tests \"previous\"");
    h.assert(focusedName(doc) === prev, `focus went to ${focusedName(doc)}, expected ${prev}`);
    // Nothing left: the container.
    facetInputs(doc, "trust").forEach((input) => setChecked(window, input, false));
    scheduler.flush();
    h.assert(order().length === 0, "trust facets did not hide everything");
    h.assert(doc.activeElement === doc.getElementById("okf-graph-canvas"), `focus went to ${doc.activeElement.tagName}#${doc.activeElement.id}`);
  });

  h.checkAsync("keyboard: changing a facet from its checkbox leaves the focus on the checkbox", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const box = facetInputs(doc, "type")[0];
    box.focus();
    setChecked(window, box, false);
    scheduler.flush();
    h.assert(doc.activeElement === box, "the facet change moved the focus");
    h.assert(tabStops(doc).length === 1, "the drawing lost its tab stop");
  });

  h.checkAsync("keyboard: a modified key is the browser's, not the graph's -- and the same keys unmodified are the graph's", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const order = drawnNodes(doc).map((n) => n.name);
    const opened = [];
    doc.addEventListener("okf:navigate", (e) => { opened.push(e.detail.href); e.preventDefault(); });
    let arrow = null;
    for (const candidate of ["ArrowRight", "ArrowLeft", "ArrowUp", "ArrowDown"]) {
      nodeNamed(doc, "p3-graph/b").focus();
      h.key(window, doc.activeElement, { key: candidate });
      if (focusedName(doc) !== "p3-graph/b") { arrow = candidate; break; }
    }
    h.assert(arrow !== null, "no arrow leaves p3-graph/b");
    // What each key does when unmodified: the effect is observable, so that
    // "nothing happened" under a modifier is not vacuous.
    const effects = [
      { key: arrow, from: "p3-graph/b", done: () => focusedName(doc) !== "p3-graph/b" },
      { key: "PageDown", from: order[0], done: () => focusedName(doc) === order[1] },
      { key: "PageUp", from: order[1], done: () => focusedName(doc) === order[0] },
      { key: "Home", from: order[order.length - 1], done: () => focusedName(doc) === order[0] },
      { key: "End", from: order[0], done: () => focusedName(doc) === order[order.length - 1] },
      { key: "Enter", from: "p3-graph/b", done: () => opened.length === 1 },
      { key: " ", from: "p3-graph/b", done: () => selectedName(doc) === "p3-graph/b" },
    ];
    const reset = () => {
      opened.length = 0;
      click(window, nodeNamed(doc, "p3-graph/a"));
    };
    for (const effect of effects) {
      reset();
      const from = nodeNamed(doc, effect.from);
      from.focus();
      const plain = h.key(window, from, { key: effect.key });
      h.assert(plain.defaultPrevented && effect.done(), `unmodified ${JSON.stringify(effect.key)} from ${effect.from} did nothing`);
      for (const modifier of ["ctrlKey", "altKey", "metaKey"]) {
        reset();
        const g = nodeNamed(doc, effect.from);
        g.focus();
        const selectedBefore = selectedName(doc);
        const event = h.key(window, g, { key: effect.key, [modifier]: true });
        h.assert(!event.defaultPrevented, `${modifier}+${JSON.stringify(effect.key)} was taken`);
        h.assert(focusedName(doc) === effect.from && opened.length === 0 && selectedName(doc) === selectedBefore, `${modifier}+${JSON.stringify(effect.key)} acted`);
      }
    }
  });

  h.checkAsync("keyboard: ids named like Object.prototype members are ordinary nodes for focus, selection and Enter (control 7)", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const index = window.OKF_INDEX;
    const opened = [];
    doc.addEventListener("okf:navigate", (e) => { opened.push(e.detail.href); e.preventDefault(); });
    for (const id of ["__proto__", "constructor", "toString"]) {
      const g = nodeNamed(doc, id);
      h.assert(g !== null, `${id} is not drawn`);
      g.focus();
      h.assert(tabStops(doc).length === 1 && tabStops(doc)[0] === g, `${id}: the roving stop is not on the focused node`);
      h.key(window, g, { key: " " });
      h.assert(selectedName(doc) === id && window.location.hash === `#${id}`, `Space on ${id} selected ${selectedName(doc)}`);
      const before = opened.length;
      h.key(window, g, { key: "Enter" });
      h.assert(opened.length === before + 1 && opened[before] === index.concepts[conceptPos(index, id)].path, `Enter on ${id} opened ${opened.slice(before)}`);
    }
    // Paging reaches each of them.
    const order = drawnNodes(doc).map((n) => n.name);
    nodeNamed(doc, order[0]).focus();
    const seen = [];
    for (let k = 0; k < order.length; k++) {
      seen.push(focusedName(doc));
      h.key(window, doc.activeElement, { key: "PageDown" });
    }
    for (const id of ["__proto__", "constructor", "toString"]) { h.assert(seen.includes(id), `Page Down never reached ${id}`); }
  });

  h.checkAsync("keyboard: a filter hiding the tab stop moves it without stealing the focus from the checkbox", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const index = window.OKF_INDEX;
    nodeNamed(doc, "p3-graph/c").focus();
    const box = facetInputs(doc, "type")[0];
    box.focus();
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    setChecked(window, facetInputs(doc, "type")[cType], false);
    scheduler.flush();
    h.assert(doc.activeElement === box, `the focus left the checkbox for ${doc.activeElement.tagName}`);
    h.assert(tabStops(doc).length === 1 && nodeNamed(doc, "p3-graph/c") === null, "the tab stop is lost or c is still drawn");
  });

  h.checkAsync("keyboard: past the node limit the focus goes to the list, already shown (§8)", async () => {
    const probe = (await h.openPage("index.html")).OKF_INDEX;
    const keepType = probe.concepts[conceptPos(probe, "p3-graph/d")].typeIndex;
    const kept = probe.concepts.map((c, i) => (c.typeIndex === keepType ? i : -1)).filter((i) => i >= 0);
    const ghostsKept = new Set(probe.edges.filter((e) => e[3] === 1 && kept.includes(e[0])).map((e) => e[1]));
    const limit = kept.length + ghostsKept.size;
    const focuses = [];
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-sim.js": lowLimitSim(limit) },
      beforeParse(w) {
        const real = w.HTMLElement.prototype.focus;
        w.HTMLElement.prototype.focus = function (...args) {
          focuses.push({ id: this.id, hidden: this.hidden });
          return real.apply(this, args);
        };
      },
    });
    facetInputs(doc, "type").forEach((input, k) => { if (k !== keepType) { setChecked(window, input, false); } });
    scheduler.flush();
    drawnNodes(doc)[0].g.focus();
    focuses.length = 0;
    facetInputs(doc, "type").forEach((input, k) => { if (k !== keepType) { setChecked(window, input, true); } });
    scheduler.flush();
    const list = doc.getElementById("okf-graph-list");
    h.assert(!list.hidden && doc.querySelector("#okf-graph-canvas svg") === null, "the list does not stand in for the drawing");
    const toList = focuses.filter((f) => f.id === "okf-graph-list");
    h.assert(toList.length === 1, `the list received ${toList.length} focus() calls`);
    h.assert(toList[0].hidden === false, "the list was focused while still hidden: a browser would drop the focus");
    h.assert(doc.activeElement === list, `focus is on ${doc.activeElement.tagName}#${doc.activeElement.id}`);
  });

  // --- fix round 1 -------------------------------------------------------

  h.checkAsync("keyboard: the arrows alone reach every neighbour of a hub with 8 and with 40 neighbours, nearest first, deterministically, and wrap", async () => {
    for (const count of [8, 40]) {
      const sequences = [];
      for (let run = 0; run < 2; run++) {
        const { window, doc, scheduler } = await openGraph(h, { override: { "assets/okf-index.js": hubIndexSource(count) } });
        scheduler.flush();
        const centre = labelPoint(nodeNamed(doc, "hub"));
        const leaves = drawnNodes(doc).map((n) => n.name).filter((name) => name !== "hub");
        h.assert(leaves.length === count, `${leaves.length} leaves drawn, expected ${count}`);
        const expected = { ArrowRight: [], ArrowLeft: [], ArrowDown: [], ArrowUp: [] };
        for (const name of leaves) {
          const p = labelPoint(nodeNamed(doc, name));
          const dx = p.x - centre.x;
          const dy = p.y - centre.y;
          const arrow = Math.abs(dx) >= Math.abs(dy) ? (dx >= 0 ? "ArrowRight" : "ArrowLeft") : (dy > 0 ? "ArrowDown" : "ArrowUp");
          expected[arrow].push({ name, d: dx * dx + dy * dy, order: leaves.indexOf(name) });
        }
        const reached = new Set();
        const sequence = {};
        for (const arrow of Object.keys(expected)) {
          const cone = expected[arrow].sort((a, b) => a.d - b.d || a.order - b.order);
          const presses = cone.length > 1 ? cone.length + 1 : cone.length;
          nodeNamed(doc, "hub").focus();
          const visited = [];
          for (let k = 0; k < presses; k++) {
            const event = h.key(window, doc.activeElement, { key: arrow });
            h.assert(event.defaultPrevented, `${arrow} was not taken`);
            visited.push(focusedName(doc));
          }
          // The label points are rounded to 0.01: the order is checked up to that.
          const names = cone.map((n) => n.name);
          const first = visited.slice(0, cone.length);
          h.assert(JSON.stringify(first.slice().sort()) === JSON.stringify(names.slice().sort()), `${count} neighbours, ${arrow}: visited ${visited.join(" > ")}, expected the cone ${names.join(", ")}`);
          const dist = (name) => { const p = labelPoint(nodeNamed(doc, name)); return Math.hypot(p.x - centre.x, p.y - centre.y); };
          for (let k = 1; k < first.length; k++) { h.assert(dist(first[k]) >= dist(first[k - 1]) - 0.1, `${count} neighbours, ${arrow}: ${first[k]} is nearer than ${first[k - 1]} yet comes after it`); }
          if (cone.length > 1) { h.assert(visited[cone.length] === visited[0], `${arrow} did not wrap to ${visited[0]}: ${visited[cone.length]}`); }
          names.forEach((name) => reached.add(name));
          sequence[arrow] = visited;
        }
        h.assert(reached.size === count, `${reached.size} of ${count} neighbours reachable by the arrows`);
        sequences.push(JSON.stringify(sequence));
      }
      h.assert(sequences[0] === sequences[1], `${count} neighbours: two runs visited differently`);
    }
  });

  h.checkAsync("keyboard: ties, the diagonal and neighbours at the same position still belong to one cone; a lone neighbour is not a cycle", async () => {
    // Hub at the origin; leaf1 right, leaf2 left, leaf3 down, leaf4 up, leaf5 on
    // the right/down diagonal, leaf6 and leaf7 exactly on the hub, leaf8 right.
    const points = [[0, 0], [100, 0], [-100, 0], [0, 100], [0, -100], [50, 50], [0, 0], [0, 0], [120, 10], [-220, 0]];
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": hubIndexSource(8, true, true), "assets/okf-sim.js": fixedSim(points) },
    });
    scheduler.flush();
    const walk = (arrow, presses) => {
      nodeNamed(doc, "hub").focus();
      const visited = [];
      for (let k = 0; k < presses; k++) {
        h.key(window, doc.activeElement, { key: arrow });
        visited.push(focusedName(doc));
      }
      return visited;
    };
    // The diagonal and the coincident leaves go to the horizontal cone (angle 0 for a zero vector).
    const right = walk("ArrowRight", 6).join(" > ");
    h.assert(right === "leaf6 > leaf7 > leaf5 > leaf1 > leaf8 > leaf6", `Right: ${right}`);
    h.assert(walk("ArrowLeft", 1)[0] === "leaf2" && walk("ArrowDown", 1)[0] === "leaf3" && walk("ArrowUp", 1)[0] === "leaf4", "a lone neighbour is not reached");
    // A lone neighbour is not a cycle: the same arrow goes on from it (leaf9 hangs off leaf2, further left).
    const again = walk("ArrowLeft", 3);
    h.assert(again.join(" > ") === "leaf2 > leaf9 > leaf9", `Left three times: ${again.join(" > ")}`);
  });

  h.checkAsync("keyboard: the cycle belongs to the origin: another key in between ends it", async () => {
    const points = [[0, 0], [100, 0], [-100, 0], [0, 100], [0, -100], [50, 50], [0, 0], [0, 0], [120, 10]];
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": hubIndexSource(8), "assets/okf-sim.js": fixedSim(points) },
    });
    scheduler.flush();
    const hub = nodeNamed(doc, "hub");
    hub.focus();
    h.key(window, hub, { key: "ArrowRight" });
    h.assert(focusedName(doc) === "leaf6", "first press");
    // Another key in between: pressing Right again starts afresh from where the focus is, not on the old cycle.
    h.key(window, doc.activeElement, { key: "PageDown" });
    h.key(window, doc.activeElement, { key: "PageUp" });
    h.assert(focusedName(doc) === "leaf6", "paging did not come back to leaf6");
    h.key(window, doc.activeElement, { key: "ArrowRight" });
    h.assert(focusedName(doc) !== "leaf7", `an interrupted cycle went on: ${focusedName(doc)}`);
  });

  h.checkAsync("keyboard: the selected node, and only it, carries aria-current=true -- through clicks, fragments, rebuilds and ghosts", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const current = () => Array.from(doc.querySelectorAll("#okf-graph-canvas g.okf-node[aria-current]")).map((g) => `${g.querySelector("title").textContent}=${g.getAttribute("aria-current")}`);
    h.assert(current().length === 0, `something is current at start: ${current()}`);
    click(window, nodeNamed(doc, "p3-graph/b"));
    h.assert(JSON.stringify(current()) === JSON.stringify(["p3-graph/b=true"]), `after a click: ${current()}`);
    click(window, nodeNamed(doc, "p3-graph/a"));
    h.assert(JSON.stringify(current()) === JSON.stringify(["p3-graph/a=true"]), `after another click: ${current()}`);
    click(window, nodeNamed(doc, "absent: p3-graph/absent-target"));
    h.assert(JSON.stringify(current()) === JSON.stringify(["absent: p3-graph/absent-target=true"]), `a ghost: ${current()}`);
    // A rebuild (a filter hides other nodes) keeps it on the selected node.
    click(window, nodeNamed(doc, "p3-graph/hostile"));
    const index = window.OKF_INDEX;
    const other = index.concepts.find((c) => c.typeIndex !== index.concepts[conceptPos(index, "p3-graph/hostile")].typeIndex);
    setChecked(window, facetInputs(doc, "type")[other.typeIndex], false);
    scheduler.flush();
    h.assert(JSON.stringify(current()) === JSON.stringify(["p3-graph/hostile=true"]), `after a rebuild: ${current()}`);
    // A fragment that selects nothing clears it.
    const changed = nextHashChange(window);
    window.location.hash = "#nowhere";
    await changed;
    scheduler.flush();
    h.assert(current().length === 0, `a deselect left ${current()}`);
    // A fragment that selects a concept sets it (and resets the facets).
    const again = nextHashChange(window);
    window.location.hash = "#p3-graph/c";
    await again;
    scheduler.flush();
    h.assert(JSON.stringify(current()) === JSON.stringify(["p3-graph/c=true"]), `after a fragment: ${current()}`);
    const opened = await openGraph(h, { hash: "#p3-graph/d" });
    opened.scheduler.flush();
    const onOpen = Array.from(opened.doc.querySelectorAll("#okf-graph-canvas g.okf-node[aria-current]")).map((g) => g.querySelector("title").textContent);
    h.assert(JSON.stringify(onOpen) === JSON.stringify(["p3-graph/d"]), `opened on a fragment: ${onOpen}`);
  });

  h.checkAsync("keyboard: the tab stop follows the selection -- a fragment, a click, the list, and a rebuild after the node limit", async () => {
    const fragment = await openGraph(h, { hash: "#p3-graph/c" });
    fragment.scheduler.flush();
    h.assert(JSON.stringify(tabStops(fragment.doc).map((g) => g.querySelector("title").textContent)) === JSON.stringify(["p3-graph/c"]),
      `Tab would land on ${tabStops(fragment.doc).map((g) => g.querySelector("title").textContent)}, not on the node the fragment selected`);
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    click(window, nodeNamed(doc, "p3-graph/d"));
    h.assert(tabStops(doc).length === 1 && tabStops(doc)[0] === nodeNamed(doc, "p3-graph/d"), "a click did not move the tab stop to the selection");
    showList(window, doc);
    click(window, Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).find((b) => b.textContent === "p3-graph/b"));
    h.assert(tabStops(doc).length === 1 && tabStops(doc)[0] === nodeNamed(doc, "p3-graph/b"), "a list selection did not move the tab stop");
    // Past the limit there is no drawing; selecting from the list, then narrowing, the stop is the selection, not the first node.
    const probe = (await h.openPage("index.html")).OKF_INDEX;
    const keepType = probe.concepts[conceptPos(probe, "p3-graph/d")].typeIndex;
    const kept = probe.concepts.map((c, i) => (c.typeIndex === keepType ? i : -1)).filter((i) => i >= 0);
    const ghostsKept = new Set(probe.edges.filter((e) => e[3] === 1 && kept.includes(e[0])).map((e) => e[1]));
    const limit = kept.length + ghostsKept.size;
    const low = await openGraph(h, { override: { "assets/okf-sim.js": lowLimitSim(limit) } });
    const narrow = (on) => facetInputs(low.doc, "type").forEach((input, k) => { if (k !== keepType) { setChecked(low.window, input, on); } });
    narrow(false);
    low.scheduler.flush();
    const names = drawnNodes(low.doc).map((n) => n.name);
    h.assert(names.length >= 2, "the narrowed graph has fewer than two nodes: this case no longer tests the selection");
    const last = names[names.length - 1];
    narrow(true);
    low.scheduler.flush();
    click(low.window, Array.from(low.doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).find((b) => b.textContent === last));
    narrow(false);
    low.scheduler.flush();
    const stop = tabStops(low.doc).map((g) => g.querySelector("title").textContent);
    h.assert(JSON.stringify(stop) === JSON.stringify([last]), `the stop is on ${stop}, expected the selection ${last}`);
  });

  h.checkAsync("keyboard: when the list hides again, the focus it held goes to the tab-stop node, else the List toggle", async () => {
    const probe = (await h.openPage("index.html")).OKF_INDEX;
    const keepType = probe.concepts[conceptPos(probe, "p3-graph/d")].typeIndex;
    const kept = probe.concepts.map((c, i) => (c.typeIndex === keepType ? i : -1)).filter((i) => i >= 0);
    const ghostsKept = new Set(probe.edges.filter((e) => e[3] === 1 && kept.includes(e[0])).map((e) => e[1]));
    const limit = kept.length + ghostsKept.size;
    const { window, doc, scheduler } = await openGraph(h, { override: { "assets/okf-sim.js": lowLimitSim(limit) } });
    const list = doc.getElementById("okf-graph-list");
    h.assert(!list.hidden, "the list does not stand in above the limit");
    list.focus();
    // setChecked changes the facet without moving the focus, as a click on a label can.
    facetInputs(doc, "type").forEach((input, k) => { if (k !== keepType) { setChecked(window, input, false); } });
    scheduler.flush();
    h.assert(list.hidden, "the list did not hide");
    h.assert(tabStops(doc).length === 1 && doc.activeElement === tabStops(doc)[0], `focus is on ${doc.activeElement.tagName}#${doc.activeElement.id}, not on the tab-stop node`);
    // Back over the limit, then to a graph with nothing in it: the toggle.
    facetInputs(doc, "type").forEach((input, k) => { if (k !== keepType) { setChecked(window, input, true); } });
    scheduler.flush();
    h.assert(!list.hidden, "the list did not come back");
    list.focus();
    facetInputs(doc, "trust").forEach((input) => setChecked(window, input, false));
    scheduler.flush();
    h.assert(list.hidden && drawnNodes(doc).length === 0, "the empty graph is not drawn");
    h.assert(doc.activeElement === doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle"), `focus is on ${doc.activeElement.tagName}#${doc.activeElement.id}`);
  });

  h.checkAsync("keyboard: the container that takes the focus of an emptied graph has a name and a role", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    nodeNamed(doc, "p3-graph/a").focus();
    facetInputs(doc, "trust").forEach((input) => setChecked(window, input, false));
    scheduler.flush();
    const canvas = doc.getElementById("okf-graph-canvas");
    h.assert(doc.activeElement === canvas, "the container did not take the focus");
    h.assert(canvas.getAttribute("role") === "group" && /^\S.*$/.test(canvas.getAttribute("aria-label") || ""), "the focused container is nameless");
  });

  h.checkAsync("keyboard: the focus contour follows the focus alone: it leaves a node that loses the focus", async () => {
    const { doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const a = nodeNamed(doc, "p3-graph/a");
    const b = nodeNamed(doc, "p3-graph/b");
    a.focus();
    h.assert(a.classList.contains("okf-focused"), "a is not focused-marked");
    b.focus();
    h.assert(!a.classList.contains("okf-focused") && b.classList.contains("okf-focused"), "the contour did not move with the focus");
    facetInputs(doc, "type")[0].focus();
    h.assert(doc.querySelectorAll("#okf-graph-canvas .okf-focused").length === 0, "a contour stayed after the focus left the graph");
  });

  h.checkAsync("keyboard: a key at an edge is still taken (the page must not scroll), and Escape, Tab and Shift+Tab are never taken", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const order = drawnNodes(doc).map((n) => n.name);
    const isolated = nodeNamed(doc, "p3-graph/isolated");
    isolated.focus();
    for (const arrow of ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"]) {
      h.assert(h.key(window, isolated, { key: arrow }).defaultPrevented, `${arrow} on a node without links was not taken`);
    }
    nodeNamed(doc, order[order.length - 1]).focus();
    h.assert(h.key(window, doc.activeElement, { key: "PageDown" }).defaultPrevented, "Page Down past the last node was not taken");
    h.assert(h.key(window, doc.activeElement, { key: "End" }).defaultPrevented, "End on the last node was not taken");
    nodeNamed(doc, order[0]).focus();
    h.assert(h.key(window, doc.activeElement, { key: "PageUp" }).defaultPrevented, "Page Up before the first node was not taken");
    h.assert(h.key(window, doc.activeElement, { key: "Home" }).defaultPrevented, "Home on the first node was not taken");
    click(window, nodeNamed(doc, "p3-graph/b"));
    nodeNamed(doc, "p3-graph/b").focus();
    for (const init of [{ key: "Escape" }, { key: "Tab" }, { key: "Tab", shiftKey: true }, { key: "a" }]) {
      const event = h.key(window, doc.activeElement, init);
      h.assert(!event.defaultPrevented, `${JSON.stringify(init)} was taken`);
    }
    h.assert(focusedName(doc) === "p3-graph/b" && selectedName(doc) === "p3-graph/b", "Escape or Tab moved the focus or the selection");
  });

  h.checkAsync("keyboard: Shift does not change what an arrow, a paging key, Enter or Space does", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const opened = [];
    doc.addEventListener("okf:navigate", (e) => { opened.push(e.detail.href); e.preventDefault(); });
    const order = drawnNodes(doc).map((n) => n.name);
    for (const arrow of ["ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight"]) {
      nodeNamed(doc, "p3-graph/b").focus();
      h.key(window, doc.activeElement, { key: arrow });
      const plain = focusedName(doc);
      nodeNamed(doc, "p3-graph/b").focus();
      const event = h.key(window, doc.activeElement, { key: arrow, shiftKey: true });
      h.assert(event.defaultPrevented && focusedName(doc) === plain, `Shift+${arrow} reached ${focusedName(doc)}, the plain key ${plain}`);
    }
    nodeNamed(doc, order[0]).focus();
    h.key(window, doc.activeElement, { key: "PageDown", shiftKey: true });
    h.assert(focusedName(doc) === order[1], "Shift+Page Down did not page");
    h.key(window, doc.activeElement, { key: "End", shiftKey: true });
    h.assert(focusedName(doc) === order[order.length - 1], "Shift+End did not go to the end");
    nodeNamed(doc, "p3-graph/b").focus();
    h.key(window, doc.activeElement, { key: " ", shiftKey: true });
    h.assert(selectedName(doc) === "p3-graph/b", "Shift+Space did not select");
    h.key(window, doc.activeElement, { key: "Enter", shiftKey: true });
    h.assert(opened.length === 1, "Shift+Enter did not open");
  });
}

// An event whose numbers are not finite: the constructors refuse those, so
// they are set on the instance afterwards, as a hostile or broken device would.
function withNumbers(make, init) {
  const finite = {};
  const odd = {};
  for (const [name, value] of Object.entries(init)) {
    if (typeof value === "number" && !Number.isFinite(value)) { odd[name] = value; finite[name] = 0; } else { finite[name] = value; }
  }
  const event = make(finite);
  for (const [name, value] of Object.entries(odd)) { Object.defineProperty(event, name, { value }); }
  return event;
}

function pointer(window, target, type, x, y, extra = {}) {
  const init = Object.assign({ bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0, pointerId: 1 }, extra);
  target.dispatchEvent(withNumbers((i) => new window.PointerEvent(type, i), init));
}

function zoomButton(doc, label) {
  return doc.querySelector(`#okf-graph-zoom button[aria-label="${label}"]`);
}

function fitButton(doc) {
  return Array.from(doc.querySelectorAll("#okf-graph-zoom button")).find((b) => b.textContent === "Fit");
}

function wheel(window, target, init) {
  const event = withNumbers((i) => new window.WheelEvent("wheel", i), Object.assign({ bubbles: true, cancelable: true, clientX: 400, clientY: 300 }, init));
  target.dispatchEvent(event);
  return event;
}

function sameTransform(a, b) {
  return Math.abs(a.a - b.a) < 0.01 && Math.abs(a.b - b.b) < 0.01 && Math.abs(a.s - b.s) < 0.001;
}

// A drawn node's box in canvas pixels, the way the fit counts it: its shape
// and its label (about 6.9 px an advance in Space Mono 11.5, 20 under the shape).
function screenBox(window, doc, g) {
  const t = viewportTransform(doc);
  const index = window.OKF_INDEX;
  const name = g.querySelector("title").textContent;
  const kind = name.startsWith("absent: ") ? "ghost" : window.OkfShapes.kindOf(index, conceptPos(index, name));
  const half = window.OkfShapes.SIZES.graph[kind].size / 2;
  const p = labelPoint(g);
  const cy = p.y - half - 16;
  const wide = Math.max(half, g.querySelector("text").textContent.length * 6.9 / 2);
  return { l: t.a + (p.x - wide) * t.s, r: t.a + (p.x + wide) * t.s, t: t.b + (cy - half) * t.s, b: t.b + (cy + half + 20) * t.s };
}

// Concepts of one type, linked as given, with ids the case chooses.
function namedIndexSource(ids, edges) {
  const concepts = ids.map((id) => ({ id, title: `T ${id}`, type: "Note", tags: [], path: `${id}.html`, trust: "unverified", staleAfterMs: null, staleAfterDate: null, typeIndex: 0, description: "" }));
  const tree = concepts.map((c, i) => ({ name: c.id, concept: i, children: [] }));
  return "window.OKF_INDEX = " + JSON.stringify({
    version: 2, concepts, ghosts: [], edges, tree, types: [{ name: "Note", count: concepts.length, slot: 0 }],
  }) + ";";
}

const REVEAL_MARGIN = 8;
const FAR_APART = [[0, 0], [500, 0], [0, 500], [500, 500]];

function registerPointer(h) {
  h.checkAsync("pointer: zoom buttons scale around the centre, Fit restores the fitted view", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const fitted = viewportTransform(doc);
    const zoomIn = doc.querySelector('#okf-graph-zoom button[aria-label="Zoom in"]');
    const zoomOut = doc.querySelector('#okf-graph-zoom button[aria-label="Zoom out"]');
    const fit = Array.from(doc.querySelectorAll("#okf-graph-zoom button")).find((b) => b.textContent === "Fit");
    h.assert(zoomIn && zoomOut && fit, "a zoom control is missing");
    const order = Array.from(doc.querySelectorAll("#okf-graph-zoom button"), (b) => b.getAttribute("aria-label") || b.textContent);
    h.assert(JSON.stringify(order) === JSON.stringify(["Zoom in", "Zoom out", "Fit", "List"]), `zoom box order: ${order.join(", ")}`);
    click(window, zoomIn);
    const zoomed = viewportTransform(doc);
    h.assert(Math.abs(zoomed.s / fitted.s - 1.25) < 0.01, `Zoom in scaled by ${zoomed.s / fitted.s}`);
    // The canvas centre (400, 300 without layout) stays put.
    const gx = (400 - fitted.a) / fitted.s;
    h.assert(Math.abs(zoomed.a + gx * zoomed.s - 400) < 0.5, "Zoom in moved the centre");
    click(window, zoomOut);
    h.assert(Math.abs(viewportTransform(doc).s - fitted.s) < 0.01, "Zoom out did not undo Zoom in");
    click(window, zoomIn);
    click(window, fit);
    h.assert(JSON.stringify(viewportTransform(doc)) === JSON.stringify(fitted), "Fit did not restore the fitted view");
  });

  h.checkAsync("pointer: the wheel zooms around the pointer", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const canvas = doc.getElementById("okf-graph-canvas");
    const t0 = viewportTransform(doc);
    const event = new window.WheelEvent("wheel", { bubbles: true, cancelable: true, deltaY: -100, clientX: 120, clientY: 80 });
    canvas.dispatchEvent(event);
    const t1 = viewportTransform(doc);
    h.assert(event.defaultPrevented && t1.s > t0.s, "the wheel did not zoom in");
    const gx = (120 - t0.a) / t0.s;
    const gy = (80 - t0.b) / t0.s;
    h.assert(Math.abs(t1.a + gx * t1.s - 120) < 0.5 && Math.abs(t1.b + gy * t1.s - 80) < 0.5, "the point under the pointer moved");
  });

  h.checkAsync("pointer: dragging the background pans; dragging a node moves it alone and does not select it", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const canvas = doc.getElementById("okf-graph-canvas");
    const svg = canvas.querySelector("svg");
    const t0 = viewportTransform(doc);
    pointer(window, svg, "pointerdown", 10, 10);
    pointer(window, svg, "pointermove", 40, 30);
    pointer(window, svg, "pointerup", 40, 30);
    const t1 = viewportTransform(doc);
    h.assert(Math.abs(t1.a - t0.a - 30) < 0.01 && Math.abs(t1.b - t0.b - 20) < 0.01, `pan moved by (${t1.a - t0.a}, ${t1.b - t0.b})`);
    const a = nodeNamed(doc, "p3-graph/a");
    const e = labelPoint(nodeNamed(doc, "p3-graph/e"));
    const p0 = labelPoint(a);
    pointer(window, a, "pointerdown", 100, 100);
    pointer(window, a, "pointermove", 100 + 20 * t1.s, 100 + 10 * t1.s);
    pointer(window, a, "pointerup", 100 + 20 * t1.s, 100 + 10 * t1.s);
    click(window, a);
    const p1 = labelPoint(a);
    // The transform attribute gives the scale to 3 decimals: 20 * t1.s px is
    // 20 graph units to within 20 * 0.0005 / s, plus the label's 0.01 rounding.
    const tol = 0.02 + 20 * 0.0005 / (t1.s * t1.s);
    h.assert(Math.abs(p1.x - p0.x - 20) < tol && Math.abs(p1.y - p0.y - 10) < tol, `the node moved by (${p1.x - p0.x}, ${p1.y - p0.y})`);
    h.assert(JSON.stringify(labelPoint(nodeNamed(doc, "p3-graph/e"))) === JSON.stringify(e), "another node moved");
    h.assert(selectedName(doc) === null, "the drag selected the node");
    click(window, a);
    h.assert(selectedName(doc) === "p3-graph/a", "a plain click after the drag does not select");
  });

  h.checkAsync("pointer: a filter change after pan, zoom and drag starts a fresh, fitted layout", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const svg = doc.querySelector("#okf-graph-canvas svg");
    pointer(window, svg, "pointerdown", 10, 10);
    pointer(window, svg, "pointermove", 90, 70);
    pointer(window, svg, "pointerup", 90, 70);
    click(window, doc.querySelector('#okf-graph-zoom button[aria-label="Zoom in"]'));
    const a = nodeNamed(doc, "p3-graph/a");
    pointer(window, a, "pointerdown", 100, 100);
    pointer(window, a, "pointermove", 160, 160);
    pointer(window, a, "pointerup", 160, 160);
    const index = window.OKF_INDEX;
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    setChecked(window, facetInputs(doc, "type")[cType], false);
    scheduler.flush();
    const reference = await openGraph(h);
    setChecked(reference.window, facetInputs(reference.doc, "type")[cType], false);
    reference.scheduler.flush();
    h.assert(drawingText(doc) === drawingText(reference.doc), "pan, zoom or drag leaked into the new layout");
  });

  h.checkAsync("pointer: the zoom is clamped; a wheel of any size, zero or not a number never breaks the view", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const canvas = doc.getElementById("okf-graph-canvas");
    const zoomIn = zoomButton(doc, "Zoom in");
    const zoomOut = zoomButton(doc, "Zoom out");
    for (let k = 0; k < 40; k++) { click(window, zoomIn); }
    h.assert(Math.abs(viewportTransform(doc).s - 4) < 0.001, `Zoom in stops at ${viewportTransform(doc).s}, not 4 (MAX_SCALE)`);
    for (let k = 0; k < 80; k++) { click(window, zoomOut); }
    h.assert(Math.abs(viewportTransform(doc).s - 0.05) < 0.001, `Zoom out stops at ${viewportTransform(doc).s}, not 0.05 (MIN_SCALE)`);
    click(window, fitButton(doc));
    // One notch is 1.1x, proportionally: half a notch is the square root, and no delta zooms by more than a notch.
    const t0 = viewportTransform(doc);
    wheel(window, canvas, { deltaY: -50 });
    const half = viewportTransform(doc).s / t0.s;
    h.assert(Math.abs(half - Math.sqrt(1.1)) < 0.005, `half a notch zoomed by ${half}`);
    const t1 = viewportTransform(doc);
    wheel(window, canvas, { deltaY: -1e300 });
    const huge = viewportTransform(doc).s / t1.s;
    h.assert(Math.abs(huge - 1.1) < 0.005, `an enormous delta zoomed by ${huge}`);
    const t2 = viewportTransform(doc);
    const out = wheel(window, canvas, { deltaY: 1e300 });
    h.assert(out.defaultPrevented && Math.abs(t2.s / viewportTransform(doc).s - 1.1) < 0.005, "an enormous positive delta did not zoom out by a notch");
    // Zero and not-a-number: the page's own, nothing zooms.
    const before = JSON.stringify(viewportTransform(doc));
    for (const deltaY of [0, NaN, Infinity, -Infinity]) {
      const event = wheel(window, canvas, { deltaY, deltaX: 40 });
      h.assert(!event.defaultPrevented, `a wheel of deltaY ${deltaY} was taken`);
    }
    h.assert(JSON.stringify(viewportTransform(doc)) === before, "a wheel of deltaY zero or not finite moved the view");
    // The line and page units, and a pointer that is not a number.
    for (const init of [{ deltaY: -3, deltaMode: 1 }, { deltaY: 1, deltaMode: 2 }, { deltaY: -1e300, deltaMode: 1 }, { deltaY: -100, clientX: NaN }, { deltaY: -100, clientY: Infinity }]) {
      wheel(window, canvas, init);
      const t = viewportTransform(doc);
      h.assert(Number.isFinite(t.a) && Number.isFinite(t.b) && t.s >= 0.05 - 1e-9 && t.s <= 4 + 1e-9, `wheel ${JSON.stringify(init)} left ${JSON.stringify(t)}`);
    }
    h.assert(!/NaN|Infinity/.test(doc.querySelector("#okf-graph-canvas g.okf-graph-viewport").getAttribute("transform")), "the transform holds a non-number");
  });

  h.checkAsync("pointer: a press that moves under 3 px is a click; one that moves further is a drag", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const a = nodeNamed(doc, "p3-graph/a");
    const svg = doc.querySelector("#okf-graph-canvas svg");
    const t0 = viewportTransform(doc);
    const p0 = labelPoint(a);
    pointer(window, a, "pointerdown", 100, 100);
    pointer(window, a, "pointermove", 102, 101);
    pointer(window, a, "pointerup", 102, 101);
    click(window, a);
    h.assert(selectedName(doc) === "p3-graph/a", "a 2 px wobble swallowed the click");
    h.assert(JSON.stringify(labelPoint(a)) === JSON.stringify(p0), "a 2 px wobble moved the node");
    pointer(window, svg, "pointerdown", 10, 10);
    pointer(window, svg, "pointermove", 12, 11);
    pointer(window, svg, "pointerup", 12, 11);
    h.assert(JSON.stringify(viewportTransform(doc)) === JSON.stringify(t0), "a 2 px wobble panned the view");
    pointer(window, svg, "pointerdown", 10, 10);
    pointer(window, svg, "pointermove", 14, 10);
    pointer(window, svg, "pointerup", 14, 10);
    h.assert(Math.abs(viewportTransform(doc).a - t0.a - 4) < 0.01, "a 4 px drag did not pan by its whole length");
  });

  h.checkAsync("pointer: a drag ends at pointerup, pointercancel, lostpointercapture, blur or a mouse released outside; another pointer or button is not this drag", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const svg = doc.querySelector("#okf-graph-canvas svg");
    const a = nodeNamed(doc, "p3-graph/a");
    const stays = (end, extra, label) => {
      pointer(window, svg, "pointerdown", 10, 10, extra);
      pointer(window, svg, "pointermove", 40, 30, extra);
      const mid = JSON.stringify(viewportTransform(doc));
      end();
      pointer(window, svg, "pointermove", 140, 130, extra);
      h.assert(JSON.stringify(viewportTransform(doc)) === mid, `${label}: the drag went on`);
      pointer(window, svg, "pointerup", 140, 130, extra);
    };
    stays(() => pointer(window, svg, "pointerup", 40, 30), {}, "pointerup");
    stays(() => pointer(window, svg, "pointercancel", 40, 30), {}, "pointercancel");
    stays(() => pointer(window, svg, "lostpointercapture", 40, 30), {}, "lostpointercapture");
    stays(() => window.dispatchEvent(new window.Event("blur")), {}, "blur");
    // A mouse released outside the window sends no pointerup: its next move reports no button.
    pointer(window, svg, "pointerdown", 10, 10, { pointerType: "mouse", buttons: 1 });
    pointer(window, svg, "pointermove", 40, 30, { pointerType: "mouse", buttons: 1 });
    const mid = JSON.stringify(viewportTransform(doc));
    pointer(window, svg, "pointermove", 140, 130, { pointerType: "mouse", buttons: 0 });
    h.assert(JSON.stringify(viewportTransform(doc)) === mid, "a mouse move without its button went on dragging");
    pointer(window, svg, "pointermove", 200, 200, { pointerType: "mouse", buttons: 1 });
    h.assert(JSON.stringify(viewportTransform(doc)) === mid, "the drag came back to life");
    // Another pointer (a second finger) neither moves, ends nor restarts this drag.
    pointer(window, svg, "pointerdown", 10, 10, { pointerId: 1 });
    pointer(window, svg, "pointerdown", 300, 300, { pointerId: 2 });
    pointer(window, svg, "pointermove", 40, 30, { pointerId: 2 });
    const t = viewportTransform(doc);
    pointer(window, svg, "pointermove", 40, 30, { pointerId: 1 });
    h.assert(Math.abs(viewportTransform(doc).a - t.a - 30) < 0.01, "pointer 1's drag was disturbed by pointer 2");
    pointer(window, svg, "pointerup", 40, 30, { pointerId: 2 });
    pointer(window, svg, "pointermove", 50, 30, { pointerId: 1 });
    h.assert(Math.abs(viewportTransform(doc).a - t.a - 40) < 0.01, "pointer 2's release ended pointer 1's drag");
    pointer(window, svg, "pointerup", 50, 30, { pointerId: 1 });
    // Not the primary button.
    const t3 = JSON.stringify(viewportTransform(doc));
    pointer(window, svg, "pointerdown", 10, 10, { button: 2 });
    pointer(window, svg, "pointermove", 90, 90);
    pointer(window, svg, "pointerup", 90, 90, { button: 2 });
    h.assert(JSON.stringify(viewportTransform(doc)) === t3, "the secondary button panned");
    // A drag that ends in pointercancel leaves no click to swallow; one that ends in pointerup does (a node's click).
    pointer(window, a, "pointerdown", 100, 100);
    pointer(window, a, "pointermove", 140, 140);
    pointer(window, a, "pointercancel", 140, 140);
    click(window, a);
    h.assert(selectedName(doc) === "p3-graph/a", "a cancelled drag swallowed the next click");
  });

  h.checkAsync("pointer: absurd pointer coordinates leave a finite, drawable view and node", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const svg = doc.querySelector("#okf-graph-canvas svg");
    const a = nodeNamed(doc, "p3-graph/a");
    for (const far of [1e308, -1e308, NaN, Infinity]) {
      pointer(window, svg, "pointerdown", 10, 10);
      pointer(window, svg, "pointermove", far, far);
      pointer(window, svg, "pointerup", far, far);
      pointer(window, a, "pointerdown", 10, 10);
      pointer(window, a, "pointermove", far, far);
      pointer(window, a, "pointerup", far, far);
      // After each throw, not only at the end: a later one can undo an earlier.
      const t = viewportTransform(doc);
      h.assert(Number.isFinite(t.a) && Number.isFinite(t.b) && t.s > 0, `after ${far}: the view is ${JSON.stringify(t)}`);
      const p = labelPoint(a);
      h.assert(Number.isFinite(p.x) && Number.isFinite(p.y), `after ${far}: the node is not at a number`);
      h.assert(!/NaN|Infinity|[0-9]e[+-]?[0-9]/.test(doc.querySelector("#okf-graph-canvas svg").outerHTML), `after ${far}: the drawing holds a non-number or an exponent`);
    }
    click(window, fitButton(doc));
    h.assert(Number.isFinite(viewportTransform(doc).s), "Fit failed after a node was thrown away");
  });

  h.checkAsync("pointer: the tool buttons rest while the list stands in for the drawing, and do nothing", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const buttons = ["Zoom in", "Zoom out"].map((l) => zoomButton(doc, l)).concat([fitButton(doc)]);
    h.assert(buttons.every((b) => !b.disabled), "a tool button is disabled over the drawing");
    click(window, buttons[0]);
    const t0 = JSON.stringify(viewportTransform(doc));
    showList(window, doc);
    h.assert(buttons.every((b) => b.disabled), "a tool button is enabled over the list");
    for (const b of buttons) { click(window, b); }
    // Disabled buttons take no click; the handlers hold on their own (a wheel on the hidden canvas too).
    for (const b of buttons) { b.disabled = false; click(window, b); b.disabled = true; }
    wheel(window, doc.getElementById("okf-graph-canvas"), { deltaY: -100 });
    click(window, doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle"));
    h.assert(buttons.every((b) => !b.disabled), "a tool button stays disabled once the drawing is back");
    h.assert(JSON.stringify(viewportTransform(doc)) === t0, "a tool button acted on the hidden drawing");
  });

  h.checkAsync("pointer: a moved view is not refitted -- by the layout, by the list coming back, by a selection or a restyle", async () => {
    // The layout still running: a pan holds against its next frames.
    {
      const { window, doc, scheduler } = await openGraph(h, { override: { "assets/okf-sim.js": slicedSim(300) } });
      scheduler.frames(2);
      const canvas = doc.getElementById("okf-graph-canvas");
      h.assert(canvas.getAttribute("data-okf-layout") === "running", "setup: the layout is over");
      const svg = canvas.querySelector("svg");
      pointer(window, svg, "pointerdown", 10, 10);
      pointer(window, svg, "pointermove", 70, 50);
      pointer(window, svg, "pointerup", 70, 50);
      const panned = JSON.stringify(viewportTransform(doc));
      const nodes = drawingText(doc).split("|").slice(1).join("|");
      scheduler.flush();
      h.assert(canvas.getAttribute("data-okf-layout") === "done" && drawingText(doc).split("|").slice(1).join("|") !== nodes, "setup: the layout did not go on");
      h.assert(JSON.stringify(viewportTransform(doc)) === panned, "the layout refitted a view the reader had panned");
    }
    // The list and back, after a zoom; after a selection; after a restyle.
    {
      const { window, doc, scheduler } = await openGraph(h);
      scheduler.flush();
      const fitted = viewportTransform(doc);
      click(window, zoomButton(doc, "Zoom in"));
      const zoomed = JSON.stringify(viewportTransform(doc));
      h.assert(!sameTransform(viewportTransform(doc), fitted), "setup: the zoom changed nothing");
      const toggle = doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
      click(window, toggle);
      click(window, toggle);
      h.assert(JSON.stringify(viewportTransform(doc)) === zoomed, "coming back from the list refitted a zoomed view");
      click(window, nodeNamed(doc, "p3-graph/a"));
      h.assert(JSON.stringify(viewportTransform(doc)) === zoomed, "a selection refitted a zoomed view");
      setChecked(window, facetInputs(doc, "display")[0], false);
      scheduler.flush();
      h.assert(JSON.stringify(viewportTransform(doc)) === zoomed, "hiding the labels refitted a zoomed view");
      click(window, fitButton(doc));
      h.assert(sameTransform(viewportTransform(doc), fitted), "Fit did not give the fitted view back");
    }
    // A rebuild after a selection made in a moved view is a fresh layout: fitted, not centred on the selection.
    {
      const { window, doc, scheduler } = await openGraph(h);
      scheduler.flush();
      const index = window.OKF_INDEX;
      const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
      click(window, zoomButton(doc, "Zoom in"));
      click(window, nodeNamed(doc, "p3-graph/b"));
      setChecked(window, facetInputs(doc, "type")[cType], false);
      scheduler.flush();
      const reference = await openGraph(h);
      setChecked(reference.window, facetInputs(reference.doc, "type")[cType], false);
      reference.scheduler.flush();
      h.assert(sameTransform(viewportTransform(doc), viewportTransform(reference.doc)), "a rebuild kept the reader's view");
    }
  });

  h.checkAsync("pointer: a fragment is explicit navigation -- it fits and centres even a moved view; an unknown one fits", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const fitted = viewportTransform(doc);
    const svg = doc.querySelector("#okf-graph-canvas svg");
    click(window, zoomButton(doc, "Zoom in"));
    pointer(window, svg, "pointerdown", 10, 10);
    pointer(window, svg, "pointermove", 70, 50);
    pointer(window, svg, "pointerup", 70, 50);
    let changed = nextHashChange(window);
    window.location.hash = "#p3-graph/b";
    await changed;
    scheduler.flush();
    const t = viewportTransform(doc);
    h.assert(Math.abs(t.s - fitted.s) < 0.001, `the fragment left the zoom at ${t.s}, the fit is ${fitted.s}`);
    const p = labelPoint(nodeNamed(doc, "p3-graph/b"));
    const cx = t.a + p.x * t.s;
    const cy = t.b + (p.y - 15 - 16) * t.s; // a square in the graph context: label baseline at cy + 15 + 16
    h.assert(Math.abs(cx - 400) < 1 && Math.abs(cy - 300) < 1, `the fragment's node is drawn at (${cx}, ${cy}), not centred`);
    click(window, zoomButton(doc, "Zoom in"));
    changed = nextHashChange(window);
    window.location.hash = "#p3-graph/nowhere";
    await changed;
    scheduler.flush();
    h.assert(sameTransform(viewportTransform(doc), fitted), "an unknown fragment did not fit the view");
  });

  h.checkAsync("pointer: dragging a node mid-layout stops the layout; panning or zooming starts none, and pan, zoom and drag create no simulation", async () => {
    const { window, doc, scheduler } = await openGraph(h, { override: { "assets/okf-sim.js": recordingSim(slicedSim(300)) } });
    scheduler.frames(2);
    const sims = window.OKF_TEST_SIMS;
    const canvas = doc.getElementById("okf-graph-canvas");
    const svg = canvas.querySelector("svg");
    h.assert(sims.length === 1 && !sims[0].cancelled && canvas.getAttribute("data-okf-layout") === "running", "setup: the layout is not running");
    pointer(window, svg, "pointerdown", 10, 10);
    pointer(window, svg, "pointermove", 70, 50);
    pointer(window, svg, "pointerup", 70, 50);
    click(window, zoomButton(doc, "Zoom in"));
    h.assert(!sims[0].cancelled && canvas.getAttribute("data-okf-layout") === "running", "a pan or a zoom stopped the layout");
    const a = nodeNamed(doc, "p3-graph/a");
    pointer(window, a, "pointerdown", 100, 100);
    h.assert(!sims[0].cancelled, "a press stopped the layout before it was a drag");
    pointer(window, a, "pointermove", 160, 140);
    h.assert(sims[0].cancelled && canvas.getAttribute("data-okf-layout") === "done", "a node drag left the layout running");
    pointer(window, a, "pointerup", 160, 140);
    const frozen = drawingText(doc);
    scheduler.flush();
    h.assert(drawingText(doc) === frozen, "a frame of the stopped layout moved something");
    pointer(window, svg, "pointerdown", 10, 10);
    pointer(window, svg, "pointermove", 70, 50);
    pointer(window, svg, "pointerup", 70, 50);
    click(window, fitButton(doc));
    h.assert(sims.length === 1 && scheduler.pending() === 0, `${sims.length} simulations, ${scheduler.pending()} frames queued`);
  });

  h.checkAsync("pointer: a drag moves the node's links with it and only them; the layout of the others is untouched", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const lines = () => Array.from(doc.querySelectorAll("#okf-graph-canvas line"), (l) => ["x1", "y1", "x2", "y2"].map((n) => l.getAttribute(n)).join(","));
    const before = lines();
    const a = nodeNamed(doc, "p3-graph/a");
    const s = viewportTransform(doc).s;
    pointer(window, a, "pointerdown", 100, 100);
    pointer(window, a, "pointermove", 100 + 40 * s, 100 + 40 * s);
    pointer(window, a, "pointerup", 100 + 40 * s, 100 + 40 * s);
    const after = lines();
    const changed = after.filter((l, k) => l !== before[k]).length;
    const index = window.OKF_INDEX;
    const aPos = conceptPos(index, "p3-graph/a");
    const touching = index.edges.filter((e) => (e[0] === aPos || (e[3] === 0 && e[1] === aPos)) && !(e[0] === e[1] && e[3] === 0)).length;
    h.assert(changed > 0 && changed <= touching, `${changed} links changed, ${touching} touch p3-graph/a`);
  });

  h.checkAsync("pointer: focus reveals the node -- a zoomed view pans minimally so the focused node and its label stay inside (2.4.11)", async () => {
    const W = 340;
    const H = 260;
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": hubIndexSource(3), "assets/okf-sim.js": fixedSim(FAR_APART) },
      beforeParse: sizedCanvas(W, H),
    });
    scheduler.flush();
    // Fitted, every node is inside and a visit moves nothing.
    const fitted = JSON.stringify(viewportTransform(doc));
    tabStops(doc)[0].focus();
    h.key(window, doc.activeElement, { key: "End" });
    h.key(window, doc.activeElement, { key: "Home" });
    h.assert(JSON.stringify(viewportTransform(doc)) === fitted, "focusing a visible node moved the view");
    const inside = (box) => box.l >= REVEAL_MARGIN - 2 && box.r <= W - REVEAL_MARGIN + 2 && box.t >= REVEAL_MARGIN - 2 && box.b <= H - REVEAL_MARGIN + 2;
    for (let k = 0; k < 6; k++) { click(window, zoomButton(doc, "Zoom in")); }
    const zoomed = viewportTransform(doc);
    h.assert(zoomed.s > 0.9, `setup: zoomed to ${zoomed.s}`);
    const clipped = drawnNodes(doc).filter(({ g }) => !inside(screenBox(window, doc, g))).length;
    h.assert(clipped >= 3, `setup: only ${clipped} of 4 nodes are clipped`);
    // A press on a clipped node focuses it (mousedown): that is not navigation, the view stays under the drag.
    const grabbed = drawnNodes(doc).find(({ g }) => g !== tabStops(doc)[0] && !inside(screenBox(window, doc, g)));
    h.assert(grabbed, "setup: no clipped node to press");
    const held = JSON.stringify(viewportTransform(doc));
    pointer(window, grabbed.g, "pointerdown", 100, 100);
    grabbed.g.focus();
    h.assert(JSON.stringify(viewportTransform(doc)) === held, "focusing a node under the pointer's press moved the view");
    pointer(window, grabbed.g, "pointerup", 100, 100);
    // Page Down, Page Up, End, Home: each lands inside, shifting only what is needed.
    const visit = (key) => {
      const before = viewportTransform(doc);
      h.key(window, doc.activeElement, { key });
      const after = viewportTransform(doc);
      const g = doc.activeElement;
      const box = screenBox(window, doc, g);
      const name = g.querySelector("title").textContent;
      h.assert(Math.abs(after.s - before.s) < 1e-9, `${key} to ${name} changed the scale`);
      h.assert(inside(box), `${key} to ${name}: its box is x ${box.l.toFixed(1)}..${box.r.toFixed(1)}, y ${box.t.toFixed(1)}..${box.b.toFixed(1)} in ${W} x ${H}`);
      // What the reveal keeps inside: the node, its label and its focus contour.
      const c = contourBox(doc, g);
      const u = { l: Math.min(box.l, c.l), r: Math.max(box.r, c.r), t: Math.min(box.t, c.t), b: Math.max(box.b, c.b) };
      for (const [axis, lo, hi, extent] of [["a", u.l, u.r, W], ["b", u.t, u.b, H]]) {
        if (Math.abs(after[axis] - before[axis]) > 0.01) {
          h.assert(Math.abs(lo - REVEAL_MARGIN) < 2 || Math.abs(hi - (extent - REVEAL_MARGIN)) < 2, `${key} to ${name}: shifted ${axis} beyond what the margin needs (${lo.toFixed(1)}..${hi.toFixed(1)} of ${extent})`);
        }
      }
    };
    tabStops(doc)[0].focus();
    visit("Home");
    for (let k = 0; k < 3; k++) { visit("PageDown"); }
    visit("PageUp");
    visit("End");
    visit("Home");
    // The tab stop reached by Tab (focus() with no key) is revealed too.
    const stop = nodeNamed(doc, "leaf3");
    click(window, fitButton(doc));
    for (let k = 0; k < 6; k++) { click(window, zoomButton(doc, "Zoom in")); }
    stop.focus();
    h.assert(inside(screenBox(window, doc, stop)), "Tab arrival left the node outside");
  });

  h.checkAsync("pointer: a pan that only revealed a node is the reader's view: the list's return and the layout leave it", async () => {
    // No zoom, no drag: the canvas shrinks under a fitted view, and the focused node is brought in.
    const size = { w: 600, h: 500 };
    const sized = (w) => {
      for (const [name, key] of [["clientWidth", "w"], ["clientHeight", "h"]]) {
        Object.defineProperty(w.HTMLElement.prototype, name, {
          configurable: true,
          get() { return this.id === "okf-graph-canvas" && !this.hidden ? size[key] : 0; },
        });
      }
    };
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": hubIndexSource(3), "assets/okf-sim.js": fixedSim(FAR_APART) },
      beforeParse: sized,
    });
    scheduler.flush();
    const fitted = viewportTransform(doc);
    size.w = 200;
    size.h = 150;
    const leaf = nodeNamed(doc, "leaf3");
    leaf.focus();
    const b = screenBox(window, doc, leaf);
    h.assert(b.l >= REVEAL_MARGIN - 2 && b.r <= 200 - REVEAL_MARGIN + 2 && b.t >= REVEAL_MARGIN - 2 && b.b <= 150 - REVEAL_MARGIN + 2, "setup: the node was not brought in");
    const revealed = viewportTransform(doc);
    h.assert(!sameTransform(revealed, fitted), "setup: the reveal moved nothing");
    const toggle = doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
    click(window, toggle);
    click(window, toggle);
    h.assert(sameTransform(viewportTransform(doc), revealed), "coming back from the list refitted a revealed view");
    click(window, fitButton(doc));
    h.assert(!sameTransform(viewportTransform(doc), revealed), "setup: Fit did not differ from the revealed view");
  });

  h.checkAsync("pointer: a rebuild under a drag ends it at the pointer's next move; another pointer may then drag", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const index = window.OKF_INDEX;
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    const a = nodeNamed(doc, "p3-graph/a");
    pointer(window, a, "pointerdown", 100, 100);
    pointer(window, a, "pointermove", 140, 140);
    setChecked(window, facetInputs(doc, "type")[cType], false);
    scheduler.flush();
    // Revision 12: the rebuilt drawing keeps a's element (nodes are reused), and the drag still ends.
    h.assert(nodeNamed(doc, "p3-graph/c") === null && nodeNamed(doc, "p3-graph/a") === a, "setup: the filter did not rebuild the drawing, or remade a");
    const rebuilt = drawingText(doc);
    const svg = doc.querySelector("#okf-graph-canvas svg");
    pointer(window, svg, "pointermove", 200, 200);
    h.assert(drawingText(doc) === rebuilt, "a drag went on under a rebuilt drawing");
    const t = viewportTransform(doc);
    pointer(window, svg, "pointerdown", 10, 10, { pointerId: 2 });
    pointer(window, svg, "pointermove", 40, 30, { pointerId: 2 });
    h.assert(Math.abs(viewportTransform(doc).a - t.a - 30) < 0.01, "a dead drag still held the gesture against another pointer");
  });

  h.checkAsync("pointer: ids named like Object.prototype members pan, zoom, drag and reveal like any other", async () => {
    const W = 340;
    const H = 260;
    const ids = ["__proto__", "constructor", "toString"];
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": namedIndexSource(ids, [[0, 1, 1, 0], [1, 2, 1, 0]]), "assets/okf-sim.js": fixedSim([[0, 0], [500, 0], [500, 500]]) },
      beforeParse: sizedCanvas(W, H),
    });
    scheduler.flush();
    h.assert(JSON.stringify(drawnNodes(doc).map((n) => n.name)) === JSON.stringify(ids), "setup: the three ids are not drawn");
    const svg = doc.querySelector("#okf-graph-canvas svg");
    const t0 = viewportTransform(doc);
    pointer(window, svg, "pointerdown", 10, 10);
    pointer(window, svg, "pointermove", 40, 30);
    pointer(window, svg, "pointerup", 40, 30);
    h.assert(Math.abs(viewportTransform(doc).a - t0.a - 30) < 0.01, "pan");
    const t1 = viewportTransform(doc);
    for (const id of ids) {
      const g = nodeNamed(doc, id);
      const p0 = labelPoint(g);
      const was = selectedName(doc);
      pointer(window, g, "pointerdown", 100, 100);
      pointer(window, g, "pointermove", 100 + 10 * t1.s, 100 + 5 * t1.s);
      pointer(window, g, "pointerup", 100 + 10 * t1.s, 100 + 5 * t1.s);
      click(window, g);
      const p1 = labelPoint(g);
      h.assert(Math.abs(p1.x - p0.x - 10) < 0.02 && Math.abs(p1.y - p0.y - 5) < 0.02, `${id} moved by (${p1.x - p0.x}, ${p1.y - p0.y})`);
      h.assert(selectedName(doc) === was, `dragging ${id} changed the selection to ${selectedName(doc)}`);
      click(window, g);
      h.assert(selectedName(doc) === id, `a click on ${id} did not select it`);
    }
    for (let k = 0; k < 6; k++) { click(window, zoomButton(doc, "Zoom in")); }
    tabStops(doc)[0].focus();
    for (const key of ["Home", "PageDown", "PageDown"]) {
      h.key(window, doc.activeElement, { key });
      const b = screenBox(window, doc, doc.activeElement);
      h.assert(b.l >= REVEAL_MARGIN - 2 && b.r <= W - REVEAL_MARGIN + 2 && b.t >= REVEAL_MARGIN - 2 && b.b <= H - REVEAL_MARGIN + 2, `${focusedName(doc)} is clipped after ${key}`);
    }
  });
}

// A canvas whose client size a case changes after the page is drawn.
function resizableCanvas(size) {
  return (w) => {
    for (const [name, key] of [["clientWidth", "w"], ["clientHeight", "h"]]) {
      Object.defineProperty(w.HTMLElement.prototype, name, {
        configurable: true,
        get() { return this.id === "okf-graph-canvas" && !this.hidden ? size[key] : 0; },
      });
    }
  };
}

// The centre of a drawn node in canvas pixels.
function nodeCentre(window, doc, g) {
  const t = viewportTransform(doc);
  const index = window.OKF_INDEX;
  const name = g.querySelector("title").textContent;
  const kind = name.startsWith("absent: ") ? "ghost" : window.OkfShapes.kindOf(index, conceptPos(index, name));
  const half = window.OkfShapes.SIZES.graph[kind].size / 2;
  const p = labelPoint(g);
  return { x: t.a + p.x * t.s, y: t.b + (p.y - half - 16) * t.s };
}

// The focus contour (G14: side + 20, dashed) of a node in canvas pixels, stroke included.
function contourBox(doc, g) {
  const t = viewportTransform(doc);
  const r = g.querySelector(".okf-node-focus");
  const at = nodeOffset(g);
  const [x0, y0, w, hh, sw] = ["x", "y", "width", "height", "stroke-width"].map((a) => Number(r.getAttribute(a)));
  const x = at.x + x0;
  const y = at.y + y0;
  return { l: t.a + (x - sw / 2) * t.s, r: t.a + (x + w + sw / 2) * t.s, t: t.b + (y - sw / 2) * t.s, b: t.b + (y + hh + sw / 2) * t.s };
}

// okf-sim.js followed by a wrapper that records, for each simulation the
// page creates, where it stood when it was cancelled (window.OKF_TEST_SIMS).
function snapshotSim(source) {
  return `${source}\n;(function () {
  var real = window.OkfSim;
  var made = [];
  window.OKF_TEST_SIMS = made;
  window.OkfSim = Object.freeze({
    NODE_LIMIT: real.NODE_LIMIT,
    create: function (graph, options) {
      var sim = real.create(graph, options);
      if (sim === null) { return null; }
      var record = { cancelled: false, atCancel: null };
      made.push(record);
      return Object.freeze({
        step: function () { return sim.step(); },
        positions: function () { return sim.positions(); },
        stats: function () { return sim.stats(); },
        cancel: function () { record.cancelled = true; record.atCancel = Array.from(sim.positions()); return sim.cancel(); },
      });
    },
  });
})();`;
}

function keydown(window, doc, key) {
  doc.dispatchEvent(new window.KeyboardEvent("keydown", { bubbles: true, cancelable: true, key }));
}

function registerPointerFix(h) {
  h.checkAsync("fix I1: a fragment fits and centres even while a node has the focus -- with and without a rebuild", async () => {
    for (const rebuild of [false, true]) {
      const { window, doc, scheduler } = await openGraph(h);
      scheduler.flush();
      const fitted = viewportTransform(doc);
      const index = window.OKF_INDEX;
      const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
      if (rebuild) {
        setChecked(window, facetInputs(doc, "type")[cType], false);
        scheduler.flush();
      }
      const a = nodeNamed(doc, "p3-graph/a");
      a.focus();
      // Zoom about the corner farthest from the focused node: the node is pushed out of the canvas.
      const where = nodeCentre(window, doc, a);
      const canvas = doc.getElementById("okf-graph-canvas");
      for (let k = 0; k < 12; k++) { wheel(window, canvas, { deltaY: -100, clientX: where.x < 400 ? 790 : 10, clientY: where.y < 300 ? 590 : 10 }); }
      const b = screenBox(window, doc, a);
      h.assert(b.l < 0 || b.r > 800 || b.t < 0 || b.b > 600, `setup (rebuild ${rebuild}): the focused node is still inside`);
      const target = rebuild ? "p3-graph/c" : "p3-graph/b";
      const changed = nextHashChange(window);
      window.location.hash = `#${target}`;
      await changed;
      scheduler.flush();
      const t = viewportTransform(doc);
      h.assert(Math.abs(t.s - fitted.s) < 0.001, `rebuild ${rebuild}: the fragment left the scale at ${t.s}, the fit is ${fitted.s}`);
      const c = nodeCentre(window, doc, nodeNamed(doc, target));
      h.assert(Math.abs(c.x - 400) < 1 && Math.abs(c.y - 300) < 1, `rebuild ${rebuild}: ${target} is drawn at (${c.x.toFixed(1)}, ${c.y.toFixed(1)}), not centred`);
      h.assert(selectedName(doc) === target, `rebuild ${rebuild}: ${selectedName(doc)} is selected`);
      h.assert(doc.activeElement && doc.activeElement.matches && doc.activeElement.matches("g.okf-node"), `rebuild ${rebuild}: the focus left the graph`);
    }
  });

  h.checkAsync("fix I2: a touch drag on a node goes on although the browser loses the capture of the shape the node redraws", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const c = nodeNamed(doc, "p3-graph/c");
    const s = viewportTransform(doc).s;
    const p0 = labelPoint(c);
    const touch = { pointerType: "touch", pointerId: 7 };
    pointer(window, c, "pointerdown", 100, 100, touch);
    for (let k = 1; k <= 5; k++) {
      pointer(window, c, "pointermove", 100 + 8 * k, 100 + 6 * k, touch);
      // The shape the touch captured was just replaced: the browser reports the capture lost, at the document.
      doc.dispatchEvent(new window.PointerEvent("lostpointercapture", { bubbles: true, pointerId: 7, pointerType: "touch" }));
    }
    pointer(window, c, "pointerup", 140, 130, touch);
    const p1 = labelPoint(c);
    // s is read to 3 decimals: 40 / s is right to within 40 * 0.0005 / s^2.
    const tol = 0.05 + 40 * 0.0005 / (s * s);
    h.assert(Math.abs(p1.x - p0.x - 40 / s) < tol && Math.abs(p1.y - p0.y - 30 / s) < tol, `the node moved by (${(p1.x - p0.x) * s}, ${(p1.y - p0.y) * s}) px, not (40, 30)`);
    click(window, c);
    h.assert(selectedName(doc) === null, "the dragged node was selected by the click that follows");
    // A background pan is not redrawn under the pointer: a lost capture there is the browser's own, and ends it.
    const t = viewportTransform(doc);
    const svg = doc.querySelector("#okf-graph-canvas svg");
    pointer(window, svg, "pointerdown", 10, 10, touch);
    pointer(window, svg, "pointermove", 40, 30, touch);
    doc.dispatchEvent(new window.PointerEvent("lostpointercapture", { bubbles: true, pointerId: 7, pointerType: "touch" }));
    pointer(window, svg, "pointermove", 90, 90, touch);
    h.assert(Math.abs(viewportTransform(doc).a - t.a - 30) < 0.01, "a pan went on after the capture was lost");
  });

  h.checkAsync("fix M1: the reveal covers the focus contour (G14, side + 20), not only the shape and its label", async () => {
    const W = 340;
    const H = 260;
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": hubIndexSource(3), "assets/okf-sim.js": fixedSim(FAR_APART) },
      beforeParse: sizedCanvas(W, H),
    });
    scheduler.flush();
    for (let k = 0; k < 6; k++) { click(window, zoomButton(doc, "Zoom in")); }
    tabStops(doc)[0].focus();
    for (const key of ["Home", "PageDown", "PageDown", "PageDown", "PageUp", "End"]) {
      h.key(window, doc.activeElement, { key });
      const g = doc.activeElement;
      const box = contourBox(doc, g);
      const name = g.querySelector("title").textContent;
      h.assert(box.l >= 0 && box.r <= W && box.t >= 0 && box.b <= H, `${key} to ${name}: the focus contour is x ${box.l.toFixed(1)}..${box.r.toFixed(1)}, y ${box.t.toFixed(1)}..${box.b.toFixed(1)} in ${W} x ${H}`);
    }
  });

  h.checkAsync("fix M2: a press on a node (right button, touch's compatibility mouse) does not pan the view or make it the reader's; only keyboard focus reveals", async () => {
    const size = { w: 600, h: 500 };
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": hubIndexSource(3), "assets/okf-sim.js": fixedSim(FAR_APART) },
      beforeParse: resizableCanvas(size),
    });
    scheduler.flush();
    const fitted = JSON.stringify(viewportTransform(doc));
    size.w = 200;
    size.h = 150;
    const leaf = nodeNamed(doc, "leaf3");
    const clipped = () => { const b = screenBox(window, doc, leaf); return b.r > 200 || b.b > 150 || b.l < 0 || b.t < 0; };
    h.assert(clipped(), "setup: leaf3 is inside the shrunk canvas");
    // Right button: no gesture starts, the browser focuses the node on mousedown.
    pointer(window, leaf, "pointerdown", 100, 100, { button: 2 });
    leaf.focus();
    pointer(window, leaf, "pointerup", 100, 100, { button: 2 });
    h.assert(JSON.stringify(viewportTransform(doc)) === fitted, "a right-click's focus panned the view");
    leaf.blur();
    // A touch tap: pointerdown, pointerup, then the compatibility mousedown focuses the node.
    pointer(window, leaf, "pointerdown", 100, 100, { pointerType: "touch", pointerId: 3 });
    pointer(window, leaf, "pointerup", 100, 100, { pointerType: "touch", pointerId: 3 });
    leaf.focus();
    h.assert(JSON.stringify(viewportTransform(doc)) === fitted, "a tap's compatibility focus panned the view");
    leaf.blur();
    // Nothing made it the reader's view: the list's return fits the canvas as it is now.
    const toggle = doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
    click(window, toggle);
    click(window, toggle);
    h.assert(JSON.stringify(viewportTransform(doc)) !== fitted, "setup: coming back from the list did not fit the shrunk canvas (the press made the view the reader's)");
    // Keyboard focus (Tab, then the node takes the focus) reveals.
    for (let k = 0; k < 9; k++) { click(window, zoomButton(doc, "Zoom in")); }
    h.assert(clipped(), "setup: leaf3 is not clipped after zooming");
    keydown(window, doc, "Tab");
    leaf.focus();
    h.assert(!clipped(), "Tab arrival did not reveal the node");
  });

  h.checkAsync("fix M2b: a reveal is left alone while a pointer gesture is active, whatever focuses the node", async () => {
    const W = 340;
    const H = 260;
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": hubIndexSource(3), "assets/okf-sim.js": fixedSim(FAR_APART) },
      beforeParse: sizedCanvas(W, H),
    });
    scheduler.flush();
    for (let k = 0; k < 6; k++) { click(window, zoomButton(doc, "Zoom in")); }
    const leaf = nodeNamed(doc, "leaf3");
    const held = JSON.stringify(viewportTransform(doc));
    pointer(window, leaf, "pointerdown", 100, 100);
    keydown(window, doc, "Shift");
    leaf.focus();
    h.assert(JSON.stringify(viewportTransform(doc)) === held, "a node focused under a pointer press moved the view");
    pointer(window, leaf, "pointerup", 100, 100);
  });

  h.checkAsync("fix M3: the click swallowed after a drag is the one that follows its release, not a later one", async () => {
    // (1) a click long after the release (a screen reader's, say) selects.
    {
      const { window, doc, scheduler } = await openGraph(h);
      scheduler.flush();
      const a = nodeNamed(doc, "p3-graph/a");
      pointer(window, a, "pointerdown", 100, 100);
      pointer(window, a, "pointermove", 140, 140);
      const up = new window.PointerEvent("pointerup", { bubbles: true, cancelable: true, clientX: 140, clientY: 140, button: 0, pointerId: 1 });
      a.dispatchEvent(up);
      const late = new window.MouseEvent("click", { bubbles: true, cancelable: true });
      Object.defineProperty(late, "timeStamp", { value: up.timeStamp + 5000 });
      a.dispatchEvent(late);
      h.assert(selectedName(doc) === "p3-graph/a", "a click five seconds after a drag was swallowed");
    }
    // (2) released over the background: the click lands on the svg; the next click on a node selects.
    {
      const { window, doc, scheduler } = await openGraph(h);
      scheduler.flush();
      const a = nodeNamed(doc, "p3-graph/a");
      const svg = doc.querySelector("#okf-graph-canvas svg");
      pointer(window, a, "pointerdown", 100, 100);
      pointer(window, a, "pointermove", 140, 140);
      pointer(window, svg, "pointerup", 140, 140);
      click(window, svg);
      click(window, a);
      h.assert(selectedName(doc) === "p3-graph/a", "a drag released over the background swallowed the next click on a node");
    }
    // (3) a mouse released outside the window: no click will come, so none is swallowed.
    {
      const { window, doc, scheduler } = await openGraph(h);
      scheduler.flush();
      const a = nodeNamed(doc, "p3-graph/a");
      pointer(window, a, "pointerdown", 100, 100, { pointerType: "mouse", buttons: 1 });
      pointer(window, a, "pointermove", 140, 140, { pointerType: "mouse", buttons: 1 });
      pointer(window, a, "pointermove", 150, 150, { pointerType: "mouse", buttons: 0 });
      click(window, a);
      h.assert(selectedName(doc) === "p3-graph/a", "a mouse released outside the window left a click to swallow");
    }
  });

  h.checkAsync("fix pins: wheel units, a clamped no-op, Fit forgetting the fragment, the drag's hold on the view, the last paint before the layout stops", async () => {
    // Wheel units: a line is 33 px, a page 400 (then clamped to a notch).
    {
      const { window, doc, scheduler } = await openGraph(h);
      scheduler.flush();
      const canvas = doc.getElementById("okf-graph-canvas");
      for (const [init, want] of [[{ deltaY: -3, deltaMode: 1 }, Math.pow(1.1, 0.99)], [{ deltaY: 3, deltaMode: 1 }, Math.pow(1.1, -0.99)], [{ deltaY: -1, deltaMode: 2 }, 1.1], [{ deltaY: 1, deltaMode: 2 }, 1 / 1.1]]) {
        const before = viewportTransform(doc).s;
        wheel(window, canvas, init);
        const got = viewportTransform(doc).s / before;
        h.assert(Math.abs(got - want) < 0.003, `wheel ${JSON.stringify(init)} zoomed by ${got}, not ${want}`);
      }
    }
    // A zoom the clamp refuses moves nothing, so the view is still the layout's.
    {
      const size = { w: 40, h: 40 };
      const { window, doc, scheduler } = await openGraph(h, {
        override: { "assets/okf-index.js": hubIndexSource(3), "assets/okf-sim.js": fixedSim(FAR_APART) },
        beforeParse: resizableCanvas(size),
      });
      scheduler.flush();
      h.assert(Math.abs(viewportTransform(doc).s - 0.05) < 0.0005, `setup: the fit is ${viewportTransform(doc).s}, not MIN_SCALE`);
      click(window, zoomButton(doc, "Zoom out"));
      wheel(window, doc.getElementById("okf-graph-canvas"), { deltaY: 100 });
      size.w = 800;
      size.h = 600;
      const toggle = doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
      click(window, toggle);
      click(window, toggle);
      h.assert(viewportTransform(doc).s > 0.5, "a zoom the clamp refused made the view the reader's");
    }
    // Fit fits: it forgets the fragment that once centred the view.
    {
      const { window, doc, scheduler } = await openGraph(h, { hash: "#p3-graph/b" });
      scheduler.flush();
      click(window, zoomButton(doc, "Zoom in"));
      click(window, fitButton(doc));
      const reference = await openGraph(h);
      reference.scheduler.flush();
      h.assert(sameTransform(viewportTransform(doc), viewportTransform(reference.doc)), "Fit centred on the fragment's node instead of fitting");
    }
    // The press that turns into a node drag holds the view, mid-layout and once the layout is over.
    {
      const { window, doc, scheduler } = await openGraph(h, { override: { "assets/okf-sim.js": slicedSim(300) } });
      scheduler.frames(6);
      const a = nodeNamed(doc, "p3-graph/a");
      const before = JSON.stringify(viewportTransform(doc));
      pointer(window, a, "pointerdown", 100, 100);
      pointer(window, a, "pointermove", 104, 100);
      h.assert(JSON.stringify(viewportTransform(doc)) === before, "starting a node drag refitted the view");
      pointer(window, a, "pointerup", 104, 100);
    }
    {
      const { window, doc, scheduler } = await openGraph(h);
      scheduler.flush();
      const a = nodeNamed(doc, "p3-graph/a");
      const s = viewportTransform(doc).s;
      pointer(window, a, "pointerdown", 100, 100);
      pointer(window, a, "pointermove", 100 + 300 * s, 100 + 300 * s);
      pointer(window, a, "pointerup", 100 + 300 * s, 100 + 300 * s);
      const before = JSON.stringify(viewportTransform(doc));
      const toggle = doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
      click(window, toggle);
      click(window, toggle);
      h.assert(JSON.stringify(viewportTransform(doc)) === before, "coming back from the list refitted a view the reader had dragged a node in");
    }
    // The layout stops where it stands, not where it was last drawn.
    {
      const { window, doc, scheduler } = await openGraph(h, { override: { "assets/okf-sim.js": snapshotSim(slicedSim(300)) } });
      scheduler.frames(40);
      const canvas = doc.getElementById("okf-graph-canvas");
      h.assert(canvas.getAttribute("data-okf-layout") === "running", "setup: the layout is over");
      const names = drawnNodes(doc).map((n) => n.name);
      const e = nodeNamed(doc, "p3-graph/e");
      const shown = labelPoint(e);
      const a = nodeNamed(doc, "p3-graph/a");
      pointer(window, a, "pointerdown", 100, 100);
      pointer(window, a, "pointermove", 120, 100);
      pointer(window, a, "pointerup", 120, 100);
      const record = window.OKF_TEST_SIMS[0];
      h.assert(record.cancelled && record.atCancel, "setup: the layout was not cancelled");
      const slot = names.indexOf("p3-graph/e");
      const now = labelPoint(e);
      h.assert(Math.abs(now.x - record.atCancel[2 * slot]) < 0.01, `p3-graph/e is drawn at x ${now.x}, the layout stopped at ${record.atCancel[2 * slot]}`);
      h.assert(Math.abs(now.x - shown.x) > 0.01 || Math.abs(now.y - shown.y) > 0.01, "setup: the layout had not moved since the last drawing");
    }
  });
}

function registerStyles(h) {
  // §12.6: the graph page's real chrome keeps its style (a selector the
  // engine cannot match would pass the body-content check by styling
  // nothing), and the same class names worn by body content change nothing.
  // jsdom cascades viewer.css but does no layout and applies no @media rule,
  // so the wide-layout rules are checked on a copy with @media unwrapped.
  h.checkAsync("styles: the graph page's chrome keeps its style; body content wearing its classes does not", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const style = (el, prop) => window.getComputedStyle(el).getPropertyValue(prop);
    const probes = [
      [doc.getElementById("okf-graph-status"), "position", "absolute"],
      [doc.getElementById("okf-graph-status"), "font-size", "12px"],
      [doc.querySelector("#okf-facets .okf-facet-row"), "height", "32px"],
      [doc.querySelector("#okf-facets .okf-facet-display .okf-facet-row"), "height", "30px"],
      [doc.querySelector("#okf-graph-zoom .okf-graph-zoom-step"), "width", "34px"],
      [doc.getElementById("okf-graph-canvas"), "position", "absolute"],
      [doc.querySelector("#okf-graph-canvas .okf-graph-label"), "font-size", "11.5px"],
    ];
    for (const [el, prop, value] of probes) {
      h.assert(el, `a chrome element this case needs is missing (${prop}: ${value})`);
      h.assert(style(el, prop) === value, `<${el.tagName.toLowerCase()} class="${el.getAttribute("class")}"> ${prop}: ${style(el, prop)}, expected ${value}`);
    }
    showList(window, doc);
    click(window, Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).find((b) => b.textContent === "p3-graph/b"));
    h.assert(style(doc.querySelector("#okf-graph-detail .okf-graph-open"), "height") === "40px", "Open page lost its height");
    const wide = doc.createElement("style");
    wide.textContent = h.unwrapMedia(fs.readFileSync(path.join(ASSETS, "viewer.css"), "utf8"));
    doc.head.appendChild(wide);
    h.assert(style(doc.getElementById("okf-facets"), "width") === "270px", "the facets panel is not 270 px wide (G2)");
    h.assert(style(doc.getElementById("okf-graph-detail"), "width") === "340px", "the drawer is not 340 px wide (G17)");

    const page = await h.openPage("p3-chrome-classes.html");
    const body = page.document.getElementById("okf-body");
    const reference = body.querySelector("code:not([class])");
    const worn = Array.from(body.querySelectorAll("code[class]"));
    h.assert(reference && worn.length >= 2 && worn[0].classList.contains("okf-graph-layout"), "the fixture lost its classed <code> elements");
    const sheet = page.document.createElement("style");
    sheet.textContent = h.unwrapMedia(fs.readFileSync(path.join(ASSETS, "viewer.css"), "utf8"));
    page.document.head.appendChild(sheet);
    const props = ["position", "display", "width", "height", "font-size", "color", "background", "border", "padding",
      "margin", "opacity", "overflow", "cursor", "pointer-events", "flex-direction", "inset", "top", "left"];
    const expected = page.getComputedStyle(reference);
    for (const el of worn) {
      const got = page.getComputedStyle(el);
      for (const prop of props) {
        h.assert(got.getPropertyValue(prop) === expected.getPropertyValue(prop),
          `<code class="${el.getAttribute("class")}"> ${prop}: ${got.getPropertyValue(prop)} (an unclassed <code> has ${expected.getPropertyValue(prop)})`);
      }
    }
  });
  // What the shared chip and row rules (one id's weight, from their :is() anchor)
  // would otherwise win: this page's own overrides must keep out-ranking them.
  h.checkAsync("styles: a tag chip wraps instead of widening the panel, an absent row is not drawn like a link, a long facet name clamps to two lines", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const style = (el, prop) => window.getComputedStyle(el).getPropertyValue(prop);
    const chip = doc.querySelector("#okf-facets .okf-facet-tags .okf-chip");
    h.assert(chip, "the fixture has no tag chip");
    h.assert(style(chip, "white-space") === "normal", `a tag chip is white-space: ${style(chip, "white-space")}, so a long tag widens the panel`);
    h.assert(style(chip, "height") === "auto", `a tag chip is ${style(chip, "height")} high, so a wrapped tag overflows it`);
    h.assert(style(chip, "max-width") === "100%", "a tag chip may outgrow its panel");
    const name = doc.querySelector("#okf-facets .okf-facet-row .okf-facet-name");
    h.assert(style(name, "display") === "-webkit-box" && style(name, "-webkit-line-clamp") === "2", `a facet name is display ${style(name, "display")}, line-clamp ${style(name, "-webkit-line-clamp")}`);
    showList(window, doc);
    click(window, Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select")).find((b) => b.textContent === "p3-graph/hostile"));
    const rows = Array.from(doc.querySelectorAll("#okf-graph-detail .okf-row"));
    const absent = rows.find((r) => r.classList.contains("okf-graph-absent"));
    const link = rows.find((r) => r.tagName === "A");
    h.assert(absent && link, "the hostile concept lost its absent or linked row");
    h.assert(style(absent, "color") !== style(link, "color"), `an absent row is drawn in the link colour (${style(absent, "color")})`);
    h.assert(/ghost|#c0392b|rgb(192, 57, 43)/.test(style(absent, "color")), `an absent row is not in the ghost colour (${style(absent, "color")})`);
  });
}

// --- fix wave after final review B -----------------------------------------

// The hostile site's index with `code` run on it once the page has assigned
// it (`i` is window.OKF_INDEX): a damaged index the generator never writes.
function damagedIndex(code) {
  const source = fs.readFileSync(path.join(__dirname, "..", ".generated", "hostile-site", "assets", "okf-index.js"), "utf8");
  return { "assets/okf-index.js": `${source}\n;(function () { var i = window.OKF_INDEX; ${code} })();` };
}

// What the graph page must make of a damaged index, derived from the index
// alone: an entry that is not an object with a string id is skipped, and so
// is every edge that is not an array naming two such entries by position.
function validView(index) {
  const isEntry = (v) => v !== null && typeof v === "object" && typeof v.id === "string";
  const concepts = [];
  for (let i = 0; i < index.concepts.length; i++) { if (isEntry(index.concepts[i])) { concepts.push(i); } }
  const okConcept = new Set(concepts);
  const okGhost = new Set();
  for (let g = 0; g < index.ghosts.length; g++) { if (isEntry(index.ghosts[g])) { okGhost.add(g); } }
  let links = 0;
  const cited = new Set();
  for (const e of index.edges) {
    if (!Array.isArray(e) || !okConcept.has(e[0])) { continue; }
    if (e[3] === 1 ? okGhost.has(e[1]) : okConcept.has(e[1])) {
      links++;
      if (e[3] === 1) { cited.add(e[1]); }
    }
  }
  const names = concepts.map((i) => index.concepts[i].id)
    .concat(Array.from(cited).sort((a, b) => a - b).map((g) => `absent: ${index.ghosts[g].id}`));
  let status = `showing ${concepts.length} of ${concepts.length} ${concepts.length === 1 ? "concept" : "concepts"}`;
  if (cited.size > 0) { status += ` + ${cited.size} absent`; }
  status += ` ${MIDDOT} ${links} of ${links} ${links === 1 ? "link" : "links"}`;
  return { names, status };
}

// matchMedia answering `matches` for prefers-reduced-motion, false otherwise.
function reducedMotion(matches) {
  return (w) => {
    w.matchMedia = (text) => ({
      media: text,
      matches: text === "(prefers-reduced-motion: reduce)" ? matches : false,
      addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {},
    });
  };
}

function registerFixWave(h) {
  h.checkAsync("damaged index: null, sparse and wrong-typed entries are skipped, never thrown on; the valid rest draws, lists, filters and selects, and the palette works", async () => {
    const variants = {
      "concepts[0] = null": "i.concepts[0] = null;",
      "a sparse concepts array": "i.concepts.length = i.concepts.length + 5;",
      "tags = 5": "i.concepts[1].tags = 5;",
      "id = 42": "i.concepts[2].id = 42;",
      "a cited null ghost": "i.ghosts.push(null); i.edges.push([0, i.ghosts.length - 1, 1, 1]);",
      "types[0] = null": "i.types[0] = null;",
      "edges out of range": "i.edges.push([0, 99999, 1, 0], [99999, 0, 1, 0], [0, 99999, 1, 1], [-1, 0, 1, 0], [1.5, 0, 1, 0], [NaN, 1, 1, 0], null, 'x', [0]);",
      "a NaN weight": "i.edges.push([0, 1, NaN, 0]);",
      "wrong-typed fields": "var c = i.concepts[3]; c.title = {}; c.type = null; c.trust = 7; c.typeIndex = '0'; c.staleAfterMs = 'x'; c.staleAfterDate = 5; c.description = 5; c.path = null;"
        + " i.concepts[4].tags = [5, null, {}, 'graph-core']; i.types[1].name = null; i.types[1].count = 'x'; i.types[1].slot = 99;",
    };
    for (const [name, code] of Object.entries(variants)) {
      const { window, doc, scheduler } = await openGraph(h, { override: damagedIndex(code) });
      scheduler.flush();
      const want = validView(window.OKF_INDEX);
      const status = doc.getElementById("okf-graph-status").textContent;
      h.assert(status === want.status, `${name}: status "${status}", expected "${want.status}"`);
      const names = drawnNodes(doc).map((n) => n.name);
      h.assert(JSON.stringify(names) === JSON.stringify(want.names), `${name}: drawn ${names.join(" | ")}`);
      h.assert(doc.getElementById("okf-graph-canvas").getAttribute("data-okf-layout") === "done", `${name}: the layout did not end`);
      showList(window, doc);
      const listed = Array.from(doc.querySelectorAll("#okf-graph-list .okf-graph-list-select"), (b) => b.textContent);
      h.assert(JSON.stringify(listed) === JSON.stringify(want.names), `${name}: listed ${listed.join(" | ")}`);
      click(window, doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle"));
      // Filters and selection still run: every type box off and on, every node selected.
      for (const box of facetInputs(doc, "type")) { setChecked(window, box, false); setChecked(window, box, true); }
      for (const box of facetInputs(doc, "trust")) { setChecked(window, box, false); setChecked(window, box, true); }
      for (const chip of doc.querySelectorAll("#okf-facets button.okf-chip")) { click(window, chip); click(window, chip); }
      scheduler.flush();
      for (const { g, name: node } of drawnNodes(doc)) {
        click(window, g);
        h.assert(selectedName(doc) === node, `${name}: clicking ${node} selected ${selectedName(doc)}`);
        // graph.html is at the site root: a page resolved from no path would be "".
        h.assert(Array.from(doc.querySelectorAll("#okf-graph-detail a[href]")).every((a) => a.getAttribute("href") !== ""), `${name}: ${node}'s drawer links to no page`);
      }
      const dangling = Array.from(doc.querySelectorAll("#okf-graph-layout a[href]")).filter((a) => /undefined|null|object/.test(a.getAttribute("href")));
      h.assert(dangling.length === 0, `${name}: a link to ${dangling.map((a) => a.getAttribute("href")).join(", ")}`);
      if (name === "wrong-typed fields") {
        // A concept whose path is not a string has no page: no "Open page", no
        // Reading view to it, no row linking to it, and a double click goes nowhere.
        const c = window.OKF_INDEX.concepts[3].id;
        const g = nodeNamed(doc, c);
        click(window, g);
        const detail = doc.getElementById("okf-graph-detail");
        h.assert(detail.querySelector(".okf-graph-detail-id").textContent === c && !detail.querySelector("a.okf-graph-open"), `${name}: ${c} has an Open page`);
        h.assert(doc.getElementById("okf-reading-view").getAttribute("href") === "index.html", `${name}: Reading view points at ${doc.getElementById("okf-reading-view").getAttribute("href")}`);
        const before = h.navigations(window);
        g.dispatchEvent(new window.MouseEvent("dblclick", { bubbles: true }));
        h.assert(h.navigations(window) === before, `${name}: a double click on ${c} navigated`);
      }
      h.assert(!/undefined|\[object/.test(doc.getElementById("okf-graph-layout").textContent), `${name}: a damaged field reached the page as text`);
      // The palette on the same page.
      click(window, doc.querySelector("#okf-tools .okf-palette-open"));
      h.type(window, doc.getElementById("okf-palette-input"), "p3-graph");
      h.assert(h.paletteOptions(window).length > 0, `${name}: the palette finds nothing`);
    }
  });

  h.checkAsync("fragment: a percent-encoded fragment is decoded before the lookup, on load and on hashchange (§12.5)", async () => {
    for (const [hash, id] of [["#%5F%5Fproto%5F%5F", "__proto__"], ["#p3-graph%2Fb", "p3-graph/b"], ["#%70%33-graph%2F%63", "p3-graph/c"]]) {
      const { doc, scheduler } = await openGraph(h, { hash });
      scheduler.flush();
      h.assert(selectedName(doc) === id, `${hash} selected ${selectedName(doc)}, not ${id}`);
    }
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    for (const [hash, id] of [["#p3-graph%2Fd", "p3-graph/d"], ["#%5F%5Fproto%5F%5F", "__proto__"]]) {
      const changed = nextHashChange(window);
      window.location.hash = hash;
      await changed;
      h.assert(selectedName(doc) === id, `a hashchange to ${hash} selected ${selectedName(doc)}, not ${id}`);
    }
  });

  h.checkAsync("reduced motion: the layout still runs in slices, but only its start and its end are drawn (final review B, 7)", async () => {
    const sim = { "assets/okf-sim.js": slicedSim(300) };
    const calm = await openGraph(h, { override: sim, beforeParse: reducedMotion(true) });
    const canvas = calm.doc.getElementById("okf-graph-canvas");
    const start = drawingText(calm.doc);
    let frames = 0;
    while (calm.scheduler.pending() > 0) {
      calm.scheduler.frames(1);
      frames++;
      if (canvas.getAttribute("data-okf-layout") === "running") {
        h.assert(drawingText(calm.doc) === start, `frame ${frames} drew an intermediate layout`);
      }
      h.assert(frames < 100000, "the layout did not end");
    }
    h.assert(frames > 25, `setup: the layout took ${frames} frames, too few to repaint`);
    h.assert(canvas.getAttribute("data-okf-layout") === "done", "the layout did not end");
    // Its end is the layout drawn with motion, which does show the steps in between.
    const moving = await openGraph(h, { override: sim, beforeParse: reducedMotion(false) });
    const seen = new Set([drawingText(moving.doc)]);
    while (moving.scheduler.pending() > 0) { moving.scheduler.frames(1); seen.add(drawingText(moving.doc)); }
    h.assert(drawingText(calm.doc) === drawingText(moving.doc) && drawingText(calm.doc) !== start, "the final drawing is not the layout's end");
    h.assert(seen.size > 2, "setup: with motion, no step in between was drawn either");
  });

  h.checkAsync("list: the marked entry is centred in the list by scrolling the list alone, never the page (block: center)", async () => {
    const ROW = 30;
    const VIEW = 200;
    const TOP = 100;
    const HEAD = 40;
    const intoView = [];
    const geometry = (w) => {
      w.Element.prototype.scrollIntoView = function (arg) { intoView.push(arg); };
      Object.defineProperty(w.HTMLElement.prototype, "scrollTop", {
        configurable: true,
        get() { return this.okfTop || 0; },
        set(v) { this.okfTop = v; },
      });
      Object.defineProperty(w.HTMLElement.prototype, "clientHeight", {
        configurable: true,
        get() { return this.id === "okf-graph-list" && !this.hidden ? VIEW : 0; },
      });
      w.Element.prototype.getBoundingClientRect = function () {
        const list = w.document.getElementById("okf-graph-list");
        const rect = (top, height) => ({ top, bottom: top + height, height, left: 0, right: 300, width: 300, x: 0, y: top });
        if (this === list) { return rect(TOP, VIEW); }
        const item = this.closest && this.closest(".okf-graph-list-item");
        if (!item) { return rect(0, 0); }
        const k = Array.prototype.indexOf.call(item.parentElement.children, item);
        const top = TOP - list.scrollTop + HEAD + k * ROW;
        return item === this ? rect(top, ROW) : rect(top, 18);
      };
    };
    const { window, doc } = await openGraph(h, { hash: "#leaf38", beforeParse: geometry, override: { "assets/okf-index.js": hubIndexSource(40) } });
    showList(window, doc);
    const list = doc.getElementById("okf-graph-list");
    const centred = (k) => Math.max(0, HEAD + k * ROW - (VIEW - ROW) / 2);
    h.assert(intoView.length === 0, `scrollIntoView was called (${JSON.stringify(intoView)}): it scrolls the page too`);
    h.assert(list.scrollTop === centred(38), `the list scrolled to ${list.scrollTop}, not ${centred(38)} (leaf38 centred)`);
    click(window, Array.from(list.querySelectorAll(".okf-graph-list-select")).find((b) => b.textContent === "leaf20"));
    h.assert(intoView.length === 0 && list.scrollTop === centred(20), `selecting leaf20 scrolled the list to ${list.scrollTop}, not ${centred(20)}`);
    click(window, Array.from(list.querySelectorAll(".okf-graph-list-select")).find((b) => b.textContent === "hub"));
    h.assert(list.scrollTop === 0, `selecting the first entry scrolled the list to ${list.scrollTop}`);
    // An entry already wholly in view is left where it is.
    click(window, Array.from(list.querySelectorAll(".okf-graph-list-select")).find((b) => b.textContent === "leaf2"));
    h.assert(list.scrollTop === 0, `selecting leaf2, in view, scrolled the list to ${list.scrollTop}`);
    h.assert(intoView.length === 0, "scrollIntoView was called");
  });

  h.checkAsync("list: above the limit a long list is built in chunks across frames, in index order; a new shown set restarts it, and the marked entry is marked", async () => {
    const count = 1200;
    const { window, doc, scheduler } = await openGraph(h, { hash: "#leaf1100", override: { "assets/okf-index.js": hubIndexSource(count), "assets/okf-sim.js": lowLimitSim(3) } });
    const list = doc.getElementById("okf-graph-list");
    const items = () => list.querySelectorAll(".okf-graph-list-item");
    const names = () => Array.from(list.querySelectorAll(".okf-graph-list-select"), (b) => b.textContent);
    const marked = () => Array.from(list.querySelectorAll('.okf-graph-list-select[aria-current="true"]'), (b) => b.textContent);
    const all = ["hub"].concat(Array.from({ length: count }, (_, k) => `leaf${k + 1}`));
    h.assert(!list.hidden, "setup: the list is not shown above the limit");
    const drain = (when) => {
      let last = items().length;
      h.assert(last > 0 && last < all.length, `${when}: ${last} of ${all.length} entries were built in one task`);
      for (let frames = 0; scheduler.pending() > 0; frames++) {
        scheduler.frames(1);
        const now = items().length;
        h.assert(now - last <= 500, `${when}: one frame built ${now - last} entries`);
        last = now;
        h.assert(frames < 10000, `${when}: the list never ended`);
      }
      h.assert(JSON.stringify(names()) === JSON.stringify(all), `${when}: ${names().length} entries, not the index in order`);
      h.assert(JSON.stringify(marked()) === JSON.stringify(["leaf1100"]), `${when}: marked ${marked()}`);
    };
    drain("on load");
    // A change of the shown set mid-build: the old build stops, the new one is whole.
    const unverified = facetInputs(doc, "trust")[2];
    setChecked(window, unverified, false);
    h.assert(list.hidden, "setup: nothing shown, yet the list stayed");
    setChecked(window, unverified, true);
    scheduler.frames(1);
    setChecked(window, unverified, false);
    // From here, only the last build makes entries: the earlier one, cut off
    // with a frame to go, makes none.
    let made = 0;
    const create = doc.createElement.bind(doc);
    doc.createElement = (tag, options) => {
      if (String(tag).toLowerCase() === "li") { made++; }
      return create(tag, options);
    };
    setChecked(window, unverified, true);
    // A change that keeps the shown set does not restart it.
    const firstItem = items()[0];
    setChecked(window, facetInputs(doc, "display")[0], false);
    h.assert(items()[0] === firstItem, "a style change rebuilt the list");
    drain("after a restart");
    h.assert(made === list.querySelectorAll("li").length, `${made} list items made for the ${list.querySelectorAll("li").length} of the list: an older build went on`);
    doc.createElement = create;
    // The selection made while it builds is marked once its entry is built.
    click(window, list.querySelector(".okf-graph-list-select"));
    setChecked(window, unverified, false);
    setChecked(window, unverified, true);
    const changed = nextHashChange(window);
    window.location.hash = "#leaf1100";
    await changed;
    drain("after a fragment");
  });

  h.checkAsync("drawing: a layout frame moves the drawn nodes in place -- no element is created or removed while the layout runs (final review B, 1)", async () => {
    const { window, doc, scheduler } = await openGraph(h, { override: { "assets/okf-sim.js": slicedSim(300) } });
    const canvas = doc.getElementById("okf-graph-canvas");
    const observer = new window.MutationObserver(() => {});
    observer.observe(canvas, { childList: true, subtree: true });
    const before = drawingText(doc);
    scheduler.flush();
    const records = observer.takeRecords();
    observer.disconnect();
    h.assert(drawingText(doc) !== before, "setup: nothing moved");
    h.assert(records.length === 0, `${records.length} child-list mutations while the layout ran`);
    assertFixedSvg(h, doc);
  });

  h.checkAsync("drawing: a facet change keeps every node it still draws; a node drawn again is the same one, with no state left from before", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const index = window.OKF_INDEX;
    const before = new Map(drawnNodes(doc).map((n) => [n.name, n.g]));
    const lines = Array.from(doc.querySelectorAll("#okf-graph-canvas line"));
    const cType = index.concepts[conceptPos(index, "p3-graph/c")].typeIndex;
    const c = nodeNamed(doc, "p3-graph/c");
    click(window, c);
    c.focus();
    h.assert(c.classList.contains("okf-selected") && c.classList.contains("okf-focused"), "setup: c is not selected and focused");
    setChecked(window, facetInputs(doc, "type")[cType], false);
    scheduler.flush();
    const during = drawnNodes(doc);
    h.assert(nodeNamed(doc, "p3-graph/c") === null && during.length < before.size, "setup: the type did not hide c");
    h.assert(during.every((n) => before.get(n.name) === n.g), "a node still drawn was rebuilt");
    click(window, nodeNamed(doc, "p3-graph/a"));
    setChecked(window, facetInputs(doc, "type")[cType], true);
    scheduler.flush();
    const after = drawnNodes(doc);
    h.assert(JSON.stringify(after.map((n) => n.name)) === JSON.stringify(Array.from(before.keys())), "the nodes drawn again are not in index order");
    h.assert(after.every((n) => before.get(n.name) === n.g), "a node drawn again was rebuilt");
    h.assert(!c.classList.contains("okf-selected") && !c.hasAttribute("aria-current") && !c.classList.contains("okf-focused") && c.getAttribute("tabindex") === "-1",
      `c came back with its old state: class "${c.getAttribute("class")}", tabindex ${c.getAttribute("tabindex")}`);
    const linesAfter = Array.from(doc.querySelectorAll("#okf-graph-canvas line"));
    h.assert(linesAfter.length === lines.length && linesAfter.every((l, k) => l === lines[k]), "a link drawn again was rebuilt, or the links lost their order");
    h.assert(doc.querySelectorAll("#okf-graph-canvas svg").length === 1, "more than one drawing");
    assertFixedSvg(h, doc);
  });
}

// okf-sim.js followed by a stand-in whose layout grows: `points` scaled by
// `growth` at each step, every step 25 iterations (one paint), `steps` of
// them, then done.
function growingSim(points, growth, steps) {
  return `${fs.readFileSync(SIM_FILE, "utf8")}\n;(function () {
  var real = window.OkfSim;
  var points = ${JSON.stringify(points)};
  window.OkfSim = Object.freeze({
    NODE_LIMIT: real.NODE_LIMIT,
    create: function (graph) {
      var k = 0;
      function at() {
        var f = Math.pow(${growth}, k);
        var flat = new Float64Array(2 * graph.nodeCount);
        for (var i = 0; i < graph.nodeCount; i++) { flat[2 * i] = points[i][0] * f; flat[2 * i + 1] = points[i][1] * f; }
        return flat;
      }
      return Object.freeze({
        step: function () { if (k < ${steps}) { k++; } return { done: k === ${steps}, iterations: 25 * k, work: 1 }; },
        positions: at,
        stats: function () { return {}; },
        cancel: function () {},
      });
    },
  });
})();`;
}

// A hub and `count` leaves on a grid, `gap` apart, centred on the origin.
function gridPoints(count, gap) {
  const side = Math.ceil(Math.sqrt(count + 1));
  return Array.from({ length: count + 1 }, (_, i) => [((i % side) - side / 2) * gap, (Math.floor(i / side) - side / 2) * gap]);
}

function registerFixWavePaint(h) {
  h.checkAsync("drawing: a large graph is painted at most 500 nodes a frame, links included, then fitted in a frame of its own; done only then", async () => {
    const count = 1200;
    const points = gridPoints(count, 50);
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": hubIndexSource(count), "assets/okf-sim.js": fixedSim(points) },
      beforeParse: sizedCanvas(800, 600),
    });
    const canvas = doc.getElementById("okf-graph-canvas");
    const viewport = doc.querySelector("#okf-graph-canvas g.okf-graph-viewport");
    const observer = new window.MutationObserver(() => {});
    observer.observe(canvas, { attributes: true, subtree: true, attributeFilter: ["transform"] });
    h.assert(canvas.getAttribute("data-okf-layout") === "running", "setup: the first paint of 1201 nodes was not spread over frames");
    const frames = [];
    while (scheduler.pending() > 0) {
      scheduler.frames(1);
      const records = observer.takeRecords();
      frames.push({ nodes: records.filter((r) => r.target !== viewport).length, view: records.some((r) => r.target === viewport), running: canvas.getAttribute("data-okf-layout") === "running" });
      h.assert(frames.length < 100, "the paint never ended");
    }
    observer.disconnect();
    h.assert(frames.every((f) => f.nodes <= 500), `a frame moved ${Math.max(...frames.map((f) => f.nodes))} nodes`);
    const fitted = frames.filter((f) => f.view);
    h.assert(fitted.length === 1 && fitted[0].nodes === 0 && frames[frames.length - 1] === fitted[0], `the view changed in ${fitted.length} frames, or with nodes moving: ${JSON.stringify(frames)}`);
    h.assert(frames.slice(0, -1).every((f) => f.running) && canvas.getAttribute("data-okf-layout") === "done", "the layout was done before its last paint");
    // Every node where the layout put it, every link from edge to edge of its two nodes.
    const nodes = drawnNodes(doc);
    nodes.forEach(({ g, name }, slot) => {
      const at = nodeOffset(g);
      h.assert(Math.abs(at.x - points[slot][0]) < 0.006 && Math.abs(at.y - points[slot][1]) < 0.006, `${name} is at (${at.x}, ${at.y}), not (${points[slot]})`);
    });
    const lines = Array.from(doc.querySelectorAll("#okf-graph-canvas line"));
    h.assert(lines.length === count, `${lines.length} links drawn`);
    const half = window.OkfShapes.SIZES.graph[window.OkfShapes.kindOf(window.OKF_INDEX, 0)].size / 2;
    lines.forEach((line, k) => {
      const [x1, y1, x2, y2] = ["x1", "y1", "x2", "y2"].map((a) => Number(line.getAttribute(a)));
      const a = points[0];
      const b = points[k + 1];
      const da = Math.hypot(x1 - a[0], y1 - a[1]);
      const db = Math.hypot(x2 - b[0], y2 - b[1]);
      h.assert(Math.abs(da - half) < 0.02 && Math.abs(db - half - 2) < 0.02, `link ${k} runs from ${da} off the hub to ${db} off leaf${k + 1}`);
    });
    assertFitted(h, window, doc, 800, 600, "after the sliced paint");
    assertFixedSvg(h, doc);
  });

  h.checkAsync("drawing: while a large graph's layout runs its view changes scale by steps, never leaving the drawing outside; its end is fitted exactly", async () => {
    const count = 1200;
    const steps = 80;
    const { window, doc, scheduler } = await openGraph(h, {
      override: { "assets/okf-index.js": hubIndexSource(count), "assets/okf-sim.js": growingSim(gridPoints(count, 6), 1.02, steps) },
      beforeParse: sizedCanvas(800, 600),
    });
    const canvas = doc.getElementById("okf-graph-canvas");
    const scales = [];
    let fits = 0;
    let last = viewportTransform(doc).s;
    while (scheduler.pending() > 0) {
      const before = doc.querySelector("#okf-graph-canvas g.okf-graph-viewport").getAttribute("transform");
      scheduler.frames(1);
      const after = doc.querySelector("#okf-graph-canvas g.okf-graph-viewport").getAttribute("transform");
      if (canvas.getAttribute("data-okf-layout") !== "running") { break; }
      if (after !== before) {
        fits++;
        const b = fittedBox(window, doc);
        h.assert(b.minX >= -1 && b.maxX <= 801 && b.minY >= -1 && b.maxY <= 601, `a running fit left the drawing outside the canvas: x ${b.minX}..${b.maxX}, y ${b.minY}..${b.maxY}`);
        if (b.scale !== last) { scales.push(b.scale); last = b.scale; }
      }
      h.assert(fits < 1000, "the layout never ended");
    }
    h.assert(fits >= 8, `setup: only ${fits} running fits`);
    h.assert(scales.length > 0 && scales.length <= fits / 2, `the scale changed at ${scales.length} of ${fits} running fits: ${scales.join(", ")}`);
    scheduler.flush();
    h.assert(canvas.getAttribute("data-okf-layout") === "done", "the layout did not end");
    assertFitted(h, window, doc, 800, 600, "at the end of a growing layout");
  });

  h.checkAsync("drawing: a selection restyles only the links whose state changes (an unchanged attribute is not set again)", async () => {
    const { window, doc, scheduler } = await openGraph(h);
    scheduler.flush();
    const canvas = doc.getElementById("okf-graph-canvas");
    const observer = new window.MutationObserver(() => {});
    observer.observe(canvas, { attributes: true, subtree: true });
    const index = window.OKF_INDEX;
    const touching = (id) => {
      const k = conceptPos(index, id);
      return index.edges.filter((e) => e[0] === k || (e[3] === 0 && e[1] === k)).length;
    };
    click(window, nodeNamed(doc, "p3-graph/b"));
    let lines = new Set(observer.takeRecords().filter((r) => r.target.localName === "line").map((r) => r.target));
    h.assert(lines.size > 0 && lines.size <= touching("p3-graph/b"), `selecting b touched ${lines.size} links, b has ${touching("p3-graph/b")}`);
    setChecked(window, facetInputs(doc, "display")[0], false);
    lines = observer.takeRecords().filter((r) => r.target.localName === "line");
    h.assert(lines.length === 0, `hiding the labels touched ${lines.length} links`);
    observer.disconnect();
  });
}

function register(h) {
  registerSim(h);
  registerPurity(h);
  registerGraphPage(h);
  registerFacets(h);
  registerDrawing(h);
  registerFragment(h);
  registerKeyboard(h);
  registerPointer(h);
  registerPointerFix(h);
  registerStyles(h);
  registerFixWave(h);
  registerFixWavePaint(h);
}

module.exports = { register };

// `node cases/p3.js` runs the simulation cases alone, with local check and
// assert, so okf-sim.js can be built before run.js loads cases/ (§12.7).
if (require.main === module) {
  let passed = 0;
  let failed = 0;
  const h = Object.freeze({
    check(name, fn) {
      try {
        fn();
        passed++;
        console.log(`  ok  - ${name}`);
      } catch (err) {
        failed++;
        console.log(`FAIL  - ${name}`);
        console.log(`        ${err.message}`);
      }
    },
    assert(condition, message) {
      if (!condition) { throw new Error(message); }
    },
  });
  registerSim(h);
  registerPurity(h);
  console.log(`\n${passed} passed, ${failed} failed`);
  process.exit(failed === 0 ? 0 : 1);
}
