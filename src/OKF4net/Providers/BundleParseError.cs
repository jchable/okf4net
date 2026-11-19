// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// One problem met while loading a bundle (spec §2): the raw name as the provider
/// listed it, the validated key when the name passed validation (<see langword="null"/>
/// when it is precisely the name that was rejected), and the reason.
/// </summary>
public sealed record BundleParseError(string RawName, BundleKey? Key, string Error);
