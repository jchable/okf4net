// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>What a host controls when loading a bundle through a provider (spec §2, §6).</summary>
public sealed record BundleLoadOptions
{
    /// <summary>The spec's defaults: default bounds, <see cref="BundleKeyPolicy.Strict"/>, no extra validator.</summary>
    public static BundleLoadOptions Default { get; } = new();

    /// <summary>The bounds enforced by the core and handed to the provider for its own walks.</summary>
    public BundleLimits Limits { get; init; } = BundleLimits.Default;

    /// <summary>The portability policy applied on top of the provider's native constraints.</summary>
    public BundleKeyPolicy KeyPolicy { get; init; } = BundleKeyPolicy.Strict;

    /// <summary>Host validators that may only restrict further; each returns a reason or <see langword="null"/>.</summary>
    public IReadOnlyList<Func<BundleKey, string?>>? ExtraKeyValidators { get; init; }
}
