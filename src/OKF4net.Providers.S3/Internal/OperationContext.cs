// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;

namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// What the handlers expect of the requests ONE SDK call sends (spec §4.4): one <c>GetObjectAsync</c> or one
/// <c>ListObjectsV2Async</c>, with the SDK's own retries and redirects. Flows through the SDK's pipeline by
/// <see cref="AsyncLocal{T}"/> (gate G9). The send cap (<see cref="PinningHandler.MaxSendsPerOperation"/>) counts
/// against the instance, so it is per SDK call: a multi-page listing creates a NEW context for every page (plan
/// Task 7's <c>FetchAsync</c> does), never one context reused across pages, which would stop it at the third page.
/// The <see cref="Operation"/> also decides how the bounding handler bounds a successful body.
/// </summary>
internal sealed class OperationContext(S3Operation operation)
{
    public static readonly AsyncLocal<OperationContext?> Current = new();

    public S3Operation Operation { get; } = operation;

    /// <summary>For a read: the decoded S3 key the request path must name.</summary>
    public string? ExpectedObjectS3Key { get; init; }

    /// <summary>For a listing or an existence check: the decoded <c>prefix</c> query parameter.</summary>
    public string? ExpectedListPrefix { get; init; }

    /// <summary>The credentials snapshot every signature of this operation uses (spec §4.4).</summary>
    public ImmutableCredentials? Credentials { get; set; }

    /// <summary>Where the bounding handler leaves a listing page's raw body.</summary>
    public ListPageCapture? Capture { get; init; }

    /// <summary>Requests this SDK call has sent, counted by the guard (at most <see cref="PinningHandler.MaxSendsPerOperation"/>).</summary>
    public int Sends;
}

/// <summary>The raw body of the last listing page this operation received.</summary>
internal sealed class ListPageCapture
{
    public byte[]? Body { get; set; }
}
