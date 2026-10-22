// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

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
    /// exception TYPE ("executor threw: TimeoutException") — see
    /// <see cref="RunStageAsync{T}"/>'s remarks for the one exception to that
    /// rule. The exception itself is always on <see cref="AttestationOutcome.Error"/>,
    /// which is where a host reads the full detail regardless.
    /// </para>
    ///
    /// <para>
    /// The body reads as the numbered §10.5 steps, in order: step 1 here, step 2 in
    /// <see cref="Capture"/> (the one step that reads sanctioned code, pinned to the
    /// image), steps 3 to 10 in <see cref="RunCapturedAsync"/>, with attestation in
    /// <see cref="AttestAsync"/>. Everything else is inline on purpose: the sequence IS
    /// the specification, and hiding it behind helpers would cost more than it saved.
    /// </para>
    /// <para><b>Fail-closed on the <c>attester.resource</c>.</b> The attester's source is
    /// read <i>before</i> binding or execution, pinned to the version the image was loaded
    /// with, and a concept whose attester does not resolve — missing file, a path that
    /// would escape the bundle, a resource that changed since the load — ends the run with
    /// a non-displayable outcome. Nothing executes. So does a concept that declares no
    /// <c>attester.resource</c>, or declares a URL (D6, spec §5.1): there is nothing to pin,
    /// so the run is refused before the runtime is called, and the host's
    /// <see cref="IAttester"/> never receives a <see langword="null"/>
    /// <see cref="AttestationContext.AttesterSourceText"/>. This is deliberate: an attester
    /// the bundle names but the host cannot read is an attestation that was specified and
    /// then not performed, and §10.6 is about not displaying a figure whose check did not
    /// happen. A host whose <see cref="IAttester"/> attested without a source in the bundle
    /// sees its computations refused until the bundle declares a file attester
    /// (docs/spec-conformance, S10.5-1).</para>
    /// <para>This method is not <c>async</c>: a <see langword="null"/> <paramref name="bundle"/>
    /// (<see cref="ArgumentNullException"/>) and an unexpected exception from <see cref="Capture"/>
    /// (anything other than the I/O and decoding failures it turns into a refusal) are thrown
    /// synchronously instead of faulting the returned <see cref="ValueTask{TResult}"/>; a stage
    /// failure after the capture is still an outcome (errors-as-data).</para>
    /// </summary>
    /// <param name="bundle">The bundle to load the concept from.</param>
    /// <param name="conceptId">The attested-computation concept to run.</param>
    /// <param name="parameterValues">The parameter values supplied for this run (§10.3: values only, never computation code).</param>
    /// <param name="policy">The staleness gating policy for this run; defaults to the constructor's <c>defaultPolicy</c>.</param>
    /// <param name="cancellationToken">A token to cancel binding/execution/attestation.</param>
    public ValueTask<AttestationOutcome> RunAsync(
        Bundle bundle,
        ConceptId conceptId,
        IReadOnlyDictionary<string, object?> parameterValues,
        StalePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        // Step 1: load the concept; it must exist and be an Attested Computation.
        var concept = bundle.Get(conceptId);
        if (concept is null || !concept.Document.Frontmatter.IsAttestedComputation)
        {
            return ValueTask.FromResult(Fail($"concept '{conceptId}' was not found or is not an attested computation"));
        }

        // Step 2: capture the sanctioned code — the computation (inline fence, or the file read
        // through the provider) and the attester's source, pinned to the image (spec §5.4).
        var captured = Capture(bundle, concept);
        return captured.Capture is null
            ? ValueTask.FromResult(captured.Failure!)
            : RunCapturedAsync(bundle, conceptId, parameterValues, captured.Capture, policy, cancellationToken);
    }

    /// <summary>
    /// Captures, once and before any runtime is called, everything a run executes (spec §5.4):
    /// the sanctioned computation — the inline <c># Computation</c> fence, or the
    /// <c>computation:</c> file read through the provider (the key the load surveyed,
    /// <see cref="Bundle.ReadResourceText(BundleKey, BundleObjectVersion?, out BundleObjectVersion)"/>),
    /// pinned to the version the image was loaded with (<see cref="Bundle.ExecutableResources"/>)
    /// — and the attester's source, pinned the same way. Each resource's surveyed state must be
    /// <see cref="ExecutableResourceState.Pinned"/> (spec §5.1), and it is read at that version
    /// only: a file replaced after the load is refused, never run at its new bytes. A version
    /// the caller observed (<paramref name="expectedComputationFileVersion"/>) must equal the
    /// image's, or the capture is refused as outdated.
    /// <para><b>D6 (spec §5.1, §10).</b> A concept that declares no <c>attester.resource</c>, or
    /// declares a URL, is refused here: there is nothing to pin, so nothing the host or the model
    /// could hold for the attestation code that ran. The host's <see cref="IAttester"/> is never
    /// called with a <see langword="null"/> source. This narrows §10.5 for this implementation
    /// (docs/spec-conformance, S10.5-1).</para>
    /// <para>A refusal is an <see cref="AttestationOutcome"/>; only an unexpected exception
    /// (not I/O, decoding or an object over the bound) escapes.
    /// <see cref="AttestationCaptureResult.ImageOutdated"/> says whether the failure is one the
    /// image may be behind on — a resource missing, unreadable, refused, over the bound, or whose
    /// version changed — in which case a caller holding a <see cref="BundleHandle"/> invalidates
    /// it so that the next attempt starts from a fresh survey (spec §5.1); an undeclared or URL
    /// attester is not such a failure, since only a change of the concept can alter it.</para>
    /// </summary>
    /// <param name="bundle">The image the concept was loaded from.</param>
    /// <param name="concept">The attested-computation concept, from that image.</param>
    /// <param name="expectedComputationFileVersion">The version of the <c>computation</c> file the caller observed, which must equal the version the image pinned; <see langword="null"/> uses the image's version alone.</param>
    public static AttestationCaptureResult Capture(Bundle bundle, Concept concept, BundleObjectVersion? expectedComputationFileVersion = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(concept);

        // The image's own survey of this concept (spec §5.1): what the load found and pinned.
        // The concept must be the one this image holds, not a namesake from another image.
        if (!ReferenceEquals(bundle.Get(concept.Id), concept)
            || !bundle.ExecutableResources.TryGetValue(concept.Id, out var survey))
        {
            return Refused($"concept '{concept.Id}' is not part of the bundle image it was given with", imageOutdated: false);
        }

        SanctionedComputation resolved;
        var computation = concept.Document.Computation();
        switch (computation.Source)
        {
            case ComputationSource.Inline when !string.IsNullOrEmpty(computation.InlineCode):
                resolved = computation;
                break;

            case ComputationSource.File when !string.IsNullOrEmpty(computation.Path):
                // Sanctioned code is pinned to the image (spec §5.1, §5.4): only a Pinned file
                // is executable, and it is read at the version the load pinned. A version the
                // caller observed must be that same version: if the file moved on after the
                // observation, or the image is behind the observation, nothing runs.
                var file = survey.Computation;
                if (file.State != ExecutableResourceState.Pinned || file.Key is null || file.Version is null)
                {
                    return file.State switch
                    {
                        ExecutableResourceState.Unreadable or ExecutableResourceState.TooLarge
                            => Refused($"computation file '{computation.Path}' could not be read ({file.State})", imageOutdated: true),
                        // A URL (or an undeclared path) creates no reload obligation (spec §5.1): only a change of the concept alters it.
                        ExecutableResourceState.Url or ExecutableResourceState.Undeclared
                            => Refused($"computation file '{computation.Path}' could not be resolved ({file.State})", imageOutdated: false),
                        _ => Refused($"computation file '{computation.Path}' could not be resolved ({file.State})", imageOutdated: true),
                    };
                }

                if (expectedComputationFileVersion is { } observed && observed != file.Version)
                {
                    return Refused($"computation file '{computation.Path}' changed since it was shown; read it again with okf_get_computation before running it", imageOutdated: true);
                }

                string text;
                try
                {
                    text = bundle.ReadResourceText(file.Key, file.Version, out _);
                }
                catch (BundleVersionMismatchException)
                {
                    return Refused(
                        expectedComputationFileVersion is null
                            ? $"computation file '{computation.Path}' changed since the bundle was loaded"
                            : $"computation file '{computation.Path}' changed since it was shown; read it again with okf_get_computation before running it",
                        imageOutdated: true);
                }
                catch (BundleObjectTooLargeException)
                {
                    // Pinned at the load, replaced since by an object over the bound (spec §6).
                    return Refused($"computation file '{computation.Path}' could not be read ({ExecutableResourceState.TooLarge})", imageOutdated: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
                {
                    // The exception TYPE, as for the stage failures: this reason string is
                    // rendered into a model's context, and a raw I/O message can carry a host
                    // path. computation.Path is bundle-relative and safe to name.
                    return Refused($"computation file '{computation.Path}' could not be read: {e.GetType().Name}", imageOutdated: true);
                }

                resolved = new SanctionedComputation(ComputationSource.File, text, computation.Path);
                break;

            default:
                return Refused("attested computation has no computation (neither an inline `# Computation` fence nor a `computation:` path)", imageOutdated: false);
        }

        // A positive check: anything that is not a fully pinned resource refuses, so that a
        // state this code does not name can never reach an unpinned read.
        var attester = survey.Attester;
        if (attester.State != ExecutableResourceState.Pinned || attester.Key is null || attester.Version is null)
        {
            switch (attester.State)
            {
                case ExecutableResourceState.Undeclared:
                    return Refused("no attester.resource is declared, so the computation cannot be attested and is not run", imageOutdated: false);
                case ExecutableResourceState.Url:
                    return Refused($"attester resource '{attester.RawPath}' is a URL, which is never read, so the computation cannot be attested and is not run", imageOutdated: false);
                case ExecutableResourceState.Unreadable:
                case ExecutableResourceState.TooLarge:
                    return Refused($"attester resource '{attester.RawPath}' could not be read ({attester.State})", imageOutdated: true);
                default:
                    return Refused($"attester resource '{attester.RawPath}' could not be resolved ({attester.State})", imageOutdated: true);
            }
        }

        string attesterSourceText;
        try
        {
            attesterSourceText = bundle.ReadResourceText(attester.Key, attester.Version, out _);
        }
        catch (BundleVersionMismatchException)
        {
            return Refused($"attester resource '{attester.RawPath}' changed since the bundle was loaded", imageOutdated: true);
        }
        catch (BundleObjectTooLargeException)
        {
            return Refused($"attester resource '{attester.RawPath}' could not be read ({ExecutableResourceState.TooLarge})", imageOutdated: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return Refused($"attester resource '{attester.RawPath}' could not be read: {e.GetType().Name}", imageOutdated: true);
        }

        return new AttestationCaptureResult(new AttestationCapture(concept.Id, resolved, attesterSourceText), null, false);
    }

    private static AttestationCaptureResult Refused(string reason, bool imageOutdated) => new(null, Fail(reason), imageOutdated);

    /// <summary>
    /// Runs the §10.5 workflow from step 3 on what <paramref name="capture"/> holds — the
    /// computation and the attester's source, captured once by <see cref="Capture"/> — so that
    /// nothing is read from the bundle after the capture (spec §5.4). <see cref="RunAsync"/>
    /// captures and calls this; <c>okf_run_computation</c> captures itself, pinned to what its
    /// session observed, then calls this.
    /// </summary>
    /// <param name="bundle">The image the concept was loaded from.</param>
    /// <param name="conceptId">The attested-computation concept to run.</param>
    /// <param name="parameterValues">The parameter values supplied for this run (§10.3: values only, never computation code).</param>
    /// <param name="capture">What the run executes.</param>
    /// <param name="policy">The staleness gating policy for this run; defaults to the constructor's <c>defaultPolicy</c>.</param>
    /// <param name="cancellationToken">A token to cancel binding/execution/attestation.</param>
    public async ValueTask<AttestationOutcome> RunCapturedAsync(
        Bundle bundle,
        ConceptId conceptId,
        IReadOnlyDictionary<string, object?> parameterValues,
        AttestationCapture capture,
        StalePolicy? policy = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(capture);

        // Errors-as-data (see class remarks) extends to the parameters argument
        // itself: a caller passing null must get the normal "missing required
        // parameter" outcome below, never a NullReferenceException.
        parameterValues ??= new Dictionary<string, object?>();

        if (!capture.ConceptId.Equals(conceptId))
        {
            return Fail($"the capture was taken for concept '{capture.ConceptId}', not '{conceptId}', and is not run");
        }

        var concept = bundle.Get(conceptId);
        if (concept is null || !concept.Document.Frontmatter.IsAttestedComputation)
        {
            return Fail($"concept '{conceptId}' was not found or is not an attested computation");
        }

        var frontmatter = concept.Document.Frontmatter;
        var contract = frontmatter.ComputationContract;
        var resolved = capture.Computation;
        var attesterSourceText = capture.AttesterSourceText;

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

        var effectivePolicy = policy ?? _defaultPolicy;

        // Step 5: bind.
        //
        // Checked here, and again before each stage below, because handing the
        // token to a host stage is not the same as observing it. A stage whose
        // underlying client predates cancellation support — or simply forgets —
        // ignores its token, and an already-cancelled run then executed every
        // stage and could return a DISPLAYABLE success: for §10 that means a
        // computation actually ran, possibly against a live warehouse, after
        // the caller had withdrawn. RunStageAsync also stops awaiting a stage
        // the moment the token fires, and re-checks after it.
        //
        // No § for this: §10 sets no time limit and no cancellation rule at
        // all (§10.5, which numbers the steps, is explicitly informative and
        // says nothing about withdrawing a run). This is a host-side rule this
        // implementation adopts on its own, and it is only §10.5's step-6 gate
        // -- "refuse to display a failing attestation" -- that makes getting it
        // wrong visible as a displayable success.
        var (bindOk, bound, bindReason, bindError) = await RunStageAsync(
            "binder",
            ct => runtime.Binder.BindAsync(contract, resolved, parameterValues, ct),
            cancellationToken).ConfigureAwait(false);
        if (!bindOk)
        {
            var (bindStale, _) = EvaluateStaleness(frontmatter.Lifecycle, effectivePolicy);
            return Fail([bindReason!], bindStale, bindError);
        }

        // Step 6: execute.
        var (execOk, receipt, execReason, execError) = await RunStageAsync(
            "executor",
            ct => runtime.Executor.ExecuteAsync(bound, contract, ct),
            cancellationToken).ConfigureAwait(false);
        if (!execOk)
        {
            var (execStale, _) = EvaluateStaleness(frontmatter.Lifecycle, effectivePolicy);
            return Fail([execReason!], execStale, execError);
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
            // No separate ThrowIfCancellationRequested here: RunStageAsync (via
            // AttestAsync) checks at its own entry, same as bind/execute above,
            // which stopped needing one of their own the moment they moved onto
            // that helper.
            var context = new AttestationContext(contract, resolved, bound, parameterValues, receipt, attesterSourceText);
            (verdict, error) = await AttestAsync(runtime, context, reasons, cancellationToken).ConfigureAwait(false);
        }

        // Step 9/10: gate on staleness and aggregate the outcome. The clock is read here,
        // immediately before gating -- not once up front -- so a run whose stages took long
        // enough to cross stale_after is judged at release time, not at run start (§5.5).
        var (stale, staleAdmitted) = EvaluateStaleness(frontmatter.Lifecycle, effectivePolicy);
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
    /// including the exact reason wording.
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
        var (ok, verdict, reason, error) = await RunStageAsync(
            "attester",
            ct => runtime.Attester.AttestAsync(context, ct),
            cancellationToken).ConfigureAwait(false);
        if (!ok)
        {
            reasons.Add(reason!);
            return (null, error);
        }

        if (verdict is { Passed: false } failed)
        {
            reasons.Add(string.IsNullOrEmpty(failed.Detail) ? "attestation did not pass" : $"attestation did not pass: {failed.Detail}");
        }

        return (verdict, null);
    }

    /// <summary>
    /// Runs one host-plugged stage under the single cancellation and
    /// reporting policy: a caller cancellation (direct or wrapped) propagates
    /// as an <see cref="OperationCanceledException"/> tied to the caller's
    /// token; any other exception becomes a reason — the message included
    /// only for an <see cref="AttestationDiagnosticException"/>, the type
    /// alone for everything else (see that type's remarks for why).
    ///
    /// Cancellation is control flow, not data: errors-as-data is the contract
    /// for FAILURES, and a cancellation the CALLER asked for is not one. A
    /// bare `catch (Exception)` would turn it into a business outcome, so a
    /// caller that cancelled got a normal-looking result and could not tell
    /// "the stage failed" from "I asked it to stop".
    ///
    /// But the exception TYPE alone does not identify that: HttpClient raises
    /// TaskCanceledException on its own request timeout with nobody's token
    /// cancelled, and a host executor calling one is the ordinary case —
    /// that is a stage failure like any other. The token's state is what
    /// actually distinguishes the two.
    ///
    /// Caller cancellation arriving WRAPPED is still cancellation, but it has
    /// to reach the caller in the shape they catch. An unwrapped
    /// OperationCanceledException falls through both clauses uncaught — since
    /// stages are awaited through <see cref="AwaitStageAsync{T}"/>, usually
    /// <see cref="Task.WaitAsync(CancellationToken)"/>'s own
    /// <see cref="TaskCanceledException"/> tied to the caller's token rather
    /// than the stage's exception.
    ///
    /// <para><b>The token is enforced around the stage, not only before it.</b>
    /// A host-side rule, carrying no § of its own — §10 sets no time limit and
    /// no cancellation rule, and §10.5 is informative. Checking at entry alone
    /// let a stage that ignores its token
    /// run to completion and have its result used: a 30 ms
    /// <c>ComputationTimeout</c> waited out a 350 ms attester and came back
    /// <c>displayable: yes</c>, and a stage that cancelled the token and then
    /// returned success yielded an outcome that was both cancelled and
    /// displayable. So, when the token can be cancelled, each stage is started
    /// on the thread pool and awaited through <see cref="AwaitStageAsync{T}"/>,
    /// which stops waiting the moment the token fires and checks the token
    /// again after the stage succeeds.</para>
    ///
    /// <para><b>Why the thread-pool hop.</b> Awaiting the stage's returned
    /// <see cref="ValueTask{TResult}"/> is not enough: a stage that blocks its
    /// thread BEFORE returning — a synchronous client wrapped in
    /// <c>ValueTask.FromResult</c> — has handed back nothing to stop waiting on
    /// until it is done, so invoked on the caller's thread it held the run for
    /// its full duration whatever the token did. The cost is one thread-pool
    /// hop per stage, and a blocking stage that is abandoned keeps its pool
    /// thread until it returns. The caller's token is passed to the hop too, so
    /// a token that fires after the entry check but before the pool picks the
    /// work up means the stage is never invoked. A non-cancellation exception
    /// the delegate throws synchronously faults the pool task and is rethrown
    /// unchanged, so — the token not having fired — it is still a failure
    /// reason. A token that can never be
    /// cancelled invokes the stage directly: there is nothing to abandon it
    /// for.</para>
    ///
    /// <para><b>The resulting routing, precisely.</b> A cancelled run never
    /// yields a displayable outcome. Once the orchestrator has seen the token
    /// fire while a stage is running, or after it succeeded, the run ends as a
    /// cancellation — an <see cref="OperationCanceledException"/> to the
    /// caller, which <c>okf_run_computation</c> renders as
    /// <c>displayable: no … timed out</c> when it was its own
    /// <c>ComputationTimeout</c> that fired — whatever the stage does
    /// afterwards, a later failure included. Only a stage failure (a
    /// non-cancellation exception) that had already completed before the
    /// orchestrator saw the token is reported as that failure, on a
    /// non-displayable outcome. A
    /// token that fires only after the attester's post-stage check is too late
    /// to withdraw the outcome: cancellation arriving after completion is not
    /// cancellation of the run.</para>
    ///
    /// <para><b>Abandoning a stage does not stop its work.</b> Nothing can
    /// force a host's code to return; the orchestrator only stops waiting for
    /// it, and whatever that stage started keeps running until the stage ends.
    /// A stage that honours its token ends promptly on its own: for
    /// <c>OKF4net.Attestation.Containers</c>, the engine honours the token and
    /// kills its container, bounded by its kill timeout — what the orchestrator
    /// no longer waits for is that teardown, and the engine's per-run
    /// <c>Timeout</c> is the backstop. The abandoned task is observed, so a
    /// fault it raises later cannot surface as an unobserved task
    /// exception.</para>
    /// </summary>
    /// <param name="stage">The stage's name, as it appears in a reason string ("binder threw: ...").</param>
    /// <param name="run">The host-plugged stage to invoke.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    private static async ValueTask<(bool Ok, T Result, string? Reason, Exception? Error)> RunStageAsync<T>(
        string stage,
        Func<CancellationToken, ValueTask<T>> run,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // See the remarks: the hop (with the caller's token) is what lets a
            // thread-blocking stage be abandoned; a non-cancellable token has
            // nothing to abandon, and is invoked directly.
            var result = cancellationToken.CanBeCanceled
                ? await AwaitStageAsync(Task.Run(() => run(cancellationToken).AsTask(), cancellationToken), cancellationToken).ConfigureAwait(false)
                : await run(cancellationToken).ConfigureAwait(false);
            return (true, result, null, null);
        }
        catch (AggregateException e) when (IsCallerCancellation(e, cancellationToken))
        {
            throw new OperationCanceledException(CancelledMessage, e, cancellationToken);
        }
        catch (Exception e) when (!IsCallerCancellation(e, cancellationToken))
        {
            var reason = e is AttestationDiagnosticException
                ? $"{stage} threw: {e.GetType().Name}: {e.Message.ReplaceLineEndings(" ")}"
                : $"{stage} threw: {e.GetType().Name}";
            // default! is never read: Ok is false here, and every caller checks
            // it before touching Result.
            return (false, default!, reason, e);
        }
    }

    /// <summary>
    /// Awaits a started <paramref name="stage"/>, but stops waiting — with an
    /// <see cref="OperationCanceledException"/> — the moment
    /// <paramref name="cancellationToken"/> fires, whether or not the stage
    /// observes the token itself; then checks the token once more, so a stage
    /// that completed successfully after cancellation never contributes its
    /// result — a host-side rule with no § behind it, see
    /// <see cref="RunStageAsync{T}"/>'s remarks.
    ///
    /// <para>Both steps are needed. <see cref="Task.WaitAsync(CancellationToken)"/>
    /// alone returns a stage that is already complete even when the token is
    /// already cancelled — it tests completion first — which is exactly the
    /// interleaving where a stage cancels the token, returns success, and
    /// finishes on the pool before this method is reached. A race reaches that
    /// only occasionally from <see cref="RunAsync"/>, so this method is
    /// <c>internal</c> for a deterministic test of the second step.</para>
    ///
    /// <para>An abandoned stage is observed, so a fault it raises after nobody
    /// is awaiting it cannot surface as an unobserved task exception. A stage
    /// that fails (a non-cancellation exception) before the token is seen is
    /// rethrown unchanged for <see cref="RunStageAsync{T}"/> to report.</para>
    /// </summary>
    /// <typeparam name="T">The stage's result type.</typeparam>
    /// <param name="stage">The started stage.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The stage's result, when the token has not fired.</returns>
    internal static async ValueTask<T> AwaitStageAsync<T>(Task<T> stage, CancellationToken cancellationToken)
    {
        T result;
        try
        {
            result = await stage.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The stage may still be running, and may fault after nobody is
            // awaiting it. Observe that fault here, or it surfaces later as an
            // unobserved task exception on a finalizer thread. Harmless when
            // the stage itself already completed (e.g. by honouring the token):
            // OnlyOnFaulted simply never runs.
            _ = stage.ContinueWith(
                static abandoned => _ = abandoned.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }

        // A stage that finished — or cancelled the token itself — after
        // cancellation arrived must not contribute its result. The OCE this
        // throws is the caller's, so it falls through both of RunStageAsync's
        // catch clauses.
        cancellationToken.ThrowIfCancellationRequested();
        return result;
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

    /// <summary>
    /// Reads <see cref="_clock"/> once and reports both the concept's <see cref="StaleState"/>
    /// and whether <paramref name="policy"/> admits it — a single instant feeding both, per
    /// §5.5 ("content is stale when now &gt;= stale_after"). Called at the point an outcome is
    /// actually built (immediately before the success gate, or inside a post-stage <c>Fail</c>),
    /// never once up front: a run whose stages take long enough to cross <c>stale_after</c>
    /// must be judged at release time, not at the instant it started.
    /// </summary>
    /// <param name="lifecycle">The concept's §5 lifecycle fields.</param>
    /// <param name="policy">The gating policy this run is evaluated under.</param>
    private (StaleState Stale, bool Admitted) EvaluateStaleness(Lifecycle lifecycle, StalePolicy policy)
    {
        var now = _clock.Now;
        return (ComputeStale(lifecycle, now), policy.Admits(lifecycle, now));
    }

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
