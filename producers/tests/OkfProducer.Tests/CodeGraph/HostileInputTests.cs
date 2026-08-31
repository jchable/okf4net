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
