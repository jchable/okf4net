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
    /// The parser, not only the normaliser's walk, rejects a duplicate: the walk would
    /// catch the same name, so this test and the next are built to tell the two apart.
    /// Straight into <c>ParseJson</c> no walk runs at all; through <c>Parse</c>, an
    /// inexact number placed before the duplicate would be the walk's first failure, so
    /// only a parser that already refused the document reports the duplicate. Each test
    /// goes red on its own if <c>AllowDuplicateProperties = false</c> is dropped.
    /// </summary>
    [Fact]
    public void ParseJson_itself_rejects_a_duplicate_in_an_object_root()
    {
        var direct = Assert.Throws<ContainerExecutionException>(
            () => ReceiptParsing.ParseJson(new ContainerRunResult(0, """{"a": 1, "a": 2}""", ""), "script"));
        Assert.Equal("script stdout had a duplicate JSON property", direct.Message);
    }

    [Fact]
    public void Parse_reports_a_duplicate_ahead_of_an_earlier_inexact_number()
    {
        var throughParse = Assert.Throws<ContainerExecutionException>(
            () => ReceiptParsing.Parse(new ContainerRunResult(0, """{"v": 1e400, "a": 1, "a": 2}""", ""), "script"));
        Assert.Equal("script stdout had a duplicate JSON property", throughParse.Message);
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
    // The exact binary expansion of the double nearest 0.3 is rejected: "the same
    // decimal value" means the same value as the double's shortest round-trip form
    // ("0.30000000000000004"), not the double's exact binary value. Pinned so a move
    // to exact-binary identity is a decision, not a drive-by.
    [InlineData("0.3000000000000000444089209850062616169452667236328125")]
    [InlineData("1e-1000000000000000000")]
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
    /// Builds a literal too long for <c>InlineData</c> (whose value would also become the
    /// test's display name): <c>D</c> stands for <paramref name="size"/> nines.
    /// </summary>
    private static string HugeLiteral(string shape, int size)
    {
        var nines = new string('9', size);
        return shape switch
        {
            "0e+D" => "0e" + nines,
            "-0e-D" => "-0e-" + nines,
            "0.000e+D" => "0.000e+" + nines,
            "1e-D" => "1e-" + nines,
            "-1e-D" => "-1e-" + nines,
            "1e+D" => "1e" + nines,
            "0.5e+D" => "0.5e" + nines,
            // A mantissa as long as the exponent, pulling it back into range: both denote 1.
            "1 then zeros, e-(size-1)" => "1" + new string('0', size - 1) + "e-" + (size - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "0. then zeros then 1, e+size" => "0." + new string('0', size - 1) + "1e" + size.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
    }

    /// <summary>
    /// Literals whose exponent, or mantissa, runs to a million digits are decided by
    /// value, like any other: a zero mantissa is zero whatever its exponent, a non-zero
    /// one with an exponent no double reaches is rejected, and a long mantissa that pulls
    /// a long exponent back into range is an ordinary 1.
    /// </summary>
    [Theory]
    [InlineData("0e+D", true, 0.0)]
    [InlineData("-0e-D", true, -0.0)]
    [InlineData("0.000e+D", true, 0.0)]
    [InlineData("1e-D", false, 0.0)]
    [InlineData("-1e-D", false, 0.0)]
    [InlineData("1e+D", false, 0.0)]
    [InlineData("0.5e+D", false, 0.0)]
    [InlineData("1 then zeros, e-(size-1)", true, 1.0)]
    [InlineData("0. then zeros then 1, e+size", true, 1.0)]
    public void A_million_digit_exponent_or_mantissa_is_decided_by_its_value(string shape, bool accepted, double value)
    {
        var result = new ContainerRunResult(0, $$"""{"v": {{HugeLiteral(shape, 1_000_000)}}}""", "");
        if (accepted)
        {
            Assert.Equal(value, Assert.IsType<double>(ReceiptParsing.Parse(result, "script").Fields["v"]));
        }
        else
        {
            var ex = Assert.Throws<ContainerExecutionException>(() => ReceiptParsing.Parse(result, "script"));
            Assert.Equal("script stdout had a number that cannot be represented exactly", ex.Message);
        }
    }

    /// <summary>
    /// The number rule runs on the host after the container has exited, where no
    /// container ceiling and no cancellation token bounds it, so its cost must stay
    /// linear in the literal. Parsing the exponent's digits as a <c>BigInteger</c> was
    /// super-linear: an 8-million-digit exponent (under the engine's output ceiling) took
    /// about 7 s. The bound is wide — the linear walk measures in tens of milliseconds —
    /// so it trips on the complexity class, not on a slow runner.
    /// </summary>
    [Theory]
    [InlineData("0e+D")]
    [InlineData("1e-D")]
    public void An_eight_million_digit_exponent_is_decided_in_linear_time(string shape)
    {
        var result = new ContainerRunResult(0, $$"""{"v": {{HugeLiteral(shape, 8_000_000)}}}""", "");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            ReceiptParsing.Parse(result, "script");
        }
        catch (ContainerExecutionException)
        {
            // Accepted or rejected is the other test's business; only the cost is pinned here.
        }

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.ElapsedMilliseconds} ms");
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
