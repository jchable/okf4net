// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Decodes a source file's bytes to the .NET <see cref="string"/> that every extractor in this
/// producer then measures offsets into.
///
/// <para>
/// This lives in <c>OkfProducer.Core</c>, next to <see cref="Utf8Offsets"/> and for the same reason:
/// <see cref="CallSite.Offset"/> is a UTF-8 byte offset into <i>the decoded text</i> (see
/// <see cref="Utf8Offsets.ToUtf8"/>'s remarks), so two extractors only agree on that offset if they
/// agree, byte for byte, on the string they decoded. They do not agree by accident. A UTF-8 BOM is
/// three bytes: an extractor that strips it and one that keeps it as a leading U+FEFF disagree about
/// every offset in the file by exactly 3, and the resulting failure is not a missing edge -- it is a
/// call credited to whatever declaration happens to sit three bytes away, silently (§2.1). Duplicating
/// this logic per extractor is therefore not a style preference; it is the drift that produces that
/// bug. Both <c>TreeSitterExtractor</c> and <c>RoslynResolver</c> call this one method.
/// </para>
///
/// <para>
/// <b>Do not move this back into an extractor.</b> It reads like tree-sitter-specific file reading and
/// it is not: the moment each extractor owns a copy, nothing keeps the two copies decoding the same
/// bytes to the same string, and the failure that follows is silent misattribution rather than a test
/// going red. Living in <c>Core</c> is also what lets the Roslyn resolver use it without referencing
/// the tree-sitter project and its ~590 MB of native grammars -- the same reason
/// <see cref="Utf8Offsets"/> lives here.
/// </para>
/// </summary>
public static class SourceDecoder
{
    /// <summary>
    /// §2.3's two accepted encodings, selected by byte-order mark: UTF-8 (with or without its 3-byte
    /// BOM) and UTF-16, either byte order, but only *with* its 2-byte BOM -- raw UTF-16 with no BOM is
    /// deliberately not guessed at, since nothing distinguishes it reliably from binary content or
    /// from UTF-8, and §2.3 says "UTF-16 with BOM", not "UTF-16". Bytes with no recognized BOM are
    /// decoded as UTF-8. Every branch enables <c>throwOnInvalidBytes</c> (confirmed, not assumed, to
    /// throw <see cref="DecoderFallbackException"/> for both an odd trailing byte and an unpaired
    /// surrogate under <see cref="UnicodeEncoding"/>, the same exception type <see cref="UTF8Encoding"/>
    /// throws), so a byte sequence that is not valid in the selected encoding throws rather than being
    /// silently repaired with a U+FFFD replacement character -- a repair would shift every subsequent
    /// offset in the file.
    /// </summary>
    /// <exception cref="DecoderFallbackException">
    /// <paramref name="bytes"/> is not valid in the encoding its byte-order mark selected.
    /// </exception>
    public static string DecodeStrict(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (HasUtf8Bom(bytes))
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 3, bytes.Length - 3);
        }

        if (HasUtf16Bom(bytes, bigEndian: false))
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true).GetString(bytes, 2, bytes.Length - 2);
        }

        if (HasUtf16Bom(bytes, bigEndian: true))
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true).GetString(bytes, 2, bytes.Length - 2);
        }

        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 0, bytes.Length);
    }

    private static bool HasUtf16Bom(byte[] bytes, bool bigEndian) =>
        bigEndian
            ? bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
            : bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE && !IsUtf32LeBom(bytes);

    /// <summary>
    /// <c>FF FE 00 00</c> is the UTF-32 LE BOM, and its first two bytes are byte-for-byte identical to
    /// the UTF-16 LE BOM (<c>FF FE</c>) alone. Without this guard a UTF-32 LE file would be
    /// misdetected as UTF-16 LE and decoded into NUL-interleaved garbage instead of correctly falling
    /// through to (and being rejected by) the UTF-8 branch -- §2.3 accepts UTF-8 and UTF-16-with-BOM
    /// only, never UTF-32. The UTF-32 BE BOM (<c>00 00 FE FF</c>) needs no equivalent guard: its first
    /// two bytes never collide with the UTF-16 BE BOM (<c>FE FF</c>) in the first place.
    /// </summary>
    private static bool IsUtf32LeBom(byte[] bytes) =>
        bytes.Length >= 4 && bytes[2] == 0x00 && bytes[3] == 0x00;

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
