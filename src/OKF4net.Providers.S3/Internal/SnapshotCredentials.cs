// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;

namespace OKF4net.Providers.S3.Internal;

/// <summary>The SDK's view of credentials: the current operation's snapshot, never the host's provider (spec §4.4).</summary>
internal sealed class SnapshotCredentials : AWSCredentials
{
    public override ImmutableCredentials GetCredentials() =>
        OperationContext.Current.Value?.Credentials ?? throw new GuardException(S3FailureKind.Credentials);

    public override Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromResult(GetCredentials());
}

/// <summary>
/// Resolves the host's credentials for one operation (spec §4.4): on the thread pool, so a provider that blocks
/// synchronously cannot hold the caller; under the operation's deadline; a late result is dropped, and the permit
/// stays held until the abandoned call really ends — at most MaxConcurrentRequests threads per provider.
/// </summary>
internal sealed class CredentialResolver(AWSCredentials? host)
{
    /// <summary>An anonymous source (#291): there is no host credential to ask, and the operation carries no snapshot.</summary>
    /// <summary>
    /// Resolves, or throws an exception that is ALREADY final: an S3RequestException with Kind Credentials (a
    /// failure, a null result, or the deadline reached while waiting) or Disposed (the provider disposed while
    /// waiting), or the caller's OperationCanceledException carrying the caller's own token. It never calls
    /// <see cref="OperationScope.Fail"/>: the caller's catch passes that final exception to Fail first, which keeps
    /// it as is (spec §4.5), so a credentials timeout reads Credentials through every member.
    /// </summary>
    public async Task<ImmutableCredentials?> ResolveAsync(OperationScope scope)
    {
        if (host is null)
        {
            return null; // anonymous: nothing is resolved, nothing is signed (the client carries AnonymousAWSCredentials)
        }

        // The asynchronous member a refreshing provider overrides, isolated on the pool: a provider whose async
        // path runs a synchronous call before returning its task cannot hold the caller's thread either.
        var call = Task.Run(() => host.GetCredentialsAsync(), CancellationToken.None);
        try
        {
            var snapshot = await call.WaitAsync(scope.Token).ConfigureAwait(false);
            return snapshot ?? throw new S3RequestException(scope.Operation, S3FailureKind.Credentials, null, scope.Key);
        }
        catch (OperationCanceledException e)
        {
            // The caller's cancellation is sampled first, then the permit is tied to the abandoned call.
            var callerCancelled = scope.CallerCancelled;
            var disposed = scope.ProviderDisposed;
            scope.HoldPermitUntil(call);
            if (callerCancelled)
            {
                throw OperationScope.CallerCancellation(e, scope.CallerToken);
            }

            throw new S3RequestException(scope.Operation, disposed ? S3FailureKind.Disposed : S3FailureKind.Credentials, null, scope.Key, e);
        }
        catch (Exception e) when (e is not S3RequestException and not OutOfMemoryException)
        {
            throw new S3RequestException(scope.Operation, S3FailureKind.Credentials, null, scope.Key, e);
        }
    }
}
