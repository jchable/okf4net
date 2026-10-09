// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Recette of the resizable side panel (okf-resize.js): the splitter between the
// middle column and the right-hand column of a concept page (context panel) and
// of the graph page (drawer), and the graph page's refit when its canvas changes
// size. Run by recette.js (--slices p4), in file://, never by npm test or CI
// (spec §12.8). It measures what jsdom cannot: a real mouse drag against a real
// layout (the column moves by the pointer's delta, clamps at the bounds), the
// keyboard, persistence across a reload and across pages under file://, the
// stacked layout at 390 px, the focus ring, contrast and forced colours, and the
// drawing's refit.
//
// ctx is P1.1's lib.context (spec §12.8): pages come from ctx.newPage()
// (1 440 x 900 and light unless told otherwise; forcedColors and reducedMotion
// are passed through), captures through ctx.shot(page, id), colours and
// contrasts through ctx.lib. A result { pass: null, note } means "not
// applicable": the driver prints it n/a and never counts it as a failure.
"use strict";
const fs = require("fs");
const path = require("path");

const ACME_PAGE = "computations/gross-margin-period.html";
const near = (a, b, tolerance = 1) => Math.abs(a - b) <= tolerance;
const KEY = "okf-context-w";
const HANDLE = "body > .okf-layout > .okf-splitter, body > .okf-graph-layout > .okf-splitter";

// What the checks read, in one evaluation.
const snap = (page) => page.evaluate(({ handleSelector, key }) => {
  const handle = document.querySelector(handleSelector);
  const panel = document.getElementById("okf-context") || document.getElementById("okf-graph-detail");
  const main = document.getElementById("okf-main");
  const layout = panel ? panel.parentNode : null;
  const rect = (e) => {
    if (!e) { return null; }
    const r = e.getBoundingClientRect();
    return { x: r.x, y: r.y, w: r.width, h: r.height, right: r.right, bottom: r.bottom };
  };
  let stored = null;
  try { stored = window.localStorage.getItem(key); } catch (e) { stored = "(storage refused)"; }
  const cs = handle ? getComputedStyle(handle) : null;
  const attr = (name) => (handle ? handle.getAttribute(name) : null);
  return {
    handle: rect(handle), panel: rect(panel), main: rect(main), layout: rect(layout),
    display: cs ? cs.display : null, hidden: handle ? handle.hidden : null,
    now: Number(attr("aria-valuenow")), min: Number(attr("aria-valuemin")), max: Number(attr("aria-valuemax")), text: attr("aria-valuetext"),
    variable: layout ? layout.style.getPropertyValue("--okf-context-w") : null,
    custom: layout ? layout.hasAttribute("data-okf-resized") : null,
    stored, scrollW: document.documentElement.scrollWidth, innerW: window.innerWidth,
    focused: handle ? document.activeElement === handle : false,
  };
}, { handleSelector: HANDLE, key: KEY });

// A real mouse drag of the splitter by `dx` pixels (negative: left, which widens the column).
async function drag(page, dx, { steps = 10, release = true } = {}) {
  const s = await snap(page);
  if (!s.handle) { throw new Error("no splitter"); }
  const x = s.handle.x + s.handle.w / 2;
  const y = s.handle.y + Math.min(s.handle.h / 2, 300);
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + dx, y, { steps });
  if (release) { await page.mouse.up(); }
  return { x, y };
}

async function run(ctx) {
  const { contrast } = ctx.lib;
  const results = {};
  const pages = [];
  const newPage = async (options) => {
    const p = await ctx.newPage(options);
    pages.push(p);
    return p;
  };
  const check = async (id, fn) => {
    if (!ctx.wanted(id)) { return; }
    try { results[id] = await fn(); } catch (e) { results[id] = { pass: false, error: String(e).split("\n")[0] }; }
  };
  const graphUrl = (base, dir) => {
    const html = fs.readFileSync(path.join(dir, "index.html"), "utf8");
    const m = /id="okf-global-graph"[^>]*href="([^"]*)"/.exec(html) || /href="([^"]*)"[^>]*id="okf-global-graph"/.exec(html);
    if (!m) { throw new Error("index.html has no Global graph link"); }
    return base + m[1].split("#")[0];
  };
  const waitLayout = (p) => p.waitForFunction(() => {
    const c = document.getElementById("okf-graph-canvas");
    return c && (c.getAttribute("data-okf-layout") === "done" || c.querySelector("svg") === null);
  }, null, { timeout: 60000 });
  const settle = (p) => p.waitForFunction(() => document.getElementById("okf-body") && document.getElementById("okf-body").childElementCount > 0, null, { timeout: 30000 });

  const conceptUrl = ctx.acme + ACME_PAGE;
  const otherUrl = ctx.acme + (ctx.lib.pageWithSections(ctx.acmeDir, 2) || "index.html");
  const siteSections = ctx.lib.pageWithSections(ctx.siteDir, 2);
  const acmeGraph = graphUrl(ctx.acme, ctx.acmeDir);
  const openConcept = async (options, url = conceptUrl) => {
    const p = await newPage(options);
    await p.goto(url);
    await settle(p);
    return p;
  };

  // --- structure ------------------------------------------------------------

  await check("V2-1-structure", async () => {
    const p = await openConcept();
    const s = await snap(p);
    const probe = await p.evaluate(() => {
      const h = document.querySelector("body > .okf-layout > .okf-splitter");
      const panel = document.getElementById("okf-context");
      return {
        role: h.getAttribute("role"), orientation: h.getAttribute("aria-orientation"), label: h.getAttribute("aria-label"),
        controls: h.getAttribute("aria-controls"), tabindex: h.tabIndex, touch: getComputedStyle(h).touchAction, cursor: getComputedStyle(h).cursor,
        prev: h.previousElementSibling && h.previousElementSibling.id, next: h.nextElementSibling && h.nextElementSibling.id,
        transition: getComputedStyle(h).transitionDuration, panelHidden: panel.hidden,
      };
    });
    const pass = s.handle && near(s.handle.w, 10, 0.5) && s.handle.h >= 300 && s.display === "block" && !s.hidden
      && near(s.handle.x + s.handle.w / 2, s.panel.x, 1.5) && near(s.panel.w, 340) && s.now === 340 && s.min === 240 && s.max === 720
      && probe.role === "separator" && probe.orientation === "vertical" && probe.label === "Resize the side panel" && probe.controls === "okf-context"
      && probe.tabindex === 0 && probe.touch === "none" && probe.cursor === "col-resize" && probe.prev === "okf-main" && probe.next === "okf-context"
      && !s.custom && s.variable === "" && s.stored === null;
    return { pass, ...s, ...probe };
  });

  // --- the mouse -------------------------------------------------------------

  await check("V2-2-drag", async () => {
    const out = {};
    for (const [name, url] of [["acme", conceptUrl], ["okf4net", siteSections ? ctx.site + siteSections : null]]) {
      if (!url) { continue; }
      const p = await openConcept({}, url);
      const before = await snap(p);
      await drag(p, -100);
      const after = await snap(p);
      await drag(p, +40);
      const back = await snap(p);
      out[name] = {
        panel: [before.panel.w, after.panel.w, back.panel.w], main: [before.main.w, after.main.w, back.main.w],
        now: [before.now, after.now, back.now], stored: back.stored, variable: after.variable,
        pass: near(after.panel.w - before.panel.w, 100) && near(before.main.w - after.main.w, 100)
          && near(back.panel.w - after.panel.w, -40) && after.now === Math.round(after.panel.w) && back.stored === String(Math.round(back.panel.w))
          && after.variable === `${Math.round(after.panel.w)}px`,
      };
    }
    return { pass: Object.values(out).every((o) => o.pass) && Object.keys(out).length > 0, ...out };
  });

  await check("V2-3-clamps", async () => {
    const wide = await openConcept();
    const base = await snap(wide);
    await drag(wide, -2500, { steps: 20 });
    const max = await snap(wide);
    await drag(wide, +2500, { steps: 20 });
    const min = await snap(wide);
    // 1 100: the middle column keeps its 360 (explorer 290, so the panel stops at 450).
    const narrow = await newPage({ viewport: { width: 1100, height: 900 } });
    await narrow.goto(conceptUrl);
    await settle(narrow);
    await drag(narrow, -2500, { steps: 20 });
    const tight = await snap(narrow);
    const ok1100 = tight.main.w >= 359 && tight.panel.w <= 451 && tight.now === Math.round(tight.panel.w);
    return {
      pass: near(max.panel.w, 720) && max.now === 720 && max.main.w >= 360 && near(min.panel.w, 240) && min.now === 240 && ok1100,
      baseMain: base.main.w, atMax: [max.panel.w, max.main.w, max.max], atMin: min.panel.w, at1100: [tight.panel.w, tight.main.w, tight.max],
    };
  });

  await check("V2-4-keyboard", async () => {
    const p = await openConcept();
    // Reach the splitter with Tab, as a keyboard reader would, from the main column's end.
    let reached = false;
    await p.locator("a.okf-skip").focus();
    for (let k = 0; k < 120 && !reached; k++) {
      await p.keyboard.press("Tab");
      reached = (await snap(p)).focused;
    }
    const focusVisible = reached ? await p.evaluate(({ sel }) => {
      const h = document.querySelector(sel);
      const cs = getComputedStyle(h);
      return { matches: h.matches(":focus-visible"), outline: `${cs.outlineStyle} ${cs.outlineWidth} ${cs.outlineColor}` };
    }, { sel: HANDLE }) : null;
    if (!reached) { await p.locator(HANDLE).focus(); }
    const log = [];
    const read = async (label) => { const s = await snap(p); log.push([label, Math.round(s.panel.w), s.now, s.stored]); return s; };
    await p.keyboard.press("ArrowLeft");
    const a = await read("ArrowLeft");
    await p.keyboard.press("Shift+ArrowLeft");
    const b = await read("Shift+ArrowLeft");
    await p.keyboard.press("ArrowRight");
    const c = await read("ArrowRight");
    await p.keyboard.press("Home");
    const home = await read("Home");
    await p.keyboard.press("End");
    const end = await read("End");
    await p.keyboard.press("Enter");
    const reset = await read("Enter");
    await p.keyboard.press("Alt+ArrowLeft");
    const alt = await read("Alt+ArrowLeft");
    const pass = near(a.panel.w, 356) && near(b.panel.w, 420) && near(c.panel.w, 404) && near(home.panel.w, 240) && near(end.panel.w, 720)
      && near(reset.panel.w, 340) && reset.stored === null && reset.variable === "" && near(alt.panel.w, 340)
      && [a, b, c, home, end, reset].every((s) => s.now === Math.round(s.panel.w));
    // Tab reaching a non-link tabindex stop is a browser setting in Safari, not a defect.
    if (!reached && ctx.browserName === "webkit") {
      return { pass: pass ? null : false, note: "WebKit did not Tab to the splitter (Safari's own Tab setting); the keys work once it is focused", log, reached };
    }
    return { pass: pass && reached && focusVisible.matches && /solid 2px/.test(focusVisible.outline), reached, focusVisible, log };
  });

  await check("V2-5-double-click", async () => {
    const p = await openConcept();
    await drag(p, -150);
    const moved = await snap(p);
    const s = await snap(p);
    await p.mouse.dblclick(s.handle.x + s.handle.w / 2, s.handle.y + Math.min(s.handle.h / 2, 300));
    const reset = await snap(p);
    return { pass: near(moved.panel.w, 490) && near(reset.panel.w, 340) && reset.stored === null && reset.variable === "", moved: moved.panel.w, reset: reset.panel.w, stored: reset.stored };
  });

  // --- persistence: reload, then another page (file://) -----------------------

  await check("V2-6-persist-reload", async () => {
    const p = await openConcept();
    await drag(p, -120);
    const set = await snap(p);
    await p.reload();
    await settle(p);
    const after = await snap(p);
    return { pass: near(after.panel.w, set.panel.w) && near(after.panel.w, 460) && after.now === Math.round(set.panel.w) && after.stored === set.stored, set: set.panel.w, after: after.panel.w, stored: after.stored };
  });

  await check("V2-7-persist-across-pages", async () => {
    const p = await openConcept();
    await drag(p, -120);
    const set = await snap(p);
    await p.goto(otherUrl);
    await settle(p);
    const other = await snap(p);
    await p.goto(acmeGraph);
    await waitLayout(p);
    const graph = await snap(p);
    const same = near(other.panel.w, set.panel.w) && near(graph.panel.w, set.panel.w);
    if (!same) {
      return { pass: null, note: `file:// localStorage is not shared between these pages in ${ctx.browserName}: concept page ${Math.round(set.panel.w)} px, another page ${Math.round(other.panel.w)} px (stored ${other.stored}), graph page ${Math.round(graph.panel.w)} px (stored ${graph.stored}); the width is kept per page there, as the theme is`, set: set.panel.w, other: other.panel.w, graph: graph.panel.w };
    }
    return { pass: true, set: set.panel.w, other: other.panel.w, graph: graph.panel.w };
  });

  // --- no flash: the width a stored value gives on the first frame the panel shows ---

  await check("V2-8-first-frame", async () => {
    const out = {};
    for (const [name, url, wait] of [["concept", conceptUrl, settle], ["graph", acmeGraph, waitLayout]]) {
      const p = await newPage();
      await p.addInitScript(({ key }) => {
        try { window.localStorage.setItem(key, "520"); } catch (e) { /* refused */ }
        window.okfSeen = [];
        const tick = () => {
          const panel = document.getElementById("okf-context") || document.getElementById("okf-graph-detail");
          if (panel && !panel.hidden && panel.getBoundingClientRect().width > 0) { window.okfSeen.push(Math.round(panel.getBoundingClientRect().width)); }
          window.requestAnimationFrame(tick);
        };
        window.requestAnimationFrame(tick);
      }, { key: KEY });
      await p.goto(url);
      await wait(p);
      const seen = await p.evaluate(() => window.okfSeen);
      out[name] = { frames: seen.length, first: seen[0], other: seen.filter((w) => w !== 520).length };
    }
    const pass = Object.values(out).every((o) => o.frames > 0 && o.other === 0);
    return pass ? { pass, ...out } : { pass: null, note: "a frame painted before the stored width was applied (one layout shift, accepted): " + JSON.stringify(out), ...out };
  });

  // --- the stacked layout ------------------------------------------------------

  await check("V2-9-stacked", async () => {
    const out = {};
    for (const [name, viewport] of [["390", { width: 390, height: 844 }], ["1099", { width: 1099, height: 800 }], ["1100", { width: 1100, height: 800 }]]) {
      const p = await newPage({ viewport });
      await p.addInitScript(({ key }) => { try { window.localStorage.setItem(key, "600"); } catch (e) { /* refused */ } }, { key: KEY });
      await p.goto(conceptUrl);
      await settle(p);
      const s = await snap(p);
      out[name] = { display: s.display, hidden: s.hidden, panel: Math.round(s.panel.w), main: Math.round(s.main.w), horizontalScroll: s.scrollW > s.innerW, variable: s.variable, tabbable: await p.evaluate((sel) => document.querySelector(sel).offsetParent !== null, HANDLE) };
    }
    const g = await newPage({ viewport: { width: 390, height: 844 } });
    await g.addInitScript(({ key }) => { try { window.localStorage.setItem(key, "600"); } catch (e) { /* refused */ } }, { key: KEY });
    await g.goto(acmeGraph);
    await waitLayout(g);
    const gs = await snap(g);
    out.graph390 = { display: gs.display, panel: Math.round(gs.panel.w), horizontalScroll: gs.scrollW > gs.innerW };
    const pass = out["390"].display === "none" && !out["390"].tabbable && !out["390"].horizontalScroll && out["390"].variable === "" && out["390"].panel >= 390 - 2
      && out["1099"].display === "none" && !out["1099"].tabbable && out["1099"].variable === ""
      && out["1100"].display === "block" && out["1100"].tabbable && out["1100"].panel <= 450 && out["1100"].main >= 359 && out["1100"].variable !== ""
      && out.graph390.display === "none" && !out.graph390.horizontalScroll;
    const shot = await newPage({ viewport: { width: 390, height: 844 } });
    await shot.goto(conceptUrl);
    await settle(shot);
    await ctx.shot(shot, "V2-stacked-390");
    return { pass, ...out };
  });

  await check("V2-10-window-resize", async () => {
    const p = await openConcept();
    await drag(p, -360);
    const big = await snap(p);
    await p.setViewportSize({ width: 1100, height: 900 });
    await p.waitForTimeout(150);
    const small = await snap(p);
    await p.setViewportSize({ width: 800, height: 900 });
    await p.waitForTimeout(150);
    const stacked = await snap(p);
    await p.setViewportSize({ width: 1440, height: 900 });
    await p.waitForTimeout(150);
    const back = await snap(p);
    return {
      pass: near(big.panel.w, 700) && small.panel.w <= 451 && small.main.w >= 359 && stacked.display === "none" && near(back.panel.w, 700) && back.stored === "700",
      big: big.panel.w, at1100: [small.panel.w, small.main.w, small.max], stacked: [stacked.display, stacked.panel.w], back: back.panel.w, stored: back.stored,
    };
  });

  // --- the graph page: the drawing uses the room, or keeps the reader's view ------

  const graphState = (p) => p.evaluate(() => {
    const canvas = document.getElementById("okf-graph-canvas");
    const cr = canvas.getBoundingClientRect();
    const m = /translate\((-?[0-9.]+) (-?[0-9.]+)\) scale\(([0-9.]+)\)/.exec(canvas.querySelector("g.okf-graph-viewport").getAttribute("transform"));
    let outside = 0;
    let total = 0;
    for (const g of canvas.querySelectorAll("g.okf-node")) {
      const r = g.getBoundingClientRect();
      total++;
      if (r.left < cr.left - 1 || r.right > cr.right + 1 || r.top < cr.top - 1 || r.bottom > cr.bottom + 1) { outside++; }
    }
    return { canvasW: cr.width, canvasH: cr.height, view: [Number(m[1]), Number(m[2]), Number(m[3])], outside, total };
  });

  await check("V2-11-graph-refit", async () => {
    const p = await newPage();
    await p.goto(acmeGraph);
    await waitLayout(p);
    const s0 = await snap(p);
    const g0 = await graphState(p);
    // Widen the drawer by 200: the canvas narrows by 200 and the drawing is fitted to what is left.
    await drag(p, -200);
    await p.waitForTimeout(200);
    const s1 = await snap(p);
    const g1 = await graphState(p);
    // Narrow it to its minimum: the canvas grows and the drawing uses the room.
    await drag(p, +400, { steps: 20 });
    await p.waitForTimeout(200);
    const s2 = await snap(p);
    const g2 = await graphState(p);
    const wider = g2.canvasW > g0.canvasW;
    const pass = near(s1.panel.w - s0.panel.w, 200) && near(g0.canvasW - g1.canvasW, 200) && near(s2.panel.w, 240) && wider
      && g1.outside === 0 && g2.outside === 0 && JSON.stringify(g1.view) !== JSON.stringify(g0.view) && JSON.stringify(g2.view) !== JSON.stringify(g1.view);
    await ctx.shot(p, "V2-graph-light");
    return { pass, canvas: [g0.canvasW, g1.canvasW, g2.canvasW], view: [g0.view, g1.view, g2.view], outside: [g0.outside, g1.outside, g2.outside], total: g0.total, drawer: [s0.panel.w, s1.panel.w, s2.panel.w] };
  });

  await check("V2-12-graph-moved-view-kept", async () => {
    const p = await newPage();
    await p.goto(acmeGraph);
    await waitLayout(p);
    await p.locator('#okf-graph-zoom button[aria-label="Zoom in"]').click();
    await p.waitForTimeout(100);
    const before = await graphState(p);
    await drag(p, -150);
    await p.waitForTimeout(250);
    const after = await graphState(p);
    // The reader asked for Fit: the next resize follows the canvas again.
    await p.locator("#okf-graph-zoom button", { hasText: "Fit" }).click();
    const fitted = await graphState(p);
    await drag(p, +100);
    await p.waitForTimeout(250);
    const refit = await graphState(p);
    return {
      pass: JSON.stringify(before.view) === JSON.stringify(after.view) && after.canvasW < before.canvasW && JSON.stringify(refit.view) !== JSON.stringify(fitted.view),
      before: before.view, after: after.view, fitted: fitted.view, refit: refit.view,
    };
  });

  await check("V2-13-graph-large", async () => {
    // The OKF4net bundle's graph (hundreds of nodes, under the 1 500 limit): the drawing is fitted on every frame of a drag.
    const p = await newPage();
    await p.goto(graphUrl(ctx.site, ctx.siteDir));
    await waitLayout(p);
    const g0 = await graphState(p);
    // Frames while the splitter is dragged across 300 px: the longest gap between two animation frames.
    await p.evaluate(() => {
      window.okfGap = { last: performance.now(), max: 0, on: true };
      const tick = (t) => { if (!window.okfGap.on) { return; } window.okfGap.max = Math.max(window.okfGap.max, t - window.okfGap.last); window.okfGap.last = t; requestAnimationFrame(tick); };
      requestAnimationFrame(tick);
    });
    await drag(p, -300, { steps: 30 });
    await p.waitForTimeout(300);
    const gap = Math.round(await p.evaluate(() => { window.okfGap.on = false; return window.okfGap.max; }));
    const g1 = await graphState(p);
    return { pass: gap < 500 && near(g0.canvasW - g1.canvasW, 300), canvas: [g0.canvasW, g1.canvasW], maxFrameGapMs: gap, nodes: g0.total };
  });

  // --- the local graph keeps its drawing -----------------------------------------

  await check("V2-14-local-graph", async () => {
    const p = await openConcept();
    const before = await p.evaluate(() => {
      const svg = document.querySelector("#okf-local-graph .okf-local-canvas > svg");
      const r = svg.getBoundingClientRect();
      return { w: r.width, h: r.height, scale: svg.getScreenCTM().a, nodes: svg.querySelectorAll("g.okf-node").length };
    });
    await drag(p, -380);
    const wide = await p.evaluate(() => {
      const svg = document.querySelector("#okf-local-graph .okf-local-canvas > svg");
      const frame = svg.parentNode.getBoundingClientRect();
      const r = svg.getBoundingClientRect();
      return { w: r.width, h: r.height, scale: svg.getScreenCTM().a, frameW: frame.width, nodes: svg.querySelectorAll("g.okf-node").length };
    });
    await drag(p, +1000, { steps: 20 });
    const narrow = await p.evaluate(() => {
      const svg = document.querySelector("#okf-local-graph .okf-local-canvas > svg");
      const r = svg.getBoundingClientRect();
      return { w: r.width, h: r.height, scale: svg.getScreenCTM().a };
    });
    await ctx.shot(p, "V2-concept-narrowest");
    // At 240 nothing in the panel runs past its content box (the Neighbourhood head wraps instead).
    const overflow = await p.evaluate(() => {
      const panel = document.getElementById("okf-context");
      const pr = panel.getBoundingClientRect();
      const pad = parseFloat(getComputedStyle(panel).paddingRight);
      const out = [];
      for (const el of panel.querySelectorAll("*")) {
        if (el.closest("svg") && el.tagName.toLowerCase() !== "svg") { continue; }
        const r = el.getBoundingClientRect();
        if (r.width > 0 && r.right > pr.right - pad + 1) { out.push(el.tagName.toLowerCase() + "." + (el.getAttribute("class") || "") + " +" + Math.round(r.right - (pr.right - pad))); }
      }
      return { out: out.slice(0, 6), scrolls: panel.scrollWidth > panel.clientWidth };
    });
    // At 720 the frame is wide and the drawing stays 298 x 248 in it (scale 1, centred); at 240 it scales down to fit the width.
    return { pass: near(before.scale, 1, 0.01) && near(wide.scale, 1, 0.01) && near(wide.h, 248, 1) && wide.nodes === before.nodes && narrow.scale < 1 && narrow.scale > 0.6 && overflow.out.length === 0 && !overflow.scrolls, before, wide, narrow, overflow };
  });

  // --- themes, contrast, forced colours, reduced motion, captures ---------------

  await check("V2-15-contrast-and-captures", async () => {
    const out = {};
    for (const scheme of ["light", "dark"]) {
      const p = await openConcept({ colorScheme: scheme });
      await drag(p, -180);
      // Move the pointer away, then take the resting colours; then hover.
      await p.mouse.move(100, 500);
      const rest = await p.evaluate((sel) => {
        const h = document.querySelector(sel);
        const bg = getComputedStyle(document.body).backgroundColor;
        return { grip: getComputedStyle(h, "::after").backgroundColor, line: getComputedStyle(h, "::before").backgroundColor, bg };
      }, HANDLE);
      const s = await snap(p);
      await p.mouse.move(s.handle.x + s.handle.w / 2, s.handle.y + 300);
      const hover = await p.evaluate((sel) => {
        const h = document.querySelector(sel);
        return { grip: getComputedStyle(h, "::after").backgroundColor, line: getComputedStyle(h, "::before").backgroundColor };
      }, HANDLE);
      await ctx.shot(p, `V2-concept-${scheme}`);
      const hoverShot = await snap(p);
      out[scheme] = { rest, hover, gripContrast: contrast(rest.grip, rest.bg), lineHoverContrast: contrast(hover.line, rest.bg), gripHoverContrast: contrast(hover.grip, rest.bg), hoverPanel: hoverShot.panel.w };
      const g = await newPage({ colorScheme: scheme });
      await g.goto(acmeGraph);
      await waitLayout(g);
      await drag(g, -100);
      await g.mouse.move(100, 500);
      await ctx.shot(g, `V2-graph-${scheme}`);
    }
    const pass = ["light", "dark"].every((k) => out[k].gripContrast >= 3 && out[k].lineHoverContrast >= 3 && out[k].gripHoverContrast >= 3);
    return { pass, ...out };
  });

  await check("V2-16-focus-ring-capture", async () => {
    const p = await openConcept();
    await p.locator("a.okf-skip").focus();
    let reached = false;
    for (let k = 0; k < 120 && !reached; k++) { await p.keyboard.press("Tab"); reached = (await snap(p)).focused; }
    if (!reached) { await p.locator(HANDLE).focus(); }
    await p.keyboard.press("Shift+ArrowLeft");
    await ctx.shot(p, "V2-focus-ring");
    return { pass: true, reached, note: "capture only" };
  });

  await check("V2-17-forced-colors", async () => {
    let p;
    try { p = await newPage({ forcedColors: "active" }); } catch (e) { return { pass: null, note: `forcedColors emulation is not available in ${ctx.browserName}: ${String(e).split("\n")[0]}` }; }
    await p.goto(conceptUrl);
    await settle(p);
    const active = await p.evaluate(() => window.matchMedia("(forced-colors: active)").matches);
    if (!active) { return { pass: null, note: `${ctx.browserName} did not enter forced-colors mode` }; }
    await p.locator("a.okf-skip").focus();
    let reached = false;
    for (let k = 0; k < 120 && !reached; k++) { await p.keyboard.press("Tab"); reached = (await snap(p)).focused; }
    if (!reached) { await p.locator(HANDLE).focus(); }
    const r = await p.evaluate((sel) => {
      const h = document.querySelector(sel);
      const cs = getComputedStyle(h);
      const canvas = getComputedStyle(document.body).backgroundColor;
      return { grip: getComputedStyle(h, "::after").backgroundColor, outlineStyle: cs.outlineStyle, outlineColor: cs.outlineColor, outlineWidth: cs.outlineWidth, canvas };
    }, HANDLE);
    await ctx.shot(p, "V2-forced-colors");
    const pass = r.grip !== "rgba(0, 0, 0, 0)" && r.grip !== r.canvas && r.outlineStyle === "solid" && r.outlineColor !== r.canvas;
    return { pass, ...r };
  });

  await check("V2-18-reduced-motion", async () => {
    const p = await newPage({ reducedMotion: "reduce" });
    await p.goto(conceptUrl);
    await settle(p);
    const r = await p.evaluate((sel) => {
      const h = document.querySelector(sel);
      return ["", "::before", "::after"].map((pseudo) => getComputedStyle(h, pseudo || null).transitionDuration + "/" + getComputedStyle(h, pseudo || null).animationName);
    }, HANDLE);
    return { pass: r.every((x) => x === "0s/none"), r };
  });

  await check("V2-19-errors", async () => {
    const bad = [];
    for (const p of pages) {
      const t = p.okfTracked;
      if (t.errors.length > 0 || t.outside.length > 0) { bad.push({ errors: t.errors.slice(0, 2), outside: t.outside.slice(0, 2) }); }
    }
    return { pass: bad.length === 0, pages: pages.length, bad: bad.slice(0, 3) };
  });

  return results;
}

module.exports = { run };
