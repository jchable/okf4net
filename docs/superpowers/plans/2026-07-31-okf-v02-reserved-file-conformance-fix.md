# §11 conformance fix for malformed reserved files — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `BundleValidator.ValidateReserved` (`src/OKF4net/Validate.cs`) correctly enforce §11 condition 3 — a malformed reserved file (`index.md`/`log.md`) makes a bundle non-conformant (`Severity.Error`, `IsConformant == false`, `okf validate` exit code `1`), for every failure mode, including the two that currently produce no diagnostic at all.

**Architecture:** All production-code change is confined to one method, `ValidateReserved` (`Validate.cs:507-617`), plus two new `DiagnosticCode` enum members. Every currently-silent `continue` in its two per-file loops gets a `Severity.Error` diagnostic before continuing; three already-detected violations (`IndexHasFrontmatter`, `RootIndexExtraFrontmatter`, `LogDateInvalid`) flip from `Severity.Warning` to `Severity.Error`; `UnsupportedOkfVersion` stays `Severity.Warning`, unchanged (§12 explicitly quarantines it from conformance judgment). `Bundle.Load` and everything upstream of `ValidateReserved` is untouched.

**Tech Stack:** C# / .NET 10, xunit. No new dependencies (zero-dependency `OKF4net` core project).

## Global Constraints

- **Zero third-party runtime dependencies** in `src/OKF4net/` — this plan adds no dependency, only enum members and diagnostic-construction code using types already in the file.
- **`TreatWarningsAsErrors`, nullable enabled, XML doc comments on all public API** (`Directory.Build.props`) — the two new `DiagnosticCode` members need doc comments (see Task 1).
- **Never edit files under `tests/fixtures/` to make a failing test pass for v0.1-covered behavior.** This plan's fixture changes are exclusively **additions** (a new bundle directory, new golden `.out`/`.exitcode` files) — verified during design review against the full fixture/bundle tree that no existing fixture's expected output changes. If implementation somehow reveals an existing fixture IS affected, stop and flag it rather than editing it silently — the user has pre-authorized editing an existing fixture for this specific fix if that turns out to be necessary, but treat it as a "stop and confirm," not a default path.
- **New golden fixtures must be hand-verified against the actual current `BundleValidator` behavior, not hand-typed/assumed** — captured by actually running the built CLI and reading every line against the exact message/severity the code emits, per this repo's own established fixture convention (`tests/fixtures/README.md`).
- **CHANGELOG entry must use a `**Breaking:**` lead-in under `### Changed`**, not a plain `Fixed` bullet — this is a CLI-exit-code-changing behavior fix, and per `.claude/skills/release/SKILL.md`'s semver policy the eventual release must be versioned minor, not patch.
- Source design doc: `docs/superpowers/specs/2026-07-31-okf-v02-reserved-file-conformance-fix-design.md` — re-read the relevant section if anything below is ambiguous.

---

### Task 1: `ValidateReserved` diagnostic fix + unit tests

**Files:**
- Modify: `src/OKF4net/Validate.cs` (`DiagnosticCode` enum, `ValidateReserved` method)
- Modify: `tests/OKF4net.Tests/ValidateTests.cs`

**Interfaces:**
- Produces: `DiagnosticCode.UnparseableIndex`, `DiagnosticCode.UnparseableLog` — new enum members. `ValidateReserved` now emits `Severity.Error` for: an unreadable/unparseable `index.md`, a non-root `index.md` with frontmatter, a root `index.md` with extra keys, an unreadable `log.md`, and a non-ISO-8601 `log.md` date heading. `UnsupportedOkfVersion` stays `Severity.Warning`.
- Consumes: nothing new — uses `OkfDocument`, `DocumentParseException`, `ChangeLog`, `Bundle` exactly as `ValidateReserved` already does.

- [ ] **Step 1: Write the failing tests (new + updated)**

In `tests/OKF4net.Tests/ValidateTests.cs`, add one new test near `Unparseable_frontmatter_is_an_error` (after line 37):

```csharp
[Fact]
public void Unparseable_index_is_an_error()
{
    using var tmp = new TempDir();
    tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
    tmp.Write("broken/index.md", "---\ntitle: [unterminated\n---\n\n# Listing\n");
    var bundle = Bundle.Load(tmp.Path);
    var report = BundleValidator.Validate(bundle);

    var diag = Assert.Single(report.Of(Severity.Error).Where(d => d.Code == DiagnosticCode.UnparseableIndex));
    Assert.StartsWith("unparseable index.md: ", diag.Message);
    Assert.False(report.IsConformant);
}
```

Then rename and update the three tests whose expectations change from Warning to
Error (leave every other line of each test unchanged except what's shown):

`Nonroot_index_with_frontmatter_is_a_warning` (lines 139-152) →

```csharp
[Fact]
public void Nonroot_index_with_frontmatter_is_an_error()
{
    // frontmatter is only permitted in the bundle-root index.md.
    using var tmp = new TempDir();
    tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
    tmp.Write("sub/index.md", "---\ntitle: nope\n---\n\n# Listing\n");
    var bundle = Bundle.Load(tmp.Path);
    var report = BundleValidator.Validate(bundle);

    var diag = Assert.Single(report.Of(Severity.Error));
    Assert.Equal("index.md should not contain frontmatter (§8)", diag.Message);
    Assert.Equal(DiagnosticCode.IndexHasFrontmatter, diag.Code);
    Assert.False(report.IsConformant);
}
```

`Root_index_frontmatter_with_extra_keys_is_a_warning` (lines 166-176) →

```csharp
[Fact]
public void Root_index_frontmatter_with_extra_keys_is_an_error()
{
    using var tmp = new TempDir();
    tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
    tmp.Write("index.md", "---\nokf_version: \"0.2\"\ntitle: extra\n---\n\n# Listing\n");
    var bundle = Bundle.Load(tmp.Path);
    var report = BundleValidator.Validate(bundle);

    Assert.Contains(report.Of(Severity.Error), d => d.Message == "root index.md frontmatter should declare only `okf_version` (§12)" && d.Code == DiagnosticCode.RootIndexExtraFrontmatter && d.Field == "okf_version");
    Assert.False(report.IsConformant);
}
```

`Invalid_log_date_heading_is_a_warning` (lines 190-203) →

```csharp
[Fact]
public void Invalid_log_date_heading_is_an_error()
{
    using var tmp = new TempDir();
    tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
    tmp.Write("log.md", "# Log\n\n## not-a-date\n* **Update**: did a thing.\n");
    var bundle = Bundle.Load(tmp.Path);
    var report = BundleValidator.Validate(bundle);

    var diag = Assert.Single(report.Of(Severity.Error));
    Assert.Equal("log date heading is not ISO-8601 `YYYY-MM-DD`: \"not-a-date\"", diag.Message);
    Assert.Equal(DiagnosticCode.LogDateInvalid, diag.Code);
    Assert.False(report.IsConformant);
}
```

Leave `Root_index_frontmatter_with_only_okf_version_is_clean`,
`Index_with_no_frontmatter_produces_no_diagnostic`,
`Valid_log_date_heading_produces_no_warning`, and
`Root_okf_version_other_than_current_warns` (the `UnsupportedOkfVersion`
control test, `ValidateTests.cs:430-438`) untouched — none of them assert
anything this task changes.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ValidateTests"`

Expected: `Unparseable_index_is_an_error` FAILs (`Assert.Single` finds zero
matching diagnostics — today's code silently `continue`s past the parse
failure). `Nonroot_index_with_frontmatter_is_an_error`,
`Root_index_frontmatter_with_extra_keys_is_an_error`, and
`Invalid_log_date_heading_is_an_error` all FAIL (`Assert.False(report.IsConformant)`
fails because today's code only ever emits `Severity.Warning` for these,
so `IsConformant` is still `true`).

- [ ] **Step 3: Add the two `DiagnosticCode` enum members**

In `src/OKF4net/Validate.cs`, in the `DiagnosticCode` enum, insert
`UnparseableIndex` immediately before `IndexHasFrontmatter` and
`UnparseableLog` immediately before `LogDateInvalid` (grouping each with
its file type):

```csharp
    /// <summary>A reserved <c>index.md</c> could not be read or parsed (§8, §11).</summary>
    UnparseableIndex,

    /// <summary>A non-root <c>index.md</c> declares frontmatter, which §8 reserves for the bundle-root index only.</summary>
    IndexHasFrontmatter,

    /// <summary>The bundle-root <c>index.md</c>'s frontmatter declares keys other than <c>okf_version</c> (§12).</summary>
    RootIndexExtraFrontmatter,

    /// <summary>The bundle-root <c>index.md</c> declares an <c>okf_version</c> this build does not recognize.</summary>
    UnsupportedOkfVersion,

    /// <summary>A reserved <c>log.md</c> could not be read (§9, §11).</summary>
    UnparseableLog,

    /// <summary>A <c>log.md</c> date heading is not ISO-8601 <c>YYYY-MM-DD</c> (§9).</summary>
    LogDateInvalid,
```

- [ ] **Step 4: Rewrite `ValidateReserved`**

Replace the full body of `ValidateReserved` (`Validate.cs:507-617`) with:

```csharp
    /// <summary>Checks that reserved files (index.md and log.md) follow their structural rules when present (§8/§9).</summary>
    private static void ValidateReserved(Bundle bundle, List<Diagnostic> diagnostics)
    {
        var rootIndex = System.IO.Path.Combine(bundle.Root, IndexFilename);

        foreach (var path in bundle.IndexFiles)
        {
            string text;
            try
            {
                text = OkfEncodings.Strict.GetString(File.ReadAllBytes(path));
            }
            catch (IOException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable index.md: {e.Message}", DiagnosticCode.UnparseableIndex));
                continue;
            }
            catch (UnauthorizedAccessException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable index.md: {e.Message}", DiagnosticCode.UnparseableIndex));
                continue;
            }
            catch (System.Text.DecoderFallbackException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable index.md: {e.Message}", DiagnosticCode.UnparseableIndex));
                continue;
            }

            OkfDocument doc;
            try
            {
                doc = OkfDocument.Parse(text);
            }
            catch (DocumentParseException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable index.md: {e.Message}", DiagnosticCode.UnparseableIndex));
                continue;
            }

            if (doc.Frontmatter.IsEmpty)
            {
                continue;
            }

            // Frontmatter is only permitted in the bundle-root index.md, and only
            // to declare `okf_version` (§12).
            var isRoot = string.Equals(path, rootIndex, StringComparison.Ordinal);
            if (!isRoot)
            {
                diagnostics.Add(new Diagnostic(
                    Severity.Error,
                    path,
                    null,
                    "index.md should not contain frontmatter (§8)",
                    DiagnosticCode.IndexHasFrontmatter));
            }
            else
            {
                var onlyVersion = doc.Frontmatter.AsMapping().Keys.All(k => k == "okf_version");
                if (!onlyVersion)
                {
                    diagnostics.Add(new Diagnostic(
                        Severity.Error,
                        path,
                        null,
                        "root index.md frontmatter should declare only `okf_version` (§12)",
                        DiagnosticCode.RootIndexExtraFrontmatter,
                        "okf_version"));
                }

                var declaredVersion = doc.Frontmatter.Get("okf_version")?.AsDisplayString();
                if (declaredVersion is not null && !string.Equals(declaredVersion, OkfSpec.Version, StringComparison.Ordinal))
                {
                    diagnostics.Add(new Diagnostic(
                        Severity.Warning,
                        path,
                        null,
                        $"declared okf_version {DebugQuote.Quote(declaredVersion)} is not supported; consuming best-effort as v{OkfSpec.Version}",
                        DiagnosticCode.UnsupportedOkfVersion,
                        "okf_version"));
                }
            }
        }

        foreach (var path in bundle.LogFiles)
        {
            string text;
            try
            {
                text = OkfEncodings.Strict.GetString(File.ReadAllBytes(path));
            }
            catch (IOException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable log.md: {e.Message}", DiagnosticCode.UnparseableLog));
                continue;
            }
            catch (UnauthorizedAccessException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable log.md: {e.Message}", DiagnosticCode.UnparseableLog));
                continue;
            }
            catch (System.Text.DecoderFallbackException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable log.md: {e.Message}", DiagnosticCode.UnparseableLog));
                continue;
            }

            var log = ChangeLog.Parse(text);
            foreach (var bad in log.InvalidDates())
            {
                diagnostics.Add(new Diagnostic(
                    Severity.Error,
                    path,
                    null,
                    $"log date heading is not ISO-8601 `YYYY-MM-DD`: {DebugQuote.Quote(bad)}",
                    DiagnosticCode.LogDateInvalid));
            }
        }
    }
```

The only semantic changes versus the current method: the three previously-silent
`continue`s (both index.md read-failure catches plus the `DocumentParseException`
catch, and the log.md read-failure catches) now push an `Error` diagnostic
first; `IndexHasFrontmatter`, `RootIndexExtraFrontmatter`, and
`LogDateInvalid` change from `Severity.Warning` to `Severity.Error`.
`UnsupportedOkfVersion` is untouched (`Severity.Warning`).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ValidateTests"`
Expected: PASS, all tests in the file (including the 4 touched by this task
and every pre-existing one).

- [ ] **Step 6: Run the full suite**

Run: `dotnet test OKF4net.sln`
Expected: PASS, 0 failures (this confirms no other test anywhere in the
solution — CLI, Agents, Mcp, Catalog — silently depended on the old
Warning-only behavior; Task 2 adds *new* assertions for the two consumers
already known to render `ValidationReport`, but this full-suite run is the
backstop for anything not explicitly listed).

- [ ] **Step 7: Commit**

```sh
git add src/OKF4net/Validate.cs tests/OKF4net.Tests/ValidateTests.cs
git commit -m "fix(core): §11 conformance now enforced for malformed reserved files"
```

---

### Task 2: CLI and Agents/MCP consumer regression tests

**Files:**
- Modify: `tests/OKF4net.Tests/CliTests.cs`
- Modify: `tests/OKF4net.Tests/Agents/OkfValidateChangesTests.cs`

**Interfaces:**
- Consumes: `DiagnosticCode.UnparseableIndex`/`IndexHasFrontmatter` and
  `Severity.Error` from Task 1 — no new production code, this task only
  proves the fix flows through both remaining `ValidationReport` consumers
  the CLI proper doesn't already cover.

No RED/GREEN split for this task: the underlying behavior change already
landed in Task 1, so these tests are expected to pass on first run. They
exist to make the two-more-consumers finding from design review into
permanent regression coverage, not to drive new production code.

- [ ] **Step 1: Add the CLI exit-code test**

In `tests/OKF4net.Tests/CliTests.cs`, add after
`Validate_nonconformant_bundle_exits_nonzero` (after line 74):

```csharp
[Fact]
public void Validate_bundle_with_malformed_reserved_file_exits_nonzero()
{
    using var tmp = new TempDir();
    tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
    tmp.Write("sub/index.md", "---\ntitle: nope\n---\n\n# Listing\n");
    var r = Run("validate", tmp.Path);
    Assert.NotEqual(0, r.Code);
    Assert.Contains("not conformant", r.Out);
}
```

- [ ] **Step 2: Add the Agents/MCP parity test**

In `tests/OKF4net.Tests/Agents/OkfValidateChangesTests.cs`, add after
`ValidateBundle_reports_error_and_nonconformant_when_type_is_missing`
(after line 79):

```csharp
[Fact]
public void ValidateBundle_reports_error_and_nonconformant_for_malformed_reserved_file()
{
    using var tmp = new TempDir();
    var tools = NewToolsOverFixtureCopy(tmp);
    tmp.Write("tables/index.md", "---\ntitle: nope\n---\n\n# Listing\n");
    tools.InvalidateBundle();

    var result = tools.ValidateBundle();

    Assert.Contains("[error]", result);
    Assert.Contains("not conformant", result);
    Assert.DoesNotContain("0 error(s)", result);
}
```

- [ ] **Step 3: Run both new tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests.Validate_bundle_with_malformed_reserved_file_exits_nonzero"`
Expected: PASS.

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfValidateChangesTests.ValidateBundle_reports_error_and_nonconformant_for_malformed_reserved_file"`
Expected: PASS.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test OKF4net.sln`
Expected: PASS, 0 failures.

- [ ] **Step 5: Commit**

```sh
git add tests/OKF4net.Tests/CliTests.cs tests/OKF4net.Tests/Agents/OkfValidateChangesTests.cs
git commit -m "test(core): add CLI and Agents/MCP regression coverage for §11 reserved-file fix"
```

---

### Task 3: New golden fixture + `GoldenParityTests` wiring + fixtures README

**Files:**
- Create: `tests/fixtures/okf_v02_reserved/index.md`
- Create: `tests/fixtures/okf_v02_reserved/log.md`
- Create: `tests/fixtures/okf_v02_reserved/concepts/note.md`
- Create: `tests/fixtures/okf_v02_reserved/sub/index.md`
- Create: `tests/fixtures/okf_v02_reserved/broken/index.md`
- Create: `tests/fixtures/golden/validate-reserved.out`
- Create: `tests/fixtures/golden/validate-reserved.exitcode`
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs`
- Modify: `tests/fixtures/README.md`

**Interfaces:**
- Consumes: the fixed `ValidateReserved` from Task 1 (this task captures
  its real output, it doesn't guess it).

- [ ] **Step 1: Create the fixture bundle**

Each file isolates exactly one of this fix's new-`Error` diagnostics
(mirroring how `okf_v02_computation/malformed/*.md` isolates §10
diagnostics one-per-file), plus one clean concept so the bundle isn't
purely reserved files:

`tests/fixtures/okf_v02_reserved/index.md` (root — extra key beside
`okf_version` → `RootIndexExtraFrontmatter`):

```markdown
---
okf_version: "0.2"
title: extra
---

# Listing
```

`tests/fixtures/okf_v02_reserved/log.md` (root — invalid date heading →
`LogDateInvalid`):

```markdown
# Log

## not-a-date
* **Update**: did a thing.
```

`tests/fixtures/okf_v02_reserved/concepts/note.md` (clean concept,
contributes zero diagnostics):

```markdown
---
type: Note
title: A clean note
description: Contributes no diagnostics; isolates the reserved-file findings.
resource: https://example.com/note
tags: [note]
---

Body text.
```

`tests/fixtures/okf_v02_reserved/sub/index.md` (non-root — has frontmatter
→ `IndexHasFrontmatter`):

```markdown
---
title: nope
---

# Listing
```

`tests/fixtures/okf_v02_reserved/broken/index.md` (unparseable YAML →
`UnparseableIndex`):

```markdown
---
title: [unterminated
---

# Listing
```

- [ ] **Step 2: Build the CLI**

Run: `dotnet build src/OKF4net.Cli`
Expected: build succeeds (Task 1's changes are already committed and part
of this build).

- [ ] **Step 3: Capture the real output**

From the repo root (the embedded bundle path in diagnostics is exactly
what's given on the command line, so this must run from repo root, same
as every other golden capture):

```sh
dotnet run --project src/OKF4net.Cli -- validate tests/fixtures/okf_v02_reserved > /tmp/validate-reserved-capture.out
echo $?
```

- [ ] **Step 4: Hand-verify every line before saving as golden**

Open `/tmp/validate-reserved-capture.out` and check each diagnostic line
against `ValidateReserved`'s actual message/severity/code from Task 1 —
do not save it as golden on trust. Confirm:
- One `[error]` line for `okf_v02_reserved/index.md` containing "root
  index.md frontmatter should declare only `okf_version` (§12)".
- One `[error]` line for `okf_v02_reserved/log.md` containing "log date
  heading is not ISO-8601 `YYYY-MM-DD`: \"not-a-date\"".
- One `[error]` line for `okf_v02_reserved/sub/index.md` containing
  "index.md should not contain frontmatter (§8)".
- One `[error]` line for `okf_v02_reserved/broken/index.md` starting with
  "unparseable index.md: ".
- No line mentioning `okf_v02_reserved/concepts/note.md` (it's clean).
- The summary line reports `4 error(s)` and the bundle **not conformant**.
- The captured exit code (from `echo $?` in Step 3) is `1`.

If anything doesn't match this list, the bug is in Task 1's code, not in
this capture — go fix Task 1 first, don't hand-edit the captured output to
match expectations.

- [ ] **Step 5: Save the golden files**

```sh
cp /tmp/validate-reserved-capture.out tests/fixtures/golden/validate-reserved.out
printf '1' > tests/fixtures/golden/validate-reserved.exitcode
```

Confirm `validate-reserved.exitcode` has **no trailing newline** (matches
the convention documented in `tests/fixtures/README.md`: *"a bare ASCII
digit with no trailing newline"*) — `printf` (not `echo`) guarantees this.

- [ ] **Step 6: Wire the fixture into `GoldenParityTests`**

In `tests/OKF4net.Tests/GoldenParityTests.cs`, add after
`Validate_computation_fixture_matches_golden` (after line 98):

```csharp
[Fact]
public void Validate_reserved_fixture_matches_golden()
{
    var r = WithRepoRootAsCwd(() => Run("validate", "tests/fixtures/okf_v02_reserved"));
    Assert.Equal(int.Parse(Golden("validate-reserved.exitcode")), r.Code);
    Assert.Equal(Golden("validate-reserved.out"), r.Out.Replace('\\', '/'));
}
```

- [ ] **Step 7: Run the new golden test**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~GoldenParityTests.Validate_reserved_fixture_matches_golden"`
Expected: PASS.

- [ ] **Step 8: Document the new fixture in `tests/fixtures/README.md`**

Add a new section at the end of the file (after the "## §10 Attested
Computation bump (2026-07-29)" section), following the same
hand-authored/hand-verified documentation pattern as that section and the
"## v0.1 → v0.2 bump" section above it:

```markdown
## §11 conformance fix for malformed reserved files (2026-07-31)

- `okf_v02_reserved/` and `golden/validate-reserved.out` /
  `golden/validate-reserved.exitcode` are **new** v0.2 fixtures for the
  fix that makes `BundleValidator.ValidateReserved` correctly enforce §11
  condition 3 (reserved files must follow their §8/§9 structure) —
  hand-authored and hand-verified against the actual `BundleValidator`
  behavior after the fix, like the fixtures above: no reference binary
  implements this either, and every prior golden fixture predates the
  fix (all were re-verified during design and confirmed unaffected).
  - `index.md` (root) declares an extra key beside `okf_version` →
    `RootIndexExtraFrontmatter`, now `[error]`.
  - `log.md` (root) has a non-ISO-8601 date heading → `LogDateInvalid`,
    now `[error]`.
  - `sub/index.md` (non-root) declares frontmatter → `IndexHasFrontmatter`,
    now `[error]`.
  - `broken/index.md` has unparseable YAML frontmatter →
    `UnparseableIndex`, a brand-new diagnostic for a case that previously
    produced no diagnostic at all.
  - `concepts/note.md` is a fully clean concept, contributing zero
    diagnostics, so every diagnostic in the golden output is attributable
    to exactly one of the four cases above.
  - The bundle is **not conformant** (exit code `1`) — this is the point
    of the fix: all four cases were previously `[warning]` or silent, and
    the bundle incorrectly validated as conformant (exit code `0`).
  - Not covered by this fixture (documented gap, not an oversight): a
    reserved file that fails to *read* (I/O/permission error, as opposed
    to failing to *parse*) — `DiagnosticCode.UnparseableIndex`/
    `UnparseableLog`'s read-failure branches. No reliable, non-flaky,
    cross-platform way to construct a genuinely unreadable file was found
    for this repo's Linux/Windows/macOS CI matrix; the code path is
    identical in shape to the parse-failure branch this fixture does
    cover (same diagnostic construction, different caught exception
    type), so the risk of it being wrong is low, but it remains
    unexercised by an automated test.
```

- [ ] **Step 9: Run the full suite**

Run: `dotnet test OKF4net.sln`
Expected: PASS, 0 failures.

- [ ] **Step 10: Commit**

```sh
git add tests/fixtures/okf_v02_reserved tests/fixtures/golden/validate-reserved.out tests/fixtures/golden/validate-reserved.exitcode tests/OKF4net.Tests/GoldenParityTests.cs tests/fixtures/README.md
git commit -m "test(fixtures): add golden fixture for §11 reserved-file conformance fix"
```

---

### Task 4: CHANGELOG entry

**Files:**
- Modify: `CHANGELOG.md`

**Interfaces:** None — documentation only.

- [ ] **Step 1: Add the entry**

In `CHANGELOG.md`, under `## [Unreleased]` → `### Changed`, add after the
existing `**Breaking: Diagnostic's constructor gains a required Code
parameter**` bullet (after line 28):

```markdown
- **Breaking: `okf validate` now correctly reports non-conformance (§11)
  for malformed reserved files.** Previously a malformed `index.md`/`log.md`
  (bad structure, or unreadable/unparseable) was under-reported as
  `Warning` or produced no diagnostic at all, so `okf validate` incorrectly
  exited `0`; it now exits `1` for these cases, as §11 conformance already
  requires. Two new `DiagnosticCode` values, `UnparseableIndex` and
  `UnparseableLog`, cover the previously-silent case.
```

- [ ] **Step 2: Verify placement**

Run: `git diff CHANGELOG.md` and confirm the new bullet is under
`### Changed` (not `### Fixed`), inside `## [Unreleased]`, immediately
after the existing `Diagnostic` constructor `**Breaking:**` bullet.

- [ ] **Step 3: Commit**

```sh
git add CHANGELOG.md
git commit -m "docs(changelog): note the §11 reserved-file conformance fix"
```
