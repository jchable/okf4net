// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Reproducible screenshots of the interactive viewer for the public docs page
// (web/src/pages/docs/Viewer.tsx), run by hand, never by npm test or CI.
//
//   node tools/viewer-security-check/recette/docs-shots.js
//        [--out <dir>]        default: web/public/viewer
//        [--site <dir>]       reuse an already rendered site instead of rendering one
//        [--browser chrome]   chrome | edge | firefox | webkit (lib.js names)
//        [--only a,b]         capture only these shots (names without .png)
//
// What it does: renders bundles/acme_retail with the okf-render built from
// this tree (`dotnet run -c Release --project src/OKF4net.Render`), opens the
// pages in file:// at a fixed 1440 x 900 viewport (390 x 844 for the narrow
// one), the clock frozen at FIXED_NOW so the staleness badges do not drift
// with the day the script runs, draws numbered callouts over the page (the
// docs page lists them by number), and writes one PNG per shot. Playwright is
// NOT a dependency of this repository: lib.js resolves OKF_PLAYWRIGHT, else
// require("playwright-core").
//
//   OKF_PLAYWRIGHT=<path to a playwright-core module> node tools/viewer-security-check/recette/docs-shots.js
"use strict";
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");
const lib = require("./lib");

const REPO = path.resolve(__dirname, "..", "..", "..");
const BUNDLE = path.join(REPO, "bundles", "acme_retail");
// acme_retail's stale_after dates fall in 2026-12; any day before them gives
// the same badges. Freezing the clock makes the capture independent of "now".
const FIXED_NOW = new Date("2026-10-09T12:00:00Z");
const WIDE = { width: 1440, height: 900 };
const NARROW = { width: 390, height: 844 };
const USAGE = "usage: docs-shots.js [--out <dir>] [--site <dir>] [--browser chrome|edge|firefox|webkit] [--only name,name]";

function parseArgs(argv) {
  const out = { out: path.join(REPO, "web", "public", "viewer"), site: null, browser: "chrome", only: null };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    const value = argv[i + 1];
    if (["--out", "--site", "--browser", "--only"].includes(arg)) {
      if (value === undefined || value.startsWith("--")) { throw new Error(`${arg} needs a value\n${USAGE}`); }
      i++;
      if (arg === "--only") { out.only = new Set(value.split(",")); } else { out[arg.slice(2)] = value; }
    } else {
      throw new Error(`unknown argument ${arg}\n${USAGE}`);
    }
  }
  out.out = path.resolve(out.out);
  return out;
}

function render(dest) {
  const r = spawnSync("dotnet", ["run", "-c", "Release", "--project", path.join("src", "OKF4net.Render"), "--", BUNDLE, "--out", dest], { cwd: REPO, stdio: ["ignore", "inherit", "inherit"] });
  if (r.status !== 0) { throw new Error(`okf-render failed (status ${r.status}${r.error ? ", " + r.error.message : ""})`); }
}

// Numbered callouts, drawn into the page just before the capture (position:
// fixed, so they do not depend on the scroll). Each note is [number, selector,
// padding, corner]: it outlines the element found by the selector, kept inside
// the viewport, and puts its number on the outline's top-left corner ("tl", the
// default) or top-right corner ("tr", for a panel whose title starts at the
// left edge); "tl@N" sets the number N px lower, for a tall thin outline (the
// splitter) whose top corner another number already uses.
async function annotate(page, notes) {
  await page.evaluate((list) => {
    const colour = "#c2255c";
    const vw = document.documentElement.clientWidth;
    const vh = window.innerHeight;
    for (const [n, selector, pad, corner] of list) {
      const target = document.querySelector(selector);
      if (!target) { throw new Error(`annotation ${n}: no element matches ${selector}`); }
      const r = target.getBoundingClientRect();
      const p = pad === undefined ? 4 : pad;
      const left = Math.max(1, r.left - p);
      const top = Math.max(1, r.top - p);
      const right = Math.min(vw - 1, r.right + p);
      const bottom = Math.min(vh - 1, r.bottom + p);
      const box = document.createElement("div");
      box.setAttribute("data-docs-shot-note", String(n));
      box.style.cssText = `position:fixed;z-index:99999;pointer-events:none;box-sizing:border-box;border:2px solid ${colour};border-radius:4px;left:${left}px;top:${top}px;width:${right - left}px;height:${bottom - top}px`;
      const badge = document.createElement("div");
      badge.textContent = String(n);
      const at = /^(tl|tr)(?:@([0-9]+))?$/.exec(corner || "tl");
      const side = at[1] === "tr" ? "right:-2px" : "left:-2px";
      const shift = at[1] === "tr" ? "translate(45%,-45%)" : "translate(-45%,-45%)";
      badge.style.cssText = `position:absolute;${side};top:${at[2] ? Number(at[2]) : -2}px;transform:${shift};width:22px;height:22px;border-radius:50%;background:${colour};color:#fff;font:700 13px/22px Inter,Arial,sans-serif;text-align:center;box-shadow:0 0 0 2px #fff`;
      box.appendChild(badge);
      document.body.appendChild(box);
    }
  }, notes);
}

async function settle(page) {
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(400);
}

// The checkbox of the facet row whose name matches (the row is a label).
function facetRow(page, name) {
  return page.locator("#okf-facets label.okf-facet-row", { has: page.locator(".okf-facet-name", { hasText: name }) }).locator("input");
}

async function graphSettled(page) {
  await page.waitForFunction(() => {
    const canvas = document.getElementById("okf-graph-canvas");
    return canvas && canvas.getAttribute("data-okf-layout") !== "running";
  }, null, { timeout: 30000 });
  await settle(page);
}

(async () => {
  const opts = parseArgs(process.argv.slice(2));
  const work = fs.mkdtempSync(path.join(os.tmpdir(), "okf-docs-shots-"));
  const siteDir = opts.site ? path.resolve(opts.site) : path.join(work, "acme-site");
  if (!opts.site) { render(siteDir); }
  const base = lib.siteUrl(siteDir);
  const page_ = (rel) => lib.pageUrl(base, rel);
  fs.mkdirSync(opts.out, { recursive: true });

  const pw = lib.loadPlaywright();
  const browser = await lib.launch(pw, opts.browser);
  const written = [];

  // One fresh context per shot: no stored theme, no leftover state.
  async function shot(name, { viewport = WIDE, scheme = "light", scale = 1, clip, run }) {
    if (opts.only && !opts.only.has(name)) { return; }
    const ctx = await browser.newContext({ viewport, colorScheme: scheme, deviceScaleFactor: scale });
    try {
      await ctx.clock.setFixedTime(FIXED_NOW);
      const page = await ctx.newPage();
      const errors = [];
      page.on("pageerror", (e) => errors.push(String(e)));
      page.on("console", (m) => { if (m.type() === "error") { errors.push("console: " + m.text()); } });
      await run(page);
      if (errors.length > 0) { throw new Error(`${name}: the page raised ${errors.join(" | ")}`); }
      const file = path.join(opts.out, `${name}.png`);
      await page.screenshot({ path: file, type: "png", clip: clip || { x: 0, y: 0, width: viewport.width, height: viewport.height } });
      written.push(file);
    } finally {
      await ctx.close();
    }
  }

  try {
    // The concept page of the brief: right panel, Neighbourhood at two hops.
    await shot("viewer-concept", {
      run: async (page) => {
        await page.goto(page_("computations/gross-margin-period.html"));
        await page.getByRole("button", { name: "2 hops" }).click();
        await settle(page);
        await annotate(page, [
          [1, "#okf-tools", 3],
          [2, "#okf-explorer", 0, "tr"],
          [3, ".okf-page-head .okf-chips", 4],
          [4, "#okf-fm", 0],
          [5, "#okf-context .okf-local", 8],
          [6, "#okf-context .okf-backlinks", 8],
          [7, ".okf-splitter", 0, "tl@330"],
        ]);
      },
    });

    // The splitter, used for real: dragged with the mouse, then moved once with the
    // keyboard so its focus ring shows (a mouse press alone does not draw it).
    await shot("viewer-splitter", {
      run: async (page) => {
        await page.goto(page_("computations/gross-margin-period.html"));
        await settle(page);
        await page.getByRole("button", { name: "2 hops" }).click();
        const box = await page.locator(".okf-splitter").boundingBox();
        const x = box.x + box.width / 2;
        const y = box.y + 200;
        await page.mouse.move(x, y);
        await page.mouse.down();
        await page.mouse.move(x - 100, y, { steps: 10 });
        await page.mouse.move(x - 200, y, { steps: 10 });
        await page.mouse.up();
        await page.keyboard.press("ArrowLeft");
        await page.mouse.move(700, 600);
        await settle(page);
      },
    });

    // The Neighbourhood enlarged into its dialog (opener: "Enlarge the neighbourhood"),
    // at two hops, with numbered callouts. Cropped to the dialog and a margin of backdrop.
    await shot("viewer-modal", {
      clip: { x: 120, y: 90, width: 1200, height: 720 },
      run: async (page) => {
        await page.goto(page_("computations/gross-margin-period.html"));
        await settle(page);
        await page.getByRole("button", { name: "Enlarge the neighbourhood" }).click();
        await page.getByRole("button", { name: "2 hops" }).last().click();
        await settle(page);
        await annotate(page, [
          [1, ".okf-local-dialog .okf-hops", 4],
          [2, ".okf-local-modal-canvas", 0],
          [3, ".okf-local-modal-side", 0],
          [4, "#okf-local-modal-open", 4],
          [5, ".okf-local-modal-close", 4],
        ]);
      },
    });

    // The same dialog on a phone: the list goes under the drawing.
    await shot("viewer-modal-narrow", {
      viewport: NARROW,
      scale: 2,
      run: async (page) => {
        await page.goto(page_("computations/gross-margin-period.html"));
        await settle(page);
        await page.getByRole("button", { name: "Enlarge the neighbourhood" }).click();
        await page.getByRole("button", { name: "2 hops" }).last().click();
        await settle(page);
      },
    });

    // A page with headings: the "On this page" panel above the Neighbourhood.
    await shot("viewer-contents", {
      scale: 2,
      clip: { x: 1106, y: 59, width: 334, height: 600 },
      run: async (page) => {
        await page.goto(page_("policies/revenue-recognition.html"));
        await settle(page);
      },
    });

    // The "Jump to" palette, opened with Ctrl+K and a query.
    await shot("viewer-palette", {
      scale: 2,
      clip: { x: 401, y: 100, width: 638, height: 360 },
      run: async (page) => {
        await page.goto(page_("policies/revenue-recognition.html"));
        await settle(page);
        await page.keyboard.press("Control+K");
        await page.keyboard.type("margin");
        await settle(page);
      },
    });

    // The explorer, narrowed by a type chip and a name filter.
    await shot("viewer-explorer", {
      scale: 2,
      clip: { x: 0, y: 59, width: 289, height: 345 },
      run: async (page) => {
        await page.goto(page_("metrics/gross-margin.html"));
        await settle(page);
        await page.locator("#okf-explorer .okf-type-chips button", { hasText: "Metric" }).click();
        await page.locator("#okf-tree-filter").fill("margin");
        await page.locator("#okf-tree-filter").blur();
        await settle(page);
      },
    });

    // The global graph with a concept selected: opened from its fragment.
    await shot("viewer-graph", {
      run: async (page) => {
        await page.goto(page_("graph.html") + "#metrics/gross-margin");
        await graphSettled(page);
        await annotate(page, [
          [1, "#okf-facets", 0, "tr"],
          [2, "#okf-graph-canvas", 0, "tl@40"],
          [3, "#okf-graph-zoom", 4],
          [4, "#okf-graph-detail", -10],
          [5, ".okf-splitter", 0, "tl@330"],
        ]);
      },
    });

    // The facets alone: a type unticked, a tag chosen, unmatched nodes dimmed.
    await shot("viewer-graph-facets", {
      scale: 2,
      clip: { x: 0, y: 59, width: 269, height: 830 },
      run: async (page) => {
        await page.goto(page_("graph.html"));
        await graphSettled(page);
        await facetRow(page, /^Skill$/).uncheck();
        await page.locator("#okf-facets button.okf-chip", { hasText: "finance" }).first().click();
        await facetRow(page, /^Dim unmatched/).check();
        await settle(page);
      },
    });

    // The narrow layout: everything stacked, the viewport of a phone.
    await shot("viewer-narrow", {
      viewport: NARROW,
      scale: 2,
      run: async (page) => {
        await page.goto(page_("computations/gross-margin-period.html"));
        await settle(page);
      },
    });

    // The dark theme, which the viewer follows from the system by default.
    await shot("viewer-dark", {
      scheme: "dark",
      run: async (page) => {
        await page.goto(page_("metrics/gross-margin.html"));
        await settle(page);
      },
    });
  } finally {
    await browser.close();
    fs.rmSync(work, { recursive: true, force: true });
  }

  let total = 0;
  for (const file of written) {
    const size = fs.statSync(file).size;
    total += size;
    console.log(`${path.relative(REPO, file)}  ${(size / 1024).toFixed(0)} KB`);
  }
  console.log(`${written.length} shots, ${(total / 1024).toFixed(0)} KB in ${opts.out}`);
})().catch((e) => {
  console.error(e && e.stack ? e.stack : String(e));
  process.exit(1);
});
