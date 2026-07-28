// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>
/// Error thrown when a concept id (or one of its segments) is malformed, or
/// when a file path cannot be resolved to/from a concept id.
/// </summary>
public sealed class ConceptIdException : OkfException
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public ConceptIdException(string message)
        : base(message)
    {
    }
}
