// SPDX-License-Identifier: LGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

/// <summary>
/// Exercises Tasks 1-9 against a REAL container engine. Excluded from CI by
/// decision (mirrors <c>producers/</c>, see <c>CLAUDE.md</c>) — run manually
/// with:
/// <c>dotnet test tests/OKF4net.Tests --filter Category=ContainerIntegration</c>.
///
/// The SqlClient case needs a reachable Postgres instance. Start one first:
/// <c>docker run --rm -d --name okf-demo-pg -e POSTGRES_PASSWORD=demo -e POSTGRES_DB=demo -p 5544:5432 postgres:16-alpine</c>,
/// then seed it: <c>docker exec -i okf-demo-pg psql -U postgres -d demo -c "CREATE TABLE users(id int, active boolean); INSERT INTO users VALUES (1,true),(2,true),(3,false);"</c>,
/// and set <c>OKF_DEMO_PG_CONN=postgresql://postgres:demo@localhost:5544/demo</c>
/// before running these tests. Stop it afterwards with
/// <c>docker stop okf-demo-pg</c>.
/// </summary>
[Trait("Category", "ContainerIntegration")]
public class ContainerIntegrationTests
{
    private static bool DockerAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("docker", "--version") { RedirectStandardOutput = true });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [SkippableFact]
    public async Task Script_runtime_runs_a_real_python_container()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        using var tmp = new TempDir();
        tmp.Write("c/greet.py", "def attest(*, sanctioned_computation, receipt, values):\n    return {'ok': receipt.get('message') == f\"Hello, {values['name']}!\"}\n");
        tmp.Write("c/greet.md",
            "---\ntype: Attested Computation\nruntime: python\n" +
            "parameters:\n  - { name: name, type: string, required: true }\n" +
            "executor: { receipt: [message] }\n" +
            "attester: { resource: greet.py }\n---\n" +
            "# Computation\n\n```python\nimport json, os\nparams = json.loads(os.environ['OKF_PARAMS_JSON'])\nprint(json.dumps({'message': f\"Hello, {params['name']}!\"}))\n```\n");
        var bundle = Bundle.Load(tmp.Path);

        var engine = new CliContainerEngine();
        var runtime = new ContainerAttestationRuntime(engine, new ContainerRuntimeProfile { Image = "python:3.12-slim", Kind = ContainerRuntimeKind.Script });
        var registry = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["python"] = runtime });
        var orchestrator = new AttestationOrchestrator(registry);

        var outcome = await orchestrator.RunAsync(bundle, ConceptId.Parse("c/greet"), new Dictionary<string, object?> { ["name"] = "Ada" });

        Assert.True(outcome.Displayable, string.Join("; ", outcome.Reasons));
        Assert.Equal("Hello, Ada!", outcome.Receipt!.Fields["message"]);
    }

    [SkippableFact]
    public async Task SqlClient_runtime_runs_a_real_postgres_query()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");
        var conn = Environment.GetEnvironmentVariable("OKF_DEMO_PG_CONN");
        Skip.If(string.IsNullOrEmpty(conn), "OKF_DEMO_PG_CONN is not set -- see this class's doc comment for setup");

        using var tmp = new TempDir();
        tmp.Write("c/count.py",
            "def attest(*, sanctioned_computation, receipt, values):\n" +
            "    executed = receipt.get('executed_sql')\n" +
            "    return {'ok': executed is not None and executed.strip() == sanctioned_computation.strip()}\n");
        tmp.Write("c/count.md",
            "---\ntype: Attested Computation\nruntime: postgres\n" +
            "parameters:\n  - { name: min_id, type: integer, required: true }\n" +
            "executor: { receipt: [executed_sql, result] }\n" +
            "attester: { resource: count.py }\n---\n" +
            "# Computation\n\n```sql\nSELECT count(*) AS active_users FROM users WHERE active = true AND id >= :min_id\n```\n");
        var bundle = Bundle.Load(tmp.Path);

        var engine = new CliContainerEngine();
        var profile = new ContainerRuntimeProfile
        {
            Image = "python:3.12-slim",
            Kind = ContainerRuntimeKind.SqlClient,
            Environment = new Dictionary<string, string> { ["OKF_CONN"] = conn! },
        };
        var runtime = new ContainerAttestationRuntime(engine, profile);
        var registry = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["postgres"] = runtime });
        var orchestrator = new AttestationOrchestrator(registry);

        var outcome = await orchestrator.RunAsync(bundle, ConceptId.Parse("c/count"), new Dictionary<string, object?> { ["min_id"] = 1 });

        Assert.True(outcome.Displayable, string.Join("; ", outcome.Reasons));
    }

    [SkippableFact]
    public async Task Cancellation_kills_the_container_and_propagates_as_OperationCanceledException()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        var engine = new CliContainerEngine();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        var spec = new ContainerRunSpec(
            Image: "python:3.12-slim",
            Command: ["python3", "-c", "import time; time.sleep(30)"],
            Stdin: null,
            Environment: new Dictionary<string, string>(),
            NetworkMode: "none",
            MemoryBytes: 128 * 1024 * 1024,
            Cpus: 0.5,
            PidsLimit: 16,
            Timeout: TimeSpan.FromSeconds(60));

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await engine.RunAsync(spec, cts.Token));
    }
}
