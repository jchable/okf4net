// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

namespace OKF4net.Internal;

/// <summary>
/// The audit summary, one audit finding line, and one verification record
/// line -- rendered once and shared by <c>okf audit</c>/<c>okf verify</c>
/// (<c>OKF4net.Cli</c>, assembly name <c>okf</c>) and the
/// <c>okf_audit</c>/<c>okf_verify</c> agent tools (<c>OKF4net.Agents</c>);
/// both have <c>InternalsVisibleTo</c> on this assembly (see
/// <c>OKF4net.csproj</c>). Internal rather than public: this renders
/// golden-locked CLI text (<c>tests/fixtures/golden/audit-v02.out</c>,
/// <c>verify.out</c>), and publishing a text-format API on the NuGet package
/// would freeze that wording for a consumer that does not exist.
/// </summary>
internal static class AuditText
{
    /// <summary>
    /// Writes the bundle-wide summary counters shared by both audit
    /// renderers: <c>as of:</c> through the <c>stale:</c> line, including its
    /// trailing newline. Deliberately excludes the leading <c>bundle:</c>
    /// line and the trailing worklist heading/lines -- each caller frames
    /// those itself (the CLI always prints a <c>bundle:</c> line and a
    /// <c>needs attention</c> heading; the tool prints neither, and picks
    /// <c>needs attention</c> or <c>selected</c> depending on its own
    /// <c>staleOnly</c> parameter, capping the worklist at 20).
    /// </summary>
    /// <param name="w">The writer to append to.</param>
    /// <param name="report">The audit report to summarize.</param>
    public static void WriteSummary(TextWriter w, AuditReport report)
    {
        w.Write($"as of:      {report.AsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\n");
        w.Write($"concepts:   {report.ConceptCount}\n");

        // Labels always come from AuditVocabulary -- never as literals here.
        // Duplicating them in each renderer is exactly the drift the shared
        // vocabulary exists to prevent. Only the ORDER is decided locally: the
        // report shows the strongest tier first, so it walks the canonical
        // (weakest-first) list in reverse.
        w.Write("\ntrust:\n");
        foreach (var tier in AuditVocabulary.TrustTiersInOrder.Reverse())
        {
            w.Write($"  {report.TrustCounts[tier],4}  {AuditVocabulary.Name(tier)}\n");
        }

        w.Write("\nstatus:\n");
        foreach (var status in AuditVocabulary.StatusesInOrder)
        {
            w.Write($"  {report.StatusCounts[status],4}  {AuditVocabulary.Name(status)}\n");
        }

        w.Write($"\nstale:      {report.StaleCount} of {report.ConceptCount} past stale_after\n");
    }

    /// <summary>Renders one concept line: id, freshness, trust tier, status -- two spaces between fields.</summary>
    /// <param name="finding">The finding to render.</param>
    public static string FormatFinding(AuditFinding finding)
    {
        var freshness = AuditVocabulary.Freshness(finding.Lifecycle, finding.IsStale);

        return $"{finding.Id}  {freshness}  {AuditVocabulary.Name(finding.Trust)}  {AuditVocabulary.Name(finding.Lifecycle.Status)}";
    }

    /// <summary>
    /// Renders one <c>okf verify</c>/<c>okf_verify</c> record line:
    /// <c>recorded {id}  {by}  {at}</c>, with a trailing
    /// <c>  (replaces {previous at})</c> when the stamp superseded an earlier
    /// one.
    /// </summary>
    /// <param name="record">The record <see cref="BundleConceptWriter.RecordVerifications"/> reported.</param>
    /// <param name="by">The actor the caller verified as -- echoed as given, not re-read from the record.</param>
    public static string FormatVerificationRecord(VerificationRecord record, string by)
    {
        var replaces = record.ReplacedAt is { } previous ? $"  (replaces {previous})" : string.Empty;
        return $"recorded {record.ConceptId}  {by}  {record.At}{replaces}";
    }
}
