// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Validation;

namespace OkfProducer.Tests.Validation;

public class BundleValidationRunnerTests
{
    private static string CreateTempBundle()
    {
        var path = Path.Combine(Path.GetTempPath(), "okfproducer-validate-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Validate_a_conformant_bundle_reports_zero_errors()
    {
        var bundleRoot = CreateTempBundle();
        try
        {
            File.WriteAllText(Path.Combine(bundleRoot, "overview.md"),
                "---\ntype: Repository\ntitle: t\ndescription: d\n---\n\n# t\n");

            var outcome = new BundleValidationRunner().Validate(bundleRoot);

            Assert.Equal(0, outcome.ErrorCount);
            Assert.True(outcome.IsConformant);
        }
        finally
        {
            Directory.Delete(bundleRoot, recursive: true);
        }
    }

    [Fact]
    public void Validate_a_bundle_missing_type_reports_an_error()
    {
        var bundleRoot = CreateTempBundle();
        try
        {
            File.WriteAllText(Path.Combine(bundleRoot, "broken.md"), "---\ntitle: t\n---\n\nbody\n");

            var outcome = new BundleValidationRunner().Validate(bundleRoot);

            Assert.True(outcome.ErrorCount > 0);
            Assert.False(outcome.IsConformant);
            Assert.NotEmpty(outcome.DiagnosticLines);
        }
        finally
        {
            Directory.Delete(bundleRoot, recursive: true);
        }
    }

    [Fact]
    public void Validate_reads_today_from_the_clock_it_is_given()
    {
        // The overload exists so a validation result cannot depend on the day it ran. §5.5's staleness
        // check is the one thing in the validator that reads a clock, so it is the only place the seam
        // is observable -- assert on it, or the overload is untested plumbing.
        var bundleRoot = CreateTempBundle();
        try
        {
            File.WriteAllText(Path.Combine(bundleRoot, "overview.md"),
                "---\ntype: Repository\ntitle: t\ndescription: d\nstale_after: 2026-06-30\n---\n\n# t\n");

            var before = new BundleValidationRunner().Validate(bundleRoot, new FixedClock(new DateOnly(2026, 1, 1)));
            var after = new BundleValidationRunner().Validate(bundleRoot, new FixedClock(new DateOnly(2027, 1, 1)));

            Assert.DoesNotContain(before.DiagnosticLines, line => line.Contains("stale", StringComparison.Ordinal));
            Assert.Contains(after.DiagnosticLines, line => line.Contains("stale", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(bundleRoot, recursive: true);
        }
    }

    [Fact]
    public void Validate_without_a_clock_still_works_and_a_null_clock_is_refused()
    {
        // The clock-less overload is the one the CLI calls and it must keep working untouched; a null
        // clock is a caller bug, not a request for the system clock -- that request is the other overload.
        var bundleRoot = CreateTempBundle();
        try
        {
            File.WriteAllText(Path.Combine(bundleRoot, "overview.md"),
                "---\ntype: Repository\ntitle: t\ndescription: d\n---\n\n# t\n");

            Assert.True(new BundleValidationRunner().Validate(bundleRoot).IsConformant);
            Assert.Throws<ArgumentNullException>(() => new BundleValidationRunner().Validate(bundleRoot, clock: null!));
        }
        finally
        {
            Directory.Delete(bundleRoot, recursive: true);
        }
    }

    [Fact]
    public void Validate_a_missing_directory_throws_BundleLoadException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "okfproducer-does-not-exist-" + Guid.NewGuid());

        Assert.Throws<BundleLoadException>(() => new BundleValidationRunner().Validate(missingPath));
    }
}
