// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>
/// Constants describing the OKF specification this library implements.
/// Exposed as a small static class so library consumers (and
/// <c>OKF4net.Cli</c>) have a single public source of truth for the spec
/// version (§11).
/// </summary>
public static class OkfSpec
{
    /// <summary>The OKF spec version this library implements (§11).</summary>
    public const string Version = "0.2";
}
