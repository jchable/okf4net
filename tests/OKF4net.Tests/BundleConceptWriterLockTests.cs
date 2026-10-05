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
        string mainResult;
        using (a.EnterWriteLock())
        {
            worker.Start();
            // The timeout is a deadlock guard, not an ordering delay: the
            // worker either blocks on the bundle monitor or runs to the end.
            settled = SpinWait.SpinUntil(
                () => (worker.ThreadState & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) != 0,
                TimeSpan.FromSeconds(10));
            workerFinishedWhileHeld = (worker.ThreadState & ThreadState.Stopped) != 0;
            mainResult = a.AppendToConceptAtomic("notes/shared", Fm, body => (body ?? string.Empty).Trim() + "[A]");
        }

        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "The worker did not finish.");
        Assert.True(settled, "The worker neither waited nor completed.");
        Assert.False(workerFinishedWhileHeld, "B wrote while A held the lock: the two writers do not share it.");
        Assert.DoesNotContain("Error", mainResult, StringComparison.Ordinal);
        Assert.DoesNotContain("Error", workerResult!, StringComparison.Ordinal);

        var doc = OkfDocument.Parse(File.ReadAllText(Path.Combine(external.Path, "bundle", "notes", "shared.md")));
        // A wrote first, under its hold; B's read-modify-write then saw it.
        Assert.Equal("[A][B]", doc.Body.Trim());
    }

    [SkippableFact]
    public void A_nested_acquisition_reuses_the_held_lock_even_if_the_topology_changed()
    {
        using var tmp = new TempDir();
        using var external = new TempDir();
        var a = new BundleConceptWriter(Path.Combine(tmp.Path, "later", "bundle"));

        object outerObject;
        object nowObject;
        using (var outer = a.EnterWriteLock())
        {
            outerObject = outer.LockObject;
            Skip.IfNot(tmp.TryCreateJunctionToExternalDir("later", external.Path), NoLinkPrivilege);
            Directory.CreateDirectory(Path.Combine(external.Path, "bundle"));

            nowObject = a.CurrentLockObjectForTest();
            Assert.NotSame(outerObject, nowObject); // the topology change moved the root

            using (var inner = a.EnterWriteLock())
            {
                Assert.Same(outerObject, inner.LockObject);
                Assert.True(Monitor.IsEntered(outerObject));
                Assert.False(Monitor.IsEntered(nowObject));
            }

            Assert.True(Monitor.IsEntered(outerObject)); // the outer hold survives the inner release
        }

        Assert.False(Monitor.IsEntered(outerObject));
        Assert.False(Monitor.IsEntered(nowObject));

        // A fresh outermost acquisition follows the new topology.
        using var next = a.EnterWriteLock();
        Assert.Same(nowObject, next.LockObject);
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
