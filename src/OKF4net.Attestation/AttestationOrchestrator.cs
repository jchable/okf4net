// SPDX-License-Identifier: LGPL-3.0-or-later
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
    private readonly IAttestationRuntimeRegistry _runtimes;
    private readonly IOkfClock _clock;
    private readonly StalePolicy _defaultPolicy;

    /// <summary>
    /// Creates an orchestrator over <paramref name="runtimes"/>.
    /// </summary>
    /// <param name="runtimes">Resolves an <see cref="IAttestationRuntime"/> by the concept's <c>runtime</c> field.</param>
    /// <param name="clock">Supplies today's date for staleness gating. Defaults to <see cref="SystemClock"/>.</param>
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
        var computation = concept.Document.Computation();
        SanctionedComputation resolved;
        switch (computation.Source)
        {
            case ComputationSource.Inline when !string.IsNullOrEmpty(computation.InlineCode):
                resolved = computation;
                break;

            case ComputationSource.File when !string.IsNullOrEmpty(computation.Path):
                if (!bundle.TryResolveResource(concept, computation.Path, out var absolutePath, out var status)
                    || status != ResourceResolutionStatus.Resolved)
                {
                    return Fail($"computation file '{computation.Path}' could not be resolved ({status})");
                }

                string text;
                try
                {
                    text = bundle.ReadResourceText(absolutePath!);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
                {
                    return Fail($"computation file '{computation.Path}' could not be read: {e.Message}");
                }

                resolved = new SanctionedComputation(ComputationSource.File, text, computation.Path);
                break;

            default:
                return Fail("attested computation has no computation (neither an inline `# Computation` fence nor a `computation:` path)");
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
        BoundComputation bound;
        try
        {
            bound = await runtime.Binder.BindAsync(contract, resolved, parameterValues, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return Fail([$"binder threw: {e.Message}"], stale, e);
        }

        // Step 6: execute.
        Receipt receipt;
        try
        {
            receipt = await runtime.Executor.ExecuteAsync(bound, contract, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return Fail([$"executor threw: {e.Message}"], stale, e);
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
            try
            {
                var context = new AttestationContext(contract, resolved, bound, parameterValues, receipt);
                verdict = await runtime.Attester.AttestAsync(context, cancellationToken).ConfigureAwait(false);
                if (verdict is { Passed: false } failed)
                {
                    reasons.Add(string.IsNullOrEmpty(failed.Detail) ? "attestation did not pass" : $"attestation did not pass: {failed.Detail}");
                }
            }
            catch (Exception e)
            {
                error = e;
                reasons.Add($"attester threw: {e.Message}");
            }
        }

        // Step 9/10: gate on staleness and aggregate the outcome.
        if (!staleAdmitted)
        {
            reasons.Add("concept is stale and the gating policy does not admit it");
        }

        var displayable = receiptShapeOk && verdict is { Passed: true } && staleAdmitted;
        return new AttestationOutcome(displayable, verdict, receipt, receiptShapeOk, stale, reasons, error);
    }

    private static AttestationOutcome Fail(string reason, StaleState stale = StaleState.Unknown, Exception? error = null)
        => Fail([reason], stale, error);

    private static AttestationOutcome Fail(IReadOnlyList<string> reasons, StaleState stale = StaleState.Unknown, Exception? error = null)
        => new(false, null, null, false, stale, reasons, error);

    private static StaleState ComputeStale(Lifecycle lifecycle, DateTimeOffset now)
    {
        // A stale_after that is absent *or unparseable* is Unknown, never
        // Fresh: IsStale alone returns false for both, and reporting a
        // malformed stamp as Fresh would let §10.6's gate pass on data it
        // could not read.
        if (lifecycle.StaleAfter is null)
        {
            return StaleState.Unknown;
        }

        return lifecycle.IsStale(now) ? StaleState.Stale : StaleState.Fresh;
    }
}
