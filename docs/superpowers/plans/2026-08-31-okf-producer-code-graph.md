# Producer Code-Graph — Generator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `producers/OkfProducer` an extraction stage that turns a repository's C# source into linked OKF concepts — namespaces, types and members, with a call graph — so `okf graph` on a generated bundle shows a graph instead of nothing.

**Architecture:** A fourth pipeline stage parallel to the scanner. `ICodeGraphBuilder` combines one tree-sitter `ILanguageExtractor` (parameterised by a `LanguageProfile`) with a chain of `ISymbolResolver`s (name matching for every language, Roslyn for C#). Its `CodeGraph` feeds `ConceptGenerator`, which emits §6 markdown links — containment one level down, calls across — so no OKF4net change is needed. Writes are transactional: a staging directory, a generation manifest, and pruning only after a fully successful run.

**Tech Stack:** C# / net10.0, `TreeSitter.DotNet`, `Microsoft.CodeAnalysis.CSharp`, xunit.

**Spec:** `docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md`. Read it before Task 1 — this plan argues from it and cites its sections throughout. Both of its gates are cleared: §7.2 by `producers/spikes/RoslynCompilationSpike/`, §8.7 by `ConceptSearch.TopDiversified`.

## Global Constraints

- **`producers/` is exempt from the zero-dependency rule** and lives outside `OKF4net.sln` and outside CI (decision of 2026-08-01). It builds via `producers/OkfProducer.sln`. **`OkfProducer.Core` still references only `OKF4net`** — the heavy dependencies live in the two `CodeGraph.*` projects (§2.2).
- **No change to `src/OKF4net`.** Edges are §6 markdown links; nothing is added to the OKF schema. If a task seems to need a library change, stop and re-read §11 before proceeding.
- **Every new source file starts with** `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- **File-scoped namespaces, XML doc comments on public API, nullable enabled** — inherited from the root `Directory.Build.props`, which `producers/Directory.Build.props` imports.
- **Never edit `tests/fixtures/`.** This plan's golden lives under `producers/tests/OkfProducer.Tests/fixtures/` and follows the *opposite* discipline (§8.2) — read Task 12 before creating it.
- **Determinism rules apply to every task that writes output** (§6.2): sort `Ordinal` everywhere, never iterate a `Dictionary`/`HashSet` into output, normalize path separators to `/`, no absolute paths, no culture-dependent casing.
- **Call-site identity is a UTF-8 byte offset**, never `(file, line, column)` — tree-sitter columns are byte counts, Roslyn positions are UTF-16 (§2.1).
- Verification: `dotnet build producers/OkfProducer.sln`, `dotnet test producers/OkfProducer.sln`, `dotnet format producers/OkfProducer.sln --verify-no-changes`.
- **Test helpers are yours to write.** Every task's test code calls small local factories and fixtures — `Member(...)`, `Site(...)`, `Generate(...)`, `Single(...)`, `ExtractSource(...)`, `RunCheck(...)` and the like. They are deliberately not spelled out: they are one-liners over the types the task's **Interfaces → Produces** block defines, and writing them is how you check that block is coherent. Two rules: put them at the bottom of the test file that uses them, and **never invent a production API to make one work** — if a helper needs something the plan does not define, that is a defect in the plan, so stop and say so rather than widening the surface.

### Three corrections the Roslyn prototype forced, which Task 6 must honour

Measured 2026-08-31, `producers/spikes/RoslynCompilationSpike/`:

1. **The MSBuild query needs more than `ResolveReferences`.** Generated sources are absent from `Compile` unless `GenerateGlobalUsings` and `GenerateAssemblyInfo` also run, and `ImplicitUsings` is on by default.
2. **The repo must be BUILT, not merely restored.** `ProjectReference`s resolve to `bin/<config>/<tfm>/*.dll`. With those absent, `OKF4net.Mcp` goes from 0 to 4 errors. This makes the `CompilationReference` route mandatory, not preferred.
3. **The Roslyn package must know the SDK's language version.** 4.14.0 does not recognise `LangVersion 14`; falling back to `Preview` silently changes parse semantics. Fail loudly instead.

---

## File Structure

**New project — `producers/src/OkfProducer.Core/CodeGraph/`** (references `OKF4net` only):

- `SymbolFact.cs` — one extracted declaration: kind, name, container, visibility, span, doc comment.
- `CallSite.cs` — one call: the enclosing symbol, the called name, the UTF-8 offset.
- `CodeGraph.cs` — the assembled `SymbolFact`/resolved-edge collections plus per-file status.
- `ILanguageExtractor.cs`, `LanguageProfile.cs` — the extraction seam.
- `ISymbolResolver.cs`, `ResolvedEdge.cs` — the resolution seam.
- `CodeGraphBuilder.cs` — orchestration; pure, no I/O beyond what the extractor does.
- `ExtractionLimits.cs`, `FileStatus.cs`, `RunStatus.cs` — the hostile-input policy (§2.3).

**New project — `producers/src/OkfProducer.CodeGraph.TreeSitter/`** (+ `TreeSitter.DotNet`):
- `TreeSitterExtractor.cs`, `Profiles/CSharpProfile.cs`, `Utf8Offsets.cs`.

**New project — `producers/src/OkfProducer.CodeGraph.Roslyn/`** (+ `Microsoft.CodeAnalysis.CSharp`):
- `MsBuildProjectQuery.cs`, `CompilationFactory.cs`, `RoslynResolver.cs`.

**Modified in `producers/src/OkfProducer.Core/`:**
- `Generation/ConceptGenerator.cs` — the unified id registry, code concepts, containment links.
- `Generation/CodeConceptIds.cs` *(new)* — the §3 id scheme.
- `Generation/IDescriptionSource.cs`, `DocCommentSource.cs`, `SignatureSource.cs` *(new)* — §4.2.
- `Generation/IBundleWriter.cs`, `BundleWriter.cs` — staging, manifest, owned-prefix pruning.
- `Generation/GenerationManifest.cs` *(new)* — §6.3.
- `Validation/IBundleValidationRunner.cs`, `BundleValidationRunner.cs` — thread `IOkfClock` (§8.1).

**Tests:** one file per unit under `producers/tests/OkfProducer.Tests/CodeGraph/` and `Generation/`, plus `producers/tests/OkfProducer.Tests/fixtures/` for the golden repo and bundle.

---

### Task 1: Core contracts and data types

**Files:**
- Create: `producers/src/OkfProducer.Core/CodeGraph/SymbolFact.cs`, `CallSite.cs`, `CodeGraph.cs`, `ExtractionResult.cs`, `FileStatus.cs`, `RunStatus.cs`, `ExtractionLimits.cs`, `ILanguageExtractor.cs`, `LanguageProfile.cs`, `ISymbolResolver.cs`, `ResolvedEdge.cs`, `CodeGraphBuilder.cs`
- Test: `producers/tests/OkfProducer.Tests/CodeGraph/CodeGraphBuilderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces — every later task depends on these exact shapes:
  - `enum SymbolKind { Namespace, Type, Member }`
  - `enum SymbolVisibility { Public, Internal, Private }`
  - `record SymbolFact(SymbolKind Kind, string Language, string Container, string Name, string Signature, SymbolVisibility Visibility, string RelativePath, int StartOffset, int EndOffset, int StartLine, int EndLine, string? DocComment)`
  - `record CallSite(string CallerContainer, string CallerName, string CalledName, string RelativePath, int Offset)`
  - `record ResolvedEdge(CallSite Site, string? TargetContainer, string? TargetName, EdgeConfidence Confidence)` with `enum EdgeConfidence { Unresolved, ByName, Exact }`
  - `record CodeGraph(IReadOnlyList<SymbolFact> Symbols, IReadOnlyList<ResolvedEdge> Edges, RunStatus Status)`
  - `record ExtractionResult(IReadOnlyList<SymbolFact> Symbols, IReadOnlyList<CallSite> Sites, FileStatus Status)`
  - `enum FileStatus { Extracted, PartiallyExtracted, SkippedTooLarge, SkippedEncoding, SkippedDepth, SkippedUnreadable, SkippedSymlink }`
  - `record RunStatus(bool IsComplete, IReadOnlyList<(string Path, FileStatus Status)> Skipped)` with `static RunStatus Complete { get; }` = `new(true, [])`
  - `record ExtractionLimits(long MaxFileBytes, int MaxDepth, TimeSpan Timeout)` with `static ExtractionLimits Default` = 2 MB, depth 512, 10 minutes
  - `record LanguageProfile(string Language, string GrammarName, string DeclarationQuery, string CallQuery, string DocCommentPrefix)` with two behaviours the later tasks call: `IReadOnlyList<string> SplitContainer(string container)` (`.` for C#/Java namespaces, `/` for a TS/JS module path) and `SymbolVisibility VisibilityOf(string modifiers)`
  - `interface ILanguageExtractor { ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile); }`

> **These five carrier types are defined here, in Task 1, even though only Task 4 gives `FileStatus` and `ExtractionLimits` their behaviour, and only Task 3 fills a real `LanguageProfile`.** Task 1's own tests construct `ExtractionResult` and `CodeGraph`, so deferring the types would leave this task unable to compile — and an executor working Task 1 in isolation, which is the point, would be stuck. Task 4 adds the *policy* that produces these statuses; it does not create the types.
  - `interface ISymbolResolver { bool Owns(string relativePath); IReadOnlyList<ResolvedEdge> Resolve(IReadOnlyList<CallSite> sites, IReadOnlyList<SymbolFact> symbols); }`
  - `sealed class CodeGraphBuilder(ILanguageExtractor extractor, IReadOnlyList<ISymbolResolver> resolvers)` with `CodeGraph Build(RepositorySnapshot snapshot, ExtractionLimits limits)`

> **Why offsets and not line/column.** `SymbolFact` and `CallSite` carry a UTF-8 byte offset because that is the only identity both engines can agree on: tree-sitter's `Point.column` counts bytes, Roslyn positions count UTF-16 units, and they diverge at the first non-ASCII character on a line (§2.1). Line numbers are carried too, but only for display.

- [ ] **Step 1: Write the failing test for resolver chaining**

`producers/tests/OkfProducer.Tests/CodeGraph/CodeGraphBuilderTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.Tests.CodeGraph;

public class CodeGraphBuilderTests
{
    private static SymbolFact Member(string container, string name, string path = "A.cs") =>
        new(SymbolKind.Member, "csharp", container, name, $"public void {name}()",
            SymbolVisibility.Public, path, 0, 10, 1, 1, null);

    private sealed class StubExtractor(params SymbolFact[] symbols) : ILanguageExtractor
    {
        public IReadOnlyList<CallSite> Sites { get; init; } = [];

        public ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile) =>
            new([.. symbols.Where(s => s.RelativePath == relativePath)],
                [.. Sites.Where(s => s.RelativePath == relativePath)],
                FileStatus.Extracted);
    }

    private sealed class StubResolver(string owned, EdgeConfidence confidence) : ISymbolResolver
    {
        public bool Owns(string relativePath) => relativePath == owned;

        public IReadOnlyList<ResolvedEdge> Resolve(IReadOnlyList<CallSite> sites, IReadOnlyList<SymbolFact> symbols) =>
            [.. sites.Select(s => new ResolvedEdge(s, "T", s.CalledName, confidence))];
    }

    [Fact]
    public void A_later_resolver_overrides_an_earlier_verdict_for_files_it_owns()
    {
        // §2.1: resolvers are chained, not exclusive. NameMatch gives a baseline
        // for every language; Roslyn overrides it for the files it owns, at
        // identity of call site.
        var site = new CallSite("T", "Caller", "Callee", "A.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller")) { Sites = [site] },
            [new StubResolver("A.cs", EdgeConfidence.ByName), new StubResolver("A.cs", EdgeConfidence.Exact)]);

        var graph = builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default);

        var edge = Assert.Single(graph.Edges);
        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
    }

    [Fact]
    public void A_resolver_that_does_not_own_a_file_leaves_the_earlier_verdict_alone()
    {
        var site = new CallSite("T", "Caller", "Callee", "A.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller")) { Sites = [site] },
            [new StubResolver("A.cs", EdgeConfidence.ByName), new StubResolver("Other.cs", EdgeConfidence.Exact)]);

        var graph = builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default);

        Assert.Equal(EdgeConfidence.ByName, Assert.Single(graph.Edges).Confidence);
    }

    [Fact]
    public void With_no_resolver_at_all_the_shape_of_the_output_is_unchanged()
    {
        // The property the two-seam design exists to guarantee: a missing
        // resolver degrades precision, never the shape.
        var site = new CallSite("T", "Caller", "Callee", "A.cs", 42);
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "Caller")) { Sites = [site] }, []);

        var graph = builder.Build(SnapshotWith("A.cs"), ExtractionLimits.Default);

        Assert.Equal(EdgeConfidence.Unresolved, Assert.Single(graph.Edges).Confidence);
        Assert.Single(graph.Symbols);
    }

    [Fact]
    public void Symbols_and_edges_come_out_in_a_deterministic_order()
    {
        var builder = new CodeGraphBuilder(
            new StubExtractor(Member("T", "b", "B.cs"), Member("T", "a", "A.cs")), []);

        var graph = builder.Build(SnapshotWith("B.cs", "A.cs"), ExtractionLimits.Default);

        Assert.Equal(["a", "b"], graph.Symbols.Select(s => s.Name));
    }
}
```

> `SnapshotWith(params string[] relativePaths)` is a helper you write in this file; it builds the `RepositorySnapshot` the existing `RepositoryScanner` produces. Read `producers/src/OkfProducer.Core/Scanning/RepositorySnapshot.cs` first and match its real constructor — do not guess it.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test producers/OkfProducer.sln --filter "FullyQualifiedName~CodeGraphBuilderTests"`
Expected: compile errors — none of the types exist yet.

- [ ] **Step 3: Write the contracts**

Create each file with the records, enums and interfaces listed under **Produces** above. Two of them carry a doc comment worth writing now, because a later task depends on reading it correctly:

```csharp
// ExtractionResult.cs
/// <summary>What one file yielded: its declarations, its call sites, and how the extraction went.</summary>
public sealed record ExtractionResult(
    IReadOnlyList<SymbolFact> Symbols,
    IReadOnlyList<CallSite> Sites,
    FileStatus Status);

// RunStatus.cs
/// <summary>
/// Whether a whole extraction run succeeded, and what it could not read.
/// "Absent from this run" has two indistinguishable causes — the symbol is
/// gone, or the file could not be read — so Task 11's pruning keys off this
/// type. SHIPPED SHAPE (ruling R21, see the note under Task 4): the gate is
/// TraversalComplete, a separate stored fact, and IsComplete is derived from
/// it plus the per-file statuses. Gating on IsComplete would be dead code.
/// </summary>
public sealed record RunStatus(bool IsComplete, IReadOnlyList<(string Path, FileStatus Status)> Skipped)
{
    /// <summary>A run in which every eligible file extracted cleanly.</summary>
    public static RunStatus Complete { get; } = new(true, []);
}
```

`CodeGraphBuilder.Build` does exactly this, and nothing more:

1. Extract every eligible file, collecting `ExtractionResult`s and per-file `FileStatus`.
2. Concatenate all symbols; sort `Ordinal` by `(Container, Name, RelativePath)`.
3. Seed every call site as `EdgeConfidence.Unresolved`.
4. For each resolver in order, replace the verdict of any site whose `RelativePath` that resolver `Owns`, matching on `(RelativePath, Offset)`.
5. Sort edges `Ordinal` by `(CallerContainer, CallerName, CalledName)`.
6. Return the graph with an aggregate `RunStatus`: `RunStatus.Complete` when every `ExtractionResult.Status` is `FileStatus.Extracted`, otherwise a `RunStatus` listing each non-`Extracted` file. Task 4 is what makes the extractor ever report anything but `Extracted`; the aggregation belongs here.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test producers/OkfProducer.sln --filter "FullyQualifiedName~CodeGraphBuilderTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add producers/src/OkfProducer.Core/CodeGraph producers/tests/OkfProducer.Tests/CodeGraph
git commit -m "feat(producer): add the code-graph contracts and builder"
```

---

### Task 2: The concept id scheme

**Files:**
- Create: `producers/src/OkfProducer.Core/Generation/CodeConceptIds.cs`
- Test: `producers/tests/OkfProducer.Tests/Generation/CodeConceptIdsTests.cs`

**Interfaces:**
- Consumes: `SymbolFact` from Task 1.
- Produces:
  - `sealed class ConceptIdRegistry` — one registry across all four families, with `ConceptId Register(string prefix, string naturalName)` applying the reserved-segment and collision rules
  - `static string CodeConceptIds.For(SymbolFact fact, LanguageProfile profile)` — the `code/<language>/<container…>/<name>` path

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Generation;

namespace OkfProducer.Tests.Generation;

public class CodeConceptIdsTests
{
    [Fact]
    public void A_type_and_its_member_nest_under_the_namespace_path()
    {
        Assert.Equal("code/csharp/okf4net/link-scanner",
            CodeConceptIds.For(Type("OKF4net", "LinkScanner"), CSharp));
        Assert.Equal("code/csharp/okf4net/link-scanner/scan",
            CodeConceptIds.For(Member("OKF4net", "LinkScanner", "Scan"), CSharp));
    }

    [Fact]
    public void A_nested_namespace_becomes_nested_segments()
        => Assert.Equal("code/csharp/okf4net/yaml/yaml-value",
            CodeConceptIds.For(Type("OKF4net.Yaml", "YamlValue"), CSharp));

    [Fact]
    public void Overloads_collapse_to_one_id()
    {
        // §3.2: one concept per (container, name). A numeric suffix would be
        // order-dependent, so adding an overload would renumber its neighbours
        // and churn concepts that did not change.
        var a = CodeConceptIds.For(Member("N", "T", "Validate", "public void Validate()"), CSharp);
        var b = CodeConceptIds.For(Member("N", "T", "Validate", "public void Validate(int x)"), CSharp);

        Assert.Equal(a, b);
    }

    [Fact]
    public void A_member_named_index_is_not_allowed_to_shadow_a_reserved_file()
    {
        // BundleConceptWriter rejects `index` and `log`; a property named Index
        // is perfectly plausible.
        var registry = new ConceptIdRegistry();

        var id = registry.Register("code/csharp/n/t", "Index");

        Assert.NotEqual("code/csharp/n/t/index", id.ToString());
    }

    [Fact]
    public void A_case_only_collision_is_broken_by_ordinal_order_of_the_original_name()
    {
        // §3.3: ordinal on the NAME, not on (file, line), so the tie-break
        // survives a file move or a line shift.
        var registry = new ConceptIdRegistry();

        var first = registry.Register("code/go/pkg", "Parse");
        var second = registry.Register("code/go/pkg", "parse");

        Assert.Equal("code/go/pkg/parse", first.ToString());
        Assert.Equal("code/go/pkg/parse-2", second.ToString());
    }

    [Fact]
    public void The_registry_sees_collisions_across_families()
    {
        // §3.4: usedIds must span overview, packages/, docs/ and code/ — one
        // registry, not one per family.
        var registry = new ConceptIdRegistry();

        var a = registry.Register("packages", "my-lib");
        var b = registry.Register("packages", "my.lib");

        Assert.NotEqual(a.ToString(), b.ToString());
    }
}
```

> `Type(...)`, `Member(...)` and `CSharp` are helpers you write at the bottom of this file, building `SymbolFact` values and a minimal `LanguageProfile`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test producers/OkfProducer.sln --filter "FullyQualifiedName~CodeConceptIdsTests"`
Expected: compile errors.

- [ ] **Step 3: Implement**

`CodeConceptIds.For` slugifies each container segment and the name with `ConceptId.Slugify` (the OKF4net helper — do not write a second slugifier) and joins with `/`, prefixed `code/<language>/`. The container split is `profile.SplitContainer(fact.Container)`, so a language profile decides whether that is a namespace, a module path or a package path (§3.1).

`ConceptIdRegistry` holds one `HashSet<string>` (Ordinal) across every family. `Register` slugifies, **reuses `IsReservedSegment` from `ConceptGenerator`** rather than writing a second copy, and appends `-2`, `-3`, … on collision. Registration order is the caller's responsibility: callers must register in `Ordinal` order of the original name so the tie-break is stable (§3.3).

- [ ] **Step 4: Run to verify PASS** — `dotnet test producers/OkfProducer.sln --filter "FullyQualifiedName~CodeConceptIdsTests"`

- [ ] **Step 5: Commit**

```bash
git add producers/src/OkfProducer.Core/Generation/CodeConceptIds.cs producers/tests/OkfProducer.Tests/Generation/CodeConceptIdsTests.cs
git commit -m "feat(producer): add the code concept id scheme and a cross-family registry"
```

---

### Task 3: The tree-sitter extractor and the C# profile

**Files:**
- Create: `producers/src/OkfProducer.CodeGraph.TreeSitter/` (project + `TreeSitterExtractor.cs`, `Utf8Offsets.cs`, `Profiles/CSharpProfile.cs`)
- Test: `producers/tests/OkfProducer.Tests/CodeGraph/TreeSitterExtractorTests.cs`

**Interfaces:**
- Consumes: `ILanguageExtractor`, `SymbolFact`, `CallSite`, `LanguageProfile` from Task 1.
- Produces: `sealed class TreeSitterExtractor : ILanguageExtractor`; `static class Utf8Offsets` with `int ToUtf16(string text, int utf8Offset)` and `int ToUtf8(string text, int utf16Offset)`.

- [ ] **Step 1: Write the failing tests, offsets first**

```csharp
    [Fact]
    public void Offsets_survive_a_non_ascii_identifier_before_the_call()
    {
        // §2.1, the bug class this whole offset discipline exists to prevent:
        // tree-sitter counts bytes, Roslyn counts UTF-16. "café" is 5 bytes and
        // 4 chars, so every offset after it differs by one.
        const string source = "var café = Foo();";

        var utf8 = source.IndexOf("Foo", StringComparison.Ordinal);   // UTF-16 index
        Assert.NotEqual(Utf8Offsets.ToUtf8(source, utf8), utf8);
        Assert.Equal(utf8, Utf8Offsets.ToUtf16(source, Utf8Offsets.ToUtf8(source, utf8)));
    }

    [Theory]
    [InlineData("var x = \"🎯\"; Foo();")]     // astral plane, surrogate pair
    [InlineData("var naïve = 1;\r\nFoo();")]   // CRLF
    [InlineData("// commentaire accentué\nFoo();")]
    public void Offset_conversion_round_trips(string source)
    {
        var utf16 = source.IndexOf("Foo", StringComparison.Ordinal);

        Assert.Equal(utf16, Utf8Offsets.ToUtf16(source, Utf8Offsets.ToUtf8(source, utf16)));
    }

    [Fact]
    public void Public_types_and_members_are_extracted_with_their_doc_comment()
    {
        var result = ExtractSource("""
            namespace N;
            /// <summary>Scans a body.</summary>
            public sealed class Scanner
            {
                /// <summary>Scans it.</summary>
                public int Scan(string body) => body.Length;
                private int Hidden() => 0;
            }
            """);

        var type = Assert.Single(result.Symbols, s => s.Kind == SymbolKind.Type);
        Assert.Equal("Scanner", type.Name);
        Assert.Equal("Scans a body.", type.DocComment);

        var member = Assert.Single(result.Symbols, s => s.Kind == SymbolKind.Member && s.Visibility == SymbolVisibility.Public);
        Assert.Equal("Scan", member.Name);
        Assert.Equal("Scans it.", member.DocComment);
    }

    [Fact]
    public void Private_members_are_extracted_but_marked_so_scope_can_filter_them()
        => Assert.Contains(
            ExtractSource("namespace N;\npublic class T { private int Hidden() => 0; }").Symbols,
            s => s.Name == "Hidden" && s.Visibility == SymbolVisibility.Private);

    [Fact]
    public void Local_functions_are_covered()
    {
        // The spike's remaining 1.2% attachment gap was local_function_statement.
        var result = ExtractSource("namespace N;\npublic class T { public void M() { void Inner() { } Inner(); } }");

        Assert.Contains(result.Sites, s => s.CalledName == "Inner");
    }

    [Fact]
    public void Call_sites_carry_the_enclosing_symbol()
    {
        var result = ExtractSource("namespace N;\npublic class T { public void M() { Other(); } }");

        var site = Assert.Single(result.Sites);
        Assert.Equal("T", site.CallerContainer);
        Assert.Equal("M", site.CallerName);
        Assert.Equal("Other", site.CalledName);
    }
```

- [ ] **Step 2: Run to verify failure** — the project does not exist yet.

- [ ] **Step 3: Create the project and implement**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="TreeSitter.DotNet" Version="1.3.0" />
    <ProjectReference Include="..\OkfProducer.Core\OkfProducer.Core.csproj" />
  </ItemGroup>
</Project>
```

`Utf8Offsets` converts by walking the string and accumulating `Encoding.UTF8.GetByteCount` per rune — correct across surrogate pairs, which a naive per-`char` loop is not.

`TreeSitterExtractor.Extract` parses with the profile's grammar, runs the profile's queries for declarations and call expressions, and converts every tree-sitter byte offset into the `SymbolFact`/`CallSite` UTF-8 offset directly (no conversion needed — tree-sitter is already UTF-8; the conversion is what Roslyn needs in Task 6).

`CSharpProfile` supplies: the grammar, the declaration query (including `local_function_statement`), the call query, the doc-comment shape (`///` with a `<summary>` element), the container split (`.` on namespaces), and the visibility rule.

- [ ] **Step 4: Run to verify PASS**

- [ ] **Step 5: Commit**

```bash
git add producers/src/OkfProducer.CodeGraph.TreeSitter producers/tests/OkfProducer.Tests/CodeGraph/TreeSitterExtractorTests.cs
git commit -m "feat(producer): extract C# symbols and call sites with tree-sitter"
```

---

### Task 4: Scope rule, hostile-input policy and run status

**Files:**
- Create: `producers/src/OkfProducer.Core/CodeGraph/FileEligibility.cs`
- Modify: `producers/src/OkfProducer.Core/CodeGraph/CodeGraphBuilder.cs`, `producers/src/OkfProducer.CodeGraph.TreeSitter/TreeSitterExtractor.cs`
- Test: `producers/tests/OkfProducer.Tests/CodeGraph/HostileInputTests.cs`, `ScopeTests.cs`

**Interfaces:**
- Consumes: `ExtractionLimits`, `FileStatus`, `RunStatus` — **defined in Task 1**; this task gives them their behaviour.
- Produces:
  - `record ScopeOptions(bool IncludeTests, bool IncludeInternal)` with `static ScopeOptions Default` = both false
  - `static bool FileEligibility.IsEligible(string relativePath, RepositorySnapshot snapshot, ScopeOptions scope)`
  - `static bool FileEligibility.IsInScope(SymbolFact fact, ScopeOptions scope)`

> **This task is what makes pruning safe.** §6.3's transactional rule keys off `RunStatus`; without an honest per-file status, a parse failure is indistinguishable from a deleted symbol and Task 11 would delete valid concepts.
>
> **Corrected while implementing Task 11 (ruling R21):** the gate is `RunStatus.TraversalComplete`, **not** `IsComplete`. `IsComplete` additionally requires every file to have parsed cleanly, and the vendored grammar mis-parses an empty collection expression `[]` — ordinary modern C#, this repository's own idiom — so gating on it makes pruning dead code. Per-file quality is applied one candidate at a time instead: only ids owned entirely by `FileStatus.Extracted` files may be deleted.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Theory]
    [InlineData(FileStatus.SkippedTooLarge)]
    [InlineData(FileStatus.SkippedEncoding)]
    [InlineData(FileStatus.SkippedUnreadable)]
    public void Any_skipped_file_makes_the_whole_run_incomplete(FileStatus status)
    {
        var graph = BuildWith(("A.cs", status));

        Assert.False(graph.Status.IsComplete);
        Assert.Contains(graph.Status.Skipped, s => s.Path == "A.cs" && s.Status == status);
    }

    [Fact]
    public void A_file_over_the_size_cap_is_skipped_whole_never_truncated()
    {
        // Truncating would yield spans that point at the wrong code — worse
        // than not extracting the file at all.
        using var tmp = new TempDir();
        var path = tmp.Write("big.cs", new string('x', 3 * 1024 * 1024));

        var result = Extract(path, ExtractionLimits.Default with { MaxFileBytes = 2 * 1024 * 1024 });

        Assert.Equal(FileStatus.SkippedTooLarge, result.Status);
        Assert.Empty(result.Symbols);
    }

    [Fact]
    public void Invalid_utf8_is_skipped_not_replaced_with_substitution_characters()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "bad.cs");
        File.WriteAllBytes(path, [0x6E, 0x73, 0xFF, 0xFE, 0x00, 0x41]);

        Assert.Equal(FileStatus.SkippedEncoding, Extract(path, ExtractionLimits.Default).Status);
    }

    [Fact]
    public void A_tree_with_error_nodes_keeps_what_parsed_and_reports_partial()
    {
        var result = ExtractSource("namespace N;\npublic class T { public void M() { @@@ } public void N2() { } }");

        Assert.Equal(FileStatus.PartiallyExtracted, result.Status);
        Assert.Contains(result.Symbols, s => s.Name == "N2");
    }

    [Fact]
    public void A_run_where_every_file_extracted_is_complete()
        => Assert.True(BuildWith(("A.cs", FileStatus.Extracted)).Status.IsComplete);
```

And the scope rule (§5.4), in `ScopeTests.cs` — without it the bundle triples in size for no gain:

```csharp
    [Theory]
    [InlineData("bin/Debug/net10.0/Gen.cs")]
    [InlineData("obj/Debug/net10.0/Gen.cs")]
    [InlineData("node_modules/pkg/index.cs")]
    [InlineData(".git/hooks/x.cs")]
    public void Build_output_and_vendored_directories_are_never_scanned(string path)
        => Assert.False(FileEligibility.IsEligible(path, Snapshot, ScopeOptions.Default));

    [Fact]
    public void A_project_referencing_a_test_SDK_is_excluded_by_default()
    {
        // §5.4: on this repo that removes ~900 methods of OKF4net.Tests. Scoping
        // on `src/` instead would hard-code a convention that is only ours.
        Assert.False(FileEligibility.IsEligible("tests/OKF4net.Tests/AuditTests.cs", SnapshotWithTestProject, ScopeOptions.Default));
        Assert.True(FileEligibility.IsEligible("tests/OKF4net.Tests/AuditTests.cs", SnapshotWithTestProject, ScopeOptions.Default with { IncludeTests = true }));
    }

    [Theory]
    [InlineData("test")]
    [InlineData("tests")]
    [InlineData("spec")]
    public void A_conventionally_named_directory_is_excluded_even_without_a_test_SDK(string dir)
        => Assert.False(FileEligibility.IsEligible($"{dir}/Thing.cs", Snapshot, ScopeOptions.Default));

    [Fact]
    public void Visibility_and_not_a_path_prefix_does_the_filtering()
    {
        Assert.False(FileEligibility.IsInScope(Member("T", "Hidden", SymbolVisibility.Private), ScopeOptions.Default));
        Assert.False(FileEligibility.IsInScope(Member("T", "Internal", SymbolVisibility.Internal), ScopeOptions.Default));
        Assert.True(FileEligibility.IsInScope(Member("T", "Internal", SymbolVisibility.Internal), ScopeOptions.Default with { IncludeInternal = true }));
        Assert.True(FileEligibility.IsInScope(Member("T", "Public", SymbolVisibility.Public), ScopeOptions.Default));
    }
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

**The scope rule first**, since it decides which files the rest even sees. `FileEligibility.IsEligible` rejects `bin`, `obj`, `node_modules` and `.git` outright; rejects a file whose owning project references a test SDK (`Microsoft.NET.Test.Sdk`, visible in the `RepositorySnapshot`'s `.csproj` data) unless `IncludeTests`; and rejects a `test`/`tests`/`spec` directory by convention. `IsInScope` filters symbols by visibility, not by path — that is the §5.4 rule, and hard-coding `src/` instead would bake in a convention that is only this repo's.

**Then the hostile-input policy.** Read files as bytes and decode with `new UTF8Encoding(false, throwOnInvalidBytes: true)` inside a `try` — a `DecoderFallbackException` is `SkippedEncoding`. Check length before reading. Reject reparse points using `OKF4net`'s internal detection through the seam `OKF4net.Catalog` already uses, or `FileSystemInfo.LinkTarget` if that seam is not reachable from `producers/`. Thread a `CancellationToken` from the timeout through `CodeGraphBuilder.Build`.

- [ ] **Step 4: Run to verify PASS.**

- [ ] **Step 5: Commit**

```bash
git add producers/src/OkfProducer.Core/CodeGraph producers/src/OkfProducer.CodeGraph.TreeSitter producers/tests/OkfProducer.Tests/CodeGraph/HostileInputTests.cs
git commit -m "feat(producer): bound hostile input and report per-file extraction status"
```

---

### Task 5: `NameMatchResolver`

**Files:**
- Create: `producers/src/OkfProducer.Core/CodeGraph/NameMatchResolver.cs`
- Test: `producers/tests/OkfProducer.Tests/CodeGraph/NameMatchResolverTests.cs`

**Interfaces:**
- Produces: `sealed class NameMatchResolver : ISymbolResolver` — `Owns` returns true for every path (it is the baseline for every language).

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void A_unique_name_resolves_ByName()
    {
        var edges = Resolve(sites: [Site("Caller", "Scan")], symbols: [Member("Scanner", "Scan")]);

        var edge = Assert.Single(edges);
        Assert.Equal(EdgeConfidence.ByName, edge.Confidence);
        Assert.Equal("Scanner", edge.TargetContainer);
    }

    [Fact]
    public void An_ambiguous_name_stays_Unresolved_rather_than_guessing()
    {
        // The spike measured 38-39% of internal edges as inter-type ambiguous
        // (`Equals` across 7 types). Picking one would be a silent wrong answer;
        // §4.5 puts these in `## Calls (unresolved)` as text instead.
        var edges = Resolve(
            sites: [Site("Caller", "Equals")],
            symbols: [Member("A", "Equals"), Member("B", "Equals")]);

        Assert.Equal(EdgeConfidence.Unresolved, Assert.Single(edges).Confidence);
    }

    [Fact]
    public void A_name_with_no_declaration_in_the_repo_stays_Unresolved()
        => Assert.Equal(EdgeConfidence.Unresolved,
            Assert.Single(Resolve([Site("Caller", "Substring")], [Member("T", "Scan")])).Confidence);
```

- [ ] **Step 2-4:** run red, implement (group symbols by name into a `Dictionary<string, List<SymbolFact>>`, resolve only where the list has exactly one entry), run green.

- [ ] **Step 5: Commit**

```bash
git add producers/src/OkfProducer.Core/CodeGraph/NameMatchResolver.cs producers/tests/OkfProducer.Tests/CodeGraph/NameMatchResolverTests.cs
git commit -m "feat(producer): add the language-agnostic name-match resolver"
```

---

### Task 6: `RoslynResolver`

**Files:**
- Create: `producers/src/OkfProducer.CodeGraph.Roslyn/` (project + `MsBuildProjectQuery.cs`, `CompilationFactory.cs`, `RoslynResolver.cs`)
- Test: `producers/tests/OkfProducer.Tests/CodeGraph/RoslynResolverTests.cs`

**Interfaces:**
- Produces: `sealed class RoslynResolver : ISymbolResolver`; `record ProjectInputs(IReadOnlyList<string> CompileFiles, IReadOnlyList<string> References, string DefineConstants, string LangVersion, bool Nullable, bool AllowUnsafe, string OutputType)`; `static ProjectInputs MsBuildProjectQuery.Query(string projectPath)`.

> **Start by recovering the prototype:** `git checkout 0db6e9a -- producers/spikes`. It is throwaway code, deliberately not kept in the working tree, but it is the working answer for this task — it reached zero errors on three real projects. This task productionises it; it does not re-derive it. Delete the recovered directory again once Task 6 is done.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void The_msbuild_query_returns_generated_sources_too()
    {
        // Correction 1 from the spike: -t:ResolveReferences alone omits
        // GlobalUsings.g.cs and AssemblyInfo.cs, and ImplicitUsings is on by
        // default, so every file relying on an implicit using then fails.
        var inputs = MsBuildProjectQuery.Query(RepoProject("src/OKF4net/OKF4net.csproj"));

        Assert.Contains(inputs.CompileFiles, f => f.EndsWith("GlobalUsings.g.cs", StringComparison.Ordinal));
        Assert.True(inputs.References.Count > 100);
    }

    [Fact]
    public void An_unknown_language_version_fails_loudly_instead_of_degrading()
    {
        // Correction 3: falling back to Preview silently changes parse
        // semantics. The producer must not do that.
        var inputs = SomeInputs with { LangVersion = "99" };

        var ex = Assert.Throws<InvalidOperationException>(() => CompilationFactory.Create(inputs));
        Assert.Contains("LangVersion", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_inter_type_ambiguity_that_NameMatch_cannot_settle_resolves_Exact()
    {
        // The 38-39% the spike measured: the ambiguity is inter-type, and the
        // container is exactly what Roslyn settles.
        var edges = ResolveWithRoslyn("""
            namespace N;
            public class A { public bool Same(object o) => true; }
            public class B { public bool Same(object o) => false; }
            public class C { public void Go(A a) { a.Same(null); } }
            """);

        var edge = Assert.Single(edges, e => e.Site.CalledName == "Same");
        Assert.Equal(EdgeConfidence.Exact, edge.Confidence);
        Assert.Equal("A", edge.TargetContainer);
    }

    [Fact]
    public void Attachment_to_tree_sitter_sites_holds_above_the_measured_floor()
    {
        // §8.4: assert the RATE with a floor, not one lucky call. A grammar or
        // Roslyn upgrade that degrades this must fail loudly rather than
        // silently move calls into `## Calls (unresolved)`.
        var (attached, total) = MeasureAttachment(RepoProject("src/OKF4net/OKF4net.csproj"));

        Assert.True(attached / (double)total >= 0.98, $"attachment fell to {attached}/{total}");
    }

    [Fact]
    public void Attachment_survives_a_non_ascii_line_before_the_call()
    {
        // The unit mismatch, end to end: tree-sitter gives a byte offset,
        // Roslyn a UTF-16 position. A wrong attachment is worse than none.
        var edges = ResolveWithRoslyn("""
            namespace N;
            public class T
            {
                public void Callee() { }
                public void M() { var café = 1; Callee(); }
            }
            """);

        Assert.Equal(EdgeConfidence.Exact, Assert.Single(edges, e => e.Site.CalledName == "Callee").Confidence);
    }
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Port the prototype**

`MsBuildProjectQuery` runs the exact command the spike validated:

```
dotnet msbuild <proj> -t:ResolveReferences -t:GenerateGlobalUsings -t:GenerateAssemblyInfo
  -getItem:ReferencePath -getItem:Compile
  -getProperty:DefineConstants -getProperty:LangVersion -getProperty:Nullable
  -getProperty:AllowUnsafeBlocks -getProperty:TargetFramework -getProperty:OutputType
```

`CompilationFactory.Create` builds the `CSharpCompilation` from those inputs and **throws** when `LanguageVersionFacts.TryParse` fails.

`RoslynResolver.Owns` returns true for `.cs` files belonging to a project it queried. `Resolve` walks each `SyntaxTree`, and for every invocation converts Roslyn's UTF-16 position to a UTF-8 offset with `Utf8Offsets.ToUtf8` before matching the tree-sitter `CallSite` on `(RelativePath, Offset)`.

- [ ] **Step 4: Handle the built-vs-restored requirement**

Correction 2 is not optional. `ProjectReference`s resolve to `bin/<config>/<tfm>/*.dll`, which exists only after a build; without them `OKF4net.Mcp` produced 4 errors. Implement it this way:

- Compile every repo project from source and cross-reference them as `CompilationReference`, so a merely-restored repo still works.
- If a `ProjectReference` can be satisfied neither from source nor from `bin/`, the resolver reports itself unavailable for that project and `NameMatchResolver`'s baseline stands for its files. ~~`CodeGraphBuilder` then marks the run incomplete, which disables pruning in Task 11.~~ **Both halves of that were wrong, corrected while implementing Task 11:** `RunStatus` records traversal and per-file extraction only — no resolver ever touches it — and resolver availability does not gate pruning, because no resolver contributes a symbol to `CodeGraph.Symbols`, so a degraded one can turn a call link into a code span but never make a concept *absent*, which is what pruning acts on. Gating on it would also make pruning dead on this repository, whose CLI project does not compile without its source generator. Pruning gates on `RunStatus.TraversalComplete` plus the per-file `FileStatus`; `RoslynResolver.IsComplete` is reported to the operator instead. See §7.2 of the design spec and `RoslynResolver.IsComplete`'s doc comment.
- Never build a compilation with errors and resolve from it anyway: an incomplete symbol table mis-attributes calls.

- [ ] **Step 5: Run to verify PASS** — including the attachment-rate test.

- [ ] **Step 6: Commit**

```bash
git add producers/src/OkfProducer.CodeGraph.Roslyn producers/tests/OkfProducer.Tests/CodeGraph/RoslynResolverTests.cs
git commit -m "feat(producer): resolve C# call sites exactly with Roslyn, no MSBuildWorkspace"
```

---

### Task 7: The description chain and field preservation

**Files:**
- Create: `producers/src/OkfProducer.Core/Generation/IDescriptionSource.cs`, `DocCommentSource.cs`, `SignatureSource.cs`, `DescriptionResolver.cs`
- Test: `producers/tests/OkfProducer.Tests/Generation/DescriptionTests.cs`

**Interfaces:**
- Produces: `interface IDescriptionSource { (string Text, string Source)? Describe(SymbolFact fact); }`; `sealed class DescriptionResolver(IReadOnlyList<IDescriptionSource> chain)` with `(string Text, string Source) Resolve(SymbolFact fact, Frontmatter? existing)`.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void A_doc_comment_wins_and_is_labelled_doc_comment()
    {
        var (text, source) = Resolver.Resolve(Member("T", "Scan", doc: "Scans a body."), existing: null);

        Assert.Equal("Scans a body.", text);
        Assert.Equal("doc-comment", source);
    }

    [Fact]
    public void Without_a_doc_comment_a_sentence_is_derived_from_the_signature()
    {
        var (text, source) = Resolver.Resolve(Member("Scanner", "Scan", doc: null), existing: null);

        Assert.Equal("generated", source);
        Assert.Contains("Scan", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manual_description_is_never_overwritten()
    {
        // §4.2: without field-level preservation, a hand-written description
        // disappears on the next generate and the bundle is a throwaway
        // artefact rather than an editable knowledge base.
        var existing = FrontmatterWith(description: "Hand written.", descriptionSource: "manual");

        var (text, source) = Resolver.Resolve(Member("T", "Scan", doc: "Scans a body."), existing);

        Assert.Equal("Hand written.", text);
        Assert.Equal("manual", source);
    }

    [Theory]
    [InlineData("doc-comment")]
    [InlineData("generated")]
    public void A_generated_description_is_re_derived(string previousSource)
    {
        var existing = FrontmatterWith(description: "Stale text.", descriptionSource: previousSource);

        Assert.Equal("Scans a body.", Resolver.Resolve(Member("T", "Scan", doc: "Scans a body."), existing).Text);
    }

    [Fact]
    public void An_llm_description_is_preserved_like_a_manual_one()
        => Assert.Equal("From a model.",
            Resolver.Resolve(Member("T", "Scan", doc: "d"), FrontmatterWith("From a model.", "llm")).Text);
```

- [ ] **Steps 2-4:** red, implement the ordered chain plus the preservation table from §4.2, green.

- [ ] **Step 5: Commit**

```bash
git add producers/src/OkfProducer.Core/Generation producers/tests/OkfProducer.Tests/Generation/DescriptionTests.cs
git commit -m "feat(producer): derive descriptions from doc comments and preserve manual ones"
```

---

### Task 8: Emit code concepts

**Files:**
- Modify: `producers/src/OkfProducer.Core/Generation/ConceptGenerator.cs`
- Test: `producers/tests/OkfProducer.Tests/Generation/CodeConceptGeneratorTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 2, 7.
- Produces: `ConceptGenerator.Generate(RepositorySnapshot, CodeGraph, GenerateOptions)` — the existing overload stays for the no-code path.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void A_member_concept_carries_the_frontmatter_shape_of_section_4_1()
    {
        var concept = Single(Generate(), "code/csharp/n/scanner/scan");
        var fm = concept.Document.Frontmatter;

        Assert.Equal("C# Member", fm.Type);
        Assert.Equal("Scanner.Scan", fm.Title);
        Assert.Equal("doc-comment", fm.Get("description_source")?.AsDisplayString());
        Assert.Contains("csharp", fm.Tags);
        Assert.Null(fm.Get("generated")?.AsMapping()?.Get("at"));   // §4.4: `at` is on overview only
    }

    [Fact]
    public void Resolved_calls_become_absolute_markdown_links()
    {
        // §4.5 / §6.1: absolute so the generator does no relative-path
        // arithmetic, and so `okf graph` resolves them.
        Assert.Contains("[Other.Callee](/code/csharp/n/other/callee)", Single(Generate(), "code/csharp/n/scanner/scan").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Unresolved_calls_are_code_spans_not_links()
    {
        // 54-58% of call sites have no declaration in the repo. Linking them
        // would emit that many BrokenLink diagnostics and drown `validate`.
        var body = Single(Generate(), "code/csharp/n/scanner/scan").Document.Body;

        Assert.Contains("## Calls (unresolved)", body, StringComparison.Ordinal);
        Assert.Contains("`string.Substring`", body, StringComparison.Ordinal);
        Assert.DoesNotContain("[string.Substring]", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Overloads_are_one_concept_listing_every_signature()
    {
        var body = Single(Generate(), "code/csharp/n/t/validate").Document.Body;

        Assert.Contains("public void Validate()", body, StringComparison.Ordinal);
        Assert.Contains("public void Validate(int x)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void With_repo_url_the_resource_is_a_url_and_earns_no_path_warning()
    {
        // §4.3: a bare relative resource resolves against the CONCEPT directory,
        // not the bundle root, so it would miss for every code concept.
        var fm = Single(Generate(repoUrl: "https://github.com/o/r", rev: "main"), "code/csharp/n/scanner/scan").Document.Frontmatter;

        Assert.StartsWith("https://github.com/o/r/blob/main/", fm.Resource, StringComparison.Ordinal);
        Assert.Contains("#L", fm.Resource, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_repo_url_no_resource_is_emitted_rather_than_a_broken_path()
        => Assert.Null(Single(Generate(repoUrl: null), "code/csharp/n/scanner/scan").Document.Frontmatter.Resource);

    [Fact]
    public void There_is_no_called_by_section()
        => Assert.DoesNotContain("## Called by", Single(Generate(), "code/csharp/n/other/callee").Document.Body, StringComparison.Ordinal);
```

- [ ] **Steps 2-4:** red, implement, green.

- [ ] **Step 5: Commit**

```bash
git add producers/src/OkfProducer.Core/Generation/ConceptGenerator.cs producers/tests/OkfProducer.Tests/Generation/CodeConceptGeneratorTests.cs
git commit -m "feat(producer): emit code concepts with call links and unresolved call spans"
```

---

### Task 9: Namespace concepts and containment links

**Files:**
- Modify: `producers/src/OkfProducer.Core/Generation/ConceptGenerator.cs`
- Test: `producers/tests/OkfProducer.Tests/Generation/ContainmentTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void Every_namespace_gets_a_real_concept()
    {
        // A link to a directory's index.md would be a BrokenLink: index.md is a
        // reserved file, not a concept (§5.1).
        Assert.Contains(Generate(), c => c.Id.ToString() == "code/csharp/n");
    }

    [Fact]
    public void Each_level_links_exactly_one_level_down()
    {
        // §5.2, and it is churn control, not cosmetics: if overview listed all
        // 480 concepts, adding one type would rewrite overview.
        Assert.Contains("(/code/csharp/n)", Single(Generate(), "packages/lib").Document.Body, StringComparison.Ordinal);
        Assert.Contains("(/code/csharp/n/scanner)", Single(Generate(), "code/csharp/n").Document.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("/code/csharp/n/scanner/scan", Single(Generate(), "code/csharp/n").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_owns_the_namespaces_declared_by_its_Compile_items()
    {
        // §5.1: NOT "the files in its folder" — MSBuild lets a project add,
        // remove and link sources across directories.
        Assert.Contains("(/code/csharp/n)", Single(Generate(), "packages/lib").Document.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_claimed_by_two_projects_is_attached_once_to_the_first_ordinal_project()
    {
        var linkCount = Generate(sharedFile: true).Count(c => c.Document.Body.Contains("(/code/csharp/shared)", StringComparison.Ordinal));

        Assert.Equal(1, linkCount);
    }

    [Fact]
    public void The_bundle_that_comes_out_validates_clean()
    {
        using var tmp = new TempDir();
        Write(Generate(repoUrl: "https://github.com/o/r"), tmp.Path);

        var outcome = new BundleValidationRunner().Validate(tmp.Path, new FixedClock(new DateOnly(2026, 8, 31)));

        Assert.True(outcome.IsConformant, string.Join("\n", outcome.DiagnosticLines));
        Assert.DoesNotContain(outcome.DiagnosticLines, l => l.Contains("BrokenLink", StringComparison.Ordinal));
    }
```

> The last test needs the `IOkfClock` seam threaded through `IBundleValidationRunner` — §8.1's first debt. Do it here rather than leaving the producer's only end-to-end conformance assertion dependent on today's date.

- [ ] **Steps 2-4:** red, implement, green.

- [ ] **Step 5: Commit**

```bash
git add producers/src/OkfProducer.Core producers/tests/OkfProducer.Tests/Generation/ContainmentTests.cs
git commit -m "feat(producer): add namespace concepts and one-level containment links"
```

---

### Task 10: Determinism and the HEAD-commit stamp

**Files:**
- Create: `producers/src/OkfProducer.Core/Generation/GitRevision.cs`
- Modify: `producers/src/OkfProducer.Core/Generation/ConceptGenerator.cs`
- Test: `producers/tests/OkfProducer.Tests/Generation/DeterminismTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void Two_runs_over_the_same_source_are_byte_identical()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        Write(Generate(), a.Path);
        Write(Generate(), b.Path);

        foreach (var file in Directory.GetFiles(a.Path, "*.md", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(a.Path, file);
            Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(b.Path, rel)));
        }
    }

    [Fact]
    public void Generated_at_is_the_HEAD_commit_instant_not_the_wall_clock()
    {
        // §6.1: a wall clock makes --check fail forever on that one field, and
        // the stamp answers a better question — which state of the code this
        // bundle reflects.
        var at = Single(Generate(), "overview").Document.Frontmatter.GeneratedAt;

        Assert.Equal(GitRevision.HeadCommitInstant(RepoRoot), at);
        Assert.EndsWith("Z", at, StringComparison.Ordinal);   // §5: explicit UTC offset
    }

    [Fact]
    public void Only_overview_carries_at_and_revision()
    {
        var concepts = Generate();

        Assert.NotNull(Single(concepts, "overview").Document.Frontmatter.Get("revision"));
        Assert.Null(Single(concepts, "code/csharp/n/scanner/scan").Document.Frontmatter.Get("revision"));
    }

    [Fact]
    public void Output_never_contains_an_absolute_path_or_a_backslash_separator()
    {
        foreach (var concept in Generate())
        {
            Assert.DoesNotContain(RepoRoot, concept.Document.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\", concept.Document.Frontmatter.Resource ?? "", StringComparison.Ordinal);
        }
    }
```

> `generated.at` must be a full ISO 8601 instant with an explicit offset (`2026-08-31T12:34:56Z`) — §5 of the upstream spec requires it, and a date-only value is non-conformant. See `docs/superpowers/plans/2026-08-31-okf-temporal-conformance.md` for the matching library-side fix.

- [ ] **Steps 2-4:** red, implement `GitRevision` by shelling out to `git rev-parse HEAD` and `git show -s --format=%cI HEAD` (falling back to the wall clock outside a repo, documented), green.

- [ ] **Step 5: Commit**

```bash
git add producers/src/OkfProducer.Core/Generation producers/tests/OkfProducer.Tests/Generation/DeterminismTests.cs
git commit -m "feat(producer): make output deterministic and stamp the HEAD commit instant"
```

---

### Task 11: Transactional writes, manifest and pruning

**Files:**
- Create: `producers/src/OkfProducer.Core/Generation/GenerationManifest.cs`
- Modify: `producers/src/OkfProducer.Core/Generation/IBundleWriter.cs`, `BundleWriter.cs`
- Test: `producers/tests/OkfProducer.Tests/Generation/PruningTests.cs`

**Interfaces:**
- Produces: `record GenerationManifest(string OwnedPrefix, IReadOnlyList<string> ConceptIds, IReadOnlyList<string> ExtractedFiles)`; `IBundleWriter.Write(string outPath, IReadOnlyList<GeneratedConcept> concepts, WritePolicy policy, string repoPath, GenerationManifest manifest, RunStatus status)`.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void A_deleted_method_loses_its_concept_on_the_next_run()
    {
        // The defect §6.3 exists to fix: WritePolicy.Update never deletes, so a
        // removed method keeps a concept pointing at code that no longer exists,
        // and an agent gets a confidently wrong answer.
        using var tmp = new TempDir();
        WriteRun(tmp, ["code/csharp/n/t/a", "code/csharp/n/t/b"], complete: true);
        WriteRun(tmp, ["code/csharp/n/t/a"], complete: true);

        Assert.False(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
    }

    [Fact]
    public void An_incomplete_run_deletes_nothing()
    {
        // "Absent from this run" has two causes — the symbol is gone, or the
        // file could not be read. They are indistinguishable, so a degraded run
        // must not prune.
        using var tmp = new TempDir();
        WriteRun(tmp, ["code/csharp/n/t/a", "code/csharp/n/t/b"], complete: true);
        WriteRun(tmp, ["code/csharp/n/t/a"], complete: false);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
    }

    [Fact]
    public void A_hand_written_concept_under_the_owned_prefix_is_never_deleted()
    {
        // Pruning is keyed on the PREVIOUS manifest, not on the prefix, so a
        // file the generator never produced is not its to delete.
        using var tmp = new TempDir();
        WriteRun(tmp, ["code/csharp/n/t/a"], complete: true);
        File.WriteAllText(Path.Combine(tmp.Path, "code/csharp/n/t/human.md"), "---\ntype: Note\n---\nMine.\n");

        WriteRun(tmp, ["code/csharp/n/t/a"], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/human.md")));
    }

    [Fact]
    public void Anything_outside_the_owned_prefix_is_preserved()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "notes.md"), "---\ntype: Note\n---\nKeep me.\n");

        WriteRun(tmp, ["code/csharp/n/t/a"], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "notes.md")));
    }

    [Fact]
    public void A_failure_mid_write_leaves_the_bundle_untouched()
    {
        // Staging: the bundle is only touched once the whole run succeeded.
        using var tmp = new TempDir();
        WriteRun(tmp, ["code/csharp/n/t/a"], complete: true);
        var before = File.ReadAllBytes(Path.Combine(tmp.Path, "code/csharp/n/t/a.md"));

        Assert.Throws<InvalidOperationException>(() => WriteRunThatThrows(tmp));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(tmp.Path, "code/csharp/n/t/a.md")));
    }
```

- [ ] **Steps 2-4:** red; implement staging (write to a sibling temp directory, then move), the manifest (a JSON file under the bundle, e.g. `.okfgen-manifest.json`, excluded from concept discovery because it is not `*.md`), and pruning restricted to `previousManifest.ConceptIds` minus this run's ids, ~~only when `status.IsComplete`~~ **only when `status.TraversalComplete` (ruling R21 — `IsComplete` is false on ordinary modern C# and would make pruning dead code), and per candidate only when every source file it was derived from is `FileStatus.Extracted`**; green.

- [ ] **Step 5: Commit**

```bash
git add producers/src/OkfProducer.Core/Generation producers/tests/OkfProducer.Tests/Generation/PruningTests.cs
git commit -m "feat(producer): write transactionally and prune only what a complete run owned"
```

---

### Task 12: `--check` and the golden fixture bundle

**Files:**
- Create: `producers/tests/OkfProducer.Tests/fixtures/` (a small source repo + its golden bundle + a README)
- Modify: `producers/src/OkfProducer.Cli/Program.cs`
- Test: `producers/tests/OkfProducer.Tests/Generation/CheckTests.cs`, `BlastRadiusTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void Check_passes_on_an_unchanged_bundle()
        => Assert.Equal(0, RunCheck(FixtureRepo, FixtureBundle));

    [Fact]
    public void Check_fails_when_the_source_changed()
    {
        using var repo = CopyFixtureRepo();
        File.AppendAllText(Path.Combine(repo.Path, "src/Scanner.cs"), "\n// nudge\n");

        Assert.NotEqual(0, RunCheck(repo.Path, FixtureBundle));
    }

    [Fact]
    public void Check_does_not_report_a_manual_description_as_drift()
    {
        // The contradiction §6.2 had to resolve: regenerating into an EMPTY temp
        // directory has nothing to preserve, so every concept with a manual
        // description would read as drift, forever. --check copies the existing
        // bundle and runs the update path over it.
        using var bundle = CopyFixtureBundle();
        SetDescription(bundle, "code/csharp/n/scanner/scan", "Hand written.", "manual");

        Assert.Equal(0, RunCheck(FixtureRepo, bundle.Path));
    }
```

And the blast-radius tests, which are what actually verify §3.2, §5.2 and §6.3's churn promises:

```csharp
    [Theory]
    [InlineData(Mutation.AddOverload, "code/csharp/n/scanner/scan")]
    [InlineData(Mutation.AddPublicType, "code/csharp/n", "code/csharp/n/added")]
    [InlineData(Mutation.AddPrivateMember)]
    [InlineData(Mutation.DeleteMethod, "code/csharp/n/scanner", "-code/csharp/n/scanner/gone")]
    public void A_source_mutation_changes_exactly_the_expected_concepts(Mutation mutation, params string[] expected)
    {
        // The table counts CONCEPTS. The index.md files of their parent
        // directories follow mechanically and are excluded from the comparison.
        var changed = ConceptsChangedBy(mutation);

        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), changed.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void A_commit_that_does_not_touch_code_changes_only_overview()
    {
        // overview carries `revision`, so it moves with every commit — 1 file
        // out of ~480. That bound is the property; "nothing changes" is not.
        Assert.Equal(["overview"], ConceptsChangedBy(Mutation.CommitUnrelatedFile));
    }
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Build the fixture and write its README**

The fixture repo is **purpose-built and small** — around 15-20 concepts, one occurrence of each shape: a namespace, a type, a merged overload pair, a resolved call, an unresolved call, a private member, and a symbol that a later mutation deletes. A golden over 480 concepts is not reviewable in a diff, so it is not a test.

`producers/tests/OkfProducer.Tests/fixtures/README.md` must state the discipline explicitly, because it is the **opposite** of the repo's other fixture rule:

> This golden captures **our own** output, not a reference implementation's. It is regenerable by construction, and it **must** be regenerated whenever the generator changes intentionally — then the diff is reviewed as part of the change. This is not `tests/fixtures/`, whose byte-exact captures of the reference CLI must never be edited to make a test pass.

- [ ] **Step 4: Implement `--check`** — copy the existing bundle to a temp directory, run the full `--update` path over the copy, compare byte-for-byte, and exclude only the fields §6.2 lists, only outside a git repo.

- [ ] **Step 5: Run to verify PASS.**

- [ ] **Step 6: Commit**

```bash
git add producers/tests/OkfProducer.Tests producers/src/OkfProducer.Cli
git commit -m "feat(producer): add --check over a bundle copy, with a golden fixture and blast-radius tests"
```

---

### Task 13: CLI surface, packaging and documentation

**Files:**
- Modify: `producers/src/OkfProducer.Cli/Program.cs`, `producers/src/OkfProducer.Cli/OkfProducer.Cli.csproj`
- Modify: `producers/README.md` *(create if absent)*, `ROADMAP.md`
- Test: `producers/tests/OkfProducer.Tests/CliTests.cs`

- [ ] **Step 1: Write the failing tests** — one per flag in §9: `--repo-url`, `--rev`, `--check`, `--include-tests`, `--include-internal`, `--no-code`, `--max-file-size`; plus that `--rev` is required for permalinks on a detached HEAD, and that `--no-code` reproduces the pre-existing output exactly.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement the flags** with `System.CommandLine`, matching the defaults in §9's table.

- [ ] **Step 4: Configure RID-specific tool packaging**

```xml
<PackAsTool>true</PackAsTool>
<ToolCommandName>okfgen</ToolCommandName>
<RuntimeIdentifiers>win-x64;linux-x64;osx-arm64</RuntimeIdentifiers>
<ToolPackageRuntimeIdentifiers>win-x64;linux-x64;osx-arm64</ToolPackageRuntimeIdentifiers>
```

`ToolPackageRuntimeIdentifiers` **without** `RuntimeIdentifiers` fails `dotnet pack` with `NETSDK1047` (§7.1). All sub-packages must be pushed, not just the pointer. Do **not** apply any of this to `src/OKF4net.Mcp`, which has real users and declares no `RuntimeIdentifiers` today.

- [ ] **Step 5: Verify the pack manually and document it as manual**

```bash
dotnet pack producers/src/OkfProducer.Cli -c Release
```

Confirm one pointer package plus one per RID. This is a **documented release-time manual step**, not a guarantee: `producers/` is outside CI by decision, and a per-RID install smoke test cannot be honest without it. Write it that way in `producers/README.md` rather than implying coverage that does not exist.

- [ ] **Step 6: Correct the ROADMAP**

`ROADMAP.md`'s producer section still records "No CI coverage" as an open follow-up with two options. The decision was taken on 2026-08-01: `producers/` does not go into CI. Record the **decision**, not the question, so the next reader does not reopen it. Also correct the neighbouring claim that the validator resolves `sources[].resource` "relative to the bundle root" — it resolves a bare relative value against the **concept directory** (`Bundle.cs:384`).

- [ ] **Step 7: Full verification**

```bash
dotnet build producers/OkfProducer.sln
dotnet test producers/OkfProducer.sln
dotnet format producers/OkfProducer.sln --verify-no-changes
```

- [ ] **Step 8: Commit**

```bash
git add producers ROADMAP.md
git commit -m "feat(producer): complete the okfgen CLI surface and RID-specific packaging"
```

---

## Out of scope

Each is deliberate, and §10 of the spec carries the reasoning:

- **LLM description enrichment.** The seam (`IDescriptionSource`) and its prerequisite (field preservation, Task 7) ship; the implementation does not.
- **Incremental cache and `watch`.** `ILanguageExtractor` is per-file and pure, so a cache is a decorator when `watch` arrives.
- **Reducing the per-RID package below ~69 MB.** Documented follow-up, not a v1 promise.
- **`log.md` generation history.** Nothing to record until pruning has run for real.
- **SCIP/LSIF resolvers.** Re-attachable later as another `ISymbolResolver`.
- **Languages beyond C#.** The architecture is multi-language by construction, but only the C# profile — the one with a precise resolver behind it — is in v1.
- **A node/edge filter for `okf graph`.** At ~480 concepts the whole-graph DOT render is unreadable and `CmdGraph` has no filter. That is a CLI follow-up, not this lot.
