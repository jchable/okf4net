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

    /// <summary>
    /// The non-regression case the design names, and the reason this whole design
    /// exists: the bound text must be the sanctioned text byte-for-byte
    /// <i>whatever the values</i>. <c>Never_touches_the_bound_text</c> above cannot
    /// show that — its computation is <c>print('hi')</c>, which contains no
    /// placeholder, so a binder that substituted values would leave it identical
    /// and the test would stay green.
    ///
    /// These cases make substitution observable. Each computation carries a
    /// placeholder in a different dialect's spelling, and each value is chosen so
    /// that interpolating it would visibly change the text — including the
    /// injection-shaped one, where substitution would both alter the text and
    /// change what the database executes. The binder's contract is to carry the
    /// text through untouched and hand the values to the driver separately; round
    /// 1 of this plan got that wrong, which is what the design is guarding.
    /// </summary>
    [Theory]
    // BigQuery/@-style, dbt-style, and PEP 249 named style.
    [InlineData("SELECT SUM(amount) FROM t WHERE fiscal_year = @year", "year", 2026)]
    [InlineData("SELECT * FROM users WHERE id >= :min_id", "min_id", 7)]
    [InlineData("SELECT {{ var('window') }} FROM t", "window", 30)]
    // A value that would be unmistakable if it were ever interpolated.
    [InlineData("SELECT * FROM users WHERE id >= :min_id", "min_id", "1; DROP TABLE users --")]
    public async Task Bound_text_is_the_sanctioned_text_whatever_the_values(string sanctioned, string parameter, object value)
    {
        var contract = new AttestedComputationContract(
            Runtime: "postgres",
            Parameters: [new ComputationParameter(parameter, value is string ? "string" : "integer", Required: true)],
            ComputationPath: null,
            Executor: null,
            Attester: null);
        var computation = new SanctionedComputation(ComputationSource.Inline, sanctioned, null);

        var binder = new AllowlistParameterBinder();
        var bound = await binder.BindAsync(contract, computation, new Dictionary<string, object?> { [parameter] = value });

        // Byte-for-byte, not merely "still contains the placeholder".
        Assert.Equal(sanctioned, bound.BoundText);

        // And the value did travel -- separately, for the driver to bind. A binder
        // that dropped values entirely would also satisfy the assertion above.
        Assert.Equal(value, bound.Values[parameter]);
    }
}
