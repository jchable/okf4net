// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Threading;
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
    /// A file whose extension matches none of <paramref name="profiles"/>, or that
    /// <see cref="FileEligibility.IsEligible"/> excludes under <paramref name="scope"/> (§5.4), is
    /// skipped entirely: neither is an error or a <see cref="FileStatus"/> skip reason, since both
    /// simply fall outside what this run is asked to cover, so neither can make the run incomplete.
    /// A file this run does attempt is still subject to <paramref name="limits"/>'s hostile-input
    /// guards (§2.3), enforced by <see cref="ILanguageExtractor.Extract"/> itself; a real skip from
    /// one of those guards -- or the overall <paramref name="limits"/>.<see cref="ExtractionLimits.Timeout"/>
    /// elapsing before every file is attempted -- does make the run incomplete (§2.3's closing rule).
    /// Extracted symbols are further filtered by <see cref="FileEligibility.IsInScope"/> before being
    /// returned, so an out-of-scope member never reaches <see cref="CodeGraph.Symbols"/>.
    /// </summary>
    public CodeGraph Build(RepositorySnapshot snapshot, ExtractionLimits limits, ScopeOptions scope)
    {
        using var timeoutSource = new CancellationTokenSource(limits.Timeout);

        var results = new List<(string RelativePath, ExtractionResult Result)>();
        var timedOut = false;

        foreach (var relativePath in EnumerateFiles(snapshot.RepoPath))
        {
            var profile = SelectProfile(relativePath);
            if (profile is null)
            {
                continue;
            }

            if (!FileEligibility.IsEligible(relativePath, snapshot, scope))
            {
                continue;
            }

            if (timeoutSource.IsCancellationRequested)
            {
                // §2.3: a run that hits its overall timeout before attempting every file is partial,
                // not complete -- the files never attempted are silently absent rather than reported
                // with a per-file FileStatus, but timedOut alone is enough to keep IsComplete false,
                // which is the property Task 11's pruning actually keys off.
                timedOut = true;
                break;
            }

            if (ExceedsMaxDepth(relativePath, limits.MaxDepth))
            {
                results.Add((relativePath, new ExtractionResult([], [], FileStatus.SkippedDepth)));
                continue;
            }

            var absolutePath = Path.Combine(snapshot.RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            results.Add((relativePath, extractor.Extract(relativePath, absolutePath, profile, limits)));
        }

        // Sort keys must fully disambiguate every tie a real repository can produce: two overloads
        // of the same member in the same file tie on (Container, Name, RelativePath), so
        // StartOffset breaks that tie. Without a final key, LINQ's stable OrderBy would fall back to
        // input order, which is not a documented contract -- and Tasks 10/12 assert the generated
        // bundle byte-for-byte, so an unspecified tie order would surface there as an intermittent
        // failure.
        var symbolList = results.SelectMany(r => r.Result.Symbols)
            .Where(s => FileEligibility.IsInScope(s, scope))
            .ToList();
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

        // A timed-out run is partial even when every file attempted before the timeout extracted
        // cleanly -- the files never reached are indistinguishable from "not modified", exactly the
        // ambiguity RunStatus.IsComplete exists to rule out (§2.3, §6.3).
        var status = skipped.Count == 0 && !timedOut ? RunStatus.Complete : new RunStatus(false, skipped);

        return new CodeGraph(symbols, edges, status);
    }

    /// <summary>
    /// §2.3's pathological-nesting-depth guard: counts <paramref name="relativePath"/>'s directory
    /// segments (its <c>/</c> separators) against <paramref name="maxDepth"/> before any file handle
    /// is opened -- purely a path check, so it belongs in the walk rather than in
    /// <see cref="ILanguageExtractor.Extract"/>, which never sees paths this shallow-checked walk has
    /// already ruled pathological.
    /// </summary>
    private static bool ExceedsMaxDepth(string relativePath, int maxDepth) =>
        relativePath.Count(c => c == '/') > maxDepth;

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
