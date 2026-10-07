// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Shared helpers of the tooled recette (spec §12.8): Playwright resolution,
// browsers, file:// URLs, pages by depth, colours and contrast, request and
// error tracking, captures. Playwright is NOT a dependency of this
// repository: it is resolved at run time (OKF_PLAYWRIGHT, else
// require("playwright-core")), and neither npm test nor CI ever runs this.
"use strict";
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const { pathToFileURL } = require("url");

function loadPlaywright() {
  const where = process.env.OKF_PLAYWRIGHT || "playwright-core";
  try {
    return require(where);
  } catch (e) {
    throw new Error(`Playwright not found (${where}). The recette needs playwright-core, which is deliberately not a dependency of this repository: set OKF_PLAYWRIGHT to the path of a playwright-core module (for instance the one an "npx playwright" run left in the npm cache), or install it outside the repository.`);
  }
}

const BROWSERS = {
  chrome: { type: "chromium", launch: { channel: "chrome" } },
  edge: { type: "chromium", launch: { channel: "msedge" } },
  firefox: { type: "firefox", launch: {} },
  webkit: { type: "webkit", launch: {} },
};

async function launch(pw, name) {
  const b = BROWSERS[name];
  if (!b) { throw new Error(`unknown browser ${name} (known: ${Object.keys(BROWSERS).join(", ")})`); }
  return pw[b.type].launch(b.launch);
}

function siteUrl(dir) {
  const url = pathToFileURL(path.resolve(dir)).href;
  return url.endsWith("/") ? url : url + "/";
}

// The OKF_INDEX a generated site's okf-index.js defines, executed in a sandbox.
function readIndex(dir) {
  const sandbox = { window: {} };
  vm.runInNewContext(fs.readFileSync(path.join(dir, "assets", "okf-index.js"), "utf8"), sandbox);
  return sandbox.window.OKF_INDEX;
}

// Spec §11.0 (A26): index.html for depth 0, then, for each depth present, the
// first page of that depth in index order. Depth = number of "/" in the path.
function pagesByDepth(dir) {
  const out = [{ rel: "index.html", depth: 0 }];
  const seen = new Set([0]);
  for (const concept of readIndex(dir).concepts) {
    const depth = concept.path.split("/").length - 1;
    if (!seen.has(depth)) {
      seen.add(depth);
      out.push({ rel: concept.path, depth });
    }
  }
  return out.sort((a, b) => a.depth - b.depth);
}

function parseColor(css) {
  const m = String(css).match(/rgba?\(([^)]+)\)/);
  if (!m) { return null; }
  const p = m[1].split(/[\s,/]+/).filter(Boolean).map(Number);
  return { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 };
}

function hex(h) {
  const n = parseInt(h.slice(1), 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255, a: 1 };
}

// The computed-style form of a #rrggbb token: "rgb(r, g, b)".
function rgb(h) {
  const c = hex(h);
  return `rgb(${c.r}, ${c.g}, ${c.b})`;
}

function luminance(c) {
  const f = (v) => {
    const s = v / 255;
    return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
  };
  return 0.2126 * f(c.r) + 0.7152 * f(c.g) + 0.0722 * f(c.b);
}

// WCAG contrast ratio of two colours given as "#rrggbb" or "rgb(…)".
function contrast(a, b) {
  const ca = String(a).startsWith("#") ? hex(a) : parseColor(a);
  const cb = String(b).startsWith("#") ? hex(b) : parseColor(b);
  const [x, y] = [luminance(ca), luminance(cb)].sort((p, q) => q - p);
  return Math.round(((x + 0.05) / (y + 0.05)) * 100) / 100;
}

function track(page, roots) {
  const tracked = { errors: [], outside: [], failed: [] };
  page.on("pageerror", (e) => tracked.errors.push(String(e)));
  page.on("console", (m) => { if (m.type() === "error") { tracked.errors.push("console: " + m.text()); } });
  page.on("request", (r) => {
    const url = r.url();
    if (!url.startsWith("data:") && !url.startsWith("about:") && !roots.some((root) => url.startsWith(root))) { tracked.outside.push(url); }
  });
  page.on("requestfailed", (r) => tracked.failed.push(r.url()));
  return tracked;
}

function context({ browser, name, opts, slice }) {
  const site = siteUrl(opts.site);
  const acme = siteUrl(opts.acme);
  const opened = [];
  const shots = path.join(opts.out, "shots", name, slice);
  fs.mkdirSync(shots, { recursive: true });
  return {
    browserName: name,
    site,
    acme,
    siteDir: path.resolve(opts.site),
    acmeDir: path.resolve(opts.acme),
    lib: module.exports,
    wanted: (id) => !opts.only || opts.only.has(id),
    async newPage(o = {}) {
      const ctx = await browser.newContext({ viewport: o.viewport || { width: 1440, height: 900 }, colorScheme: o.colorScheme || "light" });
      const page = await ctx.newPage();
      page.okfTracked = track(page, [site, acme]);
      opened.push(ctx);
      return page;
    },
    async shot(page, id) {
      await page.screenshot({ path: path.join(shots, `${id}.png`) });
    },
    async close() {
      for (const ctx of opened) { await ctx.close(); }
    },
  };
}

// Runs one control, turning a crash into a failed result.
async function guard(fn) {
  try {
    return await fn();
  } catch (e) {
    return { pass: false, error: String(e && e.stack ? e.stack : e).split("\n").slice(0, 2).join(" | ") };
  }
}

module.exports = { loadPlaywright, launch, siteUrl, readIndex, pagesByDepth, parseColor, rgb, contrast, context, guard, BROWSERS };
