# Acme Retail bundle + Agent Framework sample — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the upstream `acme_retail` OKF v0.2 sample bundle into this repo at `bundles/acme_retail/`, and build a standalone console sample (`samples/acme-retail-agent/`) that drives it through Microsoft Agent Framework (`OKF4net.Agents`), read-only, against any OpenAI-compatible chat endpoint.

**Architecture:** Part A copies the bundle verbatim (byte-exact via `gh api`, pinned to one commit) and documents its provenance. Part B is a self-contained console app with its own `.sln`: `ChatClientFactory` resolves an `IChatClient` from env vars pointed at an OpenAI-compatible endpoint; `Program.cs` wires `OkfBundleTools` (no attestation orchestrator — read-only tool set) + `OkfContextProvider` into a `ChatClientAgent`, then runs an interactive REPL or a one-shot `--prompt` turn.

**Tech Stack:** .NET 10 (net10.0), `Microsoft.Agents.AI` 1.15.0, `Microsoft.Extensions.AI.OpenAI` 10.8.3, xunit is NOT used here (see Global Constraints).

## Global Constraints

- Spec of record: `docs/superpowers/specs/2026-07-30-acme-retail-bundle-and-agent-sample-design.md`. Read it before starting if anything below is ambiguous.
- `bundles/acme_retail/` content is copied byte-exact from `GoogleCloudPlatform/knowledge-catalog` commit `3fcbb9f828c2f23d109c855ee403c3a4c81f3a96`, Apache-2.0. Never hand-edit the copied `.md`/`.py` files' content.
- `attesters/sql_equality.py` is preserved **untouched** — it is never ported or reimplemented in C#, in this plan or otherwise (see [[feedback-attestation-no-porting-sanctioned-scripts]] / the spec's "Why not port the attester to C#" section). `okf_run_computation` is never wired in this sample.
- `viz.html` is **not** copied from upstream.
- `samples/acme-retail-agent/` has its own `AcmeRetailAgent.sln` and is **not** added to the repo's `OKF4net.sln` and **not** wired into `ci.yml`.
- Every new `.cs` file starts with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- `samples/acme-retail-agent/` inherits the repo-root `Directory.Build.props` automatically (`Nullable=enable`, `TreatWarningsAsErrors=true`, `LangVersion=14`, `ImplicitUsings=enable`) — MSBuild walks up the directory tree regardless of solution membership. Any nullable-reference-type warning is a build failure; use `[NotNullWhen]`/`[MaybeNullWhen]` attributes on `TryXxx`-style methods to keep flow analysis clean.
- No dedicated xunit test project for `samples/acme-retail-agent/` — per the design spec, its only bespoke logic (env-var config resolution) is small enough to verify by manual smoke test (exact commands + expected output are given in each task below). Do not add an xunit project speculatively.
- All commands below assume the current working directory is the repo root (`e:\Sources\okf`) unless a task explicitly says otherwise.

---

## Task 1: Copy the acme_retail bundle files

**Files:**
- Create: `bundles/acme_retail/index.md`, `bundles/acme_retail/log.md`, `bundles/acme_retail/attesters/index.md`, `bundles/acme_retail/attesters/sql_equality.py`, `bundles/acme_retail/computations/index.md`, `bundles/acme_retail/computations/gross-margin-period.md`, `bundles/acme_retail/computations/revenue-ytd.md`, `bundles/acme_retail/metrics/index.md`, `bundles/acme_retail/metrics/revenue.md`, `bundles/acme_retail/metrics/gross-margin.md`, `bundles/acme_retail/metrics/gross-margin-legacy.md`, `bundles/acme_retail/policies/index.md`, `bundles/acme_retail/policies/revenue-recognition.md`, `bundles/acme_retail/policies/margin-standard.md`, `bundles/acme_retail/skills/index.md`, `bundles/acme_retail/skills/run-on-bq.md`, `bundles/acme_retail/tables/index.md`, `bundles/acme_retail/tables/orders.md`

**Interfaces:** None (pure data; no code produced or consumed by later tasks).

- [ ] **Step 1: Fetch every file byte-exact from the pinned upstream commit**

Requires the `gh` CLI, authenticated (this repo's sessions already have it working — confirmed during spec research). Run from the repo root:

```bash
SHA="3fcbb9f828c2f23d109c855ee403c3a4c81f3a96"
mkdir -p bundles/acme_retail/{attesters,computations,metrics,policies,skills,tables}
for f in \
  index.md log.md \
  attesters/index.md attesters/sql_equality.py \
  computations/index.md computations/gross-margin-period.md computations/revenue-ytd.md \
  metrics/index.md metrics/revenue.md metrics/gross-margin.md metrics/gross-margin-legacy.md \
  policies/index.md policies/revenue-recognition.md policies/margin-standard.md \
  skills/index.md skills/run-on-bq.md \
  tables/index.md tables/orders.md; do
  gh api "repos/GoogleCloudPlatform/knowledge-catalog/contents/okf/bundles/acme_retail/$f?ref=$SHA" --jq '.content' | base64 -d > "bundles/acme_retail/$f"
done
```

- [ ] **Step 2: Verify all 18 files landed**

Run: `find bundles/acme_retail -type f | sort`

Expected: exactly the 18 paths listed in the **Files** section above (as `bundles/acme_retail/...`), no more, no fewer.

- [ ] **Step 3: Validate the copied bundle conforms to OKF v0.2**

Run: `dotnet run --project src/OKF4net.Cli -- validate bundles/acme_retail`

Expected: exit code `0`, and the output ends with:

```text
9 concept(s); 0 error(s), 18 warning(s), 0 info.
✓ conformant with OKF v0.2
```

(This exact count was verified against a scratch copy of the same commit during planning — see Task 2's README, which documents why the 18 warnings are expected and harmless.) If the error count is not `0`, something was mistranscribed or the upstream commit changed — stop and investigate before proceeding; do not edit the copied files to silence warnings/errors.

- [ ] **Step 4: Commit**

```bash
git add bundles/acme_retail
git commit -m "$(cat <<'EOF'
data: add acme_retail sample bundle from OKF reference implementation

Copied byte-exact from GoogleCloudPlatform/knowledge-catalog commit
3fcbb9f828c2f23d109c855ee403c3a4c81f3a96 (okf/bundles/acme_retail),
Apache-2.0, minus the generated viz.html. Used for manual testing and by
samples/acme-retail-agent.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Provenance docs (bundle README, NOTICE, CLAUDE.md)

**Files:**
- Create: `bundles/acme_retail/README.md`
- Modify: `NOTICE`
- Modify: `CLAUDE.md`

**Interfaces:** None (documentation only).

- [ ] **Step 1: Write `bundles/acme_retail/README.md`**

```markdown
# Acme Retail (sample bundle)

A fictional retail company's [Open Knowledge Format](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
v0.2 bundle, used in this repo for manual testing and samples (see
[`samples/acme-retail-agent/`](../../samples/acme-retail-agent/README.md)).
It exercises parts of the spec a minimal synthetic bundle can't: `Metric`
and `Policy` concepts, a `Skill`, an `Attested Computation` pair
(`runtime: bigquery`) with its executor and attester, trust tiers
(`verified`), staleness (`stale_after`), and a deprecated concept kept for
historical reproducibility.

## Provenance

Copied verbatim from `okf/bundles/acme_retail` in
[`GoogleCloudPlatform/knowledge-catalog`](https://github.com/GoogleCloudPlatform/knowledge-catalog),
commit [`3fcbb9f828c2f23d109c855ee403c3a4c81f3a96`](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/3fcbb9f828c2f23d109c855ee403c3a4c81f3a96/okf/bundles/acme_retail),
licensed under the Apache License, Version 2.0 — see `LICENSE.Apache-2.0` at
the repo root and the attribution entry in `NOTICE`.

## What's different from upstream

- `viz.html` was **not** carried over: it's a generated artifact of the
  upstream Python `reference_agent` visualizer (Cytoscape JS/CSS tied to
  that toolchain), not OKF bundle content — nothing in this repo generates
  or keeps it in sync.
- `attesters/sql_equality.py` **is** carried over, untouched, as a plain
  reference resource (the `attester.resource` target for
  `computations/*.md`). OKF4net does not execute Python, and nothing in
  this repo ports or reimplements its logic in C# — see
  [`samples/acme-retail-agent/README.md`](../../samples/acme-retail-agent/README.md)
  for why, and what actually running an Attested Computation against this
  bundle would require.

## Validating

```bash
dotnet run --project src/OKF4net.Cli -- validate bundles/acme_retail
```

Exits `0` (conformant): 9 concepts, 0 errors, 18 warnings, 0 info. The
warnings are expected and harmless:

- Most are "missing recommended frontmatter field `resource`" on concept
  types where a `resource` URI doesn't apply (`Metric`, `Skill`).
- The rest are `sources[].resource` / `executor.resource` /
  `attester.resource` frontmatter paths reported as "not found". OKF v0.2
  §6.2 resolves a plain relative path (no leading `/`) against the
  **referencing concept's own directory**, not the bundle root — e.g.
  `computations/gross-margin-period.md`'s `sources[0].resource:
  policies/margin-standard.md` resolves to
  `computations/policies/margin-standard.md` (which doesn't exist); the
  real file is one level up, at `../policies/margin-standard.md` from that
  concept. The upstream bundle writes these paths bundle-root-relative
  instead. This affects only frontmatter-path *resolution* diagnostics —
  reading, browsing, and searching the bundle are unaffected.
```

- [ ] **Step 2: Add a NOTICE entry**

Read `NOTICE` first (it currently ends with the "OKF and Google Cloud are trademarks..." paragraph). Append this new section at the end of the file:

```text

------------------------------------------------------------------------------

bundles/acme_retail/ is a sample OKF bundle copied verbatim from
okf/bundles/acme_retail in the OKF reference implementation

    Open Knowledge Format (OKF)
    Copyright Google LLC
    https://github.com/GoogleCloudPlatform/knowledge-catalog

commit 3fcbb9f828c2f23d109c855ee403c3a4c81f3a96, licensed under the Apache
License, Version 2.0 (see LICENSE.Apache-2.0). It is fictional demonstration
content (a fictional company, "Acme Retail") used here for manual testing
and samples; see bundles/acme_retail/README.md for details, including which
upstream files were intentionally not carried over.
```

- [ ] **Step 3: Add a short note to `CLAUDE.md`**

Read `CLAUDE.md` first and find the line `` `docs/design/` holds historical migration specs/plans — context only; the code and README are authoritative. `` (end of the "Architecture" section). Insert this new paragraph immediately after it:

```markdown

`bundles/` holds sample OKF bundles for manual testing/demos (e.g.
`bundles/acme_retail/`) — distinct from `tests/fixtures/`, which stays
byte-exact golden captures. `samples/` holds standalone example projects
that consume those bundles (each with its own solution/build, not part of
`OKF4net.sln` or CI).
```

- [ ] **Step 4: Review and commit**

Run: `git diff NOTICE CLAUDE.md` and confirm only the intended additions appear (no accidental reformatting of surrounding text).

```bash
git add bundles/acme_retail/README.md NOTICE CLAUDE.md
git commit -m "$(cat <<'EOF'
docs: document acme_retail bundle provenance and license

Adds bundles/acme_retail/README.md (provenance, license, upstream
divergences, validation), a NOTICE attribution entry, and a CLAUDE.md note
describing what bundles/ and samples/ are for.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Scaffold `samples/acme-retail-agent`

**Files:**
- Create: `samples/acme-retail-agent/AcmeRetailAgent.sln`
- Create: `samples/acme-retail-agent/src/AcmeRetailAgent/AcmeRetailAgent.csproj`
- Create: `samples/acme-retail-agent/src/AcmeRetailAgent/Program.cs` (placeholder, replaced in Task 5)

**Interfaces:**
- Produces: an `AcmeRetailAgent` executable project referencing `OKF4net` and `OKF4net.Agents`, with `Microsoft.Agents.AI` 1.15.0 and `Microsoft.Extensions.AI.OpenAI` 10.8.3 package references, targeting `net10.0`, namespace `OKF4net.Samples.AcmeRetailAgent`.

- [ ] **Step 1: Scaffold via the .NET CLI**

Run from the repo root:

```bash
mkdir -p samples/acme-retail-agent/src/AcmeRetailAgent
dotnet new sln --name AcmeRetailAgent --output samples/acme-retail-agent
dotnet new console --name AcmeRetailAgent --output samples/acme-retail-agent/src/AcmeRetailAgent
dotnet sln samples/acme-retail-agent/AcmeRetailAgent.sln add samples/acme-retail-agent/src/AcmeRetailAgent/AcmeRetailAgent.csproj
```

- [ ] **Step 2: Overwrite the generated `.csproj` with its final content**

Replace the full content of `samples/acme-retail-agent/src/AcmeRetailAgent/AcmeRetailAgent.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>OKF4net.Samples.AcmeRetailAgent</RootNamespace>
    <AssemblyName>AcmeRetailAgent</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Agents.AI" Version="1.15.0" />
    <PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.8.3" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\OKF4net\OKF4net.csproj" />
    <ProjectReference Include="..\..\..\..\src\OKF4net.Agents\OKF4net.Agents.csproj" />
  </ItemGroup>

</Project>
```

(No explicit `Nullable`/`TreatWarningsAsErrors`/`ImplicitUsings` here — inherited from the repo-root `Directory.Build.props`.)

- [ ] **Step 3: Replace the placeholder `Program.cs`**

Replace the full content of `samples/acme-retail-agent/src/AcmeRetailAgent/Program.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
Console.WriteLine("AcmeRetailAgent scaffold OK.");
```

- [ ] **Step 4: Build and run**

Run: `dotnet build samples/acme-retail-agent/AcmeRetailAgent.sln`

Expected: `Build succeeded.` with `0 Warning(s)` and `0 Error(s)`.

Run: `dotnet run --project samples/acme-retail-agent/src/AcmeRetailAgent`

Expected output: `AcmeRetailAgent scaffold OK.`

- [ ] **Step 5: Commit**

```bash
git add samples/acme-retail-agent/AcmeRetailAgent.sln samples/acme-retail-agent/src
git commit -m "$(cat <<'EOF'
chore(samples): scaffold acme-retail-agent console project

Standalone project (own .sln, not in OKF4net.sln/CI) referencing OKF4net
and OKF4net.Agents, targeting net10.0. Placeholder Program.cs; real logic
follows in later commits.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Startup config resolution (bundle root + chat client)

**Files:**
- Create: `samples/acme-retail-agent/src/AcmeRetailAgent/ChatClientFactory.cs`
- Modify: `samples/acme-retail-agent/src/AcmeRetailAgent/Program.cs`

**Interfaces:**
- Consumes: none beyond BCL/`Microsoft.Extensions.AI`/`OpenAI` package APIs.
- Produces: `OKF4net.Samples.AcmeRetailAgent.ChatClientFactory.TryCreate(Func<string,string?> getEnv, out IChatClient? client, out string? error) : bool` and `ChatClientFactory.FormatStartupError(string? error) : string`, plus `ChatClientFactory.BaseUrlEnv`/`ApiKeyEnv`/`ModelEnv` (`const string`). `Program.cs`'s bundle-root resolution (a `ResolveBundleRoot(string?) : string?` local function) is consumed unchanged by Task 5.

- [ ] **Step 1: Write `ChatClientFactory.cs`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using OpenAI;

namespace OKF4net.Samples.AcmeRetailAgent;

/// <summary>
/// Resolves an <see cref="IChatClient"/> from environment variables naming an
/// OpenAI-compatible endpoint (OpenAI, Ollama, Azure OpenAI, or any
/// Claude/Copilot-fronting OpenAI-compatible gateway) -- one code path for
/// every provider, no per-provider branching.
/// </summary>
public static class ChatClientFactory
{
    /// <summary>Environment variable naming the OpenAI-compatible base URL (required).</summary>
    public const string BaseUrlEnv = "OKF_CHAT_BASE_URL";

    /// <summary>Environment variable naming the bearer API key (optional -- e.g. not required for local Ollama).</summary>
    public const string ApiKeyEnv = "OKF_CHAT_API_KEY";

    /// <summary>Environment variable naming the model id understood by the endpoint (required).</summary>
    public const string ModelEnv = "OKF_CHAT_MODEL";

    /// <summary>
    /// Resolves an <see cref="IChatClient"/> from <paramref name="getEnv"/>.
    /// Returns <see langword="false"/> with a human-readable
    /// <paramref name="error"/> when <see cref="BaseUrlEnv"/> is missing or
    /// not a valid absolute URI, or <see cref="ModelEnv"/> is missing. Makes
    /// no network calls -- the returned client only talks to the endpoint on
    /// its first real chat request.
    /// </summary>
    /// <param name="getEnv">Environment-variable accessor (e.g. <see cref="Environment.GetEnvironmentVariable(string)"/>).</param>
    /// <param name="client">The resolved chat client, or <see langword="null"/> on failure.</param>
    /// <param name="error">The failure reason, or <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryCreate(
        Func<string, string?> getEnv,
        [NotNullWhen(true)] out IChatClient? client,
        [NotNullWhen(false)] out string? error)
    {
        client = null;

        var baseUrl = getEnv(BaseUrlEnv)?.Trim();
        if (string.IsNullOrEmpty(baseUrl))
        {
            error = $"{BaseUrlEnv} is not set";
            return false;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint))
        {
            error = $"{BaseUrlEnv} is not a valid absolute URI: '{baseUrl}'";
            return false;
        }

        var model = getEnv(ModelEnv)?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            error = $"{ModelEnv} is not set";
            return false;
        }

        // Ollama and other local gateways ignore the API key, but the OpenAI
        // SDK's ApiKeyCredential rejects an empty string -- fall back to a
        // placeholder when none is configured.
        var apiKey = getEnv(ApiKeyEnv)?.Trim();
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(apiKey) ? "unused" : apiKey);
        var options = new OpenAIClientOptions { Endpoint = endpoint };

        client = new OpenAIClient(credential, options).GetChatClient(model).AsIChatClient();
        error = null;
        return true;
    }

    /// <summary>
    /// Formats a single-line startup usage/error for stderr, mirroring
    /// <c>OkfMcpConfig.FormatStartupError</c>'s convention in
    /// <c>OKF4net.Mcp</c>.
    /// </summary>
    /// <param name="error">The failure reason from <see cref="TryCreate"/> (may be <see langword="null"/>).</param>
    public static string FormatStartupError(string? error)
    {
        var message = string.IsNullOrWhiteSpace(error) ? "startup configuration error" : error.Trim();
        return $"acme-retail-agent: {message}. Set {BaseUrlEnv} and {ModelEnv} "
            + $"(and optionally {ApiKeyEnv}) to an OpenAI-compatible endpoint.";
    }
}
```

- [ ] **Step 2: Wire bundle-root + chat-client resolution into `Program.cs`**

Replace the full content of `samples/acme-retail-agent/src/AcmeRetailAgent/Program.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Samples.AcmeRetailAgent;

var bundleRoot = ResolveBundleRoot(Environment.GetEnvironmentVariable("OKF_BUNDLE_ROOT"));
if (bundleRoot is null)
{
    Console.Error.WriteLine(
        "acme-retail-agent: could not locate bundles/acme_retail (no OKF4net.sln found "
        + "above " + AppContext.BaseDirectory + "). Set OKF_BUNDLE_ROOT to an absolute path instead.");
    return 2;
}

if (!Directory.Exists(bundleRoot))
{
    Console.Error.WriteLine($"acme-retail-agent: bundle root not found: {bundleRoot}. Set OKF_BUNDLE_ROOT to override.");
    return 2;
}

if (!ChatClientFactory.TryCreate(Environment.GetEnvironmentVariable, out var chatClient, out var chatError))
{
    Console.Error.WriteLine(ChatClientFactory.FormatStartupError(chatError));
    return 2;
}

Console.WriteLine($"chat client ready (bundle root: {bundleRoot})");
return 0;

static string? ResolveBundleRoot(string? overridePath)
{
    if (!string.IsNullOrWhiteSpace(overridePath))
    {
        return Path.GetFullPath(overridePath.Trim());
    }

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
    {
        dir = dir.Parent;
    }

    return dir is null ? null : Path.Combine(dir.FullName, "bundles", "acme_retail");
}
```

(`ResolveBundleRoot` walks up from the running assembly's output directory to find `OKF4net.sln` — the same technique `tests/OKF4net.Tests/TestPaths.cs`'s `RepoRoot()` already uses in this repo, robust regardless of Debug/Release output-folder depth. `OKF_BUNDLE_ROOT` overrides it; pass an absolute path when using the override, since a relative one resolves against the process's actual working directory, which for `dotnet run` is the project's build output folder, not your shell's cwd.)

- [ ] **Step 3: Build**

Run: `dotnet build samples/acme-retail-agent/AcmeRetailAgent.sln`

Expected: `Build succeeded.`, `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 4: Manually verify the missing-config error path**

Run (from repo root, in a shell with no `OKF_CHAT_*` vars set):

```bash
dotnet run --project samples/acme-retail-agent/src/AcmeRetailAgent
```

Expected: exit code `2`, and stderr contains exactly:

```text
acme-retail-agent: OKF_CHAT_BASE_URL is not set. Set OKF_CHAT_BASE_URL and OKF_CHAT_MODEL (and optionally OKF_CHAT_API_KEY) to an OpenAI-compatible endpoint.
```

- [ ] **Step 5: Manually verify the success path**

Run:

```bash
OKF_CHAT_BASE_URL=http://localhost:11434/v1 OKF_CHAT_MODEL=llama3 \
  dotnet run --project samples/acme-retail-agent/src/AcmeRetailAgent
```

Expected: exit code `0`, stdout is `chat client ready (bundle root: <absolute path>/bundles/acme_retail)`. (No network call happens here — `ChatClientFactory.TryCreate` only constructs the client object; nothing needs to actually be listening on `localhost:11434`.)

- [ ] **Step 6: Commit**

```bash
git add samples/acme-retail-agent/src
git commit -m "$(cat <<'EOF'
feat(samples): resolve bundle root and OpenAI-compatible chat client

ChatClientFactory builds an IChatClient from OKF_CHAT_BASE_URL/_MODEL/
_API_KEY, covering OpenAI/Ollama/Azure OpenAI/any OpenAI-compatible gateway
via one code path. Program.cs locates bundles/acme_retail by walking up to
OKF4net.sln (overridable via OKF_BUNDLE_ROOT), mirroring OkfMcpConfig's
startup-error convention.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Agent wiring + REPL / one-shot CLI

**Files:**
- Modify: `samples/acme-retail-agent/src/AcmeRetailAgent/Program.cs`

**Interfaces:**
- Consumes: `ChatClientFactory.TryCreate`/`FormatStartupError` (Task 4), `OKF4net.Agents.OkfBundleTools` (constructor `OkfBundleTools(string bundleRoot)`, `GetTools() : IList<AITool>`), `OKF4net.Agents.OkfContextProvider` (constructor `OkfContextProvider(OkfBundleTools tools, OkfContextProviderOptions? options = null)`), `Microsoft.Agents.AI.ChatClientAgentOptions`, `Microsoft.Extensions.AI.ChatOptions`, `IChatClient.AsAIAgent(ChatClientAgentOptions)`, `AIAgent.CreateSessionAsync()`, `AIAgent.RunAsync(string, AgentSession?)`.
- Produces: the finished CLI entry point (no further tasks depend on it).

- [ ] **Step 1: Replace `Program.cs` with the full agent + CLI logic**

Replace the full content of `samples/acme-retail-agent/src/AcmeRetailAgent/Program.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Samples.AcmeRetailAgent;

var bundleRoot = ResolveBundleRoot(Environment.GetEnvironmentVariable("OKF_BUNDLE_ROOT"));
if (bundleRoot is null)
{
    Console.Error.WriteLine(
        "acme-retail-agent: could not locate bundles/acme_retail (no OKF4net.sln found "
        + "above " + AppContext.BaseDirectory + "). Set OKF_BUNDLE_ROOT to an absolute path instead.");
    return 2;
}

if (!Directory.Exists(bundleRoot))
{
    Console.Error.WriteLine($"acme-retail-agent: bundle root not found: {bundleRoot}. Set OKF_BUNDLE_ROOT to override.");
    return 2;
}

if (!ChatClientFactory.TryCreate(Environment.GetEnvironmentVariable, out var chatClient, out var chatError))
{
    Console.Error.WriteLine(ChatClientFactory.FormatStartupError(chatError));
    return 2;
}

const string SystemInstructions =
    "You are grounded in the Acme Retail OKF knowledge bundle (a fictional "
    + "retail company's metrics, policies, and attested computations). Use "
    + "the okf_* tools to answer questions -- do not guess at bundle "
    + "content. Attested Computations can be inspected with "
    + "okf_get_computation (their contract and sanctioned SQL) but this "
    + "sample cannot run them.";

var tools = new OkfBundleTools(bundleRoot);
var contextProvider = new OkfContextProvider(tools);
var agentOptions = new ChatClientAgentOptions
{
    ChatOptions = new ChatOptions
    {
        Instructions = SystemInstructions,
        Tools = tools.GetTools(),
    },
    AIContextProviders = [contextProvider],
};
AIAgent agent = chatClient.AsAIAgent(agentOptions);

var oneShotPrompt = ReadOneShotPrompt(args);
if (oneShotPrompt is not null)
{
    var response = await agent.RunAsync(oneShotPrompt);
    PrintToolCalls(response);
    Console.WriteLine(response.Text);
    return 0;
}

Console.WriteLine("Acme Retail agent -- ask a question, or type 'exit'/'quit' to leave.");
var session = await agent.CreateSessionAsync();
while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null || line.Trim() is "exit" or "quit")
    {
        break;
    }

    if (line.Trim().Length == 0)
    {
        continue;
    }

    var response = await agent.RunAsync(line, session);
    PrintToolCalls(response);
    Console.WriteLine(response.Text);
}

return 0;

static string? ResolveBundleRoot(string? overridePath)
{
    if (!string.IsNullOrWhiteSpace(overridePath))
    {
        return Path.GetFullPath(overridePath.Trim());
    }

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
    {
        dir = dir.Parent;
    }

    return dir is null ? null : Path.Combine(dir.FullName, "bundles", "acme_retail");
}

static string? ReadOneShotPrompt(string[] args)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--prompt" && i + 1 < args.Length)
        {
            return args[i + 1];
        }
    }

    if (Console.IsInputRedirected)
    {
        var piped = Console.In.ReadToEnd();
        return string.IsNullOrWhiteSpace(piped) ? null : piped.Trim();
    }

    return null;
}

static void PrintToolCalls(AgentResponse response)
{
    var calls = response.Messages
        .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
        .Select(c => c.Name)
        .ToList();
    if (calls.Count > 0)
    {
        Console.WriteLine($"[tools: {string.Join(", ", calls)}]");
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build samples/acme-retail-agent/AcmeRetailAgent.sln`

Expected: `Build succeeded.`, `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 3: Manually verify the network-free paths**

The full REPL/one-shot flow needs a real (or locally running) OpenAI-compatible endpoint to actually converse — out of scope to fake here (per the design spec, this sample's chat loop is manually smoke-tested by a human with a real provider, not covered by an automated test). Two things ARE verifiable without any network call, because `agent.RunAsync` is never reached in either case:

1. Missing config still fails the same way as Task 4 verified (unchanged code path — re-run Task 4 Step 4's command and confirm the same error).
2. The REPL starts and exits cleanly on immediate EOF, never calling the agent:

```bash
echo -n "" | OKF_CHAT_BASE_URL=http://localhost:11434/v1 OKF_CHAT_MODEL=llama3 \
  dotnet run --project samples/acme-retail-agent/src/AcmeRetailAgent
```

Expected: exit code `0`, stdout is exactly:

```text
Acme Retail agent -- ask a question, or type 'exit'/'quit' to leave.
> 
```

(the trailing `> ` prompt with no newline after it, then the process exits because `Console.ReadLine()` returns `null` on EOF).

Note for later manual follow-up (not asserted by this plan): trying `--prompt "..."` or typing a real question against a live endpoint (e.g. a running local Ollama with a pulled model, or a real `OKF_CHAT_API_KEY`) to confirm the agent actually answers using the `okf_*` tools and the `[tools: ...]` line appears.

- [ ] **Step 4: Commit**

```bash
git add samples/acme-retail-agent/src
git commit -m "$(cat <<'EOF'
feat(samples): wire OkfBundleTools + OkfContextProvider into a REPL/one-shot agent

ChatClientAgent built via ChatClientAgentOptions (tools on ChatOptions.Tools,
OkfContextProvider on AIContextProviders, system instructions on
ChatOptions.Instructions). Interactive REPL keeps one AgentSession across
turns; --prompt/piped-stdin runs a single turn and exits. Each response
prints which okf_* tools the agent called, for visibility.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Sample README

**Files:**
- Create: `samples/acme-retail-agent/README.md`

**Interfaces:** None (documentation only).

- [ ] **Step 1: Write `samples/acme-retail-agent/README.md`**

```markdown
# Acme Retail agent sample

A standalone console app demonstrating [Microsoft Agent Framework](https://github.com/microsoft/agent-framework)
driving [OKF4net.Agents](../../src/OKF4net.Agents/README.md)'s tools and
context provider against the [`bundles/acme_retail`](../../bundles/acme_retail/README.md)
sample bundle. Read-only: browse/read/search/graph the bundle, and inspect
(but not run) its Attested Computations. See
[the design spec](../../docs/superpowers/specs/2026-07-30-acme-retail-bundle-and-agent-sample-design.md)
for the full rationale, including why this sample does not run
`attesters/sql_equality.py` or wire `okf_run_computation`.

Standalone: this project has its own `AcmeRetailAgent.sln`, is not part of
`OKF4net.sln`, and is not built or tested by this repo's CI.

## Setup

Point the agent at any OpenAI-compatible chat endpoint via environment
variables:

- `OKF_CHAT_BASE_URL` (required) — e.g. `https://api.openai.com/v1`,
  `http://localhost:11434/v1` (Ollama), or any OpenAI-compatible gateway
  (including Claude- or Copilot-fronting proxies).
- `OKF_CHAT_MODEL` (required) — a model id understood by that endpoint.
- `OKF_CHAT_API_KEY` (optional) — bearer key; not required for local
  Ollama.
- `OKF_BUNDLE_ROOT` (optional) — overrides the bundle path; defaults to
  `bundles/acme_retail` at this repo's root (located by walking up from
  the running assembly to `OKF4net.sln`). Use an absolute path if you set
  this — see the note in `Program.cs`.

## Run

Interactive:

```bash
OKF_CHAT_BASE_URL=http://localhost:11434/v1 OKF_CHAT_MODEL=llama3 \
  dotnet run --project src/AcmeRetailAgent
```

One-shot:

```bash
OKF_CHAT_BASE_URL=http://localhost:11434/v1 OKF_CHAT_MODEL=llama3 \
  dotnet run --project src/AcmeRetailAgent -- --prompt "What is Acme's FY2026 revenue recognition policy?"
```

(or pipe a prompt via stdin instead of `--prompt`). Type `exit` or `quit` to
leave the interactive REPL.

## What it does

Wires `OkfBundleTools` (constructed without an `AttestationOrchestrator`, so
`okf_run_computation` is not exposed — only `okf_get_computation`, which
reads a §10 contract and its sanctioned SQL, is available for the two
Attested Computations in the bundle) and `OkfContextProvider` (ambient
budget-bounded context injection) into one `ChatClientAgent`. The
interactive mode keeps one `AgentSession` across turns; one-shot mode runs a
single turn and exits. Each response prints a `[tools: ...]` line naming any
`okf_*` tools the agent called, for visibility into what it did.

## Why no attested-computation execution

`bundles/acme_retail/attesters/sql_equality.py` is kept untouched, not
ported to C#: §10's attestation trust model depends on running the *actual*
sanctioned script, not a reimplementation that could silently diverge from
it. Real execution is planned as a separate, later container-based
execution runtime that runs the sanctioned scripts themselves — see the
design spec's "Future work" section.
```

- [ ] **Step 2: Commit**

```bash
git add samples/acme-retail-agent/README.md
git commit -m "$(cat <<'EOF'
docs(samples): add acme-retail-agent README

Setup (env vars), interactive/one-shot run commands, what the sample wires
up, and why it doesn't run Attested Computations yet.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```
