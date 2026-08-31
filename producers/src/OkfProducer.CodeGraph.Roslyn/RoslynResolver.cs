// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.CodeGraph.Roslyn;

/// <summary>
/// The exact C# resolver (2.1): compiles the repository's own projects with Roslyn and settles every
/// call site the name-matching baseline cannot -- which is most of the interesting ones, since a
/// spike measured 38-39% of internal call edges as inter-type ambiguous (<c>Equals</c> across seven
/// types, <c>Get</c> across three) and <see cref="NameMatchResolver"/> deliberately refuses to guess
/// among them.
///
/// <para><b>Offsets.</b> A <see cref="CallSite"/>'s identity is a UTF-8 byte offset; Roslyn's
/// positions are UTF-16 code units. Every callee position this resolver reads therefore goes through
/// <see cref="Utf8Offsets.ToUtf8"/> before it is matched, exactly as <c>TreeSitterExtractor</c> does
/// on its side. That conversion is not redundant just because both engines currently hand back UTF-16
/// -- read <see cref="Utf8Offsets"/>' own summary for why that agreement is an artefact of how the
/// tree-sitter binding is fed a .NET string rather than a contract, and why the failure mode if it
/// ever changes is a call credited to the <i>wrong</i> symbol rather than to none. The two sides also
/// convert against the same text: both decode the file's bytes through
/// <see cref="SourceDecoder.DecodeStrict"/>, never through a convenience reader that might strip a BOM
/// or normalise line endings differently.</para>
///
/// <para><b>Degradation.</b> A project that cannot be compiled cleanly is reported unavailable and its
/// files are not <see cref="Owns"/>ed, so <see cref="NameMatchResolver"/>'s baseline stands for them
/// untouched. This resolver never resolves from a compilation that has errors. <see cref="Projects"/>,
/// <see cref="IsAvailable"/> and <see cref="IsComplete"/> are what let a caller distinguish "ran, and
/// resolved nothing" from "could not run" -- the difference between a repository with no internal
/// calls and one whose graph should not be pruned against.</para>
///
/// <para><b>Not caught, by design.</b> An unknown <c>LangVersion</c> throws
/// <see cref="InvalidOperationException"/> out of <see cref="Create"/> rather than degrading to an
/// unavailable project (correction 3): the whole hazard there is a silent change of parse semantics,
/// and burying it in a per-project status line is exactly the silence being avoided.</para>
///
/// <para><b>Known limit: Roslyn source generators do not run.</b> The MSBuild query returns the
/// <i>files</i> the SDK generates (correction 1 is exactly about getting them), but a Roslyn source
/// generator runs inside the compiler and contributes no <c>Compile</c> item at all. A project whose
/// source references generated members therefore does not compile here, and this resolver reports it
/// <see cref="RoslynProjectAvailability.CompilationHadErrors"/> and declines to resolve from it.
/// Measured on this repository: <c>src/OKF4net.Cli</c> uses <c>System.Text.Json</c>'s generator and
/// produces six errors on the members it generates, while the other seven <c>src/</c> projects compile
/// clean. Running generators would mean loading and executing analyzer assemblies the scanned
/// repository chooses -- arbitrary code, from the very input this producer treats as untrusted
/// elsewhere -- so it is a deliberate, separately-decidable step, not an oversight. The behaviour
/// without it is the safe one: such a project degrades to the name-matching baseline and
/// <see cref="IsComplete"/> says so, which is what a pruning consumer must see.</para>
/// </summary>
public sealed class RoslynResolver : ISymbolResolver
{
    private readonly IReadOnlyDictionary<string, OwnedFile> _ownedFiles;

    // Built once per file on first use rather than for every compiled file up front: converting each
    // callee offset costs a pass over the file's prefix, and most files in a repository hold no call
    // site this run actually asks about.
    private readonly Dictionary<string, IReadOnlyDictionary<int, CalleeMatch>> _indexCache;

    private RoslynResolver(IReadOnlyList<RoslynProjectReport> projects, IReadOnlyDictionary<string, OwnedFile> ownedFiles)
    {
        Projects = projects;
        _ownedFiles = ownedFiles;
        _indexCache = new Dictionary<string, IReadOnlyDictionary<int, CalleeMatch>>(PathComparer);
    }

    /// <summary>
    /// Every project this resolver tried to compile -- those it was asked for and the in-repository
    /// projects they reference -- with the outcome for each, sorted by path (<see cref="StringComparer.Ordinal"/>).
    /// </summary>
    public IReadOnlyList<RoslynProjectReport> Projects { get; }

    /// <summary>Whether at least one project compiled, i.e. whether this resolver can settle anything at all.</summary>
    public bool IsAvailable => Projects.Any(p => p.Availability == RoslynProjectAvailability.Compiled);

    /// <summary>
    /// Whether <i>every</i> project compiled. <see langword="false"/> means some C# in the repository
    /// was resolved by name alone, so a consumer that deletes concepts on the strength of this graph
    /// (Task 11's pruning) is working from an incomplete picture and must not.
    /// </summary>
    public bool IsComplete => Projects.All(p => p.Availability == RoslynProjectAvailability.Compiled);

    /// <summary>
    /// Compiles <paramref name="projectPaths"/>, plus every project they reference that lives under
    /// <paramref name="repositoryPath"/>, and returns a resolver over whichever of them came out clean.
    ///
    /// <para>
    /// Pulling in referenced projects transitively is what makes correction 2 work: MSBuild resolves a
    /// <c>ProjectReference</c> to a <c>bin/</c> assembly that exists only after a build, so a caller
    /// naming one project would otherwise silently require the whole repository to have been built.
    /// Compiling those referenced projects here and passing them as <c>CompilationReference</c>s means
    /// a merely-restored checkout resolves exactly the same. A project outside the repository is left
    /// to its resolved assembly on disk, which is the correct treatment: its sources are not ours to
    /// compile and its symbols are not concepts this producer will emit.
    /// </para>
    /// </summary>
    /// <param name="repositoryPath">The repository root that <see cref="CallSite.RelativePath"/> values are relative to.</param>
    /// <param name="projectPaths">The <c>.csproj</c> files to compile. Order does not affect the result.</param>
    /// <exception cref="InvalidOperationException">
    /// A project declares a <c>LangVersion</c> this Roslyn build does not know. See the class summary:
    /// this is the one failure deliberately not degraded into <see cref="Projects"/>.
    /// </exception>
    public static RoslynResolver Create(string repositoryPath, IReadOnlyList<string> projectPaths)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositoryPath);
        ArgumentNullException.ThrowIfNull(projectPaths);

        var repositoryRoot = Path.GetFullPath(repositoryPath);
        var queried = QueryProjectClosure(repositoryRoot, projectPaths, out var reports);
        var compiled = CompileInDependencyOrder(queried, reports);

        var ownedFiles = new Dictionary<string, OwnedFile>(PathComparer);
        // Ordinal by project path so that, when two projects both compile the same file (a linked
        // source file, or one shared by two projects), which compilation wins is fixed rather than
        // whichever the query happened to reach first.
        foreach (var projectPath in compiled.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            var compilation = compiled[projectPath];
            foreach (var tree in compilation.SyntaxTrees)
            {
                var relativePath = RelativeToRepository(repositoryRoot, tree.FilePath);
                if (relativePath is not null)
                {
                    ownedFiles.TryAdd(relativePath, new OwnedFile(compilation, tree));
                }
            }
        }

        return new RoslynResolver(
            reports.Values.OrderBy(r => r.ProjectPath, StringComparer.Ordinal).ToList(),
            ownedFiles);
    }

    /// <inheritdoc />
    /// <remarks>
    /// True only for a file belonging to a project that compiled with zero errors. A file whose
    /// project failed is deliberately <i>not</i> owned, which is the whole degradation mechanism:
    /// <c>CodeGraphBuilder</c> then never asks this resolver about it, and the baseline verdict
    /// <see cref="NameMatchResolver"/> already produced stands.
    /// </remarks>
    public bool Owns(string relativePath) => _ownedFiles.ContainsKey(relativePath);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Returns a verdict only for the sites it could <i>attach</i>: a site whose
    /// <c>(RelativePath, Offset)</c> lands on a callee identifier Roslyn also sees, carrying the same
    /// name. A site it cannot attach is omitted rather than returned as
    /// <see cref="EdgeConfidence.Unresolved"/>, so the earlier resolver's verdict survives instead of
    /// being overwritten by this one's ignorance -- and so the attachment rate is directly observable
    /// as <c>result.Count / sites.Count</c>, which is what a test can hold to a floor.
    /// </para>
    /// <para>
    /// An attached site whose target Roslyn binds to a declaration <i>outside</i> the repository (the
    /// BCL, a NuGet package) is returned as <see cref="EdgeConfidence.Unresolved"/>, and that is a
    /// gain, not a loss: it retracts the baseline's name-only guess. If a repository happens to
    /// declare exactly one method called <c>Format</c>, <see cref="NameMatchResolver"/> links every
    /// <c>string.Format</c> call to it; Roslyn knows better and says so.
    /// </para>
    /// <para><paramref name="symbols"/> is unused. This resolver reads the compilation's own symbol
    /// table, which is strictly better information than the extracted facts; <c>CodeGraphBuilder</c>
    /// already drops or degrades any edge naming a symbol absent from the graph, so re-filtering here
    /// would only duplicate that.</para>
    /// </remarks>
    public IReadOnlyList<ResolvedEdge> Resolve(IReadOnlyList<CallSite> sites, IReadOnlyList<SymbolFact> symbols)
    {
        ArgumentNullException.ThrowIfNull(sites);

        var edges = new List<ResolvedEdge>();
        foreach (var site in sites)
        {
            if (!TryGetIndex(site.RelativePath, out var index)
                || !index.TryGetValue(site.Offset, out var match))
            {
                continue;
            }

            // The name guard. If the offsets are aligned this can never fire, because the site's
            // offset IS the start of that identifier -- which is exactly why it is worth checking:
            // the day it fires, the two engines have drifted apart, and turning that into a missing
            // edge rather than a confident wrong one is the whole point of 2.1. It also keeps the
            // attachment-rate test honest, since a systematic drift collapses the rate instead of
            // quietly re-pointing every call.
            if (!string.Equals(match.CalleeName, site.CalledName, StringComparison.Ordinal))
            {
                continue;
            }

            edges.Add(match.TargetContainer is null || match.TargetName is null
                ? new ResolvedEdge(site, TargetContainer: null, TargetName: null, EdgeConfidence.Unresolved)
                : new ResolvedEdge(site, match.TargetContainer, match.TargetName, EdgeConfidence.Exact));
        }

        return edges;
    }

    /// <summary>
    /// Runs <see cref="MsBuildProjectQuery.Query"/> over the requested projects and, transitively,
    /// over every project reference that resolves to a <c>.csproj</c> under the repository root.
    /// Queried in sorted order and de-duplicated by absolute path, so the closure is the same set in
    /// the same order regardless of the order <paramref name="projectPaths"/> arrives in.
    /// </summary>
    private static Dictionary<string, ProjectInputs> QueryProjectClosure(
        string repositoryRoot,
        IReadOnlyList<string> projectPaths,
        out Dictionary<string, RoslynProjectReport> reports)
    {
        var queried = new Dictionary<string, ProjectInputs>(PathComparer);
        reports = new Dictionary<string, RoslynProjectReport>(PathComparer);

        var pending = new List<string>(projectPaths.Select(Path.GetFullPath).OrderBy(p => p, StringComparer.Ordinal));
        var seen = new HashSet<string>(pending, PathComparer);

        for (var i = 0; i < pending.Count; i++)
        {
            var projectPath = pending[i];

            ProjectInputs inputs;
            try
            {
                inputs = MsBuildProjectQuery.Query(projectPath);
            }
            catch (MsBuildQueryException e)
            {
                reports[projectPath] = new RoslynProjectReport(projectPath, RoslynProjectAvailability.MsBuildQueryFailed, e.Message);
                continue;
            }

            queried[projectPath] = inputs;

            var discovered = inputs.References
                .Select(r => r.ProjectPath)
                .Where(p => p is not null && RelativeToRepository(repositoryRoot, p) is not null)
                .Select(p => p!)
                .Where(seen.Add)
                .OrderBy(p => p, StringComparer.Ordinal);

            pending.AddRange(discovered);
        }

        return queried;
    }

    /// <summary>
    /// Compiles every queried project, each one after the in-repository projects it references, so
    /// those can be handed in as <c>CompilationReference</c>s instead of <c>bin/</c> assemblies.
    /// Dependencies are visited depth-first with a cycle guard: MSBuild rejects circular
    /// <c>ProjectReference</c>s, but a guard here costs nothing and a stack overflow on malformed
    /// input costs a diagnosis.
    /// </summary>
    private static Dictionary<string, CSharpCompilation> CompileInDependencyOrder(
        Dictionary<string, ProjectInputs> queried,
        Dictionary<string, RoslynProjectReport> reports)
    {
        var compiled = new Dictionary<string, CSharpCompilation>(PathComparer);
        var inProgress = new HashSet<string>(PathComparer);

        foreach (var projectPath in queried.Keys.OrderBy(p => p, StringComparer.Ordinal))
        {
            Compile(projectPath, queried, reports, compiled, inProgress);
        }

        return compiled;
    }

    private static void Compile(
        string projectPath,
        Dictionary<string, ProjectInputs> queried,
        Dictionary<string, RoslynProjectReport> reports,
        Dictionary<string, CSharpCompilation> compiled,
        HashSet<string> inProgress)
    {
        if (compiled.ContainsKey(projectPath) || reports.ContainsKey(projectPath) || !inProgress.Add(projectPath))
        {
            return;
        }

        try
        {
            var inputs = queried[projectPath];

            foreach (var dependency in inputs.References
                         .Select(r => r.ProjectPath)
                         .Where(p => p is not null && queried.ContainsKey(p))
                         .Select(p => p!)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                Compile(dependency, queried, reports, compiled, inProgress);
            }

            var compilation = CompilationFactory.Create(inputs, compiled, out var missingReferences);

            if (missingReferences.Count > 0)
            {
                reports[projectPath] = new RoslynProjectReport(
                    projectPath,
                    RoslynProjectAvailability.ReferencesUnresolved,
                    $"{missingReferences.Count} reference(s) exist neither on disk nor as a project compiled from source, "
                    + $"first: {missingReferences[0]}");
                return;
            }

            var errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToList();

            if (errors.Count > 0)
            {
                reports[projectPath] = new RoslynProjectReport(
                    projectPath,
                    RoslynProjectAvailability.CompilationHadErrors,
                    $"{errors.Count} compilation error(s): {DescribeErrors(errors)}");
                return;
            }

            compiled[projectPath] = compilation;
            reports[projectPath] = new RoslynProjectReport(projectPath, RoslynProjectAvailability.Compiled, string.Empty);
        }
        finally
        {
            inProgress.Remove(projectPath);
        }
    }

    private static string DescribeErrors(IReadOnlyList<Diagnostic> errors) =>
        string.Join(", ", errors
            .GroupBy(d => d.Id, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(4)
            .Select(g => $"{g.Key} x{g.Count()}"));

    private bool TryGetIndex(string relativePath, out IReadOnlyDictionary<int, CalleeMatch> index)
    {
        if (_indexCache.TryGetValue(relativePath, out var cached))
        {
            index = cached;
            return true;
        }

        if (!_ownedFiles.TryGetValue(relativePath, out var owned))
        {
            index = new Dictionary<int, CalleeMatch>();
            return false;
        }

        index = BuildIndex(owned);
        _indexCache[relativePath] = index;
        return true;
    }

    /// <summary>
    /// Maps every callee identifier in one file, by UTF-8 byte offset, to what Roslyn binds it to.
    ///
    /// <para>
    /// The node whose offset is recorded is the bare callee identifier -- <c>Bar</c> in
    /// <c>obj.Bar&lt;T&gt;()</c>, not the member access, not the generic name, not the whole
    /// invocation -- because that is precisely the node the C# profile's call query captures
    /// (<c>name: (identifier) @callee</c>), and identity here is the offset the two engines share.
    /// </para>
    /// </summary>
    private static Dictionary<int, CalleeMatch> BuildIndex(OwnedFile owned)
    {
        // The exact string the tree was parsed from: SourceText.From(string) stores it verbatim, so
        // this is the same text TreeSitterExtractor decoded and measured against, not a re-read.
        var text = owned.Tree.GetText().ToString();
        var model = owned.Compilation.GetSemanticModel(owned.Tree);

        var index = new Dictionary<int, CalleeMatch>();
        foreach (var invocation in owned.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var callee = CalleeName(invocation.Expression);
            if (callee is null)
            {
                continue;
            }

            var symbol = TargetSymbol(model, invocation, callee);
            var (container, name) = DescribeTarget(symbol);

            // Roslyn counts UTF-16 code units; a CallSite's identity is a UTF-8 byte offset. Do not
            // delete this conversion on the grounds that the tree-sitter side "already gives UTF-16
            // too" -- see Utf8Offsets' summary; that is an artefact of the binding, and the failure
            // mode when it stops holding is a call credited to the wrong symbol, silently.
            var offset = Utf8Offsets.ToUtf8(text, callee.Identifier.SpanStart);
            index[offset] = new CalleeMatch(callee.Identifier.ValueText, container, name);
        }

        return index;
    }

    /// <summary>
    /// The callee identifier of an invocation, for exactly the expression shapes the C# profile's call
    /// query matches: a bare call, a member-access call, a generic call, a generic member-access call,
    /// and a null-conditional call. Anything else (an invoked parenthesized expression, an invoked
    /// element access) yields <see langword="null"/> -- the query does not capture those either, so
    /// there is no <see cref="CallSite"/> to attach to.
    /// </summary>
    private static SimpleNameSyntax? CalleeName(ExpressionSyntax expression) =>
        expression switch
        {
            SimpleNameSyntax simple => simple,
            MemberAccessExpressionSyntax member => member.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            _ => null,
        };

    /// <summary>
    /// What the invocation actually calls.
    ///
    /// <para>
    /// Resolved from the invocation, not the name node, so overload resolution has done its work --
    /// that is the whole difference between this resolver and the name-matching one. The one case
    /// where the invocation's symbol is the wrong answer is a delegate call (<c>_handler()</c>, where
    /// Roslyn binds to the delegate type's synthesized <c>Invoke</c>): the declaration a reader wants
    /// linked there is the field or property holding the delegate, which is also the one this
    /// producer emits a concept for, so the name node's symbol is used instead.
    /// </para>
    /// </summary>
    private static ISymbol? TargetSymbol(SemanticModel model, InvocationExpressionSyntax invocation, SimpleNameSyntax callee)
    {
        var symbol = model.GetSymbolInfo(invocation).Symbol;

        if (symbol is null or IMethodSymbol { MethodKind: MethodKind.DelegateInvoke })
        {
            symbol = model.GetSymbolInfo(callee).Symbol ?? symbol;
        }

        return symbol;
    }

    /// <summary>
    /// Turns a bound symbol into the <c>(Container, Name)</c> pair <see cref="SymbolFact"/> uses, or
    /// <c>(null, null)</c> when there is nothing in this repository to point at.
    ///
    /// <para>
    /// Shape matters more than it looks: <c>CodeGraphBuilder</c> joins an edge's target to a
    /// <see cref="SymbolFact"/> on <c>(Container, Name)</c> exactly, and degrades any edge that does
    /// not join. So the container is built the same way <c>TreeSitterExtractor.ComputeContainerPath</c>
    /// builds it -- every enclosing namespace and type, outermost first, dotted, plus the enclosing
    /// member for a local function -- and not, say, as Roslyn's own display string, which would render
    /// generics with their type arguments and never join anything.
    /// </para>
    ///
    /// <para>
    /// A symbol with no source declaration (the BCL, a NuGet package, a source-generated type from a
    /// referenced assembly) returns <c>(null, null)</c> deliberately: there is no concept for it, and
    /// saying so retracts the baseline's name-only guess rather than leaving a link to a same-named
    /// declaration that has nothing to do with the call.
    /// </para>
    /// </summary>
    private static (string? Container, string? Name) DescribeTarget(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return (null, null);
        }

        var definition = symbol.OriginalDefinition;
        if (definition is IMethodSymbol { ReducedFrom: not null } reduced)
        {
            // An extension method called as x.Foo(): link to the static method as declared.
            definition = reduced.ReducedFrom.OriginalDefinition;
        }

        if (definition.DeclaringSyntaxReferences.Length == 0 && !definition.Locations.Any(l => l.IsInSource))
        {
            return (null, null);
        }

        if (!IsDeclarationKindThisProducerEmits(definition))
        {
            return (null, null);
        }

        return (ContainerPathOf(definition), SimpleNameOf(definition));
    }

    /// <summary>
    /// Whether a bound target is the kind of declaration this producer emits a concept for.
    ///
    /// <para>
    /// Measured, not assumed: on <c>src/OKF4net</c>, every exact target that failed to join an
    /// extracted <see cref="SymbolFact"/> was a call through a delegate-valued local or parameter
    /// (<c>buildBody(...)</c>, <c>body(...)</c>, <c>synthesize(...)</c>). Roslyn resolves those
    /// perfectly well -- to a local, which is in source and has a name -- but a local is not a
    /// declaration any extractor turns into a symbol, so an edge naming one points at a concept that
    /// will never exist. <c>CodeGraphBuilder</c> would degrade it, and that is a real safety net, but
    /// it is a downstream one: it is better for <see cref="EdgeConfidence.Exact"/> to mean "an exact
    /// target this producer can actually link to" than for it to mean "exact, pending someone else
    /// noticing". Type parameters, labels and range variables are excluded for the same reason.
    /// </para>
    /// </summary>
    private static bool IsDeclarationKindThisProducerEmits(ISymbol symbol) =>
        symbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol;

    private static string ContainerPathOf(ISymbol symbol)
    {
        var segments = new List<string>();

        for (var current = symbol.ContainingSymbol; current is not null; current = current.ContainingSymbol)
        {
            if (current is INamespaceSymbol { IsGlobalNamespace: true })
            {
                break;
            }

            var segment = current switch
            {
                INamespaceSymbol ns => ns.Name,
                INamedTypeSymbol type => type.Name,
                // The enclosing member of a local function. An accessor reports its own name
                // (get_P), so its associated property is used instead -- that is the declaration
                // the extractor names. A constructor reports ".ctor"; the extractor names it after
                // its type, as C# source does.
                IMethodSymbol { AssociatedSymbol: { } associated } => associated.Name,
                IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } ctor =>
                    ctor.ContainingType?.Name ?? string.Empty,
                IMethodSymbol method => method.Name,
                _ => string.Empty,
            };

            // A lambda or anonymous function contributes no segment, exactly as it contributes no
            // `name` field for the tree-sitter walk to pick up.
            if (segment.Length > 0)
            {
                segments.Insert(0, segment);
            }
        }

        return string.Join(".", segments);
    }

    /// <summary>
    /// The declaration's name as it is written in source. Roslyn mangles an explicit interface
    /// implementation's name to its fully qualified form (<c>N.IFoo.Bar</c>); the source token, and
    /// so the extracted <see cref="SymbolFact.Name"/>, is just <c>Bar</c>.
    /// </summary>
    private static string SimpleNameOf(ISymbol symbol)
    {
        var name = symbol.Name;
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name[(lastDot + 1)..] : name;
    }

    /// <summary>
    /// <paramref name="absolutePath"/> as a repository-relative, forward-slashed path, or
    /// <see langword="null"/> when it is not under <paramref name="repositoryRoot"/> at all (a linked
    /// file from elsewhere, or a different drive).
    /// </summary>
    private static string? RelativeToRepository(string repositoryRoot, string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            return null;
        }

        var relative = Path.GetRelativePath(repositoryRoot, absolutePath);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
        {
            return null;
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// Used only to look a path up, never to decide what a path means. Windows resolves <c>Foo.cs</c>
    /// and <c>foo.cs</c> to one file, and MSBuild's spelling of a path need not match the walker's, so
    /// an ordinal comparison would silently own nothing on a case difference. Being too permissive on
    /// a case-sensitive filesystem is the safe direction: the worst it can do is claim a file whose
    /// offsets then match no call site, and an unattached site is left to the baseline.
    /// </summary>
    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record OwnedFile(CSharpCompilation Compilation, SyntaxTree Tree);

    private sealed record CalleeMatch(string CalleeName, string? TargetContainer, string? TargetName);
}
