// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>The provider's operation permits (spec D16): one per operation, from the call to the end of the body and of the cleanup.</summary>
internal sealed class OperationGate(int maxConcurrent, TimeProvider time)
{
    private readonly SemaphoreSlim _permits = new(maxConcurrent, maxConcurrent);

    /// <summary>Waits for a permit under the operation's deadline, which starts now (spec §4.5).</summary>
    public async ValueTask<OperationScope> EnterAsync(S3Operation operation, TimeSpan timeout, CancellationToken caller, BundleKey? key)
    {
        var deadline = new CancellationTokenSource(timeout, time);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, deadline.Token);
        try
        {
            await _permits.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            linked.Dispose();
            deadline.Dispose();
            throw new S3RequestException(operation, S3FailureKind.Timeout, null, key);
        }
        catch
        {
            linked.Dispose();
            deadline.Dispose();
            throw;
        }

        return new OperationScope(_permits, operation, key, caller, deadline, linked);
    }
}

/// <summary>One provider operation: its token (caller + deadline), its permit, and the failure priority of spec §4.5.</summary>
internal sealed class OperationScope : IAsyncDisposable
{
    private readonly SemaphoreSlim _permits;
    private readonly CancellationToken _caller;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private Task? _holdUntil;
    private Exception? _principal;
    private int _disposed;

    internal OperationScope(SemaphoreSlim permits, S3Operation operation, BundleKey? key, CancellationToken caller, CancellationTokenSource deadline, CancellationTokenSource linked)
    {
        _permits = permits;
        Operation = operation;
        Key = key;
        _caller = caller;
        _deadline = deadline;
        _linked = linked;
    }

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

    /// <summary>
    /// The exception this operation ends with, given a failure just observed (spec §4.5). The FIRST call decides and
    /// the result is kept for good: every later call returns it, whatever it observes. At that first call: the
    /// caller's cancellation if it was already requested; else the observed failure if it is already a contract
    /// exception (a bound, an absent or refused object, a version mismatch, an S3RequestException); else, if the
    /// deadline has fired, a Timeout — whatever the interrupted read threw (an OperationCanceledException, or an
    /// ObjectDisposedException/IOException because the response was disposed to unblock it); else the closed table
    /// (ErrorClassifier). Never throws. Safe to call from several threads: one answer is kept, atomically.
    /// </summary>
    public Exception Fail(Exception observed)
    {
        if (Volatile.Read(ref _principal) is { } decided)
        {
            return decided;
        }

        var candidate = _caller.IsCancellationRequested
            ? observed as OperationCanceledException ?? new OperationCanceledException(_caller)
            : observed switch
            {
                BundleObjectTooLargeException or BundleLimitExceededException or BundleObjectNotFoundException
                    or BundleVersionMismatchException or BundleAccessRefusedException or S3RequestException => observed,
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
