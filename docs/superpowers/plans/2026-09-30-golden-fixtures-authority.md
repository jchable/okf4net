# Golden Fixtures Authority — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Revision 2 (2026-10-01)** — after an external review that executed the plan's code: two blockers in the update mode fixed (xunit allows one public constructor on a fixture; the group commit was not atomic), the one pasted expectation replaced by a derived literal, the audit projection given categories whose permutation is detectable, four assertions the retired tests carried restored, the DOT test made sensitive to duplicate edges, the comparator hardened against duplicate property names and double-rounded integers, the update mode moved before the doctrine (spec §5.7 amended), and the doctrine sweep widened to every active text the review found.

**Goal:** Make `docs/spec/SPEC.md` the only conformance authority, turn `tests/fixtures/golden/` into regenerable snapshots of our own output under a scoped update mode, and add the independent semantic-fidelity tests that make regeneration defensible.

**Architecture:** Three layers with distinct authorities — conformance (tests citing a `§`), semantic fidelity of the four machine outputs (one full-projection test per surface, compared structurally against hand-derived expectations), and snapshots (`GoldenParityTests`, compared as text after the existing path normalisation). The update mode lives in a new `GoldenUpdate` helper plus an xunit collection fixture that validates the requested scope before any write, commits each test's group atomically with restore on failure, and checks at teardown that every named test actually captured. Work order is mandatory: pin dates → direct tests → deletions → update mode → doctrine → first real use.

**Tech Stack:** .NET 10 / C# 14, xunit 2.9.3 (collection fixtures, one public constructor per fixture, no dynamic skip, no assembly fixture), `System.Text.Json` (`JsonDocument`/`JsonElement`), the in-process CLI runner `TestPaths.Run`.

**Spec:** `docs/superpowers/specs/2026-09-22-golden-fixtures-authority-design.md` — read it first; every task below cites the section it implements.

## Global Constraints

- Zero third-party runtime dependencies in `src/` (CLAUDE.md); test-only packages are fine. This plan adds none.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`; file-scoped namespaces; XML doc comments on public API; `TreatWarningsAsErrors` — a warning (including a broken `<see cref>`) fails the build.
- `dotnet format OKF4net.sln --verify-no-changes` must pass; run `dotnet format OKF4net.sln --no-restore` before each commit.
- **`OKF4net.sln` carries an uncommitted edit from another session that references absent worktree projects and breaks `dotnet test OKF4net.sln`.** Every test command in this plan targets the test project directly: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj`. Never `git add` the `.sln`; `git status` is therefore never fully clean — bound every cleanliness check to the paths named.
- Never edit `docs/spec/SPEC.md`.
- Everything under `tests/fixtures/` stays LF, UTF-8 without BOM, `.gitattributes -text`, excluded from `.editorconfig` normalisation (spec §1 "Ce qui ne change pas").
- Snapshots are compared **after** the existing normalisation (`\` → `/` on text at `GoldenParityTests.cs:84,93,101,109`; escaped pair `\\` → `/` on JSON at `:156`). Capture writes exactly the compared value (spec §2 "Normalisation des chemins").
- Every expected value in a projection test is **derived** — from a fixture file, a hand-verified text golden, or the source line that produces it — and the plan cites the source next to the literal. Reading a value from the output under test and writing it back into the expectation is forbidden; it is the tautology the update mode exists to refuse.
- Commit messages: imperative subject with a `type(scope):` prefix as in `git log`, a body explaining *why*. If the session carries an attribution instruction (a `Co-Authored-By` line), end the message with it exactly as given; otherwise add none.
- Work in the order given. Tasks 1–2 precede everything; Task 9 (update mode) precedes Task 10 (doctrine); Task 11 is last (spec §5.7, amended 2026-10-01).

## Review Focus

Failure modes the spec implies that no task's happy path exercises; each is pinned by a test in the owning task:

1. **Capture writes a BOM or CRLF on Windows** — a snapshot regenerated on Windows must be byte-identical to one regenerated on Linux (Task 9: `Commit_writes_utf8_without_bom_and_lf_only`).
2. **A group is left half-replaced when the second file's move fails** — the committed state must be restored (Task 9: `Commit_restores_the_group_when_a_later_move_fails`).
3. **A test named in the scope fails a guard and the snapshot is overwritten with an error rendering** — nothing may be written (Task 9: the guard order in `AssertGolden`, and `Commit_refuses_an_artefact_set_that_does_not_match_the_group`).
4. **A typo in the scope silently regenerates nothing and the run looks green** — the whole list is rejected before any test runs (Task 9: `ParseScope_rejects_unknown_empty_and_duplicate_names`, `ParseScope_reports_every_problem_at_once`; Task 11 Step 5).
5. **A JSON projection test copies the observed output, becoming a tautology** — every literal is derived and cited (Tasks 2–4), and the comparator rejects extra, missing and duplicated properties (Task 2: `AssertEquivalent_reports_missing_and_unexpected_properties`, `AssertEquivalent_rejects_duplicate_property_names`).

Known, documented limit (spec §5.2, amended): a `--filter` that excludes the **entire** `GoldenParity` collection leaves the variable without effect and the run green, because the collection fixture is never constructed. Nothing is written and no diff appears — loud by absence. Task 11 Step 4 demonstrates it; the README names it.

---

### Task 1: Pin `--as-of` on the four `validate` snapshots

Spec §2 "Les dates ne sont pas toutes fixées", §5.7 step 1. The four `validate` golden invocations pass no date; `okf_v02/metrics/dau.md` carries `stale_after: 2099-01-01T00:00:00Z`, so on 2099-01-01 the output gains a staleness warning nobody asked for. Pinning `--as-of 2026-09-25` was verified by execution to leave all four outputs and exit codes identical.

**Files:**
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs:68-110`

**Interfaces:**
- Consumes: `TestPaths.Run(params string[])`, `WithRepoRootAsCwd`.
- Produces: `PinnedAsOf` constant; the four tests keep their names (Task 9 keys the update scope on them).

- [ ] **Step 1: Add the constant and the argument**

Next to `GoldenRoot` at the top of the class:

```csharp
    /// <summary>
    /// The date every <c>validate</c> snapshot is evaluated at. Pinned so the
    /// captured output cannot drift with the calendar: <c>okf_v02/metrics/dau.md</c>
    /// carries <c>stale_after: 2099-01-01T00:00:00Z</c>, and an unpinned run
    /// would gain a staleness warning on that day. Any date before the first
    /// <c>stale_after</c> in the fixtures gives the same output -- the validator
    /// has no "timestamp in the future" check -- but this is the date that was
    /// exercised when the pin was introduced, so keep it rather than a
    /// supposedly equivalent one.
    /// </summary>
    private const string PinnedAsOf = "2026-09-25";
```

Replace the four `Run("validate", "tests/fixtures/...")` calls so each reads, in order:

```csharp
var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/appendix_a", "--as-of", PinnedAsOf));
```

```csharp
var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02", "--as-of", PinnedAsOf));
```

```csharp
var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02_computation", "--as-of", PinnedAsOf));
```

```csharp
var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02_reserved", "--as-of", PinnedAsOf));
```

- [ ] **Step 2: Run the four tests and confirm nothing moved**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~GoldenParityTests.Validate" --no-restore`
Expected: 4 passed, 0 failed. If any fails, the pinned date is wrong for that fixture — stop and report; do not touch a snapshot.

- [ ] **Step 3: Confirm the boundary by hand, once**

Run from the repo root: `src/OKF4net.Cli/bin/Debug/net10.0/okf validate tests/fixtures/okf_v02 --as-of 2099-01-01` (build first with `dotnet build src/OKF4net.Cli --no-restore` if the binary is missing).
Expected: 4 warnings (one more than the snapshot's 3) — the drift the pin prevents. Nothing to commit from this step.

- [ ] **Step 4: Format and commit**

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/GoldenParityTests.cs
git commit -m "test(golden): pin --as-of on the four validate snapshots so they stop hanging on the machine clock"
```

---

### Task 2: JSON structural comparison + full projection of `validate --json`

Spec §3 (obligation 2), §5.3 (validate row; expected built independently; structural comparison). Adds the shared comparator and the first full-projection test, and retires the one presence-only test it makes redundant — keeping the three assertions the review found that a retired test carried and the projection did not: the **raw** `bundle` string before any normalisation, `evaluatedAt` at a **second** date, and the trailing newline.

**Files:**
- Create: `tests/OKF4net.Tests/JsonShape.cs`
- Create: `tests/OKF4net.Tests/JsonShapeTests.cs`
- Create: `tests/OKF4net.Tests/MachineOutputTests.cs`
- Modify: `tests/OKF4net.Tests/CliTests.cs` — delete `Validate_json_reports_bundle_conformance_and_diagnostics` (line 371). **Keep** `Validate_json_reports_the_instant_it_evaluated_at` (line 282: it pins `asOf`/`evaluatedAt` at 2099-06-01, a second date the projections do not use — a constant `evaluatedAt` would pass every pinned-date test), `Validate_json_records_the_date_it_was_evaluated_against` (unpinned branch) and `Validate_json_diagnostic_field_is_populated_when_applicable` (a date-only input, a different input from the projections').

**Interfaces:**
- Produces: `internal static class JsonShape { static void AssertEquivalent(string expectedJson, string actualJson); }` — recursive structural comparison, object property order ignored, duplicate property names rejected, array order significant, numbers compared as text, missing/extra properties reported with a JSON path. Tasks 3–4 use it.
- Produces: `MachineOutputTests` with helpers `Fwd(string)` and `NormalizeJsonPaths(string)`, reused by Tasks 3–5.

- [ ] **Step 1: Write the comparator's own tests**

Create `tests/OKF4net.Tests/JsonShapeTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Xunit.Sdk;

namespace OKF4net.Tests;

/// <summary>
/// The comparator behind every full-projection test. Its two jobs are to
/// ignore what the design calls presentation (object property order) and to
/// refuse what a field-by-field assertion cannot see (a property missing,
/// added or duplicated, an array truncated, an integer rounded).
/// </summary>
public class JsonShapeTests
{
    [Fact]
    public void AssertEquivalent_ignores_object_property_order()
    {
        JsonShape.AssertEquivalent("""{"a":1,"b":[true,null]}""", """{"b":[true,null],"a":1}""");
    }

    [Fact]
    public void AssertEquivalent_reports_missing_and_unexpected_properties()
    {
        var ex = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"a":1,"b":2}""", """{"a":1,"c":2}"""));
        Assert.Contains("$.b: missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("$.c: unexpected property", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertEquivalent_rejects_duplicate_property_names()
    {
        // JsonDocument accepts {"a":0,"a":1}; a name-set comparison would see
        // one "a" and TryGetProperty would return one value, hiding the
        // duplicate. The comparator counts names.
        var ex = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"a":1}""", """{"a":0,"a":1}"""));
        Assert.Contains("$.a: duplicated", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertEquivalent_treats_array_order_and_length_as_significant()
    {
        var reordered = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""[1,2]""", """[2,1]"""));
        Assert.Contains("$[0]", reordered.Message, StringComparison.Ordinal);

        var truncated = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""[1,2]""", """[1]"""));
        Assert.Contains("expected 2 elements, got 1", truncated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertEquivalent_compares_numbers_as_text_not_as_doubles()
    {
        // 2^53 and 2^53+1 are the same double; they are not the same JSON number.
        var ex = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"n":9007199254740992}""", """{"n":9007199254740993}"""));
        Assert.Contains("$.n: expected 9007199254740992, got 9007199254740993", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AssertEquivalent_compares_decoded_strings_not_escaped_text()
    {
        // System.Text.Json's default encoder escapes '`' as a backslash-u
        // sequence; the expected side is written with the plain character.
        // The escaped text is produced at run time by the serializer itself,
        // never spelled with a backslash in this source: an editor, a tool or
        // a review that materialises this file can decode a backslash-u
        // sequence on the way, and the two arguments then become identical
        // text -- which is exactly how the first version of this test was
        // inert. The guard below proves the escape is really there.
        var escaped = "{\"m\":" + System.Text.Json.JsonSerializer.Serialize("`x`") + "}";
        Assert.Contains("u0060", escaped, StringComparison.Ordinal);
        Assert.DoesNotContain("`", escaped, StringComparison.Ordinal);

        JsonShape.AssertEquivalent("""{"m":"`x`"}""", escaped);

        var different = "{\"m\":" + System.Text.Json.JsonSerializer.Serialize("axa") + "}";
        Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"m":"`x`"}""", different));
    }

    [Fact]
    public void AssertEquivalent_distinguishes_null_from_absent_and_kinds()
    {
        var nullVsAbsent = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"a":null}""", """{}"""));
        Assert.Contains("$.a: missing", nullVsAbsent.Message, StringComparison.Ordinal);

        var kind = Assert.Throws<TrueException>(() =>
            JsonShape.AssertEquivalent("""{"a":1}""", """{"a":"1"}"""));
        Assert.Contains("$.a: expected Number, got String", kind.Message, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~JsonShapeTests" --no-restore`
Expected: build error — `JsonShape` does not exist.

- [ ] **Step 3: Write the comparator**

Create `tests/OKF4net.Tests/JsonShape.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

namespace OKF4net.Tests;

/// <summary>
/// Structural comparison of two JSON documents for the full-projection tests
/// (design §5.3): the expected side is a literal derived by hand, never a
/// captured output, and the comparison must catch what a field-by-field
/// assertion cannot -- a property that disappeared, appeared or was
/// duplicated, an array that lost an element, an integer a double would
/// round. Object property order is ignored (it is presentation, design §1);
/// array order is significant. Strings compare decoded, so a <c>`</c>
/// in the actual text equals a plain backtick in the expected literal.
/// Numbers compare as text.
/// </summary>
internal static class JsonShape
{
    /// <summary>Asserts that <paramref name="actualJson"/> has exactly the shape and values of <paramref name="expectedJson"/>.</summary>
    /// <param name="expectedJson">The hand-derived expectation.</param>
    /// <param name="actualJson">The document the code under test produced.</param>
    public static void AssertEquivalent(string expectedJson, string actualJson)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        var differences = new List<string>();
        Compare(expected.RootElement, actual.RootElement, "$", differences);
        Assert.True(differences.Count == 0, "JSON differs from the expected projection:\n" + string.Join("\n", differences));
    }

    private static void Compare(JsonElement expected, JsonElement actual, string path, List<string> differences)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            differences.Add($"{path}: expected {expected.ValueKind}, got {actual.ValueKind}");
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedNames = expected.EnumerateObject().Select(p => p.Name).ToList();
                var actualNames = actual.EnumerateObject().Select(p => p.Name).ToList();
                foreach (var name in actualNames.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal))
                {
                    differences.Add($"{path}.{name}: duplicated");
                }

                foreach (var name in expectedNames.Except(actualNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    differences.Add($"{path}.{name}: missing");
                }

                foreach (var name in actualNames.Except(expectedNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    differences.Add($"{path}.{name}: unexpected property");
                }

                foreach (var property in expected.EnumerateObject())
                {
                    if (actual.TryGetProperty(property.Name, out var actualValue))
                    {
                        Compare(property.Value, actualValue, $"{path}.{property.Name}", differences);
                    }
                }

                break;

            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();
                if (expectedItems.Count != actualItems.Count)
                {
                    differences.Add($"{path}: expected {expectedItems.Count} elements, got {actualItems.Count}");
                }

                for (var i = 0; i < Math.Min(expectedItems.Count, actualItems.Count); i++)
                {
                    Compare(expectedItems[i], actualItems[i], $"{path}[{i}]", differences);
                }

                break;

            case JsonValueKind.String:
                if (!string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
                {
                    differences.Add($"{path}: expected \"{expected.GetString()}\", got \"{actual.GetString()}\"");
                }

                break;

            case JsonValueKind.Number:
                if (!string.Equals(expected.GetRawText(), actual.GetRawText(), StringComparison.Ordinal))
                {
                    differences.Add($"{path}: expected {expected.GetRawText()}, got {actual.GetRawText()}");
                }

                break;

            default:
                // True, False, Null: equal kinds are equal values.
                break;
        }
    }
}
```

- [ ] **Step 4: Run the comparator tests**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~JsonShapeTests" --no-restore`
Expected: 7 passed.

- [ ] **Step 5: Write the `validate --json` projection tests**

Create `tests/OKF4net.Tests/MachineOutputTests.cs`. Derivation of every value, so the agent can check each one without running anything:

- `bundle`: the argument, projected verbatim (`JsonOutput.cs:139`).
- `asOf` / `evaluatedAt`: the `--as-of` date and that date at midnight UTC (`JsonOutput.cs:140-141`).
- `conformant`: `report.IsConformant` (`Validate.cs:302`) — true when no error; the verdict line of `tests/fixtures/golden/validate.out` (`✓ conformant`) and `validate-reserved.out` (`✗ not conformant`).
- `conceptCount`, `errorCount`, `warningCount`, `infoCount`: the summary line of the same two goldens (`4 concept(s); 0 error(s), 8 warning(s), 0 info.` / `1 concept(s); 4 error(s), 0 warning(s), 0 info.`; projected from `Validate.cs:308-311` and `JsonOutput.cs:143-146`).
- `diagnostics[]`: the golden's lines in order (`JsonOutput.cs:128` preserves the report's order); per diagnostic, `severity` is the bracketed word, `path` the path before the colon, `message` the text after it; `code`, `conceptId` and `field` come from the validator site that emits that message — `LegacyTimestamp` with `Field = "timestamp"` and `MissingRecommendedField` with the field named in the message (`Validate.cs`, the `MissingRecommendedField`/`LegacyTimestamp` emitters; `conceptId` is the concept's id for concept diagnostics); for the four reserved-file errors, `Validate.cs:856` (`UnparseableIndex`), `:890` (`RootIndexExtraFrontmatter`, `Field = "okf_version"`), `:878` (`IndexHasFrontmatter`), `:929` (`LogDateInvalid`) — all with `conceptId = null`, and `field = null` except the second. The inputs: `tests/fixtures/okf_v02_reserved/broken/index.md:2` (unterminated flow sequence), `index.md:3` (`title` beside `okf_version`), `sub/index.md:1` (frontmatter in a non-root index), `log.md:3` (`not-a-date`); the one concept is `concepts/note.md`.

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OKF4net.Tests;

/// <summary>
/// Full-projection tests for the four machine outputs (design §5.1, §5.3):
/// each compares the ENTIRE document to a hand-derived expectation, so a
/// projected value that no field-by-field test asserted -- the round-1 hole
/// was <c>findings[].status</c> -- cannot drift, and a property that appears
/// or disappears fails the test. The expectations are derived from the
/// fixture files, the hand-verified text snapshots and the source lines that
/// project each value, never captured from a run: a captured expectation is
/// the tautology the update mode exists to refuse.
/// </summary>
public class MachineOutputTests
{
    private static readonly string AppendixA = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");
    private static readonly string OkfV02 = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02");
    private static readonly string OkfV02Reserved = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02_reserved");

    /// <summary>The date every pinned invocation here evaluates at; before any fixture's <c>stale_after</c>.</summary>
    private const string AsOf = "2026-09-25";

    private static (int Code, string Out, string Err) Run(params string[] args) => TestPaths.Run(args);

    /// <summary>A path with forward slashes, the form the expectations are written in.</summary>
    private static string Fwd(string path) => path.Replace('\\', '/');

    /// <summary>
    /// The one normalisation <c>GoldenParityTests</c> applies to JSON output
    /// (<c>Audit_json_matches_golden</c>): on Windows the projected file paths
    /// carry native separators, escaped by the serializer as the two-character
    /// sequence <c>\\</c>. Collapsed to <c>/</c> on the OUTPUT before parsing,
    /// so the expectations can be written once for every platform. The raw
    /// <c>bundle</c> string is asserted BEFORE this, so a projection that
    /// rewrote the argument's separators would still be caught.
    /// </summary>
    private static string NormalizeJsonPaths(string json) => json.Replace("\\\\", "/");

    /// <summary>The <c>bundle</c> property as projected, before any normalisation: the argument verbatim.</summary>
    private static void AssertRawBundle(string expectedArgument, string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        Assert.Equal(expectedArgument, doc.RootElement.GetProperty("bundle").GetString());
    }

    [Fact]
    public void Validate_json_projects_a_conformant_bundle_completely()
    {
        var r = Run("validate", AppendixA, "--as-of", AsOf, "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(AppendixA, r.Out);

        var b = Fwd(AppendixA);
        var expected = $$"""
        {
          "bundle": "{{b}}",
          "asOf": "2026-09-25",
          "evaluatedAt": "2026-09-25T00:00:00Z",
          "conformant": true,
          "conceptCount": 4,
          "errorCount": 0,
          "warningCount": 8,
          "infoCount": 0,
          "diagnostics": [
            { "severity": "warning", "code": "LegacyTimestamp", "path": "{{b}}/datasets/sales.md", "conceptId": "datasets/sales", "field": "timestamp", "message": "`timestamp` is a legacy field; prefer `generated.at`" },
            { "severity": "warning", "code": "MissingRecommendedField", "path": "{{b}}/tables/customers.md", "conceptId": "tables/customers", "field": "resource", "message": "missing recommended frontmatter field `resource`" },
            { "severity": "warning", "code": "MissingRecommendedField", "path": "{{b}}/tables/customers.md", "conceptId": "tables/customers", "field": "tags", "message": "missing recommended frontmatter field `tags`" },
            { "severity": "warning", "code": "LegacyTimestamp", "path": "{{b}}/tables/customers.md", "conceptId": "tables/customers", "field": "timestamp", "message": "`timestamp` is a legacy field; prefer `generated.at`" },
            { "severity": "warning", "code": "LegacyTimestamp", "path": "{{b}}/tables/orders.md", "conceptId": "tables/orders", "field": "timestamp", "message": "`timestamp` is a legacy field; prefer `generated.at`" },
            { "severity": "warning", "code": "MissingRecommendedField", "path": "{{b}}/tables/users.md", "conceptId": "tables/users", "field": "description", "message": "missing recommended frontmatter field `description`" },
            { "severity": "warning", "code": "MissingRecommendedField", "path": "{{b}}/tables/users.md", "conceptId": "tables/users", "field": "resource", "message": "missing recommended frontmatter field `resource`" },
            { "severity": "warning", "code": "MissingRecommendedField", "path": "{{b}}/tables/users.md", "conceptId": "tables/users", "field": "tags", "message": "missing recommended frontmatter field `tags`" }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Validate_json_projects_a_non_conformant_bundle_completely()
    {
        // The opposite case for every verdict-bearing field: conformant false,
        // errorCount non-zero, warningCount zero, diagnostics without a
        // conceptId (reserved files are not concepts), and one diagnostic with
        // a `field` (RootIndexExtraFrontmatter names okf_version) next to
        // three without.
        var r = Run("validate", OkfV02Reserved, "--as-of", AsOf, "--json");
        Assert.Equal(1, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(OkfV02Reserved, r.Out);

        var b = Fwd(OkfV02Reserved);
        var expected = $$"""
        {
          "bundle": "{{b}}",
          "asOf": "2026-09-25",
          "evaluatedAt": "2026-09-25T00:00:00Z",
          "conformant": false,
          "conceptCount": 1,
          "errorCount": 4,
          "warningCount": 0,
          "infoCount": 0,
          "diagnostics": [
            { "severity": "error", "code": "UnparseableIndex", "path": "{{b}}/broken/index.md", "conceptId": null, "field": null, "message": "unparseable index.md: Invalid YAML in frontmatter: YAML error at line 1: expected ',' or ']' in flow sequence" },
            { "severity": "error", "code": "RootIndexExtraFrontmatter", "path": "{{b}}/index.md", "conceptId": null, "field": "okf_version", "message": "root index.md frontmatter must declare only `okf_version` (§12)" },
            { "severity": "error", "code": "IndexHasFrontmatter", "path": "{{b}}/sub/index.md", "conceptId": null, "field": null, "message": "index.md must not contain frontmatter (§8)" },
            { "severity": "error", "code": "LogDateInvalid", "path": "{{b}}/log.md", "conceptId": null, "field": null, "message": "log date heading is not ISO-8601 `YYYY-MM-DD`: \"not-a-date\"" }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }
}
```

- [ ] **Step 6: Run the two projection tests**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~MachineOutputTests.Validate_json" --no-restore`
Expected: 2 passed. If a value differs, decide from the derivation list above whether the literal was mis-derived (fix it, citing the source) or the projection is wrong (a real finding — stop and report). Never paste the actual output into the expectation.

- [ ] **Step 7: Retire the one presence-only test the projection subsumes**

In `tests/OKF4net.Tests/CliTests.cs`, delete the whole method `Validate_json_reports_bundle_conformance_and_diagnostics` (line 371). Its assertions and where each now lives: exit code and `conformant`/`conceptCount`/`errorCount` → the first projection; `bundle` equal to the raw argument → `AssertRawBundle`; a non-empty `diagnostics` array with `severity`/`code`/`message` present → the complete array. Do **not** delete `Validate_json_reports_the_instant_it_evaluated_at`, `Validate_json_records_the_date_it_was_evaluated_against` or `Validate_json_diagnostic_field_is_populated_when_applicable`.

- [ ] **Step 8: Run the CLI + projection set, format, commit**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~CliTests|FullyQualifiedName~MachineOutputTests|FullyQualifiedName~JsonShapeTests" --no-restore`
Expected: all passed, 0 failed.

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/JsonShape.cs tests/OKF4net.Tests/JsonShapeTests.cs tests/OKF4net.Tests/MachineOutputTests.cs tests/OKF4net.Tests/CliTests.cs
git commit -m "test(cli): assert the whole validate --json projection, structurally, against a derived expectation"
```

---

### Task 3: Full projection of `info --json`

Spec §5.3 (info row): the barest surface — only `conceptCount` was asserted by value, and no snapshot compensates because the `info` snapshot captures the text rendering.

**Files:**
- Modify: `tests/OKF4net.Tests/MachineOutputTests.cs`
- Modify: `tests/OKF4net.Tests/CliTests.cs` — delete `Info_json_reports_bundle_summary` (line 404); keep `Info_json_types_is_present_and_empty_for_a_bundle_with_no_concepts` (the empty-histogram boundary).

**Interfaces:**
- Consumes: `JsonShape.AssertEquivalent`, `Fwd`, `NormalizeJsonPaths`, `AssertRawBundle` from Task 2.

- [ ] **Step 1: Write the two tests**

Derivation. `appendix_a`: 4 concepts and the `1 BigQuery Dataset` / `3 BigQuery Table` split are the `concepts:` and `types:` lines of `tests/fixtures/golden/info.out`; `links: 5 internal (0 broken)` is its last line (`linkCount` sums `LinksFrom` over every concept, `JsonOutput.cs:158-162`; `brokenLinkCount` is `bundle.BrokenLinks().Count`, `:176`); `log.md` is the one reserved file in the fixture and there is no `index.md` (`tests/fixtures/README.md`, Layout), so `logFileCount` 1, `indexFileCount` 0, and `okfVersion` null (no root index to declare it). The built bundle holds every opposite: a root `index.md` declaring `okf_version: "0.2"` (→ `okfVersion`, `indexFileCount` 1, `logFileCount` 0), `a.md` with two links of which one unresolved (→ `linkCount` 2, `brokenLinkCount` 1), two parsable `Note`s (→ `types {"Note": 2}`, `conceptCount` 2), and `bad.md` whose frontmatter line 2 holds a YAML alias — rejected by the parser with the constant `AliasMessage` at `src/OKF4net/Yaml/YamlParser.cs:30`, wrapped as `YAML error at line 2: …` by `YamlParseException.cs:28` (frontmatter line numbering), then as `Invalid YAML in frontmatter: …` by `OkfDocument.cs:174`, and projected verbatim with the file's path by `JsonOutput.cs:164-165`. The fence is not indented, so no `(file line N)` suffix. That message is a derived literal below, not read from the output.

```csharp
    [Fact]
    public void Info_json_projects_appendix_a_completely()
    {
        var r = Run("info", AppendixA, "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(AppendixA, r.Out);

        var expected = $$"""
        {
          "bundle": "{{Fwd(AppendixA)}}",
          "okfVersion": null,
          "conceptCount": 4,
          "indexFileCount": 0,
          "logFileCount": 1,
          "types": { "BigQuery Dataset": 1, "BigQuery Table": 3 },
          "linkCount": 5,
          "brokenLinkCount": 0,
          "parseErrors": []
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Info_json_projects_the_opposite_cases_completely()
    {
        // okfVersion present, a broken link, a parse error, an index file and
        // no log file: the opposite of every appendix_a value above.
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n\n# Root\n");
        tmp.Write("a.md", "---\ntype: Note\ntitle: A\ndescription: d\n---\n\nSee [b](/b.md) and [gone](/missing.md).\n");
        tmp.Write("b.md", "---\ntype: Note\ntitle: B\ndescription: d\n---\n\nbody\n");
        tmp.Write("bad.md", "---\ntype: Note\nk: *a\n---\nbody\n");

        var r = Run("info", tmp.Path, "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(tmp.Path, r.Out);

        // The parse error's message is DERIVED: YamlParser.AliasMessage, wrapped
        // by YamlParseException.FormatMessage with the frontmatter line (the
        // alias is on line 2 of the frontmatter), then by OkfDocument.Parse's
        // "Invalid YAML in frontmatter: " prefix. Not read from r.Out.
        var expected = $$"""
        {
          "bundle": "{{Fwd(tmp.Path)}}",
          "okfVersion": "0.2",
          "conceptCount": 2,
          "indexFileCount": 1,
          "logFileCount": 0,
          "types": { "Note": 2 },
          "linkCount": 2,
          "brokenLinkCount": 1,
          "parseErrors": [
            { "path": "{{Fwd(Path.Combine(tmp.Path, "bad.md"))}}", "message": "Invalid YAML in frontmatter: YAML error at line 2: YAML aliases (*name) are not supported by the OKF YAML subset" }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~MachineOutputTests.Info_json" --no-restore`
Expected: 2 passed. A mismatch on the message means the derivation chain above changed — check the three source lines before touching the literal.

- [ ] **Step 3: Delete `Info_json_reports_bundle_summary` from `CliTests.cs`**

It asserted `conceptCount == 4`, `types` non-empty, and the presence of `linkCount`/`brokenLinkCount` — all asserted by value now.

- [ ] **Step 4: Run, format, commit**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~CliTests|FullyQualifiedName~MachineOutputTests" --no-restore`
Expected: all passed.

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/MachineOutputTests.cs tests/OKF4net.Tests/CliTests.cs
git commit -m "test(cli): assert the whole info --json projection; it was the barest machine output"
```

---

### Task 4: Full projection of `audit --json`

Spec §5.3 (audit row), §3 (the round-1 hole: `findings[].status`), and the spec's "catégories distinctes" requirement: the `okf_v02` fixture yields only `stable`/`human-reviewed` findings and zero counts for `draft`, `deprecated` and `machine-confirmed`, so a projection that permuted the categories would pass. A built bundle with asymmetric counts closes that.

**Files:**
- Modify: `tests/OKF4net.Tests/MachineOutputTests.cs`
- Modify: `tests/OKF4net.Tests/CliTests.cs` — delete `Audit_json_carries_counts_query_and_findings` (line 1026; every value it asserts at 2099-06-01 is asserted by the first projection at the same date, and its `EndsWith("\n")` is kept in every projection). Keep `Audit_json_spells_trust_tiers_the_same_way_in_counts_and_findings` (it checks the **order** of the three `trust` count properties, which the structural comparator deliberately ignores — a distinct check), `Audit_json_serializes_trust_query_in_ladder_order` (two tiers passed in reverse order; the projections pass one) and `Audit_json_keeps_a_malformed_stale_after_raw_and_not_stale` (malformed branch).

- [ ] **Step 1: Write the four tests**

Derivation, `okf_v02`: two `Metric`s. `metrics/dau.md` — `status: stable`, `verified` by `process:nightly` then `human:ada` (→ `human-reviewed`, `Trust.cs:38-46`: any human actor), `stale_after: 2099-01-01T00:00:00Z` (→ stale at 2099-06-01, not at 2026-09-25); `metrics/legacy.md` — `status: retired`, unknown and treated as `stable` (`validate-v02.out` line 2), no `verified` (→ `unverified`), no `stale_after`. Counts are bundle-wide, not query-scoped (`JsonOutput.cs:222-229`). The default query is `--stale` (`query.stale` true, `JsonOutput.cs:218`); with any filter `--stale` is off; `query.trust` serialises in ladder order (`:186-198`); `path` is the finding's file path (`:203`), `staleAfter` the raw frontmatter value (`:208`).

Built bundle: `d1.md`, `d2.md` — `status: draft`, `verified` by `process:bot` only (→ `machine-confirmed`), no `stale_after`; `e.md` — `status: deprecated`, no `verified` (→ `unverified`). Counts: `trust {unverified: 1, machine-confirmed: 2, human-reviewed: 0}`, `status {draft: 2, stable: 0, deprecated: 1}` — every count distinct from its neighbours, so a permutation of either vocabulary changes the document. Query `--status draft` selects `d1` and `d2`; `stale` false and `staleAfter` null for both (no `stale_after`).

```csharp
    [Fact]
    public void Audit_json_projects_a_stale_finding_completely()
    {
        var r = Run("audit", OkfV02, "--as-of", "2099-06-01", "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(OkfV02, r.Out);

        var b = Fwd(OkfV02);
        var expected = $$"""
        {
          "bundle": "{{b}}",
          "asOf": "2099-06-01",
          "evaluatedAt": "2099-06-01T00:00:00Z",
          "conceptCount": 2,
          "query": { "stale": true, "trust": null, "status": null, "type": null },
          "trust": { "unverified": 1, "machine-confirmed": 0, "human-reviewed": 1 },
          "status": { "draft": 0, "stable": 2, "deprecated": 0 },
          "staleCount": 1,
          "findings": [
            { "conceptId": "metrics/dau", "path": "{{b}}/metrics/dau.md", "type": "Metric", "title": "Daily Active Users", "trust": "human-reviewed", "status": "stable", "staleAfter": "2099-01-01T00:00:00Z", "stale": true }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Audit_json_projects_an_empty_stale_query_completely()
    {
        // Same bundle before anything is stale: staleCount 0, findings empty,
        // the counts unchanged -- they are bundle-wide, not query-scoped.
        var r = Run("audit", OkfV02, "--as-of", AsOf, "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);

        var expected = $$"""
        {
          "bundle": "{{Fwd(OkfV02)}}",
          "asOf": "2026-09-25",
          "evaluatedAt": "2026-09-25T00:00:00Z",
          "conceptCount": 2,
          "query": { "stale": true, "trust": null, "status": null, "type": null },
          "trust": { "unverified": 1, "machine-confirmed": 0, "human-reviewed": 1 },
          "status": { "draft": 0, "stable": 2, "deprecated": 0 },
          "staleCount": 0,
          "findings": []
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Audit_json_projects_a_filtered_query_completely()
    {
        // Every query field non-null at once; --stale is off because a filter
        // is a report over the selection, so `dau` is listed for its trust and
        // still reported stale at this date.
        var r = Run("audit", OkfV02, "--as-of", "2099-06-01", "--status", "stable", "--type", "Metric", "--trust", "human-reviewed", "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);

        var b = Fwd(OkfV02);
        var expected = $$"""
        {
          "bundle": "{{b}}",
          "asOf": "2099-06-01",
          "evaluatedAt": "2099-06-01T00:00:00Z",
          "conceptCount": 2,
          "query": { "stale": false, "trust": ["human-reviewed"], "status": "stable", "type": "Metric" },
          "trust": { "unverified": 1, "machine-confirmed": 0, "human-reviewed": 1 },
          "status": { "draft": 0, "stable": 2, "deprecated": 0 },
          "staleCount": 1,
          "findings": [
            { "conceptId": "metrics/dau", "path": "{{b}}/metrics/dau.md", "type": "Metric", "title": "Daily Active Users", "trust": "human-reviewed", "status": "stable", "staleAfter": "2099-01-01T00:00:00Z", "stale": true }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }

    [Fact]
    public void Audit_json_projects_distinct_categories_with_asymmetric_counts()
    {
        // okf_v02 only ever yields stable / human-reviewed, with zeros in the
        // other buckets, so a projection that permuted the vocabularies would
        // pass the three tests above. Here every count differs from its
        // neighbours: trust 1/2/0, status 2/0/1.
        using var tmp = new TempDir();
        tmp.Write("d1.md", "---\ntype: Note\ntitle: D1\nstatus: draft\nverified:\n  - { by: process:bot, at: 2026-06-01T00:00:00Z }\n---\n\nbody\n");
        tmp.Write("d2.md", "---\ntype: Note\ntitle: D2\nstatus: draft\nverified:\n  - { by: process:bot, at: 2026-06-01T00:00:00Z }\n---\n\nbody\n");
        tmp.Write("e.md", "---\ntype: Note\ntitle: E\nstatus: deprecated\n---\n\nbody\n");

        var r = Run("audit", tmp.Path, "--as-of", AsOf, "--status", "draft", "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);
        AssertRawBundle(tmp.Path, r.Out);

        var b = Fwd(tmp.Path);
        var expected = $$"""
        {
          "bundle": "{{b}}",
          "asOf": "2026-09-25",
          "evaluatedAt": "2026-09-25T00:00:00Z",
          "conceptCount": 3,
          "query": { "stale": false, "trust": null, "status": "draft", "type": null },
          "trust": { "unverified": 1, "machine-confirmed": 2, "human-reviewed": 0 },
          "status": { "draft": 2, "stable": 0, "deprecated": 1 },
          "staleCount": 0,
          "findings": [
            { "conceptId": "d1", "path": "{{b}}/d1.md", "type": "Note", "title": "D1", "trust": "machine-confirmed", "status": "draft", "staleAfter": null, "stale": false },
            { "conceptId": "d2", "path": "{{b}}/d2.md", "type": "Note", "title": "D2", "trust": "machine-confirmed", "status": "draft", "staleAfter": null, "stale": false }
          ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~MachineOutputTests.Audit_json" --no-restore`
Expected: 4 passed. If the built-bundle test differs on the finding order (`d1` before `d2`), the walk order is `bundle.Concepts` (component-wise path order, `BundleTests.Walk_order_is_component_wise_not_a_flat_string_sort`) — `d1` sorts before `d2`; a different order is a finding to report.

- [ ] **Step 3: Delete `Audit_json_carries_counts_query_and_findings` from `CliTests.cs`**

- [ ] **Step 4: Run, format, commit**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~CliTests|FullyQualifiedName~MachineOutputTests" --no-restore`
Expected: all passed.

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/MachineOutputTests.cs tests/OKF4net.Tests/CliTests.cs
git commit -m "test(cli): assert the whole audit --json projection, with categories whose permutation is detectable"
```

---

### Task 5: DOT structural test and the `okf info` links line

Spec §5.4 (grammar, edge **set with its cardinality**, determinism — not order) and §3 (the `links:` line carries two counts). The three existing `Graph_dot_*` tests in `CliTests` (prefix, dashed broken edge, unstyled resolved edge) are each subsumed by the structural test and are retired.

**Files:**
- Modify: `tests/OKF4net.Tests/MachineOutputTests.cs`
- Modify: `tests/OKF4net.Tests/CliTests.cs` — delete `Graph_dot_prints_digraph`, `Graph_dot_styles_broken_links_dashed_and_red`, `Graph_dot_does_not_style_resolvable_links` (around lines 500–530). Keep `Graph_plain_text_lists_links_and_marks_broken_ones` (the text renderer).

- [ ] **Step 1: Write the DOT tests and the links-line tests**

Append to `MachineOutputTests`:

```csharp
    /// <summary>
    /// The restricted DOT grammar <c>WriteGraphDot</c> emits: header, the
    /// fixed <c>rankdir</c> line, one edge statement per link, a closing
    /// brace, nothing else. This is the guarantee -- Graphviz is not installed
    /// in CI and is not invoked; a document this grammar accepts that
    /// Graphviz would reject is outside what the test can promise.
    /// </summary>
    private static readonly Regex DotEdgeLine = new(
        "^  \"[^\"]+\" -> \"[^\"]+\"( \\[style=dashed, color=red\\])?;$",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Graph_dot_follows_the_restricted_grammar_with_the_expected_edge_set()
    {
        // Three concepts: one with a resolved link and a broken one, one
        // linked-to with no outgoing link, one isolated. Edge ORDER is not
        // asserted -- it is presentation (design §1); the SET is, and so is
        // its size, so an edge emitted twice cannot hide in a set.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: A\n---\n\nSee [b](/b.md) and [gone](/missing.md).\n");
        tmp.Write("b.md", "---\ntype: Note\ntitle: B\n---\n\nno links\n");
        tmp.Write("c.md", "---\ntype: Note\ntitle: C\n---\n\nisolated\n");

        var r = Run("graph", tmp.Path, "--dot");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("}\n", r.Out, StringComparison.Ordinal);

        var lines = r.Out.Split('\n');
        Assert.Equal("", lines[^1]); // the trailing newline
        Assert.Equal("digraph okf {", lines[0]);
        Assert.Equal("  rankdir=LR; node [shape=box, fontsize=10];", lines[1]);
        Assert.Equal("}", lines[^2]);

        var edges = lines[2..^2];
        Assert.Equal(2, edges.Length);
        Assert.All(edges, line => Assert.Matches(DotEdgeLine, line));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "  \"a\" -> \"b\";",
                "  \"a\" -> \"missing\" [style=dashed, color=red];",
            },
            edges.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Graph_dot_is_deterministic_across_runs()
    {
        var first = Run("graph", AppendixA, "--dot");
        var second = Run("graph", AppendixA, "--dot");
        Assert.Equal(0, first.Code);
        Assert.Equal(first.Out, second.Out);
    }

    /// <summary>
    /// The one text line the design keeps under direct test (§5.1): it carries
    /// two counts, and a rewording that drops the second silently loses the
    /// broken-link information. Spacing is presentation and is not asserted.
    /// </summary>
    private static readonly Regex LinksLine = new(@"^links:\s+(\d+) internal \((\d+) broken\)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void Info_links_line_reports_both_counts()
    {
        var r = Run("info", AppendixA);
        Assert.Equal(0, r.Code);
        var m = LinksLine.Match(r.Out);
        Assert.True(m.Success, "no `links: N internal (M broken)` line in:\n" + r.Out);
        Assert.Equal("5", m.Groups[1].Value);
        Assert.Equal("0", m.Groups[2].Value);
    }

    [Fact]
    public void Info_links_line_counts_a_broken_link()
    {
        // A broken link is still a link: `linkCount` sums LinksFrom, which
        // includes unresolved targets (JsonOutput.WriteInfo), so one broken
        // link reads `1 internal (1 broken)` -- verified by execution.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: A\n---\n\n[gone](/missing.md)\n");

        var r = Run("info", tmp.Path);
        Assert.Equal(0, r.Code);
        var m = LinksLine.Match(r.Out);
        Assert.True(m.Success, r.Out);
        Assert.Equal("1", m.Groups[1].Value);
        Assert.Equal("1", m.Groups[2].Value);
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~MachineOutputTests.Graph|FullyQualifiedName~MachineOutputTests.Info_links" --no-restore`
Expected: 4 passed.

- [ ] **Step 3: Delete the three `Graph_dot_*` tests from `CliTests.cs`**

`Graph_dot_prints_digraph` (asserts the `digraph okf {` prefix → line 0 above), `Graph_dot_styles_broken_links_dashed_and_red` (one dashed red edge → the `missing` edge above), `Graph_dot_does_not_style_resolvable_links` (one unstyled edge → the `b` edge above).

- [ ] **Step 4: Run, format, commit**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~CliTests|FullyQualifiedName~MachineOutputTests" --no-restore`
Expected: all passed.

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/MachineOutputTests.cs tests/OKF4net.Tests/CliTests.cs
git commit -m "test(cli): pin the DOT grammar and edge set with its size, determinism, and both counts of info's links line"
```

---

### Task 6: Direct §8 index tests

Spec §5.5. Each test names its input and its distinctive expectation; none reads a snapshot.

**Files:**
- Modify: `tests/OKF4net.Tests/IndexTests.cs` (append; reuse `WriteDoc`)

- [ ] **Step 1: Write the five tests**

```csharp
    /// <summary>
    /// §8: an index file has no frontmatter. The existing tests cover a root
    /// index whose PRE-EXISTING frontmatter is dropped or preserved; this is
    /// the case where no index existed at all, so nothing could be preserved
    /// and the generated root must start straight at its first section.
    /// </summary>
    [Fact]
    public void Regenerate_writes_no_frontmatter_when_no_root_index_pre_existed()
    {
        using var tmp = new TempDir();
        WriteDoc(tmp, "notes/a.md", "Note", "A", "First.");

        IndexGenerator.RegenerateIndexes(tmp.Path);

        var rootIndex = File.ReadAllText(Path.Combine(tmp.Path, "index.md"));
        Assert.StartsWith("# ", rootIndex, StringComparison.Ordinal);
        Assert.DoesNotContain("---", rootIndex, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reserved log.md has no frontmatter and therefore no type; the
    /// generator lists it under `# Other` with its file stem as the title.
    /// This is our behaviour, not a §8 requirement -- §8 does not name the
    /// group -- pinned because only a snapshot pinned it before.
    /// </summary>
    [Fact]
    public void Regenerate_lists_a_reserved_log_under_Other_by_file_stem()
    {
        using var tmp = new TempDir();
        tmp.Write("log.md", "# Log\n\n## 2026-05-27\n* **Added**: something.\n");
        WriteDoc(tmp, "a.md", "Note", "A", "First.");

        IndexGenerator.RegenerateIndexes(tmp.Path);

        var rootIndex = File.ReadAllText(Path.Combine(tmp.Path, "index.md"));
        Assert.Contains("# Other\n\n* [log](log.md)\n", rootIndex, StringComparison.Ordinal);
    }

    /// <summary>An entry without a description ends at the link -- no dangling ` - `.</summary>
    [Fact]
    public void Regenerate_omits_the_description_suffix_when_there_is_none()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: Bare\n---\n\nbody\n");

        IndexGenerator.RegenerateIndexes(tmp.Path);

        var rootIndex = File.ReadAllText(Path.Combine(tmp.Path, "index.md"));
        Assert.Contains("* [Bare](a.md)\n", rootIndex, StringComparison.Ordinal);
        Assert.DoesNotContain("* [Bare](a.md) - ", rootIndex, StringComparison.Ordinal);
    }

    /// <summary>
    /// The generated index.md must not list itself on the NEXT regeneration.
    /// A single pass on a bundle without indexes never exercises this branch
    /// (there is no index.md to skip yet), which is why the snapshot never did.
    /// </summary>
    [Fact]
    public void Regenerate_does_not_list_index_md_as_an_entry_on_a_second_pass()
    {
        using var tmp = new TempDir();
        WriteDoc(tmp, "tables/a.md", "Table", "A", "First.");

        IndexGenerator.RegenerateIndexes(tmp.Path);
        IndexGenerator.RegenerateIndexes(tmp.Path);

        var tablesIndex = File.ReadAllText(Path.Combine(tmp.Path, "tables", "index.md"));
        Assert.DoesNotContain("(index.md)", tablesIndex, StringComparison.Ordinal);
        var rootIndex = File.ReadAllText(Path.Combine(tmp.Path, "index.md"));
        Assert.DoesNotContain("[index](index.md)", rootIndex, StringComparison.Ordinal);
        Assert.Contains("(tables/index.md)", rootIndex, StringComparison.Ordinal); // the subdirectory link is not the self-listing
    }

    /// <summary>
    /// The default synthesizer's text for a subdirectory: `Contains N: titles`.
    /// Executed by every RegenerateIndexes call in this file, but its exact
    /// wording was pinned only by the index snapshot until now.
    /// </summary>
    [Fact]
    public void Default_synthesizer_lists_child_titles_in_order()
    {
        using var tmp = new TempDir();
        WriteDoc(tmp, "tables/customers.md", "Table", "Customers", "c");
        WriteDoc(tmp, "tables/orders.md", "Table", "Orders", "o");
        WriteDoc(tmp, "tables/users.md", "Table", "Users", "u");

        IndexGenerator.RegenerateIndexes(tmp.Path);

        var rootIndex = File.ReadAllText(Path.Combine(tmp.Path, "index.md"));
        Assert.Contains("* [tables](tables/index.md) - Contains 3: Customers, Orders, Users.\n", rootIndex, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~IndexTests" --no-restore`
Expected: all passed. If `Regenerate_lists_a_reserved_log_under_Other_by_file_stem` fails on the exact section text, compare with `tests/fixtures/golden/index-input/index.md` lines 1-3 (`# Other`, blank, `* [log](log.md)`), which is the behaviour being pinned.

- [ ] **Step 3: Format, commit**

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/IndexTests.cs
git commit -m "test(index): pin the §8 structure and our index conventions directly, not only through the snapshot"
```

---

### Task 7: Direct serialisation and `fmt` tests

Spec §5.5 (Serialize full text with three keys, idempotence, fmt stdout).

**Files:**
- Modify: `tests/OKF4net.Tests/DocumentTests.cs` (append)
- Modify: `tests/OKF4net.Tests/CliTests.cs` (append directly after `Fmt_write_normalizes_file_in_place`, line 546)

- [ ] **Step 1: Write the serialisation tests**

Append to `DocumentTests`:

```csharp
    /// <summary>
    /// The full serialized text, in one assertion: `---`, three frontmatter
    /// keys in order, `---`, one blank line, the body, one trailing newline.
    /// Every other test here round-trips through Parse and compares structure;
    /// this is the only place the envelope itself is pinned outside a snapshot.
    /// </summary>
    [Fact]
    public void Serialize_emits_the_canonical_envelope_exactly()
    {
        var doc = OkfDocument.Parse("---\ntype: BigQuery Table\ntitle: Users\ndescription: Application users.\n---\n\nApplication users; not part of the sales domain.\n");

        Assert.Equal(
            "---\ntype: BigQuery Table\ntitle: Users\ndescription: Application users.\n---\n\nApplication users; not part of the sales domain.\n",
            doc.Serialize());
    }

    /// <summary>A document already in canonical form re-serializes to the same bytes.</summary>
    [Fact]
    public void Serialize_is_idempotent_on_a_canonical_document()
    {
        var canonical = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a", "tables", "users.md"));

        var once = OkfDocument.Parse(canonical).Serialize();
        var twice = OkfDocument.Parse(once).Serialize();

        Assert.Equal(canonical, once);
        Assert.Equal(once, twice);
    }
```

- [ ] **Step 2: Write the `fmt` stdout test**

In `CliTests.cs`, directly after the closing brace of `Fmt_write_normalizes_file_in_place` (line 546 onward):

```csharp
    /// <summary>
    /// `fmt` without `-w` prints the formatted document to stdout and leaves
    /// the file alone. Until now the only passing test through this branch was
    /// the snapshot comparison. On this canonical input the output equals the
    /// file; the test pins the branch, not a normalisation.
    /// </summary>
    [Fact]
    public void Fmt_without_write_prints_the_document_and_leaves_the_file_untouched()
    {
        var path = Path.Combine(BundlePath, "tables", "users.md");
        var before = File.ReadAllBytes(path);

        var r = Run("fmt", path);

        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Err);
        Assert.Equal(File.ReadAllText(path), r.Out);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
```

- [ ] **Step 3: Run, format, commit**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~DocumentTests.Serialize|FullyQualifiedName~CliTests.Fmt" --no-restore`
Expected: all passed.

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/DocumentTests.cs tests/OKF4net.Tests/CliTests.cs
git commit -m "test(document): pin Serialize's envelope and idempotence, and fmt's stdout branch, directly"
```

---

### Task 8: The two deletions, with their dated entry and an accurate Layout

Spec §4, §5.6. `golden/index-input/`'s five bundle copies are read by no test; the four `.exitcode` files are integers that belong in assertions — `1` for `validate-reserved` (the §11 non-conformant verdict, mapped to `1` by our exit-code contract), `0` for the other three. The README's Layout is corrected in the same commit so no commit describes files that are gone.

**Files:**
- Delete: `tests/fixtures/golden/index-input/log.md`, `datasets/sales.md`, `tables/customers.md`, `tables/orders.md`, `tables/users.md`
- Delete: `tests/fixtures/golden/validate.exitcode`, `validate-v02.exitcode`, `validate-computation.exitcode`, `validate-reserved.exitcode`
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs:72,92,100,108` (exit-code assertions) and `:205-208` (cardinality comment)
- Modify: `tests/fixtures/README.md` — Layout lines 15-16 and 23-28, plus the appended dated entry

- [ ] **Step 1: Confirm the five copies are unread**

Run from the repo root: `grep -rn "index-input" tests/OKF4net.Tests src`
Expected: only `GoldenParityTests.cs` (the three `index.md` reads). Stop if anything else references the directory.

- [ ] **Step 2: Replace the exit-code file reads**

In `GoldenParityTests.cs`, replace the four `Assert.Equal(int.Parse(Golden("....exitcode")), r.Code);` lines with, in order:

```csharp
        Assert.Equal(0, r.Code); // warnings only: appendix_a stays conformant (§11)
```

```csharp
        Assert.Equal(0, r.Code); // warnings only: okf_v02 stays conformant (§11)
```

```csharp
        Assert.Equal(0, r.Code); // §10 diagnostics are warnings; conformance (§11) is unaffected
```

```csharp
        // §11 condition 3 fails: malformed reserved files make the bundle
        // non-conformant, and this CLI's contract maps that verdict to exit
        // code 1 -- the spec defines the verdict, the integer is ours.
        Assert.Equal(1, r.Code);
```

Replace the cardinality comment at lines 205-206 with:

```csharp
        // Exactly the 3 generated index.md files plus the 5 source documents
        // copied in from appendix_a: catches a file created in excess or a
        // net deletion; not a modified original, nor a delete-and-create.
```

- [ ] **Step 3: Delete the nine files**

```bash
git rm tests/fixtures/golden/index-input/log.md tests/fixtures/golden/index-input/datasets/sales.md tests/fixtures/golden/index-input/tables/customers.md tests/fixtures/golden/index-input/tables/orders.md tests/fixtures/golden/index-input/tables/users.md
git rm tests/fixtures/golden/validate.exitcode tests/fixtures/golden/validate-v02.exitcode tests/fixtures/golden/validate-computation.exitcode tests/fixtures/golden/validate-reserved.exitcode
```

- [ ] **Step 4: Run the snapshot tests**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~GoldenParityTests" --no-restore`
Expected: 11 passed.

- [ ] **Step 5: Correct the Layout and append the dated entry in `tests/fixtures/README.md`**

Replace the two Layout bullets

```markdown
- `golden/validate.out` — stdout of `okf validate tests/fixtures/appendix_a`.
- `golden/validate.exitcode` — the process exit code of that same run, as a
  bare ASCII digit with **no trailing newline** (currently `0`).
```

with

```markdown
- `golden/validate.out` — stdout of `okf validate tests/fixtures/appendix_a
  --as-of 2026-09-25`; the exit code (`0`) is asserted in the test.
```

and the `golden/index-input/` bullet (lines 23-28) with

```markdown
- `golden/index-input/` — the three `index.md` files (`index.md`,
  `datasets/index.md`, `tables/index.md`) the index generator writes over a
  copy of `appendix_a/`; the test makes that copy in a temporary directory
  and reads only these three.
```

Append at the end of the file:

```markdown

## Two deletions under the fixtures-authority design (2026-10-01)

Arbitration: `docs/superpowers/specs/2026-09-22-golden-fixtures-authority-design.md`, §4 and §5.6. Both are element removals (criterion 2 of that design's diff rule), hence recorded here.

- **`golden/index-input/`'s five bundle copies** (`log.md`, `datasets/sales.md`, `tables/{customers,orders,users}.md`) are deleted. `Index_generation_matches_golden` copies `appendix_a/` into a temporary directory and reads only the three generated `index.md` files, so no assertion changes. What is lost is a standalone historical archive of the input bundle as it was captured on 2026-07-21; what is kept is the three `index.md` outputs and the file-count assertion (which catches an extra file or a net deletion, not a modified original).
- **The four `*.exitcode` files** are deleted. Each held one ASCII digit; the values now live in `GoldenParityTests` as assertions with their reason: `0` for `appendix_a`, `okf_v02` and `okf_v02_computation` (warnings only, still conformant), `1` for `okf_v02_reserved` (§11 condition 3 fails; the integer is this CLI's contract, not the spec's).
```

- [ ] **Step 6: Format, commit**

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/GoldenParityTests.cs tests/fixtures/README.md
git commit -m "test(golden): drop the unread bundle copies and the four exit-code files; the values are assertions now"
```

---

### Task 9: The scoped update mode

Spec §2 and §5.2 in full, with the two corrections the plan review forced: a fixture may have **one** public constructor, and a group commit must **restore** what it already replaced when a later move fails. The capture path **starts at the exit-code guard** in `AssertGolden` and has two phases, each named in its failure message: *before writing* (the guard, the artefact reader with its own cardinality guards, the commit's staging and backups — the message ends with "nothing written") and *while replacing* (the moves — the message says the group was restored, or names what could not be). Both are a *capture failure*, distinct from the deliberate *refuses to assert*. What precedes the capture path — `TestPaths.Run` (which returns exit codes rather than throwing for CLI errors) and, for the index test, `IndexGenerator.RegenerateIndexes` — is an ordinary test failure in either mode, with nothing written, and the plan claims no more than that.

**Files:**
- Create: `tests/OKF4net.Tests/GoldenUpdate.cs`
- Create: `tests/OKF4net.Tests/GoldenUpdateTests.cs`
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs` (collection attribute, constructor, `AssertGolden`, every test, the class docstring's cref)

**Interfaces:**
- Produces: `GoldenUpdate.Variable` (`"OKF_UPDATE_GOLDEN"`), `GoldenUpdate.CollectionName`, `GoldenUpdate.Groups` (test name → snapshot files), `GoldenUpdate.ParseScope(string?)`, `GoldenUpdate.Scope` (the collection fixture: one public constructor; `Includes(test)`, `MarkCaptured(test)`, teardown check; an `internal` constructor for tests), `GoldenUpdate.Commit(Scope, test, artefacts, goldenRoot?)` (atomic per group, restore on failure; an `internal` overload takes a `Func<DirectoryInfo> createStaging` so a test can fail the very first step without mutating process-wide temp variables), `GoldenUpdate.RefusesToAssert(test)`.

- [ ] **Step 1: Write the unit tests for the parts that run without the variable**

Create `tests/OKF4net.Tests/GoldenUpdateTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// The update mode's pieces that can be exercised without setting the
/// variable: scope parsing (design §5.2 -- the whole list is validated before
/// any write), the atomic group commit with restore, the encoding contract of
/// what it writes, and the teardown check. The end-to-end behaviour under the
/// variable -- including the <c>--filter</c> interaction -- is verified by
/// hand once (README, "Regenerating") because it cannot run inside one test.
/// </summary>
public class GoldenUpdateTests
{
    [Fact]
    public void ParseScope_returns_an_empty_set_when_the_variable_is_unset_or_blank()
    {
        Assert.Empty(GoldenUpdate.ParseScope(null));
        Assert.Empty(GoldenUpdate.ParseScope(""));
        Assert.Empty(GoldenUpdate.ParseScope("   "));
    }

    [Fact]
    public void ParseScope_accepts_exact_test_names_and_trims_whitespace()
    {
        var scope = GoldenUpdate.ParseScope(" Info_output_matches_golden , Graph_dot_matches_golden ");
        Assert.Equal(["Graph_dot_matches_golden", "Info_output_matches_golden"], scope.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ParseScope_rejects_unknown_empty_and_duplicate_names()
    {
        var unknown = Assert.Throws<InvalidOperationException>(() => GoldenUpdate.ParseScope("Info_output_matches_golden,info_output_matches_golden"));
        Assert.Contains("unknown: info_output_matches_golden", unknown.Message, StringComparison.Ordinal);

        var empty = Assert.Throws<InvalidOperationException>(() => GoldenUpdate.ParseScope("Info_output_matches_golden,,Graph_dot_matches_golden"));
        Assert.Contains("empty element", empty.Message, StringComparison.Ordinal);

        var duplicate = Assert.Throws<InvalidOperationException>(() => GoldenUpdate.ParseScope("Info_output_matches_golden,Info_output_matches_golden"));
        Assert.Contains("duplicate: Info_output_matches_golden", duplicate.Message, StringComparison.Ordinal);

        var wildcard = Assert.Throws<InvalidOperationException>(() => GoldenUpdate.ParseScope("Validate_*"));
        Assert.Contains("unknown: Validate_*", wildcard.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseScope_reports_every_problem_at_once()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => GoldenUpdate.ParseScope("Nope,Info_output_matches_golden,,Info_output_matches_golden"));
        Assert.Contains("unknown: Nope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("empty element", ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate: Info_output_matches_golden", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Groups_name_every_snapshot_file_exactly_once_and_every_golden_test()
    {
        var goldenRoot = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "golden");
        var onDisk = Directory.GetFiles(goldenRoot, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(goldenRoot, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();
        var declared = GoldenUpdate.Groups.Values.SelectMany(g => g).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(onDisk, declared);
        Assert.Equal(declared.Count, declared.Distinct(StringComparer.Ordinal).Count());

        var goldenTests = typeof(GoldenParityTests).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: false).Length > 0)
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(goldenTests, GoldenUpdate.Groups.Keys.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Commit_writes_utf8_without_bom_and_lf_only()
    {
        using var root = new TempDir();
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Info_output_matches_golden"));

        GoldenUpdate.Commit(scope, "Info_output_matches_golden", [("info.out", "a\nb\n")], root.Path);

        var bytes = File.ReadAllBytes(Path.Combine(root.Path, "info.out"));
        Assert.Equal(new byte[] { 0x61, 0x0A, 0x62, 0x0A }, bytes);
        scope.Dispose(); // every requested test captured: no throw
    }

    [Fact]
    public void Commit_replaces_existing_files_of_the_group()
    {
        // The committed snapshots already exist; a Move that did not overwrite
        // would throw (or silently keep the old bytes). Both files must carry
        // the new content afterwards.
        using var root = new TempDir();
        root.Write("verify.out", "OLD\n");
        root.Write("verify-dau.md", "OLD\n");
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Verify_output_matches_golden"));

        GoldenUpdate.Commit(scope, "Verify_output_matches_golden", [("verify.out", "NEW\n"), ("verify-dau.md", "NEW2\n")], root.Path);

        Assert.Equal("NEW\n", File.ReadAllText(Path.Combine(root.Path, "verify.out")));
        Assert.Equal("NEW2\n", File.ReadAllText(Path.Combine(root.Path, "verify-dau.md")));
    }

    [Fact]
    public void Commit_refuses_an_artefact_set_that_does_not_match_the_group()
    {
        using var root = new TempDir();
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Verify_output_matches_golden"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoldenUpdate.Commit(scope, "Verify_output_matches_golden", [("verify.out", "x\n")], root.Path));

        Assert.Contains("verify-dau.md", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nothing written", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root.Path, "verify.out")));
    }

    [Fact]
    public void Commit_reports_nothing_written_when_the_staging_directory_cannot_be_created()
    {
        // The staging directory is the first thing the commit creates (the
        // real case: TMP pointing at a file, a full or read-only temp volume).
        // The failure is injected rather than provoked through TMP/TEMP/TMPDIR:
        // those are process-wide, and every other test class -- xunit runs
        // them in parallel -- creates its TempDir from them.
        using var root = new TempDir();
        root.Write("verify.out", "OLD\n");
        root.Write("verify-dau.md", "OLD2\n");
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Verify_output_matches_golden"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoldenUpdate.Commit(
                scope,
                "Verify_output_matches_golden",
                [("verify.out", "NEW\n"), ("verify-dau.md", "NEW2\n")],
                root.Path,
                () => throw new IOException("Cannot create the staging directory")));

        Assert.Contains("Cannot create the staging directory", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nothing written", ex.Message, StringComparison.Ordinal);
        Assert.Equal("OLD\n", File.ReadAllText(Path.Combine(root.Path, "verify.out")));
        Assert.Equal("OLD2\n", File.ReadAllText(Path.Combine(root.Path, "verify-dau.md")));
    }

    [Fact]
    public void Commit_restores_the_group_when_a_later_move_fails()
    {
        // The first destination is a file, the second is a DIRECTORY, so the
        // second File.Move throws. The first file must be back to its
        // committed bytes, and the message must say the group was restored.
        using var root = new TempDir();
        root.Write("verify.out", "OLD\n");
        Directory.CreateDirectory(Path.Combine(root.Path, "verify-dau.md"));
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Verify_output_matches_golden"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoldenUpdate.Commit(scope, "Verify_output_matches_golden", [("verify.out", "NEW\n"), ("verify-dau.md", "NEW2\n")], root.Path));

        Assert.Contains("restored", ex.Message, StringComparison.Ordinal);
        Assert.Equal("OLD\n", File.ReadAllText(Path.Combine(root.Path, "verify.out")));
        var teardown = Assert.Throws<InvalidOperationException>(scope.Dispose); // not marked captured
        Assert.Contains("Verify_output_matches_golden", teardown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_teardown_fails_when_a_requested_test_never_captured()
    {
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Info_output_matches_golden,Graph_dot_matches_golden"));
        scope.MarkCaptured("Info_output_matches_golden");

        var ex = Assert.Throws<InvalidOperationException>(scope.Dispose);
        Assert.Contains("Graph_dot_matches_golden", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--filter", ex.Message, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~GoldenUpdateTests" --no-restore`
Expected: build error — `GoldenUpdate` does not exist.

> **Superseded in execution (commit 21db7dd and the final fix wave):** the restore loop below covers only the destinations already replaced. The task review found that the destination of the move that FAILS was left unrestored; the shipped code restores every planned destination, keeps the staging directory when a restore fails, and has more tests. The file in the repository is authoritative, not this listing.

- [ ] **Step 3: Write `GoldenUpdate.cs`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Tests;

/// <summary>
/// The scoped update mode for the snapshots under <c>tests/fixtures/golden/</c>
/// (design §2, §5.2). Set <see cref="Variable"/> to a comma-separated list of
/// <see cref="GoldenParityTests"/> method names; each named test then REWRITES
/// its group of snapshot files from the value it would have compared, and
/// fails on purpose (<see cref="RefusesToAssert"/>): in update mode the
/// expected side was just produced by the harness that produced the actual
/// side, so a comparison would be a tautology, and a tautology reported green
/// is how a stale variable in a shell disarms a snapshot without anyone
/// noticing. Two runs, never one: rewrite, read the diff, re-run without the
/// variable.
///
/// <para>The scope is validated as a whole before the first test runs
/// (<see cref="Scope"/> is the collection fixture), so a typo rejects the list
/// instead of rewriting some groups and skipping one. At teardown the fixture
/// checks that every named test actually captured -- a test excluded by
/// <c>dotnet test --filter</c> would otherwise leave a red run that rewrote
/// less than it announced. Known limit: a filter that excludes the whole
/// collection never constructs this fixture, so the variable is then without
/// effect and the run green; nothing is written and no diff appears. The mode
/// captures; it never authorises: a semantic diff, or any edit to an input
/// bundle, still takes explicit arbitration and a dated entry in
/// <c>tests/fixtures/README.md</c>.</para>
/// </summary>
public static class GoldenUpdate
{
    /// <summary>The environment variable naming the tests to rewrite.</summary>
    public const string Variable = "OKF_UPDATE_GOLDEN";

    /// <summary>The xunit collection every snapshot test belongs to, so they share one <see cref="Scope"/>.</summary>
    public const string CollectionName = "GoldenParity";

    /// <summary>
    /// Test method name → the snapshot files (relative to <c>tests/fixtures/golden/</c>,
    /// '/' separators) that test captures as one group. A group is written
    /// whole or not at all. <c>GoldenUpdateTests</c> checks this table names
    /// every file on disk exactly once and every <c>[Fact]</c> in
    /// <see cref="GoldenParityTests"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["Validate_output_and_exitcode_match_golden"] = ["validate.out"],
        ["Validate_v02_fixture_matches_golden"] = ["validate-v02.out"],
        ["Validate_computation_fixture_matches_golden"] = ["validate-computation.out"],
        ["Validate_reserved_fixture_matches_golden"] = ["validate-reserved.out"],
        ["Info_output_matches_golden"] = ["info.out"],
        ["Audit_report_matches_golden"] = ["audit-v02.out"],
        ["Audit_json_matches_golden"] = ["audit-v02.json"],
        ["Graph_dot_matches_golden"] = ["graph.dot"],
        ["Fmt_output_matches_golden"] = ["fmt/users.md"],
        ["Index_generation_matches_golden"] = ["index-input/index.md", "index-input/datasets/index.md", "index-input/tables/index.md"],
        ["Verify_output_matches_golden"] = ["verify.out", "verify-dau.md"],
    };

    private static string DefaultGoldenRoot => Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "golden");

    /// <summary>
    /// Parses the variable's value into the set of test names to rewrite.
    /// Unset or blank means "not in update mode". Exact names only, case
    /// sensitive, no wildcard; an unknown name, an empty element or a
    /// duplicate rejects the WHOLE list, with every problem listed, before
    /// anything is written.
    /// </summary>
    public static IReadOnlySet<string> ParseScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var problems = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in raw.Split(','))
        {
            var name = element.Trim();
            if (name.Length == 0)
            {
                problems.Add("empty element");
            }
            else if (!Groups.ContainsKey(name))
            {
                problems.Add("unknown: " + name);
            }
            else if (!names.Add(name))
            {
                problems.Add("duplicate: " + name);
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{Variable} rejected, nothing was rewritten. Exact GoldenParityTests method names, comma-separated, no wildcard. Problems: "
                + string.Join("; ", problems) + ". Known names: " + string.Join(", ", Groups.Keys.Order(StringComparer.Ordinal)));
        }

        return names;
    }

    /// <summary>
    /// Rewrites one test's snapshot group from the values that test would have
    /// compared. Guards first: the artefact set must equal the group exactly,
    /// or nothing is written. Then every new file is staged (UTF-8 without
    /// BOM, bytes as given -- LF stays LF), every existing destination is
    /// backed up, and the staged files are moved into place one by one. If a
    /// move fails, every destination already replaced is put back from its
    /// backup, so the committed group is whole again -- and if THAT fails, the
    /// message says which files are not restored rather than claiming they are.
    /// </summary>
    /// <param name="scope">The run's scope; the test is marked captured on success.</param>
    /// <param name="test">The <see cref="GoldenParityTests"/> method name.</param>
    /// <param name="artefacts">Each snapshot file of the group with the exact text to write.</param>
    /// <param name="goldenRoot">Where the snapshots live; tests pass a temporary directory.</param>
    public static void Commit(Scope scope, string test, IReadOnlyList<(string Relative, string Content)> artefacts, string? goldenRoot = null) =>
        Commit(scope, test, artefacts, goldenRoot, () => Directory.CreateTempSubdirectory("okf-golden-"));

    /// <summary>
    /// The public overload's implementation, with the staging directory's
    /// creation injected, so a test can make that first step fail without
    /// touching the process-wide temp variables -- which every other test
    /// class running in parallel reads.
    /// </summary>
    internal static void Commit(Scope scope, string test, IReadOnlyList<(string Relative, string Content)> artefacts, string? goldenRoot, Func<DirectoryInfo> createStaging)
    {
        var root = goldenRoot ?? DefaultGoldenRoot;
        var expected = Groups[test].Order(StringComparer.Ordinal).ToList();
        var given = artefacts.Select(a => a.Relative).Order(StringComparer.Ordinal).ToList();
        if (!expected.SequenceEqual(given, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"nothing written: the group of {test} is [{string.Join(", ", expected)}] but the test offered [{string.Join(", ", given)}].");
        }

        DirectoryInfo? staging = null;
        try
        {
            // Phase 1, before writing: create the staging directory, stage
            // every new file and back up every existing destination. A failure
            // anywhere here -- the temp directory itself included -- has
            // replaced nothing, and says so.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var planned = new List<(string Staged, string Destination, string? Backup)>();
            try
            {
                staging = createStaging();
                foreach (var (relative, content) in artefacts)
                {
                    var native = relative.Replace('/', Path.DirectorySeparatorChar);
                    var staged = Path.Combine(staging.FullName, "new", native);
                    Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
                    File.WriteAllBytes(staged, utf8.GetBytes(content));

                    var destination = Path.Combine(root, native);
                    string? backup = null;
                    if (File.Exists(destination))
                    {
                        backup = Path.Combine(staging.FullName, "old", native);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(destination, backup);
                    }

                    planned.Add((staged, destination, backup));
                }
            }
            catch (Exception stagingFailure) when (stagingFailure is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"{stagingFailure.Message}; nothing written (staging failed before any file was replaced).", stagingFailure);
            }

            // Phase 2, while replacing: move the staged files into place. A
            // failure here puts back what was already replaced.
            var replaced = new List<(string Destination, string? Backup)>();
            try
            {
                foreach (var (staged, destination, backup) in planned)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(staged, destination, overwrite: true);
                    replaced.Add((destination, backup));
                }
            }
            catch (Exception moveFailure) when (moveFailure is IOException or UnauthorizedAccessException)
            {
                var notRestored = new List<string>();
                foreach (var (destination, backup) in replaced)
                {
                    try
                    {
                        if (backup is null)
                        {
                            File.Delete(destination);
                        }
                        else
                        {
                            File.Copy(backup, destination, overwrite: true);
                        }
                    }
                    catch (Exception restoreFailure) when (restoreFailure is IOException or UnauthorizedAccessException)
                    {
                        notRestored.Add(destination + " (" + restoreFailure.Message + ")");
                    }
                }

                var state = notRestored.Count == 0
                    ? "the group was restored to its committed state"
                    : "RESTORE FAILED, check `git status tests/fixtures/golden` -- not restored: " + string.Join("; ", notRestored);
                throw new InvalidOperationException($"{moveFailure.Message}; {state}.", moveFailure);
            }
        }
        finally
        {
            try
            {
                staging?.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the staging directory.
            }
        }

        scope.MarkCaptured(test);
    }

    /// <summary>The deliberate failure a test raises after rewriting its group.</summary>
    public static string RefusesToAssert(string test) =>
        $"{Variable}: {test} REWROTE its snapshot group [{string.Join(", ", Groups[test])}] and refuses to assert -- the expected side"
        + " was just produced by the same harness as the actual side, so this run proves nothing. Review"
        + " `git diff tests/fixtures/golden`, then re-run WITHOUT the variable to actually check it. If the"
        + " diff is semantic (a verdict, a count, an exit code, §8/§9 structure, a projected value, a written"
        + " document), it needs explicit arbitration and a dated entry in tests/fixtures/README.md.";

    /// <summary>
    /// The collection fixture shared by every snapshot test: parses the scope
    /// once before the first test, records which tests captured, and at
    /// teardown fails if a requested test never did. xunit constructs it
    /// through its ONE public constructor; the internal one is for tests of
    /// the mode itself.
    /// </summary>
    public sealed class Scope : IDisposable
    {
        private readonly HashSet<string> _captured = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        /// <summary>Reads the scope from <see cref="Variable"/>; xunit calls this before the first test of the collection.</summary>
        public Scope()
            : this(ParseScope(Environment.GetEnvironmentVariable(Variable)))
        {
        }

        internal Scope(IReadOnlySet<string> requested)
        {
            Requested = requested;
        }

        /// <summary>The tests to rewrite; empty when not in update mode.</summary>
        public IReadOnlySet<string> Requested { get; }

        /// <summary>Whether <paramref name="test"/> is in update mode this run.</summary>
        public bool Includes(string test) => Requested.Contains(test);

        /// <summary>Records that <paramref name="test"/> rewrote its group.</summary>
        public void MarkCaptured(string test)
        {
            lock (_gate)
            {
                _captured.Add(test);
            }
        }

        /// <summary>
        /// Fails the collection if a requested test never captured -- excluded
        /// by <c>--filter</c>, or failed on the capture path -- so a red run
        /// cannot have rewritten less than it announced.
        /// </summary>
        public void Dispose()
        {
            List<string> missing;
            lock (_gate)
            {
                missing = Requested.Except(_captured, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            }

            if (missing.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{Variable} named tests that did not capture: {string.Join(", ", missing)}. Either dotnet test --filter excluded"
                    + " them, or they failed on the capture path. Nothing was verified for them; the other groups may have been rewritten.");
            }
        }
    }
}

/// <summary>Binds the <see cref="GoldenUpdate.Scope"/> fixture to the snapshot tests' collection.</summary>
[CollectionDefinition(GoldenUpdate.CollectionName)]
public sealed class GoldenParityCollection : ICollectionFixture<GoldenUpdate.Scope>
{
}
```

- [ ] **Step 4: Run the unit tests**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~GoldenUpdateTests" --no-restore`
Expected: 11 passed. (`Groups_name_every_snapshot_file_exactly_once_and_every_golden_test` passes only because Task 8 deleted the nine files.)

- [ ] **Step 5: Wire `GoldenParityTests` into the collection and route every test through `AssertGolden`**

Replace `public class GoldenParityTests` with:

```csharp
[Collection(GoldenUpdate.CollectionName)]
public class GoldenParityTests(GoldenUpdate.Scope scope)
```

In the class docstring (Task 8 left it as is; Task 10 rewrites it), nothing to change yet. Change `Golden` to accept '/'-separated relatives:

```csharp
    private static string Golden(string rel) => File.ReadAllText(Path.Combine(GoldenRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
```

Add this helper after `WithRepoRootAsCwd`:

```csharp
    /// <summary>
    /// The one comparison path for every snapshot test, and where the capture
    /// path STARTS: the CLI (or the index generator) has already run by the
    /// time this is called, and an exception there is an ordinary failure in
    /// either mode, with nothing written. From here on, in update mode, every
    /// error surfaces as one "capture failure" whose message names its phase:
    /// the exit-code guard and the artefact reader (with its own cardinality
    /// guards) fail "before writing: nothing written"; the commit's own
    /// messages say whether staging failed (nothing written) or a move failed
    /// (the group restored, or what was not). All distinct from the deliberate
    /// <see cref="GoldenUpdate.RefusesToAssert"/> raised after a successful
    /// rewrite. Outside update mode every artefact is compared to its snapshot.
    /// </summary>
    /// <param name="test">The calling test's name (<c>nameof</c>).</param>
    /// <param name="code">The exit code observed.</param>
    /// <param name="expectedCode">The exit code the test expects.</param>
    /// <param name="artefacts">Reads each snapshot file of the test's group with the text to compare (or write), already normalised where the test normalises; may assert guards first.</param>
    private void AssertGolden(string test, int code, int expectedCode, Func<IReadOnlyList<(string Relative, string Actual)>> artefacts)
    {
        if (scope.Includes(test))
        {
            var phase = "before writing: nothing written";
            try
            {
                if (code != expectedCode)
                {
                    throw new InvalidOperationException($"exit code {code}, expected {expectedCode}");
                }

                var captured = artefacts().Select(a => (a.Relative, a.Actual)).ToList();
                phase = "in the commit, which reports its own state";
                GoldenUpdate.Commit(scope, test, captured);
            }
            catch (Exception e)
            {
                Assert.Fail($"capture failure for {test} ({phase}): {e.Message}");
            }

            Assert.Fail(GoldenUpdate.RefusesToAssert(test));
        }

        Assert.Equal(expectedCode, code);
        foreach (var (relative, actual) in artefacts())
        {
            Assert.Equal(Golden(relative), actual);
        }
    }
```

Then rewrite each test's assertions. The complete new bodies:

```csharp
    [Fact]
    public void Validate_output_and_exitcode_match_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/appendix_a", "--as-of", PinnedAsOf));

        // The snapshot was captured on Linux, where paths display with '/'.
        // Per-file diagnostic paths combine the literal root with
        // Path.Combine, which emits the OS-native separator, so on Windows
        // the tail of each path comes out with '\'. A platform display
        // artifact, not a semantic difference: normalised in the OUTPUT before
        // comparing -- and, in update mode, written normalised, so the
        // snapshot stays the same on every platform.
        AssertGolden(nameof(Validate_output_and_exitcode_match_golden), r.Code, 0,
            () => [("validate.out", r.Out.Replace('\\', '/'))]);
    }

    [Fact]
    public void Validate_v02_fixture_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02", "--as-of", PinnedAsOf));
        AssertGolden(nameof(Validate_v02_fixture_matches_golden), r.Code, 0,
            () => [("validate-v02.out", r.Out.Replace('\\', '/'))]);
    }

    [Fact]
    public void Validate_computation_fixture_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02_computation", "--as-of", PinnedAsOf));
        AssertGolden(nameof(Validate_computation_fixture_matches_golden), r.Code, 0,
            () => [("validate-computation.out", r.Out.Replace('\\', '/'))]);
    }

    [Fact]
    public void Validate_reserved_fixture_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02_reserved", "--as-of", PinnedAsOf));
        // §11 condition 3 fails: the bundle is non-conformant, and this CLI's
        // contract maps that verdict to exit code 1 -- the spec defines the
        // verdict, the integer is ours.
        AssertGolden(nameof(Validate_reserved_fixture_matches_golden), r.Code, 1,
            () => [("validate-reserved.out", r.Out.Replace('\\', '/'))]);
    }

    [Fact]
    public void Info_output_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("info", "tests/fixtures/appendix_a"));
        AssertGolden(nameof(Info_output_matches_golden), r.Code, 0, () => [("info.out", r.Out)]);
    }

    [Fact]
    public void Audit_report_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("audit", "tests/fixtures/okf_v02", "--as-of", "2099-06-01"));
        // Every path in this output is a concept id, always '/'-normalised by
        // ConceptId.FromPath, so the comparison is as read.
        AssertGolden(nameof(Audit_report_matches_golden), r.Code, 0, () => [("audit-v02.out", r.Out)]);
    }

    [Fact]
    public void Audit_json_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("audit", "tests/fixtures/okf_v02", "--as-of", "2099-06-01", "--json"));
        // Only the findings' `path` carries a native separator, and the
        // serializer escapes each backslash as the two-character sequence `\\`
        // in the JSON text; a single-char Replace would turn that pair into
        // "//", so the search pattern is the escaped sequence.
        AssertGolden(nameof(Audit_json_matches_golden), r.Code, 0, () => [("audit-v02.json", r.Out.Replace("\\\\", "/"))]);
    }

    [Fact]
    public void Graph_dot_matches_golden()
    {
        var r = Run("graph", BundlePath, "--dot");
        AssertGolden(nameof(Graph_dot_matches_golden), r.Code, 0, () => [("graph.dot", r.Out)]);
    }

    [Fact]
    public void Fmt_output_matches_golden()
    {
        var r = Run("fmt", Path.Combine(BundlePath, "tables", "users.md"));
        AssertGolden(nameof(Fmt_output_matches_golden), r.Code, 0, () => [("fmt/users.md", r.Out)]);
    }

    [Fact]
    public void Index_generation_matches_golden()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);

        var written = IndexGenerator.RegenerateIndexes(tmp.Path);

        AssertGolden(nameof(Index_generation_matches_golden), 0, 0, () =>
        {
            // Guards, inside the capture path so a failure is a capture
            // failure in update mode: three files written, three on disk, and
            // exactly the 3 generated index.md files plus the 5 source
            // documents copied in from appendix_a -- catches a file created in
            // excess or a net deletion; not a modified original, nor a
            // delete-and-create.
            Assert.Equal(3, written.Count);
            Assert.Equal(3, Directory.GetFiles(tmp.Path, "index.md", SearchOption.AllDirectories).Length);
            Assert.Equal(8, Directory.GetFiles(tmp.Path, "*", SearchOption.AllDirectories).Length);

            return
            [
                ("index-input/index.md", File.ReadAllText(Path.Combine(tmp.Path, "index.md"))),
                ("index-input/datasets/index.md", File.ReadAllText(Path.Combine(tmp.Path, "datasets", "index.md"))),
                ("index-input/tables/index.md", File.ReadAllText(Path.Combine(tmp.Path, "tables", "index.md"))),
            ];
        });
    }

    [Fact]
    public void Verify_output_matches_golden()
    {
        using var tmp = new TempDir();
        CopyDirectory(Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02"), tmp.Path);

        var r = Run("verify", tmp.Path, "metrics/dau", "metrics/legacy", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z");

        // stdout alone would stay green if the verb printed the right line and
        // wrote the wrong stamp, touched `generated`, or mangled the document.
        // The written file is the artefact that matters, so it is in the group.
        AssertGolden(nameof(Verify_output_matches_golden), r.Code, 0, () =>
        [
            ("verify.out", r.Out),
            ("verify-dau.md", File.ReadAllText(Path.Combine(tmp.Path, "metrics", "dau.md"))),
        ]);
    }
```

Keep `Run`, `WithRepoRootAsCwd`, `CopyDirectory`, `PinnedAsOf`, `BundlePath`, `GoldenRoot` as they are. The `Index_generation_matches_golden` guards that Task 8 placed before the comparison now live inside the reader; delete the earlier copies.

- [ ] **Step 6: Run the snapshot and update-mode tests without the variable**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~GoldenParityTests|FullyQualifiedName~GoldenUpdateTests" --no-restore`
Expected: 22 passed.

- [ ] **Step 7: Format, commit**

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/GoldenUpdate.cs tests/OKF4net.Tests/GoldenUpdateTests.cs tests/OKF4net.Tests/GoldenParityTests.cs
git commit -m "test(golden): a scoped update mode that rewrites named groups atomically, then refuses to assert"
```

---

### Task 10: Rewrite the doctrine everywhere it lives

Spec §1 (README, CLAUDE.md, the producer's three texts, the site, the two code comments), §5.6 (CHANGELOG), §5.8 (README outline), plus every active text the plan review found: `CLAUDE.md:29` and `CONTRIBUTING.md:54` ("mirrors the reference implementation"), the "byte-exact" / "byte-for-byte" wording at `README.md:21`, `README.md:101`, `CLAUDE.md:55`, `CONTRIBUTING.md:36`, `web/src/pages/docs/Spec.tsx:186`, and three outreach drafts that state the abrogated rule. One task, one commit: the texts contradict each other if split. The update mode exists (Task 9), so every text can describe it in the present tense.

**Files:**
- Modify: `tests/fixtures/README.md` (rewrite the top; keep the revision log verbatim)
- Modify: `CLAUDE.md:28-29`, `:55`, `:76`
- Modify: `CONTRIBUTING.md:34-41`, `:52-55`
- Modify: `README.md:21-22`, `:100-101`
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs:6-27` (class docstring)
- Modify: `src/OKF4net.Cli/OkfCli.cs:936-939` (`WriteGraphDot` comment)
- Modify: `producers/tests/OkfProducer.Tests/fixtures/README.md:17-27`, `.gitattributes:17`, `producers/tests/OkfProducer.Tests/Generation/CheckTests.cs:12-18`
- Modify: `web/src/pages/Contributing.tsx:72-84`, `web/src/pages/docs/Spec.tsx:186-187`
- Modify: `docs/outreach/issues/add-fmt-idempotency-golden-test.md`, `add-quickstart-example-bundle.md:5`, `document-cli-help-version-aliases.md:12`
- Modify: `CHANGELOG.md` under `## [Unreleased]` → `### Changed` (line 440)

- [ ] **Step 1: Rewrite the top of `tests/fixtures/README.md`**

Replace everything from the start of the file up to (not including) the heading `## v0.1 → v0.2 bump (2026-07-28)` with the text below. Everything from that heading to the end — including the entry Task 8 appended — is kept verbatim under the `## Revision log` heading that closes the new text.

````markdown
# Test fixtures — what this directory is, and what has authority

This directory holds the input bundles the test suite exercises and, under
`golden/`, **snapshots of this project's own CLI output**. The snapshots are
regenerable, under conditions stated below. They are not a reference
implementation's output, and they carry no conformance authority of their own.

**The OKF specification is the only conformance authority**: `docs/spec/SPEC.md`,
vendored at a fixed version. What the spec requires is verified by tests that
cite a `§` (`ValidateTests`, `IndexTests`, `AttestedComputationTests`, …).
What the four machine outputs (`validate --json`, `info --json`,
`audit --json`, `graph --dot`) project is verified by the full-projection
tests in `MachineOutputTests`, against hand-derived expectations. What the
snapshots here verify is that the CLI's **rendering** has not changed — no
more, and no less.

Design and rationale: `docs/superpowers/specs/2026-09-22-golden-fixtures-authority-design.md`.

## Layout

- `appendix_a/` — the example bundle: `datasets/sales.md`, `tables/orders.md`,
  `tables/customers.md`, plus two additions to exercise more of the CLI:
  - `log.md` — a root-level reserved log file with one valid ISO-8601 dated
    entry, so `info`/`index` see a non-empty log and `validate` reports no
    log-related warnings.
  - `tables/users.md` — a deliberately **non-strict** concept document: it
    has `type` and `title` but is missing `description` and `timestamp`, so
    `validate` emits the "missing recommended frontmatter field" warnings
    (§11 soft guidance — the bundle stays conformant, exit code 0).
- `okf_v02/`, `okf_v02_computation/`, `okf_v02_reserved/` — hand-authored
  v0.2 input bundles; see the revision log below for what each isolates.
- `golden/` — one group of snapshot files per test in `GoldenParityTests`.
  Exit codes are asserted in the tests, not stored as files. The `validate`
  invocations run from the repository root with the relative path shown,
  because the output embeds the path as given.

| Test | Snapshot files | Invocation | Guarded by the snapshot alone (presentation) | Also pinned directly (semantic) |
|---|---|---|---|---|
| `Validate_output_and_exitcode_match_golden` | `validate.out` | `okf validate tests/fixtures/appendix_a --as-of 2026-09-25` | line wording and order | the verdict, every diagnostic, the counts (`MachineOutputTests`, `ValidateTests`), exit code 0 |
| `Validate_v02_fixture_matches_golden` | `validate-v02.out` | `okf validate tests/fixtures/okf_v02 --as-of 2026-09-25` | line wording | the diagnostics (`ValidateTests`), exit code 0 |
| `Validate_computation_fixture_matches_golden` | `validate-computation.out` | `okf validate tests/fixtures/okf_v02_computation --as-of 2026-09-25` | line wording | the §10/§6.2 diagnostics (`AttestedComputationTests`, `ValidateTests`), exit code 0 |
| `Validate_reserved_fixture_matches_golden` | `validate-reserved.out` | `okf validate tests/fixtures/okf_v02_reserved --as-of 2026-09-25` | line wording | the four §11 errors and the non-conformant verdict (`MachineOutputTests`), exit code 1 |
| `Info_output_matches_golden` | `info.out` | `okf info tests/fixtures/appendix_a` | column alignment, the `types:` block | every count (`MachineOutputTests.Info_json_*`), both numbers of the `links:` line |
| `Audit_report_matches_golden` | `audit-v02.out` | `okf audit tests/fixtures/okf_v02 --as-of 2099-06-01` | column layout | the selection, tiers, statuses, staleness (`MachineOutputTests.Audit_json_*`, `AuditTests`) |
| `Audit_json_matches_golden` | `audit-v02.json` | `okf audit tests/fixtures/okf_v02 --as-of 2099-06-01 --json` | property order | every value (`MachineOutputTests.Audit_json_projects_a_stale_finding_completely`) |
| `Graph_dot_matches_golden` | `graph.dot` | `okf graph <appendix_a> --dot` | edge order | the grammar — header line, the `rankdir` line, two-space edge indentation, closing brace — the edge set and its size, determinism (`MachineOutputTests.Graph_dot_*`) |
| `Fmt_output_matches_golden` | `fmt/users.md` | `okf fmt <appendix_a>/tables/users.md` | nothing beyond the envelope | the envelope, idempotence, the stdout branch (`DocumentTests`, `CliTests`) |
| `Index_generation_matches_golden` | `index-input/index.md`, `index-input/datasets/index.md`, `index-input/tables/index.md` | `IndexGenerator.RegenerateIndexes` on a copy of `appendix_a` | the synthesizer wording | §8 structure and no frontmatter, `# Other`, no description suffix, self-listing (`IndexTests`) |
| `Verify_output_matches_golden` | `verify.out`, `verify-dau.md` | `okf verify <copy of okf_v02> metrics/dau metrics/legacy --by human:ada --at 2026-08-28T09:14:00Z` | the two stdout lines' wording | the written `verified` block (§5.2) and that nothing else moved (`RecordVerificationTests`) |

## Two regimes for a change to a snapshot

A snapshot changes when the CLI's output changes on purpose. **Which procedure
applies depends on what the diff changes, not on which file it lands in.** A
diff is **presentation** only if it preserves all five of:

1. the facts exposed, their values and their associations (a count stays on
   the same type, a diagnostic on the same severity);
2. the elements present, absent, and their multiplicity;
3. link targets and relations;
4. the syntactic validity and interpreted structure of the format (a DOT
   document stays parseable; an `index.md` stays sections and lists in the
   sense of §8);
5. written effects and exit codes.

A diff that changes any of these is **semantic**. A mixed diff is semantic.
Doubt resolves to semantic.

- **Presentation diff** — regenerate with the update mode (below), read the
  diff, re-run without the variable. Nothing else.
- **Semantic diff, and every edit to an input bundle** — explicit user
  arbitration first, then a dated entry in the revision log below saying what
  changed and why, then the same two runs. The update mode is a capture tool;
  having the command is not permission to use it on these.

Examples, so the rule is applied the same way twice:

| Change | Regime | Why |
|---|---|---|
| Realign the columns of `okf info` | presentation | same facts, same values |
| Swap two DOT edge lines without changing the edges | presentation | same edge set (order is not a contract; determinism is, and `MachineOutputTests` pins it) |
| Reorder the properties of a JSON object | presentation | same interpreted object |
| `5 internal (0 broken)` → `5 internal` | **semantic** | a fact disappeared (criterion 1) |
| Indent a whole `index.md` by four spaces | **semantic** | sections become a code block (criterion 4) |
| `[Users](users.md)` → `[Comptes](users.md)` in an index | **semantic** | the visible label is a fact exposed to the reader (criterion 1) |

## Regenerating a snapshot

The update mode is **scoped**: it rewrites only the tests you name, and refuses
the whole list before writing anything if a name is unknown, empty or
duplicated. Name the tests (exact `GoldenParityTests` method names,
comma-separated, no wildcard) in `OKF_UPDATE_GOLDEN`, and pass the same names
to `--filter` so the run executes them:

```sh
OKF_UPDATE_GOLDEN=Info_output_matches_golden,Graph_dot_matches_golden \
  dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj \
  --filter "FullyQualifiedName~GoldenParityTests.Info_output_matches_golden|FullyQualifiedName~GoldenParityTests.Graph_dot_matches_golden"
```

(PowerShell: `$env:OKF_UPDATE_GOLDEN = "Info_output_matches_golden,Graph_dot_matches_golden"`
first, and `Remove-Item Env:OKF_UPDATE_GOLDEN` afterwards.)

**That command exits RED, by design, and a green run would be the bug.** Each
named test rewrites its group of files — a group is written whole, and put back
whole if a file cannot be replaced — from the value it would have compared, and
then *refuses to assert*: the expected side was just produced by the same
harness as the actual side, so the comparison would be a tautology, and a
tautology reported green is exactly how a stale variable in someone's shell
disarms a snapshot without anyone noticing. Two failures are possible and say
which they are: **capture failure** (a guard such as the exit code did not
hold, or a file could not be written — the message says whether the group was
left untouched or restored) and **refuses to assert** (the group was rewritten;
read the diff). If a named test did not run because `--filter` excluded it, the
collection fails at teardown and says so.

One limit, stated rather than hidden: if the filter excludes **every**
`GoldenParityTests` test, the fixture that enforces the scope is never
constructed, the variable has no effect, and the run is green. Nothing is
written and there is no diff — the absence is the signal. Keep the filter and
the variable naming the same tests.

So the procedure is two runs, not one:

1. Run with the variable. Read `git diff tests/fixtures/golden`. Decide the
   regime (above): a presentation diff needs nothing more; a semantic diff
   needed arbitration **before** this step and needs its dated entry below.
2. Re-run **without** the variable to actually check, and commit the diff with
   the change that caused it.

## Comparison contract

Snapshots are committed with LF endings, UTF-8 without BOM, and compared after
being read as text: the four `validate` outputs and `audit-v02.json` are
compared **after** the OUTPUT's native path separators are normalised to `/`
(the snapshots were captured on Linux and embed paths); every other file is
compared as read. The update mode writes the normalised text, UTF-8 without
BOM, LF, nothing added. `.gitattributes` marks `tests/fixtures/** -text` and
`.editorconfig` excludes this directory, so no tool normalises them. Never let
an editor touch them: trailing whitespace, final newlines and line endings are
significant.

## Provenance (historical)

Four snapshot groups — `info.out`, `graph.dot`, `fmt/users.md` and the three
`index-input/*.md` (six files) — still hold the bytes first captured on
2026-07-21 from the Rust `okf` crate that then lived in this repository (source
at commit `d20343c`), built in Docker:

```
docker image: rust:1  (pulled digest sha256:9a2cd304a852f05d3352f75bc2775242371c0169a72dbb40d5d881379d571989)
rustc 1.97.1 (8bab26f4f 2026-07-14)
cargo 1.97.1 (c980f4866 2026-06-30)
```

Build: `cargo build --release` with `CARGO_TARGET_DIR=/tmp/target` (kept
outside the mounted worktree so no build artifacts landed in git).

That origin is history, not authority: the project implements the published
spec, and treating a deleted reimplementation as its norm was an authority that
existed and was abandoned by decision on 2026-09-30. These four groups follow
the same two regimes as every other snapshot.

## Revision log
````

- [ ] **Step 2: Rewrite the two hard-rule bullets and two sentences in `CLAUDE.md`**

Replace the whole bullet at line 28 (starting `- **Never touch \`tests/fixtures/\` to make a failing test pass.**`) with:

```markdown
- **Never regenerate a snapshot without reading the diff, and never to make a failing test pass.** `tests/fixtures/golden/` holds snapshots of *our own* CLI output — regenerable, not a reference implementation's bytes — and `docs/spec/SPEC.md` is the only conformance authority. Whether a change to a snapshot needs arbitration depends on **what the diff changes, not which file it lands in** (the five-criteria rule and its examples are in `tests/fixtures/README.md`): a presentation-only diff (spacing, wording, order nothing prescribes — same facts, elements, relations, valid structure, exit codes) is regenerated with the scoped update mode (`OKF_UPDATE_GOLDEN=<GoldenParityTests method names>`, which rewrites the named groups then deliberately fails: two runs, never one), while a **semantic** diff — a verdict, a count, an exit code, §8/§9 structure, a value a machine output projects, a document a verb writes — and **every edit to an input bundle** takes explicit user arbitration **and** a dated entry in that README first. The update mode captures; it never authorises. An unintended diff is a real failure to investigate on the C# side, never a snapshot to refresh. The machine outputs (`validate`/`info`/`audit --json`, `graph --dot`) are pinned independently by `MachineOutputTests` against hand-derived expectations; those tests are code, and change with the code they test.
```

At line 29, replace `intentional divergences from the reference implementation need a documented reason` with `intentional divergences from the spec's reading, or from the behaviour of the OKF reference implementation where it is informative, need a documented reason in `docs/spec-conformance/``.

At line 55, replace `` `GoldenParityTests` diffs CLI output byte-for-byte against `tests/fixtures/golden/` `` with `` `GoldenParityTests` compares CLI output as text (after path-separator normalisation where the output embeds paths) against the snapshots in `tests/fixtures/golden/`, regenerable under the regimes in that directory's README ``.

At line 76, replace `which stays byte-exact golden captures` with `which holds snapshots of our own CLI output under the regimes stated in its README`.

- [ ] **Step 3: Align `CONTRIBUTING.md`**

Replace the section `### Golden fixtures — do not reformat` (lines 34-41) with:

```markdown
### Snapshot fixtures — do not reformat, do not regenerate blindly

`tests/fixtures/golden/` holds **snapshots of this project's own CLI output**:
several tests compare generated output (index files, formatted documents, CLI
output) as text against them — after path-separator normalisation for the
outputs that embed paths. They are regenerable under two regimes stated in
`tests/fixtures/README.md`: a presentation-only diff goes through the scoped
update mode and a review of the diff; a semantic diff, or any edit to an input
bundle, needs explicit arbitration and a dated entry first. Conformance is
verified against the spec by tests that cite a §, not by these files. They are
protected by `.gitattributes` (`-text`) and excluded from `.editorconfig`
normalization. Never let an editor or formatter touch them — trailing
whitespace, final newlines, and line endings are all significant.
```

At lines 54-55, replace `Behaviour intentionally mirrors the OKF\nreference implementation; divergences need a documented reason.` with `The spec is the authority; where this implementation deliberately reads it differently, or departs from the OKF reference implementation's behaviour, the reason is recorded in `docs/spec-conformance/`.`

- [ ] **Step 4: Two sentences in `README.md`**

Lines 21-22: replace `including byte-exact golden CLI comparisons (see\n> [\`tests/fixtures/\`](tests/fixtures/README.md))` with `including snapshot comparisons of the CLI's output and full-projection\n> tests of its machine outputs (see [\`tests/fixtures/\`](tests/fixtures/README.md))`.

Line 101: replace `including byte-exact golden CLI comparisons.` with `including snapshot comparisons of the CLI's output.`

- [ ] **Step 5: Rewrite the `GoldenParityTests` class docstring**

Replace lines 6-27 with:

```csharp
/// <summary>
/// Snapshot tests: every output the CLI produces is compared against the
/// corresponding file under <c>tests/fixtures/golden/</c> -- read as text, and
/// for the <c>validate</c> outputs and <c>audit-v02.json</c> compared after the
/// OUTPUT's native path separators are normalised to '/'. The snapshots are
/// this project's own output, regenerable under the two regimes stated in
/// <c>tests/fixtures/README.md</c>: a presentation-only diff through the scoped
/// update mode (<see cref="GoldenUpdate"/>), a semantic diff or any edit to an
/// input bundle only after explicit arbitration and a dated README entry.
/// Conformance is not what these tests establish -- <c>docs/spec/SPEC.md</c>
/// is, through the tests that cite a section -- and the machine outputs are
/// pinned independently by <see cref="MachineOutputTests"/>. An unintended
/// diff here is a regression to investigate, never a snapshot to refresh.
/// </summary>
```

- [ ] **Step 6: Rewrite the `WriteGraphDot` comment in `OkfCli.cs:936-939`**

```csharp
    /// <summary>
    /// Renders the link graph as Graphviz DOT, broken links dashed and red.
    /// The grammar (header, one edge statement per link, closing brace), the
    /// edge set and determinism are pinned by <c>MachineOutputTests</c>; the
    /// exact bytes are snapshotted in <c>tests/fixtures/golden/graph.dot</c>
    /// under the regimes in <c>tests/fixtures/README.md</c>.
    /// </summary>
```

- [ ] **Step 7: Align the producer's three texts**

In `producers/tests/OkfProducer.Tests/fixtures/README.md`, replace the section from `## Read this first: the discipline here is the OPPOSITE of \`tests/fixtures/\`` through the paragraph ending `read the diff before you accept it.` with:

```markdown
## Read this first: the same capture mechanics as `tests/fixtures/`, without its arbitration layer

The repository's other golden directory, `tests/fixtures/golden/`, also holds **our own** output and
is also regenerable — since 2026-10-01, under the two regimes its README states (a presentation diff
is regenerated and reviewed; a semantic diff or an input edit needs explicit arbitration and a dated
entry first), with an update mode that must be given the names of the tests to rewrite.

**This golden has no protected subset**, so it keeps the simpler shape: it captures **our own**
output, it is regenerable by construction, and it **must** be regenerated whenever the generator
changes intentionally — then the diff is reviewed as part of that change. The refusal-to-assert on
update and the two-run procedure are shared with `tests/fixtures/`; the arbitration layer is not,
because nothing here carries a conformance verdict.

The corollary matters too: a diff you did **not** intend is a real failure. `--check` exists to make
an unintended one loud, so read the diff before you accept it.
```

In `.gitattributes`, replace `# Note the discipline there is the OPPOSITE of tests/fixtures/ -- see that directory's own README.` with:

```
# tests/fixtures/ is regenerable too, under its own README's regimes; the -text protection is shared.
```

In `producers/tests/OkfProducer.Tests/Generation/CheckTests.cs`, replace the `<para>` at lines 12-18 (`<para><b>The golden here follows the OPPOSITE discipline ...</para>`) with:

```csharp
/// <para><b>This golden captures our own output and is regenerable by construction</b>, like
/// <c>tests/fixtures/golden/</c> since 2026-10-01 -- with one difference: nothing here carries a
/// conformance verdict, so there is no arbitration layer, and the update switch rewrites the whole
/// golden rather than a named scope. It <i>must</i> be regenerated whenever the generator changes
/// intentionally, with the diff reviewed as part of that change. <c>fixtures/README.md</c> states it
/// in full; the regeneration switch is <see cref="UpdateGoldenVariable"/>, read by
/// <see cref="Check_passes_on_an_unchanged_bundle"/> below.</para>
```

- [ ] **Step 8: The site — two pages**

In `web/src/pages/Contributing.tsx`, replace lines 72-84 (the `<Warn title="GOLDEN FIXTURES — DO NOT REFORMAT">` block) with:

```tsx
          <Warn title="GOLDEN FIXTURES — DO NOT REFORMAT">
            <p>
              <code>tests/fixtures/golden/</code> holds <strong>snapshots of this project's own CLI output</strong>,
              compared as text (after path-separator normalisation for the outputs that embed paths). They are
              regenerable under two regimes stated in <code>tests/fixtures/README.md</code>: a presentation-only diff
              is regenerated through the scoped update mode and reviewed; a semantic diff, or any edit to an input
              bundle, needs explicit arbitration and a dated entry first. Conformance is verified against the spec by
              tests that cite a §, not by these files. They are protected by <code>.gitattributes</code> and excluded
              from <code>.editorconfig</code> normalization — never let an editor or formatter touch them: trailing
              whitespace, final newlines, and line endings are all significant.
            </p>
          </Warn>
```

In `web/src/pages/docs/Spec.tsx`, replace the two sentences at lines 186-187 (`The suite's byte-exact golden CLI comparisons mostly trace to this project's own former Rust\n            implementation, before its removal; the newer v0.2 and §10 behaviour predates any reference\n            implementation and is hand-verified against the spec text instead.`) with:

```tsx
            The suite pins the CLI's rendering with snapshots of this project's own output (regenerable under
            the regimes in <code>tests/fixtures/README.md</code>) and its machine outputs with full-projection
            tests against hand-derived expectations; the spec is the only conformance authority.
```

- [ ] **Step 9: The three outreach drafts**

`docs/outreach/README.md` presents `docs/outreach/issues/` as ready-to-publish drafts, so a draft stating the abrogated rule would publish it.

- `docs/outreach/issues/add-fmt-idempotency-golden-test.md`: the idempotence test it asks for now exists (`DocumentTests.Serialize_is_idempotent_on_a_canonical_document`, Task 7). Prepend, as the first line of the file: `> **Superseded (2026-10-01):** this test was added as \`DocumentTests.Serialize_is_idempotent_on_a_canonical_document\`; do not publish. Kept for the record.`
- `docs/outreach/issues/add-quickstart-example-bundle.md`, line 5: replace `(a byte-exact golden fixture — see "Never touch \`tests/fixtures/\`" in \`CONTRIBUTING.md\`)` with `(a test fixture — see "Snapshot fixtures" in \`CONTRIBUTING.md\`)`.
- `docs/outreach/issues/document-cli-help-version-aliases.md`, line 12: replace `This text is not part of any byte-exact golden fixture (\`tests/fixtures/golden/\` has no usage/help capture), so it's safe to edit.` with `This text has no snapshot under \`tests/fixtures/golden/\` (no usage/help capture), so editing it regenerates nothing.`

- [ ] **Step 10: Add the CHANGELOG entry**

Under `## [Unreleased]` → `### Changed` (line 440), insert as the first bullet:

```markdown
- **`tests/fixtures/golden/` is now a set of snapshots of this project's own CLI output, and
  `docs/spec/SPEC.md` is the only conformance authority.** The directory presented itself as
  byte-exact captures of a reference implementation and `CLAUDE.md` forbade touching it. That
  authority was real — the Rust crate those bytes came from was the norm of the July migration — and
  it is abandoned by decision: the crate was removed in July, and a project that implements a
  published spec answers to the spec. Conformance is verified by the tests that cite a section; the
  four machine outputs (`validate`/`info`/`audit --json`, `graph --dot`) are pinned by new
  full-projection tests against hand-derived expectations; and the snapshots pin rendering only.
  They are regenerable with a scoped update mode (`OKF_UPDATE_GOLDEN=<test names>`, which rewrites
  the named groups atomically then deliberately fails so a tautology is never reported green) under
  two regimes that depend on what the diff changes: presentation diffs are regenerated and reviewed;
  semantic diffs and any input-bundle edit need explicit arbitration and a dated README entry. Two
  deletions came with it: the five unread bundle copies under `golden/index-input/`, and the four
  one-byte `*.exitcode` files, whose values are assertions now. The four `validate` snapshot
  invocations are pinned with `--as-of` so they no longer depend on the machine clock.
  Design: `docs/superpowers/specs/2026-09-22-golden-fixtures-authority-design.md`.
```

- [ ] **Step 11: Grep for leftovers, build, run the suite, check the site compiles, format, commit**

Run from the repo root: `grep -rn -i "never touch \`tests/fixtures/\`\|byte-exact golden\|golden-locked\|never a reason to touch\|OPPOSITE of" --include=*.md --include=*.cs --include=*.tsx --include=.gitattributes . | grep -v "docs/superpowers\|docs/design\|docs/review-briefing\|node_modules\|/bin/\|/obj/\|graphify-out"`
Expected: no line outside these three, each kept on purpose: `tests/fixtures/README.md`'s revision log (historical entries quote the old rule and stay), `src/OKF4net.Agents/OkfBundleTools.cs` (its two "golden-locked" comments ask that the agent rendering not alter the CLI rendering — compatible with the design), and `docs/outreach/issues/add-fmt-idempotency-golden-test.md` (superseded by Step 9's banner, kept for the record, never to be published). Anything else is a leftover: fix it in this task.

Run: `dotnet build tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore` (doc-comment warnings are errors).
Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "Category!=ContainerIntegration"`
Expected: 0 failed.
Run: `cd web && npx tsc --noEmit && cd ..`
Expected: no output.

```bash
dotnet format OKF4net.sln --no-restore
git add tests/fixtures/README.md CLAUDE.md CONTRIBUTING.md README.md tests/OKF4net.Tests/GoldenParityTests.cs src/OKF4net.Cli/OkfCli.cs producers/tests/OkfProducer.Tests/fixtures/README.md .gitattributes producers/tests/OkfProducer.Tests/Generation/CheckTests.cs web/src/pages/Contributing.tsx web/src/pages/docs/Spec.tsx docs/outreach/issues/add-fmt-idempotency-golden-test.md docs/outreach/issues/add-quickstart-example-bundle.md docs/outreach/issues/document-cli-help-version-aliases.md CHANGELOG.md
git commit -m "docs: the spec is the conformance authority; tests/fixtures/golden/ are snapshots of our own output"
```

---

### Task 11: First real use of the update mode, and the end-to-end checks

Spec §5.7 step 6. Five manual verifications the suite cannot run on itself. Each must leave `git status --short tests/fixtures/golden` empty afterwards (the `.sln` stays modified; it is outside this check).

**Files:** none modified permanently.

- [ ] **Step 1: A no-op regeneration leaves no diff**

Run (bash; PowerShell equivalent in the README):
```bash
OKF_UPDATE_GOLDEN=Info_output_matches_golden dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "FullyQualifiedName~GoldenParityTests.Info_output_matches_golden"
```
Expected: 1 failed, with a message starting `OKF_UPDATE_GOLDEN: Info_output_matches_golden REWROTE its snapshot group [info.out] and refuses to assert`.
Then: `git status --short tests/fixtures/golden` → **no output** (the rewritten bytes equal the committed ones). If `info.out` shows as modified, inspect `git diff`: a CRLF or BOM difference means Task 9's `Commit` is wrong; stop and fix it there.

- [ ] **Step 2: A group with two artefacts and one with three**

```bash
OKF_UPDATE_GOLDEN=Verify_output_matches_golden,Index_generation_matches_golden dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "FullyQualifiedName~GoldenParityTests.Verify_output_matches_golden|FullyQualifiedName~GoldenParityTests.Index_generation_matches_golden"
```
Expected: 2 failed (both "refuses to assert"); `git status --short tests/fixtures/golden` → no output.

- [ ] **Step 3: A partial `--filter` is caught at teardown**

```bash
OKF_UPDATE_GOLDEN=Info_output_matches_golden,Graph_dot_matches_golden dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "FullyQualifiedName~GoldenParityTests.Info_output_matches_golden"
```
Expected: the `Info` test fails with "refuses to assert" **and** the run reports a test collection cleanup failure whose message names `Graph_dot_matches_golden` and `--filter`; `dotnet test` exits non-zero. `git status --short tests/fixtures/golden` → no output.

- [ ] **Step 4: A `--filter` that excludes the whole collection — the documented limit**

```bash
OKF_UPDATE_GOLDEN=Info_output_matches_golden dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "FullyQualifiedName~JsonShapeTests"
```
Expected: green, exit 0 — the fixture was never constructed. `git status --short tests/fixtures/golden` → no output. This is the limit the README and spec §5.2 state; record the observed outcome in the completion note so the statement is backed by a run.

- [ ] **Step 5: The typo case writes nothing**

```bash
OKF_UPDATE_GOLDEN=Info_output_matches_goldne dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "FullyQualifiedName~GoldenParityTests"
```
Expected: every snapshot test errors at fixture construction with `OKF_UPDATE_GOLDEN rejected, nothing was rewritten … unknown: Info_output_matches_goldne`; no test ran to a comparison. `git status --short tests/fixtures/golden` → no output.

- [ ] **Step 6: The full suite, once more, without the variable**

Make sure the variable is unset (`echo $OKF_UPDATE_GOLDEN` prints nothing; PowerShell `Remove-Item Env:OKF_UPDATE_GOLDEN`).
Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "Category!=ContainerIntegration"`
Expected: 0 failed. The pre-plan run on this machine was 2300 passed + 12 skipped = 2312. The plan deletes 6 `CliTests` methods and adds 38 (7 `JsonShapeTests`, 12 `MachineOutputTests`, 5 `IndexTests`, 2 `DocumentTests`, 1 `CliTests`, 11 `GoldenUpdateTests`), so expect **2344 total**. The skipped count is host-dependent (symlink privilege, case sensitivity, POSIX-only cases — not the container tests, which the filter removes from the selection entirely), so judge the **total**, not the split; a different total means a task added or removed a test the plan did not account for: list it in the final report.
Run: `dotnet format OKF4net.sln --verify-no-changes --no-restore` → exit 0.

Nothing to commit. Report the five observed outcomes verbatim in the task's completion note.

---

## Self-review

**Spec coverage.** §1 change of status → Task 10 (README, CLAUDE.md, CONTRIBUTING, README.md, code comments, producer texts, site, outreach drafts); the five-criteria rule and six examples → README in Task 10; the double loosening → README "Provenance (historical)" and CLAUDE.md in Task 10; §2 update mode → Task 9; normalisation ("capture writes the compared value") → `AssertGolden` receives normalised text, Task 9 Step 5; `--as-of` pin → Task 1; §3 semantic fidelity → Tasks 2–5, with the raw-bundle, second-date and trailing-newline assertions kept; the `§`-required rows → Task 6 (index structure), Task 2 (both verdicts), Task 8 (exit codes with reasons); "our behaviour" rows → Tasks 6–7; DOT out of snapshot-only → Task 5; §4 deletions → Task 8, with the Layout corrected in the same commit; §5.1 four surfaces → Tasks 2–5; §5.2 every bullet → Task 9 (`ParseScope`, one public constructor, `Commit` guards, staging, backup and restore, two messages, teardown check) and the documented limit → README and Task 11 Step 4; §5.3 table → Tasks 2–4 with the named inputs, the derived literals and the opposite cases, including distinct categories with asymmetric counts; structural comparison → `JsonShape`, duplicates and large integers covered; the CliTests inventory → the delete and keep lists in Tasks 2–5, each assertion of a deleted test mapped; §5.4 → Task 5 with edge cardinality; §5.5 → Tasks 6–7, three keys in the envelope test; §5.6 → Task 8 Step 5 and Task 10 Step 10; §5.7 (amended) order → task numbering; §5.8 outline, including the per-property column → Task 10 Step 1. Gap found and fixed: the Index test's cardinality guards now run inside the capture path so they are capture failures in update mode.

**Placeholder scan.** No TBD/TODO. The `fmt` test is placed after a named existing method (`Fmt_write_normalizes_file_in_place`, line 546). Every forward reference is resolved by task order: `GoldenUpdate` exists (Task 9) before the docstring cref (Task 10) and before the README's `## Regenerating` section (Task 10).

**Type consistency.** `GoldenUpdate.Commit(Scope, string, IReadOnlyList<(string Relative, string Content)>, string?)` — called in Task 9 Step 5 with `artefacts().Select(a => (a.Relative, a.Actual)).ToList()` (tuple element names differ, types match) and in `GoldenUpdateTests` with collection expressions of tuples. `Scope(IReadOnlySet<string>)` is `internal`, used only inside the test assembly; `ParseScope` returns `IReadOnlySet<string>`. `AssertGolden(string, int, int, Func<IReadOnlyList<(string Relative, string Actual)>>)` — every call passes a lambda returning a collection expression; the Index lambda has a block body. `JsonShape.AssertEquivalent(string expected, string actual)` — (expected, actual) everywhere. `AssertRawBundle(string, string)`, `Fwd`, `NormalizeJsonPaths` are private statics of `MachineOutputTests`.

**Review Focus.** 1 → `Commit_writes_utf8_without_bom_and_lf_only` (Task 9), Task 11 Step 1 on Windows. 2 → `Commit_restores_the_group_when_a_later_move_fails` (Task 9). 3 → the guard order in `AssertGolden` and `Commit_refuses_an_artefact_set_that_does_not_match_the_group` (Task 9). 4 → `ParseScope_rejects_unknown_empty_and_duplicate_names`, `ParseScope_reports_every_problem_at_once` (Task 9), Task 11 Step 5. 5 → `AssertEquivalent_reports_missing_and_unexpected_properties`, `AssertEquivalent_rejects_duplicate_property_names` (Task 2), and the derivation paragraphs before each literal (Tasks 2–4). The documented limit (whole collection filtered out) → Task 11 Step 4.
