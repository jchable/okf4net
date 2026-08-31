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
public sealed class CodeGraphBuilder(ILanguageExtractor extractor, IReadOnlyList<LanguageProfile> profiles, IReadOnlyList<ISymbolResolver> resolvers)
{
    /// <summary>
    /// Extracts every eligible file in <paramref name="snapshot"/>'s repository, concatenates and
    /// deterministically sorts the resulting symbols and edges, and aggregates a <see cref="RunStatus"/>.
    /// A file whose extension matches none of <paramref name="profiles"/> is skipped entirely: it is
    /// not extracted, not an error, and not a <see cref="FileStatus"/> skip reason -- it simply falls
    /// outside what this run understands, so it cannot make the run incomplete. <paramref name="limits"/>
    /// and <paramref name="scope"/> are threaded through for a later task's hostile-input and scoping
    /// policy (§2.1); Task 1 does not act on either yet.
    /// </summary>
    public CodeGraph Build(RepositorySnapshot snapshot, ExtractionLimits limits, ScopeOptions scope)
    {
        _ = limits;
        _ = scope;

        var results = new List<(string RelativePath, ExtractionResult Result)>();
        foreach (var relativePath in EnumerateFiles(snapshot.RepoPath))
        {
            var profile = SelectProfile(relativePath);
            if (profile is null)
            {
                continue;
            }

            var absolutePath = Path.Combine(snapshot.RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            results.Add((relativePath, extractor.Extract(relativePath, absolutePath, profile)));
        }

        // Sort keys must fully disambiguate every tie a real repository can produce: two overloads
        // of the same member in the same file tie on (Container, Name, RelativePath), so
        // StartOffset breaks that tie. Without a final key, LINQ's stable OrderBy would fall back to
        // input order, which is not a documented contract -- and Tasks 10/12 assert the generated
        // bundle byte-for-byte, so an unspecified tie order would surface there as an intermittent
        // failure.
        var symbolList = results.SelectMany(r => r.Result.Symbols).ToList();
        var symbols = symbolList
            .OrderBy(s => s.Container, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.RelativePath, StringComparer.Ordinal)
            .ThenBy(s => s.StartOffset)
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

        // Never sort by iterating the dictionary directly: materialise its values into a list first,
        // then sort that list explicitly. Two call sites to the same method from the same caller tie
        // on (CallerContainer, CallerName, CalledName) -- RelativePath then Offset (unique per call
        // site within a file) fully disambiguate, the same reasoning as the symbol sort above.
        var verdictList = verdicts.Values.ToList();
        var edges = verdictList
            .OrderBy(e => e.Site.CallerContainer, StringComparer.Ordinal)
            .ThenBy(e => e.Site.CallerName, StringComparer.Ordinal)
            .ThenBy(e => e.Site.CalledName, StringComparer.Ordinal)
            .ThenBy(e => e.Site.RelativePath, StringComparer.Ordinal)
            .ThenBy(e => e.Site.Offset)
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

    private LanguageProfile? SelectProfile(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        if (extension.Length == 0)
        {
            return null;
        }

        foreach (var profile in profiles)
        {
            foreach (var candidate in profile.FileExtensions)
            {
                if (string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase))
                {
                    return profile;
                }
            }
        }

        return null;
    }
}
