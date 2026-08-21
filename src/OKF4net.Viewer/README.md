# OKF4net.Viewer

Static HTML site generation for OKF knowledge bundles: one page per concept
(frontmatter + rendered body), a generated index, and navigable cross-links.

Zero third-party runtime dependencies — references only `OKF4net`.

Consumed by the `okf render` CLI verb. See the
[OKF4net repository](https://github.com/jchable/okf4net) for usage.

## Licensing

LGPL-3.0-or-later. The generated site embeds a vendored copy of
[marked](https://github.com/markedjs/marked) (MIT) for client-side markdown
rendering — see `NOTICE`.
