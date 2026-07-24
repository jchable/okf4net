# Website design system + developer docs — design spec

**Date:** 2026-07-24
**Branch:** `worktree-website-docs` (off `main`)
**Status:** approved (brainstorming) — pending implementation plan

## Goal

Two outcomes, so future website requests are fast and consistent:

1. **A documented design system** for the OKF4net site — extracted from the
   existing `website/assets/site.css` ("The §Document" theme), so new pages
   reuse the same tokens, components, and conventions instead of drifting.
2. **A proposed information architecture** for developer-oriented documentation
   (the project's target audience is developers), grounded in the real API
   surface of the three source projects. This cycle ships the *system* plus a
   *docs template stub*; the content pages themselves are a backlog for
   subsequent requests.

## Context (what already exists)

- `website/` (deployed to GitHub Pages by `.github/workflows/pages.yml`, which
  publishes **only** the `website/` folder) contains 5 hand-authored static
  pages + `404.html` sharing `website/assets/site.css` and `site.js`. No build
  step; pure HTML/CSS/JS.
- The CSS already embodies a coherent system — "The §Document" theme, where
  every page behaves like an OKF concept document: editor-tab nav, `doc-window`
  with chrome, `raw`/`rendered` toggle, `##` chapter badges with `§` spec refs,
  frontmatter tables. Klein blue `#1a3fd6`; type trio Inter Tight (display) /
  Inter (body) / Space Mono (mono).
- The current pages read as **overview/marketing**. They cover the library and
  CLI but **never mention `OKF4net.Agents`** — the Microsoft Agent Framework
  layer — which is a real developer-facing gap.

### Real API surface (anchors for the docs proposal)

- **Library (`src/OKF4net/`)** — `Bundle` (`Load`, `Backlinks`, `ParseErrors`),
  `ConceptId`, `OkfDocument` (`Validate` / `ValidateConformance`), `Frontmatter`
  (ordered mapping + typed getters), YAML subset (`YamlValue` and subtypes,
  `YamlMapping`, `YamlEmitter`), `LinkScanner` / `ResolvedLink` / `ConceptLink`,
  `IndexGenerator` / `IndexEntry`, `ChangeLog` / `LogDay` / `LogEntry`,
  `BundleValidator` / `ValidationReport` / `Diagnostic` / `Severity`, `OkfSpec`.
- **CLI (`src/OKF4net.Cli/`)** — 6 commands: `validate`, `info`, `index`,
  `graph` (`--dot`), `parse`, `fmt` (`-w`). Non-zero exit on non-conformance.
- **Agents (`src/OKF4net.Agents/`)** — `OkfBundleTools` exposes **9 function
  tools**: `ReadConcept`, `Browse`, `Graph`, `Search`, `WriteConcept`,
  `AppendLog`, `RegenerateIndexes`, `ValidateBundle`, `ChangesSince`; plus
  `OkfContextProvider` (`AIContextProvider`) and `OkfContextProviderOptions`
  (budget-bounded context injection, opt-in per-day memory concepts). A
  `README.md` already lives in this project and is the content source.

## Deliverable 1 — the design system (NOT published)

All design-system files live in a dedicated top-level `design-system/`
directory. `pages.yml` deploys only `website/`, so nothing here is served.

### `design-system/styleguide.html` — living styleguide

A single page, itself authored as a §Document so it dogfoods the system and
cannot misrepresent the rendering. It links the **real** stylesheet at
`../website/assets/site.css` (single source of truth — no CSS copy). Opened from
disk (`file://`) during design work; never deployed.

Sections:

- **Foundations** — colour tokens as swatches (hex + CSS variable name + role);
  type scale (the three families and their display/body/mono roles with real
  sizes); spacing/rhythm; the layout grid (`.frame`, 1200px max, `clamp`
  gutters).
- **The metaphor** — "every page is a concept document": the rules for the
  editor-tab nav, `.doc-window` + `.doc-chrome`, `##` chapter badges + `§` refs,
  the `raw`/`rendered` toggle, frontmatter tables.
- **Component gallery** — live-rendered example + class name + copy-paste
  snippet for each: `.doc-window`, `.chead`, `.concept-grid`, `pre.block` (token
  classes `k`/`s`/`c`/`ok`), `table.map`, `ol.steps`, `.warn`, `blockquote`,
  `.btn` / `.btn.primary`, `.conform`, `.cta`, `.next`, `.colophon`.
- **Patterns & guardrails** — page skeletons (home hero vs subpage `.page-doc`);
  responsive breakpoints (960px collapses nav; 720px full-bleed); accessibility
  (`:focus-visible`, `prefers-reduced-motion`, colour contrast).

### `design-system/DESIGN.md` — concise written spec

The durable reference future requests consult:

- **Principles** and do/don't list (spec-citation discipline, monospace for
  identifiers, factual claims verified against the test suite before publishing).
- **Token table** — every CSS variable, its value, and its intended use.
- **Component catalogue** — each component's name + *intent* + when to use it.
- **Build conventions** — no build step; hand-authored static HTML; shared
  `site.css`; how to add a page (copy the nearest template, wire the nav, keep
  the §Document chrome); the "only `website/` is deployed" rule.
- **Voice & content rules** — tone, how to cite `§` sections, how to present
  code, how numbers (test counts, concept counts) must be verified.

## Deliverable 2 — developer docs IA (proposal + template stub)

The 5 existing pages stay as the **overview/front**. A new `website/docs/`
section (deployed, public) holds reference-grade developer docs with a
left sidebar, "on this page" anchors, and a lighter §Document chrome tuned for
reference density.

Proposed pages (this cycle scaffolds only `docs/index.html`; the rest are
backlog):

| Page | Role | Anchor source |
|---|---|---|
| `docs/index.html` | Docs map, entry points by persona | — (scaffolded this cycle) |
| `docs/getting-started.html` | Install library (NuGet) + CLI (AOT / dotnet tool); first bundle; first `validate` | csproj, CLI |
| `docs/guides.html` | Task recipes: load/traverse, validate-in-CI, indexes (§6)/changelog (§7), round-trip `fmt`, build AOT | library + CLI |
| `docs/library.html` | API reference across the public types above | enumerated types |
| `docs/cli.html` | 6-command reference: flags, exit codes, transcripts | CLI + golden fixtures |
| `docs/agents.html` | **Fills the gap:** Agent Framework — 9-tool table, `OkfContextProvider` + options, wiring example | `OKF4net.Agents/README.md` |
| `docs/spec.html` | §2–§9 → type mapping, conformance rules, documented divergences | README |

The main nav gains a `docs.md` entry (added to the 5 existing pages and to the
docs pages). The docs template stub establishes the shared docs skeleton
(sidebar + content region + chrome) so every future docs page is a copy-edit.

## This implementation cycle delivers

1. `design-system/styleguide.html` — living styleguide (internal, not deployed).
2. `design-system/DESIGN.md` — concise design-system spec.
3. `website/docs/index.html` — docs landing + shared docs template/skeleton.
4. Nav update: `docs.md` entry added across existing pages.
5. This design doc, committed.

Explicitly **out of scope this cycle** (backlog for later requests): the content
of `getting-started`, `guides`, `library`, `cli`, `agents`, `spec` docs pages.

## Integration & deployment

- Work happens on `worktree-website-docs` (off `main`).
- The `design-system/` directory does not match `pages.yml`'s `website/**`
  trigger, so pushing it neither deploys nor changes the live site.
- Adding `website/docs/index.html` + nav edits **will** trigger a Pages deploy
  when merged to `main`. The docs landing must be presentable on its own (a map
  with "coming soon" markers is acceptable) so the first deploy isn't broken.
- Integration path mirrors the prior website track: promote to `main` via `dev`
  (or a PR), consistent with existing branch conventions.

## Testing / verification

Static site, no unit tests. Verification is:

- Every internal link resolves (nav, sidebar, cross-links, footer).
- Pages render correctly at desktop, 960px, and 720px widths.
- The styleguide's live component examples match what the real pages render
  (they share `site.css`, so this holds by construction).
- Any factual claim on a page (test counts, concept counts, command behaviour)
  is verified against the actual build/test output before publishing.
- `styleguide.html` and `design-system/` are absent from any Pages deploy
  (grep the deploy artifact / confirm `pages.yml` path filter).

## Open questions

None blocking. Content depth per docs page will be decided per-page when each is
written.
