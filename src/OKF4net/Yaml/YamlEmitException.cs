// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Yaml;

/// <summary>
/// An error produced while EMITTING YAML — today, a <see cref="YamlValue"/>
/// built in code that the emitter refuses to write: one nested deeper than
/// the parser's nesting limit, one that reuses collections or long strings so
/// much that writing every reuse in full, the only encoding the OKF subset
/// allows (it has no anchors or aliases), would pass the emitter's sharing
/// limit, or one whose text would pass the emitter's total output limit.
///
/// It derives from <see cref="OkfException"/> for one reason: the parser
/// signals the same condition as a <see cref="YamlParseException"/>, which is
/// an <see cref="OkfException"/>, and every layer that turns library failures
/// into data already catches that base type
/// (<c>BundleConceptWriter</c>'s <c>RunTool</c>, the CLI's top-level handler).
/// A bare <see cref="InvalidOperationException"/> here escaped both: it threw
/// out of <c>okf_verify</c> into the MCP host, and killed the CLI with a stack
/// trace — while <c>VerificationOutcome</c> promises errors-as-data, never
/// thrown. The type is what makes that promise true on every path, so an
/// emitter failure must never be signalled with anything else.
/// </summary>
public sealed class YamlEmitException : OkfException
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    /// <param name="message">What could not be emitted.</param>
    public YamlEmitException(string message)
        : base($"YAML emit error: {message}")
    {
    }
}
