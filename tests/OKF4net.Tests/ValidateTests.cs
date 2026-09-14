// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Conformance-checking tests, exercised rule-by-rule against
/// <c>BundleValidator.Validate</c>. Each test targets exactly one
/// diagnostic-producing rule and asserts its exact severity and message
/// shape: only true §11 violations -- unparseable frontmatter, missing/empty
/// `type`, and reserved files that fail to follow their §8/§9 structure
/// (malformed/unreadable `index.md`/`log.md`) -- are <see cref="Severity.Error"/>;
/// everything else is <see cref="Severity.Warning"/> or <see cref="Severity.Info"/>.
/// </summary>
public class ValidateTests
{
    [Fact]
    public void ToString_ignores_Code_and_Field()
    {
        var withField = new Diagnostic(Severity.Warning, "a.md", null, "msg", DiagnosticCode.LegacyTimestamp, "timestamp");
        var withoutField = new Diagnostic(Severity.Warning, "a.md", null, "msg", DiagnosticCode.LegacyTimestamp);
        Assert.Equal("[warning] a.md: msg", withField.ToString());
        Assert.Equal(withField.ToString(), withoutField.ToString());
    }

    [Fact]
    public void Unparseable_frontmatter_is_an_error()
    {
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntype: [unterminated\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        Assert.Single(bundle.ParseErrors);

        var report = BundleValidator.Validate(bundle);
        var diag = Assert.Single(report.Of(Severity.Error));
        Assert.StartsWith("unparseable concept document: ", diag.Message);
        Assert.Equal(DiagnosticCode.UnparseableDocument, diag.Code);
        Assert.False(report.IsConformant);
        Assert.Equal(1, report.ErrorCount);
    }

    [Fact]
    public void Unparseable_index_is_an_error()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
        tmp.Write("broken/index.md", "---\ntitle: [unterminated\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Error), d => d.Code == DiagnosticCode.UnparseableIndex);
        Assert.StartsWith("unparseable index.md: ", diag.Message);
        Assert.False(report.IsConformant);
    }

    [Fact]
    public void Unreadable_index_bytes_are_an_error()
    {
        // A distinct code path from Unparseable_index_is_an_error above: this
        // exercises the DecoderFallbackException branch (invalid UTF-8 bytes
        // that never reach OkfDocument.Parse), not the YAML-parse-failure
        // branch. Same raw-bytes technique as
        // OkfValidateChangesTests.ChangesSince_skips_a_non_utf8_log_file_with_a_note_instead_of_throwing.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "broken"));
        File.WriteAllBytes(Path.Combine(tmp.Path, "broken", "index.md"), [0x23, 0x20, 0xFF, 0xFE, 0x0A]);
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Error), d => d.Code == DiagnosticCode.UnparseableIndex);
        Assert.StartsWith("index.md could not be read: ", diag.Message);
        Assert.False(report.IsConformant);
    }

    [Fact]
    public void Unreadable_log_bytes_are_an_error()
    {
        // ChangeLog.Parse never throws, so this DecoderFallbackException
        // branch (invalid UTF-8 bytes) is the only way DiagnosticCode
        // .UnparseableLog can fire in practice -- there is no analogous
        // "malformed but decodable" parse-failure branch for log.md the way
        // there is for index.md's YAML frontmatter.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
        File.WriteAllBytes(Path.Combine(tmp.Path, "log.md"), [0x23, 0x20, 0xFF, 0xFE, 0x0A]);
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Error), d => d.Code == DiagnosticCode.UnparseableLog);
        Assert.StartsWith("log.md could not be read: ", diag.Message);
        Assert.False(report.IsConformant);
    }

    [Fact]
    public void Missing_type_is_an_error()
    {
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntitle: No Type\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Error));
        Assert.Equal("missing required frontmatter field `type`", diag.Message);
        Assert.Equal(DiagnosticCode.MissingType, diag.Code);
        Assert.Equal("type", diag.Field);
        Assert.False(report.IsConformant);
    }

    [Fact]
    public void Empty_type_string_is_an_error()
    {
        // Document.ValidateConformance requires a non-empty `type`; an
        // explicit empty string counts as empty too.
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntype: \"\"\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.Contains(report.Of(Severity.Error), d => d.Message == "missing required frontmatter field `type`");
    }

    [Fact]
    public void Missing_recommended_fields_are_warnings()
    {
        // title/description/resource/tags are soft guidance.
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntype: Note\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.True(report.IsConformant);
        var warnings = report.Of(Severity.Warning).ToList();
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `title`" && d.Code == DiagnosticCode.MissingRecommendedField && d.Field == "title");
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `description`" && d.Code == DiagnosticCode.MissingRecommendedField && d.Field == "description");
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `resource`" && d.Code == DiagnosticCode.MissingRecommendedField && d.Field == "resource");
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `tags`" && d.Code == DiagnosticCode.MissingRecommendedField && d.Field == "tags");
        Assert.DoesNotContain(warnings, d => d.Message.Contains("`timestamp`"));
    }

    /// <summary>
    /// §4.1 qualifies `resource` where it recommends it: "A URI that uniquely
    /// identifies the underlying asset the concept describes. Absent for
    /// concepts that describe abstract ideas rather than physical resources."
    /// A §10 Attested Computation is the one concept the spec itself both
    /// names normatively (§10.1: "a standalone concept of
    /// `type: Attested Computation`") and shows without a `resource` in every
    /// example it gives (§10.2, and all of Appendix A's). Warning there says a
    /// well-formed concept is deficient, so it is suppressed -- narrowly, and
    /// only for that type, because §4.1 leaves the type vocabulary open and no
    /// syntactic test decides "abstract" in general (S4.1-8 in
    /// docs/spec-conformance/).
    /// </summary>
    [Fact]
    public void Attested_computation_is_not_warned_for_a_missing_resource()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\ntitle: T\ndescription: D\ntags: [x]\n---\n# Computation\n\n```sql\nSELECT 1\n```\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.DoesNotContain(report.Of(Severity.Warning), d => d.Field == "resource");
    }

    /// <summary>
    /// The other half of S4.1-8: the suppression is keyed on the §10 type and
    /// nothing else. A concept that is equally abstract but carries any other
    /// `type` still gets the warning, because §4.1's carve-out is stated in
    /// terms of meaning and only §10.1 supplies a type name to key on.
    /// </summary>
    [Fact]
    public void A_non_computation_concept_is_still_warned_for_a_missing_resource()
    {
        using var tmp = new TempDir();
        tmp.Write("m/rev.md", "---\ntype: Metric\ntitle: T\ndescription: D\ntags: [x]\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.Contains(report.Of(Severity.Warning), d => d.Field == "resource" && d.Code == DiagnosticCode.MissingRecommendedField);
    }

    /// <summary>
    /// The limit of S4.1-8's carve-out: it suppresses the warning for an
    /// ABSENT `resource` key only. §4.1's sentence licenses absence
    /// ("**Absent** for concepts that describe abstract ideas rather than
    /// physical resources") -- it does not license declaring the key with a
    /// value that is not the "URI that uniquely identifies the underlying
    /// asset" §4.1 specifies. An empty string, an explicit null, an empty
    /// list and an empty mapping are each a malformed value, not an expression
    /// of abstractness, so they keep warning exactly as they do for
    /// `title`/`description`/`tags`
    /// (see <see cref="Empty_recommended_field_values_are_also_warnings"/>).
    ///
    /// `false` and `0` are in here because
    /// <see cref="OKF4net.Yaml.YamlValue.IsEmptyValue"/> is a falsiness test,
    /// not an emptiness test: a carve-out that skipped the value check
    /// entirely swallowed those too, and no reading of "absent" covers
    /// `resource: 0`.
    /// </summary>
    [Theory]
    [InlineData("resource: \"\"")]
    [InlineData("resource: null")]
    [InlineData("resource:")]
    [InlineData("resource: []")]
    [InlineData("resource: {}")]
    [InlineData("resource: false")]
    [InlineData("resource: 0")]
    public void An_attested_computation_is_still_warned_for_a_present_but_empty_resource(string resourceLine)
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", $"---\ntype: Attested Computation\nruntime: bigquery\ntitle: T\ndescription: D\ntags: [x]\n{resourceLine}\n---\n# Computation\n\n```sql\nSELECT 1\n```\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.Contains(report.Of(Severity.Warning), d => d.Field == "resource" && d.Code == DiagnosticCode.MissingRecommendedField);
    }

    [Fact]
    public void Empty_recommended_field_values_are_also_warnings()
    {
        // An explicit but empty value (empty string) is treated the same as
        // an absent one.
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntype: Note\ntitle: \"\"\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.Contains(report.Of(Severity.Warning), d => d.Message == "missing recommended frontmatter field `title`");
    }

    [Fact]
    public void Nonempty_recommended_fields_produce_no_warning_for_them()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "ok.md",
            "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\ntimestamp: 2026-05-28\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.DoesNotContain(report.Diagnostics, d => d.Message.Contains("recommended"));
    }

    [Fact]
    public void Non_iso_timestamp_is_a_warning()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "bad.md",
            "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: not-a-date\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.DoesNotContain(report.Of(Severity.Info), d => d.Message.Contains("timestamp"));
        Assert.Contains(report.Of(Severity.Warning), d => d.Message.Contains("timestamp", StringComparison.Ordinal));
    }

    [Fact]
    public void Iso_timestamp_produces_no_warning()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "ok.md",
            "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: 2026-05-28T00:00:00Z\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.DoesNotContain(report.Diagnostics, d => d.Message.Contains("ISO-8601") && d.Message.Contains("timestamp"));
    }

    [Fact]
    public void Nonroot_index_with_frontmatter_is_an_error()
    {
        // frontmatter is only permitted in the bundle-root index.md.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
        tmp.Write("sub/index.md", "---\ntitle: nope\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Error));
        Assert.Equal("index.md must not contain frontmatter (§8)", diag.Message);
        Assert.Equal(DiagnosticCode.IndexHasFrontmatter, diag.Code);
        Assert.False(report.IsConformant);
    }

    [Fact]
    public void Root_index_frontmatter_with_only_okf_version_is_clean()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.DoesNotContain(report.Diagnostics, d => d.Message.Contains("index.md"));
    }

    [Fact]
    public void Root_index_frontmatter_with_extra_keys_is_an_error()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\ntitle: extra\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.Contains(report.Of(Severity.Error), d => d.Message == "root index.md frontmatter must declare only `okf_version` (§12)" && d.Code == DiagnosticCode.RootIndexExtraFrontmatter && d.Field == "okf_version");
        Assert.False(report.IsConformant);
    }

    [Fact]
    public void Index_with_no_frontmatter_produces_no_diagnostic()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        tmp.Write("index.md", "# Listing\n\n* [a](a.md)\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.DoesNotContain(report.Diagnostics, d => d.Message.Contains("index.md"));
    }

    [Fact]
    public void Invalid_log_date_heading_is_an_error()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
        tmp.Write("log.md", "# Log\n\n## not-a-date\n* **Update**: did a thing.\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Error));
        Assert.Equal("log date heading is not ISO-8601 `YYYY-MM-DD`: \"not-a-date\"", diag.Message);
        Assert.Equal(DiagnosticCode.LogDateInvalid, diag.Code);
        Assert.False(report.IsConformant);
    }

    [Fact]
    public void Valid_log_date_heading_produces_no_warning()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        tmp.Write("log.md", "# Log\n\n## 2026-05-22\n* **Update**: did a thing.\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.DoesNotContain(report.Diagnostics, d => d.Message.Contains("log date"));
    }

    [Fact]
    public void Broken_link_is_info_not_error_or_warning()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nSee [missing](/does/not/exist.md).\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.True(report.IsConformant);
        var diag = Assert.Single(report.Of(Severity.Info));
        Assert.Equal(
            "link target does not resolve to a concept in the bundle: /does/not/exist.md",
            diag.Message);
        Assert.Equal(DiagnosticCode.BrokenLink, diag.Code);
    }

    [Fact]
    public void ErrorCount_and_WarningCount_reflect_only_their_own_severity()
    {
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntitle: No Type\n---\nbody\n"); // 1 error (missing type), 3 recommended-field warnings (description, resource, tags)
        tmp.Write("log.md", "# Log\n\n## nope\n* x\n"); // 1 more error (invalid log date, now Error instead of Warning)
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.Equal(2, report.ErrorCount);
        Assert.Equal(3, report.WarningCount);
        Assert.False(report.IsConformant);
    }

    [Theory]
    [InlineData("2026-05-28", true)]
    [InlineData("2026-05-28T00:00:00Z", true)]
    [InlineData("2026-05-28T00:00:00", true)]
    [InlineData("2026-05-28 00:00:00", true)]
    [InlineData("not-a-date", false)]
    [InlineData("2026/05/28", false)]
    [InlineData("2026-13-01", false)]
    public void IsIso8601DateTime_splits_on_T_or_space_then_checks_the_date_part(string s, bool expected)
        // IsIso8601DateTime: split on 'T' or ' ', then delegate to the
        // date-only ISO check on the date part.
        => Assert.Equal(expected, BundleValidator.IsIso8601DateTime(s));

    [Fact]
    public void Appendix_a_bundle_is_conformant_with_only_soft_diagnostics()
    {
        // The Appendix A example is conformant (no errors), but under v0.2 it
        // still carries soft diagnostics: customers.md lacks resource/tags,
        // and all three concepts carry the legacy `timestamp` field (Info).
        using var tmp = new TempDir();
        tmp.Write(
            "datasets/sales.md",
            "---\n" +
            "type: BigQuery Dataset\n" +
            "title: Sales\n" +
            "description: All sales-related tables for the retail business.\n" +
            "resource: https://console.cloud.google.com/bigquery?p=acme&d=sales\n" +
            "tags: [sales]\n" +
            "timestamp: 2026-05-28T00:00:00Z\n" +
            "---\n\n" +
            "The sales dataset contains transactional tables, including\n" +
            "[orders](/tables/orders.md) and [customers](/tables/customers.md).\n");
        tmp.Write(
            "tables/orders.md",
            "---\n" +
            "type: BigQuery Table\n" +
            "title: Orders\n" +
            "description: One row per completed customer order.\n" +
            "resource: https://console.cloud.google.com/bigquery?p=acme&d=sales&t=orders\n" +
            "tags: [sales, orders]\n" +
            "timestamp: 2026-05-28T00:00:00Z\n" +
            "---\n\n" +
            "# Schema\n\n" +
            "Part of the [sales dataset](/datasets/sales.md). FK to [customers](/tables/customers.md).\n");
        tmp.Write(
            "tables/customers.md",
            "---\n" +
            "type: BigQuery Table\n" +
            "title: Customers\n" +
            "description: One row per customer.\n" +
            "timestamp: 2026-05-28T00:00:00Z\n" +
            "---\n\n" +
            "Linked from [orders](/tables/orders.md).\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.True(report.IsConformant);
        Assert.Equal(0, report.ErrorCount);
    }

    private static ValidationReport ValidateConcept(string frontmatter, IOkfClock? clock = null)
    {
        using var tmp = new TempDir();
        tmp.Write("c.md", $"---\n{frontmatter}---\nbody\n");
        return BundleValidator.Validate(Bundle.Load(tmp.Path), clock);
    }

    private static bool HasWarning(ValidationReport r, string needle)
        => r.Of(Severity.Warning).Any(d => d.Message.Contains(needle, StringComparison.Ordinal));

    [Fact]
    public void Missing_resource_and_tags_now_warn()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\n");
        Assert.True(HasWarning(r, "missing recommended frontmatter field `resource`"));
        Assert.True(HasWarning(r, "missing recommended frontmatter field `tags`"));
        Assert.True(r.IsConformant); // all Warning, no Error
    }

    [Fact]
    public void Generated_without_by_warns_but_stays_conformant()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ngenerated: {at: '2026-07-27'}\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("generated is missing required `by`") && d.Code == DiagnosticCode.GeneratedMissingBy && d.Field == "generated.by");
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Malformed_actor_warns_strictly()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ngenerated: {by: bob, at: '2026-07-27'}\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("generated.by is not a valid §7 actor") && d.Code == DiagnosticCode.GeneratedInvalidActor && d.Field == "generated.by");
    }

    [Fact]
    public void Verified_list_entry_not_a_mapping_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nverified: [human:ada]\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("verified entry is not a `{by, at}` mapping") && d.Code == DiagnosticCode.VerifiedEntryNotMapping && d.Field == "verified");
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Verified_bare_scalar_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nverified: notamapping\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("verified must be") && d.Code == DiagnosticCode.VerifiedMalformed && d.Field == "verified");
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Unknown_status_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstatus: archived\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("unknown status") && d.Code == DiagnosticCode.StatusUnknown && d.Field == "status");
    }

    [Fact]
    public void Non_scalar_status_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstatus: [draft]\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("status is not a scalar") && d.Code == DiagnosticCode.StatusNotScalar && d.Field == "status");
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Stale_concept_warns_using_injected_clock()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: '2026-01-01'\n",
            new FixedClock(new DateOnly(2026, 7, 27)));
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("concept is stale") && d.Code == DiagnosticCode.ConceptStale && d.Field == "stale_after");
    }

    [Fact]
    public void Source_without_resource_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources:\n  - title: no resource\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("source entry is missing required `resource`") && d.Code == DiagnosticCode.SourceMissingResource && d.Field == "sources.resource");
    }

    [Fact]
    public void Sources_list_entry_not_a_mapping_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources: [just-a-string]\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("source entry is not a mapping") && d.Code == DiagnosticCode.SourceEntryNotMapping && d.Field == "sources");
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Source_last_modified_bad_date_warns()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources:\n  - resource: https://x\n    last_modified: not-a-date\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("source last_modified could not be read as a timestamp") && d.Code == DiagnosticCode.SourceInvalidLastModified && d.Field == "sources.last_modified");
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Well_formed_verified_status_sources_produce_none_of_the_new_warnings()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\n" +
            "status: stable\n" +
            "verified:\n  - by: human:ada\n    at: '2026-07-27'\n" +
            "sources:\n  - resource: https://x\n    last_modified: '2026-07-27'\n");
        Assert.DoesNotContain(r.Diagnostics, d => d.Message.Contains("verified entry is not"));
        Assert.DoesNotContain(r.Diagnostics, d => d.Message.Contains("verified must be"));
        Assert.DoesNotContain(r.Diagnostics, d => d.Message.Contains("status is not a scalar"));
        Assert.DoesNotContain(r.Diagnostics, d => d.Message.Contains("source entry is not a mapping"));
        Assert.DoesNotContain(r.Diagnostics, d => d.Message.Contains("sources must be a list"));
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == DiagnosticCode.SourceInvalidLastModified);
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Legacy_timestamp_is_a_warning()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ntimestamp: '2026-05-28'\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("timestamp", StringComparison.Ordinal) && d.Code == DiagnosticCode.LegacyTimestamp && d.Field == "timestamp");
        Assert.DoesNotContain(r.Of(Severity.Info), d => d.Message.Contains("timestamp", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_okf_version_other_than_current_warns_but_stays_conformant()
    {
        // §12 deliberately keeps this Warning (not Error, unlike the other
        // reserved-file structural violations): "Consumers that do not
        // understand the declared version SHOULD attempt best-effort
        // consumption rather than refusing the bundle." Pinned as its own
        // assertion so a future accidental promotion of this one path is
        // caught here, not just left to whoever notices.
        var dir = Directory.CreateTempSubdirectory("okfv02root").FullName;
        File.WriteAllText(Path.Combine(dir, "index.md"), "---\nokf_version: \"0.9\"\n---\n\n# Index\n");
        File.WriteAllText(Path.Combine(dir, "c.md"), "---\ntype: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\n---\nbody\n");
        var r = BundleValidator.Validate(Bundle.Load(dir));
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("declared okf_version") && d.Code == DiagnosticCode.UnsupportedOkfVersion && d.Field == "okf_version");
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Attested_computation_missing_runtime_warns_but_stays_conformant()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n# Computation absent + pas de computation:\n---\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.True(report.IsConformant);                                   // Error reste §11-only
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("runtime") && d.Code == DiagnosticCode.ComputationMissingRuntime && d.Field == "runtime");
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("no computation") && d.Code == DiagnosticCode.ComputationMissingBody);
    }

    [Fact]
    public void Both_inline_and_path_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: ./x.sql\n---\n# Computation\n\n```\nSELECT 1\n```\n");
        tmp.Write("c/x.sql", "SELECT 1\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("both inline and") && d.Code == DiagnosticCode.ComputationAmbiguous && d.Field == "computation");
    }

    [Fact]
    public void Attester_present_with_no_resource_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nattester: {}\n---\n# Computation\n\n```\nSELECT 1\n```\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("attester.resource") && d.Code == DiagnosticCode.AttesterResourceEmpty && d.Field == "attester.resource");
        Assert.True(report.IsConformant);
    }

    [Fact]
    public void Absent_attester_does_not_warn()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n---\n# Computation\n\n```\nSELECT 1\n```\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.DoesNotContain(report.Diagnostics, d => d.Message.Contains("attester.resource"));
    }

    [Fact]
    public void Broken_frontmatter_path_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: ./missing.md, receipt: [job_id] }\n---\n# Computation\n\n```\nSELECT 1\n```\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("not found") && d.Code == DiagnosticCode.FrontmatterPathMissing && d.Field == "executor.resource");
    }

    [Fact]
    public void Parameter_without_name_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nparameters:\n  - type: integer\n    required: true\n---\n# Computation\n\n```\nSELECT 1\n```\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("missing") && d.Message.Contains("name") && d.Code == DiagnosticCode.ComputationParameterMissingName && d.Field == "parameters");
        Assert.True(report.IsConformant);
    }

    [Fact]
    public void Executor_receipt_not_a_list_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { receipt: nope }\n---\n# Computation\n\n```\nSELECT 1\n```\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("executor.receipt is not a list") && d.Code == DiagnosticCode.ExecutorReceiptInvalid && d.Field == "executor.receipt");
        Assert.True(report.IsConformant);
    }

    [Fact]
    public void Unsafe_frontmatter_path_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: ../../../outside.sql\n---\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("escapes the bundle") && d.Code == DiagnosticCode.FrontmatterPathUnsafe && d.Field == "computation");
        Assert.True(report.IsConformant);
    }

    [Fact]
    public void Fake_heading_inside_earlier_fence_still_warns_no_computation()
    {
        // A heading-like line inside an earlier, unrelated fenced block must
        // not be mistaken for the real "# Computation" heading -- if it
        // were, the unrelated fence's own closing ``` would be misread as
        // opening "the computation", and trailing prose would be extracted
        // and validated as if it were the sanctioned computation instead of
        // correctly triggering the "no computation" warning.
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n---\nSome intro text.\n\n```\n# Computation\n```\n\nSELECT ordinary_body_text\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("no computation"));
    }

    [Fact]
    public void Generated_invalid_date_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ngenerated: {by: 'human:bob', at: 'not-a-date'}\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("generated.at could not be read as a timestamp") && d.Code == DiagnosticCode.GeneratedInvalidDate && d.Field == "generated.at");
    }

    [Fact]
    public void Verified_invalid_actor_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nverified:\n  - by: notanactor\n    at: '2026-07-27'\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("verified.by is not a valid §7 actor") && d.Code == DiagnosticCode.VerifiedInvalidActor && d.Field == "verified.by");
    }

    [Fact]
    public void Verified_invalid_date_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nverified:\n  - by: 'human:bob'\n    at: 'not-a-date'\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("verified.at could not be read as a timestamp") && d.Code == DiagnosticCode.VerifiedInvalidDate && d.Field == "verified.at");
    }

    [Fact]
    public void Sources_malformed_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources: not-a-list\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("sources must be a list of entries") && d.Code == DiagnosticCode.SourcesMalformed && d.Field == "sources");
    }

    [Fact]
    public void Usage_window_invalid_from_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nusage_window: {from: 'not-a-date', to: '2026-07-27'}\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("usage_window from could not be read as a timestamp") && d.Code == DiagnosticCode.UsageWindowInvalidFrom && d.Field == "usage_window.from");
    }

    [Fact]
    public void Usage_window_invalid_to_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nusage_window: {from: '2026-07-27', to: 'not-a-date'}\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("usage_window to could not be read as a timestamp") && d.Code == DiagnosticCode.UsageWindowInvalidTo && d.Field == "usage_window.to");
    }

    [Fact]
    public void Legacy_citations_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c.md", "---\ntype: T\ntitle: X\ndescription: D\nresource: https://x\ntags: [a]\n---\n\nbody text\n\n# Citations\n\n[1] Some citation\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Message.Contains("# Citations") && d.Code == DiagnosticCode.LegacyCitations);
    }

    [Fact]
    public void Stale_after_invalid_date_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: 'not-a-date'\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("stale_after could not be read as a timestamp") && d.Code == DiagnosticCode.StaleAfterInvalid && d.Field == "stale_after");
    }

    [Fact]
    public void Verified_missing_by_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nverified:\n  - at: '2026-07-27'\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("verified entry is missing `by`") && d.Code == DiagnosticCode.VerifiedMissingBy && d.Field == "verified.by");
    }

    [Fact]
    public void A_conformant_instant_stale_after_produces_no_temporal_warning()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: '2099-01-01T00:00:00Z'\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.StaleAfterInvalid);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void A_legacy_date_only_stale_after_warns_but_still_parses()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: '2099-01-01'\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.StaleAfterInvalid);
        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "stale_after");
    }

    [Fact]
    public void A_zoneless_datetime_stale_after_warns_as_legacy()
    {
        // §5 wants an explicit offset; a bare datetime is read as UTC and flagged.
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: '2099-01-01T12:00:00'\n");

        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "stale_after");
    }

    [Fact]
    public void A_legacy_date_only_generated_at_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ngenerated:\n  by: tool:okfgen\n  at: '2026-01-01'\n");

        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "generated.at");
    }

    [Fact]
    public void A_legacy_date_only_verified_at_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nverified:\n  - { by: human:ada, at: '2026-01-01' }\n");

        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "verified.at");
    }

    [Fact]
    public void A_conformant_generated_at_produces_no_temporal_warning()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ngenerated:\n  by: tool:okfgen\n  at: '2026-01-01T00:00:00Z'\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.GeneratedInvalidDate);
    }

    [Fact]
    public void A_truly_malformed_stale_after_is_still_invalid_not_legacy()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: 'not-a-date'\n");

        Assert.Contains(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.StaleAfterInvalid);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Theory]
    [InlineData("2026-06-30T14:00:00Z", true)]
    [InlineData("2026-06-30T14:00:00+02:00", true)]
    [InlineData("2026-06-30", false)]
    [InlineData("2026-06-30T14:00:00", false)]
    [InlineData("not-a-date", false)]
    public void IsConformantInstant_recognises_only_the_section_5_form(string raw, bool expected)
        => Assert.Equal(expected, BundleValidator.IsConformantInstant(raw));

    // §5.1 makes `last_modified` a timestamp-valued key and `usage_window` a
    // "{ from, to } datetime range", so §5's rule covers all three. They were
    // checked against `YYYY-MM-DD`, which rejected the conformant form outright
    // — a false positive on correct data, the mirror of the stale_after bug.

    [Fact]
    public void A_conformant_instant_last_modified_produces_no_warning()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources:\n  - resource: https://x\n    last_modified: '2026-06-30T14:00:00Z'\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.SourceInvalidLastModified);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void A_legacy_date_only_last_modified_warns_as_legacy_not_invalid()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources:\n  - resource: https://x\n    last_modified: '2026-06-30'\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.SourceInvalidLastModified);
        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "sources.last_modified");
    }

    [Fact]
    public void A_malformed_last_modified_is_still_invalid_not_legacy()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources:\n  - resource: https://x\n    last_modified: not-a-date\n");

        Assert.Contains(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.SourceInvalidLastModified);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void A_conformant_instant_usage_window_produces_no_warning()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nusage_window: {from: '2026-06-01T00:00:00Z', to: '2026-06-30T00:00:00Z'}\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.UsageWindowInvalidFrom);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.UsageWindowInvalidTo);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void A_legacy_date_only_usage_window_warns_once_per_bound()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nusage_window: {from: '2026-06-01', to: '2026-06-30'}\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.UsageWindowInvalidFrom);
        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "usage_window.from");
        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "usage_window.to");
    }

    [Fact]
    public void A_malformed_usage_window_bound_is_still_invalid_not_legacy()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nusage_window: {from: 'not-a-date', to: '2026-06-30T00:00:00Z'}\n");

        Assert.Contains(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.UsageWindowInvalidFrom);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.UsageWindowInvalidTo);
        Assert.DoesNotContain(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "usage_window.from");
    }

    // §5.1's per-entry usage_window override (a sources[] entry carrying its
    // own usage_window) routes its bounds through the same CheckTemporal
    // classifier as the shared, top-level usage_window -- reusing
    // UsageWindowInvalidFrom/…To, with Field distinguishing
    // "sources.usage_window.from"/".to" from the shared "usage_window.from"/
    // ".to". No new DiagnosticCode member.

    [Fact]
    public void A_conformant_instant_per_entry_usage_window_produces_no_warning()
    {
        // A false-positive guard, not proof the feature works: this test
        // asserts only absences, so it passes against pre-change code too --
        // deleting the whole per-entry validation block fails the other five
        // tests in this group and leaves this one green. Its job is to catch a
        // future over-eager warning on conformant input; do not count it as
        // coverage that the override is validated at all.
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\n" +
            "sources:\n  - resource: https://x\n    usage_window: {from: '2026-01-01T00:00:00Z'}\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.UsageWindowInvalidFrom);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.NonIso8601Timestamp);
    }

    [Fact]
    public void A_legacy_date_only_per_entry_usage_window_from_warns_as_legacy()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\n" +
            "sources:\n  - resource: https://x\n    usage_window: {from: '2026-01-01'}\n");

        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.UsageWindowInvalidFrom);
        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp && d.Field == "sources.usage_window.from");
    }

    [Fact]
    public void A_non_iso8601_spelling_per_entry_usage_window_from_warns_not_legacy_not_invalid()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\n" +
            "sources:\n  - resource: https://x\n    usage_window: {from: '2026-1-1T00:00:00Z'}\n");

        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.NonIso8601Timestamp && d.Field == "sources.usage_window.from");
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.UsageWindowInvalidFrom);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void An_unreadable_per_entry_usage_window_from_warns_invalid()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\n" +
            "sources:\n  - resource: https://x\n    usage_window: {from: 'not-a-date'}\n");

        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.UsageWindowInvalidFrom && d.Field == "sources.usage_window.from");
        // The message label is the third hand-copied literal at the call site
        // and the only one no other assertion pins. Asserted in FULL, not by
        // Contains: the shared position's message
        // ("usage_window from could not be read…") is a suffix of this one, so
        // a substring check would pass with either label.
        Assert.Contains(r.Of(Severity.Warning),
            d => d.Message == "source usage_window from could not be read as a timestamp: \"not-a-date\"");
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void An_unreadable_per_entry_usage_window_to_warns_invalid()
    {
        // Pins the *identity* of the `to` call site, distinct from the
        // `from` one above: the two blocks in BundleValidator.Validate are
        // hand-copied literals (code, field, label) sitting right next to
        // each other, so nothing else catches a copy-paste swap between
        // them (e.g. UsageWindowInvalidFrom / "sources.usage_window.from"
        // accidentally reused for the `to` bound). Without this test that
        // exact mutation passes the whole suite.
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\n" +
            "sources:\n  - resource: https://x\n    usage_window: {to: 'not-a-date'}\n");

        Assert.Contains(r.Of(Severity.Warning),
            d => d.Code == DiagnosticCode.UsageWindowInvalidTo && d.Field == "sources.usage_window.to");
        // Pins the label too, in FULL (see the `from` case above for why a
        // Contains would not discriminate): with Code and Field asserted but
        // not the label, swapping this call site's "source usage_window to"
        // for the `from` wording leaves the whole suite green, and a bad `to`
        // bound then reports a message naming `from`.
        Assert.Contains(r.Of(Severity.Warning),
            d => d.Message == "source usage_window to could not be read as a timestamp: \"not-a-date\"");
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void Bad_bounds_in_both_the_shared_and_per_entry_usage_window_yield_two_diagnostics_distinguished_by_field()
    {
        // Proves the reuse of UsageWindowInvalidFrom is safe: a bad shared
        // usage_window.from and a bad per-entry sources.usage_window.from in
        // the same document must not collapse into one finding, nor be
        // mistaken for each other -- Field is what tells them apart.
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\n" +
            "usage_window: {from: 'not-a-date'}\n" +
            "sources:\n  - resource: https://x\n    usage_window: {from: 'also-not-a-date'}\n");

        var invalidFromDiagnostics = r.Of(Severity.Warning).Where(d => d.Code == DiagnosticCode.UsageWindowInvalidFrom).ToList();
        Assert.Equal(2, invalidFromDiagnostics.Count);
        Assert.Contains(invalidFromDiagnostics, d => d.Field == "usage_window.from");
        Assert.Contains(invalidFromDiagnostics, d => d.Field == "sources.usage_window.from");
    }

    [Theory]
    [InlineData("2026-01-01T25:00:00Z")]  // hour out of range
    [InlineData("2026-01-01T00:61:00Z")]  // minute out of range
    public void A_generated_at_with_a_good_date_but_a_bad_time_is_invalid_not_legacy(string at)
    {
        // The date part alone used to decide this, so a broken time slipped past
        // GeneratedInvalidDate and was mislabelled "a legacy date-only value" —
        // which it is not. All six §5 keys must agree on malformed vs legacy.
        var r = ValidateConcept($"type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ngenerated: {{by: 'human:bob', at: '{at}'}}\n");

        Assert.Contains(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.GeneratedInvalidDate && d.Field == "generated.at");
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void A_verified_at_with_a_good_date_but_a_bad_time_is_invalid_not_legacy()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nverified:\n  - by: 'human:bob'\n    at: '2026-01-01T25:00:00Z'\n");

        Assert.Contains(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.VerifiedInvalidDate && d.Field == "verified.at");
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    // A value that carries an explicit UTC offset and parses, but is not
    // spelled ISO 8601 (unpadded month/day, a lowercase designator, a
    // basic-format offset, ...), used to pass every one of these fields
    // silently -- neither malformed nor legacy. It now gets its own code,
    // distinct from both, across all six §5 keys.

    [Fact]
    public void A_non_iso8601_spelling_stale_after_warns_not_legacy_not_invalid()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: '2026-6-3T14:00:00Z'\n");

        Assert.Contains(r.Of(Severity.Warning),
            d => d.Message.Contains("stale_after \"2026-6-3T14:00:00Z\" is not an ISO-8601 spelling")
                 && d.Code == DiagnosticCode.NonIso8601Timestamp && d.Field == "stale_after");
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.StaleAfterInvalid);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void A_non_iso8601_spelling_generated_at_warns_not_legacy_not_invalid()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ngenerated:\n  by: tool:okfgen\n  at: '2026-06-30T14:00:00z'\n");

        Assert.Contains(r.Of(Severity.Warning),
            d => d.Message.Contains("generated.at \"2026-06-30T14:00:00z\" is not an ISO-8601 spelling")
                 && d.Code == DiagnosticCode.NonIso8601Timestamp && d.Field == "generated.at");
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.GeneratedInvalidDate);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void A_non_iso8601_spelling_last_modified_warns_not_legacy_not_invalid()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources:\n  - resource: https://x\n    last_modified: '2026-06-30T14:00:00+0200'\n");

        Assert.Contains(r.Of(Severity.Warning),
            d => d.Message.Contains("source last_modified \"2026-06-30T14:00:00+0200\" is not an ISO-8601 spelling")
                 && d.Code == DiagnosticCode.NonIso8601Timestamp && d.Field == "sources.last_modified");
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.SourceInvalidLastModified);
        Assert.DoesNotContain(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    [Fact]
    public void StaleAfterIsLegacyDate_is_false_for_a_non_iso8601_spelling_while_the_validator_still_warns()
    {
        // Public API: StaleAfterIsLegacyDate documents exactly "bare date, or
        // datetime with no offset" -- 2026-6-3T14:00:00Z is neither (it carries
        // an explicit Z offset, just spelled with an unpadded month/day), so
        // widening it to also cover NonIso8601 would repeat the mislabelling
        // fixed in 65b7d9b. The validator still warns, just under a different code.
        var lc = Lifecycle.From(null, "2026-6-3T14:00:00Z");
        Assert.False(lc.StaleAfterIsLegacyDate);
        Assert.False(lc.StaleAfterMalformed);

        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: '2026-6-3T14:00:00Z'\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Code == DiagnosticCode.NonIso8601Timestamp && d.Field == "stale_after");
    }

    [Fact]
    public void An_unreadable_value_is_not_told_it_is_not_iso8601()
    {
        // The Unreadable bucket is defined by what DateTimeOffset.TryParse can
        // read, not by what ISO 8601 permits: the wholly-basic form used here,
        // plus leap seconds, week dates and ordinal dates, are all genuine ISO
        // 8601 datetimes with an explicit UTC offset that the BCL parser refuses
        // (pinned in
        // OkfTimestampTests.Iso8601_forms_the_bcl_parser_cannot_read_are_Unreadable).
        // Reporting them as "not an ISO-8601 datetime" would be false, so the
        // message states only that the value could not be read. The code is
        // unchanged; only the wording is constrained.
        //
        // The input must be a form ISO 8601 genuinely permits, or this test
        // proves nothing: it previously used end-of-day 24:00, which ISO 8601
        // allows only inside a time interval and forbids for a single time point
        // (§4.2.3 NOTE 3) -- so the premise did not hold for the one value the
        // test was built on.
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: '20200630T140000Z'\n");

        var d = Assert.Single(r.Of(Severity.Warning), x => x.Code == DiagnosticCode.StaleAfterInvalid);
        Assert.Equal("stale_after could not be read as a timestamp: \"20200630T140000Z\"", d.Message);
        Assert.DoesNotContain("ISO", d.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_log_date_heading_stays_a_bare_date_not_an_instant()
    {
        // §9: "Date headings MUST use ISO 8601 `YYYY-MM-DD` form." The §5
        // instant rule does NOT reach into the changelog, and ChangeLog.IsIsoDate
        // is shared with it — this pins that the §5 fix left §9 alone.
        using var tmp = new TempDir();
        tmp.Write("c.md", "---\ntype: T\ntitle: X\ndescription: D\nresource: https://x\ntags: [a]\n---\nbody\n");
        tmp.Write("log.md", "# Changelog\n\n## 2026-05-15\n\n* **Initialization**: Created structure.\n");

        var r = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == DiagnosticCode.LogDateInvalid);
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == DiagnosticCode.LegacyDateOnlyTimestamp);
    }

    // ---------------------------------------------------------------------
    // §8: "Entries SHOULD include the description from the linked concept's
    // frontmatter." Until these, the validator never read an index body at all:
    // ValidateReserved returned early for any index.md without frontmatter, which
    // is every well-formed one. A hand-written index could omit every description
    // and nothing would say so. (IndexGenerator does include them, which is why
    // the conformance report marked S8-3 Implemented — for generation, not for
    // validation.)
    // ---------------------------------------------------------------------

    private const string DescribedConcept =
        "---\ntype: Metric\ntitle: Revenue\ndescription: Recognized revenue for a period.\nresource: https://x\ntags: [x]\n---\nbody\n";

    [Fact]
    public void Index_entry_omitting_the_linked_concepts_description_is_a_warning()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/revenue.md", DescribedConcept);
        tmp.Write("metrics/index.md", "# Metrics\n\n* [Revenue](revenue.md)\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        var diag = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryMissingDescription);
        Assert.Equal(Severity.Warning, diag.Severity);
        Assert.Contains("revenue.md", diag.Message, StringComparison.Ordinal);
        // A SHOULD: warned, never a conformance failure (§11).
        Assert.True(report.IsConformant);
    }

    /// <summary>
    /// Presence is what is checked, not a verbatim copy. §8's own illustration is
    /// "<c>- short description of item 1</c>", and upstream samples routinely
    /// shorten: acme_retail's metrics index reads "Recognized revenue per Acme's
    /// FY2026 policy." against a longer frontmatter description. Demanding an exact
    /// match would warn on the spec's own sample practice.
    /// </summary>
    [Fact]
    public void Index_entry_carrying_a_shortened_description_is_not_warned()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/revenue.md", DescribedConcept);
        tmp.Write("metrics/index.md", "# Metrics\n\n* [Revenue](revenue.md) - Revenue, recognized.\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryMissingDescription);
    }

    /// <summary>
    /// An inline code span is text the reader sees, so under a presence check it
    /// counts as a description. This pins a real subtlety running the validator over
    /// <c>bundles/attestation_containers_demo</c> surfaced: its entries read
    /// <c>- [greeting.md](greeting.md) — `runtime: python`</c>, and the first cut of
    /// this check measured presence on the code-blanked line, where that span is
    /// spaces — so it flagged an entry that plainly carries text. Link structure is
    /// still read from the code-blanked line, so a link written inside a code span is
    /// never mistaken for an entry.
    /// </summary>
    [Fact]
    public void Index_entry_whose_description_is_inline_code_is_not_warned()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/revenue.md", DescribedConcept);
        tmp.Write("metrics/index.md", "# Metrics\n\n* [Revenue](revenue.md) — `runtime: postgres`\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryMissingDescription);
    }

    /// <summary>A link written inside an inline code span is source text, not an index entry.</summary>
    [Fact]
    public void A_link_inside_inline_code_is_not_an_index_entry()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/revenue.md", DescribedConcept);
        tmp.Write("metrics/index.md", "# Metrics\n\n* `[Revenue](revenue.md)`\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryMissingDescription);
    }

    /// <summary>A concept with no <c>description</c> has nothing to include, so its entry cannot omit one.</summary>
    [Fact]
    public void Index_entry_for_a_concept_without_a_description_is_not_warned()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/users.md", "---\ntype: Table\ntitle: Users\n---\nbody\n");
        tmp.Write("tables/index.md", "# Tables\n\n* [Users](users.md)\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryMissingDescription);
    }

    /// <summary>
    /// §8 speaks of "the linked <b>concept's</b> frontmatter". A link to a
    /// subdirectory index, a script, or the reserved <c>log.md</c> points at
    /// something with no frontmatter description, so the rule does not reach it.
    /// </summary>
    [Fact]
    public void Index_entries_linking_to_non_concepts_are_not_warned()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/revenue.md", DescribedConcept);
        tmp.Write("attesters/check.py", "def attest(**_):\n    return {}\n");
        tmp.Write("log.md", "# Log\n\n## 2026-09-14\n* **Update**: x.\n");
        tmp.Write("index.md", "# Contents\n\n* [metrics](metrics/index.md)\n* [check.py](attesters/check.py)\n* [log](log.md)\n");
        tmp.Write("metrics/index.md", "# Metrics\n\n* [Revenue](revenue.md) - Revenue.\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryMissingDescription);
    }

    /// <summary>The root index resolves relative to the bundle root, and an absolute (<c>/</c>) link does too (§6.1).</summary>
    [Fact]
    public void Root_index_and_absolute_links_are_resolved_like_concept_links()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/revenue.md", DescribedConcept);
        tmp.Write("index.md", "# Contents\n\n* [Revenue](metrics/revenue.md)\n* [Revenue again](/metrics/revenue.md)\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.Equal(2, report.Diagnostics.Count(d => d.Code == DiagnosticCode.IndexEntryMissingDescription));
    }

    // ---------------------------------------------------------------------
    // §5.1: `id` "SHOULD be present when the body cites the source", and §4.2
    // makes footnotes the citation mechanism ("Per-claim attribution to external
    // sources uses markdown footnotes keyed to `sources` entries"). A footnote
    // reference with no matching `sources[].id` is a citation that attributes to
    // nothing. The conformance report classed this N/A as producer guidance, but
    // the validator already checks producer-authored content (generated.by,
    // status, actors) — this is checkable the same way.
    // ---------------------------------------------------------------------

    [Fact]
    public void Footnote_citing_a_source_with_no_matching_id_is_a_warning()
    {
        using var tmp = new TempDir();
        tmp.Write("m.md",
            "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n" +
            "sources:\n  - { id: policy, resource: https://example.com/policy }\n---\n" +
            "Revenue is recognized on delivery.[^policy] Returns net out.[^returns]\n\n" +
            "[^policy]: Revenue policy\n[^returns]: Returns policy\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        var diag = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.CitationMissingSourceId);
        Assert.Equal(Severity.Warning, diag.Severity);
        Assert.Contains("returns", diag.Message, StringComparison.Ordinal);
        Assert.True(report.IsConformant);
    }

    [Fact]
    public void Footnote_matching_a_source_id_is_not_warned()
    {
        using var tmp = new TempDir();
        tmp.Write("m.md",
            "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n" +
            "sources:\n  - { id: policy, resource: https://example.com/policy }\n---\n" +
            "Revenue is recognized on delivery.[^policy]\n\n[^policy]: Revenue policy\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.CitationMissingSourceId);
    }

    /// <summary>
    /// The false positive this rule must not produce: <c>[^a-z]</c> is a negated
    /// character class, and it is ordinary inside a regex or a SQL pattern. A
    /// fenced block or an inline code span is source text, never a citation. The
    /// scan reuses LinkScanner's code-free view of the body rather than a second
    /// implementation of "skip code".
    /// </summary>
    [Fact]
    public void Footnote_syntax_inside_code_is_not_a_citation()
    {
        using var tmp = new TempDir();
        tmp.Write("m.md",
            "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\n" +
            "Match with `[^a-z]` inline.\n\n```sql\nSELECT * FROM t WHERE code SIMILAR TO '[^0-9]+'\n```\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.CitationMissingSourceId);
    }

    /// <summary>
    /// A definition line (<c>[^key]: …</c>) is not a citation, only the reference in
    /// running text is. A definition nothing refers to is dead markdown, not an
    /// unattributed claim.
    /// </summary>
    [Fact]
    public void A_footnote_definition_alone_is_not_a_citation()
    {
        using var tmp = new TempDir();
        tmp.Write("m.md",
            "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\n" +
            "No claim cites anything here.\n\n[^orphan]: An unused definition\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.CitationMissingSourceId);
    }

    /// <summary>
    /// §8 shows an entry as <c>* [Title](relative-url) - description</c>: something to
    /// follow. An entry naming a file in inline code gives a reader nothing to open —
    /// the drift a hand-written <c>attesters/index.md</c> actually had. §8 states the
    /// format by example rather than by rule, so this is a heuristic warning.
    /// </summary>
    [Fact]
    public void Index_list_item_that_is_not_a_link_is_a_warning()
    {
        using var tmp = new TempDir();
        tmp.Write("attesters/index.md", "# Attesters\n\n* `fare_cap.py` - checks the per-trip split\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        var diag = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryNotALink);
        Assert.Equal(Severity.Warning, diag.Severity);
        Assert.Contains("fare_cap.py", diag.Message, StringComparison.Ordinal);
        Assert.True(report.IsConformant);
    }

    /// <summary>
    /// An item that opens with inline code does not start with a link, even when a link
    /// follows — the code span is visible text, not whitespace.
    /// </summary>
    [Fact]
    public void Index_list_item_opening_with_inline_code_before_a_link_is_not_an_entry()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/revenue.md", DescribedConcept);
        tmp.Write("metrics/index.md", "# Metrics\n\n* `rev` [Revenue](revenue.md)\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryNotALink);
        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryMissingDescription);
    }

    /// <summary>
    /// A prose line that opens with inline code and a dash is not a list item: the code
    /// span is text, not the indentation it becomes once blanked.
    /// </summary>
    [Fact]
    public void A_line_opening_with_inline_code_and_a_dash_is_not_a_list_item()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "# Bundle\n\n`okf validate` - run it before publishing.\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryNotALink);
    }

    [Fact]
    public void Index_list_items_that_start_with_a_link_are_not_warned()
    {
        using var tmp = new TempDir();
        tmp.Write("attesters/fare_cap.py", "def attest(**_):\n    return {}\n");
        tmp.Write("attesters/index.md", "# Attesters\n\n* [fare_cap.py](fare_cap.py) - checks the per-trip split\n- [Sub](sub/)\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryNotALink);
    }

    /// <summary>
    /// <c>* * *</c> and <c>- - -</c> open like a list item but are thematic breaks, and
    /// prose paragraphs and list items inside a fence are not entries at all.
    /// </summary>
    [Fact]
    public void Thematic_breaks_prose_and_fenced_lists_in_an_index_are_not_warned()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "# Bundle\n\nSome prose about this bundle.\n\n* * *\n\n- - -\n\n```\n* not an entry\n```\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryNotALink);
    }

    /// <summary>
    /// The rule reads index files only. A concept body is free to use plain bullet
    /// lists (§4.2).
    /// </summary>
    [Fact]
    public void Plain_bullets_in_a_concept_body_are_not_warned()
    {
        using var tmp = new TempDir();
        tmp.Write("m.md", "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\n* plain\n* bullets\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryNotALink);
    }

    /// <summary>
    /// §4.2 gives <c># Examples</c> and <c># Schema</c> a conventional meaning, because
    /// structure aids agent retrieval. A heading that plainly holds examples under
    /// another name — <c># Worked example</c>, the drift a fare-cap policy actually
    /// had — reads fine and retrieves worse.
    /// </summary>
    [Theory]
    [InlineData("# Worked example", "Examples")]
    [InlineData("# Example", "Examples")]
    [InlineData("# examples", "Examples")]
    [InlineData("## Examples", "Examples")]
    [InlineData("# Table schema", "Schema")]
    [InlineData("# Schemas", "Schema")]
    public void A_variant_of_a_conventional_heading_is_a_warning(string heading, string conventional)
    {
        using var tmp = new TempDir();
        tmp.Write("m.md", "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\n" + heading + "\n\ntext\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        var diag = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.NonConventionalHeading);
        Assert.Equal(Severity.Warning, diag.Severity);
        Assert.Contains(heading.TrimStart('#', ' '), diag.Message, StringComparison.Ordinal);
        Assert.Contains("# " + conventional, diag.Message, StringComparison.Ordinal);
        Assert.True(report.IsConformant);
    }

    [Theory]
    [InlineData("# Examples")]
    [InlineData("# Schema")]
    [InlineData("# Usage")]
    [InlineData("# Counterexamples")]
    public void Conventional_and_unrelated_headings_are_not_warned(string heading)
    {
        using var tmp = new TempDir();
        tmp.Write("m.md", "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\n" + heading + "\n\ntext\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.NonConventionalHeading);
    }

    /// <summary>
    /// Once the document carries the conventional heading, a further heading mentioning
    /// examples is a subsection (<c>## Example with a cap</c>), not a missing one.
    /// </summary>
    [Fact]
    public void A_related_heading_beside_the_conventional_one_is_not_warned()
    {
        using var tmp = new TempDir();
        tmp.Write("m.md",
            "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\n" +
            "# Examples\n\n## Example with a cap\n\ntext\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.NonConventionalHeading);
    }

    [Fact]
    public void A_heading_inside_a_fence_is_not_a_heading()
    {
        using var tmp = new TempDir();
        tmp.Write("m.md",
            "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\n" +
            "```python\n# Example: call attest()\n```\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.NonConventionalHeading);
    }

    /// <summary>
    /// <c># Computation</c> is deliberately not part of this rule. A heading that merely
    /// mentions a computation is ordinary prose structure (acme_retail's
    /// <c># Why no attested computation</c>), and an Attested Computation whose heading
    /// is misspelled already gets <see cref="DiagnosticCode.ComputationMissingBody"/>.
    /// </summary>
    [Fact]
    public void Headings_mentioning_a_computation_are_not_warned()
    {
        using var tmp = new TempDir();
        tmp.Write("m.md",
            "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\n" +
            "# Why no attested computation\n\ntext\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.NonConventionalHeading);
    }

    private const string MetricFrontmatter = "---\ntype: Metric\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\n";

    /// <summary>
    /// Footnote syntax that markdown does not render as a footnote must not be read as a
    /// citation: a backslash-escaped bracket, an indented code block, and a
    /// double-backtick span (which the one-character code toggle used to leave visible).
    /// Raised by Copilot on #98.
    /// </summary>
    [Theory]
    [InlineData("Match \\[^a-z] literally.\n")]
    [InlineData("A pattern:\n\n    SIMILAR TO '[^0-9]+'\n")]
    [InlineData("Use `` [^x] `` as the class.\n")]
    [InlineData("Use `` a`[^x] `` as the class.\n")]
    [InlineData("An escaped \\` is literal, so `[^x]` is a span.\n")]
    public void Footnote_syntax_markdown_does_not_render_as_a_footnote_is_not_a_citation(string body)
    {
        using var tmp = new TempDir();
        tmp.Write("m.md", MetricFrontmatter + body);

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.CitationMissingSourceId);
    }

    /// <summary>
    /// The escape and code rules must not swallow real citations: a reference after an
    /// escaped backslash (<c>\\[^k]</c> is a literal backslash, then a footnote), and one
    /// in a list item's indented continuation paragraph.
    /// </summary>
    [Theory]
    [InlineData("A path C:\\\\[^k] cites.\n")]
    [InlineData("The span `C:\\` ends at its backslash, so this cites.[^k] Then `more`.\n")]
    [InlineData("* An item.\n\n    Its continuation cites this.[^k]\n")]
    public void Real_citations_next_to_those_forms_are_still_citations(string body)
    {
        using var tmp = new TempDir();
        tmp.Write("m.md", MetricFrontmatter + body);

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.CitationMissingSourceId);
    }

    /// <summary>
    /// A list item continues onto the following indented line, so a description wrapped
    /// there is still the entry's description. Raised by Copilot on #98.
    /// </summary>
    [Theory]
    [InlineData("# Metrics\n\n* [Revenue](revenue.md)\n  Recognized revenue.\n")]
    [InlineData("# Metrics\n\n* [Revenue](revenue.md) -\n  Recognized revenue.\n")]
    public void Index_entry_whose_description_wraps_onto_the_next_line_is_not_warned(string index)
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/revenue.md", DescribedConcept);
        tmp.Write("metrics/index.md", index);

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryMissingDescription);
    }

    /// <summary>
    /// The wrapped-description allowance ends where the item does: the next item, a blank
    /// line, or a heading is not a continuation.
    /// </summary>
    [Theory]
    [InlineData("# Metrics\n\n* [Revenue](revenue.md)\n* [Other](other.md) - other\n")]
    [InlineData("# Metrics\n\n* [Revenue](revenue.md)\n\nSome prose after the list.\n")]
    [InlineData("# Metrics\n\n* [Revenue](revenue.md)\n# Next section\n")]
    public void What_follows_an_entry_without_a_description_is_not_its_description(string index)
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/revenue.md", DescribedConcept);
        tmp.Write("metrics/index.md", index);

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryMissingDescription);
    }

    /// <summary>
    /// A tab after the list marker is as much a list item as a space (CommonMark §5.2), so
    /// the entry is read and its missing description reported. Raised by Copilot on #98.
    /// </summary>
    [Fact]
    public void Index_entry_with_a_tab_after_its_marker_is_an_entry()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/revenue.md", DescribedConcept);
        tmp.Write("metrics/index.md", "# Metrics\n\n*\t[Revenue](revenue.md)\n-\t`notes.txt`\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryMissingDescription);
        Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.IndexEntryNotALink);
    }

    /// <summary>
    /// Raised by Copilot on #98: a fence opened with four backticks is not closed by an
    /// inner three-backtick line, so footnote syntax after it is still code.
    /// </summary>
    [Fact]
    public void Footnote_syntax_inside_a_longer_fence_is_not_a_citation()
    {
        using var tmp = new TempDir();
        tmp.Write("m.md", MetricFrontmatter + "````markdown\n```\nA claim.[^k]\n```\n[^k]: a source\n````\n");

        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));

        Assert.DoesNotContain(report.Diagnostics, d => d.Code == DiagnosticCode.CitationMissingSourceId);
    }
}
