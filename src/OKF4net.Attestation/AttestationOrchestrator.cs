// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;

namespace OKF4net.Attestation;

/// <summary>
/// Runs the §10.5 attested-computation workflow for one concept: load →
/// resolve computation → resolve runtime → validate parameters → bind →
/// execute → validate receipt shape → attest → gate on verdict + staleness.
///
/// Errors-as-data: every expected failure (concept not found, wrong type,
/// unresolved computation, unregistered runtime, missing required
/// parameter, malformed receipt shape) produces a non-<see cref="AttestationOutcome.Displayable"/>
/// outcome with <see cref="AttestationOutcome.Reasons"/> — never an
/// exception. Exceptions thrown by the host-supplied binder, executor, or
/// attester are caught and surfaced via <see cref="AttestationOutcome.Error"/>,
/// never propagated. The orchestrator never writes to the bundle (§10.6:
/// attestation is per-run, not stored).
/// </summary>
public sealed class AttestationOrchestrator
{
    /// <summary>Message on the <see cref="OperationCanceledException"/> synthesised when a caller's cancellation arrives wrapped.</summary>
    private const string CancelledMessage = "The attested computation was cancelled.";

    private readonly IAttestationRuntimeRegistry _runtimes;
    private readonly IOkfClock _clock;
    private readonly StalePolicy _defaultPolicy;

    /// <summary>
    /// Creates an orchestrator over <paramref name="runtimes"/>.
    /// </summary>
    /// <param name="runtimes">Resolves an <see cref="IAttestationRuntime"/> by the concept's <c>runtime</c> field.</param>
    /// <param name="clock">Supplies the instant staleness gating is evaluated at. Defaults to <see cref="SystemClock"/>.</param>
    /// <param name="defaultPolicy">The gating policy used when <see cref="RunAsync"/> is not given one. Defaults to <see cref="StalePolicy.Use"/>.</param>
    public AttestationOrchestrator(IAttestationRuntimeRegistry runtimes, IOkfClock? clock = null, StalePolicy? defaultPolicy = null)
    {
        _runtimes = runtimes;
        _clock = clock ?? new SystemClock();
        _defaultPolicy = defaultPolicy ?? StalePolicy.Use;
    }

    /// <summary>
    /// Runs the §10.5 workflow for the <see cref="AttestedComputationContract"/>
    /// declared by <paramref name="conceptId"/> in <paramref name="bundle"/>,
    /// binding <paramref name="parameterValues"/> and gating the result on
    /// <paramref name="policy"/> (or the constructor's default policy).
    ///
    /// <para>
    /// When a host-plugged stage throws, the resulting
    /// <see cref="AttestationOutcome.Reasons"/> entry names the stage and the
    /// exception TYPE ("executor threw: TimeoutException"), never its message.
    /// The exception itself is on <see cref="AttestationOutcome.Error"/>, which
    /// is where a host reads the detail. The split is deliberate: reasons are
    /// rendered into an agent's context by <c>OkfBundleTools</c>, and the
    /// message on an exception from code this library does not control can
    /// carry a connection string, a query, or the data that broke it. Nothing
    /// is lost to the host; what changes is what crosses into a model's
    /// context.
    /// </para>
    ///
    /// <para>
    /// The body reads as the numbered §10.5 steps, in order. Two of them —
    /// resolving the computation and attesting — live in
    /// <see cref="TryResolveComputation"/> and <see cref="AttestAsync"/>
    /// because they branch several ways with I/O of their own, and inlining
    /// them buried the pipeline they are steps of. Everything else is here on
    /// purpose: the sequence IS the specification, and hiding it behind
    /// helpers would cost more than it saved.
    /// </para>
    /// </summary>
    /// <param name="bundle">The bundle to load the concept from.</param>
    /// <param name="conceptId">The attested-computation concept to run.</param>
    /// <param name="parameterValues">The parameter values supplied for this run (§10.3: values only, never computation code).</param>
    /// <param name="policy">The staleness gating policy for this run; defaults to the constructor's <c>defaultPolicy</c>.</param>
    /// <param name="cancellationToken">A token to cancel binding/execution/attestation.</param>
    public async ValueTask<AttestationOutcome> RunAsync(
        Bundle bundle,
        ConceptId conceptId,
        IReadOnlyDictionary<string, object?> parameterValues,
        StalePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        // Errors-as-data (see class remarks) extends to the parameters argument
        // itself: a caller passing null must get the normal "missing required
        // parameter" outcome below, never a NullReferenceException.
        parameterValues ??= new Dictionary<string, object?>();

        // Step 1: load the concept; it must exist and be an Attested Computation.
        var concept = bundle.Get(conceptId);
        if (concept is null || !concept.Document.Frontmatter.IsAttestedComputation)
        {
            return Fail($"concept '{conceptId}' was not found or is not an attested computation");
        }

        var frontmatter = concept.Document.Frontmatter;
        var contract = frontmatter.ComputationContract;

        // Step 2: resolve the sanctioned computation (inline fence, or file via §6.2 path-safe resolution).
        if (!TryResolveComputation(bundle, concept, out var resolved, out var resolutionFailure))
        {
            return resolutionFailure;
        }

        // Step 3: resolve the runtime.
        if (string.IsNullOrEmpty(contract.Runtime) || !_runtimes.TryGet(contract.Runtime, out var runtime) || runtime is null)
        {
            return Fail($"no runtime configured for '{contract.Runtime}'");
        }

        // Step 4: every required parameter must be supplied; extra values are ignored (§10.3).
        var missingParameters = contract.Parameters
            .Where(p => p.Required && !parameterValues.ContainsKey(p.Name))
            .Select(p => $"missing required parameter '{p.Name}'")
            .ToArray();
        if (missingParameters.Length > 0)
        {
            return Fail(missingParameters);
        }

        var now = _clock.Now;
        var stale = ComputeStale(frontmatter.Lifecycle, now);
        var staleAdmitted = (policy ?? _defaultPolicy).Admits(frontmatter.Lifecycle, now);

        // Step 5: bind.
        //
        // Checked here, and again before each stage below, because handing the
        // token to a host stage is not the same as observing it. A stage whose
        // underlying client predates cancellation support — or simply forgets —
        // ignores its token, and an already-cancelled run then executed every
        // stage and could return a DISPLAYABLE success: for §10 that means a
        // computation actually ran, possibly against a live warehouse, after
        // the caller had withdrawn.
        cancellationToken.ThrowIfCancellationRequested();

        BoundComputation bound;
        try
        {
            bound = await runtime.Binder.BindAsync(contract, resolved, parameterValues, cancellationToken).ConfigureAwait(false);
        }
        // Cancellation is control flow, not data: errors-as-data is the contract
        // for FAILURES, and a cancellation the CALLER asked for is not one.
        // Caught by a bare `catch (Exception)` it became a business outcome, so
        // a caller that cancelled got a normal-looking result and could not
        // tell "the stage failed" from "I asked it to stop".
        //
        // But the exception TYPE alone does not identify that: HttpClient
        // raises TaskCanceledException on its own request timeout with nobody's
        // token cancelled, and a host executor calling one is the ordinary
        // case. Filtering on the type alone let that escape as a raw exception
        // — a downstream timeout is a stage failure like any other. The token's
        // state is what actually distinguishes the two. Same filter on all
        // three stages below.
        // Caller cancellation arriving WRAPPED is still cancellation, but it has
        // to reach the caller in the shape they catch. A direct
        // OperationCanceledException falls through both clauses uncaught, which
        // keeps its original stack trace.
        catch (AggregateException e) when (IsCallerCancellation(e, cancellationToken))
        {
            throw new OperationCanceledException(CancelledMessage, e, cancellationToken);
        }
        catch (Exception e) when (!IsCallerCancellation(e, cancellationToken))
        {
            return Fail([$"binder threw: {e.GetType().Name}"], stale, e);
        }

        // Step 6: execute.
        cancellationToken.ThrowIfCancellationRequested();

        Receipt receipt;
        try
        {
            receipt = await runtime.Executor.ExecuteAsync(bound, contract, cancellationToken).ConfigureAwait(false);
        }
        // Caller cancellation arriving WRAPPED is still cancellation, but it has
        // to reach the caller in the shape they catch. A direct
        // OperationCanceledException falls through both clauses uncaught, which
        // keeps its original stack trace.
        catch (AggregateException e) when (IsCallerCancellation(e, cancellationToken))
        {
            throw new OperationCanceledException(CancelledMessage, e, cancellationToken);
        }
        catch (Exception e) when (!IsCallerCancellation(e, cancellationToken))
        {
            return Fail([$"executor threw: {e.GetType().Name}"], stale, e);
        }

        // Step 7: validate the receipt shape (no declared executor.receipt fields ⇒ trivially ok).
        var declaredFields = contract.Executor?.Receipt ?? [];
        var missingFields = declaredFields.Where(f => !receipt.Fields.ContainsKey(f)).ToArray();
        var receiptShapeOk = missingFields.Length == 0;

        var reasons = new List<string>();
        if (!receiptShapeOk)
        {
            reasons.Add($"receipt is missing declared field(s): {string.Join(", ", missingFields)}");
        }

        // Step 8: attest, only if the receipt shape is trustworthy.
        AttestationVerdict? verdict = null;
        Exception? error = null;
        if (receiptShapeOk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var context = new AttestationContext(contract, resolved, bound, parameterValues, receipt);
            (verdict, error) = await AttestAsync(runtime, context, reasons, cancellationToken).ConfigureAwait(false);
        }

        // Step 9/10: gate on staleness and aggregate the outcome.
        if (!staleAdmitted)
        {
            reasons.Add("concept is stale and the gating policy does not admit it");
        }

        var displayable = receiptShapeOk && verdict is { Passed: true } && staleAdmitted;
        return new AttestationOutcome(displayable, verdict, receipt, receiptShapeOk, stale, reasons, error);
    }

    /// <summary>
    /// §10.5 step 8: runs the attester and reports what it decided.
    ///
    /// Appends to <paramref name="reasons"/> rather than returning a third
    /// value, because a non-passing verdict and a throwing attester contribute
    /// the same kind of entry to the same list the caller is already building.
    /// Behaviour is unchanged from when this was inline in <see cref="RunAsync"/>,
    /// including the exact reason wording and the deliberate choice to report
    /// the exception TYPE rather than its message.
    /// </summary>
    /// <param name="runtime">The resolved runtime whose attester to invoke.</param>
    /// <param name="context">The §10.5 attestation context for this run.</param>
    /// <param name="reasons">The outcome's reason list, appended to in place.</param>
    /// <param name="cancellationToken">Cancels the attester; an <see cref="OperationCanceledException"/> propagates rather than becoming an outcome.</param>
    private static async ValueTask<(AttestationVerdict? Verdict, Exception? Error)> AttestAsync(
        IAttestationRuntime runtime,
        AttestationContext context,
        List<string> reasons,
        CancellationToken cancellationToken)
    {
        try
        {
            var verdict = await runtime.Attester.AttestAsync(context, cancellationToken).ConfigureAwait(false);
            if (verdict is { Passed: false } failed)
            {
                reasons.Add(string.IsNullOrEmpty(failed.Detail) ? "attestation did not pass" : $"attestation did not pass: {failed.Detail}");
            }

            return (verdict, null);
        }
        // Caller cancellation arriving WRAPPED is still cancellation, but it has
        // to reach the caller in the shape they catch. A direct
        // OperationCanceledException falls through both clauses uncaught, which
        // keeps its original stack trace.
        catch (AggregateException e) when (IsCallerCancellation(e, cancellationToken))
        {
            throw new OperationCanceledException(CancelledMessage, e, cancellationToken);
        }
        catch (Exception e) when (!IsCallerCancellation(e, cancellationToken))
        {
            reasons.Add($"attester threw: {e.GetType().Name}");
            return (null, e);
        }
    }

    /// <summary>
    /// §10.5 step 2: resolves the concept's sanctioned computation — the inline
    /// <c># Computation</c> fence, or the <c>computation:</c> file read through
    /// §6.2 path-safe resolution.
    ///
    /// Extracted from <see cref="RunAsync"/> purely to keep that method
    /// readable: it is one linear pipeline, and this was the one step that
    /// branched three ways with I/O of its own inside. Behaviour is unchanged,
    /// including which failures are reported and their exact wording.
    /// </summary>
    /// <param name="bundle">The bundle the concept was loaded from.</param>
    /// <param name="concept">The attested-computation concept.</param>
    /// <param name="resolved">The resolved computation, when this returns <see langword="true"/>.</param>
    /// <param name="failure">The non-displayable outcome to return, when this returns <see langword="false"/>.</param>
    private static bool TryResolveComputation(
        Bundle bundle,
        Concept concept,
        out SanctionedComputation resolved,
        [NotNullWhen(false)] out AttestationOutcome? failure)
    {
        var computation = concept.Document.Computation();
        failure = null;

        switch (computation.Source)
        {
            case ComputationSource.Inline when !string.IsNullOrEmpty(computation.InlineCode):
                resolved = computation;
                return true;

            case ComputationSource.File when !string.IsNullOrEmpty(computation.Path):
                resolved = default;
                if (!bundle.TryResolveResource(concept, computation.Path, out var absolutePath, out var status)
                    || status != ResourceResolutionStatus.Resolved)
                {
                    failure = Fail($"computation file '{computation.Path}' could not be resolved ({status})");
                    return false;
                }

                string text;
                try
                {
                    text = bundle.ReadResourceText(absolutePath!);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
                {
                    // The exception TYPE, for the same reason as the stage
                    // failures above: this reason string is rendered into a
                    // model's context, and an IOException's own message carries
                    // the absolute host path. computation.Path is bundle-
                    // relative and safe to name; e.Message is not.
                    failure = Fail($"computation file '{computation.Path}' could not be read: {e.GetType().Name}");
                    return false;
                }

                resolved = new SanctionedComputation(ComputationSource.File, text, computation.Path);
                return true;

            default:
                resolved = default;
                failure = Fail("attested computation has no computation (neither an inline `# Computation` fence nor a `computation:` path)");
                return false;
        }
    }

    /// <summary>
    /// Whether <paramref name="e"/> represents the CALLER's cancellation, which
    /// propagates, rather than a stage failure, which becomes an outcome.
    ///
    /// Both halves are needed. The token must actually be cancelled, because
    /// the exception type alone does not mean the caller withdrew: HttpClient
    /// raises <see cref="TaskCanceledException"/> on its own request timeout
    /// with nobody's token cancelled, and a host executor calling one is the
    /// ordinary case — that is a stage failure like any other.
    ///
    /// And the cancellation has to be recognised however it is packaged. A
    /// stage that blocks on a cancelled task with <c>.Result</c> or
    /// <c>.Wait()</c> surfaces an <see cref="AggregateException"/> wrapping the
    /// <see cref="OperationCanceledException"/>, which is a realistic shape at
    /// a plugin boundary; matching only the top-level type turned the caller's
    /// own cancellation into an ordinary non-displayable outcome.
    /// <see cref="AggregateException.Flatten"/> handles nesting.
    /// </summary>
    /// <param name="e">The exception a host stage threw.</param>
    /// <param name="cancellationToken">The token this run was given.</param>
    private static bool IsCallerCancellation(Exception e, CancellationToken cancellationToken)
    {
        if (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return e switch
        {
            OperationCanceledException => true,
            AggregateException aggregate => aggregate.Flatten().InnerExceptions.Any(inner => inner is OperationCanceledException),
            _ => false,
        };
    }

    private static AttestationOutcome Fail(string reason, StaleState stale = StaleState.Unknown, Exception? error = null)
        => Fail([reason], stale, error);

    private static AttestationOutcome Fail(IReadOnlyList<string> reasons, StaleState stale = StaleState.Unknown, Exception? error = null)
        => new(false, null, null, false, stale, reasons, error);

    private static StaleState ComputeStale(Lifecycle lifecycle, DateTimeOffset now)
    {
        // A stale_after that is absent *or unparseable* is Unknown, never
        // Fresh: IsStale alone returns false for both, so Fresh would assert a
        // freshness nothing established. This is reporting, not gating -- the
        // gate admits the concept either way (StalePolicy.Admits returns true
        // when StaleAfter is null, under every mode, which
        // StalePolicyTests.A_malformed_stale_after_is_admitted_by_every_mode
        // pins as deliberate: the validator owns that diagnostic, and a policy
        // must not silently drop a concept over an unreadable stamp). What
        // Unknown buys is that a caller inspecting Outcome.Stale can tell "we
        // checked, it is current" from "we could not check", instead of being
        // told the second is the first.
        if (lifecycle.StaleAfter is null)
        {
            return StaleState.Unknown;
        }

        return lifecycle.IsStale(now) ? StaleState.Stale : StaleState.Fresh;
    }
}
