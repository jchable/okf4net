// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// The provider's operation permits (spec D16): one per operation, from the call to the end of the body and of the
/// cleanup. The constructor's <c>disposal</c> token is the provider's own disposal: it is linked into every operation's token, so a
/// provider disposed in the middle of an operation interrupts it, and the operation fails as
/// <see cref="S3FailureKind.Disposed"/> (spec §4.2), never as the transport failure the disposal caused.
/// </summary>
internal sealed class OperationGate
{
    private readonly SemaphoreSlim _permits;
    private readonly TimeProvider _time;
    private readonly CancellationToken _disposal;

    public OperationGate(int maxConcurrent, TimeProvider time, CancellationToken disposal = default)
        : this(new SemaphoreSlim(maxConcurrent, maxConcurrent), time, disposal)
    {
    }

    /// <summary>Tests only: the permits are given, so a test can make the wait itself fail in a way no cancellation explains (a disposed semaphore).</summary>
    internal OperationGate(SemaphoreSlim permits, TimeProvider time, CancellationToken disposal = default)
    {
        _permits = permits;
        _time = time;
        _disposal = disposal;
    }

    /// <summary>Waits for a permit under the operation's deadline, which starts now (spec §4.5).</summary>
    public async ValueTask<OperationScope> EnterAsync(S3Operation operation, TimeSpan timeout, CancellationToken caller, BundleKey? key)
    {
        var deadline = new CancellationTokenSource(timeout, _time);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, deadline.Token, _disposal);
        try
        {
            await _permits.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Decided BEFORE the sources are disposed: the caller's cancellation is sampled first (spec §4.5, rule 1).
            // Every exit is classified: anything but a cancellation (none is expected from a semaphore that is never
            // disposed) is a Transport failure carrying the original, stack intact, as its inner exception.
            Exception failure = caller.IsCancellationRequested ? OperationScope.CallerCancellation(e, caller)
                : _disposal.IsCancellationRequested ? new S3RequestException(operation, S3FailureKind.Disposed, null, key, e)
                : e is OperationCanceledException ? new S3RequestException(operation, S3FailureKind.Timeout, null, key)
                : new S3RequestException(operation, S3FailureKind.Transport, null, key, e);
            linked.Dispose();
            deadline.Dispose();
            throw failure;
        }

        return new OperationScope(_permits, operation, key, caller, _disposal, deadline, linked);
    }
}

/// <summary>One provider operation: its token (caller + deadline), its permit, and the failure priority of spec §4.5.</summary>
internal sealed class OperationScope : IAsyncDisposable
{
    /// <summary>The message of the cancellation an operation ends with when its caller cancelled: fixed, never a server's or an SDK's text.</summary>
    internal const string CancelledMessage = "The S3 operation was canceled by its caller.";

    private readonly SemaphoreSlim _permits;
    private readonly CancellationToken _caller;
    private readonly CancellationToken _disposal;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private Task? _holdUntil;
    private Exception? _principal;
    private int _disposed;

    internal OperationScope(SemaphoreSlim permits, S3Operation operation, BundleKey? key, CancellationToken caller, CancellationToken disposal, CancellationTokenSource deadline, CancellationTokenSource linked)
    {
        _permits = permits;
        Operation = operation;
        Key = key;
        _caller = caller;
        _disposal = disposal;
        _deadline = deadline;
        _linked = linked;
    }

    /// <summary>
    /// The caller's cancellation, carrying the caller's OWN token (spec §4.5, rule 1), so a host's
    /// <c>catch (OperationCanceledException e) when (e.CancellationToken == ct)</c> recognizes it whatever token the
    /// interrupted await observed (the operation's linked token, the SDK's, HttpClient's): <paramref name="observed"/>
    /// when it already carries it, else a new cancellation with <paramref name="observed"/> as its inner exception.
    /// </summary>
    internal static OperationCanceledException CallerCancellation(Exception observed, CancellationToken caller) =>
        observed is OperationCanceledException oce && oce.CancellationToken == caller
            ? oce
            : new OperationCanceledException(CancelledMessage, observed, caller);

    public S3Operation Operation { get; }

    public BundleKey? Key { get; }

    /// <summary>The caller's token and the deadline together.</summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>
    /// Keeps the permit until <paramref name="abandoned"/> ends (an isolated credentials call that outlived its
    /// deadline), however it ends. Called within the operation, before <see cref="DisposeAsync"/>; a second call
    /// holds the permit until both have ended.
    /// </summary>
    public void HoldPermitUntil(Task abandoned) =>
        _holdUntil = _holdUntil is null ? abandoned : Task.WhenAll(_holdUntil, abandoned);

    /// <summary>Whether the caller's own token asked to stop (not the deadline).</summary>
    public bool CallerCancelled => _caller.IsCancellationRequested;

    /// <summary>The caller's own token, for the cancellation an operation ends with (<see cref="CallerCancellation"/>).</summary>
    public CancellationToken CallerToken => _caller;

    /// <summary>Whether the provider was disposed (its disposal token is linked into <see cref="Token"/>).</summary>
    public bool ProviderDisposed => _disposal.IsCancellationRequested;

    /// <summary>
    /// The exception this operation ends with, given a failure just observed (spec §4.5). The FIRST call decides and
    /// the result is kept for good: every later call returns it, whatever it observes. At that first call: the
    /// caller's cancellation if it was already requested (carrying the caller's own token, <see cref="CallerCancellation"/>);
    /// else the observed failure if it is already a contract exception (a bound, an absent or refused object, a
    /// version mismatch, an S3RequestException); else, if the provider was disposed, <see cref="S3FailureKind.Disposed"/>
    /// — whatever the disposal made the interrupted call throw; else, if the deadline has fired, a Timeout — whatever
    /// the interrupted read threw (an OperationCanceledException, or an ObjectDisposedException/IOException because
    /// the response was disposed to unblock it); else the closed table (ErrorClassifier). Never throws. Safe to call
    /// from several threads: one answer is kept, atomically.
    /// </summary>
    public Exception Fail(Exception observed)
    {
        if (Volatile.Read(ref _principal) is { } decided)
        {
            return decided;
        }

        var candidate = _caller.IsCancellationRequested
            ? CallerCancellation(observed, _caller)
            : observed switch
            {
                BundleObjectTooLargeException or BundleLimitExceededException or BundleObjectNotFoundException
                    or BundleVersionMismatchException or BundleAccessRefusedException or S3RequestException => observed,
                _ when _disposal.IsCancellationRequested => new S3RequestException(Operation, S3FailureKind.Disposed, null, Key, observed),
                _ when _deadline.IsCancellationRequested => new S3RequestException(Operation, S3FailureKind.Timeout, null, Key, observed),
                OperationCanceledException => new S3RequestException(Operation, S3FailureKind.Timeout, null, Key, observed),
                GuardException g => new S3RequestException(Operation, g.Kind, null, Key, g),
                _ => ErrorClassifier.Classify(Operation, Key, observed),
            };
        return Interlocked.CompareExchange(ref _principal, candidate, null) ?? candidate;
    }

    /// <summary>
    /// Ends the operation: returns its permit now, or when the task given to <see cref="HoldPermitUntil"/> ends.
    /// Idempotent — a second call returns nothing, so a permit is never returned twice.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _linked.Dispose();
        _deadline.Dispose();
        if (_holdUntil is { IsCompleted: false } pending)
        {
            // Observes the abandoned call's fault (its result was already dropped) and returns the permit; never throws.
            _ = pending.ContinueWith(
                static (t, s) =>
                {
                    _ = t.Exception;
                    ((SemaphoreSlim)s!).Release();
                },
                _permits,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            _ = _holdUntil?.Exception;
            _permits.Release();
        }

        return ValueTask.CompletedTask;
    }
}
