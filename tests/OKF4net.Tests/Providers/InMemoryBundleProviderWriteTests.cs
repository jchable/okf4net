// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>The write contract on the in-memory test provider, plus the hooks later tasks use to script a storage's behaviour.</summary>
public class InMemoryBundleProviderWriteTests : WritableProviderContractTests
{
    protected override IBundleProvider CreateEmptyProvider() => new InMemoryBundleProvider(canWrite: true);

    /// <summary>A dictionary has no second client: the same instance stands for it.</summary>
    protected override IBundleProvider OpenSameStorage() => Provider;

    private InMemoryBundleProvider Memory => (InMemoryBundleProvider)Provider;

    [Fact]
    public void Versions_are_the_lowercase_hex_sha256_of_the_bytes_like_the_filesystem()
    {
        var v = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(B("one"))), v.Token);
    }

    [Fact]
    public void By_default_the_provider_is_read_only_and_refuses_before_storing()
    {
        var ro = new InMemoryBundleProvider().Add("a.md", "one");
        Assert.False(ro.Capabilities.CanWrite);
        Assert.Throws<BundleReadOnlyException>(() => ro.Write(K("a.md"), B("two"), BundleWriteCondition.None));
        Assert.Throws<BundleReadOnlyException>(() => ro.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.Equal(["write a.md None", "delete a.md None"], ro.Calls);
        using var obj = ro.Read(K("a.md"));
        Assert.Equal(3, obj.Length);
    }

    [Fact]
    public void Every_call_is_recorded_in_order()
    {
        Memory.Add("seed.md", "s");
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.MustNotExist);
        _ = Provider.Exists(K("a.md"));
        using (Provider.Read(K("a.md")))
        {
        }

        _ = Provider.Enumerate(K("a.md"), BundleLimits.Default).ToList();
        _ = Provider.GetVersion(BundleLimits.Default);
        Provider.Delete(K("a.md"), BundleWriteCondition.None);
        Assert.Equal(["write a.md MustNotExist", "exists a.md", "read a.md", "enumerate a.md", "version", "delete a.md None"], Memory.Calls);
        Memory.ClearCalls();
        Assert.Empty(Memory.Calls);
    }

    [Fact]
    public void A_scripted_conflict_hits_the_next_write_or_delete_once_and_stores_nothing()
    {
        var v1 = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Memory.ConflictOnNextWrite();
        var ex = Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("a.md"), B("two"), BundleWriteCondition.IfMatch(v1)));
        Assert.Equal(K("a.md"), ex.Key);
        Assert.Equal("one", ReadText("a.md"));
        Provider.Write(K("a.md"), B("two"), BundleWriteCondition.IfMatch(v1)); // one-shot

        Memory.ConflictOnNextWrite();
        Assert.Throws<BundleWriteConflictException>(() => Provider.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.True(Provider.Exists(K("a.md")));
    }

    [Fact]
    public void A_scripted_change_lands_just_before_the_next_write_checks_its_condition()
    {
        var v1 = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Memory.BeforeNextWrite(p => p.Add("a.md", "changed elsewhere"));
        Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("a.md"), B("two"), BundleWriteCondition.IfMatch(v1)));
        Assert.Equal("changed elsewhere", ReadText("a.md"));
    }

    [Fact]
    public void A_scripted_failure_hits_exactly_the_nth_write_or_delete_and_stores_nothing()
    {
        var boom = new IOException("disk full");
        Memory.FailWrite(2, boom);
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Assert.Same(boom, Assert.Throws<IOException>(() => Provider.Write(K("b.md"), B("two"), BundleWriteCondition.None)));
        Assert.False(Provider.Exists(K("b.md")));
        Provider.Write(K("b.md"), B("two"), BundleWriteCondition.None);
        Assert.True(Provider.Exists(K("b.md")));
    }

    [Fact]
    public void Writes_do_not_disturb_an_enumeration_in_progress()
    {
        Provider.Write(K("a.md"), B("1"), BundleWriteCondition.None);
        Provider.Write(K("b.md"), B("2"), BundleWriteCondition.None);
        var seen = new List<string>();
        foreach (var l in Provider.Enumerate(null, BundleLimits.Default))
        {
            seen.Add(l.RawName);
            Provider.Write(K("c.md"), B("3"), BundleWriteCondition.None);
        }

        Assert.Equal(["a.md", "b.md"], seen);
    }
}
