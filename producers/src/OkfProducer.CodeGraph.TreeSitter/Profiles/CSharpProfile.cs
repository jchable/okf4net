// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.CodeGraph.TreeSitter.Profiles;

/// <summary>
/// The C# <see cref="LanguageProfile"/>: <see cref="LanguageProfile.GrammarName"/> is the identifier
/// <c>TreeSitter.Language</c>'s string constructor expects (it prefixes it with <c>tree-sitter-</c> and
/// loads <c>tree-sitter-c-sharp.dll</c> from the <c>TreeSitter.DotNet</c> package's native runtimes).
/// </summary>
public static class CSharpProfile
{
    /// <summary>
    /// Every declaration this producer extracts from C# source: the five type kinds (class,
    /// interface, struct, record, enum), the member kinds that carry their own <c>name</c> field
    /// (method, constructor, destructor, property, event, delegate, and -- the spike's remaining
    /// attachment gap -- <c>local_function_statement</c>), and fields (including <c>event</c>
    /// fields), whose <c>variable_declarator</c> children each produce one match sharing their
    /// enclosing <c>field_declaration</c>'s span (so <c>public int a, b;</c> yields two symbols with
    /// the same signature and offsets, one named <c>a</c> and one named <c>b</c>).
    ///
    /// Two things are deliberately, permanently out of scope for this query, not oversights:
    ///
    /// <list type="bullet">
    /// <item><b>Enum members are not emitted as their own symbols.</b> An <c>enum_member_declaration</c>
    /// is public API and a filter-by-visibility policy would otherwise wrongly drop it if it defaulted
    /// to <see cref="SymbolVisibility.Private"/> (it has no modifier syntax in C# at all, so there is
    /// no explicit-modifier signal <see cref="LanguageProfile.VisibilityOf"/> could read). Rather than
    /// invent a visibility default for a declaration kind that never carries one, the enum
    /// *type* is the concept; a later task lists its members inside that concept's own body instead
    /// of emitting one symbol per member.</item>
    /// <item><b>Indexers, operator overloads, and conversion operators are not extracted at all.</b>
    /// None of them has a <c>name</c> field in this grammar -- an indexer is written <c>this[...]</c>
    /// with no name field whatsoever; an operator's symbol, e.g. <c>+</c>, is an anonymous child
    /// immediately after the anonymous <c>operator</c> keyword node, not a field. Naming either
    /// requires bespoke, untested-by-any-brief-requirement logic (walk to the child after the
    /// keyword; synthesize something like <c>this[]</c>), so this profile accepts the coverage gap
    /// rather than add unverified naming rules.</item>
    /// </list>
    /// </summary>
    public const string DeclarationQuery = """
        (class_declaration name: (identifier) @name) @decl
        (interface_declaration name: (identifier) @name) @decl
        (struct_declaration name: (identifier) @name) @decl
        (record_declaration name: (identifier) @name) @decl
        (enum_declaration name: (identifier) @name) @decl
        (method_declaration name: (identifier) @name) @decl
        (constructor_declaration name: (identifier) @name) @decl
        (destructor_declaration name: (identifier) @name) @decl
        (property_declaration name: (identifier) @name) @decl
        (event_declaration name: (identifier) @name) @decl
        (delegate_declaration name: (identifier) @name) @decl
        (local_function_statement name: (identifier) @name) @decl
        (field_declaration (variable_declaration (variable_declarator name: (identifier) @name))) @decl
        (event_field_declaration (variable_declaration (variable_declarator name: (identifier) @name))) @decl
        """;

    /// <summary>
    /// Every call-expression shape this producer resolves a callee name from: a bare call
    /// (<c>Foo()</c>), a member-access call (<c>obj.Bar()</c>, <c>this.Bar()</c>,
    /// <c>Type.Static()</c>), a generic call (<c>Foo&lt;T&gt;()</c>), a generic member-access call
    /// (<c>obj.Bar&lt;T&gt;()</c>), and a null-conditional call (<c>obj?.Baz()</c>). The captured
    /// node is always the simple callee identifier, dropping any qualifier -- call sites are matched
    /// by name (§2's <c>NameMatchResolver</c> baseline; Roslyn resolves precisely later), so the
    /// qualifier is not part of this query's job.
    /// </summary>
    public const string CallQuery = """
        (invocation_expression function: (identifier) @callee)
        (invocation_expression function: (member_access_expression name: (identifier) @callee))
        (invocation_expression function: (generic_name (identifier) @callee))
        (invocation_expression function: (member_access_expression name: (generic_name (identifier) @callee)))
        (invocation_expression function: (conditional_access_expression (member_binding_expression name: (identifier) @callee)))
        """;

    /// <summary>The real C# <see cref="LanguageProfile"/>: <c>.cs</c> files, <c>///</c> doc comments.</summary>
    public static readonly LanguageProfile Instance = new(
        Language: "csharp",
        GrammarName: "c-sharp",
        DeclarationQuery: DeclarationQuery,
        CallQuery: CallQuery,
        DocCommentPrefix: "///",
        FileExtensions: [".cs"]);
}
