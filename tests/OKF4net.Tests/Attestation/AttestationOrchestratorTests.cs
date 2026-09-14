// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using Xunit;

namespace OKF4net.Tests.Attestation;

/// <summary>
/// <see cref="AttestationOrchestrator"/>: the §10.5 load → bind → execute →
/// validate-receipt → attest → gate sequence, errors-as-data throughout —
/// every expected failure surfaces as a non-displayable <see cref="AttestationOutcome"/>
/// with <see cref="AttestationOutcome.Reasons"/>, and binder/executor/attester
/// exceptions are caught rather than propagated. Exercised against
/// <see cref="FakeRuntime"/> (shared with <c>AttestationValuesTests</c>), never
/// touching <c>tests/fixtures</c>.
/// </summary>
public class AttestationOrchestratorTests
{
    private static (Bundle, ConceptId) InlineComputation(TempDir tmp)
    {
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id, result] }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        return (Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"));
    }

    [Fact]
    public async Task Happy_path_is_displayable()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 })),
        });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.True(outcome.Displayable);
        Assert.True(outcome.Verdict!.Value.Passed);
        Assert.True(outcome.ReceiptShapeOk);
        Assert.Null(outcome.Error);
    }

    /// <summary>
    /// The orchestrator handed its token to each host stage and then trusted
    /// them to observe it. A stage that ignores its token is not a hypothetical
    /// — it is any binder or executor whose underlying client predates
    /// cancellation support, or simply forgets — and for those, an
    /// already-cancelled run executed every stage and could return a
    /// DISPLAYABLE success. For §10 that means a computation actually ran, and
    /// possibly hit a warehouse, after the caller withdrew.
    ///
    /// Passing the token on is not observing it. The orchestrator checks at
    /// each step boundary, so cancellation is honoured whatever the host does.
    /// </summary>
    [Fact]
    public async Task A_cancelled_run_does_not_execute_stages_that_ignore_their_token()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var executed = false;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        // Ignores its token entirely, like a stage built on a client that has
        // no cancellation support.
        runtime.ExecuteFunc = (_, _, _) =>
        {
            executed = true;
            return ValueTask.FromResult(new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 }, cancellationToken: cts.Token));

        Assert.False(executed, "the executor ran after the caller had already withdrawn");
    }

    /// <summary>
    /// A stage that cancels the supplied token and then faults with the
    /// cancellation WRAPPED — an AggregateException, which is what
    /// `.Result`/`.Wait()` on a cancelled task produces, and a realistic shape
    /// at a plugin boundary — was converted into an ordinary non-displayable
    /// outcome, because the filter only recognised a top-level
    /// OperationCanceledException. The caller had cancelled; that is a
    /// cancellation however it is packaged.
    /// </summary>
    [Fact]
    public async Task A_wrapped_cancellation_still_propagates()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        using var cts = new CancellationTokenSource();

        var runtime = new FakeRuntime
        {
            ExecuteFunc = (_, _, _) =>
            {
                cts.Cancel();
                throw new AggregateException(new OperationCanceledException(cts.Token));
            },
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 }, cancellationToken: cts.Token));
    }

    /// <summary>
    /// Cancellation is control flow, not data. Every stage's catch was a bare
    /// `catch (Exception)`, so an OperationCanceledException raised by a
    /// host-plugged stage was caught with everything else and converted into a
    /// business outcome — `RunAsync(ct)` with a cancelled token returned a
    /// normal-looking result, and a caller could not tell "the executor failed"
    /// from "I asked it to stop".
    ///
    /// Errors-as-data is the contract for FAILURES and stays; an OCE is not one.
    /// </summary>
    [Theory]
    [InlineData("binder")]
    [InlineData("executor")]
    [InlineData("attester")]
    public async Task A_cancelled_stage_propagates_rather_than_becoming_an_outcome(string stage)
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        using var cts = new CancellationTokenSource();

        // Each stage observes the token and honours it, the way a real
        // implementation awaiting I/O would.
        var runtime = new FakeRuntime();
        switch (stage)
        {
            case "binder":
                runtime.BindFunc = (_, _, _, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("unreachable"); };
                break;
            case "executor":
                runtime.ExecuteFunc = (_, _, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("unreachable"); };
                break;
            default:
                // Step 8 runs only when the receipt shape is trustworthy, so the
                // executor has to return the two fields the concept declares --
                // FakeRuntime's default empty Receipt would skip attestation
                // entirely and the stage under test would never be reached.
                runtime.ExecuteFunc = (_, _, _) =>
                    ValueTask.FromResult(new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
                runtime.AttestFunc = (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("unreachable"); };
                break;
        }

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 }, cancellationToken: cts.Token));
    }

    /// <summary>
    /// The token was checked only BEFORE each stage, so a stage that ignores
    /// its token and is already running when cancellation arrives ran to
    /// completion, and its result was used: a run cancelled at 30 ms waited
    /// out the whole attester and came back with an outcome instead of a
    /// cancellation. The orchestrator stops awaiting such a stage the moment
    /// the token fires (§10.5).
    ///
    /// Two shapes: <c>async</c> awaits without the token; <c>blocking</c> blocks
    /// its calling thread before it returns its <see cref="ValueTask{TResult}"/>
    /// at all (a synchronous client wrapped in <c>ValueTask.FromResult</c>), so
    /// awaiting the returned value alone could never abandon it.
    ///
    /// The stage would take 30 s; the bound is 5 s. The discriminator is
    /// "returned long before the stage would have finished", deliberately far
    /// above scheduling noise on a loaded machine — a 350 ms stage under a
    /// 200 ms bound flaked with three test suites running concurrently. Nothing
    /// awaits the abandoned stage.
    /// </summary>
    [Theory]
    [InlineData("async")]
    [InlineData("blocking")]
    public async Task A_token_ignoring_stage_is_abandoned_when_the_token_fires(string shape)
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));

        // Not disposed: the abandoned stage may still be waiting on it when the
        // test ends, and Set() in finally is what releases it.
        var gate = new ManualResetEventSlim(false);
        if (shape == "async")
        {
            runtime.AttestFunc = async (_, _) =>
            {
                // Ignores its token, like a client with no cancellation support.
                await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
                return new AttestationVerdict(true, null);
            };
        }
        else
        {
            runtime.AttestFunc = (_, _) =>
            {
                // Blocks its calling thread before returning anything.
                gate.Wait(TimeSpan.FromSeconds(30));
                return ValueTask.FromResult(new AttestationVerdict(true, null));
            };
        }
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        try
        {
            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 }, cancellationToken: cts.Token));
            stopwatch.Stop();

            Assert.True(stopwatch.ElapsedMilliseconds < 5_000, $"RunAsync returned after {stopwatch.ElapsedMilliseconds} ms; the token fired at 30 ms and the {shape} stage would have finished at 30 s");
        }
        finally
        {
            gate.Set();
        }
    }

    /// <summary>
    /// Moving each stage onto the thread pool (so a thread-blocking stage can
    /// be abandoned) must not change how a stage that throws SYNCHRONOUSLY —
    /// before returning any <see cref="ValueTask{TResult}"/> — is reported: a
    /// non-cancellation exception is still a failure reason on a
    /// non-displayable outcome, and an HttpClient-style
    /// <see cref="TaskCanceledException"/> with the caller's token NOT
    /// cancelled is still a stage failure, not a cancellation. The token here
    /// is cancellable, so the stage really does take the pool hop (a
    /// non-cancellable token is invoked directly and never reaches it).
    /// </summary>
    [Theory]
    [InlineData("invalid-operation")]
    [InlineData("task-canceled")]
    public async Task A_synchronously_throwing_stage_under_a_cancellable_token_is_still_a_failure_reason(string kind)
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        using var cts = new CancellationTokenSource();

        var runtime = new FakeRuntime();
        runtime.ExecuteFunc = (_, _, _) => throw (kind == "invalid-operation"
            ? new InvalidOperationException("secret detail")
            : new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."));
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));

        var outcome = await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 }, cancellationToken: cts.Token);

        Assert.False(outcome.Displayable);
        var expectedType = kind == "invalid-operation" ? nameof(InvalidOperationException) : nameof(TaskCanceledException);
        Assert.Equal([$"executor threw: {expectedType}"], outcome.Reasons);
        Assert.Equal(expectedType, outcome.Error!.GetType().Name);
    }

    /// <summary>
    /// A stage that cancels the caller's token and then returns SUCCESS — the
    /// token is cancelled, yet the stage's result was used. For the attester,
    /// the last stage, nothing checked the token afterwards, so the run came
    /// back cancelled AND displayable. A stage completing after cancellation
    /// never contributes a result (§10.5).
    ///
    /// These rows pin the observable rule end to end; they do NOT pin the
    /// token check that follows a successful stage in
    /// <c>AttestationOrchestrator.AwaitStageAsync</c>. Stages now run on the
    /// thread pool, so when a stage cancels the token, <c>WaitAsync</c> almost
    /// always throws first, and the post-stage check is reached only in the
    /// rare interleaving where the stage finishes before <c>WaitAsync</c> is
    /// called — deleting that check leaves these rows green.
    /// <see cref="The_post_stage_check_rejects_a_stage_that_completed_before_the_token_was_seen"/>
    /// is what pins it, deterministically.
    /// <list type="bullet">
    /// <item><c>attester</c> and <c>executor</c> fail if the orchestrator stops
    /// enforcing the token after a stage has started (both <c>WaitAsync</c> and
    /// the post-stage check removed). The executor's receipt omits the declared
    /// <c>result</c> field, so attestation is skipped and no later stage-entry
    /// check can stand in for that enforcement.</item>
    /// <item><c>binder</c> guards nothing beyond that rule: a successful bind is
    /// always followed by the executor's entry check, which throws first.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("binder")]
    [InlineData("executor")]
    [InlineData("attester")]
    public async Task A_stage_that_cancels_the_token_and_succeeds_never_yields_an_outcome(string stage)
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        using var cts = new CancellationTokenSource();

        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        switch (stage)
        {
            case "binder":
                runtime.BindFunc = (contract, computation, values, _) =>
                {
                    cts.Cancel();
                    return ValueTask.FromResult(new BoundComputation(contract.Runtime ?? "fake", computation.InlineCode, null, values));
                };
                break;
            case "executor":
                runtime.ExecuteFunc = (_, _, _) =>
                {
                    cts.Cancel();
                    // 'result' deliberately missing: see the summary.
                    return ValueTask.FromResult(new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }));
                };
                break;
            default:
                runtime.AttestFunc = (_, _) =>
                {
                    cts.Cancel();
                    return ValueTask.FromResult(new AttestationVerdict(true, null));
                };
                break;
        }

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 }, cancellationToken: cts.Token));
    }

    /// <summary>
    /// The token check AFTER a stage succeeds, pinned deterministically. A
    /// stage that cancels the token and returns success can finish on the
    /// thread pool before the orchestrator reaches <c>WaitAsync</c>, and
    /// <c>WaitAsync</c> hands back an already-completed task's result even when
    /// the token is already cancelled (it tests completion first). Without the
    /// post-stage check, that interleaving returned the result — for the
    /// attester, a displayable outcome after the attester itself had cancelled
    /// the run (§10.5). From <see cref="AttestationOrchestrator.RunAsync"/> only
    /// a race reaches it, so this drives <c>AwaitStageAsync</c> directly with
    /// exactly that state: a completed stage and a cancelled token.
    /// </summary>
    [Fact]
    public async Task The_post_stage_check_rejects_a_stage_that_completed_before_the_token_was_seen()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await AttestationOrchestrator.AwaitStageAsync(Task.FromResult(42), cts.Token));

        Assert.Equal(cts.Token, ex.CancellationToken);
    }

    /// <summary>
    /// The counterpart: a completed stage under a token that has not fired
    /// returns its result, so the check above rejects only cancellation.
    /// </summary>
    [Fact]
    public async Task A_completed_stage_under_an_unfired_token_returns_its_result()
    {
        using var cts = new CancellationTokenSource();

        var result = await AttestationOrchestrator.AwaitStageAsync(Task.FromResult(42), cts.Token);

        Assert.Equal(42, result);
    }

    /// <summary>
    /// Abandoning a stage must not leave its eventual fault unobserved: a
    /// stage that throws after the orchestrator stopped waiting for it would
    /// otherwise surface later as a <see cref="TaskScheduler.UnobservedTaskException"/>,
    /// on a finalizer thread, far from the run that caused it.
    ///
    /// Filtered on a marker unique to this test, so another test's unobserved
    /// exception under xunit's parallel execution cannot make this one fail.
    /// </summary>
    [Fact]
    public async Task An_abandoned_stage_that_later_throws_is_observed()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var marker = $"abandoned-stage-{Guid.NewGuid():N}";

        var runtime = new FakeRuntime();
        runtime.ExecuteFunc = async (_, _, _) =>
        {
            await Task.Delay(100, CancellationToken.None);
            throw new InvalidOperationException(marker);
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));

        var unobserved = false;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(inner => inner.Message == marker))
            {
                unobserved = true;
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    async () => await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 }, cancellationToken: cts.Token));
            }

            // Let the abandoned stage fault, then force its task's finalizer.
            for (var i = 0; i < 5 && !unobserved; i++)
            {
                await Task.Delay(100);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.False(unobserved, "the abandoned stage's exception surfaced as an unobserved task exception");
    }

    [Fact]
    public async Task Receipt_missing_declared_field_is_not_displayable()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" })), // 'result' missing
        });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.False(outcome.ReceiptShapeOk);
        Assert.False(outcome.Displayable);
        Assert.Null(outcome.Verdict); // not attested: shape check happens before attest
        Assert.Contains(outcome.Reasons, r => r.Contains("result"));
    }

    [Fact]
    public async Task Missing_required_parameter_fails_before_binding()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("year"));
        Assert.Null(outcome.Receipt); // never reached bind/execute
    }

    [Fact]
    public async Task Unregistered_runtime_reports_reason()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var outcome = await new AttestationOrchestrator(new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>()))
            .RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("runtime"));
    }

    [Fact]
    public async Task Stale_concept_gated_under_strict_policy()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nstale_after: 2025-01-01\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle: Bundle.Load(tmp.Path), conceptId: ConceptId.Parse("c/rev"),
            parameterValues: new Dictionary<string, object?>(), policy: StalePolicy.Strict);
        Assert.Equal(StaleState.Stale, outcome.Stale);
        Assert.False(outcome.Displayable);
        Assert.True(outcome.Verdict!.Value.Passed); // attested fine; only the staleness gate blocks display
        Assert.Contains(outcome.Reasons, r => r.Contains("stale"));
    }

    [Fact]
    public async Task Fresh_concept_is_not_gated_by_stale_after()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nstale_after: 2099-01-01\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"),
            new Dictionary<string, object?>(), policy: StalePolicy.Strict);
        Assert.Equal(StaleState.Fresh, outcome.Stale);
        Assert.True(outcome.Displayable);
    }

    /// <summary>
    /// Regression test for the §5 bug this branch fixes, at §10.6's gate.
    /// <see cref="Stale_concept_gated_under_strict_policy"/> above uses the
    /// legacy date-only <c>2025-01-01</c>, which the pre-fix <c>Lifecycle</c>
    /// parsed too (<c>DateOnly.TryParseExact("yyyy-MM-dd")</c>) — so it would
    /// stay green even if the gate regressed to that parser. A §5-conformant
    /// instant is what it could not read: <c>StaleAfter</c> came back null,
    /// <c>ComputeStale</c> returned <see cref="StaleState.Unknown"/>, and
    /// <c>StalePolicy.Strict</c> admits a null <c>StaleAfter</c> — so a concept
    /// six months past its expiry was attested and displayed. This test fails
    /// on the pre-fix parser and passes on the shipped one.
    /// </summary>
    [Fact]
    public async Task Stale_concept_with_a_conformant_instant_stale_after_is_gated_under_strict_policy()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nstale_after: 2025-06-30T14:00:00Z\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle: Bundle.Load(tmp.Path), conceptId: ConceptId.Parse("c/rev"),
            parameterValues: new Dictionary<string, object?>(), policy: StalePolicy.Strict);
        Assert.Equal(StaleState.Stale, outcome.Stale);
        Assert.False(outcome.Displayable);
        Assert.True(outcome.Verdict!.Value.Passed); // attested fine; only the staleness gate blocks display
        Assert.Contains(outcome.Reasons, r => r.Contains("stale"));
    }

    /// <summary>
    /// A test-only mutable clock. <see cref="FixedClock"/> is immutable by design (see its
    /// own doc comment), so pinning G2's release-time staleness read needs a clock a test
    /// can advance mid-run, e.g. from inside a stage delegate. <see cref="Now"/> is guarded by
    /// a lock rather than a plain auto-property: <see cref="Staleness_crossed_on_the_pool_hopped_stage_is_still_caught_at_release"/>
    /// below mutates it from a stage running on G1's <c>Task.Run</c> hop while <see cref="AttestationOrchestrator.RunAsync"/>
    /// later reads it back from a different thread, and the lock rules out a torn or stale
    /// read of this struct across that hop rather than relying on reasoning about
    /// <see cref="Task"/> happens-before semantics.
    /// </summary>
    private sealed class SteppingClock : IOkfClock
    {
        private readonly object _gate = new();
        private DateTimeOffset _now;

        public SteppingClock(DateTimeOffset now) => _now = now;

        public DateTimeOffset Now
        {
            get { lock (_gate) { return _now; } }
            set { lock (_gate) { _now = value; } }
        }
    }

    /// <summary>
    /// G2 regression (§5.5): <c>RunAsync</c> used to read <c>_clock.Now</c> once before
    /// binding and reuse that instant after every stage, so a run started one second
    /// before <c>stale_after</c> whose stages crossed it was still released <c>Fresh</c>
    /// and displayable. The fix re-reads the clock immediately before the gate (step 9),
    /// so staging time that crosses <c>stale_after</c> is caught at release. The attester
    /// — the last stage before the gate — advances the clock in place of a slow stage.
    /// </summary>
    [Fact]
    public async Task Staleness_crossed_during_a_stage_is_caught_at_release_not_at_run_start()
    {
        using var tmp = new TempDir();
        var staleAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tmp.Write("c/rev.md",
            $"---\ntype: Attested Computation\nruntime: bigquery\nstale_after: {staleAfter:yyyy-MM-ddTHH:mm:ssZ}\n---\n# Computation\n\n```\nX\n```\n");
        var clock = new SteppingClock(staleAfter - TimeSpan.FromSeconds(1));
        var runtime = FakeRuntime.Passing();
        runtime.AttestFunc = (_, _) =>
        {
            clock.Now = staleAfter + TimeSpan.FromSeconds(1);
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: clock);

        var outcome = await orch.RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>(), policy: StalePolicy.Strict);

        Assert.Equal(StaleState.Stale, outcome.Stale);
        Assert.False(outcome.Displayable);
    }

    /// <summary>
    /// Companion to <see cref="Staleness_crossed_during_a_stage_is_caught_at_release_not_at_run_start"/>
    /// that actually exercises G1's <c>Task.Run</c> hop instead of asserting cross-thread
    /// visibility is safe by reasoning about <see cref="Task"/> semantics alone. That test (and
    /// the other two G2 staleness tests) call <c>RunAsync</c> with the default
    /// <c>CancellationToken.None</c>, for which <c>RunStageAsync</c>'s
    /// <c>cancellationToken.CanBeCanceled</c> is <see langword="false"/> and every stage --
    /// including the one that mutates the clock -- runs synchronously in-line on the calling
    /// thread, never via <c>Task.Run</c>. Here the token comes from a live, never-cancelled
    /// <see cref="CancellationTokenSource"/>, so <c>CanBeCanceled</c> is <see langword="true"/>
    /// and the attester genuinely hops onto the thread pool before mutating the clock; the
    /// assertion on <c>hopped</c> (a different managed thread id than the caller's) makes the
    /// test fail loudly rather than silently stay on the fast path if that hop stops happening.
    /// </summary>
    [Fact]
    public async Task Staleness_crossed_on_the_pool_hopped_stage_is_still_caught_at_release()
    {
        using var tmp = new TempDir();
        var staleAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tmp.Write("c/rev.md",
            $"---\ntype: Attested Computation\nruntime: bigquery\nstale_after: {staleAfter:yyyy-MM-ddTHH:mm:ssZ}\n---\n# Computation\n\n```\nX\n```\n");
        var clock = new SteppingClock(staleAfter - TimeSpan.FromSeconds(1));
        var runtime = FakeRuntime.Passing();
        var callerThreadId = Environment.CurrentManagedThreadId;
        var hopped = false;
        runtime.AttestFunc = (_, _) =>
        {
            hopped = Environment.CurrentManagedThreadId != callerThreadId;
            clock.Now = staleAfter + TimeSpan.FromSeconds(1);
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: clock);
        // Live and cancelable, but never cancelled: forces RunStageAsync's
        // cancellationToken.CanBeCanceled to true so every stage takes the Task.Run hop,
        // without the run itself ever being cancelled.
        using var cts = new CancellationTokenSource();

        var outcome = await orch.RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>(),
            policy: StalePolicy.Strict, cancellationToken: cts.Token);

        Assert.True(hopped, "the attester stage ran on the caller's own thread -- this test would pass even if a regression broke cross-thread visibility of the release-time clock read");
        Assert.Equal(StaleState.Stale, outcome.Stale);
        Assert.False(outcome.Displayable);
    }

    /// <summary>
    /// Counterpart to <see cref="Staleness_crossed_during_a_stage_is_caught_at_release_not_at_run_start"/>:
    /// a clock left before <c>stale_after</c> at release time still reports <c>Fresh</c> and
    /// displayable, so the re-read is not itself a source of false staleness.
    /// </summary>
    [Fact]
    public async Task Staleness_not_crossed_by_release_time_remains_fresh_and_displayable()
    {
        using var tmp = new TempDir();
        var staleAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tmp.Write("c/rev.md",
            $"---\ntype: Attested Computation\nruntime: bigquery\nstale_after: {staleAfter:yyyy-MM-ddTHH:mm:ssZ}\n---\n# Computation\n\n```\nX\n```\n");
        var clock = new SteppingClock(staleAfter - TimeSpan.FromSeconds(1));
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var orch = new AttestationOrchestrator(reg, clock: clock);

        var outcome = await orch.RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>(), policy: StalePolicy.Strict);

        Assert.Equal(StaleState.Fresh, outcome.Stale);
        Assert.True(outcome.Displayable);
    }

    /// <summary>
    /// The fix touches every <c>Fail(..., stale, ...)</c> site after a stage, not only the
    /// success path: a stage that crosses <c>stale_after</c> and then fails must still
    /// report <see cref="StaleState.Stale"/> on the resulting non-displayable outcome,
    /// evaluated at the point that failure outcome is built rather than at run start.
    /// </summary>
    [Fact]
    public async Task A_stage_failure_after_the_clock_crossed_stale_after_reports_stale()
    {
        using var tmp = new TempDir();
        var staleAfter = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tmp.Write("c/rev.md",
            $"---\ntype: Attested Computation\nruntime: bigquery\nstale_after: {staleAfter:yyyy-MM-ddTHH:mm:ssZ}\n---\n# Computation\n\n```\nX\n```\n");
        var clock = new SteppingClock(staleAfter - TimeSpan.FromSeconds(1));
        var runtime = new FakeRuntime();
        runtime.ExecuteFunc = (_, _, _) =>
        {
            clock.Now = staleAfter + TimeSpan.FromSeconds(1);
            throw new InvalidOperationException("boom");
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: clock);

        var outcome = await orch.RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());

        Assert.False(outcome.Displayable);
        Assert.Equal(StaleState.Stale, outcome.Stale);
    }

    [Fact]
    public async Task A_diagnostic_exception_reports_its_message_a_foreign_one_only_its_type()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);   // the class's existing fixture helper (runtime "bigquery", parameter `year`)
        var diagnostic = FakeRuntime.Passing();
        diagnostic.ExecuteFunc = (_, _, _) => throw new AttestationDiagnosticException("receipt was not a JSON object");
        var foreign = FakeRuntime.Passing();
        foreign.ExecuteFunc = (_, _, _) => throw new InvalidOperationException("Host=db;Password=hunter2");

        static AttestationOrchestrator Orch(FakeRuntime r) =>
            new(new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = r }), clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var values = new Dictionary<string, object?> { ["year"] = 2026 };
        var a = await Orch(diagnostic).RunAsync(bundle, id, values);
        var b = await Orch(foreign).RunAsync(bundle, id, values);

        Assert.Contains("executor threw: AttestationDiagnosticException: receipt was not a JSON object", a.Reasons);
        Assert.Contains("executor threw: InvalidOperationException", b.Reasons);
        Assert.DoesNotContain(b.Reasons, r => r.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Executor_exception_is_captured_not_thrown()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.ThrowingExecutor() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.False(outcome.Displayable);
        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.Receipt);
    }

    [Fact]
    public async Task Attest_negative_verdict_not_displayable()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(
                receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }),
                verdict: new AttestationVerdict(false, "sql does not match sanctioned computation")),
        });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.True(outcome.ReceiptShapeOk);
        Assert.False(outcome.Verdict!.Value.Passed);
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("sanctioned computation"));
    }

    [Fact]
    public async Task File_based_computation_resolved_and_read()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\n" +
            "executor: { resource: references/run.md, receipt: [job_id] }\n---\n");
        // Laid out as Appendix A does: the concept sits in a subdirectory and its
        // bare `computation` path resolves from the BUNDLE ROOT, not from "c/".
        tmp.Write("references/revenue.sql", "SELECT revenue FROM t;\n");
        string? capturedText = null;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }));
        runtime.BindFunc = (contract, computation, values, ct) =>
        {
            capturedText = computation.InlineCode;
            return ValueTask.FromResult(new BoundComputation(contract.Runtime ?? "bigquery", computation.InlineCode, null, values));
        };
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());
        Assert.True(outcome.Displayable);
        Assert.Equal("SELECT revenue FROM t;\n", capturedText);
    }

    [Fact]
    public async Task Unresolvable_computation_file_fails_before_binding()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/missing.sql\n---\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        // Asserts the RESOLUTION arm specifically. "missing.sql" alone also
        // appears in the read/decode arm's message, so a bare substring match
        // cannot tell the two failures apart -- which is how a layout change
        // once left the two decode tests below green while they silently
        // stopped reaching the decoder at all.
        Assert.Contains(outcome.Reasons, r => r.Contains("computation file 'references/missing.sql' could not be resolved (Missing)", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Reasons, r => r.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreadable_computation_file_is_captured_not_thrown()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\n---\n");
        // At the BUNDLE ROOT, because a bare `computation` path resolves from
        // there (§6.2) -- same layout as File_based_computation_resolved_and_read,
        // so the only thing that differs between the two is the file's BYTES,
        // which is the variable under test here.
        var sqlPath = System.IO.Path.Combine(tmp.Path, "references", "revenue.sql");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sqlPath)!);
        // Lone UTF-8 continuation bytes with no leading byte: invalid UTF-8 that
        // isn't also a recognized BOM prefix (unlike e.g. 0xFF 0xFE), so it reliably
        // trips OkfEncodings.Strict's decoder instead of being silently reinterpreted
        // as a different encoding by File.ReadAllText's BOM auto-detection.
        File.WriteAllBytes(sqlPath, [0x80, 0x81, 0x82]);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        // Pinned to the READ arm, and to the decoder exception specifically:
        // "revenue.sql" on its own also matches the resolution arm's
        // "could not be resolved (Missing)", so the old substring assertion
        // stayed green when the file stopped resolving and the decoder was
        // never reached.
        Assert.Contains(outcome.Reasons, r => r.Contains("could not be read: DecoderFallbackException", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Reasons, r => r.Contains("could not be resolved", StringComparison.Ordinal));
    }

    /// <summary>
    /// Regression test for the BOM-sniff hole in <see cref="Bundle.ReadResourceText"/>:
    /// the old implementation was <c>File.ReadAllText(absolutePath, OkfEncodings.Strict)</c>,
    /// and <see cref="File.ReadAllText(string, System.Text.Encoding)"/> hardcodes
    /// <c>detectEncodingFromByteOrderMarks: true</c> regardless of the encoding
    /// passed in -- so a UTF-16-BOM-prefixed file was silently reinterpreted as
    /// UTF-16 instead of tripping the strict UTF-8 decoder, the exact hole
    /// <see cref="OkfEncodings.Strict"/> exists to prevent. 0xFF 0xFE is a UTF-16 LE
    /// BOM; the trailing 0x00 0xD8 is an unpaired low/high surrogate byte pair that
    /// decodes without throwing under .NET's default (replacement-character) UTF-16
    /// handling, so under the old code this file was read as a garbage-but-non-throwing
    /// string and the run proceeded to a normal, displayable outcome. Under strict
    /// UTF-8 (no BOM sniffing), 0xFF and 0xFE are never valid UTF-8 lead bytes, so the
    /// fixed <see cref="Bundle.ReadResourceText"/> must trip the decoder, which the
    /// orchestrator's existing guarded read (<see cref="AttestationOrchestrator.RunAsync"/>'s
    /// file-computation step) catches as a non-displayable outcome -- never an
    /// unhandled exception.
    /// </summary>
    [Fact]
    public async Task Utf16_bom_prefixed_computation_file_trips_strict_decoder()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: references/revenue.sql\n---\n");
        // At the BUNDLE ROOT: a bare `computation` path resolves from there (§6.2).
        var sqlPath = System.IO.Path.Combine(tmp.Path, "references", "revenue.sql");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sqlPath)!);
        File.WriteAllBytes(sqlPath, new byte[] { 0xFF, 0xFE, 0x00, 0xD8 });
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        // The regression this test exists for is a SILENT SUCCESS (the BOM was
        // sniffed, the file decoded as UTF-16, the run proceeded), so the
        // assertion has to name the decoder failure. `Displayable == false`
        // plus a shared "revenue.sql" substring is satisfied by a file that
        // never resolved, which proves nothing about BOM sniffing.
        Assert.Contains(outcome.Reasons, r => r.Contains("could not be read: DecoderFallbackException", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Reasons, r => r.Contains("could not be resolved", StringComparison.Ordinal));
    }

    /// <summary>
    /// Step 2's <c>default</c> arm (<see cref="AttestationOrchestrator.RunAsync"/>):
    /// a concept can decline into neither switch case above -- no
    /// <c>computation:</c> frontmatter path AND no inline <c># Computation</c>
    /// fence in the body -- and must fail before the runtime is even
    /// resolved, with the orchestrator's dedicated "has no computation"
    /// reason rather than some other generic message.
    /// </summary>
    [Fact]
    public async Task Neither_inline_nor_file_computation_is_not_displayable()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n---\n" +
            "Just prose -- no `# Computation` fence and no `computation:` path.\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("has no computation"));
    }

    [Fact]
    public async Task Not_found_concept_is_not_displayable()
    {
        using var tmp = new TempDir();
        var (bundle, _) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, ConceptId.Parse("c/nope"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.NotEmpty(outcome.Reasons);
    }

    [Fact]
    public async Task Non_attested_computation_concept_is_not_displayable()
    {
        using var tmp = new TempDir();
        tmp.Write("c/other.md", "---\ntype: Metric\n---\n# Body\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>());
        var outcome = await new AttestationOrchestrator(reg).RunAsync(Bundle.Load(tmp.Path), ConceptId.Parse("c/other"), new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.NotEmpty(outcome.Reasons);
    }

    /// <summary>
    /// P2b regression: <see cref="AttestationOrchestrator.RunAsync"/> is a
    /// direct public API (not just reached through the agent wrapper, which
    /// already normalizes a null parameter dictionary before calling in), so
    /// it must uphold its own errors-as-data promise for a caller who passes
    /// <c>null!</c> directly -- never an unhandled <see cref="NullReferenceException"/>.
    /// Before the fix, the required-parameter gate dereferenced
    /// <c>parameterValues</c> (<c>.ContainsKey</c>) before any guarded host
    /// call, so a null dictionary threw immediately. <see cref="InlineComputation"/>
    /// declares a required "year" parameter, so a normalized empty dictionary
    /// must still surface the normal missing-required-parameter outcome.
    /// </summary>
    [Fact]
    public async Task Null_parameter_dictionary_does_not_throw_and_reports_missing_required_parameter()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, null!);
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("year"));
        Assert.Null(outcome.Receipt); // never reached bind/execute
    }

    private static (Bundle, ConceptId) InlineComputationWithAttesterFile(TempDir tmp, string attesterBody)
    {
        // Laid out as §6.2 resolves it and as Appendix A shows it: the concept sits in a
        // subdirectory, its bare `attester.resource` resolves from the BUNDLE ROOT, and the
        // attester file lives there -- the shape bundles/acme_retail/ uses for its shared
        // attesters. Co-locating the script with the concept needs the explicit `./` form
        // instead, which Attester_resource_can_be_concept_relative below covers.
        tmp.Write("att.py", attesterBody);
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id, result] }\n" +
            "attester: { resource: att.py }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        return (Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"));
    }

    [Fact]
    public async Task AttesterSourceText_carries_the_resolved_attester_script()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputationWithAttesterFile(tmp, "def attest(**_):\n    return {}\n");

        AttestationContext? captured = null;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        runtime.AttestFunc = (ctx, _) =>
        {
            captured = ctx;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });

        Assert.NotNull(captured);
        Assert.Equal("def attest(**_):\n    return {}\n", captured!.AttesterSourceText);
    }

    /// <summary>
    /// The other §6.2 base, through the one consumer that READS and then hands off what it
    /// resolves: an `attester.resource` written `./att.py` resolves beside the concept, not
    /// from the bundle root. Pinned with a decoy at the root, which is what the bare form
    /// would have found -- this interaction is what broke two tests when the §6.2 change
    /// landed, and neither of them could tell the two bases apart.
    /// </summary>
    [Fact]
    public async Task Attester_resource_can_be_concept_relative()
    {
        using var tmp = new TempDir();
        tmp.Write("att.py", "def attest(**_):\n    return {}  # root decoy\n");
        tmp.Write("c/att.py", "def attest(**_):\n    return {}  # beside the concept\n");
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id, result] }\n" +
            "attester: { resource: ./att.py }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        var bundle = Bundle.Load(tmp.Path);

        AttestationContext? captured = null;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        runtime.AttestFunc = (ctx, _) =>
        {
            captured = ctx;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        await orch.RunAsync(bundle, ConceptId.Parse("c/rev"), new Dictionary<string, object?> { ["year"] = 2026 });

        Assert.NotNull(captured);
        Assert.Equal("def attest(**_):\n    return {}  # beside the concept\n", captured!.AttesterSourceText);
    }

    [Fact]
    public async Task Missing_attester_resource_file_fails_early_without_calling_the_attester()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputationWithAttesterFile(tmp, "def attest(**_):\n    return {}\n");
        // Overwrite with a concept whose attester.resource does not exist on disk.
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id, result] }\n" +
            "attester: { resource: does-not-exist.py }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        var bundle2 = Bundle.Load(tmp.Path);

        var attested = false;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        runtime.AttestFunc = (_, _) =>
        {
            attested = true;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle2, id, new Dictionary<string, object?> { ["year"] = 2026 });

        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("attester resource", StringComparison.Ordinal));
        Assert.False(attested, "the attester ran despite its own resource being unresolvable");
    }

    [Fact]
    public async Task Absent_attester_declaration_yields_null_source_text_not_a_failure()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { receipt: [job_id, result] }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        var bundle = Bundle.Load(tmp.Path);
        var id = ConceptId.Parse("c/rev");

        AttestationContext? captured = null;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1", ["result"] = 42 }));
        runtime.AttestFunc = (ctx, _) =>
        {
            captured = ctx;
            return ValueTask.FromResult(new AttestationVerdict(true, null));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new DateOnly(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });

        Assert.True(outcome.Displayable);
        Assert.Null(captured!.AttesterSourceText);
    }
}
