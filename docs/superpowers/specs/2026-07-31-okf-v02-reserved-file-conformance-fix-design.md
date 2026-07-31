# Design: §11 conformance for malformed reserved files (`index.md`/`log.md`)

## Problem

OKF v0.2's §11 conformance rests on three declarative conditions:

1. Every non-reserved `.md` file has a parseable frontmatter block.
2. Every frontmatter block has a non-empty `type`.
3. Every reserved filename (`index.md`, `log.md`) follows the structure in
   §8 and §9 respectively, when present.

`BundleValidator` (`src/OKF4net/Validate.cs`) implements conditions 1 and 2
correctly as `Severity.Error` (`Validate.cs:250-256`, `:263-272`). Condition
3 — implemented entirely by `ValidateReserved` (`Validate.cs:507-617`) — was
never wired to `Severity.Error` at all: every structural violation it
detects tops out at `Warning`, and two failure modes (a reserved file that
can't be read, or one that can't be parsed as a document) are silently
swallowed with **no diagnostic whatsoever**. `okf validate` therefore
reports `IsConformant = true` (exit code 0) for bundles that are not
actually §11-conformant.

This contradicts:
- The class's own docstring (`Validate.cs:8-21`), which states the same
  three conditions and says `Error` is reserved for "true §11 violations."
- The original implementation plan,
  `docs/superpowers/plans/2026-07-27-okf-v0.2-core-and-cli.md:18`: **"Error
  severity is reserved for §11 conformance only (unparseable frontmatter,
  missing/empty `type`, malformed reserved files)."** That plan's intent
  for the third category was simply never implemented — this design
  corrects the implementation to match the plan's own stated intent, not
  the other way around. (The plan itself is a historical, already-executed
  record; it is not being edited retroactively — this doc supersedes it for
  this one behavior.)

The gap was surfaced by `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md`,
finding **S11-3** (Critical, `Diverges`/Undocumented) — the one
undocumented, unresolved finding in that report.

## Goal

Make `BundleValidator` correctly enforce §11 condition 3: a bundle with a
malformed reserved file is reported non-conformant (`Severity.Error`,
`IsConformant = false`, `okf validate` exit code 1), with every failure
mode — including "couldn't even read/parse it" — producing a diagnostic.
Severity is decided **per case**, against the actual normative force of the
relevant §8/§9/§12 text, not a blanket promotion.

## Non-goals

- Not touching `Bundle.Load`'s permissive-loading contract (§3): reserved
  files stay collected without any parse attempt at load time
  (`Bundle.cs:140-145`). `ValidateReserved` remains the sole place that
  reads and judges them — this fix makes it actually report what it finds,
  it does not move where the check happens.
- Not touching golden fixtures beyond additions. Verified directly against
  the current fixture tree: no existing fixture bundle contains a
  non-root `index.md` with frontmatter, a root `index.md` with extra keys,
  or a non-ISO-8601 `log.md` date heading (`tests/fixtures/okf_v02/index.md`
  and `tests/fixtures/okf_v02_computation/index.md` both carry only the
  sanctioned root `okf_version` key; every `log.md` date heading found —
  `appendix_a`, `golden/index-input`, `bundles/acme_retail` — is valid
  ISO-8601). No existing fixture's expected output changes. (The user has
  authorized modifying existing fixtures for this specific fix if one
  turns out to be affected during implementation — this is the documented
  reason for that exception, per `CLAUDE.md`'s fixture rule — but it is not
  expected to be exercised.)
- Not revisiting `okf_version` handling beyond confirming it stays
  `Warning` — see the severity table below; §12 explicitly forbids treating
  an unrecognized declared version as grounds for refusal.
- Not adding an `okf validate --strict`-style opt-out. The three §11
  conditions are supposed to be unconditional; this fix makes condition 3
  actually behave that way, matching conditions 1 and 2.

## Severity mapping (case by case, per actual spec text)

Re-read directly from the live spec (`GoogleCloudPlatform/knowledge-catalog`,
`okf/SPEC.md`) rather than assumed:

| Case | Current | New | Spec basis |
|---|---|---|---|
| Reserved file unreadable (I/O/encoding) or fails to parse as a document | *(nothing — silently skipped)* | **Error** | No literal §8/§9 text, but analogous to §11 condition 1's treatment of non-reserved unparseable documents: a file that cannot even be parsed cannot be said to "follow its structure." |
| `index.md` (non-root) declares frontmatter | Warning | **Error** | §8: *"Index files contain no frontmatter, with one exception..."* — declarative, same normative weight as §11's own three conditions (neither uses a modal keyword either). |
| `index.md` (root) declares keys beyond `okf_version` | Warning | **Error** | Same §8 sentence — root frontmatter's *only* sanctioned content is `okf_version`. |
| `log.md` date heading not `YYYY-MM-DD` | Warning | **Error** | §9: *"Date headings **MUST** use ISO 8601 `YYYY-MM-DD` form."* — explicit MUST. |
| `index.md` (root) declares an unrecognized `okf_version` | Warning | **Warning (unchanged)** | §12: *"Consumers that do not understand the declared version **SHOULD attempt best-effort consumption rather than refusing the bundle**."* Promoting this to Error would directly contradict that SHOULD. |

## New `DiagnosticCode` members

No code exists today for "reserved file couldn't be processed at all" —
add two, next to the other reserved-file codes in the enum
(`Validate.cs`, near `IndexHasFrontmatter`/`RootIndexExtraFrontmatter`/
`UnsupportedOkfVersion`/`LogDateInvalid`):

```csharp
/// <summary>A reserved <c>index.md</c> could not be read or parsed (§8, §11).</summary>
UnparseableIndex,

/// <summary>A reserved <c>log.md</c> could not be read (§9, §11).</summary>
UnparseableLog,
```

`UnparseableLog` is read-failure-only in practice: `ChangeLog.Parse` is
permissive by design (it never throws; malformed date headings are
reported via `InvalidDates()`, not a parse exception), so the only way a
`log.md` fails to become processable at all is an I/O/encoding error while
reading it. `UnparseableIndex` covers both — a read failure, or
`OkfDocument.Parse` throwing `DocumentParseException` — since both mean
"this `index.md` cannot be judged against §8 at all," and modeling that as
one code (mirroring the existing `UnparseableDocument` code's own
message pattern, `"unparseable concept document: {error}"`) keeps the
enum from fragmenting into read-vs-parse variants that no consumer needs
to distinguish.

Message text: `"unparseable index.md: {error}"` / read failures phrase the
`{error}` from the caught exception's `Message`; parse failures phrase it
from `DocumentParseException.Message` — both land in the same
`{error}` slot, consistent with how `UnparseableDocument` already does this
for non-reserved files (`Validate.cs:254`, `$"unparseable concept
document: {error}"`).

## Architecture: `ValidateReserved` changes

`Validate.cs:507-617`. Each of the three currently-silent `continue`
branches per reserved-file loop (I/O exception, `UnauthorizedAccessException`,
`DecoderFallbackException`, `DocumentParseException`) is replaced with a
diagnostic push before the `continue`:

- In the `index.md` loop (`Validate.cs:511-584`): the three read-exception
  catches and the `DocumentParseException` catch each add a
  `Severity.Error` diagnostic with `DiagnosticCode.UnparseableIndex` before
  continuing to the next file. The existing `IndexHasFrontmatter` and
  `RootIndexExtraFrontmatter` diagnostics change from `Severity.Warning` to
  `Severity.Error` — no other change to their construction.
  `UnsupportedOkfVersion` stays `Severity.Warning`, unchanged.
- In the `log.md` loop (`Validate.cs:586-616`): the three read-exception
  catches add a `Severity.Error` diagnostic with `DiagnosticCode.UnparseableLog`
  before continuing. The existing `LogDateInvalid` diagnostic changes from
  `Severity.Warning` to `Severity.Error` — no other change to its
  construction.

No change to `Bundle.cs`, `Bundle.Load`, `OkfDocument`, `ChangeLog`, or
anything upstream of `ValidateReserved` — this is confined entirely to one
method's diagnostic severities plus two new diagnostic-emitting branches
where none existed.

## Tests

- **Existing tests to update** (currently assert the soon-to-be-wrong
  behavior):
  - `Nonroot_index_with_frontmatter_is_a_warning`
    (`tests/OKF4net.Tests/ValidateTests.cs:139-152`) — rename to
    `Nonroot_index_with_frontmatter_is_an_error`; assert
    `report.Of(Severity.Error)` instead of `Of(Severity.Warning)`; assert
    `report.IsConformant == false`.
  - `Invalid_log_date_heading_is_a_warning`
    (`tests/OKF4net.Tests/ValidateTests.cs:191-203`) — rename to
    `Invalid_log_date_heading_is_an_error`; same Error/`IsConformant`
    updates.
  - Any test covering root `index.md` with extra keys beyond
    `okf_version` (grep `RootIndexExtraFrontmatter` in
    `ValidateTests.cs` for the exact test name at implementation time) —
    same Error/`IsConformant` updates.
  - `Root_index_frontmatter_with_only_okf_version_is_clean`
    (`ValidateTests.cs:155+`) and `Valid_log_date_heading_produces_no_warning`
    (`ValidateTests.cs:206+`) — these are the "nothing wrong" control
    cases; confirm they still pass unchanged (they should — no diagnostic
    is expected either way).
  - Any test asserting `okf_version` unsupported stays `Warning` — no
    change needed, but confirm one exists and still passes.
- **New tests** (zero coverage today):
  - An `index.md` with unreadable/unparseable content (e.g. malformed YAML
    frontmatter fence: `"---\ntype: [unterminated\n---\n"`) →
    `Severity.Error`, `DiagnosticCode.UnparseableIndex`,
    `IsConformant == false`.
  - A `log.md` that cannot be read (simulate via a genuinely unreadable
    file — e.g. a directory named `log.md`, or an access-denied path if the
    test platform allows constructing one reliably; if no reliable
    cross-platform way exists, that's fine to note as a documented gap
    rather than force a flaky test) → `Severity.Error`,
    `DiagnosticCode.UnparseableLog`.
- **CLI-level**: confirm `okf validate` on a bundle with any of these
  violations now exits `1` (was `0`), via `OkfCliTests` or equivalent
  existing CLI test harness.

## Fixtures

Add new golden fixtures (no existing ones affected — verified above) under
`tests/fixtures/`, following the existing `okf_v02*` naming convention,
demonstrating `okf validate`'s new output for at least: a non-root
`index.md` with frontmatter, and a `log.md` with a non-ISO-8601 date
heading. Exact fixture bundle contents and captured `.out` files are an
implementation-time decision (the plan should specify them precisely,
hand-verified against this design — not against a reference binary, per
`CLAUDE.md`'s v0.2 fixture-addition allowance).

## CHANGELOG

A `Fixed` entry (Keep a Changelog format) in `[Unreleased]`: something like
*"`okf validate` now correctly reports non-conformance (§11) for malformed
reserved files — previously a malformed `index.md`/`log.md` (bad structure,
or unreadable/unparseable) was under-reported as `Warning` or produced no
diagnostic at all, so `okf validate` incorrectly exited `0`."* This is
framed as a bug fix (the tool was wrong), not a breaking behavior change,
even though it does change the exit code for any bundle that happens to
have a malformed reserved file — pre-1.0, this doesn't require a major
version bump, and no shipped `bundles/`/`samples/` bundle is affected
(verified above).

## Provenance

- Original stated intent:
  `docs/superpowers/plans/2026-07-27-okf-v0.2-core-and-cli.md:18` (not
  edited by this work — historical record).
- Gap discovered by: `docs/spec-conformance/2026-07-31-okf-spec-gap-report.md`,
  finding S11-3.
- This document is the design for closing that finding.

## Risks

- **CLI exit-code change for real users**: any bundle anyone has that
  currently has a malformed reserved file will start failing `okf
  validate`. This is intentional (the point of the fix) and framed as a
  bugfix in the CHANGELOG, not silently.
- **No fixture/sample-bundle regression**: verified directly against the
  current tree (see Non-goals) — nothing in this repo's own fixtures or
  shipped sample bundles is affected.
- **`ChangeLog.Parse` never throwing** means `UnparseableLog` is
  read-failure-only in practice, not a general "malformed log.md content"
  catch-all — documented explicitly above so a future reader doesn't
  expect it to fire on bad date headings (which stays `LogDateInvalid`,
  now `Error`).
