// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Attestation.Internal;

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
        using var document = ParseJson(result, stageName);

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

        // A duplicated key never reaches this point: ParseJson rejects it at any depth.
        // Every number in every field is held to one exactness rule (JsonValues).
        var fields = (Dictionary<string, object?>)JsonValues.Normalize(document.RootElement, result, stageName)!;
        return new Receipt(fields);
    }

    /// <summary>
    /// Checks <paramref name="result"/>'s exit code and parses its stdout as JSON
    /// with no duplicate property at any depth, throwing a
    /// <see cref="ContainerExecutionException"/> naming <paramref name="stageName"/>
    /// for any failure. Shared by <see cref="Parse"/> and <c>ContainerAttester</c>,
    /// which each apply their own shape check to the returned document afterward,
    /// and normalize it through <see cref="JsonValues"/> — the caller owns disposing it.
    /// </summary>
    /// <param name="result">The container's raw run result.</param>
    /// <param name="stageName">Names the stage in a thrown exception's message (e.g. <c>"script"</c>, <c>"attester"</c>).</param>
    public static JsonDocument ParseJson(ContainerRunResult result, string stageName)
    {
        if (result.ExitCode != 0)
        {
            throw new ContainerExecutionException($"{stageName} exited with code {result.ExitCode}", result.Stdout, result.Stderr);
        }

        // Duplicate properties are rejected at any depth rather than resolved. RFC 8259
        // leaves a repeated name's meaning undefined, so two readers of the same output
        // -- this host building the receipt, the bundle's attester reading it back, an
        // auditor re-reading the logged stdout -- can each retain a different value, and
        // the receipt a verdict was reached on would not be the one displayed (§10.5).
        try
        {
            return JsonDocument.Parse(result.Stdout, StrictOptions);
        }
        catch (JsonException e)
        {
            // Told apart by re-parsing, not by matching e.Message: the options differ
            // only in duplicate handling, so a document that parses once duplicates are
            // allowed failed on a duplicate. The fixed wording matters, because the
            // parser's own message quotes the property name -- container output.
            if (ParsesWithDuplicatesAllowed(result.Stdout))
            {
                throw JsonValues.Rejection(StrictJsonViolation.DuplicateProperty, result, stageName);
            }

            throw new ContainerExecutionException($"{stageName} stdout was not valid JSON: {e.Message}", result.Stdout, result.Stderr);
        }
        catch (InvalidOperationException)
        {
            // The duplicate check unescapes every property name while parsing, and a
            // name escaping a lone surrogate (e.g. "\uD800") cannot be unescaped:
            // System.Text.Json throws InvalidOperationException for it, not JsonException.
            throw JsonValues.Rejection(StrictJsonViolation.InvalidString, result, stageName);
        }
    }

    private static readonly JsonDocumentOptions StrictOptions = new() { AllowDuplicateProperties = false };

    private static bool ParsesWithDuplicatesAllowed(string stdout)
    {
        try
        {
            using var _ = JsonDocument.Parse(stdout);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
