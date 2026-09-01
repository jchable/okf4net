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

        // The refusal is checked here, before a byte of work is done, so an operator who pointed --out
        // at the repository is told immediately. The DELETE it guards is not here -- see the Reset
        // block inside the staging try below, and IBundleWriter's transactional guarantee.
        if (policy == WritePolicy.Reset && Directory.Exists(outPath))
        {
            var fullOut = Path.GetFullPath(outPath);
            var fullRepo = Path.GetFullPath(repoPath);
            if (IsSameOrAncestor(fullOut, fullRepo))
            {
                throw new InvalidOperationException(
                    $"Refusing to reset '{outPath}': it is the same as, or an ancestor of, the repository being scanned ('{repoPath}'). Choose a different --out.");
            }
        }

        if (policy == WritePolicy.RequireEmpty && Directory.Exists(outPath) && Directory.EnumerateFileSystemEntries(outPath).Any())
        {
            throw new InvalidOperationException(
                $"Output directory '{outPath}' is not empty. Use --update or --reset.");
        }

        Directory.CreateDirectory(outPath);

        // Read before anything is written, so the "previous" run really is the previous one. Only
        // Update can have one: Reset empties the directory it lived in as part of the commit below,
        // and RequireEmpty refuses to run at all unless the directory is empty.
        var previous = policy == WritePolicy.Update ? GenerationManifest.TryRead(outPath) : null;

        // Read at the same moment, and for the one thing a null `previous` cannot tell apart: "no
        // manifest was ever written here" from "a manifest is here and this build cannot read it".
        // Identical for every deletion decision below -- both own nothing -- and not identical at all
        // for what this run may TELL an operator about a file it overwrote. See ReportClaimedFiles.
        var previousUnreadable = policy == WritePolicy.Update && previous is null && GenerationManifest.IsPresent(outPath);

        var failures = new List<(ConceptId Id, string Error)>();
        var notes = new List<string>();
        var written = 0;

        // §6.3 rule 1. Everything is produced into a directory the bundle cannot see; the bundle is
        // only touched by the commit block below, after the whole set exists. A throw anywhere in the
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

            // The Reset deletion happens HERE and not at the top of the method. Deleting first means a
            // run that then throws while generating -- a hostile source file, a full disk, a bug in the
            // extractor -- has already destroyed the bundle, which is precisely what IBundleWriter's
            // transactional guarantee says cannot happen. Deleted at the commit boundary instead, the
            // window between "the old bundle exists" and "the new one is in place" is the commit loop
            // alone, and everything before it is undoable by doing nothing.
            if (policy == WritePolicy.Reset)
            {
                ResetBundle(outPath);
            }

            // Before the commit, because it is the pre-overwrite state on disk that answers the
            // question. After the Reset above, because a reset bundle has nothing left to claim.
            if (manifest is not null)
            {
                ReportClaimedFiles(staging, outPath, manifest, previous, previousUnreadable, notes);
            }

            // Recorded as write failures, before Reconcile, because that is exactly what they are: the
            // concept never reached the bundle. Doing it here rather than as a note gets both of the
            // consequences for free -- the id is excluded from the manifest this run writes (so it is
            // never claimed as owned, and never becomes a licence to delete whatever appears at that
            // path), and a non-zero failure count disqualifies the run from pruning.
            foreach (var relative in CommitStaging(staging, outPath))
            {
                written--;
                var destination = Path.Combine(outPath, relative.Replace('/', Path.DirectorySeparatorChar));
                var error =
                    $"Error: refused to write '{relative}': the path leaves the bundle root through a symbolic link"
                    + " or junction, and File.Move would have followed it and overwritten a file outside the bundle."
                    + " Remove the link, or generate into a bundle that does not contain one.";

                // Either a failure or a note, never both. Adding it to both put the same sentence on
                // stderr twice under clashing prefixes -- OkfgenCli renders one as
                // "note: Error: refused to write ..." and the other as
                // "error: code/x/report: Error: refused to write ..." -- which reads as two problems
                // and makes the "note:" copy look like something that did not stop the run. The note
                // is the fallback for the one case with no failure to carry the text: a staged path
                // whose concept id will not parse, which no id this producer generates can produce,
                // but which is the branch that would otherwise report the refusal nowhere at all.
                if (TryConceptIdOf(outPath, destination, out var id) && ConceptId.TryParse(id, out var parsed))
                {
                    failures.Add((parsed, error));
                }
                else
                {
                    notes.Add(error);
                }
            }
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

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

                // And the SCOPE is widened with the concept set, which is not bookkeeping either. The
                // set above is this run's ids PLUS ids a wider run produced; recording this run's own
                // narrow scope over it describes a state no run ever reached, and it turns the
                // narrowing refusal into a one-run reprieve: the next identical command finds
                // `previous.Scope == manifest.Scope`, sees no narrowing, finds the carried concept
                // settled (its file was read cleanly), and deletes exactly what the refusal existed to
                // protect -- taking any hand-written description with it. See MergedScope.
                Scope = MergedScope(manifest, previous, outcome.Carried),
            };

            merged.WriteTo(outPath);

            ReportUnownedFiles(outPath, manifest.OwnedPrefix, manifest, previous, notes);
        }

        // Not gated HERE, and it does not need to be: IndexGenerator gates itself. A previous round of
        // this review recorded the opposite as established fact -- that this call "follows a junction
        // inside the bundle and writes outside it" -- and that claim was false in every clause. Read
        // src/OKF4net/IndexGenerator.cs before restoring any version of it:
        //
        //   * the traversal is CollectMarkdown over Directory.GetFileSystemEntries (:427), not
        //     Directory.EnumerateDirectories, and it tests ReparsePoints.IsReparsePoint BEFORE it
        //     recurses (:437), so nothing under a linked directory is ever collected;
        //   * the per-directory child listing applies the same skip (:219), so a linked subdirectory
        //     contributes no index entry either;
        //   * immediately before each write there is an ancestor re-check (:259) AND an
        //     IsReparsePoint check on the index.md file node itself (:280), the latter added -- per
        //     its own comment -- to close exactly this class;
        //   * ReparsePoints.IsReparsePoint answers for Windows junctions and Unix symlinks alike.
        //
        // So this is the most heavily gated of the writes reachable from here, not the ungated one.
        // The gates live in OKF4net rather than in this producer, which is the only sense in which
        // this call differs from the ones above.
        IndexGenerator.RegenerateIndexes(outPath);

        return new WriteResult(written, failures)
        {
            Pruned = outcome.Pruned,
            Notes = notes,
        };
    }

    /// <summary>
    /// The scope to record for a manifest whose concept set is this run's output <b>plus</b> entries
    /// carried over from the previous manifest: the widest scope that covers that whole set, so the
    /// next run has to be at least that wide before it may delete any of it.
    ///
    /// <para><b>Why a union and not this run's scope.</b> <see cref="GenerationManifest.Scope"/> is
    /// read back as a gate, and what it gates is the id list it sits beside. Widen the list to keep a
    /// wider run's concepts -- which is exactly what a narrowing refusal does -- while leaving the
    /// scope at this run's narrow value, and the file claims a narrow run produced a wide run's
    /// concepts. One run later the refusal is gone: the next identical command compares equal scopes,
    /// finds no narrowing, and prunes the concepts the refusal was protecting.</para>
    ///
    /// <para><b>It cannot over-refuse.</b> The union differs from this run's own scope only where the
    /// previous manifest sets a flag this run dropped -- which is precisely the condition
    /// <see cref="Narrowing"/> tests, and a narrowing run prunes nothing anyway. Where nothing was
    /// carried, the set is this run's output alone and this run's scope is the honest record of it.
    /// The alternative shape -- a per-entry scope so a carried concept keeps the scope that produced
    /// it -- says the same thing about the set as a whole and costs another schema version, so the
    /// union is preferred; it is more conservative only in the cases the gate already refuses.</para>
    ///
    /// <para><see langword="null"/> when either side is unknown and anything was carried: the carried
    /// entries then came from a run whose coverage this build cannot establish, and "unknown" is the
    /// value that refuses. It is not a dead end -- a later run that produces every id the manifest
    /// claims carries nothing, and records its own scope.</para>
    /// </summary>
    private static ScopeOptions? MergedScope(
        GenerationManifest manifest,
        GenerationManifest? previous,
        IReadOnlyList<ManifestConcept> carried)
    {
        if (carried.Count == 0)
        {
            return manifest.Scope;
        }

        if (manifest.Scope is not { } scope || previous?.Scope is not { } previousScope)
        {
            return null;
        }

        return new ScopeOptions(
            IncludeTests: scope.IncludeTests || previousScope.IncludeTests,
            IncludeInternal: scope.IncludeInternal || previousScope.IncludeInternal);
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

            if (TryResolveConceptFile(outPath, candidate.Id, out var conceptPath, out var detail) is not { } conceptId)
            {
                carried.Add(candidate);
                notes.Add($"'{candidate.Id}' does not resolve to a file inside the bundle, so it was kept rather than deleted.{detail}");
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
            // NOT "whose symbols are gone from the repository", which this producer cannot know and
            // used to claim anyway. What it actually established is narrower and is what the sentence
            // now says: the previous manifest claimed the id, this run did not produce it, and every
            // file it was derived from was either read in full or is no longer in the repository. A
            // symbol that merely left the run's scope satisfies all three -- which is why the scope is
            // recorded in the manifest and a narrowing refuses the prune outright (see RefusalToPrune),
            // rather than being explained away in a note after the file is gone.
            notes.Add(
                $"Pruned {pruned.Count} concept(s) the previous manifest claimed and this run did not produce;"
                + " every source file they were derived from was read in full or is gone from the repository.");
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
        // The write policy is not among the checks because it cannot fail here: a previous manifest is
        // only ever READ under WritePolicy.Update. Reset does not read one at all (and empties the
        // directory it would have lived in), and RequireEmpty refuses to run against a directory that
        // has one. A branch for it would be a note no input could produce.
        if (manifest is null || status is null)
        {
            return "this run supplied no generation manifest or no extraction status, so it claims ownership of nothing.";
        }

        if (!string.Equals(manifest.OwnedPrefix, previous.OwnedPrefix, StringComparison.Ordinal))
        {
            return $"this run owns the prefix '{manifest.OwnedPrefix}' but the previous manifest claims '{previous.OwnedPrefix}'.";
        }

        // Before the run-quality checks, because a narrowed scope is not a degraded run: this run may
        // have read every file perfectly and still be unable to account for the concepts the previous
        // one produced. See GenerationManifest.Scope for the asymmetry that makes --include-internal
        // dangerous where --include-tests is not.
        if (previous.Scope is not { } previousScope)
        {
            // Names the way out, because this refusal is STICKY and nothing else tells an operator
            // that. MergedScope writes a null scope forward for as long as anything is carried, so a
            // manifest that lost its scope -- a hand edit, or a bundle written before the field
            // existed -- refuses on this branch every run until one accounts for every id it claims.
            // "Records no extraction scope" on its own reads like a fact about this run and leaves
            // the operator with no move.
            return "the previous manifest records no extraction scope, so this run cannot tell whether that run covered a wider"
                + " one than this. This does not clear itself: it holds until a run produces every id the manifest claims, or"
                + " until `--reset` rebuilds the bundle and its manifest together.";
        }

        if (manifest.Scope is not { } scope)
        {
            return "this run recorded no extraction scope of its own, so it cannot show that it covers everything the previous run did.";
        }

        if (Narrowing(previousScope, scope) is { } narrowed)
        {
            return narrowed;
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
    /// Which scope flags <paramref name="current"/> drops relative to <paramref name="previous"/>, as
    /// a sentence, or <see langword="null"/> when it drops none. Only NARROWING matters: a run that
    /// covers strictly more than the one that claimed the ids can still show a symbol is gone, while a
    /// run that covers less cannot distinguish "deleted" from "no longer in scope" -- and the second
    /// reading is the one that keeps a human's work.
    /// </summary>
    private static string? Narrowing(ScopeOptions previous, ScopeOptions current)
    {
        var dropped = new List<string>();
        if (previous.IncludeInternal && !current.IncludeInternal)
        {
            dropped.Add("--include-internal");
        }

        if (previous.IncludeTests && !current.IncludeTests)
        {
            dropped.Add("--include-tests");
        }

        return dropped.Count == 0
            ? null
            : $"the previous run covered a wider scope than this one ({string.Join(" and ", dropped)} was dropped),"
                + " so a concept missing from this run may simply be out of scope rather than gone from the repository.";
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

    /// <summary>The sentence appended to the refusal note when it was a reparse point, and not the id itself, that left the bundle.</summary>
    private const string LinkDetail =
        " The path leaves the bundle root through a symbolic link or junction, which no comparison of"
        + " path strings can see -- Path.GetFullPath does not resolve reparse points.";

    /// <summary>
    /// Resolves a manifest id to the file it names inside the bundle, refusing anything that is not a
    /// well-formed concept id whose canonical form is the id as recorded, and anything that resolves
    /// outside <paramref name="bundleRoot"/> -- <b>through the filesystem</b>, not through a string
    /// comparison.
    ///
    /// <para>The manifest is a file in a directory the user (or anything else on the machine) can
    /// edit, and this is the one place its contents turn into a <see cref="File.Delete(string)"/>.
    /// <see cref="ConceptId.ValidateSegment"/> already rejects a <c>..</c> segment -- <c>.</c> is not
    /// a valid first character -- and the round-trip check rejects an id that parses to something other
    /// than what it says.</para>
    ///
    /// <para><b>Those two are not enough, and the string check that used to stand behind them was not
    /// either.</b> <see cref="Path.GetFullPath(string)"/> resolves <c>.</c> and <c>..</c> and nothing
    /// else: it does not follow a symbolic link or a junction, so <c>StartsWith(root)</c> answered
    /// "inside" for <c>&lt;bundle&gt;/code/x/report.md</c> even when <c>code/x</c> was a link to
    /// somewhere else entirely -- and <see cref="File.Delete(string)"/> follows the link. Reachable in
    /// the workflow the README documents, since a bundle committed beside a repository is content a
    /// clone brings with it. So the containment question is asked of the filesystem instead, by
    /// <see cref="ResolveInsideRoot"/>, which walks the path component by component and follows every
    /// reparse point it meets.</para>
    ///
    /// <para>The lexical check is kept in front of it as a cheap first pass. It cannot fire on any id
    /// this producer can parse -- a segment is <c>[A-Za-z0-9_][A-Za-z0-9_.-]*</c>, which admits neither
    /// <c>..</c> nor a drive letter nor a separator, so no id can join to a path outside the root --
    /// and it is left in place as the backstop against a future relaxation of that charset, not as
    /// today's defence. What defends today is the resolution below it.</para>
    /// </summary>
    /// <param name="bundleRoot">Root of the bundle. Its own reparse points are resolved too, so a bundle that lives behind a junction is not refused wholesale.</param>
    /// <param name="id">The id, as the previous manifest recorded it.</param>
    /// <param name="path">The <b>resolved</b> path to delete, or empty when the id was refused.</param>
    /// <param name="detail">A sentence to append to the caller's refusal note, or empty when the plain note says everything.</param>
    private static ConceptId? TryResolveConceptFile(string bundleRoot, string id, out string path, out string detail)
    {
        path = string.Empty;
        detail = string.Empty;

        if (!ConceptId.TryParse(id, out var parsed) || !string.Equals(parsed.ToString(), id, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            if (ResolveRoot(bundleRoot) is not { } root)
            {
                detail = LinkDetail;
                return null;
            }

            var candidate = Path.GetFullPath(parsed.ToPath(root));
            if (!IsInside(root, candidate))
            {
                return null;
            }

            if (ResolveInsideRoot(root, candidate) is not { } resolved)
            {
                detail = LinkDetail;
                return null;
            }

            path = resolved;
            return parsed;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="bundleRoot"/> as an absolute path with its own reparse point followed, or
    /// <see langword="null"/> when it is a link this process cannot follow.
    ///
    /// <para>Resolved rather than taken literally so that a bundle which <i>is</i> a junction -- or
    /// sits behind one the operator created deliberately -- is not treated as an escape by every
    /// containment check below. Only the root's own link is followed; a link on one of its ancestors
    /// is irrelevant, because every path compared against it is built from this same value.</para>
    /// </summary>
    private static string? ResolveRoot(string bundleRoot)
    {
        try
        {
            var full = Path.GetFullPath(bundleRoot);
            var info = new DirectoryInfo(full);
            if (info.LinkTarget is null)
            {
                return full;
            }

            return info.ResolveLinkTarget(returnFinalTarget: true) is { } target
                ? Path.GetFullPath(target.FullName)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where <paramref name="candidate"/> really lands once every symbolic link and junction between
    /// <paramref name="resolvedRoot"/> and it has been followed, or <see langword="null"/> when that
    /// lands outside the root -- or when the answer cannot be established at all.
    ///
    /// <para>Walked component by component because the BCL cannot answer it in one call:
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> resolves the path it is given only if <i>that</i>
    /// path is itself a link, and the dangerous shape is a link several components up with an ordinary
    /// file name hanging off it. <c>returnFinalTarget: true</c> at each hop, since a chain of links
    /// that passes back through the bundle proves nothing about where the last one lands.</para>
    ///
    /// <para>Every failure is <see langword="null"/>, which the callers read as "refuse". A broken
    /// link, a permission error, a path the platform rejects: none of them is evidence that deleting
    /// is safe, and this is the code path that ends in <see cref="File.Delete(string)"/>. The broken
    /// link is the one this used to get wrong -- see <see cref="LinkAt"/>.</para>
    /// </summary>
    private static string? ResolveInsideRoot(string resolvedRoot, string candidate)
    {
        string relative;
        try
        {
            relative = Path.GetRelativePath(resolvedRoot, candidate);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var current = resolvedRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            try
            {
                current = Path.Combine(current, segment);

                FileSystemInfo? info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current) ? new FileInfo(current) : LinkAt(current);

                if (info?.LinkTarget is null)
                {
                    continue;
                }

                if (info.ResolveLinkTarget(returnFinalTarget: true) is not { } target)
                {
                    return null;
                }

                current = Path.GetFullPath(target.FullName);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return IsInside(resolvedRoot, current) ? current : null;
    }

    /// <summary>
    /// The reparse point at <paramref name="path"/> when neither <see cref="Directory.Exists"/> nor
    /// <see cref="File.Exists"/> could see one, or <see langword="null"/> when there is none.
    ///
    /// <para><b>Why the Exists probes are not enough, which the doc above claimed they were.</b> Both
    /// of them FOLLOW a symbolic link, so a link whose target has been removed answers false to both
    /// -- and the component walk above then treated it as an ordinary path component and carried on,
    /// while <see cref="ResolveInsideRoot"/> promised in writing that a broken link is refused. The
    /// link's own target string is still on disk, so asking for it directly finds it.</para>
    ///
    /// <para><b>Measured, and it is not uniform.</b> On Windows a dangling <i>junction</i> is already
    /// caught: a junction is a real directory entry, so <c>Directory.Exists</c> answers true even with
    /// its target gone, and the walk resolves it. The gap is a dangling <i>symbolic</i> link, which
    /// needs SeCreateSymbolicLinkPrivilege to create on Windows -- so no test in this suite can reach
    /// this method on an ordinary Windows run, and it is left untested rather than covered by an
    /// assertion that would pass whatever this code did. On Unix, where a symbolic link needs no
    /// privilege, it is the ordinary shape.</para>
    ///
    /// <para>Reached only after both probes have failed, deliberately: probing first would change
    /// which of <see cref="DirectoryInfo"/> and <see cref="FileInfo"/> is handed to
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> for links that resolve perfectly well today,
    /// and that argument is not inert -- it tells the BCL which kind of object to expect at the far
    /// end. This adds the missing case without moving any case that already worked.</para>
    /// </summary>
    private static FileSystemInfo? LinkAt(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.LinkTarget is not null)
        {
            return directory;
        }

        var file = new FileInfo(path);
        return file.LinkTarget is not null ? file : null;
    }

    /// <summary>Whether <paramref name="path"/> lies strictly under <paramref name="root"/>, comparing whole path components.</summary>
    private static bool IsInside(string root, string path) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    /// <summary>
    /// After a prune, removes the directories the deleted concepts left behind -- bottom-up, within the
    /// owned prefix only, and only where nothing remains but the <c>index.md</c> this producer's own
    /// regeneration put there. A directory holding any other file, or any subdirectory, is left alone.
    ///
    /// <para><b>Every directory the walk ascends through is resolved before it is deleted.</b> This is
    /// the one place the producer calls <c>Directory.Delete(recursive: true)</c>, and the walk climbs
    /// paths built by string concatenation from ids: without the resolution, a junction sitting
    /// anywhere between the owned prefix and a pruned concept is a directory the walk reaches
    /// lexically and deletes. A directory that does not resolve to itself is left alone and the climb
    /// stops there -- a link is not this producer's to remove even when the bundle contains it,
    /// because what hangs off the far end was never the bundle's.</para>
    ///
    /// <para><b>What is at stake there, stated at its real size rather than its scariest.</b> The
    /// guard stops the LINK being unlinked, not the tree behind it being erased:
    /// <c>Directory.Delete(recursive: true)</c> does not recurse through a name-surrogate reparse
    /// point on Windows, and on Unix it unlinks the symbolic link rather than walking into it. So an
    /// unguarded delete costs the operator the structure they put in the bundle, and the far end
    /// survives. That is still not this producer's to remove, which is why the guard is here and why
    /// <c>PruningTests.The_directory_cleanup_after_a_prune_removes_no_link_the_bundle_merely_holds</c>
    /// asserts exactly that -- the link still exists and is still a link. An earlier version of this
    /// paragraph implied the tree went with it; it does not.</para>
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
            if (ResolveRoot(outPath) is not { } resolvedRoot)
            {
                return;
            }

            root = resolvedRoot;
            prefixRoot = Path.GetFullPath(Path.Combine(root, ownedPrefix.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return;
        }

        if (!IsInside(root, prefixRoot))
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
                // Identity, not mere containment: the directory must be reachable from the root with
                // no reparse point anywhere on the way. Without it the delete unlinks a link the
                // operator put in the bundle -- the structure, not the tree behind it, which a
                // recursive delete does not follow through a reparse point either way.
                if (ResolveInsideRoot(root, directory) is not { } resolved
                    || !string.Equals(resolved, directory, PathComparison))
                {
                    break;
                }

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

        var root = ResolveRoot(outPath);
        if (root is null)
        {
            return;
        }

        var prefixDirectory = Path.Combine(root, ownedPrefix.Replace('/', Path.DirectorySeparatorChar));
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

            // Directory.EnumerateFiles(AllDirectories) DESCENDS a junction: its default
            // EnumerationOptions skips Hidden and System entries, and a reparse point is neither, so
            // the walk follows one and hands back paths at the far end. Measured, not assumed -- a
            // bundle carrying `code/x -> ~/notes` reported "'code/x/report' sits under the owned
            // prefix 'code' but no manifest claims it" about a file that is not in the bundle and
            // never was. This is the only place here that reaches outside, and it reaches by reading
            // rather than writing, so what it cost was a false statement about somebody else's file
            // rather than the file itself.
            if (ResolveInsideRoot(root, Path.GetFullPath(file)) is null)
            {
                continue;
            }

            if (!TryConceptIdOf(root, file, out var id) || owned.Contains(id))
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

    /// <summary>
    /// §6.3 rule 2's blind spot, made visible: a file under the owned prefix that no previous manifest
    /// claimed and that this run is about to <b>overwrite</b>.
    ///
    /// <para>Rule 2 protects such a file from DELETION -- pruning only ever considers ids the previous
    /// manifest recorded, so a concept a human wrote by hand is never this producer's to delete, and
    /// <see cref="ReportUnownedFiles"/> says so. Nothing protected it from being written over. The
    /// moment the generator produces the same id, <see cref="CommitStaging"/> moves the staged file
    /// with <c>overwrite: true</c> and the hand-written body, description and every unknown key are
    /// gone -- silently, because by then this run's own manifest claims the id, so
    /// <see cref="ReportUnownedFiles"/> sees an owned file and says nothing.</para>
    ///
    /// <para><b>§4.2 does not cover it either, and cannot.</b> Field preservation keys on
    /// <c>description_source</c>: a file a human wrote has no such key, so
    /// <see cref="DescriptionResolver"/> takes its ordinary derive path. That is the correct reading of
    /// an absent key -- it is also what "fresh concept" looks like -- and it is why this note exists
    /// rather than a wider preservation rule guessing between the two.</para>
    ///
    /// <para>Bounded to the owned prefix, deliberately. That is the subtree §6.3 governs and the one
    /// this producer claims; <c>overview</c>, <c>packages/*</c> and <c>docs/*</c> are wholly derived
    /// from the repository and rewritten by every run by design, so reporting them would be a note on
    /// every file of every run against a bundle with no manifest.</para>
    ///
    /// <para><b>And it does not say "no manifest claimed it" when a manifest is sitting there unread.</b>
    /// <see cref="GenerationManifest.TryRead"/> returns null for a missing manifest, a corrupt one, and
    /// one whose schema this build does not know -- and that last case is every bundle written before
    /// the version-2 bump, on its very first <c>--update</c>. Reporting per file there would print one
    /// alarming line per code concept, each of them claiming as a fact about that file something the
    /// run cannot know, on an upgrade, about this producer's own output. So an unread manifest gets one
    /// note about the manifest instead, which is where the uncertainty actually lives.</para>
    /// </summary>
    /// <param name="previousUnreadable">
    /// A manifest file is present and this build could not read it, so <paramref name="previous"/>
    /// being null says nothing about which ids were claimed.
    /// </param>
    private static void ReportClaimedFiles(
        string staging,
        string outPath,
        GenerationManifest manifest,
        GenerationManifest? previous,
        bool previousUnreadable,
        List<string> notes)
    {
        var alreadyClaimed = new HashSet<string>(previous?.ConceptIds ?? [], StringComparer.Ordinal);

        // Resolved once, and a root this process cannot follow silences every note below rather than
        // producing them against unresolved paths. Nothing is lost by that: CommitStaging refuses the
        // whole commit on the same condition, and each refusal is reported as a write failure.
        var root = ResolveRoot(outPath);
        if (root is null)
        {
            return;
        }

        List<string> staged;
        try
        {
            staged = Directory.EnumerateFiles(staging, "*.md", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var claimed = new List<(string Id, string Note)>();
        var overwritten = 0;
        foreach (var source in staged)
        {
            var destination = Path.GetFullPath(Path.Combine(root, Path.GetRelativePath(staging, source)));

            // The SAME gate CommitStaging applies to this same path, one step earlier, because this
            // method reaches the file too: File.Exists follows a link and answers for whatever is at
            // the far end, and Overwrite below then reads that with File.ReadAllText. Without it a
            // bundle carrying `code/x -> ~/notes` got a note saying this run "has taken ownership of
            // that id and overwritten the file ... its previous body, description and any keys this
            // producer does not write are gone" about a file outside the bundle -- immediately
            // followed by the note refusing to write it. Two notes about one path, contradicting each
            // other, and only the second one true.
            if (ResolveInsideRoot(root, destination) is null
                || !File.Exists(destination)
                || !TryConceptIdOf(root, destination, out var id)
                || !IsUnderPrefix(id, manifest.OwnedPrefix)
                || alreadyClaimed.Contains(id))
            {
                continue;
            }

            overwritten++;
            if (previousUnreadable)
            {
                continue;
            }

            var (provenance, loss) = Overwrite(destination, DescriptionOf(source));
            claimed.Add((id,
                $"'{id}' already existed under the owned prefix '{manifest.OwnedPrefix}' and no previous manifest claimed it,"
                + $" so this run has taken ownership of that id and overwritten the file ({provenance}); {loss}"
                + " Recover what you need from version control and give the concept an id this producer does not generate."));
        }

        if (previousUnreadable)
        {
            // One note, and it names the count rather than the files: the run genuinely cannot tell
            // which of them it had written before, so listing them would be listing suspects. It also
            // says why nothing was pruned, which is the same fact seen from the other side and was
            // otherwise reported nowhere at all -- a run against an unread manifest returns from
            // Reconcile before any refusal note is written.
            //
            // Two spellings of the overwrite clause, because one of them used to render as "which of
            // the 0 file(s) it overwrote" -- an invitation to go checking version control for files
            // that do not exist, attached to the note whose whole purpose is to tell an operator
            // whether they need to.
            var overwriteClause = overwritten == 0
                ? $" It also overwrote no existing file under the owned prefix '{manifest.OwnedPrefix}', so there is nothing to check."
                : $" It also cannot say which of the {overwritten} file(s) it overwrote under the owned prefix"
                    + $" '{manifest.OwnedPrefix}' it had written before; if a concept under that prefix was"
                    + " hand-written, check it against version control now.";

            notes.Add(
                $"A generation manifest is present in '{outPath}' but this build could not read it -- it is corrupt, or it"
                + $" carries a schema version this build does not know (bundles written before schema {GenerationManifest.SchemaVersion}"
                + " do). It therefore claims nothing: this run pruned no concept."
                + overwriteClause
                + " This run leaves a manifest this build does read, so the next one behaves normally.");
            return;
        }

        foreach (var (_, note) in claimed.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            notes.Add(note);
        }
    }

    /// <summary>
    /// What the file about to be overwritten says about who wrote it, and -- separately, because the
    /// two answers are not the same -- what the overwrite actually destroys.
    ///
    /// <para><b>Two false versions of the second half, and why this one is answerable.</b> It began as
    /// the constant "its previous body, description and any keys this producer does not write are
    /// gone", printed even for a file whose <c>description_source</c> was <c>manual</c> -- exactly the
    /// value that makes §4.2 keep the description. Replacing the constant with
    /// <see cref="DescriptionResolver.PreservesDescription"/> traded that for a subtler falsehood:
    /// §4.2 is applied by the GENERATOR, and only when the caller supplies
    /// <c>GenerateOptions.ExistingFrontmatter</c>. The shipped CLI wires that under <c>--update</c>,
    /// so the note was true there and false for every other caller of
    /// <see cref="IBundleWriter.Write"/> -- including this repository's own tests, where the file on
    /// disk after the run carried the generated description while the note said the human's had
    /// survived.</para>
    ///
    /// <para><b>So the question is asked of the bytes instead of the rule.</b>
    /// <paramref name="incoming"/> is the description in the staged document that is about to replace
    /// this file. If the file's own description is non-empty and identical to it, the overwrite
    /// changes no description -- whoever arranged that, and whether §4.2 was consulted at all. That is
    /// a statement this writer can make about this run, and it stays true for a caller that never
    /// heard of <see cref="DescriptionResolver"/>.</para>
    /// </summary>
    /// <param name="path">The file about to be overwritten.</param>
    /// <param name="incoming">The description of the staged document replacing it, or <see langword="null"/> when it has none or could not be read.</param>
    private static (string Provenance, string Loss) Overwrite(string path, string? incoming)
    {
        const string EverythingLost =
            "its previous body, description and any keys this producer does not write are gone.";

        try
        {
            if (!OkfDocument.TryParse(File.ReadAllText(path), out var document, out _))
            {
                return ("it does not parse as an OKF concept, so nothing can be said about what wrote it", EverythingLost);
            }

            var existing = document.Frontmatter.Description;
            var loss = existing is { Length: > 0 } && string.Equals(existing, incoming, StringComparison.Ordinal)
                ? "its previous body and any keys this producer does not write are gone; its description is not,"
                    + " because the text this run is about to write is the same text."
                : EverythingLost;

            var source = document.Frontmatter.Get(DescriptionResolver.DescriptionSourceKey)?.AsDisplayString();
            return source is { Length: > 0 }
                ? ($"its `description_source` was `{source}`", loss)
                : ("it carried no `description_source`, so this producer had not written it", loss);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OkfException)
        {
            return ("it could not be read to say what wrote it", EverythingLost);
        }
    }

    /// <summary>
    /// The <c>description</c> of the staged document at <paramref name="path"/>, or
    /// <see langword="null"/> when it has none or cannot be read -- both of which
    /// <see cref="Overwrite"/> reads as "not the same text", which is the safe direction: it makes the
    /// note claim a loss rather than a survival it cannot demonstrate.
    /// </summary>
    private static string? DescriptionOf(string path)
    {
        try
        {
            return OkfDocument.TryParse(File.ReadAllText(path), out var document, out _)
                ? document.Frontmatter.Description
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OkfException)
        {
            return null;
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
    /// length never to touch. That is true of <b>every</b> run under a swap, whatever the policy, and
    /// it is why the swap was not taken. The property that actually needs to hold -- a run that fails
    /// while producing content leaves the bundle untouched -- is provided by the staging directory
    /// itself, not by the shape of the commit.</para>
    ///
    /// <para><b>What moving file by file buys, said exactly.</b> The moves alone cannot lose data: the
    /// worst interruption leaves a mix of new and old concepts, which the next run corrects. That is
    /// the whole claim, and it is a claim about the moves. It is <b>not</b> a claim about the commit
    /// under <see cref="WritePolicy.Reset"/>, whose first act is <see cref="ResetBundle"/> deleting
    /// the bundle outright -- the same window the swap has, entered deliberately, for the one policy
    /// whose contract is "throw this away and write it again". Under Reset the difference from a swap
    /// is that the window opens once per <c>--reset</c> instead of once per run of any kind; it is not
    /// that there is no window. <see cref="IBundleWriter.Write"/> states it as part of the
    /// interface.</para>
    ///
    /// <para><b>One thing staging gives up, stated so nobody discovers it later.</b>
    /// <c>BundleConceptWriter</c> serializes writes through a lock keyed on its bundle root, so two
    /// concurrent runs against one bundle used to interleave at concept granularity. They now write to
    /// two different staging roots and meet only here, where no lock applies. Two <c>generate</c> runs
    /// into the same <c>--out</c> were already unsafe -- they share one manifest and one index
    /// regeneration -- so this narrows nothing that was safe; it just means the last writer wins per
    /// file rather than per lock acquisition.</para>
    ///
    /// <para><b>Every destination is resolved through the filesystem before it is written, for the
    /// same reason the delete side is.</b> <c>File.Move(overwrite: true)</c> follows a symbolic link or
    /// junction exactly as <see cref="File.Delete(string)"/> does, and the destination here is built by
    /// joining a staged relative path onto <paramref name="outPath"/> -- string work, which
    /// <see cref="Path.GetFullPath(string)"/> cannot see through. A bundle carrying
    /// <c>code/x -&gt; ~/notes</c>, which a clone brings with it, turned a generated
    /// <c>code/x/report</c> into an overwrite of <c>~/notes/report.md</c>. The gate is
    /// <see cref="ResolveInsideRoot"/>, the same resolution the prune uses, and a destination that
    /// does not land inside the bundle is not written: the file is left in staging (where the
    /// <c>finally</c> discards it) and its path is returned to the caller, which records it as a
    /// write failure. A failure is the right shape rather than a note, because it already means both
    /// of the things that have to follow -- the id is dropped from the manifest this run writes, so it
    /// is never claimed as owned, and the run is disqualified from pruning.</para>
    ///
    /// <para><b>What runs after this, and gates itself.</b>
    /// <c>IndexGenerator.RegenerateIndexes</c> runs after the commit. An earlier round of this review
    /// recorded here that it walks the bundle with <c>Directory.EnumerateDirectories</c> and so writes
    /// an <c>index.md</c> through a junction; that was wrong. It collects with
    /// <c>Directory.GetFileSystemEntries</c> and skips reparse points before recursing, skips them
    /// again when listing a directory's children, and re-checks both the ancestor chain and the
    /// <c>index.md</c> node itself immediately before writing. The line references are in
    /// <see cref="Write"/>, beside the call.</para>
    /// </summary>
    /// <returns>The bundle-relative paths that were refused, sorted <see cref="StringComparer.Ordinal"/>; empty on an ordinary commit.</returns>
    private static IReadOnlyList<string> CommitStaging(string staging, string outPath)
    {
        var staged = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var root = ResolveRoot(outPath);
        var refused = new List<string>();

        foreach (var source in staged)
        {
            var relative = Path.GetRelativePath(staging, source);

            if (root is null)
            {
                refused.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
                continue;
            }

            // Joined onto `root`, not onto `outPath`, and that is a precondition rather than a
            // tidiness point. ResolveInsideRoot starts with Path.GetRelativePath(resolvedRoot,
            // candidate) and walks the result from resolvedRoot, so it only ever means what it says
            // when the candidate really is under the resolved root. TryResolveConceptFile and
            // RemoveEmptyDirectories both build their candidate from `root` for that reason; this
            // was the one caller that did not, and it is the caller whose `--out` the operator names.
            //
            // MEASURED, because the consequence is smaller than it looks and the honest size is worth
            // recording. With `--out` itself a junction, GetRelativePath returns an ABSOLUTE path
            // (cross-volume always; same-volume it comes back starting `..`), and Path.Combine then
            // discards the accumulated root the moment it meets a rooted segment like `C:` -- so the
            // walk restarts at the drive and climbs the whole absolute path. It still lands inside,
            // because it meets the same link again on the way down and resolves through it, so no run
            // was refused that should have succeeded. What it was doing instead was deciding
            // containment by resolving components with nothing to do with the bundle -- every
            // directory between the drive root and the link. Joining onto `root` confines the walk to
            // the concept-id segments, which is the only thing it was ever meant to inspect.
            var destination = Path.GetFullPath(Path.Combine(root, relative));

            // Resolved BEFORE the directories are created, not after: CreateDirectory follows a
            // reparse point too, so a check that ran afterwards would already have built the caller's
            // directory tree inside somebody else's.
            if (ResolveInsideRoot(root, destination) is null)
            {
                refused.Add(relative.Replace(Path.DirectorySeparatorChar, '/'));
                continue;
            }

            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(source, destination, overwrite: true);
        }

        return refused;
    }

    /// <summary>
    /// <see cref="WritePolicy.Reset"/>'s deletion, performed at the commit boundary rather than on the
    /// way in: everything before it is work in a staging directory the bundle cannot see, so a run that
    /// fails there leaves the bundle exactly as it was. The directory is recreated immediately, because
    /// a run with nothing to write must still leave an empty bundle behind rather than no bundle.
    /// </summary>
    private static void ResetBundle(string outPath)
    {
        if (Directory.Exists(outPath))
        {
            Directory.Delete(outPath, recursive: true);
        }

        Directory.CreateDirectory(outPath);
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
