// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;

namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// The closed table of spec §4.6, as (status, code) PAIRS: a code counts only with the status S3 sends it with.
/// A pair the table does not list falls into its status range (429 Throttled, 5xx ServerError, 4xx ClientError); any
/// other status (1xx, 2xx, a 3xx the SDK did not follow, ≥ 600) is InvalidResponse — the terminal rule. Anything not
/// from the service is Transport, except the response-integrity failure gate G8 recorded. The S3 error code decides
/// the row and never leaves this class: no message and no property carries it (it is server text).
/// </summary>
internal static class ErrorClassifier
{
    private static readonly Dictionary<(int Status, string Code), S3FailureKind> Pairs = new()
    {
        [(403, "InvalidAccessKeyId")] = S3FailureKind.Credentials,
        [(403, "SignatureDoesNotMatch")] = S3FailureKind.Credentials,
        [(403, "ExpiredToken")] = S3FailureKind.Credentials,
        [(403, "InvalidToken")] = S3FailureKind.Credentials,
        [(403, "TokenRefreshRequired")] = S3FailureKind.Credentials,
        [(400, "ExpiredToken")] = S3FailureKind.Credentials,
        [(400, "InvalidToken")] = S3FailureKind.Credentials,
        [(400, "TokenRefreshRequired")] = S3FailureKind.Credentials,
        [(404, "NoSuchBucket")] = S3FailureKind.BucketUnavailable,
        [(301, "PermanentRedirect")] = S3FailureKind.BucketUnavailable,
        [(301, "MovedPermanently")] = S3FailureKind.BucketUnavailable, // a 301 without a body: the SDK names it after the status (measured, 4.1.0)
        [(400, "AuthorizationHeaderMalformed")] = S3FailureKind.BucketUnavailable,
        [(400, "IllegalLocationConstraintException")] = S3FailureKind.BucketUnavailable,
        [(503, "SlowDown")] = S3FailureKind.Throttled,
    };

    /// <summary>The message HashStream.CalculateHash raises (AWSSDK.Core 4.1.0) when a response hash does not match; gate G8 confirms it is the path a wrong checksum takes (an <see cref="AmazonClientException"/>, raised during the read).</summary>
    internal const string HashMismatchMessage = "Expected hash not equal to calculated hash";

    /// <summary>
    /// Translates <paramref name="e"/>, a failure observed during one operation, into the exception the operation ends
    /// with. The contract's own types for a read of one key (<see cref="BundleObjectNotFoundException"/> for 404
    /// NoSuchKey, <see cref="BundleAccessRefusedException"/> for 403 AccessDenied), else an
    /// <see cref="S3RequestException"/> whose inner exception is <paramref name="e"/>, for the host's log (spec §4.6).
    /// </summary>
    public static Exception Classify(S3Operation operation, BundleKey? key, Exception e)
    {
        var service = e as AmazonServiceException ?? e.InnerException as AmazonServiceException;
        if (service is null)
        {
            return e switch
            {
                GuardException g => new S3RequestException(operation, g.Kind, null, key, g),
                AmazonClientException c when IsIntegrity(c) => new S3RequestException(operation, S3FailureKind.Integrity, null, key, c),
                _ => new S3RequestException(operation, S3FailureKind.Transport, null, key, e),
            };
        }

        var status = (int)service.StatusCode;
        var code = service.ErrorCode ?? "";
        if (operation == S3Operation.Read && key is not null)
        {
            if (status == 404 && code == "NoSuchKey")
            {
                return new BundleObjectNotFoundException(key);
            }

            if (status == 403 && code == "AccessDenied")
            {
                return new BundleAccessRefusedException(key, "access denied");
            }
        }

        var kind = Pairs.TryGetValue((status, code), out var paired) ? paired
            : status == 429 ? S3FailureKind.Throttled
            : status is >= 500 and <= 599 ? S3FailureKind.ServerError
            : status is >= 400 and <= 499 ? S3FailureKind.ClientError
            : S3FailureKind.InvalidResponse;
        return new S3RequestException(operation, kind, status, key, service);
    }

    private static bool IsIntegrity(AmazonClientException c) =>
        c.Message.StartsWith(HashMismatchMessage, StringComparison.Ordinal);
}
