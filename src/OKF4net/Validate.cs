// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net;

/// <summary>
/// Conformance checking against OKF v0.1 §9.
///
/// A bundle is <b>conformant</b> if (1) every non-reserved <c>.md</c> file
/// has a parseable frontmatter block, (2) every frontmatter has a non-empty
/// <c>type</c>, and (3) reserved files follow their structure when present.
/// Everything else is soft guidance: consumers MUST NOT reject a bundle for
/// missing optional fields, unknown types/keys, broken links, or missing
/// <c>index.md</c> files.
///
/// Accordingly, <see cref="BundleValidator.Validate"/> reports only true §9
/// violations as <see cref="Severity.Error"/>; all softer issues are
/// <see cref="Severity.Warning"/> or <see cref="Severity.Info"/>.
/// </summary>
public enum Severity
{
    /// <summary>A §9 conformance violation.</summary>
    Error,

    /// <summary>A soft-guidance deviation (the bundle is still conformant).</summary>
    Warning,

    /// <summary>Informational note (e.g. a broken but permitted cross-link).</summary>
    Info,
}

/// <summary>
/// A single finding about a bundle: <see cref="Path"/> and
/// <see cref="Concept"/> are each populated only when the finding relates to a
/// file or a concept respectively (never both, per
/// <see cref="BundleValidator.Validate"/>).
/// </summary>
public sealed record Diagnostic(Severity Severity, string? Path, ConceptId? Concept, string Message)
{
    /// <summary>
    /// Renders as <c>[severity] path: message</c> or <c>[severity] concept:
    /// message</c> (falling back to a bare <c>[severity] message</c> if
    /// neither is set).
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(SeverityText(Severity)).Append("] ");
        if (Path is not null)
        {
            sb.Append(Path).Append(": ");
        }
        else if (Concept is not null)
        {
            sb.Append(Concept).Append(": ");
        }

        sb.Append(Message);
        return sb.ToString();
    }

    /// <summary>Lower-case severity label.</summary>
    private static string SeverityText(Severity severity) => severity switch
    {
        Severity.Error => "error",
        Severity.Warning => "warning",
        Severity.Info => "info",
        _ => severity.ToString(),
    };
}

/// <summary>
/// The result of validating a bundle.
/// </summary>
public sealed class ValidationReport
{
    /// <summary>All findings, in the order <see cref="BundleValidator.Validate"/> produced them.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>Creates a report over <paramref name="diagnostics"/>.</summary>
    public ValidationReport(IReadOnlyList<Diagnostic> diagnostics)
    {
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// <c>true</c> if there are no <see cref="Severity.Error"/> diagnostics
    /// -- i.e. the bundle conforms to §9.
    /// </summary>
    public bool IsConformant => !Diagnostics.Any(d => d.Severity == Severity.Error);

    /// <summary>Diagnostics of a given severity.</summary>
    public IEnumerable<Diagnostic> Of(Severity severity) => Diagnostics.Where(d => d.Severity == severity);

    /// <summary>Count of error-level diagnostics.</summary>
    public int ErrorCount => Of(Severity.Error).Count();

    /// <summary>Count of warning-level diagnostics.</summary>
    public int WarningCount => Of(Severity.Warning).Count();
}

/// <summary>
/// Validates a loaded <see cref="Bundle"/> against §9.
/// </summary>
public static class BundleValidator
{
    private const string IndexFilename = "index.md";

    /// <summary>
    /// Validates a loaded bundle against §9, returning all findings.
    /// </summary>
    public static ValidationReport Validate(Bundle bundle)
    {
        var diagnostics = new List<Diagnostic>();

        // (1) Files whose frontmatter could not be parsed are conformance errors.
        foreach (var (path, error) in bundle.ParseErrors)
        {
            diagnostics.Add(new Diagnostic(
                Severity.Error,
                path,
                null,
                $"unparseable concept document: {error}"));
        }

        // (2) Every concept must carry a non-empty `type`; recommended fields are
        // soft guidance.
        foreach (var concept in bundle.Concepts)
        {
            var fm = concept.Document.Frontmatter;
            if (!HasConformantType(concept.Document))
            {
                diagnostics.Add(new Diagnostic(
                    Severity.Error,
                    concept.Path,
                    concept.Id,
                    "missing required frontmatter field `type`"));
            }

            foreach (var field in RecommendedFields)
            {
                var value = fm.Get(field);
                if (value is null || value.IsEmptyValue)
                {
                    diagnostics.Add(new Diagnostic(
                        Severity.Warning,
                        concept.Path,
                        concept.Id,
                        $"missing recommended frontmatter field `{field}`"));
                }
            }

            var ts = fm.Timestamp;
            if (ts is not null && !IsIso8601DateTime(ts))
            {
                diagnostics.Add(new Diagnostic(
                    Severity.Warning,
                    concept.Path,
                    concept.Id,
                    $"`timestamp` is not ISO-8601: {DebugQuote.Quote(ts)}"));
            }
        }

        // (3) Reserved files must follow their structure when present.
        ValidateReserved(bundle, diagnostics);

        // Broken cross-links are permitted (§5.3); report them as info only.
        foreach (var (source, raw) in bundle.BrokenLinks())
        {
            diagnostics.Add(new Diagnostic(
                Severity.Info,
                null,
                source,
                $"link target does not resolve to a concept in the bundle: {raw}"));
        }

        return new ValidationReport(diagnostics);
    }

    private static readonly string[] RecommendedFields = ["title", "description", "timestamp"];

    /// <summary>Non-throwing check that the concept carries a conformant <c>type</c> (§9), without relying on exceptions for control flow.</summary>
    private static bool HasConformantType(OkfDocument document)
    {
        var value = document.Frontmatter.Get("type");
        return value is not null && !value.IsEmptyValue;
    }

    /// <summary>Checks that reserved files (index.md and log.md) follow their structural rules when present (§6/§7).</summary>
    private static void ValidateReserved(Bundle bundle, List<Diagnostic> diagnostics)
    {
        var rootIndex = System.IO.Path.Combine(bundle.Root, IndexFilename);

        foreach (var path in bundle.IndexFiles)
        {
            string text;
            try
            {
                text = OkfEncodings.Strict.GetString(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (System.Text.DecoderFallbackException)
            {
                continue;
            }

            OkfDocument doc;
            try
            {
                doc = OkfDocument.Parse(text);
            }
            catch (DocumentParseException)
            {
                continue;
            }

            if (doc.Frontmatter.IsEmpty)
            {
                continue;
            }

            // Frontmatter is only permitted in the bundle-root index.md, and only
            // to declare `okf_version` (§11).
            var isRoot = string.Equals(path, rootIndex, StringComparison.Ordinal);
            if (!isRoot)
            {
                diagnostics.Add(new Diagnostic(
                    Severity.Warning,
                    path,
                    null,
                    "index.md should not contain frontmatter (§6)"));
            }
            else
            {
                var onlyVersion = doc.Frontmatter.AsMapping().Keys.All(k => k == "okf_version");
                if (!onlyVersion)
                {
                    diagnostics.Add(new Diagnostic(
                        Severity.Warning,
                        path,
                        null,
                        "root index.md frontmatter should declare only `okf_version` (§11)"));
                }
            }
        }

        foreach (var path in bundle.LogFiles)
        {
            string text;
            try
            {
                text = OkfEncodings.Strict.GetString(File.ReadAllBytes(path));
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (System.Text.DecoderFallbackException)
            {
                continue;
            }

            var log = ChangeLog.Parse(text);
            foreach (var bad in log.InvalidDates())
            {
                diagnostics.Add(new Diagnostic(
                    Severity.Warning,
                    path,
                    null,
                    $"log date heading is not ISO-8601 `YYYY-MM-DD`: {DebugQuote.Quote(bad)}"));
            }
        }
    }

    /// <summary>
    /// Light ISO-8601 datetime check: a valid <c>YYYY-MM-DD</c> date,
    /// optionally followed by <c>T&lt;time&gt;</c> with an optional zone.
    /// This is intentionally permissive -- the spec treats <c>timestamp</c>
    /// formatting as soft guidance.
    /// </summary>
    public static bool IsIso8601DateTime(string s)
    {
        var sepIndex = s.IndexOfAny(['T', ' ']);
        var datePart = sepIndex >= 0 ? s[..sepIndex] : s;
        return ChangeLog.IsIsoDate(datePart);
    }

}
