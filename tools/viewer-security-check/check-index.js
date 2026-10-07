// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Executes an okf-index.js written by okf-render and checks that it defines
// a well-formed site index, schema version 2 (the shape IndexScript.cs
// writes, spec §12.1). CI runs it on the file the NATIVE AOT binary writes:
// the jsdom harness (run.js) only exercises a site from a regular build.
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
// The longest description, in code points (spec §12.1: 200, or 120 under A23).
const DESCRIPTION_LIMIT = 200;

global.window = {};
try {
  require(file);
} catch (e) {
  fail(`does not execute: ${e && e.message}`);
}

const index = global.window.OKF_INDEX;
if (!index || typeof index !== "object") fail("window.OKF_INDEX is not defined");
if (index.version !== 2) fail(`version is ${JSON.stringify(index.version)}, expected 2`);
for (const key of ["concepts", "ghosts", "edges", "tree", "types"]) {
  if (!Array.isArray(index[key])) fail(`${key} is not an array`);
}
if (index.concepts.length === 0) fail("no concepts");
if (index.tree.length === 0) fail("empty tree");

const n = index.concepts.length;
const g = index.ghosts.length;
const t = index.types.length;

let rank = 0;
let total = 0;
index.types.forEach((type, i) => {
  if (!type || typeof type !== "object") fail(`types[${i}] is not an object`);
  if (!isStr(type.name)) fail(`types[${i}].name is not a string`);
  if (!isInt(type.count) || type.count <= 0) fail(`types[${i}].count is not a positive integer`);
  if (i > 0) {
    const prev = index.types[i - 1];
    if (prev.count < type.count || (prev.count === type.count && !(prev.name < type.name))) fail(`types[${i}] is out of order (count descending, then ordinal name)`);
  }
  // Ranks 0 to 4 go to the first five non-empty names, once each; 5 to the rest.
  const expected = type.name !== "" && rank < 5 ? rank++ : 5;
  if (type.slot !== expected) fail(`types[${i}].slot is ${type.slot}, expected ${expected}`);
  total += type.count;
});
if (total !== n) fail(`the type counts add up to ${total}, not to the ${n} concepts`);

// Each type's count is the number of concepts that point at it, not merely a
// share of the total.
const tally = new Array(t).fill(0);
index.concepts.forEach((c) => {
  if (isInt(c.typeIndex) && c.typeIndex >= 0 && c.typeIndex < t) tally[c.typeIndex]++;
});
index.types.forEach((type, i) => {
  if (type.count !== tally[i]) fail(`types[${i}].count is ${type.count}, but ${tally[i]} concepts point at it`);
});

index.concepts.forEach((c, i) => {
  if (!c || typeof c !== "object") fail(`concepts[${i}] is not an object`);
  for (const k of ["id", "title", "type", "path", "trust", "description"]) {
    if (!isStr(c[k])) fail(`concepts[${i}].${k} is not a string`);
  }
  if (!Array.isArray(c.tags) || !c.tags.every(isStr)) fail(`concepts[${i}].tags is not an array of strings`);
  if (c.path !== `${c.id}.html`) fail(`concepts[${i}].path is not id + ".html"`);
  // A deadline rounded up to a whole millisecond, and its date as written.
  if (c.staleAfterMs !== null && !isInt(c.staleAfterMs)) fail(`concepts[${i}].staleAfterMs is neither an integer nor null`);
  if (c.staleAfterDate !== null && !isStr(c.staleAfterDate)) fail(`concepts[${i}].staleAfterDate is neither a string nor null`);
  if (!isInt(c.typeIndex) || c.typeIndex < 0 || c.typeIndex >= t) fail(`concepts[${i}].typeIndex is out of range`);
  if (index.types[c.typeIndex].name !== c.type) fail(`concepts[${i}].typeIndex does not name its type`);
  if (Array.from(c.description).length > DESCRIPTION_LIMIT) fail(`concepts[${i}].description is longer than ${DESCRIPTION_LIMIT} code points`);
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

console.log(`${file}: ${index.concepts.length} concepts, ${index.edges.length} edges, ${index.types.length} types`);
