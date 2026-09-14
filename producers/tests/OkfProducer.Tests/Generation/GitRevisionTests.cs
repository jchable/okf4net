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
/// rather than trying to smuggle a hostile <c>git.exe</c> into a real repository scan.
///
/// <para><b>Why this class mutates <see cref="Environment.CurrentDirectory"/> and the process' own
/// <c>PATH</c>, and why that is dangerous enough to need <see cref="Collection"/>.</b> Both are
/// process-wide, not per-thread, and several other test classes shell out to a real <c>git</c> --
/// directly (<c>CliTests</c>) or through <c>ProducerFixture</c>'s own <c>Git</c> helper and
/// <c>GitRevision.HeadSha</c> (<c>BlastRadiusTests</c>, <c>CheckTests</c>, <c>DeterminismTests</c>).
/// xunit runs different test collections in parallel by default, so a test here that narrows <c>PATH</c>
/// to exclude the real <c>git</c> -- or repoints the current directory at a directory holding a
/// non-executable stand-in file named <c>git</c>/<c>git.exe</c> -- could otherwise land mid-flight of one
/// of those, and either make a legitimate git invocation fail to find git, or (worse, on Windows) have
/// it resolve and attempt to "run" this test's inert stand-in file instead. Both classes are placed in
/// the same named xunit collection below, which is enough on its own: tests in the same collection
/// never run in parallel with each other, with no separate opt-out needed. <see cref="Environment.CurrentDirectory"/>
/// and <c>PATH</c> are restored in <c>finally</c> in every test regardless.</para>
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
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
/// The shared xunit collection name for every test that touches the process' own current directory or
/// <c>PATH</c> -- <see cref="GitRevisionTests"/>, plus every class that shells out to a real <c>git</c>
/// (<c>CliTests</c>, <c>BlastRadiusTests</c>, <c>CheckTests</c>, <c>DeterminismTests</c>). Membership in
/// one named collection is what keeps them from running in parallel with each other; no separate
/// opt-out is needed for that. No fixture is attached -- this exists purely to name the collection.
/// </summary>
public static class ProcessEnvironmentCollection
{
    public const string Name = "Process environment (current directory / PATH)";
}
