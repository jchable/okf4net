// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Tests.TestSupport;

/// <summary>
/// Pins <see cref="TempTree.Delete"/> against the two shapes that made the suite's own scratch
/// directories survive in the system temp (#119). On Windows a bare <c>Directory.Delete(path, true)</c>
/// fails on both shapes (measured when #119 was fixed, by running these cases with the helper reduced
/// to that bare call); on POSIX hosts the shapes are harmless and the cases simply pass.
/// </summary>
public sealed class TempTreeTests
{
    [DirectoryLinkFact]
    public void A_tree_holding_a_directory_link_is_removed_and_the_links_target_is_left_alone()
    {
        var root = Directory.CreateTempSubdirectory("okfproducer-temptree-").FullName;
        var outside = Directory.CreateTempSubdirectory("okfproducer-temptree-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "must survive");
            DirectoryLinks.Create(Path.Combine(root, "link"), outside);
            Directory.CreateDirectory(Path.Combine(root, "real"));
            File.WriteAllText(Path.Combine(root, "real", "f.txt"), "x");

            Assert.True(TempTree.Delete(root));

            Assert.False(Directory.Exists(root));
            Assert.Equal("must survive", File.ReadAllText(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            ForceDelete(root);
            ForceDelete(outside);
        }
    }

    [Fact]
    public void A_tree_holding_read_only_files_is_removed()
    {
        var root = Directory.CreateTempSubdirectory("okfproducer-temptree-").FullName;
        try
        {
            // The shape git leaves under .git/objects: a read-only file in a nested directory.
            var objects = Directory.CreateDirectory(Path.Combine(root, ".git", "objects", "ab")).FullName;
            var obj = Path.Combine(objects, "cdef");
            File.WriteAllText(obj, "x");
            File.SetAttributes(obj, FileAttributes.ReadOnly);

            Assert.True(TempTree.Delete(root));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            ForceDelete(root);
        }
    }

    [Fact]
    public void A_path_that_does_not_exist_is_already_gone()
    {
        Assert.True(TempTree.Delete(Path.Combine(Path.GetTempPath(), "okfproducer-temptree-" + Guid.NewGuid().ToString("N"))));
    }

    // Test cleanup that does not depend on the code under test.
    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
