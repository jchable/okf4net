// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net;

/// <summary>
/// Errors raised when parsing a single OKF concept document. Port of the
/// <c>UnterminatedFrontmatter</c>, <c>FrontmatterNotMapping</c>, and
/// <c>InvalidYaml</c> variants of the Rust <c>DocumentError</c> enum
/// (error.rs:8-17); their <c>Display</c> messages (error.rs:19-34) become
/// this exception's <see cref="Exception.Message"/>.
/// </summary>
public sealed class DocumentParseException : OkfException
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public DocumentParseException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Error raised when a document fails <see cref="OkfDocument.Validate"/> or
/// <see cref="OkfDocument.ValidateConformance"/>. Port of the
/// <c>MissingKeys</c> variant of the Rust <c>DocumentError</c> enum
/// (error.rs:16), carrying the same keys that fed its <c>Display</c> message
/// (error.rs:29-31) as structured data via <see cref="MissingKeys"/>.
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
