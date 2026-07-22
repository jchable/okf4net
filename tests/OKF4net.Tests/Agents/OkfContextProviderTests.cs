// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Phase 3 Task 1 skeleton tests for <see cref="OkfContextProvider"/>: options
/// defaults and constructor validation only. The overridden context-provider
/// behavior (progressive disclosure, memory capture) is covered starting
/// Task 2/3; here the overrides just need to exist and return an empty
/// <c>AIContext</c>/no-op, which <see cref="AgentIntegrationTests"/>-style
/// end-to-end coverage is not needed yet.
/// </summary>
public class OkfContextProviderTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    [Fact]
    public void Options_defaults_match_the_documented_values()
    {
        var options = new OkfContextProviderOptions();

        Assert.Equal(2000, options.TokenBudget);
        Assert.True(options.EnableMemoryCapture);
        Assert.Equal("memory", options.MemoryDirectory);
        Assert.Equal(5, options.MaxConceptsInjected);
    }

    [Fact]
    public void Constructor_rejects_null_tools()
    {
        Assert.Throws<ArgumentNullException>(() => new OkfContextProvider(null!));
    }

    [Fact]
    public void Constructor_with_default_options_succeeds()
    {
        var tools = new OkfBundleTools(BundlePath);

        var provider = new OkfContextProvider(tools);

        Assert.NotNull(provider);
    }

    [Fact]
    public void Constructor_with_explicit_null_options_uses_defaults()
    {
        var tools = new OkfBundleTools(BundlePath);

        var provider = new OkfContextProvider(tools, options: null);

        Assert.NotNull(provider);
    }

    [Fact]
    public void Constructor_with_nonpositive_token_budget_still_succeeds()
    {
        var tools = new OkfBundleTools(BundlePath);
        var options = new OkfContextProviderOptions { TokenBudget = 0 };

        var provider = new OkfContextProvider(tools, options);

        Assert.NotNull(provider);
    }

    [Theory]
    [InlineData("")]
    [InlineData("memory/nested")]
    [InlineData("..")]
    [InlineData("mem ory")]
    [InlineData(".hidden")]
    public void Constructor_rejects_a_memory_directory_that_is_not_a_valid_concept_id_segment(string memoryDirectory)
    {
        var tools = new OkfBundleTools(BundlePath);
        var options = new OkfContextProviderOptions { MemoryDirectory = memoryDirectory };

        Assert.Throws<ArgumentException>(() => new OkfContextProvider(tools, options));
    }

    [Fact]
    public void Constructor_accepts_a_valid_custom_memory_directory()
    {
        var tools = new OkfBundleTools(BundlePath);
        var options = new OkfContextProviderOptions { MemoryDirectory = "agent_memory" };

        var provider = new OkfContextProvider(tools, options);

        Assert.NotNull(provider);
    }
}
