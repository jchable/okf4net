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
    private const string NamespaceDeclarationNodeType = "namespace_declaration";
    private const string NamespaceKeywordNodeType = "namespace";
    private const string NameFieldName = "name";
    private const string BodyFieldName = "body";
    private const string AccessorsFieldName = "accessors";
    private const string ValueFieldName = "value";
    private const string ModifierNodeType = "modifier";

    /// <summary>
    /// Node types that, by this grammar, hold nothing but a C# identifier. A node of one of these
    /// types whose text is exactly <c>namespace</c> cannot come from valid source (the keyword is
    /// reserved, and its escaped form measures as the text <c>@namespace</c>), so it is evidence that
    /// error recovery re-lexed a real <c>namespace</c> keyword as a name -- see
    /// <see cref="ReadNamespaceContext"/>. Not a closed list: it holds the two carriers measured
    /// against the vendored grammar, and a shape that produces a third would be invisible until it is
    /// measured and added.
    /// </summary>
    private static readonly string[] IdentifierNodeTypes = ["identifier", "implicit_parameter"];

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

        // UNBOUNDED, and knowingly so. ExtractionLimits.Timeout does not reach this line and cannot
        // be made to on the shipped package: TreeSitter.DotNet 1.3.0's Parser exposes only
        // Parse(string) and Parse(string, Tree) -- no cancellation-token overload, no options
        // argument, no timeout property -- and its internal P/Invoke surface declares none of
        // ts_parser_parse_with_options, ts_parser_set_timeout_micros or
        // ts_parser_set_cancellation_flag, while Parser's native handle is internal, so the progress
        // callback the package's own native tree-sitter.dll exports is unreachable from here.
        // (Checked by reflecting over TreeSitter.dll's full type surface, not inferred from its
        // documentation.) Running this on a Task to impose a deadline would leak the thread and the
        // native call rather than stop them, so the guard that does exist is MaxFileBytes above, and
        // ExtractionLimits.Timeout is documented as the between-files deadline it actually is. If
        // the wrapper ever exposes the progress callback, that is the hook to wire a real per-parse
        // bound to -- and the place to revisit ILanguageExtractor.Extract's no-token signature.
        // ExtractionLimits.Timeout's doc comment records the CONDITION under which this trade
        // expires: it holds only while okfgen is interactive and local, so an operator's Ctrl-C is a
        // real bound. First non-interactive caller (CI, an MCP/agent tool, a scheduled run) and this
        // becomes an unstoppable hang; do not re-document it then, close it.
        using var tree = engine.Parser.Parse(source)!;

        var namespaceContext = ReadNamespaceContext(tree.RootNode);
        var symbols = ExtractSymbols(source, tree, engine.DeclarationQuery, profile, relativePath, namespaceContext);
        var sites = ExtractCallSites(source, tree, engine.CallQuery, relativePath, namespaceContext);

        // §2.3: code that fails to parse is not an error -- tree-sitter recovers around an ERROR
        // node and keeps every declaration outside the malformed region, so the file still counts as
        // (partially) extracted rather than skipped. This also covers every declaration
        // ReadNamespaceContext suppressed: suppression only ever runs on a tree that already reports
        // HasError (see that method), so it can never leave a file claiming Extracted while silently
        // holding declarations back.
        var status = tree.RootNode.HasError ? FileStatus.PartiallyExtracted : FileStatus.Extracted;

        return new ExtractionResult(symbols, sites, status);
    }

    /// <summary>
    /// What one parse can say about the C# file-scoped namespace (<c>namespace N;</c>) the
    /// declarations in a file sit under.
    /// </summary>
    /// <param name="FileScopedName">
    /// The declared file-scoped namespace, or <see langword="null"/> when the file declares none --
    /// the ordinary case for a block-namespace or top-level file, where <c>""</c> is a fact about the
    /// source rather than a lookup that failed.
    /// </param>
    /// <param name="SuppressFromIndex">
    /// The offset of the earliest evidence that this file declares a namespace this parse could not
    /// recover, or <see langword="null"/> when there is none. Every declaration STARTING at or after
    /// it is covered by that lost namespace (a file-scoped namespace runs to end of file), so its
    /// container is unknown and it is dropped; everything before it is untouched.
    /// </param>
    private readonly record struct NamespaceContext(string? FileScopedName, int? SuppressFromIndex);

    /// <summary>
    /// Reads the C# file-scoped namespace (<c>namespace N;</c>) every declaration in
    /// <paramref name="root"/>'s file sits under, and -- where that read fails -- how much of the
    /// file the failure actually covers.
    ///
    /// <para>
    /// The problem is not theoretical. A file-scoped namespace declaration is a SIBLING of the
    /// declarations it covers, so it is read by looking among the root's own children rather than by
    /// walking up from a declaration -- and when tree-sitter's error recovery moves or destroys it,
    /// that lookup finds nothing and is indistinguishable, to the caller, from a file that never had
    /// one. The container then comes out as <c>""</c>: not a crash, not a skip, but a confident claim
    /// that a type lives in the global namespace when the source says otherwise -- and two such files
    /// in one run collide on that same empty container.
    /// </para>
    ///
    /// <para><b>Recover before refusing.</b> The root-children lookup is only a fast path. When it
    /// finds nothing on a tree that reports <c>HasError</c>, the whole tree is searched for a
    /// <c>file_scoped_namespace_declaration</c> before any suppression is considered, because the
    /// declaration is sometimes merely REPARENTED rather than destroyed: <c>#if DEBUG</c> with no
    /// <c>#endif</c> above the namespace line puts the intact declaration under a <c>preproc_if</c>
    /// (measured), where the deep search finds it and the correct container <c>N</c> is emitted. That
    /// is strictly better than both the wrong <c>""</c> and a refusal. An earlier revision of this
    /// method asserted the opposite -- that searching deeper would find nothing -- on the strength of
    /// one probed shape; it was wrong, and the fix is this search.
    /// </para>
    ///
    /// <para><b>Suppress locally, never file-wide.</b> Where recovery genuinely fails, what is
    /// unknown is the container of the declarations the lost namespace would have covered -- which is
    /// everything from its line to end of file, and nothing above it. So this returns an OFFSET, not
    /// a verdict on the file: a block namespace closed above the damage keeps its declarations and
    /// their correct containers. Refusing the whole file (which this method's previous revision did)
    /// deleted work that was never wrong -- for two block namespaces with the second one's <c>}</c>
    /// missing, <c>[N]T</c> and <c>[N.T]M</c> were correct before that guard and gone after it.
    /// </para>
    ///
    /// <para><b>What the evidence can be, and what it cannot.</b> Two node-level signals, both
    /// measured against the vendored tree-sitter-c-sharp grammar:
    /// <list type="bullet">
    /// <item>an orphaned <c>namespace</c> KEYWORD node -- one whose parent is neither a
    /// <c>namespace_declaration</c> nor a <c>file_scoped_namespace_declaration</c>. This is what an
    /// unclosed <c>public class Leftover {</c> above the namespace line, or a merge conflict wrapping
    /// a <c>using</c> block, recovers to.</item>
    /// <item>a node that by grammar can only hold an identifier (<see cref="IdentifierNodeTypes"/>)
    /// whose text is exactly <c>namespace</c>. C# reserves <c>namespace</c>, and the escaped form is
    /// spelled <c>@namespace</c> -- which measures as node text <c>"@namespace"</c>, not
    /// <c>"namespace"</c> -- so such a node cannot come from valid source at all: it only appears when
    /// recovery has re-lexed a real <c>namespace</c> keyword as a name. <c>using System</c> with no
    /// semicolon reparses the keyword as a <c>variable_declarator</c>'s <c>identifier</c>; an
    /// unterminated block comment or a bare set of conflict markers reparses it as an
    /// <c>implicit_parameter</c>.</item>
    /// </list>
    /// Both are node-TYPE tests, deliberately: the text of a <c>comment</c> or a string literal is
    /// never re-lexed into an identifier, so neither can trip them.
    /// </para>
    ///
    /// <para><b>This does not close the class, and must not be read as if it did.</b> Some shapes
    /// leave NO node for the lost keyword at all -- <c>global using System</c> with no semicolon, and
    /// an unterminated string literal inside an assembly attribute, both measured -- because the
    /// bytes spelling <c>namespace N</c> land inside an <c>ERROR</c> node that exposes no child
    /// covering them. Nothing in the tree names them, so this walk is structurally blind to them and
    /// they still emit the wrong <c>""</c> container. Recovering those would mean re-lexing the raw
    /// text of error regions, which this method deliberately does not do; the line drawn here is that
    /// the evidence must be a node the grammar itself produced. Any shape reported as still-wrong
    /// belongs in <c>HostileInputTests</c> alongside the ones that are handled.
    /// </para>
    ///
    /// <para>
    /// The deep walk runs only when <paramref name="root"/> reports <c>HasError</c>: on every clean
    /// shape measured, every <c>namespace</c> keyword was already parented to a namespace node, so a
    /// clean tree has nothing to find. That early-out is also what keeps this off the
    /// empty-collection-expression grammar gap's blast radius -- that gap sets <c>HasError</c> on
    /// nearly every modern C# file in this codebase (see <c>HostileInputTests</c>), and a rule that
    /// distrusted the file-scoped name whenever <c>HasError</c> was set would drop most of a real
    /// repository. The walk is iterative rather than recursive because its depth is bounded by
    /// hostile input, not by <see cref="ExtractionLimits.MaxDepth"/>, which bounds directory nesting
    /// and not syntactic nesting.
    /// </para>
    /// </summary>
    private static NamespaceContext ReadNamespaceContext(Node root)
    {
        var declaration = root.Children.FirstOrDefault(c => c.Type == FileScopedNamespaceNodeType);

        if (declaration is null && root.HasError)
        {
            declaration = Descendants(root).FirstOrDefault(n => n.Type == FileScopedNamespaceNodeType);
        }

        if (declaration is not null)
        {
            var name = declaration.GetChildForField(NameFieldName)?.Text;

            // A declaration whose name did not survive (`namespace ;` recovers to exactly this shape)
            // is the same failure by a different route: the name reads as empty, and prepending an
            // empty segment produces a container with a leading dot rather than a real path. The
            // declaration's own offset is where its coverage starts, so it is also where suppression
            // starts.
            return string.IsNullOrEmpty(name)
                ? new NamespaceContext(null, declaration.StartIndex)
                : new NamespaceContext(name, null);
        }

        if (!root.HasError)
        {
            return new NamespaceContext(null, null);
        }

        int? suppressFrom = null;
        foreach (var node in Descendants(root))
        {
            if (IsLostNamespaceEvidence(node) && (suppressFrom is null || node.StartIndex < suppressFrom))
            {
                suppressFrom = node.StartIndex;
            }
        }

        return new NamespaceContext(null, suppressFrom);
    }

    /// <summary>
    /// Whether <paramref name="node"/> is evidence of a namespace declaration this parse lost. See
    /// <see cref="ReadNamespaceContext"/> for the measurement behind both arms and for the shapes
    /// neither arm can see.
    /// </summary>
    private static bool IsLostNamespaceEvidence(Node node) =>
        node.Type == NamespaceKeywordNodeType
            ? node.Parent?.Type is not (NamespaceDeclarationNodeType or FileScopedNamespaceNodeType)
            : Array.IndexOf(IdentifierNodeTypes, node.Type) >= 0
                && string.Equals(node.Text, NamespaceKeywordNodeType, StringComparison.Ordinal);

    /// <summary>
    /// Every node at or below <paramref name="root"/>, in no particular order. Iterative rather than
    /// recursive because its depth is bounded by hostile input, not by
    /// <see cref="ExtractionLimits.MaxDepth"/>.
    /// </summary>
    private static IEnumerable<Node> Descendants(Node root)
    {
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;

            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }
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
    /// all. What does get read is decoded strictly via <see cref="SourceDecoder.DecodeStrict"/> -- the
    /// shared decoder in <c>OkfProducer.Core</c>, not a private copy, because the Roslyn resolver has
    /// to decode the same file to exactly the same string for their offsets to be comparable at all
    /// (see that type's own summary): a byte sequence invalid in the selected encoding, which
    /// <see cref="UTF8Encoding"/>/<see cref="UnicodeEncoding"/> would otherwise silently replace with
    /// U+FFFD, is reported as <see cref="FileStatus.SkippedEncoding"/>
    /// instead of corrupting offsets silently. Returns <see langword="null"/> and sets
    /// <paramref name="source"/> to the decoded text when every guard passes; otherwise returns the
    /// <see cref="FileStatus"/> to report and sets <paramref name="source"/> to <see cref="string.Empty"/>.
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

        try
        {
            source = SourceDecoder.DecodeStrict(bytes);
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
        string source, Tree tree, Query declarationQuery, LanguageProfile profile, string relativePath, NamespaceContext namespaceContext)
    {
        var symbols = new List<SymbolFact>();

        foreach (var match in declarationQuery.Execute(tree.RootNode).Matches)
        {
            var decl = match.Captures.First(c => c.Name == "decl").Node;

            // Everything from the lost namespace's line onwards is covered by it, so its container is
            // unknown -- not empty -- and §2.3's rule is that a wrong identity is worse than a
            // missing one. A declaration that merely CONTAINS the damage (a leftover class body
            // holding a stray `namespace Q;`) starts above it and keeps the container its own
            // ancestors give it.
            if (namespaceContext.SuppressFromIndex is { } suppressFrom && decl.StartIndex >= suppressFrom)
            {
                continue;
            }

            var name = match.Captures.First(c => c.Name == "name").Node.Text;

            var kind = IsTypeDeclaration(decl.Type) ? SymbolKind.Type : SymbolKind.Member;
            var container = ComputeContainerPath(decl, namespaceContext.FileScopedName);
            var modifiersText = ComputeModifiersText(decl, kind);
            var visibility = profile.VisibilityOf(modifiersText, kind);
            var docComment = ExtractDocComment(decl, profile.DocCommentPrefix);

            // Computed once and used twice: the node the header stops at is exactly what
            // ComputeSignature cuts the signature text at, and exactly what SymbolFact.HeaderEndLine
            // reports. Two independent walks could disagree, and a disagreement would show up as a
            // permalink whose line span does not match the signature printed next to it.
            var bodyStart = HeaderEndNode(decl);
            var signature = ComputeSignature(decl, bodyStart);

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
                docComment)
            {
                // The line the body OPENS on, not the line before it: a brace on its own line is the
                // ordinary C# layout, and cutting the span above it would produce a permalink that
                // stops short of the declaration it names. Where there is no body at all
                // (`public record Foo(int X);`), the declaration's own last line is its header's.
                HeaderEndLine = (bodyStart?.StartPosition.Row ?? decl.EndPosition.Row) + 1,
            });
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
    private static List<CallSite> ExtractCallSites(string source, Tree tree, Query callQuery, string relativePath, NamespaceContext namespaceContext)
    {
        var sites = new List<CallSite>();

        foreach (var capture in callQuery.Execute(tree.RootNode).Captures)
        {
            var callee = capture.Node;
            var callerMember = FindNearestAncestor(callee, CallerMemberAncestorNodeTypes);

            // Suppressed on the same rule, and keyed off the CALLER's own offset rather than the
            // call's: CallerContainer is the field a lost namespace makes wrong, and it is the
            // caller's position that decides whether the lost namespace covers it. A call inside a
            // member declared above the damage keeps a container its ancestors still establish.
            var callerStart = (callerMember ?? callee).StartIndex;
            if (namespaceContext.SuppressFromIndex is { } suppressFrom && callerStart >= suppressFrom)
            {
                continue;
            }

            var callerContainer = callerMember is not null ? ComputeContainerPath(callerMember, namespaceContext.FileScopedName) : string.Empty;

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
    ///
    /// <para>
    /// A <c>file_scoped_namespace_declaration</c> met during the ancestor walk contributes no segment
    /// of its own, so the prepended name can never also be collected here and produce <c>N.N</c>.
    /// That is a structural guarantee of this method rather than a shape that was measured: on every
    /// shape probed the declaration was a sibling, but <see cref="ReadNamespaceContext"/> may now
    /// recover one from anywhere in the tree, and this walk is the only thing standing between a
    /// reparented one and a doubled segment.
    /// </para>
    /// </summary>
    private static string ComputeContainerPath(Node decl, string? fileScopedNamespaceName)
    {
        var segments = new List<string>();
        var current = decl.Parent;
        while (current is not null)
        {
            var nameField = current.Type == FileScopedNamespaceNodeType ? null : current.GetChildForField(NameFieldName);
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
    private static string ComputeSignature(Node decl, Node? end)
    {
        var headerLength = end is not null ? end.StartIndex - decl.StartIndex : decl.Text.Length;
        var header = decl.Text[..headerLength].TrimEnd();

        if (header.EndsWith(';'))
        {
            header = header[..^1].TrimEnd();
        }

        return CollapseWhitespace(header).Trim();
    }

    /// <summary>
    /// The node <paramref name="decl"/>'s header stops at -- its <c>body</c>, <c>accessors</c> or
    /// <c>value</c> field, in that precedence -- or <see langword="null"/> for a declaration that has
    /// none of the three (a field, a delegate, a body-less record). Split out of
    /// <see cref="ComputeSignature"/> so the signature text and
    /// <see cref="SymbolFact.HeaderEndLine"/> are cut at the same node by construction; see that
    /// property for why the line matters.
    /// </summary>
    private static Node? HeaderEndNode(Node decl) =>
        decl.GetChildForField(BodyFieldName)
        ?? decl.GetChildForField(AccessorsFieldName)
        ?? decl.GetChildForField(ValueFieldName);

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
