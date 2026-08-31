// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Unit tests for <see cref="ConceptAudit"/>: tier and status counting,
/// the §5.5 staleness boundary, predicate composition and ordering.
/// Every test pins the date with <see cref="FixedClock"/> so nothing here
/// depends on the day the suite runs.
/// </summary>
public class AuditTests
{
    private static readonly DateOnly Today = new(2026, 8, 21);

    private static Bundle Load(TempDir tmp) => Bundle.Load(tmp.Path);

    private static AuditReport Audit(TempDir tmp, AuditQuery query = default)
        => ConceptAudit.Run(Load(tmp), query, new FixedClock(Today));

    [Fact]
    public void Counts_the_three_trust_tiers()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\nverified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n---\n");
        tmp.Write("b.md", "---\ntype: Metric\nverified:\n  - { by: process:nightly, at: 2026-01-01T00:00:00Z }\n---\n");
        tmp.Write("c.md", "---\ntype: Metric\n---\n");

        var report = Audit(tmp);

        Assert.Equal(3, report.ConceptCount);
        Assert.Equal(1, report.TrustCounts[TrustTier.HumanReviewed]);
        Assert.Equal(1, report.TrustCounts[TrustTier.MachineConfirmed]);
        Assert.Equal(1, report.TrustCounts[TrustTier.Unverified]);
    }

    [Fact]
    public void Unknown_status_counts_as_stable()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\nstatus: retired\n---\n");
        tmp.Write("b.md", "---\ntype: Metric\nstatus: draft\n---\n");

        var report = Audit(tmp);

        Assert.Equal(1, report.StatusCounts[ConceptStatus.Stable]);
        Assert.Equal(1, report.StatusCounts[ConceptStatus.Draft]);
        Assert.Equal(0, report.StatusCounts[ConceptStatus.Deprecated]);
    }

    [Theory]
    [InlineData("2026-08-21", true)]   // §5.5: today >= stale_after -- the exact boundary IS stale.
    [InlineData("2026-08-22", false)]
    public void Staleness_boundary_follows_section_5_5(string staleAfter, bool expectedStale)
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", $"---\ntype: Metric\nstale_after: {staleAfter}\n---\n");

        // The default query filters nothing, so the single concept is always returned.
        var report = Audit(tmp);

        Assert.Equal(expectedStale, report.Findings.Single().IsStale);
        Assert.Equal(expectedStale ? 1 : 0, report.StaleCount);
    }

    [Fact]
    public void Malformed_or_absent_stale_after_is_never_stale()
    {
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntype: Metric\nstale_after: not-a-date\n---\n");
        tmp.Write("none.md", "---\ntype: Metric\n---\n");

        var report = Audit(tmp);

        Assert.Equal(0, report.StaleCount);
        Assert.Empty(ConceptAudit.Run(Load(tmp), new AuditQuery(StaleOnly: true), new FixedClock(Today)).Findings);
        Assert.True(report.Findings.Single(f => f.Id.ToString() == "bad").Lifecycle.StaleAfterMalformed);
    }

    [Fact]
    public void Predicates_compose_with_and()
    {
        using var tmp = new TempDir();
        tmp.Write("stale-unverified.md", "---\ntype: Metric\nstale_after: 2026-01-01\n---\n");
        tmp.Write("stale-human.md", "---\ntype: Metric\nstale_after: 2026-01-01\nverified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n---\n");
        tmp.Write("fresh-unverified.md", "---\ntype: Metric\nstale_after: 2099-01-01\n---\n");

        var query = new AuditQuery(
            StaleOnly: true,
            Trust: new HashSet<TrustTier> { TrustTier.Unverified });

        var findings = ConceptAudit.Run(Load(tmp), query, new FixedClock(Today)).Findings;

        Assert.Equal(["stale-unverified"], findings.Select(f => f.Id.ToString()));
    }

    [Fact]
    public void Findings_are_sorted_by_concept_id_component_wise()
    {
        using var tmp = new TempDir();
        tmp.Write("zeta.md", "---\ntype: Metric\n---\n");
        tmp.Write("alpha.md", "---\ntype: Metric\n---\n");
        tmp.Write("mid/beta.md", "---\ntype: Metric\n---\n");
        // Discriminating pair: component-wise ordering differs from flat ordinal.
        // ConceptId.CompareTo compares segments; "orders/extra" has segments ["orders", "extra"],
        // and "orders-extra" is a single segment. The first segment "orders" < "orders-extra",
        // so "orders/extra" comes before "orders-extra" under component-wise ordering.
        tmp.Write("orders-extra.md", "---\ntype: Metric\n---\n");
        tmp.Write("orders/extra.md", "---\ntype: Metric\n---\n");

        var findings = Audit(tmp).Findings.Select(f => f.Id.ToString()).ToList();

        Assert.Equal(["alpha", "mid/beta", "orders/extra", "orders-extra", "zeta"], findings);
    }

    [Fact]
    public void Counts_cover_the_whole_bundle_even_when_the_query_filters()
    {
        using var tmp = new TempDir();
        tmp.Write("stale.md", "---\ntype: Metric\nstale_after: 2026-01-01\n---\n");
        tmp.Write("fresh.md", "---\ntype: Metric\n---\n");

        var report = ConceptAudit.Run(Load(tmp), new AuditQuery(StaleOnly: true), new FixedClock(Today));

        Assert.Single(report.Findings);
        Assert.Equal(2, report.ConceptCount);
        Assert.Equal(2, report.TrustCounts[TrustTier.Unverified]);
        Assert.Equal(1, report.StaleCount);
    }

    [Fact]
    public void Empty_bundle_yields_zeroed_counts_and_no_findings()
    {
        using var tmp = new TempDir();

        var report = Audit(tmp);

        Assert.Equal(0, report.ConceptCount);
        Assert.Empty(report.Findings);
        Assert.Equal(0, report.TrustCounts[TrustTier.HumanReviewed]);
        Assert.Equal(0, report.StatusCounts[ConceptStatus.Deprecated]);
    }

    /// <summary>
    /// A document whose frontmatter cannot be parsed lands in
    /// <c>Bundle.ParseErrors</c> (permissive loading) and is not a concept, so
    /// it must not reach any counter. Note this is a *parse* failure -- a truly
    /// unreadable file (I/O, permissions, non-UTF-8) throws
    /// <c>BundleLoadException</c> and never reaches <see cref="ConceptAudit"/>.
    /// </summary>
    [Fact]
    public void Unparseable_documents_are_excluded_from_every_count()
    {
        using var tmp = new TempDir();
        tmp.Write("ok.md", "---\ntype: Metric\n---\n");
        tmp.Write("broken.md", "---\ntype: Metric\n");  // unterminated frontmatter block

        var bundle = Load(tmp);
        Assert.Single(bundle.ParseErrors);

        var report = ConceptAudit.Run(bundle, default, new FixedClock(Today));

        Assert.Equal(1, report.ConceptCount);
        Assert.Single(report.Findings);
    }

    [Fact]
    public void Null_clock_falls_back_to_today_in_utc()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n");

        // The only test here that does not pin the clock -- it is the one
        // asserting the unpinned fallback. Sampling UtcNow on both sides of the
        // call would flake on a run crossing midnight UTC, so the assertion
        // accepts either date the call could legitimately have observed.
        var before = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var report = ConceptAudit.Run(Load(tmp));
        var after = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        Assert.True(
            report.AsOf == before || report.AsOf == after,
            $"AsOf {report.AsOf} was neither {before} nor {after}");
    }

    [Fact]
    public void Type_filter_is_exact_and_ordinal()
    {
        using var tmp = new TempDir();
        tmp.Write("metric.md", "---\ntype: Metric\n---\n");
        tmp.Write("lower.md", "---\ntype: metric\n---\n");
        tmp.Write("untyped.md", "---\ntitle: No type here\n---\n");

        var findings = ConceptAudit.Run(Load(tmp), new AuditQuery(Type: "Metric"), new FixedClock(Today)).Findings;

        Assert.Equal(["metric"], findings.Select(f => f.Id.ToString()));
    }

    [Fact]
    public void Vocabulary_names_round_trip()
    {
        Assert.Equal("human-reviewed", AuditVocabulary.Name(TrustTier.HumanReviewed));
        Assert.Equal("machine-confirmed", AuditVocabulary.Name(TrustTier.MachineConfirmed));
        Assert.Equal("unverified", AuditVocabulary.Name(TrustTier.Unverified));
        Assert.Equal("draft", AuditVocabulary.Name(ConceptStatus.Draft));

        Assert.True(AuditVocabulary.TryParseTrustTier("machine-confirmed", out var tier));
        Assert.Equal(TrustTier.MachineConfirmed, tier);
        Assert.False(AuditVocabulary.TryParseTrustTier("machine", out _));

        Assert.True(AuditVocabulary.TryParseStatus("deprecated", out var status));
        Assert.Equal(ConceptStatus.Deprecated, status);
        Assert.False(AuditVocabulary.TryParseStatus("retired", out _));
    }

    /// <summary>
    /// The single grammar shared by the CLI's <c>--trust</c> flag and the
    /// <c>okf_audit</c> tool's <c>trust</c> parameter: comma-split, trim each
    /// entry, absorb duplicates (the result is a set), fail on the first
    /// unparseable entry and report it (trimmed) via <paramref name="badEntry"/>.
    /// </summary>
    [Fact]
    public void TryParseTrustTiers_trims_absorbs_duplicates_and_reports_the_bad_entry()
    {
        Assert.True(AuditVocabulary.TryParseTrustTiers(
            "unverified, unverified,human-reviewed", out var tiers, out var badEntry));
        Assert.Equal(
            new HashSet<TrustTier> { TrustTier.Unverified, TrustTier.HumanReviewed },
            tiers);
        Assert.Null(badEntry);

        Assert.False(AuditVocabulary.TryParseTrustTiers(
            "unverified,,human-reviewed", out var afterFailure, out var emptyEntry));
        Assert.Empty(afterFailure);
        Assert.Equal("", emptyEntry);

        Assert.False(AuditVocabulary.TryParseTrustTiers("bogus", out _, out var unknownEntry));
        Assert.Equal("bogus", unknownEntry);
    }

    /// <summary>
    /// <see cref="AuditQuery.IsFiltered"/> answers "does this query constrain
    /// the selection?", not "did the caller type a flag?" -- the CLI's report
    /// mode builds <c>new AuditQuery(StaleOnly: true)</c> itself, with no flag
    /// typed, and must still see <see langword="true"/> here.
    /// </summary>
    [Fact]
    public void IsFiltered_reflects_whether_the_query_constrains_the_selection()
    {
        Assert.False(AuditQuery.All.IsFiltered);
        Assert.True(new AuditQuery(StaleOnly: true).IsFiltered);
    }

    [Fact]
    public void Audit_detects_staleness_from_a_conformant_instant_stale_after()
    {
        // Before this fix, a §5-conformant stale_after failed to parse, so the
        // concept was reported fresh forever. This is the defect, end to end.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\nstale_after: 2026-01-01T00:00:00Z\n---\n");

        var report = Audit(tmp);

        Assert.True(report.Findings.Single().IsStale);
        Assert.Equal(1, report.StaleCount);
    }

    [Fact]
    public void Audit_resolves_staleness_to_the_hour_not_the_day()
    {
        // A concept expiring at 18:00Z is fresh when audited at 09:00Z the same
        // day and stale at 20:00Z -- a distinction the pre-fix date-only
        // comparison could not make.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\nstale_after: 2026-08-21T18:00:00Z\n---\n");

        var morning = ConceptAudit.Run(Load(tmp), default, new FixedClock(new DateTimeOffset(2026, 8, 21, 9, 0, 0, TimeSpan.Zero)));
        var evening = ConceptAudit.Run(Load(tmp), default, new FixedClock(new DateTimeOffset(2026, 8, 21, 20, 0, 0, TimeSpan.Zero)));

        Assert.False(morning.Findings.Single().IsStale);
        Assert.True(evening.Findings.Single().IsStale);

        // AsOf stays a date in both cases -- it is the report's display stamp.
        Assert.Equal(new DateOnly(2026, 8, 21), morning.AsOf);
        Assert.Equal(new DateOnly(2026, 8, 21), evening.AsOf);
    }

    [Fact]
    public void Freshness_still_renders_a_bare_date_for_a_conformant_instant()
    {
        // Golden-locked format: tests/fixtures/golden/audit-v02.out captures
        // "fresh 2099-01-01". Changing this rendering breaks the goldens.
        var lc = Lifecycle.From(null, "2099-01-01T00:00:00Z");

        Assert.Equal("fresh 2099-01-01", AuditVocabulary.Freshness(lc, isStale: false));
    }
}
