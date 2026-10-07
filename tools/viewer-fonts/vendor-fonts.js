// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Vendors the viewer's three font families (spec §11.0, A16): Inter (400,
// 500, 600), Inter Tight (600, 900) and Space Mono (400, 700), latin subset,
// woff2, exactly as Google Fonts distributes them -- never re-subset nor
// modified, so no SIL OFL "Modified Version" or Reserved Font Name question
// arises -- with their SIL OFL 1.1 texts. Writes
// src/OKF4net.Viewer/Assets/fonts/ (the fonts, the licences and README.md,
// the provenance table) and the @font-face block of viewer.css, between its
// two markers. Run by hand (Node 22, network) when the fonts are vendored or
// re-vendored; no build and no test runs it:
//   node tools/viewer-fonts/vendor-fonts.js
"use strict";
const crypto = require("crypto");
const fs = require("fs");
const path = require("path");

const ROOT = path.join(__dirname, "..", "..");
const FONTS = path.join(ROOT, "src", "OKF4net.Viewer", "Assets", "fonts");
const CSS = path.join(ROOT, "src", "OKF4net.Viewer", "Assets", "viewer.css");
const BEGIN = "/* === @font-face (P1.1 Task 10 replaces the lines between these two markers) === */";
const END = "/* === end @font-face === */";
// A desktop Chrome user agent: Google Fonts answers it with woff2.
const UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";
const CSS_URL = "https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600&family=Inter+Tight:wght@600;900&family=Space+Mono:wght@400;700&display=swap";
// The faces, in the order viewer.css declares them.
const FACES = [["Inter", 400], ["Inter", 500], ["Inter", 600], ["Inter Tight", 600], ["Inter Tight", 900], ["Space Mono", 400], ["Space Mono", 700]];
const LICENCES = [
  ["Inter", "OFL-Inter.txt", "https://raw.githubusercontent.com/google/fonts/main/ofl/inter/OFL.txt"],
  ["Inter Tight", "OFL-InterTight.txt", "https://raw.githubusercontent.com/google/fonts/main/ofl/intertight/OFL.txt"],
  ["Space Mono", "OFL-SpaceMono.txt", "https://raw.githubusercontent.com/google/fonts/main/ofl/spacemono/OFL.txt"],
];
const BUDGET = 300000;

async function get(url, binary) {
  const response = await fetch(url, { headers: { "User-Agent": UA } });
  if (!response.ok) { throw new Error(`${url}: HTTP ${response.status}`); }
  return binary ? Buffer.from(await response.arrayBuffer()) : response.text();
}

const slug = (family) => family.toLowerCase().replace(/ /g, "-");
const sha256 = (buffer) => crypto.createHash("sha256").update(buffer).digest("hex");

(async () => {
  const css = await get(CSS_URL, false);
  const latin = [];
  for (const m of css.matchAll(/\/\*\s*([a-z-]+)\s*\*\/\s*@font-face\s*\{([^}]*)\}/g)) {
    if (m[1] !== "latin") { continue; }
    const rule = m[2];
    latin.push({
      family: /font-family:\s*'([^']+)'/.exec(rule)[1],
      weight: Number(/font-weight:\s*(\d+)/.exec(rule)[1]),
      url: /src:\s*url\((https:[^)]+\.woff2)\)\s*format\('woff2'\)/.exec(rule)[1],
      range: /unicode-range:\s*([^;]+);/.exec(rule)[1].trim(),
    });
  }
  const faces = FACES.map(([family, weight]) => {
    const face = latin.find((f) => f.family === family && f.weight === weight);
    if (!face) { throw new Error(`Google Fonts served no latin woff2 face for ${family} ${weight}`); }
    return face;
  });

  fs.mkdirSync(FONTS, { recursive: true });
  const files = new Map(); // url -> { name, bytes, sha, version, family, weights }
  for (const face of faces) {
    if (!files.has(face.url)) {
      const urls = new Set(faces.filter((f) => f.family === face.family).map((f) => f.url));
      const name = `${slug(face.family)}${urls.size > 1 ? "-" + face.weight : ""}-latin.woff2`;
      const bytes = await get(face.url, true);
      if (bytes.subarray(0, 4).toString("latin1") !== "wOF2") { throw new Error(`${face.url} is not a woff2 file`); }
      const version = (/\/s\/[^/]+\/(v\d+)\//.exec(face.url) || [null, "unknown"])[1];
      files.set(face.url, { name, bytes, sha: sha256(bytes), version, family: face.family, weights: [] });
      fs.writeFileSync(path.join(FONTS, name), bytes);
    }
    files.get(face.url).weights.push(face.weight);
  }

  const licences = [];
  for (const [family, name, url] of LICENCES) {
    const text = await get(url, false);
    if (!/SIL OPEN FONT LICENSE Version 1\.1/i.test(text)) { throw new Error(`${url} is not the SIL OFL 1.1`); }
    fs.writeFileSync(path.join(FONTS, name), text);
    licences.push({ family, name, url, sha: sha256(Buffer.from(text)), size: Buffer.byteLength(text) });
  }

  const block = [
    BEGIN,
    "/* Inter, Inter Tight and Space Mono (SIL OFL 1.1), latin subset, as Google Fonts serves",
    "   them (fonts/README.md). Relative to this stylesheet, so valid at every page depth. */",
  ];
  for (const face of faces) {
    block.push(`@font-face { font-family: "${face.family}"; font-style: normal; font-weight: ${face.weight}; font-display: swap; src: url("fonts/${files.get(face.url).name}") format("woff2"); unicode-range: ${face.range}; }`);
  }
  block.push(END);
  const source = fs.readFileSync(CSS, "utf8");
  const start = source.indexOf(BEGIN);
  const end = source.indexOf(END);
  if (start === -1 || end < start) { throw new Error("viewer.css lost its @font-face markers"); }
  // Keep the stylesheet's own line endings (a Windows checkout is CRLF).
  const eol = source.includes("\r\n") ? "\r\n" : "\n";
  fs.writeFileSync(CSS, source.slice(0, start) + block.join(eol) + source.slice(end + END.length));

  const date = new Date().toISOString().slice(0, 10);
  const total = [...files.values()].reduce((n, f) => n + f.bytes.length, 0);
  const lines = [
    "# Viewer fonts: provenance",
    "",
    `Vendored on ${date} by \`tools/viewer-fonts/vendor-fonts.js\` from Google Fonts`,
    `(\`${CSS_URL}\`), latin subset, woff2, **unmodified** (spec §11.0, A16).`,
    "Licence: SIL Open Font License 1.1, texts beside the fonts. This README is",
    "not embedded (`OKF4net.Viewer.csproj` excludes it); every other file here is",
    "embedded and written to `assets/fonts/` by `okf-render`.",
    "",
    "| File | Family | Weights | Source | Version | Bytes | sha256 |",
    "| --- | --- | --- | --- | --- | --- | --- |",
    ...[...files.entries()].map(([url, f]) => `| \`${f.name}\` | ${f.family} | ${f.weights.join(", ")} | ${url} | ${f.version} | ${f.bytes.length} | \`${f.sha}\` |`),
    "",
    "| Licence | Family | Source | Bytes | sha256 |",
    "| --- | --- | --- | --- | --- |",
    ...licences.map((l) => `| \`${l.name}\` | ${l.family} | ${l.url} | ${l.size} | \`${l.sha}\` |`),
    "",
    `Total woff2: ${total} bytes (budget ${BUDGET}, spec §11.0).`,
    "",
  ];
  fs.writeFileSync(path.join(FONTS, "README.md"), lines.join("\n"));
  console.log(`${files.size} woff2 files, ${total} bytes (budget ${BUDGET}); ${licences.length} licences; viewer.css @font-face rewritten.`);
  if (total > BUDGET) { console.log("OVER BUDGET: stop and report to the owner (spec §11.0)."); process.exitCode = 1; }
})().catch((e) => {
  console.error(e && e.message ? e.message : e);
  process.exit(1);
});
