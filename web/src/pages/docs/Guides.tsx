// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import DocsLayout from '../../layouts/DocsLayout'
import { PageDoc, Chapter, Next } from '../../components/doc'

// Whitespace-significant code samples (blank lines are part of the displayed
// code) mixing inline syntax-highlighting spans with literal text — kept as
// verbatim HTML strings per the technique established in Home.tsx, since JSX
// would collapse the blank lines. Sourced verbatim from
// website/docs/guides.html.
const traverseHtml = `<span class="k">using</span> OKF4net;

<span class="k">var</span> bundle = Bundle.Load(<span class="s">"./my_bundle"</span>);
<span class="k">var</span> id = ConceptId.Parse(<span class="s">"tables/orders"</span>);

<span class="c">// Outgoing — each link knows whether its target exists.</span>
<span class="k">foreach</span> (<span class="k">var</span> link <span class="k">in</span> bundle.LinksFrom(id))
    Console.WriteLine(<span class="s">$"{id} -&gt; {link.Target} (exists: {link.Exists})"</span>);

<span class="c">// Incoming — who cites this concept.</span>
<span class="k">foreach</span> (<span class="k">var</span> back <span class="k">in</span> bundle.Backlinks(id))
    Console.WriteLine(<span class="s">$"cited by {back}"</span>);

<span class="c">// Every dangling edge in the whole bundle.</span>
<span class="k">foreach</span> (<span class="k">var</span> (source, rawTarget) <span class="k">in</span> bundle.BrokenLinks())
    Console.WriteLine(<span class="s">$"{source} -x {rawTarget}"</span>);`

const ciWorkflowHtml = `<span class="c"># .github/workflows/knowledge.yml</span>
name: Validate knowledge
on: [push]
jobs:
  okf:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: ./okf validate ./bundles/ga4`

const ciLibraryHtml = `<span class="k">var</span> report = BundleValidator.Validate(Bundle.Load(<span class="s">"./bundles/ga4"</span>));
Environment.Exit(report.IsConformant ? <span class="s">0</span> : <span class="s">1</span>);`

const indexHtml = `<span class="k">var</span> written = IndexGenerator.RegenerateIndexes(<span class="s">"./my_bundle"</span>);
<span class="k">foreach</span> (<span class="k">var</span> path <span class="k">in</span> written)
    Console.WriteLine(<span class="s">$"wrote {path}"</span>);`

const changelogHtml = `<span class="k">using</span> OKF4net;

<span class="k">foreach</span> (<span class="k">var</span> logPath <span class="k">in</span> bundle.LogFiles)
{
    <span class="k">var</span> log = ChangeLog.Parse(File.ReadAllText(logPath));
    <span class="k">foreach</span> (<span class="k">var</span> day <span class="k">in</span> log.Days)
        Console.WriteLine(<span class="s">$"{day.Date}: {day.Entries.Count} entr(y/ies)"</span>);
}`

const normalizeHtml = `<span class="k">var</span> doc = OkfDocument.Parse(File.ReadAllText(<span class="s">"orders.md"</span>));
File.WriteAllText(<span class="s">"orders.md"</span>, doc.Serialize());`

const publishHtml = `$ git clone https://github.com/jchable/okf4net
$ dotnet publish src/OKF4net.Cli -c Release   <span class="c"># self-contained okf binary</span>`

/**
 * Port of `website/docs/guides.html` — task-shaped recipes over the public
 * API: traverse the graph, gate CI, regenerate indexes, read a change log,
 * normalize a document, publish the AOT binary.
 */
export default function Guides() {
  return (
    <DocsLayout
      title="Guides — OKF4net docs"
      description="Task recipes for OKF4net: traverse the cross-link graph, gate CI on conformance, regenerate index.md listings, read a change log, normalize documents, and publish the Native AOT okf binary."
      current="guides"
    >
      <PageDoc
        path={
          <>
            docs/<b>guides.md</b>
          </>
        }
        type="Guide"
        title={
          <>
            Recipes for <em>real tasks.</em>
          </>
        }
        lede={
          <>
            Short, task-shaped walkthroughs — traverse a graph, gate a build, regenerate listings, normalize a file,
            ship the binary. For the exhaustive surface, see <Link to="/docs/library">library.md</Link> and{' '}
            <Link to="/docs/cli">cli.md</Link>.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="traverse" title="Traverse the cross-link graph" refText="§5 — LinksFrom · Backlinks">
          <p>Load once, then walk edges in either direction. Broken links stay in the graph so you can find every hole in one pass.</p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: traverseHtml }} />
        </Chapter>

        <Chapter id="ci" title="Gate CI on conformance" refText="§9 — exit codes">
          <p>
            <code>okf validate</code> exits non-zero on a non-conformant bundle. The binary is self-contained, so a
            runner needs no .NET installed — copy the file and run it.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: ciWorkflowHtml }} />
          <p>Prefer to gate from your own tool? The library reports the same result — warnings never fail conformance, only errors do:</p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: ciLibraryHtml }} />
        </Chapter>

        <Chapter id="index" title="Regenerate index.md listings" refText="§6 — progressive disclosure">
          <p>After adding or renaming concepts, rewrite every directory listing. The call returns the paths it wrote.</p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: indexHtml }} />
          <p>
            Same thing from the shell: <code>okf index ./my_bundle</code>. Supply your own description synthesizer
            with <code>RegenerateIndexesWith(root, synthesize)</code> when the default summaries aren't enough.
          </p>
        </Chapter>

        <Chapter id="changelog" title="Read a change log" refText="§7 — log.md">
          <p>
            <code>Bundle.LogFiles</code> lists every reserved <code>log.md</code>; <code>ChangeLog.Parse</code> turns
            one into date-grouped entries.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: changelogHtml }} />
        </Chapter>

        <Chapter id="normalize" title="Normalize a document" refText="§4 — the round-trip guarantee">
          <p>
            Parse and re-serialize to normalize frontmatter and block structure. <strong>Unknown keys come out
            untouched</strong> — the ordered mapping is preserved, not projected onto a fixed type.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: normalizeHtml }} />
          <p>
            Or in place from the shell: <code>okf fmt orders.md -w</code>.
          </p>
        </Chapter>

        <Chapter id="publish" title="Publish the AOT binary" refText="no runtime on the target">
          <p>
            Build <code>okf</code> as a self-contained, single-file Native AOT executable — nothing to install where
            it runs.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: publishHtml }} />
          <Next>
            → <Link to="/docs/cli">cli.md</Link> — every command and flag · <Link to="/docs/library">library.md</Link> — the full API
          </Next>
        </Chapter>
      </div>
    </DocsLayout>
  )
}
