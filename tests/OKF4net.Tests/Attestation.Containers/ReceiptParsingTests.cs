// SPDX-License-Identifier: LGPL-3.0-or-later
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
}
