# Post-audit fixes (v0.5.0..dev review) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close every finding of the 2026-09-11/12 code review of `v0.5.0..dev` (baseline `cee0c30`): the container runtime's typed-parameter bug and privilege defaults, the producer's trailing-separator refusal, the CLI/core correctness and doc-vs-code gaps, the viewer sanitizer's content-loss and prototype-key holes, and the quality/reuse cleanups the review named.

**Architecture:** Fixes are grouped by project so each task builds and tests one project's change in isolation, TDD first (a failing test that reproduces the finding, then the fix). Work happens in a dedicated git worktree branched from `origin/dev` (a parallel session edits the main checkout). Every behavioural change carries a CHANGELOG line in its own task; goldens are never edited except the one hand-verified v0.2 JSON fixture whose schema this plan extends (documented in `tests/fixtures/README.md`).

**Tech Stack:** .NET 10 / C# 14, xunit, Node + jsdom (`tools/viewer-security-check`), Docker (Postgres 16 fixture for `ContainerIntegration` tests).

**Spec:** the review report delivered in-session on 2026-09-12 (findings #1–#17 + quality section) and the four arbitrations: non-root + cap-drop **by default**; **no** §6.2 fallback but a validator hint; MCP: nothing beyond the "three → four" fix; **surgical** edit of `verified`.

## Global Constraints

- Zero third-party runtime `PackageReference` in `OKF4net`, `OKF4net.Cli`, `OKF4net.Render`, `OKF4net.Viewer`, `OKF4net.Catalog`, `OKF4net.Attestation`, `OKF4net.Attestation.Containers` (BCL only). `OKF4net.Agents` → only `Microsoft.Agents.AI` (+ `OKF4net.Attestation`).
- `tests/fixtures/` v0.1 goldens are never edited. The only fixture this plan touches is `tests/fixtures/golden/audit-v02.json` (hand-verified v0.2 fixture, Task C4), with a note in `tests/fixtures/README.md`.
- New source files start with `// SPDX-License-Identifier: LGPL-3.0-or-later`; file-scoped namespaces; XML docs on public API; `TreatWarningsAsErrors`.
- Every behavioural change cites the spec § in a code comment or CHANGELOG; each task adds its own `CHANGELOG.md` `[Unreleased]` line(s).
- `dotnet format OKF4net.sln --verify-no-changes` must pass before every commit (run `dotnet format OKF4net.sln` to fix).
- Commit messages end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Test commands (from the worktree root):
  - `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` — the CI run.
  - `dotnet test OKF4net.sln --filter "Category=ContainerIntegration"` — needs Docker + `OKF_DEMO_PG_CONN` (Task A2).
  - `dotnet test producers/OkfProducer.sln` — the producer's only guarantee.
  - `cd tools/viewer-security-check && npm ci && npm test` — the viewer's only executable guard.
- The main checkout `E:\Sources\okf` is shared with a parallel session: **never edit files there**. All work happens in the worktree created in Task A1.

---

## Phase A — Setup

### Task A1: Worktree and baseline

**Files:** none (git only)

- [ ] **Step 1: Create the worktree from origin/dev**

```powershell
cd E:\Sources\okf
git fetch origin
git worktree add -b fix/post-audit-2026-09 E:\Sources\okf-post-audit origin/dev
cd E:\Sources\okf-post-audit
git log --oneline -1   # expect cee0c30 or later
```

- [ ] **Step 2: Copy this plan into the worktree and commit it**

```powershell
Copy-Item E:\Sources\okf\docs\superpowers\plans\2026-09-12-post-audit-fixes.md docs\superpowers\plans\
git add docs/superpowers/plans/2026-09-12-post-audit-fixes.md
git commit -m "docs(plans): post-audit fixes plan"
```

- [ ] **Step 3: Baseline — everything green before any change**

```powershell
dotnet build OKF4net.sln
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"
dotnet test producers/OkfProducer.sln
cd tools/viewer-security-check; npm ci; npm test; cd ../..
```
Expected: 0 warnings; all tests pass; harness `N passed, 0 failed`. Record the counts for the PR description.

### Task A2: Postgres fixture for the `ContainerIntegration` tests

**Files:** none (docker only)

- [ ] **Step 1: Start and seed Postgres (recipe from `tests/OKF4net.Tests/Attestation.Containers/ContainerIntegrationTests.cs:23-37`)**

```powershell
docker run --rm -d --name okf-demo-pg -e POSTGRES_PASSWORD=demo -e POSTGRES_DB=demo -p 5544:5432 postgres:16-alpine
Start-Sleep -Seconds 5
docker exec -i okf-demo-pg psql -U postgres -d demo -c "CREATE TABLE users(id int, active boolean); INSERT INTO users VALUES (1,true),(2,true),(3,false);"
Get-Content bundles/meridian_transit/references/schema.sql | docker exec -i okf-demo-pg psql -U postgres -d demo
$env:OKF_DEMO_PG_CONN = "postgresql://postgres:demo@host.docker.internal:5544/demo"
```

- [ ] **Step 2: Run the integration suite and record the baseline**

```powershell
dotnet test OKF4net.sln --filter "Category=ContainerIntegration"
```
Expected: 8 tests, 0 skipped, all pass. If one fails on the untouched baseline, stop and report — that is a finding on `dev`, not on this plan.

Keep `okf-demo-pg` running for Phase B; `docker stop okf-demo-pg` at the very end.

---

## Phase B — `OKF4net.Attestation.Containers` / `OKF4net.Attestation` / `OKF4net.Agents`

### Task B1: `okf_run_computation` hands the binder CLR values, not `JsonElement`s (finding #1)

**Files:**
- Create: `src/OKF4net.Agents/Internal/ParameterValues.cs`
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (`RunComputationAsync`, the `parameterValues ??=` line)
- Test: `tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs` (`okf_run_computation_binds_parameterValues_dictionary_from_a_json_object`), new `tests/OKF4net.Tests/Agents/ParameterValuesTests.cs`, `tests/OKF4net.Tests/Attestation.Containers/AllowlistParameterBinderTests.cs`

**Interfaces:**
- Produces: `internal static IReadOnlyDictionary<string, object?> ParameterValues.Normalize(IReadOnlyDictionary<string, object?> values)` — every `JsonElement` replaced by `long`/`double`/`string`/`bool`/`null`/`List<object?>`/`Dictionary<string, object?>`; other values pass through untouched.

- [ ] **Step 1: Write the failing unit test** `tests/OKF4net.Tests/Agents/ParameterValuesTests.cs`

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using OKF4net.Agents.Internal;
using Xunit;

namespace OKF4net.Tests.Agents;

public class ParameterValuesTests
{
    [Theory]
    [InlineData("2026", typeof(long))]
    [InlineData("1.5", typeof(double))]
    [InlineData("\"Ada\"", typeof(string))]
    [InlineData("true", typeof(bool))]
    public void A_JsonElement_scalar_becomes_the_matching_CLR_value(string json, System.Type expected)
    {
        var values = new Dictionary<string, object?> { ["p"] = JsonSerializer.Deserialize<JsonElement>(json) };
        var normalized = ParameterValues.Normalize(values);
        Assert.IsType(expected, normalized["p"]);
    }

    [Fact]
    public void Null_arrays_and_objects_are_normalized_recursively()
    {
        var values = new Dictionary<string, object?>
        {
            ["n"] = JsonSerializer.Deserialize<JsonElement>("null"),
            ["a"] = JsonSerializer.Deserialize<JsonElement>("[1, \"x\"]"),
            ["o"] = JsonSerializer.Deserialize<JsonElement>("{\"k\": 2}"),
        };
        var normalized = ParameterValues.Normalize(values);
        Assert.Null(normalized["n"]);
        var list = Assert.IsType<List<object?>>(normalized["a"]);
        Assert.Equal(1L, list[0]);
        Assert.Equal("x", list[1]);
        var map = Assert.IsType<Dictionary<string, object?>>(normalized["o"]);
        Assert.Equal(2L, map["k"]);
    }

    [Fact]
    public void A_native_CLR_value_passes_through_unchanged()
    {
        var values = new Dictionary<string, object?> { ["p"] = 42, ["s"] = "q3" };
        var normalized = ParameterValues.Normalize(values);
        Assert.Equal(42, normalized["p"]);
        Assert.Equal("q3", normalized["s"]);
    }
}
```

- [ ] **Step 2: Run it — expect a compile failure (`ParameterValues` does not exist)**

`dotnet test OKF4net.sln --filter "FullyQualifiedName~ParameterValuesTests"`

- [ ] **Step 3: Implement** `src/OKF4net.Agents/Internal/ParameterValues.cs`

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

namespace OKF4net.Agents.Internal;

/// <summary>
/// Turns the values an <c>AIFunction</c> invocation binds for
/// <c>okf_run_computation</c> — every one a <see cref="JsonElement"/>, which
/// is System.Text.Json's representation of an <see cref="object"/>-typed
/// dictionary value — into the plain CLR values a §10 binder type-checks
/// against a concept's declared <c>parameters</c>. Without this, a binder
/// that checks <c>value is int or long</c> rejects every typed parameter the
/// tool ever receives, while the same call from C# with a boxed <c>int</c>
/// succeeds — which is exactly how the gap stayed invisible.
/// </summary>
internal static class ParameterValues
{
    internal static IReadOnlyDictionary<string, object?> Normalize(IReadOnlyDictionary<string, object?> values)
    {
        var result = new Dictionary<string, object?>(values.Count, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            result[key] = value is JsonElement element ? FromElement(element) : value;
        }

        return result;
    }

    private static object? FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.Array => element.EnumerateArray().Select(FromElement).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => FromElement(p.Value), StringComparer.Ordinal),
        _ => null,
    };
}
```

- [ ] **Step 4: Call it in `RunComputationAsync`** — replace the line `parameterValues ??= new Dictionary<string, object?>();` with:

```csharp
        // See RunComputation's remarks: an AIFunction-bound call can pass null
        // despite the non-nullable static type. And what it does pass is a
        // dictionary of JsonElements, never native values -- normalized here,
        // once, for every binder (see ParameterValues).
        parameterValues = ParameterValues.Normalize(parameterValues ?? new Dictionary<string, object?>());
```

- [ ] **Step 5: Update the characterization test** in `AIFunctionExposureTests.cs`: replace the two `((JsonElement)captured![...])` assertions with

```csharp
        // Values reach the binder as native CLR values (ParameterValues
        // normalizes the JsonElements AIFunctionFactory binds), so a binder
        // that type-checks against the declared `parameters` sees an integer
        // where the caller sent one.
        Assert.Equal(42L, captured!["threshold"]);
        Assert.Equal("q3", captured!["label"]);
```
and rewrite the comment above them to say the same.

- [ ] **Step 6: End-to-end guard** — append to `AllowlistParameterBinderTests.cs`:

```csharp
    /// <summary>
    /// The tool path normalizes JsonElements before the orchestrator; this
    /// pins that the binder's CLR checks and that normalization agree, so a
    /// JSON `2026` for an `integer` parameter is accepted end to end.
    /// </summary>
    [Fact]
    public async Task Accepts_the_long_the_tool_path_produces_for_a_json_integer()
    {
        var contract = new AttestedComputationContract(
            Runtime: "python",
            Parameters: [new ComputationParameter("year", "integer", Required: true)],
            ComputationPath: null, Executor: null, Attester: null);
        var bound = await new AllowlistParameterBinder().BindAsync(contract, Computation, new Dictionary<string, object?> { ["year"] = 2026L });
        Assert.Equal(2026L, bound.Values["year"]);
    }
```

- [ ] **Step 7: Run** `dotnet test OKF4net.sln --filter "FullyQualifiedName~ParameterValuesTests|FullyQualifiedName~AIFunctionExposureTests|FullyQualifiedName~AllowlistParameterBinderTests"` — all pass.

- [ ] **Step 8: CHANGELOG** — under `### Fixed`:

```markdown
- **`okf_run_computation` now delivers native CLR parameter values to the
  binder.** `AIFunctionFactory` binds an `object`-typed dictionary's values as
  `JsonElement`s, which `OKF4net.Attestation.Containers`' allowlist binder
  rejected for every declared `type` (`integer`, `string`, `boolean`,
  `number`) — the container runtime was unusable through the tool and MCP for
  any typed parameter, while the same call from C# succeeded. Values are now
  normalized once in the tool (`ParameterValues`), for every binder.
```

- [ ] **Step 9: Format + commit**

```powershell
dotnet format OKF4net.sln
git add -A; git commit -m "fix(agents): hand the binder CLR values, not JsonElements, from okf_run_computation"
```

### Task B2: Non-root, capability-dropped containers by default, and one `ContainerIsolation` record instead of three copies (findings #3, quality "ceilings duplicated")

**Files:**
- Create: `src/OKF4net.Attestation.Containers/ContainerIsolation.cs`
- Modify: `src/OKF4net.Attestation.Containers/IContainerEngine.cs` (`ContainerRunSpec`), `CliContainerEngine.cs` (`BuildRunArguments`), `ContainerRuntimeProfile.cs` (both records), `ScriptComputationExecutor.cs`, `SqlClientComputationExecutor.cs`, `ContainerAttester.cs`, `README.md` (Limitations + "What the host controls")
- Test: `tests/OKF4net.Tests/Attestation.Containers/CliContainerEngineArgumentsTests.cs`, `ContainerRuntimeProfileTests.cs`, `ContainerIntegrationTests.cs` (rerun)

**Interfaces:**
- Produces: `public sealed record ContainerIsolation` with `string? User` (default `"65534:65534"`), `bool DropAllCapabilities` (default `true`), `bool NoNewPrivileges` (default `true`), `bool ReadOnlyRootFilesystem` (default `true`), `IReadOnlyList<string> TmpfsMounts` (default `["/tmp"]`), `long MemoryBytes`, `double Cpus`, `int PidsLimit`, `TimeSpan Timeout` (validated via `ResourceCeiling`), and `internal ContainerRunSpec ToRunSpec(string image, IReadOnlyList<string> command, string? stdin, IReadOnlyDictionary<string, string> environment, string? networkMode)`.
- `ContainerRuntimeProfile.Isolation` and `ContainerAttesterOptions.Isolation` (init, default `new()`); the eight per-ceiling properties on both records are **removed** (breaking, 0.x, this project is unpublished).
- `ContainerRunSpec` gains `string? User { get; init; }`, `bool DropAllCapabilities { get; init; }`, `bool NoNewPrivileges { get; init; }` (all opt-in on the raw spec, like `ReadOnlyRootFilesystem`; the safe defaults live on `ContainerIsolation`).

- [ ] **Step 1: Failing argument-builder tests** — append to `CliContainerEngineArgumentsTests.cs`:

```csharp
    [Fact]
    public void Emits_user_cap_drop_and_no_new_privileges_when_set()
    {
        var spec = Spec() with { User = "65534:65534", DropAllCapabilities = true, NoNewPrivileges = true };
        var args = CliContainerEngine.BuildRunArguments(spec, "okf-1").ToList();
        Assert.Equal("65534:65534", args[args.IndexOf("--user") + 1]);
        Assert.Equal("ALL", args[args.IndexOf("--cap-drop") + 1]);
        Assert.Equal("no-new-privileges", args[args.IndexOf("--security-opt") + 1]);
    }

    [Fact]
    public void A_raw_spec_omits_the_isolation_flags_by_default()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(), "okf-1");
        Assert.DoesNotContain("--user", args);
        Assert.DoesNotContain("--cap-drop", args);
        Assert.DoesNotContain("--security-opt", args);
    }

    [Fact]
    public void A_profile_built_spec_carries_the_hardened_defaults()
    {
        var profile = new ContainerRuntimeProfile { Image = "python:3.12-slim", Kind = ContainerRuntimeKind.Script };
        var spec = profile.Isolation.ToRunSpec(profile.Image, ["python3", "-"], "print()", profile.Environment, profile.NetworkMode);
        var args = CliContainerEngine.BuildRunArguments(spec, "okf-1").ToList();
        Assert.Equal("65534:65534", args[args.IndexOf("--user") + 1]);
        Assert.Contains("--cap-drop", args);
        Assert.Contains("--security-opt", args);
        Assert.Contains("--read-only", args);
    }
```

- [ ] **Step 2: Run** `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliContainerEngineArgumentsTests"` — compile failure (`User`, `Isolation` unknown).

- [ ] **Step 3: Create `ContainerIsolation.cs`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Attestation.Containers.Internal;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// Everything that bounds a container run other than its image, command,
/// input, environment and network: who the process runs as, which
/// capabilities it keeps, whether it can gain privileges, whether its root is
/// writable, and the four resource ceilings. One record, shared by
/// <see cref="ContainerRuntimeProfile"/> and <see cref="ContainerAttesterOptions"/>,
/// so a hardening decision cannot land on two of the three container
/// consumers and miss the third.
///
/// <para>Defaults are the hardened ones, because the code these containers run
/// is bundle-authored and therefore untrusted: a non-root uid (<c>65534</c>,
/// <c>nobody</c> on every mainstream image), every capability dropped, no
/// privilege escalation, a read-only root with <c>/tmp</c> on a memory-backed
/// tmpfs. A host that needs the image's own user (an image whose entrypoint
/// insists on it) sets <see cref="User"/> to <see langword="null"/>.</para>
/// </summary>
public sealed record ContainerIsolation
{
    /// <summary>Passed as <c>--user</c>; <see langword="null"/> keeps the image's default user. Default <c>65534:65534</c>.</summary>
    public string? User { get; init; } = "65534:65534";

    /// <summary>Passed as <c>--cap-drop ALL</c> when <see langword="true"/> (default).</summary>
    public bool DropAllCapabilities { get; init; } = true;

    /// <summary>Passed as <c>--security-opt no-new-privileges</c> when <see langword="true"/> (default).</summary>
    public bool NoNewPrivileges { get; init; } = true;

    /// <summary>Passed as <c>--read-only</c> when <see langword="true"/> (default). See <see cref="TmpfsMounts"/> for the writable exceptions.</summary>
    public bool ReadOnlyRootFilesystem { get; init; } = true;

    /// <summary>Memory-backed writable paths under a read-only root; <c>/tmp</c> by default (the attester bootstrap and the SQL wrapper's driver install both write there).</summary>
    public IReadOnlyList<string> TmpfsMounts { get; init; } = ["/tmp"];

    /// <summary><c>--memory</c>, in bytes. Must be positive: zero means <i>unlimited</i> to docker and podman.</summary>
    public long MemoryBytes { get => _memoryBytes; init => _memoryBytes = ResourceCeiling.Positive(value, nameof(MemoryBytes)); }

    /// <summary><c>--cpus</c>. Must be positive (see <see cref="MemoryBytes"/>).</summary>
    public double Cpus { get => _cpus; init => _cpus = ResourceCeiling.Positive(value, nameof(Cpus)); }

    /// <summary><c>--pids-limit</c>. Must be positive (see <see cref="MemoryBytes"/>).</summary>
    public int PidsLimit { get => _pidsLimit; init => _pidsLimit = ResourceCeiling.Positive(value, nameof(PidsLimit)); }

    /// <summary>Wall-clock ceiling on one run, enforced by the engine. Must be positive and enforceable by a timer.</summary>
    public TimeSpan Timeout { get => _timeout; init => _timeout = ResourceCeiling.Timeout(value, nameof(Timeout)); }

    /// <summary>Builds the run spec for one container with every isolation setting of this record applied.</summary>
    internal ContainerRunSpec ToRunSpec(string image, IReadOnlyList<string> command, string? stdin, IReadOnlyDictionary<string, string> environment, string? networkMode) =>
        new(image, command, stdin, environment, networkMode, MemoryBytes, Cpus, PidsLimit, Timeout)
        {
            ReadOnlyRootFilesystem = ReadOnlyRootFilesystem,
            TmpfsMounts = TmpfsMounts,
            User = User,
            DropAllCapabilities = DropAllCapabilities,
            NoNewPrivileges = NoNewPrivileges,
        };

    private readonly long _memoryBytes = 512L * 1024 * 1024;
    private readonly double _cpus = 1.0;
    private readonly int _pidsLimit = 64;
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);
}
```

- [ ] **Step 4: Extend `ContainerRunSpec`** (IContainerEngine.cs) — after `TmpfsMounts` add three init properties with XML docs: `public string? User { get; init; }` (`--user`, omitted when null), `public bool DropAllCapabilities { get; init; }` (`--cap-drop ALL`), `public bool NoNewPrivileges { get; init; }` (`--security-opt no-new-privileges`). Document that a hand-built spec is unhardened by default and the safe defaults live on `ContainerIsolation`.

- [ ] **Step 5: Emit the flags in `BuildRunArguments`** — after the `--tmpfs` loop:

```csharp
        if (spec.User is { } user)
        {
            args.Add("--user");
            args.Add(user);
        }

        if (spec.DropAllCapabilities)
        {
            args.Add("--cap-drop");
            args.Add("ALL");
        }

        if (spec.NoNewPrivileges)
        {
            args.Add("--security-opt");
            args.Add("no-new-privileges");
        }
```

- [ ] **Step 6: Replace the duplicated ceilings on both records.** In `ContainerRuntimeProfile` delete `ReadOnlyRootFilesystem`, `TmpfsMounts`, `MemoryBytes`, `Cpus`, `PidsLimit`, `Timeout` and their backing fields; add

```csharp
    /// <summary>Who the container runs as, what it keeps, and the four ceilings. Hardened by default — see <see cref="ContainerIsolation"/>.</summary>
    public ContainerIsolation Isolation { get; init; } = new();
```
Do the same in `ContainerAttesterOptions`, keeping its smaller defaults by initializing `Isolation = new() { MemoryBytes = 256L * 1024 * 1024, Cpus = 0.5, PidsLimit = 32, Timeout = TimeSpan.FromSeconds(30) }`.

- [ ] **Step 7: Rebuild the three specs through `ToRunSpec`**
  - `ScriptComputationExecutor.ExecuteAsync`: `var spec = profile.Isolation.ToRunSpec(profile.Image, [profile.Interpreter, "-"], bound.BoundText ?? "", env, profile.NetworkMode);`
  - `SqlClientComputationExecutor.ExecuteAsync`: `var spec = profile.Isolation.ToRunSpec(profile.Image, ["python3", "-c", Wrapper], envelope, profile.Environment, profile.NetworkMode);`
  - `ContainerAttester.AttestAsync`: `var spec = options.Isolation.ToRunSpec(options.Image, ["python3", "-c", Bootstrap], envelope, options.Environment, "none");`

- [ ] **Step 8: Fix the compile fallout** in `ContainerRuntimeProfileTests.cs`, `ContainerIntegrationTests.cs`, `samples/attestation-containers-demo/Program.cs`, `ContainerAttestationRuntimeTests.cs`: every `MemoryBytes = …`/`Timeout = …` on a profile becomes `Isolation = new() { … }`. Grep: `rg -n "MemoryBytes|PidsLimit|Cpus =|Timeout =" tests samples src/OKF4net.Attestation.Containers`.

- [ ] **Step 9: Run** `dotnet build OKF4net.sln` (0 warnings) then `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Attestation"` — green.

- [ ] **Step 10: Prove it against real Docker** — `dotnet test OKF4net.sln --filter "Category=ContainerIntegration"` with `OKF_DEMO_PG_CONN` set. Expected: all 8 pass as uid 65534 (the pip `--target /tmp/okf-pkgs` install and the attester's `tempfile` both write to the tmpfs). Then a manual check that the flags are live:

```powershell
docker run --rm --user 65534:65534 --cap-drop ALL --security-opt no-new-privileges --read-only --tmpfs /tmp python:3.12-slim python3 -c "import os; print(os.getuid())"
```
Expected output: `65534`.

- [ ] **Step 11: README** — in `src/OKF4net.Attestation.Containers/README.md`, "What the host controls": replace the four-ceilings sentence with a `ContainerIsolation` paragraph listing `User` (65534 default), `DropAllCapabilities`, `NoNewPrivileges`, `ReadOnlyRootFilesystem`, `TmpfsMounts` and the ceilings. In "Limitations", add:

```markdown
- **`--user` is a uid, not a sandbox.** Non-root plus `--cap-drop ALL` and
  `no-new-privileges` removes the ordinary escalation paths; it does not add
  user namespaces, seccomp/AppArmor profiles beyond the engine's defaults, or
  gVisor/Kata-class isolation. An image whose entrypoint requires root will
  fail under the default profile — set `Isolation = new() { User = null }` to
  keep the image's user, knowingly.
```

- [ ] **Step 12: CHANGELOG** — under `### Changed`:

```markdown
- **Breaking (`OKF4net.Attestation.Containers`, unpublished): containers run
  as uid 65534 with every capability dropped and `no-new-privileges`, by
  default.** The isolation settings — user, capabilities, privilege
  escalation, read-only root, tmpfs mounts and the four ceilings — moved off
  `ContainerRuntimeProfile`/`ContainerAttesterOptions` onto one shared
  `ContainerIsolation` record (`Isolation = new() { … }`), so a hardening
  decision reaches the script executor, the SQL executor and the attester at
  once. A hand-built `ContainerRunSpec` stays opt-in, as `ReadOnlyRootFilesystem`
  already was. Found by an external review: untrusted bundle code ran as the
  image's default user (root on `python:3.12-slim`) with Docker's default
  capability set.
```

- [ ] **Step 13: Format + commit**

```powershell
dotnet format OKF4net.sln
git add -A; git commit -m "feat(attestation-containers)!: run as non-root with capabilities dropped, via one ContainerIsolation record"
```

### Task B3: SQL wrapper — percent-decoded userinfo and statements without a result set (finding #13)

**Files:**
- Modify: `src/OKF4net.Attestation.Containers/SqlClientComputationExecutor.cs` (`Wrapper`)
- Test: `tests/OKF4net.Tests/Attestation.Containers/SqlClientComputationExecutorTests.cs` (source smoke checks), `ContainerIntegrationTests.cs` (new real-Postgres test)

- [ ] **Step 1: Failing integration test** — append to `ContainerIntegrationTests.cs` (same shape as `SqlClient_runtime_runs_a_real_postgres_query`, reuse its `Profile(conn)` helper if one exists, else inline):

```csharp
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
```

- [ ] **Step 2: Run** `dotnet test OKF4net.sln --filter "FullyQualifiedName~SqlClient_wrapper_decodes_userinfo"` — fails (`SQL wrapper exited with code 1`).

- [ ] **Step 3: Fix the wrapper** — replace the `from urllib.parse import urlparse` … `result = […]` lines with:

```python
        from urllib.parse import urlparse, unquote
        envelope = json.load(sys.stdin)
        sql = envelope['sql']
        values = envelope.get('values') or {}
        u = urlparse(os.environ['OKF_CONN'])
        # urlparse hands userinfo back still percent-encoded; libpq decodes it,
        # so a password spelled `p%40ss` (the only way to write `p@ss` in a URL)
        # must be decoded here or it never authenticates.
        conn = pg8000.native.Connection(
            user=unquote(u.username or ''), password=unquote(u.password or ''),
            host=u.hostname, port=u.port or 5432, database=unquote(u.path.lstrip('/')))
        try:
            rows = conn.run(sql, **values)
            # A statement with no result set (DDL, INSERT without RETURNING)
            # returns None, and iterating it raised AFTER the statement had
            # already run -- a side-effecting run reported as a failure.
            cols = [c['name'] for c in conn.columns] if conn.columns else []
            result = [dict(zip(cols, row)) for row in (rows or [])]
```

- [ ] **Step 4: Smoke checks** — in `SqlClientComputationExecutorTests.cs` add (labelled as a smoke check, not proof, like its neighbours):

```csharp
    [Fact]
    public void Wrapper_decodes_userinfo_and_tolerates_a_rowless_statement_SMOKE_CHECK()
    {
        Assert.Contains("unquote(u.password", SqlClientComputationExecutor.Wrapper, StringComparison.Ordinal);
        Assert.Contains("(rows or [])", SqlClientComputationExecutor.Wrapper, StringComparison.Ordinal);
    }
```

- [ ] **Step 5: Run** the integration test again — passes; run `--filter "Category=ContainerIntegration"` — all pass.

- [ ] **Step 6: CHANGELOG** (`### Fixed`):

```markdown
- **The SQL wrapper percent-decodes `OKF_CONN`'s userinfo and survives a
  statement without a result set.** `urlparse` keeps `p%40ss` encoded (libpq
  decodes it), so the only URL spelling of a password containing `@` failed
  authentication; and `pg8000` returns `None` for DDL/INSERT, which the
  wrapper iterated — after the statement had run against the live database —
  reporting a completed side effect as a failed run.
```

- [ ] **Step 7: Commit** — `git add -A; git commit -m "fix(attestation-containers): decode userinfo and accept rowless statements in the SQL wrapper"`

### Task B4: Stage failures carry their own diagnosis; one `RunStageAsync` instead of three catch pairs (findings #B-4 "reasons erase own diagnostics", quality "catch pasted 3×")

**Files:**
- Create: `src/OKF4net.Attestation/AttestationDiagnosticException.cs`
- Modify: `src/OKF4net.Attestation/AttestationOrchestrator.cs` (bind/execute/attest stages), `src/OKF4net.Attestation.Containers/ContainerExecutionException.cs`, `Internal/DeclaredParameterFilter.cs`, `SqlClientComputationExecutor.cs` (reserved-name throw), `src/OKF4net.Agents/OkfBundleTools.cs` (`FormatOutcome` error line)
- Test: `tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs`, `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs`

**Interfaces:**
- Produces: `public class AttestationDiagnosticException : Exception` in `OKF4net.Attestation` — "a stage failure whose message was authored by an OKF4net component and carries no host secret; the orchestrator renders its `Message` into `AttestationOutcome.Reasons`". Constructors `(string message)` and `(string message, Exception? inner)`.
- `ContainerExecutionException : AttestationDiagnosticException` (was `: Exception`); `DeclaredParameterFilter.CheckType` and the SQL reserved-name check throw `AttestationDiagnosticException` instead of `ArgumentException`.
- Orchestrator reason text: `"{stage} threw: {TypeName}"` for any other exception (unchanged), `"{stage} threw: {TypeName}: {Message}"` for an `AttestationDiagnosticException`.

- [ ] **Step 1: Failing orchestrator test** — in `AttestationOrchestratorTests.cs`, next to the existing "executor throws" test:

```csharp
    [Fact]
    public async Task A_diagnostic_exception_reports_its_message_a_foreign_one_only_its_type()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);   // the class's existing fixture helper (runtime "bigquery", parameter `year`)
        var diagnostic = FakeRuntime.Passing();
        diagnostic.ExecuteFunc = (_, _, _) => throw new AttestationDiagnosticException("receipt was not a JSON object");
        var foreign = FakeRuntime.Passing();
        foreign.ExecuteFunc = (_, _, _) => throw new InvalidOperationException("Host=db;Password=hunter2");

        static AttestationOrchestrator Orch(FakeRuntime r) =>
            new(new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = r }), clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var values = new Dictionary<string, object?> { ["year"] = 2026 };
        var a = await Orch(diagnostic).RunAsync(bundle, id, values);
        var b = await Orch(foreign).RunAsync(bundle, id, values);

        Assert.Contains("executor threw: AttestationDiagnosticException: receipt was not a JSON object", a.Reasons);
        Assert.Contains("executor threw: InvalidOperationException", b.Reasons);
        Assert.DoesNotContain(b.Reasons, r => r.Contains("hunter2", StringComparison.Ordinal));
    }
```


- [ ] **Step 2: Run** — compile failure (`AttestationDiagnosticException` missing).

- [ ] **Step 3: Create the exception**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation;

/// <summary>
/// A stage failure whose message was written by an OKF4net component — a
/// binder rejecting a value's type, an executor that could not parse a
/// receipt, a container that exited non-zero — and therefore carries no host
/// secret. <see cref="AttestationOrchestrator"/> renders the
/// <see cref="Exception.Message"/> of this type into
/// <see cref="AttestationOutcome.Reasons"/>; every other exception is reported
/// by type name only, because a host-plugged runtime's message can name a
/// connection string, a query, or the row it choked on. A host runtime that
/// wants its own diagnosis to reach the model derives from this type and
/// takes responsibility for what the message contains.
/// </summary>
public class AttestationDiagnosticException : Exception
{
    /// <summary>Creates the exception with a message safe to render to a model.</summary>
    public AttestationDiagnosticException(string message) : base(message) { }

    /// <summary>Creates the exception with a message safe to render to a model and the exception that caused it.</summary>
    public AttestationDiagnosticException(string message, Exception? innerException) : base(message, innerException) { }
}
```

- [ ] **Step 4: One stage runner in the orchestrator** — add a private helper and replace the three try/catch pairs (bind, execute, and the body of `AttestAsync`) with calls to it:

```csharp
    /// <summary>
    /// Runs one host-plugged stage under the single cancellation and
    /// reporting policy: a caller cancellation (direct or wrapped) propagates
    /// as an <see cref="OperationCanceledException"/> tied to the caller's
    /// token; any other exception becomes a reason — the message included
    /// only for an <see cref="AttestationDiagnosticException"/>, the type
    /// alone for everything else (see that type's remarks for why).
    /// </summary>
    private static async ValueTask<(bool Ok, T Result, string? Reason, Exception? Error)> RunStageAsync<T>(
        string stage,
        Func<CancellationToken, ValueTask<T>> run,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return (true, await run(cancellationToken).ConfigureAwait(false), null, null);
        }
        catch (AggregateException e) when (IsCallerCancellation(e, cancellationToken))
        {
            throw new OperationCanceledException(CancelledMessage, e, cancellationToken);
        }
        catch (Exception e) when (!IsCallerCancellation(e, cancellationToken))
        {
            var reason = e is AttestationDiagnosticException
                ? $"{stage} threw: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}"
                : $"{stage} threw: {e.GetType().Name}";
            return (false, default!, reason, e);
        }
    }
```
(`AttestationVerdict` is a `readonly record struct`, so no `class` constraint — the `Ok` flag says whether `Result` is meaningful.) Call sites:

```csharp
        var (bindOk, bound, bindReason, bindError) = await RunStageAsync("binder", ct => runtime.Binder.BindAsync(contract, resolved, parameterValues, ct), cancellationToken).ConfigureAwait(false);
        if (!bindOk) { return Fail([bindReason!], stale, bindError); }

        var (execOk, receipt, execReason, execError) = await RunStageAsync("executor", ct => runtime.Executor.ExecuteAsync(bound, contract, ct), cancellationToken).ConfigureAwait(false);
        if (!execOk) { return Fail([execReason!], stale, execError); }
```
and in `AttestAsync`: `var (ok, verdict, reason, error) = await RunStageAsync("attester", ct => runtime.Attester.AttestAsync(context, ct), cancellationToken)…; if (!ok) { reasons.Add(reason!); return (null, error); }` then the existing "attestation did not pass" branch. Delete the now-duplicated comments; keep one copy on `RunStageAsync`.

- [ ] **Step 5: Derive the container exception and the two rejections** — `ContainerExecutionException : AttestationDiagnosticException` (pass `message` to base); in `DeclaredParameterFilter.CheckType` throw `new AttestationDiagnosticException($"parameter value for declared type '{declaredType}' has an incompatible CLR type: {value.GetType().Name}")`; in `SqlClientComputationExecutor` the reserved-name throw likewise. Update the two unit tests that `Assert.Throws<ArgumentException>` on those paths to `Assert.Throws<AttestationDiagnosticException>` (`rg -n "Throws<ArgumentException>" tests/OKF4net.Tests/Attestation.Containers`).

- [ ] **Step 6: `FormatOutcome` error line** (OkfBundleTools) — keep "the TYPE, never the message" for foreign exceptions, but render the message for `AttestationDiagnosticException`: `outcome.Error is AttestationDiagnosticException d ? $"{d.GetType().Name}: {d.Message}" : outcome.Error.GetType().Name`. Extend `OkfComputationToolsTests.cs` line ~58's test with a second case asserting the message appears for a diagnostic exception and not for a foreign one.

- [ ] **Step 7: Run** `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Attestation|FullyQualifiedName~OkfComputationToolsTests"` — green; then the full CI filter.

- [ ] **Step 8: CHANGELOG** (`### Changed`):

```markdown
- **Stage failures the library itself diagnosed now say why.** A new
  `AttestationDiagnosticException` (`OKF4net.Attestation`) marks a message
  authored by an OKF4net component — `ContainerExecutionException` derives
  from it, and the allowlist binder's type rejection and the SQL executor's
  reserved-name refusal throw it. The orchestrator renders such a message into
  `Reasons` (`executor threw: ContainerExecutionException: SQL wrapper exited
  with code 1`); every other exception is still reported by type only, since a
  host runtime's message can carry a connection string. Before, a missing
  attester, a Python crash, an unpullable image and a timeout all read as the
  same `attester threw: ContainerExecutionException`.
```

- [ ] **Step 9: Commit** — `git add -A; git commit -m "feat(attestation): report self-authored stage diagnostics, run every stage through one policy"`

### Task B5: `FormatOutcome` renders nested receipt values as JSON (finding #9)

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (`FormatOutcome`, receipt loop)
- Test: `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs`

- [ ] **Step 1: Failing test**

```csharp
    [Fact]
    public async Task A_list_valued_receipt_field_renders_as_json_not_a_type_name()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [result] }\n---\n# Computation\n\n```\nX\n```\n");
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?>
        {
            ["result"] = new List<object?> { new Dictionary<string, object?> { ["active_users"] = 2L } },
        }));
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        var text = await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>());
        Assert.Contains("- result: [{\"active_users\":2}]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Collections", text, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Run** — fails (type name rendered).

- [ ] **Step 3: Fix** — replace `.Append(value?.ToString() ?? NoneLine)` with `.Append(FormatReceiptValue(value))` and add:

```csharp
    /// <summary>
    /// Scalars print as before; a list or map (what ReceiptParsing produces
    /// for a JSON array/object) prints as compact JSON, because
    /// `List`1[System.Object]` tells the model nothing about the rows the
    /// computation returned.
    /// </summary>
    private static string FormatReceiptValue(object? value) => value switch
    {
        null => NoneLine,
        string s => s,
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };
```
(`System.Text.Json` and `System.Globalization` usings; `JsonSerializer.Serialize(object)` handles `List<object?>`/`Dictionary<string, object?>` with boxed scalars.)

- [ ] **Step 4: Run** the test file — green. **Step 5: CHANGELOG** (`### Fixed`): "`okf_run_computation` renders list- and object-valued receipt fields as compact JSON instead of the CLR type name, so a SQL result actually reaches the model." **Step 6: Commit** `fix(agents): render nested receipt values as JSON in okf_run_computation`.

### Task B6: `ContainerAttester` reuses `ReceiptParsing`'s prologue (quality)

**Files:**
- Modify: `src/OKF4net.Attestation.Containers/Internal/ReceiptParsing.cs`, `ContainerAttester.cs`
- Test: `tests/OKF4net.Tests/Attestation.Containers/ReceiptParsingTests.cs`, `ContainerAttesterTests.cs`

- [ ] **Step 1: Failing test** in `ReceiptParsingTests.cs`:

```csharp
    [Fact]
    public void ParseJson_rejects_a_non_zero_exit_then_invalid_json_with_the_stage_name()
    {
        var ex1 = Assert.Throws<ContainerExecutionException>(() => ReceiptParsing.ParseJson(new ContainerRunResult(3, "", "boom"), "attester"));
        Assert.Equal("attester exited with code 3", ex1.Message);
        var ex2 = Assert.Throws<ContainerExecutionException>(() => ReceiptParsing.ParseJson(new ContainerRunResult(0, "nope", ""), "attester"));
        Assert.StartsWith("attester stdout was not valid JSON", ex2.Message, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Split `ReceiptParsing.Parse`** into `internal static JsonDocument ParseJson(ContainerRunResult result, string stageName)` (exit-code check + `JsonDocument.Parse`, both throwing as today) and `Parse` which calls it and keeps the object check + normalization. Caller disposes the document.

- [ ] **Step 3: Use it in `ContainerAttester.AttestAsync`** — replace the exit-code check and the `JsonSerializer.Deserialize<JsonElement>` try/catch with `using var document = ReceiptParsing.ParseJson(result, "attester"); var verdict = document.RootElement;` and keep the `ok`/`reason` extraction.

- [ ] **Step 4: Run** `--filter "FullyQualifiedName~ReceiptParsingTests|FullyQualifiedName~ContainerAttesterTests"` — green; existing attester tests still assert the same messages. **Step 5: Commit** `refactor(attestation-containers): share the container-output prologue between executors and attester`.

---

## Phase C — `OKF4net` core and `okf` CLI

### Task C1: Caller-supplied concept ids are never echoed raw; BOM-tolerant `verify -` (findings #4, BOM)

**Files:**
- Modify: `src/OKF4net.Cli/OkfCli.cs` (`CmdVerify` unknown/duplicate/no-type messages, `ReadIdsFrom`), `src/OKF4net.Agents/OkfBundleTools.cs` (`Verify` tool's two `Error: concept '{id}'` lines), `src/OKF4net/BundleConceptWriter.cs` (`RecordVerifications`: the `concept '{conceptIds[i]}'` messages and `ValidateConceptTarget`'s invalid-id message)
- Test: `tests/OKF4net.Tests/CliTests.cs` (verify section), `tests/OKF4net.Tests/Agents/OkfVerifyToolTests.cs`

**Interfaces:** every echoed id goes through `DebugQuote.Quote(id)` (`OKF4net.Internal`, already used by the validator: produces a C#-style quoted string with control characters escaped, e.g. `"a\nb"`). `OKF4net.Agents` already has `InternalsVisibleTo`; `OKF4net.Cli` — check `src/OKF4net/OKF4net.csproj`; add `<InternalsVisibleTo Include="OKF4net.Cli" />` if absent (it references `LineSafeText`? no — `CliArgScanning` is internal and used by Cli, so the grant exists).

- [ ] **Step 1: Failing CLI test** — in `CliTests.cs`:

```csharp
    [Fact]
    public void Verify_escapes_a_control_bearing_concept_id_instead_of_forging_a_line()
    {
        var forged = "metrics/nope\nrecorded metrics/dau  human:ada  2026-01-01T00:00:00Z";
        var (code, _, err) = Run("verify", OkfV02, forged, "--by", "human:ada");
        Assert.Equal(1, code);
        Assert.Equal("error: unknown concept \"metrics/nope\\nrecorded metrics/dau  human:ada  2026-01-01T00:00:00Z\"\n", err);
    }

    [Fact]
    public void Verify_from_stdin_ignores_a_leading_byte_order_mark()
    {
        var (code, out_, _) = TestPaths.RunWithStdin("\uFEFFmetrics/dau\n", "verify", OkfV02, "--by", "human:ada", "--dry-run", "-");
        Assert.Equal(0, code);
        Assert.Equal("would record metrics/dau  human:ada  (now)\n", out_);
    }
```
(`Run(params string[])` and `TestPaths.RunWithStdin(stdin, args)` are the file's existing helpers; add `private static readonly string OkfV02 = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02");` beside `BundlePath`.)

- [ ] **Step 2: Run** — first test fails (raw newline in stderr), second fails (`unknown concept "\uFEFFmetrics/dau"`).

- [ ] **Step 3: Fix the CLI** — in `CmdVerify`: `throw new CliOperationException($"unknown concept {DebugQuote.Quote(id)}");`, `$"concept {DebugQuote.Quote(id)} has no `type` …"`, `$"concept {DebugQuote.Quote(duplicate.Key)} is named more than once"`. `DebugQuote.Quote` wraps in double quotes itself — do not add a second pair. In `ReadIdsFrom`, before `Trim()`: `if (ids.Count == 0 && line.StartsWith('\uFEFF')) { line = line[1..]; }` with a comment: Console.In does not strip a preamble from redirected input and `Trim()` does not treat U+FEFF as whitespace.

- [ ] **Step 4: Fix the tool and the writer** — `OkfBundleTools.Verify`: `$"Error: concept {DebugQuote.Quote(id)} does not exist."` / `… has no `type` …`; `BundleConceptWriter.RecordVerifications`: the three `concept '{conceptIds[i]}'` messages → `concept {DebugQuote.Quote(conceptIds[i])}`; `ValidateConceptTarget`'s `invalid concept id '{conceptId}'` likewise. Update the tests that pin those exact strings (`rg -n "concept '" tests/OKF4net.Tests`), keeping single quotes only where the id is not interpolated.

- [ ] **Step 5: Run** the CLI + verify-tool + writer tests, then `GoldenParityTests` (the `verify.out` golden echoes no id in an error, so it must stay byte-identical — if it changes, the fix is wrong, not the golden). **Step 6: CHANGELOG** (`### Fixed`): "`okf verify`, `okf_verify` and `RecordVerifications` now quote-escape a concept id in every error they echo, closing for the positional the line-forging hole `LineSafeText` closed for `--by`/`--at`; `okf verify -` tolerates a UTF-8 BOM on the first line." **Step 7: Commit** `fix(verify): escape echoed concept ids, strip a stdin BOM`.

### Task C2: A repeated valued flag is an error (finding #7)

**Files:**
- Modify: `src/OKF4net.Cli/OkfCli.cs` (`CliArgs.TakeValuedFlag`)
- Test: `tests/OKF4net.Tests/CliTests.cs`

- [ ] **Step 1: Failing test**

```csharp
    [Fact]
    public void A_valued_flag_given_twice_is_refused_rather_than_first_wins()
    {
        var (code, _, err) = Run("audit", OkfV02, "--as-of", "2020-01-01", "--as-of", "2099-01-01");
        Assert.Equal(1, code);
        Assert.Equal("error: option --as-of given more than once\n", err);
    }
```

- [ ] **Step 2: Fix `TakeValuedFlag`** — replace the "first occurrence wins" branch:

```csharp
            // Refused, not first-wins: a script that appends an override flag
            // got the EARLIER value with no diagnostic, the exact "silently
            // different behaviour than asked for" this scanner exists to stop.
            if (_flags.ContainsKey(token))
            {
                throw new CliOperationException($"option {token} given more than once");
            }

            _flags[token] = hasValue ? args[i + 1] : null;
```
Delete or update the existing test asserting first-wins (`rg -n "first.*wins|wins" tests/OKF4net.Tests/CliTests.cs`).

- [ ] **Step 3: Run** CLI tests + goldens. **Step 4: CHANGELOG** (`### Changed`): "A valued flag given twice (`--by`, `--at`, `--as-of`, `--trust`, `--status`, `--type`, `--out`) is now `error: option … given more than once` instead of silently keeping the first." **Step 5: Commit** `fix(cli): refuse a repeated valued flag`.

### Task C3: A §5 timestamp must carry a date (finding #5)

**Files:**
- Modify: `src/OKF4net/Internal/OkfTimestamp.cs` (`Classify`)
- Test: `tests/OKF4net.Tests/OkfTimestampTests.cs` (or wherever `Classify` is tested — `rg -n "Classify(" tests/OKF4net.Tests`)

- [ ] **Step 1: Failing test**

```csharp
    [Theory]
    [InlineData("10:00Z")]
    [InlineData("10:00+02:00")]
    [InlineData("T10:00:00Z")]
    public void A_value_without_a_date_is_unreadable_not_todays_date(string raw)
    {
        Assert.Equal(TimestampForm.Unreadable, OkfTimestamp.Classify(raw, out _));
    }

    [Fact]
    public void A_readable_non_iso_spelling_that_has_a_date_is_still_evaluated()
    {
        Assert.Equal(TimestampForm.NonIso8601, OkfTimestamp.Classify("2026-06-30 10:00Z", out var instant));
        Assert.Equal(new DateTimeOffset(2026, 6, 30, 10, 0, 0, TimeSpan.Zero), instant);
    }
```

- [ ] **Step 2: Fix** — in `Classify`, guard the offset branch:

```csharp
        if (HasExplicitOffset(raw)
            && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var withOffset)
            && CarriesADate(raw))
```
and add:

```csharp
    /// <summary>
    /// DateTimeOffset.TryParse fills a missing date with the machine's wall-clock
    /// date, so `10:00Z` read as "today at 10:00" -- a staleness that flipped within
    /// the day, per machine, ignoring --as-of. DateTimeOffset does not support
    /// NoCurrentDateDefault, so the date's presence is probed through DateTime,
    /// where it does: a value with no date lands on year 1.
    /// </summary>
    private static bool CarriesADate(string raw) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var probe)
        && probe.Year > 1;
```

- [ ] **Step 3: Run** the timestamp tests + `BundleValidatorTests` + goldens. **Step 4: CHANGELOG** (`### Fixed`): "A §5 timestamp with no date part (`stale_after: 10:00Z`) is now unreadable — warned and never evaluated — instead of being read as today at that time, which made staleness flip during the day, per machine, ignoring `--as-of`." **Step 5: Commit** `fix(timestamp): a value with no date is unreadable, not today`.

### Task C4: One clock read per run; `Now` is the clock's primitive; JSON reports the evaluation instant (findings #10)

**Files:**
- Modify: `src/OKF4net/IOkfClock.cs`, `src/OKF4net.Cli/OkfCli.cs` (`CmdValidate`, `CmdAudit`), `src/OKF4net.Cli/JsonOutput.cs` (`WriteValidate`, `WriteAudit` + the two records), `src/OKF4net/Audit.cs` (`AuditReport.EvaluatedAt`), `tests/fixtures/golden/audit-v02.json`, `tests/fixtures/README.md`, `README.md` (the `--json` field tables)
- Test: `tests/OKF4net.Tests/ClockTests.cs`, `tests/OKF4net.Tests/CliTests.cs`, `tests/OKF4net.Tests/JsonOutputTests.cs` (if present), `GoldenParityTests`

**Interfaces:**
- `IOkfClock`: `DateTimeOffset Now { get; }` becomes the required member; `DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);` becomes the default member. **Breaking (0.x):** a `Today`-only implementer no longer compiles — deliberately, since it silently evaluated every §5.5 comparison at midnight.
- `AuditReport` gains `public DateTimeOffset EvaluatedAt { get; }` (the single clock read; `AsOf` stays its date).
- `validate --json` and `audit --json` gain `"evaluatedAt": "<yyyy-MM-ddTHH:mm:ssZ>"` right after `"asOf"`.

- [ ] **Step 1: Failing tests**

```csharp
    // ClockTests.cs -- replace TodayOnlyClock with its mirror
    [Fact]
    public void Today_is_derived_from_Now()
    {
        IOkfClock clock = new NowOnlyClock(new DateTimeOffset(2026, 7, 1, 23, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateOnly(2026, 7, 1), clock.Today);
    }

    private sealed class NowOnlyClock(DateTimeOffset now) : IOkfClock
    {
        public DateTimeOffset Now { get; } = now;
    }

    // CliTests.cs
    [Fact]
    public void Validate_json_reports_the_instant_it_evaluated_at()
    {
        var (_, out_, _) = Run("validate", OkfV02, "--as-of", "2099-06-01", "--json");
        Assert.Contains("\"asOf\":\"2099-06-01\",\"evaluatedAt\":\"2099-06-01T00:00:00Z\"", out_, StringComparison.Ordinal);
    }
```

- [ ] **Step 2: Fix `IOkfClock`** — swap the primitive: `Now` abstract, `Today` default; update `SystemClock` (both explicit, unchanged) and `FixedClock` (drop its `Today` property — the default now covers it). XML doc on the interface: why `Now` is the primitive since the §5 instant rewrite.

- [ ] **Step 3: One read in `CmdValidate`**

```csharp
        var clock = ParseAsOf(parsed) ?? new SystemClock();
        // ONE read: the instant staleness is evaluated at is the instant the
        // report says it evaluated at. Two reads straddling midnight produced an
        // asOf that omitted a concept stale on that very date.
        var evaluatedAt = clock.Now;
        var pinned = new FixedClock(evaluatedAt);
        var path = parsed.Positional("<bundle>");
        var bundle = Load(path);
        var report = BundleValidator.Validate(bundle, pinned);
        if (parsed.Has("--json"))
        {
            JsonOutput.WriteValidate(stdout, path, evaluatedAt, bundle, report);
```
`WriteValidate` takes `DateTimeOffset evaluatedAt` and writes `asOf` = its date and `evaluatedAt` = `OkfTimestamp.FormatUtc(evaluatedAt.UtcDateTime)`. In `ConceptAudit.Run` keep the single `clock.Now` read and store it in `AuditReport.EvaluatedAt`; `WriteAudit` writes it after `asOf`.

- [ ] **Step 4: The golden** — `tests/fixtures/golden/audit-v02.json` was captured with `--as-of 2099-06-01`; insert `"evaluatedAt":"2099-06-01T00:00:00Z",` after `"asOf":"2099-06-01",`. In `tests/fixtures/README.md`, in the `audit-v02.json` entry, add one line: "2026-09-12: gained `evaluatedAt` (the §5.5 evaluation instant; `asOf` is its date) — hand-verified against §5.5, this fixture is a v0.2 hand-authored capture, not a reference-binary capture." Update the JSON field tables in `README.md` (`okf validate --json`, `okf audit --json`).

- [ ] **Step 5: Run** `--filter "FullyQualifiedName~ClockTests|FullyQualifiedName~CliTests|FullyQualifiedName~GoldenParityTests|FullyQualifiedName~AuditTests"` then the CI filter. **Step 6: CHANGELOG**: `### Changed` — "**Breaking (0.x):** `IOkfClock.Now` is the required member and `Today` derives from it — a `Today`-only clock written against 0.5.0 evaluated every §5.5 instant comparison at 00:00Z without a compile-time hint; it now fails to compile instead. `validate --json` and `audit --json` report `evaluatedAt`, the exact instant staleness was evaluated at (`asOf` remains its date), read from the clock exactly once per run." **Step 7: Commit** `fix(clock): Now is the primitive, one read per run, evaluatedAt in JSON`.

### Task C5: `StalePolicy.Tolerate(0)` is `Strict` (finding #17 boundary)

**Files:** `src/OKF4net/StalePolicy.cs`; test `tests/OKF4net.Tests/StalePolicyTests.cs` (or `CatalogTests` — `rg -n "Tolerate(" tests`)

- [ ] **Step 1: Failing test**

```csharp
    [Fact]
    public void Tolerate_zero_agrees_with_Strict_at_the_exact_instant()
    {
        var lc = Lifecycle.From(null, "2026-06-30T00:00:00Z");
        var at = new DateTimeOffset(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(StalePolicy.Strict.Admits(lc, at), StalePolicy.Tolerate(0).Admits(lc, at));
        Assert.False(StalePolicy.Tolerate(0).Admits(lc, at));
    }
```

- [ ] **Step 2: Fix** — `StaleMode.Tolerate => lc.StaleAfter is not { } d || now < d.AddDays(GraceDays),` with a comment: `IsStale` is `now >= stale_after` (§5.5), so the grace window's far edge is exclusive too; `Tolerate(0)` and `Strict` now answer identically at the boundary. **Step 3: Run** + CHANGELOG (`### Fixed`) + commit `fix(stale): Tolerate's grace edge is exclusive like IsStale`.

### Task C6: `okf_audit` trims `type` like it trims `status` (finding #17)

**Files:** `src/OKF4net.Agents/OkfBundleTools.cs` (`Audit` tool, where `status` is trimmed); test `tests/OKF4net.Tests/Agents/OkfAuditToolTests.cs`

- [ ] **Step 1: Failing test** — call the audit tool with `type: "Metric "` on the `okf_v02` fixture and assert the worklist names `metrics/dau` (same assertion the existing `type: "Metric"` test makes). **Step 2: Fix** — `var type = string.IsNullOrWhiteSpace(rawType) ? null : rawType.Trim();` beside the `status` trim, comment "same treatment as `status`/`trust`: a model copying a label from prose brings whitespace". **Step 3: Run + CHANGELOG (`### Fixed`) + commit** `fix(agents): okf_audit trims the type filter`.

### Task C7: `RecordVerifications` edits only the `verified` block, byte-preserving everything else (finding #11, arbitration: surgical)

**Files:**
- Create: `src/OKF4net/Internal/FrontmatterBlockEdit.cs`
- Modify: `src/OKF4net/BundleConceptWriter.cs` (`RecordVerifications` prepare loop), `README.md` (`okf verify` section: what is and is not preserved)
- Test: new `tests/OKF4net.Tests/FrontmatterBlockEditTests.cs`, `tests/OKF4net.Tests/BundleConceptWriterTests.cs` (verify section)

**Interfaces:**
- Produces: `internal static string FrontmatterBlockEdit.ReplaceTopLevelKey(string documentText, string key, string emittedBlock)` — `documentText` is the whole file (any line ending); `emittedBlock` is the YAML for that key as `YamlEmitter` produces it (`key:\n  - by: …\n    at: …\n`, LF-terminated). Returns the whole file with the existing top-level `key` block replaced, or the block inserted just before the closing `---` when absent, using the document's own line ending. Throws `OkfException`-derived `DocumentValidationException` if the text has no frontmatter fence.
- Block boundary rule: the block starts at the line matching `^key:` (column 0) and ends before the next line that starts at column 0 with a non-space, non-`#` character, or before the closing `---`. Column-0 comment lines inside the block are kept with the block (and therefore replaced) — documented.

- [ ] **Step 1: Failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;
using Xunit;

namespace OKF4net.Tests;

public class FrontmatterBlockEditTests
{
    private const string Block = "verified:\n  - by: human:ada\n    at: 2026-07-01T00:00:00Z\n";

    [Fact]
    public void Replaces_an_existing_block_and_keeps_every_other_byte()
    {
        var doc = "---\r\ntype: Metric\r\n# reviewed quarterly\r\ndescription: >\r\n  Daily\r\n  users.\r\nverified:\r\n  - by: human:bob\r\n    at: 2025-01-01T00:00:00Z\r\ntags: [a, b]\r\n---\r\n# Body\r\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\r\ntype: Metric\r\n# reviewed quarterly\r\ndescription: >\r\n  Daily\r\n  users.\r\nverified:\r\n  - by: human:ada\r\n    at: 2026-07-01T00:00:00Z\r\ntags: [a, b]\r\n---\r\n# Body\r\n", edited);
    }

    [Fact]
    public void Inserts_before_the_closing_fence_when_the_key_is_absent()
    {
        var doc = "---\ntype: Metric\ntitle: T\n---\nbody\n";
        Assert.Equal("---\ntype: Metric\ntitle: T\nverified:\n  - by: human:ada\n    at: 2026-07-01T00:00:00Z\n---\nbody\n", FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block));
    }

    [Fact]
    public void A_flow_valued_key_on_one_line_is_replaced_whole()
    {
        var doc = "---\ntype: Metric\nverified: [{by: human:bob, at: 2025-01-01T00:00:00Z}]\ntitle: T\n---\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.Equal("---\ntype: Metric\n" + Block + "title: T\n---\n", edited);
    }

    [Fact]
    public void A_key_that_is_a_prefix_of_another_is_not_matched()
    {
        var doc = "---\nverified_by_policy: x\ntype: Metric\n---\n";
        var edited = FrontmatterBlockEdit.ReplaceTopLevelKey(doc, "verified", Block);
        Assert.StartsWith("---\nverified_by_policy: x\ntype: Metric\n" + Block, edited, System.StringComparison.Ordinal);
    }

    [Fact]
    public void No_frontmatter_fence_is_refused()
    {
        Assert.Throws<DocumentValidationException>(() => FrontmatterBlockEdit.ReplaceTopLevelKey("# just a body\n", "verified", Block));
    }
}
```

- [ ] **Step 2: Implement**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// Replaces or inserts ONE top-level frontmatter key's block in a document's
/// raw text, leaving every other byte — comments, line endings, scalar
/// spellings, key order — untouched. This is what lets
/// <c>BundleConceptWriter.RecordVerifications</c> stamp <c>verified</c>
/// without re-emitting the whole frontmatter through <c>YamlEmitter</c>, which
/// normalized CRLF to LF, dropped comments and reflowed folded scalars.
///
/// <para>Block rule: a key's block starts at the line <c>key:</c> in column 0
/// and ends before the next line whose first character is neither a space nor
/// <c>#</c> (the next top-level key), or before the closing <c>---</c>. A
/// column-0 comment line inside the block belongs to it and is replaced with
/// it. The document is re-parsed by the caller after the edit; this type does
/// not validate YAML.</para>
/// </summary>
internal static class FrontmatterBlockEdit
{
    internal static string ReplaceTopLevelKey(string documentText, string key, string emittedBlock)
    {
        var newline = documentText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = LfLines.Split(documentText);   // "\n"-split, "\r" stripped
        var trailingNewline = documentText.EndsWith('\n');

        if (lines.Count == 0 || lines[0] != "---")
        {
            throw new DocumentValidationException("document has no frontmatter fence to edit", []);
        }

        var close = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i] == "---") { close = i; break; }
        }

        if (close < 0)
        {
            throw new DocumentValidationException("document has no closing frontmatter fence", []);
        }

        var prefix = key + ":";
        var start = -1;
        for (var i = 1; i < close; i++)
        {
            if (lines[i].StartsWith(prefix, StringComparison.Ordinal)
                && (lines[i].Length == prefix.Length || lines[i][prefix.Length] is ' ' or '\t'))
            {
                start = i;
                break;
            }
        }

        var end = close;
        if (start >= 0)
        {
            end = start + 1;
            while (end < close && (lines[end].Length == 0 || lines[end][0] is ' ' or '\t' or '#'))
            {
                end++;
            }
        }
        else
        {
            start = close;
        }

        var block = LfLines.Split(emittedBlock.TrimEnd('\n'));
        var result = new List<string>(lines.Count + block.Count);
        result.AddRange(lines.Take(start));
        result.AddRange(block);
        result.AddRange(lines.Skip(end));

        var text = string.Join(newline, result);
        return trailingNewline ? text + newline : text;
    }
}
```
(`LfLines.Split` returns a `List<string>`; if it keeps a trailing empty element for a final `\n`, drop it before joining — check its contract and adjust `trailingNewline` handling so the round trip on an untouched document is byte-identical; add a test `Untouched_document_round_trips_when_the_block_is_identical` for a CRLF and an LF document.)

- [ ] **Step 3: Use it in `RecordVerifications`** — in the prepare loop, keep `OkfDocument.Parse(text)` + `UpsertStamp` (they compute the new list and `replacedAt`), then instead of `BuildConformantContent(map, document.Body)`:

```csharp
                    var one = new YamlMapping();
                    one.Insert("verified", upserted);
                    var stampBlock = YamlEmitter.Emit(one);   // the same emitter OkfDocument.Serialize uses, on a one-key mapping
                    var content = FrontmatterBlockEdit.ReplaceTopLevelKey(text, "verified", stampBlock);

                    // Re-parsed and validated: the edit is textual, so this is
                    // the proof the result is still a conformant document and
                    // the stamp landed where a reader will find it.
                    var reparsed = OkfDocument.Parse(content);
                    reparsed.ValidateConformance();
                    if (reparsed.Frontmatter.Verified.Count != Trust.ParseVerified(upserted).Count)
                    {
                        return $"Error: concept {DebugQuote.Quote(conceptIds[i])}: the verified block could not be edited in place.";
                    }
```
`upserted` is the `YamlValue` `UpsertStamp` returns (keep the `map.Insert("verified", …)` call too, so `replacedAt` is computed exactly as before). `MaybeStampGenerated` must NOT run on this path (the surgical edit stamps only `verified`); note it in the comment.

- [ ] **Step 4: Writer tests** — in `BundleConceptWriterTests.cs` (verify section) add: a CRLF document with a `# comment`, a folded `description: >` and `tags: [a, b]` is stamped and the file differs from the original **only** in the `verified` lines (assert by removing those lines from both and comparing byte-equal). Keep the existing behavioural tests (replace vs add, `ReplacedAt`).

- [ ] **Step 5: Run** `--filter "FullyQualifiedName~FrontmatterBlockEditTests|FullyQualifiedName~BundleConceptWriterTests|FullyQualifiedName~CliTests|FullyQualifiedName~GoldenParityTests"` — `verify-dau.md` golden must still match (it is LF, no comments; if bytes differ, the block emission differs from before — align the emitted block to what `YamlEmitter` produced through `BuildConformantContent`, never the golden).

- [ ] **Step 6: README** `okf verify` section: "The stamp is edited in place: only the `verified:` block changes; comments, line endings, key order and scalar spellings elsewhere are preserved byte for byte (a column-0 comment inside the `verified:` block is replaced with it)." **Step 7: CHANGELOG** (`### Fixed`): "`okf verify` / `okf_verify` no longer rewrite the whole frontmatter: `RecordVerifications` now edits the `verified:` block in place (`FrontmatterBlockEdit`), so CRLF endings, YAML comments and folded/flow spellings elsewhere survive — the contract's 'preserving every other frontmatter key' was previously false." **Step 8: Commit** `fix(core): edit the verified block in place instead of re-emitting the frontmatter`.

### Task C8: One spelling of the emitted UTC stamp (quality: grammar spelled 3×)

**Files:** `src/OKF4net/Internal/OkfTimestamp.cs`, `src/OKF4net/BundleConceptWriter.cs` (`RecordVerifications` `--at` check), `src/OKF4net.Cli/OkfCli.cs` (`CmdVerify` `--at` pre-check, ~line 880); test `tests/OKF4net.Tests/OkfTimestampTests.cs`

- [ ] **Step 1: Failing test**

```csharp
    [Theory]
    [InlineData("2026-07-01T00:00:00Z", true)]
    [InlineData("2026-07-01T00:00:00+00:00", false)]
    [InlineData("2026-07-01", false)]
    [InlineData("2026-07-01T00:00:00.000Z", false)]
    public void IsEmittedUtcForm_accepts_exactly_what_FormatUtc_writes(string raw, bool expected)
    {
        Assert.Equal(expected, OkfTimestamp.IsEmittedUtcForm(raw));
        Assert.True(OkfTimestamp.IsEmittedUtcForm(OkfTimestamp.FormatUtc(new DateTime(2026, 7, 1, 12, 30, 0, DateTimeKind.Utc))));
    }
```

- [ ] **Step 2: Implement** in `OkfTimestamp`:

```csharp
    /// <summary>The exact shape <see cref="FormatUtc"/> writes; the writer and the CLI check `--at` against this and nothing else.</summary>
    internal const string EmittedUtcFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Whether <paramref name="raw"/> is spelled exactly as <see cref="FormatUtc"/> spells a stamp.</summary>
    internal static bool IsEmittedUtcForm(string raw) =>
        DateTime.TryParseExact(raw, EmittedUtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _);
```
and make `FormatUtc` use `EmittedUtcFormat` (`utc.ToString(EmittedUtcFormat, CultureInfo.InvariantCulture)` — note the format string's quoted `'Z'` and `'T'` produce the same bytes as today; pin with the existing `FormatUtc` test). Replace the two `TryParseExact("yyyy-MM-dd'T'HH:mm:ss'Z'", …)` calls (writer + CLI) with `OkfTimestamp.IsEmittedUtcForm(...)`, keeping their messages.

- [ ] **Step 3: Run** writer + CLI + goldens; commit `refactor(timestamp): one predicate for the emitted UTC stamp`.

### Task C9: §6.2 concept-relative hint, `RecommendedFieldsFor`, and the combined Breaking entry (arbitration: no fallback + warn; quality: carve-out in wrong layer; release readiness)

**Files:** `src/OKF4net/Validate.cs` (resource-resolution diagnostics + the recommended-fields loop), `src/OKF4net/Frontmatter.cs` (`RecommendedFieldsFor`), `CHANGELOG.md`; tests `tests/OKF4net.Tests/BundleValidatorTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
    [Fact]
    public void A_missing_bare_path_that_exists_beside_the_concept_gets_a_concept_relative_hint()
    {
        using var tmp = new TempDir();
        tmp.Write("computations/rev.md", "---\ntype: Attested Computation\ntitle: R\ndescription: D\nruntime: python\ncomputation: query.sql\n---\n");
        tmp.Write("computations/query.sql", "SELECT 1");
        tmp.Write("index.md", "---\ntype: Index\ntitle: I\ndescription: D\n---\n");
        var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
        var d = Assert.Single(report.Diagnostics, x => x.Code == DiagnosticCode.FrontmatterPathMissing);
        Assert.Contains("a file exists at computations/query.sql; a bare path resolves from the bundle root (§6.2) — write ./query.sql for the concept-relative form", d.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendedFieldsFor_omits_resource_for_an_attested_computation_without_the_key()
    {
        var fm = OkfDocument.Parse("---\ntype: Attested Computation\nruntime: python\n---\n").Frontmatter;
        Assert.DoesNotContain("resource", Frontmatter.RecommendedFieldsFor(fm));
        var withEmpty = OkfDocument.Parse("---\ntype: Attested Computation\nresource: {}\n---\n").Frontmatter;
        Assert.Contains("resource", Frontmatter.RecommendedFieldsFor(withEmpty));
    }
```

- [ ] **Step 2: `Frontmatter.RecommendedFieldsFor`** — move the carve-out definition here:

```csharp
    /// <summary>The §4.1 recommended keys this document is expected to carry: <see cref="RecommendedFields"/>, minus <c>resource</c> for an Attested Computation that declares no <c>resource</c> key at all (§4.1: such a concept is abstract). A declared-but-unusable value (`resource: {}`, `null`, `[]`, …) is not the carve-out — it keeps warning as a malformed value.</summary>
    public static IReadOnlyList<string> RecommendedFieldsFor(Frontmatter frontmatter) =>
        frontmatter.IsAttestedComputation && frontmatter.Get("resource") is null
            ? RecommendedFields.Where(f => f != "resource").ToArray()
            : RecommendedFields;
```
(`RecommendedFields` = the existing list the validator loops over — if it lives in `Validate.cs`, move the constant to `Frontmatter` next to `RequiredKeys`.) In `Validate.cs`, loop over `Frontmatter.RecommendedFieldsFor(fm)` and delete the `if (value is null && field == "resource" && fm.IsAttestedComputation) continue;` branch and its long comment (move the two-sentence rationale onto `RecommendedFieldsFor`).

- [ ] **Step 3: The hint** — where `FrontmatterPathMissing` is emitted after `bundle.TryResolveResource(...)` returns `Missing`: compute `var conceptRelative = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(concept.Path)!, rawPath))`; if `File.Exists(conceptRelative)` **and** `ReparsePoints.IsWithinBundleRoot(bundle.Root, conceptRelative)`, append to the message: `$" — a file exists at {relativeToRoot}; a bare path resolves from the bundle root (§6.2) — write ./{rawPath} for the concept-relative form"` where `relativeToRoot` = `Path.GetRelativePath(bundle.Root, conceptRelative).Replace('\\', '/')`. Same diagnostic code, same severity — only the message grows, so goldens (`validate-computation.out`) are unaffected unless a fixture concept has a file beside it under the same bare name (it does not; verify with the golden run).

- [ ] **Step 4: Run** validator tests + goldens. **Step 5: CHANGELOG** — add a dedicated `### Changed` entry at the top of the section:

```markdown
- **Breaking (combined effect on 0.5.0 bundles): a bare `attester.resource` /
  `computation` path that used to resolve beside the concept now resolves
  from the bundle root (§6.2), AND an unresolvable `attester.resource` now
  fails the run closed — together, a bundle whose attester script sits next
  to its concept goes from running under 0.5.0 to never executing.** There is
  deliberately no fallback (the spec's Appendix A resolves bare paths from the
  root); instead `okf validate` now says where the file was found and what to
  write (`./script.py`). Also: `resource` is omitted from an Attested
  Computation's recommended fields by `Frontmatter.RecommendedFieldsFor`, the
  one definition of §4.1's carve-out.
```
**Step 6: Commit** `feat(validate): hint at the concept-relative file a bare §6.2 path misses; RecommendedFieldsFor`.

### Task C10: `LfLines` everywhere a body is split (quality)

**Files:** `src/OKF4net/ConceptSearch.cs` (`Excerpt`), `src/OKF4net.Agents/OkfContextProvider.cs` (the `content.Split('\n')` truncation, ~line 646); tests `ConceptSearchTests`, `OkfContextProviderTests`

- [ ] **Step 1: Failing tests** — `Excerpt("line one\r\nneedle here\r\n", "needle")` returns `"needle here"` with no `\r` **even when `Trim()` is removed** (make the test assert `Assert.DoesNotContain('\r', excerpt)` after changing the implementation to not rely on `Trim` for `\r`); for the provider, a CRLF concept truncated to a budget yields lines with no `\r` (find the existing truncation test and add a CRLF variant).
- [ ] **Step 2: Fix** — `foreach (var rawLine in LfLines.Split(body))` and `var lines = LfLines.Split(content);` (`OKF4net.Agents` has `InternalsVisibleTo`). **Step 3: Run + commit** `refactor: split bodies with LfLines in ConceptSearch and the context provider`.

### Task C11: Audit/verify labels and lines live once (quality: `TrustLabel`/`StatusLabel` copies; audit summary and `recorded` line hand-formatted twice)

**Files:**
- Create: `src/OKF4net/AuditText.cs`
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs` (`ReadConcept` status/trust line, `RenderAudit`, `Verify`, delete `StatusLabel`/`TrustLabel`), `src/OKF4net.Cli/OkfCli.cs` (`WriteAuditReport`, `FormatAuditFinding`, `CmdVerify` record line)
- Test: existing golden `audit-v02.out`, `verify.out` (bytes must not move), `OkfAuditToolTests`, `OkfVerifyToolTests`

**Interfaces:** `public static class AuditText` (drop to `internal` if `VerificationRecord` or `AuditFinding` turns out to be internal — `okf` and `OKF4net.Agents` already have `InternalsVisibleTo`) with `static void WriteSummary(TextWriter w, AuditReport r)` (the `as of:` … `stale:` lines, exact bytes of today's CLI renderer), `static string FormatFinding(AuditFinding f)` (the id/freshness/trust/status line), and `static string FormatVerificationRecord(VerificationRecord r, string by)` (`recorded {id}  {by}  {at}[  (replaces …)]`).

- [ ] **Step 1: Pin first** — run `GoldenParityTests` and copy `audit-v02.out`'s expected summary into a new unit test `AuditTextTests.WriteSummary_matches_the_cli_bytes` that calls `AuditText.WriteSummary` on `ConceptAudit.Run(okf_v02, stale: true, FixedClock(2099-06-01))` and asserts the exact lines 2–13 of the golden (everything after the `bundle:` line up to `needs attention`). It fails to compile now.
- [ ] **Step 2: Create `AuditText`** by moving the bodies of `WriteAuditReport` (minus the `bundle:` line and the worklist heading) and `FormatAuditFinding` verbatim; `FormatVerificationRecord` returns exactly the string both renderers build today.
- [ ] **Step 3: Rewire** — CLI: `stdout.Write($"bundle:     {bundlePath}\n"); AuditText.WriteSummary(stdout, report);` then the heading/worklist using `AuditText.FormatFinding`; tool: `RenderAudit` writes `AuditText.WriteSummary` into a `StringWriter` then its own heading/cap; `ReadConcept` uses `AuditVocabulary.Name(...)`; both verify renderers use `AuditText.FormatVerificationRecord`. Delete `StatusLabel`/`TrustLabel`.
- [ ] **Step 4: Run** goldens + audit/verify tool tests — bytes identical. **Step 5: Commit** `refactor: one AuditText for the audit summary, finding and verification lines`.

### Task C12: Memory concepts are built by `OkfDocumentBuilder` (quality: hand-emitted YAML)

**Files:** `src/OKF4net.Agents/OkfContextProvider.cs` (`MemoryFrontmatter` and its caller), `src/OKF4net/BundleConceptWriter.cs` (`ProducerActor` stays internal; Agents reads it); tests `OkfContextProviderTests` (memory capture)

- [ ] **Step 1: Failing test** — capture a memory concept whose date string makes the title contain `: ` (e.g. force `dateStr` via the provider's clock seam to produce a title like `Agent memory 2026-07-01`, then assert the written file's `title` round-trips through `OkfDocument.Parse` and `Frontmatter.Generated.By == "okf4net/" + OkfSpec.Version`). Add a second case where the description contains a colon-space (`Captured user/agent exchanges for: 2026-07-01`) and assert `OkfDocument.TryParse` succeeds and `Description` equals the intended text.
- [ ] **Step 2: Fix** — replace `MemoryFrontmatter` with

```csharp
    private static string MemoryFrontmatter(string dateStr, DateTime now)
    {
        var doc = OkfDocumentBuilder.ForType("AgentMemory")
            .Title($"Agent memory {dateStr}")
            .Description($"Captured user/agent exchanges for {dateStr}.")
            .Extension("generated", GeneratedStamp(now))
            .Build();
        // AppendToConceptAtomic takes the frontmatter YAML alone (no fences, no
        // body), so emit the mapping through the same emitter Serialize uses.
        return YamlEmitter.Emit(doc.Frontmatter.AsMapping());
    }
```
where `GeneratedStamp` builds a `YamlMapping` with `by` = `BundleConceptWriter.DefaultProducerActor` (add `internal static string DefaultProducerActor => "okf4net/" + OkfSpec.Version;` next to `ProducerActor` and make `ProducerActor` default to it) and `at` = `OkfTimestamp.FormatUtc(now)`. Keep the existing doc comment's point (this is the only stamp source on these paths).
- [ ] **Step 3: Run** provider tests + commit `refactor(agents): build memory frontmatter with OkfDocumentBuilder`.

### Task C13: The writer attributes its own §11 failure; the two pre-loops go; the tool refreshes instead of dropping its cache (quality/efficiency)

**Files:** `src/OKF4net/BundleConceptWriter.cs` (`RecordVerifications` prepare loop), `src/OKF4net.Cli/OkfCli.cs` (`CmdVerify`), `src/OKF4net.Agents/OkfBundleTools.cs` (`Verify`, `onWriteCommitted`); tests `BundleConceptWriterTests`, `CliTests`, `OkfVerifyToolTests`

- [ ] **Step 1: Failing writer test** — a batch `["metrics/dau", "metrics/notype"]` where `notype.md` has no `type`: `outcome.Message == "Error: concept \"metrics/notype\" has no `type` and is not §11-conformant."` and nothing written.
- [ ] **Step 2: Writer** — in the prepare loop wrap the parse/validate in `try { … } catch (DocumentValidationException e) { return $"Error: concept {DebugQuote.Quote(conceptIds[i])}: {e.Message}"; }` and add an explicit `type` check before it producing the message above. Then delete the pre-loops in `CmdVerify` (keep the duplicate-id check and the dry-run) and in the tool's `Verify`; the CLI maps the writer's `Error: …` to `CliOperationException` as it already does. The CLI no longer calls `Load(path)` for verify at all (the writer reads the k files it needs).
- [ ] **Step 3: Tool cache** — replace `onWriteCommitted: () => _bundle = null` for verify with a refresh of the k stamped concepts: read each `record.ConceptId` file through `Bundle.Load`-equivalent single-concept parse and replace it in the cached bundle — if `Bundle` has no per-concept replace API, keep `_bundle = null` but only on the verify path **after** the batch (one reload per batch, not per stamp) and note it. Prefer adding `internal void Bundle.Refresh(ConceptId id)` if it is a ≤ 20-line change.
- [ ] **Step 4: Run** CLI/tool/writer tests + goldens (`verify.out` bytes unchanged: its error cases must produce the same text — check each; the golden captures `unknown concept` wording, so the writer's message for an unknown id must stay `unknown concept "…"` on the CLI: map `Error: concept "x" does not exist.` → `unknown concept "x"` in `CmdVerify` before throwing). **Step 5: Commit** `refactor(verify): the writer attributes its own failures; drop the duplicated pre-checks`.

### Task C14: One argument scanner for `okf` and `okf-render`; `VerbSpec.Name` goes (quality)

**Files:**
- Create: `src/OKF4net/Internal/CliArgs.cs`, `src/OKF4net/Internal/CliArgumentException.cs`
- Modify: `src/OKF4net.Cli/OkfCli.cs` (delete nested `CliArgs`, adapt `VerbSpec`, wrap `CliArgumentException` → `CliOperationException`), `src/OKF4net.Render/OkfRenderCli.cs` (delete `ParsedArgs`/`Scan`)
- Test: `CliTests`, `tests/OKF4net.Tests/Render/OkfRenderCliTests.cs`, goldens

**Interfaces:** `internal sealed class CliArgs` in `OKF4net.Internal` with `static CliArgs Scan(string[] args, string[] valuedFlags, string[] valuelessFlags, bool variadic, string[] helpFlags)`, `string? Value(string flag)`, `bool Has(string flag)`, `IReadOnlyList<string> Positionals`, `string Positional(string name)` (throws `CliArgumentException("missing <name>")`), `bool WantsHelp`. Error messages exactly as today: `unknown option: X`, `unexpected argument: X`, `X requires a value`, `missing <bundle>`, `option X given more than once`.

- [ ] **Step 1: Pin first** — run `CliTests` + `OkfRenderCliTests` + goldens green; then write one failing render test: `okf-render <bundle> - --out d` — a lone `-` is a positional (today: `unknown option: -`), asserting the error is `unexpected argument: -` (bundle already given). It fails now.
- [ ] **Step 2: Move** the nested `CliArgs` (with `TakeValuedFlag`, `TakeOption`, `TakePositional`, the `--` rule, the lone-dash rule) to `OKF4net.Internal.CliArgs`, replacing `VerbSpec` parameters by the four lists and `CliOperationException` by `CliArgumentException : Exception`. In `OkfCli`, `VerbSpec` loses `Name` (the dictionary key is the name; populate `Verbs` from a `VerbSpec[]`? no — keep the dictionary, drop the first constructor argument on every entry) and `Run` catches `CliArgumentException` where it catches `CliOperationException`. `CliArgs.Scan(args, spec, stdin)` becomes `CliArgs.Scan(args, spec.ValuedFlags, spec.ValuelessFlags, spec.Variadic, HelpFlags)` with `stdin` carried on the `OkfCli` side (a small wrapper record `(CliArgs Args, TextReader Stdin)` or a second parameter to the verb delegate — pick the smaller diff).
- [ ] **Step 3: `okf-render`** — `var parsed = CliArgs.Scan(args, ["--out"], [], variadic: false, ["-h", "--help", "-V", "--version"]); var bundle = parsed.Positional("<bundle>"); var outDir = parsed.Value("--out") ?? throw new CliOperationException("--out requires a value")` (preserve today's exact messages — read the current error strings in `OkfRenderCli` and its tests before editing).
- [ ] **Step 4: Run** all CLI/render tests + goldens — bytes unchanged, the lone-dash test passes. **Step 5: CHANGELOG** (`### Fixed`): "`okf-render` treats a lone `-` as an argument, like `okf` (the two scanners had drifted; there is now one)." **Step 6: Commit** `refactor(cli): one CliArgs scanner shared by okf and okf-render`.

### Task C15: Doc-vs-code and release-readiness lines (findings #16, #6, CLAUDE.md, CHANGELOG gaps)

**Files:** `CHANGELOG.md`, `CLAUDE.md`, `samples/acme-retail-agent/src/AcmeRetailAgent/AcmeRetailAgent.csproj`

- [ ] **Step 1:** CHANGELOG `[Unreleased]` MCP entry (line ~335): "The three write tools" → "The four write tools (`okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes`, `okf_verify`)". Add a sentence: "There is no compatibility shim for `OKF_MCP_READONLY`: a 0.5.0 configuration that relied on the writable default must set `OKF_MCP_WRITABLE=1`."
- [ ] **Step 2:** CHANGELOG: mark the `ComputationTimeout` entry "**Breaking (behaviour):** a run that used to complete after more than two minutes now reports a timeout unless the host sets `ComputationTimeout` (`Timeout.InfiniteTimeSpan` restores 0.5.0's unbounded wait)"; and on the `RunComputationAsync` entry: "`[Obsolete]` is a build error under `TreatWarningsAsErrors` — migrate the call or suppress CS0618 for one version."
- [ ] **Step 3:** CLAUDE.md line 44: after "Every verb is a `VerbSpec` in the `Verbs` table …" add "(the two meta-commands `help`/`version` and their flag forms are dispatched by a `switch` in `Run()` before the table, deliberately — they operate on no bundle)".
- [ ] **Step 4:** `AcmeRetailAgent.csproj`: `Microsoft.Agents.AI` `1.17.0` → `1.20.0`; verify `dotnet build samples/acme-retail-agent/AcmeRetailAgent.sln` succeeds. CHANGELOG (`### Fixed`): "`samples/acme-retail-agent` restores again (NU1605 after the `Microsoft.Agents.AI` 1.20.0 bump)."
- [ ] **Step 5: Commit** `docs: four write tools, release-readiness notes, meta-commands; fix the acme sample's package pin`.

---

## Phase D — `OKF4net.Viewer`

### Task D1: Sanitizer — unwrap instead of flatten, own-property lookups, namespace-proof opaque tags, and harness cases for every rule (finding #15)

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/viewer.js` (`TAG_VALUE_CONSTRAINTS`/`ALLOWED_ATTRS`/`URL_ATTRS` lookups, `OPAQUE_TAGS`, the disallowed-element branch of `sanitize`), `tools/viewer-security-check/run.js`
- Test: `tools/viewer-security-check/run.js` (the only executable guard — every new rule gets a case there), `tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs` (smoke markers only)

- [ ] **Step 1: Failing harness cases** — append to `run.js` after the existing hostile section (a `has()` helper: `const has = (o, k) => Object.prototype.hasOwnProperty.call(o, k);` is not needed in tests):

```js
console.log("Content preservation and lookup hardening:");

check("a disallowed wrapper keeps its sanitized children, not just their text", () => {
  const body = renderBody("<details><summary>S</summary>\n\n**bold** [l](a.md)\n\n</details>", { "a.md": "a.html" });
  assert(body.querySelector("strong"), "the <strong> inside the wrapper was flattened away");
  const a = body.querySelector("a");
  assert(a && a.getAttribute("href") === "a.html", "the rewired link inside the wrapper was lost");
  assert(!body.querySelector("details"), "<details> itself must not survive");
});

check("a table inside a disallowed <div> survives", () => {
  const body = renderBody("<div>\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n</div>");
  assert(body.querySelector("table"), "the table collapsed to text");
});

check("an Object.prototype key does not satisfy the input type constraint", () => {
  const body = renderBody('<input type="constructor">');
  assert(!body.querySelector("input"), "<input type=constructor> survived as a live input");
});

check("Object.prototype keys are not allowed attributes", () => {
  const body = renderBody('<a href="x.md" constructor="1" __proto__="2">l</a>', { "x.md": "x.html" });
  const a = body.querySelector("a");
  assert(a && !a.hasAttribute("constructor") && !a.hasAttribute("__proto__"), "prototype-named attributes survived");
});

check("script inside svg is dropped with its source text", () => {
  const body = renderBody("<svg><script>alert(1)</script></svg>");
  assert(!body.textContent.includes("alert(1)"), "script source leaked as visible text");
});

check("raw-text elements (iframe, xmp, noembed) are dropped with their source", () => {
  for (const tag of ["iframe", "xmp", "noembed", "noframes"]) {
    const body = renderBody(`<${tag}><script>alert(1)</script></${tag}>`);
    assert(!body.textContent.includes("alert(1)"), `${tag} source leaked as visible text`);
  }
});

console.log("Scheme obfuscation (each rule of isSafeUrl has a case):");
for (const [name, href] of [
  ["entity-encoded tab", "java&#9;script:alert(1)"],
  ["percent-encoded tab", "java%09script:alert(1)"],
  ["double-encoded tab", "java%2509script:alert(1)"],
  ["newline inside the scheme", "java\nscript:alert(1)"],
  ["leading control characters", "javascript:alert(1)"],
]) {
  check(`${name} does not produce a live javascript: href`, () => {
    const body = renderBody(`<a href="${href}">t</a>`);
    const a = body.querySelector("a");
    assert(a && !a.hasAttribute("href"), `href survived for ${name}`);
  });
}

check("<template> content is never rendered", () => {
  const body = renderBody("<template><img src=x onerror=alert(1)></template>");
  assert(!body.querySelector("img") && !body.innerHTML.includes("onerror"), "template content leaked");
});

check("<base> is dropped", () => {
  assert(!renderBody('<base href="javascript:alert(1)//">').querySelector("base"), "<base> survived");
});

check("srcset is not an allowed attribute", () => {
  const img = renderBody('<img src="a.png" srcset="javascript:alert(1)">').querySelector("img");
  assert(img && !img.hasAttribute("srcset"), "srcset survived");
});
```
Run `npm test` — the first six fail (content flattened; `type=constructor` survives; attributes survive; svg/iframe text leaks).

- [ ] **Step 2: Fix `viewer.js`**
  - `passesTagValueConstraint`: `var constraint = Object.prototype.hasOwnProperty.call(TAG_VALUE_CONSTRAINTS, node.tagName) ? TAG_VALUE_CONSTRAINTS[node.tagName] : null; … return Object.prototype.hasOwnProperty.call(constraint.values, actual);`
  - `sanitizeAttributes`: `if (!Object.prototype.hasOwnProperty.call(allowed, name)) { … }` and `if (Object.prototype.hasOwnProperty.call(URL_ATTRS, name) && …)`.
  - `OPAQUE_TAGS = { SCRIPT: 1, STYLE: 1, IFRAME: 1, NOEMBED: 1, NOFRAMES: 1, XMP: 1, PLAINTEXT: 1, TEMPLATE: 1, BASE: 1 }` and compare on `node.tagName.toUpperCase()` (SVG/MathML-namespace elements report a lowercase `tagName`). Update the OPAQUE_TAGS comment: raw-text/RCDATA elements' content is source, not prose, and a foreign-namespace `script` is still a script.
  - Disallowed-element branch: unwrap instead of `createTextNode(node.textContent)`:

```js
        // Drop the element but KEEP its children (already sanitized: the walk is
        // back-to-front, so every descendant was resolved before its ancestor).
        // Replacing with .textContent flattened sanitized links and tables inside
        // a <details> or <div> into prose -- a milder form of the content loss
        // that got marked's renderer hooks removed. Nothing about the element
        // itself (tag, attributes) survives; its subtree does.
        var parent = node.parentNode;
        while (node.firstChild) { parent.insertBefore(node.firstChild, node); }
        parent.removeChild(node);
        continue;
```
  (Opaque tags are removed **before** this branch, so their text never reaches an unwrap.)

- [ ] **Step 3: Run** `npm test` — all pass (old + new). Run `dotnet test --filter "FullyQualifiedName~ViewerAssetsTests"` and update its marker strings if it grepped `createTextNode` (it must say it is a smoke check).
- [ ] **Step 4: CHANGELOG** (`### Fixed`): "The viewer sanitizer unwraps a disallowed element instead of flattening its subtree to text (a link or table inside `<details>`/`<div>` survives); its constraint and attribute allowlists are own-property lookups (`<input type=\"constructor\">` and `constructor=`/`__proto__=` attributes no longer pass); `<script>`/`<style>` in a foreign namespace and raw-text elements (`iframe`, `xmp`, `noembed`, `noframes`, `plaintext`, `template`, `base`) are dropped with their source. Every scheme-obfuscation rule now has a harness case. None of these was an XSS; all three were gaps between the sanitizer's comments and its code." **Step 5: Commit** `fix(viewer): unwrap disallowed wrappers, own-property lookups, namespace-proof opaque tags, harness cases`.

### Task D2: Case-variant concept ids cannot silently overwrite one output file (finding: `SiteModel` collision)

**Files:** `src/OKF4net.Viewer/HtmlWriter.cs` (`Write`), tests `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs`

- [ ] **Step 1: Failing test** — build a `ViewerSite` with two pages whose `RelativeHtmlPath` are `users.html` and `Users.html` (construct `ViewerPage` records directly) and assert `HtmlWriter.Write(site, outDir)` throws `ArgumentException` whose message contains both ids and "case-insensitive".
- [ ] **Step 2: Fix** — at the top of `Write`, before any file is written:

```csharp
        // Two ids that differ only by case are two concepts on a case-sensitive
        // bundle volume and ONE file on a case-insensitive output volume, where
        // the second write silently replaces the first and the index links both
        // entries to the survivor. Refused up front, whatever the volume: a site
        // that renders differently per filesystem is not a site.
        var seen = new Dictionary<string, ViewerPage>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in site.Pages)
        {
            if (!seen.TryAdd(page.RelativeHtmlPath, page))
            {
                throw new ArgumentException(
                    $"concepts '{seen[page.RelativeHtmlPath].Id}' and '{page.Id}' would render to the same file on a case-insensitive volume ('{page.RelativeHtmlPath}')",
                    paramName: nameof(site));
            }
        }
```
- [ ] **Step 3: Run** viewer tests; `okf-render` surfaces it as `error: …` through its existing `ArgumentException` mapping (check `OkfRenderCliTests` has a case; add one). CHANGELOG (`### Fixed`). Commit `fix(render): refuse case-variant ids that collide on a case-insensitive output volume`.

### Task D3: Writer efficiency and the reparse resolver's home (quality: guard re-walks per file; `AppendUnicodeEscape` allocates; `ResolveThroughReparsePoints` is a leaf-local copy)

**Files:** `src/OKF4net.Viewer/HtmlWriter.cs`, `src/OKF4net.Viewer/HtmlSafeJson.cs`, `src/OKF4net/Internal/ReparsePoints.cs`; tests `HtmlWriterTests`, `HtmlSafeJsonTests`, `ReparsePointsTests`

- [ ] **Step 1: Move** `ResolveThroughReparsePoints` verbatim to `ReparsePoints.ResolveThroughReparsePoints(string path)` (internal; Viewer has `InternalsVisibleTo`), with its existing tests moved to `ReparsePointsTests` — no behaviour change. Add the ROADMAP/#86 note: `BundleConceptWriter`'s lock keying can now call this.
- [ ] **Step 2: Guard once per directory** — in `Write`, canonicalize `outDir` once (`var root = ReparsePoints.CanonicalizeRoot(outDir);`) and keep `var verifiedDirs = new HashSet<string>(StringComparer.Ordinal);`; `WriteFile` takes `root` and `verifiedDirs`, checks `IsWithin(root, resolved)` for every file (cheap string test), and runs `HasReparsePointAncestor` + `IsReparsePoint(directory)` only the first time a directory is seen; the per-file `IsReparsePoint(resolved)` becomes `new FileInfo(resolved).LinkTarget is not null` (no throw path for a missing file). Existing containment tests must still pass unchanged — they are the spec of this guard.
- [ ] **Step 3: `AppendUnicodeEscape`** — `Span<char> hex = stackalloc char[4]; ((int)c).TryFormat(hex, out _, "x4", CultureInfo.InvariantCulture); sb.Append("\\u").Append(hex);` — pin bytes with the existing `HtmlSafeJson` tests (`<` → `<` etc.).
- [ ] **Step 4: Run** viewer + core tests; commit `refactor(viewer): guard each output directory once, allocation-free escapes, reparse resolver in ReparsePoints`.

---

## Phase E — `producers/OkfProducer` (outside CI: `dotnet test producers/OkfProducer.sln` is the only gate; run it at the end of every task)

### Task E1: A trailing separator on `--out` / `--repo` is not a symlink (finding #2)

**Files:** `producers/src/OkfProducer.Core/Generation/BundlePaths.cs` (`ResolveRoot`), `BundleWriter.cs` (`RepositoryFileExists`, `CreateStagingDirectory`); tests `producers/tests/OkfProducer.Tests/BundlePathsTests.cs`, `BundleWriterTests.cs`

- [ ] **Step 1: Failing tests**

```csharp
    [Fact]
    public void ResolveRoot_drops_a_trailing_separator_so_IsInside_can_see_the_bundle()
    {
        using var tmp = new TempDir();
        var root = BundlePaths.ResolveRoot(tmp.Path + Path.DirectorySeparatorChar)!;
        Assert.False(root.EndsWith(Path.DirectorySeparatorChar));
        Assert.True(BundlePaths.IsInside(root, Path.Combine(root, "overview.md")));
    }

    [Fact]
    public void Generate_into_an_out_path_with_a_trailing_slash_writes_every_concept()
    {
        // Reuse the existing end-to-end helper of BundleWriterTests / GenerateRunTests that writes a
        // tiny bundle; pass `outPath + Path.DirectorySeparatorChar` and assert the same file set as
        // the no-slash run, and zero failures.
    }
```
(Write the second test concretely from the helper the file already has for "writes N concepts"; the assertion is `Assert.Equal(withoutSlash.Written, withSlash.Written)` and `Assert.Empty(withSlash.Failures)`.)

- [ ] **Step 2: Fix** — `ResolveRoot`: `var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));` and the `target.FullName` branch likewise; `RepositoryFileExists`: `var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath));`; `CreateStagingDirectory`: `Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outPath)))` (with the slash, `GetDirectoryName` returned the bundle itself, putting the staging directory inside the bundle). Add a `TrailingSeparator` test for `RepositoryFileExists` through `--update` pruning if the existing prune tests can be parameterized with a trailing slash on `--repo` (they should: the finding is that a deleted file's concept is never pruned).
- [ ] **Step 3: Run** `dotnet test producers/OkfProducer.sln`; CHANGELOG (`### Fixed`): "`okfgen generate --out dir/` (trailing separator, as shell completion writes it) wrote nothing and blamed a symbolic link; `--repo dir/` never pruned a deleted file's concept; the staging directory landed inside the bundle. All three were `Path.GetFullPath` preserving the separator." Commit `fix(producer): trailing directory separators on --out/--repo`.

### Task E2: `git` is resolved on PATH, never from the scanned tree (finding #12)

**Files:** `producers/src/OkfProducer.Core/Generation/GitRevision.cs`; tests `GitRevisionTests.cs`

- [ ] **Step 1: Failing test** — on Windows only (`[SkippableFact]`, skip elsewhere): create a repo dir containing a `git.exe` that is a copy of `cmd.exe`-launched batch? Simpler and portable: make `RunGit` take the resolved executable path through an internal seam `GitRevision.ResolveGitExecutable()` and test **that**: it returns an absolute path under one of the `PATH` directories and never a path under the current directory when `PATH` does not contain it (set `Environment.CurrentDirectory` to a temp dir holding a fake `git.exe`/`git` file during the test; assert the returned path is not under it).
- [ ] **Step 2: Fix** —

```csharp
    /// <summary>
    /// The absolute path of `git` found on PATH, or null. Process.Start with a
    /// bare name lets CreateProcess search the CURRENT DIRECTORY before PATH on
    /// Windows, so a `git.exe` committed in the scanned repository would run
    /// with the operator's privileges whenever okfgen is launched from inside
    /// it -- under --no-msbuild too, whose promise is that nothing from the tree
    /// executes. Resolved here, against PATH only.
    /// </summary>
    internal static string? ResolveGitExecutable()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "git.exe", "git.cmd" } : new[] { "git" };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate)) { return Path.GetFullPath(candidate); }
            }
        }
        return null;
    }
```
`RunGit`: `var git = ResolveGitExecutable(); if (git is null) { return null; } var startInfo = new ProcessStartInfo(git) { … };`.
- [ ] **Step 3: Run** producer tests; CHANGELOG (`### Fixed`, security): "`okfgen` resolves `git` on `PATH` itself; on Windows a bare `Process.Start("git")` searched the current directory first, so a `git.exe` committed in the scanned repository ran when `okfgen` was launched from inside it — `--no-msbuild` included." Commit `fix(producer): resolve git on PATH, never from the scanned tree`.

### Task E3: `RepositoryScanner` does not follow links and does not abort on an unreadable manifest (finding #17 producers)

**Files:** `producers/src/OkfProducer.Core/Scanning/RepositoryScanner.cs`; tests `RepositoryScannerTests.cs`

- [ ] **Step 1: Failing tests** — (a) a repo with a directory junction/symlink `loop → ..` (create with `Directory.CreateSymbolicLink`; `[SkippableFact]` when the platform refuses) → `Scan` completes and returns the repo's own manifests; (b) a dangling symlink named `x.csproj` in a solution-less repo → `Scan` completes and skips it; (c) a `.csproj` that is a directory → skipped.
- [ ] **Step 2: Fix** — in `EnumerateFilesRecursively`, skip a subdirectory when `BundlePaths.IsReparsePoint(subDirectory)` (that helper lives in Core: reuse it); in `ScanNuGetManifest`, `ScanNpmManifest` and `ParseSolutionProjectPaths`, widen the catch to `catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException or JsonException)` returning `null`/`[]` (the type's contract: malformed manifests are skipped, not fatal). Fix the doc comment in `GenerateRun` (line ~143) that claims the abort happens only without a root `.sln`.
- [ ] **Step 3: Run**, CHANGELOG (`### Fixed`), commit `fix(producer): scanner skips links and unreadable manifests instead of aborting`.

### Task E4: Effective visibility walks the type chain, not the namespace (finding: `FileEligibility` conflation)

**Files:** `producers/src/OkfProducer.Core/CodeGraph/FileEligibility.cs` (`IsInScope`); tests `FileEligibilityTests.cs`

- [ ] **Step 1: Failing test** — declared: `("A", "B")` = internal Type in `a.cs`; fact: public type `C` with `Container = "A.B"` in `c.cs` → `IsInScope(fact, declared, default)` is **true**. Second case: same declared type in `a.cs`, fact public member `M` with `Container = "A.B"` in `a.cs` → **false** (a real nested member is still capped).
- [ ] **Step 2: Fix** — `SymbolFact` has no namespace property, and `declared` is keyed by `(Container, Name)`, so a type `A.B` and a namespace `A.B` cannot both be present — the lookup alone cannot tell them apart. Use the one signal that distinguishes a genuinely enclosing type: a nested declaration lives in the **same file** as its enclosing type (C# has no cross-file nesting except `partial`, which fails open here — the symbol is kept, never wrongly dropped). Cap only when `enclosing.Kind == SymbolKind.Type && string.Equals(enclosing.RelativePath, fact.RelativePath, StringComparison.Ordinal)`; otherwise `break`. Comment: CA1724-style namespace/type name collisions (a `Logging` class beside an `X.Logging` namespace) are common; the previous walk capped every public symbol of the namespace to the type's visibility.
- [ ] **Step 3: Run**, CHANGELOG (`### Fixed`), commit `fix(producer): a namespace segment sharing a type's name no longer caps visibility`.

### Task E5: Enumeration tolerates one inaccessible directory (finding: `CodeGraphBuilder` all-or-nothing)

**Files:** `producers/src/OkfProducer.Core/CodeGraph/CodeGraphBuilder.cs` (`EnumerateFiles`); tests `CodeGraphBuilderTests.cs`

- [ ] **Step 1: Failing test** (Unix-only `[SkippableFact]`; on Windows use an ACL deny via `icacls` if the test host allows, else skip): a repo with `src/ok.cs` and `locked/` (mode 000) → the build visits `src/ok.cs` and reports `TraversalComplete == false` with a `Skipped` entry naming `locked`.
- [ ] **Step 2: Fix** — `Directory.EnumerateFiles(repoPath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })`; record inaccessible directories: after enumeration, walk `Directory.EnumerateDirectories(repoPath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })` and for each try `Directory.EnumerateFileSystemEntries(dir).Any()` in a try/catch `UnauthorizedAccessException` → add a `Skipped` entry with reason `Inaccessible` and set `incomplete = true`. (Add the `Inaccessible` reason to the skip-reason enum if absent; the `run:` report already prints per-cause counts.)
- [ ] **Step 3: Run**, CHANGELOG, commit `fix(producer): one unreadable directory no longer empties the whole file list`.

### Task E6: Implicit framework defines reach the compilation (finding: `DefineConstants` missing when `GenerateAssemblyInfo=false`)

**Files:** `producers/src/OkfProducer.CodeGraph.Roslyn/MsBuildProjectQuery.cs` (`Targets`); tests `MsBuildProjectQueryTests.cs` / `CompilationFactoryTests.cs`

- [ ] **Step 1: Measure first** (this is the empirical step the plan depends on): create a temp project with `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>` and run

```powershell
dotnet msbuild t.csproj -t:ResolveReferences -t:GenerateGlobalUsings -t:GenerateAssemblyInfo -getProperty:DefineConstants
dotnet msbuild t.csproj -t:ResolveReferences -t:GenerateGlobalUsings -t:AddImplicitDefineConstants -getProperty:DefineConstants
```
Expected: the first prints `TRACE;DEBUG` (or similar) without `NET10_0`; the second includes `NET;NETCOREAPP;NET10_0;NET5_0_OR_GREATER…`. If `AddImplicitDefineConstants` is not a public target on this SDK (`error MSB4057`), fall back to `-t:PrepareForBuild` and re-measure; if that also fails, compute the implicit set from `TargetFramework` in a small `ImplicitDefines.For(string tfm)` helper (NET, NETCOREAPP, `NET{major}_{minor}`, `NET{n}_0_OR_GREATER` for 5..major, `NETCOREAPP3_1_OR_GREATER`…) and union it into `DefineConstants` — write tests for `net10.0`, `net8.0`, `netstandard2.0`.
- [ ] **Step 2: Failing test** — a fixture project with `GenerateAssemblyInfo=false` and `#if NET10_0_OR_GREATER class A {} #else #error nope #endif` compiles without `CompilationHadErrors`.
- [ ] **Step 3: Apply** whichever of the three the measurement chose; document the choice in the `Targets` comment. Run producer tests; CHANGELOG (`### Fixed`); commit `fix(producer): implicit framework defines are part of the Roslyn compilation`.

### Task E7: Strong-name inputs reach `CSharpCompilation` (finding: signed `InternalsVisibleTo` refused)

**Files:** `MsBuildProjectQuery.cs` (`Properties`: add `-getProperty:SignAssembly`, `-getProperty:AssemblyOriginatorKeyFile`, `-getProperty:PublicSign`), `ProjectInputs` record, `CompilationFactory.cs`; tests `CompilationFactoryTests.cs`

- [ ] **Step 1: Failing test** — two fixture projects: `Lib` signed with a test `.snk` (generate with `sn -k` once and commit under `fixtures/`; or create the key at test time via `System.Reflection.StrongNameKeyPair`-free path: `dotnet` can `PublicSign` with a `.snk` containing just the public key — use `PublicSign=true` + a checked-in public-key `.snk`) declaring `[assembly: InternalsVisibleTo("App, PublicKey=…")]`, and `App` using an `internal` member of `Lib`. The compilation of `App` reports no errors.
- [ ] **Step 2: Fix** — in `CompilationFactory`, when `inputs.SignAssembly` and `inputs.KeyFile` is set: `.WithCryptoKeyFile(keyFile).WithStrongNameProvider(new DesktopStrongNameProvider())` and `.WithPublicSign(inputs.PublicSign)`; resolve the key file relative to the project directory.
- [ ] **Step 3: Run**, CHANGELOG (`### Fixed`), commit `fix(producer): honour SignAssembly/KeyFile/PublicSign so signed friend assemblies compile`.

### Task E8: Tree-sitter caller attribution and container paths (finding: field-initializer lambdas, accessor/argument `name` fields)

**Files:** `producers/src/OkfProducer.CodeGraph.TreeSitter/TreeSitterExtractor.cs` (call-site caller name ~line 845, `ComputeContainerPath` ~line 970); tests `TreeSitterExtractorTests.cs`

- [ ] **Step 1: Failing tests** — (a) `public sealed class T { public int result; private readonly Lazy<int> _lazy = new(() => { var result = Compute(); return result; }); static int Compute() => 1; }` → the `Compute` call site's `CallerName == "_lazy"`. (b) `public class T { public int P { get { int Helper() => 2; return Helper(); } } }` → the local function `Helper`'s `Container == "N.T.P"` (no `get` segment), matching `RoslynResolver.ContainerPathFromSyntax` for the same source (assert equality between the two engines' spellings if a Roslyn test helper exists; else assert the literal).
- [ ] **Step 2: Fix** — (a) when `callerMember` is a `field_declaration`/`event_field_declaration`, take the `variable_declarator` that is a **direct child of the member's `variable_declaration`** (walk `callerMember.Children` for `variable_declaration` → its `variable_declarator` children, choose the one whose byte range contains the callee), not the nearest ancestor. (b) In `ComputeContainerPath`, skip a `name` field on `accessor_declaration` and `argument` node types: `var skip = current.Type is "accessor_declaration" or "argument";`. Confirm the grammar's node type strings against `TreeSitterExtractor`'s existing constants.
- [ ] **Step 3: Run**, regenerate the golden if it moves (`OKFGEN_UPDATE_GOLDEN=1 …` per `producers/tests/OkfProducer.Tests/fixtures/README.md`) and review the diff line by line — only the two shapes above may change. CHANGELOG (`### Fixed`). Commit `fix(producer): attribute field-initializer calls to the field; no accessor/argument segments in container paths`.

### Task E9: Reserved device names in concept ids (finding, low, Windows Server kernels)

**Files:** `producers/src/OkfProducer.Core/Generation/CodeConceptIds.cs` (`Compose`); tests `CodeConceptIdsTests.cs`

- [ ] **Step 1: Failing test** — `namespace Aux { class Con {} }` → ids `code/csharp/aux_/con_` (a trailing underscore on each reserved segment).
- [ ] **Step 2: Fix** — in `Compose`, after a segment is lower-cased: `if (ReservedDeviceNames.Contains(segment)) segment += "_";` with `private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase) { "con", "prn", "aux", "nul", "com1", …, "com9", "lpt1", …, "lpt9" };` and a comment: on Windows 10 / Server these names are devices, so `aux/con.md` is unwritable there and written on Linux — one commit, two bundles. Regenerate the golden only if the fixture repo declares such a name (it does not).
- [ ] **Step 3: Run**, CHANGELOG, commit `fix(producer): suffix Win32 reserved device names in concept ids`.

### Task E10: `O(k)` package-child filter and wave-parallel MSBuild queries (efficiency)

**Files:** `producers/src/OkfProducer.Core/Generation/ConceptGenerator.cs` (~line 990, `IsProperAncestor`), `producers/src/OkfProducer.CodeGraph.Roslyn/RoslynResolver.cs` (`QueryProjectClosure` ~line 440); tests `DeterminismTests`, `CheckTests` (the golden must not move)

- [ ] **Step 1:** Replace the `keys.Where(key => !keys.Any(other => IsProperAncestor(other, key)) …)` with a single sorted pass: `keys` is a `SortedSet<string>` of NUL-joined paths, so iterate in order keeping `lastKept`; skip `key` when `lastKept is not null && key.StartsWith(lastKept + '\0', Ordinal)`; build `candidate + '\0'` once per kept key. Pin with a unit test on `[A, A\0B, A\0B\0C, D]` → `[A, D]`.
- [ ] **Step 2:** In `QueryProjectClosure`, query each frontier wave concurrently: `var wave = pending.Skip(i).ToList(); var results = await Task.WhenAll(wave.Select(p => Task.Run(() => Query(p))))` bounded by `Environment.ProcessorCount` (use `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`), then merge discovered references in `Ordinal` order so the closure stays deterministic; keep the `deadline.ShouldAbandon()` check between waves. The golden (`CheckTests`) and `DeterminismTests` must pass unchanged.
- [ ] **Step 3: Run** producer tests; commit `perf(producer): O(k) package-child filter, MSBuild queries per dependency wave`.

### Task E11: One bounded process runner and one link test in `producers/` (reuse)

**Files:** create `producers/src/OkfProducer.Core/Generation/BoundedProcess.cs`; modify `GitRevision.cs` (`RunGit`), `MsBuildProjectQuery.cs` (`Run`), `CompilationFactory.cs` (`IsBehindReparsePoint`), `TreeSitterExtractor.cs` (`IsUnderReparsePoint`), `BundlePaths.cs` (`HasLinkAncestor`); tests `BoundedProcessTests.cs`

- [ ] **Step 1:** `internal static class BoundedProcess { internal static BoundedResult? Run(string executable, IReadOnlyList<string> args, string workingDirectory, TimeSpan timeout) }` returning `(int ExitCode, string Stdout, string Stderr)` or `null` when the process could not start / timed out (killed, entire tree). It owns: `ArgumentList`, UTF-8 pipes, concurrent drain, the second `WaitAsync(timeout)` bound `GitRevision` has and `MsBuildProjectQuery` lacks (that asymmetry is the bug this closes), `TryKill`. Tests: a process that prints and exits; one that sleeps past the timeout is killed and returns null; stdout > 1 MiB is fully drained.
- [ ] **Step 2:** `RunGit` and `MsBuildProjectQuery.Run` call it; delete both private `TryKill`s. `BundlePaths.HasLinkAncestor(string path, string stopAt)` (walk up to `stopAt` testing `BundlePaths.IsReparsePoint`, fail-closed on `IOException`/`UnauthorizedAccessException` like `IsReparsePoint` does) replaces the two private walks in `CompilationFactory` and `TreeSitterExtractor`.
- [ ] **Step 3: Run**, commit `refactor(producer): one bounded process runner, one link-ancestor walk`.

### Task E12: SPDX headers on the fixture repository's `.cs` files (convention)

**Files:** `producers/tests/OkfProducer.Tests/fixtures/fixture-repo/src/{Registry,Scanner,Shapes,Sub/Formatter}.cs`, `producers/tests/OkfProducer.Tests/fixtures/golden/**`

- [ ] **Step 1:** Prepend `// SPDX-License-Identifier: LGPL-3.0-or-later` to the four files. **Step 2:** Regenerate the golden: `$env:OKFGEN_UPDATE_GOLDEN=1; dotnet test producers/OkfProducer.sln --filter "FullyQualifiedName~CheckTests.Check_passes_on_an_unchanged_bundle"`; review the diff — only `#L` line numbers in `resource:` permalinks may change (+1 on every declaration in those files). **Step 3:** `dotnet test producers/OkfProducer.sln` green; commit `chore(producer): SPDX headers on fixture sources, golden line numbers regenerated`.

---

## Addendum 2026-09-14 — resumption after the stop at E2

Execution was stopped after E2 because other worktrees had changed the same code. On resumption the user added, to this same wave: the three out-of-plan findings raised during execution (Phase H) and the external audit #2 of `bc3bf47` (Phase G). **Execution order: R1 → G1…G6 → H1…H3 → E3…E12 → F1.**

User arbitrations (2026-09-14), binding on the tasks below:
- PR #104 is merged into `dev` before R1 (done: `05fee2e`).
- G4, duplicate keys: **reject everywhere** — receipt, attester verdict, the tool's parameter values, at every nesting level.
- G4, numbers: **reject non-exact** — an integer literal must fit `long`; a non-integer must be a finite `double` that reads back as the same value.
- G1: **return promptly** — a stage that ignores its token is abandoned (`WaitAsync`), and its background work stays bounded by the engine's own timeout; an outcome is never displayable after a cancellation or a timeout.
- Phase H: decided **task by task with the user** — the controller stops before dispatching each H task.

Already fixed before this branch (verified by code reading, no task): audit #2's output-ceiling truncation (#95, `exceeded the output ceiling`), timeout validated before `Process.Start` (#95), top-level `null` receipt (#95).

## Phase R — Reconcile with dev

### Task R1: Merge `origin/dev` (`05fee2e`) into the branch

**Why a merge, not a rebase:** the ledger cites the SHAs of the 46 reviewed commits; a rebase would invalidate every one of them.

**Files (the 9 textual conflicts `git merge-tree` reports):**
- `src/OKF4net.Attestation.Containers/ContainerRuntimeProfile.cs`
- `src/OKF4net.Attestation.Containers/ContainerAttester.cs`
- `src/OKF4net.Attestation.Containers/SqlClientComputationExecutor.cs`
- `src/OKF4net.Attestation.Containers/ContainerExecutionException.cs`
- `src/OKF4net.Attestation.Containers/README.md`
- `CHANGELOG.md`
- `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs`
- `tests/OKF4net.Tests/Attestation.Containers/CliContainerEngineArgumentsTests.cs`
- `tests/OKF4net.Tests/Attestation.Containers/SqlClientComputationExecutorTests.cs`

Also (auto-merged, but will not compile): every `dev` test that sets `TmpfsMounts`, `ReadOnlyRootFilesystem`, `MemoryBytes`, `Cpus`, `PidsLimit` or `Timeout` directly on `ContainerRuntimeProfile` / `ContainerAttesterOptions` — `ContainerRuntimeProfileTests.cs` (~23), `ContainerAttesterTests.cs` (~4), `ContainerIntegrationTests.cs` (~3), `ScriptComputationExecutorTests.cs` (~2), `SqlClientComputationExecutorTests.cs` (1), `CliContainerEngineArgumentsTests.cs` (1).

**Resolution rules (binding):**

1. **`ContainerIsolation` stays the one home** of user / capabilities / no-new-privileges / read-only root / tmpfs mounts / the four ceilings (B2's reviewed design: one record so a hardening decision cannot reach two of the three container consumers and miss the third). `dev`'s per-property versions on the profile and the attester options are **not** reintroduced. What `dev` added moves into that record:
   - `ContainerIsolation.TmpfsMounts` gets a validating init: `init => _tmpfsMounts = ScratchDirectory.ValidateMounts(value, nameof(TmpfsMounts))`, backing field defaulting to `["/tmp"]`. Keep `dev`'s doc text (first entry = the run's scratch = `TMPDIR`; absolute path with optional `:options`; empty list allowed on a profile) adapted to the record.
   - `ContainerIsolation.ToRunSpec` applies `ScratchDirectory.Apply(environment, TmpfsMounts)` to the environment it puts in the spec. Every consumer therefore gets `TMPDIR` without repeating it — do not also call `Apply` at the call sites.
   - `ContainerAttester` keeps `dev`'s constructor guards (both of them: read-only root with no mount; `ScratchDirectory.ReachesWritableDirectory` false on the applied environment) and `dev`'s environment snapshot, reading `options.Isolation.ReadOnlyRootFilesystem` / `options.Isolation.TmpfsMounts`. The class becomes a regular constructor + `_engine`/`_options` fields, as on `dev`. Keep `dev`'s doc comments, `Isolation`-qualified.
   - `ContainerAttester.AttestAsync` builds its spec with `_options.Isolation.ToRunSpec(_options.Image, ["python3", "-c", Bootstrap], envelope, _options.Environment, "none")` and parses with `ReceiptParsing.ParseJson(result, "attester")` (B6) — not `dev`'s inline exit-code / `JsonSerializer` block. Keep `dev`'s Bootstrap text (it follows `TMPDIR`) and its visibility (`internal`, a `dev` test reads it).
   - `SqlClientComputationExecutor` keeps HEAD's `profile.Isolation.ToRunSpec(...)` line, and `dev`'s Wrapper text **merged with** B3's `unquote(...)` / `(rows or [])` changes — both sides changed the Python; keep every change of both.
2. **`ContainerExecutionException`**: HEAD's base class (`AttestationDiagnosticException`) **plus** `dev`'s `ToString()` override with the stream tails. Merge the class doc into one statement that is true of both: the `Message` is library-authored and may be rendered into `Reasons`; `Stdout`/`Stderr` — and therefore `ToString()` — are host-side only and never reach the model. Before writing that sentence, grep every `new ContainerExecutionException(` on the merged tree and confirm no message interpolates container output (the only interpolated text allowed is `JsonException.Message`, already covered by B4's newline neutralisation).
3. **Tests**: keep **both** sides of every conflicting test file (HEAD's B2/B3/B4 tests and `dev`'s TMPDIR / stderr-not-rendered tests). Rewrite `dev`'s property usages onto `Isolation`, preserving what each test proves. **Attester idiom:** `new ContainerAttesterOptions { Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = [...] } }` — a bare `Isolation = new() { ... }` silently resets the attester's smaller ceilings to the profile's defaults, which changes what the test exercises. `dev`'s `A_container_failure_does_not_render_the_containers_output_to_the_model` must keep passing alongside HEAD's `A_diagnostic_exceptions_message_is_rendered_in_the_error_line`.
4. **README**: keep `dev`'s read-only / `TMPDIR` / attester-guard paragraphs and HEAD's `NetworkMode`-stays-on-the-profile paragraph; every property name is written as `Isolation.<Name>`.
5. **CHANGELOG**: union of both `[Unreleased]` blocks, no entry dropped. `dev`'s entries naming `ContainerRuntimeProfile.TmpfsMounts` / `ContainerAttesterOptions.TmpfsMounts` (and any other moved property) are reworded to `ContainerIsolation.<Name>` (reached through `Isolation`), and B2's Breaking entry lists every property that moved, including the ones `dev` documented after `cee0c30`.
6. **Auto-merged `src/OKF4net/Validate.cs`**: not a textual conflict, but `dev` (§8 index entries, §5.1 citation ids, heuristics) and C9 (`RecommendedFieldsFor`, concept-relative hint) both edited it — read the merged file once end to end and confirm neither side's diagnostics were lost.

- [ ] **Step 1:** `git fetch origin && git merge --no-ff origin/dev` (expect the 9 conflicts above). Resolve per the rules. `dotnet build OKF4net.sln` → 0 warnings.
- [ ] **Step 2:** `dotnet format OKF4net.sln --verify-no-changes`.
- [ ] **Step 3:** `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"` → 0 failed; record the count.
- [ ] **Step 4 (the risk the merge cannot show):** `dev`'s Meridian / TMPDIR integration tests have never run under B2's hardened defaults (uid `65534`, `--cap-drop ALL`, `no-new-privileges`). Start and seed the fixture with **all three** seeds (the class doc: `users`, `bundles/meridian_transit/references/schema.sql`, `typed`), set `OKF_DEMO_PG_CONN`, run `dotnet test tests/OKF4net.Tests --filter "Category=ContainerIntegration"` → **0 failed, 0 skipped**. A failure caused by the hardened defaults is reported, not papered over by relaxing a default — that is a user decision.
- [ ] **Step 5:** `dotnet test producers/OkfProducer.sln` (the link scanner was rewritten on `dev`; the producer is outside CI) and `cd tools/viewer-security-check && npm test`.
- [ ] **Step 6:** Commit the merge: `merge: bring dev (05fee2e) into the post-audit branch` with a body listing each conflict and its resolution rule.

## Phase G — External audit #2 (target `bc3bf47`)

### Task G1: Cancellation and `ComputationTimeout` never yield a displayable outcome, and return promptly

**Finding (High):** `AttestationOrchestrator.RunStageAsync` checks the token only before a stage. A stage that ignores its token runs to completion and its result is used: with a 30 ms `ComputationTimeout` and an attester that sleeps 350 ms ignoring the token, `okf_run_computation` returned after 352 ms with `displayable: yes`; an attester that cancels the caller's token and returns `Passed` gave `IsCancellationRequested = true` and `Displayable = true`. Contradicts the CHANGELOG's `ComputationTimeout` entry ("wall-clock ceiling").

**Files:** `src/OKF4net.Attestation/AttestationOrchestrator.cs` (`RunStageAsync`); `src/OKF4net.Agents/OkfBundleTools.cs` (`RunComputationAsync` only if the tests show it needs a change); tests `tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs`, `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs`; `CHANGELOG.md`.

**Required behaviour:**
- `RunStageAsync` awaits the stage through `.AsTask().WaitAsync(cancellationToken)`, so a token-ignoring stage is abandoned the moment the token fires, and calls `cancellationToken.ThrowIfCancellationRequested()` again **after** the await, so a stage that completes (or cancels the token itself) after cancellation never contributes a result.
- An abandoned stage task is observed (a continuation that reads `task.Exception` on `OnlyOnFaulted`) so its later fault cannot surface as an unobserved task exception. Its work is not otherwise stopped — the container engine's own `Timeout` bounds it; say so in the doc comment.
- The resulting `OperationCanceledException` keeps today's routing: the caller's own cancellation propagates to the caller; the tool's `ComputationTimeout` becomes the existing `displayable: no … timed out after` text.

**Tests (each proven RED on the current code first):**
1. Orchestrator: attester `await Task.Delay(350, CancellationToken.None)` then `Passed`; token cancelled after 30 ms → `RunAsync` throws `OperationCanceledException` in < 200 ms (generous bound; the RED case takes ≥ 350 ms).
2. Orchestrator: attester that calls `cts.Cancel()` on the caller's source and returns `Passed` → throws `OperationCanceledException`, never returns an outcome.
3. Same as 2 for the binder and the executor (a theory over the three stages).
4. Tool: `ComputationTimeout = 30 ms`, token-ignoring attester sleeping 350 ms → rendered text starts with `displayable: no` and contains `timed out`, returned in < 200 ms.
5. An abandoned stage that later throws does not raise `TaskScheduler.UnobservedTaskException` (force `GC.Collect(); GC.WaitForPendingFinalizers()` after the stage's delay).

**CHANGELOG:** Fixed — a stage that ignores cancellation no longer keeps a run alive past `ComputationTimeout` or the caller's token, and can no longer turn a cancelled run into a displayable outcome (§10.5).

### Task G2: Staleness is gated at the instant the outcome is released

**Finding (Medium):** `RunAsync` reads `_clock.Now` once, before bind (`AttestationOrchestrator.cs` ~138), and reuses it after every stage. A run started one second before `stale_after` whose stages take two seconds is released as `Fresh` and displayable, although §5.5 says content is stale when `now >= stale_after`.

**Files:** `src/OKF4net.Attestation/AttestationOrchestrator.cs`; tests `AttestationOrchestratorTests.cs`; `CHANGELOG.md`.

**Required behaviour:** `stale` and `staleAdmitted` are computed from `_clock.Now` read **when the outcome is built** — immediately before step 9 for the success path, and at each `Fail(..., stale, ...)` after a stage for the failure paths (a helper taking the lifecycle and returning both values keeps it to one line per site). No early refusal is added (out of scope: a run still executes a concept that is already stale).

**Tests:** `FixedClock` is immutable, so add a small test-only mutable `IOkfClock` in the test file; the attester advances it from `stale_after - 1 s` to `stale_after + 1 s` → `Stale == StaleState.Stale`, `Displayable == false` under `StalePolicy.Strict` (`Use` admits everything); the same with the clock left before `stale_after` → `Fresh`, displayable. A failure after the clock advanced reports `Stale`. RED first on the current code.

**CHANGELOG:** Fixed — staleness (§5.5) is evaluated when the result is released, not when the run started.

### Task G3: Container stdout that is not valid UTF-8 fails the stage

**Finding (Medium):** `CliContainerEngine` decodes stdout with a replacement-fallback `UTF8Encoding`: a container writing `b'{"x":"\xff"}'` produces a receipt `{"x":"\uFFFD"}` — the engine silently rewrote the data the attester authenticates.

**Files:** `src/OKF4net.Attestation.Containers/CliContainerEngine.cs` (stdout decoding, `ReadBoundedAsync`); tests `CliContainerEngineRunTests.cs` and a real-Docker case in `ContainerIntegrationTests.cs`; `CHANGELOG.md`.

**Required behaviour:**
- stdout is decoded strictly (`new UTF8Encoding(false, throwOnInvalidBytes: true)`). An invalid sequence makes the run a `ContainerExecutionException("container stdout was not valid UTF-8", …)`.
- **The pipe keeps being drained after an invalid sequence** (discarding), exactly as the output ceiling does after truncation — a reader that stops reading blocks the child on a full pipe and turns the error into a timeout. The invalid-UTF-8 check is reported after the process exits, like the ceiling check; if both happen, either message is acceptable but the stage fails.
- stderr keeps the lenient decoder: it is host-side diagnostics, never authenticated, and a strict decoder there would hide the very traceback a host needs. Say so in a comment.
- `ReadBoundedAsync`'s contract (the `maxChars` ceiling, `Truncated`) is unchanged for valid input.

**Tests:** unit test on `ReadBoundedAsync` (or its replacement) over a `MemoryStream` containing a valid prefix, `0xFF`, then more than 64 KiB of valid bytes → reports invalid **and** consumed the whole stream; valid multi-byte UTF-8 split across the 8192-char buffer boundary still decodes exactly (guards against a decoder that throws on a split sequence). Integration: `python3 -c "import sys; sys.stdout.buffer.write(b'{\"x\":\"\\xff\"}')"` → stage fails with the UTF-8 message.

**CHANGELOG:** Fixed — invalid UTF-8 on a container's stdout fails the stage instead of being replaced with U+FFFD in the receipt.

### Task G4: Container JSON is parsed strictly — one object, no duplicate keys, only exact numbers

**Finding (Medium):** duplicate top-level receipt keys are last-wins (a documented choice this task reverses by user arbitration); a nested duplicate escapes as a raw `ArgumentException` from `ToDictionary`; `1e400` becomes `+∞`; `9223372036854775809` is silently rounded to a `double`.

**Files:**
- `src/OKF4net.Attestation.Containers/Internal/ReceiptParsing.cs`, `Internal/JsonValues.cs` (receipt and attester verdict — `ContainerAttester` parses through `ReceiptParsing.ParseJson` after R1);
- `src/OKF4net.Agents/Internal/ParameterValues.cs` and its call site in `OkfBundleTools.RunComputationAsync`;
- tests `tests/OKF4net.Tests/Attestation.Containers/ReceiptParsingTests.cs`, `JsonValuesTests.cs`, `ContainerAttesterTests.cs`, `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs`;
- `CHANGELOG.md`.

**Required behaviour:**
- **Duplicates:** parse with `new JsonDocumentOptions { AllowDuplicateProperties = false }` (.NET 10). A duplicate at any depth → `ContainerExecutionException("<stage> stdout had a duplicate JSON property", …)` — the `JsonException` is caught in `ParseJson` like any other parse error; do not interpolate its message if it quotes the property name (bundle-authored text): use the fixed wording. Remove the "last-wins" comment and replace it with the reason for rejecting (two readers of the same receipt can retain different values).
- **Numbers**, one shared rule in `JsonValues` for receipt and verdict: a literal with no `.`/`e`/`E` is an integer and must satisfy `TryGetInt64` → `long`, else reject; any other literal must parse to a finite `double` whose shortest round-trip form (`d.ToString("R", CultureInfo.InvariantCulture)`) denotes the same decimal value as the literal (compare as `decimal` when both fit; otherwise compare normalised mantissa digits + exponent) → `double`, else reject. Rejection → `ContainerExecutionException("<stage> stdout had a number that cannot be represented exactly", …)`. `JsonValues` never throws anything but that exception; its nested objects are built with indexer assignment (duplicates are already rejected by the parser).
- **Tool parameters:** `ParameterValues` applies the same number rule and rejects nested duplicates. The JSON of `parameterValues` itself is deserialized by Microsoft.Extensions.AI before this code sees it, so a duplicate **top-level** parameter name is outside its reach — say so in its doc comment rather than claiming it. A rejection returns the tool's `Error: …` text; it must not throw out of `RunComputationAsync` (today `Normalize` runs outside the try/catch — move it inside or catch its exception).
- The number rule lives once. `OKF4net.Agents` does not reference `OKF4net.Attestation.Containers`, so put the shared normaliser in `OKF4net.Attestation` as an `internal` type with `InternalsVisibleTo` for `OKF4net.Attestation.Containers` and `OKF4net.Agents`, and make both callers use it. The exception type each caller raises stays its own.

**Tests (table-driven, RED first where the current code accepts):** `{"a":1,"a":2}` rejected; `{"a":{"b":1,"b":2}}` rejected (not `ArgumentException`); `[{"a":1,"a":1}]` nested in an array rejected; `1e400`, `-1e400`, `1e-400` rejected; `9223372036854775808` rejected, `9223372036854775807` and `-9223372036854775808` → `long`; `0.1`, `1234.56`, `1.5e3`, `1e308`, `5e-324` → `double`; `0.10000000000000000001` rejected; the verdict path rejects a duplicate `passed`; the tool path returns `Error:` for `{"n": 1e400}` and for a nested duplicate, and still accepts `{"n": 42}` as `long`.

**CHANGELOG:** Breaking (unreleased container runtime) — receipts and verdicts with duplicate JSON properties, or numbers that cannot be represented exactly, now fail the stage instead of being silently resolved (last-wins, rounding, infinity).

### Task G5: One short deadline bounds teardown after a timeout

**Finding (Medium, partly fixed by #95):** teardown after a timeout runs `engine kill` bounded by `KillTimeout = 5 s` per attempt, with one retry; a slow but responsive `kill` (750 ms per call) stretched a 30 ms timeout to ~1.74 s, and the worst case today is ~5 s + delay + a second attempt.

**Files:** `src/OKF4net.Attestation.Containers/CliContainerEngine.cs` (`KillContainerAsync`); tests `CliContainerEngineRunTests.cs`; `CHANGELOG.md`.

**Required behaviour:** replace the per-attempt bound with **one teardown budget of 3 s** for the whole of `KillContainerAsync` (both attempts and the delay between them); the retry runs only if at least 500 ms of the budget remain; the local `Process.Kill(entireProcessTree: true)` still follows. Document the number and why (a healthy engine answers `kill` well inside it; a daemon that has gone away must not turn the promised timeout into a hang).

**Tests:** extend the existing fake-engine test at `CliContainerEngineRunTests.cs:73`: a fake `kill` that takes 750 ms and fails → total elapsed after a 30 ms timeout < 3.5 s and the retry happened; a fake `kill` that hangs → total < 3.5 s and no retry. RED first against the current 5 s per-attempt bound (the hanging case exceeds 5 s).

**CHANGELOG:** Fixed — teardown after a container timeout is bounded by a single 3-second budget instead of 5 seconds per kill attempt.

### Task G6: Regression test for audit #2's JSON-parameter reproduction

**Finding (High, fixed by B1):** `okf_run_computation` invoked with `{"parameterValues":{"n":42}}` against `ContainerAttestationRuntime` with an `integer` parameter threw `ArgumentException` in the binder, executor never called.

**Files:** `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs` only.

**Required behaviour:** one test that reproduces the audit's exact path — the tool invoked **through its `AIFunction`** with JSON arguments (as `AIFunctionExposureTests` does), a `ContainerAttestationRuntime` built on a `FakeContainerEngine`, a concept declaring `n` as `integer` — and asserts the fake engine received exactly one executor run whose parameter envelope carries `42` as an integer. Prove it RED by temporarily removing the `ParameterValues.Normalize` call (revert the removal; do not commit it).

**CHANGELOG:** none (test only).

## Phase H — Out-of-plan findings (each task decided with the user before dispatch)

### Task H1: Link guards fail closed when a reparse point cannot be inspected (security)

**Finding (pre-existing, executed by a D3 reviewer):** `ReparsePoints.IsReparsePoint` (`src/OKF4net/Internal/ReparsePoints.cs` ~58) returns `false` on `IOException` / `UnauthorizedAccessException`. A junction carrying a deny ACE was treated as "not a link", and `okf-render` wrote outside `--out`. The same predicate backs `BundleConceptWriter`'s write guard and `OKF4net.Catalog`.

**Executed escape (D3 reviewer, at base `85e8dde`):** a junction `out/x/y → ext` carrying a deny-read ACE on the junction itself (plus deny list-directory on `out/x`) made `okf-render` write `x/y/z/two.html` into `ext/z/two.html`.

**USER DECISION (2026-09-14): a strict variant for guard callers only.** Loading and walks keep today's predicate, so nothing `okf validate` reports on a bundle changes.

**Files:** `src/OKF4net/Internal/ReparsePoints.cs`; every guard call site listed below; tests `tests/OKF4net.Tests/` (the existing test files for each guard, plus a `ReparsePoints` unit test file); `CHANGELOG.md`.

**Required behaviour:**
- Add `internal static bool IsReparsePointOrUninspectable(string path)`, or a better name stating the same thing:
  - `FileNotFoundException` or `DirectoryNotFoundException` (the entry does not exist, e.g. a file about to be created) → `false`, exactly as today;
  - `UnauthorizedAccessException` or any other `IOException` → `true` (treat it as a link: the guard refuses);
  - otherwise the same answer as `IsReparsePoint`.
  - Check what `File.GetAttributes` and `LinkTarget` actually throw for a missing leaf and for a missing parent, on Windows and Unix. A missing entry must never be refused.
- Add a strict ancestor walk with the same shape (`HasReparsePointAncestor` over the strict predicate), or a strict flag on the existing one.
- **Classify every call site explicitly**, in a table in the report:
  - **Guards, switch to strict:** each site that refuses or allows a write or an outward path. At least `BundleConceptWriter.cs` ~1129 and ~1459; `IndexGenerator.cs` ~280 (the index file it writes); `OkfBundleTools.cs` ~875 and ~931 (`log.md`); `FileMemoryStore.cs` ~241; `HtmlWriter.cs` ~269; `HtmlWriter.GuardOutputDirectory` / `ResolveThroughReparsePoints` ~163. Also every `HasReparsePointAncestor` call made on behalf of one of these guards, including `IndexGenerator`'s private wrapper if it guards a write.
  - **Walks, keep lenient:** `Bundle.cs` ~407 and ~457, `IndexGenerator.cs` ~219 and ~437 (directory enumeration), `CatalogPathResolver`. Say for each why skipping it is not a guard.
  - `producers/` references the predicate only in comments; confirm.
- The two predicates' doc comments state the polarity rule: a guard fails closed and a walk fails open, and why.

**Tests:**
1. Unit tests on the strict predicate: missing leaf → `false`; missing parent → `false`; a plain file or directory → `false`; a symlink or junction → `true`; an entry whose attributes cannot be read → `true`.
   - On Windows, build the last case with a junction plus a deny ACE for the current user. Use `icacls <junction> /deny "%USERNAME%:(RA)"` (or `System.Security.AccessControl`, which is BCL on Windows), and restore the ACE in `finally` so the temp directory can be deleted.
   - Skip on non-Windows with the repo's existing skip idiom for Windows-only tests.
2. **The executed escape, end to end, RED first:** reproduce the D3 scenario through `HtmlWriter` (or `okf-render` in-process). Assert that nothing is written under `ext` and that the render fails with the guard's existing refusal error. Prove it RED on the current code (it writes into `ext`).
3. One end-to-end refusal test per other guard family, using the same deny-ACE junction under the bundle or the store root: `BundleConceptWriter` write, `okf_append_log`, `FileMemoryStore` write, the index write. Each is RED on the current code where the escape is reachable. If one is unreachable (the OS refuses the write anyway), say so with the evidence instead of writing a vacuous test.
4. A regression test that a guard still allows creating a new file in a new subdirectory (the missing-entry case), for at least `BundleConceptWriter` and `HtmlWriter`.

**Review:** adversarial and executed (mutants: a strict variant that returns `false` on `UnauthorizedAccessException`; one guard site left on the lenient predicate; missing-entry → `true`).

**CHANGELOG:** Security — link guards (render output, concept writes, `log.md`, index writes, the catalog's memory store) now refuse an entry whose link status cannot be inspected, instead of treating it as a plain directory; a junction protected by a deny ACE could previously redirect `okf-render` output outside `--out`.

### Task H2: `okf index dir\` writes the root `index.md`

**Finding:** with a trailing separator, `IndexGenerator` (`src/OKF4net/IndexGenerator.cs` ~184 / ~400) computes the root's parent as the bundle itself and omits the root `index.md` — the library half of E1.

**USER DECISION (2026-09-15): fix it inside `IndexGenerator`, for every caller** (`okf index`, `okf_regenerate_indexes`, any host).

**Files:** `src/OKF4net/IndexGenerator.cs`; tests `tests/OKF4net.Tests/IndexTests.cs` and a CLI-level test beside the existing `okf index` tests; `CHANGELOG.md`.

**Required behaviour:**
- `RegenerateIndexesWith` resolves `bundleRoot` through `ReparsePoints.CanonicalizeRoot` (`Path.TrimEndingDirectorySeparator(Path.GetFullPath(...))`) instead of a bare `Path.GetFullPath` (~line 191). Every later use then sees the trimmed root: `DirectoriesToIndex`'s `rootParent` (~410), the reparse-ancestor walks, and the returned paths.
- Grep `IndexGenerator.cs` for every other `Path.GetFullPath` applied to a root and apply the same treatment. Leave the root of a drive (`C:\`, `/`) as `CanonicalizeRoot` already does.
- The list of written paths returned for `dir\` is identical to the list for `dir`.

**Tests (RED first):**
1. Library: `RegenerateIndexes(root + Path.DirectorySeparatorChar)` writes the root `index.md` and returns the same list as `RegenerateIndexes(root)`. Compare the whole written tree byte for byte against a run without the separator.
2. The alternate separator on Windows (`root + '/'`).
3. CLI: `okf index <dir>\` (in-process `OkfCli.Run`) writes the root `index.md`.
4. Check `tests/fixtures/golden` for any `okf index` capture that passes a trailing separator. If one would change, stop and report instead of touching it.

**CHANGELOG:** Fixed — `okf index dir/` and `IndexGenerator.RegenerateIndexes` with a trailing directory separator now write the root `index.md` (§8).

### Task H3: Frontmatter closing fence and YAML anchors match what the docs say

**Findings:**
- `OkfDocument.Parse` accepts an indented `---` as the closing fence, an undocumented divergence from §4 ("delimited by `---` on its own line"). The shared predicate is `OkfDocument.IsFenceLine(line) => line.Trim() == "---"` (`src/OKF4net/OkfDocument.cs:48`), also used by `Internal/FrontmatterBlockEdit.cs` (task C7).
- The YAML subset (`src/OKF4net/Yaml/YamlParser.cs`) rejects none of anchors, aliases, tags, directives or document markers. `&a`, `*a` and `!!str` values parse as plain strings. `README.md:117-121` and `CLAUDE.md:41` say the subset "rejects (with a clear error) … anchors, tags, multiple documents".

**USER DECISIONS (2026-09-15):**
1. **Fix the fence.** Only a line whose first three characters are `---`, followed by nothing or only spaces/tabs, opens or closes the frontmatter. The `\r` of CRLF is already stripped by `LfLines`. Leading whitespace is no longer accepted. Trailing spaces/tabs are tolerated, as in Jekyll's `^---\s*$`.
2. **Reject what the docs say is rejected**, with a clear `YamlParseException` (line number plus a message naming the feature): anchors, aliases, tags, directives, document markers. Only the constructs themselves are rejected: an indicator at the start of an unquoted node, not a `&`, `*` or `!` inside a string.

**Files:** `src/OKF4net/OkfDocument.cs`, `src/OKF4net/Yaml/YamlParser.cs`, `src/OKF4net/Internal/FrontmatterBlockEdit.cs` (C7), tests (`OkfDocumentTests`, `YamlParserTests` or their current names, `FrontmatterBlockEdit` tests, `RecordVerificationTests` if the fence refusal is pinned there), `README.md`, `CLAUDE.md`, `CHANGELOG.md`.

**Required behaviour: fence**
- `IsFenceLine` becomes: `line.StartsWith("---", Ordinal)` and every character after index 3 is `' '` or `'\t'`.
- Check how a UTF-8 BOM on the first line is handled today, since `string.Trim()` does not strip U+FEFF. Keep that behaviour exactly.
- An indented `---` inside the frontmatter is now an ordinary line: YAML content, or content of a block scalar. Pin the block-scalar case, which used to truncate the frontmatter silently: a `|` block whose content contains an indented `---` line must now round-trip that line.
- An indented `---` on the first line means the document has no frontmatter (the whole text is body), as for any other non-fence first line.
- `FrontmatterBlockEdit` (C7) uses the same predicate and has explicit logic that refuses an indented closing fence. Re-align it so the scan and the parser agree, and keep C7's guarantee that the edit refuses unless `reparsed.Equals(document)`. Remove refusal logic that can no longer trigger, and update C7's comments that describe the old trimming.

**Required behaviour: YAML rejections.** The message names the feature, e.g. `"YAML anchors (&name) are not supported in OKF frontmatter"`. The feature must be named in the message, not quoted from the bundle.
- **Anchor:** an unquoted node (mapping value, sequence item, flow item, or a mapping key) whose first character is `&`, including `key: &a` followed by a nested block.
- **Alias:** an unquoted node whose first character is `*`.
- **Tag:** an unquoted node whose first character is `!` (`!tag`, `!!str`, `!<…>`).
- **Directive:** a frontmatter line starting with `%` at column 0 (`%YAML 1.2`, `%TAG …`).
- **Document marker:** a frontmatter line that is exactly `...` (optionally followed by whitespace) at column 0. `---` cannot occur inside the frontmatter, because it closes it.
- **Not rejected:**
  - quoted scalars (`"*a"`, `'&b'`);
  - indicators in the middle of a plain scalar (`a & b`, `x*y`, `wow!`);
  - block-scalar (`|`, `>`) content;
  - continuation lines of a multi-line plain scalar that happen to start with `*`, `&` or `!` (YAML only restricts a plain scalar's first character).
  - Verify each one against the parser's actual plain-scalar continuation handling.
- How rejections surface: `Bundle.Load` puts them in `ParseErrors` as today for any `YamlParseException`, and `okf validate` reports them as parse errors. No golden capture contains any of these constructs (controller grep of `bundles/`, `tests/fixtures/`, `samples/`: none). Re-check, and stop if one would change.
- `YamlEmitter` already quotes strings starting with these indicators (`YamlEmitter.cs:316`). Add a round-trip test: every emitted string starting with `&`, `*`, `!`, `%` parses back to the same string.

**Tests (RED first where the current code accepts):**
- Fence, accepted:
  - `---` then `---` closes;
  - `---  ` and `---\t` close;
  - CRLF works.
- Fence, no longer a fence:
  - `  ---` as the closing line: the frontmatter does not close there;
  - `  ---` as the first line: no frontmatter;
  - the block-scalar-with-indented-`---` round-trip;
  - `----` and `--- x` are not fences (unchanged).
- YAML, rejected (one row each, asserting the exception, the line number and that the message names the feature):
  - `k: &a v`, `k: *a`, `k: !!str v`, `k: !tag v`;
  - `- &a v`, `- *a`, `[*a]`, `{x: *a}`;
  - `&a k: v`;
  - `%YAML 1.2`;
  - `...`.
- YAML, accepted: `k: "*a"`, `k: 'a&'`, `k: a & b`, `k: x*y`, `k: wow!`, a `|` block containing `*a` and `&b` lines, a multi-line plain scalar whose continuation line starts with `*`.
- Bundle level: a concept with `k: *a` lands in `Bundle.ParseErrors` with the feature-naming message, and the rest of the bundle loads.

**Docs:**
- `README.md` and `CLAUDE.md` list exactly what is rejected, true to the code.
- CHANGELOG, Breaking (0.x): an indented `---` no longer opens or closes the frontmatter (§4); anchors, aliases, tags, directives and document markers now fail parsing with a clear error, as the docs already claimed. Say that such a document previously loaded with those values read as plain strings.

**Review:** adversarial and executed (hostile inputs, mutants), per the project's rule for parsers of untrusted input.

### Task H4: The YAML emitter quotes every key the parser would misread

**Finding (pre-existing; found by the H3 review's fuzz; identical on `b881385` and `7b111b3`):** `YamlEmitter` writes a frontmatter KEY containing `[`, `{`, `"` or `'` after its first character without quoting it. The parser then misreads or rejects the result:
- `{"a[b": "v"}` → emitted `a[b: v` → re-parsed as the scalar string `"a[b: v"` at top level, or throws `unexpected indentation in mapping` when nested.
- Fuzz (10 000 random strings, length 0–8, alphabet ``&*!%@`-?:,[]{}#|>'"abcXYZ .\n\t``, seed 20260915): 992 failures, 496 at top-level keys and 496 at nested keys. Values, sequence items and top-level scalars: 0.
- Root cause: `IsSafePlain` (`src/OKF4net/Yaml/YamlEmitter.cs` ~323–373) judges a key as if it were a standalone scalar.

**USER DECISION (2026-09-15):** fix it in this wave, after H3, as task H4 in the main lane.

**Files:** `src/OKF4net/Yaml/YamlEmitter.cs`; tests `tests/OKF4net.Tests/Yaml/YamlRoundtripTests.cs` (and the emitter tests' current home); `CHANGELOG.md`.

**Required behaviour:**
- In key position, the emitter quotes any key the parser would not read back as the same key. At minimum this covers keys containing `"`, `'`, `[` or `{`.
  - Prefer a precise key-context rule that mirrors the parser's key-splitting logic over "quote anything unusual", so ordinary keys keep their plain form and no golden moves.
  - Read how the parser splits `key: value` (`YamlParser` key-line reading, `TryReadTopLevelKeyLine`) and derive the rule from it. Say in the doc comment which parser behaviour each quoting condition mirrors.
- Choose the quoting style consistently with how the emitter already quotes values (single or double, and escaping).
- Nothing changes for keys that round-trip today: no golden capture, no producer golden and no `BundleConceptWriter` output may move for ordinary keys. Verify with `GoldenParityTests` and `dotnet test producers/OkfProducer.sln`.

**Tests (RED first):**
1. A deterministic key-position fuzz in `YamlRoundtripTests`:
   - the review's alphabet and seed, 10 000 strings, as a top-level key, a nested key, and a key inside a sequence item's mapping;
   - emit then parse, asserting structural identity;
   - bounded runtime (well under a second or two).
   - RED on the H3 head: 992 failures expected at the top level and nested.
2. Explicit rows: `a[b`, `a{b`, `a"b`, `a'b`, `[ab`, `{ab`, `"ab`, `'ab` as keys round-trip; `abc`, `a-b`, `a.b`, `a b` keep their plain form (assert the emitted text).
3. Mutation check: remove the new key-context condition and confirm the fuzz goes RED.

**CHANGELOG:** Fixed — the YAML emitter now quotes a frontmatter key containing `[`, `{`, `"` or `'` (or whatever the final rule is, stated exactly), which it previously wrote plain and the parser then read back as a different structure or rejected.

**Review:** executed (fuzz re-run, mutants, golden/producer checks).

## Phase F — Close

### Task F1: Full verification, CHANGELOG read-through, PR

- [ ] **Step 1:** From the worktree root, run everything and record exact counts:

```powershell
dotnet build OKF4net.sln
dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"
$env:OKF_DEMO_PG_CONN = "postgresql://postgres:demo@host.docker.internal:5544/demo"
dotnet test OKF4net.sln --filter "Category=ContainerIntegration"
dotnet format OKF4net.sln --verify-no-changes
dotnet publish src/OKF4net.Cli -c Release
dotnet publish src/OKF4net.Render -c Release
dotnet test producers/OkfProducer.sln
cd tools/viewer-security-check; npm test; cd ../..
dotnet build samples/acme-retail-agent/AcmeRetailAgent.sln
dotnet build samples/attestation-containers-demo/AttestationContainersDemo.sln
```
All green, 0 skipped in the integration run.

- [ ] **Step 2: CHANGELOG bullet-by-bullet check** — for every `[Unreleased]` line this plan added, open the code it names and confirm the claim (the project's history is exactly claims that drifted). Fix wording, not code, where they disagree — unless the code is wrong.

- [ ] **Step 3: Push and open the PR against `dev`**

```powershell
git push -u origin fix/post-audit-2026-09
gh pr create --base dev --title "fix: close the v0.5.0..dev review findings" --body-file docs/superpowers/plans/2026-09-12-post-audit-fixes.md
```
PR body: the counts from Step 1, the list of findings closed by number, and the two things it deliberately does not do (no §6.2 fallback; no `OKF_MCP_READONLY` shim), ending with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.

- [ ] **Step 4:** `docker stop okf-demo-pg`. Leave the worktree in place until the PR is merged, then `git worktree remove E:\Sources\okf-post-audit`.

---

## Self-review

- **Coverage:** #1 → B1; #2 → E1; #3 → B2; #4 + BOM → C1; #5 → C3; #6 → C15; #7 → C2; #8 → already fixed on `dev` (`BoundedRead`), no task; #9 → B5; #10 → C4; #11 → C7; #12 → E2; #13 → B3; #14 → already fixed on `dev` (`Timeout` validated before `Start`), no task; #15 → D1; #16 → C15; #17 → C5, C6, E3, E4, E5, E6, E7, E12, C15 (CLAUDE.md); viewer collision → D2; quality: orchestrator catch ×3 → B4; ceilings ×2 + specs ×3 → B2; attester prologue → B6; render scanner drift → C14; audit/verify renderers → C11; `VerbSpec.Name` → C14; labels → C11; `MemoryFrontmatter` → C12; timestamp grammar ×3 → C8; process runners + reparse walks → E11; verify pre-pass + cache → C13; `HtmlWriter` guard, `HtmlSafeJson`, reparse resolver → D3; `ConceptGenerator` O(k²), MSBuild serial → E10; `Validate.cs` carve-out layer → C9; `LfLines` → C10; device names → E9; tree-sitter attribution → E8. Release readiness (combined §6.2 entry, MCP no-shim sentence, `ComputationTimeout`, `[Obsolete]`) → C9 + C15.
- **Not done, by decision:** `GitRevision.FormatUtc` stays a private copy (producers may only use `OKF4net`'s public API; exposing `OkfTimestamp` publicly is out of scope) — C8 notes it.
- **Type consistency:** `ContainerIsolation.ToRunSpec` signature is used identically in B2 steps 1, 3 and 7; `AttestationDiagnosticException` (B4) is what B3/B6's `ContainerExecutionException` derives from and what B5's `FormatOutcome` tests; `ParameterValues.Normalize` (B1) is called once in `RunComputationAsync`; `AuditText` (C11) is consumed by both renderers; `FrontmatterBlockEdit.ReplaceTopLevelKey` (C7) takes the whole document text and an LF block; `OkfTimestamp.IsEmittedUtcForm` (C8) replaces both `TryParseExact` calls; `CliArgs` (C14) keeps every error string C2 introduced.
