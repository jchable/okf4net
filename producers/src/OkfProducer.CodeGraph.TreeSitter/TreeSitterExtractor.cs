// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OkfProducer.Core.CodeGraph;
using TreeSitter;

namespace OkfProducer.CodeGraph.TreeSitter;

/// <summary>
/// The single <see cref="ILanguageExtractor"/> implementation, driven entirely by a
/// <see cref="LanguageProfile"/>: it loads the profile's grammar, runs its declaration and call
/// queries, and turns every match into a <see cref="SymbolFact"/> or <see cref="CallSite"/>.
/// Adding a language to the producer means adding a profile (grammar name, two query strings, a
/// doc-comment prefix) -- not a new extractor class (§2's "one seam, one implementation" design).
/// </summary>
/// <remarks>
/// Not every piece of this class is language-neutral yet: attaching a doc comment (walking
/// <see cref="Node.PreviousSibling"/> for a leading run of <c>comment</c>-typed nodes) and reading a
/// container from a <c>name</c> field are conventions shared by nearly every tree-sitter grammar, but
/// C#'s file-scoped namespace form (<c>namespace N;</c>, a sibling of the declarations it covers
/// rather than their syntactic parent) and the caller-resolution walk's fixed lists of "type" and
/// "member" node-type names are C#-specific. <see cref="LanguageProfile"/>'s six fields (as shipped)
/// have no hook for a second language to override these; a Java/TypeScript/JavaScript profile would
/// need this class extended, not just a new <see cref="LanguageProfile"/> value.
/// </remarks>
public sealed class TreeSitterExtractor : ILanguageExtractor, IDisposable
{
    private const string CommentNodeType = "comment";
    private const string FileScopedNamespaceNodeType = "file_scoped_namespace_declaration";
    private const string NameFieldName = "name";
    private const string BodyFieldName = "body";
    private const string AccessorsFieldName = "accessors";
    private const string ValueFieldName = "value";
    private const string ModifierNodeType = "modifier";

    private static readonly string[] TypeDeclarationNodeTypes =
    [
        "class_declaration", "interface_declaration", "struct_declaration", "record_declaration", "enum_declaration",
    ];

    /// <summary>
    /// Ancestor node types <see cref="ExtractCallSites"/> stops at when walking up from a call site to
    /// find its caller. <c>field_declaration</c>/<c>event_field_declaration</c> (a call in a field or
    /// event-field initializer, e.g. <c>private readonly Foo _x = new Bar();</c>) are included even
    /// though neither has its own <c>name</c> field -- <see cref="ExtractCallSites"/> falls back to the
    /// nearest <see cref="VariableDeclaratorNodeTypes"/> ancestor for the name in that case, so a call
    /// in a multi-declarator statement (<c>public int a = Foo(), b = Bar();</c>) still attributes to
    /// the right declarator, not to the statement as a whole.
    /// </summary>
    private static readonly string[] CallerMemberAncestorNodeTypes =
    [
        "method_declaration", "constructor_declaration", "destructor_declaration", "property_declaration",
        "event_declaration", "delegate_declaration", "local_function_statement",
        "field_declaration", "event_field_declaration",
    ];

    private static readonly string[] VariableDeclaratorNodeTypes = ["variable_declarator"];

    // Keyed by LanguageProfile.Language (e.g. "csharp"), not by the LanguageProfile record itself:
    // LanguageProfile's record equality is reference-based over FileExtensions (an IReadOnlyList<string>
    // has no value equality), so two structurally identical profiles would miss the cache and each leak
    // a fresh native Language/Parser/Query set. This assumes at most one LanguageProfile per Language
    // string is ever passed to one TreeSitterExtractor instance -- true for how CodeGraphBuilder is
    // documented to use it (a profile list built once, not per file); it is not re-validated here.
    private readonly Dictionary<string, Engine> _engines = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile, ExtractionLimits limits)
    {
        var skipStatus = TryReadSource(relativePath, absolutePath, limits, out var source);
        if (skipStatus is not null)
        {
            return new ExtractionResult([], [], skipStatus.Value);
        }

        var engine = GetOrCreateEngine(profile);
        using var tree = engine.Parser.Parse(source)!;

        var fileScopedNamespaceName = tree.RootNode.Children
            .FirstOrDefault(c => c.Type == FileScopedNamespaceNodeType)
            ?.GetChildForField(NameFieldName)?.Text;

        var symbols = ExtractSymbols(source, tree, engine.DeclarationQuery, profile, relativePath, fileScopedNamespaceName);
        var sites = ExtractCallSites(source, tree, engine.CallQuery, relativePath, fileScopedNamespaceName);

        // §2.3: code that fails to parse is not an error -- tree-sitter recovers around an ERROR
        // node and keeps every declaration outside the malformed region, so the file still counts as
        // (partially) extracted rather than skipped.
        var status = tree.RootNode.HasError ? FileStatus.PartiallyExtracted : FileStatus.Extracted;

        return new ExtractionResult(symbols, sites, status);
    }

    /// <summary>
    /// Applies §2.3's hostile-input guards before a single byte is parsed. A reparse point (symlink
    /// or junction, detected via the public <see cref="FileSystemInfo.LinkTarget"/> rather than an
    /// internal seam this project cannot reach) is never followed -- checked both for
    /// <paramref name="absolutePath"/> itself and for every directory between it and the repository
    /// root, via <see cref="IsUnderReparsePoint"/>: a plain file reached only because one of its
    /// *ancestor* directories is a junction/symlink is exactly as unfollowed as a directly-symlinked
    /// file, and <see cref="CodeGraphBuilder"/>'s own walk (<see cref="Directory.EnumerateFiles"/>)
    /// does traverse through such a directory rather than stopping at it. A file over
    /// <paramref name="limits"/>'s <see cref="ExtractionLimits.MaxFileBytes"/> is rejected by its
    /// reported length alone -- it is never loaded into memory, let alone truncated to fit, since a
    /// partial parse would produce spans that point at the wrong code, worse than no extraction at
    /// all. What does get read is decoded strictly as UTF-8 (with or without a byte-order mark): any
    /// byte sequence <see cref="UTF8Encoding"/> would otherwise silently replace with U+FFFD is
    /// reported as <see cref="FileStatus.SkippedEncoding"/> instead of corrupting offsets silently.
    /// Returns <see langword="null"/> and sets <paramref name="source"/> to the decoded text when
    /// every guard passes; otherwise returns the <see cref="FileStatus"/> to report and sets
    /// <paramref name="source"/> to <see cref="string.Empty"/>.
    /// </summary>
    private static FileStatus? TryReadSource(string relativePath, string absolutePath, ExtractionLimits limits, out string source)
    {
        source = string.Empty;

        byte[] bytes;
        try
        {
            var fileInfo = new FileInfo(absolutePath);
            if (fileInfo.LinkTarget is not null || IsUnderReparsePoint(absolutePath, relativePath))
            {
                return FileStatus.SkippedSymlink;
            }

            if (!fileInfo.Exists)
            {
                return FileStatus.SkippedUnreadable;
            }

            if (fileInfo.Length > limits.MaxFileBytes)
            {
                return FileStatus.SkippedTooLarge;
            }

            bytes = File.ReadAllBytes(absolutePath);
        }
        catch (IOException)
        {
            return FileStatus.SkippedUnreadable;
        }
        catch (UnauthorizedAccessException)
        {
            return FileStatus.SkippedUnreadable;
        }

        var bomLength = HasUtf8Bom(bytes) ? 3 : 0;
        try
        {
            source = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, bomLength, bytes.Length - bomLength);
            return null;
        }
        catch (DecoderFallbackException)
        {
            source = string.Empty;
            return FileStatus.SkippedEncoding;
        }
    }

    /// <summary>
    /// Walks up from <paramref name="absolutePath"/>'s containing directory exactly as many levels as
    /// <paramref name="relativePath"/> has directory segments -- i.e. no further than the repository
    /// root this file was discovered under -- checking each level's own <see cref="FileSystemInfo.LinkTarget"/>.
    /// Bounding the walk by <paramref name="relativePath"/>'s own segment count avoids needing the
    /// repository root as a separate argument: it is exactly the number of directories between the
    /// root and this file, no more.
    /// </summary>
    private static bool IsUnderReparsePoint(string absolutePath, string relativePath)
    {
        var depth = relativePath.Count(c => c == '/');
        var directory = Path.GetDirectoryName(absolutePath);

        for (var i = 0; i < depth && directory is not null; i++)
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var engine in _engines.Values)
        {
            engine.Dispose();
        }

        _engines.Clear();
    }

    private Engine GetOrCreateEngine(LanguageProfile profile)
    {
        if (_engines.TryGetValue(profile.Language, out var existing))
        {
            return existing;
        }

        var language = new Language(profile.GrammarName);
        var parser = new Parser(language);
        var declarationQuery = language.CreateQuery(profile.DeclarationQuery);
        var callQuery = language.CreateQuery(profile.CallQuery);
        var engine = new Engine(language, parser, declarationQuery, callQuery);
        _engines[profile.Language] = engine;
        return engine;
    }

    private static List<SymbolFact> ExtractSymbols(
        string source, Tree tree, Query declarationQuery, LanguageProfile profile, string relativePath, string? fileScopedNamespaceName)
    {
        var symbols = new List<SymbolFact>();

        foreach (var match in declarationQuery.Execute(tree.RootNode).Matches)
        {
            var decl = match.Captures.First(c => c.Name == "decl").Node;
            var name = match.Captures.First(c => c.Name == "name").Node.Text;

            var kind = IsTypeDeclaration(decl.Type) ? SymbolKind.Type : SymbolKind.Member;
            var container = ComputeContainerPath(decl, fileScopedNamespaceName);
            var modifiersText = ComputeModifiersText(decl, kind);
            var visibility = profile.VisibilityOf(modifiersText, kind);
            var docComment = ExtractDocComment(decl, profile.DocCommentPrefix);
            var signature = ComputeSignature(decl);

            symbols.Add(new SymbolFact(
                kind,
                profile.Language,
                container,
                name,
                signature,
                visibility,
                relativePath,
                Utf8Offsets.ToUtf8(source, decl.StartIndex),
                Utf8Offsets.ToUtf8(source, decl.EndIndex),
                decl.StartPosition.Row + 1,
                decl.EndPosition.Row + 1,
                docComment));
        }

        return symbols;
    }

    /// <summary>
    /// <see cref="CallSite.CallerContainer"/> is deliberately computed with the *same*
    /// <see cref="ComputeContainerPath"/> call a caller's own <see cref="SymbolFact.Container"/> uses
    /// (rooted at the same nearest-enclosing-member node <see cref="CallSite.CallerName"/> is read
    /// from), not a shallower "just the type's bare name" walk: Task 8 joins a call site back to its
    /// caller's own concept by matching <c>(Container, Name)</c> against every <see cref="SymbolFact"/>,
    /// and a fully-qualified path is the only one guaranteed to match exactly one symbol -- a bare
    /// type name like <c>"T"</c> collides the moment two namespaces each hold a type named <c>T</c>,
    /// which is ordinary in a real repository.
    /// </summary>
    private static List<CallSite> ExtractCallSites(string source, Tree tree, Query callQuery, string relativePath, string? fileScopedNamespaceName)
    {
        var sites = new List<CallSite>();

        foreach (var capture in callQuery.Execute(tree.RootNode).Captures)
        {
            var callee = capture.Node;
            var callerMember = FindNearestAncestor(callee, CallerMemberAncestorNodeTypes);
            var callerContainer = callerMember is not null ? ComputeContainerPath(callerMember, fileScopedNamespaceName) : string.Empty;

            // A field/event-field declaration has no name field of its own (its declarators do), so
            // fall back to the nearest enclosing variable_declarator -- the specific name a call inside
            // that declarator's initializer should attribute to, correct even when the statement
            // declares more than one name (public int a = Foo(), b = Bar();).
            var callerName = callerMember?.GetChildForField(NameFieldName)?.Text
                ?? FindNearestAncestor(callee, VariableDeclaratorNodeTypes)?.GetChildForField(NameFieldName)?.Text
                ?? string.Empty;

            sites.Add(new CallSite(
                callerContainer,
                callerName,
                callee.Text,
                relativePath,
                Utf8Offsets.ToUtf8(source, callee.StartIndex)));
        }

        return sites;
    }

    private static bool IsTypeDeclaration(string nodeType) =>
        Array.IndexOf(TypeDeclarationNodeTypes, nodeType) >= 0;

    /// <summary>
    /// Builds the dotted <c>N.Outer.Inner</c> path above <paramref name="decl"/>: every ancestor
    /// that exposes a <c>name</c> field (a namespace, a type, or -- for a local function -- the
    /// method it's nested in) contributes one segment, outermost first. A C# file-scoped namespace
    /// (<c>namespace N;</c>) is a *sibling* of the declarations it covers, not their syntactic
    /// parent, so it never surfaces from the ancestor walk and must be prepended separately.
    /// </summary>
    private static string ComputeContainerPath(Node decl, string? fileScopedNamespaceName)
    {
        var segments = new List<string>();
        var current = decl.Parent;
        while (current is not null)
        {
            var nameField = current.GetChildForField(NameFieldName);
            if (nameField is not null)
            {
                segments.Insert(0, nameField.Text);
            }

            current = current.Parent;
        }

        if (fileScopedNamespaceName is not null)
        {
            segments.Insert(0, fileScopedNamespaceName);
        }

        return string.Join(".", segments);
    }

    /// <summary>
    /// Joins <paramref name="decl"/>'s direct <c>modifier</c> children into the space-separated text
    /// <see cref="LanguageProfile.VisibilityOf"/> expects. A member with no explicit access modifier
    /// declared directly inside an <c>interface</c> is implicitly <c>public</c> in C# -- a default
    /// <see cref="LanguageProfile.VisibilityOf"/> cannot apply on its own because it never sees the
    /// declaring type, so it is synthesized into the modifier text here.
    /// </summary>
    private static string ComputeModifiersText(Node decl, SymbolKind kind)
    {
        var modifiers = decl.Children.Where(c => c.Type == ModifierNodeType).Select(c => c.Text).ToList();

        if (kind == SymbolKind.Member
            && !modifiers.Any(m => m is "public" or "internal" or "protected" or "private")
            && FindNearestAncestor(decl, TypeDeclarationNodeTypes)?.Type == "interface_declaration")
        {
            modifiers.Insert(0, "public");
        }

        return string.Join(" ", modifiers);
    }

    /// <summary>
    /// A single-line header for <paramref name="decl"/>: everything up to the earliest of its
    /// <c>body</c> field (a method's block or arrow-expression clause), its <c>accessors</c> field (a
    /// property's <c>{ get; set; }</c> list), or its <c>value</c> field (an arrow-bodied property's
    /// <c>=&gt; expr</c>, or a plain property/auto-property's own initializer). A property has no
    /// <c>body</c> field at all -- it is <c>accessors</c> and/or <c>value</c> that carry its accessor
    /// list, its arrow implementation, or its initializer, and all three must be excluded from the
    /// signature the same way a method's block is. When an auto-property has both an accessor list
    /// and an initializer (<c>public int P { get; set; } = Init();</c>), <c>accessors</c> always
    /// starts first in source order, so truncating there drops both in one step. Declarations with
    /// none of the three (fields, delegates) keep the whole text with a trailing <c>;</c> dropped.
    /// </summary>
    private static string ComputeSignature(Node decl)
    {
        var end = decl.GetChildForField(BodyFieldName)
            ?? decl.GetChildForField(AccessorsFieldName)
            ?? decl.GetChildForField(ValueFieldName);
        var headerLength = end is not null ? end.StartIndex - decl.StartIndex : decl.Text.Length;
        var header = decl.Text[..headerLength].TrimEnd();

        if (header.EndsWith(';'))
        {
            header = header[..^1].TrimEnd();
        }

        return CollapseWhitespace(header).Trim();
    }

    /// <summary>
    /// Collects a contiguous run of <paramref name="prefix"/>-prefixed <c>comment</c> siblings
    /// immediately preceding <paramref name="decl"/> (each <c>///</c> line is its own sibling node in
    /// this grammar, so a multi-line doc comment is several nodes, not one), strips the prefix from
    /// each, and returns the <c>&lt;summary&gt;</c> element's content if present, or the joined text
    /// otherwise. A plain <c>//</c> comment (or a <c>////</c> divider, which C# does not treat as a
    /// doc comment despite starting with <paramref name="prefix"/>) stops the walk rather than being
    /// folded in.
    /// </summary>
    private static string? ExtractDocComment(Node decl, string prefix)
    {
        var lines = new List<string>();
        var sibling = decl.PreviousSibling;
        while (sibling is not null && sibling.Type == CommentNodeType && IsDocCommentLine(sibling.Text, prefix))
        {
            lines.Insert(0, StripDocCommentPrefix(sibling.Text, prefix));
            sibling = sibling.PreviousSibling;
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var joined = CollapseWhitespace(string.Join(" ", lines)).Trim();
        if (joined.Length == 0)
        {
            return null;
        }

        return ExtractXmlElement(joined, "summary") ?? joined;
    }

    private static bool IsDocCommentLine(string text, string prefix) =>
        text.StartsWith(prefix, StringComparison.Ordinal) && (text.Length == prefix.Length || text[prefix.Length] != '/');

    private static string StripDocCommentPrefix(string text, string prefix)
    {
        var rest = text[prefix.Length..];
        return rest.StartsWith(' ') ? rest[1..] : rest;
    }

    private static string? ExtractXmlElement(string text, string elementName)
    {
        var openTag = $"<{elementName}>";
        var closeTag = $"</{elementName}>";

        var contentStart = text.IndexOf(openTag, StringComparison.Ordinal);
        if (contentStart < 0)
        {
            return null;
        }

        contentStart += openTag.Length;
        var contentEnd = text.IndexOf(closeTag, contentStart, StringComparison.Ordinal);
        return contentEnd < 0 ? null : text[contentStart..contentEnd].Trim();
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasWhitespace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasWhitespace)
                {
                    builder.Append(' ');
                }

                lastWasWhitespace = true;
            }
            else
            {
                builder.Append(c);
                lastWasWhitespace = false;
            }
        }

        return builder.ToString();
    }

    private static Node? FindNearestAncestor(Node node, IReadOnlyList<string> nodeTypes)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (nodeTypes.Contains(current.Type, StringComparer.Ordinal))
            {
                return current;
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>The cached grammar, parser, and compiled queries for one <see cref="LanguageProfile"/>.</summary>
    private sealed record Engine(Language Language, Parser Parser, Query DeclarationQuery, Query CallQuery) : IDisposable
    {
        public void Dispose()
        {
            DeclarationQuery.Dispose();
            CallQuery.Dispose();
            Parser.Dispose();
            Language.Dispose();
        }
    }
}
