# §5 Timestamp Spelling — Implementation Plan

**Spec (binding authority):** `docs/superpowers/specs/2026-08-31-okf-timestamp-spelling-design.md`
**Normative source:** `docs/spec/SPEC.md` (OKF v0.2, vendored verbatim, `sha256 26aa5da0…`).
Read the spec before Task 1; it holds the reasoning, the line anchors, and the
verified claims. This plan holds only the work.

## Global Constraints

- **`docs/spec/SPEC.md` is the only authority.** Where it delegates ("an ISO 8601
  datetime"), the delegate is the only authority. It cites no RFC — do not
  import RFC 3339 or any other profile. Never edit `docs/spec/SPEC.md`.
- **Zero third-party runtime dependencies** in `src/OKF4net`. BCL only.
- **Warnings are errors** (`TreatWarningsAsErrors`). `dotnet build OKF4net.sln`
  must be clean.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- File-scoped namespaces, XML doc comments on public API, nullable enabled.
- **Never edit `tests/fixtures/` to make a failing test pass.** The spec's §7
  verified that no fixture should change; if one does, that is a finding to
  report, not a fixture to edit.
- **A Debug test run is the gate** (`dotnet test OKF4net.sln`), because CI runs
  Debug. If `bin/Debug` is locked by another process, say so in the report and
  run `-c Release` as a fallback — do not silently substitute it.
- Verification: `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln`,
  `dotnet format OKF4net.sln --verify-no-changes`.

---

### Task 1: The §5 grammar and `Classify`

**File:** `src/OKF4net/Internal/OkfTimestamp.cs`
**Test file:** `tests/OKF4net.Tests/OkfTimestampTests.cs`

Add a four-state classification to the existing `OkfTimestamp`, replacing the
`bool isLegacyForm` model. Reading stays permissive (§11: a readable value is
never dropped); only the classification becomes strict.

```csharp
internal enum TimestampForm { Unreadable, Conformant, LegacyDateOnly, NonIso8601 }

internal static TimestampForm Classify(string raw, out DateTimeOffset instant);
```

- `Conformant` — matches the §5 grammar below.
- `LegacyDateOnly` — bare `YYYY-MM-DD`, or a datetime with no offset at all.
  Unchanged meaning; these already work.
- `NonIso8601` — carries an explicit offset, but the spelling is not ISO 8601.
- `Unreadable` — not a timestamp at all.

**The grammar** (ISO 8601 extended format with an explicit UTC offset), as
shipped — implementation widened the first draft twice and narrowed it once; see
the design doc's §4 for each delta and its ISO 8601 citation:

```
YYYY "-" MM "-" DD "T" hh ":" mm [ ":" ss [ ("." | ",") s+ ] ] offset
offset = "Z" | ("+" | "-") hh [ ":" mm ]
       , except that a negative zero offset ("-00" / "-00:00") is NOT conformant
```

Every component is fixed-width. The designator is the uppercase `Z`. Seconds may
be omitted (ISO 8601 reduced precision) and may carry a fraction, whose decimal
sign may be `.` or `,` (§4.2.2.4 names the comma the *preferred* one). The
representation is wholly extended, so `+02:00` is in and `+0200` is out — but a
reduced-precision `+02`, having no minutes to separate, is not basic/extended
mixing and is in. A negative zero offset is out (ISO 8601:2004 §4.2.5.2 / 2019
§4.3.13: a zero difference from UTC takes a plus sign; only RFC 3339 permits
`-00:00`, and `SPEC.md` cites no RFC). `+00:00` stays in — it is the spelling of
one of the spec's own 18 literals.

Keep `TryParse` working (`isLegacyForm` becomes `form is LegacyDateOnly`) and
keep `IsConformant` (`form is Conformant`) — Task 2 re-points their callers.
`HasExplicitOffset` stays private and stays lenient: it is the *readability*
gate, and tightening it would make a non-ISO spelling unreadable rather than
flagged, which §11 forbids.

**Tests — the oracle first.** Write this one before the grammar:

```csharp
[Fact]
public void Every_timestamp_the_spec_itself_writes_is_conformant()
```

It reads `docs/spec/SPEC.md` (locate the repo root the way the other tests do —
see `TestPaths.RepoRoot()`), extracts every timestamp literal with
`[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+([Z]|[+-][0-9:]+)`, asserts it found **18
distinct** values, and asserts every one classifies `Conformant`. This is the
check the grammar answers to: if it rejects a spelling the spec itself uses, the
grammar is wrong, not the spec.

Then a table-driven battery, each rejection reason separately:

| Input | Expected |
|---|---|
| `2026-06-30T14:00:00Z` | `Conformant` |
| `2026-06-30T14:00:00+02:00` | `Conformant` |
| `2026-05-28T22:53:05+00:00` | `Conformant` |
| `2026-06-30T14:00:00.123Z` | `Conformant` |
| `2026-06-30T14:00Z` | `Conformant` |
| `2026-06-30T14:00:00,123Z` | `Conformant` |
| `2026-06-30T14:00:00+02` | `Conformant` |
| `2026-6-3T14:00:00Z` | `NonIso8601` |
| `2026-06-3T14:00:00Z` | `NonIso8601` |
| `2026-06-30T4:00:00Z` | `Unreadable` ¹ |
| `2026-06-30T14:00:00z` | `NonIso8601` |
| `2026-06-30T14:00:00+0200` | `NonIso8601` |
| `2026-06-30T14:00:00-00:00` | `NonIso8601` |
| `2026-06-30T14:00:00-00` | `NonIso8601` |
| `2026-07-01` | `LegacyDateOnly` |
| `2026-07-01T12:00:00` | `LegacyDateOnly` |
| `2026-07-01 12:00:00` | `LegacyDateOnly` |
| `""` / `not-a-date` / `2026-13-01T00:00:00Z` / `2026-01-01T25:00:00Z` | `Unreadable` |
| `01/02/2026` / `2026` / `July 1, 2026` | `Unreadable` |

¹ Corrected after execution: the readability gate — `DateTimeOffset.TryParse`,
deliberately unchanged — accepts an unpadded month or day but rejects an
unpadded *hour* outright, so `2026-06-30T4:00:00Z` never reaches the spelling
check at all. That asymmetry is the BCL's, not the grammar's.

Plus: a `NonIso8601` value still yields its instant (`2026-6-3T14:00:00Z` →
2026-06-03T14:00:00Z), because §11 forbids dropping a readable value.

This is hand-written scanning of untrusted input — the class of bug this project
has seen survive five reviews. Add cases for anything you find while writing it.

---

### Task 2: Wire the validator, and close the sixth key

**Files:** `src/OKF4net/Validate.cs`, `src/OKF4net/Lifecycle.cs`
**Test file:** `tests/OKF4net.Tests/ValidateTests.cs`

**Consumes from Task 1:** `TimestampForm`, `OkfTimestamp.Classify`.

1. Add one `DiagnosticCode` beside `LegacyDateOnlyTimestamp` for the
   `NonIso8601` case. Name it for what it is; document it with the §5 sentence.
   Message shape, matching the existing one:
   `<label> <quoted raw> is not an ISO-8601 spelling; §5 wants an ISO-8601 datetime with an explicit UTC offset`
2. `CheckTemporal` gains the fourth case. Its `Field` values stay exactly as they
   are today.
3. **Route `stale_after` through `CheckTemporal`.** It is currently handled
   separately at `Validate.cs:407–413` via `Lifecycle.StaleAfterMalformed` and
   `Lifecycle.StaleAfterIsLegacyDate` — five of the six §5 keys share
   `CheckTemporal`, not six. The staleness check (`lc.IsStale(now)` →
   `ConceptStale`) is a separate concern and stays.
4. **`Lifecycle.StaleAfterIsLegacyDate` must keep returning `false` for a
   `NonIso8601` value.** It is public API documented as "bare date, or datetime
   with no offset"; widening it silently would repeat the mislabelling fixed in
   `65b7d9b`. Do not make `TimestampForm` public.

**Tests:** conformant / legacy / non-ISO / unreadable for `stale_after`,
`generated.at` and `sources[].last_modified` at minimum, asserting the exact
`Code` and `Field`. Plus: `StaleAfterIsLegacyDate` is `false` for
`2026-6-3T14:00:00Z` while the validator still warns about it.

**Then verify, and report what you find rather than adjusting it:**

- `dotnet test OKF4net.sln` — the spec's §7 predicts **no golden and no fixture
  changes**. If a golden moves, stop and report it; do not edit `tests/fixtures/`.
- `okf validate bundles/ga4` must still report 0 warnings.
- `CHANGELOG.md`, under `Unreleased` → `Fixed`: one entry, stating that
  conformance was decided by a permissive parser so non-ISO spellings passed
  silently, and that the grammar is now checked against the spec's own literals.
