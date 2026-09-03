// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OKF4net.Cli;

/// <summary>One <see cref="Diagnostic"/>, projected for <c>--json</c> output.</summary>
internal sealed record DiagnosticJson(
    string Severity,
    string Code,
    string? Path,
    string? ConceptId,
    string? Field,
    string Message);

/// <summary>The full result of <c>okf validate --json</c>.</summary>
internal sealed record ValidateJsonResult(
    string Bundle,
    string AsOf,
    bool Conformant,
    int ConceptCount,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    IReadOnlyList<DiagnosticJson> Diagnostics);

/// <summary>One unparseable file, projected for <c>--json</c> output.</summary>
internal sealed record ParseErrorJson(string Path, string Message);

/// <summary>The full result of <c>okf info --json</c>.</summary>
internal sealed record InfoJsonResult(
    string Bundle,
    string? OkfVersion,
    int ConceptCount,
    int IndexFileCount,
    int LogFileCount,
    IReadOnlyDictionary<string, int> Types,
    int LinkCount,
    int BrokenLinkCount,
    IReadOnlyList<ParseErrorJson> ParseErrors);

/// <summary>The query <c>okf audit</c> applied, replayed for <c>--json</c> consumers.</summary>
internal sealed record AuditQueryJson(bool Stale, IReadOnlyList<string>? Trust, string? Status, string? Type);

/// <summary>
/// Concept counts per trust tier (§5.3), over the whole bundle. The property
/// names are spelled out rather than left to the camelCase policy so one
/// document says <c>human-reviewed</c> everywhere: a consumer grouping findings
/// by tier can look the tier straight up in these counts. Declared weakest to
/// strongest, the same ladder order <c>query.trust</c> serializes in.
/// </summary>
internal sealed record TrustCountsJson(
    [property: JsonPropertyName("unverified")] int Unverified,
    [property: JsonPropertyName("machine-confirmed")] int MachineConfirmed,
    [property: JsonPropertyName("human-reviewed")] int HumanReviewed);

/// <summary>
/// Concept counts per lifecycle status (§5.4), over the whole bundle, in §5.4
/// order. These names need no override: the camelCase policy already emits the
/// vocabulary's own spelling for single-word statuses.
/// </summary>
internal sealed record StatusCountsJson(int Draft, int Stable, int Deprecated);

/// <summary>One selected concept, projected for <c>--json</c> output.</summary>
internal sealed record AuditFindingJson(
    string ConceptId,
    string Path,
    string? Type,
    string? Title,
    string Trust,
    string Status,
    string? StaleAfter,
    bool Stale);

/// <summary>The full result of <c>okf audit --json</c>.</summary>
internal sealed record AuditJsonResult(
    string Bundle,
    string AsOf,
    int ConceptCount,
    AuditQueryJson Query,
    TrustCountsJson Trust,
    StatusCountsJson Status,
    int StaleCount,
    IReadOnlyList<AuditFindingJson> Findings);

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> for every
/// <c>--json</c> output type. Required, not optional, because the CLI is
/// published Native AOT (<c>PublishAot</c>): reflection-based
/// <see cref="JsonSerializer.Serialize{T}(T, JsonSerializerOptions?)"/>
/// without a context is not trim-safe and some of its reflection APIs do
/// not work under Native AOT at all. camelCase property names.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ValidateJsonResult))]
[JsonSerializable(typeof(InfoJsonResult))]
[JsonSerializable(typeof(AuditJsonResult))]
internal partial class CliJsonContext : JsonSerializerContext
{
}

/// <summary>Builds and writes the <c>--json</c> output for <c>validate</c>, <c>info</c> and <c>audit</c>.</summary>
internal static class JsonOutput
{
    /// <summary>Writes <c>okf validate --json</c>'s result to <paramref name="stdout"/> as a single line-terminated JSON document.</summary>
    /// <param name="stdout">Where the document is written.</param>
    /// <param name="bundlePath">The bundle path, echoed as given on the command line.</param>
    /// <param name="asOf">
    /// The date of the instant staleness (§5.5) was evaluated at — the whole
    /// point of <c>--as-of</c>. Without it in the document, an archived report
    /// cannot be told apart from an unpinned run, so the reproducibility the
    /// flag buys is not visible in the artefact itself.
    /// </param>
    /// <param name="bundle">The validated bundle.</param>
    /// <param name="report">The validator's findings.</param>
    internal static void WriteValidate(
        TextWriter stdout,
        string bundlePath,
        DateOnly asOf,
        Bundle bundle,
        ValidationReport report)
    {
        var diagnostics = report.Diagnostics
            .Select(d => new DiagnosticJson(
                Diagnostic.SeverityText(d.Severity),
                d.Code.ToString(),
                d.Path,
                d.Concept?.ToString(),
                d.Field,
                d.Message))
            .ToList();

        var result = new ValidateJsonResult(
            bundlePath,
            asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            report.IsConformant,
            bundle.Count,
            report.ErrorCount,
            report.WarningCount,
            report.Of(Severity.Info).Count(),
            diagnostics);

        stdout.Write(JsonSerializer.Serialize(result, CliJsonContext.Default.ValidateJsonResult));
        stdout.Write("\n");
    }

    /// <summary>Writes <c>okf info --json</c>'s result to <paramref name="stdout"/> as a single line-terminated JSON document.</summary>
    internal static void WriteInfo(TextWriter stdout, string bundlePath, Bundle bundle)
    {
        var byType = BuildTypeHistogram(bundle);

        var totalLinks = 0;
        foreach (var c in bundle.Concepts)
        {
            totalLinks += bundle.LinksFrom(c.Id).Count;
        }

        var parseErrors = bundle.ParseErrors
            .Select(pe => new ParseErrorJson(pe.Path, pe.Error))
            .ToList();

        var result = new InfoJsonResult(
            bundlePath,
            bundle.OkfVersion,
            bundle.Count,
            bundle.IndexFiles.Count,
            bundle.LogFiles.Count,
            byType,
            totalLinks,
            bundle.BrokenLinks().Count,
            parseErrors);

        stdout.Write(JsonSerializer.Serialize(result, CliJsonContext.Default.InfoJsonResult));
        stdout.Write("\n");
    }

    /// <summary>Writes <c>okf audit --json</c>'s result to <paramref name="stdout"/> as a single line-terminated JSON document.</summary>
    internal static void WriteAudit(TextWriter stdout, string bundlePath, AuditQuery query, AuditReport report)
    {
        // Serialized in ladder order, never in the order the user typed them:
        // IReadOnlySet has no guaranteed order, and the document must be
        // reproducible. Written as a statement rather than a ternary so the
        // nullable analysis narrows `Trust` through the pattern -- it does not
        // narrow a property across the arms of a conditional.
        List<string>? trustQuery = null;
        if (query.Trust is { } selectedTiers)
        {
            trustQuery = AuditVocabulary.TrustTiersInOrder
                .Where(selectedTiers.Contains)
                .Select(AuditVocabulary.Name)
                .ToList();
        }

        var findings = report.Findings
            .Select(f => new AuditFindingJson(
                f.Id.ToString(),
                f.Path,
                f.Type,
                f.Title,
                AuditVocabulary.Name(f.Trust),
                AuditVocabulary.Name(f.Lifecycle.Status),
                f.Lifecycle.StaleAfterRaw,
                f.IsStale))
            .ToList();

        var result = new AuditJsonResult(
            bundlePath,
            report.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            report.ConceptCount,
            new AuditQueryJson(
                query.StaleOnly,
                trustQuery,
                query.Status is { } status ? AuditVocabulary.Name(status) : null,
                query.Type),
            new TrustCountsJson(
                report.TrustCounts[TrustTier.Unverified],
                report.TrustCounts[TrustTier.MachineConfirmed],
                report.TrustCounts[TrustTier.HumanReviewed]),
            new StatusCountsJson(
                report.StatusCounts[ConceptStatus.Draft],
                report.StatusCounts[ConceptStatus.Stable],
                report.StatusCounts[ConceptStatus.Deprecated]),
            report.StaleCount,
            findings);

        stdout.Write(JsonSerializer.Serialize(result, CliJsonContext.Default.AuditJsonResult));
        stdout.Write("\n");
    }

    /// <summary>Counts concepts by frontmatter <c>type</c> (a missing type counts as <c>"(none)"</c>), sorted ordinally by type name.</summary>
    internal static SortedDictionary<string, int> BuildTypeHistogram(Bundle bundle)
    {
        var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in bundle.Concepts)
        {
            var t = c.Document.Frontmatter.Type ?? "(none)";
            byType[t] = byType.GetValueOrDefault(t) + 1;
        }

        return byType;
    }
}
