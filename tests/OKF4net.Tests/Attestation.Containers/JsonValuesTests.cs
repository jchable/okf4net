// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using OKF4net.Attestation.Containers;
using OKF4net.Attestation.Containers.Internal;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class JsonValuesTests
{
    private static readonly ContainerRunResult Run = new(0, "{}", "");

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static object? Normalize(string json) => JsonValues.Normalize(Parse(json), Run, "script");

    /// <summary>A JSON escape for a lone high surrogate, built so no tool or editor unescapes it on the way in.</summary>
    private static readonly string LoneSurrogate = (char)92 + "uD800";

    [Fact]
    public void Normalizes_scalars_and_null()
    {
        Assert.Equal("hi", Normalize("\"hi\""));
        Assert.Equal(42L, Normalize("42"));
        Assert.Equal(1.5, Normalize("1.5"));
        Assert.Equal(true, Normalize("true"));
        Assert.Null(Normalize("null"));
    }

    [Fact]
    public void Normalizes_nested_arrays_and_objects()
    {
        var result = Normalize("""{"a": [1, 2, {"b": "c"}]}""");
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        var list = Assert.IsType<List<object?>>(dict["a"]);
        Assert.Equal(1L, list[0]);
        Assert.Equal(2L, list[1]);
        var nested = Assert.IsType<Dictionary<string, object?>>(list[2]);
        Assert.Equal("c", nested["b"]);
    }

    /// <summary>
    /// An element parsed on default options still carries a duplicate, so the walk's
    /// own check is what stands between it and <c>ToDictionary</c>'s raw
    /// <see cref="System.ArgumentException"/> — this is the path the tool's parameter
    /// values take, where no strict parser ran first.
    /// </summary>
    [Theory]
    [InlineData("""{"a": {"b": 1, "b": 2}}""")]
    [InlineData("""[{"a": 1, "a": 1}]""")]
    public void A_duplicate_the_parser_let_through_is_still_rejected_by_the_walk(string json)
    {
        var ex = Assert.Throws<ContainerExecutionException>(() => Normalize(json));
        Assert.Equal("script stdout had a duplicate JSON property", ex.Message);
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("9223372036854775808")]
    [InlineData("0.10000000000000000001")]
    public void An_inexact_number_is_rejected(string json)
    {
        var ex = Assert.Throws<ContainerExecutionException>(() => Normalize(json));
        Assert.Equal("script stdout had a number that cannot be represented exactly", ex.Message);
    }

    /// <summary>
    /// System.Text.Json accepts an escaped lone surrogate at parse time and throws
    /// <see cref="System.InvalidOperationException"/> when the string is read; the
    /// normaliser throws nothing but its stage failure.
    /// </summary>
    [Fact]
    public void A_string_or_name_escaping_a_lone_surrogate_is_rejected_not_thrown_raw()
    {
        foreach (var json in new[] { "[\"" + LoneSurrogate + "\"]", "{\"" + LoneSurrogate + "\": 1}" })
        {
            var ex = Assert.Throws<ContainerExecutionException>(() => Normalize(json));
            Assert.Equal("script stdout had a string that is not valid Unicode", ex.Message);
        }
    }
}
