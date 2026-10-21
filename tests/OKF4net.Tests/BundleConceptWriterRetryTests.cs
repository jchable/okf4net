// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;
using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// Optimistic concurrency in <see cref="BundleConceptWriter"/> (spec §4, decisions B4, B5): a write
/// conditioned by its caller is never retried, a read-modify-write is retried on a conflict at most
/// three times with a growing random delay, re-reading each time and calling <c>buildBody</c> once
/// per attempt. The conflicts come from a writer outside the writer's lock, scripted through the
/// in-memory provider, or real through two provider instances over one directory.
/// </summary>
public class BundleConceptWriterRetryTests
{
    private const string Fm = "type: Metric\ntitle: DAU\ndescription: Daily active users.\n";
    private const string Doc = "---\ntype: Metric\ntitle: DAU\ndescription: Daily active users.\n---\n\nbody\n";
    private const string Changed = "---\ntype: Metric\ntitle: DAU\ndescription: Daily active users.\nowner: elsewhere\n---\n\nchanged elsewhere\n";

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static string Text(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    private static BundleObjectVersion VersionOf(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return obj.Version;
    }

    private static (BundleConceptWriter Writer, List<TimeSpan> Delays) WriterOver(IBundleProvider p)
    {
        var delays = new List<TimeSpan>();
        var writer = new BundleConceptWriter(p) { Sleep = delays.Add, UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };
        return (writer, delays);
    }

    /// <summary>What a write returns when its notification raised <see cref="WriterNotifyingWithAConflict"/>'s conflict: the notification's own text (B46).</summary>
    private const string NotificationConflict = "Error: 'elsewhere.md' changed since it was read; the write was refused";

    /// <summary>
    /// A writer whose write notification raises a conflict of another operation — on its first
    /// call only, or on every call — after the write it follows is committed (external review,
    /// F6), with the number of its calls.
    /// </summary>
    private static (BundleConceptWriter Writer, List<TimeSpan> Delays, Func<int> Notifications) WriterNotifyingWithAConflict(IBundleProvider p, bool always)
    {
        var delays = new List<TimeSpan>();
        var notifications = 0;
        var writer = new BundleConceptWriter(p, onWriteCommitted: () =>
        {
            if (++notifications == 1 || always)
            {
                throw new BundleWriteConflictException(K("elsewhere.md"));
            }
        })
        {
            Sleep = delays.Add,
            UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc),
        };
        return (writer, delays, () => notifications);
    }

    [Fact]
    public void A_write_conditioned_on_the_version_read_lands_only_on_that_version()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, _) = WriterOver(storage);
        var v1 = VersionOf(storage, "metrics/dau.md");

        Assert.StartsWith("Written metrics/dau (updated,", writer.WriteConcept("metrics/dau", Fm, "second\n", BundleWriteCondition.IfMatch(v1)), StringComparison.Ordinal);
        Assert.Equal(
            "Error: 'metrics/dau' changed since it was read, or no longer exists; the write was refused. Read it again before writing it.",
            writer.WriteConcept("metrics/dau", Fm, "third\n", BundleWriteCondition.IfMatch(v1)));
        Assert.EndsWith("second\n", Text(storage, "metrics/dau.md"), StringComparison.Ordinal);

        Assert.Equal(
            "Error: 'metrics/new' changed since it was read, or no longer exists; the write was refused. Read it again before writing it.",
            writer.WriteConcept("metrics/new", Fm, "x\n", BundleWriteCondition.IfMatch(v1)));
        Assert.False(storage.Exists(K("metrics/new.md")));
    }

    [Fact]
    public void A_creation_conditioned_on_absence_refuses_an_existing_concept()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, _) = WriterOver(storage);
        var frontmatter = Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm));

        Assert.Equal(
            "Error: 'metrics/dau' already exists; the write was refused. Read it before writing it.",
            writer.WriteConcept("metrics/dau", frontmatter, "x\n", BundleWriteCondition.MustNotExist));
        Assert.Equal(Doc, Text(storage, "metrics/dau.md"));
        Assert.StartsWith("Written metrics/rev (new,", writer.WriteConcept("metrics/rev", frontmatter, "x\n", BundleWriteCondition.MustNotExist), StringComparison.Ordinal);
    }

    /// <summary>A caller's condition is never retried: retrying would overwrite what the caller has not seen (spec §4).</summary>
    [Fact]
    public void A_write_conditioned_by_its_caller_is_never_retried()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays) = WriterOver(storage);
        var v1 = VersionOf(storage, "metrics/dau.md");
        storage.BeforeNextWrite(p => p.Add("metrics/dau.md", Changed));
        storage.ClearCalls();

        Assert.StartsWith("Error: 'metrics/dau' changed since it was read", writer.WriteConcept("metrics/dau", Fm, "mine\n", BundleWriteCondition.IfMatch(v1)), StringComparison.Ordinal);

        Assert.Single(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
        Assert.Empty(delays);
        Assert.Equal(Changed, Text(storage, "metrics/dau.md"));
    }

    [Fact]
    public void An_append_that_meets_a_change_reads_again_and_builds_on_it()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays) = WriterOver(storage);
        storage.BeforeNextWrite(p => p.Add("metrics/dau.md", Changed));
        var seen = new List<string?>();

        var result = writer.AppendToConceptAtomic("metrics/dau", Fm, body =>
        {
            seen.Add(body);
            return body + "\nappended\n";
        });

        Assert.StartsWith("Written metrics/dau (updated,", result, StringComparison.Ordinal);
        Assert.Equal(["body", "changed elsewhere"], seen);
        var after = OkfDocument.Parse(Text(storage, "metrics/dau.md"));
        Assert.Equal("elsewhere", after.Frontmatter.Get("owner")!.AsDisplayString());
        Assert.Equal("changed elsewhere\nappended", after.Body.TrimEnd('\n'));
        var delay = Assert.Single(delays);
        Assert.InRange(delay.TotalMilliseconds, 10, 40);
    }

    [Fact]
    public void A_creation_that_meets_a_concept_created_meanwhile_reads_it_and_builds_on_it()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var (writer, _) = WriterOver(storage);
        storage.BeforeNextWrite(p => p.Add("metrics/dau.md", Doc));
        var seen = new List<string?>();

        var result = writer.AppendToConceptAtomic("metrics/dau", Fm, body =>
        {
            seen.Add(body);
            return (body ?? "first") + "\nmine\n";
        });

        Assert.StartsWith("Written metrics/dau (updated,", result, StringComparison.Ordinal);
        Assert.Equal([null, "body"], seen);
        Assert.Equal(["read metrics/dau.md", "write metrics/dau.md MustNotExist", "read metrics/dau.md", "write metrics/dau.md IfMatch"], storage.Calls);
    }

    [Fact]
    public void After_three_conflicting_attempts_the_append_gives_up_with_an_error_and_writes_nothing()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays) = WriterOver(storage);
        var calls = 0;

        var result = writer.AppendToConceptAtomic("metrics/dau", Fm, body =>
        {
            calls++;
            storage.ConflictOnNextWrite();
            return body + "\nappended\n";
        });

        Assert.Equal("Error: 'metrics/dau' kept changing while it was being written; the write was refused after 3 attempts.", result);
        Assert.Equal(3, calls);
        Assert.Equal(Doc, Text(storage, "metrics/dau.md"));
        Assert.Equal(2, delays.Count);
        Assert.InRange(delays[0].TotalMilliseconds, 10, 40);
        Assert.InRange(delays[1].TotalMilliseconds, 30, 120);
    }

    [Fact]
    public void The_retry_delays_are_random_and_grow()
    {
        var first = Enumerable.Range(0, 200).Select(_ => BundleConceptWriter.RetryDelay(1).TotalMilliseconds).ToList();
        var second = Enumerable.Range(0, 200).Select(_ => BundleConceptWriter.RetryDelay(2).TotalMilliseconds).ToList();
        Assert.All(first, d => Assert.InRange(d, 10, 40));
        Assert.All(second, d => Assert.InRange(d, 30, 120));
        Assert.True(first.Distinct().Count() > 1 && second.Distinct().Count() > 1);
        Assert.True(second.Average() > first.Average());
    }

    [Fact]
    public void A_verification_that_meets_a_change_stamps_the_new_content()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays) = WriterOver(storage);
        storage.BeforeNextWrite(p => p.Add("metrics/dau.md", Changed));

        var outcome = writer.RecordVerifications(["metrics/dau"], "human:ada");

        Assert.True(outcome.Recorded, outcome.Message);
        var after = OkfDocument.Parse(Text(storage, "metrics/dau.md"));
        Assert.Equal("elsewhere", after.Frontmatter.Get("owner")!.AsDisplayString());
        Assert.Equal("human:ada", Assert.Single(after.Frontmatter.Verified).By!.Value.Raw);
        Assert.Single(delays);
    }

    [Fact]
    public void A_verification_batch_whose_second_concept_keeps_changing_reports_the_first_as_written()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc);
        var (writer, _) = WriterOver(storage);
        for (var nth = 2; nth <= 4; nth++)
        {
            storage.FailWrite(nth, new BundleWriteConflictException(K("b.md")));
        }

        var outcome = writer.RecordVerifications(["a", "b"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: 'b' kept changing while it was being written; the write was refused after 3 attempts. — already written: a", outcome.Message);
        Assert.Equal(["a"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal(Doc, Text(storage, "b.md"));
    }

    /// <summary>
    /// A conflict the write notification raises once the write is committed (external review, F6)
    /// is not a refusal of the write's own condition (B48): a caller's <c>IfMatch</c> write, through
    /// either overload, lands once and notifies once, and returns the notification's text — never
    /// "changed since it was read". Once or on every call, the same.
    /// </summary>
    [Theory]
    [InlineData(false, "yaml")]
    [InlineData(true, "yaml")]
    [InlineData(false, "frontmatter")]
    [InlineData(true, "frontmatter")]
    public void A_conflict_raised_by_the_notification_of_a_conditioned_write_is_not_its_refusal(bool always, string overload)
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays, notifications) = WriterNotifyingWithAConflict(storage, always);
        var condition = BundleWriteCondition.IfMatch(VersionOf(storage, "metrics/dau.md"));
        storage.ClearCalls();

        var result = overload == "yaml"
            ? writer.WriteConcept("metrics/dau", Fm, "mine\n", condition)
            : writer.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "mine\n", condition);

        Assert.Equal(NotificationConflict, result);
        Assert.Equal(["write metrics/dau.md IfMatch"], storage.Calls.Where(c => c.StartsWith("write ", StringComparison.Ordinal)));
        Assert.Equal(1, notifications());
        Assert.Empty(delays);
        Assert.EndsWith("\nmine\n", Text(storage, "metrics/dau.md"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same for a read-modify-write (F6): the reviewer's append, its notification raising a
    /// conflict once or every time, is committed once — <c>buildBody</c> ran once, the body holds
    /// one appended copy, nothing was retried, the notification ran once.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_conflict_raised_by_the_notification_of_an_append_neither_retries_nor_appends_again(bool always)
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var (writer, delays, notifications) = WriterNotifyingWithAConflict(storage, always);
        storage.ClearCalls();
        var builds = 0;

        var result = writer.AppendToConceptAtomic("metrics/dau", Fm, body =>
        {
            builds++;
            return body + "\nADDED\n";
        });

        Assert.Equal(NotificationConflict, result);
        Assert.Equal(1, builds);
        Assert.Equal(1, notifications());
        Assert.Empty(delays);
        Assert.Equal(["read metrics/dau.md", "write metrics/dau.md IfMatch"], storage.Calls);
        Assert.Equal(2, Text(storage, "metrics/dau.md").Split("ADDED").Length);
    }

    /// <summary>
    /// And for a verification batch (F6): a notification raising a conflict, once or every time,
    /// prepares and writes nothing again — one write and one notification per concept, both
    /// concepts recorded, stamped once, and the message says the notification failed.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_conflict_raised_by_the_notification_of_a_verification_writes_nothing_again(bool always)
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc);
        var (writer, delays, notifications) = WriterNotifyingWithAConflict(storage, always);
        storage.ClearCalls();

        var outcome = writer.RecordVerifications(["a", "b"], "human:ada", "2026-10-19T00:00:00Z");

        Assert.True(outcome.Recorded, outcome.Message);
        Assert.Equal("Recorded 2 verification(s) by human:ada at 2026-10-19T00:00:00Z; the write notification failed: 'elsewhere.md' changed since it was read; the write was refused", outcome.Message);
        Assert.Equal(["a", "b"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal(2, notifications());
        Assert.Empty(delays);
        Assert.Equal(["write a.md IfMatch", "write b.md IfMatch"], storage.Calls.Where(c => c.StartsWith("write ", StringComparison.Ordinal)));
        Assert.All(["a.md", "b.md"], key => Assert.Single(OkfDocument.Parse(Text(storage, key)).Frontmatter.Verified));
    }

    /// <summary>
    /// A notification that fails does not hide a real conflict of the next concept (F6): the first
    /// concept is written and notified once, the second meets a conflict on every attempt and is
    /// reported as that concept's conflict, after its three attempts.
    /// </summary>
    [Fact]
    public void A_failing_notification_leaves_the_real_conflict_of_the_next_concept_reported_as_such()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("a.md", Doc).Add("b.md", Doc);
        var (writer, delays, notifications) = WriterNotifyingWithAConflict(storage, always: true);
        for (var nth = 2; nth <= 4; nth++)
        {
            storage.FailWrite(nth, new BundleWriteConflictException(K("b.md")));
        }

        var outcome = writer.RecordVerifications(["a", "b"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Equal("Error: 'b' kept changing while it was being written; the write was refused after 3 attempts. — already written: a", outcome.Message);
        Assert.Equal(["a"], outcome.Records.Select(r => r.ConceptId));
        Assert.Equal(1, notifications());
        Assert.Equal(2, delays.Count);
        Assert.Single(OkfDocument.Parse(Text(storage, "a.md")).Frontmatter.Verified);
        Assert.Equal(Doc, Text(storage, "b.md"));
    }

    /// <summary>
    /// Two writers append to one concept through two provider instances whose ids differ, so the
    /// writers do not share a lock: only the provider's conditional commit stands between them.
    /// Each round both read the same version before either writes (a barrier in <c>buildBody</c>'s
    /// first call), so exactly one meets a conflict, reads again and retries — fifty rounds, and
    /// no append is ever lost.
    /// </summary>
    [Fact]
    public void Two_writers_without_a_shared_lock_lose_no_append_over_fifty_rounds()
    {
        using var tmp = new TempDir();
        var writers = new[] { "alias-1", "alias-2" }
            .Select(id => new BundleConceptWriter(new IdAlias(new FileSystemBundleProvider(tmp.Path), id)) { Sleep = _ => { } })
            .ToArray();
        Assert.NotSame(writers[0].CurrentLockObjectForTest(), writers[1].CurrentLockObjectForTest());

        for (var round = 0; round < 50; round++)
        {
            using var bothRead = new Barrier(2);
            var builds = new int[2];
            var results = new string?[2];
            var threads = Enumerable.Range(0, 2).Select(i => new Thread(() =>
            {
                results[i] = writers[i].AppendToConceptAtomic("notes/shared", Fm, body =>
                {
                    if (++builds[i] == 1)
                    {
                        bothRead.SignalAndWait(TimeSpan.FromSeconds(10));
                    }

                    return (body ?? string.Empty) + $"[{round}.{i}]";
                });
            })).ToArray();
            foreach (var t in threads)
            {
                t.Start();
            }

            foreach (var t in threads)
            {
                Assert.True(t.Join(TimeSpan.FromSeconds(30)), "a writer did not finish");
            }

            Assert.All(results, r => Assert.StartsWith("Written notes/shared", r, StringComparison.Ordinal));
            Assert.Equal(3, builds.Sum()); // one writer built once, the other twice
            var body = OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "notes", "shared.md"))).Body;
            Assert.Contains($"[{round}.0]", body, StringComparison.Ordinal);
            Assert.Contains($"[{round}.1]", body, StringComparison.Ordinal);
        }

        Assert.Empty(new FileSystemBundleProvider(tmp.Path).ListOrphanedTemporaries(BundleLimits.Default));
    }

    /// <summary>A provider seen under another <see cref="IBundleProvider.Id"/>: same storage, another writer lock (B11).</summary>
    private sealed class IdAlias(IBundleProvider inner, string id) : IBundleProvider
    {
        public string Id => id;

        public string Location => inner.Location;

        public BundleProviderCapabilities Capabilities => inner.Capabilities;

        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        public BundleVersion GetVersion(BundleLimits limits) => inner.GetVersion(limits);

        public IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits) => inner.Enumerate(prefix, limits);

        public BundleObject Read(BundleKey key, BundleObjectVersion? expected = null) => inner.Read(key, expected);

        public bool Exists(BundleKey key) => inner.Exists(key);

        public BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => inner.Write(key, content, condition);

        public void Delete(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);
    }
}
