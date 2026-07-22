// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Internal;

/// <summary>
/// The two UTF-8 <see cref="UTF8Encoding"/> configurations this port needs
/// everywhere it touches bundle files on disk, mirroring Rust's <c>std::fs</c>
/// string I/O exactly: <see cref="Strict"/> for reads (matching
/// <c>fs::read_to_string</c>, which fails outright on invalid UTF-8 rather
/// than substituting U+FFFD) and <see cref="NoBom"/> for writes (matching
/// <c>fs::write</c>, which never emits a byte-order mark). Neither of .NET's
/// <see cref="Encoding.UTF8"/> defaults matches Rust here: the BCL's
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
    /// U+FFFD replacement, no BOM emission), matching the strictness of
    /// Rust's <c>fs::read_to_string</c> (which fails with an <c>io::Error</c>
    /// of kind <c>InvalidData</c> — message "stream did not contain valid
    /// UTF-8" — for any file that is not valid UTF-8).
    /// <see cref="System.IO.File.ReadAllText(string)"/> is deliberately not
    /// used at any call site: it silently substitutes U+FFFD for invalid
    /// bytes instead of failing.
    /// </summary>
    internal static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// UTF-8 encoder without a byte-order mark, for every file this port
    /// writes (matching Rust's <c>fs::write</c>, which never emits a BOM).
    /// </summary>
    internal static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);
}
