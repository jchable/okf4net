// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Concurrent;
using OKF4net.Internal;
using OKF4net.Yaml;

namespace OKF4net;

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
    /// root, keyed by <see cref="Path.GetFullPath(string)"/> of the bundle
    /// root -- the SAME canonical form <see cref="ReparsePoints.IsWithinBundleRoot"/>
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
    /// <see cref="AppendToConceptAtomic"/>/<see cref="WriteConcept"/> calls,
    /// even though each instance's OWN calls were already serialized against
    /// themselves. <see cref="StringComparer.OrdinalIgnoreCase"/>, matching
    /// the ordinal-ignore-case comparisons <see cref="ReparsePoints.IsWithinBundleRoot"/>
    /// and the reserved-id check already use (Windows/macOS filesystems are
    /// typically case-insensitive). The registry grows by one small object
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
    /// <c>title</c>, <c>description</c> and <c>timestamp</c>) runs BEFORE
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

            var (content, buildError) = BuildValidatedContent(frontmatterYaml, body);
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
    /// Atomically reads, transforms, and rewrites one concept's body under
    /// the shared bundle lock — the seam a same-day memory-capture caller
    /// (e.g. <c>OKF4net.Agents.OkfContextProvider.CaptureMemory</c>) uses to
    /// close a lost-update race (E2): before this
    /// existed, a caller that read a concept's body via a cached bundle,
    /// built a new body from it OUTSIDE any lock, then called the plain
    /// <see cref="WriteConcept"/>, could have that read/build/write sequence
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
    /// producer-grade validation and serialization (<see cref="BuildValidatedContent"/>),
    /// and write/callback (<see cref="WriteValidatedContentLocked"/>)
    /// steps <see cref="WriteConcept"/> itself uses — this is a locked
    /// read-modify-write wrapped AROUND that same core, not a divergent
    /// second write path.
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
    /// Parses <paramref name="frontmatterYaml"/>, builds and validates the
    /// resulting <see cref="OkfDocument"/> against <paramref name="body"/>,
    /// and serializes it — the exact producer-grade validation
    /// <see cref="WriteConcept"/> performs, shared verbatim with
    /// <see cref="AppendToConceptAtomic"/> so the two can never validate
    /// divergently. Throws <see cref="Yaml.YamlParseException"/> (malformed
    /// frontmatter YAML) or <see cref="DocumentValidationException"/> (failed
    /// producer validation) — both caught by the caller's <see cref="RunTool"/>
    /// wrapper — rather than returning an error for those two cases; only
    /// "frontmatter parses but isn't a mapping" is reported via the
    /// returned <c>Error</c> string.
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
    /// verbatim by <see cref="WriteConcept"/> (which wraps a single call to
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
    /// (the default, used by <see cref="WriteConcept"/>'s single-call site)
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
