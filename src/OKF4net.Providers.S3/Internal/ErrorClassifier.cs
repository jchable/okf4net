// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>The closed table of spec §4.6 that translates a failure the provider observed.</summary>
internal static class ErrorClassifier
{
    /// <summary>
    /// Translates <paramref name="e"/> into the exception the operation ends with. Stub: completed in Task 8 (spec §4.6
    /// table); until then every failure is <see cref="S3FailureKind.Transport"/>.
    /// </summary>
    public static Exception Classify(S3Operation operation, BundleKey? key, Exception e) =>
        new S3RequestException(operation, S3FailureKind.Transport, null, key, e);
}
