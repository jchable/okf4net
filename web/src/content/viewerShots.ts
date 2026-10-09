// SPDX-License-Identifier: LGPL-3.0-or-later

/** Everything `<Shot>` needs about one screenshot of the `okf-render` viewer. */
export interface ViewerShot {
  /** File name under `web/public/viewer/`. */
  file: string
  /** Alternative text. */
  alt: string
  /** The PNG's own width in pixels. */
  width: number
  /** The PNG's own height in pixels. */
  height: number
  /** Widest the figure is drawn, in CSS pixels. */
  maxWidth: number
}

/**
 * The screenshots of `docs/viewer`. They are produced by one command —
 * `tools/viewer-security-check/recette/docs-shots.js`, see its README — from
 * `bundles/acme_retail`, so re-running it after a viewer change replaces the
 * files in `public/viewer/`. `viewerShots.test.tsx` reads each PNG's header
 * and fails when a file is missing or its size here is stale, which is the
 * reminder to update the numbers below after a re-capture.
 *
 * The numbered callouts drawn in `concept` and `graph` are listed by number in
 * the prose of `Viewer.tsx`; changing a selector in `docs-shots.js` means
 * rereading those lists.
 */
export const viewerShots = {
  concept: {
    file: 'viewer-concept.png',
    alt: 'A concept page of the acme_retail bundle in okf-render, with seven numbered callouts: the header, the explorer on the left, the chips under the title, the frontmatter box, the Neighbourhood local graph at two hops, the Referenced by list and, between the centre column and the right-hand panel, the splitter.',
    width: 1440,
    height: 900,
    maxWidth: 1440,
  },
  splitter: {
    file: 'viewer-splitter.png',
    alt: 'A concept page whose right-hand column has been dragged wider than its default: the splitter between the centre column and the panel shows its blue focus ring, the frontmatter box is narrower and the Neighbourhood graph sits in the wider panel.',
    width: 1440,
    height: 900,
    maxWidth: 1440,
  },
  modal: {
    file: 'viewer-modal.png',
    alt: 'The enlarged Neighbourhood dialog over a concept page, with five numbered callouts: the 1 hop and 2 hops toggle, the large drawing at two hops, the list of all seven neighbours beside it, the Open in graph link and the Close button.',
    width: 1200,
    height: 720,
    maxWidth: 1200,
  },
  modalNarrow: {
    file: 'viewer-modal-narrow.png',
    alt: 'The same dialog on a phone, 390 px wide: the title, the hop toggle and Close on top, the drawing in the middle and the list of neighbours under it, with Open in graph at the bottom.',
    width: 780,
    height: 1688,
    maxWidth: 390,
  },
  explorer: {
    file: 'viewer-explorer.png',
    alt: 'The explorer narrowed to the Metric type and the name filter "margin": two concepts remain under their folder, each with a blue trust dot, above the legend of trust and staleness marks.',
    width: 578,
    height: 690,
    maxWidth: 289,
  },
  palette: {
    file: 'viewer-palette.png',
    alt: 'The "Jump to" palette open over a concept page with the query "margin": four matching concepts, each with its type shape, title and id, the first one highlighted.',
    width: 1276,
    height: 720,
    maxWidth: 638,
  },
  contents: {
    file: 'viewer-contents.png',
    alt: 'The right-hand panel of a concept page: the "On this page" list of six headings with the current section marked, above the Neighbourhood graph at one hop and its "Open in graph" link.',
    width: 668,
    height: 1200,
    maxWidth: 334,
  },
  graph: {
    file: 'viewer-graph.png',
    alt: 'The global graph page with five numbered callouts: the facets column, the drawing with the selected concept outlined and its links in blue, the zoom, Fit and List buttons, the detail drawer of the selected concept with its Open page button and, between the drawing and the drawer, the splitter.',
    width: 1440,
    height: 900,
    maxWidth: 1440,
  },
  graphFacets: {
    file: 'viewer-graph-facets.png',
    alt: 'The facets column of the global graph: type, trust and freshness checkboxes with counts, a tag cloud with "finance" chosen, and the display options with "Dim unmatched instead of hiding" ticked.',
    width: 538,
    height: 1660,
    maxWidth: 269,
  },
  narrow: {
    file: 'viewer-narrow.png',
    alt: 'A concept page at the width of a phone: the header wraps onto three rows, then the breadcrumb, the title, the chips and the frontmatter box in one column.',
    width: 780,
    height: 1688,
    maxWidth: 390,
  },
  dark: {
    file: 'viewer-dark.png',
    alt: 'A concept page in the dark theme: the same three columns on a dark background, with the explorer, the frontmatter box and the Neighbourhood graph.',
    width: 1440,
    height: 900,
    maxWidth: 1440,
  },
} satisfies Record<string, ViewerShot>
