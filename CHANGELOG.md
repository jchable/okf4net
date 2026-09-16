# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **`OKF4net.Attestation.Containers`** — a host implementation of the §10
  `IParameterBinder` / `IComputationExecutor` / `IAttester` contracts that runs a
  bundle's *actual* sanctioned script or SQL, and its *actual* attester, inside a
  real container. Nothing a bundle references is ported or reimplemented in C#,
  which is the whole point: a computation OKF4net reimplemented would no longer
  be the sanctioned one, and attesting it would attest the reimplementation.
  - One `IContainerEngine` abstraction over any `run`-compatible CLI — Docker,
    Podman, nerdctl — with `CliContainerEngine` as its only real implementation.
  - **No container is ever given a bind-mounted volume.** Every *code* payload —
    script text, SQL text, attester module source — travels on stdin as text or
    JSON, and the container's own command is always a short fixed string: an
    interpreter invocation, or a project-authored wrapper. Arguments are built
    exclusively through `ProcessStartInfo.ArgumentList`, never a concatenated
    shell string, and never with `UseShellExecute`.
  - **Parameter values travel differently per runtime, and it matters for
    exposure.** A `SqlClient` run carries them on stdin alongside the SQL; a
    `Script` run carries them in the `OKF_PARAMS_JSON` environment variable, which
    is visible to `docker inspect` and in the host process list — the same exposure
    class as a connection string. The project README's Limitations section says so;
    do not pass a value you would not put in a process listing.
  - Two executors: `Script` (the bound text is a standalone program) and
    `SqlClient` (the bound text is SQL, bound by a driver's own native parameter
    mechanism — never interpolated into the query).
  - One shared `AllowlistParameterBinder` filters and type-checks supplied values
    against the concept's declared `parameters` and **never edits the sanctioned
    text**, placeholders included. Both executors and the attester receive only
    that filtered set.
  - `ContainerIsolation`, reached through `ContainerRuntimeProfile.Isolation` and
    `ContainerAttesterOptions.Isolation`, carries the per-run ceilings
    (`--memory`, `--cpus`, `--pids-limit`, wall clock) and rejects a non-positive
    value rather than accept it: to docker and podman, zero there means
    *unlimited*, so a profile written with `MemoryBytes = 0` would remove the very
    ceiling it looks like it sets.
  - **The root filesystem is read-only by default**, with `/tmp` as a
    memory-backed `tmpfs` that dies with the container — the one writable path the
    stages genuinely need, named explicitly rather than leaving the whole image
    writable. The attester bootstrap writes the bundle's module there before
    importing it, and the SQL wrapper installs its driver there
    (`pip install --target`). Pinned by an integration test that requires a write
    outside the tmpfs to fail *and* one inside it to succeed — the first alone
    would also pass on an image with no such path, the second alone with no
    hardening at all.
  - The `SqlClient` wrapper imports its driver first and pip-installs it — pinned
    to `pg8000==1.31.5`, never "whatever the index serves today" into a process
    holding `OKF_CONN` — only when the image does not already provide it, so an
    image that vendors the driver runs with its network closed down to the
    database. An explicit `NetworkMode = null` is honoured on either kind as "the
    engine's default".
  - Every run's stdout and stderr are capped at 8 Mi characters, and exceeding
    the cap fails the stage rather than truncating quietly. `Timeout` must be a
    duration a timer can enforce — `Timeout.InfiniteTimeSpan` is rejected rather
    than read as "no ceiling" — and is checked before any process starts;
    teardown after a timeout, both `kill` attempts included, is abandoned once
    one 3-second budget is spent (see Fixed), so a timed-out run still returns.
    A receipt is a JSON object and nothing else: a run whose stdout is the
    literal `null` is rejected, not read as empty.
  - **Not published to NuGet**, deliberately, and the `.csproj` carries no
    packaging block — see the root README's project table. Its useful operation
    needs a container engine on `PATH` and a reachable daemon, which no package
    restore can provide.
  - Integration tests that shell to a real engine are excluded from CI by
    decision (`Category=ContainerIntegration`, mirroring `producers/`); they are
    run manually against real Docker.

- **`bundles/meridian_transit/`** — a self-authored bundle built the way
  `acme_retail` is, but whose computations actually run on the executors above:
  one `postgres` SQL computation over a real table, and one `python` computation
  applying a daily fare cap in order. Its two attesters differ in what they can
  establish, which is the reason it carries both: the fare-cap attester
  *recomputes* the policy from the run's inputs, so a pass is evidence about the
  number; the ridership attester can only check invariants of the query's shape,
  because `executed_sql` is echoed by this host's own wrapper and Postgres mints
  no equivalent of BigQuery's `job_id`. `references/schema.sql` carries the seed,
  chosen so a query that drops `status` or `service_date` fails rather than
  coincides.

- **`okfgen generate --roslyn-timeout <seconds>`** — a wall-clock budget for the
  whole Roslyn stage, the `dotnet msbuild` queries and the compilations after
  them. Absent by default, and absent means unbounded: each query is capped at
  two minutes on its own, but nothing caps their sum, so a large repository runs
  for as long as it runs. It is not a default because a budget makes the emitted
  bundle a function of how fast the machine is, and determinism is pinned at a
  fixed extractor version, not a fixed CPU. If the budget runs out the stage is
  abandoned **whole**, never truncated: the run lands in exactly the
  `--no-msbuild` state, with a note naming the same two losses, rather than
  emitting a bundle whose exact and name-matched links are divided by machine
  speed with nothing recording where the line fell.
- **`okfgen` gains a C# code-graph stage** (`producers/OkfProducer`, outside
  `OKF4net.sln` and outside CI by decision). `generate` now emits one `code/`
  concept per namespace, type and member, with resolved `## Calls` links. Two
  engines behind one contract: tree-sitter extracts symbols and call sites
  language-agnostically, and Roslyn resolves C# call sites exactly — without
  `MSBuildWorkspace`, querying project inputs through a bounded `msbuild -getItem`
  subprocess — with a name-match resolver covering what Roslyn cannot reach. Call
  sites are identified by UTF-8 byte offset, since the two engines natively speak
  UTF-16.
- **`okfgen generate` prints a completeness report** to stderr, prefixed `run: `,
  on every run that reaches the generation stage: files visited and how many fell
  to each cause, whether the traversal was complete, projects detected and how
  many of the closure compiled, exact-resolver coverage, and how many `code`
  concepts are reachable from `overview`. It exists because every other account a
  run gives of itself is a note gated on its own trigger, so a run printing
  nothing was indistinguishable from a mechanism that did not fire. On stderr, so
  no CI gate reading stdout changes and nothing lands in the bundle.
- **`okfgen generate --check`** compares a regenerated bundle against the one on
  disk, over a copy, and reports drift without writing. Backed by a golden
  fixture.
- **Project detection follows every `*.sln` in the tree**, not only one at the
  repository root. A root-only lookup let the first root solution decide the whole
  answer: measured on this repository, 9 of 17 `.csproj` were detected, and the
  194 `code` concepts of the undetected projects belonged to no package concept
  and were unreachable from `overview` — which `okf validate` does not report,
  because an orphan dangles nothing. A `.csproj` that no solution references is
  still not a package.
- **`okfgen generate --repo-url <url>` / `--rev <ref>`** make each concept's
  `resource` a forge permalink — to its declaration for a `code/` concept, to the
  file for a `packages/` or `docs/` one. The two families differ without the
  flags: a `code/` concept then carries no `resource` at all, while a
  `packages/`/`docs/` one falls back to the repository-relative path. A
  `--repo-url` that is not an absolute http/https URL is refused rather than
  silently dropping every permalink. `--rev` defaults to the current branch name
  and never to a sha — a sha would rewrite every code concept's `resource` on the
  next commit — and is required for permalinks on a detached HEAD, where there is
  no branch name to read.
- **Scope and size flags on `generate`**: `--include-tests` and
  `--include-internal` widen what the code stage emits, `--no-code` skips the
  stage entirely, and `--max-file-size <bytes>` (2 MiB by default) caps the
  largest source file either engine will read. The tree-sitter engine counts what
  it skips, which holds back the concepts that file owned; the rest of the bundle
  is pruned as usual.
- **A generate is a function of the commit, not of the clock.** `overview`'s
  `generated.at` and `revision` are stamped from the HEAD commit's committer date
  and sha — the wall clock is only the fallback for a tree git cannot answer for —
  and the output ordering is deterministic, so re-running over the same commit
  does not churn the bundle. Writes land in a staging directory and are committed
  at the end, so a run that fails while generating leaves the previous bundle
  where it was.
- **`ConceptSearch.TopDiversified`** — picks the top N of a scored result set
  while rotating across top-level id families, so one family cannot take every
  slot in a truncated window. `ConceptSearch.Search` is unchanged; this is an
  added selection step, not a second scorer.
- **`ConceptSearch.TopDiversifiedBy`** — the same rotation for a list the caller
  has already ordered by something the scores cannot express (a catalog
  resolver's source interleave, for instance): families are visited in the order
  they first appear rather than re-sorted by score, so the two orderings compose
  instead of one silently undoing the other. Pass `items.Count` for a full
  reordering when what bounds the list is a budget rather than a slot count.
- **`OkfBundleTools.GetTools(OkfToolMode)`** — chooses how the four
  write-capable tools are exposed. `ReadOnly` omits them; `RequireApprovalForWrites`
  wraps exactly those in `ApprovalRequiredAIFunction` so the Agent Framework
  asks the host before a mutation; `ReadWrite` is the historical ungated
  behaviour and remains what the parameterless `GetTools()` means, so no
  existing host changes under them. Read tools are never wrapped: prompting for
  everything trains a user to click through, which is how the one approval that
  mattered gets waved past. `OkfMcpToolset` now filters through `ReadOnly`
  rather than re-implementing the same rule against `WriteToolNames`.
- **`OkfBundleTools.RunComputationAsync`** — the cancellable form of
  `okf_run_computation`, and what `GetTools()` now exposes under that name.
  `AIFunctionFactory` binds its `CancellationToken` from the invocation and
  leaves it out of the generated JSON schema, so the model still sees exactly
  two parameters (pinned by a characterization test, since the design depends
  on that upstream behaviour and a change would fail silently). The synchronous
  `RunComputation` is `[Obsolete]` for one version and now delegates.
  `[Obsolete]` is a build error under `TreatWarningsAsErrors` — migrate the
  call or suppress CS0618 for one version.
- **`OkfBundleTools.ComputationTimeout`** — a wall-clock ceiling on one run,
  default two minutes, combined with the caller's own token. **Breaking
  (behaviour):** a run that used to complete after more than two minutes now
  reports a timeout unless the host sets `ComputationTimeout`
  (`Timeout.InfiniteTimeSpan` restores 0.5.0's unbounded wait). §10 sets no time
  limit; this is a host guard, because bind/execute/attest are host-plugged code
  that may do unbounded I/O. Elapsing is reported to the model as a normal
  non-displayable outcome rather than thrown at a caller who never asked to
  stop; `Timeout.InfiniteTimeSpan` disables it. A value the runtime will not
  accept as a delay — negative, or past its ~49.7-day ceiling — is reported as
  an `Error:` string as well, since a misconfiguration is exactly when the
  never-throw contract is worth most.
- **`OkfContextProviderOptions.OnInternalError`** — an optional
  `Action<Exception>` sink called with the real exception whenever context
  assembly swallows one (a failed bundle load, a failed knowledge/memory read).
  `null` by default, so the never-throw contract is unchanged. This is the
  counterpart to the error sanitization under *Fixed*: the injected context used
  to be the only place that detail surfaced at all, so removing it there without
  a replacement channel would have traded a leak for an undiagnosable failure.
  The host gets the exception; the model gets the category.
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
- **`okf verify <bundle> <id>… --by <actor>`** — the verb that answers what
  `okf audit` asks about trust: it records a review (§5.2) by adding, or
  from the same actor replacing, a `{by, at}` entry in each named concept's
  `verified` list, so — for a `human:` actor — the concept clears audit's
  trust-filtered (`--trust unverified`/`unverified,machine-confirmed`)
  selection. A `process:` or `<producer>/<version>` actor is accepted symmetrically (§7) but
  only moves the concept from `unverified` to `machine-confirmed`, which
  that same filter still selects. Verification only moves the trust
  dimension (§5.3) — it never touches
  `stale_after`, so a just-reviewed concept can still appear in `okf audit`'s
  *default* worklist, which selects on staleness alone. `<id>…` also accepts
  a single `-`, reading concept ids from standard input, so
  `okf audit … --trust unverified | cut -d' ' -f1 | okf verify … --by
  human:ada -` closes the loop in one line. An empty stream on that pipeline
  is "nothing to do", not an error: `verify -` writes nothing and exits 0,
  matching `audit`'s own empty-worklist exit, so the loop stays idempotent
  and safe under `set -e` when the bundle needs no attention. Naming no
  concept at all (`okf verify <bundle>`) is still an error. An actor carrying
  a control character is refused by `RecordVerifications` itself, so a `--by`
  value can never forge a line in the verb's own line-oriented output; reading
  an actor out of an existing bundle (`Actor.Parse`, `Trust.DeriveTier`) stays
  permissive, as it must. `--dry-run` shows what would be
  recorded without writing; `--at <yyyy-MM-ddTHH:mm:ssZ>` overrides the
  default of "now" (a bare date, an offset, or fractional seconds are
  rejected). A batch is validated (existence, §11 conformance, no duplicate id) before the
  first write, but writing several files cannot be atomic — a mid-batch I/O
  failure still leaves the earlier concepts stamped, and is reported as
  such. Backed by the new `BundleConceptWriter.RecordVerifications` in the
  core library — the single governed writer of `verified` — and exposed to
  agents as the `okf_verify` tool. **A `verified` stamp is a dated
  declaration, not a proof**: it cannot and does not authenticate the
  signer's identity, nor confirm anyone read the concept. Credibility comes
  from where the stamp lands — a diff a human reviewed — never from
  inferring one out of a PR approval.
- **A new `okf-render <bundle> --out <dir>` binary** generates a
  self-contained, browsable HTML site from a bundle: one page per concept
  (frontmatter table + rendered body), a generated index, navigable
  cross-links with broken links flagged, and backlinks. Backed by the new
  zero-dependency `OKF4net.Viewer` project, consumed by the new
  `OKF4net.Render` project rather than by `okf` itself — `okf` is meant to
  stay the small, dependency-free CI validator winget distributes, and the
  viewer's vendored JavaScript is dead weight in a binary that never executes
  it. Markdown renders client-side via a vendored copy of marked (MIT); raw
  HTML is neutralized by sanitizing the parsed DOM in `viewer.js` (element
  allowlist, per-tag attribute allowlist, URL-scheme validation) rather than
  by patching marked's renderer hooks, which cannot bound the attack surface
  in general (see `CLAUDE.md`). GFM task list items survive sanitization as
  real `<input type="checkbox" disabled>` elements with correct checked
  state, so a screen reader announces them as checkboxes rather than as
  decorative text. No full-text search: a static site has no server to run
  the shared `ConceptSearch` scorer, and mirroring its weights in JavaScript
  would fork it. (This started life as `okf`'s `render` verb; it
  moved to its own binary before ever shipping in a release, so there is no
  deprecated verb or shim to call out here.)
- **A `sources[]` entry can now carry its own `usage_window` override
  (§5.1).** `Provenance.ParseSources` reads a per-entry `usage_window`
  through the same `ParseUsageWindow` the shared, top-level one already
  used, `Validate` checks its bounds through the same `CheckTemporal` machinery
  as the other §5 timestamp keys (reusing `UsageWindowInvalidFrom`/
  `UsageWindowInvalidTo`, with `Diagnostic.Field` telling the two positions
  apart — `sources.usage_window.from`/`.to` vs. `usage_window.from`/`.to`; no
  new `DiagnosticCode`), and the new `Frontmatter.EffectiveUsageWindow(Source)`
  resolves the two into the one value a consumer actually wants. This closes
  **S5.1-3**, recorded as "Missing" in
  `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md:204`, where an
  annotation beside that finding now names the branch that implements it and
  points to the design doc.

  This was never a data-loss bug: `okf fmt` already round-tripped a per-entry
  `usage_window` before this change, because `Frontmatter` re-serializes an
  order-preserving `YamlMapping` and the lossy path, `Provenance.ToYaml`, has
  exactly one caller — the producer builder — which `fmt` never goes through.
  The gap was that the library could not *see* the value: no typed access, no
  §5 validation, no way for a consumer to obtain it. `Source` gains an
  optional `UsageWindow?` member and `OkfDocumentBuilder.AddSource` a matching
  optional parameter, so a per-entry override is also writable through the
  producer builder. The *shared*, top-level `usage_window` — §5.1's normal
  case — still has no typed builder method and can only be written through
  `Extension("usage_window", …)`; that gap is tracked in `ROADMAP.md`.

  The override is **whole-object, not per-field**: an entry writing
  `usage_window: { from: X }` yields a window whose `to` is `null` — it does
  not inherit the shared sibling's `to`. §5.1 (`SPEC.md:332-334`) says an
  entry MAY carry its own `usage_window` "to override the shared one" and
  stops there — a per-field merge would be inventing a rule the spec does not
  state, so this is a deliberate interpretation on our part, not something
  the spec itself settles.
- **`okf validate` warns on an `index.md` list item with no link.** §8 shows
  every entry as `* [Title](relative-url) - description`; an entry naming a file
  in inline code gives a reader nothing to follow. It raises `IndexEntryNotALink`
  (warning). §8 gives that format by example rather than by rule, so this is a
  heuristic, kept to items with no link anywhere: one that renders a link without
  opening on one — `**[A](a.md)**`, an icon before the link, a link on a
  continuation line — is not warned. Thematic breaks (`* * *`), prose and code are
  not items, and a block quote right after an item starts a block of its own, so a
  link inside it is not the item's. Items take their links from the same
  paragraph-at-a-time pass as `ExtractLinks`, so an entry whose link text or
  destination wraps onto the next line is still an entry with its description. Reading items off the code-blanked line had also let an item opening
  with inline code (`` * `x` [a](b) ``) pass as an entry whose description is
  checked, and a prose line such as `` `code` - [a](b) `` pass as a bulleted one;
  whitespace is now read on the line as written.
- **`okf validate` warns on a heading that names examples or a schema without
  §4.2's conventional heading.** A heading containing the word *example(s)* or
  *schema(s)* — `# Worked example`, `# Table schema`, `## Examples` — raises
  `NonConventionalHeading` (warning) when the concept carries no exact
  `# Examples` / `# Schema`; beside the conventional heading, a related one is a
  subsection and is not warned. A heading is read as it renders — raw HTML, code
  span backticks and link destinations left out, a code span's content kept, so
  `# <span>Examples</span>` is the conventional heading, ``# `Worked example` ``
  is a variant of it, and `# Glossary <!-- schema -->` names no schema — and
  wherever it stands, a block quote or list item included. A heuristic: it matches
  words, not meaning.
  `# Computation` is left out — a heading that merely mentions a computation is
  ordinary structure (acme_retail's `# Why no attested computation`), and a
  misspelled one on an Attested Computation already raises
  `ComputationMissingBody`. Both rules emit nothing on any bundle in `bundles/`
  or any fixture, and both flag the drift `bundles/meridian_transit` had before
  it was corrected by hand (`# Worked example`, three index entries in inline
  code). Both new `DiagnosticCode` members are appended.
- **Reference links count as links.** `LinkScanner.ExtractLinks` read only inline
  `[text](dest)` links, so a body using the other standard markdown forms (§6.1:
  "standard markdown links") — full `[text][label]`, collapsed `[label][]`,
  shortcut `[label]`, and `![alt][label]` images — recorded no edge in the graph,
  no backlink, no broken link, nothing in `okf parse` or an index entry, and the
  viewer left a dead link to a `.md` file. They now resolve against the body's
  link reference definitions (CommonMark §4.7) and are ordinary `ConceptLink`s
  whose `Target` is the definition's destination; `ConceptLink` itself is
  unchanged. Labels match as CommonMark normalizes them (case-folded, whitespace
  collapsed, first definition wins), a definition's destination has its backslash
  escapes resolved as an inline link's has (`[r]: a\_b.md` is `a_b.md`, which is
  also the href marked renders), and a full reference with an undefined label is
  no link. One deliberate divergence from commonmark.js, which has no
  footnotes: a bracket starting with `^` is always a footnote, so `[^k][r]` stays a
  citation (and `[r][^k]` is the link `[r]` beside a footnote), as GitHub renders
  them. Inline links are now found by the same algorithm — see Fixed. Compared
  with commonmark.js on 120 000 random bodies built around references, inline
  destinations, titles and angle brackets, and with `dev`'s scanner on the same
  cases: outside footnote brackets, no case regresses from `dev`, about 6 550 of
  every 40 000 are fixed, and 5 in 120 000 still differ, all one case:
  commonmark.js letting a later duplicate definition above a setext underline
  win, where §4.7 says "the first one takes precedence". An external audit then
  found a definition missed after `>` and a tab (`>\t[x]: /`): a paragraph's lines
  lose their leading whitespace (§4.8), so a definition may now follow any spaces
  and tabs. On 150 000 more bodies built around tabs and containers, the only
  other difference is commonmark.js itself: it skips spaces but not tabs between
  a link's parts, which §6.3 allows ("spaces, tabs, and up to one line ending"),
  so `[t](x\t)` is a link here as the spec has it. No bundle or
  fixture uses a reference link, so `okf graph` and `okf validate` output is
  byte-identical on all of them.

### Changed

- **Breaking (0.x): the frontmatter fence is `---` at column 0, and the YAML
  subset rejects what the docs already said it rejects.**
  - An indented `---` no longer opens or closes the frontmatter (§4: "delimited
    by `---` on its own line"). A fence is `---` at column 0, optionally
    followed by spaces or tabs. Other trailing whitespace (NO-BREAK SPACE, a
    lone `\r`) no longer counts either. The old trimmed comparison took an
    indented `---` inside a `|` block scalar as the closing fence, silently
    cutting the frontmatter there and moving the rest of it into the body; that
    line is now block content. A leading byte-order mark still means no
    frontmatter, as before.
  - What happens to a mistyped (indented) fence:
    - **On line 1:** the file has no frontmatter. `okf validate` still reports
      the missing `type`, and a new warning,
      `DiagnosticCode.FrontmatterFenceNotAtColumn0`, names the cause: "line 1
      looks like a frontmatter fence but is not at column 0 / starts with a
      byte-order mark, so the file has no frontmatter (§4)".
    - **As the closing line, with no column-0 `---` later in the file:** the
      document fails as `Unterminated YAML frontmatter block`.
    - **As the closing line, with a column-0 `---` later** (e.g. a thematic
      break in the body): the frontmatter runs to that later line. The indented
      line then fails parsing with `YAML error at line N: indented frontmatter
      fence: a `---` line must start at column 0 (§4) (file line N+1)`, whether
      it sits after a plain value, in a mapping, after a sequence item, inside an
      unterminated flow collection or quoted string (on the key's line or on its
      own line), or on its own. N counts from the first frontmatter line, like
      every YAML error. Only this message adds the file line; every other YAML
      error keeps its format, and `YamlValue.Parse` alone, which has no file
      around it, omits it. Before this rule, one such shape loaded silently,
      with the preceding value rewritten (`title: "T ---"`) and a following
      `# Heading` read as a YAML comment. Another failed with an unrelated error
      on a body line.
    - **Inside `|`/`>` block-scalar content:** the line is still content. The
      exception is a line indented less than the block's first content line,
      which YAML does not treat as content either; it gets the error above.
    - **Residual gap:** a mistyped closing fence directly after a block scalar,
      indented at least as deep as that block's content (or as the block's first
      line), is still read as block content. The body up to the next column-0
      `---` then joins the frontmatter, where it fails or is read as comments.
  - `okf verify` / `RecordVerifications` shares the fence predicate, so the
    `FrontmatterBlockEdit` refusal of an indented closing fence is gone: it could
    no longer trigger. A `verified` entry after an indented `---` inside a block
    scalar is now visible and merged instead of refused. An indented `---`
    outside block content is refused as unparseable before any edit.
  - Anchors (`&name`), aliases (`*name`) and tags (`!name`, `!!type`) starting
    an unquoted node (block key or value, sequence item, a node on its own line,
    flow item or key) now fail parsing with a `YamlParseException` that gives the
    line and names the feature. `README.md` already claimed the subset rejected
    anchors and tags, but such a document loaded, with those values read as plain
    strings (`k: *a` read as `"*a"`).
  - Directives, meaning a line starting at column 0 with `%`, and document
    markers, meaning a line starting at column 0 with `---` or `...` followed by
    the end of the line, a space or a tab (`...`, `--- x`, `... # end`), fail the
    same way. Inside a frontmatter mapping, such a line already failed when it
    was not also a `key: value` entry, but with an unrelated "expected 'key:
    value'" message. What used to load was:
    - a key spelled that way (`%x: 1`, `... x: v`);
    - a top-level scalar passed to `YamlValue.Parse` (`...`, `%x`, `--- x`).
  - **Re-emit a file written by an earlier `YamlEmitter` with a top-level key
    beginning with `... ` (three dots and a space).** Such keys were emitted
    unquoted (`... x: v`) and now fail as a document marker. The emitter now
    quotes them. No other frontmatter key or value an earlier emitter wrote is
    newly rejected: it quoted every string starting with `%`, `-`, `&`, `*`, `!` or a
    quote, with a leading space, or containing a tab, and it never wrote block
    scalars or continuation lines.
  - A quoted scalar followed by anything other than whitespace or a `#` comment
    (`k: "a" *b`, `k: 'a' b`, `"k" x: v`) now fails with `unexpected content after
    quoted scalar`, the rule already applied after a flow collection. The
    trailing text used to be silently dropped (`k: "a" *b` read as `"a"`).
  - Unaffected: quoted scalars, an indicator later in a plain scalar (`a & b`,
    `50%`), a value starting with `%` (`k: %foo`), block-scalar content and a
    plain scalar's continuation lines.
  - Still not detected, documented in the README: structures the subset does not
    parse are read as plain strings, so an indicator inside them is text:
    compact nested sequences (`- - *a`), flow-collection keys (`[*a]: v`) and
    `?` complex keys.
  - `Bundle.Load` reports every rejected file in `ParseErrors`, and `okf validate`
    reports it as a parse error.

- **Breaking (0.x): link-guard refusals, see the Security entry below.**
  - `Bundle.ReadResourceText` now throws `UnauthorizedAccessException` for a
    path outside `Bundle.Root`, or one that is (or sits below) a reparse point
    or an entry that could not be inspected. It used to read any path it was
    given. Its documented contract already limited it to
    `TryResolveResource`'s `Resolved` output, which still reads unchanged.
  - `CatalogPathResolver.TryResolve` returns false (`ReparsePointInPath`) for
    an existing source path below an entry that could not be inspected, which
    `FileKnowledgeCatalog` reports fail-fast as a `CatalogException`.
  - Public error strings changed. Code that matches them must update:
    - `BundleConceptWriter` (`WriteConcept`, `AppendToConceptAtomic`,
      `RecordVerifications`): "resolves through a reparse point
      (symlink/junction) inside the bundle" became "…(symlink/junction), or
      an entry that could not be inspected, inside the bundle", and "is a
      reparse point (symlink/junction), not a regular file" became "…
      (symlink/junction) or could not be inspected, not a regular file".
    - `OkfBundleTools.AppendLog` (`okf_append_log`): the same two changes
      for `log.md`.
    - `FileMemoryStore` (`ReadAsync` diagnostic, `DeleteScopeAsync` error):
      "path is a reparse point; refusing to …" became "path is a reparse
      point or could not be inspected; refusing to …".
    - `CatalogPathResolver`'s `ReparsePointInPath` diagnostic message gains
      ", or an entry that could not be inspected,".
    - `HtmlWriter.Write` has a new `ArgumentException`, "refusing to render
      into '…': cannot determine where it resolves …". It replaces the
      `UnauthorizedAccessException` that could escape while resolving
      `--out` through a link.

- `BundleConceptWriter.RecordVerifications` (§11) now names the offending
  concept when it refuses a batch — a concept missing `type` used to escape
  as an unattributed "Missing required frontmatter keys: type" (an
  unparseable concept's own parse error, and now an unreadable one's I/O
  error, are similarly attributed for the first time), leaving a caller to
  bisect a multi-id batch by hand. The check (`CheckVerificationTargets`,
  internal) is the single place `okf verify` and `okf_verify` now defer to
  for their own "nicer" pre-write message too, instead of each
  re-implementing an id-validity/existence/conformance loop of their own;
  `okf verify` also no longer loads the whole bundle to answer a k-id
  question, reading only the named concept files. Id validity, then a
  resolved-path duplicate, is now checked across every id before existence/
  parseability/§11 is checked for any of them — the old CLI and tool
  pre-checks were already deterministic on their own (each reported the
  first unknown id in list order; the writer already checked duplicates
  across the whole batch before existence), so this is not a fix to
  nondeterminism. What actually changes for a batch with more than one
  problem: a later id's not-found, unparseable or unreadable problem (all
  caught by the new front-loaded `CheckVerificationTargets` check) now wins
  over an earlier id's fence, NaN-float or deep-nesting refusal (caught only
  downstream, in the writer's per-id prepare loop) — the reverse of what the
  old single combined per-id loop reported. `RecordVerifications` also now
  refuses a case-variant id (`METRICS/DAU`) on a case-insensitive volume
  instead of silently stamping the differently-named on-disk file
  (`metrics/dau.md`), and reports an unreadable concept file as its own
  problem rather than an unhandled exception.
- **Breaking (combined effect on 0.5.0 bundles): a bare `attester.resource` /
  `computation` path that used to resolve beside the concept now resolves
  from the bundle root (this repo's reading of §6.2, which names no base — see
  the §6.2 entry below), AND an unresolvable `attester.resource` now
  fails the run closed — together, a bundle whose attester script sits next
  to its concept goes from running under 0.5.0 to never executing.** There is
  deliberately no fallback (Appendix A's layout only resolves from the root);
  instead `okf validate` now says where the file was found and what to
  write (`./script.py`). Also: `resource` is omitted from an Attested
  Computation's recommended fields by `Frontmatter.RecommendedFieldsFor`, the
  one definition of §4.1's carve-out.

- **Breaking (0.x):** `IOkfClock.Now` is the required member and `Today`
  derives from it — a `Today`-only clock written against 0.5.0 evaluated every
  §5.5 instant comparison at 00:00Z without a compile-time hint; it now fails
  to compile instead. `validate --json` and `audit --json` report
  `evaluatedAt`, the exact instant staleness was evaluated at (`asOf` remains
  its date), read from the clock exactly once per run.
- A valued flag given twice (`--by`, `--at`, `--as-of`, `--trust`, `--status`,
  `--type`) is now `error: option … given more than once` instead of silently
  keeping the first.
- **Stage failures the library itself diagnosed now say why.** A new
  `AttestationDiagnosticException` (`OKF4net.Attestation`) marks a message
  authored by an OKF4net component — `ContainerExecutionException` derives
  from it, and the allowlist binder's type rejection and the SQL executor's
  reserved-name refusal throw it. The orchestrator renders such a message into
  `Reasons` (`executor threw: ContainerExecutionException: SQL wrapper exited
  with code 1`); every other exception is still reported by type only, since a
  host runtime's message can carry a connection string. Before, a missing
  attester, a Python crash, an unpullable image and a timeout all read as the
  same `attester threw: ContainerExecutionException`.

- **Breaking (`OKF4net.Attestation.Containers`, unpublished): containers run
  as uid 65534 with every capability dropped and `no-new-privileges`, by
  default.** The isolation settings live on one shared `ContainerIsolation`
  record, reached through `ContainerRuntimeProfile.Isolation` and
  `ContainerAttesterOptions.Isolation`, so a hardening decision reaches the
  script executor, the SQL executor and the attester at once. New on it:
  `User`, `DropAllCapabilities`, `NoNewPrivileges`. Moved onto it, off both
  `ContainerRuntimeProfile` and `ContainerAttesterOptions`:
  `ReadOnlyRootFilesystem`, `TmpfsMounts` (including its absolute-path
  validation and the `TMPDIR` derived from its first entry — see Fixed),
  `MemoryBytes`, `Cpus`, `PidsLimit` and `Timeout`. On a profile,
  `Isolation = new() { … }` starts from the defaults; on attester options,
  write `new ContainerAttesterOptions().Isolation with { … }`, since a fresh
  record carries the profile-sized ceilings rather than the attester's smaller
  ones. A hand-built `ContainerRunSpec` stays opt-in, as `ReadOnlyRootFilesystem`
  already was. Found by an external review: untrusted bundle code ran as the
  image's default user (root on `python:3.12-slim`) with Docker's default
  capability set.

- **Breaking (`OKF4net.Attestation.Containers`, unpublished): receipts and
  attester verdicts with duplicate JSON properties, or with numbers that cannot
  be represented exactly, now fail the stage instead of being silently
  resolved.** A host rule: the spec says nothing about receipt JSON; this keeps
  §10.5's attest step judging the receipt the container actually wrote. A
  top-level duplicate receipt key used to be last-wins, a nested one
  escaped as a raw `ArgumentException`, `1e400` became infinity, `1e-400` became
  zero, and `9223372036854775808` was rounded to a `double` — so the receipt the
  attester judged could carry a value the container never wrote, or read
  differently to another JSON reader of the same stdout. stdout is now parsed
  with `AllowDuplicateProperties = false`, and every number at any depth of the
  receipt *and* of the whole verdict document must be exact: a literal with no
  `.`/`e`/`E` must fit a `long`, any other literal must be a finite `double`
  whose round-trip form denotes the same decimal value. The failures are
  `ContainerExecutionException`s with fixed wording that never quotes the
  offending name or literal: `<stage> stdout had a duplicate JSON property`,
  `<stage> stdout had a number that cannot be represented exactly`, and — for a
  string or name escaping a lone surrogate such as `\uD800`, which used to throw a
  raw `InvalidOperationException` — `<stage> stdout had a string that is
  not valid Unicode`. `okf_run_computation` (`OKF4net.Agents`) holds its parameter
  values to the same rule through one internal normaliser in
  `OKF4net.Attestation`, and reports a rejection as its `Error: …` text instead
  of passing infinity to the binder or throwing at the model. The tool the model
  calls also rejects a duplicate *top-level* parameter name in its raw
  `parameterValues` JSON (a `JsonElement`, `JsonNode` or JSON string) with
  `Error: parameterValues had a duplicate JSON property.`, before
  Microsoft.Extensions.AI deserializes it into a dictionary last-wins —
  `{"n": 1, "n": 2}` used to reach the binder as `n = 2`. The schema the model
  sees is unchanged, and the public `RunComputation`/`RunComputationAsync`
  dictionary API is untouched. Found by an external review.

- **`OKF4net.Attestation`: a declared but unresolvable `attester.resource` now
  fails the run, and `AttestationContext` gained a field.** Both are breaking for
  existing consumers of a published package, so state them plainly:
  - `AttestationOrchestrator` now resolves a concept's `attester.resource` (§6.2)
    and reads it *before* binding or execution, surfacing it as the new
    `AttestationContext.AttesterSourceText`. A declared value that does not
    resolve — missing file, or a path that would escape the bundle — ends the run
    with a non-displayable outcome and nothing executes. **Source-breaking:**
    `AttestationContext` takes a sixth positional parameter, so any host that
    constructs one directly must be updated.
  - The new failure mode reaches hosts that never wanted the bundle's attester
    source, which before this release was all of them: their `IAttester` supplies
    its own implementation, yet a broken `attester.resource` now stops the run.
    Failing closed is the intent — an attester the bundle names but the host
    cannot read is an attestation that was specified and then not performed, and
    §10.6 is about not displaying a figure whose check did not happen. The
    cheapest workaround, deleting the `attester:` block, silently removes
    attestation altogether; fix the path instead.

- **`okf_search` and the agent context provider now return diversified results.**
  Scores are presence-based and capped at 6 per term, so ties are the common
  case, and ties were broken by `ConceptId` order — which is ordinal by segment.
  On a bundle whose concepts are dominated by one id family, that family took
  every slot in the 20-result search window and the 5-concept injection window.
  Measured on a 396-concept bundle: curated concepts held 1 of 55 top-5 slots,
  and 5 of 11 broad queries returned none at all in the top 20; after the change,
  23 of 55 and 0 of 11. The trade is deliberate — a higher-scoring concept can
  now be displaced by a lower-scoring one from a family that would otherwise be
  absent. Small bundles, where every family already fits in the window, are
  unaffected. `okf_search`'s tool description says so now: the printed scores no
  longer descend monotonically, and a model reading that description was
  previously told they would.
  There are **three** truncated windows, not two: the scoped (V2) context
  provider — the one hosts are steered towards, the V1 provider's
  `MemoryDirectory` being `[Obsolete]` — bounds its passage list by token budget
  rather than by slot count, which is a truncation all the same. It is
  diversified too, with `TopDiversifiedBy` so that `KnowledgeQuery.FairnessQuota`
  (which interleaves *sources*, not id families) keeps working alongside it.
  Measured on the same corpus with generated descriptions sharing the curated
  vocabulary: 38 of 336 passages rendered, and zero curated concepts injected on
  6 of 7 broad queries. The memory surface is deliberately *not* diversified —
  `FileMemoryStore` concatenates one ranked list per tier in its read order, so a
  family rotation there would interleave the tiers and override that precedence.

- **Breaking: `okf-mcp` serves a bundle read-only by default.** The four write
  tools (`okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes`,
  `okf_verify`) are registered only when `OKF_MCP_WRITABLE=1` is set. Writes used to be
  the default, which put unconfirmed write access to the corpus behind nothing
  on the surface most people actually deploy — a desktop client's MCP config —
  while bundle content is untrusted by design, so an injection carried in a
  concept body only matters if a write tool is reachable. `OKF_MCP_READONLY=1`
  is still accepted and still forces read-only; it wins over `OKF_MCP_WRITABLE`
  when both are set, so an existing configuration keeps working and keeps
  meaning the same thing. **If you relied on the old default, add
  `OKF_MCP_WRITABLE=1`.**

- **`okf --version` reports the version the build stamped**, read from
  `AssemblyInformationalVersionAttribute` instead of a hand-maintained constant
  in `OkfCli.cs`. `-p:Version` — the property `release.yml` derives from the git
  tag — never touched that constant, so the tag, the NuGet package and the zip
  filename could all say one version while the binary inside said another; the
  winget package for 0.2.0 shipped a binary printing `0.1.0-alpha.1`, caught by
  a Microsoft moderator rather than by CI. Two guards back it up: `release.yml`
  refuses a tag that disagrees with `Directory.Build.props`, and CI's Native AOT
  job runs the published binary's version verb, since an in-process test cannot
  prove the attribute survives AOT. `Directory.Build.props` is now the only
  version to bump in code.

- **Breaking (0.x): `Source`'s constructor and `Deconstruct` arity changed.**
  The `UsageWindow?` member above is appended last with a default, so every
  in-repo construction site (positional or named) still compiles unchanged —
  but the shape is not binary-compatible for anything built against the
  previous six-member `Source`, and it also breaks at **source** level for
  external code that deconstructs a `Source` positionally (`var (id, resource,
  title, author, usageCount, lastModified) = source;`) or pattern-matches on
  its members, since `Deconstruct` now yields seven values. Nothing in this
  repository does either. Consistent with this release's other 0.x breaks (see
  `Lifecycle.StaleAfter` below).

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
- **`OkfCli.Run` gains a `TextReader stdin` parameter** (now
  `Run(args, stdin, stdout, stderr)`), so `verify -` can read concept ids
  from standard input without every other verb paying for a blocking read.
  This is a breaking change to a public API signature, but it breaks no
  external caller: `OKF4net.Cli` is the only project under `src/` with no
  `PackageId`/`IsPackable` — it ships only as the `okf` binary, never
  published as a library — and the sole call site outside `Program.cs` is
  the test suite's `TestPaths.cs`, updated alongside it.
- **`--` now keeps the positionals given before it, instead of discarding
  them.** The separator used to let the token right after it take the single
  positional slot outright, so `okf <verb> a -- b` resolved to `b`; verbs now
  keep every positional in order, `--` included, so the same invocation
  resolves to `a`. This is what makes `verify <bundle> <id>…`'s multiple
  positionals possible — a single "the positional" slot could never have
  held more than one concept id.
- **A lone `-` is now a positional argument, not a flag.** The flag scan
  previously matched any token starting with `-`, including the bare
  character, so `-` was silently absorbed as a valueless, meaningless flag.
  It now falls through to the positional list, which is what lets
  `okf verify <bundle> -` mean "read concept ids from standard input" — the
  POSIX convention — instead of being swallowed before `verify` ever sees it.
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
- **`okfgen` is packaged per-RID** (`win-x64;linux-x64;osx-arm64`), which it was
  not at 0.5.0: the code-graph stage pulls `Microsoft.CodeAnalysis.CSharp` and
  `TreeSitter.DotNet`, and the latter ships native binaries. `producers/` keeps
  every other bit of the status it had at 0.5.0 — its own solution
  (`producers/OkfProducer.sln`), referencing `src/OKF4net` by project reference,
  **not** part of `OKF4net.sln`, **not** in CI (a decision taken 2026-08-01, not
  an omission), **not** published to NuGet, and exempt from the zero-dependency
  rule. `OkfProducer.Core` itself still references only `OKF4net`. Because
  nothing on a pull request builds this solution, the guarantee is one local
  command — `dotnet test producers/OkfProducer.sln` — and `producers/README.md`
  now opens with it.
- **Breaking (producer): `okfgen generate` now runs the scanned repository's
  build logic.** The exact call-site resolver gets its reference set by spawning
  `dotnet msbuild` once per project, in that project's own directory, and an
  MSBuild *evaluation* is the execution of repository-authored logic — there is no
  read-only mode to ask for. `Directory.Build.props`/`.targets` and everything they
  import, any target hooked on `BeforeTargets="ResolveReferences"`, and a
  `RoslynCodeTaskFactory` inline `<Code>` task all run as the user running
  `okfgen`; a `Directory.Build.rsp` in that directory even adds switches to the
  producer's own invocation (measured on this host: a one-line rsp containing
  `-t:Pwn` made the query run a target it never requested). **Only point `okfgen`
  at a repository you would be willing to build.** `--no-msbuild` is the way out —
  no msbuild is spawned and no MSBuild logic from the scanned tree is evaluated —
  at the cost of name-matching-only call resolution (which refuses an ambiguous
  name rather than guessing, so what is lost is edges, not correctness) and of
  emitting no `packages` → namespace containment link at all. It is off by default
  on purpose: defaulting it on would silently degrade every run that exists today.
  Note that `--no-msbuild` does not make the run process-free: `okfgen` still runs
  `git` in the scanned tree (two to five `show -s`/`rev-parse`/`symbolic-ref`
  invocations, depending on the flags) to stamp `overview`. `producers/README.md`
  carries the full threat model.
- **Breaking (producer): `--update` no longer preserves everything.** Under the
  `code` prefix, a concept the previous run claimed and this one no longer produces
  is pruned — otherwise a deleted type would leave its concept behind forever.
  Outside `code`, hand-written concepts are preserved exactly as before. The gate
  is the *traversal*: a run prunes only when it visited every eligible file, and
  one cut short — the extraction budget elapsed, the run cancelled, the walk
  itself failing — prunes nothing, so it cannot delete what it never reached.
  Deliberately not "every file parsed cleanly": the vendored tree-sitter grammar
  mis-parses an empty collection expression, ordinary modern C#, so gating on that
  would make pruning dead code. A file that was visited but not extracted holds
  back only the concepts it owned, one candidate at a time. `--check`
  is refused together with `--reset`/`--force` and with `--no-code`, both of which
  would otherwise let an operator believe something was verified that was not.

- **§6.2 path-valued fields: a bare relative path now resolves from the bundle
  root, not from the concept's directory.** `resource`, `sources[].resource`,
  `computation`, `executor.resource` and `attester.resource` are resolved by
  prefix: a leading `/` → bundle root, an explicit `./` or `../` → the
  concept's own directory, and anything else → bundle root. §6.2 lists the
  three accepted shapes without saying what a "relative path" is relative to;
  the spec's own examples require both bases — `../computations/revenue.md`
  (§6.2) is document-relative, while §6.3's `references/attesters/revenue.py`,
  §10.2's `executor.resource: references/skills/run-on-bq.md` and Appendix A's
  layout (that concept in `computations/`, `references/` at the bundle root)
  only resolve from the root. Resolving everything against the concept
  directory made the spec's own worked example unresolvable, and made
  `bundles/acme_retail/` — a verbatim upstream sample laid out exactly as
  Appendix A is — emit twelve bogus `… not found` warnings. The spec does not
  define the base for a bare path, so this is a change of interpretation
  rather than a conformance fix: §11 puts path resolution outside the
  conformance floor entirely, and `acme_retail` was a conformant bundle before
  and after. What changed is which file a given string names, and therefore
  the diagnostics. Recorded as **S6.2-1** in
  `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md`.
  - Where it reaches beyond diagnostics: `AttestationOrchestrator` resolves a
    file-backed `computation:` path (`AttestationOrchestrator.cs:295`), so a
    bare one now names a different file. It does **not** resolve
    `executor.resource` or `attester.resource` — those implementations come
    from the host runtime — so no computation became runnable or unrunnable
    because of this change.
  - **Breaking (source):** `FrontmatterResourceKind.Relative` is renamed
    `FrontmatterResourceKind.ConceptRelative`, and a bare path now classifies
    as `BundleRelative`. The rename is deliberate: it turns a silent change of
    meaning into a compile error for any consumer that switched on the old
    member. Note the limit of that protection: the enum's numeric values are
    unchanged (`Url=0`, `BundleRelative=1`, the former `Relative=2` now
    `ConceptRelative=2`), so it only bites on recompilation. A consumer still
    binary-linked against the previous assembly gets `BundleRelative` where it
    used to get `Relative` for a bare path, with no error, and changes
    behaviour silently.
  - The drive-relative guard (a raw value like `e:query.sql`, which
    `Path.GetFullPath` resolves against that drive's own current directory)
    now covers both bases rather than only the concept-relative one.

- **`okf validate` no longer reports a missing `resource` on a §10 Attested
  Computation.** §4.1 recommends `resource` but qualifies it in the same
  sentence — "Absent for concepts that describe abstract ideas rather than
  physical resources" — so on such a concept its absence is correct, not a
  deficiency. `Attested Computation` is the one type a rule can be keyed on
  instead of guessed: §10.1 names it normatively, and every example the spec
  gives of one omits `resource`. Other abstract types (`Metric`, `Skill`, …)
  still warn, because §4.1 draws the line by meaning and leaves the type
  vocabulary open, so nothing syntactic decides it. `bundles/acme_retail/`
  goes from 24 warnings to 22. Recorded as **S4.1-8** in
  `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md`.
- **`okf` and `okf-render` now share one `CliArgs` argument scanner**
  (`OKF4net.Internal.CliArgs`) instead of two hand-rolled copies that had
  already drifted: `okf-render` now treats a lone `-` as an argument like
  `okf` does, and a repeated `--out` is refused (`option --out given more
  than once`) instead of silently keeping the first value, matching `okf`'s
  own repeated-flag refusal for every other valued flag.
- **`ContainerExecutionException.ToString()` now includes the container's
  output, so a failed run says why in the host's logs.** It carried only
  "attester exited with code 1"; the container's stderr, where the cause was
  ("No usable temporary directory", a missing table), sat unread on the `Stderr`
  property. It now appends the last 4096 characters of each non-empty captured
  stream. `Message` is unchanged, and so is what reaches the model: the captured
  streams never do — `AttestationOutcome.Reasons` and `okf_run_computation`
  carry at most the library-authored `Message` (see the
  `AttestationDiagnosticException` entry), now guarded by a test that puts a
  secret in a container's stdout and stderr. The container integration tests
  print the exception on failure too, rather than only the `Reasons`.
- **`okfgen`'s package-child minimality filter is now `O(k)` instead of `O(k²)`
  per package.** `ConceptGenerator.AttributePackages` computes, for each
  package, the raw paths "minimal under the ancestor relation" it owns — a
  path whose own ancestor the same package also claims is dropped, since it is
  already reachable one level down from that ancestor (§5.2 of the producer's
  code-graph design, `docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md`,
  not of the OKF spec). That used to be
  a pairwise `keys.Where(key => !keys.Any(other => IsProperAncestor(other,
  key)))` scan. The extracted `ConceptGenerator.MinimalUnderAncestry` instead
  makes one pass over the already-sorted `SortedSet<string>(Ordinal)`,
  tracking only the most recently kept key: because the raw-path keys are
  joined with `NUL` (the smallest character under `Ordinal`), every
  descendant of a kept key sorts contiguously right after it, so comparing
  each next key against `lastKept + NUL` (not `lastKept` alone, which would
  wrongly drop a sibling like `A\0Ba` immediately after `A\0B`) is enough — no
  quadratic re-scan of the whole set per key. Registration
  (`registeredByRawPath.ContainsKey`) is still applied after minimality, so an
  unregistered ancestor still suppresses its descendants. Behaviour is
  unchanged — pinned by an equivalence property test against the original
  pairwise expression over several thousand random key sets, and the golden
  captures and `DeterminismTests` move by zero bytes (`producers/`).
- **`okfgen` now has one bounded child-process runner and one link-ancestor
  walk, instead of drifted copies (`producers/`).** `GitRevision.RunGit` and
  `MsBuildProjectQuery.Run` each carried their own start/drain/timeout/kill
  code; both now call the internal `BoundedProcess.Run`, which redirects and
  closes stdin, drains stdout and stderr concurrently under caps, bounds the
  whole call (reads included) by its timeout, and reports
  `Completed`/`NotStarted`/`TimedOut`/`Faulted` rather than throwing —
  `MsBuildProjectQuery` maps those to its existing messages verbatim. It holds
  no shared state and is safe for concurrent use. On giving up it kills the
  process tree **as it stands at that moment**: on Windows and POSIX a
  descendant of a still-running child dies with it, but on POSIX a
  descendant whose own parent already exited has been re-parented away and
  survives (measured on Linux), so there the call is bounded while a
  pipe-holding orphan may outlive it — unchanged from both former copies.
  The repository-containment question — which `RepositoryScanner`'s
  solution-project filter, `CompilationFactory`, `RoslynResolver` and
  `SourceOwnershipMap` each answered with their own code — is now answered
  once, by `BundlePaths.TryGetPathUnderRoot`; `BundleWriter` alone keeps the
  deliberately different `BundlePaths.IsInside` (strictly under an
  already-resolved bundle root, the root itself excluded). The count-bounded
  link-ancestor walk `CompilationFactory` and `TreeSitterExtractor` each
  re-implemented lives once too, as `BundlePaths.HasLinkAncestor`
  (`OkfProducer.Core` grants `InternalsVisibleTo` to the two code-graph
  projects rather than making either public). The walk is bounded by a
  directory count, never by meeting a root string, which is what once made
  `CompilationFactory` walk to the filesystem root for out-of-repository
  `Compile` items — that bug is now pinned by a regression test. Behaviour
  changes, all at the edges:
  - an ancestor (or the file itself) whose metadata cannot be read — a
    `chmod 000` directory above it on POSIX, an inheritable deny-read ACE on
    Windows — now counts as a link, so tree-sitter reports the file as
    `SkippedSymlink`, the Roslyn engine drops the `Compile` item, and a key
    file there is not used. For those two shapes the file was unreadable
    anyway, but not for every shape: on Windows a file beneath a level whose
    read-attributes right is denied, under a parent that denies listing, still
    opens by path, and **was read before** — including from outside the
    repository through a junction (see Security). Such files are now refused
    even when readable, deliberately, since the escape and a harmless
    directory with the same ACEs cannot be told apart. A directory that merely
    cannot be *listed* while its files stay readable by path is not refused;
  - any first path segment other than exactly `..` is a name, not a climb —
    `..foo`, `...`, `.. `, and on POSIX `..\x` (a backslash is a filename
    character there) — so such paths are inside the repository for every
    caller; `SourceOwnershipMap` still refuses the POSIX `..\x` case, because
    its cross-platform `\`→`/` join fold would key it as `../x/…`, spelled as
    a climb (the file is simply not owned);
  - a repository-rooted path the platform rejects (a NUL in it) is "not in
    the repository" for `RoslynResolver` and `SourceOwnershipMap` instead of
    throwing `ArgumentException`;
  - the solution-project filter now also calls an unnormalised
    `repo/../other/P.csproj` outside (it only ever sees `GetFullPath`'d
    paths, so no scan result changes on Windows or Linux);
  - the `dotnet msbuild` child now gets a closed stdin; `git` answers are
    capped at 64 KiB and a failed `git` pipe read yields the outside-git
    fallback instead of an exception;
  - the MSBuild reads gain the `WaitAsync` bound only the `git` copy had —
    insurance rather than a fix: on Windows and on Linux (.NET 10) the reads'
    cancellation token was measured to bound a grandchild holding the pipe on
    its own.

  The `..foo` ownership fix is listed under Fixed. The golden captures move by
  zero bytes. Measured per file on a 12-level path, the walk costs x1.02 the
  tree-sitter copy it replaced on Windows and x1.63 on Linux: each level is
  classified by one attribute read, and only a level carrying the reparse-point
  attribute is asked for its link target.

### Fixed

- **The YAML emitter now quotes a frontmatter key the parser would read back
  as something else (§4.1).** A key was judged as if it were a value, so one
  containing `[`, `{`, `"` or `'` after its first character was written plain,
  and the parser then read `a[b: v` as the single string `a[b: v` or rejected
  the document. A string key that was already written plain is now
  double-quoted, with the same escapes as a quoted value, when the parser's own
  `key: value` split does not end at that key's colon. For such a key that is
  exactly when it leaves a quote open (a `'` or `"` with no closing quote later
  in the key, where a doubled `''` inside `'` and a `\`-escaped character inside
  `"` do not close it) or leaves a flow level open (outside quotes, a `[` or
  `{` not balanced by a later `]` or `}`; either closer closes either opener,
  and one with nothing open is ignored). So `a[b`, `a"b` and `a[b]]c[` are
  quoted, while `a[b]`, `a"b"c` and every ordinary key keep their plain form and
  no emitted output changes for them. A deterministic 10 000-string fuzz went
  from 496 failures to 0 at each of the three key positions (top-level, nested,
  in a sequence item's mapping), and stays at 0 for values, sequence items and
  whole-document scalars.
- **`okfgen generate` no longer builds the repository it is scanning, so a run
  writes no file into that repository's `obj/` or `bin/` (`producers/`).** The
  MSBuild query asks for `-t:ResolveReferences`, which depends on
  `ResolveProjectReferences` — and outside Visual Studio `BuildProjectReferences`
  defaults to `true`, so that target *built every referenced project*. Measured
  on SDK 10.0.204: one resolver stage over a restored, never-built three-project
  chain wrote **39 files** into the scanned tree — `bin/`, the full
  `obj/Debug/<tfm>/` compile output of both referenced projects, `ref/` and
  `refint/` assemblies included — for a repository `okfgen` was only asked to
  read. The query now passes `-p:BuildProjectReferences=false`, and what MSBuild
  still generates for the queried project itself (`*.GlobalUsings.g.cs` and
  `*.AssemblyInfo.cs` — `Compile` items that must exist on disk for Roslyn to
  parse them, and which no switch produces without writing) is redirected into a
  per-run `okfgen-msbuild-*` directory under the system temp that the producer
  deletes when the stage ends. That path is escaped the way MSBuild expects
  (`%XX`, `%` included): unescaped, MSBuild *decodes* `%XX` in a `-p:` value, so
  a temp directory holding `%41` sent the files to a sibling of the scratch (and
  `%2E%2E` out of it) where nothing ever deleted them, and one holding `;` failed
  every query with `MSB1006` — both measured, both pinned. What the query still
  creates in the scanned repository is **empty directories**: a
  `bin/<Configuration>/<TFM>/` per never-built project, from `PrepareForBuild`'s
  `<MakeDir Directories="$(OutDir);…">`, never written into. They are left on
  purpose: redirecting `OutDir` too removes them but moves the referenced
  projects' `ReferencePath` into the scratch (measured) — the assembly
  `CompilationFactory` falls back to on a built tree. A killed run's leftover
  scratch is swept by a later run once it is a day old, touching only directories
  named exactly as the producer names them and never following a link. On a
  restored-but-never-built tree, a project whose dependency cannot be compiled
  from source is now reported `ReferencesUnresolved` (that dependency's `bin/`
  assembly used to exist because the query built it), and its note says to build
  the repository once and re-run. The reference set is unchanged, not merely
  similar: measured before and after, 169 `ReferencePath` items on that
  three-project chain and 213 on this repository's own `src/OKF4net.Mcp`, with
  identical `Identity` sets, identical resolved properties, and the transitive
  project reference still tagged with its `.csproj` so `CompilationFactory` keeps
  substituting a from-source compilation for it. `-p:DesignTimeBuild=true` was
  measured to stop the same builds and was *not* chosen: it is a signal
  repository-authored targets routinely condition on, so it would quietly change
  what the scanned repository's own logic does while buying nothing extra. Pinned
  by an acceptance test that snapshots the scanned tree around a whole resolver
  stage — files with their sizes and last-write times, and directories, so the
  empty output directories are a named exception rather than an invisible one.
  What this does **not** bound is
  what the repository's own MSBuild logic writes while it is evaluated — see
  `producers/README.md`, "Generating from a repository runs that repository's
  build logic".
- **`--roslyn-timeout` is pinned to bound the project-query stage, checked
  between individual project queries (`producers/`).** `GenerateRun` hands every
  detected project in as a query root, so nothing is discovered transitively and
  the closure is a single pass: a deadline consulted once before that pass would
  leave the option bounding only the compilations after it. The serial loop does
  consult `StageDeadline` at the top of every iteration, and nothing said so — an
  abandoned stage returns nothing at all, so a run that queried one project of
  three and one that queried all three were indistinguishable from outside. The
  loop now reports how far it got, and a test holds it: three projects, a budget
  larger than the loop's own set-up and smaller than one `dotnet msbuild`
  invocation, one query run, the stage abandoned whole and reported degraded
  exactly as before. Moving the check back out of the loop turns that count into
  three and the test red.
- **`okfgen`'s MSBuild query now requests `AddImplicitDefineConstants`, so
  `#if NETx_OR_GREATER` compiles correctly under an SDK 8 toolchain
  (`producers/`).** `MsBuildProjectQuery`'s target list
  (`ResolveReferences`/`GenerateGlobalUsings`/`GenerateAssemblyInfo`) never ran
  `CoreCompile`, and SDK 8.0.425 wires `AddImplicitDefineConstants` to
  `BeforeTargets="CoreCompile"` — so on that SDK line, `DefineConstants` came
  back as just `TRACE;DEBUG;NET;NET8_0;NETCOREAPP`, missing every
  `NETx_OR_GREATER` symbol, and a repository whose `global.json` pins SDK 8
  had every such branch compiled the wrong way (a false `CompilationHadErrors`
  on `#error` guards, or worse, a silently wrong branch on a plain `#if`).
  Measured, not assumed to be `GenerateAssemblyInfo`-related as first
  suspected: SDK 9.0.318 and 10.0.204 already carry the full implicit set
  regardless, because dotnet/sdk#43908 moved the same target to
  `AfterTargets="PrepareForBuild"`, which `ResolveReferences` already depends
  on. Requesting the target explicitly closes the SDK 8 gap and is a measured
  no-op (no duplicate defines) on SDK 9.0.3xx+/10. No hand-maintained
  per-TFM fallback table — the target is public on every SDK that supports
  `-getProperty`, and reimplementing its rules would fork them.
- **`okfgen`'s Roslyn stage now signs its analysis-only compilation, so a
  project's own `InternalsVisibleTo` friend grant resolves instead of failing
  `CS0281` (`producers/`).** `CompilationFactory.Create` built every
  `CSharpCompilationOptions` with no key at all, so a signed consumer calling
  an internal member of a friend assembly it names via
  `InternalsVisibleTo("Consumer, PublicKey=…")` still compiled as if it had no
  public key (`""`), and Roslyn reports `CS0281` — measured against Roslyn
  5.3.0 in the pre-flight. `MsBuildProjectQuery` now also queries
  `SignAssembly`, `KeyOriginatorFile` (preferred — it is the value
  `Microsoft.Common.CurrentVersion.targets` actually passes `csc`'s
  `/keyfile`) and `AssemblyOriginatorKeyFile` (fallback), and
  `CompilationFactory.Create` always **public-signs** with the resolved key —
  never the project's own `DelaySign`/`PublicSign` — since this compilation is
  never emitted and public signing alone (no `StrongNameProvider`) resolves the
  friend grant without the `CS7027` a bare `.WithCryptoKeyFile` adds. A key
  file that is missing, outside the repository root, or reached through a
  reparse point (a directory junction/symlink above it) is never read — the
  compilation degrades to unsigned exactly as before E7, rather than trading
  today's partial success (the name-matching baseline) for a whole-project
  `CS7027` failure, and rather than following repository-controlled data
  outside the tree `okfgen` was asked to scan.
- **A stage that ignores cancellation no longer keeps a run alive past
  `ComputationTimeout` or the caller's token, and can no longer turn a
  cancelled run into a displayable outcome.** A host guarantee: §10 sets no
  time limit or cancellation rule. The orchestrator checked
  the token only *before* each stage, so a binder/executor/attester already
  running when the token fired ran to completion and its result was used: under
  a 30 ms `ComputationTimeout`, an attester sleeping 350 ms made
  `okf_run_computation` return after ~350 ms with `displayable: yes`, and an
  attester that cancelled the caller's token and returned a passing verdict
  yielded an outcome that was both cancelled and displayable. Each stage is now
  started on the thread pool and awaited through `Task.WaitAsync`, so the run
  stops waiting the moment the token fires — including for a stage that blocks
  its thread before returning anything, such as a synchronous client wrapped in
  `ValueTask.FromResult` — and the token is re-checked after each stage that
  succeeds. The cost is one thread-pool hop per stage when the token can be
  cancelled; an abandoned blocking stage keeps its pool thread until it returns.
  Once the orchestrator has seen the token fire — while a stage runs, or after
  it succeeded — the run ends as a cancellation whatever the stage does
  afterwards, a later failure included: the caller's
  `OperationCanceledException`, or `displayable: no … timed out` when the tool's
  own `ComputationTimeout` fired. Only a stage failure that had already
  completed before the token was seen is reported as that failure;
  non-displayable either way. Abandoning a stage
  does not stop its work — nothing can force host code to return. A stage that
  honours its token ends on its own: `OKF4net.Attestation.Containers`' engine
  kills its container, bounded by its kill timeout, and the run simply no
  longer waits for that teardown (the engine's per-run `Timeout` is the
  backstop). The abandoned task is observed, so a later fault cannot surface as
  an unobserved task exception.
- **Attested-computation staleness is now evaluated when the outcome is
  released, not when the run started (§5.5).** `RunAsync` read `_clock.Now`
  once, before binding, and reused that instant for both `Outcome.Stale` and
  the staleness gate after every later stage — so a run started one second
  before a concept's `stale_after` whose stages (bind/execute/attest) took two
  seconds was still released `Fresh` and displayable, even though the concept
  was already stale by the time anyone saw the result. The clock is now read
  immediately before the gate on the success path, and again at the point each
  post-stage failure outcome is built, so a run that crosses `stale_after`
  while it is in flight is caught at release time either way. Not a change to
  early failures (concept not found, unresolved computation, unregistered
  runtime, missing required parameters), which still report `StaleState.Unknown`
  without reading the clock, and not an early refusal of an already-stale
  concept — a run still executes and reports it as stale at the gate.
- **Invalid UTF-8 on a container's stdout fails the stage instead of being
  replaced with U+FFFD in the receipt.** A host rule, like the strict receipt
  JSON above: the spec defines no receipt encoding. `CliContainerEngine` decoded
  stdout with a replacement fallback, so a container writing the bytes
  `{"x":"\xff"}` produced the receipt `{"x":"\uFFFD"}` — the engine silently
  rewrote the data the attester authenticates. stdout is now decoded strictly,
  and an invalid sequence (a multi-byte sequence cut off by the end of the
  stream included) is a `ContainerExecutionException` "container stdout was
  not valid UTF-8 (exit code N)", reported after the process exits. The pipe
  keeps being drained past the bad bytes, as after the output ceiling, so the
  child never blocks on a full pipe and the failure cannot turn into a timeout.
  stderr
  keeps the lenient decoder: it is host-side diagnostics, never authenticated.
  A leading UTF-8 byte-order mark is still skipped as before; a UTF-16 or
  UTF-32 one, on which the previous reader silently switched encodings, is now
  invalid UTF-8 like any other stray byte.
- **`okfgen` resolves `git` on `PATH` itself, never from the scanned tree or
  a drive-relative entry.** A bare `Process.Start("git")` let the OS search
  the current directory before `PATH` — closed on every platform .NET
  supports, not only Windows: the same current-directory search is part of
  .NET's own bare-name process lookup on Unix too, so a `git` script
  committed in the scanned repository could run there as well as on
  Windows, whenever `okfgen` was launched from inside that checkout —
  `--no-msbuild` included. The Windows-specific resolver also rejected a
  bare `.`/empty `PATH` entry but missed a **drive-relative** one
  (`E:tools`, `E:.`), which `Path.IsPathRooted` calls rooted and which
  still resolves against the current directory's own drive; now checked
  with `Path.IsPathFullyQualified` instead. On Unix, a `PATH` hit is also
  now required to carry the execute bit, matching the OS's own lookup, so a
  non-executable `git` earlier on `PATH` can no longer shadow the real one
  (`producers/`).
- `okfgen generate --out dir/` (a trailing directory separator, as shell
  completion writes it) wrote nothing and blamed a symbolic link; `--repo
  dir/` never pruned a deleted file's concept; the staging directory landed
  inside the bundle; and the bundle it did write was missing its root
  `index.md` (`IndexGenerator.RegenerateIndexes` received the same
  untrimmed path and stopped its directory walk one level short of the
  root). All were `Path.GetFullPath` preserving the trailing separator,
  which then broke a path comparison somewhere downstream — `BundlePaths`'
  and `BundleWriter`'s own, and (for the index) the one `IndexGenerator`
  makes internally, closed here by normalising `outPath`/`repoPath` once at
  `BundleWriter.Write`'s and `GenerateRun.Execute`'s own entry points
  (`producers/`). `IndexGenerator` now also normalises its own root, for
  every caller — see the `okf index dir/` entry.
- The viewer sanitizer unwraps a disallowed element instead of flattening its
  subtree to text (a link or table inside `<details>`/`<div>` survives). It
  marks disallowed elements, then detaches every node bottom-up and
  re-appends each kept node top-down under its nearest kept ancestor, so
  every unwrap mutation moves a single childless node (removing an opaque
  element still drags its subtree, innermost opaque element first): on the
  harness's shapes (wide, deep chain, alternating, nested opaque) sanitizing
  drags 1.0–1.9 × N nodes for an N-node body.
  Two intermediate, never-released versions of this same fix unwrapped one
  element at a time instead and were superlinear, because moving a node
  drags its whole subtree: innermost-first and outermost-first alike dragged
  the same nodes again for every enclosing wrapper (up to 44.5 × N and
  201 × N nodes respectively on the harness's shapes). Its
  constraint and attribute allowlists, and `isSafeUrl`'s own scheme table,
  are own-property lookups throughout (`<input type="constructor">` and
  `<input type="__proto__">` no longer survive as elements — both used to
  resolve an inherited `Object.prototype` member instead of failing the
  check; `constructor=`/`__proto__=` attributes no longer pass either).
  `<script>`/`<style>` in a foreign namespace and raw-text elements
  (`iframe`, `xmp`, `noembed`, `noframes`, `plaintext`, `template`, `base`)
  are dropped with their source — `<plaintext>` now drops the *rest of the
  body* it swallows instead of showing that tail as source text, since the
  HTML parser never leaves the PLAINTEXT tokenizer state once it sees one.
  Every scheme-obfuscation rule, `<style>` (HTML and SVG context),
  `<plaintext>`, a corrected `<noframes>` case, five mutation-XSS re-parenting
  payloads, a disallowed element nested inside an opaque one, a sanitize root
  detached from any document, a phase-2 consistency check reached by fault
  injection, and four unwrap-cost cases now have a harness
  case in `tools/viewer-security-check/run.js`. The unwrap-cost cases count
  the nodes every DOM mutation drags during sanitizing and bound them at
  4 × N — deterministically, never by wall-clock time — and fail against
  both intermediate versions; the harness also now rejects any surviving
  foreign-namespace element. None of this was an exploitable XSS: these were
  gaps between the sanitizer's comments and its code, plus a performance
  regression and a latent fail-open (on a detached root, which the viewer
  never passes) introduced and caught within this same unreleased change.
- `OkfContextProvider`'s budget truncation now splits a concept's body on
  `LfLines.Split` instead of a bare `'\n'`, so a CRLF-bodied concept truncated
  to a small token budget no longer keeps a stray trailing `\r` on its last
  kept line.
- `okf_audit`'s `type` filter is now trimmed like `status`/`trust` already are
  — a model copying a label from prose that brings surrounding whitespace no
  longer silently selects nothing.
- `StalePolicy.Tolerate`'s grace window now excludes its far edge, like
  `Lifecycle.IsStale` (§5.5: `now >= stale_after`) — at the exact instant
  `stale_after` falls due, `Tolerate(0)` used to admit a concept `Strict`
  already excluded.
- A §5 timestamp with no date part (`stale_after: 10:00Z`) is now unreadable —
  warned and never evaluated — instead of being read as today at that time,
  which made staleness flip during the day, per machine, ignoring `--as-of`.
- `okf verify`, `okf_verify` and `RecordVerifications` now quote-escape a
  concept id in every error they echo, closing for the positional the
  line-forging hole `LineSafeText` closed for `--by`/`--at`; `okf verify -`
  tolerates a UTF-8 BOM on the first line.
- `okf_run_computation` renders list- and object-valued receipt fields as
  compact JSON instead of the CLR type name, so a SQL result actually reaches
  the model. The same rendering pass now also prints a boolean receipt field
  lowercase (`true`/`false`, not the CLR `True`/`False`) and a numeric one
  under the invariant culture rather than the current thread's, so a
  double-valued field no longer prints a locale-dependent decimal separator
  (e.g. `0,95` under `fr-FR`) into a receipt a model has to re-parse.
- **`okf_run_computation` now delivers native CLR parameter values to the
  binder.** `AIFunctionFactory` binds an `object`-typed dictionary's values as
  `JsonElement`s, which `OKF4net.Attestation.Containers`' allowlist binder
  rejected for every declared `type` (`integer`, `string`, `boolean`,
  `number`) — the container runtime was unusable through the tool and MCP for
  any typed parameter, while the same call from C# succeeded. Values are now
  normalized once in the tool (`ParameterValues`), for every binder.
- **An inline link destination in angle brackets loses its brackets.**
  `[x](<../glossary/term.md>)` was extracted with target `<../glossary/term.md>`,
  which never resolves: a false broken link, no backlink, and a dead link in the
  viewer (documented there as a known gap, now removed). The destination may hold
  spaces and parentheses (`[x](<a (b).md>)`), a `<` inside it makes it invalid,
  and a `<` that opens a valid one is no longer mistaken for inline HTML
  (`[b](<my file.md>)` read `<my file.md>` as a tag and hid the link). And a setext
  underline under a paragraph holding only link reference definitions no longer
  ends that paragraph — with nothing left to head, commonmark.js reads it as
  continuation text, so a definition after it defines nothing.
- **Inline links follow CommonMark's link algorithm (§6.3).** The scan matched
  each `[` to its balanced `]` and took whatever balanced parentheses followed,
  which differed from every markdown renderer in four ways, all now fixed:
  - a destination with spaces was accepted — `[a](not a link)` linked to
    `not a link`; a destination now has no spaces, parentheses only when balanced
    or escaped, backslash escapes resolved, and a title only after whitespace;
  - a link inside a link's text kept the outer link — `[a [b](x) c](y)` linked to
    `y`; a link now deactivates the brackets opened before it, so only `x` is a
    link (an image may still hold one, as in `![[a](x)](y)`);
  - a link could not cross a line ending — `[orders\ntable](x)` was missed;
    inline content is now read a paragraph at a time, past quote and list
    markers, and a link's text is reported on one line;
  - an escaped `\[` opened a link — `\[a](x)` linked to `x`; an escaped bracket now
    opens and closes nothing.
  Code spans and raw HTML are now resolved in the same left-to-right pass as the
  links, where they used to be blanked first: whichever starts first wins, and
  what a link consumes after its `]` is neither text, span nor tag. So a
  destination with a backtick no longer pairs with a later one
  (`` [a](x`y) [b](/b.md)` `` lost `/b.md`); a `<` after a `]` that closes no
  link is a tag again (`foo](<a title="[in](/in.md)">)` linked to `/in.md` —
  raised by Copilot on #105); and a `[^k]` inside a link's destination
  (`[t](a[^k]b)`) is no longer counted as a citation. Footnote visibility still
  matches commonmark.js on 200 000 random bodies of code, HTML and containers.
  The scan follows commonmark.js's bracket algorithm (a stack of openers,
  resolved at each `]`) with every search for an end a table lookup, so it stays
  linear however the brackets nest; a link's text is capped at 2 000 characters
  for display, so a pathological nest of images cannot make its copies
  quadratic. The pre-CommonMark oracle test was replaced by one that checks the
  tables against the same algorithm with every search written out.
- **Link scanning is linear on unclosed brackets.** Every `[` restarted a
  balanced scan to the end of its line, so a line of brackets that never close
  was quadratic: a 200 KB `[a[a[a…` concept took ~11 s to `okf validate`, and
  bundle content is untrusted input — a CI job validating a contributed bundle
  could be stalled by one line. The behaviour dates from the initial port, so
  every release has it. The closer each opener would reach is now precomputed in
  one pass; the same bundle validates in 0.3 s. The links found were unchanged
  by that fix alone — checked against the original algorithm on 20 000 random
  lines, and `okf graph` / `okf validate` output byte-identical on every bundle and
  fixture — before the move to CommonMark's link algorithm above changed them on
  purpose.
- **`okf validate` now reads the body of an `index.md`, and checks §8's entry
  rule.** It never had: the reserved-file check returned early for any index
  without frontmatter, which is every well-formed index, so no index body was
  ever inspected. An entry linking to a concept that has a `description`, while
  carrying no description text itself, now raises `IndexEntryMissingDescription`
  (warning — §8 is a SHOULD, never a §11 rejection). It checks that a
  description is **present**, not that it is a verbatim copy: §8's own
  illustration is "`- short description of item 1`", and upstream samples
  shorten. Entries resolve exactly as concept links do (§6.1), and an entry
  pointing at a non-concept — a subdirectory's index, a script, `log.md` — is
  not checked, since §8 speaks of the linked *concept's* frontmatter. The
  conformance report had marked this Implemented on the strength of
  `IndexGenerator`, which was true for generated indexes and false for
  hand-written ones.
- **`okf validate` warns when a footnote cites a source that has no `id`.** §5.1
  says a source's `id` "SHOULD be present when the body cites the source", and
  §4.2 makes footnotes keyed to `sources` the citation mechanism, so a
  `[^key]` with no matching `sources[].id` is a claim attributed to nothing. It
  raises `CitationMissingSourceId` (warning). Code is skipped, so `[^a-z]` — a
  negated character class, ordinary in a regex or SQL pattern — is never read
  as a citation. Both new checks emit nothing on `bundles/acme_retail`,
  `bundles/ga4`, or any validated golden fixture, and both new `DiagnosticCode`
  members are appended so existing members keep their numeric values.
- **A configured `ContainerIsolation.TmpfsMounts` (reached through `Isolation`)
  is now honoured inside the containers, not just
  mounted.** The engine mounted whatever the host named, but the Python inside
  kept writing to `/tmp`: with `Isolation.TmpfsMounts = ["/scratch"]` under the default
  read-only root, the attester bootstrap's `NamedTemporaryFile` failed every run
  ("No usable temporary directory"), and so did the `SqlClient` wrapper — pip
  unpacks in the temp directory, so even a correct `--target` failed. The first
  mount is now passed into every container as `TMPDIR` (by
  `ContainerIsolation`, for all three stages), which every writer
  inside follows; a `TMPDIR` set in `Environment` wins. Each entry must be an
  absolute container path (optionally `:options`), and an **empty**
  `Isolation.TmpfsMounts` under a read-only root is rejected when a `ContainerAttester` is
  built — its bootstrap writes on every run, so it could never attest, and would
  only discover that after the computation had already run. A
  `ContainerAttesterOptions` whose `Isolation.ReadOnlyRootFilesystem` is set is
  rejected the same way when Python's `tempfile`
  reaches no writable mount from its `TMPDIR` — none of `TMPDIR`, `TEMP`, `TMP`,
  `/tmp`, `/var/tmp`, `/usr/tmp` is a mount without the `ro` option, nor docker's
  own `/dev/shm`: a `TMPDIR` set in `Environment` wins, `tempfile` never creates
  it, and the run failed the same way (`TMPDIR=/work` with only `/scratch`
  mounted, or `/scratch:ro`). `Environment` is copied when the attester is
  built, so the check cannot be bypassed afterwards. The check rejects only what is
  sure to fail: a `TMPDIR` rescued by a later candidate, such as a mounted
  `/tmp`, is accepted, and paths are resolved as `tempfile` resolves them
  (`scratch` and `/work/../scratch` both reach `/scratch`). What it cannot see —
  an image `WORKDIR` or `ENV`, and podman's extra default tmpfs mounts — is
  listed in the project README.
- **`bundles/meridian_transit`'s fare-cap attester now verifies the per-trip
  split in order.** It checked the total, the reconciliation and each charge's
  bounds, but never the order, so a statement charging `[0, 250, 250, 200]` for
  four 250 fares against a 700 cap passed alongside the correct
  `[250, 250, 200, 0]` — the very breakdown the policy says a rider disputes. It
  now recomputes the sequential split and compares element for element, and a
  malformed receipt is a failing verdict rather than an exception.
- **Bundle attesters no longer accept a value that is not a genuine integer as a
  count.** `meridian_transit`'s ridership attester read counts through `int()`,
  so `"5"` passed and `2.9` was silently truncated to `2`;
  `attestation_containers_demo`'s active-user attester rejected those but, like
  any plain `isinstance(_, int)` check in Python, accepted `true` and `false`,
  since `bool` subclasses `int`. Both now reject strings, floats and booleans.
- **The link, citation and index scanners now recognize code and escapes as
  CommonMark defines them.** Raised by Copilot on #98 and by review of #99,
  each confirmed by a failing test first. `CitationMissingSourceId` warned on footnote syntax
  markdown does not render as a footnote: an escaped `\[^a-z]`, an indented code
  block, or a double-backtick span (the one-character code toggle left
  `` `` [^x] `` `` visible). A fence closed on any line opening with three
  backticks, so a ```` ``` ```` inside a four-backtick fence — or a
  ```` ```python ```` line inside a plain one — ended it early and the rest of the
  code was scanned as prose. An unmatched backtick hid the rest of its line, links
  included. And in an `index.md`, a tab after the list marker was not a list item,
  and a description wrapped onto the next line read as missing, so
  `IndexEntryMissingDescription` fired on an entry that has one — or, when the
  line after it was code, took the prose after the code as its description. The
  pass now tracks open list items by the column their content starts at, and
  whether a paragraph is open, and measures indentation from the innermost item:
  a fence opens and closes only within three columns of it, closes only on a run
  of the same character at least as long with no info string, and ends with its
  list item, including one opened on the marker's own line (`` * ``` ``); four
  columns is indented code wherever no paragraph is open (after a heading or a
  closing fence as much as after a blank line) and continuation text inside one;
  a code line leaves an empty line behind, so it still separates what surrounds
  it. Code spans are matched over a whole paragraph, so one may cross a line
  ending but never a block boundary; they match runs of equal length, an
  unmatched run is literal, and a backslash escapes an opener but never a closer
  (`` `C:\` `` is a complete span). Block quotes are containers like list items,
  so a fence or indented code inside `>` is code and ends with the quote, while
  quoted prose — lazy continuation lines included — is still read. Raw HTML is not
  markdown either: an HTML block (a comment, `<script>`/`<pre>`/`<style>`/
  `<textarea>`, `<?…?>`, `<!…>`, CDATA to its end marker; a block-level tag, or a
  complete tag alone on its line, to the next blank line) hides what it holds,
  and so does inline raw HTML in a paragraph — a comment, a tag's attribute values
  — matched left to right against code spans, whichever starts first. So a
  `[^x]` inside `<!-- -->` is no longer a citation, and the `<details>` pattern
  still reads the markdown after its blank line. Tabs count to the next multiple
  of four from the start of the line and may be partly consumed by a container;
  a setext underline ends its paragraph; an ordered list interrupts a paragraph
  only from 1; an empty list item ends at a blank line; and a link reference
  definition's destination and title are not text. Checked against commonmark.js
  0.31.2 on 300 000 random bodies: no difference in which footnote references are
  visible, reference links (`[text][label]`, since read — see Added) aside.
  Nesting deeper than 100 containers is read as text, as markdown-it limits it.
  All of this lives in the one shared "skip code" pass, so
  `okf graph`'s links change too; its output and `okf validate`'s were compared
  before and after on every bundle in `bundles/` and every fixture, and are
  byte-identical. The pass is linear on hostile input: a first version of code-span
  matching searched for each opener's closer from scratch (14 s on a 1.4 MB line
  of unclosable backtick runs), and the ATX heading regex's lazy `(.*?)` retried
  its closing sequence at every character (25 s to scan `# a`, 150 000 spaces, `x`);
  both are hand-written scans now, with tests. Container matching reads a bounded
  stretch of the line per container (a first cut took 5.8 s on one line of 75 000
  nested `> - ` markers) and nesting is capped (80 000 nested items followed by
  160 000 blank lines took over a minute), HTML end markers and closing quotes are
  table lookups, and a fuzz test checks no extractor throws on random markdown.
- **`bundles/meridian_transit`'s fare-cap attester rejects negative amounts.**
  Raised by Copilot on #98: it checked `cap_cents` was an integer, never that it
  was a cap, so a cap of `-1` recomputed to an all-zero split that a matching
  receipt passed on every check. A negative cap or fare is now unusable input; a
  cap of `0` still passes. `attestation_containers_demo`'s active-user attester,
  whose boolean guard nothing executed, now runs as it ships in a Docker-gated
  test with a genuine count as control.
- **§4.1's `resource` carve-out for an Attested Computation now applies only to
  an ABSENT key.** The suppression added for S4.1-8 skipped the field before
  reading its value, so a §10 concept declaring `resource` with an unusable
  value — `resource: ""`, a bare `resource:`, an explicit `resource: null`,
  `resource: []`, `resource: {}`, and, because `YamlValue.IsEmptyValue` is a
  falsiness test rather than an emptiness one, `resource: false` and
  `resource: 0` — was silently accepted. §4.1 licenses *absence* ("**Absent**
  for concepts that describe abstract ideas rather than physical resources");
  it specifies the present form as "a URI that uniquely identifies the
  underlying asset", which none of those is. A declared-but-unusable
  `resource` is a malformed value, not a statement of abstractness, so it
  warns again — identically to `title`/`description`/`tags`, which have always
  warned on an explicitly empty value. The carve-out now moves one axis only
  (key presence) and never suppresses the value check.
  `bundles/acme_retail` is unaffected at 22 warnings: its Attested Computations
  omit `resource` entirely.
- **`YamlEmitter`'s nesting guard now throws `YamlEmitException`** (an
  `OkfException`, like the parser's `YamlParseException`) instead of a bare
  `InvalidOperationException`. The parser enforces its 1000-level cap with two
  independent counters — one for block nesting, one for flow — while the
  emitter has a single counter covering both, so a frontmatter mixing the two
  can parse and then fail to re-emit. That exception matched no catch filter
  in the library: it escaped `BundleConceptWriter`'s errors-as-data contract,
  threw out of the `okf_verify` tool into its host, and killed the CLI with a
  stack trace. Every existing filter already covers `OkfException`, so the
  failure is now data on all three paths. Reconciling the two counters with
  the one is a separate, read-path question and is left open.
- **`okf` reports an unanticipated library failure as `error: <message>`,
  exit 1**, instead of a stack trace and exit 127. `OkfCli.Run` caught only
  its own internal `CliOperationException`; it now also catches
  `OkfException`, the library's expected-error base. Applies to all eight
  verbs. An unexpected BCL exception still crashes loudly, on purpose.
- **`generated.by` is an actor again, and the engine versions moved to
  `generated.engines`.** §5.2 makes that field an actor and §7 defines an actor as
  exactly one of `<producer>/<version>`, `human:<id>`, `process:<id>`. It was written
  as `okfgen/0.1.0 tree-sitter/1.3.0 roslyn/5.3.0`, which is none of them — and the
  failure was silent, because `Actor.Parse` splits on the first `/` and reported it
  well-formed with a version of `0.1.0 tree-sitter/1.3.0 roslyn/5.3.0`. `okf validate`
  called such a bundle clean while every consumer reading the version got a string
  naming no release. The provenance is preserved in a sibling key, which OKF keeps
  across a round-trip. **Regenerate to update an existing bundle's `overview`.**
- **Scope filters on effective visibility.** A `public` member of an `internal` type is
  capped at internal by C#, so it is now out of scope by default. It used to be emitted
  with `--include-internal` off *and* tagged `public` — a visibility the language does
  not give it — so a bundle generated to exclude internal API published it anyway.
  **This removes concepts from regenerated bundles**, which is the point.
- **Generic types are disambiguated with a backtick, not `_`.** `Holder<T>` was spelled
  `Holder_1`, drawn from the C# identifier alphabet, so a type genuinely named
  `Holder_1` collapsed into the same concept — both signatures under one description.
  The ids move from `holder_1` to `holder-1`.
- **`okfgen` no longer names Roslyn in `generated.engines` on a run where Roslyn never
  ran** — including the common one, where every project failed to query or compile
  (an unrestored checkout, no `dotnet` on `PATH`). That field is a determinism claim
  — *these engine versions produced these bytes* — so naming an engine the run never
  invoked makes it false in the direction that matters, by promising reproducibility
  against a tool that was not there. It was written unconditionally on every run that
  was not `--no-code`, which also covers `--no-msbuild`, a repository with no project
  file, and an exhausted `--roslyn-timeout`. The golden fixture could not catch this:
  the fixture harness had the rule right while the shipped CLI did not, so the two
  disagreed about the same repository.
- **`--roslyn-timeout` is read invariantly, and its whole range is validated.** Without
  a custom parser the value was converted with the machine's culture and
  `AllowThousands`: `1.5` meant 15 on a comma-decimal locale, silently, and was refused
  outright on another. Values above `TimeSpan`'s range and below one tick escaped the
  range guard as unhandled exceptions. `--roslyn-timeout 0` was accepted and meant
  *unbounded*, the opposite of the smallest bound; it is refused now.
- **Cancelling an attested computation now stops it, whatever the host stage
  does.** The orchestrator handed its token to each stage and trusted them to
  observe it; a stage that ignores its token — any client predating cancellation
  support — meant an already-cancelled run executed every stage and could return
  a *displayable* success, so a §10 computation ran against a live system after
  the caller had withdrawn. The token is now checked at each step boundary. A
  caller's cancellation is also recognised when it arrives wrapped in an
  `AggregateException` (what `.Result`/`.Wait()` on a cancelled task produces at
  a plugin boundary) and is re-raised as an `OperationCanceledException`, so it
  reaches the caller in the shape they catch.
- **`OkfContextProvider` also honours cancellation between injected concepts.**
  The V1 guard shipped only before the bundle walk, because no seam existed to
  drive a cancellation once the loop had started; the loop reads one concept off
  disk per iteration, so a caller that withdrew part-way still paid for the rest.
- **Cancelling an attested computation works.** Two defects compounded. The
  orchestrator caught every stage's exception with a bare `catch (Exception)`,
  so an `OperationCanceledException` from a host-plugged binder/executor/attester
  was converted into a business outcome — `RunAsync(ct)` with a cancelled token
  returned a normal-looking result, and a caller could not tell "the stage
  failed" from "I asked it to stop". Errors-as-data is the contract for
  failures; a cancellation is not one, and now propagates. And
  `okf_run_computation` blocked its thread with `.GetAwaiter().GetResult()`
  while passing **no** token at all, so a slow or wedged executor pinned an
  Agent Framework worker indefinitely.

- **Raw exception messages no longer cross into the model's context.** Three
  places rendered a .NET exception's own message to the LLM: the context
  provider's `bundle unavailable: {ex.Message}`, `okf_run_computation`'s
  `Error: {outcome.Error.Message}`, and the orchestrator's own
  `executor threw: {e.Message}` reason string, which the tool then rendered
  too. A filesystem exception's message carries the absolute path; an exception
  from a host-plugged attestation runtime can carry a connection string, a
  query, or the row it choked on. All three now name a category or the
  exception *type*. Nothing is lost to the host — the exception object is still
  on `AttestationOutcome.Error` — and `AttestationOutcome.Reasons` is now safe
  to render into an agent's context, which is what it is for.
- **The scoped (V2) context path no longer swallows read failures in total
  silence.** Its knowledge and memory reads degrade to empty by design, but the
  bare `catch (Exception) { }` left the host with nothing to diagnose from.

- **The CLI validates each verb's arguments instead of silently ignoring what it
  does not understand.** Any `-`-prefixed token was kept as a valueless flag and
  every positional after the first was dropped, so `okf validate b --jsonn`
  printed the human report and exited 0, and `okf info b extra` ignored `extra`
  — a typo ran the command with different behaviour than asked for, with no
  signal. Each verb now declares the options it accepts; anything else is
  `error: unknown option: <flag>` and a surplus positional is
  `error: unexpected argument: <token>`, both exit 1. The allowlist is per-verb,
  so `okf validate b --dot` is rejected rather than quietly ignored.
- **`-h`/`--help` works on every verb.** Each command body opens by demanding
  its positional, so `okf validate --help` answered `error: missing <bundle>`
  and exited 1 — the one question a user asks when they do not know what that
  argument is. Help is now answered before dispatch and prints that verb's own
  usage line, summary and options.
- **A token after `--` is a positional like any other.** The separator used to
  let the first token after it override an earlier positional and swallow the
  rest, so `okf audit -- b --json` resolved `b` and discarded `--json` in
  silence. The separator's contract is unchanged — nothing after it is ever a
  flag, so a path starting with `-` still works and `okf fmt -- f -w` still
  never writes — but the leftovers are now named rather than dropped.

- **`okf index` no longer reports success for a bundle root that does not
  exist.** Every other bundle verb (`validate`/`info`/`graph`) routes
  through `Bundle.Load`, which rejects a non-directory root; `index` hands its
  path straight to `IndexGenerator.RegenerateIndexes`, whose documented contract
  is to return an empty list rather than throw. The CLI rendered that as
  `no index files written (empty bundle?)` and **exited 0**, so a typo'd path
  looked like an empty bundle. `index` now runs the same guard and exits 1 with
  the same message the other verbs produce.
- **`OkfContextProvider` no longer ignores its `CancellationToken` on the V1
  path.** The token was forwarded to the scoped (V2) path and then never
  consulted again: with no resolver and memory store wired, the provider walked
  the whole bundle off disk and ran the injection loop regardless, so a caller
  that had already cancelled still paid for a full context assembly.
- **Captured memory concepts carry a `generated` stamp, not a legacy
  `timestamp`.** Both capture paths wrote the §13.1 `timestamp` field, so every
  memory concept the provider created tripped `BundleValidator`'s
  `LegacyTimestamp` warning the moment the memory bundle was validated — the
  provider is a producer, and since the v0.2 bump provenance is the §5.2
  `generated` stamp. Note this could not have been fixed by enabling
  `BundleConceptWriter.AutoStampGenerated`: both paths write through
  `AppendToConceptAtomic`, which never runs the auto-stamp.
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
- **The `okf_audit` tool told the model `stale_after` was a date.** Its `stale`
  parameter carries two descriptions: an XML doc comment, which never leaves the
  IDE, and a `[Description]` attribute, which is the text handed to the model in
  the function-tool schema. The instant-semantics work above corrected the first
  and left the second, so the only description that ships was also the wrong
  one. No behaviour changed — the filter was already instant-based — but an
  agent driving `okf_audit` kept a date-grained model of it, wrong exactly at
  the boundary this work exists to pin: a `stale_after` falling later the same
  day is not yet stale.

- The CLI's `--version` is now checked against `<Version>` in
  `Directory.Build.props` by a test. The two are maintained separately and had
  drifted: the 0.2.0 winget package shipped a binary printing
  `okf 0.1.0-alpha.1`, which the previous test did not catch (it only asserted
  the `okf ` prefix).
- **`okfgen --reset` no longer empties the bundle and then fails.** The delete
  moved to the commit boundary, so a run that fails while generating leaves the
  previous bundle intact (a run interrupted during the commit itself still leaves
  a half-written directory — `--update` is the flag with no such window). A `--out`
  that is, or contains, `--repo` is now refused, as is one holding a symbolic link
  or junction.
- **The SQL wrapper percent-decodes `OKF_CONN`'s userinfo and survives a
  statement without a result set.** `urlparse` keeps `p%40ss` encoded (libpq
  decodes it), so the only URL spelling of a password containing `@` failed
  authentication; and `pg8000` returns `None` for DDL/INSERT, which the
  wrapper iterated — after the statement had run against the live database —
  reporting a completed side effect as a failed run.
- **`okf verify` / `okf_verify` no longer rewrite the whole frontmatter:**
  `RecordVerifications` now edits the `verified:` block in place
  (`FrontmatterBlockEdit`), so CRLF endings, YAML comments and folded/flow
  spellings elsewhere survive — the contract's "preserving every other
  frontmatter key" was previously false. The edit locates `verified` the
  same way this library's own YAML parser does (`"verified":`, `'verified':`,
  and `verified :` are all recognized, not just a bare column-0
  `verified:` prefix — a spelling the old prefix check missed used to insert
  a silently shadowed duplicate instead of replacing the existing stamp), and
  shares the frontmatter fence's own detection with `OkfDocument.Parse`
  (`OkfDocument.IsFenceLine`) so the two can never disagree about where the
  frontmatter ends. YAML's indentless block-sequence form under `verified:`
  is now absorbed correctly instead of truncating the block, and a mixed-
  line-ending document keeps every UNTOUCHED line's own terminator exactly
  (no more CRLF-izing the whole file because one line happened to use it).
  The result is checked by full structural equality against the intended
  document — frontmatter and body — rather than a `verified`-entry count, so
  a future mis-edit is refused rather than silently written.
- `samples/acme-retail-agent` restores again (NU1605 after the
  `Microsoft.Agents.AI` 1.20.0 bump).
- **`okf-render` refuses a bundle holding two concept ids that differ only by
  case** (e.g. `users` and `Users`, both valid per §2 — `ConceptId` segments
  are case-sensitive) instead of silently letting the second overwrite the
  first on a case-insensitive output volume (NTFS, default APFS, exFAT, SMB)
  with the generated index linking both entries to the survivor. This also
  covers a concept colliding with `HtmlWriter`'s own generated `index.html`:
  `Bundle`'s reserved-filename check is an ordinal switch, so a root-level
  `Index.md`/`INDEX.md` loads as an ordinary concept named `Index` on a
  case-sensitive bundle volume, whose page would otherwise collide with the
  site's own index one level up from the page-vs-page case. `HtmlWriter.Write`
  now checks every page's `RelativeHtmlPath` — plus the generated
  `index.html` name itself — for a case-insensitive collision before doing
  anything else — before even creating the output directory — and throws
  `ArgumentException` naming the colliding concept(s), which `okf-render`
  already surfaced as `error: …` through its existing exception mapping. The
  refusal applies unconditionally, even on a case-sensitive volume where both
  files would render fine: a site that renders differently depending on the
  filesystem it lands on is not a site.
- Teardown after a container timeout is bounded by a single 3-second budget
  instead of 5 seconds per kill attempt. `CliContainerEngine.KillContainerAsync`
  bounded each of its two `kill` attempts (plus the delay between them) by its
  own 5 s `CancellationTokenSource`, so a daemon that answered slowly but
  reliably could stretch teardown to ~10 s, and a responsive engine's 750 ms
  `kill` still cost the caller up to 5 s if the daemon later went
  unresponsive — a Medium external-audit finding (a 750 ms-per-call `kill`
  measured stretching a 30 ms `Timeout` to ~1.74 s). One 3 s deadline, computed
  once, now covers both attempts and the delay between them; the retry is
  skipped once fewer than 500 ms of the budget remain, so an unresponsive
  engine cannot turn the timeout `RunAsync` promised into a multi-attempt
  hang — this host's own contract (the OKF spec's §10 says nothing about
  timeouts or teardown): the requested `ContainerRunSpec.Timeout` bounds the
  whole run, and teardown after it fires is part of what the caller is
  still waiting on.
- `okf index dir/` and `IndexGenerator.RegenerateIndexes` with a trailing
  directory separator now write the root `index.md` (§8). A bare
  `Path.GetFullPath` preserves a trailing separator, and `Path.GetDirectoryName`
  on such a path returns the path itself trimmed, not its true parent -- so
  the intended parent sentinel came out equal to the bundle root, and the
  ancestor walk from any concept's directory stopped one step too early,
  never adding the bundle root to the set of directories to index.
  `RegenerateIndexesWith` now resolves `bundleRoot` through
  `ReparsePoints.CanonicalizeRoot` (already used elsewhere in this codebase
  for the identical reason), which trims a trailing separator -- the
  platform's and the alternate one -- before anything downstream compares
  against it.
- **`okfgen` no longer produces a Win32 reserved device name as a concept id
  segment** (a finding, low severity, Windows Server/10 kernels — Windows 11
  relaxed the restriction, verified on build 26200, but a bundle generated
  there must stay writable when checked out or regenerated on the
  still-supported kernels that keep it). `con`, `prn`, `aux`, `nul`,
  `com0`-`com9` and `lpt0`-`lpt9` (Microsoft's documented reserved list,
  `com0`/`lpt0` included even though some Windows versions' path parser
  accepts them) address a system device rather than a
  regular file there, so a bare `aux.md` or `aux/con.md` is unwritable, and
  the restriction applies to a segment's base name (the part before its
  first `.`) regardless of extension, so a NuGet `PackageId` such as
  `Aux.Core` was equally affected (`packages/aux.core`). Both id families —
  code ids (`CodeConceptIds.Compose`) and package/doc ids
  (`ConceptIdRegistry.Register`, touched for this) — now suffix the matching
  segment's base name with `_`, through one shared helper
  (`CodeConceptIds.SuffixWindowsDeviceName`) so the two sites cannot diverge:
  `aux` → `aux_`, `packages/aux.core` → `packages/aux_.core` — the suffix
  lands right after the base name, before the first `.`, not appended at the
  end of the slug (`aux.core_` is still reserved: its base name is still
  exactly `aux`). Applied before the registry's existing numeric-collision
  loop, so a synthesized `aux_` still collides with a real segment already
  registered under that exact name. A name that only resembles a reserved
  word (`Auxiliary`, `Com10`, `Console`) is unaffected — the match is on the
  whole base name, never a prefix. **Id churn:** an existing bundle containing a
  code, package or doc name whose slug's base name exactly matches one of
  these words gets a new id on the next `okfgen generate` (`producers/`).
- **`okfgen`'s effective-visibility cap (code-graph design §5.4) no longer caps
  a namespace's own members when a type happens to share the namespace's full
  dotted name** (a
  finding, `FileEligibility.IsInScope`) — a CA1724-style collision (a
  `Logging` class beside an `X.Logging` namespace) that is common in real
  code. `SymbolFact.Container` is a flat dotted string, so a type nested
  inside a type named `B` and a type merely declared inside a same-spelled
  namespace `A.B` both read `Container = "A.B"`; the cap walk could not tell
  them apart, so an internal `class B` in namespace `A` capped every public
  symbol of the unrelated namespace `A.B`, whatever it declared, to
  `internal`. The walk now stops at `SymbolFact.ContainerNamespace` (always a
  dotted prefix of `Container`) rather than walking every segment, so it caps
  only the segments genuinely below the namespace — a type nested one level
  inside a real enclosing type still caps correctly, including across a
  `partial` type's two files, where `declared` keeps only the first-seen
  declaration. A run with no such collision is unaffected, and a
  `SymbolFact` built without `ContainerNamespace` (an older extraction, a
  hand-built fixture) keeps the prior behaviour exactly.
- **`okfgen`'s tree-sitter extractor no longer credits a field-initializer call
  to a lambda's local, nor adds non-declaration segments to a container path**
  (a finding, `TreeSitterExtractor`). A call inside a lambda in a field
  initializer (`Lazy<int> _lazy = new(() => { var result = Compute(); … })`)
  was attributed to the nearest `variable_declarator` above it — the lambda's
  local `result` — so with a field named `result` on the same type the edge
  hung off that real, unrelated field; it now takes the declarator of the field
  declaration itself (`_lazy`), still per declarator in `a = Foo(), b = Bar()`.
  Separately, the container walk took a segment from any ancestor with a
  grammar `name` field, which includes accessors (`get`/`set`/`add`), named
  arguments, named tuple elements and member accesses (`.First()`), so a local
  function in a getter sat under `N.T.P.get` where `RoslynResolver` says
  `N.T.P`, so the two engines' `(Container, Name)` join key (code-graph design
  §2.1) disagreed.
  The walk is now an allow-list mirroring
  `RoslynResolver.ContainerPathFromSyntax` kind for kind (namespace, type,
  delegate, method, constructor, destructor, property, event, local function,
  variable declarator). **No generated bundle changes from this second half:**
  the only declarations that can sit under those nodes are local functions,
  which carry no access modifier, are always `Private`, and are excluded by
  `FileEligibility.IsInScope` unconditionally — `--include-internal` does not
  admit them. So a local function's container (`N.T.P.get` → `N.T.P`) never
  becomes a concept id, a getter and a setter each declaring a same-named local
  function never reach the code-graph design's §3.2 overload merge, and a call
  to one renders as unresolved both before and after. The change is visible to direct consumers
  of `TreeSitterExtractor`'s `SymbolFact.Container` / `CallSite.CallerContainer`
  and of resolver edges before `CodeGraphBuilder`'s scope pass, and it keeps the
  join key correct should a future scope rule ever admit such a declaration.
  The first half (field-initializer callers) does reach bundles: such a call
  now appears under the in-scope field that holds it (`## Calls` or
  `## Calls (unresolved)`), where it used to be dropped or, as above, listed
  under an unrelated same-named field (`producers/`).
- **`okfgen`'s repository scan no longer aborts on a directory link, or on a
  subdirectory or manifest it cannot read** (a finding, `RepositoryScanner`).
  A directory junction/symlink anywhere in the tree previously made the
  recursive `.sln`/`.csproj` walk loop until it threw `IOException` (the OS's
  own path-length refusal), aborting the whole run before it wrote anything —
  reachable through an accidental self-referencing link, not only a crafted
  one. The walk now skips a subdirectory that is itself a link, the same way
  `BundleWriter`/`BundleDrift` already do (`BundlePaths.IsReparsePoint`), so a
  cycle is never entered rather than merely bounded. Separately, a `.csproj`,
  `.sln`, `package.json` or `README.md` this process cannot read (a
  permission-denying ACL, most concretely) threw `UnauthorizedAccessException`
  out of `Scan` instead of being treated like a malformed one: `ScanNuGetManifest`
  and `ScanNpmManifest` now widen their catch to match
  `FileEligibility.ReferencesTestSdk`'s list (`IOException`,
  `UnauthorizedAccessException`, `NotSupportedException`,
  `SecurityException`, plus `XmlException`/`JsonException`), and a
  subdirectory whose own listing throws the same way is skipped, its siblings
  still walked. An unreadable `README.md` still produces a doc entry, titled
  with the repository name (`BuildDocConcept` never reads its content, only
  the title `Scan` hands it) — matching the existing no-heading fallback.
  **Deliberately still throwing:** the repository ROOT itself — a `--repo`
  that exists but cannot be listed is a usage error `OkfgenCli.Generate`
  already reports as `error:`, not degraded input to route around; and a
  `.sln` entry that resolves through a link inside the repository, unchanged
  and consistent with `CodeGraphBuilder`, which also walks through links
  (`producers/`).
- **One unreadable directory no longer empties the whole code walk** (E5,
  `CodeGraphBuilder`). The code stage's file enumeration used
  `Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories)`, which
  throws `UnauthorizedAccessException` for the whole call the instant it
  reaches an inaccessible subdirectory — the existing outer catch then
  discarded every file already found, including every sibling the locked
  directory has nothing to do with, and the run reported zero symbols on a
  repository that was otherwise perfectly readable. `EnumerateFiles` now
  passes `EnumerationOptions { RecurseSubdirectories = true,
  IgnoreInaccessible = true, AttributesToSkip = 0 }` (the last field
  deliberately overrides its non-zero default, so a hidden or system file this
  producer used to see is still seen), and a second, directory-scoped pass
  names exactly which directories were inaccessible in the new
  `RunStatus.InaccessibleDirectories` — kept off `RunStatus.Skipped`
  deliberately, since a directory is not a file this run attempted and adding
  it there would inflate the "N source file(s) visited" count
  `GenerateRun.Summarize` derives from that list's length. `Summarize` names
  each inaccessible directory in the same unanalysed listing and the same cap
  a per-file skip uses (e.g. `- locked/: skipped, directory not readable`).
  The repository ROOT itself and a circular reparse point remain
  all-or-nothing, unchanged (code-graph design §2.3).
- **`okfgen`'s Roslyn stage now owns a `Compile` item under a directory whose
  name merely starts with `..` (`producers/`).**
  `RoslynResolver.RelativeToRepository` tested the repository-relative path's
  `..` as a string prefix, so `..foo/Dotted.cs` read as outside the
  repository: its tree was compiled but never owned, and its calls fell back
  to the name-matching baseline. It now uses the shared
  `BundlePaths.TryGetPathUnderRoot`, which treats only a first segment of
  exactly `..` as a climb. Pinned end to end by
  `A_compile_item_under_a_directory_named_with_a_leading_double_dot_is_still_owned`,
  red before the change.

### Security

- **Link guards now refuse an entry whose link status cannot be inspected**,
  instead of treating it as a plain directory. A junction carrying a
  deny-ReadAttributes ACE, under a parent that denies listing, makes reading
  its attributes fail while the OS still traverses it on the write or read
  that follows; the shared predicate answered "not a link", and `okf-render`
  wrote `x/y/z/two.html` outside `--out` (executed, not hypothesised). The
  fix is a strict variant for guards:
  - render output: `--out` resolution and every file written;
  - concept writes (`BundleConceptWriter`, hence `okf verify`,
    `okf_write_concept` and memory writes), `log.md` (`okf_append_log`),
    index writes and `okf_browse`;
  - the memory store's read, enumerate and recursive delete of a scope
    directory;
  - **catalog source paths** (`CatalogPathResolver`): a knowledge source or a
    memory tier root reached through such a junction is now refused as
    `ReparsePointInPath`, which the catalog reports fail-fast. Before, a
    knowledge search returned a passage from outside the catalog root, and a
    memory tier wrote into, and recursively deleted, a directory outside it.
    An ordinary catalog's diagnostics do not change: a path that does not
    exist is still `TargetNotFound`;
  - **resource reads**: `Bundle.ReadResourceText` now re-checks the path it is
    given, strictly, right before reading — it must be inside the bundle root,
    with no reparse point or uninspectable entry on the way — and throws
    `UnauthorizedAccessException` otherwise. `okf_get_computation` and the
    attestation orchestrator's computation and attester reads report that
    through their existing "could not be read" errors; before,
    `okf_get_computation` returned a file from outside the bundle held by a
    long-lived tool instance. `Bundle.TryResolveResource` is unchanged, so
    what `okf validate` reports (§6.2 resource status) does not change.

  Walks keep the lenient predicate, so what a bundle loads and what an
  `index.md` lists do not change. An entry that does not exist is still
  allowed — new files and subdirectories, and equally an empty drive or a
  missing network share, whose own I/O error is reported as before
  (`okf-render --out F:\site` on an empty drive says the device is not
  ready, not that the path is inside the bundle).

  **Changed messages.** The existing refusals that named a reparse point now
  say "a reparse point (symlink/junction), or an entry that could not be
  inspected" (`BundleConceptWriter`, `okf_append_log`, the memory store,
  `CatalogPathResolver`), since an ACL-protected plain directory or an invalid
  name such as `nul` is refused the same way. `okf-render` has one new
  refusal, "cannot determine where it resolves", for an `--out` path whose
  link cannot be followed or inspected. `HtmlWriter.Write` no longer lets an
  `UnauthorizedAccessException` from resolving `--out` through such a link
  escape; it refuses with that message instead. Not a spec behaviour: the OKF
  spec says nothing about filesystem links — this is the host's guarantee
  that a bundle-relative read or write stays in the bundle (§3), the catalog
  root or the output directory it was given.
- **`okfgen`'s code stage no longer reads a source file or strong-name key
  from outside the repository through a junction whose attributes are
  denied (`producers/`, Windows).** Shape, executed: a directory `repo\p`
  denying listing (`(RD)`), holding a junction `p\jra` that points outside
  the repository and carries a deny read-attributes (`(RA)`) ACE. A file
  beneath it still opens by its in-repository path (traverse bypass) and
  reports as existing, but `LinkTarget` on the junction answers "not a link"
  without throwing, and that was the only probe the producer's link walks
  asked. Measured on Windows 11 at `da6225d` and at E11's first cut
  (`f4f8250`), with the outside `y.cs` and `k.snk` addressed as
  `repo\p\jra\y.cs` / `repo\p\jra\k.snk`:
  - `CompilationFactory.Create` parsed the outside `y.cs` into the project's
    compilation (a `Compile` item reaches it as a path MSBuild printed, not
    through a listing of `p`);
  - `CompilationFactory.Create` handed the outside `k.snk` to the compilation
    as its strong-name key file (public signing on);
  - `TreeSitterExtractor.Extract`, given that path directly, extracted the
    outside file's symbols (`Extracted`). The repository walk itself does not
    list `p`, so `CodeGraphBuilder` never handed it that path: it reports `p`
    as inaccessible instead.

  Now each level of `BundlePaths.HasLinkAncestor` is also classified by
  `File.GetAttributes`, which throws on the denied junction, and an
  uninspectable level counts as a link: no syntax tree, no key file,
  `SkippedSymlink` — pinned end to end by
  `A_junction_to_outside_whose_attributes_are_denied_under_an_unlistable_parent_is_never_read_windows`,
  red with `f4f8250`'s walk. A plain directory with the same two ACEs cannot
  be told apart and is refused too, even though its file is inside and
  readable. POSIX has no analogue: opening the file needs search permission
  on every ancestor, which is all `lstat` needs, so a link that cannot be
  inspected cannot be read through either. Not an OKF spec behaviour: the
  spec says nothing about filesystem links — this is the producer's own
  hostile-input guarantee (the code-graph design's §2.3 guards, which
  `TreeSitterExtractor` applies) that it reads only the repository it was
  pointed at.

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
