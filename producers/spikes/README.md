# Producer spikes

Feasibility probes that back a design decision in `docs/superpowers/specs/`.

**These are committed on purpose.** The earlier tree-sitter spike lived as three
untracked directories in a worktree, and the commit its design doc cited
(`79ade6a`) contained none of it — so when an external reviewer tried to
reproduce its numbers, they could not, and two of its conclusions turned out to
be wrong. A measurement nobody else can re-run is not evidence. A spike that a
design cites gets committed, with the command that produced its numbers.

Like the rest of `producers/`, these are outside `OKF4net.sln` and outside CI.
They are throwaway code kept for its evidence, not shipped components: they are
exempt from the repo's warnings-as-errors, and nothing depends on them.

---

## `RoslynCompilationSpike`

**Question (design §7.2):** can a *correct* `CSharpCompilation` be built from
MSBuild's own item and property queries, without `MSBuildWorkspace`?

**Why it existed as an open question.** The design asserted yes, on the strength
of a measurement — "213 references resolved in 1.9 s" — that measured an MSBuild
command, not a compilation. The tree-sitter spike it leaned on never used those
references: it fed Roslyn from `AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")`,
the running process's own assemblies, so its ~419 errors were an artefact of its
own setup and said nothing either way.

**Run it:**

```sh
dotnet run --project producers/spikes/RoslynCompilationSpike -- \
  src/OKF4net/OKF4net.csproj \
  src/OKF4net.Mcp/OKF4net.Mcp.csproj \
  src/OKF4net.Agents/OKF4net.Agents.csproj

# Simulate a repo that is restored but not built:
dotnet run --project producers/spikes/RoslynCompilationSpike -- \
  --drop-project-refs src/OKF4net.Mcp/OKF4net.Mcp.csproj
```

**Result, 2026-08-31 (SDK 10.0.204, `Microsoft.CodeAnalysis.CSharp` 4.14.0):**

| Project | Compile items | References | Errors |
|---|---|---|---|
| `OKF4net` | 40 (2 generated) | 167 | **0** |
| `OKF4net.Mcp` | 7 (2 generated) | 213 | **0** |
| `OKF4net.Agents` | 6 (2 generated) | 186 | **0** |

MSBuild query cost: 533–1194 ms per project.

**Answer: yes — with three corrections to what the design assumed.**

1. **`-t:ResolveReferences` alone is not enough.** It yields the references but
   not the generated sources. This repo enables `ImplicitUsings` (the SDK default
   for new projects), so without `-t:GenerateGlobalUsings -t:GenerateAssemblyInfo`
   the `Compile` set is missing `*.GlobalUsings.g.cs` and `*.AssemblyInfo.cs`, and
   every file relying on an implicit global using fails.

2. **The repo must be BUILT, not merely restored.** `ProjectReference`s resolve to
   `bin/<config>/<tfm>/*.dll`, which exists only after a build. Measured with
   `--drop-project-refs`: `OKF4net.Mcp` goes from 0 errors to **4** (`CS0234` on
   the `OKF4net.Agents` namespace, `CS0246`/`CS0103` on `OkfBundleTools`) — the
   referenced project's symbols vanish entirely. The design said "restored";
   that is wrong. Either require a build, or have the producer compile the
   referenced projects itself and pass `CompilationReference` — the route the
   design already preferred, now mandatory rather than optional.

3. **The Roslyn package must track the SDK's language version.**
   `Microsoft.CodeAnalysis.CSharp` 4.14.0 does not know `LangVersion 14`:
   `LanguageVersionFacts.TryParse("14", …)` fails and the spike falls back to
   `Preview`. It reached zero errors anyway, but the fallback silently changes
   parse semantics, so the real producer must either pin a Roslyn build that
   knows the SDK's language version or fail loudly rather than degrade.

**What this does not establish.** Only C# projects on one machine, one SDK, one
configuration (`Debug`). Multi-TFM projects, source generators that contribute
`Compile` items, and `Directory.Build.props` chains other than this repo's are
untested — see §7.2 of the design for what the production resolver must still
handle.
