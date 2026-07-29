// SPDX-License-Identifier: LGPL-3.0-or-later
import { Link } from 'react-router-dom'
import DocsLayout from '../../layouts/DocsLayout'
import { PageDoc, Chapter, MapTable, Warn, Next } from '../../components/doc'

// Whitespace-significant code samples (blank lines are part of the displayed
// code) mixing inline syntax-highlighting spans with literal text — kept as
// verbatim HTML strings per the technique established in Home.tsx/Mcp.tsx.
const manifestHtml = `{
  <span class="s">"version"</span>: <span class="s">1</span>,
  <span class="s">"sources"</span>: [
    { <span class="s">"id"</span>: <span class="s">"products"</span>, <span class="s">"path"</span>: <span class="s">"./bundles/products"</span>, <span class="s">"role"</span>: <span class="s">"knowledge"</span> },
    { <span class="s">"id"</span>: <span class="s">"mem-user"</span>, <span class="s">"path"</span>: <span class="s">"./memory/user"</span>, <span class="s">"role"</span>: <span class="s">"memory"</span>, <span class="s">"tier"</span>: <span class="s">"user"</span> }
  ]
}`

const wireUpHtml = `<span class="k">using</span> OKF4net.Catalog;
<span class="k">using</span> OKF4net.Catalog.Hosting;

services.AddKnowledge(o =&gt; o.AddCatalogFile(<span class="s">"./catalog.json"</span>));
services.AddMemory();

<span class="c">// Elsewhere, resolve and search:</span>
IKnowledgeResolver resolver = provider.GetRequiredService&lt;IKnowledgeResolver&gt;();
KnowledgeContext result = <span class="k">await</span> resolver.SearchAsync(<span class="k">new</span> KnowledgeQuery(<span class="s">"refund policy"</span>));`

/**
 * `/docs/catalog` — reference for `OKF4net.Catalog` and
 * `OKF4net.Catalog.Hosting`: the `catalog.json` manifest, `FileKnowledgeCatalog`,
 * the multi-source resolver, trust/staleness on results, scoped memory, and
 * DI wiring. This page did not exist before this task — the packages had
 * zero presence on the site. Every claim traces to a direct read of
 * `src/OKF4net.Catalog/*.cs` and `src/OKF4net.Catalog.Hosting/*.cs` (see the
 * audit in `docs/superpowers/specs/2026-07-28-website-v0.2-content-refresh-design.md`).
 */
export default function Catalog() {
  return (
    <DocsLayout
      title="Catalog — OKF4net docs"
      description="OKF4net.Catalog: a hot-reloadable catalog.json manifest naming one or more local OKF bundles as sources, a multi-source resolver, trust and staleness on every result, and a scoped memory store for host-scoped, multi-tenant agent deployments."
      current="catalog"
    >
      <PageDoc
        path={
          <>
            docs/<b>catalog.md</b>
          </>
        }
        type="Reference"
        title={
          <>
            Name your bundles once, <em>search them all.</em>
          </>
        }
        lede={
          <>
            <code>OKF4net.Catalog</code> names one or more local OKF bundles as <strong>sources</strong> in a
            hot-reloadable <code>catalog.json</code> manifest, then searches every enabled one. It's an
            OKF4net-specific manifest, not an OKF concept — it configures the catalog from the outside and
            isn't part of the OKF spec itself.
          </>
        }
      />

      <div className="docbody">
        <Chapter id="manifest" title="The catalog.json manifest" refText="strict, never-throw parser">
          <MapTable
            head={['Field', 'Default / rule']}
            rows={[
              ['id', 'required — a valid single concept-id segment, unique within the manifest'],
              ['path', 'required — resolved relative to the manifest directory, must stay inside the catalog root'],
              ['priority', '0 — higher-priority sources sort first within the grouped results'],
              ['enabled', "true — a disabled source's path is never resolved or checked against the filesystem"],
              ['role', '"knowledge" — or "memory"; any other string is rejected (IllegalRole)'],
              ['tier', 'required only when role is "memory": one of session, user, or tenant'],
            ]}
          />
          <pre className="block" dangerouslySetInnerHTML={{ __html: manifestHtml }} />
          <p>
            A malformed manifest never throws mid-parse — every problem (wrong version, empty sources, a
            duplicate or invalid id, an illegal role/tier combination, an embedded NUL, an absolute or
            out-of-root path, a reparse point in the path, …) comes back as a structured diagnostic instead.
          </p>
        </Chapter>

        <Chapter id="catalog" title="FileKnowledgeCatalog" refText="fail-fast load, errors-as-data reload">
          <p>
            Construction is <strong>fail-fast</strong>: an invalid initial manifest throws
            (<code>CatalogException</code>) rather than publishing a partial or empty catalog — a caller like a
            DI container at startup never gets a silently broken state. Every reload after that first
            successful load is <strong>errors-as-data</strong>: <code>ReloadAsync()</code> re-reads and
            re-validates a whole new snapshot before ever touching the live one, and only swaps it in if{' '}
            <em>every</em> enabled source's path is still valid — one bad source rejects the entire reload, not
            just that source, and the previous good snapshot keeps serving. A monotonic{' '}
            <code>Generation</code> counter increments only on a successful swap, so a caller can tell whether a
            search reflects the latest reload. A debounced (250ms default) file watcher on the manifest file
            itself triggers reloads automatically, but it's best-effort — <code>ReloadAsync()</code> is the only
            path with a delivery guarantee.
          </p>
        </Chapter>

        <Chapter id="resolver" title="The multi-source resolver" refText="grouped by source, no fusion (V1)">
          <p>
            <code>DefaultKnowledgeResolver</code> searches every enabled <code>Knowledge</code>-role source
            (sources with <code>role: "memory"</code> are never searched here — they feed the memory store
            instead) and returns results <strong>grouped by source</strong> — sorted by source priority, then
            by score within each source. There is no cross-source fusion, score normalization, or
            deduplication into one merged ranking in this version; a caller that wants that has to do it
            itself. A <code>StalePolicy</code> on the query (<code>Use</code> by default — admit everything) is
            applied once, across the combined result set, after every source has been searched.
          </p>
        </Chapter>

        <Chapter id="trust" title="Trust & staleness" refText="§5 — TrustTier, Lifecycle, StalePolicy">
          <p>
            Every <code>KnowledgePassage</code> carries the matching concept's <code>TrustTier</code> (default{' '}
            <code>Unverified</code>) and full <code>Lifecycle</code> (<code>Status</code>,{' '}
            <code>StaleAfter</code>), read straight off its frontmatter — a host can filter or render
            provenance without re-parsing anything. Staleness is a method, not a stored flag:{' '}
            <code>Lifecycle.IsStale(today)</code>. <code>StalePolicy</code> has three modes: <code>Use</code>{' '}
            (admit everything, the default — never a silent drop), <code>Strict</code> (exclude anything
            stale), and <code>Tolerate(graceDays)</code> (admit until <code>stale_after + graceDays</code>).
          </p>
        </Chapter>

        <Chapter id="memory" title="Scoped memory (role: memory)" refText="session → user → tenant">
          <p>
            A <code>role: "memory"</code> source is written by capture (an agent's{' '}
            <Link to="/docs/agents">context provider</Link>), never searched by the resolver — it feeds an{' '}
            <code>IMemoryStore</code> instead, via <code>FileMemoryStore</code>. All three tiers —{' '}
            <code>Session</code>, <code>User</code>, <code>Tenant</code> — are backed by durable storage, read
            in most-specific-first order (session → user → tenant) so a host can layer per-session scratch
            memory over durable per-user and per-tenant memory. Each present scope segment is path-encoded as
            <code>{'{lowercased}'}-{'{hash}'}</code> (a truncated SHA-256 of the case-sensitive raw value), so
            case-variant tenant or user ids never collide on a case-insensitive filesystem. RGPD/audit
            needs are covered by <code>DeleteScopeAsync</code> and <code>EnumerateAsync</code> — both
            errors-as-data, never throwing on an expected filesystem condition.
          </p>
        </Chapter>

        <Chapter id="hosting" title="Hosting (AddKnowledge / AddMemory)" refText="the one Microsoft.Extensions.* dependency">
          <p>
            <code>OKF4net.Catalog.Hosting</code> is the sole project in the dependency graph allowed a{' '}
            <code>Microsoft.Extensions.*</code> package — <code>AddKnowledge</code> registers{' '}
            <code>IKnowledgeCatalog</code>/<code>IKnowledgeResolver</code> lazily (no file I/O until the first
            resolve), and <code>AddMemory</code> registers <code>IMemoryStore</code> from whichever{' '}
            <code>role:memory</code> sources the manifest declares.
          </p>
          <pre className="block" dangerouslySetInnerHTML={{ __html: wireUpHtml }} />
          <Warn title="ADDMEMORY IS FROZEN AT STARTUP">
            <p>
              <code>AddMemory</code> resolves the set of <code>role:memory</code> sources once, at the first{' '}
              <code>IMemoryStore</code> resolution from the container. A later catalog{' '}
              <code>ReloadAsync()</code> does <strong>not</strong> pick up a memory source added, removed, or
              edited afterward — that requires rebuilding the DI container. The knowledge resolver has no such
              limit: it re-reads the live catalog on every search.
            </p>
          </Warn>
        </Chapter>

        <Next>
          → <Link to="/docs/agents">docs/agents.md</Link> — the context provider that reads and writes through
          this catalog
        </Next>
      </div>
    </DocsLayout>
  )
}
