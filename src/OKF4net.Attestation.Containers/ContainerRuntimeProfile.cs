// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Attestation.Containers.Internal;

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
/// is built per profile and registered under that name in
/// <c>AttestationRuntimeRegistry</c>.
/// </summary>
public sealed record ContainerRuntimeProfile
{
    /// <summary>The image to run the computation in. For <see cref="ContainerRuntimeKind.SqlClient"/>, must be Python-capable — the wrapper is always <c>python3</c>.</summary>
    public required string Image { get; init; }

    /// <summary>Which execution protocol applies.</summary>
    public required ContainerRuntimeKind Kind { get; init; }

    /// <summary>The interpreter invoked for <see cref="ContainerRuntimeKind.Script"/> profiles (e.g. <c>"python3"</c>). Ignored for <see cref="ContainerRuntimeKind.SqlClient"/>.</summary>
    public string Interpreter { get; init; } = "python3";

    /// <summary>Environment variables passed to every container run under this profile (e.g. <c>OKF_CONN</c> for a <see cref="ContainerRuntimeKind.SqlClient"/> profile).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The <c>--network</c> mode for every run under this profile, or <see langword="null"/>
    /// to leave the engine's default. Defaults to <c>"none"</c> for a
    /// <see cref="ContainerRuntimeKind.Script"/> profile — a sanctioned script has no
    /// business reaching the network — and to <see langword="null"/> for
    /// <see cref="ContainerRuntimeKind.SqlClient"/>, which must reach both the database
    /// and a package index.
    ///
    /// It lives here, on the profile, rather than being hardcoded per executor, because
    /// hardening is the host's decision: a host that vendors the driver into its own
    /// image can close the SqlClient path down to its database's network, and one that
    /// needs a Script run to fetch something can open it, without either having to fork
    /// an executor.
    /// </summary>
    public string? NetworkMode
    {
        get => _networkMode ?? (Kind == ContainerRuntimeKind.Script ? "none" : null);
        init => _networkMode = value;
    }

    /// <summary>Default <c>--memory</c> ceiling. 512 MiB. Must be positive: zero or negative means <i>unlimited</i> to docker and podman, so it is rejected rather than silently removing the ceiling.</summary>
    public long MemoryBytes
    {
        get => _memoryBytes;
        init => _memoryBytes = ResourceCeiling.Positive(value, nameof(MemoryBytes));
    }

    /// <summary>Default <c>--cpus</c> ceiling. Must be positive (see <see cref="MemoryBytes"/>).</summary>
    public double Cpus
    {
        get => _cpus;
        init => _cpus = ResourceCeiling.Positive(value, nameof(Cpus));
    }

    /// <summary>Default <c>--pids-limit</c> ceiling. Must be positive (see <see cref="MemoryBytes"/>).</summary>
    public int PidsLimit
    {
        get => _pidsLimit;
        init => _pidsLimit = ResourceCeiling.Positive(value, nameof(PidsLimit));
    }

    /// <summary>Wall-clock ceiling enforced independently of the caller's <see cref="CancellationToken"/>. Must be positive.</summary>
    public TimeSpan Timeout
    {
        get => _timeout;
        init => _timeout = ResourceCeiling.Positive(value, nameof(Timeout));
    }

    private readonly string? _networkMode;
    private readonly long _memoryBytes = 512L * 1024 * 1024;
    private readonly double _cpus = 1.0;
    private readonly int _pidsLimit = 64;
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Configuration for ContainerAttester — always a
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

    /// <summary>Default <c>--memory</c> ceiling. 256 MiB — an attester is a pure function over its inputs, never a network call. Must be positive: zero or negative means <i>unlimited</i> to docker and podman.</summary>
    public long MemoryBytes
    {
        get => _memoryBytes;
        init => _memoryBytes = ResourceCeiling.Positive(value, nameof(MemoryBytes));
    }

    /// <summary>Default <c>--cpus</c> ceiling. Must be positive (see <see cref="MemoryBytes"/>).</summary>
    public double Cpus
    {
        get => _cpus;
        init => _cpus = ResourceCeiling.Positive(value, nameof(Cpus));
    }

    /// <summary>Default <c>--pids-limit</c> ceiling. Must be positive (see <see cref="MemoryBytes"/>).</summary>
    public int PidsLimit
    {
        get => _pidsLimit;
        init => _pidsLimit = ResourceCeiling.Positive(value, nameof(PidsLimit));
    }

    /// <summary>Wall-clock ceiling. Must be positive.</summary>
    public TimeSpan Timeout
    {
        get => _timeout;
        init => _timeout = ResourceCeiling.Positive(value, nameof(Timeout));
    }

    private readonly long _memoryBytes = 256L * 1024 * 1024;
    private readonly double _cpus = 0.5;
    private readonly int _pidsLimit = 32;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
}
