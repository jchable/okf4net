# CLI Richer Diagnostics + `--json` Output Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `okf validate` and `okf info` a `--json` output mode, backed by a `Diagnostic` record extended with a stable `DiagnosticCode` and a `Field` name, so external tooling can consume results by contract instead of parsing human-readable text.

**Architecture:** `Diagnostic` (core library, `src/OKF4net/Validate.cs`) gains two additive members (`Code: DiagnosticCode`, `Field: string?`); `ToString()` is untouched, so every byte-exact golden fixture stays identical. The CLI (`src/OKF4net.Cli/`) gets a new `JsonOutput.cs` with plain DTOs and a source-generated `JsonSerializerContext`, and a `--json` flag on `validate`/`info` that serializes those DTOs instead of writing the human-readable text.

**Tech Stack:** C# / .NET 10, `System.Text.Json` (BCL, source-generated serialization — required for Native AOT), xunit.

## Global Constraints

- Zero third-party dependencies (`System.Text.Json` is part of the shared framework — no `PackageReference` needed).
- `TreatWarningsAsErrors`, nullable enabled, file-scoped namespaces, XML doc comments on public/internal API touched by this plan.
- The CLI (`src/OKF4net.Cli`) is published Native AOT (`PublishAot=true`) — JSON serialization MUST use a source-generated `JsonSerializerContext`, never reflection-based `JsonSerializer.Serialize(obj)` without a context, or the AOT publish will emit a trimming/reflection warning that fails the build under `TreatWarningsAsErrors`.
- **Never touch `tests/fixtures/`.** `Diagnostic.ToString()` must not change in any way (verified explicitly by a new test in Task 1).
- `dotnet format OKF4net.sln --verify-no-changes` must stay clean after every task.
- Full suite (currently **839/839** on `dev`) must stay green after every task.
- Design doc: `docs/superpowers/specs/2026-07-31-cli-json-diagnostics-design.md` — the authoritative source for every `DiagnosticCode`/`Field` pairing (§3's table) and the JSON shapes (§4). This plan transcribes both verbatim; if anything here appears to differ from the design doc, the design doc governs and this is a plan bug to flag.
- Base branch: `dev` (`E:/Sources/okf`). Work in an isolated git worktree per `superpowers:using-git-worktrees`.

---

### Task 1: Extend `Diagnostic` with `Code`/`Field` across all 36 emission sites

**Files:**
- Modify: `src/OKF4net/Validate.cs` (the `Diagnostic` record, the new `DiagnosticCode` enum, and every one of the 36 `new Diagnostic(...)` call sites inside `BundleValidator.Validate` and `BundleValidator.ValidateReserved`)
- Test: `tests/OKF4net.Tests/ValidateTests.cs`

**Interfaces:**
- Produces: `public enum DiagnosticCode { UnparseableDocument, MissingType, MissingRecommendedField, GeneratedMissingBy, GeneratedInvalidActor, GeneratedInvalidDate, VerifiedMissingBy, VerifiedInvalidActor, VerifiedInvalidDate, VerifiedEntryNotMapping, VerifiedMalformed, SourceEntryNotMapping, SourcesMalformed, SourceMissingResource, SourceInvalidLastModified, UsageWindowInvalidFrom, UsageWindowInvalidTo, StatusNotScalar, StatusUnknown, StaleAfterInvalid, ConceptStale, LegacyCitations, LegacyTimestamp, ComputationMissingRuntime, ComputationParameterMissingName, ComputationMissingBody, ComputationAmbiguous, ExecutorReceiptInvalid, AttesterResourceEmpty, FrontmatterPathMissing, FrontmatterPathUnsafe, IndexHasFrontmatter, RootIndexExtraFrontmatter, UnsupportedOkfVersion, LogDateInvalid, BrokenLink }` (36 values, in `OKF4net` namespace, same file as `Diagnostic`).
- Produces: `public sealed record Diagnostic(Severity Severity, string? Path, ConceptId? Concept, string Message, DiagnosticCode Code, string? Field = null)` — `Code` has no default (every call site must name one); `Field` defaults to `null` (most diagnostics have none).
- Consumes: nothing new from other tasks (this is the first task).

- [ ] **Step 1: Write failing tests pinning `ToString()` is unaffected by the new fields**

Add to `tests/OKF4net.Tests/ValidateTests.cs` (top of the class, right after the opening brace on line 13):

```csharp
    [Fact]
    public void ToString_ignores_Code_and_Field()
    {
        var withField = new Diagnostic(Severity.Warning, "a.md", null, "msg", DiagnosticCode.LegacyTimestamp, "timestamp");
        var withoutField = new Diagnostic(Severity.Warning, "a.md", null, "msg", DiagnosticCode.LegacyTimestamp);
        Assert.Equal("[warning] a.md: msg", withField.ToString());
        Assert.Equal(withField.ToString(), withoutField.ToString());
    }
```

- [ ] **Step 2: Run the test to verify it fails to compile**

Run: `dotnet build OKF4net.sln 2>&1 | tail -20`
Expected: build FAILS — `DiagnosticCode` does not exist yet, and the `Diagnostic` constructor does not accept 5-6 arguments. This is the RED state (a compile failure, not a runtime assertion failure, since the type doesn't exist yet).

- [ ] **Step 3: Add the `DiagnosticCode` enum and extend the `Diagnostic` record**

In `src/OKF4net/Validate.cs`, replace the `Diagnostic` record (currently right after the `Severity` enum) with:

```csharp
/// <summary>
/// Stable, machine-readable identifier for a specific
/// <see cref="BundleValidator.Validate"/> finding, independent of the
/// human-readable <see cref="Diagnostic.Message"/> text (which may be
/// reworded without notice). One member per distinct diagnostic
/// <see cref="BundleValidator.Validate"/> and <see cref="BundleValidator.ValidateReserved"/>
/// can emit -- see each member's doc comment for the corresponding message
/// and, where applicable, the <see cref="Diagnostic.Field"/> it pairs with.
/// </summary>
public enum DiagnosticCode
{
    /// <summary>A concept document's frontmatter could not be parsed.</summary>
    UnparseableDocument,

    /// <summary>Frontmatter is missing the required <c>type</c> field (§11).</summary>
    MissingType,

    /// <summary>Frontmatter is missing a recommended field (<c>title</c>/<c>description</c>/<c>resource</c>/<c>tags</c>).</summary>
    MissingRecommendedField,

    /// <summary><c>generated</c> is present but missing its required <c>by</c>.</summary>
    GeneratedMissingBy,

    /// <summary><c>generated.by</c> is not a well-formed §7 actor.</summary>
    GeneratedInvalidActor,

    /// <summary><c>generated.at</c> is not ISO-8601.</summary>
    GeneratedInvalidDate,

    /// <summary>A <c>verified</c> entry is missing its required <c>by</c>.</summary>
    VerifiedMissingBy,

    /// <summary><c>verified.by</c> is not a well-formed §7 actor.</summary>
    VerifiedInvalidActor,

    /// <summary><c>verified.at</c> is not ISO-8601.</summary>
    VerifiedInvalidDate,

    /// <summary>A <c>verified</c> list entry is not a <c>{by, at}</c> mapping.</summary>
    VerifiedEntryNotMapping,

    /// <summary><c>verified</c> is neither a <c>{by, at}</c> mapping nor a list of them.</summary>
    VerifiedMalformed,

    /// <summary>A <c>sources</c> list entry is not a mapping.</summary>
    SourceEntryNotMapping,

    /// <summary><c>sources</c> is not a list of entries.</summary>
    SourcesMalformed,

    /// <summary>A <c>sources</c> entry is missing its required <c>resource</c>.</summary>
    SourceMissingResource,

    /// <summary>A <c>sources</c> entry's <c>last_modified</c> is not <c>YYYY-MM-DD</c>.</summary>
    SourceInvalidLastModified,

    /// <summary><c>usage_window.from</c> is not <c>YYYY-MM-DD</c>.</summary>
    UsageWindowInvalidFrom,

    /// <summary><c>usage_window.to</c> is not <c>YYYY-MM-DD</c>.</summary>
    UsageWindowInvalidTo,

    /// <summary><c>status</c> is present but not a scalar.</summary>
    StatusNotScalar,

    /// <summary><c>status</c> is a scalar but not one of <c>draft</c>/<c>stable</c>/<c>deprecated</c>.</summary>
    StatusUnknown,

    /// <summary><c>stale_after</c> is not <c>YYYY-MM-DD</c>.</summary>
    StaleAfterInvalid,

    /// <summary>The concept is past its <c>stale_after</c> date.</summary>
    ConceptStale,

    /// <summary>The body uses the legacy <c># Citations</c> heading instead of the <c>sources</c> frontmatter field (§13.1).</summary>
    LegacyCitations,

    /// <summary>Frontmatter uses the legacy <c>timestamp</c> field instead of <c>generated.at</c> (§13.1).</summary>
    LegacyTimestamp,

    /// <summary>A §10 Attested Computation is missing its required <c>runtime</c>.</summary>
    ComputationMissingRuntime,

    /// <summary>A §10 <c>parameters</c> entry is missing its required <c>name</c>.</summary>
    ComputationParameterMissingName,

    /// <summary>A §10 Attested Computation declares neither an inline <c># Computation</c> fence nor a <c>computation:</c> path.</summary>
    ComputationMissingBody,

    /// <summary>A §10 Attested Computation declares both an inline <c># Computation</c> fence and a <c>computation:</c> path.</summary>
    ComputationAmbiguous,

    /// <summary>§10 <c>executor.receipt</c> is present but not a list of field names.</summary>
    ExecutorReceiptInvalid,

    /// <summary>§10 <c>attester.resource</c> is present but empty.</summary>
    AttesterResourceEmpty,

    /// <summary>A §6.2 path-valued frontmatter field does not resolve to an existing file.</summary>
    FrontmatterPathMissing,

    /// <summary>A §6.2 path-valued frontmatter field resolves outside the bundle root.</summary>
    FrontmatterPathUnsafe,

    /// <summary>A non-root <c>index.md</c> declares frontmatter, which §8 reserves for the bundle-root index only.</summary>
    IndexHasFrontmatter,

    /// <summary>The bundle-root <c>index.md</c>'s frontmatter declares keys other than <c>okf_version</c> (§12).</summary>
    RootIndexExtraFrontmatter,

    /// <summary>The bundle-root <c>index.md</c> declares an <c>okf_version</c> this build does not recognize.</summary>
    UnsupportedOkfVersion,

    /// <summary>A <c>log.md</c> date heading is not ISO-8601 <c>YYYY-MM-DD</c> (§9).</summary>
    LogDateInvalid,

    /// <summary>A cross-link target does not resolve to a concept in the bundle (§6; permitted, reported as <see cref="Severity.Info"/>).</summary>
    BrokenLink,
}

/// <summary>
/// A single finding about a bundle: <see cref="Path"/> and
/// <see cref="Concept"/> are each populated only when the finding relates to a
/// file or a concept respectively (never both, per
/// <see cref="BundleValidator.Validate"/>). <see cref="Code"/> is a stable
/// identifier independent of <see cref="Message"/>'s exact wording;
/// <see cref="Field"/> names the specific frontmatter key involved, when the
/// diagnostic is about one (<see langword="null"/> for body-level or
/// file-level findings).
/// </summary>
public sealed record Diagnostic(Severity Severity, string? Path, ConceptId? Concept, string Message, DiagnosticCode Code, string? Field = null)
{
    /// <summary>
    /// Renders as <c>[severity] path: message</c> or <c>[severity] concept:
    /// message</c> (falling back to a bare <c>[severity] message</c> if
    /// neither is set). Unaffected by <see cref="Code"/> or <see cref="Field"/>
    /// -- this is the exact text every byte-exact golden fixture pins.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(SeverityText(Severity)).Append("] ");
        if (Path is not null)
        {
            sb.Append(Path).Append(": ");
        }
        else if (Concept is not null)
        {
            sb.Append(Concept).Append(": ");
        }

        sb.Append(Message);
        return sb.ToString();
    }

    /// <summary>Lower-case severity label.</summary>
    private static string SeverityText(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        Severity.Info => "info",
        _ => severity.ToString(),
    };
}
```

- [ ] **Step 4: Update all 36 emission sites**

Every `new Diagnostic(...)` call in `BundleValidator.Validate` and `BundleValidator.ValidateReserved` gets a `DiagnosticCode` argument (and a `Field` string where the design doc's table has one). Apply each of the following replacements in `src/OKF4net/Validate.cs` (original text on the left of `→`, new text on the right -- these are exact `old_string`/`new_string` pairs, apply each independently):

1. `"unparseable concept document: {error}"));` → `"unparseable concept document: {error}",
                DiagnosticCode.UnparseableDocument));`
2. `"missing required frontmatter field \`type\`"));` → `"missing required frontmatter field \`type\`",
                    DiagnosticCode.MissingType,
                    "type"));`
3. `$"missing recommended frontmatter field \`{field}\`"));` → `$"missing recommended frontmatter field \`{field}\`",
                        DiagnosticCode.MissingRecommendedField,
                        field));`
4. `"generated is missing required \`by\`"));` → `"generated is missing required \`by\`", DiagnosticCode.GeneratedMissingBy, "generated.by"));`
5. `$"generated.by is not a valid §7 actor: {DebugQuote.Quote(g.By.Value.Raw)}"));` → `$"generated.by is not a valid §7 actor: {DebugQuote.Quote(g.By.Value.Raw)}", DiagnosticCode.GeneratedInvalidActor, "generated.by"));`
6. `$"generated.at is not ISO-8601: {DebugQuote.Quote(gat)}"));` → `$"generated.at is not ISO-8601: {DebugQuote.Quote(gat)}", DiagnosticCode.GeneratedInvalidDate, "generated.at"));`
7. `"verified entry is missing \`by\`"));` → `"verified entry is missing \`by\`", DiagnosticCode.VerifiedMissingBy, "verified.by"));`
8. `$"verified.by is not a valid §7 actor: {DebugQuote.Quote(stamp.By.Value.Raw)}"));` → `$"verified.by is not a valid §7 actor: {DebugQuote.Quote(stamp.By.Value.Raw)}", DiagnosticCode.VerifiedInvalidActor, "verified.by"));`
9. `$"verified.at is not ISO-8601: {DebugQuote.Quote(vat)}"));` → `$"verified.at is not ISO-8601: {DebugQuote.Quote(vat)}", DiagnosticCode.VerifiedInvalidDate, "verified.at"));`
10. `"verified entry is not a \`{by, at}\` mapping"));` → `"verified entry is not a \`{by, at}\` mapping", DiagnosticCode.VerifiedEntryNotMapping, "verified"));`
11. `"verified must be a \`{by, at}\` mapping or a list of them"));` → `"verified must be a \`{by, at}\` mapping or a list of them", DiagnosticCode.VerifiedMalformed, "verified"));`
12. `"source entry is not a mapping"));` → `"source entry is not a mapping", DiagnosticCode.SourceEntryNotMapping, "sources"));`
13. `"sources must be a list of entries"));` → `"sources must be a list of entries", DiagnosticCode.SourcesMalformed, "sources"));`
14. `"source entry is missing required \`resource\`"));` → `"source entry is missing required \`resource\`", DiagnosticCode.SourceMissingResource, "sources.resource"));`
15. `$"source last_modified is not \`YYYY-MM-DD\`: {DebugQuote.Quote(lastModified)}"));` → `$"source last_modified is not \`YYYY-MM-DD\`: {DebugQuote.Quote(lastModified)}", DiagnosticCode.SourceInvalidLastModified, "sources.last_modified"));`
16. `$"usage_window from is not \`YYYY-MM-DD\`: {DebugQuote.Quote(uf)}"));` → `$"usage_window from is not \`YYYY-MM-DD\`: {DebugQuote.Quote(uf)}", DiagnosticCode.UsageWindowInvalidFrom, "usage_window.from"));`
17. `$"usage_window to is not \`YYYY-MM-DD\`: {DebugQuote.Quote(ut)}"));` → `$"usage_window to is not \`YYYY-MM-DD\`: {DebugQuote.Quote(ut)}", DiagnosticCode.UsageWindowInvalidTo, "usage_window.to"));`
18. `"status is not a scalar \`draft|stable|deprecated\`"));` → `"status is not a scalar \`draft|stable|deprecated\`", DiagnosticCode.StatusNotScalar, "status"));`
19. `$"unknown status {DebugQuote.Quote(fm.Get("status")!.AsDisplayString() ?? string.Empty)}; treated as stable"));` → `$"unknown status {DebugQuote.Quote(fm.Get("status")!.AsDisplayString() ?? string.Empty)}; treated as stable", DiagnosticCode.StatusUnknown, "status"));`
20. `$"stale_after is not \`YYYY-MM-DD\`: {DebugQuote.Quote(lc.StaleAfterRaw!)}"));` → `$"stale_after is not \`YYYY-MM-DD\`: {DebugQuote.Quote(lc.StaleAfterRaw!)}", DiagnosticCode.StaleAfterInvalid, "stale_after"));`
21. `$"concept is stale (stale_after {lc.StaleAfterRaw})"));` → `$"concept is stale (stale_after {lc.StaleAfterRaw})", DiagnosticCode.ConceptStale, "stale_after"));`
22. `"body \`# Citations\` is legacy; move provenance to the \`sources\` frontmatter field"));` → `"body \`# Citations\` is legacy; move provenance to the \`sources\` frontmatter field", DiagnosticCode.LegacyCitations));`
23. `` "`timestamp` is a legacy field; prefer `generated.at`"));`` → `` "`timestamp` is a legacy field; prefer `generated.at`", DiagnosticCode.LegacyTimestamp, "timestamp"));``
24. `"attested computation missing required 'runtime'"));` → `"attested computation missing required 'runtime'", DiagnosticCode.ComputationMissingRuntime, "runtime"));`
25. `"parameter entry missing 'name'"));` → `"parameter entry missing 'name'", DiagnosticCode.ComputationParameterMissingName, "parameters"));`
26. `"attested computation has no computation (inline '# Computation' or 'computation:' path)"));` → `"attested computation has no computation (inline '# Computation' or 'computation:' path)", DiagnosticCode.ComputationMissingBody));`
27. `"computation specified both inline and via 'computation:'"));` → `"computation specified both inline and via 'computation:'", DiagnosticCode.ComputationAmbiguous, "computation"));`
28. `"executor.receipt is not a list of receipt field names"));` → `"executor.receipt is not a list of receipt field names", DiagnosticCode.ExecutorReceiptInvalid, "executor.receipt"));`
29. `"attester.resource is empty"));` → `"attester.resource is empty", DiagnosticCode.AttesterResourceEmpty, "attester.resource"));`
30. `$"frontmatter path '{resource.Field}' → '{resource.RawPath}' not found"));` → `$"frontmatter path '{resource.Field}' → '{resource.RawPath}' not found", DiagnosticCode.FrontmatterPathMissing, resource.Field));`
31. `$"frontmatter path '{resource.Field}' → '{resource.RawPath}' escapes the bundle"));` → `$"frontmatter path '{resource.Field}' → '{resource.RawPath}' escapes the bundle", DiagnosticCode.FrontmatterPathUnsafe, resource.Field));`
32. `"index.md should not contain frontmatter (§8)"));` → `"index.md should not contain frontmatter (§8)",
                    DiagnosticCode.IndexHasFrontmatter));`
33. `"root index.md frontmatter should declare only \`okf_version\` (§12)"));` → `"root index.md frontmatter should declare only \`okf_version\` (§12)",
                        DiagnosticCode.RootIndexExtraFrontmatter,
                        "okf_version"));`
34. `$"declared okf_version {DebugQuote.Quote(declaredVersion)} is not supported; consuming best-effort as v{OkfSpec.Version}"));` → `$"declared okf_version {DebugQuote.Quote(declaredVersion)} is not supported; consuming best-effort as v{OkfSpec.Version}",
                        DiagnosticCode.UnsupportedOkfVersion,
                        "okf_version"));`
35. `$"log date heading is not ISO-8601 \`YYYY-MM-DD\`: {DebugQuote.Quote(bad)}"));` → `$"log date heading is not ISO-8601 \`YYYY-MM-DD\`: {DebugQuote.Quote(bad)}",
                    DiagnosticCode.LogDateInvalid));`
36. `$"link target does not resolve to a concept in the bundle: {raw}"));` → `$"link target does not resolve to a concept in the bundle: {raw}",
                DiagnosticCode.BrokenLink));`

Some of these substrings appear inside multi-line `new Diagnostic(...)` calls (e.g. #1, #32, #35, #36 use the multi-line constructor form seen in the original file, ending in `Severity.X,\n    path,\n    concept,\n    "message"));`) -- when the substring match is ambiguous within a single call, include enough of the preceding lines (the message string is always unique across the file) to anchor the edit precisely; every message string above is verified unique in the file.

- [ ] **Step 5: Run the build to confirm it compiles, then run the new test**

Run: `dotnet build OKF4net.sln 2>&1 | tail -10`
Expected: 0 warnings, 0 errors.

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ToString_ignores_Code_and_Field"`
Expected: PASS.

- [ ] **Step 6: Add `Code`/`Field` assertions to existing per-rule tests, and new tests for any gap**

`ValidateTests.cs`'s own class doc comment states each existing test "targets exactly one diagnostic-producing rule." For each of the 36 `DiagnosticCode` values, find the existing test exercising that exact diagnostic (match by the `diag.Message` substring each test already asserts) and add an assertion on `diag.Code` (and `diag.Field` where the code has one per Step 4's table) right next to the existing message assertion. Where no existing test covers a code, add a new minimal `[Fact]` following the file's established pattern (`TempDir` + `Bundle.Load` + `BundleValidator.Validate` + assert on the single matching diagnostic), named `<Thing>_has_the_right_code` or similar, matching the file's naming style.

Do this by grepping the test file for each message substring first to locate the right test, e.g.:
```sh
grep -n "generated is missing required\|verified.by is not a valid\|source last_modified\|usage_window\|stale_after\|Citations\|timestamp.*legacy\|attested computation\|parameter entry\|receipt is not a list\|attester.resource\|frontmatter path\|index.md should not\|declare only\|not supported\|log date heading\|does not resolve to a concept" tests/OKF4net.Tests/ValidateTests.cs
```

- [ ] **Step 7: Verify exhaustiveness**

Run: `grep -o "DiagnosticCode\.[A-Za-z]*" tests/OKF4net.Tests/ValidateTests.cs | sort -u | wc -l`
Expected: `36` (every one of the 36 `DiagnosticCode` values is referenced by at least one test's assertion). If fewer, a code was missed in Step 6 -- go back and add the missing assertion(s).

- [ ] **Step 8: Run the full suite and format check**

Run: `dotnet test OKF4net.sln 2>&1 | tail -6` — expect all passing, no regressions (baseline 839 + this task's new tests).
Run: `dotnet format OKF4net.sln --verify-no-changes` — expect clean.

- [ ] **Step 9: Commit**

```bash
git add src/OKF4net/Validate.cs tests/OKF4net.Tests/ValidateTests.cs
git commit -m "feat(core): add DiagnosticCode and Field to Diagnostic

Additive only -- ToString() unchanged, byte-exact golden fixtures
untouched. One code per distinct BundleValidator diagnostic (36
values), Field names the frontmatter key involved where applicable."
```

---

### Task 2: `--json` output for `validate` and `info`

**Files:**
- Create: `src/OKF4net.Cli/JsonOutput.cs`
- Modify: `src/OKF4net.Cli/OkfCli.cs:214-294` (`CmdValidate`, `CmdInfo`)
- Test: `tests/OKF4net.Tests/CliTests.cs`

**Interfaces:**
- Consumes: `Diagnostic.Code` (`DiagnosticCode`) and `Diagnostic.Field` (`string?`) from Task 1; `ValidationReport.Diagnostics`/`ErrorCount`/`WarningCount`/`Of(Severity)`, `Bundle.Root`/`OkfVersion`/`Count`/`IndexFiles`/`LogFiles`/`Concepts`/`ParseErrors`/`BrokenLinks()`/`LinksFrom(ConceptId)` (all pre-existing).
- Produces: `internal static class JsonOutput` with two methods, `WriteValidate(TextWriter stdout, string bundlePath, Bundle bundle, ValidationReport report)` and `WriteInfo(TextWriter stdout, string bundlePath, Bundle bundle)`, called from `OkfCli.CmdValidate`/`OkfCli.CmdInfo`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/OKF4net.Tests/CliTests.cs` (after the existing `Info_prints_summary` test, or any convenient spot in the class):

```csharp
    [Fact]
    public void Validate_json_reports_bundle_conformance_and_diagnostics()
    {
        var r = Run("validate", "--json", BundlePath);
        Assert.Equal(0, r.Code);

        using var doc = System.Text.Json.JsonDocument.Parse(r.Out);
        var root = doc.RootElement;
        Assert.Equal(BundlePath, root.GetProperty("bundle").GetString());
        Assert.True(root.GetProperty("conformant").GetBoolean());
        Assert.Equal(4, root.GetProperty("conceptCount").GetInt32());
        Assert.Equal(0, root.GetProperty("errorCount").GetInt32());
        var diagnostics = root.GetProperty("diagnostics");
        Assert.True(diagnostics.GetArrayLength() > 0);
        var first = diagnostics[0];
        Assert.True(first.TryGetProperty("severity", out _));
        Assert.True(first.TryGetProperty("code", out _));
        Assert.True(first.TryGetProperty("message", out _));
    }

    [Fact]
    public void Validate_json_diagnostic_field_is_populated_when_applicable()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
        var r = Run("validate", "--json", tmp.Path);

        using var doc = System.Text.Json.JsonDocument.Parse(r.Out);
        var diagnostics = doc.RootElement.GetProperty("diagnostics");
        var timestampDiag = diagnostics.EnumerateArray().Single(d => d.GetProperty("code").GetString() == "LegacyTimestamp");
        Assert.Equal("timestamp", timestampDiag.GetProperty("field").GetString());
    }

    [Fact]
    public void Info_json_reports_bundle_summary()
    {
        var r = Run("info", "--json", BundlePath);
        Assert.Equal(0, r.Code);

        using var doc = System.Text.Json.JsonDocument.Parse(r.Out);
        var root = doc.RootElement;
        Assert.Equal(4, root.GetProperty("conceptCount").GetInt32());
        Assert.True(root.TryGetProperty("types", out var types));
        Assert.True(types.EnumerateObject().Any());
        Assert.True(root.TryGetProperty("linkCount", out _));
        Assert.True(root.TryGetProperty("brokenLinkCount", out _));
    }

    [Fact]
    public void Info_json_types_is_present_and_empty_for_a_bundle_with_no_concepts()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n");
        var r = Run("info", "--json", tmp.Path);

        using var doc = System.Text.Json.JsonDocument.Parse(r.Out);
        Assert.True(doc.RootElement.TryGetProperty("types", out var types));
        Assert.Equal(0, types.EnumerateObject().Count());
    }

    [Fact]
    public void Validate_text_output_is_unchanged_by_the_json_feature()
    {
        var r = Run("validate", BundlePath);
        Assert.Equal(0, r.Code);
        Assert.Contains("conformant with OKF v0.2", r.Out);
        Assert.DoesNotContain("{", r.Out);
    }

    [Fact]
    public void Info_text_output_is_unchanged_by_the_json_feature()
    {
        var r = Run("info", BundlePath);
        Assert.Equal(0, r.Code);
        Assert.Contains("concepts:   4", r.Out);
        Assert.DoesNotContain("{", r.Out);
    }
```

Add `using System.Linq;` at the top of `CliTests.cs` if not already implicitly available (the project has `ImplicitUsings` enabled for the test project, which already includes `System.Linq` — verify by building; no action needed if it already resolves).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~Validate_json|FullyQualifiedName~Info_json"`
Expected: FAIL (compile error or `--json` treated as an unhandled flag/positional-arg confusion) — `--json` does not exist yet.

- [ ] **Step 3: Create the JSON DTOs and source-generated serializer context**

Create `src/OKF4net.Cli/JsonOutput.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OKF4net.Cli;

/// <summary>One <see cref="Diagnostic"/>, projected for <c>--json</c> output.</summary>
internal sealed record DiagnosticJson(
    string Severity,
    string Code,
    string? Path,
    string? ConceptId,
    string? Field,
    string Message);

/// <summary>The full result of <c>okf validate --json</c>.</summary>
internal sealed record ValidateJsonResult(
    string Bundle,
    bool Conformant,
    int ConceptCount,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    IReadOnlyList<DiagnosticJson> Diagnostics);

/// <summary>One unparseable file, projected for <c>--json</c> output.</summary>
internal sealed record ParseErrorJson(string Path, string Message);

/// <summary>The full result of <c>okf info --json</c>.</summary>
internal sealed record InfoJsonResult(
    string Bundle,
    string? OkfVersion,
    int ConceptCount,
    int IndexFileCount,
    int LogFileCount,
    IReadOnlyDictionary<string, int> Types,
    int LinkCount,
    int BrokenLinkCount,
    IReadOnlyList<ParseErrorJson> ParseErrors);

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for every
/// <c>--json</c> output type. Required, not optional, because the CLI is
/// published Native AOT (<c>PublishAot</c>): reflection-based
/// <see cref="JsonSerializer.Serialize{T}(T, JsonSerializerOptions?)"/>
/// without a context is not trim-safe and some of its reflection APIs do
/// not work under Native AOT at all. camelCase property names.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ValidateJsonResult))]
[JsonSerializable(typeof(InfoJsonResult))]
internal partial class CliJsonContext : JsonSerializerContext
{
}

/// <summary>Builds and writes the <c>--json</c> output for <c>validate</c> and <c>info</c>.</summary>
internal static class JsonOutput
{
    /// <summary>Writes <c>okf validate --json</c>'s result to <paramref name="stdout"/> as a single line-terminated JSON document.</summary>
    internal static void WriteValidate(TextWriter stdout, string bundlePath, Bundle bundle, ValidationReport report)
    {
        var diagnostics = report.Diagnostics
            .Select(d => new DiagnosticJson(
                SeverityText(d.Severity),
                d.Code.ToString(),
                d.Path,
                d.Concept?.ToString(),
                d.Field,
                d.Message))
            .ToList();

        var result = new ValidateJsonResult(
            bundlePath,
            report.IsConformant,
            bundle.Count,
            report.ErrorCount,
            report.WarningCount,
            report.Of(Severity.Info).Count(),
            diagnostics);

        stdout.Write(JsonSerializer.Serialize(result, CliJsonContext.Default.ValidateJsonResult));
        stdout.Write("\n");
    }

    /// <summary>Writes <c>okf info --json</c>'s result to <paramref name="stdout"/> as a single line-terminated JSON document.</summary>
    internal static void WriteInfo(TextWriter stdout, string bundlePath, Bundle bundle)
    {
        var byType = new Dictionary<string, int>();
        foreach (var c in bundle.Concepts)
        {
            var t = c.Document.Frontmatter.Type ?? "(none)";
            byType[t] = byType.GetValueOrDefault(t) + 1;
        }

        var totalLinks = 0;
        foreach (var c in bundle.Concepts)
        {
            totalLinks += bundle.LinksFrom(c.Id).Count;
        }

        var parseErrors = bundle.ParseErrors
            .Select(pe => new ParseErrorJson(pe.Path, pe.Error))
            .ToList();

        var result = new InfoJsonResult(
            bundlePath,
            bundle.OkfVersion,
            bundle.Count,
            bundle.IndexFiles.Count,
            bundle.LogFiles.Count,
            byType,
            totalLinks,
            bundle.BrokenLinks().Count,
            parseErrors);

        stdout.Write(JsonSerializer.Serialize(result, CliJsonContext.Default.InfoJsonResult));
        stdout.Write("\n");
    }

    private static string SeverityText(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        Severity.Info => "info",
        _ => severity.ToString(),
    };
}
```

`bundle.ParseErrors` is declared in `src/OKF4net/Bundle.cs:231` as `IReadOnlyList<(string Path, string Error)>` (verified against source) -- `pe.Path`/`pe.Error` above are the correct named-tuple element names, used as-is.

The exact code above was compiled and run standalone (a throwaway console project mirroring the CLI's `PublishAot`/`TreatWarningsAsErrors` settings) before being written into this plan: it builds with 0 warnings, nested record types (`DiagnosticJson`, `ParseErrorJson`) and `IReadOnlyDictionary<string, int>` are covered by the source generator with no extra attributes needed, and the produced JSON matches §4's shape exactly. One characteristic confirmed while doing that: `System.Text.Json`'s default encoder unicode-escapes the backtick character found in message text (its default HTML-safety behavior) instead of emitting it literally -- valid, spec-compliant JSON either way, and not worth a custom encoder (source-generated contexts only take one via a hand-constructed `JsonSerializerOptions` passed to the context's constructor, not the `[JsonSourceGenerationOptions]` attribute, which would add real complexity for a cosmetic-only change). Leave the default encoder; do not "fix" this escaping.

- [ ] **Step 4: Wire `--json` into `CmdValidate` and `CmdInfo`**

In `src/OKF4net.Cli/OkfCli.cs`, replace the `CmdValidate` method body:

```csharp
    /// <summary>Implements the <c>validate</c> subcommand.</summary>
    private static int CmdValidate(string[] args, TextWriter stdout)
    {
        var path = Positional(args, "<bundle>");
        var bundle = Load(path);
        var report = BundleValidator.Validate(bundle);

        if (HasFlag(args, "--json"))
        {
            JsonOutput.WriteValidate(stdout, path, bundle, report);
            return report.IsConformant ? 0 : 1;
        }

        foreach (var d in report.Diagnostics)
        {
            stdout.Write(d.ToString());
            stdout.Write("\n");
        }

        var errors = report.ErrorCount;
        var warnings = report.WarningCount;
        var infos = report.Of(Severity.Info).Count();
        stdout.Write($"\n{bundle.Count} concept(s); {errors} error(s), {warnings} warning(s), {infos} info.\n");

        if (report.IsConformant)
        {
            stdout.Write($"✓ conformant with OKF v{OkfSpec.Version}\n");
            return 0;
        }

        stdout.Write($"✗ not conformant with OKF v{OkfSpec.Version}\n");
        return 1;
    }
```

Replace the `CmdInfo` method body:

```csharp
    /// <summary>Implements the <c>info</c> subcommand.</summary>
    private static int CmdInfo(string[] args, TextWriter stdout)
    {
        var path = Positional(args, "<bundle>");
        var bundle = Load(path);

        if (HasFlag(args, "--json"))
        {
            JsonOutput.WriteInfo(stdout, path, bundle);
            return 0;
        }

        stdout.Write($"bundle:     {bundle.Root}\n");
        var okfVersion = bundle.OkfVersion;
        if (okfVersion is not null)
        {
            stdout.Write($"okf_version: {okfVersion}\n");
        }

        stdout.Write($"concepts:   {bundle.Count}\n");
        stdout.Write($"index.md:   {bundle.IndexFiles.Count}\n");
        stdout.Write($"log.md:     {bundle.LogFiles.Count}\n");

        var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in bundle.Concepts)
        {
            var t = c.Document.Frontmatter.Type ?? "(none)";
            byType[t] = byType.GetValueOrDefault(t) + 1;
        }

        if (byType.Count > 0)
        {
            stdout.Write("\ntypes:\n");
            foreach (var (t, n) in byType)
            {
                stdout.Write($"  {n,4}  {t}\n");
            }
        }

        var broken = bundle.BrokenLinks();
        var totalLinks = 0;
        foreach (var c in bundle.Concepts)
        {
            totalLinks += bundle.LinksFrom(c.Id).Count;
        }

        stdout.Write($"\nlinks:      {totalLinks} internal ({broken.Count} broken)\n");

        if (bundle.ParseErrors.Count > 0)
        {
            stdout.Write("\nunparseable files:\n");
            foreach (var (p, e) in bundle.ParseErrors)
            {
                stdout.Write($"  {p}: {e}\n");
            }
        }

        return 0;
    }
```

Also update the `Usage` constant (near the top of the file) to mention the new flag -- append to the `OPTIONS:` block:

```
        "    -h, --help           Show this help\n" +
        "    -V, --version        Show version\n" +
        "        --json           Machine-readable output for validate/info";
```

(Match this exactly against the existing `Usage` string's surrounding lines when applying -- the current final line has no trailing `\n` before the closing quote; preserve that convention, only inserting the new line before it.)

- [ ] **Step 5: Run the new tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~Validate_json|FullyQualifiedName~Info_json|FullyQualifiedName~text_output_is_unchanged"`
Expected: PASS, all 6 new tests.

- [ ] **Step 6: Run the full suite, golden parity, and format check**

Run: `dotnet test OKF4net.sln 2>&1 | tail -6` — expect all passing.
Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~GoldenParityTests"` — expect unchanged (7/7), confirming the byte-exact fixtures are untouched.
Run: `dotnet format OKF4net.sln --verify-no-changes` — expect clean.

- [ ] **Step 7: Verify the Native AOT publish stays clean**

Run: `dotnet publish src/OKF4net.Cli -c Release 2>&1 | tail -30`
Expected: 0 warnings (in particular, no `IL2026`/`IL3050`-style trimming/reflection warnings from the JSON serialization). If any appear, the source-generated context is missing a `[JsonSerializable]` for a type it reaches, or something is falling back to reflection-based serialization -- do not suppress the warning, fix the context.

Run: `./src/OKF4net.Cli/bin/Release/net10.0/<rid>/publish/okf validate --json tests/fixtures/appendix_a` (substitute the actual published binary path/RID for your platform) to confirm the AOT-published binary's `--json` output actually works end-to-end, not just the JIT-run test suite.

- [ ] **Step 8: Commit**

```bash
git add src/OKF4net.Cli/JsonOutput.cs src/OKF4net.Cli/OkfCli.cs tests/OKF4net.Tests/CliTests.cs
git commit -m "feat(cli): add --json output to validate and info

Source-generated JsonSerializerContext (Native AOT-safe, camelCase).
Text output is byte-for-byte unchanged when --json is absent -- pinned
by two new regression tests and the untouched golden fixture suite."
```

---

### Task 3: CHANGELOG entry and final verification

**Files:**
- Modify: `CHANGELOG.md`

**Interfaces:** None — documentation-only.

- [ ] **Step 1: Add the CHANGELOG entry**

In `CHANGELOG.md`, under `## [Unreleased]`, add (creating an `### Added` section if none exists yet under `[Unreleased]`):

```markdown
### Added

- **`okf validate`/`okf info` gain a `--json` flag** for machine-readable
  output (camelCase, source-generated for Native AOT). `Diagnostic` gains
  a stable `Code` (`DiagnosticCode`, one per distinct validator finding)
  and a `Field` naming the frontmatter key involved, additive only --
  `Diagnostic.ToString()`'s text output is unchanged.
```

- [ ] **Step 2: Full verification**

Run: `dotnet build OKF4net.sln 2>&1 | tail -10` — expect 0 warnings.
Run: `dotnet test OKF4net.sln 2>&1 | tail -6` — expect all passing (baseline 839 + this plan's new tests: 1 from Task 1's `ToString_ignores_Code_and_Field` + however many Task 1's Step 6 added for coverage gaps + 6 from Task 2).
Run: `dotnet format OKF4net.sln --verify-no-changes` — expect clean.

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): note the --json/DiagnosticCode CLI ergonomics work"
```

---

## Post-plan

Once Task 3 is committed and the full suite is green, use `superpowers:finishing-a-development-branch` to integrate this plan's branch back into `dev`.
