// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// The write half of the provider contract (spec §1, §3, §4) as tests every writable
/// provider must pass. A provider's test class derives from this one and hands it a fresh,
/// empty, writable provider; nothing here knows how the provider stores its bytes, so the
/// in-memory and the filesystem providers are held to exactly the same rules.
/// </summary>
public abstract class WritableProviderContractTests
{
    private IBundleProvider? _provider;

    /// <summary>A new, empty provider whose <see cref="BundleProviderCapabilities.CanWrite"/> is true.</summary>
    protected abstract IBundleProvider CreateEmptyProvider();

    /// <summary>A second provider over the same storage as <see cref="Provider"/> (another instance, another client).</summary>
    protected abstract IBundleProvider OpenSameStorage();

    /// <summary>The provider under test, created on first use.</summary>
    protected IBundleProvider Provider => _provider ??= CreateEmptyProvider();

    protected static BundleKey K(string s) => BundleKey.Parse(s);

    protected static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    protected string ReadText(string key)
    {
        using var obj = Provider.Read(K(key));
        using var reader = new StreamReader(obj.Content, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_provider_declares_that_it_can_write()
    {
        Assert.True(Provider.Capabilities.CanWrite);
    }

    [Fact]
    public void A_create_stores_the_bytes_and_returns_the_version_a_read_reports()
    {
        var v = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.MustNotExist);
        Assert.True(Provider.Exists(K("a.md")));
        using var obj = Provider.Read(K("a.md"));
        Assert.Equal(v, obj.Version);
        Assert.Equal("one", ReadText("a.md"));
    }

    [Fact]
    public void Versions_follow_the_bytes()
    {
        var one = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        var two = Provider.Write(K("b.md"), B("two"), BundleWriteCondition.None);
        var again = Provider.Write(K("c.md"), B("one"), BundleWriteCondition.None);
        Assert.NotEqual(one, two);
        Assert.Equal(one, again);
    }

    [Fact]
    public void An_empty_object_is_an_object()
    {
        var v = Provider.Write(K("empty.md"), ReadOnlyMemory<byte>.Empty, BundleWriteCondition.MustNotExist);
        using var obj = Provider.Read(K("empty.md"));
        Assert.Equal(v, obj.Version);
        Assert.Equal(-1, obj.Content.ReadByte());
    }

    [Fact]
    public void MustNotExist_refuses_an_existing_object_and_changes_nothing()
    {
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        var ex = Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("a.md"), B("two"), BundleWriteCondition.MustNotExist));
        Assert.Equal(K("a.md"), ex.Key);
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, ex.Reason);
        Assert.Equal("one", ReadText("a.md"));
    }

    [Fact]
    public void IfMatch_replaces_only_the_version_it_names()
    {
        var v1 = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        var v2 = Provider.Write(K("a.md"), B("two"), BundleWriteCondition.IfMatch(v1));
        Assert.Equal("two", ReadText("a.md"));

        var stale = Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("a.md"), B("three"), BundleWriteCondition.IfMatch(v1)));
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, stale.Reason);
        Assert.Equal("two", ReadText("a.md"));

        Provider.Write(K("a.md"), B("three"), BundleWriteCondition.IfMatch(v2));
        Assert.Equal("three", ReadText("a.md"));
    }

    [Fact]
    public void IfMatch_never_holds_for_an_absent_object()
    {
        var v = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("b.md"), B("one"), BundleWriteCondition.IfMatch(v)));
        Assert.False(Provider.Exists(K("b.md")));
    }

    [Fact]
    public void None_creates_then_replaces()
    {
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Provider.Write(K("a.md"), B("two"), BundleWriteCondition.None);
        Assert.Equal("two", ReadText("a.md"));
    }

    [Fact]
    public void Prefixes_are_implicit_on_write_and_on_delete()
    {
        Provider.Write(K("a/b/c.md"), B("deep"), BundleWriteCondition.MustNotExist);
        Assert.Equal(["a/b/c.md"], Provider.Enumerate(K("a"), BundleLimits.Default).Select(l => l.RawName));
        Assert.Contains("a/b/c.md", Provider.Enumerate(null, BundleLimits.Default).Select(l => l.RawName));

        Provider.Delete(K("a/b/c.md"), BundleWriteCondition.None);
        Assert.Empty(Provider.Enumerate(K("a"), BundleLimits.Default));
        Assert.DoesNotContain(Provider.Enumerate(null, BundleLimits.Default), l => l.RawName.StartsWith("a/", StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_honours_every_condition()
    {
        var v1 = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);

        Assert.Equal(BundleWriteConflictReason.ConditionFailed, Assert.Throws<BundleWriteConflictException>(() => Provider.Delete(K("a.md"), BundleWriteCondition.MustNotExist)).Reason);
        Assert.Throws<BundleWriteConflictException>(() => Provider.Delete(K("a.md"), BundleWriteCondition.IfMatch(new BundleObjectVersion("stale"))));
        Assert.Equal("one", ReadText("a.md"));

        Provider.Delete(K("a.md"), BundleWriteCondition.IfMatch(v1));
        Assert.False(Provider.Exists(K("a.md")));
        Assert.Throws<BundleObjectNotFoundException>(() => Provider.Read(K("a.md")));

        Provider.Write(K("b.md"), B("two"), BundleWriteCondition.None);
        Provider.Delete(K("b.md"), BundleWriteCondition.None);
        Assert.False(Provider.Exists(K("b.md")));
    }

    [Fact]
    public void Deleting_an_absent_object_is_a_no_op_unless_a_version_was_expected()
    {
        Provider.Delete(K("nope.md"), BundleWriteCondition.None);
        Provider.Delete(K("nope.md"), BundleWriteCondition.MustNotExist);
        Provider.Delete(K("no/such/prefix.md"), BundleWriteCondition.None);
        Assert.Throws<BundleWriteConflictException>(() => Provider.Delete(K("nope.md"), BundleWriteCondition.IfMatch(new BundleObjectVersion("x"))));
        Assert.False(Provider.Exists(K("nope.md")));
    }

    [Fact]
    public void The_bundle_version_moves_with_every_write_and_delete()
    {
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        var v1 = Provider.GetVersion(BundleLimits.Default);
        Provider.Write(K("b.md"), B("two"), BundleWriteCondition.None);
        var v2 = Provider.GetVersion(BundleLimits.Default);
        Provider.Delete(K("b.md"), BundleWriteCondition.None);
        var v3 = Provider.GetVersion(BundleLimits.Default);
        Assert.NotEqual(v1, v2);
        Assert.NotEqual(v2, v3);
    }

    [Fact]
    public void A_conflict_message_names_the_key_and_nothing_of_the_storage()
    {
        Provider.Write(K("dir/a.md"), B("one"), BundleWriteCondition.None);
        var ex = Assert.Throws<BundleWriteConflictException>(() => Provider.Write(K("dir/a.md"), B("two"), BundleWriteCondition.MustNotExist));
        Assert.Contains("'dir/a.md'", ex.Message);
        Assert.DoesNotContain(Provider.Location, ex.Message);
        Assert.DoesNotContain(Provider.Id, ex.Message);
    }

    [Fact]
    public void The_read_only_view_refuses_every_write_and_changes_nothing()
    {
        Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        var ro = Provider.AsReadOnly();
        Assert.False(ro.Capabilities.CanWrite);
        Assert.Throws<BundleReadOnlyException>(() => ro.Write(K("a.md"), B("two"), BundleWriteCondition.None));
        Assert.Throws<BundleReadOnlyException>(() => ro.Write(K("new.md"), B("two"), BundleWriteCondition.MustNotExist));
        Assert.Throws<BundleReadOnlyException>(() => ro.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.Equal("one", ReadText("a.md"));
        Assert.False(Provider.Exists(K("new.md")));
    }

    /// <summary>
    /// Two writers holding the same version race to replace it, each through its own provider
    /// instance over the same storage: exactly one wins, the other gets a conflict, and the
    /// stored bytes are the winner's. Fifty rounds, because a check-then-replace that is not
    /// atomic loses this race only sometimes.
    /// </summary>
    [Fact]
    public void Concurrent_IfMatch_writers_on_one_version_have_exactly_one_winner()
    {
        var other = OpenSameStorage();
        for (var round = 0; round < 50; round++)
        {
            var key = K($"race/{round}.md");
            var v0 = Provider.Write(key, B("v0"), BundleWriteCondition.None);
            using var start = new Barrier(2);
            var outcomes = new string?[2];
            var errors = new Exception?[2];
            var writers = new[] { Provider, other }.Select((p, i) => new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    p.Write(key, B($"writer-{i}"), BundleWriteCondition.IfMatch(v0));
                    outcomes[i] = "won";
                }
                catch (BundleWriteConflictException)
                {
                    outcomes[i] = "conflict";
                }
                catch (Exception e)
                {
                    errors[i] = e;
                }
            })).ToArray();
            foreach (var t in writers)
            {
                t.Start();
            }

            foreach (var t in writers)
            {
                t.Join();
            }

            Assert.All(errors, e => Assert.Null(e));
            Assert.Equal(1, outcomes.Count(o => o == "won"));
            Assert.Equal(1, outcomes.Count(o => o == "conflict"));
            var winner = Array.IndexOf(outcomes, "won");
            Assert.Equal($"writer-{winner}", ReadText(key.ToString()));
        }
    }
}
