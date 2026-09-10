// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

namespace OKF4net.Attestation.Containers.Internal;

/// <summary>
/// Turns a container's raw <see cref="ContainerRunResult"/> into a
/// <see cref="Receipt"/>, or throws a <see cref="ContainerExecutionException"/>
/// explaining why it couldn't. Shared by ScriptComputationExecutor
/// and SqlClientComputationExecutor (Tasks 4/5) — both need
/// exactly this "non-zero exit is a failure; otherwise the stdout JSON
/// object's fields, normalized, are the receipt" logic, differing only in
/// the stage name, which appears in a thrown exception's message.
/// </summary>
internal static class ReceiptParsing
{
    /// <summary>Parses <paramref name="result"/> into a <see cref="Receipt"/>.</summary>
    /// <param name="result">The container's raw run result.</param>
    /// <param name="stageName">Names the stage in a thrown exception's message (e.g. <c>"script"</c>, <c>"SQL wrapper"</c>).</param>
    public static Receipt Parse(ContainerRunResult result, string stageName)
    {
        if (result.ExitCode != 0)
        {
            throw new ContainerExecutionException($"{stageName} exited with code {result.ExitCode}", result.Stdout, result.Stderr);
        }

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result.Stdout);
        }
        catch (JsonException e)
        {
            throw new ContainerExecutionException($"{stageName} stdout was not valid JSON: {e.Message}", result.Stdout, result.Stderr);
        }

        var fields = (parsed ?? []).ToDictionary(kv => kv.Key, kv => JsonValues.Normalize(kv.Value));
        return new Receipt(fields);
    }
}
