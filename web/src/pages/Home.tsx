// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import Layout from '../layouts/Layout'
import RawRenderedToggle from '../components/RawRenderedToggle'

// The two code samples below are whitespace-significant (blank lines are
// part of the displayed code) and mix inline syntax-highlighting spans with
// literal text, so they are kept as verbatim HTML strings — reproducing
// them as nested JSX would have JSX's automatic whitespace-collapsing strip
// the blank lines that are part of the sample. Sourced verbatim from
// website/index.html:113-124 and :141-145.
const libraryUsageHtml = `<span class="k">using</span> OKF4net;

<span class="k">var</span> bundle = Bundle.Load(<span class="s">"./my_bundle"</span>);
Console.WriteLine(<span class="s">$"{bundle.Count} concepts"</span>);

<span class="c">// Conformance check (§11).</span>
<span class="k">var</span> report = BundleValidator.Validate(bundle);

<span class="c">// Traverse the cross-link graph.</span>
<span class="k">var</span> id = ConceptId.Parse(<span class="s">"tables/orders"</span>);
<span class="k">foreach</span> (<span class="k">var</span> b <span class="k">in</span> bundle.Backlinks(id))
    Console.WriteLine(<span class="s">$"cited by {b}"</span>);`

const cliSessionHtml = `$ okf validate ./bundles/ga4

42 concept(s); 0 error(s), 0 warning(s), 0 info.
<span class="ok">✓ conformant with OKF v0.2</span>
$ okf graph ./bundles/ga4 --dot | dot -Tsvg &gt; graph.svg`

export default function Home() {
  return (
    <Layout
      title="OKF4net — knowledge is a directory of markdown files"
      description="OKF4net is a zero-dependency .NET implementation of the Open Knowledge Format (OKF) v0.2 — a C# library and a Native AOT okf CLI to parse, validate, index and graph bundles of markdown concepts."
      current="home"
    >
      {/* ============ HERO: this page is itself an OKF concept document ============ */}
      <div className="hero">
        <div className="hero-doc">
          <RawRenderedToggle />
        </div>
      </div>

      {/* ============ DOCUMENT BODY ============ */}
      <div className="docbody">
        <section className="chapter" id="okf">
          <div className="chead">
            <span className="h">##</span>
            <h2>What OKF is</h2>
            <span className="ref">§2–§3 — terminology, bundle structure</span>
          </div>
          <blockquote>If you can <code>cat</code> a file, you can read OKF; if you can <code>git clone</code> a repo, you can ship it.</blockquote>
          <p>The <a href="https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md">Open Knowledge Format</a> represents knowledge as a directory of UTF-8 markdown files with YAML frontmatter. No database, no binary format, no runtime — files that humans, git, and agents already understand.</p>
          <div className="concept-grid">
            <div className="cell">
              <div className="term">bundle</div>
              <p>A directory tree of markdown files — the unit of distribution.</p>
            </div>
            <div className="cell">
              <div className="term">concept</div>
              <p>One document: YAML frontmatter delimited by <code>---</code>, then a markdown body.</p>
            </div>
            <div className="cell">
              <div className="term">concept id</div>
              <p>The file's path with <code>.md</code> removed: <code>tables/users.md</code> → <code>tables/users</code>.</p>
            </div>
            <div className="cell">
              <div className="term">cross-links</div>
              <p>Ordinary markdown links between concepts — absolute or relative; backlinks derived.</p>
            </div>
          </div>
          <p>The only hard conformance requirement (§11): a non-empty <code>type</code> on every concept. Everything else — unknown types, unknown keys, broken links — must be tolerated by consumers.</p>
          <p className="next">→ <Link to="/what-okf-is">what-okf-is.md</Link> — reserved files, conformance, and the section-by-section spec mapping</p>
        </section>

        <section className="chapter" id="library">
          <div className="chead">
            <span className="h">##</span>
            <h2>The Library</h2>
            <span className="ref">§4, §6 — documents, cross-linking</span>
          </div>
          <p>Load a bundle, check conformance, walk the cross-link graph. <strong><code>Bundle.Load</code> never aborts on a bad file</strong> — parse failures land in <code>ParseErrors</code>, broken links stay in the graph as edges to missing concepts.</p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: libraryUsageHtml }} />
          <p><code>Frontmatter</code> keeps the <strong>full ordered mapping</strong> with typed getters on top — producer-defined keys survive round-trips byte for byte. Two validation levels: <code>ValidateConformance()</code> enforces only what §11 requires; <code>Validate()</code> matches the stricter producer-side check.</p>
          <p className="next">→ <Link to="/library">library.md</Link> — install, examples, design choices · <Link to="/docs/library">docs/library.md</Link> — every member, signature by signature</p>
        </section>

        <section className="chapter" id="cli">
          <div className="chead">
            <span className="h">##</span>
            <h2>The CLI</h2>
            <span className="ref">§8–§11 — indexes, logs, conformance</span>
          </div>
          <p>Published as a <strong>self-contained Native AOT single-file binary</strong> — no .NET runtime on the target machine. <code>okf validate</code> exits non-zero on a non-conformant bundle, so it drops straight into CI.</p>
          <table className="map">
            <tbody>
              <tr><th>Command</th><th>Does</th></tr>
              <tr><td>okf validate &lt;bundle&gt;</td><td>Conformance check (§11), non-zero exit on failure</td></tr>
              <tr><td>okf audit &lt;bundle&gt;</td><td>Trust, freshness and lifecycle across the bundle (§5.3–§5.5)</td></tr>
              <tr><td>okf info &lt;bundle&gt;</td><td>Concepts, types, links, version</td></tr>
              <tr><td>okf index &lt;bundle&gt;</td><td>(Re)generate every index.md (§8)</td></tr>
              <tr><td>okf graph &lt;bundle&gt;</td><td>Cross-link graph, <code>--dot</code> for Graphviz</td></tr>
              <tr><td>okf parse &lt;file&gt;</td><td>One document's structure</td></tr>
              <tr><td>okf fmt &lt;file&gt;</td><td>Normalize by parse + re-serialize (-w writes)</td></tr>
              <tr><td>okf render &lt;bundle&gt; --out &lt;dir&gt;</td><td>Self-contained, browsable static HTML site</td></tr>
            </tbody>
          </table>
          <pre className="block" dangerouslySetInnerHTML={{ __html: cliSessionHtml }} />
          <p className="next">→ <Link to="/cli">cli.md</Link> — building the binary, session transcripts, CI recipes · <Link to="/docs/cli">docs/cli.md</Link> — every flag and exit code</p>
        </section>

        <section className="chapter" id="agents">
          <div className="chead">
            <span className="h">##</span>
            <h2>The Agent tools</h2>
            <span className="ref">Microsoft Agent Framework — ten tools + bounded context</span>
          </div>
          <p><code>OKF4net.Agents</code> turns a bundle into <strong>ten <code>AIFunction</code> tools</strong> (read, search, write, validate, log, §10 attested computation, …) — an eleventh, <code>okf_run_computation</code>, when an attestation orchestrator is wired in — plus <code>OkfContextProvider</code>, which injects budget-bounded reference data automatically — never as instructions — and, opt-in, captures exchanges as deterministic memory, single-bundle or scoped across tenants, users, and sessions.</p>
          <p className="next">→ <Link to="/docs/agents">docs/agents.md</Link> — the tools, the context provider, and scoped memory capture</p>
        </section>

        <section className="chapter" id="catalog">
          <div className="chead">
            <span className="h">##</span>
            <h2>Local catalog</h2>
            <span className="ref">catalog.json — search many bundles, host multi-tenant memory</span>
          </div>
          <p><code>OKF4net.Catalog</code> names one or more local bundles as <strong>sources</strong> in a hot-reloadable <code>catalog.json</code> manifest and searches every enabled one, ranked by a <strong>selectable resolver strategy</strong> — grouped by source, merged by score, or priority-weighted, with an optional fairness quota — and with trust and staleness carried on every result. Per-caller <strong>source visibility</strong> restricts which sources a given caller may search, by a host-precomputed set or a per-source policy function. A <code>role: "memory"</code> source, backed by <code>FileMemoryStore</code>, gives an agent deployment host-scoped, layered memory — session, user, and tenant — without cross-scope leakage.</p>
          <p className="next">→ <Link to="/docs/catalog">docs/catalog.md</Link> — the manifest, the resolver, and scoped memory</p>
        </section>

        <section className="chapter" id="mcp">
          <div className="chead">
            <span className="h">##</span>
            <h2>MCP — In Claude & your editor</h2>
            <span className="ref">MCP — the bundle as tools</span>
          </div>
          <p>Run <code>okf-mcp</code>, point it at a bundle, and its ten operations become tools inside <strong>Claude Desktop, Claude Code, and Cursor</strong> — read, search, and write concepts from a conversation, over the <a href="https://modelcontextprotocol.io">Model Context Protocol</a>. Same engine as the library and the CLI, exposed to any MCP client.</p>
          <p>On <strong>Claude Code</strong>, skip the manual config: the <a href="https://github.com/jchable/okf4net-claude-plugin">OKF plugin</a> installs <code>okf-mcp</code>, an <code>okf</code> skill, and guided <code>/okf-init</code> / <code>/okf-validate</code> slash commands in one step — <code>/plugin marketplace add jchable/okf4net-claude-plugin</code>, then <code>/plugin install okf@okf4net</code>.</p>
          <p className="next">→ <Link to="/docs/mcp">docs/mcp.md</Link> — install <code>okf-mcp</code> and connect each client, step by step</p>
        </section>

        <section className="chapter" id="contribute">
          <div className="chead">
            <span className="h">##</span>
            <h2>Contributing</h2>
            <span className="ref">PR — the suite is the contract</span>
          </div>
          <p>One prerequisite: the .NET SDK. <strong>Zero third-party runtime dependencies</strong> is a design rule — contributions keep it that way. Behavioural changes cite their spec section (§) in the PR; the test suite is the contract.</p>
          <p className="next">→ <Link to="/contributing">contributing.md</Link> — setup, golden fixtures, code style, and how to submit</p>
          <div className="cta">
            <h2>Ship knowledge as files.</h2>
            <p>Star the repo, open an issue, or pick a good first one. The spec is short, the codebase is dependency-free, and the test suite tells you immediately whether you're right.</p>
            <div className="hero-actions">
              <a className="btn primary" href="https://github.com/jchable/okf4net">github.com/jchable/okf4net</a>
              <a className="btn" href="https://github.com/jchable/okf4net/issues">Open issues</a>
            </div>
          </div>
        </section>
      </div>
    </Layout>
  )
}
