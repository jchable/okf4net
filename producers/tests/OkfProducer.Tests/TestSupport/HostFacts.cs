// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Security.Principal;

namespace OkfProducer.Tests.TestSupport;

// Host-probed FactAttribute subclasses for tests that depend on a platform- or privilege-dependent
// filesystem behaviour this project has no Xunit.SkippableFact to lean on (xunit here is 2.9.3, with
// no [SkippableFact]/Skip.If). Each sets FactAttribute.Skip from a real probe of the current host
// rather than a platform check alone, so a host that claims support but actually refuses (an
// elevation policy, a filesystem that ignores an ACE) is reported as "skipped", never silently
// passed. Never use a bare `return` to skip a case -- that passes vacuously and is indistinguishable
// from a real pass in a test run's summary.

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself when this host cannot create a directory link at
/// all, rather than passing vacuously.
///
/// <para>
/// It skips on very few hosts. <see cref="Directory.CreateSymbolicLink(string, string)"/> works
/// unprivileged on Linux and macOS, and on Windows a directory JUNCTION needs no elevation either --
/// measured on this host, where <c>mklink /J</c> succeeds as an ordinary user and
/// <see cref="FileSystemInfo.LinkTarget"/> reports its target.
/// </para>
///
/// <para>Hoisted out of <c>RoslynResolverTests</c> (where it originated) so <c>RepositoryScannerTests</c>
/// can use it too, without a second implementation of the same probe.</para>
/// </summary>
internal sealed class DirectoryLinkFactAttribute : FactAttribute
{
    public DirectoryLinkFactAttribute()
    {
        if (!DirectoryLinks.Supported)
        {
            Skip = "this host can create neither a directory symbolic link nor a junction";
        }
    }
}

/// <summary>Creates directory links for the fixtures that need one, by whichever mechanism this host allows.</summary>
internal static class DirectoryLinks
{
    private static readonly Lazy<bool> Probe = new(ProbeOnce);

    public static bool Supported => Probe.Value;

    /// <summary>Creates <paramref name="target"/> (if it does not already exist), links <paramref name="link"/> to it, and returns the link's path.</summary>
    public static string Create(string link, string target)
    {
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        if (!TryLink(link, target))
        {
            throw new InvalidOperationException($"could not create a directory link at {link} -> {target}.");
        }

        return link;
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return new DirectoryInfo(link).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Windows without Developer Mode refuses a symbolic link; a junction is still allowed.
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var startInfo = new ProcessStartInfo("cmd")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        try
        {
            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            stdout.GetAwaiter().GetResult();
            stderr.GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }

        return Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null;
    }

    private static bool ProbeOnce()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "okf-producer-linkprobe-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            // The target has to exist before the probe: `mklink /J` refuses a missing one, so a
            // probe without this step would report "unsupported" on a host that supports it fine.
            Directory.CreateDirectory(Path.Combine(scratch, "target"));
            return TryLink(Path.Combine(scratch, "link"), Path.Combine(scratch, "target"));
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
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
/// A <see cref="FactAttribute"/> that skips itself unless this host can both apply an NTFS deny ACE
/// unelevated AND have it actually take effect -- Windows only.
/// </summary>
internal sealed class DenyAceFact : FactAttribute
{
    public DenyAceFact()
    {
        if (!DenyAce.Supported)
        {
            Skip = "this host is not Windows, or a deny ACE could not be verified to take effect unelevated";
        }
    }
}

/// <summary>
/// Denies the current user read access to a file, or listing access to a directory, via
/// <c>icacls.exe</c> -- verified to actually take effect before a probe reports support, so a host
/// where the ACE is silently ignored (some non-NTFS volumes, some elevation policies) is reported as
/// unsupported rather than producing a false pass.
///
/// <para>The idea (deny ACE applied and verified before use, lifted before the temp directory holding
/// it is deleted) is copied from <c>tests/OKF4net.Tests/TempDir.cs</c> in the sibling worktree's test
/// project -- not referenced, since that type lives in a different, unrelated test assembly.</para>
/// </summary>
internal static class DenyAce
{
    private static readonly Lazy<bool> Probe = new(ProbeOnce);

    public static bool Supported => OperatingSystem.IsWindows() && Probe.Value;

    /// <summary>
    /// Adds a deny ACE for the current user on <paramref name="path"/> (read, for a file; list, for a
    /// directory) and returns a handle whose <see cref="IDisposable.Dispose"/> lifts it again. Lift the
    /// ACE before the temp directory holding <paramref name="path"/> is deleted -- a deny-list ACE on a
    /// directory blocks <see cref="Directory.Delete(string, bool)"/> of its parent.
    /// </summary>
    public static IDisposable Deny(string path, bool isDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DenyAce.Deny is Windows-only; callers must check DenyAce.Supported first.");
        }

        var sid = "*" + WindowsIdentity.GetCurrent().User!.Value;
        RunIcacls(path, "/deny", sid + (isDirectory ? ":(RD)" : ":(R)"));
        return new Lifted(path, sid);
    }

    private static bool ProbeOnce()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "okf-producer-denyprobe-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            File.WriteAllText(scratch, "probe");
            using var handle = Deny(scratch, isDirectory: false);
            try
            {
                File.ReadAllText(scratch);
                return false; // The deny had no effect: this host cannot be trusted to enforce it.
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                RunIcacls(scratch, "/reset");
                File.Delete(scratch);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void RunIcacls(params string[] args)
    {
        var startInfo = new ProcessStartInfo("icacls.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    private sealed class Lifted(string path, string sid) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            RunIcacls(path, "/remove:d", sid);
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself unless this host is POSIX, not running as root,
/// and a <c>chmod 000</c> can be verified to actually deny a read.
/// </summary>
internal sealed class UnixPermissionFact : FactAttribute
{
    public UnixPermissionFact()
    {
        if (!UnixPermission.Supported)
        {
            Skip = "this host is Windows, running as a privileged user, or a permission change could not be verified to deny a read";
        }
    }
}

/// <summary>Denies all access to a file via a POSIX file mode, verified to actually take effect before a probe reports support (root ignores file permissions entirely).</summary>
internal static class UnixPermission
{
    private static readonly Lazy<bool> Probe = new(ProbeOnce);

    public static bool Supported => !OperatingSystem.IsWindows() && Probe.Value;

    /// <summary>Sets <paramref name="path"/>'s mode to deny all access and returns a handle whose <see cref="IDisposable.Dispose"/> restores the original mode.</summary>
    public static IDisposable DenyAll(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("UnixPermission.DenyAll is POSIX-only; callers must check UnixPermission.Supported first.");
        }

        var original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return new Restored(path, original);
    }

    private static bool ProbeOnce()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
        {
            return false;
        }

        var scratch = Path.Combine(Path.GetTempPath(), "okf-producer-permprobe-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            File.WriteAllText(scratch, "probe");
            using var handle = DenyAll(scratch);
            try
            {
                File.ReadAllText(scratch);
                return false; // The mode change had no effect: this host cannot be trusted to enforce it.
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Delete(scratch);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class Restored(string path, UnixFileMode original) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                File.SetUnixFileMode(path, original);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself on Windows, for a case that needs a POSIX file
/// symlink specifically (unlike a directory junction, creating one needs a privilege Windows does not
/// grant an ordinary process -- see <see cref="DirectoryLinkFactAttribute"/>'s remarks for the
/// directory case, which does not need this).
/// </summary>
internal sealed class UnixOnlyFact : FactAttribute
{
    public UnixOnlyFact()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "this case needs a POSIX file symlink, which an unprivileged process cannot create on Windows";
        }
    }
}
