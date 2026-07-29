// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>
/// Conformance checking against OKF v0.2 §11.
///
/// A bundle is <b>conformant</b> if (1) every non-reserved <c>.md</c> file
/// has a parseable frontmatter block, (2) every frontmatter has a non-empty
/// <c>type</c>, and (3) reserved files follow their structure when present.
/// Everything else is soft guidance: consumers MUST NOT reject a bundle for
/// missing optional fields, unknown types/keys, broken links, or missing
/// <c>index.md</c> files.
///
/// Accordingly, <see cref="BundleValidator.Validate"/> reports only true §11
/// violations as <see cref="Severity.Error"/>; all softer issues are
/// <see cref="Severity.Warning"/> or <see cref="Severity.Info"/>.
/// </summary>
public enum Severity
{
    /// <summary>A §11 conformance violation.</summary>
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
    /// -- i.e. the bundle conforms to §11.
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
/// Validates a loaded <see cref="Bundle"/> against §11.
/// </summary>
public static class BundleValidator
{
    private const string IndexFilename = "index.md";

    /// <summary>
    /// Validates a loaded bundle against §11, returning all findings.
    /// </summary>
    /// <param name="bundle">The loaded bundle to validate.</param>
    /// <param name="clock">Supplies "today" for staleness checks (§5.5); defaults to <see cref="SystemClock"/>.</param>
    public static ValidationReport Validate(Bundle bundle, IOkfClock? clock = null)
    {
        var today = (clock ?? new SystemClock()).Today;
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

            var gen = fm.Generated;
            if (gen is { } g)
            {
                if (g.By is null)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "generated is missing required `by`"));
                }
                else if (!g.By.Value.IsWellFormed)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"generated.by is not a valid §7 actor: {DebugQuote.Quote(g.By.Value.Raw)}"));
                }

                if (g.At is { } gat && !IsIso8601DateTime(gat))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"generated.at is not ISO-8601: {DebugQuote.Quote(gat)}"));
                }
            }

            foreach (var stamp in fm.Verified)
            {
                if (stamp.By is null)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "verified entry is missing `by`"));
                }
                else if (!stamp.By.Value.IsWellFormed)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"verified.by is not a valid §7 actor: {DebugQuote.Quote(stamp.By.Value.Raw)}"));
                }

                if (stamp.At is { } vat && !IsIso8601DateTime(vat))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"verified.at is not ISO-8601: {DebugQuote.Quote(vat)}"));
                }
            }

            var verifiedRaw = fm.Get("verified");
            if (verifiedRaw is YamlSequence verifiedSeq)
            {
                foreach (var item in verifiedSeq.Items)
                {
                    if (item is not YamlMapping)
                    {
                        diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "verified entry is not a `{by, at}` mapping"));
                    }
                }
            }
            else if (verifiedRaw is not null and not YamlNull and not YamlMapping)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "verified must be a `{by, at}` mapping or a list of them"));
            }

            var sourcesRaw = fm.Get("sources");
            if (sourcesRaw is YamlSequence sourcesSeq)
            {
                foreach (var item in sourcesSeq.Items)
                {
                    if (item is not YamlMapping)
                    {
                        diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "source entry is not a mapping"));
                    }
                }
            }
            else if (sourcesRaw is not null and not YamlNull)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "sources must be a list of entries"));
            }

            foreach (var src in fm.Sources)
            {
                if (src.Resource.Length == 0)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "source entry is missing required `resource`"));
                }

                if (src.LastModified is { } lastModified && !ChangeLog.IsIsoDate(lastModified))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"source last_modified is not `YYYY-MM-DD`: {DebugQuote.Quote(lastModified)}"));
                }
            }

            if (fm.UsageWindow is { } uw)
            {
                if (uw.From is { } uf && !ChangeLog.IsIsoDate(uf))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"usage_window from is not `YYYY-MM-DD`: {DebugQuote.Quote(uf)}"));
                }

                if (uw.To is { } ut && !ChangeLog.IsIsoDate(ut))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"usage_window to is not `YYYY-MM-DD`: {DebugQuote.Quote(ut)}"));
                }
            }

            var statusRaw = fm.Get("status");
            if (statusRaw is not null && statusRaw is not YamlNull && statusRaw.AsDisplayString() is null)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "status is not a scalar `draft|stable|deprecated`"));
            }

            var lc = fm.Lifecycle;
            if (!lc.StatusIsKnown)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"unknown status {DebugQuote.Quote(fm.Get("status")!.AsDisplayString() ?? string.Empty)}; treated as stable"));
            }

            if (lc.StaleAfterMalformed)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"stale_after is not `YYYY-MM-DD`: {DebugQuote.Quote(lc.StaleAfterRaw!)}"));
            }
            else if (lc.IsStale(today))
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"concept is stale (stale_after {lc.StaleAfterRaw})"));
            }

            if (concept.Document.UsesLegacyCitations())
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "body `# Citations` is legacy; move provenance to the `sources` frontmatter field"));
            }

            if (fm.Generated is null && fm.Timestamp is not null)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "`timestamp` is a legacy field; prefer `generated.at`"));
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

    private static readonly string[] RecommendedFields = ["title", "description", "resource", "tags"];

    /// <summary>Non-throwing check that the concept carries a conformant <c>type</c> (§11), without relying on exceptions for control flow.</summary>
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

                var declaredVersion = doc.Frontmatter.Get("okf_version")?.AsDisplayString();
                if (declaredVersion is not null && !string.Equals(declaredVersion, OkfSpec.Version, StringComparison.Ordinal))
                {
                    diagnostics.Add(new Diagnostic(
                        Severity.Warning,
                        path,
                        null,
                        $"declared okf_version {DebugQuote.Quote(declaredVersion)} is not supported; consuming best-effort as v{OkfSpec.Version}"));
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
