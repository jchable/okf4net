# OKF Producer — Core Walking Skeleton Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up `producers/OkfProducer` as a working, testable tool that scans a repository for npm/NuGet packages and a README, generates an OKF v0.2 bundle describing them, and validates a bundle — the full `generate`/`validate` pipeline in `--mode scan` (no LLM), proving the architecture end to end before adding enrichment or scanner breadth.

**Architecture:** `OkfProducer.Core` (Scanning → Generation → Writing → Validation, each an interface + one implementation) referencing `OKF4net` for `OkfDocumentBuilder`/`BundleConceptWriter`/`ConceptId`/`Bundle`/`BundleValidator`/`IndexGenerator`. `OkfProducer.Cli` wires these through `System.CommandLine` 2.0.0 (stable) and a `Microsoft.Extensions.Hosting` composition root. Separate solution (`producers/OkfProducer.sln`), outside `OKF4net.sln`/CI.

**Tech Stack:** C# / .NET 10, xunit, `System.CommandLine` 2.0.0, `Microsoft.Extensions.Hosting`/`Microsoft.Extensions.DependencyInjection`.

**Spec:** `docs/superpowers/specs/2026-07-31-okf-producer-design.md`.

## Scope of this plan (explicit — read before objecting to something as "missing")

This plan implements a deliberately narrower slice than the full spec, chosen to deliver working, testable software as fast as possible rather than one giant plan:

- **Ecosystems detected:** npm (`package.json`) and NuGet (`*.csproj` at repo root) only. Cargo/go.mod/pyproject and the `--package-scope workspaces/all` monorepo variants are **not** in this plan — `--package-scope` itself is not exposed by this plan's CLI at all (deferred, not silently no-op'd).
- **Concepts generated:** repository overview, one concept per detected package, one concept per detected doc (README only, v1). Architecture-overview, CLI-interface, and CI/test/config concepts (spec §3) are **deferred to a follow-up plan**.
- **LLM enrichment, the response cache, and the three-tier `--mode`** (spec §4) are entirely **out of scope** — this plan is scan-mode-only, unconditionally, with no `--mode` flag at all yet.
- **`generated` provenance stamp**: **not written** by this plan. `BundleConceptWriter.AutoStampGenerated` and `OKF4net.Internal.OkfTimestamp` are both `internal` to the `OKF4net` assembly (verified by reading `BundleConceptWriter.cs`/`OkfTimestamp.cs`) and `producers/` is not in `OKF4net`'s `InternalsVisibleTo` list — this producer cannot use the writer's built-in auto-stamp. Revisit later: either OKF4net exposes a public stamping helper, or this producer hand-formats its own ISO-8601 timestamp. Not attempted here to avoid duplicating internal logic this plan can't reuse correctly.

None of this is a gap in the plan — it is the plan's stated boundary. A follow-up plan extends scanner breadth (more ecosystems, CI/test/config concepts); another adds LLM enrichment + cache once this skeleton is proven.

## Global Constraints

- `producers/` stays outside `OKF4net.sln` and CI — its own solution, its own `Directory.Build.props`.
- `src/OKF4net` is referenced (`ProjectReference`), never modified by this plan.
- `OkfProducer.Core`/`OkfProducer.Cli` **may** take NuGet dependencies (only `src/OKF4net` itself is zero-dependency) — this plan adds exactly `System.CommandLine` and `Microsoft.Extensions.Hosting` (+ its `Microsoft.Extensions.DependencyInjection` dependency), nothing else.
- SPDX header (`// SPDX-License-Identifier: LGPL-3.0-or-later`), file-scoped namespaces, `Nullable` enabled, XML doc comments on public members — same conventions as the rest of the repo, inherited via `Directory.Build.props`.
- No golden fixture anywhere in this plan (`tests/fixtures/` belongs to `OKF4net.Cli`, untouched).
- Test filter pattern: `dotnet test OkfProducer.sln --filter "FullyQualifiedName~<ClassName>"` (run from `producers/`).

---

## Task 1: Solution scaffolding

**Files:**
- Create: `producers/OkfProducer.sln`
- Create: `producers/Directory.Build.props`
- Create: `producers/src/OkfProducer.Core/OkfProducer.Core.csproj`
- Create: `producers/src/OkfProducer.Cli/OkfProducer.Cli.csproj`
- Create: `producers/src/OkfProducer.Cli/Program.cs` (placeholder, replaced in Task 6)
- Create: `producers/tests/OkfProducer.Tests/OkfProducer.Tests.csproj`
- Create: `producers/tests/OkfProducer.Tests/SmokeTests.cs`

**Interfaces:** None yet — this task only proves the solution builds and a test runs.

- [ ] **Step 1: Create the directory structure and Directory.Build.props**

```bash
mkdir -p producers/src/OkfProducer.Core producers/src/OkfProducer.Cli producers/tests/OkfProducer.Tests
```

Create `producers/Directory.Build.props`:

```xml
<Project>
  <Import Project="$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))" />
  <PropertyGroup>
    <!-- Independent versioning from the OKF4net package itself -- this is a separate, unpublished tool. -->
    <Version>0.1.0</Version>
  </PropertyGroup>
</Project>
```

(The `Import` walks up to the repo root's own `Directory.Build.props` — inheriting `Nullable`/`TreatWarningsAsErrors`/`LangVersion`/`ImplicitUsings` — then this file's own `<Version>` overrides the inherited one.)

- [ ] **Step 2: Scaffold the three projects**

```bash
cd producers
dotnet new classlib -o src/OkfProducer.Core -f net10.0
dotnet new console -o src/OkfProducer.Cli -f net10.0
dotnet new xunit -o tests/OkfProducer.Tests -f net10.0
rm src/OkfProducer.Core/Class1.cs
rm tests/OkfProducer.Tests/UnitTest1.cs
```

- [ ] **Step 3: Create the solution and add all three projects**

```bash
dotnet new sln -n OkfProducer
dotnet sln add src/OkfProducer.Core/OkfProducer.Core.csproj src/OkfProducer.Cli/OkfProducer.Cli.csproj tests/OkfProducer.Tests/OkfProducer.Tests.csproj
```

- [ ] **Step 4: Wire project references and package references**

```bash
dotnet add src/OkfProducer.Core/OkfProducer.Core.csproj reference ../../../src/OKF4net/OKF4net.csproj
dotnet add src/OkfProducer.Cli/OkfProducer.Cli.csproj reference ../OkfProducer.Core/OkfProducer.Core.csproj
dotnet add src/OkfProducer.Cli/OkfProducer.Cli.csproj package System.CommandLine --version 2.0.0
dotnet add src/OkfProducer.Cli/OkfProducer.Cli.csproj package Microsoft.Extensions.Hosting
dotnet add tests/OkfProducer.Tests/OkfProducer.Tests.csproj reference ../../src/OkfProducer.Core/OkfProducer.Core.csproj
```

(`OkfProducer.Tests` references only `OkfProducer.Core` — the CLI layer (`System.CommandLine` wiring) is verified manually in Task 6, not via an in-process test harness; see that task's rationale.)

- [ ] **Step 5: Write a smoke test proving the reference to OKF4net resolves**

Create `producers/tests/OkfProducer.Tests/SmokeTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Tests;

public class SmokeTests
{
    [Fact]
    public void OKF4net_types_are_reachable_from_this_solution()
    {
        var id = OKF4net.ConceptId.Parse("smoke/test");
        Assert.Equal("smoke/test", id.ToString());
    }
}
```

- [ ] **Step 6: Build and run**

Run: `cd producers && dotnet build OkfProducer.sln`
Expected: build succeeds, 0 warnings, 0 errors (the placeholder `Program.cs` from `dotnet new console` is fine as-is for now).

Run: `dotnet test OkfProducer.sln`
Expected: 1/1 passing.

- [ ] **Step 7: Commit**

```bash
git add producers/
git commit -m "chore(producers): scaffold OkfProducer.sln (Core/Cli/Tests, references OKF4net)"
```

---

## Task 2: Repository scanning (npm + NuGet + README)

**Files:**
- Create: `producers/src/OkfProducer.Core/Scanning/RepositorySnapshot.cs`
- Create: `producers/src/OkfProducer.Core/Scanning/IRepositoryScanner.cs`
- Create: `producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs`
- Test: `producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs`

**Interfaces:**
- Produces: `PackageManifest(string Ecosystem, string RelativePath, string Name, string? Description)`, `DocFile(string RelativePath, string Title)`, `RepositorySnapshot(string RepoPath, string RepoName, IReadOnlyList<PackageManifest> Packages, IReadOnlyList<DocFile> Docs)`, `IRepositoryScanner.Scan(string repoPath) -> RepositorySnapshot` — consumed by Task 3.

- [ ] **Step 1: Write the failing tests**

Create `producers/tests/OkfProducer.Tests/Scanning/RepositoryScannerTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Scanning;

namespace OkfProducer.Tests.Scanning;

public class RepositoryScannerTests
{
    private static string CreateTempRepo()
    {
        var path = Path.Combine(Path.GetTempPath(), "okfproducer-scan-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Scan_detects_npm_package_json()
    {
        var repo = CreateTempRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "package.json"),
                """{ "name": "my-lib", "description": "A little library." }""");

            var snapshot = new RepositoryScanner().Scan(repo);

            var pkg = Assert.Single(snapshot.Packages);
            Assert.Equal("npm", pkg.Ecosystem);
            Assert.Equal("package.json", pkg.RelativePath);
            Assert.Equal("my-lib", pkg.Name);
            Assert.Equal("A little library.", pkg.Description);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_ignores_package_json_with_no_name()
    {
        var repo = CreateTempRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "package.json"), """{ "description": "no name here" }""");

            var snapshot = new RepositoryScanner().Scan(repo);

            Assert.Empty(snapshot.Packages);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_ignores_malformed_package_json()
    {
        var repo = CreateTempRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "package.json"), "{ not valid json");

            var snapshot = new RepositoryScanner().Scan(repo);

            Assert.Empty(snapshot.Packages);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_detects_root_csproj_with_PackageId_and_Description()
    {
        var repo = CreateTempRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "MyTool.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <PackageId>MyTool</PackageId>
                    <Description>Does the thing.</Description>
                  </PropertyGroup>
                </Project>
                """);

            var snapshot = new RepositoryScanner().Scan(repo);

            var pkg = Assert.Single(snapshot.Packages);
            Assert.Equal("nuget", pkg.Ecosystem);
            Assert.Equal("MyTool.csproj", pkg.RelativePath);
            Assert.Equal("MyTool", pkg.Name);
            Assert.Equal("Does the thing.", pkg.Description);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_csproj_without_PackageId_falls_back_to_filename()
    {
        var repo = CreateTempRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "MyTool.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var snapshot = new RepositoryScanner().Scan(repo);

            var pkg = Assert.Single(snapshot.Packages);
            Assert.Equal("MyTool", pkg.Name);
            Assert.Null(pkg.Description);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_detects_readme_and_extracts_first_heading_as_title()
    {
        var repo = CreateTempRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "README.md"), "\n# My Great Tool\n\nSome text.\n");

            var snapshot = new RepositoryScanner().Scan(repo);

            var doc = Assert.Single(snapshot.Docs);
            Assert.Equal("README.md", doc.RelativePath);
            Assert.Equal("My Great Tool", doc.Title);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_readme_without_heading_falls_back_to_repo_name()
    {
        var repo = CreateTempRepo();
        try
        {
            File.WriteAllText(Path.Combine(repo, "README.md"), "Just prose, no heading.\n");

            var snapshot = new RepositoryScanner().Scan(repo);

            var doc = Assert.Single(snapshot.Docs);
            Assert.Equal(new DirectoryInfo(repo).Name, doc.Title);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_empty_repo_yields_no_packages_and_no_docs()
    {
        var repo = CreateTempRepo();
        try
        {
            var snapshot = new RepositoryScanner().Scan(repo);

            Assert.Empty(snapshot.Packages);
            Assert.Empty(snapshot.Docs);
            Assert.Equal(new DirectoryInfo(repo).Name, snapshot.RepoName);
            Assert.Equal(repo, snapshot.RepoPath);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~RepositoryScannerTests"`
Expected: FAIL to compile — none of `RepositorySnapshot`/`PackageManifest`/`DocFile`/`RepositoryScanner` exist yet.

- [ ] **Step 3: Implement the domain types**

Create `producers/src/OkfProducer.Core/Scanning/RepositorySnapshot.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Scanning;

/// <summary>A detected package manifest (e.g. <c>package.json</c>, a <c>.csproj</c>).</summary>
public sealed record PackageManifest(string Ecosystem, string RelativePath, string Name, string? Description);

/// <summary>A detected documentation file (e.g. <c>README.md</c>).</summary>
public sealed record DocFile(string RelativePath, string Title);

/// <summary>The result of scanning a repository: everything <see cref="Generation.IConceptGenerator"/> needs.</summary>
public sealed record RepositorySnapshot(
    string RepoPath,
    string RepoName,
    IReadOnlyList<PackageManifest> Packages,
    IReadOnlyList<DocFile> Docs);
```

- [ ] **Step 4: Implement the scanner interface and implementation**

Create `producers/src/OkfProducer.Core/Scanning/IRepositoryScanner.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Scanning;

/// <summary>Scans a repository directory and reports what it found.</summary>
public interface IRepositoryScanner
{
    /// <summary>Scans <paramref name="repoPath"/> for packages and documentation.</summary>
    RepositorySnapshot Scan(string repoPath);
}
```

Create `producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using System.Xml.Linq;

namespace OkfProducer.Core.Scanning;

/// <summary>
/// Detects npm (<c>package.json</c>) and NuGet (root <c>*.csproj</c>) package manifests, and a root
/// <c>README.md</c>. Malformed manifests are skipped, not fatal -- permissive, matching the rest of
/// this codebase's scan philosophy.
/// </summary>
public sealed class RepositoryScanner : IRepositoryScanner
{
    /// <inheritdoc/>
    public RepositorySnapshot Scan(string repoPath)
    {
        var repoName = new DirectoryInfo(repoPath).Name;
        var packages = new List<PackageManifest>();

        var npmPackage = ScanNpmManifest(repoPath);
        if (npmPackage is not null)
        {
            packages.Add(npmPackage);
        }

        foreach (var csprojPath in Directory.EnumerateFiles(repoPath, "*.csproj", SearchOption.TopDirectoryOnly))
        {
            var nugetPackage = ScanNuGetManifest(repoPath, csprojPath);
            if (nugetPackage is not null)
            {
                packages.Add(nugetPackage);
            }
        }

        var docs = new List<DocFile>();
        var readmePath = Path.Combine(repoPath, "README.md");
        if (File.Exists(readmePath))
        {
            docs.Add(new DocFile("README.md", ExtractTitle(readmePath) ?? repoName));
        }

        return new RepositorySnapshot(repoPath, repoName, packages, docs);
    }

    private static PackageManifest? ScanNpmManifest(string repoPath)
    {
        var path = Path.Combine(repoPath, "package.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            if (!root.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var description = root.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;

            return new PackageManifest("npm", "package.json", name, description);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PackageManifest? ScanNuGetManifest(string repoPath, string csprojPath)
    {
        try
        {
            var xml = XDocument.Load(csprojPath);
            var propertyGroup = xml.Root?.Elements("PropertyGroup").FirstOrDefault();
            var name = propertyGroup?.Element("PackageId")?.Value;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Path.GetFileNameWithoutExtension(csprojPath);
            }

            var description = propertyGroup?.Element("Description")?.Value;
            var relativePath = Path.GetRelativePath(repoPath, csprojPath).Replace('\\', '/');

            return new PackageManifest("nuget", relativePath, name, string.IsNullOrWhiteSpace(description) ? null : description);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string? ExtractTitle(string readmePath)
    {
        foreach (var line in File.ReadLines(readmePath))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..].Trim();
            }
        }

        return null;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~RepositoryScannerTests"`
Expected: PASS (all 8 tests).

- [ ] **Step 6: Format and commit**

```bash
dotnet format OkfProducer.sln
git add producers/src/OkfProducer.Core/Scanning producers/tests/OkfProducer.Tests/Scanning
git commit -m "feat(producers): add RepositoryScanner (npm/NuGet/README detection)"
```

---

## Task 3: Concept generation

**Files:**
- Create: `producers/src/OkfProducer.Core/Generation/GeneratedConcept.cs`
- Create: `producers/src/OkfProducer.Core/Generation/IConceptGenerator.cs`
- Create: `producers/src/OkfProducer.Core/Generation/ConceptGenerator.cs`
- Test: `producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs`

**Interfaces:**
- Consumes: `RepositorySnapshot`/`PackageManifest`/`DocFile` (Task 2), `OKF4net.OkfDocumentBuilder`, `OKF4net.ConceptId.Slugify`/`ConceptId.Parse`.
- Produces: `GeneratedConcept(ConceptId Id, OkfDocument Document)`, `IConceptGenerator.Generate(RepositorySnapshot snapshot) -> IReadOnlyList<GeneratedConcept>` — consumed by Task 4.

- [ ] **Step 1: Write the failing tests**

Create `producers/tests/OkfProducer.Tests/Generation/ConceptGeneratorTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;

namespace OkfProducer.Tests.Generation;

public class ConceptGeneratorTests
{
    [Fact]
    public void Generate_always_includes_one_overview_concept_first()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var overview = Assert.Single(concepts);
        Assert.Equal("overview", overview.Id.ToString());
        Assert.Equal("Repository", overview.Document.Frontmatter.Type);
        Assert.Equal("my-repo", overview.Document.Frontmatter.Title);
    }

    [Fact]
    public void Generate_creates_one_concept_per_package_under_packages_prefix()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("npm", "package.json", "my-lib", "A little library.")],
            []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageConcept = concepts.Single(c => c.Id.ToString() == "packages/my-lib");
        Assert.Equal("Package", packageConcept.Document.Frontmatter.Type);
        Assert.Equal("my-lib", packageConcept.Document.Frontmatter.Title);
        Assert.Equal("A little library.", packageConcept.Document.Frontmatter.Description);
        Assert.Contains("npm", packageConcept.Document.Frontmatter.Tags);
        Assert.Single(packageConcept.Document.Frontmatter.Sources);
        Assert.Equal("package.json", packageConcept.Document.Frontmatter.Sources[0].Resource);
    }

    [Fact]
    public void Generate_package_without_description_falls_back_to_a_generated_one()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("nuget", "Foo.csproj", "Foo", null)],
            []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageConcept = concepts.Single(c => c.Id.ToString() == "packages/foo");
        Assert.Equal("nuget package Foo.", packageConcept.Document.Frontmatter.Description);
    }

    [Fact]
    public void Generate_slugifies_package_names_for_the_concept_id()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("npm", "package.json", "@scope/My Package!", null)],
            []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageConcept = Assert.Single(concepts, c => c.Id.Segments[0] == "packages");
        Assert.Equal(ConceptId.Slugify("@scope/My Package!"), packageConcept.Id.Segments[1]);
    }

    [Fact]
    public void Generate_disambiguates_two_packages_that_slugify_to_the_same_segment()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [
                new PackageManifest("npm", "a/package.json", "My Package", null),
                new PackageManifest("nuget", "b/My.Package.csproj", "My Package", null),
            ],
            []);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var packageIds = concepts.Where(c => c.Id.Segments[0] == "packages").Select(c => c.Id.ToString()).ToList();
        Assert.Equal(["packages/my-package", "packages/my-package-2"], packageIds);
    }

    [Fact]
    public void Generate_creates_one_concept_per_doc_under_docs_prefix()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo", [], [new DocFile("README.md", "My Great Tool")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        var docConcept = concepts.Single(c => c.Id.ToString() == "docs/my-great-tool");
        Assert.Equal("Documentation", docConcept.Document.Frontmatter.Type);
        Assert.Equal("My Great Tool", docConcept.Document.Frontmatter.Title);
        Assert.Single(docConcept.Document.Frontmatter.Sources);
        Assert.Equal("README.md", docConcept.Document.Frontmatter.Sources[0].Resource);
    }

    [Fact]
    public void Generate_every_concept_passes_strict_Validate()
    {
        var snapshot = new RepositorySnapshot("/repo", "my-repo",
            [new PackageManifest("npm", "package.json", "my-lib", "A little library.")],
            [new DocFile("README.md", "My Great Tool")]);

        var concepts = new ConceptGenerator().Generate(snapshot);

        foreach (var concept in concepts)
        {
            concept.Document.Validate();
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~ConceptGeneratorTests"`
Expected: FAIL to compile — `GeneratedConcept`/`IConceptGenerator`/`ConceptGenerator` don't exist yet.

- [ ] **Step 3: Implement**

Create `producers/src/OkfProducer.Core/Generation/GeneratedConcept.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <summary>A generated concept, paired with the id it will be written under.</summary>
public sealed record GeneratedConcept(ConceptId Id, OkfDocument Document);
```

Create `producers/src/OkfProducer.Core/Generation/IConceptGenerator.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Scanning;

namespace OkfProducer.Core.Generation;

/// <summary>Turns a <see cref="RepositorySnapshot"/> into the OKF concepts describing it.</summary>
public interface IConceptGenerator
{
    /// <summary>Generates the concepts for <paramref name="snapshot"/>, each paired with its concept id.</summary>
    IReadOnlyList<GeneratedConcept> Generate(RepositorySnapshot snapshot);
}
```

Create `producers/src/OkfProducer.Core/Generation/ConceptGenerator.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Scanning;

namespace OkfProducer.Core.Generation;

/// <summary>
/// Maps a <see cref="RepositorySnapshot"/> to concepts via <see cref="OkfDocumentBuilder"/>: one
/// repository overview (fixed id <c>overview</c>), one <c>packages/&lt;slug&gt;</c> concept per
/// detected package, and one <c>docs/&lt;slug&gt;</c> concept per detected doc. A concept id
/// collision (two names slugifying to the same segment) is disambiguated with a numeric suffix
/// (<c>-2</c>, <c>-3</c>, ...) -- <see cref="ConceptId.Slugify"/> itself never deduplicates, that
/// responsibility belongs to its caller (this class).
/// </summary>
public sealed class ConceptGenerator : IConceptGenerator
{
    /// <inheritdoc/>
    public IReadOnlyList<GeneratedConcept> Generate(RepositorySnapshot snapshot)
    {
        var results = new List<GeneratedConcept>
        {
            new(ConceptId.Parse("overview"), BuildOverview(snapshot)),
        };

        var usedIds = new HashSet<string>(StringComparer.Ordinal) { "overview" };

        foreach (var package in snapshot.Packages)
        {
            var id = UniqueConceptId("packages", package.Name, usedIds);
            results.Add(new GeneratedConcept(id, BuildPackageConcept(package)));
        }

        foreach (var doc in snapshot.Docs)
        {
            var id = UniqueConceptId("docs", doc.Title, usedIds);
            results.Add(new GeneratedConcept(id, BuildDocConcept(doc)));
        }

        return results;
    }

    private static ConceptId UniqueConceptId(string prefix, string name, HashSet<string> usedIds)
    {
        var baseSlug = ConceptId.Slugify(name);
        var candidate = $"{prefix}/{baseSlug}";
        var suffix = 2;
        while (!usedIds.Add(candidate))
        {
            candidate = $"{prefix}/{baseSlug}-{suffix}";
            suffix++;
        }

        return ConceptId.Parse(candidate);
    }

    private static OkfDocument BuildOverview(RepositorySnapshot snapshot)
    {
        var description = snapshot.Packages.Count switch
        {
            0 => $"Repository {snapshot.RepoName}.",
            1 => $"Repository {snapshot.RepoName}, containing 1 detected package.",
            var n => $"Repository {snapshot.RepoName}, containing {n} detected packages.",
        };

        return OkfDocumentBuilder
            .ForType("Repository")
            .Title(snapshot.RepoName)
            .Description(description)
            .Body($"# {snapshot.RepoName}\n\n{description}\n")
            .Build();
    }

    private static OkfDocument BuildPackageConcept(PackageManifest package)
    {
        var description = package.Description ?? $"{package.Ecosystem} package {package.Name}.";

        return OkfDocumentBuilder
            .ForType("Package")
            .Title(package.Name)
            .Description(description)
            .Tags(package.Ecosystem)
            .AddSource(resource: package.RelativePath)
            .Body($"# {package.Name}\n\n{description}\n")
            .Build();
    }

    private static OkfDocument BuildDocConcept(DocFile doc)
    {
        return OkfDocumentBuilder
            .ForType("Documentation")
            .Title(doc.Title)
            .Description($"Repository documentation file {doc.RelativePath}.")
            .AddSource(resource: doc.RelativePath)
            .Body($"# {doc.Title}\n\nSee `{doc.RelativePath}` in the repository.\n")
            .Build();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~ConceptGeneratorTests"`
Expected: PASS (all 7 tests).

- [ ] **Step 5: Format and commit**

```bash
dotnet format OkfProducer.sln
git add producers/src/OkfProducer.Core/Generation producers/tests/OkfProducer.Tests/Generation
git commit -m "feat(producers): add ConceptGenerator (overview/package/doc concepts via OkfDocumentBuilder)"
```

---

## Task 4: Bundle writing

**Files:**
- Create: `producers/src/OkfProducer.Core/Generation/WritePolicy.cs`
- Create: `producers/src/OkfProducer.Core/Generation/WriteResult.cs`
- Create: `producers/src/OkfProducer.Core/Generation/IBundleWriter.cs`
- Create: `producers/src/OkfProducer.Core/Generation/BundleWriter.cs`
- Test: `producers/tests/OkfProducer.Tests/Generation/BundleWriterTests.cs`

**Interfaces:**
- Consumes: `GeneratedConcept` (Task 3), `OKF4net.BundleConceptWriter.WriteConcept(string, Frontmatter, string)`, `OKF4net.IndexGenerator.RegenerateIndexes(string)`.
- Produces: `WritePolicy` (enum: `RequireEmpty`, `Update`, `Reset`), `WriteResult(int Written, IReadOnlyList<(ConceptId Id, string Error)> Failures)`, `IBundleWriter.Write(string outPath, IReadOnlyList<GeneratedConcept> concepts, WritePolicy policy) -> WriteResult` — consumed by Task 6.

- [ ] **Step 1: Write the failing tests**

Create `producers/tests/OkfProducer.Tests/Generation/BundleWriterTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Generation;

namespace OkfProducer.Tests.Generation;

public class BundleWriterTests
{
    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "okfproducer-write-" + Guid.NewGuid());
        return path;
    }

    private static GeneratedConcept SampleConcept(string id = "overview") =>
        new(ConceptId.Parse(id),
            OkfDocumentBuilder.ForType("Repository").Title("t").Description("d").Body("# t\n").Build());

    [Fact]
    public void Write_to_a_missing_directory_creates_it_and_writes_all_concepts()
    {
        var outPath = CreateTempDir();
        try
        {
            var result = new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.RequireEmpty);

            Assert.Equal(1, result.Written);
            Assert.Empty(result.Failures);
            Assert.True(File.Exists(Path.Combine(outPath, "overview.md")));
            Assert.True(File.Exists(Path.Combine(outPath, "index.md")));
        }
        finally
        {
            if (Directory.Exists(outPath)) Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_RequireEmpty_into_a_non_empty_directory_throws_and_writes_nothing()
    {
        var outPath = CreateTempDir();
        Directory.CreateDirectory(outPath);
        File.WriteAllText(Path.Combine(outPath, "existing.txt"), "pre-existing");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.RequireEmpty));

            Assert.False(File.Exists(Path.Combine(outPath, "overview.md")));
            Assert.True(File.Exists(Path.Combine(outPath, "existing.txt")));
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_Update_into_a_non_empty_directory_preserves_untouched_files()
    {
        var outPath = CreateTempDir();
        Directory.CreateDirectory(outPath);
        File.WriteAllText(Path.Combine(outPath, "hand-written.md"), "---\ntype: Note\n---\n\nkept\n");
        try
        {
            var result = new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.Update);

            Assert.Equal(1, result.Written);
            Assert.True(File.Exists(Path.Combine(outPath, "overview.md")));
            Assert.True(File.Exists(Path.Combine(outPath, "hand-written.md")));
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_Reset_deletes_and_recreates_the_directory()
    {
        var outPath = CreateTempDir();
        Directory.CreateDirectory(outPath);
        File.WriteAllText(Path.Combine(outPath, "stale.md"), "---\ntype: Note\n---\n\nstale\n");
        try
        {
            var result = new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.Reset);

            Assert.Equal(1, result.Written);
            Assert.False(File.Exists(Path.Combine(outPath, "stale.md")));
            Assert.True(File.Exists(Path.Combine(outPath, "overview.md")));
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }

    [Fact]
    public void Write_regenerates_the_index_after_writing_concepts()
    {
        var outPath = CreateTempDir();
        try
        {
            new BundleWriter().Write(outPath, [SampleConcept()], WritePolicy.RequireEmpty);

            // "t" alone would match almost any generated text (e.g. inside "Contents"); assert on the
            // actual link target IndexGenerator emits for the one concept we wrote, so this only
            // passes if the index genuinely reflects that concept.
            var indexText = File.ReadAllText(Path.Combine(outPath, "index.md"));
            Assert.Contains("overview.md", indexText);
        }
        finally
        {
            Directory.Delete(outPath, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~BundleWriterTests"`
Expected: FAIL to compile — `WritePolicy`/`WriteResult`/`IBundleWriter`/`BundleWriter` don't exist yet.

- [ ] **Step 3: Implement**

Create `producers/src/OkfProducer.Core/Generation/WritePolicy.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Generation;

/// <summary>How <see cref="IBundleWriter"/> treats a non-empty output directory.</summary>
public enum WritePolicy
{
    /// <summary>Refuse to write unless the output directory is empty or missing (the default).</summary>
    RequireEmpty,

    /// <summary>Write into a non-empty directory, preserving files this run doesn't generate.</summary>
    Update,

    /// <summary>Delete the output directory (if it exists) and recreate it before writing.</summary>
    Reset,
}
```

Create `producers/src/OkfProducer.Core/Generation/WriteResult.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <summary>
/// The outcome of a <see cref="IBundleWriter.Write"/> call. A per-concept write failure (e.g. a
/// permission error on one specific file) is reported in <see cref="Failures"/>, not thrown --
/// it does not stop the rest of the concepts from being written.
/// </summary>
public sealed record WriteResult(int Written, IReadOnlyList<(ConceptId Id, string Error)> Failures);
```

Create `producers/src/OkfProducer.Core/Generation/IBundleWriter.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Generation;

/// <summary>Writes generated concepts to an OKF bundle directory and regenerates its index.</summary>
public interface IBundleWriter
{
    /// <summary>
    /// Writes <paramref name="concepts"/> to <paramref name="outPath"/> under <paramref name="policy"/>,
    /// then regenerates the bundle's index files.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="policy"/> is <see cref="WritePolicy.RequireEmpty"/> and <paramref name="outPath"/>
    /// already exists and is non-empty. Nothing is written in this case.
    /// </exception>
    WriteResult Write(string outPath, IReadOnlyList<Generation.GeneratedConcept> concepts, WritePolicy policy);
}
```

Create `producers/src/OkfProducer.Core/Generation/BundleWriter.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Generation;

/// <inheritdoc cref="IBundleWriter"/>
public sealed class BundleWriter : IBundleWriter
{
    /// <inheritdoc/>
    public WriteResult Write(string outPath, IReadOnlyList<GeneratedConcept> concepts, WritePolicy policy)
    {
        if (policy == WritePolicy.Reset && Directory.Exists(outPath))
        {
            Directory.Delete(outPath, recursive: true);
        }

        if (policy == WritePolicy.RequireEmpty && Directory.Exists(outPath) && Directory.EnumerateFileSystemEntries(outPath).Any())
        {
            throw new InvalidOperationException(
                $"Output directory '{outPath}' is not empty. Use --update or --reset.");
        }

        Directory.CreateDirectory(outPath);

        var writer = new BundleConceptWriter(outPath);
        var failures = new List<(ConceptId, string)>();
        var written = 0;

        foreach (var concept in concepts)
        {
            var result = writer.WriteConcept(concept.Id.ToString(), concept.Document.Frontmatter, concept.Document.Body);
            if (result.StartsWith("Error:", StringComparison.Ordinal))
            {
                failures.Add((concept.Id, result));
            }
            else
            {
                written++;
            }
        }

        IndexGenerator.RegenerateIndexes(outPath);

        return new WriteResult(written, failures);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~BundleWriterTests"`
Expected: PASS (all 5 tests).

- [ ] **Step 5: Format and commit**

```bash
dotnet format OkfProducer.sln
git add producers/src/OkfProducer.Core/Generation/WritePolicy.cs producers/src/OkfProducer.Core/Generation/WriteResult.cs producers/src/OkfProducer.Core/Generation/IBundleWriter.cs producers/src/OkfProducer.Core/Generation/BundleWriter.cs producers/tests/OkfProducer.Tests/Generation/BundleWriterTests.cs
git commit -m "feat(producers): add BundleWriter (write policy + IndexGenerator reuse)"
```

---

## Task 5: Bundle validation

**Files:**
- Create: `producers/src/OkfProducer.Core/Validation/ValidationOutcome.cs`
- Create: `producers/src/OkfProducer.Core/Validation/IBundleValidationRunner.cs`
- Create: `producers/src/OkfProducer.Core/Validation/BundleValidationRunner.cs`
- Test: `producers/tests/OkfProducer.Tests/Validation/BundleValidationRunnerTests.cs`

**Interfaces:**
- Consumes: `OKF4net.Bundle.Load(string)`, `OKF4net.BundleValidator.Validate(Bundle, IOkfClock?)`.
- Produces: `ValidationOutcome(int ErrorCount, int WarningCount, IReadOnlyList<string> DiagnosticLines)` (with `IsConformant` computed property), `IBundleValidationRunner.Validate(string bundleRoot) -> ValidationOutcome` — consumed by Task 6.

- [ ] **Step 1: Write the failing tests**

Create `producers/tests/OkfProducer.Tests/Validation/BundleValidationRunnerTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Validation;

namespace OkfProducer.Tests.Validation;

public class BundleValidationRunnerTests
{
    private static string CreateTempBundle()
    {
        var path = Path.Combine(Path.GetTempPath(), "okfproducer-validate-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Validate_a_conformant_bundle_reports_zero_errors()
    {
        var bundleRoot = CreateTempBundle();
        try
        {
            File.WriteAllText(Path.Combine(bundleRoot, "overview.md"),
                "---\ntype: Repository\ntitle: t\ndescription: d\n---\n\n# t\n");

            var outcome = new BundleValidationRunner().Validate(bundleRoot);

            Assert.Equal(0, outcome.ErrorCount);
            Assert.True(outcome.IsConformant);
        }
        finally
        {
            Directory.Delete(bundleRoot, recursive: true);
        }
    }

    [Fact]
    public void Validate_a_bundle_missing_type_reports_an_error()
    {
        var bundleRoot = CreateTempBundle();
        try
        {
            File.WriteAllText(Path.Combine(bundleRoot, "broken.md"), "---\ntitle: t\n---\n\nbody\n");

            var outcome = new BundleValidationRunner().Validate(bundleRoot);

            Assert.True(outcome.ErrorCount > 0);
            Assert.False(outcome.IsConformant);
            Assert.NotEmpty(outcome.DiagnosticLines);
        }
        finally
        {
            Directory.Delete(bundleRoot, recursive: true);
        }
    }

    [Fact]
    public void Validate_a_missing_directory_throws_BundleLoadException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "okfproducer-does-not-exist-" + Guid.NewGuid());

        Assert.Throws<BundleLoadException>(() => new BundleValidationRunner().Validate(missingPath));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~BundleValidationRunnerTests"`
Expected: FAIL to compile — `ValidationOutcome`/`IBundleValidationRunner`/`BundleValidationRunner` don't exist yet.

- [ ] **Step 3: Implement**

Create `producers/src/OkfProducer.Core/Validation/ValidationOutcome.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Validation;

/// <summary>The result of validating a bundle: rendered diagnostic lines plus error/warning counts.</summary>
public sealed record ValidationOutcome(int ErrorCount, int WarningCount, IReadOnlyList<string> DiagnosticLines)
{
    /// <summary>True if there are no errors (warnings do not affect conformance).</summary>
    public bool IsConformant => ErrorCount == 0;
}
```

Create `producers/src/OkfProducer.Core/Validation/IBundleValidationRunner.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Validation;

/// <summary>Thin wrapper around <c>OKF4net.BundleValidator</c> for the <c>validate</c> command.</summary>
public interface IBundleValidationRunner
{
    /// <summary>Loads and validates the bundle at <paramref name="bundleRoot"/>.</summary>
    /// <exception cref="OKF4net.BundleLoadException"><paramref name="bundleRoot"/> does not exist or is not a directory.</exception>
    ValidationOutcome Validate(string bundleRoot);
}
```

Create `producers/src/OkfProducer.Core/Validation/BundleValidationRunner.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Validation;

/// <inheritdoc cref="IBundleValidationRunner"/>
public sealed class BundleValidationRunner : IBundleValidationRunner
{
    /// <inheritdoc/>
    public ValidationOutcome Validate(string bundleRoot)
    {
        var bundle = Bundle.Load(bundleRoot);
        var report = BundleValidator.Validate(bundle);
        var lines = report.Diagnostics.Select(d => d.ToString()).ToList();

        return new ValidationOutcome(report.ErrorCount, report.WarningCount, lines);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~BundleValidationRunnerTests"`
Expected: PASS (all 3 tests).

- [ ] **Step 5: Format and commit**

```bash
dotnet format OkfProducer.sln
git add producers/src/OkfProducer.Core/Validation producers/tests/OkfProducer.Tests/Validation
git commit -m "feat(producers): add BundleValidationRunner (thin BundleValidator wrapper)"
```

---

## Task 6: CLI wiring (`generate` + `validate`)

**Files:**
- Modify: `producers/src/OkfProducer.Cli/Program.cs` (replace the `dotnet new console` placeholder)

**Interfaces:**
- Consumes: `IRepositoryScanner`/`RepositoryScanner` (Task 2), `IConceptGenerator`/`ConceptGenerator` (Task 3), `IBundleWriter`/`BundleWriter` (Task 4), `IBundleValidationRunner`/`BundleValidationRunner` (Task 5).
- Produces: the `okfgen` executable's `generate`/`validate` subcommands — nothing else in this plan consumes this task's output; it is the plan's user-facing surface.

This task is verified by manually running the built CLI (see Step 3), not by an automated xunit test — `OkfProducer.Tests` does not reference `OkfProducer.Cli` (Task 1 deliberately wired it that way). System.CommandLine's `ParseResult.Invoke()` writes to the real console; wrapping it for in-process assertion is unnecessary complexity for a CLI this thin, when every piece of logic it calls is already unit-tested in Tasks 2-5.

- [ ] **Step 1: Replace Program.cs**

Replace the full contents of `producers/src/OkfProducer.Cli/Program.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OKF4net;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;
using OkfProducer.Core.Validation;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IRepositoryScanner, RepositoryScanner>();
builder.Services.AddSingleton<IConceptGenerator, ConceptGenerator>();
builder.Services.AddSingleton<IBundleWriter, BundleWriter>();
builder.Services.AddSingleton<IBundleValidationRunner, BundleValidationRunner>();
using var host = builder.Build();

var repoOption = new Option<string>("--repo") { Description = "Root of the repository to scan", Required = true };
var outOption = new Option<string>("--out") { Description = "Root of the OKF bundle to write", Required = true };
var updateOption = new Option<bool>("--update") { Description = "Allow writing into a non-empty --out, preserving files this run doesn't generate" };
var resetOption = new Option<bool>("--reset") { Description = "Delete and recreate --out before writing" };
var forceOption = new Option<bool>("--force") { Description = "Alias for --reset" };

var generateCommand = new Command("generate", "Generate an OKF bundle from a repository")
{
    Options = { repoOption, outOption, updateOption, resetOption, forceOption },
};

generateCommand.SetAction(parseResult =>
{
    var repo = parseResult.GetValue(repoOption)!;
    var outPath = parseResult.GetValue(outOption)!;
    var reset = parseResult.GetValue(resetOption) || parseResult.GetValue(forceOption);
    var update = parseResult.GetValue(updateOption);
    var policy = reset ? WritePolicy.Reset : update ? WritePolicy.Update : WritePolicy.RequireEmpty;

    var scanner = host.Services.GetRequiredService<IRepositoryScanner>();
    var generator = host.Services.GetRequiredService<IConceptGenerator>();
    var writer = host.Services.GetRequiredService<IBundleWriter>();

    try
    {
        var snapshot = scanner.Scan(repo);
        var concepts = generator.Generate(snapshot);
        var result = writer.Write(outPath, concepts, policy);

        Console.WriteLine($"Wrote {result.Written} concept(s) to {outPath}.");
        foreach (var (id, error) in result.Failures)
        {
            Console.Error.WriteLine($"error: {id}: {error}");
        }

        return result.Failures.Count > 0 ? 1 : 0;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
});

var okfOption = new Option<string>("--okf") { Description = "Root of the OKF bundle to validate", Required = true };

var validateCommand = new Command("validate", "Validate an OKF bundle")
{
    Options = { okfOption },
};

validateCommand.SetAction(parseResult =>
{
    var okfPath = parseResult.GetValue(okfOption)!;
    var validator = host.Services.GetRequiredService<IBundleValidationRunner>();

    try
    {
        var outcome = validator.Validate(okfPath);

        foreach (var line in outcome.DiagnosticLines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine($"{outcome.ErrorCount} error(s), {outcome.WarningCount} warning(s).");
        return outcome.IsConformant ? 0 : 1;
    }
    catch (BundleLoadException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
});

var rootCommand = new RootCommand("okfgen -- generate and validate OKF bundles from a repository")
{
    Subcommands = { generateCommand, validateCommand },
};

return rootCommand.Parse(args).Invoke();
```

- [ ] **Step 2: Build**

Run: `cd producers && dotnet build OkfProducer.sln`
Expected: build succeeds, 0 warnings, 0 errors.

- [ ] **Step 3: Manually smoke-test both commands**

```bash
cd /tmp   # or any scratch directory outside the repo
mkdir okfgen-smoke-repo && cd okfgen-smoke-repo
echo '{ "name": "smoke-lib", "description": "A smoke-test package." }' > package.json
printf '# Smoke Repo\n\nHello.\n' > README.md
cd ..
dotnet run --project <repo-root>/producers/src/OkfProducer.Cli -- generate --repo okfgen-smoke-repo --out okfgen-smoke-out
cat okfgen-smoke-out/overview.md
cat okfgen-smoke-out/packages/smoke-lib.md
dotnet run --project <repo-root>/producers/src/OkfProducer.Cli -- validate --okf okfgen-smoke-out
```

Expected: `generate` prints `Wrote 3 concept(s) to okfgen-smoke-out.` (overview + 1 package + 1 doc) and the two `cat`s show real frontmatter/body. `validate` should print `0 error(s)` (every concept has a non-empty `type`, the only conformance requirement, §11) with exit code 0 (check via `echo $?`). The warning count is **not** asserted here — this plan didn't audit every rule in `BundleValidator.Validate` (~600 lines), only confirmed the error-level conformance rule; if `validate` reports one or more warnings, read what they say and note it in your report rather than treating it as a failure (a real warning here — if there is one — is useful, actionable signal for a follow-up plan, not this task's problem to fix).

Re-run `generate` a second time without `--update`/`--reset` and confirm it prints `error: Output directory '...' is not empty. Use --update or --reset.` and exits non-zero.

- [ ] **Step 4: Commit**

```bash
git add producers/src/OkfProducer.Cli/Program.cs
git commit -m "feat(producers): wire generate/validate commands (System.CommandLine + Generic Host)"
```

---

## Task 7: Core pipeline end-to-end test

**Files:**
- Test: `producers/tests/OkfProducer.Tests/EndToEndTests.cs`

**Interfaces:** Consumes everything from Tasks 2-5 directly (no CLI layer involved) — this is the final integration proof for the plan.

- [ ] **Step 1: Write the test**

Create `producers/tests/OkfProducer.Tests/EndToEndTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;
using OkfProducer.Core.Validation;

namespace OkfProducer.Tests;

public class EndToEndTests
{
    [Fact]
    public void Scan_generate_write_validate_round_trip_on_a_small_fixture_repo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), "okfproducer-e2e-repo-" + Guid.NewGuid());
        var outPath = Path.Combine(Path.GetTempPath(), "okfproducer-e2e-out-" + Guid.NewGuid());
        Directory.CreateDirectory(repoPath);
        try
        {
            File.WriteAllText(Path.Combine(repoPath, "package.json"),
                """{ "name": "fixture-lib", "description": "A fixture package for the end-to-end test." }""");
            File.WriteAllText(Path.Combine(repoPath, "README.md"), "# Fixture Repo\n\nHello.\n");

            var snapshot = new RepositoryScanner().Scan(repoPath);
            var concepts = new ConceptGenerator().Generate(snapshot);
            var writeResult = new BundleWriter().Write(outPath, concepts, WritePolicy.RequireEmpty);

            Assert.Equal(3, writeResult.Written); // overview + 1 package + 1 doc
            Assert.Empty(writeResult.Failures);

            var validationOutcome = new BundleValidationRunner().Validate(outPath);

            Assert.True(validationOutcome.IsConformant, string.Join("\n", validationOutcome.DiagnosticLines));
            Assert.True(File.Exists(Path.Combine(outPath, "index.md")));
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
            if (Directory.Exists(outPath)) Directory.Delete(outPath, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run it**

Run: `cd producers && dotnet test OkfProducer.sln --filter "FullyQualifiedName~EndToEndTests"`
Expected: PASS.

- [ ] **Step 3: Run the full producer test suite and format check**

Run: `cd producers && dotnet test OkfProducer.sln`
Expected: all tests pass (25 total across Tasks 1-7: 1 smoke + 8 scanner + 7 generator + 5 writer + 3 validator + 1 e2e — recount against actual test run output, this is the expected tally from this plan's Steps).

Run: `dotnet format OkfProducer.sln --verify-no-changes`
Expected: exits 0, no output.

- [ ] **Step 4: Commit**

```bash
git add producers/tests/OkfProducer.Tests/EndToEndTests.cs
git commit -m "test(producers): add scan-generate-write-validate end-to-end test"
```

---

## Self-Review Notes (for the plan author, not a task to execute)

- **Spec coverage** (of the scope this plan claims, see "Scope of this plan" above): §2 solution layout → Task 1. §3 scanning (npm/NuGet/README slice) → Task 2. §3 concept generation + `ConceptId.Slugify` + dedup → Task 3. §3 write pipeline + `IndexGenerator` reuse → Task 4. §3 `validate` command → Task 5. §3 CLI flags (`--repo`/`--out`/`--update`/`--reset`/`--force`/`--okf`) → Task 6. §5 tests → each task's Step 1/Step 2 (unit) + Task 7 (end-to-end). §4 (LLM/cache), the rest of §3's flags (`--package-scope`, `--mode`, `--cache-dir`, `--no-cache`, `--quiet`, `--llm-*`), and §3's architecture/CLI/CI/test/config concepts are explicitly out of this plan's scope (see header) — not silently dropped.
- **No placeholders:** every step has complete, real C# — no "detect other ecosystems here" or "add more concept types" shorthand.
- **Type/signature consistency check:** `GeneratedConcept` (Task 3) is the exact type `IBundleWriter.Write` (Task 4) consumes. `WritePolicy`/`WriteResult` (Task 4) are the exact types Task 6's CLI handler branches on and prints. `IBundleValidationRunner`/`ValidationOutcome` (Task 5) are the exact types Task 6's `validate` handler consumes. Task 7 calls the concrete classes (`RepositoryScanner`, `ConceptGenerator`, `BundleWriter`, `BundleValidationRunner`) directly by design (no DI container in a plain xunit test) — same public constructors/methods Task 6 resolves via DI.
- **Verified against real APIs, not assumed:** `IndexGenerator.RegenerateIndexes(string) -> IReadOnlyList<string>` (read from `src/OKF4net/IndexGenerator.cs:95`), `Bundle.Load(string)` throws `BundleLoadException` for a missing directory (read from `src/OKF4net/Bundle.cs:100-111`), `BundleValidator.Validate(Bundle, IOkfClock?)` returns a `ValidationReport` with `ErrorCount`/`WarningCount`/`Diagnostics` (read from `src/OKF4net/Validate.cs`), `System.CommandLine` 2.0.0's stable object-initializer API (`Options = { ... }`, `SetAction`, `Option<T>.Required`, `RootCommand.Parse(args).Invoke()`) confirmed via Microsoft Learn's current tutorial, not the old beta `AddOption`/`Handler.SetHandler` API.
- **Known accepted gap, called out explicitly rather than silently skipped:** no `generated` provenance stamp is written (see "Scope of this plan" above) — `BundleConceptWriter.AutoStampGenerated` and `OkfTimestamp` are both `internal` to `OKF4net` and inaccessible from `producers/`.
- **Hand-traced every test against its implementation** (not just "should compile"), which caught and fixed three real issues before this plan was considered done:
  1. Task 1's scaffolding removed `Class1.cs` but not the xunit template's own default test file (`UnitTest1.cs`) — would have left a vacuous scaffold test in the suite. Fixed.
  2. Task 4's index-regeneration test asserted `Assert.Contains("t", indexText)` — a single-character match that would pass against almost any generated text regardless of correctness. Fixed to assert on the actual link target (`overview.md`) the one written concept produces.
  3. Task 7's expected total test count was miscounted (24, stated without re-adding the per-task counts) — the real sum across Tasks 1/2/3/4/5/7 is 25. Fixed.
- **One residual uncertainty, flagged rather than asserted with false confidence:** Task 6's manual smoke-test step expects `validate` to report 0 errors (verified: conformance requires only a non-empty `type`, which every generated concept has) but does **not** assert 0 warnings — `BundleValidator.Validate`'s full rule set (~600 lines) was not exhaustively audited during this review, only the error-level conformance rule. If the manual smoke test surfaces a warning, that is signal for a follow-up plan, not a sign this task's implementation is wrong.
