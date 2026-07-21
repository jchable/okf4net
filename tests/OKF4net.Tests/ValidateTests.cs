// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Port of the Rust conformance-checking tests, exercised rule-by-rule
/// against <c>validate_bundle</c> (src/validate.rs:97-208). Each test targets
/// exactly one diagnostic-producing rule and asserts its exact severity and
/// message shape, per the doc comment at the top of validate.rs: only true
/// §9 violations (unparseable frontmatter, missing/empty `type`) are
/// <see cref="Severity.Error"/>; everything else is
/// <see cref="Severity.Warning"/> or <see cref="Severity.Info"/>.
/// </summary>
public class ValidateTests
{
    [Fact]
    public void Unparseable_frontmatter_is_an_error()
    {
        // validate.rs:100-108
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
        // validate.rs:112-121
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
        // Document.ValidateConformance requires a non-empty `type`
        // (document.rs:118-129); an explicit empty string is empty_value too.
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntype: \"\"\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.Contains(report.Of(Severity.Error), d => d.Message == "missing required frontmatter field `type`");
    }

    [Fact]
    public void Missing_recommended_fields_are_warnings()
    {
        // validate.rs:122-131: title/description/timestamp are soft guidance.
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntype: Note\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.True(report.IsConformant);
        var warnings = report.Of(Severity.Warning).ToList();
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `title`");
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `description`");
        Assert.Contains(warnings, d => d.Message == "missing recommended frontmatter field `timestamp`");
    }

    [Fact]
    public void Empty_recommended_field_values_are_also_warnings()
    {
        // fm.get(field).map(|v| v.is_empty_value()).unwrap_or(true) -- an
        // explicit but empty value (empty string) is treated the same as an
        // absent one.
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
            "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.DoesNotContain(report.Diagnostics, d => d.Message.Contains("recommended"));
    }

    [Fact]
    public void Non_iso_timestamp_is_a_warning()
    {
        // validate.rs:132-141
        using var tmp = new TempDir();
        tmp.Write(
            "bad.md",
            "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: not-a-date\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Warning));
        Assert.Equal("`timestamp` is not ISO-8601: \"not-a-date\"", diag.Message);
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
        // validate.rs:170-179: frontmatter is only permitted in the
        // bundle-root index.md.
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
        tmp.Write("sub/index.md", "---\ntitle: nope\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Warning));
        Assert.Equal("index.md should not contain frontmatter (§6)", diag.Message);
        Assert.True(report.IsConformant);
    }

    [Fact]
    public void Root_index_frontmatter_with_only_okf_version_is_clean()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\n---\nbody\n");
        tmp.Write("index.md", "---\nokf_version: \"0.1\"\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.DoesNotContain(report.Diagnostics, d => d.Message.Contains("index.md"));
    }

    [Fact]
    public void Root_index_frontmatter_with_extra_keys_is_a_warning()
    {
        // validate.rs:180-189
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
        tmp.Write("index.md", "---\nokf_version: \"0.1\"\ntitle: extra\n---\n\n# Listing\n");
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        var diag = Assert.Single(report.Of(Severity.Warning));
        Assert.Equal("root index.md frontmatter should declare only `okf_version` (§11)", diag.Message);
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
        // validate.rs:193-204
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Note\ntitle: T\ndescription: D\ntimestamp: 2026-05-28\n---\nbody\n");
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
        // validate.rs:147-155 / tests/bundle.rs:83-99
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
        tmp.Write("bad.md", "---\ntitle: No Type\n---\nbody\n"); // 1 error (missing type), 2 recommended-field warnings (description, timestamp)
        tmp.Write("log.md", "# Log\n\n## nope\n* x\n"); // 1 more warning
        var bundle = Bundle.Load(tmp.Path);
        var report = BundleValidator.Validate(bundle);

        Assert.Equal(1, report.ErrorCount);
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
        // is_iso8601_datetime (validate.rs:210-213): split on 'T' or ' ',
        // then delegate to is_iso_date on the date part only.
        => Assert.Equal(expected, BundleValidator.IsIso8601DateTime(s));

    [Fact]
    public void Appendix_a_bundle_is_fully_conformant_with_zero_diagnostics_of_any_kind()
    {
        // The Appendix A example is not just conformant (no errors) -- it is
        // clean: it should also have no warnings or info diagnostics, since
        // all recommended fields are present, timestamps are ISO, and there
        // are no broken links.
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

        Assert.Empty(report.Diagnostics);
        Assert.True(report.IsConformant);
    }
}
