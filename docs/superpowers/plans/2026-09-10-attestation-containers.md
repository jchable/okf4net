# Attestation Containers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `src/OKF4net.Attestation.Containers/`, a reusable host implementation of the §10 `IParameterBinder`/`IComputationExecutor`/`IAttester` contracts that runs sanctioned computations and attesters in real containers (Docker/Podman/nerdctl), instead of porting bundle scripts to C#.

**Architecture:** One shared parameter binder (filters/type-checks against `contract.Parameters`, never touches the bound text) feeds two `IComputationExecutor` implementations (`Script`, `SqlClient`) and one `IAttester` implementation, all driven by a single `IContainerEngine` abstraction whose only real implementation (`CliContainerEngine`) shells to a `run`-compatible container CLI via `Process.Start`/`ArgumentList` — never a concatenated shell string. Every payload (script text, SQL text, attester module source) travels over stdin as text or JSON; the container command is always a short, fixed string. A small prerequisite change to the already-shipped `OKF4net.Attestation` project lets `AttestationContext` carry a pre-resolved attester source, so no `IAttester` implementation ever needs a `Bundle`.

**Tech Stack:** .NET 10 / C# 14, `System.Diagnostics.Process`, `System.Text.Json` (both in-box, no NuGet package), xunit. Container-side: Python 3 (stdlib only for the Script/Attester paths; `pg8000` pip-installed at run time for the SqlClient path — see Task 5's note).

**Spec:** `docs/superpowers/specs/2026-09-07-attestation-containers-design.md` — read it in full before starting. This plan implements it section by section; task headers below name the design section they satisfy.

## Global Constraints

- Zero third-party NuGet packages in `src/OKF4net.Attestation.Containers/` — only a `ProjectReference` to `OKF4net.Attestation`. `System.Diagnostics.Process` and `System.Text.Json` are in the net10.0 shared framework, not packages.
- `TreatWarningsAsErrors=true`, `LangVersion 14`, nullable enabled, file-scoped namespaces, XML doc comments on public API — all inherited from `Directory.Build.props`; new source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- No container ever gets a bind-mounted volume. Every payload travels over stdin (text or JSON); the container's own command is always a short, fixed string (an interpreter invocation or a project-authored wrapper) — never text derived from bundle content.
- `CliContainerEngine` builds its command exclusively via `ProcessStartInfo.ArgumentList`; never a concatenated string, never `UseShellExecute = true`.
- Container-integration tests that shell to a real engine binary are excluded from CI by decision (mirrors `producers/`) — tagged `[Trait("Category", "ContainerIntegration")]`, run manually.
- `bundles/acme_retail/` is never modified by this plan.

---

## Task 1: Resolve `attester.resource` in `AttestationOrchestrator` (prerequisite, in `OKF4net.Attestation`)

Design section: "Résolution de `attester.resource`" under Attester; "Prérequis" under Périmètre.

**Files:**
- Modify: `src/OKF4net.Attestation/Values.cs` (add a field to `AttestationContext`)
- Modify: `src/OKF4net.Attestation/AttestationOrchestrator.cs`
- Test: `tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs`

**Interfaces:**
- Produces: `AttestationContext.AttesterSourceText` (`string?`) — the attester's script source, already read from disk via §6.2 resolution; `null` when the concept declares no attester, an empty resource, or a URL resource. Every later task that implements `IAttester` reads this field instead of touching a `Bundle`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs` (after the existing tests, same class):

```csharp
    private static (Bundle, ConceptId) InlineComputationWithAttesterFile(TempDir tmp, string attesterBody)
    {
        tmp.Write("c/att.py", attesterBody);
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id, result] }\n" +
            "attester: { resource: att.py }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        return (Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"));
    }

    [Fact]
    public async Task AttesterSourceText_carries_the_resolved_attester_script()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputationWithAttesterFile(tmp, "def attest(**_):\n    return {}\n");

        AttestationContext? captured = null;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        runtime.AttestFunc = (ctx, _) =>
        {
            captured = ctx;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });

        Assert.NotNull(captured);
        Assert.Equal("def attest(**_):\n    return {}\n", captured!.AttesterSourceText);
    }

    [Fact]
    public async Task Missing_attester_resource_file_fails_early_without_calling_the_attester()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputationWithAttesterFile(tmp, "def attest(**_):\n    return {}\n");
        // Overwrite with a concept whose attester.resource does not exist on disk.
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id, result] }\n" +
            "attester: { resource: does-not-exist.py }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        var bundle2 = Bundle.Load(tmp.Path);

        var attested = false;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        runtime.AttestFunc = (_, _) =>
        {
            attested = true;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle2, id, new Dictionary<string, object?> { ["year"] = 2026 });

        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("attester resource", StringComparison.Ordinal));
        Assert.False(attested, "the attester ran despite its own resource being unresolvable");
    }

    [Fact]
    public async Task Absent_attester_declaration_yields_null_source_text_not_a_failure()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id, result] }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        var bundle = Bundle.Load(tmp.Path);
        var id = ConceptId.Parse("c/rev");

        AttestationContext? captured = null;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        runtime.AttestFunc = (ctx, _) =>
        {
            captured = ctx;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });

        Assert.True(outcome.Displayable);
        Assert.Null(captured!.AttesterSourceText);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~AttestationOrchestratorTests"`
Expected: the three new tests FAIL — `AttestationContext` has no `AttesterSourceText` member yet (compile error), so the whole test assembly fails to build.

- [ ] **Step 3: Add the field to `AttestationContext`**

In `src/OKF4net.Attestation/Values.cs`, replace:

```csharp
public sealed record AttestationContext(
    AttestedComputationContract Contract,
    SanctionedComputation Computation,
    BoundComputation Bound,
    IReadOnlyDictionary<string, object?> Values,
    Receipt Receipt);
```

with:

```csharp
public sealed record AttestationContext(
    AttestedComputationContract Contract,
    SanctionedComputation Computation,
    BoundComputation Bound,
    IReadOnlyDictionary<string, object?> Values,
    Receipt Receipt,
    string? AttesterSourceText);
```

Replace the doc comment above the record — currently:

```csharp
/// <summary>
/// The full context handed to an <see cref="IAttester"/> (decision 8,
/// §10.5(a)(b)): everything it might need to verify a receipt.
/// </summary>
/// <param name="Contract">The §10.2 contract projected from the concept's frontmatter.</param>
/// <param name="Computation">The sanctioned computation that was run.</param>
/// <param name="Bound">The bound artifact that was executed.</param>
/// <param name="Values">The parameter values supplied for this run.</param>
/// <param name="Receipt">The receipt produced by the executor.</param>
```

with:

```csharp
/// <summary>
/// The full context handed to an <see cref="IAttester"/> (decision 8,
/// §10.5(a)(b)): everything it might need to verify a receipt.
/// </summary>
/// <param name="Contract">The §10.2 contract projected from the concept's frontmatter.</param>
/// <param name="Computation">The sanctioned computation that was run.</param>
/// <param name="Bound">The bound artifact that was executed.</param>
/// <param name="Values">The parameter values supplied for this run.</param>
/// <param name="Receipt">The receipt produced by the executor.</param>
/// <param name="AttesterSourceText">
/// The attester's script source, already resolved and read via §6.2
/// (mirrors how <see cref="Computation"/> is resolved) — <see langword="null"/>
/// when the concept declares no attester, an empty resource, or a URL
/// resource. No <see cref="IAttester"/> implementation needs a
/// <see cref="Bundle"/> or <see cref="Concept"/> as a result.
/// </param>
```

- [ ] **Step 4: Add `TryResolveAttesterSource` and call it from `RunAsync`**

In `src/OKF4net.Attestation/AttestationOrchestrator.cs`, add a new private static method, placed right after `TryResolveComputation` (which it mirrors):

```csharp
    /// <summary>
    /// Resolves the concept's <c>attester.resource</c> the same way
    /// <see cref="TryResolveComputation"/> resolves <c>computation</c>, so no
    /// <see cref="IAttester"/> implementation ever needs a <see cref="Bundle"/>
    /// itself. Absent, empty, or URL-valued <c>attester.resource</c> is not a
    /// failure — §11 leaves the attester optional (a validator warning, not
    /// an error, flags an empty resource) — it simply yields a
    /// <see langword="null"/> source. A declared resource that cannot
    /// actually be resolved or read on disk IS an early failure, same as a
    /// broken computation file.
    /// </summary>
    /// <param name="bundle">The bundle the concept was loaded from.</param>
    /// <param name="concept">The attested-computation concept.</param>
    /// <param name="contract">The concept's §10.2 contract.</param>
    /// <param name="attesterSourceText">The resolved source text, or <see langword="null"/> when there is nothing to resolve.</param>
    /// <param name="failure">The non-displayable outcome to return, when this returns <see langword="false"/>.</param>
    private static bool TryResolveAttesterSource(
        Bundle bundle,
        Concept concept,
        AttestedComputationContract contract,
        out string? attesterSourceText,
        [NotNullWhen(false)] out AttestationOutcome? failure)
    {
        attesterSourceText = null;
        failure = null;

        var resource = contract.Attester?.Resource;
        if (string.IsNullOrEmpty(resource))
        {
            return true;
        }

        if (!bundle.TryResolveResource(concept, resource, out var absolutePath, out var status) || status == ResourceResolutionStatus.Url)
        {
            // URLs are never resolved on disk (§6.2); nothing to read. (The
            // `!TryResolveResource(...)` half never actually triggers --
            // resolution always returns true -- kept only for the same
            // defensive symmetry TryResolveComputation above already uses.)
            return true;
        }

        if (status != ResourceResolutionStatus.Resolved)
        {
            failure = Fail($"attester resource '{resource}' could not be resolved ({status})");
            return false;
        }

        try
        {
            attesterSourceText = bundle.ReadResourceText(absolutePath!);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            failure = Fail($"attester resource '{resource}' could not be read: {e.GetType().Name}");
            return false;
        }
    }
```

Then wire it into `RunAsync`. Immediately after step 2's block (`if (!TryResolveComputation(bundle, concept, out var resolved, out var resolutionFailure)) { return resolutionFailure; }`), insert:

```csharp
        // Step 2b: resolve the attester's own source, the same way (§6.2).
        if (!TryResolveAttesterSource(bundle, concept, contract, out var attesterSourceText, out var attesterResolutionFailure))
        {
            return attesterResolutionFailure;
        }
```

Finally, update the context construction (currently `var context = new AttestationContext(contract, resolved, bound, parameterValues, receipt);`) to:

```csharp
            var context = new AttestationContext(contract, resolved, bound, parameterValues, receipt, attesterSourceText);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~AttestationOrchestratorTests"`
Expected: PASS, including the three new tests and every pre-existing test in the class (they never constructed `AttestationContext` directly, so the added field does not break them).

- [ ] **Step 6: Run the full existing suite to confirm no regression**

Run: `dotnet test OKF4net.sln`
Expected: PASS, same total count as before this change plus 3.

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net.Attestation/Values.cs src/OKF4net.Attestation/AttestationOrchestrator.cs tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs
git commit -m "feat(attestation): pre-resolve attester.resource into AttestationContext"
```

---

## Task 2: Project scaffolding, core value types, engine abstraction, JSON helper

Design section: "Architecture & dépendances"; "Principe transversal : pas de montage de volume".

**Files:**
- Create: `src/OKF4net.Attestation.Containers/OKF4net.Attestation.Containers.csproj`
- Create: `src/OKF4net.Attestation.Containers/README.md`
- Create: `src/OKF4net.Attestation.Containers/IContainerEngine.cs`
- Create: `src/OKF4net.Attestation.Containers/ContainerRuntimeProfile.cs`
- Create: `src/OKF4net.Attestation.Containers/ContainerExecutionException.cs`
- Create: `src/OKF4net.Attestation.Containers/Internal/JsonValues.cs`
- Create: `src/OKF4net.Attestation.Containers/Internal/ReceiptParsing.cs`
- Create: `tests/OKF4net.Tests/Attestation.Containers/FakeContainerEngine.cs`
- Create: `tests/OKF4net.Tests/Attestation.Containers/JsonValuesTests.cs`
- Create: `tests/OKF4net.Tests/Attestation.Containers/ReceiptParsingTests.cs`
- Modify: `OKF4net.sln`

**Interfaces:**
- Produces: `IContainerEngine`, `ContainerRunSpec`, `ContainerRunResult`, `ContainerRuntimeKind`, `ContainerRuntimeProfile`, `ContainerAttesterOptions`, `ContainerExecutionException`, `internal static JsonValues.Normalize(JsonElement) -> object?`, `internal static ReceiptParsing.Parse(ContainerRunResult, string stageName) -> Receipt`, `FakeContainerEngine` (test double, in the test project's `OKF4net.Tests.Attestation.Containers` namespace). Every later task consumes these. `ReceiptParsing.Parse` exists so Tasks 4 and 5 don't each duplicate the same exit-code-check-then-parse-JSON-into-a-Receipt logic — they differ only in the stage name that appears in a thrown exception's message.

- [ ] **Step 1: Create the project file**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>

  <PropertyGroup Label="Packaging">
    <PackageId>OKF4net.Attestation.Containers</PackageId>
    <Authors>Julien CHABLE</Authors>
    <Description>Container-based host implementation of OKF v0.2 §10 Attested Computation contracts: runs sanctioned scripts/queries and attester scripts in real containers (Docker/Podman/nerdctl), never reimplemented in C#.</Description>
    <Copyright>Copyright 2026 Julien CHABLE</Copyright>
    <PackageLicenseExpression>LGPL-3.0-or-later</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageProjectUrl>https://github.com/jchable/okf4net</PackageProjectUrl>
    <RepositoryUrl>https://github.com/jchable/okf4net</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageTags>okf;knowledge;attestation;attested-computation;containers</PackageTags>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  </PropertyGroup>

  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
    <None Include="..\..\NOTICE" Pack="true" PackagePath="\" />
    <None Include="..\..\LICENSE.Apache-2.0" Pack="true" PackagePath="\" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\OKF4net.Attestation\OKF4net.Attestation.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- Lets the test project exercise internal members (JsonValues,
         CliContainerEngine's argument builder in Task 9) directly. -->
    <InternalsVisibleTo Include="OKF4net.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Add it to the solution**

Run: `dotnet sln OKF4net.sln add src/OKF4net.Attestation.Containers/OKF4net.Attestation.Containers.csproj`

- [ ] **Step 3: Write the README**

```markdown
# OKF4net.Attestation.Containers

A host implementation of `OKF4net.Attestation`'s §10 contracts
(`IParameterBinder`, `IComputationExecutor`, `IAttester`) that executes the
*actual* sanctioned computation and attester scripts a bundle references, in
real containers (Docker/Podman/nerdctl) — never a C# reimplementation of a
bundle's logic. See
[the design doc](https://github.com/jchable/okf4net/blob/main/docs/superpowers/specs/2026-09-07-attestation-containers-design.md)
for the full rationale and protocol.

Requires a container engine binary (`docker`, `podman`, or `nerdctl`)
installed and on `PATH` — this is a runtime prerequisite, not a NuGet
dependency; the project itself has zero third-party package references.
```

- [ ] **Step 4: Write the failing tests for `JsonValues.Normalize` and `ReceiptParsing.Parse`**

```csharp
// tests/OKF4net.Tests/Attestation.Containers/JsonValuesTests.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using OKF4net.Attestation.Containers.Internal;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class JsonValuesTests
{
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public void Normalizes_scalars_and_null()
    {
        Assert.Equal("hi", JsonValues.Normalize(Parse("\"hi\"")));
        Assert.Equal(42L, JsonValues.Normalize(Parse("42")));
        Assert.Equal(1.5, JsonValues.Normalize(Parse("1.5")));
        Assert.Equal(true, JsonValues.Normalize(Parse("true")));
        Assert.Null(JsonValues.Normalize(Parse("null")));
    }

    [Fact]
    public void Normalizes_nested_arrays_and_objects()
    {
        var result = JsonValues.Normalize(Parse("""{"a": [1, 2, {"b": "c"}]}"""));
        var dict = Assert.IsType<Dictionary<string, object?>>(result);
        var list = Assert.IsType<List<object?>>(dict["a"]);
        Assert.Equal(1L, list[0]);
        Assert.Equal(2L, list[1]);
        var nested = Assert.IsType<Dictionary<string, object?>>(list[2]);
        Assert.Equal("c", nested["b"]);
    }
}
```

```csharp
// tests/OKF4net.Tests/Attestation.Containers/ReceiptParsingTests.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using OKF4net.Attestation.Containers;
using OKF4net.Attestation.Containers.Internal;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class ReceiptParsingTests
{
    [Fact]
    public void A_non_zero_exit_code_throws_with_the_stage_name_in_the_message()
    {
        var ex = Assert.Throws<ContainerExecutionException>(
            () => ReceiptParsing.Parse(new ContainerRunResult(1, "", "boom"), "script"));
        Assert.Contains("script exited with code 1", ex.Message);
        Assert.Equal("boom", ex.Stderr);
    }

    [Fact]
    public void Malformed_stdout_JSON_throws_with_the_stage_name_in_the_message()
    {
        var ex = Assert.Throws<ContainerExecutionException>(
            () => ReceiptParsing.Parse(new ContainerRunResult(0, "not json", ""), "SQL wrapper"));
        Assert.Contains("SQL wrapper stdout was not valid JSON", ex.Message);
    }

    [Fact]
    public void Valid_stdout_JSON_becomes_a_normalized_Receipt()
    {
        var receipt = ReceiptParsing.Parse(new ContainerRunResult(0, """{"message": "hi", "count": 3}""", ""), "script");
        Assert.Equal("hi", receipt.Fields["message"]);
        Assert.Equal(3L, receipt.Fields["count"]);
    }
}
```

- [ ] **Step 5: Run the tests to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~JsonValuesTests|FullyQualifiedName~ReceiptParsingTests"`
Expected: FAIL — `OKF4net.Attestation.Containers` does not exist yet.

- [ ] **Step 6: Implement the remaining types**

```csharp
// src/OKF4net.Attestation.Containers/IContainerEngine.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers;

/// <summary>
/// Runs one container to completion. The single implementation shipped here
/// is <c>CliContainerEngine</c> (Task 8/9 — not yet written when this file
/// is; a <c>cref</c> here would fail to resolve and break the build under
/// this repo's TreatWarningsAsErrors, so this is deliberately plain text);
/// this abstraction exists so a fundamentally different engine (e.g. a
/// future Kubernetes Jobs backend) can be added without touching any
/// binder/executor/attester.
/// </summary>
public interface IContainerEngine
{
    /// <summary>Runs <paramref name="spec"/> in a fresh container and returns its result.</summary>
    ValueTask<ContainerRunResult> RunAsync(ContainerRunSpec spec, CancellationToken cancellationToken = default);
}

/// <summary>
/// Everything one container run needs. <see cref="Stdin"/> is the only
/// channel that ever carries bundle-derived content (script/SQL/attester
/// text) — no volume is ever mounted (see the design doc's "pas de montage
/// de volume" principle).
/// </summary>
/// <param name="Image">The container image to run.</param>
/// <param name="Command">The command run inside the image — always a short, fixed sequence (an interpreter, or a project-authored wrapper), never bundle-derived.</param>
/// <param name="Stdin">Piped to the container's stdin, then the stream is closed.</param>
/// <param name="Environment">Environment variables set on the container.</param>
/// <param name="NetworkMode">Passed as <c>--network</c> when non-null (e.g. <c>"none"</c>); omitted (engine default) when null.</param>
/// <param name="MemoryBytes">Passed as <c>--memory</c> when non-null.</param>
/// <param name="Cpus">Passed as <c>--cpus</c> when non-null.</param>
/// <param name="PidsLimit">Passed as <c>--pids-limit</c> when non-null.</param>
/// <param name="Timeout">A wall-clock ceiling enforced by the engine itself, independent of the caller's <see cref="CancellationToken"/>.</param>
public sealed record ContainerRunSpec(
    string Image,
    IReadOnlyList<string> Command,
    string? Stdin,
    IReadOnlyDictionary<string, string> Environment,
    string? NetworkMode,
    long? MemoryBytes,
    double? Cpus,
    int? PidsLimit,
    TimeSpan? Timeout);

/// <summary>One container run's outcome: exit code plus captured (size-bounded) stdout/stderr.</summary>
public sealed record ContainerRunResult(int ExitCode, string Stdout, string Stderr);
```

```csharp
// src/OKF4net.Attestation.Containers/ContainerRuntimeProfile.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers;

/// <summary>Which execution protocol a <see cref="ContainerRuntimeProfile"/> uses.</summary>
public enum ContainerRuntimeKind
{
    /// <summary>The bound computation text is a complete, standalone script — run directly.</summary>
    Script,

    /// <summary>The bound computation text is SQL, executed by a project-authored wrapper against an externally-configured database.</summary>
    SqlClient,
}

/// <summary>
/// Host-supplied configuration for one bundle <c>runtime</c> name (e.g.
/// <c>"python"</c>, <c>"postgres"</c>). One <c>ContainerAttestationRuntime</c>
/// (Task 7 — not yet written when this file is; plain text, not a
/// <c>cref</c>, for the same reason as <see cref="IContainerEngine"/>'s own
/// doc comment) is built per profile and registered under that name in
/// <c>AttestationRuntimeRegistry</c>.
/// </summary>
public sealed record ContainerRuntimeProfile
{
    /// <summary>The image to run the computation in. For <see cref="ContainerRuntimeKind.SqlClient"/>, must be Python-capable — the wrapper is always <c>python3</c> (Task 6).</summary>
    public required string Image { get; init; }

    /// <summary>Which execution protocol applies.</summary>
    public required ContainerRuntimeKind Kind { get; init; }

    /// <summary>The interpreter invoked for <see cref="ContainerRuntimeKind.Script"/> profiles (e.g. <c>"python3"</c>). Ignored for <see cref="ContainerRuntimeKind.SqlClient"/>.</summary>
    public string Interpreter { get; init; } = "python3";

    /// <summary>Environment variables passed to every container run under this profile (e.g. <c>OKF_CONN</c> for a <see cref="ContainerRuntimeKind.SqlClient"/> profile).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Default <c>--memory</c> ceiling. 512 MiB.</summary>
    public long MemoryBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Default <c>--cpus</c> ceiling.</summary>
    public double Cpus { get; init; } = 1.0;

    /// <summary>Default <c>--pids-limit</c> ceiling.</summary>
    public int PidsLimit { get; init; } = 64;

    /// <summary>Wall-clock ceiling enforced independently of the caller's <see cref="CancellationToken"/>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Configuration for <c>ContainerAttester</c> (Task 6 — not yet written when
/// this file is; plain text, not a <c>cref</c>, for the same reason as
/// <see cref="IContainerEngine"/>'s own doc comment) — always a fixed,
/// small Python image, independent of whatever image the executor's
/// <see cref="ContainerRuntimeProfile"/> uses (a <see cref="ContainerRuntimeKind.SqlClient"/>
/// profile's image has no Python at all).
/// </summary>
public sealed record ContainerAttesterOptions
{
    /// <summary>The attester's own image. Defaults to a small official Python image.</summary>
    public string Image { get; init; } = "python:3.12-slim";

    /// <summary>Environment variables passed to every attester run.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Default <c>--memory</c> ceiling. 256 MiB — an attester is a pure function over its inputs, never a network call.</summary>
    public long MemoryBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Default <c>--cpus</c> ceiling.</summary>
    public double Cpus { get; init; } = 0.5;

    /// <summary>Default <c>--pids-limit</c> ceiling.</summary>
    public int PidsLimit { get; init; } = 32;

    /// <summary>Wall-clock ceiling.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
```

```csharp
// src/OKF4net.Attestation.Containers/ContainerExecutionException.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers;

/// <summary>
/// A container run failed to produce a usable result (non-zero exit,
/// malformed JSON, missing prerequisite). <see cref="Message"/> and
/// <see cref="Stdout"/>/<see cref="Stderr"/> may carry bundle-derived detail
/// (a query, a stack trace) — safe only for host-side inspection via
/// <c>AttestationOutcome.Error</c>, never for the model-facing <c>Reasons</c>
/// list, which the orchestrator populates from this exception's TYPE alone.
/// </summary>
public sealed class ContainerExecutionException : Exception
{
    /// <summary>Creates the exception with the captured stdout/stderr.</summary>
    public ContainerExecutionException(string message, string stdout, string stderr)
        : base(message)
    {
        Stdout = stdout;
        Stderr = stderr;
    }

    /// <summary>The container's captured (size-bounded) standard output.</summary>
    public string Stdout { get; }

    /// <summary>The container's captured (size-bounded) standard error.</summary>
    public string Stderr { get; }
}
```

```csharp
// src/OKF4net.Attestation.Containers/Internal/JsonValues.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

namespace OKF4net.Attestation.Containers.Internal;

/// <summary>Converts a parsed <see cref="JsonElement"/> tree into plain CLR values, recursively.</summary>
internal static class JsonValues
{
    /// <summary>
    /// Normalizes <paramref name="element"/> into <see langword="string"/>,
    /// <see langword="long"/>/<see langword="double"/>, <see langword="bool"/>,
    /// <see langword="null"/>, <see cref="List{T}"/>, or
    /// <see cref="Dictionary{TKey,TValue}"/> — never a boxed
    /// <see cref="JsonElement"/> — so receipt/verdict fields compare equal to
    /// plain values the way existing tests already write them (e.g.
    /// <c>["result"] = 42</c>).
    /// </summary>
    public static object? Normalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        // The (object) cast is load-bearing: without it, C#'s conditional
        // operator unifies `long` and `double` by widening the long branch
        // to double (an implicit numeric conversion), so this would always
        // return a boxed double even when TryGetInt64 succeeds -- silently
        // failing every `Assert.Equal(42L, ...)`-shaped comparison a caller
        // makes downstream.
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(Normalize).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => Normalize(p.Value)),
        _ => null,
    };
}
```

```csharp
// src/OKF4net.Attestation.Containers/Internal/ReceiptParsing.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

namespace OKF4net.Attestation.Containers.Internal;

/// <summary>
/// Turns a container's raw <see cref="ContainerRunResult"/> into a
/// <see cref="Receipt"/>, or throws a <see cref="ContainerExecutionException"/>
/// explaining why it couldn't. Shared by <see cref="ScriptComputationExecutor"/>
/// and <see cref="SqlClientComputationExecutor"/> (Tasks 4/5) — both need
/// exactly this "non-zero exit is a failure; otherwise the stdout JSON
/// object's fields, normalized, are the receipt" logic, differing only in
/// <paramref name="stageName"/>, the word that names the stage in a thrown
/// exception's message.
/// </summary>
internal static class ReceiptParsing
{
    /// <summary>Parses <paramref name="result"/> into a <see cref="Receipt"/>.</summary>
    /// <param name="result">The container's raw run result.</param>
    /// <param name="stageName">Names the stage in a thrown exception's message (e.g. <c>"script"</c>, <c>"SQL wrapper"</c>).</param>
    public static Receipt Parse(ContainerRunResult result, string stageName)
    {
        if (result.ExitCode != 0)
        {
            throw new ContainerExecutionException($"{stageName} exited with code {result.ExitCode}", result.Stdout, result.Stderr);
        }

        Dictionary<string, JsonElement>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(result.Stdout);
        }
        catch (JsonException e)
        {
            throw new ContainerExecutionException($"{stageName} stdout was not valid JSON: {e.Message}", result.Stdout, result.Stderr);
        }

        var fields = (parsed ?? []).ToDictionary(kv => kv.Key, kv => JsonValues.Normalize(kv.Value));
        return new Receipt(fields);
    }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~JsonValuesTests|FullyQualifiedName~ReceiptParsingTests"`
Expected: PASS.

- [ ] **Step 8: Write `FakeContainerEngine`**

```csharp
// tests/OKF4net.Tests/Attestation.Containers/FakeContainerEngine.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System;
using System.Threading;
using System.Threading.Tasks;
using OKF4net.Attestation.Containers;

namespace OKF4net.Tests.Attestation.Containers;

/// <summary>
/// A configurable in-memory <see cref="IContainerEngine"/> for tests: records
/// the last <see cref="ContainerRunSpec"/> it was given, and returns a
/// settable canned result. No process is ever spawned. Mirrors
/// <c>FakeRuntime</c>'s style in <c>tests/OKF4net.Tests/Attestation</c>.
/// </summary>
public sealed class FakeContainerEngine : IContainerEngine
{
    /// <summary>The most recent spec passed to <see cref="RunAsync"/>, or <see langword="null"/> before any call.</summary>
    public ContainerRunSpec? LastSpec { get; private set; }

    /// <summary>Produces the result for a given spec. Defaults to a successful run with an empty JSON object on stdout.</summary>
    public Func<ContainerRunSpec, ContainerRunResult> Respond { get; set; } = _ => new ContainerRunResult(0, "{}", "");

    /// <inheritdoc />
    public ValueTask<ContainerRunResult> RunAsync(ContainerRunSpec spec, CancellationToken cancellationToken = default)
    {
        LastSpec = spec;
        return ValueTask.FromResult(Respond(spec));
    }
}
```

- [ ] **Step 9: Build the whole solution to confirm the new project compiles cleanly**

Run: `dotnet build OKF4net.sln`
Expected: 0 warnings, 0 errors (warnings are errors solution-wide).

- [ ] **Step 10: Commit**

```bash
git add src/OKF4net.Attestation.Containers tests/OKF4net.Tests/Attestation.Containers OKF4net.sln
git commit -m "feat(attestation-containers): scaffold project, core value types, JSON normalizer"
```

---

## Task 3: Shared parameter allowlist/type-check + `AllowlistParameterBinder`

Design section: "Note sur le filtrage des paramètres"; component table rows for the binders.

**Note on scope (discovered while writing this plan, flagged for the meticulous review):** the design lists `ScriptParameterBinder` and `SqlClientParameterBinder` as two classes. Working through their actual logic, both end up doing exactly the same thing — filter `parameterValues` to the names declared in `contract.Parameters`, type-check them, and pass `BoundComputation.BoundText` through unchanged. The dialect-specific work (JSON for scripts, native driver kwargs for SQL) turns out to belong to the *executors* (Tasks 5/6), not the binder. This task therefore implements one shared `AllowlistParameterBinder`, used for both `ContainerRuntimeKind` values in Task 8's wiring, instead of two identical classes. This is a simplification, not a behavior change — it does not reopen the substitution defect the design's round-1 fix addressed, since this binder still never touches `BoundText` either way.

**Files:**
- Create: `src/OKF4net.Attestation.Containers/Internal/DeclaredParameterFilter.cs`
- Create: `src/OKF4net.Attestation.Containers/AllowlistParameterBinder.cs`
- Test: `tests/OKF4net.Tests/Attestation.Containers/AllowlistParameterBinderTests.cs`

**Interfaces:**
- Consumes: `OKF4net.Attestation.IParameterBinder`, `OKF4net.AttestedComputationContract`, `OKF4net.ComputationParameter`, `OKF4net.Attestation.BoundComputation`, `OKF4net.SanctionedComputation`.
- Produces: `AllowlistParameterBinder : IParameterBinder`, consumed by Task 8's `ContainerAttestationRuntime`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OKF4net.Tests/Attestation.Containers/AllowlistParameterBinderTests.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class AllowlistParameterBinderTests
{
    private static readonly AttestedComputationContract Contract = new(
        Runtime: "python",
        Parameters: [new ComputationParameter("name", "string", Required: true)],
        ComputationPath: null,
        Executor: null,
        Attester: null);

    private static readonly SanctionedComputation Computation = new(ComputationSource.Inline, "print('hi')", null);

    [Fact]
    public async Task Never_touches_the_bound_text()
    {
        var binder = new AllowlistParameterBinder();
        var bound = await binder.BindAsync(Contract, Computation, new Dictionary<string, object?> { ["name"] = "Ada" });
        Assert.Equal("print('hi')", bound.BoundText);
    }

    [Fact]
    public async Task Drops_undeclared_keys()
    {
        var binder = new AllowlistParameterBinder();
        var bound = await binder.BindAsync(Contract, Computation, new Dictionary<string, object?> { ["name"] = "Ada", ["extra"] = "should not survive" });
        Assert.True(bound.Values.ContainsKey("name"));
        Assert.False(bound.Values.ContainsKey("extra"));
    }

    [Fact]
    public async Task Rejects_a_declared_value_of_the_wrong_CLR_type()
    {
        var binder = new AllowlistParameterBinder();
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await binder.BindAsync(Contract, Computation, new Dictionary<string, object?> { ["name"] = 42 }));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~AllowlistParameterBinderTests"`
Expected: FAIL — `AllowlistParameterBinder` does not exist.

- [ ] **Step 3: Implement the filter helper and the binder**

```csharp
// src/OKF4net.Attestation.Containers/Internal/DeclaredParameterFilter.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers.Internal;

/// <summary>
/// Filters a run's supplied parameter values down to the ones a concept's
/// contract actually declares, so that a value the caller supplied but the
/// concept never asked for can never reach an executed script or query.
/// <see cref="AttestationOrchestrator.RunAsync"/> only checks that *required*
/// parameters are present — it hands the binder the caller's full,
/// unfiltered dictionary (§10.3 says extras are ignored; nothing in the
/// orchestrator enforces that today), so this allowlist is this host's own
/// responsibility.
/// </summary>
internal static class DeclaredParameterFilter
{
    /// <summary>
    /// Returns a new dictionary containing only the entries of
    /// <paramref name="values"/> whose key names a parameter in
    /// <paramref name="declared"/>, each checked against that parameter's
    /// declared <c>type</c> via <see cref="CheckType"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> FilterAndTypeCheck(
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<ComputationParameter> declared)
    {
        var result = new Dictionary<string, object?>();
        foreach (var parameter in declared)
        {
            if (values.TryGetValue(parameter.Name, out var value))
            {
                result[parameter.Name] = CheckType(value, parameter.Type);
            }
        }

        return result;
    }

    /// <summary>
    /// Rejects a value whose CLR type is incompatible with a known declared
    /// <paramref name="declaredType"/> string. An unrecognized type name is
    /// accepted as-is (§3's permissive-loading philosophy: judgment on
    /// unknown shapes is deferred, not a hard failure here).
    /// </summary>
    private static object? CheckType(object? value, string? declaredType)
    {
        if (value is null || declaredType is null)
        {
            return value;
        }

        var ok = declaredType switch
        {
            "integer" => value is int or long,
            "number" => value is int or long or float or double or decimal,
            "boolean" => value is bool,
            "string" => value is string,
            "date" => value is DateOnly or DateTime or DateTimeOffset or string,
            _ => true,
        };

        if (!ok)
        {
            throw new ArgumentException($"parameter value for declared type '{declaredType}' has an incompatible CLR type: {value.GetType().Name}");
        }

        return value;
    }
}
```

```csharp
// src/OKF4net.Attestation.Containers/AllowlistParameterBinder.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Attestation.Containers.Internal;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// The single <see cref="IParameterBinder"/> shared by every
/// <see cref="ContainerRuntimeKind"/>. Never edits
/// <see cref="BoundComputation.BoundText"/> — the sanctioned computation
/// text (SQL placeholders included) is carried through byte-for-byte. Its
/// only job is filtering and type-checking the supplied values against the
/// concept's declared <c>parameters</c> (see
/// <see cref="Internal.DeclaredParameterFilter"/>); dialect-specific
/// transport (JSON for a script, native driver parameters for SQL) is the
/// executor's job (Tasks 5/6), not the binder's.
/// </summary>
public sealed class AllowlistParameterBinder : IParameterBinder
{
    /// <inheritdoc />
    public ValueTask<BoundComputation> BindAsync(
        AttestedComputationContract contract,
        SanctionedComputation computation,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default)
    {
        var filtered = DeclaredParameterFilter.FilterAndTypeCheck(values, contract.Parameters);
        var bound = new BoundComputation(contract.Runtime ?? "", computation.InlineCode, null, filtered);
        return ValueTask.FromResult(bound);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~AllowlistParameterBinderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Attestation.Containers/Internal/DeclaredParameterFilter.cs src/OKF4net.Attestation.Containers/AllowlistParameterBinder.cs tests/OKF4net.Tests/Attestation.Containers/AllowlistParameterBinderTests.cs
git commit -m "feat(attestation-containers): add the shared allowlisting parameter binder"
```

---

## Task 4: `ScriptComputationExecutor`

Design section: "Executor `Script` (ex. `python`)".

**Files:**
- Create: `src/OKF4net.Attestation.Containers/ScriptComputationExecutor.cs`
- Test: `tests/OKF4net.Tests/Attestation.Containers/ScriptComputationExecutorTests.cs`

**Interfaces:**
- Consumes: `IContainerEngine.RunAsync`, `ContainerRuntimeProfile`, `Internal.ReceiptParsing.Parse`.
- Produces: `ScriptComputationExecutor : IComputationExecutor`, consumed by Task 8.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OKF4net.Tests/Attestation.Containers/ScriptComputationExecutorTests.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class ScriptComputationExecutorTests
{
    private static readonly ContainerRuntimeProfile Profile = new()
    {
        Image = "python:3.12-slim",
        Kind = ContainerRuntimeKind.Script,
    };

    private static readonly AttestedComputationContract Contract = new(
        Runtime: "python", Parameters: [], ComputationPath: null,
        Executor: new Executor(null, ["message"]), Attester: null);

    [Fact]
    public async Task Sends_the_bound_text_unchanged_on_stdin_and_params_as_a_JSON_env_var()
    {
        var engine = new FakeContainerEngine
        {
            Respond = _ => new ContainerRunResult(0, """{"message": "Hello, Ada!"}""", ""),
        };
        var executor = new ScriptComputationExecutor(engine, Profile);
        var bound = new BoundComputation("python", "print('hi')", null, new Dictionary<string, object?> { ["name"] = "Ada" });

        var receipt = await executor.ExecuteAsync(bound, Contract);

        Assert.Equal("print('hi')", engine.LastSpec!.Stdin);
        Assert.Equal("Hello, Ada!", receipt.Fields["message"]);
        var paramsJson = engine.LastSpec.Environment["OKF_PARAMS_JSON"];
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(paramsJson);
        Assert.Equal("Ada", parsed!["name"]!.ToString());
    }

    [Fact]
    public async Task Runs_with_no_network_access()
    {
        var engine = new FakeContainerEngine();
        var executor = new ScriptComputationExecutor(engine, Profile);
        await executor.ExecuteAsync(new BoundComputation("python", "print()", null, new Dictionary<string, object?>()), Contract);

        Assert.Equal("none", engine.LastSpec!.NetworkMode);
    }

    [Fact]
    public async Task A_non_zero_exit_code_throws_ContainerExecutionException_carrying_stderr()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(1, "", "Traceback...") };
        var executor = new ScriptComputationExecutor(engine, Profile);

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await executor.ExecuteAsync(new BoundComputation("python", "raise", null, new Dictionary<string, object?>()), Contract));
        Assert.Equal("Traceback...", ex.Stderr);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~ScriptComputationExecutorTests"`
Expected: FAIL — `ScriptComputationExecutor` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/OKF4net.Attestation.Containers/ScriptComputationExecutor.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Attestation.Containers.Internal;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// <see cref="IComputationExecutor"/> for <see cref="ContainerRuntimeKind.Script"/>
/// profiles. The bound computation text IS the program: it is piped on
/// stdin to <see cref="ContainerRuntimeProfile.Interpreter"/> unchanged, and
/// is expected to print a JSON object on stdout matching
/// <c>executor.receipt</c>'s declared fields. Parameter values reach the
/// script separately, as JSON in the <c>OKF_PARAMS_JSON</c> environment
/// variable — never spliced into the script's own source.
/// </summary>
public sealed class ScriptComputationExecutor(IContainerEngine engine, ContainerRuntimeProfile profile) : IComputationExecutor
{
    /// <inheritdoc />
    public async ValueTask<Receipt> ExecuteAsync(
        BoundComputation bound,
        AttestedComputationContract contract,
        CancellationToken cancellationToken = default)
    {
        var env = new Dictionary<string, string>(profile.Environment)
        {
            ["OKF_PARAMS_JSON"] = JsonSerializer.Serialize(bound.Values),
        };

        var spec = new ContainerRunSpec(
            Image: profile.Image,
            Command: [profile.Interpreter, "-"],
            Stdin: bound.BoundText ?? "",
            Environment: env,
            NetworkMode: "none",
            MemoryBytes: profile.MemoryBytes,
            Cpus: profile.Cpus,
            PidsLimit: profile.PidsLimit,
            Timeout: profile.Timeout);

        var result = await engine.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        return ReceiptParsing.Parse(result, "script");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~ScriptComputationExecutorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Attestation.Containers/ScriptComputationExecutor.cs tests/OKF4net.Tests/Attestation.Containers/ScriptComputationExecutorTests.cs
git commit -m "feat(attestation-containers): add ScriptComputationExecutor"
```

---

## Task 5: `SqlClientComputationExecutor`

Design section: "Executor `SqlClient`".

**Known trade-off, flagged for the meticulous review (discovered while writing this plan):** the design says "pilote pur-Python (pg8000)" but does not say how pg8000 — a third-party PyPI package — gets into an *official, unmodified* `python:3.12-slim` image without a bind mount or a custom image. This task's wrapper resolves that by running `pip install pg8000` as the first thing it does, over the network the `SqlClient` profile already needs to reach the database. That is a real per-run latency and availability cost (a PyPI outage breaks every SqlClient run) not discussed in the design. Vendoring pg8000's source as an embedded resource (the way `OKF4net.Viewer` vendors `marked.min.js`) would remove that cost but is a materially bigger task. This plan takes the `pip install` approach for v1 and calls it out explicitly rather than silently deciding it.

**Files:**
- Create: `src/OKF4net.Attestation.Containers/SqlClientComputationExecutor.cs`
- Test: `tests/OKF4net.Tests/Attestation.Containers/SqlClientComputationExecutorTests.cs`

**Interfaces:**
- Consumes: `IContainerEngine.RunAsync`, `ContainerRuntimeProfile`, `Internal.ReceiptParsing.Parse`.
- Produces: `SqlClientComputationExecutor : IComputationExecutor`, consumed by Task 8.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OKF4net.Tests/Attestation.Containers/SqlClientComputationExecutorTests.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class SqlClientComputationExecutorTests
{
    private static readonly ContainerRuntimeProfile Profile = new()
    {
        Image = "python:3.12-slim",
        Kind = ContainerRuntimeKind.SqlClient,
        Environment = new Dictionary<string, string> { ["OKF_CONN"] = "postgresql://u:p@host/db" },
    };

    private static readonly AttestedComputationContract Contract = new(
        Runtime: "postgres", Parameters: [], ComputationPath: null,
        Executor: new Executor(null, ["executed_sql", "result"]), Attester: null);

    [Fact]
    public async Task Sends_the_bound_SQL_unchanged_with_values_in_the_same_envelope()
    {
        var engine = new FakeContainerEngine
        {
            Respond = _ => new ContainerRunResult(0, """{"executed_sql": "SELECT :x", "result": [{"active_users": 3}]}""", ""),
        };
        var executor = new SqlClientComputationExecutor(engine, Profile);
        var bound = new BoundComputation("postgres", "SELECT :x", null, new Dictionary<string, object?> { ["x"] = 1 });

        var receipt = await executor.ExecuteAsync(bound, Contract);

        var envelope = JsonSerializer.Deserialize<JsonElement>(engine.LastSpec!.Stdin!);
        Assert.Equal("SELECT :x", envelope.GetProperty("sql").GetString());
        Assert.Equal(1, envelope.GetProperty("values").GetProperty("x").GetInt32());
        Assert.Equal("SELECT :x", receipt.Fields["executed_sql"]);
    }

    [Fact]
    public async Task Does_not_restrict_network_access()
    {
        var engine = new FakeContainerEngine();
        var executor = new SqlClientComputationExecutor(engine, Profile);
        await executor.ExecuteAsync(new BoundComputation("postgres", "SELECT 1", null, new Dictionary<string, object?>()), Contract);

        Assert.Null(engine.LastSpec!.NetworkMode);
    }

    [Fact]
    public async Task Passes_the_connection_string_through_as_an_environment_variable()
    {
        var engine = new FakeContainerEngine();
        var executor = new SqlClientComputationExecutor(engine, Profile);
        await executor.ExecuteAsync(new BoundComputation("postgres", "SELECT 1", null, new Dictionary<string, object?>()), Contract);

        Assert.Equal("postgresql://u:p@host/db", engine.LastSpec!.Environment["OKF_CONN"]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~SqlClientComputationExecutorTests"`
Expected: FAIL — `SqlClientComputationExecutor` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/OKF4net.Attestation.Containers/SqlClientComputationExecutor.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Attestation.Containers.Internal;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// <see cref="IComputationExecutor"/> for <see cref="ContainerRuntimeKind.SqlClient"/>
/// profiles. Runs the bound SQL text — byte-for-byte unchanged, placeholders
/// included — through <see cref="Wrapper"/>, a fixed Python program that
/// binds <see cref="BoundComputation.Values"/> via a pure-Python database
/// driver's own native parameter mechanism (never string interpolation into
/// the SQL). <see cref="Wrapper"/> pip-installs its driver at run time —
/// see the plan task's "known trade-off" note.
/// </summary>
public sealed class SqlClientComputationExecutor(IContainerEngine engine, ContainerRuntimeProfile profile) : IComputationExecutor
{
    private const string Wrapper = """
        import sys, os, json, subprocess
        subprocess.run([sys.executable, '-m', 'pip', 'install', '--quiet', 'pg8000'], check=True)
        import pg8000.native
        from urllib.parse import urlparse
        envelope = json.load(sys.stdin)
        sql = envelope['sql']
        values = envelope.get('values') or {}
        u = urlparse(os.environ['OKF_CONN'])
        conn = pg8000.native.Connection(user=u.username, password=u.password, host=u.hostname, port=u.port or 5432, database=u.path.lstrip('/'))
        try:
            rows = conn.run(sql, **values)
            cols = [c['name'] for c in conn.columns] if conn.columns else []
            result = [dict(zip(cols, row)) for row in rows]
            sys.stdout.write(json.dumps({'executed_sql': sql, 'result': result}))
        finally:
            conn.close()
        """;

    /// <inheritdoc />
    public async ValueTask<Receipt> ExecuteAsync(
        BoundComputation bound,
        AttestedComputationContract contract,
        CancellationToken cancellationToken = default)
    {
        var envelope = JsonSerializer.Serialize(new { sql = bound.BoundText ?? "", values = bound.Values });

        var spec = new ContainerRunSpec(
            Image: profile.Image,
            Command: ["python3", "-c", Wrapper],
            Stdin: envelope,
            Environment: profile.Environment,
            NetworkMode: null,
            MemoryBytes: profile.MemoryBytes,
            Cpus: profile.Cpus,
            PidsLimit: profile.PidsLimit,
            Timeout: profile.Timeout);

        var result = await engine.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        return ReceiptParsing.Parse(result, "SQL wrapper");
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~SqlClientComputationExecutorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Attestation.Containers/SqlClientComputationExecutor.cs tests/OKF4net.Tests/Attestation.Containers/SqlClientComputationExecutorTests.cs
git commit -m "feat(attestation-containers): add SqlClientComputationExecutor"
```

---

## Task 6: `ContainerAttester`

Design section: "Attester" (kwargs envelope, bootstrap, output wire-contract finding #9).

**Files:**
- Create: `src/OKF4net.Attestation.Containers/ContainerAttester.cs`
- Test: `tests/OKF4net.Tests/Attestation.Containers/ContainerAttesterTests.cs`

**Interfaces:**
- Consumes: `IContainerEngine.RunAsync`, `ContainerAttesterOptions`, `OKF4net.Attestation.AttestationContext` (including `AttesterSourceText`, from Task 1).
- Produces: `ContainerAttester : IAttester`, consumed by Task 8.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OKF4net.Tests/Attestation.Containers/ContainerAttesterTests.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class ContainerAttesterTests
{
    private static AttestationContext Context(string? attesterSource, Receipt receipt) => new(
        Contract: new AttestedComputationContract("python", [], null, null, new Attester("att.py")),
        Computation: new SanctionedComputation(ComputationSource.Inline, "print()", null),
        Bound: new BoundComputation("python", "print()", null, new Dictionary<string, object?>()),
        Values: new Dictionary<string, object?>(),
        Receipt: receipt,
        AttesterSourceText: attesterSource);

    [Fact]
    public async Task Sends_the_attester_source_and_kwargs_on_stdin_never_via_the_image()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(0, """{"ok": true, "reason": null}""", "") };
        var options = new ContainerAttesterOptions();
        var attester = new ContainerAttester(engine, options);

        var verdict = await attester.AttestAsync(Context("def attest(**_):\n    return {}\n", new Receipt(new Dictionary<string, object?> { ["message"] = "hi" })));

        Assert.True(verdict.Passed);
        var envelope = JsonSerializer.Deserialize<JsonElement>(engine.LastSpec!.Stdin!);
        Assert.Equal("def attest(**_):\n    return {}\n", envelope.GetProperty("attester_source").GetString());
        Assert.Equal(options.Image, engine.LastSpec.Image);
    }

    [Fact]
    public async Task Uses_its_own_fixed_image_never_the_executors()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(0, """{"ok": true}""", "") };
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions { Image = "python:3.13-slim" });

        await attester.AttestAsync(Context("def attest(**_):\n    return {}\n", new Receipt(new Dictionary<string, object?>())));

        Assert.Equal("python:3.13-slim", engine.LastSpec!.Image);
    }

    [Fact]
    public async Task A_missing_verdict_field_is_treated_as_a_failing_verdict()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(0, "{}", "") };
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions());

        var verdict = await attester.AttestAsync(Context("def attest(**_):\n    return {}\n", new Receipt(new Dictionary<string, object?>())));

        Assert.False(verdict.Passed);
    }

    [Fact]
    public async Task Throws_when_the_concept_has_no_resolvable_attester_source()
    {
        var engine = new FakeContainerEngine();
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions());

        await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await attester.AttestAsync(Context(null, new Receipt(new Dictionary<string, object?>()))));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~ContainerAttesterTests"`
Expected: FAIL — `ContainerAttester` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/OKF4net.Attestation.Containers/ContainerAttester.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// <see cref="IAttester"/> for every runtime: an attester script is a
/// Python *library* (e.g. exposing <c>attest(**kwargs)</c>), not a
/// standalone program, so a fixed bootstrap imports it by path and calls a
/// known function with fixed kwarg names — this project's own convention
/// (§10 leaves invocation entirely host-defined). Always runs on
/// <see cref="ContainerAttesterOptions.Image"/>, never the executor's
/// profile image (a <see cref="ContainerRuntimeKind.SqlClient"/> image has
/// no Python at all).
/// </summary>
public sealed class ContainerAttester(IContainerEngine engine, ContainerAttesterOptions options) : IAttester
{
    /// <summary>
    /// Reads the JSON envelope from stdin, writes <c>attester_source</c> to a
    /// temp file inside the container, imports it, and calls
    /// <c>attest(**kwargs)</c>. Redirects stdout to a buffer for the whole
    /// import+call so a stray <c>print()</c> inside the bundle's own module
    /// can never corrupt the one JSON line this prints at the very end (the
    /// design's finding #9).
    /// </summary>
    private const string Bootstrap = """
        import sys, json, importlib.util, tempfile, io, contextlib
        envelope = json.load(sys.stdin)
        f = tempfile.NamedTemporaryFile(suffix='.py', delete=False, mode='w')
        f.write(envelope['attester_source'])
        f.close()
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            spec = importlib.util.spec_from_file_location('okf_attester', f.name)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            result = module.attest(**envelope['kwargs'])
        sys.stdout.write(json.dumps(result))
        """;

    /// <inheritdoc />
    public async ValueTask<AttestationVerdict> AttestAsync(AttestationContext context, CancellationToken cancellationToken = default)
    {
        if (context.AttesterSourceText is null)
        {
            throw new ContainerExecutionException("concept has no resolvable attester.resource", "", "");
        }

        var envelope = JsonSerializer.Serialize(new
        {
            attester_source = context.AttesterSourceText,
            kwargs = new
            {
                sanctioned_computation = context.Computation.InlineCode,
                receipt = context.Receipt.Fields,
                values = context.Values,
            },
        });

        var spec = new ContainerRunSpec(
            Image: options.Image,
            Command: ["python3", "-c", Bootstrap],
            Stdin: envelope,
            Environment: options.Environment,
            NetworkMode: "none",
            MemoryBytes: options.MemoryBytes,
            Cpus: options.Cpus,
            PidsLimit: options.PidsLimit,
            Timeout: options.Timeout);

        var result = await engine.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ContainerExecutionException($"attester exited with code {result.ExitCode}", result.Stdout, result.Stderr);
        }

        JsonElement verdict;
        try
        {
            verdict = JsonSerializer.Deserialize<JsonElement>(result.Stdout);
        }
        catch (JsonException e)
        {
            throw new ContainerExecutionException($"attester stdout was not valid JSON: {e.Message}", result.Stdout, result.Stderr);
        }

        var passed = verdict.ValueKind == JsonValueKind.Object
            && verdict.TryGetProperty("ok", out var okProp)
            && okProp.ValueKind == JsonValueKind.True;
        var detail = verdict.ValueKind == JsonValueKind.Object
            && verdict.TryGetProperty("reason", out var reasonProp)
            && reasonProp.ValueKind == JsonValueKind.String
                ? reasonProp.GetString()
                : null;
        return new AttestationVerdict(passed, detail);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~ContainerAttesterTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Attestation.Containers/ContainerAttester.cs tests/OKF4net.Tests/Attestation.Containers/ContainerAttesterTests.cs
git commit -m "feat(attestation-containers): add ContainerAttester with stdout-safe bootstrap"
```

---

## Task 7: `ContainerAttestationRuntime` (wiring)

Design section: component table row for `ContainerAttestationRuntime`.

**Files:**
- Create: `src/OKF4net.Attestation.Containers/ContainerAttestationRuntime.cs`
- Test: `tests/OKF4net.Tests/Attestation.Containers/ContainerAttestationRuntimeTests.cs`

**Interfaces:**
- Consumes: `AllowlistParameterBinder` (Task 3), `ScriptComputationExecutor` (Task 4), `SqlClientComputationExecutor` (Task 5), `ContainerAttester` (Task 6).
- Produces: `ContainerAttestationRuntime : IAttestationRuntime`. This is the library's public entry point — a host constructs one per bundle `runtime` name and registers it in `AttestationRuntimeRegistry`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OKF4net.Tests/Attestation.Containers/ContainerAttestationRuntimeTests.cs
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~ContainerAttestationRuntimeTests"`
Expected: FAIL — `ContainerAttestationRuntime` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/OKF4net.Attestation.Containers/ContainerAttestationRuntime.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers;

/// <summary>
/// The library's entry point: an <see cref="IAttestationRuntime"/> for one
/// bundle <c>runtime</c> name, backed entirely by containers. A host
/// constructs one per profile and registers it in
/// <c>AttestationRuntimeRegistry</c> — see the design doc's "Bundle de
/// validation" section (Tasks 12/13) for a worked example.
/// </summary>
public sealed class ContainerAttestationRuntime : IAttestationRuntime
{
    /// <summary>
    /// Wires <paramref name="profile"/>'s executor (chosen by
    /// <see cref="ContainerRuntimeProfile.Kind"/>) and the shared
    /// <see cref="AllowlistParameterBinder"/>, plus a <see cref="ContainerAttester"/>
    /// configured by <paramref name="attesterOptions"/> (defaulted when
    /// omitted — the attester's image is never <paramref name="profile"/>'s).
    /// </summary>
    public ContainerAttestationRuntime(IContainerEngine engine, ContainerRuntimeProfile profile, ContainerAttesterOptions? attesterOptions = null)
    {
        Binder = new AllowlistParameterBinder();
        Executor = profile.Kind switch
        {
            ContainerRuntimeKind.Script => new ScriptComputationExecutor(engine, profile),
            ContainerRuntimeKind.SqlClient => new SqlClientComputationExecutor(engine, profile),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile.Kind, "unknown ContainerRuntimeKind"),
        };
        Attester = new ContainerAttester(engine, attesterOptions ?? new ContainerAttesterOptions());
    }

    /// <inheritdoc />
    public IParameterBinder Binder { get; }

    /// <inheritdoc />
    public IComputationExecutor Executor { get; }

    /// <inheritdoc />
    public IAttester Attester { get; }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~ContainerAttestationRuntimeTests"`
Expected: PASS.

- [ ] **Step 5: Write and run an end-to-end test against `AttestationOrchestrator` (still no real Docker)**

```csharp
// Append to tests/OKF4net.Tests/Attestation.Containers/ContainerAttestationRuntimeTests.cs

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
```

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~ContainerAttestationRuntimeTests"`
Expected: PASS — this is the first test proving Tasks 1–7 work together through the real `AttestationOrchestrator`, still without touching Docker.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Attestation.Containers/ContainerAttestationRuntime.cs tests/OKF4net.Tests/Attestation.Containers/ContainerAttestationRuntimeTests.cs
git commit -m "feat(attestation-containers): add ContainerAttestationRuntime wiring"
```

---

## Task 8: `CliContainerEngine` — argument construction (unit-tested, no Docker)

Design section: "Contrainte d'implémentation explicite" (ArgumentList-only); resource-limit/`--network` bullets.

**Files:**
- Create: `src/OKF4net.Attestation.Containers/CliContainerEngine.cs` (this task adds only the internal argument builder — `RunAsync` itself is Task 9)
- Test: `tests/OKF4net.Tests/Attestation.Containers/CliContainerEngineArgumentsTests.cs`

**Interfaces:**
- Produces: `internal static CliContainerEngine.BuildRunArguments(ContainerRunSpec, string containerName) -> IReadOnlyList<string>` — a pure function, unit-tested directly via the `InternalsVisibleTo` grant from Task 2. Consumed by Task 9's `RunAsync`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/OKF4net.Tests/Attestation.Containers/CliContainerEngineArgumentsTests.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class CliContainerEngineArgumentsTests
{
    private static ContainerRunSpec Spec(
        string image = "python:3.12-slim",
        IReadOnlyDictionary<string, string>? env = null,
        string? network = "none",
        long? memory = 512L * 1024 * 1024,
        double? cpus = 1.0,
        int? pids = 64)
        => new(image, ["python3", "-"], "print()", env ?? new Dictionary<string, string>(), network, memory, cpus, pids, TimeSpan.FromMinutes(1));

    [Fact]
    public void Every_argument_is_its_own_array_element_never_one_concatenated_string()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(env: new Dictionary<string, string> { ["X"] = "a b; rm -rf /" }), "okf-abc");
        // The hostile-looking value must appear as ONE element, never split
        // or interpreted -- proving no shell ever sees this as source text.
        Assert.Contains("X=a b; rm -rf /", args);
        Assert.DoesNotContain(args, a => a.Contains("&&") || a.Contains("|"));
    }

    [Fact]
    public void Includes_run_rm_and_a_unique_name()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(), "okf-xyz");
        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        var nameIndex = args.ToList().IndexOf("--name");
        Assert.True(nameIndex >= 0);
        Assert.Equal("okf-xyz", args[nameIndex + 1]);
    }

    [Fact]
    public void Includes_resource_limit_flags_with_their_configured_values()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(memory: 256L * 1024 * 1024, cpus: 0.5, pids: 32), "okf-1");
        Assert.Contains("--memory", args);
        Assert.Contains((256L * 1024 * 1024).ToString(), args);
        Assert.Contains("--cpus", args);
        Assert.Contains("0.5", args);
        Assert.Contains("--pids-limit", args);
        Assert.Contains("32", args);
    }

    [Fact]
    public void Omits_network_flag_when_NetworkMode_is_null()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(network: null), "okf-1");
        Assert.DoesNotContain("--network", args);
    }

    [Fact]
    public void Ends_with_the_image_then_the_command()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(image: "postgres:16-alpine"), "okf-1");
        var imageIndex = args.ToList().IndexOf("postgres:16-alpine");
        Assert.True(imageIndex >= 0);
        Assert.Equal("python3", args[imageIndex + 1]);
        Assert.Equal("-", args[imageIndex + 2]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~CliContainerEngineArgumentsTests"`
Expected: FAIL — `CliContainerEngine` does not exist.

- [ ] **Step 3: Implement the argument builder**

```csharp
// src/OKF4net.Attestation.Containers/CliContainerEngine.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// The only <see cref="IContainerEngine"/> implementation shipped here:
/// shells to a Docker-CLI-compatible binary (<c>docker</c>, <c>podman</c>,
/// or <c>nerdctl</c> — their <c>run</c> surface is compatible, so one
/// parameterized class covers all three). This task adds only
/// <see cref="BuildRunArguments"/>, the pure part: it never spawns a
/// process, so it is unit-tested directly without Docker. It deliberately
/// does NOT declare <c>: IContainerEngine</c> yet — that interface requires
/// a <c>RunAsync</c> method, added in Task 9 by editing this same file
/// further (not a `partial` split, there is only ever one file); claiming
/// the interface here without it would fail to compile (CS0535, a missing
/// interface member), not just warn.
/// </summary>
public sealed class CliContainerEngine(string binaryName = "docker")
{
    /// <summary>
    /// Builds the <c>run</c> argument list for <paramref name="spec"/>. Every
    /// value (image, env vars, resource limits, the container name) is its
    /// own array element — never concatenated into one string — because this
    /// list is fed straight into <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// (Task 9), which passes each element to the child process verbatim,
    /// with no shell involved anywhere in this project's own process.
    /// </summary>
    internal static IReadOnlyList<string> BuildRunArguments(ContainerRunSpec spec, string containerName)
    {
        var args = new List<string> { "run", "-i", "--rm", "--name", containerName };

        if (spec.NetworkMode is { } network)
        {
            args.Add("--network");
            args.Add(network);
        }

        if (spec.MemoryBytes is { } memory)
        {
            args.Add("--memory");
            args.Add(memory.ToString(CultureInfo.InvariantCulture));
        }

        if (spec.Cpus is { } cpus)
        {
            args.Add("--cpus");
            args.Add(cpus.ToString(CultureInfo.InvariantCulture));
        }

        if (spec.PidsLimit is { } pids)
        {
            args.Add("--pids-limit");
            args.Add(pids.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var (key, value) in spec.Environment)
        {
            args.Add("-e");
            args.Add($"{key}={value}");
        }

        args.Add(spec.Image);
        args.AddRange(spec.Command);
        return args;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/OKF4net.Tests --filter "FullyQualifiedName~CliContainerEngineArgumentsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Attestation.Containers/CliContainerEngine.cs tests/OKF4net.Tests/Attestation.Containers/CliContainerEngineArgumentsTests.cs
git commit -m "feat(attestation-containers): add CliContainerEngine's argument builder"
```

---

## Task 9: `CliContainerEngine` — real execution (`RunAsync`)

Design section: "Gestion d'erreurs & sécurité" in full — cancellation identity, create/kill race, timeout, bounded concurrent stdout/stderr draining.

**No unit test in this task.** `RunAsync` spawns a real process against a real container engine; the design deliberately keeps that tier of verification out of CI (mirrors `producers/`). Task 11 is where this code is actually exercised, against real Docker, manually. Writing a fake-executable-based unit test here would only prove this code can drive *some* subprocess, not that it correctly drives a container engine — not worth the complexity for what it would prove.

**Files:**
- Modify: `src/OKF4net.Attestation.Containers/CliContainerEngine.cs`
- Modify: `CLAUDE.md` (document the new project in the Architecture section)

**Interfaces:**
- Produces: `CliContainerEngine.RunAsync` (the `IContainerEngine` member), completing the class. Consumed transitively by every executor/attester once a host constructs a real `CliContainerEngine` instead of `FakeContainerEngine`.

- [ ] **Step 1: Declare the interface, then implement `RunAsync` and its private helpers**

First, change the class declaration line (Task 8 deliberately left the interface off, since `RunAsync` didn't exist yet — see that task's doc-comment note):

```csharp
public sealed class CliContainerEngine(string binaryName = "docker")
```

becomes:

```csharp
public sealed class CliContainerEngine(string binaryName = "docker") : IContainerEngine
```

Then add to `src/OKF4net.Attestation.Containers/CliContainerEngine.cs` (inside the existing `CliContainerEngine` class body, after `BuildRunArguments`):

```csharp
    /// <summary>Stdout/stderr are each capped at 8 MiB; a container that floods either past this is a stage failure, not an OOM.</summary>
    private const int MaxOutputBytes = 8 * 1024 * 1024;

    /// <inheritdoc />
    public async ValueTask<ContainerRunResult> RunAsync(ContainerRunSpec spec, CancellationToken cancellationToken = default)
    {
        var containerName = $"okf-{Guid.NewGuid():N}";
        var psi = new ProcessStartInfo
        {
            FileName = binaryName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in BuildRunArguments(spec, containerName))
        {
            psi.ArgumentList.Add(arg);
        }

        using var timeoutCts = spec.Timeout is { } timeout ? new CancellationTokenSource(timeout) : null;
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdinTask = WriteStdinAsync(process.StandardInput, spec.Stdin);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxOutputBytes);
        var stderrTask = ReadBoundedAsync(process.StandardError, MaxOutputBytes);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillContainerAsync(containerName).ConfigureAwait(false);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // The CALLER asked to stop -- propagate a real
                // OperationCanceledException tied to their own token, so
                // AttestationOrchestrator's IsCallerCancellation recognises
                // it and rethrows rather than converting it into an
                // ordinary Fail(...) outcome.
                throw new OperationCanceledException("container run was cancelled by the caller", cancellationToken);
            }

            // Otherwise this project's OWN timeout fired -- a genuine stage
            // failure, reported the same way a non-zero exit code is.
            throw new ContainerExecutionException(
                "container run exceeded its timeout",
                await SafeAwaitAsync(stdoutTask).ConfigureAwait(false),
                await SafeAwaitAsync(stderrTask).ConfigureAwait(false));
        }

        await stdinTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ContainerRunResult(process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Retries <c><paramref name="binaryName"/> kill</c> once after a short
    /// delay, best-effort: a container whose creation was still in flight
    /// when the first attempt ran reports "no such container" and is caught
    /// by the retry once it actually starts. Never throws -- a failure here
    /// only means <see cref="RunAsync"/> also calls <see cref="Process.Kill(bool)"/>
    /// on its own local process, which is the other half of teardown.
    /// </summary>
    private async Task KillContainerAsync(string containerName)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var kill = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = binaryName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            kill.StartInfo.ArgumentList.Add("kill");
            kill.StartInfo.ArgumentList.Add(containerName);

            try
            {
                kill.Start();
                await kill.WaitForExitAsync().ConfigureAwait(false);
                if (kill.ExitCode == 0)
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Best-effort teardown; Process.Kill on the local process
                // handles the case where the engine binary itself is gone.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes <paramref name="input"/> then closes the stream. A container
    /// that exits before consuming all of stdin closes its end of the pipe
    /// first, which surfaces here as an <see cref="IOException"/> ("broken
    /// pipe") -- an expected occurrence (a script that errors out early),
    /// not a reason to let a raw exception replace the container's actual
    /// exit code and output in <see cref="RunAsync"/>. Swallowed here;
    /// <see cref="RunAsync"/>'s own exit-code check reports the real
    /// failure.
    /// </summary>
    private static async Task WriteStdinAsync(StreamWriter writer, string? input)
    {
        try
        {
            if (!string.IsNullOrEmpty(input))
            {
                await writer.WriteAsync(input).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
        }
        finally
        {
            try
            {
                writer.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Drains <paramref name="reader"/> to its end regardless of
    /// <paramref name="maxBytes"/>, so the child's pipe never backs up and
    /// blocks it — but only the first <paramref name="maxBytes"/> characters
    /// are kept. Runs concurrently with the other stream and with the stdin
    /// write in <see cref="RunAsync"/>, which is what actually avoids the
    /// classic redirected-pipe deadlock.
    /// </summary>
    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes)
    {
        var buffer = new char[8192];
        var sb = new StringBuilder();
        var total = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            var toKeep = Math.Max(0, Math.Min(read, maxBytes - total));
            if (toKeep > 0)
            {
                sb.Append(buffer, 0, toKeep);
                total += toKeep;
            }
        }

        return sb.ToString();
    }

    private static async Task<string> SafeAwaitAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }
```

Add the required usings at the top of the file:

```csharp
using System.Diagnostics;
using System.Text;
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build OKF4net.sln`
Expected: 0 warnings, 0 errors.

- [ ] **Step 3: Document the new project in `CLAUDE.md`**

In `CLAUDE.md`'s Architecture section, immediately after the existing `src/OKF4net.Attestation/` bullet, add:

```markdown
- **`src/OKF4net.Attestation.Containers/`** — a container-based host implementation of §10's contracts: runs the *actual* sanctioned script/SQL and attester script a bundle references in a real container (Docker/Podman/nerdctl via a single `CliContainerEngine(binaryName)`), never a C# reimplementation. `AllowlistParameterBinder` is shared by both `ContainerRuntimeKind`s and never edits `BoundComputation.BoundText` — parameter values travel separately (a JSON env var for `Script`, native driver binding for `SqlClient`), because textual substitution both reopens injection and breaks provenance comparisons that expect a sanctioned placeholder (e.g. BigQuery's `@name`) to survive verbatim into a receipt's `executed_sql`. No container is ever given a bind-mounted volume — every payload (script text, SQL text, attester module source) travels over stdin; the in-container command is always a short, fixed string. `ContainerAttester` always runs on its own fixed Python image (`ContainerAttesterOptions`), independent of whatever image an executor's `ContainerRuntimeProfile` uses. Depended on by nothing yet in `src/` — see `samples/attestation-containers-demo/` for a worked end-to-end example. References only `OKF4net.Attestation`, zero third-party `PackageReference`; requires a container engine binary on `PATH` at run time (a new *runtime* prerequisite for this repo, not a NuGet dependency).
```

- [ ] **Step 4: Run the full test suite once more**

Run: `dotnet test OKF4net.sln`
Expected: PASS, no regressions.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Attestation.Containers/CliContainerEngine.cs CLAUDE.md
git commit -m "feat(attestation-containers): implement CliContainerEngine.RunAsync"
```

---

## Task 10: Docker integration tests (local-only, excluded from CI)

Design section: "Tests" — "Intégration réelle... hors CI".

**Files:**
- Create: `tests/OKF4net.Tests/Attestation.Containers/ContainerIntegrationTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–9, against a real `CliContainerEngine`.

- [ ] **Step 1: Write the integration test class**

```csharp
// tests/OKF4net.Tests/Attestation.Containers/ContainerIntegrationTests.cs
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
/// and set <c>OKF_DEMO_PG_CONN=postgresql://postgres:demo@host.docker.internal:5544/demo</c>
/// before running these tests -- <c>host.docker.internal</c>, not
/// <c>localhost</c>: the connection string is read by the .NET test process
/// on the host, but consumed *inside* the SqlClient container this test
/// spins up, where <c>localhost</c> means that container's own loopback,
/// not the host's. On native Linux Docker Engine (not Docker Desktop) this
/// hostname may need the daemon started with <c>--add-host=host.docker.internal:host-gateway</c>
/// support, or substitute the host's real LAN/bridge IP instead. Stop the
/// fixture afterwards with <c>docker stop okf-demo-pg</c>.
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
```

Requires the `Xunit.SkippableFact` package for the `[SkippableFact]`/`Skip.IfNot` idiom used above. Add it, test-only, to `tests/OKF4net.Tests/OKF4net.Tests.csproj`:

```xml
<PackageReference Include="Xunit.SkippableFact" Version="1.4.13" />
```

(Test-only packages are allowed everywhere per `CLAUDE.md`'s dependency rule — this does not affect any `src/` project.)

- [ ] **Step 2: Run it locally (requires Docker; start the Postgres fixture first per the class doc comment)**

Run:
```bash
docker run --rm -d --name okf-demo-pg -e POSTGRES_PASSWORD=demo -e POSTGRES_DB=demo -p 5544:5432 postgres:16-alpine
sleep 3
docker exec -i okf-demo-pg psql -U postgres -d demo -c "CREATE TABLE users(id int, active boolean); INSERT INTO users VALUES (1,true),(2,true),(3,false);"
OKF_DEMO_PG_CONN=postgresql://postgres:demo@host.docker.internal:5544/demo dotnet test tests/OKF4net.Tests --filter "Category=ContainerIntegration"
docker stop okf-demo-pg
```
Expected: PASS. If it fails, fix the implementation from Tasks 1–9 (not this test) and re-run — this is where real container behavior (resource limits actually enforced, timeout/cancellation actually killing the container, the pg8000 pip-install-at-runtime approach actually working) gets its only real verification.

- [ ] **Step 3: Exclude this category from `ci.yml` explicitly — the runtime `Skip.IfNot` check alone is not CI-safe**

`Skip.IfNot(DockerAvailable(), ...)` only checks that a `docker` binary answers `--version` — it says nothing about whether Linux containers actually work. GitHub's `ubuntu-latest` runners ship a genuinely working Docker Engine, so without an explicit exclusion these tests would actually execute on every push (pulling `python:3.12-slim` over the network on every CI run — exactly what "local-only, manually-run" is meant to avoid), and `windows-latest` runners default to Windows containers, where `docker run python:3.12-slim` would very plausibly fail outright (an image platform mismatch) rather than skip — surfacing as a real CI test failure, not a clean skip, since `CliContainerEngine.RunAsync` returns a `ContainerRunResult` rather than throwing on a non-zero exit code.

In `.github/workflows/ci.yml`, change the `Test` step under the `build-test` job from:

```yaml
      - name: Test
        run: dotnet test OKF4net.sln -c Release --no-build
```

to:

```yaml
      - name: Test
        run: dotnet test OKF4net.sln -c Release --no-build --filter "Category!=ContainerIntegration"
```

This is a structural exclusion (like `producers/` being a separate solution CI never builds), not a hope that `Skip.IfNot` guesses right on every runner OS.

- [ ] **Step 4: Verify both the CI-safe filtered run and the local convenience run**

Run the exact filtered command CI will now run:
```bash
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"
```
Expected: PASS, and the three `ContainerIntegration` tests do not appear at all (excluded, not skipped — check the reported total test count dropped by 3 compared to an unfiltered run).

Then run the plain local command a developer would use:
```bash
dotnet test OKF4net.sln
```
Expected: PASS; the three tests either skip cleanly (no Docker) or actually run against Docker if it's present locally (this environment has Docker, so they should genuinely pass here, not just skip).

- [ ] **Step 5: Commit**

```bash
git add tests/OKF4net.Tests/Attestation.Containers/ContainerIntegrationTests.cs tests/OKF4net.Tests/OKF4net.Tests.csproj .github/workflows/ci.yml
git commit -m "test(attestation-containers): add local-only Docker integration tests"
```

---

## Task 11: Validation bundle `bundles/attestation_containers_demo/`

Design section: "Bundle de validation".

**Files:**
- Create: `bundles/attestation_containers_demo/README.md`
- Create: `bundles/attestation_containers_demo/index.md`
- Create: `bundles/attestation_containers_demo/computations/index.md`
- Create: `bundles/attestation_containers_demo/computations/greeting.md`
- Create: `bundles/attestation_containers_demo/computations/active-user-count.md`
- Create: `bundles/attestation_containers_demo/attesters/index.md`
- Create: `bundles/attestation_containers_demo/attesters/greeting_attester.py`
- Create: `bundles/attestation_containers_demo/attesters/active_user_count_attester.py`

**Interfaces:** none — this is content, not code, validated by running `okf validate` and by Task 12's sample project.

- [ ] **Step 1: Write the bundle root**

```markdown
<!-- bundles/attestation_containers_demo/README.md -->
# attestation_containers_demo

A small OKF v0.2 bundle, written from scratch (not copied from any
upstream source), to validate `OKF4net.Attestation.Containers`'s invocation
convention end to end. Its attesters are written against
`AttestationContext`'s real shape (`sanctioned_computation`, `receipt`,
`values`) — deliberately **not** copying `bundles/acme_retail/attesters/sql_equality.py`'s
signature, which expects a `claimed_value` that has no counterpart in
`AttestationContext` (see
`docs/superpowers/specs/2026-09-07-attestation-containers-design.md`'s
Attester section for why).
```

```markdown
<!-- bundles/attestation_containers_demo/index.md -->
---
type: Bundle
title: Attestation Containers Demo
description: Demonstrates OKF4net.Attestation.Containers against two runtimes (python, postgres).
---

# attestation_containers_demo

- [computations/](computations/index.md)
- [attesters/](attesters/index.md)
```

```markdown
<!-- bundles/attestation_containers_demo/computations/index.md -->
---
type: Index
title: Computations
description: Attested Computation concepts demonstrating the Script and SqlClient container runtimes.
---

# Computations

- [greeting.md](greeting.md) — `runtime: python`
- [active-user-count.md](active-user-count.md) — `runtime: postgres`
```

```markdown
<!-- bundles/attestation_containers_demo/attesters/index.md -->
---
type: Index
title: Attesters
description: Deterministic verification scripts for this bundle's computations.
---

# Attesters

- [greeting_attester.py](greeting_attester.py)
- [active_user_count_attester.py](active_user_count_attester.py)
```

- [ ] **Step 2: Write the `python` runtime computation**

````markdown
<!-- bundles/attestation_containers_demo/computations/greeting.md -->
---
type: Attested Computation
title: Greeting message
description: Sanctioned Python script that builds a deterministic greeting for a name, demonstrating the Script container runtime.
tags: [demo, attestation, containers]
runtime: python
parameters:
  - { name: name, type: string, required: true }
executor:
  receipt: [message]
attester:
  resource: /attesters/greeting_attester.py
---

# Computation

```python
import json, os

params = json.loads(os.environ["OKF_PARAMS_JSON"])
name = params.get("name", "world")
print(json.dumps({"message": f"Hello, {name}!"}))
```
````

- [ ] **Step 3: Write its attester**

```python
# bundles/attestation_containers_demo/attesters/greeting_attester.py
"""Deterministic attester for computations/greeting.md (runtime: python).

Written against OKF4net.Attestation.Containers's real invocation contract:
the container bootstrap calls attest(sanctioned_computation=..., receipt=...,
values=...). This is NOT the same signature as
bundles/acme_retail/attesters/sql_equality.py, which expects claimed_value --
a consumer-side notion with no counterpart in AttestationContext.
"""


def attest(*, sanctioned_computation, receipt, values):
    name = values.get("name", "world")
    expected = f"Hello, {name}!"
    actual = receipt.get("message")
    if actual != expected:
        return {"ok": False, "reason": f"expected {expected!r}, got {actual!r}"}
    return {"ok": True, "reason": None}
```

- [ ] **Step 4: Write the `postgres` runtime computation**

````markdown
<!-- bundles/attestation_containers_demo/computations/active-user-count.md -->
---
type: Attested Computation
title: Active user count
description: Sanctioned SQL that counts active users at or above a given id, demonstrating the SqlClient container runtime against a real Postgres database.
tags: [demo, attestation, containers]
runtime: postgres
parameters:
  - { name: min_id, type: integer, required: true }
executor:
  receipt: [executed_sql, result]
attester:
  resource: /attesters/active_user_count_attester.py
---

# Computation

```sql
SELECT count(*) AS active_users FROM users WHERE active = true AND id >= :min_id
```

Placeholder syntax is `:name` (pg8000's native binding style), not BigQuery's
`@name` — the two demo runtimes each use the placeholder syntax their own
driver actually binds; there is no single universal OKF placeholder syntax.
````

- [ ] **Step 5: Write its attester**

```python
# bundles/attestation_containers_demo/attesters/active_user_count_attester.py
"""Deterministic attester for computations/active-user-count.md (runtime: postgres).

Verifies provenance (the executed SQL matches the sanctioned text, modulo
whitespace) and that the reported count is a non-negative integer. See
greeting_attester.py's docstring for why this does not follow
bundles/acme_retail/attesters/sql_equality.py's signature.
"""

import re


def _canonicalize(sql):
    return re.sub(r"\s+", " ", sql).strip()


def attest(*, sanctioned_computation, receipt, values):
    executed = receipt.get("executed_sql")
    if executed is None or _canonicalize(executed) != _canonicalize(sanctioned_computation):
        return {"ok": False, "reason": "executed SQL does not match the sanctioned computation"}

    result = receipt.get("result") or []
    if not result or "active_users" not in result[0]:
        return {"ok": False, "reason": "receipt is missing the active_users column"}

    count = result[0]["active_users"]
    if not isinstance(count, int) or count < 0:
        return {"ok": False, "reason": f"active_users is not a non-negative integer: {count!r}"}

    return {"ok": True, "reason": None}
```

- [ ] **Step 6: Validate the bundle**

**Note on the leading `/` in both `attester.resource` values above (discovered empirically while writing this plan, verify you kept it):** `computations/` and `attesters/` are sibling directories under the bundle root. §6.2's *concept-relative* resolution (a resource path with no leading `/`) resolves against the **referencing concept's own directory**, not the bundle root — so a bare `attesters/greeting_attester.py` written inside `computations/greeting.md` would resolve to the nonexistent `computations/attesters/greeting_attester.py`. This is confirmed empirically: `bundles/acme_retail` uses this exact bare-path pattern for its own sibling `attesters/` directory, and running `dotnet run --project src/OKF4net.Cli -- validate bundles/acme_retail` on this very branch reports `frontmatter path 'attester.resource' → 'attesters/sql_equality.py' not found` — a real, pre-existing latent bug in the upstream reference bundle that nothing exercised until this plan's Task 1 wired up attester-resource resolution for the first time. The leading `/` (bundle-root-relative resolution) is what makes this bundle's own references actually resolve.

Run: `dotnet run --project src/OKF4net.Cli -- validate bundles/attestation_containers_demo`
Expected: no errors; any warnings printed must be understood and either fixed or knowingly accepted (e.g. there should be none here — both concepts declare `type`/`title`/`description`, `executor.receipt` is a proper list, and both `attester.resource` values are bundle-root-relative per the note above).

- [ ] **Step 7: Commit**

```bash
git add bundles/attestation_containers_demo
git commit -m "docs(bundles): add attestation_containers_demo validation bundle"
```

---

## Task 12: Sample project `samples/attestation-containers-demo/`

Design section: "Bundle de validation" (the accompanying sample project).

**Files:**
- Create: `samples/attestation-containers-demo/AttestationContainersDemo.sln`
- Create: `samples/attestation-containers-demo/AttestationContainersDemo.csproj`
- Create: `samples/attestation-containers-demo/Program.cs`
- Create: `samples/attestation-containers-demo/README.md`

**Interfaces:** none new — this composes Tasks 1–11 as a runnable console demo. Own solution, not part of `OKF4net.sln`/CI, matching the existing `samples/acme-retail-agent` and `samples/catalog-explorer` convention.

- [ ] **Step 1: Write the project file**

```xml
<!-- samples/attestation-containers-demo/AttestationContainersDemo.csproj -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>OKF4net.Samples.AttestationContainersDemo</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\OKF4net\OKF4net.csproj" />
    <ProjectReference Include="..\..\src\OKF4net.Attestation\OKF4net.Attestation.csproj" />
    <ProjectReference Include="..\..\src\OKF4net.Attestation.Containers\OKF4net.Attestation.Containers.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the solution file**

Run:
```bash
cd samples/attestation-containers-demo
dotnet new sln -n AttestationContainersDemo
dotnet sln AttestationContainersDemo.sln add AttestationContainersDemo.csproj
cd ../..
```

- [ ] **Step 3: Write `Program.cs`**

```csharp
// samples/attestation-containers-demo/Program.cs
// SPDX-License-Identifier: LGPL-3.0-or-later
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
```

- [ ] **Step 4: Write the README**

````markdown
<!-- samples/attestation-containers-demo/README.md -->
# attestation-containers-demo

Runs `bundles/attestation_containers_demo/` end to end through
`OKF4net.Attestation.Containers`. Own solution, not part of `OKF4net.sln` or
CI (same convention as `samples/acme-retail-agent` and
`samples/catalog-explorer`).

## Prerequisites

- Docker (or Podman/nerdctl) on `PATH`.
- For the `postgres` runtime: a reachable Postgres instance with a `users(id int, active boolean)` table, and `OKF_DEMO_PG_CONN` set to its connection string. Without it, this demo only runs the `python` runtime.

## Run

```bash
dotnet run --project samples/attestation-containers-demo
```
````

- [ ] **Step 5: Run it (requires Docker)**

Run: `dotnet run --project samples/attestation-containers-demo/AttestationContainersDemo.csproj`
Expected: prints `displayable: True` and `message: Hello, Ada!` for the `python` runtime. If `OKF_DEMO_PG_CONN` is unset, the postgres section is skipped, as printed.

- [ ] **Step 6: Confirm it stays outside the main build**

Run: `dotnet build OKF4net.sln` (from the repo root)
Expected: succeeds without touching `samples/attestation-containers-demo/` at all — it is not referenced by `OKF4net.sln`.

- [ ] **Step 7: Commit**

```bash
git add samples/attestation-containers-demo
git commit -m "docs(samples): add attestation-containers-demo sample project"
```

---

## Self-Review Notes

**Spec coverage:** every design-doc section has a task — "Architecture & dépendances" → Tasks 2/3; "Principe transversal" → enforced by Tasks 4–6's use of stdin/no-mount; "Protocoles d'exécution" (Script/SqlClient/Attester) → Tasks 4/5/6; "Gestion d'erreurs & sécurité" → Tasks 8/9; "Tests" → Tasks 1–10 (unit) + Task 10 (integration); "Bundle de validation" → Tasks 11/12; the round-2 "Résolution de `attester.resource`" → Task 1.

**Deviations from the design doc, both flagged inline above and repeated here for visibility:**
1. **One binder, not two** (Task 3). The design names `ScriptParameterBinder`/`SqlClientParameterBinder`; implementing them revealed they'd be identical, so this plan ships one `AllowlistParameterBinder` instead. No behavior change, no reopened defect — just DRY.
2. **pg8000 via `pip install` at container start** (Task 5). The design says "pilote pur-Python" but does not say how a third-party package reaches an official, unmodified image without a bind mount. This plan pip-installs it over the network the `SqlClient` profile already needs, accepting a real per-run latency/availability cost the design doc never priced in. Flagged for the meticulous review that follows this plan.

**Placeholder scan:** no TBD/TODO; every code step has real code; no step says "similar to Task N" without repeating the actual content.

**Type consistency check:** `ContainerRunSpec`/`ContainerRunResult` (Task 2) are used with the same shape in Tasks 4, 5, 6, 8, 9. `ContainerRuntimeProfile`'s property names (`Image`, `Kind`, `Interpreter`, `Environment`, `MemoryBytes`, `Cpus`, `PidsLimit`, `Timeout`) are used identically in Tasks 4, 5, 7, 9, 11, 12. `AttestationContext.AttesterSourceText` (Task 1) matches its use in Task 6. `ContainerExecutionException(message, stdout, stderr)`'s constructor order matches every call site in Tasks 4, 5, 6, and `Internal.ReceiptParsing.Parse` (Task 2), which is now the only place that constructs it for a non-zero-exit or malformed-JSON failure. `Internal.ReceiptParsing.Parse(ContainerRunResult, string stageName) -> Receipt` (Task 2) is used identically by Tasks 4 (`"script"`) and 5 (`"SQL wrapper"`) — added during the SDD pre-flight scan because Tasks 4 and 5 originally duplicated this exact logic verbatim, differing only in that string.

**Meticulous review (round 2), after this plan was first written — findings fixed in place, not left as notes:**

1. **Rendering bug in this document itself:** three of Task 11/12's markdown-file-content blocks nested a ` ``` ` fence inside another ` ``` ` fence (a `.md` file's own `python`/`sql`/`bash` fence, shown inside a ` ```markdown ` block). Per CommonMark, the inner fence's closing ` ``` ` line closes the *outer* fence early, corrupting everything after it. Fixed by widening the outer fence to four backticks in all three spots.
2. **Robustness gap in `CliContainerEngine.RunAsync` (Task 9):** `WriteStdinAsync` let an `IOException` (broken pipe — a container that exits before reading all of stdin, an ordinary occurrence, not exceptional) propagate uncaught, which would have replaced `RunAsync`'s normal `ContainerRunResult` return (carrying the container's real exit code and output) with a raw, uninformative exception. Now caught and swallowed; the exit-code check downstream reports the real failure.
3. **A test that didn't test its own claim (Task 7):** `The_attester_defaults_to_a_fixed_python_image_regardless_of_profile` only asserted `Assert.IsType<ContainerAttester>(...)` — true regardless of which image the attester actually uses, so it could never catch the exact regression its name promises (`ContainerAttestationRuntime` accidentally threading a `SqlClient` profile's non-Python image into the attester — the precise bug design-review finding #2 was about). Rewritten to actually run the attester through a `FakeContainerEngine` and assert on `LastSpec.Image`.
4. **Spurious `partial` keyword (Task 8):** `CliContainerEngine` was declared `partial` for no reason — Task 9 edits the same file, not a second one. Removed; the class is an ordinary `sealed class`.
5. **Ambiguous edit instruction (Task 1):** "add a `<param>` line" to an existing doc comment, without showing where relative to the existing lines, is exactly the kind of implicit instruction this skill's "No Placeholders" rule exists to catch. Replaced with the full before/after doc comment.
6. **Style inconsistency (Task 1):** `TryResolveAttesterSource` initially called `bundle.TryResolveResource(...)` without checking its (always-`true`) return value, unlike the existing `TryResolveComputation` it's explicitly modeled on. Changed to match that method's defensive style, adapted for the one real difference (a URL resource is lenient here, not a failure).
7. **Task-number typo:** the plan header's Tech Stack line pointed at "Task 6's note" for the pg8000 trade-off; it's actually Task 5. Fixed.

Extensively cross-checked but found sound: every positional-record constructor call against its actual `src/OKF4net`/`src/OKF4net.Attestation` declaration (`AttestedComputationContract`, `ComputationParameter`, `Executor`, `Attester`, `SanctionedComputation`, `BoundComputation`, `AttestationContext`); `Bundle.TryResolveResource`'s real signature and always-`true` return contract; the `CultureInfo.InvariantCulture` use in `BuildRunArguments` against a plain-string test assertion (no locale bug, in either direction); the concurrent stdin-write/stdout-drain/stderr-drain structure in `RunAsync` (no pipe deadlock); the cancellation-vs-timeout disambiguation against `AttestationOrchestrator`'s actual `IsCallerCancellation` logic; and the bare `TempDir`/`ConceptId`/`Bundle` references in new test files against this repo's own C# namespace-nesting convention (`OKF4net.Tests.Attestation.Containers` resolves unqualified names from the enclosing `OKF4net.Tests` namespace the same way `OKF4net.Tests.Attestation` already does, with no `using` needed — confirmed against `AttestationOrchestratorTests.cs`'s existing, working code).
