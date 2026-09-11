# OKF4net Roadmap

OKF4net implements the [Open Knowledge Format (OKF) v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
on the .NET base class library with zero third-party runtime dependencies.
This roadmap shows where the project is heading. It is a living document —
issues labelled [`good first issue`](https://github.com/jchable/okf4net/labels/good%20first%20issue)
and [`help wanted`](https://github.com/jchable/okf4net/labels/help%20wanted)
are the concrete entry points.

## Now (in progress)

- Broaden test coverage and worked examples for the CLI verbs and the agents layer.
- Documentation: end-to-end tutorials for both audiences (library users, agent builders).

## Next

- **`okf audit` shipped** — a corpus-level query over a bundle's trust (§5.3),
  lifecycle (§5.4) and staleness (§5.5) signals: counts plus a filterable
  worklist, across the CLI verb and the read-only `okf_audit` agent tool,
  backed by the shared `ConceptAudit`/`AuditVocabulary` model in `OKF4net`.
  Motivated by ["OKF v0.2 Quietly Admits the Folder Has a Ceiling"](https://medium.com/@davidroliver/okf-v0-2-quietly-admits-the-folder-has-a-ceiling-the-way-up-is-a-library-25fa54e872f9)
  — see [its design spec](docs/superpowers/specs/2026-08-21-okf-audit-design.md).
- **`okf verify` shipped** — the verb that answers what `okf audit` asks
  about trust: it records a review by adding, or from the same actor
  replacing, a `{by, at}` entry in a named concept's `verified` list (§5.2),
  so — for a `human:` actor — the concept clears audit's trust-filtered
  selection at the next pass. A `process:` or `<producer>/<version>` actor is accepted
  symmetrically (§7) but only moves the concept from `unverified` to
  `machine-confirmed`, which `--trust unverified,machine-confirmed` still
  selects. Verification only moves the trust dimension (§5.3) — `stale_after` is
  untouched, so a just-reviewed concept can still appear in `okf audit`'s
  *default* (staleness-only) worklist. `<id>…` accepts `-` to
  read ids from standard input, so `okf audit … --trust unverified | cut
  -d' ' -f1 | okf verify … --by human:ada -` closes the loop in one line.
  Backed by the new `BundleConceptWriter.RecordVerifications` — the single
  governed writer of `verified` — and exposed to agents as `okf_verify`. See
  [its design spec](docs/superpowers/specs/2026-08-28-okf-verify-design.md).
  - **Next, highest-value follow-up: a time-aware audit.** A `verified`
    stamp today attests a moment, not a version — `Trust.DeriveTier` derives
    `human-reviewed` from an actor's presence alone, so a five-year-old human
    stamp counts the same as one from this morning, and nothing currently
    flags that the concept's content moved after the review. Exposing the
    stamps' timestamps on `AuditFinding` would let `okf audit` ask "reviewed,
    but as of when, and has the file changed since?" — answered outside the
    library, by comparing `max(verified[].at)` against
    `git log -1 --format=%cI -- <path>` (the folder is canonical; its
    history is git's, not the frontmatter's). Deliberately out of `okf
    verify`'s scope: it needs no new write path, only turns an existing
    field from a permanent alibi into a signal that decays. No schema
    extension (`digest`, `scope`, `note` on the stamp) is planned to
    recreate this information inside the bundle instead — that question is
    answered by git, on purpose.
- **Atomic write-then-rename in `BundleConceptWriter`.** Every write path
  in the class ends at `File.WriteAllText`, which truncates the target and
  writes in place, so a failure mid-write (full disk, device error) can
  leave a concept truncated or half-written. `RecordVerifications` reports
  the concepts whose write returned, and that file is not among them — so
  the report is not wrong, but "exactly what landed" is a stronger claim
  than the primitive supports, and the docs now say so. Closing it means
  writing to a temporary file in the same directory and `File.Replace`-ing
  it over the target. Deliberately its own pass rather than a footnote to
  `okf verify`: the call sits immediately after the late reparse-point
  re-check and inside the per-bundle lock, so a replacement needs tests for
  `File.Replace` semantics (cross-volume, existing-file, permissions,
  what happens to the backup), for the path-safety guard still holding
  against the *temporary* name, and for the lock — a security-sensitive
  seam that must not be swapped in passing. Pre-existing and shared by
  every write path; not introduced by verification.
  One thing in its favour, measured rather than assumed: every write path in
  the class funnels through the single `WriteValidatedContentLocked`, whose
  `File.WriteAllText` is the only line that touches disk. The change is
  therefore contained to one method — it is the *interaction* with the late
  reparse-point re-check and the lock that needs the tests, not a scattered
  edit.
- **Resolve the bundle root before keying the write lock.**
  `BundleConceptWriter`'s process-wide lock registry is keyed by
  `ReparsePoints.CanonicalizeRoot`, which is `Path.GetFullPath` plus a
  trailing-separator trim — purely lexical. A junction or symlink
  `alias` -> `actual` therefore yields two distinct locks over one set of
  files, so two writers in the same process can interleave their
  read-modify-write cycles and lose a stamp. Shown with `mklink /J`: the two
  lock objects are not reference-equal. Fixing it means following reparse
  points on the root, which touches the same seam `ValidateConceptTarget`
  guards, so it needs its own tests (junction, symlink, a root whose parent
  is a reparse point, and the cross-platform behaviour of
  `Directory.ResolveLinkTarget`). Pre-existing and shared by every write
  path, but verification raises the stakes: it is the first operation to hold
  that lock across a batch of files. The lock is in-process only either way —
  a second `okf` process was never serialized against, and that limit is
  already documented on the class. Tracked as
  [#86](https://github.com/jchable/okf4net/issues/86), which carries the
  reproduction.
  **The design question comes before the fix, and it is bigger than the title
  suggests.** `CanonicalizeRoot` has nine call sites across five projects —
  `Bundle`, `IndexGenerator`, `BundleConceptWriter`, `OKF4net.Catalog`
  (`CatalogPathResolver`, `FileMemoryStore`), `OKF4net.Viewer` — and two of
  them are the path-safety guards themselves, `IsWithinBundleRoot` and
  `HasReparsePointAncestor`. Making it resolve reparse points would therefore
  change what "inside the bundle" means everywhere, which is a security change
  with a repo-wide blast radius, not a lock fix. The narrower alternative is to
  leave `CanonicalizeRoot` lexical and give the lock registry its own resolved
  key, so only the serialization contract moves. Deciding between those two —
  and saying what each does when resolution fails, or when the target does not
  exist — is the actual work; the code after it is small.
- **Reconcile the YAML depth counters between the parser and the emitter.**
  `YamlParser` enforces its 1000-level cap with TWO independent counters (one
  for block nesting, one for flow); `YamlEmitter` has a single counter covering
  both. A frontmatter mixing the two — roughly 450 block levels with 900 flow
  levels — therefore parses happily and then cannot be re-emitted, breaking the
  invariant a format library owes its callers: whatever it can read, it can
  write back. Only the *symptom* was addressed alongside `okf verify`: the
  emitter now raises a catchable `YamlEmitException` instead of a bare
  `InvalidOperationException`, so the failure is errors-as-data on every path
  rather than a stack trace out of the CLI or a fault in an MCP host. The
  asymmetry itself is untouched, deliberately: making the two agree changes what
  the library ACCEPTS, on the read path, which is a compatibility decision with
  its own tests (what a bundle in the wild may already contain) and not a
  footnote to a write feature. Pre-existing; reachable from any caller that
  parses a hostile-but-loadable document.
- **A typed `OkfDocumentBuilder` method for the shared `usage_window`.** The
  builder can now write a *per-entry* §5.1 override (`AddSource(…,
  usageWindow:)`), but the shared, top-level `usage_window` — §5.1's normal
  case, the one that frames every `usage_count` in a document — has no typed
  setter: a producer must hand-build its `{ from, to }` mapping and pass it
  through `Extension("usage_window", …)`. The builder currently makes the
  exception easier to write than the rule.
- More `OKF4net.Agents` samples with Microsoft Agent Framework — the first,
  `samples/acme-retail-agent`, shipped in 0.4.0; more welcome.
- `OKF4net.Catalog` samples: `samples/catalog-explorer` (multi-source
  search, ranking strategies, per-caller visibility, the `role: memory`
  tier) shipped. A natural next one: a read-write "second brain"
  personal-notes sample over `OKF4net.Mcp` in Claude Desktop — the current
  MCP story is read-only-focused; this would exercise write/append and
  `IndexGenerator`/`ChangeLog` (§8/§9) updating live as notes are added.
- Performance baselines for large bundle loads.
- Bundle viewer: **static render shipped** as the standalone `okf-render`
  binary (`OKF4net.Render`, over `OKF4net.Viewer`) — split out of `okf`
  itself so the CI-facing validator does not carry the viewer's JavaScript.
  The live-server half of [#40](https://github.com/jchable/okf4net/issues/40)
  was **dropped, and the issue closed** — the interactive, always-fresh
  viewing it was meant to provide is being pursued as the VS Code extension
  below instead, which reaches the same goal from inside the editor without
  a local HTTP server, and reaches full-text search by the same route (an
  extension host is a process, so it can have the .NET side run
  `ConceptSearch` rather than mirroring its weights in JavaScript). What the
  server would have added over `okf-render` alone was one saved command
  invocation per edit; search was the only capability that genuinely
  required it, and the extension gets that too.
  - **The client-side XSS defense is guarded by a JS harness, not by xunit.**
    xunit runs on .NET and cannot execute JavaScript, so
    `tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs` only smoke-checks for
    source-text markers — it stays green even if the sanitizer is gutted.
    `tools/viewer-security-check/` (Node/jsdom, run against the real vendored
    marked) is the actual guard, and CI runs it as the `viewer sanitizer (JS)`
    job. Re-vendoring `marked.min.js` is the change most likely to regress the
    defense, and that job is what catches it.

## Later

- Ecosystem integrations driven by user demand.
- Tracking upstream OKF spec evolution beyond v0.2.
- **Zero-dependency bundle linter for CI.** A small AOT tool built on just
  `BundleValidator`/`LinkScanner` (no `OKF4net.Cli` dependencies beyond
  what's already zero-dep), packaged for GitHub Actions/pre-commit — a
  docs/DevOps-facing entry point distinct from the agent-builder-facing
  samples, showcasing the zero-dependency story to a different audience.
- **Interactive cross-link graph explorer.** A small web front-end over
  `okf graph`/`IndexGenerator` output, visualizing a bundle's concept
  cross-links — outreach-oriented (contributor/adoption funnel), likely
  outside pure C#/.NET so scoped as its own project rather than a
  `samples/` entry.
- **A Visual Studio Code extension viewer.** Browse the bundle open in the
  workspace from the editor itself — a tree view over the concepts, a
  rendered preview of the selected one, re-rendered on save — instead of
  generating a static site and switching to a browser. Its own project
  (TypeScript, its own repo and marketplace listing), like the graph explorer
  above, not a `samples/` entry.
  - **`OKF4net.Viewer` already carries the client half.** `Assets/viewer.js`
    is a self-contained IIFE with no framework dependency: it reads a
    `{ body, links }` JSON payload, renders the markdown with the vendored
    marked, sanitizes the *parsed DOM*, then rewires inter-concept links. A
    webview can run it as-is, and `tools/viewer-security-check/` keeps
    guarding it — which is the point of reusing it rather than writing a
    second renderer, since the sanitizer is the security-critical part and
    took several rounds to get right (see that file's header comment). Two
    adaptations are unavoidable: a VS Code webview's CSP needs a per-load
    nonce on the `<script>` tags and webview asset URIs for the three files,
    and `viewer.css` hard-codes its palette where an extension should read
    the `--vscode-*` theme variables.
  - **It does not carry the C# half across.** An extension host is Node, so
    `SiteModel`/`HtmlWriter` are reachable only by shelling out. Cheapest
    path: run `okf render --out <tmp>` and point the webview at the generated
    page. Better fit: `SiteModel.Build` is a pure `Bundle` → model projection
    with no I/O, so a JSON output mode emitting exactly the `{ body, links }`
    payload for one concept would let the extension re-render a single page
    per save. That JSON payload mode is now the *only* consumer of this
    plumbing, since the live-server half of #40 was dropped in favour of
    this extension.
    `HtmlWriter` and `HtmlSafeJson` do not transfer at all: output layout and
    write-containment guards are static-site concerns, and a webview receives
    the payload by `postMessage` as a real object rather than escaping it
    into an HTML `<script>` element.
  - **Search is reachable here, unlike in the static site.** The extension
    host is a process, so it can have the .NET side run `ConceptSearch`
    instead of mirroring its weights in JavaScript. There is no `okf search`
    verb today though — the scorer is exposed only as `okf_search` in
    `OKF4net.Agents` (hence over `okf-mcp`), so this means either driving the
    MCP server or adding that verb.
  - **Licence obligations travel with the files.** `viewer.js`/`viewer.css`
    are LGPL-3.0-or-later and the vendored `marked.min.js` is MIT with a
    `NOTICE` credit; copying them into a separate extension repo carries both.
- **Dogfooding on a real third-party OSS project's docs.** Convert an
  existing open-source project's markdown docs into an OKF bundle via
  `okf fmt`/`index`/`validate`, as a concrete "here's how you'd actually
  adopt this" walkthrough rather than a synthetic sample bundle.
- **Attested Computation (§10), executed for real.** `samples/acme-retail-agent`
  is deliberately read-only for `Attested Computation` concepts — it
  inspects (`okf_get_computation`) but never runs
  (`okf_run_computation`) `bundles/acme_retail`'s sanctioned SQL, because
  trusting a C# reimplementation of `attesters/sql_equality.py` would
  undermine the whole point of attestation. Actually running one end to
  end needs a sandboxed container-based `IComputationExecutor`/`IAttester`
  runtime, scoped in
  [that sample's design spec](docs/superpowers/specs/2026-07-30-acme-retail-bundle-and-agent-sample-design.md#future-work-a-container-based-execution-runtime)
  — its own design pass before implementation.
- **Open question upstream: concept id character set.** The spec (§2) does not
  restrict which characters a concept id may contain; `ConceptId.ValidateSegment`
  currently restricts to ASCII regardless. Whether to allow full Unicode (any
  alphabet, no transliteration) is an open, deliberately deferred decision —
  raised upstream, see
  [docs/outreach/upstream-issues/2026-07-31-concept-id-character-set-clarification.md](docs/outreach/upstream-issues/2026-07-31-concept-id-character-set-clarification.md).
  Blocks nothing today (the new `ConceptId.Slugify` helper folds non-ASCII to
  `'-'` in the meantime), but revisit once upstream responds — a decision to
  broaden `ValidateSegment` needs its own design pass (cross-platform Unicode
  normalization, golden-fixture impact).

- **`producers/OkfProducer` shipped** (repo scanner → OKF v0.2 bundle generator, `generate`/
  `validate` commands, npm/NuGet/README detection, and a C# code-graph stage: one concept per
  namespace, type and member, with resolved `## Calls` links — see
  [its design spec](docs/superpowers/specs/2026-07-31-okf-producer-design.md), the
  [core plan](docs/superpowers/plans/2026-07-31-okf-producer-core.md) and the
  [code-graph design](docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md)).
  Two things a reader should not mistake for open questions:
  - **No CI coverage — decided, not pending.** On 2026-08-01 it was settled that `producers/` does
    **not** go into CI: it stays outside `OKF4net.sln`/`ci.yml`. Two consequences, both accepted.
    The guarantee is local and it is one command, stated at the top of
    [`producers/README.md`](producers/README.md): `dotnet test producers/OkfProducer.sln`, run
    before touching the producer and after any public `OKF4net` API change. And the per-RID
    packaging smoke test cannot be a guarantee without CI, so it is a **documented manual step at
    release time**, described as such rather than implied to be covered.
  - **Documented.** [`producers/README.md`](producers/README.md) carries the flag surface, the
    verification command, the packaging step and the project layout.

  Open follow-ups, still open:
  - **The pruning guard compares scope FLAGS, not the scope RULE.** `BundleWriter` refuses to prune
    when the previous run covered a wider scope, and it decides that by comparing the flags recorded
    in the manifest — so it is blind to a run whose flags are identical but whose *rule* narrowed.
    Measured on exactly that: a bundle generated before scope moved to effective visibility, then
    regenerated with the same flags, lost five concepts and the guard stayed silent, because nothing
    in the manifest said the rule had changed. The producer now prints a note when it caps a public
    member at an internal container, which covers the one case that exists today; the general fix is
    to record a scope-rule identifier beside `scope` in the manifest so the existing guard fires on a
    rule change as it does on a flag change. Deliberately not done in the fix round that found it:
    it changes the manifest format, which is a compatibility decision of its own.
  - **More ecosystems.** Package detection is npm and NuGet only, and the code stage is C# only.
    The architecture is multi-language by construction (one `LanguageProfile` per language, one
    `ISymbolResolver` per precision level); a second profile would test the generality of that
    seam rather than chase coverage.
  - **Per-RID package weight.** A RID-specific `dotnet tool` package measures 80.7–87.6 MB
    installed (11.5–13.3 MB to download). Most of it is tree-sitter grammars this producer never
    loads — `verilog` 17.3 MB, `razor` 10.5 MB, `cpp` 5.1 MB — which cannot be removed one file
    at a time, because `deps.json` is what feeds `NATIVE_DLL_SEARCH_DIRECTORIES`. Getting the
    installed size below ~40 MB is a follow-up, not a v1 promise.
  - **No test covers `--rev`'s branch auto-detection happy path.** Every CLI fixture repository is
    deliberately outside git (so the suite stays at ~16 s and spawns no MSBuild), and the detached
    -HEAD case is covered by the one test that does build a git repository. The auto-detected
    branch name is verified by manual run only.
- **Known limitation: without `--repo-url`, `packages/` and `docs/` `resource` paths don't resolve
  against the bundle.** `producers/OkfProducer` records those families' `resource` relative to the
  *scanned repository* (e.g. `src/OKF4net/OKF4net.csproj`), which is the semantically correct
  provenance reference — but that path names a file in the *repository*, not in the *bundle*, so
  `BundleValidator` looks for `<bundle>/src/OKF4net/…` and it misses by construction: one "path
  not found" warning apiece, 10 on this repository. (Until the §6.2 fix of 2026-09-11 this entry
  blamed a different mechanism — a bare relative `resource` resolving against the concept's own
  directory, giving `<bundle>/packages/src/OKF4net/…`. That resolution rule was itself the bug and
  is gone; the base moved, the miss did not.)
  **`--repo-url` removes all 10**: those families build the same forge URL the `code/` family does,
  and a URL short-circuits the validator's path classifier. The original entry here recorded 20
  warnings and framed the only alternative as embedding copies of referenced files in the bundle;
  both were wrong. Half the 20 came from a one-entry `sources` block repeating `resource` verbatim
  (deleted — §4.5 already forbade it by name), and the other half needed no scope change at all,
  only passing `GenerateOptions` to two builders that had never taken it. What remains is the
  no-`--repo-url` fallback, kept deliberately: omitting the field costs the same 10 warnings
  (measured, 2026-09-03) and the path is the only pointer those concepts have to their own subject.
  See `producers/README.md`, "What `--repo-url` changes".

## Out of scope

- Third-party runtime dependencies in the library or CLI (BCL-only is a hard rule).
- Divergence from the OKF v0.2 spec without a documented, cited reason.

## How to influence the roadmap

Open a [Discussion](https://github.com/jchable/okf4net/discussions) or comment on an
existing issue. Roadmap items graduate to labelled issues before work starts.
