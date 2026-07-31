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
