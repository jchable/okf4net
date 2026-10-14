// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>Base of the provider contract's exceptions (spec §1). Messages are library-authored and name keys, never host paths or credentials.</summary>
public abstract class BundleProviderException(string message) : OkfException(message)
{
}

/// <summary>A write or delete was attempted on a provider whose <see cref="BundleProviderCapabilities.CanWrite"/> is <see langword="false"/> (spec §3). Nothing was attempted. The message never carries the provider id (for the filesystem it is a host path, §7); it is on <see cref="ProviderId"/>.</summary>
public sealed class BundleReadOnlyException(string providerId)
    : BundleProviderException("the provider is read-only; the write was refused before being attempted")
{
    /// <summary>The refusing provider's <see cref="IBundleProvider.Id"/>, for the host's log.</summary>
    public string ProviderId { get; } = providerId;
}

/// <summary>A write or delete condition did not hold (spec §4).</summary>
public sealed class BundleWriteConflictException(BundleKey key)
    : BundleProviderException($"'{key}' changed since it was read; the write was refused")
{
    /// <summary>The key whose condition failed.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>A read with an expected version found a different one (pinning, spec §5.4).</summary>
public sealed class BundleVersionMismatchException(BundleKey key)
    : BundleProviderException($"'{key}' is no longer the version that was observed")
{
    /// <summary>The key whose version changed.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>No object under the key.</summary>
public sealed class BundleObjectNotFoundException(BundleKey key)
    : BundleProviderException($"no object under '{key}'")
{
    /// <summary>The missing key.</summary>
    public BundleKey Key { get; } = key;
}

/// <summary>
/// An object exceeds <see cref="BundleLimits.MaxObjectBytes"/> (spec §6). Derives from
/// <see cref="IOException"/> so every existing handler that treats an I/O failure as
/// "unreadable" classifies it correctly without change (spec, point to validate 6).
/// </summary>
public sealed class BundleObjectTooLargeException(BundleKey key, long observedBytes, long maxBytes)
    : IOException($"'{key}' is larger than the {maxBytes}-byte bound ({observedBytes} bytes read before stopping)")
{
    /// <summary>The offending key.</summary>
    public BundleKey Key { get; } = key;
}
