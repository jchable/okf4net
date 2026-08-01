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
            var writeResult = new BundleWriter().Write(outPath, concepts, WritePolicy.RequireEmpty, repoPath);

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
}
