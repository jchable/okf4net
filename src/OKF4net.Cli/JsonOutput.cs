// SPDX-License-Identifier: LGPL-3.0-or-later
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
internal partial class CliJsonContext : JsonSerializerContext
{
}

/// <summary>Builds and writes the <c>--json</c> output for <c>validate</c> and <c>info</c>.</summary>
internal static class JsonOutput
{
    /// <summary>Writes <c>okf validate --json</c>'s result to <paramref name="stdout"/> as a single line-terminated JSON document.</summary>
    internal static void WriteValidate(TextWriter stdout, string bundlePath, Bundle bundle, ValidationReport report)
    {
        var diagnostics = report.Diagnostics
            .Select(d => new DiagnosticJson(
                SeverityText(d.Severity),
                d.Code.ToString(),
                d.Path,
                d.Concept?.ToString(),
                d.Field,
                d.Message))
            .ToList();

        var result = new ValidateJsonResult(
            bundlePath,
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
        var byType = new Dictionary<string, int>();
        foreach (var c in bundle.Concepts)
        {
            var t = c.Document.Frontmatter.Type ?? "(none)";
            byType[t] = byType.GetValueOrDefault(t) + 1;
        }

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

    private static string SeverityText(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        Severity.Info => "info",
        _ => severity.ToString(),
    };
}
