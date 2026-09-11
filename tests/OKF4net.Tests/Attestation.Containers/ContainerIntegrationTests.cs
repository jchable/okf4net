// SPDX-License-Identifier: LGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
/// and set <c>OKF_DEMO_PG_CONN=postgresql://postgres:demo@host.docker.internal:5544/demo</c>
/// before running these tests -- <c>host.docker.internal</c>, not
/// <c>localhost</c>: the connection string is read by the .NET test process
/// on the host, but consumed *inside* the SqlClient container this test
/// spins up, where <c>localhost</c> means that container's own loopback,
/// not the host's. On native Linux Docker Engine (not Docker Desktop) this
/// hostname may need the daemon started with <c>--add-host=host.docker.internal:host-gateway</c>
/// support, or substitute the host's real LAN/bridge IP instead. Stop the
/// fixture afterwards with <c>docker stop okf-demo-pg</c>.
///
/// The <c>meridian_transit</c> case needs that same fixture to also carry the
/// bundle's own schema, which is checked in as the bundle's source of truth:
/// <c>docker exec -i okf-demo-pg psql -U postgres -d demo &lt; bundles/meridian_transit/references/schema.sql</c>.
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
        tmp.Write("greet.py", "def attest(*, sanctioned_computation, receipt, values):\n    return {'ok': receipt.get('message') == f\"Hello, {values['name']}!\"}\n");
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

    /// <summary>
    /// Proves <c>--read-only</c> actually reaches the container, rather than being a
    /// flag we emit and nobody checks. The other tests here pass whether or not the
    /// root filesystem is writable, so none of them can tell.
    ///
    /// Two halves, and both are needed: a write OUTSIDE the tmpfs must fail, and a
    /// write INSIDE it must succeed. The first alone would also pass if the image
    /// simply had no <c>/root</c>; the second alone would pass with no hardening at
    /// all. Together they pin exactly the boundary the profile draws.
    /// </summary>
    [SkippableFact]
    public async Task Read_only_root_blocks_a_write_outside_the_tmpfs_and_allows_one_inside()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        var engine = new CliContainerEngine();
        var profile = new ContainerRuntimeProfile { Image = "python:3.12-slim", Kind = ContainerRuntimeKind.Script };

        // Sanity: the profile is hardened by default. If this ever flips, the rest of
        // this test would quietly stop testing anything.
        Assert.True(profile.ReadOnlyRootFilesystem);
        Assert.Contains("/tmp", profile.TmpfsMounts);

        var executor = new ScriptComputationExecutor(engine, profile);
        var contract = new AttestedComputationContract("python", [], null, null, null);

        var probe = """
            import json
            outside = None
            try:
                open('/root/okf-probe', 'w').write('x')
                outside = 'written'
            except OSError as e:
                outside = type(e).__name__
            open('/tmp/okf-probe', 'w').write('x')
            print(json.dumps({'outside': outside, 'inside': 'written'}))
            """;

        var receipt = await executor.ExecuteAsync(
            new BoundComputation("python", probe, null, new Dictionary<string, object?>()), contract);

        Assert.Equal("OSError", receipt.Fields["outside"]);
        Assert.Equal("written", receipt.Fields["inside"]);
    }

    /// <summary>
    /// Runs <c>bundles/meridian_transit</c> — a checked-in bundle, not a fixture this
    /// test authors — end to end through both runtimes at once, which is the thing no
    /// other test here does. Everything else builds a one-concept bundle in a TempDir
    /// shaped to suit the assertion; this one takes the bundle as it ships and has to
    /// live with it, including its bare `attester.resource` paths resolving from the
    /// bundle root under §6.2.
    ///
    /// The two halves are deliberately unequal, and that is the point of the bundle:
    /// <list type="bullet">
    /// <item><c>capped-fare</c> runs on the <c>Script</c> runtime with the network off,
    /// and its attester <b>recomputes</b> the fare-capping policy from the run's own
    /// inputs. A pass there is evidence about the number.</item>
    /// <item><c>daily-ridership</c> runs on <c>SqlClient</c> against a real Postgres,
    /// and its attester can only check invariants of the query's shape — because
    /// <c>executed_sql</c> is echoed by this host's own wrapper.</item>
    /// </list>
    /// </summary>
    [SkippableFact]
    public async Task Meridian_transit_bundle_runs_both_runtimes_end_to_end()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");
        var conn = Environment.GetEnvironmentVariable("OKF_DEMO_PG_CONN");
        Skip.If(string.IsNullOrEmpty(conn), "OKF_DEMO_PG_CONN is not set -- see this class's doc comment for setup");

        var bundle = Bundle.Load(Path.Combine(TestPaths.RepoRoot(), "bundles", "meridian_transit"));
        var engine = new CliContainerEngine();

        var registry = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["python"] = new ContainerAttestationRuntime(
                engine,
                new ContainerRuntimeProfile { Image = "python:3.12-slim", Kind = ContainerRuntimeKind.Script }),
            ["postgres"] = new ContainerAttestationRuntime(
                engine,
                new ContainerRuntimeProfile
                {
                    Image = "python:3.12-slim",
                    Kind = ContainerRuntimeKind.SqlClient,
                    Environment = new Dictionary<string, string> { ["OKF_CONN"] = conn! },
                }),
        });
        var orchestrator = new AttestationOrchestrator(registry);

        // The Script half. The worked example in policies/fare-capping.md: four 250
        // fares against a 700 cap charge 700 and waive 300, split [250, 250, 200, 0].
        var capped = await orchestrator.RunAsync(
            bundle,
            ConceptId.Parse("computations/capped-fare"),
            new Dictionary<string, object?> { ["fares_cents"] = "[250,250,250,250]", ["cap_cents"] = 700 });

        Assert.True(capped.Displayable, string.Join("; ", capped.Reasons));
        Assert.Equal(700, Convert.ToInt32(capped.Receipt!.Fields["charged_cents"], CultureInfo.InvariantCulture));
        Assert.Equal(300, Convert.ToInt32(capped.Receipt.Fields["waived_cents"], CultureInfo.InvariantCulture));

        // The SqlClient half, against the seed data in references/schema.sql: on
        // 2026-09-10 five trips completed across two distinct riders — the two
        // abandoned trips and the neighbouring service date must both be excluded, so
        // a query ignoring `status` or `service_date` fails here rather than passing
        // by coincidence.
        var ridership = await orchestrator.RunAsync(
            bundle,
            ConceptId.Parse("computations/daily-ridership"),
            new Dictionary<string, object?> { ["service_date"] = "2026-09-10" });

        Assert.True(ridership.Displayable, string.Join("; ", ridership.Reasons));
        var row = Assert.IsAssignableFrom<System.Collections.IEnumerable>(ridership.Receipt!.Fields["result"])
            .Cast<object>()
            .Single();
        var json = System.Text.Json.JsonSerializer.Serialize(row);
        Assert.Contains("\"completed_trips\":5", json, StringComparison.Ordinal);
        Assert.Contains("\"distinct_riders\":2", json, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task SqlClient_runtime_runs_a_real_postgres_query()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");
        var conn = Environment.GetEnvironmentVariable("OKF_DEMO_PG_CONN");
        Skip.If(string.IsNullOrEmpty(conn), "OKF_DEMO_PG_CONN is not set -- see this class's doc comment for setup");

        using var tmp = new TempDir();
        tmp.Write("count.py",
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

    /// <summary>
    /// The two container-side defects the original real-Docker run could not surface,
    /// pinned here because only a real database can produce them.
    ///
    /// <para><b>Non-JSON-native column types.</b> The other SqlClient test selects
    /// <c>count(*)</c>, a plain integer, so it never asked <c>json.dumps</c> to encode
    /// anything it cannot. A <c>NUMERIC</c> comes back as <c>Decimal</c>, a <c>DATE</c>
    /// as <c>date</c>, a <c>UUID</c> as <c>UUID</c> — each raising <c>TypeError</c>
    /// AFTER the query has already run, losing the receipt and reporting a bare "SQL
    /// wrapper exited with code 1" for a query that in fact succeeded. This selects all
    /// three plus a <c>BYTEA</c> and requires the receipt to come back intact.</para>
    ///
    /// <para><b>What this test does NOT prove.</b> It guards the encoding defect
    /// above and that one only. It does <i>not</i> guard the wrapper's
    /// <c>stdout=subprocess.DEVNULL</c> redirect: removing that leaves this test
    /// green, verified by reverting it and running this test against real Docker.
    /// On <c>python:3.12-slim</c> pip's warnings and notices go to stderr, so under
    /// <c>--quiet</c> nothing reaches the receipt channel anyway. The redirect is
    /// structural insurance against an image or a pip version where that is not
    /// true — a case no test here can reach, since it needs an image whose pip
    /// prints to stdout. Keep the redirect; do not read a green run as evidence
    /// that dropping it is safe.</para>
    ///
    /// Needs the same Postgres fixture as the test above, plus one extra table:
    /// <c>docker exec -i okf-demo-pg psql -U postgres -d demo -c "CREATE TABLE typed(amount numeric(12,2), booked date, ref uuid, blob bytea); INSERT INTO typed VALUES (1234.56, '2026-09-11', '00000000-0000-0000-0000-000000000001', '\\x4f4b46');"</c>
    /// </summary>
    [SkippableFact]
    public async Task SqlClient_runtime_returns_a_receipt_for_non_json_native_column_types()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");
        var conn = Environment.GetEnvironmentVariable("OKF_DEMO_PG_CONN");
        Skip.If(string.IsNullOrEmpty(conn), "OKF_DEMO_PG_CONN is not set -- see this class's doc comment for setup");

        using var tmp = new TempDir();
        tmp.Write("typed.py",
            "def attest(*, sanctioned_computation, receipt, values):\n" +
            "    rows = receipt.get('result') or []\n" +
            "    return {'ok': len(rows) == 1 and all(k in rows[0] for k in ('amount', 'booked', 'ref', 'blob'))}\n");
        tmp.Write("c/typed.md",
            "---\ntype: Attested Computation\nruntime: postgres\n" +
            "executor: { receipt: [executed_sql, result] }\n" +
            "attester: { resource: typed.py }\n---\n" +
            "# Computation\n\n```sql\nSELECT amount, booked, ref, blob FROM typed\n```\n");
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

        var outcome = await orchestrator.RunAsync(bundle, ConceptId.Parse("c/typed"), new Dictionary<string, object?>());

        // A TypeError inside the wrapper, or pip output on the receipt channel, both land
        // here as a non-displayable outcome -- so the reasons are worth printing.
        Assert.True(outcome.Displayable, string.Join("; ", outcome.Reasons));
        Assert.NotNull(outcome.Receipt);

        // The receipt parsed, which is the #3 guarantee: nothing but JSON reached stdout.
        Assert.True(outcome.ReceiptShapeOk);

        // And it carries the values, which is the #2 guarantee: the Decimal/date/UUID/bytes
        // were encoded (as their str() form) instead of aborting the dump.
        var rows = Assert.IsAssignableFrom<System.Collections.IEnumerable>(outcome.Receipt!.Fields["result"]);
        Assert.Contains("1234.56", System.Text.Json.JsonSerializer.Serialize(rows));
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

        // The name of this test claims the container is killed, so observe that
        // rather than trusting the exception to imply it. Without this the test
        // passed just as happily on an engine that abandoned a `python3 -c
        // "sleep(30)"` container to run for another 30 seconds, which is the whole
        // failure it exists to catch. Containers are named `okf-<guid>` and run with
        // --rm, so none should survive; removal is asynchronous, hence the bounded
        // wait rather than a bare assertion.
        Assert.True(
            await NoEngineContainersWithin(TimeSpan.FromSeconds(15)),
            $"a run container outlived its cancellation: {string.Join(", ", EngineContainers())}");
    }

    /// <summary>
    /// <see cref="CliContainerEngine"/> names each run <c>okf-{Guid:N}</c>, so a
    /// leftover is a name of exactly that shape. Matching a bare <c>okf-</c> prefix
    /// instead would also catch <c>okf-demo-pg</c> — the Postgres fixture this class's
    /// own doc comment tells you to start — and report the fixture as a leak on every
    /// run that uses it.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex EngineContainerName =
        new("^okf-[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string[] EngineContainers() =>
        RunDocker("ps -a --filter name=okf- --format {{.Names}}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => EngineContainerName.IsMatch(name))
            .ToArray();

    private static async Task<bool> NoEngineContainersWithin(TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (EngineContainers().Length == 0)
            {
                return true;
            }

            await Task.Delay(500);
        }

        return EngineContainers().Length == 0;
    }

    private static string RunDocker(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo("docker", arguments) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(10_000);
        return output.Trim();
    }
}
