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
}
