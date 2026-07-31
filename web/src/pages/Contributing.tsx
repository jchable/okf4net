// SPDX-License-Identifier: LGPL-3.0-or-later
import Layout from '../layouts/Layout'
import { PageDoc, Chapter, Warn, Steps, Cta } from '../components/doc'

// Whitespace-significant code sample mixing inline syntax-highlighting spans
// with literal text — kept as a verbatim HTML string per the technique
// established in Home.tsx. Sourced verbatim from website/contributing.html:50-52.
const buildTestHtml = `$ dotnet build OKF4net.sln            <span class="c"># core library + okf CLI + test project</span>
$ dotnet test OKF4net.sln             <span class="c"># unit + integration tests (incl. golden CLI comparisons)</span>
$ dotnet publish src/OKF4net.Cli -c Release   <span class="c"># Native AOT, self-contained okf binary</span>`

export default function Contributing() {
  return (
    <Layout
      title="Contributing — OKF4net"
      description="How to contribute to OKF4net: one prerequisite (the .NET SDK), byte-exact golden fixtures, spec citations in PRs, and zero third-party runtime dependencies — by design."
      current="contributing"
    >
      <PageDoc
        path={
          <>
            my_bundle/<b>contributing.md</b>
          </>
        }
        type="Guide"
        title={
          <>
            One prerequisite: <em>the .NET SDK.</em>
          </>
        }
        lede={
          <>
            That's the whole list. OKF4net has <strong>zero third-party runtime dependencies by design</strong> —
            please keep it that way (test-only packages like xunit are fine).
          </>
        }
      />

      <div className="docbody">
        <Chapter id="start" title="Where to start" refText="new here?">
          <p>
            Pick an issue labelled{' '}
            <a href="https://github.com/jchable/okf4net/labels/good%20first%20issue">good first issue</a> —
            scoped, names the files, states how to verify. Bigger idea? See the roadmap below, or open a{' '}
            <a href="https://github.com/jchable/okf4net/discussions">Discussion</a> first.
          </p>
        </Chapter>

        <Chapter id="roadmap" title="Roadmap" refText="now · next · later">
          <ul className="plain">
            <li>
              <strong>Now:</strong> broader test coverage and tutorials, for library users and agent builders
              alike.
            </li>
            <li>
              <strong>Next:</strong> more Agent Framework and Catalog samples, performance baselines, a bundle
              viewer.
            </li>
            <li>
              <strong>Later:</strong> ecosystem integrations, tracking the spec past v0.2.
            </li>
          </ul>
        </Chapter>

        <Chapter id="build" title="Build and test" refText="warnings are errors">
          <pre className="block" dangerouslySetInnerHTML={{ __html: buildTestHtml }} />
          <p>All warnings are treated as errors, so a clean build is required.</p>
          <Warn title="GOLDEN FIXTURES — DO NOT REFORMAT">
            <p>
              <code>tests/fixtures/</code> contains <strong>byte-exact golden files</strong>: several tests compare
              generated output byte-for-byte against them. Most were captured from this project's own former Rust
              implementation before its removal (see <code>NOTICE</code>); a couple of newer ones (v0.2, §10
              Attested Computation) postdate that implementation and are hand-verified against the spec text
              instead — none of them currently re-check against Google's own OKF reference implementation, whose
              CLI has no equivalent commands to compare output against. They are protected by{' '}
              <code>.gitattributes</code> and excluded from <code>.editorconfig</code> normalization. Never let an
              editor or formatter touch them — trailing whitespace, final newlines, and line endings are all
              significant.
            </p>
          </Warn>
        </Chapter>

        <Chapter id="style" title="Code style" refText="enforced in CI">
          <ul className="plain">
            <li>
              Formatting is enforced with <code>dotnet format --verify-no-changes</code>; run{' '}
              <code>dotnet format OKF4net.sln</code> before pushing.
            </li>
            <li>
              Follow the existing conventions: file-scoped namespaces, XML doc comments on public API, nullable
              reference types enabled.
            </li>
            <li>
              New source files need the SPDX header used across the codebase:{' '}
              <code>// SPDX-License-Identifier: LGPL-3.0-or-later</code>
            </li>
          </ul>
        </Chapter>

        <Chapter id="spec" title="Spec fidelity" refText="cite your §">
          <p>
            OKF4net implements the{' '}
            <a href="https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md">OKF v0.2 spec</a>
            . Behavioural changes must stay conformant — <strong>cite the relevant section (§) in your PR description</strong>.
            The data model intentionally mirrors Google's OKF reference implementation's structure; divergences
            need a documented reason.
          </p>
        </Chapter>

        <Chapter id="submit" title="Submitting changes" refText="the suite is the contract">
          <Steps>
            <li>
              <strong>Fork</strong> and create a topic branch from <code>main</code>.
            </li>
            <li>
              <strong>Add or update tests</strong> for any behaviour change — the test suite is the contract,
              including golden comparisons for CLI-visible output.
            </li>
            <li>
              <strong>Make sure</strong> <code>dotnet test</code> and <code>dotnet format --verify-no-changes</code>{' '}
              pass.
            </li>
            <li>
              <strong>Open a pull request</strong> with a clear description of the what and why.
            </li>
          </Steps>
          <p style={{ marginTop: '16px' }}>
            Bug reports and feature requests go through{' '}
            <a href="https://github.com/jchable/okf4net/issues">GitHub issues</a>; please use the provided templates.
            By contributing, you agree that your contributions are licensed under the project license,{' '}
            <strong>LGPL-3.0-or-later</strong>.
          </p>
          <Cta title="Ship knowledge as files.">
            <p>The spec is short, the codebase is dependency-free, and the test suite tells you immediately whether you're right.</p>
            <div className="hero-actions">
              <a className="btn primary" href="https://github.com/jchable/okf4net">
                github.com/jchable/okf4net
              </a>
              <a className="btn" href="https://github.com/jchable/okf4net/issues">
                Open issues
              </a>
            </div>
          </Cta>
        </Chapter>
      </div>
    </Layout>
  )
}
