// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// Writes against the listing scope of issue #205, and the temporaries a write can leave
/// (issue #157). In the repository only <c>Bundle.Load</c> and <c>BundleValidator.Validate</c>
/// open a scope, around their own reads, and the writer never runs inside one; these tests pin
/// what happens if a write does meet a scope — on its own flow, or on another while a load runs.
/// </summary>
public class FileSystemBundleProviderWriteScopeTests
{
    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>
    /// Inside a scope the write resolves the key against the directory as it is now, not as the
    /// scope first listed it: the object deleted behind the scope's back is absent for the write,
    /// so <c>MustNotExist</c> holds. Afterwards the scope's listings on the key's path are dropped,
    /// so the same flow's next lookup sees the new object.
    /// </summary>
    [Fact]
    public void A_write_inside_a_scope_ignores_the_scopes_listing_then_drops_it()
    {
        using var tmp = new TempDir();
        var gone = tmp.Write("d/x.md", "x");
        var p = new FileSystemBundleProvider(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            Assert.True(p.Exists(K("d/x.md")));   // memoises d = [x.md]
            Assert.False(p.Exists(K("d/new.md")));
            File.Delete(gone);                      // behind the scope's back

            p.Write(K("d/x.md"), B("recreated"), BundleWriteCondition.MustNotExist);
            p.Write(K("d/new.md"), B("new"), BundleWriteCondition.MustNotExist);

            Assert.True(p.Exists(K("d/new.md")));
            using var obj = p.Read(K("d/x.md"));
            Assert.Equal(9, obj.Length);
        }
    }

    /// <summary>
    /// The scope's listings dropped by a write are those of the root and of every directory on
    /// the key's path, not only the key's parent: the root, memoised without <c>e</c>, must not
    /// keep hiding the directory the write just created.
    /// </summary>
    [Fact]
    public void A_write_inside_a_scope_drops_the_listing_of_the_root_and_of_every_directory_on_the_path()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "a");
        var p = new FileSystemBundleProvider(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            Assert.True(p.Exists(K("a.md")));   // memoises the root, which holds no e
            p.Write(K("e/z.md"), B("z"), BundleWriteCondition.MustNotExist);

            Assert.True(p.Exists(K("e/z.md")));
        }
    }

    /// <summary>
    /// A successful delete inside a scope drops the scope's listings on the key's path: the
    /// next lookup of the deleted key lists the root and the directory again (two listings, none
    /// without the drop) and finds it absent.
    /// </summary>
    [Fact]
    public void A_delete_inside_a_scope_drops_the_listings_on_its_path()
    {
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "x");
        var p = new FileSystemBundleProvider(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            Assert.True(p.Exists(K("d/x.md")));   // memoises the root and d

            p.Delete(K("d/x.md"), BundleWriteCondition.None);

            var listings = p.DirectoryListings;
            Assert.False(p.Exists(K("d/x.md")));
            Assert.Equal(2, p.DirectoryListings - listings);
        }
    }

    /// <summary>
    /// Per-flow isolation of the scope. A load's scope is open on one flow while another flow
    /// writes. A write on a flow that did not inherit the scope never touches its memo: a name
    /// created meanwhile stays unseen until the scope ends (the window documented on
    /// <c>BeginScope</c>), whereas a write on a flow that inherited the scope drops the scope's
    /// listings on its path, so the new name is seen at once. In both rows the scope's read of
    /// the replaced object returns the bytes now stored with the version of those bytes: that
    /// holds because <c>Read</c> hashes the very bytes it returns, not because of the memo.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_write_on_a_flow_without_the_scope_leaves_its_listings_alone_and_a_read_pairs_its_bytes_with_their_version(bool writerInheritsTheScope)
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "v1");
        var p = new FileSystemBundleProvider(tmp.Path);
        using (((IProviderScope)p).BeginScope())
        {
            using (var before = p.Read(K("x.md")))
            {
                Assert.Equal(2, before.Length); // memoises the root
            }

            Assert.False(p.Exists(K("y.md")));
            Exception? failure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    p.Write(K("x.md"), B("version two"), BundleWriteCondition.None);
                    p.Write(K("y.md"), B("y"), BundleWriteCondition.MustNotExist);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });
            if (writerInheritsTheScope)
            {
                writer.Start();
            }
            else
            {
                using (ExecutionContext.SuppressFlow())
                {
                    writer.Start();
                }
            }

            writer.Join();
            Assert.Null(failure);

            using (var after = p.Read(K("x.md")))
            {
                var bytes = new byte[after.Length!.Value];
                after.Content.ReadExactly(bytes);
                Assert.Equal("version two", Encoding.UTF8.GetString(bytes));
                Assert.Equal(new BundleObjectVersion(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))), after.Version);
            }

            Assert.Equal(writerInheritsTheScope, p.Exists(K("y.md")));
        }

        Assert.True(p.Exists(K("y.md")));
    }

    /// <summary>
    /// A temporary left behind (B9) is listed by the test seam, counts toward the entry bound
    /// like any file, and is ignored as a concept by the load and the validation (it is no
    /// <c>.md</c>): nothing reports it, which is why the README says how to find it.
    /// </summary>
    [Fact]
    public void An_orphaned_temporary_is_listed_counted_and_otherwise_ignored()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: guide\ntitle: A\ndescription: d\n---\n\nbody\n");
        var orphan = FileSystemBundleProvider.TemporaryPrefix + new string('0', 32) + FileSystemBundleProvider.TemporarySuffix;
        tmp.Write("d/" + orphan, "new content of d/x.md");
        var p = new FileSystemBundleProvider(tmp.Path);

        Assert.Equal(["d/" + orphan], p.ListOrphanedTemporaries(BundleLimits.Default));
        Assert.Throws<BundleLimitExceededException>(() => p.Enumerate(null, new BundleLimits { MaxEntries = 2 }));

        var bundle = Bundle.Load(p);
        Assert.Equal(1, bundle.Count);
        Assert.Empty(bundle.ParseProblems);
        Assert.DoesNotContain(BundleValidator.Validate(bundle).Diagnostics, d => d.Message.Contains(orphan, StringComparison.Ordinal));
    }
}
