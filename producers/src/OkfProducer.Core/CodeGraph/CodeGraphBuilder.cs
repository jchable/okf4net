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
    /// simply fall outside what this run is asked to cover, so neither affects
    /// <see cref="RunStatus"/> at all. A file this run does attempt is subject to
    /// <paramref name="limits"/>'s hostile-input guards (§2.3), enforced by
    /// <see cref="ILanguageExtractor.Extract"/> itself, and its outcome -- clean or not -- is always
    /// recorded in <see cref="RunStatus.Skipped"/>; a skip from one of those guards affects
    /// <see cref="RunStatus.IsComplete"/> but, on its own, leaves <see cref="RunStatus.TraversalComplete"/>
    /// <see langword="true"/> (the file WAS visited). Only <paramref name="limits"/>.<see cref="ExtractionLimits.Timeout"/>
    /// being found already elapsed at a between-files checkpoint, <paramref name="cancellationToken"/>
    /// itself being cancelled, or the walk failing outright (a missing/unreadable repository root, or
    /// an enumeration failure such as a circular reparse point) before every eligible file is even
    /// visited flips <see cref="RunStatus.TraversalComplete"/> to <see langword="false"/> -- see
    /// <see cref="RunStatus"/>'s own doc comment for why that distinction exists and matters to Task
    /// 11's pruning gate. <paramref name="cancellationToken"/> defaults to <see langword="default"/>
    /// (never cancelled by the caller) and is linked internally with the timeout, so a caller does not
    /// have to fabricate a timeout to test cancellation, or vice versa.
    ///
    /// <para>
    /// <b>Both deadlines are checked between files only, never inside one.</b> The loop below tests
    /// them once per eligible file, after the profile and eligibility checks and before the
    /// <see cref="ExtractionLimits.MaxDepth"/> check that immediately precedes
    /// <see cref="ILanguageExtractor.Extract"/>; once inside that call there is no further
    /// checkpoint, so an extraction that does not return does not return, whatever the clock says and
    /// whatever the caller does with its token. What this method therefore guarantees is that it
    /// STARTS no further file after either deadline -- not that it finishes within one.
    /// <see cref="ExtractionLimits.Timeout"/>'s own doc comment records why the shipped extractor
    /// cannot offer more (the parser it wraps exposes no per-parse bound at all) and what would have
    /// to change for it to.
    /// </para>
    ///
    /// <para>
    /// Extracted symbols are filtered by <see cref="FileEligibility.IsInScope"/> before being
    /// returned, so an out-of-scope member never reaches <see cref="CodeGraph.Symbols"/> -- but each
    /// <see cref="ISymbolResolver"/> is handed the set unfiltered by THAT check, so narrowing
    /// <see cref="ScopeOptions.IncludeInternal"/> cannot erase an ambiguity the resolver needed to
    /// see; an edge resolved to an out-of-scope target is degraded to
    /// <see cref="EdgeConfidence.Unresolved"/> afterwards instead.
    /// </para>
    ///
    /// <para>
    /// <b>"Unfiltered" means unfiltered by the visibility check, and by nothing else.</b>
    /// <see cref="FileEligibility.IsEligible"/> runs FIRST, in the loop below, and rejects whole
    /// files before they are opened -- so on the <see cref="ScopeOptions.IncludeTests"/> axis the
    /// resolver does not see past the filter: a declaration in a test project is absent from the set
    /// entirely, not merely absent from the output. So are the declarations of every file skipped
    /// for size, depth, encoding or unreadability, and every one an extractor dropped for want of a
    /// container. Narrowing scope on that axis therefore CAN turn an unresolved call into a resolved
    /// one, by removing the competitor that made it ambiguous. See
    /// <see cref="ISymbolResolver.Resolve"/>, whose contract this qualifies.
    /// </para>
    /// </summary>
    public CodeGraph Build(RepositorySnapshot snapshot, ExtractionLimits limits, ScopeOptions scope, CancellationToken cancellationToken = default)
    {
        using var timeoutSource = new CancellationTokenSource(limits.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken);

        var results = new List<(string RelativePath, ExtractionResult Result)>();
        var incomplete = false;

        // The file list is materialised in its own try, separate from the per-file loop below: a
        // failure here (a missing or unreadable repository root, or a circular reparse point -- see
        // both catch clauses) means the walk itself could not produce a list, so nothing was ever
        // attempted and the run is unconditionally incomplete. An IOException raised later, while
        // extracting one specific file, must NOT be folded into this same catch: that would abort
        // every remaining file over one file's failure and destroy the diagnosis -- RunStatus would
        // stay honestly incomplete, but Skipped would come back empty with no indication which file
        // was responsible. Every extractor already reports that kind of per-file failure as a
        // FileStatus on its ExtractionResult instead of throwing (see ILanguageExtractor.Extract's
        // own contract), so nothing inside the loop below is expected to throw at all in normal
        // operation; if something still does, it is a genuine, unexpected bug, and is deliberately
        // left to propagate rather than be silently absorbed here.
        List<string> orderedRelativePaths;
        try
        {
            orderedRelativePaths = EnumerateFiles(snapshot.RepoPath).ToList();
        }
        catch (IOException)
        {
            // Covers, among other IOException subtypes: a missing repository root
            // (DirectoryNotFoundException) -- reachable from the public API on nothing more than a
            // typo'd or transiently-unmounted RepositorySnapshot.RepoPath, since neither
            // RepositoryScanner.Scan nor this method checked existence before this fix -- and a
            // circular reparse point (measured, not assumed: a junction pointing back at one of its
            // own ancestors is not detected as a cycle by Directory.EnumerateFiles below; it keeps
            // recursing into it, extending the accumulated path a level deeper on every re-entry, and
            // throws PathTooLongException within a fraction of a second, nowhere near
            // ExtractionLimits.Timeout). Both cases mean the walk produced no file list at all, so
            // treating that the same as "zero files, nothing skipped" and returning RunStatus.Complete
            // would silently look like an empty-but-valid repository -- exactly the ambiguity that
            // would make Task 11's pruning gate delete every concept in the user's bundle on what was
            // really a broken run, not an empty one.
            orderedRelativePaths = [];
            incomplete = true;
        }
        catch (UnauthorizedAccessException)
        {
            orderedRelativePaths = [];
            incomplete = true;
        }

        foreach (var relativePath in orderedRelativePaths)
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

            if (linkedSource.IsCancellationRequested)
            {
                // §2.3: a run that is cancelled -- by the timeout, or by a cancellationToken the
                // caller passed in -- before attempting every file is partial, not complete. The
                // files never attempted are silently absent rather than reported with a per-file
                // FileStatus, but `incomplete` alone is enough to keep IsComplete false, which is
                // the property Task 11's pruning actually keys off. Whatever was already added to
                // `results` for files attempted before the cancellation is still returned --
                // partial results, honestly labelled incomplete, not discarded.
                incomplete = true;
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

        // Two lists, deliberately. `declared` is every symbol this run extracted, whatever its
        // visibility; `symbols` is the scope-filtered subset that becomes CodeGraph.Symbols.
        // Resolvers are handed `declared`, never `symbols` -- see the Resolve call below for why
        // that is load-bearing rather than incidental. Filtering `declared` (already sorted) rather
        // than sorting a separately-filtered list keeps the two in the same order by construction.
        var declared = SortSymbols(results.SelectMany(r => r.Result.Symbols));
        var symbols = declared.Where(s => FileEligibility.IsInScope(s, scope)).ToList();

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

            // `declared`, NOT `symbols`: a resolver must decide ambiguity over what the source
            // DECLARES, not over what this run chose to publish. Handing it the scope-filtered list
            // instead erases ambiguity before it can be seen -- a name declared once publicly and
            // once internally arrives as a single declaration under the default scope, which
            // NameMatchResolver reads as unambiguous and links, confidently, to the public one even
            // when the call was to the internal one. The same repository run with
            // --include-internal correctly leaves that call Unresolved, so the narrower run would
            // be asserting something the wider run knows to be false. Resolving against the full
            // declared set gives the invariant `symbols`-first cannot: narrowing visibility scope
            // only ever LOSES edges, never invents one. Targets that are themselves out of scope
            // are handled after the fact, by the degrade-to-Unresolved pass below.
            foreach (var edge in resolver.Resolve(ownedSites, declared))
            {
                verdicts[(edge.Site.RelativePath, edge.Site.Offset)] = edge;
            }
        }

        // Never sort by iterating the dictionary directly: materialise its values into a list first,
        // then sort that list explicitly. Two call sites to the same method from the same caller tie
        // on (CallerContainer, CallerName, CalledName) -- RelativePath then Offset (unique per call
        // site within a file) fully disambiguate, the same reasoning as the symbol sort above.
        var verdictList = verdicts.Values.ToList();
        var sortedEdges = verdictList
            .OrderBy(e => e.Site.CallerContainer, StringComparer.Ordinal)
            .ThenBy(e => e.Site.CallerName, StringComparer.Ordinal)
            .ThenBy(e => e.Site.CalledName, StringComparer.Ordinal)
            .ThenBy(e => e.Site.RelativePath, StringComparer.Ordinal)
            .ThenBy(e => e.Site.Offset)
            .ToList();

        // CodeGraph's own invariant (see its XML doc): no edge may reference a symbol -- as caller or
        // as resolved target -- absent from `symbols`. `symbols` above is already IsInScope-filtered,
        // but `sites`/`edges` are not (a call SITE is not a symbol, so scope never applies to it
        // directly), so an edge whose caller was filtered out of scope (e.g. a private or excluded
        // internal method) would otherwise survive into the output with no SymbolFact for Task 8 to
        // hang a `## Calls` entry on. Preserving input order here (Where/Select over an already-sorted
        // list) keeps the deterministic edge order established above.
        var symbolKeys = new HashSet<(string Container, string Name)>(symbols.Select(s => (s.Container, s.Name)));
        var edges = sortedEdges
            .Where(e => symbolKeys.Contains((e.Site.CallerContainer, e.Site.CallerName)))
            .Select(e => e.Confidence != EdgeConfidence.Unresolved
                    && e.TargetContainer is not null && e.TargetName is not null
                    && !symbolKeys.Contains((e.TargetContainer, e.TargetName))
                // A resolved target absent from Symbols (filtered by scope, or never a real symbol at
                // all) would otherwise point at a concept that will not exist; degrading to Unresolved
                // renders it as plain text instead (§4.5 already prescribes that fallback).
                ? e with { TargetContainer = null, TargetName = null, Confidence = EdgeConfidence.Unresolved }
                : e)
            .ToList();

        // Every attempted file's outcome, INCLUDING FileStatus.Extracted -- not just the failures.
        // §6.3's finer rule (a completed traversal may still prune, scoped to the files that
        // extracted cleanly) needs a consumer to be able to ask "which files extracted cleanly?" and
        // get an exact answer straight out of RunStatus, without also needing the full universe of
        // eligible files to compute the complement of a failures-only list.
        var skipped = results
            .Select(r => (r.RelativePath, r.Result.Status))
            .ToList();

        // `incomplete` here means the TRAVERSAL was cut short -- by timeout, explicit cancellation, or
        // the walk itself failing (missing root, enumeration failure) -- before every eligible file
        // was even visited. That is a different, strictly worse risk than an individual file's own
        // extraction quality: a symbol may have moved to a file this run never reached at all, so
        // RunStatus.TraversalComplete carries it separately from RunStatus.IsComplete (see both types'
        // own doc comments for the full reasoning §6.3 and this task's own measurement forced).
        var status = new RunStatus(!incomplete, skipped);

        return new CodeGraph(symbols, edges, status);
    }

    /// <summary>
    /// The one symbol ordering this class uses, applied to the full declared set so its scope-filtered
    /// subset inherits the same order by construction. Sort keys must fully disambiguate every tie a
    /// real repository can produce: two overloads of the same member in the same file tie on
    /// (<see cref="SymbolFact.Container"/>, <see cref="SymbolFact.Name"/>,
    /// <see cref="SymbolFact.RelativePath"/>), so <see cref="SymbolFact.StartOffset"/> breaks that
    /// tie. Without a final key, LINQ's stable <c>OrderBy</c> would fall back to input order, which
    /// is not a documented contract -- and Tasks 10/12 assert the generated bundle byte-for-byte, so
    /// an unspecified tie order would surface there as an intermittent failure.
    /// </summary>
    private static List<SymbolFact> SortSymbols(IEnumerable<SymbolFact> facts) =>
        facts
            .OrderBy(s => s.Container, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.RelativePath, StringComparer.Ordinal)
            .ThenBy(s => s.StartOffset)
            .ToList();

    /// <summary>
    /// §2.3's pathological-nesting-depth guard: counts <paramref name="relativePath"/>'s directory
    /// segments (its <c>/</c> separators) against <paramref name="maxDepth"/> before any file handle
    /// is opened -- purely a path check, so it belongs in the walk rather than in
    /// <see cref="ILanguageExtractor.Extract"/>, which never sees paths this shallow-checked walk has
    /// already ruled pathological.
    /// </summary>
    private static bool ExceedsMaxDepth(string relativePath, int maxDepth) =>
        relativePath.Count(c => c == '/') > maxDepth;

    /// <summary>
    /// Deliberately does not pre-check <see cref="Directory.Exists"/>: letting
    /// <see cref="Directory.EnumerateFiles(string, string, SearchOption)"/> throw
    /// <see cref="DirectoryNotFoundException"/> (an <see cref="IOException"/> subtype) naturally for a
    /// missing <paramref name="repoPath"/> is what lets <see cref="Build"/>'s enumeration-scoped
    /// <c>catch</c> turn that into an incomplete run instead of this method silently returning an
    /// empty sequence that would read as "zero files, nothing wrong" (C-1).
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string repoPath) =>
        Directory.EnumerateFiles(repoPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repoPath, path).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(path => path, StringComparer.Ordinal);

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
