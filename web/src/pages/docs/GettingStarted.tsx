// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import DocsLayout from '../../layouts/DocsLayout'
import { PageDoc, Chapter, Next } from '../../components/doc'

// Whitespace-significant code samples (blank lines are part of the displayed
// code) mixing inline syntax-highlighting spans with literal text — kept as
// verbatim HTML strings per the technique established in Home.tsx, since JSX
// would collapse the blank lines. Sourced verbatim from
// website/docs/getting-started.html.
const dotnetVersionHtml = `$ dotnet --version
10.0.100`

const cloneAndPublishHtml = `$ git clone https://github.com/jchable/okf4net
$ dotnet publish src/OKF4net.Cli -c Release   <span class="c"># self-contained okf binary, no runtime needed</span>`

const firstBundleHtml = `<span class="c"># my_bundle/orders.md</span>
---
type: Table
title: Orders
description: One row per customer order.
timestamp: 2026-07-24
---

<span class="md-h"># Orders</span>

Each order belongs to a customer and carries a total.`

const validateCliHtml = `$ okf validate ./my_bundle

1 concept(s); 0 error(s), 0 warning(s), 0 info.
<span class="ok">✓ conformant with OKF v0.2</span>`

const validateCsharpHtml = `<span class="k">using</span> OKF4net;

<span class="k">var</span> bundle = Bundle.Load(<span class="s">"./my_bundle"</span>);
<span class="k">var</span> report = BundleValidator.Validate(bundle);

Console.WriteLine(report.IsConformant
    ? <span class="s">$"conformant with OKF v{OkfSpec.Version} ({bundle.Count} concepts)"</span>
    : <span class="s">$"{report.Diagnostics.Count} problems found"</span>);`

/**
 * Port of `website/docs/getting-started.html` — install the package, author
 * one concept, validate it, from both the CLI and the library.
 */
export default function GettingStarted() {
  return (
    <DocsLayout
      title="Getting started — OKF4net docs"
      description="Install OKF4net from NuGet, build the okf CLI, author your first OKF bundle, and validate it — from zero to a conformant knowledge bundle in a few minutes."
      current="getting-started"
    >
      <PageDoc
        path={
          <>
            docs/<b>getting-started.md</b>
          </>
        }
        type="Guide"
        title={
          <>
            From zero to a <em>validated bundle.</em>
          </>
        }
        lede={
          <>
            Install the package, write one markdown file with frontmatter, and prove it's conformant — with the{' '}
            <strong>
              <code>okf</code> CLI
            </strong>{' '}
            or the <strong>C# library</strong>. A few minutes, no database, no scaffolding.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="prerequisites" title="Prerequisites" refText="one tool">
          <p>
            The <a href="https://dotnet.microsoft.com/download">.NET SDK 10.0 or later</a>. That's the whole
            list — OKF4net has <strong>zero third-party runtime dependencies</strong>, so there's nothing else
            to install and nothing else to audit.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: dotnetVersionHtml }} />
        </Chapter>

        <Chapter id="install" title="Install" refText="library from NuGet · CLI from source">
          <p>
            Use the <strong>library</strong> to work with bundles from C#. Add the package to any project:
          </p>
          <pre className="block">$ dotnet add package OKF4net</pre>
          <p>
            One package, no transitive dependency tree. Prefer the command line? The{' '}
            <strong>
              <code>okf</code> CLI
            </strong>{' '}
            builds to a self-contained Native AOT binary from source (a packaged distribution is on the way):
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: cloneAndPublishHtml }} />
        </Chapter>

        <Chapter id="first-bundle" title="Write your first bundle" refText="§2–§4 — a concept is a file">
          <p>
            A <strong>bundle</strong> is just a directory. A <strong>concept</strong> is one markdown file: YAML
            frontmatter delimited by <code>---</code>, then a body. Create a folder and drop in a single concept:
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: firstBundleHtml }} />
          <p>
            The file's path with <code>.md</code> removed is its <strong>concept id</strong> — here,{' '}
            <code>orders</code>. The only value OKF conformance strictly requires (§11) is a non-empty{' '}
            <code>type</code>; <code>title</code>, <code>description</code> and <code>timestamp</code> round out a
            producer-grade concept.
          </p>
        </Chapter>

        <Chapter id="validate" title="Validate it" refText="§11 — conformance">
          <p>
            From the command line, <code>okf validate</code> checks the bundle and exits non-zero if anything is
            off — so it drops straight into CI:
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: validateCliHtml }} />
          <p>
            Or do the same from C#. <code>Bundle.Load</code> walks the directory, parses every concept, and builds
            the cross-link graph — <strong>permissively</strong>, so a bad file never aborts the load:
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: validateCsharpHtml }} />
          <p>
            Parse failures land in <code>bundle.ParseErrors</code> and broken cross-links stay in the graph as edges
            to missing concepts — you're told exactly where the holes are, never stopped at the first one.
          </p>
        </Chapter>

        <Chapter id="next" title="Where to next" refText="the rest of the manual">
          <ul className="plain">
            <li>
              <strong>Cross-link concepts and walk the graph.</strong> Ordinary markdown links between concepts
              become backlinks. → <Link to="/library">library.md</Link>
            </li>
            <li>
              <strong>Generate indexes and gate CI.</strong> <code>okf index</code> writes the <code>index.md</code>{' '}
              listings (§8); <code>okf validate</code>'s exit code is your CI check. →{' '}
              <Link to="/cli">cli.md</Link>
            </li>
            <li>
              <strong>Understand the format.</strong> Reserved files, conformance, and the section-by-section spec
              mapping. → <Link to="/what-okf-is">what-okf-is.md</Link>
            </li>
            <li>
              <strong>Use it in Claude.</strong> Serve the bundle to Claude Desktop, Claude Code, or Cursor as
              read/write tools. → <Link to="/docs/mcp">mcp.md</Link>
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
