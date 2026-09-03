// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using OKF4net;
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

    /// <summary>The id <c>ConceptGenerator</c> fixes for the repository overview, and the root every reachability walk starts from.</summary>
    private const string OverviewId = "overview";

    /// <summary>
    /// One complete run into <paramref name="request"/>'s output directory.
    /// <paramref name="note"/> receives the run's own account of what it could not do;
    /// <paramref name="report"/> receives the completeness report, unconditionally.
    /// </summary>
    /// <param name="request">Everything this invocation was asked to do.</param>
    /// <param name="services">The scan/generate/write services the host resolved.</param>
    /// <param name="note">Where each individual degradation is reported.</param>
    /// <param name="report">
    /// Where the completeness report goes -- <b>every run, degraded or not</b>. A report that only
    /// appears when something went wrong cannot be told apart from a mechanism that failed to fire,
    /// which is precisely the defect the individual notes above have: each of them is scoped to a
    /// different subset of what a run can leave out, and nothing aggregated them. See
    /// <see cref="Summarize"/>.
    /// </param>
    public static WriteResult Execute(GenerateRequest request, ProducerServices services, Action<string> note, Action<string> report)
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

        // Hoisted out of the code-stage block below so the completeness report can read them whatever
        // path the run took. `projectsDetected` is the count of `.csproj` the SCAN found, which is not
        // the same number as `roslyn.Projects.Count` -- that one covers the queried closure, referenced
        // projects included -- and the difference is exactly what tells "this repository has no project
        // file" apart from "its projects would not compile".
        var projectsDetected = 0;
        RoslynResolver? roslyn = null;

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
            projectsDetected = projectPaths.Count;

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
                roslyn = RoslynResolver.Create(request.RepoPath, projectPaths, limits);
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

        // BEFORE the write, not after it. Everything the report states is settled once the concepts
        // exist, and the write is the one step here that can throw out of this method -- a full volume,
        // a bundle root that will not resolve. Reporting first means a run whose write failed has still
        // said what its analysis found, rather than losing that account to the same exception.
        foreach (var line in Summarize(request, graph?.Status, projectsDetected, roslyn, concepts))
        {
            report(line);
        }

        // Null manifest and null status on the --no-code path, which is what keeps pruning out of a
        // run that analysed no source at all. BundleWriter has a backstop for that case, but a caller
        // that hands over a licence to delete and relies on the callee to refuse it is one refactor
        // away from deleting the whole code family.
        var manifest = graph is null ? null : GenerationManifest.ForRun(OwnedPrefix, concepts, graph.Status, scope);

        return services.Writer.Write(request.OutPath, concepts, request.Policy, snapshot.RepoPath, manifest, graph?.Status);
    }

    /// <summary>
    /// The completeness report: what this run read, what it could not read, how much of it was
    /// resolved exactly, and whether the <c>code</c> family it produced is reachable at all.
    ///
    /// <para><b>Why this exists at all, when every degradation above already has a note.</b> Each note
    /// is scoped to a different subset of what a run can leave out, and each is emitted only when its
    /// own trigger fires -- so on a run where none of them fires, an operator cannot tell "nothing to
    /// report" from "the mechanism did not fire". Reproduced on this host, each exiting 0 having printed
    /// NOTHING AT ALL beyond <c>Wrote N concept(s)</c>: a run whose <c>--max-file-size</c> dropped every
    /// source file; a repository with no package manifest and no source-ownership map, where 100% of the
    /// <c>code</c> family is unreachable from <c>overview</c> while <c>okf validate</c> stays silent
    /// because nothing dangles; and a walk truncated by a circular junction, which writes
    /// <c>overview</c> alone. A fourth -- a repository with no <c>.csproj</c> but WITH a package
    /// manifest, where the whole Roslyn stage sits behind a guard with no <c>else</c> -- printed the
    /// generic "no source-ownership map" note and nothing more, which says nothing about the stage
    /// having been skipped or about every call link being a name match. This report is emitted on every
    /// run, in the same shape, whether or not any of that happened.</para>
    ///
    /// <para><b>It says counts, not verdicts, and it never guesses.</b> The resolution fraction is
    /// asked of <paramref name="owns"/> -- the resolver's own answer, file by file -- rather than
    /// derived from the project verdicts, so a project reported <c>Compiled</c> whose files were all
    /// refused before the compilation was built contributes zero exact files here, with no second copy
    /// of that rule. Reachability is walked over the bytes the run actually produced, using
    /// <see cref="LinkScanner.ExtractLinks"/> -- the validator's own scanner -- rather than re-deriving
    /// the spine rule, so a link this producer wrote and the scanner will not see (an unbalanced
    /// backtick in a lifted title, a description line that opens a fence) counts as absent here exactly
    /// as it does for <c>okf validate</c>.</para>
    ///
    /// <para><b>It is not a substitute for the notes.</b> The notes say <i>why</i>, name the project or
    /// the flag, and prescribe the remedy; this says <i>how much</i>, in one line, always.</para>
    /// </summary>
    /// <param name="request">This invocation, for the two flags that change what the report can say.</param>
    /// <param name="status">This run's extraction outcome, or <see langword="null"/> when the code stage did not run.</param>
    /// <param name="projectsDetected">How many <c>.csproj</c> the scan found -- zero means the Roslyn stage was never entered.</param>
    /// <param name="roslyn">The exact resolver, or <see langword="null"/> when no compilation was attempted.</param>
    /// <param name="concepts">Everything the generator produced, which is what reachability is walked over.</param>
    private static IReadOnlyList<string> Summarize(
        GenerateRequest request,
        RunStatus? status,
        int projectsDetected,
        RoslynResolver? roslyn,
        IReadOnlyList<GeneratedConcept> concepts) =>
        Summarize(request.NoMsBuild, status, projectsDetected, roslyn?.Projects ?? [], roslyn is null ? null : roslyn.Owns, concepts);

    /// <summary>
    /// The report itself, over the values rather than over the resolver and the request -- the same
    /// shape, and for the same reason, as <see cref="ReportProjects(string, IReadOnlyList{RoslynProjectReport}, IReadOnlyList{ProjectInputs}, Func{string, bool}, Action{string})"/>:
    /// it is pure data, so every branch of it is exercisable without an MSBuild invocation or a
    /// repository on disk.
    /// </summary>
    /// <param name="noMsBuild">Whether <c>--no-msbuild</c> suppressed the exact resolver.</param>
    /// <param name="status">
    /// This run's extraction outcome. <see langword="null"/> means the code stage did not run at all,
    /// which <c>--no-code</c> is the only thing that causes -- <c>Execute</c> assigns the graph
    /// unconditionally inside its <c>if (!request.NoCode)</c> block and nowhere else -- so the line
    /// this produces names that flag.
    /// </param>
    /// <param name="projectsDetected">How many <c>.csproj</c> the scan found.</param>
    /// <param name="projects">One availability verdict per project in the compiled closure, which is a superset of the detected set.</param>
    /// <param name="owns">
    /// <c>RoslynResolver.Owns</c>, over repository-relative <c>/</c>-separated paths -- the same form
    /// <see cref="RunStatus.Skipped"/> records. <see langword="null"/> when no compilation was attempted.
    /// </param>
    /// <param name="concepts">Everything the generator produced.</param>
    internal static IReadOnlyList<string> Summarize(
        bool noMsBuild,
        RunStatus? status,
        int projectsDetected,
        IReadOnlyList<RoslynProjectReport> projects,
        Func<string, bool>? owns,
        IReadOnlyList<GeneratedConcept> concepts)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(concepts);

        if (status is null)
        {
            return ["the code stage did not run (--no-code), so no source file was read, no call was resolved and no `code` concept was generated. Nothing about the rest of this bundle is affected."];
        }

        var counts = new Dictionary<FileStatus, int>();
        foreach (var (_, fileStatus) in status.Skipped)
        {
            counts[fileStatus] = counts.GetValueOrDefault(fileStatus) + 1;
        }

        var attempted = status.Skipped.Count;
        var analysed = counts.GetValueOrDefault(FileStatus.Extracted) + counts.GetValueOrDefault(FileStatus.PartiallyExtracted);
        var exact = owns is null
            ? 0
            : status.Skipped.Count(f => f.Status is FileStatus.Extracted or FileStatus.PartiallyExtracted && owns(f.Path));
        var compiled = projects.Count(p => p.Availability == RoslynProjectAvailability.Compiled);
        var owned = concepts.Count(c => IsOwned(c.Id.ToString()));
        var reachable = ReachableOwnedConcepts(concepts);

        // Enum order, not dictionary order, and only the values this run actually saw: the segment
        // must be byte-identical for two identical runs, and it must not read "0 skipped, unreadable"
        // on a healthy repository.
        var breakdown = string.Join(
            ", ",
            Enum.GetValues<FileStatus>()
                .Where(counts.ContainsKey)
                .Select(s => $"{Count(counts[s])} {Label(s)}"));

        var files = attempted == 0
            ? "no source file was read"
            : counts.GetValueOrDefault(FileStatus.Extracted) == attempted
                ? $"{Count(attempted)} source file(s) read, all extracted"
                : $"{Count(attempted)} source file(s) read: {breakdown}";

        // Stated in both directions rather than only when it fails. The false case is the one that
        // forbids pruning outright, and it is reachable without any file being unreadable: a circular
        // junction empties the walk's file list (measured on this host -- see the wave-3 report), and
        // the run then writes `overview` alone and exits 0.
        var traversal = status.TraversalComplete
            ? "the traversal visited every eligible file"
            : "THE TRAVERSAL DID NOT COMPLETE -- some eligible files were never visited, so a symbol may have moved into one of them and nothing was pruned";

        // The two halves are separate facts and both are always stated: how many project files exist
        // and how many of them yielded a compilation, then how many of the files that were actually
        // analysed had their calls resolved exactly. The second is asked file by file rather than
        // inferred from the first, so a project reported `Compiled` that owns none of its own files
        // lands in the name-matched count where it belongs.
        var compilations = projectsDetected == 0
            ? "no project file was detected, so no compilation was built"
            : noMsBuild
                ? $"{Count(projectsDetected)} project file(s) detected but none was queried (--no-msbuild)"
                : $"{Count(projectsDetected)} project file(s) detected and {Count(compiled)} of {Count(projects.Count)} in the compiled closure built cleanly";

        var calls = analysed == 0
            ? "no file was analysed, so no call was resolved either way"
            : $"calls resolved exactly for {Count(exact)} of {Count(analysed)} analysed file(s), by name matching for the other {Count(analysed - exact)}";

        var resolution = compilations + "; " + calls;

        var code = owned == 0
            ? "no `code` concept was generated"
            : reachable == owned
                ? $"all {Count(owned)} `code` concept(s) are reachable from `overview`"
                : $"{Count(reachable)} of {Count(owned)} `code` concept(s) are reachable from `overview` -- the others are in the bundle with nothing linking down to them, which `okf validate` does not report because nothing dangles";

        var lines = new List<string> { string.Join("; ", [files, traversal, resolution, code]) + "." };

        // §2.3 asks the output report to name the unanalysed files with their cause -- that is what
        // distinguishes "symbol deleted" from "file not read". Only the wholly-skipped ones: a
        // PartiallyExtracted file WAS read, and it is the steady state of any modern C# repository
        // (the vendored grammar mis-parses an empty collection expression), so naming those would bury
        // the ones that matter under hundreds that do not. Capped, because the count is not bounded by
        // anything: a generated tree can put thousands of files over the cap at once.
        var unanalysed = status.Skipped
            .Where(f => f.Status is not (FileStatus.Extracted or FileStatus.PartiallyExtracted))
            .ToList();

        foreach (var (path, fileStatus) in unanalysed.Take(UnanalysedFilesListed))
        {
            lines.Add($"  - {path}: {Label(fileStatus)}");
        }

        if (unanalysed.Count > UnanalysedFilesListed)
        {
            lines.Add($"  - ... and {Count(unanalysed.Count - UnanalysedFilesListed)} more");
        }

        return lines;
    }

    /// <summary>How many unanalysed files the report names before it stops and gives a remainder count.</summary>
    private const int UnanalysedFilesListed = 10;

    /// <summary>
    /// How many of the <c>code</c> concepts this run produced are reachable from <c>overview</c> by
    /// following the links the run actually wrote.
    ///
    /// <para><b>Walked, not derived.</b> The spine is built one level at a time -- <c>overview</c>
    /// links only the <c>packages/</c> and <c>docs/</c> concepts, each package links down to the
    /// namespaces its <c>Compile</c> items declare into, and each of those links one level further --
    /// so "is the code family reachable" is a question about several hops, not about any single rule
    /// this method could restate. Restating it is also what would make it wrong: with no
    /// source-ownership map <b>and</b> no package manifest, the two conditions that break the spine
    /// completely, no note fires anywhere, and a re-derivation would have to reproduce
    /// <c>AttributePackages</c>' three §5.1 rules to know it. Following
    /// <see cref="LinkScanner.ExtractLinks"/> over the produced bodies instead gives the validator's
    /// own answer over this run's own bytes.</para>
    /// </summary>
    /// <param name="concepts">Everything the generator produced.</param>
    private static int ReachableOwnedConcepts(IReadOnlyList<GeneratedConcept> concepts)
    {
        var byId = new Dictionary<string, GeneratedConcept>(StringComparer.Ordinal);
        foreach (var concept in concepts)
        {
            byId[concept.Id.ToString()] = concept;
        }

        if (!byId.ContainsKey(OverviewId))
        {
            return 0;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { OverviewId };
        var queue = new Queue<string>();
        queue.Enqueue(OverviewId);
        var reached = 0;

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (IsOwned(id))
            {
                reached++;
            }

            foreach (var link in LinkScanner.ExtractLinks(byId[id].Document.Body))
            {
                // §6.1's bundle-root form is the only one this producer writes, and the only one a
                // containment link may take; a relative or external target is somebody else's link.
                if (link.Kind != LinkKind.Absolute)
                {
                    continue;
                }

                var target = link.Target.Trim()[1..];
                if (byId.ContainsKey(target) && seen.Add(target))
                {
                    queue.Enqueue(target);
                }
            }
        }

        return reached;
    }

    /// <summary>Whether <paramref name="id"/> falls under the prefix this producer owns and prunes.</summary>
    private static bool IsOwned(string id) =>
        string.Equals(id, OwnedPrefix, StringComparison.Ordinal)
        || id.StartsWith(OwnedPrefix + "/", StringComparison.Ordinal);

    /// <summary>One number, culture-invariant, because this report is compared between runs and between hosts.</summary>
    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The report's name for one file outcome. A member added to <see cref="FileStatus"/> renders as
    /// its own enum name rather than as a wrong label -- deliberately not a compile break, because the
    /// value would still be counted and reported honestly, just less readably.
    /// </summary>
    /// <param name="status">The outcome to name.</param>
    private static string Label(FileStatus status) => status switch
    {
        FileStatus.Extracted => "extracted",
        FileStatus.PartiallyExtracted => "partially extracted",
        FileStatus.SkippedTooLarge => "skipped, over --max-file-size",
        FileStatus.SkippedEncoding => "skipped, not decodable as text",
        FileStatus.SkippedDepth => "skipped, too deeply nested",
        FileStatus.SkippedUnreadable => "skipped, unreadable",
        FileStatus.SkippedSymlink => "skipped, a symbolic link",
        _ => status.ToString(),
    };

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

            // Zero `Compile` items is not a refusal, and `Any` cannot tell the two apart: it is false
            // for an empty set exactly as it is for a set whose every member the gate refused. A
            // packaging or targets-only project -- Microsoft.Build.NoTargets, or
            // EnableDefaultCompileItems=false with nothing added back -- declares none, compiles
            // clean, and owns nothing, all of it healthy. The note below would report a refusal that
            // never happened and a `## Calls` degradation for a project with no links to lose.
            if (compiledInputs.CompileFiles.Count == 0)
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
