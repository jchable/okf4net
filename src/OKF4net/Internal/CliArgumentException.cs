// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// Signals a malformed command-line invocation detected by <see cref="CliArgs"/>:
/// an unknown option, an unexpected positional, a valued flag left without a
/// value, a flag given more than once, or a missing required positional.
///
/// This is the single scanner-level error shared by both <c>okf</c> and
/// <c>okf-render</c>. Neither binary lets it escape to the top level: each
/// catches it at the same point it used to catch its own private
/// <c>CliOperationException</c> and renders it as <c>error: {message}</c> on
/// stderr with exit code 1 — the shape both CLIs already used, unchanged by
/// this type moving into the shared scanner.
/// </summary>
internal sealed class CliArgumentException(string message) : Exception(message);
