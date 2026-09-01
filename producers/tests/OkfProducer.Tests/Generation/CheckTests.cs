// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OkfProducer.Core.Generation;

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
public class CheckTests
{
    /// <summary>Set this to <c>1</c> to rewrite the golden bundle from the fixture repository instead of asserting against it.</summary>
    private const string UpdateGoldenVariable = "OKFGEN_UPDATE_GOLDEN";

    [Fact]
    public void Check_passes_on_an_unchanged_bundle()
    {
        using var workspace = ProducerFixture.CopyRepoOutsideGit();

        // Regeneration is a mode of THIS test rather than a separate fact that does nothing when the
        // variable is unset: a fact whose only job is to be skipped is a green result that proves
        // nothing, and this plan has already shipped several assertions incapable of failing. Here the
        // assertion below runs in both modes.
        if (Environment.GetEnvironmentVariable(UpdateGoldenVariable) == "1")
        {
            ProducerFixture.RegenerateGolden(workspace);
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
        // The other direction of the comparison. A check that only diffed the files it regenerated
        // would stay green here, having never looked at what the bundle has and the run does not
        // produce -- and a concept someone deleted by hand is exactly the staleness this guards.
        using var workspace = ProducerFixture.CopyRepoOutsideGit();
        using var bundle = ProducerFixture.CopyGoldenBundle();

        File.Delete(Path.Combine(bundle.Path, "code/csharp/n/scanner/normalize.md"));

        var report = RunCheck(workspace, bundle.Path);

        Assert.Contains(
            report.Differences,
            d => d.StartsWith("code/csharp/n/scanner/normalize.md:", StringComparison.Ordinal));
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
    public void Check_refuses_a_bundle_path_that_does_not_exist()
    {
        using var workspace = ProducerFixture.CopyRepoOutsideGit();

        Assert.Throws<InvalidOperationException>(
            () => BundleDrift.Check(Path.Combine(workspace.Path, "nope"), RepoIn(workspace), _ => { }));
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
        var concepts = Directory.EnumerateFiles(ProducerFixture.GoldenBundle, "*.md", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ProducerFixture.GoldenBundle, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !path.EndsWith("index.md", StringComparison.Ordinal))
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
        return BundleDrift.Check(bundlePath, repo, copy => ProducerFixture.Run(repo, copy));
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
