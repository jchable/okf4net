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
    /// The wrapper binds via <c>conn.run(sql, **values)</c> against a driver whose
    /// signature is <c>(self, sql, stream=None, types=None, **params)</c>, so a
    /// declared parameter sharing one of those names collides — in one of two ways,
    /// verified against the real driver in a container:
    /// <list type="bullet">
    /// <item><c>self</c>/<c>sql</c> are already bound positionally, so they raise
    /// <c>TypeError: got multiple values for argument …</c> inside the container,
    /// attributed to the bundle's query.</item>
    /// <item><c>stream</c>/<c>types</c> raise nothing: the value is consumed as a
    /// driver option and never reaches <c>**params</c>, so the query runs with its
    /// placeholder unbound. Silent, and the worse of the two.</item>
    /// </list>
    ///
    /// <b>What this test does and does not prove.</b> It is a C#-side test of a
    /// C#-side list: it shows the list is <i>enforced</i> before any container
    /// starts. It cannot show the list is <i>complete</i> — it stayed green while
    /// <c>self</c> was missing from it. Completeness is pinned against the driver's
    /// actual signature, not here.
    ///
    /// Caught here rather than in the shared <c>DeclaredParameterFilter</c>: the
    /// collision is a property of *this* transport. A Script profile passes its
    /// values as JSON in an environment variable and has no such reserved names,
    /// so rejecting them globally would refuse a perfectly good bundle on the
    /// other path.
    /// </summary>
    [Theory]
    [InlineData("self")]
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
