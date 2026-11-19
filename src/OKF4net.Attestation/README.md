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

## Sanctioned files are read through the provider

The `computation` file and the `attester.resource` a concept names are never
opened with `System.IO`. `Bundle.ExecutableResources` surveys them once per
load, by key, through the bundle's `IBundleProvider`, recording each as
`Pinned` (with its version) or in a state that forbids running it.
`AttestationOrchestrator.Capture` is the single place sanctioned code is read
for a run: it reads only the keys the survey found `Pinned`, and only at that
version (`Bundle.ReadResourceText(BundleKey, expected, out version)`), so a file
replaced after the load is refused rather than run. This holds for any provider,
including one whose `Location` is not a path.

## Allowing only approved code (#154)

Wiring an orchestrator trusts whatever sanctioned code the bundle references,
so in effect everyone who can write to the bundle's source. A host can pin
*what* may run instead, with an allowlist of digests:

```csharp
var orchestrator = AttestationOrchestrator.Create(runtimes, new AttestationOrchestratorOptions
{
    AllowedCodeDigests = new HashSet<string> { approvedComputationDigest, approvedAttesterDigest },
});
```

A digest is `AttestationCodeDigest.Of(text)`: SHA-256 of the UTF-8 encoding of
the exact text the runtime receives, as 64 lowercase hex digits (case is
ignored on input), with no normalisation. A change of line endings is a
different digest. That text is what `AttestationOrchestrator.Capture` holds:
`Capture.Computation.InlineCode`, which is the fence's content (without its
final line break) or the computation file's text, and `Capture.AttesterSourceText`.
One set covers both roles and every provider: a digest approves a content
wherever a bundle references it.

When set, a run whose computation or attester is not in the set is refused
before any runtime stage. The outcome's `Reasons` name the refused roles, never
their digests, because a tool may show them to a model. `UnapprovedCode` gives
the host each refused role with its digest, so an operator can approve it
deliberately. A malformed entry is refused when the orchestrator is built.
Without an allowlist (`null`, the default) nothing changes. The allowlist and
the pinning are complementary: the pinning decides *which* bytes are read, the
allowlist whether they may run.

Licensed LGPL-3.0-or-later.
