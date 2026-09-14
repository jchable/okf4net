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
/// plus the extra table <c>SqlClient_runtime_returns_a_receipt_for_non_json_native_column_types</c>
/// needs: <c>docker exec -i okf-demo-pg psql -U postgres -d demo -c "CREATE TABLE typed(amount numeric(12,2), booked date, ref uuid, blob bytea); INSERT INTO typed VALUES (1234.56, '2026-09-11', '00000000-0000-0000-0000-000000000001', '\\x4f4b46');"</c>,
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

    /// <summary>
    /// The failure message for an outcome that should have been displayable. Its
    /// <c>Reasons</c> carry at most the exception's library-authored message, by design
    /// (they reach the model); the exception on <c>Error</c> carries the container's
    /// stderr, which is what says why.
    /// </summary>
    private static string Why(AttestationOutcome outcome) =>
        string.Join("; ", outcome.Reasons) + (outcome.Error is null ? "" : "\n" + outcome.Error);

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

        Assert.True(outcome.Displayable, Why(outcome));
        Assert.Equal("Hello, Ada!", outcome.Receipt!.Fields["message"]);
    }

    /// <summary>
    /// Proves <c>--read-only</c> actually reaches the container, rather than being a
    /// flag we emit and nobody checks. The other tests here pass whether or not the
    /// root filesystem is writable, so none of them can tell.
    ///
    /// Two halves, and both are needed: a write OUTSIDE the tmpfs must fail, and a
    /// write INSIDE it must succeed. The first alone would also pass if the image
    /// simply had no writable directory at all; the second alone would pass with no
    /// hardening at all. Together they pin exactly the boundary the profile draws.
    ///
    /// The outside probe targets <c>/var/tmp</c>, not <c>/root</c>: the profile now
    /// also runs as uid 65534 by default (see <see cref="ContainerIsolation.User"/>),
    /// and <c>/root</c> is <c>0700</c> root-owned, so a non-root writer is refused by
    /// ordinary Unix permissions (<c>PermissionError</c>) before the read-only mount
    /// is ever consulted -- which would prove the wrong thing here. <c>/var/tmp</c> is
    /// world-writable (<c>1777</c>) on this image, so ordinary permissions let the
    /// write through and it is the read-only root itself that then refuses it with a
    /// bare <c>OSError</c> (no more specific subclass -- Python has none for EROFS).
    /// </summary>
    [SkippableFact]
    public async Task Read_only_root_blocks_a_write_outside_the_tmpfs_and_allows_one_inside()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        var engine = new CliContainerEngine();
        var profile = new ContainerRuntimeProfile { Image = "python:3.12-slim", Kind = ContainerRuntimeKind.Script };

        // Sanity: the profile is hardened by default. If this ever flips, the rest of
        // this test would quietly stop testing anything.
        Assert.True(profile.Isolation.ReadOnlyRootFilesystem);
        Assert.Contains("/tmp", profile.Isolation.TmpfsMounts);

        var executor = new ScriptComputationExecutor(engine, profile);
        var contract = new AttestedComputationContract("python", [], null, null, null);

        var probe = """
            import json
            outside = None
            try:
                open('/var/tmp/okf-probe', 'w').write('x')
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
    /// <c>Isolation.TmpfsMounts</c> is documented as configurable, and for a long time was only
    /// half so: the engine mounted whatever the host named, while the Python inside the
    /// containers kept writing to <c>/tmp</c>. With <c>["/scratch"]</c> under the default
    /// read-only root, <c>/tmp</c> is read-only and the attester bootstrap's
    /// <c>NamedTemporaryFile</c> failed every run with "No usable temporary directory".
    ///
    /// This runs both stages of a Script concept with <c>/scratch</c> and no <c>/tmp</c>
    /// mount. The computation probes the boundary it runs under — a write to <c>/tmp</c>
    /// must fail, a <c>tempfile</c> write must land in <c>/scratch</c> — so the test
    /// cannot pass by accident on a configuration where <c>/tmp</c> is writable after
    /// all. The attester checks the same from its own container, and merely reaching it
    /// proves the bootstrap found somewhere to write its module.
    /// </summary>
    [SkippableFact]
    public async Task A_custom_tmpfs_mount_with_no_tmp_is_honoured_by_the_script_executor_and_the_attester()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        using var tmp = new TempDir();
        tmp.Write("probe.py",
            "import tempfile\n" +
            "def attest(*, sanctioned_computation, receipt, values):\n" +
            "    try:\n" +
            "        open('/tmp/okf-attester-probe', 'w').write('x')\n" +
            "        own_tmp = 'written'\n" +
            "    except OSError as e:\n" +
            "        own_tmp = type(e).__name__\n" +
            "    ok = (receipt.get('tmp') == 'OSError' and str(receipt.get('scratch', '')).startswith('/scratch/')\n" +
            "          and own_tmp == 'OSError' and tempfile.gettempdir() == '/scratch')\n" +
            "    return {'ok': ok, 'reason': None if ok else f'receipt={receipt!r} own_tmp={own_tmp} gettempdir={tempfile.gettempdir()}'}\n");
        tmp.Write("c/probe.md",
            "---\ntype: Attested Computation\nruntime: python\n" +
            "executor: { receipt: [tmp, scratch] }\n" +
            "attester: { resource: probe.py }\n---\n" +
            "# Computation\n\n```python\n" +
            "import json, tempfile\n" +
            "try:\n" +
            "    open('/tmp/okf-probe', 'w').write('x')\n" +
            "    tmp = 'written'\n" +
            "except OSError as e:\n" +
            "    tmp = type(e).__name__\n" +
            "with tempfile.NamedTemporaryFile() as f:\n" +
            "    f.write(b'x')\n" +
            "    scratch = f.name\n" +
            "print(json.dumps({'tmp': tmp, 'scratch': scratch}))\n" +
            "```\n");
        var bundle = Bundle.Load(tmp.Path);

        var engine = new CliContainerEngine();
        var profile = new ContainerRuntimeProfile { Image = "python:3.12-slim", Kind = ContainerRuntimeKind.Script, Isolation = new ContainerIsolation { TmpfsMounts = ["/scratch"] } };
        var attesterOptions = new ContainerAttesterOptions { Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = ["/scratch"] } };
        Assert.True(profile.Isolation.ReadOnlyRootFilesystem);
        Assert.True(attesterOptions.Isolation.ReadOnlyRootFilesystem);

        var registry = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["python"] = new ContainerAttestationRuntime(engine, profile, attesterOptions),
        });
        var outcome = await new AttestationOrchestrator(registry).RunAsync(bundle, ConceptId.Parse("c/probe"), new Dictionary<string, object?>());

        Assert.True(outcome.Displayable, Why(outcome));
        Assert.Equal("OSError", outcome.Receipt!.Fields["tmp"]);
        Assert.StartsWith("/scratch/", (string)outcome.Receipt.Fields["scratch"]!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same defect on the SqlClient path, where it was worse than the attester
    /// review first said: the wrapper's fallback install hardcoded
    /// <c>--target /tmp/okf-pkgs</c>, and pip also unpacks and builds in the temp
    /// directory, so under <c>Isolation.TmpfsMounts = ["/scratch"]</c> the driver install failed
    /// before any SQL ran. A bare <c>python:3.12-slim</c> image, so the install path is
    /// the one exercised, against the real Postgres fixture.
    /// </summary>
    [SkippableFact]
    public async Task SqlClient_runtime_installs_its_driver_into_a_custom_tmpfs_mount_with_no_tmp()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");
        var conn = Environment.GetEnvironmentVariable("OKF_DEMO_PG_CONN");
        Skip.If(string.IsNullOrEmpty(conn), "OKF_DEMO_PG_CONN is not set -- see this class's doc comment for setup");

        using var tmp = new TempDir();
        tmp.Write("count.py",
            "def attest(*, sanctioned_computation, receipt, values):\n" +
            "    return {'ok': receipt.get('result') == [{'active_users': 2}]}\n");
        tmp.Write("c/count.md",
            "---\ntype: Attested Computation\nruntime: postgres\n" +
            "executor: { receipt: [executed_sql, result] }\n" +
            "attester: { resource: count.py }\n---\n" +
            "# Computation\n\n```sql\nSELECT count(*) AS active_users FROM users WHERE active = true\n```\n");
        var bundle = Bundle.Load(tmp.Path);

        var engine = new CliContainerEngine();
        var profile = new ContainerRuntimeProfile
        {
            Image = "python:3.12-slim",
            Kind = ContainerRuntimeKind.SqlClient,
            Environment = new Dictionary<string, string> { ["OKF_CONN"] = conn! },
            Isolation = new ContainerIsolation { TmpfsMounts = ["/scratch"] },
        };
        Assert.True(profile.Isolation.ReadOnlyRootFilesystem);

        var registry = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["postgres"] = new ContainerAttestationRuntime(engine, profile, new ContainerAttesterOptions { Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = ["/scratch"] } }),
        });
        var outcome = await new AttestationOrchestrator(registry).RunAsync(bundle, ConceptId.Parse("c/count"), new Dictionary<string, object?>());

        Assert.True(outcome.Displayable, Why(outcome));
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
    /// inputs, per-trip split included. A pass there is evidence about the numbers —
    /// but this test only ever feeds it the correct split, so it cannot show the
    /// attester rejects a wrong one; that is
    /// <see cref="Meridian_fare_cap_attester_rejects_a_per_trip_split_in_the_wrong_order"/>.</item>
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

        Assert.True(capped.Displayable, Why(capped));
        Assert.Equal(700, Convert.ToInt32(capped.Receipt!.Fields["charged_cents"], CultureInfo.InvariantCulture));
        Assert.Equal(300, Convert.ToInt32(capped.Receipt.Fields["waived_cents"], CultureInfo.InvariantCulture));
        // The split is the order-dependent part of the result and what a rider's statement
        // shows, so it is asserted here too rather than left to the attester alone: this
        // test must still fail if the script and fare_cap.py ever drift wrong together.
        Assert.Equal("[250,250,200,0]", System.Text.Json.JsonSerializer.Serialize(capped.Receipt.Fields["per_trip_cents"]));

        // The SqlClient half, against the seed data in references/schema.sql: on
        // 2026-09-10 five trips completed across two distinct riders — the two
        // abandoned trips and the neighbouring service date must both be excluded, so
        // a query ignoring `status` or `service_date` fails here rather than passing
        // by coincidence.
        var ridership = await orchestrator.RunAsync(
            bundle,
            ConceptId.Parse("computations/daily-ridership"),
            new Dictionary<string, object?> { ["service_date"] = "2026-09-10" });

        Assert.True(ridership.Displayable, Why(ridership));
        var row = Assert.IsAssignableFrom<System.Collections.IEnumerable>(ridership.Receipt!.Fields["result"])
            .Cast<object>()
            .Single();
        var json = System.Text.Json.JsonSerializer.Serialize(row);
        Assert.Contains("\"completed_trips\":5", json, StringComparison.Ordinal);
        Assert.Contains("\"distinct_riders\":2", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// The guard behind the claim that <c>meridian_transit</c>'s <c>fare_cap.py</c>
    /// "recomputes the policy". The end-to-end test above cannot make that claim: it
    /// runs the sanctioned script, which always reports the correct split, so it
    /// stayed green while the attester checked only totals and would have passed a
    /// wrong statement.
    ///
    /// Here the executor is replaced by one reporting a <b>wrong</b> per-trip split —
    /// <c>[0, 250, 250, 200]</c> for four 250 fares against a 700 cap — that agrees
    /// with the policy on every order-blind property: it charges 700, waives 300,
    /// sums to the total, has one charge per trip, and keeps each charge within its
    /// own fare. Only the sequential order is wrong (the policy says
    /// <c>[250, 250, 200, 0]</c>). Everything else is real: the bundle as it ships,
    /// its attester resolved by the orchestrator under §6.2, the real
    /// <see cref="AllowlistParameterBinder"/>, and the real
    /// <see cref="ContainerAttester"/> running the script in Docker.
    ///
    /// The correct split is run first through the same wiring as a control, so a
    /// failing verdict on the wrong one cannot come from an attester that fails
    /// everything.
    /// </summary>
    [SkippableFact]
    public async Task Meridian_fare_cap_attester_rejects_a_per_trip_split_in_the_wrong_order()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        var bundle = Bundle.Load(Path.Combine(TestPaths.RepoRoot(), "bundles", "meridian_transit"));
        var engine = new CliContainerEngine();
        var binder = new AllowlistParameterBinder();
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions());

        async Task<AttestationOutcome> AttestSplit(params long[] perTrip)
        {
            var runtime = new Tests.Attestation.FakeRuntime
            {
                BindFunc = (contract, computation, values, ct) => binder.BindAsync(contract, computation, values, ct),
                ExecuteFunc = (_, _, _) => ValueTask.FromResult(new Receipt(new Dictionary<string, object?>
                {
                    ["charged_cents"] = 700L,
                    ["waived_cents"] = 300L,
                    ["per_trip_cents"] = perTrip.Cast<object?>().ToList(),
                })),
                AttestFunc = (context, ct) => attester.AttestAsync(context, ct),
            };
            var orchestrator = new AttestationOrchestrator(
                new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["python"] = runtime }));

            return await orchestrator.RunAsync(
                bundle,
                ConceptId.Parse("computations/capped-fare"),
                new Dictionary<string, object?> { ["fares_cents"] = "[250,250,250,250]", ["cap_cents"] = 700 });
        }

        var correct = await AttestSplit(250, 250, 200, 0);
        Assert.True(correct.Displayable, Why(correct));
        Assert.True(correct.Verdict?.Passed);

        var wrong = await AttestSplit(0, 250, 250, 200);
        Assert.Null(wrong.Error);
        Assert.True(wrong.ReceiptShapeOk);
        Assert.False(wrong.Displayable);
        Assert.False(wrong.Verdict?.Passed ?? true, "the attester passed a per-trip split in the wrong order");
        Assert.Contains("sequential split", wrong.Verdict!.Value.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Raised by Copilot on #98: <c>fare_cap.py</c> checked that the cap was an integer,
    /// never that it was a cap. For one 250 fare against a cap of -1, the recomputed
    /// split is <c>[0]</c>, and a receipt charging 0 and waiving 250 matches it on every
    /// check — a pass for inputs outside any fare policy's domain. A negative fare is
    /// the same kind of input; the bounds check happened to fail it, but under a reason
    /// about the receipt rather than the inputs.
    ///
    /// The receipts below are each exactly what the sanctioned computation would return
    /// for its inputs, so only the input check can fail them. A 0 cap — every trip
    /// free — is a real policy and stays a pass, which also shows the attester does
    /// not fail everything.
    /// </summary>
    [SkippableFact]
    public async Task Meridian_fare_cap_attester_rejects_negative_amounts_in_its_inputs()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        var bundle = Bundle.Load(Path.Combine(TestPaths.RepoRoot(), "bundles", "meridian_transit"));
        var engine = new CliContainerEngine();
        var binder = new AllowlistParameterBinder();
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions());

        async Task<AttestationOutcome> Attest(string fares, int cap, long charged, long waived, params long[] perTrip)
        {
            var runtime = new Tests.Attestation.FakeRuntime
            {
                BindFunc = (contract, computation, values, ct) => binder.BindAsync(contract, computation, values, ct),
                ExecuteFunc = (_, _, _) => ValueTask.FromResult(new Receipt(new Dictionary<string, object?>
                {
                    ["charged_cents"] = charged,
                    ["waived_cents"] = waived,
                    ["per_trip_cents"] = perTrip.Cast<object?>().ToList(),
                })),
                AttestFunc = (context, ct) => attester.AttestAsync(context, ct),
            };
            var orchestrator = new AttestationOrchestrator(
                new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["python"] = runtime }));

            return await orchestrator.RunAsync(
                bundle,
                ConceptId.Parse("computations/capped-fare"),
                new Dictionary<string, object?> { ["fares_cents"] = fares, ["cap_cents"] = cap });
        }

        var freeDay = await Attest("[250]", 0, 0, 250, 0);
        Assert.True(freeDay.Displayable, Why(freeDay));

        var negativeCap = await Attest("[250]", -1, 0, 250, 0);
        Assert.Null(negativeCap.Error);
        Assert.False(negativeCap.Verdict?.Passed ?? true, "the attester passed a negative cap");
        Assert.Contains("cap_cents", negativeCap.Verdict!.Value.Detail, StringComparison.Ordinal);

        var negativeFare = await Attest("[-100]", 700, 0, -100, 0);
        Assert.Null(negativeFare.Error);
        Assert.False(negativeFare.Verdict?.Passed ?? true, "the attester passed a negative fare");
        Assert.Contains("fares_cents", negativeFare.Verdict!.Value.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ridership_shape.py</c> used to read both counts through <c>int()</c> before
    /// checking anything, so a receipt carrying <c>"5"</c>, <c>2.9</c> or <c>true</c>
    /// passed — the string coerced, the float silently truncated, the boolean an
    /// <c>int</c> to Python. Each malformed receipt below is one the old attester
    /// accepted (every value also satisfies the non-negative and riders-within-trips
    /// invariants once coerced), so each fails only if the type check itself holds.
    ///
    /// Runs the bundle's shipped attester source through the real
    /// <see cref="ContainerAttester"/> bootstrap, receipt shaped as
    /// <c>ReceiptParsing</c> produces it (a list of column-keyed dictionaries). The
    /// genuine control is what keeps a red result from meaning "the script failed to
    /// load": it must pass, and with integer counts it is exactly what a real
    /// <c>count(*)</c> receipt looks like. Needs Docker only, no Postgres.
    ///
    /// <para>No integral-float case (<c>5.0</c>) here, and that is deliberate rather
    /// than an oversight: <c>JsonSerializer</c> writes a CLR <see langword="double"/>
    /// 5.0 as <c>5</c>, so through this host it reaches the script as a Python
    /// <c>int</c> and passes — verified by adding the case and watching it fail. The
    /// attester does reject a float <c>5.0</c>; this host simply cannot hand it one.</para>
    /// </summary>
    [SkippableFact]
    public async Task Meridian_ridership_attester_rejects_counts_that_are_not_genuine_integers()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "bundles", "meridian_transit", "attesters", "ridership_shape.py"));
        var attester = new ContainerAttester(new CliContainerEngine(), new ContainerAttesterOptions());

        Task<AttestationVerdict> Attest(object? trips, object? riders) => attester.AttestAsync(new AttestationContext(
            Contract: new AttestedComputationContract(
                "postgres",
                [new ComputationParameter("service_date", "string", true)],
                null,
                new Executor(null, ["executed_sql", "result"]),
                new Attester("attesters/ridership_shape.py")),
            Computation: new SanctionedComputation(ComputationSource.Inline, "SELECT 1", null),
            Bound: new BoundComputation("postgres", "SELECT 1", null, new Dictionary<string, object?> { ["service_date"] = "2026-09-10" }),
            Values: new Dictionary<string, object?> { ["service_date"] = "2026-09-10" },
            Receipt: new Receipt(new Dictionary<string, object?>
            {
                ["executed_sql"] = "SELECT 1",
                ["result"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["completed_trips"] = trips, ["distinct_riders"] = riders },
                },
            }),
            AttesterSourceText: source)).AsTask();

        var genuine = await Attest(5L, 2L);
        Assert.True(genuine.Passed, genuine.Detail);

        var malformed = new (string Label, object? Trips, object? Riders, string Column)[]
        {
            ("string count", "5", 2L, "completed_trips"),
            ("fractional float", 5L, 2.9, "distinct_riders"),
            ("boolean", 5L, true, "distinct_riders"),
        };

        foreach (var (label, trips, riders, column) in malformed)
        {
            var verdict = await Attest(trips, riders);
            Assert.False(verdict.Passed, $"{label}: the attester accepted {column} = {trips ?? "null"} / {riders ?? "null"}");
            Assert.Contains($"{column} is not an integer", verdict.Detail ?? "", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Raised by Copilot on #98: <c>active_user_count_attester.py</c>'s boolean guard is a
    /// real behaviour change in shipped Python that nothing executed — the other Docker
    /// tests run the Meridian modules or attesters written inline. The module as it ships
    /// runs here. A genuine count passes first, so the rejections below cannot come from
    /// an attester that fails everything; <c>true</c> and <c>false</c> are the cases a
    /// plain <c>isinstance(_, int)</c> check lets through, since Python's <c>bool</c>
    /// subclasses <c>int</c>.
    /// </summary>
    [SkippableFact]
    public async Task Demo_active_user_count_attester_accepts_only_a_genuine_non_negative_count()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        const string sql = "SELECT count(*) AS active_users FROM users WHERE active = true AND id >= :min_id";
        var source = File.ReadAllText(Path.Combine(TestPaths.RepoRoot(), "bundles", "attestation_containers_demo", "attesters", "active_user_count_attester.py"));
        var attester = new ContainerAttester(new CliContainerEngine(), new ContainerAttesterOptions());

        Task<AttestationVerdict> Attest(object? count) => attester.AttestAsync(new AttestationContext(
            Contract: new AttestedComputationContract(
                "postgres",
                [new ComputationParameter("min_id", "integer", true)],
                null,
                new Executor(null, ["executed_sql", "result"]),
                new Attester("/attesters/active_user_count_attester.py")),
            Computation: new SanctionedComputation(ComputationSource.Inline, sql, null),
            Bound: new BoundComputation("postgres", sql, null, new Dictionary<string, object?> { ["min_id"] = 1L }),
            Values: new Dictionary<string, object?> { ["min_id"] = 1L },
            Receipt: new Receipt(new Dictionary<string, object?>
            {
                ["executed_sql"] = sql,
                ["result"] = new List<object?> { new Dictionary<string, object?> { ["active_users"] = count } },
            }),
            AttesterSourceText: source)).AsTask();

        var genuine = await Attest(2L);
        Assert.True(genuine.Passed, genuine.Detail);

        foreach (var (label, count) in new (string, object?)[] { ("true", true), ("false", false), ("string", "2"), ("float", 2.5), ("negative", -1L) })
        {
            var verdict = await Attest(count);
            Assert.False(verdict.Passed, $"{label}: the attester accepted active_users = {count ?? "null"}");
            Assert.Contains("active_users is not a non-negative integer", verdict.Detail ?? "", StringComparison.Ordinal);
        }
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

        Assert.True(outcome.Displayable, Why(outcome));
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
        Assert.True(outcome.Displayable, Why(outcome));
        Assert.NotNull(outcome.Receipt);

        // The receipt parsed, which is the #3 guarantee: nothing but JSON reached stdout.
        Assert.True(outcome.ReceiptShapeOk);

        // And it carries the values, which is the #2 guarantee: the Decimal/date/UUID/bytes
        // were encoded (as their str() form) instead of aborting the dump.
        var rows = Assert.IsAssignableFrom<System.Collections.IEnumerable>(outcome.Receipt!.Fields["result"]);
        Assert.Contains("1234.56", System.Text.Json.JsonSerializer.Serialize(rows));
    }

    /// <summary>
    /// libpq spells a password containing `@` as `%40`; the wrapper must
    /// percent-decode userinfo or that password never authenticates. And a
    /// sanctioned statement that returns no rows (`CREATE TEMP TABLE …`) must
    /// yield an empty `result`, not a TypeError after the statement already ran.
    /// </summary>
    [SkippableFact]
    public async Task SqlClient_wrapper_decodes_userinfo_and_survives_a_statement_without_rows()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");
        var conn = Environment.GetEnvironmentVariable("OKF_DEMO_PG_CONN");
        Skip.If(string.IsNullOrEmpty(conn), "OKF_DEMO_PG_CONN is not set");

        // Same credentials, password spelled percent-encoded ("demo" -> "d%65mo").
        var encoded = conn!.Replace("postgres:demo@", "postgres:d%65mo@", StringComparison.Ordinal);
        var profile = new ContainerRuntimeProfile
        {
            Image = "python:3.12-slim",
            Kind = ContainerRuntimeKind.SqlClient,
            Environment = new Dictionary<string, string> { ["OKF_CONN"] = encoded },
        };
        var executor = new SqlClientComputationExecutor(new CliContainerEngine(), profile);
        var bound = new BoundComputation("postgres", "CREATE TEMP TABLE okf_probe(id int)", null, new Dictionary<string, object?>());
        var contract = new AttestedComputationContract(Runtime: "postgres", Parameters: [], ComputationPath: null, Executor: new Executor(null, ["executed_sql", "result"]), Attester: null);

        var receipt = await executor.ExecuteAsync(bound, contract);

        Assert.Equal("CREATE TEMP TABLE okf_probe(id int)", receipt.Fields["executed_sql"]);
        Assert.Empty(Assert.IsType<List<object?>>(receipt.Fields["result"]));
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

    /// <summary>
    /// The 8 Mi-character output cap is a stage failure, not a silent truncation: a
    /// container that floods stdout must come back as a
    /// <see cref="ContainerExecutionException"/> naming the ceiling, never as a receipt
    /// built from a cut-off prefix. The unit-level half of this is
    /// <c>CliContainerEngineRunTests.The_bounded_reader_reports_when_it_dropped_output</c>;
    /// this is the same property observed through a real engine.
    /// </summary>
    [SkippableFact]
    public async Task A_container_that_floods_stdout_fails_the_stage_instead_of_being_truncated()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        var engine = new CliContainerEngine();
        var spec = new ContainerRunSpec(
            Image: "python:3.12-slim",
            Command: ["python3", "-c", "import sys; sys.stdout.write('x' * (9 * 1024 * 1024))"],
            Stdin: null,
            Environment: new Dictionary<string, string>(),
            NetworkMode: "none",
            MemoryBytes: 128 * 1024 * 1024,
            Cpus: 0.5,
            PidsLimit: 16,
            Timeout: TimeSpan.FromSeconds(60));

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(async () => await engine.RunAsync(spec));
        Assert.Contains("output ceiling", ex.Message);
    }

    /// <summary>
    /// The executable guard behind SqlClientComputationExecutorTests' source-text smoke
    /// check. Builds a throwaway image with pg8000 vendored in, then runs the SqlClient
    /// wrapper on it with <c>--network none</c> and a connection string nothing can
    /// answer. Import-first means the run gets as far as the driver's own connection
    /// attempt, so the failure names pg8000 and never pip. Install-first — the first
    /// version — made the documented "vendor the driver and close the network"
    /// hardening a guaranteed pip failure before any SQL ran, which is the defect this
    /// pins. The build is cached by the engine after the first run.
    /// </summary>
    [SkippableFact]
    public async Task SqlClient_runtime_with_the_driver_vendored_needs_no_package_index()
    {
        Skip.IfNot(DockerAvailable(), "docker is not on PATH");

        const string image = "okf-test-pg8000-vendored:local";
        BuildImage(image, "FROM python:3.12-slim\nRUN pip install --quiet pg8000==1.31.5\n");

        var engine = new CliContainerEngine();
        var profile = new ContainerRuntimeProfile
        {
            Image = image,
            Kind = ContainerRuntimeKind.SqlClient,
            NetworkMode = "none",
            Environment = new Dictionary<string, string> { ["OKF_CONN"] = "postgresql://u:p@127.0.0.1:1/nowhere" },
            Isolation = new() { Timeout = TimeSpan.FromSeconds(60) },
        };
        var executor = new SqlClientComputationExecutor(engine, profile);
        var bound = new BoundComputation("postgres", "SELECT 1", null, new Dictionary<string, object?>());
        var contract = new AttestedComputationContract(
            Runtime: "postgres", Parameters: [], ComputationPath: null,
            Executor: new Executor(null, ["executed_sql", "result"]), Attester: null);

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(async () => await executor.ExecuteAsync(bound, contract));

        Assert.DoesNotContain("pip", ex.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pg8000", ex.Stderr, StringComparison.Ordinal);
    }

    private static void BuildImage(string tag, string dockerfile)
    {
        using var p = Process.Start(new ProcessStartInfo("docker", $"build --quiet -t {tag} -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        p.StandardInput.Write(dockerfile);
        p.StandardInput.Close();
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit(300_000);
        Assert.True(p.ExitCode == 0, $"docker build failed: {stderr.Result}");
    }
}
