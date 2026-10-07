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
  const given = process.env.OKF_PLAYWRIGHT;
  // A value with a separator is a path: resolve it against the working
  // directory (require() would resolve it against this file's directory).
  const where = given ? (/[\\/]/.test(given) ? path.resolve(given) : given) : "playwright-core";
  try {
    return require(where);
  } catch (e) {
    throw new Error(`Playwright not found (${where}). The recette needs playwright-core, which is deliberately not a dependency of this repository: set OKF_PLAYWRIGHT to the path of a playwright-core module (for instance the one an "npx playwright" run left in the npm cache), or install it outside the repository. Cause: ${e.message.split("\n")[0]}`);
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

// A page url: the site url + a "/"-separated relative path, each segment
// percent-encoded ("#", "?" and "%" in a file name must not be read as a
// fragment, a query or an escape), the "/" kept.
function pageUrl(base, rel) {
  return base + rel.split("/").map(encodeURIComponent).join("/");
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

// Colours. Only what a computed style or a design token can hold is accepted,
// everything else throws an Error naming the input: a helper that guessed
// (color(srgb ...), oklch(), transparent, 4- and 8-digit hex) would hand a
// confident wrong contrast ratio to a check.
function fail(input, why) {
  throw new Error(`unsupported colour ${JSON.stringify(input)}: ${why}`);
}

function hex(h) {
  const m = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(String(h));
  if (!m) { fail(h, "expected #rgb or #rrggbb (4- and 8-digit hex carry alpha: write rgba())"); }
  const digits = m[1].length === 3 ? m[1].replace(/./g, (c) => c + c) : m[1];
  const n = parseInt(digits, 16);
  return { r: (n >> 16) & 255, g: (n >> 8) & 255, b: n & 255, a: 1 };
}

// "rgb(r, g, b)", "rgba(r, g, b, a)", "rgb(r g b)", "rgb(r g b / a)", a as a
// number or a percentage. Channels are numbers (what getComputedStyle gives).
function parseColor(css) {
  const m = /^rgba?\(\s*([^)]*?)\s*\)$/i.exec(String(css).trim());
  if (!m) { fail(css, "expected #rgb, #rrggbb, rgb() or rgba()"); }
  const body = m[1];
  const num = "-?\\d*\\.?\\d+";
  const alpha = `(?:${num}%?)`;
  const comma = new RegExp(`^(${num})\\s*,\\s*(${num})\\s*,\\s*(${num})(?:\\s*,\\s*(${alpha}))?$`).exec(body);
  const space = new RegExp(`^(${num})\\s+(${num})\\s+(${num})(?:\\s*/\\s*(${alpha}))?$`).exec(body);
  const parts = comma || space;
  if (!parts) { fail(css, "channels must be plain numbers (comma or space/slash syntax)"); }
  const channels = parts.slice(1, 4).map(Number);
  if (channels.some((c) => c < 0 || c > 255)) { fail(css, "a channel is outside 0-255"); }
  let a = 1;
  if (parts[4] !== undefined) {
    a = parts[4].endsWith("%") ? parseFloat(parts[4]) / 100 : Number(parts[4]);
    if (!(a >= 0 && a <= 1)) { fail(css, "alpha is outside 0-1"); }
  }
  return { r: channels[0], g: channels[1], b: channels[2], a };
}

function colour(x) {
  return String(x).trim().startsWith("#") ? hex(String(x).trim()) : parseColor(x);
}

// The computed-style form of a #rgb / #rrggbb token: "rgb(r, g, b)".
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

// WCAG contrast ratio of a foreground over a background, each "#rgb",
// "#rrggbb", "rgb(...)" or "rgba(...)". A translucent foreground is composited
// over the background first (what the eye sees); a translucent BACKGROUND has
// no defined ratio without what is behind it, so it throws.
function contrast(fg, bg) {
  const f = colour(fg);
  const b = colour(bg);
  if (b.a < 1) { throw new Error(`contrast: the background ${JSON.stringify(bg)} is translucent, composite it over what is behind it first`); }
  const over = { r: f.r * f.a + b.r * (1 - f.a), g: f.g * f.a + b.g * (1 - f.a), b: f.b * f.a + b.b * (1 - f.a) };
  const [x, y] = [luminance(over), luminance(b)].sort((p, q) => q - p);
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

// The first page, in index order, whose markdown body holds at least `n`
// "## " or "### " headings: read from the payload of the generated HTML,
// where a newline is the two characters \n (HtmlSafeJson).
function pageWithSections(dir, n) {
  for (const concept of readIndex(dir).concepts) {
    const html = fs.readFileSync(path.join(dir, ...concept.path.split("/")), "utf8");
    if ((html.match(/\\n#{2,3} /g) || []).length >= n) { return concept.path; }
  }
  return null;
}

module.exports = { loadPlaywright, launch, siteUrl, pageUrl, readIndex, pagesByDepth, pageWithSections, parseColor, rgb, contrast, context, guard, BROWSERS };

// node recette/lib.js --selftest: known answers for the pure helpers (no
// Playwright, no browser). A colour or url helper that is wrong gives every
// later recette check a confident wrong number, so these run before trusting it.
if (require.main === module) {
  if (!process.argv.includes("--selftest")) {
    console.log("usage: node recette/lib.js --selftest");
    process.exit(2);
  }
  const assert = require("assert");
  const throws = (fn, re) => assert.throws(fn, (e) => e instanceof Error && re.test(e.message), String(fn));
  let n = 0;
  const t = (name, fn) => { fn(); n++; console.log("  ok  - " + name); };
  t("contrast #000 on #fff is 21 and #fff on #000 too", () => { assert.strictEqual(contrast("#000000", "#ffffff"), 21); assert.strictEqual(contrast("#ffffff", "#000000"), 21); });
  t("3-digit hex is expanded: #fff on #000 is 21, #777 equals #777777", () => { assert.strictEqual(contrast("#fff", "#000"), 21); assert.strictEqual(contrast("#777", "#fff"), contrast("#777777", "#fff")); });
  t("#767676 on white is 4.54 and #777777 is 4.48 (the AA threshold pair)", () => { assert.strictEqual(contrast("#767676", "#ffffff"), 4.54); assert.strictEqual(contrast("#777777", "#ffffff"), 4.48); });
  t("rgb() in computed, space and percent-alpha syntax gives the same colours", () => {
    assert.deepStrictEqual(parseColor("rgb(0, 0, 0)"), { r: 0, g: 0, b: 0, a: 1 });
    assert.deepStrictEqual(parseColor("rgb(0 0 0 / 50%)"), { r: 0, g: 0, b: 0, a: 0.5 });
    assert.deepStrictEqual(parseColor("rgba(10, 20, 30, 0.25)"), { r: 10, g: 20, b: 30, a: 0.25 });
    assert.deepStrictEqual(parseColor("rgb(10 20 30 / 0.25)"), { r: 10, g: 20, b: 30, a: 0.25 });
  });
  t("a translucent foreground is composited over the background (rgba(0,0,0,.5) on #fff = 3.98, not 21)", () => {
    assert.strictEqual(contrast("rgba(0, 0, 0, 0.5)", "#ffffff"), 3.98);
    assert.strictEqual(contrast("rgb(0 0 0 / 50%)", "rgb(255, 255, 255)"), 3.98);
  });
  t("a translucent background throws", () => throws(() => contrast("#000000", "rgba(255, 255, 255, 0.5)"), /translucent/));
  t("rgb(r, g, b) of a token", () => { assert.strictEqual(rgb("#1a2b3c"), "rgb(26, 43, 60)"); assert.strictEqual(rgb("#fff"), "rgb(255, 255, 255)"); });
  t("unsupported colours throw an Error naming the input", () => {
    for (const bad of ["transparent", "oklch(0.5 0.1 200)", "color(srgb 1 1 1)", "#ffff", "#ffffff80", "#ggg", "rgb(300, 0, 0)", "rgb(0, 0)", "hsl(0 0% 0%)", ""]) {
      assert.throws(() => contrast(bad, "#ffffff"), (e) => e instanceof Error && e.message.startsWith("unsupported colour " + JSON.stringify(bad)), bad);
    }
  });
  t("pageUrl encodes each segment and keeps the slashes", () => {
    assert.strictEqual(pageUrl("file:///s/", "a#b/c?d/e%f g.html"), "file:///s/a%23b/c%3Fd/e%25f%20g.html");
    assert.strictEqual(pageUrl("file:///s/", "index.html"), "file:///s/index.html");
  });
  console.log(`${n} passed`);
}
