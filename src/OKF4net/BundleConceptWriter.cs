// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Globalization;
using OKF4net.Internal;
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>One concept stamped by <see cref="BundleConceptWriter.RecordVerifications"/>.</summary>
/// <param name="ConceptId">The concept that was stamped.</param>
/// <param name="At">
/// The timestamp written. Callers could format their own — the CLI and the
/// Agents layer both see <c>OkfTimestamp</c> through <c>InternalsVisibleTo</c> —
/// but two clocks are one too many: only the writer holds the seam tests pin,
/// so it reports what it wrote.
/// </param>
/// <param name="ReplacedAt">The superseded <c>at</c>, or null when the stamp is new.</param>
public readonly record struct VerificationRecord(string ConceptId, string At, string? ReplacedAt);

/// <summary>
/// The outcome of <see cref="BundleConceptWriter.RecordVerifications"/>:
/// errors-as-data, never thrown.
///
/// <b>Read <see cref="Records"/>, not just <see cref="Recorded"/>.</b> Every
/// concept is validated before the first byte is written, so a rejected batch
/// — unknown id, malformed actor, non-conformant document — writes nothing.
/// But writing several files cannot be atomic: if the third write fails on
/// I/O, the first two are already on disk. <see cref="Recorded"/> is then
/// false while <see cref="Records"/> lists what actually landed, and
/// <see cref="Message"/> names them.
/// </summary>
/// <param name="Recorded">Whether the whole batch was written.</param>
/// <param name="Message">A confirmation, or what went wrong and how far it got.</param>
/// <param name="Records">One entry per concept actually stamped, in the order given.</param>
public readonly record struct VerificationOutcome(bool Recorded, string Message, IReadOnlyList<VerificationRecord> Records);

/// <summary>
/// The core, thread-safe write primitive for OKF bundles: producer-validated,
/// reparse-guarded, atomically-serialized create/update of a concept and an
/// atomic read-modify-write append-to-concept, over a single bundle root.
/// Promoted verbatim from <c>OKF4net.Agents.OkfBundleTools</c> so both that
/// type and <c>OKF4net.Catalog.FileMemoryStore</c> share one write path and one
/// process-wide per-path lock registry (no duplicate lock registry, no divergent
/// second write path). Never throws for an expected error — I/O, YAML,
/// validation, and reparse-point rejections are returned as an
/// <c>Error: ...</c> result string.
/// </summary>
public sealed class BundleConceptWriter
{
    /// <summary>
    /// Process-wide registry of one lock object per canonicalized bundle
    /// root, keyed by <see cref="ReparsePoints.CanonicalizeRoot"/> of the
    /// bundle root -- the SAME canonical form <see cref="ReparsePoints.IsWithinBundleRoot"/>
    /// and <see cref="ReparsePoints.HasReparsePointAncestor(string, string)"/>
    /// resolve to, so two different
    /// spellings of the same directory (e.g. a trailing separator, or a
    /// relative vs. absolute path) still share one lock. Every
    /// <see cref="BundleConceptWriter"/> instance constructed over the same
    /// bundle path -- not just the same instance -- ends up sharing the
    /// same lock object via <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,Func{TKey,TValue})"/>
    /// in the constructor: a per-INSTANCE lock (the previous design) left
    /// two separate instances pointed at the
    /// same bundle directory free to race each other's
    /// <see cref="AppendToConceptAtomic"/>/<see cref="WriteConcept(string, string, string)"/> calls,
    /// even though each instance's OWN calls were already serialized against
    /// themselves. <see cref="StringComparer.OrdinalIgnoreCase"/> is
    /// deliberate: two case-variant spellings of the same physical bundle
    /// directory must coalesce onto one lock object, or each spelling gets
    /// its own lock and two writers pointed at the same physical directory
    /// could still race each other's writes -- the exact bug this registry
    /// exists to prevent. The two failure directions are asymmetric:
    /// over-coalescing (two spellings that happen to be genuinely different
    /// directories on a case-sensitive volume sharing a lock anyway) only
    /// costs them a little unnecessary serialization against each other,
    /// while under-coalescing reopens the race -- <c>OrdinalIgnoreCase</c>
    /// picks the harmless side. The registry grows by one small object
    /// per distinct bundle path for the process's lifetime -- bounded in
    /// practice by how many distinct bundle directories a process ever
    /// opens, and never removed (there is no matching "last instance for
    /// this path went away" signal to remove it on).
    /// </summary>
    private static readonly ConcurrentDictionary<string, object> BundleLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Guards every write this class performs to disk. Callers may invoke
    /// write methods concurrently from multiple threads; the same lock also
    /// lets a co-located caller (e.g. <c>OkfBundleTools.AppendLog</c>/
    /// <c>RegenerateIndexes</c>/cache access) serialize its own
    /// read-modify-write sequences against this writer's writes via
    /// <see cref="WriteLock"/>.
    ///
    /// Obtained from the process-wide <see cref="BundleLocks"/> registry, so
    /// this guarantee extends to every <see cref="BundleConceptWriter"/>
    /// instance constructed over the same canonicalized bundle root -- not
    /// just calls on THIS instance. It does NOT serialize writes across
    /// separate processes (e.g. two CLI invocations, or two server processes
    /// sharing a network path), and a C# lock cannot defend against a
    /// concurrent external actor mutating the bundle's files directly on
    /// disk -- see <see cref="ValidateConceptTarget"/>'s remarks for that
    /// separate, residual TOCTOU limitation.
    /// </summary>
    private readonly object _bundleLock;

    private readonly Action? _onWriteCommitted;

    /// <summary>When true, <see cref="WriteConcept(string, string, string)"/> stamps a <c>generated</c> block (§5.2) if the caller omitted one. Off by default so only opt-in producer paths (the Agents write tool) auto-stamp.</summary>
    internal bool AutoStampGenerated { get; set; }

    /// <summary>
    /// Clock seam for the <c>generated</c> auto-stamp and for
    /// <see cref="RecordVerifications"/>'s <c>at</c>; overridable in tests.
    /// </summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>The §7 actor recorded as <c>generated.by</c> when auto-stamping.</summary>
    internal string ProducerActor { get; set; } = "okf4net/" + OkfSpec.Version;

    /// <summary>Creates a writer rooted at <paramref name="bundleRoot"/>.</summary>
    /// <param name="bundleRoot">The bundle's root directory.</param>
    /// <param name="onWriteCommitted">
    /// Optional callback invoked inside the write lock immediately after a
    /// successful file write, before the lock is released — the seam
    /// <c>OkfBundleTools</c> uses to invalidate its bundle cache atomically with
    /// the write. <see langword="null"/> (the default) for callers with no cache.
    /// </param>
    public BundleConceptWriter(string bundleRoot, Action? onWriteCommitted = null)
    {
        ArgumentNullException.ThrowIfNull(bundleRoot);
        BundleRoot = bundleRoot;
        _onWriteCommitted = onWriteCommitted;

        // Canonicalize BEFORE looking up the shared lock so two different
        // spellings of the same bundle directory (e.g. with/without a
        // trailing separator) still resolve to the same registry entry --
        // the same canonicalization ReparsePoints.IsWithinBundleRoot and
        // ReparsePoints.HasReparsePointAncestor use for their own root (see
        // ReparsePoints.CanonicalizeRoot's remarks); otherwise those two
        // spellings would land in different registry entries and defeat the
        // very serialization this lock exists to provide (F3).
        var canonicalRoot = ReparsePoints.CanonicalizeRoot(bundleRoot);
        _bundleLock = BundleLocks.GetOrAdd(canonicalRoot, static _ => new object());
    }

    /// <summary>The bundle root, as passed to the constructor.</summary>
    public string BundleRoot { get; }

    /// <summary>
    /// The shared per-path lock object for this bundle root, obtained from the
    /// process-wide registry keyed by the canonicalized root. Exposed so a
    /// co-located caller (<c>OkfBundleTools.AppendLog</c>/<c>RegenerateIndexes</c>/
    /// cache access) can serialize its own read-modify-write sequences against
    /// this writer's writes.
    /// </summary>
    internal object WriteLock => _bundleLock;

    /// <summary>
    /// Test-only hook, invoked (if set) immediately before the late
    /// reparse-point re-check in <see cref="WriteValidatedContentLocked"/>
    /// (after its own <see cref="Directory.CreateDirectory(string)"/> call).
    /// Lets a test deterministically simulate a filesystem substitution
    /// racing the final write -- e.g. deleting the just-created parent
    /// directory and replacing it with a junction to an external directory --
    /// at exactly the point such a race would need to land, instead of
    /// relying on real (flaky, unreliable) thread timing.
    /// <see langword="internal"/>, always <see langword="null"/> outside
    /// tests, so it has zero effect on production behavior.
    /// </summary>
    internal Action? BeforeLateReparseCheckForTest { get; set; }

    /// <summary>
    /// Creates or updates one concept document. Producer-grade validation
    /// (<see cref="OkfDocument.Validate"/>: non-empty <c>type</c>,
    /// <c>title</c>, and <c>description</c>) runs BEFORE
    /// anything is written — on failure, the file on disk (if any) is left
    /// untouched. Never throws for expected errors (a null/blank/malformed
    /// concept id, a reserved id, invalid frontmatter YAML, or a failed
    /// validation) — those are reported as a plain-text message instead.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatterYaml">Frontmatter as <c>key: value</c> lines (the same YAML subset used inside a document's frontmatter block, without the <c>---</c> delimiters).</param>
    /// <param name="body">The markdown body.</param>
    public string WriteConcept(string conceptId, string frontmatterYaml, string body)
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

            var (content, buildError) = BuildValidatedContent(ParseFrontmatterAndMaybeStamp(frontmatterYaml), body);
            if (buildError is not null)
            {
                return buildError;
            }

            // Serialized under _bundleLock (shared with AppendToConceptAtomic
            // and, since _bundleLock is obtained from the process-wide
            // BundleLocks registry, with every OTHER BundleConceptWriter
            // instance pointed at this same canonicalized bundle root, not
            // just this instance) so concurrent writers can't interleave an
            // existence check with another writer's write, and so the
            // committed callback below is atomic with the write it follows.
            lock (_bundleLock)
            {
                return WriteValidatedContentLocked(target.Id, target.TargetPath, content!);
            }
        });
    }

    /// <summary>
    /// Like <see cref="WriteConcept(string, string, string)"/>, but takes an already-built
    /// <see cref="Frontmatter"/> instead of pre-serialized YAML text — skips the serialize/re-parse
    /// round trip for a programmatic caller (e.g. the forthcoming <c>OkfDocumentBuilder</c>). Same
    /// producer-grade validation, per-bundle lock, and reparse-point guards as the string overload
    /// (both share <see cref="ValidateConceptTarget"/>, <see cref="BuildValidatedContent(YamlValue, string)"/>,
    /// and <see cref="WriteValidatedContentLocked"/>).
    ///
    /// Operates on a shallow copy of <paramref name="frontmatter"/>'s underlying mapping, never the
    /// caller's own <see cref="YamlMapping"/> instance — <see cref="Frontmatter.AsMapping"/> returns
    /// that instance directly (no defensive copy of its own), and mutating it in place (e.g. via
    /// auto-stamping, see <see cref="MaybeStampGenerated"/>) would otherwise silently modify an object
    /// the caller may still hold and inspect afterward.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatter">The frontmatter to write. Not mutated by this call.</param>
    /// <param name="body">The markdown body.</param>
    public string WriteConcept(string conceptId, Frontmatter frontmatter, string body)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        ArgumentNullException.ThrowIfNull(frontmatter);

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

            var mapping = ShallowCopy(frontmatter.AsMapping());
            MaybeStampGenerated(mapping);

            var (content, buildError) = BuildValidatedContent(mapping, body);
            if (buildError is not null)
            {
                return buildError;
            }

            lock (_bundleLock)
            {
                return WriteValidatedContentLocked(target.Id, target.TargetPath, content!);
            }
        });
    }

    /// <summary>
    /// Copies <paramref name="map"/>'s entries into a fresh <see cref="YamlMapping"/>, preserving
    /// order and every entry verbatim (including a duplicate or non-string key, via
    /// <see cref="YamlMapping.PushRaw"/>) — a shallow copy, sufficient because
    /// <see cref="WriteConcept(string, Frontmatter, string)"/> only ever inserts a new top-level key
    /// into the copy, never mutates a nested value.
    /// </summary>
    private static YamlMapping ShallowCopy(YamlMapping map)
    {
        var copy = new YamlMapping();
        foreach (var (key, value) in map.Entries)
        {
            copy.PushRaw(key, value);
        }

        return copy;
    }

    /// <summary>
    /// Atomically reads, transforms, and rewrites one concept's body under
    /// the shared bundle lock — the seam a same-day memory-capture caller
    /// (e.g. <c>OKF4net.Agents.OkfContextProvider.CaptureMemory</c>) uses to
    /// close a lost-update race (E2): before this
    /// existed, a caller that read a concept's body via a cached bundle,
    /// built a new body from it OUTSIDE any lock, then called the plain
    /// <see cref="WriteConcept(string, string, string)"/>, could have that read/build/write sequence
    /// interleave with a concurrent caller doing the same for the same
    /// concept — both read the same "before" body, and the second write
    /// silently clobbers the first's change (a lost update), even if some
    /// OTHER, already-locked write faithfully recorded both calls, producing
    /// a count divergence between the two.
    /// Here, the read of the concept's CURRENT on-disk body, the caller's
    /// <paramref name="buildBody"/> transform, and the validated write all
    /// happen inside one unbroken hold of <see cref="_bundleLock"/>, so two
    /// concurrent calls for the same concept id can never interleave: the
    /// second call's read always observes the first call's completed write.
    /// Because <see cref="_bundleLock"/> is obtained from the process-wide
    /// <c>BundleLocks</c> registry (keyed by the canonicalized bundle root),
    /// this holds for two concurrent calls on the SAME <see cref="BundleConceptWriter"/>
    /// instance AND for two concurrent calls on two SEPARATE instances
    /// constructed over the same bundle path -- but only within one process:
    /// it does not serialize a second process writing the same bundle path,
    /// and a C# lock cannot stop a concurrent external actor from mutating
    /// the target file/its ancestor directories on disk out from under this
    /// method (see <see cref="ValidateConceptTarget"/>'s remarks for that
    /// separate, residual check-then-write limitation, which this lock does
    /// not close). Reuses the exact same target validation (<see cref="ValidateConceptTarget"/>),
    /// producer-grade validation and serialization (<see cref="BuildValidatedContent(string, string)"/>),
    /// and write/callback (<see cref="WriteValidatedContentLocked"/>)
    /// steps <see cref="WriteConcept(string, string, string)"/> itself uses — this is a locked
    /// read-modify-write wrapped AROUND that same core, not a divergent
    /// second write path.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>memory/2026-07-24</c>.</param>
    /// <param name="frontmatterYamlIfCreating">
    /// Frontmatter used only when the concept does not yet exist. When it
    /// already exists, its own current frontmatter is re-read and
    /// re-serialized unchanged (mirroring how a caller that read-then-called
    /// <see cref="WriteConcept(string, string, string)"/> would carry it forward) and this parameter
    /// is ignored.
    /// </param>
    /// <param name="buildBody">
    /// Given the concept's current body (<see langword="null"/> if it does
    /// not yet exist), returns the full new body to write. Invoked exactly
    /// once, inside the lock, against the freshly re-read current body —
    /// never a caller's own stale, pre-lock snapshot.
    /// </param>
    /// <returns>
    /// The same style of result text as <see cref="WriteConcept(string, string, string)"/> (a
    /// <c>Written ...</c> confirmation) or an <c>Error: ...</c> message;
    /// never throws.
    /// </returns>
    public string AppendToConceptAtomic(
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
                var existedBefore = File.Exists(target.TargetPath);
                if (existedBefore)
                {
                    // Strict UTF-8: a non-UTF-8 concept file throws
                    // DecoderFallbackException (caught by RunTool below)
                    // rather than being silently re-decoded and rewritten.
                    var text = OkfEncodings.Strict.GetString(File.ReadAllBytes(target.TargetPath));
                    // Fail-closed: if the existing concept has malformed
                    // frontmatter (hand-edited, or a prior partial/crashed
                    // write), OkfDocument.Parse throws DocumentParseException
                    // (caught by RunTool -> Error text) so this append is
                    // dropped rather than overwriting a possibly important
                    // file. This is stricter than a permissive bundle-load
                    // path, which would treat an unparseable file as absent
                    // and silently recreate it.
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

                return WriteValidatedContentLocked(target.Id, target.TargetPath, content!, existedBefore);
            }
        });
    }

    /// <summary>
    /// Records a review of every concept in <paramref name="conceptIds"/>:
    /// adds — or replaces, at its position — the <c>{ by, at }</c> entry of
    /// <paramref name="by"/> in each concept's §5.2 <c>verified</c> list,
    /// preserving every other frontmatter key and the body.
    ///
    /// Fully validated before the first write: every concept is resolved, read,
    /// edited and validated inside one hold of the bundle lock, before a single
    /// byte is written. A batch is therefore REJECTED as a whole — an unknown
    /// id, a malformed actor or a non-conformant document writes nothing.
    ///
    /// It is NOT a transaction. Writing several files cannot be atomic in
    /// .NET, so a failure during the write phase (I/O, permissions, a reparse
    /// point appearing after the late re-check) leaves the concepts already
    /// written stamped. That case reports <c>Recorded = false</c> with
    /// <c>Records</c> listing what did land — see <see cref="VerificationOutcome"/>.
    /// The lock is also in-process, so an external actor mutating the bundle
    /// mid-batch is not stopped: the same documented limit as this class's
    /// reparse-point guard.
    ///
    /// A stamp is a dated declaration, not an authentication result: this
    /// method cannot and does not check that the caller is who
    /// <paramref name="by"/> names. What makes a stamp credible is where it
    /// lands — a reviewed diff — not the tool that wrote it.
    /// </summary>
    /// <param name="conceptIds">Concept ids (paths without <c>.md</c>); each must already exist.</param>
    /// <param name="by">The §7 actor recording the review; must be well-formed.</param>
    /// <param name="at">
    /// Timestamp in the library's own UTC shape (<c>yyyy-MM-ddTHH:mm:ssZ</c>);
    /// null uses <see cref="UtcNow"/>.
    /// </param>
    public VerificationOutcome RecordVerifications(IReadOnlyList<string> conceptIds, string by, string? at = null)
    {
        if (conceptIds is null || conceptIds.Count == 0)
        {
            return Failed("Error: no concept id given.");
        }

        // Guard every element before any of them reaches ValidateConceptTarget:
        // ConceptId.TryParse's Parse -> s.Split('/') throws NullReferenceException
        // for a null element (NRE is not in RunTool's catch filter), and a JSON
        // binder handing this list to a string[] can put a null in it regardless
        // of nullable annotations. Mirrors WriteConcept's own id guards verbatim.
        foreach (var conceptId in conceptIds)
        {
            if (string.IsNullOrWhiteSpace(conceptId))
            {
                return Failed("Error: invalid concept id — it must not be empty.");
            }

            if (conceptId.Contains('\0'))
            {
                return Failed("Error: invalid concept id — it must not contain a null character.");
            }
        }

        // Strict on input, permissive on read: `human:` with no id promotes the
        // tier (Actor.IsHuman ignores well-formedness), so it must never be
        // written here even though a parser would accept it.
        if (by is null || !Actor.Parse(by).IsWellFormed)
        {
            return Failed($"Error: '{by}' is not a well-formed §7 actor.");
        }

        // NOT BundleValidator.IsIso8601DateTime: that predicate validates the
        // date and ignores everything after the `T` (Validate.cs:618), because
        // reading frontmatter is deliberately permissive. Writing is not: a
        // stamp this library produces is UTC in one exact shape, and accepting
        // "2026-08-28" or a +02:00 offset here would write a value the field's
        // own documentation calls UTC.
        var stampedAt = at ?? OkfTimestamp.FormatUtc(UtcNow());
        if (!DateTime.TryParseExact(
                stampedAt,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out _))
        {
            return Failed($"Error: '{stampedAt}' is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ.");
        }

        var records = new List<VerificationRecord>(conceptIds.Count);
        var message = RunTool(() =>
        {
            // Resolved outside the lock, like AppendToConceptAtomic does.
            var targets = new List<ConceptTarget>(conceptIds.Count);
            foreach (var conceptId in conceptIds)
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                targets.Add(target);
            }

            // Duplicates are refused, not silently collapsed: preparing the same
            // file twice would build both versions from the same original
            // content and write it twice, reporting two records for the single
            // stamp that survives — a result that reads like two reviews.
            // Checked on the RESOLVED target path, not the raw id string that
            // was passed in: two case-variant spellings of the same concept
            // ("metrics/dau" / "metrics/DAU") resolve to the same file on a
            // case-insensitive filesystem (Windows/macOS) and must collide too
            // — the same OrdinalIgnoreCase reasoning the BundleLocks registry
            // above uses for exactly this class of bug.
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < targets.Count; i++)
            {
                if (!seenPaths.Add(targets[i].TargetPath))
                {
                    return $"Error: concept '{conceptIds[i]}' is named more than once.";
                }
            }

            lock (_bundleLock)
            {
                // PREPARE every concept — read, parse, upsert the stamp, and
                // validate — before writing any of them, so an unknown,
                // unreadable, or non-conformant concept later in the list
                // rejects the WHOLE batch, even though earlier concepts in it
                // already built successfully.
                var prepared = new List<(ConceptTarget Target, string Content, string ConceptId, string? ReplacedAt)>(targets.Count);
                for (var i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    if (!File.Exists(target.TargetPath))
                    {
                        return $"Error: concept '{conceptIds[i]}' does not exist.";
                    }

                    var text = OkfEncodings.Strict.GetString(File.ReadAllBytes(target.TargetPath));
                    var document = OkfDocument.Parse(text);
                    var map = document.Frontmatter.AsMapping();

                    map.Insert("verified", UpsertStamp(map.Get("verified"), by, stampedAt, out var replacedAt));

                    // Throws DocumentValidationException on a failed §11 check,
                    // caught by RunTool -- nothing in `prepared` so far has been
                    // written, so the whole batch is rejected cleanly.
                    var content = BuildConformantContent(map, document.Body);

                    prepared.Add((target, content, conceptIds[i], replacedAt));
                }

                // Writing N files cannot be atomic, so a failure here — I/O,
                // permissions, a reparse point appearing between the late
                // re-check and the write — leaves the earlier concepts
                // stamped. `records` is built HERE, one entry per successful
                // write, deliberately NOT in the prepare loop above: that is
                // what makes it mean "landed on disk", not "was validated". A
                // batch rejected during PREPARE never reaches this loop, so
                // `records` stays empty; a batch that fails partway through
                // WRITE leaves `records` holding exactly the prefix that
                // actually made it to disk — no separate trim/rollback step
                // to keep in sync, and no way for a future early return in
                // this loop to under- or over-report what landed.
                for (var i = 0; i < prepared.Count; i++)
                {
                    var (target, content, conceptId, replacedAt) = prepared[i];
                    var writeResult = WriteValidatedContentLocked(target.Id, target.TargetPath, content, existedBefore: true);
                    if (writeResult.StartsWith("Error:", StringComparison.Ordinal))
                    {
                        return records.Count == 0
                            ? writeResult
                            : $"{writeResult} — already written: {string.Join(", ", records.Select(r => r.ConceptId))}";
                    }

                    records.Add(new VerificationRecord(conceptId, stampedAt, replacedAt));
                }

                return $"Recorded {prepared.Count} verification(s) by {by} at {stampedAt}.";
            }
        });

        // On failure, Records is NOT emptied: it carries whatever reached disk
        // before the failure, so a caller can tell "nothing happened" from
        // "three of five were stamped and then it broke".
        return message.StartsWith("Error:", StringComparison.Ordinal)
            ? new VerificationOutcome(false, message, records)
            : new VerificationOutcome(true, message, records);

        static VerificationOutcome Failed(string message) => new(false, message, []);
    }

    /// <summary>
    /// Returns the <c>verified</c> sequence with <paramref name="by"/>'s stamp
    /// added, or replaced at its existing position. <see cref="YamlSequence"/>
    /// is immutable, so the list is rebuilt; only the FIRST entry matching the
    /// actor is replaced — a permissive reader accepts duplicates, and this
    /// writer never deletes an entry it is not replacing.
    /// </summary>
    private static YamlSequence UpsertStamp(YamlValue? existing, string by, string at, out string? replacedAt)
    {
        replacedAt = null;

        var items = existing switch
        {
            YamlSequence sequence => new List<YamlValue>(sequence.Items),
            // `verified: { by, at }` — a bare mapping — is a shape ParseVerified
            // accepts, so normalize it into the list rather than discarding it.
            YamlMapping single => [single],
            _ => [],
        };

        var stamp = new YamlMapping();
        stamp.Insert("by", new YamlString(by));
        stamp.Insert("at", new YamlString(at));

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is YamlMapping mapping
                && string.Equals(mapping.Get("by")?.AsDisplayString(), by, StringComparison.Ordinal))
            {
                replacedAt = mapping.Get("at")?.AsDisplayString();
                items[i] = stamp;
                return new YamlSequence(items);
            }
        }

        items.Add(stamp);
        return new YamlSequence(items);
    }

    /// <summary>
    /// Serializes after §11 conformance validation only (non-empty <c>type</c>),
    /// unlike <see cref="BuildValidatedContent(YamlValue, string)"/>'s
    /// producer-grade check. Deliberate: recording a review is not producing
    /// content, and refusing a reviewer because a third party omitted a
    /// <c>description</c> would make precisely the concepts an audit surfaces
    /// unstampable. Unlike the <see cref="YamlValue"/>-based overload above,
    /// there is no "not a mapping" case to report here — the caller always
    /// passes an already-typed <see cref="YamlMapping"/> — so this returns the
    /// serialized content directly rather than an <c>(Content, Error)</c> pair
    /// whose <c>Error</c> half could never be anything but <see langword="null"/>.
    /// Throws <see cref="DocumentValidationException"/> on a failed conformance
    /// check, caught by the caller's <see cref="RunTool"/> wrapper.
    /// </summary>
    private static string BuildConformantContent(YamlMapping frontmatter, string body)
    {
        var document = new OkfDocument(Frontmatter.FromMapping(frontmatter), body);
        document.ValidateConformance();
        return document.Serialize();
    }

    /// <summary>A validated concept id and the absolute path it resolves to, produced by <see cref="ValidateConceptTarget"/>.</summary>
    private readonly record struct ConceptTarget(ConceptId Id, string TargetPath);

    /// <summary>
    /// Validates <paramref name="conceptId"/> (parseable, not the reserved
    /// <c>index</c>/<c>log</c> name) and the filesystem path it resolves to
    /// (within the bundle root; no reparse point among its parent
    /// directories or at the target itself) — shared by <see cref="WriteConcept(string, string, string)"/>
    /// and <see cref="AppendToConceptAtomic"/> so the two can never diverge
    /// on what counts as a valid write target. Pure: performs no I/O beyond
    /// the reparse-point/existence checks themselves, and does not touch
    /// <see cref="_bundleLock"/>.
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
        // defense-in-depth check the reparse helpers below use before
        // touching disk.
        if (!ReparsePoints.IsWithinBundleRoot(BundleRoot, targetPath))
        {
            return $"Error: '{id}' resolves outside the bundle root.";
        }

        // Reject a reparse point (symlink/junction) anywhere between the
        // bundle root and the target's parent directory: the lexical check
        // above would happily accept "tables/refunds" even if "tables" is a
        // junction pointing outside the bundle -- the OS follows it when
        // Directory.CreateDirectory/File.WriteAllText actually touch disk.
        var targetParentDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetParentDir) && ReparsePoints.HasReparsePointAncestor(BundleRoot, targetParentDir))
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
    /// Parses <paramref name="frontmatterYaml"/> once and delegates the auto-stamp decision to
    /// <see cref="MaybeStampGenerated"/>. Returns the parsed (and possibly stamped) <see cref="YamlValue"/>
    /// for <see cref="BuildValidatedContent(YamlValue, string)"/> to validate and serialize directly —
    /// so the write path parses exactly once, never re-serializing and re-parsing a stamped mapping.
    /// Throws <see cref="Yaml.YamlParseException"/> on malformed frontmatter, caught by the caller's
    /// <see cref="RunTool"/> wrapper before anything is written.
    /// </summary>
    private YamlValue ParseFrontmatterAndMaybeStamp(string frontmatterYaml)
    {
        var parsed = YamlValue.Parse(frontmatterYaml);
        if (parsed is YamlMapping map)
        {
            MaybeStampGenerated(map);
        }

        return parsed;
    }

    /// <summary>
    /// When <see cref="AutoStampGenerated"/> is on and <paramref name="map"/> has no <c>generated</c>
    /// key of its own, stamps a <c>generated: { by, at }</c> block (§5.2) into it **in place** — the
    /// single stamping decision shared by the string-based <see cref="WriteConcept(string, string, string)"/>
    /// path (via <see cref="ParseFrontmatterAndMaybeStamp"/>, on a freshly parsed, caller-invisible
    /// mapping) and the <see cref="Frontmatter"/>-based <see cref="WriteConcept(string, Frontmatter, string)"/>
    /// overload (which passes a defensive copy — see that overload's remarks — precisely so this
    /// in-place mutation never reaches the caller's own <see cref="Frontmatter"/> object). No-op when
    /// the flag is off or a <c>generated</c> key is already present.
    /// </summary>
    private void MaybeStampGenerated(YamlMapping map)
    {
        if (AutoStampGenerated && !map.ContainsKey("generated"))
        {
            var generated = new YamlMapping();
            generated.Insert("by", new YamlString(ProducerActor));
            generated.Insert("at", new YamlString(OkfTimestamp.FormatUtc(UtcNow())));
            map.Insert("generated", generated);
        }
    }

    /// <summary>
    /// Parses <paramref name="frontmatterYaml"/> and delegates to the
    /// <see cref="BuildValidatedContent(YamlValue, string)"/> overload. The
    /// <see cref="AppendToConceptAtomic"/> path enters here (it parses only
    /// once); <see cref="WriteConcept(string, string, string)"/> parses up front in
    /// <see cref="ParseFrontmatterAndMaybeStamp"/> and enters the overload
    /// directly. Throws <see cref="Yaml.YamlParseException"/> (line-tagged
    /// message) on malformed input, caught by the caller's
    /// <see cref="RunTool"/> wrapper before anything is written.
    /// </summary>
    private static (string? Content, string? Error) BuildValidatedContent(string frontmatterYaml, string body) =>
        BuildValidatedContent(YamlValue.Parse(frontmatterYaml), body);

    /// <summary>
    /// Builds and validates the <see cref="OkfDocument"/> for the already-parsed
    /// <paramref name="frontmatter"/> against <paramref name="body"/>, then
    /// serializes it — the exact producer-grade validation
    /// <see cref="WriteConcept(string, string, string)"/> performs, shared verbatim with
    /// <see cref="AppendToConceptAtomic"/> so the two can never validate
    /// divergently. Throws <see cref="DocumentValidationException"/> (failed
    /// producer validation), caught by the caller's <see cref="RunTool"/>
    /// wrapper, rather than returning an error for that case; only
    /// "frontmatter parses but isn't a mapping" is reported via the returned
    /// <c>Error</c> string.
    /// </summary>
    private static (string? Content, string? Error) BuildValidatedContent(YamlValue frontmatter, string body)
    {
        Frontmatter? fm = frontmatter switch
        {
            YamlNull => new Frontmatter(),
            YamlMapping map => Frontmatter.FromMapping(map),
            _ => null,
        };

        if (fm is null)
        {
            return (null, "Error: frontmatter must be a YAML mapping of 'key: value' lines, not a list or scalar.");
        }

        var doc = new OkfDocument(fm, body);

        // Strict producer validation BEFORE any write. On failure this
        // throws DocumentValidationException (message lists MissingKeys),
        // caught by RunTool -- nothing is written for a failed write.
        doc.Validate();

        return (doc.Serialize(), null);
    }

    /// <summary>
    /// Late, best-effort reparse-point re-check used by
    /// <see cref="WriteValidatedContentLocked"/> immediately before its
    /// <see cref="File.WriteAllText(string, string, System.Text.Encoding)"/>
    /// call, still inside the caller's hold of <see cref="_bundleLock"/>.
    /// Re-runs the same two checks <see cref="ValidateConceptTarget"/>
    /// already ran earlier: a reparse point among <paramref name="targetPath"/>'s
    /// directory ancestors (up to <see cref="BundleRoot"/>), or at
    /// <paramref name="targetPath"/> itself -- see
    /// <see cref="ValidateConceptTarget"/>'s remarks for the full threat model
    /// this narrows (not closes).
    /// </summary>
    /// <param name="subject">
    /// Human-readable identifier for the returned error message, e.g.
    /// <c>"'tables/x'"</c>.
    /// </param>
    /// <param name="parentDir"><paramref name="targetPath"/>'s parent directory.</param>
    /// <param name="targetPath">The file about to be written.</param>
    /// <returns>
    /// An <c>Error: ...</c> message if a reparse point is detected; otherwise
    /// <see langword="null"/>.
    /// </returns>
    private string? LateReparseGuard(string subject, string? parentDir, string targetPath)
    {
        if ((!string.IsNullOrEmpty(parentDir) && ReparsePoints.HasReparsePointAncestor(BundleRoot, parentDir))
            || ReparsePoints.IsReparsePoint(targetPath))
        {
            return $"Error: {subject} resolves through a reparse point (symlink/junction) inside the bundle, which is not allowed.";
        }

        return null;
    }

    /// <summary>
    /// Writes already-validated <paramref name="content"/> to <paramref name="targetPath"/>
    /// and invokes <see cref="_onWriteCommitted"/>. CALLER MUST already hold
    /// <see cref="_bundleLock"/> — this method does not acquire it itself,
    /// so that <see cref="AppendToConceptAtomic"/> can enclose its own
    /// preceding read-and-transform in the SAME lock acquisition as this
    /// write (a nested/second acquisition here would either reintroduce the
    /// exact gap this seam exists to close, or -- if <see cref="_bundleLock"/>
    /// were ever changed to a non-reentrant primitive -- deadlock). Shared
    /// verbatim by <see cref="WriteConcept(string, string, string)"/> (which wraps a single call to
    /// this in its own <c>lock (_bundleLock)</c>) and
    /// <see cref="AppendToConceptAtomic"/>.
    /// </summary>
    /// <remarks>
    /// Defense-in-depth against the check-then-write gap documented on
    /// <see cref="ValidateConceptTarget"/>: immediately before the actual
    /// <see cref="File.WriteAllText(string, string, System.Text.Encoding)"/> call below,
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
    /// <param name="id">The concept id being written, used only for the returned confirmation message.</param>
    /// <param name="targetPath">The absolute path to write <paramref name="content"/> to.</param>
    /// <param name="content">The fully serialized, already-validated document content to write.</param>
    /// <param name="existedBefore">
    /// Whether <paramref name="targetPath"/> already existed, if the caller
    /// already knows this from a check performed earlier under the same
    /// <see cref="_bundleLock"/> hold (<see cref="AppendToConceptAtomic"/>
    /// passes its own earlier <see cref="File.Exists(string)"/> result here
    /// to avoid a redundant second stat of the same path). <see langword="null"/>
    /// (the default, used by <see cref="WriteConcept(string, string, string)"/>'s single-call site)
    /// means "no such check has happened yet" -- this method then performs
    /// it itself, exactly as before.
    /// </param>
    private string WriteValidatedContentLocked(ConceptId id, string targetPath, string content, bool? existedBefore = null)
    {
        var existed = existedBefore ?? File.Exists(targetPath);

        var parentDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        BeforeLateReparseCheckForTest?.Invoke();

        // Late, best-effort re-check -- see this method's <remarks> and
        // ValidateConceptTarget's <remarks> for exactly what this does and
        // does not close.
        var lateError = LateReparseGuard($"'{id}'", parentDir, targetPath);
        if (lateError is not null)
        {
            return lateError;
        }

        File.WriteAllText(targetPath, content, OkfEncodings.NoBom);
        _onWriteCommitted?.Invoke();

        var byteCount = OkfEncodings.NoBom.GetByteCount(content);
        var status = existed ? "updated" : "new";
        return $"Written {id} ({status}, {byteCount} bytes). Remember to run okf_regenerate_indexes.";
    }

    /// <summary>
    /// Runs a write-method body, converting any exception that a well-formed
    /// but unlucky input could still trigger — a producer-validation failure
    /// (<see cref="OkfException"/>, e.g. <see cref="DocumentValidationException"/>),
    /// a rejected argument surfaced late by a BCL API (<see cref="ArgumentException"/>),
    /// a filesystem read/write failure (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>), or a strict-UTF-8 decode
    /// failure reading an existing concept file directly
    /// (<see cref="System.Text.DecoderFallbackException"/>) — into a
    /// plain-text message. This is the single enforcement point for the
    /// "never throw for an expected error" rule.
    /// </summary>
    private static string RunTool(Func<string> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (ex is OkfException or ArgumentException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return $"Error: {ex.Message}";
        }
    }
}
