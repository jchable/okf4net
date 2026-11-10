// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // the obsolete synchronous tool methods are the subject (#152): kept one version, bridged over a provider that completes synchronously
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Attestation;
using OKF4net.Providers;
using OKF4net.Tests.Attestation;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Agents;

/// <summary>
/// <c>okf_run_computation</c> runs only what the session was shown (spec §5.4, D1): the
/// concept at the version the image holds (contract and runtime included), the computation
/// file at the version <c>okf_get_computation</c> showed, and the attester pinned to the image.
/// A refusal for a changed version or a negative resource state invalidates the image, so the
/// next attempt starts from a fresh survey under every refresh policy (spec §5.1), and the
/// session's observations survive that refresh.
/// </summary>
public class RunComputationObservationTests
{
    private const string Attester = "def attest(**_):\n    return {}  # v1\n";
    private const string Inline = "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { receipt: [job_id] }\nattester: { resource: att.py }\n---\n# Computation\n\n```sql\nSELECT 1\n```\n";
    private const string FromFile = "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: q.sql\nexecutor: { receipt: [job_id] }\nattester: { resource: att.py }\n---\n";

    private sealed class Runs
    {
        public int Stages;
        public string? AttesterSeen;
        public string? CodeSeen;
    }

    /// <summary>A provider whose <c>Exists</c> can be made to answer wrongly or throw after the load: the transient failure of a HEAD that a guard must not depend on.</summary>
    private sealed class FlakyExists(IBundleProvider inner) : SyncTestProvider
    {
        public bool? Answer { get; set; }

        public bool Throw { get; set; }

        public override string Id => inner.Id;
        public override string Location => inner.Location;
        public override BundleProviderCapabilities Capabilities => inner.Capabilities;
        public override BundleKeyConstraints NativeConstraints => inner.NativeConstraints;
        protected override BundleVersion GetVersionSync(BundleLimits limits) => inner.GetVersion(limits);
        protected override IEnumerable<BundleListing> EnumerateSync(BundleKey? prefix, BundleLimits limits) => inner.Enumerate(prefix, limits);
        protected override BundleObject ReadSync(BundleKey key, BundleObjectVersion? expected) => inner.Read(key, expected);
        protected override BundleObjectVersion WriteSync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition) => inner.Write(key, content, condition);
        protected override void DeleteSync(BundleKey key, BundleWriteCondition condition) => inner.Delete(key, condition);

        protected override bool ExistsSync(BundleKey key) =>
            Throw ? throw new IOException("transient failure") : Answer ?? inner.Exists(key);
    }

    private static (InMemoryBundleProvider Storage, OkfBundleTools Tools, Runs Runs) ToolsOver(string concept, BundleRefreshPolicy? policy = null, Func<IBundleProvider, IBundleProvider>? wrap = null)
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("c/rev.md", concept).Add("att.py", Attester).Add("q.sql", "SELECT 2\n");
        var runs = new Runs();
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }));
        runtime.BindFunc = (contract, computation, values, _) =>
        {
            runs.Stages++;
            runs.CodeSeen = computation.InlineCode;
            return ValueTask.FromResult(new BoundComputation("bigquery", computation.InlineCode, null, values));
        };
        runtime.AttestFunc = (context, _) =>
        {
            runs.Stages++;
            runs.AttesterSeen = context.AttesterSourceText;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var handle = new BundleHandle(wrap is null ? storage : wrap(storage), refreshPolicy: policy);
        return (storage, new OkfBundleTools(handle, new AttestationOrchestrator(reg)), runs);
    }

    private static Task<string> Run(OkfBundleTools tools) => tools.RunComputationToolAsync("c/rev", new Dictionary<string, object?>());

    [Fact]
    public async Task A_computation_not_shown_to_the_session_is_refused_and_a_shown_one_runs()
    {
        var (_, tools, runs) = ToolsOver(Inline);

        Assert.Equal("Error: 'c/rev' has not been shown to this session; read it first with okf_get_computation, then run it.", await Run(tools));
        Assert.Equal(0, runs.Stages);

        Assert.Contains("SELECT 1", tools.GetComputationTool("c/rev").Sync(), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal(2, runs.Stages);
        Assert.Equal(Attester, runs.AttesterSeen);

        // A whole-concept observation authorises an inline run too (spec §5.4).
        var (_, other, otherRuns) = ToolsOver(Inline);
        other.ReadConceptTool("c/rev").Sync();
        Assert.Contains("displayable: yes", await Run(other), StringComparison.Ordinal);
        Assert.Equal(2, otherRuns.Stages);
    }

    [Fact]
    public async Task The_public_method_needs_no_observation_but_authorises_none()
    {
        var (_, tools, runs) = ToolsOver(Inline);

        Assert.Contains("displayable: yes", await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>()), StringComparison.Ordinal);
        Assert.Equal(2, runs.Stages);

        tools.GetComputation("c/rev"); // the public method: a host presenting the result itself
        Assert.StartsWith("Error: 'c/rev' has not been shown", await Run(tools), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_concept_whose_contract_changed_since_it_was_shown_is_refused_and_the_image_refreshed()
    {
        var (storage, tools, runs) = ToolsOver(FromFile);
        tools.GetComputationTool("c/rev").Sync();

        // The contract changes and the handle is refreshed (spec §11, revision 4): the file is
        // unchanged, yet the concept the session was shown is not the one the image holds.
        storage.Add("c/rev.md", FromFile.Replace("runtime: bigquery", "runtime: bigquery\nparameters:\n  - { name: year, required: true }", StringComparison.Ordinal));
        tools.Handle.Invalidate();

        Assert.Equal("Error: 'c/rev' changed since it was shown to this session; read it again with okf_get_computation before running it.", await Run(tools));
        Assert.Equal(0, runs.Stages);
        Assert.True(tools.Handle.IsStale); // the refusal itself obliged the reload (the test's own Invalidate was acknowledged by the load of that run)

        Assert.Contains("year", tools.GetComputationTool("c/rev").Sync(), StringComparison.Ordinal); // the refreshed image
        var refused = await Run(tools); // the required parameter is missing: the run reached the orchestrator
        Assert.Contains("missing required parameter 'year'", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_computation_file_changed_since_it_was_shown_is_refused_at_its_pinned_read()
    {
        var (storage, tools, runs) = ToolsOver(FromFile);
        tools.ReadConceptTool("c/rev").Sync(); // a whole-concept observation does not show the file
        Assert.Equal("Error: the computation file 'q.sql' of 'c/rev' has not been shown to this session; read it first with okf_get_computation, then run it.", await Run(tools));
        Assert.Contains("SELECT 2", tools.GetComputationTool("c/rev").Sync(), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal("SELECT 2\n", runs.CodeSeen);

        storage.Add("q.sql", "SELECT 3\n");

        var refused = await Run(tools);
        Assert.Contains("computation file 'q.sql' changed since it was shown; read it again with okf_get_computation before running it", refused, StringComparison.Ordinal);
        Assert.Equal(2, runs.Stages);
        Assert.True(tools.Handle.IsStale);

        Assert.Contains("SELECT 3", tools.GetComputationTool("c/rev").Sync(), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal("SELECT 3\n", runs.CodeSeen);
    }

    /// <summary>D5: a computation injected by the context provider is not an observation, inline code included.</summary>
    [Fact]
    public async Task A_context_injection_does_not_authorise_a_run()
    {
        var (_, tools, runs) = ToolsOver(Inline);
        var provider = new OkfContextProvider(tools, new OkfContextProviderOptions { TokenBudget = 100_000 });
        var agent = new ScriptedChatClient([]).AsAIAgent();
#pragma warning disable MAAI001 // InvokingContext's public ctor is marked [Experimental].
        var context = await provider.ProvideForTest(new AIContextProvider.InvokingContext(agent, session: null, new AIContext { Messages = [new ChatMessage(ChatRole.User, "SELECT")] }), CancellationToken.None);
#pragma warning restore MAAI001

        Assert.Contains("SELECT 1", Assert.Single(context.Messages!).Text, StringComparison.Ordinal);
        Assert.StartsWith("Error: 'c/rev' has not been shown", await Run(tools), StringComparison.Ordinal);
        Assert.Equal(0, runs.Stages);
    }

    [Fact]
    public async Task An_observation_survives_a_refresh_of_the_image()
    {
        var (storage, tools, runs) = ToolsOver(Inline);
        Assert.StartsWith("Error: 'c/rev' has not been shown", await Run(tools), StringComparison.Ordinal);
        tools.GetComputationTool("c/rev").Sync();
        storage.Add("notes/other.md", "---\ntype: Note\ntitle: O\ndescription: o.\n---\n\nO.\n");
        tools.Handle.Invalidate();

        Assert.Equal(2, tools.GetBundle().Count); // reloaded
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal(2, runs.Stages);
    }

    [Fact]
    public async Task Another_tool_sets_observation_does_not_replace_this_sessions()
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("c/rev.md", Inline).Add("att.py", Attester);
        using var handle = new BundleHandle(storage);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" })) });
        var first = new OkfBundleTools(handle, new AttestationOrchestrator(reg));
        var second = new OkfBundleTools(handle, new AttestationOrchestrator(reg));
        first.GetComputationTool("c/rev").Sync();

        storage.Add("c/rev.md", Inline.Replace("SELECT 1", "SELECT 9", StringComparison.Ordinal));
        handle.Invalidate();
        second.GetComputationTool("c/rev").Sync();

        Assert.StartsWith("Error: 'c/rev' changed since it was shown", await Run(first), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(second), StringComparison.Ordinal);
    }

    /// <summary>
    /// An old full-concept observation does not hide a fresh code one (spec §5.4 point 1: either,
    /// at the current version, is enough): after a refusal for a changed concept, obeying it
    /// by reading it again with okf_get_computation lets the run through instead of refusing for ever.
    /// </summary>
    [Fact]
    public async Task A_fresh_code_observation_recovers_from_a_stale_full_concept_observation()
    {
        var (storage, tools, runs) = ToolsOver(Inline);
        tools.ReadConceptTool("c/rev").Sync(); // full, at v1
        storage.Add("c/rev.md", Inline.Replace("SELECT 1", "SELECT 9", StringComparison.Ordinal));
        tools.Handle.Invalidate();

        Assert.StartsWith("Error: 'c/rev' changed since it was shown", await Run(tools), StringComparison.Ordinal);
        Assert.Equal(0, runs.Stages);

        Assert.Contains("SELECT 9", tools.GetComputationTool("c/rev").Sync(), StringComparison.Ordinal); // code, at v2
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal("SELECT 9", runs.CodeSeen!.Trim());

        // The other way round: a code observation left behind does not hide a fresh full one.
        storage.Add("c/rev.md", Inline.Replace("SELECT 1", "SELECT 8", StringComparison.Ordinal));
        tools.Handle.Invalidate();
        Assert.StartsWith("Error: 'c/rev' changed since it was shown", await Run(tools), StringComparison.Ordinal);
        tools.ReadConceptTool("c/rev").Sync();
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
    }

    /// <summary>
    /// The file requirement is decided from the image's survey, never from a live lookup: a
    /// provider whose Exists answers wrongly or throws must not let a computation file the
    /// session never saw run, and must not refuse one it saw.
    /// </summary>
    [Theory]
    [InlineData("false")]
    [InlineData("throw")]
    public async Task A_failing_exists_lookup_never_lets_an_unseen_computation_file_run(string failure)
    {
        FlakyExists? flaky = null;
        var (_, tools, runs) = ToolsOver(FromFile, wrap: storage => flaky = new FlakyExists(storage));
        tools.ReadConceptTool("c/rev").Sync(); // the concept in full, but never the file
        void Fail()
        {
            flaky!.Answer = failure == "false" ? false : null;
            flaky.Throw = failure == "throw";
        }

        Fail();
        var refused = await Run(tools);
        Assert.Equal(0, runs.Stages);
        Assert.DoesNotContain("displayable: yes", refused, StringComparison.Ordinal);
        Assert.Null(runs.CodeSeen);

        flaky!.Answer = null;
        flaky.Throw = false;
        Assert.Equal("Error: the computation file 'q.sql' of 'c/rev' has not been shown to this session; read it first with okf_get_computation, then run it.", await Run(tools));
        Assert.Contains("SELECT 2", tools.GetComputationTool("c/rev").Sync(), StringComparison.Ordinal);

        Fail(); // the file was shown: the lookup failing now refuses nothing
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal("SELECT 2\n", runs.CodeSeen);
    }

    /// <summary>
    /// A file observation is a purpose of its own: showing the concept that happens to sit under a
    /// computation file's key (code of another concept) does not show that file.
    /// </summary>
    [Fact]
    public async Task A_code_observation_of_a_concept_is_not_the_observation_of_a_file_sharing_its_key()
    {
        var (storage, tools, runs) = ToolsOver(FromFile.Replace("computation: q.sql", "computation: a.md", StringComparison.Ordinal));
        storage.Add("a.md", Inline);
        tools.Handle.Invalidate();
        tools.ReadConceptTool("c/rev").Sync();
        Assert.Contains("SELECT 1", tools.GetComputationTool("a").Sync(), StringComparison.Ordinal); // a.md as a concept: its code, not the file c/rev runs

        Assert.Equal("Error: the computation file 'a.md' of 'c/rev' has not been shown to this session; read it first with okf_get_computation, then run it.", await Run(tools));
        Assert.Equal(0, runs.Stages);

        tools.GetComputationTool("c/rev").Sync();
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
    }

    /// <summary>
    /// Where the survey says nothing can be captured, the gate does not tell the model to read first
    /// with a tool that could never record: the capture's honest refusal comes first.
    /// </summary>
    [Fact]
    public async Task A_run_that_cannot_be_captured_is_refused_for_its_own_reason()
    {
        var (_, noAttester, noAttesterRuns) = ToolsOver(Inline.Replace("attester: { resource: att.py }\n", "", StringComparison.Ordinal));
        Assert.Contains("no attester.resource is declared", await Run(noAttester), StringComparison.Ordinal);
        Assert.False(noAttester.Handle.IsStale); // only a change of the concept alters it

        var (_, urlFile, _) = ToolsOver(FromFile.Replace("computation: q.sql", "computation: https://example.org/q.sql", StringComparison.Ordinal));
        var refused = await Run(urlFile);
        Assert.Contains("computation file 'https://example.org/q.sql' could not be resolved (Url)", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("has not been shown", refused, StringComparison.Ordinal);

        var (_, missing, missingRuns) = ToolsOver(FromFile.Replace("computation: q.sql", "computation: nothing.sql", StringComparison.Ordinal));
        Assert.Contains("computation file 'nothing.sql' could not be resolved (Missing)", await Run(missing), StringComparison.Ordinal);
        Assert.True(missing.Handle.IsStale);
        Assert.Equal(0, noAttesterRuns.Stages + missingRuns.Stages);
    }

    /// <summary>
    /// Open decision 4: the public method, which needs no observation, still obliges the reload after
    /// a refused pin, so that a host on the Manual policy recovers without calling Refresh().
    /// </summary>
    [Fact]
    public async Task The_public_method_obliges_a_reload_after_a_refused_pin()
    {
        var (storage, tools, runs) = ToolsOver(Inline, BundleRefreshPolicy.Manual);
        Assert.Contains("displayable: yes", await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>()), StringComparison.Ordinal);
        var loaded = tools.Handle.Current;

        const string v2 = "def attest(**_):\n    return {}  # v2\n";
        storage.Add("att.py", v2);

        var refused = await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>());
        Assert.Contains("attester resource 'att.py' changed since the bundle was loaded", refused, StringComparison.Ordinal);
        Assert.True(tools.Handle.IsStale);

        Assert.Contains("displayable: yes", await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>()), StringComparison.Ordinal);
        Assert.Equal(v2, runs.AttesterSeen);
        Assert.NotSame(loaded, tools.Handle.Current);
    }

    /// <summary>
    /// Liveness under Manual: the file is surveyed Pinned, then removed after the load, and the session never
    /// showed it. okf_get_computation, which fails to read it, obliges the reload, so the run's next attempt
    /// carries the capture's own refusal instead of telling the model again to read it first.
    /// </summary>
    [Fact]
    public async Task A_file_that_disappears_after_the_load_does_not_leave_the_model_looping_under_Manual()
    {
        var (storage, tools, runs) = ToolsOver(FromFile, BundleRefreshPolicy.Manual);
        tools.ReadConceptTool("c/rev").Sync(); // the concept in full, never the file
        storage.Delete(BundleKey.Parse("q.sql"), BundleWriteCondition.None);

        Assert.StartsWith("Error: the computation file 'q.sql' of 'c/rev' has not been shown", await Run(tools), StringComparison.Ordinal);

        Assert.Contains("could not be resolved (Missing)", tools.GetComputationTool("c/rev").Sync(), StringComparison.Ordinal);
        Assert.True(tools.Handle.IsStale);

        var refused = await Run(tools);
        Assert.Contains("computation file 'q.sql' could not be resolved (Missing)", refused, StringComparison.Ordinal);
        Assert.DoesNotContain("has not been shown", refused, StringComparison.Ordinal);
        Assert.Equal(0, runs.Stages);
    }

    /// <summary>
    /// Defence in depth: the gate defers a run its predicate judges not capturable to Capture's refusal. If Capture
    /// accepts it (the predicate and Capture drifted apart), nothing observed covers what would run: the run is
    /// refused. The seam makes the predicate say "not capturable" for a computation that is in fact runnable.
    /// </summary>
    [Fact]
    public async Task A_run_the_gate_deferred_and_the_capture_accepted_is_refused()
    {
        var (_, tools, runs) = ToolsOver(Inline);
        tools.GateCapturable = (_, _) => false;

        var refused = await Run(tools); // never shown: the gate would have refused, had it judged the run capturable
        Assert.Equal("Error: 'c/rev' was not gated by this session's observations (a defect in the run gate); it is not run.", refused);
        Assert.Equal(0, runs.Stages);

        tools.GetComputationTool("c/rev").Sync(); // shown, too: still refused, the gate's judgement is what failed
        Assert.StartsWith("Error: 'c/rev' was not gated", await Run(tools), StringComparison.Ordinal);
        Assert.Equal(0, runs.Stages);

        tools.GateCapturable = (_, _) => true; // a predicate that agrees with Capture: the observed run goes through
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
    }

    /// <summary>
    /// Recovery after a refused pin (spec §5.1, §11), under each policy: the attester changes
    /// after the load; the first attempt is refused (unless the policy itself reloads, as
    /// OnAccess does on the in-memory token, which hashes content); the next attempt, with no
    /// host <c>Refresh()</c>, runs on the new version. Same path for an attester missing at the
    /// load and created after it. The session's observation is untouched by the reload.
    /// </summary>
    [Theory]
    [InlineData("manual", false)]
    [InlineData("onaccess", false)]
    [InlineData("interval", false)]
    [InlineData("notify", false)]
    [InlineData("manual", true)]
    [InlineData("notify", true)]
    public async Task A_refused_pin_obliges_a_reload_and_the_next_attempt_runs_on_the_new_state(string policy, bool missingFirst)
    {
        var storage = new InMemoryBundleProvider(canWrite: true).Add("c/rev.md", Inline);
        if (!missingFirst)
        {
            storage.Add("att.py", Attester);
        }

        var notifying = new NotifyingBundleProvider(storage);
        var refresh = policy switch
        {
            "onaccess" => BundleRefreshPolicy.OnAccess,
            "interval" => BundleRefreshPolicy.Every(TimeSpan.FromHours(1)),
            "notify" => BundleRefreshPolicy.Notify,
            _ => BundleRefreshPolicy.Manual,
        };
        var runs = new Runs();
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }));
        runtime.AttestFunc = (context, _) =>
        {
            runs.Stages++;
            runs.AttesterSeen = context.AttesterSourceText;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        using var handle = new BundleHandle(notifying, refreshPolicy: refresh);
        var tools = new OkfBundleTools(handle, new AttestationOrchestrator(reg));
        tools.GetComputationTool("c/rev").Sync();
        var loaded = handle.Current;
        Assert.Equal(missingFirst ? ExecutableResourceState.Missing : ExecutableResourceState.Pinned, loaded.ExecutableResources[ConceptId.Parse("c/rev")].Attester.State);

        const string v2 = "def attest(**_):\n    return {}  # v2\n";
        storage.Add("att.py", v2);

        var first = await Run(tools);
        if (policy != "onaccess")
        {
            Assert.Contains(missingFirst ? "attester resource 'att.py' could not be resolved (Missing)" : "attester resource 'att.py' changed since the bundle was loaded", first, StringComparison.Ordinal);
            Assert.Equal(0, runs.Stages);
            Assert.True(handle.IsStale);
        }
        else
        {
            // OnAccess compares the (content-hashing) token at every access: the image is
            // reloaded before the run, so there is nothing to refuse and no recovery to make.
            // (This row pins that no spurious refusal appears; it does not exercise the gate.)
            Assert.Contains("displayable: yes", first, StringComparison.Ordinal);
            Assert.Equal(v2, runs.AttesterSeen);
        }

        var second = await Run(tools);
        Assert.Contains("displayable: yes", second, StringComparison.Ordinal);
        Assert.Equal(v2, runs.AttesterSeen);
        Assert.NotSame(loaded, handle.Current);
        Assert.False(handle.IsStale);
        Assert.Equal(1, tools.Observations.Count);
    }

    /// <summary>
    /// What a model reaches: <c>GetTools()</c> binds <c>okf_run_computation</c> to the gated wrapper, not to the
    /// public ungated method (which authorises nothing and needs no observation). Pinned by the bound method's
    /// name and by invoking the exposed function on a computation the session never saw: refused, no stage called.
    /// </summary>
    [Fact]
    public async Task The_exposed_run_function_is_the_gated_wrapper()
    {
        var (_, tools, runs) = ToolsOver(Inline);
        var function = tools.GetTools().Cast<AIFunction>().Single(f => f.Name == "okf_run_computation");

        Assert.Equal(nameof(OkfBundleTools.RunComputationToolAsync), function.UnderlyingMethod!.Name);

        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["conceptId"] = "c/rev", ["parameterValues"] = new Dictionary<string, object?>() });
        var refused = await function.InvokeAsync(arguments);

        Assert.Contains("Error: 'c/rev' has not been shown to this session", refused?.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, runs.Stages);
    }
}
