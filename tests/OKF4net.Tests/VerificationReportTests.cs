// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// <see cref="VerificationOutcome.Report"/> (spec §1, §4, decision B14): a verification batch is
/// several objects with no transaction, so the outcome says object by object what was written and
/// what failed, beside the unchanged <see cref="VerificationOutcome.Records"/> and the message's
/// <c>— already written: …</c> suffix.
/// </summary>
public class VerificationReportTests
{
    private const string Doc = "---\ntype: Metric\ntitle: DAU\ndescription: Daily active users.\n---\n\nbody\n";

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static BundleConceptWriter WriterOver(IBundleProvider p) =>
        new(p) { Sleep = _ => { }, UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };

    [Fact]
    public void A_recorded_batch_reports_every_key_written_in_order()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("b.md", Doc).Add("a/c.md", Doc);

        var outcome = WriterOver(storage).RecordVerifications(["b", "a/c"], "human:ada");

        Assert.True(outcome.Recorded, outcome.Message);
        Assert.Equal([K("b.md"), K("a/c.md")], outcome.Report.Written);
        Assert.Empty(outcome.Report.Deleted);
        Assert.Empty(outcome.Report.Failed);
        Assert.True(outcome.Report.Succeeded);
    }

    [Fact]
    public void A_batch_refused_before_its_first_write_reports_nothing()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc);

        var outcome = WriterOver(storage).RecordVerifications(["a", "missing"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: concept \"missing\" does not exist.", outcome.Message);
        Assert.Same(BundleWriteReport.Empty, outcome.Report);
    }

    [Fact]
    public void A_write_failing_mid_batch_is_reported_with_its_reason_beside_what_was_written()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc).Add("c.md", Doc);
        storage.FailWrite(2, new IOException("disk full"));

        var outcome = WriterOver(storage).RecordVerifications(["a", "b", "c"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: disk full — already written: a", outcome.Message);
        Assert.Equal(["a"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal([K("a.md")], outcome.Report.Written);
        var failed = Assert.Single(outcome.Report.Failed);
        Assert.Equal(K("b.md"), failed.Key);
        Assert.Equal("disk full", failed.Reason);
        Assert.False(outcome.Report.Succeeded);
    }

    [Fact]
    public void A_write_failing_first_reports_no_key_written_and_that_key_failed_without_a_suffix()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc);
        storage.FailWrite(1, new IOException("disk full"));

        var outcome = WriterOver(storage).RecordVerifications(["a", "b"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: disk full", outcome.Message);
        Assert.DoesNotContain("already written", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(outcome.Records);
        Assert.Empty(outcome.Report.Written);
        var failed = Assert.Single(outcome.Report.Failed);
        Assert.Equal(K("a.md"), failed.Key);
        Assert.Equal("disk full", failed.Reason);
    }

    /// <summary>
    /// A failure whose exception carries no message leaves the writer's text as exactly
    /// <c>Error: </c>: the report still gets its entry, with a fixed reason, and the outcome keeps
    /// its message and its <c>— already written: …</c> suffix instead of being replaced by the
    /// refusal of an empty reason.
    /// </summary>
    [Fact]
    public void A_write_failing_with_an_empty_message_still_reports_a_reason_and_keeps_the_outcome()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc);
        storage.FailWrite(2, new IOException(""));

        var outcome = WriterOver(storage).RecordVerifications(["a", "b"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal("Error:  — already written: a", outcome.Message);
        Assert.Equal(["a"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal([K("a.md")], outcome.Report.Written);
        var failed = Assert.Single(outcome.Report.Failed);
        Assert.Equal(K("b.md"), failed.Key);
        Assert.False(string.IsNullOrWhiteSpace(failed.Reason));
    }

    [Fact]
    public void An_early_refusal_and_a_default_outcome_carry_the_same_report_state()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc);

        var refused = WriterOver(storage).RecordVerifications(["a"], "not an actor");

        Assert.False(refused.Recorded);
        Assert.Same(BundleWriteReport.Empty, refused.Report);
        Assert.Equal(refused with { Report = BundleWriteReport.Empty }, refused);
    }

    [Fact]
    public void The_report_of_a_default_outcome_is_empty_never_null()
    {
        Assert.Same(BundleWriteReport.Empty, default(VerificationOutcome).Report);
    }

    /// <summary>
    /// B14 and B15 on the real filesystem: the second concept's directory is swapped for a
    /// junction after its parent was resolved (the late-guard hook), the provider refuses the path,
    /// and the batch reports it with the writer's reparse-point message and the same
    /// <c>— already written: …</c> suffix as before the write moved behind the provider.
    /// </summary>
    [SkippableFact]
    public void A_path_refused_by_the_provider_mid_batch_keeps_the_writers_message_and_suffix()
    {
        using var external = new TempDir();
        using var tmp = new TempDir();
        tmp.Write("a/x.md", Doc);
        tmp.Write("b/y.md", Doc);
        var writer = new BundleConceptWriter(tmp.Path) { UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };
        var writes = 0;
        var swapped = false;
        writer.BeforeLateReparseCheckForTest = () =>
        {
            if (++writes == 2)
            {
                Directory.Move(Path.Combine(tmp.Path, "b"), Path.Combine(tmp.Path, "b-moved"));
                swapped = tmp.TryCreateJunctionToExternalDir("b", external.Path);
            }
        };

        var outcome = writer.RecordVerifications(["a/x", "b/y"], "human:ada");

        Skip.IfNot(swapped, "junctions unavailable");
        Assert.Equal(
            "Error: 'b/y' resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed. — already written: a/x",
            outcome.Message);
        Assert.Equal([K("a/x.md")], outcome.Report.Written);
        Assert.Equal(K("b/y.md"), Assert.Single(outcome.Report.Failed).Key);
        Assert.Empty(Directory.EnumerateFileSystemEntries(external.Path));
        Assert.Equal(Doc, File.ReadAllText(Path.Combine(tmp.Path, "b-moved", "y.md")));
    }

    /// <summary>
    /// A write notification that throws after the commit (external review, F2, F6) never turns the
    /// committed concept into a failure: the reviewer's case — one concept, the notification
    /// always failing — and a batch whose second notification of three fails both report every
    /// key written, once, nothing failed, and <see cref="VerificationOutcome.Recorded"/> true,
    /// whether the notification threw an I/O error or a conflict of another operation.
    /// </summary>
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(3, 2, false)]
    [InlineData(1, 1, true)]
    [InlineData(3, 2, true)]
    public void A_notification_failing_after_a_commit_is_written_in_the_report_never_failed(int concepts, int failing, bool conflict)
    {
        using var tmp = new TempDir();
        string[] ids = [.. Enumerable.Range(0, concepts).Select(i => $"c{i}")];
        foreach (var id in ids)
        {
            tmp.Write(id + ".md", Doc);
        }

        Exception failure = conflict ? new BundleWriteConflictException(K("elsewhere.md")) : new IOException("notification failed");
        var notifications = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            if (++notifications == failing)
            {
                throw failure;
            }
        });

        var outcome = writer.RecordVerifications(ids, "human:ada", "2026-10-19T00:00:00Z");

        Assert.True(outcome.Recorded);
        Assert.Equal([.. ids.Select(id => K(id + ".md"))], outcome.Report.Written);
        Assert.Empty(outcome.Report.Failed);
        Assert.True(outcome.Report.Succeeded);
        Assert.Equal(concepts, notifications);
        Assert.EndsWith("; the write notification failed: " + failure.Message, outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A notification that fails on every write (F6) does not hide the real conflict of the next
    /// concept: the first is written, the second — a conflict on every attempt — is the report's
    /// <c>Failed</c> entry with the writer's reason, nothing else.
    /// </summary>
    [Fact]
    public void A_failing_notification_leaves_the_next_concepts_real_conflict_in_the_report()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc);
        for (var nth = 2; nth <= 4; nth++)
        {
            storage.FailWrite(nth, new BundleWriteConflictException(K("b.md")));
        }

        var notifications = 0;
        var writer = new BundleConceptWriter(storage, onWriteCommitted: () =>
        {
            notifications++;
            throw new BundleWriteConflictException(K("elsewhere.md"));
        })
        {
            Sleep = _ => { },
        };

        var outcome = writer.RecordVerifications(["a", "b"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal([K("a.md")], outcome.Report.Written);
        var failed = Assert.Single(outcome.Report.Failed);
        Assert.Equal(K("b.md"), failed.Key);
        Assert.Equal("'b' kept changing while it was being written; the write was refused after 3 attempts.", failed.Reason);
        Assert.Equal(1, notifications);
    }
}
