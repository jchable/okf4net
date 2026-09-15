// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
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
/// an earlier version of this comment claimed. Putting <b>only a class that itself mutates
/// process-wide state</b> in such a collection (<see cref="ProcessEnvironmentCollectionDefinition"/>)
/// is therefore what actually keeps it from overlapping the other four classes above, which stay in
/// their own (parallel, unmarked) collections -- putting them in the SAME named collection as this one,
/// as an earlier round did, would have serialised them against each other for nothing, since none of
/// them mutates process state themselves; they only read <c>PATH</c> indirectly by shelling out.
/// <see cref="Environment.CurrentDirectory"/>, <c>PATH</c> and (where a test sets it)
/// <c>NoDefaultCurrentDirectoryInExePath</c> are restored in <c>finally</c> in every test here
/// regardless of outcome. <c>CodeGraph.Sdk8ImplicitDefinesTests</c> shares this same collection for the
/// identical reason, mutating a different set of process-wide environment variables (MSBuild/SDK
/// resolution, not <c>PATH</c>/current directory) -- see its own remarks for why.</para>
///
/// <para><b><c>NoDefaultCurrentDirectoryInExePath</c> is not incidental.</b> This harness's own process
/// sets that environment variable, which makes Windows' <c>CreateProcess</c> skip the current directory
/// unconditionally when resolving a bare executable name -- and a child process inherits it from this
/// one unless told otherwise. Discovered when a reviewer's mutant (<c>RunGit</c> reverted to a bare
/// <c>new ProcessStartInfo("git")</c>) passed the process-launching tests below unchanged: the
/// current-directory trap those tests build was never even considered by <c>CreateProcess</c>, because
/// this inherited variable told it not to look there, for a reason that has nothing to do with whether
/// the fix is present. A test that wants to observe the OS's ordinary bare-name search order -- current
/// directory before <c>PATH</c>, which is exactly the vulnerability this whole file exists to close --
/// has to clear the variable for its own duration, or it is testing a search order nothing in production
/// ever actually exercises.</para>
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

        // E11 fix round 1: on POSIX the resolver (correctly) requires the execute bit, so a candidate
        // written without it is skipped and this test failed on Linux for a reason unrelated to what it
        // pins -- the TEST was platform-biased, not the resolver. Both stand-ins get the bit, so the
        // trap is a real competitor and the PATH one is a real hit. Neither is ever launched.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(trapGit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(realGit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

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
    /// rather than only to the resolver in isolation, for the FULL-regression shape: <c>PATH</c> carries
    /// nothing usable at all. The two tests above call <see cref="GitRevision.ResolveGitExecutable"/>
    /// directly and would stay green even if <c>RunGit</c> reverted to
    /// <c>new ProcessStartInfo("git")</c> (a bare name) and stopped consulting the resolver's answer
    /// entirely -- this test calls the public <see cref="GitRevision.HeadSha"/> instead, which is what
    /// actually launches a process, so a regression there is what this one exists to catch. See
    /// <see cref="HeadSha_uses_the_PATH_executable_even_when_the_current_directory_also_has_one"/> for
    /// the complementary PARTIAL-regression shape this one cannot see: it proves nothing about a
    /// <c>RunGit</c> that still calls the resolver, still gates on <see langword="null"/>, but then
    /// launches a bare <c>"git"</c> anyway once the resolver returns something -- here <c>PATH</c> is
    /// empty, so that distinction never arises.
    ///
    /// <para>The current directory holds a copy of <c>cmd.exe</c> named <c>git.exe</c> -- a real,
    /// launchable Windows executable rather than an inert stand-in, since a plain text file of that name
    /// would fail to start at all (<c>Win32Exception</c>, "not a valid Win32 application") and
    /// <c>RunGit</c>'s own catch turns that into the very same <see langword="null"/> a correct
    /// resolution produces, masking the regression this test exists to catch either way. <c>cmd.exe</c>
    /// launched directly (no shell, argv <c>rev-parse HEAD</c> -- neither a recognised switch) starts an
    /// interactive session, prints its banner to stdout, and exits 0 once its stdin hits EOF --
    /// <c>RunGit</c> now redirects and immediately closes its child's stdin itself (the round-2 fix for
    /// this same finding), so this no longer depends on what this test HOST's own stdin happens to be, as
    /// an earlier version of this comment claimed. <c>PATH</c> is emptied, so with the fix in place the
    /// resolver finds nothing and <c>RunGit</c> returns <see langword="null"/> before <c>Process.Start</c>
    /// is ever called; with the regression, <c>CreateProcess</c> would find and run the current-directory
    /// copy instead, and <c>HeadSha</c> would return its (non-null) banner text rather than
    /// <see langword="null"/> -- provided <c>NoDefaultCurrentDirectoryInExePath</c> is cleared first; see
    /// the class doc.</para>
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
        var originalNoDefaultCwd = Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath");
        try
        {
            Environment.CurrentDirectory = cwdTrap.Path;
            Environment.SetEnvironmentVariable("PATH", emptyPathDir.Path);
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", null);

            Assert.Null(GitRevision.HeadSha(cwdTrap.Path));
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", originalNoDefaultCwd);
        }
    }

    /// <summary>
    /// The PARTIAL-regression shape <see cref="HeadSha_goes_through_the_resolver_rather_than_a_bare_process_start"/>
    /// cannot see: a <c>RunGit</c> that still calls <see cref="GitRevision.ResolveGitExecutable"/>, still
    /// refuses to run at all when it returns <see langword="null"/>, but was edited to launch a bare
    /// <c>"git"</c> instead of the resolved path once it returns something. With nothing on <c>PATH</c>
    /// that shape is indistinguishable from the full regression -- both end up asking <c>CreateProcess</c>
    /// to resolve a bare name -- so this needs <c>PATH</c> to hold a second, genuinely resolvable
    /// candidate, and a way to tell "the resolved one ran" apart from "the current-directory one ran".
    ///
    /// <para><b>The discriminator.</b> Two different real Windows executables stand in as <c>git.exe</c>,
    /// one at each location, chosen because both happen to exit 0 with SOME stdout for the exact argv
    /// <c>RunGit</c> sends (<c>rev-parse HEAD</c>) -- <c>hostname</c>, <c>whoami</c>, <c>mode</c>,
    /// <c>chcp</c>, <c>tzutil</c>, <c>cscript</c>, <c>expand</c>, <c>klist</c> and <c>getmac</c> were all
    /// tried and every one exits non-zero on those args, which <c>RunGit</c> maps to <see langword="null"/>
    /// -- indistinguishable from "nothing resolved" either way, so none of them can serve. <c>attrib</c>
    /// (current directory) and <c>cmd</c> (<c>PATH</c>) both exit 0: <c>attrib</c> treats
    /// <c>rev-parse</c>/<c>HEAD</c> as unrecognised attribute switches and prints a short, fixed error
    /// about it; <c>cmd</c>, launched with no recognised switch, prints its startup banner (see the test
    /// above). Neither message depends on WHERE the copy sits -- the working directory line in <c>cmd</c>'s
    /// banner comes from <see cref="ProcessStartInfo.WorkingDirectory"/>, which this test sets to the same
    /// value either way, not from the exe's own location -- so the current directory's answer is captured
    /// directly, once, by running that copy independently of <see cref="GitRevision"/> entirely
    /// (<see cref="RunDirectly"/>), rather than hard-coded: <c>attrib</c>'s message is locale-dependent
    /// (French on this host) and this way the test does not need to know it in advance. With the fix,
    /// <c>HeadSha</c> answers with <c>cmd</c>'s banner, which is not equal to that captured text; under
    /// the partial regression, it would equal it exactly, because <c>CreateProcess</c> would have found
    /// and run the SAME current-directory copy this test already ran once to learn its answer.</para>
    /// </summary>
    [Fact]
    public void HeadSha_uses_the_PATH_executable_even_when_the_current_directory_also_has_one()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Both stand-ins are Windows-specific; see the drive-relative test above for the same
            // no-skip-mechanism trade-off.
            return;
        }

        var attrib = Path.Combine(Environment.SystemDirectory, "attrib.exe");
        var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.True(File.Exists(attrib), $"expected '{attrib}' to exist on this host -- it stands in for a harmless real executable.");
        Assert.True(File.Exists(cmdExe), $"expected '{cmdExe}' to exist on this host -- it stands in for a harmless real executable.");

        using var cwdTrap = new TempDir();
        using var pathDir = new TempDir();
        var trapGit = Path.Combine(cwdTrap.Path, "git.exe");
        File.Copy(attrib, trapGit);
        File.Copy(cmdExe, Path.Combine(pathDir.Path, "git.exe"));

        // What the current-directory copy alone would answer -- captured directly, bypassing
        // GitRevision entirely, with the same argv and working directory RunGit itself uses, so the
        // comparison below does not depend on knowing attrib's exact (locale-dependent) message.
        var trapAnswer = RunDirectly(trapGit, cwdTrap.Path);
        Assert.NotNull(trapAnswer); // the premise this test rests on: the trap alone is a valid, working stand-in.

        var originalCwd = Environment.CurrentDirectory;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalNoDefaultCwd = Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath");
        try
        {
            Environment.CurrentDirectory = cwdTrap.Path;
            Environment.SetEnvironmentVariable("PATH", pathDir.Path);
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", null);

            var result = GitRevision.HeadSha(cwdTrap.Path);

            Assert.NotNull(result);
            Assert.NotEqual(trapAnswer, result);
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", originalNoDefaultCwd);
        }
    }

    /// <summary>
    /// Runs <paramref name="executable"/> directly with the same argv (<c>rev-parse HEAD</c>) and stdin
    /// handling <see cref="GitRevision.RunGit"/> uses, entirely outside <see cref="GitRevision"/> --
    /// used only to learn what one stand-in alone would answer, as an oracle for
    /// <see cref="HeadSha_uses_the_PATH_executable_even_when_the_current_directory_also_has_one"/>, never
    /// to exercise the code under test.
    /// </summary>
    private static string? RunDirectly(string executable, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = workingDirectory,
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"could not start '{executable}' for the test's own probe.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? stdout.Trim() : null;
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
/// The xunit collection for every test class that mutates PROCESS-WIDE environment state (as opposed
/// to merely reading it, or shelling out without mutating it). <c>DisableParallelization</c> on a
/// <see cref="CollectionDefinitionAttribute"/> makes THIS collection run by itself, after every
/// ordinary (parallel) collection has finished -- so nothing that shells out to a real <c>dotnet</c> or
/// <c>git</c> (<c>CliTests</c>, <c>BlastRadiusTests</c>, <c>CheckTests</c>, <c>DeterminismTests</c>,
/// <c>RoslynResolverTests</c>, all left in their own default collections, since none of them mutates
/// process state itself) can be mid-flight while a member of THIS collection is narrowing <c>PATH</c>,
/// repointing <see cref="Environment.CurrentDirectory"/>, or clearing MSBuild/SDK-resolution
/// environment variables. Two members today: <see cref="GitRevisionTests"/> (the original reason this
/// collection exists -- see its own remarks) and <c>CodeGraph.Sdk8ImplicitDefinesTests</c> (added for
/// its <c>NestedDotnetEnvironment</c>, a different set of variables, same underlying hazard). No
/// fixture is attached -- this exists purely to declare the collection and its parallelization
/// setting.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ProcessEnvironmentCollectionDefinition
{
    public const string Name = "Process environment (PATH / current directory / MSBuild env vars) -- mutators only";
}
