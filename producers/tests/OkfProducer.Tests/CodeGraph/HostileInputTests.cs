// SPDX-License-Identifier: LGPL-3.0-or-later
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
        => Assert.True(BuildWith(("A.cs", FileStatus.Extracted)).Status.IsComplete);

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

    [Fact]
    public void A_tree_with_error_nodes_keeps_what_parsed_and_reports_partial()
    {
        var result = ExtractSource("namespace N;\npublic class T { public void M() { @@@ } public void N2() { } }");

        Assert.Equal(FileStatus.PartiallyExtracted, result.Status);
        Assert.Contains(result.Symbols, s => s.Name == "N2");
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

        Assert.False(graph.Status.IsComplete);
        Assert.Contains(graph.Status.Skipped, s => s.Path == deepRelativePath && s.Status == FileStatus.SkippedDepth);
        Assert.Empty(graph.Symbols);
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
