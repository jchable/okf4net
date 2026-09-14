// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Generation;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// Finding #12: a bare <c>Process.Start("git")</c> lets Windows' <c>CreateProcess</c> search the
/// current directory before <c>PATH</c>, so a <c>git.exe</c> committed inside the repository being
/// scanned would run with the operator's privileges the moment <c>okfgen</c> was launched from inside
/// that checkout -- <c>--no-msbuild</c> included, whose whole promise is that nothing from the tree
/// executes. <see cref="GitRevision.ResolveGitExecutable"/> is the seam that closes it: resolved
/// against <c>PATH</c> only, never the current directory, and these tests exercise that seam directly
/// rather than trying to smuggle a hostile <c>git.exe</c> into a real repository scan -- except the last
/// one, which deliberately calls the public <see cref="GitRevision.HeadSha"/> instead, precisely because
/// the direct-seam tests below would stay green even if <see cref="GitRevision"/>'s own <c>RunGit</c>
/// stopped calling the resolver at all.
///
/// <para><b>Why this class mutates <see cref="Environment.CurrentDirectory"/> and the process' own
/// <c>PATH</c>, and why that needs isolating.</b> Both are process-wide, not per-thread, and several
/// other test classes shell out to a real <c>git</c> -- directly (<c>CliTests</c>) or through
/// <c>ProducerFixture</c>'s own <c>Git</c> helper and <c>GitRevision.HeadSha</c>
/// (<c>BlastRadiusTests</c>, <c>CheckTests</c>, <c>DeterminismTests</c>). In xunit 2.x, a collection
/// carrying <see cref="CollectionDefinitionAttribute.DisableParallelization"/> runs ALONE, after every
/// parallel collection has finished -- not merely non-parallel with respect to its own members, which
/// an earlier version of this comment claimed. Putting only <b>this</b> class in such a collection
/// (<see cref="ProcessEnvironmentCollectionDefinition"/>) is therefore what actually keeps it from
/// overlapping the other four classes above, which stay in their own (parallel, unmarked) collections
/// -- putting them in the SAME named collection as this one, as an earlier round did, would have
/// serialised them against each other for nothing, since none of them mutates process state themselves;
/// they only read <c>PATH</c> indirectly by shelling out. <see cref="Environment.CurrentDirectory"/> and
/// <c>PATH</c> are restored in <c>finally</c> in every test here regardless of outcome.</para>
/// </summary>
[Collection(ProcessEnvironmentCollectionDefinition.Name)]
public class GitRevisionTests
{
    [Fact]
    public void ResolveGitExecutable_prefers_PATH_over_the_current_directory()
    {
        var gitName = OperatingSystem.IsWindows() ? "git.exe" : "git";

        using var cwdTrap = new TempDir();
        using var pathDir = new TempDir();
        var trapGit = Path.Combine(cwdTrap.Path, gitName);
        var realGit = Path.Combine(pathDir.Path, gitName);
        File.WriteAllText(trapGit, "not a real executable -- if this ever runs, the resolver picked the wrong one");
        File.WriteAllText(realGit, "not a real executable either -- only its path is asserted on, never launched");

        var originalCwd = Environment.CurrentDirectory;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = cwdTrap.Path;
            Environment.SetEnvironmentVariable("PATH", pathDir.Path);

            var resolved = GitRevision.ResolveGitExecutable();

            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(realGit), resolved, ignoreCase: OperatingSystem.IsWindows());
            Assert.False(
                resolved.StartsWith(Path.GetFullPath(cwdTrap.Path), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
                $"resolved '{resolved}' from the current directory '{cwdTrap.Path}' instead of PATH.");
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void ResolveGitExecutable_ignores_a_relative_PATH_entry()
    {
        // A relative PATH entry -- "." above all -- is itself resolved against the current directory by
        // whatever eventually consumes it, so honouring one here would reopen the exact hole this method
        // exists to close, just one indirection later. It must be skipped outright, not resolved and then
        // rejected only if it happens to land on the trap file.
        var gitName = OperatingSystem.IsWindows() ? "git.exe" : "git";

        using var cwdTrap = new TempDir();
        File.WriteAllText(Path.Combine(cwdTrap.Path, gitName), "not a real executable -- must never be picked");

        var originalCwd = Environment.CurrentDirectory;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = cwdTrap.Path;
            Environment.SetEnvironmentVariable("PATH", "." + Path.PathSeparator);

            Assert.Null(GitRevision.ResolveGitExecutable());
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    /// <summary>
    /// A Windows <b>drive-relative</b> <c>PATH</c> entry (<c>E:tools</c>, or the degenerate <c>E:.</c>
    /// this test builds) is <see cref="Path.IsPathRooted(string)"/>-true but resolves against
    /// <see cref="Environment.CurrentDirectory"/>'s own drive -- reopening the current-directory hole
    /// one drive letter removed, which is why the resolver checks
    /// <see cref="Path.IsPathFullyQualified(string)"/> instead. Built from the current directory's own
    /// drive rather than a hard-coded letter, so this runs correctly whatever drive the temp directory
    /// this host uses lands on.
    /// </summary>
    [Fact]
    public void ResolveGitExecutable_ignores_a_drive_relative_PATH_entry()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Drive-relative paths are an NTFS/Windows-specific shape with no Unix equivalent -- nothing
            // to test on another platform. No skip mechanism (Xunit.SkippableFact and the like) is
            // referenced by this test project, so this returns having asserted nothing rather than
            // failing or fabricating a Windows-only scenario; the same trade-off other platform-gated
            // tests in this suite already make (see e.g. HostileInputTests.TryCreateJunction).
            return;
        }

        using var cwdTrap = new TempDir();
        var driveRelative = Path.GetPathRoot(Path.GetFullPath(cwdTrap.Path))![..2]; // e.g. "C:" from "C:\..."
        File.WriteAllText(Path.Combine(cwdTrap.Path, "git.exe"), "not a real executable -- must never be picked");

        var originalCwd = Environment.CurrentDirectory;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = cwdTrap.Path;

            // "C:." -- current directory OF DRIVE C:, which SetCurrentDirectory keeps in step with
            // Environment.CurrentDirectory above via Windows' own per-drive tracking, not merely a
            // string coincidence. The probe that found this (E:\tmp\e12probe\e2\cwd\tools\git.EXE from
            // PATH `E:.`) is the shape this test reproduces on whatever drive this host's temp
            // directory happens to be on.
            Environment.SetEnvironmentVariable("PATH", driveRelative + "." + Path.PathSeparator);

            Assert.Null(GitRevision.ResolveGitExecutable());
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    /// <summary>
    /// Ties <see cref="GitRevision.RunGit"/> to <see cref="GitRevision.ResolveGitExecutable"/> itself,
    /// rather than only to the resolver in isolation. The two tests above call
    /// <see cref="GitRevision.ResolveGitExecutable"/> directly and would stay green even if
    /// <c>RunGit</c> reverted to <c>new ProcessStartInfo("git")</c> (a bare name) and stopped consulting
    /// the resolver's answer at all -- this test calls the public <see cref="GitRevision.HeadSha"/>
    /// instead, which is what actually launches a process, so a regression there is what this one
    /// exists to catch.
    ///
    /// <para>The current directory holds a copy of <c>cmd.exe</c> named <c>git.exe</c> -- a real,
    /// launchable Windows executable rather than an inert stand-in, since a plain text file of that name
    /// would fail to start at all (<c>Win32Exception</c>, "not a valid Win32 application") and
    /// <c>RunGit</c>'s own catch turns that into the very same <see langword="null"/> a correct
    /// resolution produces, masking the regression this test exists to catch either way. <c>cmd.exe</c>
    /// launched directly (no shell, argv <c>rev-parse HEAD</c> -- neither a recognised switch) starts an
    /// interactive session, prints its banner to stdout, and exits 0 once its (uninherited, closed by
    /// the test host) stdin hits EOF -- measured, not assumed: <c>RunGit</c> does not set
    /// <c>RedirectStandardInput</c>. <c>PATH</c> is emptied, so with the fix in place the resolver finds
    /// nothing and <c>RunGit</c> returns <see langword="null"/> before <c>Process.Start</c> is ever
    /// called; with the regression, <c>CreateProcess</c> would find and run the current-directory copy
    /// instead, and <c>HeadSha</c> would return its (non-null) banner text rather than <see langword="null"/>.</para>
    /// </summary>
    [Fact]
    public void HeadSha_goes_through_the_resolver_rather_than_a_bare_process_start()
    {
        if (!OperatingSystem.IsWindows())
        {
            // cmd.exe is a Windows-specific stand-in; see the drive-relative test above for the same
            // no-skip-mechanism trade-off.
            return;
        }

        var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.True(File.Exists(cmdExe), $"expected '{cmdExe}' to exist on this host -- it stands in for a harmless real executable.");

        using var cwdTrap = new TempDir();
        using var emptyPathDir = new TempDir();
        File.Copy(cmdExe, Path.Combine(cwdTrap.Path, "git.exe"));

        var originalCwd = Environment.CurrentDirectory;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.CurrentDirectory = cwdTrap.Path;
            Environment.SetEnvironmentVariable("PATH", emptyPathDir.Path);

            Assert.Null(GitRevision.HeadSha(cwdTrap.Path));
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "okfproducer-gitrevision-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// The xunit collection <see cref="GitRevisionTests"/> alone belongs to. <c>DisableParallelization</c>
/// on a <see cref="CollectionDefinitionAttribute"/> makes THIS collection run by itself, after every
/// ordinary (parallel) collection has finished -- so nothing that shells out to a real <c>git</c>
/// (<c>CliTests</c>, <c>BlastRadiusTests</c>, <c>CheckTests</c>, <c>DeterminismTests</c>, all left in
/// their own default collections) can be mid-flight while <see cref="GitRevisionTests"/> is narrowing
/// <c>PATH</c> or repointing <see cref="Environment.CurrentDirectory"/>. No fixture is attached -- this
/// exists purely to declare the collection and its parallelization setting.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ProcessEnvironmentCollectionDefinition
{
    public const string Name = "Process environment (current directory / PATH) -- GitRevisionTests only";
}
