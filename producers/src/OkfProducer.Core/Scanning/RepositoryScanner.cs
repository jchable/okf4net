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
