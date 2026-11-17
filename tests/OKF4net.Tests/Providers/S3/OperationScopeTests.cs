// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>
/// Spec D16, §4.5. Every timing here is driven by a <see cref="ManualClock"/> or a gate the test opens: nothing sleeps
/// to synchronize. The only real-time values are hang guards (<see cref="Within{T}"/>), which a correct implementation
/// never reaches.
/// </summary>
public class OperationScopeTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Never_more_than_N_operations_hold_a_permit()
    {
        var gate = new OperationGate(3, new ManualClock());
        var inFlight = 0;
        var peak = 0;
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new[] { S3Operation.List, S3Operation.Read, S3Operation.Exists };
        var tasks = Enumerable.Range(0, 10).Select(async i =>
        {
            var scope = await gate.EnterAsync(operations[i % 3], Long, default, null);
            await using (scope)
            {
                Interlocked.Increment(ref entered);
                InterlockedMax(ref peak, Interlocked.Increment(ref inFlight));
                await release.Task;
                Interlocked.Decrement(ref inFlight);
            }
        }).ToArray();

        // A free permit is granted synchronously, so the first three are in and the seven others are queued.
        Assert.Equal(3, Volatile.Read(ref entered));
        Assert.All(tasks, t => Assert.False(t.IsCompleted));

        release.SetResult();
        await Within(Task.WhenAll(tasks));
        Assert.Equal(10, Volatile.Read(ref entered));
        Assert.Equal(3, Volatile.Read(ref peak));
    }

    [Fact]
    public async Task Waiting_for_a_permit_counts_in_the_deadline_and_is_a_Timeout()
    {
        var clock = new ManualClock();
        var gate = new OperationGate(1, clock);
        await using var held = await gate.EnterAsync(S3Operation.Read, Long, default, null);
        var waiting = gate.EnterAsync(S3Operation.List, TimeSpan.FromMilliseconds(100), default, null).AsTask();

        clock.Advance(TimeSpan.FromMilliseconds(99));
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        var e = await Assert.ThrowsAsync<S3RequestException>(() => Within(waiting));
        Assert.Equal(S3FailureKind.Timeout, e.Kind);
        Assert.Equal(S3Operation.List, e.Operation);
        Assert.Equal("S3 list failed: Timeout", e.Message);
    }

    [Fact]
    public async Task The_deadline_starts_at_the_call_and_covers_the_wait_for_the_permit()
    {
        var clock = new ManualClock();
        var gate = new OperationGate(1, clock);
        var held = await gate.EnterAsync(S3Operation.Read, Long, default, null);
        var waiting = gate.EnterAsync(S3Operation.Read, TimeSpan.FromMilliseconds(100), default, null).AsTask();

        clock.Advance(TimeSpan.FromMilliseconds(60));
        await held.DisposeAsync();
        await using var scope = await Within(waiting);

        clock.Advance(TimeSpan.FromMilliseconds(39));
        Assert.False(scope.Token.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(scope.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task The_token_carries_the_callers_cancellation()
    {
        var gate = new OperationGate(1, new ManualClock());
        using var caller = new CancellationTokenSource();
        await using var scope = await gate.EnterAsync(S3Operation.Read, Long, caller.Token, null);
        Assert.False(scope.Token.IsCancellationRequested);
        await caller.CancelAsync();
        Assert.True(scope.Token.IsCancellationRequested);
        Assert.True(scope.CallerCancelled);
    }

    [Fact]
    public async Task A_cancelled_waiter_leaves_the_queue_as_a_cancellation()
    {
        var gate = new OperationGate(1, new ManualClock());
        var held = await gate.EnterAsync(S3Operation.Read, Long, default, null);
        using var cts = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(S3Operation.Read, Long, cts.Token, null).AsTask();
        var behind = gate.EnterAsync(S3Operation.Read, Long, default, null).AsTask();
        Assert.False(cancelled.IsCompleted);

        await cts.CancelAsync();
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(cancelled));
        Assert.IsNotType<S3RequestException>(e);

        // Its place in the queue is gone: the permit goes to the waiter behind it, and comes back whole after.
        Assert.False(behind.IsCompleted);
        await held.DisposeAsync();
        await (await Within(behind)).DisposeAsync();
        var again = gate.EnterAsync(S3Operation.Read, Long, default, null);
        Assert.True(again.IsCompletedSuccessfully);
        await (await again).DisposeAsync();
    }

    [Fact]
    public async Task An_already_cancelled_caller_is_a_cancellation_even_with_a_free_permit()
    {
        var gate = new OperationGate(1, new ManualClock());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gate.EnterAsync(S3Operation.Read, Long, cts.Token, null));
        await (await gate.EnterAsync(S3Operation.Read, Long, default, null)).DisposeAsync();
    }

    [Fact]
    public async Task A_permit_held_for_an_abandoned_task_returns_only_when_it_ends()
    {
        var clock = new ManualClock();
        var gate = new OperationGate(1, clock);
        var abandoned = new TaskCompletionSource();
        await using (var scope = await gate.EnterAsync(S3Operation.Read, Long, default, null))
        {
            scope.HoldPermitUntil(abandoned.Task);
        }

        var timedOut = gate.EnterAsync(S3Operation.Read, TimeSpan.FromMilliseconds(100), default, null).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(S3FailureKind.Timeout, (await Assert.ThrowsAsync<S3RequestException>(() => Within(timedOut))).Kind);

        var next = gate.EnterAsync(S3Operation.Read, Long, default, null).AsTask();
        Assert.False(next.IsCompleted);
        abandoned.SetResult();
        await (await Within(next)).DisposeAsync();
    }

    [Fact]
    public async Task A_permit_held_for_an_abandoned_task_returns_when_it_faults()
    {
        var gate = new OperationGate(1, new ManualClock());
        var abandoned = new TaskCompletionSource();
        await using (var scope = await gate.EnterAsync(S3Operation.Read, Long, default, null))
        {
            scope.HoldPermitUntil(abandoned.Task);
        }

        var next = gate.EnterAsync(S3Operation.Read, Long, default, null).AsTask();
        Assert.False(next.IsCompleted);
        abandoned.SetException(new InvalidOperationException("host failure"));
        await (await Within(next)).DisposeAsync();
    }

    [Fact]
    public async Task Disposing_a_scope_twice_returns_one_permit()
    {
        var gate = new OperationGate(2, new ManualClock());
        await using var other = await gate.EnterAsync(S3Operation.Read, Long, default, null);
        var scope = await gate.EnterAsync(S3Operation.Read, Long, default, null);
        await scope.DisposeAsync();
        await scope.DisposeAsync();

        await using var second = await gate.EnterAsync(S3Operation.Read, Long, default, null);
        var third = gate.EnterAsync(S3Operation.Read, Long, default, null).AsTask();
        Assert.False(third.IsCompleted); // two permits, both held
        await other.DisposeAsync();
        await (await Within(third)).DisposeAsync();
    }

    public static TheoryData<string> PriorityCases => new()
    {
        "cancel-then-bound", "bound-then-cancel", "foreign-oce", "integrity-alone",
        "deadline-then-oce", "deadline-then-disposed", "bound-after-deadline", "contract-kept",
        "cleanup-does-not-mask", "cancel-then-io",
    };

    [Theory]
    [MemberData(nameof(PriorityCases))]
    public async Task The_first_principal_failure_wins_by_the_rule_of_4_5(string scenario)
    {
        var clock = new ManualClock();
        var gate = new OperationGate(4, clock);
        var key = BundleKey.Parse("a.md");
        using var caller = new CancellationTokenSource();
        await using var scope = await gate.EnterAsync(S3Operation.Read, Long, caller.Token, key);
        Exception result;
        switch (scenario)
        {
            case "cancel-then-bound":
                await caller.CancelAsync();
                result = scope.Fail(new BundleObjectTooLargeException(key, 10, 5));
                Assert.IsAssignableFrom<OperationCanceledException>(result);
                break;
            case "bound-then-cancel":
                var bound = new BundleObjectTooLargeException(key, 10, 5);
                Assert.Same(bound, scope.Fail(bound));
                await caller.CancelAsync();
                Assert.Same(bound, scope.Fail(new OperationCanceledException(caller.Token)));
                break;
            case "foreign-oce":
                result = scope.Fail(new OperationCanceledException()); // neither the caller's token nor the deadline
                Assert.Equal(S3FailureKind.Timeout, Assert.IsType<S3RequestException>(result).Kind);
                break;
            case "deadline-then-oce":
                clock.Advance(Long);
                var oce = new OperationCanceledException(scope.Token);
                var timeout = Assert.IsType<S3RequestException>(scope.Fail(oce));
                Assert.Equal(S3FailureKind.Timeout, timeout.Kind);
                Assert.Same(oce, timeout.InnerException);
                Assert.Equal("S3 read failed: Timeout for 'a.md'", timeout.Message);
                break;
            case "deadline-then-disposed":
                // The response was disposed to unblock a read the deadline interrupted: still the deadline.
                clock.Advance(Long);
                Assert.Equal(S3FailureKind.Timeout, Assert.IsType<S3RequestException>(scope.Fail(new ObjectDisposedException("stream"))).Kind);
                await caller.CancelAsync();
                Assert.Equal(S3FailureKind.Timeout, Assert.IsType<S3RequestException>(scope.Fail(new OperationCanceledException(caller.Token))).Kind);
                break;
            case "bound-after-deadline":
                clock.Advance(Long);
                var late = new BundleLimitExceededException(BundleBound.Entries, 3);
                Assert.Same(late, scope.Fail(late));
                break;
            case "contract-kept":
                var missing = new BundleObjectNotFoundException(key);
                Assert.Same(missing, scope.Fail(missing));
                break;
            case "cleanup-does-not-mask":
                var integrity = Assert.IsType<S3RequestException>(scope.Fail(new GuardException(S3FailureKind.Integrity)));
                Assert.Same(integrity, scope.Fail(new IOException("disposal failed")));
                Assert.Equal(S3FailureKind.Integrity, integrity.Kind);
                break;
            case "cancel-then-io":
                await caller.CancelAsync();
                var cancelled = Assert.IsAssignableFrom<OperationCanceledException>(scope.Fail(new IOException("connection reset")));
                Assert.Equal(caller.Token, cancelled.CancellationToken);
                break;
            default:
                result = scope.Fail(new GuardException(S3FailureKind.Integrity));
                Assert.Equal(S3FailureKind.Integrity, Assert.IsType<S3RequestException>(result).Kind);
                break;
        }
    }

    [Fact]
    public async Task Fail_never_throws_and_its_first_answer_holds_across_threads()
    {
        var gate = new OperationGate(1, new ManualClock());
        await using var scope = await gate.EnterAsync(S3Operation.Read, Long, default, null);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var racers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await start.Task;
            return scope.Fail(new GuardException(i % 2 == 0 ? S3FailureKind.Integrity : S3FailureKind.InvalidResponse));
        })).ToArray();
        start.SetResult();
        var answers = await Within(Task.WhenAll(racers));
        Assert.All(answers, a => Assert.Same(answers[0], a));
        Assert.Same(answers[0], scope.Fail(new IOException()));
    }

    internal static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    internal static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
