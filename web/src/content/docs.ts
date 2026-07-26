// SPDX-License-Identifier: LGPL-3.0-or-later

/**
 * One entry in the docs bundle's tree, in sidebar order.
 *
 * The docs section is itself an OKF bundle (`docs/`), so its sidebar is a
 * generated listing rather than 7 hand-copied `<aside class="docs-side">`
 * blocks (one per page, as in the pre-migration static site). Adding,
 * reordering, or removing a docs page means editing this one array —
 * `DocsSidebar` derives the ASCII tree, the `.current` marker, and the
 * `.soon` state from it.
 */
export interface DocsTreeEntry {
  /**
   * Route segment under `/docs`, e.g. `"getting-started"`. The landing page
   * uses `"index"` — `DocsSidebar` special-cases it to route `/docs` rather
   * than `/docs/index`. `soon` entries carry a slug for identification only;
   * they render no link.
   */
  slug: string
  /** The `*.md` file name shown in the tree and used as link text. */
  label: string
  /** True for concepts not yet published — renders as a non-link `.soon` row with a `Tag`. */
  soon?: boolean
}

/**
 * The docs bundle tree, matching `website/docs/index.html`'s
 * `<ul class="tree">` (commit `40fe17f`, lines ~37-47): index,
 * getting-started, guides, library, cli, agents (soon), mcp, spec.
 */
export const docsTree: DocsTreeEntry[] = [
  { slug: 'index', label: 'index.md' },
  { slug: 'getting-started', label: 'getting-started.md' },
  { slug: 'guides', label: 'guides.md' },
  { slug: 'library', label: 'library.md' },
  { slug: 'cli', label: 'cli.md' },
  { slug: 'agents', label: 'agents.md', soon: true },
  { slug: 'mcp', label: 'mcp.md' },
  { slug: 'spec', label: 'spec.md' },
]
