// SPDX-License-Identifier: LGPL-3.0-or-later
//
// P1.1 recette (spec §11, §12.8): what jsdom cannot see -- fonts really
// loaded, computed sizes and colours, widths, contrasts in both themes,
// requests -- measured in real browsers on the generated sites, opened as
// files. Results are keyed by §11 id (H, E, C, X, J, L) or named (fonts,
// tokens, requests); every id gets a capture at 1440 x 900 named after it, to
// set beside the mockups (A: Main.dc.html; the palette of C: Focus.dc.html).
// A second result for an id that has both a style probe and a behaviour is
// keyed "<id>-<name>" (e.g. H7-H7narrow).
"use strict";

// The seven faces of spec §11.0, A16.
const FACES = [["Inter", 400], ["Inter", 500], ["Inter", 600], ["Inter Tight", 600], ["Inter Tight", 900], ["Space Mono", 400], ["Space Mono", 700]];
// §11.0 light tokens the probes compare with.
const T = { white: "#ffffff", ink: "#101014", blue: "#1a3fd6", blueSoft: "#eef1fd", gray: "#6a6a72", hair: "#e3e3e8", stale: "#b4540a" };
// Mockup A's page, in acme_retail.
const PAGE = "computations/gross-margin-period.html";

// §11.0: on index.html and on the first page of every depth of both sites,
// document.fonts.load must return a non-empty list whose every face is
// "loaded" (not document.fonts.check, which is true when no face matches),
// with no failed request under assets/fonts/.
async function fonts(ctx) {
  const results = [];
  for (const [label, dir, url] of [["okf4net", ctx.siteDir, ctx.site], ["acme", ctx.acmeDir, ctx.acme]]) {
    for (const { rel, depth } of ctx.lib.pagesByDepth(dir)) {
      const page = await ctx.newPage();
      await page.goto(ctx.lib.pageUrl(url, rel), { waitUntil: "load" });
      const faces = await page.evaluate(async (wanted) => {
        const out = [];
        for (const [family, weight] of wanted) {
          try {
            const list = await document.fonts.load(`${weight} 16px "${family}"`);
            out.push({ family, weight, faces: list.length, loaded: list.length > 0 && list.every((f) => f.status === "loaded") });
          } catch (e) {
            out.push({ family, weight, faces: 0, loaded: false, error: String(e) });
          }
        }
        return out;
      }, FACES);
      const failed = page.okfTracked.failed.filter((u) => /\/assets\/fonts\//.test(u));
      results.push({ site: label, page: rel, depth, ok: failed.length === 0 && faces.every((f) => f.loaded), failed, faces: faces.filter((f) => !f.loaded) });
      await page.close();
    }
  }
  return { pass: results.every((r) => r.ok), pages: results.length, failures: results.filter((r) => !r.ok) };
}

// Computed values of [selector, { property: expected }] pairs on `page`.
// expected: a string (exact), a RegExp, or a number (px, within 0.1, since
// Firefox rounds lengths to 1/60 px). The measured values are returned too,
// so a report shows what was read, not only what was missed.
//
// Border widths: their computed value is "snapped as a border width" (CSS
// Values 4): at a device pixel ratio r, a width w >= 1 device px becomes
// floor(w * r) / r, a smaller non-zero one 1 / r. So 1 px reads 0.666667px
// in a browser at r = 1.5 (Playwright's Firefox here), and 1.5 px reads 1px
// at r = 1. The expected value of a *-width of a border, outline or column
// rule is snapped the same way before comparing; the report keeps both.
// The ratio is read from the snapping itself (a 0.25 px border computes to
// 1 / ratio): Playwright's Firefox reports devicePixelRatio 1 while it lays
// out at the screen's 1.5.
function snapBorder(px, ratio) {
  if (!(px > 0)) { return px; }
  const device = px * ratio;
  return device < 1 ? 1 / ratio : Math.floor(device + 1e-3) / ratio;
}
const BORDER_WIDTH = /^(border(-(top|right|bottom|left|block|inline)(-(start|end))?)?|outline|column-rule)-width$/;

async function measure(page, checks) {
  const { ratio, got } = await page.evaluate((list) => ({
    ratio: (() => { const d = document.createElement("div"); d.style.borderTop = "0.25px solid"; document.body.appendChild(d); const w = parseFloat(getComputedStyle(d).borderTopWidth); d.remove(); return w > 0 ? Math.round(100 / w) / 100 : window.devicePixelRatio; })(),
    got: list.map(([sel, props]) => {
      const el = document.querySelector(sel);
      if (!el) { return null; }
      const cs = getComputedStyle(el);
      const out = {};
      for (const p of props) { out[p] = cs.getPropertyValue(p).trim(); }
      return out;
    }),
  }), checks.map(([sel, props]) => [sel, Object.keys(props)]));
  const misses = [];
  const values = {};
  checks.forEach(([sel, props], k) => {
    if (!got[k]) { misses.push(`${sel}: missing`); return; }
    values[sel] = got[k];
    for (const [p, raw] of Object.entries(props)) {
      const value = got[k][p];
      let want = raw;
      if (BORDER_WIDTH.test(p) && (typeof raw === "number" || /^\d*\.?\d+px$/.test(raw))) {
        const snapped = snapBorder(parseFloat(raw), ratio);
        want = snapped === parseFloat(raw) ? raw : snapped;
      }
      const ok = want instanceof RegExp ? want.test(value)
        : typeof want === "number" ? Math.abs(parseFloat(value) - want) <= 0.1
        : value === want;
      if (!ok) { misses.push(`${sel} ${p}: ${value} (expected ${raw}${want !== raw ? `, ${want}px at ratio ${ratio}` : ""})`); }
    }
  });
  return { misses, values, ratio };
}

async function styles(page, checks) {
  return (await measure(page, checks)).misses;
}

function probes(rgb, tocPage) {
  const head = "body > .okf-layout > main > .okf-page-head";
  return [
    ["H1", PAGE, [["body > .topline", { height: "6px", "background-color": rgb(T.blue) }]]],
    ["H2", PAGE, [["body > header.bar .bar-in", { height: "52px", "padding-left": "20px", "padding-right": "20px", "column-gap": "20px" }],
      ["body > header.bar", { "border-bottom-width": "1px", "border-bottom-color": rgb(T.hair) }]]],
    ["H3", PAGE, [["body > header.bar .wordmark", { "font-family": /Inter Tight/, "font-weight": "900", "font-size": "20px", "letter-spacing": /^-0\.4px$/, color: rgb(T.ink) }],
      ["body > header.bar .wordmark sup", { "font-family": /Space Mono/, "font-weight": "700", "font-size": "12px", color: rgb(T.blue) }]]],
    ["H4", PAGE, [["body > header.bar .bar-sep", { width: "1px", height: "20px", "background-color": rgb(T.hair) }]]],
    ["H5", PAGE, [["#okf-bundle-name", { "font-family": /Space Mono/, "font-size": "13px", color: rgb(T.ink), "text-overflow": "ellipsis", "white-space": "nowrap", "min-width": "0px" }]]],
    ["H6", PAGE, [["#okf-bundle-counts", { "font-family": /Space Mono/, "font-size": "13px", color: rgb(T.gray) }]]],
    ["H7", PAGE, [["#okf-tools .okf-palette-open", { width: "320px", height: "34px", color: rgb(T.gray), "font-size": 13.5, "border-top-color": rgb(T.hair) }],
      ["#okf-tools .okf-palette-hint", { "font-family": /Space Mono/, "font-size": "11px", "border-top-color": rgb(T.hair), "padding-top": "1px", "padding-left": "6px" }]]],
    ["H8", PAGE, [["#okf-filters-toggle", { height: "34px", "padding-left": "14px", "padding-right": "14px", "column-gap": "8px", "font-weight": "500", "font-size": 13.5, "border-top-color": rgb(T.hair) }]]],
    ["H9", PAGE, [["#okf-global-graph", { height: "34px", "border-top-color": rgb(T.blue), "background-color": rgb(T.blueSoft), color: rgb(T.blue), "font-weight": "600", "font-size": 13.5 }]]],
    ["H11", PAGE, [["#okf-theme-toggle", { width: "34px", height: "34px", "border-top-color": rgb(T.hair) }], ["#okf-theme-toggle path", { stroke: rgb(T.ink), fill: "none", "stroke-width": "1.5px" }]]],
    ["E1", PAGE, [["#okf-explorer", { width: "290px", "border-right-color": rgb(T.hair), "border-right-width": "1px" }]]],
    ["E2", PAGE, [["#okf-explorer .okf-explorer-head", { "padding-top": "14px", "padding-left": "16px", "padding-right": "16px", "padding-bottom": "10px" }],
      ["#okf-explorer .okf-explorer-title", { "font-family": /Space Mono/, "font-size": "11px", "text-transform": "uppercase", "margin-bottom": "6px", color: rgb(T.gray) }]]],
    ["E3", PAGE, [["#okf-tree-filter", { height: "34px", "font-size": 13.5, "padding-left": "10px", "border-top-color": rgb(T.hair) }]]],
    ["E4", PAGE, [["#okf-explorer .okf-type-chips", { "column-gap": "6px", "margin-top": "10px", "flex-wrap": "wrap" }],
      ["#okf-explorer .okf-type-chips .okf-chip", { height: "26px", "padding-left": "10px", "column-gap": "7px", "font-size": "12px", color: rgb(T.gray), "border-top-color": rgb(T.hair), "background-color": rgb(T.white) }]]],
    ["E5", PAGE, [["#okf-explorer .okf-tree", { "border-top-color": rgb(T.hair), "padding-top": "6px", "padding-bottom": "6px" }]]],
    ["E6", PAGE, [["#okf-explorer .okf-tree-row", { height: "30px", "column-gap": "9px", "padding-right": "14px", "padding-left": "12px", "border-left-width": "3px" }],
      ["#okf-explorer .okf-tree-children .okf-tree-row", { "padding-left": "30px" }]]],
    ["E7", PAGE, [["#okf-explorer .okf-tree-toggle", { width: "12px", height: "30px" }]]],
    ["E8", PAGE, [["#okf-explorer .okf-tree-folder", { "font-size": 13.5, "font-weight": "600", color: rgb(T.ink) }],
      ["#okf-explorer .okf-tree-link:not([aria-current])", { "font-size": 13.5, "font-weight": "400", color: rgb(T.ink), "text-overflow": "ellipsis" }]]],
    ["E9", PAGE, [["#okf-explorer .okf-tree-count", { "font-family": /Space Mono/, "font-size": "11px", color: rgb(T.gray) }]]],
    ["E10", PAGE, [["#okf-explorer .okf-flag-trust", { width: "10px", height: "10px" }]]],
    ["E11", PAGE, [["#okf-explorer .okf-tree-row.okf-tree-current", { "background-color": rgb(T.blueSoft), "border-left-color": rgb(T.blue) }],
      ['#okf-explorer a.okf-tree-link[aria-current="page"]', { color: rgb(T.blue), "font-weight": "600" }]]],
    ["E12", PAGE, [["#okf-explorer .okf-explorer-foot", { "padding-top": "12px", "padding-left": "16px", "border-top-color": rgb(T.hair) }],
      ["#okf-explorer .okf-explorer-foot .okf-legend", { "font-size": "12px", color: rgb(T.gray), "row-gap": "7px" }],
      ["#okf-explorer .okf-explorer-foot .okf-legend li", { "column-gap": "8px" }]]],
    ["C1", PAGE, [["body > .okf-layout > main", { "padding-top": "26px", "padding-left": "48px", "padding-right": "48px" }], [head, { "max-width": "720px" }]]],
    ["C2", PAGE, [[`${head} .okf-crumbs ol`, { "font-family": /Space Mono/, "font-size": "12.5px", color: rgb(T.gray), "column-gap": "8px" }],
      [`${head} .okf-crumbs [aria-current="page"]`, { color: rgb(T.ink) }]]],
    ["C3", PAGE, [[`${head} h1`, { "font-family": /Inter Tight/, "font-weight": "600", "font-size": "34px", "line-height": 39.1, "letter-spacing": /^-0\.68px$/, "margin-top": "14px", "margin-bottom": "6px" }]]],
    ["C5", PAGE, [[`${head} .okf-chips`, { "column-gap": "8px", "margin-bottom": "18px" }],
      [`${head} .okf-chip-type`, { height: "26px", "padding-left": "10px", "column-gap": "7px", "background-color": rgb(T.ink), color: rgb(T.white), "font-weight": "600", "font-size": "12px" }],
      [`${head} .okf-chip-status`, { "font-family": /Space Mono/, "border-top-color": rgb(T.hair) }],
      [`${head} .okf-chip-trust`, { "border-top-color": rgb(T.blue), color: rgb(T.blue) }],
      [`${head} .okf-chip-stale`, { "border-top-color": rgb(T.hair), color: rgb(T.gray) }]]],
    ["C6", PAGE, [["#okf-fm", { "border-top-color": rgb(T.hair), "margin-bottom": "22px" }],
      ["#okf-fm .okf-fm-head", { "background-color": rgb(T.blueSoft), "padding-top": "9px", "padding-left": "14px", "border-bottom-color": rgb(T.hair) }],
      ["#okf-fm .okf-fm-grid", { "font-size": 13.5 }],
      ["#okf-fm .okf-fm-cell", { "column-gap": "12px", "padding-top": "8px", "padding-left": "14px" }],
      ["#okf-fm .okf-fm-key", { width: "92px", "font-family": /Space Mono/, "font-size": "12px", color: rgb(T.gray) }],
      ["#okf-fm .okf-fm-struct", { "font-family": /Space Mono/, "font-size": "12.5px", "white-space": "pre-wrap" }],
      ["#okf-fm .okf-fm-toggle", { "font-size": "12.5px", "font-weight": "600", color: rgb(T.blue), "border-top-width": "0px" }]]],
    ["C7", PAGE, [["#okf-body p", { "font-size": "15.5px", "line-height": 25.575, "margin-bottom": "20px", "font-family": /^"?Inter"?,/ }],
      ["#okf-body pre", { "background-color": rgb(T.blueSoft), "font-size": "12.5px", "padding-top": "16px", "line-height": "20px", "font-family": /Space Mono/ }],
      ["#okf-body a", { color: rgb(T.blue) }]]],
    ["C7-h2", tocPage, [["#okf-body h2", { "font-family": /Inter Tight/, "font-size": "21px", "font-weight": "600", "margin-bottom": "8px" }]]],
    ["X1", PAGE, [["#okf-context", { width: "340px", "padding-top": "20px", "padding-left": "20px", "padding-right": "20px", "row-gap": "22px", "border-left-color": rgb(T.hair) }]]],
    ["X3", tocPage, [["#okf-toc a:not([aria-current])", { "font-size": 13.5, color: rgb(T.ink), "border-left-width": "2px", "border-left-color": rgb(T.hair), "padding-left": "10px", "padding-top": "5px", "text-decoration-line": "none" }]]],
    ["X10", PAGE, [["#okf-context .okf-backlinks a.okf-row", { display: "flex", "column-gap": "9px", "font-size": 13.5, "padding-top": "6px", "border-bottom-color": rgb(T.hair), "text-decoration-line": "none" }]]],
  ];
}

// The page of a probe: PAGE is in acme_retail, every other one in OKF4net.
const urlOf = (ctx, rel) => rel === PAGE ? ctx.lib.pageUrl(ctx.acme, rel) : ctx.lib.pageUrl(ctx.site, rel);

async function probe(ctx, id, url, checks) {
  const page = await ctx.newPage();
  await page.goto(url);
  const { misses, values, ratio } = await measure(page, checks);
  await ctx.shot(page, id);
  await page.close();
  return { pass: misses.length === 0, misses, ratio, values };
}

// X4's reading of the contents list: the entry the 25 % rule (or the
// bottom of the page) designates, read from the headings' own boxes, and the
// one okf-toc.js marks.
function tocState(page) {
  return page.evaluate(() => {
    const links = Array.from(document.querySelectorAll("#okf-toc a"));
    const tops = links.map((a) => document.getElementById(a.getAttribute("href").slice(1)).getBoundingClientRect().top);
    const atBottom = scrollY + innerHeight >= document.documentElement.scrollHeight - 1;
    let expected = 0;
    tops.forEach((t, i) => { if (t <= innerHeight * 0.25) { expected = i; } });
    if (atBottom) { expected = links.length - 1; }
    const current = links.findIndex((a) => a.getAttribute("aria-current") === "location");
    const cs = current >= 0 ? getComputedStyle(links[current]) : null;
    return { atBottom, expected, current, currents: links.filter((a) => a.hasAttribute("aria-current")).length, n: links.length, color: cs && cs.color, rule: cs && cs.borderLeftColor, weight: cs && cs.fontWeight };
  });
}

// Behaviours and geometry the table above cannot express.
const specials = {
  async H2full(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const w = await page.evaluate(() => [document.querySelector("body > header.bar .bar-in").getBoundingClientRect().width, innerWidth]);
    return { pass: Math.abs(w[0] - w[1]) <= 1, widths: w };
  },
  // H3: the brand links to index.html (relative to the page's depth).
  async H3link(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const r = await page.evaluate(() => { const a = document.querySelector("body > header.bar a.wordmark"); return { href: a.getAttribute("href"), text: a.textContent, sup: a.querySelector("sup") && a.querySelector("sup").textContent }; });
    return { pass: r.href === "../index.html" && r.text === "OKF4net§" && r.sup === "§", ...r };
  },
  // H6: "N concepts · M links", N and M read from the index.
  async H6text(ctx) {
    const idx = ctx.lib.readIndex(ctx.acmeDir);
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const text = await page.evaluate(() => document.getElementById("okf-bundle-counts").textContent);
    const want = `${idx.concepts.length} concepts · ${idx.edges.length} links`;
    return { pass: text === want, text, want };
  },
  // H7: 320 wide with its hint at 900 and above; the hint goes under 900
  // (Firefox's 899.333 included), the button never under 160.
  async H7narrow(ctx) {
    const out = {};
    for (const width of [900, 899, 390]) {
      const page = await ctx.newPage({ viewport: { width, height: 900 } });
      await page.goto(urlOf(ctx, PAGE));
      out[width] = await page.evaluate(() => {
        const b = document.querySelector("#okf-tools .okf-palette-open");
        return { width: b.getBoundingClientRect().width, hint: getComputedStyle(b.querySelector(".okf-palette-hint")).display, innerWidth: window.innerWidth, vw: document.documentElement.clientWidth };
      });
    }
    return { pass: out[900].hint !== "none" && out[899].hint === "none" && out[390].hint === "none" && Object.values(out).every((v) => v.width >= 159.5 && v.width <= 320.5), ...out };
  },
  async H8count(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const atRest = await page.evaluate(() => document.querySelector("#okf-filters-toggle .okf-filters-count").hidden);
    await page.click("#okf-explorer .okf-type-chips button.okf-chip");
    await page.fill("#okf-tree-filter", "margin");
    const misses = await styles(page, [["#okf-filters-toggle .okf-filters-count", { display: /^(inline|block|inline-block)$/, "background-color": ctx.lib.rgb(T.blue), color: ctx.lib.rgb(T.white), "font-family": /Space Mono/, "font-size": "11px", "padding-left": "6px" }]]);
    const r = await page.evaluate(() => { const b = document.getElementById("okf-filters-toggle"); return { count: b.querySelector(".okf-filters-count").textContent, label: b.getAttribute("aria-label"), hiddenToAT: b.querySelector(".okf-filters-count").getAttribute("aria-hidden") }; });
    await ctx.shot(page, "H8");
    return { pass: atRest && misses.length === 0 && r.count === "2" && r.label === "Filters, 2 active" && r.hiddenToAT === "true", atRest, misses, ...r };
  },
  // H9: a link drawn as a button, before the theme; graph.html is P3's, so
  // the link is not followed (dead until P3): its markup is what is checked.
  async H9link(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const r = await page.evaluate(() => {
      const a = document.getElementById("okf-global-graph");
      const tools = Array.from(document.getElementById("okf-tools").children).map((e) => e.id || e.className);
      return { tag: a.tagName, href: a.getAttribute("href"), current: a.getAttribute("aria-current"), text: a.textContent, tools };
    });
    return { pass: r.tag === "A" && r.href === "../graph.html#computations/gross-margin-period" && r.current === null && r.text === "Global graph" && /okf-palette-open/.test(r.tools[0]) && r.tools.slice(-1)[0] === "okf-theme-toggle", ...r };
  },
  // H11: pressed = border and stroke blue.
  async H11pressed(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    await page.click("#okf-theme-toggle");
    const r = await page.evaluate(() => {
      const b = document.getElementById("okf-theme-toggle");
      const blue = getComputedStyle(document.documentElement).getPropertyValue("--blue").trim();
      return { pressed: b.getAttribute("aria-pressed"), name: b.getAttribute("aria-label") || b.textContent.trim(), border: getComputedStyle(b).borderTopColor, stroke: getComputedStyle(b.querySelector("path")).stroke, blue };
    });
    await page.evaluate(() => { try { localStorage.removeItem("okf-theme"); } catch (e) { /* none */ } });
    const want = ctx.lib.rgb(r.blue);
    return { pass: r.pressed === "true" && /Dark theme/.test(r.name) && r.border === want && r.stroke === want, ...r };
  },
  async H12(ctx) {
    // WebKit (Safari) does not Tab to links by default: Option+Tab does, or a
    // system setting. The viewer cannot change that, and a link that never
    // takes the Tab focus is the browser's choice, not a defect of the skip link.
    if (ctx.browserName === "webkit") {
      return { pass: null, note: "WebKit does not Tab to links by default (Option+Tab does): the skip link is not reachable by plain Tab in Safari whatever the page does; checked in Chrome, Edge and Firefox" };
    }
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const before = await page.evaluate(() => document.querySelector("body > a.okf-skip").getBoundingClientRect().bottom);
    await page.keyboard.press("Tab");
    const r = await page.evaluate(() => { const a = document.activeElement; return { skip: a.matches("body > a.okf-skip"), top: a.getBoundingClientRect().top, href: a.getAttribute("href") }; });
    await ctx.shot(page, "H12");
    await page.keyboard.press("Enter");
    await page.keyboard.press("Tab");
    const next = await page.evaluate(() => !!document.activeElement.closest("main"));
    return { pass: before <= 0 && r.skip && r.top >= 0 && r.href === "#okf-main" && next, hiddenAtRest: before <= 0, ...r, nextInMain: next };
  },
  // H13: one 52 px line from 900; under 900 two lines (brand, name, counts;
  // then the tools, whose own row may wrap: controller ruling); counts gone
  // at 520 and less; never a sideways scroll.
  async H13(ctx) {
    const out = {};
    for (const width of [900, 899, 521, 520, 390]) {
      const page = await ctx.newPage({ viewport: { width, height: 844 } });
      await page.goto(urlOf(ctx, PAGE));
      out[width] = await page.evaluate(() => {
        const r = (sel) => document.querySelector(sel).getBoundingClientRect();
        const brand = r("body > header.bar .wordmark");
        const name = r("#okf-bundle-name");
        const tools = Array.from(document.querySelectorAll("#okf-tools > *")).filter((e) => e.getBoundingClientRect().height > 0).map((e) => e.getBoundingClientRect());
        const rows = new Set(tools.map((t) => Math.round(t.top))).size;
        const countsEl = document.getElementById("okf-bundle-counts");
        const counts = countsEl.getBoundingClientRect();
        const mid = (b) => b.top + b.height / 2;
        return {
          barHeight: r("body > header.bar .bar-in").height,
          counts: getComputedStyle(countsEl).display,
          brandLine: Math.abs(mid(brand) - mid(name)) <= 2 && (counts.height === 0 || Math.abs(mid(counts) - mid(name)) <= 2),
          toolsBelow: tools.every((t) => t.top >= brand.bottom),
          toolsBeside: tools.every((t) => Math.abs(mid(t) - mid(brand)) <= 2),
          toolRows: rows,
          scrollWidth: document.documentElement.scrollWidth,
          clientWidth: document.documentElement.clientWidth,
        };
      });
      await ctx.shot(page, `H13-${width}`);
    }
    const noScroll = Object.values(out).every((v) => v.scrollWidth <= v.clientWidth);
    return {
      pass: noScroll && Math.abs(out[900].barHeight - 52) <= 0.5 && out[900].toolsBeside
        && [899, 521, 520, 390].every((w) => out[w].barHeight > 52 && out[w].brandLine && out[w].toolsBelow)
        && out[521].counts !== "none" && out[520].counts === "none" && out[390].counts === "none",
      ...out,
    };
  },
  // E6/E8: a row's label is the last id segment, the concept title its
  // tooltip and its id in data-okf-id (A22).
  async E8label(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const r = await page.evaluate(() => {
      const a = document.querySelector('#okf-explorer a.okf-tree-link[aria-current="page"]');
      return { text: a.textContent, title: a.getAttribute("title"), id: a.getAttribute("data-okf-id"), h1: document.querySelector("main h1").textContent };
    });
    return { pass: r.text === "gross-margin-period" && r.title === r.h1 && r.id === "computations/gross-margin-period", ...r };
  },
  // E7: the CSS chevron, 6 x 6, 1.5 strokes in gray, turned by 90 degrees
  // between closed and open.
  async E7chevron(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const r = await page.evaluate(() => {
      const pick = (state) => {
        const t = document.querySelector(`#okf-explorer .okf-tree-toggle[aria-expanded="${state}"]`);
        if (!t) { return null; }
        const cs = getComputedStyle(t, "::before");
        return { width: cs.width, height: cs.height, border: cs.borderRightWidth, color: cs.borderRightColor, transform: cs.transform };
      };
      return { open: pick("true"), closed: pick("false"), ratio: (() => { const d = document.createElement("div"); d.style.borderTop = "0.25px solid"; document.body.appendChild(d); const w = parseFloat(getComputedStyle(d).borderTopWidth); d.remove(); return w > 0 ? Math.round(100 / w) / 100 : window.devicePixelRatio; })() };
    });
    const gray = ctx.lib.rgb(T.gray);
    // 1.5 px snapped as a border width (see measure): 1 px at ratio 1.
    const stroke = snapBorder(1.5, r.ratio);
    const ok = (c) => c && c.width === "6px" && c.height === "6px" && Math.abs(parseFloat(c.border) - stroke) <= 0.1 && c.color === gray;
    return { pass: ok(r.open) && ok(r.closed) && r.open.transform !== r.closed.transform, ...r };
  },
  // E10: trust flags from the index (no glyph for unverified: A1, S3), the
  // human dot 8 px in --blue, stale flags shown only while stale now.
  async E10flags(ctx) {
    const idx = ctx.lib.readIndex(ctx.acmeDir);
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const rows = await page.evaluate(() => Array.from(document.querySelectorAll("#okf-explorer a.okf-tree-link")).map((a) => {
      const row = a.parentElement;
      const trust = row.querySelector(".okf-flag-trust");
      const shape = trust && trust.querySelector("svg .okf-trust-human, svg .okf-trust-machine");
      const box = shape && shape.getBoundingClientRect();
      const stale = row.querySelector(".okf-flag-stale");
      return { id: a.getAttribute("data-okf-id"), trust: trust ? trust.getAttribute("title") : null, sr: trust ? trust.querySelector(".okf-sr").textContent : null, fill: shape ? getComputedStyle(shape).fill : null, size: box ? [box.width, box.height] : null, stale: stale ? !stale.hidden : null };
    }));
    const now = Date.now();
    const bad = [];
    for (const r of rows) {
      const c = idx.concepts.find((x) => x.id === r.id);
      if (c.trust === "unverified" ? r.trust !== null : (r.trust !== c.trust || r.sr !== c.trust)) { bad.push(`${r.id}: trust ${r.trust} for ${c.trust}`); }
      // Size: only on rows on screen (a collapsed folder's rows have no box).
      if (c.trust === "human-reviewed" && (r.fill !== ctx.lib.rgb(T.blue) || !r.size || (r.size[0] > 0 && Math.abs(r.size[0] - 8) > 0.5))) { bad.push(`${r.id}: human dot ${r.fill} ${r.size}`); }
      const staleNow = typeof c.staleAfterMs === "number" && c.staleAfterMs <= now;
      if (typeof c.staleAfterMs === "number" ? r.stale !== staleNow : r.stale !== null) { bad.push(`${r.id}: stale flag ${r.stale}`); }
    }
    const measured = rows.filter((r) => r.size && r.size[0] > 0).length;
    return { pass: rows.length === idx.concepts.length && measured > 0 && bad.length === 0, rows: rows.length, measured, bad };
  },
  // E13: the head (chips included) stays at the top of the explorer while
  // the explorer scrolls, and the foot at its bottom. The far page of
  // OKF4net (the last in index order) opens a long branch, so its explorer
  // scrolls at 1440 x 900; the page itself is scrolled past the header first,
  // so the explorer's whole box is on screen. Measured in the MIDDLE of the
  // explorer's scroll and at its end: at the end alone, a foot that is not
  // sticky also sits at the bottom (it is the last thing in the content).
  async E13(ctx) {
    const idx = ctx.lib.readIndex(ctx.siteDir);
    const far = idx.concepts[idx.concepts.length - 1].path;
    const page = await ctx.newPage();
    await page.goto(ctx.lib.pageUrl(ctx.site, far));
    const r = await page.evaluate(() => {
      window.scrollTo(0, 200);
      const nav = document.getElementById("okf-explorer");
      const measure = (scrollTop) => {
        nav.scrollTop = scrollTop;
        const n = nav.getBoundingClientRect();
        const h = nav.querySelector(".okf-explorer-head").getBoundingClientRect();
        const f = nav.querySelector(".okf-explorer-foot").getBoundingClientRect();
        return { scrolled: nav.scrollTop, headTop: h.top - Math.max(0, n.top), footBottom: Math.min(innerHeight, n.bottom) - f.bottom };
      };
      const room = nav.scrollHeight - nav.clientHeight;
      const middle = measure(Math.floor(room / 2));
      const end = measure(nav.scrollHeight);
      return { room, middle, end, navTop: nav.getBoundingClientRect().top, chipsInHead: !!nav.querySelector(".okf-explorer-head .okf-type-chips") };
    });
    await ctx.shot(page, "E13");
    const stuck = (m) => m.scrolled > 0 && Math.abs(m.headTop) <= 1 && Math.abs(m.footBottom) <= 1;
    // The middle needs room to scroll on both sides of it.
    return { pass: r.room > 2 && r.middle.scrolled > 0 && r.middle.scrolled < r.end.scrolled && stuck(r.middle) && stuck(r.end) && r.chipsInHead, ...r };
  },
  // E12: the legend is "stuck at the bottom of the explorer" in the wide
  // layout; as a page opens (not scrolled) at 1440 x 900, the legend must be
  // on screen whole: on acme's page, on the deepest and on the far page of
  // OKF4net.
  async E12foot(ctx) {
    const idx = ctx.lib.readIndex(ctx.siteDir);
    const pages = [ctx.lib.pageUrl(ctx.acme, PAGE), ctx.lib.pageUrl(ctx.site, ctx.lib.pagesByDepth(ctx.siteDir).slice(-1)[0].rel), ctx.lib.pageUrl(ctx.site, idx.concepts[idx.concepts.length - 1].path)];
    const out = [];
    const page = await ctx.newPage();
    for (const url of pages) {
      await page.goto(url);
      out.push(await page.evaluate(() => {
        const nav = document.getElementById("okf-explorer");
        const n = nav.getBoundingClientRect();
        const f = nav.querySelector(".okf-explorer-foot").getBoundingClientRect();
        return { page: location.pathname.split("/").slice(-2).join("/"), navTop: n.top, navBottom: n.bottom, footTop: f.top, footBottom: f.bottom, innerHeight, scrolls: nav.scrollHeight > nav.clientHeight };
      }));
      await ctx.shot(page, `E12-foot-${out.length}`);
    }
    // The same on the far page in the smaller windows a laptop gives (the
    // legend must still end inside the window as the page opens) ...
    const probe = () => page.evaluate(() => {
      const nav = document.getElementById("okf-explorer");
      const f = nav.querySelector(".okf-explorer-foot").getBoundingClientRect();
      return { scrollY: Math.round(scrollY), navTop: nav.getBoundingClientRect().top, footBottom: f.bottom, innerHeight };
    });
    const sizes = [];
    for (const [width, height] of [[1366, 768], [1100, 700]]) {
      const small = await ctx.newPage({ viewport: { width, height } });
      await small.goto(pages[2]);
      const r = await small.evaluate(() => {
        const f = document.querySelector("#okf-explorer .okf-explorer-foot").getBoundingClientRect();
        return { footBottom: f.bottom, innerHeight };
      });
      sizes.push({ width, height, ...r });
    }
    // ... and scrolled: on a long page the panel sticks at the top and the
    // legend stays whole at every scroll position (top, past the header, bottom).
    const long = ctx.lib.pageWithSections(ctx.siteDir, 3);
    await page.goto(ctx.lib.pageUrl(ctx.site, long.rel || long));
    const scrolled = [];
    for (const y of [0, 30, 59, 400, 1e6]) {
      await page.evaluate((to) => window.scrollTo(0, to), y);
      await page.waitForTimeout(80);
      scrolled.push(await probe());
    }
    await ctx.shot(page, "E12-foot-scrolled");
    const whole = (v) => v.footBottom <= v.innerHeight + 0.5;
    return { pass: out.every(whole) && sizes.every(whole) && scrolled.every(whole), pages: out, sizes, scrolled };
  },
  // C4: a body whose first line repeats the title in an ATX h1 loses that
  // line; seen in the browser as: no page's body starts with an h1 equal to
  // the page's own h1 (every depth of OKF4net, whose generated concepts open
  // on "# <title>", and every acme page).
  async C4(ctx) {
    const pages = ctx.lib.pagesByDepth(ctx.siteDir).filter((p) => p.depth > 0).map((p) => ctx.lib.pageUrl(ctx.site, p.rel))
      .concat(ctx.lib.readIndex(ctx.acmeDir).concepts.map((c) => ctx.lib.pageUrl(ctx.acme, c.path)));
    const page = await ctx.newPage();
    const doubled = [];
    for (const url of pages) {
      await page.goto(url);
      const r = await page.evaluate(() => {
        const norm = (s) => s.replace(/\s+/g, " ").trim();
        const first = document.querySelector("#okf-body > :first-child");
        return { title: norm(document.querySelector("main > .okf-page-head h1").textContent), first: first ? first.tagName + " " + norm(first.textContent) : null };
      });
      if (r.first === "H1 " + r.title) { doubled.push(url.split("/").slice(-2).join("/")); }
    }
    return { pass: doubled.length === 0, pages: pages.length, doubled };
  },
  async C6fold(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const visible = () => page.evaluate(() => Array.from(document.querySelectorAll("#okf-fm .okf-fm-cell")).filter((c) => getComputedStyle(c).display !== "none").length);
    const folded = await visible();
    const head = await page.evaluate(() => { const t = document.querySelector("#okf-fm .okf-fm-toggle"); return { title: document.getElementById("okf-fm-title").textContent, toggle: t.textContent, expanded: t.getAttribute("aria-expanded"), controls: t.getAttribute("aria-controls") }; });
    await page.click("#okf-fm .okf-fm-toggle");
    const all = await page.evaluate(() => document.querySelectorAll("#okf-fm .okf-fm-cell").length);
    const expanded = await visible();
    const after = await page.evaluate(() => { const t = document.querySelector("#okf-fm .okf-fm-toggle"); return { toggle: t.textContent, expanded: t.getAttribute("aria-expanded") }; });
    await ctx.shot(page, "C6-expanded");
    return { pass: folded === 4 && expanded === all && all > 4 && head.title === `Frontmatter · ${all} fields` && head.toggle === "Show all" && head.expanded === "false" && !!head.controls && after.toggle === "Show fewer" && after.expanded === "true", folded, expanded, all, head, after };
  },
  // C6: the head is padding 9 14 around one line, its title and "Show all"
  // on the same centre line, as in A (no title margin inside the head).
  async C6head(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const r = await page.evaluate(() => {
      const box = (el) => el.getBoundingClientRect();
      const head = box(document.querySelector("#okf-fm .okf-fm-head"));
      const title = box(document.getElementById("okf-fm-title"));
      const toggle = box(document.querySelector("#okf-fm .okf-fm-toggle"));
      const mid = (b) => b.top + b.height / 2;
      return { headHeight: head.height, titleMid: mid(title) - head.top, toggleMid: mid(toggle) - head.top, titleBottomGap: head.bottom - title.bottom, titleMargin: getComputedStyle(document.getElementById("okf-fm-title")).marginBottom };
    });
    return { pass: Math.abs(r.titleMid - r.toggleMid) <= 1, ...r };
  },
  async C8(ctx) {
    const page = await ctx.newPage();
    await page.goto(ctx.lib.pageUrl(ctx.acme, "index.html"));
    const misses = await styles(page, [["body > .okf-layout > main > .meta", { "font-family": /Space Mono/, "font-size": "13px" }], ["#okf-bundle-name", { "font-size": "13px" }], ["body > header.bar .bar-in", { height: "52px" }]]);
    await ctx.shot(page, "C8");
    return { pass: misses.length === 0, misses };
  },
  // X4: current = the last body H2/H3 whose top is above 25 % of the window
  // height, else the first; at the bottom of the page, the last. Measured
  // with the second heading brought to 10 % of the window: the expected
  // entry is read from the headings' own boxes after the scroll (a short
  // section can put a third heading above 25 % too). The sections of the
  // generated OKF4net pages are short and close to the page's end, so the
  // second heading cannot reach 10 % of a 900 px window: the window height
  // is lowered (900, 600, 420, 300, 240) until the middle state is neither
  // the bottom of the page nor the first or last entry; none = fail.
  async X4(ctx, tocPage) {
    let page = null;
    let middle = null;
    const tried = [];
    for (const height of [900, 600, 420, 300, 240]) {
      page = await ctx.newPage({ viewport: { width: 1440, height } });
      await page.goto(ctx.lib.pageUrl(ctx.site, tocPage));
      await page.evaluate(() => {
        const second = document.querySelectorAll("#okf-toc a")[1].getAttribute("href").slice(1);
        const h = document.getElementById(second);
        window.scrollTo(0, h.getBoundingClientRect().top + scrollY - innerHeight * 0.1);
      });
      await page.waitForTimeout(150);
      middle = await tocState(page);
      tried.push(height);
      if (!middle.atBottom && middle.expected >= 1 && middle.expected < middle.n - 1) { break; }
      await page.close();
      page = null;
    }
    if (!page) { return { pass: false, note: "no window height gives a middle state", tried, middle }; }
    await ctx.shot(page, "X4");
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    await page.waitForTimeout(150);
    const bottom = await tocState(page);
    const blue = ctx.lib.rgb(T.blue);
    return { pass: middle.current === middle.expected && middle.currents === 1 && middle.color === blue && middle.rule === blue && middle.weight === "600" && bottom.atBottom && bottom.current === bottom.n - 1, height: tried.slice(-1)[0], middle, bottom };
  },
  // X10: "Referenced by · N", N written in C#, one type glyph per row.
  async X10count(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const r = await page.evaluate(() => {
      const rows = Array.from(document.querySelectorAll("#okf-context .okf-backlinks a.okf-row"));
      return { title: document.getElementById("okf-backlinks-title").textContent, rows: rows.length, glyphs: rows.filter((a) => a.querySelector("svg.okf-glyph")).length };
    });
    return { pass: r.title === `Referenced by · ${r.rows}` && r.rows > 0 && r.glyphs === r.rows, ...r };
  },
  async X11(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const r = await page.evaluate(() => {
      const chips = Array.from(document.querySelectorAll("#okf-explorer .okf-type-chips button.okf-chip"));
      return { chips: chips.length, titled: chips.filter((c) => c.querySelector("svg > title") && c.querySelector("svg > title").textContent === c.querySelector(".okf-chip-text").textContent).length };
    });
    return { pass: r.chips > 0 && r.titled === r.chips, ...r };
  },
  // J1-J6 on one open palette (query "o"), each id its own result.
  async J(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    await page.keyboard.press("Control+k");
    await page.keyboard.type("o");
    const rgb = ctx.lib.rgb;
    const geo = await page.evaluate(() => {
      const r = document.querySelector(".okf-palette").getBoundingClientRect();
      const option = document.querySelector(".okf-palette-option").getBoundingClientRect();
      const search = document.querySelector(".okf-palette-search").getBoundingClientRect();
      const close = document.querySelector(".okf-palette-close");
      const matches = document.querySelector(".okf-palette-matches");
      const status = document.querySelector(".okf-palette [role=status]");
      const n = document.querySelectorAll("[role=option]").length;
      const foot = Array.from(document.querySelectorAll(".okf-palette-foot > *"), (e) => e.textContent);
      const gap = document.querySelector(".okf-palette-list").getBoundingClientRect().top - matches.getBoundingClientRect().bottom;
      return { top: r.top, width: r.width, optionHeight: option.height, search: [search.width, search.height], closeName: close.getAttribute("aria-label"), closeText: close.textContent, matches: matches.textContent, matchesHidden: matches.getAttribute("aria-hidden"), status: status.textContent, statusWidth: status.getBoundingClientRect().width, n, foot, gap };
    });
    const sel = '.okf-palette-option[aria-selected="true"]';
    const ids = {
      J1: [[".okf-palette", { "box-shadow": /rgba?\(16, 16, 20, 0\.28\).*18px 50px|18px 50px.*rgba?\(16, 16, 20, 0\.28\)/, "background-color": rgb(T.white), "border-top-color": rgb(T.ink), "border-top-width": "1px" }],
        ["body > .okf-palette-backdrop", { "background-color": /rgba\(16, 16, 20, 0\.34\)/ }]],
      J2: [[".okf-palette-head", { height: "54px", "padding-left": "16px", "column-gap": "12px", "border-bottom-color": rgb(T.hair) }],
        [".okf-palette-search", { stroke: rgb(T.gray) }],
        [".okf-palette-search circle", { "stroke-width": "1.6px" }],
        [".okf-palette-input", { "font-size": "17px", "border-top-width": "0px" }],
        [".okf-palette-close", { "font-family": /Space Mono/, "font-size": "11px", color: rgb(T.gray), "border-top-color": rgb(T.hair), "padding-top": "1px", "padding-left": "6px" }]],
      J3: [[".okf-palette-matches", { "text-transform": "uppercase", "font-family": /Space Mono/, "font-size": "11px", "padding-top": "10px", "padding-left": "16px", "padding-bottom": "6px", color: rgb(T.gray) }]],
      J4: [[".okf-palette-list", { "padding-bottom": "8px" }],
        ['.okf-palette-option[aria-selected="false"]', { "padding-left": "16px", "column-gap": "12px", "border-left-width": "3px", "border-left-color": "rgba(0, 0, 0, 0)" }],
        ['.okf-palette-option[aria-selected="false"] .okf-palette-title', { "font-size": "15px", color: rgb(T.ink) }],
        [".okf-palette-id", { "font-family": /Space Mono/, "font-size": "12px", color: rgb(T.gray) }]],
      J5: [[sel, { "background-color": rgb(T.blueSoft), "border-left-color": rgb(T.blue) }],
        [`${sel} .okf-palette-title`, { color: rgb(T.blue), "font-weight": "600", "font-size": "15px" }]],
      J6: [[".okf-palette-foot", { "font-size": "12px", "column-gap": "16px", color: rgb(T.gray), "padding-top": "10px", "padding-left": "16px", "border-top-color": rgb(T.hair) }]],
    };
    const extra = {
      J1: { ok: Math.abs(geo.top - 120) <= 1 && Math.abs(geo.width - 600) <= 1, top: geo.top, width: geo.width },
      J2: { ok: Math.abs(geo.search[0] - 18) <= 0.5 && geo.closeName === "Close" && geo.closeText === "Esc", search: geo.search, closeName: geo.closeName, closeText: geo.closeText },
      // The list starts right under the "Matches" line, as in C (padding 10 16 6, no gap).
      J3: { ok: geo.matches === `Matches in title, id, tags · ${geo.n}` && geo.matchesHidden === "true" && /matching concept/.test(geo.status) && geo.statusWidth <= 1 && Math.abs(geo.gap) <= 1, matches: geo.matches, status: geo.status, gapUnderMatches: geo.gap },
      J4: { ok: geo.optionHeight >= 46, optionHeight: geo.optionHeight },
      J5: { ok: true },
      J6: { ok: geo.foot.join(" | ") === "Up / Down to move | Enter to open", foot: geo.foot },
    };
    const out = {};
    for (const [id, checks] of Object.entries(ids)) {
      const { misses, values } = await measure(page, checks);
      const { ok, ...seen } = extra[id];
      out[id] = { pass: ok && misses.length === 0, misses, ...seen, values };
    }
    await ctx.shot(page, "J1-J6");
    return out;
  },
  // L6 at 1100 and above (1 100, 1 190, 1 280, 1 440, 1 920): three columns
  // side by side, 290 and 340; at 1099 the zones stack (page, context,
  // explorer: L2), the boundary of the same media pair.
  async L6(ctx) {
    const out = {};
    for (const width of [1099, 1100, 1190, 1280, 1440, 1920]) {
      const page = await ctx.newPage({ viewport: { width, height: 900 } });
      await page.goto(urlOf(ctx, PAGE));
      out[width] = await page.evaluate(() => {
        const r = (el) => el.getBoundingClientRect();
        const e = r(document.getElementById("okf-explorer"));
        const m = r(document.querySelector("body > .okf-layout > main"));
        const c = r(document.getElementById("okf-context"));
        return { explorer: e.width, main: m.width, context: c.width, sideBySide: e.right <= m.left + 1 && m.right <= c.left + 1 && Math.abs(e.top - m.top) < 1 && Math.abs(c.top - m.top) < 1, stacked: c.top >= m.bottom - 1 && e.top >= c.bottom - 1, scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth };
      });
      await ctx.shot(page, `L6-${width}`);
    }
    const wide = Object.entries(out).filter(([w]) => Number(w) >= 1100);
    return { pass: wide.every(([, v]) => v.sideBySide && Math.abs(v.explorer - 290) <= 1 && Math.abs(v.context - 340) <= 1 && v.scrollWidth <= v.clientWidth) && out[1099].stacked && out[1099].scrollWidth <= out[1099].clientWidth, ...out };
  },
  // §11.0 and A19: text tokens >= 4.5:1 on the page; shapes >= 3:1 on the
  // page and on the active row (--blue-soft); in both themes, measured.
  async tokens(ctx) {
    const page = await ctx.newPage();
    await page.goto(urlOf(ctx, PAGE));
    const out = {};
    let pass = true;
    for (const theme of ["light", "dark"]) {
      await page.evaluate((t) => document.documentElement.setAttribute("data-theme", t), theme);
      const c = await page.evaluate((names) => {
        const probeEl = document.createElement("span");
        document.body.appendChild(probeEl);
        const got = {};
        for (const n of names) { probeEl.style.color = `var(${n})`; got[n] = getComputedStyle(probeEl).color; }
        probeEl.remove();
        return got;
      }, ["--white", "--blue-soft", "--ink", "--gray", "--blue", "--blue-hover", "--red", "--stale", "--okf-type-0", "--okf-type-1", "--okf-type-2", "--okf-type-3", "--okf-type-4", "--okf-type-5"]);
      const ratios = {};
      for (const n of ["--ink", "--gray", "--blue", "--blue-hover", "--red", "--stale"]) {
        ratios[`${n} text`] = ctx.lib.contrast(c[n], c["--white"]);
        if (ratios[`${n} text`] < 4.5) { pass = false; }
      }
      for (const n of ["--okf-type-0", "--okf-type-1", "--okf-type-2", "--okf-type-3", "--okf-type-4", "--okf-type-5", "--blue", "--stale"]) {
        for (const bg of ["--white", "--blue-soft"]) {
          ratios[`${n} on ${bg}`] = ctx.lib.contrast(c[n], c[bg]);
          if (ratios[`${n} on ${bg}`] < 3) { pass = false; }
        }
      }
      out[theme] = ratios;
      // Rank 1 (the square) is green in both themes, never the ink it was
      // (owner's request, 2026-10-09): the token, and a square as drawn.
      const square = await page.evaluate(() => {
        const e = Array.from(document.querySelectorAll("svg .okf-shape-1")).find((x) => !x.closest(".okf-chip-type"));
        return e ? getComputedStyle(e).fill : null;
      });
      const green = ctx.lib.rgb(theme === "light" ? "#1e7d32" : "#5dc26b");
      out[`${theme} square`] = { token: c["--okf-type-1"], drawn: square, expected: green };
      if (c["--okf-type-1"] !== green || square !== green) { pass = false; }
      await ctx.shot(page, `tokens-${theme}`);
    }
    return { pass, ...out };
  },
};

async function run(ctx) {
  const out = {};
  const want = (id) => ctx.wanted(id);
  const tocPage = ctx.lib.pageWithSections(ctx.siteDir, 3);
  const g = ctx.lib.guard;
  if (want("fonts")) { out.fonts = await g(() => fonts(ctx)); }
  for (const [id, rel, checks] of probes(ctx.lib.rgb, tocPage)) {
    if (!want(id)) { continue; }
    out[id] = await g(() => probe(ctx, id, urlOf(ctx, rel), checks));
  }
  const special = [
    ["H2", "H2full"], ["H3", "H3link"], ["H6", "H6text"], ["H7", "H7narrow"], ["H8", "H8count"], ["H9", "H9link"], ["H11", "H11pressed"], ["H12", "H12"], ["H13", "H13"],
    ["E7", "E7chevron"], ["E8", "E8label"], ["E10", "E10flags"], ["E12", "E12foot"], ["E13", "E13"], ["C4", "C4"], ["C6", "C6fold"], ["C6", "C6head"], ["C8", "C8"],
    ["X4", "X4"], ["X10", "X10count"], ["X11", "X11"], ["L6", "L6"], ["tokens", "tokens"],
  ];
  for (const [id, name] of special) {
    if (!want(id)) { continue; }
    const key = out[id] ? `${id}-${name}` : id;
    out[key] = await g(() => specials[name](ctx, tocPage));
  }
  // J1-J6: one palette, six results (a crash fails all six).
  const js = ["J1", "J2", "J3", "J4", "J5", "J6"].filter(want);
  if (js.length) {
    const r = await g(() => specials.J(ctx));
    for (const id of js) { out[id] = r.error ? r : r[id]; }
  }
  // Control 14 and console errors, over every page this slice opened.
  if (want("requests")) {
    const pages = [];
    for (const rel of ["index.html", PAGE]) {
      const page = await ctx.newPage();
      await page.goto(ctx.lib.pageUrl(ctx.acme, rel));
      pages.push(page.okfTracked);
    }
    const page = await ctx.newPage();
    await page.goto(ctx.lib.pageUrl(ctx.site, tocPage));
    pages.push(page.okfTracked);
    const outside = pages.flatMap((t) => t.outside);
    const errors = pages.flatMap((t) => t.errors);
    out.requests = { pass: outside.length === 0 && errors.length === 0, outside: outside.slice(0, 10), errors: errors.slice(0, 10) };
  }
  return out;
}

module.exports = { run };
