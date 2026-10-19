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

/// <summary>The constraint sets of the storages this library knows (spec §2, table of native constraints).</summary>
public static class BundleKeyConstraintSets
{
    /// <summary>NTFS/FAT as Windows exposes them. Names are stored in UTF-16, so an unpaired surrogate is no alias there: no <see cref="BundleKeyConstraints.WellFormedUnicode"/>.</summary>
    public static BundleKeyConstraints Windows =>
        BundleKeyConstraints.TrailingDotOrSpace
        | BundleKeyConstraints.WindowsIllegalCharacters
        | BundleKeyConstraints.WindowsReservedNames
        | BundleKeyConstraints.CaseInsensitiveCollisions
        | BundleKeyConstraints.FileDirectoryConflict;

    /// <summary>APFS/HFS+ as macOS exposes them by default; names are stored in UTF-8, hence <see cref="BundleKeyConstraints.WellFormedUnicode"/>.</summary>
    public static BundleKeyConstraints MacOS =>
        BundleKeyConstraints.CaseInsensitiveCollisions
        | BundleKeyConstraints.RequireNfc
        | BundleKeyConstraints.UnicodeFormCollisions
        | BundleKeyConstraints.FileDirectoryConflict
        | BundleKeyConstraints.WellFormedUnicode;

    /// <summary>A POSIX filesystem: the file/directory conflict and, since names are bytes written in UTF-8, <see cref="BundleKeyConstraints.WellFormedUnicode"/> beyond the base rules.</summary>
    public static BundleKeyConstraints Linux => BundleKeyConstraints.FileDirectoryConflict | BundleKeyConstraints.WellFormedUnicode;

    /// <summary>The union of every known set: a key valid here is valid on every provider (<see cref="BundleKeyPolicy.Strict"/>).</summary>
    public static BundleKeyConstraints Strict => Windows | MacOS | Linux;

    /// <summary>The set for the operating system this process runs on. The real boundary is the volume, not the OS (spec §2); hosts on mixed volumes keep <see cref="BundleKeyPolicy.Strict"/>.</summary>
    public static BundleKeyConstraints ForCurrentOperatingSystem() =>
        OperatingSystem.IsWindows() ? Windows
        : OperatingSystem.IsMacOS() ? MacOS
        : Linux;
}

/// <summary>Which constraints apply on top of a provider's own (spec §2, layer 3). Chosen by the host, never by content.</summary>
public enum BundleKeyPolicy
{
    /// <summary>Every known native set (the default): a bundle valid on one provider is valid on all.</summary>
    Strict,

    /// <summary>Only the current provider's native set.</summary>
    Native,
}
