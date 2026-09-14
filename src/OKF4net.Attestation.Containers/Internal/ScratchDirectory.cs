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
    /// Whether, under a read-only root, Python's <c>tempfile</c> will find one of
    /// <paramref name="tmpfsMounts"/> among the directories it tries with
    /// <paramref name="environment"/> — the environment the container actually gets, so
    /// after <see cref="Apply"/>. <c>tempfile</c> never creates a directory: it walks
    /// <c>TMPDIR</c>, <c>TEMP</c>, <c>TMP</c>, then <c>/tmp</c>, <c>/var/tmp</c>,
    /// <c>/usr/tmp</c> and finally the working directory, and takes the first it can
    /// write to. Only a candidate that is exactly a mount's path counts (trailing slashes
    /// ignored): a subdirectory of a mount does not exist in a fresh tmpfs.
    ///
    /// <para>Conservative about what the image decides and this cannot see: a relative
    /// candidate and the final working-directory fallback both resolve against the
    /// image's <c>WORKDIR</c>, and the image's own <c>ENV</c> can set <c>TEMP</c> or
    /// <c>TMP</c>. None of those counts here, so an image relying on one of them to reach
    /// a mount is reported as having none.</para>
    /// </summary>
    internal static bool ReachesMount(IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> tmpfsMounts)
    {
        var candidates = TempVariables
            .Select(name => environment.TryGetValue(name, out var value) ? value : string.Empty)
            .Where(value => value.StartsWith('/'))
            .Concat(FallbackDirectories);

        return candidates.Any(candidate => tmpfsMounts.Any(
            mount => string.Equals(MountPath(mount).TrimEnd('/'), candidate.TrimEnd('/'), StringComparison.Ordinal)));
    }

    /// <summary>
    /// Returns a copy of <paramref name="mounts"/> if every entry names an absolute
    /// container path; otherwise throws naming <paramref name="property"/>. Checked at
    /// configuration time because a malformed entry does not fail loudly where it is
    /// used: an empty or relative <c>TMPDIR</c> is silently skipped by Python's
    /// <c>tempfile</c>, which falls back to <c>/tmp</c> — the read-only path the mount
    /// was meant to replace — and the run then fails with a traceback about temporary
    /// directories instead of a message about the profile. Copied so a list the host
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
