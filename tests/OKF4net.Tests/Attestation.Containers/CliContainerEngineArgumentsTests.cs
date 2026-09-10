// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class CliContainerEngineArgumentsTests
{
    private static ContainerRunSpec Spec(
        string image = "python:3.12-slim",
        IReadOnlyDictionary<string, string>? env = null,
        string? network = "none",
        long? memory = 512L * 1024 * 1024,
        double? cpus = 1.0,
        int? pids = 64)
        => new(image, ["python3", "-"], "print()", env ?? new Dictionary<string, string>(), network, memory, cpus, pids, TimeSpan.FromMinutes(1));

    [Fact]
    public void Every_argument_is_its_own_array_element_never_one_concatenated_string()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(env: new Dictionary<string, string> { ["X"] = "a b; rm -rf /" }), "okf-abc");
        // The hostile-looking value must appear as ONE element, never split
        // or interpreted -- proving no shell ever sees this as source text.
        Assert.Contains("X=a b; rm -rf /", args);
        Assert.DoesNotContain(args, a => a.Contains("&&") || a.Contains("|"));
    }

    [Fact]
    public void Includes_run_rm_and_a_unique_name()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(), "okf-xyz");
        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        var nameIndex = args.ToList().IndexOf("--name");
        Assert.True(nameIndex >= 0);
        Assert.Equal("okf-xyz", args[nameIndex + 1]);
    }

    [Fact]
    public void Includes_resource_limit_flags_with_their_configured_values()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(memory: 256L * 1024 * 1024, cpus: 0.5, pids: 32), "okf-1");
        Assert.Contains("--memory", args);
        Assert.Contains((256L * 1024 * 1024).ToString(), args);
        Assert.Contains("--cpus", args);
        Assert.Contains("0.5", args);
        Assert.Contains("--pids-limit", args);
        Assert.Contains("32", args);
    }

    [Fact]
    public void Omits_network_flag_when_NetworkMode_is_null()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(network: null), "okf-1");
        Assert.DoesNotContain("--network", args);
    }

    [Fact]
    public void Ends_with_the_image_then_the_command()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(image: "postgres:16-alpine"), "okf-1");
        var imageIndex = args.ToList().IndexOf("postgres:16-alpine");
        Assert.True(imageIndex >= 0);
        Assert.Equal("python3", args[imageIndex + 1]);
        Assert.Equal("-", args[imageIndex + 2]);
    }
}
