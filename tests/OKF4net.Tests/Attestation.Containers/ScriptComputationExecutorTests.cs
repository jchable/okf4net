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

    /// <summary>
    /// A sanctioned script's own temp files follow <c>TMPDIR</c>, so it has to point at
    /// the scratch the host mounted — a script using <c>tempfile</c> under
    /// <c>Isolation.TmpfsMounts = ["/scratch"]</c> would otherwise hit a read-only <c>/tmp</c>.
    /// Setting it must not cost the script its parameters.
    /// </summary>
    [Fact]
    public async Task Points_TMPDIR_at_the_first_configured_mount_alongside_the_params()
    {
        var engine = new FakeContainerEngine();
        var executor = new ScriptComputationExecutor(engine, Profile with { Isolation = Profile.Isolation with { TmpfsMounts = ["/scratch"] } });
        await executor.ExecuteAsync(new BoundComputation("python", "print()", null, new Dictionary<string, object?> { ["n"] = 1 }), Contract);

        Assert.Equal(["/scratch"], engine.LastSpec!.TmpfsMounts);
        Assert.Equal("/scratch", engine.LastSpec.Environment["TMPDIR"]);
        Assert.Equal("""{"n":1}""", engine.LastSpec.Environment["OKF_PARAMS_JSON"]);
    }

    /// <summary>
    /// The host's explicit <c>TMPDIR</c> wins over the derived one, as on every stage;
    /// <c>OKF_PARAMS_JSON</c> is the executor's own and still cannot be overridden.
    /// </summary>
    [Fact]
    public async Task A_TMPDIR_the_host_set_wins_but_OKF_PARAMS_JSON_stays_the_executors()
    {
        var engine = new FakeContainerEngine();
        var profile = Profile with
        {
            Isolation = Profile.Isolation with { TmpfsMounts = ["/scratch", "/work"] },
            Environment = new Dictionary<string, string> { ["TMPDIR"] = "/work", ["OKF_PARAMS_JSON"] = "forged" },
        };
        await new ScriptComputationExecutor(engine, profile)
            .ExecuteAsync(new BoundComputation("python", "print()", null, new Dictionary<string, object?>()), Contract);

        Assert.Equal("/work", engine.LastSpec!.Environment["TMPDIR"]);
        Assert.Equal("{}", engine.LastSpec.Environment["OKF_PARAMS_JSON"]);
    }

    /// <summary>
    /// An empty mount list is a legitimate profile — a script that writes nothing needs
    /// no scratch — and leaves <c>TMPDIR</c> unset rather than inventing one.
    /// </summary>
    [Fact]
    public async Task No_tmpfs_mount_sets_no_TMPDIR()
    {
        var engine = new FakeContainerEngine();
        await new ScriptComputationExecutor(engine, Profile with { Isolation = Profile.Isolation with { TmpfsMounts = [] } })
            .ExecuteAsync(new BoundComputation("python", "print()", null, new Dictionary<string, object?>()), Contract);

        Assert.True(engine.LastSpec!.ReadOnlyRootFilesystem);
        Assert.Empty(engine.LastSpec.TmpfsMounts);
        Assert.False(engine.LastSpec.Environment.ContainsKey("TMPDIR"));
    }
}
