// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using OKF4net.Attestation.Containers.Internal;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class JsonValuesTests
{
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Normalizes_scalars_and_null()
    {
        Assert.Equal("hi", JsonValues.Normalize(Parse("\"hi\"")));
        Assert.Equal(42L, JsonValues.Normalize(Parse("42")));
        Assert.Equal(1.5, JsonValues.Normalize(Parse("1.5")));
        Assert.Equal(true, JsonValues.Normalize(Parse("true")));
        Assert.Null(JsonValues.Normalize(Parse("null")));
    }

    [Fact]
    public void Normalizes_nested_arrays_and_objects()
    {
        var result = JsonValues.Normalize(Parse("""{"a": [1, 2, {"b": "c"}]}"""));
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        var list = Assert.IsType<List<object?>>(dict["a"]);
        Assert.Equal(1L, list[0]);
        Assert.Equal(2L, list[1]);
        var nested = Assert.IsType<Dictionary<string, object?>>(list[2]);
        Assert.Equal("c", nested["b"]);
    }
}
