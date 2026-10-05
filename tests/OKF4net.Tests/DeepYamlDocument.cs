// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Tests;

/// <summary>
/// Builds a concept document nested EXACTLY at the YAML subset's limit: 1000
/// collections deep, the most the parser reads and the emitter writes (the
/// one nesting rule, <c>YamlParser.MaxNestingDepth</c>). Under the defaults:
/// the frontmatter's root mapping (1), then <c>blockLevels</c> block
/// mappings (450), then <c>flowLevels</c> flow mappings (549) — 1000.
///
/// It loads, and re-emits whole. What it cannot survive is one more level,
/// which is exactly what <c>okf verify</c> adds when the nesting lives IN
/// <c>verified</c>: <c>UpsertStamp</c> normalizes a bare <c>verified</c>
/// mapping into a sequence holding it (the new stamp beside it), pushing the
/// deepest collection to 1001 — so <c>YamlEmitter</c> refuses the
/// re-emitted <c>verified</c> block. That is how this document reaches the
/// emitter's guard through a normal read-modify-write, now that the parser
/// and the emitter count nesting the same way: a document the parser reads
/// re-emits whole, so only a write that ADDS a level can exceed the limit
/// (before, a 450 block + 600 flow document parsed, because the parser
/// counted block and flow on separate counters, and then could not be
/// re-emitted at all). Building it
/// by hand (rather than assembling a <c>YamlValue</c> tree in memory) is the
/// point: this is a file a bundle can actually contain, so every layer that
/// loads and rewrites a concept meets it the way a caller would.
///
/// Block and flow are mixed on purpose: the parser must count both on one
/// counter for this document to sit at, not over, the limit.
///
/// Shared by the emitter, writer and CLI tests so all three describe the same
/// artifact — a second hand-rolled copy would drift the moment the limit
/// moved.
/// </summary>
internal static class DeepYamlDocument
{
    /// <summary>
    /// A §11-conformant document (non-empty <c>type</c>, so it is stampable)
    /// whose <paramref name="key"/> key nests <paramref name="blockLevels"/>
    /// block mappings and then <paramref name="flowLevels"/> flow mappings.
    /// </summary>
    /// <param name="key">
    /// The frontmatter key to nest under. Defaults to <c>deep</c>, an
    /// otherwise-inert key. Pass <c>"verified"</c> for a caller exercising
    /// <c>BundleConceptWriter.RecordVerifications</c>: since C7
    /// (<c>FrontmatterBlockEdit</c>), that method's surgical edit only ever
    /// re-emits the <c>verified</c> block through <c>YamlEmitter</c> — every
    /// OTHER key's value, however deep, is carried as untouched raw text and
    /// never reaches the emitter at all, so nesting depth under <c>deep</c>
    /// (or any key but <c>verified</c>) no longer reaches the emitter's guard
    /// through that path. Nesting it under <c>verified</c> instead keeps this
    /// fixture meaningful there: <c>UpsertStamp</c> preserves a pre-existing,
    /// non-matching <c>verified</c> value (or, for a bare mapping, wraps it
    /// whole) rather than discarding it, so the deep structure survives into
    /// the sequence that IS re-emitted, one level deeper than it was read.
    /// </param>
    internal static string Text(int blockLevels = 450, int flowLevels = 549, string key = "deep")
    {
        var sb = new StringBuilder("---\ntype: Metric\ntitle: Deep\n").Append(key).Append(":\n");

        // blockLevels - 1 "a:" lines, each one indent step deeper, then a
        // final line carrying the flow value on the same line as its key --
        // the shape our parser accepts without a more-indented continuation.
        for (var i = 0; i < blockLevels - 1; i++)
        {
            sb.Append(' ', (i + 1) * 2).Append("a:\n");
        }

        sb.Append(' ', blockLevels * 2).Append("a: ");
        for (var i = 0; i < flowLevels; i++)
        {
            sb.Append("{a: ");
        }

        sb.Append('1').Append('}', flowLevels).Append('\n');
        return sb.Append("---\n\nbody\n").ToString();
    }
}
