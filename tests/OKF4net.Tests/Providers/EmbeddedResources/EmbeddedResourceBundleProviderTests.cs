// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using OKF4net.Providers;
using OKF4net.Providers.EmbeddedResources;

namespace OKF4net.Tests.Providers.EmbeddedResources;

public class EmbeddedResourceBundleProviderTests
{
    private static readonly Assembly Self = typeof(EmbeddedResourceBundleProviderTests).Assembly;
    private static readonly string Assets = Path.Combine(TestPaths.RepoRoot(), "tests", "OKF4net.Tests", "Providers", "EmbeddedResources", "Assets");

    private static EmbeddedResourceBundleProvider Contract() => new(Self, "contract");

    private static List<string> Names(IBundleProvider p, BundleKey? prefix = null)
    {
        var names = new List<string>();
        var e = p.EnumerateAsync(prefix, BundleLimits.Default, CancellationToken.None).GetAsyncEnumerator();
        while (e.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            names.Add(e.Current.RawName);
        }

        e.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return names;
    }

    [Fact]
    public void Lists_its_own_prefix_only_sorted_ordinally()
    {
        Assert.Equal(["index.md", "notes/a-much-longer-name.md", "notes/a.md", "notes/b.md", "refs/q.sql"], Names(Contract()));
        Assert.Equal(["one.md"], Names(new EmbeddedResourceBundleProvider(Self, "contract2")));
    }

    [Fact]
    public void A_key_prefix_filters_at_a_segment_boundary()
    {
        Assert.Equal(["notes/a-much-longer-name.md", "notes/a.md", "notes/b.md"], Names(Contract(), BundleKey.Parse("notes")));
        Assert.Empty(Names(Contract(), BundleKey.Parse("note")));
    }

    [Fact]
    public async Task Reads_the_bytes_of_the_file_with_its_length()
    {
        using var obj = await Contract().ReadAsync(BundleKey.Parse("refs/q.sql"), null, CancellationToken.None);
        using var copy = new MemoryStream();
        await obj.Content.CopyToAsync(copy);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Assets, "contract", "refs", "q.sql")), copy.ToArray());
        Assert.Equal(copy.Length, obj.Length);
    }

    [Fact]
    public async Task Exists_is_exact_and_ordinal()
    {
        var p = Contract();
        var exact = p.ExistsAsync(BundleKey.Parse("notes/a.md"), CancellationToken.None);
        Assert.True(exact.IsCompleted);
        Assert.True(await exact);
        Assert.False(await p.ExistsAsync(BundleKey.Parse("Notes/a.md"), CancellationToken.None));
        Assert.False(await p.ExistsAsync(BundleKey.Parse("notes"), CancellationToken.None)); // a prefix is not an object
    }

    [Fact]
    public async Task A_missing_object_and_a_stale_expected_version_fault_the_task()
    {
        var p = Contract();
        var missing = p.ReadAsync(BundleKey.Parse("nope.md"), null, CancellationToken.None);
        Assert.True(missing.IsFaulted);
        await Assert.ThrowsAsync<BundleObjectNotFoundException>(async () => await missing);

        var stale = p.ReadAsync(BundleKey.Parse("index.md"), new BundleObjectVersion("other"), CancellationToken.None);
        Assert.True(stale.IsFaulted);
        await Assert.ThrowsAsync<BundleVersionMismatchException>(async () => await stale);

        var version = await p.GetVersionAsync(BundleLimits.Default, CancellationToken.None);
        using var ok = await p.ReadAsync(BundleKey.Parse("index.md"), new BundleObjectVersion(version.Token), CancellationToken.None);
        Assert.Equal(version.Token, ok.Version.Token);
    }

    [Fact]
    public async Task Writes_and_deletes_are_refused_as_completed_faults()
    {
        var p = Contract();
        var write = p.WriteAsync(BundleKey.Parse("index.md"), "x"u8.ToArray(), BundleWriteCondition.None, CancellationToken.None);
        var delete = p.DeleteAsync(BundleKey.Parse("index.md"), BundleWriteCondition.None, CancellationToken.None);
        Assert.True(write.IsFaulted);
        Assert.True(delete.IsFaulted);
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await write);
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await delete);
    }

    [Fact]
    public async Task A_cancelled_token_cancels_through_the_task()
    {
        // Same contract as AsyncProviderContractTests pins for the filesystem provider.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var read = Contract().ReadAsync(BundleKey.Parse("index.md"), null, cts.Token);
        Assert.True(read.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in Contract().EnumerateAsync(null, BundleLimits.Default, cts.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Declares_its_capabilities_constraints_identity_and_version()
    {
        var p = Contract();
        var mvid = Self.ManifestModule.ModuleVersionId.ToString("N");
        Assert.False(p.Capabilities.CanWrite);
        Assert.True(p.Capabilities.CompletesSynchronously);
        Assert.Equal(BundleKeyConstraints.WellFormedUnicode, p.NativeConstraints);
        Assert.Equal($"embedded:{Self.FullName}/{mvid}/contract", p.Id);
        Assert.Equal($"embedded:{Self.GetName().Name}/contract", p.Location);
        var version = p.GetVersionAsync(BundleLimits.Default, CancellationToken.None);
        Assert.True(version.IsCompleted);
        Assert.Equal("mvid:" + mvid, (await version).Token);
        Assert.Equal(p.Id, Contract().Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("nope")]
    [InlineData("contrac")]
    public void An_invalid_or_unknown_prefix_throws(string prefix)
    {
        Assert.Throws<ArgumentException>(() => new EmbeddedResourceBundleProvider(Self, prefix));
    }

    [Fact]
    public void A_dynamic_assembly_is_refused()
    {
        var dynamic = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("okf-dynamic"), AssemblyBuilderAccess.Run);
        Assert.Throws<ArgumentException>(() => new EmbeddedResourceBundleProvider(dynamic, "kb"));
        Assert.Throws<ArgumentException>(() => EmbeddedResourceBundleProvider.GetBundlePrefixes(dynamic));
    }

    [Fact]
    public void GetBundlePrefixes_lists_the_conventional_prefixes_and_ignores_strays()
    {
        var prefixes = EmbeddedResourceBundleProvider.GetBundlePrefixes(Self);
        Assert.Contains("contract", prefixes);
        Assert.Contains("contract2", prefixes);
        Assert.Contains("names", prefixes);
        Assert.DoesNotContain("", prefixes);
        Assert.DoesNotContain("noslash", prefixes);
        Assert.Equal(prefixes.Order(StringComparer.Ordinal), prefixes);
        Assert.Equal(prefixes.Distinct(StringComparer.Ordinal).Count(), prefixes.Count);
    }

    [Fact]
    public void Every_operation_of_a_load_completes_synchronously()
    {
        var tracked = new CompletionTrackingProvider(Contract());
        var bundle = Bundle.Load(tracked);
        Assert.Equal(3, bundle.Concepts.Count); // notes/a, notes/b, notes/a-much-longer-name; index.md is an index, not a concept
        Assert.True(tracked.Observed > 3);
        Assert.Empty(tracked.Incomplete);
    }

    [Fact]
    public void An_over_long_key_is_a_local_problem_not_a_load_failure()
    {
        // "notes/a-much-longer-name.md" is 27 bytes; the bound is 16. The filesystem provider behaves the same way.
        var bundle = Bundle.Load(Contract(), new BundleLoadOptions { Limits = new BundleLimits { MaxKeyBytes = 16 } });
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal("notes/a-much-longer-name.md", problem.RawName);
        Assert.Equal(2, bundle.Concepts.Count); // notes/a and notes/b load; only the long key is set aside
    }

    [Fact]
    public async Task Two_builds_of_the_same_name_with_different_content_have_distinct_ids_and_one_build_loaded_twice_keeps_its_id()
    {
        using var first = new TempDir();
        first.Write("knowledge/a.md", "one\n");
        var a = EmbeddedBuild.Build(first.Path, """    <OkfBundle Include="knowledge/" Prefix="kb" />""");
        Assert.True(a.ExitCode == 0, a.Output);
        using var second = new TempDir();
        second.Write("knowledge/a.md", "two\n");
        var b = EmbeddedBuild.Build(second.Path, """    <OkfBundle Include="knowledge/" Prefix="kb" />""");
        Assert.True(b.ExitCode == 0, b.Output);

        var contexts = new List<AssemblyLoadContext>();
        try
        {
            Assembly Load(string path)
            {
                var context = new AssemblyLoadContext("okf-id-" + contexts.Count, isCollectible: true);
                contexts.Add(context);
                return context.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
            }

            var pa = new EmbeddedResourceBundleProvider(Load(a.AssemblyPath), "kb");
            var pa2 = new EmbeddedResourceBundleProvider(Load(a.AssemblyPath), "kb");
            var pb = new EmbeddedResourceBundleProvider(Load(b.AssemblyPath), "kb");
            Assert.Equal(pa.Id, pa2.Id);
            Assert.NotEqual(pa.Id, pb.Id);
            Assert.NotEqual(
                await pa.GetVersionAsync(BundleLimits.Default, CancellationToken.None),
                await pb.GetVersionAsync(BundleLimits.Default, CancellationToken.None));
        }
        finally
        {
            contexts.ForEach(c => c.Unload());
        }
    }
}
