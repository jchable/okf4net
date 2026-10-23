// SPDX-License-Identifier: LGPL-3.0-or-later
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

    private static (InMemoryBundleProvider Storage, OkfBundleTools Tools, Runs Runs) ToolsOver(string concept, BundleRefreshPolicy? policy = null, IBundleProvider? wrapped = null)
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
        var handle = new BundleHandle(wrapped ?? storage, refreshPolicy: policy);
        return (storage, new OkfBundleTools(handle, new AttestationOrchestrator(reg)), runs);
    }

    private static Task<string> Run(OkfBundleTools tools) => tools.RunComputationToolAsync("c/rev", new Dictionary<string, object?>());

    [Fact]
    public async Task A_computation_not_shown_to_the_session_is_refused_and_a_shown_one_runs()
    {
        var (_, tools, runs) = ToolsOver(Inline);

        Assert.Equal("Error: 'c/rev' has not been shown to this session; read it first with okf_get_computation, then run it.", await Run(tools));
        Assert.Equal(0, runs.Stages);

        Assert.Contains("SELECT 1", tools.GetComputationTool("c/rev"), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal(2, runs.Stages);
        Assert.Equal(Attester, runs.AttesterSeen);

        // A whole-concept observation authorises an inline run too (spec §5.4).
        var (_, other, otherRuns) = ToolsOver(Inline);
        other.ReadConceptTool("c/rev");
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
        tools.GetComputationTool("c/rev");

        // The contract changes and the handle is refreshed (spec §11, revision 4): the file is
        // unchanged, yet the concept the session was shown is not the one the image holds.
        storage.Add("c/rev.md", FromFile.Replace("runtime: bigquery", "runtime: bigquery\nparameters:\n  - { name: year, required: true }", StringComparison.Ordinal));
        tools.Handle.Invalidate();

        Assert.Equal("Error: 'c/rev' changed since it was shown to this session; read it again with okf_get_computation before running it.", await Run(tools));
        Assert.Equal(0, runs.Stages);

        Assert.Contains("year", tools.GetComputationTool("c/rev"), StringComparison.Ordinal); // the refreshed image
        var refused = await Run(tools); // the required parameter is missing: the run reached the orchestrator
        Assert.Contains("missing required parameter 'year'", refused, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_computation_file_changed_since_it_was_shown_is_refused_at_its_pinned_read()
    {
        var (storage, tools, runs) = ToolsOver(FromFile);
        tools.ReadConceptTool("c/rev"); // a whole-concept observation does not show the file
        Assert.Equal("Error: the computation file 'q.sql' of 'c/rev' has not been shown to this session; read it first with okf_get_computation, then run it.", await Run(tools));
        Assert.Contains("SELECT 2", tools.GetComputationTool("c/rev"), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(tools), StringComparison.Ordinal);
        Assert.Equal("SELECT 2\n", runs.CodeSeen);

        storage.Add("q.sql", "SELECT 3\n");

        var refused = await Run(tools);
        Assert.Contains("computation file 'q.sql' changed since it was shown; read it again with okf_get_computation before running it", refused, StringComparison.Ordinal);
        Assert.Equal(2, runs.Stages);
        Assert.True(tools.Handle.IsStale);

        Assert.Contains("SELECT 3", tools.GetComputationTool("c/rev"), StringComparison.Ordinal);
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
        tools.GetComputationTool("c/rev");
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
        first.GetComputationTool("c/rev");

        storage.Add("c/rev.md", Inline.Replace("SELECT 1", "SELECT 9", StringComparison.Ordinal));
        handle.Invalidate();
        second.GetComputationTool("c/rev");

        Assert.StartsWith("Error: 'c/rev' changed since it was shown", await Run(first), StringComparison.Ordinal);
        Assert.Contains("displayable: yes", await Run(second), StringComparison.Ordinal);
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
        tools.GetComputationTool("c/rev");
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

        var second = await Run(tools);
        Assert.Contains("displayable: yes", second, StringComparison.Ordinal);
        Assert.Equal(v2, runs.AttesterSeen);
        Assert.NotSame(loaded, handle.Current);
        Assert.False(handle.IsStale);
        Assert.Equal(1, tools.Observations.Count);
    }
}
