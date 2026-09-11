// SPDX-License-Identifier: LGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class SqlClientComputationExecutorTests
{
    private static readonly ContainerRuntimeProfile Profile = new()
    {
        Image = "python:3.12-slim",
        Kind = ContainerRuntimeKind.SqlClient,
        Environment = new Dictionary<string, string> { ["OKF_CONN"] = "postgresql://u:p@host/db" },
    };

    private static readonly AttestedComputationContract Contract = new(
        Runtime: "postgres", Parameters: [], ComputationPath: null,
        Executor: new Executor(null, ["executed_sql", "result"]), Attester: null);

    [Fact]
    public async Task Sends_the_bound_SQL_unchanged_with_values_in_the_same_envelope()
    {
        var engine = new FakeContainerEngine
        {
            Respond = _ => new ContainerRunResult(0, """{"executed_sql": "SELECT :x", "result": [{"active_users": 3}]}""", ""),
        };
        var executor = new SqlClientComputationExecutor(engine, Profile);
        var bound = new BoundComputation("postgres", "SELECT :x", null, new Dictionary<string, object?> { ["x"] = 1 });

        var receipt = await executor.ExecuteAsync(bound, Contract);

        var envelope = JsonSerializer.Deserialize<JsonElement>(engine.LastSpec!.Stdin!);
        Assert.Equal("SELECT :x", envelope.GetProperty("sql").GetString());
        Assert.Equal(1, envelope.GetProperty("values").GetProperty("x").GetInt32());
        Assert.Equal("SELECT :x", receipt.Fields["executed_sql"]);
    }

    [Fact]
    public async Task Does_not_restrict_network_access()
    {
        var engine = new FakeContainerEngine();
        var executor = new SqlClientComputationExecutor(engine, Profile);
        await executor.ExecuteAsync(new BoundComputation("postgres", "SELECT 1", null, new Dictionary<string, object?>()), Contract);

        Assert.Null(engine.LastSpec!.NetworkMode);
    }

    [Fact]
    public async Task Passes_the_connection_string_through_as_an_environment_variable()
    {
        var engine = new FakeContainerEngine();
        var executor = new SqlClientComputationExecutor(engine, Profile);
        await executor.ExecuteAsync(new BoundComputation("postgres", "SELECT 1", null, new Dictionary<string, object?>()), Contract);

        Assert.Equal("postgresql://u:p@host/db", engine.LastSpec!.Environment["OKF_CONN"]);
    }

    /// <summary>
    /// The wrapper binds via <c>conn.run(sql, **values)</c>, so a declared
    /// parameter whose name happens to match one of pg8000's own keyword
    /// arguments collides with the driver's signature. Left alone that surfaced
    /// inside the container as a bare Python <c>TypeError</c> — "got multiple
    /// values for argument 'sql'" — attributed to the bundle's query, which is
    /// both confusing and a long way from the cause.
    ///
    /// Caught here rather than in the shared <c>DeclaredParameterFilter</c>: the
    /// collision is a property of *this* transport. A Script profile passes its
    /// values as JSON in an environment variable and has no such reserved names,
    /// so rejecting them globally would refuse a perfectly good bundle on the
    /// other path.
    /// </summary>
    [Theory]
    [InlineData("sql")]
    [InlineData("stream")]
    [InlineData("types")]
    public async Task Rejects_a_parameter_name_that_collides_with_the_drivers_own_kwargs(string name)
    {
        var engine = new FakeContainerEngine();
        var executor = new SqlClientComputationExecutor(engine, Profile);
        var bound = new BoundComputation("postgres", "SELECT 1", null, new Dictionary<string, object?> { [name] = "x" });

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            async () => await executor.ExecuteAsync(bound, Contract));

        Assert.Contains(name, ex.Message, StringComparison.Ordinal);
        // Nothing was run: the collision is caught before any container starts.
        Assert.Null(engine.LastSpec);
    }
}
