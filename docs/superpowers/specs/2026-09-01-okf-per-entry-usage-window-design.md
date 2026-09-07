# §5.1 Per-entry `usage_window` override — Design

**Status:** implemented, merged into `dev` (PR #57). This document is maintained
to describe the code as it shipped, not the draft that preceded it: verification
came back false on two of its claims (C6 and C10 in §6) and the text was
corrected in place rather than left standing. §7's predictions held — the suite
was green at merge, and no fixture and no golden moved.
**Date:** 2026-09-01, revised 2026-09-01 (§3 malformed-vs-empty, §4 `ToYaml`)
**Branch:** `fix/usage-window-override`
**Normative source:** `docs/spec/SPEC.md` (OKF v0.2, vendored verbatim at upstream
`62432a0`, `sha256 26aa5da0…`). Every `§` and line number refers to **that file**.

---

## 1. Why this exists

§5.1 line 333-334 allows a `sources` entry to carry its own `usage_window`.
OKF4net parses only the top-level one, so an override is **invisible to the
library**: it has no typed representation, its bounds are never validated
against §5, and no consumer can obtain it.

**It is not lost, though — an earlier draft of this document claimed it was.**
`okf fmt` round-trips it today, verified by running it on a probe document
before any change: `Frontmatter` wraps an order-preserving `YamlMapping` and
re-serializes the raw tree, so a nested key it has no typed accessor for
survives untouched. `Provenance.ToYaml` has exactly one caller
(`OkfDocumentBuilder.cs:176`), so the serializer reaches only the
producer/builder path, never `fmt`. The gap is therefore **blindness, not data
loss**: the value sits in the file, correctly preserved, and the library cannot
see it, cannot check it, and cannot hand it to anyone.

This is a pre-existing gap, recorded before this work as **S5.1-3** in
`docs/spec-conformance/2026-07-31-okf-spec-gap-report.md:204` ("**Missing**
(Minor) … the `Source` record has no `UsageWindow` field"). It was declared out
of scope by the §5 timestamp-spelling work (see
`2026-08-31-okf-timestamp-spelling-design.md` §6) because it is a **parsing**
gap, not a spelling one — but it bounded that work's "all six §5 keys" claim,
since an override's `from`/`to` are a seventh and eighth timestamp position the
library never sees.

## 2. Normative basis, verbatim

| Line | Text |
|---|---|
| 332-334 | "`usage_window`: Written once as a sibling of `sources`, it frames every `usage_count` with a `{ from, to }` datetime range. **A single entry MAY carry its own `usage_window` to override the shared one.**" |
| 284-285 | "Every timestamp-valued key in OKF is an ISO 8601 datetime with an explicit UTC offset, for example `2026-06-30T14:00:00Z`." |
| 300 | The §5.1 example: `usage_window: { from: 2026-06-01T00:00:00Z, to: 2026-06-30T00:00:00Z }` |
| 738-755 | §11: three conformance conditions, none of which is this; producers SHOULD follow §5; consumers "SHOULD treat all other constraints as soft guidance". |
| 819-821 | §13: the `usage_window` sibling is **additive** in v0.2 — its absence yields a plain v0.1 concept. |

## 3. What the spec settles, and what it does not

**Settled.**

- An entry MAY carry `usage_window`. It is optional, like every §5 family.
- Its bounds are a `{ from, to }` **datetime** range, so §5's timestamp rule
  (line 284) reaches them exactly as it reaches the shared window's. They join
  the set of §5 timestamp-valued keys already routed through `CheckTemporal`.
- Severity: §11 does not make any of this a conformance condition, so a bad
  bound is a `Warning`, never an `Error`, and a readable value is never dropped.

**Decided here, because the spec says "override" and stops.**

- **The override is whole-object, not per-field.** An entry writing
  `usage_window: { from: X }` overrides the shared window entirely; its `to` is
  *absent*, not inherited from the sibling. §5.1 says an entry may carry its own
  window "to override the shared one" — the object is what is overridden. A
  per-field merge would be inventing a rule the spec does not state, and would
  make a half-written entry silently borrow half a window from elsewhere. This
  is the one semantic judgement in the change, and it is pinned by a test.
- **A malformed override falls back; a present-but-empty one does not.**
  `usage_window: hello` is *not a mapping*, so `ParseUsageWindow`'s existing
  leniency ("a non-mapping value yields null") lets the entry inherit the shared
  window, exactly as an absent key does. `usage_window: {}` **is** a mapping — it
  parses to `UsageWindow(null, null)`, a present override with two absent bounds
  — so by the whole-object rule above it wins rather than falling back; reading
  `{}` as "nothing to override with" would be the per-field merge this design
  rejects. Both halves are pinned:
  `FrontmatterTests.EffectiveUsageWindow_falls_back_to_shared_when_the_entrys_override_is_not_a_mapping`
  and `…_present_and_empty_entry_window_is_not_absent_and_does_not_fall_back`.

## 4. Model

```csharp
// Source gains a seventh positional member, with a default so every existing
// construction site still compiles.
public readonly record struct Source(
    string? Id, string Resource, string? Title, Actor? Author,
    long? UsageCount, string? LastModified, UsageWindow? UsageWindow = null);

// The §5.1 override rule, spelled once.
public UsageWindow? Frontmatter.EffectiveUsageWindow(Source source);
```

- `Provenance.ParseSources` reads `usage_window` per entry through the existing
  `ParseUsageWindow`, so the two positions cannot drift in how they parse.
- `Provenance.ToYaml` writes it back, keeping the canonical key order it
  documents (`id, resource, title, author, usage_count, last_modified`, then
  `usage_window`) so a round-trip is lossless. **This is the part most likely to
  be forgotten** and, per §1, the only path that ever risked the field: `ToYaml`
  serves `OkfDocumentBuilder` alone, so a field read but not written back is lost
  for builder callers — never for `okf fmt`, which does not call it.
- `Frontmatter.EffectiveUsageWindow` applies the override rule. Without it every
  consumer re-derives §5.1 for itself — the forking `CLAUDE.md` forbids for
  `ConceptSearch`, `LfLines` and `ConceptAudit`, and there is no reason this
  rule should be the exception.

**Diagnostics reuse the existing codes**, with `Field` distinguishing the
position — the pattern already used by `MissingRecommendedField` and by
`LegacyDateOnlyTimestamp` across six keys:

| Position | Code | `Field` |
|---|---|---|
| shared (unchanged) | `UsageWindowInvalidFrom` / `…To` | `usage_window.from` / `.to` |
| per-entry (new) | same two codes | `sources.usage_window.from` / `.to` |

No new enum member, so no ordinal shifts.

## 5. Blast radius

- `src/OKF4net/Provenance.cs` — the record, `ParseSources`, `ToYaml`.
- `src/OKF4net/Frontmatter.cs` — the resolution accessor.
- `src/OKF4net/Validate.cs` — the per-entry bounds through `CheckTemporal`.
- Public API: `Source` gains an optional positional member. Source-compatible for
  construction; **not** binary-compatible, and its `Deconstruct` arity changes,
  which breaks an external positional deconstruction at *source* level too — a
  `with` expression names the members it sets, so it has no arity and is
  unaffected. Acceptable at 0.x and consistent with this release's existing
  break.
- Fixtures and goldens: **none expected to move**. No fixture carries a
  per-entry `usage_window` today. To be verified, not assumed.
- `bundles/`: verbatim upstream copies, untouched whatever the counts.

## 6. Claims verified against the code

Checked one at a time on `d6b778d`, **before** writing any implementation.

| | Claim | Result |
|---|---|---|
| **C1** | No fixture carries a per-entry `usage_window`, so no golden should move | ✅ every `usage_window` in `tests/fixtures/` and `bundles/` is the top-level sibling |
| **C2** | Every `Source` construction site takes six arguments, positional or named | ✅ seven sites (`git grep -n "new Source(" d6b778d -- src tests`): three in `src/` (`OkfDocument.cs:212`, `OkfDocumentBuilder.cs:94`, `Provenance.cs:32`) and four in `ProvenanceTests.cs` (`:81`, `:95`, `:106`, `:115`) — an optional seventh member keeps all seven compiling |
| **C3** | `usage_window` is consumed nowhere outside `Frontmatter`/`Validate` | ✅ no resolver, agent, viewer or CLI path reads it |
| **C4** | The known-key list governs top-level keys only | ✅ `Frontmatter.cs:27-35` is a top-level list; a nested key inside a `sources` entry is not matched against it |
| **C5** | `ToYaml`'s round-trip is already exercised | ✅ `ProvenanceTests.ToYaml_round_trips_through_ParseSources_in_order` and `…_uses_canonical_per_entry_key_order` |
| **C6** | *(added by verification)* the producer-side write path covers the new field | ❌ **false** — see below |
| **C7** | *(added by verification)* `Source(…, UsageWindow? UsageWindow = null)` compiles despite the member sharing its type's name, and every existing call site still builds | ✅ probed by actually adding the member and building: `OKF4net.sln` at 0 errors, 0 warnings, then reverted |
| **C8** | *(added by verification)* nothing deconstructs `Source` positionally, so the arity change breaks no in-repo caller | ✅ no `is Source(…)`, no `Deconstruct` call anywhere in `src/`, `tests/`, `producers/`, `samples/` |
| **C9** | *(added by verification)* `samples/` and `producers/` carry no `usage_window` at all | ✅ zero occurrences |
| **C10** | *(the draft's §1 and §8)* an unparsed override is **lost** | ❌ **false** — `okf fmt` preserves it today, proven by running it before any change. §1 and §8 corrected. |

**C6, the one the design missed.** `OkfDocumentBuilder.AddSource`
(`OkfDocumentBuilder.cs:85-96` at `d6b778d` — signature line through closing
brace; its doc comment runs 81-84) is the supported producer API for writing a
source, and it has parameters for `id`, `title`, `author`, `usageCount` and
`lastModified` — but adding a seventh member to `Source` does not reach it. A
producer using the builder could not emit a per-entry window at all: the field
would be readable, round-trippable, and *unwritable* through the one path built
for writing it. `AddSource` therefore gains an optional `usageWindow` parameter,
in the same position the record uses, and that is part of this work rather than
a follow-up.

## 7. Verification plan

1. `dotnet build OKF4net.sln` — 0 warnings (warnings are errors).
2. `dotnet test OKF4net.sln` — full suite, **in Debug** (CI's configuration).
3. `dotnet format OKF4net.sln --verify-no-changes`.
4. A round-trip test: parse → `ToYaml` → parse, per-entry window preserved.
5. `okf validate` on a probe bundle: a conformant, a legacy, a non-ISO and an
   unreadable bound in the per-entry position, each yielding the same
   classification as the shared position with the distinguishing `Field`.
6. `git diff --stat -- tests/fixtures/` empty.

## 8. Risk

Low. The parsing is delegated to the existing `ParseUsageWindow`, the validation
to the existing `CheckTemporal`, and the timestamp grammar is untouched.

The place a mistake would hide is the **override semantics**: a per-field merge
would look reasonable, is unspecified, and would pass a careless review. It gets
its own test.

The **serializer** is the second place, but narrower than an earlier draft of
this document claimed. `Provenance.ToYaml` is the producer/builder path only, so
a field read but not written back would lose data for `OkfDocumentBuilder`
callers — not for `okf fmt`, which never touches it. Still worth its own
round-trip test; not worth the alarm the first draft gave it.
