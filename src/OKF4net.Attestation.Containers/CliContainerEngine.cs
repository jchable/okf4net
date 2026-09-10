// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// The only <see cref="IContainerEngine"/> implementation shipped here:
/// shells to a Docker-CLI-compatible binary (<c>docker</c>, <c>podman</c>,
/// or <c>nerdctl</c> — their <c>run</c> surface is compatible, so one
/// parameterized class covers all three). This task adds only
/// <see cref="BuildRunArguments"/>, the pure part: it never spawns a
/// process, so it is unit-tested directly without Docker. It deliberately
/// does NOT declare <c>: IContainerEngine</c> yet — that interface requires
/// a <c>RunAsync</c> method, added in Task 9 by editing this same file
/// further (not a `partial` split, there is only ever one file); claiming
/// the interface here without it would fail to compile (CS0535, a missing
/// interface member), not just warn.
/// </summary>
public sealed class CliContainerEngine(string binaryName = "docker")
{
    private readonly string _binaryName = binaryName;

    /// <summary>
    /// Builds the <c>run</c> argument list for <paramref name="spec"/>. Every
    /// value (image, env vars, resource limits, the container name) is its
    /// own array element — never concatenated into one string — because this
    /// list is fed straight into <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>
    /// (Task 9), which passes each element to the child process verbatim,
    /// with no shell involved anywhere in this project's own process.
    /// </summary>
    internal static IReadOnlyList<string> BuildRunArguments(ContainerRunSpec spec, string containerName)
    {
        var args = new List<string> { "run", "-i", "--rm", "--name", containerName };

        if (spec.NetworkMode is { } network)
        {
            args.Add("--network");
            args.Add(network);
        }

        if (spec.MemoryBytes is { } memory)
        {
            args.Add("--memory");
            args.Add(memory.ToString(CultureInfo.InvariantCulture));
        }

        if (spec.Cpus is { } cpus)
        {
            args.Add("--cpus");
            args.Add(cpus.ToString(CultureInfo.InvariantCulture));
        }

        if (spec.PidsLimit is { } pids)
        {
            args.Add("--pids-limit");
            args.Add(pids.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var (key, value) in spec.Environment)
        {
            args.Add("-e");
            args.Add($"{key}={value}");
        }

        args.Add(spec.Image);
        args.AddRange(spec.Command);
        return args;
    }
}
