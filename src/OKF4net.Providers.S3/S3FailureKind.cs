// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3;

/// <summary>Why an S3 request failed (spec §4.6). Compare by value; members are appended, never inserted.</summary>
public enum S3FailureKind
{
    /// <summary>The operation's deadline passed.</summary>
    Timeout,

    /// <summary>The store asked to slow down (503 SlowDown, 429).</summary>
    Throttled,

    /// <summary>Another 5xx.</summary>
    ServerError,

    /// <summary>Another 4xx.</summary>
    ClientError,

    /// <summary>The connection or the SDK failed before or while reading a response.</summary>
    Transport,

    /// <summary>The request guard refused a request outside this source (spec D3).</summary>
    RefusedHost,

    /// <summary>A response the provider will not trust (transport bounds, listing invariants, an unrecognized status).</summary>
    InvalidResponse,

    /// <summary>The credentials could not be obtained, or the store rejected them.</summary>
    Credentials,

    /// <summary>The bucket is absent or served from another region.</summary>
    BucketUnavailable,

    /// <summary>A response checksum did not match.</summary>
    Integrity,

    /// <summary>The provider was disposed.</summary>
    Disposed,
}
