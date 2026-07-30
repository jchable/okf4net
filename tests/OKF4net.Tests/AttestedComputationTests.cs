// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Yaml;
using Xunit;

namespace OKF4net.Tests;

public class AttestedComputationTests
{
    private static Frontmatter Parse(string yaml) =>
        Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(yaml));

    [Fact]
    public void Projects_full_contract_from_frontmatter()
    {
        var fm = Parse(
            "type: Attested Computation\n" +
            "runtime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "computation: references/computations/revenue.sql\n" +
            "executor:\n  resource: references/skills/run-on-bq.md\n  receipt: [job_id, executed_sql, result]\n" +
            "attester:\n  resource: references/attesters/revenue.py\n");

        Assert.True(fm.IsAttestedComputation);
        var c = fm.ComputationContract;
        Assert.Equal("bigquery", c.Runtime);
        var p = Assert.Single(c.Parameters);
        Assert.Equal("year", p.Name);
        Assert.Equal("integer", p.Type);
        Assert.True(p.Required);
        Assert.Equal("references/computations/revenue.sql", c.ComputationPath);
        Assert.Equal("references/skills/run-on-bq.md", c.Executor!.Value.Resource);
        Assert.Equal(new[] { "job_id", "executed_sql", "result" }, c.Executor!.Value.Receipt);
        Assert.Equal("references/attesters/revenue.py", c.Attester!.Value.Resource);
    }

    [Fact]
    public void Non_computation_type_is_not_attested_and_projects_empty()
    {
        var fm = Parse("type: Metric\ntitle: Revenue\n");
        Assert.False(fm.IsAttestedComputation);
        Assert.Null(fm.ComputationContract.Runtime);
        Assert.Empty(fm.ComputationContract.Parameters);
    }

    [Fact]
    public void Malformed_fields_never_throw_and_degrade()
    {
        // runtime absent ; parameters entrée sans name ; executor.receipt non-liste
        var fm = Parse(
            "type: Attested Computation\n" +
            "parameters:\n  - { type: integer }\n" +
            "executor:\n  receipt: nope\n");
        var c = fm.ComputationContract;              // ne throw pas
        Assert.Null(c.Runtime);
        Assert.Equal(string.Empty, c.Parameters[0].Name);   // name absent → ""
        Assert.Empty(c.Executor!.Value.Receipt);            // receipt non-liste → []
    }

    [Fact]
    public void Parameters_entry_that_is_not_a_mapping_is_silently_skipped()
    {
        // A `parameters` sequence entry that is not a YAML mapping (here a
        // bare scalar) is dropped outright rather than throwing or surfacing
        // as some degraded placeholder entry (§3 permissive loading) -- only
        // the well-formed entry that follows it survives the projection.
        var fm = Parse(
            "type: Attested Computation\n" +
            "parameters:\n  - 5\n  - { name: year, type: integer, required: true }\n");

        var p = Assert.Single(fm.ComputationContract.Parameters);
        Assert.Equal("year", p.Name);
    }
}
