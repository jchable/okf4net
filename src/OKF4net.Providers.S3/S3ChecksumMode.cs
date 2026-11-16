// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3;

/// <summary>How the SDK computes and validates checksums (spec D12); never read from the environment.</summary>
public enum S3ChecksumMode
{
    /// <summary>The SDK default: checksums on every operation that supports them (accepted by RustFS, measured).</summary>
    WhenSupported,

    /// <summary>Only where the operation requires one: for a compatible store that refuses the default.</summary>
    WhenRequired,
}
