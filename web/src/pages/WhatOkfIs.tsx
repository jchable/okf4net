// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import Layout from '../layouts/Layout'
import { PageDoc, Chapter, ConceptGrid, Cell, Term, MapTable, Next } from '../components/doc'

/**
 * Port of `website/what-okf-is.html` — terminology, reserved files,
 * conformance, and the spec-section → implementation mapping table.
 */
export default function WhatOkfIs() {
  return (
    <Layout
      title="What OKF is — OKF4net"
      description="The Open Knowledge Format in one page: bundles, concepts, cross-links, reserved files, conformance — and how each spec section maps to an OKF4net type."
      current="what-okf-is"
    >
      <PageDoc
        path={
          <>
            my_bundle/<b>what-okf-is.md</b>
          </>
        }
        type="Guide"
        title={
          <>
            A knowledge format you can <em>cat.</em>
          </>
        }
        lede={
          <>
            The <a href="https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md">Open Knowledge Format v0.2</a>{' '}
            is Google's open, human- and agent-friendly format for representing knowledge as{' '}
            <strong>a directory of markdown files with YAML frontmatter</strong>. It is intentionally minimal — and
            OKF4net implements all of it bar the new §10 attested-computations concept — see the{' '}
            <Link to="/docs/spec">spec mapping</Link>.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="terms" title="Terminology" refText="§2–§3 — bundle, concept, id">
          <ConceptGrid>
            <Cell>
              <Term>bundle</Term>
              <p>
                A directory tree of UTF-8 markdown files — the unit of distribution. Ship it with{' '}
                <code>git clone</code>, read it with <code>cat</code>.
              </p>
            </Cell>
            <Cell>
              <Term>concept</Term>
              <p>
                One markdown document: a YAML frontmatter block delimited by <code>---</code>, followed by a
                markdown body.
              </p>
            </Cell>
            <Cell>
              <Term>concept id</Term>
              <p>
                The file's path within the bundle with <code>.md</code> removed: <code>tables/users.md</code> →{' '}
                <code>tables/users</code>.
              </p>
            </Cell>
            <Cell>
              <Term>cross-links</Term>
              <p>
                Concepts link via ordinary markdown links — absolute (<code>/tables/users.md</code>,
                bundle-relative) or relative (<code>./other.md</code>). Backlinks are derived.
              </p>
            </Cell>
          </ConceptGrid>
        </Chapter>

        <Chapter id="reserved" title="Reserved files" refText="§8–§9 — index.md, log.md">
          <p>Two filenames are reserved in every directory of a bundle:</p>
          <ul className="plain">
            <li>
              <strong>
                <code>index.md</code>
              </strong>{' '}
              — a generated directory listing, for <strong>progressive disclosure</strong>: an agent (or a human)
              reads the index first and descends only into what looks relevant.
            </li>
            <li>
              <strong>
                <code>log.md</code>
              </strong>{' '}
              — a date-grouped change history for the directory's concepts.
            </li>
          </ul>
          <p>
            OKF4net generates both: <code>IndexGenerator</code> (re)builds every <code>index.md</code>,{' '}
            <code>ChangeLog</code> parses and builds <code>log.md</code> histories — verified byte-exact against the
            reference implementation's output.
          </p>
        </Chapter>

        <Chapter id="conformance" title="Conformance is one rule" refText="§11 — permissive by design">
          <blockquote>A concept is conformant if it has a non-empty <code>type</code>. That's it.</blockquote>
          <p>
            Everything else is deliberately permissive: consumers must tolerate unknown types, unknown frontmatter
            keys, broken links, and missing optional fields. This is what lets independently produced bundles
            interoperate.
          </p>
          <p>
            OKF4net honors both sides of that bargain. <code>OkfDocument.ValidateConformance()</code> enforces only
            the §11 rule; <code>OkfDocument.Validate()</code> matches the stricter producer-side check from the
            reference agent (<code>type</code>, <code>title</code>, <code>description</code>, <code>timestamp</code>
            ). And <code>Bundle.Load</code> never aborts on a bad file — it collects parse failures and keeps going.
          </p>
        </Chapter>

        <Chapter id="mapping" title="Spec → implementation" refText="§2–§13, section by section">
          <p>
            The library mirrors the spec's structure, so a spec citation in an issue or a PR points straight at the
            responsible type:
          </p>
          <MapTable
            head={['Spec section', 'Implemented by']}
            rows={[
              [
                '§2 Terminology / concept id',
                <code key="c2">OKF4net.ConceptId</code>,
              ],
              [
                '§3 Bundle structure',
                <>
                  <code>OKF4net.Bundle</code>, <code>Bundle.ReservedFilenames</code>
                </>,
              ],
              [
                '§4 Concept documents',
                <>
                  <code>OKF4net.OkfDocument</code>, <code>OKF4net.Frontmatter</code>
                </>,
              ],
              [
                '§5 Provenance, trust, and lifecycle',
                <>
                  <code>Frontmatter.Sources</code>/<code>Generated</code>/<code>Verified</code>/
                  <code>TrustTier</code>/<code>Status</code>/<code>StaleAfter</code>, and the{' '}
                  <code>OKF4net.Actor</code>/<code>Trust</code>/<code>Provenance</code>/<code>Lifecycle</code> value
                  types
                </>,
              ],
              [
                '§6 Cross-linking',
                <>
                  <code>OKF4net.LinkScanner</code>, <code>Bundle.LinksFrom</code> / <code>Bundle.Backlinks</code>
                </>,
              ],
              [
                '§7 Actor convention',
                <>
                  <code>OKF4net.Actor.Parse</code> — <code>human:</code>/<code>process:</code>/
                  <code>&lt;producer&gt;/&lt;version&gt;</code>
                </>,
              ],
              ['§8 Index files', <code key="c8">OKF4net.IndexGenerator</code>],
              ['§9 Log files', <code key="c9">OKF4net.ChangeLog</code>],
              ['§11 Conformance', <code key="c11">OKF4net.BundleValidator</code>],
              [
                '§12 Versioning',
                <>
                  <code>Bundle.OkfVersion</code>, <code>OKF4net.OkfSpec.Version</code>
                </>,
              ],
              [
                '§13.1 Citations (legacy fallback)',
                <>
                  <code>LinkScanner</code>, <code>OkfDocument.Citations()</code>
                </>,
              ],
            ]}
          />
          <Next>
            → <Link to="/library">library.md</Link> — how to use those types, with examples
          </Next>
        </Chapter>
      </div>
    </Layout>
  )
}
