// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Skeleton tests for <see cref="OkfBundleTools"/>: constructor validation
/// and the lazy <c>Bundle</c> cache. Mirrors the <c>RepoRoot()</c> fixture
/// lookup pattern used by <see cref="CliTests"/> so the fixture path does
/// not depend on the process's current directory.
/// </summary>
public class OkfBundleToolsTests
{
    private static readonly string BundlePath = Path.Combine(RepoRoot(), "tests", "fixtures", "appendix_a");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OKF4net.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException($"could not locate OKF4net.sln above {AppContext.BaseDirectory}");
    }

    [Fact]
    public void Constructor_rejects_nonexistent_directory()
    {
        Assert.Throws<ArgumentException>(() => new OkfBundleTools("nonexistent-dir"));
    }

    [Fact]
    public void GetBundle_loads_appendix_a_fixture()
    {
        var tools = new OkfBundleTools(BundlePath);
        Assert.Equal(4, tools.GetBundle().Count);
    }
}
