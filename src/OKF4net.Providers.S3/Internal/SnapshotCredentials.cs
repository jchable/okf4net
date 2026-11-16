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
