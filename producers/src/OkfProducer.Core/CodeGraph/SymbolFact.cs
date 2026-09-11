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
    string? DocComment)
{
    /// <summary>
    /// Separates a generic type's name from its arity in <see cref="Name"/> (<c>Holder`1</c>).
    ///
    /// <para><b>It lives here, in Core, because it is a contract BETWEEN the two engines</b> -- the
    /// same reason <c>Utf8Offsets</c> does. Both the tree-sitter extractor and the Roslyn resolver
    /// spell a generic type's name this way, and they join on that spelling; a copy in each that drifts
    /// does not lose a call, it credits it to another symbol.</para>
    ///
    /// <para><b>A backtick because it cannot occur in a C# identifier</b> -- not through <c>@</c>, not
    /// through a Unicode escape -- which is what makes the qualification injective. The first version
    /// used <c>_N</c>, chosen so a reader could tell "arity 1" from the registry's "second thing called
    /// Foo" (<c>-2</c>). Both characters are in the identifier alphabet, so a type genuinely named
    /// <c>Holder_1</c> and one named <c>Holder&lt;T&gt;</c> produced the same <see cref="Name"/>,
    /// grouped as one concept, and were emitted with both signatures under one description -- exactly
    /// the merge this qualification exists to prevent, reachable from legal C#. The backtick is also
    /// the CLR's own arity convention, and <c>ConceptId.Slugify</c> maps it to <c>-</c>, so the emitted
    /// id is <c>holder-1</c> while a real <c>Holder_1</c> stays <c>holder_1</c>.</para>
    /// </summary>
    public const char ArityMarker = '`';

    /// <summary>
    /// The line carrying the end of this declaration's <b>header</b> -- the opening brace of its body
    /// where it has one, the declaration's own last line where it does not -- or <see langword="null"/>
    /// when the extractor did not record one.
    ///
    /// <para><b>Why it exists, and why only a type's concept renders it.</b> A type declaration's span
    /// runs to its closing brace, so <i>any</i> edit inside the body -- adding a private helper, adding
    /// an overload, deleting a method -- moves its <see cref="EndLine"/> and rewrites the type's
    /// concept. That is churn caused by the edit's position rather than by anything the type declares,
    /// and it falsifies the design's own blast-radius promise that adding a private member changes no
    /// concept at all (§8.3). Emission therefore caps a <see cref="SymbolKind.Type"/>'s rendered span
    /// at this line; an edit <i>above</i> the type still moves it, which is correct, because the
    /// declaration genuinely moved.</para>
    ///
    /// <para>A <see cref="SymbolKind.Member"/> keeps its full <see cref="StartLine"/>..<see cref="EndLine"/>
    /// range: a member's span already covers only the member, so there is nothing to cap and a
    /// permalink to its whole body is the useful one.</para>
    ///
    /// <para>An <c>init</c> property with a <see langword="null"/> default rather than a positional
    /// parameter, so the many call sites that construct a <see cref="SymbolFact"/> with no syntax tree
    /// to read it from -- every test fixture in this solution -- keep compiling, and keep meaning "no
    /// header line recorded, so use the full span".</para>
    /// </summary>
    public int? HeaderEndLine { get; init; }

    /// <summary>
    /// The namespace part of <see cref="Container"/> -- a dotted prefix of it -- or
    /// <see langword="null"/> when the extractor did not record one.
    ///
    /// <para><b>What it separates, and why <see cref="Container"/> alone cannot.</b>
    /// <c>Container</c> is a flat dotted string, so a type nested inside <c>Bar</c> and a top-level
    /// type in the namespace <c>Foo.Bar</c> both report <c>"Foo.Bar"</c> -- two structurally different
    /// parents spelled identically. The consequence was measured: with a class <c>Bar</c> in namespace
    /// <c>Foo</c> and a class <c>Baz</c> in namespace <c>Foo.Bar</c>, <c>Baz</c> was emitted at
    /// <c>code/csharp/foo/bar/baz</c>, as though it were nested inside the type. §3.3 enumerated two
    /// residual collisions; this was a third.
    ///
    /// The extractor knows the difference -- it walks ancestors and sees a namespace node in one case
    /// and a type node in the other -- and the flattening threw it away. Recording where the namespace
    /// STOPS is enough to recover it: a group's parent is a namespace exactly when the parent's depth
    /// equals the namespace's.</para>
    ///
    /// <para>An <c>init</c> property defaulting to <see langword="null"/> for the same reason as
    /// <see cref="HeaderEndLine"/>: every fixture in this solution constructs a <see cref="SymbolFact"/>
    /// positionally with no syntax tree to read this from, and <see langword="null"/> means "not
    /// recorded", which the generator treats exactly as it behaved before this existed.</para>
    /// </summary>
    public string? ContainerNamespace { get; init; }
}
