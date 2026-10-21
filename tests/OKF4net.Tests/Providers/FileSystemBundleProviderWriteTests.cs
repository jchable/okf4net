// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>The filesystem provider's write side: resolution for write, the temporary, the commit, and the guards around them (spec §4, §8; issue #157).</summary>
public class FileSystemBundleProviderWriteTests
{
    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(B(s)));

    /// <summary>Every entry under <paramref name="root"/>, relative, '/'-separated, sorted: what is really on disk, temporaries included.</summary>
    private static string[] Tree(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void A_write_creates_the_object_and_its_missing_parents_and_leaves_nothing_else()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        var v = p.Write(K("a/b/c.md"), B("deep"), BundleWriteCondition.MustNotExist);
        Assert.Equal(Sha("deep"), v.Token);
        Assert.Equal("deep", File.ReadAllText(Path.Combine(tmp.Path, "a", "b", "c.md")));
        Assert.Equal(["a", "a/b", "a/b/c.md"], Tree(tmp.Path));
    }

    [Fact]
    public void A_write_replaces_an_object_another_process_wrote()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(Sha("new"), p.Write(K("x.md"), B("new"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("old")))).Token);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    [Fact]
    public void Conditions_are_checked_against_the_stored_bytes_and_a_refusal_leaves_nothing_behind()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleWriteConflictException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.MustNotExist));
        Assert.Throws<BundleWriteConflictException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("other")))));
        Assert.Throws<BundleWriteConflictException>(() => p.Write(K("y.md"), B("new"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("old")))));
        Assert.Equal("old", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// On a volume that folds case (NTFS, APFS by default, a vfat or CIFS mount under Linux) the
    /// OS would open or create the stored twin for a name that differs only in case: the write is
    /// refused instead, with nothing created. On a volume that keeps both (ext4), both names are
    /// distinct objects and both writes succeed — the provider follows the volume, not the OS.
    /// </summary>
    [Fact]
    public void A_name_the_volume_would_confuse_with_a_stored_one_is_refused_and_creates_nothing()
    {
        using var tmp = new TempDir();
        tmp.Write("Twin/x.md", "x");
        var folds = Directory.Exists(Path.Combine(tmp.Path, "twin"));
        var p = new FileSystemBundleProvider(tmp.Path);
        if (folds)
        {
            var dir = Assert.Throws<BundleWriteConflictException>(() => p.Write(K("twin/y.md"), B("y"), BundleWriteCondition.None));
            Assert.Equal(BundleWriteConflictReason.NameUnavailable, dir.Reason);
            var leaf = Assert.Throws<BundleWriteConflictException>(() => p.Write(K("Twin/X.md"), B("X"), BundleWriteCondition.None));
            Assert.Equal(BundleWriteConflictReason.NameUnavailable, leaf.Reason);
            Assert.Equal(["Twin", "Twin/x.md"], Tree(tmp.Path));
            Assert.Equal("x", File.ReadAllText(Path.Combine(tmp.Path, "Twin", "x.md")));
        }
        else
        {
            p.Write(K("twin/y.md"), B("y"), BundleWriteCondition.None);
            p.Write(K("Twin/X.md"), B("X"), BundleWriteCondition.None);
            Assert.Equal(["Twin", "Twin/X.md", "Twin/x.md", "twin", "twin/y.md"], Tree(tmp.Path));
        }
    }

    [SkippableTheory]
    [InlineData("x.")]
    [InlineData("x ")]
    [InlineData("d./x.md")]
    [InlineData("a:b.md")]
    [InlineData("C:x.md")]
    public void On_Windows_a_name_no_entry_can_carry_literally_is_refused_before_any_effect(string key)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "a Windows name rule: elsewhere these names are stored as written");
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        var ex = Assert.Throws<BundleWriteConflictException>(() => p.Write(K(key), B("x"), BundleWriteCondition.None));
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, ex.Reason);
        Assert.Empty(Tree(tmp.Path));
    }

    [Fact]
    public void A_file_where_a_directory_is_needed_and_a_directory_under_the_key_are_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "a");
        tmp.Write("d/x.md", "x");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "empty"));
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, Assert.Throws<BundleWriteConflictException>(() => p.Write(K("a.md/b.md"), B("b"), BundleWriteCondition.None)).Reason);
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, Assert.Throws<BundleWriteConflictException>(() => p.Write(K("d"), B("d"), BundleWriteCondition.None)).Reason);
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, Assert.Throws<BundleWriteConflictException>(() => p.Write(K("empty"), B("e"), BundleWriteCondition.None)).Reason);
        Assert.Equal(["a.md", "d", "d/x.md", "empty"], Tree(tmp.Path));
    }

    [SkippableFact]
    public void A_junction_on_the_path_is_refused_and_nothing_reaches_its_target()
    {
        using var external = new TempDir();
        using var tmp = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("j", external.Path), "junctions unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Write(K("j/x.md"), B("x"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Write(K("j/new/x.md"), B("x"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Delete(K("j/x.md"), BundleWriteCondition.None));
        Assert.Empty(Tree(external.Path));
    }

    [SkippableFact]
    public void A_symlinked_object_is_refused_and_its_target_is_untouched()
    {
        using var external = new TempDir();
        var outside = external.Write("secret.md", "outside");
        using var tmp = new TempDir();
        Skip.IfNot(tmp.TryCreateFileSymlinkToExternalFile("link.md", outside), "file symlinks unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.Throws<BundleAccessRefusedException>(() => p.Write(K("link.md"), B("pwned"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Delete(K("link.md"), BundleWriteCondition.None));
        Assert.Equal("outside", File.ReadAllText(outside));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "link.md")));
    }

    [SkippableFact]
    public void The_confinement_boundary_is_re_checked_before_a_write()
    {
        using var external = new TempDir();
        using var tmp = new TempDir();
        tmp.Write("src/a.md", "inside");
        var root = Path.Combine(tmp.Path, "src");
        var p = new FileSystemBundleProvider(root, confinementBoundary: tmp.Path);
        p.Write(K("a.md"), B("still inside"), BundleWriteCondition.None);

        Directory.Delete(root, recursive: true);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("src", external.Path), "junctions unavailable");
        Assert.Throws<BundleAccessRefusedException>(() => p.Write(K("a.md"), B("escaped"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.Empty(Tree(external.Path));
    }

    /// <summary>
    /// The writer's <c>Directory.CreateDirectory</c> of the target's parent created a bundle root
    /// that did not exist yet; the provider keeps that: a write over an absent root creates it,
    /// for a nested key and a top-level one, with or without a confinement boundary (here the
    /// nearest existing parent), and leaves nothing but the object.
    /// </summary>
    [Theory]
    [InlineData("a/b/c.md", false)]
    [InlineData("top.md", false)]
    [InlineData("a/b/c.md", true)]
    [InlineData("top.md", true)]
    public void A_write_over_an_absent_root_creates_the_root(string key, bool confined)
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "new", "bundle");
        var p = new FileSystemBundleProvider(root, confined ? tmp.Path : null);
        Assert.False(Directory.Exists(root));

        var v = p.Write(K(key), B("first"), BundleWriteCondition.MustNotExist);

        Assert.Equal(Sha("first"), v.Token);
        Assert.Equal("first", File.ReadAllText(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(key.Contains('/') ? new[] { "a", "a/b", "a/b/c.md" } : new[] { "top.md" }, Tree(root));
        using var read = p.Read(K(key));
        Assert.Equal(v, read.Version);
    }

    /// <summary>The root is created only inside its confinement boundary: a boundary that does not contain it refuses the write as before and creates nothing.</summary>
    [Fact]
    public void A_write_over_an_absent_root_outside_its_confinement_boundary_is_refused_and_creates_nothing()
    {
        using var tmp = new TempDir();
        using var boundary = new TempDir();
        var root = Path.Combine(tmp.Path, "new", "bundle");
        var p = new FileSystemBundleProvider(root, confinementBoundary: boundary.Path);

        Assert.Throws<BundleAccessRefusedException>(() => p.Write(K("a/b.md"), B("x"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Write(K("b.md"), B("x"), BundleWriteCondition.None));

        Assert.False(Directory.Exists(root));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "new")));
        Assert.Empty(Tree(boundary.Path));
    }

    /// <summary>
    /// A delete through a link in the path is refused and nothing outside the root is touched,
    /// whether the object lies directly under the link or deeper, and even when the link's
    /// target holds an object under the same relative name.
    /// </summary>
    [SkippableFact]
    public void A_delete_through_a_junction_is_refused_and_leaves_the_outside_alone()
    {
        using var external = new TempDir();
        external.Write("x.md", "outside");
        external.Write("sub/x.md", "outside deep");
        using var tmp = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("j", external.Path), "junctions unavailable");
        var p = new FileSystemBundleProvider(tmp.Path);

        Assert.Throws<BundleAccessRefusedException>(() => p.Delete(K("j/x.md"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Delete(K("j/sub/x.md"), BundleWriteCondition.None));
        Assert.Throws<BundleAccessRefusedException>(() => p.Delete(K("j/sub/x.md"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("outside deep")))));

        Assert.Equal("outside", File.ReadAllText(Path.Combine(external.Path, "x.md")));
        Assert.Equal("outside deep", File.ReadAllText(Path.Combine(external.Path, "sub", "x.md")));
    }

    /// <summary>
    /// A directory on the key's path that the process may not search is refused as an access
    /// refusal before anything is listed (the whole path is STRICT-checked up front, as for a
    /// write), not as the raw access-denied error of listing it. Unix only: Windows lets the
    /// process through a directory whose listing is denied, so no listing refusal arises there.
    /// </summary>
    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_delete_under_a_directory_the_process_may_not_search_is_an_access_refusal_not_a_raw_error()
    {
        Skip.If(OperatingSystem.IsWindows(), "the mode of a directory is a Unix notion");
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "kept");
        var dir = Path.Combine(tmp.Path, "d");
        File.SetUnixFileMode(dir, UnixFileMode.None);
        try
        {
            var searchable = true;
            try
            {
                _ = File.GetAttributes(Path.Combine(dir, "x.md"));
            }
            catch (UnauthorizedAccessException)
            {
                searchable = false;
            }

            Skip.If(searchable, "the process searches every directory anyway (root)");
            var p = new FileSystemBundleProvider(tmp.Path);
            Assert.Throws<BundleAccessRefusedException>(() => p.Delete(K("d/x.md"), BundleWriteCondition.None));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal("kept", File.ReadAllText(Path.Combine(dir, "x.md")));
    }

    /// <summary>
    /// An <c>IfMatch</c> condition over an object longer than the provider's
    /// <see cref="BundleLimits.MaxObjectBytes"/> is refused before any byte is hashed (a read never
    /// returns a version for such an object, so the condition could only fail, and the hash would
    /// run under the commit lock): <see cref="BundleObjectTooLargeException"/>, for a write and for
    /// a delete, nothing written, nothing left behind. A write or delete under an in-bound
    /// object still works.
    /// </summary>
    [Fact]
    public void An_IfMatch_over_an_object_past_the_bound_is_refused_before_hashing_and_leaves_nothing()
    {
        using var tmp = new TempDir();
        var big = tmp.Write("big.md", "0123456789 and more");
        tmp.Write("small.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path, limits: new BundleLimits { MaxObjectBytes = 10 });
        var before = Tree(tmp.Path);
        var matching = BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("0123456789 and more")));

        Assert.Throws<BundleObjectTooLargeException>(() => p.Write(K("big.md"), B("new"), matching));
        Assert.Throws<BundleObjectTooLargeException>(() => p.Delete(K("big.md"), matching));

        Assert.Equal("0123456789 and more", File.ReadAllText(big));
        Assert.Equal(before, Tree(tmp.Path));
        Assert.Empty(p.ListOrphanedTemporaries(BundleLimits.Default));

        Assert.Equal(Sha("new"), p.Write(K("small.md"), B("new"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("old")))).Token);
        p.Delete(K("small.md"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("new"))));
        Assert.Equal(["big.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// The temporary is written without a managed buffer (<c>BufferSize = 0</c>): when the flush
    /// hook runs, every byte is already on the file, where a 4 KiB buffer would hold a small
    /// content back and a disk error would surface only at the flush. Unix only: Windows reports
    /// the size of an open file from its directory entry, which lags.
    /// </summary>
    [SkippableFact]
    public void The_temporary_is_unbuffered_every_byte_is_on_the_file_before_the_flush()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows reports the size of an open file from its directory entry, which lags the writes");
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        var content = B("a small content, far below any buffer size");
        long lengthAtFlush = -1;
        p.TemporaryFlushForTest = path => lengthAtFlush = new FileInfo(path).Length;

        p.Write(K("x.md"), content, BundleWriteCondition.None);

        Assert.Equal(content.Length, lengthAtFlush);
    }

    /// <summary>
    /// File.WriteAllText refused to overwrite a file the process may not write. A rename needs
    /// only the directory's permission on Unix, so the provider opens the target for writing
    /// first and refuses as before: a read-only object stays read-only.
    /// </summary>
    [SkippableFact]
    public void A_target_the_process_may_not_write_is_refused_as_before_and_left_as_it_was()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("ro.md", "old");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            var privileged = true;
            try
            {
                using (File.OpenHandle(file, FileMode.Open, FileAccess.Write))
                {
                }
            }
            catch (UnauthorizedAccessException)
            {
                privileged = false;
            }

            Skip.If(privileged, "the process writes read-only files anyway (root)");
        }

        try
        {
            var p = new FileSystemBundleProvider(tmp.Path);

            // IfMatch opens the target for reading and writing to hash it: that open is what refuses
            // a replacement of a file the process may not write (a rename needs only the directory's
            // permission on Unix).
            Assert.Throws<UnauthorizedAccessException>(() => p.Write(K("ro.md"), B("conditional"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("old")))));
            Assert.Equal("old", File.ReadAllText(file));
            Assert.Equal(["ro.md"], Tree(tmp.Path));
            Assert.Throws<UnauthorizedAccessException>(() => p.Write(K("ro.md"), B("new"), BundleWriteCondition.None));
            Assert.Equal("old", File.ReadAllText(file));
            Assert.Equal(["ro.md"], Tree(tmp.Path));
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    /// <summary>
    /// File.WriteAllText needed only the right to write: a file the process may write but not
    /// read (mode 0200 on Unix, an ACE denying ReadData on Windows) was overwritten. An
    /// unconditional write still replaces it — only IfMatch reads the target, to compare its
    /// SHA-256, and that write is refused with the access error, since the version cannot be
    /// established — and the temporary of the refused write is removed.
    /// </summary>
    [SkippableFact]
    public void A_target_the_process_may_write_but_not_read_is_replaced_unconditionally_and_refused_under_IfMatch()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("wo.md", "old");
        SetReadable(file, false);
        try
        {
            var readable = true;
            try
            {
                using (File.OpenHandle(file, FileMode.Open, FileAccess.Read))
                {
                }
            }
            catch (UnauthorizedAccessException)
            {
                readable = false;
            }

            Skip.If(readable, "the process reads unreadable files anyway (root)");
            var p = new FileSystemBundleProvider(tmp.Path);
            Assert.Throws<UnauthorizedAccessException>(() => p.Write(K("wo.md"), B("conditional"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("old")))));
            Assert.Equal(["wo.md"], Tree(tmp.Path));
            Assert.Equal(Sha("new"), p.Write(K("wo.md"), B("new"), BundleWriteCondition.None).Token);
            Assert.Equal(["wo.md"], Tree(tmp.Path));
        }
        finally
        {
            SetReadable(file, true);
        }

        Assert.Equal("new", File.ReadAllText(file));
    }

    /// <summary>Denies (or gives back) the current user the right to read <paramref name="file"/>: mode 0200 or 0600 on Unix, an explicit ReadData deny ACE on Windows.</summary>
    private static void SetReadable(string file, bool readable)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(file);
            var security = info.GetAccessControl();
            var deny = new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.ReadData, System.Security.AccessControl.AccessControlType.Deny);
            if (readable)
            {
                security.RemoveAccessRule(deny);
            }
            else
            {
                security.AddAccessRule(deny);
            }

            info.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(file, readable ? UnixFileMode.UserRead | UnixFileMode.UserWrite : UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// The temporary that holds a replacement's bytes is created owner-only (0600) before any
    /// byte is written, so a 0600 object's next content is never readable through a looser
    /// mode; the committed object keeps the mode the target had. A new object's temporary gets
    /// the mode a new file gets in that directory (umask, default ACL), which is exactly what the
    /// committed object has and what <c>File.WriteAllText</c> gave it before.
    /// </summary>
    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void On_Unix_a_replacement_is_prepared_owner_only_and_keeps_the_target_mode()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX modes");
        using var tmp = new TempDir();
        var secret = tmp.Write("secret.md", "old");
        File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var shared = tmp.Write("shared.md", "old");
        File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var reference = tmp.Write("reference.txt", "a new file's mode here");
        var p = new FileSystemBundleProvider(tmp.Path);
        var seen = new List<UnixFileMode>();
        p.TemporaryCreatedForTest = temporary => seen.Add(File.GetUnixFileMode(temporary));

        p.Write(K("secret.md"), B("new"), BundleWriteCondition.None);
        p.Write(K("shared.md"), B("new"), BundleWriteCondition.None);
        p.Write(K("fresh.md"), B("new"), BundleWriteCondition.MustNotExist);

        var ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal([ownerOnly, ownerOnly, File.GetUnixFileMode(reference)], seen);
        Assert.Equal(ownerOnly, File.GetUnixFileMode(secret));
        Assert.Equal(ownerOnly | UnixFileMode.GroupRead, File.GetUnixFileMode(shared));
        Assert.Equal(File.GetUnixFileMode(reference), File.GetUnixFileMode(Path.Combine(tmp.Path, "fresh.md")));
    }

    [Fact]
    public void The_temporary_lives_beside_the_target_under_a_reserved_name_while_it_is_written()
    {
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        string? temporary = null;
        p.TemporaryCreatedForTest = t => temporary = t;
        p.Write(K("d/x.md"), B("new"), BundleWriteCondition.None);
        Assert.NotNull(temporary);
        Assert.Equal(Path.Combine(tmp.Path, "d"), Path.GetDirectoryName(temporary));
        Assert.Matches(@"^\.okf-write-[0-9a-f]{32}\.tmp$", Path.GetFileName(temporary));
        Assert.True(FileSystemBundleProvider.IsTemporaryName(Path.GetFileName(temporary!)));
        Assert.False(File.Exists(temporary));
    }

    /// <summary>
    /// A failure while the temporary's bytes are made durable (a full disk, a quota, an I/O error)
    /// removes the temporary and leaves the target byte-identical, whether the write creates or
    /// replaces. The hook stands for the error; a real full disk is exercised by hand (the
    /// unbuffered stream is what lets the cleanup run: see <c>WriteTemporary</c>).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failure_making_the_temporary_durable_propagates_and_leaves_no_temporary(bool replace)
    {
        using var tmp = new TempDir();
        tmp.Write("keep.md", "kept");
        var file = tmp.Write("d/x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var before = Tree(tmp.Path);
        var created = 0;
        p.TemporaryCreatedForTest = _ => created++;
        p.TemporaryFlushForTest = _ => throw new IOException("no space left on device");
        var key = replace ? "d/x.md" : "d/new.md";

        Assert.Throws<IOException>(() => p.Write(K(key), B("new small content"), BundleWriteCondition.None));

        Assert.Equal(1, created);
        Assert.Equal(before, Tree(tmp.Path));
        Assert.Equal("old", File.ReadAllText(file));
        Assert.Equal("kept", File.ReadAllText(Path.Combine(tmp.Path, "keep.md")));
        Assert.Empty(p.ListOrphanedTemporaries(BundleLimits.Default));
    }

    [Fact]
    public void The_provider_can_write_and_its_read_only_view_still_refuses_without_naming_the_root()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        Assert.True(p.Capabilities.CanWrite);
        var ro = p.AsReadOnly();
        var ex = Assert.Throws<BundleReadOnlyException>(() => ro.Write(K("a.md"), B("x"), BundleWriteCondition.None));
        Assert.DoesNotContain(tmp.Path, ex.Message);
        Assert.Equal(p.Id, ex.ProviderId);
        Assert.Throws<BundleReadOnlyException>(() => ro.Delete(K("a.md"), BundleWriteCondition.None));
        Assert.Empty(Tree(tmp.Path));
    }

    [Fact]
    public void A_delete_removes_the_object_and_leaves_its_empty_directories()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        p.Write(K("a/b/c.md"), B("c"), BundleWriteCondition.None);
        p.Delete(K("a/b/c.md"), BundleWriteCondition.None);
        Assert.Equal(["a", "a/b"], Tree(tmp.Path));
        Assert.Empty(p.Enumerate(null, BundleLimits.Default));
        Assert.Empty(Bundle.Load(p).Concepts);
    }

    [Fact]
    public void A_delete_never_touches_a_directory_under_the_key()
    {
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "x");
        var p = new FileSystemBundleProvider(tmp.Path);
        p.Delete(K("d"), BundleWriteCondition.None);
        Assert.Throws<BundleWriteConflictException>(() => p.Delete(K("d"), BundleWriteCondition.IfMatch(new BundleObjectVersion(Sha("x")))));
        Assert.Equal(["d", "d/x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// Writers creating objects in one directory that does not exist yet, at the same moment:
    /// each lists the parent, finds no entry, and may then find the directory another writer just
    /// created. That is a name created meanwhile, not a name the volume folds, so every write
    /// succeeds. Two hundred rounds of four writers, because the window is a few microseconds.
    /// </summary>
    [Fact]
    public void Writers_creating_one_new_directory_at_the_same_moment_all_succeed()
    {
        using var tmp = new TempDir();
        var providers = Enumerable.Range(0, 4).Select(_ => new FileSystemBundleProvider(tmp.Path)).ToArray();
        for (var round = 0; round < 200; round++)
        {
            using var start = new Barrier(providers.Length);
            var failures = new Exception?[providers.Length];
            var threads = providers.Select((p, i) => new Thread(() =>
            {
                start.SignalAndWait();
                try
                {
                    p.Write(K($"r{round}/w{i}.md"), B("x"), BundleWriteCondition.MustNotExist);
                }
                catch (Exception e)
                {
                    failures[i] = e;
                }
            })).ToArray();
            foreach (var t in threads)
            {
                t.Start();
            }

            foreach (var t in threads)
            {
                t.Join();
            }

            Assert.All(failures, f => Assert.Null(f));
        }
    }
}
