// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import Layout from '../layouts/Layout'
import { PageDoc, Chapter, MapTable, Next } from '../components/doc'

// Whitespace-significant code samples (blank lines are part of the displayed
// code) mixing inline syntax-highlighting spans with literal text — kept as
// verbatim HTML strings per the technique established in Home.tsx, since JSX
// would collapse the blank lines. Sourced verbatim from
// website/library.html:57-72 and :78-85.
const bundleWalkHtml = `<span class="k">using</span> OKF4net;

<span class="k">var</span> bundle = Bundle.Load(<span class="s">"./my_bundle"</span>);
Console.WriteLine(<span class="s">$"{bundle.Count} concepts"</span>);

<span class="c">// Conformance check (§11).</span>
<span class="k">var</span> report = BundleValidator.Validate(bundle);
<span class="k">if</span> (report.IsConformant)
    Console.WriteLine(<span class="s">$"conformant with OKF v{OkfSpec.Version}"</span>);

<span class="c">// Traverse the cross-link graph.</span>
<span class="k">var</span> id = ConceptId.Parse(<span class="s">"tables/orders"</span>);
<span class="k">foreach</span> (<span class="k">var</span> link <span class="k">in</span> bundle.LinksFrom(id))
    Console.WriteLine(<span class="s">$"{id} -&gt; {link.Target} (exists: {link.Exists})"</span>);
<span class="k">foreach</span> (<span class="k">var</span> backlink <span class="k">in</span> bundle.Backlinks(id))
    Console.WriteLine(<span class="s">$"cited by {backlink}"</span>);`

const documentRoundTripHtml = `<span class="k">using</span> OKF4net;

<span class="k">var</span> doc = OkfDocument.Parse(<span class="s">"---\\ntype: Metric\\ntitle: DAU\\n---\\n\\n# Body\\n"</span>);
Console.WriteLine(doc.Frontmatter.Type); <span class="c">// "Metric"</span>
doc.ValidateConformance(); <span class="c">// throws DocumentValidationException on failure</span>

<span class="c">// Serialize() preserves frontmatter key order and the body.</span>
<span class="k">var</span> text = doc.Serialize();`

export default function Library() {
  return (
    <Layout
      title="The library — OKF4net"
      description="Using OKF4net as a C# library: install from NuGet, load bundles, parse and round-trip concept documents, traverse the cross-link graph — with zero third-party dependencies."
      current="library"
    >
      <PageDoc
        path={
          <>
            my_bundle/<b>library.md</b>
          </>
        }
        type="Reference"
        title={
          <>
            Pure C#, <em>zero dependencies.</em>
          </>
        }
        lede={
          <>
            OKF4net is implemented entirely on the .NET <strong>base class library</strong> — it brings its own
            YAML-subset parser, markdown link scanner, and directory walker. Nothing to audit but this one package.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="install" title="Install" refText="nuget.org/packages/OKF4net">
          <pre className="block">$ dotnet add package OKF4net</pre>
          <p>One package, no transitive dependency tree. Targets modern .NET; every public API carries XML documentation.</p>
        </Chapter>

        <Chapter id="bundles" title="Load a bundle, walk the graph" refText="§3, §6 — Bundle · LinkScanner">
          <p>
            <code>Bundle.Load</code> walks the directory tree, parses every concept, and builds the cross-link graph
            with backlinks. It is <strong>permissive by design</strong>: a bad file never aborts the load.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: bundleWalkHtml }} />
          <p>
            Parse failures are collected in <code>ParseErrors</code>; broken cross-links are retained as graph edges
            to non-existent concepts, so <code>link.Exists</code> tells you exactly where the holes are.
          </p>
        </Chapter>

        <Chapter id="documents" title="Parse and round-trip a document" refText="§4 — OkfDocument · Frontmatter">
          <pre className="block" dangerouslySetInnerHTML={{ __html: documentRoundTripHtml }} />
          <p>
            Rather than deserializing into a fixed type — which would drop producer-defined keys —{' '}
            <code>Frontmatter</code> keeps the <strong>full ordered mapping</strong> and layers typed getters (
            <code>Type</code>, <code>Title</code>, <code>Tags</code>, …) on top. Round-trips preserve unknown keys,
            as the spec requires of consumers.
          </p>
        </Chapter>

        <Chapter id="design" title="Design choices" refText="what makes it faithful">
          <ul className="plain">
            <li>
              <strong>Frontmatter preserves everything.</strong> The full ordered mapping survives; typed accessors
              are a view, not a projection.
            </li>
            <li>
              <strong>Permissive loading.</strong> <code>Bundle.Load</code> collects errors and keeps going —
              exactly what §11 asks of consumers.
            </li>
            <li>
              <strong>Two levels of validation.</strong> §11-only conformance, or the stricter producer-side check (
              <code>type</code>, <code>title</code>, <code>description</code>, <code>timestamp</code>).
            </li>
            <li>
              <strong>A documented YAML subset.</strong> Block/flow collections, quoted and plain scalars,{' '}
              <code>|</code>/<code>&gt;</code> block scalars, comments — and clear errors for anchors, tags, and
              multi-document streams, which frontmatter never uses.
            </li>
          </ul>
        </Chapter>

        <Chapter id="api" title="API surface" refText="one type per responsibility">
          <MapTable
            head={['Type / namespace', 'Responsibility']}
            rows={[
              [
                <>
                  Yaml.YamlValue · YamlMapping
                </>,
                'YAML-subset value/mapping model for frontmatter',
              ],
              [
                <>
                  Yaml.YamlValue.Parse · YamlEmitter
                </>,
                'Parser entry point and emitter for the same subset',
              ],
              [<code key="doc">OkfDocument</code>, 'Frontmatter + body; parse / serialize / validate (§4)'],
              [<code key="fm">Frontmatter</code>, 'Typed accessors over an order-preserving mapping (§4.1)'],
              [<code key="cid">ConceptId</code>, 'Id ↔ path conversion and segment validation (§2)'],
              [<code key="ls">LinkScanner</code>, 'Markdown link extraction, classification, citations (§6, §13.1)'],
              [<code key="bundle">Bundle</code>, 'Walk a tree, build the concept graph + backlinks (§3, §6)'],
              [<code key="ig">IndexGenerator</code>, 'Generate index.md directory listings (§8)'],
              [<code key="cl">ChangeLog</code>, 'Parse / build log.md update histories (§9)'],
              [<code key="bv">BundleValidator</code>, '§11 conformance with severity-tagged diagnostics'],
            ]}
          />
          <Next>
            → <Link to="/cli">cli.md</Link> — the same engine, as one AOT binary for CI
          </Next>
        </Chapter>
      </div>
    </Layout>
  )
}
