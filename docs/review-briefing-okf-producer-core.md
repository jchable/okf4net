# External Review Briefing — OKF Producer Core Walking Skeleton (branch `okf-producer-core`)

You are an external reviewer with no prior context. This document is self-contained: it tells you what to review, how to verify claims independently, which gaps are intentional (do not re-flag), and which issues are already known (do not re-report). Everything else is fair game — the goal of this review is **fresh eyes**.

## 1. What this project is

**OKF4net** is a zero-third-party-dependency .NET 10 implementation of the [Open Knowledge Format (OKF) v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md) — knowledge bundles as directories of markdown files with YAML frontmatter, cross-linked into a concept graph.

This branch adds `producers/OkfProducer` — a **brand-new, separate solution** (`producers/OkfProducer.sln`, deliberately outside `OKF4net.sln`/CI) implementing the first slice of a tool that scans a git repository and generates an OKF v0.2 bundle describing it: package manifests, README, a repository overview. This is explicitly a "walking skeleton" — narrower than the full design spec on purpose (see §5) — meant to prove the architecture end to end before a follow-up plan adds scanner breadth and LLM enrichment.

It depends on four API members added to `src/OKF4net` by a prerequisite, already-merged branch (`OkfDocumentBuilder`, `ConceptId.Slugify`, `Provenance.ToYaml`, a `Frontmatter`-typed `BundleConceptWriter.WriteConcept` overload) — those are already in `dev` and already independently reviewed; you do not need to re-review them, but you will see this branch call them.

## 2. Repository state and review scope

- **Branch under review:** `okf-producer-core` (HEAD `4adbedf` at time of writing), 10 commits.
- **Base:** forked from `dev` at `e2c8fcd` (note: `dev` has since moved further ahead with unrelated work from other sessions — for diffing purposes, compare against `e2c8fcd`, not the current tip of `dev`).
- **Review scope:** everything under `producers/` — it is all new code on this branch:
  - `producers/src/OkfProducer.Core/` — scanning, concept generation, bundle writing, validation. Referenced by `producers/src/OkfProducer.Cli/` and `producers/tests/OkfProducer.Tests/`.
  - `producers/src/OkfProducer.Cli/` — the `generate`/`validate` CLI, `System.CommandLine` 2.0.0 + `Microsoft.Extensions.Hosting`.
  - `producers/tests/OkfProducer.Tests/` — xUnit, 32 tests, no golden fixtures (this isn't `OKF4net.Cli`, no byte-exact contract applies).
- **Out of scope:** `src/`, `tests/OKF4net.Tests/` — untouched by this branch (verify: `git diff e2c8fcd..HEAD -- src tests` is empty). `docs/superpowers/` (working design/plan docs, background only — see §8).
- `producers/` is a separate solution, intentionally outside `OKF4net.sln` and CI — see §5 for why that's not an oversight.

## 3. How to build and verify

```bash
cd producers
dotnet build OkfProducer.sln                          # must be 0 warnings, 0 errors
dotnet test OkfProducer.sln                            # must be 32/32
dotnet format OkfProducer.sln --verify-no-changes       # must be clean

# From the repo root, confirm the core library/CLI/tests are genuinely untouched:
cd .. && dotnet test OKF4net.sln                        # must be 903/903
```

Manual smoke test (there is no automated test for the CLI layer itself — see the plan's rationale in Task 6 if you want it, briefly: every piece of logic the CLI calls is already unit-tested, and wrapping `System.CommandLine`'s console I/O for in-process assertion was judged unnecessary complexity for a CLI this thin):

```bash
mkdir /tmp/okfgen-review-repo && cd /tmp/okfgen-review-repo
echo '{ "name": "review-lib", "description": "A review smoke package." }' > package.json
printf '# Review Repo\n\nHello.\n' > README.md
cd -
dotnet run --project producers/src/OkfProducer.Cli -- generate --repo /tmp/okfgen-review-repo --out /tmp/okfgen-review-out
cat /tmp/okfgen-review-out/overview.md
dotnet run --project producers/src/OkfProducer.Cli -- validate --okf /tmp/okfgen-review-out
```

## 4. Core review criteria

This is not a port of any reference implementation — it's original code built against the OKF v0.2 spec and the existing `OKF4net` library's public API. Judge it against:

1. **The design spec** — `docs/superpowers/specs/2026-07-31-okf-producer-design.md` (§ references below point here).
2. **The implementation plan's own stated scope boundary** — `docs/superpowers/plans/2026-07-31-okf-producer-core.md`'s "Scope of this plan" section explicitly narrows what this branch claims to deliver. Read it before flagging something as "missing."
3. **Ordinary C# correctness/robustness** — not fidelity to any external reference, just: does the code do what it claims, does it handle realistic bad input without crashing, are the tests real.

## 5. Deliberately deferred or intentional — do NOT flag these as missing/wrong

| # | Item | Why it's not a gap |
|---|---|---|
| 1 | Only npm (`package.json`) + root NuGet (`.csproj`) + `README.md` are scanned. No Cargo/go.mod/pyproject. No `--package-scope` flag at all (not even a no-op). | Explicit v1a scope narrowing, stated in the plan's header. A follow-up plan adds ecosystem breadth. |
| 2 | Concepts generated: repository overview, one per package, one per doc (README only). No architecture-overview, CLI-interface, or CI/test/config concepts. | Same — explicit v1a scope. |
| 3 | No LLM enrichment, no `--mode` flag, no response cache. | Same — a separate follow-up plan, per the design spec's own §4. |
| 4 | No `generated` provenance stamp on written concepts. | `BundleConceptWriter.AutoStampGenerated` and `OKF4net.Internal.OkfTimestamp` are both `internal` to the `OKF4net` assembly; `producers/` is not in its `InternalsVisibleTo` list and cannot use them. Documented in the plan as an accepted gap, not an oversight. |
| 5 | `producers/` has no CI job and isn't mentioned in `README.md`/`CLAUDE.md`/`CONTRIBUTING.md`. | Decided explicitly at merge time (see `ROADMAP.md`, "Later" section) — premature to wire up CI/docs for a first walking-skeleton slice with no users yet. Tracked, not silently missing. |
| 6 | Every generated package/doc concept produces a `BundleValidator` warning: `sources[0].resource` (e.g. `package.json`) doesn't resolve. | Intentional: `sources[].resource` records the path *relative to the scanned repository* (the correct provenance reference — "this claim came from this file in the original repo"), while `BundleValidator` resolves `sources[].resource` *relative to the bundle root*, where that file was never copied. Decided explicitly (see `ROADMAP.md`) to accept the warning rather than embed copies of referenced files into the bundle. Not a bug. |
| 7 | The already-merged `BundleConceptWriter.WriteConcept(string, Frontmatter, string)` overload throws `ArgumentNullException` for a `null` `Frontmatter` argument, instead of returning an `"Error: ..."` string like every other precondition on that class. | Human-adjudicated during the prerequisite branch's review: a `null` `Frontmatter` is judged a caller contract violation on a strongly-typed parameter (fail-fast), distinct from the sibling string overload's validation of *content* that could come from untrusted external data. Not part of this branch, but this branch calls that overload, so you'll encounter it. |

## 6. Architecture map and hot spots for fresh eyes

Layering: `Scanning/` (`RepositoryScanner` → `RepositorySnapshot`) → `Generation/` (`ConceptGenerator` → `GeneratedConcept`, `BundleWriter`) → `Validation/` (`BundleValidationRunner`) → `Cli/Program.cs` wires all four behind DI.

Where independent scrutiny has the most expected value:

1. **`Generation/ConceptGenerator.cs`'s `UniqueConceptId`:** derives a concept id from a free-form package/doc name via `ConceptId.Slugify`, with a hand-rolled numeric-suffix collision scheme (`-2`, `-3`, ...) scoped per id-prefix (`packages/`, `docs/`), plus a fallback (`package`/`doc`) for names that slugify to nothing (empty or entirely non-ASCII input) — added late, in the final-review fix wave, specifically because the first version of this method crashed the entire `generate` run on such input. Worth tracing by hand against a few adversarial names.
2. **`Generation/BundleWriter.cs`'s `Write`:** the ordering of the `Reset`/`RequireEmpty`/`Update` directory-policy checks relative to `Directory.CreateDirectory` and the actual per-concept write loop — get this wrong and either a rejected write isn't actually a no-op, or `Reset` doesn't leave a writable directory behind. Also recently added: a same-or-ancestor guard preventing `--reset`/`--force` from deleting the very repository being scanned (`--out` resolving to `--repo` or an ancestor of it) — also added late, worth a skeptical look at exactly what it does and doesn't catch.
3. **`Scanning/RepositoryScanner.cs`'s `ScanNuGetManifest`:** reads `PackageId`/`Description` across *all* `<PropertyGroup>` elements in a `.csproj` (a real bug — reading only the first group — was caught late using this very repo's own `src/OKF4net/OKF4net.csproj` as the counterexample, which splits properties across two groups). Worth checking whether other realistic `.csproj` shapes still slip through (see §7.8).
4. **`Cli/Program.cs`:** uses `System.CommandLine` 2.0.0 — the **current stable** release (object-initializer style: `Options = { ... }`, `command.SetAction(parseResult => ...)`, `Option<T>.Required`), not the old, long-lived beta API (`AddOption`, `Handler.SetHandler`) that most existing online examples/training data still show. If anything here looks like it's using the old pattern, or if you're unsure which API generation is in play, check the installed package version and Microsoft's current docs rather than assuming. The `generate` handler's exception handling (which exception types it catches vs. lets escape) is worth a skeptical pass.
5. **Four sealed classes, four interfaces** (`IRepositoryScanner`/`IConceptGenerator`/`IBundleWriter`/`IBundleValidationRunner`), each with exactly one implementation, wired via `Microsoft.Extensions.DependencyInjection`. Judge whether this DI-friendly shape earns its keep at this size (it exists to make the CLI layer thin and the core logic independently unit-testable without mocks — every test in `OkfProducer.Tests` uses real temp directories, not fakes) or is premature ceremony.

## 7. Known open issues — do NOT re-report (already found, deliberately left open)

1. No test exercises a genuine per-concept write **failure** (`WriteResult.Failures` actually populated) — only the whole-directory `RequireEmpty` precondition exception is tested. `BundleWriter`'s per-concept-failure branch is real code, currently untested.
2. No test positively demonstrates that a package and a doc slugifying to the same bare name do **not** collide with each other (cross-prefix non-collision) — verified correct by direct code reading during review, not by a test.
3. `ConceptGeneratorTests.cs`'s "slugify integration" test is self-referential: it computes its own expected value by calling `ConceptId.Slugify` — the same function the implementation calls — so it can't distinguish "calls `Slugify`" from "calls something coincidentally identical for this one input."
4. `IBundleWriter.cs` has a redundant `Generation.` namespace qualifier on `GeneratedConcept` (harmless — resolves via C#'s outward nested-namespace lookup — but confusing to a reader expecting a different type).
5. A doc titled exactly `Index` or `Log` (any casing) produces a reserved concept id (`docs/index`/`docs/log`) that `BundleConceptWriter` rejects — this degrades correctly (a `WriteResult.Failures` entry + non-zero exit), not a crash, but the generator doesn't try to rename around the collision.
6. A doc titled literally `README.md` (as opposed to the file's actual name) would produce the on-disk file `docs/readme.md.md` — untested edge case.
7. `ExtractTitle`'s `# `-heading detection is not fenced-code-block-aware — a `# ` line inside a README's own fenced code sample would be mistaken for the real heading.
8. Old-style (non-SDK, `xmlns`-declaring) `.csproj` files are invisible to the current `Elements("PropertyGroup")` XML query, which is namespace-unaware — the same silent-fallback failure mode as the now-fixed first-`PropertyGroup`-only bug (§6.3), just for a different, older `.csproj` dialect.
9. `Program.cs` prints per-concept write failures to stderr, but the final summary line only reports `BundleValidator`'s error/warning counts (from a separate `validate` invocation) — the two counts are never shown together, which could read as inconsistent if both a write failure and a validation issue occurred in the same session (not currently reachable in any single tested scenario).
10. `RepositorySnapshot.RepoPath` is threaded through the entire pipeline but never actually read by anything downstream.
11. `IRepositoryScanner.Scan`/`IConceptGenerator.Generate` are synchronous (`Scan`/`Generate`), while the design spec's original interface names were `ScanAsync`/`GenerateAsync` — a deliberate simplification for this slice (plan-sanctioned); the future LLM-enrichment work will likely force a breaking signature change to introduce async/cancellation.
12. No test scans a repository containing **both** an npm `package.json` and a NuGet `.csproj` at once — ecosystems are only exercised one at a time (plus a hand-built two-package `RepositorySnapshot` at the generator level, which doesn't exercise the scanner).

## 8. Background documents

- Design spec: `docs/superpowers/specs/2026-07-31-okf-producer-design.md`
- Implementation plan: `docs/superpowers/plans/2026-07-31-okf-producer-core.md` — its "Scope of this plan" and "Self-Review Notes" sections are directly relevant, not just background.
- Prerequisite (already merged to `dev`, not in this branch's diff): `docs/superpowers/specs/2026-07-31-okf4net-producer-ergonomics-api-design.md`.
- `ROADMAP.md`'s "Later" section records the two decisions made at merge time (§5 rows 5 and 6).

## 9. Requested output format

Report findings ranked by severity (Critical / Important / Minor), each with: file:line, the concrete failure scenario (inputs/state → wrong output or crash), and — where relevant — which design-spec/plan section you checked it against. Explicitly separate: (a) defects against the plan/spec, (b) defects independent of any document (crashes, resource leaks, API misuse), (c) non-blocking suggestions. Do not re-report anything already listed in §5 or §7 unless you've found a genuinely *new* consequence not already described there. If you verify a claim empirically (actually running the code against a constructed input), say so — empirical evidence outranks reasoning about what the code "should" do.
