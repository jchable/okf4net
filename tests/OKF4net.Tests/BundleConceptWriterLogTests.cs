// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// <see cref="BundleConceptWriter.AppendLog"/> (spec §3, §9; decision B12): <c>log.md</c> is written
/// by the writer, through the provider, conditioned on the version read and retried on a conflict,
/// with the field guards and result strings <c>OkfBundleTools.AppendLog</c> had (that tool now
/// delegates here, and its own tests pin the rest of the behaviour).
/// </summary>
public class BundleConceptWriterLogTests
{
    private static readonly DateTime Noon = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static string Text(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    private static (BundleConceptWriter Writer, List<TimeSpan> Delays) WriterOver(IBundleProvider p)
    {
        var delays = new List<TimeSpan>();
        return (new BundleConceptWriter(p) { Sleep = delays.Add, UtcNow = () => Noon }, delays);
    }

    [Fact]
    public void The_log_is_created_then_extended_through_the_provider_under_the_version_read()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var fired = 0;
        var writer = new BundleConceptWriter(storage, onWriteCommitted: () => fired++) { UtcNow = () => Noon };

        Assert.Equal("Appended a 'Creation' entry under 2026-07-22 in log.md.", writer.AppendLog("Creation", "Bootstrapped."));
        Assert.Equal("Appended a 'Update' entry under 2026-07-22 in log.md.", writer.AppendLog("Update", "Second."));
        Assert.Equal("Appended a 'Update' entry under 2026-07-23 in log.md.", writer.AppendLog("Update", "Next day.", new DateOnly(2026, 7, 23)));

        Assert.Equal(3, fired);
        Assert.Equal(
            ["read log.md", "write log.md MustNotExist", "read log.md", "write log.md IfMatch", "read log.md", "write log.md IfMatch"],
            storage.Calls);
        var log = ChangeLog.Parse(Text(storage, "log.md"));
        Assert.Equal(["2026-07-23", "2026-07-22"], log.Days.Select(d => d.Date));
        Assert.Equal(["Bootstrapped.", "Second."], log.Days[1].Entries.Select(e => e.Text));
    }

    [Theory]
    [InlineData("", "text", "Error: invalid kind — it must not be empty.")]
    [InlineData("Update", "a\nb", "Error: invalid text — it must not contain a line break (this would let it forge fake '## date' or '* entry' lines in log.md).")]
    public void The_field_guards_moved_with_their_messages_and_write_nothing(string kind, string text, string expected)
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        Assert.Equal(expected, WriterOver(storage).Writer.AppendLog(kind, text));
        Assert.Empty(storage.Calls);
    }

    [Fact]
    public void An_entry_appended_by_another_writer_meanwhile_is_kept()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var (writer, delays) = WriterOver(storage);
        writer.AppendLog("Creation", "First.");
        storage.BeforeNextWrite(p => p.Add("log.md", "# Log\n\n## 2026-07-22\n\n* **Creation**: First.\n* **Update**: From elsewhere.\n"));

        Assert.Equal("Appended a 'Update' entry under 2026-07-22 in log.md.", writer.AppendLog("Update", "Mine."));

        Assert.Equal(["First.", "From elsewhere.", "Mine."], ChangeLog.Parse(Text(storage, "log.md")).Days.Single().Entries.Select(e => e.Text));
        Assert.Single(delays);
    }

    [Fact]
    public void A_log_that_keeps_changing_is_refused_after_three_attempts()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("log.md", "# Log\n");
        var (writer, delays) = WriterOver(storage);
        for (var nth = 1; nth <= 3; nth++)
        {
            storage.FailWrite(nth, new BundleWriteConflictException(K("log.md")));
        }

        Assert.Equal("Error: log.md kept changing while it was being written; the write was refused after 3 attempts.", writer.AppendLog("Update", "Mine."));
        Assert.Equal("# Log\n", Text(storage, "log.md"));
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public void Creating_the_log_where_objects_already_sit_under_its_name_is_refused()
    {
        var storage = new InMemoryBundleProvider(BundleKeyConstraints.FileDirectoryConflict, canWrite: true).Add("log.md/x.md", "x");
        var result = WriterOver(storage).Writer.AppendLog("Update", "Mine.");
        Assert.Equal("Error: \"log.md\" cannot be created: objects exist under it (such as \"log.md/x.md\"), so it cannot also be an object.", result);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The log's key goes through the same validator as a concept's: a host validator that
    /// refuses the name <c>log.md</c> makes the append an <c>Error:</c> text before the provider
    /// is asked anything.
    /// </summary>
    [Fact]
    public void A_log_key_the_hosts_key_policy_refuses_is_reported_and_nothing_is_asked_of_the_storage()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var options = new BundleLoadOptions
        {
            ExtraKeyValidators = [k => k.ToString() == "log.md" ? "the host reserves that name" : null],
        };
        var writer = new BundleConceptWriter(storage, options: options) { UtcNow = () => Noon };

        Assert.Equal("Error: log.md cannot be written under the bundle's key policy: the host reserves that name.", writer.AppendLog("Update", "Mine."));
        Assert.Empty(storage.Calls);
    }

    [Fact]
    public void A_log_that_is_not_UTF8_is_refused_and_left_as_it_was()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "log.md");
        byte[] bytes = [.. "# Log\n\n## 2026-07-21\n\n* caf"u8, 0xE9, .. "\n"u8];
        File.WriteAllBytes(path, bytes);

        var result = new BundleConceptWriter(tmp.Path) { UtcNow = () => Noon }.AppendLog("Update", "Mine.");

        Assert.StartsWith("Error: ", result, StringComparison.Ordinal);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(["log.md"], Directory.GetFileSystemEntries(tmp.Path).Select(Path.GetFileName));
    }

    /// <summary>
    /// A write notification that throws once the entry is committed (external review, F2, F6):
    /// the result is <c>Error: {message}</c>, as for the other string methods, but the entry is in
    /// <c>log.md</c>, appended once — the failure is not a conflict of the log's write, so nothing
    /// is retried, even when what the notification throws is a conflict of another operation
    /// (B48). Failing once, the next entry is appended normally; failing always, each entry is
    /// still appended once.
    /// </summary>
    [Theory]
    [InlineData("io", false)]
    [InlineData("io", true)]
    [InlineData("conflict", false)]
    [InlineData("conflict", true)]
    public void A_notification_failing_after_the_commit_is_an_error_text_and_the_entry_is_appended_once(string kind, bool always)
    {
        using var tmp = new TempDir();
        Exception failure = kind == "io" ? new IOException("notification failed") : new BundleWriteConflictException(K("elsewhere.md"));
        var notifications = 0;
        var delays = new List<TimeSpan>();
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            if (++notifications == 1 || always)
            {
                throw failure;
            }
        })
        { Sleep = delays.Add, UtcNow = () => Noon };

        Assert.Equal("Error: " + failure.Message, writer.AppendLog("Update", "Mine."));
        Assert.Equal(1, notifications);
        Assert.Equal(always ? "Error: " + failure.Message : "Appended a 'Update' entry under 2026-07-22 in log.md.", writer.AppendLog("Update", "Then."));
        Assert.Equal(2, notifications);
        Assert.Empty(delays);
        var lines = File.ReadAllText(Path.Combine(tmp.Path, "log.md")).Split('\n');
        Assert.Single(lines, line => line.Contains("Mine.", StringComparison.Ordinal));
        Assert.Single(lines, line => line.Contains("Then.", StringComparison.Ordinal));
    }

    /// <summary>
    /// A cancellation the notification raises is not converted (B48): it propagates unchanged once
    /// the entry is committed — the entry is in <c>log.md</c>, once, and nothing was retried.
    /// </summary>
    [Fact]
    public void A_notification_cancelled_after_the_commit_propagates_and_the_entry_stays()
    {
        using var tmp = new TempDir();
        var notifications = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            notifications++;
            throw new OperationCanceledException();
        })
        { UtcNow = () => Noon };

        Assert.Throws<OperationCanceledException>(() => writer.AppendLog("Update", "Mine."));
        Assert.Equal(1, notifications);
        Assert.Single(File.ReadAllText(Path.Combine(tmp.Path, "log.md")).Split('\n'), line => line.Contains("Mine.", StringComparison.Ordinal));
    }
}
