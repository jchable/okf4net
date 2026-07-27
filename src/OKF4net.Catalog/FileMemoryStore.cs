// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Catalog;

/// <summary>
/// Filesystem <see cref="IMemoryStore"/>. Path derivation is isolated in
/// <see cref="MemoryPath.For"/>; writes reuse the core
/// <see cref="OKF4net.BundleConceptWriter"/> (producer validation + per-path
/// lock + reparse guards) over the tier's memory source root. The user tier is
/// implemented; a tier absent from the configured roots is treated as "no
/// source configured" (errors-as-data), so session/tenant remain staged.
/// </summary>
public sealed class FileMemoryStore : IMemoryStore
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // Most-specific first (spec §6.1).
    private static readonly MemoryTier[] ReadOrder = [MemoryTier.Session, MemoryTier.User, MemoryTier.Tenant];

    private readonly IReadOnlyDictionary<MemoryTier, string> _tierRoots;

    /// <summary>Creates a store over the given per-tier, resolved absolute source roots.</summary>
    public FileMemoryStore(IReadOnlyDictionary<MemoryTier, string> tierRoots)
    {
        ArgumentNullException.ThrowIfNull(tierRoots);
        _tierRoots = tierRoots;
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
            if (!IsApplicable(tier, scope) || !_tierRoots.TryGetValue(tier, out var root))
            {
                continue;
            }

            var subDir = ScopedDir(root, tier, scope);
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            if (IsReparseEscaped(root, subDir))
            {
                diagnostics.Add(new KnowledgeDiagnostic(KnowledgeDiagnosticCode.SourceUnavailable, $"memory:{tier}", $"Memory tier '{tier}' path is a reparse point; refusing to read."));
                continue;
            }

            Bundle bundle;
            try
            {
                bundle = Bundle.Load(subDir);
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

            // Invariant across every hit in this tier -- computed once rather
            // than per hit (MemoryPath.For hashes a segment per call).
            var conceptIdPrefix = MemoryPath.For(tier, scope);

            foreach (var hit in ConceptSearch.Search(bundle.Concepts, query.Text, query.Tag))
            {
                passages.Add(new KnowledgePassage(
                    SourceId: $"memory:{tier}",
                    ConceptId: $"{conceptIdPrefix}/{hit.Concept.Id}",
                    Title: hit.Concept.Document.Frontmatter.Title,
                    Excerpt: ConceptSearch.Excerpt(hit.Concept.Document.Body, query.Text) ?? string.Empty,
                    Score: hit.Score,
                    BundleRelativePath: Path.GetRelativePath(bundle.Root, hit.Concept.Path).Replace(Path.DirectorySeparatorChar, '/')));
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

        if (!_tierRoots.TryGetValue(tier, out var root))
        {
            return new ValueTask<MemoryWriteResult>(new MemoryWriteResult(false, $"No memory source configured for tier '{tier}'."));
        }

        var conceptId = $"{MemoryPath.For(tier, scope)}/{entry.ConceptName}";
        var writer = new BundleConceptWriter(root);
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
        var deleted = 0;
        var errors = new List<string>();

        foreach (var currentTier in tiers)
        {
            ct.ThrowIfCancellationRequested();
            if (!_tierRoots.TryGetValue(currentTier, out var root))
            {
                continue;
            }

            var subDir = ScopedDir(root, currentTier, scope);
            if (!Directory.Exists(subDir))
            {
                continue;
            }

            if (IsReparseEscaped(root, subDir))
            {
                errors.Add($"Memory tier '{currentTier}' path is a reparse point; refusing to delete.");
                continue;
            }

            try
            {
                Directory.Delete(subDir, recursive: true);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Memory tier '{currentTier}' subtree could not be deleted: {e.Message}");
            }
        }

        var error = errors.Count > 0 ? string.Join("; ", errors) : null;
        return new ValueTask<MemoryDeleteResult>(new MemoryDeleteResult(deleted, error));
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<MemoryConcept>> EnumerateAsync(KnowledgeAccessScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var concepts = new List<MemoryConcept>();

        foreach (var tier in ReadOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsApplicable(tier, scope) || !_tierRoots.TryGetValue(tier, out var root))
            {
                continue;
            }

            var subDir = ScopedDir(root, tier, scope);
            if (!Directory.Exists(subDir) || IsReparseEscaped(root, subDir))
            {
                continue;
            }

            Bundle bundle;
            try
            {
                bundle = Bundle.Load(subDir);
            }
            catch (OkfException)
            {
                continue;
            }

            var prefix = MemoryPath.For(tier, scope);
            foreach (var concept in bundle.Concepts)
            {
                concepts.Add(new MemoryConcept(tier, $"{prefix}/{concept.Id}", concept.Document.Frontmatter.Title));
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

    private static string ScopedDir(string root, MemoryTier tier, KnowledgeAccessScope scope)
    {
        var relative = MemoryPath.For(tier, scope).Split('/');
        return Path.Combine([root, .. relative]);
    }

    private static bool IsReparseEscaped(string root, string subDir)
    {
        // TrimEndingDirectorySeparator matters here: a host-configured tier
        // root with a trailing separator would otherwise never string-equal
        // the ancestor HasReparsePointAncestor's walk produces via
        // Path.GetDirectoryName (which never carries one), overshooting the
        // walk past the intended root into the real filesystem above it.
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(subDir);
        return ReparsePoints.IsReparsePoint(full) || ReparsePoints.HasReparsePointAncestor(fullRoot, full, PathComparison);
    }
}
