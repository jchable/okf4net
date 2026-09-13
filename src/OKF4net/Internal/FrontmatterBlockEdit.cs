// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net.Internal;

/// <summary>
/// Replaces or inserts ONE top-level frontmatter key's block in a document's
/// raw text, leaving every other byte — comments, line endings, scalar
/// spellings, key order — untouched. This is what lets
/// <c>BundleConceptWriter.RecordVerifications</c> stamp <c>verified</c>
/// (§5.2) without re-emitting the whole frontmatter through
/// <see cref="YamlEmitter"/>: whole-frontmatter re-emission is
/// <see cref="YamlEmitter"/>'s ordinary, documented behaviour for every OTHER
/// write path in this library (it explicitly disclaims byte-for-byte
/// identity with the source), but <c>RecordVerifications</c>'s own contract
/// already promises "preserving every other frontmatter key and the body" —
/// which sharing that behaviour did not deliver: it normalized CRLF to LF,
/// dropped comments, and reflowed folded/flow scalars everywhere in the
/// document, not just where the stamp landed.
///
/// <para><b>Block rule:</b> a key's block starts at the line that DEFINES
/// that key by this library's own parser rules
/// (<see cref="YamlParser.TryReadTopLevelKeyLine"/> — so <c>key:</c>,
/// <c>"key":</c>, <c>'key':</c>, and <c>key :</c> are all recognized, exactly
/// as <see cref="Yaml.YamlMapping.Get"/>'s first-wins lookup would find them;
/// see finding #C7-1, where a hand-rolled column-0-prefix check missed the
/// quoted/spaced spellings and inserted a silently shadowed duplicate
/// instead) and ends before the next line whose first character is neither a
/// space nor <c>#</c> (the next top-level key), or before the closing
/// <c>---</c>. A key line with NO inline value also absorbs a following
/// column-0 <c>-</c>/<c>- …</c> run — YAML's indentless block-sequence form,
/// which this library's own parser accepts (<c>BlockParser.ParseNested</c>)
/// but which used to look like "the next top-level key" to this type and cut
/// the block off mid-sequence. A column-0 comment line inside the block
/// belongs to it and is replaced with it (documented in the README); a
/// TRAILING run of blank lines does not (see
/// <see cref="ReplaceTopLevelKey"/>'s implementation) and survives untouched.
/// The frontmatter fence itself is located by
/// <see cref="OkfDocument.IsFenceLine"/> — the SAME predicate
/// <see cref="OkfDocument.Parse"/> uses, so the two can never disagree about
/// where the frontmatter/body boundary sits (finding #C7-2: they used to,
/// which let a fence-shaped line inside another key's block-scalar body
/// silently swap places with the real closing fence for one of the two
/// readers but not the other).</para>
///
/// <para>The document is re-parsed by the caller after the edit, and the
/// caller is expected to verify the result by FULL STRUCTURAL EQUALITY
/// against the intended document (frontmatter + body), not by a weaker
/// sanity check like a stamp count — this type does not itself validate
/// YAML, and a corrupted edit (e.g. the shadowed-duplicate case above) can
/// still re-parse into something superficially plausible.</para>
/// </summary>
internal static class FrontmatterBlockEdit
{
    /// <summary>
    /// Replaces <paramref name="key"/>'s top-level block in
    /// <paramref name="documentText"/> with <paramref name="emittedBlock"/>,
    /// or inserts it just before the closing <c>---</c> fence when the key is
    /// absent. <paramref name="documentText"/> may use any line ending, INCLUDING
    /// a mix of them (a CRLF line in an otherwise-LF file, or vice versa):
    /// every line this method does not touch keeps its own original
    /// terminator byte-for-byte, because the untouched prefix and suffix are
    /// literal substrings of <paramref name="documentText"/>, never rejoined
    /// under one chosen separator. Only the NEWLY INSERTED block's internal
    /// line endings are chosen (the replaced key line's own terminator, or —
    /// when inserting — the closing fence's, falling back to CRLF-if-any-
    /// where-else-in-the-document-else-LF only in the rare case that
    /// reference line itself has none, e.g. a fence with no trailing
    /// newline).
    /// </summary>
    /// <param name="documentText">The whole file, frontmatter fence and body included.</param>
    /// <param name="key">The top-level frontmatter key to replace or insert, e.g. <c>"verified"</c>.</param>
    /// <param name="emittedBlock">
    /// The YAML for that key as <see cref="YamlEmitter"/> produces it (e.g.
    /// <c>"key:\n  - by: …\n    at: …\n"</c>), LF-terminated regardless of the
    /// document's own line ending — this method re-splits it and joins with
    /// the chosen terminator (see above).
    /// </param>
    /// <exception cref="DocumentValidationException">
    /// <paramref name="documentText"/> has no opening or no closing frontmatter fence.
    /// </exception>
    internal static string ReplaceTopLevelKey(string documentText, string key, string emittedBlock)
    {
        var spans = LfLines.SplitSpans(documentText);

        string Content(int i) => documentText.Substring(spans[i].Start, spans[i].ContentEnd - spans[i].Start);

        string TerminatorOf(LineSpan span)
        {
            var length = span.TerminatorEnd - span.ContentEnd;
            return length > 0
                ? documentText.Substring(span.ContentEnd, length)
                : (documentText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        }

        if (spans.Count == 0 || !OkfDocument.IsFenceLine(Content(0)))
        {
            throw new DocumentValidationException("document has no frontmatter fence to edit", []);
        }

        var close = -1;
        for (var i = 1; i < spans.Count; i++)
        {
            if (OkfDocument.IsFenceLine(Content(i)))
            {
                close = i;
                break;
            }
        }

        if (close < 0)
        {
            throw new DocumentValidationException("document has no closing frontmatter fence", []);
        }

        // Locate the line that DEFINES `key` at the top level (column 0),
        // using this library's own parser rule for what a mapping-entry line
        // is -- not a hand-rolled prefix match -- so a quoted or
        // whitespace-shifted spelling of `key` is found exactly where
        // YamlMapping.Get's first-wins lookup would find it (finding #C7-1).
        // A line that does not start at column 0 with a non-blank,
        // non-comment character cannot be a top-level entry at all, so it is
        // skipped before even asking the parser -- this is also what keeps a
        // COLUMN-0 fence-shaped line from ever being mistaken for a key line
        // (it has no ':' the split would accept as a key/value separator
        // there, so TryReadTopLevelKeyLine already returns null for it, but
        // skipping first avoids the question for every other continuation
        // line too).
        var keyLine = -1;
        var keyHasInlineValue = false;
        for (var i = 1; i < close; i++)
        {
            var content = Content(i);
            if (content.Length == 0 || content[0] is ' ' or '\t' or '#')
            {
                continue;
            }

            if (YamlParser.TryReadTopLevelKeyLine(content) is { } parsed
                && string.Equals(parsed.KeyName, key, StringComparison.Ordinal))
            {
                keyLine = i;
                keyHasInlineValue = parsed.HasInlineValue;
                break;
            }
        }

        int start;
        int end;
        string terminator;
        if (keyLine >= 0)
        {
            start = keyLine;
            end = keyLine + 1;
            while (end < close)
            {
                var content = Content(end);
                if (content.Length == 0 || content[0] is ' ' or '\t' or '#')
                {
                    end++;
                    continue;
                }

                // YAML's indentless block-sequence form (`key:\n- a\n- b`,
                // no extra indent on the items) is only legal when the key
                // line itself carried no inline value -- exactly the
                // condition BlockParser.ParseNested checks before it looks
                // for one. Absorbing a column-0 `-`/`- …` run otherwise would
                // swallow a line that could not possibly belong to this
                // key's already-inline value in the first place (the
                // original document would already have failed to parse).
                if (!keyHasInlineValue && (content == "-" || content.StartsWith("- ", StringComparison.Ordinal)))
                {
                    end++;
                    continue;
                }

                break;
            }

            // A trailing run of blank lines is formatting BETWEEN entries,
            // not part of this key's own value -- unlike the column-0
            // comment case above (documented, deliberate: a comment
            // immediately after the block is replaced along with it), a
            // blank line here has no annotation relationship to the value
            // and deleting it would be a silent, unrelated change. Only
            // TRAILING blanks are excluded: one embedded between two
            // absorbed continuation lines still reads as part of the
            // (unusual, but not impossible) shape being replaced.
            while (end > start + 1 && Content(end - 1).Length == 0)
            {
                end--;
            }

            terminator = TerminatorOf(spans[start]);
        }
        else
        {
            start = close;
            end = close;
            terminator = TerminatorOf(spans[close]);
        }

        var blockLines = LfLines.Split(emittedBlock.TrimEnd('\n'));
        var blockText = string.Join(terminator, blockLines) + terminator;

        // Everything outside [start, end) is a literal substring of the
        // original text, never rejoined under a chosen separator -- this is
        // what preserves every OTHER line's own terminator byte-for-byte,
        // even in a document that mixes CRLF and LF (finding #C7-3).
        return documentText[..spans[start].Start] + blockText + documentText[spans[end].Start..];
    }
}
