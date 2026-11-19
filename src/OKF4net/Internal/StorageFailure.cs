// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Internal;

/// <summary>
/// Which raw <see cref="IOException"/>s, raised while a loaded image reads one object
/// (<see cref="Bundle.ReadResourceTextAsync"/>, hence the validator's <c>index.md</c>/<c>log.md</c>
/// and the attestation capture), say that the STORAGE failed rather than that object (#247; the
/// error-priority rule of #139: global failures abort, per-resource states are local).
/// <para>
/// Global: the device or the transport is not ready, unreachable, timed out or reset, a remote
/// handle stale, the device failing an I/O — the same read can succeed later with the bundle
/// unchanged, so a verdict on the object would be wrong. Everything else stays the object's:
/// absent (<see cref="FileNotFoundException"/>, <see cref="DirectoryNotFoundException"/>), a name
/// too long, over the bound, locked by another process (a sharing or lock violation, which the
/// filesystem provider already retried), and any code this table does not name. Codes that an
/// entry INSIDE the bundle can give on its own are deliberately absent: <c>ENXIO</c> (a socket
/// opened as a file), <c>ENODEV</c> (a device node without a driver), <c>ERROR_CRC</c> (a bad
/// sector of one file).
/// </para>
/// <para>
/// Not applied by the load (the executable-resource survey, the root <c>index.md</c> read):
/// there every raw I/O failure stays the resource's state, because <c>EIO</c> or
/// <c>ERROR_IO_DEVICE</c> can still be one bad file, and one entry must never make the whole
/// bundle unloadable. After the load the read is of one object the caller asked for, and a
/// failure there fails that caller only.
/// </para>
/// <para>
/// .NET gives a Win32 error as the HRESULT <c>0x8007xxxx</c> and, on Unix, the raw
/// <c>errno</c>, whose numbers differ between Linux and the BSD family; a code is read only in
/// the convention of the OS this process runs on, and on any other OS (illumos, Solaris…) no
/// code is global: the classification fails closed to local.
/// </para>
/// </summary>
internal static class StorageFailure
{
    /// <summary>
    /// Win32 codes: <c>ERROR_NOT_READY</c> (21), <c>ERROR_BAD_NETPATH</c> (53),
    /// <c>ERROR_NETWORK_BUSY</c> (54), <c>ERROR_DEV_NOT_EXIST</c> (55), <c>ERROR_BAD_NET_RESP</c> (58),
    /// <c>ERROR_UNEXP_NET_ERR</c> (59), <c>ERROR_NETNAME_DELETED</c> (64), <c>ERROR_BAD_NET_NAME</c> (67),
    /// <c>ERROR_SEM_TIMEOUT</c> (121), <c>ERROR_IO_DEVICE</c> (1117), <c>ERROR_DEVICE_NOT_CONNECTED</c> (1167),
    /// <c>ERROR_NO_NETWORK</c> (1222), <c>ERROR_NETWORK_UNREACHABLE</c> (1231), <c>ERROR_HOST_UNREACHABLE</c> (1232),
    /// <c>ERROR_CONNECTION_ABORTED</c> (1236), <c>ERROR_TIMEOUT</c> (1460).
    /// </summary>
    private static readonly HashSet<int> Win32 = [21, 53, 54, 55, 58, 59, 64, 67, 121, 1117, 1167, 1222, 1231, 1232, 1236, 1460];

    /// <summary>
    /// Linux <c>errno</c>: <c>EIO</c> (5), <c>ENETDOWN</c> (100), <c>ENETUNREACH</c> (101),
    /// <c>ECONNABORTED</c> (103), <c>ECONNRESET</c> (104), <c>ENOTCONN</c> (107), <c>ETIMEDOUT</c> (110),
    /// <c>EHOSTDOWN</c> (112), <c>EHOSTUNREACH</c> (113), <c>ESTALE</c> (116), <c>EREMOTEIO</c> (121),
    /// <c>ENOMEDIUM</c> (123).
    /// </summary>
    private static readonly HashSet<int> Linux = [5, 100, 101, 103, 104, 107, 110, 112, 113, 116, 121, 123];

    /// <summary>
    /// The same failures in the BSD family's numbering (Darwin, FreeBSD): <c>EIO</c> (5),
    /// <c>ENETDOWN</c> (50), <c>ENETUNREACH</c> (51), <c>ECONNABORTED</c> (53), <c>ECONNRESET</c> (54),
    /// <c>ENOTCONN</c> (57), <c>ETIMEDOUT</c> (60), <c>EHOSTDOWN</c> (64), <c>EHOSTUNREACH</c> (65),
    /// <c>ESTALE</c> (70).
    /// </summary>
    private static readonly HashSet<int> Bsd = [5, 50, 51, 53, 54, 57, 60, 64, 65, 70];

    /// <summary>Whether <paramref name="e"/> is a failure of the storage or its transport, not of one object.</summary>
    internal static bool IsStorageFailure(IOException e)
    {
        if (e is FileNotFoundException or DirectoryNotFoundException or PathTooLongException or EndOfStreamException or BundleObjectTooLargeException)
        {
            return false;
        }

        var code = e.HResult;
        if (OperatingSystem.IsWindows())
        {
            return (code & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) && Win32.Contains(code & 0xFFFF);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            return Linux.Contains(code);
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsTvOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsFreeBSD())
        {
            return Bsd.Contains(code);
        }

        return false; // an errno numbering this table does not know: local, never global by guess
    }
}
