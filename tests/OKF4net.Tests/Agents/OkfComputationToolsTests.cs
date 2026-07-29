// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OKF4net.Agents;
using OKF4net.Attestation;
using OKF4net.Tests.Attestation;
using Xunit;

namespace OKF4net.Tests.Agents;

public class OkfComputationToolsTests
{
    [Fact]
    public void Get_computation_returns_contract_and_inline_code()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\n---\n# Computation\n\n```sql\nSELECT 1\n```\n");
        var tools = new OkfBundleTools(tmp.Path);
        var s = tools.GetComputation("c/rev");
        Assert.Contains("bigquery", s);
        Assert.Contains("SELECT 1", s);
    }

    /// <summary>
    /// Regression test: <see cref="OkfBundleTools.ReadConcept"/>'s Attested-Computation
    /// enrichment must not advertise <c>okf_run_computation</c> when no orchestrator is
    /// wired -- <see cref="OkfBundleTools.GetTools"/> only exposes that tool when
    /// <c>_orchestrator</c> is non-null (as the shipped <c>okf-mcp</c> server never wires
    /// one), so mentioning it unconditionally would tell a consumer to call a tool that
    /// isn't in their tool list.
    /// </summary>
    [Fact]
    public void Read_concept_mentions_only_get_computation_without_orchestrator()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\n---\n# Computation\n\n```sql\nSELECT 1\n```\n");
        var tools = new OkfBundleTools(tmp.Path);
        var s = tools.ReadConcept("c/rev");
        Assert.Contains("okf_get_computation", s);
        Assert.DoesNotContain("okf_run_computation", s);
    }

    /// <summary>
    /// Counterpart to <see cref="Read_concept_mentions_only_get_computation_without_orchestrator"/>:
    /// with an orchestrator wired, <c>okf_run_computation</c> IS in the tool list, so
    /// <see cref="OkfBundleTools.ReadConcept"/>'s enrichment should mention both tools.
    /// </summary>
    [Fact]
    public void Read_concept_mentions_both_tools_with_orchestrator_wired()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\n---\n# Computation\n\n```sql\nSELECT 1\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing()
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        var s = tools.ReadConcept("c/rev");
        Assert.Contains("okf_get_computation", s);
        Assert.Contains("okf_run_computation", s);
    }

    [Fact]
    public void Run_computation_tool_absent_without_orchestrator()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\n---\n");
        var names = new OkfBundleTools(tmp.Path).GetTools().Select(t => t.Name).ToList();
        Assert.Contains("okf_get_computation", names);
        Assert.DoesNotContain("okf_run_computation", names);
    }

    [Fact]
    public async Task Run_computation_invokes_orchestrator_when_wired()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }))
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        Assert.Contains("okf_run_computation", tools.GetTools().Select(t => t.Name));
        var s = await Task.FromResult(tools.RunComputation("c/rev", new Dictionary<string, object?>()));
        Assert.Contains("displayable", s.ToLowerInvariant());
    }

    /// <summary>
    /// A reflection/AIFunction-bound call can pass CLR <see langword="null"/>
    /// for <c>parameterValues</c> despite its non-nullable static type (e.g. a
    /// host/LLM that omits the property entirely). Without a guard,
    /// <see cref="AttestationOrchestrator.RunAsync"/>'s own required-parameter
    /// gate (<c>parameterValues.ContainsKey(...)</c>) would throw a
    /// <see cref="NullReferenceException"/> that <c>RunTool</c>'s catch filter
    /// does not cover -- breaking the "tools never throw toward the LLM"
    /// invariant. This concept declares a required parameter so the gate is
    /// actually exercised; the run must degrade to a non-displayable outcome
    /// (missing required parameter), never an unhandled exception.
    /// </summary>
    [Fact]
    public void Run_computation_with_null_parameterValues_does_not_throw()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nparameters:\n  - name: threshold\n    required: true\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }))
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));

        var s = tools.RunComputation("c/rev", null!);

        var lower = s.ToLowerInvariant();
        Assert.Contains("displayable: no", lower);
        Assert.Contains("missing required parameter", lower);
    }
}
