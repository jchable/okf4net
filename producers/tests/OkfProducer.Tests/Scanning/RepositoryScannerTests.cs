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
}
