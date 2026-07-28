// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Internal;

/// <summary>
/// The two UTF-8 <see cref="UTF8Encoding"/> configurations needed everywhere
/// this library touches bundle files on disk: <see cref="Strict"/> for reads
/// (fails outright on invalid UTF-8 rather than substituting U+FFFD) and
/// <see cref="NoBom"/> for writes (never emits a byte-order mark). Neither of
/// .NET's <see cref="Encoding.UTF8"/> defaults is suitable: the BCL's
/// singleton silently replaces invalid bytes with U+FFFD on decode and emits
/// a BOM on encode.
///
/// Previously five (<see cref="Strict"/>) and three (<see cref="NoBom"/>)
/// byte-identical private copies existed across <c>Bundle</c>,
/// <c>BundleValidator</c>, <c>IndexGenerator</c>, <c>OkfCli</c>, and
/// <c>OkfBundleTools</c> — consolidated here. <c>OKF4net.Cli</c> and
/// <c>OKF4net.Agents</c> can see this internal type via the core project's
/// <c>InternalsVisibleTo</c>.
/// </summary>
internal static class OkfEncodings
{
    /// <summary>
    /// UTF-8 decoder configured to throw on invalid byte sequences (no
    /// U+FFFD replacement, no BOM emission): any file that is not valid UTF-8
    /// fails with the message "stream did not contain valid UTF-8" rather than
    /// decoding to replacement characters.
    /// <see cref="System.IO.File.ReadAllText(string)"/> is deliberately not
    /// used at any call site: it silently substitutes U+FFFD for invalid
    /// bytes instead of failing.
    /// </summary>
    internal static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// UTF-8 encoder without a byte-order mark, for every file this library
    /// writes (BOM-less output).
    /// </summary>
    internal static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
