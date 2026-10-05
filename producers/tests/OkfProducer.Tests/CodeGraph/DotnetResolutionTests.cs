// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.CodeGraph.Roslyn;
using OkfProducer.Tests.Generation;
using OkfProducer.Tests.TestSupport;

namespace OkfProducer.Tests.CodeGraph;

/// <summary>
/// #112: <c>dotnet exec OkfProducer.Cli.dll generate …</c> queried a different <c>dotnet</c> than the
/// apphost, <c>dotnet run</c> and an MSBuild <c>&lt;Exec&gt;</c> did, with an identical environment and an
/// identical <c>PATH</c>, because <c>Process.Start("dotnet")</c> searches the running application's own
/// directory before <c>PATH</c> -- and under <c>dotnet exec</c> the application is <c>dotnet</c>. A scanned
/// repository whose <c>global.json</c> pins an SDK only the <c>PATH</c> install has was then queried by
/// an install without it, and the project degraded to <c>MsBuildQueryFailed</c> with exit code 0.
///
/// <para>
/// What this pins is the resolution, which is where the bug was. The end-to-end behaviour needs two
/// <c>dotnet</c> installs and is recorded as a measurement in <c>producers/README.md</c>; these tests
/// need none. They mutate the process-wide <c>PATH</c>, hence the same non-parallel collection
/// <see cref="GitRevisionTests"/> uses for the same reason.
/// </para>
/// </summary>
[Collection(ProcessEnvironmentCollectionDefinition.Name)]
public sealed class DotnetResolutionTests
{
    [Fact]
    public void The_dotnet_on_PATH_is_chosen_over_a_bare_name()
    {
        using var pathDir = new TempDir();
        var fake = WriteStandIn(pathDir.Path);

        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", pathDir.Path);

            // A bare "dotnet" here is the bug: it is what Process.Start would then resolve against the
            // application directory first. The stand-in lives in a scratch directory that is neither the
            // application directory nor the current one, so only a PATH search can return it.
            Assert.Equal(Path.GetFullPath(fake), MsBuildProjectQuery.ResolveDotnet(), ignoreCase: OperatingSystem.IsWindows());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    [Fact]
    public void With_no_dotnet_on_PATH_the_hosting_one_is_used_and_otherwise_the_bare_name()
    {
        using var emptyDir = new TempDir();

        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", emptyDir.Path);

            var host = Environment.ProcessPath;
            var hostIsDotnet = host is not null
                && string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase);

            // A machine with no dotnet on PATH that runs the producer through one keeps working as it
            // did; one with neither fails the way it always did ("the dotnet CLI was not found").
            Assert.Equal(hostIsDotnet ? host : "dotnet", MsBuildProjectQuery.ResolveDotnet());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("okfproducer-dotnetpath-").FullName;

        public void Dispose() => TempTree.Delete(Path);
    }

    // Neither is ever launched: only the resolved path is asserted on.
    private static string WriteStandIn(string directory)
    {
        var path = Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(path, "not a real executable -- only its path is asserted on");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}
