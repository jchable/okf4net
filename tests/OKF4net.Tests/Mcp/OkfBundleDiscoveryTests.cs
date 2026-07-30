// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Mcp;

namespace OKF4net.Tests.Mcp;

public sealed class OkfBundleDiscoveryTests
{
    private const string Marked = "---\nokf_version: \"0.2\"\n---\n\n# Index\n";
    private const string Unmarked = "# Index\n";
    private const string FrontmatterWithoutVersion = "---\ntitle: Not a bundle\n---\n\n# Index\n";

    // Rooted, platform-neutral fake tree base. No real filesystem involved:
    // the walk sees only what the injected readRootIndex answers.
    private static readonly string Base = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "okf-disc-fake"));

    private static string At(params string[] parts) => Path.GetFullPath(Path.Combine([Base, .. parts]));

    private static Func<string, string?> Fs(params (string Dir, string IndexText)[] entries)
    {
        var map = entries.ToDictionary(e => e.Dir, e => e.IndexText, StringComparer.Ordinal);
        return dir => map.TryGetValue(Path.GetFullPath(dir), out var text) ? text : null;
    }

    [Fact]
    public void Start_directory_that_is_a_marked_bundle_wins()
    {
        var ok = OkfBundleDiscovery.TryDiscover(At("proj"), Fs((At("proj"), Marked)), out var root);

        Assert.True(ok);
        Assert.Equal(At("proj"), root);
    }

    [Fact]
    public void Knowledge_child_is_found_when_the_directory_itself_is_not_a_bundle()
    {
        var ok = OkfBundleDiscovery.TryDiscover(At("proj"), Fs((At("proj", "knowledge"), Marked)), out var root);

        Assert.True(ok);
        Assert.Equal(At("proj", "knowledge"), root);
    }

    [Fact]
    public void Directory_itself_beats_its_knowledge_child()
    {
        var ok = OkfBundleDiscovery.TryDiscover(
            At("proj"),
            Fs((At("proj"), Marked), (At("proj", "knowledge"), Marked)),
            out var root);

        Assert.True(ok);
        Assert.Equal(At("proj"), root);
    }

    [Fact]
    public void Nearest_level_beats_ancestors()
    {
        var ok = OkfBundleDiscovery.TryDiscover(
            At("proj", "sub"),
            Fs((At("proj", "sub", "knowledge"), Marked), (At("proj"), Marked)),
            out var root);

        Assert.True(ok);
        Assert.Equal(At("proj", "sub", "knowledge"), root);
    }

    [Fact]
    public void Walk_reaches_marked_ancestors()
    {
        var ok = OkfBundleDiscovery.TryDiscover(
            At("proj", "a", "b"),
            Fs((At("proj"), Marked)),
            out var root);

        Assert.True(ok);
        Assert.Equal(At("proj"), root);
    }

    [Fact]
    public void Index_without_okf_version_is_not_a_bundle()
    {
        var ok = OkfBundleDiscovery.TryDiscover(
            At("proj"),
            Fs((At("proj"), Unmarked), (At("proj", "knowledge"), FrontmatterWithoutVersion)),
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void No_marked_bundle_anywhere_returns_false()
    {
        var ok = OkfBundleDiscovery.TryDiscover(At("proj", "a", "b"), Fs(), out var root);

        Assert.False(ok);
        Assert.Equal(string.Empty, root);
    }

    [Fact]
    public void Marked_bundle_at_the_filesystem_root_is_found()
    {
        var fsRoot = Path.GetPathRoot(Base)!;

        var ok = OkfBundleDiscovery.TryDiscover(At("a", "b"), Fs((fsRoot, Marked)), out var root);

        Assert.True(ok);
        Assert.Equal(fsRoot, root);
    }

    [Fact]
    public void Empty_start_directory_returns_false_instead_of_throwing()
    {
        var ok = OkfBundleDiscovery.TryDiscover(string.Empty, Fs(), out var root);

        Assert.False(ok);
        Assert.Equal(string.Empty, root);
    }

    // ---- Production adapter (real filesystem) --------------------------------

    [Fact]
    public void Adapter_reads_root_index_text()
    {
        var dir = Directory.CreateTempSubdirectory("okf-disc-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "index.md"), Marked);

            Assert.Equal(Marked, OkfBundleDiscovery.ReadRootIndexOrNull(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Adapter_returns_null_when_index_is_missing()
    {
        var dir = Directory.CreateTempSubdirectory("okf-disc-").FullName;
        try
        {
            Assert.Null(OkfBundleDiscovery.ReadRootIndexOrNull(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Adapter_returns_null_on_invalid_utf8()
    {
        var dir = Directory.CreateTempSubdirectory("okf-disc-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "index.md"), [0xFF, 0xFE, 0xFA]);

            Assert.Null(OkfBundleDiscovery.ReadRootIndexOrNull(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void End_to_end_discovery_over_a_real_tree()
    {
        var top = Directory.CreateTempSubdirectory("okf-disc-e2e-").FullName;
        try
        {
            var knowledge = Directory.CreateDirectory(Path.Combine(top, "knowledge")).FullName;
            var nested = Directory.CreateDirectory(Path.Combine(top, "src", "deep")).FullName;
            File.WriteAllText(Path.Combine(knowledge, "index.md"), Marked, new UTF8Encoding(false));

            var ok = OkfBundleDiscovery.TryDiscover(nested, OkfBundleDiscovery.ReadRootIndexOrNull, out var root);

            Assert.True(ok);
            Assert.Equal(knowledge, root);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }
}
