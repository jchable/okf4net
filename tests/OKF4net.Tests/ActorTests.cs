// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

public class ActorTests
{
    [Fact]
    public void Human_prefix_is_human_and_well_formed()
    {
        var a = Actor.Parse("human:ahormati");
        Assert.Equal(ActorKind.Human, a.Kind);
        Assert.Equal("ahormati", a.Id);
        Assert.True(a.IsHuman);
        Assert.True(a.IsWellFormed);
    }

    [Fact]
    public void Process_prefix_is_process_not_human()
    {
        var a = Actor.Parse("process:finance-nightly");
        Assert.Equal(ActorKind.Process, a.Kind);
        Assert.Equal("finance-nightly", a.Id);
        Assert.False(a.IsHuman);
        Assert.True(a.IsWellFormed);
    }

    [Fact]
    public void Producer_slash_version_splits_and_is_well_formed()
    {
        var a = Actor.Parse("reference_agent/gemini-2.5-pro");
        Assert.Equal(ActorKind.Producer, a.Kind);
        Assert.Equal("reference_agent", a.Producer);
        Assert.Equal("gemini-2.5-pro", a.Version);
        Assert.True(a.IsWellFormed);
    }

    [Theory]
    [InlineData("bob")]          // no prefix, no slash
    [InlineData("human:")]       // empty id
    [InlineData("process:")]     // empty id
    [InlineData("agent/")]       // empty version
    [InlineData("/v1")]          // empty producer
    [InlineData("")]             // empty
    public void Malformed_actors_are_not_well_formed(string raw)
    {
        Assert.False(Actor.Parse(raw).IsWellFormed);
    }

    [Fact]
    public void Empty_human_prefix_still_classifies_as_human_for_trust()
    {
        var a = Actor.Parse("human:");
        Assert.True(a.IsHuman);       // prefix present → trust keys off it
        Assert.False(a.IsWellFormed); // but empty id is malformed
    }
}
