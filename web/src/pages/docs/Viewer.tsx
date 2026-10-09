// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import DocsLayout from '../../layouts/DocsLayout'
import { PageDoc, Chapter, MapTable, Shot, Warn, Next } from '../../components/doc'
import { viewerShots } from '../../content/viewerShots'

const renderHtml = `$ okf-render bundles/acme_retail --out ./acme-site
<span class="c"># then open ./acme-site/index.html in any browser — double-click it, no server needed</span>`

/**
 * `docs/viewer.md` — a reader's guide to the pages `okf-render` writes: what
 * is on them and what the marks mean. Facts here are taken from
 * `src/OKF4net.Viewer/README.md`, the CHANGELOG entry for the interactive
 * pages, §5.2–§5.5 of the spec and the viewer's own scripts; the screenshots
 * come from `tools/viewer-security-check/recette/docs-shots.js` (see
 * `content/viewerShots.ts`).
 *
 * Wording rule: nothing here names a colour or a shape size, and the
 * numbered lists next to the annotated screenshots must match the
 * callouts `docs-shots.js` draws (the concept page, the global graph and the
 * enlarged Neighbourhood).
 */
export default function Viewer() {
  return (
    <DocsLayout
      title="Viewer — OKF4net docs"
      description="Browse an OKF bundle as a static website with okf-render: an explorer, a Jump to palette, concept pages with trust and staleness badges and a local graph, and a global link graph. Opens from the disk, no server."
      current="viewer"
    >
      <PageDoc
        path={
          <>
            docs/<b>viewer.md</b>
          </>
        }
        type="Guide"
        title={
          <>
            Browse a bundle as a <em>website.</em>
          </>
        }
        lede={
          <>
            <code>okf-render</code> turns a bundle into a <strong>static site</strong> you open straight from the
            disk: a tree explorer, a <em>Jump to</em> palette, one page per concept with its trust and staleness at a
            glance, and a graph of how the concepts link. No server, no network, read-only.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="what" title="What you get" refText="okf-render <bundle> --out <dir>">
          <p>
            Point <code>okf-render</code> at a bundle and name, with <code>--out</code>, the directory to write the site into:
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: renderHtml }} />
          <p>
            The output is a folder of plain files: an <code>index.html</code> at the root, one <code>.html</code> page
            per concept at the concept's own path (<code>computations/gross-margin-period.html</code>), a{' '}
            <code>graph.html</code> for the whole bundle, and an <code>assets/</code> folder holding the scripts, the
            stylesheet and the fonts. Nothing is fetched from the network when a page opens, so the folder works from{' '}
            <code>file://</code>, from a USB stick, from a web server or from a documentation host alike. The pages
            below are all of <code>bundles/acme_retail</code>, the sample bundle in the repository.
          </p>
          <p>
            <code>okf-render</code> is its own binary, separate from <code>okf</code>: the validator that runs in CI
            stays small, and only the site generator carries the vendored{' '}
            <a href="https://github.com/markedjs/marked">marked</a> (MIT) that renders the markdown in the browser
            and the three embedded font families (SIL OFL 1.1). Both licences ship beside the binary and inside every
            generated site, under <code>assets/licenses/</code>.
          </p>
        </Chapter>

        <Chapter id="install" title="Install it" refText="a release archive per OS, or build from source">
          <p>
            Each <a href="https://github.com/jchable/okf4net/releases">GitHub Release</a> carries an{' '}
            <code>okf-render</code> archive for Windows, Linux and macOS, on x64 and arm64. On Linux and macOS the
            install script fetches the right one and verifies its SHA-256: pass <code>--bin okf-render</code> to
            install the site generator instead of <code>okf</code> (the script is described in{' '}
            <Link to="/docs/cli#install">docs/cli.md</Link>). On any OS you can also build it from a checkout with{' '}
            <code>dotnet publish src/OKF4net.Render -c Release</code>, which needs the .NET SDK 10.0 or later and
            produces a self-contained Native AOT binary.
          </p>
          <p>
            <code>okf-render --help</code> prints the usage, <code>--version</code> the build. The site is a snapshot:
            render again after the bundle changes.
          </p>
        </Chapter>

        <Chapter id="anatomy" title="A concept page, region by region" refText="one header, three columns">
          <Shot {...viewerShots.concept}>
            A concept page of <code>acme_retail</code>, with the Neighbourhood graph set to two hops. The numbers are
            listed below.
          </Shot>
          <ol className="plain">
            <li>
              <strong>The header</strong>, the same on every page: the bundle's name with its concept and link counts,
              the <em>Jump to a concept…</em> palette button, <em>Filters</em>, <em>Global graph</em> and the theme
              toggle. A <em>Skip to content</em> link comes before it for keyboard users.
            </li>
            <li>
              <strong>The explorer</strong>: the bundle as a tree, with type chips, a name filter and the legend of
              trust and staleness marks. <a href="#explorer">More below.</a>
            </li>
            <li>
              <strong>The chips</strong> under the title: the concept's type, its <code>status</code>, its trust tier
              with who verified it and when, and its staleness.{' '}
              <a href="#trust">What they mean.</a>
            </li>
            <li>
              <strong>The frontmatter box</strong>, folded to four entries with a <em>Show all</em> button. The keys
              the title and the chips already show are not repeated in the folded view.
            </li>
            <li>
              <strong>Neighbourhood</strong>: the concept and the concepts it links to or that link to it, drawn as a
              small graph that a button enlarges. <a href="#concept">More below.</a>
            </li>
            <li>
              <strong>Referenced by</strong>: the concepts whose body links to this one, with a count.
            </li>
            <li>
              <strong>The splitter</strong> between the centre column and the right-hand column: drag it to give that
              column more room. <a href="#splitter">More below.</a>
            </li>
          </ol>
          <p>
            From 1,100 px of window width the three columns sit side by side; below that they stack, the page first,
            then its context, then the explorer, so nobody scrolls through a tree to reach the text.
          </p>
        </Chapter>

        <Chapter id="splitter" title="Resizing the side column" refText="drag it or use the keys — from 1,100 px">
          <p>
            The right-hand column — the context panel of a concept page, the detail drawer of the global graph — is
            340 px wide by default. A thin bar on its left edge, the <strong>splitter</strong>, resizes it, so a graph
            can have more room. Drag it with a mouse, a finger or a pen: the column follows the pointer, and{' '}
            <kbd>Esc</kbd> during a drag puts the width back. The explorer and the facets column are not resizable.
          </p>
          <Shot {...viewerShots.splitter}>
            The panel after a drag to the left and one press of <kbd>←</kbd>: the splitter shows its focus ring.
          </Shot>
          <ul className="plain">
            <li>
              <strong>Keyboard.</strong> The splitter is a tab stop (a <code>separator</code> named{' '}
              <em>Resize the side panel</em>, with its width in pixels as its value). With it focused, <kbd>←</kbd>{' '}
              widens the column and <kbd>→</kbd> narrows it by 16 px, 64 px with <kbd>Shift</kbd>; <kbd>Home</kbd> and{' '}
              <kbd>End</kbd> go to the narrowest and the widest; <kbd>Enter</kbd> or a double click restores the
              default width. Combinations with <kbd>Alt</kbd>, <kbd>Ctrl</kbd> or <kbd>⌘</kbd> are left to the
              browser.
            </li>
            <li>
              <strong>Bounds.</strong> At least 240 px; at most the smallest of 60% of the window, 720 px, and what
              leaves the centre column 360 px.
            </li>
            <li>
              <strong>Remembered.</strong> The width is kept in the browser's local storage, under one key shared by the
              concept pages and the graph page; where the browser refuses storage, or does not share it between{' '}
              <code>file://</code> pages, the splitter still works but forgets.
            </li>
            <li>
              <strong>Narrow windows.</strong> Below 1,100 px the columns are stacked, the splitter is hidden and a
              stored width is ignored.
            </li>
            <li>
              <strong>The graph follows.</strong> When the drawing's area changes size, the global graph fits itself to
              it again, unless you have already moved the view yourself (then <em>Fit</em> does it).
            </li>
          </ul>
        </Chapter>

        <Chapter id="explorer" title="The explorer" refText="tree, type chips, filter, marks">
          <p>
            The left column lists the bundle the way its directories do. A folder opens and closes on its own; a
            concept that is also a folder opens and expands separately. Rows are labelled with the last segment of the id, and the title is the tooltip.
          </p>
          <Shot {...viewerShots.explorer}>
            The <em>Metric</em> chip pressed and <code>margin</code> typed in the filter.
          </Shot>
          <ul className="plain">
            <li>
              <strong>Type chips</strong> carry the type's name and its number of concepts. Pressing several chips
              shows any of those types; the name filter then narrows within them. <em>Filters</em> in the header
              moves the focus to the filter field and shows how many filters are active.
            </li>
            <li>
              <strong>Shapes and colours.</strong> Each type is drawn as a shape and a colour fixed by its{' '}
              <em>rank</em>: types are ranked by how many concepts carry them, most frequent first. The first five
              types get a shape of their own, every further type shares a sixth one. The same shape stands for the
              type in the explorer, the palette, the chips, the lists and both graphs.
            </li>
            <li>
              <strong>Trust and staleness marks</strong> sit at the right of each row: a filled dot for a{' '}
              <em>human-reviewed</em> concept, a ring for a <em>machine-confirmed</em> one, an hourglass for a concept
              past its <code>stale_after</code>. A concept with no <code>verified</code> key has no trust mark.
            </li>
          </ul>
        </Chapter>

        <Chapter id="palette" title="Jump to a concept" refText="Ctrl+K or / — not full-text search">
          <p>
            Press <kbd>Ctrl</kbd>+<kbd>K</kbd> or <kbd>/</kbd> on any page (or press the <em>Jump to a concept…</em>{' '}
            button) and type. The palette matches what you type against concept <strong>titles, ids and tags</strong>,
            lists the best matches with their type shape, title and id, and opens the one you choose.{' '}
            <kbd>↑</kbd>/<kbd>↓</kbd> move, <kbd>Enter</kbd> opens, <kbd>Esc</kbd> closes and returns focus to where
            it was.
          </p>
          <Shot {...viewerShots.palette}>
            The palette over a concept page, after <kbd>Ctrl</kbd>+<kbd>K</kbd> and the query <code>margin</code>.
          </Shot>
          <p>
            The ranking is a fixed order of tiers over those three fields, not a weighted score, and it never reads
            the body of a concept: see <a href="#limits">What it does not do</a>.
          </p>
        </Chapter>

        <Chapter id="concept" title="Reading a concept" refText="body, contents, references, neighbourhood">
          <p>
            The centre column is the concept itself: a breadcrumb, the title (a leading <code>#</code> heading that
            only repeats it is dropped), the chips, the frontmatter box and the markdown body, rendered in the browser.
            Links between concepts work as links, and a link to a concept the bundle does not contain is flagged as
            broken. Headings get generated anchors, so an author's own <code>#usage</code> link lands.
          </p>
          <Shot {...viewerShots.contents}>
            The right-hand panel of a concept with headings: its contents, then the Neighbourhood at one hop.
          </Shot>
          <ul className="plain">
            <li>
              <strong>On this page</strong> lists the concept's <code>##</code> and <code>###</code> headings and marks
              the section you are reading as you scroll. A concept with no such heading has no list.
            </li>
            <li>
              <strong>Referenced by</strong> lists the concepts that link to this one, with their number.
            </li>
            <li>
              <strong>Neighbourhood</strong> draws the concept and its neighbours, links out and links in, at{' '}
              <em>1 hop</em> or, with the button, <em>2 hops</em>, in rings around it. A solid line is a link{' '}
              <em>to</em> a concept, a dashed one is a link <em>from</em> it. At most 40 nodes are drawn, the concept
              included; the rest is announced as <em>+N omitted</em>. Links to concepts the bundle does not contain
              are drawn as ghosts and never navigate. Below the drawing, <em>List</em> gives every neighbour, drawn or
              not, with its relation to the concept: that list is the way to explore the neighbourhood without a
              pointer. <em>Open in graph</em> opens the global graph on this concept. A concept with no neighbour has
              no Neighbourhood.
            </li>
          </ul>
          <p>
            <strong>Enlarging it.</strong> The icon button beside the <em>1 hop</em> / <em>2 hops</em> toggle,{' '}
            <em>Enlarge the neighbourhood</em>, opens the same graph in a dialog, up to 1,100 × 760 px, for a
            neighbourhood too busy for the panel.
          </p>
          <Shot {...viewerShots.modal}>
            The dialog at two hops. The numbers are listed below.
          </Shot>
          <ol className="plain">
            <li>
              <strong>The hop toggle</strong>, shared with the panel: change it in one place and the other follows. It
              is not remembered from one page to the next.
            </li>
            <li>
              <strong>The drawing</strong>, laid out for the room it has: shapes and text keep their size, the rings
              spread out and, once the drawing is 560 px wide, labels are cut at 32 characters instead of 20. The cap
              of 40 nodes still applies, with the same <em>+N omitted</em> note. A click on a node opens its concept;
              a ghost does nothing.
            </li>
            <li>
              <strong>The list</strong>, always open: every neighbour with its relation to the concept, as in the
              panel.
            </li>
            <li>
              <strong>Open in graph</strong>, the same link as in the panel.
            </li>
            <li>
              <strong>Close.</strong> <kbd>Esc</kbd>, the button, or a click on the dimmed page behind also close it
              and put the focus back on the button that opened it.
            </li>
          </ol>
          <p>
            While the dialog is open, <kbd>Tab</kbd> and <kbd>Shift</kbd>+<kbd>Tab</kbd> move among its own controls and
            wrap round, the page behind is inert and does not scroll, and <kbd>/</kbd> and <kbd>Ctrl</kbd>+<kbd>K</kbd>{' '}
            do nothing. Below 760 px of window width the dialog fills the window and the list goes under the drawing.
          </p>
          <Shot {...viewerShots.modalNarrow}>The dialog at 390 px wide.</Shot>
        </Chapter>

        <Chapter id="graph" title="The global graph" refText="graph.html — §6 links, laid out in the browser">
          <p>
            <strong>Global graph</strong> in the header opens <code>graph.html</code> (<code>graph-1.html</code> if a
            concept already uses the name), a page that draws every body link of the bundle — the edges{' '}
            <code>okf graph</code> counts — with the arrows pointing at the target. The layout is computed in your
            browser, and it is deterministic: the same bundle gives the same starting layout on every conforming
            browser.
          </p>
          <Shot {...viewerShots.graph}>
            The global graph with <code>metrics/gross-margin</code> selected. The numbers are listed below.
          </Shot>
          <ol className="plain">
            <li>
              <strong>Facets</strong>, which narrow what is drawn. A node must satisfy every facet that has something
              chosen (<em>and</em> between facets) and any one of the values chosen inside a facet (<em>or</em>).
            </li>
            <li>
              <strong>The drawing.</strong> The wheel zooms and the background pans; a node can be dragged. The
              selection and its links are drawn in blue and a broken link to an absent concept is dashed.
            </li>
            <li>
              <strong>The tools</strong>: zoom in, zoom out, <em>Fit</em>, which frames everything drawn, and{' '}
              <em>List</em>.
            </li>
            <li>
              <strong>The detail drawer</strong> of the selected concept: its id, title, chips, description, the
              concepts it links to and those that reference it, and an <em>Open page</em> button. In the header,{' '}
              <em>Reading view</em> leads to the selected concept's page.
            </li>
            <li>
              <strong>The splitter</strong>, which resizes the drawer: see{' '}
              <a href="#splitter">Resizing the side column</a>.
            </li>
          </ol>
          <Shot {...viewerShots.graphFacets}>
            The facets with <em>Skill</em> unticked, the tag <code>finance</code> chosen and unmatched concepts dimmed
            instead of hidden.
          </Shot>
          <ul className="plain">
            <li>
              <strong>Type</strong> and <strong>Trust</strong> have a checkbox per value with its count;{' '}
              <strong>Freshness</strong> offers <em>Stale only (as of now)</em>; <strong>Tags</strong> shows the twelve
              most frequent ones, the rest behind <em>Show all tags (N)</em>.
            </li>
            <li>
              <strong>Display</strong> switches the node labels and chooses between hiding unmatched concepts and
              dimming them.
            </li>
          </ul>
          <p>
            <strong>Large bundles.</strong> Above 1,500 visible nodes (concepts and absent targets) the graph is not
            drawn at all: the list is shown and the status line asks for narrower filters. The list is also there
            whenever you want it, with the same facets applied.
          </p>
          <p>
            <strong>Addresses.</strong> Selecting a concept writes <code>graph.html#&lt;concept id&gt;</code> to the
            address bar without adding a history entry, and opening such an address selects that concept. A concept
            page's <em>Global graph</em> link carries its own id, so it lands on that concept.
          </p>
          <p>
            <strong>Keyboard.</strong> The drawing is a single tab stop. Inside it, <kbd>Page Up</kbd> and{' '}
            <kbd>Page Down</kbd> move through the nodes in index order, <kbd>Home</kbd> and <kbd>End</kbd> go to the
            first and the last, an arrow key goes to the nearest neighbour in that direction, <kbd>Space</kbd> selects
            and <kbd>Enter</kbd> opens the concept's page.
          </p>
        </Chapter>

        <Chapter id="theme" title="Theme and small screens" refText="follows the system, works on a phone">
          <p>
            The pages follow your system's light or dark setting. The moon button in the header overrides it for that
            site and remembers the choice in the browser's local storage; where storage is unavailable (some
            browsers refuse it for <code>file://</code> pages) the page simply keeps following the system.
          </p>
          <Shot {...viewerShots.dark}>
            The same concept page with a dark system theme, no setting made.
          </Shot>
          <p>
            On a narrow window the header wraps onto several rows and the columns stack as described{' '}
            <a href="#anatomy">above</a>.
          </p>
          <Shot {...viewerShots.narrow}>A concept page at 390 px wide.</Shot>
        </Chapter>

        <Chapter id="trust" title="What the marks mean" refText="§5.2–§5.5 — advisory, never access control">
          <p>
            Every mark comes from the concept's own frontmatter; <code>okf-render</code> reads it through the same
            audit that <code>okf audit</code> runs.
          </p>
          <MapTable
            head={['Mark', 'Means (and where the spec says so)']}
            rows={[
              [
                'human-reviewed',
                <>
                  <code>verified</code> holds at least one entry whose actor is <code>human:&lt;id&gt;</code> (§5.2,
                  §5.3)
                </>,
              ],
              [
                'machine-confirmed',
                <>
                  <code>verified</code> holds entries, but only from actors that are not <code>human:</code> (§5.2,
                  §5.3)
                </>,
              ],
              [
                'unverified',
                <>
                  No <code>verified</code> key, so no trust mark is drawn (§5.3)
                </>,
              ],
              [
                'stale after D / stale since D',
                <>
                  <code>stale_after</code> is a date still to come, or already reached
                  (<code>now ≥ stale_after</code>); the hourglass marks the second (§5.5)
                </>,
              ],
              [
                'status chip',
                <>
                  <code>draft</code>, <code>stable</code> or <code>deprecated</code>, as the concept declares it (§5.4)
                </>,
              ],
            ]}
          />
          <Warn title="A STAMP IS A DECLARATION">
            <p>
              The trust tier shows who a concept says reviewed it and when, not a proof that they did: the spec calls
              these tiers advisory signals, not access control, and <code>okf verify</code> cannot authenticate the
              actor it is given. Staleness is judged <strong>as of reading</strong>: the page compares{' '}
              <code>stale_after</code> with your browser's clock when it opens, and again when the tab becomes visible
              again, so a page rendered in March can be stale in June without being rendered again.
            </p>
          </Warn>
        </Chapter>

        <Chapter id="access" title="Keyboard and assistive technology" refText="every view has a text equivalent">
          <ul className="plain">
            <li>
              <strong>Everything is reachable without a pointer.</strong> The explorer, the palette, the frontmatter
              toggle, the graph's facets and tools are ordinary buttons, links and checkboxes in a sensible tab order;
              focus stays inside the palette while it is open and returns to where it was on <kbd>Esc</kbd>.
            </li>
            <li>
              <strong>Names and states are announced.</strong> The palette is a modal dialog with a labelled list and
              its active option; type and tag chips and the theme toggle report whether they are pressed; the selected
              node of the graph carries <code>aria-current</code>.
            </li>
            <li>
              <strong>Drawings are never the only view.</strong> The Neighbourhood has its <em>List</em>; the global
              graph has its <em>List</em>, the detail drawer and the keyboard contract above; the marks differ in shape
              as well as in colour.
            </li>
            <li>
              <strong>Motion is optional.</strong> With the system set to reduced motion the graph's layout still
              runs, without the animation.
            </li>
          </ul>
        </Chapter>

        <Chapter id="limits" title="What it does not do" refText="by design">
          <ul className="plain">
            <li>
              <strong>No full-text search.</strong> A static site has no process to run the shared{' '}
              <code>ConceptSearch</code> scorer in, and copying its weights into JavaScript would fork it. The palette
              finds concepts by title, id and tag; to search bodies, use <code>okf-mcp</code> or the library (
              <Link to="/docs/mcp">mcp.md</Link>, <Link to="/docs/library">library.md</Link>).
            </li>
            <li>
              <strong>No server and no live reload.</strong> There is no <code>okf serve</code>, and none is planned.
              Render again to see a change.
            </li>
            <li>
              <strong>Read-only.</strong> The pages never write to the bundle, and the bundle's markdown is untrusted
              input: raw HTML in a concept body is sanitised in the browser, scripts and event handlers included.
            </li>
            <li>
              <strong>Staleness is as of reading</strong>, not as of rendering, and trust marks are declarations (see
              above).
            </li>
          </ul>
          <Next>
            → <Link to="/docs/cli">cli.md</Link> — the <code>okf</code> commands that check the same bundle ·{' '}
            <Link to="/docs/getting-started">getting-started.md</Link> — write one first
          </Next>
        </Chapter>
      </div>
    </DocsLayout>
  )
}
