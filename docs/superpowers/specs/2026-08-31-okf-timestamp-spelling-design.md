# §5 Timestamp Spelling — Design

**Status:** implemented on `fix/temporal-conformance` (`52801f2`, `787f237`,
`538d949`). §4 and §6 below have been brought back in line with what actually
shipped after two rounds of review — the grammar widened twice and narrowed once
during implementation, and this document is only useful if it describes the code
rather than the first draft of it. §7's predictions held: no fixture and no
golden moved for this work.
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

**Grammar (ISO 8601 extended format with an explicit UTC offset)** — as shipped,
after implementation widened the first draft twice and narrowed it once:

```
YYYY "-" MM "-" DD "T" hh ":" mm [ ":" ss [ ("." | ",") s+ ] ] offset
offset = "Z" | ("+" | "-") hh [ ":" mm ]
       , except that a negative zero offset ("-00" / "-00:00") is NOT conformant
```

Every component is fixed-width. The UTC designator is the uppercase `Z`.
Seconds may be omitted (ISO 8601 reduced precision) and may carry a fraction.
The representation is wholly *extended*: ISO 8601 does not permit an extended
date and time to carry a basic-format offset, so `+02:00` is in and `+0200` is
out.

Three deltas from this document's first draft, each found by implementing it and
each a case where the draft was stricter than ISO 8601 itself — the very defect
class the branch exists to fix:

- **The comma decimal sign.** ISO 8601 §4.2.2.4 names the comma the *preferred*
  sign and the full stop the alternative, so `…T14:00:00,123Z` is conformant.
  The draft's `"." s+` would have flagged the preferred spelling.
- **The reduced-precision `±hh` offset.** An offset with no minutes has nothing
  to separate, so it is not basic/extended mixing: `+02` is in, while `+0200`
  (minutes, unseparated) stays out. The draft's mandatory `":" mm` would have
  flagged `+02`.
- **The negative zero offset, narrowing the other way.** `-00:00` and its
  reduced form `-00` parse and match the component shape, but ISO 8601 forbids
  them (2004 §4.2.5.2, 2019 §4.3.13): a zero difference from UTC takes a plus
  sign, so `Z` and `+00:00` spell it. Only RFC 3339 §4.3 permits `-00:00`, with
  its own "offset unknown" meaning — and `SPEC.md` cites no RFC, so that licence
  does not reach here. They classify `NonIso8601` and are still read as the
  instant they denote. `+00:00` stays conformant: it is the spelling of one of
  the spec's own 18 literals, so the sign of the zero is what decides, not the
  zero. A negative *non*-zero offset (`-05:00`, `-05`) is conformant.

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

**The oracle's reach, and the guard that keeps it honest.** That extraction is
modelled on the §5 grammar it validates, so on its own it is blind to exactly
the forms the grammar does not describe: a wholly-basic `20260630T140000Z`, a
week date, an ordinal date, a lowercase `z`, a comma fraction. The vendored spec
contains none of them today — verified, not assumed — so the 18 really are every
timestamp literal it writes. But that is a fact about *this* spec revision, not a
property of the test: a future vendored spec could add one, still yield 18
matches, and the new form would go unvalidated while the test stayed green. The
oracle would quietly stop being an oracle.

So the test carries a second, **shape-agnostic** scan — any token with a time and
a zone designator, in any ISO 8601 form — and asserts everything it finds is
already covered by the strict extraction. Proven to bite by mutation: appending
`20260630T140000Z` to a throwaway copy of the spec fails the test with
*"The spec now writes 1 timestamp literal(s) the §5 extraction cannot see"*. The
universal claim above is therefore the test's claim too, not a stronger one.

**Consequences, stated plainly.** `2026-6-3T…`, `…T14:00:00z`, `…+0200` and
`…-00:00` become flagged. Not because they are disliked: because ISO 8601 fixes
component widths, fixes the designator's case, forbids mixing basic and extended
forms, and forbids a negative zero offset. Each is still **read** — §11 forbids
dropping it.

One spelling in the same family is **not** flagged as `NonIso8601`, because it
is not readable at all: `2026-06-30T4:00:00Z`, with an unpadded hour.
`DateTimeOffset.TryParse` — the readability gate ahead of the spelling check,
deliberately left as it was — accepts an unpadded month or day but rejects an
unpadded hour outright. So it classifies `Unreadable`, not `NonIso8601`. That
asymmetry lives in the BCL's permissive parser, not in this grammar; it was
settled by executing it, not by reading. See §6 for the wider consequence.

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

- **ISO 8601 spellings `DateTimeOffset.TryParse` cannot read.** The readability
  gate is the BCL parser, unchanged by this work, and it refuses several
  spellings that *are* genuine ISO 8601 datetimes with an explicit UTC offset —
  verified by execution, not assumed: the wholly-basic `20200630T140000Z`
  (§4.3.2), a leap second `…T23:59:60Z` (§4.2.2.2 admits `[60]` "only to
  indicate a positive leap second"), a week date `2026-W27-1T14:00:00Z` and an
  ordinal date `2026-181T14:00:00Z` (§4.3.2: "ordinal dates or week dates may be
  substituted"). All classify `Unreadable`: they yield **no instant**, so a
  `stale_after` written that way is never evaluated for staleness, and its key
  gets the field's `*Invalid*` code. Reading them is a parser rewrite, not a
  spelling change, and no literal in `SPEC.md` uses any of these forms — so the
  cost is real and the benefit is hypothetical. What this *does* require is that
  the validator stop claiming they are not ISO 8601, which would be false of
  every one of them: the `Unreadable` message says only that the value could not
  be read as a timestamp. Pinned by
  `OkfTimestampTests.Iso8601_forms_the_bcl_parser_cannot_read_are_Unreadable`
  and `ValidateTests.An_unreadable_value_is_not_told_it_is_not_iso8601`.
- **The BCL's digit-grouped offset, `2026-06-30T14:00:00+002`.** Recorded as a
  real behaviour change, not a non-event. It matches no ISO 8601 offset form, so
  it warns `NonIso8601` — but it is now *readable*: `DateTimeOffset.TryParse`
  groups the digits as `+00:2` and yields an offset of **+00:02**, two minutes
  away from what a reader would guess. At `ccacfc5`, `stale_after` produced no
  instant at all for it, so nothing was computed from the wrong value.
  Deliberately kept readable anyway: §11 forbids dropping a value that parses,
  the warning names the spelling as wrong in the same breath, and two minutes of
  skew on a value the producer is being told to fix is a smaller harm than
  silently ignoring a staleness deadline. Worth knowing it moved.
- **End-of-day `2020-06-30T24:00:00Z` is NOT in this list**, though an earlier
  round of this document put it there. ISO 8601 admits `[24]` for the hour, but
  §4.2.2.2 allows it "only to indicate the end of a calendar day within a time
  interval", and §4.2.3 NOTE 3 is explicit: "The end of day representation,
  where [hh] has a value of [24], **shall not be used for a single time point**."
  `SPEC.md` §5.5 makes `stale_after` "an absolute instant" — a single time
  point. So `24:00` is not a valid spelling for any §5 key, and reporting it as
  unreadable is not a gap to apologise for. Corrected on 2026-09-01 after
  obtaining a normative source (see §11).
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

## 11. The delegate, and what it actually says

`SPEC.md` §5 delegates to "ISO 8601" and stops, so ISO 8601 decides this
grammar. Through the implementation and its three review rounds, every ISO rule
below was applied **from recalled knowledge, with no source consulted** — the
single largest weakness in this work, and the one no amount of internal review
could have caught, because every reviewer shared the same blind spot.

ISO 8601-1:2019 is not free (CHF 181; ISO publishes only a cover-and-terms
preview). The rules were therefore checked on 2026-09-01 against
**ISO/TC 154/WG 5 N0038, ISO/WD 8601-1, 2016-02-16** — the drafting committee's
own working draft, publicly archived. It is not the published text and its
clause numbers may have shifted for the 2019 edition, so it is cited here as
*strong evidence*, not as the standard. It is **not vendored**: it is © ISO 2016
and the repo has no licence to redistribute it.

| Rule the grammar enforces | Verdict | Source |
|---|---|---|
| Components are fixed-width | **Confirmed** | §3.6: "If a time element in a defined representation has a defined length, then leading zeros shall be used as required." |
| A representation may not mix basic and extended | **Confirmed** | §4.3.3 d): "the expression shall either be completely in basic format … or completely in extended format" |
| The comma is the *preferred* decimal sign | **Confirmed** | §4.2.2.4: "the comma [,] or full stop [.]. Of these, the comma is the preferred sign." Also "A decimal fraction shall have at least one digit", which the grammar's `[0-9]+` already required. |
| A reduced-precision `±hh` offset is valid | **Confirmed, and stronger than assumed** | §4.3.2 lists `YYYY-MM-DDThh:mm:ss±hh` (`1985-04-12T10:15:30+04`) among the *extended* complete representations. §4.2.5.1 permits omitting minutes "only if the difference … is exactly an integral number of hours" — which `±hh` satisfies by construction. |
| Seconds may be omitted | **Confirmed** | §4.2.2.3 with §4.3.3, which allows a complete date with a reduced-accuracy time. |
| A negative zero offset is forbidden | **Confirmed** | §4.2.5.1: the difference "shall be expressed as positive (i.e. with the leading plus sign [+]) if the local time is ahead of **or equal to** UTC of day". Zero takes a plus. |
| The UTC designator is uppercase `Z` | **Partly** | §3.5: "[Z] is used as UTC designator", and no lowercase variant is defined anywhere. But no sentence forbids lowercase in so many words, so flagging `…00z` rests on *not defined* rather than *prohibited*. The weakest of the seven. |

Two things the source **corrected**, neither of which recall had right:

1. **`24:00` was wrongly listed as a valid spelling we fail to read** (§6). §4.2.2.2
   admits `[24]` "only to indicate the end of a calendar day within a time
   interval", and §4.2.3 NOTE 3 says it "shall not be used for a single time
   point". `stale_after` is an absolute instant, so `24:00` is simply invalid
   for it. The out-of-scope entry was overstating a gap that does not exist.
2. **A leap second `…T23:59:60Z` is genuinely valid** — §4.2.2.2: "second is
   represented by two digits from [00] to [60]. The representation of the second
   by [60] is allowed only to indicate a positive leap second". It stays in the
   out-of-scope list, correctly.
