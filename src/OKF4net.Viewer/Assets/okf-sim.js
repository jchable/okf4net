// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Layout of the global graph (spec §4.2, §12.5): a pure force simulation --
// no DOM, no clock, no Math.random, no global but its own. Same input, same
// initial layout on every conforming engine, because it uses only + - * / % >>>,
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
//
// Boxes (spec §12.5, revision 11): a graph may give, per node, the box its
// drawing covers -- shape and label -- as [halfWidth, above, below] around the
// centre. Two boxes closer than GAP push each other apart along the axis where
// they overlap least, a spring pulls only across the clearance between its two
// boxes, and the last fifth of the iterations does nothing but separate boxes
// that still overlap. Without boxes the force law is the plain one.
(function () {
  "use strict";

  var NODE_LIMIT = 1500;
  var DEFAULT_OPTIONS = { maxIterations: 200, sliceWork: 100000, cellCap: 24 };

  var SPACING = 60;              // ideal edge length, in graph units
  var K2 = SPACING * SPACING;
  var CUTOFF2 = 4 * K2;          // repulsion cut-off: 2 x SPACING
  var GAP = 8;                   // clearance kept between two boxes
  var CELL = 180;                // grid cell side: every interaction is shorter
  var BOX_LIMIT = (CELL - GAP) / 2;  // so two overlapping boxes share a 3 x 3 neighbourhood
  var COLLIDE = 10;              // push per unit of overlap
  var SETTLE = 5;                // the last 1/SETTLE of the iterations only separates boxes...
  var SETTLE_PUSH = 0.9;         // ...each member of a pair moving 0.9 of their overlap
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
    var given = Object.keys(options);
    for (var u = 0; u < given.length; u++) {
      if (names.indexOf(given[u]) < 0) { throw new TypeError("OkfSim.create: unknown option \"" + given[u] + "\""); }
    }
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

  // How far a box reaches from its centre along a direction (ax, ay), both
  // >= 0 and not both 0: the half-width over ax or the half-height over ay,
  // whichever wall the direction meets first.
  function reach(boxes, i, ax, ay) {
    var halfHeight = (boxes[3 * i + 1] + boxes[3 * i + 2]) / 2;
    if (ax === 0) { return halfHeight / ay; }
    if (ay === 0) { return boxes[3 * i] / ax; }
    return Math.min(boxes[3 * i] / ax, halfHeight / ay);
  }

  function createSimulation(n, edges, boxes, opts) {
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
    // From this iteration on, only overlapping boxes move (never without boxes).
    var settleFrom = boxes === null ? opts.maxIterations : opts.maxIterations - Math.floor(opts.maxIterations / SETTLE);
    var settling = false;

    // REPULSE cursors: node, neighbour cell (0..8), the cell's member list,
    // where the scan of that list starts, how many members to visit in it and
    // how many were visited, and the node's force.
    var node = 0;
    var cell = 0;
    var start = 0;
    var take = 0;
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
      settling = iterations >= settleFrom;
    }

    // The push node i gets from j when their boxes (with GAP) overlap: along
    // the axis of the smaller overlap, away from j; a tie at the same
    // coordinate is broken by the pair's order. Settling, each member of a
    // pair moves SETTLE_PUSH of the overlap (both together: more than all of it).
    function collide(i, j) {
      var bx = pos[2 * i] - pos[2 * j];
      var by = pos[2 * i + 1] - pos[2 * j + 1];
      var ox = boxes[3 * i] + boxes[3 * j] + GAP - Math.abs(bx);
      if (ox <= 0) { return; }
      var oy = by >= 0
        ? boxes[3 * i + 1] + boxes[3 * j + 2] + GAP - by
        : boxes[3 * i + 2] + boxes[3 * j + 1] + GAP + by;
      if (oy <= 0) { return; }
      var strength = settling ? SETTLE_PUSH : COLLIDE;
      var away = i < j ? -1 : 1;
      if (ox <= oy) {
        ax += strength * ox * (bx > 0 ? 1 : bx < 0 ? -1 : away);
      } else {
        ay += strength * oy * (by > 0 ? 1 : by < 0 ? -1 : away);
      }
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
                phase = edgeCount > 0 && !settling ? SPRING : INTEGRATE;
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
            visited = 0;
            // A crowded cell is scanned for at most cellCap members. The scan
            // starts at an offset fixed by the node and the iteration, so the
            // members that fall outside the cap rotate instead of always being
            // the highest-numbered ones; the offset does not depend on how the
            // work was sliced.
            start = (node + iterations) % list.length;
            take = Math.min(list.length, opts.cellCap);
          }
          var j = list[(start + visited) % list.length];
          visited++;
          work++;
          if (j !== node) {
            var dx = pos[2 * node] - pos[2 * j];
            var dy = pos[2 * node + 1] - pos[2 * j + 1];
            var d2 = dx * dx + dy * dy;
            if (d2 < CUTOFF2 && !settling) {
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
            if (boxes !== null) { collide(node, j); }
          }
          if (visited === take) {
            list = null;
            cell++;
          }
        } else if (phase === SPRING) {
          var s = edges[2 * edge];
          var t = edges[2 * edge + 1];
          var ex = pos[2 * t] - pos[2 * s];
          var ey = pos[2 * t + 1] - pos[2 * s + 1];
          var d = Math.sqrt(ex * ex + ey * ey);
          var pull = d / SPACING;
          if (boxes !== null && d > 0) {
            // Across the clearance g between the two boxes (a box's centre sits
            // (below - above) / 2 under its node's): e * g^2 / (SPACING * d),
            // that is g^2 / SPACING along the link; nothing once they touch.
            var ty = ey + (boxes[3 * t + 2] - boxes[3 * t + 1] - boxes[3 * s + 2] + boxes[3 * s + 1]) / 2;
            var dc = Math.sqrt(ex * ex + ty * ty);
            var clear = dc > 0 ? dc - reach(boxes, s, Math.abs(ex) / dc, Math.abs(ty) / dc) - reach(boxes, t, Math.abs(ex) / dc, Math.abs(ty) / dc) : 0;
            pull = clear > 0 ? clear * clear / (SPACING * d) : 0;
          }
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
          var gravity = settling ? 0 : GRAVITY;
          var vx = fx[inode] - gravity * x;
          var vy = fy[inode] - gravity * y;
          var m2 = vx * vx + vy * vy;
          var nx = x;
          var ny = y;
          if (m2 > 0) {
            var m = Math.sqrt(m2);
            var move = Math.min(m, settling ? SPACING : temperature);
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

  // Each box entry is read once, each of its three fields once; a field above
  // BOX_LIMIT is clamped to it.
  function readBoxes(given, n) {
    if (given.length !== n) { throw new TypeError("OkfSim.create: boxes must have one entry per node"); }
    var out = new Float64Array(3 * n);
    for (var k = 0; k < n; k++) {
      var e = given[k];
      if (!Array.isArray(e) || e.length !== 3) {
        throw new TypeError("OkfSim.create: boxes[" + k + "] must be three finite numbers >= 0");
      }
      for (var c = 0; c < 3; c++) {
        var v = e[c];
        if (typeof v !== "number" || !Number.isFinite(v) || v < 0) {
          throw new TypeError("OkfSim.create: boxes[" + k + "] must be three finite numbers >= 0");
        }
        out[3 * k + c] = Math.min(BOX_LIMIT, v);
      }
    }
    return out;
  }

  // graph: { nodeCount, edges: [[source, target], ...], boxes? }, nodes
  // numbered in index order (spec §12.5); boxes, when given, one
  // [halfWidth, above, below] per node. Returns null above NODE_LIMIT (A6: the
  // page then asks the reader to narrow the filters). Order of checks: graph
  // is an object, nodeCount an integer >= 0, edges an array, boxes absent or an
  // array, options valid (unknown keys included) -- each a TypeError; then
  // nodeCount > NODE_LIMIT gives null WITHOUT looking at the edge or box
  // entries; then every edge entry, then every box entry, is validated, each
  // field read once, before anything is allocated from edges.length.
  function create(graph, options) {
    if (!graph || typeof graph !== "object") { throw new TypeError("OkfSim.create: graph must be an object"); }
    var n = graph.nodeCount;
    if (!isInt(n) || n < 0) { throw new TypeError("OkfSim.create: nodeCount must be an integer >= 0"); }
    var list = graph.edges;
    if (!Array.isArray(list)) { throw new TypeError("OkfSim.create: edges must be an array"); }
    var givenBoxes = graph.boxes;
    if (givenBoxes !== undefined && !Array.isArray(givenBoxes)) { throw new TypeError("OkfSim.create: boxes must be an array"); }
    var opts = readOptions(options);
    if (n > NODE_LIMIT) { return null; }
    var count = list.length;
    var flat = [];
    for (var k = 0; k < count; k++) {
      var e = list[k];
      if (!Array.isArray(e) || e.length !== 2) {
        throw new TypeError("OkfSim.create: edges[" + k + "] must be two distinct node numbers below nodeCount");
      }
      var s = e[0];
      var t = e[1];
      if (!isInt(s) || !isInt(t) || s < 0 || s >= n || t < 0 || t >= n || s === t) {
        throw new TypeError("OkfSim.create: edges[" + k + "] must be two distinct node numbers below nodeCount");
      }
      flat.push(s, t);
    }
    var boxes = givenBoxes === undefined ? null : readBoxes(givenBoxes, n);
    return createSimulation(n, new Int32Array(flat), boxes, opts);
  }

  window.OkfSim = Object.freeze({
    NODE_LIMIT: NODE_LIMIT,
    create: create,
  });
})();
