// SPDX-License-Identifier: LGPL-3.0-or-later
using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OKF4net;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;
using OkfProducer.Core.Validation;

namespace OkfProducer.Cli;

/// <summary>The four services a run resolves from the host, gathered so the pipeline takes one argument rather than a container.</summary>
/// <param name="Scanner">Reads the repository.</param>
/// <param name="Generator">Turns the scan (and the code graph) into concepts.</param>
/// <param name="Writer">Writes, prunes and indexes the bundle.</param>
/// <param name="Validator">Backs the <c>validate</c> verb.</param>
internal sealed record ProducerServices(
    IRepositoryScanner Scanner,
    IConceptGenerator Generator,
    IBundleWriter Writer,
    IBundleValidationRunner Validator);

/// <summary>
/// The <c>okfgen</c> command surface. Every verb's logic runs through
/// <see cref="Run(string[], TextWriter, TextWriter)"/> against the two writers it is given, never
/// against <see cref="Console"/> directly, so the whole CLI -- flags, exit codes, stdout and stderr --
/// is exercised in-process by the test suite rather than by spawning a binary. That is the same shape
/// <c>OkfCli.Run</c> uses in <c>src/OKF4net.Cli</c>.
/// </summary>
public static class OkfgenCli
{
    /// <summary>The prefix every note carries on stderr. Notes are what a run reports about what it could not do; they are not errors and never change the exit code.</summary>
    private const string NotePrefix = "note: ";

    /// <summary>
    /// Parses <paramref name="args"/> and runs the requested verb, writing ordinary output to
    /// <paramref name="output"/> and errors, notes and parse failures to <paramref name="error"/>.
    /// </summary>
    /// <param name="args">The command line, as the process received it.</param>
    /// <param name="output">Where results are written.</param>
    /// <param name="error">Where errors and notes are written.</param>
    /// <returns>The process exit code: <c>0</c> on success, non-zero otherwise.</returns>
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddSingleton<IRepositoryScanner, RepositoryScanner>();
        builder.Services.AddSingleton<IConceptGenerator, ConceptGenerator>();
        builder.Services.AddSingleton<IBundleWriter, BundleWriter>();
        builder.Services.AddSingleton<IBundleValidationRunner, BundleValidationRunner>();
        using var host = builder.Build();

        var services = new ProducerServices(
            host.Services.GetRequiredService<IRepositoryScanner>(),
            host.Services.GetRequiredService<IConceptGenerator>(),
            host.Services.GetRequiredService<IBundleWriter>(),
            host.Services.GetRequiredService<IBundleValidationRunner>());

        return BuildRootCommand(services, output, error)
            .Parse(args)
            .Invoke(new InvocationConfiguration { Output = output, Error = error });
    }

    private static RootCommand BuildRootCommand(ProducerServices services, TextWriter output, TextWriter error)
    {
        var repoOption = new Option<string>("--repo") { Description = "Root of the repository to scan", Required = true };
        var outOption = new Option<string>("--out") { Description = "Root of the OKF bundle to write", Required = true };
        var updateOption = new Option<bool>("--update") { Description = "Allow writing into a non-empty --out. Concepts this run does not generate are preserved, except under the `code` prefix, where a concept the previous run claimed and this one no longer produces is pruned." };
        var resetOption = new Option<bool>("--reset") { Description = "Delete and recreate --out before writing" };
        var forceOption = new Option<bool>("--force") { Description = "Alias for --reset" };

        var repoUrlOption = new Option<string>("--repo-url")
        {
            Description =
                "Base URL of the repository, e.g. https://github.com/owner/repo. With it and a ref, every code "
                + "concept carries a `resource` permalink to its declaration; without both, no `resource` is "
                + "emitted at all -- a repository-relative path would be resolved against the concept's own "
                + "directory by the validator and miss for every code concept.",
        };

        var revOption = new Option<string>("--rev")
        {
            Description =
                "The git ref --repo-url permalinks are built against. Defaults to the current branch name, never "
                + "a commit sha -- a sha would rewrite every code concept's `resource` on the next commit. On a "
                + "detached HEAD there is no branch name to read, so this becomes required for permalinks.",
        };

        var checkOption = new Option<bool>("--check") { Description = BundleDrift.CheckDescription };

        var includeTestsOption = new Option<bool>("--include-tests")
        {
            Description = "Include test projects and `test`/`tests`/`spec` directories in the code stage. Off by default: on a repository like this one they triple the bundle without telling an agent anything new.",
        };

        var includeInternalOption = new Option<bool>("--include-internal")
        {
            Description = "Emit `internal` declarations too. Off by default, so scope is a visibility filter rather than a hard-coded path convention.",
        };

        var noCodeOption = new Option<bool>("--no-code")
        {
            Description = "Skip the code-graph stage entirely: overview, packages and docs only, exactly as this producer behaved before the stage existed. Generates no `code` concept, and therefore prunes none either.",
        };

        var noMsBuildOption = new Option<bool>("--no-msbuild")
        {
            Description =
                "Do not run `dotnet msbuild` on the scanned repository, and skip the Roslyn resolver that "
                + "depends on it. Generating from a repository otherwise EVALUATES its MSBuild logic, which "
                + "means executing it: Directory.Build.props/targets, whatever they Import, targets hooked on "
                + "ResolveReferences, inline code tasks, and any switch a Directory.Build.rsp adds to the "
                + "invocation, all as you. Pass this for a repository you would not be willing to build. It "
                + "costs two things: call links then come from name matching alone, so an ambiguous name is "
                + "left unlinked rather than resolved exactly; and there is no source-ownership map, so NO "
                + "packages -> namespace containment link is emitted (under --update that overwrites the ones "
                + "a previous run wrote). It does not make the run process-free: `git` still runs in the "
                + "scanned tree, as it does on every run.",
        };

        var maxFileSizeOption = new Option<long>("--max-file-size")
        {
            // The qualification is the whole point of this text and is pinned by
            // CliTests.The_help_keeps_the_clauses_that_make_its_two_hazard_sentences_true. The
            // unqualified version ("A larger file is skipped and counted, which makes the run partial")
            // was false of the Roslyn half of the code stage, and shipped in four places.
            Description =
                "Largest source file, in bytes, the code stage will read -- by both engines, which enforce it "
                + "differently. The tree-sitter extractor skips a larger file and counts it, which makes the run "
                + "partial: the concepts that file owned are then never pruned, because this run cannot vouch for "
                + "their absence. The Roslyn stage applies the same cap to the Compile items MSBuild reports, but "
                + "drops an over-cap item silently: for a file the scan also walked the counted skip covers it; "
                + "for one it did not (a linked out-of-repository source, a generated file under obj/) nothing "
                + "names the dropped file. What the drop costs is not stated here because it varies -- any "
                + "consequence this run can see is reported by its per-project notes instead.",
            DefaultValueFactory = _ => ExtractionLimits.Default.MaxFileBytes,
        };

        var generateCommand = new Command("generate", "Generate an OKF bundle from a repository")
        {
            Options =
            {
                repoOption, outOption, updateOption, resetOption, forceOption,
                repoUrlOption, revOption, checkOption,
                includeTestsOption, includeInternalOption, noCodeOption, noMsBuildOption, maxFileSizeOption,
            },
        };

        generateCommand.SetAction(parseResult =>
        {
            var reset = parseResult.GetValue(resetOption) || parseResult.GetValue(forceOption);
            var update = parseResult.GetValue(updateOption);
            var check = parseResult.GetValue(checkOption);
            var maxFileBytes = parseResult.GetValue(maxFileSizeOption);

            if (maxFileBytes <= 0)
            {
                error.WriteLine("error: --max-file-size must be a positive number of bytes.");
                return 1;
            }

            if (check && reset)
            {
                // Rejected rather than ignored: --check never writes to --out, so honouring --reset
                // would mean deleting nothing while the operator believes a reset happened, and
                // ignoring it silently would mean the same thing one run later.
                error.WriteLine("error: --check never writes to --out, so it cannot be combined with --reset/--force. Drop one of them.");
                return 1;
            }

            if (check && parseResult.GetValue(noCodeOption))
            {
                // Rejected for the same reason as --check --reset, and it is the more dangerous of the
                // two because it fails silently rather than doing nothing. --check regenerates over a
                // COPY of the bundle; with --no-code the regeneration produces no `code` concept and no
                // manifest, so every `code/` file is copied forward untouched, cannot differ, and the
                // manifest on the copy stays byte-identical. The run exits 0 and prints "No drift" over
                // a `code/` family that may be arbitrarily stale -- and a CI gate keyed on --check then
                // stays green for ever. A note would not help: a note never changes the exit code.
                error.WriteLine(
                    "error: --check cannot be combined with --no-code. --check compares the bundle against a regeneration of it,"
                    + " and a regeneration that skips the code stage produces no `code` concept at all -- so every `code/` concept"
                    + " is copied forward untouched, cannot differ, and the check would report no drift however stale they are."
                    + " Drop --no-code to check the whole bundle, or drop --check.");
                return 1;
            }

            var repoUrl = Trimmed(parseResult.GetValue(repoUrlOption));
            if (repoUrl is not null && !GenerateOptions.TryPermalinkBase(repoUrl, out _))
            {
                // Refused here rather than tolerated, because the failure downstream is silent: the
                // generator returns no permalink for a value that is not an absolute http(s) URL, so
                // `--repo-url github.com/o/r` or `--repo-url git@github.com:o/r` -- the two forms a
                // forge displays and a user pastes -- would produce a successful-looking run
                // containing not one `resource`. A detached HEAD gets a note instead of an error
                // because nothing the operator typed is wrong there; here it is.
                //
                // Through GenerateOptions.TryPermalinkBase, which is the generator's OWN rule and not
                // a second copy of it, so this boundary check cannot become stricter than what it
                // guards -- see that method for why it is public.
                error.WriteLine(
                    $"error: --repo-url '{repoUrl}' is not an absolute http/https URL, so no permalink can be built from it"
                    + " and every code concept would silently lose its `resource`. Pass the form the forge shows in the"
                    + " address bar, e.g. https://github.com/owner/repo.");
                return 1;
            }

            // --check always regenerates over a COPY under Update -- the only policy that runs the
            // real regeneration path, field preservation and pruning included (§6.2).
            //
            // Hoisted out of the argument list below rather than sitting inside it. A comment embedded
            // between two arguments makes Roslyn's formatter rewrite the newline trivia around it, and
            // it rewrites it to Environment.NewLine -- so on Windows the same bytes pass
            // `dotnet format --verify-no-changes` with CRLF endings and fail with LF, which is what a
            // checkout under core.autocrlf=false, or any tool that rewrites the file with LF, leaves
            // behind. `producers/` is outside CI, so nothing catches that. Out here the comment is
            // ordinary statement-level trivia and no rule has an opinion about the line ending.
            var policy = check
                ? WritePolicy.Update
                : reset ? WritePolicy.Reset : update ? WritePolicy.Update : WritePolicy.RequireEmpty;

            var request = new GenerateRequest(
                RepoPath: parseResult.GetValue(repoOption)!,
                OutPath: parseResult.GetValue(outOption)!,
                Policy: policy,
                RepoUrl: repoUrl,
                Rev: Trimmed(parseResult.GetValue(revOption)),
                Check: check,
                IncludeTests: parseResult.GetValue(includeTestsOption),
                IncludeInternal: parseResult.GetValue(includeInternalOption),
                NoCode: parseResult.GetValue(noCodeOption),
                MaxFileBytes: maxFileBytes,
                NoMsBuild: parseResult.GetValue(noMsBuildOption));

            return Generate(request, services, output, error);
        });

        var okfOption = new Option<string>("--okf") { Description = "Root of the OKF bundle to validate", Required = true };

        var validateCommand = new Command("validate", "Validate an OKF bundle")
        {
            Options = { okfOption },
        };

        validateCommand.SetAction(parseResult =>
        {
            var okfPath = parseResult.GetValue(okfOption)!;

            try
            {
                var outcome = services.Validator.Validate(okfPath);

                foreach (var line in outcome.DiagnosticLines)
                {
                    output.WriteLine(line);
                }

                output.WriteLine($"{outcome.ErrorCount} error(s), {outcome.WarningCount} warning(s).");
                return outcome.IsConformant ? 0 : 1;
            }
            catch (BundleLoadException ex)
            {
                error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        });

        return new RootCommand("okfgen -- generate and validate OKF bundles from a repository")
        {
            Subcommands = { generateCommand, validateCommand },
        };
    }

    private static int Generate(GenerateRequest request, ProducerServices services, TextWriter output, TextWriter error)
    {
        void Note(string text) => error.WriteLine(NotePrefix + text);

        if (!Directory.Exists(request.RepoPath))
        {
            error.WriteLine($"error: repository path '{request.RepoPath}' does not exist or is not a directory.");
            return 1;
        }

        try
        {
            return request.Check
                ? Check(request, services, output, error, Note)
                : Write(request, services, output, error, Note);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OkfException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Write(GenerateRequest request, ProducerServices services, TextWriter output, TextWriter error, Action<string> note)
    {
        var result = ExecuteAndReport(request, services, note);

        output.WriteLine($"Wrote {result.Written.ToString(CultureInfo.InvariantCulture)} concept(s) to {request.OutPath}.");

        if (result.Pruned.Count > 0)
        {
            // Not "whose declarations are gone from the source": that is a claim this producer cannot
            // make. What it established is that the previous manifest claimed the id, this run did not
            // produce it, and every file it came from was read in full or is gone. A declaration that
            // merely left this run's scope satisfies all three -- which is why a narrowed scope refuses
            // the prune outright (BundleWriter.Narrowing) instead of being papered over here.
            output.WriteLine($"Pruned {result.Pruned.Count.ToString(CultureInfo.InvariantCulture)} concept(s) the previous run claimed and this one no longer produces.");
            foreach (var id in result.Pruned)
            {
                output.WriteLine($"  - {id}");
            }
        }

        foreach (var (id, failure) in result.Failures)
        {
            error.WriteLine($"error: {id}: {failure}");
        }

        return result.Failures.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// One generation, with <b>everything</b> the run had to say about what it could not do forwarded
    /// to <paramref name="note"/>: the notes raised during generation (which reach the sink directly)
    /// and <see cref="WriteResult.Notes"/>, which the writer can only hand back at the end.
    ///
    /// <para><b>Both callers go through here, and that is the point.</b> The two paths used to forward
    /// separately, and <c>--check</c> forgot: it took only the concept count off the run and dropped
    /// the reconciliation notes -- exactly the sentences saying a clean check was weakened.
    ///
    /// <para>The case where a note is the <i>only</i> signal that exists is a hand-written concept under
    /// the owned prefix that no manifest claims. Pruning only considers ids a previous manifest claimed,
    /// and staging only overwrites files the generator produced, so such a file is copied forward
    /// untouched and never regenerated: it <i>cannot</i> differ. <c>--check</c> exits 0 and reports no
    /// drift for ever, while the bundle carries a concept this producer will never prune.</para>
    ///
    /// <para>A degraded run is <i>not</i> that case, and this doc comment used to say it was. A source
    /// file over <c>--max-file-size</c> does leave its stale <c>code/</c> concepts held back rather than
    /// pruned, but the file also drops out of the manifest's extracted-file list, so
    /// <c>.okfgen-manifest.json</c> differs and <c>--check</c> exits 1 naming it. There the notes say
    /// <i>why</i> the run was degraded, not <i>whether</i> anything is wrong. One funnel, so a third
    /// caller cannot reintroduce the drop.</para>
    /// </summary>
    private static WriteResult ExecuteAndReport(GenerateRequest request, ProducerServices services, Action<string> note)
    {
        var result = GenerateRun.Execute(request, services, note);

        foreach (var text in result.Notes)
        {
            note(text);
        }

        return result;
    }

    private static int Check(GenerateRequest request, ProducerServices services, TextWriter output, TextWriter error, Action<string> note)
    {
        // The regeneration's own write failures, which this used to drop on the floor. `--check` runs
        // a real generation into the copy, so it can fail to write a concept exactly as `generate`
        // can -- and a copy missing a concept is a comparison whose result means nothing, in the
        // direction that reads as clean: if the bundle is missing the same concept, neither side has
        // it, no difference is found, and the run exits 0 over a regeneration that did not work.
        // Reported, and counted into the exit code, for the same reason ConceptsRegenerated is.
        //
        // KEPT although nothing in this suite turns it red -- verified by deleting both the rendering
        // below and the `failures.Count` term of the exit code and watching all 519 tests stay green.
        // The argument that offered to drop it as untested plumbing was nevertheless wrong on the fact
        // it rested on. That argument said `failures` is provably empty
        // for every reachable --check: it is not. WriteResult.Failures is fed (BundleWriter.Write)
        // from BundleConceptWriter.WriteConcept, whose RunTool converts OkfException, ArgumentException,
        // IOException, UnauthorizedAccessException and DecoderFallbackException alike into an "Error:"
        // string. The last four are environmental, not id-shaped: a full volume, an antivirus or
        // indexer holding a handle, an encoding fault reading an existing concept. And --check is more
        // exposed to those than an ordinary run, not less -- its generation writes into a GUID-named
        // temporary copy and stages beside it, both under Path.GetTempPath() (BundleDrift.Check,
        // BundleWriter.CreateStagingDirectory), so the writes land on whatever volume that names and
        // behind whatever watches it, rather than beside the operator's --out.
        //
        // Reachable, then, but not deterministically constructible from a test -- which is why there is
        // no witness for it, and why deleting it would be the wrong repair: deleting it restores an
        // exit 0 over a regeneration that failed.
        var failures = new List<(ConceptId Id, string Error)>();

        // The count is the floor DriftReport refuses to report clean without: a composition that
        // regenerates nothing would otherwise leave the copy identical to the bundle and pass for ever.
        var report = BundleDrift.Check(
            request.OutPath,
            request.RepoPath,
            copy =>
            {
                var result = ExecuteAndReport(request with { OutPath = copy }, services, note);
                failures.AddRange(result.Failures);
                return result.Written;
            });

        foreach (var difference in report.Differences)
        {
            output.WriteLine($"drift: {difference}");
        }

        foreach (var (id, failure) in failures)
        {
            error.WriteLine($"error: {id}: {failure}");
        }

        output.WriteLine(report.IsClean
            ? $"No drift: {request.OutPath} is what regenerating it produces."
            : $"{report.Differences.Count.ToString(CultureInfo.InvariantCulture)} difference(s) between {request.OutPath} and what regenerating it produces.");

        if (report.FieldsExcluded)
        {
            // "Every other FIELD", where this used to say "every other file and field". The file half
            // stopped being true the moment --check learned to skip a link, and a note that overstates
            // its own coverage is the thing this run exists to avoid. What was and was not compared at
            // FILE granularity is said by the link notes below, which name each one.
            note("the repository has no HEAD commit to stamp from, so `generated.at` and `revision` on `overview` were excluded from the comparison -- both fall back to the wall clock there and cannot be reproduced by regenerating. Every other field was compared byte for byte, on every file compared.");
        }

        foreach (var link in report.LinksSkipped.Except(report.LinksReportedAsDrift, StringComparer.Ordinal))
        {
            // A note rather than a difference, for the links no difference already names: both sides
            // are listed by the same walk and that walk stops at a reparse point, so a link's own path
            // is absent from the bundle side's file set, and counting every link as drift on that
            // ground alone would fail a check over a bundle nobody had touched. What it does change is
            // which property a clean result asserts, and that is what this says.
            //
            // THE EXCEPT IS THE POINT, and it is here because two earlier versions of this comment
            // were wrong in the same direction. The first claimed the link "is on both sides"; the
            // second claimed its path could not be reported as a difference at all. Neither holds:
            // the copy never holds the link, so where the regeneration writes a file at exactly that
            // path, Compare reports it -- and printing this note beside that line handed the operator
            // two sentences about one path, one saying it was missing from the bundle and one saying
            // it was never compared. Those links carry their whole story in the drift line now, so
            // they are left out here. Links whose CHILDREN differ are not: the note names the
            // directory, the differences name the files under it, and both are true.
            // See DriftReport.LinksSkipped and DriftReport.LinksReportedAsDrift.
            note($"'{link}' is a symbolic link or junction, so it was neither copied nor compared -- what hangs off the far end was never part of this bundle. A clean result here is a statement about everything else.");
        }

        return report.ExitCode != 0 || failures.Count > 0 ? 1 : 0;
    }

    /// <summary>An option's value with surrounding whitespace removed, or <see langword="null"/> when it was absent or blank.</summary>
    private static string? Trimmed(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;
}
