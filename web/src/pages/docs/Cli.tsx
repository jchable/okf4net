// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import DocsLayout from '../../layouts/DocsLayout'
import { PageDoc, Chapter, MapTable, Next } from '../../components/doc'

// Whitespace-significant code samples (blank lines are part of the displayed
// code) mixing inline syntax-highlighting spans with literal text — kept as
// verbatim HTML strings per the technique established in Home.tsx, since JSX
// would collapse the blank lines. Sourced verbatim from
// website/docs/cli.html.
const versionHtml = `$ okf --version
okf 0.5.0 (OKF spec v0.2)`

const validateHtml = `$ okf validate tests/fixtures/appendix_a
<span class="c">[warning] tests/fixtures/appendix_a/tables/users.md: missing recommended frontmatter field \`description\`</span>
<span class="c">[warning] tests/fixtures/appendix_a/tables/users.md: missing recommended frontmatter field \`timestamp\`</span>

4 concept(s); 0 error(s), 2 warning(s), 0 info.
<span class="ok">✓ conformant with OKF v0.2</span>`

const auditHtml = `$ okf audit bundles/acme_retail --as-of 2027-06-01
bundle:     bundles/acme_retail
as of:      2027-06-01
concepts:   9

trust:
     8  human-reviewed
     0  machine-confirmed
     1  unverified

status:
     0  draft
     8  stable
     1  deprecated

stale:      7 of 9 past stale_after

needs attention (7):
  computations/gross-margin-period  stale 2026-12-31  human-reviewed  stable
  computations/revenue-ytd  stale 2026-12-31  human-reviewed  stable
  metrics/gross-margin  stale 2026-12-31  human-reviewed  stable
  metrics/revenue  stale 2026-12-31  human-reviewed  stable
  policies/margin-standard  stale 2026-12-31  human-reviewed  stable
  policies/revenue-recognition  stale 2026-12-31  human-reviewed  stable
  tables/orders  stale 2026-12-31  human-reviewed  stable`

const auditQueryHtml = `<span class="c"># any filter flag switches to one line per concept — pipe-friendly</span>
$ okf audit bundles/acme_retail --trust unverified
skills/run-on-bq  no-stale-after  unverified  stable`

const infoHtml = `$ okf info tests/fixtures/appendix_a
bundle:     tests/fixtures/appendix_a
concepts:   4
index.md:   0
log.md:     1

types:
     1  BigQuery Dataset
     3  BigQuery Table

links:      5 internal (0 broken)`

const validateJsonHtml = `$ okf validate tests/fixtures/appendix_a --json
{"bundle":"tests/fixtures/appendix_a","conformant":true,"conceptCount":4,"errorCount":0,"warningCount":2,"infoCount":0,"diagnostics":[{"severity":"warning","code":"MissingRecommendedField","path":"tests/fixtures/appendix_a/tables/users.md","conceptId":"tables/users","field":"description","message":"missing recommended frontmatter field \`description\`"}, …]}`

const infoJsonHtml = `$ okf info tests/fixtures/appendix_a --json
{"bundle":"tests/fixtures/appendix_a","okfVersion":null,"conceptCount":4,"indexFileCount":0,"logFileCount":1,"types":{"BigQuery Dataset":1,"BigQuery Table":3},"linkCount":5,"brokenLinkCount":0,"parseErrors":[]}`

const indexHtml = `$ okf index ./my_bundle
wrote my_bundle/index.md
wrote my_bundle/tables/index.md

2 index file(s) regenerated.`

const graphHtml = `$ okf graph tests/fixtures/appendix_a
datasets/sales
  -&gt; tables/orders
  -&gt; tables/customers
tables/customers
  -&gt; tables/orders
tables/orders
  -&gt; datasets/sales
  -&gt; tables/customers`

const graphDotHtml = `$ okf graph tests/fixtures/appendix_a --dot
digraph okf {
  rankdir=LR; node [shape=box, fontsize=10];
  <span class="s">"datasets/sales"</span> -&gt; <span class="s">"tables/orders"</span>;
  <span class="s">"datasets/sales"</span> -&gt; <span class="s">"tables/customers"</span>;
  <span class="s">"tables/customers"</span> -&gt; <span class="s">"tables/orders"</span>;
  <span class="s">"tables/orders"</span> -&gt; <span class="s">"datasets/sales"</span>;
  <span class="s">"tables/orders"</span> -&gt; <span class="s">"tables/customers"</span>;
}

$ okf graph ./my_bundle --dot | dot -Tsvg &gt; graph.svg`

const parseHtml = `$ okf parse tests/fixtures/appendix_a/tables/orders.md
frontmatter (6 key(s)):
  type: BigQuery Table
  title: Orders
  description: One row per completed customer order.
  resource: https://console.cloud.google.com/bigquery?…
  tags: [sales, orders]
  timestamp: 2026-05-28T00:00:00Z

has non-empty \`type\`: true
body: 99 byte(s)

links (2):
  [Absolute] sales dataset -&gt; /datasets/sales.md
  [Absolute] customers -&gt; /tables/customers.md`

const fmtHtml = `$ okf fmt tests/fixtures/appendix_a/tables/orders.md
---
type: BigQuery Table
title: Orders
description: One row per completed customer order.
resource: https://console.cloud.google.com/bigquery?p=acme&amp;d=sales&amp;t=orders
tags:
  - sales
  - orders
timestamp: 2026-05-28T00:00:00Z
---

# Schema

Part of the [sales dataset](/datasets/sales.md). FK to [customers](/tables/customers.md).`

const renderHtml = `$ okf render tests/fixtures/appendix_a --out ./site
wrote 8 files to ./site

$ ls ./site
assets/  datasets/  index.html  tables/`

const ciSnippetHtml = `<span class="c"># any pipeline — fail the build on non-conformant knowledge</span>
okf validate ./bundles/ga4`

const wingetInstallHtml = `$ winget install Coderise.OKF4net`

const buildHtml = `$ git clone https://github.com/jchable/okf4net
$ dotnet publish src/OKF4net.Cli -c Release   <span class="c"># self-contained okf binary</span>`

/**
 * Port of `website/docs/cli.html` — the eight `okf` subcommands: synopsis,
 * per-command reference with real captured output, exit codes, build.
 */
export default function Cli() {
  return (
    <DocsLayout
      title="CLI reference — OKF4net docs"
      description="Reference for the okf command-line tool: validate, info, index, graph, parse, fmt and render — arguments, flags, real output, and exit codes. A self-contained Native AOT binary."
      current="cli"
    >
      <PageDoc
        path={
          <>
            docs/<b>cli.md</b>
          </>
        }
        type="Reference"
        title={
          <>
            The <em>okf</em> command line.
          </>
        }
        lede={
          <>
            Eight subcommands over a bundle or a file, a self-contained <strong>Native AOT binary</strong> with no
            runtime to install. <code>validate</code> exits non-zero on a non-conformant bundle, so the whole tool
            drops into CI as one line.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="synopsis" title="Synopsis" refText="okf <command> [args]">
          <table className="map">
            <thead>
              <tr>
                <th>Command</th>
                <th>Argument</th>
                <th>Does</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>
                  <a href="#validate">validate</a>
                </td>
                <td>&lt;bundle&gt;</td>
                <td>Check a bundle against OKF v0.2 conformance (§11)</td>
              </tr>
              <tr>
                <td>
                  <a href="#audit">audit</a>
                </td>
                <td>&lt;bundle&gt;</td>
                <td>Report trust, freshness and lifecycle across the bundle (§5.3–§5.5)</td>
              </tr>
              <tr>
                <td>
                  <a href="#info">info</a>
                </td>
                <td>&lt;bundle&gt;</td>
                <td>Summarize concepts, types, links and version</td>
              </tr>
              <tr>
                <td>
                  <a href="#index">index</a>
                </td>
                <td>&lt;bundle&gt;</td>
                <td>(Re)generate every index.md (§8)</td>
              </tr>
              <tr>
                <td>
                  <a href="#graph">graph</a>
                </td>
                <td>&lt;bundle&gt;</td>
                <td>
                  Print the cross-link graph; <code>--dot</code> for Graphviz
                </td>
              </tr>
              <tr>
                <td>
                  <a href="#parse">parse</a>
                </td>
                <td>&lt;file&gt;</td>
                <td>Parse one document and print its structure</td>
              </tr>
              <tr>
                <td>
                  <a href="#fmt">fmt</a>
                </td>
                <td>&lt;file&gt;</td>
                <td>
                  Normalize by parse + re-serialize (<code>-w</code> writes)
                </td>
              </tr>
              <tr>
                <td>
                  <a href="#render">render</a>
                </td>
                <td>
                  &lt;bundle&gt; <code>--out</code> &lt;dir&gt;
                </td>
                <td>Generate a browsable static HTML site from the bundle</td>
              </tr>
            </tbody>
          </table>
          <p>
            Global options: <code>-h</code>/<code>--help</code> prints usage; <code>-V</code>/<code>--version</code>{' '}
            prints the build and spec version; <code>--as-of &lt;YYYY-MM-DD&gt;</code> pins today's date for{' '}
            <a href="#validate">validate</a> and <a href="#audit">audit</a>.
          </p>
          <p>
            Everything after a <code>--</code> separator is an argument, never an option — which is how a path
            beginning with <code>-</code> is passed. The rule holds for every verb and every flag, so{' '}
            <code>okf fmt -- notes.md -w</code> treats <code>-w</code> as a second filename rather than as the
            write-in-place flag; write it as <code>okf fmt -w -- notes.md</code> if that is what you meant. A value
            belonging to an option is likewise only ever a value: in <code>okf audit b --type --stale</code>,{' '}
            <code>--stale</code> is the type being searched for, not a filter.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: versionHtml }} />
        </Chapter>

        <Chapter id="validate" title="validate <bundle>" refText="§11 — exit code is the interface">
          <p>
            Loads the bundle, runs the §11 conformance check, and prints every diagnostic followed by a tally. Exits{' '}
            <code>0</code> when conformant, <code>1</code> otherwise. <strong>Warnings never break conformance</strong>{' '}
            — only errors do — so recommended-but-missing fields are surfaced without failing the build.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: validateHtml }} />
          <p>
            <code>--json</code> emits the same diagnostics as machine-readable, camelCase JSON — one object per line,
            no pretty-printing. Each diagnostic carries a stable <code>code</code> (<code>DiagnosticCode</code>) and,
            where relevant, a <code>field</code> naming the frontmatter key involved, so a caller can branch on the
            finding without parsing prose.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: validateJsonHtml }} />
          <p>
            <code>--as-of &lt;YYYY-MM-DD&gt;</code> pins the date the §5.5 staleness warning is evaluated against.
            Without it that one diagnostic depends on the day the command runs, so a pipeline asserting on
            validate's output should pin it rather than let the calendar move underneath.
          </p>
        </Chapter>

        <Chapter id="audit" title="audit <bundle>" refText="§5.3–§5.5 — the corpus, not the concept">
          <p>
            Answers questions about the bundle <em>as a whole</em>: how much of it is human-reviewed, what has passed
            its <code>stale_after</code> date, what is deprecated. Counts always describe the whole bundle while the
            worklist describes the selection — <code>audit</code> is a worklist, not an inventory. Always exits{' '}
            <code>0</code>: a stale concept is editorial hygiene, not a conformance failure.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: auditHtml }} />
          <p>
            With no filter flag it selects exactly what <code>--stale</code> selects and prints the summary above.
            With any of <code>--stale</code>, <code>--trust</code>, <code>--status</code> or <code>--type</code> it
            prints one line per matching concept and nothing else, so the output pipes. <code>--as-of</code> pins the
            observation date (it never changes the mode), and <code>--json</code> always emits the full document.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: auditQueryHtml }} />
          <p>
            The question this exists for — <em>which concepts are past their <code>stale_after</code> date and have
            never been verified by a human?</em> — is <code>--stale --trust unverified,machine-confirmed</code>: both
            tiers, because &ldquo;machine-confirmed&rdquo; also means no human ever looked.
          </p>
        </Chapter>

        <Chapter id="info" title="info <bundle>" refText="a summary, no mutation">
          <p>
            Reports the bundle root, declared OKF version (if any), concept count, reserved-file counts, a breakdown
            by <code>type</code>, and the internal link total with broken-link count. Unparseable files are listed at
            the end. Always exits <code>0</code>.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: infoHtml }} />
          <p>
            <code>--json</code> works here too, for scripting against the same summary.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: infoJsonHtml }} />
        </Chapter>

        <Chapter id="index" title="index <bundle>" refText="§8 — progressive disclosure">
          <p>
            Regenerates every <code>index.md</code> directory listing in the bundle and prints each path written,
            then a total. On an empty bundle it prints <code>no index files written (empty bundle?)</code>. Exits{' '}
            <code>0</code>. This command <strong>writes files</strong> — run it after adding or renaming concepts.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: indexHtml }} />
        </Chapter>

        <Chapter id="graph" title="graph <bundle> [--dot]" refText="§6 — cross-links">
          <p>
            Prints each concept's outgoing links. Resolved links use <code>-&gt;</code>, broken ones{' '}
            <code>-x</code>. Pass <code>--dot</code> to emit Graphviz DOT (broken edges dashed and red) — pipe it
            straight into <code>dot</code>.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: graphHtml }} />
          <pre className="block" dangerouslySetInnerHTML={{ __html: graphDotHtml }} />
        </Chapter>

        <Chapter id="parse" title="parse <file>" refText="§4 — one document">
          <p>
            Parses a single concept document (strict UTF-8) and prints its frontmatter keys, whether it has a
            non-empty <code>type</code>, the body size in bytes, and any links and citations. Exits <code>0</code> if
            the document is conformant, <code>1</code> if not.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: parseHtml }} />
        </Chapter>

        <Chapter id="fmt" title="fmt <file> [-w]" refText="the round-trip, made tangible">
          <p>
            Parses the document and re-serializes it — normalizing frontmatter and block structure while{' '}
            <strong>preserving unknown keys byte for byte</strong>. Prints to stdout by default; <code>-w</code> (or{' '}
            <code>--write</code>) writes back in place. Exits <code>0</code>.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: fmtHtml }} />
        </Chapter>

        <Chapter id="render" title="render <bundle> --out <dir>" refText="a self-contained static site">
          <p>
            Generates a browsable HTML site from the bundle: one page per concept (a frontmatter table, in document
            order, with unknown producer keys preserved, followed by the rendered body) plus a generated index page
            built from the same logic as <code>okf index</code>. Inter-concept links are rewired to the generated
            pages, with backlinks (&ldquo;Referenced by&rdquo;) added on each target page; a link to a concept that
            doesn't exist is flagged and left non-clickable rather than silently dropped or pointed at a 404.
            External links and in-page anchors are left untouched.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: renderHtml }} />
          <p>
            The output is <strong>self-contained</strong> — it opens straight from the filesystem
            (<code>file://</code>) with no server required. Markdown renders <strong>client-side</strong>, via a
            vendored copy of <a href="https://github.com/markedjs/marked">marked</a> v15.0.12 (MIT, credited in{' '}
            <code>NOTICE</code>); a DOM sanitizer strips anything the fixed template doesn't expect. GFM task list
            items survive sanitization as real, disabled <code>&lt;input type=&quot;checkbox&quot;&gt;</code> elements
            with the correct checked state, so a screen reader announces them as checkboxes rather than as
            decorative text.
          </p>
          <p>
            There's no full-text search in this slice — a static site has no server to run the shared{' '}
            <code>ConceptSearch</code> scorer against, and duplicating its ranking logic in JavaScript would fork the
            one place that scorer is meant to live. Search arrives with the planned <code>okf serve</code> companion
            (the live-server half of this work). Backed by the new, zero-dependency <code>OKF4net.Viewer</code>{' '}
            project, which ships inside the <code>okf</code> binary — it isn't published as a separate NuGet
            package.
          </p>
        </Chapter>

        <Chapter id="ci" title="Exit codes & CI" refText="copy the file, run it">
          <MapTable
            head={['Code', 'Meaning']}
            rows={[
              [
                '0',
                <>
                  Success — or, for <code>validate</code>/<code>parse</code>, conformant
                </>,
              ],
              ['1', 'Non-conformant, missing argument, unknown subcommand, or I/O error'],
            ]}
          />
          <p>Because the binary is self-contained, a CI image needs no .NET runtime, no SDK, no restore:</p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: ciSnippetHtml }} />
          <Next>
            → <Link to="/library">library.md</Link> — the same engine, as a C# API · <Link to="/docs/getting-started">getting-started.md</Link>
          </Next>
        </Chapter>

        <Chapter id="install" title="Install it" refText="winget on Windows, or Native AOT publish">
          <p>
            On Windows, install via <a href="https://github.com/microsoft/winget-pkgs">winget</a>:
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: wingetInstallHtml }} />
          <p>On any OS, build it from source:</p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: buildHtml }} />
        </Chapter>
      </div>
    </DocsLayout>
  )
}
