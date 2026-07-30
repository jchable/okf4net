// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import DocsLayout from '../../layouts/DocsLayout'
import { PageDoc, Chapter, IndexTable, ConceptGrid, Cell, Term, Next } from '../../components/doc'

/**
 * The docs landing page — `docs/index.md`, the generated listing `okf index`
 * would write for the `docs/` bundle. Port of `website/docs/index.html`
 * (commit `40fe17f`).
 */
export default function DocsIndex() {
  return (
    <DocsLayout
      title="Docs — OKF4net"
      description="Developer documentation for OKF4net — the .NET implementation of the Open Knowledge Format. Getting started, task guides, and reference for the library, the okf CLI, the Agent Framework tools, and the catalog. The manual is itself an OKF bundle."
      current="index"
    >
      <PageDoc
        path={
          <>
            docs/<b>index.md</b>
          </>
        }
        type="Index"
        title={
          <>
            The manual is a <em>bundle.</em>
          </>
        }
        lede={
          <>
            These docs are an OKF bundle — one markdown <strong>concept</strong> per page, cross-linked and indexed. What
            follows is <code>index.md</code>: the generated listing <code>okf index</code> would write for this directory.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="contents" title="Contents" refText="§8 — generated index listing">
          <IndexTable
            rows={[
              {
                type: 'Guide',
                concept: <Link to="/docs/getting-started">getting-started</Link>,
                desc: (
                  <>
                    Install the library from NuGet and the <code>okf</code> CLI, then validate your first bundle.
                  </>
                ),
              },
              {
                type: 'Guide',
                concept: <Link to="/docs/guides">guides</Link>,
                desc: (
                  <>
                    Task recipes: traverse the cross-link graph, gate CI on conformance, generate indexes (§8) and
                    changelogs (§9), round-trip with <code>fmt</code>, publish the AOT binary.
                  </>
                ),
              },
              {
                type: 'Reference',
                concept: <Link to="/docs/library">library</Link>,
                desc: (
                  <>
                    The C# API surface — <code>Bundle</code>, <code>ConceptId</code>, <code>OkfDocument</code>,{' '}
                    <code>Frontmatter</code>, the YAML subset, links, <code>IndexGenerator</code>, <code>ChangeLog</code>.
                  </>
                ),
              },
              {
                type: 'Reference',
                concept: <Link to="/docs/cli">cli</Link>,
                desc: (
                  <>
                    The six <code>okf</code> commands — flags, exit codes, and copy-paste transcripts.
                  </>
                ),
              },
              {
                type: 'Reference',
                concept: <Link to="/docs/agents">agents</Link>,
                desc: 'The Microsoft Agent Framework layer — ten bundle tools and a budget-bounded context provider.',
              },
              {
                type: 'Reference',
                concept: <Link to="/docs/catalog">catalog</Link>,
                desc: (
                  <>
                    A hot-reloadable <code>catalog.json</code> manifest naming local bundles as sources, a
                    multi-source resolver, and a scoped memory store for multi-tenant agent deployments.
                  </>
                ),
              },
              {
                type: 'Guide',
                concept: <Link to="/docs/mcp">mcp</Link>,
                desc: (
                  <>
                    Serve a bundle to Claude Desktop, Claude Code, and any MCP client with the <code>okf-mcp</code>{' '}
                    server — install and connect, step by step.
                  </>
                ),
              },
              {
                type: 'Reference',
                concept: <Link to="/docs/spec">spec</Link>,
                desc: 'OKF v0.2, section by section, mapped to the types that implement it.',
              },
            ]}
          />
          <Next>Every concept above is published — guides teach, reference tells.</Next>
        </Chapter>

        <Chapter id="start" title="Start here" refText="what you came to do">
          <ConceptGrid>
            <Cell>
              <Term>use the library</Term>
              <p>
                Load bundles, parse and round-trip concepts, walk the graph from C#. → <Link to="/library">library.md</Link>
              </p>
            </Cell>
            <Cell>
              <Term>use the cli</Term>
              <p>
                Validate, index, and graph bundles from one AOT binary — drops into CI. → <Link to="/cli">cli.md</Link>
              </p>
            </Cell>
            <Cell>
              <Term>understand okf</Term>
              <p>
                What a bundle is, the reserved files, and the one conformance rule. →{' '}
                <Link to="/what-okf-is">what-okf-is.md</Link>
              </p>
            </Cell>
            <Cell>
              <Term>build an agent</Term>
              <p>
                Expose a bundle to an AI agent as tools plus bounded context. → <Link to="/docs/agents">agents.md</Link>
              </p>
            </Cell>
            <Cell>
              <Term>search many bundles</Term>
              <p>
                Name bundles as sources in one manifest and search them all. → <Link to="/docs/catalog">catalog.md</Link>
              </p>
            </Cell>
            <Cell>
              <Term>use it in Claude</Term>
              <p>
                Serve the bundle to Claude and any MCP client as read/write tools. → <Link to="/docs/mcp">mcp.md</Link>
              </p>
            </Cell>
          </ConceptGrid>
        </Chapter>

        <Chapter id="how" title="How these docs are organised" refText="guides teach · reference tells">
          <p>
            <strong>Guides</strong> are task-shaped — they walk you end to end through one job. <strong>Reference</strong>{' '}
            pages are lookup-shaped — one entry per type, command, or tool, exhaustive and skimmable. The sidebar is the
            bundle tree; every page is a concept you could <code>cat</code>.
          </p>
          <Next>
            → <Link to="/">okf4net.md</Link> — back to the overview
          </Next>
        </Chapter>
      </div>
    </DocsLayout>
  )
}
