// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import DocsLayout from '../../layouts/DocsLayout'
import { PageDoc, Chapter, MapTable, Tag, Next } from '../../components/doc'

/**
 * Port of `website/docs/spec.html` — the OKF v0.2 conformance rule, the
 * section-by-section spec → type mapping, reserved files, and fidelity.
 */
export default function Spec() {
  return (
    <DocsLayout
      title="Spec mapping — OKF4net docs"
      description="The Open Knowledge Format v0.2, section by section, mapped to the OKF4net types that implement it: concept ids (§2), bundles (§3), documents (§4), provenance/trust/lifecycle (§5), cross-linking (§6), the actor convention (§7), indexes (§8), logs (§9), attested computations (§10, not yet implemented), conformance (§11), versioning (§12), and the v0.1 legacy fallbacks (§13)."
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
            OKF v0.2, <em>section by section.</em>
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
        <Chapter id="one-rule" title="The one hard rule" refText="§11 — conformance">
          <blockquote>
            Every concept must carry a non-empty <code>type</code>. Everything else, a consumer must tolerate.
          </blockquote>
          <p>
            That is the entire conformance bar. Unknown types, unknown frontmatter keys, broken cross-links, and
            missing optional fields are all <strong>valid</strong> — a conformant consumer keeps going and reports,
            never rejects. OKF4net enforces exactly this in <code>BundleValidator</code> (§11) and, per document, in{' '}
            <code>OkfDocument.ValidateConformance()</code>; the stricter producer check (<code>type</code>,{' '}
            <code>title</code>, <code>description</code>, <code>timestamp</code>) is a separate, opt-in{' '}
            <code>Validate()</code>.
          </p>
        </Chapter>

        <Chapter id="whats-new" title="What's new in v0.2" refText="§5, §7, §13 — for readers who know v0.1">
          <p>
            v0.2 is a superset of v0.1: every v0.1 bundle keeps loading and validating unchanged. What's new is
            entirely additive, plus two sanctioned legacy fallbacks (§13.1) for the fields it supersedes:
          </p>
          <ul className="plain">
            <li>
              <strong>Provenance, trust, and lifecycle (§5).</strong> <code>sources</code> (per-source
              credibility signals), <code>generated</code>/<code>verified</code> stamps deriving a trust tier
              (unverified / machine-confirmed / human-reviewed), and <code>status</code>/<code>stale_after</code>{' '}
              for lifecycle.
            </li>
            <li>
              <strong>The actor convention (§7).</strong> <code>human:&lt;id&gt;</code>,{' '}
              <code>process:&lt;id&gt;</code>, or <code>&lt;producer&gt;/&lt;version&gt;</code> — trust
              classification keys off the <code>human:</code> prefix.
            </li>
            <li>
              <strong>Two breaking renames, both with a legacy fallback (§13.1).</strong>{' '}
              <code>timestamp</code> is superseded by <code>generated.at</code> (a bare <code>timestamp</code>{' '}
              is still read when <code>generated</code> is absent); the body <code>{'# Citations'}</code> list is
              superseded by frontmatter <code>sources</code> (still parsed as a fallback for v0.1 documents).
            </li>
          </ul>
        </Chapter>

        <Chapter id="mapping" title="Section by section" refText="§ → what it defines → type">
          <MapTable
            head={['Section', 'Implemented by']}
            rows={[
              [
                <>§2 Terminology</>,
                <>
                  <code>ConceptId</code> — bundle, concept, and the <strong>concept id</strong> (a file path with{' '}
                  <code>.md</code> removed).
                </>,
              ],
              [
                <>§3 Bundle structure</>,
                <><code>Bundle</code> — a directory tree of UTF-8 markdown; <code>index.md</code>/<code>log.md</code> reserved.</>,
              ],
              [
                <>§4 Concept documents</>,
                <><code>OkfDocument</code>, <code>Frontmatter</code> — YAML frontmatter delimited by <code>---</code>, then a markdown body.</>,
              ],
              [
                <>§5 Provenance, trust, and lifecycle</>,
                <>
                  <code>Frontmatter.Sources</code>/<code>Generated</code>/<code>Verified</code>/
                  <code>TrustTier</code>/<code>Status</code>/<code>StaleAfter</code>, and the{' '}
                  <code>Actor</code>/<code>Trust</code>/<code>Provenance</code>/<code>Lifecycle</code> value types.
                </>,
              ],
              [
                <>§6 Cross-linking and paths</>,
                <><code>LinkScanner</code>, <code>Bundle.LinksFrom</code>/<code>Backlinks</code> — absolute or relative markdown links; broken links tolerated.</>,
              ],
              [<>§7 Actor convention</>, <><code>Actor.Parse</code> — <code>human:</code>/<code>process:</code>/<code>&lt;producer&gt;/&lt;version&gt;</code>.</>],
              [<>§8 Index files</>, <><code>IndexGenerator</code> — <code>index.md</code> directory listings for progressive disclosure.</>],
              [<>§9 Log files</>, <><code>ChangeLog</code> — <code>log.md</code> date-grouped change history.</>],
              [
                <>§10 Attested computations</>,
                <>
                  <Tag>not yet implemented</Tag> — a new concept type in the spec; OKF4net loads and navigates a
                  bundle containing one without error, but has no dedicated logic for it yet.
                </>,
              ],
              [<>§11 Conformance</>, <><code>BundleValidator</code>, <code>OkfDocument.ValidateConformance()</code> — the one hard rule above.</>],
              [<>§12 Versioning</>, <><code>Bundle.OkfVersion</code>, <code>OkfSpec.Version</code> — optional <code>okf_version</code> declaration.</>],
              [
                <>§13 Changes from v0.1</>,
                <>
                  <code>Frontmatter.LastChangedAt</code> (falls back to legacy <code>timestamp</code>),{' '}
                  <code>OkfDocument.Sources()</code> (falls back to a legacy <code>{'# Citations'}</code> list) —
                  see "What's new in v0.2" above.
                </>,
              ],
            ]}
          />
        </Chapter>

        <Chapter id="reserved" title="Reserved files" refText="§8, §9 — not concepts">
          <p>
            Two filenames are structural, not concepts: <code>index.md</code> (generated listings) and{' '}
            <code>log.md</code> (change history). OKF4net surfaces them separately — <code>Bundle.IndexFiles</code>{' '}
            and <code>Bundle.LogFiles</code> — and neither counts toward <code>Bundle.Count</code>. Regenerate indexes
            with <code>IndexGenerator.RegenerateIndexes</code>; parse a log with <code>ChangeLog.Parse</code>.
          </p>
        </Chapter>

        <Chapter id="fidelity" title="Fidelity" refText="a faithful port">
          <p>
            Behaviour conforms to OKF v0.2. The document parser, validator, and index generator are faithful ports
            of the reference implementation — verified by tests adapted from the reference suite and, for the CLI,
            by <strong>byte-exact comparison</strong> against captured reference output. Any intentional divergence
            is documented with its reason; there are none that affect conformance.
          </p>
          <ul className="plain">
            <li>
              <strong>Permissive by construction.</strong> <code>Bundle.Load</code> collects parse failures in{' '}
              <code>ParseErrors</code> and retains broken links as graph edges to missing concepts — §11's
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
            <li>
              <strong>"Citations" isn't a section anymore.</strong> §13.1's legacy body <code>{'# Citations'}</code>{' '}
              fallback is what used to be v0.1's §8 — superseded by frontmatter <code>sources</code> (§5.1); a{' '}
              <code>references/</code> subdirectory (§6.3) is a naming convention for external material, not a
              requirement.
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
