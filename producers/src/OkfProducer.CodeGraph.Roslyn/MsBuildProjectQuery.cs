// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OkfProducer.Core.Generation;

namespace OkfProducer.CodeGraph.Roslyn;

/// <summary>
/// Thrown when <see cref="MsBuildProjectQuery.Query"/> cannot obtain a project's inputs -- the
/// <c>dotnet</c> CLI is absent, the project has not been restored, MSBuild exited non-zero, or its
/// output was not the JSON document <c>-getItem</c>/<c>-getProperty</c> promise.
///
/// <para>
/// A distinct exception type rather than a bare <see cref="InvalidOperationException"/> so
/// <see cref="RoslynResolver"/> can catch exactly this, rather than catching every
/// <see cref="InvalidOperationException"/> anything downstream might raise and quietly turning a
/// genuine bug into a "project unavailable" line. Both this and
/// <see cref="UnknownLanguageVersionException"/> lead to the same outcome -- the project is reported
/// unavailable and the name-matching baseline carries it -- but they are told apart so the report can
/// name the actual cause, and so neither catch can swallow the other's failures.
/// </para>
/// </summary>
public sealed class MsBuildQueryException : Exception
{
    /// <summary>Creates the exception with a message describing what the query could not do.</summary>
    public MsBuildQueryException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the underlying failure.</summary>
    public MsBuildQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A directory this producer owns, outside the scanned repository, that MSBuild's
/// <c>IntermediateOutputPath</c> is redirected into for the length of one query stage -- and that is
/// deleted with it.
///
/// <para><b>Why a scratch directory exists at all.</b> The query asks MSBuild for the project's
/// <c>Compile</c> item set, and on an SDK-style project that set includes files the SDK
/// <i>generates</i>: <c>*.GlobalUsings.g.cs</c> and <c>*.AssemblyInfo.cs</c>, which
/// <c>GenerateGlobalUsings</c> and <c>GenerateAssemblyInfo</c> write before naming them. Roslyn has to
/// read those files, so they have to exist on disk; there is no MSBuild switch that produces the item
/// without the file. Measured on this host (SDK 10.0.204, Windows 11): with
/// <c>-p:BuildProjectReferences=false</c> and nothing else, a query over a restored, never-built
/// three-project repository still wrote four files into the scanned tree
/// (<c>App.AssemblyInfo.cs</c>, <c>App.AssemblyInfoInputs.cache</c>, <c>App.GlobalUsings.g.cs</c>,
/// <c>App.assets.cache</c>, all under <c>obj/Debug/net10.0/</c>); the design-time-build properties
/// (<c>DesignTimeBuild=true</c>, <c>SkipCompilerExecution=true</c>, <c>ProvideCommandLineArgs=true</c>)
/// wrote exactly the same four. So "resolve references without writing anything" is not available, and
/// this is the documented fallback: everything MSBuild still writes lands here instead.</para>
///
/// <para><b>The caller owns the lifetime, and it has to outlive the compilations.</b>
/// <see cref="CompilationFactory"/> reads those generated files when it parses the <c>Compile</c>
/// items, so a scratch disposed between the query and the compilation would take a project's implicit
/// global usings with it -- and a compilation missing them does not merely lose a file, it produces a
/// symbol table full of holes. That is why <see cref="MsBuildProjectQuery.Query(string, MsBuildQueryScratch)"/>
/// takes one rather than making its own: the query cannot know when its answer has been read.</para>
///
/// <para><b>Residue.</b> <see cref="Dispose"/> deletes the root recursively, best-effort -- a file
/// another process is holding must not fail a producer run that has already finished its work. A run
/// killed before it disposes (Ctrl+C, a crash) leaves one <c>okfgen-msbuild-*</c> directory in the
/// system temp directory. Nothing else would ever remove it, so <see cref="SweepStale()"/> -- called by
/// <c>RoslynResolver</c> as the stage starts -- deletes this producer's own leftovers once they are a
/// day old.</para>
///
/// <para><b>One thing in the scanned repository is not redirected: empty output directories.</b>
/// <c>PrepareForBuild</c> (<c>Microsoft.Common.CurrentVersion.targets</c>, line 1208 in SDK 10.0.204)
/// runs <c>&lt;MakeDir Directories="$(OutDir);$(IntermediateOutputPath);..."/&gt;</c>, and both
/// <c>ResolveReferences</c> and <c>GenerateAssemblyInfo</c> depend on it, so a never-built project
/// gains an empty <c>bin/&lt;Configuration&gt;/&lt;TFM&gt;/</c>. Nothing is ever written into it. It is
/// left deliberately: redirecting <c>OutDir</c> as well removes the directories, but a <c>-p:</c> switch
/// is a global property that reaches the referenced projects too, and measured, it moves their
/// <c>ReferencePath</c> identity from <c>Lib/bin/Debug/net10.0/Lib.dll</c> into the scratch -- the
/// very path <see cref="CompilationFactory"/> falls back to reading on a built tree. An empty
/// directory is the cheaper of the two.</para>
/// </summary>
public sealed class MsBuildQueryScratch : IDisposable
{
    /// <summary>The name every scratch root starts with, and the only one <see cref="SweepStale()"/> touches.</summary>
    internal const string DirectoryPrefix = "okfgen-msbuild-";

    /// <summary>
    /// How old a leftover root must be before <see cref="SweepStale()"/> removes it. A day, because the
    /// sweep cannot tell a killed run's directory from a concurrent run's live one by anything but age,
    /// and a stage lasting a day would need hundreds of projects each running into the two-minute query
    /// cap.
    /// </summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(1);

    /// <summary>A scratch rooted in the system temp directory, under a name unique to this instance.</summary>
    public MsBuildQueryScratch()
        : this(Path.Combine(Path.GetTempPath(), DirectoryPrefix + Guid.NewGuid().ToString("N")[..12]))
    {
    }

    /// <remarks>
    /// <see langword="internal"/> so the escaping tests can put the characters MSBuild treats specially
    /// -- <c>%</c> and <c>;</c> -- into the root, which is exactly what a <c>TMPDIR</c> holding them
    /// produces through the public constructor. Setting <c>TMPDIR</c> in-process instead would reach
    /// every test running in parallel. The production root comes from <see cref="Path.GetTempPath"/>;
    /// it is not an operator knob.
    /// </remarks>
    internal MsBuildQueryScratch(string root) => Root = root;

    /// <summary>
    /// The root, named but never created here: MSBuild creates <c>IntermediateOutputPath</c> and its
    /// parents itself (measured, including for a path with a space in it), so constructing a scratch
    /// touches no filesystem and has no failure mode of its own to degrade a project over.
    /// </summary>
    public string Root { get; }

    /// <summary>
    /// Where one project's intermediate output goes: a per-project subdirectory of <see cref="Root"/>,
    /// forward-slash separated and ending in a separator as MSBuild requires. This is the <i>literal</i>
    /// path; <c>MsBuildProjectQuery</c> escapes it before it becomes a <c>-p:</c> value.
    ///
    /// <para>
    /// Per-project, because two projects sharing one intermediate directory would write each other's
    /// <c>&lt;AssemblyName&gt;.AssemblyInfo.cs</c> -- and keyed by a hash of the project's absolute
    /// path rather than by its file name, because two projects in a repository may share a name.
    /// Deterministic within a run, so the multi-targeting re-query (which runs the same project again
    /// under <c>-p:TargetFramework=</c>) overwrites its own files rather than accumulating.
    /// </para>
    ///
    /// <para>
    /// Forward slashes on every platform, deliberately: a Windows path ending in <c>\</c> inside a
    /// <c>-p:</c> switch is a trailing backslash immediately before a closing quote, which is the
    /// classic Windows argument-quoting hazard. MSBuild normalises separators itself -- the
    /// <c>%(FullPath)</c> values it prints back come out in the platform's own spelling either way.
    /// </para>
    /// </summary>
    /// <param name="projectFullPath">The project's absolute path, as <c>Path.GetFullPath</c> returns it.</param>
    public string IntermediateOutputPathFor(string projectFullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectFullPath);

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(projectFullPath)))[..16];
        return (Root + Path.DirectorySeparatorChar + digest + Path.DirectorySeparatorChar).Replace('\\', '/');
    }

    /// <summary>
    /// Deletes the scratch root and everything MSBuild left in it. Best-effort: a locked or
    /// already-removed directory is not a reason to fail a run whose work is done, and the only cost
    /// of a failed delete is a directory in the system temp.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Swallowed on purpose, both of them. A file another process is still holding, or a
            // directory removed under us, must not fail a producer run that has already done its work
            // and written its bundle -- the entire cost is one directory left in the system temp,
            // which a later run's SweepStale removes. Nothing downstream reads this directory again.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes scratch roots a killed run left in the system temp directory, once they are a day old.
    /// Best-effort throughout: a sweep that fails costs nothing but the directories it could not
    /// remove, so no exception ever leaves it.
    /// </summary>
    /// <returns>How many directories were deleted.</returns>
    public static int SweepStale() => SweepStale(Path.GetTempPath(), StaleAfter, DateTime.UtcNow);

    /// <summary>
    /// <see cref="SweepStale()"/> over <paramref name="directory"/>, against an explicit clock, so a
    /// test can age a directory without waiting a day.
    ///
    /// <para><b>What it will touch, and nothing else.</b> A direct child of
    /// <paramref name="directory"/> whose name is <see cref="DirectoryPrefix"/> followed by exactly
    /// twelve lowercase hex digits -- the shape the public constructor produces, so a user's own
    /// <c>okfgen-msbuild-notes</c> is not this producer's to delete. Not a symbolic link or junction:
    /// on a shared <c>/tmp</c> another user can plant one under a matching name, and a sweep must not
    /// be what follows it. And not younger than <paramref name="olderThan"/>, judged by the newest
    /// last-write time of the root and its immediate subdirectories, since a live run creates one
    /// subdirectory per project it queries.</para>
    /// </summary>
    internal static int SweepStale(string directory, TimeSpan olderThan, DateTime utcNow)
    {
        var deleted = 0;
        try
        {
            foreach (var candidate in Directory.EnumerateDirectories(directory, DirectoryPrefix + "*"))
            {
                if (TryDeleteStale(candidate, olderThan, utcNow))
                {
                    deleted++;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The directory itself could not be listed. Nothing to sweep, and no run fails for it.
        }

        return deleted;
    }

    private static bool TryDeleteStale(string candidate, TimeSpan olderThan, DateTime utcNow)
    {
        try
        {
            var info = new DirectoryInfo(candidate);
            if (!IsOwnRootName(info.Name)
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || info.LinkTarget is not null)
            {
                return false;
            }

            var newest = info.LastWriteTimeUtc;
            foreach (var child in info.EnumerateDirectories())
            {
                if (child.LastWriteTimeUtc > newest)
                {
                    newest = child.LastWriteTimeUtc;
                }
            }

            if (utcNow - newest < olderThan)
            {
                return false;
            }

            info.Delete(recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // One directory that cannot be read or removed -- another user's on a shared /tmp, a file
            // still held -- is skipped, never a reason to stop sweeping the rest.
            return false;
        }
    }

    private static bool IsOwnRootName(string name) =>
        name.Length == DirectoryPrefix.Length + 12
        && name.StartsWith(DirectoryPrefix, StringComparison.Ordinal)
        && name.AsSpan(DirectoryPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;
}

/// <summary>
/// Reads one project's compiler inputs straight out of MSBuild, with no
/// <c>Microsoft.CodeAnalysis.Workspaces.MSBuild</c> anywhere: <c>dotnet msbuild</c>'s own
/// <c>-getItem</c>/<c>-getProperty</c> switches print exactly the item and property values a target
/// run produced, as JSON, which is all a <c>CSharpCompilation</c> needs.
///
/// <para><b>THREAT MODEL: this class executes the scanned repository's code.</b> Not "may", and not
/// only under some option -- an MSBuild evaluation <i>is</i> the execution of repository-authored
/// logic, and there is no read-only mode of it to ask for. Everything the project reaches runs, as the
/// user running <c>okfgen</c>: <c>Directory.Build.props</c> and <c>Directory.Build.targets</c> (found
/// from the project's own directory, which is why <see cref="Run"/> sets
/// <c>WorkingDirectory</c> there), every <c>Import</c> they pull in, any target hooked on
/// <c>BeforeTargets="ResolveReferences"</c>, and a <c>RoslynCodeTaskFactory</c> inline
/// <c>&lt;Code&gt;</c> task, which is a C# compiler invocation on source the repository supplies.</para>
///
/// <para><b>And the repository adds to the request itself, not merely to what runs on the way.</b>
/// This paragraph used to say the three targets in <see cref="Targets"/> bound what is <i>asked
/// for</i>. They do not. <c>dotnet msbuild</c> auto-applies a <c>Directory.Build.rsp</c> found in the
/// project's directory -- the directory <see cref="Run"/> deliberately runs in -- and what that file
/// holds is command-line switches. Measured on this host: a one-line <c>Directory.Build.rsp</c>
/// containing <c>-t:Pwn</c> made this exact query run a <c>Pwn</c> target the producer never
/// requested, which wrote its marker file, alongside the producer's own <c>-t:ResolveReferences</c>.
/// A repository can therefore turn this query into <c>-t:Build</c>, or add any other switch. Two
/// things were measured to still hold: an explicit command-line switch wins on conflict, so the
/// producer's own <c>-nodeReuse:false</c> cannot be flipped from the rsp; and the mitigation below is
/// unchanged, because it never rested on the target list. It was the enumerated <i>bound</i> that was
/// untrue, not the conclusion.</para>
///
/// <para><b>It nonetheless writes no FILE into the scanned repository (E13).</b> Two different things,
/// and the second used to be false as well. <c>-t:ResolveReferences</c> pulls in
/// <c>ResolveProjectReferences</c>, and outside Visual Studio that target <i>builds every referenced
/// project</i>, so a query of one project wrote the whole compile output of its reference closure into
/// the tree -- <c>bin/</c>, <c>obj/Debug/&lt;tfm&gt;/</c>, <c>ref/</c>, <c>refint/</c> -- for a
/// repository the producer was only asked to read. <see cref="ReadOnlySwitches"/> ends that, and what
/// MSBuild still generates for the queried project itself (its <c>Compile</c> items, which have to
/// exist for Roslyn to parse them) is redirected into a <see cref="MsBuildQueryScratch"/> the producer
/// deletes. What is left is an empty <c>bin/&lt;Configuration&gt;/&lt;TFM&gt;/</c> directory per
/// never-built project, created by <c>PrepareForBuild</c> and never written into -- see
/// <see cref="MsBuildQueryScratch"/> for why redirecting it costs more than it saves. What this does NOT
/// bound is what the repository's own MSBuild logic chooses to write while
/// it runs: a <c>Directory.Build.targets</c> hooked on <c>ResolveReferences</c> can write anything it
/// likes, anywhere, and the paragraphs above are why that is not a contradiction.</para>
///
/// <para>
/// So <b>only point <c>okfgen</c> at a repository you would be willing to build</b>. That is the whole
/// mitigation, stated rather than implied, and it is why <c>okfgen generate --no-msbuild</c> exists:
/// it skips this stage entirely, spawns no <c>dotnet msbuild</c>, and leaves call resolution to the
/// name-matching baseline. It is off by default because turning it on by default would silently
/// degrade every existing run's resolution quality -- a documented hazard with a lever beats a quiet
/// downgrade.
/// </para>
///
/// <para>
/// Nothing else in this producer <i>evaluates</i> scanned input: the tree-sitter extractor parses, and
/// <see cref="CompilationFactory"/> compiles without running generators (see
/// <see cref="RoslynResolver"/>'s remarks, whose refusal is about not widening this further rather
/// than about holding a line that this class had already crossed). It is not the only place a child
/// process runs, though, and <c>--no-msbuild</c> does not make it so: <c>GitRevision.RunGit</c> spawns
/// <c>git</c> in the scanned tree on every generate run, including that one. How many times is not a
/// constant -- <c>--rev</c> removes one invocation and <c>--check</c> adds one -- so the exact
/// breakdown lives in <c>producers/README.md</c> and <c>GenerateRun</c>'s remarks rather than as a
/// number repeated here. Far less exposure -- none of them triggers a hook, an fsmonitor, or a pager
/// with stdout redirected -- but "no process is spawned" is false and <c>producers/README.md</c> no
/// longer says it.
/// </para>
/// </summary>
public static class MsBuildProjectQuery
{
    /// <summary>
    /// How long one <c>dotnet msbuild</c> invocation may take before it is killed and the project
    /// reported unavailable. The spike measured 533-1194 ms per project on a warm SDK; two minutes is
    /// slack for a cold first run (which JITs the SDK's MSBuild) without letting a wedged process hang
    /// a producer run until <c>ExtractionLimits.Timeout</c>.
    /// </summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The exact target list the spike validated, and the reason it is longer than the obvious one.
    /// <c>ResolveReferences</c> alone yields the <c>ReferencePath</c> items but leaves the
    /// <c>Compile</c> set missing the SDK's own generated sources, <c>*.GlobalUsings.g.cs</c> and
    /// <c>*.AssemblyInfo.cs</c>. <c>ImplicitUsings</c> is on by default for SDK-style projects, so
    /// without <c>GenerateGlobalUsings</c> every file relying on an implicit global using fails to
    /// compile -- and a compilation with errors has an incomplete symbol table, which mis-attributes
    /// calls rather than merely missing them. Measured, not reasoned: adding these two targets is
    /// what took the spike's three probe projects to zero errors.
    ///
    /// <para>
    /// <c>AddImplicitDefineConstants</c> is here for a fourth reason, found later and narrower than it
    /// first looked. The suspect was <c>GenerateAssemblyInfo</c> -- it is not: measured with this exact
    /// target list against SDK 10.0.204 and SDK 9.0.318, <c>DefineConstants</c> already carries the
    /// full implicit set (<c>NET</c>, <c>NETCOREAPP</c>, <c>NET8_0</c>, every <c>NETx_OR_GREATER</c> up
    /// to the project's TFM) whether or not <c>GenerateAssemblyInfo</c> runs. The real trigger is the
    /// SDK line: SDK 8.0.425 declares <c>AddImplicitDefineConstants</c> with
    /// <c>BeforeTargets="CoreCompile"</c> (<c>Microsoft.NET.Sdk.BeforeCommon.targets</c>), and this
    /// query never runs <c>CoreCompile</c>, so on SDK 8 none of the requested targets ever populate the
    /// <c>*_OR_GREATER</c> defines -- <c>DefineConstants</c> comes back as just
    /// <c>TRACE;DEBUG;NET;NET8_0;NETCOREAPP</c>. SDK 9.0.3xx and 10 moved the same target to
    /// <c>AfterTargets="PrepareForBuild"</c> (dotnet/sdk#43908), which <c>ResolveReferences</c> already
    /// depends on, so those SDKs never showed the gap. Requesting the target explicitly closes it on
    /// SDK 8 and is a measured no-op (no duplicate defines) on SDK 9.0.3xx+ and 10, where it has
    /// already run by the time this list is evaluated. A project setting
    /// <c>DisableImplicitFrameworkDefines=true</c> is unaffected either way, since the target's own
    /// condition then skips it -- measured, not only reasoned from the target's MSBuild condition: see
    /// <c>RoslynResolverTests.A_project_disabling_implicit_framework_defines_gains_none_of_them</c>. A
    /// non-SDK-style project -- one with no <c>AddImplicitDefineConstants</c> target at all -- already
    /// fails this query at <c>GenerateGlobalUsings</c> (MSB4057) before reaching this target, so
    /// nothing new degrades.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/> rather than <see langword="private"/> so
    /// <c>RoslynResolverTests.The_msbuild_query_always_requests_the_implicit_defines_target</c> can pin
    /// this exact list on every host, not only one with an SDK 8 installed to reproduce the actual
    /// gap this target closes -- see that test, and
    /// <c>CodeGraph.Sdk8ImplicitDefinesTests</c> for the SDK-8-gated end-to-end proof, for why an
    /// always-running structural pin exists alongside it.
    /// </remarks>
    internal static readonly string[] Targets =
    [
        "-t:ResolveReferences", "-t:GenerateGlobalUsings", "-t:GenerateAssemblyInfo", "-t:AddImplicitDefineConstants",
    ];

    /// <summary>
    /// The switches that keep the query a <i>read</i> of the scanned repository rather than a build of
    /// it. One entry today, and the measurement behind it is the reason it is a list of its own rather
    /// than three more strings in <see cref="Targets"/>.
    ///
    /// <para>
    /// <c>-t:ResolveReferences</c> depends on <c>ResolveProjectReferences</c>, and outside Visual
    /// Studio <c>BuildProjectReferences</c> defaults to <see langword="true"/>, so that target
    /// <b>builds every referenced project</b> -- which is what this query used to do. Measured on this
    /// host (SDK 10.0.204, Windows 11) over a restored, never-built <c>App -> Mid -> Lib</c>
    /// repository, one query of <c>App</c> wrote <b>39 files</b> into the scanned tree, <b>34 of them
    /// into the two projects it merely references</b>: <c>Lib/bin/</c>, <c>Mid/bin/</c> and both
    /// projects' full <c>obj/Debug/net10.0/</c> compile output, <c>ref/</c> and <c>refint/</c>
    /// assemblies included. With <c>-p:BuildProjectReferences=false</c> the two
    /// referenced projects are not touched at all, and the answer is byte-for-byte the same one: 169
    /// <c>ReferencePath</c> items in both runs, the same two <c>MSBuildSourceProjectFile</c> values
    /// (including the <b>transitive</b> <c>Lib</c>, which never stopped being reported), and identical
    /// properties. Re-measured against this repository's own <c>src/OKF4net.Mcp</c>: 213 references
    /// before and after, identical <c>Identity</c> sets, identical properties.
    /// </para>
    ///
    /// <para>
    /// <c>-p:DesignTimeBuild=true</c> was measured too, and it stops the referenced-project builds just
    /// as well -- it is not chosen because it is a far broader signal: repository-authored targets
    /// routinely condition on it (<c>Condition="'$(DesignTimeBuild)' != 'true'"</c>), so it would
    /// silently change what the scanned repository's own logic does, and therefore what this query
    /// sees, to buy a property <c>BuildProjectReferences</c> already buys. <c>SkipCompilerExecution</c>
    /// and <c>ProvideCommandLineArgs</c> were measured to change nothing here at all (the compiler
    /// never runs in this target set), so they are absent rather than carried as decoration.
    /// </para>
    ///
    /// <para>
    /// <b>Deliberately NOT here: <c>BaseOutputPath</c>/<c>OutputPath</c>.</b> A <c>-p:</c> switch is a
    /// global property, and global properties propagate into the referenced projects MSBuild evaluates
    /// to answer <c>GetTargetPath</c>. Redirecting their output path would move the
    /// <c>ReferencePath</c> identities this query reports into a scratch directory, which is exactly
    /// the value <c>CompilationFactory</c> falls back to reading when a project could not be compiled
    /// from source. Nothing writes to <c>bin/</c> once the builds are gone, so there is nothing to
    /// redirect.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <see langword="internal"/> for the same reason as <see cref="Targets"/> (E6) and
    /// <see cref="Properties"/> (E7): so
    /// <c>RoslynResolverTests.The_msbuild_query_always_refuses_to_build_the_referenced_projects</c>
    /// can pin it on every host, with no <c>dotnet</c> and no restore needed.
    /// </remarks>
    internal static readonly string[] ReadOnlySwitches =
    [
        "-p:BuildProjectReferences=false",
    ];

    private static readonly string[] Items =
    [
        "-getItem:ReferencePath", "-getItem:Compile",
    ];

    /// <remarks>
    /// <see langword="internal"/> rather than <see langword="private"/> (E7 fix round 1, Minor-2) so
    /// <c>RoslynResolverTests.The_msbuild_query_always_requests_the_signing_properties</c> can pin the
    /// three signing properties this list carries on every host, with no <c>dotnet</c> and no restore
    /// needed -- mirroring exactly why <see cref="Targets"/> is <see langword="internal"/> (E6).
    /// </remarks>
    internal static readonly string[] Properties =
    [
        "-getProperty:DefineConstants", "-getProperty:LangVersion",
        "-getProperty:Nullable", "-getProperty:AllowUnsafeBlocks",
        "-getProperty:TargetFramework", "-getProperty:OutputType",
        "-getProperty:AssemblyName", "-getProperty:SignAssembly",
        "-getProperty:KeyOriginatorFile", "-getProperty:AssemblyOriginatorKeyFile",
    ];

    /// <summary>
    /// Runs the MSBuild query for <paramref name="projectPath"/> and returns its inputs.
    ///
    /// <para>
    /// A multi-targeting project is queried for one target framework: its outer build has no
    /// <c>ResolveReferences</c> target at all (MSBuild answers <c>MSB4057</c>) and no single reference
    /// set to report, so the first framework its <c>TargetFrameworks</c> lists is selected and the
    /// query re-run against that. First-listed rather than newest is a deliberate choice of a rule
    /// that is stable and readable from the project file itself: "newest" would have this producer
    /// silently change which symbols exist whenever a TFM is added.
    /// </para>
    ///
    /// <para><b>No file is written into the scanned repository.</b> The referenced projects are not
    /// built (<see cref="ReadOnlySwitches"/>) and whatever MSBuild still generates -- the
    /// <c>Compile</c> items the SDK writes before naming them -- is redirected into
    /// <paramref name="scratch"/>. Those generated files are part of the answer, so
    /// <paramref name="scratch"/> must outlive every use of the returned <see cref="ProjectInputs"/>.
    /// What the query does still create in the scanned repository is a <i>directory</i>: an empty
    /// <c>bin/&lt;Configuration&gt;/&lt;TFM&gt;/</c> per never-built project. See
    /// <see cref="MsBuildQueryScratch"/> for both, and for why that directory is left.</para>
    /// </summary>
    /// <param name="projectPath">Path to a <c>.csproj</c>; relative paths are made absolute.</param>
    /// <param name="scratch">Where MSBuild's intermediate output goes instead of the repository's <c>obj/</c>.</param>
    /// <exception cref="MsBuildQueryException">
    /// <c>dotnet</c> could not be started, the query did not finish within its timeout, MSBuild
    /// exited non-zero (an unrestored project is the common case), or its output was not parseable
    /// JSON. Every one of these means "this project's inputs are unknown", never "this project has no
    /// references" -- the caller must degrade, not compile from a half-answer.
    /// </exception>
    public static ProjectInputs Query(string projectPath, MsBuildQueryScratch scratch) =>
        Query(projectPath, scratch, "dotnet", QueryTimeout);

    /// <summary>
    /// <see cref="Query(string, MsBuildQueryScratch)"/> against a named <paramref name="executable"/>
    /// and a caller-chosen <paramref name="timeout"/>, so the two degradation paths that spawning
    /// hides can be executed.
    ///
    /// <para><b>Why this exists.</b> Two branches here -- the dotnet CLI being absent
    /// (<see cref="Win32Exception"/> out of <see cref="Process.Start(ProcessStartInfo)"/>) and the
    /// query outrunning its deadline -- were reachable only by uninstalling the SDK or waiting two
    /// minutes, so neither was ever executed by a test. That is not a cosmetic gap: this class's
    /// wrapping is what keeps ONE project's failure from ending the whole repository's run, and the
    /// review that found C2-1 (an unwrapped exception doing exactly that) named this absence as the
    /// reason it had gone unnoticed.</para>
    ///
    /// <para><c>internal</c>, and the public overload is the only production caller: the executable
    /// and the deadline are not knobs an operator gets, they are what a test needs to make a real
    /// failure happen instead of describing one.</para>
    /// </summary>
    internal static ProjectInputs Query(
        string projectPath, MsBuildQueryScratch scratch, string executable, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        ArgumentNullException.ThrowIfNull(scratch);

        var fullPath = Path.GetFullPath(projectPath);
        var intermediateOutputPath = scratch.IntermediateOutputPathFor(fullPath);

        string json;
        try
        {
            json = RunQuery(fullPath, targetFramework: null, intermediateOutputPath, executable, timeout);
        }
        catch (MsBuildQueryException)
        {
            // Optimistic first, probe only on failure: asking every project for TargetFrameworks up
            // front would add an MSBuild round trip per project to spare the rare multi-targeting one.
            // An unrestored project fails here too and its probe comes back null, so the original --
            // and far more useful -- failure is what propagates.
            var framework = FirstTargetFramework(fullPath, executable, timeout);
            if (framework is null)
            {
                throw;
            }

            json = RunQuery(fullPath, framework, intermediateOutputPath, executable, timeout);
        }

        return ReadInputs(fullPath, json);
    }

    /// <summary>
    /// Turns one MSBuild answer into <see cref="ProjectInputs"/>.
    ///
    /// <para>
    /// <see langword="internal"/> rather than folded into <see cref="Query"/> so the malformed-answer
    /// branches have an executable test at all: every other route to them spawns a real
    /// <c>dotnet msbuild</c>, which cannot be made to print a document of the wrong shape. Those
    /// branches are exactly where an <see cref="InvalidOperationException"/> used to escape
    /// <see cref="MsBuildQueryException"/> and abort the whole run.
    /// </para>
    /// </summary>
    /// <exception cref="MsBuildQueryException">
    /// <paramref name="json"/> is not the JSON object, with the item and property groups in the shapes,
    /// that <c>-getItem</c>/<c>-getProperty</c> promise.
    /// </exception>
    internal static ProjectInputs ReadInputs(string projectPath, string json)
    {
        using var document = ParseJson(projectPath, json);
        var root = document.RootElement;

        var properties = ReadProperties(projectPath, root);
        var items = ReadItems(projectPath, root);

        var signAssembly = string.Equals(Property(properties, "SignAssembly"), "true", StringComparison.OrdinalIgnoreCase);

        return new ProjectInputs(
            projectPath,
            Property(properties, "AssemblyName") ?? Path.GetFileNameWithoutExtension(projectPath),
            ReadCompileFiles(items),
            ReadReferences(projectPath, items),
            Property(properties, "DefineConstants") ?? string.Empty,
            Property(properties, "LangVersion") ?? string.Empty,
            string.Equals(Property(properties, "Nullable"), "enable", StringComparison.OrdinalIgnoreCase),
            string.Equals(Property(properties, "AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase),
            Property(properties, "OutputType") ?? "Library",
            Property(properties, "TargetFramework") ?? string.Empty)
        {
            SignAssembly = signAssembly,
            KeyFile = signAssembly ? ReadKeyFile(projectPath, properties) : null,
        };
    }

    /// <summary>
    /// The project's strong-name key file, resolved to an absolute path against the project's own
    /// directory -- never against this process's current directory, which is unrelated to where the
    /// project (and therefore a relative <c>KeyOriginatorFile</c>) lives.
    ///
    /// <para>
    /// <c>KeyOriginatorFile</c> is preferred over <c>AssemblyOriginatorKeyFile</c> because it is the
    /// value <c>Microsoft.Common.CurrentVersion.targets</c> actually passes to <c>csc</c>'s
    /// <c>/keyfile</c> switch (<c>AssemblyOriginatorKeyFile</c> is the property most project files set;
    /// the SDK copies it into <c>KeyOriginatorFile</c> unless something overrides that directly) -- so
    /// preferring it is preferring what the real compiler would see. <see cref="FullPath"/> is reused
    /// for the same reason it exists for <c>MSBuildSourceProjectFile</c>: this is a string MSBuild
    /// printed, not a path anything validated, and a malformed one (a NUL, a 40&nbsp;KB value) must
    /// refuse the whole project's query rather than crash past <see cref="MsBuildQueryException"/> the
    /// way an unwrapped <see cref="ArgumentException"/> or <see cref="PathTooLongException"/> already
    /// did once for that sibling case.
    /// </para>
    ///
    /// <para>
    /// Existence, repository containment and reparse-point safety are deliberately NOT checked here --
    /// this method only resolves where the property points. <see cref="CompilationFactory"/> is where
    /// those refusals belong: it is the one place that already holds a repository root (via
    /// <c>SourceFileGate</c>) and the reparse-point walk <c>TryParse</c> applies to <c>Compile</c>
    /// items, so a key file that is missing, outside the repository, or behind a link degrades to an
    /// unsigned compilation there rather than this query refusing the whole project over a key the
    /// caller might not even need yet.
    /// </para>
    /// </summary>
    private static string? ReadKeyFile(string projectPath, Dictionary<string, string> properties)
    {
        var metadata = Property(properties, "KeyOriginatorFile") is not null
            ? "KeyOriginatorFile"
            : "AssemblyOriginatorKeyFile";
        var value = Property(properties, metadata);
        if (value is null)
        {
            return null;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath) ?? string.Empty;
        var candidate = Path.IsPathRooted(value) ? value : Path.Combine(projectDirectory, value);
        return FullPath(projectPath, metadata, candidate);
    }

    /// <summary>
    /// The first framework a multi-targeting project lists, or <see langword="null"/> when the project
    /// is not multi-targeting (or cannot be evaluated at all, in which case the caller's original
    /// failure is the one worth reporting). Evaluation only -- no targets run -- so this is the cheap
    /// probe, taken only after the full query has already failed.
    ///
    /// <para>
    /// It carries neither <see cref="ReadOnlySwitches"/> nor an <c>IntermediateOutputPath</c> redirect,
    /// and does not need them: with no <c>-t:</c> at all MSBuild runs no target, so there is nothing to
    /// build a reference for and nothing to generate. Measured on a never-queried multi-targeting
    /// project: the probe left the scanned tree byte-identical.
    /// </para>
    /// </summary>
    private static string? FirstTargetFramework(string projectPath, string executable, TimeSpan timeout)
    {
        string json;
        try
        {
            json = Run(projectPath, ["-getProperty:TargetFrameworks", "-getProperty:TargetFramework"], executable, timeout);
        }
        catch (MsBuildQueryException)
        {
            return null;
        }

        try
        {
            // Through ParseJson, not JsonDocument.Parse: the shape check lives there, and a probe that
            // parsed the document its own way would be the one route into ReadProperties that had not
            // been through it -- the shape on which TryGetProperty throws rather than returning false.
            using var document = ParseJson(projectPath, json);
            var properties = ReadProperties(projectPath, document.RootElement);

            // A single-targeting project reports TargetFramework and (usually) no TargetFrameworks;
            // only the outer build of a multi-targeting one reports the plural with the singular empty.
            if (Property(properties, "TargetFramework") is not null)
            {
                return null;
            }

            return (Property(properties, "TargetFrameworks") ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }
        catch (MsBuildQueryException)
        {
            // This is a probe taken only after the full query already failed, so its own failure is
            // never the interesting one: returning null re-throws the caller's original error, which
            // says what actually went wrong with the project.
            return null;
        }
    }

    private static string RunQuery(
        string projectPath,
        string? targetFramework,
        string intermediateOutputPath,
        string executable,
        TimeSpan timeout)
    {
        var arguments = new List<string>();
        if (targetFramework is not null)
        {
            arguments.Add($"-p:TargetFramework={targetFramework}");
        }

        arguments.AddRange(ReadOnlySwitches);

        // The scanned repository is read, not built, and this is the half of that guarantee MSBuild
        // cannot be argued out of: the generated Compile items have to be written somewhere. They go
        // into the producer's own scratch directory, which it deletes. See MsBuildQueryScratch.
        // Escaped, because the path comes from TMPDIR/TEMP and MSBuild does not take it literally.
        arguments.Add($"-p:IntermediateOutputPath={EscapePropertyValue(intermediateOutputPath)}");

        arguments.AddRange(Targets);
        arguments.AddRange(Items);
        arguments.AddRange(Properties);

        return Run(projectPath, arguments, executable, timeout);
    }

    /// <summary>
    /// <paramref name="value"/> escaped with MSBuild's own <c>%XX</c> notation, so MSBuild reads it back
    /// as exactly the literal string it is -- every character in Microsoft Learn's "MSBuild special
    /// characters" table: <c>% $ @ ' ( ) ; ? *</c>.
    ///
    /// <para><b>Why, measured rather than argued</b> (SDK 10.0.204, Windows 11, an
    /// <c>-p:IntermediateOutputPath=</c> value under a scratch directory, the query's full target list).
    /// Unescaped, MSBuild does not take the value literally, and the failures differ in the one way that
    /// matters -- which direction they fail in:</para>
    /// <list type="bullet">
    /// <item><c>%XX</c> is <b>decoded</b>: <c>a%41b</c> wrote into <c>aAb</c>, and <c>x/%2E%2E/y</c>
    /// wrote into <c>y</c>, a directory traversal. That fails <b>open</b>: the generated files land
    /// outside <see cref="MsBuildQueryScratch.Root"/>, <see cref="MsBuildQueryScratch.Dispose"/> deletes
    /// a directory that was never created, and the files survive every successful run. <c>%</c> is a
    /// legal character in a Windows account name and in any <c>TMPDIR</c>.</item>
    /// <item><c>;</c> splits the switch: <c>MSB1006</c>, every query fails.</item>
    /// <item><c>@(Compile)</c> is expanded inside the SDK's targets and fails the query.</item>
    /// <item><c>$</c>, <c>$(Foo)</c>, <c>@</c>, <c>'</c>, <c>(</c>, <c>)</c> and a space were each
    /// taken literally -- escaped anyway, because a command-line value's handling is MSBuild's to change
    /// and Learn's own guidance is that escaping a character where it is not special "does no
    /// harm".</item>
    /// </list>
    /// <para>Escaped, each of those -- and all of them in one path, <c>p%;$@'()q</c> -- produced the
    /// literal directory, byte for byte. <c>?</c> and <c>*</c> cannot occur in a Windows path; they are
    /// in the set because they are in MSBuild's.</para>
    ///
    /// <para>
    /// One pass, one character at a time, which is what makes <c>%</c> safe: an input <c>%3B</c> becomes
    /// <c>%253B</c> and decodes back to <c>%3B</c>, never to <c>;</c>. A two-step
    /// <c>Replace(";", "%3B").Replace("%", "%25")</c> would get that backwards, which is why
    /// <c>RoslynResolverTests.Escaping_a_property_value_turns_every_MSBuild_special_character_into_its_literal</c>
    /// pins the <c>%3B</c> case by name.
    /// </para>
    /// </summary>
    internal static string EscapePropertyValue(string value)
    {
        var escaped = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            _ = c switch
            {
                '%' => escaped.Append("%25"),
                '$' => escaped.Append("%24"),
                '@' => escaped.Append("%40"),
                '\'' => escaped.Append("%27"),
                '(' => escaped.Append("%28"),
                ')' => escaped.Append("%29"),
                ';' => escaped.Append("%3B"),
                '?' => escaped.Append("%3F"),
                '*' => escaped.Append("%2A"),
                _ => escaped.Append(c),
            };
        }

        return escaped.ToString();
    }

    private static string Run(string projectPath, IReadOnlyList<string> arguments, string executable, TimeSpan timeout)
    {
        // Guards Process.Start against a working directory that is not there -- a project directory
        // that vanished between the scan and this query, or a caller naming a project that never
        // existed. The codebase's own precedent is GitRevision.RunGit, which checks the same thing for
        // the same reason. Measured on this host (.NET 10 / Windows 11): Process.Start with a missing
        // WorkingDirectory throws Win32Exception, which the catch below ALREADY converted -- so this is
        // not a new escape being closed, it is a wrong DIAGNOSIS being fixed. That catch reports "the
        // dotnet CLI was not found", which sends an operator hunting for an SDK that is installed.
        var workingDirectory = Path.GetDirectoryName(projectPath);
        if (workingDirectory is null || !Directory.Exists(workingDirectory))
        {
            throw new MsBuildQueryException(
                $"could not start `dotnet msbuild` for {projectPath}: its directory does not exist.");
        }

        // MSBuild resolves Directory.Build.props/targets from the project's own directory, so run there
        // rather than wherever the producer happened to be invoked from. Note what that sentence means:
        // those files are then FOUND, and being found means being evaluated, and being evaluated means
        // running. See this class's threat-model paragraph -- the choice here is between evaluating the
        // project correctly and evaluating it wrongly, not between running repository logic and not
        // running it.
        var allArguments = new List<string>(arguments.Count + 3)
        {
            "msbuild",
            projectPath,

            // Node reuse is on by default, and it is wrong for this caller twice over. It leaves worker
            // processes alive after the build -- N of them per producer run, which a tool meant for CI
            // has no business doing -- and those workers INHERIT the redirected pipes, so the reads can
            // stay open long after the msbuild process itself has exited, holding a call whose
            // process-level timeout has already been satisfied. BoundedProcess bounds that case too (its
            // deadline covers the reads, not only the exit), but a worker that is never started is
            // better than one that is waited out.
            "-nodeReuse:false",
        };
        allArguments.AddRange(arguments);

        // One runner for every child process this producer starts (E11): it redirects and closes stdin,
        // drains both streams concurrently under the caps below -- capped rather than read to the end,
        // because what msbuild prints on stdout is repository-controlled and an unbounded read of it is
        // an OutOfMemoryException a scanned repository can ask for -- bounds the WHOLE call, reads
        // included, by `timeout`, and kills the process tree when it gives up. What it reports is mapped
        // below to this class's own messages, verbatim, so every failure still leaves as
        // MsBuildQueryException -- the one type RoslynResolver.QueryProjectClosure catches, which is what
        // keeps one project's failure from ending the whole repository's run.
        var result = BoundedProcess.Run(executable, allArguments, workingDirectory, timeout, MaxStdoutChars, MaxStderrChars);

        switch (result.Outcome)
        {
            case BoundedOutcome.NotStarted when result.Exception is Win32Exception notFound:
                // The "MSBuild absent" degradation path from the brief: no dotnet on PATH at all.
                throw new MsBuildQueryException(
                    $"could not start `dotnet msbuild` for {projectPath}: the dotnet CLI was not found.", notFound);

            case BoundedOutcome.NotStarted:
                throw new MsBuildQueryException($"could not start `dotnet msbuild` for {projectPath}.");

            case BoundedOutcome.TimedOut:
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} did not finish within {timeout.TotalSeconds:0} s.");

            case BoundedOutcome.Faulted:
                // The SUCCESS path has its own failure mode, and it is the one that used to escape raw.
                // The exit wait can return normally and a subsequent read still throw -- a pipe torn down
                // abnormally by a killed or crashed msbuild, by a scanner holding the handle, or by a
                // worker going away mid-write -- and the exit code throws InvalidOperationException if
                // the process object is not in the state that read requires. Every one of those means
                // exactly what a non-zero exit means: this project's inputs are unknown. Left unwrapped
                // they escaped RoslynResolver.QueryProjectClosure's deliberately narrow
                // `catch (MsBuildQueryException)`, so a single project's abnormal msbuild aborted
                // generation for the WHOLE repository instead of degrading that one project to the
                // name-matching baseline.
                var fault = result.Exception!;
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} ended abnormally while its output was being read: {fault.Message}", fault);
        }

        if (result.ExitCode != 0)
        {
            var detail = result.Stderr.Length > 0 ? result.Stderr : result.Stdout;
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} exited {result.ExitCode}. "
                + $"A project that has not been restored fails here. {Truncate(detail, 400)}");
        }

        if (result.StdoutOverflowed)
        {
            // After the exit code, deliberately: a project that ALSO failed is better described by its
            // own error than by "it printed too much".
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} printed more than {MaxStdoutChars / (1024 * 1024)} MiB "
                + "on stdout, which no legitimate -getItem/-getProperty answer approaches. The answer was "
                + "not read.");
        }

        return result.Stdout;
    }

    /// <summary>
    /// How much of msbuild's stdout is kept before the answer is refused. A real answer is nowhere
    /// near this: measured on this host, <c>src/OKF4net.Mcp/OKF4net.Mcp.csproj</c> -- a restored
    /// project with over a hundred resolved references -- answers in <b>456,364 bytes</b>, and a
    /// one-file scratch project in <b>344,326</b>. 32 MiB is ~70x the larger of those.
    ///
    /// <para>
    /// <b>What makes the cap reachable, measured -- and it is not the obvious mechanism.</b> The escape
    /// register offered <c>-v:diag</c> injected through a repository's <c>Directory.Build.rsp</c>. That
    /// does not reproduce: in <c>-getItem</c>/<c>-getProperty</c> mode the console log is suppressed
    /// entirely, and on this host a query run with <c>-v:diag</c> in the rsp, and one run with a
    /// <c>Directory.Build.targets</c> emitting three 100 KB high-importance <c>&lt;Message&gt;</c>
    /// lines, each printed <b>344,326 bytes</b> -- byte-identical to the clean run. What does
    /// reproduce is the JSON itself, whose size the repository controls: a fifteen-line
    /// <c>Directory.Build.targets</c> declaring 10,000 <c>Compile</c> items took the same query from
    /// 344,326 bytes to <b>10,457,323 bytes in 1.1 s</b>. One more doubling level in that file is
    /// ~100 MB, and it costs the repository nothing. Past the cap the stream is still drained, not
    /// abandoned (see <c>BoundedProcess</c>), so the child is never left blocked on a full pipe.
    /// </para>
    /// </summary>
    private const int MaxStdoutChars = 32 * 1024 * 1024;

    /// <summary>
    /// How much of msbuild's stderr is kept. Far smaller, because the only use it is ever put to is
    /// <c>Truncate(stderr, 400)</c> inside a failure message.
    /// </summary>
    private const int MaxStderrChars = 1024 * 1024;

    /// <summary>
    /// Parses MSBuild's answer, and checks it is the <i>object</i> <c>-getItem</c>/<c>-getProperty</c>
    /// promise rather than merely well-formed JSON.
    ///
    /// <para>
    /// Syntax was never the only way that answer can be wrong. <c>JsonDocument.Parse</c> accepts
    /// <c>"[]"</c>, <c>"7"</c> and <c>"null"</c> quite happily, and every reader below then meets a
    /// <see cref="JsonElement"/> of the wrong kind -- on which <c>TryGetProperty</c>,
    /// <c>EnumerateObject</c> and <c>EnumerateArray</c> all throw
    /// <see cref="InvalidOperationException"/>, a type nothing upstream catches. The shape check
    /// belongs here, once, so that every route into the readers has been through it.
    /// </para>
    /// </summary>
    private static JsonDocument ParseJson(string projectPath, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} did not print the JSON that -getItem/-getProperty promise: {Truncate(json, 400)}", e);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} printed valid JSON that is not an object "
                + $"({Truncate(json, 400)}); -getItem/-getProperty promise an object.");
        }

        return document;
    }

    private static Dictionary<string, string> ReadProperties(string projectPath, JsonElement root)
    {
        // A lookup only -- Property() indexes it, nothing ever iterates it into output.
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!TryGetGroup(projectPath, root, "Properties", JsonValueKind.Object, out var element))
        {
            return properties;
        }

        foreach (var property in element.EnumerateObject())
        {
            // A non-string value is read as absent rather than crashing the run: Property() already
            // treats an empty value as "not set", which is the same answer, and every property this
            // query asks for is a string when MSBuild answers at all.
            properties[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : string.Empty;
        }

        return properties;
    }

    private static Dictionary<string, List<Dictionary<string, string>>> ReadItems(string projectPath, JsonElement root)
    {
        var items = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.Ordinal);
        if (!TryGetGroup(projectPath, root, "Items", JsonValueKind.Object, out var element))
        {
            return items;
        }

        foreach (var group in element.EnumerateObject())
        {
            if (group.Value.ValueKind != JsonValueKind.Array)
            {
                // Refused, not skipped. An item group this reader cannot read means the item set is
                // unknown, and an empty list would be read downstream as "this project has no Compile
                // items" -- a half-answer, which is exactly what Query's contract forbids.
                throw new MsBuildQueryException(
                    $"`dotnet msbuild` for {projectPath} printed item group `{group.Name}` as "
                    + $"{group.Value.ValueKind} rather than an array.");
            }

            var entries = new List<Dictionary<string, string>>();
            foreach (var entry in group.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    throw new MsBuildQueryException(
                        $"`dotnet msbuild` for {projectPath} printed an entry of item group `{group.Name}` as "
                        + $"{entry.ValueKind} rather than an object.");
                }

                var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var value in entry.EnumerateObject())
                {
                    metadata[value.Name] = value.Value.ValueKind == JsonValueKind.String
                        ? value.Value.GetString() ?? string.Empty
                        : string.Empty;
                }

                entries.Add(metadata);
            }

            items[group.Name] = entries;
        }

        return items;
    }

    /// <summary>
    /// The named top-level group, or <see langword="false"/> when MSBuild did not print one at all --
    /// which is legitimate (nothing matched) and reads as empty. A group that is present but of the
    /// wrong kind is not: that is an answer this reader cannot read, and an empty result there would
    /// be indistinguishable from "no references", which the caller is told never to conclude.
    /// </summary>
    private static bool TryGetGroup(
        string projectPath, JsonElement root, string name, JsonValueKind expected, out JsonElement element)
    {
        if (!root.TryGetProperty(name, out element))
        {
            return false;
        }

        if (element.ValueKind != expected)
        {
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} printed `{name}` as {element.ValueKind} rather than {expected}.");
        }

        return true;
    }

    /// <summary>
    /// The <c>Compile</c> items' absolute paths, in MSBuild's own order, with duplicates removed. An
    /// item can legitimately appear twice (a glob plus an explicit include), and handing Roslyn the
    /// same file twice is a compile error (CS0101 on every type it declares), not a harmless repeat.
    /// </summary>
    private static List<string> ReadCompileFiles(Dictionary<string, List<Dictionary<string, string>>> items)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(PathComparer);

        foreach (var item in Group(items, "Compile"))
        {
            if (item.TryGetValue("FullPath", out var path) && path.Length > 0 && seen.Add(path))
            {
                files.Add(path);
            }
        }

        return files;
    }

    /// <summary>
    /// The <c>ReferencePath</c> items, each tagged with the <c>.csproj</c> it is the output of when
    /// MSBuild says so. <c>MSBuildSourceProjectFile</c> is present exactly on the items
    /// <c>ResolveProjectReferences</c> contributed (marked <c>ReferenceSourceTarget=ProjectReference</c>),
    /// including transitive ones, which is what lets <see cref="CompilationFactory"/> replace each with
    /// a from-source <c>CompilationReference</c> instead of the <c>bin/</c> assembly that only exists
    /// after a build.
    ///
    /// <para><b>Repository-controlled, recorded here because this is where the value is born.</b> The
    /// <c>FullPath</c> read on the line below becomes
    /// <see cref="ProjectReferenceInput.AssemblyPath"/>, and it is the one MSBuild-JSON path value in
    /// this class that does <i>not</i> go through <see cref="FullPath"/> -- <c>projectFile</c> beside
    /// it does. It does not need to: nothing here dereferences it, and the shape this class hands over
    /// is a valid path string either way. A <c>Directory.Build.targets</c> can nonetheless add
    /// <c>&lt;ReferencePath Include="..." /&gt;</c> to any project it is imported into, so what the
    /// value points at is the scanned repository's choice, and the handling is
    /// <c>CompilationFactory</c>'s reference loop: <see cref="File.Exists(string)"/> drops the
    /// malformed shapes measured on this host (it is <c>false</c> for a path holding a NUL, for a 40 KB
    /// path, for a directory, and for <c>NUL</c>), an
    /// <see cref="IOException"/> from the read is reported as an unusable reference, and an existing
    /// <b>non-assembly</b> -- <c>&lt;ReferencePath Include="$(MSBuildProjectFullPath)" /&gt;</c>, a real
    /// <c>.csproj</c> -- reaches <c>compilation.GetDiagnostics()</c> as CS0009. All three degrade the
    /// one project. An earlier round recorded that last shape as raising
    /// <see cref="BadImageFormatException"/> and killing the run; it does not, on this host: Roslyn
    /// reads the bytes eagerly but the metadata lazily, and turns the format failure into a diagnostic.
    /// See <c>CompilationFactory.Create</c>'s reference loop for the eight shapes measured.</para>
    /// </summary>
    private static List<ProjectReferenceInput> ReadReferences(
        string projectPath, Dictionary<string, List<Dictionary<string, string>>> items)
    {
        var references = new List<ProjectReferenceInput>();
        var seen = new HashSet<string>(PathComparer);

        foreach (var item in Group(items, "ReferencePath"))
        {
            if (!item.TryGetValue("FullPath", out var path) || path.Length == 0 || !seen.Add(path))
            {
                continue;
            }

            var isProjectOutput = item.TryGetValue("ReferenceSourceTarget", out var source)
                && string.Equals(source, "ProjectReference", StringComparison.Ordinal);
            var projectFile = isProjectOutput
                && item.TryGetValue("MSBuildSourceProjectFile", out var project)
                && project.Length > 0
                    ? FullPath(projectPath, "MSBuildSourceProjectFile", project)
                    : null;

            references.Add(new ProjectReferenceInput(path, projectFile));
        }

        return references;
    }

    /// <summary>
    /// <see cref="Path.GetFullPath(string)"/> on a value that came out of MSBuild's JSON, refused
    /// rather than thrown.
    ///
    /// <para>
    /// Item metadata is not a path anything validated: <c>MSBuildSourceProjectFile</c> is a string a
    /// repository-authored target can set to anything at all. Measured on this host (.NET 10 /
    /// Windows 11): a value holding a NUL throws <see cref="ArgumentException"/> ("Null character in
    /// path"), and a 40 KB one throws <see cref="PathTooLongException"/>. Neither is an
    /// <see cref="MsBuildQueryException"/>, so both escaped <see cref="ReadInputs"/>, escaped
    /// <see cref="Query"/>, missed <c>RoslynResolver.QueryProjectClosure</c>'s deliberately narrow
    /// <c>catch (MsBuildQueryException)</c> and aborted generation for the whole repository -- the
    /// exact shape the shape-guards above were added for, in the method that added them.
    /// </para>
    ///
    /// <para>
    /// Refused, not skipped, for the reason <see cref="ReadItems"/> gives: a reference item this
    /// reader cannot read means this project's reference set is unknown, and reading it as "no project
    /// reference here" is the half-answer <see cref="Query"/>'s contract forbids -- it would send
    /// <see cref="CompilationFactory"/> to the <c>bin/</c> assembly of a project it could have
    /// compiled from source, or to nothing at all.
    /// </para>
    /// </summary>
    private static string FullPath(string projectPath, string metadata, string value)
    {
        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            throw new MsBuildQueryException(
                $"`dotnet msbuild` for {projectPath} printed `{metadata}` as a value that is not a path "
                + $"({Truncate(value, 200)}).", e);
        }
    }

    private static List<Dictionary<string, string>> Group(
        Dictionary<string, List<Dictionary<string, string>>> items, string name) =>
        items.TryGetValue(name, out var group) ? group : [];

    private static string? Property(Dictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// <see cref="StringComparer.Ordinal"/>, one rule for every path comparison in this producer
    /// (6.2), including this de-duplication of MSBuild's own item lists. Both spellings here come out
    /// of a single MSBuild evaluation, which already de-duplicates its item lists, so this is a
    /// belt-and-braces pass against handing Roslyn one file twice (CS0101 on every type it declares).
    /// An ordinal comparison could in principle let one through under two casings on Windows -- and
    /// that lands as a compilation error, reported and degraded cleanly, not as a silently wrong edge.
    /// A case-insensitive one would instead drop a genuinely distinct file on a case-sensitive
    /// filesystem, which is the worse of the two, and it would reintroduce the exact divergence
    /// <c>FileEligibility</c> was deliberately moved off <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// to close.
    /// </summary>
    private static StringComparer PathComparer => StringComparer.Ordinal;

    private static string Truncate(string text, int max)
    {
        var collapsed = text.ReplaceLineEndings(" ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..max] + "...";
    }
}
