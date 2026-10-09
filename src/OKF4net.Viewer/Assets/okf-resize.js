// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The splitter between the middle column and the right-hand column: the
// context panel of a concept page or of the index, the drawer of the graph
// page. A window splitter (WAI-ARIA: role="separator", focusable, with a
// value): drag it with a mouse, a finger or a pen, or use the arrow keys, to
// widen or narrow the right-hand column so a graph can breathe.
//
// The width is a CSS custom property on the layout container
// (--okf-context-w) and a fixed attribute (data-okf-resized) that viewer.css
// reads from 1 100 px up only: the stacked layout below it ignores both. It is
// kept in localStorage under one key shared by every page, and only a whole
// number of pixels within the bounds is ever read back from it (anything
// else is as if nothing was stored). Every storage access is guarded: the page
// works, unsaved, where storage is refused.
//
// Standalone: it needs no other script and no index, so it loads first and the
// stored width is in place before the other scripts fill the page. It looks
// its columns up by id (never by class: body content can wear any class, none
// can carry an id), builds the one handle itself, and writes only that handle's
// attributes and the layout's variable and two attributes.
(function () {
  "use strict";

  var KEY = "okf-context-w";
  var VAR = "--okf-context-w";
  var WIDE = "(min-width: 1100px)";
  var DEFAULT_W = 340; // today's width of both panels (spec 11.4 X1, 11.5 G17)
  var MIN_W = 240;
  var MAX_W = 720;
  var MAX_SHARE = 0.6; // of the layout's width
  var MIN_MAIN = 360; // the middle column never gets narrower than this
  var STEP = 16;
  var BIG_STEP = 64;

  var panel = document.getElementById("okf-context") || document.getElementById("okf-graph-detail");
  var main = document.getElementById("okf-main");
  var layout = panel ? panel.parentNode : null;
  if (!panel || !main || !layout || main.parentNode !== layout || layout.parentNode !== document.body) { return; }

  // The columns to the left of the middle one, which the bounds leave room for.
  var fixed = [document.getElementById("okf-explorer"), document.getElementById("okf-facets")].filter(function (column) {
    return column && column.parentNode === layout;
  });

  // === storage: nothing is trusted ===

  // A stored width is three digits, 240 to 720: what this script writes.
  function readStored() {
    try {
      var raw = window.localStorage.getItem(KEY);
      if (typeof raw !== "string" || !/^[1-9][0-9]{2}$/.test(raw)) { return null; }
      var n = Number(raw);
      return n >= MIN_W && n <= MAX_W ? n : null;
    } catch (e) {
      return null;
    }
  }

  function writeStored(width) {
    try {
      if (width === null) { window.localStorage.removeItem(KEY); } else { window.localStorage.setItem(KEY, String(width)); }
    } catch (e) { /* not persisted */ }
  }

  // === the handle ===

  var handle = document.createElement("div");
  handle.setAttribute("class", "okf-splitter");
  handle.setAttribute("role", "separator");
  handle.setAttribute("aria-orientation", "vertical");
  handle.setAttribute("aria-label", "Resize the side panel");
  handle.setAttribute("aria-controls", panel.getAttribute("id"));
  handle.setAttribute("tabindex", "0");
  handle.hidden = true;
  layout.insertBefore(handle, panel);

  // The width the reader asked for (null: the default), and the one shown,
  // which the bounds of the moment may cut: a window that grows gives the
  // asked width back.
  var desired = readStored();
  var shown = DEFAULT_W;
  var query = typeof window.matchMedia === "function" ? window.matchMedia(WIDE) : null;

  function isWide() { return query ? query.matches : true; }

  // The room the right-hand column may take: 60% of the layout, 720, and what
  // the other columns leave once the middle one has its 360. A layout that has
  // no measurable width (not displayed) is bounded by the absolute limits only.
  function bounds() {
    var total = layout.getBoundingClientRect().width;
    if (!(total > 0)) { return { min: MIN_W, max: MAX_W }; }
    var others = 0;
    for (var i = 0; i < fixed.length; i++) { others += fixed[i].getBoundingClientRect().width || 0; }
    var max = Math.min(Math.floor(total * MAX_SHARE), MAX_W, Math.floor(total - others - MIN_MAIN));
    return { min: MIN_W, max: Math.max(MIN_W, max) };
  }

  function clamp(width, b) { return Math.max(b.min, Math.min(b.max, Math.round(width))); }

  // Puts the layout, the handle and its value in step with `desired`, the
  // bounds and the breakpoint. Idempotent; `b` lets a drag keep the bounds it
  // measured when it began instead of measuring again on every move.
  function render(b) {
    var wide = isWide();
    handle.hidden = !(wide && !panel.hidden);
    if (!wide) {
      // Stacked: the stored width is ignored, the column is the page's width.
      layout.style.removeProperty(VAR);
      layout.removeAttribute("data-okf-resized");
      return;
    }
    b = b || bounds();
    shown = clamp(desired === null ? DEFAULT_W : desired, b);
    if (shown === DEFAULT_W) {
      layout.style.removeProperty(VAR);
      layout.removeAttribute("data-okf-resized");
    } else {
      layout.style.setProperty(VAR, shown + "px");
      layout.setAttribute("data-okf-resized", "");
    }
    handle.setAttribute("aria-valuemin", String(b.min));
    handle.setAttribute("aria-valuemax", String(b.max));
    handle.setAttribute("aria-valuenow", String(shown));
    handle.setAttribute("aria-valuetext", shown + " px");
  }

  // The reader sets a width: kept, and stored.
  function commit(width) {
    desired = clamp(width, bounds());
    render();
    writeStored(desired);
  }

  function reset() {
    desired = null;
    render();
    writeStored(null);
  }

  // === keyboard: only with the focus on the handle ===

  handle.addEventListener("keydown", function (e) {
    if (e.altKey || e.ctrlKey || e.metaKey || drag) { return; }
    render();
    var step = e.shiftKey ? BIG_STEP : STEP;
    var b = bounds();
    if (e.key === "ArrowLeft") { commit(shown + step); }
    else if (e.key === "ArrowRight") { commit(shown - step); }
    else if (e.key === "Home") { commit(b.min); }
    else if (e.key === "End") { commit(b.max); }
    else if (e.key === "Enter") { reset(); }
    else { return; }
    e.preventDefault();
  });
  handle.addEventListener("dblclick", reset);
  // The explorer may have appeared since the last measure: refresh the maximum.
  handle.addEventListener("focus", function () { render(); });

  // === pointer: a drag follows the pointer by its delta ===

  var drag = null;

  function onMove(e) {
    if (!drag || e.pointerId !== drag.id) { return; }
    // A mouse released outside the window sends no pointerup: its next move
    // says no button is down.
    if (e.pointerType === "mouse" && e.buttons === 0) { finish(true); return; }
    if (!Number.isFinite(e.clientX)) { return; }
    // The handle sits left of the column: moving it left widens the column.
    desired = clamp(drag.width - (e.clientX - drag.x), drag.bounds);
    render(drag.bounds);
  }

  function onEnd(e) {
    if (!drag || (e.pointerId !== undefined && e.pointerId !== drag.id)) { return; }
    finish(true);
  }

  function onBlur() { finish(true); }

  function onKey(e) {
    if (e.key !== "Escape" || !drag) { return; }
    e.preventDefault();
    finish(false);
  }

  // Ends the drag: kept (and stored if it moved anything), or cancelled, which
  // puts back what the reader had when it began. Idempotent: a pointerup is
  // followed by lostpointercapture.
  function finish(keep) {
    if (!drag) { return; }
    var ended = drag;
    drag = null;
    window.removeEventListener("pointermove", onMove);
    window.removeEventListener("pointerup", onEnd);
    window.removeEventListener("pointercancel", onEnd);
    window.removeEventListener("lostpointercapture", onEnd);
    window.removeEventListener("blur", onBlur);
    document.removeEventListener("keydown", onKey, true);
    layout.removeAttribute("data-okf-resizing");
    try {
      if (typeof handle.releasePointerCapture === "function") { handle.releasePointerCapture(ended.id); }
    } catch (e) { /* the capture was already gone */ }
    if (keep) {
      if (shown !== ended.width) { writeStored(shown); }
    } else {
      desired = ended.desired;
      render();
    }
  }

  handle.addEventListener("pointerdown", function (e) {
    if (e.button !== 0 || (drag && drag.id !== e.pointerId) || handle.hidden || !Number.isFinite(e.clientX)) { return; }
    if (drag) { finish(true); }
    render();
    drag = { id: e.pointerId, x: e.clientX, width: shown, desired: desired, bounds: bounds() };
    layout.setAttribute("data-okf-resizing", "");
    try {
      if (typeof handle.setPointerCapture === "function") { handle.setPointerCapture(e.pointerId); }
    } catch (err) { /* followed on window instead */ }
    // Moves and releases are followed on window: with the capture they arrive
    // there through the handle, without it from wherever the pointer is.
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onEnd);
    window.addEventListener("pointercancel", onEnd);
    window.addEventListener("lostpointercapture", onEnd);
    window.addEventListener("blur", onBlur);
    document.addEventListener("keydown", onKey, true);
  });

  // === following the page ===

  function sync() { render(); }
  window.addEventListener("resize", sync);
  window.addEventListener("load", sync);
  if (typeof window.ResizeObserver === "function") { new window.ResizeObserver(sync).observe(layout); }
  if (query) {
    if (typeof query.addEventListener === "function") { query.addEventListener("change", sync); }
    else if (typeof query.addListener === "function") { query.addListener(sync); }
  }
  // The panel is shown when it has something to list; the explorer when it is
  // built. Either changes the room the bounds leave.
  if (typeof window.MutationObserver === "function") {
    var watcher = new window.MutationObserver(sync);
    watcher.observe(panel, { attributes: true, attributeFilter: ["hidden"] });
    for (var i = 0; i < fixed.length; i++) { watcher.observe(fixed[i], { attributes: true, attributeFilter: ["hidden"] }); }
  }

  render();
})();
