// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OKF4net.Attestation.Internal;

namespace OKF4net.Agents.Internal;

/// <summary>
/// Turns the values an <c>AIFunction</c> invocation binds for
/// <c>okf_run_computation</c> — every one a <see cref="JsonElement"/>, which
/// is System.Text.Json's representation of an <see cref="object"/>-typed
/// dictionary value — into the plain CLR values a §10 binder type-checks
/// against a concept's declared <c>parameters</c>. Without this, a binder
/// that checks <c>value is int or long</c> rejects every typed parameter the
/// tool ever receives, while the same call from C# with a boxed <c>int</c>
/// succeeds — which is exactly how the gap stayed invisible.
/// </summary>
/// <remarks>
/// Each value is held to the strict JSON value contract container receipts are held
/// to (<see cref="StrictJsonValues"/>, in <c>OKF4net.Attestation</c>): a number that
/// has no exact <see langword="long"/> or <see langword="double"/> counterpart, a
/// duplicate property inside a value, or a string escaping a lone surrogate is
/// rejected rather than rounded, overflowed or resolved last-wins. A duplicate
/// <em>top-level</em> parameter name never reaches this class: by then
/// Microsoft.Extensions.AI has deserialized the <c>parameterValues</c> object into a
/// dictionary, one entry per name. The tool the model calls rejects it before that,
/// in <see cref="TopLevelDuplicateParameterGuard"/>, with
/// <see cref="DuplicatePropertyError"/>; a direct C# caller of
/// <see cref="OkfBundleTools.RunComputationAsync"/> passes a dictionary, which cannot
/// carry one.
/// </remarks>
internal static class ParameterValues
{
    /// <summary>The fixed sentence for a duplicate property, at the top level or nested.</summary>
    internal const string DuplicatePropertyError = "parameterValues had a duplicate JSON property.";

    /// <summary>
    /// Normalizes every <see cref="JsonElement"/> in <paramref name="values"/>;
    /// any other value passes through unchanged.
    /// </summary>
    /// <param name="values">The bound parameter values.</param>
    /// <param name="normalized">The normalized values, when this returns <see langword="true"/>.</param>
    /// <param name="error">
    /// When this returns <see langword="false"/>, a fixed sentence saying which rule a
    /// value broke. It never quotes the value, its property names or its number
    /// literal.
    /// </param>
    /// <returns><see langword="true"/> when every value was normalized.</returns>
    internal static bool TryNormalize(
        IReadOnlyDictionary<string, object?> values,
        [NotNullWhen(true)] out IReadOnlyDictionary<string, object?>? normalized,
        [NotNullWhen(false)] out string? error)
    {
        var result = new Dictionary<string, object?>(values.Count, StringComparer.Ordinal);
        try
        {
            foreach (var (key, value) in values)
            {
                result[key] = value is JsonElement element ? StrictJsonValues.Normalize(element) : value;
            }
        }
        catch (StrictJsonException e)
        {
            normalized = null;
            error = e.Violation switch
            {
                StrictJsonViolation.DuplicateProperty => DuplicatePropertyError,
                StrictJsonViolation.InexactNumber => "parameterValues had a number that cannot be represented exactly.",
                _ => "parameterValues had a string that is not valid Unicode.",
            };
            return false;
        }

        normalized = result;
        error = null;
        return true;
    }
}
