// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// Thrown by the provider's own handlers to stop a request (refused authority, transport bound). It derives from
/// <see cref="Exception"/> only, so the SDK does not retry it (gate G6), and it carries no text from the request.
/// </summary>
internal sealed class GuardException(S3FailureKind kind) : Exception("request stopped by the S3 provider's guard: " + kind)
{
    public S3FailureKind Kind { get; } = kind;
}
