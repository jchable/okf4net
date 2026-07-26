// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import DocsLayout from '../../layouts/DocsLayout'
import { PageDoc, Chapter, Next } from '../../components/doc'

/**
 * Port of `website/docs/spec.html` — the OKF v0.1 conformance rule, the
 * section-by-section spec → type mapping, reserved files, and fidelity.
 */
export default function Spec() {
  return (
    <DocsLayout
      title="Spec mapping — OKF4net docs"
      description="The Open Knowledge Format v0.1, section by section, mapped to the OKF4net types that implement it: concept ids (§2), bundles (§3), documents (§4), cross-links (§5), indexes (§6), logs (§7), citations (§8), conformance (§9), versioning (§11)."
      current="spec"
    >
      <PageDoc
        path={
          <>
            docs/<b>spec.md</b>
          </>
        }
        type="Reference"
        title={
          <>
            OKF v0.1, <em>section by section.</em>
          </>
        }
        lede={
          <>
            The format is short. Each spec section maps to one OKF4net type — parse failures never abort a load,
            unknown keys and broken links are tolerated, and exactly one thing is required. Here is the whole
            surface, cross-referenced to the{' '}
            <a href="https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md">upstream spec</a>{' '}
            and to <Link to="/docs/library">library.md</Link>.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="one-rule" title="The one hard rule" refText="§9 — conformance">
          <blockquote>
            Every concept must carry a non-empty <code>type</code>. Everything else, a consumer must tolerate.
          </blockquote>
          <p>
            That is the entire conformance bar. Unknown types, unknown frontmatter keys, broken cross-links, and
            missing optional fields are all <strong>valid</strong> — a conformant consumer keeps going and reports,
            never rejects. OKF4net enforces exactly this in <code>BundleValidator</code> (§9) and, per document, in{' '}
            <code>OkfDocument.ValidateConformance()</code>; the stricter producer check (<code>type</code>,{' '}
            <code>title</code>, <code>description</code>, <code>timestamp</code>) is a separate, opt-in{' '}
            <code>Validate()</code>.
          </p>
        </Chapter>

        <Chapter id="mapping" title="Section by section" refText="§ → what it defines → type">
          <table className="map">
            <thead>
              <tr>
                <th>Section</th>
                <th>What it defines</th>
                <th>Implemented by</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>§2 Terminology</td>
                <td>
                  Bundle, concept, and the <strong>concept id</strong> — a file path with <code>.md</code> removed (
                  <code>tables/users.md</code> → <code>tables/users</code>).
                </td>
                <td>
                  <code>ConceptId</code>
                </td>
              </tr>
              <tr>
                <td>§3 Bundle structure</td>
                <td>
                  A directory tree of UTF-8 markdown; <code>index.md</code> and <code>log.md</code> are reserved
                  filenames.
                </td>
                <td>
                  <code>Bundle</code>
                </td>
              </tr>
              <tr>
                <td>§4 Concept documents</td>
                <td>
                  A concept: YAML <strong>frontmatter</strong> delimited by <code>---</code>, then a markdown body.
                  Reserved keys include <code>type</code>, <code>title</code>, <code>description</code>,{' '}
                  <code>timestamp</code>, <code>resource</code>, <code>tags</code>.
                </td>
                <td>
                  <code>OkfDocument</code>, <code>Frontmatter</code>
                </td>
              </tr>
              <tr>
                <td>§5 Cross-linking</td>
                <td>
                  Ordinary markdown links between concepts — <strong>absolute</strong> (<code>/tables/users.md</code>,
                  bundle-relative) or <strong>relative</strong> (<code>./other.md</code>). Backlinks are derived.
                </td>
                <td>
                  <code>LinkScanner</code>, <code>Bundle.LinksFrom</code> / <code>Backlinks</code>
                </td>
              </tr>
              <tr>
                <td>§6 Index files</td>
                <td>
                  <code>index.md</code> directory listings for <strong>progressive disclosure</strong>.
                </td>
                <td>
                  <code>IndexGenerator</code>
                </td>
              </tr>
              <tr>
                <td>§7 Log files</td>
                <td>
                  <code>log.md</code> date-grouped change history.
                </td>
                <td>
                  <code>ChangeLog</code>
                </td>
              </tr>
              <tr>
                <td>§8 Citations</td>
                <td>
                  Numbered <code>[n]</code> citations within a body.
                </td>
                <td>
                  <code>LinkScanner</code>, <code>OkfDocument.Citations()</code>
                </td>
              </tr>
              <tr>
                <td>§9 Conformance</td>
                <td>The one rule above, with severity-tagged diagnostics.</td>
                <td>
                  <code>BundleValidator</code>
                </td>
              </tr>
              <tr>
                <td>§11 Versioning</td>
                <td>
                  An optional <code>okf_version</code> declaring the spec version a bundle targets.
                </td>
                <td>
                  <code>Bundle.OkfVersion</code>, <code>OkfSpec.Version</code>
                </td>
              </tr>
            </tbody>
          </table>
        </Chapter>

        <Chapter id="reserved" title="Reserved files" refText="§6, §7 — not concepts">
          <p>
            Two filenames are structural, not concepts: <code>index.md</code> (generated listings) and{' '}
            <code>log.md</code> (change history). OKF4net surfaces them separately — <code>Bundle.IndexFiles</code>{' '}
            and <code>Bundle.LogFiles</code> — and neither counts toward <code>Bundle.Count</code>. Regenerate indexes
            with <code>IndexGenerator.RegenerateIndexes</code>; parse a log with <code>ChangeLog.Parse</code>.
          </p>
        </Chapter>

        <Chapter id="fidelity" title="Fidelity" refText="a faithful port">
          <p>
            Behaviour conforms to OKF v0.1. The document parser, validator, and index generator are faithful ports
            of the reference implementation — verified by tests adapted from the reference suite and, for the CLI,
            by <strong>byte-exact comparison</strong> against captured reference output. Any intentional divergence
            is documented with its reason; there are none that affect conformance.
          </p>
          <ul className="plain">
            <li>
              <strong>Permissive by construction.</strong> <code>Bundle.Load</code> collects parse failures in{' '}
              <code>ParseErrors</code> and retains broken links as graph edges to missing concepts — §9's
              tolerance, made structural.
            </li>
            <li>
              <strong>Frontmatter is preserved whole.</strong> The full ordered mapping round-trips byte for byte;
              typed getters are a view, not a projection (§4.1).
            </li>
            <li>
              <strong>A documented YAML subset.</strong> Scalars, sequences, shallow maps, block/flow,{' '}
              <code>|</code>/<code>&gt;</code> — anchors, tags, and multi-document streams are rejected with a clear
              error, since frontmatter never uses them.
            </li>
          </ul>
          <Next>
            → <Link to="/docs/library">library.md</Link> — the types up close ·{' '}
            <a href="https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md">
              the upstream spec ↗
            </a>
          </Next>
        </Chapter>
      </div>
    </DocsLayout>
  )
}
