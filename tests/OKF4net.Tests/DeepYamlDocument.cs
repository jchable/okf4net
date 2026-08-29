// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Tests;

/// <summary>
/// Builds a concept document whose frontmatter <b>parses</b> and then
/// <b>cannot be emitted</b> — the one input shape that reaches
/// <c>YamlEmitter</c>'s nesting guard through a normal read-modify-write.
///
/// It exists because the two limits are counted differently.
/// <c>YamlParser</c> enforces its 1000-level cap with two independent
/// counters, one for block nesting and one for flow; <c>YamlEmitter</c> has a
/// single counter covering both. So a frontmatter mixing 450 block levels
/// with 600 flow levels is under the cap twice on the way in and over it once
/// on the way out. Building it by hand (rather than assembling a
/// <c>YamlValue</c> tree in memory) is the point: this is a file a bundle can
/// actually contain, so every layer that loads and rewrites a concept meets
/// it the way a caller would.
///
/// The defaults are not symmetric because the counters are not: the block
/// parser charges its counter roughly twice per nesting level (a node and its
/// mapping/nested arm both increment it), so 450 block levels sit near 900 of
/// its 1000 — 600 there fails to PARSE, which would test nothing. The flow
/// counter charges about once per level. Their sum, 1050, is what clears the
/// emitter's single 1000.
///
/// Shared by the emitter, writer and CLI tests so all three describe the same
/// artifact — a second hand-rolled copy would drift the moment either limit
/// moved.
/// </summary>
internal static class DeepYamlDocument
{
    /// <summary>
    /// A §11-conformant document (non-empty <c>type</c>, so it is stampable)
    /// whose <c>deep</c> key nests <paramref name="blockLevels"/> block
    /// mappings and then <paramref name="flowLevels"/> flow mappings.
    /// </summary>
    internal static string Text(int blockLevels = 450, int flowLevels = 600)
    {
        var sb = new StringBuilder("---\ntype: Metric\ntitle: Deep\ndeep:\n");

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
