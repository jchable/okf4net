// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>The write contract on the filesystem provider: the same tests the in-memory provider passes, over a fresh directory.</summary>
public sealed class FileSystemBundleProviderContractTests : WritableProviderContractTests, IDisposable
{
    private readonly TempDir _tmp = new();

    protected override IBundleProvider CreateEmptyProvider() => new FileSystemBundleProvider(_tmp.Path);

    /// <summary>A second instance over the same root: the commit lock must hold across instances, not per instance.</summary>
    protected override IBundleProvider OpenSameStorage() => new FileSystemBundleProvider(_tmp.Path);

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Versions_are_the_lowercase_hex_sha256_of_the_bytes_like_the_in_memory_provider()
    {
        var v = Provider.Write(K("a.md"), B("one"), BundleWriteCondition.None);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(B("one"))), v.Token);
    }

    [Fact]
    public void No_temporary_is_left_behind_by_the_whole_contract_run()
    {
        Concurrent_IfMatch_writers_on_one_version_have_exactly_one_winner();
        Assert.Empty(((FileSystemBundleProvider)Provider).ListOrphanedTemporaries(BundleLimits.Default));
    }
}
