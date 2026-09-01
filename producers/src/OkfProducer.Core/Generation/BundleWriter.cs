// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <inheritdoc cref="IBundleWriter"/>
public sealed class BundleWriter : IBundleWriter
{
    /// <summary>
    /// Name prefix of the staging directory, created beside the bundle (never inside it) and deleted
    /// again in a <c>finally</c>. Beside, because a directory inside <c>outPath</c> would be part of
    /// the bundle for the moment it existed -- it would make a <see cref="WritePolicy.RequireEmpty"/>
    /// target non-empty, and a crash would leave a half-written bundle carrying a directory full of
    /// concepts <c>Bundle.Load</c> would happily pick up.
    /// </summary>
    private const string StagingPrefix = ".okfgen-staging-";

    /// <summary>The file name <c>IndexGenerator</c> owns in every directory; never a pruning candidate and never reported as unowned.</summary>
    private const string IndexFile = "index.md";

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc/>
    public WriteResult Write(
        string outPath,
        IReadOnlyList<GeneratedConcept> concepts,
        WritePolicy policy,
        string repoPath,
        GenerationManifest? manifest = null,
        RunStatus? status = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(outPath);
        ArgumentNullException.ThrowIfNull(concepts);
        ArgumentException.ThrowIfNullOrEmpty(repoPath);

        if (policy == WritePolicy.Reset && Directory.Exists(outPath))
        {
            var fullOut = Path.GetFullPath(outPath);
            var fullRepo = Path.GetFullPath(repoPath);
            if (IsSameOrAncestor(fullOut, fullRepo))
            {
                throw new InvalidOperationException(
                    $"Refusing to reset '{outPath}': it is the same as, or an ancestor of, the repository being scanned ('{repoPath}'). Choose a different --out.");
            }

            Directory.Delete(outPath, recursive: true);
        }

        if (policy == WritePolicy.RequireEmpty && Directory.Exists(outPath) && Directory.EnumerateFileSystemEntries(outPath).Any())
        {
            throw new InvalidOperationException(
                $"Output directory '{outPath}' is not empty. Use --update or --reset.");
        }

        Directory.CreateDirectory(outPath);

        // Read before anything is written, so the "previous" run really is the previous one. Only
        // Update can have one: Reset has just deleted the directory it lived in, and RequireEmpty
        // refuses to run at all unless the directory is empty.
        var previous = policy == WritePolicy.Update ? GenerationManifest.TryRead(outPath) : null;

        var failures = new List<(ConceptId Id, string Error)>();
        var written = 0;

        // §6.3 rule 1. Everything is produced into a directory the bundle cannot see; the bundle is
        // only touched by CommitStaging below, after the whole set exists. A throw anywhere in the
        // loop -- including one raised by the caller's own `concepts` sequence -- therefore leaves the
        // bundle byte-for-byte as it was, and the finally still cleans up.
        var staging = CreateStagingDirectory(outPath);
        try
        {
            var writer = new BundleConceptWriter(staging);
            foreach (var concept in concepts)
            {
                var result = writer.WriteConcept(concept.Id.ToString(), concept.Document.Frontmatter, concept.Document.Body);
                if (result.StartsWith("Error:", StringComparison.Ordinal))
                {
                    failures.Add((concept.Id, result));
                }
                else
                {
                    written++;
                }
            }

            CommitStaging(staging, outPath);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        var notes = new List<string>();
        var outcome = Reconcile(outPath, repoPath, manifest, status, previous, failures.Count, notes);

        if (manifest is not null)
        {
            // WRITTEN LAST, after the final staged file has been moved and after the prune -- the
            // ordering is load-bearing, not incidental. This file is the licence the NEXT run deletes
            // by, so a manifest describing a state the bundle never reached is worse than no manifest
            // at all. Written first, a failure during the commit (a directory sitting where a concept
            // file must land, a permission change, a full disk) would leave exactly that. Written
            // last, the same failure leaves the PREVIOUS manifest: conservative, and self-healing on
            // the next run. Pinned by PruningTests.The_manifest_is_written_after_the_concepts_it_describes.
            //
            // The new manifest is this run's ids PLUS every previous id that survived: a degraded run
            // that could not confirm an id gone must not drop it from the record, or the next complete
            // run would have no mandate to delete it and the concept would be orphaned for ever.
            //
            // Minus the ids that failed to write, which is not bookkeeping tidiness. A manifest claims
            // ownership, and ownership is what authorizes deletion: an id recorded here but never
            // written is a standing licence to delete whatever later appears at that path -- including
            // a file a human writes by hand, which is exactly what §6.3 rule 2 exists to protect.
            var failed = new HashSet<string>(failures.Select(f => f.Id.ToString()), StringComparer.Ordinal);
            var merged = manifest with
            {
                Concepts = [.. manifest.Concepts.Where(c => !failed.Contains(c.Id)), .. outcome.Carried],
            };

            merged.WriteTo(outPath);

            ReportUnownedFiles(outPath, manifest.OwnedPrefix, manifest, previous, notes);
        }

        IndexGenerator.RegenerateIndexes(outPath);

        return new WriteResult(written, failures)
        {
            Pruned = outcome.Pruned,
            Notes = notes,
        };
    }

    /// <summary>What reconciling this run against the previous manifest decided: what was deleted, and what is still owned but was not written this time.</summary>
    private sealed record ReconcileOutcome(IReadOnlyList<ConceptId> Pruned, IReadOnlyList<ManifestConcept> Carried);

    private static readonly ReconcileOutcome NothingToReconcile = new([], []);

    /// <summary>
    /// §6.3 rules 2 and 3: deletes the concepts the previous run produced that this run did not, and
    /// only those it can prove are gone rather than merely unread.
    ///
    /// <para>Every candidate has to clear three independent checks. It must be named by the
    /// <b>previous manifest</b> -- so a file the generator never produced is never this producer's to
    /// delete, whatever its path. It must lie under the <b>owned prefix</b> -- a redundant check
    /// against the manifest itself, kept because the manifest is a file in a directory the user
    /// controls. And every source file it was derived from must be <b>settled</b> by this run: read
    /// and parsed in full, or absent from the repository altogether.</para>
    /// </summary>
    private static ReconcileOutcome Reconcile(
        string outPath,
        string repoPath,
        GenerationManifest? manifest,
        RunStatus? status,
        GenerationManifest? previous,
        int failureCount,
        List<string> notes)
    {
        if (previous is null)
        {
            return NothingToReconcile;
        }

        var thisRunIds = new HashSet<string>(manifest?.ConceptIds ?? [], StringComparer.Ordinal);

        // previous.Concepts is Ordinal-sorted by id (GenerationManifest normalizes on read), and Where
        // preserves that order, so everything downstream -- deletions, notes, the merged manifest -- is
        // ordered by the id itself and never by a hash table (§6.2).
        var candidates = previous.Concepts.Where(c => !thisRunIds.Contains(c.Id)).ToList();
        if (candidates.Count == 0)
        {
            return NothingToReconcile;
        }

        if (RefusalToPrune(manifest, status, previous, repoPath, failureCount) is { } refusal)
        {
            notes.Add($"{candidates.Count} concept(s) this run did not generate were kept: {refusal}");
            return new ReconcileOutcome([], candidates);
        }

        // Not null: RefusalToPrune returns non-null for either being null, and we did not return.
        var run = status!;

        var clean = new HashSet<string>(StringComparer.Ordinal);
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        var statusByFile = new Dictionary<string, FileStatus>(StringComparer.Ordinal);
        foreach (var (path, fileStatus) in run.Skipped)
        {
            var normalized = SourceOwnershipMap.Normalize(path);
            attempted.Add(normalized);
            statusByFile[normalized] = fileStatus;
            if (fileStatus == FileStatus.Extracted)
            {
                clean.Add(normalized);
            }
        }

        var pruned = new List<ConceptId>();
        var carried = new List<ManifestConcept>();
        var heldBackByFile = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (!IsUnderPrefix(candidate.Id, previous.OwnedPrefix))
            {
                carried.Add(candidate);
                notes.Add($"'{candidate.Id}' is recorded in the previous manifest but lies outside the owned prefix '{previous.OwnedPrefix}'; it was kept.");
                continue;
            }

            if (candidate.SourceFiles.Count == 0)
            {
                // No owner means no way to tell a deleted symbol from an unread file, and the safe
                // reading of "I cannot tell" is always "keep".
                carried.Add(candidate);
                continue;
            }

            var blocking = candidate.SourceFiles
                .Where(file => !IsSettled(file, repoPath, clean, attempted))
                .ToList();

            if (blocking.Count > 0)
            {
                carried.Add(candidate);
                foreach (var file in blocking)
                {
                    // Normalized on the way in, so this map and `statusByFile` are keyed the same way
                    // and the diagnostic below cannot miss on a separator.
                    var key = SourceOwnershipMap.Normalize(file);
                    heldBackByFile[key] = heldBackByFile.GetValueOrDefault(key) + 1;
                }

                continue;
            }

            if (TryResolveConceptFile(outPath, candidate.Id, out var conceptPath) is not { } conceptId)
            {
                carried.Add(candidate);
                notes.Add($"'{candidate.Id}' does not resolve to a file inside the bundle, so it was kept rather than deleted.");
                continue;
            }

            try
            {
                if (File.Exists(conceptPath))
                {
                    File.Delete(conceptPath);
                    pruned.Add(conceptId);
                }

                // A candidate whose file is already gone is dropped from the manifest without being
                // reported as pruned: this run confirmed the symbol no longer exists, and the bundle
                // no longer carries the concept, so there is nothing left to own.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                carried.Add(candidate);
                notes.Add($"'{candidate.Id}' could not be deleted ({ex.Message}); it was kept.");
            }
        }

        // Sorted Ordinal by path, so this diagnostic reads the same on every run over the same input.
        foreach (var file in heldBackByFile.Keys.OrderBy(f => f, StringComparer.Ordinal))
        {
            var reason = statusByFile.TryGetValue(file, out var recorded)
                ? $"this run recorded it as {recorded}"
                : "this run did not visit it and it still exists in the repository";

            notes.Add(
                $"{heldBackByFile[file]} concept(s) owned by '{file}' were absent from this run but kept: {reason},"
                + " so the symbols may be unread rather than deleted.");
        }

        if (pruned.Count > 0)
        {
            notes.Add($"Pruned {pruned.Count} concept(s) whose symbols are gone from the repository.");
        }

        RemoveEmptyDirectories(outPath, previous.OwnedPrefix, pruned);

        return new ReconcileOutcome(pruned, carried);
    }

    /// <summary>
    /// Why this run may not delete anything, or <see langword="null"/> when it may. Ordered from the
    /// caller's own opt-out down to the two run-quality facts, so the note names the first reason that
    /// applied rather than a generic one.
    /// </summary>
    private static string? RefusalToPrune(
        GenerationManifest? manifest,
        RunStatus? status,
        GenerationManifest previous,
        string repoPath,
        int failureCount)
    {
        // The write policy is not among the checks because it cannot fail here: a previous manifest
        // only ever reaches this method under WritePolicy.Update. Reset deleted the directory it lived
        // in before this run wrote a byte, and RequireEmpty refuses to run at all against a directory
        // that has one. A branch for it would be a note no input could produce.
        if (manifest is null || status is null)
        {
            return "this run supplied no generation manifest or no extraction status, so it claims ownership of nothing.";
        }

        if (!string.Equals(manifest.OwnedPrefix, previous.OwnedPrefix, StringComparison.Ordinal))
        {
            return $"this run owns the prefix '{manifest.OwnedPrefix}' but the previous manifest claims '{previous.OwnedPrefix}'.";
        }

        // TraversalComplete, and deliberately NOT IsComplete. IsComplete additionally requires every
        // file to have parsed cleanly, and the vendored tree-sitter grammar mis-parses an empty
        // collection expression `[]` -- ordinary modern C#, and this repository's own idiom -- so
        // gating here would make pruning dead code. The per-file quality is not ignored, it is applied
        // one candidate at a time by IsSettled; what a truncated traversal breaks is different in kind:
        // a symbol may have MOVED to a file this run never reached, and deleting its old concept would
        // lose it with no replacement.
        if (!status.TraversalComplete)
        {
            return "this run did not visit every eligible file, so a symbol may have moved to a file it never reached.";
        }

        // A run that attempted nothing at all is indistinguishable from one where code extraction never
        // ran (a --no-code run, a profile list matching no file, a scope that excluded everything).
        // Its empty id set would otherwise be a mandate to delete every code concept in the bundle.
        if (status.Skipped.Count == 0)
        {
            return "this run analysed no source file at all, so its empty result is not evidence that anything was deleted.";
        }

        if (failureCount > 0)
        {
            return $"{failureCount} concept(s) could not be written, so this run did not fully succeed.";
        }

        // The last line of defence behind "not visited => gone": that inference is only sound if the
        // repository is actually there to be looked at.
        if (!Directory.Exists(repoPath))
        {
            return $"the repository path '{repoPath}' does not exist, so nothing can be confirmed deleted.";
        }

        return null;
    }

    /// <summary>
    /// Whether this run can vouch for what <paramref name="file"/> declares. Two ways, and only two:
    /// it was read and parsed in full (<see cref="FileStatus.Extracted"/>), or the traversal -- which
    /// the caller has already established was complete -- never saw it AND it is not in the repository
    /// any more, which is what a deleted file looks like.
    ///
    /// <para>A file that was attempted and came back anything other than
    /// <see cref="FileStatus.Extracted"/> is not settled, even though most such runs are perfectly
    /// ordinary: a partially-parsed file may simply have lost the declaration in an error region.
    /// Neither is a file the run never visited that is still on disk -- it became ineligible (a scope
    /// change, a profile change, a rename out of the extension set), which says nothing about whether
    /// the symbol exists.</para>
    /// </summary>
    private static bool IsSettled(string file, string repoPath, HashSet<string> clean, HashSet<string> attempted)
    {
        var normalized = SourceOwnershipMap.Normalize(file);
        if (clean.Contains(normalized))
        {
            return true;
        }

        if (attempted.Contains(normalized))
        {
            return false;
        }

        return !RepositoryFileExists(repoPath, normalized);
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> still exists under <paramref name="repoPath"/>. A path
    /// that escapes the repository root is reported as existing -- the conservative answer, since
    /// "exists" is the value that keeps a concept alive.
    /// </summary>
    private static bool RepositoryFileExists(string repoPath, string relativePath)
    {
        try
        {
            var root = Path.GetFullPath(repoPath);
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            return !candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison) || File.Exists(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Resolves a manifest id to the file it names inside the bundle, refusing anything that is not a
    /// well-formed concept id whose canonical form is the id as recorded, and anything that resolves
    /// outside <paramref name="bundleRoot"/>.
    ///
    /// <para>The manifest is a file in a directory the user (or anything else on the machine) can
    /// edit, and this is the one place its contents turn into a <see cref="File.Delete(string)"/>.
    /// <see cref="ConceptId.ValidateSegment"/> already rejects a <c>..</c> segment -- <c>.</c> is not
    /// a valid first character -- and the round-trip check rejects an id that parses to something other
    /// than what it says; the full-path containment check then stands behind both, because neither of
    /// them is looking at the filesystem and a symlinked directory inside the bundle is not something
    /// a string test can see.</para>
    /// </summary>
    private static ConceptId? TryResolveConceptFile(string bundleRoot, string id, out string path)
    {
        path = string.Empty;
        if (!ConceptId.TryParse(id, out var parsed) || !string.Equals(parsed.ToString(), id, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(bundleRoot);
            var candidate = Path.GetFullPath(parsed.ToPath(root));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            {
                return null;
            }

            path = candidate;
            return parsed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// After a prune, removes the directories the deleted concepts left behind -- bottom-up, within the
    /// owned prefix only, and only where nothing remains but the <c>index.md</c> this producer's own
    /// regeneration put there. A directory holding any other file, or any subdirectory, is left alone.
    /// </summary>
    private static void RemoveEmptyDirectories(string outPath, string ownedPrefix, IReadOnlyList<ConceptId> pruned)
    {
        if (pruned.Count == 0)
        {
            return;
        }

        string prefixRoot;
        string root;
        try
        {
            root = Path.GetFullPath(outPath);
            prefixRoot = Path.GetFullPath(Path.Combine(root, ownedPrefix.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return;
        }

        if (!prefixRoot.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            return;
        }

        foreach (var id in pruned)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(id.ToPath(root)));
            while (directory is not null
                && IsWithinPrefixRoot(directory, prefixRoot)
                && Directory.Exists(directory))
            {
                if (Directory.EnumerateDirectories(directory).Any())
                {
                    break;
                }

                var files = Directory.EnumerateFiles(directory).ToList();
                if (files.Any(f => !string.Equals(Path.GetFileName(f), IndexFile, StringComparison.Ordinal)))
                {
                    break;
                }

                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }

                directory = Path.GetDirectoryName(directory);
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="directory"/> is the owned prefix's own directory or one nested inside
    /// it. Compares path components rather than raw string prefixes, so a sibling directory whose name
    /// merely starts with the prefix (<c>code2/</c> beside <c>code/</c>) is not walked into and
    /// deleted.
    /// </summary>
    private static bool IsWithinPrefixRoot(string directory, string prefixRoot) =>
        string.Equals(directory, prefixRoot, PathComparison)
        || directory.StartsWith(prefixRoot + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>
    /// §6.3 rule 2's other half: a markdown file under the owned prefix that no manifest claims is
    /// hand-written, so it is left exactly where it is and reported as not owned -- the alternative,
    /// deleting anything under the prefix this run did not write, is the very behaviour the manifest
    /// exists to rule out.
    /// </summary>
    private static void ReportUnownedFiles(
        string outPath,
        string ownedPrefix,
        GenerationManifest manifest,
        GenerationManifest? previous,
        List<string> notes)
    {
        var owned = new HashSet<string>(manifest.ConceptIds, StringComparer.Ordinal);
        foreach (var id in previous?.ConceptIds ?? [])
        {
            owned.Add(id);
        }

        var prefixDirectory = Path.Combine(outPath, ownedPrefix.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(prefixDirectory))
        {
            return;
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(prefixDirectory, "*.md", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var unowned = new List<string>();
        foreach (var file in files)
        {
            if (string.Equals(Path.GetFileName(file), IndexFile, StringComparison.Ordinal))
            {
                continue;
            }

            if (!TryConceptIdOf(outPath, file, out var id) || owned.Contains(id))
            {
                continue;
            }

            unowned.Add(id);
        }

        foreach (var id in unowned.OrderBy(i => i, StringComparer.Ordinal))
        {
            notes.Add(
                $"'{id}' sits under the owned prefix '{ownedPrefix}' but no manifest claims it;"
                + " it was left in place and will never be pruned.");
        }
    }

    private static bool TryConceptIdOf(string bundleRoot, string file, out string id)
    {
        try
        {
            id = ConceptId.FromPath(Path.GetFullPath(bundleRoot), Path.GetFullPath(file)).ToString();
            return true;
        }
        catch (Exception ex) when (ex is ConceptIdException or ArgumentException or IOException or NotSupportedException)
        {
            id = string.Empty;
            return false;
        }
    }

    /// <summary>Whether <paramref name="id"/> is <paramref name="prefix"/> itself or lives under it.</summary>
    private static bool IsUnderPrefix(string id, string prefix) =>
        string.Equals(id, prefix, StringComparison.Ordinal)
        || id.StartsWith(prefix + "/", StringComparison.Ordinal);

    /// <summary>
    /// Creates the staging directory beside <paramref name="outPath"/>. The name carries a GUID rather
    /// than being derived from the bundle: it is transient and never reaches the output, so it needs
    /// uniqueness (two runs against one bundle must not share it), not determinism.
    /// </summary>
    private static string CreateStagingDirectory(string outPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(outPath));
        var staging = Path.Combine(
            string.IsNullOrEmpty(parent) ? Path.GetTempPath() : parent,
            StagingPrefix + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(staging);
        return staging;
    }

    /// <summary>
    /// Moves everything staged into the bundle, creating directories as it goes and overwriting what
    /// is already there.
    ///
    /// <para><b>Why the staged content is moved file by file rather than the staging directory being
    /// swapped in whole.</b> The swap reads as the more transactional of the two, and for the bundle
    /// as a whole it is the more dangerous: it requires deleting the existing bundle first, so a crash
    /// in the instant between the delete and the rename destroys everything the bundle held --
    /// including the hand-written concepts outside the owned prefix that this producer goes to some
    /// length never to touch. Moving file by file cannot lose data: the worst interruption leaves a mix
    /// of new and old concepts, which the next run corrects. The property that actually needs to hold
    /// -- a run that fails while producing content leaves the bundle untouched -- is provided by the
    /// staging directory itself, not by the shape of the commit.</para>
    ///
    /// <para><b>One thing staging gives up, stated so nobody discovers it later.</b>
    /// <c>BundleConceptWriter</c> serializes writes through a lock keyed on its bundle root, so two
    /// concurrent runs against one bundle used to interleave at concept granularity. They now write to
    /// two different staging roots and meet only here, where no lock applies. Two <c>generate</c> runs
    /// into the same <c>--out</c> were already unsafe -- they share one manifest and one index
    /// regeneration -- so this narrows nothing that was safe; it just means the last writer wins per
    /// file rather than per lock acquisition.</para>
    /// </summary>
    private static void CommitStaging(string staging, string outPath)
    {
        var staged = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        foreach (var source in staged)
        {
            var destination = Path.Combine(outPath, Path.GetRelativePath(staging, source));
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(source, destination, overwrite: true);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover staging directory beside the bundle is untidy, never harmful: it is outside
            // the bundle, so nothing loads it, and failing the run over it would turn a successful
            // generation into an error.
        }
    }

    /// <summary>
    /// True if <paramref name="ancestorCandidate"/> (already <see cref="Path.GetFullPath(string)"/>-resolved)
    /// equals <paramref name="path"/> (likewise resolved), or is one of its ancestor directories.
    /// Compares path components, not raw string prefixes (so <c>/repo</c> is not mistaken for an
    /// ancestor of <c>/repository</c>).
    /// </summary>
    private static bool IsSameOrAncestor(string ancestorCandidate, string path)
    {
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var ancestor = ancestorCandidate.TrimEnd(separators);
        var target = path.TrimEnd(separators);

        return string.Equals(ancestor, target, PathComparison)
            || target.StartsWith(ancestor + Path.DirectorySeparatorChar, PathComparison);
    }
}
