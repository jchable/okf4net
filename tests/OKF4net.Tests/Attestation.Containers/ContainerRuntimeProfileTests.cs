// SPDX-License-Identifier: LGPL-3.0-or-later
using System;
using System.Collections.Generic;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

/// <summary>
/// The resource ceilings are a security control, not a tuning knob: the design
/// requires that "une configuration invalide ou accidentellement illimitée doit
/// être rejetée plutôt que silencieusement ignorée". They are passed straight
/// through to the engine's <c>--memory</c> / <c>--cpus</c> / <c>--pids-limit</c>
/// flags, and to docker and podman a <b>zero or negative</b> value there does not
/// mean "invalid" — it means <b>unlimited</b>. So a profile built with
/// <c>MemoryBytes = 0</c> silently removed the very ceiling it appeared to set,
/// and a negative <see cref="ContainerRuntimeProfile.Timeout"/> surfaced much
/// later as a raw <see cref="ArgumentOutOfRangeException"/> out of a
/// <see cref="System.Threading.CancellationTokenSource"/> constructor, naming
/// neither the profile nor the property.
/// </summary>
public class ContainerRuntimeProfileTests
{
    private static ContainerRuntimeProfile Default() =>
        new() { Image = "python:3.12-slim", Kind = ContainerRuntimeKind.Script };

    [Fact]
    public void A_default_profile_is_valid()
    {
        var profile = Default();
        Assert.True(profile.MemoryBytes > 0);
        Assert.True(profile.Cpus > 0);
        Assert.True(profile.PidsLimit > 0);
        Assert.True(profile.Timeout > TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void A_non_positive_memory_ceiling_is_rejected(long bytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Default() with { MemoryBytes = bytes });

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.5d)]
    public void A_non_positive_cpu_ceiling_is_rejected(double cpus) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Default() with { Cpus = cpus });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_pids_ceiling_is_rejected(int pids) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Default() with { PidsLimit = pids });

    [Fact]
    public void A_non_positive_timeout_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Default() with { Timeout = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => Default() with { Timeout = TimeSpan.FromSeconds(-1) });
    }

    /// <summary>
    /// <see cref="ContainerAttesterOptions"/> carries the same four ceilings and
    /// runs bundle-authored code just as the executors do, so it gets the same
    /// treatment — an attester with no memory ceiling is no safer than an
    /// executor with none.
    /// </summary>
    [Fact]
    public void The_attester_options_enforce_the_same_ceilings()
    {
        var options = new ContainerAttesterOptions();
        Assert.True(options.MemoryBytes > 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => options with { MemoryBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => options with { Cpus = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => options with { PidsLimit = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => options with { Timeout = TimeSpan.Zero });
    }

    /// <summary>
    /// Network isolation defaults by kind and is overridable on the profile, which
    /// is the point of moving it off the executors: a host that vendors its SQL
    /// driver into its own image can close the SqlClient path down, and one that
    /// needs a Script run to fetch something can open it, without either forking an
    /// executor. The defaults stay closed where they can be.
    /// </summary>
    [Fact]
    public void Network_mode_defaults_by_kind_and_is_overridable()
    {
        Assert.Equal("none", Default().NetworkMode);
        Assert.Null((Default() with { Kind = ContainerRuntimeKind.SqlClient }).NetworkMode);

        Assert.Equal("my-db-net", (Default() with { NetworkMode = "my-db-net" }).NetworkMode);
        Assert.Equal("none", (Default() with { Kind = ContainerRuntimeKind.SqlClient, NetworkMode = "none" }).NetworkMode);
    }

    /// <summary>
    /// <see cref="ContainerRunSpec"/> is a public record a host can build by hand and
    /// hand straight to <see cref="CliContainerEngine"/>, bypassing the profile
    /// entirely — so the ceiling guarantee cannot live only in the profile's init
    /// accessors. Without this, such a host still got <c>--memory 0</c>, which docker
    /// reads as unlimited.
    /// </summary>
    [Fact]
    public void The_engine_refuses_to_emit_a_ceiling_flag_that_removes_the_ceiling()
    {
        var spec = new ContainerRunSpec(
            Image: "python:3.12-slim",
            Command: ["python3", "-"],
            Stdin: null,
            Environment: new Dictionary<string, string>(),
            NetworkMode: "none",
            MemoryBytes: 0,
            Cpus: 1.0,
            PidsLimit: 16,
            Timeout: TimeSpan.FromSeconds(30));

        Assert.Throws<ArgumentOutOfRangeException>(() => CliContainerEngine.BuildRunArguments(spec, "okf-test"));
    }

    /// <summary>
    /// A null ceiling is not a removed ceiling: it means "omit the flag and take the
    /// engine's own default", which is a legitimate host choice and must keep working.
    /// </summary>
    [Fact]
    public void An_absent_ceiling_is_still_allowed()
    {
        var spec = new ContainerRunSpec(
            Image: "python:3.12-slim",
            Command: ["python3", "-"],
            Stdin: null,
            Environment: new Dictionary<string, string>(),
            NetworkMode: "none",
            MemoryBytes: null,
            Cpus: null,
            PidsLimit: null,
            Timeout: null);

        var args = CliContainerEngine.BuildRunArguments(spec, "okf-test");
        Assert.DoesNotContain("--memory", args);
        Assert.DoesNotContain("--cpus", args);
        Assert.DoesNotContain("--pids-limit", args);
    }

    /// <summary>
    /// Infinity satisfies <c>&gt; 0</c>, so an ordinary positivity check would let it
    /// through and the engine would receive the literal <c>--cpus Infinity</c>.
    /// </summary>
    [Fact]
    public void An_infinite_cpu_ceiling_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Default() with { Cpus = double.PositiveInfinity });

    /// <summary>
    /// The message has to name the property, because the failure surfaces at
    /// host-configuration time where several ceilings are set together.
    /// </summary>
    [Fact]
    public void The_rejection_names_the_offending_property()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Default() with { PidsLimit = 0 });
        Assert.Equal("PidsLimit", ex.ParamName);
    }
}
