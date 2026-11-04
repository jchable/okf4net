// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests;

/// <summary>
/// #152 step 3: the asynchronous writer and index generator. The retry waits differ by mode
/// (the synchronous spelling sleeps and never throws under a conflict; the asynchronous one
/// delays cancellably), and a cancellation is honoured before a commit and between objects only:
/// after a first commit the operation says exactly what landed. Every ordering is forced by the
/// provider, never by a delay.
/// </summary>
public class BundleConceptWriterAsyncTests
{
    private const string Fm = "type: Metric\ntitle: DAU\ndescription: Daily active users.\n";
    private const string Doc = "---\ntype: Metric\ntitle: DAU\ndescription: Daily active users.\n---\n\nbody\n";

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static string Text(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    /// <summary>Delegates to an inner provider and cancels a token once its Nth write has committed.</summary>
    internal sealed class CancelAfterWrites(InMemoryBundleProvider inner, CancellationTokenSource cts, int writes) : SyncTestProvider
    {
        private int _committed;

        public override string Id => inner.Id;

        public override string Location => inner.Location;

        public override BundleProviderCapabilities Capabilities => inner.Capabilities;

        public override BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        protected override BundleVersion GetVersionSync(BundleLimits limits) => inner.GetVersion(limits);

        protected override IEnumerable<BundleListing> EnumerateSync(BundleKey? prefix, BundleLimits limits) => inner.Enumerate(prefix, limits);

        protected override BundleObject ReadSync(BundleKey key, BundleObjectVersion? expected) => inner.Read(key, expected);

        protected override bool ExistsSync(BundleKey key) => inner.Exists(key);

        protected override BundleObjectVersion WriteSync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition)
        {
            var version = inner.Write(key, content, condition);
            if (++_committed == writes)
            {
                cts.Cancel();
            }

            return version;
        }

        protected override void DeleteSync(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);
    }

    [Fact]
    public void The_sync_writer_retries_under_a_real_conflict_instead_of_throwing()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("m.md", Doc);
        storage.ConflictOnNextWrite();
        var delays = new List<TimeSpan>();
        var writer = new BundleConceptWriter(storage) { Sleep = delays.Add };

        var result = writer.AppendToConceptAtomic("m", Fm, b => (b ?? string.Empty) + "more\n");

        Assert.DoesNotContain("Error", result, StringComparison.Ordinal);
        Assert.Single(delays); // one conflict, one wait, then the attempt that landed
    }

    [Fact]
    public async Task The_async_writer_retries_with_a_cancellable_delay_not_a_sleep()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("m.md", Doc);
        storage.ConflictOnNextWrite();
        var delays = new List<TimeSpan>();
        var slept = false;
        var writer = new BundleConceptWriter(storage)
        {
            Sleep = _ => slept = true,
            DelayAsync = (d, _) =>
            {
                delays.Add(d);
                return Task.CompletedTask;
            },
        };

        var result = await writer.AppendToConceptAtomicAsync("m", Fm, b => (b ?? string.Empty) + "more\n");

        Assert.DoesNotContain("Error", result, StringComparison.Ordinal);
        Assert.Single(delays);
        Assert.False(slept, "the asynchronous spelling must not block on Thread.Sleep");
    }

    [Fact]
    public async Task A_write_cancelled_before_its_commit_leaves_the_object_unchanged()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("m.md", Doc);
        var writer = new BundleConceptWriter(storage);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await writer.WriteConceptAsync("m", Fm, "changed\n", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await writer.AppendLogAsync("Update", "x", null, cts.Token));

        Assert.Equal(Doc, Text(storage, "m.md"));
        Assert.DoesNotContain("log.md", string.Join(",", storage.Calls.Where(c => c.StartsWith("Write", StringComparison.Ordinal))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancellation_after_the_commit_still_returns_the_result_and_notifies()
    {
        var inner = new InMemoryBundleProvider(canWrite: true);
        using var cts = new CancellationTokenSource();
        var storage = new CancelAfterWrites(inner, cts, writes: 1);
        var notified = 0;
        var writer = new BundleConceptWriter(storage, onWriteCommitted: () => notified++);

        var result = await writer.WriteConceptAsync("m", Fm, "body\n", cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.StartsWith("Written m", result, StringComparison.Ordinal);
        Assert.Equal(1, notified);
        Assert.Contains("body", Text(inner, "m.md"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_batch_cancelled_between_two_objects_reports_what_landed_and_what_was_not_attempted()
    {
        var inner = new InMemoryBundleProvider(canWrite: true).Add("p/a.md", Doc).Add("p/b.md", Doc).Add("p/c.md", Doc);
        using var cts = new CancellationTokenSource();
        var storage = new CancelAfterWrites(inner, cts, writes: 1);
        var notified = 0;
        var writer = new BundleConceptWriter(storage, onWriteCommitted: () => notified++);

        var outcome = await writer.RecordVerificationsAsync(["p/a", "p/b", "p/c"], "human:ada", null, cts.Token);

        Assert.False(outcome.Recorded);
        Assert.Equal(["p/a"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal([K("p/a.md")], outcome.Report.Written);
        Assert.Equal([K("p/b.md"), K("p/c.md")], outcome.Report.Failed.Select(f => f.Key));
        Assert.All(outcome.Report.Failed, f => Assert.Equal("not attempted (cancelled)", f.Reason));
        Assert.Equal(1, notified); // the stamp that landed is notified, so the handle's image is invalidated
        Assert.Contains("verified:", Text(inner, "p/a.md"), StringComparison.Ordinal);
        Assert.Equal(Doc, Text(inner, "p/b.md"));
        Assert.Equal(Doc, Text(inner, "p/c.md"));
    }

    [Fact]
    public async Task A_batch_cancelled_before_anything_is_written_is_an_ordinary_cancellation()
    {
        var inner = new InMemoryBundleProvider(canWrite: true).Add("p/a.md", Doc).Add("p/b.md", Doc);
        var writer = new BundleConceptWriter(inner);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await writer.RecordVerificationsAsync(["p/a", "p/b"], "human:ada", null, cts.Token));

        Assert.Equal(Doc, Text(inner, "p/a.md"));
        Assert.Equal(Doc, Text(inner, "p/b.md"));
    }

    [Fact]
    public async Task A_regeneration_cancelled_between_two_indexes_reports_the_rest_as_not_attempted()
    {
        var inner = new InMemoryBundleProvider(canWrite: true)
            .Add("a/x.md", Doc).Add("b/y.md", Doc).Add("c/z.md", Doc);
        using var cts = new CancellationTokenSource();
        var storage = new CancelAfterWrites(inner, cts, writes: 1);

        var report = await IndexGenerator.RegenerateIndexesAsync(storage, null, cts.Token);

        Assert.Single(report.Written);
        Assert.NotEmpty(report.Failed);
        Assert.All(report.Failed, f => Assert.Equal("not attempted (cancelled)", f.Reason));
    }

    [Fact]
    public async Task The_async_writer_agrees_with_the_sync_one_over_the_filesystem()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        foreach (var dir in new[] { a, b })
        {
            dir.Write("notes/seed.md", $"---\n{Fm}---\n\nseed\n");
        }

        var syncWriter = new BundleConceptWriter(a.Path) { UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };
        var asyncWriter = new BundleConceptWriter(b.Path) { UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };

        Assert.Equal(syncWriter.WriteConcept("notes/new", Fm, "body\n"), await asyncWriter.WriteConceptAsync("notes/new", Fm, "body\n"));
        Assert.Equal(
            syncWriter.AppendToConceptAtomic("notes/seed", Fm, x => (x ?? string.Empty) + "more\n"),
            await asyncWriter.AppendToConceptAtomicAsync("notes/seed", Fm, x => (x ?? string.Empty) + "more\n"));
        Assert.Equal(syncWriter.AppendLog("Update", "x", new DateOnly(2026, 8, 28)), await asyncWriter.AppendLogAsync("Update", "x", new DateOnly(2026, 8, 28)));
        var s = syncWriter.RecordVerifications(["notes/seed"], "human:ada", "2026-08-28T09:14:00Z");
        var t = await asyncWriter.RecordVerificationsAsync(["notes/seed"], "human:ada", "2026-08-28T09:14:00Z");
        Assert.Equal(s.Message, t.Message);
        Assert.Equal(
            IndexGenerator.RegenerateIndexes(a.Path).Select(p => Path.GetRelativePath(a.Path, p)),
            IndexGenerator.RegenerateIndexes(b.Path).Select(p => Path.GetRelativePath(b.Path, p)));

        foreach (var file in new[] { "notes/seed.md", "notes/new.md", "log.md", "index.md", "notes/index.md" })
        {
            Assert.Equal(File.ReadAllText(Path.Combine(a.Path, file)), File.ReadAllText(Path.Combine(b.Path, file)));
        }
    }
}
