// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using OkfProducer.CodeGraph.Roslyn;
using OkfProducer.Tests.Generation;
using OkfProducer.Tests.TestSupport;

namespace OkfProducer.Tests.CodeGraph;

/// <summary>
/// E6's end-to-end proof against a REAL SDK 8 toolchain -- the SDK line whose
/// <c>AddImplicitDefineConstants</c> target runs too late (<c>BeforeTargets="CoreCompile"</c>) for
/// <c>MsBuildProjectQuery</c>'s target list to see its output before the fix in this task. Deliberately
/// its own class with no shared fixture: fix round 1 found that
/// <see cref="RoslynResolverTests.ScratchProject"/>, an <c>IClassFixture</c> shared across every test in
/// <see cref="RoslynResolverTests"/>, restores a net10.0 project through a bare <c>"dotnet"</c> that
/// resolves via <c>PATH</c> -- so a first attempt at this test that put an SDK-8-only <c>dotnet</c> first
/// on <c>PATH</c> (the only way to make <c>[Sdk8Fact]</c> not skip) broke that fixture's restore before
/// ANY test in the class ran its body, <c>[Sdk8Fact]</c>'s own test included. Every one of the 72 tests
/// in that file failed identically whether the fix under test was present or not, which cannot tell RED
/// from GREEN -- see <c>task-E6-report.md</c>, "Fix round 1", and the round-1 review it responds to.
///
/// <para>
/// This class restores its own tiny project and never relies on <c>PATH</c> order for the SDK 8
/// invocation: <see cref="Sdk8"/> locates an SDK 8 <c>dotnet</c> executable (an explicit
/// <c>OKF_TEST_DOTNET8</c> override, or one parsed out of <c>dotnet --list-sdks</c>'s own answer) and
/// every call here -- <c>restore</c> and the query -- passes that path explicitly, through
/// <see cref="MsBuildProjectQuery.Query(string, string, TimeSpan)"/>'s existing internal
/// <c>executable</c> overload (the same seam <c>RoslynResolverTests</c> already uses for its
/// "missing dotnet" and "query timeout" cases). <see cref="CompilationFactory.Create"/> then compiles
/// in-process from the returned <see cref="ProjectInputs"/> -- no further process, no <c>PATH</c>
/// dependency -- so this reaches the actual consumer of <c>DefineConstants</c>, not only the property
/// string MSBuild reports.
/// </para>
///
/// <para>
/// <b>Why this class joins <see cref="ProcessEnvironmentCollectionDefinition"/>.</b> Fix round 1's own
/// re-review found that <see cref="NestedDotnetEnvironment"/> mutates ~10 MSBuild/SDK-resolution
/// environment variables process-wide (not per-thread), for the duration of this class's
/// <c>Restore</c>+<c>Query</c> calls -- and several OTHER test classes in this assembly
/// (<see cref="RoslynResolverTests"/> among them) shell out to a real <c>dotnet</c> from their own test
/// methods, which by xunit's default behaviour can run concurrently with this one. That is exactly the
/// situation <see cref="ProcessEnvironmentCollectionDefinition"/> exists for (see
/// <c>GitRevisionTests</c>'s own remarks): putting only the class or classes that themselves MUTATE
/// process-wide state into that <c>DisableParallelization</c> collection is what actually keeps a
/// concurrently-running, unrelated real-<c>dotnet</c> call from landing inside the cleared window --
/// re-review found this harmless *today* on this host (a same-SDK, PATH-resolved call is not
/// observably different whether these variables are set or unset), but nothing enforced that as an
/// invariant. A cleaner, production-code-free alternative -- threading the scrubbed variables through
/// <c>ProcessStartInfo.Environment</c> on each child process individually, rather than mutating
/// <see cref="Environment"/> process-wide at all -- was considered and rejected for this class: it would
/// cover <see cref="Sdk8Repository.Restore"/>'s own <c>ProcessStartInfo</c> (test-owned), but
/// <see cref="MsBuildProjectQuery.Query(string, string, TimeSpan)"/>'s internal <c>Run</c> builds its
/// own <c>ProcessStartInfo</c> with no environment-overlay parameter, and adding one is a production
/// code change this round of fixes was told not to make. Joining the collection is therefore the only
/// complete fix available without touching production code.
/// </para>
/// </summary>
[Collection(ProcessEnvironmentCollectionDefinition.Name)]
public sealed class Sdk8ImplicitDefinesTests
{
    [Sdk8Fact]
    public void The_guarded_source_compiles_clean_through_compilationfactory_on_sdk_8()
    {
        // The actual regression this task fixes, executed rather than merely reasoned about: without
        // "-t:AddImplicitDefineConstants" in MsBuildProjectQuery.Targets, SDK 8.0.425 reports
        // DefineConstants as just TRACE;DEBUG;NET;NET8_0;NETCOREAPP -- no *_OR_GREATER symbol at all --
        // so `#if NET5_0_OR_GREATER` takes the `#else #error` branch and CompilationFactory.Create
        // reports a CS1029 diagnostic. With the fix, the symbol is present and the source compiles
        // clean. See task-E6-report.md, "Fix round 1" for the manual RED/GREEN capture of exactly this
        // assertion against a scratch-installed SDK 8.0.425, with the fix's target removed and restored.
        var sdk8 = Sdk8.ExecutablePath!; // [Sdk8Fact] guarantees non-null when not skipped

        // Measured while building this test (see task-E6-report.md, "Fix round 1"): this xunit process
        // is itself launched by `dotnet test` under SDK 10.0.204, which leaves MSBuildSDKsPath /
        // MSBuildExtensionsPath / DOTNET_HOST_PATH set to that SDK's OWN paths in this process's
        // environment -- and Process.Start inherits them by default. A child `dotnet.exe` from a
        // DIFFERENT SDK (SDK 8 here) then loads MSBuild's Sdks from the WRONG SDK's folder regardless
        // of which executable was invoked (MSB4062, "System.Runtime, Version=10.0.0.0 ... not found").
        // This is a "dotnet inside dotnet" environment leak specific to hosting this probe inside an
        // xunit run, not something MsBuildProjectQuery.Run needs to guard in production -- an ordinary
        // okfgen invocation is not itself nested inside an active MSBuild evaluation the way this test
        // process is.
        using var clean = NestedDotnetEnvironment.Clear();

        using var repository = new Sdk8Repository();
        repository.Restore(sdk8);

        var inputs = MsBuildProjectQuery.Query(repository.Project, sdk8, TimeSpan.FromMinutes(2));

        // The property MSBuild reports, checked first because a failure here is the more direct
        // diagnosis -- "the query never asked for the define" vs. "the compiler did something else
        // with it".
        var defines = inputs.DefineConstants.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("NET5_0_OR_GREATER", defines);
        Assert.Contains("NET8_0_OR_GREATER", defines);

        // The actual consumer: CompilationFactory turns DefineConstants into
        // CSharpParseOptions.PreprocessorSymbols, which is what decides which branch of the fixture's
        // #if actually parses. In-process, no further `dotnet` invocation -- unaffected by PATH.
        var compilation = CompilationFactory.Create(inputs, projectCompilations: null, SourceFileGate.Unbounded, out _);
        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(d => d.ToString())));
        Assert.NotNull(compilation.GetTypeByMetadataName("Sdk8Guarded.Guarded"));
    }

    /// <summary>
    /// A throwaway net8.0 project restored and queried against an explicit SDK 8 <c>dotnet</c>
    /// executable, never a bare <c>"dotnet"</c> resolved through <c>PATH</c> -- see the class remarks.
    /// Outside the repository tree, like every scratch fixture in the sibling
    /// <see cref="RoslynResolverTests"/>, so it inherits none of this repository's own build settings.
    /// </summary>
    private sealed class Sdk8Repository : IDisposable
    {
        public Sdk8Repository()
        {
            Root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "okf-producer-sdk8-" + Guid.NewGuid().ToString("N")[..12])).FullName;

            // global.json still pins the feature band/patch this project expects, so a repository
            // scanned by the real okfgen (rather than this direct query) would resolve the same SDK --
            // it is not load-bearing for THIS test, which is told exactly which dotnet to use, but
            // keeping it means the fixture is also a faithful "what a real SDK-8-pinned repository
            // looks like" shape.
            File.WriteAllText(Path.Combine(Root, "global.json"), GlobalJson.ReplaceLineEndings("\n"));
            Project = Path.Combine(Root, "Sdk8.csproj");
            File.WriteAllText(Project, ProjectFile.ReplaceLineEndings("\n"));
            File.WriteAllText(Path.Combine(Root, "Guarded.cs"), Source.ReplaceLineEndings("\n"));
        }

        public string Root { get; }

        public string Project { get; }

        /// <summary>Restores against the given <c>dotnet</c> executable explicitly -- never a bare "dotnet" on <c>PATH</c>.</summary>
        public void Restore(string executable)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Root,
            };
            startInfo.ArgumentList.Add("restore");
            startInfo.ArgumentList.Add(Project);

            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();

            Assert.True(
                process.ExitCode == 0,
                $"`{executable} restore {Project}` exited {process.ExitCode}: {stdout.GetAwaiter().GetResult()} {stderr.GetAwaiter().GetResult()}");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a locked file on the way out should not fail the test run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private const string GlobalJson = """
            {
              "sdk": {
                "version": "8.0.100",
                "rollForward": "latestFeature"
              }
            }
            """;

        private const string ProjectFile = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;

        private const string Source = """
            #if NET5_0_OR_GREATER
            namespace Sdk8Guarded;
            public class Guarded { public int Value() => 1; }
            #else
            #error missing implicit framework defines
            #endif
            """;
    }

    /// <summary>
    /// Clears the MSBuild-related environment variables an SDK sets on its own process while
    /// resolving itself, for the duration of a nested <c>dotnet</c> invocation that must resolve a
    /// DIFFERENT SDK -- see the usage site's remarks for the exact leak this closes, measured on this
    /// host. Scoped and reversible, like <c>HostFacts</c>'s <c>DenyAce</c>/<c>UnixPermission</c>
    /// handles: <see cref="Clear"/> returns an <see cref="IDisposable"/> that restores every value it
    /// touched, including a variable that was genuinely unset (restored to unset, not to
    /// <c>string.Empty</c>). <see cref="Environment.SetEnvironmentVariable(string, string)"/> is
    /// process-wide, not per-thread, so any caller of this helper MUST be a member of
    /// <see cref="ProcessEnvironmentCollectionDefinition"/> (as <see cref="Sdk8ImplicitDefinesTests"/>
    /// is) -- otherwise another, unrelated test class's own real-<c>dotnet</c> call could run
    /// concurrently inside the cleared window.
    /// </summary>
    private static class NestedDotnetEnvironment
    {
        // Measured, not a guess at the full set MSBuild might set: TEMP_env_dump (removed once this
        // list was captured) showed MSBuildSDKsPath, MSBuildExtensionsPath and DOTNET_HOST_PATH
        // actually set to the outer SDK's own paths inside this xunit process; the rest are the same
        // family of variable (documented MSBuild/SDK-resolver overrides) included defensively since an
        // unset one costs nothing to also clear.
        private static readonly string[] Keys =
        [
            "MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBuildExtensionsPath32", "MSBuildExtensionsPath64",
            "MSBUILD_EXE_PATH", "DOTNET_HOST_PATH", "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR",
            "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR", "DOTNET_ROOT", "DOTNET_ROOT(x86)", "FrameworkPathOverride",
        ];

        public static IDisposable Clear()
        {
            var saved = Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            foreach (var key in Keys)
            {
                Environment.SetEnvironmentVariable(key, null);
            }

            return new Restorer(saved);
        }

        private sealed class Restorer(Dictionary<string, string?> saved) : IDisposable
        {
            public void Dispose()
            {
                foreach (var (key, value) in saved)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }
    }
}
