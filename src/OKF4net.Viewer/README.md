# OKF4net.Viewer

Static HTML site generation for OKF knowledge bundles: one page per concept
(breadcrumb, title, type/status/trust/staleness chips, a folding frontmatter
box, the rendered body), a generated index, navigable cross-links, a tree
explorer with type shapes and filters, a "Jump to" palette, a contents list
that follows the reading position, "Referenced by" lists, light and dark
themes, and the Inter, Inter Tight and Space Mono fonts embedded — all of it
working from `file://`, with no server.

Zero third-party runtime dependencies — references only `OKF4net`.

Consumed by the standalone `okf-render` binary (`OKF4net.Render`), not by
`okf` itself. See the [OKF4net repository](https://github.com/jchable/okf4net)
for usage.

## Neighbourhood

A concept page that has neighbours shows a "Neighbourhood" section in its
side panel: the concept and its neighbours (links out and in) at one hop, or
two with the "2 hops" button, drawn in rings by `okf-local.js`. At most 40
nodes are drawn, the concept included; the rest is announced as "+N omitted"
and listed. Links to concepts the bundle does not contain are drawn as ghosts
and are never navigable. Every neighbour, drawn or not, is also in the "List"
below the drawing, with its relation to the concept (links to, referenced by,
or the first-hop neighbour a second-hop concept is reached through): that
list, not the drawing, is the keyboard path. "Open in graph" opens the global
graph on this concept. A concept with no neighbour has no section.

## Global graph

Every site has a global graph page, `graph.html` at its root (`graph-1.html`,
`graph-2.html`… if a concept already uses the name), linked as "Global graph"
from every page header. It draws the bundle's body links (the edges of
`okf graph`) with a force layout computed in the browser by `okf-sim.js`: a
pure, deterministic module (same bundle, same initial layout on every
conforming engine) whose work is bounded and spread over animation frames.
Facets (type, trust, freshness, tags, display) narrow what is drawn; above
1,500 visible nodes (concepts and absent targets) the graph is not drawn: the
list is shown and the status line asks for narrower filters. The wheel and
the zoom buttons zoom, the background pans, a node can be dragged and "Fit"
frames everything drawn.

Every view of the graph has a text equivalent: the "List" button, the detail
drawer of the selected concept and the keyboard. The drawing is a single tab
stop; Page Up and Page Down move through the nodes in index order, Home and
End to the first and the last, an arrow to the nearest neighbour in its
direction (pressed again, to the next neighbour in that direction), Space
selects and Enter opens the concept's page. The selected node carries
`aria-current`. Selecting a concept writes `graph.html#<concept id>` without
adding a history entry, and opening that address selects the concept, so a
concept page's "Global graph" link lands on that concept.

## Licensing

LGPL-3.0-or-later. The generated site embeds a vendored copy of
[marked](https://github.com/markedjs/marked) (MIT) for client-side markdown
rendering, and three font families — Inter, Inter Tight and Space Mono (SIL
Open Font License 1.1), latin woff2 as Google Fonts distributes them — see
`NOTICE` and `Assets/fonts/README.md`. marked's MIT licence text ships
verbatim as `Assets/licenses/marked-LICENSE.md` and, like the fonts' OFL
texts, is written into every generated site (`assets/licenses/`) and travels
beside the `okf-render` binary in its release archives.
