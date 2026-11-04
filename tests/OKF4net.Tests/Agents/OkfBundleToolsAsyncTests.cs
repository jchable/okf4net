// SPDX-License-Identifier: LGPL-3.0-or-later
#pragma warning disable CS0618 // the obsolete synchronous tool methods are the subject (#152): their refusal over a provider that does not complete synchronously
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Tests.Providers;

namespace OKF4net.Tests.Agents;

/// <summary>
/// #152 step 5: the tool set over a provider that completes later. The asynchronous tools are
/// pending, not blocked; cancelling stops the wait; the obsolete synchronous spellings refuse such a
/// provider before anything starts; and <c>GetTools</c> stays bound to the observation-checking
/// wrappers whatever the public asynchronous methods do.
/// </summary>
public class OkfBundleToolsAsyncTests
{
    private const string Doc = "---\ntype: Note\ntitle: A\ndescription: A concept.\n---\n\nbody\n";

    private static (GatedProvider Gated, OkfBundleTools Tools) ToolsOverGated(bool canWrite = false)
    {
        var store = new InMemoryBundleProvider(canWrite: canWrite).Add("notes/a.md", Doc);
        var gated = new GatedProvider(store, canWrite);
        return (gated, new OkfBundleTools(new BundleHandle(gated)));
    }

    [Fact]
    public async Task Tool_reads_are_pending_not_blocked_while_the_provider_has_not_answered()
    {
        var (gated, tools) = ToolsOverGated();

        var read = tools.ReadConceptAsync("notes/a");
        var browse = tools.BrowseAsync();
        var search = tools.SearchAsync("concept");
        var validate = tools.ValidateBundleAsync();
        Assert.False(read.IsCompleted);
        Assert.False(browse.IsCompleted);
        Assert.False(search.IsCompleted);
        Assert.False(validate.IsCompleted);

        gated.Gate.SetResult();
        Assert.Contains("# A", await read, StringComparison.Ordinal);
        Assert.DoesNotContain("Error", await browse, StringComparison.Ordinal);
        Assert.Contains("notes/a", await search, StringComparison.Ordinal);
        Assert.False((await validate).StartsWith("Error", StringComparison.Ordinal));
        Assert.Equal(1, gated.Enumerations); // one shared load for all four
    }

    [Fact]
    public async Task A_cancelled_tool_call_stops_waiting_and_the_load_goes_on_for_the_others()
    {
        var (gated, tools) = ToolsOverGated();
        using var cts = new CancellationTokenSource();

        var cancelling = tools.ReadConceptAsync("notes/a", cts.Token);
        var staying = tools.BrowseAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling);
        gated.Gate.SetResult();
        Assert.DoesNotContain("Error", await staying, StringComparison.Ordinal);
    }

    [Fact]
    public void The_obsolete_synchronous_tools_refuse_a_provider_that_does_not_complete_synchronously_before_any_io()
    {
        var (gated, tools) = ToolsOverGated();

        Assert.Throws<InvalidOperationException>(() => tools.ReadConcept("notes/a"));
        Assert.Throws<InvalidOperationException>(() => tools.Browse());
        Assert.Throws<InvalidOperationException>(() => tools.ValidateBundle());

        Assert.Equal(0, gated.Calls);
    }

    [Fact]
    public async Task The_exposed_write_tool_is_gated_by_observations_but_the_public_async_method_is_not()
    {
        var store = new InMemoryBundleProvider(canWrite: true).Add("notes/a.md", Doc);
        var tools = new OkfBundleTools(new BundleHandle(store));
        var write = tools.GetTools().Cast<Microsoft.Extensions.AI.AIFunction>().Single(f => f.Name == "okf_write_concept");
        var arguments = new Microsoft.Extensions.AI.AIFunctionArguments
        {
            ["conceptId"] = "notes/b",
            ["frontmatterYaml"] = "type: Note\ntitle: B\ndescription: Another.",
            ["body"] = "b\n",
        };

        var refused = (await write.InvokeAsync(arguments))?.ToString();
        Assert.Contains("has not been read by this session", refused, StringComparison.Ordinal);

        var host = await tools.WriteConceptAsync("notes/b", "type: Note\ntitle: B\ndescription: Another.", "b\n");
        Assert.StartsWith("Written notes/b", host, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_write_tool_over_a_provider_that_completes_later_waits_and_commits_once_released()
    {
        var (gated, tools) = ToolsOverGated(canWrite: true);

        var pending = tools.WriteConceptAsync("notes/c", "type: Note\ntitle: C\ndescription: Third.", "c\n");
        Assert.False(pending.IsCompleted);
        gated.Gate.SetResult();

        Assert.StartsWith("Written notes/c", await pending, StringComparison.Ordinal);
    }
}
