// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Validation;

/// <summary>Thin wrapper around <c>OKF4net.BundleValidator</c> for the <c>validate</c> command.</summary>
public interface IBundleValidationRunner
{
    /// <summary>Loads and validates the bundle at <paramref name="bundleRoot"/>.</summary>
    /// <exception cref="OKF4net.BundleLoadException"><paramref name="bundleRoot"/> does not exist or is not a directory.</exception>
    ValidationOutcome Validate(string bundleRoot);
}
