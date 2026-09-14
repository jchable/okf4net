// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using OKF4net.Agents.Internal;
using Xunit;

namespace OKF4net.Tests.Agents;

public class ParameterValuesTests
{
    private static IReadOnlyDictionary<string, object?> NormalizeOrFail(Dictionary<string, object?> values)
    {
        Assert.True(ParameterValues.TryNormalize(values, out var normalized, out var error), error);
        return normalized;
    }

    [Theory]
    [InlineData("2026", typeof(long))]
    [InlineData("1.5", typeof(double))]
    [InlineData("\"Ada\"", typeof(string))]
    [InlineData("true", typeof(bool))]
    public void A_JsonElement_scalar_becomes_the_matching_CLR_value(string json, System.Type expected)
    {
        var values = new Dictionary<string, object?> { ["p"] = JsonSerializer.Deserialize<JsonElement>(json) };
        var normalized = NormalizeOrFail(values);
        Assert.IsType(expected, normalized["p"]);
    }

    [Fact]
    public void Null_arrays_and_objects_are_normalized_recursively()
    {
        var values = new Dictionary<string, object?>
        {
            ["n"] = JsonSerializer.Deserialize<JsonElement>("null"),
            ["a"] = JsonSerializer.Deserialize<JsonElement>("[1, \"x\"]"),
            ["o"] = JsonSerializer.Deserialize<JsonElement>("{\"k\": 2}"),
        };
        var normalized = NormalizeOrFail(values);
        Assert.Null(normalized["n"]);
        var list = Assert.IsType<List<object?>>(normalized["a"]);
        Assert.Equal(1L, list[0]);
        Assert.Equal("x", list[1]);
        var map = Assert.IsType<Dictionary<string, object?>>(normalized["o"]);
        Assert.Equal(2L, map["k"]);
    }

    [Fact]
    public void A_native_CLR_value_passes_through_unchanged()
    {
        var values = new Dictionary<string, object?> { ["p"] = 42, ["s"] = "q3" };
        var normalized = NormalizeOrFail(values);
        Assert.Equal(42, normalized["p"]);
        Assert.Equal("q3", normalized["s"]);
    }

    /// <summary>
    /// The receipt's strict JSON contract, applied to a parameter value: the error is a
    /// fixed sentence that never quotes the value, its names or its literal.
    /// </summary>
    [Theory]
    [InlineData("1e400", "parameterValues had a number that cannot be represented exactly.")]
    [InlineData("-1e400", "parameterValues had a number that cannot be represented exactly.")]
    [InlineData("1e-400", "parameterValues had a number that cannot be represented exactly.")]
    [InlineData("9223372036854775808", "parameterValues had a number that cannot be represented exactly.")]
    [InlineData("0.10000000000000000001", "parameterValues had a number that cannot be represented exactly.")]
    [InlineData("""{"secret_name": 1, "secret_name": 2}""", "parameterValues had a duplicate JSON property.")]
    [InlineData("""[{"secret_name": 1, "secret_name": 1}]""", "parameterValues had a duplicate JSON property.")]
    public void A_value_that_breaks_the_strict_JSON_contract_is_reported_not_thrown(string json, string expected)
    {
        var values = new Dictionary<string, object?> { ["p"] = JsonSerializer.Deserialize<JsonElement>(json) };

        Assert.False(ParameterValues.TryNormalize(values, out var normalized, out var error));

        Assert.Null(normalized);
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("0.1", 0.1)]
    [InlineData("1234.56", 1234.56)]
    [InlineData("1.5e3", 1500.0)]
    [InlineData("1e308", 1e308)]
    [InlineData("5e-324", double.Epsilon)]
    public void An_exact_number_becomes_a_long_or_a_double(string json, object expected)
    {
        var values = new Dictionary<string, object?> { ["p"] = JsonSerializer.Deserialize<JsonElement>(json) };
        Assert.Equal(expected, NormalizeOrFail(values)["p"]);
    }
}
