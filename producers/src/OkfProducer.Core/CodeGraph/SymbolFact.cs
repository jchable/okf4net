// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>The category of declaration a <see cref="SymbolFact"/> describes.</summary>
public enum SymbolKind
{
    /// <summary>A namespace, package, or module declaration.</summary>
    Namespace,

    /// <summary>A type declaration (class, interface, struct, enum, record, ...).</summary>
    Type,

    /// <summary>A member declaration (method, property, field, constructor, ...).</summary>
    Member,
}

/// <summary>The visibility of a declared symbol, collapsed across languages to three tiers.</summary>
public enum SymbolVisibility
{
    /// <summary>Visible outside its declaring assembly/package/module.</summary>
    Public,

    /// <summary>Visible within its declaring assembly/package/module, but not beyond it.</summary>
    Internal,

    /// <summary>Visible only within its declaring type or file.</summary>
    Private,
}

/// <summary>
/// One declared symbol (a namespace, type, or member) extracted from source. <see cref="StartOffset"/>
/// and <see cref="EndOffset"/> are UTF-8 byte offsets into <see cref="RelativePath"/>'s contents --
/// the one identity both tree-sitter (whose <c>Point.column</c> counts bytes) and Roslyn (whose
/// positions count UTF-16 units) can agree on; <see cref="StartLine"/>/<see cref="EndLine"/> are
/// carried only for display. See <c>Utf8Offsets</c> for the conversion between the two.
/// </summary>
public sealed record SymbolFact(
    SymbolKind Kind,
    string Language,
    string Container,
    string Name,
    string Signature,
    SymbolVisibility Visibility,
    string RelativePath,
    int StartOffset,
    int EndOffset,
    int StartLine,
    int EndLine,
    string? DocComment);
