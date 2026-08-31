// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Scanning;

namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Runs an <see cref="ILanguageExtractor"/> over every file in a repository, then chains the
/// configured <see cref="ISymbolResolver"/>s to resolve each call site: each resolver, in order,
/// overrides the verdict for the files it <see cref="ISymbolResolver.Owns"/> (matching on the
/// site's relative path and offset), so a missing or non-owning resolver degrades precision, never
/// the shape of the output (§2.1).
/// </summary>
public sealed class CodeGraphBuilder(ILanguageExtractor extractor, IReadOnlyList<ISymbolResolver> resolvers)
{
    // Task 1 has no real LanguageProfile to select per file (that wiring -- e.g. routing by file
    // extension -- belongs to whatever later task composes a multi-language ILanguageExtractor).
    // This placeholder is what's passed until then; the single extractor Task 1's tests exercise
    // never reads it.
    private static readonly LanguageProfile PlaceholderProfile = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

    /// <summary>
    /// Extracts every eligible file in <paramref name="snapshot"/>'s repository, concatenates and
    /// deterministically sorts the resulting symbols and edges, and aggregates a <see cref="RunStatus"/>.
    /// <paramref name="limits"/> and <paramref name="scope"/> are threaded through for a later task's
    /// hostile-input and scoping policy (§2.1); Task 1 does not act on either yet.
    /// </summary>
    public CodeGraph Build(RepositorySnapshot snapshot, ExtractionLimits limits, ScopeOptions scope)
    {
        _ = limits;
        _ = scope;

        var results = new List<(string RelativePath, ExtractionResult Result)>();
        foreach (var relativePath in EnumerateFiles(snapshot.RepoPath))
        {
            var absolutePath = Path.Combine(snapshot.RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            results.Add((relativePath, extractor.Extract(relativePath, absolutePath, PlaceholderProfile)));
        }

        var symbols = results
            .SelectMany(r => r.Result.Symbols)
            .OrderBy(s => s.Container, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.RelativePath, StringComparer.Ordinal)
            .ToList();

        var sites = results.SelectMany(r => r.Result.Sites).ToList();

        var verdicts = new Dictionary<(string RelativePath, int Offset), ResolvedEdge>();
        foreach (var site in sites)
        {
            verdicts[(site.RelativePath, site.Offset)] = new ResolvedEdge(site, TargetContainer: null, TargetName: null, EdgeConfidence.Unresolved);
        }

        foreach (var resolver in resolvers)
        {
            var ownedSites = sites.Where(s => resolver.Owns(s.RelativePath)).ToList();
            if (ownedSites.Count == 0)
            {
                continue;
            }

            foreach (var edge in resolver.Resolve(ownedSites, symbols))
            {
                verdicts[(edge.Site.RelativePath, edge.Site.Offset)] = edge;
            }
        }

        var edges = verdicts.Values
            .OrderBy(e => e.Site.CallerContainer, StringComparer.Ordinal)
            .ThenBy(e => e.Site.CallerName, StringComparer.Ordinal)
            .ThenBy(e => e.Site.CalledName, StringComparer.Ordinal)
            .ToList();

        var skipped = results
            .Where(r => r.Result.Status != FileStatus.Extracted)
            .Select(r => (r.RelativePath, r.Result.Status))
            .ToList();

        var status = skipped.Count == 0 ? RunStatus.Complete : new RunStatus(false, skipped);

        return new CodeGraph(symbols, edges, status);
    }

    private static IEnumerable<string> EnumerateFiles(string repoPath) =>
        Directory.Exists(repoPath)
            ? Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(repoPath, path).Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
            : [];
}
