// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OkfProducer.Core.Validation;

/// <inheritdoc cref="IBundleValidationRunner"/>
public sealed class BundleValidationRunner : IBundleValidationRunner
{
    /// <inheritdoc/>
    public ValidationOutcome Validate(string bundleRoot) => Run(bundleRoot, clock: null);

    /// <inheritdoc/>
    public ValidationOutcome Validate(string bundleRoot, IOkfClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return Run(bundleRoot, clock);
    }

    /// <summary>
    /// The one implementation both overloads share. <paramref name="clock"/> is passed straight through
    /// to <see cref="BundleValidator.Validate"/>, whose own <see langword="null"/> default is
    /// <see cref="SystemClock"/> -- so the clock-less overload keeps exactly the behaviour it had, and
    /// this class never picks a clock of its own that could drift from the library's default.
    /// </summary>
    private static ValidationOutcome Run(string bundleRoot, IOkfClock? clock)
    {
        var bundle = Bundle.Load(bundleRoot);
        var report = BundleValidator.Validate(bundle, clock);
        var lines = report.Diagnostics.Select(d => d.ToString()).ToList();

        return new ValidationOutcome(report.ErrorCount, report.WarningCount, lines);
    }
}
