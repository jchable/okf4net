// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Attestation.Internal;

namespace OKF4net.Attestation.Containers.Internal;

/// <summary>
/// Converts a container's parsed stdout — a receipt or an attester verdict — into
/// plain CLR values under the strict JSON value contract of
/// <see cref="StrictJsonValues"/>, failing the stage with a
/// <see cref="ContainerExecutionException"/> when the document breaks it.
/// </summary>
internal static class JsonValues
{
    /// <summary>
    /// Normalizes <paramref name="element"/> into <see langword="string"/>,
    /// <see langword="long"/>/<see langword="double"/>, <see langword="bool"/>,
    /// <see langword="null"/>, <see cref="List{T}"/>, or
    /// <see cref="Dictionary{TKey,TValue}"/> — never a boxed
    /// <see cref="JsonElement"/> — so receipt/verdict fields compare equal to
    /// plain values the way existing tests already write them (e.g.
    /// <c>["result"] = 42L</c>).
    /// </summary>
    /// <param name="element">A value parsed from <paramref name="result"/>'s stdout.</param>
    /// <param name="result">The run the element was parsed from; its stdout and stderr ride on a thrown exception.</param>
    /// <param name="stageName">Names the stage in a thrown exception's message (e.g. <c>"script"</c>, <c>"attester"</c>).</param>
    /// <exception cref="ContainerExecutionException">
    /// A number cannot be represented exactly, a property is duplicated, or a string
    /// escapes a lone surrogate — at any depth. Nothing else is thrown.
    /// </exception>
    public static object? Normalize(JsonElement element, ContainerRunResult result, string stageName)
    {
        try
        {
            return StrictJsonValues.Normalize(element);
        }
        catch (StrictJsonException e)
        {
            throw Rejection(e.Violation, result, stageName);
        }
    }

    /// <summary>
    /// The stage failure for <paramref name="violation"/>. Fixed wording naming only the
    /// stage: the offending property name or literal is container output, and this
    /// message is rendered into model-facing reasons.
    /// </summary>
    internal static ContainerExecutionException Rejection(StrictJsonViolation violation, ContainerRunResult result, string stageName) => new(
        violation switch
        {
            StrictJsonViolation.DuplicateProperty => $"{stageName} stdout had a duplicate JSON property",
            StrictJsonViolation.InexactNumber => $"{stageName} stdout had a number that cannot be represented exactly",
            _ => $"{stageName} stdout had a string that is not valid Unicode",
        },
        result.Stdout,
        result.Stderr);
}
