// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// A storage behind a bundle (spec §1): a key space and the bytes under each key.
/// A provider knows nothing of concepts, links or frontmatter; it lists raw names,
/// reads and writes bytes under keys the core has already validated, and declares
/// what it can do. There are no directories: a prefix exists only while an object
/// lives under it. Reading is mandatory; writing is a declared capability.
/// </summary>
public interface IBundleProvider
{
    /// <summary>A stable, non-secret identity: diagnostics, catalog de-duplication, lock key for non-filesystem providers.</summary>
    string Id { get; }

    /// <summary>A displayable, non-secret location (the root as given, for the filesystem).</summary>
    string Location { get; }

    /// <summary>What this provider can do beyond reading.</summary>
    BundleProviderCapabilities Capabilities { get; }

    /// <summary>The aliasing rules of this storage (spec §2, layer 2); never disabled by any policy.</summary>
    BundleKeyConstraints NativeConstraints { get; }

    /// <summary>A cheap token for the whole bundle; its internal walk respects <paramref name="limits"/>.</summary>
    BundleVersion GetVersion(BundleLimits limits);

    /// <summary>Lists raw names under the root, or under <paramref name="prefix"/> at a segment boundary (<c>a</c> covers <c>a/…</c>, never <c>ab/…</c>). The core validates them; the provider does not.</summary>
    IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits);

    /// <summary>Reads one object. With <paramref name="expected"/>, succeeds only if the current version matches, else throws <see cref="BundleVersionMismatchException"/>.</summary>
    /// <exception cref="BundleObjectNotFoundException">No object under the key.</exception>
    BundleObject Read(BundleKey key, BundleObjectVersion? expected = null);

    /// <summary>Whether an object exists under the key, matching it exactly (case and Unicode form).</summary>
    bool Exists(BundleKey key);

    /// <summary>Writes an object atomically if <paramref name="condition"/> holds; returns the new version.</summary>
    /// <exception cref="BundleReadOnlyException">The provider cannot write.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold.</exception>
    BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition);

    /// <summary>Deletes an object if <paramref name="condition"/> holds.</summary>
    /// <exception cref="BundleReadOnlyException">The provider cannot write.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold.</exception>
    void Delete(BundleKey key, BundleWriteCondition condition);
}
