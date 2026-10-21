// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// The filesystem commit under faults (issue #157): what a failure before, during and after
/// the commit leaves behind, the guards re-run at the moment of the commit, and readers that
/// must not block a replacement (B8). Faults are injected through the provider's internal test
/// hooks, which are <see langword="null"/> outside tests; Windows' <c>ReplaceFile</c> error 1176
/// cannot be provoked on demand, so its classification is driven by a commit hook that leaves
/// the disk exactly as that error does.
/// </summary>
public class FileSystemBundleProviderCommitTests
{
    private const int ErrorUnableToMoveReplacement = unchecked((int)0x80070498); // HRESULT of Win32 1176

    private const int ErrorUnableToMoveReplacement2 = unchecked((int)0x80070499); // HRESULT of Win32 1177

    private const int ErrorUnableToRemoveReplaced = unchecked((int)0x80070497); // HRESULT of Win32 1175

    private const int ErrorSharingViolation = unchecked((int)0x80070020); // HRESULT of Win32 32

    private const int ErrorLockViolation = unchecked((int)0x80070021); // HRESULT of Win32 33

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    private static string[] Tree(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    private static string ReadVia(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    [Fact]
    public void A_failure_between_the_temporary_and_the_commit_leaves_the_target_byte_identical_and_no_temporary()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("d/x.md", "old");
        var before = File.ReadAllBytes(file);
        var p = new FileSystemBundleProvider(tmp.Path);
        var injected = new IOException("injected after the temporary was written");
        p.AfterTemporaryWrittenForTest = _ => throw injected;
        Assert.Same(injected, Assert.Throws<IOException>(() => p.Write(K("d/x.md"), B("new"), BundleWriteCondition.None)));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(["d", "d/x.md"], Tree(tmp.Path));
    }

    [Fact]
    public void Readers_see_the_whole_old_object_until_the_commit_and_the_whole_new_one_after()
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var reader = new FileSystemBundleProvider(tmp.Path);
        var seen = new List<string>();
        p.TemporaryCreatedForTest = _ => seen.Add(ReadVia(reader, "x.md"));
        p.AfterTemporaryWrittenForTest = t =>
        {
            seen.Add(ReadVia(reader, "x.md"));
            Assert.Equal("new", File.ReadAllText(t)); // the whole new content, under the temporary's name only
        };
        p.BeforeCommitForTest = (_, _) => seen.Add(ReadVia(reader, "x.md"));
        p.Write(K("x.md"), B("new"), BundleWriteCondition.None);
        seen.Add(ReadVia(reader, "x.md"));
        Assert.Equal(["old", "old", "old", "new"], seen);
    }

    /// <summary>The per-object lock covers the check and the commit (issue #157): a second writer of the same object, through another instance, waits for the first commit, then finds the version it expected gone.</summary>
    [Fact]
    public void The_commit_lock_covers_the_check_and_the_commit_across_instances()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        var v0 = p.Write(K("x.md"), B("v0"), BundleWriteCondition.None);
        var other = new FileSystemBundleProvider(tmp.Path);
        Task? second = null;
        p.BeforeCommitForTest = (_, _) =>
        {
            second = Task.Run(() => other.Write(K("x.md"), B("second"), BundleWriteCondition.IfMatch(v0)));
            Assert.False(second.Wait(TimeSpan.FromMilliseconds(200)), "the second writer passed the lock the first one holds");
        };
        p.Write(K("x.md"), B("first"), BundleWriteCondition.IfMatch(v0));
        var thrown = Assert.Throws<AggregateException>(() => second!.Wait());
        Assert.IsType<BundleWriteConflictException>(thrown.InnerException);
        Assert.Equal("first", File.ReadAllText(Path.Combine(tmp.Path, "x.md")));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    [Fact]
    public void A_commit_that_fails_without_effect_is_the_raw_failure_and_leaves_nothing_behind()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var failure = new IOException("a device attached to the system is not functioning (simulated)", unchecked((int)0x8007001F));
        p.CommitForTest = (_, _, _) => throw failure;
        Assert.Same(failure, Assert.Throws<IOException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.None)));
        Assert.Equal("old", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// <c>ReplaceFile</c> can refuse a replacement while readers hold the object open, with error
    /// 1175 (<c>ERROR_UNABLE_TO_REMOVE_REPLACED</c>: both files keep their names) — measured
    /// twice in five runs of probe A3 by the external review — or with a sharing violation. Both
    /// change nothing, so the commit is made again, from the re-check.
    /// </summary>
    [Theory]
    [InlineData(ErrorUnableToRemoveReplaced)]
    [InlineData(ErrorSharingViolation)]
    public void A_replacement_the_storage_refused_without_effect_once_is_made_again_and_succeeds(int hresult)
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var calls = 0;
        p.CommitForTest = (temporary, target, replace) =>
        {
            Assert.True(replace);
            if (++calls == 1)
            {
                throw new IOException("simulated", hresult);
            }

            File.Replace(temporary, target, null);
        };
        var v = p.Write(K("x.md"), B("new"), BundleWriteCondition.None);
        Assert.Equal(2, calls);
        using var obj = p.Read(K("x.md"));
        Assert.Equal(v, obj.Version);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    [Theory]
    [InlineData(ErrorUnableToRemoveReplaced)]
    [InlineData(ErrorSharingViolation)]
    public void A_replacement_refused_on_every_attempt_is_not_committed_the_temporary_removed_and_the_target_untouched(int hresult)
    {
        using var tmp = new TempDir();
        var file = tmp.Write("d/x.md", "old");
        var before = File.ReadAllBytes(file);
        var p = new FileSystemBundleProvider(tmp.Path);
        var calls = 0;
        var failure = new IOException("simulated", hresult);
        p.CommitForTest = (_, _, _) =>
        {
            calls++;
            throw failure;
        };
        var ex = Assert.Throws<BundleWriteNotCommittedException>(() => p.Write(K("d/x.md"), B("new"), BundleWriteCondition.None));
        Assert.Equal(3, calls);
        Assert.Same(failure, ex.InnerException);
        Assert.Equal(K("d/x.md"), ex.Key);
        Assert.Equal("the write of 'd/x.md' was not committed: the storage kept refusing it (IOException); the object is unchanged", ex.Message);
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(["d", "d/x.md"], Tree(tmp.Path));
    }

    /// <summary>A retry starts from the re-check of the condition against the bytes stored now: another writer landing between two attempts is a conflict, never overwritten.</summary>
    [Fact]
    public void A_condition_another_writer_breaks_between_two_commit_attempts_is_a_conflict_not_an_overwrite()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        using var current = p.Read(K("x.md"));
        var calls = 0;
        p.CommitForTest = (_, target, _) =>
        {
            calls++;
            File.WriteAllText(target, "other writer");
            throw new IOException("simulated", ErrorUnableToRemoveReplaced);
        };
        var ex = Assert.Throws<BundleWriteConflictException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.IfMatch(current.Version)));
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, ex.Reason);
        Assert.Equal(1, calls);
        Assert.Equal("other writer", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    [Fact]
    public void A_commit_that_reports_a_failure_after_taking_effect_is_a_success()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        p.CommitForTest = (temporary, target, replace) =>
        {
            Assert.True(replace);
            File.Replace(temporary, target, null);
            throw new IOException("simulated metadata error after the rename");
        };
        var v = p.Write(K("x.md"), B("new"), BundleWriteCondition.None);
        using var obj = p.Read(K("x.md"));
        Assert.Equal(v, obj.Version);
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// <c>ReplaceFile</c> without a backup name can fail with error 1176 after removing the
    /// target and before moving the replacement in: no object under the key, the new bytes under
    /// the temporary's name. The outcome is unknown to the caller; the temporary — the only copy
    /// of the new content — is kept, and is what <see cref="FileSystemBundleProvider.ListOrphanedTemporaries"/> reports.
    /// Error 1177 (<c>ERROR_UNABLE_TO_MOVE_REPLACEMENT_2</c>) is classified the same way.
    /// </summary>
    [Theory]
    [InlineData(ErrorUnableToMoveReplacement)]
    [InlineData(ErrorUnableToMoveReplacement2)]
    public void A_commit_failing_like_ReplaceFile_1176_or_1177_is_an_unknown_outcome_that_keeps_the_temporary(int hresult)
    {
        using var tmp = new TempDir();
        var file = tmp.Write("d/x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var failure = new IOException("Unable to move the replacement file to the file to be replaced (simulated)", hresult);
        p.CommitForTest = (_, target, _) =>
        {
            File.Delete(target);
            throw failure;
        };
        var ex = Assert.Throws<BundleWriteOutcomeUnknownException>(() => p.Write(K("d/x.md"), B("new"), BundleWriteCondition.None));
        Assert.Same(failure, ex.InnerException);
        Assert.Equal(K("d/x.md"), ex.Key);
        Assert.DoesNotContain(tmp.Path, ex.Message);
        Assert.False(File.Exists(file));
        var orphan = Assert.Single(p.ListOrphanedTemporaries(BundleLimits.Default));
        Assert.StartsWith("d/.okf-write-", orphan, StringComparison.Ordinal);
        Assert.Equal("new", File.ReadAllText(Path.Combine(tmp.Path, orphan)));
    }

    [Fact]
    public void A_commit_failure_whose_effect_cannot_be_read_back_is_an_unknown_outcome()
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        p.CommitForTest = (temporary, target, _) =>
        {
            File.Delete(temporary);
            File.WriteAllText(target, "someone else");
            throw new IOException("simulated");
        };
        Assert.Throws<BundleWriteOutcomeUnknownException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.None));
        Assert.Equal("someone else", File.ReadAllText(Path.Combine(tmp.Path, "x.md")));
    }

    [Fact]
    public void Another_writer_landing_while_the_commit_fails_is_a_conflict_and_the_temporary_is_removed()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        using var current = p.Read(K("x.md"));
        p.CommitForTest = (_, target, _) =>
        {
            File.WriteAllText(target, "other writer");
            throw new IOException("simulated");
        };
        var ex = Assert.Throws<BundleWriteConflictException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.IfMatch(current.Version)));
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, ex.Reason);
        Assert.Equal("other writer", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// An unconditional replacement never read its target (it needs no right to read it, F5), so
    /// a commit that failed with its temporary still in place is that write's raw failure,
    /// whatever the target holds now: nothing of the write happened, and the temporary is removed.
    /// </summary>
    [Fact]
    public void An_unconditional_replacement_whose_commit_fails_is_the_raw_failure_whatever_the_target_holds()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var failure = new IOException("simulated");
        p.CommitForTest = (_, target, _) =>
        {
            File.WriteAllText(target, "other writer");
            throw failure;
        };
        Assert.Same(failure, Assert.Throws<IOException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.None)));
        Assert.Equal("other writer", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    [Fact]
    public void An_object_that_appears_before_a_conditional_creation_commits_is_a_conflict()
    {
        using var tmp = new TempDir();
        var p = new FileSystemBundleProvider(tmp.Path);
        p.AfterTemporaryWrittenForTest = _ => File.WriteAllText(Path.Combine(tmp.Path, "x.md"), "created elsewhere");
        Assert.Throws<BundleWriteConflictException>(() => p.Write(K("x.md"), B("mine"), BundleWriteCondition.MustNotExist));
        Assert.Equal("created elsewhere", File.ReadAllText(Path.Combine(tmp.Path, "x.md")));
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// An unconditional creation that finds the object created meanwhile replaces it; on Unix it
    /// first prepares again, so the replacement takes the mode of the object it replaces (here
    /// owner-only) rather than the mode a new file gets.
    /// </summary>
    [Fact]
    public void An_object_that_appears_before_an_unconditional_creation_commits_is_replaced_keeping_its_mode()
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "x.md");
        var p = new FileSystemBundleProvider(tmp.Path);
        var preparations = 0;
        p.AfterTemporaryWrittenForTest = _ =>
        {
            if (++preparations == 1)
            {
                File.WriteAllText(file, "created elsewhere");
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        };
        p.Write(K("x.md"), B("mine"), BundleWriteCondition.None);
        Assert.Equal("mine", File.ReadAllText(file));
        Assert.Equal(["x.md"], Tree(tmp.Path));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(2, preparations);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        else
        {
            Assert.Equal(1, preparations);
        }
    }

    /// <summary>
    /// Another process swaps the target's directory, or one of its ancestors, for a junction
    /// to an outside directory after the temporary is written: the STRICT re-check at the commit
    /// refuses, nothing reaches the junction's target, and the temporary — now reachable only
    /// through the junction — is left where it is rather than removed through the link.
    /// </summary>
    [SkippableTheory]
    [InlineData("a/b")]
    [InlineData("a")]
    public void A_directory_swapped_for_a_junction_before_the_commit_is_refused_and_nothing_reaches_its_target(string swapped)
    {
        using var external = new TempDir();
        using var tmp = new TempDir();
        tmp.Write("a/b/x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var created = false;
        p.AfterTemporaryWrittenForTest = _ =>
        {
            var dir = Path.Combine(tmp.Path, swapped.Replace('/', Path.DirectorySeparatorChar));
            Directory.Move(dir, dir + "-moved");
            created = tmp.TryCreateJunctionToExternalDir(swapped.Replace('/', Path.DirectorySeparatorChar), external.Path);
        };
        var thrown = Record.Exception(() => p.Write(K("a/b/x.md"), B("new"), BundleWriteCondition.None));
        Skip.IfNot(created, "junctions unavailable");
        Assert.IsType<BundleAccessRefusedException>(thrown);
        Assert.Empty(Tree(external.Path));
        var moved = swapped == "a" ? Path.Combine(tmp.Path, "a-moved", "b", "x.md") : Path.Combine(tmp.Path, "a", "b-moved", "x.md");
        Assert.Equal("old", File.ReadAllText(moved));
    }

    /// <summary>
    /// The classification of a failed commit renews the boundary and the STRICT guards before it
    /// reads anything back (external review, F4): an ancestor swapped for a link during the
    /// commit, behind which the new bytes happen to sit, is not read as a success that a public
    /// <see cref="FileSystemBundleProvider.Read"/> would then refuse. The outcome is unknown and
    /// the temporary, now reachable only through the moved directory, is kept.
    /// </summary>
    [SkippableFact]
    public void A_failed_commit_is_not_read_back_through_an_ancestor_swapped_for_a_link()
    {
        using var external = new TempDir();
        using var tmp = new TempDir();
        tmp.Write("d/x.md", "old");
        external.Write("x.md", "outside");
        var p = new FileSystemBundleProvider(tmp.Path, confinementBoundary: tmp.Path);
        var linked = false;
        p.CommitForTest = (_, _, _) =>
        {
            Directory.Move(Path.Combine(tmp.Path, "d"), Path.Combine(tmp.Path, "saved"));
            linked = tmp.TryCreateJunctionToExternalDir("d", external.Path);
            File.WriteAllText(Path.Combine(external.Path, "x.md"), "new");
            throw new IOException("simulated commit failure", unchecked((int)0x80070005));
        };
        var thrown = Record.Exception(() => p.Write(K("d/x.md"), B("new"), BundleWriteCondition.None));
        Skip.IfNot(linked, "junctions unavailable");
        Assert.IsType<BundleWriteOutcomeUnknownException>(thrown);
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("d/x.md")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(tmp.Path, "saved", "x.md")));
        Assert.Single(Directory.GetFiles(Path.Combine(tmp.Path, "saved"), FileSystemBundleProvider.TemporaryPrefix + "*"));
    }

    /// <summary>A link put in place of the temporary after it was written (a junction on Windows, which needs no privilege; a symlink elsewhere) is refused at the commit and is not followed.</summary>
    [SkippableFact]
    public void A_link_put_in_place_of_the_temporary_is_refused_at_the_commit()
    {
        using var external = new TempDir();
        var victim = external.Write("victim.md", "outside");
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        var linked = false;
        p.AfterTemporaryWrittenForTest = temporary =>
        {
            File.Delete(temporary);
            var name = Path.GetFileName(temporary);
            linked = OperatingSystem.IsWindows()
                ? tmp.TryCreateJunctionToExternalDir(name, external.Path)
                : tmp.TryCreateFileSymlinkToExternalFile(name, victim);
        };
        var thrown = Record.Exception(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.None));
        Skip.IfNot(linked, "links unavailable");
        Assert.IsType<BundleAccessRefusedException>(thrown);
        Assert.Equal("old", File.ReadAllText(file));
        Assert.Equal("outside", File.ReadAllText(victim));
    }

    /// <summary>The temporary is created with <see cref="FileMode.CreateNew"/>: an entry planted under its name beforehand — a link to a victim — is never opened, never written through and never removed.</summary>
    [SkippableFact]
    public void A_link_planted_under_the_temporarys_name_is_never_opened()
    {
        using var external = new TempDir();
        var victim = external.Write("victim.md", "outside");
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        var name = FileSystemBundleProvider.TemporaryPrefix + new string('a', 32) + FileSystemBundleProvider.TemporarySuffix;
        var planted = OperatingSystem.IsWindows()
            ? tmp.TryCreateJunctionToExternalDir(name, external.Path)
            : tmp.TryCreateFileSymlinkToExternalFile(name, victim);
        Skip.IfNot(planted, "links unavailable");
        var p = new FileSystemBundleProvider(tmp.Path) { TemporaryNameForTest = () => name };
        var thrown = Record.Exception(() => p.Write(K("x.md"), B("pwned"), BundleWriteCondition.None));
        Assert.True(thrown is IOException or UnauthorizedAccessException, $"unexpected {thrown?.GetType().Name}");
        Assert.Equal("outside", File.ReadAllText(victim));
        Assert.Equal(["victim.md"], Tree(external.Path));
        Assert.Equal("old", File.ReadAllText(file));
        Assert.True(Path.Exists(Path.Combine(tmp.Path, name)));
    }

    /// <summary>
    /// B8: a reader that opened the object the way <see cref="FileSystemBundleProvider.Read"/>
    /// does (<see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>) never blocks a
    /// replacement or a delete, and keeps reading the bytes it opened. A reader that denies
    /// deletion (<see cref="FileShare.Read"/>) blocks both on Windows — the failure is the raw
    /// sharing violation, and nothing changed — and blocks nothing elsewhere.
    /// </summary>
    [Theory]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    [InlineData(FileShare.Read)]
    public void A_reader_holding_the_object_open_blocks_a_replacement_only_on_Windows_and_only_when_it_denies_deletion(FileShare readerShare)
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "old");
        tmp.Write("y.md", "old");
        var p = new FileSystemBundleProvider(tmp.Path);
        using (var reader = new FileStream(file, FileMode.Open, FileAccess.Read, readerShare))
        using (var other = new FileStream(Path.Combine(tmp.Path, "y.md"), FileMode.Open, FileAccess.Read, readerShare))
        {
            var blocks = OperatingSystem.IsWindows() && !readerShare.HasFlag(FileShare.Delete);
            if (blocks)
            {
                Assert.Equal(unchecked((int)0x80070020), Assert.Throws<IOException>(() => p.Write(K("x.md"), B("new"), BundleWriteCondition.None)).HResult);
                Assert.Equal(unchecked((int)0x80070020), Assert.Throws<IOException>(() => p.Delete(K("y.md"), BundleWriteCondition.None)).HResult);
                Assert.Equal("old", File.ReadAllText(file));
                Assert.Equal(["x.md", "y.md"], Tree(tmp.Path));
            }
            else
            {
                p.Write(K("x.md"), B("new"), BundleWriteCondition.None);
                p.Delete(K("y.md"), BundleWriteCondition.None);
                Assert.Equal("new", File.ReadAllText(file));
                Assert.Equal(["x.md"], Tree(tmp.Path));
            }

            var buffer = new byte[3];
            reader.ReadExactly(buffer);
            Assert.Equal("old", Encoding.UTF8.GetString(buffer));
        }
    }

    /// <summary>
    /// B8 through the provider itself: readers looping over <see cref="FileSystemBundleProvider.Read"/>
    /// while a writer replaces the object a hundred times never keep one of those replacements from
    /// landing, and never see anything but a whole old or a whole new object. Before <c>Read</c> shared deletion, a replacement
    /// on Windows failed with a sharing violation whenever it met an open read; a replacement
    /// <c>ReplaceFile</c> refuses without effect (1175) is made again. On Windows a reader can
    /// still, for a moment, find no object under the name or meet a sharing violation while
    /// <c>ReplaceFile</c> runs — after its retries, far less often (B17, probe A3); on Unix a
    /// rename leaves no such moment. Never a torn object, anywhere.
    /// </summary>
    [Fact]
    public void Concurrent_reads_never_block_a_replacement_and_never_see_a_torn_object()
    {
        using var tmp = new TempDir();
        var a = new string('a', 256 * 1024);
        var b = new string('b', 256 * 1024);
        tmp.Write("x.md", a);
        var p = new FileSystemBundleProvider(tmp.Path);
        var stop = false;
        var readerErrors = new List<string>();
        var readers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
        {
            var reader = new FileSystemBundleProvider(tmp.Path);
            while (!Volatile.Read(ref stop))
            {
                string? problem;
                try
                {
                    var text = ReadVia(reader, "x.md");
                    problem = text == a || text == b ? null : $"torn read of {text.Length} chars";
                }
                catch (BundleObjectNotFoundException) when (OperatingSystem.IsWindows())
                {
                    problem = null; // the ReplaceFile window (B17)
                }
                catch (IOException e) when (OperatingSystem.IsWindows() && e.HResult == unchecked((int)0x80070020))
                {
                    problem = null; // the ReplaceFile window (B17)
                }
                catch (Exception e)
                {
                    problem = $"{e.GetType().Name}: {e.Message}";
                }

                if (problem is not null)
                {
                    lock (readerErrors)
                    {
                        readerErrors.Add(problem);
                    }
                }
            }
        })).ToArray();
        foreach (var t in readers)
        {
            t.Start();
        }

        try
        {
            for (var i = 0; i < 100; i++)
            {
                p.Write(K("x.md"), B(i % 2 == 0 ? b : a), BundleWriteCondition.None);
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            foreach (var t in readers)
            {
                t.Join();
            }
        }

        Assert.Empty(readerErrors);
        Assert.Equal(["x.md"], Tree(tmp.Path));
    }

    /// <summary>
    /// R1: on Windows a read retries the <c>ReplaceFile</c> window — a sharing or lock violation,
    /// or an entry the exact listing had just matched that the open no longer finds — and succeeds
    /// once it has passed. Elsewhere the read fails at once, as before.
    /// </summary>
    [Theory]
    [InlineData("sharing")]
    [InlineData("lock")]
    [InlineData("not found")]
    public void A_read_meeting_the_replace_window_once_is_retried_on_Windows_and_fails_as_before_elsewhere(string window)
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "content");
        var p = new FileSystemBundleProvider(tmp.Path);
        var opens = 0;
        p.ReadOpeningForTest = path =>
        {
            if (++opens == 1)
            {
                throw Window(window, path);
            }
        };
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("content", ReadVia(p, "x.md"));
            Assert.Equal(2, opens);
        }
        else
        {
            var thrown = Record.Exception(() => ReadVia(p, "x.md"));
            Assert.IsType(window == "not found" ? typeof(BundleObjectNotFoundException) : typeof(IOException), thrown);
            Assert.Equal(1, opens);
        }
    }

    [Theory]
    [InlineData("sharing")]
    [InlineData("not found")]
    public void A_read_meeting_the_replace_window_on_every_attempt_gives_up_after_three_on_Windows(string window)
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", "content");
        var p = new FileSystemBundleProvider(tmp.Path);
        var opens = 0;
        p.ReadOpeningForTest = path =>
        {
            opens++;
            throw Window(window, path);
        };
        var thrown = Record.Exception(() => ReadVia(p, "x.md"));
        Assert.IsType(window == "not found" ? typeof(BundleObjectNotFoundException) : typeof(IOException), thrown);
        Assert.Equal(OperatingSystem.IsWindows() ? 3 : 1, opens);
    }

    /// <summary>An object really deleted is not found as soon as the listing no longer matches it: the retry lists again and never opens it again.</summary>
    [Fact]
    public void An_object_deleted_while_it_is_read_is_not_found_without_another_open()
    {
        using var tmp = new TempDir();
        var file = tmp.Write("x.md", "content");
        var p = new FileSystemBundleProvider(tmp.Path);
        var opens = 0;
        p.ReadOpeningForTest = _ =>
        {
            opens++;
            File.Delete(file);
        };
        Assert.Throws<BundleObjectNotFoundException>(() => ReadVia(p, "x.md"));
        Assert.Equal(1, opens);
    }

    /// <summary>
    /// What the retry never covers, counted rather than timed: a key the listing does not match
    /// (validators probe many absent resources), a refusal and a version mismatch each cost one
    /// attempt — one listing of the root, at most one open.
    /// </summary>
    [SkippableFact]
    public void An_absent_key_a_refusal_and_a_version_mismatch_are_never_retried()
    {
        using var external = new TempDir();
        external.Write("x.md", "outside");
        using var tmp = new TempDir();
        tmp.Write("x.md", "content");
        var p = new FileSystemBundleProvider(tmp.Path);
        var opens = 0;
        p.ReadOpeningForTest = _ => opens++;

        var listings = p.DirectoryListings;
        Assert.Throws<BundleObjectNotFoundException>(() => p.Read(K("absent.md")));
        Assert.Equal(1, p.DirectoryListings - listings);
        Assert.Equal(0, opens);

        Assert.Throws<BundleVersionMismatchException>(() => p.Read(K("x.md"), new BundleObjectVersion(new string('0', 64))));
        Assert.Equal(1, opens);

        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("j", external.Path), "links unavailable");
        listings = p.DirectoryListings;
        Assert.Throws<BundleAccessRefusedException>(() => p.Read(K("j/x.md")));
        Assert.Equal(1, p.DirectoryListings - listings);
        Assert.Equal(1, opens);
    }

    private static IOException Window(string window, string path) => window switch
    {
        "sharing" => new IOException("simulated sharing violation", ErrorSharingViolation),
        "lock" => new IOException("simulated lock violation", ErrorLockViolation),
        _ => new FileNotFoundException("simulated: the entry is gone for a moment", path),
    };
}
