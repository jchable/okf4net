// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>An opaque version token for one object; only the provider knows how it is built. Compared ordinally.</summary>
public readonly record struct BundleObjectVersion(string Token);

/// <summary>An opaque version token for a whole bundle, cheap to obtain; drives refresh decisions. Compared ordinally.</summary>
public readonly record struct BundleVersion(string Token);

/// <summary>One entry of a provider listing: the raw name (relative to the root, <c>/</c>-separated, not yet validated), its length and version when the backend gives them for free.</summary>
public sealed record BundleListing(string RawName, long? Length, BundleObjectVersion? Version);

/// <summary>An object read from a provider: its content stream (owned by this instance) and its version. The core reads the stream counting bytes (spec §6).</summary>
public sealed class BundleObject : IDisposable
{
    /// <summary>Creates an object over <paramref name="content"/>; disposing the object disposes the stream.</summary>
    public BundleObject(Stream content, BundleObjectVersion version, long? length)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Version = version;
        Length = length;
    }

    /// <summary>The content, positioned at its start.</summary>
    public Stream Content { get; }

    /// <summary>The object's version at the time of the read.</summary>
    public BundleObjectVersion Version { get; }

    /// <summary>The length in bytes when the provider knows it before reading.</summary>
    public long? Length { get; }

    /// <inheritdoc/>
    public void Dispose() => Content.Dispose();
}

/// <summary>How a write or delete is conditioned (spec §4).</summary>
public enum BundleWriteConditionKind
{
    /// <summary>Unconditional.</summary>
    None,

    /// <summary>Succeeds only if no object exists under the key (a create).</summary>
    MustNotExist,

    /// <summary>Succeeds only if the current version equals <see cref="BundleWriteCondition.Expected"/>.</summary>
    IfMatch,
}

/// <summary>The condition a provider checks atomically with a write or delete (spec §4).</summary>
public readonly record struct BundleWriteCondition(BundleWriteConditionKind Kind, BundleObjectVersion? Expected)
{
    /// <summary>Unconditional.</summary>
    public static BundleWriteCondition None => new(BundleWriteConditionKind.None, null);

    /// <summary>Create only.</summary>
    public static BundleWriteCondition MustNotExist => new(BundleWriteConditionKind.MustNotExist, null);

    /// <summary>Replace only if the current version is <paramref name="expected"/>.</summary>
    public static BundleWriteCondition IfMatch(BundleObjectVersion expected) => new(BundleWriteConditionKind.IfMatch, expected);
}

/// <summary>What a provider can do beyond reading (spec §3): reading is never optional.</summary>
public sealed record BundleProviderCapabilities(bool CanWrite);

/// <summary>The bounds of spec §6. The core enforces sizes, counts and depth on what it receives; a provider enforces the same on its own walks, and the network bounds on its requests.</summary>
public sealed record BundleLimits
{
    /// <summary>
    /// The spec's defaults: finite, so that a hostile or runaway provider cannot exhaust
    /// memory, and large enough that no real bundle reaches them (OKF §11 imposes no size
    /// constraint). Hosts reading untrusted storage should lower them.
    /// </summary>
    public static BundleLimits Default { get; } = new();

    /// <summary>Largest object the core reads, in bytes (256 MiB).</summary>
    public long MaxObjectBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Largest listing, every entry counted whatever its name (10 000 000).</summary>
    public int MaxEntries { get; init; } = 10_000_000;

    /// <summary>Deepest key, in segments (256).</summary>
    public int MaxDepth { get; init; } = 256;

    /// <summary>Longest key, in UTF-8 bytes (1 024; also <see cref="BundleKey.MaxUtf8Bytes"/>).</summary>
    public int MaxKeyBytes { get; init; } = BundleKey.MaxUtf8Bytes;

    /// <summary>Per-request timeout for network providers (30 s); unused by the filesystem.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Concurrent requests for network providers (16); unused by the filesystem.</summary>
    public int MaxConcurrentRequests { get; init; } = 16;
}
