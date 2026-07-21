// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Internal;

namespace OKF4net;

/// <summary>A single log bullet. Kind is the leading bold marker (`Update`, `Creation`, …), if present.</summary>
public sealed record LogEntry(string? Kind, string Text);

/// <summary>All entries recorded under a single date heading.</summary>
public sealed record LogDay(string Date, IReadOnlyList<LogEntry> Entries);

/// <summary>
/// Parsing and building `log.md` update histories (§7). Port of the Rust
/// <c>Log</c> type (src/log.rs).
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

    private ChangeLog(string? title, IReadOnlyList<LogDay> days)
    {
        Title = title;
        Days = days;
    }

    /// <summary>
    /// Parses `log.md` text. Port of <c>Log::parse</c> (log.rs:45). Never
    /// fails — malformed or partial input degrades gracefully.
    /// </summary>
    public static ChangeLog Parse(string text)
    {
        string? title = null;
        var days = new List<LogDay>();
        string? currentDate = null;
        List<LogEntry>? currentEntries = null;

        foreach (var line in RustLines.Split(text))
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

    /// <summary>Renders the log back to markdown. Port of <c>to_markdown</c> (log.rs:77).</summary>
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
    /// (§7 requires this form). Port of <c>invalid_dates</c> (log.rs:99).
    /// </summary>
    public IReadOnlyList<string> InvalidDates()
        => Days.Select(d => d.Date).Where(d => !IsIsoDate(d)).ToList();

    /// <summary>
    /// Checks that a string is a syntactically valid ISO-8601 calendar date
    /// (`YYYY-MM-DD`). Port of <c>is_iso_date</c> (log.rs:135). Validates
    /// the month (1-12) and day (1-31) ranges but is NOT calendar-aware
    /// (e.g. 2026-02-30 passes).
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

    /// <summary>Returns the text after a `*` or `-` bullet marker, if the line is a bullet. Port of <c>bullet_body</c> (log.rs:109).</summary>
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

    /// <summary>Parses a bullet body into an optional bold `kind` and the remaining text. Port of <c>parse_entry</c> (log.rs:114).</summary>
    private static LogEntry ParseEntry(string body)
    {
        var b = body.Trim();
        if (b.StartsWith("**", StringComparison.Ordinal))
        {
            var rest = b.Substring(2);
            var end = rest.IndexOf("**", StringComparison.Ordinal);
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
}
