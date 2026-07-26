// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Enumerates every way a <c>catalog.json</c> manifest can be rejected by
/// <see cref="CatalogManifestParser.TryParse"/>. This list is closed: no
/// additional codes are introduced ad hoc by the parser implementation.
/// </summary>
public enum CatalogDiagnosticCode
{
    /// <summary>The input was not syntactically valid JSON, or not a JSON object where one was required.</summary>
    ParseError,

    /// <summary>The manifest root object contains a property other than <c>version</c> or <c>sources</c>.</summary>
    UnknownRootProperty,

    /// <summary>A source entry contains a property other than <c>id</c>, <c>path</c>, <c>priority</c>, <c>enabled</c>, or <c>role</c>.</summary>
    UnknownSourceProperty,

    /// <summary>The root <c>version</c> property is missing, not a number, or not exactly <c>1</c>.</summary>
    WrongVersion,

    /// <summary>The root <c>sources</c> property is missing, not an array, or an empty array.</summary>
    EmptySources,

    /// <summary>Two or more source entries share the same <c>id</c>.</summary>
    DuplicateSourceId,

    /// <summary>A source's <c>id</c> is not a valid single concept-id segment (see <see cref="OKF4net.ConceptId.ValidateSegment"/>).</summary>
    InvalidSourceId,

    /// <summary>A source's <c>path</c> is missing, not a string, or empty.</summary>
    EmptyPath,

    /// <summary>A string value anywhere in the manifest contains an embedded NUL character (<c>'\0'</c>).</summary>
    EmbeddedNul,

    /// <summary>A source's <c>priority</c> is present but not an integer.</summary>
    MalformedPriority,

    /// <summary>A source's <c>enabled</c> is present but not a boolean.</summary>
    MalformedEnabled,

    /// <summary>A source's <c>role</c> is present but is not the string <c>"knowledge"</c>.</summary>
    IllegalRole,
}
