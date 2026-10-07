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
// It returns the objects the module is given.
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
  + "  return { win: {}, math: restricted };\n"
  + "})()";

// The ONE way this file runs okf-sim.js (or a variant of it): a fresh V8
// context prepared by PRELUDE, then the module as the body of a STRICT
// function whose parameters shadow every ambient name it must not use --
// Math holds only the allowed functions (any other call throws), and
// document, Date, performance, requestAnimationFrame, setTimeout, self and
// globalThis are undefined; `this` is undefined at the module's top level.
// The module still sees its own `window`, created inside the context.
// Returns the window the module wrote to (window.OkfSim, and whatever a
// fault-injection variant records on it).
function sandboxLoad(source) {
  const context = vm.createContext({});
  const env = vm.runInContext(PRELUDE, context, { filename: "okf-sim-sandbox-prelude.js" });
  const wrapper = vm.runInContext(
    "(function (window, Math, document, Date, performance, requestAnimationFrame, setTimeout, self, globalThis) {\"use strict\";\n"
      + source + "\n})",
    context,
    { filename: SIM_FILE });
  wrapper(env.win, env.math);
  return env.win;
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
// counts the units the module must count. Summation order differs from the
// module's, so positions are compared to a tolerance, work exactly.
function referenceRun(initial, n, edges, maxIterations) {
  const SPACING = 60;
  const CELL = 120;
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
        if (d2 < CELL * CELL) { fx[i] += dx * SPACING * SPACING / d2; fy[i] += dy * SPACING * SPACING / d2; }
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
      [{ nodeCount: 3, edges: new Array(4294967295) }, undefined],
      [{ nodeCount: 3, edges: new Array(1000000000) }, undefined],
    ];
    for (const [graph, options] of bad) {
      let threw = null;
      let shown;
      try { shown = `${JSON.stringify(graph)}, ${JSON.stringify(options)}`; } catch (e) { shown = "(unprintable)"; }
      try { OkfSim.create(graph, options); } catch (e) { threw = e; }
      h.assert(threw && threw.name === "TypeError", `create(${shown}) did not throw a TypeError (${threw && threw.name})`);
    }
    // Documented order: graph, nodeCount, edges array and options are checked
    // first; above NODE_LIMIT the call then returns null without examining
    // the edge entries.
    h.assert(OkfSim.create({ nodeCount: 5000, edges: [[0, 9999], null] }) === null, "above NODE_LIMIT the edge entries were examined");
    h.assert(OkfSim.create({ nodeCount: 5000, edges: new Array(4294967295) }) === null, "a 5000-node graph with a sparse edge list was not refused with null");
    let threw = null;
    try { OkfSim.create({ nodeCount: 5000, edges: [] }, { sliceWork: 0 }); } catch (e) { threw = e; }
    h.assert(threw && threw.name === "TypeError", "bad options were not rejected above NODE_LIMIT");
  });

  h.check("sim: an edge is read once -- a getter that changes its answer cannot smuggle in an out-of-range node", () => {
    const OkfSim = loadSim();
    const edge = [];
    let reads = 0;
    Object.defineProperty(edge, "0", { get() { reads++; return reads === 1 ? 0 : 7; }, enumerable: true });
    edge[1] = 1;
    const s = OkfSim.create({ nodeCount: 3, edges: [edge] }, { maxIterations: 4 });
    h.assert(s !== null, "a valid edge (as first read) was refused");
    runToEnd(h, s, 100000, "toctou");
    assertFiniteAndBounded(h, s.positions(), 3, "toctou");
    h.assert(reads === 1, `the edge's first field was read ${reads} times`);
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
    // always in the adjacent cells (-1,-1) and (0,-1) of the 120-wide grid, so
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
  const SNAPSHOT_COMPONENTS_40 = "54cbd2f8";
  const SNAPSHOT_DENSE_24 = "5278e311";
  const SNAPSHOT_DENSE_1 = "de107342";

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
}

// --- purity guards for okf-sim.js (spec §4.2, §7 control 2) -----------------

// Walks the source and returns { code, findings }. `code` is the source with
// comments removed and the CONTENTS of string literals blanked (delimiters
// kept), so text inside a string or a comment can neither hide code (a
// string holding two slashes does not start a comment) nor look like code
// (a string reading Math.sin is not a call). `findings` lists constructs the
// static check refuses outright, because it cannot see through them: template
// literals, regular-expression literals, and any backslash-u or backslash-x
// escape (inside a string or an identifier).
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
    if (c === "/") {
      // After an operand it is division; anywhere else it opens a regular
      // expression literal, which this check does not parse.
      if (previous === "" || "(,=:[!&|?{};+-*%<>~^".includes(previous)) { findings.push("a regular-expression literal"); }
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
  // executed or not, and refuses what it cannot see through (template and
  // regular-expression literals, backslash-u and backslash-x escapes). It does
  // NOT prove the module is pure: an alias assembled at run time by a
  // construct it does not parse would pass. The sandbox is the executable
  // half, and proves only what the cases actually EXECUTE: sandboxLoad()
  // gives the module a Math rebuilt inside a fresh context from that
  // context's own Math (allowed functions only), poisons the Function
  // constructors, deletes the clock/random/dynamic-code globals, and runs it
  // strict with `this` undefined, so a forbidden call on an executed path
  // throws.
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
    const quiet = "(function () { var u = 'http://x/*y*/ Math.sin ** this'; var d = (4 + 2) / 3 / 1; window.OkfSim = u + d; })();";
    h.assert(purityFindings(quiet).length === 0, `strings or divisions were flagged: ${purityFindings(quiet).join(", ")}`);
    h.assert(codeOf("var s = 'a//b'; // c").includes("var s ="), "codeOf dropped code after a string holding //");
  });

  h.check("sim purity: the sandbox refuses every route to a real clock, random or forbidden Math function", () => {
    // Positive control: the sandbox runs a module that only uses what it may.
    const ok = sandboxLoad("window.r = Math.sqrt(16) + Math.max(1, 2) + Math.imul(2, 3);");
    h.assert(ok.r === 12, `the sandbox failed to run an allowed module: ${ok.r}`);
    const hostile = [
      ["Math.sin", "window.r = Math.sin(1);"],
      ["Math.random", "window.r = Math.random();"],
      ["assigning into Math", "Math.sin = function () { return 0; };"],
      ["Math.sqrt.constructor (the Function constructor via an allowed function)", "window.r = Math.sqrt.constructor('return Ma' + 'th.sin(0)')();"],
      ["[].constructor.constructor", "window.r = [].constructor.constructor('return Ma' + 'th.sin(0)')();"],
      ["new Map().constructor.constructor reaching Date.now", "window.r = new Map().constructor.constructor('return Da' + 'te.now()')();"],
      ["a function value's constructor", "window.r = (function () {}).constructor('return 1')();"],
      ["the async function constructor", "window.r = (async function () {}).constructor('return 1');"],
      ["the generator function constructor", "window.r = (function* () {}).constructor('return 1');"],
      ["the async generator function constructor", "window.r = (async function* () {}).constructor('return 1');"],
      ["this['Ma'+'th'].sin at the top level", "window.r = this['Ma' + 'th'].sin(0);"],
      ["Date.now()", "window.r = Date.now();"],
      ["new Date()", "window.r = new Date();"],
      ["eval", "window.r = eval('1');"],
      ["the Function global", "window.r = Function('return 1')();"],
      ["Intl", "window.r = new Intl.DateTimeFormat().format();"],
      ["WeakRef", "window.r = new WeakRef({});"],
      ["FinalizationRegistry", "window.r = new FinalizationRegistry(function () {});"],
      ["performance.now()", "window.r = performance.now();"],
      ["setTimeout", "window.r = setTimeout(function () {}, 0);"],
      ["document", "window.r = document.body;"],
    ];
    for (const [name, code] of hostile) {
      let threw = null;
      try { sandboxLoad(code); } catch (e) { threw = e; }
      h.assert(threw !== null, `the sandbox let this through: ${name}`);
    }
    // The module's own loader runs through the same function.
    h.assert(typeof loadSim().create === "function", "loadSim() does not go through sandboxLoad()");
  });
}

function register(h) {
  registerSim(h);
  registerPurity(h);
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
