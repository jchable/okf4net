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
    ///
    /// <para><b>Line-safe by itself, not by its callers (#123).</b> All four
    /// values go through <see cref="Field"/>, so none of them can end this
    /// line or drive a terminal whatever its caller checked first. The
    /// callers' own guards still stand -- <c>--by</c> is refused by
    /// <see cref="LineSafeText.ContainsControlCharacter"/>, the id has passed
    /// <see cref="ConceptId"/>'s grammar, <c>at</c> is the writer's own
    /// strictly-formatted stamp -- but none of that is visible here, and the
    /// fourth value never had a guard at all: <c>ReplacedAt</c> is the
    /// <c>at</c> already STORED in the concept's <c>verified</c> list, which a
    /// hand edit or <c>okf_write_concept</c> can fill with anything, and a
    /// <c>\n</c> in it printed a forged second <c>recorded …</c> line through
    /// both <c>okf verify</c> and <c>okf_verify</c>.</para>
    /// </summary>
    /// <param name="record">The record <see cref="BundleConceptWriter.RecordVerifications"/> reported.</param>
    /// <param name="by">The actor the caller verified as -- echoed as given, not re-read from the record.</param>
    public static string FormatVerificationRecord(VerificationRecord record, string by)
    {
        var replaces = record.ReplacedAt is { } previous ? $"  (replaces {Field(previous)})" : string.Empty;
        return $"recorded {Field(record.ConceptId)}  {Field(by)}  {Field(record.At)}{replaces}";
    }

    /// <summary>Renders one <c>okf verify --dry-run</c> line.</summary>
    /// <param name="conceptId">The id as the caller named it.</param>
    /// <param name="by">The actor the caller would verify as.</param>
    /// <param name="at">The caller's <c>--at</c>, or <see langword="null"/> when the stamp would be "now".</param>
    public static string FormatDryRunRecord(string conceptId, string by, string? at) =>
        $"would record {conceptId}  {by}  {at ?? "(now)"}";

    /// <summary>
    /// One interpolated value, made safe to sit inside a single rendered
    /// line: returned verbatim when it carries nothing that would break a
    /// line-oriented rendering of it, and as a <see cref="DebugQuote.Quote"/>
    /// literal (escaped, double-quoted) when it does.
    ///
    /// <para><b>Conditional on purpose.</b> Quoting every value would change
    /// every byte of <c>verify.out</c> for no gain; quoting only a value that
    /// needs it is a no-op on everything the golden captures (and every value
    /// the callers' guards admit), so the CLI's wording stays put. The
    /// trigger is the one shared predicate,
    /// <see cref="LineSafeText.ContainsControlCharacter"/> (every C0/C1
    /// control, plus U+2028/U+2029), and the spelling is the one shared
    /// quoter, <see cref="DebugQuote.Quote"/> -- the same helper every
    /// <c>okf verify</c> refusal already quotes an id with -- so neither the
    /// character test nor the escape format is forked here.</para>
    ///
    /// <para>Escape rather than fold to a space: a folded value reads as an
    /// ordinary one (<c>human:ada recorded x …</c>), where the quoted form
    /// says exactly what is in the value and that something unusual is. Bidi
    /// controls (Cf, not Cc) are out of scope, as they are for both shared
    /// helpers: they reorder a line's display, they do not end it.</para>
    /// </summary>
    /// <param name="value">A value about to be interpolated into one line.</param>
    private static string Field(string value) =>
        LineSafeText.ContainsControlCharacter(value) ? DebugQuote.Quote(value) : value;
}
