// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Heading anchors, the "On this page" list, its current section and fragment
// resolution (spec §5, §11.4 X4). Runs after viewer.js has rendered and
// sanitized #okf-body, and does not need the site index. Ids are GENERATED
// here, after sanitization: always "okf-h-" + a slug of letters, digits and
// hyphens. No id or name from bundle content is ever admitted (the sanitizer
// strips them), so content cannot clobber a global such as window.OKF_INDEX.
(function () {
  "use strict";
  var site = window.OkfSite;
  var body = document.getElementById("okf-body");
  if (!site || !body) { return; }

  var headings = body.querySelectorAll("h1, h2, h3, h4, h5, h6");
  var texts = [];
  for (var i = 0; i < headings.length; i++) { texts.push(headings[i].textContent); }
  var slugs = site.uniqueSlugs(texts);
  for (var j = 0; j < headings.length; j++) {
    headings[j].setAttribute("id", site.HEADING_PREFIX + slugs[j]);
    headings[j].setAttribute("tabindex", "-1");
  }

  // The listed entries, in document order: { link, heading }.
  var entries = [];
  var toc = document.getElementById("okf-toc");
  var list = toc ? toc.querySelector("ul") : null;
  if (list) {
    for (var k = 0; k < headings.length; k++) {
      var level = headings[k].tagName;
      if (level !== "H2" && level !== "H3") { continue; }
      var item = site.element(document, "li", level === "H3" ? "okf-toc-sub" : "");
      var link = site.element(document, "a", "", headings[k].textContent);
      link.setAttribute("href", "#" + headings[k].id);
      item.appendChild(link);
      list.appendChild(item);
      entries.push({ link: link, heading: headings[k] });
    }
    if (entries.length > 0) {
      toc.hidden = false;
      var context = document.getElementById("okf-context");
      if (context) { context.hidden = false; }
    }
  }

  // X4: the current section is the last listed heading whose top is above a
  // quarter of the window, else the first one.
  function updateCurrent() {
    if (entries.length === 0) { return; }
    var line = window.innerHeight * 0.25;
    var current = entries[0];
    for (var e = 0; e < entries.length; e++) {
      if (entries[e].heading.getBoundingClientRect().top < line) { current = entries[e]; }
    }
    for (var f = 0; f < entries.length; f++) {
      if (entries[f] === current) {
        entries[f].link.setAttribute("aria-current", "location");
      } else {
        entries[f].link.removeAttribute("aria-current");
      }
    }
  }

  // At most one update per frame, however many scroll events arrive.
  var frame = typeof window.requestAnimationFrame === "function"
    ? function (callback) { window.requestAnimationFrame(callback); }
    : function (callback) { window.setTimeout(callback, 16); };
  var scheduled = false;
  function scheduleUpdate() {
    if (scheduled) { return; }
    scheduled = true;
    frame(function () {
      scheduled = false;
      updateCurrent();
    });
  }

  // The generated heading a fragment designates, or null. Only elements
  // inside #okf-body qualify.
  function targetOf(hash) {
    var candidates = site.fragmentCandidates(hash);
    for (var c = 0; c < candidates.length; c++) {
      var target = document.getElementById(candidates[c]);
      if (target && body.contains(target)) { return target; }
    }
    return null;
  }

  // Moves reading focus to that heading, then recomputes the current section.
  function go(hash) {
    var target = targetOf(hash);
    if (!target) { return false; }
    if (typeof target.scrollIntoView === "function") { target.scrollIntoView(); }
    target.focus();
    updateCurrent();
    return true;
  }

  // Author links written against the heading text ("#usage") predate the
  // generated ids. A plain click becomes a real fragment navigation to the
  // generated id -- URL, history entry and Back/Forward stay native -- and
  // hashchange then moves the focus. Modified clicks (new tab, new window)
  // are left to the browser: the new page resolves the fragment on load.
  // The contents list gets the same handling: its links already carry the
  // generated id, but a second click on the current one fires no hashchange
  // either, and must still move the focus to the heading.
  function followFragments(container) {
    container.addEventListener("click", function (e) {
      if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) { return; }
      var a = e.target && typeof e.target.closest === "function" ? e.target.closest("a") : null;
      if (!a || !container.contains(a)) { return; }
      var href = a.getAttribute("href");
      if (!href || href.charAt(0) !== "#") { return; }
      var target = targetOf(href);
      if (!target) { return; }
      e.preventDefault();
      // Compare the resolved elements, not strings: location.hash is
      // percent-encoded ("#okf-h-caf%C3%A9") while the id is not ("okf-h-café"),
      // and re-assigning the same fragment fires no hashchange.
      if (targetOf(window.location.hash) === target) {
        go(window.location.hash);
      } else {
        window.location.hash = target.id;
      }
    });
  }
  followFragments(body);
  if (toc) { followFragments(toc); }
  window.addEventListener("hashchange", function () { go(window.location.hash); });
  window.addEventListener("scroll", scheduleUpdate, { passive: true });
  window.addEventListener("resize", scheduleUpdate);
  updateCurrent();
  if (window.location.hash) { go(window.location.hash); }
})();
