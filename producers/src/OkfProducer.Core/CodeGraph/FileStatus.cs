// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// How extraction went for one file. Only <see cref="Extracted"/> is produced by Task 1's
/// <c>CodeGraphBuilder</c> itself; the rest are hostile-input outcomes a real
/// <c>ILanguageExtractor</c> reports once Task 4 adds the policy that produces them.
/// </summary>
public enum FileStatus
{
    /// <summary>The file was read and parsed in full.</summary>
    Extracted,

    /// <summary>
    /// The file was read and parsed, but extraction stopped early (e.g. a timeout), or the parsed
    /// tree contains at least one error region.
    ///
    /// <para><b>Measured, not theoretical: this is the steady state for a modern C# 12+ codebase,
    /// not a rare edge case.</b> The vendored tree-sitter-c-sharp grammar (via <c>TreeSitter.DotNet</c>)
    /// cannot parse an <i>empty</i> collection expression <c>[]</c> in any expression position --
    /// a constructor argument, a method argument, a property initializer, a <c>return</c> expression,
    /// or the right-hand side of <c>??</c> -- all measured to set <c>Node.HasError</c> on the
    /// enclosing subtree. It is mis-parsed as an <c>element_binding_expression</c> (the
    /// null-conditional indexer rule, e.g. <c>a?[i]</c>), and critically, that node's own
    /// <c>IsError</c> and <c>IsMissing</c> are both <see langword="false"/> -- <c>HasError</c> is the
    /// only signal that catches it; searching the tree for a literal <c>ERROR</c>- or
    /// <c>MISSING</c>-typed node finds nothing. A non-empty collection expression (<c>[1]</c>) parses
    /// cleanly in every one of those same positions -- the bug is specific to the empty case. This
    /// matters because <c>[]</c> is the idiomatic C# 12+ way to write an empty array/list/collection,
    /// used constantly in ordinary code (including this producer's own source, e.g.
    /// <c>new ExtractionResult([], [], status)</c>): 3 of 6 real files sampled from this repository
    /// came back <see cref="PartiallyExtracted"/> for exactly this reason (see
    /// <c>producers/tests/OkfProducer.Tests/CodeGraph/HostileInputTests.cs</c>'s
    /// <c>An_empty_collection_expression_used_as_an_argument_is_a_live_grammar_gap_not_a_theoretical_one</c>,
    /// which pins the current behaviour so a future grammar upgrade that fixes this is noticed rather
    /// than silently changing what gets pruned). This is exactly why <see cref="RunStatus.IsComplete"/>
    /// alone is not enough to gate pruning -- see <see cref="RunStatus"/>'s own doc comment for the
    /// finer <see cref="RunStatus.TraversalComplete"/> distinction this finding forced.</para>
    /// </summary>
    PartiallyExtracted,

    /// <summary>The file exceeded the configured maximum size and was not read.</summary>
    SkippedTooLarge,

    /// <summary>The file's bytes could not be decoded as text.</summary>
    SkippedEncoding,

    /// <summary>The file's path exceeded the configured maximum directory depth.</summary>
    SkippedDepth,

    /// <summary>The file could not be opened for reading.</summary>
    SkippedUnreadable,

    /// <summary>The path is a symlink and was not followed.</summary>
    SkippedSymlink,
}
