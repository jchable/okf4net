// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Whether a whole extraction run succeeded, and what it could not read.
///
/// Carries two distinct facts rather than one collapsed boolean (§6.3): <see cref="TraversalComplete"/>
/// -- was every eligible file even visited -- and, per file, <see cref="Skipped"/>'s recorded
/// <see cref="FileStatus"/> -- did the file that WAS visited extract cleanly. These matter separately
/// for Task 11's pruning gate, because they carry different risk. A truncated traversal (a missing or
/// unreadable repository root, or an explicit or timeout cancellation -- <see cref="TraversalComplete"/> <see langword="false"/>) means some files
/// were never visited at all: a symbol may have *moved* to one of them, so deleting its old concept
/// would lose it with no replacement -- pruning is unsafe here for a reason that has nothing to do with
/// parse quality. A completed traversal where some individual files still hit a hostile-input guard or
/// a parse-error region is, by contrast, exactly known: every visited file's outcome is recorded in
/// <see cref="Skipped"/>, so pruning is safe for the files whose recorded status is
/// <see cref="FileStatus.Extracted"/>, and unsafe only for the ids owned by the others. §6.3 already
/// states this finer rule ("scope restricted to owners whose extraction succeeded") -- this type is
/// what gives that rule something to key off other than one boolean. (Measured, not theoretical: the
/// vendored tree-sitter-c-sharp grammar mis-parses an empty collection expression <c>[]</c> in
/// expression position, which is common enough in ordinary modern C# 12+ code that
/// <see cref="FileStatus.PartiallyExtracted"/> is the steady state for a repository like this one, not
/// a rare edge case -- see that member's own doc comment for the full finding.)
/// </summary>
public sealed record RunStatus(bool TraversalComplete, IReadOnlyList<(string Path, FileStatus Status)> Skipped)
{
    /// <summary>
    /// The coarse, honest summary: <see langword="true"/> only when the traversal visited every
    /// eligible file (<see cref="TraversalComplete"/>) <i>and</i> every one of them extracted cleanly
    /// (every entry in <see cref="Skipped"/> is <see cref="FileStatus.Extracted"/>). Derived rather
    /// than stored independently, so it can never silently diverge from the two facts it summarises --
    /// this is the same check <see cref="IsComplete"/> always was, not a weakened one, just built from
    /// parts a consumer can now also inspect individually.
    /// </summary>
    public bool IsComplete =>
        TraversalComplete && LinkedDirectories.Count == 0 && Skipped.All(s => s.Status == FileStatus.Extracted);

    /// <summary>
    /// Repo-relative, <c>/</c>-separated paths of directory links (a symbolic link or a junction) the
    /// walk found and deliberately did not enter (#118). Entering one is not safe to leave to the
    /// platform: .NET follows a directory link while recursing, and nothing stops a link that points
    /// back at an ancestor. Measured on Linux: one such link yields 42 paths before the kernel refuses
    /// to resolve more hops, but two of them under one directory had yielded 430,000 paths when the
    /// measurement was abandoned after 15 seconds (every level can take either link, so the count
    /// multiplies per level). On Windows the first one ends the whole walk with a
    /// <see cref="PathTooLongException"/> that discards every file. Not entering a link is also what
    /// <c>RepositoryScanner</c> already does, and nothing is lost by it: <c>TreeSitterExtractor</c>
    /// refuses a file reached through a link (<see cref="FileStatus.SkippedSymlink"/>) in any case.
    ///
    /// <para>
    /// Kept apart from <see cref="Skipped"/> for the reason <see cref="InaccessibleDirectories"/> is -- a
    /// directory is not a file this run attempted -- but, unlike an unreadable directory, a link does
    /// <b>not</b> clear <see cref="TraversalComplete"/>: the pruning gate keys off that, and no symbol can
    /// have moved into a file this producer would never have extracted. It does clear
    /// <see cref="IsComplete"/>, so the summary still says something was not analysed.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> LinkedDirectories { get; init; } = [];

    /// <summary>
    /// Repo-relative, <c>/</c>-separated paths of directories the walk found but could not list
    /// (§2.3: <see cref="UnauthorizedAccessException"/> on <see cref="Directory.EnumerateFileSystemEntries(string)"/>),
    /// distinct from <see cref="Skipped"/> on purpose. A directory is not a file this run attempted, so
    /// recording it there would inflate the "N source file(s) visited" count <c>GenerateRun.Summarize</c>
    /// derives from <see cref="Skipped"/>'s length, and would falsify the file-keyed joins
    /// <c>BundleWriter</c>'s pruning and <c>GenerationManifest</c> both do over that same list. One
    /// unreadable directory still flips <see cref="TraversalComplete"/> to <see langword="false"/> --
    /// its contents were never visited, the same risk an unreadable root or a cancellation carries --
    /// but it earns its own list rather than a synthetic <see cref="FileStatus"/> entry with no file
    /// behind it. An <c>init</c> property, not a constructor parameter, so the many existing
    /// <c>new RunStatus(...)</c> call sites keep compiling unchanged.
    /// </summary>
    public IReadOnlyList<string> InaccessibleDirectories { get; init; } = [];

    /// <summary>
    /// A run in which the traversal completed and every eligible file extracted cleanly.
    ///
    /// <para><b>Nothing in <c>producers/src</c> constructs one through this.</b> Grepped, not assumed:
    /// every production <see cref="RunStatus"/> comes out of <c>CodeGraphBuilder.Build</c> with the
    /// statuses it actually observed, and the only readers here are this solution's test fixtures,
    /// which want "a clean run" in one token. Recorded so the next reader does not take it for a
    /// production path and reason about a code path that has no callers -- the shape this branch has
    /// been caught by more than once.</para>
    ///
    /// <para>Kept rather than moved to the test project, unlike <c>RoslynResolver</c>'s two removed
    /// summary properties: those made a claim the spec repeated and the code did not honour, while this
    /// is a factory for the type's own least surprising value, and the fourteen call sites it would
    /// churn buy nothing.</para>
    /// </summary>
    public static RunStatus Complete { get; } = new(true, []);
}
