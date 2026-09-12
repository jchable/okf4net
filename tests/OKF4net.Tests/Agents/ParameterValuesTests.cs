// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using OKF4net.Agents.Internal;
using Xunit;

namespace OKF4net.Tests.Agents;

public class ParameterValuesTests
{
    [Theory]
    [InlineData("2026", typeof(long))]
    [InlineData("1.5", typeof(double))]
    [InlineData("\"Ada\"", typeof(string))]
    [InlineData("true", typeof(bool))]
    public void A_JsonElement_scalar_becomes_the_matching_CLR_value(string json, System.Type expected)
    {
        var values = new Dictionary<string, object?> { ["p"] = JsonSerializer.Deserialize<JsonElement>(json) };
        var normalized = ParameterValues.Normalize(values);
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
        var normalized = ParameterValues.Normalize(values);
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
        var normalized = ParameterValues.Normalize(values);
        Assert.Equal(42, normalized["p"]);
        Assert.Equal("q3", normalized["s"]);
    }
}
