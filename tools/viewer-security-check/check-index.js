// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Executes an okf-index.js written by okf-render and checks that it defines
// a well-formed site index (the shape IndexScript.cs writes). CI runs it on
// the file the NATIVE AOT binary writes: the jsdom harness (run.js) only
// exercises a site from a regular build.
//
// This is a smoke check on a NON-EMPTY bundle: it requires at least one
// concept and one tree node, so it is meant for a fixture that has them, not
// for an arbitrary (possibly empty) bundle.
"use strict";
const path = require("path");

const file = path.resolve(process.argv[2] || "");

function fail(why) {
  console.error(`${file}: not a usable site index (${why})`);
  process.exit(1);
}

const isStr = (v) => typeof v === "string";
const isInt = (v) => Number.isInteger(v);

global.window = {};
try {
  require(file);
} catch (e) {
  fail(`does not execute: ${e && e.message}`);
}

const index = global.window.OKF_INDEX;
if (!index || typeof index !== "object") fail("window.OKF_INDEX is not defined");
if (index.version !== 1) fail(`version is ${JSON.stringify(index.version)}, expected 1`);
for (const key of ["concepts", "ghosts", "edges", "tree"]) {
  if (!Array.isArray(index[key])) fail(`${key} is not an array`);
}
if (index.concepts.length === 0) fail("no concepts");
if (index.tree.length === 0) fail("empty tree");

const n = index.concepts.length;
const g = index.ghosts.length;

index.concepts.forEach((c, i) => {
  if (!c || typeof c !== "object") fail(`concepts[${i}] is not an object`);
  for (const k of ["id", "title", "type", "path", "trust"]) {
    if (!isStr(c[k])) fail(`concepts[${i}].${k} is not a string`);
  }
  if (!Array.isArray(c.tags) || !c.tags.every(isStr)) fail(`concepts[${i}].tags is not an array of strings`);
  if (c.path !== `${c.id}.html`) fail(`concepts[${i}].path is not id + ".html"`);
  // A deadline rounded up to a whole millisecond, and its date as written.
  if (c.staleAfterMs !== null && !isInt(c.staleAfterMs)) fail(`concepts[${i}].staleAfterMs is neither an integer nor null`);
  if (c.staleAfterDate !== null && !isStr(c.staleAfterDate)) fail(`concepts[${i}].staleAfterDate is neither a string nor null`);
});

index.ghosts.forEach((x, i) => {
  if (!x || !isStr(x.id)) fail(`ghosts[${i}].id is not a string`);
});

index.edges.forEach((e, i) => {
  if (!Array.isArray(e) || e.length !== 4) fail(`edges[${i}] is not a 4-element array`);
  const [from, to, count, toGhost] = e;
  if (!isInt(from) || from < 0 || from >= n) fail(`edges[${i}] from is out of range`);
  if (toGhost !== 0 && toGhost !== 1) fail(`edges[${i}] ghost flag is not 0 or 1`);
  if (!isInt(to) || to < 0 || to >= (toGhost === 1 ? g : n)) fail(`edges[${i}] to is out of range`);
  if (!isInt(count) || count <= 0) fail(`edges[${i}] count is not a positive integer`);
});

function checkNodes(nodes, where) {
  if (!Array.isArray(nodes)) fail(`${where} is not an array`);
  nodes.forEach((node, i) => {
    const at = `${where}[${i}]`;
    if (!node || typeof node !== "object") fail(`${at} is not an object`);
    if (!isStr(node.name)) fail(`${at}.name is not a string`);
    if (!isInt(node.concept) || node.concept < -1 || node.concept >= n) fail(`${at}.concept is out of range`);
    checkNodes(node.children, `${at}.children`);
  });
}
checkNodes(index.tree, "tree");

console.log(`${file}: ${index.concepts.length} concepts, ${index.edges.length} edges`);
