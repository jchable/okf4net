// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

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
internal static class ParameterValues
{
    internal static IReadOnlyDictionary<string, object?> Normalize(IReadOnlyDictionary<string, object?> values)
    {
        var result = new Dictionary<string, object?>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            result[key] = value is JsonElement element ? FromElement(element) : value;
        }

        return result;
    }

    private static object? FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => FromElement(p.Value), StringComparer.Ordinal),
        _ => null,
    };
}
