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
/// calls and one whose call graph is only approximate. That distinction is a report to the operator,
/// not a pruning gate: see <see cref="IsComplete"/>'s own doc comment for why it cannot be one.</para>
///
/// <para><b>Loud, but never fatal to the run.</b> An unknown <c>LangVersion</c> is refused rather
/// than degraded to a preview language version (correction 3) -- but the refusal is scoped to the one
/// project that pinned it, reported as <see cref="RoslynProjectAvailability.UnknownLanguageVersion"/>
/// with the version named. The hazard correction 3 identifies is a <i>silent</i> change of parse
/// semantics, and not compiling that project answers it completely; failing the whole run on top would
/// throw away the exact resolution of every other project for nothing.</para>
///
/// <para><b>Known limitation: Roslyn source generators do not run.</b> The MSBuild query returns the
/// <i>files</i> the SDK generates (correction 1 is exactly about getting them), but a Roslyn source
/// generator runs inside the compiler and contributes no <c>Compile</c> item at all. A project whose
/// source references generated members therefore does not compile here, and this resolver reports it
/// <see cref="RoslynProjectAvailability.CompilationHadErrors"/> and declines to resolve from it.
/// Measured on this repository: <c>src/OKF4net.Cli</c> uses <c>System.Text.Json</c>'s generator and
/// produces six errors (<c>CS0534</c> x2, <c>CS7036</c>, <c>CS0117</c> x3) on the members that
/// generator would have emitted, while the other seven <c>src/</c> projects compile clean.
/// <c>System.Text.Json</c>, <c>GeneratedRegex</c>, <c>LoggerMessage</c> and most of ASP.NET are all in
/// this category, so it is not a corner case.
/// <b>This is a documented limitation, ruled on deliberately -- not an oversight and not a bug to
/// fix in passing.</b> Running generators means loading and executing analyzer assemblies chosen by
/// the <i>scanned repository</i>: arbitrary code, from exactly the input 2.3 treats as hostile, inside
/// a tool whose whole job is to read untrusted source. Trading this producer's security posture for
/// better resolution on some projects is not a trade to make quietly. A future path exists -- a
/// sandboxed generator host -- and that, not an unguarded <c>CSharpGeneratorDriver</c>, is what would
/// lift the limit. Meanwhile the behaviour is the safe one: such a project degrades to the
/// name-matching baseline and <see cref="IsComplete"/> says so, which is what an operator reading the
/// run's report must see. See 7.2 of <c>docs/superpowers/specs/2026-08-31-okf-producer-code-graph-design.md</c>.</para>
///
/// <para><b>Blast radius of a failed project.</b> A project that does not compile costs more than its
/// own files, and the cost is worth stating plainly. Its files are not <see cref="Owns"/>ed, so every
/// call <i>inside</i> it drops to the name-matching baseline -- and calls <i>into</i> it, from clean
/// projects, are affected too: those bind against its <c>bin/</c> assembly, so the target arrives as a
/// metadata symbol rather than a source one and cannot be resolved exactly. <see cref="Resolve"/>
/// detects that case (see its remarks) and stays silent instead of retracting, so the baseline's
/// verdict survives -- but the edge is <see cref="EdgeConfidence.ByName"/>, not
/// <see cref="EdgeConfidence.Exact"/>. Precision degrades across the reverse-dependency cone of the
/// failing project, not just within it.</para>
/// </summary>
public sealed class RoslynResolver : ISymbolResolver
{
    private readonly IReadOnlyDictionary<string, OwnedFile> _ownedFiles;

    // Built once per file on first use rather than for every compiled file up front: converting each
    // callee offset costs a pass over the file's prefix, and most files in a repository hold no call
    // site this run actually asks about.
    private readonly Dictionary<string, IReadOnlyDictionary<int, CalleeMatch>> _indexCache;

    // Build outputs of the repository's own projects, from MSBuild's provenance metadata. A target
    // found in one of these is in the repository even though it arrived as metadata, which is what
    // separates "the BCL, retract the guess" from "our own project that failed to compile, leave the
    // baseline alone". A lookup only; never iterated into any output.
    private readonly IReadOnlySet<string> _repositoryProjectAssemblies;

    private RoslynResolver(
        IReadOnlyList<RoslynProjectReport> projects,
        IReadOnlyDictionary<string, OwnedFile> ownedFiles,
        IReadOnlySet<string> repositoryProjectAssemblies)
    {
        Projects = projects;
        _ownedFiles = ownedFiles;
        _repositoryProjectAssemblies = repositoryProjectAssemblies;
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
    /// Whether this resolver covered the repository completely: at least one project, and every one of
    /// them compiled. <see langword="false"/> means some C# was resolved by name alone, so this run's
    /// call graph is approximate and an operator should be told.
    ///
    /// <para><b>It is not the pruning gate, and an earlier version of this comment said it was.</b>
    /// Task 11 settled it against the code: which concepts exist is decided entirely by extraction --
    /// <c>CodeGraphBuilder</c> builds <c>CodeGraph.Symbols</c> from <see cref="ILanguageExtractor"/>
    /// output filtered by <see cref="FileEligibility.IsInScope"/>, and no resolver contributes a symbol
    /// to it. A resolver decides only whether a call site renders as a link or as a code span. So a
    /// degraded resolver cannot make a symbol <i>absent</i>, which is the only way an incomplete
    /// picture could turn into a wrong deletion. Gating on it would also make pruning dead code on this
    /// very repository -- <c>src/OKF4net.Cli</c> uses a source generator and does not compile here, so
    /// this property is <see langword="false"/> on an ordinary checkout -- which is the same trap
    /// <see cref="RunStatus.IsComplete"/> sets, for the same shape of reason. What DOES gate pruning is
    /// <see cref="RunStatus.TraversalComplete"/> plus the per-file <see cref="FileStatus"/>; see
    /// <c>BundleWriter</c>.</para>
    ///
    /// <para>
    /// The <c>Count &gt; 0</c> clause is the whole point of this property, not a formality.
    /// <c>Projects.All(...)</c> over an empty list is vacuously <see langword="true"/>, so a resolver
    /// constructed with no projects at all -- which is precisely the state in which EVERY call in the
    /// repository fell back to name matching -- would otherwise report itself complete. That state is
    /// reachable rather than theoretical: finding no <c>.csproj</c> in a C# repository is a known gap
    /// in this producer, and it yields an empty project list, not an error.
    /// </para>
    ///
    /// <para>
    /// What that clause protects is the <b>run's report</b>, which is all this property feeds. Claiming
    /// completeness there would tell a human the call graph is exact when every edge in it was in fact
    /// guessed from a name -- and a wrong <c>## Calls</c> link reads as confidently as a right one. It
    /// forbids nothing, gates nothing, and blocks no deletion; see the paragraph above for why it
    /// cannot.
    /// </para>
    /// </summary>
    public bool IsComplete =>
        Projects.Count > 0 && Projects.All(p => p.Availability == RoslynProjectAvailability.Compiled);

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
    /// <remarks>
    /// Does not throw for a project it cannot handle -- every failure, including a <c>LangVersion</c>
    /// this Roslyn build does not know, is reported through <see cref="Projects"/> so the run
    /// continues with whatever did compile.
    /// </remarks>
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
            ownedFiles,
            RepositoryProjectAssemblies(repositoryRoot, queried));
    }

    /// <summary>
    /// Every assembly path MSBuild reported as the output of a project under
    /// <paramref name="repositoryRoot"/>, gathered across the whole queried closure.
    ///
    /// <para>
    /// Membership is decided by <see cref="ProjectReferenceInput.ProjectPath"/> -- MSBuild's
    /// <c>MSBuildSourceProjectFile</c> -- resolving under the repository root, not by the assembly's
    /// own path or name. The distinction matters: a project's output can be redirected anywhere by
    /// <c>OutputPath</c>, and a NuGet package can share a repository project's name, so neither the
    /// path nor the name identifies a project output. The <c>.csproj</c> MSBuild names does.
    /// </para>
    /// </summary>
    private static HashSet<string> RepositoryProjectAssemblies(
        string repositoryRoot, Dictionary<string, ProjectInputs> queried)
    {
        var assemblies = new HashSet<string>(PathComparer);

        foreach (var inputs in queried.Values)
        {
            foreach (var reference in inputs.References)
            {
                if (reference.ProjectPath is not null
                    && RelativeToRepository(repositoryRoot, reference.ProjectPath) is not null)
                {
                    assemblies.Add(reference.AssemblyPath);
                }
            }
        }

        return assemblies;
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
    /// <para>
    /// That retraction is withheld in one case, and the distinction is not a nicety. A call into one
    /// of the repository's <i>own</i> projects also binds to a metadata symbol whenever that project
    /// failed to compile and was referenced from its <c>bin/</c> assembly -- but there the concept
    /// does exist, because the extractor read that project's source whether or not Roslyn could
    /// compile it, so retracting would delete a correct baseline edge. Such a site is omitted, the
    /// same as an unattached one. Without this, one failing project would silently cost edges in every
    /// clean project that calls into it, which the source-generator limitation above makes a common
    /// shape rather than a corner.
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

            if (match.Kind == TargetKind.UncompiledRepositoryProject)
            {
                // The target IS in this repository -- the extractor read its source and emitted a
                // concept for it -- Roslyn just could not compile its project and saw it as metadata
                // instead. Overriding here would retract a baseline edge that was correct and whose
                // target concept exists, so one project's failure would cost edges in every clean
                // project that calls into it. Say nothing and let the baseline stand.
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

            CSharpCompilation compilation;
            IReadOnlyList<string> missingReferences;
            try
            {
                compilation = CompilationFactory.Create(inputs, compiled, out missingReferences);
            }
            catch (UnknownLanguageVersionException e)
            {
                // Loud, but scoped. The hazard correction 3 names is a SILENT fallback to a preview
                // language version, and refusing to do that is fully achieved by not compiling this
                // project: its message lands verbatim in the report, its files are not owned, and the
                // name-matching baseline carries it. Taking the whole run down as well would be a
                // second, unrelated loss -- every other project in the repository can still be
                // resolved exactly, and one project pinning a language this Roslyn has not learned yet
                // is no reason to give that up.
                reports[projectPath] = new RoslynProjectReport(
                    projectPath, RoslynProjectAvailability.UnknownLanguageVersion, e.Message);
                return;
            }

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
    private Dictionary<int, CalleeMatch> BuildIndex(OwnedFile owned)
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
            var (kind, container, name) = DescribeTarget(owned.Compilation, _repositoryProjectAssemblies, symbol);

            // Roslyn counts UTF-16 code units; a CallSite's identity is a UTF-8 byte offset. Do not
            // delete this conversion on the grounds that the tree-sitter side "already gives UTF-16
            // too" -- see Utf8Offsets' summary; that is an artefact of the binding, and the failure
            // mode when it stops holding is a call credited to the wrong symbol, silently.
            var offset = Utf8Offsets.ToUtf8(text, callee.Identifier.SpanStart);

            // Identifier.Text, not ValueText: ValueText strips the @ from a verbatim identifier, while
            // CallSite.CalledName is the grammar's raw token and keeps it, so @class() would fail the
            // name guard in Resolve on a difference that is purely about how the name was spelled.
            index[offset] = new CalleeMatch(callee.Identifier.Text, kind, container, name);
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
    /// A symbol with no source declaration comes back <see cref="TargetKind.External"/>, and the two
    /// reasons that can happen need opposite handling -- which is why this returns three outcomes, not
    /// two. A target that is <i>genuinely</i> outside the repository (the BCL, a NuGet package) has no
    /// concept to point at, so saying so is a gain: it retracts the baseline's name-only guess rather
    /// than leaving a link to a same-named declaration that has nothing to do with the call. But a
    /// target in one of the repository's OWN projects also arrives as metadata whenever that project
    /// failed to compile and was referenced from its <c>bin/</c> assembly instead -- and there the
    /// baseline was right, its target concept does exist (the extractor read that project's source
    /// regardless of whether Roslyn could compile it), and retracting would destroy a correct edge.
    /// That second case is <see cref="TargetKind.UncompiledRepositoryProject"/> and this resolver
    /// stays out of its way.
    /// </para>
    /// </summary>
    private static (TargetKind Kind, string? Container, string? Name) DescribeTarget(
        CSharpCompilation compilation, IReadOnlySet<string> repositoryProjectAssemblies, ISymbol? symbol)
    {
        if (symbol is null)
        {
            return (TargetKind.External, null, null);
        }

        var definition = symbol.OriginalDefinition;
        if (definition is IMethodSymbol { ReducedFrom: not null } reduced)
        {
            // An extension method called as x.Foo(): link to the static method as declared.
            definition = reduced.ReducedFrom.OriginalDefinition;
        }

        if (definition.DeclaringSyntaxReferences.Length == 0 && !definition.Locations.Any(l => l.IsInSource))
        {
            return (
                IsFromUncompiledRepositoryProject(compilation, repositoryProjectAssemblies, definition)
                    ? TargetKind.UncompiledRepositoryProject
                    : TargetKind.External,
                null,
                null);
        }

        if (!IsDeclarationKindThisProducerEmits(definition))
        {
            return (TargetKind.External, null, null);
        }

        return (TargetKind.InSource, ContainerPathOf(definition), SimpleNameOf(definition));
    }

    /// <summary>
    /// Whether a metadata symbol came from an assembly that is one of the repository's own projects'
    /// build outputs.
    ///
    /// <para>
    /// Answered from MSBuild's own provenance, never from a filename: the assembly symbol is mapped
    /// back to the <see cref="MetadataReference"/> it was loaded from, and that reference's path is
    /// looked up among the <c>ReferencePath</c> items MSBuild tagged
    /// <c>ReferenceSourceTarget=ProjectReference</c> with an <c>MSBuildSourceProjectFile</c> under the
    /// repository root (see <see cref="ProjectReferenceInput"/>). Matching by assembly name would
    /// confuse a repository project with a NuGet package that happens to share its name, and this
    /// decision changes whether a correct edge survives -- not a place to guess.
    /// </para>
    /// </summary>
    private static bool IsFromUncompiledRepositoryProject(
        CSharpCompilation compilation, IReadOnlySet<string> repositoryProjectAssemblies, ISymbol definition)
    {
        var assembly = definition.ContainingAssembly;
        if (assembly is null)
        {
            return false;
        }

        return compilation.GetMetadataReference(assembly) is PortableExecutableReference { FilePath: { } path }
            && repositoryProjectAssemblies.Contains(path);
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
    /// The declaration's name <b>exactly as it is written in source</b>, because that -- not Roslyn's
    /// idea of the name -- is what <see cref="SymbolFact.Name"/> holds, and the two have to be the
    /// same string to join.
    ///
    /// <para>
    /// They differ in two ways. Roslyn mangles an explicit interface implementation's name to its
    /// fully qualified form (<c>N.IFoo.Bar</c>) where the source token is just <c>Bar</c>; and Roslyn
    /// strips the <c>@</c> from a verbatim identifier (<c>@class</c> becomes <c>class</c>) where the
    /// grammar hands the extractor the raw token, <c>@class</c>. So the declaring syntax's own
    /// identifier token is preferred whenever there is one -- it is the same text the tree-sitter
    /// query captured, by construction -- and <see cref="ISymbol.Name"/> is only the fallback, for a
    /// symbol with no source declaration to read.
    /// </para>
    /// </summary>
    private static string SimpleNameOf(ISymbol symbol)
    {
        foreach (var reference in symbol.DeclaringSyntaxReferences)
        {
            // The node kinds here mirror CSharpProfile.DeclarationQuery's, deliberately: a declaration
            // this producer does not extract has no SymbolFact to join anyway, so there is nothing to
            // match its spelling against.
            var identifier = reference.GetSyntax() switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier,
                DelegateDeclarationSyntax dele => dele.Identifier,
                MethodDeclarationSyntax method => method.Identifier,
                ConstructorDeclarationSyntax constructor => constructor.Identifier,
                DestructorDeclarationSyntax destructor => destructor.Identifier,
                PropertyDeclarationSyntax property => property.Identifier,
                EventDeclarationSyntax @event => @event.Identifier,
                LocalFunctionStatementSyntax local => local.Identifier,
                VariableDeclaratorSyntax declarator => declarator.Identifier,
                _ => default,
            };

            if (identifier.Text.Length > 0)
            {
                return identifier.Text;
            }
        }

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
    /// <see cref="StringComparer.Ordinal"/>, the same rule every other path comparison in this
    /// producer uses (6.2's "never a culture-dependent comparison"; <c>FileEligibility</c> was
    /// deliberately moved off <see cref="StringComparer.OrdinalIgnoreCase"/> for it, and a
    /// case-sensitive filesystem can genuinely hold both <c>src/Foo</c> and <c>src/foo</c>).
    ///
    /// <para>
    /// This is a join key between two extractors, so being permissive here would not paper over a
    /// mismatch, it would hide one. Both sides derive their relative path the same way -- the
    /// repository root, then <see cref="Path.GetRelativePath(string, string)"/>, then forward slashes
    /// -- so they agree exactly, and if they ever stop agreeing the right outcome is that
    /// <see cref="Owns"/> returns <see langword="false"/>, the attachment rate collapses, and the
    /// floor test says so loudly. That is a finding about normalisation, and it should not be
    /// swallowed by a comparison that shrugs at it.
    /// </para>
    /// </summary>
    private static StringComparer PathComparer => StringComparer.Ordinal;

    /// <summary>What kind of thing a call site's target turned out to be.</summary>
    private enum TargetKind
    {
        /// <summary>Declared in source this run compiled: an exact target, and one with a concept.</summary>
        InSource,

        /// <summary>
        /// Declared outside the repository (the BCL, a NuGet package). No concept exists, so the
        /// verdict is <see cref="EdgeConfidence.Unresolved"/> -- which deliberately overrides a
        /// name-only baseline guess, because that guess is wrong.
        /// </summary>
        External,

        /// <summary>
        /// Declared in one of the repository's own projects, reached as metadata because that project
        /// did not compile. A concept for it does exist, so the baseline's verdict is left alone
        /// rather than overridden.
        /// </summary>
        UncompiledRepositoryProject,
    }

    private sealed record OwnedFile(CSharpCompilation Compilation, SyntaxTree Tree);

    private sealed record CalleeMatch(string CalleeName, TargetKind Kind, string? TargetContainer, string? TargetName);
}
