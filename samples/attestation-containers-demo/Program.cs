// SPDX-License-Identifier: LGPL-3.0-or-later
// samples/attestation-containers-demo/Program.cs
using OKF4net;
using OKF4net.Attestation;
using OKF4net.Attestation.Containers;

var bundleRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bundles", "attestation_containers_demo"));
if (!Directory.Exists(bundleRoot))
{
    Console.Error.WriteLine($"error: bundle not found at '{bundleRoot}' -- run from the repo checkout.");
    return 1;
}

var bundle = Bundle.Load(bundleRoot);
var engine = new CliContainerEngine();

var pythonRuntime = new ContainerAttestationRuntime(engine, new ContainerRuntimeProfile
{
    Image = "python:3.12-slim",
    Kind = ContainerRuntimeKind.Script,
});

var pgConn = Environment.GetEnvironmentVariable("OKF_DEMO_PG_CONN");
var runtimes = new Dictionary<string, IAttestationRuntime> { ["python"] = pythonRuntime };
if (!string.IsNullOrEmpty(pgConn))
{
    runtimes["postgres"] = new ContainerAttestationRuntime(engine, new ContainerRuntimeProfile
    {
        Image = "python:3.12-slim",
        Kind = ContainerRuntimeKind.SqlClient,
        Environment = new Dictionary<string, string> { ["OKF_CONN"] = pgConn },
    });
}
else
{
    Console.WriteLine("(OKF_DEMO_PG_CONN not set -- skipping the postgres runtime)");
}

var orchestrator = new AttestationOrchestrator(new AttestationRuntimeRegistry(runtimes));

Console.WriteLine("Running 'greeting' (runtime: python)...");
var greeting = await orchestrator.RunAsync(bundle, ConceptId.Parse("computations/greeting"), new Dictionary<string, object?> { ["name"] = "Ada" });
Console.WriteLine($"  displayable: {greeting.Displayable}");
if (greeting.Displayable)
{
    Console.WriteLine($"  message: {greeting.Receipt!.Fields["message"]}");
}
else
{
    Console.WriteLine($"  reasons: {string.Join("; ", greeting.Reasons)}");
}

if (runtimes.ContainsKey("postgres"))
{
    Console.WriteLine("Running 'active-user-count' (runtime: postgres)...");
    var count = await orchestrator.RunAsync(bundle, ConceptId.Parse("computations/active-user-count"), new Dictionary<string, object?> { ["min_id"] = 1 });
    Console.WriteLine($"  displayable: {count.Displayable}");
    Console.WriteLine(count.Displayable
        ? $"  result: {System.Text.Json.JsonSerializer.Serialize(count.Receipt!.Fields["result"])}"
        : $"  reasons: {string.Join("; ", count.Reasons)}");
}

return 0;
