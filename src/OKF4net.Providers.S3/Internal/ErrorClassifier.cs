// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;

namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// The closed table of spec §4.6, as (status, code) PAIRS: a code counts only with the status S3 sends it with.
/// A pair the table does not list falls into its status range (429 Throttled, 5xx ServerError, 4xx ClientError); any
/// other status (1xx, 2xx, a 3xx the SDK did not follow, ≥ 600) is InvalidResponse — the terminal rule. Anything not
/// from the service is Transport, except the response-integrity failure gate G8 recorded (Integrity) and a
/// FormatException the SDK raises reading a response (InvalidResponse, Annexe C5c). The S3 error code decides
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
                _ when IsIntegrity(e, 0) => new S3RequestException(operation, S3FailureKind.Integrity, null, key, e),
                _ when Carries<FormatException>(e, 0) => new S3RequestException(operation, S3FailureKind.InvalidResponse, null, key, e),
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

    /// <summary>How deep <see cref="IsIntegrity"/> looks into a chain of wrappers: far past any real nesting, a bound all the same.</summary>
    private const int MaxChainDepth = 16;

    /// <summary>
    /// Whether <paramref name="e"/> is, or carries in its inner-exception chain, the SDK's hash-mismatch failure: a stream
    /// wrapper (an IOException) or a task combinator (an AggregateException, every one of its inner exceptions) may carry
    /// it rather than throw it bare.
    /// </summary>
    private static bool IsIntegrity(Exception e, int depth)
    {
        if (depth > MaxChainDepth)
        {
            return false;
        }

        if (e is AmazonClientException c && c.Message.StartsWith(HashMismatchMessage, StringComparison.Ordinal))
        {
            return true;
        }

        return e is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(inner => IsIntegrity(inner, depth + 1))
            : e.InnerException is { } next && IsIntegrity(next, depth + 1);
    }

    /// <summary>
    /// Whether <paramref name="e"/> is, or carries in its inner-exception chain (same walk and bound as
    /// <see cref="IsIntegrity"/>), a <typeparamref name="T"/>. Used for <see cref="FormatException"/> (spec Annexe C5c,
    /// maintainer's decision 2026-11-19): measured on AWSSDK.S3 4.1.0, a single-part ETag that is not an MD5 hex digest
    /// makes the SDK's own MD5 check (AmazonS3ResponseHandler.ProcessResponseHandlers → AWSSDKUtils.HexStringToBytes →
    /// Convert.FromHexString) throw a bare FormatException out of GetObjectAsync — the store's response is malformed, not
    /// the transport: <see cref="S3FailureKind.InvalidResponse"/>. Nothing in the provider's own code throws one
    /// (ListPageParser turns its own into a refusal), so any FormatException here comes from reading a response.
    /// </summary>
    private static bool Carries<T>(Exception e, int depth)
        where T : Exception
    {
        if (depth > MaxChainDepth)
        {
            return false;
        }

        if (e is T)
        {
            return true;
        }

        return e is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(inner => Carries<T>(inner, depth + 1))
            : e.InnerException is { } next && Carries<T>(next, depth + 1);
    }
}
