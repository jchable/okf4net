// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Recette of slice P2 (local graph): spec §11.4 X5-X9 and ACCEPTANCE.md
// "## P2" lines P2-1 to P2-8. Run by recette.js (--slices p2), in file://,
// never by npm test or CI (spec §12.8). It measures what jsdom cannot:
// geometry (getBBox), computed fonts and colours, contrasts in both themes,
// real clicks and navigation, 390 px, requests.
//
// ctx is P1.1's lib.context (spec §12.8): pages come from ctx.newPage(),
// captures go through ctx.shot(page, id), colours and contrasts through
// ctx.lib. A result { pass: null, note } means "not applicable": the driver
// prints it n/a and never counts it as a failure.
"use strict";
const fs = require("fs");
const { fileURLToPath } = require("url");

// acme_retail page whose type ranks second (Attested Computation -> square,
// spec §12.1): the mockup's centre, a 28 square in a 40 selection square.
const ACME_PAGE = "computations/gross-margin-period.html";
const CAP = 40;
const TOKENS = {
  light: { white: "#ffffff", blue: "#1a3fd6", gray: "#6a6a72", hair: "#e3e3e8", edge: "#8a8a94" },
  dark: { white: "#101014", blue: "#8fa5f5", gray: "#9a9aa2", hair: "#2a2a33", edge: "#8a8a94" },
};

// Spec X7 r9, measured in the page: where OkfLocal.labels() put each label of
// the drawing. The width is the browser's own (getBBox), the height the module's
// model (8 above the baseline, 3 below: a font's line box is taller and would
// flag overlaps the module does not claim to avoid). Returns, per drawing, the
// labels leaving the 4 px margin, the cuts that are wrong, the overlaps (label
// with label, label with another node's shape; the centre's shape counts with its
// selection square) and how many sit at the default place.
function measureLabels() {
  const VIEW = { width: 298, height: 248 };
  const ELLIPSIS = String.fromCharCode(0x2026);
  const svg = document.querySelector("#okf-local-graph .okf-local-canvas > svg");
  const nodes = Array.from(svg.querySelectorAll("g.okf-node")).map((g) => {
    const text = g.querySelector("text");
    const shape = text.previousElementSibling;
    const ring = g.querySelector(".okf-node-ring");
    const tb = text.getBBox();
    const y = Number(text.getAttribute("y"));
    const b = (ring || shape).getBBox();
    const sb = shape.getBBox();
    const title = g.querySelector("title").textContent.replace(/^absent: /, "");
    return {
      title, text: text.textContent, anchor: text.getAttribute("text-anchor"), y,
      box: { x0: tb.x, x1: tb.x + tb.width, y0: y - 8, y1: y + 3 },
      obstacle: { x0: b.x, x1: b.x + b.width, y0: b.y, y1: b.y + b.height },
      cy: sb.y + sb.height / 2, cx: sb.x + sb.width / 2, half: Math.max(sb.width, sb.height) / 2, centre: g.classList.contains("okf-selected"),
    };
  });
  const out = { count: nodes.length, outside: [], badCut: [], overlaps: [], atDefault: 0, moved: 0 };
  const hit = (a, b) => a.x0 < b.x1 && b.x0 < a.x1 && a.y0 < b.y1 && b.y0 < a.y1;
  nodes.forEach((n, i) => {
    const b = n.box;
    if (b.x0 < 4 - 0.05 || b.x1 > VIEW.width - 4 + 0.05 || b.y0 < 4 - 0.05 || b.y1 > VIEW.height - 4 + 0.05) { out.outside.push({ title: n.title, box: b }); }
    const seg = n.title.slice(n.title.lastIndexOf("/") + 1);
    const points = Array.from(seg);
    const expected = points.length > 20 ? points.slice(0, 19).join("") + ELLIPSIS : seg;
    if (n.text !== expected) { out.badCut.push({ title: n.title, text: n.text, expected }); }
    const under = n.cy + n.half + (n.centre ? 6 : 0) + 14;
    if (n.anchor === "middle" && Math.abs(n.y - under) < 0.06 && Math.abs((b.x0 + b.x1) / 2 - n.cx) < 0.6) { out.atDefault++; } else { out.moved++; }
    nodes.forEach((o, j) => {
      if (j > i && hit(b, o.box)) { out.overlaps.push([n.title, o.title, "label"]); }
      if (j !== i && hit(b, o.obstacle)) { out.overlaps.push([n.title, o.title, "shape"]); }
    });
  });
  return out;
}

// X12, measured in the page: the enlarged drawing in #okf-local-modal. Its
// view is its viewBox (laid out for the drawing area, 1 unit = 1 px unless the
// area is smaller than the panel's view). Label boxes: real width (getBBox),
// the module's height model (8 above the baseline, 3 below) -- `model` -- and
// the whole glyph box -- `full`, informational (a font's line box is taller
// than the model). Overlaps: label with label, label with another node's shape
// (the centre's with its selection square); `edges`: labels an edge crosses
// (informational, the halo keeps them legible). `perChar`: the widest real
// advance per character, against the 6.2 / 6.5 estimates.
function measureModal() {
  const ELLIPSIS = String.fromCharCode(0x2026);
  const svg = document.querySelector("#okf-local-modal .okf-local-modal-canvas > svg");
  const vb = svg.getAttribute("viewBox").split(" ").map(Number);
  const W = vb[2];
  const H = vb[3];
  const maxChars = W >= 560 ? 32 : 20;
  const nodes = Array.from(svg.querySelectorAll("g.okf-node")).map((g) => {
    const text = g.querySelector("text");
    const shape = text.previousElementSibling;
    const ring = g.querySelector(".okf-node-ring");
    const tb = text.getBBox();
    const y = Number(text.getAttribute("y"));
    const b = (ring || shape).getBBox();
    const title = g.querySelector("title").textContent.replace(/^absent: /, "");
    return {
      title, text: text.textContent, centre: g.classList.contains("okf-selected"),
      model: { x0: tb.x, x1: tb.x + tb.width, y0: y - 8, y1: y + 3 },
      full: { x0: tb.x, x1: tb.x + tb.width, y0: tb.y, y1: tb.y + tb.height },
      obstacle: { x0: b.x, x1: b.x + b.width, y0: b.y, y1: b.y + b.height },
      advance: text.getComputedTextLength() / Math.max(1, text.textContent.length),
    };
  });
  const hit = (a, b) => a.x0 < b.x1 && b.x0 < a.x1 && a.y0 < b.y1 && b.y0 < a.y1;
  const out = { view: [W, H], count: nodes.length, outside: [], badCut: [], model: { label: 0, shape: 0 }, full: { label: 0, shape: 0 }, pairs: [], edges: 0, perChar: { node: 0, centre: 0 } };
  nodes.forEach((n, i) => {
    const b = n.model;
    if (b.x0 < 4 - 0.05 || b.x1 > W - 4 + 0.05 || b.y0 < 4 - 0.05 || b.y1 > H - 4 + 0.05) { out.outside.push(n.title); }
    const seg = n.title.slice(n.title.lastIndexOf("/") + 1);
    const points = Array.from(seg);
    const expected = points.length > maxChars ? points.slice(0, maxChars - 1).join("") + ELLIPSIS : seg;
    if (n.text !== expected) { out.badCut.push({ title: n.title, text: n.text, expected }); }
    const key = n.centre ? "centre" : "node";
    out.perChar[key] = Math.max(out.perChar[key], Math.round(n.advance * 1000) / 1000);
    nodes.forEach((o, j) => {
      if (j > i && hit(b, o.model)) { out.model.label++; out.pairs.push([n.text, o.text, "label"]); }
      if (j !== i && hit(b, o.obstacle)) { out.model.shape++; out.pairs.push([n.text, o.text, "shape"]); }
      if (j > i && hit(n.full, o.full)) { out.full.label++; }
      if (j !== i && hit(n.full, o.obstacle)) { out.full.shape++; }
    });
  });
  // An edge crosses a label when its segment meets the label's model box.
  const crosses = (l, b) => {
    const x1 = Number(l.getAttribute("x1")); const y1 = Number(l.getAttribute("y1"));
    const x2 = Number(l.getAttribute("x2")); const y2 = Number(l.getAttribute("y2"));
    for (let t = 0; t <= 1; t += 0.02) {
      const x = x1 + (x2 - x1) * t; const y = y1 + (y2 - y1) * t;
      if (x > b.x0 && x < b.x1 && y > b.y0 && y < b.y1) { return true; }
    }
    return false;
  };
  const lines = Array.from(svg.querySelectorAll("line.okf-local-edge"));
  out.edges = nodes.filter((n) => lines.some((l) => crosses(l, n.model))).length;
  // The same overlaps in screen pixels (getBoundingClientRect), labels only.
  const rects = Array.from(svg.querySelectorAll("text.okf-local-label"), (t) => t.getBoundingClientRect());
  let screen = 0;
  rects.forEach((a, i) => rects.forEach((b, j) => { if (j > i && a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom) { screen++; } }));
  out.screenLabelPairs = screen;
  out.pairs = out.pairs.slice(0, 12);
  return out;
}

async function run(ctx) {
  const { site, acme } = ctx;
  const { rgb, contrast } = ctx.lib;
  const results = {};
  const dot = String.fromCharCode(0xb7);
  // One page at ctx.newPage's defaults (1 440 x 900, light); P2-4 and P2-5
  // open their own.
  const page = await ctx.newPage();
  const check = async (id, fn) => {
    if (!ctx.wanted(id)) { return; }
    try { results[id] = await fn(); } catch (e) { results[id] = { pass: false, error: String(e).split("\n")[0] }; }
  };
  const shot = (id, p = page) => ctx.shot(p, id);
  // A theme is forced through data-theme, which both dark blocks honour.
  const open = async (url, theme = "light", p = page) => {
    await p.goto(url);
    await p.waitForSelector("#okf-local-graph .okf-local-canvas > svg");
    await p.evaluate((t) => document.documentElement.setAttribute("data-theme", t), theme);
  };

  await check("X5", async () => {
    await open(acme + ACME_PAGE);
    const r = await page.evaluate(() => {
      const one = document.getElementById("okf-local-hops-1");
      const two = document.getElementById("okf-local-hops-2");
      const s1 = getComputedStyle(one);
      const s2 = getComputedStyle(two);
      const b1 = one.getBoundingClientRect();
      const b2 = two.getBoundingClientRect();
      return {
        pressed: [one.getAttribute("aria-pressed"), two.getAttribute("aria-pressed")],
        height: [b1.height, b2.height], padding: [s1.paddingLeft, s1.paddingRight], fontSize: s1.fontSize,
        on: { bg: s1.backgroundColor, color: s1.color, weight: s1.fontWeight },
        off: { bg: s2.backgroundColor, color: s2.color, border: s2.borderTopColor },
        adjacent: Math.abs(b2.left - b1.right) < 0.5,
      };
    });
    await shot("X5");
    await page.click("#okf-local-hops-2");
    const after = await page.evaluate(() => ["okf-local-hops-1", "okf-local-hops-2"].map((id) => document.getElementById(id).getAttribute("aria-pressed")));
    await open(acme + ACME_PAGE);
    const reloaded = await page.evaluate(() => document.getElementById("okf-local-hops-1").getAttribute("aria-pressed"));
    const t = TOKENS.light;
    const pass = r.pressed.join() === "true,false" && r.height.every((v) => v === 24) && r.padding.join() === "10px,10px"
      && r.fontSize === "12px" && r.on.bg === rgb(t.blue) && r.on.color === rgb(t.white) && r.on.weight === "600"
      && r.off.bg === rgb(t.white) && r.off.color === rgb(t.gray) && r.off.border === rgb(t.hair) && r.adjacent
      && after.join() === "false,true" && reloaded === "true";
    return { pass, ...r, after, reloaded };
  });

  await check("X6", async () => {
    await open(acme + ACME_PAGE);
    const r = await page.evaluate(() => {
      const frame = document.querySelector("#okf-local-graph .okf-local-canvas");
      const s = getComputedStyle(frame);
      return {
        height: frame.getBoundingClientRect().height,
        // A 1px border is drawn as one device pixel: Firefox reports 0.666667px
        // for EVERY 1px border of the page on a 150% display (while
        // devicePixelRatio is emulated as 1). The frame's must equal a reference
        // element's 1px border, whatever the browser snaps it to.
        border: [s.borderTopWidth, s.borderTopStyle, s.borderTopColor].join(),
        reference: (() => {
          const probe = document.createElement("div");
          probe.style.cssText = "position:absolute;visibility:hidden;border:1px solid #000;width:0;height:0";
          document.body.appendChild(probe);
          const w = getComputedStyle(probe).borderTopWidth;
          probe.remove();
          return w;
        })(),
        svgWidth: frame.querySelector("svg").getBoundingClientRect().width,
        inner: frame.clientWidth,
      };
    });
    await shot("X6");
    const pass = r.height === 250 && r.border === `${r.reference},solid,${rgb(TOKENS.light.hair)}` && Math.abs(r.svgWidth - r.inner) < 0.5;
    return { pass, ...r };
  });

  await check("X7", async () => {
    await open(acme + ACME_PAGE);
    await page.click("#okf-local-hops-2");
    const r = await page.evaluate(() => {
      const idx = window.OKF_INDEX;
      const S = window.OkfShapes;
      const svg = document.querySelector("#okf-local-graph .okf-local-canvas > svg");
      const nodes = Array.from(svg.querySelectorAll("g.okf-node")).map((g) => {
        const title = g.querySelector("title").textContent;
        const ghost = title.startsWith("absent: ");
        const centre = g.classList.contains("okf-selected");
        const kind = ghost ? "ghost" : S.kindOf(idx, idx.concepts.findIndex((c) => c.id === title));
        const s = S.SIZES[centre ? "localCenter" : "local"][kind];
        const text = g.querySelector("text");
        const box = text.previousElementSibling.getBBox();
        const ts = getComputedStyle(text);
        const ring = g.querySelector(".okf-node-ring");
        const rs = ring && getComputedStyle(ring);
        return {
          title, kind, centre, expected: s.size - s.stroke, measured: Math.max(box.width, box.height),
          label: { size: ts.fontSize, weight: ts.fontWeight, family: ts.fontFamily },
          ring: ring ? { side: ring.getBBox().width, expected: s.size + 8, width: rs.strokeWidth, color: rs.stroke } : null,
        };
      });
      const lines = Array.from(svg.querySelectorAll("line.okf-local-edge")).map((l) => {
        const ls = getComputedStyle(l);
        return { dashed: l.classList.contains("okf-local-edge-in"), dash: l.getAttribute("stroke-dasharray"), stroke: ls.stroke, width: ls.strokeWidth, marker: l.getAttribute("marker-end") };
      });
      const marker = svg.querySelector("#okf-local-arrow");
      return { nodes, lines, marker: [marker.getAttribute("markerWidth"), marker.getAttribute("markerHeight")].join() };
    });
    const placed = await page.evaluate(measureLabels);
    await shot("X7-light");
    await page.evaluate(() => document.documentElement.setAttribute("data-theme", "dark"));
    await shot("X7-dark");
    const t = TOKENS.light;
    const nodesOk = r.nodes.every((n) => Math.abs(n.measured - n.expected) <= 0.6
      && n.label.size === (n.centre ? "10.5px" : "10px") && n.label.weight === (n.centre ? "700" : "400") && /Space Mono/.test(n.label.family)
      && (n.centre ? n.ring && n.ring.side === n.ring.expected && n.ring.width === "2px" && n.ring.color === rgb(t.blue) : n.ring === null));
    const linesOk = r.lines.length > 0 && r.lines.every((l) => l.stroke === rgb(t.edge) && l.width === "1.4px"
      && (l.dashed ? l.dash === "4 3" && l.marker === "url(#okf-local-arrow-in)" : l.dash === null && l.marker === "url(#okf-local-arrow)"));
    // r9: a drawing below the cap (8 nodes) keeps every label inside the view,
    // cut as specified and clear of the other labels and shapes.
    const labelsOk = placed.count === r.nodes.length && placed.outside.length === 0 && placed.badCut.length === 0 && placed.overlaps.length === 0;
    // r9's cut, on a real long id of the OKF4net site (no site has an absent
    // concept, so no page of these two draws a ghost).
    let cut = null;
    await page.goto(site + "index.html");
    const pick = await page.evaluate(() => {
      const idx = window.OKF_INDEX;
      const deg = idx.concepts.map(() => 0);
      for (const [f, t, , g] of idx.edges) { if (!g && f !== t) { deg[f]++; deg[t]++; } }
      const k = idx.concepts.findIndex((c, i) => c.id.slice(c.id.lastIndexOf("/") + 1).length > 21 && deg[i] >= 1 && deg[i] <= 12);
      return k === -1 ? null : { path: idx.concepts[k].path, id: idx.concepts[k].id };
    });
    if (pick) {
      await open(site + pick.path);
      const m = await page.evaluate(measureLabels);
      const centreLabel = await page.evaluate(() => document.querySelector("#okf-local-graph g.okf-selected text").textContent);
      cut = { id: pick.id, centreLabel, ...m };
    }
    const cutOk = cut === null || (cut.outside.length === 0 && cut.badCut.length === 0 && cut.overlaps.length === 0 && Array.from(cut.centreLabel).length === 20 && cut.centreLabel.endsWith(String.fromCharCode(0x2026)));
    return { pass: nodesOk && linesOk && r.marker === "7,7" && labelsOk && cutOk, placed, cut, ...r };
  });

  await check("X8", async () => {
    await open(acme + ACME_PAGE);
    const r = await page.evaluate(() => {
      const foot = document.querySelector("#okf-local-graph .okf-local-foot");
      const a = document.getElementById("okf-local-open");
      const fs = getComputedStyle(foot);
      const as = a && getComputedStyle(a);
      const g = document.getElementById("okf-global-graph");
      return {
        text: foot.querySelector("span").textContent, size: fs.fontSize, color: fs.color,
        link: a && {
          text: a.textContent, weight: as.fontWeight, color: as.color, underline: as.textDecorationLine,
          href: a.getAttribute("href"), atRight: Math.abs(a.getBoundingClientRect().right - foot.getBoundingClientRect().right) < 1,
        },
        global: g && g.getAttribute("href"),
      };
    });
    await shot("X8");
    const t = TOKENS.light;
    const pass = r.text === `solid = links to ${dot} dashed = referenced by` && r.size === "12px" && r.color === rgb(t.gray)
      && Boolean(r.link) && r.link.text === "Open in graph" && r.link.weight === "600" && r.link.color === rgb(t.blue)
      && r.link.underline === "none" && r.link.atRight && r.link.href === r.global;
    return { pass, ...r };
  });

  await check("X9", async () => {
    await page.goto(site + "index.html");
    const hub = await page.evaluate((cap) => {
      const idx = window.OKF_INDEX;
      const C = idx.concepts.length;
      const sets = idx.concepts.map(() => new Set());
      for (const [from, to, , ghost] of idx.edges) {
        const key = ghost ? C + to : to;
        if (from === key) { continue; }
        sets[from].add(key);
        if (!ghost) { sets[to].add(from); }
      }
      const k = sets.findIndex((s) => s.size > cap - 1);
      return k === -1 ? null : { path: idx.concepts[k].path, total: sets[k].size };
    }, CAP);
    if (!hub) { return { pass: null, note: "no concept of this site has more than 39 neighbours; the cap is covered by the jsdom cases" }; }
    await open(site + hub.path);
    const r = await page.evaluate(() => {
      const section = document.getElementById("okf-local-graph");
      const more = section.querySelector(".okf-local-omitted");
      return {
        nodes: section.querySelectorAll(".okf-local-canvas > svg g.okf-node").length, more: more && more.textContent,
        summary: section.querySelector(".okf-local-list summary").textContent, rows: section.querySelectorAll(".okf-local-list li").length,
      };
    });
    const capLabels = await page.evaluate(measureLabels);
    await shot("X9");
    await page.click("#okf-local-graph .okf-local-omitted");
    const opened = await page.evaluate(() => ({ open: document.getElementById("okf-local-list").open, focus: document.activeElement.tagName }));
    const pass = r.nodes === CAP && r.more === `+${hub.total - (CAP - 1)} omitted` && r.summary === `List ${dot} ${hub.total} neighbours`
      && r.rows === hub.total && opened.open && opened.focus === "SUMMARY"
      && capLabels.count === CAP && capLabels.outside.length === 0 && capLabels.badCut.length === 0; // overlaps are allowed at the cap (A11)
    return { pass, hub, ...r, opened, capLabels };
  });

  await check("P2-1", async () => {
    await open(acme + ACME_PAGE);
    const target = await page.evaluate(() => {
      const g = document.querySelector("#okf-local-graph .okf-local-canvas > svg g.okf-local-node:not(.okf-selected)");
      const id = g.querySelector("title").textContent;
      return { id, path: window.OKF_INDEX.concepts.find((c) => c.id === id).path };
    });
    // On the shape itself, as a reader aims: since the shapes are 0.7 of the
    // mockups' (2026-10-09), the middle of the node's box (shape and label) can
    // fall in the gap between them, where WebKit hits the svg, not the node.
    await page.locator("#okf-local-graph .okf-local-canvas > svg g.okf-local-node:not(.okf-selected)").first().locator('[class^="okf-shape-"]').click();
    await page.waitForURL((url) => url.href.endsWith(target.path));
    const landed = page.url();
    await page.goBack();
    // "Neighbourhood" sits between "On this page" and "Referenced by": measured,
    // by document order and by vertical position, on a page that shows all
    // three. No acme page does (the ones with a contents list have no
    // backlinks), so the OKF4net site gives it: the first concept in index
    // order that something links to and whose body holds two headings.
    let order = null;
    {
      const idx = ctx.lib.readIndex(ctx.siteDir);
      const linked = new Set(idx.edges.filter((e) => !e[3] && e[0] !== e[1]).map((e) => e[1]));
      const fsPath = require("path");
      for (const [k, c] of idx.concepts.entries()) {
        if (!linked.has(k)) { continue; }
        const html = fs.readFileSync(fsPath.join(ctx.siteDir, ...c.path.split("/")), "utf8");
        if ((html.match(/\\n#{2,3} /g) || []).length < 2) { continue; }
        await page.goto(site + c.path);
        const m = await page.evaluate(() => {
          const at = (id) => { const e = document.getElementById(id); return e && !e.hidden ? { top: e.getBoundingClientRect().top, node: e } : null; };
          const t = at("okf-toc");
          const g = at("okf-local-graph");
          const b = at("okf-backlinks-title");
          const follows = (x, y) => Boolean(x && y && (x.node.compareDocumentPosition(y.node) & Node.DOCUMENT_POSITION_FOLLOWING));
          return { present: [Boolean(t), Boolean(g), Boolean(b)], tocBeforeGraph: follows(t, g), graphBeforeBacklinks: follows(g, b), tops: [t && t.top, g && g.top, b && b.top] };
        });
        if (m.present.every(Boolean)) { order = { path: c.path, ...m }; break; }
      }
    }
    const orderOk = order !== null && (order.tocBeforeGraph && order.graphBeforeBacklinks && order.tops[0] < order.tops[1] && order.tops[1] < order.tops[2]);
    return { pass: landed === acme + target.path && orderOk, target, landed, order };
  });

  await check("P2-2", async () => {
    await open(acme + ACME_PAGE);
    const one = await page.locator("#okf-local-graph .okf-local-canvas > svg g.okf-node").count();
    await page.click("#okf-local-hops-2");
    const two = await page.locator("#okf-local-graph .okf-local-canvas > svg g.okf-node").count();
    await shot("P2-2-two-hops");
    return { pass: two > one, one, two };
  });

  await check("P2-3", async () => {
    const measured = {};
    let pass = true;
    for (const theme of ["light", "dark"]) {
      await open(acme + ACME_PAGE, theme);
      const m = await page.evaluate(() => {
        const svg = document.querySelector("#okf-local-graph .okf-local-canvas > svg");
        const shapes = Array.from(svg.querySelectorAll("g.okf-node > :is(circle, rect, path):not(.okf-node-ring):not(.okf-node-focus)")).map((s) => {
          const cs = getComputedStyle(s);
          return cs.fill === "none" || s.classList.contains("okf-ghost-mark") ? cs.stroke : cs.fill;
        });
        return {
          bg: getComputedStyle(document.body).backgroundColor,
          shapes,
          edges: Array.from(svg.querySelectorAll("line.okf-local-edge"), (l) => getComputedStyle(l).stroke),
          labels: Array.from(svg.querySelectorAll("text.okf-local-label:not(.okf-local-label-ghost)"), (t) => getComputedStyle(t).fill),
          foot: getComputedStyle(document.querySelector("#okf-local-graph .okf-local-foot")).color,
        };
      });
      const min = (list) => (list.length ? Math.min(...list.map((c) => contrast(c, m.bg))) : null);
      const r = { shapes: min(m.shapes), edges: min(m.edges), labels: min(m.labels), foot: contrast(m.foot, m.bg) };
      measured[theme] = r;
      pass = pass && r.shapes !== null && r.shapes >= 3 && r.edges !== null && r.edges >= 3 && r.labels !== null && r.labels >= 4.5 && r.foot >= 4.5;
      await shot(`P2-3-${theme}`);
    }
    return { pass, ...measured };
  });

  await check("P2-4", async () => {
    const narrow = await ctx.newPage({ viewport: { width: 390, height: 844 } });
    await open(acme + ACME_PAGE, "light", narrow);
    const r = await narrow.evaluate(() => ({
      scroll: document.documentElement.scrollWidth, inner: window.innerWidth,
      svg: document.querySelector("#okf-local-graph .okf-local-canvas > svg").getBoundingClientRect().width,
    }));
    // The panel stacks under the page text at 390 px: bring the frame into the capture.
    await narrow.locator("#okf-local-graph").scrollIntoViewIfNeeded();
    await shot("P2-4-390", narrow);
    return { pass: r.scroll <= r.inner && r.svg > 0, ...r };
  });

  await check("P2-5", async () => {
    // A fresh page, so okfTracked holds this check's requests only; it lists
    // every request outside both sites (data: and about: excepted).
    const fresh = await ctx.newPage();
    await open(acme + ACME_PAGE, "light", fresh);
    await fresh.click("#okf-local-hops-2");
    await fresh.click("#okf-local-hops-1");
    const outside = fresh.okfTracked.outside.slice();
    return { pass: outside.length === 0, outside };
  });

  await check("P2-6", async () => {
    await open(acme + ACME_PAGE);
    const href = await page.getAttribute("#okf-local-open", "href");
    const target = new URL(href, page.url());
    // The graph page exists since P3: a missing target is a defect, not n/a.
    if (!fs.existsSync(fileURLToPath(target.href.split("#")[0]))) {
      return { pass: false, note: "the graph page \"Open in graph\" points at does not exist", href };
    }
    await page.click("#okf-local-open");
    await page.waitForURL((url) => url.href.split("#")[0] === target.href.split("#")[0]);
    return { pass: new URL(page.url()).hash === target.hash, href, landed: page.url() };
  });

  await check("P2-7", async () => {
    // WebKit (Safari) does not Tab to links by default (Option+Tab does; neither
    // Tab nor Alt+Tab reaches them in Playwright's WebKit): "Open in graph" and
    // the rows can not take focus whatever the page does. Same ruling as H12.
    if (ctx.browserName === "webkit") {
      return { pass: null, note: "WebKit does not Tab to links by default (Option+Tab does): \"Open in graph\" and the list rows are not reachable by plain Tab in Safari whatever the page does; checked in Chrome, Edge and Firefox" };
    }
    await open(acme + ACME_PAGE);
    await page.focus("#okf-local-hops-2");
    const stops = [];
    for (let k = 0; k < 4; k++) {
      await page.keyboard.press("Tab");
      const stop = await page.evaluate(() => {
        const a = document.activeElement;
        return { tag: a.tagName, id: a.id, inSvg: Boolean(a.closest("svg")) };
      });
      stops.push(stop);
      if (stop.tag === "SUMMARY") { break; }
    }
    await page.keyboard.press("Enter");
    await page.keyboard.press("Tab");
    const row = await page.evaluate(() => {
      const a = document.activeElement;
      return { tag: a.tagName, row: a.classList.contains("okf-row"), open: document.getElementById("okf-local-list").open };
    });
    const pass = stops.every((s) => !s.inSvg) && stops.some((s) => s.tag === "SUMMARY") && row.open && row.tag === "A" && row.row;
    return { pass, stops, row };
  });

  // --- X12: the enlarged neighbourhood ---------------------------------------
  const openModal = async (p) => {
    await p.click("#okf-local-enlarge");
    await p.waitForSelector("#okf-local-modal .okf-local-modal-canvas > svg");
  };
  // The OKF4net site's hub at the cap: the page the brief names, else the
  // concept with the most neighbours.
  const hubPage = async () => {
    const idx = ctx.lib.readIndex(ctx.siteDir);
    const named = idx.concepts.find((c) => c.path === "code/csharp/okf4net.html");
    if (named) { return named.path; }
    const C = idx.concepts.length;
    const sets = idx.concepts.map(() => new Set());
    for (const [from, to, , ghost] of idx.edges) {
      const key = ghost ? C + to : to;
      if (from !== key) { sets[from].add(key); if (!ghost) { sets[to].add(from); } }
    }
    let best = 0;
    sets.forEach((s, k) => { if (s.size > sets[best].size) { best = k; } });
    return idx.concepts[best].path;
  };

  await check("X12", async () => {
    // Opener, dialog, focus, keys, backdrop, in light at 1 440 x 900.
    await open(acme + ACME_PAGE);
    const opener = await page.evaluate(() => {
      const b = document.getElementById("okf-local-enlarge");
      const r = b.getBoundingClientRect();
      const hops = document.querySelector("#okf-local-graph .okf-hops").getBoundingClientRect();
      const head = document.querySelector("#okf-local-graph .okf-local-head").getBoundingClientRect();
      return { w: r.width, h: r.height, name: b.getAttribute("aria-label"), popup: b.getAttribute("aria-haspopup"), expanded: b.getAttribute("aria-expanded"),
        afterHops: r.left >= hops.right, inHead: r.right <= head.right + 0.5 && Math.abs(r.top + r.height / 2 - (hops.top + hops.height / 2)) < 1 };
    });
    await openModal(page);
    const opened = await page.evaluate(() => {
      const backdrop = document.getElementById("okf-local-modal");
      const dialog = backdrop.querySelector("[role=dialog]");
      const rect = dialog.getBoundingClientRect();
      const area = backdrop.querySelector(".okf-local-modal-canvas");
      const svg = backdrop.querySelector(".okf-local-modal-canvas > svg");
      return {
        modal: dialog.getAttribute("aria-modal"), label: document.getElementById(dialog.getAttribute("aria-labelledby")).textContent,
        focusInside: dialog.contains(document.activeElement), expanded: document.getElementById("okf-local-enlarge").getAttribute("aria-expanded"),
        dialog: [Math.round(rect.width), Math.round(rect.height)], area: [area.clientWidth, area.clientHeight], viewBox: svg.getAttribute("viewBox"),
        bodyScroll: getComputedStyle(document.documentElement).overflow,
        inert: Array.from(document.body.children).filter((c) => c !== backdrop && c.localName !== "script").every((c) => c.inert === true),
      };
    });
    await shot("X12-open");
    // Tab cycles inside: the dialog's stops plus two, forward then back.
    const count = await page.evaluate(() => document.querySelectorAll("#okf-local-modal [role=dialog] :is(button, a[href])").length);
    const walk = [];
    for (let k = 0; k < count + 2; k++) {
      await page.keyboard.press("Tab");
      walk.push(await page.evaluate(() => document.getElementById("okf-local-modal").contains(document.activeElement) && document.activeElement !== document.querySelector("#okf-local-modal [role=dialog]")));
    }
    for (let k = 0; k < 3; k++) {
      await page.keyboard.press("Shift+Tab");
      walk.push(await page.evaluate(() => document.getElementById("okf-local-modal").contains(document.activeElement)));
    }
    // Escape: closed, focus back on the opener.
    await page.keyboard.press("Escape");
    const escaped = await page.evaluate(() => ({ hidden: document.getElementById("okf-local-modal").hidden, focus: document.activeElement.id, scroll: getComputedStyle(document.documentElement).overflow }));
    // The backdrop (a corner of it, outside the dialog) closes too.
    await openModal(page);
    await page.mouse.click(5, 5);
    const backdropClosed = await page.evaluate(() => ({ hidden: document.getElementById("okf-local-modal").hidden, focus: document.activeElement.id }));
    // The depth is one state: 2 hops in the dialog is 2 hops in the panel.
    await openModal(page);
    await page.click("#okf-local-modal-hops-2");
    const synced = await page.evaluate(() => [document.getElementById("okf-local-hops-2").getAttribute("aria-pressed"),
      document.querySelectorAll("#okf-local-modal svg g.okf-node").length, document.querySelectorAll("#okf-local-graph .okf-local-canvas svg g.okf-node").length]);
    await page.keyboard.press("Escape");
    const pass = opener.w === 24 && opener.h === 24 && opener.name === "Enlarge the neighbourhood" && opener.popup === "dialog" && opener.expanded === "false" && opener.afterHops && opener.inHead
      && opened.modal === "true" && opened.label.startsWith("Neighbourhood of ") && opened.focusInside && opened.expanded === "true" && opened.bodyScroll === "hidden" && opened.inert
      && opened.viewBox === `0 0 ${opened.area[0]} ${opened.area[1]}`
      && walk.every(Boolean) && escaped.hidden && escaped.focus === "okf-local-enlarge" && escaped.scroll !== "hidden"
      && backdropClosed.hidden && backdropClosed.focus === "okf-local-enlarge" && synced[0] === "true" && synced[1] === synced[2];
    return { pass, opener, opened, walk, escaped, backdropClosed, synced };
  });

  await check("X12-labels", async () => {
    // The enlarged drawings, measured: acme's gross-margin-period at 2 hops
    // (under the cap: no overlap allowed) and the OKF4net hub at the cap
    // (overlaps allowed by A11, counted), each in light, dark and at 390 px.
    const hub = await hubPage();
    const out = {};
    let pass = true;
    for (const [name, url, hops, strict] of [["acme", acme + ACME_PAGE, 2, true], ["hub", site + hub, 1, false], ["hub-2", site + hub, 2, false]]) {
      for (const [variant, viewport, theme] of [["light", { width: 1440, height: 900 }, "light"], ["dark", { width: 1440, height: 900 }, "dark"], ["390", { width: 390, height: 844 }, "light"]]) {
        const p = await ctx.newPage({ viewport });
        await open(url, theme, p);
        if (hops === 2) { await p.click("#okf-local-hops-2"); }
        await openModal(p);
        const m = await p.evaluate(measureModal);
        const scroll = await p.evaluate(() => ({ page: document.documentElement.scrollWidth, inner: window.innerWidth, dialog: document.querySelector("#okf-local-modal [role=dialog]").scrollWidth, dialogClient: document.querySelector("#okf-local-modal [role=dialog]").clientWidth }));
        await shot(`X12-${name}-${variant}`, p);
        const ok = m.outside.length === 0 && m.badCut.length === 0 && (!strict || (m.model.label === 0 && m.model.shape === 0))
          && scroll.page <= scroll.inner && scroll.dialog <= scroll.dialogClient && m.perChar.node <= 6.2 && m.perChar.centre <= 6.5;
        out[`${name}-${variant}`] = { ok, ...m, scroll };
        pass = pass && ok;
      }
    }
    return { pass, hub, ...out };
  });

  await check("X12-contrast", async () => {
    const measured = {};
    let pass = true;
    for (const theme of ["light", "dark"]) {
      await open(acme + ACME_PAGE, theme);
      await openModal(page);
      const c = await page.evaluate(() => {
        const q = (s) => document.querySelector(s);
        const st = (s) => getComputedStyle(q(s));
        const bg = getComputedStyle(q("#okf-local-modal [role=dialog]")).backgroundColor;
        return {
          bg,
          title: st("#okf-local-modal-title").color, close: st("#okf-local-modal .okf-local-modal-close").color,
          count: st("#okf-local-modal-count").color, foot: st("#okf-local-modal .okf-local-modal-foot").color,
          link: st("#okf-local-modal-open").color, row: st("#okf-local-modal .okf-local-modal-rows a.okf-row").color,
          pressed: [st("#okf-local-modal-hops-1").color, st("#okf-local-modal-hops-1").backgroundColor],
          unpressed: [st("#okf-local-modal-hops-2").color, st("#okf-local-modal-hops-2").backgroundColor],
          labels: Array.from(document.querySelectorAll("#okf-local-modal svg text.okf-local-label:not(.okf-local-label-ghost)"), (t) => getComputedStyle(t).fill),
          border: getComputedStyle(q("#okf-local-modal [role=dialog]")).borderTopColor,
        };
      });
      const r = {
        title: contrast(c.title, c.bg), close: contrast(c.close, c.bg), count: contrast(c.count, c.bg), foot: contrast(c.foot, c.bg),
        link: contrast(c.link, c.bg), row: contrast(c.row, c.bg), pressed: contrast(c.pressed[0], c.pressed[1]), unpressed: contrast(c.unpressed[0], c.unpressed[1]),
        labels: Math.min(...c.labels.map((f) => contrast(f, c.bg))), border: contrast(c.border, c.bg),
      };
      measured[theme] = r;
      pass = pass && Object.entries(r).every(([k, v]) => v >= (k === "border" ? 3 : 4.5));
      await shot(`X12-contrast-${theme}`);
      await page.keyboard.press("Escape");
    }
    return { pass, ...measured };
  });

  await check("X12-resize", async () => {
    const p = await ctx.newPage({ viewport: { width: 1440, height: 900 } });
    await open(acme + ACME_PAGE, "light", p);
    await openModal(p);
    const before = await p.evaluate(() => document.querySelector("#okf-local-modal .okf-local-modal-canvas > svg").getAttribute("viewBox"));
    await p.setViewportSize({ width: 900, height: 700 });
    await p.waitForFunction((b) => document.querySelector("#okf-local-modal .okf-local-modal-canvas > svg").getAttribute("viewBox") !== b, before, { timeout: 3000 });
    const after = await p.evaluate(() => {
      const area = document.querySelector("#okf-local-modal .okf-local-modal-canvas");
      return { viewBox: document.querySelector("#okf-local-modal .okf-local-modal-canvas > svg").getAttribute("viewBox"), area: [area.clientWidth, area.clientHeight] };
    });
    const m = await p.evaluate(measureModal);
    return { pass: after.viewBox === `0 0 ${after.area[0]} ${after.area[1]}` && m.model.label === 0 && m.model.shape === 0 && m.outside.length === 0, before, after, overlaps: m.model };
  });

  if (ctx.wanted("P2-8")) {
    results["P2-8"] = { pass: null, note: "compare the captures shots/<browser>/p2/X5.png to X9.png with mockup A's Neighbourhood section by hand (ACCEPTANCE.md P2-8)" };
  }
  return results;
}

module.exports = { run };
