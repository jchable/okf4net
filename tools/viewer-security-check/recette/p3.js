// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Recette of slice P3 (global graph): spec §11.5 G1-G19, H9 and H10 on
// graph.html, the controls C13 (history under file://) and C14 (requests), and
// ACCEPTANCE.md "## P3". Run by recette.js (--slices p3), in file://, never by
// npm test or CI (spec §12.8). It measures what jsdom cannot: layout, sizes,
// computed fonts and colours, contrasts in both themes, real pointer and
// keyboard behaviour (:focus-visible, Tab order, the focus reveal), history,
// requests, 390 px.
//
// ctx is P1.1's lib.context (spec §12.8): pages come from ctx.newPage()
// (1 440 x 900 and light unless told otherwise; okfTracked lists script
// errors, requests outside both sites and failed requests), captures through
// ctx.shot(page, id), colours and contrasts through ctx.lib. A result
// { pass: null, note } means "not applicable": the driver prints it n/a and
// never counts it as a failure.
//
// Two optional sites, rendered by the same okf-render from bundles the two
// official sites do not cover (neither acme_retail nor the OKF4net bundle has
// a broken link, and the OKF4net bundle is under the 1 500 node limit):
//   OKF_RECETTE_GHOST  a site with broken links (the harness's hostile-bundle)
//   OKF_RECETTE_BIG    a site of more than 1 500 concepts, two types or more
// Without them the checks that need them report n/a.
"use strict";
const fs = require("fs");
const path = require("path");

const ELLIPSIS = String.fromCharCode(0x2026);
const MIDDOT = String.fromCharCode(0xb7);
const EMDASH = String.fromCharCode(0x2014);
const near = (a, b, tolerance = 0.6) => Math.abs(a - b) <= tolerance;
// Light tokens of §11.0 the probes compare with (H9 is a fixed drawing).
const T = { white: "#ffffff", blue: "#1a3fd6", hair: "#e3e3e8" };

// A point inside node i that hits it (not the drawing behind it): the middle
// of its shape, else its label, else the shape's edge, for a hollow ring.
const pointOn = (page, i) => page.evaluate((n) => {
  const g = document.querySelectorAll("#okf-graph-canvas g.okf-node")[n];
  if (!g) { return null; }
  const rects = [g.querySelector('[class^="okf-shape-"], .okf-ghost-mark'), g.querySelector("text")].map((e) => e.getBoundingClientRect());
  for (const r of rects) {
    for (const [dx, dy] of [[0, 0], [-0.25, 0], [0.25, 0], [0, -0.25], [0, 0.25], [-0.45, 0], [0.45, 0], [0, -0.45], [0, 0.45]]) {
      const x = r.x + r.width * (0.5 + dx);
      const y = r.y + r.height * (0.5 + dy);
      const hit = document.elementFromPoint(x, y);
      if (hit && g.contains(hit)) { return { x, y }; }
    }
  }
  return null;
}, i);

const clickNode = async (page, i, options) => {
  const p = await pointOn(page, i);
  if (!p) { throw new Error(`node ${i} has no point that hits it`); }
  await page.mouse.click(p.x, p.y, options);
};

// The drawing's view: the viewport group's transform.
const viewOf = (page) => page.evaluate(() => {
  const m = /translate\((-?[0-9.]+) (-?[0-9.]+)\) scale\(([0-9.]+)\)/.exec(
    document.querySelector("#okf-graph-canvas g.okf-graph-viewport").getAttribute("transform"));
  return { a: Number(m[1]), b: Number(m[2]), s: Number(m[3]) };
});

async function run(ctx) {
  const { contrast, rgb } = ctx.lib;
  const results = {};
  // Every page this recette opens, so C14 and P3-errors can read all of them.
  const pages = [];
  const newPage = async (options) => {
    const p = await ctx.newPage(options);
    pages.push(p);
    return p;
  };
  const page = await newPage();
  const check = async (id, fn) => {
    if (!ctx.wanted(id)) { return; }
    try { results[id] = await fn(); } catch (e) { results[id] = { pass: false, error: String(e).split("\n")[0] }; }
  };
  const shot = (id, p = page) => ctx.shot(p, id);

  const extra = (variable) => (process.env[variable] ? { base: ctx.lib.siteUrl(process.env[variable]), dir: process.env[variable] } : null);
  const ghostSite = extra("OKF_RECETTE_GHOST");
  const bigSite = extra("OKF_RECETTE_BIG");

  // The graph page, through the Global graph link (A25: never a hard-coded name).
  // Read from the file, not from a page: the sites' own index pages (the
  // hostile fixture's has a request that fails) are not under test here.
  const graphUrl = async (base, dir) => {
    const html = fs.readFileSync(path.join(dir, "index.html"), "utf8");
    const m = /id="okf-global-graph"[^>]*href="([^"]*)"/.exec(html) || /href="([^"]*)"[^>]*id="okf-global-graph"/.exec(html);
    if (!m) { throw new Error("index.html has no Global graph link"); }
    return base + m[1].split("#")[0];
  };
  const waitLayout = (p = page) => p.waitForFunction(() => {
    const c = document.getElementById("okf-graph-canvas");
    return c && (c.getAttribute("data-okf-layout") === "done" || c.querySelector("svg") === null);
  }, null, { timeout: 60000 });
  const openGraph = async (url, p = page) => {
    await p.goto(url);
    await waitLayout(p);
  };

  const acmeGraph = await graphUrl(ctx.acme, ctx.acmeDir);
  const siteGraph = await graphUrl(ctx.site, ctx.siteDir);
  const ghostGraph = ghostSite ? await graphUrl(ghostSite.base, ghostSite.dir) : null;
  const bigGraph = bigSite ? await graphUrl(bigSite.base, bigSite.dir) : null;
  const acmeIndex = ctx.lib.readIndex(ctx.acmeDir);

  await check("P3-layout-time", async () => {
    // Informative: calibrates NODE_LIMIT and the slice budget (§4.2). A frame
    // loop records the longest gap between two animation frames: a layout
    // that freezes the page shows there.
    const probe = await newPage();
    await probe.addInitScript(() => {
      window.okfFrames = { last: performance.now(), max: 0 };
      const tick = (t) => { window.okfFrames.max = Math.max(window.okfFrames.max, t - window.okfFrames.last); window.okfFrames.last = t; requestAnimationFrame(tick); };
      requestAnimationFrame(tick);
    });
    const t0 = Date.now();
    await probe.goto(siteGraph);
    await waitLayout(probe);
    const ms = Date.now() - t0;
    const nodes = await probe.locator("#okf-graph-canvas g.okf-node").count();
    const maxFrameGapMs = Math.round(await probe.evaluate(() => window.okfFrames.max));
    await shot("graph-okf4net", probe);
    return { pass: ms < 15000 && maxFrameGapMs < 1000 && nodes > 0, ms, nodes, maxFrameGapMs };
  });

  await openGraph(acmeGraph);
  await shot("graph-light");

  await check("H9", async () => {
    const r = await page.evaluate(() => {
      const a = document.getElementById("okf-global-graph");
      const cs = getComputedStyle(a);
      return { current: a.getAttribute("aria-current"), bg: cs.backgroundColor, fg: cs.color, href: a.getAttribute("href"),
        height: a.getBoundingClientRect().height, size: cs.fontSize, weight: cs.fontWeight };
    });
    return { pass: r.current === "page" && !r.href.includes("#") && r.bg === rgb(T.blue) && r.fg === rgb(T.white)
      && near(r.height, 34) && r.size === "13.5px" && r.weight === "600" && contrast(r.fg, r.bg) >= 4.5, ...r };
  });

  await check("H10", async () => {
    const r = await page.evaluate(() => {
      const a = document.getElementById("okf-reading-view");
      const cs = getComputedStyle(a);
      const tools = Array.from(document.getElementById("okf-tools").children, (e) => e.id);
      return { tag: a.tagName, bg: cs.backgroundColor, fg: cs.color, border: cs.borderTopColor, size: cs.fontSize, weight: cs.fontWeight,
        height: a.getBoundingClientRect().height, href: a.getAttribute("href"), tools };
    });
    return { pass: r.tag === "A" && r.bg === rgb(T.white) && r.border === rgb(T.hair) && r.size === "13.5px" && r.weight === "500"
      && near(r.height, 34) && r.tools.indexOf("okf-reading-view") === r.tools.indexOf("okf-global-graph") - 1, ...r };
  });

  await check("G2", async () => {
    const r = await page.evaluate(() => {
      const f = document.getElementById("okf-facets");
      const cs = getComputedStyle(f);
      const body = document.querySelector("#okf-facets .okf-facets-body");
      const sections = Array.from(body.children).map((s) => s.getBoundingClientRect());
      return {
        width: f.getBoundingClientRect().width, border: cs.borderRightWidth, borderColor: cs.borderRightColor, padding: cs.padding,
        overflowY: cs.overflowY, gap: getComputedStyle(body).rowGap,
        summary: getComputedStyle(f.querySelector("summary")).display, open: f.querySelector("details").open,
        title: getComputedStyle(f.querySelector(".okf-section-title")).marginBottom,
      };
    });
    return { pass: near(r.width, 270) && parseFloat(r.border) > 0 && r.borderColor === rgb(T.hair) && r.padding === "18px 18px 0px"
      && r.overflowY === "auto" && r.gap === "20px" && r.summary === "none" && r.open && r.title === "10px", ...r };
  });

  await check("G3", async () => {
    const r = await page.evaluate(() => {
      const rows = Array.from(document.querySelectorAll("#okf-facet-type ~ .okf-facet-list .okf-facet-row"));
      const row = rows[0];
      const box = row.querySelector("input").getBoundingClientRect();
      const count = getComputedStyle(row.querySelector(".okf-facet-count"));
      const listGap = getComputedStyle(row.parentElement).rowGap;
      return {
        rows: rows.length, types: window.OKF_INDEX.types.length, height: row.getBoundingClientRect().height,
        gap: getComputedStyle(row).columnGap, listGap, size: getComputedStyle(row).fontSize, box: [box.width, box.height],
        accent: getComputedStyle(row.querySelector("input")).accentColor,
        countFont: count.fontFamily, countSize: count.fontSize, countColor: count.color,
        glyphs: rows.every((x) => x.querySelector("svg.okf-glyph")),
        names: rows.map((x) => x.querySelector(".okf-facet-name").textContent),
        counts: rows.map((x) => x.querySelector(".okf-facet-count").textContent),
        allChecked: rows.every((x) => x.querySelector("input").checked),
      };
    });
    const names = acmeIndex.types.map((t) => t.name === "" ? "(no type)" : t.name);
    return { pass: r.rows === r.types && near(r.height, 32) && r.gap === "10px" && r.listGap === "2px" && r.size === "13.5px"
      && near(r.box[0], 16) && near(r.box[1], 16) && r.accent === rgb(T.blue) && /Space Mono/.test(r.countFont) && r.countSize === "11px"
      && r.glyphs && r.allChecked && JSON.stringify(r.names) === JSON.stringify(names)
      && JSON.stringify(r.counts) === JSON.stringify(acmeIndex.types.map((t) => String(t.count))), ...r };
  });

  await check("G4-G5-G7", async () => {
    const r = await page.evaluate(() => {
      const rows = (id) => Array.from(document.querySelectorAll(`#okf-facet-${id} ~ .okf-facet-list .okf-facet-row, #okf-facet-${id} ~ .okf-facet-row`));
      const trust = rows("trust");
      const empty = trust[2] && trust[2].querySelector(".okf-glyph-blank");
      return {
        trust: trust.map((x) => x.querySelector(".okf-facet-name").textContent),
        trustCounts: trust.map((x) => x.querySelector(".okf-facet-count").textContent),
        trustChecked: trust.map((x) => x.querySelector("input").checked),
        trustGlyphs: trust.slice(0, 2).every((x) => x.querySelector("svg.okf-glyph")),
        emptySlot: empty ? [empty.getBoundingClientRect().width, empty.getBoundingClientRect().height] : null,
        fresh: rows("freshness").map((x) => x.querySelector(".okf-facet-name").textContent),
        freshChecked: rows("freshness").map((x) => x.querySelector("input").checked),
        freshGlyph: !!(rows("freshness")[0] && rows("freshness")[0].querySelector("svg.okf-glyph")),
        display: rows("display").map((x) => [x.textContent.trim(), x.getBoundingClientRect().height, x.querySelector("input").checked]),
      };
    });
    const sum = (a) => a.reduce((x, y) => x + Number(y), 0);
    const pass = JSON.stringify(r.trust) === JSON.stringify(["human-reviewed", "machine-confirmed", "unverified"]) && r.trustGlyphs
      && sum(r.trustCounts) === acmeIndex.concepts.length && r.trustChecked.every(Boolean)
      && r.emptySlot && near(r.emptySlot[0], 10) && near(r.emptySlot[1], 10)
      && JSON.stringify(r.fresh) === JSON.stringify(["Stale only (as of now)"]) && r.freshGlyph && r.freshChecked[0] === false
      && JSON.stringify(r.display.map((d) => d[0])) === JSON.stringify(["Node labels", "Dim unmatched instead of hiding"])
      && r.display.every(([, height]) => near(height, 30)) && r.display[0][2] === true && r.display[1][2] === false;
    return { pass, ...r };
  });

  await check("G6", async () => {
    // Acme's tags are short: a chip is one line, 26 high. A tag longer than the
    // panel wraps inside its chip (viewer.css) and is taller; that is the
    // OKF4net site's case, checked in G6-more.
    const r = await page.evaluate(() => Array.from(document.querySelectorAll("#okf-facets button.okf-chip"), (b) => {
      const cs = getComputedStyle(b);
      return { text: b.textContent, height: b.getBoundingClientRect().height, hidden: b.hidden, pressed: b.getAttribute("aria-pressed"), color: cs.color, bg: cs.backgroundColor };
    }));
    const shown = r.filter((x) => !x.hidden);
    const counts = new Map();
    for (const c of acmeIndex.concepts) { for (const t of new Set(c.tags)) { counts.set(t, (counts.get(t) || 0) + 1); } }
    const want = Array.from(counts).sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0)).slice(0, 12).map(([t, n]) => `${t} ${n}`);
    return { pass: shown.length <= 12 && shown.every((x) => near(x.height, 26) && x.pressed === "false")
      && JSON.stringify(shown.map((x) => x.text)) === JSON.stringify(want), chips: r.length, first: r.slice(0, 4) };
  });

  await check("G9-G10-G15", async () => {
    const r = await page.evaluate(() => {
      const main = document.getElementById("okf-main").getBoundingClientRect();
      const rect = (id) => document.getElementById(id).getBoundingClientRect();
      const status = getComputedStyle(document.getElementById("okf-graph-status"));
      const legend = getComputedStyle(document.getElementById("okf-graph-legend"));
      const buttons = Array.from(document.querySelectorAll("#okf-graph-zoom button"), (b) => {
        const r0 = b.getBoundingClientRect();
        return [b.getAttribute("aria-label") || b.textContent, r0.width, r0.height, getComputedStyle(b).fontSize];
      });
      const edges = Array.from(document.querySelectorAll("#okf-graph-zoom button"), (b) => b.getBoundingClientRect());
      return {
        status: [rect("okf-graph-status").top - main.top, rect("okf-graph-status").left - main.left, status.fontSize, status.fontFamily, status.color],
        statusText: document.getElementById("okf-graph-status").textContent, statusRole: document.getElementById("okf-graph-status").getAttribute("role"),
        zoom: [rect("okf-graph-zoom").top - main.top, main.right - rect("okf-graph-zoom").right], buttons,
        zoomGap: edges.slice(1).map((e, k) => e.left - edges[k].right),
        legend: [rect("okf-graph-legend").left - main.left, main.bottom - rect("okf-graph-legend").bottom, legend.fontSize, legend.color, legend.columnGap],
        legendText: Array.from(document.querySelectorAll("#okf-graph-legend span"), (s) => s.textContent),
      };
    });
    const idx = acmeIndex;
    const wantStatus = `showing ${idx.concepts.length} of ${idx.concepts.length} concepts ${MIDDOT} ${idx.edges.length} of ${idx.edges.length} links`;
    const wantLegend = [
      `Edge = body link (${String.fromCharCode(0xa7)}6), as in okf graph ${MIDDOT} arrow points at the target`,
      "Red dashed = broken link to an absent concept",
      "Blue = selection and its links",
      `Drag to pan ${MIDDOT} scroll to zoom`,
    ];
    const names = r.buttons.map((b) => b[0]);
    const pass = near(r.status[0], 14) && near(r.status[1], 18) && r.status[2] === "12px" && /Space Mono/.test(r.status[3])
      && r.statusRole === "status" && r.statusText === wantStatus
      && near(r.zoom[0], 12) && near(r.zoom[1], 18) && near(r.buttons[0][1], 34) && near(r.buttons[0][2], 34) && near(r.buttons[1][1], 34)
      && r.buttons[0][3] === "18px" && r.buttons[2][3] === "13px" && near(r.buttons[2][2], 34) && names.join("|") === "Zoom in|Zoom out|Fit|List"
      && r.zoomGap.every((g) => near(g, 6))
      && near(r.legend[0], 18) && near(r.legend[1], 16) && r.legend[2] === "12px" && r.legend[4] === "14px"
      && JSON.stringify(r.legendText) === JSON.stringify(wantLegend);
    return { pass, wantStatus, ...r };
  });

  await check("G11-G12", async () => {
    const r = await page.evaluate(() => {
      const out = { nodes: [], edges: [], markers: [] };
      for (const g of document.querySelectorAll("#okf-graph-canvas g.okf-node")) {
        const shape = g.querySelector('[class^="okf-shape-"], .okf-ghost-mark');
        const box = shape.getBBox();
        const label = g.querySelector("text");
        const cs = getComputedStyle(label);
        out.nodes.push({ cls: shape.getAttribute("class"), w: box.width, h: box.height, labelSize: cs.fontSize, labelFont: cs.fontFamily, labelFill: cs.fill,
          labelGap: Number(label.getAttribute("y")) - (box.y + box.height), anchor: label.getAttribute("text-anchor"),
          cx: Math.abs(Number(label.getAttribute("x")) - (box.x + box.width / 2)) });
      }
      for (const l of document.querySelectorAll("#okf-graph-canvas line.okf-graph-edge")) {
        const cs = getComputedStyle(l);
        out.edges.push({ ghost: l.classList.contains("okf-graph-edge-ghost"), width: cs.strokeWidth, dash: cs.strokeDasharray, stroke: cs.stroke, marker: l.getAttribute("marker-end") });
      }
      for (const id of ["okf-graph-arrow", "okf-graph-arrow-sel"]) {
        const m = document.getElementById(id);
        out.markers.push({ id, w: m.getAttribute("markerWidth"), fill: getComputedStyle(m.querySelector("path")).fill });
      }
      out.ink = getComputedStyle(document.documentElement).getPropertyValue("--ink").trim();
      out.edge = getComputedStyle(document.documentElement).getPropertyValue("--edge").trim();
      out.blue = getComputedStyle(document.documentElement).getPropertyValue("--blue").trim();
      out.types = window.OKF_INDEX.types.map((t) => [t.name, t.slot]);
      return out;
    });
    // Filled shapes: getBBox() is the geometry of §12.2 (no stroke). A
    // triangle's box is as wide as its size and a little lower than it, so
    // its label gap is measured from the box and allowed that difference.
    const want = { "okf-shape-0": 26, "okf-shape-1": 30, "okf-shape-2": 31.1, "okf-shape-3": 24, "okf-ghost-mark": 26 };
    const sized = r.nodes.filter((n) => want[n.cls] !== undefined);
    const inkRgb = rgb(r.ink);
    const pass = sized.length > 0 && sized.every((n) => near(Math.max(n.w, n.h), want[n.cls], 0.2) && n.labelSize === "11.5px" && /Space Mono/.test(n.labelFont)
      && n.labelFill === inkRgb && n.anchor === "middle" && near(n.labelGap, 16, n.cls === "okf-shape-3" ? 3 : 1) && n.cx < 0.1)
      && r.edges.filter((e) => !e.ghost).every((e) => e.width === "1.3px" && e.stroke === rgb(r.edge))
      && r.edges.filter((e) => e.ghost).every((e) => e.width === "1.4px" && /^5(px)?,? ?4/.test(e.dash))
      && r.markers[0].w === "7" && r.markers[0].fill === rgb(r.edge) && r.markers[1].w === "8" && r.markers[1].fill === rgb(r.blue);
    return { pass, nodes: r.nodes.length, ghosts: r.nodes.filter((n) => n.cls === "okf-ghost-mark").length, sample: sized.slice(0, 5), edges: r.edges.slice(0, 3), markers: r.markers, types: r.types };
  });

  await check("G11-label-cut", async () => {
    // Labels are cut at 24 code points (23 + an ellipsis), the full id stays in
    // <title>. The OKF4net bundle has long ids; the cut is measured in code
    // points, not UTF-16 units.
    const p = await newPage();
    await openGraph(siteGraph, p);
    const r = await p.evaluate((ellipsis) => {
      const out = { cut: 0, whole: 0, bad: [], longest: 0 };
      for (const g of document.querySelectorAll("#okf-graph-canvas g.okf-node")) {
        const label = Array.from(g.querySelector("text").textContent);
        const id = g.querySelector("title").textContent;
        const segment = Array.from(id.slice(id.lastIndexOf("/") + 1));
        out.longest = Math.max(out.longest, label.length);
        const cut = segment.length > 24;
        const expected = cut ? segment.slice(0, 23).join("") + ellipsis : segment.join("");
        if (cut) { out.cut++; } else { out.whole++; }
        if (label.join("") !== expected || !g.getAttribute("aria-label")) { out.bad.push([id, label.join("")]); }
      }
      return out;
    }, ELLIPSIS);
    return { pass: r.cut > 0 && r.bad.length === 0 && r.longest <= 24, ...r, bad: r.bad.slice(0, 3) };
  });

  await check("G10-fit", async () => {
    // Fit: every drawn shape and label inside the canvas, 24 beside and 56 above
    // and below at least (less a few px: the layout's label width is an
    // estimate), never past 1.25x.
    const measure = (p) => p.evaluate(() => {
      const c = document.getElementById("okf-graph-canvas").getBoundingClientRect();
      let l = Infinity; let r = -Infinity; let t = Infinity; let b = -Infinity;
      for (const g of document.querySelectorAll("#okf-graph-canvas g.okf-node")) {
        for (const e of [g.querySelector('[class^="okf-shape-"], .okf-ghost-mark'), g.querySelector("text")]) {
          const x = e.getBoundingClientRect();
          l = Math.min(l, x.left - c.left); r = Math.max(r, c.right - x.right); t = Math.min(t, x.top - c.top); b = Math.max(b, c.bottom - x.bottom);
        }
      }
      return { left: l, right: r, top: t, bottom: b };
    });
    const mine = await measure(page);
    const s1 = (await viewOf(page)).s;
    const p = await newPage();
    await openGraph(siteGraph, p);
    const big = await measure(p);
    const s2 = (await viewOf(p)).s;
    const inside = (m) => m.left >= 22 && m.right >= 22 && m.top >= 54 && m.bottom >= 54;
    return { pass: s1 <= 1.25 + 1e-3 && s2 <= 1.25 + 1e-3 && inside(mine) && inside(big), acme: { scale: s1, ...mine }, okf4net: { scale: s2, ...big } };
  });

  await check("G14-G17-H10", async () => {
    await clickNode(page, 0);
    const r = await page.evaluate(() => {
      const g = document.querySelector("#okf-graph-canvas g.okf-node.okf-selected");
      const ring = g.querySelector(".okf-node-ring");
      const shape = g.querySelector('[class^="okf-shape-"]');
      const detail = document.getElementById("okf-graph-detail");
      const open = detail.querySelector(".okf-graph-open");
      const dcs = getComputedStyle(detail);
      const rcs = getComputedStyle(ring);
      const id = detail.querySelector(".okf-graph-detail-id");
      return {
        ring: [ring.getBBox().width, shape.getBBox().width, rcs.visibility, rcs.display, rcs.stroke, rcs.strokeWidth],
        drawer: detail.getBoundingClientRect().width, padding: dcs.padding, gap: dcs.rowGap, borderLeft: dcs.borderLeftWidth,
        label: detail.querySelector(".okf-section-title").textContent, idFont: getComputedStyle(id).fontFamily, idSize: getComputedStyle(id).fontSize,
        title: getComputedStyle(detail.querySelector("h2")).fontSize,
        titleGap: detail.querySelector("h2").getBoundingClientRect().top - id.getBoundingClientRect().bottom,
        open: open ? open.getBoundingClientRect().height : null, openText: open ? open.textContent : null,
        reading: document.getElementById("okf-reading-view").getAttribute("href"),
        hash: location.hash, current: document.querySelectorAll("#okf-graph-canvas [aria-current]").length,
        blueEdges: Array.from(document.querySelectorAll("#okf-graph-canvas .okf-graph-edge-out, #okf-graph-canvas .okf-graph-edge-in"), (e) => {
          const cs = getComputedStyle(e);
          return [e.classList.contains("okf-graph-edge-out"), cs.stroke, cs.strokeWidth, cs.strokeDasharray];
        }),
        chips: Array.from(detail.querySelectorAll(".okf-chip"), (c) => c.textContent),
        blocks: Array.from(detail.querySelectorAll(".okf-graph-rel h3"), (h) => h.textContent),
        blue: getComputedStyle(document.documentElement).getPropertyValue("--blue").trim(),
      };
    });
    // Leave the node and come back by the keyboard (PageDown, PageUp: Tab
    // would do, but WebKit does not Tab to the drawer's links): :focus-visible.
    await page.keyboard.press("PageDown");
    await page.keyboard.press("PageUp");
    const focus = await page.evaluate(() => {
      const g = document.activeElement;
      const f = g && g.querySelector && g.querySelector(".okf-node-focus");
      if (!f) { return null; }
      const cs = getComputedStyle(f);
      return { focused: g.classList.contains("okf-focused"), side: f.getBBox().width, visibility: cs.visibility, dash: cs.strokeDasharray, stroke: cs.stroke, width: cs.strokeWidth,
        ink: getComputedStyle(document.documentElement).getPropertyValue("--ink").trim(), ringVisible: getComputedStyle(g.querySelector(".okf-node-ring")).visibility };
    });
    await shot("graph-selected");
    const ringOk = r.ring[2] === "visible" && near(r.ring[0] - r.ring[1], 12, 0.5) && r.ring[4] === rgb(r.blue) && near(parseFloat(r.ring[5]), 2.4, 0.01);
    const edgesOk = r.blueEdges.length > 0 && r.blueEdges.every(([out, stroke, width, dash]) => stroke === rgb(r.blue) && width === "2.2px" && (out ? dash === "none" : /^5(px)?,? ?4/.test(dash)));
    const focusOk = focus && focus.focused && near(focus.side, 26 + 20, 6) && focus.visibility === "visible" && /^3(px)?,? ?3/.test(focus.dash) && focus.stroke === rgb(focus.ink) && focus.width === "2px";
    const pass = ringOk && edgesOk && focusOk && near(r.drawer, 340) && r.padding === "20px" && r.gap === "16px" && r.label === "Selected"
      && /Space Mono/.test(r.idFont) && r.idSize === "12px" && r.title === "24px" && near(r.titleGap, 6, 1)
      && near(r.open, 40) && r.openText === "Open page" && r.reading !== "index.html" && r.hash.length > 1 && r.current === 1
      && r.blocks.length === 2 && /^Links to /.test(r.blocks[0]) && /^Referenced by /.test(r.blocks[1]);
    return { pass, ringOk, edgesOk, focusOk, ...r, focus };
  });

  await check("C13-history", async () => {
    // Under file://, selection must rewrite the URL without a new entry
    // (replaceState, or location.replace when it throws).
    const p = await newPage();
    const ids = acmeIndex.concepts.slice(0, 3).map((c) => c.id);
    await p.goto(acmeGraph + "#" + ids[0]);
    await waitLayout(p);
    const before = await p.evaluate(() => ({ length: history.length, selected: document.querySelector("#okf-graph-detail .okf-graph-detail-id").textContent }));
    await clickNode(p, 1);
    await clickNode(p, 2);
    const after = await p.evaluate(() => ({ length: history.length, hash: decodeURIComponent(location.hash.slice(1)), selected: document.querySelector("#okf-graph-detail .okf-graph-detail-id").textContent }));
    return { pass: before.selected === ids[0] && after.length === before.length && after.hash === ids[2] && after.selected === ids[2], before, after };
  });

  await check("G18-fragment", async () => {
    // From a concept page, "Global graph" opens the graph with that concept
    // selected and centred; selecting others adds no history entry; Back leaves
    // the graph for the concept page; an unknown fragment selects nothing.
    const p = await newPage();
    const concept = acmeIndex.concepts.find((c) => c.path === "computations/gross-margin-period.html") || acmeIndex.concepts[1];
    const conceptUrl = ctx.lib.pageUrl(ctx.acme, concept.path);
    await p.goto(conceptUrl);
    await p.click("#okf-global-graph");
    await p.waitForURL((u) => /graph\.html/.test(u.href));
    await waitLayout(p);
    const r = await p.evaluate(() => {
      const c = document.getElementById("okf-graph-canvas").getBoundingClientRect();
      const g = document.querySelector("#okf-graph-canvas g.okf-node.okf-selected");
      const b = g && g.querySelector('[class^="okf-shape-"]').getBoundingClientRect();
      return { selected: document.querySelector("#okf-graph-detail .okf-graph-detail-id").textContent,
        offset: b ? [b.x + b.width / 2 - (c.x + c.width / 2), b.y + b.height / 2 - (c.y + c.height / 2)] : null, length: history.length };
    });
    await clickNode(p, 0);
    await clickNode(p, 1);
    const grew = await p.evaluate(() => history.length);
    await p.goBack();
    await p.waitForURL((u) => u.href.split("#")[0] === conceptUrl.split("#")[0]);
    const back = p.url();
    const q = await newPage();
    await q.goto(acmeGraph + "#no-such-concept");
    await waitLayout(q);
    const none = await q.evaluate(() => ({ selected: document.querySelectorAll("#okf-graph-canvas .okf-selected").length, text: document.querySelector("#okf-graph-detail .okf-graph-empty")?.textContent }));
    const pass = r.selected === concept.id && r.offset && Math.abs(r.offset[0]) < 3 && Math.abs(r.offset[1]) < 3 && grew === r.length
      && back.split("#")[0] === conceptUrl.split("#")[0] && none.selected === 0 && none.text === "Select a concept to see its links.";
    return { pass, concept: concept.id, ...r, grew, back, none };
  });

  await check("G16", async () => {
    await openGraph(acmeGraph);
    const r = {};
    const box = await page.locator("#okf-graph-canvas").boundingBox();
    // An earlier session saw Playwright's Firefox send pointermove events at
    // (0, -84) and no pointerup during a scripted drag; on playwright-core 1.63
    // with Firefox 155 (2026-10-08) the drag arrives intact (the events are
    // recorded in the report), so it is measured in every browser. Should it
    // break again, the check FAILS: the by-hand line is ACCEPTANCE.md P3-3.
    {
      const t0 = await viewOf(page);
      // A point of the drawing's background: the <svg> itself is hit there.
      const spot = await page.evaluate(() => {
        const svg = document.querySelector("#okf-graph-canvas svg");
        const r0 = svg.getBoundingClientRect();
        for (let y = r0.top + 60; y < Math.min(r0.bottom, innerHeight) - 60; y += 10) {
          for (let x = r0.left + 10; x < r0.right - 80; x += 10) {
            if (document.elementFromPoint(x, y) === svg) { return { x, y }; }
          }
        }
        return null;
      });
      await page.mouse.move(spot.x, spot.y);
      await page.mouse.down();
      await page.mouse.move(spot.x + 60, spot.y - 40, { steps: 4 });
      await page.mouse.up();
      const t1 = await viewOf(page);
      r.pan = [t1.a - t0.a, t1.b - t0.b];
      // Where node 1 stands: its <g>'s translate (revision 12: the shapes and the label are drawn once around (0, 0)).
      const label = () => page.evaluate(() => Number(/translate\((-?[0-9.]+) /.exec(document.querySelectorAll("#okf-graph-canvas g.okf-node")[1].getAttribute("transform"))[1]));
      const x0 = await label();
      const at = await pointOn(page, 1);
      await page.mouse.move(at.x, at.y);
      await page.mouse.down();
      await page.mouse.move(at.x + 30, at.y + 20, { steps: 4 });
      await page.mouse.up();
      r.nodeMoved = (await label()) - x0;
      r.selectedByDrag = await page.evaluate(() => document.querySelectorAll("#okf-graph-canvas .okf-selected").length);
      r.dragPass = near(r.pan[0], 60, 1) && near(r.pan[1], -40, 1) && near(r.nodeMoved, 30 / t1.s, 1) && r.selectedByDrag === 0;
    }
    const before = await viewOf(page);
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.wheel(0, -200);
    r.wheel = (await viewOf(page)).s / before.s;
    // Back to the fitted view, so the node is on screen whatever the zoom did.
    await page.locator("#okf-graph-zoom button", { hasText: "Fit" }).click();
    const target = acmeIndex.concepts[0].path;
    const at = await pointOn(page, 0);
    await page.mouse.dblclick(at.x, at.y);
    await page.waitForURL((url) => decodeURIComponent(url.href).endsWith(target), { timeout: 10000 });
    r.opened = decodeURIComponent(page.url()).endsWith(target);
    const pass = r.dragPass && r.wheel > 1 && r.opened;
    return { pass, ...r };
  });

  await check("G16-drag-during-layout", async () => {
    // A node dragged while the layout runs stops it where it is (the drag takes
    // over: §4.2's determinism covers the initial layout only), and nothing
    // moves afterwards. The OKF4net site's layout lasts a few seconds.
    const p = await newPage();
    await p.goto(siteGraph);
    await p.waitForFunction(() => document.getElementById("okf-graph-canvas").getAttribute("data-okf-layout") === "running", null, { timeout: 30000 });
    await p.evaluate(() => {
      window.okfDown = [];
      window.addEventListener("pointerdown", (e) => window.okfDown.push(!!e.target.closest && !!e.target.closest("g.okf-node")), true);
    });
    let started = null;
    for (let attempt = 0; attempt < 8 && !started; attempt++) {
      const at = await pointOn(p, 400 + attempt);
      if (!at) { continue; }
      await p.mouse.move(at.x, at.y);
      await p.mouse.down();
      const onNode = await p.evaluate(() => window.okfDown[window.okfDown.length - 1]);
      if (onNode) { started = at; break; }
      await p.mouse.up();
    }
    if (!started) { return { pass: null, note: "no node could be pressed while the layout moved them" }; }
    const before = await p.evaluate(() => document.getElementById("okf-graph-canvas").getAttribute("data-okf-layout"));
    await p.mouse.move(started.x + 24, started.y + 16, { steps: 4 });
    const state = await p.evaluate(() => document.getElementById("okf-graph-canvas").getAttribute("data-okf-layout"));
    await p.mouse.up();
    const labels = () => p.evaluate(() => Array.from(document.querySelectorAll("#okf-graph-canvas g.okf-node"), (g) => g.getAttribute("transform")).join(";"));
    const a = await labels();
    await p.waitForTimeout(800);
    const b = await labels();
    return { pass: before === "running" && state === "done" && a === b, before, state, still: a === b };
  });

  await check("G14-keyboard", async () => {
    // One tab stop for the whole graph; PageDown/PageUp/Home/End walk every node
    // in index order; every neighbour of a node is reachable by an arrow; Space
    // selects (aria-current moves); the keyboard contour shows for the keyboard
    // only (:focus-visible), never after a mouse press; a focused node is brought
    // into view at any zoom.
    const p = await newPage();
    await openGraph(acmeGraph, p);
    const active = () => p.evaluate(() => {
      const g = document.activeElement;
      return g && g.classList && g.classList.contains("okf-node") ? g.querySelector("title").textContent : null;
    });
    await p.focus("#okf-graph-zoom button:last-child");
    await p.keyboard.press("Tab");
    const landed = await active();
    const contourOnTab = await p.evaluate(() => { const g = document.activeElement; return g.classList.contains("okf-node") && getComputedStyle(g.querySelector(".okf-node-focus")).visibility; });
    const stops = await p.evaluate(() => document.querySelectorAll('#okf-graph-canvas g[tabindex="0"]').length);
    // Space selects it, so the drawer holds links for Tab to reach: with nothing
    // after the graph, Firefox leaves the focus where it is.
    await p.keyboard.press(" ");
    await p.keyboard.press("Tab");
    const leftGraph = await p.evaluate(() => !document.getElementById("okf-graph-canvas").contains(document.activeElement));
    await p.evaluate(() => document.querySelector("#okf-graph-canvas g.okf-node").focus());
    // Index order.
    await p.keyboard.press("Home");
    const order = [await active()];
    for (let k = 1; k < acmeIndex.concepts.length; k++) { await p.keyboard.press("PageDown"); order.push(await active()); }
    await p.keyboard.press("End");
    const end = await active();
    await p.keyboard.press("PageUp");
    const beforeEnd = await active();
    const ids = acmeIndex.concepts.map((c) => c.id);
    // Neighbours: arrows from the node with the most links.
    const links = ids.map(() => new Set());
    for (const [from, to, , ghost] of acmeIndex.edges) { if (ghost !== 1 && from !== to) { links[from].add(to); links[to].add(from); } }
    const hub = links.reduce((best, s, k) => (s.size > links[best].size ? k : best), 0);
    const reached = new Set();
    for (const arrow of ["ArrowRight", "ArrowLeft", "ArrowUp", "ArrowDown"]) {
      await p.evaluate((k) => document.querySelectorAll("#okf-graph-canvas g.okf-node")[k].focus(), hub);
      const seen = new Set();
      for (let k = 0; k <= links[hub].size + 1; k++) {
        await p.keyboard.press(arrow);
        const now = await active();
        if (now === ids[hub] || seen.has(now)) { break; }
        seen.add(now);
        reached.add(now);
      }
    }
    const missing = Array.from(links[hub], (k) => ids[k]).filter((id) => !reached.has(id));
    // Space selects the focused node; aria-current moves with it.
    await p.evaluate((k) => document.querySelectorAll("#okf-graph-canvas g.okf-node")[k].focus(), 1);
    await p.keyboard.press(" ");
    const selected = await p.evaluate(() => ({ current: Array.from(document.querySelectorAll("#okf-graph-canvas [aria-current]"), (g) => g.querySelector("title").textContent),
      detail: document.querySelector("#okf-graph-detail .okf-graph-detail-id").textContent }));
    // The mouse press shows no contour; the next key does.
    await clickNode(p, 2);
    const mouse = await p.evaluate(() => { const g = document.activeElement; return { onNode: g.classList.contains("okf-node"), visible: g.matches(":focus-visible"), contour: getComputedStyle(g.querySelector(".okf-node-focus")).visibility }; });
    await p.keyboard.press("PageDown");
    const keyed = await p.evaluate(() => { const g = document.activeElement; return { visible: g.matches(":focus-visible"), contour: getComputedStyle(g.querySelector(".okf-node-focus")).visibility }; });
    // Focus reveal at a high zoom.
    const zoomIn = p.locator("#okf-graph-zoom button[aria-label='Zoom in']");
    for (let k = 0; k < 6; k++) { await zoomIn.click(); }
    await p.keyboard.press("Home");
    await p.evaluate((k) => document.querySelectorAll("#okf-graph-canvas g.okf-node")[k].focus(), 0);
    const outside = [];
    for (let k = 0; k < acmeIndex.concepts.length; k++) {
      const m = await p.evaluate(() => {
        const c = document.getElementById("okf-graph-canvas").getBoundingClientRect();
        const r0 = document.activeElement.querySelector('[class^="okf-shape-"]').getBoundingClientRect();
        return [r0.left - c.left, c.right - r0.right, r0.top - c.top, c.bottom - r0.bottom];
      });
      if (m.some((v) => v < -0.5)) { outside.push([k, m]); }
      await p.keyboard.press("PageDown");
    }
    const pass = landed !== null && contourOnTab === "visible" && stops === 1 && leftGraph
      && JSON.stringify(order) === JSON.stringify(ids) && end === ids[ids.length - 1] && beforeEnd === ids[ids.length - 2]
      && missing.length === 0 && selected.current.length === 1 && selected.current[0] === selected.detail
      && mouse.onNode && !mouse.visible && mouse.contour === "hidden" && keyed.visible && keyed.contour === "visible" && outside.length === 0;
    return { pass, landed, contourOnTab, stops, leftGraph, orderOk: JSON.stringify(order) === JSON.stringify(ids), order: order.slice(0, 3), end, hub: ids[hub], neighbours: links[hub].size, missing, selected, mouse, keyed, outside };
  });

  await check("G8-facets", async () => {
    // AND/OR semantics, fixed counts, "Dim unmatched" keeps nodes drawn at 0.25.
    const p = await newPage();
    await openGraph(acmeGraph, p);
    const first = acmeIndex.types[0];
    const status = () => p.textContent("#okf-graph-status");
    const nodes = () => p.locator("#okf-graph-canvas g.okf-node").count();
    const N = acmeIndex.concepts.length;
    const out = { initial: await status() };
    const box = p.locator("#okf-facet-type ~ .okf-facet-list input").first();
    await box.focus();
    await p.keyboard.press(" ");
    out.focusStays = await p.evaluate(() => document.activeElement.type === "checkbox" && !document.activeElement.checked);
    out.unchecked = await status();
    out.nodesUnchecked = await nodes();
    out.countFixed = await p.textContent("#okf-facet-type ~ .okf-facet-list .okf-facet-count");
    out.nameGray = await p.evaluate(() => getComputedStyle(document.querySelector("#okf-facet-type ~ .okf-facet-list .okf-facet-name")).color);
    await p.locator("#okf-facet-display ~ .okf-facet-row input").nth(1).check();
    out.dimStatus = await status();
    out.nodesDim = await nodes();
    out.dimmed = await p.evaluate(() => Array.from(document.querySelectorAll("#okf-graph-canvas g.okf-node.okf-graph-dim"), (g) => getComputedStyle(g).opacity));
    await p.locator("#okf-facet-display ~ .okf-facet-row input").nth(1).uncheck();
    await box.check();
    const tagButton = p.locator("#okf-facets button.okf-chip").first();
    await tagButton.click();
    out.tagPressed = await tagButton.getAttribute("aria-pressed");
    out.tagStatus = await status();
    out.tagStyle = await tagButton.evaluate((b) => { const cs = getComputedStyle(b); return [cs.backgroundColor, cs.color]; });
    await tagButton.click();
    out.labelsOff = await (async () => {
      await p.locator("#okf-facet-display ~ .okf-facet-row input").first().uncheck();
      const hidden = await p.evaluate(() => Array.from(document.querySelectorAll("#okf-graph-canvas text.okf-graph-label")).every((t) => getComputedStyle(t).display === "none"));
      await p.locator("#okf-facet-display ~ .okf-facet-row input").first().check();
      return hidden;
    })();
    const pass = out.initial.startsWith(`showing ${N} of ${N} `) && out.focusStays
      && out.unchecked.startsWith(`showing ${N - first.count} of ${N} `) && out.nodesUnchecked === N - first.count && out.countFixed === String(first.count)
      && out.nameGray === rgb("#6a6a72")
      && out.dimStatus.startsWith(`showing ${N - first.count} of ${N} `) && out.nodesDim === N && out.dimmed.length === first.count && out.dimmed.every((o) => o === "0.25")
      && out.tagPressed === "true" && /^showing \d+ of /.test(out.tagStatus) && out.labelsOff;
    return { pass, ...out };
  });

  await check("G6-more", async () => {
    // The first 12 tags, then "Show all tags (K)", which reveals the rest and
    // puts the focus on the first revealed. A tag longer than the panel wraps
    // inside its chip instead of widening the panel.
    // The over-limit site has 20 tags, one of them longer than the panel.
    const p = await newPage();
    if (bigGraph) { await p.goto(bigGraph); await p.waitForSelector("#okf-facets button.okf-chip"); } else { await openGraph(siteGraph, p); }
    const r = await p.evaluate(() => {
      const chips = Array.from(document.querySelectorAll("#okf-facets button.okf-chip"));
      const more = document.querySelector("#okf-facets .okf-facet-more");
      const facets = document.getElementById("okf-facets");
      return { total: chips.length, shown: chips.filter((c) => !c.hidden).length, more: more ? more.textContent : null,
        overflow: facets.scrollWidth - facets.clientWidth, widest: Math.max(...chips.filter((c) => !c.hidden).map((c) => c.getBoundingClientRect().width)) };
    });
    if (!r.more) { return { pass: null, note: "neither the OKF4net site nor OKF_RECETTE_BIG has more than 12 tags", ...r }; }
    await p.click("#okf-facets .okf-facet-more");
    const after = await p.evaluate(() => ({ shown: Array.from(document.querySelectorAll("#okf-facets button.okf-chip")).filter((c) => !c.hidden).length,
      focused: document.activeElement.classList.contains("okf-chip"), gone: !document.querySelector("#okf-facets .okf-facet-more"),
      overflow: document.getElementById("okf-facets").scrollWidth - document.getElementById("okf-facets").clientWidth }));
    return { pass: r.shown === 12 && r.more === `Show all tags (${r.total})` && after.shown === r.total && after.focused && after.gone && r.overflow <= 0 && after.overflow <= 0, ...r, after };
  });

  await check("G11-contrast", async () => {
    // One fresh page per theme, its system colour scheme set by ctx.newPage:
    // with nothing stored, okf-theme.js follows the system (§11.0). Shapes and
    // edges need 3:1 against the page, labels 4.5:1.
    const r = {};
    for (const theme of ["light", "dark"]) {
      const themed = await newPage({ colorScheme: theme });
      await openGraph(acmeGraph, themed);
      r[theme] = await themed.evaluate(() => {
        const bg = getComputedStyle(document.body).backgroundColor;
        const out = [];
        for (const s of document.querySelectorAll("#okf-graph-canvas g.okf-node > [class^='okf-shape-']")) {
          const cs = getComputedStyle(s);
          out.push({ kind: "shape", cls: s.getAttribute("class"), paint: cs.fill !== "none" ? cs.fill : cs.stroke, bg, need: 3 });
        }
        const edge = document.querySelector("#okf-graph-canvas line.okf-graph-edge");
        if (edge) { out.push({ kind: "edge", cls: "edge", paint: getComputedStyle(edge).stroke, bg, need: 3 }); }
        const label = document.querySelector("#okf-graph-canvas text.okf-graph-label");
        if (label) { out.push({ kind: "label", cls: "label", paint: getComputedStyle(label).fill, bg, need: 4.5 }); }
        return out;
      });
      if (theme === "dark") { await shot("graph-dark", themed); }
    }
    const ratios = [...r.light.map((x) => ({ theme: "light", ...x })), ...r.dark.map((x) => ({ theme: "dark", ...x }))]
      .map((x) => ({ theme: x.theme, kind: x.kind, cls: x.cls, need: x.need, ratio: contrast(x.paint, x.bg) }));
    return { pass: ratios.every((x) => x.ratio >= x.need), checked: ratios.length, lowest: ratios.slice().sort((a, b) => a.ratio - b.ratio).slice(0, 4) };
  });

  await check("G19", async () => {
    const narrow = await newPage({ viewport: { width: 390, height: 844 } });
    await openGraph(acmeGraph, narrow);
    const measure = () => narrow.evaluate(() => ({
      scrollW: document.documentElement.scrollWidth,
      summary: getComputedStyle(document.querySelector("#okf-facets summary")).display,
      open: document.querySelector("#okf-facets details").open,
      order: ["okf-facets", "okf-main", "okf-graph-detail"].map((id) => Math.round(document.getElementById(id).getBoundingClientRect().top)),
      canvas: document.getElementById("okf-graph-canvas").getBoundingClientRect().height,
    }));
    const r = await measure();
    await shot("graph-390", narrow);
    await clickNode(narrow, 0);
    const selected = await measure();
    await narrow.locator("#okf-facets summary").click();
    const unfolded = await measure();
    await narrow.locator("#okf-graph-detail").scrollIntoViewIfNeeded();
    await shot("graph-390-selected", narrow);
    // The 1 100 px breakpoint of the facets: stacked at 1 099, side by side at 1 100.
    const widths = {};
    for (const width of [1099, 1100]) {
      const w = await newPage({ viewport: { width, height: 800 } });
      await openGraph(acmeGraph, w);
      widths[width] = await w.evaluate(() => ({ facets: document.getElementById("okf-facets").getBoundingClientRect().width, summary: getComputedStyle(document.querySelector("#okf-facets summary")).display,
        side: document.getElementById("okf-facets").getBoundingClientRect().top === document.getElementById("okf-main").getBoundingClientRect().top, scrollW: document.documentElement.scrollWidth }));
    }
    const pass = r.scrollW <= 390 && selected.scrollW <= 390 && unfolded.scrollW <= 390 && r.summary !== "none" && r.open === false
      && r.order[0] < r.order[1] && r.order[1] < r.order[2] && r.canvas >= 400
      && widths[1099].summary !== "none" && !widths[1099].side && widths[1100].summary === "none" && widths[1100].side && near(widths[1100].facets, 270);
    return { pass, ...r, selected, unfolded, widths };
  });

  await check("G13-ghost", async () => {
    if (!ghostGraph) { return { pass: null, note: "OKF_RECETTE_GHOST is not set: neither official site has a broken link, so no ghost is drawn" }; }
    const p = await newPage();
    await openGraph(ghostGraph, p);
    const index = ctx.lib.readIndex(ghostSite.dir);
    const r = await p.evaluate(() => {
      const root = getComputedStyle(document.documentElement);
      const ghosts = Array.from(document.querySelectorAll("#okf-graph-canvas g.okf-graph-ghost"), (g) => {
        const mark = g.querySelector(".okf-ghost-mark");
        const cs = getComputedStyle(mark);
        const label = g.querySelector("text");
        return { size: Math.max(mark.getBBox().width, mark.getBBox().height), fill: cs.fill, stroke: cs.stroke, width: cs.strokeWidth, dash: cs.strokeDasharray,
          title: g.querySelector("title").textContent, label: label.textContent, labelFill: getComputedStyle(label).fill, labelSize: getComputedStyle(label).fontSize,
          opacity: getComputedStyle(g).opacity };
      });
      return { ghosts, ghost: root.getPropertyValue("--ghost").trim(), white: root.getPropertyValue("--white").trim(),
        edges: Array.from(document.querySelectorAll("#okf-graph-canvas line.okf-graph-edge-ghost"), (l) => { const cs = getComputedStyle(l); return [cs.stroke, cs.strokeWidth, cs.strokeDasharray]; }),
        status: document.getElementById("okf-graph-status").textContent };
    });
    const labelOk = (x) => x.title.startsWith("absent: ") && Array.from(x.label).length <= 24 && (Array.from(x.title).length <= 24 || x.label.endsWith(ELLIPSIS));
    const shapeOk = r.ghosts.length === index.ghosts.length && r.ghosts.every((x) => near(x.size, 26, 0.2) && x.fill === rgb(r.white) && x.stroke === rgb(r.ghost) && x.width === "1.6px"
      && /^3(px)?,? ?3/.test(x.dash) && x.labelFill === rgb(r.ghost) && x.labelSize === "11.5px" && labelOk(x));
    const edgesOk = r.edges.length > 0 && r.edges.every(([stroke, width, dash]) => stroke === rgb(r.ghost) && width === "1.4px" && /^5(px)?,? ?4/.test(dash));
    // Dim mode: concepts the filters hide go to 0.25, a ghost never does.
    await p.locator("#okf-facet-type ~ .okf-facet-list input").first().uncheck();
    await p.locator("#okf-facet-display ~ .okf-facet-row input").nth(1).check();
    const dim = await p.evaluate(() => ({ ghosts: Array.from(document.querySelectorAll("#okf-graph-canvas g.okf-graph-ghost"), (g) => getComputedStyle(g).opacity),
      dimmed: document.querySelectorAll("#okf-graph-canvas g.okf-graph-dim").length, dimmedGhosts: document.querySelectorAll("#okf-graph-canvas g.okf-graph-ghost.okf-graph-dim").length }));
    await p.locator("#okf-facet-display ~ .okf-facet-row input").nth(1).uncheck();
    await p.locator("#okf-facet-type ~ .okf-facet-list input").first().check();
    // Selecting a ghost: "absent" and its id, no link, no Open page, no fragment.
    const gi = await p.evaluate(() => Array.from(document.querySelectorAll("#okf-graph-canvas g.okf-node")).findIndex((g) => g.classList.contains("okf-graph-ghost")));
    await shot("graph-ghost", p);
    await clickNode(p, gi);
    const sel = await p.evaluate(() => { const d = document.getElementById("okf-graph-detail"); return { title: d.querySelector("h2").textContent, id: d.querySelector(".okf-graph-detail-id").textContent,
      links: d.querySelectorAll("a").length, open: !!d.querySelector(".okf-graph-open"), hash: location.hash, reading: document.getElementById("okf-reading-view").getAttribute("href") }; });
    const absentRows = await p.evaluate(() => Array.from(document.querySelectorAll("#okf-graph-detail .okf-graph-absent"), (s) => [s.tagName, getComputedStyle(s).color]));
    return { pass: shapeOk && edgesOk && dim.ghosts.length > 0 && dim.ghosts.every((o) => o === "1") && dim.dimmed > 0 && dim.dimmedGhosts === 0
      && sel.title === "absent" && sel.id.length > 0 && sel.links === 0 && !sel.open && (sel.hash === "" || sel.hash === "#"), shapeOk, edgesOk, ghosts: r.ghosts.slice(0, 2), status: r.status, dim, sel, absentRows };
  });

  await check("G18-hostile-fragments", async () => {
    if (!ghostGraph) { return { pass: null, note: "OKF_RECETTE_GHOST is not set" }; }
    const p = await newPage();
    const index = ctx.lib.readIndex(ghostSite.dir);
    const seen = [];
    for (const id of ["__proto__", "constructor", "toString"]) {
      if (!index.concepts.some((c) => c.id === id)) { continue; }
      await p.goto(ghostGraph + "#" + id);
      await waitLayout(p);
      // A fragment change in the same document is followed by hashchange, an
      // event of its own: wait for the drawer, not for the layout.
      await p.waitForFunction((want) => document.querySelector("#okf-graph-detail .okf-graph-detail-id")?.textContent === want, id, { timeout: 5000 }).catch(() => {});
      seen.push({ id, selected: await p.textContent("#okf-graph-detail .okf-graph-detail-id") });
    }
    return { pass: seen.length > 0 && seen.every((s) => s.selected === s.id), seen };
  });

  const listChecks = async (url, farId, label) => {
    // The equivalent list: a concept far down is marked and inside the visible
    // list once rendered, at 1 440 and at 390 px; no content-visibility on its
    // items (P3-css-1). The whole entry, its relation lines included, is in
    // view (in the middle of the list, revision 12), and only the list
    // scrolled, never the page.
    const out = {};
    for (const [name, viewport] of [["1440", { width: 1440, height: 900 }], ["390", { width: 390, height: 844 }]]) {
      const p = await newPage({ viewport });
      await p.goto(url + "#" + farId);
      await waitLayout(p);
      const listed = await p.evaluate(() => !document.getElementById("okf-graph-list").hidden);
      if (!listed) { await p.click("#okf-graph-zoom button.okf-graph-list-toggle"); }
      await p.waitForSelector("#okf-graph-list .okf-graph-list-current");
      await p.waitForTimeout(300);
      out[name] = await p.evaluate(() => {
        const list = document.getElementById("okf-graph-list").getBoundingClientRect();
        const current = document.querySelector("#okf-graph-list .okf-graph-list-current");
        // The marked entry is the button; its item goes on below it with the relations.
        const r0 = current.querySelector("button").getBoundingClientRect();
        const item = document.querySelector("#okf-graph-list .okf-graph-list-item");
        const whole = current.getBoundingClientRect();
        return { inside: r0.top >= list.top - 1 && r0.bottom <= list.bottom + 1,
          whole: whole.height <= list.bottom - list.top ? whole.top >= list.top - 1 && whole.bottom <= list.bottom + 1 : Math.abs(whole.top - list.top) <= 1,
          pageScroll: [window.scrollX, window.scrollY], entry: [whole.top, whole.bottom], current: current.querySelector("button").textContent, aria: current.querySelector("button").getAttribute("aria-current"),
          items: document.querySelectorAll("#okf-graph-list .okf-graph-list-item").length, cv: getComputedStyle(item).contentVisibility, scrolled: document.getElementById("okf-graph-list").scrollTop > 0,
          list: [list.top, list.bottom], at: [r0.top, r0.bottom], pressed: document.querySelector("#okf-graph-zoom .okf-graph-list-toggle").getAttribute("aria-pressed") };
      });
      if (name === "1440") { await shot(label, p); }
    }
    return out;
  };

  await check("P3-css-1", async () => {
    // The OKF4net site (806 nodes) is under the limit: its list is the "List"
    // toggle's. The over-limit site (OKF_RECETTE_BIG) opens on the list itself.
    const ids = ctx.lib.readIndex(ctx.siteDir).concepts.map((c) => c.id);
    const target = ids[ids.length - 5];
    const r = await listChecks(siteGraph, target, "graph-list");
    const ok = (x) => x.inside && x.whole && x.pageScroll[0] === 0 && x.pageScroll[1] === 0 && x.aria === "true" && x.cv === "visible" && x.pressed === "true";
    return { pass: ok(r["1440"]) && ok(r["390"]), target, ...r };
  });

  await check("G9-over-limit", async () => {
    if (!bigGraph) { return { pass: null, note: "OKF_RECETTE_BIG is not set: the OKF4net bundle has 806 concepts, under the 1 500 limit" }; }
    const index = ctx.lib.readIndex(bigSite.dir);
    const p = await newPage();
    await p.goto(bigGraph);
    await p.waitForSelector("#okf-graph-list:not([hidden])");
    // The list is built over several frames (revision 12): it ends whole.
    await p.waitForFunction((n) => document.querySelectorAll("#okf-graph-list .okf-graph-list-item").length >= n, ctx.lib.readIndex(bigSite.dir).concepts.length, { timeout: 30000 });
    const r = await p.evaluate(() => ({
      status: document.getElementById("okf-graph-status").textContent,
      canvasHidden: document.getElementById("okf-graph-canvas").hidden, svg: !!document.querySelector("#okf-graph-canvas svg"),
      list: !document.getElementById("okf-graph-list").hidden, items: document.querySelectorAll("#okf-graph-list .okf-graph-list-item").length,
      listButton: [document.querySelector(".okf-graph-list-toggle").disabled, document.querySelector(".okf-graph-list-toggle").getAttribute("aria-pressed")],
      tools: Array.from(document.querySelectorAll("#okf-graph-zoom button:not(.okf-graph-list-toggle)"), (b) => b.disabled),
      listBox: document.getElementById("okf-graph-list").getBoundingClientRect().height,
    }));
    await shot("graph-over-limit", p);
    // Narrowing the filters below the limit draws the graph.
    await p.locator("#okf-facet-type ~ .okf-facet-list input").first().uncheck();
    await waitLayout(p);
    const drawn = await p.evaluate(() => ({ status: document.getElementById("okf-graph-status").textContent, nodes: document.querySelectorAll("#okf-graph-canvas g.okf-node").length,
      list: !document.getElementById("okf-graph-list").hidden }));
    const n = index.concepts.length;
    const pass = r.status === `${n} concepts match ${EMDASH} narrow the filters to draw the graph` && r.canvasHidden && !r.svg && r.list && r.items === n
      && r.listButton[0] === true && r.tools.every(Boolean) && r.listBox > 100 && drawn.nodes === n - index.types[0].count && !drawn.list && /^showing /.test(drawn.status);
    return { pass, ...r, drawn };
  });

  await check("P3-css-1-over-limit", async () => {
    if (!bigGraph) { return { pass: null, note: "OKF_RECETTE_BIG is not set" }; }
    const ids = ctx.lib.readIndex(bigSite.dir).concepts.map((c) => c.id);
    const r = await listChecks(bigGraph, ids[ids.length - 12], "graph-over-limit-fragment");
    const ok = (x) => x.inside && x.whole && x.pageScroll[0] === 0 && x.pageScroll[1] === 0 && x.aria === "true" && x.cv === "visible" && x.scrolled;
    return { pass: ok(r["1440"]) && ok(r["390"]), ...r };
  });

  await check("P3-css-2", async () => {
    // Folded into G14-keyboard (mouse: no contour; keyboard: contour). Kept as
    // an id of its own so ACCEPTANCE.md's line has a result.
    const p = await newPage();
    await openGraph(acmeGraph, p);
    await clickNode(p, 1);
    const mouse = await p.evaluate(() => { const g = document.activeElement; return { onNode: g.classList.contains("okf-node"), visible: g.matches(":focus-visible"), contour: getComputedStyle(g.querySelector(".okf-node-focus")).visibility }; });
    await p.keyboard.press("ArrowRight");
    await p.keyboard.press("ArrowLeft");
    const keyed = await p.evaluate(() => { const g = document.activeElement; const f = g.querySelector(".okf-node-focus"); const cs = getComputedStyle(f);
      return { onNode: g.classList.contains("okf-node"), visible: g.matches(":focus-visible"), contour: cs.visibility, side: f.getBBox().width, dash: cs.strokeDasharray, stroke: cs.stroke,
        ink: getComputedStyle(document.documentElement).getPropertyValue("--ink").trim() }; });
    return { pass: mouse.onNode && !mouse.visible && mouse.contour === "hidden" && keyed.onNode && keyed.visible && keyed.contour === "visible" && /^3(px)?,? ?3/.test(keyed.dash) && keyed.stroke === rgb(keyed.ink), mouse, keyed };
  });

  await check("P3-errors", async () => {
    const errors = pages.flatMap((p) => p.okfTracked.errors);
    const failed = pages.flatMap((p) => p.okfTracked.failed);
    return { pass: errors.length === 0 && failed.length === 0, errors: errors.slice(0, 5), failed: failed.slice(0, 5) };
  });

  if (ctx.wanted("C14-requests")) {
    // Requests outside both sites (and the optional ones), from every page this
    // recette opened (okfTracked.outside already leaves out data: and about:).
    const own = [ghostSite, bigSite].filter(Boolean).map((s) => s.base);
    const offSite = pages.flatMap((p) => p.okfTracked.outside).filter((u) => !own.some((root) => u.startsWith(root)));
    results["C14-requests"] = { pass: offSite.length === 0, pages: pages.length, offSite: offSite.slice(0, 5) };
  }
  return results;
}

module.exports = { run };
