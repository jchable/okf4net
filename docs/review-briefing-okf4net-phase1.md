# External Review Briefing — OKF4net Phase 1 (branch `okf4net-migration`)

You are an external reviewer with no prior context. This document is self-contained: it tells you what to review, how to verify claims independently, which divergences are intentional (do not re-flag), and which issues are already known (do not re-report). Everything else is fair game — the goal of this review is **fresh eyes**.

## 1. What this project is

**OKF4net** is a .NET 10 implementation of the [Open Knowledge Format (OKF) v0.1](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/main/okf) — Google's format for representing knowledge as a directory ("bundle") of markdown files with YAML frontmatter, cross-linked into a concept graph. It is a **line-faithful port** of a Rust crate (`okf`, by Walter van der Giessen, Apache-2.0) that previously lived in this repository and was deleted after byte-exact parity was proven. OKF4net is licensed LGPL-3.0-or-later with the Apache-2.0 attribution chain preserved (see `NOTICE`, `LICENSE.Apache-2.0`).

Roadmap context (affects API judgment): Phase 2 will build `OKF4net.Agents` on top of this library — nine `AIFunction` tools for Microsoft Agent Framework (`Microsoft.Agents.AI`), including programmatic document *production* by agents. Phase 3 adds an `AIContextProvider`. The library surface you review is about to become the foundation other code builds on, and the project is heading toward NuGet publication.

## 2. Repository state and review scope

- **Branch under review:** `okf4net-migration` (HEAD `98a92ff` at time of writing).
- **Base:** `main` (the branch is ~10 commits ahead; the full Phase-1 history is ~40 commits from the initial commit `4abb98a`).
- **Review scope:** the entire .NET solution — it is all new code from this branch:
  - `src/OKF4net/` — core library, **zero NuGet dependencies** (hard constraint, verify it holds).
  - `src/OKF4net.Cli/` — `okf` CLI, 6 commands, Native AOT, zero dependencies.
  - `tests/OKF4net.Tests/` — xUnit, 209 tests including 5 byte-exact golden parity tests.
- Do not spend effort on `docs/superpowers/` (internal working docs: design spec and implementation plan; useful as background — see §8) or on `graphify-out/` (generated analysis artifacts).
- The git remote `origin` points to the upstream Rust author's repo; ignore it.
- You may encounter unrelated in-progress work: a git stash made from `main`, and untracked files (`.github/`, `CONTRIBUTING.md`, `SECURITY.md`). **Do not touch, review, or clean any of these.** Your review is read-only: never mutate the working tree, index, HEAD, branches, or stash.

## 3. How to build and verify

```powershell
dotnet build OKF4net.sln -warnaserror   # must be 0 warnings (TreatWarningsAsErrors solution-wide)
dotnet test OKF4net.sln                  # must be 209/209
dotnet test OKF4net.sln --filter "FullyQualifiedName~GoldenParityTests"   # must be 5/5
dotnet publish src/OKF4net.Cli -c Release   # AOT publish must succeed (needs MSVC link.exe on PATH on Windows)
```

## 4. The parity doctrine (core review criterion)

The project's #1 correctness standard is **observable-behavior fidelity to the Rust reference**, not idiomatic preference. When judging any behavior:

1. **The Rust source is the specification.** It was deleted from HEAD but is fully accessible at the parent of the deletion commit: `git show 4cfc480~1:src/yaml/parser.rs` (likewise `mod.rs`, `emitter.rs`, `bundle.rs`, `concept_id.rs`, `document.rs`, `error.rs`, `frontmatter.rs`, `index.rs`, `links.rs`, `log.rs`, `validate.rs`, `bin/okf.rs`, and `tests/*.rs`).
2. **Golden fixtures** in `tests/fixtures/golden/` are byte-exact captures of the real Rust binary's output (generated in Docker before deletion; provenance in `tests/fixtures/README.md`). Rules: goldens are never edited, never regenerated from C#, compared byte-strict — with exactly one sanctioned normalization (`validate.out`: `\`→`/` on the C# *output* only, because the goldens were captured on Linux and Rust's `PathBuf::display` is platform-native; the C# port mirrors that same per-platform behavior).
3. A C#-vs-Rust behavioral divergence on *any* input is a bug **unless** it is on the intentional-divergences list below.

## 5. Intentional divergences — do NOT re-flag these

| # | Divergence | Justification |
|---|---|---|
| 1 | Recursion depth limit 1000 in YAML parser and emitter (`YamlParseException` "nesting depth limit exceeded") | Deliberate safety improvement: .NET's ~1 MB stack made deeply-nested input an uncatchable `StackOverflowException`; Rust merely crashes deeper. Documented in code comments. |
| 2 | Windows path separators (`\`) in CLI diagnostic output where Linux-generated goldens show `/` | Rust's own `PathBuf::display` is platform-native; the port is faithful *per platform*. Handled in `GoldenParityTests` by output normalization. |
| 3 | `Bundle.ParseErrors` wraps concept-id failures in the message prefix "Missing required frontmatter keys: …" | Looks mislabeled but faithfully mirrors Rust `bundle.rs` wrapping `ConceptIdError` in `DocumentError::MissingKeys`. |
| 4 | `ChangeLog.IsIsoDate("2026-02-30") == true` | Rust's `is_iso_date` checks shape and ranges (day ≤ 31) without calendar awareness. Faithful. |
| 5 | The Rust parser does **not** reject YAML anchors/tags/directives (its doc comment claims otherwise; the code parses them as plain strings) | Port follows the code, not the aspirational comment. |
| 6 | `tags:` with a non-sequence value yields an **empty** tag list | Faithful to `frontmatter.rs` (only the Sequence arm is handled). |
| 7 | `okf parse` prints a blank line after each frontmatter entry | Faithful to a Rust CLI quirk (`{v}` Display includes a trailing newline). |
| 8 | `YamlValue v = 5;` compiles (int→long widening + implicit `long→YamlValue`) though Rust has no `From<i32>` | Harmless widening; accepted. |
| 9 | `InvariantGlobalization=true` on the CLI project only, not the library | CLI is self-contained; the library relies on explicit `Ordinal` comparisons everywhere instead (verify: this IS enforced by convention, see §6.3). |

## 6. Architecture map and hot spots for fresh eyes

Layer order (each depends only on lower layers): `Yaml/` (YamlValue, YamlMapping, YamlParser, YamlEmitter, RustLines) → `Frontmatter`, `ConceptId`, `OkfDocument`, `Links` → `Bundle`, `IndexGenerator`, `ChangeLog`, `Validate` → CLI.

Where independent scrutiny has the most expected value:

1. **`YamlMapping` (recently refactored, highest churn):** now mirrors Rust's `Mapping` exactly — entries are a `List<(YamlValue Key, YamlValue Value)>`; `Get`/`ContainsKey`/`Keys` filter to string keys and return the *first* match; `Insert` replaces-in-place-or-appends; internal `PushRaw` appends unconditionally (parser path — duplicate keys are preserved and re-emitted); non-string keys are invisible to `Get`/`Keys` but present in `Entries` and re-emitted through scalar emission. Compare against `git show 4cfc480~1:src/yaml/mod.rs`. Anything the refactor missed — an accessor, an equality corner, a consumer still assuming deduped string keys — is a prime find.
2. **Parser/emitter edge cases:** float formatting (`FormatFloat` mirrors Rust `{:?}` Debug incl. `1e16`→`"1.0e16"` dot-insertion; `FormatDisplayFloat` mirrors `{}` Display — never scientific), block scalars `|`/`>` with chomping, quoting rules (`DoubleQuote`), flow parsing, tab rejection, line numbers in `YamlParseException`. Adversarial inputs welcome.
3. **Comparison hygiene:** every string comparison in the library must be `Ordinal` (or `OrdinalIgnoreCase` only where Rust does `eq_ignore_ascii_case`). One class of bug already found and fixed (culture-sensitive `StartsWith`); a systematic sweep for any residual culture-sensitive API (including `string.Compare`, `ToLower`, `char.IsLetterOrDigit` vs `IsAsciiLetterOrDigit`) is valuable.
4. **Public API fitness for NuGet + Phase 2:** the library will be consumed by agent tools that *write* documents programmatically. Judge the construction/mutation surface (`YamlValue` implicit conversions, `Frontmatter.Set`, `OkfDocument` immutability model, `Frontmatter.FromMapping` reference-aliasing hazard) as a public API, not just as a port.
5. **CLI contract:** exit codes, usage/error texts, output formatting must match `git show 4cfc480~1:src/bin/okf.rs` verbatim. `OkfCli.Run(string[], TextWriter, TextWriter)` is the testable entry point; output is LF-only by construction (`Write` with explicit `\n`).

## 7. Known open issues — do NOT re-report (already triaged to backlog)

1. `DebugQuote` does not escape Unicode grapheme-extending (combining) characters as Rust `char::escape_debug` does → `graph --dot` output divergence on such input.
2. Directory walk follows symlinks (`Directory.Exists`/`File.Exists`); Rust `DirEntry::file_type()` has lstat semantics and does not.
3. CLI: `ArgumentException`-class path errors (e.g. embedded NUL) escape as unhandled exceptions instead of the uniform `error: …` + exit 1 path.
4. Helper duplication pending consolidation: `StrictUtf8` ×4, `DebugQuote` ×3, `ComparePathsComponentWise` ×2, `RepoRoot()`/`Run()` duplicated across the two CLI-facing test classes.
5. `Bundle.OkfVersion` performs file I/O + parse on every property access (single-call-site today).
6. `okf --help` usage text hardcodes "OKF v0.1" instead of interpolating `OkfSpec.Version`.
7. `GoldenParityTests` temporarily sets `Environment.CurrentDirectory` (try/finally); safe under current xUnit parallelism but unguarded against future CWD-dependent tests.
8. `Frontmatter.FromMapping` stores the caller's mapping by reference (Rust's move semantics prevented aliasing); latent hazard, no current call site affected.
9. No NuGet packaging metadata yet (`PackageId`, license expression, `GenerateDocumentationFile`) — scheduled with the OSS publication work.

## 8. Background documents (optional reading)

- Design spec: `docs/superpowers/specs/2026-07-21-okf4net-migration-design.md`
- Implementation plan (15 tasks): `docs/superpowers/plans/2026-07-21-okf4net-phase1-core.md`
- Fixture provenance: `tests/fixtures/README.md`
- Note: an in-flight repo reorganization may move `docs/superpowers/` to `docs/design/`.

## 9. Requested output format

Report findings ranked by severity (Critical / Important / Minor), each with: file:line, the concrete failure scenario (inputs/state → wrong output), and — for any fidelity claim — the Rust reference line (`4cfc480~1:src/...`) you compared against. Explicitly separate: (a) correctness vs the Rust reference, (b) defects independent of the port (crashes, resource leaks, API traps), (c) suggestions (non-blocking). Do not report anything from §5 or §7 unless you found a *new* consequence not described there. If you verify a claim empirically (running code), say so — empirical evidence outranks reasoning.
