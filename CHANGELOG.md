# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`FixedClock`** — an `IOkfClock` pinned to one instant, alongside `SystemClock`.
  Every API taking a clock (`BundleValidator.Validate`, `ConceptAudit.Run`)
  exists to make staleness (§5.5) reproducible; until now each caller wanting
  that had to write the same four-line type, and three copies of it had
  accumulated inside this repo alone.
- **`okf audit`** — a corpus-level query over a bundle's trust (§5.3), lifecycle
  (§5.4) and staleness (§5.5) signals: counts plus a filterable worklist, with
  `--stale`, `--trust`, `--status`, `--type`, `--as-of` and `--json`. Backed by
  the new `ConceptAudit` in the core library and exposed to agents as the
  read-only `okf_audit` tool.
- **`okf render <bundle> --out <dir>`** generates a self-contained, browsable
  HTML site from a bundle: one page per concept (frontmatter table + rendered
  body), a generated index, navigable cross-links with broken links flagged,
  and backlinks. Backed by the new zero-dependency `OKF4net.Viewer` project.
  Markdown renders client-side via a vendored copy of marked (MIT); raw HTML
  is neutralized by sanitizing the parsed DOM in `viewer.js` (element
  allowlist, per-tag attribute allowlist, URL-scheme validation) rather than
  by patching marked's renderer hooks, which cannot bound the attack surface
  in general (see `CLAUDE.md`). GFM task list items survive sanitization as
  real `<input type="checkbox" disabled>` elements with correct checked
  state, so a screen reader announces them as checkboxes rather than as
  decorative text. No full-text search yet — that lands with the planned
  `okf serve` companion.
- **A `sources[]` entry can now carry its own `usage_window` override
  (§5.1).** `Provenance.ParseSources` reads a per-entry `usage_window`
  through the same `ParseUsageWindow` the shared, top-level one already
  used, `Validate` checks its bounds through the same `CheckTemporal` machinery
  as the other §5 timestamp keys (reusing `UsageWindowInvalidFrom`/
  `UsageWindowInvalidTo`, with `Diagnostic.Field` telling the two positions
  apart — `sources.usage_window.from`/`.to` vs. `usage_window.from`/`.to`; no
  new `DiagnosticCode`), and the new `Frontmatter.EffectiveUsageWindow(Source)`
  resolves the two into the one value a consumer actually wants. This closes
  **S5.1-3**, tracked as "Missing" in
  `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md:204`.

  This was never a data-loss bug: `okf fmt` already round-tripped a per-entry
  `usage_window` before this change, because `Frontmatter` re-serializes an
  order-preserving `YamlMapping` and the lossy path, `Provenance.ToYaml`, has
  exactly one caller — the producer builder — which `fmt` never goes through.
  The gap was that the library could not *see* the value: no typed access, no
  §5 validation, no way for a consumer to obtain it. `Source` gains an
  optional `UsageWindow?` member and `OkfDocumentBuilder.AddSource` a matching
  optional parameter, so the field is also writable through the one supported
  producer API.

  The override is **whole-object, not per-field**: an entry writing
  `usage_window: { from: X }` yields a window whose `to` is `null` — it does
  not inherit the shared sibling's `to`. §5.1 (`SPEC.md:332-334`) says an
  entry MAY carry its own `usage_window` "to override the shared one" and
  stops there — a per-field merge would be inventing a rule the spec does not
  state, so this is a deliberate interpretation on our part, not something
  the spec itself settles.

### Changed

- **Breaking (0.x): `Source`'s constructor and `Deconstruct` arity changed.**
  The `UsageWindow?` member above is appended last with a default, so every
  in-repo construction site (positional or named) still compiles unchanged —
  but the shape is not binary-compatible for anything built against the
  previous six-member `Source`. Consistent with this release's other 0.x
  breaks (see `Lifecycle.StaleAfter` below).

- **Breaking (0.x): staleness is compared on instants, not dates.**
  `Lifecycle.StaleAfter` is now a `DateTimeOffset?` rather than a `DateOnly?`,
  and `Lifecycle.IsStale` / `StalePolicy.Admits` take a `DateTimeOffset` — the
  `DateOnly` overloads are **not** kept. Two comparison semantics for one
  question is a footgun: a `DateOnly` caller silently gets midnight-UTC
  semantics and can read a concept as fresh for up to ~24h after it went stale.
  Callers thread a `DateTimeOffset` (typically `IOkfClock.Now`); code that
  rendered `StaleAfter` as a date uses the new `Lifecycle.StaleAfterDate`.
  `AuditReport.AsOf` stays a `DateOnly` — it is the report's display stamp, not
  the comparison input — so `okf audit --json` is unaffected. The mirror
  consequence applies to `StaleMode.Tolerate`, which measures grace from the
  parsed instant: a date-only `stale_after` now anchors that grace at midnight
  UTC, so `Tolerate(n)` admits the concept for up to ~24h less than the previous
  day-granular comparison did (`Tolerate(1)` on `stale_after: 2026-01-01` now
  ends at `2026-01-02T00:00:00Z`, where it used to cover all of 2026-01-02).
- `IOkfClock` gains `Now` (a `DateTimeOffset`) as a **default interface member**
  derived from `Today`, so existing implementers that define only `Today` keep
  compiling and working. `FixedClock` gains a `DateTimeOffset` constructor
  beside the `DateOnly` one; note that a target-typed `new FixedClock(new(y, m,
  d))` is now ambiguous and must name the type (`new DateOnly(y, m, d)`).
- **`Lifecycle`'s rewritten parser widened only where §5 required it.** Teaching
  `stale_after` to read instants (see *Fixed* below) meant choosing what the new
  zoneless fallback would accept. It reads an explicit ISO format list rather
  than `DateTime.TryParse`, which *would* have started accepting `01/02/2026` or
  a bare year — values the previous `DateOnly.TryParseExact` parser already
  reported as malformed and which stay malformed. Recorded because widening
  "malformed" into "legacy, assumed UTC" was the silent, easy way to write that
  rewrite, and this notes it was not taken; nothing regressed here.
- **`ConceptId.FromPath`'s "not under bundle root" error now names the root, and
  quotes both paths.** It previously reported only the offending path, leaving a
  caller deriving ids against several bundles to guess which root rejected it.
  Both the path and the root now go through the same `DebugQuote` treatment every
  other `ConceptIdException` message uses, so a path containing spaces stays
  unambiguous and one containing control characters cannot inject line breaks
  into the message.
- **`okf audit --json` spells trust tiers one way.** The counts object used
  camelCase property names (`humanReviewed`) while `findings[].trust` and
  `query.trust[]` used the vocabulary's own hyphenated names, so
  `counts[finding.trust]` did not resolve. The counts object now uses
  `unverified` / `machine-confirmed` / `human-reviewed`, in that ladder order.
  Done before `okf audit` appears in any release, while the schema is still
  free to move.
- **`okf validate --json` now reports `asOf`**, the date its §5.5 staleness
  warning was evaluated against — without it, an archived CI report could not
  be told apart from an unpinned run, which is what `--as-of` exists to fix.
- **`okf_audit`'s `stale` parameter is now unset by default** rather than
  `true`, and follows the CLI's rule: the stale worklist when no other filter
  is given, no staleness constraint once one is. Asking an agent "which
  concepts were never verified by a human?" previously meant "…and are also
  stale", and answered "none" whenever the unverified concept simply had no
  `stale_after`. An explicit `stale` still wins.
- **A blank `--type` (or `type:` on the tool) is now "no type filter"** rather
  than a filter for the empty string, which §11 forbids a concept from carrying
  and which could therefore only ever select nothing.
- **The `--` separator now applies to every verb, not just to the positional
  lookup.** Argument presence, flag values and the positional were three
  independent scans of the raw argument array, and only the last honoured `--`;
  a flag written after the separator was still obeyed. They are now one scan, so
  everything after `--` is positional on every verb — which is what the
  separator has always been documented to mean. Concretely: `okf fmt -- file -w`
  no longer rewrites the file in place (`-w` is a filename there, not a flag),
  and the same applies to `--json`, `--dot` and `--out` written after a
  separator. The well-formed spellings (`okf fmt file -w`, `okf fmt -w file`)
  are unaffected. The same rewrite also fixes a token consumed as a flag's value
  still counting as a flag: `okf audit b --type --stale` no longer sets the
  stale filter.
- **`okf validate` gains `--as-of <YYYY-MM-DD>`**, pinning the date its §5.5
  staleness warning is evaluated against. `BundleValidator.Validate` already
  accepted a clock, but the verb exposed no way to set one, so its
  `concept is stale` warning depended on the day it ran and could not be
  asserted in CI. Default behaviour is unchanged.
- **winget manifests move to schema 1.12.0** (from the now-deprecated 1.6.0).
  winget-pkgs' automated reviewer flags older schemas, and an unresolved flag
  of that kind blocked the first submission
  ([winget-pkgs#409311](https://github.com/microsoft/winget-pkgs/pull/409311))
  from merging. The generated manifests pass `winget validate` unchanged
  otherwise. The package description also stops advertising OKF v0.1.
- `release.yml` gains a `winget-submit` job that opens the winget-pkgs update
  PR automatically on each tag (`winget-releaser`). It skips with a notice
  unless a `WINGET_TOKEN` secret is configured, so releases stay green until
  the package is published and the token/fork exist — see
  `packaging/winget/README.md`.

### Fixed

- **`stale_after` now reads the spec-conformant timestamp form.** OKF v0.2 §5
  requires every timestamp-valued key to be an ISO 8601 datetime with an
  explicit UTC offset (`2026-06-30T14:00:00Z`). `Lifecycle` previously parsed
  `stale_after` only as a bare `YYYY-MM-DD`, so a conformant value was reported
  as malformed and **staleness was never computed for it** — a concept past its
  expiry silently read as fresh, in `okf audit`, `okf validate`, the agent
  tools, the catalog resolvers and §10.6's attestation gate alike. The legacy
  date-only form is still accepted and now raises a `LegacyDateOnlyTimestamp`
  warning, matching how the §13.1 legacy fields are handled. The same warning
  covers `generated.at` and `verified[].at`. A datetime with no offset is read
  as UTC and flagged the same way.
- **`sources[].last_modified` and `usage_window.from`/`.to` no longer reject the
  conformant form.** §5.1 makes `last_modified` a timestamp-valued key and
  `usage_window` a "`{ from, to }` datetime range", so §5's rule covers all
  three — but they were checked against `YYYY-MM-DD`, so a spec-conformant
  `2026-06-30T14:00:00Z` was reported *invalid*. This is the mirror of the
  `stale_after` bug and the more damaging half: rather than missing a signal, it
  told producers their correct data was wrong and pushed them toward the legacy
  form. All three now accept the §5 form silently, warn
  `LegacyDateOnlyTimestamp` on the date-only one, and keep their existing
  `SourceInvalidLastModified` / `UsageWindowInvalid*` codes for values that are
  not timestamps at all. §9 `log.md` date headings are **unchanged** — §9 pins
  those to bare `YYYY-MM-DD`, and `ChangeLog.IsIsoDate` still backs them.
- **A §5 timestamp that carries an explicit UTC offset but is not spelled ISO
  8601 now warns.** Once the two fixes above routed all six §5 keys through
  the shared `OkfTimestamp` parser, the conformance decision was made by a
  permissive `DateTimeOffset.TryParse`: `2026-6-3T14:00:00Z` (unpadded
  month/day), a lowercase `z` designator, and a basic-format offset (`+0200`
  instead of `+02:00`) all parsed successfully and passed with no diagnostic
  at all, across `generated.at`, `verified[].at`, `sources[].last_modified`,
  `usage_window.from`/`.to` and `stale_after`. The grammar is now checked
  against the exact spelling ISO 8601 requires — fixed component widths, an
  uppercase `Z`, no mixing of basic and extended offset forms, and no negative
  zero offset (`-00:00` and `-00` are RFC 3339 spellings that ISO 8601 forbids,
  and the spec cites no RFC; `Z` and `+00:00` are the conformant ones) —
  verified against every timestamp literal `docs/spec/SPEC.md` itself writes, so
  it cannot reject a spelling the spec uses. Still read as the parsed instant
  either way (§11); only the spelling now raises a new `NonIso8601Timestamp`
  warning. `stale_after` now shares the same `CheckTemporal` check as the
  other five keys rather than a separate path, so a spelling cannot be
  conformant in one field and not another.
- **A value that is not a timestamp at all is no longer told it is "not an
  ISO-8601 datetime".** That claim is false of a whole class of value: the
  readability gate is `DateTimeOffset.TryParse`, which cannot read several
  genuine ISO 8601 datetimes carrying an explicit UTC offset — the wholly-basic
  `20200630T140000Z`, a leap second (`…T23:59:60Z`), a week date
  (`2026-W27-1T…`), an ordinal date (`2026-181T…`). They are not
  read (so they are never evaluated for staleness), and the diagnostic now says
  only that: `<label> could not be read as a timestamp: "<value>"`. The
  `DiagnosticCode` for each field is unchanged, so `--json` consumers matching
  on `code` are unaffected; only the rendered message moved, and no golden
  captured it.

- The CLI's `--version` is now checked against `<Version>` in
  `Directory.Build.props` by a test. The two are maintained separately and had
  drifted: the 0.2.0 winget package shipped a binary printing
  `okf 0.1.0-alpha.1`, which the previous test did not catch (it only asserted
  the `okf ` prefix).

## [0.5.0] - 2026-07-31

### Added

- **`okf validate`/`okf info` gain a `--json` flag** for machine-readable
  output (camelCase, source-generated for Native AOT). `Diagnostic` gains
  a stable `Code` (`DiagnosticCode`, one per distinct validator finding)
  and a `Field` naming the frontmatter key involved -- `ToString()`'s text
  output is unchanged (every golden CLI fixture stays byte-exact).
- `Provenance.ToYaml`, `ConceptId.Slugify`, a `Frontmatter`-typed `BundleConceptWriter.WriteConcept`
  overload, and `OkfDocumentBuilder`: producer-facing API for constructing and writing an OKF concept
  entirely in memory, without a serialize/re-parse round trip through YAML text. Motivated by the
  upcoming native OKF producer (`producers/`), usable independently by any programmatic caller.
- **`producers/OkfProducer` walking skeleton**: a native OKF producer CLI (`generate`/`validate`,
  System.CommandLine + Generic Host) that scans a repository (`RepositoryScanner`: npm/NuGet
  manifests, README) and generates an OKF v0.2 bundle from it (`ConceptGenerator` +
  `BundleWriter`, built on `OkfDocumentBuilder`). Standalone solution (`producers/OkfProducer.sln`),
  not part of `OKF4net.sln`/CI and not published to NuGet — same status as `samples/`. First
  ecosystem slice only (npm/NuGet/README detection); more ecosystems and CI coverage are open
  follow-ups, see `ROADMAP.md`.
- **`samples/catalog-explorer`**, a new `OKF4net.Catalog` sample covering five scenarios: load &
  inspect, multi-source search, ranking strategies (`Grouped`/`Merged`/`PriorityWeighted`),
  per-caller source visibility, and the `role: memory` tier. Exercised against a second vendored
  sample bundle, `bundles/ga4` (from the upstream OKF reference bundles), alongside the existing
  `bundles/acme_retail`.

### Changed

- **Breaking: `Diagnostic`'s constructor gains a required `Code` parameter**
  (`DiagnosticCode`, before the existing optional `Field`). Source- and
  binary-breaking for any code that constructs or deconstructs `Diagnostic`
  directly; nothing in this repository does.
- **Breaking: `okf validate` now correctly reports non-conformance (§11)
  for malformed reserved files.** Previously a malformed `index.md`/`log.md`
  (bad structure, or unreadable/unparseable) was under-reported as
  `Warning` or produced no diagnostic at all, so `okf validate` incorrectly
  exited `0`; it now exits `1` for these cases, as §11 conformance already
  requires. Two new `DiagnosticCode` values, `UnparseableIndex` and
  `UnparseableLog`, cover the previously-silent case. The same applies to
  library callers of `BundleValidator.Validate` (these three diagnostic
  codes move from `Warning` to `Error`: `IndexHasFrontmatter`,
  `RootIndexExtraFrontmatter`, `LogDateInvalid` -- changing
  `ValidationReport.IsConformant`/`ErrorCount`/`WarningCount`) and to the
  `okf_validate_bundle` MCP tool's verdict. Widest practical impact:
  `ChangeLog.Parse` treats every `##` line in a `log.md` as a date
  heading (it does not distinguish a date from a section heading), so a
  `log.md` containing any non-date `##` line (e.g. `## Notes`, a manually
  added subsection) now fails conformance -- previously this was silent.

### Fixed

- **The YAML frontmatter parser now supports multi-line (folded) plain
  scalars.** A `key: value` entry whose value continues onto one or more
  subsequent, more-indented lines (valid YAML, and how the upstream OKF
  `reference_agent` generator writes long `description:` fields) previously
  threw `unexpected indentation in mapping`/`...in sequence` — the parser
  only ever read a value from its own line. Continuation lines now fold in
  per YAML's plain-scalar rule (non-blank runs join with a single space, a
  blank or comment-only line becomes a paragraph break), for both mapping
  values and sequence items. This was found to break most of the OKF
  reference implementation's own sample bundles: of the four upstream
  bundles at `GoogleCloudPlatform/knowledge-catalog` commit `3fcbb9f8`,
  only `acme_retail` (already vendored in this repo) validated cleanly —
  `ga4`, `crypto_bitcoin`, and `stackoverflow` failed to parse 7/9, 7/9, and
  15/26 of their concepts respectively before this fix; all three now
  validate with 0 errors.
- **`IndexGenerator.RegenerateIndexesWith` no longer erases the bundle-root
  `index.md`'s `okf_version` marker.** Regeneration rebuilt every `index.md`
  from scratch — entries only, no frontmatter block at all — so a bundle
  marked with `okf_version` (§12) lost that marker the moment any concept
  write triggered `okf_regenerate_indexes`. That silently broke `okf-mcp`'s
  bundle auto-discovery on the next server start (`no bundle root given and
  no marked bundle found`), even though the bundle had been correctly marked
  and previously discovered fine. The write path now preserves the root
  `index.md`'s existing frontmatter (read permissively — a file that fails
  to read or parse is left untouched rather than silently rewritten) and
  only regenerates the body; non-root `index.md` files are unaffected and
  still self-heal any stray frontmatter (§8) on the next regeneration, as
  before.
- **`OKF4net.Catalog` no longer silently drops a source directory that
  merely shares a case-insensitive spelling with another.** On a
  genuinely case-sensitive volume, `CatalogPathResolver`'s
  `OrdinalIgnoreCase` dedup (chosen by an OS heuristic) wrongly collapsed
  two distinct source directories differing only in case, and the second
  was dropped from every search with no diagnostic. Deduping now uses
  `Ordinal` comparison, and a new `KnowledgeDiagnosticCode.DuplicateDirectory`
  reports any actual directory collision by source id instead of dropping
  it without a trace.

## [0.4.0] - 2026-07-30

### Added

- **`okf-mcp` bundle auto-discovery.** When neither a positional root nor
  `OKF_BUNDLE_ROOT` is given, `okf-mcp` now walks up from the current working
  directory looking for a *marked* bundle (a root `index.md` whose
  frontmatter declares `okf_version`, testing each level's directory then
  its `knowledge/` child). Discovery is deliberately strict — an unmarked
  directory is never mistaken for a bundle, so a writable server can't
  accidentally start against an arbitrary docs folder. The resolved bundle
  root is announced on startup. Does not apply to Claude Desktop, which
  spawns servers with an unrelated working directory — keep the positional
  argument or `OKF_BUNDLE_ROOT` there.
- **`OkfBundleTools.WriteToolNames`**, a new public property naming the
  three tools that mutate a bundle (`okf_write_concept`, `okf_append_log`,
  `okf_regenerate_indexes`) — the single source of truth for a host building
  a read-only tool set, instead of hand-maintaining its own copy of the list.

### Fixed

- **`ComputationExtractor`'s `# Computation` heading match no longer
  misfires inside an earlier, unrelated fenced code block.** The heading
  scan was blind to fence state: a heading-like line trimming to
  `# Computation` inside a prior Markdown fence was treated as the real
  heading, and that fence's own closing line was then mis-read as the
  sanctioned computation's opening fence — extracting arbitrary document
  text as if it were sanctioned §10 computation. The scan is now
  fence-aware. Separately, an indented `# Computation` heading (1-3 spaces,
  valid CommonMark ATX heading indentation) is now recognized, matching
  this method's own documented "trimmed text" contract.
- **Path-containment comparisons no longer guess case-sensitivity from the OS.**
  `ReparsePoints.IsWithinBundleRoot`, the 2-arg `ReparsePoints.HasReparsePointAncestor`,
  and `FileMemoryStore`'s reparse-escape check hardcoded `OrdinalIgnoreCase`
  (or picked it via an `IsWindows()||IsMacOS()` heuristic) instead of
  treating case-sensitivity as the runtime property of the volume it
  actually is — the same reasoning behind the earlier `Bundle.PathComparison`
  fix for `Bundle.TryResolveResource`. All three now use
  `StringComparison.Ordinal` unconditionally. Every current caller of these
  three sites already runs its own `Ordinal` containment check first, so no
  caller-reachable escape existed here before this change; what changes is
  that these helpers are now sound standing alone, independent of that
  caller discipline — real hardening at a security seam, at no cost to
  legitimate use, since every candidate path at these sites is built via
  `Path.Combine` from the same root it's compared against, so its prefix
  always keeps that root's exact casing. Separately,
  `MemoryServiceCollectionExtensions`'s memory/knowledge root overlap check
  — a misconfiguration-detection check whose safe direction is inverted
  from the escape-prevention sites above — now uses
  `StringComparison.OrdinalIgnoreCase` unconditionally instead of the same
  OS heuristic. This is the one site among the four with an actual
  observable behavior change: it now catches a case-variant overlap that
  the old heuristic could miss on Linux.

## [0.3.1-preview.1] - 2026-07-30

> Preview release: ships the §10 Attested Computation and per-caller source
> visibility work ahead of a full minor release.

### Added

- **Per-caller source visibility.** `IKnowledgeResolver` searches can now be
  restricted to a subset of enabled `Knowledge`-role sources, based on the
  caller's `KnowledgeAccessScope`. Two mutually-exclusive mechanisms on
  `KnowledgeQuery`: `PermittedSourceIds` (a host-precomputed set of source
  IDs — the recommended default, no host-level default since a static set
  can't represent "differs by tenant") and `SourceVisibilityPolicy` (a
  per-source function, with a `KnowledgeOptions.DefaultSourceVisibilityPolicy`
  host default a function can still vary per call by reading the scope it's
  given). `PermittedSourceIds` always wins over a configured default when
  set. `OkfContextProvider`'s scoped (V2) mode now passes the same
  `KnowledgeAccessScope` it already resolves for memory into the knowledge
  query too.
- **Attested Computation (§10).** Full v0.2 §10 support: `Frontmatter.ComputationContract`
  projects the runtime/parameters/computation/executor/attester contract; `OkfDocument.Computation()`
  returns the sanctioned computation (fenced `# Computation` or `computation:` file); `okf validate`
  emits §10 + §6.2 soft-guidance warnings (never Error). New zero-dep **`OKF4net.Attestation`**
  package: host-plugged `IParameterBinder`/`IComputationExecutor`/`IAttester` and an
  `AttestationOrchestrator` (load → bind → execute → receipt-shape check → attest → gate on
  verdict + `stale_after`), errors-as-data. `OKF4net.Agents` gains `okf_get_computation` and, when
  an orchestrator is wired, `okf_run_computation`.
- **§6.2 path-valued frontmatter resolution** — `OkfDocument.FrontmatterResources()` +
  `Bundle.TryResolveResource`/`ReadResourceText`, with broken/unsafe-path validator warnings.

### Changed

- **`KnowledgeQuery` is no longer V1-scoped.** It gains `Scope`
  (`KnowledgeAccessScope`, defaults to `KnowledgeAccessScope.Local`) — the
  "actual multi-tenant consumer" an earlier doc comment said would justify
  adding identity fields has materialized.
- **Breaking: `KnowledgeResolverRouter`'s constructor gained a new
  parameter, `defaultSourceVisibilityPolicy`, inserted between the
  pre-existing `defaultFairnessQuota` and `clock` parameters.** Any external
  caller invoking the constructor with positional arguments past
  `defaultFairnessQuota` fails to compile until the call site is updated —
  never silently, but source- and binary-breaking for that call shape.
  Callers using named arguments are unaffected.

## [0.3.0] - 2026-07-29

OKF4net now targets **OKF specification v0.2**. The core library and `okf` CLI
implement v0.2's provenance, trust, and lifecycle model, with the two
v0.2-sanctioned legacy fallbacks so v0.1 bundles keep loading unchanged.
`OKF4net.Catalog` gains fully-implemented session/tenant memory tiers and
three selectable knowledge-resolver ranking strategies.

### Added

- **Provenance / trust / lifecycle frontmatter (§5)** — typed, order-preserving
  accessors on `Frontmatter`, each projected lazily and never throwing on
  malformed input (permissive loading, §3):
  - `sources` with per-entry credibility signals (`author`, `usage_count`,
    `last_modified`) and the `usage_window` sibling (`Source`, `UsageWindow`).
  - `generated` / `verified` stamps and the derived trust tier (`Stamp`,
    `TrustTier`: unverified / machine-confirmed / human-reviewed).
  - `status` (draft|stable|deprecated) and `stale_after` (`Lifecycle`,
    `ConceptStatus`), with staleness computed against an injectable `IOkfClock`.
  - The §7 actor convention (`Actor`: `human:`/`process:`/`<producer>/<version>`).
  - `StalePolicy` (Use / Tolerate / Strict) for consumers.
- **`OkfDocument.Sources()`** — v0.2 provenance with the §13.1 legacy fallback:
  the frontmatter `sources` field, or the legacy `# Citations` body list when it
  is absent. `Frontmatter.LastChangedAt` falls back `generated.at ?? timestamp`.
- **v0.2 conformance fixture** (`tests/fixtures/okf_v02`) and its byte-exact golden.
- **Consumer-layer v0.2 wiring** — the provenance/trust/lifecycle model is now
  surfaced through `OKF4net.Agents` and `OKF4net.Catalog`:
  - `okf_write_concept` auto-stamps a `generated` block (§5.2) —
    `{by: okf4net/<version>, at: <UTC>}` — when the frontmatter has none (opt-in
    per tool; the scoped-memory write path is deliberately never auto-stamped).
  - `okf_read_concept` prints a `status | trust | stale` meta line, and
    `okf_search` marks hits `[deprecated]` / `[stale]`, when those differ from
    the defaults.
  - `OkfContextProvider`, `GroupedKnowledgeResolver`, and `KnowledgeQuery` honor
    a `StalePolicy` (default `Use` — surface everything, never silently drop)
    when admitting concepts, with staleness resolved against an injectable clock.
  - `KnowledgePassage` carries the matching concept's `TrustTier` and full
    `Lifecycle`, so a resolver or host can filter and render provenance without
    reparsing frontmatter.
- **Session and tenant memory tiers are now fully implemented and tested.**
  v0.2.0 shipped only the user tier working end-to-end (session/tenant were
  contract/parse-only). The underlying mechanism (`FileMemoryStore`, manifest
  parsing, DI wiring, `OkfContextProviderOptions.CaptureTier`) turned out to
  already be generic across all three tiers, so this release is primarily the
  test coverage, one path-nesting fix (see Fixed), and doc corrections that
  make the existing generic mechanism trustworthy for session/tenant, not new
  surface area.
- **Selectable resolver ranking strategies.** `IKnowledgeResolver` searches
  can now be ranked three ways: `GroupedBySource` (each source's results
  concatenated in priority order — the previous and still-default
  behaviour), `Merged` (one cross-source ranking by descending score, with
  source priority as a tie-break only), and `PriorityWeighted` (source
  priority first, score only within a priority tier). Choose one per host
  via `KnowledgeOptions.DefaultResolverStrategy`, or per call via
  `KnowledgeQuery.ResolverStrategy`. `AddKnowledge` now registers
  `KnowledgeResolverRouter` as the `IKnowledgeResolver`, so existing
  consumers gain per-query selection without any code change, and result
  ordering is unchanged until a host opts in.
- **Fairness interleaving for fused strategies.** An optional
  `FairnessQuota` (host-level `KnowledgeOptions.DefaultFairnessQuota` or
  per-query `KnowledgeQuery.FairnessQuota`) caps how many consecutive
  passages one source may contribute before another source's next-best
  passage is pulled ahead. It reorders only — no passage is ever dropped —
  so it affects consumers that truncate early, such as an agent context
  provider spending a token budget top-down.
- **Same-directory source dedup.** The merged strategies collapse two
  enabled manifest entries that resolve to the same directory, searching
  that bundle once instead of twice. Two *different* directories that
  happen to share a concept id are never merged: a concept id is relative
  to its own bundle root and is not a globally stable identity.
- **`OkfContextProviderOptions.KnowledgeQueryFairnessQuota`** — attaches a
  fairness quota to the knowledge query the context provider issues. The
  provider is the archetypal early-truncating consumer (it renders
  passages top-down until its token budget is spent), so this is what lets
  a budget-bounded agent see several sources instead of one prolific
  source's entire run.

### Changed

- **`OkfSpec.Version` is now `"0.2"`** — `okf validate` and `-V` report OKF v0.2.
  Conformance (§11) still requires only a non-empty `type`, a parseable
  frontmatter block, and well-formed reserved files; every new
  provenance/trust/lifecycle/actor check is a Warning or Info, never an Error.
- **`BundleValidator`** emits the v0.2 soft-guidance diagnostics and takes an
  optional `IOkfClock` for deterministic staleness.
- The producer-side `OkfDocument.Validate()` now requires `type`/`title`/
  `description` (no longer `timestamp`).
- **Breaking:** `DefaultKnowledgeResolver` is renamed `GroupedKnowledgeResolver`
  (behaviour identical). Code that resolves `IKnowledgeResolver` from DI is
  unaffected; only direct references to the concrete type name need
  updating.
- **A non-positive `FairnessQuota` is rejected** with an `ArgumentException`
  by every strategy — including `GroupedBySource`, which ignores the quota
  otherwise — so the same malformed query fails the same way whichever
  strategy runs it. A non-positive resolver constructor default throws
  `ArgumentOutOfRangeException` at construction. `null` remains the way to
  disable fairness reordering.

### Fixed

- **YAML flow-style plain scalars** now keep bare colons inside values, so v0.2
  frontmatter written in flow style (`generated: { by: human:ada, at: … }`,
  URLs, ISO timestamps) parses correctly.
- **`MemoryPath.For`'s session-tier path now nests under tenant and user**
  (`memory-session/<tenant>/<user>/<session>`, matching how the user tier
  already nests under tenant), closing an isolation gap where two different
  tenants sharing the same session id would have collided on
  `memory-session/<session>`. Any deployment that had already enabled the
  session tier (undocumented and untested before this release) will need to
  re-capture session memory under the new path -- existing session-tier
  content at the old path is orphaned, not migrated.

## [0.2.0] - 2026-07-27

This release grows OKF4net from a core library + CLI into an agent- and
tooling-ready stack: three new integration packages, a new MCP tool, and
host-scopeable long-term memory — all built on the same zero-dependency core.

### Added

- **Scoped memory (V2)** for `OkfContextProvider` — host-scoped long-term memory
  that can be enabled on a multi-user deployment without cross-scope leakage:
  - `KnowledgeAccessScope` (tenant / user / session; every segment path-safe by
    construction), supplied per invocation by a host `ScopeAccessor` delegate —
    never derived from a message.
  - `role:"memory"` catalog sources with a `MemoryTier` (`session`/`user`/
    `tenant`), a scoped `IMemoryStore` / `FileMemoryStore` (**user tier
    implemented**; session/tenant are contract/parse-only for now), and readable
    path prefixes via `MemoryPath.For` (`memory-user/<tenant>/<user>/…`), encoded
    case-insensitive-safe so distinct scopes never collide on Windows/macOS.
  - The provider's V2 mode reads knowledge (resolver) ∪ scoped memory under a
    **split token budget** (knowledge + memory floors with spillover) and
    captures each exchange deterministically to one tier; it never throws toward
    the invocation pipeline and injects only as message data.
  - RGPD/audit: `IMemoryStore.DeleteScopeAsync` / `EnumerateAsync`.
  - `AddMemory(this IServiceCollection)` DI facade wiring a store from the
    catalog's `role:memory` sources.
- **`OKF4net.BundleConceptWriter`** — the atomic, reparse-guarded, per-path-locked
  concept-write primitive, promoted to core so `OkfBundleTools` and the memory
  store share one write path.
- **`OKF4net.Agents`** — Microsoft Agent Framework integration (new package):
  - `OkfBundleTools` exposes nine OKF bundle operations as `AIFunction` tools
    (`okf_read_concept`, `okf_browse`, `okf_graph`, `okf_search`,
    `okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes`,
    `okf_validate_bundle`, `okf_changes_since`) for use via
    `chatClient.AsAIAgent(tools: …)`. Writes are producer-grade validated,
    serialized under a per-bundle-path lock, and guarded against directory
    traversal, embedded NUL, and symlink/junction (reparse-point) escapes.
  - `OkfContextProvider` (an `AIContextProvider`) auto-injects budget-bounded,
    progressive-disclosure bundle context into each invocation as reference
    data — never as instructions — and, opt-in, captures each exchange as
    deterministic per-day long-term memory (no LLM call) written back through
    the same validated, locked, reparse-guarded write path. Memory capture is
    off by default: `MemoryCaptureMode.Disabled` unless explicitly set to
    `Enabled`.
- **`OKF4net.Catalog`** and **`OKF4net.Catalog.Hosting`** — a local knowledge
  catalog (two new packages):
  - A hot-reloadable `catalog.json` manifest naming one or more local OKF
    bundles as *sources*, parsed by a strict, never-throw manifest parser
    (structured `CatalogDiagnostic`s, immutable results).
  - `FileKnowledgeCatalog`: fail-fast on an invalid initial manifest,
    errors-as-data on reload, atomic snapshot swap with a monotonic
    `Generation`, and a best-effort debounced file watcher (`ReloadAsync` is
    the source of truth). Source paths are validated to stay within the
    catalog root (OS-appropriate containment, reparse-point rejection).
  - A multi-source resolver that searches every enabled source and returns
    results **grouped by source — no cross-source fusion or dedup** (V1), plus
    an `AddKnowledge(…)` / `AddCatalogFile(…)` `IServiceCollection` facade.
    `OKF4net.Catalog.Hosting` is the only project taking a
    `Microsoft.Extensions.*` dependency; the catalog core stays zero-dependency.
- **`OKF4net.Mcp`** — a local Model Context Protocol server, shipped as the
  `okf-mcp` `dotnet tool`, exposing one OKF bundle to Claude Desktop / Claude
  Code over stdio (`dotnet tool install -g OKF4net.Mcp`). Bundle root via
  argument or `OKF_BUNDLE_ROOT`; read-only mode via `OKF_MCP_READONLY` drops
  the three write tools. stdout is reserved for JSON-RPC; all logs go to stderr.
- `OKF4net.ConceptSearch` — the shared full-text scorer (title ×3,
  tags/description ×2, body ×1) and excerpt helper, promoted into the core
  library so `OKF4net.Agents` (`okf_search` / context provider) and
  `OKF4net.Catalog` rank results identically by construction.

### Changed

- **Breaking:** `MemoryCaptureMode.SharedBundle` is renamed to
  `MemoryCaptureMode.Enabled` (reads correctly in both single-bundle and scoped
  modes).
- `role:"memory"` catalog sources are excluded from `IKnowledgeResolver` search
  (they feed `IMemoryStore`, never shared knowledge).
- `OkfContextProviderOptions.MemoryDirectory` is deprecated in favour of scoped
  `role:memory` catalog sources.
- The package version is now sourced solely from `Directory.Build.props` (one
  source of truth across every project).
- Public read-only surfaces hardened against downcast-and-mutate:
  `YamlMapping.Entries`, `ConceptId.Segments`, `KnowledgeCatalogSnapshot.Sources`,
  and all catalog diagnostic lists are now genuine read-only views.
- The project website was rebuilt as a Vite + React static site with expanded
  developer documentation (getting-started, library, CLI, MCP, and spec pages).

### Fixed

- `IndexGenerator` no longer walks into or lists a symlinked/junctioned
  subdirectory as if it were real: reparse-point detection now uses an
  lstat-correct `FileSystemInfo.LinkTarget` fallback on Unix (where
  `File.GetAttributes` resolves *through* a link): a reparse point is treated
  as neither a file nor a directory, so it is never traversed or listed.
- `Bundle.OkfVersion` is computed eagerly at `Bundle.Load` so it reflects a
  true snapshot of the bundle at load time (previously deferred, which could
  observe later mutation).

## [0.1.1] - 2026-07-24

### Added

- **winget distribution** for the `okf` CLI: `winget install Coderise.OKF4net`
  (portable package, command alias `okf`). Tagged releases now build Native AOT
  binaries for `win-x64` and `win-arm64`, publish a GitHub Release with the
  zipped binaries and `checksums.txt`, and generate the winget v1.6.0 manifests.
  See `packaging/winget/README.md` for the one-time submission to
  `microsoft/winget-pkgs`.
- Project website and developer documentation — getting-started guide, CLI and
  library API reference, and spec-section mapping — deployed to GitHub Pages.

### Changed

- CI/dependencies: bumped `actions/checkout` 4→7 and `actions/setup-dotnet` 4→6,
  and the test-dependencies group.

## [0.1.0] - 2026-07-22

### Added

- Initial C# implementation of OKF v0.1 (see [`NOTICE`](NOTICE) for the full
  derivation and attribution chain):
  - `OKF4net` library — YAML-subset parser/emitter, `OkfDocument`,
    `Frontmatter`, `ConceptId`, `LinkScanner`, `Bundle`, `IndexGenerator`,
    `ChangeLog`, `BundleValidator`.
  - `okf` CLI (`validate`, `info`, `index`, `graph`, `parse`, `fmt`),
    published as a Native AOT single-file binary.
- Test suite (unit, integration, and byte-exact golden CLI comparisons).

### Changed

- Relicensed from Apache-2.0 to LGPL-3.0-or-later; Apache-2.0 attribution for
  upstream ported portions is preserved in `NOTICE` and `LICENSE.Apache-2.0`.

[Unreleased]: https://github.com/jchable/okf4net/compare/v0.5.0...main
[0.5.0]: https://github.com/jchable/okf4net/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/jchable/okf4net/compare/v0.3.1-preview.1...v0.4.0
[0.3.1-preview.1]: https://github.com/jchable/okf4net/compare/v0.3.0...v0.3.1-preview.1
[0.3.0]: https://github.com/jchable/okf4net/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/jchable/okf4net/compare/v0.1.1...v0.2.0
[0.1.1]: https://github.com/jchable/okf4net/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/jchable/okf4net/releases/tag/v0.1.0
