// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Attestation.Containers.Internal;

/// <summary>
/// Makes a configured <c>TmpfsMounts</c> list something the code inside the container
/// actually uses, rather than a flag the engine emits while the Python goes on writing
/// to a hardcoded <c>/tmp</c>.
///
/// The mechanism is <c>TMPDIR</c>, pointed at the first mount. Every writer inside the
/// containers already goes through it: the attester bootstrap's
/// <c>tempfile.NamedTemporaryFile</c>, pip's own unpack/build directories during the
/// SQL wrapper's fallback install, and the wrapper's <c>--target</c>, which it derives
/// from <c>tempfile.gettempdir()</c>. Without it, a host that mounted <c>/scratch</c>
/// under a read-only root got a container whose <c>/tmp</c> was read-only, and every
/// attestation failed with "No usable temporary directory" — verified against real
/// Docker. It is also a POSIX convention non-Python interpreters honour, so a
/// <see cref="ContainerRuntimeKind.Script"/> run on another image finds the same
/// scratch the same way.
///
/// The value travels in the environment dictionary, one <c>-e</c> element like every
/// other variable; nothing is spliced into a command string.
/// </summary>
internal static class ScratchDirectory
{
    /// <summary>The environment variable every stage points at its scratch mount.</summary>
    internal const string VariableName = "TMPDIR";

    /// <summary>
    /// The container path of one <c>--tmpfs</c> entry. The flag's syntax is
    /// <c>path[:options]</c> (e.g. <c>/scratch:size=64m</c>), and docker and podman both
    /// split it at the first colon, so this does too.
    /// </summary>
    internal static string MountPath(string entry)
    {
        var colon = entry.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? entry : entry[..colon];
    }

    /// <summary>
    /// The variables Python's <c>tempfile</c> reads, in order, before its fixed POSIX
    /// fallbacks (<c>tempfile._candidate_tempdir_list</c>); an empty value is skipped.
    /// </summary>
    private static readonly string[] TempVariables = [VariableName, "TEMP", "TMP"];

    /// <summary>The fixed POSIX directories <c>tempfile</c> tries after <see cref="TempVariables"/>.</summary>
    private static readonly string[] FallbackDirectories = ["/tmp", "/var/tmp", "/usr/tmp"];

    /// <summary>
    /// Writable under <c>--read-only</c> whatever the host mounts: docker and podman both
    /// put a tmpfs on <c>/dev</c> and <c>/dev/shm</c> (verified for docker).
    /// </summary>
    private static readonly string[] EngineWritableDirectories = ["/dev", "/dev/shm"];

    /// <summary>
    /// Whether, under a read-only root, Python's <c>tempfile</c> can find a writable
    /// directory with <paramref name="environment"/> — the environment the container
    /// actually gets, so after <see cref="Apply"/>. <c>tempfile</c> never creates a
    /// directory: it walks <c>TMPDIR</c>, <c>TEMP</c>, <c>TMP</c> (skipping empty ones),
    /// then <c>/tmp</c>, <c>/var/tmp</c>, <c>/usr/tmp</c> and finally the working
    /// directory, and takes the first it can write to. A candidate counts when it is
    /// exactly one of <paramref name="tmpfsMounts"/> or an engine-provided tmpfs
    /// (<see cref="EngineWritableDirectories"/>); a subdirectory of a mount does not exist
    /// in a fresh tmpfs.
    ///
    /// <para>Built to reject only what is sure to fail, because a wrong rejection blocks a
    /// working configuration while a wrong acceptance only restores the run-time failure
    /// this guard exists to bring forward. So candidates are resolved the way
    /// <c>tempfile</c>'s <c>abspath</c> resolves them — lexically, against <c>/</c>, the
    /// working directory of an image with no <c>WORKDIR</c> such as the default
    /// <c>python:3.12-slim</c>. Two things the image decides are still not seen: a
    /// <c>WORKDIR</c> that is itself a mount, and <c>TEMP</c>/<c>TMP</c> set by its own
    /// <c>ENV</c>. Neither counts, so an image reaching a mount only that way is reported
    /// as having none. Nor can this see which engine runs the container: podman's default
    /// <c>--read-only-tmpfs</c> also makes <c>/run</c>, <c>/tmp</c> and <c>/var/tmp</c>
    /// writable, which docker does not.</para>
    /// </summary>
    internal static bool ReachesWritableDirectory(IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> tmpfsMounts)
    {
        var writable = tmpfsMounts
            .Select(mount => Resolve(MountPath(mount)))
            .Concat(EngineWritableDirectories)
            .ToHashSet(StringComparer.Ordinal);

        return TempVariables
            .Select(name => environment.TryGetValue(name, out var value) ? value : string.Empty)
            .Where(value => value.Length > 0)
            .Concat(FallbackDirectories)
            .Any(candidate => writable.Contains(Resolve(candidate)));
    }

    /// <summary>
    /// <paramref name="path"/> made absolute against <c>/</c> and normalized lexically
    /// (empty and <c>.</c> segments dropped, <c>..</c> popped) — what Python's
    /// <c>os.path.abspath</c> does with a working directory of <c>/</c>.
    /// </summary>
    private static string Resolve(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment.Length > 0 && segment != ".")
            {
                segments.Add(segment);
            }
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>
    /// Returns a copy of <paramref name="mounts"/> if every entry names an absolute
    /// container path; otherwise throws naming <paramref name="property"/>. Checked at
    /// configuration time because a malformed entry does not fail loudly where it is
    /// used: an empty <c>TMPDIR</c> is silently skipped by Python's <c>tempfile</c>, and
    /// a relative one is resolved against the image's working directory rather than
    /// naming the mount, so <c>tempfile</c> can fall back to <c>/tmp</c> — the read-only
    /// path the mount was meant to replace — and the run then fails with a traceback
    /// about temporary directories instead of a message about the profile. Copied so a list the host
    /// mutates after validation cannot bypass the check.
    /// </summary>
    internal static IReadOnlyList<string> ValidateMounts(IReadOnlyList<string> mounts, string property)
    {
        ArgumentNullException.ThrowIfNull(mounts, property);
        foreach (var entry in mounts)
        {
            if (entry is null || !MountPath(entry).StartsWith('/'))
            {
                throw new ArgumentException(
                    $"{property} entries must be absolute container paths (optionally followed by ':options'); '{entry}' is not.",
                    property);
            }
        }

        return [.. mounts];
    }

    /// <summary>
    /// <paramref name="environment"/> with <c>TMPDIR</c> pointed at the first entry of
    /// <paramref name="tmpfsMounts"/>. Two cases leave it alone. With no mount there is
    /// no named scratch to point at, so the image's own default applies. And a
    /// <c>TMPDIR</c> the host already set wins, because it is an explicit choice — for
    /// instance a second mount rather than the first.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Apply(
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string> tmpfsMounts)
    {
        if (tmpfsMounts.Count == 0 || environment.ContainsKey(VariableName))
        {
            return environment;
        }

        return new Dictionary<string, string>(environment)
        {
            [VariableName] = MountPath(tmpfsMounts[0]),
        };
    }
}
