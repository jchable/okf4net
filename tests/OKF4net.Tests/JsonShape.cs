// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

namespace OKF4net.Tests;

/// <summary>
/// Structural comparison of two JSON documents for the full-projection tests
/// (design §5.3): the expected side is a literal derived by hand, never a
/// captured output, and the comparison must catch what a field-by-field
/// assertion cannot -- a property that disappeared, appeared or was
/// duplicated, an array that lost an element, an integer a double would
/// round. Object property order is ignored (it is presentation, design §1);
/// array order is significant. Strings compare decoded, so an escaped
/// character in the actual text equals the plain character in the expected literal.
/// Numbers compare as text.
/// </summary>
internal static class JsonShape
{
    /// <summary>Asserts that <paramref name="actualJson"/> has exactly the shape and values of <paramref name="expectedJson"/>.</summary>
    /// <param name="expectedJson">The hand-derived expectation.</param>
    /// <param name="actualJson">The document the code under test produced.</param>
    public static void AssertEquivalent(string expectedJson, string actualJson)
    {
        using var expected = JsonDocument.Parse(expectedJson);
        using var actual = JsonDocument.Parse(actualJson);
        var differences = new List<string>();
        Compare(expected.RootElement, actual.RootElement, "$", differences);
        Assert.True(differences.Count == 0, "JSON differs from the expected projection:\n" + string.Join("\n", differences));
    }

    private static void Compare(JsonElement expected, JsonElement actual, string path, List<string> differences)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            differences.Add($"{path}: expected {expected.ValueKind}, got {actual.ValueKind}");
            return;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedNames = expected.EnumerateObject().Select(p => p.Name).ToList();
                var actualNames = actual.EnumerateObject().Select(p => p.Name).ToList();
                foreach (var name in actualNames.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).Order(StringComparer.Ordinal))
                {
                    differences.Add($"{path}.{name}: duplicated");
                }

                foreach (var name in expectedNames.Except(actualNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    differences.Add($"{path}.{name}: missing");
                }

                foreach (var name in actualNames.Except(expectedNames, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    differences.Add($"{path}.{name}: unexpected property");
                }

                foreach (var property in expected.EnumerateObject())
                {
                    if (actual.TryGetProperty(property.Name, out var actualValue))
                    {
                        Compare(property.Value, actualValue, $"{path}.{property.Name}", differences);
                    }
                }

                break;

            case JsonValueKind.Array:
                var expectedItems = expected.EnumerateArray().ToList();
                var actualItems = actual.EnumerateArray().ToList();
                if (expectedItems.Count != actualItems.Count)
                {
                    differences.Add($"{path}: expected {expectedItems.Count} elements, got {actualItems.Count}");
                }

                for (var i = 0; i < Math.Min(expectedItems.Count, actualItems.Count); i++)
                {
                    Compare(expectedItems[i], actualItems[i], $"{path}[{i}]", differences);
                }

                break;

            case JsonValueKind.String:
                if (!string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
                {
                    differences.Add($"{path}: expected \"{expected.GetString()}\", got \"{actual.GetString()}\"");
                }

                break;

            case JsonValueKind.Number:
                if (!string.Equals(expected.GetRawText(), actual.GetRawText(), StringComparison.Ordinal))
                {
                    differences.Add($"{path}: expected {expected.GetRawText()}, got {actual.GetRawText()}");
                }

                break;

            default:
                // True, False, Null: equal kinds are equal values.
                break;
        }
    }
}
