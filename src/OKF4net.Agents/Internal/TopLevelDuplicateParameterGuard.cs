// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace OKF4net.Agents.Internal;

/// <summary>
/// Wraps the <c>AIFunctionFactory</c>-created <c>okf_run_computation</c> so a model's
/// call naming the same top-level parameter twice is rejected instead of resolved.
/// The factory deserializes <c>parameterValues</c> into a dictionary on default
/// System.Text.Json options before <see cref="OkfBundleTools.RunComputationAsync"/>
/// runs, which keeps the last occurrence: <c>{"n": 1, "n": 2}</c> reached the binder
/// as <c>n = 2</c>, a value the model's own JSON also says is 1. Only the raw forms a
/// client hands over are read — a <see cref="JsonElement"/>, a <see cref="JsonNode"/>,
/// or JSON text as a <see langword="string"/>, all of which the factory deserializes
/// last-wins — and only their top level: a duplicate nested inside a value is <see cref="ParameterValues"/>' to
/// reject. Anything else, such as a C# caller's dictionary, passes through untouched.
/// </summary>
/// <remarks>
/// <see cref="DelegatingAIFunction"/> forwards the name, description, JSON schemas,
/// serializer options, underlying method and additional properties, so the model is
/// shown exactly what the factory generated.
/// </remarks>
internal sealed class TopLevelDuplicateParameterGuard(AIFunction innerFunction) : DelegatingAIFunction(innerFunction)
{
    /// <summary>The argument whose top-level names are checked.</summary>
    internal const string ParameterValuesArgument = "parameterValues";

    /// <inheritdoc />
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (arguments.TryGetValue(ParameterValuesArgument, out var raw) && HasDuplicateTopLevelName(raw))
        {
            // The tool's usual "Error: ..." text, marshalled the way the factory marshals
            // the method's own string result, so a caller sees one result shape.
            var error = $"Error: {ParameterValues.DuplicatePropertyError}";
            return new ValueTask<object?>(JsonSerializer.SerializeToElement(error, JsonSerializerOptions.GetTypeInfo(typeof(string))));
        }

        return base.InvokeCoreAsync(arguments, cancellationToken);
    }

    private static bool HasDuplicateTopLevelName(object? raw)
    {
        switch (raw)
        {
            case JsonElement element:
                return HasDuplicateTopLevelName(element);
            case string text:
                return HasDuplicateTopLevelName(text);
            case JsonNode node:
                // A node parsed from text and not yet read still writes its original
                // properties, duplicates included; the factory deserializes it last-wins.
                string written;
                try
                {
                    written = node.ToJsonString();
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    // A node that cannot be written is left to the factory's own binding.
                    return false;
                }

                return HasDuplicateTopLevelName(written);

            default:
                return false;
        }
    }

    private static bool HasDuplicateTopLevelName(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return HasDuplicateTopLevelName(document.RootElement);
        }
        catch (JsonException)
        {
            // Not JSON: the factory's own binding reports it.
            return false;
        }
    }

    private static bool HasDuplicateTopLevelName(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    return true;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // A name escaping a lone surrogate cannot be unescaped to compare; binding
            // it is left to the factory, as for any other argument it cannot read.
            return false;
        }

        return false;
    }
}
