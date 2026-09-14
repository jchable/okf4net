// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Generation;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// §6.3's containment spine (<see cref="BundlePaths"/>) as its own subject, rather than only observed
/// through <see cref="BundleWriter"/>'s pruning and commit behaviour. The finding this file exists for:
/// a trailing directory separator on a bundle root is not a symbolic link, but <see cref="BundlePaths.ResolveRoot"/>
/// used to hand one back to <see cref="BundlePaths.IsInside"/> anyway -- see <see cref="BundleWriterTests"/> for
/// the end-to-end shape (a whole `generate` run refusing every concept it staged).
/// </summary>
public class BundlePathsTests
{
    [Fact]
    public void ResolveRoot_drops_a_trailing_separator_so_IsInside_can_see_the_bundle()
    {
        using var tmp = new TempDir();
        var root = BundlePaths.ResolveRoot(tmp.Path + Path.DirectorySeparatorChar)!;

        Assert.False(root.EndsWith(Path.DirectorySeparatorChar));
        Assert.True(BundlePaths.IsInside(root, Path.Combine(root, "overview.md")));
    }

    [Fact]
    public void ResolveRoot_is_the_same_whether_or_not_the_caller_supplied_a_trailing_separator()
    {
        using var tmp = new TempDir();

        Assert.Equal(BundlePaths.ResolveRoot(tmp.Path), BundlePaths.ResolveRoot(tmp.Path + Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// The fact the fix leans on: <see cref="Path.TrimEndingDirectorySeparator(string)"/> is documented
    /// to leave a bare drive or filesystem root alone (trimming <c>"C:\"</c> to <c>"C:"</c>, or <c>"/"</c>
    /// to <c>""</c>, would change what the path means), so applying it unconditionally in
    /// <see cref="BundlePaths.ResolveRoot"/> and the two call sites in <see cref="BundleWriter"/> is safe.
    /// Verified here rather than only asserted in a comment.
    /// </summary>
    [Fact]
    public void TrimEndingDirectorySeparator_leaves_a_filesystem_root_unchanged()
    {
        var root = OperatingSystem.IsWindows() ? Path.GetPathRoot(Environment.SystemDirectory)! : "/";
        Assert.True(root.EndsWith(Path.DirectorySeparatorChar), $"expected '{root}' to end with a separator, as every platform root does.");

        Assert.Equal(root, Path.TrimEndingDirectorySeparator(root));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "okfproducer-bundlepaths-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
