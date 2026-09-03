// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.RegularExpressions;
using OKF4net;
using OkfProducer.Cli;
using OkfProducer.CodeGraph.Roslyn;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;
using OkfProducer.Tests.Generation;

namespace OkfProducer.Tests;

/// <summary>
/// §9's flag surface, driven through <see cref="OkfgenCli.Run"/> -- the shipped composition, in
/// process.
///
/// <para><b>Why these go through the CLI and not through a hand-assembled pipeline.</b> Twelve tasks
/// built the code stage; none of it was reachable from the binary, and three of its features shipped
/// dead for exactly that reason: nothing supplied <see cref="GenerateOptions.ExistingFrontmatter"/>,
/// so field preservation never ran outside a test; <see cref="GenerateOptions.Note"/> had no
/// consumer, so every degradation the run carefully reported was dropped; and a manifest handed to a
/// code-less run would have offered a licence to delete the whole <c>code/</c> family.
/// <c>ProducerFixture</c> re-assembles the pipeline from the same types and therefore cannot see any
/// of those -- they are properties of the wiring. These tests exercise the wiring.</para>
/// </summary>
public class CliTests
{
    /// <summary>The concept every "is the code stage running, and with what scope" assertion keys off.</summary>
    private const string RunConcept = "code/csharp/demo/widget/run";

    /// <summary>The type concept above it -- present whenever any of <c>Widget</c>'s members are.</summary>
    private const string WidgetConcept = "code/csharp/demo/widget";

    /// <summary>Declared <c>internal</c>, so it exists only under <c>--include-internal</c>.</summary>
    private const string HiddenConcept = "code/csharp/demo/hidden";

    /// <summary>Declared under <c>tests/</c>, so it exists only under <c>--include-tests</c>.</summary>
    private const string ProbeConcept = "code/csharp/probes/probe";

    [Fact]
    public void Without_a_repo_url_no_code_concept_carries_a_resource()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);

        var result = Run("generate", "--repo", repo, "--out", bundle);

        Assert.Equal(0, result.ExitCode);
        Assert.Null(Frontmatter(bundle, RunConcept).Get("resource"));
    }

    [Fact]
    public void A_repo_url_and_a_rev_put_a_permalink_on_a_code_concept()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);

        var result = Run("generate", "--repo", repo, "--out", bundle, "--repo-url", "https://example.com/acme/demo", "--rev", "main");

        Assert.Equal(0, result.ExitCode);

        var resource = Frontmatter(bundle, RunConcept).Get("resource")?.AsDisplayString();
        Assert.NotNull(resource);
        Assert.StartsWith("https://example.com/acme/demo/blob/main/src/Widget.cs#L", resource, StringComparison.Ordinal);
    }

    [Fact]
    public void On_a_detached_head_a_repo_url_alone_emits_no_permalink_and_the_run_says_why()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);
        CommitAndDetach(repo);

        var withoutRev = Run("generate", "--repo", repo, "--out", bundle, "--repo-url", "https://example.com/acme/demo");

        Assert.Equal(0, withoutRev.ExitCode);
        Assert.Null(Frontmatter(bundle, RunConcept).Get("resource"));
        Assert.Contains("note: ", withoutRev.Error, StringComparison.Ordinal);
        Assert.Contains("detached HEAD", withoutRev.Error, StringComparison.Ordinal);

        // The other half, without which the assertions above would also hold for a build that never
        // emits a `resource` at all: the same detached checkout, with --rev supplied, does emit one.
        var second = Path.Combine(workspace.Path, "with-rev");
        var withRev = Run("generate", "--repo", repo, "--out", second, "--repo-url", "https://example.com/acme/demo", "--rev", "v1.2.3");

        Assert.Equal(0, withRev.ExitCode);
        Assert.Equal(
            "https://example.com/acme/demo/blob/v1.2.3/src/Widget.cs",
            Frontmatter(second, RunConcept).Get("resource")?.AsDisplayString()?.Split('#')[0]);
    }

    [Fact]
    public void Include_tests_is_what_brings_a_test_directory_into_the_bundle()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);

        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);
        AssertAbsent(bundle, ProbeConcept);

        var widened = Path.Combine(workspace.Path, "with-tests");
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", widened, "--include-tests").ExitCode);
        AssertPresent(widened, ProbeConcept);
    }

    [Fact]
    public void Include_internal_is_what_brings_an_internal_declaration_into_the_bundle()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);

        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);
        AssertAbsent(bundle, HiddenConcept);

        var widened = Path.Combine(workspace.Path, "with-internal");
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", widened, "--include-internal").ExitCode);
        AssertPresent(widened, HiddenConcept);
    }

    [Fact]
    public void Max_file_size_below_a_source_file_leaves_its_declarations_out()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);

        var widgetBytes = new FileInfo(Path.Combine(repo, "src", "Widget.cs")).Length;
        Assert.True(widgetBytes > 1, "the fixture source is empty, so no cap can be set below it.");

        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle, "--max-file-size", (widgetBytes - 1).ToString()).ExitCode);
        AssertAbsent(bundle, RunConcept);

        // The same run with a cap the file fits under does produce it -- so the assertion above is
        // about the cap and not about a fixture that declares nothing.
        var generous = Path.Combine(workspace.Path, "generous");
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", generous, "--max-file-size", widgetBytes.ToString()).ExitCode);
        AssertPresent(generous, RunConcept);
    }

    [Fact]
    public void Max_file_size_must_be_positive()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);

        var result = Run("generate", "--repo", repo, "--out", bundle, "--max-file-size", "0");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--max-file-size", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(bundle) && Directory.EnumerateFileSystemEntries(bundle).Any(), "a rejected run must write nothing.");
    }

    [Fact]
    public void No_code_reproduces_the_pre_code_output_byte_for_byte()
    {
        // Inside git, so `overview`'s `generated.at` is stamped from the HEAD commit rather than the
        // wall clock: two runs a second apart would otherwise differ on that one field and this
        // comparison would flake for a reason that has nothing to do with --no-code.
        using var workspace = NewWorkspace(out var repo, out var bundle);
        Commit(repo);

        // --repo-url and --rev are passed deliberately: --no-code must disable the whole code stage,
        // not merely stop short of writing its concepts.
        var result = Run("generate", "--repo", repo, "--out", bundle, "--no-code", "--repo-url", "https://example.com/acme/demo", "--rev", "main");
        Assert.Equal(0, result.ExitCode);

        var reference = Path.Combine(workspace.Path, "reference");
        var snapshot = new RepositoryScanner().Scan(repo);
        new BundleWriter().Write(reference, new ConceptGenerator().Generate(snapshot), WritePolicy.RequireEmpty, repo);

        AssertSameBytes(reference, bundle);

        // Stated separately because the byte comparison would also pass if BOTH sides had grown a
        // manifest: a run that generates no code concept claims ownership of nothing and must leave
        // no licence to delete behind it.
        Assert.False(File.Exists(Path.Combine(bundle, GenerationManifest.FileName)));
    }

    [Fact]
    public void No_code_over_a_code_bundle_deletes_nothing_and_leaves_the_manifest_alone()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);

        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);
        var before = ProducerFixture.SnapshotFiles(bundle);
        Assert.Contains(before.Keys, k => k.StartsWith("code/", StringComparison.Ordinal));
        Assert.Contains(GenerationManifest.FileName, before.Keys);

        var result = Run("generate", "--repo", repo, "--out", bundle, "--update", "--no-code");

        Assert.Equal(0, result.ExitCode);

        var after = ProducerFixture.SnapshotFiles(bundle);
        foreach (var (path, bytes) in before)
        {
            if (!path.StartsWith("code/", StringComparison.Ordinal) && path != GenerationManifest.FileName)
            {
                continue;
            }

            Assert.True(after.TryGetValue(path, out var now), $"'{path}' was deleted by a --no-code run.");
            Assert.True(bytes.AsSpan().SequenceEqual(now), $"'{path}' was rewritten by a --no-code run.");
        }
    }

    [Fact]
    public void A_manual_description_survives_the_next_update()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);
        const string Handwritten = "Written by a human, and not to be thrown away by the next generate.";

        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);
        Assert.NotEqual(Handwritten, Frontmatter(bundle, RunConcept).Get("description")?.AsDisplayString());

        SetDescription(bundle, RunConcept, Handwritten, DescriptionResolver.ManualLabel);

        var result = Run("generate", "--repo", repo, "--out", bundle, "--update");

        Assert.Equal(0, result.ExitCode);

        var frontmatter = Frontmatter(bundle, RunConcept);
        Assert.Equal(Handwritten, frontmatter.Get("description")?.AsDisplayString());
        Assert.Equal(DescriptionResolver.ManualLabel, frontmatter.Get(DescriptionResolver.DescriptionSourceKey)?.AsDisplayString());
    }

    [Fact]
    public void A_reset_run_does_not_preserve_a_manual_description()
    {
        // The other side of the decision above, pinned so it stays a decision rather than an
        // accident: the existing-frontmatter reader is lazy, so supplying it under --reset would have
        // a "delete and recreate" run carry hand-written text across the deletion it was asked for.
        using var workspace = NewWorkspace(out var repo, out var bundle);
        const string Handwritten = "Written by a human, and deliberately not carried across a --reset.";

        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);
        SetDescription(bundle, RunConcept, Handwritten, DescriptionResolver.ManualLabel);

        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle, "--reset").ExitCode);

        Assert.NotEqual(Handwritten, Frontmatter(bundle, RunConcept).Get("description")?.AsDisplayString());
    }

    [Fact]
    public void A_run_that_could_not_attribute_its_namespaces_says_so_on_stderr()
    {
        // The fixture repository declares a package (package.json) and C# containers, but no .csproj,
        // so no `Compile` item set can be read and §5.1's package -> namespace link cannot be
        // attributed. That degradation is exactly what GenerateOptions.Note exists to report, and
        // until this task nothing consumed it.
        using var workspace = NewWorkspace(out var repo, out var bundle);

        var result = Run("generate", "--repo", repo, "--out", bundle);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("note: no source-ownership map was supplied", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("no source-ownership map was supplied", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_passes_on_a_bundle_that_matches_its_repository()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);

        var before = ProducerFixture.SnapshotFiles(bundle);
        var result = Run("generate", "--repo", repo, "--out", bundle, "--check");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No drift", result.Output, StringComparison.Ordinal);

        // --check reports, it does not repair: the bundle it was pointed at is untouched.
        var after = ProducerFixture.SnapshotFiles(bundle);
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(before, entry => Assert.True(entry.Value.AsSpan().SequenceEqual(after[entry.Key]), $"--check rewrote '{entry.Key}'."));
    }

    [Fact]
    public void Check_fails_and_names_the_file_when_the_source_moved_on()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);

        WriteSource(repo, "src/Widget.cs", WidgetSource(AddedMember));

        var result = Run("generate", "--repo", repo, "--out", bundle, "--check");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("drift: code/csharp/demo/widget/added.md", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_says_one_thing_about_a_link_standing_where_a_concept_file_belongs()
    {
        // WHAT THE OPERATOR ACTUALLY READ, before this test existed, about one single path:
        //
        //   drift: code/csharp/demo/widget.md: produced by regenerating, but missing from the bundle.
        //   note:  'code/csharp/demo/widget.md' ... was neither copied nor compared
        //
        // Two lines contradicting each other. The suppression that fixes the second half lives in
        // OkfgenCli.Check (LinksSkipped minus LinksReportedAsDrift), so it needs a test that runs the
        // command -- CheckTests can only pin the two lists BundleDrift hands over.
        //
        // A JUNCTION named `widget.md` stands in for the file symbolic link this shape really is:
        // Windows will not create one of those without SeCreateSymbolicLinkPrivilege. See
        // CheckTests.A_link_standing_where_a_concept_file_belongs_is_named_as_the_link_it_is for why
        // that substitution reaches the same branch, and for the fact that it is an argument from
        // reading BundleDrift.Descend rather than a second measurement.
        using var workspace = NewWorkspace(out var repo, out var bundle);
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);

        var outside = Path.Combine(workspace.Path, "notes-outside-the-bundle");
        Directory.CreateDirectory(outside);

        var occupied = ConceptPath(bundle, WidgetConcept);
        Assert.True(File.Exists(occupied), "the fixture assumes `generate` writes a concept FILE at this exact path.");
        File.Delete(occupied);
        ProducerFixture.CreateDirectoryLink(occupied, outside);

        var result = Run("generate", "--repo", repo, "--out", bundle, "--check");

        Assert.Equal(1, result.ExitCode);

        // One drift line about the path, and it says a link is what the bundle holds there.
        var drift = result.Output.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("drift: " + WidgetConcept + ".md:", StringComparison.Ordinal))
            .ToList();
        var only = Assert.Single(drift);
        Assert.Contains("symbolic link or junction", only, StringComparison.Ordinal);
        Assert.DoesNotContain("missing from the bundle", only, StringComparison.Ordinal);

        // And no note repeating the same path with the opposite sense. Asserted over every note line
        // rather than over the whole of stderr, so a mention of the path inside some other note would
        // still fail this.
        Assert.DoesNotContain(
            NoteLines(result.Error),
            line => line.Contains(WidgetConcept + ".md", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_refuses_to_be_combined_with_no_code()
    {
        // The combination that used to exit 0 over an arbitrarily stale `code/` family. --check
        // regenerates over a COPY of the bundle; with --no-code that regeneration produces no `code`
        // concept and no manifest, so every `code/` file is copied forward untouched and cannot differ,
        // and the copy's .okfgen-manifest.json is byte-identical because nothing rewrote it. The floor
        // in DriftReport does not catch it either: ConceptGenerator always emits `overview`, so the
        // count is positive for every composition the CLI can build. And a note could not fix it --
        // this producer's README says a note never changes the exit code -- so a CI gate keyed on
        // `--check` stayed green for ever. Rejected, exactly as --check --reset is.
        using var workspace = NewWorkspace(out var repo, out var bundle);
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);

        // Drift the bundle can only see through the code stage: a member added to the source.
        WriteSource(repo, "src/Widget.cs", WidgetSource(AddedMember));

        var result = Run("generate", "--repo", repo, "--out", bundle, "--check", "--no-code");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--no-code", result.Error, StringComparison.Ordinal);

        // Not merely non-zero: the run must not have reported a clean bundle on the way out. Without
        // the rejection this printed "No drift" and exited 0 against exactly this drift.
        Assert.DoesNotContain("No drift", result.Output, StringComparison.Ordinal);

        // And the combination is refused rather than silently downgraded to a plain --check: nothing
        // was compared, so nothing was reported.
        Assert.DoesNotContain("drift:", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Dropping_include_internal_does_not_delete_the_concepts_it_stopped_covering()
    {
        // The end-to-end shape of the manifest-scope rule, through the shipped composition. `Hidden` is
        // declared internal, so it exists under --include-internal and not without it -- and its owning
        // file is read cleanly either way, which is what made its concept look settled and deleted.
        using var workspace = NewWorkspace(out var repo, out var bundle);
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle, "--include-internal").ExitCode);
        AssertPresent(bundle, HiddenConcept);

        // Run TWICE, because once is what a broken fix passes. The refusal keeps `Hidden` by carrying
        // its manifest entry forward; if the manifest that run writes records this narrow run's own
        // scope over that widened set, the second identical command compares equal scopes, sees no
        // narrowing, finds `Hidden` settled -- its file is read cleanly with or without the flag,
        // which is the whole asymmetry -- and deletes it. The reprieve lasted exactly one run.
        for (var run = 1; run <= 2; run++)
        {
            var narrowed = Run("generate", "--repo", repo, "--out", bundle, "--update");

            Assert.Equal(0, narrowed.ExitCode);
            AssertPresent(bundle, HiddenConcept);
            Assert.Contains("--include-internal", narrowed.Error, StringComparison.Ordinal);
        }

        var result = Run("generate", "--repo", repo, "--out", bundle, "--update");

        Assert.Equal(0, result.ExitCode);
        AssertPresent(bundle, HiddenConcept);
        Assert.Contains("--include-internal", result.Error, StringComparison.Ordinal);

        // Both halves: a writer that pruned nothing at all would pass the assertion above just as
        // happily. Re-running with the flag back on must leave the same bundle, pruning nothing and
        // saying nothing about scope.
        var widened = Run("generate", "--repo", repo, "--out", bundle, "--update", "--include-internal");

        Assert.Equal(0, widened.ExitCode);
        AssertPresent(bundle, HiddenConcept);
        Assert.DoesNotContain("wider scope", widened.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_refuses_to_be_combined_with_reset()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);
        var before = ProducerFixture.SnapshotFiles(bundle);

        var result = Run("generate", "--repo", repo, "--out", bundle, "--check", "--reset");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--check", result.Error, StringComparison.Ordinal);

        // Bytes, not a file count: a rejected run that nevertheless rewrote every concept in place
        // would keep the count identical and pass a weaker assertion.
        Assert.All(before, entry =>
        {
            var now = Path.Combine(bundle, entry.Key.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(now), $"'{entry.Key}' was deleted by a rejected run.");
            Assert.True(entry.Value.AsSpan().SequenceEqual(File.ReadAllBytes(now)), $"'{entry.Key}' was rewritten by a rejected run.");
        });
        Assert.Equal(before.Count, ProducerFixture.SnapshotFiles(bundle).Count);
    }

    [Fact]
    public void A_repo_url_that_is_not_an_absolute_http_url_is_rejected_before_anything_is_written()
    {
        // Both forms a forge displays and a user pastes. Each would otherwise produce a
        // successful-looking run carrying not one `resource`, since the generator silently returns no
        // permalink for anything that is not an absolute http(s) URL.
        foreach (var malformed in new[] { "github.com/acme/demo", "git@github.com:acme/demo.git", "file:///srv/demo" })
        {
            using var workspace = NewWorkspace(out var repo, out var bundle);

            var result = Run("generate", "--repo", repo, "--out", bundle, "--repo-url", malformed);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--repo-url", result.Error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(bundle), $"'{malformed}' was rejected but the run still created the bundle.");
        }
    }

    [Theory]
    // Accepted: the two schemes a forge blob URL can carry, scheme-insensitively, with or without a
    // path, and with the query string the generator later strips.
    [InlineData("https://github.com/acme/demo", true)]
    [InlineData("http://git.internal.example/acme/demo", true)]
    [InlineData("HTTPS://GitHub.com/acme/demo", true)]
    [InlineData("https://example.com", true)]
    [InlineData("https://example.com/acme/demo?ref=x", true)]
    // Rejected, and each for a reason that reaches a user: the form a forge displays, the form a
    // clone dialog offers, a scheme the validator would not classify as a URL, and nothing at all.
    [InlineData("github.com/acme/demo", false)]
    [InlineData("git@github.com:acme/demo.git", false)]
    [InlineData("ssh://git@github.com/acme/demo.git", false)]
    [InlineData("file:///srv/demo", false)]
    [InlineData("ftp://example.com/demo", false)]
    [InlineData("/srv/demo", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_permalink_base_rule_is_one_definition_shared_by_the_generator_and_the_cli(string? repoUrl, bool expected)
    {
        // Pinned as a table because this rule now has two consumers who must not diverge: the
        // generator, which emits no `resource` at all when it fails, and the CLI, which refuses the
        // value at its boundary. They were briefly two copies that happened to agree; they are one
        // method now, and this is the definition that method is held to.
        Assert.Equal(expected, GenerateOptions.TryPermalinkBase(repoUrl, out var parsed));
        Assert.Equal(expected, parsed is not null);
    }

    [Fact]
    public void A_rev_without_a_repo_url_says_it_had_no_effect()
    {
        using var workspace = NewWorkspace(out var repo, out var bundle);

        var result = Run("generate", "--repo", repo, "--out", bundle, "--rev", "main");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("note: --rev was supplied without --repo-url", result.Error, StringComparison.Ordinal);

        // And the note is not unconditional noise: supplying both says nothing about --rev.
        var both = Path.Combine(workspace.Path, "both");
        var quiet = Run("generate", "--repo", repo, "--out", both, "--repo-url", "https://example.com/acme/demo", "--rev", "main");

        Assert.Equal(0, quiet.ExitCode);
        Assert.DoesNotContain("--rev was supplied without", quiet.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_forwards_the_writer_notes_that_say_a_clean_result_was_weakened()
    {
        // The case where "No drift" is the whole of what an operator would otherwise see. A concept
        // sitting under the owned prefix that no manifest claims is copied forward untouched and
        // regenerating never produces it, so it cannot differ -- the comparison is clean and stays
        // clean for ever, while the bundle carries a file this producer will never prune. The
        // writer's note is the only signal that exists, and --check taking nothing but the concept
        // count off the run dropped it.
        using var workspace = NewWorkspace(out var repo, out var bundle);
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);

        var handWritten = Path.Combine(bundle, "code", "csharp", "demo", "extra.md");
        File.WriteAllText(handWritten, string.Join('\n',
            "---",
            "type: Note",
            "title: Extra",
            "description: A concept a human wrote by hand under the owned prefix.",
            "---",
            "",
            "# Extra",
            "",
            "A concept a human wrote by hand under the owned prefix.",
            ""));

        // Settles the index files the new concept appears in, so the bundle is in the state an
        // ordinary update leaves and the check below measures the unowned file, not a stale index.
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle, "--update").ExitCode);

        var result = Run("generate", "--repo", repo, "--out", bundle, "--check");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No drift", result.Output, StringComparison.Ordinal);

        // Anchored to the note's own words, and required to be ONE line: separate Assert.Contains
        // calls over the whole of stderr would pass on any note plus any mention of the id anywhere,
        // and would survive a rewording that dropped the meaning entirely.
        Assert.Contains(
            NoteLines(result.Error),
            line => line.Contains("code/csharp/demo/extra", StringComparison.Ordinal)
                && line.Contains("no manifest claims it", StringComparison.Ordinal)
                && line.Contains("will never be pruned", StringComparison.Ordinal));
    }

    [Fact]
    public void Check_forwards_the_note_that_explains_a_drift_the_drift_line_cannot()
    {
        // The second half, and the honest correction to how this was first described: a source file
        // over --max-file-size does NOT produce a clean check. Its concepts are held back rather than
        // pruned, so no `.md` differs -- but `.okfgen-manifest.json` records which files the run
        // read, so the manifest differs and --check fails.
        //
        // Which is exactly why the note matters here too. Without it the operator sees one opaque
        // line about a machine-written JSON file and nothing at all about the cause: a source file
        // that was never read, and three concepts kept on the strength of that.
        using var workspace = NewWorkspace(out var repo, out var bundle);
        Assert.Equal(0, Run("generate", "--repo", repo, "--out", bundle).ExitCode);

        var widgetBytes = new FileInfo(Path.Combine(repo, "src", "Widget.cs")).Length;
        var result = Run("generate", "--repo", repo, "--out", bundle, "--check", "--max-file-size", (widgetBytes - 1).ToString());

        Assert.Equal(1, result.ExitCode);

        var drift = result.Output.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("drift: ", StringComparison.Ordinal)).ToList();
        var only = Assert.Single(drift);
        Assert.Contains(GenerationManifest.FileName, only, StringComparison.Ordinal);

        Assert.Contains(
            NoteLines(result.Error),
            line => line.Contains("src/Widget.cs", StringComparison.Ordinal)
                && line.Contains("were absent from this run but kept", StringComparison.Ordinal)
                && line.Contains("may be unread rather than deleted", StringComparison.Ordinal));
    }

    /// <summary>Every <c>note: </c> line written to stderr, trimmed -- one string per note, so an assertion can require one note to carry the whole meaning.</summary>
    private static IReadOnlyList<string> NoteLines(string error) =>
    [
        .. error.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("note: ", StringComparison.Ordinal))
    ];

    // ---- the composition root's one piece of pure data logic ---------------------------------

    [Fact]
    public void The_ownership_join_turns_msbuild_compile_items_into_repository_relative_ownership()
    {
        // No MSBuild, no disk: ProjectInputs in, SourceOwnershipMap out. This join had zero
        // executable coverage anywhere in the solution, and a wrong one is quiet -- a missing or
        // misattributed package -> namespace link, not a failure.
        var repo = Path.Combine(Path.GetTempPath(), "okfgen-join-fixture");
        var alpha = Path.Combine(repo, "src", "Alpha", "Alpha.csproj");
        var beta = Path.Combine(repo, "src", "Beta", "Beta.csproj");

        var map = GenerateRun.Attribution(
            repo,
            [
                // Deliberately not in Ordinal order, so the "first .csproj wins" rule below is the
                // map's rule and not the order this list happened to be built in.
                Inputs(beta, [Path.Combine(repo, "src", "Beta", "Beta.cs"), Path.Combine(repo, "shared", "Shared.cs")]),
                Inputs(alpha, [Path.Combine(repo, "src", "Alpha", "Alpha.cs"), Path.Combine(repo, "shared", "Shared.cs")]),
            ],
            _ => { });

        Assert.NotNull(map);
        Assert.Equal("src/Alpha/Alpha.csproj", map.OwnerOf("src/Alpha/Alpha.cs"));
        Assert.Equal("src/Beta/Beta.csproj", map.OwnerOf("src/Beta/Beta.cs"));

        // §5.1: a file two projects compile belongs to the Ordinal-first .csproj, and the other is
        // still reported rather than the concept being duplicated.
        Assert.Equal("src/Alpha/Alpha.csproj", map.OwnerOf("shared/Shared.cs"));
        Assert.Equal(new[] { "src/Alpha/Alpha.csproj", "src/Beta/Beta.csproj" }, map.ClaimantsOf("shared/Shared.cs"));

        Assert.Null(map.OwnerOf("src/Gamma/Gamma.cs"));
    }

    [Fact]
    public void The_ownership_join_supplies_no_map_at_all_when_msbuild_answered_for_nothing()
    {
        // Not an empty map: an empty one attributes nothing AND suppresses the note that says why,
        // leaving the operator with "N containers unattributed" -- the symptom instead of the cause.
        var notes = new List<string>();

        Assert.Null(GenerateRun.Attribution(Path.GetTempPath(), [], notes.Add));
        Assert.Contains(notes, n => n.Contains("source-ownership map", StringComparison.Ordinal));
    }

    /// <summary>One project's MSBuild answer, with only the two fields this join reads filled in meaningfully.</summary>
    private static ProjectInputs Inputs(string projectPath, string[] compileFiles) =>
        new(projectPath, Path.GetFileNameWithoutExtension(projectPath), compileFiles, [], string.Empty, "14", true, false, "Library", "net10.0");

    [Fact]
    public void A_project_reported_compiled_that_owns_none_of_its_files_is_named_rather_than_passed_over()
    {
        // A Library whose every `Compile` item SourceFileGate refused compiles from NO syntax tree,
        // and an empty library compilation has zero diagnostics -- so RoslynResolver reports it
        // Compiled, owns none of its files, and the name-matching baseline carries them exactly as it
        // carries a failed project's. The reporting used to print nothing for it, which is not a
        // silent gap but an affirmatively wrong statement about the run. (An Exe would give CS5001
        // and be reported as CompilationHadErrors; a Library gives nothing.)
        //
        // Pure data, like the ownership-join tests above: no MSBuild, no disk, no gate. What is
        // pinned is the CONCLUSION -- reported compiled, owns none of its own items -- not a second
        // copy of the gate's rules.
        var repo = Path.Combine(Path.GetTempPath(), "okfgen-vacuous-compilation-fixture");
        var refused = Path.Combine(repo, "src", "Big", "Big.csproj");
        var healthy = Path.Combine(repo, "src", "Small", "Small.csproj");
        var notes = new List<string>();

        // The `owns` predicate is exactly what the gate leaves behind: the healthy project's file
        // reached a syntax tree and is owned; the refused project's never did.
        GenerateRun.ReportProjects(
            repo,
            [
                new RoslynProjectReport(refused, RoslynProjectAvailability.Compiled, string.Empty),
                new RoslynProjectReport(healthy, RoslynProjectAvailability.Compiled, string.Empty),
            ],
            [
                Inputs(refused, [Path.Combine(repo, "src", "Big", "Big.cs")]),
                Inputs(healthy, [Path.Combine(repo, "src", "Small", "Small.cs")]),
            ],
            path => string.Equals(path, "src/Small/Small.cs", StringComparison.Ordinal),
            notes.Add);

        Assert.Contains(
            notes,
            n => n.Contains("src/Big/Big.csproj", StringComparison.Ordinal)
                && n.Contains("owns none of its files", StringComparison.Ordinal));

        // The half that keeps this from being satisfied by "note every compiled project": the one
        // whose file IS owned is not named at all.
        Assert.DoesNotContain(notes, n => n.Contains("src/Small/Small.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void A_project_with_no_compile_items_at_all_is_not_reported_as_a_refused_compilation()
    {
        // The other way to own none of your files: declare none. `Any` is false for an EMPTY
        // `Compile` set exactly as it is for one the gate refused entirely, and a packaging or
        // targets-only project -- Microsoft.Build.NoTargets, EnableDefaultCompileItems=false --
        // legitimately has none, compiles clean, and is healthy. Reporting it would state a refusal
        // that did not occur and a `## Calls` degradation for a project with no links to lose.
        var repo = Path.Combine(Path.GetTempPath(), "okfgen-empty-compile-set-fixture");
        var packaging = Path.Combine(repo, "build", "Packaging.csproj");
        var notes = new List<string>();

        GenerateRun.ReportProjects(
            repo,
            [new RoslynProjectReport(packaging, RoslynProjectAvailability.Compiled, string.Empty)],
            [Inputs(packaging, [])],
            _ => false,
            notes.Add);

        Assert.Empty(notes);
    }

    [Fact]
    public void Check_help_carries_the_whole_exclusion_list()
    {
        // BundleDrift owns that sentence because §6.2 requires the exclusion list to be closed and
        // visible to the operator rather than a property of whatever the implementation happens to do.
        // Compared with whitespace collapsed, since the help renderer wraps to the console width.
        var result = Run("generate", "--help");

        Assert.Contains(Collapse(BundleDrift.CheckDescription), Collapse(result.Output), StringComparison.Ordinal);
    }

    [Fact]
    public void The_help_keeps_the_clauses_that_make_its_two_hazard_sentences_true()
    {
        // Nothing in this repository pinned user-facing help text, and this branch shipped three
        // false sentences that lived exactly there -- "no process is spawned", and
        // --max-file-size's unqualified "a larger file is skipped and counted". A correction to one
        // of those moved rather than propagated four separate times, because the only thing holding
        // the text was prose review.
        //
        // What is asserted is the CLAUSE that makes each sentence true, not the sentence: a
        // substring long enough to be specific to the qualification and short enough to survive the
        // rest being reworded. Dropping the qualification -- back to "A larger file is skipped and
        // counted, which makes the run partial", or back to "no process is spawned" -- turns this
        // red. README prose is deliberately NOT pinned here: it changes for good reasons, and a
        // substring test over it would be noise.
        var help = Collapse(Run("generate", "--help").Output);

        Assert.Contains("but drops an over-cap item silently", help, StringComparison.Ordinal);
        Assert.Contains(
            "It does not make the run process-free: `git` still runs in the scanned tree",
            help,
            StringComparison.Ordinal);

        // The clause that used to ride beside the first one, unpinned and false: the sentence ended
        // "nothing reports it, and the project simply fails to compile", which holds at neither
        // branch. A dropped file nothing else uses leaves a clean compilation reported `Compiled`,
        // and a project whose every item was dropped IS reported -- by ReportProjects' second note,
        // added in the same commit as the sentence denying it. The replacement makes no claim about
        // the compilation at all, and this pins the refusal to make one.
        Assert.Contains(
            "any consequence this run can see is reported by its per-project notes",
            help,
            StringComparison.Ordinal);
    }

    private static string Collapse(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    // ---- the fixture repository -------------------------------------------------------------

    /// <summary>
    /// A repository with one npm package, one public type carrying one public method, one
    /// <c>internal</c> type, and one type under <c>tests/</c> -- one occurrence of each thing a §9 flag
    /// switches on, and deliberately <b>no</b> <c>.csproj</c>, so no test here spawns
    /// <c>dotnet msbuild</c>.
    /// </summary>
    private static ProducerFixture.TempDir NewWorkspace(out string repoPath, out string bundlePath)
    {
        var workspace = new ProducerFixture.TempDir();
        repoPath = Path.Combine(workspace.Path, "demo-repo");
        bundlePath = Path.Combine(workspace.Path, "bundle");

        Directory.CreateDirectory(repoPath);
        File.WriteAllText(
            Path.Combine(repoPath, "package.json"),
            """{ "name": "demo-lib", "description": "The one package of the CLI fixture repository." }""");

        WriteSource(repoPath, "src/Widget.cs", WidgetSource());
        WriteSource(repoPath, "tests/Probe.cs", ProbeSource);

        return workspace;
    }

    private const string WidgetHead = """
        namespace Demo;

        internal class Hidden
        {
        }

        /// <summary>The one type this fixture repository declares.</summary>
        public class Widget
        {
            /// <summary>Runs the widget.</summary>
            public void Run()
            {
            }
        """;

    private const string ProbeSource = """
        namespace Probes;

        /// <summary>A type under tests/, so it is out of scope unless --include-tests is passed.</summary>
        public class Probe
        {
            /// <summary>Pokes the widget.</summary>
            public void Poke()
            {
            }
        }
        """;

    /// <summary>A second public method for <c>Widget</c>, which is the drift <c>--check</c> must find.</summary>
    private const string AddedMember =
        "\n    /// <summary>Added by a test, so the source moves on.</summary>\n"
        + "    public void Added()\n    {\n    }\n";

    private static string WidgetSource(string extraMembers = "") => WidgetHead + extraMembers + "\n}\n";

    /// <summary>
    /// Writes one source file with <c>\n</c> endings whatever git checked this test file out with, so
    /// nothing here depends on the working tree's line-ending configuration.
    /// </summary>
    private static void WriteSource(string repoPath, string relativePath, string source)
    {
        var path = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, source.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void Commit(string repoPath)
    {
        ProducerFixture.RequireGit();
        ProducerFixture.Git(repoPath, "init", "-q");
        ProducerFixture.Git(repoPath, "config", "user.email", "cli-tests@example.invalid");
        ProducerFixture.Git(repoPath, "config", "user.name", "CLI Tests");
        ProducerFixture.Git(repoPath, "config", "commit.gpgsign", "false");
        ProducerFixture.Git(repoPath, "add", "-A");
        ProducerFixture.Git(repoPath, "commit", "-q", "-m", "fixture");

        Assert.True(ProducerFixture.IsInsideGitRepository(repoPath), "the fixture repository was initialised but has no resolvable HEAD.");
    }

    private static void CommitAndDetach(string repoPath)
    {
        Commit(repoPath);
        ProducerFixture.Git(repoPath, "checkout", "--detach", "-q");

        Assert.Null(GitRevision.CurrentBranch(repoPath));
    }

    // ---- assertions -------------------------------------------------------------------------

    private sealed record CliResult(int ExitCode, string Output, string Error);

    private static CliResult Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exitCode = OkfgenCli.Run(args, output, error);

        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private static string ConceptPath(string bundlePath, string conceptId) =>
        Path.Combine(bundlePath, conceptId.Replace('/', Path.DirectorySeparatorChar) + ".md");

    private static Frontmatter Frontmatter(string bundlePath, string conceptId)
    {
        AssertPresent(bundlePath, conceptId);
        return OkfDocument.Parse(File.ReadAllText(ConceptPath(bundlePath, conceptId))).Frontmatter;
    }

    private static void AssertPresent(string bundlePath, string conceptId) =>
        Assert.True(File.Exists(ConceptPath(bundlePath, conceptId)), $"expected concept '{conceptId}'. The bundle holds:\n  {string.Join("\n  ", RelativeFiles(bundlePath))}");

    private static void AssertAbsent(string bundlePath, string conceptId) =>
        Assert.False(File.Exists(ConceptPath(bundlePath, conceptId)), $"did not expect concept '{conceptId}'.");

    private static IReadOnlyList<string> RelativeFiles(string root) =>
        Directory.Exists(root)
            ? [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)]
            : [];

    private static void AssertSameBytes(string expectedRoot, string actualRoot)
    {
        var expected = ProducerFixture.SnapshotFiles(expectedRoot);
        var actual = ProducerFixture.SnapshotFiles(actualRoot);

        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), actual.Keys.OrderBy(k => k, StringComparer.Ordinal));

        foreach (var (path, bytes) in expected)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(actual[path]), $"'{path}' differs between the two runs.");
        }
    }

    /// <summary>
    /// Rewrites one concept's <c>description</c>, its <c>description_source</c> label and the body
    /// paragraph carrying the same text -- the state an ordinary hand edit leaves behind.
    /// </summary>
    private static void SetDescription(string bundlePath, string conceptId, string description, string source)
    {
        var path = ConceptPath(bundlePath, conceptId);
        var document = OkfDocument.Parse(File.ReadAllText(path));

        document.Frontmatter.Set("description", new OKF4net.Yaml.YamlString(description));
        document.Frontmatter.Set(DescriptionResolver.DescriptionSourceKey, new OKF4net.Yaml.YamlString(source));

        var body = document.Body;
        var afterHeading = body.IndexOf("\n\n", StringComparison.Ordinal);
        Assert.True(afterHeading >= 0, $"'{conceptId}' has no heading paragraph break; the body shape assumed here has changed.");

        var nextSection = body.IndexOf("\n\n##", afterHeading + 2, StringComparison.Ordinal);
        Assert.True(nextSection >= 0, $"'{conceptId}' has no section after its description; the body shape assumed here has changed.");

        File.WriteAllText(path, new OkfDocument(document.Frontmatter, body[..(afterHeading + 2)] + description + body[nextSection..]).Serialize());
        IndexGenerator.RegenerateIndexes(bundlePath);
    }
}
