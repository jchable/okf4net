# OKF4net.Attestation

Host-plugged orchestration of [Open Knowledge Format (OKF) v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
§10 Attested Computations, over the [OKF4net](https://www.nuget.org/packages/OKF4net)
format core. This project defines the host contracts a runtime plugs in
(`IParameterBinder`, `IComputationExecutor`, `IAttester`, resolved per
`contract.Runtime` through an `IAttestationRuntimeRegistry`), the value types
that flow between them (`BoundComputation`, `Receipt`, `AttestationVerdict`,
`AttestationContext`, `AttestationOutcome`), and an `AttestationOrchestrator`
that drives one `RunAsync` call end to end: resolve the sanctioned
computation, bind parameters, execute, validate the receipt shape, attest,
and gate on staleness — always returning an `AttestationOutcome`
(errors-as-data), never throwing for an expected failure. This package
references only `OKF4net` — no third-party runtime dependencies.

```csharp
using OKF4net;
using OKF4net.Attestation;

IAttestationRuntimeRegistry runtimes = new AttestationRuntimeRegistry(
    new Dictionary<string, IAttestationRuntime> { ["bigquery"] = myBigQueryRuntime });

var orchestrator = new AttestationOrchestrator(runtimes);

AttestationOutcome outcome = await orchestrator.RunAsync(
    bundle, conceptId, new Dictionary<string, object?> { ["region"] = "eu" });

if (outcome.Displayable)
{
    Console.WriteLine(outcome.Receipt);
}
else
{
    Console.WriteLine(string.Join("; ", outcome.Reasons));
}
```

Licensed LGPL-3.0-or-later.
