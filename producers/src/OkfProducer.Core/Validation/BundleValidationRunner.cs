// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Validation;

/// <inheritdoc cref="IBundleValidationRunner"/>
public sealed class BundleValidationRunner : IBundleValidationRunner
{
    /// <inheritdoc/>
    public ValidationOutcome Validate(string bundleRoot)
    {
        var bundle = Bundle.Load(bundleRoot);
        var report = BundleValidator.Validate(bundle);
        var lines = report.Diagnostics.Select(d => d.ToString()).ToList();

        return new ValidationOutcome(report.ErrorCount, report.WarningCount, lines);
    }
}
