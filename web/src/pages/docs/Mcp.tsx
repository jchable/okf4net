// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import DocsLayout from '../../layouts/DocsLayout'
import { PageDoc, Chapter, MapTable, ConceptGrid, Cell, Term, Steps, Warn, Next, Tag } from '../../components/doc'

// Whitespace-significant code samples (blank lines are part of the displayed
// code) mixing inline syntax-highlighting spans with literal text — kept as
// verbatim HTML strings per the technique established in Home.tsx, since JSX
// would collapse the blank lines. Sourced verbatim from `git show
// 40fe17f:website/docs/mcp.html` (the page is not in the working tree).
const installHtml = `$ dotnet tool install -g OKF4net.Mcp   <span class="c"># installs the okf-mcp command</span>`

// Windows path with escaped backslashes, exactly as captured in
// commit 40fe17f — each `\\` in the rendered output needs `\\\\` here since
// a template literal collapses one backslash-escape level.
const claudeDesktopConfigHtml = `{
  <span class="s">"mcpServers"</span>: {
    <span class="s">"okf"</span>: {
      <span class="s">"command"</span>: <span class="s">"okf-mcp"</span>,
      <span class="s">"args"</span>: [<span class="s">"C:\\\\Users\\\\you\\\\my-bundle"</span>]
    }
  }
}`

const cursorConfigHtml = `{
  <span class="s">"mcpServers"</span>: {
    <span class="s">"okf"</span>: {
      <span class="s">"command"</span>: <span class="s">"okf-mcp"</span>,
      <span class="s">"args"</span>: [<span class="s">"/path/to/my-bundle"</span>],
      <span class="s">"env"</span>: { <span class="s">"OKF_MCP_READONLY"</span>: <span class="s">"1"</span> }
    }
  }
}`

const readOnlyConfigHtml = `{
  <span class="s">"mcpServers"</span>: {
    <span class="s">"okf-docs"</span>: {
      <span class="s">"command"</span>: <span class="s">"okf-mcp"</span>,
      <span class="s">"args"</span>: [<span class="s">"/path/to/reference-bundle"</span>],
      <span class="s">"env"</span>: { <span class="s">"OKF_MCP_READONLY"</span>: <span class="s">"1"</span> }
    }
  }
}`

/**
 * Port of `website/docs/mcp.html` (commit `40fe17f` — the page was removed
 * from the working tree but the content still describes the shipped
 * `okf-mcp` server). Install, connect Claude Desktop / Claude Code / Cursor,
 * use it, read-only mode, how it works.
 */
export default function Mcp() {
  return (
    <DocsLayout
      title="MCP — OKF4net docs"
      description="Serve an OKF bundle to Claude Desktop, Claude Code, Cursor, and any MCP client with okf-mcp — a local Model Context Protocol server. Install, connect each client step by step, and read, search, and write concepts from a conversation."
      current="mcp"
    >
      <PageDoc
        path={
          <>
            docs/<b>mcp.md</b>
          </>
        }
        type="Guide"
        title={
          <>
            Your bundle, live in <em>Claude.</em>
          </>
        }
        lede={
          <>
            <code>okf-mcp</code> is a small <strong>MCP server</strong>. Point it at a bundle and its nine
            operations become tools inside <strong>Claude</strong> — and any MCP client — so you read, search, and
            write concepts from a conversation. It's the same tools as the Agent Framework layer, spoken over
            the <a href="https://modelcontextprotocol.io">Model Context Protocol</a>.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="what" title="What you get" refText="MCP — the tool channel">
          <p>
            MCP is the open protocol Claude Desktop, Claude Code, and editors like Cursor use to talk to local tools.{' '}
            <code>okf-mcp</code> is a <strong>thin façade</strong> over the same <code>OkfBundleTools</code> the CLI
            and the Agent Framework layer use — one bundle per server, nine tools, read and write. Everything runs
            through the library, so path-safety, producer validation, and permissive loading come for free.
          </p>
          <MapTable
            head={['Tool', 'Does']}
            rows={[
              ['okf_read_concept', 'One concept — frontmatter, body, outgoing links, backlinks'],
              ['okf_browse', 'Progressive-disclosure listing of a directory (§6)'],
              ['okf_search', 'Ranked full-text search over titles, tags, and bodies'],
              ['okf_graph', "Link stats, or one concept's links, backlinks, broken links (§5)"],
              ['okf_write_concept', 'Create or update a concept — producer validation first (§9)'],
              [
                'okf_append_log',
                <>
                  Append a dated entry to <code>log.md</code> (§7)
                </>,
              ],
              [
                'okf_regenerate_indexes',
                <>
                  Rewrite every <code>index.md</code> (§6)
                </>,
              ],
              ['okf_validate_bundle', 'Conformance report (§9)'],
              ['okf_changes_since', 'What changed since an ISO date, across every log'],
            ]}
          />
        </Chapter>

        <Chapter id="install" title="Install" refText="one tool on your PATH">
          <p>
            The prerequisite is the <a href="https://dotnet.microsoft.com/download">.NET SDK 10.0 or later</a>.
            Install <code>okf-mcp</code> as a .NET global tool:
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: installHtml }} />
          <p>
            That's the whole install — one command, <code>okf-mcp</code>, on your PATH. Each server serves
            exactly <strong>one bundle</strong>; run one entry per bundle you want a client to reach.
          </p>
        </Chapter>

        <Chapter id="claude-desktop" title="Connect Claude Desktop" refText="claude_desktop_config.json">
          <Steps>
            <li>
              <strong>
                Install <code>okf-mcp</code>
              </strong>{' '}
              (above) and note the absolute path to your bundle.
            </li>
            <li>
              <strong>Open the config.</strong> Claude Desktop → <em>Settings → Developer → Edit Config</em> opens{' '}
              <code>claude_desktop_config.json</code>.
            </li>
            <li>
              <strong>
                Add an <code>okf</code> server
              </strong>{' '}
              under <code>mcpServers</code>:
              <pre className="block" dangerouslySetInnerHTML={{ __html: claudeDesktopConfigHtml }} />
            </li>
            <li>
              <strong>Restart Claude Desktop.</strong> The <code>okf</code> tools appear in the tools menu — ask it
              to <em>"browse the okf bundle"</em> to confirm.
            </li>
          </Steps>
          <p>
            Prefer an environment variable to a positional path? Drop <code>args</code> and use{' '}
            <code>"env": {'{'} "OKF_BUNDLE_ROOT": "/path/to/my-bundle" {'}'}</code> instead — the two are
            interchangeable.
          </p>
        </Chapter>

        <Chapter id="claude-code" title="Connect Claude Code" refText="claude mcp add">
          <p>
            From your terminal, register the server. The <code>--</code> separates Claude Code's own flags from
            the command it runs:
          </p>
          <pre className="block">$ claude mcp add okf -- okf-mcp /path/to/my-bundle</pre>
          <p>Save it to your user scope so it's available everywhere, and start it read-only by passing an environment variable with <code>-e</code>:</p>
          <pre className="block">$ claude mcp add --scope user okf -e OKF_MCP_READONLY=1 -- okf-mcp /path/to/my-bundle</pre>
          <p>
            <code>claude mcp list</code> shows the registered servers; inside a session, <code>/mcp</code> lists the{' '}
            <code>okf</code> tools it exposes.
          </p>
        </Chapter>

        <Chapter id="other-clients" title="Connect Cursor & other clients" refText="any stdio MCP client">
          <p>
            Any client that speaks MCP over stdio can run <code>okf-mcp</code>, and the config shape is the same
            everywhere. In <strong>Cursor</strong>, add it to <code>~/.cursor/mcp.json</code> (global) or{' '}
            <code>.cursor/mcp.json</code> (one project):
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: cursorConfigHtml }} />
          <p>
            The essentials for any client are the same three: run <code>okf-mcp</code>, pass the bundle path as the
            first argument (or set <code>OKF_BUNDLE_ROOT</code>), and let it talk MCP over stdio.
          </p>
        </Chapter>

        <Chapter id="use" title="Use it" refText="read, search, write — in plain language">
          <p>Once connected, just describe the task; the model picks the tool. A few everyday moves:</p>
          <ConceptGrid>
            <Cell>
              <Term>"What do I know about refunds?"</Term>
              <p>
                <code>okf_search</code> then <code>okf_read_concept</code> — finds the matching concepts and reads
                them back.
              </p>
            </Cell>
            <Cell>
              <Term>"Note that refunds now take 3 days."</Term>
              <p>
                <code>okf_write_concept</code> and <code>okf_append_log</code> — writes the concept and records the
                change (§7).
              </p>
            </Cell>
            <Cell>
              <Term>"What changed since Monday?"</Term>
              <p>
                <code>okf_changes_since</code> — aggregates every <code>log.md</code> in the bundle from that date.
              </p>
            </Cell>
            <Cell>
              <Term>"Are there any broken links?"</Term>
              <p>
                <code>okf_graph</code> and <code>okf_validate_bundle</code> — dangling edges (§5) and a conformance
                report (§9).
              </p>
            </Cell>
          </ConceptGrid>
          <Warn title="IT WRITES TO YOUR BUNDLE">
            <p>
              <code>okf_write_concept</code> and <code>okf_append_log</code> modify files on disk. Point{' '}
              <code>okf-mcp</code> at a directory you keep under version control — or start it read-only (next) — so
              every change is one you can see and undo.
            </p>
          </Warn>
          <Next>
            After a batch of writes, ask it to <em>"regenerate the indexes"</em> (<code>okf_regenerate_indexes</code>)
            so the <code>index.md</code> listings stay current.
          </Next>
        </Chapter>

        <Chapter id="read-only" title="Read-only mode" refText="consultation only">
          <p>
            Set <code>OKF_MCP_READONLY=1</code> and <code>okf-mcp</code> registers only the six read tools — the
            three writers (<code>okf_write_concept</code>, <code>okf_append_log</code>,{' '}
            <code>okf_regenerate_indexes</code>) are left out entirely. Use it for a shared reference bundle you want
            the model to consult but never edit.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: readOnlyConfigHtml }} />
        </Chapter>

        <Chapter id="how" title="How it works" refText="a thin façade, nothing reimplemented">
          <p>
            Each tool <em>is</em> an <code>OkfBundleTools</code> operation — the same code behind{' '}
            <Link to="/cli">the CLI</Link> and the Agent Framework layer — wrapped as an MCP tool. So a concept id
            that would escape the bundle is rejected, a write is validated against the §9 producer rules{' '}
            <strong>before</strong> it touches disk, and a malformed file never aborts a load. Logs go to{' '}
            <code>stderr</code>; <code>stdout</code> carries only the protocol.
          </p>
          <Next>
            →{' '}
            <span className="muted">
              agents.md <Tag>soon</Tag>
            </span>{' '}
            — the same nine tools for the Microsoft Agent Framework · <Link to="/library">library.md</Link> — the API
            underneath
          </Next>
        </Chapter>

        <Chapter id="next" title="Where to next" refText="the rest of the manual">
          <ul className="plain">
            <li>
              <strong>Author the bundle first.</strong> Write one concept, validate it, then hand it to a client. →{' '}
              <Link to="/docs/getting-started">getting-started.md</Link>
            </li>
            <li>
              <strong>Do the same from code.</strong> Load bundles, walk the graph, generate indexes from C#. →{' '}
              <Link to="/library">library.md</Link>
            </li>
            <li>
              <strong>Or from the shell.</strong> The <code>okf</code> CLI runs the same operations without a
              client. → <Link to="/cli">cli.md</Link>
            </li>
          </ul>
          <Next>
            → <Link to="/docs">docs.md</Link> — back to the docs index
          </Next>
        </Chapter>
      </div>
    </DocsLayout>
  )
}
