// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Attestation;
using OKF4net.Catalog;
using OKF4net.Providers;
using OKF4net.Tests.Attestation;
using OKF4net.Viewer;

namespace OKF4net.Tests.Providers;

/// <summary>
/// #140: one bundle, loaded from a fixture into an <see cref="InMemoryBundleProvider"/> whose
/// <see cref="IBundleProvider.Location"/> is <c>memory://x</c> (not a path on any OS), is handed
/// to every public entry point that consumes a bundle, with the current directory set to an empty
/// folder. The entry point must work, must leave the empty folder empty, and a write must have
/// reached the provider.
///
/// <para><b>What this proves, and what it does not.</b> It is a runtime probe, not an analyzer.
/// A residual <c>System.IO</c> call that resolves the location string (<c>memory://x</c>,
/// invalid as a path on Windows, relative on Linux) or a relative path against the current
/// directory fails or leaves a trace here. It does NOT prove that no <c>System.IO</c> call exists:
/// an absolute path built from something other than the location, an I/O call on a branch this
/// bundle does not reach, or an exception swallowed by an entry point's own error handling would
/// all pass. Coverage is what the table lists; the reflection guard
/// (<see cref="Every_public_bundle_consuming_member_is_in_the_table_or_excluded_with_a_reason"/>)
/// keeps the table from silently falling behind the API, and it finds members by signature (a
/// parameter of type <see cref="Bundle"/>, <see cref="BundleHandle"/> or
/// <see cref="IBundleProvider"/>) and by being a public method of the writer, the tool set or the
/// knowledge source — a bundle consumer that takes none of those and is not one of those types is
/// invisible to it.</para>
/// </summary>
#pragma warning disable CS0618 // RunComputation is obsolete but still a public entry point: it must stay provider-only until removed
[Collection(CurrentDirectoryCollection.Name)]
public class NoResidualFileIoTests
{
    private const string Fixture = "okf_v02_computation";
    private const string Fm = "type: Note\ntitle: Probe\ndescription: A probe concept.";

    private sealed class Ctx
    {
        public required InMemoryBundleProvider Mem { get; init; }
        public required Bundle Bundle { get; init; }
        public required BundleHandle Handle { get; init; }
        public required AttestationOrchestrator Orchestrator { get; init; }
    }

    private static readonly IReadOnlyDictionary<string, Func<Ctx, Task>> Table = BuildTable();

    /// <summary>Members that take a bundle type but do no bundle I/O of their own, with the reason.</summary>
    private static readonly IReadOnlyDictionary<string, string> Excluded = new Dictionary<string, string>
    {
        ["AttestationCapture..ctor(ConceptId,Bundle,SanctionedComputation,String)"] = "a value type: it binds an already-captured text to the image and reads nothing",
        ["AttestationCapture.set_Image(Bundle)"] = "an init-only property of that value type",
        ["BundleHandle..ctor(IBundleProvider,BundleLoadOptions,BundleRefreshPolicy,TimeProvider)"] = "builds the handle every case here uses; it loads lazily, at Current",
        ["ReadOnlyBundleProvider..ctor(IBundleProvider)"] = "a decorator: covered through AsReadOnly, the public way to build it",
    };

    private static void Add(Dictionary<string, Func<Ctx, Task>> t, string key, Func<Ctx, Task> body) => t.Add(key, body);

    private static void Add(Dictionary<string, Func<Ctx, Task>> t, string key, Action<Ctx> body) =>
        t.Add(key, c =>
        {
            body(c);
            return Task.CompletedTask;
        });

    private static IReadOnlyDictionary<string, Func<Ctx, Task>> BuildTable()
    {
        var t = new Dictionary<string, Func<Ctx, Task>>(StringComparer.Ordinal);

        // ----- read side -----
        Add(t, "Bundle.LoadAsync(IBundleProvider,BundleLoadOptions,CancellationToken)", async c =>
        {
            var b = await Bundle.LoadAsync(c.Mem);
            Assert.Equal("memory://x", b.Location);
            Assert.True(b.Count > 0);
        });
        Add(t, "BundleValidator.Validate(Bundle,IOkfClock)", c => Assert.NotNull(BundleValidator.Validate(c.Bundle)));
        Add(t, "ConceptAudit.Run(Bundle,AuditQuery,IOkfClock)", c => Assert.NotNull(ConceptAudit.Run(c.Bundle)));
        Add(t, "SiteModel.Build(Bundle)", c => Assert.NotNull(SiteModel.Build(c.Bundle)));
        Add(t, "SiteIndex.Build(Bundle)", c => Assert.NotNull(SiteIndex.Build(c.Bundle)));
        Add(t, "BundleProviderExtensions.AsReadOnly(IBundleProvider)", c =>
        {
            var ro = c.Mem.AsReadOnly();
            Assert.False(ro.Capabilities.CanWrite);
            Assert.True(Bundle.Load(ro).Count > 0);
            Assert.Throws<BundleReadOnlyException>(() => new BundleConceptWriter(ro).WriteConcept("p/ro", Fm, "b\n"));
        });
        Add(t, "OkfBundleKnowledgeSource..ctor(String,BundleHandle)", async c =>
        {
            var result = await new OkfBundleKnowledgeSource("src", c.Handle).SearchAsync(new KnowledgeQuery("revenue"));
            Assert.Null(result.Diagnostic);
            Assert.NotEmpty(result.Passages);
        });
        Add(t, "OkfBundleKnowledgeSource.SearchAsync(KnowledgeQuery,CancellationToken)", async c =>
        {
            var result = await new OkfBundleKnowledgeSource("src", c.Handle).SearchAsync(new KnowledgeQuery("dau"), CancellationToken.None);
            Assert.Null(result.Diagnostic);
        });

        // ----- index generator -----
        Add(t, "IndexGenerator.RegenerateIndexes(IBundleProvider,BundleLoadOptions)", c =>
        {
            Assert.NotNull(IndexGenerator.RegenerateIndexes(c.Mem));
            Assert.Contains(c.Mem.Calls, call => call.StartsWith("enumerate", StringComparison.Ordinal));
        });
        Add(t, "IndexGenerator.RegenerateIndexesWith(IBundleProvider,Synthesize,BundleLoadOptions)", c =>
            Assert.NotNull(IndexGenerator.RegenerateIndexesWith(c.Mem, IndexGenerator.DefaultSynthesize)));

        // ----- the writer -----
        Add(t, "BundleConceptWriter..ctor(BundleHandle,Action)", c => Written(c, new BundleConceptWriter(c.Handle).WriteConcept("p/h", Fm, "b\n"), "p/h"));
        Add(t, "BundleConceptWriter..ctor(IBundleProvider,Action,BundleLoadOptions)", c => Written(c, new BundleConceptWriter(c.Mem).WriteConcept("p/pr", Fm, "b\n"), "p/pr"));
        Add(t, "BundleConceptWriter.WriteConcept(String,String,String)", c => Written(c, Writer(c).WriteConcept("p/w1", Fm, "b\n"), "p/w1"));
        Add(t, "BundleConceptWriter.WriteConcept(String,String,String,BundleWriteCondition)", c =>
            Written(c, Writer(c).WriteConcept("p/w2", Fm, "b\n", BundleWriteCondition.MustNotExist), "p/w2"));
        Add(t, "BundleConceptWriter.WriteConcept(String,Frontmatter,String)", c =>
            Written(c, Writer(c).WriteConcept("p/w3", Doc().Frontmatter, "b\n"), "p/w3"));
        Add(t, "BundleConceptWriter.WriteConcept(String,Frontmatter,String,BundleWriteCondition)", c =>
            Written(c, Writer(c).WriteConcept("p/w4", Doc().Frontmatter, "b\n", BundleWriteCondition.MustNotExist), "p/w4"));
        Add(t, "BundleConceptWriter.AppendToConceptAtomic(String,String,Func`2)", c =>
            Written(c, Writer(c).AppendToConceptAtomic("p/a", Fm, existing => (existing ?? string.Empty) + "line\n"), "p/a"));
        Add(t, "BundleConceptWriter.RecordVerifications(IReadOnlyList`1,String,String)", c =>
        {
            _ = Writer(c).RecordVerifications(["metrics/revenue"], "human:ada");
            Assert.Contains(c.Mem.Calls, call => call.StartsWith("write metrics/revenue.md", StringComparison.Ordinal));
        });
        Add(t, "BundleConceptWriter.AppendLog(String,String,Nullable`1)", c =>
        {
            Assert.StartsWith("Appended", Writer(c).AppendLog("Update", "probe"), StringComparison.Ordinal);
            Assert.Contains(c.Mem.Calls, call => call.StartsWith("write log.md", StringComparison.Ordinal));
        });

        // ----- the tool set (public methods) -----
        Add(t, "OkfBundleTools..ctor(BundleHandle,AttestationOrchestrator)", c => Assert.NotNull(new OkfBundleTools(c.Handle).Handle));
        Add(t, "OkfBundleTools.ReadConcept(String)", c => Assert.Contains("Revenue", Tools(c).ReadConcept("computations/revenue"), StringComparison.Ordinal));
        Add(t, "OkfBundleTools.Browse(String)", c => NotError(Tools(c).Browse()));
        Add(t, "OkfBundleTools.Graph(String)", c => NotError(Tools(c).Graph()));
        Add(t, "OkfBundleTools.Search(String,String)", c => NotError(Tools(c).Search("revenue")));
        Add(t, "OkfBundleTools.Audit(Nullable`1,String,String,String)", c => NotError(Tools(c).Audit()));
        Add(t, "OkfBundleTools.WriteConcept(String,String,String)", c => Written(c, Tools(c).WriteConcept("p/t", Fm, "b\n"), "p/t"));
        Add(t, "OkfBundleTools.Verify(String,String,String)", c => NotError(Tools(c).Verify("metrics/revenue", "human:ada")));
        Add(t, "OkfBundleTools.AppendLog(String,String)", c => NotError(Tools(c).AppendLog("Update", "probe")));
        Add(t, "OkfBundleTools.RegenerateIndexes()", c => NotError(Tools(c).RegenerateIndexes()));
        Add(t, "OkfBundleTools.ValidateBundle()", c => NotError(Tools(c).ValidateBundle()));
        Add(t, "OkfBundleTools.ChangesSince(String)", c => NotError(Tools(c).ChangesSince("2020-01-01")));
        Add(t, "OkfBundleTools.GetComputation(String)", c => Assert.Contains("SELECT", Tools(c).GetComputation("computations/revenue"), StringComparison.Ordinal));

        // ----- attestation: the attester is read through the provider, by key -----
        Add(t, "OkfBundleTools.RunComputation(String,IReadOnlyDictionary`2)", c =>
            AssertRan(c, Tools(c).RunComputation("computations/revenue", Year)));
        Add(t, "OkfBundleTools.RunComputationAsync(String,IReadOnlyDictionary`2,CancellationToken)", async c =>
            AssertRan(c, await Tools(c).RunComputationAsync("computations/revenue", Year)));
        Add(t, "AttestationOrchestrator.RunAsync(Bundle,ConceptId,IReadOnlyDictionary`2,Nullable`1,CancellationToken)", async c =>
        {
            var outcome = await c.Orchestrator.RunAsync(c.Bundle, ConceptId.Parse("computations/revenue"), Year);
            Assert.True(outcome.Displayable, string.Join("; ", outcome.Reasons));
            AssertAttesterSourceRead(c);
        });
        Add(t, "AttestationOrchestrator.Capture(Bundle,Concept,Nullable`1)", c =>
        {
            var concept = c.Bundle.Concepts.Single(x => x.Id.ToString() == "computations/revenue");
            var captured = AttestationOrchestrator.Capture(c.Bundle, concept);
            Assert.NotNull(captured.Capture);
            AssertAttesterSourceRead(c);
        });
        Add(t, "AttestationOrchestrator.RunCapturedAsync(Bundle,ConceptId,IReadOnlyDictionary`2,AttestationCapture,Nullable`1,CancellationToken)", async c =>
        {
            var id = ConceptId.Parse("computations/revenue");
            var captured = AttestationOrchestrator.Capture(c.Bundle, c.Bundle.Concepts.Single(x => x.Id.ToString() == id.ToString()));
            var outcome = await c.Orchestrator.RunCapturedAsync(c.Bundle, id, Year, captured.Capture!);
            Assert.True(outcome.Displayable, string.Join("; ", outcome.Reasons));
        });

        // ----- the presentation boundary: every model-facing function, in the order the gates need -----
        Add(t, "OkfBundleTools.GetTools()", c => Assert.Equal(13, new OkfBundleTools(c.Handle, c.Orchestrator).GetTools().Count));
        Add(t, "OkfBundleTools.GetTools(OkfToolMode)", async c =>
        {
            var fns = new OkfBundleTools(c.Handle, c.Orchestrator).GetTools().Cast<AIFunction>().ToDictionary(f => f.Name, StringComparer.Ordinal);
            async Task<string> Call(string name, params (string Key, object? Value)[] args)
            {
                var a = new AIFunctionArguments();
                foreach (var (k, v) in args)
                {
                    a[k] = v;
                }

                return Convert.ToString(await fns[name].InvokeAsync(a), CultureInfo.InvariantCulture) ?? string.Empty;
            }

            NotError(await Call("okf_browse"));
            NotError(await Call("okf_graph"));
            NotError(await Call("okf_search", ("query", "revenue")));
            NotError(await Call("okf_audit"));
            NotError(await Call("okf_validate_bundle"));
            NotError(await Call("okf_changes_since", ("sinceDate", "2020-01-01")));
            NotError(await Call("okf_read_concept", ("conceptId", "computations/revenue")));
            NotError(await Call("okf_get_computation", ("conceptId", "computations/revenue")));
            NotError(await Call("okf_run_computation", ("conceptId", "computations/revenue"), ("parameterValues", Year)));
            AssertAttesterSourceRead(c);
            NotError(await Call("okf_verify", ("conceptIds", "metrics/revenue"), ("by", "human:ada")));
            NotError(await Call("okf_append_log", ("kind", "Update"), ("text", "probe")));
            _ = await Call("okf_read_concept", ("conceptId", "p/gt")); // an absent observation, which the write gate requires
            NotError(await Call("okf_write_concept", ("conceptId", "p/gt"), ("frontmatterYaml", Fm), ("body", "b\n")));
            NotError(await Call("okf_regenerate_indexes"));
            Assert.Contains(c.Mem.Calls, call => call.StartsWith("write p/gt.md", StringComparison.Ordinal));
        });

        return t;
    }

    private static IReadOnlyDictionary<string, object?> Year { get; } = new Dictionary<string, object?> { ["year"] = 2026 };

    private static OkfDocument Doc() => OkfDocument.Parse("---\ntype: Note\ntitle: Probe\ndescription: A probe concept.\n---\n\nbody\n");

    private static BundleConceptWriter Writer(Ctx c) => new(c.Handle);

    private static OkfBundleTools Tools(Ctx c) => new(c.Handle, c.Orchestrator);

    private static void NotError(string result) => Assert.False(result.StartsWith("Error", StringComparison.Ordinal), result);

    private static void Written(Ctx c, string result, string id)
    {
        Assert.StartsWith("Written", result, StringComparison.Ordinal);
        Assert.Contains(c.Mem.Calls, call => call.StartsWith($"write {id}.md", StringComparison.Ordinal));
    }

    private static void AssertRan(Ctx c, string result)
    {
        NotError(result);
        AssertAttesterSourceRead(c);
    }

    private static void AssertAttesterSourceRead(Ctx c) =>
        Assert.Contains(c.Mem.Calls, call => call.StartsWith("read references/attesters/revenue.py", StringComparison.Ordinal));

    private static Ctx NewContext()
    {
        var mem = new InMemoryBundleProvider(id: "x", canWrite: true);
        var root = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", Fixture);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            mem.Add(Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file));
        }

        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?>
        {
            ["job_id"] = "j1",
            ["executed_sql"] = "SELECT 1",
            ["result"] = 42,
        }));
        var orchestrator = new AttestationOrchestrator(new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime }));
        var ctx = new Ctx { Mem = mem, Bundle = Bundle.Load(mem), Handle = new BundleHandle(mem), Orchestrator = orchestrator };
        mem.ClearCalls();
        return ctx;
    }

    public static TheoryData<string> Entries()
    {
        var data = new TheoryData<string>();
        foreach (var key in Table.Keys)
        {
            data.Add(key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public async Task Entry_point_works_over_a_provider_that_is_not_a_directory(string entry)
    {
        var ctx = NewContext();
        var original = Environment.CurrentDirectory;
        using var empty = new TempDir();
        Environment.CurrentDirectory = empty.Path;
        try
        {
            await Table[entry](ctx);
        }
        finally
        {
            Environment.CurrentDirectory = original;
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(empty.Path));
    }

    [Fact]
    public void Every_public_bundle_consuming_member_is_in_the_table_or_excluded_with_a_reason()
    {
        var found = ConsumingMembers().ToHashSet(StringComparer.Ordinal);

        var missing = found.Except(Table.Keys).Except(Excluded.Keys).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "public bundle-consuming members absent from the table:\n" + string.Join('\n', missing));

        var stale = Table.Keys.Concat(Excluded.Keys).Where(k => !found.Contains(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "table or exclusion keys that name no public member:\n" + string.Join('\n', stale));
    }

    private static IEnumerable<string> ConsumingMembers()
    {
        var assemblies = new[]
        {
            typeof(Bundle).Assembly,
            typeof(OkfBundleTools).Assembly,
            typeof(AttestationOrchestrator).Assembly,
            typeof(OkfBundleKnowledgeSource).Assembly,
            typeof(SiteModel).Assembly,
        };
        var bundleTypes = new HashSet<Type> { typeof(Bundle), typeof(BundleHandle), typeof(IBundleProvider) };
        var wholeTypes = new HashSet<Type> { typeof(BundleConceptWriter), typeof(OkfBundleTools), typeof(OkfBundleKnowledgeSource) };
        const BindingFlags All = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var type in assemblies.SelectMany(a => a.GetExportedTypes()))
        {
            foreach (var m in type.GetMembers(All).OfType<MethodBase>())
            {
                if (m.IsSpecialName && !m.IsConstructor && !m.Name.StartsWith("set_", StringComparison.Ordinal))
                {
                    continue; // property getters and the like take no bundle
                }

                var ps = m.GetParameters();
                var takesBundle = ps.Any(p => bundleTypes.Contains(p.ParameterType));
                var wholeType = wholeTypes.Contains(type) && !m.IsSpecialName;
                if (takesBundle || wholeType)
                {
                    yield return $"{type.Name}.{m.Name}({string.Join(",", ps.Select(p => p.ParameterType.Name))})";
                }
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CurrentDirectoryCollection
{
    public const string Name = "CurrentDirectory";
}
