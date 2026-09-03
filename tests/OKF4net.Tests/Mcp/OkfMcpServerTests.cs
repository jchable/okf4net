// SPDX-License-Identifier: LGPL-3.0-or-later
using System.IO.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OKF4net.Mcp;

namespace OKF4net.Tests.Mcp;

public sealed class OkfMcpServerTests
{
    private static string NewBundleDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "okf-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Spins up an in-memory MCP server over the given tools and returns a connected client.</summary>
    private static async Task<(McpServer Server, McpClient Client)> ConnectAsync(IReadOnlyList<McpServerTool> tools)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var server = McpServer.Create(
            new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
            new McpServerOptions { ToolCollection = [.. tools] });
        _ = server.RunAsync();

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));

        return (server, client);
    }

    private static string ResultText(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task Write_then_read_round_trips_through_mcp()
    {
        var bundle = NewBundleDir();
        try
        {
            var tools = OkfMcpToolset.Build(bundle, readOnly: false);
            var (server, client) = await ConnectAsync(tools);
            await using var _ = server;
            await using var __ = client;

            var write = await client.CallToolAsync(
                "okf_write_concept",
                new Dictionary<string, object?>
                {
                    ["conceptId"] = "notes/test",
                    ["frontmatterYaml"] =
                        "type: note\ntitle: Test Note\ndescription: A test note.\ntimestamp: 2026-07-24T00:00:00Z",
                    ["body"] = "Hello from MCP.",
                });
            Assert.Contains("Written notes/test", ResultText(write));

            var read = await client.CallToolAsync(
                "okf_read_concept",
                new Dictionary<string, object?> { ["conceptId"] = "notes/test" });
            var text = ResultText(read);

            Assert.Contains("Test Note", text);
            Assert.Contains("Hello from MCP.", text);
            Assert.True(File.Exists(Path.Combine(bundle, "notes", "test.md")));
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Fact]
    public async Task Build_exposes_all_eleven_tools()
    {
        var bundle = NewBundleDir();
        try
        {
            var tools = OkfMcpToolset.Build(bundle, readOnly: false);
            var (server, client) = await ConnectAsync(tools);
            await using var _ = server;
            await using var __ = client;

            var listed = await client.ListToolsAsync();
            var names = listed.Select(t => t.Name).OrderBy(n => n).ToArray();

            Assert.Equal(
                new[]
                {
                    "okf_append_log", "okf_audit", "okf_browse", "okf_changes_since", "okf_get_computation",
                    "okf_graph", "okf_read_concept", "okf_regenerate_indexes", "okf_search",
                    "okf_validate_bundle", "okf_write_concept",
                },
                names);

            // okf_run_computation is never wired in MCP: OkfMcpToolset.Build
            // constructs OkfBundleTools without an attestation orchestrator, so
            // GetTools() never includes it (see OkfComputationToolsTests).
            Assert.DoesNotContain("okf_run_computation", names);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Fact]
    public async Task Build_readOnly_omits_the_three_write_tools()
    {
        var bundle = NewBundleDir();
        try
        {
            var tools = OkfMcpToolset.Build(bundle, readOnly: true);
            var (server, client) = await ConnectAsync(tools);
            await using var _ = server;
            await using var __ = client;

            var names = (await client.ListToolsAsync()).Select(t => t.Name).ToHashSet();

            Assert.Equal(8, names.Count);
            Assert.DoesNotContain("okf_write_concept", names);
            Assert.DoesNotContain("okf_append_log", names);
            Assert.DoesNotContain("okf_regenerate_indexes", names);
            Assert.Contains("okf_read_concept", names);
            // okf_get_computation is read-only and needs no attestation runtime,
            // so it surfaces in read-only mode too -- this is deliberate.
            Assert.Contains("okf_get_computation", names);
            // okf_audit is read-only too, so it surfaces in read-only mode.
            Assert.Contains("okf_audit", names);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Fact]
    public void ConfigureServices_registers_all_eleven_tools()
    {
        var bundle = NewBundleDir();
        try
        {
            var services = new ServiceCollection();
            OkfMcpHost.ConfigureServices(services, bundle, readOnly: false, version: "0.0.0");
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
            Assert.Equal(11, options.ToolCollection?.Count);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    [Fact]
    public void ConfigureServices_readOnly_registers_eight_tools()
    {
        var bundle = NewBundleDir();
        try
        {
            var services = new ServiceCollection();
            OkfMcpHost.ConfigureServices(services, bundle, readOnly: true, version: "0.0.0");
            using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptions<McpServerOptions>>().Value;
            Assert.Equal(8, options.ToolCollection?.Count);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }

    /// <summary>
    /// The other MCP tests only prove <c>okf_audit</c> appears in the tool
    /// list. This one calls it, because the MCP adapter does its own
    /// schema-driven argument conversion: <c>okf_audit</c> takes four optional
    /// parameters — a bool and three strings — so a conversion or binding
    /// regression could ship while the Agent-level test stayed green.
    ///
    /// The filter is what makes the assertion meaningful: <c>orphan</c> is
    /// unverified, <c>reviewed</c> is human-reviewed, and only the former may
    /// come back. `stale` is passed as false because neither concept carries a
    /// `stale_after`, so the tool's default (the stale worklist) would select
    /// nothing and the test would pass without proving the trust filter bound.
    /// </summary>
    [Fact]
    public async Task Audit_tool_invoked_over_mcp_applies_its_filters()
    {
        var bundle = NewBundleDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(bundle, "metrics"));
            await File.WriteAllTextAsync(
                Path.Combine(bundle, "metrics", "orphan.md"),
                "---\ntype: Metric\ntitle: Orphaned Metric\n---\n");
            await File.WriteAllTextAsync(
                Path.Combine(bundle, "metrics", "reviewed.md"),
                "---\ntype: Metric\ntitle: Reviewed Metric\n"
                + "verified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n---\n");

            var tools = OkfMcpToolset.Build(bundle, readOnly: true);
            var (server, client) = await ConnectAsync(tools);
            await using var _ = server;
            await using var __ = client;

            var audit = await client.CallToolAsync(
                "okf_audit",
                new Dictionary<string, object?>
                {
                    ["stale"] = false,
                    ["trust"] = "unverified",
                });

            var text = ResultText(audit);
            Assert.Contains("metrics/orphan", text);
            Assert.DoesNotContain("metrics/reviewed", text);

            // The counters still describe the whole bundle, not the selection.
            Assert.Contains("concepts:   2", text);
        }
        finally
        {
            Directory.Delete(bundle, recursive: true);
        }
    }
}
