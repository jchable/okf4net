// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using OKF4net.Internal;
using OKF4net.Yaml;

namespace OKF4net.Agents;

/// <summary>OKF bundle operations exposed as Microsoft Agent Framework function tools.</summary>
public sealed class OkfBundleTools
{
    private const string IndexFilename = "index.md";
    private const string LogFilename = "log.md";
    private const string NoneLine = "(none)";

    private const string SearchUsageMessage =
        "Usage: okf_search requires a non-empty query — one or more terms to match "
        + "(case-insensitive substring) against concept titles, descriptions, tags and "
        + "bodies. Example: okf_search(\"orders\").";

    private const string ChangesSinceUsageMessage =
        "Usage: okf_changes_since requires a valid ISO date (yyyy-MM-dd), inclusive. "
        + "Example: okf_changes_since(\"2026-01-01\").";

    /// <summary>
    /// Process-wide registry of one lock object per canonicalized bundle
    /// root, keyed by <see cref="Path.GetFullPath(string)"/> of the bundle
    /// root -- the SAME canonical form <see cref="IsWithinBundleRoot"/> and
    /// <see cref="HasReparsePointAncestor"/> resolve to, so two different
    /// spellings of the same directory (e.g. a trailing separator, or a
    /// relative vs. absolute path) still share one lock. Every
    /// <see cref="OkfBundleTools"/> instance constructed over the same
    /// bundle path -- not just the same instance -- ends up sharing the
    /// same lock object via <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/>
    /// in the constructor: a per-INSTANCE lock (the previous design) left
    /// two separate <see cref="OkfBundleTools"/> instances pointed at the
    /// same bundle directory free to race each other's
    /// <see cref="AppendToConceptAtomic"/>/<see cref="WriteConcept"/> calls,
    /// even though each instance's OWN calls were already serialized against
    /// themselves. <see cref="StringComparer.OrdinalIgnoreCase"/>, matching
    /// the ordinal-ignore-case comparisons <see cref="IsWithinBundleRoot"/>
    /// and the reserved-id check already use (Windows/macOS filesystems are
    /// typically case-insensitive). The registry grows by one small object
    /// per distinct bundle path for the process's lifetime -- bounded in
    /// practice by how many distinct bundle directories a process ever
    /// opens, and never removed (there is no matching "last instance for
    /// this path went away" signal to remove it on).
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> BundleLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guards <see cref="_bundle"/> and every write this class performs to
    /// disk. Agent hosts may invoke tool methods concurrently from multiple
    /// threads, so the lazy cache in <see cref="GetBundle"/> and the
    /// invalidation in <see cref="InvalidateBundle"/> must not race; the same
    /// lock also serializes <see cref="WriteConcept"/>, <see cref="AppendLog"/>,
    /// and <see cref="RegenerateIndexes"/>'s own read-modify-write sequences
    /// (each holds it around the read, the write, and its own cache
    /// invalidation), so two concurrent calls into the same tool can't
    /// interleave and lose one side's update.
    ///
    /// Obtained from the process-wide <see cref="BundleLocks"/> registry, so
    /// this guarantee extends to every <see cref="OkfBundleTools"/> instance
    /// constructed over the same canonicalized bundle root -- not just calls
    /// on THIS instance. It does NOT serialize writes across separate
    /// processes (e.g. two CLI invocations, or two server processes sharing
    /// a network path), and a C# lock cannot defend against a concurrent
    /// external actor mutating the bundle's files directly on disk -- see
    /// <see cref="ValidateConceptTarget"/>'s remarks for that separate,
    /// residual TOCTOU limitation. The per-instance <see cref="_bundle"/>
    /// CACHE deliberately stays instance-level (unaffected by this change):
    /// <see cref="AppendToConceptAtomic"/> always re-reads the concept's
    /// on-disk body under this lock rather than trusting any cache, so two
    /// instances having independent caches does not affect write
    /// correctness, only how eagerly each one's read-only calls see another
    /// instance's writes before their own next reload.
    /// </summary>
    private readonly object _bundleLock;

    private Bundle? _bundle;

    /// <summary>
    /// Creates the tool set rooted at <paramref name="bundleRoot"/>.
    /// </summary>
    /// <param name="bundleRoot">Path to the bundle's root directory.</param>
    /// <exception cref="ArgumentException"><paramref name="bundleRoot"/> does not exist.</exception>
    public OkfBundleTools(string bundleRoot)
    {
        if (!Directory.Exists(bundleRoot))
        {
            throw new ArgumentException($"bundle root does not exist: {bundleRoot}", nameof(bundleRoot));
        }

        BundleRoot = bundleRoot;

        // Canonicalize BEFORE looking up the shared lock so two different
        // spellings of the same bundle directory (e.g. with/without a
        // trailing separator) still resolve to the same registry entry --
        // the same Path.GetFullPath canonicalization IsWithinBundleRoot and
        // HasReparsePointAncestor already use for their own comparisons.
        var canonicalRoot = Path.GetFullPath(bundleRoot);
        _bundleLock = BundleLocks.GetOrAdd(canonicalRoot, static _ => new object());
    }

    /// <summary>The bundle's root directory, as passed to the constructor.</summary>
    public string BundleRoot { get; }

    /// <summary>
    /// The current UTC time, consulted by <see cref="AppendLog"/> to compute
    /// "today"'s ISO date heading. Defaults to <see cref="DateTime.UtcNow"/>;
    /// overridable so tests can pin the date deterministically. Internal: an
    /// implementation seam, not part of the tool's public surface.
    /// </summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Returns the loaded bundle, loading it from <see cref="BundleRoot"/> on
    /// first access and caching it thereafter until <see cref="InvalidateBundle"/>
    /// is called.
    /// </summary>
    internal Bundle GetBundle()
    {
        lock (_bundleLock)
        {
            return _bundle ??= Bundle.Load(BundleRoot);
        }
    }

    /// <summary>
    /// Drops the cached bundle so the next <see cref="GetBundle"/> call
    /// reloads it from disk. Call after any write to <see cref="BundleRoot"/>.
    /// </summary>
    internal void InvalidateBundle()
    {
        lock (_bundleLock)
        {
            _bundle = null;
        }
    }

    /// <summary>
    /// All nine OKF tools as Agent Framework <see cref="AIFunction"/>s (via
    /// <see cref="AITool"/>), ready for <c>AsAIAgent(tools: ...)</c>. Each
    /// call returns a fresh list of freshly-created <see cref="AIFunction"/>
    /// instances bound to this <see cref="OkfBundleTools"/> — invoking one
    /// invokes the corresponding public method above, including its
    /// never-throw behavior.
    ///
    /// Names are explicit snake_case (the default would be the C# method
    /// name); descriptions are omitted here and instead derived by
    /// <see cref="AIFunctionFactory"/> from each method's own
    /// <see cref="DescriptionAttribute"/> — the single source of truth, so
    /// the two can never drift apart. The order is stable: read → browse →
    /// graph → search → write → append → regenerate → validate →
    /// changes-since.
    /// </summary>
    public IList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(ReadConcept, "okf_read_concept"),
        AIFunctionFactory.Create(Browse, "okf_browse"),
        AIFunctionFactory.Create(Graph, "okf_graph"),
        AIFunctionFactory.Create(Search, "okf_search"),
        AIFunctionFactory.Create(WriteConcept, "okf_write_concept"),
        AIFunctionFactory.Create(AppendLog, "okf_append_log"),
        AIFunctionFactory.Create(RegenerateIndexes, "okf_regenerate_indexes"),
        AIFunctionFactory.Create(ValidateBundle, "okf_validate_bundle"),
        AIFunctionFactory.Create(ChangesSince, "okf_changes_since"),
    ];

    /// <summary>
    /// Reads one concept: its frontmatter, body, outgoing links, and
    /// backlinks, rendered as agent-friendly markdown. Never throws for
    /// expected errors (a null/blank, malformed, or unknown concept id, or a
    /// bundle that fails to (re)load) — those are reported as a plain-text
    /// message instead.
    /// </summary>
    /// <param name="conceptId">The concept id, e.g. <c>tables/orders</c>.</param>
    [Description("Read one concept from the OKF bundle: its frontmatter, body, outgoing links and backlinks.")]
    public string ReadConcept([Description("The concept id, e.g. 'tables/orders'.")] string conceptId)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return ConceptNotFoundMessage(conceptId ?? string.Empty);
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var bundle = GetBundle();
            if (!ConceptId.TryParse(conceptId, out var id) || bundle.Get(id) is not { } concept)
            {
                return ConceptNotFoundMessage(conceptId);
            }

            var sb = new StringBuilder();
            sb.Append("# ").Append(concept.Document.Frontmatter.Title ?? concept.Id.ToString()).Append('\n').Append('\n');
            AppendFrontmatterBlock(sb, concept.Document.Frontmatter);
            sb.Append(concept.Document.Body.TrimEnd('\n')).Append('\n').Append('\n');
            AppendSection(sb, "Outgoing links", FormatOutgoingLinks(bundle.LinksFrom(id)));
            sb.Append('\n');
            AppendSection(sb, "Backlinks", FormatBacklinks(bundle.Backlinks(id)));

            return sb.ToString();
        });
    }

    /// <summary>
    /// Browses the bundle via its <c>index.md</c> files (progressive
    /// disclosure): returns the raw content of the requested directory's
    /// index if one exists, otherwise a generated listing of the concepts
    /// and subdirectories at that level. Never throws for expected errors
    /// (an invalid, traversing, or out-of-bundle path, or a bundle that
    /// fails to (re)load) — those are reported as a plain-text message
    /// instead.
    /// </summary>
    /// <param name="path">Optional directory path within the bundle, e.g. <c>tables</c>. Omit to list the bundle root.</param>
    [Description("Browse the bundle via its index files (progressive disclosure). Without a path, lists the bundle root.")]
    public string Browse([Description("Optional directory path within the bundle, e.g. 'tables'.")] string? path = null)
    {
        if (path is not null && path.Contains('\0'))
        {
            return "Error: invalid path — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var relPath = path?.Trim() ?? string.Empty;
            var segments = relPath.Length == 0
                ? []
                : relPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

            if (segments.Any(s => s == "..") || Path.IsPathRooted(relPath))
            {
                return $"Error: invalid path '{path}' — '..' segments and absolute paths are not allowed.";
            }

            var bundle = GetBundle();
            var fullDir = segments.Length == 0 ? bundle.Root : Path.Combine([bundle.Root, .. segments]);

            if (!IsWithinBundleRoot(bundle.Root, fullDir)
                || !Directory.Exists(fullDir)
                || HasReparsePointAncestor(bundle.Root, fullDir))
            {
                return $"Error: path '{path}' not found in the bundle. Use okf_browse to list available directories.";
            }

            var indexPath = Path.Combine(fullDir, IndexFilename);
            if (File.Exists(indexPath))
            {
                return File.ReadAllText(indexPath);
            }

            return BuildLevelListing(bundle, segments, relPath);
        });
    }

    /// <summary>
    /// Inspects the cross-link graph: bundle-wide stats, or (with a concept
    /// id) that concept's outgoing links, backlinks, and broken links.
    /// Never throws for expected errors (an unknown concept id, or a bundle
    /// that fails to (re)load) — reported as a plain-text message instead.
    /// </summary>
    /// <param name="conceptId">Optional concept id to focus on.</param>
    [Description("Inspect the cross-link graph. With a concept id: its outgoing links, backlinks and broken links. Without: bundle-wide stats.")]
    public string Graph([Description("Optional concept id to focus on.")] string? conceptId = null)
    {
        if (conceptId is not null && conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var bundle = GetBundle();

            if (string.IsNullOrWhiteSpace(conceptId))
            {
                return BuildBundleGraphSummary(bundle);
            }

            if (!ConceptId.TryParse(conceptId, out var id) || bundle.Get(id) is null)
            {
                return ConceptNotFoundMessage(conceptId);
            }

            return BuildConceptGraphDetail(bundle, id);
        });
    }

    /// <summary>
    /// Full-text search across concept titles, descriptions, tags and
    /// bodies. Never throws for expected errors (a null/blank query, a
    /// query or tag containing a null character, or a bundle that fails to
    /// (re)load) — those are reported as a plain-text message instead.
    ///
    /// The query is split into terms on whitespace; each term is matched as
    /// an <see cref="StringComparison.OrdinalIgnoreCase"/> substring. A
    /// concept's score is the sum, over all terms, of the weights of every
    /// field the term is found in: title ×3, tags/description ×2, body ×1.
    /// Concepts scoring zero are dropped. Results are sorted by descending
    /// score, then ascending concept id (ordinal), bounded to the top 20
    /// with the total match count reported alongside.
    /// </summary>
    /// <param name="query">The search query (case-insensitive substring terms).</param>
    /// <param name="tag">Optional tag filter: only concepts carrying this tag.</param>
    [Description("Full-text search across concept titles, descriptions, tags and bodies. Returns matching concept ids ranked by relevance.")]
    public string Search(
        [Description("The search query (case-insensitive substring terms).")] string query,
        [Description("Optional tag filter: only concepts carrying this tag.")] string? tag = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return SearchUsageMessage;
        }

        if (query.Contains('\0'))
        {
            return "Error: invalid query — it must not contain a null character.";
        }

        if (tag is not null && tag.Contains('\0'))
        {
            return "Error: invalid tag — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0)
            {
                return SearchUsageMessage;
            }

            var effectiveTag = string.IsNullOrWhiteSpace(tag) ? null : tag;
            var scored = ScoreConceptsFor(query, tag);

            if (scored.Count == 0)
            {
                return effectiveTag is null
                    ? $"No results for query '{query}'."
                    : $"No results for query '{query}' with tag '{effectiveTag}'.";
            }

            return FormatSearchResults(query, effectiveTag, terms, scored);
        });
    }

    /// <summary>
    /// Scores every candidate concept against <paramref name="query"/>
    /// (optionally restricted to concepts carrying <paramref name="tag"/>):
    /// the shared seam behind <see cref="Search"/> and
    /// <see cref="OKF4net.Agents.OkfContextProvider"/>'s progressive
    /// disclosure. Same candidate selection, <see cref="ScoreConcept"/>
    /// weights, score&gt;0 filter, and ordering (descending score, then
    /// ascending concept id) that <see cref="Search"/> used inline before
    /// this was extracted, so the two can never drift apart. Assumes
    /// <paramref name="query"/> has already been validated non-null/blank by
    /// the caller (mirroring <see cref="Search"/>'s own precondition); a
    /// query that splits into zero terms yields an empty result rather than
    /// throwing.
    /// </summary>
    internal IReadOnlyList<(Concept Concept, int Score)> ScoreConceptsFor(string query, string? tag = null)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        var bundle = GetBundle();
        var effectiveTag = string.IsNullOrWhiteSpace(tag) ? null : tag;

        var candidates = effectiveTag is null
            ? bundle.Concepts
            : bundle.Concepts.Where(c => c.Document.Frontmatter.Tags.Any(t => string.Equals(t, effectiveTag, StringComparison.OrdinalIgnoreCase)));

        return candidates
            .Select(c => (Concept: c, Score: ScoreConcept(c, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Concept.Id)
            .ToList();
    }

    /// <summary>
    /// Creates or updates one concept document. Producer-grade validation
    /// (<see cref="OkfDocument.Validate"/>: non-empty <c>type</c>,
    /// <c>title</c>, <c>description</c> and <c>timestamp</c>) runs BEFORE
    /// anything is written — on failure, the file on disk (if any) is left
    /// untouched. Never throws for expected errors (a null/blank/malformed
    /// concept id, a reserved id, invalid frontmatter YAML, or a failed
    /// validation) — those are reported as a plain-text message instead.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatterYaml">Frontmatter as <c>key: value</c> lines (the same YAML subset used inside a document's frontmatter block, without the <c>---</c> delimiters).</param>
    /// <param name="body">The markdown body.</param>
    [Description("Create or update a concept document. The frontmatter must contain non-empty type, title, description and timestamp (producer-grade validation is enforced before writing).")]
    public string WriteConcept(
        [Description("The concept id (path without .md), e.g. 'tables/refunds'.")] string conceptId,
        [Description("Frontmatter as 'key: value' lines (YAML subset).")] string frontmatterYaml,
        [Description("The markdown body.")] string body)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYaml is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYaml.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var targetError = ValidateConceptTarget(conceptId, out var target);
            if (targetError is not null)
            {
                return targetError;
            }

            var (content, buildError) = BuildValidatedContent(frontmatterYaml, body);
            if (buildError is not null)
            {
                return buildError;
            }

            // Serialized under _bundleLock (shared with AppendLog,
            // RegenerateIndexes, and AppendToConceptAtomic -- and, since
            // _bundleLock is now obtained from the process-wide BundleLocks
            // registry, with every OTHER OkfBundleTools instance pointed at
            // this same canonicalized bundle root, not just this instance)
            // so concurrent writers can't interleave an existence check with
            // another writer's write, and so the cache invalidation below is
            // atomic with the write it follows.
            lock (_bundleLock)
            {
                return WriteValidatedContentLocked(target.Id, target.TargetPath, content!);
            }
        });
    }

    /// <summary>
    /// Atomically reads, transforms, and rewrites one concept's body under
    /// <see cref="_bundleLock"/> — the seam <see cref="OKF4net.Agents.OkfContextProvider.CaptureMemory"/>
    /// uses to close the same-day memory-capture race (E2): before this
    /// existed, a caller that read a concept's body via <see cref="GetBundle"/>,
    /// built a new body from it OUTSIDE any lock, then called the plain
    /// <see cref="WriteConcept"/>, could have that read/build/write sequence
    /// interleave with a concurrent caller doing the same for the same
    /// concept — both read the same "before" body, and the second write
    /// silently clobbers the first's change (a lost update), even if some
    /// OTHER, already-locked write (e.g. <see cref="AppendLog"/>) faithfully
    /// recorded both calls, producing a count divergence between the two.
    /// Here, the read of the concept's CURRENT on-disk body, the caller's
    /// <paramref name="buildBody"/> transform, and the validated write all
    /// happen inside one unbroken hold of <see cref="_bundleLock"/>, so two
    /// concurrent calls for the same concept id can never interleave: the
    /// second call's read always observes the first call's completed write.
    /// Because <see cref="_bundleLock"/> is obtained from the process-wide
    /// <c>BundleLocks</c> registry (keyed by the canonicalized bundle root),
    /// this holds for two concurrent calls on the SAME <see cref="OkfBundleTools"/>
    /// instance AND for two concurrent calls on two SEPARATE instances
    /// constructed over the same bundle path -- but only within one process:
    /// it does not serialize a second process writing the same bundle path,
    /// and a C# lock cannot stop a concurrent external actor from mutating
    /// the target file/its ancestor directories on disk out from under this
    /// method (see <see cref="ValidateConceptTarget"/>'s remarks for that
    /// separate, residual check-then-write limitation, which this lock does
    /// not close). Reuses the exact same target validation (<see cref="ValidateConceptTarget"/>),
    /// producer-grade validation and serialization (<see cref="BuildValidatedContent"/>),
    /// and write/cache-invalidation (<see cref="WriteValidatedContentLocked"/>)
    /// steps <see cref="WriteConcept"/> itself uses — this is a locked
    /// read-modify-write wrapped AROUND that same core, not a divergent
    /// second write path. <see langword="internal"/>: a narrow seam for
    /// same-process callers that need atomicity, not part of the tool's
    /// public agent-facing surface.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>memory/2026-07-24</c>.</param>
    /// <param name="frontmatterYamlIfCreating">
    /// Frontmatter used only when the concept does not yet exist. When it
    /// already exists, its own current frontmatter is re-read and
    /// re-serialized unchanged (mirroring how a caller that read-then-called
    /// <see cref="WriteConcept"/> would carry it forward) and this parameter
    /// is ignored.
    /// </param>
    /// <param name="buildBody">
    /// Given the concept's current body (<see langword="null"/> if it does
    /// not yet exist), returns the full new body to write. Invoked exactly
    /// once, inside the lock, against the freshly re-read current body —
    /// never a caller's own stale, pre-lock snapshot.
    /// </param>
    /// <returns>
    /// The same style of result text as <see cref="WriteConcept"/> (a
    /// <c>Written ...</c> confirmation) or an <c>Error: ...</c> message;
    /// never throws.
    /// </returns>
    internal string AppendToConceptAtomic(
        string conceptId,
        string frontmatterYamlIfCreating,
        Func<string?, string> buildBody)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYamlIfCreating is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYamlIfCreating.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var targetError = ValidateConceptTarget(conceptId, out var target);
            if (targetError is not null)
            {
                return targetError;
            }

            // The read of the current body, the caller's transform, and the
            // validated write all happen inside this ONE lock acquisition —
            // never released and reacquired in between — which is what makes
            // the whole read-modify-write atomic against a concurrent
            // WriteConcept/AppendToConceptAtomic call for the same concept.
            lock (_bundleLock)
            {
                string frontmatterYaml;
                string? currentBody;
                if (File.Exists(target.TargetPath))
                {
                    // Strict UTF-8, matching AppendLog's own existing-file
                    // read: a non-UTF-8 concept file throws
                    // DecoderFallbackException (caught by RunTool below)
                    // rather than being silently re-decoded and rewritten.
                    var text = OkfEncodings.Strict.GetString(File.ReadAllBytes(target.TargetPath));
                    // Fail-closed: if the existing concept has malformed
                    // frontmatter (hand-edited, or a prior partial/crashed
                    // write), OkfDocument.Parse throws DocumentParseException
                    // (caught by RunTool -> Error text, LastMemoryError set) so
                    // this append is dropped rather than overwriting a possibly
                    // important file. This is stricter than the old permissive
                    // Bundle.Get path, which treated an unparseable file as
                    // absent and silently recreated it.
                    var existingDoc = OkfDocument.Parse(text);
                    frontmatterYaml = existingDoc.Frontmatter.AsMapping().ToYamlString();
                    currentBody = existingDoc.Body;
                }
                else
                {
                    frontmatterYaml = frontmatterYamlIfCreating;
                    currentBody = null;
                }

                var newBody = buildBody(currentBody);
                if (newBody is null)
                {
                    return "Error: body must not be null.";
                }

                if (newBody.Contains('\0'))
                {
                    return "Error: invalid body — it must not contain a null character.";
                }

                var (content, buildError) = BuildValidatedContent(frontmatterYaml, newBody);
                if (buildError is not null)
                {
                    return buildError;
                }

                return WriteValidatedContentLocked(target.Id, target.TargetPath, content!);
            }
        });
    }

    /// <summary>A validated concept id and the absolute path it resolves to, produced by <see cref="ValidateConceptTarget"/>.</summary>
    private readonly record struct ConceptTarget(ConceptId Id, string TargetPath);

    /// <summary>
    /// Validates <paramref name="conceptId"/> (parseable, not the reserved
    /// <c>index</c>/<c>log</c> name) and the filesystem path it resolves to
    /// (within the bundle root; no reparse point among its parent
    /// directories or at the target itself) — shared by <see cref="WriteConcept"/>
    /// and <see cref="AppendToConceptAtomic"/> so the two can never diverge
    /// on what counts as a valid write target. Pure: performs no I/O beyond
    /// the reparse-point/existence checks themselves, and does not touch
    /// <see cref="_bundleLock"/> or the bundle cache.
    /// </summary>
    /// <remarks>
    /// <b>Scope of the reparse-point guarantee (read this before assuming
    /// "no reparse point" holds for the whole write, not just this check):</b>
    /// this method is a point-in-time check, not an ongoing guarantee — it
    /// rejects a reparse point that is PRESENT AT THE MOMENT THIS METHOD
    /// RUNS. The actual write happens later, in <see cref="WriteValidatedContentLocked"/>
    /// (after YAML parsing and producer validation in between), so this is a
    /// classic check-then-write (TOCTOU): a concurrent local actor able to
    /// replace a path component with a symlink/junction between this check
    /// and that later write is not stopped by this method, and the <see cref="_bundleLock"/>
    /// this class otherwise relies on for atomicity is a C# in-process lock —
    /// it has no effect on what a separate, unsynchronized filesystem
    /// mutation can do to the same paths. <see cref="WriteValidatedContentLocked"/>
    /// re-runs the same two checks immediately before its actual
    /// <see cref="File.WriteAllText(string, string)"/> call, still inside the
    /// same lock hold, which narrows this window considerably but — since
    /// .NET has no portable "open/write only if not a symlink, atomically" —
    /// cannot close it completely; a substitution racing that final,
    /// immediately-preceding check is a residual, documented limitation, not
    /// something this design claims to eliminate. The threat model this
    /// guard IS effective against: an actor who does not already have write
    /// access to the bundle tree planting a reparse point ahead of time (or
    /// a stale one left over from a previous, unrelated operation) — since an
    /// actor who DOES already have concurrent write access to the bundle
    /// tree could corrupt its content directly and has no need to race this
    /// check at all.
    /// </remarks>
    /// <returns>
    /// <see langword="null"/> and a populated <paramref name="target"/> on
    /// success; otherwise the <c>Error: ...</c> message to return to the
    /// caller (and <paramref name="target"/> is <see langword="default"/>).
    /// </returns>
    private string? ValidateConceptTarget(string conceptId, out ConceptTarget target)
    {
        target = default;

        if (!ConceptId.TryParse(conceptId, out var id))
        {
            return $"Error: invalid concept id '{conceptId}'. Concept ids are '/'-separated "
                + "segments matching [A-Za-z0-9_][A-Za-z0-9_.-]*.";
        }

        if (string.Equals(id.Name, "index", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id.Name, "log", StringComparison.OrdinalIgnoreCase))
        {
            return $"Error: '{id}' is a reserved concept id — the last segment must not be "
                + "'index' or 'log' in any casing (these would collide with the bundle's "
                + "index.md/log.md files on case-insensitive filesystems).";
        }

        var targetPath = id.ToPath(BundleRoot);

        // Belt and braces alongside ConceptId's own segment validation
        // (which already forbids '..' and '/' inside a segment): the same
        // defense-in-depth check Browse uses before touching disk.
        if (!IsWithinBundleRoot(BundleRoot, targetPath))
        {
            return $"Error: '{id}' resolves outside the bundle root.";
        }

        // Reject a reparse point (symlink/junction) anywhere between the
        // bundle root and the target's parent directory: the lexical check
        // above would happily accept "tables/refunds" even if "tables" is a
        // junction pointing outside the bundle -- the OS follows it when
        // Directory.CreateDirectory/File.WriteAllText actually touch disk.
        // Same guard Browse uses.
        var targetParentDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetParentDir) && HasReparsePointAncestor(BundleRoot, targetParentDir))
        {
            return $"Error: '{id}' resolves through a reparse point (symlink/junction) inside the bundle, which is not allowed.";
        }

        // Also reject the target FILE node itself being a reparse point (a
        // planted file symlink at e.g. tables/x.md pointing at an external
        // file): HasReparsePointAncestor above only walks directory
        // ANCESTORS of targetPath, it never inspects targetPath itself, so
        // an existing symlinked concept file would otherwise sail through
        // both checks and a later read/write would follow the link.
        if (ReparsePoints.IsReparsePoint(targetPath))
        {
            return $"Error: '{id}' is a reparse point (symlink/junction), not a regular file -- refusing to overwrite it.";
        }

        target = new ConceptTarget(id, targetPath);
        return null;
    }

    /// <summary>
    /// Parses <paramref name="frontmatterYaml"/>, builds and validates the
    /// resulting <see cref="OkfDocument"/> against <paramref name="body"/>,
    /// and serializes it — the exact producer-grade validation
    /// <see cref="WriteConcept"/> performed inline before this was
    /// extracted, now shared verbatim with <see cref="AppendToConceptAtomic"/>
    /// so the two can never validate divergently. Throws
    /// <see cref="Yaml.YamlParseException"/> (malformed frontmatter YAML) or
    /// <see cref="DocumentValidationException"/> (failed producer
    /// validation) — both caught by the caller's <see cref="RunTool"/>
    /// wrapper — rather than returning an error for those two cases; only
    /// "frontmatter parses but isn't a mapping" is reported via the
    /// returned <c>Error</c> string, matching the original inline code.
    /// </summary>
    private static (string? Content, string? Error) BuildValidatedContent(string frontmatterYaml, string body)
    {
        // Throws YamlParseException (line-tagged message) on malformed input;
        // caught by RunTool's catch-all, before anything is written.
        var yaml = YamlValue.Parse(frontmatterYaml);
        Frontmatter? frontmatter = yaml switch
        {
            YamlNull => new Frontmatter(),
            YamlMapping map => Frontmatter.FromMapping(map),
            _ => null,
        };

        if (frontmatter is null)
        {
            return (null, "Error: frontmatter must be a YAML mapping of 'key: value' lines, not a list or scalar.");
        }

        var doc = new OkfDocument(frontmatter, body);

        // Strict producer validation BEFORE any write. On failure this
        // throws DocumentValidationException (message lists MissingKeys),
        // caught by RunTool -- nothing is written for a failed write.
        doc.Validate();

        return (doc.Serialize(), null);
    }

    /// <summary>
    /// Test-only hook, invoked (if set) after <see cref="Directory.CreateDirectory(string)"/>
    /// but immediately before the late reparse-point re-check in
    /// <see cref="WriteValidatedContentLocked"/>. Lets a test deterministically
    /// simulate a filesystem substitution racing the final write -- e.g.
    /// deleting the just-created parent directory and replacing it with a
    /// junction to an external directory -- at exactly the point such a race
    /// would need to land, instead of relying on real (flaky, unreliable)
    /// thread timing. <see langword="internal"/>, always <see langword="null"/>
    /// outside tests, so it has zero effect on production behavior.
    /// </summary>
    internal Action? BeforeLateReparseCheckForTest { get; set; }

    /// <summary>
    /// Writes already-validated <paramref name="content"/> to <paramref name="targetPath"/>
    /// and invalidates the bundle cache. CALLER MUST already hold
    /// <see cref="_bundleLock"/> — this method does not acquire it itself,
    /// so that <see cref="AppendToConceptAtomic"/> can enclose its own
    /// preceding read-and-transform in the SAME lock acquisition as this
    /// write (a nested/second acquisition here would either reintroduce the
    /// exact gap this seam exists to close, or -- if <see cref="_bundleLock"/>
    /// were ever changed to a non-reentrant primitive -- deadlock). Shared
    /// verbatim by <see cref="WriteConcept"/> (which wraps a single call to
    /// this in its own <c>lock (_bundleLock)</c>) and
    /// <see cref="AppendToConceptAtomic"/>.
    /// </summary>
    /// <remarks>
    /// Defense-in-depth against the check-then-write gap documented on
    /// <see cref="ValidateConceptTarget"/>: immediately before the actual
    /// <see cref="File.WriteAllText(string, string, Encoding)"/> call below,
    /// this method re-runs the SAME two reparse-point checks
    /// <see cref="ValidateConceptTarget"/> already ran earlier (a reparse
    /// point among <paramref name="targetPath"/>'s parent directories, or at
    /// <paramref name="targetPath"/> itself), still inside the caller's hold
    /// of <see cref="_bundleLock"/>. This narrows the window a concurrent
    /// local filesystem substitution would need to land in — from "anywhere
    /// between validation and the write" down to "between this re-check and
    /// the write two lines later" — but does NOT close it: .NET has no
    /// portable API to open/write a file "only if not currently a symlink"
    /// atomically, so a substitution racing this exact re-check is still
    /// possible in principle. Best-effort, not a guarantee; see
    /// <see cref="ValidateConceptTarget"/>'s remarks for the full threat
    /// model this is (and is not) meant to defend against.
    /// </remarks>
    private string WriteValidatedContentLocked(ConceptId id, string targetPath, string content)
    {
        var existed = File.Exists(targetPath);

        var parentDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        BeforeLateReparseCheckForTest?.Invoke();

        // Late, best-effort re-check -- see this method's <remarks> and
        // ValidateConceptTarget's <remarks> for exactly what this does and
        // does not close.
        if ((!string.IsNullOrEmpty(parentDir) && HasReparsePointAncestor(BundleRoot, parentDir))
            || ReparsePoints.IsReparsePoint(targetPath))
        {
            return $"Error: '{id}' resolves through a reparse point (symlink/junction) inside the bundle, which is not allowed.";
        }

        File.WriteAllText(targetPath, content, OkfEncodings.NoBom);
        _bundle = null;

        var byteCount = OkfEncodings.NoBom.GetByteCount(content);
        var status = existed ? "updated" : "new";
        return $"Written {id} ({status}, {byteCount} bytes). Remember to run okf_regenerate_indexes.";
    }

    /// <summary>
    /// Appends one entry to the bundle root's <c>log.md</c> under today's
    /// (UTC) ISO date, creating the file if it does not yet exist. If a
    /// heading for today's date already exists, the entry is appended to the
    /// end of that day's entries (days are newest-first by convention (§7),
    /// but entries within a day stay chronological). The read-modify-write is
    /// serialized under <see cref="_bundleLock"/> (shared with
    /// <see cref="WriteConcept"/> and <see cref="RegenerateIndexes"/>) so
    /// concurrent calls can't lose an update to each other. The existing file,
    /// if any, is read with the same strict-UTF-8 decoding <see cref="ChangesSince"/>
    /// uses, then re-rendered through <see cref="ChangeLog.ToMarkdown"/> — the
    /// strict §7 model — so any non-conforming prose or comments in a
    /// hand-authored <c>log.md</c> are not preserved. Never throws for
    /// expected errors (a null/blank/embedded-null <paramref name="kind"/> or
    /// <paramref name="text"/>, or a <c>log.md</c> that fails strict UTF-8
    /// decoding) — those are reported as a plain-text message instead.
    /// </summary>
    /// <param name="kind">Entry kind, e.g. <c>Update</c> or <c>Creation</c>.</param>
    /// <param name="text">The entry text.</param>
    [Description("Append an entry to the bundle root log.md under today's date (ISO). Note: log.md is re-rendered through the strict §7 model, so non-conforming prose or comments in a hand-authored log.md are not preserved.")]
    public string AppendLog(
        [Description("Entry kind, e.g. 'Update' or 'Creation'.")] string kind,
        [Description("The entry text.")] string text)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return "Error: invalid kind — it must not be empty.";
        }

        if (kind.Contains('\0'))
        {
            return "Error: invalid kind — it must not contain a null character.";
        }

        if (kind.Contains('\n') || kind.Contains('\r'))
        {
            return "Error: invalid kind — it must not contain a line break (this would let it "
                + "forge fake '## date' or '* entry' lines in log.md).";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "Error: invalid text — it must not be empty.";
        }

        if (text.Contains('\0'))
        {
            return "Error: invalid text — it must not contain a null character.";
        }

        if (text.Contains('\n') || text.Contains('\r'))
        {
            return "Error: invalid text — it must not contain a line break (this would let it "
                + "forge fake '## date' or '* entry' lines in log.md).";
        }

        return RunTool(() =>
        {
            var logPath = Path.Combine(BundleRoot, LogFilename);

            // Reject log.md itself being a reparse point (symlink/junction),
            // e.g. a planted file symlink at bundleRoot/log.md pointing at an
            // external file: File.Exists/ReadAllBytes/WriteAllText below all
            // follow it, so without this check AppendLog would silently
            // overwrite whatever external file it points at. log.md always
            // lives directly at BundleRoot, so its only directory ancestor is
            // BundleRoot itself -- HasReparsePointAncestor's walk stops there
            // immediately without checking anything, which is why the file
            // node itself (not its ancestor chain) is the check that matters
            // here; both are included for the same defense-in-depth shape as
            // WriteConcept's guard.
            if (ReparsePoints.IsReparsePoint(logPath) || HasReparsePointAncestor(BundleRoot, BundleRoot))
            {
                return "Error: log.md is a reparse point (symlink/junction), not a regular file -- refusing to write through it.";
            }

            var today = UtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var entry = new LogEntry(kind.Trim(), text.Trim());

            // Serialized under _bundleLock (shared with WriteConcept and
            // RegenerateIndexes): without it, two concurrent AppendLog calls
            // could both read the same "before" text, each append their own
            // entry to it, and the second write would silently clobber the
            // first (a lost update). Locking the whole read-modify-write
            // makes it atomic.
            lock (_bundleLock)
            {
                // Strict UTF-8, matching ChangesSince's AppendLogFileChanges:
                // a non-UTF-8 log.md throws DecoderFallbackException (caught
                // by RunTool below) instead of being silently re-decoded with
                // U+FFFD replacement characters and then rewritten that way.
                var existingText = File.Exists(logPath)
                    ? OkfEncodings.Strict.GetString(File.ReadAllBytes(logPath))
                    : string.Empty;

                // ChangeLog.Parse is permissive (never throws); used here only to
                // locate today's day (if any) among the existing entries.
                var changeLog = ChangeLog.Parse(existingText);

                var days = changeLog.Days.ToList();
                var dayIndex = days.FindIndex(d => string.Equals(d.Date, today, StringComparison.Ordinal));
                if (dayIndex >= 0)
                {
                    var day = days[dayIndex];
                    days[dayIndex] = day with { Entries = [.. day.Entries, entry] };
                }
                else
                {
                    // New LogDay at the head: days are newest-first (§7).
                    days.Insert(0, new LogDay(today, [entry]));
                }

                File.WriteAllText(logPath, new ChangeLog(changeLog.Title, days).ToMarkdown(), OkfEncodings.NoBom);
                _bundle = null;
            }

            return $"Appended a '{kind}' entry under {today} in log.md.";
        });
    }

    /// <summary>
    /// Regenerates every <c>index.md</c> in the bundle (progressive
    /// disclosure listings). The regeneration and cache invalidation are
    /// serialized under <see cref="_bundleLock"/> (shared with
    /// <see cref="WriteConcept"/> and <see cref="AppendLog"/>) so a
    /// concurrent write can't be missed by (or interleave with) this pass.
    /// Never throws for expected errors (a bundle root that disappeared out
    /// from under it) — reported as a plain-text message instead.
    /// </summary>
    [Description("Regenerate every index.md in the bundle (progressive-disclosure listings). Run after adding or changing concepts.")]
    public string RegenerateIndexes()
    {
        return RunTool(() =>
        {
            IReadOnlyList<string> written;
            lock (_bundleLock)
            {
                written = IndexGenerator.RegenerateIndexes(BundleRoot);
                _bundle = null;
            }

            if (written.Count == 0)
            {
                return "No index.md files were regenerated (empty bundle?).";
            }

            var relative = written
                .Select(p => Path.GetRelativePath(BundleRoot, p).Replace('\\', '/'))
                .ToList();

            var sb = new StringBuilder();
            sb.Append("Regenerated ").Append(relative.Count).Append(" index file(s):").Append('\n');
            foreach (var rel in relative)
            {
                sb.Append("- ").Append(rel).Append('\n');
            }

            return sb.ToString();
        });
    }

    /// <summary>
    /// Validates the bundle against OKF v0.1 conformance (§9) and renders the
    /// report the same way the CLI's <c>validate</c> command does: one line
    /// per <see cref="Diagnostic"/> (via its own <see cref="Diagnostic.ToString"/>),
    /// then a summary line with the concept/error/warning/info counts and a
    /// conformant ✓/✗ verdict. Never throws for expected errors (a bundle
    /// that fails to (re)load) — reported as a plain-text message instead.
    /// </summary>
    [Description("Validate the bundle against OKF v0.1 conformance (§9). Returns the diagnostics report.")]
    public string ValidateBundle()
    {
        return RunTool(() =>
        {
            var bundle = GetBundle();
            var report = BundleValidator.Validate(bundle);

            var sb = new StringBuilder();
            foreach (var diagnostic in report.Diagnostics)
            {
                sb.Append(diagnostic).Append('\n');
            }

            var errors = report.ErrorCount;
            var warnings = report.WarningCount;
            var infos = report.Of(Severity.Info).Count();
            sb.Append('\n')
                .Append(bundle.Count).Append(" concept(s); ")
                .Append(errors).Append(" error(s), ")
                .Append(warnings).Append(" warning(s), ")
                .Append(infos).Append(" info.").Append('\n');

            sb.Append(report.IsConformant
                ? $"✓ conformant with OKF v{OkfSpec.Version}"
                : $"✗ not conformant with OKF v{OkfSpec.Version}").Append('\n');

            return sb.ToString();
        });
    }

    /// <summary>
    /// Summarizes bundle changes since a given ISO date (inclusive),
    /// aggregated across every <c>log.md</c> in the bundle (<see cref="Bundle.LogFiles"/>).
    /// Each log is parsed with <see cref="ChangeLog.Parse"/>, filtered to the
    /// <see cref="LogDay"/>s whose <see cref="LogDay.Date"/> is a valid ISO
    /// date (<see cref="ChangeLog.IsIsoDate"/>) greater than or equal to
    /// <paramref name="sinceDate"/> (ordinal string comparison — sufficient
    /// for well-formed ISO dates; non-ISO headings, e.g. a stray
    /// <c>## Notes</c> section, are excluded rather than risk a bogus
    /// ordinal comparison — <see cref="BundleValidator"/> is what reports
    /// those as diagnostics), and rendered newest-first, grouped by the log
    /// file's path relative to the bundle root ('/' separators). A log file
    /// that fails strict UTF-8 decoding is skipped with a note line rather
    /// than aborting the whole report (mirroring <see cref="BundleValidator"/>'s
    /// permissive handling of reserved files); that note is preserved even
    /// when no other log contributes matching days. Never throws for
    /// expected errors (a null/blank/invalid date, or a bundle that fails to
    /// (re)load) — those are reported as a plain-text message instead.
    /// </summary>
    /// <param name="sinceDate">ISO date (<c>yyyy-MM-dd</c>), inclusive.</param>
    [Description("Summarize bundle changes since a given ISO date, aggregated from every log.md in the bundle.")]
    public string ChangesSince([Description("ISO date (yyyy-MM-dd), inclusive.")] string sinceDate)
    {
        if (string.IsNullOrWhiteSpace(sinceDate))
        {
            return ChangesSinceUsageMessage;
        }

        if (sinceDate.Contains('\0'))
        {
            return "Error: invalid date — it must not contain a null character.";
        }

        var date = sinceDate.Trim();
        if (!ChangeLog.IsIsoDate(date))
        {
            return ChangesSinceUsageMessage;
        }

        return RunTool(() =>
        {
            var bundle = GetBundle();
            var notes = new StringBuilder();
            var changes = new StringBuilder();
            var any = false;

            foreach (var logPath in bundle.LogFiles)
            {
                any |= AppendLogFileChanges(bundle.Root, logPath, date, notes, changes);
            }

            if (!any)
            {
                // Preserve any skip notes even when nothing matched — a
                // silently-discarded note would hide a real read failure
                // behind an otherwise-correct "no changes" report.
                return notes.Length == 0 ? $"No changes since {date}." : notes + $"No changes since {date}.";
            }

            var sb = new StringBuilder();
            sb.Append("# Changes since ").Append(date).Append('\n').Append('\n');
            sb.Append(notes);
            sb.Append(changes);
            return sb.ToString();
        });
    }

    /// <summary>
    /// Processes one <c>log.md</c> for <see cref="ChangesSince"/>: on a
    /// strict-UTF-8 read failure, appends a skip note to <paramref name="notes"/>
    /// and returns <c>false</c>; otherwise parses the log, filters to valid-ISO
    /// days at or after <paramref name="date"/> (descending), and — if any
    /// matched — appends a <c>## {relative path}</c> section to <paramref name="changes"/>
    /// and returns <c>true</c>.
    /// </summary>
    private static bool AppendLogFileChanges(string bundleRoot, string logPath, string date, StringBuilder notes, StringBuilder changes)
    {
        var rel = Path.GetRelativePath(bundleRoot, logPath).Replace('\\', '/');

        string text;
        try
        {
            text = OkfEncodings.Strict.GetString(File.ReadAllBytes(logPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            notes.Append("> Skipped ").Append(rel).Append(" (could not be read: ")
                .Append(SkipReason(ex)).Append(").").Append('\n').Append('\n');
            return false;
        }

        var matchingDays = ChangeLog.Parse(text).Days
            .Where(d => ChangeLog.IsIsoDate(d.Date) && string.CompareOrdinal(d.Date, date) >= 0)
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .ToList();

        if (matchingDays.Count == 0)
        {
            return false;
        }

        changes.Append("## ").Append(rel).Append('\n');
        foreach (var day in matchingDays)
        {
            AppendLogDay(changes, day);
        }

        changes.Append('\n');
        return true;
    }

    /// <summary>Appends one <c>### {date}</c> section and its bulleted entries (bold <c>Kind</c> when present) to <paramref name="sb"/>.</summary>
    private static void AppendLogDay(StringBuilder sb, LogDay day)
    {
        sb.Append("### ").Append(day.Date).Append('\n');
        foreach (var entry in day.Entries)
        {
            if (entry.Kind is not null)
            {
                sb.Append("- **").Append(entry.Kind).Append("**: ").Append(entry.Text).Append('\n');
            }
            else
            {
                sb.Append("- ").Append(entry.Text).Append('\n');
            }
        }
    }

    /// <summary>Brief, non-sensitive reason category for a log-file read failure, used by <see cref="ChangesSince"/>'s skip note.</summary>
    private static string SkipReason(Exception ex) => ex switch
    {
        DecoderFallbackException => "not valid UTF-8",
        UnauthorizedAccessException => "access denied",
        IOException => "I/O error",
        _ => "unreadable",
    };

    /// <summary>Renders the ranked, bounded (top 20) search results as markdown, with the total match count.</summary>
    private static string FormatSearchResults(string query, string? tag, IReadOnlyList<string> terms, IReadOnlyList<(Concept Concept, int Score)> scored)
    {
        const int MaxResults = 20;
        var shown = scored.Take(MaxResults).ToList();

        var sb = new StringBuilder();
        sb.Append("# Search: \"").Append(query).Append('"');
        if (tag is not null)
        {
            sb.Append(" (tag: ").Append(tag).Append(')');
        }

        sb.Append('\n').Append('\n');
        sb.Append("Showing ").Append(shown.Count).Append(" of ").Append(scored.Count).Append(" result(s).").Append('\n').Append('\n');

        foreach (var (concept, score) in shown)
        {
            var title = concept.Document.Frontmatter.Title ?? concept.Id.ToString();
            sb.Append("* ").Append(concept.Id).Append(" — ").Append(title).Append(" (").Append(score).Append(')').Append('\n');

            var excerpt = FindExcerpt(concept.Document.Body, terms);
            if (excerpt is not null)
            {
                sb.Append("  ").Append(excerpt).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A concept's relevance score for <paramref name="terms"/>: the sum,
    /// over all terms, of the weights of every field the term is found in
    /// (<see cref="StringComparison.OrdinalIgnoreCase"/> substring match):
    /// title ×3, tags/description ×2, body ×1.
    /// </summary>
    private static int ScoreConcept(Concept concept, IReadOnlyList<string> terms)
    {
        var frontmatter = concept.Document.Frontmatter;
        var title = frontmatter.Title ?? string.Empty;
        var tagsAndDescription = string.Join(' ', frontmatter.Tags) + ' ' + (frontmatter.Description ?? string.Empty);
        var body = concept.Document.Body;

        var score = 0;
        foreach (var term in terms)
        {
            if (title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }

            if (tagsAndDescription.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (body.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        return score;
    }

    /// <summary>The first non-blank line of <paramref name="body"/> containing any of <paramref name="terms"/> (substring, ordinal-ignore-case), or <c>null</c> if none does.</summary>
    private static string? FindExcerpt(string body, IReadOnlyList<string> terms)
    {
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (terms.Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                return line;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the fallback listing for a bundle level with no <c>index.md</c>:
    /// the subdirectories and concepts found directly under
    /// <paramref name="segments"/>, derived from <see cref="Bundle.Concepts"/>.
    /// </summary>
    private static string BuildLevelListing(Bundle bundle, IReadOnlyList<string> segments, string relPath)
    {
        var subdirectories = new SortedSet<string>(StringComparer.Ordinal);
        var concepts = new List<Concept>();
        foreach (var concept in bundle.Concepts)
        {
            var conceptSegments = concept.Id.Segments;
            if (conceptSegments.Count <= segments.Count || !conceptSegments.Take(segments.Count).SequenceEqual(segments))
            {
                continue;
            }

            if (conceptSegments.Count == segments.Count + 1)
            {
                concepts.Add(concept);
            }
            else
            {
                subdirectories.Add(conceptSegments[segments.Count]);
            }
        }

        var sb = new StringBuilder();
        sb.Append("# ").Append(segments.Count == 0 ? "(bundle root)" : relPath).Append('\n').Append('\n');

        if (subdirectories.Count == 0 && concepts.Count == 0)
        {
            sb.Append("(empty)").Append('\n');
            return sb.ToString();
        }

        if (subdirectories.Count > 0)
        {
            AppendSection(sb, "Subdirectories", subdirectories);
            sb.Append('\n');
        }

        if (concepts.Count > 0)
        {
            var lines = concepts
                .OrderBy(c => c.Id)
                .Select(c => $"{c.Id} — {c.Document.Frontmatter.Title ?? c.Id.Name}");
            AppendSection(sb, "Concepts", lines);
        }

        return sb.ToString();
    }

    /// <summary>Bundle-wide stats: concept, link, and broken-link counts, plus the broken links themselves.</summary>
    private static string BuildBundleGraphSummary(Bundle bundle)
    {
        var totalLinks = bundle.Concepts.Sum(c => bundle.LinksFrom(c.Id).Count);
        var broken = bundle.BrokenLinks();

        var sb = new StringBuilder();
        sb.Append("# Bundle graph").Append('\n').Append('\n');
        sb.Append("- ").Append(bundle.Count).Append(" concepts").Append('\n');
        sb.Append("- ").Append(totalLinks).Append(" links").Append('\n');
        sb.Append("- ").Append(broken.Count).Append(" broken links").Append('\n');

        if (broken.Count > 0)
        {
            sb.Append('\n');
            AppendSection(sb, "Broken links", broken.Select(b => $"{b.Source} -> {b.RawTarget}"));
        }

        return sb.ToString();
    }

    /// <summary>A single concept's outgoing links, backlinks, and broken outgoing links.</summary>
    private static string BuildConceptGraphDetail(Bundle bundle, ConceptId id)
    {
        var outgoing = bundle.LinksFrom(id);
        var backlinks = bundle.Backlinks(id);
        var brokenOutgoing = outgoing.Where(l => !l.Exists).ToList();

        var sb = new StringBuilder();
        sb.Append("# Graph: ").Append(id).Append('\n').Append('\n');
        AppendSection(sb, "Outgoing links", FormatOutgoingLinks(outgoing));
        sb.Append('\n');
        AppendSection(sb, "Backlinks", FormatBacklinks(backlinks));
        sb.Append('\n');
        AppendSection(sb, "Broken links", brokenOutgoing.Select(l => $"{id} -> {l.Raw}"));

        return sb.ToString();
    }

    private static IEnumerable<string> FormatOutgoingLinks(IEnumerable<ResolvedLink> links) =>
        links.Select(link => link.Target + (link.Exists ? string.Empty : " (broken)"));

    private static IEnumerable<string> FormatBacklinks(IEnumerable<ConceptId> backlinks) =>
        backlinks.Select(source => source.ToString());

    /// <summary>Appends a markdown <c>## </c>-heading section, one bullet per line, or <see cref="NoneLine"/> if empty.</summary>
    private static void AppendSection(StringBuilder sb, string heading, IEnumerable<string> lines)
    {
        sb.Append("## ").Append(heading).Append('\n');
        var any = false;
        foreach (var line in lines)
        {
            any = true;
            sb.Append("- ").Append(line).Append('\n');
        }

        if (!any)
        {
            sb.Append(NoneLine).Append('\n');
        }
    }

    private static void AppendFrontmatterBlock(StringBuilder sb, Frontmatter frontmatter)
    {
        var map = frontmatter.AsMapping();
        if (map.IsEmpty)
        {
            return;
        }

        foreach (var key in map.Keys)
        {
            sb.Append(key).Append(": ").Append(FormatFrontmatterValue(map.Get(key))).Append('\n');
        }

        sb.Append('\n');
    }

    /// <summary>
    /// Runs a tool method body, converting any exception that a well-formed
    /// but unlucky input could still trigger — a bundle that fails to
    /// (re)load (<see cref="OkfException"/>, e.g. <see cref="BundleLoadException"/>
    /// for I/O failures, a missing root, or non-UTF-8 content), a rejected
    /// argument surfaced late by a BCL API (<see cref="ArgumentException"/>),
    /// a filesystem read failure (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>), or a strict-UTF-8 decode
    /// failure reading an existing reserved file directly
    /// (<see cref="DecoderFallbackException"/>, e.g. <see cref="AppendLog"/>
    /// reading a non-UTF-8 <c>log.md</c>) — into a plain-text message. This
    /// is the single enforcement point for the "tools never throw toward the
    /// LLM" rule: callers still perform their own null/whitespace and
    /// null-character guards up front (for a precise, tool-specific message),
    /// but this catch-all is what makes every public tool method structurally
    /// unable to throw for any string input, now and for tools added later.
    /// </summary>
    private static string RunTool(Func<string> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (ex is OkfException or ArgumentException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string ConceptNotFoundMessage(string conceptId) =>
        $"Concept '{conceptId}' not found. Use okf_browse to list available concepts.";

    /// <summary>
    /// <c>true</c> if <paramref name="candidate"/> is <paramref name="root"/>
    /// itself or a descendant of it, comparing resolved absolute paths
    /// case-insensitively (Windows/macOS filesystems are typically
    /// case-insensitive; a stricter check would reject legitimate paths
    /// there). Defense in depth alongside the explicit <c>..</c>/rooted-path
    /// rejection in <see cref="Browse"/>.
    /// </summary>
    private static bool IsWithinBundleRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        if (string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>true</c> if <paramref name="path"/> itself, or any directory
    /// strictly between it and <paramref name="bundleRoot"/>, is a
    /// filesystem reparse point (symlink, junction, mount point) -- checked
    /// via <see cref="ReparsePoints.IsReparsePoint"/>, which reports the
    /// entry's own type (lstat-like) without following it.
    ///
    /// <see cref="IsWithinBundleRoot"/> only compares resolved path STRINGS:
    /// a junction at, say, <c>bundleRoot/tables</c> pointing at an external
    /// directory still lexically resolves to a path under
    /// <paramref name="bundleRoot"/> via <see cref="Path.GetFullPath(string)"/>,
    /// so that check alone would accept it -- but the OS follows the
    /// junction the moment <see cref="Browse"/> or <see cref="WriteConcept"/>
    /// actually touches disk (<see cref="Directory.Exists(string)"/>,
    /// <see cref="File.ReadAllText(string)"/>, <see cref="File.WriteAllText(string, string)"/>),
    /// silently reading or writing outside the bundle. Walking every
    /// intermediate directory and rejecting on the first reparse point
    /// closes that gap.
    ///
    /// Used by <see cref="Browse"/> (on the resolved directory) and
    /// <see cref="WriteConcept"/> (on the target file's parent directory --
    /// <see cref="WriteConcept"/> separately checks
    /// <see cref="ReparsePoints.IsReparsePoint"/> on the target FILE itself,
    /// since this helper only walks directory ancestors and never inspects
    /// the leaf path passed to it, so it would miss an existing concept file
    /// that is itself a planted symlink).
    ///
    /// Not needed by <see cref="ReadConcept"/>, <see cref="Graph"/>, or
    /// <see cref="Search"/> -- they only query the already-loaded
    /// <see cref="Bundle"/>, whose own load walk already skips reparse-point
    /// entries (mirroring Rust's lstat-based <c>collect_markdown</c>); nor by
    /// <see cref="RegenerateIndexes"/>, whose whole-root walk (<see cref="IndexGenerator"/>)
    /// likewise already skips reparse-point directories via the same core
    /// helper.
    ///
    /// <see cref="AppendLog"/> does NOT use this helper for its main guard:
    /// <c>log.md</c> always lives directly at <see cref="BundleRoot"/>, so
    /// its only directory ancestor is <see cref="BundleRoot"/> itself, which
    /// this helper's walk never inspects (it stops as soon as
    /// <paramref name="path"/> equals <paramref name="bundleRoot"/>). The
    /// real risk for <see cref="AppendLog"/> is <c>log.md</c> itself being a
    /// planted file symlink -- <see cref="File.ReadAllBytes(string)"/>/
    /// <see cref="File.WriteAllText(string, string)"/> would follow it and
    /// silently overwrite whatever external file it points at -- so
    /// <see cref="AppendLog"/> checks
    /// <see cref="ReparsePoints.IsReparsePoint"/> on <c>log.md</c> directly
    /// instead.
    /// </summary>
    private static bool HasReparsePointAncestor(string bundleRoot, string path)
    {
        var fullRoot = Path.GetFullPath(bundleRoot);
        var current = Path.GetFullPath(path);

        while (!string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (ReparsePoints.IsReparsePoint(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                // Walked past the filesystem root without ever reaching
                // bundleRoot -- callers already guard containment via
                // IsWithinBundleRoot, but stop here rather than loop forever.
                break;
            }

            current = parent;
        }

        return false;
    }

    /// <summary>
    /// Renders a frontmatter value as a single display line: scalars via
    /// <see cref="YamlValue.AsDisplayString"/>, sequences as a
    /// comma-separated flow list (e.g. <c>[sales, orders]</c>), and any
    /// other structure via its YAML text with newlines collapsed.
    /// </summary>
    private static string FormatFrontmatterValue(YamlValue? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var display = value.AsDisplayString();
        if (display is not null)
        {
            return display;
        }

        if (value is YamlSequence seq)
        {
            return "[" + string.Join(", ", seq.Items.Select(FormatFrontmatterValue)) + "]";
        }

        return value.ToYamlString().Trim();
    }
}
