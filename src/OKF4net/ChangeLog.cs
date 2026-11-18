// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net;

/// <summary>A single log bullet. Kind is the leading bold marker (`Update`, `Creation`, …), if present.</summary>
public sealed record LogEntry(string? Kind, string Text);

/// <summary>All entries recorded under a single date heading.</summary>
public sealed record LogDay(string Date, IReadOnlyList<LogEntry> Entries);

/// <summary>
/// Parsing and building `log.md` update histories (§9).
///
/// A log is a flat list of date-grouped entries, newest first:
/// <code>
/// # Directory Update Log
///
/// ## 2026-05-22
/// * **Update**: Added a new table reference.
/// * **Creation**: Established the playbook.
/// </code>
///
/// Date headings use ISO-8601 <c>YYYY-MM-DD</c>. The leading bold word
/// (<c>**Update**</c>, <c>**Creation**</c>, …) is a convention, not a
/// requirement.
/// </summary>
public sealed class ChangeLog
{
    /// <summary>The top-level `# ` heading text, if any.</summary>
    public string? Title { get; }

    /// <summary>Date-grouped entries, in document order (the convention is newest-first).</summary>
    public IReadOnlyList<LogDay> Days { get; }

    /// <summary>
    /// Creates a change log directly from a title and a pre-built day list —
    /// e.g. for a caller that programmatically inserts or appends entries
    /// into the <see cref="Days"/> obtained from an earlier <see cref="Parse"/>
    /// call, then wants to re-render via <see cref="ToMarkdown"/>. Defensively
    /// copies <paramref name="days"/>, mirroring <see cref="Parse"/>'s own
    /// immutable-snapshot behaviour (and <see cref="OKF4net.Yaml.YamlSequence"/>'s
    /// constructor). A thin data constructor with no validation beyond what
    /// the type system already enforces, matching <see cref="OkfDocument"/>'s
    /// two-argument constructor.
    /// </summary>
    /// <param name="title">The top-level <c>#</c> heading text, or <c>null</c> for none.</param>
    /// <param name="days">Date-grouped entries, in the order they should render (the convention is newest-first).</param>
    public ChangeLog(string? title, IReadOnlyList<LogDay> days)
    {
        Title = title;
        Days = [.. days];
    }

    /// <summary>
    /// Parses `log.md` text. Never fails — malformed or partial input
    /// degrades gracefully. A leading frontmatter block is not part of the §9
    /// log and is skipped (#258): none of its lines is taken for the title, a
    /// day or an entry. It is one only when the loader would read it as one —
    /// a <c>---</c> fence at column 0 on the first line, closed by the next
    /// such fence, around a non-empty YAML mapping; a <c>---</c> thematic
    /// break around log content is read as the log it is. <see cref="ToMarkdown"/>
    /// renders the log only, never that block.
    /// </summary>
    public static ChangeLog Parse(string text) => ParseFrom(text, FrontmatterBlockLength(text, out _));

    /// <summary><see cref="Parse(string)"/> over <paramref name="text"/> past its first <paramref name="start"/> characters — the frontmatter block <see cref="FrontmatterBlockLength"/> measured.</summary>
    internal static ChangeLog ParseFrom(string text, int start)
    {
        string? title = null;
        var days = new List<LogDay>();
        string? currentDate = null;
        List<LogEntry>? currentEntries = null;

        foreach (var line in LfLines.Split(text[start..]))
        {
            var t = line.TrimEnd().TrimStart();
            if (t.StartsWith("## ", StringComparison.Ordinal))
            {
                if (currentDate != null)
                {
                    days.Add(new LogDay(currentDate, currentEntries!));
                }

                currentDate = t.Substring(3).Trim();
                currentEntries = new List<LogEntry>();
            }
            else if (t.StartsWith("# ", StringComparison.Ordinal))
            {
                if (title == null && currentDate == null)
                {
                    title = t.Substring(2).Trim();
                }
            }
            else
            {
                var body = BulletBody(t);
                if (body != null && currentDate != null)
                {
                    currentEntries!.Add(ParseEntry(body));
                }
            }
        }

        if (currentDate != null)
        {
            days.Add(new LogDay(currentDate, currentEntries!));
        }

        return new ChangeLog(title, days);
    }

    /// <summary>
    /// The length of the frontmatter block <paramref name="text"/> opens with — its
    /// opening fence through its closing fence and that fence's own line end, if it has
    /// one — or 0 when it opens with none.
    ///
    /// <para>The fences are found by the predicate <see cref="OkfDocument.Parse"/> uses
    /// (<see cref="OkfDocument.IsFenceLine"/>), and the region between them counts as a
    /// frontmatter only when <see cref="OkfDocument.Parse"/> reads it as a non-empty
    /// YAML mapping — what the loader reads as a concept's frontmatter. Anything else is
    /// log content and gives 0: an opening fence never closed, and a region that is not
    /// valid YAML in the supported subset, is not a mapping, or holds no key (empty, or
    /// comments only). That is what keeps a log opening with a <c>---</c> thematic break
    /// (§9's own spec uses them) from having the days inside it hidden from the
    /// validator, <c>okf_changes_since</c> and the writer. A region that does parse as a
    /// mapping is a frontmatter even if a markdown reader would have seen a break
    /// there: the loader reads it the same way.</para>
    ///
    /// <para>The block is kept, or skipped, as the exact characters it is: it is parsed
    /// only to decide whether it is one, never re-emitted.</para>
    /// </summary>
    /// <param name="text">The whole <c>log.md</c> text.</param>
    /// <param name="lineEnd">The closing fence's line end (<c>"\n"</c> or <c>"\r\n"</c>), or the opening fence's when the closing one ends the text; <c>"\n"</c> when there is no block.</param>
    internal static int FrontmatterBlockLength(string text, out string lineEnd)
    {
        lineEnd = "\n";

        // The first line alone decides most logs: no fence, no block, nothing split.
        var next = NextLine(text, 0, out var content, out var openingEnd);
        if (!OkfDocument.IsFenceLine(content))
        {
            return 0;
        }

        while (next < text.Length)
        {
            var start = next;
            next = NextLine(text, start, out content, out var end);
            if (!OkfDocument.IsFenceLine(content))
            {
                continue;
            }

            if (!OkfDocument.TryParse(text[..next], out var doc, out _) || !doc.HasFrontmatterBlock || doc.Frontmatter.IsEmpty)
            {
                return 0;
            }

            lineEnd = end.Length > 0 ? end : openingEnd;
            return next;
        }

        return 0;
    }

    /// <summary>
    /// The line of <paramref name="text"/> starting at <paramref name="start"/>, split the
    /// way <see cref="LfLines"/> splits (on <c>\n</c>, a preceding <c>\r</c> stripped):
    /// its content, its line end, and the offset just past that line end.
    /// </summary>
    private static int NextLine(string text, int start, out string content, out string lineEnd)
    {
        var lf = text.IndexOf('\n', start);
        if (lf < 0)
        {
            content = text[start..];
            lineEnd = string.Empty;
            return text.Length;
        }

        var contentEnd = lf > start && text[lf - 1] == '\r' ? lf - 1 : lf;
        content = text[start..contentEnd];
        lineEnd = text[contentEnd..(lf + 1)];
        return lf + 1;
    }

    /// <summary>Renders the log back to markdown (the §9 log only: a frontmatter block <see cref="Parse"/> skipped is not part of it).</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        if (Title != null)
        {
            sb.Append("# ").Append(Title).Append("\n\n");
        }

        for (var i = 0; i < Days.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            var day = Days[i];
            sb.Append("## ").Append(day.Date).Append('\n');
            foreach (var entry in day.Entries)
            {
                if (entry.Kind != null)
                {
                    sb.Append("* **").Append(entry.Kind).Append("**: ").Append(entry.Text).Append('\n');
                }
                else
                {
                    sb.Append("* ").Append(entry.Text).Append('\n');
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the date headings that are not valid ISO-8601 `YYYY-MM-DD`
    /// (§9 requires this form).
    /// </summary>
    public IReadOnlyList<string> InvalidDates()
        => Days.Select(d => d.Date).Where(d => !IsIsoDate(d)).ToList();

    /// <summary>
    /// Checks that a string is a syntactically valid ISO-8601 calendar date
    /// (`YYYY-MM-DD`). Validates the month (1-12) and day (1-31) ranges but
    /// is NOT calendar-aware (e.g. 2026-02-30 passes).
    /// </summary>
    public static bool IsIsoDate(string s)
    {
        if (s.Length != 10 || s[4] != '-' || s[7] != '-')
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if (!char.IsAsciiDigit(s[i])) return false;
        }

        for (var i = 5; i < 7; i++)
        {
            if (!char.IsAsciiDigit(s[i])) return false;
        }

        for (var i = 8; i < 10; i++)
        {
            if (!char.IsAsciiDigit(s[i])) return false;
        }

        if (!int.TryParse(s.AsSpan(5, 2), out var month))
        {
            month = 0;
        }

        if (!int.TryParse(s.AsSpan(8, 2), out var day))
        {
            day = 0;
        }

        return month is >= 1 and <= 12 && day is >= 1 and <= 31;
    }

    /// <summary>Returns the text after a `*` or `-` bullet marker, if the line is a bullet.</summary>
    private static string? BulletBody(string line)
    {
        if (line.StartsWith("* ", StringComparison.Ordinal))
        {
            return line.Substring(2);
        }

        if (line.StartsWith("- ", StringComparison.Ordinal))
        {
            return line.Substring(2);
        }

        return null;
    }

    /// <summary>Parses a bullet body into an optional bold `kind` and the remaining text.</summary>
    private static LogEntry ParseEntry(string body)
    {
        var b = body.Trim();
        if (b.StartsWith("**", StringComparison.Ordinal))
        {
            var rest = b.Substring(2);
            var end = ClosingBoldMarker(rest);
            if (end >= 0)
            {
                var kind = rest.Substring(0, end).Trim();
                var text = rest.Substring(end + 2).TrimStart();
                if (text.StartsWith(':'))
                {
                    text = text.Substring(1);
                }

                text = text.TrimStart();
                return new LogEntry(kind, text);
            }
        }

        return new LogEntry(null, b);
    }

    /// <summary>
    /// The index, in <paramref name="rest"/> (a bullet body past its opening
    /// <c>**</c>), of the <c>**</c> that CLOSES that opener — or <c>-1</c>
    /// when none does.
    ///
    /// Not simply the first <c>**</c>: a kind may itself contain a bold span,
    /// and <see cref="ToMarkdown"/> renders it verbatim between the delimiters
    /// (<c>* **Upd - **Forged**: kind**: text</c>). Taking the first marker
    /// read that back as kind <c>"Upd -"</c> with the rest swallowed into the
    /// text, so an entry did not survive a round trip through this type's own
    /// renderer. Inner markers are paired off instead, by the one flanking
    /// rule that tells them apart: a <c>**</c> preceded by a non-space
    /// character can close the span it is inside, and one preceded by a space
    /// (or by nothing) can only open a new one. That is the reading a
    /// CommonMark renderer gives the same line, so the parse agrees with what
    /// a human sees in a rendered <c>log.md</c>.
    ///
    /// Deliberately <c>-1</c> for an UNBALANCED body (more openers than
    /// closers, e.g. <c>**Kind **x**</c>): the §9 parser is permissive, so it
    /// then keeps the whole body as plain text — exactly what it already did
    /// for a body with no closing marker at all.
    /// </summary>
    /// <param name="rest">The bullet body with its opening <c>**</c> removed.</param>
    private static int ClosingBoldMarker(string rest)
    {
        var depth = 1;
        var i = 0;
        while ((i = rest.IndexOf("**", i, StringComparison.Ordinal)) >= 0)
        {
            depth += i > 0 && !char.IsWhiteSpace(rest[i - 1]) ? -1 : 1;
            if (depth == 0)
            {
                return i;
            }

            i += 2;
        }

        return -1;
    }
}
