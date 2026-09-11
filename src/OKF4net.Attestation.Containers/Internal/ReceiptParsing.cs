// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

namespace OKF4net.Attestation.Containers.Internal;

/// <summary>
/// Turns a container's raw <see cref="ContainerRunResult"/> into a
/// <see cref="Receipt"/>, or throws a <see cref="ContainerExecutionException"/>
/// explaining why it couldn't. Shared by ScriptComputationExecutor
/// and SqlClientComputationExecutor — both need
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

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Stdout);
        }
        catch (JsonException e)
        {
            throw new ContainerExecutionException($"{stageName} stdout was not valid JSON: {e.Message}", result.Stdout, result.Stderr);
        }

        using (document)
        {
            // A receipt is a JSON OBJECT, and only that. Deserializing straight into a
            // dictionary looked equivalent but was not: for the valid JSON literal
            // `null` it returns null, and coalescing that to an empty receipt let a run
            // whose entire stdout was `null` pass the shape check whenever
            // executor.receipt declared no fields. Every non-object shape -- null, an
            // array, a bare string or number -- is one rejection with one message.
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ContainerExecutionException($"{stageName} stdout was not a JSON object", result.Stdout, result.Stderr);
            }

            // Indexer assignment, not ToDictionary: a duplicated key is last-wins here,
            // as it was for the dictionary deserializer this replaces, rather than an
            // ArgumentException blamed on nothing the bundle author can see.
            var fields = new Dictionary<string, object?>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                fields[property.Name] = JsonValues.Normalize(property.Value);
            }

            return new Receipt(fields);
        }
    }
}
