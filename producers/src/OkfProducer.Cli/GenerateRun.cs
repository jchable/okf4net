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
/// <param name="MaxFileBytes">
/// §2.3: the per-file size cap, enforced by <b>both</b> engines and not only the extractor. The
/// tree-sitter extractor skips an over-cap file and counts it, so the run reports itself partial; the
/// Roslyn stage's <c>SourceFileGate</c> applies the same cap to MSBuild's <c>Compile</c> items and
/// drops an over-cap item silently. "The cap the extractor enforces" was the singular this parameter
/// used to claim, and it was the same false qualification the help text carried.
/// </param>
/// <param name="NoMsBuild">
/// Skip the Roslyn stage, and with it the <c>dotnet msbuild</c> evaluation it is built on, leaving
/// the name-matching baseline to resolve calls and no source-ownership map at all. This is the run's
/// only lever over the fact that evaluating a repository's MSBuild logic <i>executes</i> that logic;
/// see <see cref="GenerateRun"/>'s own remarks for what it does and does not buy.
/// </param>
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
    long MaxFileBytes,
    bool NoMsBuild);

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
///
/// <para><b>Running this on a repository evaluates that repository's MSBuild logic, which means
/// executing it.</b> The Roslyn stage below builds its compilations from a reference set obtained by
/// spawning <c>dotnet msbuild</c> once per project, in the project's own directory -- so
/// <c>Directory.Build.props</c> and <c>Directory.Build.targets</c>, every <c>Import</c> they reach,
/// any target hooked on <c>BeforeTargets="ResolveReferences"</c>, and a
/// <c>RoslynCodeTaskFactory</c> inline <c>&lt;Code&gt;</c> task all run, as the user running
/// <c>okfgen</c>. A <c>Directory.Build.rsp</c> in that directory is auto-applied too, so the
/// repository adds switches to the invocation itself -- measured on this host, an injected
/// <c>-t:Pwn</c> ran a target the producer never asked for. That is not a bound this producer can
/// tighten from the outside: it is what MSBuild evaluation is. <b>Point <c>okfgen</c> only at a
/// repository you would be willing to build.</b></para>
///
/// <para><c>--no-msbuild</c> is the way out for a repository you would not: it skips this stage
/// entirely, leaving the name-matching baseline, so <b>no <c>dotnet msbuild</c> is spawned and no
/// MSBuild logic from the scanned tree is evaluated</b>. That is the true statement, and narrower
/// than the one that stood here: <c>ConceptGenerator</c> spawns <c>git show -s</c> and
/// <c>git rev-parse</c> on every run including this one; this method spawns <c>git symbolic-ref</c>
/// below <i>unless</i> <c>--rev</c> already named the ref, in which case the <c>??</c> never
/// evaluates it; and <c>--check</c> spawns one further <c>git rev-parse</c>
/// (<c>BundleDrift.Check</c>) before the regeneration it compares against. All of them go through
/// <c>GitRevision.RunGit</c> with the scanned repository as their working directory. Two to four
/// invocations, then, not a fixed three -- <c>producers/README.md</c> states the same breakdown.
/// The flag is off by default deliberately -- making it opt-in would silently
/// degrade the resolution quality of every run that exists today -- which is why it is stated here and
/// in <c>producers/README.md</c> rather than left to be discovered.</para>
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

        // Built once and used twice -- by the extraction and by the manifest -- rather than
        // reconstructed at each site. The manifest's copy is what a later run compares its own scope
        // against before it is allowed to delete anything (see GenerationManifest.Scope), so the two
        // must be the same value by construction and not by two call sites agreeing.
        var scope = new ScopeOptions(request.IncludeTests, request.IncludeInternal);

        if (!request.NoCode)
        {
            profiles = [CSharpProfile.Instance];
            var resolvers = new List<ISymbolResolver> { new NameMatchResolver() };
            var projectPaths = CSharpProjectPaths(snapshot);

            // The one cap the two engines have to agree on. The tree-sitter extractor has always
            // enforced it; the Roslyn stage read every Compile item MSBuild listed regardless, which
            // made --max-file-size's help text false of half the code stage.
            var limits = ExtractionLimits.Default with { MaxFileBytes = request.MaxFileBytes };

            if (request.NoMsBuild)
            {
                // Said out loud rather than left to be inferred from a thinner call graph: this run's
                // links are name matches, and §2.1's whole point is that a name match on an ambiguous
                // name is refused rather than guessed, so what an operator loses here is edges.
                //
                // And the SECOND cost, which this note used to leave to a reader of the generic
                // "no source-ownership map" note further down: §5.1's ownership map is built from the
                // Compile item sets this stage asks MSBuild for, so skipping the stage leaves it null
                // and ConceptGenerator.AttributePackages emits no package -> namespace link at all.
                // That is a missing level of the containment spine, not a missing edge, and under
                // --update it overwrites previously-good packages/ concepts with link-less ones.
                note("--no-msbuild was passed, so no `dotnet msbuild` was spawned and no project was"
                    + " compiled. Two things are lost, not one. (1) Every `## Calls` link in this bundle"
                    + " comes from the name-matching baseline alone, and an inter-type ambiguity it"
                    + " cannot settle is left unlinked. (2) There is no source-ownership map either --"
                    + " it is built from the same MSBuild query -- so NO `packages` -> namespace"
                    + " containment link is emitted, and under --update that overwrites the ones a"
                    + " previous run had. Drop --no-msbuild on a repository you are willing to build to"
                    + " get both back.");
            }
            else if (projectPaths.Count > 0)
            {
                // Later resolvers override earlier ones for the files they own (§2.1), so the exact
                // resolver goes after the name-matching baseline, never before it.
                var roslyn = RoslynResolver.Create(request.RepoPath, projectPaths, limits);
                resolvers.Add(roslyn);

                ReportProjects(roslyn, request.RepoPath, note);
                ownership = Attribution(request.RepoPath, roslyn.QueriedProjects, note);
            }

            // Disposed as soon as the graph exists: CodeGraph holds symbols and edges, never a handle
            // into the parser.
            using var extractor = new TreeSitterExtractor();
            graph = new CodeGraphBuilder(extractor, profiles, resolvers).Build(snapshot, limits, scope);
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
        var manifest = graph is null ? null : GenerationManifest.ForRun(OwnedPrefix, concepts, graph.Status, scope);

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
    private static void ReportProjects(RoslynResolver resolver, string repoPath, Action<string> note) =>
        ReportProjects(repoPath, resolver.Projects, resolver.QueriedProjects, resolver.Owns, note);

    /// <summary>
    /// The reporting itself, over the three things the resolver exposes rather than over the resolver,
    /// for the same reason <see cref="Attribution"/> takes <see cref="ProjectInputs"/>: it is pure data
    /// and can be exercised without an MSBuild invocation.
    ///
    /// <para><b>Two notes, not one, and the second exists because the first is not enough.</b> A
    /// project that failed is named by its <see cref="RoslynProjectAvailability"/>. But a project can
    /// also be reported <see cref="RoslynProjectAvailability.Compiled"/> and still contribute nothing:
    /// if <c>SourceFileGate</c> refused every one of its <c>Compile</c> items -- each over
    /// <c>--max-file-size</c>, each behind a symbolic link or junction, or each absent -- the
    /// compilation is built from no syntax tree at all, and an empty <c>Library</c> compilation has
    /// zero diagnostics. (An <c>Exe</c> would give CS5001 and land in the first note; a <c>Library</c>
    /// does not.) The resolver then owns none of its files, so the name-matching baseline carries them
    /// exactly as it carries a failed project's -- while the report says <c>Compiled</c> and this
    /// method used to print nothing. That is not a silent gap, it is an affirmatively wrong statement
    /// about the run, which is why it is worth a rare-trigger guard.</para>
    ///
    /// <para>The test is "reported compiled but owns none of its own <c>Compile</c> items", and
    /// it is computable here precisely because <see cref="RoslynResolver.QueriedProjects"/> carries the
    /// item sets and <see cref="RoslynResolver.Owns"/> answers for the files -- no channel out of
    /// <c>CompilationFactory</c> is needed. It is deliberately a check on the <i>outcome</i> and not a
    /// second copy of the gate's rules: whatever refused the files, the conclusion is the same one.</para>
    /// </summary>
    /// <param name="repoPath">The repository root every reported path is made relative to.</param>
    /// <param name="reports">One availability verdict per project in the queried closure.</param>
    /// <param name="queried">The compiler inputs MSBuild answered with, one entry per project.</param>
    /// <param name="owns">
    /// <see cref="RoslynResolver.Owns"/>, over repository-relative <c>/</c>-separated paths -- the same
    /// form <see cref="Relative"/> produces, which is what makes the join here the resolver's own.
    /// </param>
    /// <param name="note">Where each named project is reported.</param>
    internal static void ReportProjects(
        string repoPath,
        IReadOnlyList<RoslynProjectReport> reports,
        IReadOnlyList<ProjectInputs> queried,
        Func<string, bool> owns,
        Action<string> note)
    {
        var inputsByProject = new Dictionary<string, ProjectInputs>(StringComparer.Ordinal);
        foreach (var inputs in queried)
        {
            inputsByProject[inputs.ProjectPath] = inputs;
        }

        foreach (var report in reports)
        {
            if (report.Availability != RoslynProjectAvailability.Compiled)
            {
                var detail = report.Detail is { Length: > 0 } ? " -- " + report.Detail : string.Empty;
                note(
                    $"{Relative(repoPath, report.ProjectPath)}: not compiled ({report.Availability}){detail}."
                    + " Calls in its files fall back to name matching, so some `## Calls` links in this run are"
                    + " approximate rather than exact.");
                continue;
            }

            // No inputs entry means MSBuild never answered for this project, in which case it cannot
            // have been reported Compiled either. Skipped rather than guessed at: a note whose premise
            // is a missing join is worse than no note.
            if (!inputsByProject.TryGetValue(report.ProjectPath, out var compiledInputs))
            {
                continue;
            }

            if (compiledInputs.CompileFiles.Any(file => owns(Relative(repoPath, file))))
            {
                continue;
            }

            note(
                $"{Relative(repoPath, report.ProjectPath)}: compiled with zero errors, but this run owns none"
                + $" of its files, so its `## Calls` links come from name matching exactly as an uncompiled"
                + $" project's do. MSBuild reported {compiledInputs.CompileFiles.Count} `Compile` item(s) for"
                + " it; a compilation whose files were all refused before it was built -- over"
                + " --max-file-size, behind a symbolic link or junction, absent, or outside this repository"
                + " -- is empty, and an empty library compilation has no errors to report. `Compiled`"
                + " overstates what happened here, which is why this note exists.");
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
