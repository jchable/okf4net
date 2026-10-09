// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The concept page's head (spec §11.3 C5, C6; §11.4 X10; §12.3): places the
// chip glyphs in the empty slots HtmlWriter wrote, flips the stale chip at
// the deadline (§4.4: on load and whenever the page becomes visible again),
// adds "Show all" to the frontmatter box, and puts its type's glyph before
// each "Referenced by" row. It reads only the fixed data-okf-* values the C#
// wrote and the index -- never bundle text; every query starts from a chrome
// container (getElementById or "body > …") and a class selection also
// requires its data-okf-* attribute, so body content wearing these class
// names is never touched (spec §12.6). Loaded on the index too, where it does
// nothing. Each part needs only what it uses: "Show all" works without
// OkfShapes, OkfSite or the index; the chip glyphs without the index; the
// stale chip's text without OkfShapes (only its hourglass needs it).
(function () {
  "use strict";
  if (document.documentElement.getAttribute("data-okf-view") !== "page") { return; }
  var head = document.querySelector("body > .okf-layout > main > .okf-page-head");
  if (!head) { return; }
  var site = window.OkfSite;
  var shapes = window.OkfShapes;

  // C6: "Show all" / "Show fewer", only when some entries are folded.
  var box = document.getElementById("okf-fm");
  var grid = document.getElementById("okf-fm-grid");
  if (box && grid && head.contains(box) && box.contains(grid) && grid.querySelector(".okf-fm-cell[data-okf-extra]")) {
    var toggle = document.createElement("button");
    toggle.setAttribute("class", "okf-fm-toggle");
    toggle.setAttribute("type", "button");
    toggle.textContent = "Show all";
    toggle.setAttribute("aria-expanded", "false");
    toggle.setAttribute("aria-controls", "okf-fm-grid");
    toggle.addEventListener("click", function () {
      var expand = !box.hasAttribute("data-okf-expanded");
      if (expand) { box.setAttribute("data-okf-expanded", ""); } else { box.removeAttribute("data-okf-expanded"); }
      toggle.setAttribute("aria-expanded", expand ? "true" : "false");
      toggle.textContent = expand ? "Show fewer" : "Show all";
    });
    var boxHead = box.firstElementChild;
    (boxHead && boxHead.classList.contains("okf-fm-head") ? boxHead : box).appendChild(toggle);
  }

  // C5: the type glyph (chip context, drawn white on the ink chip) and the
  // trust glyph (flag context), from the fixed values the C# wrote.
  if (shapes) {
    var typeSlot = head.querySelector(".okf-chip-type > .okf-chip-glyph[data-okf-slot]");
    if (typeSlot) {
      var slot = typeSlot.getAttribute("data-okf-slot");
      if (/^[0-5]$/.test(slot)) { typeSlot.appendChild(shapes.icon(shapes.KINDS[Number(slot)], "chip")); }
    }
    var trustSlot = head.querySelector(".okf-chip-trust > .okf-chip-glyph[data-okf-trust]");
    if (trustSlot) {
      var trust = trustSlot.getAttribute("data-okf-trust");
      if (trust === "human" || trust === "machine") { trustSlot.appendChild(shapes.icon(trust, "flag")); }
    }
  }

  var index = site ? site.readIndex(window) : null;
  if (!index) { return; }
  var positions = new Map();
  for (var i = 0; i < index.concepts.length; i++) {
    var entry = index.concepts[i];
    if (entry !== null && typeof entry === "object") { positions.set(entry.id, i); }
  }

  // C5: "stale after D" until the deadline, then "stale since D" with the
  // hourglass (A24); D is the index's staleAfterDate, never rebuilt. A
  // concept whose entry carries no date leaves the chip as written.
  var staleSlot = head.querySelector(".okf-chip-stale > .okf-chip-glyph[data-okf-stale]");
  var current = positions.get(document.documentElement.getAttribute("data-okf-concept"));
  var staleText = staleSlot ? staleSlot.nextElementSibling : null;
  var concept = current === undefined ? null : index.concepts[current];
  if (staleSlot && staleText && staleText.classList.contains("okf-chip-text") && concept && typeof concept.staleAfterDate === "string") {
    var staleChip = staleSlot.parentElement;
    var date = concept.staleAfterDate;
    var refreshStale = function () {
      if (site.isStale(concept.staleAfterMs, Date.now())) {
        if (shapes && !staleSlot.firstChild) { staleSlot.appendChild(shapes.icon("stale", "flag")); }
        staleText.textContent = "stale since " + date;
        staleChip.setAttribute("data-okf-stale-now", "");
      } else {
        while (staleSlot.firstChild) { staleSlot.removeChild(staleSlot.firstChild); }
        staleText.textContent = "stale after " + date;
        staleChip.removeAttribute("data-okf-stale-now");
      }
    };
    refreshStale();
    document.addEventListener("visibilitychange", function () {
      if (document.visibilityState === "visible") { refreshStale(); }
    });
  }

  // X10: each "Referenced by" row gets its source's type glyph, titled when
  // the index names a type.
  var context = document.getElementById("okf-context");
  var rows = shapes && context ? context.querySelectorAll(".okf-backlinks a.okf-row[data-okf-target]") : [];
  for (var r = 0; r < rows.length; r++) {
    var pos = positions.get(rows[r].getAttribute("data-okf-target"));
    if (pos === undefined) { continue; }
    var type = index.concepts[pos].type;
    var label = typeof type === "string" ? shapes.typeLabel(type) : undefined;
    rows[r].insertBefore(shapes.icon(shapes.kindOf(index, pos), "icon", label), rows[r].firstChild);
  }
})();
