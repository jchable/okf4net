// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>
/// Base class for all OKF4net exceptions. Mirrors the Rust crate's use of a
/// small set of typed errors (e.g. <c>YamlError</c>) rather than a single
/// generic error type.
/// </summary>
public abstract class OkfException : Exception
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    protected OkfException(string message)
        : base(message)
    {
    }
}
