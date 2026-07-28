// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

namespace OKF4net.Internal;

/// <summary>
/// The single UTC timestamp format this library emits for OKF provenance
/// stamps — the §5.2 <c>generated.at</c> value written by
/// <see cref="OKF4net.BundleConceptWriter"/> and the agent-memory capture
/// stamps in <c>OkfContextProvider</c>: second precision, no fractional part,
/// a literal <c>Z</c> suffix (<c>yyyy-MM-ddTHH:mm:ssZ</c>), invariant culture.
/// Consolidated here so those call sites can never format divergently.
/// <c>OKF4net.Agents</c> sees this internal type via the core project's
/// <c>InternalsVisibleTo</c>.
/// </summary>
internal static class OkfTimestamp
{
    /// <summary>
    /// Formats <paramref name="utc"/> as <c>yyyy-MM-ddTHH:mm:ssZ</c> under the
    /// invariant culture. The caller is responsible for passing a UTC instant;
    /// the trailing <c>Z</c> is a literal designator, not a computed offset.
    /// </summary>
    internal static string FormatUtc(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";
}
