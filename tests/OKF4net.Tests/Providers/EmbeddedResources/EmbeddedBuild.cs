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

    /// <summary>Writes only when the text differs: rewriting an unchanged project file would make the next build non-incremental, which the incremental tests must be able to see.</summary>
    private static void WriteIfChanged(string path, string text)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != text)
        {
            File.WriteAllText(path, text);
        }
    }

    public static Result Build(string projectDir, string itemsXml, string? directoryBuildTargetsXml = null, string verbosity = "minimal", IEnumerable<string>? extraArgs = null)
    {
        Directory.CreateDirectory(Path.Combine(projectDir, "obj"));
        WriteIfChanged(Path.Combine(projectDir, "probe.csproj"), $"""
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
        WriteIfChanged(Path.Combine(projectDir, "obj", "probe.csproj.okf.targets"), $"""
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

        foreach (var arg in extraArgs ?? [])
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

    /// <summary>The names of the types the assembly defines, to prove the project's own sources were compiled.</summary>
    public static SortedSet<string> ReadTypeNames(string assemblyPath)
    {
        var context = new AssemblyLoadContext("okf-types-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        try
        {
            using var file = File.OpenRead(assemblyPath);
            return new SortedSet<string>(context.LoadFromStream(file).GetTypes().Select(t => t.Name), StringComparer.Ordinal);
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
