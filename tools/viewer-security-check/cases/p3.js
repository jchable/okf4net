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

// --- simulation (okf-sim.js) ------------------------------------------------

// The exact Math functions §4.2 allows; everything else is approximated per
// engine and would break cross-engine determinism.
const ALLOWED_MATH = ["sqrt", "floor", "ceil", "trunc", "abs", "min", "max", "imul"];

// Runs okf-sim.js in a fresh V8 context, wrapped in a function whose
// parameters shadow every ambient name the module must not use: Math holds
// only the allowed functions (any other call throws a TypeError), and
// document, Date, performance, requestAnimationFrame, setTimeout, self and
// globalThis are undefined. The module still sees its own `window`.
function loadSim() {
  const source = fs.readFileSync(SIM_FILE, "utf8");
  const math = {};
  for (const name of ALLOWED_MATH) { math[name] = Math[name]; }
  Object.freeze(math);
  const win = {};
  const wrapper = vm.runInNewContext(
    "(function (window, Math, document, Date, performance, requestAnimationFrame, setTimeout, self, globalThis) {\n"
      + source + "\n})",
    {},
    { filename: SIM_FILE });
  wrapper(win, math);
  return win.OkfSim;
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

// Shapes of control 1 (spec §7): empty, singleton, edgeless, clumped,
// repeated edges, two components, and a large graph at the node limit.
function shapes(OkfSim) {
  return {
    empty: { nodeCount: 0, edges: [] },
    singleton: { nodeCount: 1, edges: [] },
    edgeless: { nodeCount: 50, edges: [] },
    clumped: { nodeCount: 30, edges: complete(30) },
    repeated: { nodeCount: 3, edges: Array.from({ length: 50 }, () => [0, 1]).concat([[1, 0], [1, 2]]) },
    components: { nodeCount: 9, edges: [[0, 1], [1, 2], [2, 0], [3, 4], [4, 5]] },
    path: { nodeCount: 40, edges: path_(40) },
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
      [{ nodeCount: 3, edges: [] }, { sliceWork: 0 }], [{ nodeCount: 3, edges: [] }, { maxIterations: 1.5 }],
      [{ nodeCount: 3, edges: [] }, { cellCap: -2 }], [{ nodeCount: 3, edges: [] }, "fast"],
    ];
    for (const [graph, options] of bad) {
      let threw = null;
      try { OkfSim.create(graph, options); } catch (e) { threw = e; }
      h.assert(threw && threw.name === "TypeError", `create(${JSON.stringify(graph)}, ${JSON.stringify(options)}) did not throw a TypeError`);
    }
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

  h.check("sim: the initial layout is a grid in index order, before any step", () => {
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
    }
  });

  h.check("sim: every shape stays finite and bounded, iterations within the bound, slices within budget", () => {
    const OkfSim = loadSim();
    const all = shapes(OkfSim);
    for (const name of Object.keys(all)) {
      const graph = all[name];
      const maxIterations = name === "large" ? 3 : 60;
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

  h.check("sim: work is counted per candidate pair visited, capped per cell", () => {
    const OkfSim = loadSim();
    // 30 nodes pulled into one clump by a complete graph: without the cap a
    // node would visit every member of its crowded cell.
    const graph = shapes(OkfSim).clumped;
    const cellCap = 4;
    const s = OkfSim.create(graph, { maxIterations: 40, sliceWork: 1000000, cellCap });
    runToEnd(h, s, 1000000, "clumped");
    const perIteration = s.stats().totalWork / 40;
    const n = graph.nodeCount;
    const ceiling = n * 9 * cellCap + graph.edges.length + n;
    h.assert(perIteration <= ceiling, `${perIteration} units per iteration exceeds n*9*cellCap + edges + n = ${ceiling}`);
    // Every node visits at least itself in its own cell, every spring and
    // every integration counts: a floor that a counter skipping pairs breaks.
    h.assert(perIteration >= n + graph.edges.length + n, `${perIteration} units per iteration: pairs or springs are not counted`);
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

  h.check("sim: positions() is a copy of the last complete iteration", () => {
    const OkfSim = loadSim();
    const s = OkfSim.create(shapes(OkfSim).path, { maxIterations: 5, sliceWork: 10 });
    const before = s.positions();
    const r = s.step();
    h.assert(r.iterations === 0 && r.work === 10, `the first slice of 10 units completed an iteration: ${JSON.stringify(r)}`);
    h.assert(sameBits(s.positions(), before), "positions moved before an iteration completed");
    const copy = s.positions();
    copy[0] = 123456;
    h.assert(s.positions()[0] !== 123456, "positions() returned the live buffer");
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
  });
}

// Source text with comments removed (block, then line comments). okf-sim.js
// holds no string with "//" or "/*" in it, which this naive stripping
// would otherwise cut.
function codeOf(source) {
  return source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/[^\n]*/g, "");
}

// The names okf-sim.js must not reach, beyond Math: anything that reads a
// clock, a random source, the DOM, a frame callback or another global.
const FORBIDDEN_NAMES = ["Date", "performance", "random", "requestAnimationFrame", "setTimeout", "setInterval",
  "document", "globalThis", "self", "eval", "Function", "OKF_SCHEDULER", "OKF_INDEX"];

// Findings of the static check, as readable strings (empty = clean).
function purityFindings(source) {
  const code = codeOf(source);
  const found = [];
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
  // forbidden Math function, the ** operator, a clock, the DOM or another
  // global written anywhere in the file, executed or not -- but not one
  // reached by a disguise it does not parse (an alias assembled at run
  // time). The runtime half below is the executable guard for the paths
  // the cases above execute: loadSim() runs the module with a Math that
  // holds only the allowed functions and with the DOM, clock and frame
  // names undefined, so any such call made by a case throws.
  h.check("sim purity: okf-sim.js calls no Math function outside §4.2 and no clock, DOM or other global (smoke check)", () => {
    const findings = purityFindings(fs.readFileSync(SIM_FILE, "utf8"));
    h.assert(findings.length === 0, `okf-sim.js uses: ${findings.join(", ")}`);
  });

  h.check("sim purity: the static check itself flags each forbidden form", () => {
    const clean = "(function () { window.OkfSim = Object.freeze({ a: Math.sqrt(2) + Math.imul(3, 4) }); })();";
    h.assert(purityFindings(clean).length === 0, `a clean module was flagged: ${purityFindings(clean).join(", ")}`);
    const dirty = [
      "var a = Math.sin(1);", "var a = Math.pow(2, 3);", "var a = Math.round(2.5);", "var a = 2 ** 3;",
      "var a = Math['cos'](1);", "var m = Math; var a = m.exp(1);", "var a = Date.now();", "var a = Math.random();",
      "var a = performance.now();", "requestAnimationFrame(f);", "var d = document.body;", "var g = globalThis;",
      "var w = window.innerWidth;",
    ];
    for (const line of dirty) {
      const source = clean.replace("window.OkfSim", `${line} window.OkfSim`);
      h.assert(purityFindings(source).length > 0, `not flagged: ${line}`);
    }
    h.assert(purityFindings(clean + "\n// Math.sin and ** in a comment are not code\n").length === 0, "a comment was flagged");
  });

  h.check("sim purity: in the sandbox a forbidden Math function throws (the runtime guard is live)", () => {
    const math = {};
    for (const name of ALLOWED_MATH) { math[name] = Math[name]; }
    Object.freeze(math);
    const probe = vm.runInNewContext("(function (Math, Date) { var r = []; try { Math.sin(1); r.push('sin ran'); } catch (e) { r.push(e.name); } r.push(typeof Date); return r.join(); })", {});
    const got = probe(math);
    h.assert(got === "TypeError,undefined", `the sandbox let a forbidden call through: ${got}`);
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
