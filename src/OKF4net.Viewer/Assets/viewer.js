// SPDX-License-Identifier: LGPL-3.0-or-later
// Client bootstrap for a generated OKF bundle page: read the embedded JSON
// payload, render its markdown, then rewire inter-concept links.
(function () {
  "use strict";

  // marked renders raw HTML by default and no longer exposes a `sanitize`
  // option, so raw HTML is suppressed at the renderer level. Without this,
  // a concept body could inject arbitrary script into the generated page.
  //
  // Verified against the vendored build (marked v15.0.12): both block-level
  // raw HTML (`<script>...</script>` as its own block) and inline raw HTML
  // (`<img onerror=...>` inside a paragraph) are dispatched through this
  // same `renderer.html` hook, so a single override covers both — there is
  // no separate inline-vs-block hook to patch in this version.
  marked.use({
    renderer: {
      html: function () { return ""; },
    },
  });

  // marked v15 renders image/title alt-text through a *second*, separate
  // TextRenderer instance (`marked.TextRenderer`) that the `marked.use({
  // renderer: ... })` override above does not touch — it is constructed
  // fresh internally and never reads back from options. Left unpatched, a
  // concept body like `![<a href="x" onclick="alert(1)">t</a>](img.png)`
  // still smuggles raw markup into the generated `alt="..."` attribute,
  // even with the override above in place, because that code path calls
  // `parser.textRenderer.html(...)`, not `parser.renderer.html(...)`.
  // Patching the shared prototype closes that second path.
  if (marked.TextRenderer) {
    marked.TextRenderer.prototype.html = function () { return ""; };
  }

  var el = document.getElementById("okf-payload");
  if (!el) { return; }
  var payload = JSON.parse(el.textContent);

  var target = document.getElementById("okf-body");
  target.innerHTML = marked.parse(payload.body || "");

  // Rewire internal links from the generation-time table. Anything absent
  // from the table (external URLs, anchors) is left exactly as authored.
  var map = payload.links || {};
  var anchors = target.getElementsByTagName("a");
  for (var i = 0; i < anchors.length; i++) {
    var raw = anchors[i].getAttribute("href");
    if (!raw || !Object.prototype.hasOwnProperty.call(map, raw)) { continue; }
    var entry = map[raw];
    if (entry.exists) {
      anchors[i].setAttribute("href", entry.href);
    } else {
      anchors[i].removeAttribute("href");
      anchors[i].className = "broken";
      anchors[i].setAttribute("title", "broken link: " + raw);
    }
  }
})();
