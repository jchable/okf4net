// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Conformance-checking tests, exercised rule-by-rule against
/// <c>BundleValidator.Validate</c>. Each test targets exactly one
/// diagnostic-producing rule and asserts its exact severity and message
/// shape: only true §11 violations (unparseable frontmatter, missing/empty
/// `type`) are <see cref="Severity.Error"/>; everything else is
/// <see cref="Severity.Warning"/> or <see cref="Severity.Info"/>.
/// </summary>
public class ValidateTests
{
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
        Assert.False(report.IsConformant);
        Assert.Equal(1, report.ErrorCount);
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
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `title`");
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `description`");
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `resource`");
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `tags`");
        Assert.DoesNotContain(warnings, d => d.Message.Contains("`timestamp`"));
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
    public void Nonroot_index_with_frontmatter_is_a_warning()
    {
        // frontmatter is only permitted in the bundle-root index.md.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
        tmp.Write("sub/index.md", "---\ntitle: nope\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Warning));
        Assert.Equal("index.md should not contain frontmatter (§8)", diag.Message);
        Assert.True(report.IsConformant);
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
    public void Root_index_frontmatter_with_extra_keys_is_a_warning()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
        tmp.Write("index.md", "---\nokf_version: \"0.2\"\ntitle: extra\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.Contains(report.Of(Severity.Warning), d => d.Message == "root index.md frontmatter should declare only `okf_version` (§12)");
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
    public void Invalid_log_date_heading_is_a_warning()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\nresource: https://x\ntags: [x]\n---\nbody\n");
        tmp.Write("log.md", "# Log\n\n## not-a-date\n* **Update**: did a thing.\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Warning));
        Assert.Equal("log date heading is not ISO-8601 `YYYY-MM-DD`: \"not-a-date\"", diag.Message);
        Assert.True(report.IsConformant);
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
    }

    [Fact]
    public void ErrorCount_and_WarningCount_reflect_only_their_own_severity()
    {
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntitle: No Type\n---\nbody\n"); // 1 error (missing type), 3 recommended-field warnings (description, resource, tags)
        tmp.Write("log.md", "# Log\n\n## nope\n* x\n"); // 1 more warning
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.Equal(1, report.ErrorCount);
        Assert.Equal(4, report.WarningCount);
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
        Assert.True(HasWarning(r, "generated is missing required `by`"));
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Malformed_actor_warns_strictly()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ngenerated: {by: bob, at: '2026-07-27'}\n");
        Assert.True(HasWarning(r, "generated.by is not a valid §7 actor"));
    }

    [Fact]
    public void Verified_list_entry_not_a_mapping_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nverified: [human:ada]\n");
        Assert.True(HasWarning(r, "verified entry is not a `{by, at}` mapping"));
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Verified_bare_scalar_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nverified: notamapping\n");
        Assert.True(HasWarning(r, "verified must be a"));
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Unknown_status_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstatus: archived\n");
        Assert.True(HasWarning(r, "unknown status"));
    }

    [Fact]
    public void Non_scalar_status_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstatus: [draft]\n");
        Assert.True(HasWarning(r, "status is not a scalar"));
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Stale_concept_warns_using_injected_clock()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nstale_after: '2026-01-01'\n",
            new FixedClock(new DateOnly(2026, 7, 27)));
        Assert.True(HasWarning(r, "concept is stale (stale_after 2026-01-01)"));
    }

    [Fact]
    public void Source_without_resource_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources:\n  - title: no resource\n");
        Assert.True(HasWarning(r, "source entry is missing required `resource`"));
    }

    [Fact]
    public void Sources_list_entry_not_a_mapping_warns()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources: [just-a-string]\n");
        Assert.True(HasWarning(r, "source entry is not a mapping"));
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Source_last_modified_bad_date_warns()
    {
        var r = ValidateConcept(
            "type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\nsources:\n  - resource: https://x\n    last_modified: not-a-date\n");
        Assert.True(HasWarning(r, "source last_modified is not `YYYY-MM-DD`"));
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
        Assert.DoesNotContain(r.Diagnostics, d => d.Message.Contains("source last_modified is not"));
        Assert.True(r.IsConformant);
    }

    [Fact]
    public void Legacy_timestamp_is_a_warning()
    {
        var r = ValidateConcept("type: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\ntimestamp: '2026-05-28'\n");
        Assert.Contains(r.Of(Severity.Warning), d => d.Message.Contains("timestamp", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Of(Severity.Info), d => d.Message.Contains("timestamp", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_okf_version_other_than_current_warns()
    {
        var dir = Directory.CreateTempSubdirectory("okfv02root").FullName;
        File.WriteAllText(Path.Combine(dir, "index.md"), "---\nokf_version: \"0.9\"\n---\n\n# Index\n");
        File.WriteAllText(Path.Combine(dir, "c.md"), "---\ntype: T\ntitle: X\ndescription: D\nresource: R\ntags: [a]\n---\nbody\n");
        var r = BundleValidator.Validate(Bundle.Load(dir));
        Assert.True(HasWarning(r, "declared okf_version"));
    }

    [Fact]
    public void Attested_computation_missing_runtime_warns_but_stays_conformant()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n# Computation absent + pas de computation:\n---\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.True(report.IsConformant);                                   // Error reste §11-only
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("runtime"));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("no computation"));
    }

    [Fact]
    public void Both_inline_and_path_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: ./x.sql\n---\n# Computation\n\n```\nSELECT 1\n```\n");
        tmp.Write("c/x.sql", "SELECT 1\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("both inline and"));
    }

    [Fact]
    public void Attester_present_with_no_resource_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nattester: {}\n---\n# Computation\n\n```\nSELECT 1\n```\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("attester.resource"));
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
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("not found"));
    }

    [Fact]
    public void Parameter_without_name_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nparameters:\n  - type: integer\n    required: true\n---\n# Computation\n\n```\nSELECT 1\n```\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("missing") && d.Message.Contains("name"));
        Assert.True(report.IsConformant);
    }

    [Fact]
    public void Executor_receipt_not_a_list_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { receipt: nope }\n---\n# Computation\n\n```\nSELECT 1\n```\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("executor.receipt is not a list"));
        Assert.True(report.IsConformant);
    }

    [Fact]
    public void Unsafe_frontmatter_path_warns()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: ../../../outside.sql\n---\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("escapes the bundle"));
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
}
