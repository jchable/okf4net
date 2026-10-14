// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Pins <see cref="TempDir.Dispose"/> (#120): the whole tree goes, links
/// inside it are removed as links, and a link's target OUTSIDE the tree
/// survives untouched. Every test asserts the directory is actually gone, not
/// merely that disposing did not throw.
/// </summary>
public sealed class TempDirTests
{
    [SkippableFact]
    public void A_junction_to_an_outside_directory_is_removed_with_the_tree_and_its_target_survives_intact()
    {
        // The exact shape that leaked: .NET's recursive Directory.Delete fails
        // on a junction (DeleteVolumeMountPoint) and leaves its ancestors.
        using var outside = new TempDir();
        var expected = PopulateTarget(outside);

        using var tmp = new TempDir();
        tmp.Write("a/b/note.md", "inside");
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(Path.Combine("a", "b", "link"), outside.Path), "cannot create a junction or directory symlink here");
        Assert.True((File.GetAttributes(Path.Combine(tmp.Path, "a", "b", "link")) & FileAttributes.ReparsePoint) != 0);

        tmp.Dispose();

        Assert.False(Directory.Exists(tmp.Path));
        Assert.Equal(expected, Snapshot(outside.Path));
    }

    [SkippableFact]
    public void Directory_and_file_symlinks_to_outside_targets_are_removed_as_links_and_their_targets_survive_intact()
    {
        using var outside = new TempDir();
        var expected = PopulateTarget(outside);

        using var tmp = new TempDir();
        Skip.IfNot(tmp.TryCreateFileSymlinkToExternalFile(Path.Combine("x", "file-link.md"), Path.Combine(outside.Path, "top.md")), "needs symlink-creation privilege");
        Directory.CreateSymbolicLink(Path.Combine(tmp.Path, "x", "dir-link"), outside.Path);

        tmp.Dispose();

        Assert.False(Directory.Exists(tmp.Path));
        Assert.Equal(expected, Snapshot(outside.Path));
    }

    [SkippableFact]
    public void A_junction_whose_target_was_already_removed_is_still_removed()
    {
        using var outside = new TempDir();
        using var tmp = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("dangling", outside.Path), "cannot create a junction or directory symlink here");
        outside.Dispose();
        Assert.False(Directory.Exists(outside.Path));

        tmp.Dispose();

        Assert.False(Directory.Exists(tmp.Path));
    }

    [SkippableFact]
    public void An_uninspectable_junction_disposed_first_leaves_nothing_behind_and_its_target_survives_intact()
    {
        // The shape #120 names: a junction carrying deny ACEs. Its handle lifts
        // them and removes the link; TempDir then removes the rest.
        using var outside = new TempDir();
        var expected = PopulateTarget(outside);

        using var tmp = new TempDir();
        var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), outside.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");

        junction!.Dispose();
        tmp.Dispose();

        Assert.False(Directory.Exists(tmp.Path));
        Assert.Equal(expected, Snapshot(outside.Path));
    }

    [SkippableFact]
    public void A_directory_whose_listing_is_denied_makes_dispose_throw_and_leaves_the_junction_target_intact()
    {
        // Disposing in the wrong order: the junction's parent "x" cannot be
        // listed, so the walk throws while listing "x" and never reaches the
        // junction "y" itself (the entry-level guard on an uninspectable entry
        // is not exercised here; see the comment in
        // TempDir.DeleteWithoutFollowingLinks). The failure must not pass
        // silently; once the ACEs are lifted the same TempDir disposes
        // cleanly and the target is untouched.
        using var outside = new TempDir();
        var expected = PopulateTarget(outside);

        using var tmp = new TempDir();
        var junction = tmp.TryCreateUninspectableJunction(Path.Combine("x", "y"), outside.Path);
        Skip.If(junction is null, "needs Windows (a junction plus deny ACEs)");

        try
        {
            var failure = Assert.Throws<IOException>(tmp.Dispose);
            Assert.Contains(tmp.Path, failure.Message, StringComparison.Ordinal);
            Assert.True(Directory.Exists(tmp.Path));
            Assert.Equal(expected, Snapshot(outside.Path));
        }
        finally
        {
            junction!.Lift();
        }

        tmp.Dispose();

        Assert.False(Directory.Exists(tmp.Path));
        Assert.Equal(expected, Snapshot(outside.Path));
    }

    [Fact]
    public void A_read_only_file_is_removed()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("deep/locked.md", "x");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        tmp.Dispose();

        Assert.False(Directory.Exists(tmp.Path));
    }

    [Fact]
    public void Disposing_a_tree_the_test_already_removed_does_nothing()
    {
        using var tmp = new TempDir();
        Directory.Delete(tmp.Path);

        tmp.Dispose();
        tmp.Dispose();

        Assert.False(Directory.Exists(tmp.Path));
    }

    private static SortedDictionary<string, string> PopulateTarget(TempDir target)
    {
        target.Write("top.md", "keep me");
        target.Write("sub/deeper/inner.md", "keep me too");
        return Snapshot(target.Path);
    }

    private static SortedDictionary<string, string> Snapshot(string root)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            files[Path.GetRelativePath(root, file).Replace('\\', '/')] = File.ReadAllText(file);
        }

        Assert.Equal(2, files.Count);
        return files;
    }
}
