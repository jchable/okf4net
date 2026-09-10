// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class AllowlistParameterBinderTests
{
    private static readonly AttestedComputationContract Contract = new(
        Runtime: "python",
        Parameters: [new ComputationParameter("name", "string", Required: true)],
        ComputationPath: null,
        Executor: null,
        Attester: null);

    private static readonly SanctionedComputation Computation = new(ComputationSource.Inline, "print('hi')", null);

    [Fact]
    public async Task Never_touches_the_bound_text()
    {
        var binder = new AllowlistParameterBinder();
        var bound = await binder.BindAsync(Contract, Computation, new Dictionary<string, object?> { ["name"] = "Ada" });
        Assert.Equal("print('hi')", bound.BoundText);
    }

    [Fact]
    public async Task Drops_undeclared_keys()
    {
        var binder = new AllowlistParameterBinder();
        var bound = await binder.BindAsync(Contract, Computation, new Dictionary<string, object?> { ["name"] = "Ada", ["extra"] = "should not survive" });
        Assert.True(bound.Values.ContainsKey("name"));
        Assert.False(bound.Values.ContainsKey("extra"));
    }

    [Fact]
    public async Task Rejects_a_declared_value_of_the_wrong_CLR_type()
    {
        var binder = new AllowlistParameterBinder();
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await binder.BindAsync(Contract, Computation, new Dictionary<string, object?> { ["name"] = 42 }));
    }
}
