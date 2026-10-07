// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Layout of the global graph (spec §4.2, §12.5): a pure force simulation --
// no DOM, no clock, no Math.random, no global but its own. Same input, same
// initial layout on every conforming engine, because it uses only + - * / %,
// Math.sqrt (correctly rounded by ECMAScript) and the exact Math.floor,
// ceil, trunc, abs, min, max and imul: never another Math function and never
// the ** operator, whose results are implementation-approximated.
//
// Work is counted, never timed: one unit per candidate pair visited (whether
// or not a force results), per spring and per node integrated. step() does
// at most options.sliceWork units and may stop INSIDE an iteration; its
// cursors and force accumulators are kept, and the next step() resumes with
// the same sequence of operations. Positions change only when an iteration
// completes, so the state after k iterations does not depend on how the work
// was sliced. tools/viewer-security-check/cases/p3.js is the guard.
(function () {
  "use strict";

  var NODE_LIMIT = 1500;
  var DEFAULT_OPTIONS = { maxIterations: 200, sliceWork: 100000, cellCap: 24 };

  var SPACING = 60;              // ideal edge length, in graph units
  var K2 = SPACING * SPACING;
  var CELL = 2 * SPACING;        // grid cell side, and repulsion cut-off
  var CUTOFF2 = CELL * CELL;
  var MIN_D2 = 0.01;             // below this, two nodes count as coincident
  var GRAVITY = 0.02;
  var JITTER = SPACING / 4;
  var OFFSET = 4096;             // cell coordinates are shifted by this...
  var STRIDE = 8192;             // ...and packed into one integer key

  var REPULSE = 0;
  var SPRING = 1;
  var INTEGRATE = 2;
  var DONE = 3;

  function isInt(v) {
    return typeof v === "number" && Number.isInteger(v);
  }

  function readOptions(options) {
    var out = {
      maxIterations: DEFAULT_OPTIONS.maxIterations,
      sliceWork: DEFAULT_OPTIONS.sliceWork,
      cellCap: DEFAULT_OPTIONS.cellCap,
    };
    if (options === undefined || options === null) { return out; }
    if (typeof options !== "object") { throw new TypeError("OkfSim.create: options must be an object"); }
    var names = ["maxIterations", "sliceWork", "cellCap"];
    for (var k = 0; k < names.length; k++) {
      var v = options[names[k]];
      if (v === undefined) { continue; }
      if (!isInt(v) || v <= 0) { throw new TypeError("OkfSim.create: options." + names[k] + " must be an integer > 0"); }
      out[names[k]] = v;
    }
    return out;
  }

  // 32-bit linear congruential generator on an integer state: exact.
  function nextState(state) {
    return (Math.imul(state, 1664525) + 1013904223) >>> 0;
  }

  // Square grid in index order (side ceil(sqrt(n))), each node shifted by a
  // jitter drawn from the generator. The seed depends on the node and edge
  // counts only.
  function initialLayout(n, edgeCount) {
    var pos = new Float64Array(2 * n);
    var side = Math.ceil(Math.sqrt(n));
    var half = (side - 1) / 2;
    var state = (Math.imul(n, 73856093) + Math.imul(edgeCount + 1, 19349663)) >>> 0;
    for (var i = 0; i < n; i++) {
      var col = i % side;
      var row = Math.floor(i / side);
      state = nextState(state);
      var jx = (state / 4294967296 - 0.5) * 2 * JITTER;
      state = nextState(state);
      var jy = (state / 4294967296 - 0.5) * 2 * JITTER;
      pos[2 * i] = (col - half) * SPACING + jx;
      pos[2 * i + 1] = (row - half) * SPACING + jy;
    }
    return pos;
  }

  function cellOf(v) {
    return Math.floor(v / CELL);
  }

  function cellKey(cx, cy) {
    return (cx + OFFSET) * STRIDE + (cy + OFFSET);
  }

  // Appends node i to its cell. Callers add nodes in index order, so every
  // cell lists its members in ascending order.
  function addToGrid(grid, i, x, y) {
    var key = cellKey(cellOf(x), cellOf(y));
    var list = grid.get(key);
    if (list === undefined) {
      list = [];
      grid.set(key, list);
    }
    list.push(i);
  }

  function createSimulation(n, edges, opts) {
    var edgeCount = edges.length / 2;
    var pos = initialLayout(n, edgeCount);
    var nextPos = new Float64Array(2 * n);
    var fx = new Float64Array(n);
    var fy = new Float64Array(n);
    // Every coordinate is clamped to [-limit, limit] after each move.
    var limit = SPACING * (Math.ceil(Math.sqrt(n)) + 2) * 4;
    var grid = new Map();
    for (var g = 0; g < n; g++) { addToGrid(grid, g, pos[2 * g], pos[2 * g + 1]); }
    var nextGrid = new Map();

    var iterations = 0;
    var totalWork = 0;
    var maxSliceWork = 0;
    var cancelled = false;
    var phase = n <= 1 ? DONE : REPULSE;
    var temperature = SPACING;

    // REPULSE cursors: node, neighbour cell (0..8), member of that cell,
    // members visited in it, the cell's member list, and the node's force.
    var node = 0;
    var cell = 0;
    var member = 0;
    var visited = 0;
    var list = null;
    var ax = 0;
    var ay = 0;
    // SPRING and INTEGRATE cursors.
    var edge = 0;
    var inode = 0;

    function finishIteration() {
      var swap = pos;
      pos = nextPos;
      nextPos = swap;
      grid = nextGrid;
      nextGrid = new Map();
      iterations++;
      inode = 0;
      temperature = SPACING * (1 - iterations / opts.maxIterations);
      phase = iterations === opts.maxIterations ? DONE : REPULSE;
    }

    function step() {
      var work = 0;
      while (work < opts.sliceWork && phase !== DONE && !cancelled) {
        if (phase === REPULSE) {
          if (list === null) {
            if (cell === 9) {
              fx[node] = ax;
              fy[node] = ay;
              ax = 0;
              ay = 0;
              cell = 0;
              node++;
              if (node === n) {
                node = 0;
                phase = edgeCount > 0 ? SPRING : INTEGRATE;
              }
              continue;
            }
            var cx = cellOf(pos[2 * node]) + (cell % 3) - 1;
            var cy = cellOf(pos[2 * node + 1]) + Math.floor(cell / 3) - 1;
            var found = grid.get(cellKey(cx, cy));
            if (found === undefined) {
              cell++;
              continue;
            }
            list = found;
            member = 0;
            visited = 0;
          }
          var j = list[member];
          member++;
          visited++;
          work++;
          if (j !== node) {
            var dx = pos[2 * node] - pos[2 * j];
            var dy = pos[2 * node + 1] - pos[2 * j + 1];
            var d2 = dx * dx + dy * dy;
            if (d2 < CUTOFF2) {
              if (d2 < MIN_D2) {
                // Coincident nodes: a fixed separation decided by the pair's
                // order, opposite for the two members of the pair.
                var sign = node < j ? -1 : 1;
                dx = sign * 0.1;
                dy = sign * 0.1 * ((node + j) % 3 - 1);
                d2 = dx * dx + dy * dy;
              }
              var f = K2 / d2;
              ax += dx * f;
              ay += dy * f;
            }
          }
          if (member === list.length || visited === opts.cellCap) {
            list = null;
            cell++;
          }
        } else if (phase === SPRING) {
          var s = edges[2 * edge];
          var t = edges[2 * edge + 1];
          var ex = pos[2 * t] - pos[2 * s];
          var ey = pos[2 * t + 1] - pos[2 * s + 1];
          var pull = Math.sqrt(ex * ex + ey * ey) / SPACING;
          fx[s] += ex * pull;
          fy[s] += ey * pull;
          fx[t] -= ex * pull;
          fy[t] -= ey * pull;
          work++;
          edge++;
          if (edge === edgeCount) {
            edge = 0;
            phase = INTEGRATE;
          }
        } else {
          var x = pos[2 * inode];
          var y = pos[2 * inode + 1];
          var vx = fx[inode] - GRAVITY * x;
          var vy = fy[inode] - GRAVITY * y;
          var m2 = vx * vx + vy * vy;
          var nx = x;
          var ny = y;
          if (m2 > 0) {
            var m = Math.sqrt(m2);
            var move = Math.min(m, temperature);
            nx = x + vx / m * move;
            ny = y + vy / m * move;
          }
          if (!Number.isFinite(nx) || !Number.isFinite(ny)) {
            nx = x;
            ny = y;
          }
          nx = Math.max(-limit, Math.min(limit, nx));
          ny = Math.max(-limit, Math.min(limit, ny));
          nextPos[2 * inode] = nx;
          nextPos[2 * inode + 1] = ny;
          addToGrid(nextGrid, inode, nx, ny);
          work++;
          inode++;
          if (inode === n) { finishIteration(); }
        }
      }
      totalWork += work;
      if (work > maxSliceWork) { maxSliceWork = work; }
      return { done: phase === DONE || cancelled, iterations: iterations, work: work };
    }

    return Object.freeze({
      step: step,
      positions: function () { return new Float64Array(pos); },
      stats: function () {
        return { iterations: iterations, totalWork: totalWork, maxSliceWork: maxSliceWork };
      },
      cancel: function () { cancelled = true; },
    });
  }

  // graph: { nodeCount, edges: [[source, target], ...] }, nodes numbered in
  // index order (spec §12.5). Returns null above NODE_LIMIT (A6: the page
  // then asks the reader to narrow the filters).
  function create(graph, options) {
    if (!graph || typeof graph !== "object") { throw new TypeError("OkfSim.create: graph must be an object"); }
    var n = graph.nodeCount;
    if (!isInt(n) || n < 0) { throw new TypeError("OkfSim.create: nodeCount must be an integer >= 0"); }
    if (n > NODE_LIMIT) { return null; }
    if (!Array.isArray(graph.edges)) { throw new TypeError("OkfSim.create: edges must be an array"); }
    var edges = new Int32Array(2 * graph.edges.length);
    for (var k = 0; k < graph.edges.length; k++) {
      var e = graph.edges[k];
      if (!Array.isArray(e) || e.length !== 2 || !isInt(e[0]) || !isInt(e[1])
          || e[0] < 0 || e[0] >= n || e[1] < 0 || e[1] >= n || e[0] === e[1]) {
        throw new TypeError("OkfSim.create: edges[" + k + "] must be two distinct node numbers below nodeCount");
      }
      edges[2 * k] = e[0];
      edges[2 * k + 1] = e[1];
    }
    return createSimulation(n, edges, readOptions(options));
  }

  window.OkfSim = Object.freeze({
    NODE_LIMIT: NODE_LIMIT,
    create: create,
  });
})();
