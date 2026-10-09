// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Reproducible screenshots for the viewer launch article
// (docs/outreach/articles/okf-viewer/article.md), run by hand, never by npm
// test or CI. A sibling of docs-shots.js: same frozen clock, same viewports,
// but clean captures (no numbered callouts), two still sequences composed in
// the browser (no image library), a "before" capture made with the okf-render
// of the v0.6.0 tag, and a synthetic bundle for what acme_retail is too small
// to show (a Neighbourhood at its 40-node cap, a graph of a hundred concepts).
//
//   OKF_PLAYWRIGHT=<path to a playwright-core module> \
//     node tools/viewer-security-check/recette/article-shots.js
//        [--out <dir>]          default: docs/outreach/articles/okf-viewer/img
//        [--browser chrome]     chrome | edge | firefox | webkit (lib.js names)
//        [--only a,b]           capture only these shots (names without .png)
//        [--before-site <dir>]  reuse a site rendered by okf-render v0.6.0
//        [--no-before]          skip the "before" capture (no v0.6.0 build)
//
// What it renders, all into a temporary folder that is deleted at the end:
//   - bundles/acme_retail, with the okf-render built from this tree;
//   - a synthetic bundle, written by synthBundle() below from a fixed seed:
//     112 concepts of 7 types, fictional names only, one hub concept
//     (glossary/customer) with 61 neighbours, two absent link targets;
//   - bundles/acme_retail again, with the okf-render of the v0.6.0 tag
//     (`git archive v0.6.0` into the temporary folder, then `dotnet run`),
//     for the one "before" capture. --no-before skips it.
// Playwright is NOT a dependency of this repository: lib.js resolves
// OKF_PLAYWRIGHT, else require("playwright-core"). Two runs give identical
// files (the layout frames come from a scheduler this script drives, never
// from requestAnimationFrame timing).
"use strict";
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");
const lib = require("./lib");

const REPO = path.resolve(__dirname, "..", "..", "..");
const ACME = path.join(REPO, "bundles", "acme_retail");
// Same instant as docs-shots.js: acme_retail's stale_after dates (2026-12)
// are still to come; the synthetic bundle has some deadlines before it, so
// the hourglass shows.
const FIXED_NOW = new Date("2026-10-09T12:00:00Z");
const WIDE = { width: 1440, height: 900 };
const NARROW = { width: 390, height: 844 };
const USAGE = "usage: article-shots.js [--out <dir>] [--browser chrome|edge|firefox|webkit] [--only name,name] [--before-site <dir>] [--no-before]";

function parseArgs(argv) {
  const out = { out: path.join(REPO, "docs", "outreach", "articles", "okf-viewer", "img"), browser: "chrome", only: null, beforeSite: null, before: true };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    const value = argv[i + 1];
    if (arg === "--no-before") { out.before = false; continue; }
    if (["--out", "--browser", "--only", "--before-site"].includes(arg)) {
      if (value === undefined || value.startsWith("--")) { throw new Error(`${arg} needs a value\n${USAGE}`); }
      i++;
      if (arg === "--only") { out.only = new Set(value.split(",")); }
      else if (arg === "--before-site") { out.beforeSite = path.resolve(value); }
      else { out[arg.slice(2)] = value; }
    } else {
      throw new Error(`unknown argument ${arg}\n${USAGE}`);
    }
  }
  out.out = path.resolve(out.out);
  return out;
}

function run(cmd, args, cwd) {
  const r = spawnSync(cmd, args, { cwd, stdio: ["ignore", "inherit", "inherit"] });
  if (r.status !== 0) { throw new Error(`${cmd} ${args.join(" ")} failed (status ${r.status}${r.error ? ", " + r.error.message : ""})`); }
}

function render(repo, bundle, dest) {
  run("dotnet", ["run", "-c", "Release", "--project", path.join("src", "OKF4net.Render"), "--", bundle, "--out", dest], repo);
}

// The v0.6.0 tree, extracted with git archive (no worktree, no checkout).
function extractTag(tag, dest) {
  fs.mkdirSync(dest, { recursive: true });
  const tar = path.join(dest, "..", `${tag}.tar`);
  run("git", ["archive", "--format=tar", "-o", tar, tag], REPO);
  // A relative name: GNU tar reads "C:\..." as a remote host.
  run("tar", ["-xf", path.join("..", path.basename(tar))], dest);
}

// ---------------------------------------------------------------------------
// The synthetic bundle. Everything comes from these tables and one seeded
// generator, so it is the same bundle on every run and every machine.

function lcg(seed) {
  let s = seed >>> 0;
  return () => { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s / 4294967296; };
}

const DOMAINS = [
  { dir: "glossary", type: "Glossary Term", names: ["customer", "order", "invoice", "shipment", "refund", "subscription", "churn", "cohort", "sku", "warehouse"] },
  { dir: "tables", type: "Table", names: ["customers", "customer_addresses", "orders", "order_lines", "invoices", "invoice_lines", "payments", "refunds", "shipments", "carriers", "skus", "sku_prices", "inventory_levels", "warehouses", "subscriptions", "subscription_events", "plans", "coupons", "support_tickets", "ticket_events", "web_sessions", "page_views", "campaigns", "campaign_touches", "suppliers", "purchase_orders", "returns", "exchange_rates", "fiscal_calendar", "regions"] },
  { dir: "metrics", type: "Metric", names: ["active-customers", "new-customers", "churn-rate", "net-revenue-retention", "gross-revenue", "net-revenue", "average-order-value", "orders-per-customer", "refund-rate", "on-time-delivery", "fill-rate", "stockout-rate", "customer-lifetime-value", "acquisition-cost", "payback-period", "conversion-rate", "repeat-purchase-rate", "ticket-resolution-time", "first-contact-resolution", "monthly-recurring-revenue", "annual-recurring-revenue", "trial-conversion", "basket-size", "return-rate"] },
  { dir: "runbooks", type: "Runbook", names: ["backfill-orders", "rebuild-customer-dim", "reprocess-refunds", "late-shipment-triage", "inventory-reconciliation", "month-end-close", "currency-rate-outage", "duplicate-customer-merge", "subscription-sync-failure", "payment-gateway-retry", "data-freshness-alert", "schema-change-rollout", "pii-deletion-request", "warehouse-cutover", "campaign-attribution-rerun", "ticket-backlog-surge", "price-change-rollout", "carrier-api-outage"] },
  { dir: "policies", type: "Policy", names: ["revenue-recognition", "refund-approval", "customer-data-retention", "pii-access", "discount-limits", "credit-terms", "inventory-write-off", "fraud-review", "subscription-cancellation", "price-change-notice", "supplier-onboarding", "returns-window", "data-quality-slo", "metric-change-control"] },
  { dir: "services", type: "Service", names: ["checkout", "billing", "fulfilment", "catalog", "identity", "notifications", "pricing", "returns-portal", "support-desk", "ingestion"] },
  { dir: "teams", type: "Team", names: ["finance-analytics", "growth", "operations", "platform-data", "customer-care", "merchandising"] },
];
const TAGS = ["finance", "customer", "orders", "logistics", "billing", "retention", "quality", "privacy", "pricing", "support", "inventory", "marketing"];
const HUB = "glossary/customer";

function titleOf(name) {
  const t = name.replace(/[-_]/g, " ");
  return t.charAt(0).toUpperCase() + t.slice(1);
}

// Returns the bundle as [relative path, file text] pairs.
function synthBundle() {
  const rnd = lcg(20261009);
  const pick = (arr) => arr[Math.floor(rnd() * arr.length)];
  const concepts = [];
  for (const d of DOMAINS) {
    d.names.forEach((name, i) => concepts.push({ id: `${d.dir}/${name}`, dir: d.dir, type: d.type, name, i }));
  }
  const ids = concepts.map((c) => c.id);
  const files = [];
  for (const c of concepts) {
    // Who links to the hub: every metric, every other table, every other
    // runbook and policy (55 in-links); the hub links to six terms.
    const links = new Set();
    if (c.id === HUB) {
      ["order", "invoice", "subscription", "churn", "cohort", "refund"].forEach((n) => links.add(`glossary/${n}`));
    } else {
      if (c.dir === "metrics" || (["tables", "runbooks", "policies"].includes(c.dir) && c.i % 2 === 0)) { links.add(HUB); }
      // One more link each: enough for a connected-looking graph, few
      // enough that the captures stay legible (and small).
      while (links.size < 1 + (links.has(HUB) ? 1 : 0)) {
        const other = pick(ids);
        if (other !== c.id && other !== HUB) { links.add(other); }
      }
    }
    // Two absent targets, drawn as ghosts.
    if (c.dir === "runbooks" && c.i < 3) { links.add("services/legacy-billing"); }
    if (c.dir === "tables" && (c.i === 3 || c.i === 7)) { links.add("tables/orders_v1"); }

    const tags = [pick(TAGS)];
    const second = pick(TAGS);
    if (second !== tags[0]) { tags.push(second); }
    const fm = [
      "---",
      `type: ${c.type}`,
      `title: ${titleOf(c.name)}`,
      `description: ${titleOf(c.name)} — a ${c.type.toLowerCase()} of the synthetic handbook, generated for screenshots.`,
      `tags: [${tags.join(", ")}]`,
    ];
    const trust = rnd();
    if (trust < 0.45) {
      fm.push("verified:", "  - { by: human:a.rivera, at: 2026-08-14T09:30:00Z }");
    } else if (trust < 0.7) {
      fm.push("verified:", "  - { by: agent:catalog-checker, at: 2026-09-02T06:00:00Z }");
    }
    const status = rnd();
    fm.push(`status: ${status < 0.75 ? "stable" : status < 0.92 ? "draft" : "deprecated"}`);
    // About one in six is past its deadline on FIXED_NOW.
    fm.push(`stale_after: ${rnd() < 0.17 ? "2026-09-15" : "2027-03-31"}`);
    fm.push("---");
    const linkLines = [...links].map((target) => `- [${titleOf(target.split("/").pop())}](/${target}.md)`);
    const body = [
      "",
      `${titleOf(c.name)} is part of a synthetic bundle that exists only to show the viewer at a size the sample bundles do not reach.`,
      "",
      "## Definition",
      "",
      `What the ${c.type.toLowerCase()} *${titleOf(c.name).toLowerCase()}* means, in one paragraph. The text is filler; the links below are what the graphs draw.`,
      "",
      "## Related",
      "",
      ...linkLines,
      "",
    ];
    files.push([`${c.id}.md`, fm.concat(body).join("\n")]);
  }
  return files;
}

function writeBundle(dir, files) {
  for (const [rel, text] of files) {
    const file = path.join(dir, ...rel.split("/"));
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text, "utf8");
  }
}

// ---------------------------------------------------------------------------
// Browser helpers.

async function settle(page) {
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(400);
}

async function graphSettled(page) {
  await page.waitForFunction(() => {
    const canvas = document.getElementById("okf-graph-canvas");
    return canvas && canvas.getAttribute("data-okf-layout") !== "running";
  }, null, { timeout: 60000 });
  await settle(page);
}

function facetRow(page, name) {
  return page.locator("#okf-facets label.okf-facet-row", { has: page.locator(".okf-facet-name", { hasText: name }) }).locator("input");
}

// okf-graph.js uses window.OKF_SCHEDULER, when it is a function, instead of
// requestAnimationFrame (spec §7; the jsdom harness relies on it). Here it
// queues the callbacks, and flush(n) runs n of them: one layout slice each,
// so a capture after n flushes shows the same frame on every run.
function installScheduler() {
  window.__okfQueue = [];
  window.OKF_SCHEDULER = (cb) => { window.__okfQueue.push(cb); };
  window.__okfFlush = (n) => {
    for (let i = 0; i < n && window.__okfQueue.length > 0; i++) { window.__okfQueue.shift()(); }
    return window.__okfQueue.length;
  };
}

// Lays out still frames side by side in a blank page and captures them as
// one PNG: the browser is the image compositor, so no image library is
// needed. frames: [{ png: Buffer, caption }].
async function compose(browser, frames, { width, file }) {
  const ctx = await browser.newContext({ viewport: { width, height: 400 }, deviceScaleFactor: 1 });
  try {
    const page = await ctx.newPage();
    const cells = frames.map((f, i) => `<figure><div class="n">${i + 1}</div><img src="data:image/png;base64,${f.png.toString("base64")}" alt=""><figcaption>${f.caption}</figcaption></figure>`).join("");
    await page.setContent(`<!doctype html><html><head><style>
      html,body{margin:0;background:#f4f4f6;font:500 15px/1.35 Inter,"Segoe UI",Arial,sans-serif;color:#101014}
      main{display:grid;grid-template-columns:repeat(${frames.length},1fr);gap:16px;padding:16px;width:${width}px;box-sizing:border-box}
      figure{margin:0;position:relative;background:#fff;border:1px solid #d6d6dc;border-radius:6px;overflow:hidden}
      img{display:block;width:100%;height:auto;border-bottom:1px solid #e4e4e8}
      figcaption{padding:8px 12px 10px}
      .n{position:absolute;top:8px;left:8px;width:26px;height:26px;border-radius:50%;background:#1d3fd8;color:#fff;font-weight:700;text-align:center;line-height:26px}
    </style></head><body><main>${cells}</main></body></html>`);
    await page.evaluate(() => Promise.all(Array.from(document.images).map((img) => img.decode())));
    await page.locator("main").screenshot({ path: file, type: "png" });
  } finally {
    await ctx.close();
  }
}

// ---------------------------------------------------------------------------

(async () => {
  const opts = parseArgs(process.argv.slice(2));
  const work = fs.mkdtempSync(path.join(os.tmpdir(), "okf-article-shots-"));
  const acmeSite = path.join(work, "acme-site");
  const synthDir = path.join(work, "synthetic_handbook");
  const synthSite = path.join(work, "synthetic-site");
  let beforeSite = opts.beforeSite;

  const need = (name) => !opts.only || opts.only.has(name);
  render(REPO, ACME, acmeSite);
  writeBundle(synthDir, synthBundle());
  render(REPO, synthDir, synthSite);
  if (opts.before && !beforeSite && need("before-static-site")) {
    const tree = path.join(work, "v0.6.0");
    extractTag("v0.6.0", tree);
    beforeSite = path.join(work, "acme-site-v060");
    render(tree, path.join(tree, "bundles", "acme_retail"), beforeSite);
  }

  const acme = (rel) => lib.pageUrl(lib.siteUrl(acmeSite), rel);
  const synth = (rel) => lib.pageUrl(lib.siteUrl(synthSite), rel);
  fs.mkdirSync(opts.out, { recursive: true });

  const pw = lib.loadPlaywright();
  const browser = await lib.launch(pw, opts.browser);
  const written = [];

  // One fresh context per capture: no stored theme or width, no leftover state.
  async function open({ viewport = WIDE, scheme = "light", scale = 1, scheduler = false }) {
    const ctx = await browser.newContext({ viewport, colorScheme: scheme, deviceScaleFactor: scale });
    await ctx.clock.setFixedTime(FIXED_NOW);
    if (scheduler) { await ctx.addInitScript(installScheduler); }
    const page = await ctx.newPage();
    const errors = [];
    page.on("pageerror", (e) => errors.push(String(e)));
    page.on("console", (m) => { if (m.type() === "error") { errors.push("console: " + m.text()); } });
    return { ctx, page, errors };
  }

  async function shot(name, { viewport = WIDE, scheme, scale, clip, run: body }) {
    if (!need(name)) { return; }
    const { ctx, page, errors } = await open({ viewport, scheme, scale });
    try {
      await body(page);
      if (errors.length > 0) { throw new Error(`${name}: the page raised ${errors.join(" | ")}`); }
      const file = path.join(opts.out, `${name}.png`);
      await page.screenshot({ path: file, type: "png", clip: clip || { x: 0, y: 0, width: viewport.width, height: viewport.height } });
      written.push(file);
    } finally {
      await ctx.close();
    }
  }

  try {
    // Before: the same concept page as rendered by okf-render 0.6.0.
    if (beforeSite) {
      await shot("before-static-site", {
        run: async (page) => {
          await page.goto(lib.pageUrl(lib.siteUrl(beforeSite), "computations/gross-margin-period.html"));
          await settle(page);
        },
      });
    }

    // After: the concept page, Neighbourhood at two hops.
    await shot("concept-page", {
      run: async (page) => {
        await page.goto(acme("computations/gross-margin-period.html"));
        await page.getByRole("button", { name: "2 hops" }).click();
        await settle(page);
      },
    });

    // The explorer of the synthetic bundle: two type chips pressed, a filter.
    await shot("explorer", {
      scale: 2,
      clip: { x: 0, y: 59, width: 289, height: 604 },
      run: async (page) => {
        await page.goto(synth("metrics/churn-rate.html"));
        await settle(page);
        await page.locator("#okf-explorer .okf-type-chips button", { hasText: "Metric" }).click();
        await page.locator("#okf-explorer .okf-type-chips button", { hasText: "Runbook" }).click();
        await page.locator("#okf-tree-filter").fill("re");
        await page.locator("#okf-tree-filter").blur();
        await settle(page);
      },
    });

    // The palette, as a sequence: opened with Ctrl+K, a query, the arrow keys.
    if (need("palette-sequence")) {
      const { ctx, page, errors } = await open({ scale: 1 });
      try {
        await page.goto(acme("policies/revenue-recognition.html"));
        await settle(page);
        const clip = { x: 391, y: 80, width: 658, height: 400 };
        const frames = [];
        await page.keyboard.press("Control+K");
        await settle(page);
        frames.push({ png: await page.screenshot({ clip }), caption: "<b>Ctrl+K</b> (or <b>/</b>) opens the palette on any page." });
        await page.keyboard.type("margin");
        await settle(page);
        frames.push({ png: await page.screenshot({ clip }), caption: "<b>margin</b>: titles and ids first, by fixed tiers." });
        await page.keyboard.type(" ");
        await page.keyboard.press("Backspace");
        await page.keyboard.press("ArrowDown");
        await page.keyboard.press("ArrowDown");
        await settle(page);
        frames.push({ png: await page.screenshot({ clip }), caption: "<b>↓ ↓</b> then <b>Enter</b> opens the concept." });
        if (errors.length > 0) { throw new Error(`palette-sequence: ${errors.join(" | ")}`); }
        const file = path.join(opts.out, "palette-sequence.png");
        await compose(browser, frames, { width: 1440, file });
        written.push(file);
      } finally {
        await ctx.close();
      }
    }

    // The palette alone, with a query, at twice the pixels.
    await shot("palette", {
      scale: 2,
      clip: { x: 401, y: 100, width: 638, height: 360 },
      run: async (page) => {
        await page.goto(synth("tables/orders.html"));
        await settle(page);
        await page.keyboard.press("Control+K");
        await page.keyboard.type("refund");
        await settle(page);
      },
    });

    // The hub of the synthetic bundle: the Neighbourhood at its 40-node cap
    // in the side panel (crowded: the honest limit), then enlarged.
    await shot("neighbourhood-cap", {
      scale: 2,
      clip: { x: 1106, y: 188, width: 334, height: 356 },
      run: async (page) => {
        await page.goto(synth("glossary/customer.html"));
        await settle(page);
      },
    });
    await shot("neighbourhood-enlarged", {
      clip: { x: 160, y: 80, width: 1120, height: 740 },
      run: async (page) => {
        await page.goto(synth("glossary/customer.html"));
        await settle(page);
        await page.getByRole("button", { name: "Enlarge the neighbourhood" }).click();
        await settle(page);
      },
    });

    // The global graph of acme_retail: a tag facet, unmatched concepts dimmed,
    // a concept selected from the address, its drawer open.
    await shot("graph-facets-drawer", {
      run: async (page) => {
        await page.goto(acme("graph.html") + "#metrics/gross-margin");
        await graphSettled(page);
        await page.locator("#okf-facets button.okf-chip", { hasText: "finance" }).first().click();
        await facetRow(page, /^Dim unmatched/).check();
        await settle(page);
      },
    });

    // The global graph of the synthetic bundle, nothing selected: the
    // drawing only, without the status line and the legend.
    await shot("graph-synthetic", {
      // Under the zoom tools (y 71-105), above the legend (y 850 on), where
      // Fit's margins keep the whole drawing.
      clip: { x: 285, y: 110, width: 800, height: 732 },
      run: async (page) => {
        await page.goto(synth("graph.html"));
        await graphSettled(page);
      },
    });

    // The layout as a sequence: the start, a middle frame, the end, each
    // after a counted number of layout slices.
    if (need("layout-sequence")) {
      const { ctx, page, errors } = await open({ scheduler: true });
      try {
        await page.goto(synth("graph.html"));
        await settle(page);
        // Without labels: the sequence is about the positions.
        await facetRow(page, /^Node labels/).uncheck();
        await settle(page);
        // The drawing without the status line and the legend under it.
        const clip = { x: 290, y: 110, width: 790, height: 720 };
        const frames = [];
        frames.push({ png: await page.screenshot({ clip }), caption: "The start: a grid in index order, jittered by a seeded integer generator. No <code>Math.random</code>." });
        await page.evaluate(() => window.__okfFlush(3));
        await settle(page);
        frames.push({ png: await page.screenshot({ clip }), caption: "Three slices of work later, still moving." });
        await page.evaluate(() => { let left = 1; let guard = 0; while (left > 0 && guard++ < 10000) { left = window.__okfFlush(1); } });
        await page.waitForFunction(() => document.getElementById("okf-graph-canvas").getAttribute("data-okf-layout") === "done");
        await settle(page);
        frames.push({ png: await page.screenshot({ clip }), caption: "Done and fitted: the same positions on every run, and in Chromium, Firefox and WebKit." });
        if (errors.length > 0) { throw new Error(`layout-sequence: ${errors.join(" | ")}`); }
        const file = path.join(opts.out, "layout-sequence.png");
        await compose(browser, frames, { width: 1140, file });
        written.push(file);
      } finally {
        await ctx.close();
      }
    }

    // The keyboard in the graph: one tab stop, then the arrows.
    if (need("keyboard-sequence")) {
      const { ctx, page, errors } = await open({});
      try {
        await page.goto(acme("graph.html"));
        await graphSettled(page);
        const clip = { x: 450, y: 250, width: 520, height: 470 };
        const frames = [];
        // The drawing's one tab stop is the node that carries tabindex 0.
        await page.locator('#okf-graph-canvas g[tabindex="0"]').focus();
        await page.keyboard.press("Home");
        await settle(page);
        frames.push({ png: await page.screenshot({ clip }), caption: "<b>Tab</b> into the drawing, <b>Home</b>: the first node in index order." });
        await page.keyboard.press("ArrowLeft");
        await settle(page);
        frames.push({ png: await page.screenshot({ clip }), caption: "<b>←</b>: the nearest neighbour to the left." });
        await page.keyboard.press("Space");
        await settle(page);
        frames.push({ png: await page.screenshot({ clip }), caption: "<b>Space</b> selects it; <b>Enter</b> would open its page." });
        if (errors.length > 0) { throw new Error(`keyboard-sequence: ${errors.join(" | ")}`); }
        const file = path.join(opts.out, "keyboard-sequence.png");
        await compose(browser, frames, { width: 1440, file });
        written.push(file);
      } finally {
        await ctx.close();
      }
    }

    // The list equivalent of the global graph, in place of the drawing.
    await shot("graph-list", {
      // Left of the splitter's grip; ends on a row boundary.
      clip: { x: 270, y: 59, width: 820, height: 734 },
      run: async (page) => {
        await page.goto(acme("graph.html") + "#metrics/gross-margin");
        await graphSettled(page);
        await page.getByRole("button", { name: "List" }).click();
        await settle(page);
      },
    });

    // The splitter on the graph page: dragged to widen the drawer (the
    // drawing fits itself to its narrower canvas), then one key press so its
    // focus ring shows.
    await shot("splitter", {
      run: async (page) => {
        await page.goto(acme("graph.html") + "#metrics/gross-margin");
        await graphSettled(page);
        const box = await page.locator(".okf-splitter").boundingBox();
        const x = box.x + box.width / 2;
        const y = box.y + 200;
        await page.mouse.move(x, y);
        await page.mouse.down();
        await page.mouse.move(x - 120, y, { steps: 10 });
        await page.mouse.move(x - 240, y, { steps: 10 });
        await page.mouse.up();
        await page.keyboard.press("ArrowLeft");
        await page.mouse.move(700, 600);
        await settle(page);
      },
    });

    // Dark: the global graph of acme_retail, following the system.
    await shot("graph-dark", {
      scheme: "dark",
      run: async (page) => {
        await page.goto(acme("graph.html") + "#policies/revenue-recognition");
        await graphSettled(page);
      },
    });

    // A phone: the concept page stacked.
    await shot("phone", {
      viewport: NARROW,
      scale: 2,
      run: async (page) => {
        await page.goto(acme("metrics/gross-margin.html"));
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
