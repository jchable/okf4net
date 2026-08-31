// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Converts between a .NET string index (UTF-16 code units) and a UTF-8 byte offset into the same
/// text. Roslyn reports positions as UTF-16 code-unit offsets, so its extractor (Task 6) needs
/// <see cref="ToUtf8(string, int)"/> to reach the shared UTF-8-byte identity. tree-sitter's native C
/// API positions in UTF-8 bytes too -- but <c>TreeSitter.DotNet</c> 1.3.0's public .NET API does not
/// expose that: when parsing a .NET <see cref="string"/> (the only input its <c>Parser.Parse</c>
/// accepts; there is no <c>byte[]</c> overload), its <c>Node.StartIndex</c>/<c>EndIndex</c> and
/// <c>Point.Column</c> come back as UTF-16 offsets into that same .NET string, not raw tree-sitter
/// bytes. Measured directly against the package, not assumed or inherited from the native API's
/// documented behaviour -- see <c>OkfProducer.CodeGraph.TreeSitter</c>'s <c>TreeSitterExtractor</c>
/// and its task report for the café/emoji/CRLF evidence. So <c>TreeSitterExtractor</c> calls
/// <see cref="ToUtf8(string, int)"/> too, on every offset it reads from a tree-sitter <c>Node</c> --
/// this is <b>not</b> a redundant double conversion sitting alongside a tree-sitter offset that was
/// already UTF-8. Do not remove it because the two engines currently agree once both are converted:
/// that agreement is an artefact of how this particular binding is fed a .NET string rather than raw
/// bytes, not a contract, and a binding upgrade or a future byte-based parse path could break it
/// silently, in the wrong-attachment direction (§2.1) rather than a missing-attachment one. Both
/// engines only ever produce offsets that land on a codepoint boundary -- never inside a surrogate
/// pair -- and these conversions assume the same. Lives in <c>OkfProducer.Core</c>, not the
/// tree-sitter project, so the Roslyn extractor can use it without pulling in ~590 MB of native
/// grammars.
/// </summary>
public static class Utf8Offsets
{
    /// <summary>
    /// Converts a UTF-16 code-unit offset into <paramref name="text"/> to the equivalent UTF-8
    /// byte offset. A naive per-<c>char</c> loop that scores each UTF-16 code unit independently is
    /// wrong here: a surrogate pair's two code units only mean anything as a pair, and scoring
    /// either half alone (as if it were a lone BMP character) miscounts the byte length of every
    /// astral-plane character. <see cref="Encoding.GetByteCount(ReadOnlySpan{char})"/> encodes the
    /// prefix as a whole and does not have this bug.
    /// </summary>
    public static int ToUtf8(string text, int utf16Offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (utf16Offset < 0 || utf16Offset > text.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(utf16Offset), utf16Offset, "must be within the text.");
        }

        return Encoding.UTF8.GetByteCount(text.AsSpan(0, utf16Offset));
    }

    /// <summary>
    /// Converts a UTF-8 byte offset into the UTF-8 encoding of <paramref name="text"/> to the
    /// equivalent UTF-16 code-unit offset, by walking <paramref name="text"/> one Unicode scalar
    /// (rune) at a time -- so a surrogate pair is always advanced over as the single codepoint it
    /// represents, both in its UTF-16 length (2) and its UTF-8 length (4).
    /// </summary>
    public static int ToUtf16(string text, int utf8Offset)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (utf8Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(utf8Offset), utf8Offset, "must not be negative.");
        }

        var utf16Index = 0;
        var utf8Count = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (utf8Count >= utf8Offset)
            {
                return utf16Index;
            }

            utf8Count += rune.Utf8SequenceLength;
            utf16Index += rune.Utf16SequenceLength;
        }

        if (utf8Count != utf8Offset)
        {
            throw new ArgumentOutOfRangeException(nameof(utf8Offset), utf8Offset, "beyond the end of the text.");
        }

        return utf16Index;
    }
}
