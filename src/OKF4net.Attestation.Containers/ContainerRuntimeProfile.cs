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

    /// <summary>Who the container runs as, what it keeps, and the four ceilings. Hardened by default — see <see cref="ContainerIsolation"/>.</summary>
    public ContainerIsolation Isolation { get; init; } = new();

    private readonly string? _networkMode;
    private readonly bool _networkModeSet;
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

    /// <summary>Who the container runs as, what it keeps, and the four ceilings. Hardened by default — see <see cref="ContainerIsolation"/> — with the attester's smaller ceilings (256 MiB, 0.5 CPU, 32 pids, 30 s): an attester is a pure function over its inputs, never a network call.</summary>
    public ContainerIsolation Isolation { get; init; } = new()
    {
        MemoryBytes = 256L * 1024 * 1024,
        Cpus = 0.5,
        PidsLimit = 32,
        Timeout = TimeSpan.FromSeconds(30),
    };
}
