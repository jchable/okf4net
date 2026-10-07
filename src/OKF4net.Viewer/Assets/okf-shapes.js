// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The one source of every type, trust, staleness and ghost shape the viewer
// draws (spec §12.2): explorer, palette, chips, lists, legends and graphs all
// call this module; none draws such a shape itself. Pure geometry in absolute
// coordinates (no transform), a fixed SVG vocabulary, colours only through
// fixed classes that viewer.css maps to the §11.0 tokens, and every number
// checked finite. Bundle text never becomes a class, an id or an attribute
// here: titles and labels go through textContent. Loading this file only
// defines window.OkfShapes; elements are created in this window's document.
(function () {
  "use strict";
  var SVG_NS = "http://www.w3.org/2000/svg";
  var doc = window.document;

  var KINDS = Object.freeze(["circle", "square", "diamond", "triangle", "ring", "other"]);
  var OTHER_SLOT = 5;
  var BOXES = Object.freeze({ icon: 12, chip: 10, flag: 10 });

  // Class of each shape: the six type kinds by rank, then the marks.
  var SHAPE_CLASS = Object.freeze({
    circle: "okf-shape-0", square: "okf-shape-1", diamond: "okf-shape-2",
    triangle: "okf-shape-3", ring: "okf-shape-4", other: "okf-shape-5",
    human: "okf-trust-human", machine: "okf-trust-machine", stale: "okf-stale-mark", ghost: "okf-ghost-mark",
  });

  function cell(values, ring, focus) {
    return Object.freeze({
      size: values[0],
      stroke: values.length > 1 ? values[1] : 0,
      dash: values.length > 2 ? Object.freeze(values[2].slice()) : null,
      ring: ring,
      focus: focus,
    });
  }

  function row(ring, focus, cells) {
    var out = {};
    var kinds = Object.keys(cells);
    for (var i = 0; i < kinds.length; i++) { out[kinds[i]] = cell(cells[kinds[i]], ring, focus); }
    return Object.freeze(out);
  }

  // Spec §12.2: size (then stroke, then dash) per context and shape, read in
  // the mockups; the cells no mockup shows are drafting choices (§13).
  var SIZES = Object.freeze({
    icon: row(0, 0, { circle: [10], square: [9], diamond: [11.3], triangle: [11], ring: [10, 2], other: [9, 1.5], ghost: [10, 1.2, [2, 2]] }),
    chip: row(0, 0, { circle: [8], square: [8], diamond: [9], triangle: [9], ring: [8, 1.5], other: [8, 1.5] }),
    flag: row(0, 0, { human: [8], machine: [8, 2], stale: [10] }),
    local: row(0, 0, { circle: [20], square: [23], diamond: [25.5], triangle: [22], ring: [20, 2.5], other: [20, 1.5], ghost: [21.2, 1.4, [3, 3]] }),
    localCenter: row(2, 0, { circle: [26], square: [28], diamond: [31.1], triangle: [24], ring: [26, 3], other: [26, 2] }),
    graph: row(2.4, 2, { circle: [26], square: [30], diamond: [31.1], triangle: [24], ring: [26, 3], other: [26, 2], ghost: [27.6, 1.6, [3, 3]] }),
  });

  var TRUST_NAMES = Object.freeze(["human-reviewed", "machine-confirmed", "unverified"]);

  function own(table, key) {
    return typeof key === "string" && Object.prototype.hasOwnProperty.call(table, key);
  }

  function checkKind(kind) {
    if (!own(SHAPE_CLASS, kind)) { throw new TypeError("OkfShapes: unknown shape " + String(kind)); }
  }

  function finite(value, what) {
    if (typeof value !== "number" || !Number.isFinite(value)) { throw new TypeError("OkfShapes: " + what + " is not a finite number"); }
    return value;
  }

  function nonNegative(value, what) {
    if (finite(value, what) < 0) { throw new TypeError("OkfShapes: " + what + " is negative"); }
    return value;
  }

  function positive(value, what) {
    if (!(finite(value, what) > 0)) { throw new TypeError("OkfShapes: " + what + " is not positive"); }
    return value;
  }

  // Rounded to the hundredth and written by String (spec §12.2). The RESULT is
  // checked: arithmetic on finite inputs can still overflow to +-Infinity.
  function fmt(value) {
    var rounded = Math.round(value * 100) / 100;
    if (!Number.isFinite(rounded)) { throw new TypeError("OkfShapes: a coordinate is not a finite number"); }
    return String(rounded);
  }

  function readOptions(options) {
    var o = options || {};
    var dash = o.dash === undefined ? null : o.dash;
    if (dash !== null) {
      if (!Array.isArray(dash) || dash.length !== 2) { throw new TypeError("OkfShapes: dash is neither a pair nor null"); }
      nonNegative(dash[0], "dash");
      nonNegative(dash[1], "dash");
    }
    return {
      stroke: o.stroke === undefined ? 0 : nonNegative(o.stroke, "stroke"),
      dash: dash,
      ring: o.ring === undefined ? 0 : nonNegative(o.ring, "ring"),
      focus: o.focus === undefined ? 0 : nonNegative(o.focus, "focus"),
    };
  }

  function svg(name) {
    return doc.createElementNS(SVG_NS, name);
  }

  // "M6 0.35 L11.65 6 ... Z": a command is glued to its first number; numbers
  // and commands are separated by one space.
  function pathData(points) {
    var parts = [];
    for (var i = 0; i < points.length; i++) {
      parts.push(points[i][0] + fmt(points[i][1]), fmt(points[i][2]));
    }
    parts.push("Z");
    return parts.join(" ");
  }

  // One element whose bounding box fits the size x size square centred on
  // (cx, cy); for a stroked shape, the stroke's OUTER edge is on that square.
  function shape(kind, cx, cy, size, options) {
    checkKind(kind);
    finite(cx, "cx");
    finite(cy, "cy");
    positive(size, "size");
    var o = readOptions(options);
    var h = size / 2;
    var w = o.stroke;
    // A stroke-only shape (its stroke IS the shape) needs a stroke, and a stroke
    // wider than the shape would give a negative r or side.
    if (kind === "ring" || kind === "machine" || kind === "ghost" || kind === "other") {
      if (!(w > 0)) { throw new TypeError("OkfShapes: " + kind + " needs a positive stroke"); }
      if (size - w < 0) { throw new TypeError("OkfShapes: the stroke of a " + kind + " is wider than its size"); }
    }
    var el;
    if (kind === "circle" || kind === "human") {
      el = svg("circle");
      el.setAttribute("cx", fmt(cx));
      el.setAttribute("cy", fmt(cy));
      el.setAttribute("r", fmt(h));
    } else if (kind === "square") {
      el = svg("rect");
      el.setAttribute("x", fmt(cx - h));
      el.setAttribute("y", fmt(cy - h));
      el.setAttribute("width", fmt(size));
      el.setAttribute("height", fmt(size));
    } else if (kind === "diamond") {
      el = svg("path");
      el.setAttribute("d", pathData([["M", cx, cy - h], ["L", cx + h, cy], ["L", cx, cy + h], ["L", cx - h, cy]]));
    } else if (kind === "triangle") {
      el = svg("path");
      el.setAttribute("d", pathData([["M", cx, cy - h], ["L", cx + h, cy + h], ["L", cx - h, cy + h]]));
    } else if (kind === "ring" || kind === "machine" || kind === "ghost") {
      el = svg("circle");
      el.setAttribute("cx", fmt(cx));
      el.setAttribute("cy", fmt(cy));
      el.setAttribute("r", fmt((size - w) / 2));
      el.setAttribute("stroke-width", fmt(w));
      if (kind === "ghost" && o.dash) {
        el.setAttribute("stroke-dasharray", fmt(o.dash[0]) + " " + fmt(o.dash[1]));
      }
    } else if (kind === "other") {
      var side = size - w;
      el = svg("rect");
      el.setAttribute("x", fmt(cx - side / 2));
      el.setAttribute("y", fmt(cy - side / 2));
      el.setAttribute("width", fmt(side));
      el.setAttribute("height", fmt(side));
      el.setAttribute("stroke-width", fmt(w));
    } else {
      // stale: an hourglass, two triangles joined tip to tip, 0.9 x size wide.
      var a = 0.45 * size;
      el = svg("path");
      el.setAttribute("d", pathData([["M", cx - a, cy - h], ["L", cx + a, cy - h], ["L", cx, cy], ["L", cx + a, cy + h], ["L", cx - a, cy + h], ["L", cx, cy]]));
    }
    el.setAttribute("class", SHAPE_CLASS[kind]);
    return el;
  }

  function sizeOf(context, kind) {
    if (!own(SIZES, context) || !own(SIZES[context], kind)) {
      throw new TypeError("OkfShapes: no size for " + String(kind) + " in context " + String(context));
    }
    return SIZES[context][kind];
  }

  // A self-contained glyph for HTML: an svg of the context's box with the
  // shape centred in it; `title`, as text, in a first <title>.
  function icon(kind, context, title) {
    checkKind(kind);
    if (!own(BOXES, context)) { throw new TypeError("OkfShapes: unknown icon context " + String(context)); }
    var s = sizeOf(context, kind);
    var box = BOXES[context];
    var el = svg("svg");
    el.setAttribute("class", "okf-glyph");
    el.setAttribute("width", String(box));
    el.setAttribute("height", String(box));
    el.setAttribute("viewBox", "0 0 " + box + " " + box);
    el.setAttribute("aria-hidden", "true");
    el.setAttribute("focusable", "false");
    if (title !== undefined && title !== null) {
      var t = svg("title");
      t.textContent = String(title);
      el.appendChild(t);
    }
    el.appendChild(shape(kind, box / 2, box / 2, s.size, s));
    return el;
  }

  function outline(className, cx, cy, side, width, dashed) {
    var r = svg("rect");
    r.setAttribute("class", className);
    r.setAttribute("x", fmt(cx - side / 2));
    r.setAttribute("y", fmt(cy - side / 2));
    r.setAttribute("width", fmt(side));
    r.setAttribute("height", fmt(side));
    r.setAttribute("stroke-width", fmt(width));
    if (dashed) { r.setAttribute("stroke-dasharray", "3 3"); }
    return r;
  }

  // A graph node: focus outline (side + 20, dashed), selection outline
  // (side + 12), then the shape. The outlines show only under the fixed
  // classes okf-focused / okf-selected the caller toggles on the <g>. No
  // <title>, no <text>: the caller adds them (X7, G11, G13).
  function node(kind, cx, cy, size, options) {
    checkKind(kind);
    finite(cx, "cx");
    finite(cy, "cy");
    positive(size, "size");
    var o = readOptions(options);
    var g = svg("g");
    g.setAttribute("class", "okf-node");
    if (o.focus > 0) { g.appendChild(outline("okf-node-focus", cx, cy, size + 20, o.focus, true)); }
    if (o.ring > 0) { g.appendChild(outline("okf-node-ring", cx, cy, size + 12, o.ring, false)); }
    g.appendChild(shape(kind, cx, cy, size, options));
    return g;
  }

  function typeLabel(name) {
    return name === "" ? "(no type)" : String(name);
  }

  // The rank (0..5) of the concept at `position`, read from the index's
  // types table, never recomputed; OTHER_SLOT whenever anything is missing
  // or out of bounds.
  function slotOf(index, position) {
    if (!index || !Array.isArray(index.concepts) || !Array.isArray(index.types)) { return OTHER_SLOT; }
    if (!Number.isInteger(position) || position < 0 || position >= index.concepts.length) { return OTHER_SLOT; }
    var concept = index.concepts[position];
    var at = concept ? concept.typeIndex : undefined;
    if (!Number.isInteger(at) || at < 0 || at >= index.types.length) { return OTHER_SLOT; }
    var slot = index.types[at] ? index.types[at].slot : undefined;
    return Number.isInteger(slot) && slot >= 0 && slot <= OTHER_SLOT ? slot : OTHER_SLOT;
  }

  function kindOf(index, position) {
    return KINDS[slotOf(index, position)];
  }

  function trustKind(trust) {
    return trust === "human-reviewed" ? "human" : trust === "machine-confirmed" ? "machine" : null;
  }

  // Ranks 0-4 in the order of `types`, then one "Other types" entry summing
  // every type of the other slot, when there is one.
  function typeLegendEntries(index) {
    var out = [];
    var types = index && Array.isArray(index.types) ? index.types : [];
    var other = 0;
    var hasOther = false;
    for (var i = 0; i < types.length; i++) {
      var t = types[i];
      var count = t && Number.isInteger(t.count) ? t.count : 0;
      if (t && Number.isInteger(t.slot) && t.slot >= 0 && t.slot < OTHER_SLOT) {
        out.push({ role: "type", slot: t.slot, label: typeLabel(t.name), count: count });
      } else {
        hasOther = true;
        other += count;
      }
    }
    if (hasOther) { out.push({ role: "type", slot: OTHER_SLOT, label: "Other types", count: other }); }
    return out;
  }

  function html(tag, className, text) {
    var el = doc.createElement(tag);
    if (className) { el.className = className; }
    if (text !== undefined) { el.textContent = text; }
    return el;
  }

  function legendItem(entry) {
    var li = doc.createElement("li");
    var role = entry ? entry.role : undefined;
    if (role === "type") {
      var slot = entry.slot;
      if (!Number.isInteger(slot) || slot < 0 || slot > OTHER_SLOT) { throw new TypeError("OkfShapes: legend slot out of range"); }
      li.appendChild(icon(KINDS[slot], "icon"));
      li.appendChild(html("span", "", String(entry.label)));
      if (entry.count !== undefined) { li.appendChild(html("span", "okf-legend-count", String(entry.count))); }
    } else if (role === "trust") {
      if (TRUST_NAMES.indexOf(entry.trust) === -1) { throw new TypeError("OkfShapes: unknown trust tier"); }
      var kind = trustKind(entry.trust);
      li.appendChild(kind ? icon(kind, "flag") : html("span", "okf-glyph-blank"));
      li.appendChild(html("span", "", entry.trust));
    } else if (role === "stale") {
      li.appendChild(icon("stale", "flag"));
      var label = html("span", "", "stale (now " + String.fromCharCode(0x2265) + " ");
      label.appendChild(html("span", "okf-legend-mono", "stale_after"));
      label.appendChild(doc.createTextNode(")"));
      li.appendChild(label);
    } else if (role === "ghost") {
      li.appendChild(icon("ghost", "icon"));
      li.appendChild(html("span", "", "absent concept"));
    } else {
      throw new TypeError("OkfShapes: unknown legend role " + String(role));
    }
    return li;
  }

  function legend(entries) {
    var ul = html("ul", "okf-legend");
    for (var i = 0; i < entries.length; i++) { ul.appendChild(legendItem(entries[i])); }
    return ul;
  }

  window.OkfShapes = Object.freeze({
    KINDS: KINDS,
    OTHER_SLOT: OTHER_SLOT,
    BOXES: BOXES,
    SIZES: SIZES,
    typeLabel: typeLabel,
    slotOf: slotOf,
    kindOf: kindOf,
    trustKind: trustKind,
    shape: shape,
    icon: icon,
    node: node,
    typeLegendEntries: typeLegendEntries,
    legend: legend,
  });
})();
