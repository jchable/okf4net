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

    /// <summary>
    /// Passed as <c>--read-only</c> when <see langword="true"/> (default): a sanctioned
    /// computation or an attester has no reason to write into the image, and the writable
    /// scratch the stages genuinely need is named explicitly by <see cref="TmpfsMounts"/>
    /// instead of being the whole filesystem. Verified against real Docker for all three
    /// paths, including the SQL wrapper's driver install, both with the default
    /// <c>/tmp</c> mount and with a custom one — which is why this is a default rather
    /// than an opt-in.
    /// </summary>
    public bool ReadOnlyRootFilesystem { get; init; } = true;

    /// <summary>
    /// The writable paths under <see cref="ReadOnlyRootFilesystem"/>, memory-backed and
    /// destroyed with the container, one <c>--tmpfs</c> each. <c>/tmp</c> by default.
    ///
    /// <para>The <b>first</b> entry is the run's scratch directory: <see cref="ToRunSpec"/>
    /// passes it into the container as <c>TMPDIR</c> (unless the consumer's
    /// <c>Environment</c> already sets one), and that is where the SQL wrapper's fallback
    /// driver install goes, where the attester bootstrap writes the bundle's attester
    /// module before importing it, and what a sanctioned script's own temp files use. So
    /// <c>["/scratch"]</c> under a read-only root works — <c>/tmp</c> is then read-only
    /// and nothing here writes to it. Each entry must be an absolute container path,
    /// optionally followed by engine options (<c>/scratch:size=64m</c>); anything else is
    /// rejected here rather than silently sending <c>TMPDIR</c> back to a read-only
    /// <c>/tmp</c>.</para>
    ///
    /// <para>An empty list is allowed on a <see cref="ContainerRuntimeProfile"/>, even with
    /// a read-only root: a script that writes nothing, or a
    /// <see cref="ContainerRuntimeKind.SqlClient"/> image with the driver vendored in,
    /// needs no scratch at all, and that is the most locked-down configuration a profile
    /// can express. Its cost is that the wrapper's fallback install then has nowhere to
    /// go, so a bare Python image fails the run. For the same reason a <c>TMPDIR</c> set
    /// in <see cref="ContainerRuntimeProfile.Environment"/> is not checked against these
    /// mounts. Under a read-only root it still has to lead to one of them to be usable:
    /// Python's <c>tempfile</c>, which pip and the wrapper's <c>--target</c> both go
    /// through, never creates it, and skips on through <c>TEMP</c>, <c>TMP</c>,
    /// <c>/tmp</c>, <c>/var/tmp</c> and <c>/usr/tmp</c> — read-only unless mounted — so a
    /// <c>TMPDIR</c> that reaches no mount fails the fallback install and a script's own
    /// temp files.</para>
    ///
    /// <para>An attester, unlike a profile, needs this scratch on <i>every</i> run, so on
    /// <see cref="ContainerAttesterOptions.Isolation"/> a read-only root that leaves it
    /// nowhere to write is rejected when the <see cref="ContainerAttester"/> is
    /// constructed: no mount at all, or a <c>TMPDIR</c> in
    /// <see cref="ContainerAttesterOptions.Environment"/> from which <c>tempfile</c>
    /// reaches none of these mounts (see that constructor for the exact rule). Either
    /// could never attest anything, and would say so only as a Python traceback, after
    /// the executor had already run the computation.</para>
    /// </summary>
    public IReadOnlyList<string> TmpfsMounts
    {
        get => _tmpfsMounts;
        init => _tmpfsMounts = ScratchDirectory.ValidateMounts(value, nameof(TmpfsMounts));
    }

    /// <summary><c>--memory</c>, in bytes. Must be positive: zero means <i>unlimited</i> to docker and podman.</summary>
    public long MemoryBytes { get => _memoryBytes; init => _memoryBytes = ResourceCeiling.Positive(value, nameof(MemoryBytes)); }

    /// <summary><c>--cpus</c>. Must be positive (see <see cref="MemoryBytes"/>).</summary>
    public double Cpus { get => _cpus; init => _cpus = ResourceCeiling.Positive(value, nameof(Cpus)); }

    /// <summary><c>--pids-limit</c>. Must be positive (see <see cref="MemoryBytes"/>).</summary>
    public int PidsLimit { get => _pidsLimit; init => _pidsLimit = ResourceCeiling.Positive(value, nameof(PidsLimit)); }

    /// <summary>Wall-clock ceiling on one run, enforced by the engine. Must be positive and enforceable by a timer.</summary>
    public TimeSpan Timeout { get => _timeout; init => _timeout = ResourceCeiling.Timeout(value, nameof(Timeout)); }

    /// <summary>
    /// Builds the run spec for one container with every isolation setting of this record
    /// applied — including <c>TMPDIR</c>, pointed at the first of <see cref="TmpfsMounts"/>
    /// unless <paramref name="environment"/> already sets one, so every consumer honours a
    /// configured scratch mount without repeating it.
    /// </summary>
    internal ContainerRunSpec ToRunSpec(string image, IReadOnlyList<string> command, string? stdin, IReadOnlyDictionary<string, string> environment, string? networkMode) =>
        new(image, command, stdin, ScratchDirectory.Apply(environment, TmpfsMounts), networkMode, MemoryBytes, Cpus, PidsLimit, Timeout)
        {
            ReadOnlyRootFilesystem = ReadOnlyRootFilesystem,
            TmpfsMounts = TmpfsMounts,
            User = User,
            DropAllCapabilities = DropAllCapabilities,
            NoNewPrivileges = NoNewPrivileges,
        };

    private readonly IReadOnlyList<string> _tmpfsMounts = ["/tmp"];
    private readonly long _memoryBytes = 512L * 1024 * 1024;
    private readonly double _cpus = 1.0;
    private readonly int _pidsLimit = 64;
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);
}
