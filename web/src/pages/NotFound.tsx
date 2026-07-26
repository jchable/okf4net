// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import Layout from '../layouts/Layout'

// Whitespace-significant code sample mixing an inline syntax-highlighting
// span with literal text — kept as a verbatim HTML string per the technique
// established in Home.tsx. Sourced verbatim from website/404.html:47-50.
const sessionHtml = `$ okf validate ./this-page
<span class="s">warning: broken link — target does not exist</span>
$ echo $?
1`

/**
 * Port of `website/404.html`. Registered both as the router catch-all
 * (`path: '*'`, for unknown-route client navigation) and as an explicit
 * `/404` route so the SSG build emits `dist/404/index.html` for GitHub
 * Pages to serve on any unmatched path.
 *
 * `current` is left unset (`Layout`/`SiteBar` treat that as "no active
 * tab" — correct here, since a 404 isn't any of the site's chapters) and
 * `footerVariant="minimal"` matches the shorter `website/404.html` footer.
 *
 * The `.page-doc` chrome bar here reads `exists: false` rather than the
 * usual `type: <Type>`, so this hand-rolls the `doc-window` markup instead
 * of using the `PageDoc` device component (whose chrome bar is hardcoded to
 * a `type: ` label). Likewise the body `<section class="chapter">` in the
 * source has no `.chead` (no `##`/`<h2>`/`.ref`), so it's a plain
 * `<section>` rather than the `Chapter` component.
 */
export default function NotFound() {
  return (
    <Layout
      title="Concept not found — OKF4net"
      description="Concept not found — OKF4net"
      footerVariant="minimal"
      noindex
    >
      <div className="page-doc">
        <div className="doc-window">
          <div className="doc-chrome">
            <span className="path">
              my_bundle/<b>404.md</b>
            </span>
            <span>exists: false</span>
          </div>
          <div className="rendered">
            <h1>
              Concept <em>not found.</em>
            </h1>
            <p className="lede">
              This link is retained in the graph as an edge to a non-existent concept, as §5 requires — but there is
              nothing to render here.
            </p>
          </div>
        </div>
      </div>

      <div className="docbody">
        <section className="chapter">
          <pre className="block" dangerouslySetInnerHTML={{ __html: sessionHtml }} />
          <p className="next">
            → <Link to="/">okf4net.md</Link> — back to the bundle root
          </p>
        </section>
      </div>
    </Layout>
  )
}
