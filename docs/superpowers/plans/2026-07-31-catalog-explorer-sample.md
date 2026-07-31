# Catalog Explorer Sample Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a standalone console sample, `samples/catalog-explorer`, that exercises `OKF4net.Catalog` end to end (multi-source search, ranking-strategy comparison, per-caller source visibility, and the `role: memory` tier) — the only project in this repo with no existing sample — against `bundles/acme_retail` plus a newly-vendored `bundles/ga4`.

**Architecture:** A DI-free console app (`OKF4net.Catalog` only, no `OKF4net.Catalog.Hosting`) runs five scenarios in sequence, printing each under its own header. `bundles/ga4` is vendored verbatim (Apache-2.0) from the OKF reference repo, the same way `bundles/acme_retail` already was. `bundles/README.md`, which currently doubles as `acme_retail`'s own docs, is restructured into a short index now that there are two bundles.

**Tech Stack:** .NET 10 (net10.0), C# top-level statements, `OKF4net.Catalog` (zero third-party dependencies).

## Global Constraints

- Full design rationale: `docs/superpowers/specs/2026-07-31-catalog-explorer-sample-design.md` (read before starting; this plan implements it as corrected).
- Zero third-party runtime dependencies: `samples/catalog-explorer` references only `OKF4net.Catalog` (which itself references only `OKF4net`).
- `samples/catalog-explorer` gets its own `CatalogExplorer.sln` — **not** added to `OKF4net.sln`, **not** wired into `ci.yml` — same convention as `samples/acme-retail-agent`. It still inherits the repo's `Directory.Build.props` (`Nullable=enable`, `TreatWarningsAsErrors=true`, `LangVersion=14`, `ImplicitUsings=enable`) automatically, since MSBuild walks up the directory tree regardless of solution membership.
- Every new `.cs` file starts with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- `bundles/ga4` is copied byte-for-byte verbatim from `okf/bundles/ga4` at `GoogleCloudPlatform/knowledge-catalog` commit `3fcbb9f828c2f23d109c855ee403c3a4c81f3a96` (the same commit `bundles/acme_retail` is pinned to), Apache-2.0, minus `viz.html` (a generated artifact of the upstream visualizer, not OKF content — same exclusion already applied to `acme_retail`).
- No dedicated test project for `samples/catalog-explorer` (matches `samples/acme-retail-agent`): verification throughout is `dotnet build` + manual `dotnet run`, checking the printed output's content against what's described in each task below.
- The YAML parser fix that made `bundles/ga4` (and `crypto_bitcoin`/`stackoverflow`) parseable already shipped in commit `11f7f47` on this branch — nothing in this plan touches `src/OKF4net/Yaml/`.

---

## Task 1: Vendor `bundles/ga4` and restructure the bundle docs

**Files:**
- Create: `bundles/ga4/index.md`, `bundles/ga4/datasets/index.md`, `bundles/ga4/datasets/ga4_obfuscated_sample_ecommerce.md`, `bundles/ga4/references/index.md`, `bundles/ga4/references/metrics/index.md`, `bundles/ga4/references/metrics/acquired_users.md`, `bundles/ga4/references/metrics/frequently_active_users.md`, `bundles/ga4/references/metrics/google_acquired_cohorts.md`, `bundles/ga4/references/metrics/highly_active_users.md`, `bundles/ga4/references/metrics/n_day_active_users.md`, `bundles/ga4/references/metrics/n_day_inactive_users.md`, `bundles/ga4/references/metrics/purchasers.md`, `bundles/ga4/tables/index.md`, `bundles/ga4/tables/events_.md` (14 files, vendored verbatim — do not hand-edit their content)
- Create: `bundles/ga4/README.md`
- Create: `bundles/acme_retail/README.md` (content moved from the current `bundles/README.md`, unchanged)
- Modify: `bundles/README.md` (rewritten as a short index)
- Modify: `.gitattributes`
- Modify: `NOTICE`
- Modify: `samples/acme-retail-agent/README.md:5`

**Interfaces:** None (docs/content only, no code).

- [ ] **Step 1: Vendor the 14 `bundles/ga4` files verbatim**

From the repo root:

```bash
tmp="$(mktemp -d)"
gh api repos/GoogleCloudPlatform/knowledge-catalog/tarball/3fcbb9f828c2f23d109c855ee403c3a4c81f3a96 > "$tmp/repo.tar.gz"
mkdir "$tmp/extracted"
tar -xzf "$tmp/repo.tar.gz" -C "$tmp/extracted" --strip-components=1
cp -r "$tmp/extracted/okf/bundles/ga4" bundles/ga4
rm -f bundles/ga4/viz.html
find bundles/ga4 -type f | sort
```

Expected: 14 files (no `viz.html`, no `README.md` yet — that's Step 4), matching this list:

```
bundles/ga4/datasets/ga4_obfuscated_sample_ecommerce.md
bundles/ga4/datasets/index.md
bundles/ga4/index.md
bundles/ga4/references/index.md
bundles/ga4/references/metrics/acquired_users.md
bundles/ga4/references/metrics/frequently_active_users.md
bundles/ga4/references/metrics/google_acquired_cohorts.md
bundles/ga4/references/metrics/highly_active_users.md
bundles/ga4/references/metrics/index.md
bundles/ga4/references/metrics/n_day_active_users.md
bundles/ga4/references/metrics/n_day_inactive_users.md
bundles/ga4/references/metrics/purchasers.md
bundles/ga4/tables/events_.md
bundles/ga4/tables/index.md
```

- [ ] **Step 2: Protect the vendored content from line-ending conversion**

Edit `.gitattributes`, adding a third line (mirroring the existing `bundles/acme_retail/` entry):

```
tests/fixtures/** -text
bundles/acme_retail/** -text
bundles/ga4/** -text
```

- [ ] **Step 3: Validate the vendored bundle**

```bash
dotnet run --project src/OKF4net.Cli -- validate bundles/ga4
```

Expected: exit code `0`, output ending in `9 concept(s); 0 error(s), 0 warning(s), 0 info.` and `✓ conformant with OKF v0.2`. (If this doesn't match — e.g. any error or warning appears — stop and investigate before continuing; do not hand-edit the vendored files to force a clean result, per the project's "never touch vendored/fixture content to make a check pass" convention. The commit-pinned upstream content is expected to be genuinely clean now that the YAML multi-line-scalar parser fix has shipped.)

- [ ] **Step 4: Move `bundles/README.md`'s current content to `bundles/acme_retail/README.md`**

`bundles/acme_retail/` has no README of its own today — `bundles/README.md` currently *is* its documentation (there being only one bundle so far). Read the current `bundles/README.md`, then write its exact content, unchanged, to a new `bundles/acme_retail/README.md`.

- [ ] **Step 5: Write `bundles/ga4/README.md`**

```markdown
# GA4 (sample bundle)

Google's public [Open Knowledge Format](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
v0.2 bundle documenting the Google Analytics 4 obfuscated sample ecommerce
BigQuery dataset, used in this repo as a second knowledge source alongside
[`bundles/acme_retail`](../acme_retail/README.md) — see
[`samples/catalog-explorer/`](../../samples/catalog-explorer/README.md).
It exercises concept types `acme_retail` doesn't: a `BigQuery Dataset`, and
a set of `Reference` concepts documenting ecommerce audience metrics
(`purchasers`, `n_day_active_users`, and others).

## Provenance

Copied verbatim from `okf/bundles/ga4` in
[`GoogleCloudPlatform/knowledge-catalog`](https://github.com/GoogleCloudPlatform/knowledge-catalog),
commit [`3fcbb9f828c2f23d109c855ee403c3a4c81f3a96`](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/3fcbb9f828c2f23d109c855ee403c3a4c81f3a96/okf/bundles/ga4),
licensed under the Apache License, Version 2.0 — see `LICENSE.Apache-2.0` at
the repo root and the attribution entry in `NOTICE`.

## What's different from upstream

- `viz.html` was **not** carried over: it's a generated artifact of the
  upstream Python `reference_agent` visualizer (Cytoscape JS/CSS tied to
  that toolchain), not OKF bundle content — nothing in this repo generates
  or keeps it in sync (same as `bundles/acme_retail`).

## Validating

```bash
dotnet run --project src/OKF4net.Cli -- validate bundles/ga4
```

Exits `0` (conformant): 9 concepts, 0 errors, 0 warnings, 0 info.
```

- [ ] **Step 6: Rewrite `bundles/README.md` as a short index**

```markdown
# Sample bundles

Sample [Open Knowledge Format](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
v0.2 bundles used in this repo for manual testing and samples — distinct
from [`tests/fixtures/`](../tests/fixtures/README.md), which stays
byte-exact golden CLI captures.

- [`acme_retail/`](acme_retail/README.md) — a fictional retail company;
  `Metric`/`Policy`/`Skill`/`Attested Computation` concepts, trust tiers,
  staleness.
- [`ga4/`](ga4/README.md) — Google's public GA4 ecommerce reference docs;
  `BigQuery Dataset`/`BigQuery Table`/`Reference` concepts.

Both are consumed together by [`samples/catalog-explorer/`](../samples/catalog-explorer/README.md);
`acme_retail` alone is also consumed by [`samples/acme-retail-agent/`](../samples/acme-retail-agent/README.md).
```

- [ ] **Step 7: Fix the one existing inbound link**

In `samples/acme-retail-agent/README.md`, line 5 currently reads (in context):

```
context provider against the [`bundles/acme_retail`](../../bundles/README.md)
```

Change the link target so it points at the relocated file directly:

```
context provider against the [`bundles/acme_retail`](../../bundles/acme_retail/README.md)
```

- [ ] **Step 8: Fix `NOTICE`'s cross-reference and add the `ga4` attribution entry**

`NOTICE` currently ends with this paragraph (attributing `bundles/acme_retail/`):

```
bundles/acme_retail/ is a sample OKF bundle copied verbatim from
okf/bundles/acme_retail in the OKF reference implementation

    Open Knowledge Format (OKF)
    Copyright Google LLC
    https://github.com/GoogleCloudPlatform/knowledge-catalog

commit 3fcbb9f828c2f23d109c855ee403c3a4c81f3a96, licensed under the Apache
License, Version 2.0 (see LICENSE.Apache-2.0). It is fictional demonstration
content (a fictional company, "Acme Retail") used here for manual testing
and samples; see bundles/README.md for details, including which
upstream files were intentionally not carried over.
```

Change its last sentence's `bundles/README.md` reference to `bundles/acme_retail/README.md`, then append a new paragraph for `ga4`, separated by the same `---...---` rule used elsewhere in this file:

```
------------------------------------------------------------------------------

bundles/ga4/ is a sample OKF bundle copied verbatim from
okf/bundles/ga4 in the OKF reference implementation

    Open Knowledge Format (OKF)
    Copyright Google LLC
    https://github.com/GoogleCloudPlatform/knowledge-catalog

commit 3fcbb9f828c2f23d109c855ee403c3a4c81f3a96, licensed under the Apache
License, Version 2.0 (see LICENSE.Apache-2.0). It documents Google's public
Google Analytics 4 obfuscated sample ecommerce BigQuery dataset; see
bundles/ga4/README.md for details, including which upstream files were
intentionally not carried over.
```

- [ ] **Step 9: Verify and commit**

```bash
dotnet run --project src/OKF4net.Cli -- validate bundles/acme_retail
dotnet run --project src/OKF4net.Cli -- validate bundles/ga4
git add bundles/ .gitattributes NOTICE samples/acme-retail-agent/README.md
git status
```

Confirm `bundles/acme_retail` still validates exactly as before (9 concepts, 0 errors, 18 warnings, 0 info) and `bundles/ga4` validates clean (9 concepts, 0 errors, 0 warnings, 0 info), then commit:

```bash
git commit -m "$(cat <<'EOF'
feat(bundles): vendor bundles/ga4 as a second sample bundle

Copied verbatim from okf/bundles/ga4 at GoogleCloudPlatform/knowledge-catalog
commit 3fcbb9f828c2f23d109c855ee403c3a4c81f3a96 (Apache-2.0), minus viz.html
(same exclusion as bundles/acme_retail). bundles/README.md, which doubled as
acme_retail's own docs, is now a short index; that content moved to
bundles/acme_retail/README.md.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Scaffold `samples/catalog-explorer` and implement scenario 1 (load & inspect)

**Files:**
- Create: `samples/catalog-explorer/CatalogExplorer.sln`
- Create: `samples/catalog-explorer/src/CatalogExplorer/CatalogExplorer.csproj`
- Create: `samples/catalog-explorer/src/CatalogExplorer/Program.cs`
- Create: `samples/catalog-explorer/config/catalog.json`
- Modify: `.gitignore`

**Interfaces:**
- Produces: `static string? FindRepoRoot()` — repo root, or `null` if `OKF4net.sln` isn't found by walking up from `AppContext.BaseDirectory`. Used by every later task.
- Produces: `static void PrintHeader(string title)` — prints a blank line then `=== {title} ===`. Used by every later task.
- Produces: top-level locals `repoRoot` (`string`), `catalog` (`FileKnowledgeCatalog`, disposed via `using`) — later tasks build directly on top of this file's existing statements, inserting new scenarios between scenario 1's block and the final `return 0;`.

- [ ] **Step 1: Scaffold the project and solution**

From the repo root:

```bash
dotnet new sln -n CatalogExplorer -o samples/catalog-explorer -f sln
dotnet new console -n CatalogExplorer -o samples/catalog-explorer/src/CatalogExplorer --framework net10.0
dotnet sln samples/catalog-explorer/CatalogExplorer.sln add samples/catalog-explorer/src/CatalogExplorer/CatalogExplorer.csproj
```

- [ ] **Step 2: Replace the generated `.csproj`**

`dotnet new console` generates a `.csproj` with its own `<ImplicitUsings>`/`<Nullable>` lines and no `<RootNamespace>`/`<AssemblyName>`/`<ProjectReference>`. Overwrite `samples/catalog-explorer/src/CatalogExplorer/CatalogExplorer.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace>OKF4net.Samples.CatalogExplorer</RootNamespace>
    <AssemblyName>CatalogExplorer</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\..\..\src\OKF4net.Catalog\OKF4net.Catalog.csproj" />
  </ItemGroup>

</Project>
```

(`<ImplicitUsings>`/`<Nullable>`/`TreatWarningsAsErrors`/`LangVersion` are dropped because `Directory.Build.props` at the repo root already sets them — matching `samples/acme-retail-agent/src/AcmeRetailAgent/AcmeRetailAgent.csproj`'s shape.)

- [ ] **Step 3: Write `config/catalog.json`**

```json
{
  "version": 1,
  "sources": [
    { "id": "acme", "path": "../../../bundles/acme_retail", "role": "knowledge", "priority": 10 },
    { "id": "ga4-reference", "path": "../../../bundles/ga4", "role": "knowledge", "priority": 0 },
    { "id": "mem-session", "path": "../memory/session", "role": "memory", "tier": "session" },
    { "id": "mem-user", "path": "../memory/user", "role": "memory", "tier": "user" },
    { "id": "mem-tenant", "path": "../memory/tenant", "role": "memory", "tier": "tenant" }
  ]
}
```

Every `path` is relative to `config/` itself (`CatalogPathResolver.TryResolve` always resolves a source's `path` against the manifest file's own directory — never against `CatalogRoot`, which is only the containment boundary the resolved result is checked against): `../../../bundles/...` climbs `config/` → `CatalogExplorer's samples dir` → `src/` ... — concretely, from `samples/catalog-explorer/config/`, three `..` reach the repo root, then descend into `bundles/acme_retail` or `bundles/ga4`; one `..` from `config/` reaches `samples/catalog-explorer/`, then descends into a sibling `memory/` directory that doesn't exist yet (created on first write in Task 6).

- [ ] **Step 4: Ignore the memory scratch directory**

Add to `.gitignore` (anywhere; e.g. near the end):

```
# samples/catalog-explorer writes here at runtime and cleans up after
# itself, but an interrupted run could leave residue.
samples/catalog-explorer/memory/
```

- [ ] **Step 5: Write `Program.cs` (scenario 1 only)**

Overwrite `samples/catalog-explorer/src/CatalogExplorer/Program.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;

var repoRoot = FindRepoRoot()
    ?? throw new InvalidOperationException(
        "catalog-explorer: could not locate OKF4net.sln by walking up from the running assembly.");

var catalogFilePath = Path.GetFullPath(Path.Combine(repoRoot, "samples", "catalog-explorer", "config", "catalog.json"));
var options = new KnowledgeCatalogOptions
{
    CatalogFilePath = catalogFilePath,
    CatalogRoot = repoRoot,
    WatchForChanges = false,
};

using var catalog = new FileKnowledgeCatalog(options);

PrintHeader("1. Load & inspect");
foreach (var source in catalog.Current.Sources)
{
    Console.WriteLine($"  [{(source.Enabled ? "enabled " : "disabled")}] {source.Id,-14} role={source.Role,-9} priority={source.Priority,-3} path={source.Path}");
}

if (catalog.LastReloadDiagnostics.Count > 0)
{
    foreach (var d in catalog.LastReloadDiagnostics)
    {
        Console.WriteLine($"  diagnostic: [{d.Code}] {d.Message}");
    }
}

return 0;

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName;
}

static void PrintHeader(string title)
{
    Console.WriteLine();
    Console.WriteLine($"=== {title} ===");
}
```

- [ ] **Step 6: Build and run**

```bash
dotnet build samples/catalog-explorer/CatalogExplorer.sln
dotnet run --project samples/catalog-explorer/src/CatalogExplorer
```

Expected: builds with 0 warnings/errors; running prints a `=== 1. Load & inspect ===` header followed by exactly 5 lines, one per source (`acme`, `ga4-reference`, `mem-session`, `mem-user`, `mem-tenant`), every one marked `[enabled ]` — `acme` shows `priority=10`, the other four show `priority=0`; `acme`/`ga4-reference` show `role=Knowledge`, the three `mem-*` show `role=Memory`. No `diagnostic:` lines (no reload has happened yet).

- [ ] **Step 7: Commit**

```bash
git add samples/catalog-explorer .gitignore
git commit -m "$(cat <<'EOF'
feat(samples): scaffold catalog-explorer, scenario 1 (load & inspect)

Standalone console sample exercising OKF4net.Catalog directly (no DI, no
OKF4net.Catalog.Hosting) against bundles/acme_retail and bundles/ga4. Own
CatalogExplorer.sln, not part of OKF4net.sln or CI, same convention as
samples/acme-retail-agent.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Scenario 2 — multi-source search

**Files:**
- Modify: `samples/catalog-explorer/src/CatalogExplorer/Program.cs`

**Interfaces:**
- Consumes: `catalog` (`FileKnowledgeCatalog`, from Task 2), `PrintHeader(string)` (from Task 2).
- Produces: top-level locals `resolver` (`KnowledgeResolverRouter`) and `const string queryText = "revenue purchase"` — reused by every later scenario. Produces `static void PrintContext(KnowledgeContext context)` — reused by every later scenario.

- [ ] **Step 1: Insert scenario 2 and the `PrintContext` helper**

In `Program.cs`, insert this block between the existing `if (catalog.LastReloadDiagnostics.Count > 0) { ... }` block and `return 0;`:

```csharp
var resolver = new KnowledgeResolverRouter(catalog);
const string queryText = "revenue purchase";

PrintHeader("2. Multi-source search (default: GroupedBySource)");
PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText)));
```

And add `PrintContext` alongside the existing `static` local functions, after `PrintHeader`:

```csharp
static void PrintContext(KnowledgeContext context)
{
    foreach (var passage in context.Passages)
    {
        Console.WriteLine($"  [{passage.SourceId}] {passage.ConceptId} ({passage.Score}): {passage.Title}");
    }

    foreach (var diagnostic in context.Diagnostics)
    {
        Console.WriteLine($"  diagnostic: [{diagnostic.Code}] source={diagnostic.SourceId} {diagnostic.Message}");
    }

    if (context.Passages.Count == 0 && context.Diagnostics.Count == 0)
    {
        Console.WriteLine("  (no passages, no diagnostics)");
    }
}
```

(The query text `"revenue purchase"` splits into two independent substring terms — `ConceptSearch.Search` scores a concept if *either* term matches anywhere in title/tags/description/body, so `bundles/acme_retail/metrics/revenue.md`, whose title/tags/body are full of "revenue", and `bundles/ga4/references/metrics/purchasers.md`, whose title/tags/body are full of "purchase"/"purchasers", both score above zero on this one query.)

- [ ] **Step 2: Run and verify**

```bash
dotnet run --project samples/catalog-explorer/src/CatalogExplorer
```

Expected: a new `=== 2. Multi-source search (default: GroupedBySource) ===` section with at least one line starting `[acme]` and at least one line starting `[ga4-reference]` — confirming both sources contributed real results to the same query, grouped source-by-source (every `[acme]` line before every `[ga4-reference]` line, since `acme` has the higher `priority`). No `diagnostic:` lines.

- [ ] **Step 3: Commit**

```bash
git add samples/catalog-explorer/src/CatalogExplorer/Program.cs
git commit -m "$(cat <<'EOF'
feat(samples): catalog-explorer scenario 2, multi-source search

One query ("revenue purchase") through KnowledgeResolverRouter's default
GroupedBySource strategy, showing real contributions from both acme and
ga4-reference for the same query.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Scenario 3 — ranking strategies compared

**Files:**
- Modify: `samples/catalog-explorer/src/CatalogExplorer/Program.cs`

**Interfaces:**
- Consumes: `resolver`, `queryText`, `PrintHeader`, `PrintContext` (all from Task 3).

- [ ] **Step 1: Insert scenario 3**

Insert this block after scenario 2's `PrintContext(...)` call and before `return 0;`:

```csharp
PrintHeader("3. Ranking strategies compared");
foreach (var strategy in new[] { KnowledgeResolverStrategy.GroupedBySource, KnowledgeResolverStrategy.Merged, KnowledgeResolverStrategy.PriorityWeighted })
{
    Console.WriteLine($"-- {strategy} --");
    PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText) { ResolverStrategy = strategy }));
}
```

- [ ] **Step 2: Run and verify**

```bash
dotnet run --project samples/catalog-explorer/src/CatalogExplorer
```

Expected: a new `=== 3. Ranking strategies compared ===` section with three `-- Strategy --` subsections (`GroupedBySource`, `Merged`, `PriorityWeighted`), each listing passages from both `acme` and `ga4-reference`. The `GroupedBySource` subsection's passage order matches scenario 2's exactly (same strategy, same query). The `Merged` subsection interleaves passages from both sources by descending score rather than grouping by source. Confirm the three subsections are not byte-identical to each other (if they are, the strategies aren't actually differing — stop and investigate rather than proceeding).

- [ ] **Step 3: Commit**

```bash
git add samples/catalog-explorer/src/CatalogExplorer/Program.cs
git commit -m "$(cat <<'EOF'
feat(samples): catalog-explorer scenario 3, ranking strategies compared

Same query re-run through GroupedBySource/Merged/PriorityWeighted via one
KnowledgeResolverRouter instance (KnowledgeQuery.ResolverStrategy varies
per call), to show each strategy's effect on passage order.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Scenario 4 — visibility

**Files:**
- Modify: `samples/catalog-explorer/src/CatalogExplorer/Program.cs`

**Interfaces:**
- Consumes: `resolver`, `queryText`, `PrintHeader`, `PrintContext` (from Task 3/4).
- Produces: top-level local `employeeScope` (`KnowledgeAccessScope`) — reused by Task 6. Produces `static bool AcmeIfEmployee(KnowledgeAccessScope scope, KnowledgeCatalogSource source)`.

- [ ] **Step 1: Insert scenario 4**

Insert this block after scenario 3's loop and before `return 0;`:

```csharp
PrintHeader("4. Visibility");
var employeeScope = new KnowledgeAccessScope(userId: "acme-employee-jsmith");

Console.WriteLine("-- unscoped caller (no restriction) --");
PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText)));

Console.WriteLine("-- external-partner caller (PermittedSourceIds = { \"ga4-reference\" }) --");
PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText)
{
    PermittedSourceIds = new HashSet<string> { "ga4-reference" },
}));

Console.WriteLine("-- acme-employee caller (SourceVisibilityPolicy) --");
PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText)
{
    Scope = employeeScope,
    SourceVisibilityPolicy = AcmeIfEmployee,
}));

Console.WriteLine("-- non-employee caller (SourceVisibilityPolicy, fails closed) --");
PrintContext(await resolver.SearchAsync(new KnowledgeQuery(queryText)
{
    Scope = new KnowledgeAccessScope(userId: "external-bob"),
    SourceVisibilityPolicy = AcmeIfEmployee,
}));
```

And add `AcmeIfEmployee` alongside the other `static` local functions:

```csharp
static bool AcmeIfEmployee(KnowledgeAccessScope scope, KnowledgeCatalogSource source) =>
    source.Id == "ga4-reference"
    || (scope.UserId is { } userId && userId.StartsWith("acme-employee-", StringComparison.Ordinal) && source.Id == "acme");
```

(`ga4-reference` is always visible — it's the public reference source. `acme` is visible only when the caller's `UserId` starts with `"acme-employee-"`; any other `UserId`, including none, is denied — fail-closed, mirroring the pattern already documented in `OKF4net.Catalog`'s own README.)

- [ ] **Step 2: Run and verify**

```bash
dotnet run --project samples/catalog-explorer/src/CatalogExplorer
```

Expected: a new `=== 4. Visibility ===` section with four subsections. "unscoped caller" shows passages from both sources (identical to scenario 2's). "external-partner caller" shows only `[ga4-reference]` passages. "acme-employee caller" shows passages from both sources. "non-employee caller" shows only `[ga4-reference]` passages (same as the external-partner case — `acme` is denied because `"external-bob"` doesn't start with `"acme-employee-"`).

- [ ] **Step 3: Commit**

```bash
git add samples/catalog-explorer/src/CatalogExplorer/Program.cs
git commit -m "$(cat <<'EOF'
feat(samples): catalog-explorer scenario 4, visibility

Same query run as an unscoped caller, an external-partner caller
restricted to the public ga4-reference source via PermittedSourceIds, and
two SourceVisibilityPolicy callers (acme employee vs. not) demonstrating
the fail-closed pattern for the proprietary acme source.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: Scenario 5 — memory tier

**Files:**
- Modify: `samples/catalog-explorer/src/CatalogExplorer/Program.cs`

**Interfaces:**
- Consumes: `catalog`, `employeeScope` (from Task 5), `PrintHeader`.

- [ ] **Step 1: Insert scenario 5**

Insert this block after scenario 4's four subsections and before `return 0;`:

```csharp
PrintHeader("5. Memory tier");
var tierRoots = ResolveMemoryTierRoots(catalog);
var memoryStore = new FileMemoryStore(tierRoots);

var memoryEntry = new MemoryEntry(
    ConceptName: "onboarding-note",
    FrontmatterYamlIfCreating: "type: Note\ntitle: Onboarding notes\ndescription: A demo memory entry written by the catalog-explorer sample.\n",
    SectionMarkdown: "- Reminded Alice about the GA4 purchasers reference during onboarding.");

var writeResult = await memoryStore.WriteAsync(employeeScope, memoryEntry, MemoryTier.User);
Console.WriteLine($"  write: written={writeResult.Written} error={writeResult.Error}");

var readResult = await memoryStore.ReadAsync(employeeScope, new KnowledgeQuery("onboarding"));
foreach (var passage in readResult.Passages)
{
    Console.WriteLine($"  read: [{passage.SourceId}] {passage.ConceptId} ({passage.Score}): {passage.Excerpt}");
}

var deleteResult = await memoryStore.DeleteScopeAsync(employeeScope);
Console.WriteLine($"  cleanup: tiersDeleted={deleteResult.TiersDeleted} error={deleteResult.Error}");
```

And add `ResolveMemoryTierRoots` alongside the other `static` local functions:

```csharp
static Dictionary<MemoryTier, string> ResolveMemoryTierRoots(IKnowledgeCatalog catalog)
{
    var snapshot = catalog.Current;
    var tierRoots = new Dictionary<MemoryTier, string>();

    foreach (var source in snapshot.Sources)
    {
        if (source.Enabled
            && source.Role == SourceRole.Memory
            && source.Tier is { } tier
            && CatalogPathResolver.TryResolve(catalog.CatalogRoot, snapshot.ManifestDirectory, source.Path, out var resolved, out _))
        {
            tierRoots[tier] = resolved!;
        }
    }

    return tierRoots;
}
```

This mirrors, by hand, the same steps `OKF4net.Catalog.Hosting.AddMemory()` performs (filter the catalog's `role: memory` sources, resolve each via `CatalogPathResolver.TryResolve`, build a `Dictionary<MemoryTier, string>`) — so the manifest's `mem-session`/`mem-user`/`mem-tenant` entries are what actually back the store, not a hardcoded path dictionary.

`employeeScope` (from Task 5) has `UserId` set but no `TenantId`/`SessionId`, so only the `User` tier applies to it (`FileMemoryStore.IsApplicable`: the `Session`/`Tenant` tiers require their own id to be non-null) — the write/read/delete calls above only ever touch `samples/catalog-explorer/memory/user/`.

- [ ] **Step 2: Run and verify**

```bash
dotnet run --project samples/catalog-explorer/src/CatalogExplorer
```

Expected: a new `=== 5. Memory tier ===` section: `write: written=True error=`, one `read:` line whose `[SourceId]` is `memory:User` and whose excerpt contains "onboarding", and `cleanup: tiersDeleted=1 error=`.

- [ ] **Step 3: Verify the run is repeatable and leaves no residue**

```bash
dotnet run --project samples/catalog-explorer/src/CatalogExplorer
git status
```

Run it a second time back-to-back; the output must be identical both times (in particular, scenario 5's write must succeed both times — a leftover file from a failed cleanup would make the second run's write target an already-existing concept, which is still handled by `AppendToConceptAtomic`, but should not be the case here). `git status` must show no untracked files under `samples/catalog-explorer/memory/` after either run (deleted by `DeleteScopeAsync`, and covered by the `.gitignore` entry from Task 2 as a backstop).

- [ ] **Step 4: Commit**

```bash
git add samples/catalog-explorer/src/CatalogExplorer/Program.cs
git commit -m "$(cat <<'EOF'
feat(samples): catalog-explorer scenario 5, memory tier

Resolves the manifest's role:memory sources into a FileMemoryStore by
hand (mirroring OKF4net.Catalog.Hosting.AddMemory()'s own algorithm),
writes one entry, reads it back, then deletes the scope so the run stays
repeatable.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: Sample README and final verification

**Files:**
- Create: `samples/catalog-explorer/README.md`

**Interfaces:** None.

- [ ] **Step 1: Write the sample's README**

```markdown
# Catalog Explorer sample

A standalone console app walking through [`OKF4net.Catalog`](../../src/OKF4net.Catalog/README.md)
end to end: multi-source search, ranking-strategy comparison, per-caller
source visibility, and the `role: memory` tier — against
[`bundles/acme_retail`](../../bundles/acme_retail/README.md) and
[`bundles/ga4`](../../bundles/ga4/README.md). Uses `OKF4net.Catalog`
directly, with no dependency injection and no `OKF4net.Catalog.Hosting` —
see [the design spec](../../docs/superpowers/specs/2026-07-31-catalog-explorer-sample-design.md)
for the full rationale, including why these two bundles (not two unrelated
ones) and why DI is out of scope here.

Standalone: this project has its own `CatalogExplorer.sln`, is not part of
`OKF4net.sln`, and is not built or tested by this repo's CI.

## Run

```bash
dotnet run --project samples/catalog-explorer/src/CatalogExplorer
```

No environment variables, no API key, no network access — every scenario
runs against the two bundles already vendored in this repo.

## What it does

Five scenarios, printed in sequence:

1. **Load & inspect** — constructs `FileKnowledgeCatalog` over
   `config/catalog.json` and prints its five sources.
2. **Multi-source search** — one query (`"revenue purchase"`) through
   `KnowledgeResolverRouter`, default `GroupedBySource` strategy, showing
   real contributions from both `acme` (Acme's own proprietary metrics)
   and `ga4-reference` (Google's public GA4 reference docs).
3. **Ranking strategies compared** — the same query re-run under
   `Merged` and `PriorityWeighted`, to show each strategy's effect on
   passage order.
4. **Visibility** — the same query as an unscoped caller, an
   external-partner caller restricted to the public `ga4-reference`
   source via `PermittedSourceIds`, and an Acme-employee caller granted
   both sources via a fail-closed `SourceVisibilityPolicy`.
5. **Memory tier** — resolves the manifest's `role: memory` sources into
   a `FileMemoryStore` by hand (the same steps
   `OKF4net.Catalog.Hosting.AddMemory()` performs), writes one memory
   entry, reads it back, then deletes it so the run leaves no residue.
```

- [ ] **Step 2: Full solo build/run verification**

```bash
dotnet build samples/catalog-explorer/CatalogExplorer.sln
dotnet run --project samples/catalog-explorer/src/CatalogExplorer
dotnet format samples/catalog-explorer/CatalogExplorer.sln --verify-no-changes
```

Expected: clean build (0 warnings/errors), the full 5-scenario walkthrough printed top to bottom matching every task's expected output above, and `dotnet format` reports no changes needed.

- [ ] **Step 3: Confirm the main solution and CI-relevant checks are unaffected**

```bash
dotnet build OKF4net.sln
dotnet test OKF4net.sln
dotnet format OKF4net.sln --verify-no-changes
```

Expected: all three pass exactly as before this plan (`samples/catalog-explorer` is not part of `OKF4net.sln`, so none of this should even touch it) — this just confirms Task 1's `bundles/` restructuring didn't break anything in the main solution.

- [ ] **Step 4: Commit**

```bash
git add samples/catalog-explorer/README.md
git commit -m "$(cat <<'EOF'
docs(samples): add catalog-explorer README

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```
