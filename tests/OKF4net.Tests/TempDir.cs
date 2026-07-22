// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Shared test helper: a tiny dependency-free temporary-directory fixture.
/// Port of <c>tests/common/mod.rs</c>. Only the subset of members exercised
/// by the ported tests is included (<see cref="Path"/>, <see cref="Write"/>,
/// <see cref="Dispose"/>) — Rust's <c>read</c>/<c>mkdir</c> helpers are
/// unused by the Task 8 tests and are omitted.
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
    /// (<c>if (!created) return;</c>) rather than fail the whole run on a
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
    /// (<c>if (!created) return;</c>) rather than fail the whole run.
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

    /// <summary>Removes the temporary directory and its contents (best-effort).</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup, mirroring Rust's `Drop` impl (tests/common/mod.rs:57-61).
        }
    }
}
