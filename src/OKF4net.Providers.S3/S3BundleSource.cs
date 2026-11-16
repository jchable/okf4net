// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon;

namespace OKF4net.Providers.S3;

/// <summary>
/// Where a bundle lives in S3 or an S3-compatible store, and the provider's own bounds (spec §4.2). Trusted
/// operator configuration (#139, §1); no credential goes here — the host passes an <c>AWSCredentials</c> to the
/// provider's constructor. Exactly one of <see cref="Region"/> (Amazon S3) and <see cref="ServiceUrl"/> (a
/// compatible store's origin) is set.
/// </summary>
public sealed record S3BundleSource
{
    /// <summary>A general-purpose bucket name (3–63 characters: lowercase letters, digits, '-', '.'); access points, ARNs, aliases and directory buckets are refused.</summary>
    public required string Bucket { get; init; }

    /// <summary>The bundle's key prefix: empty (the whole bucket) or '/'-separated segments that are valid bundle key segments, in well-formed Unicode, with no leading or trailing '/'.</summary>
    public string Prefix { get; init; } = "";

    /// <summary>The name shown in <see cref="IBundleProvider.Location"/> instead of the bucket, which never reaches a model (spec D5). One valid key segment, without ':'. When absent, 8 hex digits of the provider id.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The Amazon S3 region; exclusive of <see cref="ServiceUrl"/>.</summary>
    public RegionEndpoint? Region { get; init; }

    /// <summary>A compatible store's origin (scheme, host, port; no user info, path, query or fragment); exclusive of <see cref="Region"/>.</summary>
    public Uri? ServiceUrl { get; init; }

    /// <summary>Path-style addressing (<c>host/bucket/key</c>) instead of virtual-hosted (<c>bucket.host/key</c>); most compatible stores need it.</summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>Allows an <c>http://</c> <see cref="ServiceUrl"/> (a local store, tests). HTTPS is required otherwise (spec D11).</summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>The deadline of one provider operation, from the call to the end of the body, retries and credential resolution included (spec §4.5).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Operations in flight at once, bodies included (spec D16).</summary>
    public int MaxConcurrentRequests { get; init; } = 16;

    /// <summary>The largest object read, in bytes (1 to <see cref="Array.MaxLength"/>); a read buffers the whole object (spec D8).</summary>
    public long MaxObjectBytes { get; init; } = BundleLimits.Default.MaxObjectBytes;

    /// <summary>Checksum behaviour (spec D12).</summary>
    public S3ChecksumMode Checksums { get; init; } = S3ChecksumMode.WhenSupported;
}
