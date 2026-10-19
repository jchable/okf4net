// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers;

/// <summary>
/// Storage-specific aliasing rules a provider declares (spec §2, layer 2). Each flag
/// names a way two distinct keys could designate the same object on that storage.
/// </summary>
[Flags]
public enum BundleKeyConstraints
{
    /// <summary>No constraint beyond the base rules.</summary>
    None = 0,

    /// <summary>A segment must not end with <c>.</c> or a space (Windows strips them).</summary>
    TrailingDotOrSpace = 1 << 0,

    /// <summary>A segment must not contain <c>&lt; &gt; : " | ? *</c> (Windows; <c>:</c> also opens an NTFS stream).</summary>
    WindowsIllegalCharacters = 1 << 1,

    /// <summary>A segment's base (before the first <c>.</c>) must not be a reserved Windows device name.</summary>
    WindowsReservedNames = 1 << 2,

    /// <summary>Two siblings must not be equal once folded the way a filesystem upcase table folds.</summary>
    CaseInsensitiveCollisions = 1 << 3,

    /// <summary>Every segment must pass the NFC quick check (no <c>NFC_QC=No</c> character, no composing mark).</summary>
    RequireNfc = 1 << 4,

    /// <summary>
    /// Two siblings must not be canonically equivalent (APFS compares normalized names):
    /// sibling names are compared in canonical decomposition (<c>CanonicalForm.Decompose</c>),
    /// independently of <see cref="RequireNfc"/> — a decomposed name that the quick check
    /// refuses is still a file beside its precomposed twin.
    /// </summary>
    UnicodeFormCollisions = 1 << 5,

    /// <summary>An object key must not also be a prefix of other keys (a file cannot be a directory).</summary>
    FileDirectoryConflict = 1 << 6,

    /// <summary>
    /// A segment must not contain an unpaired surrogate: no UTF-8 storage can represent it
    /// injectively (an encoder turns every lone surrogate into U+FFFD, so <c>"\uD800.sql"</c>
    /// and <c>"\uD801.sql"</c> would be written to the same name). A storage constraint, not a
    /// base rule: NTFS stores UTF-16 and holds both names.
    /// </summary>
    WellFormedUnicode = 1 << 7,
}

/// <summary>
/// The constraint sets of the storages this library knows (spec §2, table of native
/// constraints). A native set refuses only what its storage itself cannot hold, or would
/// alias: names that cannot be created there, or two names that would designate the same
/// object. Anything stricter is portability, which belongs to <see cref="Strict"/>.
/// </summary>
public static class BundleKeyConstraintSets
{
    /// <summary>
    /// NTFS/FAT as Windows exposes them through Win32, on a default (case-insensitive)
    /// directory. A name ending in <c>.</c> or a space, carrying <c>&lt; &gt; : " | ? *</c>, or
    /// whose base is a reserved device name (<c>con.md</c>) cannot be created through Win32;
    /// two siblings that differ only by case are one entry. A case-sensitive directory
    /// (per-directory flag, WSL) holds such siblings, and this set still refuses them: it fails
    /// closed there. Names are stored in UTF-16, so an unpaired surrogate is no alias: no
    /// <see cref="BundleKeyConstraints.WellFormedUnicode"/>.
    /// </summary>
    public static BundleKeyConstraints Windows =>
        BundleKeyConstraints.TrailingDotOrSpace
        | BundleKeyConstraints.WindowsIllegalCharacters
        | BundleKeyConstraints.WindowsReservedNames
        | BundleKeyConstraints.CaseInsensitiveCollisions
        | BundleKeyConstraints.FileDirectoryConflict;

    /// <summary>
    /// APFS/HFS+ as macOS formats them by default: case-insensitive and
    /// normalization-insensitive, so two siblings that differ only by case or Unicode form
    /// are one entry. No <see cref="BundleKeyConstraints.RequireNfc"/>: HFS+ stores names
    /// decomposed and APFS keeps whatever form was written, so a decomposed name is an
    /// ordinary file there. A case-sensitive APFS volume holds siblings that differ only by
    /// case, and this set still refuses them: it fails closed there. Names are stored in
    /// UTF-8, hence <see cref="BundleKeyConstraints.WellFormedUnicode"/>.
    /// </summary>
    public static BundleKeyConstraints MacOS =>
        BundleKeyConstraints.CaseInsensitiveCollisions
        | BundleKeyConstraints.UnicodeFormCollisions
        | BundleKeyConstraints.FileDirectoryConflict
        | BundleKeyConstraints.WellFormedUnicode;

    /// <summary>
    /// A POSIX filesystem on a case-sensitive volume: the file/directory conflict and, since
    /// names are bytes written in UTF-8, <see cref="BundleKeyConstraints.WellFormedUnicode"/>
    /// beyond the base rules. A host reading a case-insensitive mount (a FAT or NTFS volume, an
    /// ext4 casefold directory) adds <see cref="BundleKeyConstraints.CaseInsensitiveCollisions"/>
    /// through <see cref="FileSystemBundleProvider"/>'s <c>additionalConstraints</c>.
    /// </summary>
    public static BundleKeyConstraints Linux => BundleKeyConstraints.FileDirectoryConflict | BundleKeyConstraints.WellFormedUnicode;

    /// <summary>
    /// The union of every known set, plus <see cref="BundleKeyConstraints.RequireNfc"/> (no
    /// storage requires it, but a name in NFC is spelled the same way on every one): a key
    /// valid here is valid on every provider. The set of <see cref="BundleKeyPolicy.Strict"/>.
    /// </summary>
    public static BundleKeyConstraints Strict => Windows | MacOS | Linux | BundleKeyConstraints.RequireNfc;

    /// <summary>The set for the operating system this process runs on, on its default volumes. The real boundary is the volume, not the OS (spec §2): see each set for what it assumes.</summary>
    public static BundleKeyConstraints ForCurrentOperatingSystem() =>
        OperatingSystem.IsWindows() ? Windows
        : OperatingSystem.IsMacOS() ? MacOS
        : Linux;
}

/// <summary>Which constraints apply on top of a provider's own (spec §2, layer 3). Chosen by the host, never by content.</summary>
public enum BundleKeyPolicy
{
    /// <summary>
    /// Opt-in portable profile, for hosts that must produce or consume bundles that survive
    /// any filesystem: every known native set plus <see cref="BundleKeyConstraints.RequireNfc"/>
    /// (<see cref="BundleKeyConstraintSets.Strict"/>), on top of the provider's own. A key valid
    /// on one provider is then valid on all.
    /// </summary>
    Strict,

    /// <summary>
    /// The default (<see cref="BundleLoadOptions.KeyPolicy"/>): the provider's own constraint
    /// set, never below it. Refuses only what this provider's storage itself cannot hold or
    /// would alias.
    /// </summary>
    Native,
}
