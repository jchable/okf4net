// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

namespace OKF4net.Attestation.Containers.Internal;

/// <summary>Converts a parsed <see cref="JsonElement"/> tree into plain CLR values, recursively.</summary>
internal static class JsonValues
{
    /// <summary>
    /// Normalizes <paramref name="element"/> into <see langword="string"/>,
    /// <see langword="long"/>/<see langword="double"/>, <see langword="bool"/>,
    /// <see langword="null"/>, <see cref="List{T}"/>, or
    /// <see cref="Dictionary{TKey,TValue}"/> — never a boxed
    /// <see cref="JsonElement"/> — so receipt/verdict fields compare equal to
    /// plain values the way existing tests already write them (e.g.
    /// <c>["result"] = 42</c>).
    /// </summary>
    public static object? Normalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(Normalize).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => Normalize(p.Value)),
        _ => null,
    };
}
