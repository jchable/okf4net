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

    /// <summary>
    /// Whether an object exists under the key, matching it exactly (case and Unicode form).
    /// <see langword="false"/> means that no such object exists, with one carve-out: a listing
    /// the provider is denied, or that fails (the filesystem provider, when it matches names
    /// exactly, cannot list a directory on the key's path), also counts as "no such entry" — no
    /// object is read and nothing outside the root is reached. When the key's path is refused
    /// at an entry the provider reached — a reparse point, an entry that cannot be inspected,
    /// the confinement boundary — a provider throws
    /// <see cref="BundleAccessRefusedException"/>, exactly as <see cref="Read"/> would, and
    /// never answers <see langword="false"/>: a refused path is not an absent one.
    /// </summary>
    /// <exception cref="BundleAccessRefusedException">The key's path is refused.</exception>
    bool Exists(BundleKey key);

    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="key"/> if
    /// <paramref name="condition"/> holds, and returns the version a later <see cref="Read"/> of
    /// these bytes reports (spec §1, §4).
    /// <para><b>Conditions</b>, checked atomically with the replacement:
    /// <see cref="BundleWriteCondition.None"/> holds whatever the object's state (the storage may
    /// still report a creation that loses a race to another writer as
    /// <see cref="BundleWriteConflictException"/>: on the filesystem, a <c>None</c> creation can be
    /// reported as a conflict when another process creates the name between the provider's last
    /// check and its commit, see the conflicts under <c>None</c> in
    /// <see cref="FileSystemBundleProvider.Write"/>);
    /// <see cref="BundleWriteCondition.MustNotExist"/> holds while no object exists under the key;
    /// <see cref="BundleWriteCondition.IfMatch"/> holds while an object exists under the key and
    /// its current version is the expected one — never for an absent object.</para>
    /// <para><b>Atomic</b>, per object: whatever fails, the key holds the previous content (no
    /// object, for a creation) or the whole new content, never a part of it, and no reader, in
    /// this process or another, ever reads a part of the new content. A provider documents
    /// whether a concurrent reader can, while the commit runs, momentarily find no object under
    /// the key (<see cref="FileSystemBundleProvider"/> can, on Windows). When the call throws
    /// anything but <see cref="BundleWriteOutcomeUnknownException"/>, the stored object is
    /// unchanged. There is no transaction across objects.</para>
    /// <para><b>Prefixes are implicit</b>: there are no directories in a key space, so no parent
    /// needs creating; a provider whose storage has directories creates the missing ones itself,
    /// under its own path guards.</para>
    /// <para>The core has validated the key under its policy before the call (spec §2); the
    /// provider adds only its own aliasing guarantees — it matches stored names exactly, and
    /// refuses a name its storage would confuse with another.</para>
    /// </summary>
    /// <returns>The new version.</returns>
    /// <exception cref="BundleReadOnlyException"><see cref="BundleProviderCapabilities.CanWrite"/> is <see langword="false"/>; nothing was attempted.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold, or the storage cannot hold an object under that exact name (<see cref="BundleWriteConflictException.Reason"/>); nothing was changed.</exception>
    /// <exception cref="BundleAccessRefusedException">The key's path is refused (a reparse point, an entry that cannot be inspected, the confinement boundary); nothing was changed.</exception>
    /// <exception cref="BundleWriteOutcomeUnknownException">The commit itself failed and what the storage now holds could not be established.</exception>
    /// <exception cref="BundleWriteNotCommittedException">The storage refused the commit, without any effect, on every one of a bounded number of attempts (the filesystem provider: Windows, a replacement kept refused); the object is unchanged.</exception>
    /// <exception cref="BundleObjectTooLargeException">An <see cref="BundleWriteCondition.IfMatch"/> write found the existing object longer than <see cref="BundleLimits.MaxObjectBytes"/>, whose version cannot be established; nothing was changed.</exception>
    /// <exception cref="IOException">The storage failed before the commit, or at a commit that changed nothing (the filesystem provider passes a raw <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> up as it is: its message may name a host path, decision B15, issue #211); the object is unchanged.</exception>
    /// <exception cref="UnauthorizedAccessException">The storage denied an access before the commit, as above; the object is unchanged.</exception>
    BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition);

    /// <summary>
    /// Removes the object under <paramref name="key"/> if <paramref name="condition"/> holds
    /// (spec §1, §4), atomically. The conditions read as for <see cref="Write"/>:
    /// <see cref="BundleWriteCondition.None"/> always holds,
    /// <see cref="BundleWriteCondition.MustNotExist"/> only when there is nothing to delete, and
    /// <see cref="BundleWriteCondition.IfMatch"/> only while the object exists at that version.
    /// Deleting an absent object under a condition that holds does nothing and is not an error. A
    /// prefix stops existing with the last object under it; a storage with directories may leave
    /// them in place, since an empty directory holds no key.
    /// </summary>
    /// <exception cref="BundleReadOnlyException"><see cref="BundleProviderCapabilities.CanWrite"/> is <see langword="false"/>; nothing was attempted.</exception>
    /// <exception cref="BundleWriteConflictException">The condition did not hold; nothing was changed.</exception>
    /// <exception cref="BundleAccessRefusedException">The key's path is refused; nothing was changed.</exception>
    /// <exception cref="BundleObjectTooLargeException">An <see cref="BundleWriteCondition.IfMatch"/> delete found the object longer than <see cref="BundleLimits.MaxObjectBytes"/>, whose version cannot be established; nothing was changed.</exception>
    /// <exception cref="IOException">The storage failed (the filesystem provider passes a raw <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> up as it is: a denied listing of a directory on the key's path, a failed removal; its message may name a host path, decision B15, issue #211).</exception>
    /// <exception cref="UnauthorizedAccessException">The storage denied an access, as above.</exception>
    void Delete(BundleKey key, BundleWriteCondition condition);
}
