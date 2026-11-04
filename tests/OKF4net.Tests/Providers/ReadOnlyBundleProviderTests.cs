// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class ReadOnlyBundleProviderTests
{
    /// <summary>A writable in-memory provider that records every write call; the decorator must never reach it.</summary>
    private sealed class RecordingProvider : SyncTestProvider
    {
        public List<string> Calls { get; } = [];
        public override string Id => "recording";
        public override string Location => "memory://recording";
        public override BundleProviderCapabilities Capabilities => new(CanWrite: true, CompletesSynchronously: true);
        public override BundleKeyConstraints NativeConstraints => BundleKeyConstraints.FileDirectoryConflict;
        protected override BundleVersion GetVersionSync(BundleLimits limits) => new("v1");
        protected override IEnumerable<BundleListing> EnumerateSync(BundleKey? prefix, BundleLimits limits) => [new BundleListing("a.md", 3, null)];
        protected override BundleObject ReadSync(BundleKey key, BundleObjectVersion? expected) =>
            new(new MemoryStream(Encoding.UTF8.GetBytes("abc")), new BundleObjectVersion("sha"), 3);
        protected override bool ExistsSync(BundleKey key) => key.ToString() == "a.md";
        protected override BundleObjectVersion WriteSync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition)
        {
            Calls.Add($"write {key}");
            return new BundleObjectVersion("sha2");
        }
        protected override void DeleteSync(BundleKey key, BundleWriteCondition condition) => Calls.Add($"delete {key}");
    }

    [Fact]
    public void Decorator_declares_no_write_and_refuses_before_touching_the_inner_provider()
    {
        var inner = new RecordingProvider();
        var ro = inner.AsReadOnly();
        Assert.False(ro.Capabilities.CanWrite);
        Assert.Throws<BundleReadOnlyException>(() => ro.Write(BundleKey.Parse("a.md"), new byte[] { 1 }, BundleWriteCondition.None));
        Assert.Throws<BundleReadOnlyException>(() => ro.Delete(BundleKey.Parse("a.md"), BundleWriteCondition.None));
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void Decorator_delegates_every_read_member()
    {
        var inner = new RecordingProvider();
        var ro = inner.AsReadOnly();
        Assert.Equal("recording", ro.Id);
        Assert.Equal("memory://recording", ro.Location);
        Assert.Equal(BundleKeyConstraints.FileDirectoryConflict, ro.NativeConstraints);
        Assert.Equal(new BundleVersion("v1"), ro.GetVersion(BundleLimits.Default));
        Assert.Single(ro.Enumerate(null, BundleLimits.Default));
        Assert.True(ro.Exists(BundleKey.Parse("a.md")));
        using var obj = ro.Read(BundleKey.Parse("a.md"));
        Assert.Equal(new BundleObjectVersion("sha"), obj.Version);
    }

    [Fact]
    public void AsReadOnly_is_idempotent()
    {
        var ro = new RecordingProvider().AsReadOnly();
        Assert.Same(ro, ro.AsReadOnly());
    }

    [Fact]
    public void Default_limits_match_the_spec()
    {
        var l = BundleLimits.Default;
        Assert.Equal(256L * 1024 * 1024, l.MaxObjectBytes);
        Assert.Equal(10_000_000, l.MaxEntries);
        Assert.Equal(1024, l.MaxDepth);
        Assert.Equal(1024, l.MaxKeyBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), l.RequestTimeout);
        Assert.Equal(16, l.MaxConcurrentRequests);
    }

    [Fact]
    public void Write_conditions_and_versions_are_value_types_compared_ordinally()
    {
        var v = new BundleObjectVersion("abc");
        Assert.Equal(BundleWriteConditionKind.IfMatch, BundleWriteCondition.IfMatch(v).Kind);
        Assert.Equal(v, BundleWriteCondition.IfMatch(v).Expected);
        Assert.Null(BundleWriteCondition.None.Expected);
        Assert.Equal(BundleWriteConditionKind.MustNotExist, BundleWriteCondition.MustNotExist.Kind);
        Assert.NotEqual(new BundleObjectVersion("ABC"), v);
    }

    [Fact]
    public void Too_large_is_an_IOException_so_existing_handlers_treat_it_as_unreadable()
    {
        var e = new BundleObjectTooLargeException(BundleKey.Parse("a.md"), 17, 16);
        Assert.IsAssignableFrom<IOException>(e);
        Assert.Contains("a.md", e.Message);
    }
}
