# §5 Timestamp Spelling — Design

**Status:** proposed, awaiting implementation
**Date:** 2026-08-31
**Branch:** `fix/temporal-conformance`
**Normative source:** `docs/spec/SPEC.md` (OKF v0.2, vendored verbatim at upstream
`62432a0`, `sha256 26aa5da0…`). Every `§` and every line number below refers to
**that file**, not to upstream `main` and not to a paraphrase.

---

## 1. Why this exists

`fix/temporal-conformance` made OKF4net read §5 timestamps as instants and warn
on the legacy date-only form. An external audit then found that the *conformance
decision* is made by a permissive parser, so spellings that are not ISO 8601 pass
with no diagnostic at all.

Reproduced on the current branch (`66c0b1b`), six concepts differing only in
their `stale_after` spelling — **none** produced a timestamp diagnostic:

```
2026-06-30T14:00:00Z        canonical
2026-06-30T14:00:00z        lowercase designator
2026-6-3T14:00:00Z          unpadded month and day
2026-06-30T14:00:00+0200    basic-format offset on an extended datetime
2026-06-30T14:00:00.123Z    fractional seconds
2026-06-30T14:00Z           seconds omitted
```

`2026-6-3T14:00:00Z` is not an ISO 8601 datetime. §5 says every timestamp-valued
key *is* one. So the validator is silent where the spec is not.

**The methodological failure behind it**, which this document exists to prevent
recurring: the previous round settled the strictness question by asking which
spellings *seemed* reasonable, and even offered "RFC 3339 strict" as an option —
a standard `SPEC.md` never cites (`grep -c 'RFC' docs/spec/SPEC.md` → 0). The
spec is the only authority here, and where it delegates, the delegate is the only
authority.

## 2. Normative basis, verbatim

| Line | Text |
|---|---|
| 284–285 | "Every timestamp-valued key in OKF is an ISO 8601 datetime with an explicit UTC offset, for example `2026-06-30T14:00:00Z`." |
| 332–334 | "`usage_window`: Written once as a sibling of `sources`, it frames every `usage_count` with a `{ from, to }` datetime range. A single entry MAY carry its own `usage_window` to override the shared one." |
| 378–379 | "`generated.at`: An ISO 8601 datetime marking the content's last meaningful change." |
| 389–390 | "`verified`: A list of verification events, each with `by` (an actor) and `at` (an ISO 8601 datetime)." |
| 430–431 | "Optional. An absolute instant. A concept is stale when `now >= stale_after`." |
| 550 | "Date headings MUST use ISO 8601 `YYYY-MM-DD` form." |
| 738–745 | §11: a bundle is conformant if (1) parseable frontmatter, (2) non-empty `type`, (3) reserved filenames follow §8/§9. |
| 746–747 | "producers SHOULD follow §5 through §10" |
| 755 | "Consumers SHOULD treat all other constraints as soft guidance." |

## 3. What the spec settles, and what it delegates

**Settled.**

- The set of timestamp-valued keys under §5: `sources[].last_modified`,
  `usage_window.from`, `usage_window.to` (§5.1), `generated.at`,
  `verified[].at` (§5.2), `stale_after` (§5.5).
- The required shape: an ISO 8601 datetime **and** an explicit UTC offset.
- The severity. §11's three conformance conditions do not include timestamp
  form, and §5 is a producer **SHOULD**. A bad spelling therefore yields a
  `Warning`, never an `Error`, and never a rejected bundle (line 755, and
  §11's explicit `MUST NOT reject` list). This is already the case and does
  not change.
- §9 log date headings are **bare `YYYY-MM-DD`** and are outside §5's rule
  (line 550). Unchanged; already pinned by a test.

**Delegated.** The spec names "ISO 8601" and stops. It enumerates no spellings
and cites no profile (no RFC 3339, no `RFC` string anywhere in the file). So
ISO 8601 itself is the authority for the grammar, and nothing else is —
in particular not the author's sense of what looks tolerable.

## 4. The grammar, and the oracle that checks it

**Grammar (ISO 8601 extended format with an explicit UTC offset):**

```
YYYY "-" MM "-" DD "T" hh ":" mm [ ":" ss [ "." s+ ] ] offset
offset = "Z" | ("+" | "-") hh ":" mm
```

Every component is fixed-width. The UTC designator is the uppercase `Z`.
Seconds may be omitted (ISO 8601 reduced precision) and may carry a fraction.
The representation is wholly *extended*: ISO 8601 does not permit an extended
date and time to carry a basic-format offset, so `+02:00` is in and `+0200` is
out.

**The oracle.** A grammar derived by reasoning is exactly what produced the two
defects this branch has already fixed. So it is checked against evidence the
author does not control: **every timestamp literal the spec itself writes.**

```sh
grep -oE "[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9:.]+([Z]|[+-][0-9:]+)" docs/spec/SPEC.md
```

At `62432a0` that yields **29 occurrences, 18 distinct**, all of the form
`…THH:MM:SSZ` bar one, `2026-05-28T22:53:05+00:00` (the §13.1 legacy example,
and the only reason the grammar must accept an extended `±HH:MM` offset as well
as `Z`). A test extracts them from the vendored file at run time and asserts
each classifies **Conformant**.
If the grammar ever rejects a spelling the spec itself uses, the grammar is
wrong — not the spec. This test is the reason the grammar is not merely an
opinion.

**Consequences, stated plainly.** `2026-6-3T…`, `…T14:00:00z` and `…+0200`
become flagged. Not because they are disliked: because ISO 8601 fixes component
widths, fixes the designator's case, and forbids mixing basic and extended
forms. Each is still **read** — §11 forbids dropping it.

## 5. Model

Reading stays permissive (§11); only classification is strict.

```csharp
internal enum TimestampForm
{
    Unreadable,      // not a timestamp at all
    Conformant,      // §5: ISO 8601 extended, explicit UTC offset
    LegacyDateOnly,  // bare YYYY-MM-DD, or a datetime with no offset
    NonIso8601,      // explicit offset present, spelling is not ISO 8601
}

internal static TimestampForm Classify(string raw, out DateTimeOffset instant);
```

Two diagnostic codes, per the decision taken on this branch:

| Form | Code | Severity |
|---|---|---|
| `LegacyDateOnly` | `LegacyDateOnlyTimestamp` (existing) | Warning |
| `NonIso8601` | **new code** | Warning |
| `Unreadable` | the field's existing `*Invalid*` code | Warning |

Splitting them matters because the two say different things to a producer:
`LegacyDateOnly` means "you wrote a v0.1-era value, here is the v0.2 form";
`NonIso8601` means "you meant the v0.2 form and mistyped it". One code for both
would make the message wrong for one of them — the exact defect fixed in
`65b7d9b`.

### 5.1 How `stale_after` reaches the validator — decided during verification

The other five keys already go through one `CheckTemporal`
(`Validate.cs:316, 333, 378, 386, 391`). `stale_after` does **not**: it is
handled separately at `Validate.cs:407–413`, reading
`Lifecycle.StaleAfterMalformed` and `Lifecycle.StaleAfterIsLegacyDate`. So the
count today is five of six, and closing it is part of this work rather than a
description of the present.

`StaleAfterIsLegacyDate` is **public API** on a public record struct, and its
documented meaning is precisely "bare date, or datetime with no offset". A
`NonIso8601` value is neither, so that property must keep returning `false` for
it — quietly widening it would repeat the mislabelling fixed in `65b7d9b`.

**Decision:** route `stale_after` through `CheckTemporal` like the other five,
reading the form from `OkfTimestamp.Classify(lc.StaleAfterRaw)`.
`StaleAfterIsLegacyDate` keeps its exact meaning and stays public for consumers,
with no internal caller — the same standing as `IsIso8601DateTime`.

**Alternative considered and rejected:** making `TimestampForm` public and
exposing `Lifecycle.StaleAfterForm`. It is the richer API, but it commits a new
public enum to the surface for one internal need, and the branch is already
carrying a breaking change. If a consumer ever asks for the distinction, that is
the shape to add — additively, later.

With that, all six keys go through one `CheckTemporal`, so a spelling cannot be
conformant in one field and not another.

## 6. Out of scope, with the reason

- **§9 log date headings.** Line 550 pins them to bare `YYYY-MM-DD`.
  `ChangeLog.IsIsoDate` stays untouched; a test already pins the boundary.
- **The legacy `timestamp` field (§13.1).** Its *presence* already warns
  (`LegacyTimestamp`); its value's spelling is not checked. §13.1 supersedes the
  field entirely, so a second warning about how a superseded field is spelled
  adds noise, not information. Recorded as a deliberate choice, not an oversight.
- **Per-entry `usage_window` override.** §5.1 (line 333) allows a `sources`
  entry to carry its own `usage_window`. OKF4net parses only the top-level one,
  so an override's bounds are neither read nor validated. This is a pre-existing
  §5.1 feature gap (`docs/spec-conformance/…`, S5.1-3), **not** created here —
  but it bounds the claim "all six §5 keys": six *named* keys are covered, and
  the override is a seventh and eighth position that is not. Fixing it is a
  §5.1 parsing change, not a spelling change, and belongs to its own piece of
  work.

## 7. Blast radius

- `src/OKF4net/Internal/OkfTimestamp.cs` — grammar + `Classify`.
- `src/OKF4net/Validate.cs` — new `DiagnosticCode`, `CheckTemporal` gains the
  fourth case.
- `src/OKF4net/Lifecycle.cs` — `StaleAfterIsLegacyDate` must not silently
  absorb `NonIso8601`; see §8 claim C4.
- Fixtures: none expected to change — every fixture timestamp is already
  canonical or deliberately legacy. **To be verified, not assumed.**
- Goldens: none expected to move. **To be verified.**
- `bundles/acme_retail`: verbatim upstream, untouched whatever the count.
- Public API: `BundleValidator.IsConformantInstant` keeps its signature; its
  meaning tightens. `TimestampForm` is internal.

## 8. Claims verified against the code

Checked one line at a time on `66c0b1b` **before** writing any implementation.
Three claims came back wrong and are corrected above; that is what this section
is for.

| | Claim | Result |
|---|---|---|
| **C1** | No fixture under `tests/fixtures/` would be newly flagged | ✅ every fixture timestamp is canonical `…THH:MM:SSZ` or deliberately legacy |
| **C2** | `bundles/ga4/` stays at 0 warnings | ✅ its 9 non-canonical-looking values are `+00:00`, an extended offset the grammar accepts |
| **C3** | "The 20 spec literals satisfy the grammar" | ⚠️ **count wrong** — 29 occurrences, 18 distinct. All satisfy it. Corrected in §4 |
| **C4** | `StaleAfterIsLegacyDate` is consumed only by `Validate.cs` | ✅ one call site (`:413`) — but it is **public API**, which the claim ignored. See §5.1 |
| **C5** | `HasExplicitOffset` stays the lenient readability gate | ✅ `private`, single caller inside `OkfTimestamp` |
| **C6** | `samples/` and `producers/` emit nothing this would flag | ✅ no timestamp literal outside the canonical form |
| **C7** | *(added by verification)* all six keys already share `CheckTemporal` | ❌ **false** — five do; `stale_after` is separate. See §5.1 |

The three corrections matter beyond bookkeeping: C3 was a miscount that would
have made the oracle test assert the wrong arity, C4 missed a public-API
consequence, and C7 was a claim about the present tense that was actually a
description of the goal.

## 9. Verification plan

1. `dotnet build OKF4net.sln` — 0 warnings (warnings are errors).
2. `dotnet test OKF4net.sln` — full suite, **in Debug** (CI's configuration).
3. `dotnet format OKF4net.sln --verify-no-changes`.
4. The spec-literal oracle test passes.
5. Re-run the six-spelling probe from §1 and confirm exactly the intended
   three are flagged.
6. `okf validate` on `bundles/ga4` still reports 0 warnings (C2).

## 10. Risk

The grammar is hand-written scanning of untrusted input — the same shape as the
two defects already found on this branch, and the class the project's own memory
flags as having survived five internal reviews. Mitigation: the oracle test
above, a table-driven case battery covering each rejection reason separately,
and no reliance on `DateTimeOffset.TryParse` for any conformance decision.
