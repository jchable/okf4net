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
    /// <see cref="ContainerRuntimeKind.SqlClient"/>, which must reach its database —
    /// and a package index too, but only when <see cref="Image"/> lacks the driver
    /// and the wrapper falls back to installing it (see
    /// <see cref="SqlClientComputationExecutor"/>).
    ///
    /// It lives here, on the profile, rather than being hardcoded per executor, because
    /// hardening is the host's decision: a host that vendors the driver into its own
    /// image can close the SqlClient path down to its database's network, and one that
    /// needs a Script run to fetch something can open it, without either having to fork
    /// an executor. An explicit <see langword="null"/> is honoured on either kind: it
    /// means the engine's default, not "unset, fall back to the kind's default".
    /// </summary>
    public string? NetworkMode
    {
        // Whether the host SET the value is tracked separately from the value, because
        // null is a legitimate setting ("engine default") and not just the absence of
        // one. Folding the two together (`_networkMode ?? default-for-kind`) meant a
        // Script profile could never express the one override this doc offers: the
        // init accessor stored null and the getter substituted "none" right back.
        get => _networkModeSet ? _networkMode : Kind == ContainerRuntimeKind.Script ? "none" : null;
        init
        {
            _networkMode = value;
            _networkModeSet = true;
        }
    }

    /// <summary>
    /// Mount the container's root filesystem read-only (<c>--read-only</c>). On by
    /// default, for both kinds: a sanctioned computation has no reason to write into
    /// the image, and the writable scratch the stages genuinely need is named
    /// explicitly by <see cref="TmpfsMounts"/> instead of being the whole filesystem.
    /// Verified against real Docker for all three paths, including the SQL wrapper's
    /// driver install — which is why this is a default rather than an opt-in.
    /// </summary>
    public bool ReadOnlyRootFilesystem { get; init; } = true;

    /// <summary>
    /// The writable paths under <see cref="ReadOnlyRootFilesystem"/>, memory-backed and
    /// destroyed with the container. <c>/tmp</c> by default, which is what the attester
    /// bootstrap's temp module and the SQL wrapper's <c>--target</c> install both use.
    /// </summary>
    public IReadOnlyList<string> TmpfsMounts { get; init; } = ["/tmp"];

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

    /// <summary>Wall-clock ceiling enforced independently of the caller's <see cref="CancellationToken"/>. Must be a positive duration a timer can count (at most about 49.7 days): <c>Timeout.InfiniteTimeSpan</c> is rejected rather than read as "no ceiling".</summary>
    public TimeSpan Timeout
    {
        get => _timeout;
        init => _timeout = ResourceCeiling.Timeout(value, nameof(Timeout));
    }

    private readonly string? _networkMode;
    private readonly bool _networkModeSet;
    private readonly long _memoryBytes = 512L * 1024 * 1024;
    private readonly double _cpus = 1.0;
    private readonly int _pidsLimit = 64;
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Configuration for ContainerAttester — always a
/// fixed, small Python image, independent of whatever image the executor's
/// <see cref="ContainerRuntimeProfile"/> uses. That image is whatever the sanctioned
/// code needs — a <see cref="ContainerRuntimeKind.Script"/> profile can run on one
/// with no Python at all — while the attester bootstrap always has its own. (A
/// <see cref="ContainerRuntimeKind.SqlClient"/> image is the one that <i>must</i> be
/// Python-capable: the wrapper is <c>python3</c>.)
/// </summary>
public sealed record ContainerAttesterOptions
{
    /// <summary>The attester's own image. Defaults to a small official Python image.</summary>
    public string Image { get; init; } = "python:3.12-slim";

    /// <summary>Environment variables passed to every attester run.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Mount the attester container's root filesystem read-only. On by default — an attester is a pure function over its inputs.</summary>
    public bool ReadOnlyRootFilesystem { get; init; } = true;

    /// <summary>The writable paths under <see cref="ReadOnlyRootFilesystem"/>. <c>/tmp</c> by default: the bootstrap writes the bundle's attester module there before importing it.</summary>
    public IReadOnlyList<string> TmpfsMounts { get; init; } = ["/tmp"];

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

    /// <summary>Wall-clock ceiling. Must be a positive duration a timer can count (see <see cref="ContainerRuntimeProfile.Timeout"/>).</summary>
    public TimeSpan Timeout
    {
        get => _timeout;
        init => _timeout = ResourceCeiling.Timeout(value, nameof(Timeout));
    }

    private readonly long _memoryBytes = 256L * 1024 * 1024;
    private readonly double _cpus = 0.5;
    private readonly int _pidsLimit = 32;
    private readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);
}
