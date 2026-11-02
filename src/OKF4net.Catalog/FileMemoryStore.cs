// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;
using OKF4net.Providers;

namespace OKF4net.Catalog;

/// <summary>
/// Filesystem <see cref="IMemoryStore"/>, through the provider seam (spec §9). Path
/// derivation is isolated in <see cref="MemoryPath.For"/>; every read, write, deletion and
/// enumeration goes through a <see cref="FileSystemBundleProvider"/>: one per tier root,
/// and, for a scope, one rooted at the scope's directory and confined to the tier root, so
/// the chain from the tier root to the scope is re-checked STRICTly before every access (a
/// reparse point or an entry that cannot be inspected on it refuses the scope: a guard
/// fails closed). Writes reuse the core <see cref="OKF4net.BundleConceptWriter"/>
/// (producer validation + per-path lock + the provider's guards) over the tier's provider.
/// Read/write/delete/enumerate are all generic over <see cref="MemoryTier"/> -- a tier
/// absent from the configured roots is simply treated as "no source configured"
/// (errors-as-data), not a special case per tier.
/// </summary>
/// <remarks>
/// <para><b>A scope exists while it holds a key</b> (spec §9, decision 14): a scope whose
/// directory is empty, or absent, is read and enumerated as nothing and deleted as a no-op.
/// A scope is loaded at every read, as before (no <see cref="BundleHandle"/>; spec §5.3).</para>
/// <para><b>Deleting a scope</b> enumerates the keys under its prefix and deletes each object
/// on its own (<see cref="IBundleProvider.Delete"/>, unconditional); there is no multi-object
/// transaction, and the result says per tier what was deleted and what failed
/// (<see cref="MemoryTierDeletion.Report"/>), so a failed deletion can be run again. When every
/// object was deleted, the scope's now-empty directories are removed too, bottom-up and without
/// following a link (see <see cref="RemoveScopeDirectories"/>): the directory names carry the
/// lowercased tenant, user and session ids, so erasing a scope leaves nothing named after it.
/// A directory that cannot be removed (it holds a link or a file the provider did not list) is
/// a failure under the scope's prefix, so the tier is not reported as succeeded.</para>
/// <para>A scope's listing that fails for any other reason than a refusal (a subdirectory that
/// cannot be listed, a bound of <see cref="BundleLimits"/> exceeded) is an unavailable scope,
/// reported as data: a diagnostic on a read, nothing on an enumeration, a failure on a deletion,
/// each naming the failure by its type, never by a runtime message that could name a host path.</para>
/// </remarks>
public sealed class FileMemoryStore : IMemoryStore
{
    // Most-specific first (spec §6.1).
    private static readonly MemoryTier[] ReadOrder = [MemoryTier.Session, MemoryTier.User, MemoryTier.Tenant];

    /// <summary>Any key: <see cref="IBundleProvider.Exists"/> on it runs the scope provider's boundary check before anything is listed.</summary>
    private static readonly BundleKey ProbeKey = BundleKey.Parse("index.md");

    private readonly Dictionary<MemoryTier, FileSystemBundleProvider> _tierProviders;

    /// <summary>Creates a store over the given per-tier, resolved absolute source roots.</summary>
    public FileMemoryStore(IReadOnlyDictionary<MemoryTier, string> tierRoots)
    {
        ArgumentNullException.ThrowIfNull(tierRoots);
        _tierProviders = tierRoots.ToDictionary(pair => pair.Key, pair => new FileSystemBundleProvider(pair.Value));
    }

    private enum ScopeState
    {
        /// <summary>No key under the scope's prefix.</summary>
        Absent,

        /// <summary>At least one key under the scope's prefix.</summary>
        Present,

        /// <summary>The provider refuses the chain from the tier root to the scope (a reparse point, or an entry that cannot be inspected).</summary>
        Refused,

        /// <summary>The scope could not be listed (a subdirectory denies listing, an I/O failure, a bound of the limits exceeded).</summary>
        Unavailable,
    }

    /// <summary>What <see cref="Probe"/> found: the state, the failure's type name when it is <see cref="ScopeState.Unavailable"/>, and the keys listed under the scope's prefix.</summary>
    private readonly record struct ScopeProbe(ScopeState State, string? Failure, IReadOnlyList<BundleListing> Listings);

    /// <inheritdoc/>
    public ValueTask<MemoryReadResult> ReadAsync(KnowledgeAccessScope scope, KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        var passages = new List<KnowledgePassage>();
        var diagnostics = new List<KnowledgeDiagnostic>();

        foreach (var tier in ReadOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsApplicable(tier, scope) || !_tierProviders.TryGetValue(tier, out var tierProvider))
            {
                continue;
            }

            // Computed once per tier -- MemoryPath.For hashes a segment per
            // call -- and reused both to open the scope below and as every
            // hit's ConceptId prefix further down.
            var conceptIdPrefix = MemoryPath.For(tier, scope);
            var prefix = BundleKey.Parse(conceptIdPrefix);
            var scopeProvider = ScopeProvider(tierProvider, prefix);
            var probe = Probe(tierProvider, scopeProvider, prefix);
            switch (probe.State)
            {
                case ScopeState.Absent:
                    continue;
                case ScopeState.Refused:
                    diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' path is a reparse point or could not be inspected; refusing to read."));
                    continue;
                case ScopeState.Unavailable:
                    diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' could not be loaded: {probe.Failure}"));
                    continue;
            }

            Bundle bundle;
            try
            {
                bundle = Bundle.Load(scopeProvider);
            }
            catch (OkfException e)
            {
                diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' could not be loaded: {e.Message}"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(query.Text))
            {
                continue;
            }

            foreach (var hit in ConceptSearch.Search(bundle.Concepts, query.Text, query.Tag))
            {
                var fm = hit.Concept.Document.Frontmatter;
                passages.Add(new KnowledgePassage(
                    SourceId: $"memory:{tier}",
                    ConceptId: $"{conceptIdPrefix}/{hit.Concept.Id}",
                    Title: fm.Title,
                    Excerpt: ConceptSearch.Excerpt(hit.Concept.Document.Body, query.Text) ?? string.Empty,
                    Score: hit.Score,
                    BundleRelativePath: OkfBundleKnowledgeSource.RelativePathOf(bundle, hit.Concept),
                    Lifecycle: fm.Lifecycle));
            }
        }

        return new ValueTask<MemoryReadResult>(new MemoryReadResult(passages.AsReadOnly(), diagnostics.AsReadOnly()));
    }

    /// <inheritdoc/>
    public ValueTask<MemoryWriteResult> WriteAsync(KnowledgeAccessScope scope, MemoryEntry entry, MemoryTier tier, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();

        if (!_tierProviders.TryGetValue(tier, out var tierProvider))
        {
            return new ValueTask<MemoryWriteResult>(new MemoryWriteResult(false, $"No memory source configured for tier '{tier}'."));
        }

        var conceptId = $"{MemoryPath.For(tier, scope)}/{entry.ConceptName}";
        var writer = new BundleConceptWriter(tierProvider);
        var section = entry.SectionMarkdown;
        var result = writer.AppendToConceptAtomic(
            conceptId,
            entry.FrontmatterYamlIfCreating,
            current => current is null ? section : current.TrimEnd('\n') + "\n\n" + section);

        return result.StartsWith("Error:", StringComparison.Ordinal)
            ? new ValueTask<MemoryWriteResult>(new MemoryWriteResult(false, result))
            : new ValueTask<MemoryWriteResult>(new MemoryWriteResult(true, null));
    }

    /// <inheritdoc/>
    public ValueTask<MemoryDeleteResult> DeleteScopeAsync(KnowledgeAccessScope scope, MemoryTier? tier = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var tiers = tier is { } t ? [t] : ReadOrder.Where(x => IsApplicable(x, scope));
        var deletions = new List<MemoryTierDeletion>();
        var errors = new List<string>();

        foreach (var currentTier in tiers)
        {
            ct.ThrowIfCancellationRequested();
            if (!_tierProviders.TryGetValue(currentTier, out var tierProvider))
            {
                continue;
            }

            var prefix = BundleKey.Parse(MemoryPath.For(currentTier, scope));
            var probe = Probe(tierProvider, ScopeProvider(tierProvider, prefix), prefix);
            switch (probe.State)
            {
                case ScopeState.Absent:
                    // No key, but directories may be left (a previous deletion that could not remove them).
                    if (RemoveScopeDirectories(tierProvider, prefix) is { } leftover)
                    {
                        errors.Add($"Memory tier '{currentTier}' subtree could not be deleted: {leftover}");
                        deletions.Add(new MemoryTierDeletion(currentTier, new BundleWriteReport([], [], [new BundleWriteFailure(prefix, leftover)])));
                    }

                    continue;
                case ScopeState.Refused:
                    var refusal = $"Memory tier '{currentTier}' path is a reparse point or could not be inspected; refusing to delete.";
                    errors.Add(refusal);
                    deletions.Add(new MemoryTierDeletion(currentTier, new BundleWriteReport([], [], [new BundleWriteFailure(prefix, refusal)])));
                    continue;
                case ScopeState.Unavailable:
                    var unavailable = $"the scope could not be listed ({probe.Failure}); nothing was deleted";
                    errors.Add($"Memory tier '{currentTier}' subtree could not be deleted: {unavailable}");
                    deletions.Add(new MemoryTierDeletion(currentTier, new BundleWriteReport([], [], [new BundleWriteFailure(prefix, unavailable)])));
                    continue;
            }

            var deleted = new List<BundleKey>();
            var failed = new List<BundleWriteFailure>();
            foreach (var listing in probe.Listings)
            {
                ct.ThrowIfCancellationRequested();
                if (!BundleKey.TryParse(listing.RawName, out var key, out var why))
                {
                    failed.Add(new BundleWriteFailure(prefix, $"{DebugQuote.Quote(listing.RawName)} is not a key ({why}), so it cannot be deleted through the provider."));
                    continue;
                }

                try
                {
                    tierProvider.Delete(key, BundleWriteCondition.None);
                    deleted.Add(key);
                }
                catch (BundleProviderException e)
                {
                    failed.Add(new BundleWriteFailure(key, e.Message));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The type only, with the key: the runtime's message can name a host path (#211).
                    failed.Add(new BundleWriteFailure(key, $"'{key}' could not be deleted ({e.GetType().Name})"));
                }
            }

            if (failed.Count == 0 && RemoveScopeDirectories(tierProvider, prefix) is { } kept)
            {
                failed.Add(new BundleWriteFailure(prefix, kept));
            }

            if (failed.Count > 0)
            {
                errors.Add($"Memory tier '{currentTier}' subtree could not be deleted: {string.Join(", ", failed.Select(f => f.Reason))}");
            }

            deletions.Add(new MemoryTierDeletion(currentTier, new BundleWriteReport([], deleted.AsReadOnly(), failed.AsReadOnly())));
        }

        var error = errors.Count > 0 ? string.Join("; ", errors) : null;
        return new ValueTask<MemoryDeleteResult>(new MemoryDeleteResult(deletions.AsReadOnly(), error));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<MemoryConcept>> EnumerateAsync(KnowledgeAccessScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var concepts = new List<MemoryConcept>();

        foreach (var tier in ReadOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsApplicable(tier, scope) || !_tierProviders.TryGetValue(tier, out var tierProvider))
            {
                continue;
            }

            var conceptIdPrefix = MemoryPath.For(tier, scope);
            var prefix = BundleKey.Parse(conceptIdPrefix);
            var scopeProvider = ScopeProvider(tierProvider, prefix);
            if (Probe(tierProvider, scopeProvider, prefix).State != ScopeState.Present)
            {
                continue;
            }

            Bundle bundle;
            try
            {
                bundle = Bundle.Load(scopeProvider);
            }
            catch (OkfException)
            {
                continue;
            }

            foreach (var concept in bundle.Concepts)
            {
                concepts.Add(new MemoryConcept(tier, $"{conceptIdPrefix}/{concept.Id}", concept.Document.Frontmatter.Title));
            }
        }

        return new ValueTask<IReadOnlyList<MemoryConcept>>(concepts.AsReadOnly());
    }

    private static bool IsApplicable(MemoryTier tier, KnowledgeAccessScope scope) => scope.IsLocal || tier switch
    {
        MemoryTier.Session => scope.SessionId is not null,
        MemoryTier.User => scope.UserId is not null,
        MemoryTier.Tenant => scope.TenantId is not null,
        _ => false,
    };

    /// <summary>
    /// A provider rooted at the scope's directory (the tier provider's root plus the prefix's
    /// segments, the same string <c>Path.Combine</c> gave before the seam) and confined to the
    /// tier provider's root: the chain from that root to the scope is re-checked STRICTly before
    /// every access (spec §8), so the scope's concepts load with the ids and links a bundle
    /// rooted there has, as before. Derived from the tier provider, never from the dictionary
    /// the host passed, so a host that mutates its dictionary cannot make two operations disagree.
    /// </summary>
    private static FileSystemBundleProvider ScopeProvider(FileSystemBundleProvider tierProvider, BundleKey prefix) =>
        new(Path.Combine([tierProvider.Root, .. prefix.Segments]), confinementBoundary: tierProvider.Root);

    /// <summary>
    /// The scope's state and keys: the chain from the tier root to the scope is checked first
    /// (<see cref="IBundleProvider.Exists"/> on the scope provider runs its boundary check
    /// before anything is listed, and answers for an absent root without throwing), then the
    /// tier provider lists the keys under the scope's prefix. The listing walks the whole
    /// subtree, so it can fail for reasons other than a refusal (a subdirectory that denies
    /// listing, an I/O failure, a bound of the limits): that is <see cref="ScopeState.Unavailable"/>,
    /// reported by the exception's type only. Nothing here throws for a storage condition.
    /// </summary>
    private static ScopeProbe Probe(FileSystemBundleProvider tierProvider, FileSystemBundleProvider scopeProvider, BundleKey prefix)
    {
        try
        {
            scopeProvider.Exists(ProbeKey);
            var listings = tierProvider.Enumerate(prefix, BundleLimits.Default).ToList();
            return new ScopeProbe(listings.Count > 0 ? ScopeState.Present : ScopeState.Absent, null, listings);
        }
        catch (BundleAccessRefusedException)
        {
            return new ScopeProbe(ScopeState.Refused, null, []);
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException)
        {
            return new ScopeProbe(ScopeState.Unavailable, e.GetType().Name, []);
        }
    }

    /// <summary>
    /// Removes the scope's directories once its objects are gone: bottom-up, only directories
    /// at or under the scope's own directory (never the tier root nor anything above the
    /// scope), each with a non-recursive <see cref="Directory.Delete(string, bool)"/>, and
    /// never following a link: the STRICT guard (a reparse point, or an entry whose link
    /// status cannot be read, is refused like a link) runs on the chain from the tier root to
    /// the scope and on every directory before it is entered or removed. Returns <see langword="null"/>
    /// when nothing is left (or there was nothing), otherwise a sentence with no host path:
    /// a link or an uninspectable entry that was left in place, or a directory that is not
    /// empty or could not be removed, named by its exception's type.
    /// </summary>
    /// <remarks>
    /// This is the ONE direct filesystem deletion left in <c>OKF4net.Catalog</c>: the provider
    /// seam has no directory deletion (design §8, objects only), and the directory names carry
    /// the lowercased tenant, user and session ids, so the erasure obligation behind
    /// <see cref="DeleteScopeAsync"/> keeps it (follow-up #152). Do not add another.
    /// </remarks>
    private static string? RemoveScopeDirectories(FileSystemBundleProvider tierProvider, BundleKey prefix)
    {
        var tierRoot = ReparsePoints.CanonicalizeRoot(tierProvider.Root);
        var scopeDir = Path.GetFullPath(Path.Combine([tierRoot, .. prefix.Segments]));
        if (!Directory.Exists(scopeDir))
        {
            return null;
        }

        if (ReparsePoints.HasReparsePointOrUninspectableAncestor(tierRoot, scopeDir, StringComparison.Ordinal))
        {
            return "the scope's directory is a link, or on a path with one, or could not be inspected; its directories were left in place";
        }

        return RemoveEmptyDirectories(scopeDir);
    }

    private static string? RemoveEmptyDirectories(string directory)
    {
        string? failure = null;
        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory).ToList())
            {
                if (ReparsePoints.IsReparsePointOrUninspectable(child))
                {
                    failure ??= "a link, or an entry that could not be inspected, is under the scope; its directories were left in place";
                    continue;
                }

                var childFailure = RemoveEmptyDirectories(child);
                failure ??= childFailure;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"a directory under the scope could not be listed ({e.GetType().Name}); its directories were left in place";
        }

        if (failure is not null)
        {
            return failure; // a child stays, so this directory cannot be removed either
        }

        try
        {
            Directory.Delete(directory, recursive: false);
        }
        catch (DirectoryNotFoundException)
        {
            // gone already
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"a directory under the scope is not empty or could not be removed ({e.GetType().Name}); its directories were left in place";
        }

        return null;
    }
}
