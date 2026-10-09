// SPDX-License-Identifier: LGPL-3.0-or-later
//
// P4 (resizable side panel) cases for tools/viewer-security-check: the
// splitter okf-resize.js puts between the middle column and the right-hand
// column of a concept page (context panel) and of the graph page (drawer),
// and the graph page's refit when its canvas changes size (okf-graph.js).
// run.js calls register(h) with its frozen helpers.
//
// jsdom has no layout, no pointer capture and applies no @media rule: each
// case gives the page the geometry it needs (a layout width, the widths of the
// fixed columns), a matchMedia answering the 1 100 px query, a ResizeObserver
// it can fire, and a recording setPointerCapture, all before any script runs.
// What a real browser does with them (a drag by the pointer's delta, the
// clamps against a real layout, the persistence across file:// pages, the
// refit of a drawing) is measured by recette/p4.js.
"use strict";
const fs = require("fs");
const path = require("path");

const ASSETS = path.join(__dirname, "..", "..", "..", "src", "OKF4net.Viewer", "Assets");
const KEY = "okf-context-w";
const VAR = "--okf-context-w";
const WIDE = "(min-width: 1100px)";

// The page and its stand-in browser. `layoutW` and the fixed columns' widths
// are what getBoundingClientRect answers; `wide` is the 1 100 px query.
function stage(init = {}) {
  const st = Object.assign({
    layoutW: 1440, explorerW: 290, facetsW: 270, wide: true,
    queries: new Map(), observers: [], captured: [], released: [], stored: null, capture: true,
  }, init);
  st.setWide = (value) => {
    st.wide = value;
    const q = st.queries.get(WIDE);
    if (!q) { return; }
    q.matches = value;
    q.listeners.slice().forEach((fn) => fn({ matches: value, media: WIDE }));
  };
  // Runs the callbacks of every ResizeObserver the page made.
  st.fireObservers = () => st.observers.slice().forEach((o) => o.callback([], o));
  st.install = (w) => {
    w.Element.prototype.getBoundingClientRect = function () {
      let width = 0;
      if (this.classList.contains("okf-layout") || this.classList.contains("okf-graph-layout")) { width = st.layoutW; }
      else if (this.id === "okf-explorer" && !this.hidden) { width = st.explorerW; }
      else if (this.id === "okf-facets") { width = st.facetsW; }
      return { x: 0, y: 0, left: 0, top: 0, right: width, bottom: 0, width, height: 0 };
    };
    w.matchMedia = (text) => {
      if (!st.queries.has(text)) {
        const q = { media: text, matches: text === WIDE ? st.wide : false, listeners: [] };
        q.addEventListener = (type, fn) => { if (type === "change") { q.listeners.push(fn); } };
        q.removeEventListener = (type, fn) => { q.listeners = q.listeners.filter((f) => f !== fn); };
        q.addListener = (fn) => { q.listeners.push(fn); };
        q.removeListener = q.removeEventListener.bind(null, "change");
        st.queries.set(text, q);
      }
      return st.queries.get(text);
    };
    w.ResizeObserver = class {
      constructor(callback) { this.callback = callback; this.targets = []; st.observers.push(this); }
      observe(target) { this.targets.push(target); }
      unobserve() {}
      disconnect() { st.observers = st.observers.filter((o) => o !== this); }
    };
    if (st.capture) {
      w.Element.prototype.setPointerCapture = function (id) { st.captured.push(id); };
      w.Element.prototype.releasePointerCapture = function (id) { st.released.push(id); };
      w.Element.prototype.hasPointerCapture = function () { return false; };
    }
    if (st.stored !== null) { w.localStorage.setItem(KEY, st.stored); }
  };
  return st;
}

async function openConcept(h, st, opts = {}) {
  const before = opts.beforeParse;
  const window = await h.openPage(opts.page || "foo.html", Object.assign({}, opts, {
    beforeParse(w) { st.install(w); if (before) { before(w); } },
  }));
  const doc = window.document;
  const tick = () => new Promise((resolve) => window.setTimeout(resolve, 0));
  await tick();
  return { window, doc, tick, layout: doc.querySelector("body > .okf-layout"), panel: doc.getElementById("okf-context"), handle: doc.querySelector("body > .okf-layout > .okf-splitter") };
}

// The graph page, its frame callbacks held by the case.
async function openGraph(h, st, opts = {}) {
  const index = await h.openPage("index.html");
  const rel = index.document.getElementById("okf-global-graph").getAttribute("href").split("#")[0];
  const queue = [];
  const before = opts.beforeParse;
  const window = await h.openPage(rel, Object.assign({}, opts, {
    beforeParse(w) {
      st.install(w);
      w.OKF_SCHEDULER = (callback) => { queue.push(callback); };
      if (before) { before(w); }
    },
  }));
  const doc = window.document;
  const tick = () => new Promise((resolve) => window.setTimeout(resolve, 0));
  await tick();
  const flush = () => { let k = 0; while (queue.length > 0) { if (k++ > 100000) { throw new Error("the layout did not end"); } queue.shift()(); } };
  return { window, doc, tick, flush, layout: doc.getElementById("okf-graph-layout"), panel: doc.getElementById("okf-graph-detail"), handle: doc.querySelector("body > .okf-graph-layout > .okf-splitter") };
}

function now(page) {
  const { handle, layout, window } = page;
  return {
    valuenow: Number(handle.getAttribute("aria-valuenow")),
    valuemin: Number(handle.getAttribute("aria-valuemin")),
    valuemax: Number(handle.getAttribute("aria-valuemax")),
    variable: layout.style.getPropertyValue(VAR),
    custom: layout.hasAttribute("data-okf-resized"),
    stored: window.localStorage.getItem(KEY),
    hidden: handle.hidden,
  };
}

function pointer(window, target, type, x, extra = {}) {
  const init = Object.assign({ bubbles: true, cancelable: true, clientX: x, clientY: 300, button: 0, buttons: type === "pointerup" ? 0 : 1, pointerId: 1, pointerType: "mouse" }, extra);
  const event = new window.PointerEvent(type, init);
  target.dispatchEvent(event);
  return event;
}

function register(h) {
  const { assert } = h;

  // --- structure and ARIA ----------------------------------------------------

  h.checkAsync("resize: a concept page has the splitter between the middle column and the context panel, with the window-splitter ARIA", async () => {
    const st = stage();
    const page = await openConcept(h, st);
    const { doc, handle, panel } = page;
    assert(panel && !panel.hidden, "setup: the fixture page has no visible context panel");
    assert(handle, "no .okf-splitter directly under the layout");
    assert(handle.previousElementSibling === doc.getElementById("okf-main"), "the splitter does not follow <main>");
    assert(handle.nextElementSibling === panel, "the splitter does not precede the context panel");
    const attr = (name) => handle.getAttribute(name);
    assert(attr("role") === "separator", `role is ${attr("role")}`);
    assert(attr("aria-orientation") === "vertical", `aria-orientation is ${attr("aria-orientation")}`);
    assert(attr("aria-controls") === "okf-context", `aria-controls is ${attr("aria-controls")}`);
    assert(attr("aria-label") === "Resize the side panel", `aria-label is ${attr("aria-label")}`);
    assert(attr("tabindex") === "0", `tabindex is ${attr("tabindex")}`);
    const s = now(page);
    assert(s.valuemin === 240 && s.valuenow === 340 && s.valuemax === 720, `value min/now/max ${s.valuemin}/${s.valuenow}/${s.valuemax}, expected 240/340/720`);
    assert(attr("aria-valuetext") === "340 px", `aria-valuetext is ${attr("aria-valuetext")}`);
    assert(!s.custom && s.variable === "" && s.stored === null, "a page with no stored width carries a custom width");
    assert(handle.hidden === false, "the splitter is hidden beside a visible panel");
    assert(doc.querySelectorAll(".okf-splitter").length === 1, "more than one splitter");
  });

  h.checkAsync("resize: the graph page has the splitter between the canvas and the drawer, with the same ARIA", async () => {
    const st = stage();
    const page = await openGraph(h, st);
    const { doc, handle, panel } = page;
    assert(handle, "no .okf-splitter directly under the graph layout");
    assert(handle.previousElementSibling === doc.getElementById("okf-main"), "the splitter does not follow <main>");
    assert(handle.nextElementSibling === panel, "the splitter does not precede the drawer");
    const attr = (name) => handle.getAttribute(name);
    assert(attr("role") === "separator" && attr("aria-orientation") === "vertical", "role / orientation");
    assert(attr("aria-controls") === "okf-graph-detail", `aria-controls is ${attr("aria-controls")}`);
    assert(attr("aria-label") === "Resize the side panel" && attr("tabindex") === "0", "label / tabindex");
    const s = now(page);
    assert(s.valuemin === 240 && s.valuenow === 340 && s.valuemax === 720, `value min/now/max ${s.valuemin}/${s.valuenow}/${s.valuemax}`);
    assert(!doc.getElementById("okf-facets").contains(handle), "the facets column holds a splitter: it is not resizable");
    assert(doc.querySelectorAll(".okf-splitter").length === 1, "more than one splitter");
  });

  h.checkAsync("resize: the splitter is hidden with the panel it controls and back with it", async () => {
    const st = stage();
    const page = await openConcept(h, st);
    const { panel, handle, tick } = page;
    panel.hidden = true;
    await tick();
    assert(handle.hidden, "the splitter stays when the panel is hidden");
    panel.hidden = false;
    await tick();
    assert(!handle.hidden, "the splitter did not come back with the panel");
    // A page whose context panel starts hidden (nothing to list): the same.
    const bare = await openConcept(h, stage(), { page: "p3-graph/isolated.html" });
    assert(bare.panel.hidden, "setup: p3-graph/isolated.html has a visible context panel");
    assert(bare.handle.hidden, "a hidden panel starts with a visible splitter");
  });

  // --- keyboard --------------------------------------------------------------

  const keys = async (label, open) => {
    const st = stage();
    const page = await open(st);
    const { window, doc, handle } = page;
    const press = (key, extra) => h.key(window, handle, Object.assign({ key }, extra));
    let e = press("ArrowLeft");
    let s = now(page);
    assert(e.defaultPrevented, `${label}: ArrowLeft was not taken`);
    assert(s.valuenow === 356 && s.variable === "356px" && s.custom && s.stored === "356", `${label}: ArrowLeft gave ${JSON.stringify(s)}`);
    press("ArrowLeft", { shiftKey: true });
    s = now(page);
    assert(s.valuenow === 420 && s.variable === "420px" && s.stored === "420", `${label}: Shift+ArrowLeft gave ${JSON.stringify(s)}`);
    press("ArrowRight");
    assert(now(page).valuenow === 404, `${label}: ArrowRight gave ${now(page).valuenow}`);
    press("ArrowRight", { shiftKey: true });
    assert(now(page).valuenow === 340, `${label}: Shift+ArrowRight gave ${now(page).valuenow}`);
    press("Home");
    s = now(page);
    assert(s.valuenow === 240 && s.variable === "240px" && s.stored === "240", `${label}: Home gave ${JSON.stringify(s)}`);
    press("ArrowRight", { shiftKey: true });
    assert(now(page).valuenow === 240, `${label}: the minimum was passed: ${now(page).valuenow}`);
    press("End");
    s = now(page);
    assert(s.valuenow === 720 && s.variable === "720px" && s.stored === "720", `${label}: End gave ${JSON.stringify(s)}`);
    press("ArrowLeft", { shiftKey: true });
    assert(now(page).valuenow === 720 && now(page).stored === "720" && now(page).variable === "720px", `${label}: past the maximum: ${JSON.stringify(now(page))}`);
    e = press("Enter");
    s = now(page);
    assert(e.defaultPrevented, `${label}: Enter was not taken`);
    assert(s.valuenow === 340 && s.variable === "" && !s.custom && s.stored === null, `${label}: Enter did not reset: ${JSON.stringify(s)}`);
    assert(handle.getAttribute("aria-valuetext") === "340 px", `${label}: aria-valuetext after reset ${handle.getAttribute("aria-valuetext")}`);
    // Keys that are not the splitter's, and the browser's own shortcuts.
    for (const [key, extra] of [["Tab", {}], ["a", {}], ["ArrowUp", {}], ["ArrowLeft", { altKey: true }], ["ArrowLeft", { ctrlKey: true }], ["ArrowLeft", { metaKey: true }], ["Home", { ctrlKey: true }]]) {
      const ev = press(key, extra);
      assert(!ev.defaultPrevented && now(page).valuenow === 340 && now(page).stored === null, `${label}: ${JSON.stringify(extra)} ${key} was taken`);
    }
    // Focus elsewhere: the same keys do nothing.
    for (const target of [doc.body, doc.getElementById("okf-main")]) {
      const ev = h.key(window, target, { key: "ArrowLeft" });
      assert(!ev.defaultPrevented && now(page).valuenow === 340, `${label}: ArrowLeft on <${target.tagName.toLowerCase()}> moved the splitter`);
    }
  };
  h.checkAsync("resize: keyboard on a concept page -- arrows +-16 (Shift +-64), Home/End to the bounds, Enter resets; other keys and other targets untouched", () => keys("concept", (st) => openConcept(h, st)));
  h.checkAsync("resize: keyboard on the graph page -- the same", () => keys("graph", (st) => openGraph(h, st)));

  h.checkAsync("resize: a double click resets the width", async () => {
    const st = stage();
    const page = await openConcept(h, st);
    h.key(page.window, page.handle, { key: "End" });
    assert(now(page).valuenow === 720, "setup");
    page.handle.dispatchEvent(new page.window.MouseEvent("dblclick", { bubbles: true, cancelable: true }));
    const s = now(page);
    assert(s.valuenow === 340 && s.variable === "" && !s.custom && s.stored === null, `double click gave ${JSON.stringify(s)}`);
  });

  // --- persistence -----------------------------------------------------------

  for (const [label, open] of [["concept page", (st) => openConcept(h, st)], ["graph page", (st) => openGraph(h, st)]]) {
    h.checkAsync(`resize: ${label} -- a stored width is restored as the CSS variable on the layout and the splitter's value`, async () => {
      const st = stage({ stored: "480" });
      const page = await open(st);
      const s = now(page);
      assert(s.variable === "480px" && s.custom && s.valuenow === 480, `restored ${JSON.stringify(s)}`);
      assert(s.stored === "480", "restoring rewrote the stored width");
      // The two page kinds share the key: a width set on one is read by the other.
      h.key(page.window, page.handle, { key: "ArrowLeft" });
      assert(page.window.localStorage.getItem(KEY) === "496", "the key is not the shared okf-context-w");
    });
  }

  h.checkAsync("resize: with storage denied the page works and the width still changes (not persisted)", async () => {
    const st = stage();
    const page = await openConcept(h, st, { storage: "denied" });
    h.key(page.window, page.handle, { key: "ArrowLeft", shiftKey: true });
    const s = { valuenow: Number(page.handle.getAttribute("aria-valuenow")), variable: page.layout.style.getPropertyValue(VAR) };
    assert(s.valuenow === 404 && s.variable === "404px", `with storage denied: ${JSON.stringify(s)}`);
    h.key(page.window, page.handle, { key: "Enter" });
    assert(page.layout.style.getPropertyValue(VAR) === "", "reset failed with storage denied");
    const graph = await openGraph(h, stage(), { storage: "denied" });
    assert(graph.handle, "the graph page lost its splitter with storage denied");
  });

  h.checkAsync("resize: hostile stored values are ignored (NaN, text, negative, huge, decimals, hex, exponent, spaces, out of range, __proto__)", async () => {
    const hostile = ["NaN", "abc", "-50", "99999999999999999999", "__proto__", "constructor", "0", "239", "721", "3e2", "340.5", " 400", "400 ", "0x190", "", "Infinity", "400px", "400;--x:1", "4 0 0", "+400", "00400"];
    for (const value of hostile) {
      const st = stage({ stored: value });
      const page = await openConcept(h, st);
      const s = now(page);
      assert(s.variable === "" && !s.custom && s.valuenow === 340, `stored ${JSON.stringify(value)} was used: ${JSON.stringify(s)}`);
      assert(page.window.localStorage.getItem(KEY) === value, `stored ${JSON.stringify(value)} was rewritten on load`);
    }
    for (const [value, expected] of [["240", 240], ["720", 720], ["500", 500]]) {
      const page = await openConcept(h, stage({ stored: value }));
      assert(now(page).valuenow === expected && now(page).variable === `${expected}px`, `stored ${value} was not restored`);
    }
  });

  // --- breakpoint and bounds -------------------------------------------------

  h.checkAsync("resize: below 1100 px the splitter is hidden and the stored width is ignored; wide again, both come back", async () => {
    const st = stage({ stored: "480", wide: false });
    const page = await openConcept(h, st);
    assert(page.handle.hidden, "the splitter shows below the breakpoint");
    let s = now(page);
    assert(s.variable === "" && !s.custom, `the stored width was applied below the breakpoint: ${JSON.stringify(s)}`);
    assert(s.stored === "480", "the stored width was dropped");
    st.setWide(true);
    s = now(page);
    assert(!page.handle.hidden && s.variable === "480px" && s.custom && s.valuenow === 480, `back to wide: ${JSON.stringify(s)}`);
    st.setWide(false);
    s = now(page);
    assert(page.handle.hidden && s.variable === "" && !s.custom, `narrow again: ${JSON.stringify(s)}`);
    const graph = await openGraph(h, stage({ stored: "480", wide: false }));
    assert(graph.handle.hidden && graph.layout.style.getPropertyValue(VAR) === "", "graph page: below the breakpoint");
  });

  h.checkAsync("resize: the bounds follow the layout -- main keeps 360, 60% of the layout, 720; a window resize re-clamps and keeps the wanted width", async () => {
    // 1100: explorer 290 + main 360 leaves 450 (60% of 1100 is 660).
    const st = stage({ layoutW: 1100, stored: "700" });
    const page = await openConcept(h, st);
    let s = now(page);
    assert(s.valuemax === 450, `valuemax at 1100 is ${s.valuemax}, expected 450 (1100 - 290 - 360)`);
    assert(s.valuenow === 450 && s.variable === "450px", `700 at 1100 shows ${JSON.stringify(s)}`);
    assert(s.stored === "700", "clamping rewrote the stored width");
    h.key(page.window, page.handle, { key: "End" });
    assert(now(page).valuenow === 450 && now(page).stored === "450", "End at 1100 is not the bound");
    // Wider: the wanted width (the last the reader set, 450) holds; the bound grows.
    st.layoutW = 1440;
    page.window.dispatchEvent(new page.window.Event("resize"));
    s = now(page);
    assert(s.valuemax === 720 && s.valuenow === 450, `at 1440: ${JSON.stringify(s)}`);
    // 60% bound: 1200 wide, explorer hidden by the stub -> min(720, 720, 840)=720; with a 1000 layout 60% = 600.
    st.layoutW = 1000;
    st.explorerW = 0;
    page.window.dispatchEvent(new page.window.Event("resize"));
    assert(now(page).valuemax === 600, `60% of 1000: valuemax ${now(page).valuemax}`);
    // A stored width the window had to cut comes back when it grows.
    const second = stage({ layoutW: 1100, stored: "600" });
    const p2 = await openConcept(h, second);
    assert(now(p2).valuenow === 450, `600 at 1100: ${now(p2).valuenow}`);
    second.layoutW = 1600;
    p2.window.dispatchEvent(new p2.window.Event("resize"));
    assert(now(p2).valuenow === 600 && now(p2).variable === "600px", `1600: ${JSON.stringify(now(p2))}`);
    // A ResizeObserver, where there is one, drives the same re-clamp.
    second.layoutW = 1100;
    second.fireObservers();
    assert(now(p2).valuenow === 450, `ResizeObserver: ${now(p2).valuenow}`);
    // What the reader saw is what is wanted: a drag or a key pressed past the
    // bound asks for the bound, not for what the pointer or the step reached.
    const third = stage({ layoutW: 1100 });
    const p3 = await openConcept(h, third);
    pointer(p3.window, p3.handle, "pointerdown", 1000);
    pointer(p3.window, p3.handle, "pointermove", 100);
    pointer(p3.window, p3.handle, "pointerup", 100);
    assert(now(p3).valuenow === 450 && now(p3).stored === "450", `a drag past the bound at 1100: ${JSON.stringify(now(p3))}`);
    third.layoutW = 1600;
    p3.window.dispatchEvent(new p3.window.Event("resize"));
    assert(now(p3).valuenow === 450, `a drag past the bound asked for more than the bound: ${now(p3).valuenow} once the window grew`);
    h.key(p3.window, p3.handle, { key: "ArrowLeft", shiftKey: true });
    assert(now(p3).valuenow === 514, `450 + 64 at 1600: ${now(p3).valuenow}`);
    third.layoutW = 1100;
    p3.window.dispatchEvent(new p3.window.Event("resize"));
    h.key(p3.window, p3.handle, { key: "ArrowLeft", shiftKey: true });
    assert(now(p3).valuenow === 450 && now(p3).stored === "450", `a key past the bound: ${JSON.stringify(now(p3))}`);
    third.layoutW = 1600;
    p3.window.dispatchEvent(new p3.window.Event("resize"));
    assert(now(p3).valuenow === 450, `a key past the bound asked for more than the bound: ${now(p3).valuenow}`);
    // The graph page: the facets (270) count as a fixed column.
    const g = await openGraph(h, stage({ layoutW: 1100, stored: "700" }));
    assert(now(g).valuemax === 470 && now(g).valuenow === 470, `graph at 1100: ${JSON.stringify(now(g))}, expected 470 (1100 - 270 - 360)`);
  });

  // --- pointer ---------------------------------------------------------------

  h.checkAsync("resize: a pointer drag moves the width by the pointer's delta (left widens), clamps, takes the capture, persists once at the end", async () => {
    const st = stage();
    const page = await openConcept(h, st);
    const { window, handle, layout } = page;
    pointer(window, handle, "pointerdown", 1000);
    assert(st.captured.length === 1 && st.captured[0] === 1, `setPointerCapture calls: ${JSON.stringify(st.captured)}`);
    assert(layout.hasAttribute("data-okf-resizing"), "no data-okf-resizing while the drag runs");
    pointer(window, handle, "pointermove", 900);
    assert(now(page).valuenow === 440 && now(page).variable === "440px", `after -100: ${JSON.stringify(now(page))}`);
    assert(now(page).stored === null, "the width was persisted mid-drag");
    pointer(window, handle, "pointermove", 1030);
    assert(now(page).valuenow === 310, `after +30: ${now(page).valuenow}`);
    pointer(window, handle, "pointermove", 400);
    assert(now(page).valuenow === 720, `past the maximum: ${now(page).valuenow}`);
    pointer(window, handle, "pointermove", 1900);
    assert(now(page).valuenow === 240, `past the minimum: ${now(page).valuenow}`);
    pointer(window, handle, "pointermove", 850);
    pointer(window, handle, "pointerup", 850);
    assert(now(page).valuenow === 490 && now(page).stored === "490", `released: ${JSON.stringify(now(page))}`);
    assert(!layout.hasAttribute("data-okf-resizing"), "data-okf-resizing outlives the drag");
    assert(st.released.length >= 1, "the capture was not released");
    pointer(window, handle, "pointermove", 600);
    assert(now(page).valuenow === 490, "a move after the release still resizes");
    // A second drag starts from the width the first left.
    pointer(window, handle, "pointerdown", 700);
    pointer(window, handle, "pointermove", 710);
    pointer(window, handle, "pointerup", 710);
    assert(now(page).valuenow === 480 && now(page).stored === "480", `second drag: ${JSON.stringify(now(page))}`);
  });

  h.checkAsync("resize: a drag on the graph page resizes the drawer the same way", async () => {
    const st = stage();
    const page = await openGraph(h, st);
    pointer(page.window, page.handle, "pointerdown", 1000);
    pointer(page.window, page.handle, "pointermove", 880);
    pointer(page.window, page.handle, "pointerup", 880);
    assert(now(page).valuenow === 460 && now(page).variable === "460px" && now(page).stored === "460", JSON.stringify(now(page)));
  });

  h.checkAsync("resize: a click with no movement (the two halves of a double click) writes nothing", async () => {
    const st = stage();
    const page = await openConcept(h, st);
    pointer(page.window, page.handle, "pointerdown", 1000);
    pointer(page.window, page.handle, "pointerup", 1000);
    const s = now(page);
    assert(s.stored === null && s.variable === "" && !s.custom, `a click changed the width: ${JSON.stringify(s)}`);
  });

  const endings = [
    ["pointercancel", (w, hd) => pointer(w, hd, "pointercancel", 880, { buttons: 0 })],
    ["lostpointercapture", (w, hd) => pointer(w, hd, "lostpointercapture", 880)],
    ["window blur", (w) => w.dispatchEvent(new w.Event("blur"))],
    ["a mouse move with no button down", (w, hd) => pointer(w, hd, "pointermove", 880, { buttons: 0 })],
  ];
  for (const [name, end] of endings) {
    h.checkAsync(`resize: the drag ends cleanly on ${name} (width kept and persisted, no later move resizes)`, async () => {
      const st = stage();
      const page = await openConcept(h, st);
      const { window, handle, layout } = page;
      pointer(window, handle, "pointerdown", 1000);
      pointer(window, handle, "pointermove", 900);
      assert(now(page).valuenow === 440, "setup: the drag did not start");
      end(window, handle);
      assert(!layout.hasAttribute("data-okf-resizing"), `${name}: data-okf-resizing outlives the drag`);
      const kept = now(page);
      assert(kept.valuenow === 440 && kept.stored === "440", `${name}: ${JSON.stringify(kept)}`);
      pointer(window, handle, "pointermove", 500);
      assert(now(page).valuenow === 440, `${name}: a later move still resizes`);
      pointer(window, handle, "pointerup", 500);
      assert(now(page).valuenow === 440, `${name}: a later release changed the width`);
    });
  }

  h.checkAsync("resize: Escape during a drag puts the width back and ends it; a pointerup after it changes nothing", async () => {
    const st = stage({ stored: "400" });
    const page = await openConcept(h, st);
    const { window, handle, layout } = page;
    pointer(window, handle, "pointerdown", 1000);
    pointer(window, handle, "pointermove", 800);
    assert(now(page).valuenow === 600, "setup");
    const ev = h.key(window, handle, { key: "Escape" });
    assert(ev.defaultPrevented, "Escape was not taken during a drag");
    const s = now(page);
    assert(s.valuenow === 400 && s.variable === "400px" && s.stored === "400", `Escape: ${JSON.stringify(s)}`);
    assert(!layout.hasAttribute("data-okf-resizing"), "the drag survives Escape");
    pointer(window, handle, "pointermove", 700);
    pointer(window, handle, "pointerup", 700);
    assert(now(page).valuenow === 400, "a move after Escape resized");
    // Escape with no drag running is not the splitter's.
    const idle = h.key(window, handle, { key: "Escape" });
    assert(!idle.defaultPrevented, "Escape was taken with no drag running");
  });

  h.checkAsync("resize: only the primary button of the pointer that started the drag counts", async () => {
    const st = stage();
    const page = await openConcept(h, st);
    const { window, handle } = page;
    pointer(window, handle, "pointerdown", 1000, { button: 2 });
    pointer(window, handle, "pointermove", 900);
    assert(now(page).valuenow === 340 && !page.layout.hasAttribute("data-okf-resizing"), "a right-button press started a drag");
    pointer(window, handle, "pointerdown", 1000, { pointerId: 5, pointerType: "touch" });
    pointer(window, handle, "pointermove", 900, { pointerId: 9, pointerType: "touch" });
    assert(now(page).valuenow === 340, "another pointer moved the splitter");
    pointer(window, handle, "pointermove", 900, { pointerId: 5, pointerType: "touch" });
    assert(now(page).valuenow === 440, "the touch pointer that started the drag did not move it");
    pointer(window, handle, "pointerdown", 1000, { pointerId: 9, pointerType: "touch" });
    pointer(window, handle, "pointermove", 700, { pointerId: 9, pointerType: "touch" });
    assert(now(page).valuenow === 440, "a second finger took over the drag");
    pointer(window, handle, "pointerup", 900, { pointerId: 5, pointerType: "touch" });
    assert(now(page).stored === "440", `the touch drag did not persist: ${now(page).stored}`);
    // Non-finite coordinates move nothing.
    pointer(window, handle, "pointerdown", 1000);
    const bad = new window.PointerEvent("pointermove", { bubbles: true, clientX: 0, pointerId: 1, buttons: 1, pointerType: "mouse" });
    Object.defineProperty(bad, "clientX", { value: NaN });
    handle.dispatchEvent(bad);
    assert(now(page).valuenow === 440, "a NaN coordinate moved the splitter");
    pointer(window, handle, "pointerup", 1000);
  });

  h.checkAsync("resize: without setPointerCapture (or when it throws) the drag still follows the pointer and ends", async () => {
    const st = stage({ capture: false });
    const page = await openConcept(h, st);
    const { window, handle } = page;
    pointer(window, handle, "pointerdown", 1000);
    pointer(window, window.document.body, "pointermove", 900);
    assert(now(page).valuenow === 440, "moves outside the splitter were not followed without capture");
    pointer(window, window.document.body, "pointerup", 900);
    assert(now(page).stored === "440", "the release outside the splitter was not followed");
    const throwing = stage();
    const second = await openConcept(h, throwing, { beforeParse(w) { w.Element.prototype.setPointerCapture = function () { throw new w.DOMException("no", "InvalidPointerId"); }; } });
    pointer(second.window, second.handle, "pointerdown", 1000);
    pointer(second.window, second.handle, "pointermove", 950);
    assert(now(second).valuenow === 390, `a throwing setPointerCapture broke the drag: ${now(second).valuenow}`);
    pointer(second.window, second.handle, "pointerup", 950);
  });

  // --- the script never trusts the page --------------------------------------

  h.checkAsync("resize: body content wearing the splitter's class is not the splitter (no role, no tabindex, not first-found) and the script still finds its own", async () => {
    const st = stage();
    const page = await openConcept(h, st, { page: "p4-chrome-classes.html" });
    const worn = Array.from(page.doc.querySelectorAll("#okf-body .okf-splitter"));
    assert(worn.length >= 2, "the fixture lost its classed <code> elements, or the sanitizer dropped class: this case tests nothing");
    for (const el of worn) {
      assert(!el.hasAttribute("role") && !el.hasAttribute("tabindex") && !el.hasAttribute("aria-valuenow"), "the script touched body content wearing the class");
    }
    assert(page.handle && !page.doc.getElementById("okf-body").contains(page.handle), "the real splitter is not under the layout");
    h.key(page.window, worn[0], { key: "ArrowLeft" });
    assert(now(page).valuenow === 340, "a key on body content moved the splitter");
  });

  h.checkAsync("resize: the script touches nothing but its handle, the layout's variable and attributes, and the panels' hidden state it reads", async () => {
    const src = fs.readFileSync(path.join(ASSETS, "okf-resize.js"), "utf8");
    assert(src.startsWith("// SPDX-License-Identifier: LGPL-3.0-or-later"), "no SPDX header");
    // Static smoke check, not proof.
    for (const banned of ["innerHTML", "outerHTML", "insertAdjacentHTML", "eval(", "new Function", "document.write", "document.querySelector", "document.getElementsByClassName"]) {
      assert(!src.includes(banned), `okf-resize.js uses ${banned}`);
    }
    assert(!/\\u[0-9a-fA-F]{4}/.test(src), "okf-resize.js holds a unicode escape");
    assert(!/\r/.test(src), "okf-resize.js has CR line endings");
  });

  // --- stylesheet (static smoke checks, not proof) ---------------------------

  h.checkAsync("resize: viewer.css applies the variable only from 1100 px, on both layouts, hides and styles the handle (static smoke check, not proof)", async () => {
    const css = fs.readFileSync(path.join(ASSETS, "viewer.css"), "utf8");
    const at = css.indexOf("/* === P4:");
    assert(at > 0, "no P4 section in viewer.css");
    const section = css.slice(at);
    const media = (text) => {
      const open = section.indexOf(text);
      assert(open >= 0, `no ${text} block in the P4 section`);
      let depth = 0;
      for (let i = section.indexOf("{", open); i < section.length; i++) {
        if (section[i] === "{") { depth++; } else if (section[i] === "}" && --depth === 0) { return section.slice(open, i + 1); }
      }
      throw new Error(`${text} never closes`);
    };
    const wide = media("@media (min-width: 1100px)");
    for (const selector of ["body > .okf-layout[data-okf-resized] > #okf-context", "body > .okf-graph-layout[data-okf-resized] > .okf-graph-detail"]) {
      const at = wide.indexOf(selector);
      assert(at >= 0, `no ${selector} rule inside the wide block`);
      const rule = wide.slice(at, wide.indexOf("}", at));
      assert(/width:\s*var\(--okf-context-w\)\s*;?\s*$/.test(rule), `${selector} does not take its width from the variable`);
    }
    assert(!/var\(--okf-context-w/.test(section.replace(wide, "")), "the variable is read outside the wide block");
    assert(/\.okf-splitter\s*\{[^}]*display:\s*none/.test(section.replace(wide, "")), "the splitter is not display:none by default");
    assert(/\.okf-splitter\s*\{[^}]*touch-action:\s*none/.test(wide), "the splitter does not turn touch-action off");
    assert(/\.okf-splitter\[hidden\]\s*\{[^}]*display:\s*none/.test(section), "the hidden attribute does not win over the splitter's display");
    assert(/\.okf-splitter:focus-visible\s*\{[^}]*outline:\s*2px solid/.test(section), "no focus ring (a 2px solid outline on :focus-visible)");
    assert(/@media \(forced-colors: active\)[^]*\.okf-splitter/.test(section), "no forced-colors rule");
    assert(/#okf-context \.okf-local-head\s*\{[^}]*flex-wrap:\s*wrap/.test(section), "the Neighbourhood head does not wrap at the narrowest panel");
    assert(!/@media[^{]*prefers-reduced-motion/.test(section) || !/transition/.test(section), "motion in the splitter styles");
    for (const line of section.replace(/[/][*][^]*?[*][/]/g, "").split("\n")) {
      if (!line.includes("okf-splitter")) { continue; }
      for (const selector of line.split("{")[0].split(",")) {
        const s = selector.trim();
        if (s && s.includes("okf-splitter")) { assert(/^body > \.okf-(graph-)?layout/.test(s), `a splitter selector is not anchored under a layout: ${s}`); }
      }
    }
  });

  // --- the graph page refits when its canvas changes size ---------------------

  const sized = (size) => (w) => {
    for (const [name, key] of [["clientWidth", "w"], ["clientHeight", "h"]]) {
      Object.defineProperty(w.HTMLElement.prototype, name, { configurable: true, get() { return this.id === "okf-graph-canvas" && !this.hidden ? size[key] : 0; } });
    }
  };
  const transformOf = (doc) => {
    const m = /^translate\((-?[0-9.]+) (-?[0-9.]+)\) scale\(([0-9.]+)\)$/.exec(doc.querySelector("#okf-graph-canvas g.okf-graph-viewport").getAttribute("transform"));
    return JSON.stringify([Number(m[1]), Number(m[2]), Number(m[3])]);
  };

  h.checkAsync("graph: a canvas that changed size is fitted again (a ResizeObserver on the canvas), unless the reader moved the view", async () => {
    const size = { w: 600, h: 500 };
    const st = stage();
    // The view's transform is written whenever the drawing is fitted, even to the same value: a count of the writes tells a refit from nothing.
    let writes = 0;
    const counting = (w) => {
      sized(size)(w);
      const set = w.Element.prototype.setAttribute;
      w.Element.prototype.setAttribute = function (name, value) {
        if (name === "transform" && this.getAttribute("class") === "okf-graph-viewport") { writes++; }
        return set.call(this, name, value);
      };
    };
    const page = await openGraph(h, st, { beforeParse: counting });
    page.flush();
    assert(st.observers.some((o) => o.targets.some((t) => t.id === "okf-graph-canvas")), "no ResizeObserver watches #okf-graph-canvas");
    const first = transformOf(page.doc);
    // The observer's first notification (the size it was given) refits nothing.
    st.fireObservers();
    assert(transformOf(page.doc) === first, "the first notification moved the view");
    size.w = 1000;
    st.fireObservers();
    const wider = transformOf(page.doc);
    assert(wider !== first, "a wider canvas left the old fit");
    // Same size again: nothing.
    const settled = writes;
    st.fireObservers();
    assert(transformOf(page.doc) === wider, "an unchanged size refitted");
    assert(writes === settled, `an unchanged size fitted the drawing again (${writes - settled} write(s) of its transform)`);
    // The reader moved the view (a zoom): a new size does not take it back.
    page.doc.querySelector('#okf-graph-zoom button[aria-label="Zoom in"]').dispatchEvent(new page.window.MouseEvent("click", { bubbles: true, cancelable: true }));
    const moved = transformOf(page.doc);
    assert(moved !== wider, "setup: the zoom button did nothing");
    size.w = 700;
    st.fireObservers();
    assert(transformOf(page.doc) === moved, "a resize took the view back from the reader");
    // Fit gives the view back to the layout, and the next resize refits.
    Array.from(page.doc.querySelectorAll("#okf-graph-zoom button")).find((b) => b.textContent === "Fit").dispatchEvent(new page.window.MouseEvent("click", { bubbles: true, cancelable: true }));
    const refit = transformOf(page.doc);
    size.w = 1100;
    st.fireObservers();
    assert(transformOf(page.doc) !== refit, "after Fit a resize did not refit");
  });

  h.checkAsync("graph: without ResizeObserver a window resize refits, and a hidden canvas (list mode) is left alone", async () => {
    const size = { w: 600, h: 500 };
    const st = stage();
    const page = await openGraph(h, st, { beforeParse(w) { sized(size)(w); w.ResizeObserver = undefined; } });
    page.flush();
    const first = transformOf(page.doc);
    size.w = 1000;
    page.window.dispatchEvent(new page.window.Event("resize"));
    assert(transformOf(page.doc) !== first, "a window resize did not refit without ResizeObserver");
    const toggle = page.doc.querySelector("#okf-graph-zoom .okf-graph-list-toggle");
    toggle.dispatchEvent(new page.window.MouseEvent("click", { bubbles: true, cancelable: true }));
    const canvas = page.doc.getElementById("okf-graph-canvas");
    assert(canvas.hidden, "setup: list mode did not hide the canvas");
    size.w = 0;
    page.window.dispatchEvent(new page.window.Event("resize"));
    toggle.dispatchEvent(new page.window.MouseEvent("click", { bubbles: true, cancelable: true }));
    size.w = 800;
    page.window.dispatchEvent(new page.window.Event("resize"));
    assert(!canvas.hidden && transformOf(page.doc).length > 0, "the canvas did not come back drawn");
  });
}

module.exports = { register };
