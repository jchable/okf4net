# §5.1 Per-entry `usage_window` — Implementation Plan

**Spec (binding authority):** `docs/superpowers/specs/2026-09-01-okf-per-entry-usage-window-design.md`
**Normative source:** `docs/spec/SPEC.md` (OKF v0.2, vendored verbatim, `sha256 26aa5da0…`).
Read the spec before Task 1 — it holds the line anchors, the two semantic
decisions, and ten claims already checked against the code (two came back false).
This plan holds only the work.

## Global Constraints

- **`docs/spec/SPEC.md` is the only authority.** Never edit it. §5.1 lines
  332-334: "`usage_window`: Written once as a sibling of `sources` … **A single
  entry MAY carry its own `usage_window` to override the shared one.**"
- **Zero third-party runtime dependencies** in `src/OKF4net`. BCL only.
- **Warnings are errors.** `dotnet build OKF4net.sln` must be clean.
- File-scoped namespaces, XML doc comments on all public API, nullable enabled.
- **Never edit `tests/fixtures/`.** The spec's §6 verified that no fixture
  carries a per-entry `usage_window`, so **no golden should move**. If one does,
  STOP and report it — that is a finding, not a fixture to update.
- **Do not use `sed -i`** — CRLF repo, it rewrites line endings repo-wide.
- Verification: `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln` (Debug —
  CI's configuration), `dotnet format OKF4net.sln --verify-no-changes`.
- **Severity is `Warning`, never `Error`** (§11: timestamp form is not one of
  the three conformance conditions), and a readable value is never dropped.

## The two decisions this plan encodes

Both are ours, because §5.1 says "override" and stops. Do not re-litigate them;
do pin each with a test.

1. **The override is whole-object, not per-field.** An entry writing
   `usage_window: { from: X }` yields a window whose `To` is `null` — it does
   **not** inherit the shared window's `to`. A per-field merge is a rule the spec
   does not state and would let a half-written entry borrow half a window.
2. **A malformed override falls back.** `usage_window: "hello"` on an entry
   yields `null` (matching `ParseUsageWindow`'s existing leniency), so the entry
   inherits the shared window exactly as an absent one does.

---

### Task 1: The data layer — record, parse, serialize, builder

**Files:** `src/OKF4net/Provenance.cs`, `src/OKF4net/OkfDocumentBuilder.cs`
**Test file:** `tests/OKF4net.Tests/ProvenanceTests.cs`

**Produces (consumed by Task 2):** `Source.UsageWindow`.

1. **The record.** `Source` gains a seventh positional member with a default:

   ```csharp
   public readonly record struct Source(
       string? Id, string Resource, string? Title, Actor? Author,
       long? UsageCount, string? LastModified, UsageWindow? UsageWindow = null);
   ```

   The member deliberately shares its type's name; this compiles (verified in
   the spec's C7 by building the whole solution with it, 0 errors/0 warnings).
   Update the type's XML doc to mention the override and cite §5.1.

2. **`ParseSources`** reads `usage_window` from each entry mapping **through the
   existing `ParseUsageWindow`**, so the two positions cannot drift in how they
   parse: `UsageWindow: ParseUsageWindow(m.Get("usage_window"))`.

3. **`ToYaml`** writes it. Two details that decide whether the round-trip is
   lossless:
   - The key goes **last**, after `last_modified`, keeping the canonical order
     the method's own XML doc enumerates — **and that doc must be updated to
     list it**, or the comment becomes false the moment the code is right.
   - Write the key whenever `source.UsageWindow is not null`, and inside it omit
     a null bound. This preserves the present-but-empty case: `usage_window: {}`
     parses to `new UsageWindow(null, null)`, which is *not* the same as absent,
     and must not collapse into it.
     **The empty mapping does round-trip** — established before writing this
     plan by running `okf fmt` on a document carrying `usage_window: {}`, which
     re-emits it verbatim. So write an empty `YamlMapping` in that case rather
     than dropping the key, and pin it with a test.

4. **`OkfDocumentBuilder.AddSource`** gains a trailing optional parameter
   `UsageWindow? usageWindow = null`, passed through to the `Source`. Without it
   the field is readable and unwritable through the one API built for writing it
   (the spec's C6). Update its XML doc.

**Tests** — append to `ProvenanceTests.cs`, matching the file's existing style
(read it first; it uses named arguments for `Source`):

| Case | Expectation |
|---|---|
| entry with `usage_window: { from: '2026-01-01T00:00:00Z', to: '2026-01-31T00:00:00Z' }` | `Source.UsageWindow` has both bounds |
| entry with no `usage_window` | `Source.UsageWindow` is `null` |
| entry with `usage_window: hello` (not a mapping) | `Source.UsageWindow` is `null` — decision 2 |
| entry with `usage_window: { from: '…' }` only | `From` set, `To` is `null` — decision 1, at the parse level |
| `ToYaml` on a `Source` with a window | key present, **after** `last_modified` |
| `ToYaml` on a `Source` with `UsageWindow: null` | key absent entirely |
| `ToYaml` on a `Source` with `new UsageWindow(null, null)` | key present, empty mapping — *not* collapsed into absent |
| `ToYaml` → `ParseSources` round-trip | window preserved, both bounds |
| round-trip of `new UsageWindow(null, null)` | still present-and-empty, not `null` |
| `AddSource(..., usageWindow: …)` then `Build()` | the window reaches the document's frontmatter |

Extend the existing `ToYaml_uses_canonical_per_entry_key_order` rather than
writing a rival test beside it.

---

### Task 2: The override rule, and validation

**Files:** `src/OKF4net/Frontmatter.cs`, `src/OKF4net/Validate.cs`
**Test files:** `tests/OKF4net.Tests/FrontmatterTests.cs`, `tests/OKF4net.Tests/ValidateTests.cs`

**Consumes from Task 1:** `Source.UsageWindow`.

1. **The resolution accessor**, on `Frontmatter`:

   ```csharp
   /// <summary>
   /// The <c>usage_window</c> framing <paramref name="source"/>'s
   /// <c>usage_count</c> (§5.1): the entry's own window when it carries one,
   /// otherwise the shared sibling. …
   /// </summary>
   public UsageWindow? EffectiveUsageWindow(Source source) => source.UsageWindow ?? this.UsageWindow;
   ```

   Write `this.UsageWindow` explicitly — the property and its type share a name,
   and the bare form reads as ambiguous even where it compiles. The XML doc must
   state that the override is whole-object (decision 1), so a reader does not
   assume a merge.

2. **Validation.** Inside the existing `foreach (var src in fm.Sources)` loop in
   `Validate.cs` (immediately after the `LastModified` check, around line 396),
   add the per-entry bounds. Reuse the existing codes; the `Field` distinguishes
   the position, exactly as `MissingRecommendedField` and
   `LegacyDateOnlyTimestamp` already do:

   ```csharp
   if (src.UsageWindow is { } suw)
   {
       if (suw.From is { } suf)
       {
           CheckTemporal(diagnostics, concept, suf, "source usage_window from", "sources.usage_window.from", DiagnosticCode.UsageWindowInvalidFrom);
       }

       if (suw.To is { } sut)
       {
           CheckTemporal(diagnostics, concept, sut, "source usage_window to", "sources.usage_window.to", DiagnosticCode.UsageWindowInvalidTo);
       }
   }
   ```

   The label `"source usage_window from"` follows the existing
   `"source last_modified"`; the shared position keeps `"usage_window from"` and
   `usage_window.from` unchanged. **Add no new `DiagnosticCode` member** — that
   would shift every later member's ordinal for no gain.

3. **Update the XML docs of `UsageWindowInvalidFrom` / `UsageWindowInvalidTo`**
   (`Validate.cs:95-106`) to say they now cover both positions and that `Field`
   tells them apart. Leaving them describing only the shared one is the
   doc-accuracy defect this project has been caught on repeatedly.

**Tests:**

`FrontmatterTests` — the override rule:

| Case | Expectation |
|---|---|
| entry has a window, shared exists | entry's window wins |
| entry has none, shared exists | shared window |
| neither | `null` |
| entry has `{ from: X }`, shared has `{ from: Y, to: Z }` | result is `From: X, To: null` — **decision 1, the load-bearing test** |
| entry's `usage_window` is not a mapping, shared exists | shared window — decision 2 |

`ValidateTests` — one per classification, asserting the exact `Code` **and**
`Field`, mirroring the shared-position tests already in the file:

| Per-entry bound | Code | Field |
|---|---|---|
| `2026-01-01T00:00:00Z` | *(silent)* | — |
| `2026-01-01` | `LegacyDateOnlyTimestamp` | `sources.usage_window.from` |
| `2026-1-1T00:00:00Z` | `NonIso8601Timestamp` | `sources.usage_window.from` |
| `not-a-date` | `UsageWindowInvalidFrom` | `sources.usage_window.from` |

Plus one test with a bad bound in **both** positions in one document, asserting
two diagnostics distinguished by `Field` — that is what proves the reuse of the
codes is safe.

**Then verify, and report rather than adjust:**

- `dotnet test OKF4net.sln` — **no golden and no fixture may move** (spec §6,
  C1). If one does, STOP and report.
- `okf validate bundles/ga4` still reports 0 warnings.
- Report `okf validate bundles/acme_retail`'s total. It is a verbatim upstream
  copy and must **not** be edited; `bundles/README.md` documents the count, so
  if it moved, say so and I will decide about the doc.

---

### Task 3: Documentation

**Files:** `CHANGELOG.md`, and whatever Step 1 establishes.

1. **Do NOT amend the gap report.** Settled before writing this plan, so you do
   not have to: `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md` is a
   dated, generated snapshot — its own header says it compares the spec "against
   **OKF4net** at commit `e5d8e4c` (2026-07-31)" and that it is "a report for a
   human to act on". Editing its S5.1-3 entry would falsify that header, and
   `git log -- docs/spec-conformance/` shows no prior in-place amendment. It
   stays as the record of what was true then. The CHANGELOG entry below is where
   this branch records that S5.1-3 is closed; reference the finding id there so
   the two are linkable.
2. **`CHANGELOG.md`, under `Unreleased` → `Added`:** one entry stating that a
   `sources` entry's own `usage_window` is now read, validated against §5 and
   resolved through `Frontmatter.EffectiveUsageWindow`; that the override is
   whole-object; that `Source` gains an optional member and `AddSource` an
   optional parameter (source-compatible, not binary-compatible, 0.x); and that
   the existing `UsageWindowInvalid*` codes now cover both positions with
   `Field` distinguishing them.

   Do **not** claim the value was previously lost — it was not. `okf fmt`
   round-tripped it; the library simply could not see it. Say that.
