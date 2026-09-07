// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using System.Xml.Linq;
using OkfProducer.Core.Generation;

namespace OkfProducer.Core.Scanning;

/// <summary>
/// Detects npm (<c>package.json</c>, root only) and NuGet package manifests, and a root
/// <c>README.md</c>. NuGet projects are resolved from the project references of every <c>*.sln</c>
/// anywhere in the tree when there is at least one, otherwise by recursively walking for
/// <c>*.csproj</c>; both walks skip <c>bin</c>/<c>obj</c>/<c>.git</c>/<c>node_modules</c>. Malformed
/// manifests are skipped, not fatal -- permissive, matching the rest of this codebase's scan
/// philosophy.
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

        foreach (var csprojPath in ResolveCsprojPaths(repoPath))
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

    private static readonly string[] ExcludedDirectoryNames = ["bin", "obj", ".git", "node_modules"];

    /// <summary>
    /// Every <c>.csproj</c> this repository presents as a package: the union of what its solutions
    /// reference, or -- only when it has no solution at all -- every <c>.csproj</c> in the tree.
    ///
    /// <para><b>Why solutions are looked for at every depth, not just at the root.</b> A root-only
    /// lookup is not a narrower search, it is a different rule: the first root solution found decides
    /// the whole answer and silently discards every project belonging to a solution nested below it.
    /// Measured on the OKF4net repository, that rule detected the 9 projects of <c>OKF4net.sln</c> and
    /// missed the 8 others, so the 194 <c>code/</c> concepts under the <c>OkfProducer</c> namespace
    /// belonged to no package concept and formed a component nothing reached from <c>overview</c> --
    /// which <c>okf validate</c> does not report, because an orphan dangles nothing.</para>
    ///
    /// <para>What does NOT change: a <c>.csproj</c> that no solution references is still not a
    /// package. The recursive <c>.csproj</c> walk stays a fallback for solution-less repositories
    /// rather than becoming a union with it, so a test fixture or a scratch project sitting outside
    /// every solution keeps being excluded.</para>
    /// </summary>
    private static IReadOnlyList<string> ResolveCsprojPaths(string repoPath)
    {
        var slnPaths = EnumerateFilesRecursively(repoPath, "*.sln").ToList();
        if (slnPaths.Count == 0)
        {
            return EnumerateFilesRecursively(repoPath, "*.csproj")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // A solution's project paths are relative and may climb out of the repository (`..\..\Shared`).
        // Such a project has no repository-relative path to publish -- Path.GetRelativePath would emit
        // a `../..` prefix, which every downstream `resource` and `sources` field would then carry --
        // so it is not this repository's package to describe.
        var repoRoot = Path.GetFullPath(repoPath).TrimEnd(Path.DirectorySeparatorChar);

        return slnPaths
            .SelectMany(ParseSolutionProjectPaths)
            .Where(File.Exists)
            .Where(path => BundlePaths.IsInside(repoRoot, path))
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

    /// <summary>
    /// Every file matching <paramref name="searchPattern"/> at or below <paramref name="directory"/>,
    /// skipping <see cref="ExcludedDirectoryNames"/>. Shared by the solution walk and the
    /// <c>.csproj</c> fallback so the two cannot come to disagree about which directories are build
    /// output.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesRecursively(string directory, string searchPattern)
    {
        foreach (var file in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            yield return file;
        }

        foreach (var subDirectory in Directory.EnumerateDirectories(directory))
        {
            if (!ExcludedDirectoryNames.Contains(Path.GetFileName(subDirectory), StringComparer.OrdinalIgnoreCase))
            {
                foreach (var file in EnumerateFilesRecursively(subDirectory, searchPattern))
                {
                    yield return file;
                }
            }
        }
    }

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
}
