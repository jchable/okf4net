// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class ContainerAttestationRuntimeTests
{
    [Fact]
    public void A_Script_profile_wires_ScriptComputationExecutor()
    {
        var runtime = new ContainerAttestationRuntime(new FakeContainerEngine(), new ContainerRuntimeProfile { Image = "python:3.12-slim", Kind = ContainerRuntimeKind.Script });
        Assert.IsType<ScriptComputationExecutor>(runtime.Executor);
        Assert.IsType<AllowlistParameterBinder>(runtime.Binder);
        Assert.IsType<ContainerAttester>(runtime.Attester);
    }

    [Fact]
    public void A_SqlClient_profile_wires_SqlClientComputationExecutor()
    {
        var runtime = new ContainerAttestationRuntime(new FakeContainerEngine(), new ContainerRuntimeProfile { Image = "postgres:16-alpine", Kind = ContainerRuntimeKind.SqlClient });
        Assert.IsType<SqlClientComputationExecutor>(runtime.Executor);
    }

    [Fact]
    public async Task The_attester_uses_its_own_fixed_image_even_when_the_profile_image_has_no_python()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(0, """{"ok": true}""", "") };
        // A SqlClient profile's image (postgres:16-alpine) has no Python at
        // all -- if ContainerAttestationRuntime ever threaded profile.Image
        // into the attester instead of ContainerAttesterOptions's own
        // default, this would be the regression that proves it: the
        // attester run would target an image the assertion below shows it
        // did NOT use.
        var runtime = new ContainerAttestationRuntime(engine, new ContainerRuntimeProfile { Image = "postgres:16-alpine", Kind = ContainerRuntimeKind.SqlClient });

        var context = new AttestationContext(
            Contract: new AttestedComputationContract("postgres", [], null, null, new Attester("a.py")),
            Computation: new SanctionedComputation(ComputationSource.Inline, "SELECT 1", null),
            Bound: new BoundComputation("postgres", "SELECT 1", null, new Dictionary<string, object?>()),
            Values: new Dictionary<string, object?>(),
            Receipt: new Receipt(new Dictionary<string, object?>()),
            AttesterSourceText: "def attest(**_):\n    return {}\n");

        await runtime.Attester.AttestAsync(context);

        Assert.Equal("python:3.12-slim", engine.LastSpec!.Image);
        Assert.NotEqual("postgres:16-alpine", engine.LastSpec.Image);
    }

    [Fact]
    public async Task Runs_end_to_end_through_AttestationOrchestrator_with_a_fake_engine()
    {
        using var tmp = new OKF4net.Tests.TempDir();
        tmp.Write("c/greet.py", "def attest(*, sanctioned_computation, receipt, values):\n    return {'ok': receipt.get('message') == f\"Hello, {values['name']}!\"}\n");
        tmp.Write("c/greet.md",
            "---\ntype: Attested Computation\nruntime: python\n" +
            "parameters:\n  - { name: name, type: string, required: true }\n" +
            "executor: { receipt: [message] }\n" +
            "attester: { resource: greet.py }\n---\n# Computation\n\n```python\nprint()\n```\n");
        var bundle = OKF4net.Bundle.Load(tmp.Path);

        var engine = new FakeContainerEngine
        {
            Respond = spec => spec.Command[0] == "python3" && spec.Stdin!.Contains("attester_source")
                ? new ContainerRunResult(0, """{"ok": true, "reason": null}""", "")
                : new ContainerRunResult(0, """{"message": "Hello, Ada!"}""", ""),
        };
        var runtime = new ContainerAttestationRuntime(engine, new ContainerRuntimeProfile { Image = "python:3.12-slim", Kind = ContainerRuntimeKind.Script });
        var registry = new OKF4net.Attestation.AttestationRuntimeRegistry(new Dictionary<string, OKF4net.Attestation.IAttestationRuntime> { ["python"] = runtime });
        var orchestrator = new OKF4net.Attestation.AttestationOrchestrator(registry);

        var outcome = await orchestrator.RunAsync(bundle, OKF4net.ConceptId.Parse("c/greet"), new Dictionary<string, object?> { ["name"] = "Ada" });

        Assert.True(outcome.Displayable);
        Assert.Equal("Hello, Ada!", outcome.Receipt!.Fields["message"]);
    }
}
