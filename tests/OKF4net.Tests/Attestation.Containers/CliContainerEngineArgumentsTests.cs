// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class CliContainerEngineArgumentsTests
{
    private static ContainerRunSpec Spec(
        string image = "python:3.12-slim",
        IReadOnlyDictionary<string, string>? env = null,
        string? network = "none",
        long? memory = 512L * 1024 * 1024,
        double? cpus = 1.0,
        int? pids = 64)
        => new(image, ["python3", "-"], "print()", env ?? new Dictionary<string, string>(), network, memory, cpus, pids, TimeSpan.FromMinutes(1));

    [Fact]
    public void Every_argument_is_its_own_array_element_never_one_concatenated_string()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(env: new Dictionary<string, string> { ["X"] = "a b; rm -rf /" }), "okf-abc");
        // The hostile-looking value must appear as ONE element, never split
        // or interpreted -- proving no shell ever sees this as source text.
        Assert.Contains("X=a b; rm -rf /", args);
        Assert.DoesNotContain(args, a => a.Contains("&&") || a.Contains("|"));
    }

    [Fact]
    public void Includes_run_rm_and_a_unique_name()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(), "okf-xyz");
        Assert.Equal("run", args[0]);
        Assert.Contains("--rm", args);
        var nameIndex = args.ToList().IndexOf("--name");
        Assert.True(nameIndex >= 0);
        Assert.Equal("okf-xyz", args[nameIndex + 1]);
    }

    [Fact]
    public void Includes_resource_limit_flags_with_their_configured_values()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(memory: 256L * 1024 * 1024, cpus: 0.5, pids: 32), "okf-1");
        Assert.Contains("--memory", args);
        Assert.Contains((256L * 1024 * 1024).ToString(), args);
        Assert.Contains("--cpus", args);
        Assert.Contains("0.5", args);
        Assert.Contains("--pids-limit", args);
        Assert.Contains("32", args);
    }

    [Fact]
    public void Omits_network_flag_when_NetworkMode_is_null()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(network: null), "okf-1");
        Assert.DoesNotContain("--network", args);
    }

    [Fact]
    public void Ends_with_the_image_then_the_command()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(image: "postgres:16-alpine"), "okf-1");
        var imageIndex = args.ToList().IndexOf("postgres:16-alpine");
        Assert.True(imageIndex >= 0);
        Assert.Equal("python3", args[imageIndex + 1]);
        Assert.Equal("-", args[imageIndex + 2]);
    }

    /// <summary>
    /// A read-only root filesystem stops a sanctioned script from writing anywhere the
    /// image would otherwise let it — and, more to the point, from leaving anything
    /// behind for a later stage of the same run to pick up.
    /// </summary>
    [Fact]
    public void Read_only_root_emits_the_flag()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec() with { ReadOnlyRootFilesystem = true }, "okf-1");
        Assert.Contains("--read-only", args);
    }

    /// <summary>
    /// Every writable path is named explicitly, one <c>--tmpfs</c> per mount, each its
    /// own argument element. A tmpfs is memory-backed and dies with the container, so
    /// naming one is not a hole in the read-only root: it is the writable scratch the
    /// attester bootstrap and pip need, bounded to a path this host chose.
    /// </summary>
    [Fact]
    public void Each_tmpfs_mount_is_its_own_flag_and_value()
    {
        var args = CliContainerEngine.BuildRunArguments(
            Spec() with { ReadOnlyRootFilesystem = true, TmpfsMounts = ["/tmp", "/var/tmp"] },
            "okf-1").ToList();

        var first = args.IndexOf("/tmp");
        Assert.True(first > 0);
        Assert.Equal("--tmpfs", args[first - 1]);

        var second = args.IndexOf("/var/tmp");
        Assert.True(second > 0);
        Assert.Equal("--tmpfs", args[second - 1]);
    }

    /// <summary>
    /// Both are opt-in at the spec level: a caller that says nothing gets neither flag,
    /// so <see cref="ContainerRunSpec"/> keeps behaving exactly as before. The safe
    /// defaults live on <c>ContainerRuntimeProfile</c>, not here.
    /// </summary>
    [Fact]
    public void Neither_flag_appears_unless_asked_for()
    {
        var args = CliContainerEngine.BuildRunArguments(Spec(), "okf-1");
        Assert.DoesNotContain("--read-only", args);
        Assert.DoesNotContain("--tmpfs", args);
    }

    /// <summary>
    /// What reaches the engine for a custom scratch mount, from a real stage through to
    /// the argument list: the mount as its own <c>--tmpfs</c> value, and <c>TMPDIR</c> as
    /// one <c>-e</c> element — never spliced into the in-container command, which stays
    /// the fixed <c>python3 -c &lt;bootstrap&gt;</c>. And nothing anywhere names
    /// <c>/tmp</c>, which under <c>--read-only</c> is exactly the path that is not
    /// writable.
    /// </summary>
    [Fact]
    public async Task A_custom_scratch_mount_reaches_the_engine_as_a_tmpfs_flag_and_one_TMPDIR_element()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(0, """{"ok": true}""", "") };
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions { TmpfsMounts = ["/scratch:size=64m"] });
        await attester.AttestAsync(new AttestationContext(
            Contract: new AttestedComputationContract("python", [], null, null, new Attester("att.py")),
            Computation: new SanctionedComputation(ComputationSource.Inline, "print()", null),
            Bound: new BoundComputation("python", "print()", null, new Dictionary<string, object?>()),
            Values: new Dictionary<string, object?>(),
            Receipt: new Receipt(new Dictionary<string, object?>()),
            AttesterSourceText: "def attest(**_):\n    return {}\n"));

        var args = CliContainerEngine.BuildRunArguments(engine.LastSpec!, "okf-1").ToList();

        var tmpfs = args.IndexOf("--tmpfs");
        Assert.Equal("/scratch:size=64m", args[tmpfs + 1]);
        var tmpdir = args.IndexOf("TMPDIR=/scratch");
        Assert.True(tmpdir > 0);
        Assert.Equal("-e", args[tmpdir - 1]);
        Assert.Contains("--read-only", args);
        Assert.DoesNotContain(args, a => a.Contains("/tmp", StringComparison.Ordinal));

        var image = args.IndexOf("python:3.12-slim");
        Assert.Equal(["python3", "-c", ContainerAttester.Bootstrap], args.Skip(image + 1));
    }
}
