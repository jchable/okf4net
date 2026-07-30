// SPDX-License-Identifier: LGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OKF4net.Attestation;

namespace OKF4net.Tests.Attestation;

/// <summary>
/// A configurable in-memory <see cref="IAttestationRuntime"/> for tests.
/// Each stage (bind/execute/attest) delegates to a settable <see cref="Func{T,TResult}"/>-shaped
/// field with a happy-path default, so a test can override just the stage it cares about.
/// Reused by both the registry tests here and the orchestrator tests (Task 6).
/// </summary>
public sealed class FakeRuntime : IAttestationRuntime
{
    /// <summary>Delegate backing <see cref="Binder"/>. Defaults to echoing <paramref name="values"/> back as a <see cref="BoundComputation"/>.</summary>
    public Func<AttestedComputationContract, SanctionedComputation, IReadOnlyDictionary<string, object?>, CancellationToken, ValueTask<BoundComputation>> BindFunc { get; set; }

    /// <summary>Delegate backing <see cref="Executor"/>. Defaults to an empty <see cref="Receipt"/>.</summary>
    public Func<BoundComputation, AttestedComputationContract, CancellationToken, ValueTask<Receipt>> ExecuteFunc { get; set; }

    /// <summary>Delegate backing <see cref="Attester"/>. Defaults to a passing <see cref="AttestationVerdict"/>.</summary>
    public Func<AttestationContext, CancellationToken, ValueTask<AttestationVerdict>> AttestFunc { get; set; }

    /// <inheritdoc />
    public IParameterBinder Binder { get; }

    /// <inheritdoc />
    public IComputationExecutor Executor { get; }

    /// <inheritdoc />
    public IAttester Attester { get; }

    /// <summary>Creates a runtime with happy-path defaults for every stage.</summary>
    public FakeRuntime()
    {
        BindFunc = (contract, computation, values, _) =>
            ValueTask.FromResult(new BoundComputation(contract.Runtime ?? "fake", computation.InlineCode, null, values));
        ExecuteFunc = (_, _, _) =>
            ValueTask.FromResult(new Receipt(new Dictionary<string, object?>()));
        AttestFunc = (_, _) =>
            ValueTask.FromResult(new AttestationVerdict(true, null));

        Binder = new DelegatingBinder(this);
        Executor = new DelegatingExecutor(this);
        Attester = new DelegatingAttester(this);
    }

    /// <summary>A runtime that binds/executes normally and always attests as passing, optionally with a fixed <paramref name="receipt"/> and/or <paramref name="verdict"/>.</summary>
    public static FakeRuntime Passing(Receipt? receipt = null, AttestationVerdict? verdict = null)
    {
        var runtime = new FakeRuntime();
        if (receipt is not null)
        {
            runtime.ExecuteFunc = (_, _, _) => ValueTask.FromResult(receipt);
        }

        if (verdict is not null)
        {
            var v = verdict.Value;
            runtime.AttestFunc = (_, _) => ValueTask.FromResult(v);
        }

        return runtime;
    }

    /// <summary>A runtime whose executor throws (to exercise orchestrator error-capture), everything else happy-path.</summary>
    public static FakeRuntime ThrowingExecutor(Exception? exception = null)
    {
        var runtime = new FakeRuntime();
        runtime.ExecuteFunc = (_, _, _) => throw exception ?? new InvalidOperationException("fake executor failure");
        return runtime;
    }

    private sealed class DelegatingBinder(FakeRuntime owner) : IParameterBinder
    {
        public ValueTask<BoundComputation> BindAsync(
            AttestedComputationContract contract,
            SanctionedComputation computation,
            IReadOnlyDictionary<string, object?> values,
            CancellationToken cancellationToken = default)
            => owner.BindFunc(contract, computation, values, cancellationToken);
    }

    private sealed class DelegatingExecutor(FakeRuntime owner) : IComputationExecutor
    {
        public ValueTask<Receipt> ExecuteAsync(
            BoundComputation bound,
            AttestedComputationContract contract,
            CancellationToken cancellationToken = default)
            => owner.ExecuteFunc(bound, contract, cancellationToken);
    }

    private sealed class DelegatingAttester(FakeRuntime owner) : IAttester
    {
        public ValueTask<AttestationVerdict> AttestAsync(
            AttestationContext context,
            CancellationToken cancellationToken = default)
            => owner.AttestFunc(context, cancellationToken);
    }
}
