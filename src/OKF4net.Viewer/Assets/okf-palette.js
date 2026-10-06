// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The "Jump to" palette (spec §4.6, §8): a modal dialog opened by a visible
// button, Ctrl+K or "/". Results come from OkfSite.rank (fixed tiers, no
// weights -- not ConceptSearch, and never the body text). Navigating
// dispatches a cancelable "okf:navigate" event first, so a host -- or the
// test harness -- can observe or veto it.
(function () {
  "use strict";
  var site = window.OkfSite;
  var tools = document.getElementById("okf-tools");
  if (!site || !tools) { return; }
  var index = site.readIndex(window);
  if (!index) { return; }
  var root = site.rootOf(document);

  function el(tag, className, text) {
    return site.element(document, tag, className, text);
  }

  var opener = el("button", "okf-tool okf-palette-open", "Jump to...");
  opener.type = "button";
  opener.setAttribute("aria-haspopup", "dialog");
  opener.setAttribute("aria-keyshortcuts", "Control+K /");
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
  var list = el("ul", "okf-palette-list");
  list.id = "okf-palette-list";
  list.setAttribute("role", "listbox");
  list.setAttribute("aria-label", "Matching concepts");
  var status = el("p", "okf-palette-status");
  status.id = "okf-palette-status";
  status.setAttribute("role", "status");
  var close = el("button", "okf-tool okf-palette-close", "Close");
  close.type = "button";
  dialog.appendChild(title);
  dialog.appendChild(input);
  dialog.appendChild(list);
  dialog.appendChild(status);
  dialog.appendChild(close);
  backdrop.appendChild(dialog);
  document.body.appendChild(backdrop);

  var shown = [];
  var active = -1;
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

  function render() {
    while (list.firstChild) { list.removeChild(list.firstChild); }
    for (var k = 0; k < shown.length; k++) {
      var concept = index.concepts[shown[k]];
      var option = el("li", "okf-palette-option");
      option.id = "okf-palette-opt-" + k;
      option.setAttribute("role", "option");
      option.setAttribute("aria-selected", "false");
      option.appendChild(el("span", "okf-palette-title", concept.title));
      option.appendChild(el("span", "okf-palette-id", concept.id));
      option.addEventListener("click", activate.bind(null, k));
      list.appendChild(option);
    }
    input.setAttribute("aria-expanded", shown.length > 0 ? "true" : "false");
    sync();
  }

  function update() {
    var previous = active >= 0 ? shown[active] : -1;
    // Every match is listed and reachable: no cap (owner decision, 2026-10-06).
    shown = site.rank(index, input.value);
    // Keep the active concept when it survives the new query, else the first
    // option becomes active (spec §8).
    active = shown.indexOf(previous);
    if (active === -1) { active = shown.length > 0 ? 0 : -1; }
    render();
    if (site.normalize(input.value) === "") {
      status.textContent = "";
    } else if (shown.length === 0) {
      status.textContent = "No matching concept";
    } else {
      status.textContent = shown.length + (shown.length === 1 ? " matching concept" : " matching concepts");
    }
  }

  function move(delta) {
    if (shown.length === 0) { return; }
    list.children[active].setAttribute("aria-selected", "false");
    active = (active + delta + shown.length) % shown.length;
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

  // One listener serves both states, on document, because a click inside the
  // dialog on something unfocusable can leave focus on <body>: the open-state
  // keys must not depend on where focus is.
  //
  // Closed: Ctrl+K and "/" are also browser shortcuts (Chrome, Firefox), so
  // they are taken only outside editable fields, without other modifiers and
  // outside IME composition, and prevented only when taken (spec §8).
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
      }
      return;
    }
    if (e.defaultPrevented || isEditable(e.target)) { return; }
    var ctrlK = e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && (e.key === "k" || e.key === "K");
    var slash = e.key === "/" && !e.ctrlKey && !e.altKey && !e.metaKey;
    if (ctrlK || slash) {
      e.preventDefault();
      open();
    }
  });
})();
