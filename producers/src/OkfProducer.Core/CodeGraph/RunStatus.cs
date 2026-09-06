// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Whether a whole extraction run succeeded, and what it could not read.
///
/// Carries two distinct facts rather than one collapsed boolean (§6.3): <see cref="TraversalComplete"/>
/// -- was every eligible file even visited -- and, per file, <see cref="Skipped"/>'s recorded
/// <see cref="FileStatus"/> -- did the file that WAS visited extract cleanly. These matter separately
/// for Task 11's pruning gate, because they carry different risk. A truncated traversal (a missing or
/// unreadable repository root, an enumeration failure such as a circular reparse point, or an explicit
/// or timeout cancellation -- <see cref="TraversalComplete"/> <see langword="false"/>) means some files
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
    public bool IsComplete => TraversalComplete && Skipped.All(s => s.Status == FileStatus.Extracted);

    /// <summary>A run in which the traversal completed and every eligible file extracted cleanly.</summary>
    public static RunStatus Complete { get; } = new(true, []);
}
