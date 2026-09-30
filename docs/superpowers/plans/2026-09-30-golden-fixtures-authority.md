# Golden Fixtures Authority — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `docs/spec/SPEC.md` the only conformance authority, turn `tests/fixtures/golden/` into regenerable snapshots of our own output under a scoped update mode, and add the independent semantic-fidelity tests that make regeneration defensible.

**Architecture:** Three layers with distinct authorities — conformance (tests citing a `§`), semantic fidelity of the four machine outputs (one full-projection test per surface, compared structurally), and snapshots (`GoldenParityTests`, byte-compared after the existing path normalisation). The update mode lives in a new `GoldenUpdate` helper plus an xunit collection fixture that validates the requested scope before any write and checks at teardown that every named test actually captured. Work order is mandatory: pin dates → direct tests → deletions → doctrine → update mode last.

**Tech Stack:** .NET 10 / C# 14, xunit 2.9.3 (collection fixtures, no dynamic skip), `System.Text.Json` (`JsonDocument`/`JsonElement`), the in-process CLI runner `TestPaths.Run`.

**Spec:** `docs/superpowers/specs/2026-09-22-golden-fixtures-authority-design.md` — read it first; every task below cites the section it implements.

## Global Constraints

- Zero third-party runtime dependencies in `src/` (CLAUDE.md); test-only packages are fine. This plan adds none.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`; file-scoped namespaces; XML doc comments on public API; `TreatWarningsAsErrors` — a warning fails the build.
- `dotnet format OKF4net.sln --verify-no-changes` must pass; run `dotnet format OKF4net.sln` before each commit.
- **`OKF4net.sln` currently carries an uncommitted edit from another session that references absent worktree projects and breaks `dotnet test OKF4net.sln`.** Every test command in this plan targets the test project directly: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj`. Never `git add` the `.sln`.
- Never edit `docs/spec/SPEC.md`.
- Everything under `tests/fixtures/` stays LF, UTF-8 without BOM, `.gitattributes -text`, excluded from `.editorconfig` normalisation (spec §1 "Ce qui ne change pas").
- Goldens are compared **after** the existing normalisation (`\` → `/` on text at `GoldenParityTests.cs:84,93,101,109`; escaped pair `\\` → `/` on JSON at `:156`). Capture writes exactly the compared value (spec §2 "Normalisation des chemins").
- Commit messages: imperative subject with a `type(scope):` prefix as in `git log`, body explaining *why*, ending with the attribution line the session reminder specifies.
- Work in the order given. Tasks 1–2 precede everything; Task 10 is last (spec §5.7).

## Review Focus

Failure modes the spec implies that no task's happy path exercises; each is pinned by a test in the owning task:

1. **Capture writes a BOM or CRLF on Windows** — a golden regenerated on a Windows machine must be byte-identical to one regenerated on Linux (Task 10: `Commit_writes_utf8_without_bom_and_lf_only`).
2. **A test named in the scope fails its guard (wrong exit code) and the golden is overwritten with an error rendering** — nothing may be written (Task 10: `Commit_refuses_an_artefact_set_that_does_not_match_the_group`, and the guard order in `AssertGolden`).
3. **A typo in the scope silently regenerates nothing and the run looks like a normal green** — the whole list is rejected before any test runs (Task 10: `ParseScope_rejects_unknown_empty_and_duplicate_names`).
4. **A test named in the scope is excluded by `--filter`** — the collection fixture fails at teardown (Task 11, manual verification: it cannot be exercised in-process).
5. **A JSON projection test copies the observed output, becoming a tautology** — every expected value in Tasks 2–4 is justified from the fixture files and the hand-verified text goldens, and the comparator rejects extra/missing properties (Task 2: `AssertEquivalent_reports_missing_and_unexpected_properties`).

---

### Task 1: Pin `--as-of` on the four `validate` goldens

Spec §2 "Les dates ne sont pas toutes fixées" and §5.7 step 1. The four `validate` golden invocations pass no date; `okf_v02/metrics/dau.md` carries `stale_after: 2099-01-01T00:00:00Z`, so on 2099-01-01 the output gains a staleness warning nobody asked for. Pinning `--as-of 2026-09-25` was verified by execution to leave all four outputs and exit codes identical (spec §2).

**Files:**
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs:68-110`

**Interfaces:**
- Consumes: `TestPaths.Run(params string[])`, `WithRepoRootAsCwd`.
- Produces: nothing new; the four tests keep their names (Task 10 keys the update scope on them).

- [ ] **Step 1: Add the date to the four invocations**

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

and add, next to `GoldenRoot` at the top of the class:

```csharp
    /// <summary>
    /// The date every <c>validate</c> golden is evaluated at. Pinned so the
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

- [ ] **Step 2: Run the four tests and confirm nothing moved**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~GoldenParityTests.Validate" --no-restore`
Expected: 4 passed, 0 failed. If any fails, the pinned date is wrong for that fixture — stop and report; do not touch a golden.

- [ ] **Step 3: Confirm the boundary by hand, once**

Run: `src/OKF4net.Cli/bin/Debug/net10.0/okf validate tests/fixtures/okf_v02 --as-of 2099-01-01` from the repo root (build first with `dotnet build src/OKF4net.Cli --no-restore` if the binary is missing).
Expected: 4 warnings (one more than the golden's 3) — this is the drift the pin prevents. Nothing to commit from this step.

- [ ] **Step 4: Format and commit**

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/GoldenParityTests.cs
git commit -m "test(golden): pin --as-of on the four validate goldens so they stop hanging on the machine clock"
```

---

### Task 2: JSON structural comparison + full projection of `validate --json`

Spec §3 (obligation 2), §5.3 (validate row, expected built independently, structural comparison). Adds the shared comparator and the first full-projection test, and retires the two presence-only tests it makes redundant.

**Files:**
- Create: `tests/OKF4net.Tests/JsonShape.cs`
- Create: `tests/OKF4net.Tests/JsonShapeTests.cs`
- Create: `tests/OKF4net.Tests/MachineOutputTests.cs`
- Modify: `tests/OKF4net.Tests/CliTests.cs` — delete `Validate_json_reports_bundle_conformance_and_diagnostics` (line 370) and `Validate_json_reports_the_instant_it_evaluated_at` (line 280); keep `Validate_json_records_the_date_it_was_evaluated_against` (unpinned branch) and `Validate_json_diagnostic_field_is_populated_when_applicable` (branch on `field`).

**Interfaces:**
- Produces: `internal static class JsonShape { static void AssertEquivalent(string expectedJson, string actualJson); }` — recursive structural comparison, object property order ignored, array order significant, missing/extra properties reported with a JSON path. Tasks 3–4 use it.
- Produces: `MachineOutputTests` with helpers `Fwd(string)` (backslash → slash) and `NormalizeJsonPaths(string)` (the `:156` escaped-pair replace), reused by Tasks 3–4.

- [ ] **Step 1: Write the comparator's own tests**

Create `tests/OKF4net.Tests/JsonShapeTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Xunit.Sdk;

namespace OKF4net.Tests;

/// <summary>
/// The comparator behind every full-projection test. Its two jobs are to
/// ignore what the design calls presentation (object property order) and to
/// refuse what a field-by-field assertion cannot see (a property missing or
/// added, an array truncated).
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
    public void AssertEquivalent_compares_decoded_strings_not_escaped_text()
    {
        // System.Text.Json escapes '`' as ` in its output; the expected
        // side is written with the plain character. They are the same string.
        JsonShape.AssertEquivalent("""{"m":"`x`"}""", """{"m":"`x`"}""");
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
/// (design §5.3): the expected side is a literal written by hand, never a
/// captured output, and the comparison must catch what a field-by-field
/// assertion cannot -- a property that disappeared, one that appeared, an
/// array that lost an element. Object property order is ignored (it is
/// presentation, design §1); array order is significant. Strings compare
/// decoded, so a <c>`</c> in the actual text equals a plain backtick in
/// the expected literal.
/// </summary>
internal static class JsonShape
{
    /// <summary>Asserts that <paramref name="actualJson"/> has exactly the shape and values of <paramref name="expectedJson"/>.</summary>
    /// <param name="expectedJson">The hand-written expectation.</param>
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
                var expectedNames = expected.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                var actualNames = actual.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var name in expectedNames.Except(actualNames).Order(StringComparer.Ordinal))
                {
                    differences.Add($"{path}.{name}: missing");
                }

                foreach (var name in actualNames.Except(expectedNames).Order(StringComparer.Ordinal))
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
Expected: 5 passed.

- [ ] **Step 5: Write the `validate --json` projection test**

Create `tests/OKF4net.Tests/MachineOutputTests.cs`. Every expected value below is justified from the fixtures, not copied from a run: the eight `appendix_a` diagnostics are the eight lines of `tests/fixtures/golden/validate.out` (hand-verified at the v0.2 bump, see `tests/fixtures/README.md`), each with the `code` the validator assigns to that message (`DiagnosticCode.LegacyTimestamp`, `DiagnosticCode.MissingRecommendedField` in `src/OKF4net/Validate.cs`) and the `field` the message names; the four `okf_v02_reserved` errors are the four lines of `validate-reserved.out`, whose codes are listed in the README's "§11 conformance fix" section; `conformant`/`errorCount`/`warningCount`/`infoCount` are the summary lines of those two goldens; `asOf`/`evaluatedAt` are what `--as-of` pins (`JsonOutput.cs:140-141`).

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace OKF4net.Tests;

/// <summary>
/// Full-projection tests for the four machine outputs (design §5.1, §5.3):
/// each compares the ENTIRE document to a hand-written expectation, so a
/// projected value that no field-by-field test asserted -- the round-1 hole
/// was <c>findings[].status</c> -- cannot drift, and a property that appears
/// or disappears fails the test. The expectations are justified from the
/// fixture files and the hand-verified text goldens, never captured from a
/// run: a captured expectation is the tautology the update mode exists to
/// refuse.
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
    /// so the expectations can be written once for every platform.
    /// </summary>
    private static string NormalizeJsonPaths(string json) => json.Replace("\\\\", "/");

    [Fact]
    public void Validate_json_projects_a_conformant_bundle_completely()
    {
        var r = Run("validate", AppendixA, "--as-of", AsOf, "--json");
        Assert.Equal(0, r.Code);
        Assert.EndsWith("\n", r.Out, StringComparison.Ordinal);

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
Expected: 2 passed. If a value differs, first decide whether the expectation was mis-justified (fix the literal, citing the golden line) or the projection is wrong (a real finding — stop and report). Do not paste the actual output into the expectation.

- [ ] **Step 7: Retire the two presence-only tests the projection subsumes**

In `tests/OKF4net.Tests/CliTests.cs`, delete the whole methods `Validate_json_reports_bundle_conformance_and_diagnostics` (asserts `conformant`, `conceptCount`, `errorCount` on `appendix_a` and mere presence of `severity`/`code`/`message` — every one of those values is now asserted exactly) and `Validate_json_reports_the_instant_it_evaluated_at` (a substring check of `asOf`/`evaluatedAt` at 2099 — the projection asserts both at the pinned date; the unpinned branch stays covered by `Validate_json_records_the_date_it_was_evaluated_against`).

- [ ] **Step 8: Run the whole CLI + projection set, format, commit**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~CliTests|FullyQualifiedName~MachineOutputTests|FullyQualifiedName~JsonShapeTests" --no-restore`
Expected: all passed, 0 failed.

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/JsonShape.cs tests/OKF4net.Tests/JsonShapeTests.cs tests/OKF4net.Tests/MachineOutputTests.cs tests/OKF4net.Tests/CliTests.cs
git commit -m "test(cli): assert the whole validate --json projection, structurally, against a hand-written expectation"
```

---

### Task 3: Full projection of `info --json`

Spec §5.3 (info row): the barest surface — only `conceptCount` was asserted by value, and no golden compensates because the `info` golden captures the text rendering.

**Files:**
- Modify: `tests/OKF4net.Tests/MachineOutputTests.cs`
- Modify: `tests/OKF4net.Tests/CliTests.cs` — delete `Info_json_reports_bundle_summary` (line 404); keep `Info_json_types_is_present_and_empty_for_a_bundle_with_no_concepts` (the empty-bundle boundary).

**Interfaces:**
- Consumes: `JsonShape.AssertEquivalent`, `Fwd`, `NormalizeJsonPaths` from Task 2.

- [ ] **Step 1: Write the two tests**

Append to `MachineOutputTests`. Justification of the `appendix_a` values: 4 concepts and the 1/3 type split are `info.out`'s `concepts:` and `types:` block; `links: 5 internal (0 broken)` is its last line; `log.md` is the one reserved file in the fixture (`tests/fixtures/README.md`, Layout) and there is no `index.md`; no root index means no `okfVersion`. The built bundle is constructed so every opposite holds: a root `index.md` with `okf_version`, one broken link, one file the YAML parser rejects (an alias, the case `BundleTests.A_yaml_alias_lands_in_parse_errors_and_the_rest_loads` pins, whose message contains `alias` and `line 2`).

```csharp
    [Fact]
    public void Info_json_projects_appendix_a_completely()
    {
        var r = Run("info", AppendixA, "--json");
        Assert.Equal(0, r.Code);

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

        // The parse error's message names the rejected feature and the line; its
        // exact wording belongs to the YAML parser (pinned in Yaml/ tests), so it
        // is asserted by shape here and by content just below.
        using var doc = System.Text.Json.JsonDocument.Parse(NormalizeJsonPaths(r.Out));
        var parseError = doc.RootElement.GetProperty("parseErrors").EnumerateArray().Single();
        var message = parseError.GetProperty("message").GetString()!;
        Assert.Contains("alias", message, StringComparison.Ordinal);
        Assert.Contains("line 2", message, StringComparison.Ordinal);

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
          "parseErrors": [ { "path": "{{Fwd(Path.Combine(tmp.Path, "bad.md"))}}", "message": {{System.Text.Json.JsonSerializer.Serialize(message)}} } ]
        }
        """;

        JsonShape.AssertEquivalent(expected, NormalizeJsonPaths(r.Out));
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~MachineOutputTests.Info_json" --no-restore`
Expected: 2 passed. If `parseErrors[].path` is reported differently from `tmp.Path + "/bad.md"` (e.g. relative), that is a projection finding to report, not an expectation to bend.

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

Spec §5.3 (audit row) and §3 (the round-1 hole: `findings[].status`).

**Files:**
- Modify: `tests/OKF4net.Tests/MachineOutputTests.cs`
- Modify: `tests/OKF4net.Tests/CliTests.cs` — delete `Audit_json_carries_counts_query_and_findings` (line 1026; every value it asserts at 2099 is asserted by the projection at the same date). Keep `Audit_json_spells_trust_tiers_the_same_way_in_counts_and_findings` (a naming-consistency claim), `Audit_json_serializes_trust_query_in_ladder_order` (multi-tier ordering boundary) and `Audit_json_keeps_a_malformed_stale_after_raw_and_not_stale` (malformed branch).

- [ ] **Step 1: Write the three tests**

Justification: `okf_v02` holds two `Metric`s — `dau.md` (`status: stable`, verified by `process:nightly` then `human:ada` → `human-reviewed`, `stale_after: 2099-01-01T00:00:00Z`) and `legacy.md` (`status: retired`, unknown → treated as `stable` per `validate-v02.out`; no `verified` → `unverified`; no `stale_after`). At 2099-06-01 `dau` is stale, at 2026-09-25 nothing is. The default query is `--stale`; `query.trust` serialises in ladder order (`JsonOutput.cs:186-198`).

```csharp
    [Fact]
    public void Audit_json_projects_a_stale_finding_completely()
    {
        var r = Run("audit", OkfV02, "--as-of", "2099-06-01", "--json");
        Assert.Equal(0, r.Code);

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
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~MachineOutputTests.Audit_json" --no-restore`
Expected: 3 passed.

- [ ] **Step 3: Delete `Audit_json_carries_counts_query_and_findings` from `CliTests.cs`**

- [ ] **Step 4: Run, format, commit**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~CliTests|FullyQualifiedName~MachineOutputTests" --no-restore`
Expected: all passed.

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/MachineOutputTests.cs tests/OKF4net.Tests/CliTests.cs
git commit -m "test(cli): assert the whole audit --json projection, closing the per-finding status hole"
```

---

### Task 5: DOT structural test and the `okf info` links line

Spec §5.4 (grammar, edge set, determinism — not order) and §3 (the `links:` line carries two counts).

**Files:**
- Modify: `tests/OKF4net.Tests/MachineOutputTests.cs`
- Modify: `tests/OKF4net.Tests/CliTests.cs` — keep `Graph_dot_prints_digraph`, `Graph_dot_styles_broken_links_dashed_and_red`, `Graph_dot_does_not_style_resolvable_links` (each a distinct branch); add nothing there.

- [ ] **Step 1: Write the DOT test and the links-line tests**

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
        // asserted -- it is presentation (design §1); the SET is.
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
        Assert.All(edges, line => Assert.Matches(DotEdgeLine, line));
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "  \"a\" -> \"b\";",
                "  \"a\" -> \"missing\" [style=dashed, color=red];",
            },
            edges.ToHashSet(StringComparer.Ordinal));
        // b and c emit nothing: no node declarations, no empty statements.
        Assert.DoesNotContain(edges, line => line.Contains("\"c\"", StringComparison.Ordinal));
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
Expected: 4 passed. If `Info_links_line_counts_a_broken_link` reports `0 internal`, check whether a broken link is counted in `linkCount` (`JsonOutput.WriteInfo` sums `LinksFrom`, which includes unresolved targets) — the expectation above assumes it is; a mismatch is a finding to report.

- [ ] **Step 3: Format, commit**

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/MachineOutputTests.cs
git commit -m "test(cli): pin the DOT grammar and edge set, determinism, and both counts of info's links line"
```

---

### Task 6: Direct §8 index tests

Spec §5.5. Each test names its input and its distinctive expectation; none reads a golden.

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
    /// group -- pinned because only a golden pinned it before.
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
    /// (there is no index.md to skip yet), which is why the golden never did.
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
    /// wording was pinned only by the index golden until now.
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
git commit -m "test(index): pin the §8 structure and our index conventions directly, not only through the golden"
```

---

### Task 7: Direct serialisation and `fmt` tests

Spec §5.5 (Serialize full text, idempotence, fmt stdout).

**Files:**
- Modify: `tests/OKF4net.Tests/DocumentTests.cs` (append)
- Modify: `tests/OKF4net.Tests/CliTests.cs` (append near the existing `Fmt_` tests around line 516)

- [ ] **Step 1: Write the serialisation tests**

Append to `DocumentTests`:

```csharp
    /// <summary>
    /// The full serialized text, in one assertion: `---`, the frontmatter, `---`,
    /// one blank line, the body, one trailing newline. Every other test here
    /// round-trips through Parse and compares structure; this is the only
    /// place the envelope itself is pinned outside a golden.
    /// </summary>
    [Fact]
    public void Serialize_emits_the_canonical_envelope_exactly()
    {
        var doc = OkfDocument.Parse("---\ntype: BigQuery Table\ntitle: Users\n---\n\nApplication users; not part of the sales domain.\n");

        Assert.Equal(
            "---\ntype: BigQuery Table\ntitle: Users\n---\n\nApplication users; not part of the sales domain.\n",
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

Append to `CliTests`, next to `Fmt_write_flag_formats_in_place` (or whatever the existing `-w` test at ~line 516 is named — keep it):

```csharp
    /// <summary>
    /// `fmt` without `-w` prints the formatted document to stdout and leaves
    /// the file alone. Until now the only passing test through this branch was
    /// the golden comparison.
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

### Task 8: The two deletions, with their dated entries

Spec §4 and §5.6. `golden/index-input/`'s five bundle copies are read by no test (the test copies `appendix_a` into a `TempDir`); the four `.exitcode` files are integers that belong in assertions — `1` for `validate-reserved` (the §11 non-conformant verdict, our exit-code contract), `0` for the other three.

**Files:**
- Delete: `tests/fixtures/golden/index-input/log.md`, `tests/fixtures/golden/index-input/datasets/sales.md`, `tests/fixtures/golden/index-input/tables/customers.md`, `tests/fixtures/golden/index-input/tables/orders.md`, `tests/fixtures/golden/index-input/tables/users.md`
- Delete: `tests/fixtures/golden/validate.exitcode`, `validate-v02.exitcode`, `validate-computation.exitcode`, `validate-reserved.exitcode`
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs:72,92,100,108` (exit-code assertions) and `:205-208` (cardinality comment)
- Modify: `tests/fixtures/README.md` — append the dated entry (the file is rewritten in Task 9; the entry is written now so the commit that deletes carries its trace)

- [ ] **Step 1: Confirm the five copies are unread**

Run: `grep -rn "index-input" tests/OKF4net.Tests src` from the repo root.
Expected: only `GoldenParityTests.cs` line 201, which reads the three `index.md` files. Stop if anything else references the directory.

- [ ] **Step 2: Replace the exit-code file reads**

In `GoldenParityTests.cs`, replace the four `Assert.Equal(int.Parse(Golden("....exitcode")), r.Code);` lines with:

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

And replace the cardinality comment at lines 205-206 with:

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

- [ ] **Step 4: Run the golden tests**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~GoldenParityTests" --no-restore`
Expected: 11 passed.

- [ ] **Step 5: Append the dated entry to `tests/fixtures/README.md`**

Append at the end of the file (use today's date):

```markdown

## Two deletions under the fixtures-authority design (2026-09-30)

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

### Task 9: Rewrite the doctrine everywhere it lives

Spec §1 (README, CLAUDE.md, the producer's three texts, the site, the two code comments), §5.6 (CHANGELOG), §5.8 (README outline). One task, one commit: the texts contradict each other if split.

**Files:**
- Modify: `tests/fixtures/README.md` (rewrite the top; keep the revision log verbatim)
- Modify: `CLAUDE.md:28` (the hard rule) and `:76` ("which stays byte-exact golden captures")
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs:6-27` (class docstring)
- Modify: `src/OKF4net.Cli/OkfCli.cs:936-939` (`WriteGraphDot` comment)
- Modify: `producers/tests/OkfProducer.Tests/fixtures/README.md:17-27`, `.gitattributes:17`, `producers/tests/OkfProducer.Tests/Generation/CheckTests.cs:12-18`
- Modify: `web/src/pages/Contributing.tsx:72-84`
- Modify: `CHANGELOG.md` under `## [Unreleased]` → `### Changed` (line 440)

- [ ] **Step 1: Rewrite the top of `tests/fixtures/README.md`**

Replace everything from the start of the file up to (not including) the heading `## v0.1 → v0.2 bump (2026-07-28)` with the text below. Everything from that heading to the end of the file — including the entry Task 8 appended — is kept verbatim under the new `## Revision log` heading inserted just before it.

```markdown
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
tests in `MachineOutputTests`, against hand-written expectations. What the
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
- `golden/` — one group of snapshot files per test in `GoldenParityTests`:

| Test | Snapshot files | Invocation |
|---|---|---|
| `Validate_output_and_exitcode_match_golden` | `validate.out` | `okf validate tests/fixtures/appendix_a --as-of 2026-09-25` |
| `Validate_v02_fixture_matches_golden` | `validate-v02.out` | `okf validate tests/fixtures/okf_v02 --as-of 2026-09-25` |
| `Validate_computation_fixture_matches_golden` | `validate-computation.out` | `okf validate tests/fixtures/okf_v02_computation --as-of 2026-09-25` |
| `Validate_reserved_fixture_matches_golden` | `validate-reserved.out` | `okf validate tests/fixtures/okf_v02_reserved --as-of 2026-09-25` |
| `Info_output_matches_golden` | `info.out` | `okf info tests/fixtures/appendix_a` |
| `Audit_report_matches_golden` | `audit-v02.out` | `okf audit tests/fixtures/okf_v02 --as-of 2099-06-01` |
| `Audit_json_matches_golden` | `audit-v02.json` | `okf audit tests/fixtures/okf_v02 --as-of 2099-06-01 --json` |
| `Graph_dot_matches_golden` | `graph.dot` | `okf graph <appendix_a> --dot` |
| `Fmt_output_matches_golden` | `fmt/users.md` | `okf fmt <appendix_a>/tables/users.md` |
| `Index_generation_matches_golden` | `index-input/index.md`, `index-input/datasets/index.md`, `index-input/tables/index.md` | `IndexGenerator.RegenerateIndexes` on a copy of `appendix_a` |
| `Verify_output_matches_golden` | `verify.out`, `verify-dau.md` | `okf verify <copy of okf_v02> metrics/dau metrics/legacy --by human:ada --at 2026-08-28T09:14:00Z` |

Exit codes are asserted in the tests, not stored as files. The `validate`
invocations run from the repository root with the relative path shown, because
the output embeds the path as given.

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

## Comparison contract

Snapshots are committed with LF endings, UTF-8 without BOM, and compared after
being read as text: the four `validate` outputs and `audit-v02.json` are
compared **after** the OUTPUT's native path separators are normalised to `/`
(the snapshots were captured on Linux and embed paths); every other file is
compared as read. `.gitattributes` marks `tests/fixtures/** -text` and
`.editorconfig` excludes this directory, so no tool normalises them. Never let
an editor touch them: trailing whitespace, final newlines and line endings are
significant.

## Provenance (historical)

Four snapshots — `info.out`, `graph.dot`, `fmt/users.md` and the three
`index-input/*.md` — still hold the bytes first captured on 2026-07-21 from the
Rust `okf` crate that then lived in this repository (source at commit
`d20343c`), built in Docker:

```
docker image: rust:1  (pulled digest sha256:9a2cd304a852f05d3352f75bc2775242371c0169a72dbb40d5d881379d571989)
rustc 1.97.1 (8bab26f4f 2026-07-14)
cargo 1.97.1 (c980f4866 2026-06-30)
```

That origin is history, not authority: the project implements the published
spec, and treating a deleted reimplementation as its norm was abandoned by
decision on 2026-09-30. These four files follow the same two regimes as every
other snapshot.

## Revision log
```

- [ ] **Step 2: Rewrite the hard rule in `CLAUDE.md:28`**

Replace the whole bullet starting `- **Never touch \`tests/fixtures/\` to make a failing test pass.**` with:

```markdown
- **Never regenerate a snapshot without reading the diff, and never to make a failing test pass.** `tests/fixtures/golden/` holds snapshots of *our own* CLI output — regenerable, not a reference implementation's bytes — and `docs/spec/SPEC.md` is the only conformance authority. Whether a change to a snapshot needs arbitration depends on **what the diff changes, not which file it lands in** (the five-criteria rule is in `tests/fixtures/README.md`): a presentation-only diff (spacing, wording, order nothing prescribes — same facts, elements, relations, valid structure, exit codes) is regenerated with the scoped update mode (`OKF_UPDATE_GOLDEN=<test names>`, two runs, never one), while a **semantic** diff — a verdict, a count, an exit code, §8/§9 structure, a value a machine output projects, a document a verb writes — and **every edit to an input bundle** takes explicit user arbitration **and** a dated entry in that README first. The update mode captures; it never authorises. An unintended diff is a real failure to investigate on the C# side, never a fixture to refresh. Machine outputs (`validate`/`info`/`audit --json`, `graph --dot`) are additionally pinned by `MachineOutputTests` against hand-written expectations; those tests are code, and change with the code they test.
```

At `CLAUDE.md:76`, replace `which stays byte-exact golden captures` with `which holds snapshots of our own CLI output under the regimes stated in its README`.

- [ ] **Step 3: Rewrite the `GoldenParityTests` class docstring**

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

(`GoldenUpdate` does not exist until Task 10; write the `<see cref="GoldenUpdate"/>` as plain text `GoldenUpdate` in this task and switch it to a cref in Task 10, so the doc build does not warn.)

- [ ] **Step 4: Rewrite the `WriteGraphDot` comment in `OkfCli.cs:936-939`**

Replace with:

```csharp
    /// <summary>
    /// Renders the link graph as Graphviz DOT, broken links dashed and red.
    /// The grammar (header, one edge statement per link, closing brace) and
    /// the edge set are pinned by <c>MachineOutputTests</c>; the exact bytes
    /// are snapshotted in <c>tests/fixtures/golden/graph.dot</c> under the
    /// regimes in <c>tests/fixtures/README.md</c>.
    /// </summary>
```

- [ ] **Step 5: Align the producer's three texts**

In `producers/tests/OkfProducer.Tests/fixtures/README.md`, replace the section from `## Read this first: the discipline here is the OPPOSITE of \`tests/fixtures/\`` through the paragraph ending `read the diff before you accept it.` with:

```markdown
## Read this first: the same capture mechanics as `tests/fixtures/`, without its arbitration layer

The repository's other golden directory, `tests/fixtures/golden/`, also holds **our own** output and
is also regenerable — since 2026-09-30, under the two regimes its README states (a presentation diff
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

In `.gitattributes`, replace the line `# Note the discipline there is the OPPOSITE of tests/fixtures/ -- see that directory's own README.` with:

```
# tests/fixtures/ is regenerable too, under its own README's regimes; the -text protection is shared.
```

In `producers/tests/OkfProducer.Tests/Generation/CheckTests.cs`, replace the paragraph at lines 12-18 (`<para><b>The golden here follows the OPPOSITE discipline ...</para>`) with:

```csharp
/// <para><b>This golden captures our own output and is regenerable by construction</b>, like
/// <c>tests/fixtures/golden/</c> since 2026-09-30 -- with one difference: nothing here carries a
/// conformance verdict, so there is no arbitration layer, and the update switch rewrites the whole
/// golden rather than a named scope. It <i>must</i> be regenerated whenever the generator changes
/// intentionally, with the diff reviewed as part of that change. <c>fixtures/README.md</c> states it
/// in full; the regeneration switch is <see cref="UpdateGoldenVariable"/>, read by
/// <see cref="Check_passes_on_an_unchanged_bundle"/> below.</para>
```

- [ ] **Step 6: Rewrite the site's warning block**

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

- [ ] **Step 7: Add the CHANGELOG entry**

Under `## [Unreleased]` → `### Changed` (line 440), insert as the first bullet:

```markdown
- **`tests/fixtures/golden/` is now a set of snapshots of this project's own CLI output, and
  `docs/spec/SPEC.md` is the only conformance authority.** The directory presented itself as
  byte-exact captures of a reference implementation and `CLAUDE.md` forbade touching it; the
  binary those bytes came from was removed in July and had never been the norm of a project that
  implements a published spec. Conformance is verified by the tests that cite a section; the four
  machine outputs (`validate`/`info`/`audit --json`, `graph --dot`) are pinned by new
  full-projection tests against hand-written expectations; and the snapshots pin rendering only.
  They are regenerable with a scoped update mode (`OKF_UPDATE_GOLDEN=<test names>`, which
  rewrites then deliberately fails so a tautology is never reported green) under two regimes
  that depend on what the diff changes: presentation diffs are regenerated and reviewed; semantic
  diffs and any input-bundle edit need explicit arbitration and a dated README entry. Two
  deletions came with it: the five unread bundle copies under `golden/index-input/`, and the four
  one-byte `*.exitcode` files, whose values are assertions now. The four `validate` snapshot
  invocations are pinned with `--as-of` so they no longer depend on the machine clock.
  Design: `docs/superpowers/specs/2026-09-22-golden-fixtures-authority-design.md`.
```

- [ ] **Step 8: Build, run the whole suite, check the site compiles, format, commit**

Run: `dotnet build tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore` (doc-comment warnings are errors).
Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "Category!=ContainerIntegration"`
Expected: all passed.
Run: `cd web && npx tsc --noEmit && cd ..`
Expected: no output (clean).

```bash
dotnet format OKF4net.sln --no-restore
git add tests/fixtures/README.md CLAUDE.md tests/OKF4net.Tests/GoldenParityTests.cs src/OKF4net.Cli/OkfCli.cs producers/tests/OkfProducer.Tests/fixtures/README.md .gitattributes producers/tests/OkfProducer.Tests/Generation/CheckTests.cs web/src/pages/Contributing.tsx CHANGELOG.md
git commit -m "docs: the spec is the conformance authority; tests/fixtures/golden/ are snapshots of our own output"
```

---

### Task 10: The scoped update mode

Spec §2 and §5.2, in full: a mandatory scope, whole-list validation before any write, a scope-versus-executed check at collection teardown, guards before writing, per-group atomicity, two distinct failures. Last by design (§5.7).

**Files:**
- Create: `tests/OKF4net.Tests/GoldenUpdate.cs`
- Create: `tests/OKF4net.Tests/GoldenUpdateTests.cs`
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs` (collection attribute, constructor, `AssertGolden`, every test)
- Modify: `tests/fixtures/README.md` (a `## Regenerating` section, inserted before `## Revision log`)

**Interfaces:**
- Produces: `GoldenUpdate.Variable` (`"OKF_UPDATE_GOLDEN"`), `GoldenUpdate.Groups` (test name → snapshot files), `GoldenUpdate.ParseScope(string?)`, `GoldenUpdate.Scope` (the collection fixture: `Includes(test)`, `MarkCaptured(test)`, teardown check), `GoldenUpdate.Commit(Scope, test, artefacts)`, `GoldenUpdate.RefusesToAssert(test)`.

- [ ] **Step 1: Write the unit tests for the parts that run without the variable**

Create `tests/OKF4net.Tests/GoldenUpdateTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// The update mode's pieces that can be exercised without setting the
/// variable: scope parsing (design §5.2 -- the whole list is validated before
/// any write), the atomic group commit, and the encoding contract of what it
/// writes. The end-to-end behaviour under the variable, including the
/// teardown check against <c>--filter</c>, is verified by hand once
/// (README, "Regenerating") because it cannot run inside a single test.
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
    public void Commit_refuses_an_artefact_set_that_does_not_match_the_group()
    {
        using var root = new TempDir();
        var scope = new GoldenUpdate.Scope(GoldenUpdate.ParseScope("Verify_output_matches_golden"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoldenUpdate.Commit(scope, "Verify_output_matches_golden", [("verify.out", "x\n")], root.Path));

        Assert.Contains("verify-dau.md", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root.Path, "verify.out"))); // nothing written for the group
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
/// less than it announced. The mode captures; it never authorises: a semantic
/// diff, or any edit to an input bundle, still takes explicit arbitration and
/// a dated entry in <c>tests/fixtures/README.md</c>.</para>
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
    /// or nothing is written. Then every file is staged in a temporary
    /// directory (UTF-8 without BOM, bytes as given -- LF stays LF) and moved
    /// into place in one pass, so an exception mid-group leaves the committed
    /// snapshots untouched.
    /// </summary>
    /// <param name="scope">The run's scope; the test is marked captured on success.</param>
    /// <param name="test">The <see cref="GoldenParityTests"/> method name.</param>
    /// <param name="artefacts">Each snapshot file of the group with the exact text to write.</param>
    /// <param name="goldenRoot">Where the snapshots live; tests pass a temporary directory.</param>
    public static void Commit(Scope scope, string test, IReadOnlyList<(string Relative, string Content)> artefacts, string? goldenRoot = null)
    {
        var root = goldenRoot ?? DefaultGoldenRoot;
        var expected = Groups[test].Order(StringComparer.Ordinal).ToList();
        var given = artefacts.Select(a => a.Relative).Order(StringComparer.Ordinal).ToList();
        if (!expected.SequenceEqual(given, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"capture failure for {test}: nothing written. The group is [{string.Join(", ", expected)}] but the test offered [{string.Join(", ", given)}].");
        }

        var staging = Directory.CreateTempSubdirectory("okf-golden-");
        try
        {
            var staged = new List<(string Source, string Destination)>();
            foreach (var (relative, content) in artefacts)
            {
                var source = Path.Combine(staging.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                File.WriteAllBytes(source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
                staged.Add((source, Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))));
            }

            foreach (var (source, destination) in staged)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(source, destination, overwrite: true);
            }
        }
        finally
        {
            try
            {
                staging.Delete(recursive: true);
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
    /// teardown fails if a requested test never did.
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

        /// <summary>A scope from an already-parsed set, for tests of the mode itself.</summary>
        public Scope(IReadOnlySet<string> requested)
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
        /// by <c>--filter</c>, or failed a guard before writing -- so a red run
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
                    + " them, or they failed a guard before writing. Nothing was verified for them; the other groups may have been rewritten.");
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
Expected: 8 passed. (`Groups_name_every_snapshot_file_exactly_once_and_every_golden_test` passes only after Task 8 deleted the nine files — it did.)

- [ ] **Step 5: Wire `GoldenParityTests` into the collection and route every test through `AssertGolden`**

At the top of the class, replace `public class GoldenParityTests` with:

```csharp
[Collection(GoldenUpdate.CollectionName)]
public class GoldenParityTests(GoldenUpdate.Scope scope)
```

Add this helper after `WithRepoRootAsCwd`:

```csharp
    /// <summary>
    /// The one comparison path for every snapshot test. Guards first, in both
    /// modes: the expected exit code. In update mode the group is then
    /// rewritten from the values that would have been compared -- already
    /// normalised where the test normalises -- and the test fails on purpose
    /// (<see cref="GoldenUpdate.RefusesToAssert"/>). Otherwise every artefact
    /// is compared to its snapshot.
    /// </summary>
    /// <param name="test">The calling test's name (<c>nameof</c>).</param>
    /// <param name="code">The exit code observed.</param>
    /// <param name="expectedCode">The exit code the test expects; a mismatch is a capture failure in update mode and a plain failure otherwise.</param>
    /// <param name="artefacts">Each snapshot file of the test's group with the text to compare (or write).</param>
    private void AssertGolden(string test, int code, int expectedCode, params (string Relative, string Actual)[] artefacts)
    {
        if (scope.Includes(test))
        {
            Assert.True(code == expectedCode, $"capture failure for {test}: exit code {code}, expected {expectedCode}; nothing written.");
            GoldenUpdate.Commit(scope, test, artefacts.Select(a => (a.Relative, a.Actual)).ToList());
            Assert.Fail(GoldenUpdate.RefusesToAssert(test));
        }

        Assert.Equal(expectedCode, code);
        foreach (var (relative, actual) in artefacts)
        {
            Assert.Equal(Golden(relative), actual);
        }
    }
```

Then rewrite each test body's assertions to use it. The complete new bodies:

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
            ("validate.out", r.Out.Replace('\\', '/')));
    }

    [Fact]
    public void Validate_v02_fixture_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02", "--as-of", PinnedAsOf));
        AssertGolden(nameof(Validate_v02_fixture_matches_golden), r.Code, 0,
            ("validate-v02.out", r.Out.Replace('\\', '/')));
    }

    [Fact]
    public void Validate_computation_fixture_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02_computation", "--as-of", PinnedAsOf));
        AssertGolden(nameof(Validate_computation_fixture_matches_golden), r.Code, 0,
            ("validate-computation.out", r.Out.Replace('\\', '/')));
    }

    [Fact]
    public void Validate_reserved_fixture_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02_reserved", "--as-of", PinnedAsOf));
        // §11 condition 3 fails: the bundle is non-conformant, and this CLI's
        // contract maps that verdict to exit code 1 -- the spec defines the
        // verdict, the integer is ours.
        AssertGolden(nameof(Validate_reserved_fixture_matches_golden), r.Code, 1,
            ("validate-reserved.out", r.Out.Replace('\\', '/')));
    }

    [Fact]
    public void Info_output_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("info", "tests/fixtures/appendix_a"));
        AssertGolden(nameof(Info_output_matches_golden), r.Code, 0, ("info.out", r.Out));
    }

    [Fact]
    public void Audit_report_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("audit", "tests/fixtures/okf_v02", "--as-of", "2099-06-01"));
        // Every path in this output is a concept id, always '/'-normalised by
        // ConceptId.FromPath, so the comparison is as read.
        AssertGolden(nameof(Audit_report_matches_golden), r.Code, 0, ("audit-v02.out", r.Out));
    }

    [Fact]
    public void Audit_json_matches_golden()
    {
        var r = WithRepoRootAsCwd(() => Run("audit", "tests/fixtures/okf_v02", "--as-of", "2099-06-01", "--json"));
        // Only the findings' `path` carries a native separator, and the
        // serializer escapes each backslash as the two-character sequence `\\`
        // in the JSON text; a single-char Replace would turn that pair into
        // "//", so the search pattern is the escaped sequence.
        AssertGolden(nameof(Audit_json_matches_golden), r.Code, 0, ("audit-v02.json", r.Out.Replace("\\\\", "/")));
    }

    [Fact]
    public void Graph_dot_matches_golden()
    {
        var r = Run("graph", BundlePath, "--dot");
        AssertGolden(nameof(Graph_dot_matches_golden), r.Code, 0, ("graph.dot", r.Out));
    }

    [Fact]
    public void Fmt_output_matches_golden()
    {
        var r = Run("fmt", Path.Combine(BundlePath, "tables", "users.md"));
        AssertGolden(nameof(Fmt_output_matches_golden), r.Code, 0, ("fmt/users.md", r.Out));
    }

    [Fact]
    public void Index_generation_matches_golden()
    {
        using var tmp = new TempDir();
        CopyDirectory(BundlePath, tmp.Path);

        var written = IndexGenerator.RegenerateIndexes(tmp.Path);

        // Guards in both modes, before any comparison or capture: three files
        // written, three on disk, and exactly the 3 generated index.md files
        // plus the 5 source documents copied in from appendix_a -- catches a
        // file created in excess or a net deletion; not a modified original,
        // nor a delete-and-create.
        Assert.Equal(3, written.Count);
        Assert.Equal(3, Directory.GetFiles(tmp.Path, "index.md", SearchOption.AllDirectories).Length);
        Assert.Equal(8, Directory.GetFiles(tmp.Path, "*", SearchOption.AllDirectories).Length);

        AssertGolden(nameof(Index_generation_matches_golden), 0, 0,
            ("index-input/index.md", File.ReadAllText(Path.Combine(tmp.Path, "index.md"))),
            ("index-input/datasets/index.md", File.ReadAllText(Path.Combine(tmp.Path, "datasets", "index.md"))),
            ("index-input/tables/index.md", File.ReadAllText(Path.Combine(tmp.Path, "tables", "index.md"))));
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
        AssertGolden(nameof(Verify_output_matches_golden), r.Code, 0,
            ("verify.out", r.Out),
            ("verify-dau.md", File.ReadAllText(Path.Combine(tmp.Path, "metrics", "dau.md"))));
    }
```

Keep `Golden`, `Run`, `WithRepoRootAsCwd`, `CopyDirectory`, `PinnedAsOf`, `BundlePath`, `GoldenRoot` as they are. In the class docstring, change the plain-text `GoldenUpdate` from Task 9 to `<see cref="GoldenUpdate"/>`. Note `Golden(...)` now takes '/'-separated relatives: change its body to `File.ReadAllText(Path.Combine(GoldenRoot, rel.Replace('/', Path.DirectorySeparatorChar)))`.

- [ ] **Step 6: Run the snapshot tests without the variable**

Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --filter "FullyQualifiedName~GoldenParityTests|FullyQualifiedName~GoldenUpdateTests" --no-restore`
Expected: 19 passed.

- [ ] **Step 7: Add the `## Regenerating` section to `tests/fixtures/README.md`**

Insert before `## Revision log`:

```markdown
## Regenerating a snapshot

The update mode is **scoped**: it rewrites only the tests you name, and refuses the whole list before
writing anything if a name is unknown, empty or duplicated. Name the tests (exact
`GoldenParityTests` method names, comma-separated, no wildcard) in `OKF_UPDATE_GOLDEN`, and pass the
same names to `--filter` so the run executes them:

```sh
OKF_UPDATE_GOLDEN=Info_output_matches_golden,Graph_dot_matches_golden \
  dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj \
  --filter "FullyQualifiedName~GoldenParityTests.Info_output_matches_golden|FullyQualifiedName~GoldenParityTests.Graph_dot_matches_golden"
```

(PowerShell: `$env:OKF_UPDATE_GOLDEN = "Info_output_matches_golden,Graph_dot_matches_golden"` first, and
`Remove-Item Env:OKF_UPDATE_GOLDEN` afterwards.)

**That command exits RED, by design, and a green run would be the bug.** Each named test rewrites its
group of files — a group is written whole or not at all — from the value it would have compared, and
then *refuses to assert*: the expected side was just produced by the same harness as the actual side,
so the comparison would be a tautology, and a tautology reported green is exactly how a stale variable
in someone's shell disarms a snapshot without anyone noticing. Two failures are possible and say which
they are: **capture failure** (a guard such as the exit code did not hold; nothing was written for that
group) and **refuses to assert** (the group was rewritten; read the diff). If a named test did not run
— excluded by `--filter` — the collection fails at teardown and says so.

So the procedure is two runs, not one:

1. Run with the variable. Read `git diff tests/fixtures/golden`. Decide the regime (above): a
   presentation diff needs nothing more; a semantic diff needed arbitration **before** this step and
   needs its dated entry below.
2. Re-run **without** the variable to actually check, and commit the diff with the change that caused
   it.

The mode captures; it never authorises. Groups: see the table under Layout.
```

- [ ] **Step 8: Format, commit**

```bash
dotnet format OKF4net.sln --no-restore
git add tests/OKF4net.Tests/GoldenUpdate.cs tests/OKF4net.Tests/GoldenUpdateTests.cs tests/OKF4net.Tests/GoldenParityTests.cs tests/fixtures/README.md
git commit -m "test(golden): a scoped update mode that rewrites named groups, then refuses to assert"
```

---

### Task 11: First real use of the update mode, and the three end-to-end checks

Spec §5.7 step 5. Three manual verifications the suite cannot run on itself; each must leave `git status` clean afterwards.

**Files:** none modified permanently.

- [ ] **Step 1: A no-op regeneration leaves no diff**

Run (bash; PowerShell equivalent in the README):
```bash
OKF_UPDATE_GOLDEN=Info_output_matches_golden dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "FullyQualifiedName~GoldenParityTests.Info_output_matches_golden"
```
Expected: 1 failed, with a message starting `OKF_UPDATE_GOLDEN: Info_output_matches_golden REWROTE its snapshot group [info.out] and refuses to assert`.
Then: `git status --short tests/fixtures/golden` → **no output** (the rewritten bytes equal the committed ones). If `info.out` shows as modified, inspect `git diff`: a CRLF or BOM difference means Task 10's `Commit` is wrong; stop and fix it there.

- [ ] **Step 2: A group with two artefacts and one with three**

```bash
OKF_UPDATE_GOLDEN=Verify_output_matches_golden,Index_generation_matches_golden dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "FullyQualifiedName~GoldenParityTests.Verify_output_matches_golden|FullyQualifiedName~GoldenParityTests.Index_generation_matches_golden"
```
Expected: 2 failed (both "refuses to assert"); `git status --short tests/fixtures/golden` → no output.

- [ ] **Step 3: The `--filter` mismatch is caught at teardown**

```bash
OKF_UPDATE_GOLDEN=Info_output_matches_golden,Graph_dot_matches_golden dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "FullyQualifiedName~GoldenParityTests.Info_output_matches_golden"
```
Expected: the `Info` test fails with "refuses to assert" **and** the run reports a collection cleanup failure whose message names `Graph_dot_matches_golden` and `--filter`. `git status --short tests/fixtures/golden` → no output.

- [ ] **Step 4: The typo case writes nothing**

```bash
OKF_UPDATE_GOLDEN=Info_output_matches_goldne dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "FullyQualifiedName~GoldenParityTests"
```
Expected: every snapshot test errors at fixture construction with `OKF_UPDATE_GOLDEN rejected, nothing was rewritten … unknown: Info_output_matches_goldne`; no test ran to a comparison. `git status --short` → clean.

- [ ] **Step 5: The full suite, once more, without the variable**

Make sure the variable is unset (`echo $OKF_UPDATE_GOLDEN` prints nothing).
Run: `dotnet test tests/OKF4net.Tests/OKF4net.Tests.csproj --no-restore --filter "Category!=ContainerIntegration"`
Expected: 0 failed; the pre-plan run was 2300 passed + 12 skipped (the container tests). The plan deletes 4 `CliTests` methods and adds 32 (5 `JsonShapeTests`, 11 `MachineOutputTests`, 5 `IndexTests`, 2 `DocumentTests`, 1 `CliTests`, 8 `GoldenUpdateTests`), so expect **2328 passed, 12 skipped, 2340 total**. A different number means a task added or removed a test the plan did not account for: list it in the final report.
Run: `dotnet format OKF4net.sln --verify-no-changes --no-restore` → exit 0.

Nothing to commit. Report the four observed messages verbatim in the task's completion note.

---

## Self-review

**Spec coverage.** §1 change of status → Task 9 (README, CLAUDE.md, code comments, producer texts, site); the five-criteria rule → README in Task 9; the double loosening → README "Provenance (historical)" and CLAUDE.md in Task 9; §2 update mode → Task 10; normalisation ("capture writes the compared value") → `AssertGolden` receives the normalised text, Task 10 Step 5; `--as-of` pin → Task 1; §3 semantic fidelity → Tasks 2–5; the `§`-required rows (index structure, conformant verdict, non-conformant verdict + exit 1) → Task 6 (`Regenerate_writes_no_frontmatter_when_no_root_index_pre_existed`), Task 2 (both projections carry `conformant` and the exit codes), Task 8 (exit-code assertions with reasons); "our behaviour" rows → Tasks 6–7; DOT out of snapshot-only → Task 5; §4 deletions → Task 8; §5.1 four surfaces → Tasks 2–5, no test of the text renderings beyond the `links:` line; §5.2 every bullet → Task 10 (`ParseScope`, `Scope` ctor/teardown, `Commit` guards and staging, two messages); §5.3 table → Tasks 2–4 use the named inputs and enumerate the opposite cases; structural comparison → `JsonShape`; the CliTests inventory the spec left to the plan → the delete lists in Tasks 2, 3, 4 with the kept tests named; §5.4 → Task 5; §5.5 → Tasks 6–7; §5.6 → Task 8 Step 5 and Task 9 Step 7; §5.7 order → task numbering; §5.8 outline → Task 9 Step 1. Gap found and fixed: the spec's "stderr empty where the test requires it today" — no snapshot test asserts stderr today, so `AssertGolden` does not guard it, and the fmt direct test (Task 7) is where an empty stderr is asserted.

**Placeholder scan.** No TBD/TODO. Task 7 Step 2 said "or whatever the existing `-w` test is named" — rewritten above as an instruction to append next to the existing `Fmt_` tests; the new test does not depend on the old one's name. Task 9 Step 3's `<see cref="GoldenUpdate"/>` forward reference is handled explicitly (plain text in Task 9, cref in Task 10).

**Type consistency.** `GoldenUpdate.Commit(Scope, string, IReadOnlyList<(string Relative, string Content)>, string?)` — called in Task 10 Step 5 with `artefacts.Select(a => (a.Relative, a.Actual)).ToList()` (tuple names differ but types match; `List<(string, string)>` converts to `IReadOnlyList<(string Relative, string Content)>`), and in `GoldenUpdateTests` with a collection expression of tuples. `Scope(IReadOnlySet<string>)` used by tests; `ParseScope` returns `IReadOnlySet<string>`. `AssertGolden(string, int, int, params (string Relative, string Actual)[])` — every call passes name, code, expected code, then tuples. `JsonShape.AssertEquivalent(string expected, string actual)` — argument order is (expected, actual) everywhere. `MachineOutputTests.Fwd`/`NormalizeJsonPaths` are private statics used within the class only.

**Review Focus.** 1 → `Commit_writes_utf8_without_bom_and_lf_only` (Task 10) plus Task 11 Step 1 on Windows. 2 → `Commit_refuses_an_artefact_set_that_does_not_match_the_group` and the exit-code guard order in `AssertGolden` (Task 10). 3 → `ParseScope_rejects_unknown_empty_and_duplicate_names`, `ParseScope_reports_every_problem_at_once` (Task 10) and Task 11 Step 4. 4 → `Scope_teardown_fails_when_a_requested_test_never_captured` (Task 10) and Task 11 Step 3. 5 → `AssertEquivalent_reports_missing_and_unexpected_properties` (Task 2) and the justification paragraphs before each projection literal.
