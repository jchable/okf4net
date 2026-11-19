// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3;

/// <summary>The provider operation that failed.</summary>
public enum S3Operation
{
    /// <summary>A listing page (EnumerateAsync, GetVersionAsync).</summary>
    List,

    /// <summary>An object read (ReadAsync).</summary>
    Read,

    /// <summary>An existence check (ExistsAsync).</summary>
    Exists,
}
