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

## Licensing

LGPL-3.0-or-later. The generated site embeds a vendored copy of
[marked](https://github.com/markedjs/marked) (MIT) for client-side markdown
rendering, and three font families — Inter, Inter Tight and Space Mono (SIL
Open Font License 1.1), latin woff2 as Google Fonts distributes them — see
`NOTICE` and `Assets/fonts/README.md`.
