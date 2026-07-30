// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>
/// Errors raised when parsing a single OKF concept document: an unterminated
/// frontmatter block, frontmatter that is not a YAML mapping, or invalid
/// YAML. The descriptive text becomes this exception's
/// <see cref="Exception.Message"/>.
/// </summary>
/// <remarks>Creates the exception with a descriptive message.</remarks>
public sealed class DocumentParseException(string message) : OkfException(message)
{
}

/// <summary>
/// Error raised when a document fails <see cref="OkfDocument.Validate"/> or
/// <see cref="OkfDocument.ValidateConformance"/>, carrying the required
/// frontmatter keys found missing or empty as structured data via
/// <see cref="MissingKeys"/>.
/// </summary>
public sealed class DocumentValidationException : OkfException
{
    /// <summary>The required frontmatter keys found missing or empty, in <c>Frontmatter.RequiredKeys</c> order.</summary>
    public IReadOnlyList<string> MissingKeys { get; }

    /// <summary>Creates the exception, listing the frontmatter keys that failed validation.</summary>
    public DocumentValidationException(string message, IReadOnlyList<string> missingKeys)
        : base(message)
    {
        MissingKeys = missingKeys;
    }
}
