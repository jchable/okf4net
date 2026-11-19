// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net.Tests;

/// <summary>
/// #86: <see cref="BundleConceptWriter"/>'s write lock is keyed on the bundle
/// root RESOLVED through reparse points each time a thread takes it for an
/// operation, so a junction or symlink to a bundle shares the bundle's lock,
/// while a nested acquisition on the same thread reuses the object it holds.
/// Every link test creates real junctions (no privilege needed on Windows) or
/// symlinks, and skips only where neither can be created.
/// </summary>
public class BundleConceptWriterLockTests
{
    private const string NoLinkPrivilege = "no junction/symlink privilege on this host";
    private const string Fm = "type: Note\ntitle: Shared\ndescription: A concept two writers append to.";

    [SkippableFact]
    public void A_junction_to_the_bundle_shares_the_bundles_lock()
    {
        using var tmp = new TempDir();
        using var actual = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("alias", actual.Path), NoLinkPrivilege);

        var overActual = new BundleConceptWriter(actual.Path);
        var overAlias = new BundleConceptWriter(Path.Combine(tmp.Path, "alias"));

        Assert.Same(overActual.CurrentLockObjectForTest(), overAlias.CurrentLockObjectForTest());
    }

    [SkippableFact]
    public void A_root_below_a_linked_parent_shares_the_lock_of_its_real_path()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        Directory.CreateDirectory(Path.Combine(external.Path, "bundle"));
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("parent", external.Path), NoLinkPrivilege);

        var throughLink = new BundleConceptWriter(Path.Combine(tmp.Path, "parent", "bundle"));
        var real = new BundleConceptWriter(Path.Combine(external.Path, "bundle"));

        Assert.Same(real.CurrentLockObjectForTest(), throughLink.CurrentLockObjectForTest());
    }

    /// <summary>
    /// A fixture that needs TWO resolution passes. On Windows, one
    /// <see cref="ReparsePoints.TryResolveThroughReparsePoints"/> call already
    /// resolves a chain of links whose final target EXISTS (measured: .NET
    /// asks the OS for the final path through a handle), so the brief's
    /// <c>real/k/b</c>-exists layout converges in one pass there. With
    /// <c>real/k</c> missing, the handle cannot be opened and .NET follows the
    /// chain link by link instead: <c>j</c> resolves to <c>m/k</c>, and only a
    /// second pass finds that <c>m</c> is itself a link to <c>real</c>. The
    /// precondition below asserts that on whichever host runs it.
    /// </summary>
    [SkippableFact]
    public void A_link_found_only_after_the_first_resolution_still_converges()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "real"));
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("m", Path.Combine(tmp.Path, "real")), NoLinkPrivilege);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir(Path.Combine("a", "j"), Path.Combine(tmp.Path, "m", "k")), NoLinkPrivilege);

        var throughLinks = Path.Combine(tmp.Path, "a", "j", "b");
        var realPath = Path.Combine(tmp.Path, "real", "k", "b");
        // The temp directory's own ancestors may be links too (macOS: /var
        // -> /private/var), so the target is the real path's own key, not
        // its lexical spelling.
        var realKey = ReparsePoints.ResolveLockKey(realPath);

        // Precondition: one pass is NOT enough, or this test proves nothing
        // about the loop.
        Assert.True(ReparsePoints.TryResolveThroughReparsePoints(throughLinks, out var onePass));
        Assert.False(string.Equals(onePass, ReparsePoints.CanonicalizeRoot(realPath), StringComparison.OrdinalIgnoreCase), $"one pass already reached {onePass}");
        Assert.False(string.Equals(onePass, realKey, StringComparison.OrdinalIgnoreCase), $"one pass already reached {onePass}");

        var viaLinks = new BundleConceptWriter(throughLinks);
        var viaReal = new BundleConceptWriter(realPath);

        Assert.Equal(realKey, ReparsePoints.ResolveLockKey(throughLinks));
        Assert.Same(viaReal.CurrentLockObjectForTest(), viaLinks.CurrentLockObjectForTest());
    }

    /// <summary>
    /// Convergence is ORDINAL, not case-insensitive: a pass that only changes
    /// case can still leave a link to follow. <c>G</c> is a case-sensitive
    /// directory reached through the junction <c>D</c>; inside it, <c>a</c> is
    /// a junction to <c>D/A</c>, which does not exist. Resolving <c>D/a/b</c>
    /// gives <c>D/A/b</c> (equal ignoring case), and only a further pass
    /// follows <c>D</c> to reach <c>G/A/b</c>. Needs a per-directory
    /// case-sensitivity flag, which <c>fsutil</c> sets without elevation.
    /// </summary>
    [SkippableFact]
    public void A_pass_that_only_changes_case_is_not_convergence()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "needs Windows (fsutil per-directory case sensitivity)");
        using var tmp = new TempDir();
        using var g = new TempDir();
        Skip.IfNot(Run("fsutil.exe", "file", "setCaseSensitiveInfo", g.Path, "enable"), "cannot mark a directory case-sensitive on this host");
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("D", g.Path), NoLinkPrivilege);
        Skip.IfNot(g.TryCreateJunctionToExternalDir("a", Path.Combine(tmp.Path, "D", "A")), NoLinkPrivilege);

        var throughLinks = Path.Combine(tmp.Path, "D", "a", "b");
        Assert.True(ReparsePoints.TryResolveThroughReparsePoints(throughLinks, out var onePass));
        Assert.Equal(Path.Combine(tmp.Path, "D", "A", "b"), onePass);

        var viaLinks = new BundleConceptWriter(throughLinks);
        var viaReal = new BundleConceptWriter(Path.Combine(g.Path, "A", "b"));

        Assert.Same(viaReal.CurrentLockObjectForTest(), viaLinks.CurrentLockObjectForTest());
    }

    [SkippableFact]
    public void Writers_built_across_a_topology_change_share_the_lock_at_write_time()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();

        // Built while `later` does not exist: its lexical root and its
        // resolved root are then the same path.
        var a = new BundleConceptWriter(Path.Combine(tmp.Path, "later", "bundle"));

        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("later", external.Path), NoLinkPrivilege);
        Directory.CreateDirectory(Path.Combine(external.Path, "bundle"));
        var b = new BundleConceptWriter(Path.Combine(external.Path, "bundle"));

        Assert.Same(b.CurrentLockObjectForTest(), a.CurrentLockObjectForTest());

        string? workerResult = null;
        var worker = new Thread(() =>
            workerResult = b.AppendToConceptAtomic("notes/shared", Fm, body => (body ?? string.Empty).Trim() + "[B]"))
        { IsBackground = true };

        bool settled;
        bool workerFinishedWhileHeld;
        using (a.EnterWriteLock())
        {
            worker.Start();
            // The timeout is a deadlock guard, not an ordering delay: the
            // worker either blocks on the bundle monitor or runs to the end.
            settled = SpinWait.SpinUntil(
                () => (worker.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) != 0,
                TimeSpan.FromSeconds(10));
            workerFinishedWhileHeld = (worker.ThreadState & ThreadState.Stopped) != 0;
        }

        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "The worker did not finish.");
        Assert.True(settled, "The worker neither waited nor completed.");
        Assert.False(workerFinishedWhileHeld, "B wrote while A held the lock: the two writers do not share it.");
        Assert.DoesNotContain("Error", workerResult!, StringComparison.Ordinal);

        // B ran once A released the lock it shared with it (a lease is not re-entrant: A cannot
        // write under its own lease through the writer, so only the worker writes here).
        var doc = OkfDocument.Parse(File.ReadAllText(Path.Combine(external.Path, "bundle", "notes", "shared.md")));
        Assert.Equal("[B]", doc.Body.Trim());
    }

    [SkippableFact]
    public void Windows_namespace_spellings_share_one_lock()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the \\\\?\\ namespace is Windows-only");
        using var tmp = new TempDir();
        var plain = new BundleConceptWriter(tmp.Path);
        var device = new BundleConceptWriter(@"\\?\" + tmp.Path);

        Assert.Same(plain.CurrentLockObjectForTest(), device.CurrentLockObjectForTest());
    }

    /// <summary>
    /// The fallback keeps the namespace normalization: when resolution FAILS,
    /// <c>\\?\C:\x</c> and <c>C:\x</c> still share one lock, not one each. The
    /// failure is forced with a junction whose attributes are readable but
    /// whose target is not (a deny ReadAttributes ACE, see
    /// <see cref="TempDir.TryCreateUninspectableJunction"/>), so
    /// <see cref="ReparsePoints.TryResolveThroughReparsePoints"/> returns false;
    /// the precondition asserts that.
    /// </summary>
    [SkippableFact]
    public void Windows_namespace_spellings_share_one_lock_when_resolution_fails()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the \\\\?\\ namespace is Windows-only");
        using var tmp = new TempDir();
        using var external = new TempDir();
        using var junction = tmp.TryCreateUninspectableJunction("link", external.Path, denyParentListing: false);
        Skip.If(junction is null, "needs a junction plus deny ACEs");
        var plain = junction!.LinkPath;
        var device = @"\\?\" + plain;

        Assert.False(ReparsePoints.TryResolveThroughReparsePoints(plain, out _), "precondition: resolution must fail");
        Assert.Equal(ReparsePoints.ResolveLockKey(plain), ReparsePoints.ResolveLockKey(device));
        Assert.Same(
            new BundleConceptWriter(plain).CurrentLockObjectForTest(),
            new BundleConceptWriter(device).CurrentLockObjectForTest());
    }

    /// <summary>
    /// The UNC half of the namespace test, split out so the drive-letter half
    /// still runs on a host where the local administrative share is not
    /// reachable (it is opened through the server service, which can be off).
    /// </summary>
    [SkippableFact]
    public void Windows_UNC_namespace_spellings_share_one_lock()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "UNC paths are Windows-only");
        using var tmp = new TempDir();
        var drive = Path.GetPathRoot(tmp.Path)!;
        Skip.IfNot(drive.Length == 3 && drive[1] == ':', "the temp directory is not on a drive letter");
        var unc = @"\\localhost\" + drive[0] + "$" + tmp.Path[2..];
        Skip.IfNot(Directory.Exists(unc), $"{unc} is not reachable (administrative share unavailable)");

        var plainUnc = new BundleConceptWriter(unc);
        var deviceUnc = new BundleConceptWriter(@"\\?\UNC\" + unc[2..]);

        Assert.Same(plainUnc.CurrentLockObjectForTest(), deviceUnc.CurrentLockObjectForTest());
    }

    [Fact]
    public void Two_lexical_spellings_still_share_one_lock()
    {
        using var tmp = new TempDir();
        var bare = new BundleConceptWriter(tmp.Path);
        var trailing = new BundleConceptWriter(tmp.Path + Path.DirectorySeparatorChar);

        Assert.Same(bare.CurrentLockObjectForTest(), trailing.CurrentLockObjectForTest());
    }

    [Fact]
    public void Two_different_bundles_get_two_locks()
    {
        using var one = new TempDir();
        using var two = new TempDir();

        Assert.NotSame(new BundleConceptWriter(one.Path).CurrentLockObjectForTest(), new BundleConceptWriter(two.Path).CurrentLockObjectForTest());
    }

    [Fact]
    public void A_missing_root_constructs_writes_and_shares_the_lock_once_created()
    {
        using var tmp = new TempDir();
        var root = Path.Combine(tmp.Path, "not-yet", "bundle");
        var first = new BundleConceptWriter(root);

        Directory.CreateDirectory(root);
        var result = first.WriteConcept("notes/a", Fm, "body\n");
        Assert.DoesNotContain("Error", result, StringComparison.Ordinal);

        var second = new BundleConceptWriter(root);
        Assert.Same(first.CurrentLockObjectForTest(), second.CurrentLockObjectForTest());
    }


    [SkippableFact]
    public void Case_distinct_aliases_hold_independent_locks()
    {
        using var tmp = new TempDir();
        using var x = new TempDir();
        using var y = new TempDir();
        if (OperatingSystem.IsWindows())
        {
            Skip.IfNot(Run("fsutil.exe", "file", "setCaseSensitiveInfo", tmp.Path, "enable"),
                "cannot mark a directory case-sensitive on this host");
        }

        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("A", x.Path), NoLinkPrivilege);
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("a", y.Path),
            "needs case-distinct junctions/symlinks on this host");
        var upper = new BundleConceptWriter(Path.Combine(tmp.Path, "A"));
        var lower = new BundleConceptWriter(Path.Combine(tmp.Path, "a"));
        var yLock = new BundleConceptWriter(y.Path).CurrentLockObjectForTest();
        Assert.NotSame(upper.CurrentLockObjectForTest(), yLock);
        Assert.Same(lower.CurrentLockObjectForTest(), yLock);

        using var outer = upper.EnterWriteLock();
        // Independent physical bundles must remain independently acquirable.
        AssertAnotherThreadCanEnter(lower);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_topology_change_during_an_append_does_not_split_the_same_lexical_root(bool separateWriter)
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        var root = Path.Combine(tmp.Path, "later");
        var first = new BundleConceptWriter(root);
        var second = separateWriter ? new BundleConceptWriter(root) : first;
        string? secondResult = null;
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                secondResult = second.AppendToConceptAtomic("notes/shared", Fm,
                    body => (body ?? string.Empty).Trim() + "[B]");
            }
            catch (Exception e)
            {
                workerError = e;
            }
        })
        { IsBackground = true };

        var settled = false;
        var finishedWhileHeld = false;
        var firstResult = first.AppendToConceptAtomic("notes/shared", Fm, body =>
        {
            // The first read happened under the old key. A new acquisition
            // must wait even though the link now resolves to another key.
            Skip.IfNot(tmp.TryCreateJunctionToExternalDir("later", external.Path), NoLinkPrivilege);
            worker.Start();
            settled = SpinWait.SpinUntil(
                () => (worker.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) != 0,
                TimeSpan.FromSeconds(10));
            finishedWhileHeld = (worker.ThreadState & ThreadState.Stopped) != 0;
            return (body ?? string.Empty).Trim() + "[A]";
        });

        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "The second append did not finish.");
        Assert.Null(workerError);
        Assert.True(settled, "The worker neither waited nor completed.");
        Assert.False(finishedWhileHeld, "The second append completed while the first still held its lock.");
        Assert.DoesNotContain("Error", firstResult, StringComparison.Ordinal);
        Assert.DoesNotContain("Error", secondResult!, StringComparison.Ordinal);
        Assert.Equal("[A][B]", OkfDocument.Parse(File.ReadAllText(
            Path.Combine(external.Path, "notes", "shared.md"))).Body.Trim());
    }


    [SkippableTheory]
    [InlineData(@"\\?\UNC\server\share\bundle", @"\\server\share\bundle")]
    [InlineData(@"\\?\unc\server\share\bundle\", @"\\server\share\bundle")]
    [InlineData(@"\\?\C:\bundle", @"C:\bundle")]
    public void Windows_lock_namespace_normalization_needs_no_accessible_share(string path, string expected)
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "Windows namespaces");
        Assert.Equal(expected, ReparsePoints.NormalizeNamespaceForLockKey(path));
    }


    [Fact]
    public void An_exception_in_the_append_callback_releases_both_locks()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);
        Assert.Throws<InvalidOperationException>(() =>
            writer.AppendToConceptAtomic("notes/shared", Fm, _ => throw new InvalidOperationException("probe")));
        AssertAnotherThreadCanEnter(writer);
    }

    [SkippableFact]
    public void An_interrupted_waiter_does_not_leave_a_lexical_gate_held()
    {
        using var tmp = new TempDir();
        using var actual = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("alias", actual.Path), NoLinkPrivilege);
        var owner = new BundleConceptWriter(actual.Path);
        var waiterWriter = new BundleConceptWriter(Path.Combine(tmp.Path, "alias"));
        using var aboutToWait = new ManualResetEventSlim();
        waiterWriter.BeforeContendedWaitForTest = aboutToWait.Set;
        Exception? error = null;
        var waiter = new Thread(() =>
        {
            try
            {
                using var scope = waiterWriter.EnterWriteLock();
            }
            catch (Exception e)
            {
                error = e;
            }
        })
        { IsBackground = true };

        using (owner.EnterWriteLock())
        {
            waiter.Start();
            // The interrupt is pended until the waiter's wait for the busy
            // monitor, which follows this hook.
            Assert.True(aboutToWait.Wait(TimeSpan.FromSeconds(10)), "The waiter never found the monitor busy.");
            waiter.Interrupt();
            Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
            Assert.IsType<ThreadInterruptedException>(error);
        }

        AssertAnotherThreadCanEnter(waiterWriter);
    }


    [SkippableFact]
    public void A_waiter_resolves_the_root_again_after_waiting_for_the_old_target()
    {
        using var tmp = new TempDir();
        using var before = new TempDir();
        using var after = new TempDir();
        Skip.IfNot(tmp.TryCreateJunctionToExternalDir("alias", before.Path), NoLinkPrivilege);
        var root = Path.Combine(tmp.Path, "alias");
        var owner = new BundleConceptWriter(before.Path);
        var waiterWriter = new BundleConceptWriter(root);
        using var aboutToWait = new ManualResetEventSlim();
        // Deterministic: fires only after the waiter resolved the OLD target
        // and found its monitor busy, so the junction swap below cannot
        // race the waiter's first resolution.
        waiterWriter.BeforeContendedWaitForTest = aboutToWait.Set;
        object? entered = null;
        Exception? error = null;
        var waiter = new Thread(() =>
        {
            try
            {
                using var scope = waiterWriter.EnterWriteLock();
                entered = scope.LockObject;
            }
            catch (Exception e)
            {
                error = e;
            }
        })
        { IsBackground = true };

        using (owner.EnterWriteLock())
        {
            waiter.Start();
            Assert.True(aboutToWait.Wait(TimeSpan.FromSeconds(10)), "The waiter never found the old target busy.");
            // Delete only the link itself, never the directory it targets.
            Directory.Delete(root);
            Assert.True(tmp.TryCreateJunctionToExternalDir("alias", after.Path));
        }

        Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(error);
        Assert.Same(new BundleConceptWriter(after.Path).CurrentLockObjectForTest(), entered);
    }

    private static void Retarget(TempDir tmp, string link, string target)
    {
        // Delete only the link itself, never the directory it targets.
        Directory.Delete(link);
        Assert.True(tmp.TryCreateJunctionToExternalDir(Path.GetFileName(link), target), "Could not recreate the junction.");
    }

    private static void AssertAnotherThreadCanEnter(BundleConceptWriter writer)
    {
        Exception? error = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var scope = writer.EnterWriteLock();
            }
            catch (Exception e)
            {
                error = e;
            }
        })
        { IsBackground = true };
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "A lock was leaked.");
        Assert.Null(error);
    }

    private static bool Run(string fileName, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(psi)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
