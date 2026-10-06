// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Executes an okf-index.js written by okf-render and checks that it defines
// a usable site index. CI runs it on the file the NATIVE AOT binary writes:
// the jsdom harness (run.js) only exercises a site from a regular build.
"use strict";
const path = require("path");

const file = path.resolve(process.argv[2] || "");
global.window = {};
require(file);
const index = global.window.OKF_INDEX;
const ok = Boolean(index)
  && Array.isArray(index.concepts) && index.concepts.length > 0
  && Array.isArray(index.ghosts) && Array.isArray(index.edges)
  && Array.isArray(index.tree) && index.tree.length > 0
  && index.concepts.every((c) => typeof c.id === "string" && c.path === `${c.id}.html`);
if (!ok) {
  console.error(`${file}: not a usable site index`);
  process.exit(1);
}
console.log(`${file}: ${index.concepts.length} concepts, ${index.edges.length} edges`);
