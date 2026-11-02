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
/// (<see cref="MemoryTierDeletion.Report"/>), so a failed deletion can be run again. The
/// directories are left in place (spec §8): empty, they hold no key, so the scope no longer
/// exists for the store.</para>
/// </remarks>
public sealed class FileMemoryStore : IMemoryStore
{
    // Most-specific first (spec §6.1).
    private static readonly MemoryTier[] ReadOrder = [MemoryTier.Session, MemoryTier.User, MemoryTier.Tenant];

    /// <summary>Any key: <see cref="IBundleProvider.Exists"/> on it runs the scope provider's boundary check before anything is listed.</summary>
    private static readonly BundleKey ProbeKey = BundleKey.Parse("index.md");

    private readonly IReadOnlyDictionary<MemoryTier, string> _tierRoots;
    private readonly Dictionary<MemoryTier, FileSystemBundleProvider> _tierProviders;

    /// <summary>Creates a store over the given per-tier, resolved absolute source roots.</summary>
    public FileMemoryStore(IReadOnlyDictionary<MemoryTier, string> tierRoots)
    {
        ArgumentNullException.ThrowIfNull(tierRoots);
        _tierRoots = tierRoots;
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
    }

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
            var scopeProvider = ScopeProvider(tier, prefix);
            switch (Probe(tierProvider, scopeProvider, prefix))
            {
                case ScopeState.Absent:
                    continue;
                case ScopeState.Refused:
                    diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' path is a reparse point or could not be inspected; refusing to read."));
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
            switch (Probe(tierProvider, ScopeProvider(currentTier, prefix), prefix))
            {
                case ScopeState.Absent:
                    continue;
                case ScopeState.Refused:
                    var refusal = $"Memory tier '{currentTier}' path is a reparse point or could not be inspected; refusing to delete.";
                    errors.Add(refusal);
                    deletions.Add(new MemoryTierDeletion(currentTier, new BundleWriteReport([], [], [new BundleWriteFailure(prefix, refusal)])));
                    continue;
            }

            var deleted = new List<BundleKey>();
            var failed = new List<BundleWriteFailure>();
            foreach (var listing in tierProvider.Enumerate(prefix, BundleLimits.Default).ToList())
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
                catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException)
                {
                    failed.Add(new BundleWriteFailure(key, e.Message));
                }
            }

            if (failed.Count > 0)
            {
                errors.Add($"Memory tier '{currentTier}' subtree could not be deleted: {string.Join("; ", failed.Select(f => f.Reason))}");
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
            var scopeProvider = ScopeProvider(tier, prefix);
            if (Probe(tierProvider, scopeProvider, prefix) != ScopeState.Present)
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
    /// A provider rooted at the scope's directory (the tier root plus the prefix's segments,
    /// the same string <c>Path.Combine</c> gave before the seam) and confined to the tier
    /// root: the chain from that root to the scope is re-checked STRICTly before every
    /// access (spec §8), so the scope's concepts load with the ids and links a bundle rooted
    /// there has, as before.
    /// </summary>
    private FileSystemBundleProvider ScopeProvider(MemoryTier tier, BundleKey prefix) =>
        new(Path.Combine([_tierRoots[tier], .. prefix.Segments]), confinementBoundary: _tierRoots[tier]);

    /// <summary>
    /// The scope's state: the chain from the tier root to the scope is checked first
    /// (<see cref="IBundleProvider.Exists"/> on the scope provider runs its boundary check
    /// before anything is listed, and answers for an absent root without throwing), then the
    /// tier provider lists the keys under the scope's prefix.
    /// </summary>
    private static ScopeState Probe(FileSystemBundleProvider tierProvider, FileSystemBundleProvider scopeProvider, BundleKey prefix)
    {
        try
        {
            scopeProvider.Exists(ProbeKey);
            return tierProvider.Enumerate(prefix, BundleLimits.Default).Any() ? ScopeState.Present : ScopeState.Absent;
        }
        catch (BundleAccessRefusedException)
        {
            return ScopeState.Refused;
        }
    }
}
