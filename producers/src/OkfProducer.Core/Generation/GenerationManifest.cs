// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Buffers;
using System.Text.Json;
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Core.Generation;

/// <summary>
/// One concept a generation run produced, together with the repository files it was derived from --
/// the join §6.3's third rule needs and the only reason this is a record rather than a bare id string.
///
/// <para><b>Why the owning files are recorded and not re-derived.</b> Pruning asks, of an id the
/// previous run produced and this one did not: "is the symbol gone, or was its file simply not read?"
/// Nothing in the bundle answers that -- a code concept carries a <c>resource</c> URL at best, and a
/// URL is not a path this producer can match against <see cref="RunStatus.Skipped"/>. Recomputing the
/// mapping from the id would mean re-deriving <see cref="CodeConceptIds"/>' slug rules and the
/// registry's collision suffixes outside the generator, i.e. forking the one piece of logic whose
/// drift silently deletes the wrong file. So the run that knows writes it down.</para>
/// </summary>
/// <param name="Id">The concept id, in its canonical <see cref="OKF4net.ConceptId.ToString"/> form.</param>
/// <param name="SourceFiles">
/// Every repository-relative, <c>/</c>-separated source path that contributed a declaration to this
/// concept, sorted <see cref="StringComparer.Ordinal"/>. Empty for a concept derived from something
/// other than extracted source (<c>overview</c>, <c>packages/*</c>, <c>docs/*</c>) -- and an entry
/// with no source file is never pruned, because there is nothing to check its owner against.
/// </param>
public sealed record ManifestConcept(string Id, IReadOnlyList<string> SourceFiles);

/// <summary>
/// The record a generation run leaves in the bundle (<see cref="FileName"/>) of what it produced and
/// what it analysed -- §6.3's second rule, "a manifest, not a prefix".
///
/// <para><b>What it is for.</b> <see cref="WritePolicy.Update"/> used to preserve everything it did
/// not generate, which meant a deleted method kept its concept for ever, pointing at code that no
/// longer exists. Pruning fixes that, but "absent from this run" has two indistinguishable causes --
/// the symbol was deleted, or its file could not be read -- so the deletion has to be keyed on
/// something narrower than "everything under <c>code/</c> that this run did not write". That
/// something is this file: <see cref="BundleWriter"/> deletes <b>only</b> ids the previous manifest
/// claims, so a concept a human hand-wrote under the owned prefix is never this producer's to delete.
/// It is warned about instead.</para>
///
/// <para><b>Why it is not discovered as a concept.</b> <c>Bundle.Load</c> collects <c>*.md</c>; this
/// file is <c>.json</c>, so it is inert to the bundle model, to <c>IndexGenerator</c> (which lists
/// markdown children only) and to <c>okf validate</c>.</para>
///
/// <para><b>It is written output, so it is byte-stable.</b> <see cref="WriteTo"/> normalizes before it
/// serializes -- every list sorted <see cref="StringComparer.Ordinal"/> and de-duplicated, two-space
/// indentation, <c>\n</c> line endings, a trailing newline -- so two runs over identical input produce
/// identical bytes and the bundle's <c>git diff</c> shows what changed in the code (§6.2).</para>
///
/// <para><b>What is deliberately NOT here.</b> The spec's §2.3 table also asks for the hash of each
/// file's read content, so a file modified mid-run is detectable. Nothing in the pipeline surfaces
/// one today (<see cref="ExtractionResult"/> carries symbols, sites and a status; the extractor reads
/// the bytes and drops them), so adding the field would mean recording a hash this producer never
/// computes. It is left out rather than faked -- see this task's report.</para>
/// </summary>
/// <param name="OwnedPrefix">
/// The concept-id prefix this producer claims, e.g. <c>code</c>. Two distinct jobs: nothing outside it
/// is ever pruned even if the manifest names it (a defence in depth behind the manifest itself), and
/// it is the subtree scanned for the "present under the owned prefix but not owned by this producer"
/// warning §6.3 asks for.
/// </param>
/// <param name="Concepts">Every concept the run wrote, with the files each was derived from.</param>
/// <param name="ExtractedFiles">
/// The repository-relative paths this run read and parsed in full (<see cref="FileStatus.Extracted"/>).
/// <b>Written for the operator, never read back as a gate</b>: the pruning decision keys off the
/// <see cref="RunStatus"/> of the run doing the pruning, not off what some earlier run managed to
/// read, so no one should wire this field into a safety check on the strength of it being here.
/// </param>
/// <param name="Scope">
/// The <see cref="ScopeOptions"/> the run was given, and -- unlike <see cref="ExtractedFiles"/> --
/// <b>read back as a gate</b>.
///
/// <para><b>Why it has to be recorded.</b> <c>FileEligibility</c> filters tests at the FILE level and
/// visibility at the SYMBOL level, and only the first of those is safe to forget. Drop
/// <c>--include-tests</c> and the owning file is never visited, so it is absent from
/// <see cref="RunStatus.Skipped"/>, still on disk, and <c>BundleWriter</c>'s per-candidate settled
/// check keeps its concepts. Drop <c>--include-internal</c> and the owning file <i>is</i> visited and
/// comes back <see cref="FileStatus.Extracted"/> -- so every internal symbol's concept looks settled
/// and gets deleted, taking any hand-written description with it, while the run reports the deletion
/// as a symbol gone from the repository. Nothing in a run's own output distinguishes "this symbol was
/// deleted" from "this symbol is no longer in scope"; only the previous run's scope does, so the
/// previous run writes it down.</para>
///
/// <para><see langword="null"/> only for a manifest that was hand-assembled or hand-edited without
/// one -- <see cref="ForRun"/> always sets it. A null is treated as "unknown", which refuses pruning,
/// because the whole point of the field is that its absence cannot be assumed benign.</para>
/// </param>
public sealed record GenerationManifest(
    string OwnedPrefix,
    IReadOnlyList<ManifestConcept> Concepts,
    IReadOnlyList<string> ExtractedFiles,
    ScopeOptions? Scope = null)
{
    /// <summary>
    /// The manifest's file name inside the bundle. A leading dot and a <c>.json</c> extension: the
    /// extension is what keeps it out of concept discovery, the dot is only convention.
    /// </summary>
    public const string FileName = ".okfgen-manifest.json";

    /// <summary>
    /// The schema version this code writes and is the only one it reads. A manifest carrying anything
    /// else is treated exactly like a missing one -- ignored, and nothing is pruned -- because the one
    /// thing a manifest from an unknown future must not do is authorize deletions under rules this
    /// build does not know.
    ///
    /// <para><b>2 rather than 1 because <see cref="Scope"/> is a gate, not a field.</b> A version-1
    /// manifest records no scope, and this build cannot tell whether the run that wrote it covered a
    /// wider one -- so reading it as if it did would be exactly the deletion the field exists to stop.
    /// Bumping makes an old manifest ignored once: that run prunes nothing, writes a version-2
    /// manifest, and every run after it prunes normally. The alternative -- reading a version-1
    /// manifest and defaulting its scope -- silently assumes the narrowest answer for a question the
    /// file does not answer.</para>
    /// </summary>
    public const int SchemaVersion = 2;

    /// <summary>
    /// The ids in <see cref="Concepts"/>, in the order they are held. Convenience for callers that
    /// only need the id set; the owning files are what pruning actually joins on.
    /// </summary>
    public IReadOnlyList<string> ConceptIds => [.. Concepts.Select(c => c.Id)];

    /// <summary>
    /// The manifest describing one finished generation run: every concept it produced, paired with the
    /// source files those concepts were derived from, and the files it read in full.
    ///
    /// <para><see cref="ExtractedFiles"/> is derived from <paramref name="status"/> here rather than
    /// taken from the caller, so the persisted record cannot disagree with the run it claims to
    /// describe. That derivation is exact only because <see cref="RunStatus.Skipped"/> records every
    /// attempted file including the clean ones -- and it is exact only when
    /// <see cref="RunStatus.TraversalComplete"/> is true, which is why the pruning gate checks that
    /// first and this method makes no safety claim at all.</para>
    /// </summary>
    /// <param name="ownedPrefix">The concept-id prefix the run claims.</param>
    /// <param name="concepts">Every concept the run produced.</param>
    /// <param name="status">The run's extraction outcome.</param>
    /// <param name="scope">
    /// The scope the run was given. Required rather than defaulted: see <see cref="Scope"/> for what a
    /// forgotten scope deletes, and a default would be the one value that makes the omission invisible.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="ownedPrefix"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="concepts"/>, <paramref name="status"/> or <paramref name="scope"/> is null.</exception>
    public static GenerationManifest ForRun(
        string ownedPrefix,
        IReadOnlyList<GeneratedConcept> concepts,
        RunStatus status,
        ScopeOptions scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownedPrefix);
        ArgumentNullException.ThrowIfNull(concepts);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(scope);

        var entries = concepts
            .Select(c => new ManifestConcept(c.Id.ToString(), NormalizePaths(c.SourceFiles)))
            .ToList();

        var extracted = status.Skipped
            .Where(s => s.Status == FileStatus.Extracted)
            .Select(s => s.Path);

        return new GenerationManifest(ownedPrefix.Trim(), entries, NormalizePaths(extracted), scope).Normalized();
    }

    /// <summary>
    /// The same manifest with every list sorted <see cref="StringComparer.Ordinal"/> and
    /// de-duplicated. Applied on construction by <see cref="ForRun"/>, on read, and again immediately
    /// before serialization -- the last of those is the one that matters, because it is what makes the
    /// bytes on disk independent of the order a caller happened to hand things in.
    /// </summary>
    public GenerationManifest Normalized() => new(
        OwnedPrefix,
        [.. (Concepts ?? [])
            .Where(c => c is not null && !string.IsNullOrEmpty(c.Id))
            .GroupBy(c => c.Id, StringComparer.Ordinal)
            .Select(g => new ManifestConcept(g.Key, NormalizePaths(g.SelectMany(c => c.SourceFiles ?? []))))
            .OrderBy(c => c.Id, StringComparer.Ordinal)],
        NormalizePaths(ExtractedFiles ?? []),
        Scope);

    /// <summary>
    /// Writes this manifest into <paramref name="bundleRoot"/>, normalized (see
    /// <see cref="Normalized"/>) and byte-stable. Overwrites any manifest already there.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bundleRoot"/> is null or empty.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    public void WriteTo(string bundleRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundleRoot);
        File.WriteAllBytes(Path.Combine(bundleRoot, FileName), Serialize(Normalized()));
    }

    /// <summary>
    /// Reads the manifest in <paramref name="bundleRoot"/>, or <see langword="null"/> when there is
    /// none, it cannot be read, it is not valid JSON, or it carries a
    /// <see cref="SchemaVersion"/> this build does not know.
    ///
    /// <para><b>Every failure is <see langword="null"/>, never an exception.</b> A manifest is the
    /// permission slip for deletions: the safe reading of "I cannot understand this file" is "this run
    /// owns nothing, delete nothing", which is exactly what a null does downstream. Throwing would be
    /// strictly worse in both directions -- it would abort a write that was otherwise fine, and it
    /// would make a corrupt manifest a denial of service on regenerating the bundle.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="bundleRoot"/> is null or empty.</exception>
    public static GenerationManifest? TryRead(string bundleRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundleRoot);

        var path = Path.Combine(bundleRoot, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return Deserialize(document.RootElement)?.Normalized();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static GenerationManifest? Deserialize(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var schema)
            || schema != SchemaVersion
            || !root.TryGetProperty("ownedPrefix", out var prefix)
            || prefix.ValueKind != JsonValueKind.String
            || prefix.GetString() is not { Length: > 0 } ownedPrefix)
        {
            return null;
        }

        var concepts = new List<ManifestConcept>();
        if (root.TryGetProperty("concepts", out var conceptArray) && conceptArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in conceptArray.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || id.GetString() is not { Length: > 0 } conceptId)
                {
                    continue;
                }

                concepts.Add(new ManifestConcept(conceptId, ReadStrings(entry, "sources")));
            }
        }

        return new GenerationManifest(ownedPrefix, concepts, ReadStrings(root, "extractedFiles"), ReadScope(root));
    }

    /// <summary>
    /// The <c>scope</c> object, or <see langword="null"/> when it is missing, is not an object, or does
    /// not carry both booleans. Null rather than a default, and the whole manifest is <b>not</b>
    /// rejected for it: a scope-less manifest still bounds deletion by id and prefix, and
    /// <c>BundleWriter</c> reads the null as "unknown scope, prune nothing" -- strictly safer than
    /// throwing the id list away, which would leave the concepts orphaned instead.
    /// </summary>
    private static ScopeOptions? ReadScope(JsonElement root)
    {
        if (!root.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryReadBool(scope, "includeTests") is { } tests && TryReadBool(scope, "includeInternal") is { } internals
            ? new ScopeOptions(tests, internals)
            : null;
    }

    private static bool? TryReadBool(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static IReadOnlyList<string> ReadStrings(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var values = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static byte[] Serialize(GenerationManifest manifest)
    {
        var buffer = new ArrayBufferWriter<byte>();

        // `NewLine` is set explicitly rather than left to default to Environment.NewLine: this file is
        // committed alongside the bundle, and a manifest whose line endings depend on which OS
        // regenerated it would churn in `git diff` for no reason at all.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", SchemaVersion);
            writer.WriteString("ownedPrefix", manifest.OwnedPrefix);

            // Omitted entirely rather than written as `null` when unknown: the reader treats a missing
            // and a malformed scope the same way (prune nothing), so one spelling of "not recorded" is
            // enough, and a JSON null would read as a value that was chosen.
            if (manifest.Scope is { } scope)
            {
                writer.WriteStartObject("scope");
                writer.WriteBoolean("includeTests", scope.IncludeTests);
                writer.WriteBoolean("includeInternal", scope.IncludeInternal);
                writer.WriteEndObject();
            }

            writer.WriteStartArray("extractedFiles");
            foreach (var file in manifest.ExtractedFiles)
            {
                writer.WriteStringValue(file);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("concepts");
            foreach (var concept in manifest.Concepts)
            {
                writer.WriteStartObject();
                writer.WriteString("id", concept.Id);
                writer.WriteStartArray("sources");
                foreach (var source in concept.SourceFiles)
                {
                    writer.WriteStringValue(source);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var json = buffer.WrittenSpan;
        var bytes = new byte[json.Length + 1];
        json.CopyTo(bytes);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    /// <summary>
    /// Normalized (<see cref="SourceOwnershipMap.Normalize"/> -- <c>\</c> to <c>/</c>, a leading
    /// <c>./</c> dropped), de-duplicated and sorted <see cref="StringComparer.Ordinal"/>. The
    /// separator rule is borrowed rather than restated: these paths are joined against
    /// <see cref="SymbolFact.RelativePath"/> and <see cref="RunStatus.Skipped"/>, and a join whose two
    /// sides normalize by different rules is one that silently returns nothing -- here that would mean
    /// "no owner recorded", which reads as "safe to delete".
    /// </summary>
    private static IReadOnlyList<string> NormalizePaths(IEnumerable<string> paths) =>
    [
        .. paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(SourceOwnershipMap.Normalize)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
    ];
}
