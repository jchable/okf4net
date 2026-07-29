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
okf 0.1.0-alpha.1 (OKF spec v0.2)`

const validateHtml = `$ okf validate tests/fixtures/appendix_a
<span class="c">[warning] tests/fixtures/appendix_a/tables/users.md: missing recommended frontmatter field \`description\`</span>
<span class="c">[warning] tests/fixtures/appendix_a/tables/users.md: missing recommended frontmatter field \`timestamp\`</span>

4 concept(s); 0 error(s), 2 warning(s), 0 info.
<span class="ok">✓ conformant with OKF v0.2</span>`

const infoHtml = `$ okf info tests/fixtures/appendix_a
bundle:     tests/fixtures/appendix_a
concepts:   4
index.md:   0
log.md:     1

types:
     1  BigQuery Dataset
     3  BigQuery Table

links:      5 internal (0 broken)`

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

const ciSnippetHtml = `<span class="c"># any pipeline — fail the build on non-conformant knowledge</span>
okf validate ./bundles/ga4`

const buildHtml = `$ git clone https://github.com/jchable/okf4net
$ dotnet publish src/OKF4net.Cli -c Release   <span class="c"># self-contained okf binary</span>`

/**
 * Port of `website/docs/cli.html` — the six `okf` subcommands: synopsis,
 * per-command reference with real captured output, exit codes, build.
 */
export default function Cli() {
  return (
    <DocsLayout
      title="CLI reference — OKF4net docs"
      description="Reference for the okf command-line tool: validate, info, index, graph, parse and fmt — arguments, flags, real output, and exit codes. A self-contained Native AOT binary."
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
            Six subcommands over a bundle or a file, a self-contained <strong>Native AOT binary</strong> with no
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
            </tbody>
          </table>
          <p>
            Global options: <code>-h</code>/<code>--help</code> prints usage; <code>-V</code>/<code>--version</code>{' '}
            prints the build and spec version. A path beginning with <code>-</code> can be passed after a{' '}
            <code>--</code> separator.
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
        </Chapter>

        <Chapter id="info" title="info <bundle>" refText="a summary, no mutation">
          <p>
            Reports the bundle root, declared OKF version (if any), concept count, reserved-file counts, a breakdown
            by <code>type</code>, and the internal link total with broken-link count. Unparseable files are listed at
            the end. Always exits <code>0</code>.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: infoHtml }} />
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

        <Chapter id="build" title="Build it" refText="Native AOT publish">
          <pre className="block" dangerouslySetInnerHTML={{ __html: buildHtml }} />
        </Chapter>
      </div>
    </DocsLayout>
  )
}
