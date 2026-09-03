# OKF Producer Hardening & NuGet Manifest Detection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden `producers/OkfProducer`'s first walking-skeleton slice by closing known-open issues from its review briefing, and fix the concrete bug that made the scanner miss every package in `okf` itself: `RepositoryScanner` only ever looked for a `*.csproj` at the scanned repository's root, so a multi-project repo whose `.csproj` files live under `src/*/` (discoverable only via its root `.sln`) produced zero package concepts.

**Architecture:** No new projects or layers. All changes are inside the existing `producers/OkfProducer.Core` (`Scanning/RepositoryScanner.cs`, `Generation/ConceptGenerator.cs`, `Generation/IBundleWriter.cs`) and `producers/OkfProducer.Cli` (`Program.cs`), plus their existing test files under `producers/tests/OkfProducer.Tests/`. `RepositoryScanner`'s NuGet detection changes from "one `*.csproj` at repo root" to "resolve project paths from a root `*.sln` if one exists, otherwise recursively search the tree (excluding `bin`/`obj`/`.git`/`node_modules`)" — this is additive to the existing npm/README detection, which is untouched.

**Tech Stack:** .NET 10, xUnit, `System.Xml.Linq` (already referenced), no new NuGet packages.

## Global Constraints

- `producers/` stays outside `OKF4net.sln`/`ci.yml` — **permanently, by explicit decision** (not the "not yet acted on" follow-up `ROADMAP.md` currently describes; do not add a CI job for `producers/` as part of this plan or suggest one).
- SPDX header (`// SPDX-License-Identifier: LGPL-3.0-or-later`) on every file (all touched files already have it — preserve, don't duplicate).
- File-scoped namespaces, `Nullable` enabled, XML doc comments on public members, `dotnet format`-compatible — inherited from the root `Directory.Build.props` via `producers/Directory.Build.props`.
- `producers/OkfProducer.Core` stays free of new NuGet dependencies for this plan (only `OKF4net` via `ProjectReference`, as today).
- Build/test loop for every task: `cd producers && dotnet build OkfProducer.sln && dotnet test OkfProducer.sln` — must stay 0 warnings / 0 errors, all tests green, growing by exactly the tests each task adds.
- Never touch `tests/fixtures/` (unrelated to this plan — root-repo golden fixtures, not `producers/`).

---

### Task 1: Remove the redundant `Generation.` namespace qualifier in `IBundleWriter`

**Files:**
- Modify: `producers/src/OkfProducer.Core/Generation/IBundleWriter.cs:23`

**Interfaces:** None (pure cleanup, no signature change — `Generation.GeneratedConcept` and `GeneratedConcept` are the same type since this file is already in the `OkfProducer.Core.Generation` namespace).

- [ ] **Step 1: Edit the signature**

In `producers/src/OkfProducer.Core/Generation/IBundleWriter.cs`, change:

```csharp
    WriteResult Write(string outPath, IReadOnlyList<Generation.GeneratedConcept> concepts, WritePolicy policy, string repoPath);
```

to:

```csharp
    WriteResult Write(string outPath, IReadOnlyList<GeneratedConcept> concepts, WritePolicy policy, string repoPath);
```

- [ ] **Step 2: Build to confirm it still compiles**

Run: `cd producers && dotnet build OkfProducer.sln`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add producers/src/OkfProducer.Core/Generation/IBundleWriter.cs
git commit -m "refactor(producer): drop redundant namespace qualifier in IBundleWriter"
```

---

### Task 2: Fix the self-referential assertion in the slugify integration test

**Files:**
- Modify: `producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs:66`

**Interfaces:** None — test-only change. `ConceptId.Slugify("@scope/My Package!")` evaluates to the literal string `"scope-my-package-"` (lowercase-folded; `@`, `/`, ` `, `!` each map to `-`; no run of 2+ consecutive substituted dashes to collapse here; the leading `-` is stripped because `-` fails `IsValidFirstChar`; nothing is trimmed from the end, so the trailing `-` from `!` survives — see `ConceptId.Slugify`'s XML doc in `src/OKF4net/ConceptId.cs`).

- [ ] **Step 1: Replace the self-referential expected value**

In `producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs`, inside `Generate_slugifies_package_names_for_the_concept_id`, change:

```csharp
        var packageConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "packages");
        Assert.Equal(ConceptId.Slugify("@scope/My Package!"), packageConcept.Id.Segments[1]);
```

to:

```csharp
        var packageConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "packages");
        Assert.Equal("scope-my-package-", packageConcept.Id.Segments[1]);
```

- [ ] **Step 2: Run the test**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Generate_slugifies_package_names_for_the_concept_id"`
Expected: PASS. (This proves the literal matches `ConceptId.Slugify`'s real output, not just "whatever `Slugify` happens to return.")

- [ ] **Step 3: Commit**

```bash
git add producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs
git commit -m "test(producer): stop asserting slugify test against itself"
```

---

### Task 3: Add the missing cross-prefix non-collision test

**Files:**
- Modify: `producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs` (add a new `[Fact]`)

**Interfaces:** None — test-only addition. Confirms existing (already-correct) behavior: `UniqueConceptId`'s `usedIds` set stores the full `"prefix/segment"` string, so a package and a doc that slugify to the same bare segment under different prefixes never collide with each other.

- [ ] **Step 1: Add the test**

Add this `[Fact]` to `producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs` (e.g. after `Generate_disambiguates_two_packages_that_slugify_to_the_same_segment`):

```csharp
    [Fact]
    public void Generate_does_not_collide_a_package_and_a_doc_that_slugify_to_the_same_bare_name()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("npm", "package.json", "Foo", null)],
            [new DocFile("Foo.md", "Foo")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        Assert.Contains(concepts, c => c.Id.ToString() == "packages/foo");
        Assert.Contains(concepts, c => c.Id.ToString() == "docs/foo");
    }
```

- [ ] **Step 2: Run the test**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Generate_does_not_collide_a_package_and_a_doc"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs
git commit -m "test(producer): cover cross-prefix non-collision between packages and docs"
```

---

### Task 4: Make `ExtractTitle` fenced-code-block aware

**Files:**
- Modify: `producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs:105-118`
- Test: `producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs`

**Interfaces:** `ExtractTitle(string readmePath) -> string?` — signature unchanged, only the scan logic changes (skips `# `-heading detection while inside a ``` ``` ``` fenced code block).

- [ ] **Step 1: Write the failing test**

Add this `[Fact]` to `producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs` (e.g. after `Scan_detects_readme_and_extracts_first_heading_as_title`):

```csharp
    [Fact]
    public void Scan_readme_ignores_a_heading_line_inside_a_fenced_code_block()
    {
        var repo = CreateTempRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "README.md"),
                "```\n# Not a heading\n```\n\n# Real Heading\n\nSome text.\n");

            var snapshot = new RepositoryScanner().Scan(repo);

            var doc = Assert.Single(snapshot.Docs);
            Assert.Equal("Real Heading", doc.Title);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Scan_readme_ignores_a_heading_line_inside_a_fenced_code_block"`
Expected: FAIL — `doc.Title` is `"Not a heading"` (the current implementation has no fence awareness).

- [ ] **Step 3: Fix `ExtractTitle`**

In `producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs`, replace:

```csharp
    private static string? ExtractTitle(string readmePath)
    {
        foreach (var line in File.ReadLines(readmePath))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                var heading = trimmed[2..].Trim();
                return heading.Length == 0 ? null : heading;
            }
        }

        return null;
    }
```

with:

```csharp
    private static string? ExtractTitle(string readmePath)
    {
        var inFencedCodeBlock = false;
        foreach (var line in File.ReadLines(readmePath))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFencedCodeBlock = !inFencedCodeBlock;
                continue;
            }

            if (inFencedCodeBlock)
            {
                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                var heading = trimmed[2..].Trim();
                return heading.Length == 0 ? null : heading;
            }
        }

        return null;
    }
```

- [ ] **Step 4: Run the test again**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Scan_readme_ignores_a_heading_line_inside_a_fenced_code_block"`
Expected: PASS.

- [ ] **Step 5: Run the full scanner test file to confirm no regression**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~RepositoryScannerTests"`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs
git commit -m "fix(producer): ignore README headings inside fenced code blocks"
```

---

### Task 5: `ConceptGenerator` avoids reserved (`index`/`log`) and double-extension (`*.md`) slugs

**Files:**
- Modify: `producers/src/OkfProducer.Core/Generation/ConceptGenerator.cs:42-71`
- Test: `producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs`

**Interfaces:** `UniqueConceptId(string prefix, string name, HashSet<string> usedIds) -> ConceptId` — signature unchanged. Behavior change: a doc titled exactly `Index` or `Log` (any casing) — which today produces `docs/index`/`docs/log` and gets rejected downstream by `BundleConceptWriter.WriteConcept`'s reserved-id check (`src/OKF4net/BundleConceptWriter.cs:491-496`) — now gets disambiguated with the same numeric-suffix scheme already used for ordinary collisions (`docs/index-2`). A doc/package name that slugifies to something ending in `.md` (e.g. a doc literally titled `README.md`) no longer produces a double-extension file (`docs/readme.md.md`) — the trailing `.md` is stripped from the slug before use.

- [ ] **Step 1: Write the failing tests**

Add these two `[Fact]`s to `producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs` (e.g. after `Generate_disambiguates_two_packages_that_are_both_entirely_non_ascii`):

```csharp
    [Fact]
    public void Generate_disambiguates_a_doc_titled_Index_instead_of_producing_a_reserved_id()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("INDEX.md", "Index")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var docConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "docs");
        Assert.Equal("docs/index-2", docConcept.Id.ToString());
    }

    [Fact]
    public void Generate_strips_a_trailing_dot_md_from_a_doc_slug_to_avoid_a_double_extension()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("README.md", "README.md")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var docConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "docs");
        Assert.Equal("docs/readme", docConcept.Id.ToString());
    }
```

- [ ] **Step 2: Run them to confirm they fail**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Generate_disambiguates_a_doc_titled_Index|FullyQualifiedName~Generate_strips_a_trailing_dot_md"`
Expected: FAIL — first test produces `docs/index` (not `-2`); second produces `docs/readme.md`.

- [ ] **Step 3: Fix `UniqueConceptId`**

In `producers/src/OkfProducer.Core/Generation/ConceptGenerator.cs`, replace:

```csharp
    private static ConceptId UniqueConceptId(string prefix, string name, HashSet<string> usedIds)
    {
        string baseSlug;
        try
        {
            baseSlug = ConceptId.Slugify(name);
        }
        catch (ConceptIdException)
        {
            // `name` normalized to nothing (e.g. entirely non-ASCII, or empty) -- fall back to a
            // generic slug derived from the prefix; the collision loop below still disambiguates
            // multiple equally-unnameable entries under the same prefix with a numeric suffix.
            baseSlug = prefix switch
            {
                "packages" => "package",
                "docs" => "doc",
                _ => prefix,
            };
        }

        var candidate = $"{prefix}/{baseSlug}";
        var suffix = 2;
        while (!usedIds.Add(candidate))
        {
            candidate = $"{prefix}/{baseSlug}-{suffix}";
            suffix++;
        }

        return ConceptId.Parse(candidate);
    }
```

with:

```csharp
    private static ConceptId UniqueConceptId(string prefix, string name, HashSet<string> usedIds)
    {
        string baseSlug;
        try
        {
            baseSlug = ConceptId.Slugify(name);
        }
        catch (ConceptIdException)
        {
            // `name` normalized to nothing (e.g. entirely non-ASCII, or empty) -- fall back to a
            // generic slug derived from the prefix; the collision loop below still disambiguates
            // multiple equally-unnameable entries under the same prefix with a numeric suffix.
            baseSlug = prefix switch
            {
                "packages" => "package",
                "docs" => "doc",
                _ => prefix,
            };
        }

        // A concept id segment ending in ".md" would double up when BundleConceptWriter appends its
        // own ".md" extension to serialize the file (e.g. a doc literally titled "README.md" would
        // otherwise become "docs/readme.md.md").
        if (baseSlug.Length > ".md".Length && baseSlug.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            baseSlug = baseSlug[..^".md".Length];
        }

        // "index"/"log" are reserved concept ids (BundleConceptWriter.WriteConcept rejects them --
        // they'd collide with the bundle's own index.md/log.md). Treat a name that slugifies to one of
        // these the same as an ordinary collision: fall through to the numeric-suffix loop below
        // instead of producing an id that write time would reject.
        var segment = baseSlug;
        var suffix = 2;
        while (IsReservedSegment(segment) || !usedIds.Add($"{prefix}/{segment}"))
        {
            segment = $"{baseSlug}-{suffix}";
            suffix++;
        }

        return ConceptId.Parse($"{prefix}/{segment}");
    }

    private static bool IsReservedSegment(string segment) =>
        string.Equals(segment, "index", StringComparison.OrdinalIgnoreCase)
        || string.Equals(segment, "log", StringComparison.OrdinalIgnoreCase);
```

- [ ] **Step 4: Run the two new tests again**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Generate_disambiguates_a_doc_titled_Index|FullyQualifiedName~Generate_strips_a_trailing_dot_md"`
Expected: both PASS.

- [ ] **Step 5: Run the full generator test file to confirm no regression**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~ConceptGeneratorTests"`
Expected: all PASS (including `Generate_every_concept_passes_strict_Validate`, unaffected).

- [ ] **Step 6: Commit**

```bash
git add producers/src/OkfProducer.Core/Generation/ConceptGenerator.cs producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs
git commit -m "fix(producer): avoid reserved and double-extension concept ids in ConceptGenerator"
```

---

### Task 6: Prove `BundleWriter` still degrades gracefully on a reserved concept id

**Files:**
- Modify: `producers/tests/OkfProducer.Tests/Generation/BundleWriterTests.cs` (add a new `[Fact]`)

**Interfaces:** None — test-only addition, exercises `IBundleWriter.Write`'s existing `WriteResult.Failures` path directly (bypassing `ConceptGenerator`, which after Task 5 no longer produces reserved ids itself) so this safety net stays covered regardless of which caller constructs a `GeneratedConcept`.

- [ ] **Step 1: Add the test**

Add this `[Fact]` to `producers/tests/OkfProducer.Tests/Generation/BundleWriterTests.cs` (e.g. after `Write_regenerates_the_index_after_writing_concepts`):

```csharp
    [Fact]
    public void Write_reports_a_reserved_concept_id_as_a_failure_without_stopping_the_rest()
    {
        var outPath = CreateTempDir();
        var concepts = new List<GeneratedConcept>
        {
            SampleConcept("overview"),
            new(ConceptId.Parse("index"),
                OkfDocumentBuilder.ForType("Documentation").Title("t").Description("d").Body("# t\n").Build()),
        };
        try
        {
            var result = new BundleWriter().Write(outPath, concepts, WritePolicy.RequireEmpty, UnrelatedRepoPath());

            Assert.Equal(1, result.Written);
            var failure = Assert.Single(result.Failures);
            Assert.Equal("index", failure.Id.ToString());
            Assert.Contains("reserved concept id", failure.Error, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(outPath, "overview.md")));
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }
```

- [ ] **Step 2: Run it**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Write_reports_a_reserved_concept_id_as_a_failure"`
Expected: PASS (this documents already-correct `BundleWriter`/`BundleConceptWriter` behavior, per review finding §7.1).

- [ ] **Step 3: Commit**

```bash
git add producers/tests/OkfProducer.Tests/Generation/BundleWriterTests.cs
git commit -m "test(producer): cover BundleWriter's per-concept reserved-id failure path"
```

---

### Task 7: Wire `RepositorySnapshot.RepoPath` as the single source of truth in `Program.cs`

**Files:**
- Modify: `producers/src/OkfProducer.Cli/Program.cs:48-50`

**Interfaces:** None — `RepositorySnapshot.RepoPath` already exists (`producers/src/OkfProducer.Core/Scanning/RepositorySnapshot.cs:12`) and is already populated by `RepositoryScanner.Scan` with the exact same value as the CLI's `repo` variable. This task makes it the value actually passed to `IBundleWriter.Write`'s `repoPath` guard-rail parameter, instead of the CLI keeping its own separately-threaded copy of the same path. No automated test — `Program.cs`'s CLI layer has no automated tests by design (every piece of logic it calls is already unit-tested; see `docs/superpowers/plans/2026-07-31-okf-producer-core.md` Task 6's rationale) — verified manually in Task 11.

- [ ] **Step 1: Edit the `generate` handler**

In `producers/src/OkfProducer.Cli/Program.cs`, change:

```csharp
        var snapshot = scanner.Scan(repo);
        var concepts = generator.Generate(snapshot);
        var result = writer.Write(outPath, concepts, policy, repo);
```

to:

```csharp
        var snapshot = scanner.Scan(repo);
        var concepts = generator.Generate(snapshot);
        var result = writer.Write(outPath, concepts, policy, snapshot.RepoPath);
```

- [ ] **Step 2: Build**

Run: `cd producers && dotnet build OkfProducer.sln`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add producers/src/OkfProducer.Cli/Program.cs
git commit -m "refactor(producer): thread RepositorySnapshot.RepoPath through instead of a second copy"
```

---

### Task 8: Recursive `.csproj` discovery + old-style (`xmlns`-declaring) `.csproj` support

**Files:**
- Modify: `producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs`
- Test: `producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs`

**Interfaces:** `Scan(string repoPath) -> RepositorySnapshot` — public signature unchanged. Internally, `ScanNuGetManifest` becomes namespace-unaware (matches `PropertyGroup`/`PackageId`/`Description` by `XName.LocalName` instead of an un-namespaced `XName`, so both SDK-style and old-style `xmlns="http://schemas.microsoft.com/developer/msbuild/2003")`-declaring `.csproj` files are read correctly). A new private helper `EnumerateCsprojFilesRecursively(string directory) -> IEnumerable<string>` walks the tree, pruning `bin`/`obj`/`.git`/`node_modules` directories. `ResolveCsprojPaths` (added in this task, extended in Task 9) is the single call site `Scan` uses to get the list of `.csproj` paths to examine.

- [ ] **Step 1: Write the failing tests**

Add these three `[Fact]`s to `producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs` (e.g. after `Scan_csproj_without_PackageId_falls_back_to_filename`):

```csharp
    [Fact]
    public void Scan_finds_a_csproj_nested_in_a_subdirectory_when_no_sln_is_present()
    {
        var repo = CreateTempRepo();
        try
        {
            var srcDir = Path.Combine(repo, "src", "MyTool");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "MyTool.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <PackageId>MyTool</PackageId>
                  </PropertyGroup>
                </Project>
                """);

            var snapshot = new RepositoryScanner().Scan(repo);

            var pkg = Assert.Single(snapshot.Packages);
            Assert.Equal("nuget", pkg.Ecosystem);
            Assert.Equal("src/MyTool/MyTool.csproj", pkg.RelativePath);
            Assert.Equal("MyTool", pkg.Name);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_ignores_csproj_files_under_bin_obj_dot_git_and_node_modules()
    {
        var repo = CreateTempRepo();
        try
        {
            foreach (var excluded in new[] { "bin", "obj", ".git", "node_modules" })
            {
                var dir = Path.Combine(repo, excluded);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "Stale.csproj"), """
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <PackageId>Stale</PackageId>
                      </PropertyGroup>
                    </Project>
                    """);
            }

            var snapshot = new RepositoryScanner().Scan(repo);

            Assert.Empty(snapshot.Packages);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_detects_PackageId_and_Description_in_an_old_style_xmlns_csproj()
    {
        var repo = CreateTempRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "MyTool.csproj"), """
                <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <PackageId>MyTool</PackageId>
                    <Description>Does the thing, the old way.</Description>
                  </PropertyGroup>
                </Project>
                """);

            var snapshot = new RepositoryScanner().Scan(repo);

            var pkg = Assert.Single(snapshot.Packages);
            Assert.Equal("MyTool", pkg.Name);
            Assert.Equal("Does the thing, the old way.", pkg.Description);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
```

- [ ] **Step 2: Run them to confirm they fail**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Scan_finds_a_csproj_nested|FullyQualifiedName~Scan_ignores_csproj_files_under|FullyQualifiedName~Scan_detects_PackageId_and_Description_in_an_old_style_xmlns_csproj"`
Expected: FAIL — nested/old-style `.csproj` files are invisible to the current root-only, un-namespaced query. (The `bin`/`obj`/etc. test currently passes vacuously since nothing is found anywhere — that's fine, it becomes a real exclusion test once Step 3 lands.)

- [ ] **Step 3: Rewrite the scan and `ScanNuGetManifest`**

In `producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs`, replace the `foreach` loop over `Directory.EnumerateFiles`:

```csharp
        foreach (var csprojPath in Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.TopDirectoryOnly))
        {
            var nugetPackage = ScanNuGetManifest(repoPath, csprojPath);
            if (nugetPackage is not null)
            {
                packages.Add(nugetPackage);
            }
        }
```

with:

```csharp
        foreach (var csprojPath in ResolveCsprojPaths(repoPath))
        {
            var nugetPackage = ScanNuGetManifest(repoPath, csprojPath);
            if (nugetPackage is not null)
            {
                packages.Add(nugetPackage);
            }
        }
```

Then replace the whole `ScanNuGetManifest` method:

```csharp
    private static PackageManifest? ScanNuGetManifest(string repoPath, string csprojPath)
    {
        try
        {
            var xml = XDocument.Load(csprojPath);
            var propertyGroups = xml.Root?.Elements("PropertyGroup");
            var name = propertyGroups?.Elements("PackageId").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileNameWithoutExtension(csprojPath);
            }

            var description = propertyGroups?.Elements("Description").FirstOrDefault()?.Value;
            var relativePath = Path.GetRelativePath(repoPath, csprojPath).Replace('\\', '/');

            return new PackageManifest("nuget", relativePath, name, string.IsNullOrWhiteSpace(description) ? null : description);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
```

with (namespace-unaware element matching, so both SDK-style and old-style `xmlns`-declaring `.csproj` files work):

```csharp
    private static PackageManifest? ScanNuGetManifest(string repoPath, string csprojPath)
    {
        try
        {
            var xml = XDocument.Load(csprojPath);
            var propertyGroups = (xml.Root?.Elements().Where(e => e.Name.LocalName == "PropertyGroup") ?? [])
                .ToList();
            var name = propertyGroups
                .SelectMany(group => group.Elements())
                .FirstOrDefault(e => e.Name.LocalName == "PackageId")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileNameWithoutExtension(csprojPath);
            }

            var description = propertyGroups
                .SelectMany(group => group.Elements())
                .FirstOrDefault(e => e.Name.LocalName == "Description")?.Value;
            var relativePath = Path.GetRelativePath(repoPath, csprojPath).Replace('\\', '/');

            return new PackageManifest("nuget", relativePath, name, string.IsNullOrWhiteSpace(description) ? null : description);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
```

Then add `ResolveCsprojPaths` and `EnumerateCsprojFilesRecursively` (place them right above `ScanNuGetManifest`; `ResolveCsprojPaths` only has the recursive branch for now -- Task 9 adds the `.sln` branch in front of it):

```csharp
    private static readonly string[] ExcludedDirectoryNames = ["bin", "obj", ".git", "node_modules"];

    private static IReadOnlyList<string> ResolveCsprojPaths(string repoPath)
    {
        return EnumerateCsprojFilesRecursively(repoPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> EnumerateCsprojFilesRecursively(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
        {
            yield return file;
        }

        foreach (var subDirectory in Directory.EnumerateDirectories(directory))
        {
            if (!ExcludedDirectoryNames.Contains(Path.GetFileName(subDirectory), StringComparer.OrdinalIgnoreCase))
            {
                foreach (var file in EnumerateCsprojFilesRecursively(subDirectory))
                {
                    yield return file;
                }
            }
        }
    }
```

- [ ] **Step 4: Run the three tests again**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Scan_finds_a_csproj_nested|FullyQualifiedName~Scan_ignores_csproj_files_under|FullyQualifiedName~Scan_detects_PackageId_and_Description_in_an_old_style_xmlns_csproj"`
Expected: all PASS.

- [ ] **Step 5: Run the full scanner test file to confirm no regression**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~RepositoryScannerTests"`
Expected: all PASS, including the pre-existing root-`.csproj` tests (a root `.csproj` is still found — `EnumerateCsprojFilesRecursively` yields `TopDirectoryOnly` matches before recursing).

- [ ] **Step 6: Commit**

```bash
git add producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs
git commit -m "feat(producer): recursive .csproj discovery + old-style xmlns .csproj support"
```

---

### Task 9: Prefer a root `.sln`'s project references over the recursive search

**Files:**
- Modify: `producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs`
- Test: `producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs`

**Interfaces:** `ResolveCsprojPaths(string repoPath) -> IReadOnlyList<string>` (from Task 8) gains a `.sln`-first branch. When one or more `*.sln` files exist at `repoPath`'s top level, every `.csproj` path they reference is used (deduplicated, sorted) instead of the recursive search — this is the fix for the motivating bug: `okf`'s own `.csproj` files live under `src/*/` and are only discoverable via `OKF4net.sln`'s `Project(...)` lines, not by being at the repo root.

- [ ] **Step 1: Write the failing test**

Add this `[Fact]` to `producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs` (e.g. after the three added in Task 8):

```csharp
    [Fact]
    public void Scan_prefers_sln_project_references_over_a_full_recursive_search()
    {
        var repo = CreateTempRepo();
        try
        {
            var includedDir = Path.Combine(repo, "src", "Included");
            Directory.CreateDirectory(includedDir);
            File.WriteAllText(Path.Combine(includedDir, "Included.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <PackageId>Included</PackageId>
                  </PropertyGroup>
                </Project>
                """);

            var excludedDir = Path.Combine(repo, "samples", "Excluded");
            Directory.CreateDirectory(excludedDir);
            File.WriteAllText(Path.Combine(excludedDir, "Excluded.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <PackageId>Excluded</PackageId>
                  </PropertyGroup>
                </Project>
                """);

            File.WriteAllText(Path.Combine(repo, "MySolution.sln"), """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Included", "src\Included\Included.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                """);

            var snapshot = new RepositoryScanner().Scan(repo);

            var pkg = Assert.Single(snapshot.Packages);
            Assert.Equal("Included", pkg.Name);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
```

- [ ] **Step 2: Run it to confirm it fails**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Scan_prefers_sln_project_references"`
Expected: FAIL — `snapshot.Packages` has 2 entries (`Included` and `Excluded`), since `ResolveCsprojPaths` from Task 8 always does the full recursive search.

- [ ] **Step 3: Add the `.sln` branch**

In `producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs`, replace the `ResolveCsprojPaths` method from Task 8:

```csharp
    private static IReadOnlyList<string> ResolveCsprojPaths(string repoPath)
    {
        return EnumerateCsprojFilesRecursively(repoPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
```

with:

```csharp
    private static IReadOnlyList<string> ResolveCsprojPaths(string repoPath)
    {
        var slnPaths = Directory.EnumerateFiles(repoPath, "*.sln", SearchOption.TopDirectoryOnly).ToList();
        if (slnPaths.Count == 0)
        {
            return EnumerateCsprojFilesRecursively(repoPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return slnPaths
            .SelectMany(ParseSolutionProjectPaths)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> ParseSolutionProjectPaths(string slnPath)
    {
        var slnDirectory = Path.GetDirectoryName(slnPath)!;
        foreach (var line in File.ReadLines(slnPath))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("Project(", StringComparison.Ordinal))
            {
                continue;
            }

            // Project("{TypeGuid}") = "Name", "RelativePath", "{ProjectGuid}" -- splitting on '"'
            // puts the relative path at index 5 (index 3 is the display name, a solution-folder
            // pseudo-project or non-.csproj-extension entry is filtered out below).
            var parts = trimmed.Split('"');
            if (parts.Length < 6)
            {
                continue;
            }

            var relativePath = parts[5];
            if (!relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return Path.GetFullPath(Path.Combine(slnDirectory, relativePath.Replace('\\', '/')));
        }
    }
```

- [ ] **Step 4: Run the new test again**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~Scan_prefers_sln_project_references"`
Expected: PASS.

- [ ] **Step 5: Run the full scanner test file to confirm no regression**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~RepositoryScannerTests"`
Expected: all PASS -- including Task 8's tests (none of them have a `.sln`, so they still take the recursive branch).

- [ ] **Step 6: Commit**

```bash
git add producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs
git commit -m "feat(producer): resolve .csproj paths from a root .sln when one exists"
```

---

### Task 10: Cover a repo with both npm and NuGet (via `.sln`) manifests end-to-end

**Files:**
- Modify: `producers/tests/OkfProducer.Tests/EndToEndTests.cs` (add a new `[Fact]`)

**Interfaces:** None — test-only addition, closes review finding §7.12 ("no test scans a repository containing both an npm `package.json` and a NuGet `.csproj` at once").

- [ ] **Step 1: Add the test**

Add this `[Fact]` to `producers/tests/OkfProducer.Tests/EndToEndTests.cs`, in the `OkfProducer.Tests` namespace's `EndToEndTests` class:

```csharp
    [Fact]
    public void Scan_generate_write_validate_round_trip_on_a_repo_with_both_npm_and_nuget_manifests()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), "okfproducer-e2e-multi-repo-" + Guid.NewGuid());
        var outPath = Path.Combine(Path.GetTempPath(), "okfproducer-e2e-multi-out-" + Guid.NewGuid());
        var csprojDir = Path.Combine(repoPath, "src", "Tool");
        Directory.CreateDirectory(csprojDir);
        try
        {
            File.WriteAllText(Path.Combine(repoPath, "package.json"),
                """{ "name": "fixture-lib", "description": "npm half of a mixed-ecosystem fixture." }""");
            File.WriteAllText(Path.Combine(csprojDir, "Tool.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <PackageId>Tool</PackageId>
                    <Description>NuGet half of a mixed-ecosystem fixture.</Description>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(repoPath, "Fixture.sln"), """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Tool", "src\Tool\Tool.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                """);

            var snapshot = new RepositoryScanner().Scan(repoPath);
            Assert.Equal(2, snapshot.Packages.Count);
            Assert.Contains(snapshot.Packages, p => p.Ecosystem == "npm");
            Assert.Contains(snapshot.Packages, p => p.Ecosystem == "nuget");

            var concepts = new ConceptGenerator().Generate(snapshot);
            var writeResult = new BundleWriter().Write(outPath, concepts, WritePolicy.RequireEmpty, repoPath);

            Assert.Equal(3, writeResult.Written); // overview + npm package + nuget package
            Assert.Empty(writeResult.Failures);

            var validationOutcome = new BundleValidationRunner().Validate(outPath);
            Assert.True(validationOutcome.IsConformant, string.Join("\n", validationOutcome.DiagnosticLines));
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
            if (Directory.Exists(outPath)) Directory.Delete(outPath, recursive: true);
        }
    }
```

- [ ] **Step 2: Run it**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~both_npm_and_nuget_manifests"`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add producers/tests/OkfProducer.Tests/EndToEndTests.cs
git commit -m "test(producer): cover a repo with both npm and NuGet-via-.sln manifests"
```

---

### Task 11: Full verification, including a manual run against `okf` itself

**Files:** None modified -- verification only.

- [ ] **Step 1: Full build + test suite**

Run: `cd producers && dotnet build OkfProducer.sln && dotnet test OkfProducer.sln`
Expected: 0 warnings, 0 errors; all tests pass (32 original + 10 added in this plan = 42).

- [ ] **Step 2: Format check**

Run: `cd producers && dotnet format OkfProducer.sln --verify-no-changes`
Expected: no output, exit code 0.

- [ ] **Step 3: Confirm `src`/`tests` (the root repo) is untouched**

Run: `git diff --stat -- src tests` (from the repo root)
Expected: empty output -- this plan only touches `producers/`.

- [ ] **Step 4: Manual smoke test against the `okf` repo itself**

From the repo root:

```bash
dotnet run --project producers/src/OkfProducer.Cli -- generate --repo . --out /tmp/okfgen-self-scan --reset
cat /tmp/okfgen-self-scan/overview.md
dotnet run --project producers/src/OkfProducer.Cli -- validate --okf /tmp/okfgen-self-scan
```

Expected: `generate` reports far more than the 2 concepts it wrote before this plan (`overview` + one `packages/*` concept per `.csproj` referenced by `OKF4net.sln` -- `OKF4net`, `OKF4net.Cli`, `OKF4net.Agents`, `OKF4net.Mcp`, `OKF4net.Catalog`, `OKF4net.Catalog.Hosting`, `OKF4net.Attestation`, `OKF4net.Tests` -- plus the `docs/okf4net` doc concept). `validate` still reports the expected `sources[].resource`/`resource` "not found" warnings (accepted per `ROADMAP.md`'s "Known limitation" entry -- unrelated to this plan) and `0 error(s)`.

- [ ] **Step 5: Clean up the manual smoke-test output**

```bash
rm -rf /tmp/okfgen-self-scan
```

(No commit for this task -- it only verifies the prior ten tasks' commits.)
