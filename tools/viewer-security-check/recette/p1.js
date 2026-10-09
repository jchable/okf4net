// SPDX-License-Identifier: LGPL-3.0-or-later
//
// P1's recette (2026-10-07, run by hand outside the repository), ported and
// kept (spec §12.8): its checks C1–C11, renamed RC1–RC11 so they are not
// confused with C1–C8 of spec §11.3. Pages are chosen from the generated
// index instead of hard-coded paths, and selectors follow the P1.1 DOM.
"use strict";
const fs = require("fs");
const { fileURLToPath } = require("url");

function choose(ctx) {
  const idx = ctx.lib.readIndex(ctx.siteDir);
  const depth = (c) => c.path.split("/").length - 1;
  const max = Math.max(...idx.concepts.map(depth));
  const find = (nodes) => {
    for (const n of nodes) {
      if (n.concept >= 0 && n.children.length > 0) { return idx.concepts[n.concept]; }
      const hit = find(n.children);
      if (hit) { return hit; }
    }
    return null;
  };
  return {
    deep: idx.concepts.find((c) => depth(c) === max).path,
    far: idx.concepts[idx.concepts.length - 1].path,
    both: find(idx.tree),
    toc: ctx.lib.pageWithSections(ctx.siteDir, 3),
  };
}

const at = (ctx, rel) => ctx.lib.pageUrl(ctx.site, rel);
// The page's path, decoded, so it compares with an index path.
const here = (page) => { try { return decodeURIComponent(new URL(page.url()).pathname); } catch (e) { return page.url(); } };
const active = (page) => page.evaluate(() => { const a = document.activeElement; return a ? (a.id || a.className || a.tagName) : null; });
const paletteOpen = (page) => page.evaluate(() => { const b = document.querySelector("body > .okf-palette-backdrop"); return !!b && !b.hidden; });
const exists = (page) => { try { return fs.existsSync(fileURLToPath(page.url().split("#")[0])); } catch (e) { return false; } };
const clean = (page) => page.okfTracked.errors.length === 0;

async function rc1(ctx, p) {
  const page = await ctx.newPage();
  const times = {};
  for (const rel of ["index.html", p.deep, p.far]) {
    const t0 = Date.now();
    await page.goto(at(ctx, rel));
    times[rel] = Date.now() - t0;
  }
  await ctx.shot(page, "RC1");
  return { pass: Math.max(...Object.values(times)) < 2000 && clean(page), times };
}

async function rc2(ctx, p) {
  const page = await ctx.newPage();
  await page.goto(at(ctx, p.deep));
  const deep = await page.evaluate(() => {
    const nav = document.getElementById("okf-explorer");
    const current = nav.querySelector('a.okf-tree-link[aria-current="page"]');
    const toggles = [];
    let li = current ? current.closest("li").parentElement.closest("li") : null;
    while (li) {
      const t = li.querySelector(":scope > .okf-tree-row > .okf-tree-toggle");
      if (t) { toggles.push(t.getAttribute("aria-expanded")); }
      li = li.parentElement.closest("li");
    }
    return { hidden: nav.hidden, current: !!current, ancestorsOpen: toggles.every((x) => x === "true") };
  });
  await page.goto(at(ctx, p.far));
  const far = await page.evaluate(() => {
    const nav = document.getElementById("okf-explorer");
    const r = nav.getBoundingClientRect();
    const c = nav.querySelector('a.okf-tree-link[aria-current="page"]').getBoundingClientRect();
    return c.top >= Math.max(0, r.top) && c.bottom <= Math.min(innerHeight, r.bottom);
  });
  await page.goto(at(ctx, "index.html"));
  // On index.html only the top level shows: open the node's ancestors first,
  // outermost first, with their own chevrons (a node with both roles can sit
  // several folders deep).
  const openAncestors = () => page.evaluate((id) => {
    const link = document.querySelector(`#okf-explorer a.okf-tree-link[data-okf-id="${CSS.escape(id)}"]`);
    const chain = [];
    for (let li = link.closest("li").parentElement.closest("li"); li; li = li.parentElement.closest("li")) { chain.unshift(li); }
    for (const li of chain) {
      const t = li.querySelector(":scope > .okf-tree-row > .okf-tree-toggle");
      if (t && t.getAttribute("aria-expanded") !== "true") { t.click(); }
    }
  }, p.both.id);
  await openAncestors();
  const row = page.locator(`#okf-explorer a.okf-tree-link[data-okf-id="${p.both.id}"]`).locator("xpath=..");
  const toggle = row.locator(".okf-tree-toggle");
  const before = await toggle.getAttribute("aria-expanded");
  await toggle.click();
  const after = await toggle.getAttribute("aria-expanded");
  const stayed = page.url().endsWith("index.html");
  await page.fill("#okf-tree-filter", p.both.id.split("/").pop());
  const filtered = await page.evaluate(() => Array.from(document.querySelectorAll("#okf-explorer a.okf-tree-link")).filter((a) => !a.closest("[hidden]")).length);
  await page.fill("#okf-tree-filter", "");
  // An empty filter restores the default state: the ancestors close again.
  await openAncestors();
  await Promise.all([page.waitForNavigation(), row.locator("a.okf-tree-link").click()]);
  const opened = here(page).endsWith("/" + p.both.path);
  await ctx.shot(page, "RC2");
  return { pass: !deep.hidden && deep.current && deep.ancestorsOpen && far && before !== after && stayed && filtered >= 1 && opened && clean(page), deep, far, before, after, filtered, opened };
}

async function rc3(ctx, p) {
  const page = await ctx.newPage();
  await page.goto(at(ctx, p.deep));
  const res = {};
  for (const [name, selector] of [["explorer", "#okf-explorer a.okf-tree-link"], ["backlink", "#okf-context .okf-backlinks a.okf-row"], ["body", "#okf-body a[href$='.html']"]]) {
    const link = page.locator(selector).first();
    if (await link.count()) {
      await Promise.all([page.waitForNavigation(), link.click()]);
      res[name] = exists(page);
      await page.goBack();
    }
  }
  return { pass: res.explorer === true && res.backlink !== false && res.body !== false && clean(page), res };
}

async function rc4(ctx, p) {
  const page = await ctx.newPage();
  await page.goto(at(ctx, p.toc));
  const r = {};
  await page.click("#okf-tools .okf-palette-open");
  r.button = (await paletteOpen(page)) && (await active(page)) === "okf-palette-input";
  await page.keyboard.press("Escape");
  r.escapeReturnsToOpener = !(await paletteOpen(page)) && /okf-palette-open/.test(await active(page));
  await page.locator("#okf-body p").first().click();
  await page.keyboard.press("Control+k");
  r.ctrlK = await paletteOpen(page);
  for (let i = 0; i < 5; i++) { await page.keyboard.press("Tab"); }
  r.tabStays = await page.evaluate(() => !!document.activeElement.closest(".okf-palette"));
  for (let i = 0; i < 5; i++) { await page.keyboard.press("Shift+Tab"); }
  r.shiftTabStays = await page.evaluate(() => !!document.activeElement.closest(".okf-palette"));
  await page.keyboard.press("Escape");
  await page.locator("#okf-body p").first().click();
  await page.keyboard.press("/");
  r.slash = await paletteOpen(page);
  await page.keyboard.type(p.far.replace(/\.html$/, "").split("/").pop(), { delay: 10 });
  await page.keyboard.press("ArrowDown");
  const target = await page.evaluate(() => {
    const input = document.getElementById("okf-palette-input");
    const option = document.getElementById(input.getAttribute("aria-activedescendant"));
    return option && option.querySelector(".okf-palette-id").textContent;
  });
  await Promise.all([page.waitForNavigation(), page.keyboard.press("Enter")]);
  r.arrowEnter = here(page).endsWith("/" + target + ".html");
  r.aria = await page.evaluate(() => {
    const d = document.querySelector(".okf-palette");
    return d.getAttribute("role") === "dialog" && d.getAttribute("aria-modal") === "true" && !!d.querySelector("[role=status]");
  });
  return { pass: Object.values(r).every(Boolean) && clean(page), ...r };
}

async function rc5(ctx) {
  const page = await ctx.newPage();
  await page.goto(at(ctx, "index.html"));
  await page.keyboard.press("Control+k");
  await page.keyboard.type("o");
  const total = await page.evaluate(() => document.querySelectorAll("[role=option]").length);
  let hidden = 0;
  for (let i = 0; i < total; i++) {
    await page.keyboard.press("ArrowDown");
    if (i % 25 === 0 || i > total - 3) {
      const visible = await page.evaluate(() => {
        const input = document.getElementById("okf-palette-input");
        const o = document.getElementById(input.getAttribute("aria-activedescendant"));
        const r = o.getBoundingClientRect();
        const l = o.parentElement.getBoundingClientRect();
        return r.top >= l.top - 1 && r.bottom <= l.bottom + 1;
      });
      if (!visible) { hidden++; }
    }
  }
  await ctx.shot(page, "RC5");
  const broken = await page.evaluate(() => Array.from(document.querySelectorAll("[role=option]")).slice(0, 60).filter((o) => {
    const t = o.querySelector(".okf-palette-title");
    const lh = parseFloat(getComputedStyle(t).lineHeight) || 20;
    return !/\s/.test(t.textContent) && t.textContent.length < 30 && t.getBoundingClientRect().height > lh * 1.5;
  }).map((o) => o.querySelector(".okf-palette-title").textContent));
  return { pass: total > 50 && hidden === 0 && broken.length === 0 && clean(page), total, hidden, broken: broken.slice(0, 5) };
}

async function rc6(ctx, p) {
  const page = await ctx.newPage();
  await page.goto(at(ctx, p.far));
  await page.evaluate(() => { try { localStorage.removeItem("okf-theme"); } catch (e) { /* none */ } });
  await page.reload();
  const state = () => page.evaluate(() => ({
    attr: document.documentElement.getAttribute("data-theme"),
    pressed: document.getElementById("okf-theme-toggle").getAttribute("aria-pressed"),
    scheme: getComputedStyle(document.documentElement).colorScheme,
  }));
  await page.click("#okf-theme-toggle");
  const toggled = await state();
  await ctx.shot(page, "RC6");
  await page.goto(at(ctx, p.toc));
  const other = await state();
  await page.reload();
  const reload = await state();
  await page.click("#okf-theme-toggle");
  await page.evaluate(() => { try { localStorage.removeItem("okf-theme"); } catch (e) { /* none */ } });
  return { pass: toggled.attr === "dark" && toggled.pressed === "true" && /dark/.test(toggled.scheme) && other.attr === "dark" && reload.attr === "dark" && clean(page), toggled, other, reload };
}

async function rc7(ctx, p) {
  const out = {};
  for (const theme of ["light", "dark"]) {
    const page = await ctx.newPage();
    await page.goto(at(ctx, "index.html"));
    await page.evaluate((t) => { try { localStorage.setItem("okf-theme", t); } catch (e) { /* none */ } }, theme);
    await page.goto(at(ctx, p.toc));
    const c = await page.evaluate(() => {
      const bg = getComputedStyle(document.body).backgroundColor;
      const pick = (sel) => { const e = document.querySelector(sel); return e ? getComputedStyle(e).color : null; };
      return { bg, text: pick("#okf-body p"), link: pick("#okf-body a, #okf-context a"), tree: pick("#okf-explorer .okf-tree-link"), current: pick('#okf-explorer [aria-current="page"]'), toc: pick("#okf-toc a") };
    });
    await page.keyboard.press("Control+k");
    await page.keyboard.type("o");
    const option = await page.evaluate(() => {
      const o = document.querySelector('[role=option][aria-selected="true"]');
      return { fg: getComputedStyle(o.querySelector(".okf-palette-title")).color, bg: getComputedStyle(o).backgroundColor };
    });
    const ratios = {};
    for (const k of ["text", "link", "tree", "current", "toc"]) { if (c[k]) { ratios[k] = ctx.lib.contrast(c[k], c.bg); } }
    ratios.activeOption = ctx.lib.contrast(option.fg, /rgba?\(0, 0, 0, 0\)/.test(option.bg) ? c.bg : option.bg);
    out[theme] = ratios;
    await page.evaluate(() => { try { localStorage.removeItem("okf-theme"); } catch (e) { /* none */ } });
  }
  return { pass: [...Object.values(out.light), ...Object.values(out.dark)].every((x) => x >= 4.5), ...out };
}

async function rc8(ctx, p) {
  const page = await ctx.newPage();
  await page.goto(at(ctx, p.toc));
  const items = page.locator("#okf-toc a");
  const n = await items.count();
  const ids = await page.evaluate(() => Array.from(document.querySelectorAll("#okf-toc a"), (a) => a.getAttribute("href").slice(1)));
  const r = { n };
  await items.nth(0).click();
  r.first = await active(page);
  await items.nth(n - 1).click();
  r.last = await active(page);
  await page.click("#okf-tools .okf-palette-open");
  await page.keyboard.press("Escape");
  await items.nth(n - 1).click();
  r.reclick = await active(page);
  await page.goBack();
  await page.waitForTimeout(200);
  r.back = await active(page);
  await page.goForward();
  await page.waitForTimeout(200);
  r.forward = await active(page);
  return { pass: r.first === ids[0] && r.last === ids[n - 1] && r.reclick === r.last && r.back === r.first && r.forward === r.last && clean(page), ...r };
}

async function rc9(ctx, p) {
  const page = await ctx.newPage();
  const id = await (async () => {
    await page.goto(at(ctx, p.toc));
    return page.evaluate(() => document.querySelectorAll("#okf-toc a")[1].getAttribute("href").slice(1));
  })();
  await page.goto("about:blank");
  await page.goto(at(ctx, p.toc) + "#" + id.replace(/^okf-h-/, ""));
  await page.waitForTimeout(200);
  const focus = await active(page);
  return { pass: focus === id && clean(page), focus, expected: id };
}

async function rc10(ctx, p) {
  const page = await ctx.newPage({ viewport: { width: 390, height: 844 } });
  const r = {};
  for (const rel of [p.deep, p.far, "index.html"]) {
    await page.goto(at(ctx, rel));
    r[rel] = await page.evaluate(() => ({ scrollWidth: document.documentElement.scrollWidth, mainTop: Math.round(document.querySelector("main").getBoundingClientRect().top + scrollY) }));
  }
  await ctx.shot(page, "RC10-page");
  await page.keyboard.press("Control+k");
  await page.keyboard.type(p.deep.replace(/\.html$/, "").split("/").pop());
  r.palette = await page.evaluate(() => {
    const box = document.querySelector(".okf-palette").getBoundingClientRect();
    const options = Array.from(document.querySelectorAll("[role=option]"));
    return { left: Math.round(box.left), right: Math.round(box.right), overflowing: options.filter((o) => o.scrollWidth > o.clientWidth + 1).length };
  });
  await ctx.shot(page, "RC10-palette");
  const pages = [p.deep, p.far, "index.html"].map((rel) => r[rel]);
  return { pass: pages.every((v) => v.scrollWidth <= 390 && v.mainTop < 400) && r.palette.left >= 0 && r.palette.right <= 390 && r.palette.overflowing === 0 && clean(page), ...r };
}

async function rc11(ctx) {
  const page = await ctx.newPage();
  const idx = ctx.lib.readIndex(ctx.acmeDir);
  for (const c of idx.concepts) { await page.goto(ctx.lib.pageUrl(ctx.acme, c.path)); }
  await page.keyboard.press("Control+k");
  await page.keyboard.type("order");
  const results = await page.evaluate(() => document.querySelectorAll("[role=option]").length);
  await ctx.shot(page, "RC11");
  return { pass: idx.concepts.length === 9 && results > 0 && clean(page), concepts: idx.concepts.length, results };
}

async function run(ctx) {
  const p = choose(ctx);
  const checks = { RC1: rc1, RC2: rc2, RC3: rc3, RC4: rc4, RC5: rc5, RC6: rc6, RC7: rc7, RC8: rc8, RC9: rc9, RC10: rc10, RC11: rc11 };
  const out = {};
  for (const [id, fn] of Object.entries(checks)) {
    if (ctx.wanted(id)) { out[id] = await ctx.lib.guard(() => fn(ctx, p)); }
  }
  return out;
}

module.exports = { run };
