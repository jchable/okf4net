// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Generation;
using OkfProducer.Tests.TestSupport;

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

    // --- E11: the one repository-containment question and the one count-bounded link walk, shared by
    // CompilationFactory, TreeSitterExtractor, RoslynResolver and SourceOwnershipMap.

    [Fact]
    public void TryGetPathUnderRoot_answers_at_or_under_and_nothing_else()
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "repo");

        Assert.True(BundlePaths.TryGetPathUnderRoot(root, root, out var self));
        Assert.Equal(".", self);

        Assert.True(BundlePaths.TryGetPathUnderRoot(root, Path.Combine(root, "a", "x.cs"), out var child));
        Assert.Equal(Path.Combine("a", "x.cs"), child);

        // A SEGMENT of `..` climbs out; a directory whose name merely starts with `..` does not.
        // RoslynResolver's former copy tested the prefix and read `..foo/x.cs` as outside.
        Assert.True(BundlePaths.TryGetPathUnderRoot(root, Path.Combine(root, "..foo", "x.cs"), out var dotted));
        Assert.Equal(Path.Combine("..foo", "x.cs"), dotted);
        Assert.False(BundlePaths.TryGetPathUnderRoot(root, Path.Combine(tmp.Path, "shared", "x.cs"), out _));
        Assert.False(BundlePaths.IsAtOrUnderRoot(root, Path.Combine(root, "..", "shared", "x.cs")));

        // A sibling sharing the root's name as a string prefix is not under it.
        Assert.False(BundlePaths.IsAtOrUnderRoot(root, Path.Combine(tmp.Path, "repo2", "x.cs")));
    }

    [DirectoryLinkFact]
    public void HasLinkAncestorUnderRoot_finds_a_link_between_the_root_and_the_file()
    {
        using var tmp = new TempDir();
        var root = Directory.CreateDirectory(Path.Combine(tmp.Path, "repo")).FullName;
        var link = DirectoryLinks.Create(Path.Combine(root, "a", "link"), Path.Combine(tmp.Path, "elsewhere"));
        var file = WriteFile(Path.Combine(link, "b", "X.cs"));

        try
        {
            Assert.True(BundlePaths.HasLinkAncestorUnderRoot(root, file));

            // And a file beside the link, not under it, is not refused.
            Assert.False(BundlePaths.HasLinkAncestorUnderRoot(root, WriteFile(Path.Combine(root, "a", "Y.cs"))));
        }
        finally
        {
            Unlink(link);
        }
    }

    /// <summary>
    /// The regression the count exists for. <c>CompilationFactory</c>'s first walk stopped only on
    /// meeting the root string, so a path OUTSIDE the root -- a <c>Compile</c> item from
    /// <c>..\..\Shared</c> -- never met it and every ancestor up to the filesystem root was probed; with
    /// the shared tree under a linked ancestor (<c>/tmp</c> -&gt; <c>/private/tmp</c> on macOS) every such
    /// item was dropped. The precondition proves the layout really has a link an unbounded walk would
    /// find, so the <see langword="false"/> below is the bound's doing and not an absent link's.
    /// </summary>
    [DirectoryLinkFact]
    public void HasLinkAncestorUnderRoot_never_walks_above_the_root_for_a_path_outside_it()
    {
        using var tmp = new TempDir();
        var root = Directory.CreateDirectory(Path.Combine(tmp.Path, "repo")).FullName;
        var linked = DirectoryLinks.Create(Path.Combine(tmp.Path, "linked"), Path.Combine(tmp.Path, "shared-real"));
        var file = WriteFile(Path.Combine(linked, "Shared", "X.cs"));

        try
        {
            Assert.True(BundlePaths.HasLinkAncestor(file, levels: 32), "precondition: an unbounded walk finds the link above the file.");

            Assert.False(BundlePaths.HasLinkAncestorUnderRoot(root, file));
        }
        finally
        {
            Unlink(linked);
        }
    }

    [DirectoryLinkFact]
    public void HasLinkAncestorUnderRoot_ignores_a_link_at_the_root_itself()
    {
        // An operator may check a repository out behind a junction; that is their layout, not something
        // the scanned repository chose, so the walk stops below the root.
        using var tmp = new TempDir();
        var root = DirectoryLinks.Create(Path.Combine(tmp.Path, "linkroot"), Path.Combine(tmp.Path, "realroot"));
        var file = WriteFile(Path.Combine(root, "src", "X.cs"));

        try
        {
            Assert.True(BundlePaths.HasLinkAncestor(file, levels: 2), "precondition: one level further up is the link.");

            Assert.False(BundlePaths.HasLinkAncestorUnderRoot(root, file));
        }
        finally
        {
            Unlink(root);
        }
    }

    [DirectoryLinkFact]
    public void HasLinkAncestor_returns_at_once_on_a_link_loop()
    {
        // `a/loop` points back at `a`, so `a/loop/loop/.../File.cs` names a real file through twenty
        // re-entries. A walk that followed links could recurse forever; this one only ever inspects the
        // path string it was given, so it returns -- and answers true, since the loop is a link.
        using var tmp = new TempDir();
        var root = Directory.CreateDirectory(Path.Combine(tmp.Path, "repo")).FullName;
        var a = Directory.CreateDirectory(Path.Combine(root, "a")).FullName;
        WriteFile(Path.Combine(a, "File.cs"));
        var loop = DirectoryLinks.Create(Path.Combine(a, "loop"), a);

        try
        {
            var deep = Path.Combine([a, .. Enumerable.Repeat("loop", 20), "File.cs"]);
            Assert.True(File.Exists(deep), "precondition: the file is reachable through the loop.");

            Assert.True(BundlePaths.HasLinkAncestorUnderRoot(root, deep));
            Assert.True(BundlePaths.HasLinkAncestor(deep, levels: int.MaxValue));
        }
        finally
        {
            Unlink(loop);
        }
    }

    [Fact]
    public void HasLinkAncestorUnderRoot_walks_a_deep_link_free_tree_and_finds_nothing()
    {
        using var tmp = new TempDir();
        var root = Directory.CreateDirectory(Path.Combine(tmp.Path, "repo")).FullName;
        var file = WriteFile(Path.Combine([root, .. Enumerable.Repeat("d", 64), "X.cs"]));

        Assert.False(BundlePaths.HasLinkAncestorUnderRoot(root, file));
    }

    [DirectoryLinkFact]
    public void HasLinkAncestor_tests_exactly_as_many_levels_as_it_is_given()
    {
        // A link directly under the root with a 60-level tree hanging off it: the walk has to climb all
        // 61 directories to reach it, and one level short of that must not.
        using var tmp = new TempDir();
        var root = Directory.CreateDirectory(Path.Combine(tmp.Path, "repo")).FullName;
        var link = DirectoryLinks.Create(Path.Combine(root, "link"), Path.Combine(tmp.Path, "target"));
        var file = WriteFile(Path.Combine([link, .. Enumerable.Repeat("d", 60), "X.cs"]));

        try
        {
            Assert.True(BundlePaths.HasLinkAncestorUnderRoot(root, file));
            Assert.True(BundlePaths.HasLinkAncestor(file, levels: 61));
            Assert.False(BundlePaths.HasLinkAncestor(file, levels: 60));
        }
        finally
        {
            Unlink(link);
        }
    }

    [UnixPermissionFact]
    public void HasLinkAncestor_fails_closed_on_an_ancestor_it_cannot_inspect_posix()
    {
        // Directory `a` with mode 000: nothing beneath it can be lstat'ed, so whether `a/b` is a link is
        // unanswerable -- and IsReparsePoint reports an unanswerable path as a link. No Windows twin, and
        // measured rather than assumed (E11, Windows 11 / .NET 10, unelevated): a deny ACE of (RD), (X),
        // (RA) or (F) on `a` left DirectoryInfo("a/b").LinkTarget answering normally -- the traverse
        // bypass an ordinary user holds means no ACL this suite can set makes that probe throw.
        using var tmp = new TempDir();
        var root = Directory.CreateDirectory(Path.Combine(tmp.Path, "repo")).FullName;
        var a = Path.Combine(root, "a");
        var file = WriteFile(Path.Combine(a, "b", "X.cs"));

        using (UnixPermission.DenyAll(a))
        {
            Assert.True(BundlePaths.HasLinkAncestorUnderRoot(root, file));
        }
    }

    private static string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "namespace N;");
        return path;
    }

    /// <summary>Removes a directory link itself, never recursively -- a recursive delete would follow it into its target.</summary>
    private static void Unlink(string link) => Directory.Delete(link, recursive: false);

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
