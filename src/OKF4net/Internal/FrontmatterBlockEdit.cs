// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// Replaces or inserts ONE top-level frontmatter key's block in a document's
/// raw text, leaving every other byte — comments, line endings, scalar
/// spellings, key order — untouched. This is what lets
/// <c>BundleConceptWriter.RecordVerifications</c> stamp <c>verified</c>
/// (§5.2) without re-emitting the whole frontmatter through <c>YamlEmitter</c>,
/// which normalized CRLF to LF, dropped comments and reflowed folded scalars
/// — a real regression the emitter's own docs and <c>RecordVerifications</c>'s
/// contract ("preserving every other frontmatter key and the body") both
/// promised did not happen.
///
/// <para>Block rule: a key's block starts at the line <c>key:</c> in column 0
/// and ends before the next line whose first character is neither a space nor
/// <c>#</c> (the next top-level key), or before the closing <c>---</c>. A
/// column-0 comment line inside the block belongs to it and is replaced with
/// it. The document is re-parsed by the caller after the edit; this type does
/// not validate YAML.</para>
/// </summary>
internal static class FrontmatterBlockEdit
{
    /// <summary>
    /// Replaces <paramref name="key"/>'s top-level block in
    /// <paramref name="documentText"/> with <paramref name="emittedBlock"/>,
    /// or inserts it just before the closing <c>---</c> fence when the key is
    /// absent. <paramref name="documentText"/> may use any line ending; the
    /// result uses the document's own (CRLF if any <c>"\r\n"</c> occurs in it,
    /// otherwise LF), and its trailing-newline-or-not shape is preserved.
    /// </summary>
    /// <param name="documentText">The whole file, frontmatter fence and body included.</param>
    /// <param name="key">The top-level frontmatter key to replace or insert, e.g. <c>"verified"</c>.</param>
    /// <param name="emittedBlock">
    /// The YAML for that key as <see cref="Yaml.YamlEmitter"/> produces it
    /// (e.g. <c>"key:\n  - by: …\n    at: …\n"</c>), LF-terminated regardless
    /// of the document's own line ending — this method re-splits it and joins
    /// with the document's ending.
    /// </param>
    /// <exception cref="DocumentValidationException">
    /// <paramref name="documentText"/> has no opening or no closing frontmatter fence.
    /// </exception>
    internal static string ReplaceTopLevelKey(string documentText, string key, string emittedBlock)
    {
        var newline = documentText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = LfLines.Split(documentText);
        var trailingNewline = documentText.EndsWith('\n');

        if (lines.Count == 0 || lines[0] != "---")
        {
            throw new DocumentValidationException("document has no frontmatter fence to edit", []);
        }

        var close = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i] == "---") { close = i; break; }
        }

        if (close < 0)
        {
            throw new DocumentValidationException("document has no closing frontmatter fence", []);
        }

        var prefix = key + ":";
        var start = -1;
        for (var i = 1; i < close; i++)
        {
            if (lines[i].StartsWith(prefix, StringComparison.Ordinal)
                && (lines[i].Length == prefix.Length || lines[i][prefix.Length] is ' ' or '\t'))
            {
                start = i;
                break;
            }
        }

        var end = close;
        if (start >= 0)
        {
            end = start + 1;
            while (end < close && (lines[end].Length == 0 || lines[end][0] is ' ' or '\t' or '#'))
            {
                end++;
            }
        }
        else
        {
            start = close;
        }

        var block = LfLines.Split(emittedBlock.TrimEnd('\n'));
        var result = new List<string>(lines.Count + block.Count);
        result.AddRange(lines.Take(start));
        result.AddRange(block);
        result.AddRange(lines.Skip(end));

        var text = string.Join(newline, result);
        return trailingNewline ? text + newline : text;
    }
}
