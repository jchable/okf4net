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
              [
                'Slugify(string) → string',
                'Derive a spec-legal segment from a free-form title, for a producer minting new concept ids.',
              ],
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
              [
                'OkfDocumentBuilder.ForType(string) → …→ Build() → OkfDocument',
                <>
                  A fluent, in-memory builder (<code>Title</code>/<code>Description</code>/<code>Resource</code>/<code>Tags</code>/<code>AddSource</code>/<code>Extension</code>/<code>Body</code>) for a producer
                  constructing a concept from scratch, without a serialize/re-parse round trip through YAML text.
                </>,
              ],
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
              [
                'Sources → IReadOnlyList<Source> · UsageWindow → UsageWindow?',
                <>
                  The §5.1 <code>sources</code> list and its shared sibling <code>usage_window</code>.
                </>,
              ],
              [
                'EffectiveUsageWindow(Source) → UsageWindow?',
                <>
                  The §5.1 window framing one entry's <code>usage_count</code>: that entry's own{' '}
                  <code>usage_window</code> if it has one, else the shared sibling — the entry wins whole-object,
                  never per-bound.
                </>,
              ],
              [
                'Generated → Stamp? · Verified → IReadOnlyList<Stamp>',
                <>
                  The §5.2 <code>generated</code>/<code>verified</code> stamps.
                </>,
              ],
              [
                'TrustTier → TrustTier',
                <>
                  Derived from <code>Verified</code> (§5.3) — see the{' '}
                  <a href="#provenance">provenance, trust &amp; lifecycle</a> chapter below.
                </>,
              ],
              [
                'Lifecycle → Lifecycle',
                <>
                  The §5.4/§5.5 <code>status</code>/<code>stale_after</code> pair, as one value.
                </>,
              ],
              [
                'GeneratedAt → string? · LastChangedAt → string?',
                <>
                  <code>Generated?.At</code>, and its §13.1 fallback to the legacy <code>Timestamp</code>.
                </>,
              ],
            ]}
          />
        </Chapter>

        <Chapter id="provenance" title="Provenance, trust &amp; lifecycle" refText="§5 — new in v0.2">
          <p>
            Five small, dependency-free value types, added for v0.2 and shared by the core, <code>Agents</code>,
            and <code>Catalog</code>. Every accessor is <strong>lenient</strong>: a malformed field yields a
            default or empty value rather than throwing — judgment is left entirely to{' '}
            <code>BundleValidator</code> (§11), never made at parse time.
          </p>
          <MapTable
            head={['Type', 'Description']}
            rows={[
              [
                'Actor(Raw, Kind, Id, Producer, Version, IsWellFormed)',
                <>
                  The §7 actor convention. <code>Actor.Parse(string)</code> reads{' '}
                  <code>human:&lt;id&gt;</code>/<code>process:&lt;id&gt;</code>/
                  <code>&lt;producer&gt;/&lt;version&gt;</code>, never throws; <code>IsHuman</code> drives trust.
                </>,
              ],
              [
                'Stamp(By, At) · TrustTier',
                <>
                  One <code>{'{ by, at }'}</code> stamp (§5.2), and the derived tier (§5.3):{' '}
                  <code>Unverified</code> / <code>MachineConfirmed</code> / <code>HumanReviewed</code> — human
                  iff any verifier's <code>By.IsHuman</code>.
                </>,
              ],
              [
                'Source(Id, Resource, Title, Author, UsageCount, LastModified, UsageWindow) · UsageWindow(From, To)',
                <>
                  One §5.1 <code>sources[]</code> entry, and the <code>usage_window</code> that frames{' '}
                  <code>usage_count</code> — shared as a sibling of <code>sources</code>, or carried by a single
                  entry as its own override. The override replaces the shared window whole, so read it through{' '}
                  <code>Frontmatter.EffectiveUsageWindow</code>.
                </>,
              ],
              [
                'Provenance.ToYaml(IEnumerable<Source>) → YamlSequence',
                <>
                  The serialize direction of <code>Frontmatter.Sources</code>' parse — for a producer building{' '}
                  <code>sources</code> from scratch rather than editing an existing document.
                </>,
              ],
              [
                'Lifecycle(Status, StatusIsKnown, StaleAfterRaw, StaleAfter) · ConceptStatus',
                <>
                  §5.4/§5.5. Absent <code>status</code> ⇒ <code>Stable</code>;{' '}
                  <code>IsStale(DateTimeOffset)</code> is <code>now &gt;= stale_after</code>. §5 makes{' '}
                  <code>stale_after</code> an absolute instant, so <code>StaleAfter</code> is a{' '}
                  <code>DateTimeOffset?</code>; the legacy date-only form is still read, normalized to
                  midnight UTC and flagged by <code>StaleAfterIsLegacyDate</code>.
                </>,
              ],
              [
                'StalePolicy(Mode, GraceDays)',
                <>
                  A consumer's policy for stale concepts: <code>Use</code> (admit everything, the default),{' '}
                  <code>Strict</code> (exclude), <code>Tolerate(graceDays)</code>.{' '}
                  <code>Admits(Lifecycle, DateTimeOffset)</code> is the one method both <code>Agents</code> and{' '}
                  <code>Catalog</code> call.
                </>,
              ],
              [
                'IOkfClock · SystemClock · FixedClock',
                <>
                  <code>DateTimeOffset Now</code> and <code>DateOnly Today</code>, injected wherever "now" matters.
                  Staleness compares <code>Now</code> (§5 makes <code>stale_after</code> an instant);{' '}
                  <code>Today</code> is for display, such as an audit report's stamp. <code>Now</code> is a default
                  interface member derived from <code>Today</code>, so a clock written before it existed still
                  compiles. <code>SystemClock</code> for real time, <code>FixedClock</code> to pin it — it takes
                  either a <code>DateTimeOffset</code> or a <code>DateOnly</code> (which pins midnight UTC). Every
                  API taking a clock (
                  <code>BundleValidator.Validate</code>, <code>ConceptAudit.Run</code>) exists so staleness (§5.5)
                  can be made reproducible, in your own code as much as in ours.
                </>,
              ],
            ]}
          />
        </Chapter>

        <Chapter id="shared" title="Shared with Agents &amp; Catalog" refText="one implementation, three consumers">
          <MapTable
            head={['Type', 'Description']}
            rows={[
              [
                'BundleConceptWriter',
                <>
                  Atomic, per-path-locked, reparse-guarded concept writes —{' '}
                  <code>WriteConcept</code>/<code>AppendToConceptAtomic</code>, plus a <code>Frontmatter</code>-typed{' '}
                  <code>WriteConcept</code> overload for a caller building a document programmatically (e.g. with{' '}
                  <code>OkfDocumentBuilder</code>), no YAML text round trip. The primitive behind{' '}
                  <code>okf_write_concept</code> and the scoped memory store; see{' '}
                  <Link to="/docs/agents">docs/agents.md</Link>.
                </>,
              ],
              [
                'ConceptSearch',
                <>
                  <code>Search(concepts, query, tag?)</code> — title ×3, tags/description ×2, body ×1. The one
                  scorer behind <code>okf_search</code> and the local catalog resolver; see{' '}
                  <Link to="/docs/catalog">docs/catalog.md</Link>.
                </>,
              ],
              [
                'ConceptAudit · AuditVocabulary',
                <>
                  <code>Run(bundle, query?, clock?)</code> — the corpus-level query over §5.3–§5.5 signals behind
                  both <Link to="/docs/cli">okf audit</Link> and the <code>okf_audit</code> tool. Counts describe
                  the whole bundle, findings describe the selection; <code>AuditVocabulary</code> is the one
                  spelling of the trust, status and freshness labels every surface renders.
                </>,
              ],
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
