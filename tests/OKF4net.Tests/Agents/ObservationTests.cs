// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Agents.Internal;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Agents;

/// <summary>
/// The observation registry and the write it conditions (spec §4, §5.4; D1, D5): a session
/// rewrites only what <c>okf_read_concept</c> showed it in full, at the version it was shown,
/// and creates only what that tool showed absent; a context injection, a host's direct call
/// of the public method, <c>okf_get_computation</c> and an evicted entry authorise nothing.
/// The exposed functions are reached through their internal wrappers
/// (<see cref="OkfBundleTools.ReadConceptTool"/>, <see cref="OkfBundleTools.WriteConceptTool"/>,
/// <see cref="OkfBundleTools.GetComputationTool"/>), which <see cref="OkfBundleTools.GetTools()"/>
/// binds, so the tests say exactly which boundary records what.
/// </summary>
public class ObservationTests
{
    private const string Fm = "type: Note\ntitle: A\ndescription: a.\n";
    private const string Doc = "---\n" + Fm + "---\n\nA.\n";
    private const string Refusal = "Error: 'notes/a' has not been read by this session; read it first with okf_read_concept (a context block injected for you, or okf_get_computation, does not count), then write it.";

    private static BundleKey K(string s) => BundleKey.Parse(s);

    private static (InMemoryBundleProvider Storage, OkfBundleTools Tools) ToolsOver(params (string Key, string Text)[] objects)
    {
        var storage = new InMemoryBundleProvider(canWrite: true);
        foreach (var (key, text) in objects)
        {
            storage.Add(key, text);
        }

        return (storage, new OkfBundleTools(new BundleHandle(storage)));
    }

    [Fact]
    public void The_registry_keeps_three_purposes_and_evicts_the_least_recently_used()
    {
        var registry = new ObservationRegistry(capacity: 2);
        var v1 = new BundleObjectVersion("1");
        var v2 = new BundleObjectVersion("2");

        registry.RecordFullConcept(K("a.md"), v1);
        registry.RecordCode(K("a.md"), v2);
        Assert.True(registry.TryGetFullConcept(K("a.md"), out var full) && full == v1);
        Assert.True(registry.TryGetCode(K("a.md"), out var code) && code == v2);
        Assert.False(registry.IsAbsent(K("a.md")));

        registry.RecordAbsent(K("a.md")); // clears both
        Assert.True(registry.IsAbsent(K("a.md")));
        Assert.False(registry.TryGetFullConcept(K("a.md"), out _));
        Assert.False(registry.TryGetCode(K("a.md"), out _));
        registry.RecordFullConcept(K("a.md"), v2); // clears absent
        Assert.False(registry.IsAbsent(K("a.md")));

        registry.RecordFullConcept(K("b.md"), v1);
        Assert.True(registry.TryGetFullConcept(K("a.md"), out _)); // a is now the most recent
        registry.RecordFullConcept(K("c.md"), v1); // evicts b, the least recently used
        Assert.Equal(2, registry.Count);
        Assert.False(registry.TryGetFullConcept(K("b.md"), out _));
        Assert.True(registry.TryGetFullConcept(K("a.md"), out _));

        registry.Forget(K("a.md"));
        Assert.False(registry.TryGetFullConcept(K("a.md"), out _));
        Assert.Equal(1, registry.Count);
        Assert.Equal(ObservationRegistry.DefaultCapacity, new ObservationRegistry().Capacity);
        Assert.Equal(10_000, ObservationRegistry.DefaultCapacity);
    }

    [Fact]
    public void A_rewrite_needs_a_full_read_and_a_committed_write_consumes_it()
    {
        var (storage, tools) = ToolsOver(("notes/a.md", Doc));

        Assert.Equal(Refusal, tools.WriteConceptTool("notes/a", Fm, "Changed.\n"));
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));

        Assert.Contains("A.", tools.ReadConceptTool("notes/a"), StringComparison.Ordinal);
        var written = tools.WriteConceptTool("notes/a", Fm, "Changed.\n");
        Assert.StartsWith("Written", written, StringComparison.Ordinal);
        Assert.Contains(storage.Calls, c => c == "write notes/a.md IfMatch");

        Assert.Equal(Refusal, tools.WriteConceptTool("notes/a", Fm, "Changed twice.\n"));
        Assert.Contains("Changed.", tools.ReadConceptTool("notes/a"), StringComparison.Ordinal);
        Assert.StartsWith("Written", tools.WriteConceptTool("notes/a", Fm, "Changed twice.\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_concept_changed_since_it_was_read_is_a_conflict_and_the_image_is_refreshed()
    {
        var (storage, tools) = ToolsOver(("notes/a.md", Doc));
        tools.ReadConceptTool("notes/a");
        storage.Add("notes/a.md", "---\n" + Fm + "---\n\nChanged elsewhere.\n");

        var refused = tools.WriteConceptTool("notes/a", Fm, "Mine.\n");

        Assert.Equal("Error: 'notes/a' changed since it was read, or no longer exists; the write was refused. Read it again before writing it.", refused);
        Assert.Contains("Changed elsewhere.", tools.ReadConceptTool("notes/a"), StringComparison.Ordinal); // the image was refreshed
        Assert.StartsWith("Written", tools.WriteConceptTool("notes/a", Fm, "Mine.\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_concept_shown_absent_can_be_created_and_a_deletion_seen_is_a_conflict_then_a_creation()
    {
        var (storage, tools) = ToolsOver(("notes/a.md", Doc));

        Assert.Equal(Refusal.Replace("notes/a", "notes/b", StringComparison.Ordinal), tools.WriteConceptTool("notes/b", Fm, "B.\n"));
        Assert.Contains("not found", tools.ReadConceptTool("notes/b"), StringComparison.Ordinal);
        Assert.StartsWith("Written", tools.WriteConceptTool("notes/b", Fm, "B.\n"), StringComparison.Ordinal);
        Assert.Contains(storage.Calls, c => c == "write notes/b.md MustNotExist");

        tools.ReadConceptTool("notes/a");
        storage.Delete(K("notes/a.md"), BundleWriteCondition.None);
        Assert.StartsWith("Error: 'notes/a' changed since it was read, or no longer exists", tools.WriteConceptTool("notes/a", Fm, "Again.\n"), StringComparison.Ordinal);
        Assert.Contains("not found", tools.ReadConceptTool("notes/a"), StringComparison.Ordinal);
        Assert.StartsWith("Written", tools.WriteConceptTool("notes/a", Fm, "Again.\n"), StringComparison.Ordinal);
        Assert.Contains(storage.Calls, c => c == "write notes/a.md MustNotExist");
    }

    [Fact]
    public void Neither_the_public_method_nor_get_computation_nor_a_context_injection_authorises_a_write()
    {
        var (_, tools) = ToolsOver(
            ("notes/a.md", Doc),
            ("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\nattester: { resource: att.py }\n---\n# Computation\n\n```sql\nSELECT 1\n```\n"),
            ("att.py", "def attest(**_):\n    return {}\n"));

        Assert.Contains("A.", tools.ReadConcept("notes/a"), StringComparison.Ordinal); // the public method, as a host calls it
        Assert.Equal(Refusal, tools.WriteConceptTool("notes/a", Fm, "Changed.\n"));
        Assert.Equal(0, tools.Observations.Count);

        Assert.Contains("SELECT 1", tools.GetComputationTool("c/rev"), StringComparison.Ordinal);
        Assert.True(tools.Observations.TryGetCode(K("c/rev.md"), out _)); // the exposed function recorded the code it showed
        Assert.False(tools.Observations.TryGetFullConcept(K("c/rev.md"), out _)); // ... and that is no full-concept observation
        Assert.Equal(Refusal.Replace("notes/a", "c/rev", StringComparison.Ordinal), tools.WriteConceptTool("c/rev", "type: Attested Computation\ntitle: R\ndescription: r.\n", "Rewritten.\n"));

        Assert.Equal(Refusal, tools.WriteConceptTool("notes/a", Fm, "Changed.\n"));
    }

    /// <summary>D5: a concept the context provider injects, whole or truncated, is not an observation.</summary>
    [Fact]
    public async Task A_context_injection_is_never_an_observation()
    {
        var (_, tools) = ToolsOver(("notes/a.md", Doc));
        var provider = new OkfContextProvider(tools, new OkfContextProviderOptions { TokenBudget = 100_000 });

        var context = await provider.ProvideForTest(InvokingContext("a"), CancellationToken.None);

        var injected = Assert.Single(context.Messages!).Text;
        Assert.Contains("A.", injected, StringComparison.Ordinal); // the whole concept was injected
        Assert.Equal(0, tools.Observations.Count);
        Assert.Equal(Refusal, tools.WriteConceptTool("notes/a", Fm, "Changed.\n"));
    }

    [Fact]
    public void Another_tool_sets_read_does_not_replace_this_sessions_observation()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("notes/a.md", Doc);
        using var handle = new BundleHandle(storage);
        var first = new OkfBundleTools(handle);
        var second = new OkfBundleTools(handle);

        first.ReadConceptTool("notes/a");
        storage.Add("notes/a.md", "---\n" + Fm + "---\n\nv2.\n");
        handle.Invalidate();
        Assert.Contains("v2.", second.ReadConceptTool("notes/a"), StringComparison.Ordinal);

        Assert.StartsWith("Error: 'notes/a' changed since it was read", first.WriteConceptTool("notes/a", Fm, "From first.\n"), StringComparison.Ordinal);
        Assert.StartsWith("Written", second.WriteConceptTool("notes/a", Fm, "From second.\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_observation_evicted_by_ten_thousand_later_reads_authorises_nothing()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("n/c0.md", Doc);
        for (var i = 1; i <= ObservationRegistry.DefaultCapacity; i++)
        {
            storage.Add($"n/c{i}.md", Doc);
        }

        var tools = new OkfBundleTools(new BundleHandle(storage));
        tools.ReadConceptTool("n/c0");
        for (var i = 1; i <= ObservationRegistry.DefaultCapacity; i++)
        {
            tools.ReadConceptTool($"n/c{i}");
        }

        Assert.Equal(ObservationRegistry.DefaultCapacity, tools.Observations.Count);
        Assert.StartsWith("Error: 'n/c0' has not been read by this session", tools.WriteConceptTool("n/c0", Fm, "Changed.\n"), StringComparison.Ordinal);
        tools.ReadConceptTool("n/c0");
        Assert.StartsWith("Written", tools.WriteConceptTool("n/c0", Fm, "Changed.\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// An object that exists but is no concept is never shown absent: that made the write a
    /// MustNotExist the storage refused, and the re-read said "not found" again, for ever.
    /// It is shown as read, at the image's version, so the repair is an IfMatch rewrite.
    /// </summary>
    [Fact]
    public void A_file_that_exists_but_is_no_concept_can_be_repaired_and_is_never_shown_absent()
    {
        var (storage, tools) = ToolsOver(("notes/broken.md", "---\ntype: Note\ntitle: B\n\nno closing fence\n"));

        var shown = tools.ReadConceptTool("notes/broken");

        Assert.StartsWith("Concept 'notes/broken' exists but could not be read as a concept: ", shown, StringComparison.Ordinal);
        Assert.EndsWith("It can be rewritten with okf_write_concept.", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", shown, StringComparison.Ordinal);
        Assert.False(tools.Observations.IsAbsent(K("notes/broken.md")));
        Assert.True(tools.Observations.TryGetFullConcept(K("notes/broken.md"), out _));

        var written = tools.WriteConceptTool("notes/broken", Fm, "Repaired.\n");

        Assert.StartsWith("Written", written, StringComparison.Ordinal);
        Assert.Contains(storage.Calls, c => c == "write notes/broken.md IfMatch");
        Assert.DoesNotContain(storage.Calls, c => c == "write notes/broken.md MustNotExist");
        Assert.Contains("Repaired.", tools.ReadConceptTool("notes/broken"), StringComparison.Ordinal); // the loop is closed: now a concept
    }

    [Fact]
    public void An_oversized_object_exists_without_a_version_and_nothing_authorises_its_rewrite()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("notes/big.md", "---\n" + Fm + "---\n\n" + new string('x', 600) + "\n");
        var tools = new OkfBundleTools(new BundleHandle(storage, new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 300 } }));

        var shown = tools.ReadConceptTool("notes/big");

        Assert.StartsWith("Concept 'notes/big' exists but could not be read as a concept: ", shown, StringComparison.Ordinal);
        Assert.EndsWith("It cannot be rewritten through this tool.", shown, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", shown, StringComparison.Ordinal);
        Assert.Equal(0, tools.Observations.Count); // not absent, not read: nothing recorded
        Assert.Equal(Refusal.Replace("notes/a", "notes/big", StringComparison.Ordinal), tools.WriteConceptTool("notes/big", Fm, "Changed.\n"));
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_bundle_index_is_not_a_concept_and_is_not_shown_absent()
    {
        var (storage, tools) = ToolsOver(("notes/a.md", Doc), ("notes/index.md", "# Notes\n"));

        Assert.Equal("'notes/index' is not a concept: that name is a bundle index or log file.", tools.ReadConceptTool("notes/index"));

        Assert.Equal(0, tools.Observations.Count);
        Assert.Equal(Refusal.Replace("notes/a", "notes/index", StringComparison.Ordinal), tools.WriteConceptTool("notes/index", Fm, "Changed.\n"));
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
    }

    /// <summary>
    /// Nothing changes for a host: the public method keeps the text it always had for an object
    /// that exists but is no concept (an unparseable file, an oversized one, an index), the plain
    /// "not found"; the honest texts and the observations belong to the exposed function only.
    /// </summary>
    [Fact]
    public void The_public_method_keeps_its_not_found_text_for_an_object_that_is_no_concept()
    {
        var storage = new InMemoryBundleProvider(canWrite: true)
            .Add("notes/broken.md", "---\ntype: Note\ntitle: B\n\nno closing fence\n")
            .Add("notes/big.md", "---\n" + Fm + "---\n\n" + new string('x', 600) + "\n")
            .Add("notes/index.md", "# Notes\n");
        var tools = new OkfBundleTools(new BundleHandle(storage, new BundleLoadOptions { Limits = new BundleLimits { MaxObjectBytes = 300 } }));

        foreach (var id in new[] { "notes/broken", "notes/big", "notes/index" })
        {
            Assert.Equal($"Concept '{id}' not found. Use okf_browse to list available concepts.", tools.ReadConcept(id));
        }

        Assert.Equal(0, tools.Observations.Count); // the public method records nothing
        Assert.StartsWith("Concept 'notes/broken' exists but could not be read as a concept: ", tools.ReadConceptTool("notes/broken"), StringComparison.Ordinal);
        Assert.Contains("It cannot be rewritten through this tool.", tools.ReadConceptTool("notes/big"), StringComparison.Ordinal);
        Assert.Equal("'notes/index' is not a concept: that name is a bundle index or log file.", tools.ReadConceptTool("notes/index"));
        Assert.Equal("Concept 'notes/broken' not found. Use okf_browse to list available concepts.", tools.ReadConcept("notes/broken"));
        Assert.Equal(1, tools.Observations.Count); // only the exposed function recorded (notes/broken, with its version)
    }

    /// <summary>The observation survives a write the writer refused: nothing was written, and IfMatch still guards the retry.</summary>
    [Fact]
    public void A_write_that_answers_an_error_keeps_the_observation()
    {
        var (storage, tools) = ToolsOver(("notes/a.md", Doc));
        tools.ReadConceptTool("notes/a");

        var refused = tools.WriteConceptTool("notes/a", "type: Note\ntitle: A\n", "Changed.\n"); // no description: refused before any write

        Assert.StartsWith("Error:", refused, StringComparison.Ordinal);
        Assert.DoesNotContain(storage.Calls, c => c.StartsWith("write ", StringComparison.Ordinal));
        Assert.True(tools.Observations.TryGetFullConcept(K("notes/a.md"), out _));
        Assert.StartsWith("Written", tools.WriteConceptTool("notes/a", Fm, "Changed.\n"), StringComparison.Ordinal); // no new read needed
    }

    /// <summary>A smoke test of the lock, deterministic in what it asserts: the bound holds and nothing throws.</summary>
    [Fact]
    public void The_registry_stays_bounded_under_concurrent_records()
    {
        var registry = new ObservationRegistry(capacity: 100);
        var version = new BundleObjectVersion("1");

        Parallel.For(0, 4000, i =>
        {
            registry.RecordFullConcept(K($"n/c{i % 500}.md"), version);
            registry.TryGetFullConcept(K($"n/c{(i * 7) % 500}.md"), out _);
            registry.IsAbsent(K($"n/c{(i * 3) % 500}.md"));
        });

        Assert.Equal(100, registry.Count);
    }

    [Fact]
    public void The_exposed_functions_are_the_recording_ones_and_keep_their_descriptions()
    {
        var (_, tools) = ToolsOver(("notes/a.md", Doc));
        var functions = tools.GetTools().Cast<AIFunction>().ToDictionary(f => f.Name, StringComparer.Ordinal);

        Assert.Equal(nameof(OkfBundleTools.ReadConceptTool), functions["okf_read_concept"].UnderlyingMethod!.Name);
        Assert.Equal(nameof(OkfBundleTools.WriteConceptTool), functions["okf_write_concept"].UnderlyingMethod!.Name);
        Assert.Equal(nameof(OkfBundleTools.GetComputationTool), functions["okf_get_computation"].UnderlyingMethod!.Name);
        Assert.Equal(AIFunctionFactory.Create(tools.ReadConcept, "okf_read_concept").JsonSchema.GetRawText(), functions["okf_read_concept"].JsonSchema.GetRawText());
        Assert.Equal(AIFunctionFactory.Create(tools.WriteConcept, "okf_write_concept").JsonSchema.GetRawText(), functions["okf_write_concept"].JsonSchema.GetRawText());
        Assert.Equal(AIFunctionFactory.Create(tools.GetComputation, "okf_get_computation").Description, functions["okf_get_computation"].Description);
    }

    private static AIContextProvider.InvokingContext InvokingContext(string userMessageText)
    {
        var agent = new ScriptedChatClient([]).AsAIAgent();
        var aiContext = new AIContext { Messages = [new ChatMessage(ChatRole.User, userMessageText)] };
#pragma warning disable MAAI001 // InvokingContext's public ctor is marked [Experimental].
        return new AIContextProvider.InvokingContext(agent, session: null, aiContext);
#pragma warning restore MAAI001
    }
}
