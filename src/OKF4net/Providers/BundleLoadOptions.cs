// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>What a host controls when loading a bundle through a provider (spec §2, §6).</summary>
public sealed record BundleLoadOptions
{
    /// <summary>The spec's defaults: default bounds, <see cref="BundleKeyPolicy.Native"/>, no extra validator.</summary>
    public static BundleLoadOptions Default { get; } = new();

    /// <summary>The bounds enforced by the core and handed to the provider for its own walks.</summary>
    public BundleLimits Limits { get; init; } = BundleLimits.Default;

    /// <summary>
    /// The portability policy applied on top of the provider's native constraints.
    /// <see cref="BundleKeyPolicy.Native"/> by default: the load refuses only what this
    /// provider's storage itself cannot hold or would alias, because OKF §11 makes a bundle
    /// conformant whatever its names. What the portable profile would refuse beyond that is
    /// reported in <see cref="Bundle.PortabilityIssues"/> and, by the validator, as
    /// <see cref="DiagnosticCode.KeyNotPortable"/> warnings. <see cref="BundleKeyPolicy.Strict"/>
    /// is the opt-in portable profile, for hosts that must produce or consume bundles that
    /// survive any filesystem.
    /// </summary>
    public BundleKeyPolicy KeyPolicy { get; init; } = BundleKeyPolicy.Native;

    /// <summary>Host validators that may only restrict further; each returns a reason or <see langword="null"/>.</summary>
    public IReadOnlyList<Func<BundleKey, string?>>? ExtraKeyValidators { get; init; }
}
