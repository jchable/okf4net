// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Tests.Generation;

/// <summary>The source edit a <see cref="BlastRadiusTests"/> row applies between two generations.</summary>
public enum Mutation
{
    /// <summary>A second <c>Scan</c> signature on the existing type -- §3.2's merged overloads.</summary>
    AddOverload,

    /// <summary>A new public type in a new file under the existing namespace -- §5.2's one-level containment.</summary>
    AddPublicType,

    /// <summary>A private member on the existing type -- §5.4's visibility filter.</summary>
    AddPrivateMember,

    /// <summary>Removes the last method of the existing type -- §6.3's pruning.</summary>
    DeleteMethod,

    /// <summary>Commits a file the producer never reads -- §6.1's HEAD-commit stamp.</summary>
    CommitUnrelatedFile,
}

/// <summary>
/// §8.3's blast-radius test: generate, mutate the source, regenerate, and assert the <b>exact</b> set
/// of concepts that moved.
///
/// <para>This is the only thing in the suite that tests the plan's central promise. Merged overloads
/// (§3.2), one-level containment (§5.2) and the deliberate absence of <c>## Called by</c> (§4.5) all
/// exist for one reason -- to bound churn -- and no assertion on a single id can show whether they
/// do. Nor can a count: what matters is <i>which</i> files moved.</para>
///
/// <para><b>Two things the comparison deliberately does not count.</b> The <c>index.md</c> of a
/// directory whose children changed is rewritten by <c>IndexGenerator</c> mechanically, and the
/// generation manifest records the id set, so both follow from any structural change and neither is
/// churn a design decision could have avoided. The table is about concepts.</para>
///
/// <para><b>Every mutation but the last is left uncommitted on purpose.</b> The fixture repository is
/// a real git checkout here (unlike <see cref="CheckTests"/>, which needs the outside-git case), so
/// <c>overview</c>'s <c>generated.at</c> and <c>revision</c> track HEAD. Editing the working tree
/// without committing leaves HEAD alone, which is what isolates the code churn from the stamp --
/// and it is also why the last row, which does commit, moves <c>overview</c> and nothing else.</para>
/// </summary>
public class BlastRadiusTests
{
    [Theory]

    // §3.2: overloads collapse onto one concept, so a second signature rewrites that concept and
    // creates no `scan-2` to renumber its neighbours. Its TYPE does not move -- see the note below.
    [InlineData(Mutation.AddOverload, "code/csharp/n/scanner/scan")]

    // §5.2: the namespace names the level directly below it and nothing further, so a new type
    // rewrites its container and adds itself -- not `overview`, which would name all ~480 concepts
    // under a design that linked the whole tree.
    [InlineData(Mutation.AddPublicType, "code/csharp/n", "code/csharp/n/added")]

    // §5.4: a private member is not in scope, so it produces no concept -- and, being a member, it
    // adds no child to anything. Nothing whatsoever changes, which is the strongest row in the table
    // and the one that caught the defect described below.
    [InlineData(Mutation.AddPrivateMember)]

    // §6.3: `Update` used to preserve everything it did not generate, so a deleted method kept its
    // concept for ever, pointing at code that no longer exists. It is pruned, and its type loses a
    // child.
    [InlineData(Mutation.DeleteMethod, "-code/csharp/n/scanner/gone", "code/csharp/n/scanner")]
    public void A_source_mutation_changes_exactly_the_expected_concepts(Mutation mutation, params string[] expected)
    {
        // WHAT THIS TABLE CAUGHT, on its first run. A type's concept records the type's own line span
        // -- in `resource` and beside its signature -- and a type declaration's span runs to its
        // CLOSING brace. So every edit inside a type moved that type's concept, whatever it declared
        // or failed to declare: rows 1 and 3 came back with `code/csharp/n/scanner` attached, and row 3
        // is supposed to come back empty.
        //
        // That was churn caused by the edit's position rather than by anything the type declares, and
        // it falsified one of the three promises the id scheme exists to keep. The fix is in emission,
        // not here: `ConceptGenerator.RenderedEndLine` caps a type's rendered span at its header
        // (SymbolFact.HeaderEndLine), so the body may change freely underneath it. An edit ABOVE a type
        // still moves it, which is right -- the declaration genuinely moved -- and row 4 still lists
        // `code/csharp/n/scanner`, because deleting a method really does remove one of its children.
        //
        // Kept as a note rather than deleted with the defect: this table is the only thing in the suite
        // that could have found it, and the next person to widen what a concept records needs to know
        // that a span is not a free field to add.
        var changed = ConceptsChangedBy(mutation);

        Assert.Equal(
            expected.OrderBy(x => x, StringComparer.Ordinal),
            changed.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void An_added_overload_creates_no_new_id_and_renumbers_nothing()
    {
        // §3.2's promise named in the vocabulary of the design it rejects. One concept per SIGNATURE
        // -- the alternative -- would have produced `scan-2` here, and a numeric suffix allocated in
        // declaration order renumbers its neighbours whenever an overload appears or disappears. The
        // theory row above already implies the absence (a new file would show in its change set); this
        // says which id must never appear, so the counterfactual is greppable rather than folded into
        // an expected-set literal.
        //
        // Deliberately not paired with a private-member twin: the row above expects the EMPTY set for
        // that mutation, which says everything an id-set comparison could and more.
        var (before, after) = ConceptIdsAround(Mutation.AddOverload);

        Assert.Equal(before, after);
        Assert.Contains("code/csharp/n/scanner/scan", after);
        Assert.DoesNotContain("code/csharp/n/scanner/scan-2", after);
    }

    [Fact]
    public void A_commit_that_does_not_touch_code_changes_only_overview()
    {
        // This row LOOKS like it says "nothing" and does not. `overview` carries `revision` and
        // `generated.at`, both read off the HEAD commit, so any commit rewrites it -- one file out of
        // the bundle. That bound is the property being asserted; "nothing changes" would be false, and
        // an assertion that can never pass is worse than none. With a wall-clock stamp instead of the
        // HEAD one, this same assertion would come back with every concept in the bundle.
        Assert.Equal(["overview"], ConceptsChangedBy(Mutation.CommitUnrelatedFile));
    }

    // -- fixture ----------------------------------------------------------------------------------

    /// <summary>The one file every mutation but <see cref="Mutation.AddPublicType"/> edits.</summary>
    private const string ScannerSource = "src/Scanner.cs";

    /// <summary>
    /// The exact text of the method <see cref="Mutation.DeleteMethod"/> removes, doc comment included.
    /// It is the <b>last</b> declaration in its file on purpose: deleting text above another
    /// declaration would move that declaration's lines and rewrite its concept too, and this test would
    /// then be measuring the edit's position rather than the deletion.
    /// </summary>
    private const string GoneMethod =
        "\n    /// <summary>Reads a legacy manifest. The symbol a mutation deletes; it is last in the file on purpose.</summary>\n"
        + "    public void Gone()\n    {\n    }\n";

    /// <summary>
    /// Generates the bundle, applies <paramref name="mutation"/>, regenerates over the same bundle
    /// (the real <c>--update</c> path, pruning included) and returns the concepts that moved: an id per
    /// changed or added concept, and <c>-id</c> for one the run deleted.
    /// </summary>
    private static IReadOnlyList<string> ConceptsChangedBy(Mutation mutation)
    {
        ProducerFixture.RequireGit();

        using var workspace = ProducerFixture.CopyRepoIntoGit();
        var repo = Path.Combine(workspace.Path, ProducerFixture.RepoDirectoryName);
        var bundle = Path.Combine(workspace.Path, "bundle");

        ProducerFixture.Run(repo, bundle);
        var before = ProducerFixture.SnapshotFiles(bundle);

        Apply(mutation, repo);

        var outcome = ProducerFixture.Run(repo, bundle);

        // A floor. Without it a run that generated nothing at all -- an extractor that stopped
        // finding symbols, a traversal that failed -- would report an empty change set and every row
        // of the table above would pass vacuously.
        Assert.True(outcome.Generated > 0, "the regeneration produced no concepts at all; nothing below would discriminate a producer gone silent.");
        Assert.True(outcome.Status.TraversalComplete, "the regeneration did not visit every eligible file, so it was not allowed to prune and the deletion rows cannot mean anything.");
        Assert.Empty(outcome.Write.Failures);

        var after = ProducerFixture.SnapshotFiles(bundle);

        return Diff(before, after);
    }

    /// <summary>The set of concept ids present before and after <paramref name="mutation"/>, for the tests that assert on the id set rather than on the bytes.</summary>
    private static (IReadOnlyList<string> Before, IReadOnlyList<string> After) ConceptIdsAround(Mutation mutation)
    {
        ProducerFixture.RequireGit();

        using var workspace = ProducerFixture.CopyRepoIntoGit();
        var repo = Path.Combine(workspace.Path, ProducerFixture.RepoDirectoryName);
        var bundle = Path.Combine(workspace.Path, "bundle");

        ProducerFixture.Run(repo, bundle);
        var before = ConceptIds(bundle);

        Apply(mutation, repo);
        ProducerFixture.Run(repo, bundle);

        Assert.NotEmpty(before);
        return (before, ConceptIds(bundle));
    }

    private static void Apply(Mutation mutation, string repo)
    {
        switch (mutation)
        {
            case Mutation.AddOverload:
                ProducerFixture.EditSource(repo, ScannerSource, ProducerFixture.InsertAtEndOfLastType(
                    "\n    /// <summary>Scans one root. Nothing calls this either.</summary>\n"
                    + "    public void Scan(string root)\n    {\n    }\n"));
                break;

            case Mutation.AddPublicType:
                // A NEW FILE, not an edit to an existing one: a type appended to `Scanner.cs` would
                // also move `Scanner`'s own closing line, and the row would then measure two things.
                File.WriteAllText(
                    Path.Combine(repo, "src", "Added.cs"),
                    "namespace N;\n\n/// <summary>A type added by a test.</summary>\npublic class Added\n{\n}\n");
                break;

            case Mutation.AddPrivateMember:
                ProducerFixture.EditSource(repo, ScannerSource, ProducerFixture.InsertAtEndOfLastType(
                    "\n    private void Hidden()\n    {\n    }\n"));
                break;

            case Mutation.DeleteMethod:
                ProducerFixture.EditSource(repo, ScannerSource, source =>
                {
                    Assert.Contains(GoneMethod, source, StringComparison.Ordinal);
                    return source.Replace(GoneMethod, string.Empty, StringComparison.Ordinal);
                });

                break;

            case Mutation.CommitUnrelatedFile:
                // A `.txt` at the repository root: not a manifest, not a README, not a source file the
                // C# profile matches, so nothing the producer reads changes -- only the commit does.
                File.WriteAllText(Path.Combine(repo, "NOTES.txt"), "Nothing here is code.\n");
                ProducerFixture.Git(repo, "add", "-A");
                ProducerFixture.Git(repo, "commit", "-q", "-m", "docs: a commit that touches no code");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "unhandled mutation.");
        }
    }

    /// <summary>
    /// The concepts that differ between two snapshots of the same bundle: an id for one added or
    /// rewritten, <c>-id</c> for one deleted. <c>index.md</c> and the generation manifest are dropped
    /// -- see this class's own summary for why.
    /// </summary>
    private static IReadOnlyList<string> Diff(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        var changed = new List<string>();

        foreach (var relative in before.Keys.Union(after.Keys, StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!IsConceptFile(relative))
            {
                continue;
            }

            var id = relative[..^".md".Length];
            var had = before.TryGetValue(relative, out var left);
            var has = after.TryGetValue(relative, out var right);

            if (had && !has)
            {
                changed.Add("-" + id);
            }
            else if (!had && has)
            {
                changed.Add(id);
            }
            else if (!left!.AsSpan().SequenceEqual(right!))
            {
                changed.Add(id);
            }
        }

        return changed;
    }

    private static IReadOnlyList<string> ConceptIds(string bundle) =>
        [.. ProducerFixture.SnapshotFiles(bundle).Keys
            .Where(IsConceptFile)
            .Select(path => path[..^".md".Length])
            .OrderBy(id => id, StringComparer.Ordinal)];

    private static bool IsConceptFile(string relativePath) =>
        relativePath.EndsWith(".md", StringComparison.Ordinal)
        // The whole file name, not a suffix: a concept legitimately named `build-index` would end with
        // "index.md" too, and dropping it would hide real churn.
        && !string.Equals(Path.GetFileName(relativePath), "index.md", StringComparison.Ordinal);
}
