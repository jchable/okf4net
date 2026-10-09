// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The "Jump to" palette (spec §4.6, §8, §11.6), drawn as mockup C draws it: a
// modal dialog opened by a visible button, Ctrl+K or "/". Results come from
// OkfSite.rank (fixed tiers, no weights -- not ConceptSearch, and never the
// body text); each option shows its type's glyph from OkfShapes with the type
// name as hidden text. Navigating dispatches a cancelable "okf:navigate"
// event first, so a host -- or the test harness -- can observe or veto it.
(function () {
  "use strict";
  var site = window.OkfSite;
  var shapes = window.OkfShapes;
  var tools = document.getElementById("okf-tools");
  if (!site || !shapes || !tools) { return; }
  var index = site.readIndex(window);
  if (!index) { return; }
  var root = site.rootOf(document);
  var SVG_NS = "http://www.w3.org/2000/svg";
  var ELLIPSIS = String.fromCharCode(0x2026);
  var MIDDLE_DOT = String.fromCharCode(0xB7);

  function el(tag, className, text) {
    return site.element(document, tag, className, text);
  }

  // J2: the search glyph of C, a constant of this module (spec §4.5).
  function searchIcon() {
    var svg = document.createElementNS(SVG_NS, "svg");
    svg.setAttribute("class", "okf-palette-search");
    svg.setAttribute("width", "18");
    svg.setAttribute("height", "18");
    svg.setAttribute("viewBox", "0 0 18 18");
    svg.setAttribute("aria-hidden", "true");
    svg.setAttribute("focusable", "false");
    var circle = document.createElementNS(SVG_NS, "circle");
    circle.setAttribute("cx", "8");
    circle.setAttribute("cy", "8");
    circle.setAttribute("r", "5");
    circle.setAttribute("stroke-width", "1.6");
    var handle = document.createElementNS(SVG_NS, "path");
    handle.setAttribute("d", "M12 12l4 4");
    handle.setAttribute("stroke-width", "1.6");
    svg.appendChild(circle);
    svg.appendChild(handle);
    return svg;
  }

  // H7: the label, then the shortcut hint, hidden from assistive technology
  // (aria-keyshortcuts announces the shortcuts).
  var opener = el("button", "okf-tool okf-palette-open");
  opener.type = "button";
  opener.setAttribute("aria-haspopup", "dialog");
  opener.setAttribute("aria-keyshortcuts", "Control+K /");
  opener.appendChild(el("span", "okf-palette-label", "Jump to a concept" + ELLIPSIS));
  var hint = el("span", "okf-palette-hint", "Ctrl K " + MIDDLE_DOT + " /");
  hint.setAttribute("aria-hidden", "true");
  opener.appendChild(hint);
  tools.insertBefore(opener, tools.firstChild);

  var backdrop = el("div", "okf-palette-backdrop");
  backdrop.hidden = true;
  var dialog = el("div", "okf-palette");
  dialog.setAttribute("role", "dialog");
  dialog.setAttribute("aria-modal", "true");
  dialog.setAttribute("aria-labelledby", "okf-palette-title");
  // Focusable but not a tab stop: a click on the dialog's own text keeps
  // focus inside it instead of dropping it to <body>.
  dialog.tabIndex = -1;
  var title = el("h2", "okf-sr", "Jump to a concept");
  title.id = "okf-palette-title";
  var head = el("div", "okf-palette-head");
  var input = document.createElement("input");
  input.type = "text";
  input.id = "okf-palette-input";
  input.className = "okf-palette-input";
  input.setAttribute("role", "combobox");
  input.setAttribute("aria-expanded", "true");
  input.setAttribute("aria-controls", "okf-palette-list");
  input.setAttribute("aria-autocomplete", "list");
  input.setAttribute("aria-labelledby", "okf-palette-title");
  input.setAttribute("autocomplete", "off");
  // J2: the close button is drawn as an "Esc" key and keeps the name Close.
  var close = el("button", "okf-palette-close", "Esc");
  close.type = "button";
  close.setAttribute("aria-label", "Close");
  head.appendChild(searchIcon());
  head.appendChild(input);
  head.appendChild(close);
  // J3: what sighted readers see; the status region below is what is announced.
  var matches = el("p", "okf-section-title okf-palette-matches");
  matches.setAttribute("aria-hidden", "true");
  var list = el("ul", "okf-palette-list");
  list.id = "okf-palette-list";
  list.setAttribute("role", "listbox");
  list.setAttribute("aria-label", "Matching concepts");
  var status = el("p", "okf-palette-status okf-sr");
  status.id = "okf-palette-status";
  status.setAttribute("role", "status");
  // J6
  var foot = el("div", "okf-palette-foot");
  foot.appendChild(el("span", "", "Up / Down to move"));
  foot.appendChild(el("span", "", "Enter to open"));
  dialog.appendChild(title);
  dialog.appendChild(head);
  dialog.appendChild(matches);
  dialog.appendChild(list);
  dialog.appendChild(status);
  dialog.appendChild(foot);
  backdrop.appendChild(dialog);
  document.body.appendChild(backdrop);

  var shown = [];
  var active = -1;
  // The concept the reader made active with the arrow keys since the palette
  // opened, or -1. Only that choice survives a query change; an option that
  // is active merely because it ranked first earlier does not (spec §8).
  var chosen = -1;
  var returnFocus = null;

  // Marks the active option and keeps it visible. Arrow keys call this alone:
  // the list is not rebuilt (it can hold every concept of a large bundle).
  function sync() {
    if (active >= 0 && list.children[active]) {
      var activeOption = list.children[active];
      activeOption.setAttribute("aria-selected", "true");
      input.setAttribute("aria-activedescendant", activeOption.id);
      // aria-activedescendant does not move DOM focus, so nothing scrolls
      // the list by itself: keep the active option visible.
      if (typeof activeOption.scrollIntoView === "function") {
        activeOption.scrollIntoView({ block: "nearest" });
      }
    } else {
      input.removeAttribute("aria-activedescendant");
    }
  }

  // J4: glyph of the type, the type name as hidden text (the explorer and its
  // legend are behind the modal), then the title and the id.
  function render() {
    while (list.firstChild) { list.removeChild(list.firstChild); }
    for (var k = 0; k < shown.length; k++) {
      var concept = index.concepts[shown[k]];
      var option = el("li", "okf-palette-option");
      option.id = "okf-palette-opt-" + k;
      option.setAttribute("role", "option");
      option.setAttribute("aria-selected", "false");
      option.appendChild(shapes.icon(shapes.kindOf(index, shown[k]), "icon"));
      option.appendChild(el("span", "okf-sr okf-palette-type", shapes.typeLabel(typeof concept.type === "string" ? concept.type : "")));
      var text = el("span", "okf-palette-text");
      text.appendChild(el("span", "okf-palette-title", concept.title));
      text.appendChild(el("span", "okf-palette-id", concept.id));
      option.appendChild(text);
      option.addEventListener("click", activate.bind(null, k));
      list.appendChild(option);
    }
    input.setAttribute("aria-expanded", shown.length > 0 ? "true" : "false");
    sync();
  }

  function update() {
    // Every match is listed and reachable: no cap (owner decision, 2026-10-06).
    shown = site.rank(index, input.value);
    // Keep the concept the reader chose with the arrows when it survives the
    // new query; otherwise the first (best-ranked) option becomes active and
    // the choice is forgotten (spec §8).
    active = chosen === -1 ? -1 : shown.indexOf(chosen);
    if (active === -1) {
      chosen = -1;
      active = shown.length > 0 ? 0 : -1;
    }
    render();
    if (site.normalize(input.value) === "") {
      status.textContent = "";
      matches.textContent = "";
    } else {
      matches.textContent = "Matches in title, id, tags " + MIDDLE_DOT + " " + shown.length;
      if (shown.length === 0) {
        status.textContent = "No matching concept";
      } else {
        status.textContent = shown.length + (shown.length === 1 ? " matching concept" : " matching concepts");
      }
    }
  }

  function move(delta) {
    if (shown.length === 0) { return; }
    list.children[active].setAttribute("aria-selected", "false");
    active = (active + delta + shown.length) % shown.length;
    chosen = shown[active];
    sync();
  }

  function activate(k) {
    if (k < 0 || k >= shown.length) { return; }
    var href = site.resolve(root, index.concepts[shown[k]].path);
    var event = new CustomEvent("okf:navigate", { cancelable: true, detail: { href: href } });
    if (document.dispatchEvent(event)) { window.location.assign(href); }
  }

  function open() {
    if (!backdrop.hidden) { return; }
    returnFocus = document.activeElement;
    input.value = "";
    active = -1;
    chosen = -1;
    update();
    backdrop.hidden = false;
    input.focus();
  }

  function closePalette() {
    backdrop.hidden = true;
    var target = returnFocus && typeof returnFocus.focus === "function" && document.contains(returnFocus) && returnFocus !== document.body
      ? returnFocus
      : opener;
    returnFocus = null;
    target.focus();
  }

  opener.addEventListener("click", open);
  close.addEventListener("click", closePalette);
  backdrop.addEventListener("click", function (e) { if (e.target === backdrop) { closePalette(); } });
  input.addEventListener("input", update);

  function isEditable(target) {
    if (!target || target.nodeType !== 1) { return false; }
    var tag = target.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || target.isContentEditable === true;
  }

  // Ctrl+K and nothing else (spec §8, "sans autre modificateur"). Matched on
  // the produced character, so Dvorak (physical K gives "t") is right; a
  // layout whose key for this chord is no ASCII letter (Cyrillic, Greek) has
  // no "k" at all and falls back to the physical key.
  function isCtrlK(e) {
    if (!e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) { return false; }
    if (e.key === "k" || e.key === "K") { return true; }
    return e.code === "KeyK" && !(typeof e.key === "string" && /^[A-Za-z]$/.test(e.key));
  }

  // One listener serves both states, on document, because a click inside the
  // dialog on something unfocusable can leave focus on <body>: the open-state
  // keys must not depend on where focus is.
  //
  // Closed: Ctrl+K and "/" are also browser shortcuts (Chrome, Firefox), so
  // they are taken only outside editable fields, without other modifiers and
  // outside IME composition, and prevented only when taken (spec §8).
  // Open: Ctrl+K again is swallowed (the palette stays open, focus returns to
  // its field) rather than handed to the browser's own Ctrl+K.
  document.addEventListener("keydown", function (e) {
    // Keys that confirm or cancel an IME composition belong to the IME, not
    // to the palette (Enter would navigate, Escape would close).
    if (e.isComposing || e.keyCode === 229) { return; }
    if (!backdrop.hidden) {
      if (e.key === "Escape") {
        e.preventDefault();
        closePalette();
      } else if (e.key === "ArrowDown") {
        e.preventDefault();
        move(1);
      } else if (e.key === "ArrowUp") {
        e.preventDefault();
        move(-1);
      } else if (e.key === "Enter" && e.target !== close) {
        // Enter on Close is that button's own click.
        e.preventDefault();
        activate(active);
      } else if (e.key === "Tab") {
        // Focus stays inside the modal: its only stops are the field and
        // Close (from <body> or the dialog itself, Tab lands on the field).
        e.preventDefault();
        (document.activeElement === input ? close : input).focus();
      } else if (isCtrlK(e)) {
        e.preventDefault();
        input.focus();
      }
      return;
    }
    if (e.defaultPrevented || isEditable(e.target)) { return; }
    var slash = e.key === "/" && !e.ctrlKey && !e.altKey && !e.metaKey;
    if (isCtrlK(e) || slash) {
      e.preventDefault();
      open();
    }
  });
})();
