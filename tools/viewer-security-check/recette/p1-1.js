// SPDX-License-Identifier: LGPL-3.0-or-later
//
// P1.1 recette (spec §11, §12.8): results keyed by §11 id. This first
// version carries the `fonts` control (§11.0, A26); Task 18 of the P1.1 plan
// adds the other controls.
"use strict";

// The seven faces of spec §11.0, A16.
const FACES = [["Inter", 400], ["Inter", 500], ["Inter", 600], ["Inter Tight", 600], ["Inter Tight", 900], ["Space Mono", 400], ["Space Mono", 700]];

// §11.0: on index.html and on the first page of every depth of both sites,
// document.fonts.load must return a non-empty list whose every face is
// "loaded" (not document.fonts.check, which is true when no face matches),
// with no failed request under assets/fonts/.
async function fonts(ctx) {
  const results = [];
  for (const [label, dir, url] of [["okf4net", ctx.siteDir, ctx.site], ["acme", ctx.acmeDir, ctx.acme]]) {
    for (const { rel, depth } of ctx.lib.pagesByDepth(dir)) {
      const page = await ctx.newPage();
      await page.goto(ctx.lib.pageUrl(url, rel), { waitUntil: "load" });
      const faces = await page.evaluate(async (wanted) => {
        const out = [];
        for (const [family, weight] of wanted) {
          try {
            const list = await document.fonts.load(`${weight} 16px "${family}"`);
            out.push({ family, weight, faces: list.length, loaded: list.length > 0 && list.every((f) => f.status === "loaded") });
          } catch (e) {
            out.push({ family, weight, faces: 0, loaded: false, error: String(e) });
          }
        }
        return out;
      }, FACES);
      const failed = page.okfTracked.failed.filter((u) => /\/assets\/fonts\//.test(u));
      results.push({ site: label, page: rel, depth, ok: failed.length === 0 && faces.every((f) => f.loaded), failed, faces: faces.filter((f) => !f.loaded) });
      await page.close();
    }
  }
  return { pass: results.every((r) => r.ok), pages: results.length, failures: results.filter((r) => !r.ok) };
}

async function run(ctx) {
  const out = {};
  if (ctx.wanted("fonts")) { out.fonts = await ctx.lib.guard(() => fonts(ctx)); }
  return out;
}

module.exports = { run };
