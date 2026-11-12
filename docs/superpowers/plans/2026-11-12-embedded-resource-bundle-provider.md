# Embedded-resource bundle provider (#141) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `OKF4net.Providers.EmbeddedResources`: a read-only `IBundleProvider` over a .NET assembly's manifest resources, plus a pure-MSBuild `.targets` (in the NuGet package) that embeds a bundle directory one file per resource, refusing links and unrepresentable names.

**Architecture:** Build side: `build/OKF4net.Providers.EmbeddedResources.targets` turns `<OkfBundle Include="dir/" Prefix="kb" />` into `EmbeddedResource` items named `okf:kb/<relative path>` (`WithCulture=false`, `Type=Non-Resx`), after a recursive `MSBuild`-task walk that inspects each directory before descending and refuses any link. Run side: `EmbeddedResourceBundleProvider(Assembly, prefix)` lists the `okf:<prefix>/` names once, serves them through completed `ValueTask`s, versions everything by the manifest module's MVID, and refuses writes. The core gains one behaviour change (D7): the two path-based `[Obsolete]` members refuse a provider that is not a `FileSystemBundleProvider`.

**Tech Stack:** C# 14 / .NET 10, BCL only (zero `PackageReference` in `src/`), MSBuild property functions and item functions, xunit (+ `Xunit.SkippableFact`, already referenced), Native AOT, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-12-embedded-resource-bundle-provider-design.md` (revision 3 + the prototype note of §8). Decisions D1–D7 are in its §2; read §4 (runtime), §5 (build), §6 (tests) before any task. Prerequisite reading: `docs/superpowers/specs/2026-10-07-bundle-provider-seam-design.md`, `src/OKF4net/Providers/IBundleProvider.cs`.

## Global Constraints

- Branch `feat/embedded-resource-provider` (already created from `dev`, holds the spec). Nothing is pushed, no PR is opened, nothing is merged without the maintainer's explicit go — Task 1's gate asks for it.
- `src/OKF4net.Providers.EmbeddedResources/` references **only** `src/OKF4net/` and has **zero `PackageReference`**. The `.targets` is pure MSBuild: no task assembly, no `UsingTask`, no `RoslynCodeTaskFactory`. If Task 1's gate fails on Linux or macOS, **stop** and report: D2 is reopened with the maintainer (spec §5.1), no compiled or inline task is added on your own.
- Resource name convention: `okf:<prefix>/<relative path with '/'>`; a prefix is non-empty and contains no `/`.
- Build error codes exactly: `OKFEMB001` invalid prefix, `OKFEMB002` missing or empty directory, `OKFEMB003` prefixes equal ignoring case, `OKFEMB004` name an assembly cannot hold (`\` off Windows, `"`, `|`, control character, case twin), `OKFEMB005` link (directory or file), `OKFEMB006` a `Compile`/`Content`/`EmbeddedResource` item left under a bundle directory.
- Provider facts: `Capabilities = new(CanWrite: false, CompletesSynchronously: true)`; `NativeConstraints = BundleKeyConstraints.WellFormedUnicode`; `Id = "embedded:<assembly FullName>/<MVID:N>/<prefix>"`; `Location = "embedded:<assembly simple name>/<prefix>"`; one version token `"mvid:<MVID:N>"` for the bundle and every object; **no bound applied by the provider** (the core applies all three in `Bundle.ListNamesAsync`).
- Fixed clock for every validation/audit comparison: `new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))`.
- `tests/fixtures/` and `bundles/` are embedded, never edited. `docs/spec/SPEC.md` untouched.
- New files start with `// SPDX-License-Identifier: LGPL-3.0-or-later` (C#) or `<!-- SPDX-License-Identifier: LGPL-3.0-or-later -->` (XML). XML doc comments on public API. `ConfigureAwait(false)` on every `await` in `src/` (CA2007 is an error there).
- Per task, before committing: `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"`, `dotnet format OKF4net.sln --verify-no-changes`. Commits end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

Failure modes the spec implies that no task's happy path exercises; each is pinned by a test in the task named.

1. **A project that references the package but declares no `OkfBundle`** must embed nothing and remove nothing (an empty batch would otherwise glob the whole project, `obj/` and secrets included) — Task 1, `Is_inert_without_OkfBundle`.
2. **A link anywhere under the bundle directory** — outward junction/symlink, cycle to an ancestor, dangling link, file symlink — must fail the build with `OKFEMB005` **without listing the link's target** — Task 1, `Refuses_a_link_without_listing_its_target`.
3. **An `OkfBundle` declared where the evaluation-time `Remove` cannot see it** (`Directory.Build.targets`) must fail with `OKFEMB006` instead of compiling a bundle `.cs` into the host — Task 1, `Refuses_an_OkfBundle_the_Remove_cannot_see`.
4. **A key longer than `MaxKeyBytes`** must be a local parse problem, the rest of the bundle loading — never a whole-load failure from the provider — Task 2, `An_over_long_key_is_a_local_problem_not_a_load_failure`.
5. **A legacy caller of `Bundle.ReadResourceText(string)` or `TryResolveResource`** on a non-filesystem bundle must get `NotSupportedException`, never a read of a file under the current directory — Task 3.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/OKF4net.Providers.EmbeddedResources/OKF4net.Providers.EmbeddedResources.csproj` | Project, packaging block, packs `build/*.targets`, `README.md`, `NOTICE`. |
| `src/OKF4net.Providers.EmbeddedResources/build/OKF4net.Providers.EmbeddedResources.targets` | The whole build side (spec §5). |
| `src/OKF4net.Providers.EmbeddedResources/EmbeddedResourceBundleProvider.cs` | The provider (spec §4). |
| `src/OKF4net.Providers.EmbeddedResources/README.md` | Package README (recipe, domain, codes, catalog factory recipe). |
| `src/OKF4net/OKF4net.csproj` | `InternalsVisibleTo` the new project (reuses `Internal/SyncAsyncEnumerable`). |
| `src/OKF4net/Bundle.cs` | D7 guard on `TryResolveResource` and `ReadResourceText(string)`. |
| `tests/OKF4net.Tests/OKF4net.Tests.csproj` | Reference the provider, declare test bundles, import the `.targets` at the end of the body. |
| `tests/OKF4net.Tests/Providers/EmbeddedResources/EmbeddedBuild.cs` | Child-process build helper for the `.targets` tests. |
| `tests/OKF4net.Tests/Providers/EmbeddedResources/TargetsBuildTests.cs` | `.targets` tests (spec §6.2, child builds). |
| `tests/OKF4net.Tests/Providers/EmbeddedResources/EmbeddedResourceBundleProviderTests.cs` | Provider contract tests (spec §6.1). |
| `tests/OKF4net.Tests/Providers/EmbeddedResources/EmbeddedConsumersTests.cs` | Read-only refusal through writer/index/tools + no residual file I/O (spec §4.4, §6.1). |
| `tests/OKF4net.Tests/Providers/EmbeddedResources/ParityTests.cs` + `CapturingProvider.cs` | Filesystem/embedded parity (spec §6.3). |
| `tests/OKF4net.Tests/Providers/EmbeddedResources/Assets/**` | Test bundles `contract`, `contract2`, `names`, and `stray.txt`. |
| `tests/OKF4net.Tests/Providers/ObsoletePathMembersTests.cs` | D7 tests. |
| `tests/EmbeddedBundleAotProbe/**` | AOT probe, outside `OKF4net.sln` (spec §6.4). |
| `.github/workflows/ci.yml`, `.github/workflows/release.yml` | AOT job; pack step. |
| `README.md`, `CHANGELOG.md`, `CLAUDE.md` | Docs (spec §7). |

---

### Task 1: Project scaffold, the `.targets`, and its child-build tests — **the prototype gate**

**Files:**
- Create: `src/OKF4net.Providers.EmbeddedResources/OKF4net.Providers.EmbeddedResources.csproj`
- Create: `src/OKF4net.Providers.EmbeddedResources/build/OKF4net.Providers.EmbeddedResources.targets`
- Create: `src/OKF4net.Providers.EmbeddedResources/README.md` (stub; completed in Task 7)
- Modify: `OKF4net.sln` (add the project under `src`)
- Modify: `tests/OKF4net.Tests/OKF4net.Tests.csproj` (ProjectReference only, so the test build builds the project)
- Create: `tests/OKF4net.Tests/Providers/EmbeddedResources/EmbeddedBuild.cs`
- Test: `tests/OKF4net.Tests/Providers/EmbeddedResources/TargetsBuildTests.cs`

**Interfaces:**
- Produces: `EmbeddedBuild.TargetsPath` (string), `EmbeddedBuild.Build(string projectDir, string itemsXml, string? directoryBuildTargetsXml = null, string verbosity = "minimal") : EmbeddedBuild.Result` with `record Result(int ExitCode, string Output, string AssemblyPath)`, `EmbeddedBuild.ReadOkfResources(string assemblyPath) : SortedDictionary<string, byte[]>` (resource name → bytes, `okf:` names only), `EmbeddedBuild.ReadFileSystemView(string bundleDir) : SortedDictionary<string, byte[]>` (raw name as `FileSystemBundleProvider` lists it → bytes). Task 2 uses `Build` and loads `AssemblyPath`.
- The `.targets` exposes only the item type `OkfBundle` (metadata `Prefix`) and the target `OkfEmbedBundles`; everything else is `_Okf`-prefixed.

- [ ] **Step 1: Create the project**

`src/OKF4net.Providers.EmbeddedResources/OKF4net.Providers.EmbeddedResources.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>

  <PropertyGroup Label="Packaging">
    <PackageId>OKF4net.Providers.EmbeddedResources</PackageId>
    <Authors>Julien CHABLE</Authors>
    <Description>Ship an OKF knowledge bundle inside your .NET assembly: an MSBuild item (OkfBundle) embeds the bundle one file per manifest resource, and a read-only IBundleProvider serves it back, Native AOT and single-file included, with no directory deployed beside the binary.</Description>
    <Copyright>Copyright 2026 Julien CHABLE</Copyright>
    <PackageLicenseExpression>LGPL-3.0-or-later</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageProjectUrl>https://github.com/jchable/okf4net</PackageProjectUrl>
    <RepositoryUrl>https://github.com/jchable/okf4net</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageTags>okf;knowledge;embedded-resources;native-aot;knowledge-base</PackageTags>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  </PropertyGroup>

  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
    <None Include="..\..\NOTICE" Pack="true" PackagePath="\" />
    <!-- build/, not buildTransitive/: only the project that declares OkfBundle needs it (spec §5.6). -->
    <None Include="build\OKF4net.Providers.EmbeddedResources.targets" Pack="true" PackagePath="build\" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\OKF4net\OKF4net.csproj" />
  </ItemGroup>

</Project>
```

`src/OKF4net.Providers.EmbeddedResources/README.md` (stub):

```markdown
# OKF4net.Providers.EmbeddedResources

Ship an OKF bundle inside your .NET assembly. Full documentation lands with the provider (see the repository README).
```

Run: `dotnet sln OKF4net.sln add src/OKF4net.Providers.EmbeddedResources/OKF4net.Providers.EmbeddedResources.csproj --solution-folder src`

In `tests/OKF4net.Tests/OKF4net.Tests.csproj`, add to the `ProjectReference` group:

```xml
    <ProjectReference Include="..\..\src\OKF4net.Providers.EmbeddedResources\OKF4net.Providers.EmbeddedResources.csproj" />
```

- [ ] **Step 2: Write the `.targets`**

This is the prototype that passed on Windows on 2026-11-12 (spec §8), with its comments. Write it exactly:

`src/OKF4net.Providers.EmbeddedResources/build/OKF4net.Providers.EmbeddedResources.targets`:

```xml
<!-- SPDX-License-Identifier: LGPL-3.0-or-later -->
<!--
  OKF4net.Providers.EmbeddedResources: embeds an OKF bundle directory in the
  assembly, one manifest resource per file, named okf:<Prefix>/<relative path>.
  Design: docs/superpowers/specs/2026-11-12-embedded-resource-bundle-provider-design.md (§5).

  Imported by NuGet into EVERY project that references the package: without an
  OkfBundle item, nothing below runs (an empty batch would otherwise glob "**",
  the whole project directory, obj/ included).
-->
<Project>

  <!--
    Evaluation time (§5.3): a bundle file must not also be a default Compile,
    EmbeddedResource or Content item (a sanctioned .cs script would be compiled
    into the host, a .resx passed to ResGen). None is left alone: it keeps the
    files visible in the IDE and changes nothing unless the host sets copy
    metadata itself. '%(FullPath)/**' removes both Include="dir" and
    Include="dir/"; EnsureTrailingSlash is NOT usable here: a property function
    over metadata is not expanded inside an evaluation-time transform.
    This sees every OkfBundle declared before NuGet's import (the project body);
    one declared later (Directory.Build.targets, a target) is caught by
    OKFEMB006 at execution.
  -->
  <ItemGroup Condition="'@(OkfBundle)' != ''">
    <Compile Remove="@(OkfBundle->'%(FullPath)/**')" />
    <EmbeddedResource Remove="@(OkfBundle->'%(FullPath)/**')" />
    <Content Remove="@(OkfBundle->'%(FullPath)/**')" />
  </ItemGroup>

  <!--
    BeforeTargets="PrepareResourceNames" runs AFTER that target's dependencies
    (AssignTargetPaths, SplitResourcesByCulture, CreateManifestResourceNames):
    the items added here skip AssignCulture and the .licx/.resx handling and
    arrive complete. WithCulture="false" and Type="Non-Resx" are then the only
    filter of _CoreCompileResourceInputs: without them the build succeeds with
    ZERO resources. Running earlier would send a bundle .licx to the LC task
    (MSB4803); editing PrepareResourceNamesDependsOn is silently overwritten by
    Microsoft.Common.CurrentVersion.targets.
  -->
  <Target Name="OkfEmbedBundles"
          BeforeTargets="PrepareResourceNames"
          DependsOnTargets="_OkfPrepareBundles;_OkfCollectBundle"
          Condition="'@(OkfBundle)' != ''">
    <ItemGroup>
      <EmbeddedResource Include="@(_OkfFile)" WithCulture="false" Type="Non-Resx" />
    </ItemGroup>
  </Target>

  <Target Name="_OkfPrepareBundles">
    <ItemGroup>
      <_OkfRoot Include="@(OkfBundle->'%(FullPath)')">
        <OkfDir>$([MSBuild]::EnsureTrailingSlash('%(FullPath)'))</OkfDir>
      </_OkfRoot>
      <_OkfPrefix Include="@(_OkfRoot->'%(Prefix)')" />
    </ItemGroup>
    <Error Code="OKFEMB001"
           Condition="'%(_OkfRoot.Prefix)' == '' or $([System.String]::Copy('%(_OkfRoot.Prefix)').Contains('/'))"
           Text="OkfBundle '%(_OkfRoot.Identity)': Prefix is missing, empty or contains '/'." />
    <Error Code="OKFEMB002"
           Condition="!Exists('%(_OkfRoot.OkfDir)')"
           Text="OkfBundle '%(_OkfRoot.Identity)': the directory does not exist." />
    <!-- Distinct() compares ignoring case, as csc compares resource names (CS1508). -->
    <Error Code="OKFEMB003"
           Condition="'@(_OkfPrefix->Count())' != '@(_OkfPrefix->Distinct()->Count())'"
           Text="Two OkfBundle items share a Prefix (compared ignoring case): @(_OkfPrefix, ', ')." />
  </Target>

  <!-- One batch per bundle (target batching on _OkfRoot). -->
  <Target Name="_OkfCollectBundle" Outputs="%(_OkfRoot.Identity)">
    <PropertyGroup>
      <_OkfThisDir>%(_OkfRoot.OkfDir)</_OkfThisDir>
      <_OkfThisPrefix>%(_OkfRoot.Prefix)</_OkfThisPrefix>
      <_OkfThisDirLower>$(_OkfThisDir.ToLowerInvariant())</_OkfThisDirLower>
    </PropertyGroup>

    <!-- D6: refuse every link, inspecting each directory BEFORE descending into it. -->
    <MSBuild Projects="$(MSBuildThisFileFullPath)" Targets="_OkfWalkDir"
             Properties="_OkfDir=$([MSBuild]::Escape($([System.IO.Path]::GetDirectoryName('$(_OkfThisDir)'))))">
      <Output TaskParameter="TargetOutputs" ItemName="_OkfWalked" />
    </MSBuild>
    <Error Code="OKFEMB005"
           Condition="'%(_OkfWalked.OkfIsLink)' == 'true'"
           Text="OkfBundle '$(_OkfThisDir)': '%(_OkfWalked.Identity)' is a symbolic link or junction; links are refused." />

    <!-- OKFEMB006: a default item the evaluation-time Remove did not see (checked before this target adds its own). -->
    <ItemGroup>
      <_OkfLeftover Include="@(Compile);@(Content);@(EmbeddedResource)"
                    Condition="$([System.String]::Copy('%(FullPath)').ToLowerInvariant().StartsWith('$(_OkfThisDirLower)'))" />
    </ItemGroup>
    <Error Code="OKFEMB006"
           Condition="'@(_OkfLeftover)' != ''"
           Text="OkfBundle '$(_OkfThisDir)': @(_OkfLeftover, ', ') is still a Compile, Content or EmbeddedResource item; declare OkfBundle in the project file, not in Directory.Build.targets or a target." />

    <!-- Only now, with no directory link left, is the tree globbed. -->
    <ItemGroup>
      <_OkfThisFile Include="$(_OkfThisDir)**" />
      <_OkfThisFile>
        <!-- From FullPath, never %(RecursiveDir)%(Filename)%(Extension): %(Extension) drops the dot of "name." on Linux. -->
        <OkfRel>$([System.String]::Copy('%(FullPath)').Substring($(_OkfThisDir.Length)))</OkfRel>
        <OkfAttrs>$([System.IO.File]::GetAttributes('%(FullPath)'))</OkfAttrs>
      </_OkfThisFile>
    </ItemGroup>
    <Error Code="OKFEMB002"
           Condition="'@(_OkfThisFile)' == ''"
           Text="OkfBundle '$(_OkfThisDir)': the directory holds no file." />
    <Error Code="OKFEMB005"
           Condition="$([System.String]::Copy('%(_OkfThisFile.OkfAttrs)').Contains('ReparsePoint'))"
           Text="OkfBundle '$(_OkfThisDir)': '%(_OkfThisFile.FullPath)' is a symbolic link; links are refused." />
    <Error Code="OKFEMB004"
           Condition="$([System.Text.RegularExpressions.Regex]::IsMatch('%(_OkfThisFile.OkfRel)', '[&quot;|\x00-\x1F\x7F]')) or ('$([MSBuild]::IsOSPlatform(`Windows`))' != 'true' and $([System.String]::Copy('%(_OkfThisFile.OkfRel)').Contains('\')))"
           Text="OkfBundle '$(_OkfThisDir)': '%(_OkfThisFile.OkfRel)' cannot be held as an embedded resource name (\ off Windows, &quot;, |, or a control character)." />

    <ItemGroup>
      <_OkfThisFile>
        <OkfName Condition="$([MSBuild]::IsOSPlatform('Windows'))">$([System.String]::Copy('%(OkfRel)').Replace('\', '/'))</OkfName>
        <OkfName Condition="!$([MSBuild]::IsOSPlatform('Windows'))">%(OkfRel)</OkfName>
      </_OkfThisFile>
      <_OkfThisName Include="@(_OkfThisFile->'%(OkfName)')" />
      <!-- Batching on Identity groups case-insensitively: a bucket of two or more is a case twin. -->
      <_OkfTwin Include="%(_OkfThisName.Identity)" Condition="'@(_OkfThisName->Count())' &gt; '1'" />
    </ItemGroup>
    <Error Code="OKFEMB004"
           Condition="'@(_OkfTwin)' != ''"
           Text="OkfBundle '$(_OkfThisDir)': files differ only by case, which an assembly cannot hold: @(_OkfTwin, ', ')." />

    <ItemGroup>
      <_OkfFile Include="@(_OkfThisFile)" LogicalName="okf:$(_OkfThisPrefix)/%(_OkfThisFile.OkfName)" />
      <_OkfThisFile Remove="@(_OkfThisFile)" />
      <_OkfThisName Remove="@(_OkfThisName)" />
      <_OkfTwin Remove="@(_OkfTwin)" />
      <_OkfLeftover Remove="@(_OkfLeftover)" />
      <_OkfWalked Remove="@(_OkfWalked)" />
    </ItemGroup>
  </Target>

  <!--
    One directory per call, invoked on this file through the MSBuild task.
    GetAttributes reads the entry itself (a junction or symlink reports
    ReparsePoint); a link is reported and NEVER listed. Children are joined on
    NUL (impossible in a name on every OS), escaped, then split, so a ';' in a
    directory name survives; Char.ConvertFromUtf32 is used because
    Convert.ToChar(0) binds to the string overload and yields '0'.
  -->
  <Target Name="_OkfWalkDir" Returns="@(_OkfWalkResult)">
    <PropertyGroup>
      <_OkfAttrs>$([System.IO.File]::GetAttributes('$(_OkfDir)'))</_OkfAttrs>
      <_OkfIsLink>false</_OkfIsLink>
      <_OkfIsLink Condition="$(_OkfAttrs.Contains('ReparsePoint'))">true</_OkfIsLink>
      <_OkfNul>$([System.Char]::ConvertFromUtf32(0))</_OkfNul>
      <_OkfChildren Condition="'$(_OkfIsLink)' != 'true'">$([MSBuild]::Escape($([System.String]::Join($(_OkfNul), $([System.IO.Directory]::GetDirectories($(_OkfDir)))))).Split($(_OkfNul)))</_OkfChildren>
    </PropertyGroup>
    <ItemGroup>
      <_OkfWalkResult Include="$([MSBuild]::Escape($(_OkfDir)))" OkfIsLink="$(_OkfIsLink)" />
      <_OkfChildDir Condition="'$(_OkfIsLink)' != 'true'" Include="$(_OkfChildren)" />
      <_OkfWalkProject Include="$(MSBuildThisFileFullPath)"
                       AdditionalProperties="_OkfDir=$([MSBuild]::Escape('%(_OkfChildDir.Identity)'))"
                       Condition="'@(_OkfChildDir)' != ''" />
    </ItemGroup>
    <MSBuild Condition="'@(_OkfWalkProject)' != ''" Projects="@(_OkfWalkProject)" Targets="_OkfWalkDir">
      <Output TaskParameter="TargetOutputs" ItemName="_OkfWalkResult" />
    </MSBuild>
  </Target>

</Project>
```

- [ ] **Step 3: Write the child-build helper**

`tests/OKF4net.Tests/Providers/EmbeddedResources/EmbeddedBuild.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.Loader;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers.EmbeddedResources;

/// <summary>
/// Builds a throwaway class library that imports the package's .targets at NuGet's own import point:
/// <c>obj/probe.csproj.okf.targets</c> is matched by the same wildcard import as
/// <c>obj/probe.csproj.nuget.g.targets</c> (<c>$(MSBuildProjectExtensionsPath)$(MSBuildProjectFile).*.targets</c>,
/// in <c>Microsoft.Common.targets</c>), which restore does not rewrite. Importing after <c>Sdk.targets</c>
/// would come after <c>Directory.Build.targets</c> and see a late OkfBundle the real package does not (spec §6.2).
/// The xunit run cannot execute MSBuild in-process; these child builds are the .targets' executable guard.
/// </summary>
internal static class EmbeddedBuild
{
    public sealed record Result(int ExitCode, string Output, string AssemblyPath);

    public static string TargetsPath { get; } = Path.Combine(
        TestPaths.RepoRoot(), "src", "OKF4net.Providers.EmbeddedResources", "build", "OKF4net.Providers.EmbeddedResources.targets");

    public static Result Build(string projectDir, string itemsXml, string? directoryBuildTargetsXml = null, string verbosity = "minimal")
    {
        Directory.CreateDirectory(Path.Combine(projectDir, "obj"));
        File.WriteAllText(Path.Combine(projectDir, "probe.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Deterministic>true</Deterministic>
                <AssemblyName>probe</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
            {itemsXml}
              </ItemGroup>
            </Project>
            """);
        // An ITEM captures what is declared before this import; a property would expand @() lazily, at use.
        File.WriteAllText(Path.Combine(projectDir, "obj", "probe.csproj.okf.targets"), $"""
            <Project>
              <ItemGroup><_OkfSeenAtImport Include="@(OkfBundle)" /></ItemGroup>
              <Target Name="_OkfReportSeen" BeforeTargets="PrepareForBuild">
                <Message Importance="high" Text="SEEN_AT_IMPORT=@(_OkfSeenAtImport)" />
              </Target>
              <Import Project="{TargetsPath}" />
            </Project>
            """);
        if (directoryBuildTargetsXml is not null)
        {
            File.WriteAllText(Path.Combine(projectDir, "Directory.Build.targets"), directoryBuildTargetsXml);
        }

        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet";
        var start = new ProcessStartInfo(host)
        {
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "build", "probe.csproj", "-nologo", "-v:" + verbosity, "-nodeReuse:false", "-p:UseSharedCompilation=false", "--disable-build-servers" })
        {
            start.ArgumentList.Add(arg);
        }

        // The test host runs under an SDK whose MSBuild variables must not leak into the child build.
        foreach (var name in new[] { "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildLoadMicrosoftTargetsReadOnly" })
        {
            start.Environment.Remove(name);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet build did not finish within 5 minutes:\n" + stdout.Result);
        }

        return new Result(process.ExitCode, stdout.Result + stderr.Result, Path.Combine(projectDir, "bin", "Debug", "net10.0", "probe.dll"));
    }

    /// <summary>Every <c>okf:</c> resource of the assembly, read from a collectible context (the file is never locked).</summary>
    public static SortedDictionary<string, byte[]> ReadOkfResources(string assemblyPath)
    {
        var context = new AssemblyLoadContext("okf-probe-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var file = File.OpenRead(assemblyPath);
            var assembly = context.LoadFromStream(file);
            var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("okf:", StringComparison.Ordinal)))
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                result.Add(name, copy.ToArray());
            }

            return result;
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>The bundle as <see cref="FileSystemBundleProvider"/> lists it (links skipped), each name with the file's bytes.</summary>
    public static SortedDictionary<string, byte[]> ReadFileSystemView(string bundleDir)
    {
        var provider = new FileSystemBundleProvider(bundleDir);
        var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var listing = provider.EnumerateAsync(null, BundleLimits.Default, CancellationToken.None).GetAsyncEnumerator();
        try
        {
            while (listing.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var raw = listing.Current.RawName;
                result.Add(raw, File.ReadAllBytes(Path.Combine(bundleDir, raw.Replace('/', Path.DirectorySeparatorChar))));
            }
        }
        finally
        {
            listing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return result;
    }
}
```

(The filesystem provider completes synchronously, so `GetResult()` never waits.)

- [ ] **Step 4: Write the `.targets` tests**

`tests/OKF4net.Tests/Providers/EmbeddedResources/TargetsBuildTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests.Providers.EmbeddedResources;

/// <summary>
/// The .targets of OKF4net.Providers.EmbeddedResources, run by real child builds (spec §5, §6.2).
/// xunit cannot execute MSBuild; these builds are the .targets' only executable guard besides the
/// AOT CI job, so every case here asserts on the built assembly or on a named error code.
/// </summary>
public class TargetsBuildTests
{
    private const string Bundle = """    <OkfBundle Include="knowledge/" Prefix="kb" />""";

    private static readonly string[] CommonNames =
    [
        "a.md", "a.b.c.md", "glossary.fr.md", "x.en-US.md", "sp ace.md", "pct%25.md", "pct%3B.md",
        "$(P).md", "@(I).md", "%(M).md", "q'uote.md", "c,omma.md", "par(en).md", "hash#.md", "amp&.md",
        "brack[et].md", "tilde~.md", ".hidden", ".dir/inner.md", "étoile.md", "emoji-\U0001F600.md",
        "l.licx", "t.restext", "sub;semi/deep/b.md", "nested/one/two/three.md",
    ];

    private static readonly string[] UnixOnlyNames =
    [
        "trailingdot.", " lead.md", "trail .md", "nfd-café.md", "star*.md", "q?.md", "co:lon.md", "lt<gt>.md",
    ];

    private static void WriteTree(string root, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "content of " + name + "\n");
        }

        File.WriteAllText(Path.Combine(root, "x.cs"), "this is not C#\n");   // must not be compiled
        File.WriteAllText(Path.Combine(root, "r.resx"), "<not xml\n");      // must not reach ResGen
    }

    [Fact]
    public void Embeds_every_file_byte_for_byte_under_the_names_the_filesystem_provider_lists()
    {
        using var tmp = new TempDir();
        var knowledge = Path.Combine(tmp.Path, "knowledge");
        WriteTree(knowledge, OperatingSystem.IsWindows() ? CommonNames : CommonNames.Concat(UnixOnlyNames));

        var result = EmbeddedBuild.Build(tmp.Path, Bundle);

        Assert.True(result.ExitCode == 0, result.Output);
        var expected = EmbeddedBuild.ReadFileSystemView(knowledge);
        var embedded = EmbeddedBuild.ReadOkfResources(result.AssemblyPath);
        Assert.Equal(expected.Keys.Select(k => "okf:kb/" + k), embedded.Keys);
        foreach (var (name, bytes) in expected)
        {
            Assert.Equal(bytes, embedded["okf:kb/" + name]);
        }

        // No satellite assembly: glossary.fr.md and x.en-US.md stayed in the main assembly.
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(result.AssemblyPath)!));
    }

    [Fact]
    public void Is_inert_without_OkfBundle()
    {
        using var tmp = new TempDir();
        tmp.Write("notes/a.md", "a\n");
        tmp.Write("Class1.cs", "namespace Probe; public static class Class1 { }\n");

        var result = EmbeddedBuild.Build(tmp.Path, "");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Empty(EmbeddedBuild.ReadOkfResources(result.AssemblyPath));
    }

    [Fact]
    public void Accepts_a_directory_without_trailing_slash_and_two_bundles()
    {
        using var tmp = new TempDir();
        WriteTree(Path.Combine(tmp.Path, "knowledge"), ["a.md"]);
        tmp.Write("other/o.md", "o\n");

        var result = EmbeddedBuild.Build(tmp.Path, """
                <OkfBundle Include="knowledge" Prefix="kb" />
                <OkfBundle Include="other" Prefix="docs" />
            """);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Equal(["okf:docs/o.md", "okf:kb/a.md", "okf:kb/r.resx", "okf:kb/x.cs"], EmbeddedBuild.ReadOkfResources(result.AssemblyPath).Keys);
    }

    [Theory]
    [InlineData("""<OkfBundle Include="knowledge/" />""")]
    [InlineData("""<OkfBundle Include="knowledge/" Prefix="" />""")]
    [InlineData("""<OkfBundle Include="knowledge/" Prefix="a/b" />""")]
    public void Rejects_an_invalid_prefix(string item)
    {
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");

        var result = EmbeddedBuild.Build(tmp.Path, item);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB001", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_a_missing_or_empty_directory()
    {
        using var tmp = new TempDir();
        var missing = EmbeddedBuild.Build(tmp.Path, """<OkfBundle Include="nowhere/" Prefix="kb" />""");
        Assert.Contains("OKFEMB002", missing.Output, StringComparison.Ordinal);

        Directory.CreateDirectory(Path.Combine(tmp.Path, "empty"));
        var empty = EmbeddedBuild.Build(tmp.Path, """<OkfBundle Include="empty/" Prefix="kb" />""");
        Assert.Contains("OKFEMB002", empty.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_prefixes_equal_ignoring_case()
    {
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("other/o.md", "o\n");

        var result = EmbeddedBuild.Build(tmp.Path, """
                <OkfBundle Include="knowledge/" Prefix="kb" />
                <OkfBundle Include="other/" Prefix="KB" />
            """);

        Assert.Contains("OKFEMB003", result.Output, StringComparison.Ordinal);
    }

    [SkippableTheory]
    [InlineData("dq\".md")]
    [InlineData("pi|pe.md")]
    [InlineData("back\\slash.md")]
    [InlineData("ctl\u0001.md")]
    public void Rejects_a_name_an_assembly_cannot_hold(string name)
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows cannot create these names.");
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        File.WriteAllText(Path.Combine(tmp.Path, "knowledge", name), "x\n");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB004", result.Output, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void Rejects_case_twins()
    {
        using var tmp = new TempDir();
        tmp.Write("knowledge/Case.md", "upper\n");
        File.WriteAllText(Path.Combine(tmp.Path, "knowledge", "case.md"), "lower\n");
        Skip.If(Directory.GetFiles(Path.Combine(tmp.Path, "knowledge")).Length < 2, "This volume folds case.");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB004", result.Output, StringComparison.Ordinal);
    }

    public static TheoryData<string> LinkKinds => new() { "outward", "cycle", "dangling", "file" };

    [SkippableTheory]
    [MemberData(nameof(LinkKinds))]
    public void Refuses_a_link_without_listing_its_target(string kind)
    {
        using var tmp = new TempDir();
        using var outside = new TempDir();
        outside.Write("secret-inner-dir-marker/secret.md", "secret\n");
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("knowledge/sub/b.md", "b\n");
        var linkPath = Path.Combine(tmp.Path, "knowledge", "sub", "link");
        var created = kind switch
        {
            "outward" => TryDirectoryLink(tmp, "knowledge/sub/link", outside.Path),
            "cycle" => TryDirectoryLink(tmp, "knowledge/sub/link", Path.Combine(tmp.Path, "knowledge")),
            "dangling" => TryDirectoryLink(tmp, "knowledge/sub/link", Path.Combine(outside.Path, "gone")),
            _ => tmp.TryCreateFileSymlinkToExternalFile("knowledge/sub/link.md", Path.Combine(outside.Path, "secret-inner-dir-marker", "secret.md")),
        };
        Skip.IfNot(created, "This environment cannot create that link.");

        var result = EmbeddedBuild.Build(tmp.Path, Bundle, verbosity: "detailed");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB005", result.Output, StringComparison.Ordinal);
        // The walk never listed the link's target: its inner directory appears nowhere in the detailed log.
        Assert.DoesNotContain("secret-inner-dir-marker", result.Output, StringComparison.Ordinal);
    }

    /// <summary>A junction on Windows (no privilege needed), a directory symlink elsewhere; a dangling target is created then deleted.</summary>
    private static bool TryDirectoryLink(TempDir tmp, string relativeLink, string target)
    {
        var dangling = !Directory.Exists(target);
        if (dangling)
        {
            Directory.CreateDirectory(target);
        }

        var ok = OperatingSystem.IsWindows()
            ? tmp.TryCreateJunctionToExternalDir(relativeLink, target)
            : TryUnixDirectorySymlink(Path.Combine(tmp.Path, relativeLink.Replace('/', Path.DirectorySeparatorChar)), target);
        if (dangling)
        {
            Directory.Delete(target);
        }

        return ok;
    }

    private static bool TryUnixDirectorySymlink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [Fact]
    public void Refuses_an_OkfBundle_the_Remove_cannot_see()
    {
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");
        tmp.Write("knowledge/x.cs", "this is not C#\n");

        var result = EmbeddedBuild.Build(tmp.Path, "", directoryBuildTargetsXml: """
            <Project><ItemGroup><OkfBundle Include="knowledge/" Prefix="kb" /></ItemGroup></Project>
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("OKFEMB006", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_okf_targets_file_sits_at_NuGets_import_point()
    {
        // Spec §8 item 2: an OkfBundle from Directory.Build.targets is NOT yet declared at our import point.
        using var tmp = new TempDir();
        tmp.Write("knowledge/a.md", "a\n");

        var early = EmbeddedBuild.Build(tmp.Path, Bundle);
        Assert.Contains("SEEN_AT_IMPORT=knowledge/", early.Output, StringComparison.Ordinal);

        using var late = new TempDir();
        late.Write("knowledge/a.md", "a\n");
        var lateResult = EmbeddedBuild.Build(late.Path, "", directoryBuildTargetsXml: """
            <Project><ItemGroup><OkfBundle Include="knowledge/" Prefix="kb" /></ItemGroup></Project>
            """);
        Assert.Contains("SEEN_AT_IMPORT=\n", lateResult.Output.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }
}
```

Note on `The_okf_targets_file_sits_at_NuGets_import_point`'s late case: the build of a bundle with only `a.md` and no stray item succeeds (nothing to leave over), so the `_OkfReportSeen` message is printed; the assertion is on the empty value.

- [ ] **Step 5: Run the tests (Windows)**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~TargetsBuildTests"`
Expected: PASS on Windows, with the Unix-only cases skipped. If any fails, fix the `.targets` (never weaken an assertion); a failure of the link walk that cannot be fixed in pure MSBuild is a **stop** (Global Constraints).

- [ ] **Step 6: Run the full suite, format, commit**

Run: `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration"`, `dotnet format OKF4net.sln --verify-no-changes`.

```bash
git add src/OKF4net.Providers.EmbeddedResources OKF4net.sln tests/OKF4net.Tests/OKF4net.Tests.csproj tests/OKF4net.Tests/Providers/EmbeddedResources
git commit -m "feat(embedded): the OkfBundle .targets and its child-build tests (#141)

Pure MSBuild: a recursive MSBuild-task walk that refuses links without
descending into them, names from FullPath, OKFEMB001-006, inert without
OkfBundle. Child builds import it at NuGet's own import point.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

- [ ] **Step 7: GATE — Linux and macOS**

Ask the maintainer for permission to push `feat/embedded-resource-provider` and open a **draft** PR against `dev` (CI runs on pull requests, on `ubuntu-latest`, `windows-latest`, `macos-latest`). Once granted:

```bash
git push -u origin feat/embedded-resource-provider
gh pr create --draft --base dev --title "Embedded-resource bundle provider (#141)" --body "Draft: Task 1 gate of docs/superpowers/plans/2026-11-12-embedded-resource-bundle-provider.md (the .targets on Linux and macOS).

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
gh pr checks --watch
```

Expected: `build & test` green on all three OSes, with `TargetsBuildTests` run (not skipped) for the link cases on Linux and macOS.

Spec §8 item 3 (the evaluation-time `Remove` must not walk the disk) is settled by construction, record it in the PR description: a `Remove` glob is matched against items that already exist, and the items it removes come from the SDK's own default globs (`Microsoft.NET.Sdk.DefaultItems.props`), which enumerate the project tree at evaluation whether or not this package is referenced. The package adds no evaluation-time enumeration; the cycle case of `Refuses_a_link_without_listing_its_target` shows evaluation terminates. If the walk fails there (a symlink not reported as `ReparsePoint`, a dangling link that throws in `GetAttributes`, an escaping problem), try a fix **in pure MSBuild** once; if it still fails, **stop and report to the maintainer** with the CI log — D2 reopens (spec §5.1). Do not start Task 2 before this gate is green.

---

### Task 2: `EmbeddedResourceBundleProvider`

**Files:**
- Modify: `src/OKF4net/OKF4net.csproj` (add `<InternalsVisibleTo Include="OKF4net.Providers.EmbeddedResources" />` beside the others)
- Create: `src/OKF4net.Providers.EmbeddedResources/EmbeddedResourceBundleProvider.cs`
- Modify: `tests/OKF4net.Tests/OKF4net.Tests.csproj` (test bundles + import at the end of the body)
- Create: `tests/OKF4net.Tests/Providers/EmbeddedResources/Assets/contract/index.md`, `notes/a.md`, `notes/b.md`, `notes/a-much-longer-name.md`, `refs/q.sql`; `Assets/contract2/one.md`; `Assets/stray.txt`
- Test: `tests/OKF4net.Tests/Providers/EmbeddedResources/EmbeddedResourceBundleProviderTests.cs`

**Interfaces:**
- Consumes: `EmbeddedBuild.Build` / `Result.AssemblyPath` (Task 1); `OKF4net.Internal.SyncAsyncEnumerable.Create<T>(Func<IEnumerable<T>>, CancellationToken)` (internal to `OKF4net`).
- Produces (public): `namespace OKF4net.Providers.EmbeddedResources; public sealed class EmbeddedResourceBundleProvider : IBundleProvider` with `EmbeddedResourceBundleProvider(Assembly assembly, string prefix)` and `static IReadOnlyList<string> GetBundlePrefixes(Assembly assembly)`; `public const string ResourceNamePrefix = "okf:"`. Test bundles embedded in `OKF4net.Tests` under prefixes `contract`, `contract2`, `names`.

- [ ] **Step 1: Add the test bundles**

Create the asset files (exact content):

`Assets/contract/index.md`:
```markdown
# contract

* [A](notes/a.md) - The first note.
```

`Assets/contract/notes/a.md`:
```markdown
---
type: Note
title: A
description: The first note.
---

Links to [B](b.md).
```

`Assets/contract/notes/b.md`:
```markdown
---
type: Note
title: B
description: The second note.
---

B.
```

`Assets/contract/notes/a-much-longer-name.md`:
```markdown
---
type: Note
title: Long
description: A note whose key is 26 bytes long.
---

Long.
```

`Assets/contract/refs/q.sql`:
```sql
SELECT 1
```

`Assets/contract2/one.md`:
```markdown
---
type: Note
title: One
description: In the contract2 bundle.
---

One.
```

`Assets/stray.txt`: `stray` (one line).

Then the `names` bundle: create `tests/OKF4net.Tests/Providers/EmbeddedResources/Assets/names/` holding, each with content `content of <name>` + newline: `a.b.c.md`, `glossary.fr.md`, `x.en-US.md`, `sp ace.md`, `pct%25.md`, `pct%3B.md`, `$(P).md`, `@(I).md`, `%(M).md`, `q'uote.md`, `c,omma.md`, `par(en).md`, `étoile.md`, `.hidden`, `.dir/inner.md`, `l.licx`, `t.restext`, `nested/one/two.md`; plus `x.cs` = `this is not C#` and `r.resx` = `<not xml`. (Nothing NFD, no trailing dot, nothing Windows refuses: spec §6.2.)

In `tests/OKF4net.Tests/OKF4net.Tests.csproj`, add before `</Project>`:

```xml
  <!--
    Embedded test bundles (spec §6.1-§6.3). The .targets is imported at the END
    of the body, after these items, so its evaluation-time Remove sees them —
    the names/ bundle holds a deliberately invalid x.cs and r.resx, and this
    project still builds only because they are neither compiled nor passed to
    ResGen. NuGet's own import order is covered by TargetsBuildTests and the
    AOT CI job.
  -->
  <ItemGroup>
    <OkfBundle Include="Providers\EmbeddedResources\Assets\contract\" Prefix="contract" />
    <OkfBundle Include="Providers\EmbeddedResources\Assets\contract2\" Prefix="contract2" />
    <OkfBundle Include="Providers\EmbeddedResources\Assets\names\" Prefix="names" />
    <!-- Not under the convention: GetBundlePrefixes must ignore them. -->
    <EmbeddedResource Include="Providers\EmbeddedResources\Assets\stray.txt" LogicalName="okf:/stray.md" WithCulture="false" Type="Non-Resx" />
    <EmbeddedResource Include="Providers\EmbeddedResources\Assets\stray.txt" LogicalName="okf:noslash" WithCulture="false" Type="Non-Resx" />
  </ItemGroup>

  <Import Project="..\..\src\OKF4net.Providers.EmbeddedResources\build\OKF4net.Providers.EmbeddedResources.targets" />
```

Run: `dotnet build OKF4net.sln` — Expected: success (proves `x.cs` is not compiled and `r.resx` not ResGen'd).

- [ ] **Step 2: Write the failing tests**

`tests/OKF4net.Tests/Providers/EmbeddedResources/EmbeddedResourceBundleProviderTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using OKF4net.Providers;
using OKF4net.Providers.EmbeddedResources;

namespace OKF4net.Tests.Providers.EmbeddedResources;

public class EmbeddedResourceBundleProviderTests
{
    private static readonly Assembly Self = typeof(EmbeddedResourceBundleProviderTests).Assembly;
    private static readonly string Assets = Path.Combine(TestPaths.RepoRoot(), "tests", "OKF4net.Tests", "Providers", "EmbeddedResources", "Assets");

    private static EmbeddedResourceBundleProvider Contract() => new(Self, "contract");

    private static List<string> Names(IBundleProvider p, BundleKey? prefix = null)
    {
        var names = new List<string>();
        var e = p.EnumerateAsync(prefix, BundleLimits.Default, CancellationToken.None).GetAsyncEnumerator();
        while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            names.Add(e.Current.RawName);
        }

        e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return names;
    }

    [Fact]
    public void Lists_its_own_prefix_only_sorted_ordinally()
    {
        Assert.Equal(["index.md", "notes/a-much-longer-name.md", "notes/a.md", "notes/b.md", "refs/q.sql"], Names(Contract()));
        Assert.Equal(["one.md"], Names(new EmbeddedResourceBundleProvider(Self, "contract2")));
    }

    [Fact]
    public void A_key_prefix_filters_at_a_segment_boundary()
    {
        Assert.Equal(["notes/a-much-longer-name.md", "notes/a.md", "notes/b.md"], Names(Contract(), BundleKey.Parse("notes")));
        Assert.Empty(Names(Contract(), BundleKey.Parse("note")));
    }

    [Fact]
    public async Task Reads_the_bytes_of_the_file_with_its_length()
    {
        using var obj = await Contract().ReadAsync(BundleKey.Parse("refs/q.sql"), null, CancellationToken.None);
        using var copy = new MemoryStream();
        await obj.Content.CopyToAsync(copy);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Assets, "contract", "refs", "q.sql")), copy.ToArray());
        Assert.Equal(copy.Length, obj.Length);
    }

    [Fact]
    public void Exists_is_exact_and_ordinal()
    {
        var p = Contract();
        Assert.True(p.ExistsAsync(BundleKey.Parse("notes/a.md"), CancellationToken.None).Result);
        Assert.False(p.ExistsAsync(BundleKey.Parse("Notes/a.md"), CancellationToken.None).Result);
        Assert.False(p.ExistsAsync(BundleKey.Parse("notes"), CancellationToken.None).Result); // a prefix is not an object
    }

    [Fact]
    public void A_missing_object_and_a_stale_expected_version_fault_the_task()
    {
        var p = Contract();
        var missing = p.ReadAsync(BundleKey.Parse("nope.md"), null, CancellationToken.None);
        Assert.True(missing.IsFaulted);
        Assert.IsType<BundleObjectNotFoundException>(missing.AsTask().Exception!.InnerException);

        var stale = p.ReadAsync(BundleKey.Parse("index.md"), new BundleObjectVersion("other"), CancellationToken.None);
        Assert.IsType<BundleVersionMismatchException>(stale.AsTask().Exception!.InnerException);

        var version = p.GetVersionAsync(BundleLimits.Default, CancellationToken.None).Result;
        using var ok = p.ReadAsync(BundleKey.Parse("index.md"), new BundleObjectVersion(version.Token), CancellationToken.None).Result;
        Assert.Equal(version.Token, ok.Version.Token);
    }

    [Fact]
    public void Writes_and_deletes_are_refused_as_completed_faults()
    {
        var p = Contract();
        var write = p.WriteAsync(BundleKey.Parse("index.md"), "x"u8.ToArray(), BundleWriteCondition.None, CancellationToken.None);
        var delete = p.DeleteAsync(BundleKey.Parse("index.md"), BundleWriteCondition.None, CancellationToken.None);
        Assert.True(write.IsFaulted);
        Assert.True(delete.IsFaulted);
        Assert.IsType<BundleReadOnlyException>(write.AsTask().Exception!.InnerException);
        Assert.IsType<BundleReadOnlyException>(delete.AsTask().Exception!.InnerException);
    }

    [Fact]
    public async Task A_cancelled_token_cancels_through_the_task()
    {
        // Same contract as AsyncProviderContractTests pins for the filesystem provider.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var read = Contract().ReadAsync(BundleKey.Parse("index.md"), null, cts.Token);
        Assert.True(read.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Contract().EnumerateAsync(null, BundleLimits.Default, cts.Token))
            {
            }
        });
    }

    [Fact]
    public void Declares_its_capabilities_constraints_identity_and_version()
    {
        var p = Contract();
        var mvid = Self.ManifestModule.ModuleVersionId.ToString("N");
        Assert.False(p.Capabilities.CanWrite);
        Assert.True(p.Capabilities.CompletesSynchronously);
        Assert.Equal(BundleKeyConstraints.WellFormedUnicode, p.NativeConstraints);
        Assert.Equal($"embedded:{Self.FullName}/{mvid}/contract", p.Id);
        Assert.Equal($"embedded:{Self.GetName().Name}/contract", p.Location);
        Assert.Equal("mvid:" + mvid, p.GetVersionAsync(BundleLimits.Default, CancellationToken.None).Result.Token);
        Assert.Equal(p.Id, Contract().Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("nope")]
    [InlineData("contrac")]
    public void An_invalid_or_unknown_prefix_throws(string prefix)
    {
        Assert.Throws<ArgumentException>(() => new EmbeddedResourceBundleProvider(Self, prefix));
    }

    [Fact]
    public void A_dynamic_assembly_is_refused()
    {
        var dynamic = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("okf-dynamic"), AssemblyBuilderAccess.Run);
        Assert.Throws<ArgumentException>(() => new EmbeddedResourceBundleProvider(dynamic, "kb"));
        Assert.Throws<ArgumentException>(() => EmbeddedResourceBundleProvider.GetBundlePrefixes(dynamic));
    }

    [Fact]
    public void GetBundlePrefixes_lists_the_conventional_prefixes_and_ignores_strays()
    {
        var prefixes = EmbeddedResourceBundleProvider.GetBundlePrefixes(Self);
        Assert.Contains("contract", prefixes);
        Assert.Contains("contract2", prefixes);
        Assert.Contains("names", prefixes);
        Assert.DoesNotContain("", prefixes);
        Assert.DoesNotContain("noslash", prefixes);
        Assert.Equal(prefixes.Order(StringComparer.Ordinal), prefixes);
        Assert.Equal(prefixes.Distinct(StringComparer.Ordinal).Count(), prefixes.Count);
    }

    [Fact]
    public void Every_operation_of_a_load_completes_synchronously()
    {
        var tracked = new CompletionTrackingProvider(Contract());
        var bundle = Bundle.Load(tracked);
        Assert.Equal(4, bundle.Concepts.Count);
        Assert.True(tracked.Observed > 3);
        Assert.Empty(tracked.Incomplete);
    }

    [Fact]
    public void An_over_long_key_is_a_local_problem_not_a_load_failure()
    {
        // "notes/a-much-longer-name.md" is 27 bytes; the bound is 16. The filesystem provider behaves the same way.
        var bundle = Bundle.Load(Contract(), new BundleLoadOptions { Limits = new BundleLimits { MaxKeyBytes = 16 } });
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal("notes/a-much-longer-name.md", problem.RawName);
        Assert.Equal(3, bundle.Concepts.Count);
    }

    [Fact]
    public void Two_builds_of_the_same_name_with_different_content_have_distinct_ids_and_one_build_loaded_twice_keeps_its_id()
    {
        using var first = new TempDir();
        first.Write("knowledge/a.md", "one\n");
        var a = EmbeddedBuild.Build(first.Path, """    <OkfBundle Include="knowledge/" Prefix="kb" />""");
        Assert.True(a.ExitCode == 0, a.Output);
        using var second = new TempDir();
        second.Write("knowledge/a.md", "two\n");
        var b = EmbeddedBuild.Build(second.Path, """    <OkfBundle Include="knowledge/" Prefix="kb" />""");
        Assert.True(b.ExitCode == 0, b.Output);

        var contexts = new List<AssemblyLoadContext>();
        try
        {
            Assembly Load(string path)
            {
                var context = new AssemblyLoadContext("okf-id-" + contexts.Count, isCollectible: true);
                contexts.Add(context);
                return context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
            }

            var pa = new EmbeddedResourceBundleProvider(Load(a.AssemblyPath), "kb");
            var pa2 = new EmbeddedResourceBundleProvider(Load(a.AssemblyPath), "kb");
            var pb = new EmbeddedResourceBundleProvider(Load(b.AssemblyPath), "kb");
            Assert.Equal(pa.Id, pa2.Id);
            Assert.NotEqual(pa.Id, pb.Id);
            Assert.NotEqual(
                pa.GetVersionAsync(BundleLimits.Default, CancellationToken.None).Result,
                pb.GetVersionAsync(BundleLimits.Default, CancellationToken.None).Result);
        }
        finally
        {
            contexts.ForEach(c => c.Unload());
        }
    }
}
```

- [ ] **Step 3: Run to see them fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~EmbeddedResourceBundleProviderTests"`
Expected: build FAILS — `EmbeddedResourceBundleProvider` does not exist.

- [ ] **Step 4: Implement the provider**

In `src/OKF4net/OKF4net.csproj`, beside `<InternalsVisibleTo Include="OKF4net.Catalog" />`, add:

```xml
    <!-- Reuses Internal/SyncAsyncEnumerable rather than forking a second synchronous async-enumerable. -->
    <InternalsVisibleTo Include="OKF4net.Providers.EmbeddedResources" />
```

`src/OKF4net.Providers.EmbeddedResources/EmbeddedResourceBundleProvider.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using OKF4net.Internal;

namespace OKF4net.Providers.EmbeddedResources;

/// <summary>
/// A read-only bundle provider over an assembly's manifest resources (#141, spec of 2026-11-12 §4):
/// the file <c>&lt;rel&gt;</c> of a bundle embedded under <c>Prefix="kb"</c> is the resource
/// <c>okf:kb/&lt;rel&gt;</c>, which the <c>OkfBundle</c> item of this package's <c>.targets</c> produces.
/// <para>The content cannot change while the process runs, so one token derived from the manifest
/// module's MVID versions the bundle and every object. Every operation returns an already-completed
/// task, so the provider serves the library's synchronous entry points. It applies no bound itself:
/// it has no walk, and the core applies <see cref="BundleLimits"/> to what it lists.</para>
/// <para><see cref="Location"/> is for display only (<c>embedded:&lt;assembly&gt;/&lt;prefix&gt;</c>): never
/// a path to open. Supported: run-time assemblies (loaded from a file, a single-file bundle, a Native AOT
/// image or bytes) and resources embedded in their manifest module; a dynamic assembly is refused.</para>
/// </summary>
public sealed class EmbeddedResourceBundleProvider : IBundleProvider
{
    /// <summary>The scheme every bundle resource name starts with.</summary>
    public const string ResourceNamePrefix = "okf:";

    private readonly Assembly _assembly;
    private readonly string _resourcePrefix;
    private readonly string[] _names;
    private readonly HashSet<string> _known;
    private readonly BundleObjectVersion _version;

    /// <summary>Serves the bundle embedded in <paramref name="assembly"/> under <paramref name="prefix"/>.</summary>
    /// <param name="assembly">A run-time (not dynamic) assembly.</param>
    /// <param name="prefix">The <c>Prefix</c> of the <c>OkfBundle</c> item: non-empty, no <c>/</c>.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The assembly is dynamic, the prefix is empty or contains <c>/</c>, or no resource is named <c>okf:&lt;prefix&gt;/…</c>.</exception>
    public EmbeddedResourceBundleProvider(Assembly assembly, string prefix)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentNullException.ThrowIfNull(prefix);
        RejectDynamic(assembly);
        if (prefix.Length == 0 || prefix.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("A bundle prefix is non-empty and contains no '/'.", nameof(prefix));
        }

        _assembly = assembly;
        _resourcePrefix = ResourceNamePrefix + prefix + "/";
        _names = assembly.GetManifestResourceNames()
            .Where(n => n.Length > _resourcePrefix.Length && n.StartsWith(_resourcePrefix, StringComparison.Ordinal))
            .Select(n => n[_resourcePrefix.Length..])
            .ToArray();
        Array.Sort(_names, StringComparer.Ordinal);
        var name = assembly.GetName().Name;
        if (_names.Length == 0)
        {
            throw new ArgumentException(
                $"The assembly '{name}' holds no resource named '{_resourcePrefix}…': declare <OkfBundle Include=\"…\" Prefix=\"{prefix}\" /> in its project.",
                nameof(prefix));
        }

        _known = new HashSet<string>(_names, StringComparer.Ordinal);
        var mvid = assembly.ManifestModule.ModuleVersionId.ToString("N");
        _version = new BundleObjectVersion("mvid:" + mvid);
        Id = $"embedded:{assembly.FullName}/{mvid}/{prefix}";
        Location = $"embedded:{name}/{prefix}";
    }

    /// <summary>The prefixes of the bundles embedded in <paramref name="assembly"/>, distinct, sorted ordinally. A name <c>okf:</c> with no <c>/</c> or an empty prefix is ignored.</summary>
    /// <exception cref="ArgumentException">The assembly is dynamic.</exception>
    public static IReadOnlyList<string> GetBundlePrefixes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        RejectDynamic(assembly);
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourceNamePrefix, StringComparison.Ordinal))
            .Select(n => n[ResourceNamePrefix.Length..])
            .Select(rest => rest.IndexOf('/', StringComparison.Ordinal) is var slash and > 0 ? rest[..slash] : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public string Location { get; }

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities { get; } = new(CanWrite: false, CompletesSynchronously: true);

    /// <summary>Only <see cref="BundleKeyConstraints.WellFormedUnicode"/>: names are UTF-8 in the metadata, and lookup is ordinal and case-sensitive, with no normalization.</summary>
    public BundleKeyConstraints NativeConstraints => BundleKeyConstraints.WellFormedUnicode;

    /// <inheritdoc/>
    public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new BundleVersion(_version.Token));
        }
        catch (Exception e)
        {
            return ValueTask.FromException<BundleVersion>(e);
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) =>
        SyncAsyncEnumerable.Create(() => EnumerateCore(prefix), ct);

    private IEnumerable<BundleListing> EnumerateCore(BundleKey? prefix)
    {
        var start = prefix?.ToString();
        foreach (var name in _names)
        {
            if (start is null || name.StartsWith(start + "/", StringComparison.Ordinal))
            {
                yield return new BundleListing(name, null, _version);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var name = key.ToString();
            if (!_known.Contains(name))
            {
                throw new BundleObjectNotFoundException(key);
            }

            if (expected is { } wanted && wanted != _version)
            {
                throw new BundleVersionMismatchException(key);
            }

            var stream = _assembly.GetManifestResourceStream(_resourcePrefix + name)
                ?? throw new BundleObjectNotFoundException(key);
            return ValueTask.FromResult(new BundleObject(stream, _version, stream.CanSeek ? stream.Length : null));
        }
        catch (Exception e)
        {
            return ValueTask.FromException<BundleObject>(e);
        }
    }

    /// <inheritdoc/>
    public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_known.Contains(key.ToString()));
        }
        catch (Exception e)
        {
            return ValueTask.FromException<bool>(e);
        }
    }

    /// <inheritdoc/>
    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException<BundleObjectVersion>(new BundleReadOnlyException(Id));

    /// <inheritdoc/>
    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException(new BundleReadOnlyException(Id));

    private static void RejectDynamic(Assembly assembly)
    {
        if (assembly.IsDynamic)
        {
            throw new ArgumentException("A dynamic assembly has no manifest resources to serve.", nameof(assembly));
        }
    }
}
```

The `try { ct.ThrowIfCancellationRequested(); … } catch (Exception e) { return ValueTask.FromException(e); }` shape is exactly `FileSystemBundleProvider`'s: a cancelled token yields a completed task that throws `OperationCanceledException` when awaited, which is what `A_cancelled_token_cancels_through_the_task` asserts (as `AsyncProviderContractTests` does for the filesystem). `SyncAsyncEnumerable` honours the token on `MoveNextAsync`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~EmbeddedResourceBundleProviderTests"`
Expected: PASS.

- [ ] **Step 6: Full suite, format, commit**

Run the three verification commands (Global Constraints). `ObsoleteMembersTests` must stay green (the new assembly declares no `[Obsolete]` member).

```bash
git add src/OKF4net/OKF4net.csproj src/OKF4net.Providers.EmbeddedResources tests/OKF4net.Tests
git commit -m "feat(embedded): EmbeddedResourceBundleProvider (#141)

Read-only, synchronous, versioned by the manifest module's MVID, Id
carrying the MVID, WellFormedUnicode only, no provider-side bound.

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: D7 — the path-based `[Obsolete]` members refuse a non-filesystem provider

**Files:**
- Modify: `src/OKF4net/Bundle.cs` (`TryResolveResource` at the top of its body, ~line 1039; `ReadResourceText(string)` at the top of its body, ~line 1150)
- Test: `tests/OKF4net.Tests/Providers/ObsoletePathMembersTests.cs`

**Interfaces:**
- Consumes: `InMemoryBundleProvider` (tests), `EmbeddedResourceBundleProvider` (Task 2), `Bundle.Provider`.
- Produces: `NotSupportedException` from both members when `Provider is not FileSystemBundleProvider`; filesystem behaviour unchanged (the existing `FrontmatterResourceTests` pin it).

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;
using OKF4net.Providers.EmbeddedResources;

namespace OKF4net.Tests.Providers;

/// <summary>
/// D7 (spec of 2026-11-12 §4.4): the two path-based [Obsolete] members read through System.IO from
/// <see cref="Bundle.Root"/>, which for a provider other than the filesystem is a display string that
/// can name an ordinary relative directory (on Linux, "embedded:App/kb" under the current directory).
/// They refuse such a provider, like <c>BundleConceptWriter.BundleRoot</c> already does.
/// </summary>
#pragma warning disable CS0618 // the members under test are obsolete
public class ObsoletePathMembersTests
{
    public static TheoryData<string> Providers => new() { "memory", "embedded" };

    private static Bundle Load(string kind) => kind == "memory"
        ? Bundle.Load(new InMemoryBundleProvider().Add("notes/a.md", "---\ntype: Note\ntitle: A\ndescription: a.\n---\n\nA\n"))
        : Bundle.Load(new EmbeddedResourceBundleProvider(typeof(ObsoletePathMembersTests).Assembly, "contract"));

    [Theory]
    [MemberData(nameof(Providers))]
    public void TryResolveResource_refuses_a_non_filesystem_provider(string kind)
    {
        var bundle = Load(kind);
        var concept = bundle.Concepts[0];
        var e = Assert.Throws<NotSupportedException>(() => bundle.TryResolveResource(concept, "./x.sql", out _, out _));
        Assert.Contains("TryResolveResourceKey", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void ReadResourceText_by_path_refuses_a_non_filesystem_provider(string kind)
    {
        var bundle = Load(kind);
        var e = Assert.Throws<NotSupportedException>(() => bundle.ReadResourceText("x.sql"));
        Assert.Contains("ReadResourceText(BundleKey)", e.Message, StringComparison.Ordinal);
    }
}
#pragma warning restore CS0618
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ObsoletePathMembersTests"`
Expected: FAIL (no exception, or a different one).

- [ ] **Step 3: Implement**

In `src/OKF4net/Bundle.cs`, add a private helper near the two members:

```csharp
    /// <summary>D7: the path-based members have no meaning over a provider other than the filesystem (its Root is a display string).</summary>
    private void RequireFileSystemForPathMembers()
    {
        if (Provider is not FileSystemBundleProvider)
        {
            throw new NotSupportedException(
                "This bundle is not served by a FileSystemBundleProvider, so its Root is a display string, not a path: use Bundle.TryResolveResourceKey, then Bundle.ReadResourceText(BundleKey).");
        }
    }
```

and make it the first statement of `TryResolveResource(Concept, string, out string?, out ResourceResolutionStatus)` and of `ReadResourceText(string)`. (`out` parameters: the throw precedes any assignment, which C# allows for a throwing path.) Add to both members' XML docs: `/// <exception cref="NotSupportedException">The bundle's provider is not a <see cref="FileSystemBundleProvider"/>.</exception>`.

- [ ] **Step 4: Run the tests and the existing filesystem ones**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ObsoletePathMembersTests|FullyQualifiedName~FrontmatterResourceTests|FullyQualifiedName~NoResidualFileIoTests|FullyQualifiedName~ObsoleteMembersTests"`
Expected: PASS. If `NoResidualFileIoTests` calls either member over its `InMemoryBundleProvider`, it now gets `NotSupportedException`: update that row to assert the refusal (it is the new contract), never remove the row.

- [ ] **Step 5: Full suite, format, commit**

```bash
git add src/OKF4net/Bundle.cs tests/OKF4net.Tests
git commit -m "fix(core): path-based obsolete resource members refuse non-filesystem providers (D7, #141)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Read-only consumers and no residual file I/O

**Files:**
- Test: `tests/OKF4net.Tests/Providers/EmbeddedResources/EmbeddedConsumersTests.cs`

**Interfaces:**
- Consumes: `EmbeddedResourceBundleProvider` over `contract` (Task 2); `BundleConceptWriter(IBundleProvider, Action?, BundleLoadOptions?)`; `IndexGenerator.RegenerateIndexes(IBundleProvider, BundleLoadOptions?)`; `OkfBundleTools(BundleHandle, AttestationOrchestrator?)`, `GetTools(OkfToolMode)`, `ReadConcept`, `Search`, `Browse`; `BundleValidator.Validate(Bundle, IOkfClock?)`; `ConceptAudit.Run(Bundle, AuditQuery, IOkfClock?)`; `ConceptSearch.Search(IEnumerable<Concept>, string, string?)`; `CurrentDirectoryCollection.Name` (from `NoResidualFileIoTests.cs`).
- Produces: nothing new in `src/`.

`tools.ReadConcept(string)`, `tools.Search(string, string?)`, `tools.Browse(string?)` and `tools.WriteConcept(string, string, string)` are `OkfBundleTools`' public `[Obsolete]` `string` bridges for a synchronous provider (hence the `CS0618` pragma); `writer.AppendLog(string, string, DateOnly?)` is `BundleConceptWriter`'s. The current-directory switch follows `NoResidualFileIoTests` (same collection, restored in `finally`).

- [ ] **Step 1: Write the tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Providers.EmbeddedResources;

namespace OKF4net.Tests.Providers.EmbeddedResources;

/// <summary>Spec §4.4, §6.1: every consumer sees a read-only provider; nothing reaches the disk.</summary>
#pragma warning disable CS0618 // OkfBundleTools' string methods are obsolete bridges, still public entry points
[Collection(CurrentDirectoryCollection.Name)]
public class EmbeddedConsumersTests
{
    private const string Fm = "type: Note\ntitle: New\ndescription: A new note.";
    private static EmbeddedResourceBundleProvider Contract() => new(typeof(EmbeddedConsumersTests).Assembly, "contract");

    [Fact]
    public void The_writer_and_the_index_generator_refuse_before_attempting()
    {
        var writer = new BundleConceptWriter(Contract());
        Assert.Throws<BundleReadOnlyException>(() => writer.WriteConcept("notes/c", Fm, "C\n"));
        Assert.Throws<BundleReadOnlyException>(() => writer.AppendLog("Update", "x"));
        Assert.Throws<BundleReadOnlyException>(() => IndexGenerator.RegenerateIndexes(Contract()));
    }

    [Fact]
    public void The_tool_set_offers_no_write_tool_and_its_writes_refuse()
    {
        using var handle = new BundleHandle(Contract());
        var tools = new OkfBundleTools(handle);
        var names = tools.GetTools(OkfToolMode.ReadWrite).Select(t => t.Name).ToArray();
        Assert.Contains("okf_read_concept", names);
        Assert.DoesNotContain("okf_write_concept", names);
        Assert.Throws<BundleReadOnlyException>(() => tools.WriteConcept("notes/c", Fm, "C\n"));
    }

    [Fact]
    public void Every_read_entry_point_leaves_an_empty_current_directory_empty()
    {
        using var empty = new TempDir();
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(empty.Path);
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
            var bundle = Bundle.Load(Contract());
            _ = BundleValidator.Validate(bundle, clock);
            _ = ConceptAudit.Run(bundle, default, clock);
            Assert.NotEmpty(ConceptSearch.Search(bundle.Concepts, "note"));
            Assert.True(bundle.TryResolveResourceKey(bundle.Concepts.First(c => c.Id.ToString() == "notes/a"), "/refs/q.sql", out var key, out _));
            Assert.Equal("SELECT 1\n", bundle.ReadResourceText(key!).Replace("\r\n", "\n"));

            using var handle = new BundleHandle(Contract());
            var tools = new OkfBundleTools(handle);
            Assert.Contains("Links to", tools.ReadConcept("notes/a"), StringComparison.Ordinal);
            Assert.Contains("notes/a", tools.Search("first"), StringComparison.Ordinal);
            Assert.StartsWith("# (bundle root)", tools.Browse(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(empty.Path));
    }
}
```

- [ ] **Step 2: Run**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~EmbeddedConsumersTests"`
Expected: PASS (these exercise existing guards; a failure is a real defect in a consumer, investigate it in `src/`).

- [ ] **Step 3: Full suite, format, commit**

```bash
git add tests/OKF4net.Tests/Providers/EmbeddedResources/EmbeddedConsumersTests.cs
git commit -m "test(embedded): read-only consumers and no residual file I/O (#141)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Filesystem/embedded parity (the ticket's acceptance)

**Files:**
- Modify: `tests/OKF4net.Tests/OKF4net.Tests.csproj` (embed the fixture bundles)
- Create: `tests/OKF4net.Tests/Providers/EmbeddedResources/CapturingProvider.cs`
- Test: `tests/OKF4net.Tests/Providers/EmbeddedResources/ParityTests.cs`

**Interfaces:**
- Consumes: Task 2's provider; `Bundle` members `OkfVersion`, `IndexKeys`, `LogKeys`, `ParseProblems` (`BundleParseError(RawName, Key, Error)`), `PortabilityIssues` (`BundlePortabilityIssue(Key, Reason)`), `RejectedKeys`, `Concepts` (`Concept(Id, Path, Document)` + `Key`), `LinksFrom(ConceptId)`, `ExecutableResources` (`ExecutableResourceSurvey(Computation, Attester)`, `ExecutableResource(Field, RawPath, State, Key, Version)`), `ReadResourceText(BundleKey, BundleObjectVersion?, out BundleObjectVersion)`; `Diagnostic(Severity, Path, Concept, Message, Code, Field)` + `Key`; `AuditReport` (`EvaluatedAt`, `AsOf`, `ConceptCount`, `TrustCounts`, `StatusCounts`, `StaleCount`, `Findings`), `AuditFinding` (record struct with `Path`, init `Key`); `ScoredConcept(Concept, Score)`; `IndexGenerator.RegenerateIndexes(IBundleProvider, BundleLoadOptions?)`.
- Produces: `CapturingProvider(IBundleProvider inner)` with `IReadOnlyDictionary<string, byte[]> Written` (test-only).

- [ ] **Step 1: Embed the fixtures**

In the `OkfBundle` item group of `tests/OKF4net.Tests/OKF4net.Tests.csproj` (Task 2), add:

```xml
    <!-- Parity (spec §6.3): embedded, never edited. -->
    <OkfBundle Include="..\fixtures\okf_v02\" Prefix="fixture-okf_v02" />
    <OkfBundle Include="..\fixtures\okf_v02_computation\" Prefix="fixture-okf_v02_computation" />
    <OkfBundle Include="..\fixtures\okf_v02_reserved\" Prefix="fixture-okf_v02_reserved" />
    <OkfBundle Include="..\fixtures\appendix_a\" Prefix="fixture-appendix_a" />
    <OkfBundle Include="..\..\bundles\acme_retail\" Prefix="acme_retail" />
```

Run: `dotnet build OKF4net.sln`. Expected: success. An `OKFEMB004`/`005` here means a fixture holds a name or link the embedding cannot hold: stop and report it (fixtures are not edited).

- [ ] **Step 2: Write the capturing provider**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers.EmbeddedResources;

/// <summary>Delegates reads to <paramref name="inner"/> and captures writes in memory: IndexGenerator has no public render-without-writing API (spec §6.3), and no fixture may be written.</summary>
internal sealed class CapturingProvider(IBundleProvider inner) : IBundleProvider
{
    private readonly SortedDictionary<string, byte[]> _written = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Written => _written;

    public string Id => "capture:" + inner.Id;

    public string Location => inner.Location;

    public BundleProviderCapabilities Capabilities => new(CanWrite: true, CompletesSynchronously: true);

    public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

    public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => inner.GetVersionAsync(limits, ct);

    public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) => inner.EnumerateAsync(prefix, limits, ct);

    public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => inner.ReadAsync(key, expected, ct);

    public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => inner.ExistsAsync(key, ct);

    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct)
    {
        _written[key.ToString()] = content.ToArray();
        return ValueTask.FromResult(new BundleObjectVersion("captured:" + _written.Count));
    }

    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct)
    {
        _written[key.ToString()] = [];
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 3: Write the parity tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;
using OKF4net.Providers.EmbeddedResources;

namespace OKF4net.Tests.Providers.EmbeddedResources;

/// <summary>
/// #141 acceptance (spec §6.3): each bundle, loaded through FileSystemBundleProvider and through the
/// embedded provider, gives the same objects, load facts, concepts, links, diagnostics, audit, search,
/// executable-resource survey and generated indexes. Display paths (Concept.Path, Diagnostic.Path,
/// AuditFinding.Path) and version tokens (SHA-256 vs MVID) necessarily differ and are left out; a message
/// that embeds the display root is compared with each side's Location replaced by "&lt;root&gt;".
/// </summary>
public class ParityTests
{
    private static readonly IOkfClock Clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

    public static TheoryData<string, string> Bundles => new()
    {
        { "fixture-okf_v02", "tests/fixtures/okf_v02" },
        { "fixture-okf_v02_computation", "tests/fixtures/okf_v02_computation" },
        { "fixture-okf_v02_reserved", "tests/fixtures/okf_v02_reserved" },
        { "fixture-appendix_a", "tests/fixtures/appendix_a" },
        { "acme_retail", "bundles/acme_retail" },
    };

    private static (IBundleProvider Fs, IBundleProvider Emb) Providers(string prefix, string relative) => (
        new FileSystemBundleProvider(Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar))),
        new EmbeddedResourceBundleProvider(typeof(ParityTests).Assembly, prefix));

    private static SortedDictionary<string, byte[]> Objects(IBundleProvider p)
    {
        var result = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var e = p.EnumerateAsync(null, BundleLimits.Default, CancellationToken.None).GetAsyncEnumerator();
        while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            using var obj = p.ReadAsync(BundleKey.Parse(e.Current.RawName), null, CancellationToken.None).Result;
            using var copy = new MemoryStream();
            obj.Content.CopyTo(copy);
            result.Add(e.Current.RawName, copy.ToArray());
        }

        e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return result;
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void Same_keys_and_bytes(string prefix, string relative)
    {
        var (fs, emb) = Providers(prefix, relative);
        var a = Objects(fs);
        var b = Objects(emb);
        Assert.Equal(a.Keys, b.Keys);
        foreach (var (key, bytes) in a)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(b[key]), key);
        }
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void Same_load_facts_concepts_and_links(string prefix, string relative)
    {
        var (fs, emb) = Providers(prefix, relative);
        var a = Bundle.Load(fs);
        var b = Bundle.Load(emb);
        Assert.Equal(a.OkfVersion, b.OkfVersion);
        Assert.Equal(a.IndexKeys, b.IndexKeys);
        Assert.Equal(a.LogKeys, b.LogKeys);
        Assert.Equal(a.ParseProblems.Select(p => (p.RawName, p.Key, p.Error)), b.ParseProblems.Select(p => (p.RawName, p.Key, p.Error)));
        Assert.Equal(a.PortabilityIssues, b.PortabilityIssues);
        Assert.Equal(a.RejectedKeys.Order(), b.RejectedKeys.Order());
        Assert.Equal(a.Concepts.Select(c => (c.Key, c.Id, c.Document)), b.Concepts.Select(c => (c.Key, c.Id, c.Document)));
        foreach (var c in a.Concepts)
        {
            Assert.Equal(a.LinksFrom(c.Id), b.LinksFrom(c.Id));
            Assert.Equal(a.Backlinks(c.Id), b.Backlinks(c.Id));
        }

        Assert.Equal(a.BrokenLinks(), b.BrokenLinks());
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void Same_diagnostics_audit_and_search(string prefix, string relative)
    {
        var (fs, emb) = Providers(prefix, relative);
        var a = Bundle.Load(fs);
        var b = Bundle.Load(emb);

        static object Diag(Diagnostic d, string location) =>
            (d.Severity, d.Key, d.Concept, d.Field, d.Code, d.Message.Replace(location, "<root>", StringComparison.Ordinal));
        Assert.Equal(
            BundleValidator.Validate(a, Clock).Diagnostics.Select(d => Diag(d, fs.Location)),
            BundleValidator.Validate(b, Clock).Diagnostics.Select(d => Diag(d, emb.Location)));

        var auditA = ConceptAudit.Run(a, default, Clock);
        var auditB = ConceptAudit.Run(b, default, Clock);
        Assert.Equal(auditA.EvaluatedAt, auditB.EvaluatedAt);
        Assert.Equal(auditA.AsOf, auditB.AsOf);
        Assert.Equal(auditA.ConceptCount, auditB.ConceptCount);
        Assert.Equal(auditA.TrustCounts.OrderBy(k => k.Key), auditB.TrustCounts.OrderBy(k => k.Key));
        Assert.Equal(auditA.StatusCounts.OrderBy(k => k.Key), auditB.StatusCounts.OrderBy(k => k.Key));
        Assert.Equal(auditA.StaleCount, auditB.StaleCount);
        Assert.Equal(
            auditA.Findings.Select(f => (f with { Path = string.Empty }, f.Key)),
            auditB.Findings.Select(f => (f with { Path = string.Empty }, f.Key)));

        foreach (var query in new[] { "data", "customer", "order", "revenue", "the" })
        {
            Assert.Equal(
                ConceptSearch.Search(a.Concepts, query).Select(s => (s.Concept.Id, s.Score)),
                ConceptSearch.Search(b.Concepts, query).Select(s => (s.Concept.Id, s.Score)));
        }
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void Same_executable_resources_read_pinned_to_each_sides_version(string prefix, string relative)
    {
        var (fs, emb) = Providers(prefix, relative);
        var a = Bundle.Load(fs);
        var b = Bundle.Load(emb);

        static IEnumerable<(ConceptId, ExecutableResource)> Flat(Bundle bundle) => bundle.ExecutableResources
            .OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal)
            .SelectMany(kv => new[] { (kv.Key, kv.Value.Computation), (kv.Key, kv.Value.Attester) });

        Assert.Equal(
            Flat(a).Select(x => (x.Item1, x.Item2 with { Version = null })),
            Flat(b).Select(x => (x.Item1, x.Item2 with { Version = null })));

        foreach (var ((_, ra), (_, rb)) in Flat(a).Zip(Flat(b)))
        {
            if (ra.State == ExecutableResourceState.Pinned)
            {
                Assert.Equal(
                    a.ReadResourceText(ra.Key!, ra.Version, out _),
                    b.ReadResourceText(rb.Key!, rb.Version, out _));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Bundles))]
    public void Same_generated_indexes(string prefix, string relative)
    {
        var (fs, emb) = Providers(prefix, relative);
        var a = new CapturingProvider(fs);
        var b = new CapturingProvider(emb);
        IndexGenerator.RegenerateIndexes(a);
        IndexGenerator.RegenerateIndexes(b);
        Assert.Equal(a.Written.Keys, b.Written.Keys);
        foreach (var (key, bytes) in a.Written)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(b.Written[key]), key);
        }
    }
}
```

(`ExecutableResourceState.Pinned` and the positional `ExecutableResource(Field, RawPath, State, Key, Version)` record are as declared in `src/OKF4net/ExecutableResources.cs`.) If a `Diagnostic.Message` embeds a display path **below** the root with OS separators, the `<root>` replacement already normalizes it, since both sides use `Path.DirectorySeparatorChar` after the location (spec §4.3, `Location` row).

- [ ] **Step 4: Run**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ParityTests"`
Expected: PASS for all five bundles. A difference is a real defect (provider, or a consumer reading the display path): investigate on the C# side, never relax the projection beyond what the spec lists.

- [ ] **Step 5: Full suite, format, commit**

```bash
git add tests/OKF4net.Tests
git commit -m "test(embedded): filesystem/embedded parity over the fixtures and acme_retail (#141)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Native AOT probe and its CI job

**Files:**
- Create: `tests/EmbeddedBundleAotProbe/EmbeddedBundleAotProbe.csproj`
- Create: `tests/EmbeddedBundleAotProbe/Program.cs`
- Modify: `.github/workflows/ci.yml` (new job after `aot-publish`)

**Interfaces:**
- Consumes: the `OKF4net` and `OKF4net.Providers.EmbeddedResources` **packages** at version `$(OkfProbeVersion)`; `EmbeddedResourceBundleProvider`, `FileSystemBundleProvider`, `Bundle.Load`, `BundleValidator.Validate`, `ConceptAudit.Run`, `FixedClock`.
- Produces: a summary on stdout, identical for `probe` (embedded) and `probe --filesystem <dir>`.

- [ ] **Step 1: Write the probe**

`tests/EmbeddedBundleAotProbe/EmbeddedBundleAotProbe.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Not in OKF4net.sln: it consumes the REAL packages (so the real .targets, at
    NuGet's import point), packed by ci.yml's "embedded bundle AOT smoke test"
    job at a run-unique version passed as OkfProbeVersion. Spec §6.4.
  -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="OKF4net.Providers.EmbeddedResources" Version="$(OkfProbeVersion)" />
  </ItemGroup>

  <ItemGroup>
    <OkfBundle Include="..\..\bundles\acme_retail\" Prefix="acme_retail" />
  </ItemGroup>

</Project>
```

`tests/EmbeddedBundleAotProbe/Program.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
// Prints a deterministic summary of acme_retail, read from this binary's own resources, or with
// "--filesystem <dir>" from disk; ci.yml compares the two (spec §6.4). Fixed clock: no calendar drift.
using OKF4net;
using OKF4net.Providers;
using OKF4net.Providers.EmbeddedResources;

var clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
IBundleProvider provider = args is ["--filesystem", var dir]
    ? new FileSystemBundleProvider(dir)
    : new EmbeddedResourceBundleProvider(typeof(Marker).Assembly, "acme_retail");

var bundle = Bundle.Load(provider);
var report = BundleValidator.Validate(bundle, clock);
var audit = ConceptAudit.Run(bundle, default, clock);

Console.WriteLine($"concepts={bundle.Concepts.Count}");
foreach (var key in bundle.Concepts.Select(c => c.Key!.ToString()).Order(StringComparer.Ordinal))
{
    Console.WriteLine($"concept {key}");
}

foreach (var line in report.Diagnostics
    .Select(d => $"diagnostic {d.Severity} {d.Code} {d.Key} {d.Field}")
    .Order(StringComparer.Ordinal))
{
    Console.WriteLine(line);
}

Console.WriteLine($"audit concepts={audit.ConceptCount} stale={audit.StaleCount}");
foreach (var (tier, count) in audit.TrustCounts.OrderBy(k => k.Key))
{
    Console.WriteLine($"audit trust {tier}={count}");
}

return 0;

/// <summary>Names this assembly without relying on the generated Program type.</summary>
internal sealed class Marker;
```

- [ ] **Step 2: Try it locally (Windows)**

```powershell
$v = "0.7.0-local.1"; $feed = "$env:TEMP\okf-probe-feed"; $env:NUGET_PACKAGES = "$env:TEMP\okf-probe-packages"
dotnet pack src/OKF4net/OKF4net.csproj -c Release -o $feed -p:Version=$v
dotnet pack src/OKF4net.Providers.EmbeddedResources/OKF4net.Providers.EmbeddedResources.csproj -c Release -o $feed -p:Version=$v
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="okf-probe" value="$feed" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
"@ | Set-Content "$env:TEMP\okf-probe-nuget.config"
dotnet restore tests/EmbeddedBundleAotProbe -r win-x64 -p:PublishAot=true -p:OkfProbeVersion=$v --configfile "$env:TEMP\okf-probe-nuget.config"
dotnet publish tests/EmbeddedBundleAotProbe -c Release -r win-x64 -p:PublishAot=true -p:OkfProbeVersion=$v --no-restore -o "$env:TEMP\okf-probe-out"
```

Expected: publish succeeds (needs the MSVC linker for AOT on Windows; if absent, publish with `-p:PublishAot=false -p:PublishSingleFile=true --self-contained` instead to exercise the package and `.targets`, and leave AOT to CI). Then run the binary from an empty directory and with `--filesystem bundles/acme_retail`; the two outputs must be identical and start with `concepts=` followed by a non-zero count. Remove `$env:NUGET_PACKAGES` afterwards.

- [ ] **Step 3: Add the CI job**

In `.github/workflows/ci.yml`, after the `aot-publish` job, add:

```yaml
  # Spec of 2026-11-12 §6.4 (#141): the embedded provider and its .targets as a
  # host really consumes them -- the core and the provider PACKED at a
  # run-unique version, restored from a local feed (plus nuget.org for the AOT
  # compiler and runtime packs), published Native AOT, run from an empty
  # directory, and compared with the same binary reading the bundle from disk.
  embedded-aot:
    name: embedded bundle AOT smoke test (${{ matrix.rid }})
    strategy:
      fail-fast: false
      matrix:
        include:
          - os: ubuntu-latest
            rid: linux-x64
          - os: macos-latest
            rid: osx-arm64
          - os: windows-latest
            rid: win-x64
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v7

      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x

      # A version no feed can already hold, so restore cannot take a published core.
      - name: Derive a CI-only version and an isolated package cache
        shell: pwsh
        run: |
          $declared = (Select-String -Path Directory.Build.props -Pattern '<Version>([^<]*)</Version>').Matches[0].Groups[1].Value
          echo "PROBE_VERSION=$declared-ci.${{ github.run_id }}.${{ github.run_attempt }}" >> $env:GITHUB_ENV
          echo "PROBE_FEED=${{ runner.temp }}/okf-probe-feed" >> $env:GITHUB_ENV
          echo "NUGET_PACKAGES=${{ runner.temp }}/okf-probe-packages" >> $env:GITHUB_ENV

      - name: Pack the core and the provider
        shell: pwsh
        run: |
          foreach ($project in 'src/OKF4net/OKF4net.csproj', 'src/OKF4net.Providers.EmbeddedResources/OKF4net.Providers.EmbeddedResources.csproj') {
            dotnet pack $project -c Release -o $env:PROBE_FEED -p:Version=$env:PROBE_VERSION -p:ContinuousIntegrationBuild=true
            if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed: $project" }
          }

      - name: Restore the probe from the packages, check the resolved versions, publish Native AOT
        shell: pwsh
        run: |
          $config = Join-Path $env:RUNNER_TEMP 'okf-probe-nuget.config'
          @"
          <?xml version="1.0" encoding="utf-8"?>
          <configuration>
            <packageSources>
              <clear />
              <add key="okf-probe" value="$env:PROBE_FEED" />
              <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
            </packageSources>
          </configuration>
          "@ | Set-Content $config
          $props = @('-r', '${{ matrix.rid }}', '-p:PublishAot=true', "-p:OkfProbeVersion=$env:PROBE_VERSION")
          dotnet restore tests/EmbeddedBundleAotProbe/EmbeddedBundleAotProbe.csproj --configfile $config @props
          if ($LASTEXITCODE -ne 0) { throw "restore failed" }
          $assets = Get-Content tests/EmbeddedBundleAotProbe/obj/project.assets.json -Raw | ConvertFrom-Json
          foreach ($id in 'OKF4net', 'OKF4net.Providers.EmbeddedResources') {
            if (-not $assets.libraries.PSObject.Properties["$id/$env:PROBE_VERSION"]) { throw "$id was not resolved at $env:PROBE_VERSION" }
          }
          dotnet publish tests/EmbeddedBundleAotProbe/EmbeddedBundleAotProbe.csproj -c Release --no-restore -o probe-out @props
          if ($LASTEXITCODE -ne 0) { throw "publish failed" }

      - name: The embedded run, from an empty directory, matches the filesystem run
        shell: pwsh
        run: |
          $exe = Join-Path (Resolve-Path probe-out) ('EmbeddedBundleAotProbe' + $(if ('${{ matrix.rid }}'.StartsWith('win-')) { '.exe' } else { '' }))
          $empty = Join-Path $env:RUNNER_TEMP 'okf-probe-empty'
          New-Item -ItemType Directory -Force -Path $empty | Out-Null
          Push-Location $empty
          $embedded = @(& $exe)
          $code = $LASTEXITCODE
          Pop-Location
          if ($code -ne 0) { throw "embedded run exited $code" }
          if (@(Get-ChildItem -Force $empty).Count -ne 0) { throw "the embedded run wrote into the current directory" }
          $filesystem = @(& $exe --filesystem (Resolve-Path bundles/acme_retail).Path)
          if ($LASTEXITCODE -ne 0) { throw "filesystem run exited $LASTEXITCODE" }
          if (-not ($embedded[0] -match '^concepts=[1-9]')) { throw "no concept loaded: $($embedded[0])" }
          $diff = Compare-Object $embedded $filesystem -SyncWindow 0
          if ($diff) { $diff | Format-Table -AutoSize | Out-String | Write-Host; throw "embedded and filesystem summaries differ" }
          Write-Host "OK: $($embedded.Count) summary lines identical, $($embedded[0])"
```

- [ ] **Step 4: Commit and push to the draft PR**

```bash
git add tests/EmbeddedBundleAotProbe .github/workflows/ci.yml
git commit -m "ci(embedded): Native AOT probe consuming the real packages (#141)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
gh pr checks --watch
```

Expected: `embedded bundle AOT smoke test` green on `linux-x64`, `osx-arm64`, `win-x64`, alongside the existing jobs. (The push goes to the draft PR the maintainer authorised in Task 1; if they did not, ask again.)

---

### Task 7: Release step and documentation

**Files:**
- Modify: `.github/workflows/release.yml` (pack step)
- Modify: `src/OKF4net.Providers.EmbeddedResources/README.md` (full package README)
- Modify: `README.md` (root: `## Projects & packages` table + a subsection after `### Writing through a provider`)
- Modify: `CHANGELOG.md` (`## [Unreleased]` → `### Added`, `### Changed`)
- Modify: `CLAUDE.md` (hard rule + architecture bullet)

- [ ] **Step 1: Release pack step**

In `.github/workflows/release.yml`, after the `Pack (Catalog)` step, add:

```yaml
      - name: Pack (Providers.EmbeddedResources)
        run: >
          dotnet pack src/OKF4net.Providers.EmbeddedResources/OKF4net.Providers.EmbeddedResources.csproj -c Release -o artifacts
          -p:Version=$VERSION
          -p:ContinuousIntegrationBuild=true
```

The `Push to nuget.org` step already pushes `artifacts/*.nupkg` by glob: nothing else to change there.

- [ ] **Step 2: Package README**

Replace `src/OKF4net.Providers.EmbeddedResources/README.md` with:

````markdown
# OKF4net.Providers.EmbeddedResources

Ship an [OKF](https://github.com/jchable/okf4net) knowledge bundle **inside** your .NET assembly — a single-file or Native AOT agent, a `dotnet tool`, a test assembly — with no directory deployed beside it, and no way for the bundle to drift from the binary you tested.

## Embed

```xml
<ItemGroup>
  <PackageReference Include="OKF4net.Providers.EmbeddedResources" Version="x.y.z" />
  <OkfBundle Include="knowledge/" Prefix="kb" />
</ItemGroup>
```

Every file under `knowledge/` becomes the manifest resource `okf:kb/<relative path>`, byte for byte. The bundle's files are removed from the default `Compile`, `EmbeddedResource` and `Content` items (a `.cs` sanctioned script is never compiled into your assembly, a `.resx` never passed to ResGen); they stay `None` items, visible in the IDE. Declare `OkfBundle` in the project file itself: one declared in `Directory.Build.targets` or created by a target is refused (`OKFEMB006`). Several bundles can share an assembly under distinct prefixes. Without an `OkfBundle` item the package's `.targets` does nothing.

If you set `CopyToOutputDirectory`/`CopyToPublishDirectory` on the bundle's files yourself, they are also copied beside the binary — which works, but defeats the purpose.

## Read

```csharp
using OKF4net;
using OKF4net.Providers.EmbeddedResources;

var provider = new EmbeddedResourceBundleProvider(typeof(App).Assembly, "kb");
var bundle = await Bundle.LoadAsync(provider);
```

The provider is read-only (every write throws `BundleReadOnlyException`), completes every operation synchronously (the library's synchronous entry points work), and versions everything by the assembly's MVID. `EmbeddedResourceBundleProvider.GetBundlePrefixes(assembly)` lists the bundles an assembly carries.

`provider.Location` (`embedded:<assembly>/<prefix>`) and every display path derived from it (`Concept.Path`, `Diagnostic.Path`) are for display only: read through keys (`Bundle.TryResolveResourceKey`, `Bundle.ReadResourceText(BundleKey)`). The obsolete path-based members throw `NotSupportedException` on such a bundle.

Supported assemblies: those loaded at run time from a file, a single-file bundle, a Native AOT image or bytes (`Assembly.Load(byte[])`), with resources embedded in their manifest module. A dynamic assembly is refused (`ArgumentException`); linked resources and multi-module assemblies are not supported.

## Build errors

| Code | Meaning |
|---|---|
| `OKFEMB001` | `Prefix` missing, empty, or containing `/`. |
| `OKFEMB002` | The directory does not exist, or holds no file. |
| `OKFEMB003` | Two `OkfBundle` items share a prefix, compared ignoring case. |
| `OKFEMB004` | A file name an assembly cannot hold: `\` (outside Windows), `"`, `|`, a control character, or two files that differ only by case. |
| `OKFEMB005` | A symbolic link or junction under the bundle directory (or the directory itself). Links are refused, never followed: a link to `~/.ssh` must not end up in a published binary. |
| `OKFEMB006` | A bundle file is still a `Compile`, `Content` or `EmbeddedResource` item: `OkfBundle` was declared too late. |

## In a knowledge catalog

`OKF4net.Catalog` reads `catalog.json` sources through `IBundleProviderFactory`. Until a built-in factory exists ([#236](https://github.com/jchable/okf4net/issues/236)), register your own:

```csharp
sealed class EmbeddedFactory(Assembly assembly) : IBundleProviderFactory
{
    public string ProviderType => "embedded";
    public IReadOnlySet<string> AcceptedSettings { get; } = new HashSet<string> { "prefix" };
    public IBundleProvider Create(KnowledgeCatalogSource source) =>
        new EmbeddedResourceBundleProvider(assembly, source.Settings["prefix"]);
}
```

Pass the assembly explicitly: never let a manifest choose what code is loaded.

## License

LGPL-3.0-or-later. No third-party code.
````

(`KnowledgeCatalogSource.Settings` is a `ProviderSettings`, an `IReadOnlyDictionary<string, string>`: the indexer above is correct.)

- [ ] **Step 3: Root README**

In `README.md` `## Projects & packages`, add a row for `OKF4net.Providers.EmbeddedResources` in the same format as the `OKF4net.Catalog` row (one line: "A bundle shipped inside your assembly: the `OkfBundle` MSBuild item and a read-only provider; BCL only."). After the `### Writing through a provider` section, add:

````markdown
### A bundle inside your assembly

`OKF4net.Providers.EmbeddedResources` embeds a bundle at build time and serves it back at run time, Native AOT and single-file included:

```xml
<OkfBundle Include="knowledge/" Prefix="kb" />
```

```csharp
var bundle = await Bundle.LoadAsync(new EmbeddedResourceBundleProvider(typeof(App).Assembly, "kb"));
```

The bundle is read-only, one manifest resource per file (`okf:kb/<path>`); links under the directory, and names an assembly cannot hold, fail the build with a named `OKFEMB00x` error. See the package README.
````

- [ ] **Step 4: CHANGELOG**

Under `## [Unreleased]` → `### Added`, add:

```markdown
- **Embedded-resource bundle provider (#141).** New package
  `OKF4net.Providers.EmbeddedResources`: the `OkfBundle` MSBuild item (a
  pure-MSBuild `.targets` shipped in the package) embeds a bundle directory one
  manifest resource per file, `okf:<prefix>/<path>`, refusing links and names an
  assembly cannot hold (`OKFEMB001`–`OKFEMB006`), and
  `EmbeddedResourceBundleProvider` serves it back read-only, synchronously,
  versioned by the assembly's MVID — Native AOT and single-file included.
```

Under `### Changed`, add:

```markdown
- **`Bundle.TryResolveResource(…)` and `Bundle.ReadResourceText(string)`**
  (both `[Obsolete]`) now throw `NotSupportedException` when the bundle's
  provider is not a `FileSystemBundleProvider`: its `Root` is then a display
  string, which on Linux could resolve under the current directory. Use
  `TryResolveResourceKey` and `ReadResourceText(BundleKey)`.
```

- [ ] **Step 5: CLAUDE.md**

In the hard rule "Zero third-party runtime dependencies, per project", add `src/OKF4net.Providers.EmbeddedResources/` to the BCL-only list (`… src/OKF4net.Catalog/, src/OKF4net.Attestation/, and src/OKF4net.Providers.EmbeddedResources/: BCL only …`) and append to that bullet: `OKF4net.Providers.EmbeddedResources references only OKF4net; its build/*.targets is pure MSBuild (no task assembly, no inline task).`

In the "A green xunit run says nothing about code xunit cannot execute" rule, add a sub-bullet:

```markdown
  - **The same applies to `src/OKF4net.Providers.EmbeddedResources/build/*.targets`.** Its executable guards are `TargetsBuildTests` (real child `dotnet build`s, importing it at NuGet's own import point through `obj/<project>.csproj.okf.targets`, on all three CI OSes) and ci.yml's `embedded bundle AOT smoke test` job (the real packages, Native AOT). Any change to the `.targets` needs both green; never assert on its source text alone.
```

In `## Architecture`, after the `src/OKF4net.Catalog.Hosting/` bullet, add:

```markdown
- **`src/OKF4net.Providers.EmbeddedResources/`** — a bundle shipped inside an assembly (#141, spec `docs/superpowers/specs/2026-11-12-embedded-resource-bundle-provider-design.md`), referencing only `OKF4net`. Build side: `build/OKF4net.Providers.EmbeddedResources.targets` turns `<OkfBundle Include="dir/" Prefix="kb" />` into one `EmbeddedResource` per file named `okf:kb/<path>` (`WithCulture=false`, `Type=Non-Resx`, hooked `BeforeTargets="PrepareResourceNames"` — both metadata are then the only filter of `_CoreCompileResourceInputs`), after a recursive `MSBuild`-task walk that inspects every directory **before** descending and refuses any link (`OKFEMB005`; the glob `**` and `GetDirectories(…, AllDirectories)` both follow links); it is inert without an `OkfBundle` item. Run side: `EmbeddedResourceBundleProvider` is read-only, `CompletesSynchronously`, versioned by the manifest module's MVID (also in its `Id`), declares only `WellFormedUnicode`, and applies no bound itself (the core applies all three). `Location` is display only; the two path-based `[Obsolete]` `Bundle` members refuse any provider but the filesystem one (D7).
```

- [ ] **Step 6: Verify and commit**

Run the three verification commands, then `graphify update .`.

```bash
git add .github/workflows/release.yml src/OKF4net.Providers.EmbeddedResources/README.md README.md CHANGELOG.md CLAUDE.md graphify-out
git commit -m "docs(embedded): package README, root README, CHANGELOG, CLAUDE.md, release pack step (#141)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

Then report to the maintainer: the draft PR's checks, and that the PR is ready to leave draft on their word (do not mark it ready or merge without it).
