// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// A <c>.md</c> key the load accepted under its policy, which the portable profile
/// (<see cref="BundleKeyPolicy.Strict"/>) would have refused (spec §2, layer 3): the object
/// loads, but its name would not survive every filesystem. Recorded in
/// <see cref="Bundle.PortabilityIssues"/> and reported by <see cref="BundleValidator.Validate"/>
/// as a <see cref="DiagnosticCode.KeyNotPortable"/> warning; never under
/// <see cref="BundleKeyPolicy.Strict"/>, which refuses such a name instead.
/// </summary>
/// <param name="Key">The accepted key.</param>
/// <param name="Reason">Why the portable profile refuses it, in the words it would have used.</param>
public sealed record BundlePortabilityIssue(BundleKey Key, string Reason);
