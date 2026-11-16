# Model-facing output boundary (#122) — design

Status: decided (2026-11-16). Approach chosen by the maintainer: **both** a
type that folds by construction and an enumerating test. The §4 questions
were settled the same day, following the proposals:

- **Q1:** a stray control becomes U+FFFD.
- **Q2:** a CHANGELOG entry under Changed, with no breaking note.
- **Q3:** tool blocks use fences longer than any backtick run in the content;
  injected context uses the `<okf-context>` element with `<` escaped inside.

## 1. Problem

Every value interpolated into text a model reads — an agent tool's result
(`OkfBundleTools`) or a block the context provider injects
(`OkfContextProvider`) — must not be able to forge a line, a heading or a
bullet. Today that is a convention applied sink by sink. Six review rounds
of PR #108 each found one more missed sink; the external audit named the
gap: nothing forces the next interpolation site to discover the rule.

An inventory of `dev` (2026-11-16, read from the source, each finding
re-checked where it matters) counts **121 sinks** in the tool results and
injected context: 61 folded, 7 gated by a grammar or a refusal, 36 safe
(constants, enums, the ASCII `ConceptId` grammar), 6 raw by design
(multi-line bodies), and **11 unfolded**, which are 6 distinct defects:

| # | Where | Value | Why it is open |
|---|---|---|---|
| 1 | `okf_search` excerpt line | bundle body | `ConceptSearch.Excerpt` splits on LF only, then `Trim()`: an interior CR, NEL, U+2028/U+2029 survives (verified) |
| 2 | context provider, scoped path | same excerpt | same root cause as 1 |
| 3 | context provider, scoped path | frontmatter `title` | prepended raw, `DisplayTitle` not used (verified) |
| 4 | `okf_write_concept`, `okf_verify` | `Error: {ex.Message}` from the writer | exception text, may carry a host path (#211) |
| 5 | `okf_verify` target problems | `DetailAsSentence()` | parser / I/O exception text, raw |
| 6 | `okf_run_computation` receipt | a host `IFormattable` value | low: only a custom executor type |

The fold helpers disagree with each other. `OneLine` folds line endings only.
`OneLineLogText` and `GuardLogField` also handle bidi controls. `DebugQuote`
escapes controls and the two separators. None of them treats ESC, VT, FF and
TAB the same way as the others. `okf_audit` and the context provider have no
forged-line test.

## 2. Design

### 2.1 One fold rule, in the core

`Internal/ModelText.Fold(string)` is the single definition of "safe inside one
line of model-facing text":

- every line terminator becomes one space: CR, LF, CRLF, NEL (U+0085),
  VT, FF, U+2028, U+2029;
- every other C0/C1 control (ESC, NUL, …) becomes U+FFFD, except TAB, which
  is left as is. It cannot forge a line, and an ordinary log entry pins it
  verbatim. `LineSafeText` still refuses it, so the predicate refuses
  everything the fold changes (decided during implementation, 2026-11-16);
- bidi controls (U+061C, U+200E/U+200F, U+202A–U+202E, U+2066–U+2069) are
  removed;
- everything else is left as is, so an ordinary value prints unchanged.

`OneLine`, `OneLineLogText` and the tools' ad-hoc folds are replaced by it.
`LineSafeText.ContainsControlCharacter` (a refusal predicate, not a fold) and
`DebugQuote` (a debug literal for messages shared with the CLI) stay, and
`LineSafeText`'s character set is aligned with `Fold`'s, so the predicates and
the fold can no longer disagree.

### 2.2 A type that folds by construction

`ModelTextBuilder` (internal to `OKF4net.Agents`) is the only way a tool or
the context provider builds its result. Its line method takes a C# 14
**interpolated string handler**. `AppendLiteral`, the compile-time parts the
developer wrote, is appended as written. `AppendFormatted`, every hole, goes
through `ModelText.Fold`. So:

```csharp
text.Line($"- {title} ({id})");          // title and id folded, always
text.Line("## Results");                  // a literal, as written
text.Block(BlockKind.ConceptBody, body);  // multi-line by design: explicit and delimited
```

- Folding a hole needs no decision at the call site. Writing a raw value
  takes `Block`, which is visible in review and limited to a closed set of
  multi-line kinds: a concept body, a computation source, a log day. Each kind
  has its own delimiting rule.
- A `Block` is delimited so that its content cannot close it. This covers the
  concept body in `okf_read_concept`, which has no delimiter today. It also
  covers the context provider's `<okf-context>` element, where a literal
  `</okf-context>` in bundle content closes the block early (found by the
  inventory). Inside a block the text is escaped, so the closing marker
  cannot appear unescaped.
- Lines already rendered by `AuditText` are golden-locked for the CLI, so
  their bytes do not move. They enter through `text.Rendered(AuditLine)`: a
  type only `AuditText` can construct, which has already made every value
  line-safe (#123).
- Values that are safe by grammar (`ConceptId`, an ISO date, an enum name) are
  folded anyway. Fold is the identity on them, so nothing needs a per-site
  argument.

### 2.3 The enumerating test

`ModelFacingBoundaryTests` covers each function `GetTools()` returns, found by
reflection over every mode, `okf_run_computation` included through an
orchestrator double:

- it runs the function over a **hostile fixture bundle**. In that bundle every
  title, description, tag, body line, log entry, receipt key and value, and
  exception text is built to carry each terminator and a forged `## FORGED`,
  `- FORGED` and `FORGED:` line, plus bidi controls;
- every string argument receives the same forged payloads;
- it asserts that no output line outside a declared `Block` starts with a
  forged marker, and that no bidi control or stray C0/C1 control appears
  outside blocks;
- the tool list is a **closed inventory**, like `ObsoleteMembersTests`. A
  tool that `GetTools()` adds without a hostile-argument entry fails the test
  by name. The context provider has the same test over its injected blocks.

The test catches what the type cannot prevent: a raw `StringBuilder` or a
`Block` misused.

### 2.4 The six open sinks

They are closed by the migration itself. The excerpt is fixed at its source,
`ConceptSearch.Excerpt`, which folds what it returns; that also covers
`OKF4net.Catalog`'s search, its other consumer. Exception texts are folded
here. Removing host paths from them stays #211.

## 3. Migration

1. `ModelText.Fold` plus its table-driven tests (every terminator, control and
   bidi class), and `LineSafeText` aligned.
2. `ModelTextBuilder` with the handler, `Block` and `Rendered`.
3. Tools migrated one renderer at a time. Each migrated tool joins the
   enumerating test's inventory in the same commit, and the test starts with
   every tool listed and failing until migrated.
4. The context provider, with the `<okf-context>` delimiting fix.
5. Grep guard: a test fails if `OkfBundleTools.cs` or `OkfContextProvider.cs`
   use `StringBuilder.Append` outside `ModelTextBuilder`. This is a smoke check
   and says so; the enumerating test is the proof.

## 4. Questions for arbitration

- **Q1, what a stray control becomes.** U+FFFD (proposed) is visible and
  carries no meaning. A visible escape such as `\u{1b}` is more informative
  but longer. A space is invisible.
- **Q2, behaviour visible to a host.** Tool texts change only for values that
  carried a terminator, a control or a bidi character. The CLI's goldens do
  not move, since `AuditText` is untouched. Is a CHANGELOG entry under Changed
  enough, or does it need a breaking note?
  *Correction (implementation, 2026-11-16):* the last two claims did not hold.
  Aligning `LineSafeText` with the fold (§2.1) makes the shared predicate
  refuse bidi controls too, and the CLI uses it. `okf verify --by` (or `--at`)
  carrying a bidi control now exits 1 with "must not contain control or
  bidirectional characters" where it was accepted, and `AuditText` (refactored
  into `AuditLine`, same bytes for every golden) now quotes a stored `at`, id or
  actor that carries one. No golden captures either case, so none moved; the
  CHANGELOG entry states both.
- **Q3, block delimiting.** One option is a fenced block whose fence is longer
  than any backtick run in the content (CommonMark's rule). The other is the
  `<okf-context>` style for every block, with `<` escaped inside it. The
  proposal is fences for tool bodies and the element for context blocks.

## 5. Out of scope

- Host paths inside exception messages (#211).
- What the model does with text it read (prompt injection in general): this
  boundary only guarantees that the text's structure is the tool's own.
- The CLI's own output: a human reads it, and its bytes are golden-locked.
