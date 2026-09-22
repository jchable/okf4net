// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.AccessControl;
using System.Security.Principal;

namespace OKF4net.Tests;

/// <summary>
/// Shared test helper: a tiny dependency-free temporary-directory fixture.
/// Only the subset of members exercised by the tests is included
/// (<see cref="Path"/>, <see cref="Write"/>, <see cref="Dispose"/>).
/// </summary>
public sealed class TempDir : IDisposable
{
    /// <summary>The directory path.</summary>
    public string Path { get; }

    /// <summary>Creates a fresh unique temporary directory.</summary>
    public TempDir()
    {
        Path = Directory.CreateTempSubdirectory("okf4net-").FullName;
    }

    /// <summary>Writes a file (creating parent directories) relative to the temp root, as UTF-8 without a BOM.</summary>
    public string Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));
        return full;
    }

    /// <summary>
    /// Attempts to create a file symlink at <paramref name="relativeLink"/>
    /// pointing at <paramref name="relativeTarget"/> (both relative to the
    /// temp root; the target need not already exist). Returns <c>false</c>
    /// instead of throwing when the platform/process lacks symlink-creation
    /// privilege -- e.g. Windows without Developer Mode or an elevated
    /// process (<c>SeCreateSymbolicLinkPrivilege</c>) -- so callers can skip
    /// the symlink-dependent assertions rather than fail the whole run on
    /// machines where a real symlink simply cannot be created.
    /// </summary>
    public bool TryCreateFileSymlink(string relativeLink, string relativeTarget)
    {
        var linkPath = System.IO.Path.Combine(Path, relativeLink);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(linkPath)!);
        try
        {
            File.CreateSymbolicLink(linkPath, System.IO.Path.Combine(Path, relativeTarget));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to create a directory symlink at <paramref name="relativeLink"/>
    /// pointing at <paramref name="relativeTarget"/> (both relative to the
    /// temp root). See <see cref="TryCreateFileSymlink"/> for the privilege
    /// caveat.
    /// </summary>
    public bool TryCreateDirectorySymlink(string relativeLink, string relativeTarget)
    {
        var linkPath = System.IO.Path.Combine(Path, relativeLink);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(linkPath)!);
        try
        {
            Directory.CreateSymbolicLink(linkPath, System.IO.Path.Combine(Path, relativeTarget));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to create a directory reparse point at
    /// <paramref name="relativeLink"/> (relative to the temp root) pointing
    /// at the ABSOLUTE, external directory <paramref name="externalTarget"/>
    /// (typically another <see cref="TempDir"/>). Tries a Windows junction
    /// first via <c>cmd /c mklink /J</c> -- unlike a symlink, a junction
    /// needs no special privilege on Windows -- falling back to
    /// <see cref="Directory.CreateSymbolicLink(string, string)"/> if
    /// <c>mklink</c> is unavailable or fails (e.g. non-Windows). Returns
    /// <c>false</c> instead of throwing if neither mechanism succeeds, so
    /// callers can skip the reparse-point-dependent assertions
    /// (<c>Skip.IfNot(created, …)</c>) rather than fail the whole run on a
    /// machine where neither can be created.
    /// </summary>
    public bool TryCreateJunctionToExternalDir(string relativeLink, string externalTarget)
    {
        var linkPath = System.IO.Path.Combine(Path, relativeLink);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(linkPath) ?? Path);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    ArgumentList = { "/c", "mklink", "/J", linkPath, externalTarget },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = System.Diagnostics.Process.Start(psi);
                process!.WaitForExit();
                if (process.ExitCode == 0 && Directory.Exists(linkPath))
                {
                    return true;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // fall through to the symlink attempt below.
            }
        }

        try
        {
            Directory.CreateSymbolicLink(linkPath, externalTarget);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to create a FILE reparse point at <paramref name="relativeLink"/>
    /// (relative to the temp root) pointing at the ABSOLUTE, external file
    /// <paramref name="externalTarget"/> (typically a file inside another
    /// <see cref="TempDir"/>), via <see cref="File.CreateSymbolicLink(string, string)"/>.
    /// Unlike a directory junction, NTFS has no unprivileged way to create a
    /// file-level reparse point, so this needs the same
    /// <c>SeCreateSymbolicLinkPrivilege</c> as <see cref="TryCreateFileSymlink"/>.
    /// Returns <c>false</c> instead of throwing when unavailable, so callers
    /// can skip the reparse-point-dependent assertions
    /// (<c>Skip.IfNot(created, …)</c>) rather than fail the whole run.
    /// </summary>
    public bool TryCreateFileSymlinkToExternalFile(string relativeLink, string externalTarget)
    {
        var linkPath = System.IO.Path.Combine(Path, relativeLink);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(linkPath) ?? Path);
        try
        {
            File.CreateSymbolicLink(linkPath, externalTarget);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to deny the current Windows user write access (create
    /// files/subdirectories) to the directory at <paramref name="relativeDir"/>
    /// (relative to the temp root; created first if it does not yet exist),
    /// via an explicit NTFS deny <see cref="FileSystemAccessRule"/> -- unlike
    /// <see cref="FileAttributes.ReadOnly"/> on a directory (which NTFS does
    /// not enforce for child creation), an explicit deny ACE reliably makes
    /// <see cref="File.WriteAllText(string, string)"/>/<see cref="Directory.CreateDirectory(string)"/>
    /// inside it throw <see cref="UnauthorizedAccessException"/>, even for
    /// the owning process. Only <see cref="FileSystemRights.Write"/> is
    /// denied (not <see cref="FileSystemRights.Delete"/>), so <see cref="Dispose"/>'s
    /// best-effort recursive delete still succeeds afterward. Returns
    /// <c>false</c> instead of throwing on a non-Windows platform or if the
    /// ACL change itself is denied, so callers can skip the
    /// permission-dependent assertion (<c>Skip.IfNot(denied, …)</c>) rather
    /// than fail the whole run on a machine/platform where this cannot be
    /// set up.
    /// </summary>
    public bool TryMakeDirectoryUnwritable(string relativeDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var fullPath = System.IO.Path.Combine(Path, relativeDir);
        Directory.CreateDirectory(fullPath);

        try
        {
            var info = new DirectoryInfo(fullPath);
            var security = info.GetAccessControl();
            var sid = WindowsIdentity.GetCurrent().User!;
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Write, AccessControlType.Deny));
            info.SetAccessControl(security);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts to create, at <paramref name="relativeLink"/> (relative to
    /// the temp root), a Windows junction to the ABSOLUTE external directory
    /// <paramref name="externalTarget"/> whose link status CANNOT BE
    /// INSPECTED by the current user, while the OS still lets that user
    /// traverse it: a deny <c>ReadAttributes</c> ACE on the junction itself
    /// (<c>icacls /L</c>, so the ACE lands on the link rather than its
    /// target) plus a deny <c>ListDirectory</c> ACE on its parent directory.
    /// Both are needed: with the parent still listable, .NET's
    /// <see cref="File.GetAttributes(string)"/> falls back to
    /// <c>FindFirstFile</c> on the parent and reads the junction's
    /// attributes from its directory entry anyway. Verified before returning
    /// -- <see cref="File.GetAttributes(string)"/> on the junction must throw
    /// <see cref="UnauthorizedAccessException"/> -- so a test never runs
    /// against a setup that silently failed to deny anything.
    /// </summary>
    /// <returns>
    /// A handle whose <see cref="UninspectableJunction.Dispose"/> removes both
    /// deny ACEs and the junction itself, or <see langword="null"/> on a
    /// non-Windows platform or if any step fails (callers skip with
    /// <c>Skip.If(handle is null, ...)</c>). Declare the handle AFTER the
    /// <see cref="TempDir"/>s it lives in, so <c>using</c> disposes it first:
    /// <see cref="Dispose"/>'s recursive delete cannot remove a directory
    /// whose listing is denied, and on Windows does not reliably remove a
    /// junction by recursion either.
    /// </returns>
    /// <param name="relativeLink">The junction's path, relative to the temp root.</param>
    /// <param name="externalTarget">The absolute directory the junction points at.</param>
    /// <param name="denyParentListing">
    /// <see langword="true"/> (the default) for the uninspectable junction
    /// described above. <see langword="false"/> keeps the parent listable, so
    /// the junction's attributes stay readable (it is still reported as a
    /// reparse point) while its TARGET cannot be read:
    /// <see cref="Directory.ResolveLinkTarget(string, bool)"/> throws
    /// <see cref="UnauthorizedAccessException"/> -- also verified before
    /// returning.
    /// </param>
    public UninspectableJunction? TryCreateUninspectableJunction(string relativeLink, string externalTarget, bool denyParentListing = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!TryCreateJunctionToExternalDir(relativeLink, externalTarget))
        {
            return null;
        }

        var handle = new UninspectableJunction(System.IO.Path.Combine(Path, relativeLink), WindowsIdentity.GetCurrent().User!.Value, denyParentListing);
        if (!handle.Deny())
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    /// <summary>Removes the temporary directory and its contents (best-effort).</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup on Dispose.
        }
    }
}

/// <summary>
/// A junction created by <see cref="TempDir.TryCreateUninspectableJunction"/>,
/// carrying the two deny ACEs that make its link status uninspectable.
/// </summary>
public sealed class UninspectableJunction : IDisposable
{
    private readonly string _sid;
    private readonly bool _denyParentListing;
    private bool _disposed;

    internal UninspectableJunction(string linkPath, string sid, bool denyParentListing)
    {
        LinkPath = linkPath;
        ParentPath = System.IO.Path.GetDirectoryName(linkPath)!;
        _sid = "*" + sid;
        _denyParentListing = denyParentListing;
    }

    /// <summary>The junction's absolute path.</summary>
    public string LinkPath { get; }

    /// <summary>The junction's parent directory, which carries the deny-list ACE (when requested).</summary>
    public string ParentPath { get; }

    /// <summary>Adds the deny ACEs and confirms they had the promised effect.</summary>
    internal bool Deny()
    {
        if (!Icacls(LinkPath, "/L", "/deny", _sid + ":(RA)")
            || (_denyParentListing && !Icacls(ParentPath, "/deny", _sid + ":(RD)")))
        {
            return false;
        }

        try
        {
            if (!_denyParentListing)
            {
                // Attributes still readable (through the parent's listing),
                // target not: ResolveLinkTarget must throw.
                if ((File.GetAttributes(LinkPath) & FileAttributes.ReparsePoint) == 0)
                {
                    return false;
                }

                Directory.ResolveLinkTarget(LinkPath, returnFinalTarget: true);
                return false; // Target still readable: the setup did not take effect.
            }

            File.GetAttributes(LinkPath);
            return false; // Still inspectable: the setup did not take effect.
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes both deny ACEs, leaving an ordinary (inspectable) junction in
    /// place. Idempotent; <see cref="Dispose"/> calls it too.
    /// </summary>
    public void Lift()
    {
        Icacls(ParentPath, "/remove:d", _sid);
        Icacls(LinkPath, "/L", "/remove:d", _sid);
    }

    /// <summary>Lifts both deny ACEs, then removes the junction itself (never its target's content).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Lift();
        try
        {
            // Non-recursive: removes the link only.
            Directory.Delete(LinkPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort, like TempDir.Dispose.
        }
    }

    private static bool Icacls(params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("icacls.exe")
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

/// <summary>
/// Paths that do not exist in ways Windows reports as a plain
/// <see cref="IOException"/> rather than a not-found type -- an empty drive
/// (<c>ERROR_NOT_READY</c>) and a missing share (<c>ERROR_BAD_NET_NAME</c>, or
/// <c>ERROR_BAD_NETPATH</c> when the local server service is off).
/// </summary>
public static class AbsentPaths
{
    /// <summary>
    /// A UNC path under a share that does not exist on this machine. Windows
    /// only; on another platform it is just an odd relative name.
    /// </summary>
    public const string MissingShareSite = "\\\\localhost\\okf4net-h1-no-such-share$\\site";

    /// <summary>
    /// <c>X:\site</c> for the first drive that exists but holds no volume (an
    /// empty card reader or optical drive), or <see langword="null"/> if this
    /// machine has none.
    /// </summary>
    public static string? SiteOnADriveWithNoVolume()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var drive = DriveInfo.GetDrives().FirstOrDefault(d => !d.IsReady);
        return drive is null ? null : System.IO.Path.Combine(drive.RootDirectory.FullName, "site");
    }

    /// <summary>
    /// The HRESULTs <c>ReparsePoints</c> maps to "absent" when they arrive on
    /// a plain <see cref="IOException"/>: <c>ERROR_NOT_READY</c> (0x80070015),
    /// <c>ERROR_BAD_NETPATH</c> (0x80070035) and <c>ERROR_BAD_NET_NAME</c>
    /// (0x80070043). Mirrored here on purpose, so a test can tell "this host
    /// produces the case" apart from "our mapping handles it".
    /// </summary>
    public static readonly IReadOnlyList<int> AbsenceHResults =
        [unchecked((int)0x80070015), unchecked((int)0x80070035), unchecked((int)0x80070043)];

    /// <summary>
    /// Probes the environment BEFORE a test asserts anything: reads
    /// <paramref name="path"/>'s attributes and reports whether this host
    /// answers with exactly the case under test -- a plain
    /// <see cref="IOException"/> carrying one of <see cref="AbsenceHResults"/>.
    /// A host that answers otherwise (loopback SMB blocked, workstation
    /// service off, a drive reporting <c>ERROR_NO_MEDIA_IN_DRIVE</c>, ...) does
    /// not produce the case, and the caller skips with
    /// <paramref name="observed"/> in the reason rather than failing on the
    /// host's network setup. Reads only; never creates anything.
    /// </summary>
    /// <param name="path">The path to probe.</param>
    /// <param name="observed">What the host answered, for a skip reason.</param>
    public static bool HostReportsAbsenceAsPlainIOException(string path, out string observed)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            observed = $"no exception (attributes {attributes})";
            return false;
        }
        catch (Exception e)
        {
            observed = $"{e.GetType().Name} HResult 0x{e.HResult:X8}";
            return e.GetType() == typeof(IOException) && AbsenceHResults.Contains(e.HResult);
        }
    }
}
