# Producer Code-Graph — Gate Phase Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clear the two blockers that stand between the producer code-graph design and an implementable plan — make a 480-concept bundle survive search ranking, and settle whether Roslyn can be fed without `MSBuildWorkspace`.

**Architecture:** Two independent gates. The first extends the single shared scorer with a diversified top-N selection and wires it into the two consumers that truncate results, so generated code concepts stop crowding out curated ones; the search-scale benchmark becomes a permanent acceptance test. The second is a committed prototype that feeds a real `CSharpCompilation` from complete MSBuild inputs and reports, with evidence, whether the `ResolveReferences` route works.

**Tech Stack:** C# / net10.0, xunit; `Microsoft.CodeAnalysis.CSharp` in the prototype only.

**Spec:** `docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md` — read §7.2 and §8.7 before starting. The spec's own status block says it is not plan-ready; **this plan is what makes it plan-ready**, and nothing downstream of these gates should be built until both are green.

## Global Constraints

- **Zero third-party runtime dependencies** in `src/OKF4net`, `src/OKF4net.Cli`, `src/OKF4net.Catalog`, `src/OKF4net.Attestation`. `src/OKF4net.Agents` may reference only `Microsoft.Agents.AI` and `OKF4net.Attestation`. Task 1 and Task 2 add **no** package anywhere.
- **`ConceptSearch` is the single shared scorer** used by `OKF4net.Agents` and `OKF4net.Catalog`. Extend it; never fork a second scorer in a consumer (CLAUDE.md, Architecture).
- **Warnings are errors** (`Directory.Build.props`). `dotnet build OKF4net.sln` must be clean.
- **Every new source file starts with** `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- **Never edit `tests/fixtures/` to make a failing test pass.** This plan does not touch fixtures at all; if a golden moves, that is a regression to investigate.
- **`producers/` stays outside `OKF4net.sln` and outside CI** — decision of 2026-08-01.
- Verification commands: `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln`, `dotnet format OKF4net.sln --verify-no-changes`.

---

## Why this plan stops where it does

The spec describes seven sections of design, and only two of them are blocked. But the blocked ones sit **underneath** the rest:

- **§7.2 is unproven.** The claim "`dotnet msbuild -t:ResolveReferences -getItem:ReferencePath` is enough" was an inference. The spike never used those references — it fed the compilation from `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")` (`spike-roslyn/Program.cs:74`). Its ~419 compilation errors therefore prove nothing either way. Writing tasks for a `RoslynResolver` before knowing whether the route works would be specifying an implementation against an unknown interface.
- **§8.7 fails, measured.** On a representative 395-concept corpus, curated concepts take **1 of 55** top-5 slots and 5 of 11 queries return none in the top 20. Building the generator before fixing this would ship a bundle that makes the product worse.

Everything downstream — extraction, id scheme, concept emission, containment links, determinism, transactional pruning, `--check`, packaging — sits behind `ISymbolResolver` and is genuinely independent of both gates. It gets its own plan **after** Task 3 returns a verdict, because Task 3 decides one interface and one project reference.

---

## File Structure

**Created:**
- `src/OKF4net/ConceptSearch.cs` — extended, not created (see below).
- `tests/OKF4net.Tests/SearchScaleTests.cs` — the acceptance benchmark, corpus generator included.
- `producers/spikes/RoslynCompilationSpike/` — the committed prototype (own project, outside `OKF4net.sln`).
- `producers/spikes/README.md` — what the spikes are, and the rule that they are committed.

**Modified:**
- `src/OKF4net/ConceptSearch.cs` — add `TopDiversified`.
- `src/OKF4net.Agents/OkfBundleTools.cs:1129-1131` — use it in `okf_search`.
- `src/OKF4net.Agents/OkfContextProvider.cs:255-258` — use it when picking concepts to inject.
- `docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md` — §8.7 and §7.2 record the outcomes.

---

### Task 1: Diversified top-N in the shared scorer

**Files:**
- Modify: `src/OKF4net/ConceptSearch.cs`
- Test: `tests/OKF4net.Tests/ConceptSearchTests.cs` (extend — the file exists, and its `MakeConcept` helper is at line 16)

**Interfaces:**
- Consumes: `ScoredConcept(Concept Concept, int Score)` and `ConceptSearch.Search(...)`, both already public.
- Produces:
  - `static IReadOnlyList<ScoredConcept> ConceptSearch.TopDiversified(IReadOnlyList<ScoredConcept> scored, int count)`

**The defect being fixed, precisely.** `ScoreConcept` awards presence, not frequency: 3 for the title, 2 for tags+description, 1 for the body, capped at 6 per term (`ConceptSearch.cs:94-118`). Ties are therefore massive. `Search` breaks them with `.ThenBy(x => x.Concept.Id)` (`ConceptSearch.cs:50`), and `ConceptId.CompareTo` compares segments with `string.CompareOrdinal` (`ConceptId.cs:317`), so `code` < `docs` < `overview` < `packages` — **every** code concept precedes **every** curated one at equal score. With 470 code concepts and a 20-result cap, curated concepts become unreachable.

`TopDiversified` keeps the score ordering strictly (a lower-scoring concept never overtakes a higher-scoring one) and only changes **which** of the equally-scored concepts get the scarce slots: within one score band, it round-robins over the first id segment, in the band's own ordinal order of first appearance.

- [ ] **Step 1: Write the failing tests**

Append these tests **inside the existing `ConceptSearchTests` class** in `tests/OKF4net.Tests/ConceptSearchTests.cs` — that class already has the `MakeConcept(string id, string? title, string[]? tags, string? description, string body)` helper this needs (line 16), and reusing it beats introducing a second concept factory.

```csharp
    /// <summary>
    /// Builds a scored band without touching the filesystem: ids only, all at
    /// the same score, in the order <see cref="ConceptSearch.Search"/> would
    /// return them (ordinal by id).
    /// </summary>
    private static IReadOnlyList<ScoredConcept> Band(int score, params string[] ids) =>
        [.. ids.Select(id => new ScoredConcept(MakeConcept(id), score))];

    [Fact]
    public void Within_one_score_band_slots_rotate_across_top_level_segments()
    {
        var scored = Band(6,
            "code/csharp/a", "code/csharp/b", "code/csharp/c", "code/csharp/d",
            "docs/readme",
            "overview",
            "packages/okf4net");

        var top = ConceptSearch.TopDiversified(scored, 4);

        Assert.Equal(
            ["code/csharp/a", "docs/readme", "overview", "packages/okf4net"],
            top.Select(s => s.Concept.Id.ToString()));
    }

    [Fact]
    public void A_lower_score_never_overtakes_a_higher_one()
    {
        var scored = new List<ScoredConcept>();
        scored.AddRange(Band(6, "code/csharp/a", "code/csharp/b"));
        scored.AddRange(Band(3, "docs/readme"));

        var top = ConceptSearch.TopDiversified(scored, 2);

        Assert.Equal(["code/csharp/a", "code/csharp/b"], top.Select(s => s.Concept.Id.ToString()));
    }

    [Fact]
    public void Fewer_results_than_requested_are_returned_whole()
    {
        var scored = Band(6, "code/csharp/a", "docs/readme");

        Assert.Equal(2, ConceptSearch.TopDiversified(scored, 20).Count);
    }

    [Fact]
    public void A_single_segment_band_degrades_to_plain_score_order()
    {
        var scored = Band(6, "code/csharp/a", "code/csharp/b", "code/csharp/c");

        var top = ConceptSearch.TopDiversified(scored, 2);

        Assert.Equal(["code/csharp/a", "code/csharp/b"], top.Select(s => s.Concept.Id.ToString()));
    }

    [Fact]
    public void Selection_is_deterministic_across_runs()
    {
        var scored = Band(6, "packages/x", "code/a", "docs/y", "code/b", "overview");

        var first = ConceptSearch.TopDiversified(scored, 3).Select(s => s.Concept.Id.ToString()).ToList();
        var second = ConceptSearch.TopDiversified(scored, 3).Select(s => s.Concept.Id.ToString()).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void An_empty_input_yields_an_empty_result()
        => Assert.Empty(ConceptSearch.TopDiversified([], 5));

    [Fact]
    public void A_non_positive_count_yields_an_empty_result()
        => Assert.Empty(ConceptSearch.TopDiversified(Band(6, "code/a"), 0));
```

Close the class with the brace that is already there — these methods go **before** the existing closing `}` of `ConceptSearchTests`, not after it.

> The existing class documents itself as exercising `ConceptSearch` "directly against in-memory `Concept` values, without a bundle on disk", and calls out "tie ordering by ascending `ConceptId`" as covered behaviour. Read that XML doc comment (lines 4-14) and extend it to mention diversification, so the class summary does not go stale.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ConceptSearchDiversityTests"`
Expected: compile error — `ConceptSearch.TopDiversified` does not exist.

- [ ] **Step 3: Implement `TopDiversified`**

Append to `src/OKF4net/ConceptSearch.cs`, inside the `ConceptSearch` class:

```csharp
    /// <summary>
    /// Picks the top <paramref name="count"/> of an already-scored list while
    /// spreading scarce slots across top-level id segments.
    /// </summary>
    /// <remarks>
    /// Score order is absolute: a lower-scoring concept never overtakes a
    /// higher-scoring one. Diversification applies only *within* a score band,
    /// which is where it is needed — <see cref="Search"/> breaks ties by id, and
    /// ordinal id order makes one whole family (e.g. every <c>code/…</c>
    /// concept) precede every other at equal score. On a large generated corpus
    /// that starves the curated concepts of every slot; measured on a
    /// 395-concept bundle, they took 1 of 55 top-5 slots without this.
    /// Within a band, segments are visited round-robin in order of first
    /// appearance, so the result is fully deterministic.
    /// </remarks>
    /// <param name="scored">Results from <see cref="Search"/>, in descending score order.</param>
    /// <param name="count">Maximum number of results to return.</param>
    public static IReadOnlyList<ScoredConcept> TopDiversified(IReadOnlyList<ScoredConcept> scored, int count)
    {
        if (count <= 0 || scored.Count == 0)
        {
            return [];
        }

        var picked = new List<ScoredConcept>(Math.Min(count, scored.Count));

        for (var i = 0; i < scored.Count && picked.Count < count;)
        {
            // Collect one score band: [i, j).
            var band = scored[i].Score;
            var j = i;
            while (j < scored.Count && scored[j].Score == band)
            {
                j++;
            }

            // Group the band by top-level id segment, preserving the order in
            // which each segment first appears (the band is already ordinal by
            // id, so this order is deterministic).
            var queues = new List<Queue<ScoredConcept>>();
            var bySegment = new Dictionary<string, Queue<ScoredConcept>>(StringComparer.Ordinal);
            for (var k = i; k < j; k++)
            {
                var segment = scored[k].Concept.Id.Segments[0];
                if (!bySegment.TryGetValue(segment, out var queue))
                {
                    queue = new Queue<ScoredConcept>();
                    bySegment[segment] = queue;
                    queues.Add(queue);
                }

                queue.Enqueue(scored[k]);
            }

            // Round-robin across segments until the band is drained or the
            // budget is spent.
            var drained = false;
            while (!drained && picked.Count < count)
            {
                drained = true;
                foreach (var queue in queues)
                {
                    if (queue.Count == 0)
                    {
                        continue;
                    }

                    drained = false;
                    picked.Add(queue.Dequeue());
                    if (picked.Count == count)
                    {
                        break;
                    }
                }
            }

            i = j;
        }

        return picked;
    }
```

> Check that `ConceptId.Segments` is public and non-empty for every id before relying on `Segments[0]` — run `grep -n "Segments" src/OKF4net/ConceptId.cs`. A single-segment id such as `overview` must yield `"overview"`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ConceptSearchDiversityTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/ConceptSearch.cs tests/OKF4net.Tests/ConceptSearchTests.cs
git commit -m "feat(search): add TopDiversified so one id family cannot take every slot"
```

---

### Task 2: Wire diversification into the two truncating consumers, with the scale benchmark as its gate

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs:1129-1131`
- Modify: `src/OKF4net.Agents/OkfContextProvider.cs:255-258`
- Create: `tests/OKF4net.Tests/SearchScaleTests.cs`

**Interfaces:**
- Consumes: `ConceptSearch.TopDiversified` from Task 1.
- Produces: no new public API — behaviour change only.

**The two truncation points, verified:** `OkfBundleTools` caps at `const int MaxResults = 20` (`:1129`, and a second identical constant at `:1187` for the audit renderer — leave that one alone), and `OkfContextProvider` takes `_options.MaxConceptsInjected`, default **5** (`OkfContextProviderOptions.cs:44`), after reserving a quarter of a 2000-token budget for the root block.

- [ ] **Step 1: Write the failing acceptance test**

Create `tests/OKF4net.Tests/SearchScaleTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;

namespace OKF4net.Tests;

/// <summary>
/// The acceptance gate for §8.7 of the producer code-graph design: a bundle
/// dominated by generated code concepts must not starve the curated ones.
/// Measured before the fix on a 395-concept corpus: curated concepts took
/// 1 of 55 top-5 slots, and 5 of 11 broad queries returned none in the top 20.
/// </summary>
public class SearchScaleTests
{
    private const int CodeConceptCount = 380;

    private static readonly string[] Queries =
        ["validation", "bundle", "concept", "yaml", "catalog", "search", "index"];

    /// <summary>
    /// Writes a bundle shaped like a real producer output: a handful of curated
    /// concepts, and a large generated `code/` subtree whose titles collide with
    /// the curated vocabulary on purpose — that collision is what produces the
    /// score ties the ordering has to survive.
    /// </summary>
    private static TempDir BuildCorpus()
    {
        var tmp = new TempDir();

        tmp.Write("overview.md",
            "---\ntype: Repository\ntitle: okf\ndescription: OKF4net implements the Open Knowledge Format, with bundle loading, validation, indexing and search.\n---\nRepository overview.\n");
        tmp.Write("packages/okf4net.md",
            "---\ntype: Package\ntitle: OKF4net\ndescription: The core library: bundle loading, validation, yaml parsing and concept search.\n---\nCore package.\n");
        tmp.Write("packages/okf4net-catalog.md",
            "---\ntype: Package\ntitle: OKF4net.Catalog\ndescription: Knowledge catalog model: sources, resolvers and the memory store.\n---\nCatalog package.\n");
        tmp.Write("docs/readme.md",
            "---\ntype: Documentation\ntitle: README\ndescription: How to install the okf CLI, run bundle validation and search a bundle.\n---\nReadme.\n");

        var names = new[] { "Validate", "Bundle", "Concept", "Yaml", "Catalog", "Search", "Index", "Graph" };
        for (var i = 0; i < CodeConceptCount; i++)
        {
            var name = names[i % names.Length];
            tmp.Write($"code/csharp/okf4net/type{i:D3}/{name.ToLowerInvariant()}.md",
                $"---\ntype: C# Member\ntitle: Type{i:D3}.{name}\ndescription: Member {name} on Type{i:D3}.\ntags: [csharp, method, public]\n---\n## Signatures\n\n- `public void {name}()`\n");
        }

        return tmp;
    }

    private static bool IsCurated(ScoredConcept s) =>
        !s.Concept.Id.ToString().StartsWith("code/", StringComparison.Ordinal);

    [Fact]
    public void Curated_concepts_reach_the_agent_injection_window_on_every_broad_query()
    {
        using var tmp = BuildCorpus();
        var bundle = Bundle.Load(tmp.Path);

        foreach (var query in Queries)
        {
            var top5 = ConceptSearch.TopDiversified(ConceptSearch.Search(bundle.Concepts, query), 5);

            Assert.True(
                top5.Any(IsCurated),
                $"query {query}: no curated concept in the top 5 — the agent would be injected only generated code.");
        }
    }

    [Fact]
    public void Curated_concepts_are_well_represented_in_the_search_window()
    {
        using var tmp = BuildCorpus();
        var bundle = Bundle.Load(tmp.Path);

        foreach (var query in Queries)
        {
            var scored = ConceptSearch.Search(bundle.Concepts, query);
            var curatedAvailable = scored.Count(IsCurated);
            if (curatedAvailable == 0)
            {
                continue;   // the query genuinely matches no curated concept
            }

            var top20 = ConceptSearch.TopDiversified(scored, 20);

            Assert.True(
                top20.Count(IsCurated) >= Math.Min(curatedAvailable, 2),
                $"query {query}: only {top20.Count(IsCurated)} curated concepts in the top 20 of {scored.Count} hits.");
        }
    }

    [Fact]
    public void The_undiversified_ordering_still_fails_the_same_corpus()
    {
        // Locks the defect itself, so a future change that quietly drops
        // diversification is caught rather than silently regressing.
        using var tmp = BuildCorpus();
        var bundle = Bundle.Load(tmp.Path);

        var plainTop5 = ConceptSearch.Search(bundle.Concepts, "bundle").Take(5);

        Assert.DoesNotContain(plainTop5, IsCurated);
    }
}
```

> `TempDir` is the real helper (`tests/OKF4net.Tests/TempDir.cs`): it is `IDisposable`, its root is exposed as `Path` (not `Root`), and `Write(relative, content)` creates parent directories and writes UTF-8 without a BOM.

- [ ] **Step 2: Run the tests to verify the first two fail and the third passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~SearchScaleTests"`
Expected: `Curated_concepts_reach_the_agent_injection_window_on_every_broad_query` FAILS (that is the defect); `The_undiversified_ordering_still_fails_the_same_corpus` PASSES.

If the first test passes already, **stop and investigate** — the corpus is not reproducing the tie pattern, and the rest of this task would be built on a test that proves nothing.

- [ ] **Step 3: Use the diversified selection in `okf_search`**

In `src/OKF4net.Agents/OkfBundleTools.cs`, at the search renderer (around line 1129), replace:

```csharp
        var shown = scored.Take(MaxResults).ToList();
```

with:

```csharp
        // Diversified rather than a plain Take: at equal score, ordinal id order
        // puts every `code/…` concept ahead of every curated one, so a generated
        // bundle would fill all 20 slots with members (design §8.7).
        var shown = ConceptSearch.TopDiversified(scored, MaxResults).ToList();
```

- [ ] **Step 4: Use it when injecting context**

In `src/OKF4net.Agents/OkfContextProvider.cs`, at the concept-selection site (around lines 255-258), replace the `.Take(_options.MaxConceptsInjected)` on the scored results with `ConceptSearch.TopDiversified(scored, _options.MaxConceptsInjected)`.

Read the surrounding expression first — it is part of a larger LINQ chain that also renders each concept into a budget. Keep every other element of that chain intact; only the truncation changes.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test OKF4net.sln`
Expected: PASS, including `SearchScaleTests`, and **no golden file moves**. If a golden under `tests/fixtures/golden/` differs, stop: the sample bundles are small enough that diversification should not reorder them, so a diff means the implementation is reordering across score bands, which Task 1's second test was meant to forbid.

- [ ] **Step 6: Record the outcome in the spec**

In `docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md`, §8.7, replace the three-option list with the decision taken and the post-fix measurement, and update the status block at the top to mark this gate cleared.

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net.Agents tests/OKF4net.Tests/SearchScaleTests.cs docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md
git commit -m "fix(agents): diversify search and context selection so generated concepts cannot starve curated ones"
```

---

### Task 3: The Roslyn compilation prototype — ✅ **DONE 2026-08-31**

> **Executed. Verdict: the route works — zero errors on all three probed projects.**
> Spec §7.2 rewritten with the measurement and the exact MSBuild command, so it reads without the prototype.
>
> The prototype itself was **removed from the working tree afterwards** — it is a spike, and a spike's deliverable is the answer. It stays recoverable in history at commit `0db6e9a` (`git checkout 0db6e9a -- producers/spikes`), which is what the earlier tree-sitter spike lacked: that one was never committed at all, so nobody could re-run it and two of its wrong conclusions survived into the design.
>
> Three corrections came out of it, and the second is the one that changes the downstream design:
> 1. `-t:ResolveReferences` alone omits generated sources; `-t:GenerateGlobalUsings -t:GenerateAssemblyInfo` are required under `ImplicitUsings`.
> 2. **The repo must be built, not merely restored** — measured: dropping `bin/` references takes `OKF4net.Mcp` from 0 to 4 errors. This makes the `CompilationReference` route mandatory rather than preferred.
> 3. `Microsoft.CodeAnalysis.CSharp` 4.14.0 does not know `LangVersion 14` and silently falls back to `Preview`.
>
> The steps below are kept as the record of what was run.

**Files:**
- Create: `producers/spikes/RoslynCompilationSpike/RoslynCompilationSpike.csproj`
- Create: `producers/spikes/RoslynCompilationSpike/Program.cs`
- Create: `producers/spikes/README.md`
- Modify: `docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md` §7.2 and the status block

**Interfaces:**
- Consumes: nothing from Tasks 1-2 — this gate is independent and may run in parallel.
- Produces: a **verdict**, not an API. The next plan's `RoslynResolver` task is written against whichever route this task validates.

**Why this exists.** §7.2 asserted that `dotnet msbuild -t:ResolveReferences -getItem:ReferencePath` suffices. The measurement behind it is real (213 references in 1.9 s) but it measures an MSBuild command, not a compilation. The existing spike never used those references: `spike-roslyn/Program.cs:74` reads `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")`. Its error count is therefore evidence of nothing. **This task is not a re-run; it is the experiment that was never done.**

**And it must be committed.** The current spike lives in `.claude/worktrees/spike-treesitter-dotnet/` as three untracked directories — `git ls-tree 79ade6a` contains none of it — which is exactly why an independent reviewer could not reproduce any of its numbers. Spikes that back a design decision get committed.

- [ ] **Step 1: Create the spike project**

`producers/spikes/RoslynCompilationSpike/RoslynCompilationSpike.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- A spike, not shipped code: it is exempt from the repo's warnings-as-errors. -->
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.14.0" />
  </ItemGroup>
</Project>
```

> Check the current `Microsoft.CodeAnalysis.CSharp` version on nuget.org before pinning; use the latest stable that supports the SDK 10 language version. Do **not** add `Microsoft.CodeAnalysis.Workspaces.MSBuild` — establishing whether it is needed is the point of the experiment.

- [ ] **Step 2: Write the prototype**

`producers/spikes/RoslynCompilationSpike/Program.cs` must, for a target `.csproj` given on the command line:

1. Extract, in **one** `dotnet msbuild` invocation, the complete input set — not just references:
   `-t:ResolveReferences -getItem:ReferencePath -getItem:Compile -getProperty:DefineConstants -getProperty:LangVersion -getProperty:Nullable -getProperty:AllowUnsafeBlocks -getProperty:TargetFramework`
2. Build a `CSharpCompilation` from those inputs: parse every `Compile` item with a `CSharpParseOptions` carrying the real `LangVersion` and `DefineConstants`, add every `ReferencePath` as a `MetadataReference`, and set `CSharpCompilationOptions` from `Nullable` and `AllowUnsafeBlocks`.
3. Print: the number of `Compile` items, the number of references, and **the diagnostic count at `DiagnosticSeverity.Error`**, followed by the first 20 errors with their ids and locations.

Nothing in this program reads `AppContext.GetData`. If you find yourself reaching for it, the experiment has been inverted.

- [ ] **Step 3: Run it against three targets of increasing difficulty**

```bash
dotnet run --project producers/spikes/RoslynCompilationSpike -- src/OKF4net/OKF4net.csproj
dotnet run --project producers/spikes/RoslynCompilationSpike -- src/OKF4net.Mcp/OKF4net.Mcp.csproj
dotnet run --project producers/spikes/RoslynCompilationSpike -- src/OKF4net.Agents/OKF4net.Agents.csproj
```

The three are chosen deliberately: `OKF4net` is self-contained, `OKF4net.Mcp` has `PackageReference` **and** a transitive `ProjectReference` chain, `OKF4net.Agents` pulls `Microsoft.Agents.AI`. Record the error count for each.

**The bar is zero errors.** Not "few", not "only harmless ones" — a compilation with errors has an incomplete symbol table, and a resolver built on it silently mis-attributes calls, which §2.1 already establishes is worse than not resolving at all.

- [ ] **Step 4: If errors remain, find the missing input — do not lower the bar**

Common culprits, in the order worth checking: generated sources (source generators write to `obj/`, and their output is in the `Compile` item set only after a build), `Directory.Build.props` contributions, implicit global usings (`ImplicitUsings` generates a file under `obj/`), and multi-TFM projects where the property query returned the wrong TFM.

If after this the route still cannot reach zero, that is a **legitimate negative result** — record it and evaluate `MSBuildWorkspace` with a properly published BuildHost as the alternative, per §7.2's note that roslyn#80127 is conditional rather than absolute.

- [ ] **Step 5: Write the spikes README**

`producers/spikes/README.md` states: what each spike answers, that spikes backing a design decision are **committed** (with the counter-example of the untracked tree-sitter spike whose numbers nobody could reproduce), and that they are outside `OKF4net.sln` and outside CI like the rest of `producers/`.

- [ ] **Step 6: Record the verdict in the spec**

Rewrite §7.2 of the design with the measured outcome: the error counts for the three targets, the complete input set that was needed, the decision between `ReferencePath` and `MSBuildWorkspace`, and the resulting `PackageReference` set for `OkfProducer.CodeGraph.Roslyn`. Remove the "⚠ Section non validée" banner only if the bar was met, and update the status block at the top of the spec.

- [ ] **Step 7: Commit**

```bash
git add producers/spikes docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md
git commit -m "spike(roslyn): test whether ResolveReferences alone yields a clean compilation"
```

---

## What comes next

When both gates are green, the design is plan-ready and the implementation plan for the generator itself gets written. Its task sequence, in dependency order, is already fully determined by the spec and none of it depends on anything but Task 3's verdict:

1. `OkfProducer.Core` contracts and data types — `ILanguageExtractor`, `ISymbolResolver`, `ICodeGraphBuilder`, `SymbolFact`, `CallSite`, `CodeGraph` (§2.1-2.2)
2. Id scheme with the unified four-family registry, reserved segments and the ordinal tie-break (§3)
3. `OkfProducer.CodeGraph.TreeSitter` — the single extractor, the C# `LanguageProfile`, UTF-8 offset normalization (§2.1, §3.1)
4. Hostile-input policy and per-file status; partial runs (§2.3)
5. `OkfProducer.CodeGraph.Roslyn` — `RoslynResolver`, shaped by Task 3's verdict (§7.2)
6. Concept emission: frontmatter, the `IDescriptionSource` chain, `description_source` field preservation (§4)
7. Namespace concepts and one-level containment links (§5)
8. Determinism rules and the HEAD-commit instant (§6.1-6.2)
9. Transactional staging, generation manifest and pruning (§6.3)
10. `--check` against a copy of the existing bundle (§6.2)
11. CLI surface (§9)
12. RID-specific tool packaging (§7.1)
13. Golden fixture bundle and blast-radius tests (§8.2-8.3)

If Task 3 returns a negative result, item 5 changes shape and item 12's weight changes with it — which is precisely why that plan is not written yet.
