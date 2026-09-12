// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Attestation.Containers.Internal;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// Everything that bounds a container run other than its image, command,
/// input, environment and network: who the process runs as, which
/// capabilities it keeps, whether it can gain privileges, whether its root is
/// writable, and the four resource ceilings. One record, shared by
/// <see cref="ContainerRuntimeProfile"/> and <see cref="ContainerAttesterOptions"/>,
/// so a hardening decision cannot land on two of the three container
/// consumers and miss the third.
///
/// <para>Defaults are the hardened ones, because the code these containers run
/// is bundle-authored and therefore untrusted: a non-root uid (<c>65534</c>,
/// <c>nobody</c> on every mainstream image), every capability dropped, no
/// privilege escalation, a read-only root with <c>/tmp</c> on a memory-backed
/// tmpfs. A host that needs the image's own user (an image whose entrypoint
/// insists on it) sets <see cref="User"/> to <see langword="null"/>.</para>
/// </summary>
public sealed record ContainerIsolation
{
    /// <summary>Passed as <c>--user</c>; <see langword="null"/> keeps the image's default user. Default <c>65534:65534</c>.</summary>
    public string? User { get; init; } = "65534:65534";

    /// <summary>Passed as <c>--cap-drop ALL</c> when <see langword="true"/> (default).</summary>
    public bool DropAllCapabilities { get; init; } = true;

    /// <summary>Passed as <c>--security-opt no-new-privileges</c> when <see langword="true"/> (default).</summary>
    public bool NoNewPrivileges { get; init; } = true;

    /// <summary>Passed as <c>--read-only</c> when <see langword="true"/> (default). See <see cref="TmpfsMounts"/> for the writable exceptions.</summary>
    public bool ReadOnlyRootFilesystem { get; init; } = true;

    /// <summary>Memory-backed writable paths under a read-only root; <c>/tmp</c> by default (the attester bootstrap and the SQL wrapper's driver install both write there).</summary>
    public IReadOnlyList<string> TmpfsMounts { get; init; } = ["/tmp"];

    /// <summary><c>--memory</c>, in bytes. Must be positive: zero means <i>unlimited</i> to docker and podman.</summary>
    public long MemoryBytes { get => _memoryBytes; init => _memoryBytes = ResourceCeiling.Positive(value, nameof(MemoryBytes)); }

    /// <summary><c>--cpus</c>. Must be positive (see <see cref="MemoryBytes"/>).</summary>
    public double Cpus { get => _cpus; init => _cpus = ResourceCeiling.Positive(value, nameof(Cpus)); }

    /// <summary><c>--pids-limit</c>. Must be positive (see <see cref="MemoryBytes"/>).</summary>
    public int PidsLimit { get => _pidsLimit; init => _pidsLimit = ResourceCeiling.Positive(value, nameof(PidsLimit)); }

    /// <summary>Wall-clock ceiling on one run, enforced by the engine. Must be positive and enforceable by a timer.</summary>
    public TimeSpan Timeout { get => _timeout; init => _timeout = ResourceCeiling.Timeout(value, nameof(Timeout)); }

    /// <summary>Builds the run spec for one container with every isolation setting of this record applied.</summary>
    internal ContainerRunSpec ToRunSpec(string image, IReadOnlyList<string> command, string? stdin, IReadOnlyDictionary<string, string> environment, string? networkMode) =>
        new(image, command, stdin, environment, networkMode, MemoryBytes, Cpus, PidsLimit, Timeout)
        {
            ReadOnlyRootFilesystem = ReadOnlyRootFilesystem,
            TmpfsMounts = TmpfsMounts,
            User = User,
            DropAllCapabilities = DropAllCapabilities,
            NoNewPrivileges = NoNewPrivileges,
        };

    private readonly long _memoryBytes = 512L * 1024 * 1024;
    private readonly double _cpus = 1.0;
    private readonly int _pidsLimit = 64;
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);
}
