// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Theme (spec §4.7). Loaded in <head>, before the stylesheet, so a stored
// choice applies before the first paint on every page. Storage can be
// unavailable (file:// in some browsers, privacy modes) and, even when a
// write succeeds, local pages may not share it: every access is guarded and
// without it the page follows prefers-color-scheme.
(function () {
  "use strict";
  var KEY = "okf-theme";
  var root = document.documentElement;

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

  function addToggle() {
    var tools = document.getElementById("okf-tools");
    if (!tools) { return; }
    var button = document.createElement("button");
    button.type = "button";
    button.id = "okf-theme-toggle";
    button.className = "okf-tool";
    button.textContent = "Dark theme";
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
