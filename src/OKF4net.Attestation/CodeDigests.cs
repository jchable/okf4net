// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace OKF4net.Attestation;

/// <summary>Which sanctioned code a digest was computed over (#154).</summary>
public enum SanctionedCodeRole
{
    /// <summary>The sanctioned computation: the inline <c># Computation</c> fence, or the <c>computation</c> file's text.</summary>
    Computation,

    /// <summary>The attester's source (<c>attester.resource</c>).</summary>
    Attester,
}

/// <summary>
/// Sanctioned code a run would have executed but the host's allowlist does not hold (#154): its role and
/// the digest the host can approve deliberately. For the host and its logs only — the outcome's
/// <see cref="AttestationOutcome.Reasons"/>, which a tool shows a model, never carry the digest.
/// </summary>
/// <param name="Role">Which code was refused.</param>
/// <param name="Digest">Its <see cref="AttestationCodeDigest.Of"/> digest.</param>
public sealed record UnapprovedCode(SanctionedCodeRole Role, string Digest);

/// <summary>
/// The digest of sanctioned code for the host allowlist (#154): SHA-256 of the UTF-8 encoding of the exact
/// text handed to the runtime, as 64 lowercase hex digits. Nothing is normalised, so a change of line
/// endings is a different digest: what was approved is exactly what runs.
/// </summary>
public static class AttestationCodeDigest
{
    /// <summary>The digest of <paramref name="code"/>.</summary>
    /// <param name="code">The exact text a runtime would receive.</param>
    public static string Of(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    }

    /// <summary>Whether <paramref name="digest"/> has the form <see cref="Of"/> produces, ignoring case.</summary>
    /// <param name="digest">A candidate digest.</param>
    public static bool IsWellFormed(string? digest) =>
        digest is { Length: 64 } && digest.All(char.IsAsciiHexDigit);
}

/// <summary>What a host configures on an <see cref="AttestationOrchestrator"/> beyond its runtimes.</summary>
public sealed record AttestationOrchestratorOptions
{
    /// <summary>Supplies the instant staleness gating is evaluated at; <see cref="SystemClock"/> when <see langword="null"/>.</summary>
    public IOkfClock? Clock { get; init; }

    /// <summary>The gating policy used when a run is not given one; <see cref="StalePolicy.Use"/> when <see langword="null"/>.</summary>
    public StalePolicy? DefaultPolicy { get; init; }

    /// <summary>
    /// The digests (<see cref="AttestationCodeDigest.Of"/>, case ignored) of the sanctioned code this host
    /// allows to run (#154), or <see langword="null"/> (the default) for no allowlist: every capture runs, as
    /// before. When set, a run whose computation or attester source is not in it is refused before any
    /// runtime stage, with <see cref="AttestationOutcome.UnapprovedCode"/> naming each refused role and its
    /// digest for the host. One set covers both roles and every provider: a digest approves a content
    /// wherever a bundle references it. Bundle content is untrusted and the host's configuration trusted, so
    /// this pins <i>what</i> may run whoever can write to the bundle's source; the pinning of a capture to
    /// the image's versions still decides <i>which</i> bytes are read.
    /// </summary>
    public IReadOnlySet<string>? AllowedCodeDigests { get; init; }
}
