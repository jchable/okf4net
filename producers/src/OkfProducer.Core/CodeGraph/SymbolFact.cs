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
/// the one identity both extractors normalize to explicitly via <c>Utf8Offsets.ToUtf8</c>: Roslyn's
/// own positions count UTF-16 units, and so, in practice, do the ones the <c>TreeSitter.DotNet</c>
/// 1.3.0 binding hands back when parsing a .NET string (its public API exposes no raw tree-sitter
/// byte offset -- see <c>Utf8Offsets</c>' summary for the measured evidence). <see cref="StartLine"/>/
/// <see cref="EndLine"/> are carried only for display.
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
