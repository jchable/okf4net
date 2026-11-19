// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

namespace OKF4net.Providers.S3;

/// <summary>
/// An S3 request failed (spec D7, §4.6). The message is library-authored from a closed vocabulary — operation,
/// kind, HTTP status, bundle key — and never carries a bucket, an endpoint, a request id, a URL, the S3 error code
/// or any other text from the server; the raw failure is the <see cref="Exception.InnerException"/>, for the host's
/// log only (#139, §3). Being a <see cref="BundleProviderException"/>, it fails a load rather than being recorded as
/// a local state of one object.
/// </summary>
public sealed class S3RequestException : BundleProviderException
{
    internal S3RequestException(S3Operation operation, S3FailureKind kind, int? statusCode, BundleKey? key)
        : base(Describe(operation, kind, statusCode, key))
    {
        Operation = operation;
        Kind = kind;
        StatusCode = statusCode;
    }

    internal S3RequestException(S3Operation operation, S3FailureKind kind, int? statusCode, BundleKey? key, Exception inner)
        : base(Describe(operation, kind, statusCode, key), inner)
    {
        Operation = operation;
        Kind = kind;
        StatusCode = statusCode;
    }

    /// <summary>The operation that failed.</summary>
    public S3Operation Operation { get; }

    /// <summary>Why it failed.</summary>
    public S3FailureKind Kind { get; }

    /// <summary>The HTTP status, when a response was received.</summary>
    public int? StatusCode { get; }

    private static string Describe(S3Operation operation, S3FailureKind kind, int? status, BundleKey? key)
    {
        var op = operation switch { S3Operation.List => "list", S3Operation.Read => "read", _ => "exists" };
        var text = $"S3 {op} failed: {kind}";
        if (status is { } s)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" (HTTP {s})");
        }

        return key is null ? text : text + $" for '{key}'";
    }
}
