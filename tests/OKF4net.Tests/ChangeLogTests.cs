// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Port of the Rust <c>Log</c> semantics (src/log.rs). <c>Log::parse</c> is
/// permissive by construction — it never fails, it just tolerates whatever
/// shape the input has — so most cases here assert graceful degradation
/// rather than exceptions.
/// </summary>
public class ChangeLogTests
{
    [Fact]
    public void Parse_roundtrips_wellformed_log()
    {
        // NOTE: the exact literal below differs from the task brief's draft.
        // to_markdown (log.rs:77) never inserts a blank line between a "## "
        // heading and its first bullet, always renders bullets as "* " (never
        // "- "), and always renders a kind as "**Kind**: text" (with a
        // colon) — so a source using "- " bullets or omitting the colon
        // would NOT round-trip byte-for-byte. This literal mirrors the
        // doc-comment example at the top of log.rs (lines 5-11) instead.
        var src = "# Log\n\n## 2026-07-21\n* **Update**: Added metric X.\n* Plain entry.\n\n## 2026-07-20\n* **Creation**: Initial.\n";
        var log = ChangeLog.Parse(src);
        Assert.Equal("Log", log.Title);
        Assert.Equal(2, log.Days.Count);
        Assert.Equal("2026-07-21", log.Days[0].Date);
        Assert.Equal("Update", log.Days[0].Entries[0].Kind);
        Assert.Equal("Added metric X.", log.Days[0].Entries[0].Text);
        Assert.Null(log.Days[0].Entries[1].Kind);
        Assert.Equal("Plain entry.", log.Days[0].Entries[1].Text);
        Assert.Equal("2026-07-20", log.Days[1].Date);
        Assert.Equal("Creation", log.Days[1].Entries[0].Kind);
        Assert.Equal(src, log.ToMarkdown());
    }

    [Fact]
    public void Invalid_dates_are_reported_not_fatal()
    {
        var log = ChangeLog.Parse("## not-a-date\n\n- x\n");
        Assert.Equal(new[] { "not-a-date" }, log.InvalidDates());
    }

    [Fact]
    public void Crlf_line_endings_leave_no_carriage_return_residue()
    {
        var log = ChangeLog.Parse("## 2026-01-01\r\n* x\r\n");
        Assert.Single(log.Days);
        Assert.Equal("2026-01-01", log.Days[0].Date);
        Assert.DoesNotContain('\r', log.Days[0].Date);
        Assert.Equal("x", log.Days[0].Entries[0].Text);
        Assert.DoesNotContain('\r', log.Days[0].Entries[0].Text);
    }

    [Theory]
    [InlineData("2026-07-21", true)]
    [InlineData("2026-13-01", false)] // month out of range: is_iso_date DOES validate the 1..=12 range
    [InlineData("26-07-21", false)] // wrong length (8, not 10)
    [InlineData("2026-00-01", false)] // month 0 out of range
    [InlineData("2026-07-00", false)] // day 0 out of range
    [InlineData("2026-07-32", false)] // day out of range: is_iso_date only checks 1..=31, no calendar awareness
    [InlineData("2026-02-30", true)] // is_iso_date is NOT calendar-aware: Feb 30 passes the 1..=31 day check
    [InlineData("2026/07/21", false)] // wrong separator
    [InlineData("2026-07-2a", false)] // non-digit in day
    public void IsIsoDate_checks_shape_and_ranges(string s, bool ok)
        => Assert.Equal(ok, ChangeLog.IsIsoDate(s));

    [Fact]
    public void Parse_never_throws_on_arbitrary_garbage()
    {
        var log = ChangeLog.Parse("not a log at all\njust ### junk\n\n\n**bold** no bullet\n");
        Assert.Null(log.Title);
        Assert.Empty(log.Days);
    }

    [Fact]
    public void Parse_with_no_title_starts_directly_with_a_day()
    {
        var log = ChangeLog.Parse("## 2026-07-21\n* Entry.\n");
        Assert.Null(log.Title);
        Assert.Single(log.Days);
        Assert.Equal("2026-07-21", log.Days[0].Date);
    }

    [Fact]
    public void Parse_ignores_multiple_blank_lines()
    {
        var log = ChangeLog.Parse("# Log\n\n\n\n## 2026-07-21\n\n\n* One.\n\n\n* Two.\n\n\n");
        Assert.Equal("Log", log.Title);
        Assert.Single(log.Days);
        Assert.Equal(2, log.Days[0].Entries.Count);
        Assert.Equal("One.", log.Days[0].Entries[0].Text);
        Assert.Equal("Two.", log.Days[0].Entries[1].Text);
    }

    [Fact]
    public void Parse_bullet_without_bold_marker_has_null_kind()
    {
        var log = ChangeLog.Parse("## 2026-07-21\n* Just plain text.\n");
        var entry = log.Days[0].Entries[0];
        Assert.Null(entry.Kind);
        Assert.Equal("Just plain text.", entry.Text);
    }

    [Fact]
    public void Parse_bullet_with_unclosed_bold_marker_falls_back_to_plain_text()
    {
        // No closing "**" found -> parse_entry (log.rs:114) falls through to
        // the None arm and keeps the whole trimmed body, "**" included.
        var log = ChangeLog.Parse("## 2026-07-21\n* **Unclosed bold text.\n");
        var entry = log.Days[0].Entries[0];
        Assert.Null(entry.Kind);
        Assert.Equal("**Unclosed bold text.", entry.Text);
    }

    [Fact]
    public void Parse_bullet_bold_marker_without_colon_still_parses_kind()
    {
        // strip_prefix(':') is optional (unwrap_or) -> a marker with no
        // trailing colon still yields a Kind, just no ':' to strip.
        var log = ChangeLog.Parse("## 2026-07-21\n* **Update** Added metric X.\n");
        var entry = log.Days[0].Entries[0];
        Assert.Equal("Update", entry.Kind);
        Assert.Equal("Added metric X.", entry.Text);
    }

    [Fact]
    public void Parse_accepts_both_dash_and_star_bullets()
    {
        var log = ChangeLog.Parse("## 2026-07-21\n- Dash entry.\n* Star entry.\n");
        Assert.Equal(2, log.Days[0].Entries.Count);
        Assert.Equal("Dash entry.", log.Days[0].Entries[0].Text);
        Assert.Equal("Star entry.", log.Days[0].Entries[1].Text);
    }

    [Fact]
    public void Parse_ignores_a_second_h1_heading_once_a_day_has_started()
    {
        // title is only ever set while `current` (the open day) is still
        // None (log.rs:61); a "# " line seen after the first "## " heading
        // is simply dropped (it matches neither bullet nor "## " prefix).
        var log = ChangeLog.Parse("# Log\n\n## 2026-07-21\n# Not a title\n* Entry.\n");
        Assert.Equal("Log", log.Title);
        Assert.Single(log.Days[0].Entries);
        Assert.Equal("Entry.", log.Days[0].Entries[0].Text);
    }

    [Fact]
    public void Parse_drops_bullets_that_appear_before_any_day_heading()
    {
        var log = ChangeLog.Parse("# Log\n* Orphan bullet.\n## 2026-07-21\n* Real entry.\n");
        Assert.Single(log.Days);
        Assert.Single(log.Days[0].Entries);
        Assert.Equal("Real entry.", log.Days[0].Entries[0].Text);
    }

    [Fact]
    public void Parse_trims_leading_and_trailing_whitespace()
    {
        var log = ChangeLog.Parse("  ## 2026-07-21  \n  * Entry.  \n");
        Assert.Equal("2026-07-21", log.Days[0].Date);
        Assert.Equal("Entry.", log.Days[0].Entries[0].Text);
    }

    [Fact]
    public void ToMarkdown_omits_title_block_when_absent()
    {
        var log = ChangeLog.Parse("## 2026-07-21\n* Entry.\n");
        Assert.Equal("## 2026-07-21\n* Entry.\n", log.ToMarkdown());
    }

    [Fact]
    public void ToMarkdown_of_empty_log_is_empty_string()
    {
        var log = ChangeLog.Parse("");
        Assert.Null(log.Title);
        Assert.Empty(log.Days);
        Assert.Equal("", log.ToMarkdown());
    }

    [Fact]
    public void InvalidDates_returns_empty_when_all_dates_are_iso()
    {
        var log = ChangeLog.Parse("## 2026-07-21\n* a\n\n## 2026-07-20\n* b\n");
        Assert.Empty(log.InvalidDates());
    }

    [Fact]
    public void Public_constructor_from_title_and_days_roundtrips_through_parse()
    {
        // The public (title, days) constructor lets a caller programmatically
        // rebuild a ChangeLog (e.g. after inserting/appending entries into a
        // day list obtained from Parse) and re-render via ToMarkdown, without
        // needing a private-constructor workaround.
        var days = new List<LogDay>
        {
            new("2026-07-21", new List<LogEntry> { new("Update", "Added metric X."), new(null, "Plain entry.") }),
            new("2026-07-20", new List<LogEntry> { new("Creation", "Initial.") }),
        };

        var log = new ChangeLog("Log", days);
        var markdown = log.ToMarkdown();
        Assert.Equal(
            "# Log\n\n## 2026-07-21\n* **Update**: Added metric X.\n* Plain entry.\n\n## 2026-07-20\n* **Creation**: Initial.\n",
            markdown);

        var reparsed = ChangeLog.Parse(markdown);
        Assert.Equal("Log", reparsed.Title);
        Assert.Equal(2, reparsed.Days.Count);
        Assert.Equal("2026-07-21", reparsed.Days[0].Date);
        Assert.Equal("Update", reparsed.Days[0].Entries[0].Kind);
        Assert.Equal("Added metric X.", reparsed.Days[0].Entries[0].Text);
        Assert.Null(reparsed.Days[0].Entries[1].Kind);
        Assert.Equal("Plain entry.", reparsed.Days[0].Entries[1].Text);
        Assert.Equal("2026-07-20", reparsed.Days[1].Date);
        Assert.Equal("Creation", reparsed.Days[1].Entries[0].Kind);
        Assert.Equal("Initial.", reparsed.Days[1].Entries[0].Text);
    }

    [Fact]
    public void Public_constructor_defensively_copies_the_days_list()
    {
        var days = new List<LogDay> { new("2026-07-21", new List<LogEntry> { new(null, "Entry.") }) };
        var log = new ChangeLog(null, days);

        days.Add(new LogDay("2026-07-20", new List<LogEntry> { new(null, "Later mutation.") }));

        Assert.Single(log.Days);
    }
}
