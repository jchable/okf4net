// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>
/// Error thrown when a concept id (or one of its segments) is malformed, or
/// when a file path cannot be resolved to/from a concept id. Port of the
/// Rust <c>ConceptIdError</c> (src/concept_id.rs:14).
/// </summary>
public sealed class ConceptIdException : OkfException
{
    public ConceptIdException(string message)
        : base(message)
    {
    }
}
