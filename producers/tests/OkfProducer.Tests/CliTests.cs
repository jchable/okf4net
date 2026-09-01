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
    public void Check_help_carries_the_whole_exclusion_list()
    {
        // BundleDrift owns that sentence because §6.2 requires the exclusion list to be closed and
        // visible to the operator rather than a property of whatever the implementation happens to do.
        // Compared with whitespace collapsed, since the help renderer wraps to the console width.
        var result = Run("generate", "--help");

        Assert.Contains(Collapse(BundleDrift.CheckDescription), Collapse(result.Output), StringComparison.Ordinal);
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
