// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import DocsLayout from '../../layouts/DocsLayout'
import { PageDoc, Chapter, MapTable, Next } from '../../components/doc'

// Whitespace-significant code sample (blank lines are part of the displayed
// code) mixing inline syntax-highlighting spans with literal text — kept as
// a verbatim HTML string per the technique established in Home.tsx, since
// JSX would collapse the blank lines. Sourced verbatim from
// website/docs/library.html:70-81.
const glanceHtml = `<span class="k">using</span> OKF4net;

<span class="k">var</span> bundle = Bundle.Load(<span class="s">"./my_bundle"</span>);           <span class="c">// §3 — permissive walk</span>
<span class="k">var</span> report = BundleValidator.Validate(bundle);      <span class="c">// §11 — diagnostics</span>
Console.WriteLine(report.IsConformant
    ? <span class="s">$"conformant with OKF v{OkfSpec.Version}"</span> : <span class="s">$"{report.ErrorCount} error(s)"</span>);

<span class="k">var</span> id = ConceptId.Parse(<span class="s">"tables/orders"</span>);        <span class="c">// §2</span>
<span class="k">foreach</span> (<span class="k">var</span> link <span class="k">in</span> bundle.LinksFrom(id))          <span class="c">// §6</span>
    Console.WriteLine(<span class="s">$"{id} -&gt; {link.Target} (exists: {link.Exists})"</span>);
<span class="k">foreach</span> (<span class="k">var</span> back <span class="k">in</span> bundle.Backlinks(id))
    Console.WriteLine(<span class="s">$"cited by {back}"</span>);`

/**
 * Port of `website/docs/library.html` — the full C# API surface grouped by
 * spec concern: Bundle, ConceptId, OkfDocument/Frontmatter, the YAML
 * subset, links, IndexGenerator/ChangeLog, validation, errors.
 */
export default function Library() {
  return (
    <DocsLayout
      title="Library reference — OKF4net docs"
      description="API reference for the OKF4net C# library: Bundle, ConceptId, OkfDocument, Frontmatter, the YAML subset, links, index and changelog generation, and §11 validation — grouped by spec concern."
      current="library"
    >
      <PageDoc
        path={
          <>
            docs/<b>library.md</b>
          </>
        }
        type="Reference"
        title={
          <>
            One type per <em>spec concern.</em>
          </>
        }
        lede={
          <>
            The <code>OKF4net</code> namespace mirrors the spec: <code>Bundle</code> (§3), <code>ConceptId</code>{' '}
            (§2), <code>OkfDocument</code>/<code>Frontmatter</code> (§4), links (§6, §13.1), <code>IndexGenerator</code>{' '}
            (§8), <code>ChangeLog</code> (§9), <code>BundleValidator</code> (§11). <strong>Zero third-party
            dependencies</strong> — the YAML subset and link scanner are the library's own.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="start" title="At a glance" refText="load · validate · traverse">
          <pre className="block" dangerouslySetInnerHTML={{ __html: glanceHtml }} />
        </Chapter>

        <Chapter id="bundle" title="Bundle" refText="§3 — a directory of concepts">
          <p>
            <code>Bundle.Load</code> walks the tree, parses every concept, and builds the cross-link graph. It is{' '}
            <strong>permissive</strong>: a bad file lands in <code>ParseErrors</code> and never aborts the load.
          </p>
          <MapTable
            head={['Member', 'Description']}
            rows={[
              [
                'Load(string root) → Bundle',
                <>
                  Walk a directory and build the graph. Throws <code>BundleLoadException</code> only when the root
                  itself is unreadable.
                </>,
              ],
              ['Root → string', "The bundle's root path."],
              [
                'Concepts → IReadOnlyList<Concept>',
                'All parsed concepts, in component-wise walk order.',
              ],
              ['Count → int · IsEmpty → bool', 'Number of concepts.'],
              [
                'Get(ConceptId) → Concept?',
                <>
                  Look up one concept; <code>null</code> if absent.
                </>,
              ],
              ['Contains(ConceptId) → bool', 'Whether the id resolves.'],
              [
                'LinksFrom(ConceptId) → IReadOnlyList<ResolvedLink>',
                <>
                  Outgoing links, each flagged <code>Exists</code>.
                </>,
              ],
              ['Backlinks(ConceptId) → IReadOnlyList<ConceptId>', 'Concepts that link to this one.'],
              ['BrokenLinks() → IReadOnlyList<(ConceptId, string)>', 'Every edge to a missing concept.'],
              [
                'IndexFiles · LogFiles → IReadOnlyList<string>',
                <>
                  Reserved <code>index.md</code> / <code>log.md</code> paths found.
                </>,
              ],
              [
                'ParseErrors → IReadOnlyList<(string Path, string Error)>',
                'Files that failed to parse, with why.',
              ],
              [
                'Concept',
                <>
                  Record: <code>Id</code>, <code>Path</code>, <code>Document</code>.
                </>,
              ],
            ]}
          />
        </Chapter>

        <Chapter id="conceptid" title="ConceptId" refText="§2 — id ↔ path">
          <p>A concept id is the file path with <code>.md</code> removed, as ordered segments. Sortable and value-equal.</p>
          <MapTable
            head={['Member', 'Description']}
            rows={[
              [
                'Parse(string) → ConceptId',
                <>
                  Parse <code>"tables/orders"</code>; throws <code>ConceptIdException</code> on an invalid segment.
                </>,
              ],
              ['TryParse(string, out ConceptId?) → bool', 'Non-throwing parse.'],
              ['FromPath(root, path) → ConceptId', 'Derive an id from a file path under a bundle root.'],
              ['ToPath(root) → string', 'Inverse: the .md file path under a root.'],
              ['Segments → IReadOnlyList<string>', 'The path components.'],
              ['Name → string · Parent → ConceptId?', "Last segment; id of the containing directory."],
              ['ValidateSegment(string)', 'Throw if a single segment is not spec-legal.'],
            ]}
          />
        </Chapter>

        <Chapter id="document" title="OkfDocument & Frontmatter" refText="§4 — one concept">
          <p>
            <code>Frontmatter</code> keeps the <strong>full ordered mapping</strong> and layers typed getters on top,
            so producer-defined keys survive round-trips. Two validation levels:{' '}
            <code>ValidateConformance()</code> enforces only §11 (non-empty <code>type</code>); <code>Validate()</code>{' '}
            is the stricter producer check (<code>type</code>, <code>title</code>, <code>description</code>,{' '}
            <code>timestamp</code>).
          </p>
          <MapTable
            head={['OkfDocument', 'Description']}
            rows={[
              [
                'Parse(string) → OkfDocument',
                <>
                  Parse frontmatter + body; throws <code>DocumentParseException</code>.
                </>,
              ],
              ['TryParse(string, out doc, out error) → bool', 'Non-throwing parse.'],
              ['Serialize() → string', 'Re-emit; preserves key order and unknown keys.'],
              [
                'Validate() · ValidateConformance()',
                <>
                  Producer check / §11 check; throw <code>DocumentValidationException</code>.
                </>,
              ],
              ['Links() → IReadOnlyList<ConceptLink>', 'Markdown links in the body.'],
              ['Citations() → IReadOnlyList<Citation>', 'Numbered citations in the body.'],
              ['Frontmatter → Frontmatter · Body → string', 'The two halves of the document.'],
            ]}
          />
          <MapTable
            head={['Frontmatter', 'Description']}
            rows={[
              ['Type · Title · Description · Resource · Timestamp → string?', 'Typed getters over the mapping.'],
              [
                'Tags → IReadOnlyList<string>',
                <>
                  The <code>tags</code> sequence, or empty.
                </>,
              ],
              ['ExtensionKeys → IReadOnlyList<string>', 'Producer-defined keys beyond the reserved set.'],
              ['AsMapping() → YamlMapping', 'The underlying ordered mapping.'],
              ['Set(string, YamlValue) · FromMapping(YamlMapping)', 'Mutate a key / wrap an existing mapping.'],
            ]}
          />
        </Chapter>

        <Chapter id="yaml" title="The YAML subset" refText="Yaml — frontmatter only">
          <p>
            A documented subset: scalars, sequences, shallow maps, block and flow styles, <code>|</code>/
            <code>&gt;</code> block scalars. It <strong>rejects anchors, tags, and multi-document streams</strong>{' '}
            with clear errors — frontmatter never needs them.
          </p>
          <MapTable
            head={['Member', 'Description']}
            rows={[
              [
                'YamlValue.Parse(string) → YamlValue',
                <>
                  Parse the subset; throws <code>YamlParseException</code> (with a line number).
                </>,
              ],
              ['YamlEmitter.Emit(YamlValue) → string', 'Serialize back to the same subset.'],
              [
                'AsString() · AsBool() · AsSequence() · AsMapping()',
                <>
                  Typed views; <code>null</code> if the node is another kind.
                </>,
              ],
              ['ToYamlString() → string', 'Emit a single value.'],
              ['YamlMapping: Get · ContainsKey · Entries · Keys · Count', 'Order-preserving map access.'],
            ]}
          />
        </Chapter>

        <Chapter id="links" title="Links & citations" refText="§6, §13.1 — the graph edges">
          <MapTable
            head={['Member', 'Description']}
            rows={[
              [
                'LinkScanner.ExtractLinks(body) → IReadOnlyList<ConceptLink>',
                <>
                  Markdown links, classified by <code>LinkKind</code>.
                </>,
              ],
              [
                'LinkScanner.ExtractCitations(body) → IReadOnlyList<Citation>',
                <>
                  Numbered <code>[n]</code> citations.
                </>,
              ],
              [
                'ConceptLink(Text, Target, Kind)',
                <>
                  A link; <code>Resolve(source) → ConceptId?</code> turns it into a target id.
                </>,
              ],
              [
                'ResolvedLink(Target, Exists, Text, Raw)',
                <>
                  A link resolved against the bundle — <code>Exists</code> tells you if the target is real.
                </>,
              ],
              ['Citation(Number, Text, Target, Raw)', 'One numbered citation.'],
              ['LinkKind', 'Enum: absolute vs relative link classification.'],
            ]}
          />
        </Chapter>

        <Chapter id="index" title="IndexGenerator & ChangeLog" refText="§8, §9 — reserved files">
          <MapTable
            head={['Member', 'Description']}
            rows={[
              [
                'IndexGenerator.RegenerateIndexes(root) → IReadOnlyList<string>',
                <>
                  Write every <code>index.md</code>; returns the paths written.
                </>,
              ],
              ['RegenerateIndexesWith(root, Synthesize)', 'Same, with a custom description synthesizer.'],
              ['BuildIndexText(entries) → string', 'Render one listing without touching disk.'],
              ['IndexEntry(Type, Title, Link, Description)', 'One row of a generated index.'],
              [
                'ChangeLog: Days · Title · ToMarkdown() · InvalidDates()',
                <>
                  Parse / render a <code>log.md</code> (§9); <code>IsIsoDate(s)</code> validates a date.
                </>,
              ],
              ['LogDay(Date, Entries) · LogEntry(Kind, Text)', "A day's block and one entry."],
            ]}
          />
        </Chapter>

        <Chapter id="validation" title="Validation" refText="§11 — conformance">
          <MapTable
            head={['Member', 'Description']}
            rows={[
              ['BundleValidator.Validate(Bundle) → ValidationReport', 'Run the §11 conformance check.'],
              [
                'ValidationReport.IsConformant → bool',
                <>
                  True when there are no <code>Error</code> diagnostics.
                </>,
              ],
              [
                'Diagnostics → IReadOnlyList<Diagnostic>',
                <>
                  Every finding; <code>Of(severity)</code> filters.
                </>,
              ],
              ['ErrorCount · WarningCount → int', 'Tallies by severity.'],
              [
                'Diagnostic(Severity, Path, Concept, Message)',
                <>
                  One finding; <code>ToString()</code> is the CLI line.
                </>,
              ],
              [
                'Severity',
                <>
                  Enum: <code>Error</code>, <code>Warning</code>, <code>Info</code>.
                </>,
              ],
              [
                'OkfSpec.Version → string',
                <>
                  The implemented spec version (<code>"0.2"</code>).
                </>,
              ],
            ]}
          />
        </Chapter>

        <Chapter id="errors" title="Errors" refText="one base exception">
          <p>
            Every library exception derives from <code>OkfException</code>, so one <code>catch</code> covers the
            surface. Loading is permissive, so most day-to-day work throws nothing — failures accumulate in{' '}
            <code>ParseErrors</code> instead.
          </p>
          <MapTable
            head={['Exception', 'Thrown by']}
            rows={[
              ['OkfException', 'Base type for all of the below.'],
              [
                'ConceptIdException',
                <>
                  <code>ConceptId.Parse</code> / <code>ValidateSegment</code>.
                </>,
              ],
              [
                'BundleLoadException',
                <>
                  <code>Bundle.Load</code>, when the root is unreadable.
                </>,
              ],
              [
                'DocumentParseException',
                <>
                  <code>OkfDocument.Parse</code>.
                </>,
              ],
              [
                'DocumentValidationException',
                <>
                  <code>Validate</code> / <code>ValidateConformance</code>.
                </>,
              ],
              [
                'YamlParseException',
                <>
                  The YAML subset parser (carries a <code>Line</code>).
                </>,
              ],
            ]}
          />
          <Next>
            → <Link to="/docs/cli">cli.md</Link> — the same engine as a binary · <Link to="/docs/getting-started">getting-started.md</Link>
          </Next>
        </Chapter>
      </div>
    </DocsLayout>
  )
}
