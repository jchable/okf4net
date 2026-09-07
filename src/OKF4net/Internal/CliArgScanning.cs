// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// The two pieces genuinely shared by this repo's two hand-rolled CLI
/// argument scanners: <c>okf</c>'s multi-verb <c>CliArgs</c> and
/// <c>okf-render</c>'s single-command <c>Scan</c>. Everything else about
/// those two scanners is deliberately separate — <c>okf-render</c> has one
/// command and does not need <c>okf</c>'s <c>VerbSpec</c>/dispatch-table
/// machinery, so porting that machinery wholesale onto a one-command tool
/// would be over-engineering, not sharing. But two small rules were
/// byte-for-byte duplicated between them regardless, including their error
/// wording and the rationale comment attached to each: that is exactly the
/// kind of fork this repo does not allow (see <see cref="LfLines"/> and
/// <c>ReparsePoints</c> for the same policy applied elsewhere), so the two
/// rules live here once instead.
/// </summary>
internal static class CliArgScanning
{
    /// <summary>
    /// Whether the token following a valued flag at index <paramref name="i"/>
    /// is available to be consumed as that flag's value: there must be a next
    /// token, and it must not be the <c>--</c> separator. Swallowing the
    /// separator as a value would both hide "requires a value" and cancel the
    /// separator's own contract (nothing after it is ever a flag) for every
    /// token that follows.
    /// </summary>
    /// <param name="args">The full argument array being scanned.</param>
    /// <param name="i">The index of the valued flag itself.</param>
    internal static bool HasFollowingValue(string[] args, int i) =>
        i + 1 < args.Length && args[i + 1] != "--";

    /// <summary>
    /// Renders an exception's message for a human reading <c>error: ...</c>
    /// on a terminal, stripping .NET's <c>" (Parameter 'x')"</c> suffix that
    /// <see cref="ArgumentException"/> appends whenever
    /// <see cref="ArgumentException.ParamName"/> is set. That suffix is
    /// framework noise -- correct and useful for a library caller catching
    /// the exception, but out of place in CLI output meant for humans.
    /// </summary>
    internal static string UserMessage(Exception e)
    {
        if (e is ArgumentException { ParamName: not null } argEx)
        {
            var suffix = $" (Parameter '{argEx.ParamName}')";
            if (argEx.Message.EndsWith(suffix, StringComparison.Ordinal))
            {
                return argEx.Message[..^suffix.Length];
            }
        }

        return e.Message;
    }
}
