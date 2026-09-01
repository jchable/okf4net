// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections;
using OKF4net;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;
using OkfProducer.Core.Validation;

// `CodeGraph` alone would bind to the sibling namespace OkfProducer.Tests.CodeGraph, not to the type
// (CS0118) -- see the same alias, and the same reason, at the top of ConceptGenerator.cs.
using CodeGraphModel = OkfProducer.Core.CodeGraph.CodeGraph;

namespace OkfProducer.Tests.Generation;

/// <summary>
/// §6.3: transactional writes, the generation manifest, and the pruning that keeps a bundle honest.
///
/// <para><b>What every test here is really guarding.</b> Every other stage of this producer can be
/// wrong and produce a bad bundle; this one can be wrong and delete work. So each assertion below is
/// written to fail in <i>both</i> directions where that is possible: a test that a concept was deleted
/// also asserts that its sibling was not, because a writer that deletes everything would otherwise
/// pass it just as happily as a correct one.</para>
/// </summary>
public class PruningTests
{
    /// <summary>The prefix this producer claims, as the CLI will pass it.</summary>
    private const string Prefix = "code";

    /// <summary>The one file every fixture concept is declared in, unless a test says otherwise.</summary>
    private const string SharedSource = "src/T.cs";

    private const string A = "code/csharp/n/t/a";
    private const string B = "code/csharp/n/t/b";

    // ---------------------------------------------------------------------------------------------
    // The five rules of §6.3, in the order the brief states them.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_deleted_method_loses_its_concept_on_the_next_run()
    {
        // The defect §6.3 exists to fix: WritePolicy.Update never deletes, so a
        // removed method keeps a concept pointing at code that no longer exists,
        // and an agent gets a confidently wrong answer.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);
        var result = WriteRun(tmp, [A], complete: true);

        Assert.False(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));

        // Both halves, or the test passes for a writer that deletes the whole bundle.
        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/a.md")));
        Assert.Equal(new[] { B }, result.Pruned.Select(id => id.ToString()));
    }

    [Fact]
    public void An_incomplete_run_deletes_nothing()
    {
        // "Absent from this run" has two causes -- the symbol is gone, or the
        // file could not be read. They are indistinguishable, so a degraded run
        // must not prune.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);
        var result = WriteRun(tmp, [A], complete: false);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Empty(result.Pruned);

        // §6.3 rule 1: a degraded run "deletes nothing AND SAYS SO".
        Assert.Contains(result.Notes, n => n.Contains("did not visit every eligible file", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hand_written_concept_under_the_owned_prefix_is_never_deleted()
    {
        // Pruning is keyed on the PREVIOUS manifest, not on the prefix, so a
        // file the generator never produced is not its to delete.
        using var tmp = new TempDir();
        WriteRun(tmp, [A], complete: true);
        File.WriteAllText(Path.Combine(tmp.Path, "code/csharp/n/t/human.md"), "---\ntype: Note\n---\nMine.\n");

        var result = WriteRun(tmp, [A], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/human.md")));
        Assert.Contains(result.Notes, n => n.Contains("code/csharp/n/t/human", StringComparison.Ordinal)
            && n.Contains("no manifest claims it", StringComparison.Ordinal));
    }

    [Fact]
    public void Anything_outside_the_owned_prefix_is_preserved()
    {
        // Weak on its own -- Update has always preserved what it did not write, so this passes
        // against a writer with no pruning at all. Its non-vacuous companion is
        // A_previous_manifest_naming_something_outside_the_owned_prefix_cannot_delete_it, where the
        // manifest DOES claim the outside file and the prefix check is the only thing stopping it.
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "notes.md"), "---\ntype: Note\n---\nKeep me.\n");

        WriteRun(tmp, [A], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "notes.md")));
    }

    [Fact]
    public void A_failure_mid_write_leaves_the_bundle_untouched()
    {
        // Staging: the bundle is only touched once the whole run succeeded.
        using var tmp = new TempDir();
        WriteRun(tmp, [A], complete: true);
        var before = File.ReadAllBytes(Path.Combine(tmp.Path, "code/csharp/n/t/a.md"));

        Assert.Throws<InvalidOperationException>(() => WriteRunThatThrows(tmp));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(tmp.Path, "code/csharp/n/t/a.md")));
    }

    [Fact]
    public void A_failure_mid_write_leaves_no_staging_directory_behind()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A], complete: true);

        Assert.Throws<InvalidOperationException>(() => WriteRunThatThrows(tmp));

        Assert.Empty(Directory.EnumerateDirectories(tmp.Root, ".okfgen-staging-*"));
    }

    [Fact]
    public void A_reset_that_fails_while_generating_leaves_the_bundle_it_was_going_to_replace()
    {
        // Reset used to delete the bundle at the TOP of Write, before the staging directory existed --
        // so anything throwing afterwards left an empty directory where the bundle had been, while
        // IBundleWriter promised without qualification that "a run that fails while generating leaves
        // the bundle exactly as it was". The delete belongs at the commit boundary, where the rest of
        // the write already is.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);
        var before = ProducerFixture.SnapshotFiles(tmp.Path);
        Assert.NotEmpty(before);

        Assert.Throws<InvalidOperationException>(() => WriteRunThatThrows(tmp, WritePolicy.Reset));

        // Byte for byte over the whole bundle, not "the file is still there": the failure mode is an
        // EMPTY directory, which a per-file assertion on one concept would also catch, but a partial
        // wipe would not.
        var after = ProducerFixture.SnapshotFiles(tmp.Path);
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(before, entry => Assert.True(entry.Value.AsSpan().SequenceEqual(after[entry.Key]), $"'{entry.Key}' changed."));
    }

    // ---------------------------------------------------------------------------------------------
    // Rule 3: scope restricted to owners that succeeded.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_concept_whose_source_file_could_not_be_read_survives_a_complete_traversal()
    {
        // The traversal visited everything (TraversalComplete is true, which is what gates pruning at
        // all), but the one file these concepts are declared in came back unreadable. Their absence
        // from this run says nothing about whether the symbols still exist.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        var result = WriteRun(tmp, [], complete: true, attempted: [(SharedSource, FileStatus.SkippedUnreadable)]);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/a.md")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Empty(result.Pruned);
        Assert.Contains(result.Notes, n => n.Contains(SharedSource, StringComparison.Ordinal)
            && n.Contains("SkippedUnreadable", StringComparison.Ordinal));
    }

    [Fact]
    public void A_partially_extracted_file_holds_its_concepts_back_too()
    {
        // PartiallyExtracted is the STEADY STATE of a modern C# repository (the vendored grammar
        // mis-parses `[]`), not an exotic failure -- and a declaration lost inside a parse-error region
        // looks exactly like a deleted one.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        var result = WriteRun(tmp, [A], complete: true, attempted: [(SharedSource, FileStatus.PartiallyExtracted)]);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Empty(result.Pruned);
    }

    [Fact]
    public void A_degraded_run_does_not_forget_what_it_could_not_confirm()
    {
        // The manifest a degraded run leaves behind must still claim the ids it held back, or the next
        // complete run would have no mandate to delete them and the stale concept would outlive the
        // very mechanism built to remove it.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);
        WriteRun(tmp, [], complete: true, attempted: [(SharedSource, FileStatus.SkippedUnreadable)]);

        var result = WriteRun(tmp, [A], complete: true);

        Assert.False(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Equal(new[] { B }, result.Pruned.Select(id => id.ToString()));
    }

    [Fact]
    public void A_deleted_source_file_loses_its_concepts()
    {
        // The same defect one level up: deleting a whole file must not leave its concepts behind. The
        // traversal was complete and the file is not in the repository any more, so its absence from
        // this run's attempted set is a fact, not an omission.
        using var tmp = new TempDir();
        tmp.WriteSource("src/X.cs");
        WriteRun(tmp, [A, "code/csharp/n/x/y"], complete: true,
            sources: Owners(("code/csharp/n/x/y", ["src/X.cs"])),
            attempted: [(SharedSource, FileStatus.Extracted), ("src/X.cs", FileStatus.Extracted)]);

        File.Delete(Path.Combine(tmp.RepoPath, "src", "X.cs"));
        var result = WriteRun(tmp, [A], complete: true);

        Assert.False(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/x/y.md")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/a.md")));
        Assert.Equal(new[] { "code/csharp/n/x/y" }, result.Pruned.Select(id => id.ToString()));
    }

    [Fact]
    public void A_source_file_that_still_exists_but_was_not_visited_keeps_its_concepts()
    {
        // Identical to the test above except that the file is still on disk. It fell out of scope --
        // a changed extension set, a scope flag, an ignore rule -- and none of those say the symbol is
        // gone. This pair is what makes both halves of the "settled" rule load-bearing: drop the
        // filesystem check and this test goes red, drop the attempted-set check and the previous one
        // does.
        using var tmp = new TempDir();
        tmp.WriteSource("src/X.cs");
        WriteRun(tmp, [A, "code/csharp/n/x/y"], complete: true,
            sources: Owners(("code/csharp/n/x/y", ["src/X.cs"])),
            attempted: [(SharedSource, FileStatus.Extracted), ("src/X.cs", FileStatus.Extracted)]);

        var result = WriteRun(tmp, [A], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/x/y.md")));
        Assert.Empty(result.Pruned);
        Assert.Contains(result.Notes, n => n.Contains("src/X.cs", StringComparison.Ordinal)
            && n.Contains("still exists in the repository", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_this_run_could_not_read_is_not_treated_as_deleted_just_because_the_writer_cannot_find_it()
    {
        // The run's own record outranks the filesystem check that stands behind it. Without this
        // ordering, a --repo that no longer points where the sources are -- a moved checkout, a stale
        // path, a subdirectory passed by mistake -- would turn every file the run failed to read into a
        // deleted one and take its concepts with it. The owner here was attempted and came back
        // unreadable, and is not where the writer would look for it: exactly that combination.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true, sources: Owners((B, ["src/Gone.cs"])));

        var result = WriteRun(tmp, [A], complete: true,
            attempted: [(SharedSource, FileStatus.Extracted), ("src/Gone.cs", FileStatus.SkippedUnreadable)]);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Empty(result.Pruned);
    }

    // ---------------------------------------------------------------------------------------------
    // The gates: every one of them, and each one refusing on its own.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_that_analysed_no_file_at_all_deletes_nothing()
    {
        // The --no-code shape: a run with no code stage produces no code ids and a traversal that
        // trivially completed. Without this gate its empty result would read as "every symbol in the
        // repository was deleted".
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        var result = WriteRun(tmp, [], complete: true, attempted: []);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/a.md")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Contains(result.Notes, n => n.Contains("analysed no source file at all", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_with_a_write_failure_deletes_nothing()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        // `index` is reserved by BundleConceptWriter, so this concept fails to write while the rest
        // succeed -- a run that did not fully succeed, which rule 1 bars from pruning.
        var result = WriteRun(tmp, [A, "code/csharp/n/t/index"], complete: true);

        Assert.Single(result.Failures);
        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Contains(result.Notes, n => n.Contains("could not be written", StringComparison.Ordinal));
    }

    [Fact]
    public void An_id_that_failed_to_write_is_not_recorded_as_owned()
    {
        // A manifest entry is a licence to delete whatever later appears at that path. An id this run
        // never actually wrote must not get one, or a human filling that gap by hand would have their
        // file deleted by the next run -- the very thing rule 2 exists to prevent.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, "code/csharp/n/t/index"], complete: true);

        var manifest = GenerationManifest.TryRead(tmp.Path);

        Assert.NotNull(manifest);
        Assert.DoesNotContain("code/csharp/n/t/index", manifest.ConceptIds);
        Assert.Contains(A, manifest.ConceptIds);
    }

    [Fact]
    public void A_run_whose_repository_path_is_gone_deletes_nothing()
    {
        // "Not visited, and not on disk => deleted" is only sound while there is a disk to look at.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        var result = WriteRun(tmp, [A], complete: true, repoPath: Path.Combine(tmp.Root, "no-such-repo"));

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Contains(result.Notes, n => n.Contains("does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_claiming_a_different_prefix_deletes_nothing()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        var result = WriteRun(tmp, [A], complete: true, ownedPrefix: "generated");

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Contains(result.Notes, n => n.Contains("previous manifest claims", StringComparison.Ordinal));
    }

    [Fact]
    public void A_caller_that_supplies_no_manifest_deletes_nothing()
    {
        // The default of the API is the safe one: an Update that says nothing about what it covered
        // behaves exactly as it did before pruning existed.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        new BundleWriter().Write(tmp.Path, [Concept(A, [SharedSource])], WritePolicy.Update, tmp.RepoPath);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
    }

    // ---------------------------------------------------------------------------------------------
    // The manifest is a file in a directory the user controls, so it is treated as input.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    // DISCRIMINATING. Both of these PARSE -- ConceptId.Parse drops empty segments, so each is a
    // different spelling of `code/csharp/n/t/a`, an id this bundle really holds. What refuses them is
    // the round-trip check, `parsed.ToString() != id`, and that check is the only part of
    // TryResolveConceptFile's validation a future edit can actually drop: the method returns a
    // ConceptId?, so TryParse cannot be removed without changing its type. Drop the round trip and
    // both rows resolve to `<bundle>/code/csharp/n/t/a.md`, which EXISTS, and delete a live concept --
    // the assertion on `a.md` below is what goes red.
    [InlineData("code//csharp/n/t/a")]
    [InlineData("code/csharp/n/t/a/")]
    // NOT DISCRIMINATING, and kept anyway with that said out loud. `..` is not a legal segment
    // (a segment is [A-Za-z0-9_][A-Za-z0-9_.-]*, and `.` is not a valid first character), so this row
    // is refused by ConceptId.Parse inside src/OKF4net and never reaches anything this producer owns.
    // The previous round's mutation table claimed a naive join made rows like this fire; it does not,
    // and neither does `code/csharp/n/t/..`, which was dropped from this table for that reason. Two
    // separate things stand behind Parse here and each is unfalsifiable on its own: the charset admits
    // no separator, no drive letter and no dotted segment, so no id this producer can parse joins to a
    // path outside the root, and any that did would be refused by the lexical IsInside check before
    // the filesystem is consulted. This row is a boundary case pinned against a future relaxation of
    // the charset -- a smoke check, not proof. What defends the bundle today against a path that
    // really does leave it is the reparse-point resolution, and the test below is the one that
    // reaches it.
    [InlineData("code/../../victim")]
    public void A_manifest_id_that_is_not_this_producers_own_spelling_of_it_resolves_to_nothing(string hostile)
    {
        // No `victim.md` outside the bundle any more, and no assertion that it survived. It was
        // inert for every row: the two discriminating rows resolve to `a.md` INSIDE the bundle and
        // can never reach it, and the `..` row is refused by ConceptId.Parse inside src/OKF4net
        // before this producer sees it. No production edit could turn that assertion red, so it read
        // as coverage the table did not have. The assertion below is the whole of this test's power.
        using var tmp = new TempDir();

        WriteRun(tmp, [A], complete: true);
        PlantManifestIds(tmp, Prefix, [A, hostile]);

        var result = WriteRun(tmp, [A], complete: true);

        Assert.True(
            File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/a.md")),
            "a manifest id spelled differently from the concept it names deleted that concept.");
        Assert.Empty(result.Pruned);
        Assert.Contains(result.Notes, n => n.Contains("does not resolve to a file inside the bundle", StringComparison.Ordinal));
    }

    [Fact]
    public void A_concept_the_bundle_reaches_only_through_a_link_is_never_deleted()
    {
        // THE ONE THAT WAS REACHABLE. Path.GetFullPath resolves `.` and `..` and nothing else -- it does
        // not follow a symbolic link or a junction -- so `candidate.StartsWith(root + sep)` answered
        // "inside the bundle" for a path whose directory was a link to somewhere else, and File.Delete
        // followed the link. Reachable in the workflow the README documents: clone an untrusted
        // repository whose committed bundle holds `code/x -> ~/notes` plus a manifest claiming
        // `code/x/report`, and every gate passes -- named by the previous manifest, under the owned
        // prefix, owned by a file this run read cleanly, lexically inside the root.
        using var tmp = new TempDir();

        var outside = Path.Combine(tmp.Root, "notes");
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "report.md");
        File.WriteAllText(victim, "someone's notes, outside the bundle entirely");

        WriteRun(tmp, [A], complete: true);

        var link = Path.Combine(tmp.Path, "code", "x");
        CreateDirectoryLink(link, outside);

        // The link is real, and the bundle really can see through it -- without this the test would
        // pass on a platform where the link silently did not happen.
        Assert.True(File.Exists(Path.Combine(link, "report.md")), "the bundle cannot see through the link, so this fixture proves nothing.");

        PlantManifestIds(tmp, Prefix, [A, "code/x/report"]);

        var result = WriteRun(tmp, [A], complete: true);

        Assert.True(File.Exists(victim), "File.Delete followed a link out of the bundle and destroyed a file outside it.");
        Assert.Empty(result.Pruned);
        Assert.Contains(result.Notes, n => n.Contains("code/x/report", StringComparison.Ordinal)
            && n.Contains("symbolic link or junction", StringComparison.Ordinal));
    }

    [Fact]
    public void A_concept_whose_destination_leaves_the_bundle_through_a_link_is_never_written_through_it()
    {
        // The WRITE side of the hole whose delete side is the test above, and it was still open after
        // that one was closed: File.Move(overwrite: true) follows a link exactly as File.Delete does,
        // and CommitStaging built its destination by joining a relative path onto the bundle root --
        // string work, which Path.GetFullPath cannot see through. Same fixture as the delete case,
        // same clone-an-untrusted-repository reachability: the bundle carries `code/x -> ~/notes`, the
        // generator produces `code/x/report`, and the commit overwrote a file outside the bundle.
        using var tmp = new TempDir();

        var outside = Path.Combine(tmp.Root, "notes");
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "report.md");
        File.WriteAllText(victim, "someone's notes, outside the bundle entirely");
        var before = File.ReadAllBytes(victim);

        WriteRun(tmp, [A], complete: true);

        var link = Path.Combine(tmp.Path, "code", "x");
        CreateDirectoryLink(link, outside);
        Assert.True(File.Exists(Path.Combine(link, "report.md")), "the bundle cannot see through the link, so this fixture proves nothing.");

        var result = WriteRun(tmp, [A, "code/x/report"], complete: true);

        Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(victim)),
            "File.Move followed a link out of the bundle and overwrote a file outside it.");

        // Recorded as a write FAILURE, not merely skipped, and both consequences are asserted because
        // each is load-bearing on its own. A refused id must never be claimed as owned -- a manifest
        // entry is a standing licence to delete whatever later appears at that path -- and a run that
        // could not commit everything must not go on to prune.
        var failure = Assert.Single(result.Failures, f => f.Id.ToString() == "code/x/report");
        Assert.Contains("symbolic link", failure.Error, StringComparison.Ordinal);
        Assert.Contains("code/x/report.md", failure.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("code/x/report", GenerationManifest.TryRead(tmp.Path)!.ConceptIds);

        // ONCE, not twice. The refusal used to be added to the notes AND recorded as a failure, so
        // OkfgenCli printed the same sentence under two prefixes -- "note: Error: refused to write ..."
        // and "error: code/x/report: Error: refused to write ..." -- which reads as two problems.
        Assert.DoesNotContain(result.Notes, n => n.Contains("refused to write", StringComparison.Ordinal));

        // AND NOT THE OPPOSITE NOTE. ReportClaimedFiles builds this same destination by string join
        // and probes it with File.Exists, which follows the junction and answers for the file at the
        // far end -- so the run announced that it "has taken ownership of that id and overwritten the
        // file ... its previous body, description and any keys this producer does not write are gone",
        // about a file outside the bundle that it then refused to write. The refusal assertion above
        // was green throughout: only this one catches it.
        Assert.DoesNotContain(result.Notes, n => n.Contains("taken ownership", StringComparison.Ordinal));

        // The other direction: the concept that had nothing to do with the link was still written, so
        // this is a refusal of one destination and not a writer that gave up on the run.
        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/a.md")));
    }

    [Fact]
    public void A_file_the_bundle_reaches_only_through_a_link_is_not_reported_as_an_unowned_concept()
    {
        // The READ side of the same hole, and the last place in this writer that reached outside the
        // bundle. ReportUnownedFiles lists the owned prefix with
        // Directory.EnumerateFiles(SearchOption.AllDirectories), whose default EnumerationOptions skip
        // Hidden and System entries -- and a junction is neither, so the walk descends it. Measured
        // before it was fixed: the run announced "'code/x/report' sits under the owned prefix 'code'
        // but no manifest claims it; it was left in place and will never be pruned" about a file that
        // is not in the bundle, has no concept id, and was never this producer's to describe.
        using var tmp = new TempDir();

        var outside = Path.Combine(tmp.Root, "notes");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "report.md"), "---\ntype: Note\ntitle: Mine\n---\n\nsomeone's notes\n");

        WriteRun(tmp, [A], complete: true);

        var link = Path.Combine(tmp.Path, "code", "x");
        CreateDirectoryLink(link, outside);
        Assert.True(File.Exists(Path.Combine(link, "report.md")), "the bundle cannot see through the link, so this fixture proves nothing.");

        var result = WriteRun(tmp, [A], complete: true);

        Assert.DoesNotContain(result.Notes, n => n.Contains("code/x/report", StringComparison.Ordinal));

        // The report is silenced for the linked-to file only, not switched off. A genuine unowned file
        // inside the bundle is still named -- without this, "return early and say nothing" passes.
        File.WriteAllText(Path.Combine(tmp.Path, "code", "csharp", "n", "t", "human.md"), "---\ntype: Note\n---\nMine.\n");
        var third = WriteRun(tmp, [A], complete: true);
        Assert.Contains(third.Notes, n => n.Contains("code/csharp/n/t/human", StringComparison.Ordinal)
            && n.Contains("no manifest claims it", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bundle_root_that_is_itself_a_link_is_written_through_normally()
    {
        // NOT a mutation witness for the fix it accompanies, and labelled so rather than left to look
        // like one. CommitStaging used to build its destination from the caller's `--out` while
        // resolving `root` from it, breaking the precondition ResolveInsideRoot depends on: with a
        // linked `--out`, Path.GetRelativePath(root, destination) comes back ABSOLUTE (cross-volume)
        // or `..`-prefixed (same volume), and the component walk restarts from the drive and climbs
        // the whole path. Measured on this host: it still lands inside, because it meets the same link
        // again on the way down, so no run was ever refused for it -- the cost was that containment
        // was being decided by resolving directories that have nothing to do with the bundle.
        //
        // What this test does guard is the direction a careless repair breaks: a gate that refused
        // whenever the candidate is not lexically under the resolved root would reject every file of
        // every run whose bundle is a symlink or junction -- a normal setup (a symlinked project
        // directory, a container or WSL bind mount).
        using var tmp = new TempDir();

        var real = Path.Combine(tmp.Root, "real-bundle");
        Directory.CreateDirectory(real);
        var linked = Path.Combine(tmp.Root, "linked-bundle");
        CreateDirectoryLink(linked, real);

        var concepts = new[] { Concept(A, [SharedSource]) };
        var status = new RunStatus(true, [(SharedSource, FileStatus.Extracted)]);
        var manifest = GenerationManifest.ForRun(Prefix, concepts, status, ScopeOptions.Default);

        var result = new BundleWriter().Write(linked, concepts, WritePolicy.Update, tmp.RepoPath, manifest, status);

        Assert.Empty(result.Failures);
        Assert.Equal(1, result.Written);

        // Landed in the REAL directory, not merely "somewhere the link can see": a writer that
        // silently created a second tree beside the link would satisfy a check through `linked`.
        Assert.True(File.Exists(Path.Combine(real, "code", "csharp", "n", "t", "a.md")));
    }

    [Fact]
    public void The_directory_cleanup_after_a_prune_removes_no_link_the_bundle_merely_holds()
    {
        // RemoveEmptyDirectories climbs from a pruned concept's directory to the owned prefix calling
        // Directory.Delete(recursive: true), and it built those paths by string concatenation too. Here
        // the link points back INSIDE the bundle, so the concept file itself resolves inside and is
        // genuinely this producer's to delete -- which is what gets the walk started. The link it walks
        // through is still a piece of structure the operator put there, and no prune may remove it.
        using var tmp = new TempDir();
        WriteRun(tmp, [A], complete: true);

        var real = Path.Combine(tmp.Path, "code", "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "b.md"), "---\ntype: Note\n---\n\nreachable two ways.\n");

        var link = Path.Combine(tmp.Path, "code", "csharp", "n", "t2");
        CreateDirectoryLink(link, real);

        PlantManifestIds(tmp, Prefix, [A, "code/csharp/n/t2/b"]);

        var result = WriteRun(tmp, [A], complete: true);

        // Both halves. The prune happened (so the walk really ran) ...
        Assert.Equal(new[] { "code/csharp/n/t2/b" }, result.Pruned.Select(id => id.ToString()));

        // ... and the link survived it, still a link rather than a directory the cleanup recreated.
        Assert.True(Directory.Exists(link), "the directory cleanup deleted a link the bundle only pointed through.");
        Assert.NotNull(new DirectoryInfo(link).LinkTarget);
    }

    [Fact]
    public void A_previous_manifest_naming_something_outside_the_owned_prefix_cannot_delete_it()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A], complete: true);
        File.WriteAllText(Path.Combine(tmp.Path, "notes.md"), "---\ntype: Note\n---\nKeep me.\n");
        PlantManifestIds(tmp, Prefix, [A, "notes"]);

        var result = WriteRun(tmp, [A], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "notes.md")));
        Assert.Empty(result.Pruned);
        Assert.Contains(result.Notes, n => n.Contains("outside the owned prefix", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unreadable_manifest_is_treated_as_no_manifest_rather_than_an_error()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);
        File.WriteAllText(Path.Combine(tmp.Path, GenerationManifest.FileName), "{ this is not json");
        Assert.Null(GenerationManifest.TryRead(tmp.Path));

        var result = WriteRun(tmp, [A], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Empty(result.Pruned);
    }

    [Fact]
    public void A_manifest_this_build_cannot_read_is_reported_once_as_a_manifest_and_not_once_per_file()
    {
        // The upgrade path, and the shape the schema bump gave every bundle already on disk. A v1
        // manifest -- or a corrupt one -- is not read at all, so `previous` is null, so every file this
        // run overwrote under the owned prefix looked like a file "no previous manifest claimed",
        // and each got its own alarming note asserting that its body and description were gone. On a
        // real repository that is one line per code concept, on an upgrade, about this producer's own
        // output, and most of it untrue: the manifest is sitting right there.
        //
        // Both halves of the fix are asserted, because either alone passes for the wrong reason: the
        // storm is gone AND the fact that produced it is still reported, once, where it belongs.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);
        File.WriteAllText(Path.Combine(tmp.Path, GenerationManifest.FileName), "{ this is not json");

        var result = WriteRun(tmp, [A, B], complete: true);

        Assert.DoesNotContain(result.Notes, n => n.Contains("taken ownership", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("could not read it", StringComparison.Ordinal)
            && n.Contains("pruned no concept", StringComparison.Ordinal));

        // ONE note, not two: this run overwrote two files it could not account for and must not have
        // said so twice.
        Assert.Single(result.Notes, n => n.Contains("could not read it", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("2 file(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unreadable_manifest_over_a_run_that_overwrote_nothing_does_not_say_it_overwrote_zero_files()
    {
        // The note is emitted whenever a manifest is present and unread, including on a run with
        // nothing to overwrite -- where it rendered "it cannot say which of the 0 file(s) it
        // overwrote under the owned prefix 'code' it had written before; if a concept under that
        // prefix was hand-written, check it against version control now". That is an instruction to
        // go looking for files that do not exist, attached to the one note whose entire purpose is to
        // tell an operator whether they need to look.
        using var tmp = new TempDir();

        // An unread manifest in a bundle holding no concept under the owned prefix, so the run
        // overwrites nothing at all -- the fact still has to be reported, just not with a count.
        File.WriteAllText(Path.Combine(tmp.Path, GenerationManifest.FileName), "{ this is not json");

        var result = WriteRun(tmp, [A], complete: true);

        var note = Assert.Single(result.Notes, n => n.Contains("could not read it", StringComparison.Ordinal));
        Assert.DoesNotContain("0 file(s)", note, StringComparison.Ordinal);

        // Still reported, and still says why nothing was pruned -- "stop emitting the note when the
        // count is zero" would lose the half of it that is always worth saying.
        Assert.Contains("pruned no concept", note, StringComparison.Ordinal);
        Assert.DoesNotContain("check it against version control", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bundle_with_no_manifest_at_all_still_names_every_file_it_took_over()
    {
        // The other side of the distinction, without which the fix above could be "stop reporting
        // overwrites whenever there is no previous manifest" -- which would silence the one case §6.3
        // rule 2 cannot otherwise reach. Nothing was ever written here by this producer, so "no
        // previous manifest claimed it" is a true statement about each file, and each is named.
        using var tmp = new TempDir();
        foreach (var id in new[] { A, B })
        {
            var path = Path.Combine(tmp.Path, id.Replace('/', Path.DirectorySeparatorChar) + ".md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "---\ntype: Note\ntitle: Mine\n---\n\nMy notes.\n");
        }

        Assert.False(GenerationManifest.IsPresent(tmp.Path));

        var result = WriteRun(tmp, [A, B], complete: true);

        Assert.Equal(2, result.Notes.Count(n => n.Contains("taken ownership", StringComparison.Ordinal)));
        Assert.DoesNotContain(result.Notes, n => n.Contains("could not read it", StringComparison.Ordinal));
    }

    [Fact]
    public void The_overwrite_note_calls_a_description_lost_when_the_file_really_loses_it()
    {
        // THE COUNTER-EXAMPLE, promoted to a test. The note's preservation branch used to fire on
        // DescriptionResolver.PreservesDescription -- true of exactly this file, whose author marked
        // it `manual` -- and say "§4.2 kept that description, so it is the one thing the overwrite did
        // not destroy". §4.2 is applied by the GENERATOR, and only when the caller supplies
        // GenerateOptions.ExistingFrontmatter; this caller does not, so the description did NOT
        // survive and the note said it had. Every assertion below is about the bytes on disk after the
        // run, which is the thing the previous version of this test never looked at.
        using var tmp = new TempDir();

        var path = Path.Combine(tmp.Path, "code", "csharp", "n", "t", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"---\ntype: Note\ntitle: Mine\ndescription: Written by a person.\n{DescriptionResolver.DescriptionSourceKey}: {DescriptionResolver.ManualLabel}\n---\n\nMy notes.\n");

        var result = WriteRun(tmp, [A], complete: true);

        // The outcome first, so the note is measured against it rather than against a rule.
        Assert.True(OkfDocument.TryParse(File.ReadAllText(path), out var rewritten, out _));
        Assert.DoesNotContain("Written by a person.", rewritten.Frontmatter.Description ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("My notes.", rewritten.Body, StringComparison.Ordinal);

        var note = Assert.Single(result.Notes, n => n.Contains("taken ownership", StringComparison.Ordinal));
        Assert.Contains($"`{DescriptionResolver.ManualLabel}`", note, StringComparison.Ordinal);
        Assert.Contains("body, description and any keys", note, StringComparison.Ordinal);
        Assert.DoesNotContain("its description is not", note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overwrite_note_spares_a_description_only_when_the_run_writes_the_same_text_back()
    {
        // The other direction, and the only condition this writer can actually establish: the text
        // already on disk is the text the staged document carries, so the overwrite changes no
        // description -- whoever arranged that, and whether §4.2 was consulted at all. The fixture
        // description is the one Concept() generates, which is what makes the two equal here; in the
        // shipped CLI it is GenerateRun's ExistingFrontmatter wiring that makes them equal.
        using var tmp = new TempDir();

        var path = Path.Combine(tmp.Path, "code", "csharp", "n", "t", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"---\ntype: Note\ntitle: Mine\ndescription: d\n{DescriptionResolver.DescriptionSourceKey}: {DescriptionResolver.ManualLabel}\n---\n\nMy notes.\n");

        var result = WriteRun(tmp, [A], complete: true);

        // The description really is still there, and the body really is gone -- the note claims both.
        Assert.True(OkfDocument.TryParse(File.ReadAllText(path), out var rewritten, out _));
        Assert.Equal("d", rewritten.Frontmatter.Description);
        Assert.DoesNotContain("My notes.", rewritten.Body, StringComparison.Ordinal);

        var note = Assert.Single(result.Notes, n => n.Contains("taken ownership", StringComparison.Ordinal));
        Assert.Contains("its description is not", note, StringComparison.Ordinal);
        Assert.DoesNotContain("body, description and any keys", note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overwrite_note_still_says_a_derived_description_is_gone()
    {
        // A `description_source` this producer DOES derive, carrying text the run does not write back.
        // Kept from the previous round because it guards the other blanket failure: a fix that softens
        // the wording for every file carrying the key would say the description survived when it did
        // not. The assertion on the file is what makes that non-vacuous.
        using var tmp = new TempDir();

        var path = Path.Combine(tmp.Path, "code", "csharp", "n", "t", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"---\ntype: Note\ntitle: Mine\ndescription: Derived once.\n{DescriptionResolver.DescriptionSourceKey}: {SignatureSource.SourceLabel}\n---\n\nMy notes.\n");

        var result = WriteRun(tmp, [A], complete: true);

        Assert.True(OkfDocument.TryParse(File.ReadAllText(path), out var rewritten, out _));
        Assert.NotEqual("Derived once.", rewritten.Frontmatter.Description);

        var note = Assert.Single(result.Notes, n => n.Contains("taken ownership", StringComparison.Ordinal));
        Assert.Contains($"`{SignatureSource.SourceLabel}`", note, StringComparison.Ordinal);
        Assert.Contains("body, description and any keys", note, StringComparison.Ordinal);
        Assert.DoesNotContain("its description is not", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_manifest_from_an_unknown_schema_version_authorizes_nothing()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);
        var path = Path.Combine(tmp.Path, GenerationManifest.FileName);
        var text = File.ReadAllText(path);

        // Read off the constant rather than pinned to a literal: with `"version": 1` hard-coded here
        // this test silently stopped substituting anything the moment the schema was bumped, and then
        // asserted that a manifest of the CURRENT version authorizes nothing -- passing only because
        // the run it was aiming at had been replaced by a different one.
        var current = $"\"version\": {GenerationManifest.SchemaVersion}";
        Assert.Contains(current, text, StringComparison.Ordinal);
        File.WriteAllText(path, text.Replace(current, "\"version\": 99", StringComparison.Ordinal));

        var result = WriteRun(tmp, [A], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Empty(result.Pruned);
    }

    // ---------------------------------------------------------------------------------------------
    // The manifest is written output (§6.2), so it obeys the same rules as the bundle.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_manifest_is_not_discovered_as_a_concept()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, GenerationManifest.FileName)));

        // Both halves, because "not a concept" is not the property that matters -- "invisible to the
        // bundle model" is. Name the manifest `.md` and it stops being a concept only by failing to
        // parse as one, which is a diagnostic in every `okf validate` run from then on.
        var bundle = Bundle.Load(tmp.Path);
        Assert.DoesNotContain(bundle.Concepts, c => c.Path.EndsWith(GenerationManifest.FileName, StringComparison.Ordinal));
        Assert.DoesNotContain(bundle.ParseErrors, e => e.Path.EndsWith(GenerationManifest.FileName, StringComparison.Ordinal));
    }

    [Fact]
    public void The_manifest_is_written_after_the_concepts_it_describes()
    {
        // Ordering, pinned rather than assumed: the manifest is the record of what the bundle holds,
        // so writing it before the staged files are in place would leave, on any failure during the
        // commit, a manifest describing a state the bundle never reached -- and that manifest is the
        // licence the NEXT run deletes by. Written last, an interrupted commit leaves the PREVIOUS
        // manifest: conservative, and self-healing on the next run.
        using var tmp = new TempDir();
        WriteRun(tmp, [A], complete: true);
        var before = File.ReadAllBytes(Path.Combine(tmp.Path, GenerationManifest.FileName));

        // A directory sitting exactly where a concept file must land. The move that commits staging
        // cannot overwrite it, so the failure lands in the one window that distinguishes the two
        // orderings: after staging succeeded, before the manifest would be written.
        Directory.CreateDirectory(Path.Combine(tmp.Path, "code", "csharp", "n", "t", "b.md"));

        Assert.ThrowsAny<SystemException>(() => WriteRun(tmp, [A, B], complete: true));

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(tmp.Path, GenerationManifest.FileName)));
        Assert.Empty(Directory.EnumerateDirectories(tmp.Root, ".okfgen-staging-*"));
    }

    [Fact]
    public void A_bundle_carrying_the_manifest_still_validates()
    {
        // The manifest lives inside the bundle, so "it is not a concept" is only half the claim; the
        // other half is that `okf validate` has nothing to say about it either.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        var outcome = new BundleValidationRunner().Validate(tmp.Path);

        Assert.Equal(0, outcome.ErrorCount);
        Assert.True(outcome.IsConformant);
    }

    [Fact]
    public void The_manifest_is_byte_identical_across_two_runs_over_the_same_input()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);
        var first = File.ReadAllBytes(Path.Combine(tmp.Path, GenerationManifest.FileName));

        WriteRun(tmp, [A, B], complete: true);

        Assert.Equal(first, File.ReadAllBytes(Path.Combine(tmp.Path, GenerationManifest.FileName)));
    }

    [Fact]
    public void The_manifest_sorts_what_it_is_handed_rather_than_recording_the_order_it_arrived_in()
    {
        // Handed in reverse, so the sort chain is what puts it right: delete either OrderBy in
        // GenerationManifest.Normalized and this goes red rather than passing by luck.
        using var tmp = new TempDir();
        new GenerationManifest(
                Prefix,
                [new ManifestConcept(B, ["src/Z.cs", "src/A.cs"]), new ManifestConcept(A, [])],
                ["src/Z.cs", "src/A.cs"],
                ScopeOptions.Default)
            .WriteTo(tmp.Path);

        var text = File.ReadAllText(Path.Combine(tmp.Path, GenerationManifest.FileName));

        Assert.True(text.IndexOf($"\"{A}\"", StringComparison.Ordinal) < text.IndexOf($"\"{B}\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("src/A.cs", StringComparison.Ordinal) < text.IndexOf("src/Z.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void The_manifest_records_the_files_the_run_read_in_full_and_only_those()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A], complete: true,
            attempted: [(SharedSource, FileStatus.Extracted), ("src/X.cs", FileStatus.SkippedTooLarge)]);

        var manifest = GenerationManifest.TryRead(tmp.Path);

        Assert.NotNull(manifest);
        Assert.Equal(new[] { SharedSource }, manifest.ExtractedFiles);
    }

    [Fact]
    public void The_manifest_uses_line_feeds_whatever_the_host_calls_a_newline()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        var text = File.ReadAllText(Path.Combine(tmp.Path, GenerationManifest.FileName));

        Assert.Contains('\n', text);
        Assert.DoesNotContain('\r', text);
    }

    // ---------------------------------------------------------------------------------------------
    // What pruning leaves behind.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Pruning_the_last_concept_in_a_directory_removes_the_directory_too()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A, "code/csharp/n/x/y"], complete: true);

        WriteRun(tmp, [A], complete: true);

        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "code/csharp/n/x")));
        Assert.True(Directory.Exists(Path.Combine(tmp.Path, "code/csharp/n/t")));
    }

    [Fact]
    public void A_directory_still_holding_something_of_someone_elses_is_left_alone()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A, "code/csharp/n/x/y"], complete: true);
        File.WriteAllText(Path.Combine(tmp.Path, "code/csharp/n/x/human.md"), "---\ntype: Note\n---\nMine.\n");

        WriteRun(tmp, [A], complete: true);

        Assert.False(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/x/y.md")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/x/human.md")));
    }

    // ---------------------------------------------------------------------------------------------
    // The ownership the manifest records comes from the generator, not from a second copy of the id rules.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_generated_code_concept_records_the_files_it_was_declared_in()
    {
        var concept = Generated("code/csharp/n/t/scan");

        Assert.Equal(new[] { "src/T.cs" }, concept.SourceFiles);
    }

    [Fact]
    public void A_synthesized_container_records_every_file_beneath_it()
    {
        // A container is not declared anywhere, so with no owners it could never be pruned -- and it
        // can carry a hand-written description that pruning would destroy. Its honest owners are the
        // files of everything nested under it.
        var container = Generated("code/csharp/n");

        Assert.Equal(new[] { "src/Other.cs", "src/T.cs" }, container.SourceFiles);
    }

    [Fact]
    public void A_concept_that_is_not_derived_from_source_records_no_owner_and_is_never_pruned()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A, "code/csharp/n/t/orphan"], complete: true,
            sources: Owners(("code/csharp/n/t/orphan", [])));

        var result = WriteRun(tmp, [A], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/orphan.md")));
        Assert.Empty(result.Pruned);
    }

    // ---------------------------------------------------------------------------------------------
    // Rule 2's other half: an unclaimed file is safe from deletion, and was not safe from overwrite.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_that_takes_over_a_file_no_manifest_claimed_says_which_one_it_overwrote()
    {
        // §6.3 rule 2 stops this producer DELETING a file no manifest claims. Nothing stopped it
        // writing over one: the moment the generator produces the same id, CommitStaging moves the
        // staged file with overwrite: true and the hand-written body and description are gone. There
        // was no signal at all -- ReportUnownedFiles computes owned as (this run's manifest) U (the
        // previous one), and this run's manifest now claims the id, so the file reads as owned.
        using var tmp = new TempDir();

        var path = Path.Combine(tmp.Path, "code", "csharp", "n", "t", "a.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "---\ntype: Note\ntitle: Mine\ndescription: Written by a person.\n---\n\nMy notes.\n");

        var result = WriteRun(tmp, [A], complete: true);

        // The overwrite still happens -- that is the behaviour, stated rather than hidden. The note is
        // the whole of the remedy, so it has to name the file and say what it could not tell about it.
        Assert.DoesNotContain("My notes.", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains(result.Notes, n => n.Contains(A, StringComparison.Ordinal)
            && n.Contains("taken ownership", StringComparison.Ordinal)
            && n.Contains("no `description_source`", StringComparison.Ordinal));
    }

    [Fact]
    public void A_run_over_its_own_previous_output_takes_over_nothing_and_says_nothing()
    {
        // The other direction, without which the note above passes just as happily on a writer that
        // announces an ownership claim for every file it writes -- which would bury the one case that
        // matters under a note per concept per run.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        var result = WriteRun(tmp, [A, B], complete: true);

        Assert.DoesNotContain(result.Notes, n => n.Contains("taken ownership", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------
    // The scope the previous run covered, which is the difference between "deleted" and "out of scope".
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_run_that_narrows_its_scope_deletes_nothing_and_names_the_flag_that_was_dropped()
    {
        // The asymmetry that made this dangerous: FileEligibility filters TESTS at the file level and
        // VISIBILITY at the symbol level. Drop --include-tests and the owning file is never visited, so
        // it is neither attempted nor gone and its concepts are held back -- correct, by accident of
        // where the filter sits. Drop --include-internal and the owning file IS visited and comes back
        // Extracted, so every internal symbol's concept looks settled and was deleted, with any manual
        // description on it, while the run reported the deletion as a symbol gone from the repository.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true, scope: new ScopeOptions(IncludeTests: false, IncludeInternal: true));

        var result = WriteRun(tmp, [A], complete: true, scope: ScopeOptions.Default);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Empty(result.Pruned);
        Assert.Contains(result.Notes, n => n.Contains("--include-internal", StringComparison.Ordinal)
            && n.Contains("out of scope rather than gone", StringComparison.Ordinal));
    }

    [Fact]
    public void The_narrowing_refusal_survives_the_manifest_the_refusing_run_writes()
    {
        // THREE runs, and the third is the one that matters. The refusal above kept B by carrying its
        // manifest entry forward -- but the merged manifest used to record THIS run's narrow scope over
        // a concept set that now included a wide run's ids. The next identical command then compared
        // equal scopes, saw no narrowing, found B settled (its owning file was read cleanly) and
        // deleted it: the refusal was a one-run reprieve, and running the same safe command twice was
        // enough to lose the work it existed to protect.
        //
        // Two runs is exactly what the fix's own regression test used to do, which is why this one goes
        // to four: three narrowed runs after the wide one, so a merge that widens the scope only once
        // is caught as well.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true, scope: new ScopeOptions(IncludeTests: false, IncludeInternal: true));

        for (var run = 1; run <= 3; run++)
        {
            var result = WriteRun(tmp, [A], complete: true, scope: ScopeOptions.Default);

            Assert.True(
                File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")),
                $"narrowed run {run} deleted a concept only the wider run could account for.");
            Assert.Empty(result.Pruned);
            Assert.Contains(result.Notes, n => n.Contains("--include-internal", StringComparison.Ordinal));

            // The record on disk, not just the behaviour: the manifest has to keep claiming B, and to
            // keep saying the set it claims needs --include-internal to account for.
            var manifest = GenerationManifest.TryRead(tmp.Path);
            Assert.NotNull(manifest);
            Assert.Contains(B, manifest.ConceptIds);
            Assert.Equal(new ScopeOptions(IncludeTests: false, IncludeInternal: true), manifest.Scope);
        }

        // The other direction, or a merge that simply pinned the widest scope for ever would pass just
        // as happily: once a run accounts for every id the manifest claims, nothing is carried, the
        // set is that run's own output, and the scope must come back down to what that run covered
        // rather than claiming a coverage nobody asked for.
        var accounted = WriteRun(tmp, [A, B], complete: true, scope: ScopeOptions.Default);

        Assert.Empty(accounted.Pruned);
        Assert.Equal(ScopeOptions.Default, GenerationManifest.TryRead(tmp.Path)?.Scope);
    }

    [Fact]
    public void A_run_that_widens_its_scope_prunes_exactly_as_it_always_did()
    {
        // Only NARROWING is a reason to refuse. Without this, "record the scope" could be implemented
        // as "never prune when the two differ", which would make pruning die the first time anyone
        // passed a flag -- and the test above would not notice.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true, scope: ScopeOptions.Default);

        var result = WriteRun(tmp, [A], complete: true, scope: new ScopeOptions(IncludeTests: true, IncludeInternal: true));

        Assert.False(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/a.md")));
        Assert.Equal(new[] { B }, result.Pruned.Select(id => id.ToString()));
    }

    [Fact]
    public void A_manifest_that_records_no_scope_authorizes_no_deletion()
    {
        // A hand-assembled or hand-edited manifest inside a schema this build does read. "No scope
        // recorded" is not "the narrowest scope"; it is a question the file does not answer, and the
        // only safe reading of an unanswered question here is to keep the concept.
        using var tmp = new TempDir();
        WriteRun(tmp, [A, B], complete: true);

        // `Scope: null` is spelled out rather than omitted: the record has no default for that
        // parameter, so "no scope recorded" is a thing a caller says on purpose and a reader can see.
        new GenerationManifest(
                Prefix,
                [new ManifestConcept(A, [SharedSource]), new ManifestConcept(B, [SharedSource])],
                [SharedSource],
                Scope: null)
            .WriteTo(tmp.Path);

        var result = WriteRun(tmp, [A], complete: true);

        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
        Assert.Empty(result.Pruned);

        var note = Assert.Single(result.Notes, n => n.Contains("records no extraction scope", StringComparison.Ordinal));

        // The refusal is STICKY, and the note has to name the way out. MergedScope writes a null
        // scope forward for as long as anything is carried, so this bundle refuses on this branch on
        // every subsequent run -- asserted below rather than argued -- and an operator reading only
        // "records no extraction scope" is told a fact about the file with no move attached.
        Assert.Contains("--reset", note, StringComparison.Ordinal);

        var second = WriteRun(tmp, [A], complete: true);
        Assert.Contains(second.Notes, n => n.Contains("records no extraction scope", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(tmp.Path, "code/csharp/n/t/b.md")));
    }

    [Fact]
    public void The_manifest_records_the_scope_the_run_covered()
    {
        using var tmp = new TempDir();
        WriteRun(tmp, [A], complete: true, scope: new ScopeOptions(IncludeTests: true, IncludeInternal: false));

        Assert.Equal(new ScopeOptions(IncludeTests: true, IncludeInternal: false), GenerationManifest.TryRead(tmp.Path)?.Scope);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A directory reparse point at <paramref name="link"/> pointing at <paramref name="target"/>.
    ///
    /// <para>A symbolic link where the platform allows one; a junction on Windows, where creating a
    /// symbolic link needs SeCreateSymbolicLinkPrivilege (Developer Mode or an elevated shell) that an
    /// ordinary test run does not have. A junction is the same kind of object for every purpose these
    /// tests have: <c>Path.GetFullPath</c> does not resolve it, <c>File.Exists</c> and
    /// <c>File.Delete</c> follow it, and <c>FileSystemInfo.LinkTarget</c> reports it.</para>
    ///
    /// <para>If neither can be created this fails loudly rather than letting the test pass without a
    /// link -- which is exactly the shape of assertion this whole exercise exists to root out.</para>
    /// </summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(link)!);

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Fail($"could not create a symbolic link at '{link}': {ex.Message}");
            }

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            process?.WaitForExit();
        }

        Assert.True(
            new DirectoryInfo(link).LinkTarget is not null,
            $"no symbolic link or junction could be created at '{link}', so this test would pass without exercising anything. "
                + "On Windows a junction needs no privilege; if even that failed, the temporary directory is on a filesystem "
                + "that has no reparse points and this test cannot run there.");
    }

    private static WriteResult WriteRun(
        TempDir tmp,
        IReadOnlyList<string> ids,
        bool complete,
        IReadOnlyList<(string Path, FileStatus Status)>? attempted = null,
        IReadOnlyDictionary<string, string[]>? sources = null,
        string? ownedPrefix = null,
        string? repoPath = null,
        ScopeOptions? scope = null)
    {
        var concepts = ids
            .Select(id => Concept(id, sources is not null && sources.TryGetValue(id, out var own) ? own : [SharedSource]))
            .ToList();

        var status = new RunStatus(complete, attempted ?? [(SharedSource, FileStatus.Extracted)]);
        var manifest = GenerationManifest.ForRun(ownedPrefix ?? Prefix, concepts, status, scope ?? ScopeOptions.Default);

        return new BundleWriter().Write(tmp.Path, concepts, WritePolicy.Update, repoPath ?? tmp.RepoPath, manifest, status);
    }

    /// <summary>
    /// A run that writes one concept and then throws while the caller is still enumerating -- the
    /// shape of a generation that dies part-way, and the only way to reach the commit boundary from
    /// outside. The concept it does yield carries a DIFFERENT body from the one already in the bundle,
    /// so that a writer without staging would visibly overwrite it and the test would fail.
    /// </summary>
    private static void WriteRunThatThrows(TempDir tmp, WritePolicy policy = WritePolicy.Update)
    {
        var concepts = new ThrowingList([Concept(A, [SharedSource], body: "a body this run must never commit")]);
        var status = RunStatus.Complete;

        new BundleWriter().Write(
            tmp.Path,
            concepts,
            policy,
            tmp.RepoPath,
            GenerationManifest.ForRun(Prefix, [], status, ScopeOptions.Default),
            status);
    }

    private static GeneratedConcept Concept(string id, IReadOnlyList<string> sources, string body = "generated") =>
        new(ConceptId.Parse(id),
            OkfDocumentBuilder.ForType("C# Member").Title(id).Description("d").Body($"# {id}\n\n{body}\n").Build())
        {
            SourceFiles = sources,
        };

    private static Dictionary<string, string[]> Owners(params (string Id, string[] Files)[] entries) =>
        entries.ToDictionary(e => e.Id, e => e.Files, StringComparer.Ordinal);

    /// <summary>
    /// Replaces the manifest in the bundle with one naming exactly <paramref name="ids"/>, each owned
    /// by the cleanly-extracted fixture source -- the way an operator, or anything else on the machine,
    /// could edit the file this producer trusts.
    ///
    /// <para>The scope is always recorded, so that a test planting a manifest exercises the check it is
    /// aiming at rather than tripping the scope gate on the way in -- a scope-less manifest prunes
    /// nothing at all, which is what
    /// <see cref="A_manifest_that_records_no_scope_authorizes_no_deletion"/> pins deliberately.</para>
    /// </summary>
    private static void PlantManifestIds(TempDir tmp, string ownedPrefix, IReadOnlyList<string> ids, ScopeOptions? scope = null) =>
        new GenerationManifest(
                ownedPrefix,
                [.. ids.Select(id => new ManifestConcept(id, [SharedSource]))],
                [SharedSource],
                scope ?? ScopeOptions.Default)
            .WriteTo(tmp.Path);

    /// <summary>One concept from a real <see cref="ConceptGenerator"/> run over the fixture graph below.</summary>
    private static GeneratedConcept Generated(string id) =>
        new ConceptGenerator()
            .Generate(new RepositorySnapshot("/repo", "my-repo", [], []), OwnershipGraph(), new GenerateOptions { Profiles = [CSharp] })
            .Single(c => c.Id.ToString() == id);

    private static readonly LanguageProfile CSharp = new(
        Language: "csharp",
        GrammarName: "c_sharp",
        DeclarationQuery: string.Empty,
        CallQuery: string.Empty,
        DocCommentPrefix: "///",
        FileExtensions: [".cs"]);

    /// <summary>
    /// Two types in <c>N</c>, declared in two different files, so the container above them has more
    /// than one owner and a union is distinguishable from "whichever file came first".
    /// </summary>
    private static CodeGraphModel OwnershipGraph() => new(
        [
            new SymbolFact(SymbolKind.Type, "csharp", "N", "T", "public class T", SymbolVisibility.Public, "src/T.cs", 0, 1, 1, 2, null),
            new SymbolFact(SymbolKind.Member, "csharp", "N.T", "Scan", "public void Scan()", SymbolVisibility.Public, "src/T.cs", 2, 3, 3, 4, null),
            new SymbolFact(SymbolKind.Type, "csharp", "N", "Other", "public class Other", SymbolVisibility.Public, "src/Other.cs", 0, 1, 1, 2, null),
        ],
        [],
        RunStatus.Complete);

    /// <summary>
    /// A concept sequence that yields its items and then throws, the way a generator failing part-way
    /// through would. A hostile <see cref="IReadOnlyList{T}"/> rather than a seam on
    /// <see cref="BundleWriter"/> itself: the property under test is that the bundle survives a throw
    /// from anywhere inside the write, and a production type should not grow a hook to prove it.
    /// </summary>
    private sealed class ThrowingList(IReadOnlyList<GeneratedConcept> items) : IReadOnlyList<GeneratedConcept>
    {
        public int Count => items.Count;

        public GeneratedConcept this[int index] => items[index];

        public IEnumerator<GeneratedConcept> GetEnumerator()
        {
            foreach (var item in items)
            {
                yield return item;
            }

            throw new InvalidOperationException("generation failed part-way through");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "okfproducer-prune-" + Guid.NewGuid());
            Path = System.IO.Path.Combine(Root, "bundle");
            RepoPath = System.IO.Path.Combine(Root, "repo");
            Directory.CreateDirectory(Path);
            Directory.CreateDirectory(RepoPath);
            WriteSource(SharedSource);
        }

        /// <summary>The directory holding both the bundle and the repository, so a staging directory beside the bundle is visible to a test.</summary>
        public string Root { get; }

        /// <summary>The bundle root.</summary>
        public string Path { get; }

        /// <summary>The repository root the bundle claims to describe.</summary>
        public string RepoPath { get; }

        /// <summary>Creates an empty file at a repository-relative path, so the writer can find it still there.</summary>
        public void WriteSource(string relativePath)
        {
            var full = System.IO.Path.Combine(RepoPath, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "// fixture\n");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
