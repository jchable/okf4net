// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Scanning;
using OkfProducer.Tests.TestSupport;

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
    public void Scan_csproj_finds_PackageId_and_Description_split_across_multiple_PropertyGroups()
    {
        var repo = CreateTempRepo();
        try
        {
            // Mirrors this very repo's own src/OKF4net/OKF4net.csproj shape: TargetFramework/Nullable in
            // one PropertyGroup, PackageId/Description in a separate one (Finding 5).
            File.WriteAllText(Path.Combine(repo, "MyTool.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
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
    public void Scan_readme_with_heading_that_trims_to_empty_falls_back_to_repo_name()
    {
        var repo = CreateTempRepo();
        try
        {
            // A literal "# " heading line: trims to an empty string, not null -- must still trigger
            // the repo-name fallback rather than leaving DocFile.Title empty (Finding 2).
            File.WriteAllText(Path.Combine(repo, "README.md"), "# \n\nSome text.\n");

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

    [Fact]
    public void Scan_finds_the_projects_of_a_solution_nested_below_the_root_solution()
    {
        var repo = CreateTempRepo();
        try
        {
            WriteProject(Path.Combine(repo, "src", "Root"), "RootLib");
            WriteProject(Path.Combine(repo, "nested", "src", "Leaf"), "LeafLib");
            WriteProject(Path.Combine(repo, "orphan"), "OrphanLib");

            File.WriteAllText(Path.Combine(repo, "Root.sln"), """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "RootLib", "src\Root\RootLib.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                """);
            File.WriteAllText(Path.Combine(repo, "nested", "Nested.sln"), """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "LeafLib", "src\Leaf\LeafLib.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                """);

            var snapshot = new RepositoryScanner().Scan(repo);

            // The root solution no longer decides the whole answer: the nested solution's project is
            // detected too. OrphanLib, referenced by neither, stays out -- the recursive .csproj walk
            // is still only the fallback for repositories with no solution at all.
            Assert.Equal(
                ["LeafLib", "RootLib"],
                snapshot.Packages.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_ignores_a_solution_project_reference_that_escapes_the_repository()
    {
        var repo = CreateTempRepo();
        var outside = Path.Combine(Path.GetDirectoryName(repo)!, Path.GetFileName(repo) + "-outside");
        try
        {
            WriteProject(Path.Combine(repo, "src", "Inside"), "InsideLib");
            WriteProject(outside, "OutsideLib");

            var escape = Path.GetRelativePath(repo, Path.Combine(outside, "OutsideLib.csproj")).Replace('/', '\\');
            File.WriteAllText(Path.Combine(repo, "Escape.sln"), $$"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "InsideLib", "src\Inside\InsideLib.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "OutsideLib", "{{escape}}", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                """);

            var snapshot = new RepositoryScanner().Scan(repo);

            // The escaping project exists and the solution names it, so only the containment check
            // keeps it out. Were it admitted, its `resource` would be a `../` path relative to a
            // bundle root it has no relation to.
            var pkg = Assert.Single(snapshot.Packages);
            Assert.Equal("InsideLib", pkg.Name);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    // --- E3: a link or an unreadable subdirectory/manifest is skipped, not fatal. ---

    [DirectoryLinkFact]
    public void Scan_completes_through_a_directory_junction_that_loops_back_to_the_repository_root_with_no_sln()
    {
        var repo = CreateTempRepo();
        string? loop = null;
        try
        {
            File.WriteAllText(Path.Combine(repo, "A.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <PackageId>A</PackageId>
                  </PropertyGroup>
                </Project>
                """);
            var subDir = Path.Combine(repo, "sub");
            Directory.CreateDirectory(subDir);
            loop = DirectoryLinks.Create(Path.Combine(subDir, "loop"), repo);

            var snapshot = new RepositoryScanner().Scan(repo);

            var pkg = Assert.Single(snapshot.Packages);
            Assert.Equal("A", pkg.Name);
        }
        finally
        {
            // Non-recursive: removes the link only, never descends through it into the repository it
            // loops back to.
            if (loop is not null)
            {
                Directory.Delete(loop);
            }

            Directory.Delete(repo, recursive: true);
        }
    }

    [DirectoryLinkFact]
    public void Scan_completes_through_a_directory_junction_that_loops_back_to_the_repository_root_with_a_root_sln()
    {
        var repo = CreateTempRepo();
        string? loop = null;
        try
        {
            File.WriteAllText(Path.Combine(repo, "A.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <PackageId>A</PackageId>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(repo, "S.sln"), """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A", "A.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                """);
            var subDir = Path.Combine(repo, "sub");
            Directory.CreateDirectory(subDir);
            loop = DirectoryLinks.Create(Path.Combine(subDir, "loop"), repo);

            var snapshot = new RepositoryScanner().Scan(repo);

            var pkg = Assert.Single(snapshot.Packages);
            Assert.Equal("A", pkg.Name);
        }
        finally
        {
            if (loop is not null)
            {
                Directory.Delete(loop);
            }

            Directory.Delete(repo, recursive: true);
        }
    }

    [DenyAceFact]
    public void Scan_completes_when_a_solution_less_csproj_denies_read()
    {
        var repo = CreateTempRepo();
        try
        {
            var csproj = Path.Combine(repo, "B.csproj");
            File.WriteAllText(csproj, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <PackageId>B</PackageId>
                  </PropertyGroup>
                </Project>
                """);

            using (DenyAce.Deny(csproj, isDirectory: false))
            {
                var snapshot = new RepositoryScanner().Scan(repo);

                Assert.Empty(snapshot.Packages);
            }
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [DenyAceFact]
    public void Scan_completes_when_one_of_several_solutions_denies_read()
    {
        var repo = CreateTempRepo();
        try
        {
            WriteProject(Path.Combine(repo, "src", "A"), "A");
            File.WriteAllText(Path.Combine(repo, "Good.sln"), """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A", "src\A\A.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                """);
            var badSln = Path.Combine(repo, "Bad.sln");
            File.WriteAllText(badSln, """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Ghost", "src\Ghost\Ghost.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                """);

            using (DenyAce.Deny(badSln, isDirectory: false))
            {
                var snapshot = new RepositoryScanner().Scan(repo);

                // Bad.sln contributes nothing -- it cannot even be read -- but Good.sln's project is
                // still found, proving the unreadable solution does not abort the whole resolution.
                var pkg = Assert.Single(snapshot.Packages);
                Assert.Equal("A", pkg.Name);
            }
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [DenyAceFact]
    public void Scan_still_yields_the_readme_doc_entry_titled_with_the_repo_name_when_it_denies_read()
    {
        var repo = CreateTempRepo();
        try
        {
            var readme = Path.Combine(repo, "README.md");
            File.WriteAllText(readme, "# A Title This Run Cannot Read\n");

            using (DenyAce.Deny(readme, isDirectory: false))
            {
                var snapshot = new RepositoryScanner().Scan(repo);

                // The file exists, so it is still a doc entry; only its title falls back, because
                // BuildDocConcept never reads its content, only the title Scan hands it.
                var doc = Assert.Single(snapshot.Docs);
                Assert.Equal("README.md", doc.RelativePath);
                Assert.Equal(new DirectoryInfo(repo).Name, doc.Title);
            }
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [DenyAceFact]
    public void Scan_completes_when_package_json_denies_read()
    {
        var repo = CreateTempRepo();
        try
        {
            var packageJson = Path.Combine(repo, "package.json");
            File.WriteAllText(packageJson, """{ "name": "unreadable-lib" }""");

            using (DenyAce.Deny(packageJson, isDirectory: false))
            {
                var snapshot = new RepositoryScanner().Scan(repo);

                Assert.Empty(snapshot.Packages);
            }
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [DenyAceFact]
    public void Scan_completes_past_a_subdirectory_that_denies_listing()
    {
        var repo = CreateTempRepo();
        var locked = Path.Combine(repo, "locked");
        Directory.CreateDirectory(locked);
        try
        {
            WriteProject(Path.Combine(repo, "ok"), "A");

            using (DenyAce.Deny(locked, isDirectory: true))
            {
                var snapshot = new RepositoryScanner().Scan(repo);

                var pkg = Assert.Single(snapshot.Packages);
                Assert.Equal("A", pkg.Name);
            }
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [UnixOnlyFact]
    public void Scan_completes_and_skips_a_dangling_csproj_symlink()
    {
        var repo = CreateTempRepo();
        try
        {
            File.CreateSymbolicLink(Path.Combine(repo, "x.csproj"), Path.Combine(repo, "does-not-exist.csproj"));

            var snapshot = new RepositoryScanner().Scan(repo);

            Assert.Empty(snapshot.Packages);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    // A pin, not a regression test: this shape was already correct before E3 (`EnumerateFiles` never
    // yields a directory, and `.Where(File.Exists)` filters a solution's reference to one out), and
    // stays that way after it -- named ALREADY_GREEN so nobody mistakes it for evidence E3 changed
    // this behaviour.
    [Fact]
    public void Scan_ignores_a_directory_literally_named_dot_csproj_ALREADY_GREEN_no_sln()
    {
        var repo = CreateTempRepo();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "y.csproj"));

            var snapshot = new RepositoryScanner().Scan(repo);

            Assert.Empty(snapshot.Packages);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public void Scan_ignores_a_directory_literally_named_dot_csproj_ALREADY_GREEN_with_sln()
    {
        var repo = CreateTempRepo();
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, "y.csproj"));
            File.WriteAllText(Path.Combine(repo, "S.sln"), """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "y", "y.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                """);

            var snapshot = new RepositoryScanner().Scan(repo);

            Assert.Empty(snapshot.Packages);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    private static void WriteProject(string directory, string packageId)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, packageId + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <PackageId>{packageId}</PackageId>
              </PropertyGroup>
            </Project>
            """);
    }
}
