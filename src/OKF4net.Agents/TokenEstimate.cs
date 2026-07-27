// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Agents;

/// <summary>
/// <see cref="OkfContextProvider"/>'s single token-estimation seam: a
/// dependency-free approximation of a text's token count, used both to
/// interpret <see cref="OkfContextProviderOptions.TokenBudget"/> and to
/// decide where injected content must be truncated. Deliberately crude (no
/// tokenizer dependency) — the estimate only needs to be monotonic in text
/// length, not exact.
/// </summary>
internal static class TokenEstimate
{
    /// <summary>Approximate token count for <paramref name="text"/>, estimated as <c>chars / 4</c> (floored).</summary>
    internal static int Chars(string text) => text.Length / 4;
}
