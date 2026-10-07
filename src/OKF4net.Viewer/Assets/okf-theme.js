// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Theme (spec §4.7). Loaded in <head>, before the stylesheet, so a stored
// choice applies before the first paint on every page. Storage can be
// unavailable (file:// in some browsers, privacy modes) and, even when a
// write succeeds, local pages may not share it: every access is guarded and
// without it the page follows prefers-color-scheme.
//
// It also marks <html> with data-okf-js as soon as it runs: viewer.css folds
// the frontmatter box only under that mark (spec §11.3, C6), so a page
// without JavaScript shows every entry, and a page with it never shows them
// all for a frame before okf-page.js has run.
(function () {
  "use strict";
  var KEY = "okf-theme";
  var SVG_NS = "http://www.w3.org/2000/svg";
  // The moon of the mockups (H11), a constant of this module (spec §4.5).
  var MOON = "M13 9.5A5.5 5.5 0 0 1 6.5 3a5.5 5.5 0 1 0 6.5 6.5z";
  var root = document.documentElement;
  root.setAttribute("data-okf-js", "");

  function stored() {
    try {
      var value = window.localStorage.getItem(KEY);
      return value === "light" || value === "dark" ? value : null;
    } catch (e) {
      return null;
    }
  }

  function store(value) {
    try { window.localStorage.setItem(KEY, value); } catch (e) { /* not persisted */ }
  }

  var initial = stored();
  if (initial) { root.setAttribute("data-theme", initial); }

  function effective() {
    var forced = root.getAttribute("data-theme");
    if (forced === "light" || forced === "dark") { return forced; }
    var query = typeof window.matchMedia === "function" ? window.matchMedia("(prefers-color-scheme: dark)") : null;
    return query && query.matches ? "dark" : "light";
  }

  function moonIcon() {
    var svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("class", "okf-theme-icon");
    svg.setAttribute("width", "16");
    svg.setAttribute("height", "16");
    svg.setAttribute("viewBox", "0 0 16 16");
    svg.setAttribute("aria-hidden", "true");
    svg.setAttribute("focusable", "false");
    var path = document.createElementNS(SVG_NS, "path");
    path.setAttribute("d", MOON);
    path.setAttribute("stroke-width", "1.5");
    svg.appendChild(path);
    return svg;
  }

  function addToggle() {
    var tools = document.getElementById("okf-tools");
    if (!tools) { return; }
    var button = document.createElement("button");
    button.type = "button";
    button.id = "okf-theme-toggle";
    button.className = "okf-tool okf-theme-toggle";
    // An icon button: its name stays P1's "Dark theme", its state is aria-pressed.
    button.setAttribute("aria-label", "Dark theme");
    button.appendChild(moonIcon());
    function sync() {
      button.setAttribute("aria-pressed", effective() === "dark" ? "true" : "false");
    }
    button.addEventListener("click", function () {
      var next = effective() === "dark" ? "light" : "dark";
      root.setAttribute("data-theme", next);
      store(next);
      sync();
    });
    // While no theme is forced, the pressed state follows the system
    // preference: re-announce it when that preference changes.
    if (typeof window.matchMedia === "function") {
      var query = window.matchMedia("(prefers-color-scheme: dark)");
      if (query && typeof query.addEventListener === "function") {
        query.addEventListener("change", sync);
      } else if (query && typeof query.addListener === "function") {
        query.addListener(sync);
      }
    }
    sync();
    tools.appendChild(button);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", addToggle);
  } else {
    addToggle();
  }
})();
