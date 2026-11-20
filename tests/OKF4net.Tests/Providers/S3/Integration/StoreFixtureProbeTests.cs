// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests.Providers.S3.Integration;

/// <summary>
/// The engine probe of <see cref="StoreFixture"/> needs no container, so it runs everywhere (not S3Integration): a
/// missing binary or a non-zero exit is a skip that names each candidate's outcome, while an engine that does not answer
/// in time FAILS the run. The dotnet host stands in for an engine: a real process, on every OS, without a script.
/// </summary>
public class StoreFixtureProbeTests
{
    private static readonly string Missing = "okf-no-such-engine-" + Guid.NewGuid().ToString("N")[..12];
    private static readonly TimeSpan Generous = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task A_missing_binary_is_a_skip_that_says_so()
    {
        var (engine, reason) = await StoreFixture.ProbeEnginesAsync([Missing], Generous, "version");
        Assert.Null(engine);
        Assert.Equal($"no container engine answers ({Missing}: missing)", reason);
    }

    [Fact]
    public async Task A_non_zero_exit_is_a_skip_that_names_each_candidates_outcome()
    {
        var dotnet = TestProcess.DotnetHost();
        var (engine, reason) = await StoreFixture.ProbeEnginesAsync([Missing, dotnet], Generous, "okf-no-such-command-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.Null(engine);
        Assert.StartsWith($"no container engine answers ({Missing}: missing; {dotnet}: exit code ", reason, StringComparison.Ordinal);
        Assert.DoesNotContain(": exit code 0", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_candidate_that_answers_is_chosen()
    {
        var dotnet = TestProcess.DotnetHost();
        var (engine, reason) = await StoreFixture.ProbeEnginesAsync([Missing, dotnet], Generous, "--version");
        Assert.Equal(dotnet, engine);
        Assert.Null(reason);
    }

    [Fact]
    public async Task A_candidate_that_does_not_answer_in_time_fails_instead_of_skipping()
    {
        // No process starts, runs and exits within a millisecond: the deadline expires, the tree is killed.
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => StoreFixture.ProbeEnginesAsync([Missing, TestProcess.DotnetHost()], TimeSpan.FromMilliseconds(1), "--version"));
        Assert.Contains("did not answer within", e.Message, StringComparison.Ordinal);
        Assert.IsType<TimeoutException>(e.InnerException);
    }
}
