// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OKF4net.Agents;
using OKF4net.Attestation;
using OKF4net.Tests.Attestation;
using Xunit;

namespace OKF4net.Tests.Agents;

public class OkfComputationToolsTests
{
    [Fact]
    public void Get_computation_returns_contract_and_inline_code()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\n---\n# Computation\n\n```sql\nSELECT 1\n```\n");
        var tools = new OkfBundleTools(tmp.Path);
        var s = tools.GetComputation("c/rev");
        Assert.Contains("bigquery", s);
        Assert.Contains("SELECT 1", s);
    }

    /// <summary>
    /// Not every OperationCanceledException means the caller asked to stop.
    /// HttpClient raises TaskCanceledException on its own request timeout with
    /// no token of ours cancelled, and a host executor calling one is the
    /// normal case. The #65 filters (`when (e is not OperationCanceledException)`)
    /// let that escape the orchestrator, and the tool's catch chain has no arm
    /// for it either — its OCE handler requires the timeout source to have
    /// fired, and its general handler does not list OCE. So a routine
    /// downstream timeout blew a raw exception at the LLM, which the bare
    /// `catch (Exception)` those filters replaced used to absorb.
    ///
    /// Cancellation propagates only when the caller's own token is cancelled.
    /// </summary>
    [Fact]
    public async Task An_executors_own_timeout_is_reported_not_thrown_at_the_caller()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var runtime = new FakeRuntime
        {
            // Exactly what HttpClient throws when ITS timeout elapses: an OCE
            // subclass, with nobody's token cancelled.
            ExecuteFunc = (_, _, _) => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."),
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));

        var rendered = await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>());

        Assert.Contains("displayable: no", rendered.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("executor threw", rendered.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Raised in review of #65: the timeout's CancellationTokenSource was
    /// constructed BEFORE the try, and `new CancellationTokenSource(TimeSpan)`
    /// throws ArgumentOutOfRangeException for a negative delay other than
    /// Timeout.InfiniteTimeSpan. A host misconfiguring ComputationTimeout would
    /// therefore blow a raw exception out of the tool, breaking the "tools never
    /// throw toward the LLM" invariant that every other guard here maintains —
    /// and doing it on a misconfiguration, which is exactly when a clear message
    /// matters most.
    /// </summary>
    [Fact]
    public async Task An_invalid_timeout_is_reported_not_thrown()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" })),
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg))
        {
            ComputationTimeout = TimeSpan.FromSeconds(-5),
        };

        var rendered = await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>());

        Assert.StartsWith("Error:", rendered, StringComparison.Ordinal);
        Assert.Contains("ComputationTimeout", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// Raised in review of #77's pass over this file: the guard rejects
    /// `&lt; TimeSpan.Zero` while its own message says "must be positive", so
    /// zero slipped through — and `CancelAfter(TimeSpan.Zero)` fires
    /// immediately, making EVERY computation report "timed out after 0s"
    /// instead of naming the misconfiguration. Silently turning a fat-fingered
    /// setting into a tool that always fails is worse than rejecting it.
    /// </summary>
    [Fact]
    public async Task A_zero_timeout_is_rejected_rather_than_timing_every_run_out()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" })),
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg))
        {
            ComputationTimeout = TimeSpan.Zero,
        };

        var rendered = await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>());

        Assert.StartsWith("Error:", rendered, StringComparison.Ordinal);
        Assert.Contains("ComputationTimeout", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("timed out", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative half of that guard is not the whole hole:
    /// `new CancellationTokenSource(TimeSpan)` also rejects any delay past
    /// uint.MaxValue - 1 milliseconds (~49.71 days, measured against the
    /// runtime), so a host setting a very long ceiling got exactly the raw
    /// ArgumentOutOfRangeException the negative case used to throw. One bound
    /// checked is not a bound checked.
    /// </summary>
    [Fact]
    public async Task A_timeout_past_the_runtimes_ceiling_is_reported_not_thrown()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" })),
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg))
        {
            ComputationTimeout = TimeSpan.FromDays(60),
        };

        var rendered = await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>());

        Assert.StartsWith("Error:", rendered, StringComparison.Ordinal);
        Assert.Contains("ComputationTimeout", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tool blocked the calling thread with `.GetAwaiter().GetResult()` and
    /// passed no token at all, so `cancellationToken` reached the orchestrator
    /// as `default`. A slow or wedged executor — an HTTP call to a warehouse
    /// that never answers — pinned an Agent Framework worker with no way out.
    ///
    /// AIFunctionFactory binds a CancellationToken parameter automatically and
    /// excludes it from the JSON schema, so the async tool takes the host's
    /// token with no change to what the model sees.
    /// </summary>
    [Fact]
    public async Task Run_computation_async_honours_the_callers_token()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        using var cts = new CancellationTokenSource();
        var runtime = new FakeRuntime();
        runtime.ExecuteFunc = (_, _, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("unreachable"); };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>(), cts.Token));
    }

    /// <summary>
    /// A host that never cancels still needs a floor: an executor that simply
    /// never returns would otherwise hang the invocation forever. The timeout is
    /// a host guard, not a §10 rule — §10 says nothing about wall-clock limits.
    /// </summary>
    [Fact]
    public async Task A_computation_past_its_timeout_is_reported_not_hung()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var runtime = new FakeRuntime();
        runtime.ExecuteFunc = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg))
        {
            ComputationTimeout = TimeSpan.FromMilliseconds(50),
        };

        var rendered = await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>());

        // Reported to the model as a normal non-displayable outcome -- the tool
        // never throws toward the LLM -- and NOT as a cancellation, which would
        // wrongly suggest the caller asked for it.
        Assert.Contains("displayable: no", rendered.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("timed out", rendered.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A host-plugged runtime's exception was rendered straight to the model —
    /// twice: as `Error: {outcome.Error.Message}` and again inside the
    /// orchestrator's own reason string. Those messages come from code this
    /// library does not control; a real executor's failure can name a
    /// connection string, a query, or a row it choked on.
    ///
    /// The exception object itself stays on <see cref="AttestationOutcome.Error"/>
    /// for the host, which is the right audience for it. What changes is what
    /// crosses into the model's context.
    /// </summary>
    [Fact]
    public async Task A_runtime_failure_does_not_render_the_exception_message_to_the_model()
    {
        const string secret = "Server=db-prod;Password=hunter2";
        using var tmp = new TempDir();
        tmp.Write(
            "c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.ThrowingExecutor(new InvalidOperationException(secret)),
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));

        var rendered = await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>());

        Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        // Still useful: the model must learn the run failed, and where.
        Assert.Contains("displayable: no", rendered.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("executor", rendered.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression test: <see cref="OkfBundleTools.ReadConcept"/>'s Attested-Computation
    /// enrichment must not advertise <c>okf_run_computation</c> when no orchestrator is
    /// wired -- <see cref="OkfBundleTools.GetTools"/> only exposes that tool when
    /// <c>_orchestrator</c> is non-null (as the shipped <c>okf-mcp</c> server never wires
    /// one), so mentioning it unconditionally would tell a consumer to call a tool that
    /// isn't in their tool list.
    /// </summary>
    [Fact]
    public void Read_concept_mentions_only_get_computation_without_orchestrator()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\n---\n# Computation\n\n```sql\nSELECT 1\n```\n");
        var tools = new OkfBundleTools(tmp.Path);
        var s = tools.ReadConcept("c/rev");
        Assert.Contains("okf_get_computation", s);
        Assert.DoesNotContain("okf_run_computation", s);
    }

    /// <summary>
    /// Counterpart to <see cref="Read_concept_mentions_only_get_computation_without_orchestrator"/>:
    /// with an orchestrator wired, <c>okf_run_computation</c> IS in the tool list, so
    /// <see cref="OkfBundleTools.ReadConcept"/>'s enrichment should mention both tools.
    /// </summary>
    [Fact]
    public void Read_concept_mentions_both_tools_with_orchestrator_wired()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\n---\n# Computation\n\n```sql\nSELECT 1\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing()
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        var s = tools.ReadConcept("c/rev");
        Assert.Contains("okf_get_computation", s);
        Assert.Contains("okf_run_computation", s);
    }

    [Fact]
    public void Run_computation_tool_absent_without_orchestrator()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\n---\n");
        var names = new OkfBundleTools(tmp.Path).GetTools().Select(t => t.Name).ToList();
        Assert.Contains("okf_get_computation", names);
        Assert.DoesNotContain("okf_run_computation", names);
    }

    [Fact]
    public async Task Run_computation_invokes_orchestrator_when_wired()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }))
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        Assert.Contains("okf_run_computation", tools.GetTools().Select(t => t.Name));
        var s = await tools.RunComputationAsync("c/rev", new Dictionary<string, object?>());
        Assert.Contains("displayable", s.ToLowerInvariant());
    }

    /// <summary>
    /// A reflection/AIFunction-bound call can pass CLR <see langword="null"/>
    /// for <c>parameterValues</c> despite its non-nullable static type (e.g. a
    /// host/LLM that omits the property entirely). Without a guard,
    /// <see cref="AttestationOrchestrator.RunAsync"/>'s own required-parameter
    /// gate (<c>parameterValues.ContainsKey(...)</c>) would throw a
    /// <see cref="NullReferenceException"/> that <c>RunTool</c>'s catch filter
    /// does not cover -- breaking the "tools never throw toward the LLM"
    /// invariant. This concept declares a required parameter so the gate is
    /// actually exercised; the run must degrade to a non-displayable outcome
    /// (missing required parameter), never an unhandled exception.
    /// </summary>
    [Fact]
    public void Run_computation_with_null_parameterValues_does_not_throw()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nparameters:\n  - name: threshold\n    required: true\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }))
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));

#pragma warning disable CS0618 // The obsolete sync overload is still shipped, so its own null guard stays pinned.
        var s = tools.RunComputation("c/rev", null!);
#pragma warning restore CS0618

        var lower = s.ToLowerInvariant();
        Assert.Contains("displayable: no", lower);
        Assert.Contains("missing required parameter", lower);
    }
}
