// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Threading;
using OkfProducer.CodeGraph.TreeSitter;
using OkfProducer.CodeGraph.TreeSitter.Profiles;
using OkfProducer.Core.CodeGraph;
using OkfProducer.Core.Scanning;

namespace OkfProducer.Tests.CodeGraph;

/// <summary>
/// §2.3: the extraction pipeline must survive source it did not write. Covers the hostile-input
/// policy itself (<see cref="TreeSitterExtractor"/>, exercised directly) and the aggregation rule it
/// exists to feed (<see cref="RunStatus.IsComplete"/>, exercised through <see cref="CodeGraphBuilder"/>
/// with a stubbed extractor, so the aggregation is tested independently of any real detection logic).
/// </summary>
public class HostileInputTests : IDisposable
{
    private readonly TreeSitterExtractor _extractor = new();
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        _extractor.Dispose();

        foreach (var directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a locked file on the way out should not fail the test run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class StubExtractor(IReadOnlyDictionary<string, FileStatus> statusByPath) : ILanguageExtractor
    {
        public ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile, ExtractionLimits limits) =>
            new([], [], statusByPath[relativePath]);
    }

    /// <summary>
    /// Builds a graph from a repository with one empty file per <paramref name="files"/> entry, using
    /// a stub extractor that reports exactly the given <see cref="FileStatus"/> for each file --
    /// isolating <see cref="CodeGraphBuilder"/>'s <see cref="RunStatus"/> aggregation from any real
    /// hostile-input detection, which is tested separately below through the real extractor.
    /// </summary>
    private static OkfProducer.Core.CodeGraph.CodeGraph BuildWith(params (string Path, FileStatus Status)[] files)
    {
        var repoPath = Directory.CreateTempSubdirectory("okfproducer-hostile-").FullName;
        foreach (var (path, _) in files)
        {
            var fullPath = Path.Combine(repoPath, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, string.Empty);
        }

        var snapshot = new RepositorySnapshot(repoPath, "test-repo", [], []);
        var statusByPath = files.ToDictionary(f => f.Path, f => f.Status);
        var builder = new CodeGraphBuilder(new StubExtractor(statusByPath), [CSharpProfile.Instance], []);

        return builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default);
    }

    [Theory]
    [InlineData(FileStatus.SkippedTooLarge)]
    [InlineData(FileStatus.SkippedEncoding)]
    [InlineData(FileStatus.SkippedUnreadable)]
    public void Any_skipped_file_makes_the_whole_run_incomplete(FileStatus status)
    {
        var graph = BuildWith(("A.cs", status));

        Assert.False(graph.Status.IsComplete);
        Assert.Contains(graph.Status.Skipped, s => s.Path == "A.cs" && s.Status == status);
    }

    [Fact]
    public void A_run_where_every_file_extracted_is_complete()
    {
        // The all-clean shape: both facts RunStatus now carries separately agree.
        var status = BuildWith(("A.cs", FileStatus.Extracted)).Status;

        Assert.True(status.TraversalComplete);
        Assert.True(status.IsComplete);
    }

    [Fact]
    public void A_complete_traversal_with_one_partially_extracted_file_is_traversal_complete_but_not_is_complete()
    {
        // §6.3's finer rule, restated by the coordinator after the ERROR-node measurement: the
        // traversal itself finished -- every eligible file was visited and has a recorded outcome --
        // so pruning IS safe for whichever files extracted cleanly, even though this run as a whole
        // is not fully clean. TraversalComplete and IsComplete must disagree here, on purpose --
        // that disagreement is the entire point of splitting them.
        var status = BuildWith(("A.cs", FileStatus.PartiallyExtracted)).Status;

        Assert.True(status.TraversalComplete);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public void Skipped_records_every_attempted_files_outcome_including_a_clean_extraction()
    {
        // So a consumer (Task 11) can ask "which files extracted cleanly?" and get an exact answer
        // directly from RunStatus.Skipped, without also needing the full universe of eligible files
        // to compute the complement of a failures-only list: a cleanly extracted file gets a real
        // entry too, not just omission.
        var skipped = BuildWith(("A.cs", FileStatus.Extracted), ("B.cs", FileStatus.SkippedTooLarge)).Status.Skipped;

        Assert.Contains(skipped, s => s.Path == "A.cs" && s.Status == FileStatus.Extracted);
        Assert.Contains(skipped, s => s.Path == "B.cs" && s.Status == FileStatus.SkippedTooLarge);
    }

    [Fact]
    public void A_nonexistent_repository_root_reports_an_incomplete_run_not_an_empty_complete_one()
    {
        // C-1: RepositoryScanner.Scan performs no existence check on the path it is handed, so a
        // typo'd or transiently-unmounted RepositorySnapshot.RepoPath is reachable from the public
        // API. Before this fix, EnumerateFiles silently returned an empty sequence for a missing
        // root, the per-file loop never ran, Skipped stayed empty, and Build returned
        // RunStatus.Complete with zero symbols -- indistinguishable from a real, legitimately empty
        // repository. Under Task 11's gate that prunes every concept absent from a complete run, that
        // would have deleted every concept in the user's bundle. A missing root must degrade the run
        // to incomplete instead, consistent with every other reason this task's walk can fail
        // (timeout, cancellation, a circular reparse point) -- never throw, matching this codebase's
        // established "parse failures are errors as data" philosophy (see CLAUDE.md's Bundle
        // description) rather than adding the one API surface in this design that can throw.
        var nonexistentRoot = Path.Combine(Path.GetTempPath(), "okfproducer-does-not-exist-" + Guid.NewGuid());
        var snapshot = new RepositorySnapshot(nonexistentRoot, "test-repo", [], []);
        var builder = new CodeGraphBuilder(_extractor, [CSharpProfile.Instance], []);

        var graph = builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default);

        Assert.False(graph.Status.TraversalComplete);
        Assert.False(graph.Status.IsComplete);
        Assert.Empty(graph.Symbols);
    }

    [Fact]
    public void A_file_over_the_size_cap_is_skipped_whole_never_truncated()
    {
        // Truncating would yield spans that point at the wrong code -- worse than not extracting the
        // file at all.
        using var tmp = new TempDir();
        var path = tmp.Write("big.cs", new string('x', 3 * 1024 * 1024));

        var result = Extract(path, ExtractionLimits.Default with { MaxFileBytes = 2 * 1024 * 1024 });

        Assert.Equal(FileStatus.SkippedTooLarge, result.Status);
        Assert.Empty(result.Symbols);
    }

    [Fact]
    public void The_size_check_never_loads_an_oversized_file_into_memory()
    {
        // The file's declared length alone must decide SkippedTooLarge -- the extractor is never
        // given a chance to read its bytes at all. A file that reports itself too large but whose
        // actual bytes (if ever read) would decode fine still gets rejected on length.
        using var tmp = new TempDir();
        var path = tmp.Write("big.cs", "namespace N;\npublic class T {}");

        var result = Extract(path, ExtractionLimits.Default with { MaxFileBytes = 1 });

        Assert.Equal(FileStatus.SkippedTooLarge, result.Status);
    }

    [Fact]
    public void Invalid_utf8_is_skipped_not_replaced_with_substitution_characters()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "bad.cs");
        File.WriteAllBytes(path, [0x6E, 0x73, 0xFF, 0xFE, 0x00, 0x41]);

        Assert.Equal(FileStatus.SkippedEncoding, Extract(path, ExtractionLimits.Default).Status);
    }

    [Fact]
    public void A_utf8_bom_is_accepted_and_stripped_before_parsing()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "bommed.cs");
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var content = System.Text.Encoding.UTF8.GetBytes("namespace N;\npublic class T {}");
        File.WriteAllBytes(path, [.. bom, .. content]);

        var result = Extract(path, ExtractionLimits.Default);

        Assert.Equal(FileStatus.Extracted, result.Status);
        var type = Assert.Single(result.Symbols);
        // "namespace N;\n" is 13 ASCII bytes -- if the BOM had leaked into the decoded source as a
        // stray leading character (U+FEFF), every offset after it would be off by one.
        Assert.Equal(13, type.StartOffset);
    }

    [Theory]
    [InlineData(false)] // FF FE -- little-endian
    [InlineData(true)]  // FE FF -- big-endian
    public void Utf16_with_a_bom_is_accepted(bool bigEndian)
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "utf16.cs");
        var encoding = new System.Text.UnicodeEncoding(bigEndian, byteOrderMark: true);
        // UnicodeEncoding.GetBytes never includes the preamble (a common gotcha) -- it has to be
        // prepended explicitly to actually put a BOM on disk.
        File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. encoding.GetBytes("namespace N;\npublic class T {}")]);

        var result = Extract(path, ExtractionLimits.Default);

        Assert.Equal(FileStatus.Extracted, result.Status);
        Assert.Equal("T", Assert.Single(result.Symbols).Name);
    }

    [Fact]
    public void Invalid_utf16_with_a_bom_is_skipped_not_replaced_with_substitution_characters()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "bad-utf16.cs");
        // FF FE (UTF-16 LE BOM), then 00 D8 -- an unpaired high surrogate with no low surrogate to
        // follow it, invalid regardless of what comes after.
        File.WriteAllBytes(path, [0xFF, 0xFE, 0x00, 0xD8, 0x41, 0x00]);

        Assert.Equal(FileStatus.SkippedEncoding, Extract(path, ExtractionLimits.Default).Status);
    }

    [Fact]
    public void Utf32_le_bom_is_not_mistaken_for_utf16_le()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "utf32.cs");
        // FF FE 00 00 is the UTF-32 LE BOM -- its first two bytes are byte-for-byte identical to the
        // UTF-16 LE BOM (FF FE) alone, so a two-byte-only check would misclassify this and decode
        // NUL-interleaved garbage instead of correctly rejecting it. §2.3 accepts UTF-8 and
        // UTF-16-with-BOM only, never UTF-32.
        File.WriteAllBytes(path, [0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00]);

        Assert.Equal(FileStatus.SkippedEncoding, Extract(path, ExtractionLimits.Default).Status);
    }

    [Fact]
    public void A_tree_with_error_nodes_keeps_what_parsed_and_reports_partial()
    {
        var result = ExtractSource("namespace N;\npublic class T { public void M() { @@@ } public void N2() { } }");

        Assert.Equal(FileStatus.PartiallyExtracted, result.Status);
        Assert.Contains(result.Symbols, s => s.Name == "N2");
    }

    /// <summary>
    /// The bottom of every "damage ABOVE the namespace line" case below: an intact
    /// <c>namespace N;</c> with one type and one method under it. Whatever the shape prefixed to it
    /// does to the parse, the truth about these two declarations never changes -- <c>T</c> is
    /// <c>N.T</c> and <c>M</c> is <c>N.T.M</c> -- so any result containing <c>[]T</c> is the extractor
    /// asserting the global namespace about code that names a different one.
    /// </summary>
    private const string LostNamespaceTail = "namespace N;\n\npublic class T\n{\n    public void M() { }\n}\n";

    [Fact]
    public void A_file_scoped_namespace_merely_reparented_by_recovery_is_recovered_rather_than_refused()
    {
        // MEASURED against the vendored grammar: `#if DEBUG` with no `#endif` above the namespace
        // line leaves the file_scoped_namespace_declaration INTACT but moves it out of the root's
        // own children and under a preproc_if, together with the declarations it covers. The
        // root-children fast path finds nothing, but a search of the whole tree finds the
        // declaration and reads `N` off it, so the correct container is emitted.
        //
        // This case is why the deep search exists. An earlier revision of ReadNamespaceContext
        // asserted in its own doc comment that a lost declaration is absent from the tree entirely
        // and that "searching deeper would find nothing" -- true of the one shape that had been
        // probed, false as a general rule, and it was being used as the rationale for refusing
        // instead of recovering. Recovering is strictly better than both the wrong "" this shape
        // produced before and the refusal that would otherwise be the alternative.
        var result = ExtractSource("#if DEBUG\n\n" + LostNamespaceTail);

        Assert.Equal(FileStatus.PartiallyExtracted, result.Status);
        Assert.Contains(result.Symbols, s => s.Name == "T" && s.Container == "N");
        Assert.Contains(result.Symbols, s => s.Name == "M" && s.Container == "N.T");
    }

    [Theory]
    // Damage ABOVE a `namespace N;` line, where the region `using`s, assembly attributes and
    // preprocessor directives live -- so ordinary mid-edit damage lands here at least as often as it
    // lands below. Each of these MEASURED to destroy the file_scoped_namespace_declaration outright
    // (the deep search above finds nothing to recover), and each leaves evidence this walk can see:
    //
    //   `using System` / `[assembly: X(` / conflict markers around a `using` block
    //                        -- the keyword is re-lexed as an `identifier` (in the first and last,
    //                           as a variable_declarator's name -- `using System \n namespace` reads
    //                           as a using-statement declaring a variable called `namespace`).
    //   unterminated `/*` comment / bare conflict markers
    //                        -- re-lexed as an `implicit_parameter`.
    //   unclosed `public class Leftover {`
    //                        -- kept AS the `namespace` keyword, orphaned under an ERROR.
    //
    // The merge conflict wrapping a `using` block is the one that matters most in practice: it is
    // the single most common C# merge conflict, and before this round every type in such a file came
    // out claiming the global namespace, with two conflicted files colliding on that same "".
    [InlineData("using System\n\n" + LostNamespaceTail)]
    [InlineData("/* TODO\n\n" + LostNamespaceTail)]
    [InlineData("[assembly: X(\n\n" + LostNamespaceTail)]
    [InlineData("<<<<<<< HEAD\n=======\n>>>>>>> other\n\n" + LostNamespaceTail)]
    [InlineData("<<<<<<< HEAD\nusing System;\n=======\nusing System.Text;\n>>>>>>> other\n\n" + LostNamespaceTail)]
    [InlineData("public class Leftover\n{\n" + LostNamespaceTail)]
    public void A_declaration_covered_by_a_namespace_this_parse_lost_is_dropped_rather_than_mislabelled(string source)
    {
        var result = ExtractSource(source);

        Assert.Equal(FileStatus.PartiallyExtracted, result.Status);
        Assert.DoesNotContain(result.Symbols, s => s.Name is "T" or "M");
    }

    [Theory]
    // NOT a guarantee -- the opposite. These two shapes still emit the wrong "" container, and this
    // test pins that so the day someone closes the gap it is noticed rather than silently changing
    // what gets pruned.
    //
    // MEASURED: in both, the bytes spelling `namespace N` land inside an ERROR node that exposes NO
    // child covering them (`global using System` with no semicolon puts them in a childless ERROR;
    // the unterminated string literal leaves them in the untokenised tail of one). Nothing in the
    // tree names them, so a walk over NODES is structurally blind to them -- the same blindness the
    // string-literal case below is a scope marker for, except that here it hides a live defect
    // rather than a hypothetical one. Closing it means re-lexing the raw text of error regions,
    // which ReadNamespaceContext deliberately does not do; see its doc comment for where that line
    // is drawn and why.
    [InlineData("global using System\n\n" + LostNamespaceTail)]
    [InlineData("[assembly: System.Obsolete(\"oops)]\n\n" + LostNamespaceTail)]
    public void Two_lost_namespace_shapes_remain_invisible_and_still_emit_the_wrong_container(string source)
    {
        var result = ExtractSource(source);

        Assert.Contains(result.Symbols, s => s.Name == "T" && s.Container == string.Empty);
    }

    [Fact]
    public void Only_the_declarations_the_lost_namespace_covered_are_dropped_not_the_whole_file()
    {
        // A lost file-scoped namespace covers its own line to end of file and NOTHING above it, so
        // the decision is scoped to the declarations it reaches. Here a block namespace `N` is
        // closed cleanly before a leftover `public class Leftover {` swallows a `namespace Q;`:
        // `T` and `T.M` sit above the damage and their containers were never in doubt, and
        // `Leftover` merely CONTAINS it -- its own container comes from its own ancestors.
        //
        // A file-global refusal (which the first version of this guard performed) deleted all three.
        // Nothing about them was wrong, so the "a wrong identity is worse than a missing one" trade
        // never applied to them; the guard was simply reaching too far.
        var result = ExtractSource(
            "namespace N\n{\n    public class T { public void M() { } }\n}\n\npublic class Leftover\n{\nnamespace Q;\n");

        Assert.Equal(FileStatus.PartiallyExtracted, result.Status);
        Assert.Contains(result.Symbols, s => s.Name == "T" && s.Container == "N");
        Assert.Contains(result.Symbols, s => s.Name == "M" && s.Container == "N.T");
        Assert.Contains(result.Symbols, s => s.Name == "Leftover" && s.Container == string.Empty);
    }

    [Fact]
    public void A_second_namespace_lost_below_a_closed_one_keeps_the_first_ones_declarations()
    {
        // The same scoping rule on the other common shape: two block namespaces with the second
        // one's closing `}` missing. `N`'s declarations parse cleanly and completely, and the damage
        // is entirely below them.
        //
        // MEASURED before this round: a file-global refusal returned nothing at all for this file,
        // while the revision before THAT returned `[N]T, [N.T]M` plus two symbols salvaged out of the
        // wreck of `namespace U {` with containers that were wrong ("" for U, "U" for P). Keeping the
        // two correct ones and dropping the two wrong ones is better than either.
        var result = ExtractSource(
            "namespace N\n{\n    public class T { public void M() { } }\n}\n\nnamespace U\n{\n    public class P { }\n");

        Assert.Equal(FileStatus.PartiallyExtracted, result.Status);
        Assert.Contains(result.Symbols, s => s.Name == "T" && s.Container == "N");
        Assert.Contains(result.Symbols, s => s.Name == "M" && s.Container == "N.T");
        Assert.DoesNotContain(result.Symbols, s => s.Name is "U" or "P");
    }

    [Fact]
    public void A_call_site_below_a_lost_namespace_is_dropped_with_its_caller()
    {
        // CallSite.CallerContainer is computed from the same lost namespace, so a site whose caller
        // is dropped must go with it -- otherwise the wrong container survives in the edge table
        // after being suppressed in the symbol table, and Task 8's (Container, Name) join silently
        // matches the wrong caller or none at all.
        var result = ExtractSource(
            "public class Leftover\n{\nnamespace N;\n\npublic class T\n{\n    public void M() { Helper(); }\n}\n");

        Assert.DoesNotContain(result.Sites, s => s.CallerName == "M");
    }

    [Theory]
    // Malformation BELOW the namespace line: MEASURED to leave the file_scoped_namespace_declaration
    // intact at the root, so the container is still correct and no suppression must happen. This is
    // the ordinary mid-edit and merge-conflict shape -- the guard would be worthless if it ate these.
    //
    // Each case names the declaration the grammar actually recovers from it, which is not the same
    // one every time: an unclosed class loses the class_declaration node itself and keeps only the
    // method inside it, while the merge-conflict shape keeps the class. The three stray-brace shapes
    // were previously omitted on the stated grounds that such a shape "recovers no declaration at
    // all, so an assertion over its symbols would pass either way". That reasoning is the right
    // pattern but this measurement of it was wrong: RE-MEASURED, `{ { }` and `{{ }` inside a method
    // body each recover `M` with container `N`, and a stray `{` after a closed method recovers both
    // `T` and `M`. All three are assertable, so all three are asserted.
    [InlineData("namespace N;\n\npublic class T\n{\n    public void M() { }\n", "M")]
    [InlineData("namespace N;\n\n<<<<<<< HEAD\npublic class T\n{\n    public void M() { }\n}\n=======\npublic class T\n{\n}\n>>>>>>> other\n", "T")]
    [InlineData("namespace N;\n\npublic class T\n{\n    public void M() { { }\n}\n", "M")]
    [InlineData("namespace N;\n\npublic class T\n{\n    public void M() {{ }\n}\n", "M")]
    [InlineData("namespace N;\n\npublic class T\n{\n    public void M() { }\n    {\n}\n", "T")]
    public void Malformed_source_below_the_namespace_line_keeps_its_container(string source, string recoveredName)
    {
        var result = ExtractSource(source);

        Assert.Equal(FileStatus.PartiallyExtracted, result.Status);
        Assert.Contains(result.Symbols, s => s.Name == recoveredName && s.Container == "N");
    }

    [Theory]
    // The false positives that would make the guard eat ordinary code, each with the production edit
    // that was RUN by hand to confirm it turns this case red:
    //
    //   block / nested block  -- treating a `namespace_declaration` parent as orphaned and dropping
    //                            the clean-tree early-out (both go red; so do two pre-existing
    //                            TreeSitterExtractorTests container tests).
    //   `[]` collection expr  -- distrusting the file-scoped name whenever HasError is set. This is
    //                            the case that matters most: that grammar gap (pinned by its own
    //                            test below) makes HasError true for nearly every modern C# file in
    //                            this codebase, so that edit drops most of a real repository.
    //   string literal        -- NO single-line edit found that turns this one red. It is a scope
    //                            marker, not a proof, for TWO reasons, and the second was missed
    //                            when this note was first written: the word `namespace` inside a
    //                            string is a `string_literal` node and never a `namespace` keyword
    //                            node, so the node-type test cannot see it -- AND this source parses
    //                            with HasError false, so ReadNamespaceContext returns down its fast
    //                            path and the evidence walk is never reached at all. (Measured: the
    //                            HasError-distrust edit above leaves this case green, which it could
    //                            not do if the source had an error region.) It would start earning
    //                            its keep the day someone reimplements the walk over node TEXT, and
    //                            is kept for that reason alone. A case meant to constrain the walk
    //                            has to be built so the walk actually runs -- see
    //                            An_escaped_namespace_identifier_is_not_mistaken_for_a_re_lexed_keyword.
    [InlineData("namespace N\n{\n    public class T { public void M() { } }\n}\n", "N")]
    [InlineData("namespace N.Deep\n{\n    namespace Inner { public class T { } }\n}\n", "N.Deep.Inner")]
    [InlineData("namespace N;\n\npublic class T { public void M() { Use([]); } public void Use(int[] a) { } }\n", "N")]
    [InlineData("namespace N;\n\npublic class T { public string S = \"namespace X;\"; }\n", "N")]
    public void The_lost_namespace_guard_does_not_fire_on_source_whose_namespace_is_intact(string source, string expectedContainer)
    {
        var result = ExtractSource(source);

        Assert.Contains(result.Symbols, s => s.Name == "T" && s.Container == expectedContainer);
    }

    [Fact]
    public void An_escaped_namespace_identifier_is_not_mistaken_for_a_re_lexed_keyword()
    {
        // Guards the identifier arm added this round, and it had to be built with care to guard
        // anything at all. `namespace` is reserved, so the only way it can name a local is escaped,
        // and the escaped form MEASURES as node text "@namespace" -- which is exactly why an EXACT
        // text match can treat an identifier reading "namespace" as proof of a re-lexed keyword.
        //
        // The obvious way to write this case -- `var @namespace = 1;` under an intact
        // `namespace N;` -- would have been WORTHLESS, and was measured to be: that source parses
        // with HasError FALSE and its file-scoped declaration sitting at the root, so
        // ReadNamespaceContext returns down the fast path and the evidence walk is never reached.
        // No edit to the walk could turn such a test red. So the escaped identifier is placed where
        // the walk really does run (no file-scoped declaration to recover, an error region present)
        // and BEFORE a later declaration, since suppression only ever reaches what follows the
        // evidence: broadening the arm's exact match to EndsWith("namespace") makes it fire at the
        // `@namespace` token and swallow `B`. That edit was RUN by hand and turns this test red.
        var result = ExtractSource(
            "namespace N\n{\n    public class A { public void M() { var @namespace = 1; @@@ } }\n    public class B { }\n}\n");

        Assert.Contains(result.Symbols, s => s.Name == "B" && s.Container == "N");
    }

    [Fact]
    public void A_file_with_no_namespace_at_all_still_extracts_into_the_global_container()
    {
        // The other direction of the same distinction: "" here is a FACT about the source, not a
        // lookup that failed, so the guard must let it through even though the file has an ERROR
        // node of its own.
        var result = ExtractSource("public class T\n{\n    public void M() { @@@ }\n}\n");

        Assert.Contains(result.Symbols, s => s.Name == "T" && s.Container == string.Empty);
    }

    [Fact]
    public void An_empty_collection_expression_used_as_an_argument_is_a_live_grammar_gap_not_a_theoretical_one()
    {
        // Measured against the real extractor (see task-4-report.md's fix-round-2 section for the
        // full investigation): this is NOT hostile input -- it is ordinary, idiomatic C# 12 that this
        // very repository writes constantly (every ExtractionResult([], [], status) construction,
        // RunStatus.Complete's own `new(true, [])`). The vendored tree-sitter-c-sharp grammar cannot
        // parse an EMPTY collection expression `[]` in ANY expression position (constructor argument,
        // method argument, property initializer, return statement, `??` right-hand side -- all
        // measured, all HasError=true; the same shape with one element, `[1]`, parses cleanly in
        // every one of those positions). It is misparsed as an element_binding_expression (the
        // null-conditional indexer rule, `a?[i]`) with HasError set on that node, but neither
        // IsError nor IsMissing set anywhere in its subtree -- so a naive "search the tree for a
        // literal ERROR or MISSING node" check finds nothing at all, only tree.RootNode.HasError
        // catches it. Consequence: PartiallyExtracted -- and therefore RunStatus.IsComplete ==
        // false -- is the ordinary outcome for nearly any substantial file in a modern C# 12+
        // codebase written in this project's own style, not a rare edge case. Pinned here so a
        // future grammar upgrade that fixes this is noticed (this test starts failing) rather than
        // silently changing behaviour underneath Task 11's pruning gate.
        var result = ExtractSource("namespace N;\npublic class T { public void M() { new System.Collections.Generic.List<int>([]); } }");

        Assert.Equal(FileStatus.PartiallyExtracted, result.Status);
    }

    [Fact]
    public void A_reparse_point_is_never_followed()
    {
        using var tmp = new TempDir();
        var targetPath = tmp.Write("target.cs", "namespace N;\npublic class T {}");
        var linkPath = Path.Combine(tmp.Path, "link.cs");

        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating a file symlink on Windows needs SeCreateSymbolicLinkPrivilege (elevated
            // process, or Developer Mode). Not every environment this suite runs in grants that --
            // the guard itself (FileEligibility-adjacent hostile-input check in TreeSitterExtractor,
            // via FileSystemInfo.LinkTarget) is exercised wherever it does.
            return;
        }

        var result = Extract(linkPath, ExtractionLimits.Default);

        Assert.Equal(FileStatus.SkippedSymlink, result.Status);
        Assert.Empty(result.Symbols);
    }

    [Fact]
    public void A_file_reached_only_through_a_reparse_point_directory_is_never_followed()
    {
        // A plain file whose only path to the extractor runs through a symlinked/junctioned ANCESTOR
        // directory -- not a directly-symlinked file. Windows lets an unelevated process create a
        // junction (unlike a file symlink, which needs SeCreateSymbolicLinkPrivilege), so this is the
        // one reparse-point shape this suite can exercise for real rather than gracefully skip.
        using var tmp = new TempDir();
        var targetDirectory = Path.Combine(tmp.Path, "target");
        Directory.CreateDirectory(targetDirectory);
        File.WriteAllText(Path.Combine(targetDirectory, "Deep.cs"), "namespace N;\npublic class T {}");
        var linkDirectory = Path.Combine(tmp.Path, "link");

        if (!TryCreateJunction(linkDirectory, targetDirectory))
        {
            return; // Non-Windows or otherwise unable to create a reparse point here; the sibling
                    // direct-symlink test above covers the same guard where it can.
        }

        var relativePath = "link/Deep.cs";
        var absolutePath = Path.Combine(linkDirectory, "Deep.cs");

        var result = _extractor.Extract(relativePath, absolutePath, CSharpProfile.Instance, ExtractionLimits.Default);

        Assert.Equal(FileStatus.SkippedSymlink, result.Status);
        Assert.Empty(result.Symbols);
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            process!.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(linkPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public void A_file_deeper_than_the_configured_max_depth_is_skipped_and_counted()
    {
        var repoPath = Directory.CreateTempSubdirectory("okfproducer-hostile-depth-").FullName;
        _tempDirectories.Add(repoPath);
        var deepRelativePath = "a/b/c/Deep.cs";
        var fullPath = Path.Combine(repoPath, deepRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, "namespace N;\npublic class T {}");

        var snapshot = new RepositorySnapshot(repoPath, "test-repo", [], []);
        var builder = new CodeGraphBuilder(_extractor, [CSharpProfile.Instance], []);

        var graph = builder.Build(snapshot, ExtractionLimits.Default with { MaxDepth = 2 }, ScopeOptions.Default);

        // A per-file skip, not a walk failure: the file WAS visited (it has a recorded outcome), it
        // just didn't extract cleanly -- so TraversalComplete stays true even though IsComplete does
        // not, exactly the distinction this fix round exists to make.
        Assert.True(graph.Status.TraversalComplete);
        Assert.False(graph.Status.IsComplete);
        Assert.Contains(graph.Status.Skipped, s => s.Path == deepRelativePath && s.Status == FileStatus.SkippedDepth);
        Assert.Empty(graph.Symbols);
    }

    [Fact]
    public void A_circular_reparse_point_degrades_the_run_to_incomplete_instead_of_crashing()
    {
        // Measured (see task-4-report.md's fix section): Directory.EnumerateFiles does not detect a
        // junction pointing back at one of its own ancestors -- it keeps recursing into it, extending
        // the accumulated path a level deeper on every re-entry, and throws PathTooLongException
        // within a fraction of a second, nowhere near ExtractionLimits.Timeout. Left uncaught that
        // would crash Build entirely instead of returning even a partial CodeGraph.
        var repoPath = Directory.CreateTempSubdirectory("okfproducer-hostile-circular-").FullName;
        _tempDirectories.Add(repoPath);
        var sub = Path.Combine(repoPath, "a");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "File.cs"), "namespace N;\npublic class T {}");
        var loop = Path.Combine(sub, "loop");

        if (!TryCreateJunction(loop, sub))
        {
            return; // Same graceful skip as the sibling reparse-point tests when this environment
                    // cannot create one (see TryCreateJunction).
        }

        try
        {
            var snapshot = new RepositorySnapshot(repoPath, "test-repo", [], []);
            var builder = new CodeGraphBuilder(_extractor, [CSharpProfile.Instance], []);

            var graph = builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default);

            Assert.False(graph.Status.TraversalComplete);
            Assert.False(graph.Status.IsComplete);
        }
        finally
        {
            // Dispose()'s recursive Directory.Delete does not follow a junction into its target (a
            // non-recursive delete on the junction path alone confirmed that, and leaves the target
            // untouched) -- but a RECURSIVE delete rooted above the junction throws partway through
            // instead of skipping cleanly over it, which Dispose()'s best-effort catch then silently
            // swallows, leaking this whole temp directory. Unlinking the junction itself first (never
            // recursive -- that would follow it into the target) avoids the leak entirely.
            Directory.Delete(loop, recursive: false);
        }
    }

    [Fact]
    public void A_pre_cancelled_token_never_reports_a_complete_run()
    {
        // The property that actually matters for Task 11's pruning gate is not "the timer fires at
        // the right moment" -- it is "a cancelled run never reports itself complete". Testable without
        // any clock: pass a token that is already cancelled before Build even starts, so not a single
        // file is attempted, and the empty Skipped list alone must not read as success. This is the
        // truncated-traversal shape: TraversalComplete is what must be false here, since not a single
        // file was even visited -- a symbol could have moved to one of the files this run never
        // reached, which is a strictly different (and worse) risk than any individual file's own
        // extraction quality. IsComplete is derived from TraversalComplete, so it follows suit.
        var repoPath = Directory.CreateTempSubdirectory("okfproducer-hostile-cancel-").FullName;
        _tempDirectories.Add(repoPath);
        File.WriteAllText(Path.Combine(repoPath, "A.cs"), "namespace N;\npublic class T {}");

        var snapshot = new RepositorySnapshot(repoPath, "test-repo", [], []);
        var builder = new CodeGraphBuilder(_extractor, [CSharpProfile.Instance], []);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var graph = builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default, cts.Token);

        Assert.False(graph.Status.TraversalComplete);
        Assert.False(graph.Status.IsComplete);
        Assert.Empty(graph.Symbols);
    }

    [Fact]
    public void A_token_cancelled_after_the_first_file_still_reports_incomplete_with_partial_results()
    {
        // The cheap seam the pre-cancelled case doesn't exercise: a run that gets partway through
        // before being cancelled must still surface what it already extracted, honestly labelled
        // incomplete rather than either discarding the partial results or claiming success.
        var repoPath = Directory.CreateTempSubdirectory("okfproducer-hostile-cancel2-").FullName;
        _tempDirectories.Add(repoPath);
        File.WriteAllText(Path.Combine(repoPath, "A.cs"), "namespace N;\npublic class T {}");
        File.WriteAllText(Path.Combine(repoPath, "B.cs"), "namespace N;\npublic class U {}");

        var snapshot = new RepositorySnapshot(repoPath, "test-repo", [], []);
        using var cts = new CancellationTokenSource();
        var extractor = new CancelAfterFirstCallExtractor(_extractor, cts);
        var builder = new CodeGraphBuilder(extractor, [CSharpProfile.Instance], []);

        var graph = builder.Build(snapshot, ExtractionLimits.Default, ScopeOptions.Default, cts.Token);

        Assert.False(graph.Status.TraversalComplete);
        Assert.False(graph.Status.IsComplete);
        // Files sort Ordinal ("A.cs" before "B.cs"), so the first file the walk reaches is A.cs --
        // its symbol must still be present; B.cs must not have been attempted at all.
        Assert.Single(graph.Symbols);
        Assert.Equal("T", graph.Symbols[0].Name);
    }

    /// <summary>Wraps a real extractor and cancels <paramref name="cts"/> right after its first call.</summary>
    private sealed class CancelAfterFirstCallExtractor(ILanguageExtractor inner, CancellationTokenSource cts) : ILanguageExtractor
    {
        private bool _called;

        public ExtractionResult Extract(string relativePath, string absolutePath, LanguageProfile profile, ExtractionLimits limits)
        {
            var result = inner.Extract(relativePath, absolutePath, profile, limits);
            if (!_called)
            {
                _called = true;
                cts.Cancel();
            }

            return result;
        }
    }

    private ExtractionResult Extract(string absolutePath, ExtractionLimits limits) =>
        _extractor.Extract(Path.GetFileName(absolutePath), absolutePath, CSharpProfile.Instance, limits);

    private ExtractionResult ExtractSource(string source)
    {
        using var tmp = new TempDir();
        var path = tmp.Write("T.cs", source);
        return Extract(path, ExtractionLimits.Default);
    }

    /// <summary>A throwaway temp directory, deleted on dispose (best-effort, matching this file's own pattern).</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("okfproducer-hostile-file-").FullName;

        public string Write(string relativePath, string content)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
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
