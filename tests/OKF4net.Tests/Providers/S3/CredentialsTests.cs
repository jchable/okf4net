// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;
using static OKF4net.Tests.Providers.S3.OperationScopeTests;

namespace OKF4net.Tests.Providers.S3;

/// <summary>
/// Spec §4.4, identifiers: isolated, bounded, one snapshot per operation. Deadlines run on a <see cref="ManualClock"/>;
/// a host that blocks waits on an event the test sets (bounded, so a regression that calls it inline fails instead of hanging).
/// </summary>
public class CredentialsTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private sealed class HostCredentials(Func<int, ImmutableCredentials> get) : AWSCredentials
    {
        public int Calls;
        public override ImmutableCredentials GetCredentials() => get(Interlocked.Increment(ref Calls));
    }

    /// <summary>A host whose asynchronous member returns a task the test completes.</summary>
    private sealed class PendingHost : AWSCredentials
    {
        public TaskCompletionSource<ImmutableCredentials> Result { get; } = new();

        public override ImmutableCredentials GetCredentials() => throw new InvalidOperationException("the synchronous member must not be used");

        public override Task<ImmutableCredentials> GetCredentialsAsync() => Result.Task;
    }

    [Fact]
    public async Task A_provider_blocking_synchronously_is_a_Credentials_failure_within_the_deadline()
    {
        var clock = new ManualClock();
        var gate = new OperationGate(2, clock);
        using var never = new ManualResetEventSlim();
        var scope = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromMilliseconds(200), default, null);
        try
        {
            var resolving = new CredentialResolver(new HostCredentials(_ => { never.Wait(HangGuard); return null!; })).ResolveAsync(scope);
            Assert.False(resolving.IsCompleted); // the caller's thread was not held by the host

            clock.Advance(TimeSpan.FromMilliseconds(200));
            var e = await Assert.ThrowsAsync<S3RequestException>(() => Within(resolving));
            Assert.Equal(S3FailureKind.Credentials, e.Kind);
            Assert.Equal("S3 read failed: Credentials", e.Message);
        }
        finally
        {
            never.Set();
            await scope.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_provider_whose_task_never_ends_is_a_Credentials_failure_at_the_deadline()
    {
        var clock = new ManualClock();
        var gate = new OperationGate(1, clock);
        var host = new PendingHost();
        await using (var scope = await gate.EnterAsync(S3Operation.List, TimeSpan.FromMilliseconds(100), default, null))
        {
            var resolving = new CredentialResolver(host).ResolveAsync(scope);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Equal(S3FailureKind.Credentials, (await Assert.ThrowsAsync<S3RequestException>(() => Within(resolving))).Kind);
        }

        var next = gate.EnterAsync(S3Operation.Read, Long, default, null).AsTask();
        Assert.False(next.IsCompleted); // the abandoned call still holds the permit
        host.Result.SetResult(new ImmutableCredentials("A", "S", null));
        await (await Within(next)).DisposeAsync();
    }

    [Fact]
    public async Task A_late_result_is_ignored_and_the_permit_waits_for_it()
    {
        var clock = new ManualClock();
        using var release = new ManualResetEventSlim();
        var gate = new OperationGate(1, clock);
        var host = new HostCredentials(_ => { release.Wait(HangGuard); return new ImmutableCredentials("A", "S", null); });
        await using (var scope = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromMilliseconds(100), default, null))
        {
            var resolving = new CredentialResolver(host).ResolveAsync(scope);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAsync<S3RequestException>(() => Within(resolving));
        }

        var timedOut = gate.EnterAsync(S3Operation.Read, TimeSpan.FromMilliseconds(100), default, null).AsTask();
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(S3FailureKind.Timeout, (await Assert.ThrowsAsync<S3RequestException>(() => Within(timedOut))).Kind);

        var next = gate.EnterAsync(S3Operation.Read, Long, default, null).AsTask();
        Assert.False(next.IsCompleted);
        release.Set();
        await (await Within(next)).DisposeAsync();
        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public async Task The_callers_cancellation_while_resolving_is_a_cancellation_and_the_permit_still_waits()
    {
        var gate = new OperationGate(1, new ManualClock());
        using var caller = new CancellationTokenSource();
        var host = new PendingHost();
        await using (var scope = await gate.EnterAsync(S3Operation.Read, Long, caller.Token, null))
        {
            var resolving = new CredentialResolver(host).ResolveAsync(scope);
            await caller.CancelAsync();
            var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Within(resolving));
            Assert.IsNotType<S3RequestException>(e);
            Assert.Same(e, scope.Fail(e));
        }

        var next = gate.EnterAsync(S3Operation.Read, Long, default, null).AsTask();
        Assert.False(next.IsCompleted);
        host.Result.SetResult(new ImmutableCredentials("A", "S", null));
        await (await Within(next)).DisposeAsync();
    }

    [Fact]
    public async Task A_failing_provider_is_a_Credentials_failure_that_carries_none_of_its_text()
    {
        var gate = new OperationGate(1, new ManualClock());
        var thrown = new InvalidOperationException("role arn:aws:iam::123:role/x at sts.secret.example");
        await using var scope = await gate.EnterAsync(S3Operation.Read, Long, default, null);
        var e = await Assert.ThrowsAsync<S3RequestException>(() => Within(new CredentialResolver(new HostCredentials(_ => throw thrown)).ResolveAsync(scope)));
        Assert.Equal(S3FailureKind.Credentials, e.Kind);
        Assert.Same(thrown, e.InnerException);
        Assert.Equal("S3 read failed: Credentials", e.Message);
        Assert.Same(e, scope.Fail(e)); // already final: Fail keeps it as is
    }

    [Fact]
    public async Task A_provider_that_answers_null_is_a_Credentials_failure()
    {
        var gate = new OperationGate(1, new ManualClock());
        await using var scope = await gate.EnterAsync(S3Operation.Exists, Long, default, null);
        var e = await Assert.ThrowsAsync<S3RequestException>(() => Within(new CredentialResolver(new HostCredentials(_ => null!)).ResolveAsync(scope)));
        Assert.Equal(S3FailureKind.Credentials, e.Kind);
    }

    [Fact]
    public async Task The_SDK_facing_credentials_return_the_operations_snapshot_and_never_call_the_hosts_provider()
    {
        // Pins the unit: SnapshotCredentials, the only credentials object the SDK is given, returns the operation's
        // snapshot on every sync and async read and never reaches the host's provider (spec §4.4), which here would block
        // after its first call. It does NOT drive the SDK's pipeline: that the real pipeline (signature, retries,
        // redirect re-signing) never calls the host's provider is pinned end to end by ReadTests
        // (A_credentials_provider_that_blocks_fails_a_read_as_Credentials and the disposal tests that follow it).
        using var block = new ManualResetEventSlim();
        var host = new HostCredentials(n => { if (n > 1) block.Wait(HangGuard); return new ImmutableCredentials("A", "S", "T"); });
        var gate = new OperationGate(1, new ManualClock());
        await using var scope = await gate.EnterAsync(S3Operation.Read, Long, default, null);
        OperationContext.Current.Value = new OperationContext(S3Operation.Read) { Credentials = await Within(new CredentialResolver(host).ResolveAsync(scope)) };
        try
        {
            var sdkView = new SnapshotCredentials();
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal("A", sdkView.GetCredentials().AccessKey);
                Assert.Equal("A", (await sdkView.GetCredentialsAsync()).AccessKey);
            }
        }
        finally
        {
            OperationContext.Current.Value = null;
            block.Set();
        }

        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public async Task Each_operation_resolves_its_own_snapshot_and_keeps_it()
    {
        // The host rotates between the two operations: each signs with what it resolved, and resolution happens
        // once per operation (the host's own provider is the cache, spec §4.4).
        var host = new HostCredentials(n => new ImmutableCredentials("A" + n, "S", null));
        var resolver = new CredentialResolver(host);
        var gate = new OperationGate(2, new ManualClock());
        var sdkView = new SnapshotCredentials();
        var resolved1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolved2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<(string Resolved, string Signed)> Operation(TaskCompletionSource resolved)
        {
            var scope = await gate.EnterAsync(S3Operation.Read, Long, default, null);
            await using (scope)
            {
                var snapshot = await resolver.ResolveAsync(scope);
                OperationContext.Current.Value = new OperationContext(S3Operation.Read) { Credentials = snapshot };
                resolved.SetResult();
                await go.Task;
                return (snapshot!.AccessKey, sdkView.GetCredentials().AccessKey);
            }
        }

        var first = Operation(resolved1);
        await Within(resolved1.Task);
        var second = Operation(resolved2);
        await Within(resolved2.Task);
        go.SetResult();

        var (r1, s1) = await Within(first);
        var (r2, s2) = await Within(second);
        Assert.Equal(("A1", "A1"), (r1, s1));
        Assert.Equal(("A2", "A2"), (r2, s2));
        Assert.Equal(2, host.Calls);
        Assert.Null(OperationContext.Current.Value); // the operations' contexts did not leak into the caller's flow
    }

    [Fact]
    public void Outside_an_operation_the_sdk_view_refuses() =>
        Assert.Throws<GuardException>(() => new SnapshotCredentials().GetCredentials());
}
