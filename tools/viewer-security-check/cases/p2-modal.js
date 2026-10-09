// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The enlarged neighbourhood (P2, spec §11.4 X12, §12.4): the local graph of a
// concept page opened in a modal dialog. run.js's case loader calls
// register(h) with the frozen helpers (spec §12.7). Pure cases load the real
// okf-local.js into a bare window; page cases open pages of the site
// okf-render generates from fixtures/hostile-bundle/ (p2-local/), sometimes
// under a synthetic index. jsdom has no layout: the modal measures a drawing
// area of 0 x 0 there and lays out at the panel's 298 x 248, unless a case
// gives the area a size (sized()).
"use strict";
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");
const { okfLocal, siteIndex, assertFixedSvg, expectedHood, positionOf, click } = require("./p2.js");

// A hub with `first` direct neighbours (alternating directions), `second`
// concepts two hops away (all through the first ones) and `ghosts` absent
// targets, the ids' last segments `length` characters long.
function star(first, second, ghosts, length, prefix = "p2-local/") {
  const name = (p, k) => `${prefix}${p}${String(k).padStart(2, "0")}${"s".repeat(Math.max(0, length - 3))}`;
  const ids = [`${prefix}hub`];
  const links = [];
  for (let k = 0; k < first; k++) { ids.push(name("n", k)); links.push(k % 2 ? [k + 1, 0] : [0, k + 1]); }
  for (let k = 0; k < second; k++) { ids.push(name("f", k)); links.push([1 + (k % first), first + 1 + k]); }
  const absent = [];
  for (let g = 0; g < ghosts; g++) { absent.push(name("g", g)); links.push([0, g, "ghost"]); }
  return siteIndex(ids, links, absent);
}

// The default (panel) layout, pinned: build() and labels() without a viewport
// over a fixed battery, digested. The digest was taken from okf-local.js
// BEFORE the viewport parameter existed (commit 2e0b43c): any change to the
// panel's positions, edges, lists or label places changes it.
function defaultBattery(OkfLocal) {
  const out = [];
  const texts = (index, r) => r.nodes.map((n) => OkfLocal.lastSegment(n.key < index.concepts.length ? index.concepts[n.key].id : index.ghosts[n.key - index.concepts.length].id));
  for (let m = 1; m <= 39; m++) { out.push(JSON.stringify(OkfLocal.build(star(m, 0, 0, 6), 0, 1))); }
  for (let m = 1; m <= 38; m++) {
    for (let second = 1; m + second <= 39; second++) { out.push(JSON.stringify(OkfLocal.build(star(m, second, 0, 6), 0, 2))); }
  }
  for (const length of [4, 19, 24, 60]) {
    for (let first = 1; first <= 12; first++) {
      for (let ghosts = 0; ghosts <= 2; ghosts++) {
        for (const second of [0, 3, 8]) {
          const index = star(first, second, ghosts, length);
          const r = OkfLocal.build(index, 0, second > 0 ? 2 : 1);
          out.push(JSON.stringify(r));
          const halves = [r.nodes.map((n) => (n.dist === 0 ? 20 : n.key >= index.concepts.length ? 10.6 : 12.75)),
            r.nodes.map((n) => (n.dist === 0 ? 13.75 : n.key >= index.concepts.length ? 7.6 : 9))];
          for (const h of halves) { out.push(JSON.stringify(OkfLocal.labels(r.nodes, texts(index, r), h))); }
        }
      }
    }
  }
  for (const hops of [1, 2]) {
    const index = star(30, 30, 3, 22);
    const r = OkfLocal.build(index, 0, hops);
    out.push(JSON.stringify(r));
    out.push(JSON.stringify(OkfLocal.labels(r.nodes, texts(index, r), r.nodes.map((n) => (n.dist === 0 ? 13.75 : 9)))));
    out.push(JSON.stringify(OkfLocal.labels(r.nodes, r.nodes.map(() => "z".repeat(40)), r.nodes.map((n) => (n.dist === 0 ? 20 : 12)))));
  }
  return crypto.createHash("sha256").update(out.join("\n")).digest("hex");
}

// The default layout's digest, taken from okf-local.js at 2e0b43c (before the
// viewport existed) by running defaultBattery on it.
const DEFAULT_DIGEST = "f38f86dc6a3b5dcac3b12dc3014cef5514ce091528bf3d83b27de7480f8ebaf8";

// The drawing areas the modal gives build() (the CSS of X12 at 390 x 844, 900,
// 1 100 and 1 440 wide), the smallest one the label sweep covers (360 x 300),
// both sides of the cut's threshold and the largest view accepted.
const VIEWPORTS = [
  { width: 360, height: 300 }, { width: 372, height: 370 }, { width: 526, height: 464 }, { width: 559, height: 320 },
  { width: 560, height: 320 }, { width: 710, height: 544 }, { width: 798, height: 624 }, { width: 1098, height: 758 },
  { width: 4096, height: 4096 },
];

const ELLIPSIS = String.fromCharCode(0x2026);
const meet = (a, b) => a.x1 < b.x2 && b.x1 < a.x2 && a.y1 < b.y2 && b.y1 < a.y2;
// The label box the module's own model gives (6.2 a character, 6.5 for the
// centre; 8 above the baseline, 3 below), restated so a change of the
// constants is a change of this file.
function boxOf(text, x, y, anchor, centre) {
  const width = text.length * (centre ? 6.5 : 6.2);
  const x1 = anchor === "middle" ? x - width / 2 : anchor === "start" ? x : x - width;
  return { x1, x2: x1 + width, y1: y - 8, y2: y + 3 };
}

function registerPure(h) {
  const { check, assert } = h;

  check("enlarged neighbourhood: build() and labels() without a viewport are the panel's layout, unchanged (pinned digest)", () => {
    const { OkfLocal } = okfLocal();
    const digest = defaultBattery(OkfLocal);
    assert(digest === DEFAULT_DIGEST, `the default layout changed: digest ${digest}, pinned ${DEFAULT_DIGEST}`);
    // And a few values in clear, from the same commit.
    const r = OkfLocal.build(star(4, 2, 0, 6), 0, 2);
    assert(JSON.stringify(r.nodes) === '[{"key":0,"dist":0,"x":149,"y":118},{"key":1,"dist":1,"x":223.95,"y":67.09},{"key":2,"dist":1,"x":223.95,"y":168.91},{"key":3,"dist":1,"x":74.05,"y":168.91},{"key":4,"dist":1,"x":74.05,"y":67.09},{"key":5,"dist":2,"x":283,"y":118},{"key":6,"dist":2,"x":15,"y":118}]',
      `panel nodes: ${JSON.stringify(r.nodes)}`);
    const texts = ["hub", "n00sss", "n01sss", "n02sss", "n03sss", "f00sss", "f01sss"];
    const placed = OkfLocal.labels(r.nodes, texts, r.nodes.map((n) => (n.dist === 0 ? 13.75 : 9))).map((p) => [p.text, p.x, p.y, p.anchor]);
    assert(JSON.stringify(placed) === '[["hub",149,145.75,"middle"],["n00sss",223.95,90.09,"middle"],["n01sss",223.95,191.91,"middle"],["n02sss",74.05,191.91,"middle"],["n03sss",74.05,90.09,"middle"],["f00sss",275.4,141,"middle"],["f01sss",22.6,141,"middle"]]',
      `panel labels: ${JSON.stringify(placed)}`);
    // The panel's own view, given explicitly, is the same layout.
    const same = OkfLocal.build(star(4, 2, 0, 6), 0, 2, { width: 298, height: 248 });
    assert(JSON.stringify(same) === JSON.stringify(r), "build() at an explicit 298 x 248 differs from build() without a viewport");
  });

  check("enlarged neighbourhood: build() in a larger view keeps the data, scales the rings, stays in the view, is deterministic", () => {
    const one = okfLocal().OkfLocal;
    const other = okfLocal().OkfLocal;
    for (const hops of [1, 2]) {
      // At the cap: 40 direct neighbours at 1 hop; two full rings (20 + 19) at 2.
      const index = hops === 1 ? star(45, 0, 5, 8) : star(20, 30, 0, 8);
      const panel = one.build(index, 0, hops);
      for (const viewport of VIEWPORTS) {
        const r = one.build(index, 0, hops, viewport);
        const where = `hops ${hops}, ${viewport.width} x ${viewport.height}`;
        assert(JSON.stringify(r) === JSON.stringify(one.build(index, 0, hops, viewport)), `${where}: two builds differ`);
        assert(JSON.stringify(r) === JSON.stringify(other.build(index, 0, hops, { width: viewport.width, height: viewport.height })), `${where}: two windows disagree`);
        // Same neighbours, same edges, same cap: only the positions change.
        for (const field of ["list", "edges", "total", "omitted", "centre", "hops"]) {
          assert(JSON.stringify(r[field]) === JSON.stringify(panel[field]), `${where}: ${field} differs from the panel's`);
        }
        assert(JSON.stringify(r.nodes.map((n) => [n.key, n.dist])) === JSON.stringify(panel.nodes.map((n) => [n.key, n.dist])), `${where}: other nodes drawn`);
        assert(r.nodes.length === one.CAP && r.nodes.some((n) => n.dist === hops), `${where}: ${r.nodes.length} nodes drawn at the cap`);
        const sx = viewport.width / 298;
        const sy = viewport.height / 248;
        assert(r.nodes[0].x === viewport.width / 2 && r.nodes[0].y === viewport.height / 2 - 6, `${where}: the centre is at (${r.nodes[0].x}, ${r.nodes[0].y})`);
        r.nodes.forEach((n, k) => {
          assert(Number.isFinite(n.x) && Number.isFinite(n.y), `${where}: node ${k} at (${n.x}, ${n.y})`);
          // Shape (9) and its default label (14 under it, 3 of descender) inside the view.
          assert(n.x - 9 >= 0 && n.x + 9 <= viewport.width && n.y - 9 >= 0 && n.y + 9 + 17 <= viewport.height, `${where}: node ${k} at (${n.x}, ${n.y}) leaves the view`);
          if (k === 0) { return; }
          const p = panel.nodes[k];
          const want = [r.nodes[0].x + (p.x - 149) * sx, r.nodes[0].y + (p.y - 118) * sy];
          // The panel's positions are rounded to the hundredth, then scaled: allow that rounding, scaled.
          assert(Math.abs(n.x - want[0]) <= 0.01 + 0.005 * sx && Math.abs(n.y - want[1]) <= 0.01 + 0.005 * sy, `${where}: node ${k} at (${n.x}, ${n.y}), its ring scaled says (${want})`);
        });
      }
    }
  });

  check("enlarged neighbourhood: labels() in the modal's views never leave the view, never cover a label or a shape, up to 15 neighbours, 1 and 2 hops", () => {
    const { OkfLocal } = okfLocal();
    let layouts = 0;
    for (const viewport of VIEWPORTS) {
      const wide = viewport.width >= 560;
      for (const length of [4, 12, 19, 24, 32, 40, 60]) {
        const combos = [];
        for (let first = 1; first <= 15; first++) {
          for (let ghosts = 0; ghosts <= 2 && first + ghosts <= 15; ghosts++) { combos.push([first, 0, ghosts]); }
          for (let second = 1; first + second <= 15; second++) { combos.push([first, second, 0]); }
        }
        for (const [first, second, ghosts] of combos) {
          const index = star(first, second, ghosts, length);
          const C = index.concepts.length;
          const r = OkfLocal.build(index, 0, second > 0 ? 2 : 1, viewport);
          const texts = r.nodes.map((n) => OkfLocal.lastSegment(n.key < C ? index.concepts[n.key].id : index.ghosts[n.key - C].id));
          // The panel sweep's half-extents (the shapes before they shrank to 0.7): wider than any drawn today.
          const halves = r.nodes.map((n) => (n.dist === 0 ? 20 : n.key >= C ? 10.6 : 12.75));
          const placed = OkfLocal.labels(r.nodes, texts, halves, viewport);
          const where = `${viewport.width} x ${viewport.height}, ${first} + ${second} neighbours, ${ghosts} absent, ids of ${length}`;
          placed.forEach((p, k) => {
            const points = Array.from(texts[k]);
            const max = wide ? 32 : 20;
            const expected = points.length > max ? points.slice(0, max - 1).join("") + ELLIPSIS : texts[k];
            assert(p.text === expected, `${where}: label ${k} is "${p.text}", expected "${expected}"`);
            const b = boxOf(p.text, p.x, p.y, p.anchor, k === 0);
            assert(b.x1 >= 3.9 && b.x2 <= viewport.width - 3.9 && b.y1 >= 3.9 && b.y2 <= viewport.height - 3.9, `${where}: label ${k} leaves the view`);
            for (let j = 0; j < k; j++) { assert(!meet(b, boxOf(placed[j].text, placed[j].x, placed[j].y, placed[j].anchor, j === 0)), `${where}: label ${k} covers label ${j}`); }
            r.nodes.forEach((n, j) => {
              assert(!meet(b, { x1: n.x - halves[j], x2: n.x + halves[j], y1: n.y - halves[j], y2: n.y + halves[j] }), `${where}: label ${k} covers the shape of node ${j}`);
            });
          });
          layouts++;
        }
      }
    }
    assert(layouts > 9000, `${layouts} layouts swept`);
  });

  check("enlarged neighbourhood: labels() at the cap in a large view is deterministic and keeps every label in the view", () => {
    const one = okfLocal().OkfLocal;
    const other = okfLocal().OkfLocal;
    for (const viewport of [{ width: 372, height: 370 }, { width: 798, height: 624 }]) {
      for (const [hops, every] of [[1, null], [2, null], [2, "z".repeat(60)]]) {
        const index = hops === 1 ? star(45, 0, 4, 28) : star(20, 30, 0, 28);
        const r = one.build(index, 0, hops, viewport);
        const C = index.concepts.length;
        const texts = r.nodes.map((n) => every || one.lastSegment(n.key < C ? index.concepts[n.key].id : index.ghosts[n.key - C].id));
        const halves = r.nodes.map((n) => (n.dist === 0 ? 15 : 9));
        const placed = one.labels(r.nodes, texts, halves, viewport);
        assert(placed.length === one.CAP, `${placed.length} labels`);
        assert(JSON.stringify(placed) === JSON.stringify(other.labels(r.nodes, texts, halves, { width: viewport.width, height: viewport.height })), "two windows disagree");
        placed.forEach((p, k) => {
          const b = boxOf(p.text, p.x, p.y, p.anchor, k === 0);
          assert(b.x1 >= 3.9 && b.x2 <= viewport.width - 3.9 && b.y1 >= 3.9 && b.y2 <= viewport.height - 3.9, `${viewport.width} x ${viewport.height}, hops ${hops}: label ${k} leaves the view`);
        });
      }
    }
  });

  check("enlarged neighbourhood: build() and labels() refuse a viewport that is no finite size between the panel's and 4096", () => {
    const window = okfLocal();
    const { OkfLocal } = window;
    assert(OkfLocal.LARGE && OkfLocal.LARGE.max === 4096 && OkfLocal.LARGE.wideFrom === 560 && OkfLocal.LARGE.wideChars === 32 && Object.isFrozen(OkfLocal.LARGE),
      `LARGE: ${JSON.stringify(OkfLocal.LARGE)}`);
    const index = star(3, 0, 0, 4);
    const nodes = OkfLocal.build(index, 0, 1).nodes;
    const bad = [null, 0, "800x600", [800, 600], {}, { width: 800 }, { width: "800", height: 600 }, { width: NaN, height: 600 },
      { width: 800, height: Infinity }, { width: 297.99, height: 600 }, { width: 800, height: 247 }, { width: 4097, height: 600 },
      { width: 800, height: 1e308 }, { width: -800, height: 600 }];
    for (const viewport of bad) {
      for (const [what, call] of [["build", () => OkfLocal.build(index, 0, 1, viewport)], ["labels", () => OkfLocal.labels(nodes, ["a", "b", "c", "d"], [1, 1, 1, 1], viewport)]]) {
        let threw = null;
        try { call(); } catch (e) { threw = e; }
        assert(threw instanceof window.TypeError, `${what}(..., ${JSON.stringify(viewport)}) did not throw a TypeError`);
      }
    }
  });

  check("enlarged neighbourhood: OkfLocal stays pure with a viewport -- no DOM, no clock, no randomness (executed)", () => {
    const window = okfLocal();
    const trap = (what) => () => { throw new Error(`OkfLocal used ${what}`); };
    window.Math.random = trap("Math.random");
    window.Date.now = trap("Date.now");
    window.document.createElement = trap("document.createElement");
    window.document.createElementNS = trap("document.createElementNS");
    window.document.getElementById = trap("document.getElementById");
    window.document.querySelector = trap("document.querySelector");
    const index = star(5, 5, 1, 30);
    const viewport = { width: 700, height: 500 };
    const r = window.OkfLocal.build(index, 0, 2, viewport);
    window.OkfLocal.labels(r.nodes, r.nodes.map(() => "x".repeat(40)), r.nodes.map(() => 9), viewport);
  });
}

// A page whose modal drawing area measures `size` ({ width, height }, changed
// by assigning window.__okfArea): jsdom has no layout and measures 0 x 0.
function sized(size) {
  return (w) => {
    w.__okfArea = size;
    for (const prop of ["clientWidth", "clientHeight"]) {
      const own = Object.getOwnPropertyDescriptor(w.Element.prototype, prop);
      Object.defineProperty(w.HTMLElement.prototype, prop, {
        configurable: true,
        get() {
          if (this.classList && this.classList.contains("okf-local-modal-canvas")) {
            w.__okfReads = (w.__okfReads || 0) + 1;
            return prop === "clientWidth" ? w.__okfArea.width : w.__okfArea.height;
          }
          return own.get.call(this);
        },
      });
    }
  };
}

const served = (index) => ({ override: { "assets/okf-index.js": `window.OKF_INDEX = ${JSON.stringify(index)};` } });
const frames = (window, n) => new Promise((resolve) => {
  const step = (left) => (left === 0 ? resolve() : window.requestAnimationFrame(() => step(left - 1)));
  step(n);
});

function registerPage(h) {
  const { checkAsync, assert, openPage, navigations, key } = h;
  const dot = String.fromCharCode(0xb7);

  const open = (doc) => {
    doc.getElementById("okf-local-enlarge").click();
    return doc.getElementById("okf-local-modal");
  };
  const rowsOf = (items) => Array.from(items, (li) => [li.firstElementChild.localName, li.querySelector(".okf-local-id").textContent,
    li.querySelector(".okf-local-rel").textContent, li.firstElementChild.getAttribute("href")]);
  const modalRows = (doc) => rowsOf(doc.querySelectorAll("#okf-local-modal .okf-local-modal-rows > li"));
  const panelRows = (doc) => rowsOf(doc.querySelectorAll("#okf-local-list > ul > li"));

  checkAsync("enlarged neighbourhood: an icon button in the Neighbourhood head opens it, named, with aria-haspopup and aria-expanded", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const button = doc.getElementById("okf-local-enlarge");
    assert(button && button.localName === "button" && button.type === "button", "no #okf-local-enlarge button");
    const head = doc.querySelector("#okf-local-graph .okf-local-head");
    assert(button.parentElement === head && button.previousElementSibling === head.querySelector(".okf-hops"), "the button is not in the head, right after the depth toggle");
    assert(button.getAttribute("aria-label") === "Enlarge the neighbourhood", `name: ${button.getAttribute("aria-label")}`);
    assert(button.getAttribute("aria-haspopup") === "dialog" && button.getAttribute("aria-expanded") === "false", "aria-haspopup / aria-expanded");
    assert(button.classList.contains("okf-local-enlarge") && button.textContent === "", "the button is not an icon button");
    // The glyph: a constant of the fixed vocabulary (one svg, one path of M and L commands).
    const glyph = button.firstElementChild;
    assert(button.children.length === 1 && glyph.localName === "svg" && glyph.getAttribute("aria-hidden") === "true" && glyph.getAttribute("focusable") === "false",
      "the glyph is not one hidden, non-focusable svg");
    assertFixedSvg(assert, glyph);
    const path = glyph.querySelector("path");
    assert(glyph.children.length === 1 && path && /^[ML]\d+ \d+(?: [ML]\d+ \d+)*$/.test(path.getAttribute("d")), `glyph path: ${path && path.getAttribute("d")}`);
    assert(doc.getElementById("okf-local-modal") === null, "the modal exists before it is opened");
  });

  checkAsync("enlarged neighbourhood: it opens a modal dialog labelled by its title, focus inside, the rest of the page inert and unscrollable", async () => {
    const window = await openPage("p2-local/hub.html");
    const doc = window.document;
    const backdrop = open(doc);
    assert(backdrop && backdrop.parentElement === doc.body && !backdrop.hidden, "no shown #okf-local-modal under <body>");
    assert(backdrop.classList.contains("okf-local-backdrop"), "the modal is not the backdrop");
    const dialog = backdrop.querySelector(".okf-local-dialog");
    assert(dialog && dialog.getAttribute("role") === "dialog" && dialog.getAttribute("aria-modal") === "true", "role / aria-modal");
    const title = doc.getElementById(dialog.getAttribute("aria-labelledby"));
    assert(title && dialog.contains(title) && title.localName === "h2", "aria-labelledby names no title inside the dialog");
    // hub.md's title is markup: it stays text.
    assert(title.textContent === 'Neighbourhood of <img src=x onerror="window.__pwned=1">Hub', `title: ${title.textContent}`);
    assert(title.children.length === 0 && backdrop.querySelector("img") === null && window.__pwned === undefined, "the title became markup");
    assert(dialog.contains(doc.activeElement), `focus is on <${doc.activeElement.localName}>, outside the dialog`);
    assert(doc.getElementById("okf-local-enlarge").getAttribute("aria-expanded") === "true", "aria-expanded is not true");
    assert(doc.documentElement.hasAttribute("data-okf-modal-open"), "the page is not locked (data-okf-modal-open)");
    for (const child of Array.from(doc.body.children)) {
      if (child === backdrop || child.localName === "script") { continue; }
      assert(child.hasAttribute("inert") && child.getAttribute("aria-hidden") === "true", `<${child.localName} class="${child.className}"> is not inert and aria-hidden`);
    }
    assert(!backdrop.hasAttribute("inert") && !backdrop.hasAttribute("aria-hidden"), "the modal itself is inert");
  });

  checkAsync("enlarged neighbourhood: Tab and Shift+Tab stay in the dialog; Escape closes and gives focus back to the opener, restoring the page exactly", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    // A body child that was already aria-hidden keeps its own value after closing.
    const header = doc.querySelector("body > header");
    header.setAttribute("aria-hidden", "false");
    const backdrop = open(doc);
    const dialog = backdrop.querySelector(".okf-local-dialog");
    const stops = Array.from(dialog.querySelectorAll("button, a[href]"));
    assert(stops.length >= 5, `${stops.length} tab stops in the dialog`);
    const first = stops[0];
    const last = stops[stops.length - 1];
    assert(first.id === "okf-local-modal-hops-1", `first stop: ${first.id || first.textContent}`);
    // From the dialog itself (where focus lands), Tab goes to the first stop.
    assert(doc.activeElement === dialog, `focus on open: <${doc.activeElement.localName}>`);
    let e = key(window, dialog, { key: "Tab" });
    assert(e.defaultPrevented && doc.activeElement === first, "Tab from the dialog did not go to the first stop");
    e = key(window, first, { key: "Tab", shiftKey: true });
    assert(e.defaultPrevented && doc.activeElement === last, "Shift+Tab from the first stop did not wrap to the last");
    e = key(window, last, { key: "Tab" });
    assert(e.defaultPrevented && doc.activeElement === first, "Tab from the last stop did not wrap to the first");
    // Between two stops, Tab and Shift+Tab move to the next and previous one,
    // links of the list included (taken, so a browser that skips links on Tab
    // cannot leave the dialog).
    stops[1].focus();
    e = key(window, stops[1], { key: "Tab" });
    assert(e.defaultPrevented && doc.activeElement === stops[2], "Tab between two stops did not go to the next");
    e = key(window, stops[2], { key: "Tab", shiftKey: true });
    assert(e.defaultPrevented && doc.activeElement === stops[1], "Shift+Tab between two stops did not go to the previous");
    const link = stops.findIndex((s) => s.localName === "a");
    assert(link > 0, "this case needs a link among the stops");
    stops[link - 1].focus();
    key(window, stops[link - 1], { key: "Tab" });
    assert(doc.activeElement === stops[link], "Tab from the last button did not reach the first link");
    // Focus that escaped (to <body>) comes back in.
    doc.activeElement.blur();
    e = key(window, doc.body, { key: "Tab" });
    assert(e.defaultPrevented && doc.activeElement === first, "Tab from outside did not come back in");
    e = key(window, first, { key: "Escape" });
    assert(e.defaultPrevented && backdrop.hidden, "Escape did not close");
    assert(doc.activeElement === doc.getElementById("okf-local-enlarge"), `focus after Escape: <${doc.activeElement.localName} id="${doc.activeElement.id}">`);
    assert(doc.getElementById("okf-local-enlarge").getAttribute("aria-expanded") === "false", "aria-expanded is not false again");
    assert(!doc.documentElement.hasAttribute("data-okf-modal-open"), "the page stays locked");
    assert(header.getAttribute("aria-hidden") === "false", "a body child's own aria-hidden was not restored");
    for (const child of Array.from(doc.body.children)) {
      assert(!child.hasAttribute("inert"), `<${child.localName} class="${child.className}"> stays inert`);
      if (child !== header) { assert(!child.hasAttribute("aria-hidden"), `<${child.localName} class="${child.className}"> stays aria-hidden`); }
    }
    // Closed, the keys are the page's again.
    e = key(window, doc.body, { key: "Tab" });
    assert(!e.defaultPrevented, "Tab is still trapped after closing");
  });

  checkAsync("enlarged neighbourhood: a click on the backdrop or on Close closes it; a click inside the dialog does not", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const backdrop = open(doc);
    click(window, backdrop.querySelector(".okf-local-modal-title"));
    click(window, backdrop.querySelector(".okf-local-dialog"));
    assert(!backdrop.hidden, "a click inside the dialog closed it");
    click(window, backdrop);
    assert(backdrop.hidden && doc.activeElement === doc.getElementById("okf-local-enlarge"), "a click on the backdrop did not close it, focus on the opener");
    open(doc);
    const close = backdrop.querySelector("button.okf-local-modal-close");
    assert(close && close.textContent === "Close" && close.type === "button", "no Close button");
    close.click();
    assert(backdrop.hidden && doc.activeElement === doc.getElementById("okf-local-enlarge"), "Close did not close it, focus on the opener");
  });

  checkAsync("enlarged neighbourhood: opening twice is opening once; reopening reuses the one dialog and its listeners close it once", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const backdrop = open(doc);
    const focused = doc.getElementById("okf-local-modal-hops-2");
    focused.focus();
    const svgBefore = backdrop.querySelector(".okf-local-modal-canvas > svg");
    doc.getElementById("okf-local-enlarge").click();
    assert(doc.querySelectorAll("#okf-local-modal").length === 1 && doc.querySelectorAll(".okf-local-backdrop").length === 1, "a second dialog was built");
    assert(doc.activeElement === focused, "a second open moved the focus");
    assert(backdrop.querySelector(".okf-local-modal-canvas > svg") === svgBefore, "a second open redrew the dialog");
    key(window, doc.activeElement, { key: "Escape" });
    assert(backdrop.hidden, "one Escape did not close");
    // Nothing a second open did survives the close: the page is not left inert.
    for (const child of Array.from(doc.body.children)) {
      assert(!child.hasAttribute("inert") && !child.hasAttribute("aria-hidden"), `<${child.localName} class="${child.className}"> stays inert or aria-hidden after a double open`);
    }
    open(doc);
    assert(doc.querySelectorAll("#okf-local-modal").length === 1 && !backdrop.hidden, "reopening built another dialog");
    assert(backdrop.querySelectorAll(".okf-local-modal-canvas > svg").length === 1, "reopening left two drawings");
    key(window, doc.activeElement, { key: "Escape" });
    assert(backdrop.hidden && !doc.documentElement.hasAttribute("data-okf-modal-open"), "the reopened dialog did not close");
    // No listener left behind: Escape on the closed page changes nothing.
    const e = key(window, doc.body, { key: "Escape" });
    assert(!e.defaultPrevented, "a closed dialog still takes Escape");
  });

  checkAsync("enlarged neighbourhood: every listener opening adds to document and window is removed on closing", async () => {
    // Records the listeners added to and removed from document and window
    // after load (the page's own load-time listeners are not counted).
    let live = null;
    const beforeParse = (w) => {
      const add = w.EventTarget.prototype.addEventListener;
      const remove = w.EventTarget.prototype.removeEventListener;
      const keyOf = (target, type, fn, options) => [target, type, fn, Boolean(options === true || (options && options.capture))];
      const same = (a, b) => a.every((v, k) => v === b[k]);
      w.EventTarget.prototype.addEventListener = function (type, fn, options) {
        if (live && (this === w || this === w.document)) { live.push(keyOf(this, type, fn, options)); }
        return add.call(this, type, fn, options);
      };
      w.EventTarget.prototype.removeEventListener = function (type, fn, options) {
        if (live && (this === w || this === w.document)) {
          const k = keyOf(this, type, fn, options);
          const at = live.findIndex((l) => same(l, k));
          if (at !== -1) { live.splice(at, 1); }
        }
        return remove.call(this, type, fn, options);
      };
    };
    const window = await openPage("p2-local/c.html", { beforeParse });
    const doc = window.document;
    for (let round = 0; round < 2; round++) {
      live = [];
      open(doc);
      const added = live.map(([target, type, , capture]) => `${target === window ? "window" : "document"} ${type}${capture ? " (capture)" : ""}`).sort();
      assert(JSON.stringify(added) === JSON.stringify(["document keydown (capture)", "window resize"]), `round ${round}: opening added ${JSON.stringify(added)}`);
      key(window, doc.activeElement, { key: "Escape" });
      assert(live.length === 0, `round ${round}: closing left ${live.length} listener(s): ${live.map((l) => l[1]).join(", ")}`);
    }
    live = null;
  });

  checkAsync("enlarged neighbourhood: its depth toggle and the panel's are one state, both ways", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    const centre = positionOf(window, "p2-local/c");
    const shallow = expectedHood(window.OKF_INDEX, centre, 1);
    const deep = expectedHood(window.OKF_INDEX, centre, 2);
    assert(deep.total > shallow.total, "this case needs a second hop");
    const pressed = (prefix) => [1, 2].map((k) => doc.getElementById(prefix + k).getAttribute("aria-pressed")).join();
    const drawnIn = (selector) => doc.querySelectorAll(`${selector} svg g.okf-node`).length;
    const backdrop = open(doc);
    const group = backdrop.querySelector(".okf-hops");
    assert(group.getAttribute("role") === "group" && group.getAttribute("aria-label") === "Neighbourhood depth", "the dialog's toggle is not a named group");
    assert(pressed("okf-local-modal-hops-") === "true,false" && drawnIn("#okf-local-modal .okf-local-modal-canvas") === 1 + shallow.total, "the dialog does not open at the panel's depth");
    doc.getElementById("okf-local-modal-hops-2").click();
    assert(pressed("okf-local-modal-hops-") === "false,true" && pressed("okf-local-hops-") === "false,true", "2 hops in the dialog is not 2 hops in the panel");
    assert(drawnIn("#okf-local-modal .okf-local-modal-canvas") === 1 + deep.total && drawnIn("#okf-local-graph .okf-local-canvas") === 1 + deep.total, "both drawings are not at 2 hops");
    // The panel's toggle, while the dialog is open (a script or an assistive
    // technology can reach it; a reader closes first): the dialog follows.
    doc.getElementById("okf-local-hops-1").click();
    assert(pressed("okf-local-modal-hops-") === "true,false" && drawnIn("#okf-local-modal .okf-local-modal-canvas") === 1 + shallow.total, "the dialog did not follow the panel to 1 hop");
    doc.getElementById("okf-local-modal-hops-2").click();
    key(window, doc.activeElement, { key: "Escape" });
    assert(pressed("okf-local-hops-") === "false,true" && drawnIn("#okf-local-graph .okf-local-canvas") === 1 + deep.total, "the panel lost the dialog's depth on closing");
    open(doc);
    assert(pressed("okf-local-modal-hops-") === "false,true" && drawnIn("#okf-local-modal .okf-local-modal-canvas") === 1 + deep.total, "reopening forgot the depth");
  });

  checkAsync("enlarged neighbourhood: the same neighbours as the panel -- the list names all of them over the cap, +N omitted, Open in graph", async () => {
    const ids = ["p2-local/hub"];
    const links = [];
    for (let k = 0; k < 120; k++) { ids.push(`p2-local/n${String(k).padStart(3, "0")}`); links.push(k % 2 ? [k + 1, 0] : [0, k + 1]); }
    for (let k = 0; k < 10; k++) { ids.push(`p2-local/far${k}`); links.push([1 + k, 121 + k]); }
    const ghostIds = ["p2-local/zz-0", "p2-local/zz-1", "p2-local/zz-2"];
    ghostIds.forEach((_, g) => links.push([0, g, "ghost"]));
    const window = await openPage("p2-local/hub.html", served(siteIndex(ids, links, ghostIds)));
    const doc = window.document;
    for (const hops of [1, 2]) {
      doc.getElementById(`okf-local-hops-${hops}`).click();
      const backdrop = open(doc);
      const total = hops === 1 ? 123 : 133;
      const rows = modalRows(doc);
      assert(rows.length === total, `hops ${hops}: ${rows.length} rows for ${total} neighbours`);
      assert(JSON.stringify(rows) === JSON.stringify(panelRows(doc)), `hops ${hops}: the dialog's list is not the panel's`);
      assert(backdrop.querySelector(".okf-local-modal-rows").getAttribute("role") === "list", "the list lost its explicit role");
      const count = doc.getElementById("okf-local-modal-count");
      assert(count.textContent === `List ${dot} ${total} neighbours` && backdrop.querySelector(".okf-local-modal-rows").getAttribute("aria-labelledby") === "okf-local-modal-count", `count: ${count.textContent}`);
      assert(backdrop.querySelectorAll(".okf-local-modal-canvas > svg g.okf-node").length === 40, `hops ${hops}: not 40 nodes drawn`);
      const more = backdrop.querySelector(".okf-local-modal-omitted");
      assert(more && more.localName === "p" && more.textContent === `+${total - 39} omitted`, `hops ${hops}: omitted: ${more && more.textContent}`);
      const panelDrawn = Array.from(doc.querySelectorAll("#okf-local-graph svg g.okf-node > title"), (t) => t.textContent);
      const modalDrawn = Array.from(backdrop.querySelectorAll("svg g.okf-node > title"), (t) => t.textContent);
      assert(JSON.stringify(modalDrawn) === JSON.stringify(panelDrawn), `hops ${hops}: the dialog draws other nodes than the panel`);
      const link = doc.getElementById("okf-local-modal-open");
      assert(link && link.textContent === "Open in graph" && link.getAttribute("href") === doc.getElementById("okf-global-graph").getAttribute("href") && link.parentElement.classList.contains("okf-local-modal-foot"),
        "Open in graph is not the header's link, in the dialog's foot");
      assert(link.previousElementSibling.textContent === `solid = links to ${dot} dashed = referenced by`, "the foot lost the legend");
      key(window, doc.activeElement, { key: "Escape" });
    }
    // Under the cap: no note.
    const small = await openPage("p2-local/c.html");
    const backdrop = open(small.document);
    assert(backdrop.querySelector(".okf-local-modal-omitted") === null, "+N omitted under the cap");
  });

  checkAsync("enlarged neighbourhood: its drawing keeps the fixed vocabulary and ids of its own; a node click navigates, an absent one does not", async () => {
    const window = await openPage("p2-local/hub.html");
    const doc = window.document;
    const backdrop = open(doc);
    const picture = backdrop.querySelector(".okf-local-modal-canvas > svg");
    assertFixedSvg(assert, picture);
    assert(picture.getAttribute("role") === "img" && picture.getAttribute("focusable") === "false" && !picture.hasAttribute("tabindex"), "the drawing is not a non-focusable image");
    assert(/^Local graph of p2-local\/hub, 1 hop: \d+ of \d+ neighbours drawn; the list beside names them all$/.test(picture.getAttribute("aria-label")), `name: ${picture.getAttribute("aria-label")}`);
    assert(doc.querySelectorAll("#okf-local-arrow, #okf-local-arrow-in").length === 2 && doc.querySelectorAll("#okf-local-modal-arrow, #okf-local-modal-arrow-in").length === 2,
      "the arrow markers are not two in each drawing");
    const ids = Array.from(doc.querySelectorAll("[id]"), (n) => n.id);
    assert(new Set(ids).size === ids.length, `a duplicated id: ${ids.filter((v, k) => ids.indexOf(v) !== k)}`);
    for (const line of picture.querySelectorAll("line.okf-local-edge")) {
      assert(/^url\(#okf-local-modal-arrow(-in)?\)$/.test(line.getAttribute("marker-end")), `marker-end ${line.getAttribute("marker-end")}`);
    }
    let attempts = [];
    doc.addEventListener("okf:navigate", (e) => { attempts.push(e.detail.href); e.preventDefault(); });
    const ghosts = picture.querySelectorAll("g.okf-local-ghost");
    assert(ghosts.length > 0, "this case needs an absent concept");
    for (const g of ghosts) { click(window, g.lastElementChild); }
    assert(attempts.length === 0, "an absent concept navigated");
    const a = Array.from(picture.querySelectorAll("g.okf-local-node")).find((g) => g.firstElementChild.textContent === "p2-local/a");
    click(window, a.lastElementChild);
    assert(JSON.stringify(attempts) === JSON.stringify(["../" + window.OKF_INDEX.concepts[positionOf(window, "p2-local/a")].path]), `navigations: ${JSON.stringify(attempts)}`);
    // The panel's own drawing still navigates by its own nodes (each drawing has its own map).
    attempts = [];
    const panelA = Array.from(doc.querySelectorAll("#okf-local-graph svg g.okf-local-node")).find((g) => g.firstElementChild.textContent === "p2-local/a");
    click(window, panelA.lastElementChild);
    assert(attempts.length === 1 && navigations(window) === 0, `the panel's click after the dialog drew: ${JSON.stringify(attempts)}`);
  });

  checkAsync("enlarged neighbourhood: hostile ids and titles stay text in the dialog, its drawing and its list", async () => {
    const evil = "<img src=x onerror=window.__pwned=1>";
    const ids = ["__proto__", "a/__proto__", "constructor", `p/${evil}`, "toString"];
    const titles = ['<img src=x onerror="window.__pwned=2">Proto', "</title><script>window.__pwned=3</script>", "constructor", '"><svg onload="window.__pwned=4">', "toString"];
    const window = await openPage("__proto__.html", served(siteIndex(ids, [[0, 1], [0, 2], [3, 0], [4, 0], [1, 2], [0, 0, "ghost"]], [`gone/${evil}`], titles)));
    const doc = window.document;
    const backdrop = open(doc);
    doc.getElementById("okf-local-modal-hops-2").click();
    assert(doc.getElementById("okf-local-modal-title").textContent === `Neighbourhood of ${titles[0]}`, "the title is not the concept's title as text");
    assertFixedSvg(assert, backdrop.querySelector(".okf-local-modal-canvas > svg"));
    const drawn = Array.from(backdrop.querySelectorAll(".okf-local-modal-canvas > svg g.okf-node > title"), (t) => t.textContent);
    assert(drawn.includes(`p/${evil}`) && drawn.includes(`absent: gone/${evil}`), `drawn: ${JSON.stringify(drawn)}`);
    assert(backdrop.querySelectorAll("img, script, iframe, object, [onerror], [onload]").length === 0 && window.__pwned === undefined, "markup became live in the dialog");
    const listed = Array.from(backdrop.querySelectorAll(".okf-local-modal-rows .okf-local-id"), (s) => s.textContent);
    assert(JSON.stringify(listed) === JSON.stringify([...ids.slice(1), `absent: gone/${evil}`]), `listed: ${JSON.stringify(listed)}`);
  });

  checkAsync("enlarged neighbourhood: the palette's shortcuts do not open the palette over it", async () => {
    const window = await openPage("p2-local/c.html");
    const doc = window.document;
    open(doc);
    const slash = key(window, doc.activeElement, { key: "/" });
    const ctrlK = key(window, doc.activeElement, { key: "k", ctrlKey: true });
    assert(slash.defaultPrevented && ctrlK.defaultPrevented, "the shortcuts were not prevented");
    assert(doc.querySelector("body > .okf-palette-backdrop").hidden, "the palette opened over the dialog");
    assert(!doc.getElementById("okf-local-modal").hidden, "the dialog closed");
    key(window, doc.activeElement, { key: "Escape" });
    // Closed: the palette's "/" works again.
    key(window, doc.body, { key: "/" });
    assert(!doc.querySelector("body > .okf-palette-backdrop").hidden, "the palette no longer opens once the dialog is closed");
  });

  checkAsync("enlarged neighbourhood: the drawing is laid out for its area -- svg size and viewBox, 32-character labels in a wide one -- and again after a resize", async () => {
    const long = "p2-local/" + "w".repeat(40);
    const index = siteIndex(["p2-local/hub", long, "p2-local/b"], [[0, 1], [2, 0]]);
    const window = await openPage("p2-local/hub.html", Object.assign(served(index), { beforeParse: sized({ width: 798, height: 624 }) }));
    const doc = window.document;
    const backdrop = open(doc);
    let picture = backdrop.querySelector(".okf-local-modal-canvas > svg");
    assert(picture.getAttribute("viewBox") === "0 0 798 624" && picture.getAttribute("width") === "798" && picture.getAttribute("height") === "624",
      `svg: ${picture.getAttribute("viewBox")}, ${picture.getAttribute("width")} x ${picture.getAttribute("height")}`);
    const labelOf = (p) => Array.from(p.querySelectorAll("text.okf-local-label")).map((t) => t.textContent).find((t) => t.startsWith("www"));
    assert(labelOf(picture) === "w".repeat(31) + ELLIPSIS, `wide label: ${labelOf(picture)}`);
    // The positions are build()'s for that viewport.
    const r = window.OkfLocal.build(window.OKF_INDEX, 0, 1, { width: 798, height: 624 });
    const drawnAt = Array.from(picture.querySelectorAll("g.okf-node"), (g) => g.querySelector(":scope > :not(title):not(text):not(.okf-node-ring):not(.okf-node-focus)"));
    const centreOf = (shape) => (shape.localName === "circle" ? [Number(shape.getAttribute("cx")), Number(shape.getAttribute("cy"))]
      : shape.localName === "rect" ? [Number(shape.getAttribute("x")) + Number(shape.getAttribute("width")) / 2, Number(shape.getAttribute("y")) + Number(shape.getAttribute("height")) / 2] : null);
    drawnAt.forEach((shape, k) => {
      const c = centreOf(shape);
      if (c) { assert(Math.abs(c[0] - r.nodes[k].x) < 0.02 && Math.abs(c[1] - r.nodes[k].y) < 0.02, `node ${k} drawn at (${c}), laid out at (${r.nodes[k].x}, ${r.nodes[k].y})`); }
    });
    // A resize to a narrow area: one layout per frame, at the new size.
    let drawings = 0;
    const observer = new window.MutationObserver((records) => { for (const rec of records) { drawings += Array.from(rec.addedNodes).filter((n) => n.localName === "svg").length; } });
    observer.observe(backdrop.querySelector(".okf-local-modal-canvas"), { childList: true });
    window.__okfArea = { width: 372, height: 370 };
    window.__okfReads = 0;
    window.dispatchEvent(new window.Event("resize"));
    window.dispatchEvent(new window.Event("resize"));
    await frames(window, 2);
    await Promise.resolve();
    picture = backdrop.querySelector(".okf-local-modal-canvas > svg");
    assert(picture.getAttribute("viewBox") === "0 0 372 370", `after the resize: ${picture.getAttribute("viewBox")}`);
    assert(labelOf(picture) === "w".repeat(19) + ELLIPSIS, `narrow label: ${labelOf(picture)}`);
    assert(drawings === 1, `${drawings} drawings for two resize events in one frame`);
    // Debounced: two resize events in one frame measure the area once (its width and its height).
    assert(window.__okfReads === 2, `${window.__okfReads} reads of the area's size for two resize events in one frame`);
    // The same size again: nothing redrawn.
    window.dispatchEvent(new window.Event("resize"));
    await frames(window, 2);
    await Promise.resolve();
    assert(drawings === 1, "a resize to the same size redrew");
    observer.disconnect();
    // An area smaller than the panel's view lays out at the panel's (the viewBox scales it down).
    window.__okfArea = { width: 200, height: 120 };
    window.dispatchEvent(new window.Event("resize"));
    await frames(window, 2);
    picture = backdrop.querySelector(".okf-local-modal-canvas > svg");
    assert(picture.getAttribute("viewBox") === "0 0 298 248", `a small area: ${picture.getAttribute("viewBox")}`);
    // Closed, a resize redraws nothing.
    key(window, doc.activeElement, { key: "Escape" });
    window.__okfArea = { width: 900, height: 700 };
    window.dispatchEvent(new window.Event("resize"));
    await frames(window, 2);
    assert(backdrop.querySelector(".okf-local-modal-canvas > svg") === null, "a resize drew into the closed dialog");
  });
}

// X12's rules are anchored to #okf-local-modal (or #okf-context for the
// opener); the real dialog keeps its style, body <code> wearing the classes
// stays plain (p2-chrome-classes.md, run by run.js's three-part guard).
function registerChrome(h) {
  const { checkAsync, assert, openPage, key } = h;

  checkAsync("enlarged neighbourhood: its rules style the real opener and dialog (anchored to #okf-context and #okf-local-modal)", async () => {
    const window = await openPage("p2-local/hub.html");
    const doc = window.document;
    const css = (el, prop) => window.getComputedStyle(el).getPropertyValue(prop);
    const enlarge = doc.getElementById("okf-local-enlarge");
    for (const [prop, value] of [["width", "24px"], ["height", "24px"], ["cursor", "pointer"]]) {
      assert(css(enlarge, prop) === value, `the opener's ${prop} is ${css(enlarge, prop)}, not ${value}`);
    }
    assert(css(doc.querySelector("#okf-local-graph .okf-hops"), "margin-left") === "auto", "the depth toggle is not pushed right of the title");
    doc.getElementById("okf-local-enlarge").click();
    const backdrop = doc.getElementById("okf-local-modal");
    const probes = [
      [backdrop, "position", "fixed"], [backdrop, "z-index", "10"], [backdrop, "display", "flex"],
      [backdrop.querySelector(".okf-local-dialog"), "display", "flex"],
      [backdrop.querySelector(".okf-local-dialog"), "flex-direction", "column"],
      [backdrop.querySelector(".okf-local-modal-canvas"), "position", "relative"],
      [backdrop.querySelector(".okf-local-modal-canvas > svg"), "position", "absolute"],
      [backdrop.querySelector(".okf-local-modal-rows"), "overflow-y", "auto"],
      [backdrop.querySelector(".okf-local-modal-foot"), "font-size", "12px"],
      [doc.getElementById("okf-local-modal-hops-1"), "height", "24px"],
      [doc.getElementById("okf-local-modal-hops-1"), "font-size", "12px"],
      [doc.getElementById("okf-local-modal-count"), "text-transform", "uppercase"],
      [backdrop.querySelector(".okf-local-modal-rows a.okf-row"), "font-size", "13.5px"],
      [backdrop.querySelector(".okf-local-modal-canvas text.okf-local-label:not(.okf-local-label-center)"), "font-size", "10px"],
      [backdrop.querySelector(".okf-local-modal-canvas text.okf-local-label-center"), "font-size", "10.5px"],
      [backdrop.querySelector(".okf-local-modal-canvas .okf-local-edge"), "stroke", "var(--edge)"],
      [backdrop.querySelector(".okf-local-modal-canvas .okf-local-node"), "cursor", "pointer"],
      [doc.documentElement, "overflow", "hidden"],
    ];
    for (const [el, prop, value] of probes) {
      assert(el, `an element of the probe on ${prop} is missing`);
      assert(css(el, prop) === value, `<${el.localName} class="${el.getAttribute("class")}"> ${prop}: ${css(el, prop)}, not ${value}`);
    }
    key(window, doc.activeElement, { key: "Escape" });
    assert(css(backdrop, "display") === "none" && css(doc.documentElement, "overflow") !== "hidden", "closed, the dialog shows or the page stays locked");
  });

  // The dialog shares the panel's rules instead of restating them (spec
  // §12.6, written once): the shared components list it among their containers
  // and P2's toggle, row text, foot link and drawing paint are anchored to
  // :is(#okf-context, #okf-local-modal). So the same element kind computes the
  // same in both places (equal to the panel's, and to the literal pinned here,
  // which a declaration changed in the one shared rule moves too; jsdom drops a
  // shorthand holding var(), so borders, backgrounds and fill: none are pinned
  // by the real-browser comparison, not here), and only the
  // real differences (a section title's margin, sizes) are the dialog's own.
  checkAsync("enlarged neighbourhood: the shared rules compute alike in the dialog and the panel, and a hidden part stays hidden", async () => {
    const window = await openPage("p2-local/hub.html");
    const doc = window.document;
    const css = (el, prop) => window.getComputedStyle(el).getPropertyValue(prop);
    doc.getElementById("okf-local-enlarge").click();
    const modal = doc.getElementById("okf-local-modal");
    const panel = doc.getElementById("okf-local-graph");
    const one = (root, sel, what) => {
      const el = root.querySelector(sel);
      assert(el, `${what}: nothing matches ${sel}`);
      return el;
    };
    // [what, selector under the panel, selector under the dialog, [[prop, literal]...]]
    const table = [
      ["section title", ".okf-section-title", "#okf-local-modal-count", [
        ["font-family", "var(--mono)"], ["font-size", "11px"], ["font-weight", "400"], ["letter-spacing", "0.06em"],
        ["line-height", "1.4"], ["text-transform", "uppercase"], ["color", "var(--gray)"]]],
      ["link row", ".okf-local-list a.okf-row", ".okf-local-modal-rows a.okf-row", [
        ["display", "flex"], ["align-items", "center"], ["gap", "9px"], ["padding-top", "6px"], ["padding-bottom", "6px"],
        ["min-width", "0px"], ["font-size", "13.5px"], ["line-height", "normal"], ["color", "var(--blue)"], ["text-decoration", "none"], ["overflow-wrap", "anywhere"]]],
      ["absent row", ".okf-local-list .okf-row.okf-local-absent", ".okf-local-modal-rows .okf-row.okf-local-absent", [
        ["display", "flex"], ["gap", "9px"], ["font-size", "13.5px"], ["color", "var(--ghost)"]]],
      ["row glyph", ".okf-local-list a.okf-row svg.okf-glyph", ".okf-local-modal-rows a.okf-row svg.okf-glyph", [
        ["flex-grow", "0"], ["flex-shrink", "0"], ["display", "block"], ["overflow", "visible"]]],
      ["row id", ".okf-local-list a.okf-row .okf-local-id", ".okf-local-modal-rows a.okf-row .okf-local-id", [
        ["flex-grow", "1"], ["flex-shrink", "1"], ["min-width", "0px"], ["overflow-wrap", "anywhere"]]],
      ["row relation", ".okf-local-list a.okf-row .okf-local-rel", ".okf-local-modal-rows a.okf-row .okf-local-rel", [
        ["flex-grow", "0"], ["flex-shrink", "0"], ["max-width", "50%"], ["margin-left", "auto"], ["text-align", "right"],
        ["overflow-wrap", "anywhere"], ["font-size", "12px"], ["color", "var(--gray)"]]],
      ["depth group", ".okf-hops", ".okf-hops", [["display", "flex"], ["flex-grow", "0"], ["flex-shrink", "0"]]],
      ["depth button", ".okf-hops button[aria-pressed=false]", ".okf-hops button[aria-pressed=false]", [
        ["height", "24px"], ["padding-left", "10px"], ["padding-right", "10px"], ["font-size", "12px"], ["cursor", "pointer"],
        ["color", "var(--gray)"]]],
      ["second depth button", ".okf-hops button + button", ".okf-hops button + button", [["border-left-width", "0px"]]],
      ["pressed depth button", ".okf-hops button[aria-pressed=true]", ".okf-hops button[aria-pressed=true]", [
        ["color", "var(--white)"], ["font-weight", "600"]]],
      ["foot link", ".okf-local-foot a", ".okf-local-modal-foot a", [
        ["flex-grow", "0"], ["flex-shrink", "0"], ["font-weight", "600"], ["color", "var(--blue)"], ["text-decoration", "none"]]],
      ["edge", "svg .okf-local-edge", "svg .okf-local-edge", [["stroke", "var(--edge)"]]],
      ["arrowhead", "svg .okf-local-arrowhead", "svg .okf-local-arrowhead", [["fill", "var(--edge)"]]],
      ["label", "svg .okf-local-label:not(.okf-local-label-center):not(.okf-local-label-ghost)", "svg .okf-local-label:not(.okf-local-label-center):not(.okf-local-label-ghost)", [
        ["font-family", "var(--mono)"], ["font-size", "10px"], ["fill", "var(--ink)"], ["stroke", "var(--white)"],
        ["stroke-width", "3px"], ["stroke-linejoin", "round"], ["paint-order", "stroke"]]],
      ["centre label", "svg .okf-local-label-center", "svg .okf-local-label-center", [["font-size", "10.5px"], ["font-weight", "700"]]],
      ["ghost label", "svg .okf-local-label-ghost", "svg .okf-local-label-ghost", [["fill", "var(--ghost)"]]],
      ["node", "svg .okf-local-node", "svg .okf-local-node", [["cursor", "pointer"]]],
    ];
    const bad = [];
    for (const [what, inPanel, inDialog, props] of table) {
      const p = one(panel, inPanel, `the panel's ${what}`);
      const m = one(modal, inDialog, `the dialog's ${what}`);
      for (const [prop, literal] of props) {
        if (css(m, prop) !== literal) { bad.push(`the dialog's ${what} computes ${prop}: ${css(m, prop)}, not ${literal}`); }
        if (css(p, prop) !== css(m, prop)) { bad.push(`the ${what} computes ${prop} ${css(p, prop)} in the panel, ${css(m, prop)} in the dialog`); }
      }
    }
    assert(bad.length === 0, bad.join("; "));
    // The dialog's own differences stay its own.
    assert(css(one(modal, "#okf-local-modal-count", "the count"), "margin-bottom") === "6px", "the dialog's section title keeps its own 6px margin");
    // [hidden] beats the author display of a shared component, here too.
    for (const sel of [".okf-local-modal-rows a.okf-row", ".okf-local-modal-rows svg.okf-glyph"]) {
      const el = one(modal, sel, "a hideable part");
      el.setAttribute("hidden", "");
      assert(css(el, "display") === "none", `a hidden ${sel} in the dialog still computes display ${css(el, "display")}`);
      el.removeAttribute("hidden");
    }
  });

  checkAsync("enlarged neighbourhood: p2-chrome-classes lists every class the dialog and its opener wear", async () => {
    const window = await openPage("p2-local/hub.html");
    const doc = window.document;
    doc.getElementById("okf-local-enlarge").click();
    const worn = new Set();
    for (const root of [doc.getElementById("okf-local-enlarge"), doc.getElementById("okf-local-modal")]) {
      for (const el of [root, ...root.querySelectorAll("*")]) {
        for (const c of Array.from(el.classList)) { worn.add(c); }
      }
    }
    const read = (name) => fs.readFileSync(path.join(__dirname, "..", "fixtures", "hostile-bundle", name), "utf8");
    const listed = (text) => new Set((text.match(/class="([^"]*)"/g) || []).flatMap((m) => m.slice(7, -1).split(/\s+/)));
    const p2 = listed(read("p2-chrome-classes.md"));
    // The shapes' and shared components' own classes are P1.1's, listed in p11-chrome-classes.md.
    const p11 = listed(read("p11-chrome-classes.md"));
    // okf-shape-N (§12.2) are anchored by their svg ancestor; P1.1 lists two of them.
    const missing = Array.from(worn).filter((c) => !p2.has(c) && !p11.has(c) && !/^okf-shape-[0-5]$/.test(c));
    assert(missing.length === 0, `classes the dialog wears that no chrome-classes page lists: ${missing.join(", ")}`);
  });
}

function register(h) {
  registerPure(h);
  registerPage(h);
  registerChrome(h);
}

module.exports = { register, defaultBattery, star };
