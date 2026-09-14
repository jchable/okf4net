// SPDX-License-Identifier: LGPL-3.0-or-later
using System;
using System.Collections.Generic;
using OKF4net.Attestation.Containers;
using OKF4net.Attestation.Containers.Internal;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class ReceiptParsingTests
{
    [Fact]
    public void A_non_zero_exit_code_throws_with_the_stage_name_in_the_message()
    {
        var ex = Assert.Throws<ContainerExecutionException>(
            () => ReceiptParsing.Parse(new ContainerRunResult(1, "", "boom"), "script"));
        Assert.Contains("script exited with code 1", ex.Message);
        Assert.Equal("boom", ex.Stderr);
    }

    [Fact]
    public void Malformed_stdout_JSON_throws_with_the_stage_name_in_the_message()
    {
        var ex = Assert.Throws<ContainerExecutionException>(
            () => ReceiptParsing.Parse(new ContainerRunResult(0, "not json", ""), "SQL wrapper"));
        Assert.Contains("SQL wrapper stdout was not valid JSON", ex.Message);
    }

    [Fact]
    public void Valid_stdout_JSON_becomes_a_normalized_Receipt()
    {
        var receipt = ReceiptParsing.Parse(new ContainerRunResult(0, """{"message": "hi", "count": 3}""", ""), "script");
        Assert.Equal("hi", receipt.Fields["message"]);
        Assert.Equal(3L, receipt.Fields["count"]);
    }

    /// <summary>
    /// <c>JsonSerializer.Deserialize&lt;Dictionary&lt;…&gt;&gt;</c> returns <see langword="null"/>
    /// for the valid JSON literal <c>null</c>, and the first version coalesced that to an
    /// empty receipt — so a run whose whole stdout was <c>null</c> passed the shape check
    /// whenever <c>executor.receipt</c> declared no fields. A receipt is a JSON object and
    /// nothing else; every other JSON shape is one rejection, with one message.
    /// </summary>
    [Theory]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void Non_object_stdout_JSON_is_rejected_not_read_as_an_empty_receipt(string stdout)
    {
        var ex = Assert.Throws<ContainerExecutionException>(
            () => ReceiptParsing.Parse(new ContainerRunResult(0, stdout, ""), "script"));
        Assert.Contains("script stdout was not a JSON object", ex.Message);
    }

    [Fact]
    public void ParseJson_rejects_a_non_zero_exit_then_invalid_json_with_the_stage_name()
    {
        var ex1 = Assert.Throws<ContainerExecutionException>(() => ReceiptParsing.ParseJson(new ContainerRunResult(3, "", "boom"), "attester"));
        Assert.Equal("attester exited with code 3", ex1.Message);
        var ex2 = Assert.Throws<ContainerExecutionException>(() => ReceiptParsing.ParseJson(new ContainerRunResult(0, "nope", ""), "attester"));
        Assert.StartsWith("attester stdout was not valid JSON", ex2.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A duplicated property, at any depth, fails the stage. Last-wins was a choice,
    /// not a JSON rule: RFC 8259 leaves duplicate names undefined, so two readers of
    /// the same receipt — this host and an attester's own JSON reader — can retain
    /// different values. Nested duplicates used to escape as a raw
    /// <see cref="ArgumentException"/> from <c>ToDictionary</c>. The message is fixed
    /// text: the property name is container output and must not reach a model-facing
    /// reason, and the escaped spelling (<c>\u0073ecret_name</c>) is the same name.
    /// </summary>
    [Theory]
    [InlineData("""{"secret_name": 1, "secret_name": 2}""")]
    [InlineData("""{"a": {"secret_name": 1, "secret_name": 2}}""")]
    [InlineData("""{"a": [{"secret_name": 1, "secret_name": 1}]}""")]
    [InlineData("""{"secret_name": 1, "\u0073ecret_name": 2}""")]
    public void A_duplicate_property_at_any_depth_fails_the_stage_with_fixed_wording(string stdout)
    {
        var ex = Assert.Throws<ContainerExecutionException>(
            () => ReceiptParsing.Parse(new ContainerRunResult(0, stdout, ""), "script"));
        Assert.Equal("script stdout had a duplicate JSON property", ex.Message);
    }

    [Fact]
    public void ParseJson_rejects_a_duplicate_inside_an_array_root()
    {
        var ex = Assert.Throws<ContainerExecutionException>(
            () => ReceiptParsing.ParseJson(new ContainerRunResult(0, """[{"a": 1, "a": 1}]""", ""), "attester"));
        Assert.Equal("attester stdout had a duplicate JSON property", ex.Message);
    }

    /// <summary>
    /// One number rule for every receipt field, at any depth: an integer literal must
    /// fit a <see langword="long"/>, and any other literal must be a finite
    /// <see langword="double"/> that denotes the same decimal value. Before,
    /// <c>1e400</c> became infinity, <c>1e-400</c> became zero and
    /// <c>9223372036854775808</c> was silently rounded to a double — the receipt
    /// carried a value the container never wrote.
    /// </summary>
    [Theory]
    [InlineData("1e400")]
    [InlineData("-1e400")]
    [InlineData("1e-400")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("0.10000000000000000001")]
    [InlineData("0.1000000000000000000000000000001")]
    public void A_number_that_cannot_be_represented_exactly_fails_the_stage(string literal)
    {
        foreach (var stdout in new[] { $$"""{"v": {{literal}}}""", $$"""{"v": [{"w": {{literal}}}]}""" })
        {
            var ex = Assert.Throws<ContainerExecutionException>(
                () => ReceiptParsing.Parse(new ContainerRunResult(0, stdout, ""), "SQL wrapper"));
            Assert.Equal("SQL wrapper stdout had a number that cannot be represented exactly", ex.Message);
        }
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("0", 0L)]
    [InlineData("0.1", 0.1)]
    [InlineData("1234.56", 1234.56)]
    [InlineData("1.5e3", 1500.0)]
    [InlineData("1e308", 1e308)]
    [InlineData("5e-324", double.Epsilon)]
    [InlineData("-2.5E-5", -2.5e-5)]
    [InlineData("1.0", 1.0)]
    [InlineData("0e999999999999999999999", 0.0)]
    public void A_number_that_is_represented_exactly_becomes_a_long_or_a_double(string literal, object expected)
    {
        var receipt = ReceiptParsing.Parse(new ContainerRunResult(0, $$"""{"v": {{literal}}}""", ""), "script");
        Assert.Equal(expected, receipt.Fields["v"]);
    }

    /// <summary>
    /// The strict parser unescapes every property name to compare it, and an escaped
    /// lone surrogate cannot be unescaped: System.Text.Json throws
    /// <see cref="InvalidOperationException"/>, not <c>JsonException</c>, from the parse
    /// itself. A string value escaping one is only read later, in the normaliser. Both
    /// fail the stage with one fixed message rather than escaping raw. The escape is
    /// built from <c>(char)92</c> so no tool or editor unescapes it on the way in.
    /// </summary>
    [Fact]
    public void A_lone_surrogate_escape_in_a_name_or_a_value_fails_the_stage()
    {
        var escape = (char)92 + "uD800";
        foreach (var stdout in new[] { "{\"" + escape + "\": 1}", "{\"v\": \"" + escape + "\"}" })
        {
            var ex = Assert.Throws<ContainerExecutionException>(
                () => ReceiptParsing.Parse(new ContainerRunResult(0, stdout, ""), "script"));
            Assert.Equal("script stdout had a string that is not valid Unicode", ex.Message);
        }
    }
}
