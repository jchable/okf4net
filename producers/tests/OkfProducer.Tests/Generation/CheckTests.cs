// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Generation;
using Xunit.Abstractions;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// §6.2's <c>--check</c>, against the committed golden bundle in
/// <c>producers/tests/OkfProducer.Tests/fixtures/</c>.
///
/// <para><b>The golden here follows the OPPOSITE discipline to <c>tests/fixtures/</c>.</b> That
/// directory holds byte-exact captures of a reference implementation and a hard rule forbids editing
/// them to make a test pass. This one captures <b>our own</b> output: it is regenerable by
/// construction and it <i>must</i> be regenerated whenever the generator changes intentionally, with
/// the diff reviewed as part of that change. <c>fixtures/README.md</c> states it in full; the
/// regeneration switch is <see cref="UpdateGoldenVariable"/>, read by
/// <see cref="Check_passes_on_an_unchanged_bundle"/> below.</para>
///
/// <para><b>Every test here runs against a copy of the fixture repository placed outside any git
/// checkout</b> (see <c>ProducerFixture.CopyRepoOutsideGit</c>), so §6.2's outside-git exclusion --
/// <c>generated.at</c> and <c>revision</c>, on <c>overview</c> alone -- is the case the golden is
/// captured under. That means the golden does <b>not</b> exercise the HEAD-commit stamp at all;
/// <see cref="DeterminismTests"/> covers that path, and
/// <see cref="Inside_a_git_repository_the_stamp_fields_are_compared_like_any_other"/> covers the
/// other half of the exclusion rule.</para>
/// </summary>
public class CheckTests(ITestOutputHelper output)
{
    /// <summary>Set this to <c>1</c> to rewrite the golden bundle from the fixture repository instead of asserting against it.</summary>
    private const string UpdateGoldenVariable = "OKFGEN_UPDATE_GOLDEN";

    /// <summary>
    /// What update mode says on its way out. Stated as a refusal to assert rather than as a passing
    /// test, because in update mode the expected side is written by the very harness that produced the
    /// actual side: the comparison below would be a tautology, and a tautology reported green is
    /// exactly how a stale variable in someone's shell disarms a golden without anyone noticing.
    /// </summary>
    private const string GoldenRewrittenNotice =
        UpdateGoldenVariable + "=1: the golden bundle was REWRITTEN from the fixture repository, and this run"
        + " proves nothing -- the expected side was just produced by the same harness as the actual side."
        + " Review `git diff producers/tests/OkfProducer.Tests/fixtures/golden`, then re-run WITHOUT the"
        + " variable to actually check it.";

    [Fact]
    public void Check_passes_on_an_unchanged_bundle()
    {
        using var workspace = ProducerFixture.CopyRepoOutsideGit();

        // Regeneration is a mode of THIS test rather than a separate fact that does nothing when the
        // variable is unset: a fact whose only job is to be skipped is a green result that proves
        // nothing, and this plan has already shipped several assertions incapable of failing.
        if (Environment.GetEnvironmentVariable(UpdateGoldenVariable) == "1")
        {
            ProducerFixture.RegenerateGolden(workspace);

            // Written to the test output AND failed, because a line printed by a green test is a line
            // nobody reads -- and "silently disarmed" is the exact failure mode this guards against.
            // xunit 2.9 has no dynamic skip (Assert.Skip is v3), so a deliberate failure carrying the
            // explanation is the only mechanism here that a passing run cannot swallow. The message is
            // an instruction, not an error report: regenerate, read the diff, re-run without the
            // variable to get a real green.
            output.WriteLine(GoldenRewrittenNotice);
            Assert.Fail(GoldenRewrittenNotice);
        }

        var report = RunCheck(workspace, ProducerFixture.GoldenBundle);

        Assert.True(report.IsClean, Explain(report));
        Assert.Equal(0, report.ExitCode);

        // The golden was captured outside git, so the two-field projection was in play. Asserted, not
        // assumed: if a future TMPDIR landed inside a git checkout the comparison above would silently
        // become the stricter one and this fixture would stop covering the case it exists for.
        Assert.True(report.FieldsExcluded);
    }

    [Fact]
    public void Check_fails_when_the_source_changed()
    {
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        ProducerFixture.EditSource(RepoIn(workspace), "src/Scanner.cs", ProducerFixture.InsertAtEndOfLastType(
            "\n    /// <summary>Added by a test.</summary>\n    public void Added()\n    {\n    }\n"));

        var report = RunCheck(workspace, ProducerFixture.GoldenBundle);

        Assert.NotEqual(0, report.ExitCode);
        Assert.Contains(report.Differences, d => d.StartsWith("code/csharp/n/scanner/added.md:", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_ignores_source_text_that_declares_nothing()
    {
        // The companion to the test above, and the reason --check is not a hash of the repository: a
        // trailing comment changes the source bytes and changes nothing the bundle records. Without
        // this, a producer that simply digested every source file would pass the drift test above just
        // as happily as a correct one.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        File.AppendAllText(Path.Combine(workspace.Path, ProducerFixture.RepoDirectoryName, "src/Scanner.cs"), "\n// nudge\n");

        var report = RunCheck(workspace, ProducerFixture.GoldenBundle);

        Assert.True(report.IsClean, Explain(report));
    }

    [Fact]
    public void Check_reports_a_concept_deleted_from_the_bundle_by_hand()
    {
        // Deleting from the BUNDLE exercises the "produced by regenerating, but missing from the
        // bundle" branch: the source still declares `Normalize`, so the run writes it back into the
        // copy and the copy has a file the bundle lacks. A check that iterated only the BUNDLE's files
        // would stay green here, never having looked at what the regeneration produced.
        //
        // Its mirror -- a concept the bundle still holds that the regeneration no longer produces --
        // is the likelier drift in practice and has its own test below.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        using var bundle = ProducerFixture.CopyGoldenBundle();

        File.Delete(Path.Combine(bundle.Path, "code/csharp/n/scanner/normalize.md"));

        var report = RunCheck(workspace, bundle.Path);

        Assert.Contains(
            report.Differences,
            d => d.StartsWith("code/csharp/n/scanner/normalize.md:", StringComparison.Ordinal)
                && d.Contains("missing from the bundle", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_reports_a_concept_whose_symbol_was_deleted_but_never_regenerated()
    {
        // THE MOST LIKELY REAL DRIFT THERE IS: someone deletes a method and forgets to regenerate, so
        // the bundle keeps a concept pointing at code that no longer exists -- an agent querying it
        // gets a confidently wrong answer, which §6.3 calls worse than no answer.
        //
        // It is also the only branch of the comparison nothing else reaches. `Gone()` is deleted from
        // the SOURCE and the golden is left untouched, so the regeneration over the copy prunes
        // `gone.md` and the bundle is the side holding a file the run does not produce.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        ProducerFixture.DeleteGoneMethod(RepoIn(workspace));

        var report = RunCheck(workspace, ProducerFixture.GoldenBundle);

        Assert.Contains(
            report.Differences,
            d => d.StartsWith("code/csharp/n/scanner/gone.md:", StringComparison.Ordinal)
                && d.Contains("regenerating does not produce it", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_does_not_report_a_manual_description_as_drift()
    {
        // The contradiction §6.2 had to resolve: regenerating into an EMPTY temporary directory has
        // nothing to preserve, so every concept with a manual description would read as drift, for
        // ever -- precisely the concepts people care most about. --check copies the existing bundle
        // and runs the update path over it, so field preservation runs exactly as it does in an
        // ordinary regeneration.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        using var bundle = ProducerFixture.CopyGoldenBundle();

        SetDescription(bundle.Path, "code/csharp/n/scanner/scan", "Hand written.", DescriptionResolver.ManualLabel);

        var report = RunCheck(workspace, bundle.Path);

        Assert.True(report.IsClean, Explain(report));
    }

    [Fact]
    public void A_manual_description_is_still_in_the_bundle_after_a_regeneration()
    {
        // Why this exists beside the test above: that one would pass just as well against a producer
        // with NO field preservation at all, as long as it was consistently bad -- both sides would
        // re-derive the same description and the bytes would match. This one pins the property that
        // makes the other one meaningful, by regenerating over the bundle for real and reading the
        // text back.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        using var bundle = ProducerFixture.CopyGoldenBundle();

        SetDescription(bundle.Path, "code/csharp/n/scanner/scan", "Hand written.", DescriptionResolver.ManualLabel);
        ProducerFixture.Run(RepoIn(workspace), bundle.Path);

        var written = OkfDocument.Parse(File.ReadAllText(Path.Combine(bundle.Path, "code/csharp/n/scanner/scan.md")));

        Assert.Equal("Hand written.", written.Frontmatter.Description);
        Assert.Equal(DescriptionResolver.ManualLabel, written.Frontmatter.Get(DescriptionResolver.DescriptionSourceKey)?.AsDisplayString());
        Assert.Contains("Hand written.", written.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Outside_a_git_repository_the_projection_removes_two_fields_and_not_a_third()
    {
        // §6.2's exclusion list is CLOSED. The two fields it names are forgiven; every other field of
        // the very same file is not. Without the second half of this test, "excluded outside git"
        // could have been implemented as "skip overview.md entirely" and nothing would have noticed.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        using var bundle = ProducerFixture.CopyGoldenBundle();

        Edit(bundle.Path, "overview", fm =>
        {
            fm.AsMapping().Insert("revision", new OKF4net.Yaml.YamlString("0000000000000000000000000000000000000000"));
            fm.AsMapping().Get("generated")?.AsMapping()?.Insert("at", new OKF4net.Yaml.YamlString("1999-12-31T23:59:59Z"));
        });

        Assert.True(RunCheck(workspace, bundle.Path).IsClean);

        Edit(bundle.Path, "overview", fm => fm.AsMapping().Insert("title", new OKF4net.Yaml.YamlString("Something else")));

        Assert.Contains(RunCheck(workspace, bundle.Path).Differences, d => d.StartsWith("overview.md:", StringComparison.Ordinal));
    }

    [Fact]
    public void Inside_a_git_repository_the_stamp_fields_are_compared_like_any_other()
    {
        // The other row of §6.2's table, and the reason the exclusion is context-gated rather than
        // unconditional: inside a repository `generated.at` and `revision` come from the HEAD commit,
        // so they ARE reproducible and forgiving them would blind the check to a bundle generated at
        // a different commit than the one checked out.
        ProducerFixture.RequireGit();

        using var workspace = ProducerFixture.CopyRepoIntoGit();
        var bundlePath = Path.Combine(workspace.Path, "bundle");
        ProducerFixture.Run(RepoIn(workspace), bundlePath);

        var clean = RunCheck(workspace, bundlePath);

        Assert.True(clean.IsClean, Explain(clean));
        Assert.False(clean.FieldsExcluded);

        Edit(bundlePath, "overview", fm =>
            fm.AsMapping().Insert("revision", new OKF4net.Yaml.YamlString("0000000000000000000000000000000000000000")));

        Assert.Contains(RunCheck(workspace, bundlePath).Differences, d => d.StartsWith("overview.md:", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_compares_the_generation_manifest_too()
    {
        // The manifest is written output like any other file, and it is the licence the NEXT run
        // deletes by (§6.3 rule 2): one that no longer describes the bundle is a standing permission
        // to delete whatever later appears at an id it still claims. Comparing only `*.md` would leave
        // exactly that unguarded, so the comparison walks every file, dotted names included.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        using var bundle = ProducerFixture.CopyGoldenBundle();

        var manifest = Path.Combine(bundle.Path, GenerationManifest.FileName);
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("code/csharp/n/scanner/gone", "code/csharp/n/scanner/ghost", StringComparison.Ordinal));

        Assert.Contains(
            RunCheck(workspace, bundle.Path).Differences,
            d => d.StartsWith(GenerationManifest.FileName + ":", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_neither_copies_nor_compares_what_a_link_in_the_bundle_points_at()
    {
        // --check ROUTED AROUND every containment gate in BundleWriter, and it did it by removing the
        // thing they gate against. The copy was made with Directory.EnumerateDirectories/EnumerateFiles
        // (AllDirectories), which descend a junction -- so the far side of a link was MATERIALIZED into
        // the copy as ordinary directories and ordinary files. The regeneration then ran against a
        // directory in which somebody else's file genuinely was inside the root: ResolveInsideRoot said
        // yes, because by then it was true, and the run emitted the notes those gates exist to prevent
        // -- "'code/x/report' sits under the owned prefix 'code' but no manifest claims it" -- about a
        // file outside the bundle, in the one mode that writes nothing to the bundle at all.
        //
        // The notes reach the operator: OkfgenCli.Check forwards WriteResult.Notes through
        // ExecuteAndReport. So this is a false statement printed to a human about a file that is not
        // theirs, not merely an internal one.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        using var bundle = ProducerFixture.CopyGoldenBundle();

        var outside = Path.Combine(workspace.Path, "notes-outside-the-bundle");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "report.md"), "---\ntype: Note\ntitle: Mine\n---\n\nsomeone's notes\n");

        var link = Path.Combine(bundle.Path, "code", "x");
        ProducerFixture.CreateDirectoryLink(link, outside);
        Assert.True(File.Exists(Path.Combine(link, "report.md")), "the bundle cannot see through the link, so this fixture proves nothing.");

        var repo = RepoIn(workspace);
        var notes = new List<string>();
        var report = BundleDrift.Check(bundle.Path, repo, copy =>
        {
            var run = ProducerFixture.Run(repo, copy);
            notes.AddRange(run.Write.Notes);

            // The copy is where the damage was done, so it is the copy that is asserted about: the
            // far-side file must not be in it at all. Checked inside the callback because Check
            // deletes the copy in a finally.
            Assert.False(
                Directory.Exists(Path.Combine(copy, "code", "x")),
                "the copy materialised the far side of a link as a real directory, which is what let every containment gate pass.");

            return run.Write.Written;
        });

        Assert.DoesNotContain(notes, n => n.Contains("code/x/report", StringComparison.Ordinal));

        // And the ORIGINAL side skips it too, which is not a second nicety but the thing that keeps
        // this from inventing drift: skip in the copy alone and every far-side path reads as "in the
        // bundle, but regenerating does not produce it".
        Assert.DoesNotContain(report.Differences, d => d.Contains("code/x/", StringComparison.Ordinal));
        Assert.True(report.IsClean, Explain(report));

        // Reported rather than passed over in silence: a check that quietly stops looking at part of a
        // bundle is the failure this whole file is built against. Not a difference -- both sides are
        // listed by the same walk and it stops at a reparse point, so the link's own path is in
        // neither file set and counting it would fail a check over a bundle nobody had touched, which
        // the IsClean assertion above pins.
        //
        // THE CLEAN RESULT ABOVE DEPENDS ON THE FIXTURE'S CHOICE OF `code/x`, and this comment used to
        // credit it to a symmetry that does not exist ("a link is on both sides"). The copy never holds
        // the link: CopyDirectory reproduces directories and files and not links, and the copy is
        // walked only after the regeneration has written into it. `code/x` is clean because no id this
        // producer emits maps to that path, so nothing is written there and the path stays absent from
        // both sides. Move the link to somewhere the generator does write and the same code reports
        // drift -- which is
        // A_link_where_the_generator_writes_is_reported_as_drift_rather_than_passed_over, below.
        Assert.Equal(["code/x"], report.LinksSkipped);
    }

    [Fact]
    public void A_link_where_the_generator_writes_is_reported_as_drift_rather_than_passed_over()
    {
        // THE COUNTEREXAMPLE TO A SENTENCE THIS FILE USED TO CARRY: "a link is on both sides and
        // regenerating does not change it". The walk is symmetric; the two sides are not. CopyDirectory
        // reproduces directories and files and deliberately NOT the link, and the copy is walked only
        // after the regeneration has written into it -- so where the generator writes, the copy holds
        // real concepts at paths the bundle side holds nothing at.
        //
        // Its sibling above gets a clean result only because it puts the link at code/x, which no id
        // this producer emits maps to. code/csharp/n/scanner is the opposite: the golden bundle has
        // five files under it, so regenerating produces five the linked bundle cannot have.
        //
        // Clean is the WRONG answer here and drift is the right one: `generate` refuses every
        // destination that leaves the root, so those concepts are genuinely not in the bundle and will
        // not be until the link goes.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        using var bundle = ProducerFixture.CopyGoldenBundle();

        var outside = Path.Combine(workspace.Path, "notes-outside-the-bundle");
        Directory.CreateDirectory(outside);

        var occupied = Path.Combine(bundle.Path, "code", "csharp", "n", "scanner");
        Assert.True(Directory.Exists(occupied), "the fixture assumes the golden bundle writes into this directory.");
        Directory.Delete(occupied, recursive: true);
        ProducerFixture.CreateDirectoryLink(occupied, outside);

        var report = RunCheck(workspace, bundle.Path);

        Assert.Equal(["code/csharp/n/scanner"], report.LinksSkipped);
        Assert.False(report.IsClean);
        Assert.Contains(
            report.Differences,
            d => d.StartsWith("code/csharp/n/scanner/", StringComparison.Ordinal)
                && d.EndsWith("produced by regenerating, but missing from the bundle.", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_still_reports_drift_in_a_bundle_that_also_holds_a_link()
    {
        // The other direction of the skip. "Stop walking at a reparse point" is one bad edit away from
        // "stop walking", and a --check that compares nothing reports clean on everything for ever --
        // the exact failure mode DriftReport.ConceptsRegenerated exists to catch a different cause of.
        // So: the same linked bundle, with one real concept edited, must still come back dirty and name
        // the file.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        using var bundle = ProducerFixture.CopyGoldenBundle();

        var outside = Path.Combine(workspace.Path, "notes-outside-the-bundle");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "report.md"), "---\ntype: Note\ntitle: Mine\n---\n\nsomeone's notes\n");
        ProducerFixture.CreateDirectoryLink(Path.Combine(bundle.Path, "code", "x"), outside);

        var overview = Path.Combine(bundle.Path, "overview.md");
        File.WriteAllText(overview, File.ReadAllText(overview) + "\nAn edit no regeneration produces.\n");

        var report = RunCheck(workspace, bundle.Path);

        Assert.False(report.IsClean);
        Assert.Contains(report.Differences, d => d.StartsWith("overview.md:", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_refuses_a_bundle_path_that_does_not_exist()
    {
        using var workspace = ProducerFixture.CopyRepoOutsideGit();

        Assert.Throws<InvalidOperationException>(
            () => BundleDrift.Check(Path.Combine(workspace.Path, "nope"), RepoIn(workspace), _ => 0));
    }

    [Fact]
    public void A_regeneration_that_writes_nothing_is_never_reported_clean()
    {
        // THE FLOOR UNDER THIS ENTIRE CHECK, and a contract check on the caller's Func -- exactly that
        // much. The regeneration is the caller's, so a caller that composes its pipeline wrongly hands
        // over a run that writes nothing; the copy then equals the bundle, no difference is found, and
        // `--check` prints "no drift" on every bundle for ever.
        //
        // It does NOT cover the shipped CLI's own compositions. This comment used to add "or has not
        // composed it yet, which is exactly where the CLI stands until its code-graph stage is wired":
        // that stage is wired, ConceptGenerator always emits `overview`, and every composition the CLI
        // can build therefore returns at least 1. The sentence was deleted from BundleDrift.cs and
        // survived here, where BundleDrift's doc comment then pointed a reader at it. In particular
        // this floor does not catch `--check --no-code`, which the CLI rejects outright --
        // CliTests.Check_refuses_to_be_combined_with_no_code is that guard, not this one.
        //
        // Asserted against the GOLDEN, the bundle every other test in this file expects to be clean,
        // so the only thing making this report dirty is the empty run.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();

        var report = BundleDrift.Check(ProducerFixture.GoldenBundle, RepoIn(workspace), _ => 0);

        Assert.False(report.IsClean);
        Assert.NotEqual(0, report.ExitCode);
        Assert.Equal(0, report.ConceptsRegenerated);

        // And it SAYS so, rather than handing an operator an empty list beside a non-zero exit code.
        Assert.Contains(report.Differences, d => d.Contains("proves nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void A_real_regeneration_reports_how_many_concepts_it_wrote()
    {
        // The other side of the floor: the count has to be the run's real output, not a constant that
        // happens to be positive. 15 is the fixture's whole inventory, pinned by
        // The_golden_bundle_holds_one_occurrence_of_each_shape.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();

        Assert.Equal(15, RunCheck(workspace, ProducerFixture.GoldenBundle).ConceptsRegenerated);
    }

    [Fact]
    public void The_help_text_names_both_excluded_fields_and_says_the_property_is_weaker()
    {
        // §6.2: "the list is closed and must be enumerated in the command's help text, not left to the
        // implementation". A smoke check on a constant, and it says so: it proves the sentence exists
        // and names the two fields, not that the comparison honours it -- that is what the two tests
        // above are for.
        Assert.Contains("generated.at", BundleDrift.CheckDescription, StringComparison.Ordinal);
        Assert.Contains("revision", BundleDrift.CheckDescription, StringComparison.Ordinal);
        Assert.Contains("overview", BundleDrift.CheckDescription, StringComparison.Ordinal);
        Assert.Contains("byte for byte", BundleDrift.CheckDescription, StringComparison.Ordinal);
        Assert.Contains("weaker", BundleDrift.CheckDescription, StringComparison.Ordinal);

        // And the one thing that is out of reach rather than excluded: a hand-added concept at an id
        // this producer never generates is copied forward on both sides and cannot differ. Named in
        // the help because "nothing else is ever excluded" would otherwise read as a promise the
        // comparison does not make.
        Assert.Contains("by hand", BundleDrift.CheckDescription, StringComparison.Ordinal);
        Assert.Contains("unowned", BundleDrift.CheckDescription, StringComparison.Ordinal);

        // And both refusals. The CLI rejects --check --reset and --check --no-code, and until now only
        // the README said so -- but --help is where an operator meets the flag, and the second of the
        // two exists precisely because its failure mode is silent. Named here rather than left to the
        // CLI's own wiring, so the sentence travels with the definition of what --check means.
        // That the CLI really refuses them -- as opposed to this string merely saying so -- is
        // CliTests.Check_refuses_to_be_combined_with_no_code and .._with_reset, which run the command.
        Assert.Contains("--reset", BundleDrift.CheckDescription, StringComparison.Ordinal);
        Assert.Contains("--no-code", BundleDrift.CheckDescription, StringComparison.Ordinal);
    }

    // -- the end-to-end run, made permanent -------------------------------------------------------

    [Fact]
    public void The_golden_bundle_validates_with_no_error_and_only_the_warnings_we_know_about()
    {
        // Every earlier task in this plan that needed a whole run rebuilt it as a throwaway script,
        // read its counts once and threw it away. These are those counts, held still. `producers/` is
        // outside CI by decision, so this is the only place they are checked at all.
        //
        // The warning total is pinned rather than merely bounded, and every warning is matched against
        // the two kinds we have accepted, so a NEW kind cannot hide inside an unchanged total. Both
        // known kinds are §4.3 consequences, and neither is new to this plan:
        //
        //  * three "missing recommended frontmatter field `resource`" -- `overview` and the two
        //    container concepts. A container is not declared in one file, so there is no line span to
        //    build a permalink from, and §4.3 admits only a URL there.
        //  * four "frontmatter path ... not found" on `packages/*` and `docs/*`, which carry a
        //    repo-relative `resource` (`README.md`, `src/Fixture.csproj`). The validator resolves a
        //    bare relative resource against the CONCEPT's own directory, not the bundle root, so it
        //    looks for them inside the bundle and misses. That is the very trap §4.3 made the `code/`
        //    family avoid by omitting `resource` entirely; the docs/packages families predate this
        //    plan and still walk into it. Recorded here, where the harness measures it.
        var outcome = ProducerFixture.Validate(ProducerFixture.GoldenBundle);

        Assert.Equal(0, outcome.ErrorCount);
        Assert.True(outcome.IsConformant, string.Join("\n", outcome.DiagnosticLines));
        Assert.DoesNotContain(outcome.DiagnosticLines, line => line.Contains("BrokenLink", StringComparison.Ordinal));

        Assert.Equal(7, outcome.WarningCount);
        Assert.All(
            outcome.DiagnosticLines.Where(line => line.StartsWith("[warning]", StringComparison.Ordinal)),
            line => Assert.True(
                line.Contains("missing recommended frontmatter field `resource`", StringComparison.Ordinal)
                    || line.Contains("not found", StringComparison.Ordinal),
                $"a warning kind this fixture has never produced before: {line}"));
    }

    [Fact]
    public void The_golden_bundle_holds_one_occurrence_of_each_shape()
    {
        // A golden of 480 concepts is not reviewable in a diff, so it is not a test. This pins the
        // fixture at the size the brief asks for AND names the shapes it is supposed to carry, so a
        // fixture that quietly lost its merged overload pair (or grew to 400 concepts) fails here
        // rather than weakening every other test in this file silently.
        // The SHARED filter, not a local suffix test: an `EndsWith("index.md")` here would also swallow
        // a concept legitimately named `build-index`, which is exactly the mistake the one spelling in
        // ProducerFixture exists to prevent both call sites from making independently.
        var concepts = Directory.EnumerateFiles(ProducerFixture.GoldenBundle, "*.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ProducerFixture.GoldenBundle, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(ProducerFixture.IsConceptFile)
            .Select(path => path[..^".md".Length])
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        Assert.InRange(concepts.Count, 15, 20);

        Assert.Equal(
            [
                "code/csharp/n",                        // a namespace, synthesized as a container
                "code/csharp/n/registry",               // a type
                "code/csharp/n/registry/count",         // a member with no doc comment (description_source: generated)
                "code/csharp/n/registry/register",      // the merged overload pair (§3.2)
                "code/csharp/n/scanner",
                "code/csharp/n/scanner/gone",           // the symbol a mutation deletes
                "code/csharp/n/scanner/normalize",      // the one resolved call target
                "code/csharp/n/scanner/root",
                "code/csharp/n/scanner/scan",
                "code/csharp/n/sub",                    // a nested container
                "code/csharp/n/sub/formatter",
                "code/csharp/n/sub/formatter/format",
                "docs/fixture-repository",
                "overview",
                "packages/fixture",
            ],
            concepts);

        // The private member `Scanner.Cache` is in the fixture source and must NOT be here (§5.4).
        Assert.DoesNotContain("code/csharp/n/scanner/cache", concepts);

        var register = File.ReadAllText(Path.Combine(ProducerFixture.GoldenBundle, "code/csharp/n/registry/register.md"));
        var count = File.ReadAllText(Path.Combine(ProducerFixture.GoldenBundle, "code/csharp/n/registry/count.md"));

        // Two signatures on one concept: the whole point of §3.2's merge.
        Assert.Equal(2, register.Split("\n- `public string Register(", StringSplitOptions.None).Length - 1);
        Assert.Contains("## Calls\n", register, StringComparison.Ordinal);            // a resolved call
        Assert.Contains("## Calls (unresolved)\n", count, StringComparison.Ordinal);  // an unresolved one
    }

    // -- fixture ----------------------------------------------------------------------------------

    private static string RepoIn(ProducerFixture.TempDir workspace) =>
        Path.Combine(workspace.Path, ProducerFixture.RepoDirectoryName);

    private static DriftReport RunCheck(ProducerFixture.TempDir workspace, string bundlePath)
    {
        var repo = RepoIn(workspace);

        // The count is what Check refuses to report clean without -- see DriftReport.ConceptsRegenerated.
        return BundleDrift.Check(bundlePath, repo, copy => ProducerFixture.Run(repo, copy).Write.Written);
    }

    private static string Explain(DriftReport report) =>
        report.IsClean ? string.Empty : "unexpected drift:\n  " + string.Join("\n  ", report.Differences);

    /// <summary>
    /// Rewrites one concept's <c>description</c> and <c>description_source</c> <b>and the paragraph of
    /// its body that carries the same text</b>, which is what an ordinary <c>--update</c> would leave
    /// behind after someone edited the description by hand.
    ///
    /// <para>Editing the frontmatter alone would leave the file internally inconsistent, and the drift
    /// test above would then go green for the wrong reason -- it would be measuring the body, not the
    /// preservation rule.</para>
    /// </summary>
    private static void SetDescription(string bundlePath, string id, string description, string source)
    {
        var path = Path.Combine(bundlePath, id.Replace('/', Path.DirectorySeparatorChar) + ".md");
        var document = OkfDocument.Parse(File.ReadAllText(path));

        document.Frontmatter.Set("description", new OKF4net.Yaml.YamlString(description));
        document.Frontmatter.Set(DescriptionResolver.DescriptionSourceKey, new OKF4net.Yaml.YamlString(source));

        // The generated body is `# <title>\n\n<description>\n\n## <first section>`; this replaces the
        // middle of those three. It asserts rather than silently no-ops, so a change to the body's
        // shape surfaces here instead of quietly making the test vacuous.
        var body = document.Body;
        var afterHeading = body.IndexOf("\n\n", StringComparison.Ordinal);
        Assert.True(afterHeading >= 0, $"'{id}' has no heading paragraph break; the body shape assumed here has changed.");

        var nextSection = body.IndexOf("\n\n##", afterHeading + 2, StringComparison.Ordinal);
        Assert.True(nextSection >= 0, $"'{id}' has no section after its description; the body shape assumed here has changed.");

        var rewritten = new OkfDocument(document.Frontmatter, body[..(afterHeading + 2)] + description + body[nextSection..]);

        // Serialize(), not a hand-built string: BundleConceptWriter writes exactly this, so the edited
        // file is byte-shaped like a written one and no formatting difference can masquerade as drift.
        File.WriteAllText(path, rewritten.Serialize());

        // A directory's index.md quotes its children's descriptions, so an edited description leaves
        // it stale -- real drift, and drift `generate --update` repairs on the very next run. Repairing
        // it here through OKF4net's own generator puts the bundle in the state an ordinary update
        // leaves, so the drift test above measures the preservation rule and not this helper's
        // half-finished edit. It re-derives no description of its own, so the rule stays load-bearing:
        // break preservation and the concept file below changes, and the check goes red.
        IndexGenerator.RegenerateIndexes(bundlePath);
    }

    /// <summary>Applies <paramref name="edit"/> to one concept's frontmatter and writes it back, leaving the body untouched.</summary>
    private static void Edit(string bundlePath, string id, Action<Frontmatter> edit)
    {
        var path = Path.Combine(bundlePath, id.Replace('/', Path.DirectorySeparatorChar) + ".md");
        var document = OkfDocument.Parse(File.ReadAllText(path));

        edit(document.Frontmatter);
        File.WriteAllText(path, document.Serialize());
    }
}
