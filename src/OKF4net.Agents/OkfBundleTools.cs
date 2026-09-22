// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OKF4net.Agents.Internal;
using OKF4net.Attestation;
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

    private const string AuditUsageMessage =
        "Usage: okf_audit takes optional filters — stale (bool), trust (comma-separated: "
        + "unverified, machine-confirmed, human-reviewed), status (draft, stable or deprecated) "
        + "and type (exact frontmatter type). Example: okf_audit(stale: true, trust: \"unverified\").";

    private const string VerifyUsageMessage =
        "Usage: okf_verify records a review — comma-separated concept ids, plus a well-formed "
        + "§7 actor — one of exactly three forms: human:<id>, process:<id>, or "
        + "<producer>/<version> (no agent: prefix). Example: "
        + "okf_verify(\"metrics/dau, metrics/revenue\", \"human:ada\").";

    /// <summary>
    /// Refusal message for an actor carrying a character that would break the
    /// rendered line. The predicate is shared
    /// (<c>LineSafeText.ContainsControlCharacter</c>); only the phrasing is local, so
    /// it reads like this class's other <c>Error: …</c> results rather than
    /// like the CLI's. Deliberately does NOT echo the offending value: doing so
    /// would put the newline it is refusing into this very message.
    /// </summary>
    private const string VerifyControlCharacterMessage =
        "Error: a §7 actor must not contain control characters.";

    /// <summary>
    /// The core write primitive this tool set delegates every write to:
    /// producer-validated create/update (<see cref="WriteConcept"/>) and
    /// atomic read-modify-write append (<see cref="AppendToConceptAtomic"/>),
    /// plus the process-wide per-bundle-root lock registry shared by every
    /// <see cref="BundleConceptWriter"/> (and therefore every
    /// <see cref="OkfBundleTools"/>) instance constructed over the same
    /// canonicalized bundle root. Constructed with <c>onWriteCommitted:
    /// () =&gt; _bundle = null</c> so a successful write invalidates this
    /// instance's cache atomically with the write, from inside the shared
    /// lock.
    /// </summary>
    private readonly BundleConceptWriter _writer;

    /// <summary>
    /// Guards <see cref="_bundle"/> and every write this class performs to
    /// disk. Agent hosts may invoke tool methods concurrently from multiple
    /// threads, so the lazy cache in <see cref="GetBundle"/> and the
    /// invalidation in <see cref="InvalidateBundle"/> must not race; the same
    /// lock also serializes <see cref="AppendLog"/> and
    /// <see cref="RegenerateIndexes"/>'s own read-modify-write sequences
    /// (each holds it around the read, the write, and its own cache
    /// invalidation) against <see cref="_writer"/>'s own writes, so two
    /// concurrent calls into the same tool can't interleave and lose one
    /// side's update.
    ///
    /// This is <see cref="_writer"/>'s own <see cref="BundleConceptWriter.WriteLock"/>,
    /// obtained from the process-wide registry it maintains, so this
    /// guarantee extends to every <see cref="OkfBundleTools"/> instance
    /// constructed over the same canonicalized bundle root -- not just calls
    /// on THIS instance. It does NOT serialize writes across separate
    /// processes (e.g. two CLI invocations, or two server processes sharing
    /// a network path), and a C# lock cannot defend against a concurrent
    /// external actor mutating the bundle's files directly on disk. The
    /// per-instance <see cref="_bundle"/> CACHE deliberately stays
    /// instance-level (unaffected by this change): <see cref="AppendToConceptAtomic"/>
    /// always re-reads the concept's on-disk body under this lock rather
    /// than trusting any cache, so two instances having independent caches
    /// does not affect write correctness, only how eagerly each one's
    /// read-only calls see another instance's writes before their own next
    /// reload.
    /// </summary>
    private readonly object _bundleLock;

    private Bundle? _bundle;

    /// <summary>
    /// The §10.5 attestation orchestrator, if one has been wired for this tool
    /// set. <see langword="null"/> unless the <see cref="OkfBundleTools(string, AttestationOrchestrator?)"/>
    /// overload was used with a non-null orchestrator — in that case,
    /// <see cref="RunComputation"/> is a no-op error and <see cref="GetTools()"/>
    /// omits <c>okf_run_computation</c> entirely (§10.5 requires a host-supplied
    /// runtime; there is nothing sane to expose without one). <see cref="GetComputation"/>
    /// never depends on this field: reading a computation's contract and
    /// source needs no runtime.
    /// </summary>
    private readonly AttestationOrchestrator? _orchestrator;

    /// <summary>
    /// Creates the tool set rooted at <paramref name="bundleRoot"/>, without an
    /// attestation orchestrator (so <c>okf_run_computation</c> is not exposed;
    /// see <see cref="OkfBundleTools(string, AttestationOrchestrator?)"/>).
    /// </summary>
    /// <param name="bundleRoot">Path to the bundle's root directory.</param>
    /// <exception cref="ArgumentException"><paramref name="bundleRoot"/> does not exist.</exception>
    public OkfBundleTools(string bundleRoot)
        : this(bundleRoot, orchestrator: null)
    {
    }

    /// <summary>
    /// Creates the tool set rooted at <paramref name="bundleRoot"/>, wiring
    /// <paramref name="orchestrator"/> for §10.5 attested-computation runs. When
    /// <paramref name="orchestrator"/> is <see langword="null"/>, this is
    /// equivalent to <see cref="OkfBundleTools(string)"/>: <c>okf_get_computation</c>
    /// is still exposed (it is read-only and needs no runtime), but
    /// <c>okf_run_computation</c> is omitted from <see cref="GetTools()"/> and
    /// <see cref="RunComputation"/> reports a plain-text error instead of
    /// running anything.
    /// </summary>
    /// <param name="bundleRoot">Path to the bundle's root directory.</param>
    /// <param name="orchestrator">The attestation orchestrator to run §10.5 computations through, or <see langword="null"/> to leave attested-computation execution unwired.</param>
    /// <exception cref="ArgumentException"><paramref name="bundleRoot"/> does not exist.</exception>
    public OkfBundleTools(string bundleRoot, AttestationOrchestrator? orchestrator)
    {
        if (!Directory.Exists(bundleRoot))
        {
            throw new ArgumentException($"bundle root does not exist: {bundleRoot}", nameof(bundleRoot));
        }

        BundleRoot = bundleRoot;
        _orchestrator = orchestrator;

        _writer = new BundleConceptWriter(bundleRoot, onWriteCommitted: () => _bundle = null);
        _bundleLock = _writer.WriteLock;
        _writer.AutoStampGenerated = true;
        _writer.UtcNow = () => UtcNow();
    }

    /// <summary>The bundle's root directory, as passed to the constructor.</summary>
    public string BundleRoot { get; }

    /// <summary>
    /// Wall-clock ceiling on one <see cref="RunComputationAsync"/> run, default
    /// two minutes. §10 says nothing about time limits; this is a host guard,
    /// because the bind/execute/attest stages are host-plugged code that may do
    /// unbounded I/O and an agent invocation cannot wait forever.
    ///
    /// Elapsing is reported to the model as a normal non-displayable outcome,
    /// never thrown at the caller — see <see cref="RunComputationAsync"/>.
    /// <see cref="Timeout.InfiniteTimeSpan"/> disables it for a host that does
    /// its own bounding. A value the runtime will not accept as a delay —
    /// negative, or past its ~49.7-day ceiling — is likewise reported as an
    /// error, not thrown.
    /// </summary>
    public TimeSpan ComputationTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The current UTC time, consulted by <see cref="AppendLog"/> to compute
    /// "today"'s ISO date heading. Defaults to <see cref="DateTime.UtcNow"/>;
    /// overridable so tests can pin the date deterministically. Internal: an
    /// implementation seam, not part of the tool's public surface.
    /// </summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// The current instant, derived from <see cref="UtcNow"/> — the shared seam
    /// behind <see cref="ReadConcept"/>'s and <see cref="Search"/>'s staleness
    /// checks. §5 makes <c>stale_after</c> an instant, so these compare instants.
    /// </summary>
    private DateTimeOffset Now => new(DateTime.SpecifyKind(UtcNow(), DateTimeKind.Utc), TimeSpan.Zero);

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
    /// The tool names among <see cref="GetTools()"/>'s output that write to the
    /// bundle: <c>okf_write_concept</c>, <c>okf_verify</c>, <c>okf_append_log</c>,
    /// and <c>okf_regenerate_indexes</c>. A host that wants a read-only tool set
    /// (e.g. a read-only MCP server, or a demo that must never mutate a
    /// pinned/shared bundle) can filter <see cref="GetTools()"/>'s result
    /// against this set instead of hand-maintaining its own list of tool
    /// names — the single source of truth for "which tools write," so a
    /// future write tool added here can't silently slip past a consumer's
    /// stale private copy of the list.
    /// </summary>
    public static IReadOnlySet<string> WriteToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "okf_write_concept",
        "okf_append_log",
        "okf_regenerate_indexes",
        "okf_verify",
    };

    /// <summary>
    /// All OKF tools as Agent Framework <see cref="AIFunction"/>s (via
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
    /// graph → search → audit → write → verify → append → regenerate →
    /// validate → changes-since → get-computation → (conditionally)
    /// run-computation.
    ///
    /// <c>okf_get_computation</c> is always included — it is read-only and
    /// needs no attestation runtime. <c>okf_run_computation</c> is included
    /// only when this instance was constructed with a non-null
    /// <see cref="AttestationOrchestrator"/> (see
    /// <see cref="OkfBundleTools(string, AttestationOrchestrator?)"/>):
    /// without one, there is nothing for it to run, so it is omitted from the
    /// tool set entirely rather than exposed as an always-erroring tool.
    /// </summary>
    public IList<AITool> GetTools() => GetTools(OkfToolMode.ReadWrite);

    /// <summary>
    /// All OKF tools, with the four write-capable ones exposed according to
    /// <paramref name="mode"/> — see <see cref="OkfToolMode"/> for what each
    /// means and why.
    ///
    /// <para>
    /// The parameterless <see cref="GetTools()"/> is
    /// <see cref="OkfToolMode.ReadWrite"/> and stays that way: flipping the
    /// default would silently change every host already calling it. This
    /// overload is how a host opts into something safer, and
    /// <see cref="WriteToolNames"/> remains the single source of truth for
    /// which tools count as writes, so a write tool added later cannot slip
    /// past either path.
    /// </para>
    /// </summary>
    /// <param name="mode">How to expose the write-capable tools.</param>
    public IList<AITool> GetTools(OkfToolMode mode)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(ReadConcept, "okf_read_concept"),
            AIFunctionFactory.Create(Browse, "okf_browse"),
            AIFunctionFactory.Create(Graph, "okf_graph"),
            AIFunctionFactory.Create(Search, "okf_search"),
            AIFunctionFactory.Create(Audit, "okf_audit"),
            AIFunctionFactory.Create(WriteConcept, "okf_write_concept"),
            AIFunctionFactory.Create(Verify, "okf_verify"),
            AIFunctionFactory.Create(AppendLog, "okf_append_log"),
            AIFunctionFactory.Create(RegenerateIndexes, "okf_regenerate_indexes"),
            AIFunctionFactory.Create(ValidateBundle, "okf_validate_bundle"),
            AIFunctionFactory.Create(ChangesSince, "okf_changes_since"),
            AIFunctionFactory.Create(GetComputation, "okf_get_computation"),
        };

        if (_orchestrator is not null)
        {
            // The async form: AIFunctionFactory binds its CancellationToken from the
            // invocation and leaves it out of the JSON schema, so the model sees the
            // same two parameters while the host gains a way to stop a wedged run.
            // Wrapped so a duplicate top-level parameter name in the model's JSON is an
            // error rather than last-wins; the schema the model sees is unchanged.
            tools.Add(new TopLevelDuplicateParameterGuard(AIFunctionFactory.Create(RunComputationAsync, "okf_run_computation")));
        }

        return mode switch
        {
            OkfToolMode.ReadOnly =>
                tools.Where(t => !WriteToolNames.Contains(((AIFunction)t).Name)).ToList(),
            OkfToolMode.RequireApprovalForWrites =>
                tools.Select(t => WriteToolNames.Contains(((AIFunction)t).Name)
                    ? new ApprovalRequiredAIFunction((AIFunction)t)
                    : t).ToList(),
            _ => tools,
        };
    }

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
        if (GuardConceptId(conceptId) is { } err)
        {
            return err;
        }

        return RunTool(() =>
        {
            var bundle = GetBundle();
            if (!ConceptId.TryParse(conceptId, out var id) || bundle.Get(id) is not { } concept)
            {
                return ConceptNotFoundMessage(conceptId);
            }

            var sb = new StringBuilder();
            sb.Append("# ").Append(DisplayTitle(concept, concept.Id.ToString())).Append('\n').Append('\n');

            var fm = concept.Document.Frontmatter;
            var lc = fm.Lifecycle;
            var trust = fm.TrustTier;
            var stale = lc.IsStale(Now);
            if (lc.Status != ConceptStatus.Stable || trust != TrustTier.Unverified || stale)
            {
                sb.Append("> status: ").Append(AuditVocabulary.Name(lc.Status))
                  .Append(" | trust: ").Append(AuditVocabulary.Name(trust))
                  .Append(" | stale: ").Append(stale ? "yes" : "no")
                  .Append("\n\n");
            }

            AppendFrontmatterBlock(sb, concept.Document.Frontmatter);
            sb.Append(concept.Document.Body.TrimEnd('\n')).Append('\n').Append('\n');
            AppendSection(sb, "Outgoing links", FormatOutgoingLinks(bundle.LinksFrom(id)));
            sb.Append('\n');
            AppendSection(sb, "Backlinks", FormatBacklinks(bundle.Backlinks(id)));

            if (fm.IsAttestedComputation)
            {
                sb.Append('\n');
                AppendContractSummary(sb, fm.ComputationContract);
                sb.Append(_orchestrator is not null
                    ? "(Use okf_get_computation for the full computation source; okf_run_computation to run it.)"
                    : "(Use okf_get_computation for the full computation source.)").Append('\n');
            }

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

            // Strict ancestor walk: Browse reads through fullDir, so a directory
            // whose link status cannot be read is refused like a link (a guard
            // fails closed -- see ReparsePoints.IsReparsePointOrUninspectable).
            if (!ReparsePoints.IsWithinBundleRoot(bundle.Root, fullDir)
                || !Directory.Exists(fullDir)
                || ReparsePoints.HasReparsePointOrUninspectableAncestor(bundle.Root, fullDir))
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
    /// Concepts scoring zero are dropped. Matches are ranked by descending
    /// score, then ascending concept id (ordinal); the 20 that are SHOWN are
    /// then picked by <see cref="ConceptSearch.TopDiversified"/> rather than
    /// taken off the front of that ranking, so the best match is first but the
    /// rest is spread across top-level id families and the printed scores are
    /// not monotonically descending. The total match count is reported
    /// alongside.
    /// </summary>
    /// <param name="query">The search query (case-insensitive substring terms).</param>
    /// <param name="tag">Optional tag filter: only concepts carrying this tag.</param>
    [Description("Full-text search across concept titles, descriptions, tags and bodies. Returns the best-matching concept id first, then spreads the remaining results across top-level id families, so the list is not in descending score order.")]
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

            return FormatSearchResults(query, effectiveTag, scored, Now);
        });
    }

    /// <summary>
    /// Scores every candidate concept against <paramref name="query"/>
    /// (optionally restricted to concepts carrying <paramref name="tag"/>):
    /// the shared seam behind <see cref="Search"/> and
    /// <see cref="OKF4net.Agents.OkfContextProvider"/>'s progressive
    /// disclosure. A thin delegate onto the core, byte-identical
    /// <see cref="OKF4net.ConceptSearch.Search"/> (weights, score&gt;0 filter,
    /// ordering) over <see cref="GetBundle"/>'s concepts, so the two can
    /// never drift apart. Assumes <paramref name="query"/> has already been
    /// validated non-null/blank by the caller (mirroring <see cref="Search"/>'s
    /// own precondition); a query that splits into zero terms yields an empty
    /// result rather than throwing.
    /// </summary>
    internal IReadOnlyList<ScoredConcept> ScoreConceptsFor(string query, string? tag = null) =>
        ConceptSearch.Search(GetBundle().Concepts, query, tag);

    /// <summary>
    /// Audits the bundle's trust, freshness and lifecycle signals (§5.3–§5.5):
    /// counts over the whole bundle, then the concepts the filters select,
    /// bounded to 20 entries. The worklist heading tracks <paramref name="stale"/>:
    /// with it true (the default) the selection is the stale worklist, so the
    /// heading reads "needs attention"; with it false the selection can include
    /// perfectly fresh concepts, so the heading reads the neutral "selected"
    /// instead.
    /// </summary>
    /// <param name="stale">
    /// Keep only concepts past their <c>stale_after</c> instant. Left unset it
    /// follows the CLI's rule: the stale worklist when nothing else is
    /// filtered, no staleness constraint as soon as another filter is given.
    /// Without that, "which concepts were never verified by a human?" would
    /// silently mean "…and are also stale", and answer "none" on a bundle whose
    /// unverified concept simply has no <c>stale_after</c>.
    /// </param>
    /// <param name="trust">Comma-separated trust tiers to keep.</param>
    /// <param name="status">Keep only concepts with this lifecycle status.</param>
    /// <param name="type">Keep only concepts with this frontmatter type (exact match).</param>
    [Description("Audit the bundle's trust, freshness and lifecycle signals: counts by trust tier and status over the whole bundle, plus the concepts the filters select. Called bare it returns the stale worklist; combined with trust/status/type it stops constraining staleness unless you pass stale explicitly.")]
    public string Audit(
        [Description("Only concepts past their stale_after instant. Leave unset for the default: the stale worklist when no other filter is given, no staleness constraint when one is.")] bool? stale = null,
        [Description("Comma-separated trust tiers to include: unverified, machine-confirmed, human-reviewed.")] string? trust = null,
        [Description("Only concepts with this lifecycle status: draft, stable or deprecated.")] string? status = null,
        [Description("Only concepts with this frontmatter type (exact match).")] string? type = null)
    {
        HashSet<TrustTier>? tiers = null;
        if (trust is not null)
        {
            if (!AuditVocabulary.TryParseTrustTiers(trust, out var parsed, out _))
            {
                return AuditUsageMessage;
            }

            tiers = parsed;
        }

        ConceptStatus? parsedStatus = null;
        if (status is not null)
        {
            if (!AuditVocabulary.TryParseStatus(status.Trim(), out var value))
            {
                return AuditUsageMessage;
            }

            parsedStatus = value;
        }

        // Same treatment as status/trust: a model copying a label from prose
        // brings whitespace, and AuditQuery's match is
        // string.Equals(..., Ordinal), so an untrimmed value would silently
        // select nothing.
        var trimmedType = string.IsNullOrWhiteSpace(type) ? null : type.Trim();

        // The CLI's rule, restated: with no filter flag it reports the stale
        // worklist; the moment one is given, staleness stops being implied.
        // An explicit `stale` always wins over that default.
        var otherFilterGiven = tiers is not null || parsedStatus is not null || trimmedType is not null;
        var staleOnly = stale ?? !otherFilterGiven;

        // Everything that can touch the filesystem goes through RunTool, the
        // guard every bundle-loading tool uses: it turns OkfException (hence
        // BundleLoadException), ArgumentException, IOException,
        // UnauthorizedAccessException and DecoderFallbackException into an
        // "Error: ..." string. A function tool must return an error, not throw
        // one -- a directory deleted after construction would otherwise escape
        // as an exception into the agent runtime.
        return RunTool(() =>
        {
            var report = ConceptAudit.Run(
                GetBundle(),
                new AuditQuery(staleOnly, tiers, parsedStatus, trimmedType),

                // Pinned to Now -- the same UtcNow seam ReadConcept and
                // Search use -- so the tool's output never depends on the day
                // it runs.
                new FixedClock(Now));

            return RenderAudit(report, staleOnly);
        });
    }

    /// <summary>
    /// Creates or updates one concept document. Producer-grade validation
    /// (<see cref="OkfDocument.Validate"/>: non-empty <c>type</c>,
    /// <c>title</c> and <c>description</c>) runs BEFORE
    /// anything is written — on failure, the file on disk (if any) is left
    /// untouched. Never throws for expected errors (a null/blank/malformed
    /// concept id, a reserved id, invalid frontmatter YAML, or a failed
    /// validation) — those are reported as a plain-text message instead. A
    /// thin delegate onto <see cref="BundleConceptWriter.WriteConcept(string, string, string)"/>.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatterYaml">Frontmatter as <c>key: value</c> lines (the same YAML subset used inside a document's frontmatter block, without the <c>---</c> delimiters).</param>
    /// <param name="body">The markdown body.</param>
    [Description("Create or update a concept document. The frontmatter must contain non-empty type, title and description (producer-grade validation is enforced before writing).")]
    public string WriteConcept(
        [Description("The concept id (path without .md), e.g. 'tables/refunds'.")] string conceptId,
        [Description("Frontmatter as 'key: value' lines (YAML subset).")] string frontmatterYaml,
        [Description("The markdown body.")] string body) =>
        _writer.WriteConcept(conceptId, frontmatterYaml, body);

    /// <summary>
    /// Records a review of one or more concepts: adds — or replaces — the
    /// caller's <c>{ by, at }</c> entry in each concept's <c>verified</c> list.
    /// A stamp is a dated declaration, not a proof: this tool cannot check that
    /// the caller is who <paramref name="by"/> names, exactly like the CLI verb.
    /// </summary>
    /// <param name="conceptIds">Comma-separated concept ids; each must already exist.</param>
    /// <param name="by">The §7 actor recording the review.</param>
    /// <param name="at">
    /// UTC timestamp in the exact form <c>yyyy-MM-ddTHH:mm:ssZ</c> (the writer's
    /// <see cref="BundleConceptWriter.RecordVerifications"/> rejects fractional
    /// seconds, a numeric offset, and a bare date); omit for now.
    /// </param>
    [Description("Record a review of one or more concepts: adds or replaces the caller's { by, at } entry in each concept's `verified` list. The stamp is a dated declaration, not a proof — the same rules as the okf verify CLI verb.")]
    public string Verify(
        [Description("Comma-separated concept ids (paths without .md). Explicit ids only — there is no whole-bundle form.")] string conceptIds,
        [Description("The §7 actor recording the review — one of exactly three forms: human:<id> (e.g. human:ada), process:<id> (e.g. process:nightly), or <producer>/<version> for an agent or tool (e.g. assistant/1.0). There is no agent: prefix: writing agent:assistant/1.0 stores the producer name \"agent:assistant\". Must not contain control characters.")] string by,
        [Description("UTC timestamp in the exact form yyyy-MM-ddTHH:mm:ssZ, e.g. 2026-08-28T09:14:00Z — no fractional seconds, no offset, no bare date. Omit for now.")] string? at = null)
    {
        var ids = (conceptIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // Refused ahead of the usage message so a control-bearing actor gets a
        // message that names the actual problem — an agent handed the generic
        // usage text would most likely retry the same value. The write gate
        // (BundleConceptWriter.RecordVerifications) is what stops the value
        // being stored; this shares its one predicate rather than testing
        // characters itself. See LineSafeText.ContainsControlCharacter.
        if (by is not null && LineSafeText.ContainsControlCharacter(by))
        {
            return VerifyControlCharacterMessage;
        }

        if (ids.Count == 0 || by is null || !Actor.Parse(by).IsWellFormed)
        {
            return VerifyUsageMessage;
        }

        return RunTool(() =>
        {
            // The single governed §11 floor (BundleConceptWriter.CheckVerificationTargets,
            // also called first thing inside RecordVerifications itself, same
            // as the CLI's CmdVerify) — this call is not what stops a
            // half-stamped batch (the real write below refuses the whole
            // batch atomically on its own). What it buys is message quality:
            // naming the offender directly ("concept \"x\" does not exist" /
            // "concept \"x\" has no `type`...") instead of the writer's own
            // message shape, and it reads the k named files directly rather
            // than going through the tool's cached bundle (unaffected by
            // whether that cache is stale).
            var targetProblem = _writer.CheckVerificationTargets(ids);
            if (targetProblem is { } problem)
            {
                return problem.Kind switch
                {
                    VerificationTargetProblemKind.NotConformant =>
                        $"Error: concept {DebugQuote.Quote(problem.ConceptId)} has no `type` and is not §11-conformant.",
                    VerificationTargetProblemKind.DuplicateName =>
                        $"Error: concept {DebugQuote.Quote(problem.ConceptId)} is named more than once.",
                    // The file exists and names its own parse error: falling
                    // through to "does not exist" (which this did until now)
                    // told the model to go and create a concept that is
                    // already there. The detail is the parser's own message
                    // -- library-authored, never bundle text. Rendered through
                    // DetailAsSentence, not "{Detail}.", which doubled the
                    // period on every unreadable-concept refusal.
                    VerificationTargetProblemKind.ParseFailure =>
                        $"Error: concept {DebugQuote.Quote(problem.ConceptId)} could not be parsed as a valid OKF document: {problem.DetailAsSentence()}",
                    VerificationTargetProblemKind.Unreadable =>
                        $"Error: concept {DebugQuote.Quote(problem.ConceptId)} could not be read: {problem.DetailAsSentence()}",
                    // Already a complete "Error: "-prefixed sentence naming the
                    // id (ValidateConceptTarget's own return value, captured
                    // once by CheckVerificationTargets), so it is returned
                    // as-is -- the same arm FormatVerificationTargetProblem has.
                    VerificationTargetProblemKind.InvalidId => problem.Detail!,
                    VerificationTargetProblemKind.NotFound =>
                        $"Error: concept {DebugQuote.Quote(problem.ConceptId)} does not exist.",
                    // Deliberately NOT "does not exist": a kind added to the
                    // enum later must not be misdiagnosed as a missing file,
                    // which is exactly what happened to ParseFailure and
                    // InvalidId while this switch ended at a catch-all arm.
                    _ => $"Error: concept {DebugQuote.Quote(problem.ConceptId)} cannot be verified.",
                };
            }

            // One batch call — the validation guarantee comes from the writer, so the
            // pre-check above is only there to give a nicer message.
            // `at` is passed through untouched, null included: the writer owns
            // the clock seam and reports the timestamp it used, so the tool
            // never dates anything itself.
            var outcome = _writer.RecordVerifications(ids, by, at);

            // The same line shape as the CLI verb, via the shared AuditText:
            // the CLI's bytes are golden-locked, so the wording must not move
            // because an agent-facing string was tuned. The tool's tests
            // still assert this exact shape so a change here cannot drift
            // unnoticed.
            var lines = new StringBuilder();
            foreach (var record in outcome.Records)
            {
                lines.Append(AuditText.FormatVerificationRecord(record, by)).Append('\n');
            }

            // A rejected batch has no records and yields the message alone; a
            // batch that failed part-way through writing has both, and the
            // agent must see both — the lines for what landed, then why it
            // stopped.
            if (!outcome.Recorded)
            {
                lines.Append(outcome.Message).Append('\n');
            }

            return lines.ToString();
        });
    }

    /// <summary>
    /// Atomically reads, transforms, and rewrites one concept's body — the
    /// seam <see cref="OKF4net.Agents.OkfContextProvider.CaptureMemory"/> uses
    /// to close the same-day memory-capture race (E2). A thin delegate onto
    /// <see cref="BundleConceptWriter.AppendToConceptAtomic"/>, whose remarks
    /// describe the full atomicity guarantee (and its residual TOCTOU
    /// limitation). <see langword="internal"/>: a narrow seam for
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
        Func<string?, string> buildBody) =>
        _writer.AppendToConceptAtomic(conceptId, frontmatterYamlIfCreating, buildBody);

    /// <summary>
    /// Test-only hook, forwarded to <see cref="BundleConceptWriter.BeforeLateReparseCheckForTest"/>
    /// so it fires immediately before the late reparse-point re-check inside
    /// <see cref="_writer"/>'s own write methods, and separately consulted by
    /// <see cref="AppendLog"/>'s own inline late re-check (after computing the
    /// new log content, still inside <see cref="_bundleLock"/>). Lets a test
    /// deterministically simulate a filesystem substitution racing the final
    /// write -- e.g. deleting the just-created parent directory and replacing
    /// it with a junction to an external directory, or swapping <c>log.md</c>
    /// itself for a symlink -- at exactly the point such a race would need to
    /// land, instead of relying on real (flaky, unreliable) thread timing.
    /// <see langword="internal"/>, always <see langword="null"/> outside
    /// tests, so it has zero effect on production behavior.
    /// </summary>
    internal Action? BeforeLateReparseCheckForTest
    {
        get => _beforeLateReparseCheckForTest;
        set
        {
            _beforeLateReparseCheckForTest = value; // still consulted by AppendLog's own late re-check
            _writer.BeforeLateReparseCheckForTest = value;
        }
    }

    private Action? _beforeLateReparseCheckForTest;

    /// <summary>
    /// Validates one <see cref="AppendLog"/> argument, returning the rejection
    /// message or <see langword="null"/> when it is acceptable.
    ///
    /// Both of that method's arguments get the identical checks, so they share
    /// one validator rather than two copies that could drift.
    ///
    /// <para><b>Three treatments, one boundary.</b> A caller-supplied field is
    /// written into the user's repository AND echoed into a tool result, so
    /// every character that survives does so for a stated reason:</para>
    ///
    /// <list type="bullet">
    /// <item><c>\n</c> and <c>\r</c> — <b>REJECTED</b>. They split
    /// <c>log.md</c> on re-read: a newline lets an entry forge a fabricated
    /// <c>## &lt;date&gt;</c> heading or <c>* entry</c> bullet that a later
    /// <c>ChangeLog.Parse</c> reads back as genuine audit-trail history (§9).
    /// Rejected rather than stripped, so the caller learns the write did not
    /// happen.</item>
    /// <item>U+000C, U+0085, U+2028 and U+2029 — <b>FOLDED</b> to a space at
    /// the write (<see cref="FoldLogField"/>), not rejected. They cannot forge
    /// a §9 line, they only split a downstream renderer, and most callers
    /// cannot see them, so failing a call over one would cost more than it
    /// buys.</item>
    /// <item>Every other control character — <b>REJECTED</b>: ESC, backspace,
    /// BEL, VT, DEL and the rest of C0/C1. None has a legitimate use in a log
    /// entry, and each forges what a HUMAN reading the audit trail sees rather
    /// than what it says: <c>ESC[2K ESC[1A</c> rewrites the terminal line
    /// <c>cat log.md</c> just printed, backspace erases it. Folding them would
    /// silently rewrite the caller's words, and persisting them writes a
    /// rendering attack into the repository.</item>
    /// <item>Every bidirectional CONTROL character — <b>REJECTED</b>: U+061C,
    /// U+200E, U+200F, U+202A–U+202E and U+2066–U+2069
    /// (<see cref="IsBidiControl"/>). Each reorders the text a reader is shown
    /// without changing the bytes stored, so the <c>log.md</c> a human reads
    /// and the <c>log.md</c> a tool reads disagree. Ordinary right-to-left
    /// TEXT is NOT affected: Arabic and Hebrew letters carry their own strong
    /// direction and need none of these.</item>
    /// <item><c>\t</c> — <b>ACCEPTED, verbatim in the interior of the field.</b>
    /// The one control character a log message may legitimately carry. Not
    /// folded to a space the way the four soft separators are: this guard
    /// rewrites a caller's words only when the character would otherwise
    /// break structure, and an interior tab cannot — <c>ChangeLog.Parse</c>
    /// splits on LF, a tab is not a terminator, and it can never reach the
    /// START of a line (the renderer always emits <c>* </c> first), the only
    /// position where markdown would read it as an indented code block. A
    /// LEADING or TRAILING tab does not survive, though: <see cref="FoldLogField"/>
    /// trims the field before this guard or the writer ever sees it, exactly
    /// as it has always trimmed a leading/trailing space — so <c>"\tUpdate\t"</c>
    /// is written (and echoed) as <c>Update</c>, not <c>&lt;TAB&gt;Update&lt;TAB&gt;</c>.</item>
    /// </list>
    ///
    /// <para>The control-character half reuses
    /// <see cref="LineSafeText.ContainsControlCharacter"/> — the repo's shared
    /// predicate, already used at <c>okf_verify</c>'s <c>by</c> — rather than a
    /// second character list here, which is exactly the drift that predicate's
    /// own doc comment exists to prevent. Two adjustments are made to the value
    /// it sees, not to the predicate: it runs over the FOLDED text (by then the
    /// four soft separators are spaces, and the predicate would otherwise
    /// refuse U+2028/U+2029 and undo the fold), and over a probe in which tabs
    /// are spaces (the exemption above). The value WRITTEN keeps its tabs.</para>
    /// </summary>
    /// <param name="value">The argument's value.</param>
    /// <param name="fieldName">The argument's name, as it appears in the message.</param>
    private static string? GuardLogField(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"Error: invalid {fieldName} — it must not be empty.";
        }

        if (value.Contains('\0'))
        {
            return $"Error: invalid {fieldName} — it must not contain a null character.";
        }

        if (value.Contains('\n') || value.Contains('\r'))
        {
            return $"Error: invalid {fieldName} — it must not contain a line break (this would let it "
                + "forge fake '## date' or '* entry' lines in log.md).";
        }

        var folded = FoldLogField(value);
        var probe = folded.Replace('\t', ' ');

        if (LineSafeText.ContainsControlCharacter(probe))
        {
            return $"Error: invalid {fieldName} — it must not contain a control character other than a "
                + "tab (ESC, backspace and BEL forge what a human reading log.md sees).";
        }

        if (ContainsBidiControl(folded))
        {
            return $"Error: invalid {fieldName} — it must not contain a bidirectional control character "
                + "(U+061C, U+200E, U+200F, U+202A-U+202E, U+2066-U+2069 reorder the stored text on "
                + "display; ordinary right-to-left text needs none of them).";
        }

        return null;
    }

    /// <summary>
    /// True when the value carries any Unicode bidirectional CONTROL
    /// character — see <see cref="IsBidiControl"/> for the list and for why
    /// the whole class is refused rather than the two overrides alone.
    ///
    /// Local to this tool rather than added to
    /// <see cref="LineSafeText.ContainsControlCharacter"/>: none of these code
    /// points is a <see cref="char.IsControl(char)"/> character, so putting
    /// them there would widen the shared cross-assembly predicate that also
    /// gates §7 actors and <c>at</c> timestamps — a decision for those call
    /// sites, not one this tool may take on their behalf.
    /// </summary>
    /// <param name="value">The guarded argument's value.</param>
    private static bool ContainsBidiControl(string value)
    {
        foreach (var c in value)
        {
            if (IsBidiControl(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The twelve Unicode bidirectional control characters: the marks U+061C
    /// (ALM), U+200E (LRM) and U+200F (RLM); the embeddings and overrides
    /// U+202A (LRE), U+202B (RLE), U+202C (PDF), U+202D (LRO) and U+202E
    /// (RLO); and the isolates U+2066 (LRI), U+2067 (RLI), U+2068 (FSI) and
    /// U+2069 (PDI).
    ///
    /// <para>The WHOLE class, not just the two overrides: an embedding or an
    /// isolate reorders a rendered line as effectively as U+202E, a
    /// terminator can unbalance one an author opened, and a mark reorders a
    /// neutral run. A rule that stopped at "override" would refuse
    /// <c>RLO</c> and pass <c>RLE</c>, which is not a distinction any reader
    /// of a rendered <c>log.md</c> can see.</para>
    ///
    /// <para><b>One list, two call sites</b> — <see cref="ContainsBidiControl"/>
    /// (the write guard) and <see cref="NeedsVisibleEscape"/> (the read
    /// rendering). They must agree: a character the write refuses is exactly a
    /// character the read has to make visible when an older build, another
    /// producer or a human already put it in the file.</para>
    /// </summary>
    /// <param name="c">The character to classify.</param>
    private static bool IsBidiControl(char c) =>
        // Numeric constants on purpose: every one of these is invisible in
        // source, in a diff and in a review (the convention
        // Internal/LineSafeText.cs sets).
        c is (char)0x061C or (char)0x200E or (char)0x200F
            or (char)0x202A or (char)0x202B or (char)0x202C or (char)0x202D or (char)0x202E
            or (char)0x2066 or (char)0x2067 or (char)0x2068 or (char)0x2069;

    /// <summary>
    /// The four separators <see cref="GuardLogField"/> deliberately does NOT
    /// reject, folded to a single space before the value is written. Leading
    /// and trailing whitespace, tabs included, is removed first by the
    /// <see cref="string.Trim()"/> this method also does — the same trimming
    /// an ordinary leading/trailing space has always had, and the reason a
    /// tab is "verbatim" only in the interior of a field (see the <c>\t</c>
    /// item on <see cref="GuardLogField"/>).
    ///
    /// <para><b>Why the asymmetry.</b> <c>\n</c> and <c>\r</c> are REFUSED
    /// (<see cref="GuardLogField"/>): <c>ChangeLog.Parse</c> is LF-line-based,
    /// so those two are the characters that would make a later read back a
    /// forged <c>## date</c> heading or <c>* entry</c> bullet as genuine
    /// audit-trail history (§9), and a caller who sent one needs to learn the
    /// write did not happen. U+000C, U+0085, U+2028 and U+2029 cannot do that —
    /// the §9 parser does not split on them — but they DO split a downstream
    /// markdown or JavaScript renderer of the same file, and they were
    /// persisted verbatim into the user's repository, where every other
    /// consumer inherits them. Refusing them too would fail a call over a
    /// character most callers cannot see, so they are folded and the call
    /// succeeds. Same rule as the read side (<see cref="OneLine"/>): nothing
    /// downstream can start a new line.</para>
    ///
    /// <para><see cref="string.ReplaceLineEndings(string)"/> rather than a
    /// hand-rolled set: it is the same helper the rendering side uses, and by
    /// the time this runs <see cref="GuardLogField"/> has already refused the
    /// only two terminators it folds that we do not want folded silently.</para>
    /// </summary>
    /// <param name="value">The guarded argument's value.</param>
    private static string FoldLogField(string value) => value.Trim().ReplaceLineEndings(" ");

    /// <summary>
    /// Appends one entry to the bundle root's <c>log.md</c> under today's
    /// (UTC) ISO date, creating the file if it does not yet exist. If a
    /// heading for today's date already exists, the entry is appended to the
    /// end of that day's entries (days are newest-first by convention (§9),
    /// but entries within a day stay chronological). The read-modify-write is
    /// serialized under <see cref="_bundleLock"/> (shared with
    /// <see cref="WriteConcept"/> and <see cref="RegenerateIndexes"/>) so
    /// concurrent calls can't lose an update to each other. The existing file,
    /// if any, is read with the same strict-UTF-8 decoding <see cref="ChangesSince"/>
    /// uses, then re-rendered through <see cref="ChangeLog.ToMarkdown"/> — the
    /// strict §9 model — so any non-conforming prose or comments in a
    /// hand-authored <c>log.md</c> are not preserved. Never throws for
    /// expected errors (a <paramref name="kind"/> or <paramref name="text"/>
    /// that <see cref="GuardLogField"/> refuses — empty, or carrying a line
    /// break, a control character or a bidirectional control character — or a
    /// <c>log.md</c> that fails strict UTF-8 decoding) — those are reported as
    /// a plain-text message instead.
    /// </summary>
    /// <param name="kind">Entry kind, e.g. <c>Update</c> or <c>Creation</c>.</param>
    /// <param name="text">The entry text.</param>
    [Description("Append an entry to the bundle root log.md under today's date (ISO). Note: log.md is re-rendered through the strict §9 model, so non-conforming prose or comments in a hand-authored log.md are not preserved.")]
    public string AppendLog(
        [Description("Entry kind, e.g. 'Update' or 'Creation'.")] string kind,
        [Description("The entry text.")] string text)
    {
        if (GuardLogField(kind, "kind") is { } kindError)
        {
            return kindError;
        }

        if (GuardLogField(text, "text") is { } textError)
        {
            return textError;
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
            // BundleRoot itself -- the ancestor walk stops there
            // immediately without checking anything, which is why the file
            // node itself (not its ancestor chain) is the check that matters
            // here; both are included for the same defense-in-depth shape as
            // WriteConcept's guard. Both strict: a log.md whose link status
            // cannot be read is refused like a link (a guard fails closed --
            // see ReparsePoints.IsReparsePointOrUninspectable).
            if (ReparsePoints.IsReparsePointOrUninspectable(logPath) || ReparsePoints.HasReparsePointOrUninspectableAncestor(BundleRoot, BundleRoot))
            {
                return "Error: log.md is a reparse point (symlink/junction) or could not be inspected, not a regular file -- refusing to write through it.";
            }

            var today = UtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var foldedKind = FoldLogField(kind);
            var entry = new LogEntry(foldedKind, FoldLogField(text));

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
                    // New LogDay at the head: days are newest-first (§9).
                    days.Insert(0, new LogDay(today, [entry]));
                }

                BeforeLateReparseCheckForTest?.Invoke();

                // Late, best-effort re-check -- same TOCTOU gap as
                // BundleConceptWriter's own late re-check (see its remarks);
                // AppendLog has the identical validate-then-write shape
                // between the early check above (run before acquiring
                // _bundleLock) and the write below, just without an
                // intervening Directory.CreateDirectory call. log.md always
                // lives directly at BundleRoot, so the ancestor-walk half of
                // this re-check is a no-op here, same as the early check's
                // ancestor call above -- the check that matters is log.md
                // itself having been replaced with a reparse point in this
                // narrow window.
                var logParentDir = Path.GetDirectoryName(logPath);
                if ((!string.IsNullOrEmpty(logParentDir) && ReparsePoints.HasReparsePointOrUninspectableAncestor(BundleRoot, logParentDir))
                    || ReparsePoints.IsReparsePointOrUninspectable(logPath))
                {
                    return "Error: log.md resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.";
                }

                File.WriteAllText(logPath, new ChangeLog(changeLog.Title, days).ToMarkdown(), OkfEncodings.NoBom);
                _bundle = null;
            }

            // The FOLDED kind, not the raw argument: this message is itself a
            // line-structured tool result, and echoing the argument verbatim
            // would put back the separator the write just removed.
            return $"Appended a '{foldedKind}' entry under {today} in log.md.";
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
                // Same sink shape as AppendLogFileChanges's "## {rel}" and
                // "> Skipped {rel}": `rel` is a Path.GetRelativePath over a
                // bundle path (just above), and a directory name may carry a
                // line terminator. One index file, one "- " line.
                sb.Append("- ").Append(OneLine(rel)).Append('\n');
            }

            return sb.ToString();
        });
    }

    /// <summary>
    /// Validates the bundle against OKF v0.2 conformance (§11) and renders the
    /// report the same way the CLI's <c>validate</c> command does: one line
    /// per <see cref="Diagnostic"/> (via its own <see cref="Diagnostic.ToString"/>),
    /// then a summary line with the concept/error/warning/info counts and a
    /// conformant ✓/✗ verdict. Never throws for expected errors (a bundle
    /// that fails to (re)load) — reported as a plain-text message instead.
    /// </summary>
    [Description("Validate the bundle against OKF v0.2 conformance (§11). Returns the diagnostics report.")]
    public string ValidateBundle()
    {
        return RunTool(() =>
        {
            var bundle = GetBundle();
            var report = BundleValidator.Validate(bundle);

            var sb = new StringBuilder();
            foreach (var diagnostic in report.Diagnostics)
            {
                // One diagnostic, one line -- and a diagnostic message can
                // embed frontmatter (a resource path, a `type`), so the fold
                // is what makes that true. The CLI's own validate output is
                // golden-locked and is rendered elsewhere; this is the tool's
                // renderer only.
                sb.Append(OneLine(diagnostic.ToString())).Append('\n');
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
    /// Reads one §10 Attested Computation's contract and sanctioned
    /// computation source (§10.3: an inline <c># Computation</c> fence, or the
    /// text of a file resolved through <see cref="Bundle.TryResolveResource"/>),
    /// rendered as agent-friendly markdown. Always available — it is
    /// read-only and needs no attestation runtime (unlike
    /// <see cref="RunComputation"/>). Never throws for expected errors (a
    /// null/blank/malformed/unknown concept id, a concept that is not an
    /// Attested Computation, an unresolved or unreadable computation file, or
    /// a bundle that fails to (re)load) — those are reported as a plain-text
    /// message instead.
    /// </summary>
    /// <param name="conceptId">The concept id, e.g. <c>computations/monthly-revenue</c>.</param>
    [Description("Read an Attested Computation's §10 contract (runtime, parameters, executor, attester) and its sanctioned computation source (inline code, or the text of a referenced file).")]
    public string GetComputation([Description("The concept id, e.g. 'computations/monthly-revenue'.")] string conceptId)
    {
        if (GuardConceptId(conceptId) is { } err)
        {
            return err;
        }

        return RunTool(() =>
        {
            var bundle = GetBundle();
            if (!ConceptId.TryParse(conceptId, out var id) || bundle.Get(id) is not { } concept)
            {
                return ConceptNotFoundMessage(conceptId);
            }

            var fm = concept.Document.Frontmatter;
            if (!fm.IsAttestedComputation)
            {
                // OneLine on `type`: it is frontmatter, so it can be a block
                // scalar. `conceptId` is the caller's own argument, already
                // rejected upstream if it carries a control character.
                return $"Concept '{conceptId}' is not an Attested Computation (type: {OneLine(fm.Type ?? "(none)")}).";
            }

            var sb = new StringBuilder();
            sb.Append("# Computation: ").Append(id).Append('\n').Append('\n');
            AppendContractSummary(sb, fm.ComputationContract);
            sb.Append('\n').Append("## Source").Append('\n');

            var computation = concept.Document.Computation();
            switch (computation)
            {
                case { Source: ComputationSource.File, Path.Length: > 0 } file:
                    if (!bundle.TryResolveResource(concept, file.Path!, out var absolutePath, out var status)
                        || status != ResourceResolutionStatus.Resolved)
                    {
                        // OneLine on file.Path here and on the two lines below:
                        // it is the frontmatter `computation` field, so a block
                        // scalar can put a line break in the middle of an
                        // "Error: "/"File: " line. `status` is an enum.
                        sb.Append("Error: computation file '").Append(OneLine(file.Path!)).Append("' could not be resolved (").Append(status).Append(").\n");
                        break;
                    }

                    string text;
                    try
                    {
                        // Guarded: TryResolveResource only establishes path
                        // safety, not readability -- the file may still fail
                        // to read (I/O error, or non-UTF-8 content), same
                        // lesson as AttestationOrchestrator.RunAsync's own
                        // file-computation step.
                        text = bundle.ReadResourceText(absolutePath!);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException)
                    {
                        sb.Append("Error: computation file '").Append(OneLine(file.Path!)).Append("' could not be read: ").Append(OneLine(e.Message)).Append('\n');
                        break;
                    }

                    sb.Append("File: ").Append(OneLine(file.Path!)).Append('\n').Append('\n');
                    sb.Append("```\n").Append(text.TrimEnd('\n')).Append('\n').Append("```\n");
                    break;

                case { Source: ComputationSource.Inline, InlineCode.Length: > 0 } inline:
                    sb.Append("```\n").Append(inline.InlineCode!.TrimEnd('\n')).Append('\n').Append("```\n");
                    break;

                default:
                    sb.Append(NoneLine).Append('\n');
                    break;
            }

            return sb.ToString();
        });
    }

    /// <summary>
    /// Runs the §10.5 attested-computation workflow (load → resolve
    /// computation → resolve runtime → validate parameters → bind → execute →
    /// validate receipt shape → attest → gate on staleness) for one concept,
    /// via the <see cref="AttestationOrchestrator"/> this tool set was
    /// constructed with (see <see cref="OkfBundleTools(string, AttestationOrchestrator?)"/>),
    /// and renders the resulting <see cref="AttestationOutcome"/> as
    /// agent-friendly markdown. If no orchestrator was wired, returns a
    /// plain-text error rather than being omitted silently (mirroring
    /// <see cref="GetTools()"/>, which omits <c>okf_run_computation</c>
    /// entirely in that case — this direct-call path exists for callers that
    /// invoke the method itself rather than through the tool list). Synchronous
    /// like every other tool method here: the orchestrator's async workflow is
    /// awaited to completion at this boundary. Never throws for expected
    /// errors (a null/blank/malformed concept id, or any §10.5 failure the
    /// orchestrator reports as a non-displayable <see cref="AttestationOutcome"/>)
    /// — those are reported as plain text (an <c>Error: ...</c> message, or an
    /// outcome whose <c>displayable: no</c>) instead.
    /// </summary>
    /// <param name="conceptId">The Attested Computation concept id to run.</param>
    /// <param name="parameterValues">
    /// The parameter values for this run (§10.3: values only, never
    /// computation code). A <see langword="null"/> value — reachable despite
    /// the non-nullable static type when a host/LLM binds the call with the
    /// property omitted — is treated as an empty dictionary rather than
    /// dereferenced, so a computation with no required parameters still runs,
    /// and one that does simply degrades to the orchestrator's normal
    /// "missing required parameter" non-displayable outcome instead of
    /// throwing.
    /// </param>
    [Obsolete("Use RunComputationAsync: this overload blocks the calling thread and passes no cancellation token, so a slow or wedged host runtime pins the caller with no way out. Kept for one version.")]
    [Description("Run an Attested Computation (§10.5: bind, execute, attest, gate on staleness) via the configured attestation runtime, and return the resulting outcome (displayable, verdict, receipt, reasons).")]
    public string RunComputation(
        [Description("The concept id, e.g. 'computations/monthly-revenue'.")] string conceptId,
        [Description("Parameter values for this run, by name (§10.3: values only, never computation code).")] IReadOnlyDictionary<string, object?> parameterValues)
    {
        if (GuardConceptId(conceptId) is { } err)
        {
            return err;
        }

        if (_orchestrator is null)
        {
            return "Error: no attestation runtime configured.";
        }

        // A reflection/AIFunction-bound call can pass null here despite the
        // non-nullable static type (same convention as the conceptId guards
        // above) -- e.g. a host/LLM that omits the parameterValues property
        // entirely. Without this guard, AttestationOrchestrator.RunAsync's
        // own required-parameter gate (parameterValues.ContainsKey(...))
        // would throw a NullReferenceException that RunTool's catch filter
        // does not cover, breaking the "tools never throw toward the LLM"
        // invariant. Treating null as "no values supplied" lets the
        // orchestrator's existing missing-required-parameter handling take
        // over instead.
        parameterValues ??= new Dictionary<string, object?>();

        return RunComputationAsync(conceptId, parameterValues).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The §10.5 attested-computation workflow, asynchronous and cancellable —
    /// the form <see cref="GetTools()"/> exposes as <c>okf_run_computation</c>.
    ///
    /// This is the only tool here that hands control to host-plugged code
    /// (<c>IParameterBinder</c>/<c>IComputationExecutor</c>/<c>IAttester</c>),
    /// which may do real I/O of unbounded duration. The synchronous
    /// <see cref="RunComputation"/> blocked its thread and passed no token at
    /// all, so a slow or wedged executor pinned an Agent Framework worker with
    /// no way out.
    ///
    /// <paramref name="cancellationToken"/> is bound automatically by
    /// <c>AIFunctionFactory</c> and excluded from the generated JSON schema, so
    /// taking it changes nothing the model sees. It is combined with
    /// <see cref="ComputationTimeout"/>, so a host that never cancels still has
    /// a floor.
    ///
    /// Never throws for expected errors, like every tool here — with one
    /// deliberate exception: a cancellation the CALLER requested propagates as
    /// an <see cref="OperationCanceledException"/>, because a caller that
    /// withdrew is not waiting for a rendered answer. A timeout is not that: it
    /// is this tool's own decision, so it is reported as a normal
    /// non-displayable outcome rather than raised at a caller who asked for
    /// nothing of the sort.
    /// </summary>
    /// <param name="conceptId">The Attested Computation concept id to run.</param>
    /// <param name="parameterValues">The parameter values for this run (§10.3: values only, never computation code).</param>
    /// <param name="cancellationToken">The host's token; combined with <see cref="ComputationTimeout"/>.</param>
    [Description("Run an Attested Computation (§10.5: bind, execute, attest, gate on staleness) via the configured attestation runtime, and return the resulting outcome (displayable, verdict, receipt, reasons).")]
    public async Task<string> RunComputationAsync(
        [Description("The concept id, e.g. 'computations/monthly-revenue'.")] string conceptId,
        [Description("Parameter values for this run, by name (§10.3: values only, never computation code).")] IReadOnlyDictionary<string, object?> parameterValues,
        CancellationToken cancellationToken = default)
    {
        if (GuardConceptId(conceptId) is { } err)
        {
            return err;
        }

        if (_orchestrator is null)
        {
            return "Error: no attestation runtime configured.";
        }

        // See RunComputation's remarks: an AIFunction-bound call can pass null
        // despite the non-nullable static type. And what it does pass is a
        // dictionary of JsonElements, never native values -- normalized here,
        // once, for every binder (see ParameterValues). A value that breaks the
        // strict JSON contract (an inexact number, a nested duplicate property)
        // is the tool's error text, never a throw toward the model.
        if (!ParameterValues.TryNormalize(parameterValues ?? new Dictionary<string, object?>(), out var normalizedValues, out var valuesError))
        {
            return $"Error: {valuesError}";
        }

        parameterValues = normalizedValues;

        // Arming the timeout is validated rather than left to
        // CancellationTokenSource's own throw: it happens outside the try
        // below, so a host that misconfigured the timeout got a raw exception
        // out of a tool that promises never to throw at the LLM — on a
        // misconfiguration, which is exactly when a legible message is worth
        // most. Caught in review of #65.
        //
        // Two guards, for two different reasons. This one is not about the
        // throw at all, but about two settings the runtime silently ACCEPTS and
        // turns into nonsense: a negative delay between -1ms and 0 is treated
        // as no timeout, so a fat-fingered negative ceiling silently produces
        // an unbounded run — the opposite of what it asked for — while zero
        // fires the token immediately, so every single computation reports
        // "timed out after 0s". The bound is `<=` for that second case; it was
        // `<`, which let zero through even though this message already said
        // "must be positive" (caught in review).
        if (ComputationTimeout <= TimeSpan.Zero && ComputationTimeout != Timeout.InfiniteTimeSpan)
        {
            return $"Error: ComputationTimeout must be positive or Timeout.InfiniteTimeSpan, but is {ComputationTimeout}.";
        }

        // And this one is about the throw, on every bound the runtime enforces
        // rather than only the negative one the guard above was written for: a
        // delay past uint.MaxValue - 1 milliseconds (~49.71 days) is rejected
        // too, so `ComputationTimeout = TimeSpan.FromDays(60)` blew exactly the
        // ArgumentOutOfRangeException the negative case used to. Caught by
        // catching what the runtime actually rejects rather than mirroring its
        // limits in a constant here, which would go stale the moment they move.
        using var timeoutSource = new CancellationTokenSource();
        try
        {
            timeoutSource.CancelAfter(ComputationTimeout);
        }
        catch (ArgumentOutOfRangeException)
        {
            return $"Error: ComputationTimeout is out of the range the runtime accepts (at most about 49.7 days, or Timeout.InfiniteTimeSpan), but is {ComputationTimeout}.";
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            var outcome = await _orchestrator
                .RunAsync(GetBundle(), ConceptId.Parse(conceptId), parameterValues, cancellationToken: linked.Token)
                .ConfigureAwait(false);
            return FormatOutcome(outcome);
        }
        // Ours, not the caller's: the token fired because ComputationTimeout
        // elapsed while the caller's own token is still fine. Tell the model,
        // rather than throwing at a caller who never asked to stop.
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return $"displayable: no\n\nReasons:\n- the computation timed out after {ComputationTimeout.TotalSeconds:0.###}s\n";
        }
        catch (Exception ex) when (ex is OkfException or ArgumentException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return $"Error: {ex.Message}";
        }
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
            // `rel` is a path on disk, not text this process wrote: a POSIX
            // filename may hold a line terminator, and (as of the post-audit
            // re-review's Important 1) any of the other filesystems here
            // accepts a bidi control in a directory name too, with no
            // privilege needed. It goes through `OneLineLogText`, not
            // `OneLine` alone, for the same reason entry text does — folding
            // the four soft separators answers "nothing downstream can start
            // a new line", not "nothing can reorder what a human is shown".
            // `SkipReason` is a fixed library word.
            notes.Append("> Skipped ").Append(OneLineLogText(rel)).Append(" (could not be read: ")
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

        changes.Append("## ").Append(OneLineLogText(rel)).Append('\n');
        foreach (var day in matchingDays)
        {
            AppendLogDay(changes, day);
        }

        changes.Append('\n');
        return true;
    }

    /// <summary>
    /// Appends one <c>### {date}</c> section and its bulleted entries (bold
    /// <c>Kind</c> when present) to <paramref name="sb"/>.
    ///
    /// <para><b>The entries are folded even though a <c>\n</c> cannot reach
    /// them.</b> <see cref="ChangeLog.Parse"/> is LF-line-based, so a literal
    /// newline never survives into <c>Kind</c> or <c>Text</c> — which is
    /// exactly why this sink went unnoticed. The SOFT terminators do survive:
    /// U+2028, U+2029, U+0085 and U+000C are ordinary characters to an
    /// LF-based parser and line breaks to the markdown and JavaScript
    /// splitters downstream, and a <c>log.md</c> bullet carrying one printed a
    /// second, forged <c>- **Update**: …</c> bullet (measured). That is the
    /// whole reason <see cref="OneLine"/> is
    /// <see cref="string.ReplaceLineEndings(string)"/> rather than a
    /// <c>\r</c>/<c>\n</c> pass, so the rule applies here like everywhere else
    /// — "a literal newline cannot get in" is not the test; "nothing
    /// downstream can start a new line" is.</para>
    ///
    /// <para>Folding is not the whole job, though. It answers "nothing
    /// downstream can start a new line"; it says nothing about ESC, backspace
    /// or a bidi control, which a <c>log.md</c> written by an older build, by
    /// another producer or by hand may carry and which forge what a HUMAN
    /// reading this report sees. Both fields therefore go through
    /// <see cref="OneLineLogText"/>, which folds AND names each such character
    /// visibly as <c>&lt;U+XXXX&gt;</c> — see that method for why a read
    /// escapes where the write (<see cref="GuardLogField"/>) refuses.</para>
    ///
    /// <para><c>day.Date</c> needs neither: only dates matching
    /// <see cref="ChangeLog.IsIsoDate"/> reach here, and that grammar admits
    /// digits and hyphens only.</para>
    /// </summary>
    private static void AppendLogDay(StringBuilder sb, LogDay day)
    {
        sb.Append("### ").Append(day.Date).Append('\n');
        foreach (var entry in day.Entries)
        {
            if (entry.Kind is not null)
            {
                sb.Append("- **").Append(OneLineLogText(entry.Kind)).Append("**: ").Append(OneLineLogText(entry.Text)).Append('\n');
            }
            else
            {
                sb.Append("- ").Append(OneLineLogText(entry.Text)).Append('\n');
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

    /// <summary>
    /// Renders the 20 shown search results as markdown, with the total match count. The 20 are
    /// picked from <paramref name="scored"/> by <see cref="ConceptSearch.TopDiversified"/>, not off
    /// the front of it: the best-scoring hit is first, the rest are spread across top-level id
    /// families, and the printed scores therefore do not descend monotonically.
    /// Each hit is annotated with a trailing <c>[deprecated]</c> marker when its lifecycle status is
    /// <see cref="ConceptStatus.Deprecated"/> and/or a <c>[stale]</c> marker when it is stale as of
    /// <paramref name="now"/>.
    /// </summary>
    private static string FormatSearchResults(string query, string? tag, IReadOnlyList<ScoredConcept> scored, DateTimeOffset now)
    {
        const int MaxResults = 20;

        // Diversified rather than a plain Take: on a bundle carrying a generated
        // `code/` subtree, the members otherwise fill all 20 slots before any
        // curated concept is reached — measured at 0 curated results in the top
        // 20 for broad queries (design §8.7).
        var shown = ConceptSearch.TopDiversified(scored, MaxResults);

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
            // DisplayTitle folds it: `title` comes from frontmatter, where a
            // `|` block scalar carries line breaks perfectly legally, and one
            // result is one "* " line here. (The excerpt below needs no such
            // call -- ConceptSearch.Excerpt returns a single split line by
            // construction -- and `query`/`tag` above come from the caller,
            // not from the bundle.)
            var title = DisplayTitle(concept, concept.Id.ToString());
            var lc = concept.Document.Frontmatter.Lifecycle;
            sb.Append("* ").Append(concept.Id).Append(" — ").Append(title).Append(" (").Append(score).Append(')');
            if (lc.Status == ConceptStatus.Deprecated)
            {
                sb.Append(" [deprecated]");
            }

            if (lc.IsStale(now))
            {
                sb.Append(" [stale]");
            }

            sb.Append('\n');

            var excerpt = ConceptSearch.Excerpt(concept.Document.Body, query);
            if (excerpt is not null)
            {
                sb.Append("  ").Append(excerpt).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders an audit for an agent: the same shape as the CLI's report form,
    /// minus the bundle line (the tool is bound to one bundle) and bounded to
    /// 20 findings. Deliberately not shared with the CLI renderer, whose bytes
    /// are golden-locked and must not move when this string is tuned.
    /// </summary>
    /// <param name="report">The audit report to render.</param>
    /// <param name="staleOnly">
    /// Whether the selection IS the stale worklist (the tool's <c>stale</c>
    /// parameter). When true, the worklist heading reads <c>needs attention</c>
    /// -- otherwise, the selection was narrowed or widened by other filters, so
    /// the neutral <c>selected</c> heading is used instead: calling every
    /// selected concept a concept that "needs attention" would misstate a
    /// selection like <c>stale: false</c>, which can include perfectly fresh
    /// concepts.
    /// </param>
    private static string RenderAudit(AuditReport report, bool staleOnly)
    {
        const int MaxResults = 20;
        var sw = new StringWriter();

        // Same summary bytes as the CLI renderer, via the shared AuditText --
        // the two renderers stay separate on purpose (the CLI's bytes are
        // golden-locked), but the vocabulary/summary text is not spelled
        // twice.
        AuditText.WriteSummary(sw, report);
        var sb = sw.GetStringBuilder();

        var heading = staleOnly ? "needs attention" : "selected";

        if (report.Findings.Count == 0)
        {
            sb.Append($"\n{heading}: none\n");
            return sb.ToString();
        }

        sb.Append($"\n{heading} ({report.Findings.Count}):\n");
        foreach (var finding in report.Findings.Take(MaxResults))
        {
            sb.Append("  ").Append(AuditText.FormatFinding(finding)).Append('\n');
        }

        if (report.Findings.Count > MaxResults)
        {
            sb.Append($"… and {report.Findings.Count - MaxResults} more (narrow with stale/trust/status/type)\n");
        }

        return sb.ToString();
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
                // The id is already printed beside it here, so the fallback is
                // the last segment rather than the whole id. AppendSection
                // folds its bullets too; going through DisplayTitle keeps the
                // derivation itself in one place.
                .Select(c => $"{c.Id} — {DisplayTitle(c, c.Id.Name)}");
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

    /// <summary>
    /// <b>The one rule for untrusted text in a line-oriented tool result:</b>
    /// every line terminator collapses to a single space, so a value that a
    /// bundle, a container or a warehouse authored can occupy exactly the one
    /// line the renderer gave it and cannot forge a second.
    ///
    /// <para><see cref="string.ReplaceLineEndings(string)"/> and not a hand-rolled
    /// <c>\r</c>/<c>\n</c> pass: it also folds <c>FF</c>, <c>NEL</c> (U+0085)
    /// and <c>LS</c>/<c>PS</c> (U+2028/U+2029), which a markdown or JavaScript
    /// line splitter downstream may well treat as terminators even though
    /// <see cref="char.IsControl(char)"/> does not classify the last two.
    /// It is the same neutralisation the orchestrator's <c>RunStageAsync</c>
    /// and <see cref="FormatOutcome"/>'s <c>Error:</c> line already applied,
    /// promoted here so the remaining sinks cannot keep diverging one at a
    /// time (they did: the verdict detail, the reason lines, the receipt keys
    /// and the receipt string values were each left raw by a change that fixed
    /// one of the others).</para>
    ///
    /// <para><b>What it is not.</b> Nothing else is escaped: a value keeps its
    /// <c>-</c>, <c>#</c> and backticks, because the alternative — escaping
    /// markdown — would hide the data the model is meant to read, and forging
    /// a BULLET or a HEADING is what needs a line break of its own to begin
    /// with. Collections already arrive as JSON (which escapes its own line
    /// endings), so they pass through this unchanged.</para>
    /// </summary>
    /// <param name="value">Text from outside this library: a bundle, a receipt, an attester.</param>
    private static string OneLine(string value) => value.ReplaceLineEndings(" ");

    /// <summary>
    /// <see cref="OneLine"/>, plus every REMAINING control character and every
    /// bidi control rendered visibly as <c>&lt;U+XXXX&gt;</c>: the read-side
    /// counterpart of <see cref="GuardLogField"/>, for <c>log.md</c> text this
    /// process did not write.
    ///
    /// <para><b>Why escape rather than strip, fold or refuse.</b> A read has
    /// nothing to refuse — the file exists, and its entries are a human's
    /// words. Stripping or folding would silently delete what someone wrote,
    /// which an audit trail's own reader must never do. Naming each character
    /// instead keeps every word intact, makes the line inert (nothing left can
    /// reorder the display or drive a terminal), and is the form a reader can
    /// ACT on: <c>&lt;U+202E&gt;</c> in a rendered entry says exactly what is
    /// in the file and that someone put it there, where a dropped character
    /// would have said nothing at all.</para>
    ///
    /// <para>TAB is exempt on both sides (see <see cref="GuardLogField"/>):
    /// the write accepts it verbatim, so the read must not disfigure it.
    /// U+000C/U+0085/U+2028/U+2029 never reach the escaper as themselves —
    /// <see cref="OneLine"/> has already folded them to spaces, which is the
    /// established rule for this sink; everything the fold leaves behind is a
    /// character that has no business in a rendered line at all.</para>
    ///
    /// <para>Scoped to the <c>log.md</c> readers this round — which, as of
    /// the post-audit re-review's Important 1, includes the relative path
    /// <see cref="AppendLogFileChanges"/> prints in its own <c>## {path}</c>
    /// heading and <c>&gt; Skipped {path}</c> note: a path is text this
    /// library did not write either, so it needs the same treatment as entry
    /// content, not a new escaper. The same exposure still exists wherever
    /// <see cref="OneLine"/> ALONE renders text this library did not write
    /// (concept bodies, titles, receipt values, and other paths such as
    /// <see cref="GetComputation"/>'s computation-file path) — folding a line
    /// terminator was never a claim about ESC. Widening it further is a
    /// separate decision, not an oversight here.</para>
    /// </summary>
    /// <param name="value">Text read back out of a <c>log.md</c>.</param>
    private static string OneLineLogText(string value)
    {
        var folded = OneLine(value);
        if (!folded.Any(NeedsVisibleEscape))
        {
            return folded;
        }

        var sb = new StringBuilder(folded.Length + 8);
        foreach (var c in folded)
        {
            if (NeedsVisibleEscape(c))
            {
                sb.Append("<U+").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('>');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// The read-side twin of <see cref="GuardLogField"/>'s two rejections: a
    /// control character or a bidi control, TAB excepted exactly as the write
    /// excepts it. <see cref="IsBidiControl"/> is the same single list both
    /// sides read, so the two cannot drift.
    /// </summary>
    /// <param name="c">The character to classify.</param>
    private static bool NeedsVisibleEscape(char c) =>
        c != '\t' && (char.IsControl(c) || IsBidiControl(c));

    /// <summary>
    /// A concept's display title — its frontmatter <c>title</c>, or
    /// <paramref name="fallback"/> (always derived from the already-validated
    /// <see cref="ConceptId"/>) when it has none — folded by
    /// <see cref="OneLine"/>.
    ///
    /// <para><b>One call site for the one frontmatter field three different
    /// renderers print into a line of their own structure</b>: this method's
    /// <c>okf_read_concept</c> H1, <see cref="FormatSearchResults"/>'s
    /// <c>* </c> lines and <see cref="BuildLevelListing"/>'s concept list. Two
    /// of the three folded it and the H1 did not, and a <c>title: |</c> block
    /// scalar there printed a complete, forged <c>## Backlinks</c> section —
    /// with a bullet — ABOVE the real one, in the same result. Three copies of
    /// one derivation is what let that happen, so there is now one.</para>
    /// </summary>
    /// <param name="concept">The concept whose title is being rendered.</param>
    /// <param name="fallback">What to print when the concept declares no <c>title</c>.</param>
    private static string DisplayTitle(Concept concept, string fallback) =>
        OneLine(concept.Document.Frontmatter.Title ?? fallback);

    /// <summary>
    /// Appends a markdown <c>## </c>-heading section, one bullet per line, or
    /// <see cref="NoneLine"/> if empty. Every bullet goes through
    /// <see cref="OneLine"/>: the bullets carry bundle text (a concept title,
    /// a raw link target) and orchestrator <c>Reasons</c> (which embed an
    /// attester's verdict detail), and "one bullet per line" is a promise this
    /// method makes, so it is this method that keeps it.
    /// </summary>
    private static void AppendSection(StringBuilder sb, string heading, IEnumerable<string> lines)
    {
        sb.Append("## ").Append(heading).Append('\n');
        var any = false;
        foreach (var line in lines)
        {
            any = true;
            sb.Append("- ").Append(OneLine(line)).Append('\n');
        }

        if (!any)
        {
            sb.Append(NoneLine).Append('\n');
        }
    }

    /// <summary>
    /// Appends the concept's frontmatter as one <c>key: value</c> line per
    /// entry. Both sides go through <see cref="OneLine"/>: a <c>|</c> block
    /// scalar is perfectly legal YAML, and a line break in one of these values
    /// printed what looked like another frontmatter ENTRY -- a bundle could
    /// show a <c>type:</c> or <c>verified:</c> line it does not actually carry.
    /// Folding a genuinely multi-line value (typically a <c>description</c>)
    /// onto one line is the accepted cost, and is what a <c>&gt;</c> folded
    /// scalar would have rendered anyway.
    /// </summary>
    private static void AppendFrontmatterBlock(StringBuilder sb, Frontmatter frontmatter)
    {
        var map = frontmatter.AsMapping();
        if (map.IsEmpty)
        {
            return;
        }

        foreach (var key in map.Keys)
        {
            sb.Append(OneLine(key)).Append(": ").Append(OneLine(FormatFrontmatterValue(map.Get(key)))).Append('\n');
        }

        sb.Append('\n');
    }

    /// <summary>
    /// Appends a compact <c>## Contract</c> markdown block for a §10.2
    /// <see cref="AttestedComputationContract"/>: <c>runtime</c>, the
    /// <c>parameters</c> list (name, type, required), the <c>computation</c>
    /// field (a file path, or <c>(inline)</c> when the sanctioned computation
    /// is an inline fence), and the <c>executor</c>/<c>attester</c> resources.
    /// Shared by <see cref="GetComputation"/>'s full rendering and
    /// <see cref="ReadConcept"/>'s compact enrichment, so the two summaries
    /// can never drift apart.
    ///
    /// <para><b>Every value here comes out of frontmatter, so every one of them
    /// goes through <see cref="OneLine"/>.</b> Each is a §10.2 field a bundle
    /// author writes, and YAML lets any of them be a <c>|</c> block scalar: a
    /// newline in <c>runtime</c> or <c>attester.resource</c> forged a second
    /// <c>- </c> line in a block whose whole job is to tell a model what will be
    /// run and what will vouch for it. Same rule and same reason as
    /// <see cref="FormatOutcome"/>'s — see <see cref="OneLine"/>. The literals
    /// around them (<see cref="NoneLine"/>, <c>(inline)</c>, <c>(unnamed)</c>,
    /// <c>[required]</c>) are this library's own and need no call.</para>
    /// </summary>
    private static void AppendContractSummary(StringBuilder sb, AttestedComputationContract contract)
    {
        sb.Append("## Contract").Append('\n');
        sb.Append("- runtime: ").Append(OneLine(contract.Runtime ?? NoneLine)).Append('\n');

        if (contract.Parameters.Count == 0)
        {
            sb.Append("- parameters: ").Append(NoneLine).Append('\n');
        }
        else
        {
            sb.Append("- parameters:").Append('\n');
            foreach (var parameter in contract.Parameters)
            {
                sb.Append("  - ").Append(parameter.Name.Length == 0 ? "(unnamed)" : OneLine(parameter.Name));
                if (parameter.Type is not null)
                {
                    sb.Append(" (").Append(OneLine(parameter.Type)).Append(')');
                }

                if (parameter.Required)
                {
                    sb.Append(" [required]");
                }

                sb.Append('\n');
            }
        }

        sb.Append("- computation: ").Append(string.IsNullOrEmpty(contract.ComputationPath) ? "(inline)" : OneLine(contract.ComputationPath)).Append('\n');

        sb.Append("- executor: ");
        if (contract.Executor is { } executor)
        {
            // The joined list, not each item: a newline inside ONE receipt
            // field name breaks the line just as a newline between two would,
            // and folding after the join covers both with one call.
            sb.Append(OneLine(executor.Resource ?? NoneLine))
              .Append(" (receipt: ")
              .Append(executor.Receipt.Count == 0 ? NoneLine : OneLine(string.Join(", ", executor.Receipt)))
              .Append(')');
        }
        else
        {
            sb.Append(NoneLine);
        }

        sb.Append('\n');
        sb.Append("- attester: ").Append(OneLine(contract.Attester?.Resource ?? NoneLine)).Append('\n');
    }

    /// <summary>
    /// Renders an <see cref="AttestationOutcome"/> (§10.5's gated result) as
    /// agent-friendly markdown for <see cref="RunComputation"/>: whether it is
    /// <c>displayable</c>, the attester's verdict, staleness, whether the
    /// receipt shape matched the contract's declared fields, the receipt's own
    /// fields, and every reason (if any) that kept the run from being
    /// displayable, plus a captured binder/executor/attester exception's
    /// message, if any.
    ///
    /// <para><b>Every value here that this library did not author goes through
    /// <see cref="OneLine"/>.</b> §10.5 step 6 makes the <c>displayable</c> and
    /// <c>verdict</c> lines the gate the model reads, and the attester's
    /// verdict detail, the orchestrator's reason lines and the receipt's own
    /// keys and string values are all authored outside it — by a bundle's
    /// attester script, by a container, by a warehouse. A single line break in
    /// any of them printed a second, forged <c>- displayable: yes</c> right
    /// under the real <c>- displayable: no</c>.</para>
    /// </summary>
    private static string FormatOutcome(AttestationOutcome outcome)
    {
        var sb = new StringBuilder();
        sb.Append("# Attestation outcome").Append('\n').Append('\n');
        sb.Append("- displayable: ").Append(outcome.Displayable ? "yes" : "no").Append('\n');

        sb.Append("- verdict: ");
        if (outcome.Verdict is { } verdict)
        {
            sb.Append(verdict.Passed ? "passed" : "failed");
            if (!string.IsNullOrEmpty(verdict.Detail))
            {
                // The attester authored this, and an attester is a script a
                // bundle names: a newline here forged a second "- displayable:
                // yes" line one line below the real "- displayable: no". See
                // OneLine.
                sb.Append(" (").Append(OneLine(verdict.Detail)).Append(')');
            }
        }
        else
        {
            sb.Append(NoneLine);
        }

        sb.Append('\n');
        sb.Append("- stale: ").Append(StaleLabel(outcome.Stale)).Append('\n');
        sb.Append("- receipt shape ok: ").Append(outcome.ReceiptShapeOk ? "yes" : "no").Append('\n');

        if (outcome.Receipt is { } receipt && receipt.Fields.Count > 0)
        {
            sb.Append("- receipt:").Append('\n');
            foreach (var (key, value) in receipt.Fields)
            {
                // Both the field NAME and its value come from whatever the
                // executor returned -- a JSON object key can hold a newline as
                // readily as a string value can. See OneLine.
                sb.Append("  - ").Append(OneLine(key)).Append(": ").Append(FormatReceiptValue(value)).Append('\n');
            }
        }
        else
        {
            sb.Append("- receipt: ").Append(NoneLine).Append('\n');
        }

        if (outcome.Reasons.Count > 0)
        {
            sb.Append('\n');
            AppendSection(sb, "Reasons", outcome.Reasons);
        }

        if (outcome.Error is not null)
        {
            // The TYPE, never the message, for a foreign exception: it comes from
            // a host-plugged runtime -- code this library does not control -- and
            // its message can name a connection string, a query, or the row it
            // choked on. An AttestationDiagnosticException's message, by
            // contrast, was authored by an OKF4net component (see that type's
            // remarks) and is safe to render here, same as the orchestrator
            // already renders it into Reasons. The exception object stays on
            // outcome.Error for the host either way.
            //
            // OneLine, matching the orchestrator's own RunStageAsync: this text
            // lands in the same agent-facing markdown blob as Reasons (see this
            // method's doc comment), and a diagnostic message CAN carry an
            // embedded newline -- e.g. a ContainerExecutionException whose
            // message interpolates a downstream JsonException.Message built
            // from bundle-influenced stdout (Internal/ReceiptParsing.cs). Left
            // unneutralized here, an untrusted newline could spoof extra "- "
            // bullet lines or section headers in the rendered output.
            var errorLine = outcome.Error is AttestationDiagnosticException diagnostic
                ? $"{diagnostic.GetType().Name}: {OneLine(diagnostic.Message)}"
                : outcome.Error.GetType().Name;
            sb.Append('\n').Append("Error: ").Append(errorLine).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a receipt value the way JSON renders it, not the way
    /// <see cref="object.ToString()"/> does: a string prints as itself, a
    /// boolean prints lowercase (<c>true</c>/<c>false</c>, not the CLR
    /// <c>True</c>/<c>False</c>), a number prints under the invariant
    /// culture (never the current thread's -- a French decimal comma handed
    /// to a model reading a receipt is a value that gets re-parsed wrong),
    /// and a list or map (what <c>ReceiptParsing</c>/<c>JsonValues.Normalize</c>
    /// produce for a JSON array/object) prints as compact JSON, because
    /// <c>List&lt;object&gt;</c>'s type name tells the model nothing about
    /// the rows the computation returned. This deliberately changes how a
    /// bool- or double-valued field printed before this method existed.
    /// </summary>
    private static string FormatReceiptValue(object? value) => value switch
    {
        null => NoneLine,
        // OneLine: a scalar string is the one arm that carries warehouse or
        // script text verbatim -- the JSON arm below escapes its own line
        // endings, and no other arm can produce one.
        string s => OneLine(s),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };

    private static string StaleLabel(StaleState stale) => stale switch
    {
        StaleState.Fresh => "fresh",
        StaleState.Stale => "stale",
        _ => "unknown",
    };

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
    /// The common conceptId guard shared by <see cref="ReadConcept"/>,
    /// <see cref="GetComputation"/> and <see cref="RunComputation"/>: blank
    /// (or <c>null</c>) is "not found", an embedded null character is
    /// rejected outright. Returns the error message to return verbatim, or
    /// <c>null</c> if <paramref name="conceptId"/> is fit to parse.
    /// </summary>
    private static string? GuardConceptId(string? conceptId)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return ConceptNotFoundMessage(conceptId ?? string.Empty);
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        return null;
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
