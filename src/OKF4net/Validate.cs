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
/// Stable, machine-readable identifier for a specific
/// <see cref="BundleValidator.Validate"/> finding, independent of the
/// human-readable <see cref="Diagnostic.Message"/> text (which may be
/// reworded without notice). One member per distinct diagnostic
/// <see cref="BundleValidator.Validate"/> and <see cref="BundleValidator.ValidateReserved"/>
/// can emit -- see each member's doc comment for the corresponding message
/// and, where applicable, the <see cref="Diagnostic.Field"/> it pairs with.
/// </summary>
public enum DiagnosticCode
{
    /// <summary>A concept document's frontmatter could not be parsed.</summary>
    UnparseableDocument,

    /// <summary>Frontmatter is missing the required <c>type</c> field (§11).</summary>
    MissingType,

    /// <summary>Frontmatter is missing a recommended field (<c>title</c>/<c>description</c>/<c>resource</c>/<c>tags</c>).</summary>
    MissingRecommendedField,

    /// <summary><c>generated</c> is present but missing its required <c>by</c>.</summary>
    GeneratedMissingBy,

    /// <summary><c>generated.by</c> is not a well-formed §7 actor.</summary>
    GeneratedInvalidActor,

    /// <summary><c>generated.at</c> is not ISO-8601.</summary>
    GeneratedInvalidDate,

    /// <summary>A <c>verified</c> entry is missing its required <c>by</c>.</summary>
    VerifiedMissingBy,

    /// <summary><c>verified.by</c> is not a well-formed §7 actor.</summary>
    VerifiedInvalidActor,

    /// <summary><c>verified.at</c> is not ISO-8601.</summary>
    VerifiedInvalidDate,

    /// <summary>A <c>verified</c> list entry is not a <c>{by, at}</c> mapping.</summary>
    VerifiedEntryNotMapping,

    /// <summary><c>verified</c> is neither a <c>{by, at}</c> mapping nor a list of them.</summary>
    VerifiedMalformed,

    /// <summary>A <c>sources</c> list entry is not a mapping.</summary>
    SourceEntryNotMapping,

    /// <summary><c>sources</c> is not a list of entries.</summary>
    SourcesMalformed,

    /// <summary>A <c>sources</c> entry is missing its required <c>resource</c>.</summary>
    SourceMissingResource,

    /// <summary>A <c>sources</c> entry's <c>last_modified</c> is not <c>YYYY-MM-DD</c>.</summary>
    SourceInvalidLastModified,

    /// <summary><c>usage_window.from</c> is not <c>YYYY-MM-DD</c>.</summary>
    UsageWindowInvalidFrom,

    /// <summary><c>usage_window.to</c> is not <c>YYYY-MM-DD</c>.</summary>
    UsageWindowInvalidTo,

    /// <summary><c>status</c> is present but not a scalar.</summary>
    StatusNotScalar,

    /// <summary><c>status</c> is a scalar but not one of <c>draft</c>/<c>stable</c>/<c>deprecated</c>.</summary>
    StatusUnknown,

    /// <summary><c>stale_after</c> is not <c>YYYY-MM-DD</c>.</summary>
    StaleAfterInvalid,

    /// <summary>The concept is past its <c>stale_after</c> date.</summary>
    ConceptStale,

    /// <summary>The body uses the legacy <c># Citations</c> heading instead of the <c>sources</c> frontmatter field (§13.1).</summary>
    LegacyCitations,

    /// <summary>Frontmatter uses the legacy <c>timestamp</c> field instead of <c>generated.at</c> (§13.1).</summary>
    LegacyTimestamp,

    /// <summary>A §10 Attested Computation is missing its required <c>runtime</c>.</summary>
    ComputationMissingRuntime,

    /// <summary>A §10 <c>parameters</c> entry is missing its required <c>name</c>.</summary>
    ComputationParameterMissingName,

    /// <summary>A §10 Attested Computation declares neither an inline <c># Computation</c> fence nor a <c>computation:</c> path.</summary>
    ComputationMissingBody,

    /// <summary>A §10 Attested Computation declares both an inline <c># Computation</c> fence and a <c>computation:</c> path.</summary>
    ComputationAmbiguous,

    /// <summary>§10 <c>executor.receipt</c> is present but not a list of field names.</summary>
    ExecutorReceiptInvalid,

    /// <summary>§10 <c>attester.resource</c> is present but empty.</summary>
    AttesterResourceEmpty,

    /// <summary>A §6.2 path-valued frontmatter field does not resolve to an existing file.</summary>
    FrontmatterPathMissing,

    /// <summary>A §6.2 path-valued frontmatter field resolves outside the bundle root.</summary>
    FrontmatterPathUnsafe,

    /// <summary>A reserved <c>index.md</c> could not be read or parsed (§8, §11).</summary>
    UnparseableIndex,

    /// <summary>A non-root <c>index.md</c> declares frontmatter, which §8 reserves for the bundle-root index only.</summary>
    IndexHasFrontmatter,

    /// <summary>The bundle-root <c>index.md</c>'s frontmatter declares keys other than <c>okf_version</c> (§12).</summary>
    RootIndexExtraFrontmatter,

    /// <summary>The bundle-root <c>index.md</c> declares an <c>okf_version</c> this build does not recognize.</summary>
    UnsupportedOkfVersion,

    /// <summary>A reserved <c>log.md</c> could not be read (§9, §11).</summary>
    UnparseableLog,

    /// <summary>A <c>log.md</c> date heading is not ISO-8601 <c>YYYY-MM-DD</c> (§9).</summary>
    LogDateInvalid,

    /// <summary>A cross-link target does not resolve to a concept in the bundle (§6; permitted, reported as <see cref="Severity.Info"/>).</summary>
    BrokenLink,
}

/// <summary>
/// A single finding about a bundle. <see cref="Path"/> and
/// <see cref="Concept"/> are not mutually exclusive: for a concept-level
/// finding, <see cref="BundleValidator.Validate"/> typically sets both (the
/// concept's own file path alongside its id); a file-level or body-level
/// finding may set only <see cref="Path"/>, with <see cref="Concept"/> left
/// <see langword="null"/>. <see cref="Code"/> is a stable identifier
/// independent of <see cref="Message"/>'s exact wording; <see cref="Field"/>
/// names the specific frontmatter key involved, when the diagnostic is about
/// one (<see langword="null"/> for body-level or file-level findings).
/// </summary>
public sealed record Diagnostic(Severity Severity, string? Path, ConceptId? Concept, string Message, DiagnosticCode Code, string? Field = null)
{
    /// <summary>
    /// Renders as <c>[severity] path: message</c> or <c>[severity] concept:
    /// message</c> (falling back to a bare <c>[severity] message</c> if
    /// neither is set). Unaffected by <see cref="Code"/> or <see cref="Field"/>
    /// -- this is the exact text every byte-exact golden fixture pins.
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

    /// <summary>Lower-case severity label (<c>error</c>/<c>warning</c>/<c>info</c>).</summary>
    internal static string SeverityText(Severity severity) => severity switch
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
                $"unparseable concept document: {error}",
                DiagnosticCode.UnparseableDocument));
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
                    "missing required frontmatter field `type`",
                    DiagnosticCode.MissingType,
                    "type"));
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
                        $"missing recommended frontmatter field `{field}`",
                        DiagnosticCode.MissingRecommendedField,
                        field));
                }
            }

            var gen = fm.Generated;
            if (gen is { } g)
            {
                if (g.By is null)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "generated is missing required `by`", DiagnosticCode.GeneratedMissingBy, "generated.by"));
                }
                else if (!g.By.Value.IsWellFormed)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"generated.by is not a valid §7 actor: {DebugQuote.Quote(g.By.Value.Raw)}", DiagnosticCode.GeneratedInvalidActor, "generated.by"));
                }

                if (g.At is { } gat && !IsIso8601DateTime(gat))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"generated.at is not ISO-8601: {DebugQuote.Quote(gat)}", DiagnosticCode.GeneratedInvalidDate, "generated.at"));
                }
            }

            foreach (var stamp in fm.Verified)
            {
                if (stamp.By is null)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "verified entry is missing `by`", DiagnosticCode.VerifiedMissingBy, "verified.by"));
                }
                else if (!stamp.By.Value.IsWellFormed)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"verified.by is not a valid §7 actor: {DebugQuote.Quote(stamp.By.Value.Raw)}", DiagnosticCode.VerifiedInvalidActor, "verified.by"));
                }

                if (stamp.At is { } vat && !IsIso8601DateTime(vat))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"verified.at is not ISO-8601: {DebugQuote.Quote(vat)}", DiagnosticCode.VerifiedInvalidDate, "verified.at"));
                }
            }

            var verifiedRaw = fm.Get("verified");
            if (verifiedRaw is YamlSequence verifiedSeq)
            {
                foreach (var item in verifiedSeq.Items)
                {
                    if (item is not YamlMapping)
                    {
                        diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "verified entry is not a `{by, at}` mapping", DiagnosticCode.VerifiedEntryNotMapping, "verified"));
                    }
                }
            }
            else if (verifiedRaw is not null and not YamlNull and not YamlMapping)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "verified must be a `{by, at}` mapping or a list of them", DiagnosticCode.VerifiedMalformed, "verified"));
            }

            var sourcesRaw = fm.Get("sources");
            if (sourcesRaw is YamlSequence sourcesSeq)
            {
                foreach (var item in sourcesSeq.Items)
                {
                    if (item is not YamlMapping)
                    {
                        diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "source entry is not a mapping", DiagnosticCode.SourceEntryNotMapping, "sources"));
                    }
                }
            }
            else if (sourcesRaw is not null and not YamlNull)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "sources must be a list of entries", DiagnosticCode.SourcesMalformed, "sources"));
            }

            foreach (var src in fm.Sources)
            {
                if (src.Resource.Length == 0)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "source entry is missing required `resource`", DiagnosticCode.SourceMissingResource, "sources.resource"));
                }

                if (src.LastModified is { } lastModified && !ChangeLog.IsIsoDate(lastModified))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"source last_modified is not `YYYY-MM-DD`: {DebugQuote.Quote(lastModified)}", DiagnosticCode.SourceInvalidLastModified, "sources.last_modified"));
                }
            }

            if (fm.UsageWindow is { } uw)
            {
                if (uw.From is { } uf && !ChangeLog.IsIsoDate(uf))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"usage_window from is not `YYYY-MM-DD`: {DebugQuote.Quote(uf)}", DiagnosticCode.UsageWindowInvalidFrom, "usage_window.from"));
                }

                if (uw.To is { } ut && !ChangeLog.IsIsoDate(ut))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"usage_window to is not `YYYY-MM-DD`: {DebugQuote.Quote(ut)}", DiagnosticCode.UsageWindowInvalidTo, "usage_window.to"));
                }
            }

            var statusRaw = fm.Get("status");
            if (statusRaw is not null && statusRaw is not YamlNull && statusRaw.AsDisplayString() is null)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "status is not a scalar `draft|stable|deprecated`", DiagnosticCode.StatusNotScalar, "status"));
            }

            var lc = fm.Lifecycle;
            if (!lc.StatusIsKnown)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"unknown status {DebugQuote.Quote(fm.Get("status")!.AsDisplayString() ?? string.Empty)}; treated as stable", DiagnosticCode.StatusUnknown, "status"));
            }

            if (lc.StaleAfterMalformed)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"stale_after is not `YYYY-MM-DD`: {DebugQuote.Quote(lc.StaleAfterRaw!)}", DiagnosticCode.StaleAfterInvalid, "stale_after"));
            }
            else if (lc.IsStale(today))
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"concept is stale (stale_after {lc.StaleAfterRaw})", DiagnosticCode.ConceptStale, "stale_after"));
            }

            if (concept.Document.UsesLegacyCitations())
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "body `# Citations` is legacy; move provenance to the `sources` frontmatter field", DiagnosticCode.LegacyCitations));
            }

            if (fm.Generated is null && fm.Timestamp is not null)
            {
                diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "`timestamp` is a legacy field; prefer `generated.at`", DiagnosticCode.LegacyTimestamp, "timestamp"));
            }

            // §10 Attested Computation: soft guidance only -- §10 is not part
            // of the §11 conformance floor, so a malformed Attested
            // Computation concept still stays conformant (Warning, never
            // Error).
            if (fm.IsAttestedComputation)
            {
                var contract = fm.ComputationContract;
                if (string.IsNullOrEmpty(contract.Runtime))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "attested computation missing required 'runtime'", DiagnosticCode.ComputationMissingRuntime, "runtime"));
                }

                foreach (var parameter in contract.Parameters)
                {
                    if (parameter.Name.Length == 0)
                    {
                        diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "parameter entry missing 'name'", DiagnosticCode.ComputationParameterMissingName, "parameters"));
                    }
                }

                var inlineCode = ComputationExtractor.ExtractInline(concept.Document.Body);
                if (string.IsNullOrEmpty(contract.ComputationPath) && inlineCode is null)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "attested computation has no computation (inline '# Computation' or 'computation:' path)", DiagnosticCode.ComputationMissingBody));
                }
                else if (!string.IsNullOrEmpty(contract.ComputationPath) && inlineCode is not null)
                {
                    // Computation() itself prioritizes the `computation:` path
                    // over an inline `# Computation` block (§10.3), so the
                    // inline presence has to be checked independently here to
                    // flag the ambiguity rather than silently picking one.
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "computation specified both inline and via 'computation:'", DiagnosticCode.ComputationAmbiguous, "computation"));
                }

                if (fm.Get("executor") is YamlMapping executorMap
                    && executorMap.Get("receipt") is not null and not YamlNull and not YamlSequence)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "executor.receipt is not a list of receipt field names", DiagnosticCode.ExecutorReceiptInvalid, "executor.receipt"));
                }

                if (contract.Attester is { } attester && string.IsNullOrEmpty(attester.Resource))
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, "attester.resource is empty", DiagnosticCode.AttesterResourceEmpty, "attester.resource"));
                }
            }

            // §6.2 path-valued frontmatter fields: a broken or bundle-escaping
            // path is soft guidance (never a §11 conformance error).
            foreach (var resource in concept.Document.FrontmatterResources())
            {
                if (resource.Kind == FrontmatterResourceKind.Url)
                {
                    continue;
                }

                bundle.TryResolveResource(concept, resource.RawPath, out _, out var resolutionStatus);
                if (resolutionStatus == ResourceResolutionStatus.Missing)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"frontmatter path '{resource.Field}' → '{resource.RawPath}' not found", DiagnosticCode.FrontmatterPathMissing, resource.Field));
                }
                else if (resolutionStatus == ResourceResolutionStatus.Unsafe)
                {
                    diagnostics.Add(new Diagnostic(Severity.Warning, concept.Path, concept.Id, $"frontmatter path '{resource.Field}' → '{resource.RawPath}' escapes the bundle", DiagnosticCode.FrontmatterPathUnsafe, resource.Field));
                }
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
                $"link target does not resolve to a concept in the bundle: {raw}",
                DiagnosticCode.BrokenLink));
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

    /// <summary>Checks that reserved files (index.md and log.md) follow their structural rules when present (§8/§9).</summary>
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
            catch (IOException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable index.md: {e.Message}", DiagnosticCode.UnparseableIndex));
                continue;
            }
            catch (UnauthorizedAccessException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable index.md: {e.Message}", DiagnosticCode.UnparseableIndex));
                continue;
            }
            catch (System.Text.DecoderFallbackException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable index.md: {e.Message}", DiagnosticCode.UnparseableIndex));
                continue;
            }

            OkfDocument doc;
            try
            {
                doc = OkfDocument.Parse(text);
            }
            catch (DocumentParseException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable index.md: {e.Message}", DiagnosticCode.UnparseableIndex));
                continue;
            }

            if (doc.Frontmatter.IsEmpty)
            {
                continue;
            }

            // Frontmatter is only permitted in the bundle-root index.md, and only
            // to declare `okf_version` (§12).
            var isRoot = string.Equals(path, rootIndex, StringComparison.Ordinal);
            if (!isRoot)
            {
                diagnostics.Add(new Diagnostic(
                    Severity.Error,
                    path,
                    null,
                    "index.md must not contain frontmatter (§8)",
                    DiagnosticCode.IndexHasFrontmatter));
            }
            else
            {
                var onlyVersion = doc.Frontmatter.AsMapping().Keys.All(k => k == "okf_version");
                if (!onlyVersion)
                {
                    diagnostics.Add(new Diagnostic(
                        Severity.Error,
                        path,
                        null,
                        "root index.md frontmatter must declare only `okf_version` (§12)",
                        DiagnosticCode.RootIndexExtraFrontmatter,
                        "okf_version"));
                }

                var declaredVersion = doc.Frontmatter.Get("okf_version")?.AsDisplayString();
                if (declaredVersion is not null && !string.Equals(declaredVersion, OkfSpec.Version, StringComparison.Ordinal))
                {
                    diagnostics.Add(new Diagnostic(
                        Severity.Warning,
                        path,
                        null,
                        $"declared okf_version {DebugQuote.Quote(declaredVersion)} is not supported; consuming best-effort as v{OkfSpec.Version}",
                        DiagnosticCode.UnsupportedOkfVersion,
                        "okf_version"));
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
            catch (IOException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable log.md: {e.Message}", DiagnosticCode.UnparseableLog));
                continue;
            }
            catch (UnauthorizedAccessException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable log.md: {e.Message}", DiagnosticCode.UnparseableLog));
                continue;
            }
            catch (System.Text.DecoderFallbackException e)
            {
                diagnostics.Add(new Diagnostic(Severity.Error, path, null, $"unparseable log.md: {e.Message}", DiagnosticCode.UnparseableLog));
                continue;
            }

            var log = ChangeLog.Parse(text);
            foreach (var bad in log.InvalidDates())
            {
                diagnostics.Add(new Diagnostic(
                    Severity.Error,
                    path,
                    null,
                    $"log date heading is not ISO-8601 `YYYY-MM-DD`: {DebugQuote.Quote(bad)}",
                    DiagnosticCode.LogDateInvalid));
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
