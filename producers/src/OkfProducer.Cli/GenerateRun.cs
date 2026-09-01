// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.CodeGraph.Roslyn;
using OkfProducer.CodeGraph.TreeSitter.Profiles;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;
using CodeGraphModel = OkfProducer.Core.CodeGraph.CodeGraph;
using TreeSitterExtractor = OkfProducer.CodeGraph.TreeSitter.TreeSitterExtractor;

namespace OkfProducer.Cli;

/// <summary>Everything one <c>okfgen generate</c> invocation was asked to do, after parsing.</summary>
/// <param name="RepoPath">Root of the repository to scan.</param>
/// <param name="OutPath">Root of the bundle to write -- or, under <paramref name="Check"/>, the bundle to compare against.</param>
/// <param name="Policy">How an existing <paramref name="OutPath"/> is treated.</param>
/// <param name="RepoUrl">§4.3's permalink base, or <see langword="null"/> for no <c>resource</c> at all.</param>
/// <param name="Rev">The ref permalinks point at, or <see langword="null"/> to read the current branch name.</param>
/// <param name="Check">§6.2: compare rather than write.</param>
/// <param name="IncludeTests">§5.4: walk test projects and <c>test</c>/<c>tests</c>/<c>spec</c> directories too.</param>
/// <param name="IncludeInternal">§5.4: emit <c>internal</c> declarations, not only public ones.</param>
/// <param name="NoCode">§5.4: skip the code-graph stage entirely.</param>
/// <param name="MaxFileBytes">§2.3: the per-file size cap the extractor enforces.</param>
internal sealed record GenerateRequest(
    string RepoPath,
    string OutPath,
    WritePolicy Policy,
    string? RepoUrl,
    string? Rev,
    bool Check,
    bool IncludeTests,
    bool IncludeInternal,
    bool NoCode,
    long MaxFileBytes);

/// <summary>
/// The producer's composition root: the one place scan, extract, resolve, attribute, generate and
/// write are assembled into the run the <c>okfgen</c> binary actually performs.
///
/// <para><b>It lives in the CLI because only the CLI can build it.</b> <c>OkfProducer.Core</c> holds
/// the pipeline's contracts and must not reference the tree-sitter grammars or Roslyn; the two
/// implementation projects reference Core and not each other. The composition therefore has exactly
/// one possible home, and this is it.</para>
///
/// <para><b>Three things here are load-bearing rather than incidental</b>, each of them a feature that
/// shipped inert because nothing composed it:</para>
/// <list type="bullet">
/// <item><see cref="GenerateOptions.ExistingFrontmatter"/> is supplied on every
/// <see cref="WritePolicy.Update"/> run. Without it §4.2's field preservation never runs outside a
/// test, a hand-written <c>description</c> is destroyed by the next <c>generate</c>, and <c>--check</c>
/// reports every hand-edited concept as drift for ever.</item>
/// <item><c>--no-code</c> passes <see langword="null"/> for the manifest. The manifest is a licence to
/// delete the ids the previous one claimed; a run that generated no code concept would present that
/// licence while claiming ownership of nothing.</item>
/// <item><see cref="GenerateOptions.Note"/> is wired to stderr. The notes it carries are the run's own
/// account of what it could not do -- a package whose namespaces went unattributed, a project MSBuild
/// could not answer for -- and with no sink they were computed and dropped.</item>
/// </list>
/// </summary>
internal static class GenerateRun
{
    /// <summary>
    /// The concept-id prefix this producer owns, and therefore the only subtree pruning may ever
    /// touch. The same value <c>ProducerFixture</c> passes, for the same reason.
    /// </summary>
    public const string OwnedPrefix = "code";

    /// <summary>
    /// One complete run into <paramref name="request"/>'s output directory.
    /// <paramref name="note"/> receives the run's own account of what it could not do.
    /// </summary>
    public static WriteResult Execute(GenerateRequest request, ProducerServices services, Action<string> note)
    {
        var snapshot = services.Scanner.Scan(request.RepoPath);

        var rev = request.Rev ?? GitRevision.CurrentBranch(request.RepoPath);
        if (request.RepoUrl is { Length: > 0 } && rev is null)
        {
            note(
                "--repo-url was supplied, but no branch name could be read from the repository (a detached HEAD,"
                + " or not a git checkout at all), so NO `resource` permalink was emitted on any code concept."
                + " Pass --rev with the ref the permalinks should point at. The HEAD sha is deliberately not used"
                + " as a fallback: it would rewrite the `resource` of every code concept on the next commit.");
        }

        if (request.RepoUrl is null && request.Rev is { Length: > 0 })
        {
            // The mirror of the note above, and reported for the same reason: a `resource` needs both
            // halves, so a --rev with no base URL to address changes nothing at all. Saying so beats
            // letting an operator conclude the permalinks are missing for some deeper reason.
            note("--rev was supplied without --repo-url, so it had no effect: a `resource` permalink needs both a base URL and a ref, and there is no base URL here for the ref to address.");
        }

        CodeGraphModel? graph = null;
        SourceOwnershipMap? ownership = null;
        IReadOnlyList<LanguageProfile> profiles = [];

        if (!request.NoCode)
        {
            profiles = [CSharpProfile.Instance];
            var resolvers = new List<ISymbolResolver> { new NameMatchResolver() };
            var projectPaths = CSharpProjectPaths(snapshot);

            if (projectPaths.Count > 0)
            {
                // Later resolvers override earlier ones for the files they own (§2.1), so the exact
                // resolver goes after the name-matching baseline, never before it.
                var roslyn = RoslynResolver.Create(request.RepoPath, projectPaths);
                resolvers.Add(roslyn);

                ReportProjects(roslyn, request.RepoPath, note);
                ownership = Attribution(request.RepoPath, roslyn.QueriedProjects, note);
            }

            // Disposed as soon as the graph exists: CodeGraph holds symbols and edges, never a handle
            // into the parser.
            using var extractor = new TreeSitterExtractor();
            graph = new CodeGraphBuilder(extractor, profiles, resolvers).Build(
                snapshot,
                ExtractionLimits.Default with { MaxFileBytes = request.MaxFileBytes },
                new ScopeOptions(request.IncludeTests, request.IncludeInternal));
        }

        var options = new GenerateOptions
        {
            RepoUrl = request.RepoUrl,
            Rev = rev,
            Profiles = profiles,

            // Only under Update, and deliberately not under Reset: `For` reads lazily, at generation
            // time, which is BEFORE BundleWriter deletes the directory -- so supplying it under Reset
            // would have a "delete and recreate" run quietly carry hand-written descriptions across
            // the deletion it was asked to perform.
            ExistingFrontmatter = request.Policy == WritePolicy.Update
                ? ExistingBundleFrontmatter.For(request.OutPath)
                : null,
            SourceOwnership = ownership,
            Note = note,
        };

        var concepts = services.Generator.Generate(snapshot, graph, options);

        // Null manifest and null status on the --no-code path, which is what keeps pruning out of a
        // run that analysed no source at all. BundleWriter has a backstop for that case, but a caller
        // that hands over a licence to delete and relies on the callee to refuse it is one refactor
        // away from deleting the whole code family.
        var manifest = graph is null ? null : GenerationManifest.ForRun(OwnedPrefix, concepts, graph.Status);

        return services.Writer.Write(request.OutPath, concepts, request.Policy, snapshot.RepoPath, manifest, graph?.Status);
    }

    /// <summary>
    /// The <c>.csproj</c> files the scan detected, absolute -- the same set the <c>packages/</c>
    /// family is generated from, so the ownership map's join key is by construction the one
    /// <c>ConceptGenerator</c> looks a package up by.
    /// </summary>
    private static IReadOnlyList<string> CSharpProjectPaths(RepositorySnapshot snapshot) =>
    [
        .. snapshot.Packages
            .Where(p => string.Equals(p.Ecosystem, "nuget", StringComparison.Ordinal))
            .Select(p => Path.GetFullPath(Path.Combine(snapshot.RepoPath, p.RelativePath)))
    ];

    /// <summary>
    /// §5.1's source-ownership map, built from the <c>Compile</c> item sets the resolver already asked
    /// MSBuild for -- or <see langword="null"/> when MSBuild answered for no project at all, in which
    /// case the run says so and emits no package -> namespace link rather than guessing one from the
    /// directory tree.
    ///
    /// <para>Takes the queried inputs rather than the resolver that produced them, so the join is
    /// pure data and can be exercised without an MSBuild invocation. A wrong join here is not loud:
    /// it yields a missing or misattributed <c>packages -&gt; namespace</c> link, which is exactly the
    /// kind of defect that survives on "verified by one manual run".</para>
    /// </summary>
    /// <param name="repoPath">The repository root every path is made relative to.</param>
    /// <param name="queried">The compiler inputs MSBuild answered with, one entry per project.</param>
    /// <param name="note">Where the "no map at all" degradation is reported.</param>
    internal static SourceOwnershipMap? Attribution(string repoPath, IReadOnlyList<ProjectInputs> queried, Action<string> note)
    {
        if (queried.Count == 0)
        {
            note(
                "MSBuild could not report the compiler inputs of any project (no `dotnet` on PATH, or a"
                + " repository that has never been restored), so this run has no source-ownership map and emits"
                + " no package -> namespace containment link. Restoring the repository (`dotnet restore`) is what"
                + " fixes it; nothing here is derived from the directory tree instead.");
            return null;
        }

        return SourceOwnershipMap.From(
            repoPath,
            queried.Select(p => new ProjectCompileItems(p.ProjectPath, p.TargetFramework, p.CompileFiles)));
    }

    /// <summary>
    /// Names every project the exact resolver could not use, one note apiece, so a run whose call
    /// graph is only approximate says which projects made it so instead of leaving the operator to
    /// infer it from missing links.
    /// </summary>
    private static void ReportProjects(RoslynResolver resolver, string repoPath, Action<string> note)
    {
        foreach (var report in resolver.Projects)
        {
            if (report.Availability == RoslynProjectAvailability.Compiled)
            {
                continue;
            }

            var detail = report.Detail is { Length: > 0 } ? " -- " + report.Detail : string.Empty;
            note(
                $"{Relative(repoPath, report.ProjectPath)}: not compiled ({report.Availability}){detail}."
                + " Calls in its files fall back to name matching, so some `## Calls` links in this run are"
                + " approximate rather than exact.");
        }
    }

    /// <summary>
    /// <paramref name="path"/> relative to <paramref name="repoPath"/>, <c>/</c>-separated -- absolute
    /// paths belong in nobody's console output and never in the bundle (§6.2).
    /// </summary>
    private static string Relative(string repoPath, string path)
    {
        try
        {
            return Path.GetRelativePath(repoPath, path).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
