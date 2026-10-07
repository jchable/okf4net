// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Tooled recette of the interactive viewer (spec §12.8), run by hand, never
// by npm test or CI. Opens the generated sites in file://, as a reader who
// double-clicks a file, in real browsers, runs each slice's checks and
// writes results.json plus captures named by §11 id.
//
//   node tools/viewer-security-check/recette/recette.js --site <okf4net-site> --acme <acme-site>
//        [--out <dir>] [--browsers chrome,edge,firefox,webkit] [--slices p1,p1.1,p2,p3] [--only id,id]
//
// The two sites are built by the commands of ../ACCEPTANCE.md. A slice whose
// file does not exist yet is reported as skipped.
"use strict";
const fs = require("fs");
const os = require("os");
const path = require("path");
const lib = require("./lib");

const SLICE_FILES = { p1: "p1.js", "p1.1": "p1-1.js", p2: "p2.js", p3: "p3.js" };

function parseArgs(argv) {
  const out = { browsers: ["chrome", "edge", "firefox"], slices: ["p1", "p1.1"], only: null };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    const value = argv[i + 1];
    if (arg === "--site") { out.site = value; i++; }
    else if (arg === "--acme") { out.acme = value; i++; }
    else if (arg === "--out") { out.out = value; i++; }
    else if (arg === "--browsers") { out.browsers = value.split(","); i++; }
    else if (arg === "--slices") { out.slices = value.split(","); i++; }
    else if (arg === "--only") { out.only = new Set(value.split(",")); i++; }
    else { throw new Error(`unknown argument ${arg}`); }
  }
  if (!out.site || !out.acme) {
    throw new Error("usage: recette.js --site <okf4net-site> --acme <acme-site> [--out <dir>] [--browsers chrome,edge,firefox,webkit] [--slices p1,p1.1,p2,p3] [--only id,id]");
  }
  out.out = path.resolve(out.out || path.join(os.tmpdir(), `okf-recette-${new Date().toISOString().replace(/[:.]/g, "-")}`));
  return out;
}

(async () => {
  const opts = parseArgs(process.argv.slice(2));
  const pw = lib.loadPlaywright();
  fs.mkdirSync(opts.out, { recursive: true });
  const report = { date: new Date().toISOString(), site: path.resolve(opts.site), acme: path.resolve(opts.acme), browsers: [] };
  let failed = 0;
  // pass: null = not applicable (a check that needs a later slice, or one
  // done by hand): printed "n/a" with its note, never counted as a failure.
  let notApplicable = 0;
  for (const name of opts.browsers) {
    const entry = { browser: name, slices: {} };
    let browser;
    try {
      browser = await lib.launch(pw, name);
      entry.version = browser.version();
    } catch (e) {
      entry.fatal = String(e).split("\n")[0];
      failed++;
      report.browsers.push(entry);
      console.log(`${name}: cannot launch (${entry.fatal})`);
      continue;
    }
    for (const slice of opts.slices) {
      const file = path.join(__dirname, SLICE_FILES[slice] || `${slice}.js`);
      if (!fs.existsSync(file)) {
        entry.slices[slice] = { skipped: `${path.basename(file)} does not exist yet` };
        console.log(`${name} ${slice}: skipped (${path.basename(file)} does not exist yet)`);
        continue;
      }
      const ctx = lib.context({ browser, name, opts, slice });
      const results = await lib.guard(() => require(file).run(ctx)).then((r) => (r && r.pass === false && r.error ? { crash: r } : r));
      await ctx.close();
      entry.slices[slice] = results;
      for (const [id, r] of Object.entries(results)) {
        const verdict = r.pass === true ? "ok" : r.pass === null ? "n/a" : "FAIL";
        if (verdict === "FAIL") { failed++; }
        if (verdict === "n/a") { notApplicable++; }
        const detail = r.error ? " -- " + r.error : verdict === "n/a" && r.note ? " -- " + r.note : "";
        console.log(`${name} ${entry.version} ${slice} ${id}: ${verdict}${detail}`);
      }
    }
    await browser.close();
    report.browsers.push(entry);
  }
  const file = path.join(opts.out, "results.json");
  report.notApplicable = notApplicable;
  fs.writeFileSync(file, JSON.stringify(report, null, 2));
  console.log(`\n${failed === 0 ? "no check failed" : `${failed} check(s) failed`}, ${notApplicable} not applicable; report: ${file}; captures: ${path.join(opts.out, "shots")}`);
  process.exit(failed === 0 ? 0 : 1);
})().catch((e) => {
  console.error(e && e.message ? e.message : e);
  process.exit(2);
});
