// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import Layout from '../layouts/Layout'
import { PageDoc, Chapter, MapTable, Next } from '../components/doc'

// Whitespace-significant code samples (blank lines are part of the displayed
// code) mixing inline syntax-highlighting spans with literal text — kept as
// verbatim HTML strings per the technique established in Home.tsx. Sourced
// verbatim from website/cli.html:63-82.
const sessionHtml = `$ okf validate ./bundles/ga4

42 concept(s); 0 error(s), 0 warning(s), 0 info.
<span class="ok">✓ conformant with OKF v0.2</span>

$ okf info ./bundles/ga4
bundle:     ./bundles/ga4
okf_version: 0.2
concepts:   42
index.md:   6
log.md:     1

types:
    22  Table
    12  Metric
     8  Dataset

links:      117 internal (3 broken)

$ okf graph ./bundles/ga4 --dot | dot -Tsvg &gt; graph.svg`

const ciSnippetHtml = `<span class="c"># .github/workflows/knowledge.yml — or any CI</span>
okf validate ./bundles/ga4`

const buildSnippetHtml = `$ git clone https://github.com/jchable/okf4net
$ dotnet publish src/OKF4net.Cli -c Release   <span class="c"># Native AOT, self-contained okf binary</span>`

export default function Cli() {
  return (
    <Layout
      title="The okf CLI — OKF4net"
      description="The okf command-line tool: validate, info, index, graph, parse and fmt — a self-contained Native AOT binary that drops straight into CI."
      current="cli"
    >
      <PageDoc
        path={
          <>
            my_bundle/<b>cli.md</b>
          </>
        }
        type="Reference"
        title={
          <>
            Six commands, <em>one binary.</em>
          </>
        }
        lede={
          <>
            <code>okf</code> is published as a <strong>self-contained, Native AOT single-file binary</strong> — no
            .NET runtime installation required on the target machine.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="commands" title="Commands" refText="§8–§11 — the whole surface">
          <MapTable
            head={['Command', 'Does']}
            rows={[
              ['okf validate <bundle>', 'Check a bundle against OKF v0.2 conformance (§11); exits non-zero on failure'],
              ['okf info <bundle>', 'Summarize a bundle — concepts, types, links, version'],
              ['okf index <bundle>', '(Re)generate every index.md in the bundle (§6)'],
              [
                'okf graph <bundle>',
                <>
                  Print the cross-link graph; <code>--dot</code> emits Graphviz DOT
                </>,
              ],
              ['okf parse <file>', 'Parse one concept document and print its structure'],
              [
                'okf fmt <file>',
                <>
                  Normalize a document by parse + re-serialize (<code>-w</code> writes in place)
                </>,
              ],
            ]}
          />
        </Chapter>

        <Chapter id="session" title="A session" refText="what it looks like">
          <pre className="block" dangerouslySetInnerHTML={{ __html: sessionHtml }} />
          <p>
            Everything the library exposes — permissive loading, broken-link tracking, byte-exact serialization — is
            the same engine underneath. <code>okf fmt</code> is the round-trip guarantee made tangible: parse +
            re-serialize, and unknown frontmatter keys come out untouched.
          </p>
        </Chapter>

        <Chapter id="ci" title="In CI" refText="§9 — exit codes are the interface">
          <p>
            <code>okf validate</code> exits non-zero when a bundle is not conformant, so validating knowledge is one
            line in any pipeline:
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: ciSnippetHtml }} />
          <p>Because the binary is self-contained, the CI image needs no .NET runtime, no SDK, no package restore — copy the file, run it.</p>
        </Chapter>

        <Chapter id="build" title="Build it yourself" refText="Native AOT publish">
          <pre className="block" dangerouslySetInnerHTML={{ __html: buildSnippetHtml }} />
          <Next>
            → <Link to="/contributing">contributing.md</Link> — build, test, and submit changes
          </Next>
        </Chapter>
      </div>
    </Layout>
  )
}
