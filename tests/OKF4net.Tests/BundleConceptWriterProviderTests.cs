// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers;
using OKF4net.Tests.Providers;
using OKF4net.Yaml;

namespace OKF4net.Tests;

/// <summary>
/// <see cref="BundleConceptWriter"/> over a provider (spec §3, §9; decisions B2, B3, B10, B11,
/// B15, B16): the capability guard at every write entry point, keys validated by the core under
/// the load's policy, the tree check before a creation, the lock keyed by provider, and the
/// provider's refusals turned into the writer's own messages. The in-memory provider records
/// every call, so the tests can pin what the writer asks the storage, in which order.
/// </summary>
public class BundleConceptWriterProviderTests
{
    private const string Fm = "type: Metric\ntitle: DAU\ndescription: Daily active users.\n";
    private const string Doc = "---\ntype: Metric\ntitle: DAU\ndescription: Daily active users.\n---\n\nbody\n";

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static string Text(IBundleProvider p, string key)
    {
        using var obj = p.Read(K(key));
        return new StreamReader(obj.Content, Encoding.UTF8).ReadToEnd();
    }

    private static string[] Tree(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>Every write entry point of the core, by name (B3): the writer's methods, and the index generator's provider overloads, called on the writer's provider.</summary>
    private static readonly Dictionary<string, Action<BundleConceptWriter, IBundleProvider>> WriteCalls = new()
    {
        ["WriteConcept(yaml)"] = (w, _) => w.WriteConcept("metrics/dau", Fm, "body\n"),
        ["WriteConcept(frontmatter)"] = (w, _) => w.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n"),
        ["AppendToConceptAtomic"] = (w, _) => w.AppendToConceptAtomic("metrics/dau", Fm, b => (b ?? string.Empty) + "more\n"),
        ["RecordVerifications"] = (w, _) => w.RecordVerifications(["metrics/dau"], "human:ada"),
        ["WriteConcept(yaml, condition)"] = (w, _) => w.WriteConcept("metrics/dau", Fm, "body\n", BundleWriteCondition.MustNotExist),
        ["WriteConcept(frontmatter, condition)"] = (w, _) => w.WriteConcept("metrics/dau", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n", BundleWriteCondition.None),
        ["AppendLog"] = (w, _) => w.AppendLog("Update", "entry"),
        ["IndexGenerator.RegenerateIndexes(provider)"] = (_, p) => IndexGenerator.RegenerateIndexes(p),
        ["IndexGenerator.RegenerateIndexesWith(provider)"] = (_, p) => IndexGenerator.RegenerateIndexesWith(p, IndexGenerator.DefaultSynthesize),
    };

    /// <summary>
    /// The same entry points called with an argument the writer would refuse first (an empty id,
    /// an empty batch), by name: on a read-only provider the capability guard must still win.
    /// </summary>
    private static readonly Dictionary<string, Action<BundleConceptWriter, IBundleProvider>> InvalidWriteCalls = new()
    {
        ["WriteConcept(yaml)"] = (w, _) => w.WriteConcept("", Fm, "body\n"),
        ["WriteConcept(frontmatter)"] = (w, _) => w.WriteConcept("", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n"),
        ["AppendToConceptAtomic"] = (w, _) => w.AppendToConceptAtomic("", Fm, b => (b ?? string.Empty) + "more\n"),
        ["RecordVerifications"] = (w, _) => w.RecordVerifications([], "human:ada"),
        ["WriteConcept(yaml, condition)"] = (w, _) => w.WriteConcept("", Fm, "body\n", BundleWriteCondition.MustNotExist),
        ["WriteConcept(frontmatter, condition)"] = (w, _) => w.WriteConcept("", Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(Fm)), "body\n", BundleWriteCondition.None),
        ["AppendLog"] = (w, _) => w.AppendLog("", "entry"),
        // RegenerateIndexes takes no argument the generator could refuse apart from the provider
        // itself (a null one throws before any capability check, so it cannot be a row here): the
        // call is the valid one, which on an empty writable provider writes nothing.
        ["IndexGenerator.RegenerateIndexes(provider)"] = (_, p) => IndexGenerator.RegenerateIndexes(p),
        // A null synthesizer is checked after the capability guard: read-only wins, writable throws.
        ["IndexGenerator.RegenerateIndexesWith(provider)"] = (_, p) => IndexGenerator.RegenerateIndexesWith(p, null!),
    };

    /// <summary>The names of <see cref="WriteCalls"/>, for the matrix theories.</summary>
    public static TheoryData<string> WriteEntryPoints => new(WriteCalls.Keys);

    /// <summary>
    /// The matrix covers every write entry point: the public instance methods of the writer that
    /// return <c>string</c> or <see cref="VerificationOutcome"/> (read-only
    /// <c>CheckVerificationTargets</c> returns neither) are exactly the matrix's, overloads
    /// included, so a new write entry point cannot be added without the matrix noticing (B3).
    /// </summary>
    [Fact]
    public void The_write_matrix_lists_every_public_write_method_of_the_writer()
    {
        // The guard covers public instance methods returning string or VerificationOutcome: a write
        // method with another return type would escape it.
        var reflected = typeof(BundleConceptWriter)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && (m.ReturnType == typeof(string) || m.ReturnType == typeof(VerificationOutcome)))
            .GroupBy(m => m.Name)
            .ToDictionary(g => g.Key, g => g.Count());
        var matrix = WriteCalls.Keys
            .Where(k => !k.StartsWith("IndexGenerator.", StringComparison.Ordinal))
            .GroupBy(k => k.Split('(')[0])
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(matrix.OrderBy(p => p.Key), reflected.OrderBy(p => p.Key));
        Assert.Equal(WriteCalls.Keys.Order(), InvalidWriteCalls.Keys.Order());
    }

    /// <summary>
    /// The matrix covers every provider overload of the index generator too: the public static
    /// methods of <see cref="IndexGenerator"/> that take an <see cref="IBundleProvider"/> (the
    /// <c>string</c> overloads take none) are exactly the <c>IndexGenerator.</c> rows of both
    /// tables, so a deleted or an extra row fails here (the writer's reflection check above
    /// leaves those rows out).
    /// </summary>
    [Fact]
    public void The_write_matrix_lists_every_provider_overload_of_the_index_generator()
    {
        var reflected = typeof(IndexGenerator)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && m.GetParameters().Any(p => p.ParameterType == typeof(IBundleProvider)))
            .Select(m => $"IndexGenerator.{m.Name}(provider)")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(reflected);
        Assert.Equal(reflected, WriteCalls.Keys.Where(k => k.StartsWith("IndexGenerator.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        Assert.Equal(reflected, InvalidWriteCalls.Keys.Where(k => k.StartsWith("IndexGenerator.", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    /// <summary>B3: the capability guard runs before the arguments are looked at — an invalid argument on a read-only provider is still a <see cref="BundleReadOnlyException"/>, never an <c>Error:</c> text.</summary>
    [Theory]
    [MemberData(nameof(WriteEntryPoints))]
    public void The_capability_guard_runs_before_the_arguments_are_checked(string entryPoint)
    {
        var storage = new InMemoryBundleProvider().Add("metrics/dau.md", Doc);
        Assert.Throws<BundleReadOnlyException>(() => InvalidWriteCalls[entryPoint](new BundleConceptWriter(storage), storage));
        Assert.Empty(storage.Calls);

        using var tmp = new TempDir();
        var readOnlyFiles = new FileSystemBundleProvider(tmp.Path).AsReadOnly();
        Assert.Throws<BundleReadOnlyException>(() => InvalidWriteCalls[entryPoint](new BundleConceptWriter(readOnlyFiles), readOnlyFiles));

        // And on a writable provider the same call is an Error: text, so the guard is what the rows above pin.
        var writable = new InMemoryBundleProvider(canWrite: true);
        var writer = new BundleConceptWriter(writable);
        if (entryPoint == "IndexGenerator.RegenerateIndexesWith(provider)")
        {
            // The generator's argument checks throw, as they do for any caller: the order is what the rows above pin.
            Assert.Throws<ArgumentNullException>(() => InvalidWriteCalls[entryPoint](writer, writable));
        }
        else
        {
            InvalidWriteCalls[entryPoint](writer, writable);
        }

        Assert.DoesNotContain(writable.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
    }

    /// <summary>
    /// B3, spec §11 "Lecture seule": on a read-only view of a filesystem bundle, every write
    /// entry point throws <see cref="BundleReadOnlyException"/> — never an <c>Error:</c> string —
    /// and nothing on disk changes: no directory, no temporary, no byte. Even an invalid argument
    /// is not looked at first.
    /// </summary>
    [Theory]
    [MemberData(nameof(WriteEntryPoints))]
    public void Every_write_entry_point_on_a_read_only_filesystem_view_throws_and_changes_nothing(string entryPoint)
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Doc);
        var before = Tree(tmp.Path);
        var bytes = File.ReadAllBytes(Path.Combine(tmp.Path, "metrics", "dau.md"));
        var provider = new FileSystemBundleProvider(tmp.Path).AsReadOnly();
        var writer = new BundleConceptWriter(provider);

        var ex = Assert.Throws<BundleReadOnlyException>(() => WriteCalls[entryPoint](writer, provider));

        Assert.DoesNotContain(tmp.Path, ex.Message);
        Assert.Equal(before, Tree(tmp.Path));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(tmp.Path, "metrics", "dau.md")));
    }

    /// <summary>The same matrix on a read-only in-memory provider: the guard runs before the writer asks the storage anything.</summary>
    [Theory]
    [MemberData(nameof(WriteEntryPoints))]
    public void Every_write_entry_point_on_a_read_only_provider_throws_before_any_call(string entryPoint)
    {
        var storage = new InMemoryBundleProvider().Add("metrics/dau.md", Doc);
        var writer = new BundleConceptWriter(storage);

        Assert.Throws<BundleReadOnlyException>(() => WriteCalls[entryPoint](writer, storage));
        Assert.Empty(storage.Calls);
    }

    [Fact]
    public void Checking_verification_targets_reads_and_is_allowed_on_a_read_only_provider()
    {
        var storage = new InMemoryBundleProvider().Add("metrics/dau.md", Doc);
        Assert.Null(new BundleConceptWriter(storage).CheckVerificationTargets(["metrics/dau"]));
        Assert.Equal(["exists metrics/dau.md", "read metrics/dau.md"], storage.Calls);
    }

    [Fact]
    public void A_write_lands_in_the_provider_under_the_concepts_key()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var fired = 0;
        var writer = new BundleConceptWriter(storage, onWriteCommitted: () => fired++);

        var result = writer.WriteConcept("metrics/dau", Fm, "body\n");

        Assert.Equal($"Written metrics/dau (new, {Encoding.UTF8.GetByteCount(Doc)} bytes). Remember to run okf_regenerate_indexes.", result);
        Assert.Equal(Doc, Text(storage, "metrics/dau.md"));
        Assert.Equal(1, fired);
        Assert.Equal(["exists metrics/dau.md", "write metrics/dau.md None", "read metrics/dau.md"], storage.Calls);
    }

    [Fact]
    public void A_read_modify_write_is_conditioned_on_what_it_read()
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        var writer = new BundleConceptWriter(storage);

        Assert.StartsWith("Written metrics/dau (new,", writer.AppendToConceptAtomic("metrics/dau", Fm, b => "first\n"), StringComparison.Ordinal);
        Assert.StartsWith("Written metrics/dau (updated,", writer.AppendToConceptAtomic("metrics/dau", Fm, b => b + "\nsecond\n"), StringComparison.Ordinal);
        Assert.True(writer.RecordVerifications(["metrics/dau"], "human:ada").Recorded);

        Assert.Equal(
            [
                "read metrics/dau.md", "write metrics/dau.md MustNotExist",
                "read metrics/dau.md", "write metrics/dau.md IfMatch",
                "exists metrics/dau.md", "read metrics/dau.md", "read metrics/dau.md", "write metrics/dau.md IfMatch",
            ],
            storage.Calls);
    }

    /// <summary>
    /// B2: the key is checked under the provider's native constraints before any I/O. A Windows
    /// filesystem strips a trailing dot, so <c>a./b</c> used to land in <c>a/b</c> (external
    /// review, 2026-10-09); a provider that declares the rule now refuses it with the validator's
    /// reason, whatever the host OS.
    /// </summary>
    [Fact]
    public void A_concept_id_the_providers_native_rules_refuse_is_refused_before_any_call()
    {
        var storage = new InMemoryBundleProvider(BundleKeyConstraints.TrailingDotOrSpace, canWrite: true);
        var result = new BundleConceptWriter(storage).WriteConcept("a./b", Fm, "body\n");
        Assert.Equal("Error: 'a./b' cannot be written under the bundle's key policy: segment \"a.\" ends with '.' or a space.", result);
        Assert.Empty(storage.Calls);
    }

    [SkippableFact]
    public void On_Windows_a_concept_id_segment_ending_with_a_dot_is_refused_instead_of_aliasing()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "the native Windows set refuses a trailing dot; elsewhere 'a.' is stored as written");
        using var tmp = new TempDir();
        var result = new BundleConceptWriter(tmp.Path).WriteConcept("a./b", Fm, "body\n");
        Assert.StartsWith("Error: 'a./b' cannot be written under the bundle's key policy:", result, StringComparison.Ordinal);
        Assert.Empty(Tree(tmp.Path));
    }

    [Fact]
    public void Under_the_strict_policy_a_creation_beside_a_case_twin_is_refused_and_an_update_is_not_checked()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("A/x.md", Doc);
        var writer = new BundleConceptWriter(storage, options: new BundleLoadOptions { KeyPolicy = BundleKeyPolicy.Strict });

        var refused = writer.WriteConcept("a/y", Fm, "body\n");

        Assert.Equal("Error: \"a/y.md\" cannot be created: \"A\" already exists and differs from \"a\" only in case or Unicode form.", refused);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));

        storage.ClearCalls();
        Assert.StartsWith("Written A/x (updated,", writer.WriteConcept("A/x", Fm, "new body\n"), StringComparison.Ordinal);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("enumerate", StringComparison.Ordinal));
    }

    /// <summary>B10 on the filesystem: <c>x.md</c> is a concept, so the concept <c>x.md/y</c> would need it to be a directory too.</summary>
    [Fact]
    public void A_creation_under_an_existing_concept_file_is_refused_and_names_it()
    {
        using var tmp = new TempDir();
        tmp.Write("x.md", Doc);
        var before = Tree(tmp.Path);

        var result = new BundleConceptWriter(tmp.Path).WriteConcept("x.md/y", Fm, "body\n");

        Assert.Equal("Error: \"x.md/y.md\" cannot be created: \"x.md\" is an object, so it cannot also be a prefix of other keys.", result);
        Assert.Equal(before, Tree(tmp.Path));
    }

    [Fact]
    public void Reads_are_bounded_by_the_writers_options()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var writer = new BundleConceptWriter(storage, options: new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 10 } });

        var result = writer.AppendToConceptAtomic("metrics/dau", Fm, b => b + "more\n");

        Assert.StartsWith("Error: 'metrics/dau.md' is larger than the 10-byte bound", result, StringComparison.Ordinal);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_bundle_root_is_the_filesystem_root_and_has_no_meaning_elsewhere()
    {
        using var tmp = new TempDir();
        Assert.Equal(tmp.Path, new BundleConceptWriter(tmp.Path).BundleRoot);
        Assert.Equal(tmp.Path, new BundleConceptWriter(new FileSystemBundleProvider(tmp.Path)).BundleRoot);
        Assert.Throws<NotSupportedException>(() => new BundleConceptWriter(new InMemoryBundleProvider()).BundleRoot);
    }

    /// <summary>B11: the filesystem keeps its lexical-and-resolved registry, whichever constructor built the writer; any other provider is keyed by its Id.</summary>
    [Fact]
    public void The_lock_is_keyed_by_the_root_on_the_filesystem_and_by_the_id_elsewhere()
    {
        using var tmp = new TempDir();
        Assert.Same(
            new BundleConceptWriter(tmp.Path).CurrentLockObjectForTest(),
            new BundleConceptWriter(new FileSystemBundleProvider(tmp.Path)).CurrentLockObjectForTest());

        var storage = new InMemoryBundleProvider(id: "lot-b-lock");
        Assert.Same(new BundleConceptWriter(storage).CurrentLockObjectForTest(), new BundleConceptWriter(storage).CurrentLockObjectForTest());
        Assert.Same(
            new BundleConceptWriter(storage).CurrentLockObjectForTest(),
            new BundleConceptWriter(new InMemoryBundleProvider(id: "lot-b-lock")).CurrentLockObjectForTest());
        Assert.NotSame(
            new BundleConceptWriter(storage).CurrentLockObjectForTest(),
            new BundleConceptWriter(new InMemoryBundleProvider(id: "lot-b-other")).CurrentLockObjectForTest());
    }

    /// <summary>
    /// The late-guard test hook keeps its meaning over the filesystem provider: it fires inside
    /// the provider's write, once the parent directory exists (created by the write) and before
    /// the object or its temporary does, which is where the writer's own late re-check used to be.
    /// </summary>
    [Fact]
    public void The_late_guard_hook_fires_once_the_parent_exists_and_before_anything_is_written()
    {
        using var tmp = new TempDir();
        var writer = new BundleConceptWriter(tmp.Path);
        var seen = new List<string[]>();
        writer.BeforeLateReparseCheckForTest = () => seen.Add(Tree(tmp.Path));

        Assert.StartsWith("Written new/dir/x (new,", writer.WriteConcept("new/dir/x", Fm, "body\n"), StringComparison.Ordinal);
        Assert.Equal([["new", "new/dir"]], seen);

        writer.BeforeLateReparseCheckForTest = null;
        writer.WriteConcept("new/dir/y", Fm, "body\n");
        Assert.Single(seen);
    }

    /// <summary>What a failing write notification throws, by kind: an I/O error, an <see cref="OkfException"/>, a conflict raised by another operation (external review, F6).</summary>
    private static Exception NotificationFailure(string kind) => kind switch
    {
        "io" => new IOException("notification failed"),
        "okf" => new DocumentParseException("notification failed"),
        _ => new BundleWriteConflictException(K("elsewhere.md")),
    };

    /// <summary>
    /// A write notification (<c>onWriteCommitted</c>) that throws once the provider has committed
    /// (external review, F2, F6): the string methods return what they returned before the
    /// provider, <c>Error: {message}</c>, but the write is on disk, made once — <c>buildBody</c>
    /// ran once, the notification ran once — whatever the writer-converted exception, a conflict
    /// included (B48).
    /// </summary>
    [Theory]
    [InlineData("io")]
    [InlineData("okf")]
    [InlineData("conflict")]
    public void A_notification_failing_after_the_commit_keeps_the_error_text_and_the_single_write(string kind)
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "metrics", "dau.md");
        var failure = NotificationFailure(kind);
        var notifications = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            notifications++;
            throw failure;
        });

        Assert.Equal("Error: " + failure.Message, writer.WriteConcept("metrics/dau", Fm, "body\n"));
        Assert.EndsWith("\nbody\n", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal(1, notifications);

        var builds = 0;
        Assert.Equal("Error: " + failure.Message, writer.AppendToConceptAtomic("metrics/dau", Fm, b =>
        {
            builds++;
            return b + " more";
        }));
        Assert.Equal(1, builds);
        Assert.Equal(2, notifications);
        Assert.EndsWith("\nbody more\n", File.ReadAllText(file), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same in a verification batch, where a record is kept: a concept whose notification
    /// failed is on disk, so it is in <see cref="VerificationOutcome.Records"/> and the batch goes
    /// on; <see cref="VerificationOutcome.Recorded"/> stays true once every stamp is on disk, and
    /// the message says the notification failed. The reviewer's case (one concept, the
    /// notification always failing) and a batch whose second notification of three fails, for
    /// each kind of converted exception.
    /// </summary>
    [Theory]
    [InlineData(1, 1, "io")]
    [InlineData(3, 2, "io")]
    [InlineData(1, 1, "okf")]
    [InlineData(3, 2, "okf")]
    [InlineData(1, 1, "conflict")]
    [InlineData(3, 2, "conflict")]
    public void A_notification_failing_in_a_verification_batch_leaves_every_stamp_recorded_and_says_so(int concepts, int failing, string kind)
    {
        using var tmp = new TempDir();
        string[] ids = [.. Enumerable.Range(0, concepts).Select(i => $"c{i}")];
        foreach (var id in ids)
        {
            tmp.Write(id + ".md", Doc);
        }

        var failure = NotificationFailure(kind);
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
        Assert.Equal(ids, outcome.Records.Select(r => r.ConceptId));
        Assert.Equal($"Recorded {concepts} verification(s) by human:ada at 2026-10-19T00:00:00Z; the write notification failed: {failure.Message}", outcome.Message);
        Assert.Equal(concepts, notifications);
        Assert.All(ids, id => Assert.Single(OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, id + ".md"))).Frontmatter.Verified));
    }

    /// <summary>
    /// A cancellation the notification raises is not converted (B48): it propagates to the caller
    /// unchanged once the write is on disk, made once — nothing is retried, nothing written again —
    /// and it ends a verification batch there, the concepts after it not attempted.
    /// </summary>
    [Fact]
    public void A_notification_cancelled_after_the_commit_propagates_and_the_write_stays()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Doc);
        tmp.Write("b.md", Doc);
        var file = Path.Combine(tmp.Path, "metrics", "dau.md");
        var notifications = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            notifications++;
            throw new OperationCanceledException("cancelled");
        });

        Assert.Equal("cancelled", Assert.Throws<OperationCanceledException>(() => writer.WriteConcept("metrics/dau", Fm, "body\n")).Message);
        Assert.EndsWith("\nbody\n", File.ReadAllText(file), StringComparison.Ordinal);

        var builds = 0;
        Assert.Throws<OperationCanceledException>(() => writer.AppendToConceptAtomic("metrics/dau", Fm, b =>
        {
            builds++;
            return b + " more";
        }));
        Assert.Equal(1, builds);
        Assert.EndsWith("\nbody more\n", File.ReadAllText(file), StringComparison.Ordinal);

        Assert.Throws<OperationCanceledException>(() => writer.RecordVerifications(["a", "b"], "human:ada", "2026-10-19T00:00:00Z"));
        Assert.Equal(3, notifications);
        Assert.Single(OkfDocument.Parse(File.ReadAllText(Path.Combine(tmp.Path, "a.md"))).Frontmatter.Verified);
        Assert.Equal(Doc, File.ReadAllText(Path.Combine(tmp.Path, "b.md")));
    }

    /// <summary>
    /// B46: a notification that throws a provider exception the write path would translate
    /// (a refused path, a name the storage cannot hold) is reported as <c>Error: {its message}</c>,
    /// byte for byte — the write committed, so the "resolves through a reparse point" or "cannot be
    /// stored" texts would describe a failure that did not happen. The write is on disk, made once.
    /// </summary>
    [Theory]
    [InlineData("refused")]
    [InlineData("unavailable")]
    public void A_notification_failing_with_a_provider_refusal_reports_its_own_message_and_the_write_stays(string kind)
    {
        using var tmp = new TempDir();
        var file = Path.Combine(tmp.Path, "metrics", "dau.md");
        Exception failure = kind == "refused"
            ? new BundleAccessRefusedException(K("metrics/dau.md"), "a link")
            : new BundleWriteConflictException(K("metrics/dau.md"), BundleWriteConflictReason.NameUnavailable);
        var notifications = 0;
        var writer = new BundleConceptWriter(tmp.Path, onWriteCommitted: () =>
        {
            notifications++;
            throw failure;
        });

        Assert.Equal("Error: " + failure.Message, writer.WriteConcept("metrics/dau", Fm, "body\n"));
        Assert.EndsWith("\nbody\n", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal("Error: " + failure.Message, writer.AppendToConceptAtomic("metrics/dau", Fm, b => b + " more"));
        Assert.EndsWith("\nbody more\n", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal(2, notifications);

        var outcome = writer.RecordVerifications(["metrics/dau"], "human:ada", "2026-10-19T00:00:00Z");
        Assert.True(outcome.Recorded);
        Assert.Equal($"Recorded 1 verification(s) by human:ada at 2026-10-19T00:00:00Z; the write notification failed: {failure.Message}", outcome.Message);
        Assert.Single(OkfDocument.Parse(File.ReadAllText(file)).Frontmatter.Verified);
    }

    /// <summary>
    /// A provider that cannot hold the exact name (a case twin on a case-folding volume, a
    /// directory of that name) raises <see cref="BundleWriteConflictReason.NameUnavailable"/>: the
    /// writer says so in its own words, naming the concept and no storage detail.
    /// </summary>
    [Fact]
    public void A_name_the_storage_cannot_hold_is_reported_in_the_writers_own_words()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).FailWrite(1, new BundleWriteConflictException(K("a/x.md"), BundleWriteConflictReason.NameUnavailable));
        var fired = 0;
        var writer = new BundleConceptWriter(storage, onWriteCommitted: () => fired++);

        var result = writer.WriteConcept("a/x", Fm, "body\n");

        Assert.Equal("Error: 'a/x' cannot be stored under that name: the bundle holds an entry it would confuse with it (a directory, or a name differing only in case or Unicode form), or cannot hold the name.", result);
        Assert.Equal(0, fired);
        Assert.False(storage.Exists(K("a/x.md")));
    }

    /// <summary>The same for a path the provider refuses (a link, an entry that cannot be inspected): the writer's reparse-point text, whatever the host OS.</summary>
    [Fact]
    public void A_path_the_provider_refuses_at_the_write_is_reported_in_the_writers_own_words()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).FailWrite(1, new BundleAccessRefusedException(K("a/x.md"), "a link"));

        var result = new BundleConceptWriter(storage).WriteConcept("a/x", Fm, "body\n");

        Assert.Equal("Error: 'a/x' resolves through a reparse point (symlink/junction), or an entry that could not be inspected, inside the bundle, which is not allowed.", result);
        Assert.False(storage.Exists(K("a/x.md")));
    }

    /// <summary>
    /// The verification pre-check reads within the writer's own bounds, not the defaults: a
    /// concept over <c>MaxObjectBytes</c> is refused with the bound's message, nothing is written.
    /// </summary>
    [Fact]
    public void Verification_targets_are_read_within_the_writers_bounds()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("metrics/dau.md", Doc);
        var writer = new BundleConceptWriter(storage, options: new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 10 } });

        var outcome = writer.RecordVerifications(["metrics/dau"], "human:ada", "2026-10-19T00:00:00Z");

        Assert.False(outcome.Recorded);
        Assert.Contains("is larger than the 10-byte bound", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(outcome.Records);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
        Assert.NotNull(writer.CheckVerificationTargets(["metrics/dau"]));
    }

    /// <summary>
    /// D1: a write needs the right to create files in the target's directory (its temporary is
    /// created beside it and renamed over it). Where the process may write the object but not
    /// create files beside it (directory 0555, object 0666; Unix, unprivileged), the writer
    /// reports the raw access error as the <c>Error:</c> text, which names the temporary under
    /// the host path — a raw I/O text is not path-free (issue #211) — and the object keeps its
    /// content. Skipped as root and on Windows.
    /// </summary>
    [SkippableFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void A_concept_in_a_directory_the_process_may_not_create_files_in_is_reported_with_the_raw_access_error()
    {
        Skip.If(OperatingSystem.IsWindows(), "POSIX modes");
        using var tmp = new TempDir();
        var file = tmp.Write("t/orders.md", Doc);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
        var dir = Path.Combine(tmp.Path, "t");
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        try
        {
            var privileged = true;
            try
            {
                using (new FileStream(Path.Combine(dir, "probe"), FileMode.CreateNew))
                {
                }

                File.Delete(Path.Combine(dir, "probe"));
            }
            catch (UnauthorizedAccessException)
            {
                privileged = false;
            }

            Skip.If(privileged, "the process creates files in any directory anyway (root)");
            var writer = new BundleConceptWriter(tmp.Path);
            var written = writer.WriteConcept("t/orders", Fm, "changed\n");
            var appended = writer.AppendToConceptAtomic("t/orders", Fm, b => (b ?? string.Empty) + "more\n");

            foreach (var result in new[] { written, appended })
            {
                Assert.StartsWith("Error: Access to the path '", result, StringComparison.Ordinal);
                Assert.Contains(Path.Combine(tmp.Path, "t", FileSystemBundleProvider.TemporaryPrefix), result, StringComparison.Ordinal);
                Assert.EndsWith("' is denied.", result, StringComparison.Ordinal);
            }

            Assert.Equal(Doc, File.ReadAllText(file));
            Assert.Equal(["t", "t/orders.md"], Tree(tmp.Path));
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
