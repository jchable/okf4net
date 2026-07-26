# OKF4net — website design system

**"The §Document."** Every page of the OKF4net site behaves like an OKF concept
document. This file is the written reference; [`styleguide.html`](./styleguide.html)
is the living one (open it in a browser — it renders the real stylesheet).

> This directory is **not deployed**. `.github/workflows/pages.yml` builds and
> publishes only `web/` (its `dist/` output). Keep design-system files here so
> they never ship.

## Principles

1. **The metaphor is the identity.** Pages impersonate a concept document —
   editor-tab nav, framed document window, `##` chapter badges, `§` spec refs,
   raw⇄rendered duality. If a device doesn't map to something true about OKF,
   cut it.
2. **Tokens only.** Colour and type come from the `:root` variables in
   `site.css`. Never hard-code a hex or a font family in a page.
3. **Orthogonal, not soft.** Zero border-radius; `1.5px` ink borders for
   structure, `1px` hair for internal grids. Structure is drawn, not shaded.
4. **Restraint.** One brand hue, three type families, capped measures. The bold
   move is the metaphor itself — everything around it stays quiet.
5. **Claims are verified.** Any number on a page (test count, concept count,
   command behaviour) is checked against the actual build/test output before it
   ships. The current figure is **218/218 tests · 5 golden comparisons**.

## Tokens

Defined in [`../web/src/styles/site.css`](../web/src/styles/site.css) `:root`.

| Variable | Value | Role |
|---|---|---|
| `--white` | `#ffffff` | page background |
| `--ink` | `#101014` | text, borders, dark code panels |
| `--blue` | `#1a3fd6` | brand (Klein blue) — links, accents, primary actions |
| `--blue-soft` | `#eef1fd` | tints — frontmatter cells, inline code, hovers |
| `--gray` | `#6a6a72` | secondary text |
| `--hair` | `#e3e3e8` | hairline borders |
| `--ok` | `#0e8a4d` | success, YAML string values |

Inside `pre.block` (on ink), syntax tokens use a lifted palette for contrast:
`.k` `#7e9bff` keywords · `.s` `#ffd166` strings · `.c` `#7a7a85` comments ·
`.ok` `#6fd18a` success.

**Type** — three families, three jobs, never a fourth:

| Variable | Family | Weights | Job |
|---|---|---|---|
| `--display` | Inter Tight | 800 / 900 | `h1`, `h2`, `blockquote`, `.cta` — tight tracking |
| `--body` | Inter | 400 / 500 / 600 | prose, `.lede`, lists — 16px / 1.6 |
| `--mono` | Space Mono | 400 / 700 | nav, labels, paths, code, data |

**Layout** — one centred column: `.frame` at `max-width: 1200px`, gutters
`clamp(16px, 3.5vw, 48px)`. Measures capped at `64ch` (body) / `44ch`
(blockquote). Breakpoints: `960px` (hide nav, stack docs sidebar) and `720px`
(full-bleed).

## Component catalogue

Each lives in `site.css`; the styleguide renders every one with its source.

| Class | Intent |
|---|---|
| `.bar` / `.wordmark` / nav `a` | Editor-tab site navigation; current page via `aria-current`. |
| `.doc-window` / `.doc-chrome` | Framed page opening; chrome shows file path + `type:`. |
| hero `.pane` + `.toggle` | Home-only raw⇄rendered duality (needs `site.js`). |
| `.page-doc` | Slimmer subpage opening (path + type + `h1` + `.lede`, no toggle). |
| `.chead` | Chapter head: `##` badge + `h2` + `.ref` spec citation. |
| `.concept-grid` / `.cell` / `.term` | Defined terms in a bordered 2-up grid. |
| `pre.block` | Code on an ink panel with a blue rule; `.k/.s/.c/.ok` tokens. |
| `table.map` | Reference rows — mono key column + prose. |
| `table.index` | Generated `index.md` listing (§6); **docs section only**. |
| `.docs-shell` / `.docs-side` / `.tree` | Docs layout: bundle-tree sidebar + page. |
| `ol.steps` | A real numbered sequence (use only when order carries meaning). |
| `blockquote` | The thesis line — display weight, blue left rule. |
| `.warn` | A rule that must not be broken. |
| `.btn` / `.btn.primary` | Actions; primary is solid blue. |
| `.conform` | Dashed conformance badge with an inverted count chip. |
| `.tag` / `.muted` | Small status chip ("soon") / de-emphasised text. |
| `.next` | Cross-link teaser closing a chapter. |
| `.cta` | Closing call to action on a soft-blue panel. |
| `.colophon` | Footer: mono link row + attribution. |

## Build conventions

- **Vite + React 19, static-generated.** The site is a `web/` app built with
  `vite-react-ssg` and one shared stylesheet, `web/src/styles/site.css`,
  imported globally — the design system itself hasn't changed, only where it
  lives and how it ships. `pages.yml` runs `npm run build` in `web/` and
  deploys `web/dist`.
- **Only `web/` deploys.** `pages.yml` triggers on `web/**`. Anything outside
  — including this `design-system/` folder — is never served.
- **Adding a page:** create a `.tsx` page under `web/src/pages/` (or
  `web/src/pages/docs/` for a docs page), wrap it in the shared `Layout` (or
  `DocsLayout` for docs pages) so it picks up the nav bar, doc-window chrome,
  and colophon, and register its route. Docs pages get their sidebar for free
  from `web/src/content/docs.ts` — add an entry there rather than hand-copying
  a tree.
- **New shared styles go in `site.css`**, grouped and commented by section.
  Page-specific scaffolding that will never be reused (e.g. the styleguide's
  specimen chrome) stays inline in that page so `site.css` stays lean.
- **The docs section is a bundle.** Pages are presented as `<concept>.md`; the
  sidebar tree is generated from `web/src/content/docs.ts`; the landing is the
  generated `index.md`. Extend that fiction faithfully.

## Voice & content

- Write from the reader's side of the screen; name things by what they do.
- Cite the spec with real `§` sections; use monospace for every identifier
  (types, commands, paths, concept ids).
- Prefer specific over clever. State behaviour plainly ("`Bundle.Load` never
  aborts on a bad file"), not as a pitch.
- Sentence case; active voice; no filler. A label labels, an example
  demonstrates — nothing does double duty.
- Verify every factual claim before publishing (see principle 5).

## Files

- `styleguide.html` — living styleguide; renders `../web/src/styles/site.css`.
- `DESIGN.md` — this file.
