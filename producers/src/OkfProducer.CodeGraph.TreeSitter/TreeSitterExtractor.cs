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
    private const string DeclarationListNodeType = "declaration_list";
    private const string BlockNodeType = "block";
    private const string EndIfNodeType = "#endif";

    /// <summary>
    /// Node types that, by this grammar, hold nothing but a C# identifier. A node of one of these
    /// types whose text is exactly <c>namespace</c> is one of the two carriers a re-lexed
    /// <c>namespace</c> keyword was measured to land in -- see <see cref="ReadNamespaceContext"/>.
    ///
    /// <para><b>It is not a carrier only error recovery can produce.</b> A previous revision of this
    /// comment said exactly that, and it is false, measured: <c>public class Leftover { void Q() {
    /// namespace Wrong; } }</c> parses with <b>no ERROR node anywhere and <c>HasError</c> false on
    /// the whole tree</b>, as <c>local_declaration_statement -> variable_declaration -> identifier
    /// "namespace"</c>. The grammar produces the carrier from source it considers well formed. Only
    /// the <c>HasError</c> early-out kept the arm off such files, and the empty-collection-expression
    /// grammar gap lifts that early-out on nearly every modern C# file -- so a nested
    /// <c>namespace Q;</c> inside a method body was measured to suppress two later, entirely correct
    /// declarations in a file whose block namespace was intact. What bounds the arm now is
    /// <see cref="IsAtCompilationUnitLevel"/>, not an absolute about the grammar.
    ///
    /// The half of the old claim that DOES hold, and is still relied on: the escaped form measures as
    /// node text <c>"@namespace"</c>, so the exact-text comparison rejects <c>@namespace</c>.
    /// </para>
    ///
    /// Not a closed list: it holds the two carriers measured against the vendored grammar, and a
    /// shape that produces a third would be invisible until it is measured and added.
    /// </summary>
    private static readonly string[] IdentifierNodeTypes = ["identifier", "implicit_parameter"];

    /// <summary>
    /// The only node types measured to sit between the root and a <c>file_scoped_namespace_declaration</c>
    /// that can still govern the file. A file-scoped namespace is a member of the compilation unit;
    /// the one thing this grammar was measured to interpose is a preprocessor conditional
    /// (<c>#if DEBUG</c> / <c>#else</c> / <c>#elif</c> -- <c>#region</c> was measured NOT to wrap,
    /// leaving the declaration a root child). Anything else in the chain -- an <c>ERROR</c>, a
    /// <c>class_declaration</c>, a body -- means the declaration is nested inside something that
    /// cannot contain it, so it governs nothing and is refused.
    ///
    /// <para>Appearing in this list is NECESSARY but not SUFFICIENT: <see cref="CanGovernFile"/>
    /// additionally requires the conditional to have been left unclosed, because a properly closed
    /// <c>#if</c> is a branch whose selection depends on a preprocessor symbol this parse does not
    /// know. Do not read membership here as "this wrapper is transparent" -- the earlier revision of
    /// this comment said exactly that, and it was measured false with the real C# compiler; see
    /// <see cref="CanGovernFile"/> for the measurement.</para>
    ///
    /// Measured list, not a derived one: a fourth wrapper would be invisible until it is measured and
    /// added, and its cost is a refusal, not a wrong answer.
    /// </summary>
    private static readonly string[] PreprocessorWrapperNodeTypes = ["preproc_if", "preproc_else", "preproc_elif"];

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
    /// <param name="FileScopedFromIndex">
    /// The offset of the declaration <see cref="FileScopedName"/> was read off. A file-scoped
    /// namespace covers its own line to end of file and NOTHING ABOVE IT, so the name applies only to
    /// declarations starting at or after this offset -- the same rule
    /// <see cref="SuppressFromIndex"/> already applied to refusal, now applied to the answer as well.
    /// Meaningless when <see cref="FileScopedName"/> is <see langword="null"/>.
    /// </param>
    /// <param name="SuppressFromIndex">
    /// The offset of the earliest evidence that this file declares a namespace this parse could not
    /// recover, or <see langword="null"/> when there is none. Every declaration STARTING at or after
    /// it is covered by that lost namespace (a file-scoped namespace runs to end of file), so its
    /// container is unknown and it is dropped; everything before it is untouched.
    /// </param>
    private readonly record struct NamespaceContext(string? FileScopedName, int FileScopedFromIndex, int? SuppressFromIndex)
    {
        /// <summary>
        /// The file-scoped namespace covering a declaration that starts at
        /// <paramref name="startIndex"/>, or <see langword="null"/> when the declaration starts above
        /// the namespace line and is therefore not covered by it.
        /// </summary>
        public string? NameCovering(int startIndex) =>
            startIndex >= FileScopedFromIndex ? FileScopedName : null;
    }

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
    /// <para><b>Recover before refusing, but only from a declaration that could govern.</b> The
    /// root-children lookup is only a fast path. When it finds nothing on a tree that reports
    /// <c>HasError</c>, the whole tree is searched for a <c>file_scoped_namespace_declaration</c>
    /// before any suppression is considered, because the declaration is sometimes merely REPARENTED
    /// rather than destroyed: <c>#if DEBUG</c> with no <c>#endif</c> above the namespace line puts the
    /// intact declaration under a <c>preproc_if</c> (measured), where the deep search finds it and the
    /// correct container <c>N</c> is emitted. That is strictly better than both the wrong <c>""</c>
    /// and a refusal. An earlier revision of this method asserted the opposite -- that searching
    /// deeper would find nothing -- on the strength of one probed shape; it was wrong, and the fix is
    /// this search.
    ///
    /// <b>Its first revision was wrong in the other direction, and worse.</b> It took the FIRST
    /// declaration found anywhere in the tree and prepended it to EVERY declaration in the file,
    /// including declarations starting above it. Measured: a closed <c>namespace First { class A ... }</c>
    /// followed by <c>#if DEBUG</c> and <c>namespace Second;</c> emitted <c>[Second.First]A</c> -- a
    /// container naming no namespace that exists -- where the un-recovered code had emitted the
    /// correct <c>[First]A</c>. Worse, <see cref="Descendants"/> is a stack DFS in no particular
    /// order, so "first" meant last-in-source: <c>#if DEBUG namespace A; #else namespace B; #endif</c>
    /// -- compilable C# -- emitted <c>[B]T</c>, choosing a branch by traversal order rather than by
    /// anything semantic. Turning a correct container into a fabricated one is the exact failure this
    /// whole guard exists to prevent, so recovery is now bounded on both axes:
    /// <list type="bullet">
    /// <item><b>structurally</b> -- <see cref="CanGovernFile"/>: only a declaration reached through
    /// nothing but UNCLOSED preprocessor conditionals can govern. A declaration under an
    /// <c>ERROR</c>, inside an unclosed class or a method body, cannot govern anything; neither can
    /// one inside a properly closed <c>#if</c>, whose selection depends on a preprocessor symbol
    /// this parse does not know. Both are refused.</item>
    /// <item><b>positionally</b> -- the recovered name is scoped by
    /// <see cref="NamespaceContext.FileScopedFromIndex"/> exactly as suppression is scoped by
    /// <see cref="NamespaceContext.SuppressFromIndex"/>, so it covers its own line to end of file and
    /// nothing above it.</item>
    /// </list>
    /// And when the DEEP SEARCH finds more than one <c>file_scoped_namespace_declaration</c>, this
    /// REFUSES rather than picks: there is no rule here that can say which branch of a
    /// <c>#if</c>/<c>#else</c> the build selects, and inventing one by traversal order is how the
    /// fabricated containers above happened. A refused declaration is not merely ignored -- its
    /// offset becomes suppression evidence, since a namespace certainly starts there and this parse
    /// cannot say which.
    ///
    /// <b>That last sentence is about the deep search only, and deliberately not about the file.</b>
    /// A previous revision of this comment said "when more than one is IN THE TREE", which is false:
    /// the root-children fast path above returns on the first match without counting anything, so
    /// <c>namespace A;</c> followed by <c>#if DEBUG namespace B; #endif</c> -- two declarations in
    /// the tree -- yields <c>[A]T</c> (measured), and is meant to. A root-level declaration is not a
    /// candidate among others: C# requires it to precede every other member (<c>CS8956</c>), so it
    /// governs whatever follows it whatever else the tree holds. The refusal rule exists for the
    /// case where the fast path already failed and the candidates are all found by searching.
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
    /// whose text is exactly <c>namespace</c>. <c>using System</c> with no semicolon reparses the
    /// keyword as a <c>variable_declarator</c>'s <c>identifier</c>; an unterminated block comment or a
    /// bare set of conflict markers reparses it as an <c>implicit_parameter</c>. The escaped form
    /// <c>@namespace</c> measures as node text <c>"@namespace"</c>, so the exact-text comparison
    /// rejects it.</item>
    /// </list>
    /// Both are node-TYPE tests, deliberately: the text of a <c>comment</c> or a string literal is
    /// never re-lexed into an identifier, so neither can trip them.
    /// </para>
    ///
    /// <para><b>Both arms are bounded to compilation-unit level, because the identifier arm's old
    /// safety premise was false.</b> That premise was that an identifier reading exactly
    /// <c>namespace</c> "cannot come from valid source at all". Measured, it can:
    /// <c>public class Leftover { void Q() { namespace Wrong; } }</c> parses with <c>HasError</c>
    /// FALSE on the whole tree and produces the carrier with no recovery involved at all. Only the
    /// <c>HasError</c> early-out below kept the arm off such files, and the
    /// empty-collection-expression grammar gap lifts that early-out on nearly every modern C# file --
    /// so the arm fired where this method's own rule says it must not. Measured cost: an intact
    /// <c>namespace N { class T { void M() { namespace Q; } void Use() ... void Take(int[] a) ... } }</c>
    /// emitted <c>[N]T, [N.T]M</c> and DROPPED <c>Use</c> and <c>Take</c> -- two declarations whose
    /// containers were correct, in a file where nothing was lost.
    ///
    /// So evidence now counts only where a <c>file_scoped_namespace_declaration</c> could actually
    /// have stood: at compilation-unit level, with no <c>declaration_list</c> (a type or namespace
    /// body) and no <c>block</c> (a statement body) anywhere in its ancestor chain -- see
    /// <see cref="IsAtCompilationUnitLevel"/>. The bound is applied to BOTH arms, not just the one
    /// measured to misfire: every keyword-arm shape measured sits under an <c>ERROR</c> or directly
    /// under an unclosed <c>class_declaration</c> (never under its <c>declaration_list</c>, precisely
    /// because the class never closed), so the bound costs the keyword arm nothing, and a rule that
    /// held for one arm and not the other would be a rule about carriers rather than about where a
    /// namespace can be declared.
    /// </para>
    ///
    /// <para><b>Recovery is fragile, and its fragility is not a gradient.</b> It depends on the
    /// declaration's ancestor chain being nothing but unclosed conditionals, and that chain is
    /// decided by the whole file, not by the region around the declaration. Measured:
    /// <c>#if DEBUG / namespace N; / public class T { void M() {} }</c> recovers <c>[N]T, [N.T]M</c>,
    /// but appending an unrelated, much later <c>using System</c> with no semicolon reparents the
    /// <c>preproc_if</c> under an <c>ERROR</c>, and the same file then yields NOTHING -- two correct
    /// declarations lost to damage that touched neither of them. That is the safe direction (a
    /// missing identity, not a wrong one) and it is the same trade the rest of this method makes, but
    /// it is a real cost and it is recorded here rather than left for the next reader to rediscover.
    /// Recovery is a bonus on a narrow set of shapes, not a guarantee about damaged files.</para>
    ///
    /// <para><b>A confident wrong answer survives ABOVE this method, on the clean-parse path.</b>
    /// <c>#if DEBUG / namespace A; / #endif / public class T ...</c> is valid C# whose container
    /// depends on <c>DEBUG</c>. When something else in the file sets <c>HasError</c>, the deep search
    /// reaches it and <see cref="CanGovernFile"/> now refuses it, so its declarations are dropped.
    /// When the file parses cleanly, the <c>!HasError</c> early-out below returns before any of that
    /// and the declarations come out labelled <c>""</c> -- a confident global-namespace claim on a
    /// file that may well declare <c>A</c>. So the answer for one source still flips on unrelated
    /// content elsewhere in it (measured: adding an empty collection expression <c>[]</c> is enough).
    /// Closing that means loosening the <c>!HasError</c> early-out, which is the one thing keeping
    /// this whole walk off the <c>[]</c>-grammar-gap population described below; it is a separate
    /// decision, not an oversight of this one.</para>
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
        var rootChild = root.Children.FirstOrDefault(c => c.Type == FileScopedNamespaceNodeType);
        if (rootChild is not null)
        {
            return ReadDeclaration(rootChild);
        }

        if (!root.HasError)
        {
            return new NamespaceContext(null, 0, null);
        }

        // One pass, accumulating the two kinds of offset SEPARATELY. They are separate because they
        // answer different questions and only one of them survives a recovery: evidence is a
        // namespace this parse could not name at all, while a declaration offset is a namespace it
        // CAN name and may be about to return. A previous revision kept a single running minimum over
        // both and then tried to subtract the recovered declaration back out of it by comparing
        // against that minimum -- which made the subtraction fire only when the recovered declaration
        // WAS the minimum, i.e. discarded every piece of evidence lying below it. Since a recovered
        // file-scoped namespace sits at the top of the file, it is nearly always that minimum, so the
        // merge below was dead for exactly the shapes it was written for (measured: three of them --
        // `using System` with no semicolon, an unterminated block comment, and conflict markers, each
        // below a namespace recovered from an unclosed `#if`, all three emitting the second
        // namespace's types under the FIRST namespace's name). Two accumulators make the question
        // "is there other evidence?" answerable directly instead of by arithmetic on a merged
        // minimum.
        Node? onlyDeclaration = null;
        var declarationCount = 0;
        int? evidenceFrom = null;
        int? declarationFrom = null;
        foreach (var node in Descendants(root))
        {
            if (node.Type == FileScopedNamespaceNodeType)
            {
                declarationCount++;
                onlyDeclaration = node;
                declarationFrom = Earliest(declarationFrom, node.StartIndex);
            }
            else if (IsLostNamespaceEvidence(node))
            {
                evidenceFrom = Earliest(evidenceFrom, node.StartIndex);
            }
        }

        if (declarationCount == 1 && CanGovernFile(onlyDeclaration!))
        {
            // Evidence of a SECOND lost namespace, elsewhere in the same file, is not cancelled by
            // having recovered this one -- and unlike the revision described above, this merge is
            // now exercised: the three shapes named there reach it, and the declarations below the
            // second loss are dropped rather than labelled with the first namespace's name.
            var recovered = ReadDeclaration(onlyDeclaration!);
            return recovered with
            {
                SuppressFromIndex = Earliest(recovered.SuppressFromIndex, evidenceFrom),
            };
        }

        // Refusing is not silent: a refused declaration's own offset is suppression evidence too,
        // since a namespace certainly starts there and this parse cannot say which.
        return new NamespaceContext(null, 0, Earliest(evidenceFrom, declarationFrom));
    }

    /// <summary>
    /// Reads a <c>file_scoped_namespace_declaration</c> into a <see cref="NamespaceContext"/>, scoped
    /// to its own offset: a file-scoped namespace covers its own line to end of file and nothing
    /// above it.
    /// </summary>
    private static NamespaceContext ReadDeclaration(Node declaration)
    {
        var name = declaration.GetChildForField(NameFieldName)?.Text;

        // A declaration whose name did not survive (`namespace ;` recovers to exactly this shape) is
        // the same failure by a different route: the name reads as empty, and prepending an empty
        // segment produces a container with a leading dot rather than a real path. The declaration's
        // own offset is where its coverage starts, so it is also where suppression starts.
        return string.IsNullOrEmpty(name)
            ? new NamespaceContext(null, 0, declaration.StartIndex)
            : new NamespaceContext(name, declaration.StartIndex, null);
    }

    /// <summary>The earlier of two offsets, treating <see langword="null"/> as "no offset".</summary>
    private static int? Earliest(int? left, int? right) =>
        left is null ? right : right is null ? left : Math.Min(left.Value, right.Value);

    /// <summary>
    /// Whether a <c>file_scoped_namespace_declaration</c> found by the deep search is in a position
    /// that can govern the file at all. Two conditions, both necessary.
    ///
    /// <para><b>Every ancestor up to the root is a preprocessor conditional.</b> A file-scoped
    /// namespace is a member of the compilation unit, so a declaration reached through an
    /// <c>ERROR</c>, a type declaration or a body is nested inside something that cannot contain it
    /// -- it governs nothing, and treating it as the file's namespace is how <c>[Second.First]A</c>
    /// and <c>[Deep]T</c> were fabricated.</para>
    ///
    /// <para><b>And every one of those conditionals was left UNCLOSED.</b> A previous revision
    /// stopped at the first condition, on the stated rationale that a preprocessor conditional
    /// "wraps what it guards without changing its scope". That is a bet on a preprocessor symbol
    /// dressed as a structural fact, and it is false. Measured, with the real C# compiler, on
    /// <c>#if DEBUG / namespace A; / #endif / public class T ...</c>:
    /// <list type="bullet">
    /// <item>with <c>DEBUG</c> undefined -- the default of any Release build -- a second file
    /// referencing an unqualified <c>T</c> at global scope COMPILES, so <c>T</c> is in the global
    /// namespace;</item>
    /// <item>with <c>DEBUG</c> defined, that same reference fails with <c>CS0246</c>, because
    /// <c>T</c> is now <c>A.T</c>.</item>
    /// </list>
    /// The file is valid C# either way and its answer depends entirely on a symbol this parse does
    /// not know, so the old rule confidently emitted <c>[A]T, [A.T]M, [A.T]Use</c> for a file whose
    /// default build puts <c>T</c> nowhere near <c>A</c>. This is the same ignorance
    /// <see cref="ReadNamespaceContext"/> already refuses to guess through when a <c>#if</c>/<c>#else</c>
    /// offers two namespaces; with one branch the ignorance is identical and only the confidence
    /// differed.
    ///
    /// An UNCLOSED conditional is a different animal, and that is why the recovery this method
    /// exists for survives the narrowing. <c>#if DEBUG</c> with no <c>#endif</c> is not a branch the
    /// build might select -- it is <c>CS1027</c> ("#endif directive expected"), measured in BOTH
    /// configurations, so there is no build of that file at all. Nothing was conditionally compiled;
    /// the declaration was merely REPARENTED by error recovery, which is precisely the case
    /// recovery was added for. So the closed form is refused and the unclosed form is recovered.
    /// </para>
    ///
    /// <para><b>Why <c>IsMissing</c> and not an end-of-file comparison.</b> The obvious spelling of
    /// "unclosed" -- the wrapper extends to the end of the file -- was measured and does NOT work:
    /// this grammar ends a <c>preproc_if</c> BEFORE the file's trailing newline, so row 7 of the
    /// hostile-shape table measures <c>preproc_if [0,67)</c> against a <c>compilation_unit</c> ending
    /// at 68, and an <c>EndIndex >= root.EndIndex</c> test would refuse the one shape recovery
    /// exists for. What the grammar does instead is synthesize the missing terminator: an unclosed
    /// <c>#if</c> still gets an <c>#endif</c> child, zero-width and flagged <c>IsMissing</c>
    /// (measured on rows 7, R1a, R1g). Asking whether a REAL <c>#endif</c> is present is therefore
    /// both exact and a direct statement of the question.
    /// </para>
    ///
    /// <para>A <c>preproc_else</c>/<c>preproc_elif</c> carries no <c>#endif</c> child of its own --
    /// the terminator belongs to the enclosing <c>preproc_if</c> (measured) -- so it passes this test
    /// vacuously and the enclosing <c>preproc_if</c>, reached on the next turn of the same loop, is
    /// what actually decides. See <see cref="ReadNamespaceContext"/>.</para>
    /// </summary>
    private static bool CanGovernFile(Node declaration)
    {
        for (var current = declaration.Parent; current?.Parent is not null; current = current.Parent)
        {
            if (Array.IndexOf(PreprocessorWrapperNodeTypes, current.Type) < 0 || WasClosed(current))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="wrapper"/>'s preprocessor region was actually closed in the source: it
    /// has an <c>#endif</c> child that the grammar read rather than synthesized. See
    /// <see cref="CanGovernFile"/> for why a synthesized (<c>IsMissing</c>) terminator is the exact
    /// test and an end-of-file comparison is not.
    /// </summary>
    private static bool WasClosed(Node wrapper) =>
        wrapper.Children.Any(c => c.Type == EndIfNodeType && !c.IsMissing);

    /// <summary>
    /// Whether <paramref name="node"/> sits where a <c>file_scoped_namespace_declaration</c> could
    /// have stood: at compilation-unit level, i.e. with no <c>declaration_list</c> (a type or
    /// namespace body) and no <c>block</c> (a statement body) anywhere above it. Evidence found
    /// inside either is evidence about a nested statement, not about a lost file-scoped namespace --
    /// see <see cref="ReadNamespaceContext"/> for the measurement that forced this bound.
    /// </summary>
    private static bool IsAtCompilationUnitLevel(Node node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current.Type is DeclarationListNodeType or BlockNodeType)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="node"/> is evidence of a namespace declaration this parse lost. See
    /// <see cref="ReadNamespaceContext"/> for the measurement behind both arms, the bound both are
    /// held to, and the shapes neither arm can see.
    /// </summary>
    private static bool IsLostNamespaceEvidence(Node node)
    {
        var carriesTheKeyword = node.Type == NamespaceKeywordNodeType
            ? node.Parent?.Type is not (NamespaceDeclarationNodeType or FileScopedNamespaceNodeType)
            : Array.IndexOf(IdentifierNodeTypes, node.Type) >= 0
                && string.Equals(node.Text, NamespaceKeywordNodeType, StringComparison.Ordinal);

        return carriesTheKeyword && IsAtCompilationUnitLevel(node);
    }

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
            var container = ComputeContainerPath(decl, namespaceContext.NameCovering(decl.StartIndex));
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

            var callerContainer = callerMember is not null
                ? ComputeContainerPath(callerMember, namespaceContext.NameCovering(callerStart))
                : string.Empty;

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
