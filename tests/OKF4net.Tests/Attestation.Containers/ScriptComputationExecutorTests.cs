// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class ScriptComputationExecutorTests
{
    private static readonly ContainerRuntimeProfile Profile = new()
    {
        Image = "python:3.12-slim",
        Kind = ContainerRuntimeKind.Script,
    };

    private static readonly AttestedComputationContract Contract = new(
        Runtime: "python", Parameters: [], ComputationPath: null,
        Executor: new Executor(null, ["message"]), Attester: null);

    [Fact]
    public async Task Sends_the_bound_text_unchanged_on_stdin_and_params_as_a_JSON_env_var()
    {
        var engine = new FakeContainerEngine
        {
            Respond = _ => new ContainerRunResult(0, """{"message": "Hello, Ada!"}""", ""),
        };
        var executor = new ScriptComputationExecutor(engine, Profile);
        var bound = new BoundComputation("python", "print('hi')", null, new Dictionary<string, object?> { ["name"] = "Ada" });

        var receipt = await executor.ExecuteAsync(bound, Contract);

        Assert.Equal("print('hi')", engine.LastSpec!.Stdin);
        Assert.Equal("Hello, Ada!", receipt.Fields["message"]);
        var paramsJson = engine.LastSpec.Environment["OKF_PARAMS_JSON"];
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(paramsJson);
        Assert.Equal("Ada", parsed!["name"]!.ToString());
    }

    [Fact]
    public async Task Runs_with_no_network_access()
    {
        var engine = new FakeContainerEngine();
        var executor = new ScriptComputationExecutor(engine, Profile);
        await executor.ExecuteAsync(new BoundComputation("python", "print()", null, new Dictionary<string, object?>()), Contract);

        Assert.Equal("none", engine.LastSpec!.NetworkMode);
    }

    [Fact]
    public async Task A_non_zero_exit_code_throws_ContainerExecutionException_carrying_stderr()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(1, "", "Traceback...") };
        var executor = new ScriptComputationExecutor(engine, Profile);

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await executor.ExecuteAsync(new BoundComputation("python", "raise", null, new Dictionary<string, object?>()), Contract));
        Assert.Equal("Traceback...", ex.Stderr);
    }
}
