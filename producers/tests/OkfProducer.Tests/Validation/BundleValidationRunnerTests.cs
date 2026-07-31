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
    public void Validate_a_missing_directory_throws_BundleLoadException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "okfproducer-does-not-exist-" + Guid.NewGuid());

        Assert.Throws<BundleLoadException>(() => new BundleValidationRunner().Validate(missingPath));
    }
}
