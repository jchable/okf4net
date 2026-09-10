// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers;

/// <summary>Which execution protocol a <see cref="ContainerRuntimeProfile"/> uses.</summary>
public enum ContainerRuntimeKind
{
    /// <summary>The bound computation text is a complete, standalone script — run directly.</summary>
    Script,

    /// <summary>The bound computation text is SQL, executed by a project-authored wrapper against an externally-configured database.</summary>
    SqlClient,
}

/// <summary>
/// Host-supplied configuration for one bundle <c>runtime</c> name (e.g.
/// <c>"python"</c>, <c>"postgres"</c>). One ContainerAttestationRuntime
/// (Task 8) is built per profile and registered under that name in
/// <c>AttestationRuntimeRegistry</c>.
/// </summary>
public sealed record ContainerRuntimeProfile
{
    /// <summary>The image to run the computation in. For <see cref="ContainerRuntimeKind.SqlClient"/>, must be Python-capable — the wrapper is always <c>python3</c> (Task 6).</summary>
    public required string Image { get; init; }

    /// <summary>Which execution protocol applies.</summary>
    public required ContainerRuntimeKind Kind { get; init; }

    /// <summary>The interpreter invoked for <see cref="ContainerRuntimeKind.Script"/> profiles (e.g. <c>"python3"</c>). Ignored for <see cref="ContainerRuntimeKind.SqlClient"/>.</summary>
    public string Interpreter { get; init; } = "python3";

    /// <summary>Environment variables passed to every container run under this profile (e.g. <c>OKF_CONN</c> for a <see cref="ContainerRuntimeKind.SqlClient"/> profile).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Default <c>--memory</c> ceiling. 512 MiB.</summary>
    public long MemoryBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Default <c>--cpus</c> ceiling.</summary>
    public double Cpus { get; init; } = 1.0;

    /// <summary>Default <c>--pids-limit</c> ceiling.</summary>
    public int PidsLimit { get; init; } = 64;

    /// <summary>Wall-clock ceiling enforced independently of the caller's <see cref="CancellationToken"/>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Configuration for ContainerAttester (Task 7) — always a
/// fixed, small Python image, independent of whatever image the executor's
/// <see cref="ContainerRuntimeProfile"/> uses (a <see cref="ContainerRuntimeKind.SqlClient"/>
/// profile's image has no Python at all).
/// </summary>
public sealed record ContainerAttesterOptions
{
    /// <summary>The attester's own image. Defaults to a small official Python image.</summary>
    public string Image { get; init; } = "python:3.12-slim";

    /// <summary>Environment variables passed to every attester run.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Default <c>--memory</c> ceiling. 256 MiB — an attester is a pure function over its inputs, never a network call.</summary>
    public long MemoryBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Default <c>--cpus</c> ceiling.</summary>
    public double Cpus { get; init; } = 0.5;

    /// <summary>Default <c>--pids-limit</c> ceiling.</summary>
    public int PidsLimit { get; init; } = 32;

    /// <summary>Wall-clock ceiling.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}
